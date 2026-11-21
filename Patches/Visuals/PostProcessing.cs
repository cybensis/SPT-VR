using HarmonyLib;
using UnityEngine.Rendering.PostProcessing;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class PostProcessing
    {
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PostProcessLayer), "InitLegacy")]
        private static void FixPostProcessing(PostProcessLayer __instance)
        {
            UnityEngine.Object.Destroy(__instance);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
    }
}
