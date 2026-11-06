using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TarkovVR.Source.XR
{
    // Everything VR needs ships in BepInEx/plugins/sptvr/VR - nothing in the game's folders is replaced:
    //   Managed/ SteamVR, XR Management, the OpenVR / OpenXR plugins, Input System: BepInEx resolves them from the plugins folder
    //   Native/  openvr_api, XRSDKOpenVR, openxr_loader, UnityOpenXR: loaded from here before VR starts, so anything asking for
    //            them by name gets these (a copy in the game's Plugins/x86_64 wins: it isn't loaded twice)
    //   Data/    what Unity and SteamVR only read from the game's data folder, copied there when missing, never overwriting:
    //            UnitySubsystems/<plugin>/UnitySubsystemsManifest.json (Unity reads these as the engine starts, before any mod:
    //            a copied one takes effect after a restart) and StreamingAssets/SteamVR/* (XRSDKOpenVR reads OpenVRSettings.asset,
    //            SteamVR its action manifest and bindings - both when VR starts, so a copy works right away)
    internal static class VRRuntimeFiles
    {
        // Dependencies first
        private static readonly string[] nativeLibraries = { "openvr_api.dll", "XRSDKOpenVR.dll", "openxr_loader.dll", "UnityOpenXR.dll" };
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        public static bool RestartNeeded { get; private set; }   // a subsystem manifest was copied this start

        private static string ModVRFolder => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "VR");

        public static void Prepare()
        {
            string folder = ModVRFolder;
            if (!Directory.Exists(folder))
                return;   // an install with everything in the game's folders (the 1.x layout) still works
            CopyMissingDataFiles(Path.Combine(folder, "Data"));
            LoadNativeLibraries(Path.Combine(folder, "Native"));
        }

        private static void CopyMissingDataFiles(string dataFolder)
        {
            if (!Directory.Exists(dataFolder))
                return;
            foreach (string source in Directory.GetFiles(dataFolder, "*", SearchOption.AllDirectories))
            {
                string relative = source.Substring(dataFolder.Length).TrimStart('\\', '/');
                string target = Path.Combine(Application.dataPath, relative);
                if (File.Exists(target))
                    continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target);
                    Plugin.MyLog.LogInfo($"[VR files] {relative} added to the game's data folder");
                    if (relative.StartsWith("UnitySubsystems", StringComparison.OrdinalIgnoreCase))
                        RestartNeeded = true;
                }
                catch (Exception ex)
                {
                    Plugin.MyLog.LogError($"[VR files] couldn't add {relative} to the game's data folder: {ex.Message}");
                }
            }
            if (RestartNeeded)
                Plugin.MyLog.LogWarning("[VR files] VR plugin manifests were added to EscapeFromTarkov_Data\\UnitySubsystems: Unity only reads "
                    + "them when the game starts, so restart the game once to start in VR");
        }

        private static void LoadNativeLibraries(string nativeFolder)
        {
            string gamePlugins = Path.Combine(Application.dataPath, "Plugins", "x86_64");
            foreach (string library in nativeLibraries)
            {
                string path = Path.Combine(nativeFolder, library);
                if (!File.Exists(path) || File.Exists(Path.Combine(gamePlugins, library)))
                    continue;
                if (LoadLibraryEx(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH) == IntPtr.Zero)
                    Plugin.MyLog.LogError($"[VR files] loading {library} from the mod folder failed (Windows error {Marshal.GetLastWin32Error()})");
            }
        }
    }
}
