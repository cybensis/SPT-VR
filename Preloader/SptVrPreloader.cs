using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;
using TarkovVR.Source.XR;

namespace TarkovVR.Preloader
{
    // BepInEx runs preloader patchers before the engine reads EscapeFromTarkov_Data/UnitySubsystems. The SPT-VR plugin starts long
    // after that, so a manifest written by the plugin only takes effect on the next start. This writes the VR files in time.
    // It patches no assembly.
    public static class SptVrPreloader
    {
        public static IEnumerable<string> TargetDLLs { get; } = new string[0];

        public static void Patch(AssemblyDefinition assembly) { }

        public static void Initialize()
        {
            ManualLogSource log = Logger.CreateLogSource("SPT-VR Preloader");
            try
            {
                string vrFolder = FindModVRFolder();
                if (vrFolder == null)
                {
                    log.LogWarning("SPT-VR's VR folder (next to SPT-VR.dll in BepInEx\\plugins) not found: nothing to prepare");
                    return;
                }
                VRDataFiles.Apply(Path.GetDirectoryName(Paths.ManagedPath), vrFolder, log.LogInfo, log.LogError);
            }
            catch (Exception ex)
            {
                log.LogError($"Preparing the VR files failed (the plugin retries, VR then starts from the next start): {ex}");
            }
        }

        // The VR folder next to SPT-VR.dll, whatever the plugin's folder is named
        private static string FindModVRFolder()
        {
            foreach (string plugin in Directory.GetFiles(Paths.PluginPath, "SPT-VR.dll", SearchOption.AllDirectories))
            {
                string vrFolder = Path.Combine(Path.GetDirectoryName(plugin), "VR");
                if (Directory.Exists(vrFolder))
                    return vrFolder;
            }
            return null;
        }
    }
}
