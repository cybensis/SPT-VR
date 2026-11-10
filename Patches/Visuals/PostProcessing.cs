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
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PostProcessLayer), "SetupContext")]
        private static void ForcePPStereo(PostProcessLayer __instance, PostProcessRenderContext context)
        {
            if (context != null)
            {
                context.stereoActive = true;
                context.numberOfEyes = 2;
                context.stereoRenderingMode = PostProcessRenderContext.StereoRenderingMode.SinglePassInstanced;
            }
        }
        */
    }
}
