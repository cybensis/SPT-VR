using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Rendering;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using EFT.Settings.Graphics;
using UnityEngine.XR;
using Comfort.Common;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class DLSSPatches
    {
        // This wrapper manages separate DLSSWrapper instances for each eye in VR
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static StereoDLSSWrapper _stereoDLSSWrapper;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "TryRenderDLSS", new Type[] { typeof(RenderTexture), typeof(RenderTexture), typeof(CommandBuffer) })]
        private static bool FixDLSSRender(SSAAImpl __instance, RenderTexture source, RenderTexture destination, CommandBuffer externalCommandBuffer, ref bool __result)
        {
            if (_stereoDLSSWrapper == null)
            {
                if (!(__instance._ssaaPropagator != null) || !(__instance._ssaaPropagator.CopyDLSSResources != null) || !(__instance._ssaaPropagator.DLSSDebugOutput != null))
                {
                    __instance._failedToInitializeDLSS = true;
                    return false;
                }

                _stereoDLSSWrapper = new StereoDLSSWrapper(
                    __instance._ssaaPropagator.CopyDLSSResources,
                    __instance._ssaaPropagator.DLSSDebugOutput
                );

                Plugin.MyLog.LogInfo("[VR] Stereo DLSS wrapper initialized");
            }

            if (_stereoDLSSWrapper != null)
            {
                _stereoDLSSWrapper.DebugMode = __instance.DLSSDebug;
                _stereoDLSSWrapper.DebugDisable = __instance.DLSSDebugDisable || DLSSWrapper.WantToDebugDLSSViaRenderdoc;
                _stereoDLSSWrapper.Quality = __instance._DLSSCurrentQuality;
                _stereoDLSSWrapper.JitterOffsets = __instance.DLSSJitter;
                _stereoDLSSWrapper.MVScale = __instance.DLSSMVScale;
            }

            if (!_stereoDLSSWrapper.IsDLSSLibraryLoaded())
            {
                DLSSWrapper.InitErrors initErrors = _stereoDLSSWrapper.InitializeDLSS();
                __instance._failedToInitializeDLSS = initErrors != DLSSWrapper.InitErrors.INIT_SUCCESS;
                if (initErrors != 0)
                {
                    return false;
                }
            }

            _stereoDLSSWrapper.CopyDepthMotion(source, destination, __instance.DepthCopyMode, externalCommandBuffer, __instance._currentCamera);
            _stereoDLSSWrapper.Sharpness = __instance.DLSSSharpness;
            _stereoDLSSWrapper.OnRenderImage(source, destination, __instance.SwapDLSSUpDown, VRJitterComponent.CurrentJitter * new Vector2(__instance.DLSSJitterXScale, __instance.DLSSJitterYScale), externalCommandBuffer, __instance._currentCamera);  // Pass camera for eye detection
            __result = true;
            return false;
        }

        // Adjusts VR eye resolution scale when DLSS/FSR is enabled
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraClass), "SetAntiAliasing")]
        private static bool UpdateVREyeResolutionForDLSS(CameraClass __instance, EAntialiasingMode quality, EDLSSMode dlssMode, EFSR2Mode fsr2Mode, EFSR3Mode fsr3Mode)
        {
            bool dlssOn = dlssMode > EDLSSMode.Off;
            bool fsr2On = fsr2Mode > EFSR2Mode.Off;
            bool fsr3On = fsr3Mode > EFSR3Mode.Off;
            SSAAImpl ssaaimpl_ = __instance.Ssaaimpl_0;
            __instance.SSAA.UseJitter = dlssOn || fsr2On || fsr3On;

            if (dlssOn)
            {
                if (VRGlobals.VRCam != null)
                {
                    VRGlobals.VRCam.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
                }

                float scale = dlssMode switch
                {
                    EDLSSMode.Quality => 1f,
                    EDLSSMode.Balanced => 0.8f,
                    EDLSSMode.Performance => 0.7f,
                    EDLSSMode.UltraPerformance => 0.5f,
                    _ => 1f
                };

                VRGlobals.upscalingMultiplier = scale;
                VRJitterHelper.SetSampleCountForScale(Mathf.Min(scale, 1.0f));

                if (VRGlobals.VRCam.name == "FPS Camera")
                    VRGlobals.VRCam.rect = new Rect(0f, 0f, scale, scale);

                Plugin.MyLog.LogWarning($"[VR DLSS] Set eye texture scale to {scale} for mode {dlssMode}");
            }
            else if (fsr2Mode == EFSR2Mode.Off && fsr3Mode == EFSR3Mode.Off)
            {
                // Reset to native when all upscalers are off
                VRGlobals.upscalingMultiplier = 1;
                if (VRGlobals.VRCam.name == "FPS Camera")
                    VRGlobals.VRCam.rect = new Rect(0f, 0f, 1f, 1f);
                XRSettings.eyeTextureResolutionScale = 1f;
            }

            if (dlssOn)
            {
                if ((bool)ssaaimpl_)
                {
                    ssaaimpl_.DLSSQualityNext = GraphicsSettingsClass.GetDLSSQuality(dlssMode);
                    ssaaimpl_.EnableDLSS = true;
                    float num = ssaaimpl_.ComputeOptimalResamplingFactor(__instance.SSAA.GetOutputWidth(), __instance.SSAA.GetOutputHeight(), ssaaimpl_.DLSSQualityNext);
                    __instance.SSAA.Switch(num);
                    float x = Mathf.Log(num, 2f) - 1f;
                    Shader.SetGlobalVector(value: new Vector4(x, 1f, 2f, 3f), nameID: Shader.PropertyToID("_DLSSParams"));
                }
            }
            __instance.method_3();
            return false;
        }

        // Adds cleanup for the stereo DLSS wrapper
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SSAAImpl), "OnDestroy")]
        private static void StereoDLSSOnDestroy(SSAAImpl __instance)
        {
            _stereoDLSSWrapper?.OnDestroy();
            _stereoDLSSWrapper = null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GraphicsSettingsClass), "smethod_1")]
        private static bool DisableResolutionCheckForDLSS(ref EDLSSMode mode, EftResolution resolution, ref EDLSSMode __result)
        {
            if (!DLSSWrapper.IsDLSSSupported())
            {
                __result = EDLSSMode.Off;
            }
            else
            {
                __result = mode;
            }
            return false;
        }

    }

    public class StereoDLSSWrapper
    {
        private const int NUM_EYES = 2;

        // One DLSSWrapper instance per eye
        private DLSSWrapper[] _wrappers = new DLSSWrapper[NUM_EYES];

        // Expose properties from the active wrapper (or sync across both)
        public float Sharpness
        {
            get => _wrappers[0].Sharpness;
            set
            {
                foreach (var wrapper in _wrappers)
                    wrapper.Sharpness = value;
            }
        }

        public bool DebugMode
        {
            get => _wrappers[0].DebugMode;
            set
            {
                foreach (var wrapper in _wrappers)
                    wrapper.DebugMode = value;
            }
        }

        public bool DebugDisable
        {
            get => _wrappers[0].DebugDisable;
            set
            {
                foreach (var wrapper in _wrappers)
                    wrapper.DebugDisable = value;
            }
        }

        public int Quality
        {
            get => _wrappers[0].Quality;
            set
            {
                foreach (var wrapper in _wrappers)
                    wrapper.Quality = value;
            }
        }

        public Vector2 JitterOffsets
        {
            get => _wrappers[0].JitterOffsets;
            set
            {
                foreach (var wrapper in _wrappers)
                    wrapper.JitterOffsets = value;
            }
        }

        public Vector2 MVScale
        {
            get => _wrappers[0].MVScale;
            set
            {
                foreach (var wrapper in _wrappers)
                    wrapper.MVScale = value;
            }
        }

        public event Action RenderTexturesAreChanged
        {
            add => _wrappers[0].RenderTexturesAreChanged += value;
            remove => _wrappers[0].RenderTexturesAreChanged -= value;
        }
        public StereoDLSSWrapper(Material CopyDLSSSourcesMat, Material DebugMaterial)
        {
            for (int i = 0; i < NUM_EYES; i++)
            {
                _wrappers[i] = new DLSSWrapper(CopyDLSSSourcesMat, DebugMaterial);
            }
        }

        public bool IsDLSSLibraryLoaded()
        {
            return _wrappers[0].IsDLSSLibraryLoaded();
        }

        public DLSSWrapper.InitErrors InitializeDLSS()
        {
            return _wrappers[0].InitializeDLSS();
        }

        public void CopyDepthMotion(
            RenderTexture source,
            RenderTexture dst,
            DLSSWrapper.DEPTH_COPY_MODE depthCopyMode,
            CommandBuffer commandBuffer,
            Camera cam)
        {
            // Detect which eye is rendering
            int eyeIndex = (int)cam.stereoActiveEye % NUM_EYES;

            // Use the appropriate eye's DLSS wrapper
            _wrappers[eyeIndex].CopyDepthMotion(source, dst, depthCopyMode, commandBuffer);
        }

        public void OnRenderImage(
            RenderTexture src,
            RenderTexture dest,
            bool flipOutputUpDown,
            Vector2 jitterOffset,
            CommandBuffer externalCommandBuffer,
            Camera cam)
        {
            // Detect which eye is rendering
            int eyeIndex = (int)cam.stereoActiveEye % NUM_EYES;

            // Use the appropriate eye's DLSS wrapper
            _wrappers[eyeIndex].OnRenderImage(src, dest, flipOutputUpDown, jitterOffset, externalCommandBuffer);
        }

        public RenderTexture GetMotionVectorsBuffer(Camera cam)
        {
            int eyeIndex = (int)cam.stereoActiveEye % NUM_EYES;
            return _wrappers[eyeIndex].MotionVectorsBuffer;
        }

        public RenderTexture GetDepthBuffer(Camera cam)
        {
            int eyeIndex = (int)cam.stereoActiveEye % NUM_EYES;
            return _wrappers[eyeIndex].DepthBuffer;
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
