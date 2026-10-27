using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Source.XR
{
    // On SteamVR the floor origin requested at startup reports success but the seated space stays in use (head height
    // came out around -0.5 m). Asking again once the session is running and the headset is tracked fixes it.
    public class OpenXRFloorOrigin : MonoBehaviour
    {
        private bool floorRequested = false;
        private float nextCheckTime = 0f;
        private float logHeadHeightTime = -1f;

        private void Update()
        {
            if (logHeadHeightTime > 0f && Time.unscaledTime >= logHeadHeightTime)
            {
                logHeadHeightTime = -1f;
                Plugin.MyLog.LogInfo($"[OpenXR] Head height after the floor request: {GetHeadHeight():0.00} m");
            }

            if (floorRequested || Time.unscaledTime < nextCheckTime)
                return;
            nextCheckTime = Time.unscaledTime + 1f;

            if (!IsDisplayRunning() || !InputDevices.GetDeviceAtXRNode(XRNode.CenterEye).isValid)
                return;

            List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
            SubsystemManager.GetInstances(inputSubsystems);
            foreach (XRInputSubsystem inputSubsystem in inputSubsystems)
            {
                if (!inputSubsystem.running)
                    continue;

                float headHeightBefore = GetHeadHeight();
                // Switching to Device first makes the runtime rebuild the space instead of treating Floor as unchanged
                inputSubsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
                bool floorSet = inputSubsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                Plugin.MyLog.LogInfo($"[OpenXR] Floor origin requested again: success {floorSet}, mode {inputSubsystem.GetTrackingOriginMode()}, "
                    + $"head height before {headHeightBefore:0.00} m");
            }
            floorRequested = true;
            logHeadHeightTime = Time.unscaledTime + 1f;
        }

        private static bool IsDisplayRunning()
        {
            List<XRDisplaySubsystem> displaySubsystems = new List<XRDisplaySubsystem>();
            SubsystemManager.GetInstances(displaySubsystems);
            foreach (XRDisplaySubsystem displaySubsystem in displaySubsystems)
            {
                if (displaySubsystem.running)
                    return true;
            }
            return false;
        }

        private static float GetHeadHeight()
        {
            InputDevice headset = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            if (headset.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 headPosition))
                return headPosition.y;
            return float.NaN;
        }
    }
}
