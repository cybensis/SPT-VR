using System;
using System.Runtime.InteropServices;
using TarkovVR.Source.Controls;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace TarkovVR.Source.XR
{
    // Finger curls under OpenXR. Unity's OpenXR plugin has no hand tracking, so this feature turns on
    // XR_EXT_hand_tracking and reads the hand joints through the OpenXR calls directly. Each finger's curl is
    // its total joint bend scaled between an open hand and a fist, giving the same 5 values (thumb to pinky)
    // that SteamVR's skeleton action gives, so FingerTracking works unchanged.
    public class OpenXRHandTracking : OpenXRFeature
    {
        public const string REQUIRED_EXTENSIONS = "XR_EXT_hand_tracking XR_EXT_hand_joints_motion_range XR_KHR_win32_convert_performance_counter_time";

        // Total bend in degrees that counts as an open hand (curl 0) and as a fist (curl 1)
        public static float fingerOpenDegrees = 10f;
        public static float fingerFistDegrees = 250f;
        public static float thumbOpenDegrees = 10f;
        public static float thumbFistDegrees = 100f;
        public static bool indexControllersOnly = true;    // SteamVR treated Touch as estimated (baked poses), keep that
        public static bool debugLog = false;               // once a second: bend + curl per finger

        private static OpenXRHandTracking instance;

        // ------------------------------------------------------------------------------------------------------------
        // OpenXR types and values, checked against openxr_reflection_full.h in the OpenXR package

        private const int XR_TYPE_HAND_TRACKER_CREATE_INFO_EXT = 1000051001;
        private const int XR_TYPE_HAND_JOINTS_LOCATE_INFO_EXT = 1000051002;
        private const int XR_TYPE_HAND_JOINT_LOCATIONS_EXT = 1000051003;
        private const int XR_TYPE_HAND_JOINTS_MOTION_RANGE_INFO_EXT = 1000080000;
        private const int XR_HAND_LEFT_EXT = 1;
        private const int XR_HAND_RIGHT_EXT = 2;
        private const int XR_HAND_JOINT_SET_DEFAULT_EXT = 0;
        private const int XR_HAND_JOINTS_MOTION_RANGE_UNOBSTRUCTED_EXT = 1;    // a full grip reads as a fist
        private const int XR_HAND_JOINT_COUNT_EXT = 26;
        public const int WRIST_JOINT = 1;       // XR_HAND_JOINT_WRIST_EXT
        public const int INDEX_TIP_JOINT = 10;  // XR_HAND_JOINT_INDEX_TIP_EXT
        private const ulong XR_SPACE_LOCATION_ORIENTATION_VALID_BIT = 0x1;
        private const int XR_SUCCESS = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct XrHandTrackerCreateInfoEXT { public int type; public IntPtr next; public int hand; public int handJointSet; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrHandJointsLocateInfoEXT { public int type; public IntPtr next; public ulong baseSpace; public long time; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrHandJointsMotionRangeInfoEXT { public int type; public IntPtr next; public int handJointsMotionRange; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrHandJointLocationsEXT { public int type; public IntPtr next; public uint isActive; public uint jointCount; public IntPtr jointLocations; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrQuaternionf { public float x, y, z, w; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrVector3f { public float x, y, z; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrPosef { public XrQuaternionf orientation; public XrVector3f position; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrHandJointLocationEXT { public ulong locationFlags; public XrPosef pose; public float radius; }

        // Joint indices of XR_EXT_hand_tracking per finger, base to tip (the tip joint is left out, it doesn't bend)
        private static readonly int[][] fingerJoints =
        {
            new[] { 2, 3, 4 },          // thumb: metacarpal, proximal, distal
            new[] { 6, 7, 8, 9 },       // index: metacarpal, proximal, intermediate, distal
            new[] { 11, 12, 13, 14 },   // middle
            new[] { 16, 17, 18, 19 },   // ring
            new[] { 21, 22, 23, 24 },   // pinky
        };

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInstanceProcAddrDelegate(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string name, out IntPtr function);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateHandTrackerDelegate(ulong session, ref XrHandTrackerCreateInfoEXT createInfo, out ulong handTracker);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DestroyHandTrackerDelegate(ulong handTracker);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int LocateHandJointsDelegate(ulong handTracker, ref XrHandJointsLocateInfoEXT locateInfo, ref XrHandJointLocationsEXT locations);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ConvertPerformanceCounterToTimeDelegate(ulong instance, ref long performanceCounter, out long time);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long performanceCounter);

        private ulong xrInstance;
        private CreateHandTrackerDelegate xrCreateHandTrackerEXT;
        private DestroyHandTrackerDelegate xrDestroyHandTrackerEXT;
        private LocateHandJointsDelegate xrLocateHandJointsEXT;
        private ConvertPerformanceCounterToTimeDelegate xrConvertWin32PerformanceCounterToTimeKHR;

        // Per hand, indexed by OpenXRInput.LEFT_HAND / OpenXRInput.RIGHT_HAND
        private readonly ulong[] handTrackers = new ulong[2];
        private readonly IntPtr[] jointBuffers = new IntPtr[2];
        private static readonly bool[] handActive = new bool[2];
        private static readonly float[][] fingerCurls = { new float[5], new float[5] };
        private static readonly float[][] fingerBends = { new float[5], new float[5] };

        private static int lastSampledFrame = -1;
        private static float nextDebugLogTime;
        private static bool locateErrorLogged;
        private static bool firstReadLogged;

        // ------------------------------------------------------------------------------------------------------------

        public static bool IsActive(int hand)
        {
            SampleOncePerFrame();
            return handActive[hand];
        }

        public static float[] GetCurls(int hand)
        {
            SampleOncePerFrame();
            return handActive[hand] ? fingerCurls[hand] : null;
        }

        // A joint of SteamVR's hand skeleton in Unity's axes (OpenXR is right handed: z flips, and so do rotations
        // around X and Y), in the same tracking space as the controller poses
        public static unsafe bool TryGetJoint(int hand, int joint, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            SampleOncePerFrame();
            if (!handActive[hand] || instance == null || instance.jointBuffers[hand] == IntPtr.Zero)
                return false;

            XrPosef pose = ((XrHandJointLocationEXT*)instance.jointBuffers[hand])[joint].pose;
            position = new Vector3(pose.position.x, pose.position.y, -pose.position.z);
            rotation = new Quaternion(-pose.orientation.x, -pose.orientation.y, pose.orientation.z, pose.orientation.w);
            return true;
        }

        // ------------------------------------------------------------------------------------------------------------
        // OpenXR lifecycle (called by the OpenXR plugin)

        protected override bool OnInstanceCreate(ulong xrInstanceHandle)
        {
            instance = this;
            xrInstance = xrInstanceHandle;
            GetInstanceProcAddrDelegate getInstanceProcAddr = Marshal.GetDelegateForFunctionPointer<GetInstanceProcAddrDelegate>(xrGetInstanceProcAddr);
            xrCreateHandTrackerEXT = LoadFunction<CreateHandTrackerDelegate>(getInstanceProcAddr, "xrCreateHandTrackerEXT");
            xrDestroyHandTrackerEXT = LoadFunction<DestroyHandTrackerDelegate>(getInstanceProcAddr, "xrDestroyHandTrackerEXT");
            xrLocateHandJointsEXT = LoadFunction<LocateHandJointsDelegate>(getInstanceProcAddr, "xrLocateHandJointsEXT");
            xrConvertWin32PerformanceCounterToTimeKHR = LoadFunction<ConvertPerformanceCounterToTimeDelegate>(getInstanceProcAddr, "xrConvertWin32PerformanceCounterToTimeKHR");
            // Never fail the session over finger tracking
            return true;
        }

        private T LoadFunction<T>(GetInstanceProcAddrDelegate getInstanceProcAddr, string functionName) where T : class
        {
            int result = getInstanceProcAddr(xrInstance, functionName, out IntPtr functionPointer);
            if (result != XR_SUCCESS || functionPointer == IntPtr.Zero)
            {
                Plugin.MyLog.LogWarning($"[OpenXR-Hands] {functionName} unavailable (result {result}), no finger curls");
                return null;
            }
            return Marshal.GetDelegateForFunctionPointer<T>(functionPointer);
        }

        protected override void OnSessionCreate(ulong xrSession)
        {
            if (xrCreateHandTrackerEXT == null || xrLocateHandJointsEXT == null || xrConvertWin32PerformanceCounterToTimeKHR == null)
                return;
            if (!OpenXRRuntime.IsExtensionEnabled("XR_EXT_hand_tracking"))
                return;

            for (int hand = 0; hand < 2; hand++)
            {
                XrHandTrackerCreateInfoEXT createInfo = new XrHandTrackerCreateInfoEXT
                {
                    type = XR_TYPE_HAND_TRACKER_CREATE_INFO_EXT,
                    hand = hand == OpenXRInput.LEFT_HAND ? XR_HAND_LEFT_EXT : XR_HAND_RIGHT_EXT,
                    handJointSet = XR_HAND_JOINT_SET_DEFAULT_EXT,
                };
                int result = xrCreateHandTrackerEXT(xrSession, ref createInfo, out handTrackers[hand]);
                if (result != XR_SUCCESS)
                {
                    Plugin.MyLog.LogWarning($"[OpenXR-Hands] Creating the {HandName(hand)} hand tracker failed: {result}");
                    handTrackers[hand] = 0;
                    continue;
                }
                if (jointBuffers[hand] == IntPtr.Zero)
                    jointBuffers[hand] = Marshal.AllocHGlobal(Marshal.SizeOf<XrHandJointLocationEXT>() * XR_HAND_JOINT_COUNT_EXT);
            }
            Plugin.MyLog.LogInfo($"[OpenXR-Hands] Hand trackers created: left {handTrackers[OpenXRInput.LEFT_HAND] != 0}, right {handTrackers[OpenXRInput.RIGHT_HAND] != 0}");
        }

        protected override void OnSessionDestroy(ulong xrSession)
        {
            for (int hand = 0; hand < 2; hand++)
            {
                if (handTrackers[hand] != 0 && xrDestroyHandTrackerEXT != null)
                    xrDestroyHandTrackerEXT(handTrackers[hand]);
                handTrackers[hand] = 0;
                handActive[hand] = false;
            }
        }

        // ------------------------------------------------------------------------------------------------------------
        // Reading the joints

        private static void SampleOncePerFrame()
        {
            if (lastSampledFrame == Time.frameCount)
                return;
            lastSampledFrame = Time.frameCount;
            if (instance == null)
                return;

            instance.UpdateCurls();

            if (debugLog && Time.unscaledTime >= nextDebugLogTime)
            {
                nextDebugLogTime = Time.unscaledTime + 1f;
                for (int hand = 0; hand < 2; hand++)
                {
                    float[] bends = fingerBends[hand];
                    float[] curls = fingerCurls[hand];
                    Plugin.MyLog.LogInfo($"[OpenXR-Hands] {HandName(hand)} active {handActive[hand]}, "
                        + $"bend degrees {bends[0]:0},{bends[1]:0},{bends[2]:0},{bends[3]:0},{bends[4]:0}, "
                        + $"curls {curls[0]:0.00},{curls[1]:0.00},{curls[2]:0.00},{curls[3]:0.00},{curls[4]:0.00}");
                }
            }
        }

        private unsafe void UpdateCurls()
        {
            handActive[OpenXRInput.LEFT_HAND] = false;
            handActive[OpenXRInput.RIGHT_HAND] = false;

            ulong appSpace = GetCurrentAppSpace();
            if (appSpace == 0 || !QueryPerformanceCounter(out long performanceCounter))
                return;
            if (xrConvertWin32PerformanceCounterToTimeKHR(xrInstance, ref performanceCounter, out long currentTime) != XR_SUCCESS)
                return;

            for (int hand = 0; hand < 2; hand++)
            {
                if (handTrackers[hand] == 0)
                    continue;
                if (indexControllersOnly && !OpenXRInput.IsIndex(hand))
                    continue;

                XrHandJointsMotionRangeInfoEXT motionRange = new XrHandJointsMotionRangeInfoEXT
                {
                    type = XR_TYPE_HAND_JOINTS_MOTION_RANGE_INFO_EXT,
                    handJointsMotionRange = XR_HAND_JOINTS_MOTION_RANGE_UNOBSTRUCTED_EXT,
                };
                XrHandJointsLocateInfoEXT locateInfo = new XrHandJointsLocateInfoEXT
                {
                    type = XR_TYPE_HAND_JOINTS_LOCATE_INFO_EXT,
                    next = (IntPtr)(&motionRange),
                    baseSpace = appSpace,
                    time = currentTime,
                };
                XrHandJointLocationsEXT locations = new XrHandJointLocationsEXT
                {
                    type = XR_TYPE_HAND_JOINT_LOCATIONS_EXT,
                    jointCount = XR_HAND_JOINT_COUNT_EXT,
                    jointLocations = jointBuffers[hand],
                };

                int result = xrLocateHandJointsEXT(handTrackers[hand], ref locateInfo, ref locations);
                if (result != XR_SUCCESS)
                {
                    if (!locateErrorLogged)
                    {
                        locateErrorLogged = true;
                        Plugin.MyLog.LogWarning($"[OpenXR-Hands] Reading the hand joints failed: {result}");
                    }
                    continue;
                }
                if (!firstReadLogged)
                {
                    firstReadLogged = true;
                    Plugin.MyLog.LogInfo($"[OpenXR-Hands] First joint read OK ({HandName(hand)}), active {locations.isActive != 0}");
                }
                if (locations.isActive == 0)
                    continue;

                XrHandJointLocationEXT* joints = (XrHandJointLocationEXT*)jointBuffers[hand];
                if (TryUpdateHand(joints, fingerBends[hand], fingerCurls[hand]))
                    handActive[hand] = true;
            }
        }

        private static unsafe bool TryUpdateHand(XrHandJointLocationEXT* joints, float[] bends, float[] curls)
        {
            for (int finger = 0; finger < 5; finger++)
            {
                if (!TryGetFingerBend(joints, fingerJoints[finger], out float bendDegrees))
                    return false;
                bends[finger] = bendDegrees;

                bool isThumb = finger == 0;
                float openDegrees = isThumb ? thumbOpenDegrees : fingerOpenDegrees;
                float fistDegrees = isThumb ? thumbFistDegrees : fingerFistDegrees;
                curls[finger] = Mathf.Clamp01((bendDegrees - openDegrees) / Mathf.Max(1f, fistDegrees - openDegrees));
            }
            return true;
        }

        // Sum of the angles between each bone and the next one along the finger. An angle doesn't depend on
        // handedness, so the raw OpenXR rotations are used as they are.
        private static unsafe bool TryGetFingerBend(XrHandJointLocationEXT* joints, int[] jointIndices, out float bendDegrees)
        {
            bendDegrees = 0f;
            for (int i = 0; i < jointIndices.Length - 1; i++)
            {
                XrHandJointLocationEXT baseJoint = joints[jointIndices[i]];
                XrHandJointLocationEXT nextJoint = joints[jointIndices[i + 1]];
                if ((baseJoint.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) == 0
                    || (nextJoint.locationFlags & XR_SPACE_LOCATION_ORIENTATION_VALID_BIT) == 0)
                    return false;
                bendDegrees += Quaternion.Angle(ToQuaternion(baseJoint.pose.orientation), ToQuaternion(nextJoint.pose.orientation));
            }
            return true;
        }

        private static Quaternion ToQuaternion(XrQuaternionf orientation)
        {
            return new Quaternion(orientation.x, orientation.y, orientation.z, orientation.w);
        }

        private static string HandName(int hand)
        {
            return hand == OpenXRInput.LEFT_HAND ? "left" : "right";
        }
    }
}
