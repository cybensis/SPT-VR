using System.Collections.Generic;
using TarkovVR.Source.XR;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // The OpenXR side of VRInput. Reads both controllers through Unity's XR input API, converts OpenXR's grip pose
    // back into SteamVR's raw pose (what all the hand offsets were tuned on), and runs the hand pose listeners from
    // onBeforeRender, which is where SteamVR runs them.
    public static class OpenXRInput
    {
        public const int LEFT_HAND = 0;
        public const int RIGHT_HAND = 1;

        public enum Button { None, Grip, Primary, Secondary, StickClick, StickClickOrTrackpadTouch }
        public enum ButtonState { Held, PressedThisFrame, ReleasedThisFrame }

        // The grip "button" comes from the analog squeeze value (on Index: how far your fingers wrap the handle)
        public static float gripPressThreshold = 0.5f;
        public static float gripReleaseThreshold = 0.25f;

        // Extra correction on top of the grip -> raw conversion, in the raw pose's own space
        public static Vector3 rawPosePositionOffset = Vector3.zero;
        public static Vector3 rawPoseRotationOffset = Vector3.zero;

        // Where SteamVR's OpenXR grip pose sits relative to the raw pose: the "grip" component in each controller's
        // SteamVR render model file. OpenVR coordinates (right handed), meters and degrees.
        private struct GripPoseOffset
        {
            public Vector3 position;
            public Vector3 rotation;

            public GripPoseOffset(Vector3 position, Vector3 rotation)
            {
                this.position = position;
                this.rotation = rotation;
            }
        }
        private static readonly GripPoseOffset indexLeftGripPose = new GripPoseOffset(new Vector3(0f, -0.015f, 0.13f), new Vector3(15.392f, -2.071f, 0.303f));
        private static readonly GripPoseOffset indexRightGripPose = new GripPoseOffset(new Vector3(0f, -0.015f, 0.13f), new Vector3(15.392f, 2.071f, -0.303f));
        private static readonly GripPoseOffset touchGripPose = new GripPoseOffset(new Vector3(0f, 0.003f, 0.097f), new Vector3(5.037f, 0f, 0f));
        private static readonly GripPoseOffset viveWandGripPose = new GripPoseOffset(new Vector3(0f, -0.015f, 0.097f), new Vector3(5.037f, 0f, 0f));

        private struct ButtonStates
        {
            public bool grip;
            public bool primary;
            public bool secondary;
            public bool stickClick;
            public bool trackpadTouch;
        }

        private class ControllerState
        {
            public XRNode node;
            public InputDevice device;
            public string deviceName;

            // Converts the grip pose to the raw pose, set when the controller connects
            public Vector3 gripToRawPosition;
            public Quaternion gripToRawRotation = Quaternion.identity;

            public ButtonStates current;
            public ButtonStates previous;
            public float triggerValue;
            public float squeezeValue;
            public Vector2 stickAxis;

            public Vector3 rawPosition;
            public Quaternion rawRotation = Quaternion.identity;
            public Vector3 rawVelocity;
            public Vector3 angularVelocity;
        }

        private static readonly ControllerState[] controllers =
        {
            new ControllerState { node = XRNode.LeftHand },
            new ControllerState { node = XRNode.RightHand },
        };

        private static int lastButtonSampleFrame = -1;
        private static int lastPoseSampleFrame = -1;
        private static bool hapticsSupportLogged = false;

        private struct QueuedHaptic
        {
            public float fireTime;
            public float duration;
            public float frequency;
            public float amplitude;
            public SteamVR_Input_Sources inputSource;
        }
        private static readonly List<QueuedHaptic> queuedHaptics = new List<QueuedHaptic>();

        public static bool IsSourceForHand(SteamVR_Input_Sources inputSource, int hand)
        {
            if (inputSource == SteamVR_Input_Sources.Any)
                return true;
            if (inputSource == SteamVR_Input_Sources.LeftHand)
                return hand == LEFT_HAND;
            if (inputSource == SteamVR_Input_Sources.RightHand)
                return hand == RIGHT_HAND;
            return false;
        }

        public static bool IsIndex(int hand)
        {
            string deviceName = controllers[hand].deviceName;
            return deviceName != null && deviceName.ToLowerInvariant().Contains("index");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Buttons and axes

        public static bool GetButton(int hand, Button button, ButtonState state)
        {
            SampleButtonsOncePerFrame();
            ControllerState controller = controllers[hand];
            bool isDown = IsButtonDown(controller.current, button);
            bool wasDown = IsButtonDown(controller.previous, button);

            if (state == ButtonState.PressedThisFrame)
                return isDown && !wasDown;
            if (state == ButtonState.ReleasedThisFrame)
                return !isDown && wasDown;
            return isDown;
        }

        private static bool IsButtonDown(ButtonStates buttons, Button button)
        {
            switch (button)
            {
                case Button.Grip: return buttons.grip;
                case Button.Primary: return buttons.primary;
                case Button.Secondary: return buttons.secondary;
                case Button.StickClick: return buttons.stickClick;
                case Button.StickClickOrTrackpadTouch: return buttons.stickClick || buttons.trackpadTouch;
                default: return false;
            }
        }

        public static float GetTrigger(int hand)
        {
            SampleButtonsOncePerFrame();
            return controllers[hand].triggerValue;
        }

        public static Vector2 GetStick(int hand)
        {
            SampleButtonsOncePerFrame();
            return controllers[hand].stickAxis;
        }

        // All buttons move to the new frame together, on the first read of the frame or in Update, like SteamVR
        internal static void SampleButtonsOncePerFrame()
        {
            if (lastButtonSampleFrame == Time.frameCount)
                return;
            lastButtonSampleFrame = Time.frameCount;

            foreach (ControllerState controller in controllers)
            {
                controller.previous = controller.current;
                if (!TryGetDevice(controller))
                {
                    controller.current = new ButtonStates();
                    controller.triggerValue = 0f;
                    controller.squeezeValue = 0f;
                    controller.stickAxis = Vector2.zero;
                    continue;
                }

                InputDevice device = controller.device;
                device.TryGetFeatureValue(CommonUsages.trigger, out controller.triggerValue);
                device.TryGetFeatureValue(CommonUsages.grip, out controller.squeezeValue);
                device.TryGetFeatureValue(CommonUsages.primary2DAxis, out controller.stickAxis);
                device.TryGetFeatureValue(CommonUsages.primaryButton, out controller.current.primary);
                device.TryGetFeatureValue(CommonUsages.secondaryButton, out controller.current.secondary);
                device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out controller.current.stickClick);
                device.TryGetFeatureValue(CommonUsages.secondary2DAxisTouch, out controller.current.trackpadTouch);

                // Separate press and release points so the grip doesn't flicker around one value
                if (controller.previous.grip)
                    controller.current.grip = controller.squeezeValue > gripReleaseThreshold;
                else
                    controller.current.grip = controller.squeezeValue >= gripPressThreshold;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Poses

        public static Vector3 GetRawPosition(int hand) => controllers[hand].rawPosition;
        public static Quaternion GetRawRotation(int hand) => controllers[hand].rawRotation;
        public static Vector3 GetRawVelocity(int hand) => controllers[hand].rawVelocity;
        public static Vector3 GetAngularVelocity(int hand) => controllers[hand].angularVelocity;

        // Runs from onBeforeRender. Left pose listeners first, then right, the order SteamVR fires them in.
        internal static void UpdatePosesAndFireListeners()
        {
            if (lastPoseSampleFrame == Time.frameCount)
                return;
            lastPoseSampleFrame = Time.frameCount;

            foreach (ControllerState controller in controllers)
                UpdateRawPose(controller);

            VRInput.LeftHandPose.FireOpenXRListeners();
            VRInput.RightHandPose.FireOpenXRListeners();
        }

        private static void UpdateRawPose(ControllerState controller)
        {
            // Keep the last pose if tracking drops, like SteamVR does
            if (!TryGetDevice(controller))
                return;
            InputDevice device = controller.device;
            if (!device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 gripPosition))
                return;
            if (!device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion gripRotation))
                return;
            device.TryGetFeatureValue(CommonUsages.deviceVelocity, out Vector3 gripVelocity);
            device.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out controller.angularVelocity);

            controller.rawRotation = gripRotation * controller.gripToRawRotation;
            controller.rawPosition = gripPosition + gripRotation * controller.gripToRawPosition;
            if (rawPoseRotationOffset != Vector3.zero)
                controller.rawRotation *= Quaternion.Euler(rawPoseRotationOffset);
            if (rawPosePositionOffset != Vector3.zero)
                controller.rawPosition += controller.rawRotation * rawPosePositionOffset;

            // The raw point is on the same rigid controller, so it also moves with the controller's spin
            controller.rawVelocity = gripVelocity + Vector3.Cross(controller.angularVelocity, controller.rawPosition - gripPosition);
        }

        public static bool TryGetHeadPose(out Vector3 headPosition, out Quaternion headRotation)
        {
            headPosition = Vector3.zero;
            headRotation = Quaternion.identity;
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (!head.isValid)
                return false;
            return head.TryGetFeatureValue(CommonUsages.centerEyePosition, out headPosition)
                && head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out headRotation);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Haptics

        // Unity's XR haptics have no frequency setting: OpenXRHapticFrequency puts it into the OpenXR call on the way out
        public static void SendHaptic(float delaySeconds, float duration, float frequency, float amplitude, SteamVR_Input_Sources inputSource)
        {
            if (delaySeconds > 0.001f)
            {
                queuedHaptics.Add(new QueuedHaptic
                {
                    fireTime = Time.unscaledTime + delaySeconds,
                    duration = duration,
                    frequency = frequency,
                    amplitude = amplitude,
                    inputSource = inputSource,
                });
                return;
            }

            for (int hand = 0; hand < controllers.Length; hand++)
            {
                if (!IsSourceForHand(inputSource, hand) || !TryGetDevice(controllers[hand]))
                    continue;

                InputDevice device = controllers[hand].device;
                if (!hapticsSupportLogged)
                {
                    hapticsSupportLogged = true;
                    device.TryGetHapticCapabilities(out HapticCapabilities capabilities);
                    Plugin.MyLog.LogInfo($"[OpenXR-Input] Haptics: impulse {capabilities.supportsImpulse}, channels {capabilities.numChannels}");
                }
                OpenXRHapticFrequency.SetFrequency(hand, frequency);
                device.SendHapticImpulse(0, Mathf.Clamp01(amplitude), Mathf.Max(duration, 0.001f));
            }
        }

        internal static void SendQueuedHaptics()
        {
            for (int i = queuedHaptics.Count - 1; i >= 0; i--)
            {
                QueuedHaptic haptic = queuedHaptics[i];
                if (Time.unscaledTime < haptic.fireTime)
                    continue;
                queuedHaptics.RemoveAt(i);
                SendHaptic(0f, haptic.duration, haptic.frequency, haptic.amplitude, haptic.inputSource);
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Controllers

        private static bool TryGetDevice(ControllerState controller)
        {
            if (!controller.device.isValid)
                controller.device = InputDevices.GetDeviceAtXRNode(controller.node);
            if (!controller.device.isValid)
                return false;
            if (controller.device.name != controller.deviceName)
                OnControllerConnected(controller);
            return true;
        }

        private static void OnControllerConnected(ControllerState controller)
        {
            controller.deviceName = controller.device.name;
            string lowerName = (controller.deviceName ?? "").ToLowerInvariant();
            bool isRightHand = controller.node == XRNode.RightHand;

            string controllerType = null;
            GripPoseOffset? gripPose = null;
            if (lowerName.Contains("index"))
            {
                controllerType = "index";
                gripPose = isRightHand ? indexRightGripPose : indexLeftGripPose;
            }
            else if (lowerName.Contains("vive") && !lowerName.Contains("cosmos") && !lowerName.Contains("focus"))
            {
                controllerType = "vive";
                gripPose = viveWandGripPose;
            }
            else if (lowerName.Contains("touch") || lowerName.Contains("quest") || lowerName.Contains("oculus"))
            {
                controllerType = "oculus";
                gripPose = touchGripPose;
            }

            if (gripPose.HasValue)
            {
                // OpenVR is right handed, Unity left handed: z flips, and so do rotations around X and Y
                Vector3 rotation = gripPose.Value.rotation;
                Quaternion gripLocalRotation = Quaternion.AngleAxis(-rotation.x, Vector3.right)
                    * Quaternion.AngleAxis(-rotation.y, Vector3.up)
                    * Quaternion.AngleAxis(rotation.z, Vector3.forward);
                Vector3 gripLocalPosition = new Vector3(gripPose.Value.position.x, gripPose.Value.position.y, -gripPose.Value.position.z);

                // grip = raw * gripLocal, so raw = grip * inverse(gripLocal)
                controller.gripToRawRotation = Quaternion.Inverse(gripLocalRotation);
                controller.gripToRawPosition = -(controller.gripToRawRotation * gripLocalPosition);
            }
            else
            {
                controller.gripToRawRotation = Quaternion.identity;
                controller.gripToRawPosition = Vector3.zero;
            }

            // Same rule as Plugin.DetectAndLogControllers: a Vive wand always wins, others only fill an empty type
            if (controllerType == "vive")
                VRGlobals.vrControllerType = "vive";
            else if (controllerType != null && string.IsNullOrEmpty(VRGlobals.vrControllerType))
                VRGlobals.vrControllerType = controllerType;

            Vector3 offsetRotation = controller.gripToRawRotation.eulerAngles;
            Plugin.MyLog.LogInfo($"[OpenXR-Input] {(isRightHand ? "Right" : "Left")} controller '{controller.deviceName}' -> type {controllerType ?? "unknown (no raw pose offset)"}, "
                + $"grip to raw offset {controller.gripToRawPosition.x:0.000},{controller.gripToRawPosition.y:0.000},{controller.gripToRawPosition.z:0.000} "
                + $"rotation {offsetRotation.x:0.0},{offsetRotation.y:0.0},{offsetRotation.z:0.0}; vrControllerType '{VRGlobals.vrControllerType}'");
        }
    }

    // Added by Plugin only while OpenXR is running
    public class OpenXRInputDriver : MonoBehaviour
    {
        private void OnEnable()
        {
            Application.onBeforeRender += OpenXRInput.UpdatePosesAndFireListeners;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= OpenXRInput.UpdatePosesAndFireListeners;
        }

        private void Update()
        {
            OpenXRInput.SampleButtonsOncePerFrame();
            OpenXRInput.SendQueuedHaptics();
        }
    }
}
