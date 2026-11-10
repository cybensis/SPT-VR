using EFT.CameraControl;
using EFT.Settings.Graphics;
using HarmonyLib;
using TarkovVR.Source.Graphics;
using UnityEngine;

namespace TarkovVR.Patches.Visuals
{

    [HarmonyPatch]
    internal class AOPatches
    {
        // AOSix (standalone AO mod) replaces this Amplify path when installed; it owns SetSSAO then.
        // Checked lazily: at Awake, BepInEx only lists the plugins loaded before us.
        private static bool? aoSixInstalled;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraManager), "SetSSAO")]
        private static bool CustomAOSet(CameraManager __instance, ESSAOMode ssaoMode)
        {
            aoSixInstalled ??= BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.matsix.aosix");
            if (aoSixInstalled.Value)
            {
                VRGlobals.ssaoMode = ssaoMode;
                return true;
            }

            __instance._hbao.enabled = false;
            __instance._ambientOcclusion.enabled = false;
            VRGlobals.ssaoMode = ssaoMode;
            AmplifyGTAO gtaoManager;
            Camera camera = __instance.Camera;
            if (camera.GetComponent<AmplifyGTAO>() != null)
                gtaoManager = camera.GetComponent<AmplifyGTAO>();
            else
                gtaoManager = camera.gameObject.AddComponent<AmplifyGTAO>();

            if (gtaoManager != null)
            {
                gtaoManager.SetAOSettings(ssaoMode);

                AmplifyOcclusionEffect gtaoEffect = camera.GetComponent<AmplifyOcclusionEffect>();
                if (ssaoMode != ESSAOMode.Off)
                    gtaoEffect.enabled = true;
                else
                    gtaoEffect.enabled = false;
            }
            
            return false;
        }


        // Winter season: AO shows artifacts where the grass is unless it's disabled at raid start and re-enabled after the grass loads
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
