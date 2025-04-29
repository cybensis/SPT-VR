using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using KmyTarkovConfiguration.Views;
using Valve.VR;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.PostProcessing;

namespace TarkovVR.ModSupport.EFTApi
{
    [HarmonyPatch]
    internal static class EFTApiSupport
    {
        private static EFTConfigurationView configView;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFTConfigurationView), "CreateUI")]
        private static void SetEFTConfigWindow(EFTConfigurationView __instance)
        {
            __instance.transform.parent.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            __instance.transform.parent.localScale = new Vector3(0.001f, 0.001f, 0.001f);
            __instance.transform.parent.localPosition = new Vector3(0.017f, -1000.26f, 0.9748f);
            __instance.transform.parent.gameObject.layer = 5;
            configView = __instance;

        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(Drag), "OnDrag")]
        private static bool HandleDragging(Drag __instance, PointerEventData eventData)
        {
            __instance.targetRoot.position = eventData.worldPosition;
            Vector3 newPos = __instance.targetRoot.localPosition;
            newPos.y -= ((RectTransform)__instance.targetRoot.transform).rect.m_Height / 2;
            newPos.z = 0f;
            __instance.targetRoot.localPosition = newPos;
            return false;
        }

        public static void OpenCloseConfigUI()
        {
            if (configView)
                configView.State = !configView.State;
        }
    }
}
