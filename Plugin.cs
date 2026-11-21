using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using TarkovVR.ModSupport;
using Unity.XR.OpenVR;
using UnityEngine;
using UnityEngine.XR.Management;
using Valve.VR;
using UnityEngine.XR;
using System.Text;
using TarkovVR.Source.Settings;

using TarkovVR.Source.XR;
using TarkovVR.Source.Controls;

namespace TarkovVR
{
    [BepInPlugin("com.matsix.sptvr", "matsix-sptvr", "2.0.0")]
    // Soft: the FikaSync module only loads its plugin when FIKA is present; a hard dependency would break solo VR
    [BepInDependency("com.matsix.sptvr.fikasync", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft: ORBIT (bot-AI mod) is referenced (publicized DLL) so dragged corpses can relocate their loot waypoints. The VR mod
    // must load without it; the dependency only makes ORBIT load first so it's in the Chainloader registry when we look.
    [BepInDependency("com.chazut.orbit", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft: FuckThisCulling (GPU occlusion culling) gets stereo support patches (ModSupport/FuckThisCulling) when installed
    [BepInDependency("com.rootdarkarchon.fuckthisculling", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft: the mods with VR support patches (ModSupport), so each is in the Chainloader registry when ApplyModSupport looks
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.Amanda.Graphics", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.mpstark.dynamicmaps", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.tyfon.weaponcustomizer", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.kmyuhkyuk.KmyTarkovConfiguration", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;
        // Verbose logging for the dragged-corpse loot relocation (OrbitSupport): whether an ORBIT waypoint / vanilla DeadBody record
        // was found and where it was moved
        public static bool debugBodyDragLoot = false;
        private bool vrInitializedSuccessfully = false;
        public static GameObject WeaponModdingAnchor { get; set; }

        private void Awake()
        {
            Logger.LogInfo($"Plugin SPT-VR is loading!");
            MyLog = Logger;

            // The VR libraries and data files from BepInEx/plugins/sptvr/VR, before anything starts VR
            try
            {
                VRRuntimeFiles.Prepare();
            }
            catch (Exception exception)
            {
                Logger.LogError($"[VR files] preparing the VR files failed: {exception}");
            }

            // Unity read the old manifests at engine start: starting VR against them could pair two copies of a plugin
            if (VRRuntimeFiles.RestartNeeded)
            {
                Logger.LogError("VR not started: its plugin manifests in the game's data folder were just added or updated. Restart the game once.");
                return;
            }

            if (!InitializeVR())
            {
                Logger.LogError("VR initialization failed. Skipping the rest of the plugin setup.");
                return;
            }

            Logger.LogInfo("SPTVR initialized successfully.");
            vrInitializedSuccessfully = true;

            ApplyPatches("TarkovVR.Patches");
            InitializeConditionalPatches();

            // The desktop window: one eye, cropped to the window and stabilised (for recording)
            gameObject.AddComponent<TarkovVR.Source.Graphics.DesktopMirror>();
            // VR Graphics "Disable Hideout Shadows"
            gameObject.AddComponent<TarkovVR.Source.Graphics.HideoutShadows>();
            // Software foveated rendering on the headset camera (BepInEx config "Foveated Masking")
            TarkovVR.Source.Graphics.FoveatedMask.Init(Config);

            if (OpenXRBootstrap.Active)
            {
                gameObject.AddComponent<OpenXRProbe>();
                gameObject.AddComponent<OpenXRFloorOrigin>();
                gameObject.AddComponent<OpenXRInputDriver>();
                gameObject.AddComponent<OpenXRLiveResolutionWatch>();
            }

        }

        private bool InitializeVR()
        {
            // Both paths create SteamVR's action objects (OpenXR's VRInput falls back to them); once
            SteamVR_Actions.PreInitialize();
            if (VRSettings.GetUseOpenXR())
            {
                if (TryStartOpenXR())
                    return true;
                Logger.LogWarning("[VR] OpenXR didn't start (see the lines above): starting SteamVR's OpenVR instead. Its files ship in "
                    + "BepInEx\\plugins\\sptvr\\VR (Managed\\Unity.XR.OpenXR.dll + Unity.InputSystem.dll, Native\\UnityOpenXR.dll + openxr_loader.dll, "
                    + "Data\\UnitySubsystems\\UnityOpenXR). \"useOpenXR\": false in ModSettings.json starts OpenVR directly.");
            }
            return StartOpenVR();
        }

        // OpenXR never starts OpenVR. Without Unity.XR.OpenXR.dll the call itself throws (OpenXRBootstrap.Initialize can't be
        // compiled), so it is caught here rather than inside it.
        private static bool TryStartOpenXR()
        {
            try
            {
                return OpenXRBootstrap.Initialize();
            }
            catch (Exception exception)
            {
                MyLog.LogError($"[OpenXR] Couldn't load Unity's OpenXR plugin: {exception.GetType().Name}: {exception.Message}");
                return false;
            }
        }

        private bool StartOpenVR()
        {
            try
            {
                SteamVR_Settings.instance.pauseGameWhenDashboardVisible = false;

                var generalSettings = ScriptableObject.CreateInstance<XRGeneralSettings>();
                var managerSettings = ScriptableObject.CreateInstance<XRManagerSettings>();
                var xrLoader = ScriptableObject.CreateInstance<OpenVRLoader>();
                var settings = OpenVRSettings.GetSettings();
                settings.StereoRenderingMode = OpenVRSettings.StereoRenderingModes.SinglePassInstanced;

                generalSettings.Manager = managerSettings;

                managerSettings.loaders.Clear();
                managerSettings.loaders.Add(xrLoader);
                managerSettings.InitializeLoaderSync();

                XRGeneralSettings.AttemptInitializeXRSDKOnLoad();
                XRGeneralSettings.AttemptStartXRSDKOnBeforeSplashScreen();

                // SteamVR below connects without the XR plugin, so without this a missing display read as a successful start
                if (managerSettings.activeLoader == null)
                {
                    Plugin.MyLog.LogError("[OpenVR] The OpenVR XR plugin didn't start (see the [Subsystems] lines above): no headset display.");
                    return false;
                }

                XRSettings.useOcclusionMesh = true;

                SteamVR.Initialize();

                Plugin.MyLog.LogInfo("StereoRenderingMode:" + XRSettings.stereoRenderingMode);
                if (!SteamVR.active)
                {
                    Plugin.MyLog.LogError("[SteamVR] Initialization failed. SteamVR is not active.");
                    return false;
                }

                if (SteamVR.instance == null || SteamVR.instance.hmd == null)
                {
                    Plugin.MyLog.LogError("[OpenVR] HMD not found or OpenVR initialization failed.");
                    return false;
                }

                SteamVR_Input.Initialize();

                DetectAndLogControllers();

                InitializeActionSets();

                Plugin.MyLog.LogInfo("[VR] Initialization completed successfully.");
                return true;
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[VR Initialization Error] {exception.Message}");
                return false;
            }
        }

        private void DetectAndLogControllers()
        {
            try
            {
                if (SteamVR.instance == null || SteamVR.instance.hmd == null)
                {
                    Plugin.MyLog.LogError("[Controller Detection] SteamVR not initialized properly.");
                    return;
                }

                var system = OpenVR.System;
                if (system == null)
                {
                    Plugin.MyLog.LogError("[Controller Detection] OpenVR.System is null");
                    return;
                }

                Plugin.MyLog.LogInfo("[Controller Detection] Checking for connected devices...");

                for (uint deviceIndex = 0; deviceIndex < OpenVR.k_unMaxTrackedDeviceCount; deviceIndex++)
                {
                    if (system.IsTrackedDeviceConnected(deviceIndex))
                    {
                        ETrackedDeviceClass deviceClass = system.GetTrackedDeviceClass(deviceIndex);
                        if (deviceClass == ETrackedDeviceClass.Controller)
                        {
                            // A large buffer: some model strings exceed 64 chars
                            ETrackedPropertyError error = new ETrackedPropertyError();
                            StringBuilder modelNumber = new StringBuilder(256);
                            system.GetStringTrackedDeviceProperty(deviceIndex, ETrackedDeviceProperty.Prop_ModelNumber_String, modelNumber, 256, ref error);

                            // The render model name: a fallback identifier
                            StringBuilder renderModel = new StringBuilder(256);
                            system.GetStringTrackedDeviceProperty(deviceIndex, ETrackedDeviceProperty.Prop_RenderModelName_String, renderModel, 256, ref error);

                            string modelStr = modelNumber.ToString();
                            string renderStr = renderModel.ToString();
                            Plugin.MyLog.LogInfo($"[Controller Detection] Device {deviceIndex}: ModelNumber='{modelStr}' RenderModel='{renderStr}'");

                            // Case-insensitive matching — real strings seen in the wild:
                            //   "VIVE Controller Pro MV", "VIVE Controller MV", "vive_controller"
                            string modelLower = modelStr.ToLowerInvariant();
                            string renderLower = renderStr.ToLowerInvariant();

                            string controllerType = "Unknown";
                            if (modelLower.Contains("vive") || renderLower.Contains("vive_controller") || renderLower.Contains("vr_controller_vive"))
                            {
                                // Only set once — don't overwrite a confirmed type with Unknown
                                if (VRGlobals.vrControllerType != "vive")
                                    VRGlobals.vrControllerType = "vive";
                                controllerType = "Vive Wand";
                            }
                            else if (modelLower.Contains("knuckles") || renderLower.Contains("knuckles"))
                            {
                                if (string.IsNullOrEmpty(VRGlobals.vrControllerType))
                                    VRGlobals.vrControllerType = "index";
                                controllerType = "Valve Index Knuckles";
                            }
                            else if (modelLower.Contains("oculus touch") || renderLower.Contains("oculus"))
                            {
                                if (string.IsNullOrEmpty(VRGlobals.vrControllerType))
                                    VRGlobals.vrControllerType = "oculus";
                                controllerType = "Oculus Touch";
                            }

                            ETrackedControllerRole role = system.GetControllerRoleForTrackedDeviceIndex(deviceIndex);
                            Plugin.MyLog.LogInfo($"[Controller Detection] Found controller ({deviceIndex}): {modelStr} → type='{controllerType}', role={role}");
                        }
                    }
                }

                Plugin.MyLog.LogInfo($"[Controller Detection] Final vrControllerType='{VRGlobals.vrControllerType ?? "null"}'");
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[Controller Detection Error] {exception.Message}");
            }
        }

        private void InitializeActionSets()
        {
            try
            {
                SteamVR_ActionSet[] actionSets = SteamVR_Input.actionSets;
                if (actionSets != null && actionSets.Length > 0)
                {
                    foreach (var actionSet in actionSets)
                    {
                        Plugin.MyLog.LogInfo($"[ActionSet] Initializing action set: {actionSet.GetShortName()}");
                        actionSet.Activate();
                    }

                    var defaultActionSet = SteamVR_Input.GetActionSet("default");
                    if (defaultActionSet != null)
                    {
                        defaultActionSet.Activate();
                        Plugin.MyLog.LogInfo("[ActionSet] Default action set activated");
                    }
                }
                else
                {
                    Plugin.MyLog.LogWarning("[ActionSet] No action sets found to initialize");
                }

                // Forced update so the controllers are recognized
                SteamVR_Input.Update();
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[ActionSet Initialization Error] {exception.Message}");
            }
        }

        private void InitializeConditionalPatches()
        {
            if (!vrInitializedSuccessfully)
                return;

            InstalledMods.ORBITInstalled = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.chazut.orbit");
            MyLog.LogInfo(InstalledMods.ORBITInstalled
                ? "ORBIT detected — dragged corpses will relocate their loot waypoints."
                : "ORBIT not installed — corpse loot-relocation limited to vanilla DeadBodiesController.");

            InstalledMods.EFTApiInstalled = ApplyModSupport("com.kmyuhkyuk.KmyTarkovConfiguration", "KmyTarkovConfiguration (EFT API)", "TarkovVR.ModSupport.EFTApi");
            InstalledMods.AmandsGraphicsInstalled = ApplyModSupport("com.Amanda.Graphics", "AmandsGraphics", "TarkovVR.ModSupport.AmandsGraphics");
            InstalledMods.FIKAInstalled = ApplyModSupport("com.fika.core", "FIKA", "TarkovVR.ModSupport.FIKA");
            if (InstalledMods.FIKAInstalled)
            {
                // The custom-packet sync (arm poses + body-drag) lives in the standalone SPT-VR-FikaSync.dll, which starts through its
                // own plugin so flatscreen / headless peers register, relay and render the packets too (a peer without them fails on
                // "Undefined packet"). We only give it the VR-side hooks for the body-drag steal arbitration.
                try
                {
                    BindFikaSyncHooks();
                }
                catch (Exception exception)
                {
                    MyLog.LogError($"SPT-VR-FikaSync.dll missing or failed to bind ({exception.Message}). Install it next to SPT-VR.dll for co-op sync.");
                }
                // Its own call: an older module (before 2.2.0) has no door sync, and the rest still binds
                try
                {
                    BindFikaDoorHook();
                }
                catch (Exception exception)
                {
                    Source.Player.Interactions.PhysicalDoors.syncBroken = true;
                    MyLog.LogError($"SPT-VR-FikaSync.dll is older than 2.2.0 ({exception.Message}): doors moved by hand aren't synced.");
                }
            }
            InstalledMods.DynamicMapsInstalled = ApplyModSupport("com.mpstark.dynamicmaps", "DynamicMaps", "TarkovVR.ModSupport.DynamicMaps");
            InstalledMods.WeaponCustomizerInstalled = ApplyModSupport("com.tyfon.weaponcustomizer", "WeaponCustomizer", "TarkovVR.ModSupport.WeaponCustomizer");

            // FuckThisCulling: the eyes take turns in its visibility test (StereoCullingPatches). Patched against its private
            // internals, so only the version it was written for.
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue("com.rootdarkarchon.fuckthisculling", out var cullingInfo))
            {
                Version cullingVersion = cullingInfo.Metadata.Version;
                if (cullingVersion == new Version(0, 4, 1))
                {
                    try
                    {
                        ApplyPatches("TarkovVR.ModSupport.FuckThisCullingSupport");
                        InstalledMods.FuckThisCullingInstalled = true;
                        MyLog.LogInfo($"FuckThisCulling {cullingVersion} found: stereo support applied (the eyes take turns in its visibility test).");
                    }
                    catch (Exception exception)
                    {
                        MyLog.LogError($"FuckThisCulling {cullingVersion}: stereo support failed to apply, objects only the right eye sees may be hidden: {exception}");
                    }
                }
                else
                    MyLog.LogWarning($"FuckThisCulling {cullingVersion}: SPT-VR's stereo support was written for 0.4.1 and is NOT applied - objects only the right eye sees may be hidden.");
            }
        }

        // Another mod's VR support: found through BepInEx's registry by its plugin ID, wherever its DLL sits (each is a soft dependency
        // above, so it's loaded and registered before us). Patches that fail (another version of the mod) are logged and the rest of
        // our start carries on. True = installed.
        private bool ApplyModSupport(string pluginId, string modName, string patchNamespace)
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(pluginId, out PluginInfo modInfo))
            {
                MyLog.LogInfo($"{modName} not installed, its VR support isn't applied.");
                return false;
            }
            try
            {
                ApplyPatches(patchNamespace);
                MyLog.LogInfo($"{modName} {modInfo.Metadata.Version} found: VR support applied.");
            }
            catch (Exception exception)
            {
                MyLog.LogError($"{modName} {modInfo.Metadata.Version}: VR support failed to apply (another version of it?): {exception}");
            }
            return true;
        }

        // Binds the body-drag steal-arbitration hooks into the FikaSync companion module. Its own NoInlining method, only called when
        // FIKA is detected: inlined into Awake, the SptVrFikaSync (SPT-VR-FikaSync.dll) references would bind that assembly at Awake's
        // JIT and throw at startup when the companion DLL isn't installed (solo, no FIKA).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BindFikaSyncHooks()
        {
            SptVrFikaSync.FikaVrSync.getLocalDraggedCorpseNetId = () => Source.Player.Interactions.HandsInteractionController.localDraggedCorpseNetId;
            SptVrFikaSync.FikaVrSync.getLocalBodyGrabTime = () => Source.Player.Interactions.HandsInteractionController.bodyGrabTime;
            SptVrFikaSync.FikaVrSync.onYieldBodyDrag = () => VRGlobals.handsInteractionController?.RelinquishBodyDrag();
            // Corpse loot-AI sync runs inside the companion (on the headless/host, where ORBIT lives; SPT-VR.dll does not). The VR mod
            // only sends the grab/settle markers via FikaVrSync.SendCorpseLoot* from HandsInteractionController: no callback needed here.
        }

        // A door our hand moves (PhysicalDoors): another VR player's packets for it are left alone here
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BindFikaDoorHook()
        {
            SptVrFikaSync.FikaVrSync.isDoorDrivenLocally = door => Source.Player.Interactions.PhysicalDoors.DrivesDoor(door);
        }

        private void ApplyPatches(string @namespace)
        {
            if (!vrInitializedSuccessfully)
                return;

            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            var assembly = Assembly.GetExecutingAssembly();
            VRGlobals.harmonyInstance = harmony;
            foreach (var type in assembly.GetTypes())
            {
                if (type.Namespace != null && type.Namespace.StartsWith(@namespace))
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
            }
            
        }
    }
}
