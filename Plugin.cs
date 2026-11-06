using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using TarkovVR.ModSupport;
using TarkovVR.Patches.Visuals;
using Unity.XR.OpenVR;
using UnityEngine;
using UnityEngine.XR.Management;
using Valve.VR;
using static TarkovVR.Patches.Visuals.VisualPatches;
using UnityEngine.XR;
using System.Text;
using TarkovVR.Source.Settings;

using TarkovVR.Source.XR;
using TarkovVR.Source.Controls;

namespace TarkovVR
{
    [BepInPlugin("com.matsix.sptvr", "matsix-sptvr", "2.0.0")]
    // Soft: the FikaSync module only loads its plugin when FIKA is present, so we must NOT hard-depend
    // on it (that would break solo VR). We still ship its DLL alongside ours and load after it here.
    [BepInDependency("com.matsix.sptvr.fikasync", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft: ORBIT (bot-AI mod) is referenced (publicized DLL) so dragged corpses can relocate their loot
    // waypoints, but the VR mod must still load without it. The dependency only guarantees ORBIT loads
    // first so it's in the Chainloader registry when we detect it below.
    [BepInDependency("com.chazut.orbit", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft: FuckThisCulling (GPU occlusion culling) gets stereo support patches (ModSupport/FuckThisCulling) when installed
    [BepInDependency("com.rootdarkarchon.fuckthisculling", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;
        // Verbose logging for the dragged-corpse loot-relocation path (OrbitSupport): logs whether an ORBIT
        // waypoint / vanilla DeadBody record was found and where it was moved. Off by default; flip live.
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
            catch (Exception ex)
            {
                Logger.LogError($"[VR files] preparing the VR files failed: {ex}");
            }

            if (!InitializeVR())
            {
                Logger.LogError(VRRuntimeFiles.RestartNeeded
                    ? "VR didn't start because its plugin manifests were only just added to the game's data folder: restart the game once."
                    : "VR initialization failed. Skipping the rest of the plugin setup.");
                return;
            }

            Logger.LogInfo("SPTVR initialized successfully.");
            vrInitializedSuccessfully = true;

            ApplyPatches("TarkovVR.Patches");
            InitializeConditionalPatches();

            // Dev measurement tools, not added in releases: StereoCostProbe (Ctrl+F10, also needs OpenXRBootstrap.registerFrameWatch
            // for its OpenXR rows) and ShadowShareProbe (Ctrl+F8) - gameObject.AddComponent<TarkovVR.Source.Misc.StereoCostProbe>() etc.
            // The desktop window: one eye, cropped to the window and stabilised (for recording)
            gameObject.AddComponent<TarkovVR.Source.Graphics.DesktopMirror>();

            if (OpenXRBootstrap.Active)
            {
                gameObject.AddComponent<OpenXRProbe>();
                gameObject.AddComponent<OpenXRFloorOrigin>();
                gameObject.AddComponent<OpenXRInputDriver>();
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
                Logger.LogWarning("[VR] OpenXR didn't start (see the lines above): starting SteamVR's OpenVR instead. "
                    + (VRRuntimeFiles.RestartNeeded ? "Its manifest was only just added to the game's data folder: OpenXR works from the next start. "
                        : "Its files ship in BepInEx\\plugins\\sptvr\\VR (Managed\\Unity.XR.OpenXR.dll + Unity.InputSystem.dll, Native\\UnityOpenXR.dll + "
                        + "openxr_loader.dll, Data\\UnitySubsystems\\UnityOpenXR). ")
                    + "\"useOpenXR\": false in ModSettings.json starts OpenVR directly.");
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
            catch (Exception ex)
            {
                MyLog.LogError($"[OpenXR] Couldn't load Unity's OpenXR plugin: {ex.GetType().Name}: {ex.Message}");
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

                XRSettings.useOcclusionMesh = true;

                // Initialize SteamVR
                SteamVR.Initialize();

                Plugin.MyLog.LogInfo("StereoRenderingMode:" + XRSettings.stereoRenderingMode);
                // Verify SteamVR is running
                if (!SteamVR.active)
                {
                    Plugin.MyLog.LogError("[SteamVR] Initialization failed. SteamVR is not active.");
                    return false;
                }

                // Verify OpenVR initialization
                if (SteamVR.instance == null || SteamVR.instance.hmd == null)
                {
                    Plugin.MyLog.LogError("[OpenVR] HMD not found or OpenVR initialization failed.");
                    return false;
                }

                // Make sure SteamVR input is initialized
                SteamVR_Input.Initialize();

                // Check for controller presence and log controller types
                DetectAndLogControllers();

                // Initialize action sets - important for different controller types
                InitializeActionSets();

                Plugin.MyLog.LogInfo("[VR] Initialization completed successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[VR Initialization Error] {ex.Message}");
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

                // Get active controllers
                var system = OpenVR.System;
                if (system == null)
                {
                    Plugin.MyLog.LogError("[Controller Detection] OpenVR.System is null");
                    return;
                }

                // Log all connected devices
                Plugin.MyLog.LogInfo("[Controller Detection] Checking for connected devices...");

                for (uint deviceIndex = 0; deviceIndex < OpenVR.k_unMaxTrackedDeviceCount; deviceIndex++)
                {
                    if (system.IsTrackedDeviceConnected(deviceIndex))
                    {
                        ETrackedDeviceClass deviceClass = system.GetTrackedDeviceClass(deviceIndex);
                        if (deviceClass == ETrackedDeviceClass.Controller)
                        {
                            // Use a large buffer — some model strings exceed 64 chars
                            ETrackedPropertyError error = new ETrackedPropertyError();
                            StringBuilder modelNumber = new StringBuilder(256);
                            system.GetStringTrackedDeviceProperty(deviceIndex, ETrackedDeviceProperty.Prop_ModelNumber_String, modelNumber, 256, ref error);

                            // Also read the render model name as a fallback identifier
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
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[Controller Detection Error] {ex.Message}");
            }
        }

        private void InitializeActionSets()
        {
            try
            {
                // Make sure actions are initialized and updated
                SteamVR_ActionSet[] actionSets = SteamVR_Input.actionSets;
                if (actionSets != null && actionSets.Length > 0)
                {
                    foreach (var actionSet in actionSets)
                    {
                        Plugin.MyLog.LogInfo($"[ActionSet] Initializing action set: {actionSet.GetShortName()}");
                        actionSet.Activate();
                    }

                    // Explicitly activate the default action set if it exists
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

                // Force SteamVR Input update to ensure controllers are recognized
                SteamVR_Input.Update();
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[ActionSet Initialization Error] {ex.Message}");
            }
        }

        private void InitializeConditionalPatches()
        {
            if (!vrInitializedSuccessfully)
                return; // Skip patching if VR failed to initialize

            InstalledMods.ORBITInstalled = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.chazut.orbit");
            MyLog.LogInfo(InstalledMods.ORBITInstalled
                ? "ORBIT detected — dragged corpses will relocate their loot waypoints."
                : "ORBIT not installed — corpse loot-relocation limited to vanilla DeadBodiesController.");

            string modDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\kmyuhkyuk-KmyTarkovApi\\KmyTarkovConfiguration.dll");

            if (File.Exists(modDllPath))
            {
                // Load the assembly
                Assembly modAssembly = Assembly.LoadFrom(modDllPath);

                // Check for the required types and methods in the loaded assembly
                Type configViewType = modAssembly.GetType("EFTConfiguration.Views.EFTConfigurationView");
                if (configViewType != null)
                {
                    // Apply conditional patches
                    InstalledMods.EFTApiInstalled = true;
                    ApplyPatches("TarkovVR.ModSupport.EFTApi");
                    MyLog.LogInfo("Dependent mod found and patches applied.");
                }
                else
                {
                    MyLog.LogWarning("Required types/methods not found in the dependent mod.");
                }
            }
            else
            {
                MyLog.LogWarning("EFT API dll not found, support patches will not be applied.");
            }

            modDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\AmandsGraphics.dll");

            if (File.Exists(modDllPath))
            {
                // Load the assembly
                Assembly modAssembly = Assembly.LoadFrom(modDllPath);

                // Check for the required types and methods in the loaded assembly
                Type configViewType = modAssembly.GetType("AmandsGraphics.AmandsGraphicsClass");
                if (configViewType != null)
                {
                    // Apply conditional patches
                    InstalledMods.AmandsGraphicsInstalled = true;
                    ApplyPatches("TarkovVR.ModSupport.AmandsGraphics");
                    MyLog.LogInfo("AmandsGraphics found and patches applied.");
                }
                else
                {
                    MyLog.LogWarning("Required types/methods not found in the dependent mod.");
                }
            }
            else
            {
                MyLog.LogWarning("AmandsGraphics dll not found, support patches will not be applied.");
            }

            modDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\Fika\\Fika.Core.dll");

            if (File.Exists(modDllPath))
            {
                // Load the assembly
                Assembly modAssembly = Assembly.LoadFrom(modDllPath);

                // Check for the required types and methods in the loaded assembly
                Type configViewType = modAssembly.GetType("MatchMakerUI");
                if (configViewType != null)
                {
                    // Apply conditional patches
                    InstalledMods.FIKAInstalled = true;
                    ApplyPatches("TarkovVR.ModSupport.FIKA");
                    // The custom-packet sync (arm poses + body-drag) now lives in the standalone
                    // SPT-VR-FikaSync.dll module, which self-initializes via its OWN plugin so that
                    // flatscreen / headless peers can register + relay + render it too (that's the fix
                    // for the "Undefined packet" busy-hands crash). We only feed it the VR-side hooks it
                    // needs for the body-drag steal arbitration.
                    try
                    {
                        BindFikaSyncHooks();
                    }
                    catch (Exception e)
                    {
                        MyLog.LogError($"SPT-VR-FikaSync.dll missing or failed to bind ({e.Message}). Install it next to SPT-VR.dll for co-op sync.");
                    }
                    MyLog.LogInfo("Dependent mod found and patches applied.");
                }
                else
                {
                    MyLog.LogWarning("Required types/methods not found in the dependent mod.");
                }
            }
            else
            {
                MyLog.LogWarning("FIKA Core dll not found, support patches will not be applied.");
            }
            
            modDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\DynamicMaps\\DynamicMaps.dll");

            if (File.Exists(modDllPath))
            {
                // Load the assembly
                Assembly modAssembly = Assembly.LoadFrom(modDllPath);

                // Check for the required types and methods in the loaded assembly
                Type configViewType = modAssembly.GetType("DynamicMaps.Plugin");
                if (configViewType != null)
                {
                    // Apply conditional patches
                    InstalledMods.DynamicMapsInstalled = true;
                    ApplyPatches("TarkovVR.ModSupport.DynamicMaps");
                    MyLog.LogInfo("Dependent mod found and patches applied.");
                }
                else
                {
                    MyLog.LogWarning("Required types/methods not found in the dependent mod.");
                }
            }
            else
            {
                MyLog.LogWarning("DynamicMaps dll not found, support patches will not be applied.");
            }

            modDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\Tyfon.WeaponCustomizer.dll");

            if (File.Exists(modDllPath))
            {
                // Load the assembly
                Assembly modAssembly = Assembly.LoadFrom(modDllPath);

                // Check for the required types and methods in the loaded assembly
                Type configViewType = modAssembly.GetType("WeaponCustomizer.DraggableBone");
                if (configViewType != null)
                {
                    // Apply conditional patches
                    InstalledMods.WeaponCustomizerInstalled = true;
                    ApplyPatches("TarkovVR.ModSupport.WeaponCustomizer");
                    MyLog.LogInfo("Dependent mod found and patches applied.");
                }
                else
                {
                    MyLog.LogWarning("Required types/methods not found in the dependent mod.");
                }
            }
            else
            {
                MyLog.LogWarning("WeaponCustomizer dll not found, support patches will not be applied.");
            }

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
                    catch (Exception e)
                    {
                        MyLog.LogError($"FuckThisCulling {cullingVersion}: stereo support failed to apply, objects only the right eye sees may be hidden: {e}");
                    }
                }
                else
                    MyLog.LogWarning($"FuckThisCulling {cullingVersion}: SPT-VR's stereo support was written for 0.4.1 and is NOT applied - objects only the right eye sees may be hidden.");
            }
        }

        // Bind the body-drag steal-arbitration hooks into the FikaSync companion module. Isolated into
        // its own NoInlining method (only called from the FIKA-detected branch above) so the
        // SptVrFikaSync (SPT-VR-FikaSync.dll) references it contains are only JITted when FIKA is
        // actually present. Inlined into Awake, they'd bind the companion assembly at Awake's JIT and
        // throw at startup when the companion DLL isn't installed (the solo / no-FIKA deployment). Same
        // isolation pattern as HandsInteractionController.BodyGrab; see memory fika-softdep-no-typed-fields.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void BindFikaSyncHooks()
        {
            SptVrFikaSync.FikaVrSync.getLocalDraggedCorpseNetId = () => Source.Player.Interactions.HandsInteractionController.localDraggedCorpseNetId;
            SptVrFikaSync.FikaVrSync.getLocalBodyGrabTime = () => Source.Player.Interactions.HandsInteractionController.bodyGrabTime;
            SptVrFikaSync.FikaVrSync.onYieldBodyDrag = () => VRGlobals.handsInteractionController?.RelinquishBodyDrag();
            // Corpse loot-AI sync for co-op is handled inside the companion (it runs on the headless/host
            // where ORBIT lives; SPT-VR.dll does not). The VR mod just SENDS the grab/settle markers via
            // FikaVrSync.SendCorpseLoot* from HandsInteractionController — no callback needed here.
        }

        private void ApplyPatches(string @namespace)
        {
            if (!vrInitializedSuccessfully)
                return; // Skip patching if VR failed to initialize

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
