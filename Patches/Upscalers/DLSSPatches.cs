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

namespace TarkovVR.Patches.Upscalers
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
            DLSSWrapper.InitErrors result = DLSSWrapper.InitErrors.INIT_SUCCESS;

            for (int i = 0; i < NUM_EYES; i++)
            {
                var initResult = _wrappers[i].InitializeDLSS();
                if (initResult != DLSSWrapper.InitErrors.INIT_SUCCESS)
                    result = initResult;
            }

            return result;
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
