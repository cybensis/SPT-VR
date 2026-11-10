using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace TarkovVR.Source.XR
{
    // Starts Unity's OpenXR plugin instead of the OpenVR loader ("useOpenXR" in ModSettings.json).
    // Unity still renders MultiPass, so all the per-eye code keeps working. SteamVR is never started in this mode.
    public static class OpenXRBootstrap
    {
        // No static field may name an OpenXR type: Active is read every frame (VRInput), and without Unity.XR.OpenXR.dll the class
        // initializer would throw on every read - OpenVR mode included
        public static bool Active { get; private set; }
        public static bool registerFrameWatch = false;   // dev: the native frame-call watcher for the cost probe

        // false = Plugin falls back to OpenVR. Called through Plugin.TryStartOpenXR, which also catches the call itself failing
        // (Unity.XR.OpenXR.dll missing: this method can't be compiled).
        public static bool Initialize()
        {
            XRManagerSettings managerSettings = null;
            try
            {
                // The game ships no OpenXR settings asset, so this creates a default one at runtime
                OpenXRSettings openXRSettings = OpenXRSettings.Instance;
                openXRSettings.renderMode = OpenXRSettings.RenderMode.MultiPass;
                openXRSettings.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None;

                if (!AddFeatures(openXRSettings))
                    return false;

                XRGeneralSettings generalSettings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                managerSettings = ScriptableObject.CreateInstance<XRManagerSettings>();
                OpenXRLoader openXRLoader = ScriptableObject.CreateInstance<OpenXRLoader>();
                generalSettings.Manager = managerSettings;
                managerSettings.loaders.Clear();
                managerSettings.loaders.Add(openXRLoader);

                managerSettings.InitializeLoaderSync();
                if (managerSettings.activeLoader == null)
                {
                    Plugin.MyLog.LogError("[OpenXR] Loader failed to initialize, see the OpenXR lines in Player.log");
                    return false;
                }
                managerSettings.StartSubsystems();

                XRSettings.useOcclusionMesh = true;

                // The rig expects the floor at y = 0 like OpenVR's standing space. This early request reports success
                // but doesn't stick on SteamVR - OpenXRFloorOrigin asks again once the session is running.
                List<XRInputSubsystem> inputSubsystems = new List<XRInputSubsystem>();
                SubsystemManager.GetInstances(inputSubsystems);
                foreach (XRInputSubsystem inputSubsystem in inputSubsystems)
                    inputSubsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);

                Active = true;
                LogRuntimeInfo();
                return true;
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError("[OpenXR] Initialization failed: " + exception);
                StopHalfStarted(managerSettings);
                return false;
            }
        }

        // A loader that started before the failure is stopped, so OpenVR can start in its place
        private static void StopHalfStarted(XRManagerSettings managerSettings)
        {
            if (managerSettings == null || managerSettings.activeLoader == null)
                return;
            try
            {
                managerSettings.StopSubsystems();
                managerSettings.DeinitializeLoader();
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogWarning("[OpenXR] Stopping the half-started loader failed: " + exception.Message);
            }
        }

        // Features are normally set up by the Unity editor, so their fields are internal - set them through reflection
        private static bool AddFeatures(OpenXRSettings openXRSettings)
        {
            // Without at least one controller profile, OpenXR reports no controllers at all
            Type[] controllerProfiles =
            {
                typeof(ValveIndexControllerProfile),
                typeof(OculusTouchControllerProfile),
                typeof(MetaQuestTouchProControllerProfile),
                typeof(HTCViveControllerProfile),
                typeof(MicrosoftMotionControllerProfile),
                typeof(HPReverbG2ControllerProfile),
                typeof(KHRSimpleControllerProfile),
            };
            List<OpenXRFeature> features = new List<OpenXRFeature>();
            foreach (Type profileType in controllerProfiles)
            {
                OpenXRFeature profile = (OpenXRFeature)ScriptableObject.CreateInstance(profileType);
                profile.enabled = true;
                features.Add(profile);
            }

            OpenXRHandTracking handTracking = ScriptableObject.CreateInstance<OpenXRHandTracking>();
            FieldInfo extensionsField = typeof(OpenXRFeature).GetField("openxrExtensionStrings", BindingFlags.NonPublic | BindingFlags.Instance);
            FieldInfo nameField = typeof(OpenXRFeature).GetField("nameUi", BindingFlags.NonPublic | BindingFlags.Instance);
            extensionsField?.SetValue(handTracking, OpenXRHandTracking.REQUIRED_EXTENSIONS);
            nameField?.SetValue(handTracking, "SPT-VR Hand Tracking");
            handTracking.enabled = true;
            features.Add(handTracking);

            OpenXRHapticFrequency hapticFrequency = ScriptableObject.CreateInstance<OpenXRHapticFrequency>();
            nameField?.SetValue(hapticFrequency, "SPT-VR Haptic Frequency");
            hapticFrequency.enabled = true;
            features.Add(hapticFrequency);

            // The frame-call watcher (native, hooks xrWaitFrame and friends) only fed the cost probe's OpenXR rows, which is no
            // longer added: not registered in release builds
            if (registerFrameWatch)
            {
                OpenXRFrameWatch frameWatch = ScriptableObject.CreateInstance<OpenXRFrameWatch>();
                nameField?.SetValue(frameWatch, "SPT-VR Frame Watch");
                frameWatch.enabled = true;
                features.Add(frameWatch);
            }

            FieldInfo featuresField = typeof(OpenXRSettings).GetField("features", BindingFlags.NonPublic | BindingFlags.Instance);
            if (featuresField == null)
            {
                Plugin.MyLog.LogError("[OpenXR] OpenXRSettings.features not found, wrong OpenXR plugin version?");
                return false;
            }
            featuresField.SetValue(openXRSettings, features.ToArray());
            return true;
        }

        private static void LogRuntimeInfo()
        {
            Plugin.MyLog.LogInfo($"[OpenXR] Running. Runtime '{OpenXRRuntime.name}' {OpenXRRuntime.version}, API {OpenXRRuntime.apiVersion}, "
                + $"plugin {OpenXRRuntime.pluginVersion}. Device '{XRSettings.loadedDeviceName}', stereo {XRSettings.stereoRenderingMode}, "
                + $"occlusion mesh {XRSettings.useOcclusionMesh}");
            Plugin.MyLog.LogInfo("[OpenXR] Enabled extensions: " + string.Join(" ", OpenXRRuntime.GetEnabledExtensions()));

            string[] availableExtensions = OpenXRRuntime.GetAvailableExtensions();
            string[] handExtensions = availableExtensions.Where(extension => extension.Contains("hand")).ToArray();
            Plugin.MyLog.LogInfo($"[OpenXR] Runtime offers {availableExtensions.Length} extensions, hand tracking: {string.Join(" ", handExtensions)}");
        }
    }
}
