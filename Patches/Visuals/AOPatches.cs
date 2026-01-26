using EFT.Settings.Graphics;
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
            AmplifyGTAO gtao;
            Camera cam = __instance.Camera;
            if (cam.GetComponent<AmplifyGTAO>() != null)
                gtao = cam.GetComponent<AmplifyGTAO>();
            else
                gtao = cam.gameObject.AddComponent<AmplifyGTAO>();

            if (gtao != null)
                gtao.SetAOSettings(ssaoMode);

            return false;
        }       
    }
}
