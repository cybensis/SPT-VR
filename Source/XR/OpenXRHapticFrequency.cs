using System;
using System.Runtime.InteropServices;
using TarkovVR.Source.Controls;
using UnityEngine.XR.OpenXR.Features;

namespace TarkovVR.Source.XR
{
    // Haptic frequency under OpenXR. Unity's legacy XR haptics (InputDevice.SendHapticImpulse) take no frequency, so its OpenXR plugin
    // calls xrApplyHapticFeedback with the frequency unspecified and every vibration feels the same. This feature hooks that call through
    // the function lookup that Unity lets every feature hook, and fills in the frequency OpenXRInput set for that hand.
    public class OpenXRHapticFrequency : OpenXRFeature
    {

        // ------------------------------------------------------------------------------------------------------------
        // OpenXR types and values, checked against openxr_reflection_full.h in the OpenXR package

        private const int XR_TYPE_HAPTIC_VIBRATION = 13;
        private const int XR_SUCCESS = 0;
        private const ulong XR_NULL_PATH = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct XrHapticActionInfo { public int type; public IntPtr next; public ulong action; public ulong subactionPath; }

        [StructLayout(LayoutKind.Sequential)]
        private struct XrHapticVibration { public int type; public IntPtr next; public long duration; public float frequency; public float amplitude; }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ApplyHapticFeedbackDelegate(ulong session, IntPtr hapticActionInfo, IntPtr hapticFeedback);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StringToPathDelegate(ulong instance, [MarshalAs(UnmanagedType.LPStr)] string pathString, out ulong path);

        // Native code holds pointers to these delegates, so they must never be collected. The lookup's type is the shared one (OpenXRFunctions).
        private static readonly OpenXRFunctions.GetInstanceProcAddrDelegate ourGetInstanceProcAddr = LookUpFunction;
        private static readonly ApplyHapticFeedbackDelegate ourApplyHapticFeedback = ApplyHapticFeedback;
        private static OpenXRFunctions.GetInstanceProcAddrDelegate nextGetInstanceProcAddr;
        private static ApplyHapticFeedbackDelegate runtimeApplyHapticFeedback;

        // Per hand, indexed by OpenXRInput.LEFT_HAND / OpenXRInput.RIGHT_HAND
        private static readonly ulong[] handPaths = new ulong[2];
        private static readonly float[] handFrequencies = new float[2];
        private static float lastFrequency;

        // Hz for the next vibration on this hand; 0 = the runtime's default
        public static void SetFrequency(int hand, float frequency)
        {
            handFrequencies[hand] = frequency;
            lastFrequency = frequency;
        }

        // ------------------------------------------------------------------------------------------------------------

        protected override IntPtr HookGetInstanceProcAddr(IntPtr func)
        {
            nextGetInstanceProcAddr = Marshal.GetDelegateForFunctionPointer<OpenXRFunctions.GetInstanceProcAddrDelegate>(func);
            return Marshal.GetFunctionPointerForDelegate(ourGetInstanceProcAddr);
        }

        protected override bool OnInstanceCreate(ulong xrInstanceHandle)
        {
            // Never fail the session over haptics: an exception here stops the whole OpenXR start. Without the hand paths,
            // each vibration uses the last frequency set.
            try
            {
                // The two hands' paths, to tell which hand a vibration is for
                IntPtr functionPointer = OpenXRFunctions.Find(xrGetInstanceProcAddr, xrInstanceHandle, "xrStringToPath", out _);
                if (functionPointer != IntPtr.Zero)
                {
                    StringToPathDelegate stringToPath = Marshal.GetDelegateForFunctionPointer<StringToPathDelegate>(functionPointer);
                    stringToPath(xrInstanceHandle, "/user/hand/left", out handPaths[OpenXRInput.LEFT_HAND]);
                    stringToPath(xrInstanceHandle, "/user/hand/right", out handPaths[OpenXRInput.RIGHT_HAND]);
                }
                Plugin.MyLog.LogInfo($"[OpenXR-Haptics] frequency hook {(runtimeApplyHapticFeedback != null ? "in place" : "NOT in place yet")}, hand paths {handPaths[0]}/{handPaths[1]}");
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[OpenXR-Haptics] hand path lookup failed: {exception}");
            }
            return true;
        }

        // Every OpenXR function Unity looks up comes through here, and xrApplyHapticFeedback is answered with ours. This is called from
        // native code, where an exception would crash the game, so on any error the runtime's own function is kept.
        private static int LookUpFunction(ulong instance, IntPtr name, IntPtr function)
        {
            int result = nextGetInstanceProcAddr(instance, name, function);
            try
            {
                if (result != XR_SUCCESS || function == IntPtr.Zero || Marshal.PtrToStringAnsi(name) != "xrApplyHapticFeedback")
                    return result;
                IntPtr runtimeFunction = Marshal.ReadIntPtr(function);
                if (runtimeFunction == IntPtr.Zero || runtimeFunction == Marshal.GetFunctionPointerForDelegate(ourApplyHapticFeedback))
                    return result;
                runtimeApplyHapticFeedback = Marshal.GetDelegateForFunctionPointer<ApplyHapticFeedbackDelegate>(runtimeFunction);
                Marshal.WriteIntPtr(function, Marshal.GetFunctionPointerForDelegate(ourApplyHapticFeedback));
            }
            catch (Exception)
            {
            }
            return result;
        }

        // Sends a copy of Unity's vibration with our frequency in it; the original struct belongs to the caller and must not be written
        private static int ApplyHapticFeedback(ulong session, IntPtr hapticActionInfo, IntPtr hapticFeedback)
        {
            try
            {
                return ApplyWithOurFrequency(session, hapticActionInfo, hapticFeedback);
            }
            catch (Exception)
            {
                return runtimeApplyHapticFeedback(session, hapticActionInfo, hapticFeedback);
            }
        }

        private static unsafe int ApplyWithOurFrequency(ulong session, IntPtr hapticActionInfo, IntPtr hapticFeedback)
        {
            if (hapticActionInfo == IntPtr.Zero || hapticFeedback == IntPtr.Zero
                || ((XrHapticVibration*)hapticFeedback)->type != XR_TYPE_HAPTIC_VIBRATION)
                return runtimeApplyHapticFeedback(session, hapticActionInfo, hapticFeedback);

            // The hand comes from the action's path. Without one, use the last frequency set (OpenXRInput sends each hand right after setting it).
            ulong path = ((XrHapticActionInfo*)hapticActionInfo)->subactionPath;
            float frequency = path != XR_NULL_PATH && path == handPaths[OpenXRInput.LEFT_HAND] ? handFrequencies[OpenXRInput.LEFT_HAND]
                : path != XR_NULL_PATH && path == handPaths[OpenXRInput.RIGHT_HAND] ? handFrequencies[OpenXRInput.RIGHT_HAND]
                : lastFrequency;
            if (frequency <= 0f)
                return runtimeApplyHapticFeedback(session, hapticActionInfo, hapticFeedback);

            XrHapticVibration vibration = *(XrHapticVibration*)hapticFeedback;
            vibration.frequency = frequency;
            return runtimeApplyHapticFeedback(session, hapticActionInfo, (IntPtr)(&vibration));
        }
    }
}
