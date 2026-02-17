using EFT.Settings.Graphics;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TarkovVR.Patches.Upscalers
{
    [HarmonyPatch]
    internal class UpscalerQualityPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GClass1074), "CreateBindings")]
        private static void SetUpscalerQuality(GClass1074 __instance)
        {
            EDLSSMode dlssMode = __instance.Gparam_0.DLSSMode;
            EFSR2Mode fsr2Mode = __instance.Gparam_0.FSR2Mode;
            EFSR3Mode fsr3Mode = __instance.Gparam_0.FSR3Mode;

            bool dlssOn = dlssMode > EDLSSMode.Off;
            bool fsr2On = fsr2Mode > EFSR2Mode.Off;
            bool fsr3On = fsr3Mode > EFSR3Mode.Off;
            SSAAImpl ssaaimpl_ = __instance.CameraClass.Ssaaimpl_0;
            __instance.CameraClass.SSAA.UseJitter = dlssOn || fsr2On || fsr3On;

            if (dlssOn)
            {
                if (VRGlobals.VRCam != null)
                    VRGlobals.VRCam.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;

                float scale = dlssMode switch
                {
                    EDLSSMode.Quality => 0.77f,
                    EDLSSMode.Balanced => 0.67f,
                    EDLSSMode.Performance => 0.59f,
                    EDLSSMode.UltraPerformance => 0.50f,
                    _ => 1f
                };

                ApplyUpscalerScale(scale, ssaaimpl_, true);

                float num = ssaaimpl_.ComputeOptimalResamplingFactor(__instance.CameraClass.SSAA.GetOutputWidth(), __instance.CameraClass.SSAA.GetOutputHeight(), ssaaimpl_.DLSSQualityNext);
                Plugin.MyLog.LogWarning($"[VR DLSS] Set eye texture scale to {scale} for mode {dlssMode}");
                Plugin.MyLog.LogError($"[VR DLSS] DLSS enabled with resampling factor {num} (quality {ssaaimpl_.DLSSQualityNext})");
            }
            else if (fsr2On || fsr3On)
            {
                float scale;
                string label;

                if (fsr3On)
                {
                    scale = fsr3Mode switch
                    {
                        EFSR3Mode.Quality => 0.77f,
                        EFSR3Mode.Balanced => 0.67f,
                        EFSR3Mode.Performance => 0.59f,
                        EFSR3Mode.UltraPerformance => 0.50f,
                        _ => 1f
                    };
                    label = $"[VR FSR3] Set eye texture scale to {scale} for mode {fsr3Mode}";
                }
                else
                {
                    scale = fsr2Mode switch
                    {
                        EFSR2Mode.Quality => 0.77f,
                        EFSR2Mode.Balanced => 0.67f,
                        EFSR2Mode.Performance => 0.59f,
                        EFSR2Mode.UltraPerformance => 0.50f,
                        _ => 1f
                    };
                    label = $"[VR FSR2] Set eye texture scale to {scale} for mode {fsr2Mode}";
                }

                ApplyUpscalerScale(scale, ssaaimpl_, false);
                Plugin.MyLog.LogWarning(label);
            }
            else
            {
                // Reset to native when all upscalers are off
                VRGlobals.upscalingMultiplier = 1f;
                if (VRGlobals.VRCam != null && VRGlobals.VRCam.name == "FPS Camera")
                    VRGlobals.VRCam.rect = new Rect(0f, 0f, 1f, 1f);
                ssaaimpl_.EnableDLSS = false;
            }
        }

        private static void ApplyUpscalerScale(float scale, SSAAImpl ssaaimpl, bool isDlss)
        {
            VRGlobals.upscalingMultiplier = scale;
            VRJitterHelper.SetSampleCountForScale(Mathf.Min(scale, 1.0f), isDlss);

            if (VRGlobals.VRCam != null && VRGlobals.VRCam.name == "FPS Camera")
            {
                VRGlobals.VRCam.rect = scale < 1.0f
                    ? new Rect(0f, 0f, scale, scale)
                    : new Rect(0f, 0f, 1f, 1f);
            }
        }
    }
}
