using EFT.UI.Screens;
using EFT.UI;
using HarmonyLib;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.UI;
using static TarkovVR.Patches.UI.UIPatchShared;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class HUDPatches
    {
        //--------------------------------------------------------------- HUD PATCHES GLOBALS ------------------------------------------------------------------------
        private static bool showAgain = false;




        //---------------------------------------------------------------------- HUD PATCHES -------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AnimatedTextPanel), "Show")]
        private static void SetAmmoCountUi(AnimatedTextPanel __instance)
        {
            if (VRGlobals.vrPlayer)
            {
                VRGlobals.vrPlayer.showScopeZoom = true;
            }
        }

        //---------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattleUIScreen<EftBattleUIScreen.GClass3575, EEftScreenType>), "ShowAmmoDetails")]
        private static void SetAmmoCountUi(BattleUIScreen<EftBattleUIScreen.GClass3575, EEftScreenType> __instance)
        {
            if (VRSettings.GetLeftHandedMode())
                __instance._ammoCountPanel.transform.localScale = new Vector3(-0.25f, 0.25f, 0.25f);
            else
                __instance._ammoCountPanel.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);

            if (VRGlobals.vrPlayer)
            {
                VRGlobals.vrPlayer.SetAmmoFireModeUi(__instance._ammoCountPanel.transform, true);
                __instance._ammoCountPanel._ammoDetails.transform.localPosition = new Vector3(136, -23, 0);
                showAgain = true;
            }
        }


        //---------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AmmoCountPanel), "ShowFireMode")]
        private static void SetFireModeUi(AmmoCountPanel __instance)
        {
            if (VRSettings.GetLeftHandedMode())
                __instance.transform.localScale = new Vector3(-0.25f, 0.25f, 0.25f);
            else
                __instance.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
            if (VRGlobals.vrPlayer)
            {
                VRGlobals.vrPlayer.SetAmmoFireModeUi(__instance.transform, false);
                showAgain = true;
            }
        }


        //---------------------------------------------------------------------------------------------------------
        // On BattleUIComponentAnimation.Hide() with name == AmmoPanel stop updating position
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattleUIComponentAnimation), "Hide")]
        private static bool HideFireModeUi(BattleUIComponentAnimation __instance, ref float delaySeconds)
        {
            showAgain = false;
            if (__instance.name == "AmmoPanel" && VRGlobals.vrPlayer)
            {
                delaySeconds = 5f;
                __instance.WaitSeconds(delaySeconds + 2, () => { if (!showAgain) VRGlobals.vrPlayer.SetAmmoFireModeUi(null, false); });
            }
            else if (__instance.name == "OpticCratePanel" && VRGlobals.vrPlayer)
            {
                __instance.WaitSeconds(delaySeconds + 2, () => { if (!showAgain) VRGlobals.vrPlayer.showScopeZoom = false; });
            }
            return true;
        }

        //---------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EftBattleUIScreen.GClass3575), "ShowAmmoCountZeroingPanel")]
        private static bool HideZeroingUI(InventoryScreenQuickAccessPanel __instance)
        {
            return false;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(NotifierView), "Awake")]
        private static void SetNotificationsUi(NotifierView __instance)
        {
            UIPatches.notifierUi = __instance;

        }


        //-----------------------------------------------------------------------------------------------------------------
        // The extraction timer is the last in the left wrist UI components to awake so use it to position everything
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ExtractionTimersPanel), "Awake")]
        private static void SetExtractionTimerAndPositionLeftWristUi(ExtractionTimersPanel __instance)
        {
            UIPatches.extractionTimerUi = __instance;
            VRGlobals.vrPlayer.PositionLeftWristUi();
        }



        //-----------------------------------------------------------------------------------------------------------------
        public static void PositionGameUi(GameUI __instance)
        {
            __instance.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            __instance.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
            UIPatches.stancePanel = UIPatches.battleScreenUi._battleStancePanel;
            UIPatches.stancePanel.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
            UIPatches.stancePanel._battleStances[0].StanceObject.transform.parent.gameObject.active = false;

            UIPatches.healthPanel = UIPatches.battleScreenUi._characterHealthPanel;
            UIPatches.healthPanel.transform.localScale = new Vector3(0.20f, 0.20f, 0.20f);

            __instance.transform.SetParent(VRGlobals.camRoot.transform, false);
            __instance.transform.localPosition = Vector3.zero;
            __instance.transform.localRotation = Quaternion.identity;

        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseNotificationView), "Init")]
        private static void DisableComponentThatBlocksText(BaseNotificationView __instance)
        {
            RectMask2D rectmask = __instance._background.GetComponent<RectMask2D>();
            if (rectmask)
                rectmask.enabled = false;
        }


        //-----------------------------------------------------------------------------------------------------------------
        // If you have a weapon equipped this gets ran as soon as you go from the loading screen to the game
        // so its a good choice to init the quick slot icons since everything should be loaded by now.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void InitQuickSlotRadialIcons(EFT.Player.FirearmController __instance)
        {
            if (__instance._player.IsYourPlayer)
                UIPatches.quickSlotUi.CreateQuickSlotUi();
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryScreenQuickAccessPanel), "AnimatedShow")]
        private static bool HideQuickSlotBar(InventoryScreenQuickAccessPanel __instance)
        {
            return false;
        }
    }
}
