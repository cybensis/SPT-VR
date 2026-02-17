using EFT.Settings.Graphics;
using GPUInstancer;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Source.Graphics;
using UnityEngine;

namespace TarkovVR.Patches.Visuals
{

    [HarmonyPatch]
    internal class AOPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraClass), "SetSSAO")]
        private static bool CustomAOSet(CameraClass __instance, ESSAOMode ssaoMode)
        {
            __instance.Hbao_0.enabled = false;
            __instance.AmbientOcclusion_0.enabled = false;
            VRGlobals.ssaoMode = ssaoMode;
            AmplifyGTAO gtaoManager;
            Camera cam = __instance.Camera;
            if (cam.GetComponent<AmplifyGTAO>() != null)
                gtaoManager = cam.GetComponent<AmplifyGTAO>();
            else
                gtaoManager = cam.gameObject.AddComponent<AmplifyGTAO>();

            if (gtaoManager != null)
            {
                gtaoManager.SetAOSettings(ssaoMode);

                AmplifyOcclusionEffect gtaoEffect = cam.GetComponent<AmplifyOcclusionEffect>();
                if (ssaoMode != ESSAOMode.Off)
                    gtaoEffect.enabled = true;
                else
                    gtaoEffect.enabled = false;
            }
            
            return false;
        }


        // Dumb workaround to fix an issue with AO during winter season. Visual artifacts where grass is, need to disable AO then re-enable after grass loads.
        private static float aoRefreshTimer = -1f;
        private static float aoRefreshDelay = 3f;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.UI.PreloaderUI), nameof(EFT.UI.PreloaderUI.ShowRaidStartInfo))]
        private static void StartAOTimer()
        {
            aoRefreshTimer = 0f;
            var aoEffect = VRGlobals.VRCam?.GetComponent<AmplifyOcclusionEffect>();
            if (aoEffect != null && aoEffect.enabled)
            {
                aoEffect.enabled = false;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.VisualPass))]
        private static void CountAOTimer()
        {
            if (aoRefreshTimer < 0f)
                return;

            aoRefreshTimer += Time.deltaTime;

            if (aoRefreshTimer >= aoRefreshDelay)
            {
                aoRefreshTimer = -1f;
                
                var aoEffect = VRGlobals.VRCam?.GetComponent<AmplifyOcclusionEffect>();
                if (aoEffect != null && VRGlobals.ssaoMode != ESSAOMode.Off)
                {
                    Plugin.MyLog.LogInfo("Refreshing AO Effect");
                    aoEffect.enabled = true;
                }
            }
        }
    }
}
