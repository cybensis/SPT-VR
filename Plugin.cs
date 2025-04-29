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

namespace TarkovVR
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource MyLog;
        private bool vrInitializedSuccessfully = false;
        public static GameObject WeaponModdingAnchor { get; set; }

        private void Awake()
        {
            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loading!");
            MyLog = Logger;

            if (!InitializeVR())
            {
                Logger.LogError("VR initialization failed. Skipping the rest of the plugin setup.");
                return;
            }

            Logger.LogInfo("VR initialized successfully.");
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

                // Initialize SteamVR
                SteamVR.Initialize();

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

                Plugin.MyLog.LogError("[VR] Initialization completed successfully with Single Pass Instanced.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[VR Initialization Error] {ex.Message}");
                return false;
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

            modDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\Fika.Core.dll");

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
