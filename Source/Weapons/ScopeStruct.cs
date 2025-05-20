using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;


namespace TarkovVR.Source.Weapons
{

    public class Scope
    {
        public string Name { get; set; }
        public float ZoomLevel { get; set; }
        public float FOV { get; set; }

        public Scope(string name, float zoomLevel, float fov)
        {
            Name = name;
            ZoomLevel = zoomLevel;
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
                // Check if the config file exists
                if (!File.Exists(configPath))
                {
                    Plugin.MyLog.LogWarning($"Scope configuration file not found: {configPath}");
                    return;
                }

                // Read the file and deserialize it into the list of scopes
                string json = File.ReadAllText(configPath);
                scopes = JsonConvert.DeserializeObject<List<Scope>>(json);

                Plugin.MyLog.LogInfo($"Loaded {scopes.Count} scopes from {configPath}");
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"Failed to load scopes: {ex.Message}");
            }
        }


        public static void AddScope(string name, float zoomLevel, float fov)
        {
            scopes.Add(new Scope(name, zoomLevel, fov));
        }

        public static float GetFOV(string name, float zoomLevel)
        {
            foreach (var scope in scopes)
            {
                if (scope.Name == name && Math.Abs(scope.ZoomLevel - zoomLevel) < 0.01f)
                {
                    return scope.FOV;
                }
            }
            // If the scope with the specified name and zoom level is not found, return a default FOV
            return -1f; 
        }

        public static float GetMinFOV(string name)
        {
            float minFOV = float.MaxValue;
            foreach (var scope in scopes)
            {
                if (scope.Name == name && scope.FOV < minFOV)
                {
                    minFOV = scope.FOV;
                }
            }
            return minFOV;
        }

        public static float GetMaxFOV(string name)
        {
            float maxFOV = float.MinValue;
            foreach (var scope in scopes)
            {
                if (scope.Name == name && scope.FOV > maxFOV)
                {
                    maxFOV = scope.FOV;
                }
            }
            return maxFOV;
        }
    }
}
