using Comfort.Common;
using EFT.CameraControl;
using EFT.Rendering.Clouds;
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;
using Valve.VR;
using EFT.BlitDebug;
using UnityStandardAssets.ImageEffects;
using static HBAO_Core;
using static UnityEngine.ParticleSystem.PlaybackState;
using static Val;
using EFT.Visual;
using Unity.XR.OpenVR;
using EFT.Weather;
using EFT.Settings.Graphics;
using System.IO;
using Unity.Audio;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class VisualPatches
    {
        private static Camera postProcessingStoogeCamera;
        public static DistantShadow distantShadow;
        public static readonly HashSet<string> TargetShaders = new HashSet<string>
        {
        "Standard",
        "Standard (Specular setup)",
        "p0/Reflective/Specular",
        "p0/Reflective/Bumped Specular",
        "p0/Reflective/Bumped Specular SMap",
        "p0/Reflective/Bumped Specular SMap_Decal",
        "Nature/SpeedTreeEFT",
        "Custom/Vert",
        "Decal/Ultra"
        };
        //Random attempts to try and reduce specular aliasing, nothing worked
        //-matsix
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterControllerSpawner), "Spawn")]
        private static void ClampSpecularAliasOnRender(CharacterControllerSpawner __instance)
        {


            var renderers = GameObject.FindObjectsOfType<Renderer>();

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;

                var propertyBlock = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(propertyBlock);

                bool modified = false;
                //string rendererPath = GetGameObjectPath(renderer.gameObject);

                for (int i = 0; i < materials.Length; i++)
                {
                    var mat = materials[i];
                    if (mat == null || mat.shader == null)
                    {
                        continue;
                    }

                    string shaderName = mat.shader.name;
                    if (!TargetShaders.Contains(shaderName))
                    {
                        continue;
                    }

                    // Clamp NormalScale (Adjust Normal Map Intensity)
                    if (mat.HasProperty("_NormalScale"))
                    {
                        float current = mat.GetFloat("_NormalScale");
                        float clampedValue = Mathf.Min(current, 0.2f);
                        if (current != clampedValue)
                        {
                            mat.SetFloat("_NormalScale", clampedValue);
                            modified = true;
                        }
                    }

                    // Clamp BumpScale (Adjust Height Map Influence)
                    if (mat.HasProperty("_BumpScale"))
                    {
                        float current = mat.GetFloat("_BumpScale");
                        float clampedValue = Mathf.Min(current, 0.2f);
                        if (current != clampedValue)
                        {
                            mat.SetFloat("_BumpScale", clampedValue);
                            modified = true;
                        }
                    }

                    // Clamp Smoothness to prevent extreme specular reflections
                    if (mat.HasProperty("_Smoothness"))
                    {
                        float current = mat.GetFloat("_Smoothness");
                        float clampedValue = Mathf.Min(current, 0.2f);
                        if (current != clampedValue)
                        {
                            mat.SetFloat("_Smoothness", clampedValue);
                            modified = true;
                        }
                    }

                    // Clamp Glossiness (similar to Smoothness)
                    if (mat.HasProperty("_Glossiness"))
                    {
                        float current = mat.GetFloat("_Glossiness");
                        float clampedValue = Mathf.Min(current, 0.2f);
                        if (current != clampedValue)
                        {
                            mat.SetFloat("_Glossiness", clampedValue);
                            modified = true;
                        }
                    }

                    // Clamp Specular Highlights (helps reduce shimmering artifacts)
                    if (mat.HasProperty("_SpecularHighlights"))
                    {
                        float current = mat.GetFloat("_SpecularHighlights");
                        float clampedValue = Mathf.Min(current, 0.2f);
                        if (current != clampedValue)
                        {
                            mat.SetFloat("_SpecularHighlights", clampedValue);
                            modified = true;
                        }
                    }

                    // Clamp Reflection Intensity (helps stabilize sharp reflections)
                    if (mat.HasProperty("_ReflectionIntensity"))
                    {
                        float current = mat.GetFloat("_ReflectionIntensity");
                        float clampedValue = Mathf.Min(current, 0.2f);
                        if (current != clampedValue)
                        {
                            mat.SetFloat("_ReflectionIntensity", clampedValue);
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
        /*[HarmonyPostfix]
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
        }*/

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

        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PostProcessLayer), "OnEnable")]
        private static void DestroyPostProcessingLayer(PostProcessLayer __instance)
        {
            // First clean up any command buffers
            Camera camera = __instance.GetComponent<Camera>();
            if (camera != null)
            {
                foreach (CameraEvent evt in System.Enum.GetValues(typeof(CameraEvent)))
                {
                    CommandBuffer[] buffers = camera.GetCommandBuffers(evt);
                    foreach (CommandBuffer buffer in buffers)
                    {
                        if (buffer.name.Contains("PostProcess"))
                        {
                            camera.RemoveCommandBuffer(evt, buffer);
                        }
                    }
                }
            }

            // Then destroy the component
            UnityEngine.Object.Destroy(__instance);

            Plugin.MyLog.LogError("VR Mod: Completely removed PostProcessLayer from " + camera.gameObject.name);
        }
        */

        // NOTEEEEEE: You can completely delete SSAA and SSAAPropagatorOpaque and the blurriness still occcurs so it must be from SSAAPropagator or SSAAImpl

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // These two functions would return the screen resolution setting and would result in the game
        // being very blurry
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "GetOutputWidth")]
        private static bool ReturnVROutputWidth(SSAAImpl __instance, ref int __result)
        {
            __result = __instance.GetInputWidth();
            return false;
        }

        //Cybensis, im sorry but yes I did throw this through an AI to better understand what the hell is going on here
        //ive come to the same conclusion though... SSAA is ass and just outputting the native resolution is the best option here
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Holy fuck this actually fixes so many visual problems :)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAPropagator), "OnRenderImage")]
        private static bool ProcessImageRendering(SSAAPropagator __instance, RenderTexture source, RenderTexture destination)
        {
            
            if (__instance._postProcessLayer != null)
            {
                Graphics.Blit(source, destination);
                return false;
            }

            Camera.main.useOcclusionCulling = false;

            ResetRenderingState(__instance);
            int width = Camera.main.pixelWidth;
            int height = Camera.main.pixelHeight;

            InitializeHDRRenderTargets(__instance, width, height);
            InitializeLDRRenderTargets(__instance, width, height);

            // Force a higher SSAA ratio specifically for specular highlights
            float ssRatio = __instance.m_ssaa.GetCurrentSSRatio();
            float effectiveRatio = Mathf.Max(ssRatio, 2.0f); // Force at least 2x supersampling
            var rt = __instance.m_ssaa.GetRT();

            // Enhanced quality settings for better specular handling
            if (rt != null)
            {
                source.filterMode = FilterMode.Trilinear;
                source.wrapMode = TextureWrapMode.Clamp;
                source.useMipMap = true;           // Enable mipmaps for better specular filtering
                source.autoGenerateMips = true;    // Automatically generate mipmaps
                source.anisoLevel = 8;             // Increase anisotropic filtering
            }

            // Create temporary RT for specular processing
            RenderTexture tempRT = RenderTexture.GetTemporary(width, height, 0, source.format);
            tempRT.filterMode = FilterMode.Trilinear;
            tempRT.wrapMode = TextureWrapMode.Clamp;

            if (effectiveRatio > 1.1f)
            {
                if (rt != null)
                {
                    // Two-pass downsampling for better specular quality
                    RenderTexture intermediateRT = RenderTexture.GetTemporary(
                        (int)(width * 1.5f),
                        (int)(height * 1.5f),
                        0,
                        source.format
                    );

                    // First pass - higher resolution
                    __instance.m_ssaa.RenderImage(rt, intermediateRT, true, null);

                    // Second pass - careful downsampling
                    Graphics.Blit(intermediateRT, __instance._resampledColorTargetHDR[0]);

                    RenderTexture.ReleaseTemporary(intermediateRT);
                }
                else
                {
                    __instance.m_ssaa.RenderImage(source, __instance._resampledColorTargetHDR[0], true, null);
                }
            }
            else
            {
                // Enhanced filtering even at base resolution
                Graphics.Blit(source, tempRT);
                Graphics.Blit(tempRT, __instance._resampledColorTargetHDR[0]);
            }

            RenderTexture.ReleaseTemporary(tempRT);

            if (__instance._cmdBuf == null)
            {
                __instance._cmdBuf = new CommandBuffer { name = "SSAAPropagator" };
            }
            __instance._cmdBuf.Clear();

            if (!__instance._thermalVisionIsOn && HasOpticalRenderers(__instance))
            {
                InitializeDepthRenderTarget(__instance, width, height);
                RenderOpticalEffects(__instance);
            }
            ApplyVisionEffects(__instance);

            Graphics.ExecuteCommandBuffer(__instance._cmdBuf);
            return false;
        }

        private static void ResetRenderingState(SSAAPropagator __instance)
        {
            __instance._currentDestinationHDR = 0;
            __instance._currentDestinationLDR = 0;
            __instance._HDRSourceDestination = true;
        }

        // Modify InitializeHDRRenderTargets to include better specular handling
        private static void InitializeHDRRenderTargets(SSAAPropagator __instance, int width, int height)
        {
            bool needsUpdate = __instance._resampledColorTargetHDR[0] == null ||
                              __instance._resampledColorTargetHDR[0].width != width ||
                              __instance._resampledColorTargetHDR[0].height != height ||
                              __instance._resampledColorTargetHDR[0].format != RuntimeUtilities.defaultHDRRenderTextureFormat;

            if (!needsUpdate) return;

            for (int i = 0; i < 2; i++)
            {
                if (__instance._resampledColorTargetHDR[i] != null)
                {
                    __instance._resampledColorTargetHDR[i].Release();
                    RuntimeUtilities.SafeDestroy(__instance._resampledColorTargetHDR[i]);
                    __instance._resampledColorTargetHDR[i] = null;
                }
            }

            var format = RuntimeUtilities.defaultHDRRenderTextureFormat;
            for (int i = 0; i < 2; i++)
            {
                __instance._resampledColorTargetHDR[i] = new RenderTexture(width, height, 0, format)
                {
                    name = $"SSAAPropagator{i}HDR",
                    enableRandomWrite = true,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = true,
                    autoGenerateMips = true,
                    anisoLevel = 8
                };
                __instance._resampledColorTargetHDR[i].Create();
            }
        }

        private static void InitializeLDRRenderTargets(SSAAPropagator __instance, int width, int height)
        {
            bool needsUpdate = __instance._resampledColorTargetLDR[0] == null ||
                              __instance._resampledColorTargetLDR[0].width != width ||
                              __instance._resampledColorTargetLDR[0].height != height;

            if (!needsUpdate) return;

            for (int i = 0; i < 3; i++)
            {
                if (__instance._resampledColorTargetLDR[i] != null)
                {
                    __instance._resampledColorTargetLDR[i].Release();
                    RuntimeUtilities.SafeDestroy(__instance._resampledColorTargetLDR[i]);
                    __instance._resampledColorTargetLDR[i] = null;
                }
            }

            var format = RenderTextureFormat.ARGB32;
            __instance._resampledColorTargetLDR[0] = new RenderTexture(width, height, 0, format)
            {
                filterMode = FilterMode.Trilinear // Enhanced filtering
            };
            __instance._resampledColorTargetLDR[1] = new RenderTexture(width, height, 0, format)
            {
                name = "SSAAPropagator1LDR",
                filterMode = FilterMode.Trilinear // Enhanced filtering
            };
            __instance._resampledColorTargetLDR[2] = new RenderTexture(width, height, 0, format)
            {
                name = "Stub",
                filterMode = FilterMode.Trilinear // Enhanced filtering
            };
        }

        private static bool HasOpticalRenderers(SSAAPropagator __instance)
        {
            return __instance._opticLensRenderer != null || __instance._collimatorRenderer != null;
        }

        private static void InitializeDepthRenderTarget(SSAAPropagator __instance, int width, int height)
        {
            bool needsUpdate = __instance._resampledDepthTarget == null ||
                              __instance._resampledDepthTarget.width != width ||
                              __instance._resampledDepthTarget.height != height;

            if (!needsUpdate) return;

            if (__instance._resampledDepthTarget != null)
            {
                __instance._resampledDepthTarget.Release();
                RuntimeUtilities.SafeDestroy(__instance._resampledDepthTarget);
                __instance._resampledDepthTarget = null;
            }

            __instance._resampledDepthTarget = new RenderTexture(width, height, 24, RenderTextureFormat.Depth)
            {
                name = "SSAAPropagatorDepth"
            };
        }

        private static void RenderOpticalEffects(SSAAPropagator __instance)
        {
            var cmd = __instance._cmdBuf;
            cmd.BeginSample("OutputOptic");

            cmd.EnableShaderKeyword(SSAAPropagator.KWRD_TAA);
            cmd.EnableShaderKeyword(SSAAPropagator.KWRD_NON_JITTERED);

            cmd.SetGlobalMatrix(
                SSAAPropagator.ID_NONJITTEREDPROJ,
                GL.GetGPUProjectionMatrix(__instance._camera.nonJitteredProjectionMatrix, true)
            );

            cmd.SetRenderTarget(__instance._resampledColorTargetHDR[0], __instance._resampledDepthTarget);
            cmd.ClearRenderTarget(true, false, Color.black);

            if (__instance._opticLensRenderer == null && __instance._collimatorRenderer != null)
            {
                cmd.DrawRenderer(__instance._collimatorRenderer, __instance._collimatorMaterial);
            }

            if (__instance._opticLensRenderer != null)
            {
                RenderSightComponents(__instance);

                cmd.SetRenderTarget(__instance._resampledColorTargetHDR[0], __instance._resampledDepthTarget);
                cmd.DrawRenderer(__instance._opticLensRenderer, __instance._opticLensMaterial);
            }

            cmd.DisableShaderKeyword(SSAAPropagator.KWRD_NON_JITTERED);
            cmd.DisableShaderKeyword(SSAAPropagator.KWRD_TAA);
            cmd.EndSample("OutputOptic");
        }

        private static void RenderSightComponents(SSAAPropagator __instance)
        {
            if (__instance._sightNonLensRenderers == null || __instance._sightNonLensRenderers.Length == 0) return;

            var cmd = __instance._cmdBuf;
            cmd.BeginSample("DEPTH_PREPASS");
            cmd.SetRenderTarget(__instance._resampledColorTargetLDR[2], __instance._resampledDepthTarget);

            // Render sight components
            cmd.BeginSample("SIGHT_DEPTH");
            for (int i = 0; i < __instance._sightNonLensRenderers.Length; i++)
            {
                if (IsRendererValid(__instance._sightNonLensRenderers[i], __instance._sightNonLensRenderersMaterials[i]))
                {
                    cmd.DrawRenderer(__instance._sightNonLensRenderers[i], __instance._sightNonLensRenderersMaterials[i]);
                }
            }
            cmd.EndSample("SIGHT_DEPTH");

            // Render weapon components
            cmd.BeginSample("WEAPON_DEPTH");
            for (int i = 0; i < __instance._otherWeaponRenderers.Length; i++)
            {
                if (IsRendererValid(__instance._otherWeaponRenderers[i], __instance._otherWeaponRenderersMaterials[i]))
                {
                    cmd.DrawRenderer(__instance._otherWeaponRenderers[i], __instance._otherWeaponRenderersMaterials[i]);
                }
            }
            cmd.EndSample("WEAPON_DEPTH");

            cmd.EndSample("DEPTH_PREPASS");
        }

        private static bool IsRendererValid(Renderer renderer, Material material)
        {
            return renderer != null && material != null && renderer.gameObject.activeSelf;
        }

        private static void ApplyVisionEffects(SSAAPropagator __instance)
        {
            if (__instance._nightVisionMaterial)
            {
                __instance._cmdBuf.EnableShaderKeyword(SSAAPropagator.KWRD_NIGHTVISION_NOISE);
                __instance._cmdBuf.Blit(
                    __instance._resampledColorTargetHDR[0],
                    __instance._resampledColorTargetHDR[1],
                    __instance._nightVisionMaterial
                );
                __instance._cmdBuf.DisableShaderKeyword(SSAAPropagator.KWRD_NIGHTVISION_NOISE);
                __instance._currentDestinationHDR = 1;
            }
            else if (__instance._thermalVisionIsOn && __instance._thermalVisionMaterial != null)
            {
                __instance._cmdBuf.Blit(
                    __instance._resampledColorTargetHDR[0],
                    __instance._resampledColorTargetHDR[1],
                    __instance._thermalVisionMaterial,
                    1
                );
                __instance._currentDestinationHDR = 1;
            }
        }

        // Use a Transpiler to replace the Screen.Width/Height calls to get the camera height and width
        //[HarmonyPatch(typeof(SSAAPropagator), "OnRenderImage")]
        //public static class OnRenderImageTranspiler
        //{
        //    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        //    {
        //        var codes = new List<CodeInstruction>(instructions);

        //        // Find the instructions to replace
        //        for (int i = 0; i < codes.Count; i++)
        //        {
        //            if (codes[i].opcode == OpCodes.Call && codes[i].operand.ToString().Contains("UnityEngine.Screen::get_width"))
        //            {
        //                // Replace Screen.width with Camera.main.pixelWidth
        //                codes[i] = new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Camera), "main"));
        //                codes.Insert(i + 1, new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Camera), "pixelWidth")));
        //            }
        //            else if (codes[i].opcode == OpCodes.Call && codes[i].operand.ToString().Contains("UnityEngine.Screen::get_height"))
        //            {
        //                // Replace Screen.height with Camera.main.pixelHeight
        //                codes[i] = new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Camera), "main"));
        //                codes.Insert(i + 1, new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Camera), "pixelHeight")));
        //            }
        //        }

        //        return codes;
        //    }
        //}

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "GetOutputHeight")]
        private static bool ReturnVROutputHeight(SSAAImpl __instance, ref int __result)
        {
            __result = __instance.GetInputHeight();
            return false;
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SSAA), "Awake")]
        private static void DisableSSAA(SSAA __instance)
        {

            __instance.FlippedV = true;

        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PostProcessLayer), "InitLegacy")]
        private static void FixPostProcessing(PostProcessLayer __instance)
        {
            UnityEngine.Object.Destroy(__instance);

            //if (VRGlobals.camHolder && VRGlobals.camHolder.GetComponent<Camera>() == null)
            //{
            //    postProcessingStoogeCamera = VRGlobals.camHolder.AddComponent<Camera>();
            //    postProcessingStoogeCamera.enabled = false;
            //}
            //if (postProcessingStoogeCamera)
            //    mainCam = postProcessingStoogeCamera;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The volumetric light was only using the projection matrix for one eye which made it appear
        // off position in the other eye, this gets the current eyes matrix to fix this issue
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VolumetricLightRenderer), "OnPreRender")]
        private static bool PatchVolumetricLightingToVR(VolumetricLightRenderer __instance)
        {
            if (UnityEngine.XR.XRSettings.enabled && __instance.camera_0 != null)
            {
                __instance.method_3();

                Camera.StereoscopicEye eye = (Camera.StereoscopicEye)Camera.current.stereoActiveEye;

                Matrix4x4 viewMatrix = __instance.camera_0.GetStereoViewMatrix(eye);
                Matrix4x4 projMatrix = __instance.camera_0.GetStereoProjectionMatrix(eye);
                projMatrix = GL.GetGPUProjectionMatrix(projMatrix, renderIntoTexture: true);
                Matrix4x4 combinedMatrix = projMatrix * viewMatrix;
                __instance.matrix4x4_0 = combinedMatrix;
                __instance.method_4();
                __instance.method_6();

                for (int i = 0; i < VolumetricLightRenderer.list_0.Count; i++)
                {
                    VolumetricLightRenderer.list_0[i].VolumetricLightPreRender(__instance, __instance.matrix4x4_0);
                }
                __instance.commandBuffer_0.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);
                __instance.method_5();

                return false;
            }
            return true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryBlur), "Enable")]
        private static void DisableInvBlurOnEnable(InventoryBlur __instance)
        {
            __instance.enabled = false;
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryBlur), "Awake")]
        private static void DisableInvBlurOnAwake(InventoryBlur __instance)
        {
            __instance.enabled = false;
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FakeCharacterGI), "OnRenderImage")]
        private static bool FixCharacterLighting(FakeCharacterGI __instance, RenderTexture src, RenderTexture dest)
        {
            Camera.StereoscopicEye eye = (Camera.StereoscopicEye)Camera.current.stereoActiveEye;

            Matrix4x4 viewMatrix = __instance.camera_0.GetStereoViewMatrix(eye);
            __instance.method_0().SetMatrix(FakeCharacterGI.int_0, viewMatrix);
            Graphics.Blit(src, dest, __instance.method_0());
            return false;
        }



        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(PrismEffects), "OnRenderImage")]
        //private static bool DisablePrismEffects(PrismEffects __instance)
        //{
        //    if (__instance.gameObject.name != "FPS Camera")
        //        return true;

        //    __instance.enabled = false;
        //    return false;
        //}

        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(BloomAndFlares), "OnRenderImage")]
        //private static bool DisableBloomAndFlares(BloomAndFlares __instance)
        //{
        //    if (__instance.gameObject.name != "FPS Camera")
        //        return true;

        //    __instance.enabled = false;
        //    return false;
        //}


        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(ChromaticAberration), "OnRenderImage")]
        //private static bool DisableChromaticAberration(ChromaticAberration __instance)
        //{
        //    if (__instance.gameObject.name != "FPS Camera")
        //        return true;

        //    __instance.enabled = false;
        //    return false;
        //}

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UltimateBloom), "OnRenderImage")]
        private static bool DisableUltimateBloom(UltimateBloom __instance)
        {
            if (__instance.gameObject.name != "FPS Camera")
                return true;

            __instance.enabled = false;
            return false;
        }

        //Attempting to fix DLSS by forcing DLAA. not quite there yet but I think its getting somewhere... Using custom jitter because post processing is disabled for VR
        //Maybe ill try getting PostProcessing working again...
        //-matsix
        /*
        private static readonly Vector2[] ImprovedJitterSequence = new Vector2[]
{
            new Vector2(0.125f, -0.375f),
            new Vector2(-0.375f, 0.125f),
            new Vector2(0.375f, 0.125f),
            new Vector2(-0.125f, -0.375f),
            new Vector2(-0.125f, 0.375f),
            new Vector2(0.375f, -0.125f),
            new Vector2(-0.375f, -0.125f),
            new Vector2(0.125f, 0.375f)
        };
        private static int _jitterIndex = 0;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "TryRenderDLSS", new Type[] { typeof(RenderTexture), typeof(RenderTexture), typeof(CommandBuffer) })]
        private static bool FixDLAAOnlyRender(SSAAImpl __instance, RenderTexture source, RenderTexture destination, CommandBuffer externalCommandBuffer)
        {
            Camera fpsCamera = GameObject.Find("FPS Camera")?.GetComponent<Camera>();

            if (!fpsCamera)
            {
                return false;
            }

            // DLSS Wrapper initialization
            if (__instance._dlssWrapper == null)
            {
                if (!(__instance._ssaaPropagator != null) ||
                    !(__instance._ssaaPropagator.CopyDLSSResources != null) ||
                    !(__instance._ssaaPropagator.DLSSDebugOutput != null))
                {
                    __instance._failedToInitializeDLSS = true;
                    return false;
                }
                __instance._dlssWrapper = new DLSSWrapper(
                    __instance._ssaaPropagator.CopyDLSSResources,
                    __instance._ssaaPropagator.DLSSDebugOutput
                );
            }

            // Configure DLSS wrapper
            if (__instance._dlssWrapper != null)
            {
                __instance._dlssWrapper.DebugMode = __instance.DLSSDebug;
                __instance._dlssWrapper.DebugDisable = (__instance.DLSSDebugDisable || DLSSWrapper.WantToDebugDLSSViaRenderdoc);
                __instance._dlssWrapper.Quality = __instance._DLSSCurrentQuality;
                __instance._dlssWrapper.JitterOffsets = __instance.DLSSJitter;
                __instance._dlssWrapper.MVScale = __instance.DLSSMVScale;
            }

            // Check DLSS library initialization
            if (!__instance._dlssWrapper.IsDLSSLibraryLoaded())
            {
                DLSSWrapper.InitErrors initErrors = __instance._dlssWrapper.InitializeDLSS();
                __instance._failedToInitializeDLSS = (initErrors > DLSSWrapper.InitErrors.INIT_SUCCESS);
                if (initErrors != DLSSWrapper.InitErrors.INIT_SUCCESS)
                {
                    Plugin.MyLog.LogError($"Failed to initialize DLSS library: {initErrors}");
                    return false;
                }
            }

            // In SinglePassInstanced, we need to handle the combined eye texture
            // This is critical - get combined eye resolution, not just single eye
            int renderWidth = XRSettings.eyeTextureWidth;
            int renderHeight = XRSettings.eyeTextureHeight;

            // For SinglePassInstanced, we might need to handle the combined texture differently
            // Force DLAA mode (0) by setting input and output resolutions to be the same
            DLSSWrapper.SetCreateDLSSFeatureParameters(renderWidth, renderHeight, renderWidth, renderHeight, 0);

            // Copy depth and motion vectors
            __instance._dlssWrapper.CopyDepthMotion(source, destination, __instance.DepthCopyMode, externalCommandBuffer);
            __instance._dlssWrapper.Sharpness = __instance.DLSSSharpness;

            // Get jitter from our improved sequence
            Vector2 jitter = ImprovedJitterSequence[_jitterIndex++ % ImprovedJitterSequence.Length];

            // Scale jitter appropriately - may need adjustment for SinglePassInstanced
            jitter *= new Vector2(__instance.DLSSJitterXScale, __instance.DLSSJitterYScale);

            // For SinglePassInstanced, the jitter needs to be applied consistently across both eyes
            // to prevent discomfort and visual artifacts

            // Render with our calculated jitter
            __instance._dlssWrapper.OnRenderImage(source, destination, __instance.SwapDLSSUpDown, jitter, externalCommandBuffer);

            return true;
        }
        */

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "RenderImage", new Type[] { typeof(RenderTexture), typeof(RenderTexture), typeof(bool), typeof(CommandBuffer) })]
        private static bool FixSSAAImplRenderImage(SSAAImpl __instance, RenderTexture source, RenderTexture destination, bool flipV, CommandBuffer externalCommandBuffer)
        {
            int shaderPass = 0;


            if (__instance.CurrentState == SSAAImpl.SSState.UPSCALE)
            {
                bool flag = ((__instance.EnableDLSS && !__instance._failedToInitializeDLSS && !__instance.NeedToApplySwitch()) || (__instance.EnableDLSS && (__instance.DLSSDebugDisable || DLSSWrapper.WantToDebugDLSSViaRenderdoc))) && !__instance.InventoryBlurIsEnabled;
                // Attempting to use FSR is causing massive issues and neither scaling option works anyway so just disable it
                //bool flag2 = __instance.EnableFSR && !__instance._failedToInitializeFSR && !flag && !__instance.NeedToApplySwitch();
                //bool flag3 = __instance.EnableFSR2 && !__instance._failedToInitializeFSR2 && !flag && !__instance.NeedToApplySwitch();
                if ((flag && __instance.TryRenderDLSS(source, destination, externalCommandBuffer)))
                {
                    return false;
                }
            }
            
            if (__instance.RenderTextureMaterialBicubic == null)
            {
                __instance.RenderTextureMaterialBicubic = new Material(Shader.Find("Hidden/BicubicSampling"));
            }
            int num = (destination ? destination.width : Screen.width);
            int num2 = (destination ? destination.height : Screen.height);
            if (Camera.main != null)
            {
                num = Camera.main.pixelWidth;
                num2 = Camera.main.pixelHeight;
            }
            __instance._applyResultCmdBuf.Clear();
            __instance._applyResultCmdBuf.SetRenderTarget(destination);
            __instance._applyResultCmdBuf.SetViewport(new Rect(0f, 0f, num, num2));
            Mesh mesh = (flipV ? __instance.FullScreenYFlippedMesh : __instance.FullScreenMesh);
            __instance._applyResultCmdBuf.DrawMesh(mesh, Matrix4x4.identity, __instance.RenderTextureMaterialBicubic, 0, shaderPass);
            __instance.RenderTextureMaterialBicubic.SetTexture("_MainTex", source);
            Graphics.ExecuteCommandBuffer(__instance._applyResultCmdBuf);
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(SSAAImpl), "Awake")]
        //private static bool RemoveBadCameraEffects(SSAAImpl __instance)
        //{
        //    // All the SSAA stuff makes everything very blurry and bad quality, all while lowering framerate
        //    if (__instance.GetComponent<SSAAPropagatorOpaque>() != null)
        //    {
        //        UnityEngine.Object.Destroy(__instance.GetComponent<SSAAPropagatorOpaque>());
        //        Plugin.MyLog.LogWarning("SSAAPropagatorOpaque component removed successfully.");
        //    }
        //    if (__instance.GetComponent<SSAAPropagator>() != null)
        //    {
        //        UnityEngine.Object.Destroy(__instance.GetComponent<SSAAPropagator>());
        //        Plugin.MyLog.LogWarning("SSAAPropagator component removed successfully.");
        //    }
        //    if (__instance != null)
        //    {
        //        UnityEngine.Object.Destroy(__instance);
        //        Plugin.MyLog.LogWarning("SSAAImpl component removed successfully.");
        //    }
        //    if (__instance.GetComponent<SSAA>() != null)
        //    {
        //        UnityEngine.Object.Destroy(__instance.GetComponent<SSAA>());
        //        Plugin.MyLog.LogWarning("SSAA component removed successfully.");
        //    }
        //    // The EnableDistantShadowKeywords is responsible for rendering distant shadows (who woulda thunk) but it works
        //    // poorly with VR so it needs to be removed and should ideally be suplemented with high or ultra shadow settings
        //    CommandBuffer[] commandBuffers = Camera.main.GetCommandBuffers(CameraEvent.BeforeGBuffer);
        //    for (int i = 0; i < commandBuffers.Length; i++)
        //    {
        //        if (commandBuffers[i].name == "EnableDistantShadowKeywords")
        //            Camera.main.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, commandBuffers[i]);
        //    }

        //    return false;
        //}


        //NOTEEEEEEEEEEEE: Removing the SSAApropagator (i think) from the PostProcessingLayer/Volume will restore some visual fidelity but still not as good as no ssaa

        //ANOTHER NOTE: I'm pretty sure if you delete or disable the SSAA shit you still get all the nice visual effects from the post processing without the blur,
        // its just the night vision doessn't work, so maybe only enable SSAA when enabling night/thermal vision

        // FIGURED IT OUT Delete the SSAAPropagator, SSAA, and SSAAImpl and it just works

        // Also remove the distant shadows command buffer from the camera
        // MotionVectorsPASS is whats causing the annoying [Error  : Unity Log] Dimensions of color surface does not match dimensions of depth surface    error to occur 
        // but its also needed for grass and maybe other stuff

        // SSAA causes a bunch of issues like thermal/nightvision rendering all fucky, and the
        // s also render in 
        // with 2 other lenses on either side of the main lense, Although SSAA is nice for fixing the jagged edges, it 
        // also adds a strong layer of blur over everything so it's definitely best to keep it disabled. Might look into
        // keeping it around later on if I can figure a way to get it to look nice without messing with everything else

        // In hideout, don't notice any real fps difference when changing object LOD quality and overall visibility

        // anti aliasing is off or on FXAA - no FPS difference noticed - seems like scopes won't work without it
        // Resampling x1 OFF 
        // DLSS and FSR OFF
        // HBAO - Looks better but takes a massive hit on performance - off gets about around 10-20 fps increase
        // SSR - turning low to off raises FPS by about 2-5, turning ultra to off raises fps by about 5ish. I don't know if it looks better but it seems like if you have it on, you may as well go to ultra
        // Anistrophic filtering - per texture or on maybe cos it looks bettter, or just off - No real FPS difference
        // Sharpness at 1-1.5 I think it the gain falls off after around 1.5+
        // Uncheck all boxes on bottom - CHROMATIC ABBERATIONS probably causing scope issues so always have it off
        // Uncheck all boxes on bottom - CHROMATIC ABBERATIONS probably causing scope issues so always have it off
        // POST FX - Turning it off gains about 8-10 FPS

        // When aiming a lot of stuff gets culled do to the lowered LodBiasFactor, so set this to a minimum of 1 which is whats normal
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CameraLodBiasController), "SetBiasByFov")]
        private static void FixAimCulling(CameraLodBiasController __instance)
        {
            __instance.LodBiasFactor = 3;

        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DistantShadow), "Awake")]
        private static void FixDistantShadows(DistantShadow __instance)
        {
            __instance.EnableMultiviewTiles = true;
        }

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(CC_Base), "Awake")]
        //private static void OptionalDisableSharpenAwake(CC_Base __instance)
        //{
        //    if (__instance is CC_Sharpen)
        //    {
        //        if (!VRSettings.GetSharpenOn())
        //            __instance.enabled = false;
        //        else
        //            __instance.enabled = true;
        //    }
        //}

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(CC_Base), "Start")]
        //private static void OptionalDisableSharpenStart(CC_Base __instance)
        //{
        //    if (__instance is CC_Sharpen)
        //    {
        //        if (!VRSettings.GetSharpenOn())
        //            __instance.enabled = false;
        //        else
        //            __instance.enabled = true;
        //    }
        //}

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ThermalVision), "method_1")]
        private static void FixThermalsDoubleVision(ThermalVision __instance)
        {
            __instance.IsMotionBlurred = false;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(FastBlur), "Start")]
        private static void ReduceDamageBlur(FastBlur __instance)
        {
            __instance._downsampleTexDimension = FastBlur.Dimensions._1024;
            __instance._upsampleTexDimension = FastBlur.Dimensions._2048;
            __instance._blurCount = 2;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(DistantShadow), "Awake")]
        private static void SetDistantShadowSettings(DistantShadow __instance)
        {
            distantShadow = __instance;
            if (VRSettings.GetShadowOpts() == VRSettings.ShadowOpt.IncreaseLighting)
            {
                distantShadow.EnableMultiviewTiles = false;
                distantShadow.PreComputeMask = true;
                QualitySettings.shadowDistance = 25f;
                distantShadow.CurrentMaskResolution = DistantShadow.ResolutionState.FULL;
            }
            else if (VRSettings.GetShadowOpts() == VRSettings.ShadowOpt.DisableNearShadows)
            {
                distantShadow.EnableMultiviewTiles = true;
                distantShadow.PreComputeMask = false;
            }
            else {
                distantShadow.EnableMultiviewTiles = true;
                distantShadow.PreComputeMask = true;
            }
        }
        //Clouds dont render correctly and haven't found a way to fix them yet so disable it
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CloudController), "OnEnable")]
        private static void FixClouds(CloudController __instance) {
            __instance.enabled = false;
        }

        //Fog dont render correctly and haven't found a way to fix it yet so disable it
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MBOIT_Scattering), "Start")]
        private static void DisableMBOIT(CloudController __instance)
        {
            __instance.enabled = false;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CloudController), "UpdateAmbient")]
        private static bool FixAmbientErrors(CloudController __instance)
        {
            __instance.enabled = false;
            return false;
        }

        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(CloudController), "OnPreRenderCamera")]
        //private static bool FixClouds(CloudController __instance, Camera cam)
        //{
        //    if (!__instance.IsInitialized || __instance._settings == null)
        //        return false;

        //    // Check if the camera is a VR camera
        //    if (!cam.CompareTag("MainCamera") && !cam.CompareTag("OpticCamera") && !cam.stereoEnabled)
        //        return false;

        //    // Handle each eye separately in VR
        //    if (cam.stereoEnabled)
        //    {
        //        for (Camera.StereoscopicEye eye = Camera.StereoscopicEye.Left; eye <= Camera.StereoscopicEye.Right; eye++)
        //        {
        //            CommandBuffer buffer;
        //            if (eye == Camera.StereoscopicEye.Left)
        //            {
        //                __instance.gclass968_0.UpdateOnRenderObject(out buffer); // Shadow buffer
        //            }
        //            else
        //            {
        //                __instance.gclass968_1.UpdateOnRenderObject(out buffer); // Main buffer
        //            }

        //            buffer.Clear();
        //            CloudControllerMethod_1(__instance, __instance.gclass2104_0, cam, buffer, eye);
        //        }
        //    }
        //    else
        //    {
        //        __instance.gclass968_0.UpdateOnRenderObject(out var buffer);
        //        __instance.gclass968_1.UpdateOnRenderObject(out var buffer2);
        //        buffer.Clear();
        //        buffer2.Clear();
        //        CloudControllerMethod_1(__instance, __instance.gclass2104_0, cam, buffer2, Camera.StereoscopicEye.Left); // Default to left eye for non-VR
        //        __instance.method_0(buffer);
        //    }

        //    if (__instance.class1665_0.vmethod_0(__instance.gclass2104_0))
        //    {
        //        __instance.class1665_0.UpdateTexture(__instance.gclass2104_0);
        //    }
        //    return false;
        //}
        private static bool test = false;
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(CloudController), "method_1")]
        //public static bool CloudControllerMethod_1(CloudController __instance, GClass2104 parameters, Camera cam, CommandBuffer buffer)
        //{
        //    if (test)
        //        return true;

        //    int pixelWidth = cam.pixelWidth;
        //    int pixelHeight = cam.pixelHeight;
        //    Camera.StereoscopicEye eye = (Camera.StereoscopicEye)Camera.current.stereoActiveEye;
        //    // Use eye-specific matrices
        //    Matrix4x4 projectionMatrix = cam.GetStereoProjectionMatrix(eye);
        //    Matrix4x4 worldToCameraMatrix = cam.GetStereoViewMatrix(eye);

        //    Matrix4x4 matrix = GL.GetGPUProjectionMatrix(projectionMatrix, renderIntoTexture: true);
        //    Matrix4x4 view = worldToCameraMatrix;
        //    float aspect = GClass2106.ProjectionMatrixAspect(in matrix);
        //    Vector4 vector = new Vector4(pixelWidth, pixelHeight, 1f / pixelWidth, 1f / pixelHeight);
        //    Matrix4x4 pixelCoordToViewDirMatrix = GClass2106.ComputePixelCoordToWorldSpaceViewDirectionMatrix(matrix, view, cam, vector, aspect);


        //    parameters.Time = Time.time;
        //    parameters.IsAnimate = true;
        //    parameters.PixelCoordToViewDirMatrix = pixelCoordToViewDirMatrix;
        //    parameters.WorldSpaceCameraPos = cam.transform.position;
        //    parameters.ViewMatrix = worldToCameraMatrix;
        //    parameters.ScreenSize = vector;
        //    parameters.SunLight = __instance._sun;
        //    parameters.FrameIndex = Time.frameCount;
        //    parameters.CloudSettings = __instance._settings;
        //    parameters.CubemapFace = CubemapFace.Unknown;
        //    parameters.CloudOpacity = default(RenderTargetIdentifier);
        //    parameters.ExposureMultiplier = 1f;
        //    parameters.CloudAmbientProbe = __instance.gclass2102_0;
        //    parameters.CommandBuffer = buffer;
        //    parameters.ColorBuffer = new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
        //    return false;
        //}


        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(Class1665), "RenderClouds")]
        //public static bool RenderCloudsPatch(Class1665 __instance, GClass2104 builtinParams, bool renderForCubemap)
        //{
        //    // Skip the patch for cubemap rendering
        //    if (renderForCubemap)
        //        return true;

        //    CommandBuffer commandBuffer = builtinParams.CommandBuffer;
        //    CloudLayer cloudLayer = builtinParams.CloudSettings as CloudLayer;
        //    if (cloudLayer == null || cloudLayer.Opacity == 0f)
        //        return false;

        //    float deltaTime = (builtinParams.IsAnimate ? (builtinParams.Time - __instance.float_0) : 0f);
        //    __instance.float_0 = builtinParams.Time;

        //    // Stereo-aware matrices
        //    Camera.StereoscopicEye eye = (Camera.StereoscopicEye)Camera.current.stereoActiveEye;
        //    Matrix4x4 projectionMatrix = Camera.current.GetStereoProjectionMatrix(eye);
        //    Matrix4x4 viewMatrix = Camera.current.GetStereoViewMatrix(eye);

        //    Matrix4x4 gpuProjectionMatrix = GL.GetGPUProjectionMatrix(projectionMatrix, renderIntoTexture: true);
        //    Matrix4x4 pixelCoordToViewDirMatrix = GClass2106.ComputePixelCoordToWorldSpaceViewDirectionMatrix(
        //        gpuProjectionMatrix, viewMatrix, Camera.current,
        //        new Vector4(Camera.current.pixelWidth, Camera.current.pixelHeight, 1f / Camera.current.pixelWidth, 1f / Camera.current.pixelHeight),
        //        GClass2106.ProjectionMatrixAspect(in gpuProjectionMatrix)
        //    );

        //    // Set VR-specific parameters
        //    builtinParams.PixelCoordToViewDirMatrix = pixelCoordToViewDirMatrix;

        //    // Set material properties
        //    __instance.material_0.SetTexture(Class1665.int_1, __instance.class1663_0.cloudTextureRT);
        //    Vector4 flowmapParams = cloudLayer.LayerA.method_0();
        //    flowmapParams.w = cloudLayer.Opacity;
        //    __instance.material_0.SetVector(Class1664._FlowmapParam, flowmapParams);

        //    cloudLayer.LayerA.ScrollFactor += cloudLayer.LayerA.ScrollSpeed * deltaTime * 0.277778f;

        //    // Handle sun and lighting
        //    Color sunColor = builtinParams.SunLight != null ? GClass2106.EvaluateLightColor(builtinParams.SunLight) : Color.black;
        //    Vector4 params1 = cloudLayer.LayerA.Color_0 * sunColor;
        //    params1.w = cloudLayer.LayerA.Altitude;
        //    __instance.material_0.SetVector(Class1664._Params1, params1);

        //    // Ambient probe adjustment
        //    Vector4 params2 = new Vector4(cloudLayer.LayerA.AmbientProbeDimmer, 0f, 0f, 0f);
        //    __instance.material_0.SetVector(Class1664._Params2, params2);
        //    __instance.material_0.SetBuffer(Class1665.int_4, builtinParams.CloudAmbientProbe);

        //    // Render target setup
        //    commandBuffer.SetGlobalVector(Class1664._PlanetCenterRadius, cloudLayer.PlanetCenterRadius);
        //    commandBuffer.SetGlobalFloat(Class1664._ExposureMultiplier, builtinParams.ExposureMultiplier);
        //    __instance.materialPropertyBlock_0.SetMatrix(Class1664._PixelCoordToViewDirWS, builtinParams.PixelCoordToViewDirMatrix);

        //    if (builtinParams.DepthBuffer == default(RenderTargetIdentifier))
        //    {
        //        if (builtinParams.CloudOpacity == default(RenderTargetIdentifier))
        //        {
        //            GClass2106.SetRenderTarget(commandBuffer, builtinParams.ColorBuffer);
        //        }
        //        else
        //        {
        //            RenderTargetIdentifier[] rtColor = new RenderTargetIdentifier[2] { builtinParams.ColorBuffer, builtinParams.CloudOpacity };
        //            GClass2106.SetRenderTarget(commandBuffer, rtColor, default(RenderTargetIdentifier));
        //        }
        //    }
        //    else if (builtinParams.CloudOpacity == default(RenderTargetIdentifier))
        //    {
        //        GClass2106.SetRenderTarget(commandBuffer, builtinParams.ColorBuffer, builtinParams.DepthBuffer);
        //    }
        //    else
        //    {
        //        __instance.MRTToRenderCloudOcclusion[0] = builtinParams.ColorBuffer;
        //        __instance.MRTToRenderCloudOcclusion[1] = builtinParams.CloudOpacity;
        //        GClass2106.SetRenderTarget(commandBuffer, __instance.MRTToRenderCloudOcclusion, builtinParams.DepthBuffer);
        //    }

        //    // Full-screen draw call
        //    GClass2106.DrawFullScreen(commandBuffer, __instance.material_0, __instance.materialPropertyBlock_0, 1);

        //    return false; // Skip original method
        //}
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TOD_Scattering), "OnRenderImageNormalMode")]
        private static bool FixTODScattering(TOD_Scattering __instance, RenderTexture source, RenderTexture destination)
        {
            if (!__instance.CheckSupport(needDepth: true, needHdr: true))
            {
                Graphics.Blit(source, destination);
                return false;
            }

            // Apply the effect for each eye separately if in VR mode
            if (UnityEngine.XR.XRSettings.enabled)
            {
                __instance.sky.Components.Scattering = __instance;
                for (int eye = 0; eye < 2; eye++)
                {
                    // Update the camera to use the correct eye's perspective
                    Camera.StereoscopicEye stereoEye = (Camera.StereoscopicEye)eye;
                    Matrix4x4 identity = CalculateFrustumCorners(Camera.main, stereoEye);

                    __instance.material_0.SetMatrix(TOD_Scattering.int_1, identity);
                    __instance.material_0.SetTexture(TOD_Scattering.int_2, __instance.DitheringTexture);

                    Vector3 cameraDirection = Camera.main.transform.forward;
                    float cameraHeight = Camera.main.transform.position.y;
                    Vector3 adjustedDirection = new Vector3(cameraDirection.x, 0f, cameraDirection.z).normalized;

                    float adjustedHeightFalloff = __instance.HeightFalloff * Mathf.Abs(Vector3.Dot(adjustedDirection, Vector3.up));
                    float adjustedDensity = __instance.GlobalDensity * (1.0f - Mathf.Abs(cameraDirection.y));

                    if (__instance.FromLevelSettings)
                    {
                        LevelSettings instance = Singleton<LevelSettings>.Instance;
                        if (instance != null)
                        {
                            __instance.HeightFalloff = instance.HeightFalloff;
                            __instance.ZeroLevel = instance.ZeroLevel;
                        }
                    }

                    //Shader.SetGlobalVector(TOD_Scattering.int_3, new Vector4(adjustedHeightFalloff, cameraHeight - __instance.ZeroLevel, adjustedDensity, 0f));
                    Shader.SetGlobalVector(TOD_Scattering.int_3, new Vector4(__instance.HeightFalloff, VRGlobals.camRoot.transform.position.y - __instance.ZeroLevel, __instance.GlobalDensity, 0f));
                    __instance.material_0.SetFloat(TOD_Scattering.int_4, __instance.SunrizeGlow);

                    if (__instance.Lighten)
                    {
                        __instance.material_0.EnableKeyword("LIGHTEN");
                    }
                    else
                    {
                        __instance.material_0.DisableKeyword("LIGHTEN");
                    }

                    __instance.CustomBlit(source, destination, __instance.material_0);
                }
            }
            else
            {
                return true;
            }
            return false;
        }
        */

        //This replaces the fog system BSG uses and uses the fog from the Prism Post Process effects instead. This does have a drawback though,
        //fog will appear inside of buildings (I think it's better than no fog at all or broken fog)       
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TOD_Scattering), "OnRenderImage")]
        private static void FixTODScattering(TOD_Scattering __instance, RenderTexture source, RenderTexture destination)
        {
            __instance.enabled = false;
        }

        private static Camera fpsCam;
        private static Camera opticCam;
        private static PrismEffects fpsPrism;
        private static PrismEffects opticPrism;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeatherController), "method_9")]
        private static void DynamicFog(WeatherController __instance, float fog, GStruct275 interpolatedParams)
        {
            if (fpsCam == null)
            {
                foreach (var cam in Camera.allCameras)
                {
                    if (cam.name == "FPS Camera")
                        fpsCam = cam;
                    else if (cam.name == "BaseOpticCamera(Clone)")
                        opticCam = cam;
                }
            }

            if (fpsCam == null)
                return;

            if (fpsPrism == null)
                fpsPrism = fpsCam.GetComponent<PrismEffects>() ?? fpsCam.gameObject.AddComponent<PrismEffects>();

            // Enable/disable fog effect
            if (VRSettings.GetDisableFog())
                fpsPrism.useFog = false;
            else
                fpsPrism.useFog = true;

            // Calculate fog properties
            float fogDistance = Mathf.Clamp(-6944.44f * __instance.tod_Scattering_0.GlobalDensity + 544.22f, 100f, 500f);
            float fogAlpha = Mathf.Lerp(0.7f, 0.08f, (fogDistance - 100f) / 400f);
            Color fogColor = new Color(1f, 1f, 1f, fogAlpha);

            // Apply fog settings to FPS camera
            fpsPrism.fogDistance = fogDistance;
            fpsPrism.fogColor = fogColor;
            fpsPrism.fogEndColor = fogColor;

            //Plugin.MyLog.LogError("FPS Camera FogDistance: " + fpsPrism.fogDistance + " - " + __instance.tod_Scattering_0.GlobalDensity);
            // Process optic camera if it exists
            if (opticCam != null)
            {
                if (opticPrism == null)
                    opticPrism = opticCam.GetComponent<PrismEffects>() ?? opticCam.gameObject.AddComponent<PrismEffects>();

                if (VRSettings.GetDisablePrismEffects())
                {
                    opticPrism.enabled = false;
                    return;
                }
                
                if (VRSettings.GetDisableFog())
                    opticPrism.useFog = false;
                else
                    opticPrism.useFog = true;

                opticPrism.enabled = true;
                opticPrism.fogDistance = fogDistance;
                opticPrism.fogColor = fogColor;
                opticPrism.fogEndColor = fogColor;
            }
        }
        private static Matrix4x4 CalculateFrustumCorners(Camera cam, Camera.StereoscopicEye eye = Camera.StereoscopicEye.Left)
        {
            float nearClipPlane = cam.nearClipPlane;
            float farClipPlane = cam.farClipPlane;
            float fieldOfView = cam.fieldOfView;
            float aspect = cam.aspect;

            // Adjust based on the eye (left or right) in VR mode
            Vector3 forward = cam.transform.forward;
            Vector3 right = cam.transform.right;
            Vector3 up = cam.transform.up;

            if (UnityEngine.XR.XRSettings.enabled)
            {
                Matrix4x4 eyeMatrix = cam.GetStereoViewMatrix(eye);
                forward = eyeMatrix.MultiplyVector(forward);
                right = eyeMatrix.MultiplyVector(right);
                up = eyeMatrix.MultiplyVector(up);
            }

            float halfFOV = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            Vector3 toRight = right * halfFOV * aspect * nearClipPlane;
            Vector3 toTop = up * halfFOV * nearClipPlane;

            Vector3 topLeft = (forward * nearClipPlane - toRight + toTop);
            float scale = topLeft.magnitude * farClipPlane / nearClipPlane;
            topLeft.Normalize();
            topLeft *= scale;

            Vector3 topRight = (forward * nearClipPlane + toRight + toTop);
            topRight.Normalize();
            topRight *= scale;

            Vector3 bottomRight = (forward * nearClipPlane + toRight - toTop);
            bottomRight.Normalize();
            bottomRight *= scale;

            Vector3 bottomLeft = (forward * nearClipPlane - toRight - toTop);
            bottomLeft.Normalize();
            bottomLeft *= scale;

            Matrix4x4 frustumCorners = Matrix4x4.identity;
            frustumCorners.SetRow(0, topLeft);
            frustumCorners.SetRow(1, topRight);
            frustumCorners.SetRow(2, bottomRight);
            frustumCorners.SetRow(3, bottomLeft);

            return frustumCorners;
        }
    }
}