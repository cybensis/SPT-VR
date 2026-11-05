using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine.XR.OpenXR.Features;

namespace TarkovVR.Source.XR
{
    // Puts the native frame watcher (native/SPTVRXrFrameWatch.dll) in front of OpenXR's frame functions, through the function lookup
    // Unity lets every feature hook, for the alternate-eye experiment (AlternateEyeExperiment, Ctrl+F11): what Unity's OpenXR plugin
    // submits each frame, which eye images it takes, and how long the frame calls wait. It only looks. Without the DLL nothing is
    // hooked. Native because xrEndFrame and the swapchain calls run on Unity's render thread.
    public class OpenXRFrameWatch : OpenXRFeature
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string path);
        [DllImport("SPTVRXrFrameWatch")] private static extern IntPtr XrWatch_Hook(IntPtr next);
        [DllImport("SPTVRXrFrameWatch")] private static extern int XrWatch_Hooked();
        [DllImport("SPTVRXrFrameWatch")] private static extern int XrWatch_Stats(double[] totals, int max);
        [DllImport("SPTVRXrFrameWatch")] private static extern int XrWatch_Detail(byte[] text, int max);
        [DllImport("SPTVRXrFrameWatch")] private static extern void XrWatch_DescribeNext(int frames);
        [DllImport("SPTVRXrFrameWatch")] private static extern void XrAlt_SetRig(int slot, float px, float py, float pz, float qx, float qy, float qz, float qw, float scale);
        [DllImport("SPTVRXrFrameWatch")] private static extern IntPtr XrAlt_GetRenderEventFunc();
        [DllImport("SPTVRXrFrameWatch")] private static extern void XrAlt_Forget();

        // The totals the watcher keeps, in its order (XrFrameWatch.cpp, enum Stat)
        public enum Stat
        {
            Frames, Layers, ProjectionLayers, Views, ViewsShareSwapchain,
            View0Acquired, View1Acquired, View0SamePose, View1SamePose,
            Acquires, Releases, SwapchainsTaken,
            WaitFrameMs, WaitFrameMax, BeginFrameMs, BeginFrameMax, WaitImageMs, WaitImageMax,
            EndFrameMs, EndFrameMax, AcquireMs, View0ArraySlice, View1ArraySlice, WaitFrames, BeginFrames,
            AltFrames, AltKept, AltRestored, AltPosesFixed, AltNoImage, AltNoCopyYet, AltCopyFailed,
            Count
        }

        public static bool Loaded { get; private set; }

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            try
            {
                string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "native", "SPTVRXrFrameWatch.dll");
                if (!File.Exists(path) || LoadLibrary(path) == IntPtr.Zero)
                {
                    Plugin.MyLog.LogInfo("[OpenXR-FrameWatch] native/SPTVRXrFrameWatch.dll not there: frames not watched");
                    return func;
                }
                IntPtr ours = XrWatch_Hook(func);
                Loaded = ours != IntPtr.Zero;
                Plugin.MyLog.LogInfo($"[OpenXR-FrameWatch] {(Loaded ? "watching" : "NOT watching")} the frame functions");
                return Loaded ? ours : func;
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[OpenXR-FrameWatch] hook failed, frames not watched: {exception}");
                return func;
            }
        }

        // Which frame functions came through the watcher (bits: 1 end, 2 wait frame, 4 begin, 8 acquire, 16 wait image, 32 release)
        public static int Hooked()
        {
            return Loaded ? XrWatch_Hooked() : 0;
        }

        // The totals since the last call, then zeroed
        public static double[] TakeStats()
        {
            double[] totals = new double[(int)Stat.Count];
            if (Loaded)
                XrWatch_Stats(totals, totals.Length);
            return totals;
        }

        public static void DescribeNextFrames(int frames)
        {
            if (Loaded)
                XrWatch_DescribeNext(frames);
        }

        // Alternate eyes, a frame the game draws one eye (0 left, 1 right): the rig's world pose for it, then the render event that
        // names the eye, issued before the frame's cameras render (so it reaches the render thread before Unity hands the eye images
        // back). The watcher then gives the eye left out its last drawing and the pose it was drawn with.
        public static void MarkOneEyeFrame(int eye, int slot, UnityEngine.Transform rig)
        {
            if (!Loaded)
                return;
            UnityEngine.Vector3 position = rig != null ? rig.position : UnityEngine.Vector3.zero;
            UnityEngine.Quaternion rotation = rig != null ? rig.rotation : UnityEngine.Quaternion.identity;
            float scale = rig != null ? rig.lossyScale.x : 1f;
            XrAlt_SetRig(slot, position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w, scale);
            if (renderEventFunc == IntPtr.Zero)
                renderEventFunc = XrAlt_GetRenderEventFunc();
            UnityEngine.GL.IssuePluginEvent(renderEventFunc, unchecked((int)0x5A000000) | (eye << 8) | (slot & 0xFF));
        }
        private static IntPtr renderEventFunc;

        // Leaving alternate eyes: the copies kept are dropped, the next start is clean
        public static void ForgetEyes()
        {
            if (Loaded)
                XrAlt_Forget();
        }

        public static string TakeDetail()
        {
            if (!Loaded)
                return "";
            byte[] text = new byte[4096];
            int length = XrWatch_Detail(text, text.Length);
            return length > 0 ? Encoding.ASCII.GetString(text, 0, length) : "";
        }
    }
}
