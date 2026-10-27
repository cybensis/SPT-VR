using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Source.XR
{
    // OPENXR SPIKE: the Floor request made at loader start reported success, but the first test's head heights
    // (-0.5 m worn, -0.9 m on the desk) match SteamVR's SEATED space, not the standing one OpenVR gives the rig.
    // Re-request Floor once the session is actually running and the headset is tracked, and log what happened.
    public class OpenXRFloorOrigin : MonoBehaviour
    {
        private bool done;
        private float next;
        private float logHeadAt = -1f;

        private void Update()
        {
            if (logHeadAt > 0f && Time.unscaledTime >= logHeadAt)
            {
                logHeadAt = -1f;
                Plugin.MyLog.LogInfo($"[OpenXR] Head height after re-request: {HeadY():0.00} m");
            }
            if (done || Time.unscaledTime < next)
                return;
            next = Time.unscaledTime + 1f;

            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetInstances(displays);
            if (!displays.Any(d => d.running) || !InputDevices.GetDeviceAtXRNode(XRNode.CenterEye).isValid)
                return;

            var inputs = new List<XRInputSubsystem>();
            SubsystemManager.GetInstances(inputs);
            foreach (var input in inputs.Where(i => i.running))
            {
                float before = HeadY();
                TrackingOriginModeFlags supported = input.GetSupportedTrackingOriginModes();
                TrackingOriginModeFlags was = input.GetTrackingOriginMode();
                // Device first so the runtime actually rebuilds the space instead of treating Floor as unchanged.
                bool dev = input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Device);
                bool floor = input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                var boundary = new List<Vector3>();
                input.TryGetBoundaryPoints(boundary);
                input.trackingOriginUpdated += s => Plugin.MyLog.LogInfo($"[OpenXR] Tracking origin updated -> {s.GetTrackingOriginMode()}");
                Plugin.MyLog.LogInfo($"[OpenXR] Floor re-request: supported {supported}, was {was}, Device {dev}, Floor {floor}, "
                    + $"now {input.GetTrackingOriginMode()}, boundary points {boundary.Count}, head height before {before:0.00} m");
            }
            done = true;
            logHeadAt = Time.unscaledTime + 1f;
        }

        private static float HeadY()
        {
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
            return head.TryGetFeatureValue(CommonUsages.centerEyePosition, out Vector3 p) ? p.y : float.NaN;
        }
    }
}
