using EFT.InventoryLogic;
using EFT.Settings.Graphics;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;
using static UnityEngine.Rendering.PostProcessing.PostProcessRenderContext;

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
        
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PostProcessLayer), "SetupContext")]
        private static void ForcePPStereo(PostProcessLayer __instance, PostProcessRenderContext context)
        {
            if (context != null)
            {
                context.stereoActive = true;
                context.numberOfEyes = 2;
                context.stereoRenderingMode = PostProcessRenderContext.StereoRenderingMode.MultiPass;
            }
        }
        
    }
}
