using EFT.UI;
using EFT.UI.Gestures;
using EFT.UI.Map;
using EFT.UI.Ragfair;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Patches.Misc;
using UnityEngine;
using static TarkovVR.Patches.UI.UIPatchShared;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class MiscUIPatches
    {

        //---------------------------------------------------------------------- GESTURES PATCHES -------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GesturesQuickPanel), "Show")]
        private static bool RemoveQuickTip(GesturesQuickPanel __instance)
        {
            __instance.enabled = false;
            return false;
        }


        //---------------------------------------------------------------------- MAP PATCHES -------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PocketMapTile), "UnloadImage")]
        private static bool FixMapTilesAlwaysUnloading(PocketMapTile __instance)
        {
            return false;


        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SimplePocketMap), "BundleLoaded")]
        private static void FixMapTilesNotLoading(SimplePocketMap __instance)
        {
            if (__instance.Tiles.Count > 0)
                __instance.Tiles[0].method_0();
        }





        //---------------------------------------------------------------------- TOOL TIP PATCHES -------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Tooltip), "method_0")]
        private static bool PositionToolTips(SimpleTooltip __instance, Vector2 position)
        {
            if (UIPatches.vrUiInteracter)
            {
                __instance._mainTransform.position = UIPatches.vrUiInteracter.uiPointerPos;
                return false;
            }
            return true;
        }

        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(OfferView), "method_10")]
        private static void ActivateTooltipHoverArea(OfferView __instance)
        {
            if (__instance.Offer_0.Locked)
            {
                __instance._hoverTooltipArea.gameObject.active = true;
                // The hover area is constantly regenerated which means we need to run another OnEnter function
                // but we need to set the last object to null so it knows its different
                if (UIPatches.vrUiInteracter.lastHighlightedObject == __instance._hoverTooltipArea.gameObject)
                    UIPatches.vrUiInteracter.lastHighlightedObject = null;
            }
        }




        //---------------------------------------------------------------------- FLEA MARKET PATCHES -------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(OfferView), "Show")]
        private static void ResetZAxisOnFleaMarketTrades(OfferView __instance)
        {
            SetLocalZToZeroRecursively(__instance.gameObject);
        }
        static private void SetLocalZToZeroRecursively(GameObject current)
        {
            foreach (Transform child in current.transform)
            {
                // Set the local Z position to 0
                Vector3 localPosition = child.localPosition;
                localPosition.z = 0;
                child.localPosition = localPosition;

                // Recursively call this method for each child
                SetLocalZToZeroRecursively(child.gameObject);
            }
        }
    }
}
