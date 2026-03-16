using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using TarkovVR.ModSupport;
using TarkovVR.Patches.Visuals;
using Unity.XR.OpenVR;
using UnityEngine;
using UnityEngine.XR.Management;
using Valve.VR;
using static TarkovVR.Patches.Visuals.VisualPatches;
using UnityEngine.XR;
using System.Text;

namespace TarkovVR
{
    [BepInPlugin("com.matsix.sptvr", "matsix-sptvr", "1.2.5")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;
        private bool vrInitializedSuccessfully = false;
        public static GameObject WeaponModdingAnchor { get; set; }

        private void Awake()
        {
            Logger.LogInfo($"Plugin SPT-VR is loading!");
            MyLog = Logger;

            if (!InitializeVR())
            {             
                Logger.LogError("VR initialization failed. Skipping the rest of the plugin setup.");
                return;
            }

            Logger.LogInfo("SPTVR initialized successfully.");
            vrInitializedSuccessfully = true;

            ApplyPatches("TarkovVR.Patches");
            InitializeConditionalPatches();
            
        }

        private bool InitializeVR()
        {
            try
            {
                
                SteamVR_Actions.PreInitialize();
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

            /*string modDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\kmyuhkyuk-KmyTarkovApi\\KmyTarkovConfiguration.dll");

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
            }*/

            string modDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\AmandsGraphics.dll");

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
            
            // Repeat for other mods (AmandsGraphics, FIKA) as needed
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
