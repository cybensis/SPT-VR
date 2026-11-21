using EFT.CameraControl;
using EFT.EnvironmentEffect;
using HarmonyLib;
using System.Collections.Generic;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using static HBAO_Core;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class VisualPatches
    {
        public static DistantShadow distantShadow;


        // Disables eye exposure adaptation: in VR, cloudy weather makes everything far too dark
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnvironmentManager), "Update")]
        private static void LimitExposureForVR(EnvironmentManager __instance)
        {
            __instance.PrismExposureSpeed = 0f;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Vanilla volumetric light uses one eye's projection matrix, so it is out of place in the other eye. Use the current eye's matrices.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VolumetricLightRenderer), "OnPreRender")]
        private static bool PatchVolumetricLightingToVR(VolumetricLightRenderer __instance)
        {
            if (UnityEngine.XR.XRSettings.enabled && __instance._camera != null)
            {
                var currentEye = Camera.current.stereoActiveEye;
                if (currentEye == Camera.MonoOrStereoscopicEye.Mono)
                    return true;

                Camera.StereoscopicEye eye = (Camera.StereoscopicEye)currentEye;

                __instance.RecreateRTsIfNeeded();

                Matrix4x4 viewMatrix = __instance._camera.GetStereoViewMatrix(eye);
                Matrix4x4 projMatrix = __instance._camera.GetStereoProjectionMatrix(eye);
                projMatrix = GL.GetGPUProjectionMatrix(projMatrix, renderIntoTexture: true);
                Matrix4x4 combinedMatrix = projMatrix * viewMatrix;
                __instance._viewProj = combinedMatrix;

                __instance.InitPrePass();
                __instance.UpdateMaterialParameters();

                for (int i = 0; i < VolumetricLightRenderer._lights.Count; i++)
                {
                    VolumetricLightRenderer._lights[i].VolumetricLightPreRender(__instance, __instance._viewProj);
                }

                __instance._globalCommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);
                __instance.InitPostRenderPass();

                return false;
            }
            return true;
        }

        private static Dictionary<HBAO, RenderTexture[]> stereoAOTargets = new();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HBAO), "RenderHBAO")]
        private static bool FixHBAOMethod4Stereo(HBAO __instance, RenderTexture source, RenderTexture destination)
        {
            int eyeIndex = Camera.current?.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;

            if (!stereoAOTargets.ContainsKey(__instance))
            {
                stereoAOTargets[__instance] = new RenderTexture[2];
                for (int i = 0; i < 2; i++)
                    stereoAOTargets[__instance][i] = new RenderTexture(__instance._downsampledRT.width, __instance._downsampledRT.height, 0);
            }

            var aoTarget = stereoAOTargets[__instance][eyeIndex];
            var commandBuffer = __instance._cmdBuf;
            var hbaoMaterial = __instance._hbaoMaterial;

            commandBuffer.Clear();
            commandBuffer.SetRenderTarget(aoTarget);
            commandBuffer.ClearRenderTarget(false, true, Color.white);
            commandBuffer.BlitFullscreenTriangle(source, aoTarget, hbaoMaterial, __instance.GetAoPass(), null, false, null);

            hbaoMaterial.SetTexture(ShaderProperties.hbaoTex, aoTarget);
            commandBuffer.BlitFullscreenTriangle(source, destination, hbaoMaterial, __instance.GetFinalPass(), null, false, null);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HBAO), "OnDisable")]
        private static void CleanupHBAOStereo(HBAO __instance)
        {
            if (stereoAOTargets.TryGetValue(__instance, out var targets))
            {
                foreach (var tex in targets)
                    if (tex != null) UnityEngine.Object.Destroy(tex);
                stereoAOTargets.Remove(__instance);
            }
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

            Matrix4x4 viewMatrix = __instance._camera.GetStereoViewMatrix(eye);
            __instance.GetMaterial().SetMatrix(FakeCharacterGI._inverseView, viewMatrix);
            Graphics.Blit(src, dest, __instance.GetMaterial());
            return false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UltimateBloom), "OnRenderImage")]
        private static bool DisableUltimateBloom(UltimateBloom __instance)
        {
            if (__instance.gameObject.name != "FPS Camera")
                return true;

            __instance.enabled = false;
            return false;
        }       
        
        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Aiming lowers LodBiasFactor, which culls a lot. Keep it at the setting's value (normal is 1).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CameraLodBiasController), "SetBiasByFov")]
        private static void FixAimCulling(CameraLodBiasController __instance)
        {
            __instance.LodBiasFactor = VRSettings.GetLodBias();

        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DistantShadow), "Awake")]
        private static void FixDistantShadows(DistantShadow __instance)
        {
            __instance.EnableMultiviewTiles = true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ThermalVision), "Switch")]
        private static void FixThermalsDoubleVision(ThermalVision __instance)
        {
            __instance.IsMotionBlurred = false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FastBlur), "Start")]
        private static void ReduceDamageBlur(FastBlur __instance)
        {
            __instance._downsampleTexDimension = FastBlur.Dimensions._1024;
            __instance._upsampleTexDimension = FastBlur.Dimensions._2048;
            __instance._blurCount = 2;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

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

    }
}