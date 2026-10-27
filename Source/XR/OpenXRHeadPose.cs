using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Source.XR
{
    // OPENXR SPIKE: stands in for SteamVR_TrackedObject (index Hmd, no origin) on the camera objects - writes the
    // headset pose in tracking space as the local pose, like SteamVR_TrackedObject does. Added next to every
    // SteamVR_TrackedObject while OpenXR is running (OpenXRSpikePatches). Update + onBeforeRender = latest pose.
    public class OpenXRHeadPose : MonoBehaviour
    {
        private InputDevice head;

        private void OnEnable() { Application.onBeforeRender += Apply; }
        private void OnDisable() { Application.onBeforeRender -= Apply; }
        private void Update() { Apply(); }

        private void Apply()
        {
            if (!head.isValid)
                head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (!head.isValid)
                head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!head.isValid)
                return;

            if (head.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 pos)
                || head.TryGetFeatureValue(CommonUsages.devicePosition, out pos))
                transform.localPosition = pos;
            if (head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out Quaternion rot)
                || head.TryGetFeatureValue(CommonUsages.deviceRotation, out rot))
                transform.localRotation = rot;
        }
    }
}
