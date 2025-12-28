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
    [BepInPlugin("com.matsix.sptvr", "matsix-sptvr", "1.1.4")]
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
                            // Get controller type
                            ETrackedPropertyError error = new ETrackedPropertyError();
                            StringBuilder modelNumber = new StringBuilder(64);
                            system.GetStringTrackedDeviceProperty(deviceIndex, ETrackedDeviceProperty.Prop_ModelNumber_String, modelNumber, 64, ref error);

                            string controllerType = "Unknown";
                            if (modelNumber.ToString().Contains("Vive Controller"))
                            {
                                VRGlobals.vrControllerType = "vive";
                                controllerType = "Vive Wand";
                            }
                            else if (modelNumber.ToString().Contains("Knuckles"))
                            {
                                VRGlobals.vrControllerType = "index";
                                controllerType = "Valve Index Knuckles";
                            }
                            else if (modelNumber.ToString().Contains("Oculus Touch"))
                            {
                                VRGlobals.vrControllerType = "oculus";
                                controllerType = "Oculus Touch";
                            }

                            Plugin.MyLog.LogInfo($"[Controller Detection] Found controller ({deviceIndex}): {modelNumber} ({controllerType})");

                            // Log controller role (left/right)
                            ETrackedControllerRole role = system.GetControllerRoleForTrackedDeviceIndex(deviceIndex);
                            Plugin.MyLog.LogInfo($"[Controller Detection] Controller role: {role}");
                        }
                    }
                }
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
            // Repeat for other mods (AmandsGraphics, FIKA) as needed
        }

        private void ApplyPatches(string @namespace)
        {
            if (!vrInitializedSuccessfully)
                return; // Skip patching if VR failed to initialize

            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            var assembly = Assembly.GetExecutingAssembly();

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
