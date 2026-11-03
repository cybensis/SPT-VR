using System;
using System.Runtime.InteropServices;
using TarkovVR.Source.Controls;
using UnityEngine.XR.OpenXR.Features;

namespace TarkovVR.Source.XR
{
    // Haptic frequency under OpenXR. Unity's legacy XR haptics (InputDevice.SendHapticImpulse) take no frequency, so its OpenXR plugin
    // calls xrApplyHapticFeedback with the frequency left unspecified and every buzz comes out the same (the sound-driven textures in
    // AudioHaptics flatten into one rumble). OpenXR's own call does take a frequency: this feature puts itself in front of it, through
    // the function lookup Unity lets every feature hook, and fills in the frequency OpenXRInput asked for on that hand.
    public class OpenXRHapticFrequency : OpenXRFeature
    {
        public static bool passFrequency = true;           // false = the runtime's own frequency, as before

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

        // Native code holds pointers to these, so they're kept for good. The lookup's type is the shared one (OpenXRFunctions).
        private static readonly OpenXRFunctions.GetInstanceProcAddrDelegate ourGetInstanceProcAddr = LookUpFunction;
        private static readonly ApplyHapticFeedbackDelegate ourApplyHapticFeedback = ApplyHapticFeedback;
        private static OpenXRFunctions.GetInstanceProcAddrDelegate nextGetInstanceProcAddr;
        private static ApplyHapticFeedbackDelegate runtimeApplyHapticFeedback;

        // Per hand, indexed by OpenXRInput.LEFT_HAND / OpenXRInput.RIGHT_HAND
        private static readonly ulong[] handPaths = new ulong[2];
        private static readonly float[] handFrequencies = new float[2];
        private static float lastFrequency;
        private static bool firstVibrationLogged;

        // Hz the next vibration on this hand goes out at; 0 = the runtime's own
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
            // Never fail the session over haptics: an exception here stops the whole OpenXR start. Without the hand paths
            // each vibration takes the last frequency set.
            try
            {
                // The two hands' paths, to tell which one a vibration is for
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

        // Every OpenXR function Unity looks up comes through here: xrApplyHapticFeedback is answered with ours. Called from native code,
        // where an exception would take the game down: anything going wrong leaves the runtime's own answer.
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

        // A copy of Unity's vibration with our frequency in it: theirs is the caller's, not ours to write
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
            if (!passFrequency || hapticActionInfo == IntPtr.Zero || hapticFeedback == IntPtr.Zero
                || ((XrHapticVibration*)hapticFeedback)->type != XR_TYPE_HAPTIC_VIBRATION)
                return runtimeApplyHapticFeedback(session, hapticActionInfo, hapticFeedback);

            // The hand from the action's path; without one, the last asked for (OpenXRInput sends each hand right after setting it)
            ulong path = ((XrHapticActionInfo*)hapticActionInfo)->subactionPath;
            float frequency = path != XR_NULL_PATH && path == handPaths[OpenXRInput.LEFT_HAND] ? handFrequencies[OpenXRInput.LEFT_HAND]
                : path != XR_NULL_PATH && path == handPaths[OpenXRInput.RIGHT_HAND] ? handFrequencies[OpenXRInput.RIGHT_HAND]
                : lastFrequency;
            if (!firstVibrationLogged)
            {
                firstVibrationLogged = true;
                Plugin.MyLog.LogInfo($"[OpenXR-Haptics] first vibration: path {path}, Unity's frequency {((XrHapticVibration*)hapticFeedback)->frequency}, ours {frequency}");
            }
            if (frequency <= 0f)
                return runtimeApplyHapticFeedback(session, hapticActionInfo, hapticFeedback);

            XrHapticVibration vibration = *(XrHapticVibration*)hapticFeedback;
            vibration.frequency = frequency;
            return runtimeApplyHapticFeedback(session, hapticActionInfo, (IntPtr)(&vibration));
        }
    }
}
