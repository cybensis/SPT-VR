using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine.XR.OpenXR.Features;

namespace TarkovVR.Source.XR
{
    // A space Unity lets go of while the session runs (OpenXRFloorOrigin's request rebuilds the tracking space) is destroyed
    // keepSeconds later, not at once. An OpenXR layer that queues frames (OFXR-Bridge's frame generation) can still hold a frame naming
    // it: the runtime rejects that frame, the layer latches the error for every later frame call, and Unity shuts XR down (black
    // headset). Hooked through the function lookup Unity lets every feature hook. Spaces still kept when the session ends go then.
    public class OpenXRLateSpaceDestroy : OpenXRFeature
    {
        public static bool keepSpaces = true;           // false = destroyed when Unity asks
        public static double keepSeconds = 1.0;

        private const int XR_SUCCESS = 0;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DestroySpaceDelegate(ulong space);

        // Native code holds pointers to these, so they're kept for good
        private static readonly OpenXRFunctions.GetInstanceProcAddrDelegate ourGetInstanceProcAddr = LookUpFunction;
        private static readonly DestroySpaceDelegate ourDestroySpace = DestroySpace;
        private static OpenXRFunctions.GetInstanceProcAddrDelegate nextGetInstanceProcAddr;
        private static DestroySpaceDelegate runtimeDestroySpace;

        private struct KeptSpace
        {
            public ulong space;
            public long releasedAt;                     // Stopwatch ticks
        }
        private static readonly List<KeptSpace> kept = new List<KeptSpace>();
        private static readonly object keptLock = new object();
        private static bool sessionRunning;
        private static bool loggedOnce;

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            nextGetInstanceProcAddr = Marshal.GetDelegateForFunctionPointer<OpenXRFunctions.GetInstanceProcAddrDelegate>(func);
            return Marshal.GetFunctionPointerForDelegate(ourGetInstanceProcAddr);
        }

        protected override void OnSessionBegin(ulong xrSession)
        {
            sessionRunning = true;
        }

        // The spaces are still valid until the session is destroyed: the kept ones go now
        protected override void OnSessionEnd(ulong xrSession)
        {
            sessionRunning = false;
            DestroyKept(all: true);
        }

        // Anything left went with the session
        protected override void OnSessionDestroy(ulong xrSession)
        {
            sessionRunning = false;
            lock (keptLock)
                kept.Clear();
        }

        // Every OpenXR function Unity looks up comes through here: xrDestroySpace is answered with ours. Called from native code, where
        // an exception would take the game down: anything going wrong leaves the runtime's own answer.
        private static int LookUpFunction(ulong instance, IntPtr name, IntPtr function)
        {
            int result = nextGetInstanceProcAddr(instance, name, function);
            try
            {
                if (result != XR_SUCCESS || function == IntPtr.Zero || Marshal.PtrToStringAnsi(name) != "xrDestroySpace")
                    return result;
                IntPtr runtimeFunction = Marshal.ReadIntPtr(function);
                if (runtimeFunction == IntPtr.Zero || runtimeFunction == Marshal.GetFunctionPointerForDelegate(ourDestroySpace))
                    return result;
                runtimeDestroySpace = Marshal.GetDelegateForFunctionPointer<DestroySpaceDelegate>(runtimeFunction);
                Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(ourDestroySpace));
            }
            catch (Exception)
            {
            }
            return result;
        }

        private static int DestroySpace(ulong space)
        {
            try
            {
                if (!keepSpaces || !sessionRunning || space == 0)
                    return runtimeDestroySpace(space);
                lock (keptLock)
                    kept.Add(new KeptSpace { space = space, releasedAt = Stopwatch.GetTimestamp() });
                if (!loggedOnce)
                {
                    loggedOnce = true;
                    Plugin.MyLog.LogInfo($"[OpenXR] A space let go of while the session runs is destroyed {keepSeconds:0.#} s later");
                }
                return XR_SUCCESS;
            }
            catch (Exception)
            {
                return runtimeDestroySpace(space);
            }
        }

        // Each frame (OpenXRFloorOrigin.Update): the spaces kept long enough go
        internal static void DestroyDue() => DestroyKept(all: false);

        private static void DestroyKept(bool all)
        {
            if (runtimeDestroySpace == null)
                return;
            long now = Stopwatch.GetTimestamp();
            long keepTicks = (long)(keepSeconds * Stopwatch.Frequency);
            List<ulong> due = null;
            lock (keptLock)
            {
                if (kept.Count == 0)
                    return;
                for (int i = kept.Count - 1; i >= 0; i--)
                {
                    if (all || now - kept[i].releasedAt >= keepTicks)
                    {
                        (due ??= new List<ulong>()).Add(kept[i].space);
                        kept.RemoveAt(i);
                    }
                }
            }
            if (due == null)
                return;
            foreach (ulong space in due)
            {
                try
                {
                    runtimeDestroySpace(space);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
