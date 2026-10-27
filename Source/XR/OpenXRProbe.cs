using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Source.XR
{
    // OPENXR SPIKE: answers "does controller input reach Unity's basic XR input API with the game's old Input
    // Manager?" - every 5 s one [OpenXR-Probe] line per device (pose + main buttons/axes), and each device's
    // feature list the first time it shows up. Read-only.
    public class OpenXRProbe : MonoBehaviour
    {
        public static float interval = 5f;

        private float next;
        private readonly HashSet<string> listed = new HashSet<string>();

        private void Update()
        {
            if (Time.unscaledTime < next)
                return;
            next = Time.unscaledTime + interval;

            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetInstances(displays);
            bool running = displays.Any(d => d.running);
            Plugin.MyLog.LogInfo($"[OpenXR-Probe] display running {running}, device '{XRSettings.loadedDeviceName}', "
                + $"eye {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, scale {XRSettings.eyeTextureResolutionScale:0.00}, "
                + $"occlusion mesh {XRSettings.useOcclusionMesh}, main cam '{(Camera.main != null ? Camera.main.name : "none")}' "
                + $"stereo {(Camera.main != null && Camera.main.stereoEnabled)}");

            Log("head", XRNode.CenterEye);
            Log("left", XRNode.LeftHand);
            Log("right", XRNode.RightHand);
        }

        private void Log(string label, XRNode node)
        {
            InputDevice d = InputDevices.GetDeviceAtXRNode(node);
            if (!d.isValid)
            {
                Plugin.MyLog.LogInfo($"[OpenXR-Probe] {label}: no device");
                return;
            }

            if (listed.Add(label + d.name))
            {
                var usages = new List<InputFeatureUsage>();
                d.TryGetFeatureUsages(usages);
                Plugin.MyLog.LogInfo($"[OpenXR-Probe] {label} '{d.name}' ({d.characteristics}) features: "
                    + string.Join(", ", usages.Select(u => u.name + ":" + u.type.Name).ToArray()));
            }

            d.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked);
            d.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos);
            d.TryGetFeatureValue(CommonUsages.trigger, out float trigger);
            d.TryGetFeatureValue(CommonUsages.grip, out float grip);
            d.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick);
            d.TryGetFeatureValue(CommonUsages.primaryButton, out bool a);
            d.TryGetFeatureValue(CommonUsages.secondaryButton, out bool b);
            Plugin.MyLog.LogInfo($"[OpenXR-Probe] {label}: tracked {tracked} pos {pos.x:0.00},{pos.y:0.00},{pos.z:0.00} "
                + $"trigger {trigger:0.00} grip {grip:0.00} stick {stick.x:0.00},{stick.y:0.00} A {a} B {b}");
        }
    }
}
