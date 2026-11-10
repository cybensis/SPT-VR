using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TarkovVR.Source.XR
{
    // The files from BepInEx/plugins/sptvr/VR/Data that Unity and SteamVR only read from the game's data folder:
    // StreamingAssets/SteamVR/* (copied when missing; read when VR starts) and UnitySubsystems/<plugin>/UnitySubsystemsManifest.json.
    // Unity reads the manifests as the engine starts, so SPT-VR-Preloader.dll (BepInEx preloader, before the engine reads them)
    // runs this first; the plugin runs it again as a backup, where a manifest it has to write only counts from the next start.
    // No Unity types here: the preloader runs before Unity's managed side exists.
    internal static class VRDataFiles
    {
        private static readonly Regex libraryNameField = new Regex("(\"libraryName\"\\s*:\\s*\")([^\"]*)(\")");

        // True when a subsystem manifest was written or changed
        public static bool Apply(string gameDataFolder, string modVRFolder, Action<string> info, Action<string> error)
        {
            string dataFolder = Path.Combine(modVRFolder, "Data");
            string nativeFolder = Path.Combine(modVRFolder, "Native");
            string gamePlugins = Path.Combine(gameDataFolder, "Plugins", "x86_64");
            bool manifestChanged = false;
            if (!Directory.Exists(dataFolder))
                return false;
            foreach (string source in Directory.GetFiles(dataFolder, "*", SearchOption.AllDirectories))
            {
                string relative = source.Substring(dataFolder.Length).TrimStart('\\', '/');
                string target = Path.Combine(gameDataFolder, relative);
                try
                {
                    if (relative.StartsWith("UnitySubsystems", StringComparison.OrdinalIgnoreCase))
                        manifestChanged |= WriteSubsystemManifest(source, target, relative, nativeFolder, gamePlugins, info);
                    else if (!File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(source, target);
                        info($"[VR files] {relative} added to the game's data folder");
                    }
                }
                catch (Exception exception)
                {
                    error($"[VR files] couldn't add {relative} to the game's data folder: {exception.Message}");
                }
            }
            return manifestChanged;
        }

        // Unity finds "libraryName" by joining it to EscapeFromTarkov_Data/Plugins/x86_64 (then Plugins), and must load the XR
        // plugin itself (it hands it the engine's interfaces as it loads it), so the manifest names our file by a path relative
        // to that folder. While the game's Plugins/x86_64 has its own copy, the plain name: the managed side loads that one by
        // name, and both must be the same file. Only "libraryName" is ever changed in an existing manifest.
        private static bool WriteSubsystemManifest(string source, string target, string relative, string nativeFolder, string gamePlugins,
            Action<string> info)
        {
            string shipped = File.ReadAllText(source);
            Match shippedName = libraryNameField.Match(shipped);
            if (!shippedName.Success)
                return false;
            string library = shippedName.Groups[2].Value;
            string wanted = library;
            if (!File.Exists(Path.Combine(gamePlugins, library + ".dll")) && File.Exists(Path.Combine(nativeFolder, library + ".dll")))
                wanted = RelativePath(gamePlugins, Path.Combine(nativeFolder, library));

            string current = File.Exists(target) ? File.ReadAllText(target) : null;
            Match currentName = current != null ? libraryNameField.Match(current) : Match.Empty;
            if (currentName.Success && currentName.Groups[2].Value == wanted)
                return false;
            string content = currentName.Success ? current : shipped;
            content = libraryNameField.Replace(content, m => m.Groups[1].Value + wanted + m.Groups[3].Value, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllText(target, content);
            info($"[VR files] {relative}: plugin library set to {wanted}");
            return true;
        }

        // Forward slashes, like Unity's own paths
        private static string RelativePath(string fromFolder, string toPath)
        {
            Uri from = new Uri(Path.GetFullPath(fromFolder).TrimEnd('\\', '/') + "\\");
            Uri to = new Uri(Path.GetFullPath(toPath));
            return Uri.UnescapeDataString(from.MakeRelativeUri(to).ToString());
        }
    }
}
