using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Source.XR
{
    // Replaces SteamVR_TrackedObject on the camera objects while OpenXR is running, because SteamVR_TrackedObject
    // gets no poses without OpenVR. Like SteamVR_TrackedObject, it writes the headset pose as the local pose.
    public class OpenXRHeadPose : MonoBehaviour
    {
        private InputDevice headset;

        private void OnEnable()
        {
            Application.onBeforeRender += ApplyHeadPose;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= ApplyHeadPose;
        }

        private void Update()
        {
            ApplyHeadPose();
        }

        private void ApplyHeadPose()
        {
            if (!headset.isValid)
                headset = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (!headset.isValid)
                headset = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!headset.isValid)
                return;

            if (headset.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 headPosition)
                || headset.TryGetFeatureValue(CommonUsages.devicePosition, out headPosition))
                transform.localPosition = headPosition;

            if (headset.TryGetFeatureValue(CommonUsages.centerEyeRotation, out Quaternion headRotation)
                || headset.TryGetFeatureValue(CommonUsages.deviceRotation, out headRotation))
                transform.localRotation = headRotation;
        }
    }
}
