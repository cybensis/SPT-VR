using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEngine;

namespace TarkovVR.Source.XR
{
    // Everything VR needs ships in BepInEx/plugins/sptvr/VR - nothing in the game's folders is replaced:
    //   Managed/ SteamVR, XR Management, the OpenVR / OpenXR plugins, Input System: BepInEx resolves them from the plugins folder
    //   Native/  openvr_api, XRSDKOpenVR, openxr_loader, UnityOpenXR: loaded from here before VR starts, so anything asking for
    //            them by name gets these (a copy in the game's Plugins/x86_64 wins: it isn't loaded twice)
    //   Data/    what Unity and SteamVR only read from the game's data folder, copied there when missing:
    //            StreamingAssets/SteamVR/* (XRSDKOpenVR reads OpenVRSettings.asset, SteamVR its action manifest and bindings - both
    //            when VR starts, so a copy works right away) and UnitySubsystems/<plugin>/UnitySubsystemsManifest.json
    // The XR plugins (UnityOpenXR, XRSDKOpenVR) must ALSO be loaded by Unity itself: it hands them the engine's interfaces as it
    // loads them, and a copy we load never gets that. Unity finds a manifest's "libraryName" by joining it to
    // EscapeFromTarkov_Data/Plugins/x86_64 (then Plugins), so the manifest names our file by a path relative to that folder.
    // Unity reads the manifests as the engine starts, before any mod: one written or changed this start counts from the next.
    internal static class VRRuntimeFiles
    {
        // Dependencies first
        private static readonly string[] nativeLibraries = { "openvr_api.dll", "XRSDKOpenVR.dll", "openxr_loader.dll", "UnityOpenXR.dll" };
        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;
        private static readonly Regex libraryNameField = new Regex("(\"libraryName\"\\s*:\\s*\")([^\"]*)(\")");

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
            string nativeFolder = Path.Combine(folder, "Native");
            CopyDataFiles(Path.Combine(folder, "Data"), nativeFolder);
            if (!RestartNeeded)
                LoadNativeLibraries(nativeFolder);
        }

        private static void CopyDataFiles(string dataFolder, string nativeFolder)
        {
            if (!Directory.Exists(dataFolder))
                return;
            foreach (string source in Directory.GetFiles(dataFolder, "*", SearchOption.AllDirectories))
            {
                string relative = source.Substring(dataFolder.Length).TrimStart('\\', '/');
                string target = Path.Combine(Application.dataPath, relative);
                try
                {
                    if (relative.StartsWith("UnitySubsystems", StringComparison.OrdinalIgnoreCase))
                        WriteSubsystemManifest(source, target, relative, nativeFolder);
                    else if (!File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(source, target);
                        Plugin.MyLog.LogInfo($"[VR files] {relative} added to the game's data folder");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.MyLog.LogError($"[VR files] couldn't add {relative} to the game's data folder: {ex.Message}");
                }
            }
            if (RestartNeeded)
                Plugin.MyLog.LogWarning("[VR files] VR plugin manifests in EscapeFromTarkov_Data\\UnitySubsystems were added or updated: Unity "
                    + "only reads them when the game starts, so restart the game once to start in VR");
        }

        // The manifest names the game's own copy of the plugin when Plugins/x86_64 has one (the managed side loads that one by name,
        // so both must be the same file), else ours. Only "libraryName" is ever changed in an existing manifest.
        private static void WriteSubsystemManifest(string source, string target, string relative, string nativeFolder)
        {
            string shipped = File.ReadAllText(source);
            Match shippedName = libraryNameField.Match(shipped);
            if (!shippedName.Success)
                return;
            string library = shippedName.Groups[2].Value;
            string wanted = library;
            if (!File.Exists(Path.Combine(GamePluginsFolder, library + ".dll")) && File.Exists(Path.Combine(nativeFolder, library + ".dll")))
                wanted = RelativePath(GamePluginsFolder, Path.Combine(nativeFolder, library));

            string current = File.Exists(target) ? File.ReadAllText(target) : null;
            Match currentName = current != null ? libraryNameField.Match(current) : Match.Empty;
            if (currentName.Success && currentName.Groups[2].Value == wanted)
                return;
            string content = currentName.Success ? current : shipped;
            content = libraryNameField.Replace(content, m => m.Groups[1].Value + wanted + m.Groups[3].Value, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, content);
            RestartNeeded = true;
            Plugin.MyLog.LogInfo($"[VR files] {relative}: plugin library set to {wanted}");
        }

        // Forward slashes, like Unity's own paths
        private static string RelativePath(string fromFolder, string toPath)
        {
            Uri from = new Uri(Path.GetFullPath(fromFolder).TrimEnd('\\', '/') + "\\");
            Uri to = new Uri(Path.GetFullPath(toPath));
            return Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString());
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
