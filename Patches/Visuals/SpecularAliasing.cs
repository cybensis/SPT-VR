using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class SpecularAliasing
    {
        public static readonly HashSet<string> TargetShaders = new HashSet<string>
        {
        "Standard",
        "Standard (Specular setup)",
        "p0/Reflective/Specular",
        "p0/Reflective/Bumped Specular",
        "p0/Reflective/Bumped Specular SMap",
        "p0/Reflective/Bumped Specular SMap_Decal",
        "p0/Reflective/Bumped Specular SMap Transparent Cutoff",
        "p0/Reflective/Bumped Emissive Specular SMap",
        "p0/Reflective/Bumped Specular SMap Parallax",
        "Legacy Shaders/Transparent/Cutout/Bumped Specular",
        "Nature/SpeedTreeEFT",
        "Custom/Vert",
        "Decal/Ultra"
        };
        private const bool DEBUG_NUKE = false;
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "ShowRaidStartInfo")]
        private static void OnRaidStart(PreloaderUI __instance)
        {
            SpecularAliasing.ApplyVRMaterialTweaks();
        }
        */
        public static void ApplyVRMaterialTweaks()
        {
            try
            {
                Renderer[] renderers = GameObject.FindObjectsOfType<Renderer>();
                int tweakedMats = 0;
                int logged = 0;

                foreach (var r in renderers)
                {
                    var mats = r.sharedMaterials;
                    if (mats == null) continue;

                    foreach (var mat in mats)
                    {
                        if (mat == null || mat.shader == null) continue;

                        if (TweakMaterialForVR(mat, ref logged))
                            tweakedMats++;
                    }
                }

                Plugin.MyLog.LogError($"[SpecularAliasing] VR tweaks applied to {tweakedMats} materials.");
            }
            catch (System.Exception ex)
            {
                Plugin.MyLog.LogError("[SpecularAliasing] Error applying VR tweaks: " + ex);
            }
        }

        private static bool TweakMaterialForVR(Material mat, ref int logged)
        {
            bool changed = false;

            string shaderName = mat.shader.name;
            string materialName = mat.name;

            // === DEBUG MODE: make it *painfully* obvious ===
            if (DEBUG_NUKE)
            {
                if (mat.HasProperty("_Smoothness")) { mat.SetFloat("_Smoothness", 0.0f); changed = true; }
                if (mat.HasProperty("_Glossiness")) { mat.SetFloat("_Glossiness", 0.0f); changed = true; }
                if (mat.HasProperty("_Metallic")) { mat.SetFloat("_Metallic", 0.0f); changed = true; }
                if (mat.HasProperty("_NormalScale")) { mat.SetFloat("_NormalScale", 0.1f); changed = true; }
                if (mat.HasProperty("_BumpScale")) { mat.SetFloat("_BumpScale", 0.1f); changed = true; }

                if (changed && logged < 40)
                {
                    logged++;
                    Plugin.MyLog.LogError(
                        $"[SpecularAliasing] NUKE mat '{materialName}' (shader '{shaderName}')");
                }
                return changed;
            }

            // === NORMAL MODE: global clamps that still keep the game looking okay-ish ===

            if (mat.HasProperty("_Smoothness"))
            {
                float old = mat.GetFloat("_Smoothness");
                float max = 0.45f;
                float val = Mathf.Min(old, max);
                if (!Mathf.Approximately(old, val))
                {
                    mat.SetFloat("_Smoothness", val);
                    changed = true;
                }
            }

            if (mat.HasProperty("_Glossiness"))
            {
                float old = mat.GetFloat("_Glossiness");
                float max = 0.45f;
                float val = Mathf.Min(old, max);
                if (!Mathf.Approximately(old, val))
                {
                    mat.SetFloat("_Glossiness", val);
                    changed = true;
                }
            }

            if (mat.HasProperty("_Metallic"))
            {
                float old = mat.GetFloat("_Metallic");
                float max = 0.6f;
                float val = Mathf.Min(old, max);
                if (!Mathf.Approximately(old, val))
                {
                    mat.SetFloat("_Metallic", val);
                    changed = true;
                }
            }

            if (mat.HasProperty("_NormalScale"))
            {
                float old = mat.GetFloat("_NormalScale");
                float max = 0.5f;
                float val = Mathf.Min(old, max);
                if (!Mathf.Approximately(old, val))
                {
                    mat.SetFloat("_NormalScale", val);
                    changed = true;
                }
            }

            if (mat.HasProperty("_BumpScale"))
            {
                float old = mat.GetFloat("_BumpScale");
                float max = 0.5f;
                float val = Mathf.Min(old, max);
                if (!Mathf.Approximately(old, val))
                {
                    mat.SetFloat("_BumpScale", val);
                    changed = true;
                }
            }

            if (mat.HasProperty("_SpecColor"))
            {
                Color sc = mat.GetColor("_SpecColor");
                float maxChannel = Mathf.Max(sc.r, Mathf.Max(sc.g, sc.b));
                if (maxChannel > 1.0f)
                {
                    float scale = 1.0f / maxChannel;
                    sc *= 0.8f * scale; // bring into sane range
                    mat.SetColor("_SpecColor", sc);
                    changed = true;
                }
            }

            if (changed && logged < 40)
            {
                logged++;
                Plugin.MyLog.LogError(
                    $"[SpecularAliasing] Tweaked mat '{materialName}' (shader '{shaderName}')");
            }

            return changed;
        }
        //Random attempts to try and reduce specular aliasing, nothing worked
        //-matsix
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "ShowRaidStartInfo")]
        private static void ClampSpecularAliasOnRender(PreloaderUI __instance)
        {
            var renderers = GameObject.FindObjectsOfType<Renderer>();
            int totalModified = 0;

            string[] propertiesToClamp = new[]
            {
                "_NormalScale", "_NormalIntensity", "_BumpScale", "_Smoothness", "_Glossiness", "_Glossness",
                "_Shininess", "_SpecPower", "_Specularness", "_SpecularHighlights", "_ReflectionIntensity",
                "_GlossyReflections"
            };

            string[] colorOverrides = new[]
{
                "_SpecColor", "_ReflectColor"
            };

            foreach (var renderer in renderers)
            {
                bool modifiedAny = false;
                var propertyBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(propertyBlock);

                var sharedMaterials = renderer.sharedMaterials;

                for (int i = 0; i < sharedMaterials.Length; i++)
                {
                    var mat = sharedMaterials[i];
                    if (mat == null || mat.shader == null)
                        continue;

                    if (!TargetShaders.Contains(mat.shader.name))
                        continue;

                    foreach (var prop in propertiesToClamp)
                    {
                        if (mat.HasProperty(prop))
                        {
                            float sourceValue = mat.GetFloat(prop);
                            float clamped = Mathf.Min(sourceValue, 0f);

                            // Always set it — MPB will override material value
                            propertyBlock.SetFloat(prop, clamped);
                            modifiedAny = true;
                        }
                    }
                    foreach (var prop in colorOverrides)
                    {
                        if (mat.HasProperty(prop))
                        {
                            propertyBlock.SetColor(prop, Color.black);
                            modifiedAny = true;
                        }
                    }
                }

                if (modifiedAny)
                {
                    totalModified++;
                    renderer.SetPropertyBlock(propertyBlock);
                }
            }

            Plugin.MyLog.LogError($"[SpecClamp] Finished. Modified {totalModified} renderers.");
        }
        */

        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "ShowRaidStartInfo")]
        private static void LogAllUniqueMaterials()
        {
            var seenMaterials = new HashSet<Material>();
            var renderers = GameObject.FindObjectsOfType<Renderer>();

            int totalLogged = 0;

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.materials)
                {
                    if (mat == null || mat.shader == null)
                        continue;

                    if (!seenMaterials.Add(mat))
                        continue; // Already seen

                    string matName = mat.name;
                    string shaderName = mat.shader.name;
                    Plugin.MyLog.LogError($"[MaterialLog] Shader: {shaderName}, Material: {matName}");

                    int propertyCount = mat.shader.GetPropertyCount();
                    for (int i = 0; i < propertyCount; i++)
                    {
                        var propName = mat.shader.GetPropertyName(i);
                        var propType = mat.shader.GetPropertyType(i);

                        string value = propType switch
                        {
                            ShaderPropertyType.Float => mat.GetFloat(propName).ToString(),
                            ShaderPropertyType.Range => mat.GetFloat(propName).ToString(),
                            ShaderPropertyType.Color => mat.GetColor(propName).ToString(),
                            ShaderPropertyType.Vector => mat.GetVector(propName).ToString(),
                            ShaderPropertyType.Texture => mat.GetTexture(propName)?.name ?? "null",
                            _ => "(unknown type)"
                        };

                        Plugin.MyLog.LogError($"    [{propType}] {propName}: {value}");
                    }

                    totalLogged++;
                }
            }

            Plugin.MyLog.LogError($"[MaterialLog] Total unique materials logged: {totalLogged}");
        }
        */
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "ShowRaidStartInfo")]
        private static void LogAllUniqueShaders()
        {
            var seenShaders = new HashSet<Shader>();
            var renderers = GameObject.FindObjectsOfType<Renderer>();

            int totalLogged = 0;

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.materials)
                {
                    if (mat == null || mat.shader == null)
                        continue;

                    Shader shader = mat.shader;

                    if (!seenShaders.Add(shader))
                        continue; // Already logged

                    string shaderName = shader.name;
                    Plugin.MyLog.LogError($"[ShaderLog] Shader: {shaderName}");

                    int propertyCount = shader.GetPropertyCount();
                    for (int i = 0; i < propertyCount; i++)
                    {
                        string propName = shader.GetPropertyName(i);
                        ShaderPropertyType propType = shader.GetPropertyType(i);

                        string displayValue = "(unknown)";
                        try
                        {
                            switch (propType)
                            {
                                case ShaderPropertyType.Float:
                                case ShaderPropertyType.Range:
                                    displayValue = mat.GetFloat(propName).ToString("0.#####");
                                    break;
                                case ShaderPropertyType.Color:
                                    displayValue = mat.GetColor(propName).ToString();
                                    break;
                                case ShaderPropertyType.Vector:
                                    displayValue = mat.GetVector(propName).ToString();
                                    break;
                                case ShaderPropertyType.Texture:
                                    var tex = mat.GetTexture(propName);
                                    displayValue = tex != null ? tex.name : "null";
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            displayValue = $"(error reading: {ex.Message})";
                        }

                        Plugin.MyLog.LogError($"    [{propType}] {propName}: {displayValue}");
                    }

                    totalLogged++;
                }
            }

            Plugin.MyLog.LogError($"[ShaderLog] Total unique shaders logged: {totalLogged}");
        }
        */
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LocationScene), "Awake")]
        private static void ClampNormalIntensityOnRender(LocationScene __instance)
        {
            //if (!__instance.name.Contains("Camera")) return;

            foreach (var renderer in GameObject.FindObjectsOfType<Renderer>())
            {
                var materials = renderer.sharedMaterials;
                var propertyBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(propertyBlock);

                bool modified = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null || mat.shader == null)
                        continue;

                    string shaderName = mat.shader.name;
                    if (!TargetShaders.Contains(shaderName))
                        continue;

                    // Clamp NormalScale (Adjust Normal Map Intensity)
                    if (mat.HasProperty("_NormalScale"))
                    {
                        float current = mat.GetFloat("_NormalScale");
                        float clampedValue = Mathf.Clamp(current, 0.5f, 1.0f);
                        if (current != clampedValue)
                        {
                            propertyBlock.SetFloat("_NormalScale", clampedValue);
                            modified = true;
                            Plugin.MyLog.LogError($"Clamped _NormalScale for {mat.name}: {current} -> {clampedValue}");
                        }
                    }

                    // Clamp BumpScale (Adjust Height Map Influence)
                    if (mat.HasProperty("_BumpScale"))
                    {
                        float current = mat.GetFloat("_BumpScale");
                        float clampedValue = Mathf.Clamp(current, 0.5f, 1.0f);
                        if (current != clampedValue)
                        {
                            propertyBlock.SetFloat("_BumpScale", clampedValue);
                            modified = true;
                            Plugin.MyLog.LogError($"Clamped _BumpScale for {mat.name}: {current} -> {clampedValue}");
                        }
                    }
                }

                if (modified)
                {
                    renderer.SetPropertyBlock(propertyBlock);
                }
            }
        }
        */
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Camera), nameof(Camera.Render))]
        private static void ClampSmoothnessOnRender(Camera __instance)
        {
            if (!__instance.name.Contains("Camera")) return;

            foreach (var renderer in GameObject.FindObjectsOfType<Renderer>())
            {
                var materials = renderer.sharedMaterials;
                var propertyBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(propertyBlock);

                bool modified = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null || mat.shader == null)
                        continue;

                    string shaderName = mat.shader.name;
                    if (!TargetShaders.Contains(shaderName))
                        continue;

                    if (mat.HasProperty("_Smoothness"))
                    {
                        float current = mat.GetFloat("_Smoothness");
                        if (current > 0.85f)
                        {
                            propertyBlock.SetFloat("_Smoothness", 0f);
                            modified = true;
                        }
                    }

                    if (mat.HasProperty("_Glossiness"))
                    {
                        float current = mat.GetFloat("_Glossiness");
                        if (current > 0.85f)
                        {
                            propertyBlock.SetFloat("_Glossiness", 0f);
                            modified = true;
                        }
                    }
                }

                if (modified)
                {
                    renderer.SetPropertyBlock(propertyBlock);
                }
            }
        }
        */
    }
}
