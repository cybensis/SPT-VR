using Comfort.Common;
using EFT.CameraControl;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;
using static UnityEngine.ParticleSystem.PlaybackState;
using static Val;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class VisualPatches
    {
        private static Camera postProcessingStoogeCamera;

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
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Holy fuck this actually fixes so many visual problems :)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAPropagator), "OnRenderImage")]
        private static bool ReturnVROutputWidth(SSAAPropagator __instance, RenderTexture source, RenderTexture destination)
        {
            if (__instance._postProcessLayer != null)
            {
                Graphics.Blit(source, destination);
                return false;
            }
            __instance._currentDestinationHDR = 0;
            __instance._currentDestinationLDR = 0;
            __instance._HDRSourceDestination = true;
            int width = Camera.main.pixelWidth;
            int height = Camera.main.pixelHeight;
            if (__instance._resampledColorTargetHDR[0] == null || __instance._resampledColorTargetHDR[0].width != width || __instance._resampledColorTargetHDR[0].height != height || __instance._resampledColorTargetHDR[0].format != RuntimeUtilities.defaultHDRRenderTextureFormat)
            {
                if (__instance._resampledColorTargetHDR[0] != null)
                {
                    __instance._resampledColorTargetHDR[0].Release();
                    RuntimeUtilities.SafeDestroy(__instance._resampledColorTargetHDR[0]);
                    __instance._resampledColorTargetHDR[0] = null;
                }
                if (__instance._resampledColorTargetHDR[1] != null)
                {
                    __instance._resampledColorTargetHDR[1].Release();
                    RuntimeUtilities.SafeDestroy(__instance._resampledColorTargetHDR[1]);
                    __instance._resampledColorTargetHDR[1] = null;
                }
                RenderTextureFormat defaultHDRRenderTextureFormat = RuntimeUtilities.defaultHDRRenderTextureFormat;
                __instance._resampledColorTargetHDR[0] = new RenderTexture(width, height, 0, defaultHDRRenderTextureFormat);
                __instance._resampledColorTargetHDR[0].name = "SSAAPropagator0HDR";
                __instance._resampledColorTargetHDR[0].enableRandomWrite = true;
                __instance._resampledColorTargetHDR[0].Create();
                __instance._resampledColorTargetHDR[1] = new RenderTexture(width, height, 0, defaultHDRRenderTextureFormat);
                __instance._resampledColorTargetHDR[1].name = "SSAAPropagator1HDR";
                __instance._resampledColorTargetHDR[1].enableRandomWrite = true;
                __instance._resampledColorTargetHDR[1].Create();
            }
            if (__instance._resampledColorTargetLDR[0] == null || __instance._resampledColorTargetLDR[0].width != width || __instance._resampledColorTargetLDR[0].height != height)
            {
                if (__instance._resampledColorTargetLDR[0] != null)
                {
                    __instance._resampledColorTargetLDR[0].Release();
                    RuntimeUtilities.SafeDestroy(__instance._resampledColorTargetLDR[0]);
                    __instance._resampledColorTargetLDR[0] = null;
                }
                if (__instance._resampledColorTargetLDR[1] != null)
                {
                    __instance._resampledColorTargetLDR[1].Release();
                    RuntimeUtilities.SafeDestroy(__instance._resampledColorTargetLDR[1]);
                    __instance._resampledColorTargetLDR[1] = null;
                }
                if (__instance._resampledColorTargetLDR[2] != null)
                {
                    __instance._resampledColorTargetLDR[2].Release();
                    RuntimeUtilities.SafeDestroy(__instance._resampledColorTargetLDR[2]);
                    __instance._resampledColorTargetLDR[2] = null;
                }
                RenderTextureFormat format = RenderTextureFormat.ARGB32;
                __instance._resampledColorTargetLDR[0] = new RenderTexture(width, height, 0, format);
                __instance._resampledColorTargetLDR[1] = new RenderTexture(width, height, 0, format);
                __instance._resampledColorTargetLDR[1].name = "SSAAPropagator1LDR";
                __instance._resampledColorTargetLDR[2] = new RenderTexture(width, height, 0, format);
                __instance._resampledColorTargetLDR[2].name = "Stub";
            }
            if ((double)Mathf.Abs(__instance.m_ssaa.GetCurrentSSRatio() - 1f) < 0.001)
            {
                Graphics.Blit(source, __instance._resampledColorTargetHDR[0]);
            }
            else if (__instance.m_ssaa.GetCurrentSSRatio() > 1f)
            {
                __instance.m_ssaa.RenderImage(__instance.m_ssaa.GetRT(), __instance._resampledColorTargetHDR[0], flipV: true, null);
            }
            else
            {
                __instance.m_ssaa.RenderImage(source, __instance._resampledColorTargetHDR[0], flipV: true, null);
            }
            if (__instance._cmdBuf == null)
            {
                __instance._cmdBuf = new CommandBuffer();
                __instance._cmdBuf.name = "SSAAPropagator";
            }
            __instance._cmdBuf.Clear();
            if (!__instance._thermalVisionIsOn && (__instance._opticLensRenderer != null || __instance._collimatorRenderer != null))
            {
                if (__instance._resampledDepthTarget == null || __instance._resampledDepthTarget.width != width || __instance._resampledDepthTarget.height != height)
                {
                    if (__instance._resampledDepthTarget != null)
                    {
                        __instance._resampledDepthTarget.Release();
                        RuntimeUtilities.SafeDestroy(__instance._resampledDepthTarget);
                        __instance._resampledDepthTarget = null;
                    }
                    __instance._resampledDepthTarget = new RenderTexture(width, height, 24, RenderTextureFormat.Depth);
                    __instance._resampledDepthTarget.name = "SSAAPropagatorDepth";
                }
                __instance._cmdBuf.BeginSample("OutputOptic");
                __instance._cmdBuf.EnableShaderKeyword(SSAAPropagator.KWRD_TAA);
                __instance._cmdBuf.EnableShaderKeyword(SSAAPropagator.KWRD_NON_JITTERED);
                __instance._cmdBuf.SetGlobalMatrix(SSAAPropagator.ID_NONJITTEREDPROJ, GL.GetGPUProjectionMatrix(__instance._camera.nonJitteredProjectionMatrix, renderIntoTexture: true));
                __instance._cmdBuf.SetRenderTarget(__instance._resampledColorTargetHDR[0], __instance._resampledDepthTarget);
                __instance._cmdBuf.ClearRenderTarget(clearDepth: true, clearColor: false, Color.black);
                if (__instance._opticLensRenderer == null && __instance._collimatorRenderer != null)
                {
                    __instance._cmdBuf.DrawRenderer(__instance._collimatorRenderer, __instance._collimatorMaterial);
                }
                if (__instance._opticLensRenderer != null)
                {
                    if (__instance._sightNonLensRenderers != null && __instance._sightNonLensRenderers.Length != 0)
                    {
                        __instance._cmdBuf.BeginSample("DEPTH_PREPASS");
                        __instance._cmdBuf.SetRenderTarget(__instance._resampledColorTargetLDR[2], __instance._resampledDepthTarget);
                        __instance._cmdBuf.BeginSample("SIGHT_DEPTH");
                        for (int i = 0; i < __instance._sightNonLensRenderers.Length; i++)
                        {
                            if (__instance._sightNonLensRenderers[i] != null && __instance._sightNonLensRenderersMaterials[i] != null && __instance._sightNonLensRenderers[i].gameObject.activeSelf)
                            {
                                __instance._cmdBuf.DrawRenderer(__instance._sightNonLensRenderers[i], __instance._sightNonLensRenderersMaterials[i]);
                            }
                        }
                        __instance._cmdBuf.EndSample("SIGHT_DEPTH");
                        __instance._cmdBuf.BeginSample("WEAPON_DEPTH");
                        for (int j = 0; j < __instance._otherWeaponRenderers.Length; j++)
                        {
                            if (__instance._otherWeaponRenderers[j] != null && __instance._otherWeaponRenderersMaterials[j] != null && __instance._otherWeaponRenderers[j].gameObject.activeSelf)
                            {
                                __instance._cmdBuf.DrawRenderer(__instance._otherWeaponRenderers[j], __instance._otherWeaponRenderersMaterials[j]);
                            }
                        }
                        __instance._cmdBuf.EndSample("WEAPON_DEPTH");
                        __instance._cmdBuf.EndSample("DEPTH_PREPASS");
                    }
                    __instance._cmdBuf.SetRenderTarget(__instance._resampledColorTargetHDR[0], __instance._resampledDepthTarget);
                    __instance._cmdBuf.DrawRenderer(__instance._opticLensRenderer, __instance._opticLensMaterial);
                }
                __instance._cmdBuf.SetRenderTarget(destination);
                __instance._cmdBuf.DisableShaderKeyword(SSAAPropagator.KWRD_NON_JITTERED);
                __instance._cmdBuf.DisableShaderKeyword(SSAAPropagator.KWRD_TAA);
                __instance._cmdBuf.EndSample("OutputOptic");
            }
            if ((bool)__instance._nightVisionMaterial)
            {
                __instance._cmdBuf.EnableShaderKeyword(SSAAPropagator.KWRD_NIGHTVISION_NOISE);
                __instance._cmdBuf.Blit(__instance._resampledColorTargetHDR[0], __instance._resampledColorTargetHDR[1], __instance._nightVisionMaterial);
                __instance._cmdBuf.DisableShaderKeyword(SSAAPropagator.KWRD_NIGHTVISION_NOISE);
                __instance._currentDestinationHDR = 1;
            }
            else if (__instance._thermalVisionIsOn && __instance._thermalVisionMaterial != null)
            {
                int pass = 1;
                __instance._cmdBuf.Blit(__instance._resampledColorTargetHDR[0], __instance._resampledColorTargetHDR[1], __instance._thermalVisionMaterial, pass);
                __instance._currentDestinationHDR = 1;
            }
            Graphics.ExecuteCommandBuffer(__instance._cmdBuf);
            return false;
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
            //    __instance.m_Camera = postProcessingStoogeCamera;
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


        // This also uses Screen width and height so need to fix it
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "RenderImage", new Type[] {typeof(RenderTexture), typeof(RenderTexture), typeof(bool), typeof(CommandBuffer) })]
        private static bool FixSSAAImplRenderImage(SSAAImpl __instance, RenderTexture source, RenderTexture destination, bool flipV, CommandBuffer externalCommandBuffer)
            {
            int shaderPass = 0;
            if (__instance.CurrentState == SSAAImpl.SSState.UPSCALE)
            {
                bool flag = ((__instance.EnableDLSS && !__instance._failedToInitializeDLSS && !__instance.NeedToApplySwitch()) || (__instance.EnableDLSS && (__instance.DLSSDebugDisable || DLSSWrapper.WantToDebugDLSSViaRenderdoc))) && !__instance.InventoryBlurIsEnabled;
                bool flag2 = __instance.EnableFSR && !__instance._failedToInitializeFSR && !flag && !__instance.NeedToApplySwitch();
                bool flag3 = __instance.EnableFSR2 && !__instance._failedToInitializeFSR2 && !flag && !__instance.NeedToApplySwitch();
                if ((flag && __instance.TryRenderDLSS(source, destination, externalCommandBuffer)) || (flag2 && __instance.TryRenderFSR(source, destination, externalCommandBuffer)) || (flag3 && __instance.TryRenderFSR2(source, destination, externalCommandBuffer)))
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
        // POST FX - Turning it off gains about 8-10 FPS

        // When aiming a lot of stuff gets culled do to the lowered LodBiasFactor, so set this to a minimum of 1 which is whats normal
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CameraLodBiasController), "SetBiasByFov")]
        private static void FixAimCulling(CameraLodBiasController __instance)
        {
            __instance.LodBiasFactor = 1;
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DistantShadow), "Awake")]
        private static void FixDistantShadows(DistantShadow __instance)
        {
            __instance.EnableMultiviewTiles = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CC_Base), "Awake")]
        private static void OptionalDisableSharpenAwake(CC_Base __instance)
        {
            if (__instance is CC_Sharpen)
            {
                if (!VRSettings.GetSharpenOn())
                    __instance.enabled = false;
                else
                    __instance.enabled = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CC_Base), "Start")]
        private static void OptionalDisableSharpenStart(CC_Base __instance)
        {
            if (__instance is CC_Sharpen)
            {
                if (!VRSettings.GetSharpenOn())
                    __instance.enabled = false;
                else
                    __instance.enabled = true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ThermalVision), "method_1")]
        private static void FixThermalsDoubleVision(ThermalVision __instance)
        {
           __instance.IsMotionBlurred = false;
        }

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