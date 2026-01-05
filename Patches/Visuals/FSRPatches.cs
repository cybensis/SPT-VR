using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine;
using static SSAAImpl;
using GPUInstancer;
using EFT.Settings.Graphics;
using Comfort.Common;
using static Val;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;
using System.Diagnostics;
using Valve.VR;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class FSRPatches
    {
        // The two patches below intercept TryRenderFSR2/FSR3 and replaces with custom stereo-aware wrappers at the bottom of this file
        //-----------------------------------------------------------------------------------------------------------------------------------------------
        public static StereoFSR3Wrapper _stereoFSR3Wrapper;
        private static StereoFSR2Wrapper _stereoFSR2Wrapper;
        private static Rect _lastRect;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "TryRenderFSR3", new Type[] { typeof(RenderTexture), typeof(RenderTexture), typeof(CommandBuffer) })]
        private static bool FixFSR3Render(SSAAImpl __instance, RenderTexture source, RenderTexture destination, CommandBuffer externalCommandBuffer, ref bool __result)
        {

            if (_stereoFSR3Wrapper == null)
            {
                if (!(__instance._ssaaPropagator.EASUShader != null) || !(__instance._ssaaPropagator.RCASShader != null))
                {
                    __instance._failedToInitializeFSR3 = true;
                    Plugin.MyLog.LogError("[VR] Failed to initialize Stereo FSR3 wrapper: missing shaders");
                    __result = false;
                    return false;
                }

                _stereoFSR3Wrapper = new StereoFSR3Wrapper(
                    __instance._ssaaPropagator.FSR3AutogenReactiveShader,
                    __instance._ssaaPropagator.FSR3TCRAutogenShader,
                    __instance._ssaaPropagator.FSR3ComputeLuminancePyramidShader,
                    __instance._ssaaPropagator.FSR3ReconstructPreviousDepthShader,
                    __instance._ssaaPropagator.FSR3DepthClipShader,
                    __instance._ssaaPropagator.FSR3LockShader,
                    __instance._ssaaPropagator.FSR3AccumulateShader,
                    __instance._ssaaPropagator.FSR3RCAS2Shader,
                    __instance._ssaaPropagator.CopyDLSSResources
                );

                Plugin.MyLog.LogInfo("[VR] Stereo FSR3 wrapper initialized");
            }
            
            _stereoFSR3Wrapper.PrepareOutput(
                source,
                destination,
                VRJitterComponent.CurrentJitter,
                VRJitterHelper.CurrentSampleCount,
                __instance._currentCamera,
                __instance.OpticLensRenderer != null || __instance.CollimatorRenderer != null,
                externalCommandBuffer
            );
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "TryRenderFSR2", new Type[] { typeof(RenderTexture), typeof(RenderTexture), typeof(CommandBuffer) })]
        private static bool FixFSR2Render(SSAAImpl __instance, RenderTexture source, RenderTexture destination, CommandBuffer externalCommandBuffer, ref bool __result)
        {
            if (_stereoFSR2Wrapper == null)
            {
                if (!(__instance._ssaaPropagator.EASUShader != null) || !(__instance._ssaaPropagator.RCASShader != null))
                {
                    __instance._failedToInitializeFSR2 = true;
                    __result = false;
                    return false;
                }

                _stereoFSR2Wrapper = new StereoFSR2Wrapper(
                    __instance._ssaaPropagator.AutogenReactiveShader,
                    __instance._ssaaPropagator.TCRAutogenShader,
                    __instance._ssaaPropagator.ComputeLuminancePyramidShader,
                    __instance._ssaaPropagator.ReconstructPreviousDepthShader,
                    __instance._ssaaPropagator.DepthClipShader,
                    __instance._ssaaPropagator.LockShader,
                    __instance._ssaaPropagator.AccumulateShader,
                    __instance._ssaaPropagator.RCAS2Shader,
                    __instance._ssaaPropagator.CopyDLSSResources
                );

                Plugin.MyLog.LogInfo("[VR] Stereo FSR2 wrapper initialized");
            }    

            _stereoFSR2Wrapper.PrepareOutput(
                source,
                destination,
                VRJitterComponent.CurrentJitter,
                VRJitterHelper.CurrentSampleCount,
                __instance._currentCamera,
                __instance.OpticLensRenderer != null || __instance.CollimatorRenderer != null,
                externalCommandBuffer
            );

            __result = true;
            return false;
        }

        // FSR patches below for proper cleanup and after-transparent RT setting
        //-----------------------------------------------------------------------------------------------------------------------------------------------

        // Ensure proper cleanup of stereo wrapper on disable
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "EnableFSR3", MethodType.Setter)]
        private static bool FixEnableFSR3Setter(SSAAImpl __instance, ref bool value)
        {
            if (__instance._enableFSR3 && !value)
            {
                if (__instance._fsr3Wrapper != null)
                    __instance._fsr3Wrapper.OnDestroy();
                else if (_stereoFSR3Wrapper != null)
                    _stereoFSR3Wrapper.OnDestroy();
            }
            __instance._enableFSR3 = value;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "EnableFSR2", MethodType.Setter)]
        private static bool FixEnableFSR2Setter(SSAAImpl __instance, ref bool value)
        {
            if (__instance._enableFSR2 && !value)
            {
                if (__instance._fsr2Wrapper != null)
                    __instance._fsr2Wrapper.OnDestroy();
                else if (_stereoFSR2Wrapper != null)
                    _stereoFSR2Wrapper.OnDestroy();
            }
            __instance._enableFSR2 = value;
            return false;
        }

        // Ensure both stereo wrappers get the after-transparent RT set same as original FSR wrapper
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SSAAImpl), "SetAfterTransparentRT")]
        private static void StereoFSRSetAfterTransparentRT(SSAAImpl __instance, RenderTexture source)
        {
            int eyeIndex = (int)__instance._currentCamera.stereoActiveEye % 2;

            if (_stereoFSR3Wrapper != null)
            {
                _stereoFSR3Wrapper.SetAfterTransparentRT(source, eyeIndex);
            }
            
            if (_stereoFSR2Wrapper != null)
            {
                _stereoFSR2Wrapper.SetAfterTransparentRT(source, eyeIndex);
            }
            
        }

        // Clean up both stereo wrappers on destroy same as original FSR wrapper
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SSAAImpl), "OnDestroy")]
        private static void StereoFSROnDestroy(SSAAImpl __instance)
        {
            _stereoFSR3Wrapper?.OnDestroy();
            _stereoFSR2Wrapper?.OnDestroy();
        }

        // This patch updates the XR eye texture resolution scale based on the selected FSR mode
        //-----------------------------------------------------------------------------------------------------------------------------------------------
        
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraClass), "SetFSR3")]
        private static void UpdateVREyeResolutionForFSR3(EFSR3Mode fsr3Mode)
        {
            if (VRGlobals.VRCam != null && fsr3Mode != EFSR3Mode.Off)
            {
                VRGlobals.VRCam.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
            }

            float scale = fsr3Mode switch
            {
                EFSR3Mode.Quality => 0.95f,
                EFSR3Mode.Balanced => 0.8f,
                EFSR3Mode.Performance => 0.7f,
                EFSR3Mode.UltraPerformance => 0.5f,
                _ => 1f
            };

            VRGlobals.upscalingMultiplier = scale;

            VRJitterHelper.SetSampleCountForScale(Mathf.Min(scale, 1.0f));

            if (VRGlobals.VRCam.name == "FPS Camera")
            {
                if (scale < 1.0f)
                    VRGlobals.VRCam.rect = new Rect(0f, 0f, scale, scale);
                else
                    VRGlobals.VRCam.rect = new Rect(0f, 0f, 1f, 1f);
            }

            Plugin.MyLog.LogWarning($"[VR FSR3] Set eye texture scale to {scale} for mode {fsr3Mode}");
        }
        
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraClass), "SetFSR2")]
        private static void UpdateVREyeResolutionForFSR2(EFSR2Mode fsr2Mode)
        {
            if (VRGlobals.VRCam != null && fsr2Mode != EFSR2Mode.Off)
            {
                VRGlobals.VRCam.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
            }

            float scale = fsr2Mode switch
            {
                EFSR2Mode.Quality => 0.95f,
                EFSR2Mode.Balanced => 0.8f,
                EFSR2Mode.Performance => 0.7f,
                EFSR2Mode.UltraPerformance => 0.5f,
                _ => 1f
            };

            VRGlobals.upscalingMultiplier = scale;

            VRJitterHelper.SetSampleCountForScale(Mathf.Min(scale, 1.0f));
            if (VRGlobals.VRCam.name == "FPS Camera")
            {
                if (scale < 1.0f)
                    VRGlobals.VRCam.rect = new Rect(0f, 0f, scale, scale);
                else
                    VRGlobals.VRCam.rect = new Rect(0f, 0f, 1f, 1f);
            }
            Plugin.MyLog.LogWarning($"[VR FSR2] Set eye texture scale to {scale} for mode {fsr2Mode}");
        }

        // The two patches below are for making adjustments to FSR3 itself to make it work better in stereo VR.
        //------------------------------------------------------------------------------------------------------------------------------------------------

        // Add these as static fields in your patch class
        private static Vector3 lastHeadPosition = Vector3.zero;
        private static Quaternion lastHeadRotation = Quaternion.identity;
        private static bool isFirstFrame = true;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FSR3Wrapper), "PrepareOutput")]
        private static bool FSR3WrapperPatch(FSR3Wrapper __instance, RenderTexture source, RenderTexture destination, Vector2 jitter, int sampleCount, Camera mainCamera, bool opticOrCollimator, CommandBuffer externalCommandBuffer)
        {
            if (!(__instance._autogenReactiveShader == null) && !(__instance._tcrAutogenShader == null) && !(__instance._computeLuminancePyramidShader == null) && !(__instance._reconstructPreviousDepthShader == null) && !(__instance._depthClipShader == null) && !(__instance._lockShader == null) && !(__instance._accumulateShader == null))
            {
                _ = __instance._rcasShader == null;
            }
            __instance._mainCamera = mainCamera;
            __instance.CalcDeviceDepthToViewSpaceDepthParams(mainCamera);
            int num = ((source != null) ? source.width : Screen.width);
            int num2 = ((source != null) ? source.height : Screen.height);
            int num3 = ((destination != null) ? destination.width : Screen.width);
            int num4 = ((destination != null) ? destination.height : Screen.height);

            __instance.renderSize[0] = num;
            __instance.renderSize[1] = num2;
            __instance.displaySize[0] = num3;
            __instance.displaySize[1] = num4;
            __instance.jitterPhaseCount = sampleCount;
            if (__instance.renderSize[0] > __instance.displaySize[0])
            {
                jitter = Vector2.zero;
                __instance.jitterPhaseCount = 1;
            }
            __instance.jitterVec[0] = 0f - jitter.x;
            __instance.jitterVec[1] = 0f - jitter.y;
                 
            if (__instance._upscaledBuf == null ||__instance._upscaledBuf.width != num3 ||__instance._upscaledBuf.height != num4 ||__instance._prepared_input_color == null ||__instance._prepared_input_color.width != num ||__instance._prepared_input_color.height != num2 || __instance._bAfterAlphaSourceIsChanged)
            {
                __instance.CreateTextureInternal(ref __instance._upscaledBuf, num3, num4, source.format, "FSR3 Upscaled Buffer");
                __instance.CreateTextureInternal(ref __instance._exposure, 1, 1, RenderTextureFormat.RGFloat, "_exposure");
                __instance.CreateTextureInternal(ref __instance._spd_global_atomic, 1, 1, GraphicsFormat.R32_UInt, "_spd_global_atomic");
                __instance.CreateTextureInternal(ref __instance._reactive_mask, num, num2, GraphicsFormat.R8_UNorm, "_reactive_mask");
                __instance.CreateTextureInternal(ref __instance._transparency_and_composition_mask, num, num2, GraphicsFormat.R8_UNorm, "_transparency_and_composition_mask");
                __instance.CreateTextureInternal(ref __instance._dilated_motion_vectors0, num, num2, RenderTextureFormat.RGHalf, "_dilated_motion_vectors0");
                __instance.CreateTextureInternal(ref __instance._dilated_motion_vectors1, num, num2, RenderTextureFormat.RGHalf, "_dilated_motion_vectors1");
                __instance.CreateTextureInternal(ref __instance._internal_upscaled_color0, num3, num4, GraphicsFormat.R16G16B16A16_SFloat, "_internal_upscaled_color0");
                __instance.CreateTextureInternal(ref __instance._internal_upscaled_color1, num3, num4, GraphicsFormat.R16G16B16A16_SFloat, "_internal_upscaled_color1");
                __instance.CreateTextureInternal(ref __instance._lock_status0, num3, num4, RenderTextureFormat.RGHalf, "_lock_status0");
                __instance.CreateTextureInternal(ref __instance._lock_status1, num3, num4, RenderTextureFormat.RGHalf, "_lock_status1");
                __instance.CreateTextureInternal(ref __instance._prepared_input_color, num, num2, GraphicsFormat.R16G16B16A16_SFloat, "_prepared_input_color");
                __instance.CreateTextureInternal(ref __instance._luma_history0, num3, num4, GraphicsFormat.R8G8B8A8_UNorm, "_luma_history0");
                __instance.CreateTextureInternal(ref __instance._luma_history1, num3, num4, GraphicsFormat.R8G8B8A8_UNorm, "_luma_history1");
                __instance.CreateTextureInternal(ref __instance._dilatedDepth, num, num2, GraphicsFormat.R32_SFloat, "_dilatedDepth");
                __instance.CreateTextureInternal(ref __instance._lock_input_luma, num, num2, GraphicsFormat.R16_SFloat, "_lock_input_luma");
                __instance.CreateTextureInternal(ref __instance._new_locks, num3, num4, GraphicsFormat.R8_UNorm, "_new_locks");
                __instance.CreateTextureInternal(ref __instance._dilated_reactive_masks, num, num2, GraphicsFormat.R8G8_UNorm, "_dilated_reactive_masks");
                __instance.CreateTextureInternal(ref __instance._reconstructed_previous_nearest_depth, num, num2, GraphicsFormat.R32_UInt, "_reconstructed_previous_nearest_depth");
                __instance.CreateTextureInternal(ref __instance._beforeAlpha, num, num2, source.format, "Before Alpha");
                __instance.CreateTextureInternal(ref __instance._beforeAlphaPrev0, num, num2, source.format, "Before AlphaPrev0");
                __instance.CreateTextureInternal(ref __instance._beforeAlphaPrev1, num, num2, source.format, "Before AlphaPrev1");
                __instance.CreateTextureInternal(ref __instance._afterAlpha, num, num2, source.format, "After Alpha");
                __instance.CreateTextureInternal(ref __instance._afterAlphaPrev0, num, num2, source.format, "After AlphaPrev0");
                __instance.CreateTextureInternal(ref __instance._afterAlphaPrev1, num, num2, source.format, "After AlphaPrev1");
                __instance._cmdBufBeforeAlpha.Clear();
                __instance._cmdBufAfterAlpha.Clear();
                __instance._cmdBufBeforeAlpha.Blit(new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget), __instance._beforeAlpha);
                __instance._cmdBufAfterAlpha.Blit(__instance._rtAfterAlphaSource, __instance._afterAlpha);
                __instance._bAfterAlphaSourceIsChanged = false;
                __instance._mainCamera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, __instance._cmdBufBeforeAlpha);
                __instance._mainCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, __instance._cmdBufAfterAlpha);
                __instance._mainCamera.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, __instance._cmdBufBeforeAlpha);
                __instance._mainCamera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, __instance._cmdBufAfterAlpha);
                Graphics.ExecuteCommandBuffer(__instance._cmdBufBeforeAlpha);
                Graphics.ExecuteCommandBuffer(__instance._cmdBufAfterAlpha);
                __instance.mipCount = Mathf.FloorToInt(Mathf.Log(Mathf.Max(num / 2, num2 / 2)) / Mathf.Log(2f)) + 1;
                if ((bool)__instance._img_mip && __instance._img_mip.IsCreated())
                {
                    __instance._img_mip.Release();
                }
                __instance._img_mip = new RenderTexture(num / 2, num2 / 2, 0, RenderTextureFormat.RHalf, __instance.mipCount);
                __instance._img_mip.enableRandomWrite = true;
                __instance._img_mip.name = "__instance._img_mip";
                __instance._img_mip.useMipMap = true;
                __instance._img_mip.autoGenerateMips = false;
                __instance._img_mip.Create();
            }
            __instance._cmdBuf.Clear();
            CommandBuffer commandBuffer = ((externalCommandBuffer == null) ? __instance._cmdBuf : externalCommandBuffer);
            commandBuffer.BeginSample("FSR3");
            commandBuffer.SetComputeTextureParam(__instance._autogenReactiveShader, 0, "r_input_opaque_only", __instance._beforeAlpha);
            commandBuffer.SetComputeTextureParam(__instance._autogenReactiveShader, 0, "r_input_color_jittered", __instance._afterAlpha);
            commandBuffer.SetComputeTextureParam(__instance._autogenReactiveShader, 0, "rw_output_autoreactive", __instance._reactive_mask);
            __instance.Set_cbFSR3_Constants(__instance._autogenReactiveShader, commandBuffer);
            int num5 = 4;
            int num6 = 8;
            int val = 1 | num5 | num6;
            commandBuffer.SetComputeFloatParam(__instance._autogenReactiveShader, "gen_reactive_scale", 1f);
            commandBuffer.SetComputeFloatParam(__instance._autogenReactiveShader, "gen_reactive_threshold", opticOrCollimator ? 0.05f : 0.2f);
            commandBuffer.SetComputeFloatParam(__instance._autogenReactiveShader, "gen_reactive_binaryValue", 0.9f);
            commandBuffer.SetComputeIntParam(__instance._autogenReactiveShader, "gen_reactive_flags", val);
            int threadGroupsX = (int)Mathf.Ceil((float)num / 8f);
            int threadGroupsY = (int)Mathf.Ceil((float)num2 / 8f);
            commandBuffer.DispatchCompute(__instance._autogenReactiveShader, 0, threadGroupsX, threadGroupsY, 1);
            commandBuffer.SetComputeTextureParam(__instance._computeLuminancePyramidShader, 0, "r_input_color_jittered", source);
            commandBuffer.SetComputeTextureParam(__instance._computeLuminancePyramidShader, 0, "rw_spd_global_atomic", __instance._spd_global_atomic);
            commandBuffer.SetComputeTextureParam(__instance._computeLuminancePyramidShader, 0, "rw_img_mip_shading_change", __instance._img_mip, 4);
            commandBuffer.SetComputeTextureParam(__instance._computeLuminancePyramidShader, 0, "rw_img_mip_5", __instance._img_mip, 5);
            commandBuffer.SetComputeTextureParam(__instance._computeLuminancePyramidShader, 0, "rw_auto_exposure", __instance._exposure);
            __instance.Set_cbFSR3_Constants(__instance._computeLuminancePyramidShader, commandBuffer);
            int num7 = (int)Mathf.Ceil((float)num / 64f);
            int num8 = (int)Mathf.Ceil((float)num2 / 64f);
            commandBuffer.SetComputeIntParam(__instance._computeLuminancePyramidShader, "mips", __instance.mipCount);
            commandBuffer.SetComputeIntParam(__instance._computeLuminancePyramidShader, "numWorkGroups", num7 * num8);
            commandBuffer.SetComputeIntParams(__instance._computeLuminancePyramidShader, "workGroupOffset", __instance.zeroIntVec);
            commandBuffer.SetComputeIntParams(__instance._computeLuminancePyramidShader, "renderSize", __instance.renderSize);
            commandBuffer.DispatchCompute(__instance._computeLuminancePyramidShader, 0, num7, num8, 1);
            commandBuffer.SetComputeTextureParam(__instance._reconstructPreviousDepthShader, 0, "r_input_motion_vectors", BuiltinRenderTextureType.MotionVectors);
            commandBuffer.SetComputeTextureParam(__instance._reconstructPreviousDepthShader, 0, "r_input_depth", BuiltinRenderTextureType.ResolvedDepth);
            commandBuffer.SetComputeTextureParam(__instance._reconstructPreviousDepthShader, 0, "r_input_color_jittered", source);
            commandBuffer.SetComputeTextureParam(__instance._reconstructPreviousDepthShader, 0, "r_input_exposure", __instance._exposure);
            commandBuffer.SetComputeTextureParam(__instance._reconstructPreviousDepthShader, 0, "rw_reconstructed_previous_nearest_depth", __instance._reconstructed_previous_nearest_depth);
            commandBuffer.SetComputeTextureParam(__instance._reconstructPreviousDepthShader, 0, "rw_dilated_motion_vectors", __instance.isOddFrame ? __instance._dilated_motion_vectors0 : __instance._dilated_motion_vectors1);
            commandBuffer.SetComputeTextureParam(__instance._reconstructPreviousDepthShader, 0, "rw_dilated_depth", __instance._dilatedDepth);
            commandBuffer.SetComputeTextureParam(__instance._reconstructPreviousDepthShader, 0, "rw_lock_input_luma", __instance._lock_input_luma);
            __instance.Set_cbFSR3_Constants(__instance._reconstructPreviousDepthShader, commandBuffer);
            int threadGroupsX2 = (int)Mathf.Ceil((float)num / 8f);
            int threadGroupsY2 = (int)Mathf.Ceil((float)num2 / 8f);
            commandBuffer.DispatchCompute(__instance._reconstructPreviousDepthShader, 0, threadGroupsX2, threadGroupsY2, 1);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_reconstructed_previous_nearest_depth", __instance._reconstructed_previous_nearest_depth);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_dilated_motion_vectors", __instance.isOddFrame ? __instance._dilated_motion_vectors0 : __instance._dilated_motion_vectors1);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_dilated_depth", __instance._dilatedDepth);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_reactive_mask", __instance._reactive_mask);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_transparency_and_composition_mask", __instance._reactive_mask);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_previous_dilated_motion_vectors", __instance.isOddFrame ? __instance._dilated_motion_vectors1 : __instance._dilated_motion_vectors0);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_input_motion_vectors", BuiltinRenderTextureType.MotionVectors);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_input_color_jittered", source);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_input_depth", BuiltinRenderTextureType.ResolvedDepth);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "r_input_exposure", __instance._exposure);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "rw_dilated_reactive_masks", __instance._dilated_reactive_masks);
            commandBuffer.SetComputeTextureParam(__instance._depthClipShader, 0, "rw_prepared_input_color", __instance._prepared_input_color);
            __instance.Set_cbFSR3_Constants(__instance._depthClipShader, commandBuffer);
            int threadGroupsX3 = (int)Mathf.Ceil((float)num / 8f);
            int threadGroupsY3 = (int)Mathf.Ceil((float)num2 / 8f);
            commandBuffer.DispatchCompute(__instance._depthClipShader, 0, threadGroupsX3, threadGroupsY3, 1);
            commandBuffer.SetComputeTextureParam(__instance._lockShader, 0, "r_lock_input_luma", __instance._lock_input_luma);
            commandBuffer.SetComputeTextureParam(__instance._lockShader, 0, "rw_new_locks", __instance._new_locks);
            commandBuffer.SetComputeTextureParam(__instance._lockShader, 0, "rw_reconstructed_previous_nearest_depth", __instance._reconstructed_previous_nearest_depth);
            __instance.Set_cbFSR3_Constants(__instance._lockShader, commandBuffer);
            int threadGroupsX4 = (int)Mathf.Ceil((float)num / 8f);
            int threadGroupsY4 = (int)Mathf.Ceil((float)num2 / 8f);
            commandBuffer.DispatchCompute(__instance._lockShader, 0, threadGroupsX4, threadGroupsY4, 1);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_input_exposure", __instance._exposure);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_dilated_reactive_masks", __instance._dilated_reactive_masks);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_dilated_motion_vectors", __instance.isOddFrame ? __instance._dilated_motion_vectors0 : __instance._dilated_motion_vectors1);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_input_motion_vectors", BuiltinRenderTextureType.MotionVectors);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_internal_upscaled_color", __instance.isOddFrame ? __instance._internal_upscaled_color0 : __instance._internal_upscaled_color1);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_lock_status", __instance.isOddFrame ? __instance._lock_status0 : __instance._lock_status1);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_prepared_input_color", __instance._prepared_input_color);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_lanczos_lut", Texture2D.blackTexture);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_upsample_maximum_bias_lut", Texture2D.blackTexture);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_imgMips", __instance._img_mip);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_auto_exposure", __instance._exposure);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "r_luma_history", __instance.isOddFrame ? __instance._luma_history0 : __instance._luma_history1);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "rw_internal_upscaled_color", __instance.isOddFrame ? __instance._internal_upscaled_color1 : __instance._internal_upscaled_color0);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "rw_lock_status", __instance.isOddFrame ? __instance._lock_status1 : __instance._lock_status0);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "rw_upscaled_output", __instance.isOddFrame ? __instance._internal_upscaled_color1 : __instance._internal_upscaled_color0);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "rw_new_locks", __instance._new_locks);
            commandBuffer.SetComputeTextureParam(__instance._accumulateShader, 0, "rw_luma_history", __instance.isOddFrame ? __instance._luma_history1 : __instance._luma_history0);
            __instance.Set_cbFSR3_Constants(__instance._accumulateShader, commandBuffer);
            int threadGroupsX5 = (int)Mathf.Ceil((float)num3 / 8f);
            int threadGroupsY5 = (int)Mathf.Ceil((float)num4 / 8f);
            commandBuffer.DispatchCompute(__instance._accumulateShader, 0, threadGroupsX5, threadGroupsY5, 1);
            commandBuffer.SetComputeTextureParam(__instance._rcasShader, 0, "r_input_exposure", __instance._exposure);
            commandBuffer.SetComputeTextureParam(__instance._rcasShader, 0, "r_rcas_input", __instance.isOddFrame ? __instance._internal_upscaled_color1 : __instance._internal_upscaled_color0);
            commandBuffer.SetComputeTextureParam(__instance._rcasShader, 0, "rw_upscaled_output", __instance._upscaledBuf);
            __instance.Set_cbFSR3_Constants(__instance._rcasShader, commandBuffer);
            commandBuffer.SetComputeIntParams(__instance._rcasShader, "rcasConfig", __instance.rcasConfig);
            int threadGroupsX6 = (int)Mathf.Ceil((float)num3 / 16f);
            int threadGroupsY6 = (int)Mathf.Ceil((float)num4 / 16f);
            commandBuffer.DispatchCompute(__instance._rcasShader, 0, threadGroupsX6, threadGroupsY6, 1);
            commandBuffer.SetRenderTarget(destination);
            commandBuffer.SetViewport(new Rect(0f, 0f, num3, num4));
            if (__instance._propertiesOut == null)
            {
                __instance._propertiesOut = new MaterialPropertyBlock();
            }
            __instance._propertiesOut.SetTexture("_MainTex", __instance._upscaledBuf);
            commandBuffer.EnableShaderKeyword("UPDOWN");
            commandBuffer.DrawMesh(__instance._mesh, Matrix4x4.identity, __instance._matCopySources, 0, 1, __instance._propertiesOut);
            commandBuffer.EndSample("FSR3");
            if (externalCommandBuffer == null)
            {
                Graphics.ExecuteCommandBuffer(__instance._cmdBuf);
            }
            __instance.isOddFrame = !__instance.isOddFrame;
            __instance.frameNum++;
            return false;
        }
        
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FSR3Wrapper), "Set_cbFSR3_Constants")]
        private static bool FixFSR3Box(FSR3Wrapper __instance, ComputeShader shader, CommandBuffer cmdBuf)
        {
            cmdBuf.SetComputeIntParams(shader, "iRenderSize", __instance.renderSize);
            cmdBuf.SetComputeIntParams(shader, "iMaxRenderSize", __instance.renderSize);
            cmdBuf.SetComputeIntParams(shader, "iDisplaySize", __instance.displaySize);
            cmdBuf.SetComputeIntParams(shader, "iInputColorResourceDimensions", __instance.renderSize);
            int num = 4;
            float num2 = 2 << num;
            __instance.tempIntVec[0] = (int)((float)__instance.renderSize[0] / num2);
            __instance.tempIntVec[1] = (int)((float)__instance.renderSize[1] / num2);
            cmdBuf.SetComputeIntParams(shader, "iLumaMipDimensions", __instance.tempIntVec);
            cmdBuf.SetComputeIntParam(shader, "iLumaMipLevelToUse", num);
            cmdBuf.SetComputeIntParam(shader, "iFrameIndex", __instance.frameNum);
            cmdBuf.SetComputeFloatParams(shader, "fDeviceToViewDepth", __instance.deviceToViewDepthVec);
            cmdBuf.SetComputeFloatParams(shader, "fJitter", __instance.jitterVec);
            __instance.tempFloatVec[0] = -1f;
            __instance.tempFloatVec[1] = -1f;
            cmdBuf.SetComputeFloatParams(shader, "fMotionVectorScale", __instance.tempFloatVec);
            __instance.tempFloatVec[0] = (float)__instance.renderSize[0] / (float)__instance.displaySize[0];
            __instance.tempFloatVec[1] = (float)__instance.renderSize[1] / (float)__instance.displaySize[1];
            cmdBuf.SetComputeFloatParams(shader, "fDownscaleFactor", __instance.tempFloatVec);
            cmdBuf.SetComputeFloatParams(shader, "fMotionVectorJitterCancellation", __instance.zeroFloatVec);
            cmdBuf.SetComputeFloatParam(shader, "fPreExposure", 1f);
            cmdBuf.SetComputeFloatParam(shader, "fPreviousFramePreExposure", 1f);
            float num3 = (float)__instance.renderSize[0] / (float)__instance.renderSize[1];
            float val = Mathf.Tan(0.5f * __instance._mainCamera.fieldOfView * (Mathf.PI / 180f)) * num3;
            cmdBuf.SetComputeFloatParam(shader, "fTanHalfFOV", val);
            cmdBuf.SetComputeFloatParam(shader, "fJitterSequenceLength", __instance.jitterPhaseCount);
            float val2 = Mathf.Max(0f, Mathf.Min(1f, Time.deltaTime));
            cmdBuf.SetComputeFloatParam(shader, "fDeltaTime", val2);
            cmdBuf.SetComputeFloatParam(shader, "fDynamicResChangeFactor", 0f);
            cmdBuf.SetComputeFloatParam(shader, "fViewSpaceToMetersFactor", 1f);
            return false;
        }
    }
    public class StereoFSR3Wrapper
    {
        private const int NUM_EYES = 2;

        // One FSR3Wrapper instance per eye
        private FSR3Wrapper[] _wrappers = new FSR3Wrapper[NUM_EYES];

        public StereoFSR3Wrapper(
            ComputeShader autogenReactiveShader,
            ComputeShader tcrAutogenShader,
            ComputeShader computeLuminancePyramidShader,
            ComputeShader reconstructPreviousDepthShader,
            ComputeShader depthClipShader,
            ComputeShader lockShader,
            ComputeShader accumulateShader,
            ComputeShader RCASShader,
            Material CopyDLSSSourcesMat)
        {
            // Create separate FSR3 instances for each eye
            for (int i = 0; i < NUM_EYES; i++)
            {
                _wrappers[i] = new FSR3Wrapper(
                    autogenReactiveShader,
                    tcrAutogenShader,
                    computeLuminancePyramidShader,
                    reconstructPreviousDepthShader,
                    depthClipShader,
                    lockShader,
                    accumulateShader,
                    RCASShader,
                    CopyDLSSSourcesMat
                );
            }
        }

        public void PrepareOutput(RenderTexture source, RenderTexture destination, Vector2 jitterOffset, int jitterPhaseCount, Camera cam, bool opticOrCollimator, CommandBuffer externalCommandBuffer)
        {
            int eyeIndex = (int)cam.stereoActiveEye % NUM_EYES;

            _wrappers[eyeIndex].PrepareOutput(
                source,
                destination,
                jitterOffset,
                jitterPhaseCount,
                cam,
                opticOrCollimator,
                externalCommandBuffer
            );
        }

        public void SetAfterTransparentRT(RenderTexture source, int eyeIndex)
        {
            _wrappers[eyeIndex].SetAfterTransparentRT(source);
        }

        public void OnDestroy()
        {
            // Clean up both wrappers
            foreach (var wrapper in _wrappers)
            {
                wrapper.OnDestroy();
            }
        }
    }

    public class StereoFSR2Wrapper
    {
        private const int NUM_EYES = 2;

        // One FSR3Wrapper instance per eye
        private FSR2Wrapper[] _wrappers = new FSR2Wrapper[NUM_EYES];

        public StereoFSR2Wrapper(
            ComputeShader autogenReactiveShader,
            ComputeShader tcrAutogenShader,
            ComputeShader computeLuminancePyramidShader,
            ComputeShader reconstructPreviousDepthShader,
            ComputeShader depthClipShader,
            ComputeShader lockShader,
            ComputeShader accumulateShader,
            ComputeShader RCASShader,
            Material CopyDLSSSourcesMat)
        {
            // Create separate FSR3 instances for each eye
            for (int i = 0; i < NUM_EYES; i++)
            {
                _wrappers[i] = new FSR2Wrapper(
                    autogenReactiveShader,
                    tcrAutogenShader,
                    computeLuminancePyramidShader,
                    reconstructPreviousDepthShader,
                    depthClipShader,
                    lockShader,
                    accumulateShader,
                    RCASShader,
                    CopyDLSSSourcesMat
                );
            }
        }

        public void PrepareOutput(
            RenderTexture source,
            RenderTexture destination,
            Vector2 jitterOffset,
            int jitterPhaseCount,
            Camera cam,
            bool opticOrCollimator,
            CommandBuffer externalCommandBuffer)
        {
            // Detect which eye is rendering
            int eyeIndex = (int)cam.stereoActiveEye % NUM_EYES;

            // Use the appropriate eye's FSR3 wrapper
            _wrappers[eyeIndex].PrepareOutput(
                source,
                destination,
                jitterOffset,
                jitterPhaseCount,
                cam,
                opticOrCollimator,
                externalCommandBuffer
            );
        }

        public void SetAfterTransparentRT(RenderTexture source, int eyeIndex)
        {
            _wrappers[eyeIndex].SetAfterTransparentRT(source);
        }

        public void OnDestroy()
        {
            foreach (var wrapper in _wrappers)
            {
                wrapper.OnDestroy();
            }
        }
    }
}
