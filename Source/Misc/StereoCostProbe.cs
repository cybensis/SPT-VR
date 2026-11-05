using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TarkovVR.Source.XR;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;

namespace TarkovVR.Source.Misc
{
    // Measurement only: what does the SECOND eye cost, on which thread, and in which part of the frame?
    // Ctrl+F10 (raid or hideout) runs an interleaved A/B on the headset camera - both eyes vs left eye only
    // (Camera.stereoTargetEye; the right eye freezes while left-only runs) - then logs one "[StereoCost]" summary:
    //   main thread  : the VR camera's onPreCull/onPreRender/onPostRender (they fire once per eye pass), the frame, and the time
    //                  between frames; wall clock AND busy (the thread's own CPU cycles, QueryThreadCycleTime: waits don't count)
    //   render thread: CPU clock at plugin events queued by command buffers at camera events + the sun's light
    //                  events (shadow map, shadow mask), plus frame markers: start (our early Update - so the
    //                  LateUpdate GPU work, e.g. CloudSix's once-a-frame sky bake, is inside the frame), render
    //                  start (Application.onBeforeRender), the VR camera's pre-cull / pre-render per eye, and end
    //                  (WaitForEndOfFrame); wall AND busy, so time it waits for the main thread's commands shows apart
    //   GPU          : D3D11 timestamp queries at the SAME events (native helper native/SPTVRGpuProbe.dll), and
    //                  SteamVR's own frame timing (OpenVR only)
    //   OpenXR       : how long xrWaitFrame held its thread a frame, and which thread (native frame watcher)
    //   main per eye : every per-camera script callback on the main thread, per eye (StereoCostProbe.MainThread.cs)
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
        private void Awake() { instance = this; mainThreadId = GetCurrentThreadId(); }

        private enum Mode { Both = 0, LeftOnly = 1 }
        private static bool running;
        private static Mode mode;
        private static bool sampling;                 // false during the settle window

