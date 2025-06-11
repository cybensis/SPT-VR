using EFT.UI.Ragfair;
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using EFT.UI.Insurance;
using EFT.UI.DragAndDrop;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class TraderFleaMarketUIPatches
    {
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AddOfferWindow), "method_17")]
        private static void PositionOfferWindow(AddOfferWindow __instance)
        {
            foreach (Transform child in __instance.gameObject.transform)
            {
                child.localPosition = Vector3.zero;
            }
            __instance.gameObject.transform.localPosition = Vector3.zero;

        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        //Not needed anymore with the new way I'm handling opening inventory. I take that back, maybe is needed but now checking if you're in game/hideout, if so skip    
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransferItemsScreen), "Show", new Type[] { typeof(TransferItemsScreen.GClass3604) })]
        private static void UndoRotationOnTransferItems(TransferItemsScreen __instance)
        {
            if (VRGlobals.inGame)
                return;
            __instance.WaitOneFrame(delegate
            {
                VRGlobals.camRoot.transform.rotation = Quaternion.identity;
            });
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RagfairFilterWindow), "Show")]
        private static void PositionFleaMarketFilterWindow(RagfairFilterWindow __instance)
        {
            __instance.transform.localPosition = Vector3.zero;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Stupid hover thingy blocks the autofill from being selected
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HoverTooltipArea), "Show")]
        private static void HideTraderAutoFillHover(HoverTooltipArea __instance)
        {
            if (__instance.name == "Hover")
            {
                __instance.gameObject.active = false;
                if (__instance.GetComponent<UnityEngine.UI.Image>())
                {
                    __instance.GetComponent<UnityEngine.UI.Image>().enabled = false;
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TraderScreensGroup), "Awake")]
        private static void PositionTraderSeparatorBars(TraderScreensGroup __instance)
        {
            // Need to wait a bit before setting the FoV on this cam because
            // something else is changing it
            if (__instance._traderCardsContainer)
            {
                RectTransform separator = (RectTransform)__instance._traderCardsContainer.parent.Find("SeparatorTop");
                separator.sizeDelta = new Vector2(1920, 2);
                separator.localPosition = new Vector3(0, 87.5f, 0);
                separator = (RectTransform)__instance._traderCardsContainer.parent.Find("SeparatorBottom");
                separator.sizeDelta = new Vector2(1920, 2);
                separator.localPosition = new Vector3(0, -87.5f, 0);
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InsuranceWindow), "Show")]
        private static void FixInsuranceWindowPosition(InsuranceWindow __instance)
        {
            __instance.transform.localPosition = Vector3.zero;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RagfairOfferItemView), "Show")]
        private static void ResetOfferImageRotation(RagfairOfferItemView __instance)
        {
            __instance.MainImage.transform.localRotation = Quaternion.Euler(0, 0, __instance.MainImage.transform.localEulerAngles.z);
        }
    }
}
