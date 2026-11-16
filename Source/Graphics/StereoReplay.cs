using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace TarkovVR.Source.Graphics
{
    // Dev experiment (Ctrl+F9, only when native/SPTVRStereoReplay.dll is there): the headset camera drawn ONCE for both eyes. Unity renders
    // the left eye only; the native layer records that pass's D3D11 calls and replays them into the right eye with the right eye's camera
    // values (found by value in the constant buffers: the patterns sent here each frame). The right eye's image comes from the OpenXR
    // frame watcher, registered at boot when the DLL is there. First test: the hideout, upscaler and the graphics mods off.
    internal class StereoReplay : MonoBehaviour
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string path);
        [DllImport("SPTVRStereoReplay")] private static extern int SR_Init(IntPtr anyD3D11Resource);
        [DllImport("SPTVRStereoReplay")] private static extern void SR_SetEnabled(int on);
        [DllImport("SPTVRStereoReplay")] private static extern IntPtr SR_GetRenderEventFunc();
        [DllImport("SPTVRStereoReplay")] private static extern void SR_SetFrame(int slot, int count, int[] lengths, float[] tolerances, float[] left, float[] right);
        [DllImport("SPTVRStereoReplay")] private static extern int SR_Stats(double[] totals, int max);
        [DllImport("SPTVRStereoReplay")] private static extern int SR_PatternHits(double[] hits, int max);
        [DllImport("SPTVRStereoReplay")] private static extern int SR_Info(byte[] text, int max);

        // The native counters, in its order (StereoReplay.cpp, enum Stat)
        private enum Stat
        {
            Windows, Dropped, Unpaired, NoImages, Aborted, FinishFailed, Mirrored, Draws, CbMaps, CbPatched, CbUpdates,
            CbNoOverwrite, OtherDiscards, ViewsSwapped, CopiesSwapped, LeftUnswappable, NestedLists, Predication, CopiedNow,
            CopiedAtRelease, WindowMs, ExecuteMs, ScanMs, CaptureMs,
            ImmediateDrawsOutside, ImmediateListsOutside, DeferredDrawsInside, DeferredDrawsOutside, DeferredLists, Count
        }

        public static float matrixTolerance = 2e-5f;      // relative (absolute below 1): Unity's own products round differently from ours
        public static float positionTolerance = 1e-4f;    // m (relative past 1 m): the eyes are 6 cm apart, the centre 3
        public static bool combinedCulling = true;        // the culling frustum covers both eyes (else the right eye's outer edge is culled)
        public static float cullingMarginDegrees = 5f;
        public static float logInterval = 2f;            // s

        private const int StartEvent = 1000, EndEvent = 2000, FrameSlots = 8, MaxPatterns = 64;

        public static bool Installed => File.Exists(DllPath);
        public static bool Active { get; private set; }

        private static string DllPath => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "native", "SPTVRStereoReplay.dll");

        private static bool nativeTried, nativeOk;
        private static IntPtr eventFunction;
        private static RenderTexture deviceProbe;
        private static Camera headsetCamera;
        private static CommandBuffer endBuffer;
        private static StereoTargetEyeMask savedTargetEye;
        private static bool savedOcclusionMesh, savedFoveated;
        private static int frameSlot;
        private static Matrix4x4[] previousLeft = new Matrix4x4[2], previousRight = new Matrix4x4[2];
        private static bool havePrevious;

        // This frame's patterns
        private static readonly int[] lengths = new int[MaxPatterns];
        private static readonly float[] tolerances = new float[MaxPatterns];
        private static readonly float[] leftValues = new float[MaxPatterns * 16];
        private static readonly float[] rightValues = new float[MaxPatterns * 16];
        private static readonly string[] patternNames = new string[MaxPatterns];
        private static int patternCount;

        // Frame time, on and off (the last window before switching on is the both-eyes baseline)
        private static float windowStart;
        private static int windowFrames;
        private static float windowSeconds;
        private static float lastOffFrameMs;

        private void Update()
        {
            windowFrames++;
            windowSeconds += Time.unscaledDeltaTime;
            if (Input.GetKeyDown(KeyCode.F9) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            {
                if (Active) Stop("switched off (Ctrl+F9)");
                else Begin();
            }
            if (Active && (headsetCamera == null || !headsetCamera.isActiveAndEnabled))
                Stop("the headset camera went away");
            if (Time.unscaledTime - windowStart >= logInterval)
            {
                float frameMs = windowFrames > 0 ? windowSeconds * 1000f / windowFrames : 0f;
                if (Active) LogStats(frameMs);
                else lastOffFrameMs = frameMs;
                windowStart = Time.unscaledTime;
                windowFrames = 0;
                windowSeconds = 0f;
            }
        }

        private static bool TryNative()
        {
            if (nativeTried) return nativeOk;
            nativeTried = true;
            try
            {
                if (LoadLibrary(DllPath) == IntPtr.Zero)
                {
                    Plugin.MyLog.LogWarning($"[StereoReplay] couldn't load {DllPath}");
                    return false;
                }
                deviceProbe = new RenderTexture(4, 4, 0) { name = "SPT-VR StereoReplay device" };
                deviceProbe.Create();
                nativeOk = SR_Init(deviceProbe.GetNativeTexturePtr()) == 1;
                eventFunction = nativeOk ? SR_GetRenderEventFunc() : IntPtr.Zero;
                byte[] text = new byte[2048];
                int length = SR_Info(text, text.Length);
                Plugin.MyLog.LogInfo($"[StereoReplay] native {(nativeOk ? "ready" : "FAILED")}: {Encoding.ASCII.GetString(text, 0, Math.Max(0, length)).Trim()}");
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogWarning($"[StereoReplay] native layer unavailable: {exception.Message}");
                nativeOk = false;
            }
            return nativeOk;
        }

        // The headset camera: VRCam in a raid, the stereo Camera.main in the hideout (VRCam is unset there)
        private static Camera HeadsetCamera()
        {
            Camera candidate = VRGlobals.VRCam;
            if (candidate != null && candidate.isActiveAndEnabled && candidate.stereoEnabled) return candidate;
            candidate = Camera.main;
            return candidate != null && candidate.isActiveAndEnabled && candidate.stereoEnabled ? candidate : null;
        }

        public static void Begin()
        {
            if (Active) return;
            if (!TarkovVR.Source.XR.OpenXRFrameWatch.Loaded)
            {
                Plugin.MyLog.LogWarning("[StereoReplay] the OpenXR frame watcher isn't running (it gives the right eye's image): OpenXR only, and a restart after installing the DLL");
                return;
            }
            Camera headset = HeadsetCamera();
            if (headset == null)
            {
                Plugin.MyLog.LogWarning("[StereoReplay] no headset camera rendering both eyes - start it in the hideout or a raid");
                return;
            }
            if (!TryNative()) return;

            headsetCamera = headset;
            savedTargetEye = headsetCamera.stereoTargetEye;
            savedOcclusionMesh = XRSettings.useOcclusionMesh;
            savedFoveated = FoveatedMask.IsEnabled;
            // both are drawn per eye from the eye's own shape, which the replay would copy from the left
            XRSettings.useOcclusionMesh = false;
            if (savedFoveated) FoveatedMask.SetEnabled(false);
            headsetCamera.stereoTargetEye = StereoTargetEyeMask.Left;
            endBuffer = new CommandBuffer { name = "SPT-VR stereo replay end" };
            endBuffer.IssuePluginEvent(eventFunction, EndEvent);
            headsetCamera.AddCommandBuffer(CameraEvent.AfterEverything, endBuffer);
            Camera.onPreCull += OnPreCull;
            havePrevious = false;
            SR_SetEnabled(1);
            SR_Stats(new double[(int)Stat.Count + 1], (int)Stat.Count + 1);
            SR_PatternHits(new double[MaxPatterns], MaxPatterns);
            Active = true;
            Plugin.MyLog.LogInfo($"[StereoReplay] ON on '{headsetCamera.name}': the left eye is rendered, the right eye replayed. Both eyes before: {lastOffFrameMs:0.00} ms a frame. "
                + "For the first test: Tarkov's upscaler / anti-aliasing off, the graphics mods off (occlusion mesh and foveated masking are off while it runs)");
        }

        public static void Stop(string reason)
        {
            if (!Active) return;
            Active = false;
            SR_SetEnabled(0);
            Camera.onPreCull -= OnPreCull;
            if (headsetCamera != null)
            {
                if (endBuffer != null) headsetCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, endBuffer);
                headsetCamera.stereoTargetEye = savedTargetEye;
                headsetCamera.ResetCullingMatrix();
            }
            endBuffer?.Release();
            endBuffer = null;
            XRSettings.useOcclusionMesh = savedOcclusionMesh;
            if (savedFoveated) FoveatedMask.SetEnabled(true);
            headsetCamera = null;
            Plugin.MyLog.LogInfo($"[StereoReplay] OFF: {reason}");
        }

        //------------------------------------------------------------------------------------------------------------------------------
        // Each frame: the left and right eyes' camera values, then the start of the recording (before the camera culls and renders)

        private static void OnPreCull(Camera culling)
        {
            if (!Active || culling != headsetCamera) return;
            Matrix4x4 viewLeft = culling.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
            Matrix4x4 viewRight = culling.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
            Matrix4x4 projectionLeft = culling.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
            Matrix4x4 projectionRight = culling.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);

            patternCount = 0;
            AddMatrix("view", viewLeft, viewRight);
            AddMatrix("view inverse", viewLeft.inverse, viewRight.inverse);
            AddMatrix("projection", projectionLeft, projectionRight);
            AddMatrix("projection inverse", projectionLeft.inverse, projectionRight.inverse);
            for (int flip = 0; flip < 2; flip++)
            {
                string tag = flip == 0 ? " (into texture)" : " (into screen)";
                Matrix4x4 gpuLeft = GL.GetGPUProjectionMatrix(projectionLeft, flip == 0);
                Matrix4x4 gpuRight = GL.GetGPUProjectionMatrix(projectionRight, flip == 0);
                Matrix4x4 viewProjectionLeft = gpuLeft * viewLeft;
                Matrix4x4 viewProjectionRight = gpuRight * viewRight;
                AddMatrix("GPU projection" + tag, gpuLeft, gpuRight);
                AddMatrix("GPU projection inverse" + tag, gpuLeft.inverse, gpuRight.inverse);
                AddMatrix("view-projection" + tag, viewProjectionLeft, viewProjectionRight);
                AddMatrix("view-projection inverse" + tag, viewProjectionLeft.inverse, viewProjectionRight.inverse);
                if (havePrevious)
                    AddMatrix("last frame's view-projection" + tag, previousLeft[flip], previousRight[flip]);
                previousLeft[flip] = viewProjectionLeft;
                previousRight[flip] = viewProjectionRight;
            }
            havePrevious = true;
            // the eye's own transform (Unity's view matrix is this with z turned round)
            Matrix4x4 flipZ = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            Matrix4x4 worldToEyeLeft = flipZ * viewLeft;
            Matrix4x4 worldToEyeRight = flipZ * viewRight;
            AddMatrix("world to eye", worldToEyeLeft, worldToEyeRight);
            AddMatrix("eye to world", worldToEyeLeft.inverse, worldToEyeRight.inverse);
            AddVector("eye position", viewLeft.inverse.GetColumn(3), viewRight.inverse.GetColumn(3));

            SR_SetFrame(frameSlot, patternCount, lengths, tolerances, leftValues, rightValues);
            if (combinedCulling)
                culling.cullingMatrix = CombinedCulling(culling, viewLeft, viewRight, projectionLeft, projectionRight);
            GL.IssuePluginEvent(eventFunction, StartEvent + frameSlot);
            frameSlot = (frameSlot + 1) % FrameSlots;
        }

        // In Unity's memory order (columns) and transposed
        private static void AddMatrix(string name, Matrix4x4 left, Matrix4x4 right)
        {
            for (int transposed = 0; transposed < 2; transposed++)
            {
                if (patternCount >= MaxPatterns) return;
                int at = patternCount * 16;
                for (int column = 0; column < 4; column++)
                {
                    for (int row = 0; row < 4; row++)
                    {
                        int index = transposed == 0 ? column * 4 + row : row * 4 + column;
                        leftValues[at + index] = left[row, column];
                        rightValues[at + index] = right[row, column];
                    }
                }
                lengths[patternCount] = 16;
                tolerances[patternCount] = matrixTolerance;
                patternNames[patternCount] = transposed == 0 ? name : name + " (rows)";
                patternCount++;
            }
        }

        private static void AddVector(string name, Vector4 left, Vector4 right)
        {
            if (patternCount >= MaxPatterns) return;
            int at = patternCount * 16;
            for (int index = 0; index < 3; index++)
            {
                leftValues[at + index] = left[index];
                rightValues[at + index] = right[index];
            }
            lengths[patternCount] = 3;
            tolerances[patternCount] = positionTolerance;
            patternNames[patternCount] = name;
            patternCount++;
        }

        // One frustum holding both eyes': from between them, moved back until each eye is inside, as wide as the wider eye plus a margin
        // (the eyes' own frusta are off-centre, and a canted display turns them)
        private static Matrix4x4 CombinedCulling(Camera culling, Matrix4x4 viewLeft, Matrix4x4 viewRight, Matrix4x4 projectionLeft, Matrix4x4 projectionRight)
        {
            float tanX = 0f, tanY = 0f;
            foreach (Matrix4x4 projection in new[] { projectionLeft, projectionRight })
            {
                // the frustum's edges at unit distance (OpenGL off-centre projection)
                float left = (projection.m02 - 1f) / projection.m00, right = (projection.m02 + 1f) / projection.m00;
                float bottom = (projection.m12 - 1f) / projection.m11, top = (projection.m12 + 1f) / projection.m11;
                tanX = Mathf.Max(tanX, Mathf.Abs(left), Mathf.Abs(right));
                tanY = Mathf.Max(tanY, Mathf.Abs(bottom), Mathf.Abs(top));
            }
            float margin = cullingMarginDegrees * Mathf.Deg2Rad;
            tanX = Mathf.Tan(Mathf.Min(Mathf.Atan(tanX) + margin, 85f * Mathf.Deg2Rad));
            tanY = Mathf.Tan(Mathf.Min(Mathf.Atan(tanY) + margin, 85f * Mathf.Deg2Rad));
            Matrix4x4 eyeLeft = viewLeft.inverse;
            Matrix4x4 eyeRight = viewRight.inverse;
            Vector3 positionLeft = eyeLeft.GetColumn(3);
            Vector3 positionRight = eyeRight.GetColumn(3);
            Vector3 centre = (positionLeft + positionRight) * 0.5f;
            Vector3 backward = eyeLeft.GetColumn(2);    // the view looks down -z
            float halfBaseline = (positionRight - positionLeft).magnitude * 0.5f;
            Vector3 apex = centre + backward * (halfBaseline / tanX + 0.02f);
            Matrix4x4 view = viewLeft;
            view.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
            view = view * Matrix4x4.Translate(-apex);
            float near = culling.nearClipPlane, far = culling.farClipPlane;
            Matrix4x4 combined = Matrix4x4.Frustum(-tanX * near, tanX * near, -tanY * near, tanY * near, near, far);
            return combined * view;
        }

        //------------------------------------------------------------------------------------------------------------------------------

        private static void LogStats(float frameMs)
        {
            double[] totals = new double[(int)Stat.Count + 1];
            SR_Stats(totals, totals.Length);
            double[] hits = new double[MaxPatterns];
            SR_PatternHits(hits, MaxPatterns);
            double windows = Math.Max(1.0, totals[(int)Stat.Windows]);
            double PerWindow(Stat stat) => totals[(int)stat] / windows;
            StringBuilder found = new StringBuilder();
            for (int index = 0; index < patternCount; index++)
            {
                if (hits[index] > 0)
                    found.Append(found.Length > 0 ? ", " : "").Append(patternNames[index]).Append(' ').Append((hits[index] / windows).ToString("0.#"));
            }
            Plugin.MyLog.LogInfo($"[StereoReplay] {frameMs:0.00} ms a frame (both eyes before: {lastOffFrameMs:0.00}); replays {totals[(int)Stat.Windows]:0}, dropped {totals[(int)Stat.Dropped]:0} "
                + $"(stopped by an unrecordable call {totals[(int)Stat.Aborted]:0}, last at vtable slot {totals[(int)Stat.Count]:0}; unpaired {totals[(int)Stat.Unpaired]:0}; finish failed {totals[(int)Stat.FinishFailed]:0}), "
                + $"no eye images {totals[(int)Stat.NoImages]:0}. A replay: {PerWindow(Stat.Mirrored):0} state calls, {PerWindow(Stat.Draws):0} draws; constant uploads {PerWindow(Stat.CbMaps):0} mapped + "
                + $"{PerWindow(Stat.CbUpdates):0} updated, {PerWindow(Stat.CbPatched):0} with the right eye's values; mapped without discard {PerWindow(Stat.CbNoOverwrite):0}; other buffers discarded "
                + $"{PerWindow(Stat.OtherDiscards):0}; left image views swapped {PerWindow(Stat.ViewsSwapped):0}, copies {PerWindow(Stat.CopiesSwapped):0}, unswappable {PerWindow(Stat.LeftUnswappable):0}; "
                + $"nested command lists {PerWindow(Stat.NestedLists):0}, predication {PerWindow(Stat.Predication):0}; right eye copied at once {totals[(int)Stat.CopiedNow]:0} / at hand-back "
                + $"{totals[(int)Stat.CopiedAtRelease]:0}. Render thread a replay: pass {PerWindow(Stat.WindowMs):0.00} ms, replay {PerWindow(Stat.ExecuteMs):0.00}, value search {PerWindow(Stat.ScanMs):0.00}, "
                + $"state copy {PerWindow(Stat.CaptureMs):0.00}. Draws a frame elsewhere: main context outside the pass {PerWindow(Stat.ImmediateDrawsOutside):0}, "
                + $"Unity's job contexts inside the pass {PerWindow(Stat.DeferredDrawsInside):0} / outside {PerWindow(Stat.DeferredDrawsOutside):0}, job command lists "
                + $"{PerWindow(Stat.DeferredLists):0} made, {PerWindow(Stat.ImmediateListsOutside):0} run outside the pass. Values found a replay:{(found.Length > 0 ? found.ToString() : "NONE (the right eye uses the left eye's camera)")}");
        }
    }
}
