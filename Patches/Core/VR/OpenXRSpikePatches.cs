using HarmonyLib;
using TarkovVR.Source.XR;
using Valve.VR;

namespace TarkovVR.Patches.Core.VR
{
    // OPENXR SPIKE: SteamVR_TrackedObject gets no poses without OpenVR, so every camera it would drive gets
    // OpenXRHeadPose alongside it (all SPT-VR uses are index Hmd, no origin).
    [HarmonyPatch]
    internal class OpenXRSpikePatches
    {
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
