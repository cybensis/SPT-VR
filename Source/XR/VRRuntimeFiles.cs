using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TarkovVR.Source.XR
{
    // Everything VR needs ships in BepInEx/plugins/sptvr/VR - nothing in the game's folders is replaced:
    //   Managed/ SteamVR, XR Management, the OpenVR / OpenXR plugins, Input System: BepInEx resolves them from the plugins folder
    //   Native/  openvr_api, XRSDKOpenVR, openxr_loader, UnityOpenXR: loaded from here before VR starts, so anything asking for
    //            them by name gets these (if the game's Plugins/x86_64 has a copy, that one is used and ours isn't loaded)
    //   Data/    the files only read from the game's data folder (VRDataFiles). SPT-VR-Preloader.dll writes them before Unity
    //            reads the manifests; this plugin writes them again as a backup for an install without the preloader.
    internal static class VRRuntimeFiles
    {
        // Dependencies first
        private static readonly string[] nativeLibraries = { "openvr_api.dll", "XRSDKOpenVR.dll", "openxr_loader.dll", "UnityOpenXR.dll" };
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        public static bool RestartNeeded { get; private set; }   // a subsystem manifest was written this start

        private static string ModVRFolder => Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "VR");
        private static string GamePluginsFolder => Path.Combine(Application.dataPath, "Plugins", "x86_64");

        public static void Prepare()
        {
            string folder = ModVRFolder;
            if (!Directory.Exists(folder))
                return;   // an install with everything in the game's folders (the 1.x layout) still works
            RestartNeeded = VRDataFiles.Apply(Application.dataPath, folder, Plugin.MyLog.LogInfo, Plugin.MyLog.LogError);
            if (RestartNeeded)
            {
                Plugin.MyLog.LogWarning("[VR files] VR plugin manifests in EscapeFromTarkov_Data\\UnitySubsystems were added or updated: Unity "
                    + "only reads them when the game starts, so restart the game once to start in VR (BepInEx\\patchers\\SPT-VR-Preloader.dll "
                    + "does this before Unity reads them - is it installed?)");
                return;
            }
            LoadNativeLibraries(Path.Combine(folder, "Native"));
        }

        private static void LoadNativeLibraries(string nativeFolder)
        {
            foreach (string library in nativeLibraries)
            {
                string path = Path.Combine(nativeFolder, library);
                if (!File.Exists(path) || File.Exists(Path.Combine(GamePluginsFolder, library)))
                    continue;
                if (LoadLibraryEx(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH) == IntPtr.Zero)
                    Plugin.MyLog.LogError($"[VR files] loading {library} from the mod folder failed (Windows error {Marshal.GetLastWin32Error()})");
            }
        }
    }
}
