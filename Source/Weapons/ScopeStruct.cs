using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;


namespace TarkovVR.Source.Weapons
{
    public class Scope
    {
        public string Name { get; set; }
        public bool IsVariableZoom { get; set; }
        public float MinFOV { get; set; }
        public float MaxFOV { get; set; }
        public float FOV { get; set; } // For fixed zoom scopes

        public Scope(string name, bool isVariableZoom, float minFov, float maxFov, float fov)
        {
            Name = name;
            IsVariableZoom = isVariableZoom;
            MinFOV = minFov;
            MaxFOV = maxFov;
            FOV = fov;
        }
    }

    public static class ScopeManager
    {
        private static List<Scope> scopes = new List<Scope>();
        private static readonly string configPath = Path.Combine(BepInEx.Paths.PluginPath, "sptvr", "Configs", "scopes.json");

        static ScopeManager()
        {
            LoadScopes();
        }

        private static void LoadScopes()
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    Plugin.MyLog.LogWarning($"Scope configuration file not found: {configPath}");
                    return;
                }

                string json = File.ReadAllText(configPath);
                scopes = JsonConvert.DeserializeObject<List<Scope>>(json);

                Plugin.MyLog.LogInfo($"Loaded {scopes.Count} scopes from {configPath}");
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"Failed to load scopes: {ex.Message}");
            }
        }

        public static Scope GetScope(string name)
        {
            return scopes.FirstOrDefault(s => s.Name == name);
        }

        public static bool IsVariableZoom(string name)
        {
            var scope = GetScope(name);
            return scope != null && scope.IsVariableZoom;
        }

        public static float GetMinFOV(string name)
        {
            var scope = GetScope(name);
            if (scope == null) return float.MaxValue;

            return scope.MinFOV > 0 ? scope.MinFOV : scope.FOV;
        }

        public static float GetMaxFOV(string name)
        {
            var scope = GetScope(name);
            if (scope == null) return float.MinValue;

            return scope.MaxFOV > 0 ? scope.MaxFOV : scope.FOV;
        }
    }
}