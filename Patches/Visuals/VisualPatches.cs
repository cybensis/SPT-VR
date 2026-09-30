using Comfort.Common;
using EFT;
using EFT.BlitDebug;
using EFT.CameraControl;
using EFT.EnvironmentEffect;
using EFT.Rendering.Clouds;
using EFT.Settings.Graphics;
using EFT.UI;
using EFT.Visual;
using EFT.Weather;
using GPUInstancer;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using Unity.Audio;
using Unity.XR.OpenVR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;
using UnityStandardAssets.ImageEffects;
using Valve.VR;
using static HBAO_Core;
using static UnityEngine.ParticleSystem.PlaybackState;
using static Val;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class VisualPatches
    {
        private static Camera postProcessingStoogeCamera;
        public static DistantShadow distantShadow;


        // Disables eye exposure, this does not work nicely in VR at all, cloudy weather causes everything to get absurdly dark
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnvironmentManager), "Update")]
        private static void LimitExposureForVR(EnvironmentManager __instance)
        {
            __instance.PrismExposureSpeed = 0f;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The volumetric light was only using the projection matrix for one eye which made it appear
        // off position in the other eye, this gets the current eyes matrix to fix this issue              
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

        // The directional (sun) volumetric light builds its far plane corners with Camera.current.ViewportToWorldPoint at
        // farClipPlane. With the per-eye VR projection the bottom corners land just outside the frustum, so Unity logs
        // "Screen position out of view frustum" twice every frame and doesn't return a usable point. Swap those calls for one
        // that uses the active eye's matrices directly, matching the per-eye fix above.
        [HarmonyTranspiler]
        [HarmonyPatch(typeof(VolumetricLight), "SetupDirectionalLight")]
        private static IEnumerable<CodeInstruction> FixDirectionalLightFrustumCorners(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo original = AccessTools.Method(typeof(Camera), nameof(Camera.ViewportToWorldPoint), new[] { typeof(Vector3) });
            MethodInfo replacement = AccessTools.Method(typeof(VisualPatches), nameof(EyeViewportToWorldPoint));
            foreach (var code in instructions)
                yield return code.Calls(original) ? new CodeInstruction(OpCodes.Call, replacement) : code;
        }

        private static Vector3 EyeViewportToWorldPoint(Camera cam, Vector3 viewport)
        {
            if (!XRSettings.enabled || cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Mono)
                return cam.ViewportToWorldPoint(viewport);

            Camera.StereoscopicEye eye = (Camera.StereoscopicEye)cam.stereoActiveEye;
            // Point on the far plane through this viewport position, in view space (the camera looks down -Z),
            // then scaled so it sits viewport.z units in front of the camera like ViewportToWorldPoint does.
            Vector3 viewDir = cam.GetStereoProjectionMatrix(eye).inverse.MultiplyPoint(new Vector3(viewport.x * 2f - 1f, viewport.y * 2f - 1f, 1f));
            Vector3 viewPoint = viewDir * (viewport.z / -viewDir.z);
            return cam.GetStereoViewMatrix(eye).inverse.MultiplyPoint(viewPoint);
        }

        private static Dictionary<HBAO, RenderTexture[]> stereoAOTargets = new();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HBAO), "RenderHBAO")]
        private static bool FixHBAOMethod4Stereo(HBAO __instance, RenderTexture source, RenderTexture destination,
            Material ____hbaoMaterial, CommandBuffer ____cmdBuf, bool ___useTriangleBlit,
            RenderTexture ____downsampledRT, HBAO_Core.RenderTarget ____renderTarget)
        {
            int eyeIndex = Camera.current?.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;

            if (!stereoAOTargets.ContainsKey(__instance))
            {
                stereoAOTargets[__instance] = new RenderTexture[2];
                for (int i = 0; i < 2; i++)
                    stereoAOTargets[__instance][i] = new RenderTexture(____downsampledRT.width, ____downsampledRT.height, 0);
            }

            var aoTarget = stereoAOTargets[__instance][eyeIndex];

            ____cmdBuf.Clear();
            ____cmdBuf.SetRenderTarget(aoTarget);
            ____cmdBuf.ClearRenderTarget(false, true, Color.white);
            ____cmdBuf.BlitFullscreenTriangle(source, aoTarget, ____hbaoMaterial, __instance.GetAoPass(), null, false, null);

            ____hbaoMaterial.SetTexture(ShaderProperties.hbaoTex, aoTarget);
            ____cmdBuf.BlitFullscreenTriangle(source, destination, ____hbaoMaterial, __instance.GetFinalPass(), null, false, null);
            Graphics.ExecuteCommandBuffer(____cmdBuf);

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

        // When aiming a lot of stuff gets culled do to the lowered LodBiasFactor, so set this to a minimum of 1 which is whats normal
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