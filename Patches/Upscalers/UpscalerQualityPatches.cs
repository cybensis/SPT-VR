using EFT.CameraControl;
using EFT.Settings.Graphics;
using HarmonyLib;
using UnityEngine;

namespace TarkovVR.Patches.Upscalers
{
    [HarmonyPatch]
    internal class UpscalerQualityPatches
    {
        // The mode is applied several times as the settings load: log a scale only when it changes
        private static string lastScaleLog;
        private static void LogScaleOnce(string line)
        {
            if (line == lastScaleLog) return;
            lastScaleLog = line;
            Plugin.MyLog.LogInfo(line);
        }

        // Adjusts VR eye resolution scale when DLSS/FSR is enabled
        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "SetAntiAliasing")]
        private static bool UpdateVREyeResolutionForDLSS(CameraManager __instance, EAntialiasingMode quality, EDLSSMode dlssMode, EFSR2Mode fsr2Mode, EFSR3Mode fsr3Mode)
        {
            bool dlssOn = dlssMode > EDLSSMode.Off;
            bool fsr2On = fsr2Mode > EFSR2Mode.Off;
            bool fsr3On = fsr3Mode > EFSR3Mode.Off;
            SSAAImpl ssaaimpl_ = __instance._ssaaImpl;
            __instance.SSAA.UseJitter = dlssOn || fsr2On || fsr3On;
            if ((bool)ssaaimpl_)
            {
                ssaaimpl_.EnableDLSS = false;
            }

            if (dlssOn)
            {
                if ((bool)ssaaimpl_)
                {
                    ssaaimpl_.DLSSQualityNext = GraphicsSettingsGroup.GetDLSSQuality(dlssMode);
                    ssaaimpl_.EnableDLSS = true;
                    float num = ssaaimpl_.ComputeOptimalResamplingFactor(__instance.SSAA.GetOutputWidth(), __instance.SSAA.GetOutputHeight(), ssaaimpl_.DLSSQualityNext);
                    __instance.SSAA.Switch(num);
                    float x = Mathf.Log(num, 2f) - 1f;
                    Shader.SetGlobalVector(value: new Vector4(x, 1f, 2f, 3f), nameID: Shader.PropertyToID("_DLSSParams"));
                }

                if (VRGlobals.VRCam != null)
                    VRGlobals.VRCam.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;


                float scale = dlssMode switch
                {
                    EDLSSMode.Quality => 0.775f,          // 1.5x
                    EDLSSMode.Balanced => 0.6667f,         // 1.72x (58%)
                    EDLSSMode.Performance => 0.5814f,         // 2.0x
                    EDLSSMode.UltraPerformance => 0.5f, // 3.0x
                    _ => 1f
                };

                ApplyUpscalerScale(scale, ssaaimpl_, true);

                LogScaleOnce($"[VR DLSS] Set eye texture scale to {scale} for mode {dlssMode}");
            }
            else if (fsr2On || fsr3On)
            {
                float scale;
                string label;

                if (fsr3On)
                {
                    scale = fsr3Mode switch
                    {
                        EFSR3Mode.Quality => 0.775f,          // 1.5x
                        EFSR3Mode.Balanced => 0.6667f,         // 1.7x
                        EFSR3Mode.Performance => 0.5882f,         // 2.0x
                        EFSR3Mode.UltraPerformance => 0.5f, // 3.0x
                        _ => 1f
                    };
                    label = $"[VR FSR3] Set eye texture scale to {scale} for mode {fsr3Mode}";
                }
                else
                {
                    scale = fsr2Mode switch
                    {
                        EFSR2Mode.Quality => 0.775f,          // 1.5x
                        EFSR2Mode.Balanced => 0.6667f,         // 1.7x
                        EFSR2Mode.Performance => 0.5882f,         // 2.0x
                        EFSR2Mode.UltraPerformance => 0.5f, // 3.0x
                        _ => 1f
                    };
                    label = $"[VR FSR2] Set eye texture scale to {scale} for mode {fsr2Mode}";
                }

                ApplyUpscalerScale(scale, ssaaimpl_, false);
                LogScaleOnce(label);
            }
            else
            {
                // Reset to native when all upscalers are off
                VRGlobals.upscalingMultiplier = 1f;
                if (VRGlobals.VRCam != null && VRGlobals.VRCam.name == "FPS Camera")
                    VRGlobals.VRCam.rect = new Rect(0f, 0f, 1f, 1f);
                ssaaimpl_.EnableDLSS = false;
            }
            __instance.method_3();
            return false;
        }
        // VR Native AA (DLAA-style): render at native (or near) and let FSR/DLSS run purely as an anti-
        // aliaser. VR sets the camera rect DIRECTLY here (not the game's SSAA.Switch state machine), so the
        // flatscreen NOSCALE gate that forced 0.99 there doesn't apply — 1.0 is worth trying for TRUE native.
        // If FSR stops running at exactly 1.0 (jitter appears, same tell as flatscreen), set this to 0.99.
        public static float NativeAAScale = 1.0f;

        // The quality mode's real scale (pre Native-AA), stashed so the menu toggle can re-apply live.
        private static float _lastBaseScale = -1f;
        private static bool _lastIsDlss;

        private static void ApplyUpscalerScale(float scale, SSAAImpl ssaaimpl, bool isDlss)
        {
            _lastBaseScale = scale;
            _lastIsDlss = isDlss;
            if (Source.Settings.VRSettings.GetNativeAA())
                scale = NativeAAScale;

            VRGlobals.upscalingMultiplier = scale;
            VRJitterHelper.SetSampleCountForScale(Mathf.Min(scale, 1.0f), isDlss);
            if (VRGlobals.VRCam != null && VRGlobals.VRCam.name == "FPS Camera")
            {
                VRGlobals.VRCam.rect = scale < 1.0f
                    ? new Rect(0f, 0f, scale, scale)
                    : new Rect(0f, 0f, 1f, 1f);
            }
        }

        // Re-apply the last upscaler scale when the Native AA menu toggle flips (recomputes native vs the
        // stored mode scale). No need to re-select the FSR quality. No-op until a mode has been applied once.
        internal static void ReapplyForNativeAA()
        {
            if (_lastBaseScale > 0f)
                ApplyUpscalerScale(_lastBaseScale, null, _lastIsDlss);
        }
    }
}
