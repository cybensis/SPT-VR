using GPUInstancer;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using static RootMotion.FinalIK.InteractionTrigger;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class GPUInstancerPatches
    {
        // This adjusts the GPUInstancer grass motion vectors to use identity matrices, preventing issues with motion vectors in stereo VR
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GPUInstancerManager), "ClearMotionVectorData")]
        private static bool OverrideGrassMotionVectorMatrices(GPUInstancerManager __instance)
        {
            if (__instance.IsMotionVectorDataClear || __instance.Camera == null || __instance.MotionVectorsCommandBuffer == null)
            {
                return false;
            }

            __instance.MotionVectorsCommandBuffer.Clear();
            __instance.MotionVectorsAfterCommandBuffer.Clear();

            if (GPUInstancerManager.bGenerateMotionVectors)
            {
                __instance.MotionVectorsCommandBuffer.SetGlobalMatrix("_PreviousVP", Matrix4x4.identity);
                __instance.MotionVectorsCommandBuffer.SetGlobalMatrix("_NonJitteredVP", Matrix4x4.identity);

                __instance.MotionVectorsCommandBuffer.SetRenderTarget(__instance.MRT, BuiltinRenderTextureType.CameraTarget);
                __instance.MotionVectorsCommandBuffer.ClearRenderTarget(clearDepth: false, clearColor: true, Color.black);

                __instance.MotionVectorsAfterCommandBuffer.SetGlobalTexture("_CameraMotionVectorsAddition", __instance.MotionVectorsTexture);
                __instance.MotionVectorsAfterCommandBuffer.SetGlobalTexture("_CameraMotionVectorsDepth", __instance.MotionVectorsDepth);
            }
            else
            {
                __instance.MotionVectorsCommandBuffer.SetRenderTarget(__instance.MotionVectorsDepth, BuiltinRenderTextureType.CameraTarget);
                __instance.MotionVectorsCommandBuffer.ClearRenderTarget(clearDepth: false, clearColor: true, Color.black);
                __instance.MotionVectorsCommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);
            }

            __instance.IsMotionVectorDataClear = true;
            return false;
        }

        // This is where GPU Instancing handles culling, it does not work properly in multipass, it only takes left eye into account.
        // Adding toggles for it because this can heavily impact performance depending on the scene and hardware.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GPUInstancerManager), "UpdateBuffers")]
        private static void DisableAllCulling(GPUInstancerManager __instance)
        {

            if (VRSettings.GetOccCulling())
                __instance.isOcclusionCulling = false;
            else
                __instance.isOcclusionCulling = true;
            if (VRSettings.GetFrusCulling())
                __instance.isFrustumCulling = false;
            else
                __instance.isFrustumCulling = true;
        }
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GPUInstancerHiZOcclusionGenerator), "Initialize")]
        private static void ForceVREnabled(GPUInstancerHiZOcclusionGenerator __instance, Camera occlusionCamera = null)
        {
            __instance.isVREnabled = true;
            GClass1262.gpuiSettings.vrRenderingMode = 1; // Multipass
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GPUInstancerCameraData), "CalculateCameraData")]
        private static bool FixGpuInstancerCulling(GPUInstancerCameraData __instance)
        {
            if (__instance.mainCamera == null)
            {
                return false;
            }
            __instance.hasOcclusionGenerator = __instance.hiZOcclusionGenerator != null && __instance.hiZOcclusionGenerator.hiZDepthTexture != null;
            // This now uses mainCamera.projectionMatrix (center) instead of left eye
            Matrix4x4 matrix4x = __instance.mainCamera.projectionMatrix * __instance.mainCamera.worldToCameraMatrix;

            if (__instance.mvpMatrixFloats == null || __instance.mvpMatrixFloats.Length != 16)
            {
                __instance.mvpMatrixFloats = new float[16];
            }
            GClass1274.Matrix4x4ToFloatArray(matrix4x, __instance.mvpMatrixFloats);

            __instance.cameraPosition = __instance.mainCamera.transform.position;

            return false;
        }
        */
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GPUInstancerManager), "Awake")]
        private static void ForceVRComputeShader(GPUInstancerManager __instance)
        {
            // Force load the VR compute shader
            var vrShader = (ComputeShader)Resources.Load(GClass1262.CAMERA_VR_COMPUTE_RESOURCE_PATH);
            if (vrShader != null)
            {
                AccessTools.StaticFieldRefAccess<ComputeShader>(typeof(GPUInstancerManager), "_cameraComputeShader") = vrShader;

                var kernelIDs = new int[GClass1262.CAMERA_COMPUTE_KERNELS.Length];
                for (int i = 0; i < kernelIDs.Length; i++)
                {
                    kernelIDs[i] = vrShader.FindKernel(GClass1262.CAMERA_COMPUTE_KERNELS[i]);
                }
                AccessTools.StaticFieldRefAccess<int[]>(typeof(GPUInstancerManager), "_cameraComputeKernelIDs") = kernelIDs;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GPUInstancerCameraData), "CalculateCameraData")]
        private static bool FixGpuInstancerCulling(GPUInstancerCameraData __instance)
        {
            if (__instance.mainCamera == null)
            {
                return false;
            }
            __instance.hasOcclusionGenerator = __instance.hiZOcclusionGenerator != null && __instance.hiZOcclusionGenerator.hiZDepthTexture != null;
            Matrix4x4 matrix4x;
            if (__instance.hasOcclusionGenerator && __instance.hiZOcclusionGenerator.isVREnabled)
            {
                // Use center projection and widen slightly
                Matrix4x4 projection = __instance.mainCamera.projectionMatrix;
                projection.m00 *= 0.9f; // Less widening needed from center
                matrix4x = projection * __instance.mainCamera.worldToCameraMatrix;
            }
            else
            {
                matrix4x = __instance.mainCamera.projectionMatrix * __instance.mainCamera.worldToCameraMatrix;
            }
            if (__instance.mvpMatrixFloats == null || __instance.mvpMatrixFloats.Length != 16)
            {
                __instance.mvpMatrixFloats = new float[16];
            }
            GClass1274.Matrix4x4ToFloatArray(matrix4x, __instance.mvpMatrixFloats);
            if (__instance.hasOcclusionGenerator && __instance.hiZOcclusionGenerator.isVREnabled && GClass1262.gpuiSettings.testBothEyesForVROcclusion)
            {
                Matrix4x4 matrix4x2 = __instance.mainCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) * __instance.mainCamera.worldToCameraMatrix;
                if (__instance.mvpMatrix2Floats == null || __instance.mvpMatrix2Floats.Length != 16)
                {
                    __instance.mvpMatrix2Floats = new float[16];
                }
                GClass1274.Matrix4x4ToFloatArray(matrix4x2, __instance.mvpMatrix2Floats);
            }
            __instance.cameraPosition = __instance.mainCamera.transform.position;
            return false;
        }
    }
}
