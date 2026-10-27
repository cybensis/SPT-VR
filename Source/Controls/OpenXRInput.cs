using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // OpenXR side of VRInput. Reads both controllers through Unity's XR input API (works with the game's old Input
    // Manager - headset-confirmed), turns OpenXR's grip pose back into SteamVR's "raw" pose that the rig, hand
    // offsets and gun alignment were all tuned on, and fires the pose callbacks from onBeforeRender, where SteamVR
    // fires them (poseUpdateMode OnPreCull = SteamVR_Behaviour.OnBeforeRender). Buttons sample once a frame, on the
    // first read or in Update, whichever comes first - every action moves to the new frame together, like SteamVR.
    public static class OpenXRInput
    {
        public const int Left = 0, Right = 1;
        public enum Button { None, Grip, Primary, Secondary, StickClick, StickClickOrPadTouch }
        public enum Edge { Held, Down, Up }

        // Grip "click" from the analog squeeze value (Index: how far the fingers wrap the handle, which is what the
        // Index binding's touchy click follows; Touch: squeeze), with hysteresis so it doesn't chatter.
        public static float gripPress = 0.5f;
        public static float gripRelease = 0.25f;

        // In-headset correction on top of the derived grip -> raw conversion, in the raw pose's own frame.
        public static Vector3 extraPosOffset = Vector3.zero;
        public static Vector3 extraRotOffset = Vector3.zero;

        // Where SteamVR's OpenXR grip pose sits relative to the raw pose: the "grip" component of each controller's
        // SteamVR render model (OpenVR right-handed, metres / degrees X,Y,Z). Inverted to get raw back from grip.
        private struct GripComponent
        {
            public Vector3 pos, euler;
            public GripComponent(float px, float py, float pz, float ex, float ey, float ez)
            { pos = new Vector3(px, py, pz); euler = new Vector3(ex, ey, ez); }
        }
        private static readonly GripComponent IndexLeft = new GripComponent(0f, -0.015f, 0.13f, 15.392f, -2.071f, 0.303f);
        private static readonly GripComponent IndexRight = new GripComponent(0f, -0.015f, 0.13f, 15.392f, 2.071f, -0.303f);
        private static readonly GripComponent Touch = new GripComponent(0f, 0.003f, 0.097f, 5.037f, 0f, 0f);
        private static readonly GripComponent ViveWand = new GripComponent(0f, -0.015f, 0.097f, 5.037f, 0f, 0f);

        private sealed class Hand
        {
            public XRNode node;
            public InputDevice device;
            public string deviceName;
            public Vector3 gripToRawPos;                       // grip-frame offset, Unity space
            public Quaternion rawFromGrip = Quaternion.identity;

            public bool grip, primary, secondary, stickClick, padTouch;
            public bool pGrip, pPrimary, pSecondary, pStickClick, pPadTouch;
            public float trigger, squeeze;
            public Vector2 stick;

            public Vector3 rawPos, rawVel, angVel;
            public Quaternion rawRot = Quaternion.identity;
        }
        private static readonly Hand[] hands = { new Hand { node = XRNode.LeftHand }, new Hand { node = XRNode.RightHand } };
        private static int buttonFrame = -1, poseFrame = -1;
        private static bool hapticsLogged;

        private struct PendingHaptic { public float at, duration, amplitude; public SteamVR_Input_Sources src; }
        private static readonly List<PendingHaptic> pending = new List<PendingHaptic>();

        public static bool Matches(SteamVR_Input_Sources src, int hand)
            => src == SteamVR_Input_Sources.Any
            || (src == SteamVR_Input_Sources.LeftHand && hand == Left)
            || (src == SteamVR_Input_Sources.RightHand && hand == Right);

        // ---------------------------------------------------------------------------------------- buttons & axes

        public static bool Get(int hand, Button button, Edge edge)
        {
            SampleButtons();
            Hand h = hands[hand];
            bool cur, prev;
            switch (button)
            {
                case Button.Grip: cur = h.grip; prev = h.pGrip; break;
                case Button.Primary: cur = h.primary; prev = h.pPrimary; break;
                case Button.Secondary: cur = h.secondary; prev = h.pSecondary; break;
                case Button.StickClick: cur = h.stickClick; prev = h.pStickClick; break;
                case Button.StickClickOrPadTouch: cur = h.stickClick || h.padTouch; prev = h.pStickClick || h.pPadTouch; break;
                default: return false;
            }
            return edge == Edge.Held ? cur : edge == Edge.Down ? cur && !prev : !cur && prev;
        }

        public static float Trigger(int hand) { SampleButtons(); return hands[hand].trigger; }
        public static Vector2 Stick(int hand) { SampleButtons(); return hands[hand].stick; }

        internal static void SampleButtons()
        {
            if (buttonFrame == Time.frameCount)
                return;
            buttonFrame = Time.frameCount;
            foreach (Hand h in hands)
            {
                h.pGrip = h.grip; h.pPrimary = h.primary; h.pSecondary = h.secondary;
                h.pStickClick = h.stickClick; h.pPadTouch = h.padTouch;
                if (!Refresh(h))
                {
                    h.grip = h.primary = h.secondary = h.stickClick = h.padTouch = false;
                    h.trigger = h.squeeze = 0f;
                    h.stick = Vector2.zero;
                    continue;
                }
                InputDevice d = h.device;
                d.TryGetFeatureValue(CommonUsages.trigger, out h.trigger);
                d.TryGetFeatureValue(CommonUsages.grip, out h.squeeze);
                d.TryGetFeatureValue(CommonUsages.primary2DAxis, out h.stick);
                d.TryGetFeatureValue(CommonUsages.primaryButton, out h.primary);
                d.TryGetFeatureValue(CommonUsages.secondaryButton, out h.secondary);
                d.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out h.stickClick);
                d.TryGetFeatureValue(CommonUsages.secondary2DAxisTouch, out h.padTouch);
                h.grip = h.pGrip ? h.squeeze > gripRelease : h.squeeze >= gripPress;
            }
        }

        // ---------------------------------------------------------------------------------------- poses

        public static bool IsIndex(int hand) => (hands[hand].deviceName ?? "").IndexOf("index", System.StringComparison.OrdinalIgnoreCase) >= 0;

        public static Vector3 RawPosition(int hand) => hands[hand].rawPos;
        public static Quaternion RawRotation(int hand) => hands[hand].rawRot;
        public static Vector3 RawVelocity(int hand) => hands[hand].rawVel;
        public static Vector3 AngularVelocity(int hand) => hands[hand].angVel;

        // Called from onBeforeRender (OpenXRInputDriver): pose sample, then the pose callbacks, left then right
        // (the action manifest's order, which is SteamVR's firing order).
        internal static void UpdatePoses()
        {
            if (poseFrame == Time.frameCount)
                return;
            poseFrame = Time.frameCount;
            foreach (Hand h in hands)
            {
                if (!Refresh(h)
                    || !h.device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 gripPos)
                    || !h.device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion gripRot))
                    continue;    // keep the last pose, like an untracked SteamVR pose
                h.device.TryGetFeatureValue(CommonUsages.deviceVelocity, out Vector3 gripVel);
                h.device.TryGetFeatureValue(CommonUsages.deviceAngularVelocity, out h.angVel);

                h.rawRot = gripRot * h.rawFromGrip;
                h.rawPos = gripPos + gripRot * h.gripToRawPos;
                if (extraRotOffset != Vector3.zero) h.rawRot *= Quaternion.Euler(extraRotOffset);
                if (extraPosOffset != Vector3.zero) h.rawPos += h.rawRot * extraPosOffset;
                // the raw origin is a point on the same rigid body: v = v_grip + w x r
                h.rawVel = gripVel + Vector3.Cross(h.angVel, h.rawPos - gripPos);
            }
            VRInput.LeftHandPose.FireOpenXR();
            VRInput.RightHandPose.FireOpenXR();
        }

        public static bool TryGetHeadPose(out Vector3 pos, out Quaternion rot)
        {
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            rot = Quaternion.identity;
            pos = Vector3.zero;
            return head.isValid
                && head.TryGetFeatureValue(CommonUsages.centerEyePosition, out pos)
                && head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out rot);
        }

        // ---------------------------------------------------------------------------------------- haptics

        // Unity's XR haptic call has no frequency, so the frequency is dropped (AudioHaptics loses its texture).
        public static void Haptic(float secondsFromNow, float duration, float amplitude, SteamVR_Input_Sources src)
        {
            if (secondsFromNow > 0.001f)
            {
                pending.Add(new PendingHaptic { at = Time.unscaledTime + secondsFromNow, duration = duration, amplitude = amplitude, src = src });
                return;
            }
            for (int i = 0; i < hands.Length; i++)
            {
                if (!Matches(src, i) || !Refresh(hands[i]))
                    continue;
                InputDevice d = hands[i].device;
                if (!hapticsLogged)
                {
                    hapticsLogged = true;
                    d.TryGetHapticCapabilities(out HapticCapabilities caps);
                    Plugin.MyLog.LogInfo($"[OpenXR-Input] Haptics: impulse {caps.supportsImpulse}, channels {caps.numChannels}");
                }
                d.SendHapticImpulse(0, Mathf.Clamp01(amplitude), Mathf.Max(duration, 0.001f));
            }
        }

        internal static void UpdatePendingHaptics()
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (Time.unscaledTime < pending[i].at)
                    continue;
                PendingHaptic p = pending[i];
                pending.RemoveAt(i);
                Haptic(0f, p.duration, p.amplitude, p.src);
            }
        }

        // ---------------------------------------------------------------------------------------- devices

        private static bool Refresh(Hand h)
        {
            if (!h.device.isValid)
                h.device = InputDevices.GetDeviceAtXRNode(h.node);
            if (!h.device.isValid)
                return false;
            if (h.device.name != h.deviceName)
                OnDevice(h);
            return true;
        }

        private static void OnDevice(Hand h)
        {
            h.deviceName = h.device.name;
            string n = (h.deviceName ?? "").ToLowerInvariant();
            bool right = h.node == XRNode.RightHand;
            string type;
            GripComponent? grip;
            if (n.Contains("index")) { type = "index"; grip = right ? IndexRight : IndexLeft; }
            else if (n.Contains("vive") && !n.Contains("cosmos") && !n.Contains("focus")) { type = "vive"; grip = ViveWand; }
            else if (n.Contains("touch") || n.Contains("quest") || n.Contains("oculus")) { type = "oculus"; grip = Touch; }
            else { type = null; grip = null; }

            if (grip.HasValue)
            {
                // OpenVR right-handed -> Unity: position z flips, rotations about X and Y flip sign
                Vector3 e = grip.Value.euler;
                Quaternion gripLocal = Quaternion.AngleAxis(-e.x, Vector3.right) * Quaternion.AngleAxis(-e.y, Vector3.up)
                                     * Quaternion.AngleAxis(e.z, Vector3.forward);
                Vector3 gripLocalPos = new Vector3(grip.Value.pos.x, grip.Value.pos.y, -grip.Value.pos.z);
                // grip = raw * gripLocal  ->  raw = grip * inverse(gripLocal)
                h.rawFromGrip = Quaternion.Inverse(gripLocal);
                h.gripToRawPos = -(h.rawFromGrip * gripLocalPos);
            }
            else
            {
                h.rawFromGrip = Quaternion.identity;
                h.gripToRawPos = Vector3.zero;
            }

            // Same rule as Plugin.DetectAndLogControllers: a Vive wand always wins, others only fill an empty type.
            if (type == "vive")
                VRGlobals.vrControllerType = "vive";
            else if (type != null && string.IsNullOrEmpty(VRGlobals.vrControllerType))
                VRGlobals.vrControllerType = type;

            Plugin.MyLog.LogInfo($"[OpenXR-Input] {(right ? "Right" : "Left")} controller '{h.deviceName}' -> type {type ?? "unknown (no raw-pose offset)"}, "
                + $"grip->raw offset {h.gripToRawPos.x:0.000},{h.gripToRawPos.y:0.000},{h.gripToRawPos.z:0.000} "
                + $"rot {h.rawFromGrip.eulerAngles.x:0.0},{h.rawFromGrip.eulerAngles.y:0.0},{h.rawFromGrip.eulerAngles.z:0.0}; vrControllerType '{VRGlobals.vrControllerType}'");
        }
    }

    // Drives OpenXRInput: buttons in Update (reads earlier in the frame sample on first use anyway), pose callbacks
    // in onBeforeRender. Added by Plugin only while OpenXR is running.
    public class OpenXRInputDriver : MonoBehaviour
    {
        private void OnEnable() { Application.onBeforeRender += OpenXRInput.UpdatePoses; }
        private void OnDisable() { Application.onBeforeRender -= OpenXRInput.UpdatePoses; }

        private void Update()
        {
            OpenXRInput.SampleButtons();
            OpenXRInput.UpdatePendingHaptics();
        }
    }
}
