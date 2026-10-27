using System;
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
    // OPENXR SPIKE (branch openxr-spike): starts Unity's OpenXR plugin instead of the OpenVR loader, display only.
    // Unity keeps rendering MultiPass, so every per-eye code path (ours + the Six mods) runs unchanged. SteamVR is
    // never initialised in this mode: its action objects are created (PreInitialize) so calls return defaults, but
    // nothing reaches OpenVR - controllers do nothing yet. Needs the player-built OpenXR files in the game folder
    // (libs/OpenXR: managed DLLs -> Managed, native -> Plugins/x86_64, manifest -> UnitySubsystems/UnityOpenXR).
    public static class OpenXRBootstrap
    {
        public static bool Active { get; private set; }

        // Interaction profiles the runtime may bind; without any, OpenXR reports no controllers at all.
        private static readonly Type[] Profiles =
        {
            typeof(ValveIndexControllerProfile),
            typeof(OculusTouchControllerProfile),
            typeof(MetaQuestTouchProControllerProfile),
            typeof(HTCViveControllerProfile),
            typeof(MicrosoftMotionControllerProfile),
            typeof(HPReverbG2ControllerProfile),
            typeof(KHRSimpleControllerProfile),
        };

        public static bool Initialize()
        {
            var log = Plugin.MyLog;
            try
            {
                // No settings asset ships in the game, so this creates the runtime default (package source:
                // OpenXRSettings.GetInstance). Features are normally added by the editor; here we add them ourselves.
                OpenXRSettings oxr = OpenXRSettings.Instance;
                oxr.renderMode = OpenXRSettings.RenderMode.MultiPass;
                oxr.depthSubmissionMode = OpenXRSettings.DepthSubmissionMode.None;

                var features = Profiles.Select(t =>
                {
                    var f = (OpenXRFeature)ScriptableObject.CreateInstance(t);
                    f.enabled = true;
                    return f;
                }).ToList();
                // Finger curls: our own feature; its extensions are requested through the same internal field the
                // editor normally fills from the feature attribute.
                var hands = ScriptableObject.CreateInstance<OpenXRHandTracking>();
                typeof(OpenXRFeature).GetField("openxrExtensionStrings", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(hands, OpenXRHandTracking.Extensions);
                typeof(OpenXRFeature).GetField("nameUi", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(hands, "SPT-VR Hand Tracking");
                hands.enabled = true;
                features.Add(hands);
                FieldInfo featuresField = typeof(OpenXRSettings).GetField("features", BindingFlags.NonPublic | BindingFlags.Instance);
                if (featuresField == null)
                {
                    log.LogError("[OpenXR] OpenXRSettings.features not found - plugin version mismatch?");
                    return false;
                }
                featuresField.SetValue(oxr, features.ToArray());

                var general = ScriptableObject.CreateInstance<XRGeneralSettings>();
                var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
                var loader = ScriptableObject.CreateInstance<OpenXRLoader>();
                general.Manager = manager;
                manager.loaders.Clear();
                manager.loaders.Add(loader);

                manager.InitializeLoaderSync();
                if (manager.activeLoader == null)
                {
                    log.LogError("[OpenXR] Loader failed to initialise (see the [XR] / OpenXR lines above in Player.log).");
                    return false;
                }
                manager.StartSubsystems();

                XRSettings.useOcclusionMesh = true;

                // OpenVR's standing universe puts the floor at y = 0; the rig expects that.
                var inputs = new System.Collections.Generic.List<XRInputSubsystem>();
                SubsystemManager.GetInstances(inputs);
                foreach (var input in inputs)
                {
                    bool floor = input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                    log.LogInfo($"[OpenXR] Tracking origin Floor: {floor} (now {input.GetTrackingOriginMode()})");
                }

                Active = true;
                log.LogInfo($"[OpenXR] Running. Runtime '{OpenXRRuntime.name}' {OpenXRRuntime.version}, API {OpenXRRuntime.apiVersion}, "
                    + $"plugin {OpenXRRuntime.pluginVersion}. Device '{XRSettings.loadedDeviceName}', stereo {XRSettings.stereoRenderingMode}, "
                    + $"eye texture {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}, occlusion mesh {XRSettings.useOcclusionMesh}");
                log.LogInfo("[OpenXR] Enabled extensions: " + string.Join(" ", OpenXRRuntime.GetEnabledExtensions()));
                string[] avail = OpenXRRuntime.GetAvailableExtensions();
                log.LogInfo($"[OpenXR] Runtime offers {avail.Length} extensions; hand tracking: "
                    + string.Join(" ", avail.Where(e => e.Contains("hand")).ToArray()));
                return true;
            }
            catch (Exception ex)
            {
                log.LogError("[OpenXR] Initialisation threw: " + ex);
                return false;
            }
        }
    }
}
