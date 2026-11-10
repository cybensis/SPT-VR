using HarmonyLib;
using AmandsGraphics;

namespace TarkovVR.ModSupport.AmandsGraphics
{
    [HarmonyPatch]
    internal static class AmandsGraphicsSupport
    {

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AmandsGraphicsClass), "ActivateAmandsOpticDepthOfField")]
        private static bool BlockOpticDepthOfFieldClass(AmandsGraphicsClass __instance)
        {
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AmandsGraphicsOpticSightPatch), "PatchPostFix")]
        private static bool BlockOpticSightPatch(AmandsGraphicsOpticSightPatch __instance)
        {
            return false;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AmandsGraphicsOpticPatch), "PatchPostFix")]
        private static bool BlockOpticPatch(AmandsGraphicsOpticPatch __instance)
        {
            return false;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AmandsGraphicsmethod_25Patch), "PatchPostFix")]
        private static bool BlockSomeOtherOpticPatch(AmandsGraphicsmethod_25Patch __instance)
        {
            return false;
        }
    }
}
