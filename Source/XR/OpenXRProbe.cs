using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Source.XR
{
    // Debug logging for the OpenXR test: every few seconds, the display state and each tracked device's pose,
    // trigger, grip, stick and A/B. Each device's list of inputs is logged once when it first shows up.
    public class OpenXRProbe : MonoBehaviour
    {
        public static bool enabledLogging = true;
        public static float logInterval = 5f;

        private float nextLogTime = 0f;
        private readonly HashSet<string> devicesListed = new HashSet<string>();

        private void Update()
        {
            if (!enabledLogging || Time.unscaledTime < nextLogTime)
                return;
            nextLogTime = Time.unscaledTime + logInterval;

            List<XRDisplaySubsystem> displaySubsystems = new List<XRDisplaySubsystem>();
            SubsystemManager.GetInstances(displaySubsystems);
            bool displayRunning = false;
            foreach (XRDisplaySubsystem displaySubsystem in displaySubsystems)
                displayRunning |= displaySubsystem.running;

            Camera mainCamera = Camera.main;
            Plugin.MyLog.LogInfo($"[OpenXR-Probe] display running {displayRunning}, device '{XRSettings.loadedDeviceName}', "
                + $"eye {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, scale {XRSettings.eyeTextureResolutionScale:0.00}, "
                + $"occlusion mesh {XRSettings.useOcclusionMesh}, main camera '{(mainCamera != null ? mainCamera.name : "none")}' "
                + $"stereo {(mainCamera != null && mainCamera.stereoEnabled)}");

            LogDevice("head", XRNode.CenterEye);
            LogDevice("left", XRNode.LeftHand);
            LogDevice("right", XRNode.RightHand);
        }

        private void LogDevice(string label, XRNode node)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (!device.isValid)
            {
                Plugin.MyLog.LogInfo($"[OpenXR-Probe] {label}: no device");
                return;
            }

            if (devicesListed.Add(label + device.name))
            {
                List<InputFeatureUsage> inputs = new List<InputFeatureUsage>();
                device.TryGetFeatureUsages(inputs);
                List<string> inputNames = new List<string>();
                foreach (InputFeatureUsage input in inputs)
                    inputNames.Add(input.name + ":" + input.type.Name);
                Plugin.MyLog.LogInfo($"[OpenXR-Probe] {label} '{device.name}' ({device.characteristics}) inputs: {string.Join(", ", inputNames)}");
            }

            device.TryGetFeatureValue(CommonUsages.isTracked, out bool isTracked);
            device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position);
            device.TryGetFeatureValue(CommonUsages.trigger, out float trigger);
            device.TryGetFeatureValue(CommonUsages.grip, out float grip);
            device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick);
            device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton);
            device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryButton);
            Plugin.MyLog.LogInfo($"[OpenXR-Probe] {label}: tracked {isTracked} position {position.x:0.00},{position.y:0.00},{position.z:0.00} "
                + $"trigger {trigger:0.00} grip {grip:0.00} stick {stick.x:0.00},{stick.y:0.00} A {primaryButton} B {secondaryButton}");
        }
    }
}
