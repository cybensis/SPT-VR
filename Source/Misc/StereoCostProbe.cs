using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;

namespace TarkovVR.Source.Misc
{
    // Measurement only: what does the SECOND eye cost, on which thread, and in which part of the frame?
    // Ctrl+F10 in a raid runs an interleaved A/B on the headset camera - both eyes vs left eye only
    // (Camera.stereoTargetEye; the right eye freezes while left-only runs) - then logs one "[StereoCost]" summary:
    //   main thread  : the VR camera's onPreCull/onPreRender/onPostRender (they fire once per eye pass)
    //   render thread: CPU clock at plugin events queued by command buffers at camera events + the sun's light
    //                  events (shadow map, shadow mask), plus frame markers: start (our early Update - so the
    //                  LateUpdate GPU work, e.g. CloudSix's once-a-frame sky bake, is inside the frame), render
    //                  start (Application.onBeforeRender) and end (WaitForEndOfFrame)
    //   GPU          : D3D11 timestamp queries at the SAME events (native helper native/SPTVRGpuProbe.dll), and
    //                  SteamVR's own frame timing
    // BSG's build has Unity's frame-timing stats switched off, so these are our own clocks. Stand still, don't aim
    // (the scope is a separate camera), keep the same view for the whole run.
    [DefaultExecutionOrder(-32000)]   // our Update first = the main-thread frame clock starts early
    internal partial class StereoCostProbe : MonoBehaviour
    {
        public static bool hotkeyEnabled = true;      // hotkey kill switch
        public static int cycles = 4;                 // both / left pairs
        public static float segmentSeconds = 6f;      // per mode per cycle
        public static float settleSeconds = 1.5f;     // discarded after every switch

        private static StereoCostProbe instance;
        private void Awake() { instance = this; }

        private enum Mode { Both = 0, LeftOnly = 1 }
        private static bool running;
        private static Mode mode;
        private static bool sampling;                 // false during the settle window

        //------------------------------------------------------------------------------------------------------------
        // Event ids (shared with the native helper: 1 = frame start, 99 = frame end, 250 = release)
        private const int EvFrameStart = 1, EvSettle = 2, EvSampleBoth = 3, EvSampleLeft = 4, EvRenderStart = 5, EvFrameEnd = 99, EvDiscard = 251, EvHarvest = 252;
        private const int EvBeforeGBuffer = 10, EvAfterGBuffer = 11, EvBeforeLighting = 12, EvAfterLighting = 13,
                          EvAfterEverything = 14, EvBeforeAlpha = 15, EvAfterAlpha = 16, EvBeforeFx = 17, EvAfterFx = 18,
                          EvAfterOpaque = 19;
        private const int EvBeforeShadowMap = 30, EvAfterShadowMap = 31, EvBeforeMask = 32, EvAfterMask = 33;
        private static readonly (CameraEvent evt, int id)[] cameraEvents =
        {
            (CameraEvent.BeforeGBuffer, EvBeforeGBuffer), (CameraEvent.AfterGBuffer, EvAfterGBuffer),
            (CameraEvent.BeforeLighting, EvBeforeLighting), (CameraEvent.AfterLighting, EvAfterLighting),
            (CameraEvent.AfterForwardOpaque, EvAfterOpaque),   // added last = after CloudSix's sky draws there
            (CameraEvent.BeforeForwardAlpha, EvBeforeAlpha), (CameraEvent.AfterForwardAlpha, EvAfterAlpha),
            (CameraEvent.BeforeImageEffects, EvBeforeFx), (CameraEvent.AfterImageEffects, EvAfterFx),
            (CameraEvent.AfterEverything, EvAfterEverything),
        };
        private static readonly (LightEvent evt, int id)[] lightEvents =
        {
            (LightEvent.BeforeShadowMap, EvBeforeShadowMap), (LightEvent.AfterShadowMap, EvAfterShadowMap),
            (LightEvent.BeforeScreenspaceMask, EvBeforeMask), (LightEvent.AfterScreenspaceMask, EvAfterMask),
        };
        private static readonly List<(CameraEvent evt, CommandBuffer cb)> camAttached = new List<(CameraEvent, CommandBuffer)>();
        private static readonly List<(Light light, LightEvent evt, CommandBuffer cb)> lightAttached = new List<(Light, LightEvent, CommandBuffer)>();

