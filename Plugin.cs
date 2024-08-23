using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using TarkovVR.ModSupport;
using Unity.XR.OpenVR;
using UnityEngine;
using UnityEngine.XR.Management;
using Valve.VR;


namespace TarkovVR;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin
{
    public static ManualLogSource MyLog;

    private void Awake()
    {
        // Plugin startup logic
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");
        MyLog = Logger;

        ApplyPatches("TarkovVR.Patches");

        //Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());


        SteamVR_Actions.PreInitialize();
        InitializeConditionalPatches();

        SteamVR_Settings.instance.pauseGameWhenDashboardVisible = true;

        var generalSettings = ScriptableObject.CreateInstance<XRGeneralSettings>();
        var managerSettings = ScriptableObject.CreateInstance<XRManagerSettings>();
        var xrLoader = ScriptableObject.CreateInstance<OpenVRLoader>();


        var settings = OpenVRSettings.GetSettings();
        settings.StereoRenderingMode = OpenVRSettings.StereoRenderingModes.MultiPass;
        generalSettings.Manager = managerSettings;

        managerSettings.loaders.Clear();
        managerSettings.loaders.Add(xrLoader);
        managerSettings.InitializeLoaderSync(); ;

        XRGeneralSettings.AttemptInitializeXRSDKOnLoad();
        XRGeneralSettings.AttemptStartXRSDKOnBeforeSplashScreen();

        SteamVR.Initialize();
        //0.0688 -0.2245 -0.0326
        //354.4751 187.1817 105.2293

    }


    private void InitializeConditionalPatches()
    {

        string modDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\kmyuhkyuk-EFTApi\\EFTConfiguration.dll");

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
            MyLog.LogWarning("Dependent mod DLL not found. Some functionality will be disabled.");
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
                MyLog.LogInfo("Dependent mod found and patches applied.");
            }
            else
            {
                MyLog.LogWarning("Required types/methods not found in the dependent mod.");
            }
        }
        else
        {
            MyLog.LogWarning("Dependent mod DLL not found. Some functionality will be disabled.");
        }
    }


    private void ApplyPatches(string @namespace)
    {
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
