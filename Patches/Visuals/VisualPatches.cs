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
using Newtonsoft.Json;
using EFT;
using System.Security.Cryptography;
using Microsoft.SqlServer.Server;
using GPUInstancer;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class VisualPatches
    {
        private static Camera postProcessingStoogeCamera;
        public static DistantShadow distantShadow;



        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The volumetric light was only using the projection matrix for one eye which made it appear
        // off position in the other eye, this gets the current eyes matrix to fix this issue              
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VolumetricLightRenderer), "OnPreRender")]
        private static bool PatchVolumetricLightingToVR(VolumetricLightRenderer __instance)
        {
            if (UnityEngine.XR.XRSettings.enabled && __instance.camera_0 != null)
            {
                var currentEye = Camera.current.stereoActiveEye;
                if (currentEye == Camera.MonoOrStereoscopicEye.Mono)
                    return true;

                Camera.StereoscopicEye eye = (Camera.StereoscopicEye)currentEye;

                __instance.method_3();

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

        private static Dictionary<HBAO, RenderTexture[]> stereoAOTargets = new();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HBAO), "method_4")]
        private static bool FixHBAOMethod4Stereo(HBAO __instance, RenderTexture source, RenderTexture destination,
            Material ____hbaoMaterial, CommandBuffer ___commandBuffer_0, bool ___useTriangleBlit,
            RenderTexture ___renderTexture_3, HBAO_Core.RenderTarget ____renderTarget)
        {
            int eyeIndex = Camera.current?.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;

            if (!stereoAOTargets.ContainsKey(__instance))
            {
                stereoAOTargets[__instance] = new RenderTexture[2];
                for (int i = 0; i < 2; i++)
                    stereoAOTargets[__instance][i] = new RenderTexture(___renderTexture_3.width, ___renderTexture_3.height, 0);
            }

            var aoTarget = stereoAOTargets[__instance][eyeIndex];

            ___commandBuffer_0.Clear();
            ___commandBuffer_0.SetRenderTarget(aoTarget);
            ___commandBuffer_0.ClearRenderTarget(false, true, Color.white);
            ___commandBuffer_0.BlitFullscreenTriangle(source, aoTarget, ____hbaoMaterial, __instance.GetAoPass(), null, false, null);

            ____hbaoMaterial.SetTexture(ShaderProperties.hbaoTex, aoTarget);
            ___commandBuffer_0.BlitFullscreenTriangle(source, destination, ____hbaoMaterial, __instance.GetFinalPass(), null, false, null);
            Graphics.ExecuteCommandBuffer(___commandBuffer_0);

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

            Matrix4x4 viewMatrix = __instance.camera_0.GetStereoViewMatrix(eye);
            __instance.method_0().SetMatrix(FakeCharacterGI.int_0, viewMatrix);
            Graphics.Blit(src, dest, __instance.method_0());
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
            __instance.LodBiasFactor = 3;

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
        [HarmonyPatch(typeof(ThermalVision), "method_1")]
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