        //------------------------------------------------------------------------------------------------------------
        // Native helper (GPU + CPU clocks per event). Loaded from <plugin dir>/native; without it the probe falls back
        // to a managed render-thread clock (CPU only).
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string path);
        [DllImport("SPTVRGpuProbe")] private static extern int GpuProbe_Init(IntPtr anyD3D11Resource);
        [DllImport("SPTVRGpuProbe")] private static extern IntPtr GpuProbe_GetRenderEventFunc();
        [DllImport("SPTVRGpuProbe")] private static extern int GpuProbe_PopFrame(int[] ids, double[] cpuMs, float[] gpuMs, int max);
        [DllImport("SPTVRGpuProbe")] private static extern int GpuProbe_Stats(int[] stats, int max);
        [DllImport("SPTVRGpuProbe")] private static extern double GpuProbe_Frequency();
        private static bool nativeTried, nativeOk;
        private static RenderTexture deviceProbeRT;

        private static bool TryNative()
        {
            if (nativeTried) return nativeOk;
            nativeTried = true;
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "native", "SPTVRGpuProbe.dll");
                if (!File.Exists(path) || LoadLibrary(path) == IntPtr.Zero)
                {
                    Plugin.MyLog.LogWarning("[StereoCost] native/SPTVRGpuProbe.dll not found - CPU clocks only, no GPU breakdown");
                    return false;
                }
                deviceProbeRT = new RenderTexture(4, 4, 0) { name = "SPT-VR GpuProbe device" };
                deviceProbeRT.Create();
                nativeOk = GpuProbe_Init(deviceProbeRT.GetNativeTexturePtr()) == 1;
                if (!nativeOk) Plugin.MyLog.LogWarning("[StereoCost] GPU timing helper failed to start - CPU clocks only");
            }
            catch (Exception e) { Plugin.MyLog.LogWarning("[StereoCost] GPU timing helper unavailable: " + e.Message); nativeOk = false; }
            return nativeOk;
        }

        // Managed fallback clock: Unity calls this on its render thread at each queued event; single-producer ring.
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void RenderEventFn(int eventId);
        private static readonly RenderEventFn managedEvent = OnManagedRenderEvent;
        private const int RingSize = 16384;
        private static readonly long[] ringTime = new long[RingSize];
        private static readonly int[] ringId = new int[RingSize];
        private static volatile int ringWrite;
        private static int ringRead;
        private static void OnManagedRenderEvent(int id)
        {
            int w = ringWrite;
            ringTime[w & (RingSize - 1)] = Stopwatch.GetTimestamp();
            ringId[w & (RingSize - 1)] = id;
            ringWrite = w + 1;
        }
        private static readonly double tickMs = 1000.0 / Stopwatch.Frequency;

        private static IntPtr eventPtr;

        //------------------------------------------------------------------------------------------------------------
        // Report rows: spans between two events of one eye pass (or of the frame), on the render thread and the GPU.
        private enum Src { Cpu = 0, Gpu = 1 }
        private static readonly string[] eyeRowNames =
        {
            "whole eye", "geometry (G-buffer)", "G-buffer -> lighting", "lighting (incl. shadows)",
            "  sun shadow map(s) (+ hooks)", "  sun shadow mask (+ hooks)", "lighting -> fwd opaque done (sky)",
            "fwd opaque done -> transparents", "transparents", "transparents -> image effects",
            "image effects (post, FSR)", "image effects -> eye end",
        };
        private const int RowsPerEye = 12, FirstEyeRow = 5, RowCount = FirstEyeRow + 2 * RowsPerEye;
        private static readonly string[] rowNames = BuildRowNames();
        private static string[] BuildRowNames()
        {
            var l = new List<string> { "frame (Update -> end mark)", "Update -> render start (LateUpdate work)",
                                       "render start -> first eye", "between the two eyes", "after the eyes -> end mark" };
            foreach (string e in new[] { "L ", "R " }) foreach (string r in eyeRowNames) l.Add(e + r);
            return l.ToArray();
        }

        private class Samples
        {
            public readonly List<float>[,] rows = new List<float>[2, RowCount];   // [src, row]
            public readonly List<float> frameInterval = new List<float>(), mainUpdateToEnd = new List<float>(), mainCull = new List<float>(), mainRenderAll = new List<float>();
            public readonly List<float>[] mainEye = { new List<float>(), new List<float>() };
            public readonly List<float> vrGpu = new List<float>(), vrInterval = new List<float>();
            public readonly ItemSums itemSums = new ItemSums();
            public int frames, rtFrames, gpuFrames, rtEyePasses, shadowMaps, shadowMasks, otherShadowMaps, preCullCalls, preRenderCalls;
            public Samples() { for (int s = 0; s < 2; s++) for (int r = 0; r < RowCount; r++) rows[s, r] = new List<float>(); }
        }
        private static Samples[] samples;

        // main-thread per-frame scratch
        private static long tUpdateStart, tFirstPreCull, tFirstPreRender, tLastPostRender;
        private static int preCullThisFrame, preRenderThisFrame, eyePassIndex;
        private static readonly long[] tPreRender = new long[2], tPostRender = new long[2];
        private static uint lastVrFrame;

        // one frame's events for the parser
        private static readonly int[] fIds = new int[1024];
        private static readonly double[] fCpu = new double[1024];
        private static readonly float[] fGpu = new float[1024];
        private static Camera probeCam;
        private static int fCount;
        private static bool managedOpen;
        private static long managedStart;

        //------------------------------------------------------------------------------------------------------------
        private void Update()
        {
            if (running)
            {
                tUpdateStart = Stopwatch.GetTimestamp();
                GL.IssuePluginEvent(eventPtr, EvFrameStart);
                GL.IssuePluginEvent(eventPtr, !sampling ? EvSettle : mode == Mode.Both ? EvSampleBoth : EvSampleLeft);
                if (breakdown && probeCam != null) Arrange(probeCam);   // buffers other code added since last frame
                DrainFrames();
                PollSteamVR();
                return;
            }
            if (!hotkeyEnabled || !Input.GetKeyDown(KeyCode.F10) || !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                return;
            RunNow();
        }

        // Also callable from UnityExplorer.
        public static void RunNow()
        {
            if (running) { Plugin.MyLog.LogWarning("[StereoCost] already running"); return; }
            Camera cam = VRGlobals.VRCam;
            if (cam == null || !VRGlobals.inGame) { Plugin.MyLog.LogWarning("[StereoCost] start it in a raid (no headset camera yet)"); return; }
            if (instance == null) return;
            instance.StartCoroutine(Run(cam));
        }

        private static IEnumerator Run(Camera cam)
        {
            running = true;
            probeCam = cam;
            samples = new[] { new Samples(), new Samples() };
            StereoTargetEyeMask original = cam.stereoTargetEye;
            bool gpu = TryNative();
            eventPtr = gpu ? GpuProbe_GetRenderEventFunc() : Marshal.GetFunctionPointerForDelegate(managedEvent);
            Attach(cam);
            Plugin.MyLog.LogWarning($"[StereoCost] start: {cycles} x (both eyes {segmentSeconds}s, left eye only {segmentSeconds}s), " +
                                    $"{settleSeconds}s settle after each switch, GPU timing {(gpu ? "ON" : "OFF")}, sun lights watched: {lightAttached.Count / lightEvents.Length}. " +
                                    "Stand still, don't aim. Right eye freezes during left-only.");
            var endOfFrame = new WaitForEndOfFrame();
            try
            {
                for (int c = 0; c < cycles * 2; c++)
                {
                    mode = (c % 2 == 0) ? Mode.Both : Mode.LeftOnly;
                    if (VRGlobals.VRCam != cam || cam == null) break;
                    cam.stereoTargetEye = mode == Mode.Both ? StereoTargetEyeMask.Both : StereoTargetEyeMask.Left;
                    sampling = false;
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < settleSeconds + segmentSeconds && VRGlobals.VRCam == cam)
                    {
                        yield return endOfFrame;
                        long tEnd = Stopwatch.GetTimestamp();
                        GL.IssuePluginEvent(eventPtr, EvFrameEnd);
                        bool wasSampling = sampling;
                        sampling = Time.realtimeSinceStartup - t0 >= settleSeconds;
                        if (wasSampling) EndMainFrame(tEnd);
                        ResetMainFrame();
                    }
                }
            }
            finally
            {
                if (cam != null) cam.stereoTargetEye = original;   // always hand both eyes back
                Detach(cam);
                sampling = false;
                running = false;
            }
            // let the GPU results of the last frames arrive
            for (int i = 0; i < 12; i++) { if (gpu) GL.IssuePluginEvent(eventPtr, EvHarvest); yield return null; DrainFrames(); }
            Report(gpu);
        }

        private static void Attach(Camera cam)
        {
            ringRead = ringWrite; managedOpen = false; fCount = 0;
            Cleanup(cam);   // before our markers go on: clears anything an earlier broken run left on the camera
            if (nativeOk) GL.IssuePluginEvent(eventPtr, EvDiscard);   // the last run's unread frames
            while (nativeOk && GpuProbe_PopFrame(fIds, fCpu, fGpu, fIds.Length) >= 0) { }
            foreach (var (evt, id) in cameraEvents)
            {
                var cb = new CommandBuffer { name = "SPT-VR StereoCostProbe " + evt };
                cb.IssuePluginEvent(eventPtr, id);
                cam.AddCommandBuffer(evt, cb);
                camAttached.Add((evt, cb));
            }
            foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || l.type != LightType.Directional || l.shadows == LightShadows.None) continue;
                foreach (var (evt, id) in lightEvents)
                {
                    var cb = new CommandBuffer { name = "SPT-VR StereoCostProbe " + evt };
                    cb.IssuePluginEvent(eventPtr, id);
                    l.AddCommandBuffer(evt, cb);
                    lightAttached.Add((l, evt, cb));
                }
            }
            ParserReset();
            BreakdownAttach(cam);
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
            Application.onBeforeRender += OnBeforeRender;
            ResetMainFrame();
        }

        private static void Detach(Camera cam)
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPreRender -= OnPreRender;
            Camera.onPostRender -= OnPostRender;
            Application.onBeforeRender -= OnBeforeRender;
            BreakdownDetach(cam);
            foreach (var (evt, cb) in camAttached) { if (cam != null) cam.RemoveCommandBuffer(evt, cb); cb.Release(); }
            foreach (var (l, evt, cb) in lightAttached) { if (l != null) l.RemoveCommandBuffer(evt, cb); cb.Release(); }
            camAttached.Clear(); lightAttached.Clear();
        }

        // After LateUpdate, before the frame's camera work.
        private static void OnBeforeRender() { GL.IssuePluginEvent(eventPtr, EvRenderStart); }

        //------------------------------------------------------------------------------------------------------------
        private static void OnPreCull(Camera cam)
        {
            if (cam != VRGlobals.VRCam) { OtherCameraMark(cam, false); return; }
            if (preCullThisFrame++ == 0) tFirstPreCull = Stopwatch.GetTimestamp();
        }
        private static void OnPreRender(Camera cam)
        {
            if (cam != VRGlobals.VRCam) return;
            if (breakdown) Arrange(cam);   // buffers added for this eye (e.g. in another OnPreRender) get bracketed too
            long t = Stopwatch.GetTimestamp();
            if (preRenderThisFrame++ == 0) tFirstPreRender = t;
            if (eyePassIndex < 2) tPreRender[eyePassIndex] = t;
        }
        private static void OnPostRender(Camera cam)
        {
            if (cam != VRGlobals.VRCam) { OtherCameraMark(cam, true); return; }
            long t = Stopwatch.GetTimestamp();
            if (eyePassIndex < 2) tPostRender[eyePassIndex] = t;
            eyePassIndex++;
            tLastPostRender = t;
        }
        private static void ResetMainFrame()
        {
            preCullThisFrame = preRenderThisFrame = 0;
            eyePassIndex = 0;
            tFirstPreCull = tFirstPreRender = tLastPostRender = 0;
        }
        private static void EndMainFrame(long tEnd)
        {
            var s = samples[(int)mode];
            s.frames++;
            s.frameInterval.Add(Time.unscaledDeltaTime * 1000f);
            if (tUpdateStart != 0) s.mainUpdateToEnd.Add((float)((tEnd - tUpdateStart) * tickMs));
            s.preCullCalls += preCullThisFrame;
            s.preRenderCalls += preRenderThisFrame;
            if (eyePassIndex == 0) return;
            if (tFirstPreCull != 0 && tFirstPreRender != 0) s.mainCull.Add((float)((tFirstPreRender - tFirstPreCull) * tickMs));
            if (tFirstPreCull != 0 && tLastPostRender != 0) s.mainRenderAll.Add((float)((tLastPostRender - tFirstPreCull) * tickMs));
            for (int e = 0; e < Math.Min(eyePassIndex, 2); e++) s.mainEye[e].Add((float)((tPostRender[e] - tPreRender[e]) * tickMs));
        }

        //------------------------------------------------------------------------------------------------------------
        private static void DrainFrames()
        {
            if (nativeOk)
            {
                int n;
                // a frame whose GPU half failed arrives with NaN GPU times - its CPU half still counts
                while ((n = GpuProbe_PopFrame(fIds, fCpu, fGpu, fIds.Length)) >= 0) { fCount = n; ParseFrame(n > 0 && !float.IsNaN(fGpu[n - 1])); }
                return;
            }
            int w = ringWrite;
            if (w - ringRead > RingSize) ringRead = w - RingSize;
            for (; ringRead < w; ringRead++)
            {
                int id = ringId[ringRead & (RingSize - 1)];
                long t = ringTime[ringRead & (RingSize - 1)];
                if (id == EvFrameStart) { managedOpen = true; managedStart = t; fCount = 0; }
                if (!managedOpen || fCount >= fIds.Length) continue;
                fIds[fCount] = id; fCpu[fCount] = (t - managedStart) * tickMs; fGpu[fCount] = float.NaN; fCount++;
                if (id == EvFrameEnd) { managedOpen = false; ParseFrame(false); }
            }
        }

        // Turns one frame's (id, cpu, gpu) list into per-row spans for the mode tagged in the stream.
        private static readonly double[,] tCpu = new double[2, 34];
        private static readonly double[,] tGpu = new double[2, 34];
        private static readonly double[] shadowCpu = new double[2], shadowGpu = new double[2], maskCpu = new double[2], maskGpu = new double[2];
        private static readonly int[] shadowN = new int[2], maskN = new int[2];
        private static void ParseFrame(bool hasGpu)
        {
            if (fCount < 3 || fIds[0] != EvFrameStart) return;
            int tag = fIds[1];
            if (tag != EvSampleBoth && tag != EvSampleLeft) return;
            if (fIds[fCount - 1] != EvFrameEnd) return;
            var s = samples[tag == EvSampleBoth ? 0 : 1];
            for (int e = 0; e < 2; e++)
            {
                for (int k = 0; k < 34; k++) { tCpu[e, k] = double.NaN; tGpu[e, k] = double.NaN; }
                shadowCpu[e] = shadowGpu[e] = maskCpu[e] = maskGpu[e] = 0; shadowN[e] = maskN[e] = 0;
            }
            int eye = -1;
            bool inLighting = false, afterEye = false;
            int eyeBlock = 0, frameBlock = 0;
            ItemFrameReset();
            double renderStartC = double.NaN, renderStartG = double.NaN;
            double openShadowC = double.NaN, openShadowG = double.NaN, openMaskC = double.NaN, openMaskG = double.NaN;
            for (int i = 2; i < fCount - 1; i++)
            {
                int id = fIds[i]; double c = fCpu[i]; double g = hasGpu ? fGpu[i] : double.NaN;
                if (id >= ItemBase)
                {
                    int bucket = (eye < 0 || eye > 1 || afterEye) ? 0 : eye + 1;
                    ItemMark(id, c, g, bucket, bucket == 0 ? frameBlock : eyeBlock);
                    continue;
                }
                if (id == EvRenderStart) { if (double.IsNaN(renderStartC)) { renderStartC = c; renderStartG = g; frameBlock = 1; } continue; }
                if (id == EvEyeStart)
                {
                    eye++; afterEye = false; eyeBlock = 0;
                    if (eye >= 0 && eye < 2) { tCpu[eye, EvEyeStart] = c; tGpu[eye, EvEyeStart] = g; }
                    continue;
                }
                int nb = BlockAfter(id);
                if (nb >= 0) eyeBlock = nb;
                if (id == EvAfterEverything) { afterEye = true; frameBlock = eye == 0 ? 2 : 3; }
                if (id == EvBeforeLighting) inLighting = true;
                if (id == EvAfterLighting) inLighting = false;
                // sun light events of OTHER cameras (anything rendering outside the VR eyes' lighting) are counted apart
                if (id >= EvBeforeShadowMap && id <= EvAfterMask && (!inLighting || eye < 0 || eye > 1))
                {
                    if (id == EvBeforeShadowMap) s.otherShadowMaps++;
                    continue;
                }
                if (eye < 0 || eye > 1) continue;
                if (id == EvBeforeShadowMap) { openShadowC = c; openShadowG = g; continue; }
                if (id == EvAfterShadowMap && !double.IsNaN(openShadowC)) { shadowCpu[eye] += c - openShadowC; shadowGpu[eye] += g - openShadowG; shadowN[eye]++; openShadowC = double.NaN; continue; }
                if (id == EvBeforeMask) { openMaskC = c; openMaskG = g; continue; }
                if (id == EvAfterMask && !double.IsNaN(openMaskC)) { maskCpu[eye] += c - openMaskC; maskGpu[eye] += g - openMaskG; maskN[eye]++; openMaskC = double.NaN; continue; }
                if (id < 34 && double.IsNaN(tCpu[eye, id])) { tCpu[eye, id] = c; tGpu[eye, id] = g; }
            }
            int eyes = Math.Min(eye + 1, 2);
            if (eyes == 0) return;
            s.rtFrames++; s.rtEyePasses += eyes;
            if (hasGpu) s.gpuFrames++;
            for (int e = 0; e < eyes; e++) { s.shadowMaps += shadowN[e]; s.shadowMasks += maskN[e]; }
            double end = fCpu[fCount - 1], endG = hasGpu ? fGpu[fCount - 1] : double.NaN;
            ItemFrameCommit(s, hasGpu, eyes, renderStartC, renderStartG, end, endG);
            for (int src = 0; src < (hasGpu ? 2 : 1); src++)
            {
                var t = src == 0 ? tCpu : tGpu;
                double fend = src == 0 ? end : endG;
                double rstart = src == 0 ? renderStartC : renderStartG;
                Add(s, src, 0, fend);
                Add(s, src, 1, rstart);
                Add(s, src, 2, t[0, EvBeforeGBuffer] - rstart);
                if (eyes == 2) Add(s, src, 3, t[1, EvBeforeGBuffer] - t[0, EvAfterEverything]);
                Add(s, src, 4, fend - t[eyes - 1, EvAfterEverything]);
                for (int e = 0; e < eyes; e++)
                {
                    int r = FirstEyeRow + e * RowsPerEye;
                    Add(s, src, r + 0, t[e, EvAfterEverything] - t[e, EvBeforeGBuffer]);
                    Add(s, src, r + 1, t[e, EvAfterGBuffer] - t[e, EvBeforeGBuffer]);
                    Add(s, src, r + 2, t[e, EvBeforeLighting] - t[e, EvAfterGBuffer]);
                    Add(s, src, r + 3, t[e, EvAfterLighting] - t[e, EvBeforeLighting]);
                    if (shadowN[e] > 0) Add(s, src, r + 4, src == 0 ? shadowCpu[e] : shadowGpu[e]);
                    if (maskN[e] > 0) Add(s, src, r + 5, src == 0 ? maskCpu[e] : maskGpu[e]);
                    Add(s, src, r + 6, t[e, EvAfterOpaque] - t[e, EvAfterLighting]);
                    Add(s, src, r + 7, t[e, EvBeforeAlpha] - t[e, EvAfterOpaque]);
                    Add(s, src, r + 8, t[e, EvAfterAlpha] - t[e, EvBeforeAlpha]);
                    Add(s, src, r + 9, t[e, EvBeforeFx] - t[e, EvAfterAlpha]);
                    Add(s, src, r + 10, t[e, EvAfterFx] - t[e, EvBeforeFx]);
                    Add(s, src, r + 11, t[e, EvAfterEverything] - t[e, EvAfterFx]);
                }
            }
        }
        // The eye block that starts at a block marker (matches eyeBlockNames).
        private static int BlockAfter(int id)
        {
            switch (id)
            {
                case EvBeforeGBuffer: return 1;
                case EvAfterGBuffer: return 2;
                case EvBeforeLighting: return 3;
                case EvAfterLighting: return 4;
                case EvAfterOpaque: return 5;
                case EvBeforeAlpha: return 6;
                case EvAfterAlpha: return 7;
                case EvBeforeFx: return 8;
                case EvAfterFx: return 9;
                default: return -1;
            }
        }
        private static void ParserReset()
        {
            for (int k = 0; k < openC.Length; k++) openC[k] = double.NaN;
            fOpen.Clear();
            fTouched.Clear();
            Array.Clear(fItem, 0, fItem.Length);
            Array.Clear(fTouchedFlag, 0, fTouchedFlag.Length);
        }

        private static void Add(Samples s, int src, int row, double v)
        {
            if (!double.IsNaN(v) && !double.IsInfinity(v)) s.rows[src, row].Add((float)v);
        }

        private static void PollSteamVR()
        {
            if (!sampling) return;
            CVRCompositor comp = OpenVR.Compositor;
            if (comp == null) return;
            var ft = new Compositor_FrameTiming { m_nSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming)) };
            if (!comp.GetFrameTiming(ref ft, 1) || ft.m_nFrameIndex == lastVrFrame) return;
            lastVrFrame = ft.m_nFrameIndex;
            var s = samples[(int)mode];
            s.vrGpu.Add(ft.m_flPreSubmitGpuMs);
            s.vrInterval.Add(ft.m_flClientFrameIntervalMs);
        }

        //------------------------------------------------------------------------------------------------------------
        private static float Median(List<float> l)
        {
            if (l.Count == 0) return float.NaN;
            var a = l.ToArray(); Array.Sort(a);
            return a.Length % 2 == 1 ? a[a.Length / 2] : 0.5f * (a[a.Length / 2 - 1] + a[a.Length / 2]);
        }
        private static void Row(StringBuilder sb, string label, List<float> both, List<float> left)
        {
            float b = Median(both), l = Median(left);
            string d = float.IsNaN(b) || float.IsNaN(l) ? "      -" : (b - l).ToString("+0.00;-0.00").PadLeft(7);
            sb.AppendLine($"[StereoCost]   {label,-46} {Fmt(b)} {Fmt(l)} {d}   (n {both.Count}/{left.Count})");
        }
        private static string Fmt(float v) => float.IsNaN(v) ? "     -" : v.ToString("0.00").PadLeft(6);
        private static string Per(int n, int frames) => frames == 0 ? "-" : (n / (float)frames).ToString("0.00");

        // Share of each eye's image the lenses never show (SteamVR's hidden-area mesh, in 0..1 eye-image coordinates;
        // the same share of the upscaler's smaller render rect) = pixels a mask could skip in every per-pixel pass.
        private static string HiddenAreaLine()
        {
            try
            {
                CVRSystem sys = OpenVR.System;
                if (sys == null) return "[StereoCost]   headset hidden area: SteamVR not available";
                var sb = new StringBuilder("[StereoCost]   headset hidden area (never seen through the lenses):");
                foreach (EVREye eye in new[] { EVREye.Eye_Left, EVREye.Eye_Right })
                {
                    HiddenAreaMesh_t m = sys.GetHiddenAreaMesh(eye, EHiddenAreaMeshType.k_eHiddenAreaMesh_Standard);
                    if (m.pVertexData == IntPtr.Zero || m.unTriangleCount == 0) { sb.Append($" {eye} none;"); continue; }
                    var v = new float[m.unTriangleCount * 6];
                    Marshal.Copy(m.pVertexData, v, 0, v.Length);
                    double area = 0;
                    for (int t = 0; t < m.unTriangleCount; t++)
                    {
                        int o = t * 6;
                        area += Math.Abs((v[o + 2] - v[o]) * (v[o + 5] - v[o + 1]) - (v[o + 4] - v[o]) * (v[o + 3] - v[o + 1])) * 0.5;
                    }
                    sb.Append($" {eye} {area * 100:0.0}% ({m.unTriangleCount} triangles);");
                }
                return sb.ToString();
            }
            catch (Exception e) { return "[StereoCost]   headset hidden area: unavailable (" + e.Message + ")"; }
        }

        private static void Report(bool gpu)
        {
            var b = samples[0]; var l = samples[1];
            var sb = new StringBuilder();
            sb.AppendLine("[StereoCost] ===== result (medians, ms) =====     both   left   both-left");
            Row(sb, "frame interval (Unity)", b.frameInterval, l.frameInterval);
            Row(sb, "frame interval (SteamVR client)", b.vrInterval, l.vrInterval);
            Row(sb, "GPU before submit (SteamVR)", b.vrGpu, l.vrGpu);
            Row(sb, "MAIN early Update -> end of frame", b.mainUpdateToEnd, l.mainUpdateToEnd);
            Row(sb, "MAIN VR camera: first cull -> last eye done", b.mainRenderAll, l.mainRenderAll);
            Row(sb, "MAIN culling (onPreCull -> 1st onPreRender)", b.mainCull, l.mainCull);
            Row(sb, "MAIN left eye pass", b.mainEye[0], l.mainEye[0]);
            Row(sb, "MAIN right eye pass", b.mainEye[1], l.mainEye[1]);
            for (int src = 0; src < 2; src++)
            {
                sb.AppendLine(src == 0 ? "[StereoCost]   --- RENDER THREAD (CPU) ---" : "[StereoCost]   --- GPU (timestamps) ---");
                for (int r = 0; r < rowNames.Length; r++)
                    if (b.rows[src, r].Count + l.rows[src, r].Count > 0) Row(sb, rowNames[r], b.rows[src, r], l.rows[src, r]);
            }
            sb.AppendLine($"[StereoCost]   frames both/left: {b.frames}/{l.frames}; render-thread frames {b.rtFrames}/{l.rtFrames}; with GPU times {b.gpuFrames}/{l.gpuFrames}");
            if (gpu)
            {
                var st = new int[6];
                GpuProbe_Stats(st, st.Length);
                sb.AppendLine($"[StereoCost]   GPU timer: ok {st[3]}, CPU only {st[4]} (not back after 16 frames {st[0]}, read failed {st[2]}, " +
                              $"bad readings {st[5]}); driver 'disjoint' flag on {st[1]} frames (kept when readings sane); clock {GpuProbe_Frequency() / 1e6:0.###} MHz");
            }
            sb.AppendLine($"[StereoCost]   eye passes per frame both/left: {Per(b.rtEyePasses, b.rtFrames)}/{Per(l.rtEyePasses, l.rtFrames)}; " +
                          $"sun shadow maps per frame: {Per(b.shadowMaps, b.rtFrames)}/{Per(l.shadowMaps, l.rtFrames)}; " +
                          $"screen-space shadow masks per frame: {Per(b.shadowMasks, b.rtFrames)}/{Per(l.shadowMasks, l.rtFrames)}; " +
                          $"sun shadow maps of other cameras per frame: {Per(b.otherShadowMaps, b.rtFrames)}/{Per(l.otherShadowMaps, l.rtFrames)}");
            sb.AppendLine($"[StereoCost]   VR camera onPreCull per frame both/left: {Per(b.preCullCalls, b.frames)}/{Per(l.preCullCalls, l.frames)}");
            sb.AppendLine(HiddenAreaLine());
            ReportBreakdown(sb, b);
            sb.Append("[StereoCost] ===== end =====");
            Plugin.MyLog.LogWarning(sb.ToString());
        }
    }
}
