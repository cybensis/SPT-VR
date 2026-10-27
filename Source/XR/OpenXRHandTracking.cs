using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace TarkovVR.Source.XR
{
    // Finger curls under OpenXR. Unity's OpenXR plugin has no hand tracking, so this feature turns on
    // XR_EXT_hand_tracking itself and reads both hands' 26 joints through the raw OpenXR calls. Each finger's curl
    // = its summed joint bend / a full-fist bend (0 open .. 1 fist) - the same 5 numbers SteamVR's skeleton action
    // gave (thumb, index, middle, ring, pinky), so FingerTracking runs unchanged. UNOBSTRUCTED motion range: a full
    // grip on the handle reads as a fist, like SteamVR's curls. Added to OpenXRSettings by OpenXRBootstrap.
    public class OpenXRHandTracking : OpenXRFeature
    {
        public const string Extensions = "XR_EXT_hand_tracking XR_EXT_hand_joints_motion_range XR_KHR_win32_convert_performance_counter_time";

        // Bend (degrees, summed over a finger's joints) that reads as curl 1, and the natural bend of a relaxed
        // open hand that reads as 0. Live knobs; calibrate with debugLog (prints the raw bends once a second).
        public static float fingerFistDeg = 250f;
        public static float thumbFistDeg = 100f;
        public static float fingerOpenDeg = 10f;
        public static float thumbOpenDeg = 10f;
        // SteamVR reported Touch controllers as "estimated" (baked poses); keep that unless asked otherwise.
        public static bool indexOnly = true;
        public static bool debugLog = false;

        private static OpenXRHandTracking instance;

        // ---------------------------------------------------------------------------------------- OpenXR types

        // From openxr_reflection (the OpenXR plugin's copy): 1000051000 is SYSTEM_HAND_TRACKING_PROPERTIES - the
        // first build was off by one here and every locate failed with XR_ERROR_VALIDATION_FAILURE (-1).
        private const int XR_TYPE_HAND_TRACKER_CREATE_INFO_EXT = 1000051001;
        private const int XR_TYPE_HAND_JOINTS_LOCATE_INFO_EXT = 1000051002;
        private const int XR_TYPE_HAND_JOINT_LOCATIONS_EXT = 1000051003;
        private const int XR_TYPE_HAND_JOINTS_MOTION_RANGE_INFO_EXT = 1000080000;
        private const int XR_HAND_JOINTS_MOTION_RANGE_UNOBSTRUCTED_EXT = 1;
        private const int JointCount = 26;
        private const ulong OrientationValid = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct HandTrackerCreateInfo { public int type; public IntPtr next; public int hand; public int handJointSet; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JointsLocateInfo { public int type; public IntPtr next; public ulong baseSpace; public long time; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MotionRangeInfo { public int type; public IntPtr next; public int range; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JointLocations { public int type; public IntPtr next; public uint isActive; public uint jointCount; public IntPtr joints; }
        [StructLayout(LayoutKind.Sequential)]
        private struct JointLocation { public ulong flags; public float qx, qy, qz, qw, px, py, pz; public float radius; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetProcAddrFn(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateHandTrackerFn(ulong session, ref HandTrackerCreateInfo info, out ulong tracker);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DestroyHandTrackerFn(ulong tracker);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LocateHandJointsFn(ulong tracker, ref JointsLocateInfo info, ref JointLocations locations);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ConvertPerfCounterFn(ulong instance, ref long performanceCounter, out long time);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long count);

        private ulong xrInstance;
        private CreateHandTrackerFn createHandTracker;
        private DestroyHandTrackerFn destroyHandTracker;
        private LocateHandJointsFn locateHandJoints;
        private ConvertPerfCounterFn convertPerfCounter;

        private readonly ulong[] trackers = new ulong[2];          // 0 = left, 1 = right
        private readonly IntPtr[] jointBuffers = new IntPtr[2];
        private static readonly bool[] active = new bool[2];
        private static readonly float[][] curls = { new float[5], new float[5] };
        private static readonly float[][] bends = { new float[5], new float[5] };
        private static int sampledFrame = -1;
        private static float nextLog;
        private static bool errorLogged;
        private static bool firstOk;

        // ---------------------------------------------------------------------------------------- public reads

        public static bool Active(int hand) { Sample(); return active[hand]; }
        public static float[] Curls(int hand) { Sample(); return active[hand] ? curls[hand] : null; }

        // ---------------------------------------------------------------------------------------- lifecycle

        protected override bool OnInstanceCreate(ulong xrInstanceHandle)
        {
            instance = this;
            xrInstance = xrInstanceHandle;
            var getProc = Marshal.GetDelegateForFunctionPointer<GetProcAddrFn>(xrGetInstanceProcAddr);
            createHandTracker = Load<CreateHandTrackerFn>(getProc, "xrCreateHandTrackerEXT");
            destroyHandTracker = Load<DestroyHandTrackerFn>(getProc, "xrDestroyHandTrackerEXT");
            locateHandJoints = Load<LocateHandJointsFn>(getProc, "xrLocateHandJointsEXT");
            convertPerfCounter = Load<ConvertPerfCounterFn>(getProc, "xrConvertWin32PerformanceCounterToTimeKHR");
            return true;    // never fail the session over finger tracking
        }

        private T Load<T>(GetProcAddrFn getProc, string name) where T : class
        {
            int r = getProc(xrInstance, name, out IntPtr fn);
            if (r != 0 || fn == IntPtr.Zero)
            {
                Plugin.MyLog.LogWarning($"[OpenXR-Hands] {name} unavailable (result {r}) - no finger curls");
                return null;
            }
            return Marshal.GetDelegateForFunctionPointer<T>(fn);
        }

        protected override void OnSessionCreate(ulong xrSession)
        {
            if (createHandTracker == null || locateHandJoints == null || convertPerfCounter == null
                || !OpenXRRuntime.IsExtensionEnabled("XR_EXT_hand_tracking"))
                return;
            for (int h = 0; h < 2; h++)
            {
                var info = new HandTrackerCreateInfo { type = XR_TYPE_HAND_TRACKER_CREATE_INFO_EXT, hand = h + 1, handJointSet = 0 };
                int r = createHandTracker(xrSession, ref info, out trackers[h]);
                if (r != 0)
                {
                    Plugin.MyLog.LogWarning($"[OpenXR-Hands] xrCreateHandTrackerEXT ({(h == 0 ? "left" : "right")}) failed: {r}");
                    trackers[h] = 0;
                    continue;
                }
                if (jointBuffers[h] == IntPtr.Zero)
                    jointBuffers[h] = Marshal.AllocHGlobal(Marshal.SizeOf<JointLocation>() * JointCount);
            }
            Plugin.MyLog.LogInfo($"[OpenXR-Hands] Hand trackers created: left {trackers[0] != 0}, right {trackers[1] != 0}");
        }

        protected override void OnSessionDestroy(ulong xrSession)
        {
            for (int h = 0; h < 2; h++)
            {
                if (trackers[h] != 0 && destroyHandTracker != null)
                    destroyHandTracker(trackers[h]);
                trackers[h] = 0;
                active[h] = false;
            }
        }

        // ---------------------------------------------------------------------------------------- sampling

        private static void Sample()
        {
            if (sampledFrame == Time.frameCount)
                return;
            sampledFrame = Time.frameCount;
            if (instance == null)
                return;
            instance.Locate();
            if (debugLog && Time.unscaledTime >= nextLog)
            {
                nextLog = Time.unscaledTime + 1f;
                for (int h = 0; h < 2; h++)
                    Plugin.MyLog.LogInfo($"[OpenXR-Hands] {(h == 0 ? "L" : "R")} active {active[h]} bend deg "
                        + $"{bends[h][0]:0},{bends[h][1]:0},{bends[h][2]:0},{bends[h][3]:0},{bends[h][4]:0} curls "
                        + $"{curls[h][0]:0.00},{curls[h][1]:0.00},{curls[h][2]:0.00},{curls[h][3]:0.00},{curls[h][4]:0.00}");
            }
        }

        private unsafe void Locate()
        {
            ulong space = GetCurrentAppSpace();
            if (space == 0 || !QueryPerformanceCounter(out long counter) || convertPerfCounter(xrInstance, ref counter, out long now) != 0)
            {
                active[0] = active[1] = false;
                return;
            }
            for (int h = 0; h < 2; h++)
            {
                active[h] = false;
                if (trackers[h] == 0)
                    continue;
                if (indexOnly && !TarkovVR.Source.Controls.OpenXRInput.IsIndex(h))
                    continue;

                var range = new MotionRangeInfo { type = XR_TYPE_HAND_JOINTS_MOTION_RANGE_INFO_EXT, range = XR_HAND_JOINTS_MOTION_RANGE_UNOBSTRUCTED_EXT };
                var info = new JointsLocateInfo { type = XR_TYPE_HAND_JOINTS_LOCATE_INFO_EXT, next = (IntPtr)(&range), baseSpace = space, time = now };
                var locs = new JointLocations { type = XR_TYPE_HAND_JOINT_LOCATIONS_EXT, jointCount = JointCount, joints = jointBuffers[h] };
                int r = locateHandJoints(trackers[h], ref info, ref locs);
                if (r != 0)
                {
                    if (!errorLogged) { errorLogged = true; Plugin.MyLog.LogWarning($"[OpenXR-Hands] xrLocateHandJointsEXT failed: {r}"); }
                    continue;
                }
                if (!firstOk) { firstOk = true; Plugin.MyLog.LogInfo($"[OpenXR-Hands] First joint read OK ({(h == 0 ? "left" : "right")}), active {locs.isActive != 0}"); }
                if (locs.isActive == 0)
                    continue;

                JointLocation* j = (JointLocation*)jointBuffers[h];
                // XR_EXT_hand_tracking joints: 2-5 thumb (metacarpal, proximal, distal, tip), then index / middle /
                // ring / little at 6, 11, 16, 21 (metacarpal, proximal, intermediate, distal, tip).
                bool ok = Bend(j, 2, 3, out float b0) & Bend(j, 3, 4, out float b1);
                bends[h][0] = b0 + b1;
                for (int f = 1; f < 5 && ok; f++)
                {
                    int m = 6 + (f - 1) * 5;
                    ok &= Bend(j, m, m + 1, out float a) & Bend(j, m + 1, m + 2, out float b) & Bend(j, m + 2, m + 3, out float c);
                    bends[h][f] = a + b + c;
                }
                if (!ok)
                    continue;

                curls[h][0] = Mathf.Clamp01((bends[h][0] - thumbOpenDeg) / Mathf.Max(1f, thumbFistDeg - thumbOpenDeg));
                for (int f = 1; f < 5; f++)
                    curls[h][f] = Mathf.Clamp01((bends[h][f] - fingerOpenDeg) / Mathf.Max(1f, fingerFistDeg - fingerOpenDeg));
                active[h] = true;
            }
        }

        // Angle between two adjacent bones' orientations (handedness doesn't change an angle, so raw OpenXR values).
        private static unsafe bool Bend(JointLocation* j, int a, int b, out float degrees)
        {
            degrees = 0f;
            if ((j[a].flags & OrientationValid) == 0 || (j[b].flags & OrientationValid) == 0)
                return false;
            var qa = new Quaternion(j[a].qx, j[a].qy, j[a].qz, j[a].qw);
            var qb = new Quaternion(j[b].qx, j[b].qy, j[b].qz, j[b].qw);
            degrees = Quaternion.Angle(qa, qb);
            return true;
        }
    }
}
