using HarmonyLib;
using TarkovVR.Source.XR;
using Valve.VR;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class OpenXRPatches
    {
        // SteamVR_TrackedObject gets no poses without OpenVR, so the cameras it would move get OpenXRHeadPose instead
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SteamVR_TrackedObject), "OnEnable")]
        private static void AddOpenXRHeadPose(SteamVR_TrackedObject __instance)
        {
            if (!OpenXRBootstrap.Active)
                return;
            if (__instance.GetComponent<OpenXRHeadPose>() == null)
                __instance.gameObject.AddComponent<OpenXRHeadPose>();
        }
    }
}
