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

    }
}
