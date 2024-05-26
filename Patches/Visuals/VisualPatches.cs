using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class VisualPatches
    {
        private static Camera postProcessingStoogeCamera;


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
        [HarmonyPatch(typeof(SSAAPropagator), "Init")]
        private static void DisableSSAA(SSAAPropagator __instance)
        {
            Plugin.MyLog.LogWarning("SSAA Init\n");
            __instance._postProcessLayer = null;

        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PostProcessLayer), "InitLegacy")]
        private static void FixPostProcessing(PostProcessLayer __instance)
        {
            if (VRGlobals.camHolder && VRGlobals.camHolder.GetComponent<Camera>() == null)
            {
                postProcessingStoogeCamera = VRGlobals.camHolder.AddComponent<Camera>();
                postProcessingStoogeCamera.enabled = false;
            }
            if (postProcessingStoogeCamera)
                __instance.m_Camera = postProcessingStoogeCamera;
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
        private static void DisableInvBlur(InventoryBlur __instance)
        {
            __instance.enabled = false;
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

        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(UltimateBloom), "OnRenderImage")]
        //private static bool DisableUltimateBloom(UltimateBloom __instance)
        //{
        //    if (__instance.gameObject.name != "FPS Camera")
        //        return true;

        //    __instance.enabled = false;
        //    return false;
        //}


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

        // SSAA causes a bunch of issues like thermal/nightvision rendering all fucky, and the scopes also render in 
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
    }
}
