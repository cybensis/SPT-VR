using EFT.UI;
using EFT.UI.Matchmaker;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static TarkovVR.Patches.UI.UIPatchShared;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class TransitUIPatches
    {
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MatchmakerTimeHasCome), "Show", new Type[] { typeof(MatchmakerTimeHasCome.TimeHasComeScreenClass) })]
        private static void PositionMenuAfterTransit(MatchmakerTimeHasCome __instance)
        {
            VRGlobals.menuUi = __instance.transform.root;
            MainMenuUIPatches.PositionMainMenuUi();
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LocationTransitTimerPanel), "Show")]
        private static void PositionTransitPanel(LocationTransitTimerPanel __instance)
        {
            UIPatches.gameUi.transform.parent = VRGlobals.player.gameObject.transform;
            UIPatches.gameUi.transform.localScale = new Vector3(0.0008f, 0.0008f, 0.0008f);
            UIPatches.gameUi.transform.localPosition = new Vector3(0.02f, 1.7f, 0.48f);
            UIPatches.gameUi.transform.localEulerAngles = new Vector3(29.7315f, 0.4971f, 0f);
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransferItemsInRaidScreen), "Show", new Type[] { typeof(TransferItemsInRaidScreen.GClass3603) })]
        private static void ShowTransitTransferMenu(TransferItemsInRaidScreen __instance)
        {
            UIPatches.HandleOpenInventory();
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransferItemsInRaidScreen), "Close")]
        private static void HideTransitTransferMenu(TransferItemsInRaidScreen __instance)
        {
            UIPatches.HandleCloseInventory();
        }
    }
}