        //------------------------------------------------------------------------------------------------------------
        // Event ids (shared with the native helper: 1 = frame start, 99 = frame end, 250 = release)
        private const int EvFrameStart = 1, EvSettle = 2, EvSampleBoth = 3, EvSampleLeft = 4, EvRenderStart = 5, EvVrPreCull = 6, EvVrPreRender = 7,
                          EvFrameEnd = 99, EvDiscard = 251, EvHarvest = 252;
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
        // Native helper (GPU + CPU clocks + render-thread cycles per event). Loaded from <plugin dir>/native; without it the probe
        // falls back to a managed render-thread clock (CPU wall time only).
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32")] private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
        [DllImport("kernel32")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32")] private static extern int GetCurrentThreadId();
        [DllImport("SPTVRGpuProbe")] private static extern int GpuProbe_Init(IntPtr anyD3D11Resource);
        [DllImport("SPTVRGpuProbe")] private static extern IntPtr GpuProbe_GetRenderEventFunc();
        [DllImport("SPTVRGpuProbe")] private static extern int GpuProbe_PopFrame(int[] ids, double[] cpuMs, float[] gpuMs, float[] busyMs, int max);
        [DllImport("SPTVRGpuProbe")] private static extern int GpuProbe_Stats(int[] stats, int max);
        [DllImport("SPTVRGpuProbe")] private static extern double GpuProbe_Frequency();
        [DllImport("SPTVRGpuProbe")] private static extern double GpuProbe_TscPerMs();
        [DllImport("SPTVRGpuProbe")] private static extern int GpuProbe_RenderThreadId();
        private static bool nativeTried, nativeOk;
        private static RenderTexture deviceProbeRT;
        private static int mainThreadId;

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

        // The main thread's own CPU cycles so far (0 without the native helper's TSC rate)
        private static ulong MainCycles()
        {
            return QueryThreadCycleTime(GetCurrentThread(), out ulong cycles) ? cycles : 0;
        }
        private static double tscPerMs;
        private static double CyclesMs(ulong from, ulong to) => tscPerMs > 0 && to >= from && from != 0 ? (to - from) / tscPerMs : double.NaN;

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
        // Report rows: spans between two events of one eye pass (or of the frame), on the render thread (wall, busy) and the GPU.
        private const int SrcWall = 0, SrcGpu = 1, SrcBusy = 2, Sources = 3;
        private static readonly string[] eyeRowNames =
        {
            "whole eye", "geometry (G-buffer)", "G-buffer -> lighting", "lighting (incl. shadows)",
            "  sun shadow map(s) (+ hooks)", "  sun shadow mask (+ hooks)", "lighting -> fwd opaque done (sky)",
            "fwd opaque done -> transparents", "transparents", "transparents -> image effects",
            "image effects (post, FSR)", "image effects -> eye end",
            "VR camera cull (pre-cull -> pre-render)", "pre-render -> G-buffer start",
        };
        private const int RowsPerEye = 14, FirstEyeRow = 6, RowCount = FirstEyeRow + 2 * RowsPerEye;
        private static readonly string[] rowNames = BuildRowNames();
        private static string[] BuildRowNames()
        {
            var l = new List<string> { "frame (Update -> end mark)", "Update -> render start (LateUpdate work)",
                                       "render start -> first eye", "  render start -> VR camera pre-cull", "between the two eyes", "after the eyes -> end mark" };
            foreach (string e in new[] { "L ", "R " }) foreach (string r in eyeRowNames) l.Add(e + r);
            return l.ToArray();
        }

        private class Samples
        {
            public readonly List<float>[,] rows = new List<float>[Sources, RowCount];   // [src, row]
            public readonly List<float> frameInterval = new List<float>(), mainUpdateToEnd = new List<float>(), mainCull = new List<float>(), mainRenderAll = new List<float>();
            public readonly List<float>[] mainEye = { new List<float>(), new List<float>() };
            // main thread, wall and busy
            public readonly List<float> mainFrameWall = new List<float>(), mainFrameBusy = new List<float>(), mainUpdateToEndBusy = new List<float>();
            public readonly List<float> mainBetweenWall = new List<float>(), mainBetweenBusy = new List<float>(), mainRenderAllBusy = new List<float>();
            public readonly List<float>[] mainEyeCullWall = { new List<float>(), new List<float>() }, mainEyeCullBusy = { new List<float>(), new List<float>() };
            public readonly List<float>[] mainEyeBusy = { new List<float>(), new List<float>() };
            public readonly List<float> vrGpu = new List<float>(), vrInterval = new List<float>();
            public double xrWaitMs, xrWaitMax, xrBeginMs, xrEndMs, xrFrames, xrWaitFrames;
            public readonly ItemSums itemSums = new ItemSums();
            public int frames, rtFrames, gpuFrames, busyFrames, rtEyePasses, shadowMaps, shadowMasks, otherShadowMaps, preCullCalls, preRenderCalls;
            public Samples() { for (int s = 0; s < Sources; s++) for (int r = 0; r < RowCount; r++) rows[s, r] = new List<float>(); }
        }
        private static Samples[] samples;

        // main-thread per-frame scratch
        private static long tUpdateStart, tFirstPreCull, tFirstPreRender, tLastPostRender, tPrevUpdateStart, tPrevEnd;
        private static ulong cUpdateStart, cFirstPreCull, cLastPostRender, cPrevUpdateStart, cPrevEnd;
        private static bool prevSampled;
        private static Mode prevMode;
        private static int preCullThisFrame, preRenderThisFrame, eyePassIndex;
        private static readonly long[] tPreCull = new long[2], tPreRender = new long[2], tPostRender = new long[2];
        private static readonly ulong[] cPreCull = new ulong[2], cPreRender = new ulong[2], cPostRender = new ulong[2];
        private static uint lastVrFrame;

        // one frame's events for the parser
        private static readonly int[] fIds = new int[1024];
        private static readonly double[] fCpu = new double[1024];
        private static readonly float[] fGpu = new float[1024];
        private static readonly float[] fBusy = new float[1024];
        private static Camera probeCam;
        private static int fCount;
        private static bool managedOpen;
        private static long managedStart;

        //------------------------------------------------------------------------------------------------------------
        private void Update()
        {
            if (running)
            {
                long now = Stopwatch.GetTimestamp();
                ulong cycles = MainCycles();
                // The span since the last frame's end mark (present, xrWaitFrame, input, physics before Update) and the whole frame
                if (sampling && prevSampled && prevMode == mode)
                {
                    var s = samples[(int)mode];
                    if (tPrevEnd != 0) { s.mainBetweenWall.Add((float)((now - tPrevEnd) * tickMs)); AddValue(s.mainBetweenBusy, CyclesMs(cPrevEnd, cycles)); }
                    if (tPrevUpdateStart != 0) { s.mainFrameWall.Add((float)((now - tPrevUpdateStart) * tickMs)); AddValue(s.mainFrameBusy, CyclesMs(cPrevUpdateStart, cycles)); }
                }
                tPrevUpdateStart = now; cPrevUpdateStart = cycles;
                tUpdateStart = now; cUpdateStart = cycles;
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

        private static void AddValue(List<float> list, double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value)) list.Add((float)value);
        }

        // The headset camera: VRCam in a raid, the stereo Camera.main in the hideout and the menus (VRCam is unset there)
        private static Camera HeadsetCamera()
        {
            Camera cam = VRGlobals.VRCam;
            if (cam != null && cam.isActiveAndEnabled && cam.stereoEnabled) return cam;
            cam = Camera.main;
            return cam != null && cam.isActiveAndEnabled && cam.stereoEnabled ? cam : null;
        }

        // Also callable from UnityExplorer.
        public static void RunNow()
        {
            if (running) { Plugin.MyLog.LogWarning("[StereoCost] already running"); return; }
            Camera cam = HeadsetCamera();
            if (cam == null) { Plugin.MyLog.LogWarning("[StereoCost] no headset camera rendering both eyes - start it in a raid or the hideout"); return; }
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
            Plugin.MyLog.LogWarning($"[StereoCost] start on '{cam.name}': {cycles} x (both eyes {segmentSeconds}s, left eye only {segmentSeconds}s), " +
                                    $"{settleSeconds}s settle after each switch, GPU timing {(gpu ? "ON" : "OFF")}, sun lights watched: {lightAttached.Count / lightEvents.Length}, " +
                                    $"main-thread items timed: {mainItems.Count}. Stand still, don't aim. Right eye freezes during left-only.");
            var endOfFrame = new WaitForEndOfFrame();
            try
            {
                for (int c = 0; c < cycles * 2; c++)
                {
                    mode = (c % 2 == 0) ? Mode.Both : Mode.LeftOnly;
                    if (cam == null || !cam.isActiveAndEnabled) break;
                    cam.stereoTargetEye = mode == Mode.Both ? StereoTargetEyeMask.Both : StereoTargetEyeMask.Left;
                    sampling = false;
                    prevSampled = false;
                    float t0 = Time.realtimeSinceStartup;
                    while (Time.realtimeSinceStartup - t0 < settleSeconds + segmentSeconds && cam != null && cam.isActiveAndEnabled)
                    {
                        yield return endOfFrame;
                        long tEnd = Stopwatch.GetTimestamp();
                        ulong cEnd = MainCycles();
                        GL.IssuePluginEvent(eventPtr, EvFrameEnd);
                        bool wasSampling = sampling;
                        sampling = Time.realtimeSinceStartup - t0 >= settleSeconds;
                        if (sampling && !wasSampling) OpenXRFrameWatch.TakeStats();   // the frame calls' totals from here
                        if (wasSampling) EndMainFrame(tEnd, cEnd);
                        tPrevEnd = tEnd; cPrevEnd = cEnd; prevSampled = wasSampling; prevMode = mode;
                        ResetMainFrame();
                    }
                    TakeFrameCalls(samples[(int)mode]);
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

        // How long the OpenXR frame calls held their threads over the segment that just ended (frame watcher)
        private static void TakeFrameCalls(Samples s)
        {
            if (!OpenXRFrameWatch.Loaded) return;
            double[] watch = OpenXRFrameWatch.TakeStats();
            s.xrWaitMs += watch[(int)OpenXRFrameWatch.Stat.WaitFrameMs];
            s.xrWaitMax = Math.Max(s.xrWaitMax, watch[(int)OpenXRFrameWatch.Stat.WaitFrameMax]);
            s.xrWaitFrames += watch[(int)OpenXRFrameWatch.Stat.WaitFrames];
            s.xrBeginMs += watch[(int)OpenXRFrameWatch.Stat.BeginFrameMs];
            s.xrEndMs += watch[(int)OpenXRFrameWatch.Stat.EndFrameMs];
            s.xrFrames += watch[(int)OpenXRFrameWatch.Stat.Frames];
        }

        private static void Attach(Camera cam)
        {
            ringRead = ringWrite; managedOpen = false; fCount = 0;
            tscPerMs = nativeOk ? GpuProbe_TscPerMs() : 0;
            Cleanup(cam);   // before our markers go on: clears anything an earlier broken run left on the camera
            if (nativeOk) GL.IssuePluginEvent(eventPtr, EvDiscard);   // the last run's unread frames
            while (nativeOk && GpuProbe_PopFrame(fIds, fCpu, fGpu, fBusy, fIds.Length) >= 0) { }
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
            MainThreadAttach(cam);   // wraps the camera callbacks already there, before ours go on
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
            Application.onBeforeRender += OnBeforeRender;
            ResetMainFrame();
            tPrevEnd = tPrevUpdateStart = 0; prevSampled = false;
        }

        private static void Detach(Camera cam)
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPreRender -= OnPreRender;
            Camera.onPostRender -= OnPostRender;
            Application.onBeforeRender -= OnBeforeRender;
            MainThreadDetach();
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
            if (cam != probeCam) { OtherCameraMark(cam, false); return; }
            long t = Stopwatch.GetTimestamp();
            ulong c = MainCycles();
            if (preCullThisFrame++ == 0) { tFirstPreCull = t; cFirstPreCull = c; }
            if (eyePassIndex < 2) { tPreCull[eyePassIndex] = t; cPreCull[eyePassIndex] = c; }
            GL.IssuePluginEvent(eventPtr, EvVrPreCull);
        }
        private static void OnPreRender(Camera cam)
        {
            if (cam != probeCam) return;
            if (breakdown) Arrange(cam);   // buffers added for this eye (e.g. in another OnPreRender) get bracketed too
            long t = Stopwatch.GetTimestamp();
            ulong c = MainCycles();
            if (preRenderThisFrame++ == 0) tFirstPreRender = t;
            if (eyePassIndex < 2) { tPreRender[eyePassIndex] = t; cPreRender[eyePassIndex] = c; }
            GL.IssuePluginEvent(eventPtr, EvVrPreRender);
        }
        private static void OnPostRender(Camera cam)
        {
            if (cam != probeCam) { OtherCameraMark(cam, true); return; }
            long t = Stopwatch.GetTimestamp();
            ulong c = MainCycles();
            if (eyePassIndex < 2) { tPostRender[eyePassIndex] = t; cPostRender[eyePassIndex] = c; }
            eyePassIndex++;
            tLastPostRender = t; cLastPostRender = c;
        }
        private static void ResetMainFrame()
        {
            preCullThisFrame = preRenderThisFrame = 0;
            eyePassIndex = 0;
            tFirstPreCull = tFirstPreRender = tLastPostRender = 0;
            cFirstPreCull = cLastPostRender = 0;
            for (int e = 0; e < 2; e++) { tPreCull[e] = tPreRender[e] = tPostRender[e] = 0; cPreCull[e] = cPreRender[e] = cPostRender[e] = 0; }
            MainFrameReset();
        }
        private static void EndMainFrame(long tEnd, ulong cEnd)
        {
            var s = samples[(int)mode];
            s.frames++;
            s.frameInterval.Add(Time.unscaledDeltaTime * 1000f);
            if (tUpdateStart != 0) { s.mainUpdateToEnd.Add((float)((tEnd - tUpdateStart) * tickMs)); AddValue(s.mainUpdateToEndBusy, CyclesMs(cUpdateStart, cEnd)); }
            s.preCullCalls += preCullThisFrame;
            s.preRenderCalls += preRenderThisFrame;
            MainFrameCommit(s, eyePassIndex);
            if (eyePassIndex == 0) return;
            if (tFirstPreCull != 0 && tFirstPreRender != 0) s.mainCull.Add((float)((tFirstPreRender - tFirstPreCull) * tickMs));
            if (tFirstPreCull != 0 && tLastPostRender != 0) { s.mainRenderAll.Add((float)((tLastPostRender - tFirstPreCull) * tickMs)); AddValue(s.mainRenderAllBusy, CyclesMs(cFirstPreCull, cLastPostRender)); }
            for (int e = 0; e < Math.Min(eyePassIndex, 2); e++)
            {
                s.mainEye[e].Add((float)((tPostRender[e] - tPreRender[e]) * tickMs));
                AddValue(s.mainEyeBusy[e], CyclesMs(cPreRender[e], cPostRender[e]));
                if (tPreCull[e] != 0 && tPreRender[e] != 0)
                {
                    s.mainEyeCullWall[e].Add((float)((tPreRender[e] - tPreCull[e]) * tickMs));
                    AddValue(s.mainEyeCullBusy[e], CyclesMs(cPreCull[e], cPreRender[e]));
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------
        private static void DrainFrames()
        {
            if (nativeOk)
            {
                int n;
                // a frame whose GPU half failed arrives with NaN GPU times - its CPU half still counts
                while ((n = GpuProbe_PopFrame(fIds, fCpu, fGpu, fBusy, fIds.Length)) >= 0)
                {
                    fCount = n;
                    ParseFrame(n > 0 && !float.IsNaN(fGpu[n - 1]), n > 0 && !float.IsNaN(fBusy[n - 1]));
                }
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
                fIds[fCount] = id; fCpu[fCount] = (t - managedStart) * tickMs; fGpu[fCount] = float.NaN; fBusy[fCount] = float.NaN; fCount++;
                if (id == EvFrameEnd) { managedOpen = false; ParseFrame(false, false); }
            }
        }

        // Turns one frame's (id, wall, gpu, busy) list into per-row spans for the mode tagged in the stream.
        private const int EventSlots = 34;
        private static readonly double[,,] tAll = new double[Sources, 2, EventSlots];   // [src, eye, event id]
        private static readonly double[,] preCullAt = new double[Sources, 2], preRenderAt = new double[Sources, 2];
        private static readonly double[,] shadowSum = new double[Sources, 2], maskSum = new double[Sources, 2];
        private static readonly int[] shadowN = new int[2], maskN = new int[2];
        private static readonly double[] eventValue = new double[Sources], openShadow = new double[Sources], openMask = new double[Sources];
        private static readonly double[] renderStartAt = new double[Sources], frameEndAt = new double[Sources];
        private static void ParseFrame(bool hasGpu, bool hasBusy)
        {
            if (fCount < 3 || fIds[0] != EvFrameStart) return;
            int tag = fIds[1];
            if (tag != EvSampleBoth && tag != EvSampleLeft) return;
            if (fIds[fCount - 1] != EvFrameEnd) return;
            var s = samples[tag == EvSampleBoth ? 0 : 1];
            for (int src = 0; src < Sources; src++)
            {
                for (int e = 0; e < 2; e++)
                {
                    for (int k = 0; k < EventSlots; k++) tAll[src, e, k] = double.NaN;
                    preCullAt[src, e] = preRenderAt[src, e] = double.NaN;
                    shadowSum[src, e] = maskSum[src, e] = 0;
                }
                renderStartAt[src] = double.NaN;
                openShadow[src] = openMask[src] = double.NaN;
            }
            shadowN[0] = shadowN[1] = maskN[0] = maskN[1] = 0;
            int eye = -1, preCulls = 0, preRenders = 0;
            bool inLighting = false, afterEye = false;
            int eyeBlock = 0, frameBlock = 0;
            ItemFrameReset();
            for (int i = 2; i < fCount - 1; i++)
            {
                int id = fIds[i];
                eventValue[SrcWall] = fCpu[i];
                eventValue[SrcGpu] = hasGpu ? fGpu[i] : double.NaN;
                eventValue[SrcBusy] = hasBusy ? fBusy[i] : double.NaN;
                if (id >= ItemBase)
                {
                    int bucket = (eye < 0 || eye > 1 || afterEye) ? 0 : eye + 1;
                    ItemMark(id, eventValue, bucket, bucket == 0 ? frameBlock : eyeBlock);
                    continue;
                }
                if (id == EvRenderStart) { if (double.IsNaN(renderStartAt[SrcWall])) { for (int src = 0; src < Sources; src++) renderStartAt[src] = eventValue[src]; frameBlock = 1; } continue; }
                if (id == EvVrPreCull) { int k = preCulls++; if (k < 2) for (int src = 0; src < Sources; src++) preCullAt[src, k] = eventValue[src]; continue; }
                if (id == EvVrPreRender) { int k = preRenders++; if (k < 2) for (int src = 0; src < Sources; src++) preRenderAt[src, k] = eventValue[src]; continue; }
                if (id == EvEyeStart)
                {
                    eye++; afterEye = false; eyeBlock = 0;
                    if (eye >= 0 && eye < 2) for (int src = 0; src < Sources; src++) tAll[src, eye, EvEyeStart] = eventValue[src];
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
                if (id == EvBeforeShadowMap) { for (int src = 0; src < Sources; src++) openShadow[src] = eventValue[src]; continue; }
                if (id == EvAfterShadowMap && !double.IsNaN(openShadow[SrcWall]))
                {
                    for (int src = 0; src < Sources; src++) { shadowSum[src, eye] += eventValue[src] - openShadow[src]; openShadow[src] = double.NaN; }
                    shadowN[eye]++;
                    continue;
                }
                if (id == EvBeforeMask) { for (int src = 0; src < Sources; src++) openMask[src] = eventValue[src]; continue; }
                if (id == EvAfterMask && !double.IsNaN(openMask[SrcWall]))
                {
                    for (int src = 0; src < Sources; src++) { maskSum[src, eye] += eventValue[src] - openMask[src]; openMask[src] = double.NaN; }
                    maskN[eye]++;
                    continue;
                }
                if (id < EventSlots && double.IsNaN(tAll[SrcWall, eye, id])) for (int src = 0; src < Sources; src++) tAll[src, eye, id] = eventValue[src];
            }
            int eyes = Math.Min(eye + 1, 2);
            if (eyes == 0) return;
            s.rtFrames++; s.rtEyePasses += eyes;
            if (hasGpu) s.gpuFrames++;
            if (hasBusy) s.busyFrames++;
            for (int e = 0; e < eyes; e++) { s.shadowMaps += shadowN[e]; s.shadowMasks += maskN[e]; }
            frameEndAt[SrcWall] = fCpu[fCount - 1];
            frameEndAt[SrcGpu] = hasGpu ? fGpu[fCount - 1] : double.NaN;
            frameEndAt[SrcBusy] = hasBusy ? fBusy[fCount - 1] : double.NaN;
            ItemFrameCommit(s, hasGpu, hasBusy, eyes);
            for (int src = 0; src < Sources; src++)
            {
                if ((src == SrcGpu && !hasGpu) || (src == SrcBusy && !hasBusy)) continue;
                double fend = frameEndAt[src];
                double rstart = renderStartAt[src];
                Add(s, src, 0, fend);
                Add(s, src, 1, rstart);
                Add(s, src, 2, tAll[src, 0, EvBeforeGBuffer] - rstart);
                Add(s, src, 3, preCullAt[src, 0] - rstart);
                if (eyes == 2) Add(s, src, 4, tAll[src, 1, EvBeforeGBuffer] - tAll[src, 0, EvAfterEverything]);
                Add(s, src, 5, fend - tAll[src, eyes - 1, EvAfterEverything]);
                for (int e = 0; e < eyes; e++)
                {
                    int r = FirstEyeRow + e * RowsPerEye;
                    Add(s, src, r + 0, tAll[src, e, EvAfterEverything] - tAll[src, e, EvBeforeGBuffer]);
                    Add(s, src, r + 1, tAll[src, e, EvAfterGBuffer] - tAll[src, e, EvBeforeGBuffer]);
                    Add(s, src, r + 2, tAll[src, e, EvBeforeLighting] - tAll[src, e, EvAfterGBuffer]);
                    Add(s, src, r + 3, tAll[src, e, EvAfterLighting] - tAll[src, e, EvBeforeLighting]);
                    if (shadowN[e] > 0) Add(s, src, r + 4, shadowSum[src, e]);
                    if (maskN[e] > 0) Add(s, src, r + 5, maskSum[src, e]);
                    Add(s, src, r + 6, tAll[src, e, EvAfterOpaque] - tAll[src, e, EvAfterLighting]);
                    Add(s, src, r + 7, tAll[src, e, EvBeforeAlpha] - tAll[src, e, EvAfterOpaque]);
                    Add(s, src, r + 8, tAll[src, e, EvAfterAlpha] - tAll[src, e, EvBeforeAlpha]);
                    Add(s, src, r + 9, tAll[src, e, EvBeforeFx] - tAll[src, e, EvAfterAlpha]);
                    Add(s, src, r + 10, tAll[src, e, EvAfterFx] - tAll[src, e, EvBeforeFx]);
                    Add(s, src, r + 11, tAll[src, e, EvAfterEverything] - tAll[src, e, EvAfterFx]);
                    Add(s, src, r + 12, preRenderAt[src, e] - preCullAt[src, e]);
                    Add(s, src, r + 13, tAll[src, e, EvBeforeGBuffer] - preRenderAt[src, e]);
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
            for (int k = 0; k < openAt.GetLength(1); k++) for (int src = 0; src < Sources; src++) openAt[src, k] = double.NaN;
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
            if (!sampling || OpenXRBootstrap.Active) return;
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
        private static void Value(StringBuilder sb, string label, double both, double left)
        {
            string d = double.IsNaN(both) || double.IsNaN(left) ? "      -" : (both - left).ToString("+0.00;-0.00").PadLeft(7);
            sb.AppendLine($"[StereoCost]   {label,-46} {Fmt((float)both)} {Fmt((float)left)} {d}");
        }
        private static string Fmt(float v) => float.IsNaN(v) ? "     -" : v.ToString("0.00").PadLeft(6);
        private static string Per(int n, int frames) => frames == 0 ? "-" : (n / (float)frames).ToString("0.00");

        // Share of each eye's image the lenses never show (SteamVR's hidden-area mesh, in 0..1 eye-image coordinates;
        // the same share of the upscaler's smaller render rect) = pixels a mask could skip in every per-pixel pass.
        private static string HiddenAreaLine()
        {
            if (OpenXRBootstrap.Active) return "[StereoCost]   headset hidden area: (OpenVR only)";
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

        private static string ThreadName(int id, int renderThreadId)
        {
            if (id == 0) return "not seen";
            if (id == mainThreadId) return "main thread";
            if (id == renderThreadId) return "render thread";
            return $"thread {id}";
        }

        private static void Report(bool gpu)
        {
            var b = samples[0]; var l = samples[1];
            var sb = new StringBuilder();
            sb.AppendLine("[StereoCost] ===== result (medians, ms) =====     both   left   both-left");
            Row(sb, "frame interval (Unity)", b.frameInterval, l.frameInterval);
            if (!OpenXRBootstrap.Active)
            {
                Row(sb, "frame interval (SteamVR client)", b.vrInterval, l.vrInterval);
                Row(sb, "GPU before submit (SteamVR)", b.vrGpu, l.vrGpu);
            }
            sb.AppendLine("[StereoCost]   --- MAIN THREAD (wall = clock; busy = the thread's own CPU, waits left out) ---");
            Row(sb, "frame (Update -> next Update), wall", b.mainFrameWall, l.mainFrameWall);
            Row(sb, "frame (Update -> next Update), BUSY", b.mainFrameBusy, l.mainFrameBusy);
            Row(sb, "early Update -> end of frame, wall", b.mainUpdateToEnd, l.mainUpdateToEnd);
            Row(sb, "early Update -> end of frame, BUSY", b.mainUpdateToEndBusy, l.mainUpdateToEndBusy);
            Row(sb, "end of frame -> next Update, wall", b.mainBetweenWall, l.mainBetweenWall);
            Row(sb, "end of frame -> next Update, BUSY", b.mainBetweenBusy, l.mainBetweenBusy);
            Row(sb, "VR camera: first cull -> last eye done, wall", b.mainRenderAll, l.mainRenderAll);
            Row(sb, "VR camera: first cull -> last eye done, BUSY", b.mainRenderAllBusy, l.mainRenderAllBusy);
            Row(sb, "L eye cull (pre-cull -> pre-render), wall", b.mainEyeCullWall[0], l.mainEyeCullWall[0]);
            Row(sb, "L eye cull, BUSY", b.mainEyeCullBusy[0], l.mainEyeCullBusy[0]);
            Row(sb, "L eye pass (pre-render -> post-render), wall", b.mainEye[0], l.mainEye[0]);
            Row(sb, "L eye pass, BUSY", b.mainEyeBusy[0], l.mainEyeBusy[0]);
            Row(sb, "R eye cull (pre-cull -> pre-render), wall", b.mainEyeCullWall[1], l.mainEyeCullWall[1]);
            Row(sb, "R eye cull, BUSY", b.mainEyeCullBusy[1], l.mainEyeCullBusy[1]);
            Row(sb, "R eye pass (pre-render -> post-render), wall", b.mainEye[1], l.mainEye[1]);
            Row(sb, "R eye pass, BUSY", b.mainEyeBusy[1], l.mainEyeBusy[1]);
            if (OpenXRFrameWatch.Loaded)
            {
                int[] threads = OpenXRFrameWatch.Threads();
                int renderThreadId = nativeOk ? GpuProbe_RenderThreadId() : 0;
                sb.AppendLine($"[StereoCost]   --- OPENXR frame calls (xrWaitFrame on the {ThreadName(threads[0], renderThreadId)}, xrBeginFrame on the {ThreadName(threads[1], renderThreadId)}, "
                    + $"xrEndFrame on the {ThreadName(threads[2], renderThreadId)}) ---");
                Value(sb, "xrWaitFrame wait a frame (mean)", b.xrWaitFrames > 0 ? b.xrWaitMs / b.xrWaitFrames : double.NaN, l.xrWaitFrames > 0 ? l.xrWaitMs / l.xrWaitFrames : double.NaN);
                Value(sb, "xrWaitFrame longest wait", b.xrWaitMax, l.xrWaitMax);
                Value(sb, "xrBeginFrame a frame (mean)", b.xrFrames > 0 ? b.xrBeginMs / b.xrFrames : double.NaN, l.xrFrames > 0 ? l.xrBeginMs / l.xrFrames : double.NaN);
                Value(sb, "xrEndFrame a frame (mean)", b.xrFrames > 0 ? b.xrEndMs / b.xrFrames : double.NaN, l.xrFrames > 0 ? l.xrEndMs / l.xrFrames : double.NaN);
            }
            string[] sourceNames = { "--- RENDER THREAD wall (CPU clock, includes waiting for the main thread's commands) ---", "--- GPU (timestamps) ---",
                                     "--- RENDER THREAD BUSY (its own CPU work, waits left out) ---" };
            foreach (int src in new[] { SrcWall, SrcBusy, SrcGpu })
            {
                sb.AppendLine("[StereoCost]   " + sourceNames[src]);
                for (int r = 0; r < rowNames.Length; r++)
                    if (b.rows[src, r].Count + l.rows[src, r].Count > 0) Row(sb, rowNames[r], b.rows[src, r], l.rows[src, r]);
            }
            // Which one holds the frame: the busiest of main thread, render thread and GPU, both eyes
            float mainBusy = Median(b.mainFrameBusy), renderBusy = Median(b.rows[SrcBusy, 0]), gpuFrame = Median(b.rows[SrcGpu, 0]), frame = Median(b.frameInterval);
            sb.AppendLine($"[StereoCost]   >>> both eyes, frame {Fmt(frame)} ms: main thread busy {Fmt(mainBusy)}, render thread busy {Fmt(renderBusy)}, GPU {Fmt(gpuFrame)} "
                + $"-> the frame is held by the {Busiest(mainBusy, renderBusy, gpuFrame)} (left only: main {Fmt(Median(l.mainFrameBusy))}, render {Fmt(Median(l.rows[SrcBusy, 0]))}, GPU {Fmt(Median(l.rows[SrcGpu, 0]))})");
            sb.AppendLine($"[StereoCost]   frames both/left: {b.frames}/{l.frames}; render-thread frames {b.rtFrames}/{l.rtFrames}; with GPU times {b.gpuFrames}/{l.gpuFrames}; "
                + $"with busy times {b.busyFrames}/{l.busyFrames} (TSC {tscPerMs / 1000:0.###} MHz)");
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
            ReportMainThread(sb, b);
            sb.Append("[StereoCost] ===== end =====");
            Plugin.MyLog.LogWarning(sb.ToString());
        }

        private static string Busiest(float main, float render, float gpu)
        {
            float top = Math.Max(float.IsNaN(main) ? 0 : main, Math.Max(float.IsNaN(render) ? 0 : render, float.IsNaN(gpu) ? 0 : gpu));
            if (top <= 0) return "(unknown)";
            return top == main ? "MAIN THREAD" : top == render ? "RENDER THREAD" : "GPU";
        }
    }
}
