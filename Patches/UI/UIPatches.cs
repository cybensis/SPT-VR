using EFT.InputSystem;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;
using EFT.Interactive;
using TarkovVR.Source.Player.VRManager;
using EFT.InventoryLogic;
using EFT;
using System.Reflection;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using UnityEngine.UI;
using EFT.UI.Matchmaker;
using TarkovVR.Patches.Misc;
using EFT.UI.Ragfair;
using static RootMotion.FinalIK.GrounderQuadruped;
using EFT.HealthSystem;
using static EFT.UI.ItemsPanel;
using System.Threading.Tasks;
using UnityEngine.Profiling;
using UnityEngine.UIElements.UIR;
using EFT.UI.Screens;
using System.Linq;
using Valve.VR;
using System.Reflection.Emit;
using Comfort.Common;
using TarkovVR.Patches.Core.Player;
using EFT.Animations;
using UnityEngine.EventSystems;
using EFT.UI.Map;
using TarkovVR.Source.Settings;
using JetBrains.Annotations;
using static RootMotion.FinalIK.InteractionTrigger.Range;
using EFT.Rendering.Clouds;
using EFT.UI.Gestures;
using System.Collections;
using TarkovVR.Source.Player.Interactions;
using Aki.Reflection.Utils;
using TarkovVR.Patches.Visuals;
namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class UIPatches
    {
        private static int playerLayer = 8;
        public static CircularSegmentUI quickSlotUi;
        public static EftBattleUIScreen battleScreenUi;
        public static BattleStancePanel stancePanel;
        public static CharacterHealthPanel healthPanel;
        public static GameUI gameUi;
        public static AnimatedTextPanel opticUi;
        public static NotifierView notifierUi;
        public static ExtractionTimersPanel extractionTimerUi;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UsingPanel), "Init")]
        private static void SetGameUI(UsingPanel __instance)
        {

            gameUi = __instance.transform.root.GetComponent<GameUI>();
            battleScreenUi = VRGlobals.commonUi.GetComponent<CommonUI>().EftBattleUIScreen;
            battleScreenUi.transform.SetParent(VRGlobals.camRoot.transform, false);
            battleScreenUi.transform.localPosition = Vector3.zero;
            battleScreenUi.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
            opticUi = battleScreenUi._opticCratePanel;
            opticUi.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
            gameUi.GetComponent<RectTransform>().sizeDelta = new Vector2(2560, 1440);
            //VRGlobals.vrPlayer.interactionUi = UIPatches.battleScreenUi.ActionPanel._interactionButtonsContainer;
            if (!VRGlobals.camRoot)
                return;

            PositionGameUi(gameUi);

        }

        // If you have a weapon equipped this gets ran as soon as you go from the loading screen to the game
        // so its a good choice to init the quick slot icons since everything should be loaded by now.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void InitQuickSlotRadialIcons(EFT.Player.FirearmController __instance)
        {
            if (__instance._player.IsYourPlayer)
                UIPatches.quickSlotUi.CreateQuickSlotUi();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NotifierView), "Awake")]
        private static void SetNotificationsUi(NotifierView __instance)
        {
            notifierUi = __instance;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseNotificationView), "Init")]
        private static void DisableComponentThatBlocksText(BaseNotificationView __instance)
        {
            RectMask2D rectmask = __instance._background.GetComponent<RectMask2D>();
            if (rectmask)
                rectmask.enabled = false;
        }


        // The extraction timer is the last in the left wrist UI components to awake so use it to position everything
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ExtractionTimersPanel), "Awake")]
        private static void SetExtractionTimerAndPositionLeftWristUi(ExtractionTimersPanel __instance)
        {
            extractionTimerUi = __instance;
            VRGlobals.vrPlayer.PositionLeftWristUi();
        }

        public static void PositionGameUi(GameUI __instance)
        {
            __instance.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            __instance.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
            stancePanel = battleScreenUi._battleStancePanel;
            stancePanel.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
            stancePanel._battleStances[0].StanceObject.transform.parent.gameObject.active = false;

            healthPanel = battleScreenUi._characterHealthPanel;
            healthPanel.transform.localScale = new Vector3(0.20f, 0.20f, 0.20f);

            __instance.transform.SetParent(VRGlobals.camRoot.transform,false);
            __instance.transform.localPosition = Vector3.zero;
            __instance.transform.localRotation = Quaternion.identity;

        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryScreen), "TranslateCommand")]
        private static void HandleCloseInventoryPatch(InventoryScreen __instance, ECommand command)
        {
            if (!VRGlobals.inGame || !VRGlobals.vrPlayer)
                return;
            if (command.IsCommand(ECommand.Escape))
            {
                //if (!__instance.Boolean_0)
                //{
                    // If the menu is closed get rid of it, there would be better ways to do this but oh well 
                    HandleCloseInventory();
                //}
            }
            if (command.IsCommand(ECommand.ToggleInventory))
            {
                HandleCloseInventory();
            }
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemsPanel), "Show")]
        private static void FixInventoryAfterRaid(ItemsPanel __instance, ItemContextAbstractClass sourceContext, CompoundItem lootItem, ISession session, InventoryController inventoryController, IHealthController health, Profile profile, InsuranceCompanyClass insurance, EquipmentBuildsStorageClass buildsStorage, EItemsTab currentTab, bool inRaid, bool isInventoryBlocked, [CanBeNull] InventoryEquipment equipment, Task __result)
        {
            __result.ContinueWith(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    if (lootItem is InventoryEquipment inventoryEquipment)
                    {
                        __instance._complexStashPanel.Show(__instance.inventoryController_0, sourceContext.CreateChild(inventoryEquipment), inventoryEquipment, __instance.profile_0.Skills, __instance.insuranceCompanyClass, __instance.itemUiContext_0);
                        __instance.UI.AddDisposable(__instance._complexStashPanel);
                        __instance.UI.AddDisposable(__instance._complexStashPanel.UnConfigure);

                    }
                    else if (lootItem != null)
                    {
                        __instance._simpleStashPanel.Show(lootItem, __instance.inventoryController_0, sourceContext.CreateChild(lootItem), inRaid, __instance.inventoryController_0.Inventory.SortingTable, SimpleStashPanel.EStashSearchAvailability.StashOnly, null, __instance.eitemsTab_0);
                        __instance.UI.AddDisposable(__instance._simpleStashPanel);
                    }
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

        }

        public static void ResyncWeaponToIK(EFT.Player.FirearmController controller)
        {
            if (controller == null || controller.WeaponRoot == null || !controller._player.IsYourPlayer)
                return;

            // Re-parent weapon under existing VR holder if available
            if (VRGlobals.weaponHolder == null)
            {
                // fallback: try to find the holder
                Transform rightHandPos = controller.WeaponRoot.parent.Find("RightHandPositioner");
                if (rightHandPos != null)
                    VRGlobals.weaponHolder = rightHandPos.GetChild(0).gameObject;
                else
                    return; // no valid holder found
            }

            // Make sure weapon is parented correctly again
            controller.WeaponRoot.SetParent(VRGlobals.weaponHolder.transform, false);

            // Apply default local offset/rotation
            controller.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);
            VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

            // Refresh VR hand references
            if (controller._player)
            {
                VRGlobals.player = controller._player;
                VRPlayerManager.leftHandGunIK = controller.HandsHierarchy.Transforms[10];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
            }
        }

        public static void HandleOpenInventory()
        {
            //PatchTracer.TraceAllMyPatches(VRGlobals.harmonyInstance);
            Cursor.lockState = CursorLockMode.Locked;
            ShowUiScreens();
            
            if (VRGlobals.player?.PlayerBody?.MeshTransform != null)
                foreach (var renderer in VRGlobals.player.PlayerBody.MeshTransform.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = false;
            if (WeaponPatches.currentGunInteractController != null)
            {
                if (WeaponPatches.currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                    foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                        renderer.enabled = false;
            }
            
            VRGlobals.menuOpen = true;
            VRGlobals.blockRightJoystick = true;
            VRGlobals.blockLeftJoystick = true;
            //VRGlobals.vrPlayer.enabled = false;
            VRGlobals.menuVRManager.enabled = true;

            Transform head = Camera.main.transform;
            // Use horizontal forward/right
            Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Quaternion flatRotation = Quaternion.LookRotation(flatForward, Vector3.up);
            Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward).normalized;

            float distance = 0.9f;
            float heightOffset = -0.4f;
            float horizontalOffset = -0.7f; // Move UI slightly to the left
            float extraYOffset = 0f;

            Vector3 basePos = VRGlobals.camRoot.transform.position; // stable world anchor

            if (VRGlobals.player.IsInPronePose)
                extraYOffset -= 1.5f;
            else
            {
                float poseLevel = VRGlobals.player.MovementContext.PoseLevel_1;
                float crouchOffset = (1 - poseLevel) * 0.6f; // 0.6f can be adjusted to change position of menu based on crouch
                extraYOffset -= crouchOffset;
            }

            Transform ui = VRGlobals.commonUi;
            ui.SetParent(VRGlobals.camRoot.transform);
            ui.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
            ui.position = VRGlobals.camRoot.transform.position + flatForward * distance + flatRight * horizontalOffset + new Vector3(0, heightOffset + extraYOffset, 0);
            ui.localEulerAngles = Vector3.zero;
            ui.rotation = flatRotation;

            if (VRGlobals.menuUi)
            {
                Transform menuUi = VRGlobals.menuUi;
                menuUi.SetParent(VRGlobals.camRoot.transform);
                menuUi.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
                menuUi.position = VRGlobals.camRoot.transform.position + flatForward * distance + flatRight * 0.05f + new Vector3(0, -0.05f + extraYOffset, 0);
                menuUi.rotation = flatRotation;
            }

            if (VRGlobals.preloaderUi)
            {
                Transform preloader = VRGlobals.preloaderUi;
                preloader.SetParent(VRGlobals.camRoot.transform);
                preloader.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
                preloader.GetChild(0).localScale = new Vector3(1.3333f, 1.3333f, 1.3333f);
                preloader.position = VRGlobals.camRoot.transform.position + flatForward * distance + flatRight * 0.05f + new Vector3(0, -0.1f + extraYOffset, 0);
                preloader.rotation = flatRotation;

                if (UIPatches.notifierUi)
                {
                    UIPatches.notifierUi.transform.parent = PreloaderUI.Instance._alphaVersionLabel.transform.parent;
                    UIPatches.notifierUi.transform.localPosition = new Vector3(1920, 0, 0);
                    UIPatches.notifierUi.transform.localRotation = Quaternion.identity;
                    UIPatches.notifierUi.transform.localScale = Vector3.one;
                }
            }
        }
        public static void HandleCloseInventory()
        {
            HideUiScreens();
            if (VRGlobals.player?.PlayerBody?.MeshTransform != null)
                foreach (var renderer in VRGlobals.player.PlayerBody.MeshTransform.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = true;
            if (WeaponPatches.currentGunInteractController != null)
            {
                if (WeaponPatches.currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                    foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                        renderer.enabled = true;
            }
            //int bitmask = 1 << playerLayer; // 256
            //Camera.main.cullingMask |= bitmask; // -524321 & -257
            VRGlobals.menuOpen = false;
            VRGlobals.blockRightJoystick = false;
            VRGlobals.blockLeftJoystick = false;
            //VRGlobals.vrPlayer.enabled = true;
            VRGlobals.menuVRManager.enabled = false;
            VRGlobals.commonUi.parent = null;
            VRGlobals.commonUi.position = new Vector3(1000, 1000, 1000);
            VRGlobals.preloaderUi.parent = null;
            VRGlobals.preloaderUi.position = new Vector3(1000, 1000, 1000);
            VRGlobals.vrPlayer.SetNotificationUi();
            //VRGlobals.vrOffsetter.transform.localRotation = Quaternion.identity;
            //VRGlobals.camRoot.transform.eulerAngles = new Vector3(0, lastCamRootYRot, 0);

        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ActionPanel), "Start")]
        private static void DisableUiPointer(ActionPanel __instance)
        {
            __instance._pointer.gameObject.SetActive(false);
            //VRGlobals.vrPlayer.interactionUi = __instance._interactionButtonsContainer;
            VRGlobals.vrPlayer.interactionUi = __instance.transform;
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ActionPanel), "method_6")]
        private static void CopyInteractionUi(ActionPanel __instance)
        {
            __instance._pointer.gameObject.SetActive(false);
            //VRGlobals.vrPlayer.interactionUi = __instance._interactionButtonsContainer;
            VRGlobals.vrPlayer.interactionUi = __instance.transform;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattleUIPanelExitTrigger), "Show")]
        private static void PositionExtractPanel(BattleUIPanelExitTrigger __instance)
        {
            gameUi.transform.parent = VRGlobals.player.gameObject.transform;
            gameUi.transform.localScale = new Vector3(0.0008f, 0.0008f, 0.0008f);
            gameUi.transform.localPosition = new Vector3(0.02f, 1.7f, 0.7f);
            gameUi.transform.localEulerAngles = new Vector3(29.7315f, 0.4971f, 0f);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(LocationTransitTimerPanel), "Show")]
        private static void PositionTransitPanel(LocationTransitTimerPanel __instance)
        {
            gameUi.transform.parent = VRGlobals.player.gameObject.transform;
            gameUi.transform.localScale = new Vector3(0.0008f, 0.0008f, 0.0008f);
            gameUi.transform.localPosition = new Vector3(0.02f, 1.7f, 0.7f);
            gameUi.transform.localEulerAngles = new Vector3(29.7315f, 0.4971f, 0f);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattleUIPanelExtraction), "Show", new Type[] { typeof(string), typeof(float) })]
        private static void PositionPlaceItemUI(BattleUIPanelExtraction __instance)
        {
            gameUi.transform.parent = VRGlobals.player.gameObject.transform;
            gameUi.transform.localScale = new Vector3(0.0008f, 0.0008f, 0.0008f);
            gameUi.transform.localPosition = new Vector3(0.02f, 1.7f, 0.7f);
            gameUi.transform.localEulerAngles = new Vector3(29.7315f, 0.4971f, 0f);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ActionPanel), "method_0")]
        private static void SetTransmitInteractionMenuActive(ActionPanel __instance, [CanBeNull] ActionsReturnClass interactionState)
        {
            try
            {
                // Check if VR player and UI are properly set up
                if (VRGlobals.vrPlayer == null || !(VRGlobals.vrPlayer is RaidVRPlayerManager manager))
                {
                    return;
                }

                if (interactionState == null)
                {
                    manager.positionTransitUi = false;
                    return;
                }

                // Check if SelectedAction or Name is null
                if (interactionState.SelectedAction == null || string.IsNullOrEmpty(interactionState.SelectedAction.Name))
                {
                    manager.positionTransitUi = false;
                    return;
                }

                // Check for the transit interaction and if the UI is available
                if (interactionState.SelectedAction.Name.Contains("Transit") && VRGlobals.vrPlayer.interactionUi != null)
                {
                    if (Camera.main == null)
                    {
                        manager.positionTransitUi = false;
                        return;
                    }

                    // Set the UI position and rotation
                    VRGlobals.vrPlayer.interactionUi.position = Camera.main.transform.position +
                        Camera.main.transform.forward * 0.4f +
                        Camera.main.transform.up * -0.2f;

                    VRGlobals.vrPlayer.interactionUi.LookAt(Camera.main.transform);
                    VRGlobals.vrPlayer.interactionUi.Rotate(0, 180, 0);
                    manager.positionTransitUi = true;
                }
                else
                {
                    manager.positionTransitUi = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"Error in SetTransmitInteractionMenuActive: {ex.Message}");
            }
        }
        
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransferItemsInRaidScreen), "Show", new Type[] { typeof(TransferItemsInRaidScreen.GClass3893) })]
        private static void ShowTransitTransferMenu(TransferItemsInRaidScreen __instance) {
            UIPatches.HandleOpenInventory();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransferItemsInRaidScreen), "Close")]
        private static void HideTransitTransferMenu(TransferItemsInRaidScreen __instance)
        {
            UIPatches.HandleCloseInventory();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryScreenQuickAccessPanel), "AnimatedShow")]
        private static bool HideQuickSlotBar(InventoryScreenQuickAccessPanel __instance)
        {
            return false;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EftBattleUIScreen.GClass3865), "ShowAmmoCountZeroingPanel")]
        private static bool HideZeroingUI(InventoryScreenQuickAccessPanel __instance)
        {
            return false;
        }

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(InventoryScreenQuickAccessPanel), "Show", new Type[] { typeof(InventoryControllerClass), typeof(ItemUiContext), typeof(GamePlayerOwner), typeof(InsuranceCompanyClass) })]
        //private static void YoinkQuickSlotImages(InventoryScreenQuickAccessPanel __instance)
        //{
        //    if (!VRGlobals.inGame)
        //        return;

        //    if (quickSlotUi == null)
        //    {
        //        GameObject quickSlotHolder = new GameObject("quickSlotUi");
        //        quickSlotHolder.layer = 5;
        //        quickSlotHolder.transform.parent = VRGlobals.vrPlayer.LeftHand.transform;
        //        quickSlotUi = quickSlotHolder.AddComponent<CircularSegmentUI>();
        //        quickSlotUi.Init();
        //        //circularSegmentUI.CreateQuickSlotUi(mainImagesList.ToArray());
        //    }
        //    quickSlotUi.gameObject.active = false;

        //}


        // When the grid is being initialized we need to make sure the rotation is 0,0,0 otherwise the grid items don't
        // spawn in because of their weird code.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridViewMagnifier), "method_3")]
        private static void ReturnCommonUiToZeroRot(GridViewMagnifier __instance)
        {
            __instance.transform.root.rotation = Quaternion.identity;
        }



        // When in hideout the stash panel also gets shown which causes the UI to reposition/rotate so only rely
        // on this patch if its in raid, for hideout use PositionInHideoutInventory()
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GClass3808), "Show")]
        private static void PositionInRaidInventory(GClass3808 __instance)
        {
            // Dont open inv if not in game, player is in hideout, game player isn't set and the menu isn't already open
            if (!VRGlobals.inGame || VRGlobals.vrPlayer is HideoutVRPlayerManager || !VRGlobals.player || VRGlobals.menuOpen)
                return;

            HandleOpenInventory();

        }

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(OverallScreen), "Show")]
        //private static void PositionInRaidOverallInvScreen(OverallScreen __instance)
        //{
        //    // Dont open inv if not in game, player is in hideout, game player isn't set and the menu isn't already open
        //    if (!VRGlobals.inGame || VRGlobals.vrPlayer is HideoutVRPlayerManager || !VRGlobals.player || VRGlobals.menuOpen)
        //        return;

        //    HandleOpenInventory();

        //}


        // If the canvas roots rotation isn't 0,0,0 the grid/slot items display on an angle
        // so these patches prevent them from being on an angle
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GridView), "method_5")]
        private static void PreventOffAxisGridItemsViews(GridView __instance, ItemView itemView)
        {
            itemView.transform.localEulerAngles = Vector3.zero;
            itemView.MainImage.transform.localEulerAngles = new Vector3(0, 0, itemView.MainImage.transform.localEulerAngles.z);
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SlotView), "method_4")]
        private static void PreventOffAxisSlotItemsViews(SlotView __instance)
        {
            __instance.itemView_0.transform.localEulerAngles = Vector3.zero;
            __instance.itemView_0.MainImage.transform.localEulerAngles = new Vector3(0, 0, __instance.itemView_0.MainImage.transform.localEulerAngles.z);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ModSlotView), "Show")]
        private static void PreventOffAxisModSlotItemsViews(ModSlotView __instance)
        {
            __instance.transform.localEulerAngles = Vector3.zero;

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UISpawnableToggle), "method_2")]
        private static void PreventOffAxisSettingsTabText(UISpawnableToggle __instance)
        {
            __instance.transform.localEulerAngles = Vector3.zero;

        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(QuickSlotView), "SetItem")]
        private static void PreventOffAxisQuickSlotItemsViews(QuickSlotView __instance)
        {
            __instance.ItemView.transform.localEulerAngles = Vector3.zero;
            __instance.ItemView.MainImage.transform.localEulerAngles = new Vector3(0, 0, __instance.ItemView.MainImage.transform.localEulerAngles.z);
        }

        // This code is somehow responsible for determining which items in the stash/inv grid are shown and it shits the bed if
        // the CommonUI rotation isn't 0,0,0 so set it to that before running this code then set it back
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridViewMagnifier), "method_1")]
        private static bool StopGridFromHidingItemsWhenUiRotated(GridViewMagnifier __instance, bool calculate, bool forceMagnify)
        {
            if ((object)__instance.rectTransform_0 == null || (object)__instance._gridView == null || (object)__instance._scrollRect == null)
            {
                return false;
            }
            Vector3 originalRot = __instance.transform.root.eulerAngles;
            __instance.transform.root.eulerAngles = Vector3.zero;
            if (calculate)
            {

                Rect rect = __instance.rectTransform_0.rect;
                Vector3 vector = __instance.rectTransform_0.TransformPoint(rect.position);
                Vector3 vector2 = __instance.rectTransform_0.TransformPoint(rect.position + rect.size) - vector;
                rect = new Rect(vector, vector2);

                if (!forceMagnify && __instance.nullable_0 == rect)
                {
                    __instance.transform.root.eulerAngles = originalRot;
                    return false;
                }
                __instance.nullable_0 = rect;
            }
            if (__instance.nullable_0.HasValue)
            {
                __instance._gridView.MagnifyIfPossible(__instance.nullable_0.Value, forceMagnify);
            }
            __instance.transform.root.eulerAngles = originalRot;
            return false;
        }
        */
        //This sorta does the same thing as above but works better for how I'm now handling the inventory. This targets the method that directly handles the culling of items in stash.
        //For some reason, items cull when turning head 90+ degrees from front of playspace, but doesn't cull if you turn using the joystick
        private static Dictionary<GridView, (Vector3 rot, Vector3 pos, Quaternion quat)> _originalTransforms = new Dictionary<GridView, (Vector3, Vector3, Quaternion)>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "MagnifyIfPossible", new Type[] { typeof(Rect), typeof(bool) })]
        private static bool FixMagnifyWithRotationHandling(GridView __instance, ref Rect rect, bool force)
        {
            // Add null checks
            if (__instance == null || __instance.transform == null || __instance.transform.root == null)
                return true;

            Vector3 currentRotation = __instance.transform.root.eulerAngles;

            // Only apply fix if there's rotation
            if (currentRotation != Vector3.zero)
            {
                // Make sure dictionary is initialized
                if (_originalTransforms == null)
                    _originalTransforms = new Dictionary<GridView, (Vector3, Vector3, Quaternion)>();

                // Store original transform values
                _originalTransforms[__instance] = (
                    __instance.transform.root.eulerAngles,
                    __instance.transform.root.position,
                    __instance.transform.root.rotation
                );

                // Temporarily reset transform for calculation
                __instance.transform.root.eulerAngles = Vector3.zero;
                __instance.transform.root.position = Vector3.zero;
                __instance.transform.root.rotation = Quaternion.identity;

                // Use moderate rect for VR
                rect = new Rect(-1024f, -1024f, 2048f, 2048f);
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GridView), "MagnifyIfPossible", new Type[] { typeof(Rect), typeof(bool) })]
        private static async void RestoreTransformAfterMagnify(GridView __instance, Task __result)
        {
            // Check if we stored transforms for this instance
            if (_originalTransforms.TryGetValue(__instance, out var originalTransform))
            {
                // Wait for the magnify operation to complete
                if (__result != null)
                {
                    await __result;
                }

                // Restore original transform
                __instance.transform.root.eulerAngles = originalTransform.rot;
                __instance.transform.root.position = originalTransform.pos;
                __instance.transform.root.rotation = originalTransform.quat;

                // Clean up stored data
                _originalTransforms.Remove(__instance);
            }
        }
        
        private static readonly FieldInfo PossibleInteractionsChangedField = typeof(Player).GetField("PossibleInteractionsChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        private const float InteractionRayRadius = 0.03f;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "InteractionRaycast")]
        private static bool Raycaster(EFT.Player __instance)
        {
            if (__instance._playerLookRaycastTransform == null || !__instance.HealthController.IsAlive || !(VRGlobals.vrPlayer is RaidVRPlayerManager))
            {
                return false;
            }
            RaidVRPlayerManager manager = (RaidVRPlayerManager)VRGlobals.vrPlayer;
            InteractableObject interactableObject = null;
            __instance.InteractableObjectIsProxy = false;
            EFT.Player player = null;
            Ray interactionRay = __instance.InteractionRay;
            RaycastHit hit;
            if (__instance.CurrentState.CanInteract && (bool)__instance.HandsController && __instance.HandsController.CanInteract())
            {
                GameObject gameObject = null;
                if (VRGlobals.handsInteractionController && VRGlobals.handsInteractionController.useLeftHandForRaycast)
                {
                    Vector3 rayDirection = VRGlobals.handsInteractionController.laser.transform.forward;
                    Vector3 rayOrigin = VRGlobals.vrPlayer.LeftHand.transform.position;
                    if (Physics.SphereCast(rayOrigin, InteractionRayRadius, rayDirection, out hit, 0.66f, EFT.GameWorld.int_0))
                    {
                        gameObject = hit.collider.gameObject;
                        if (!__instance.InteractableObject || __instance.InteractableObject.gameObject != gameObject)
                            manager.PlaceUiInteracter(hit);
                    }
                }
                else
                {
                    Vector3 rayOrigin = Camera.main.transform.position;
                    // Raycasts hit a bit too high so tilt it down for it to hit closer to the centre of vision
                    Vector3 rayDirection = Quaternion.Euler(-5, 0, 0) * Camera.main.transform.forward;
                    float adjustedRayDistance = manager.rayDistance * manager.GetDistanceMultiplier(rayDirection);

                    if (Physics.SphereCast(rayOrigin, InteractionRayRadius, rayDirection, out hit, adjustedRayDistance, EFT.GameWorld.int_0))
                    {
                        gameObject = hit.collider.gameObject;
                        if (!__instance.InteractableObject || __instance.InteractableObject.gameObject != gameObject)
                            manager.PlaceUiInteracter(hit);
                    }
                }

                if (gameObject != null)
                {
                    InteractiveProxy interactiveProxy = null;
                    interactableObject = gameObject.GetComponentInParent<InteractableObject>();
                    if (interactableObject == null)
                    {
                        interactiveProxy = gameObject.GetComponent<InteractiveProxy>();
                        if (interactiveProxy != null)
                        {
                            __instance.InteractableObjectIsProxy = true;
                            interactableObject = interactiveProxy.Link;
                        }
                    }
                    player = ((interactableObject == null) ? gameObject.GetComponent<EFT.Player>() : null);
                }
                __instance.RayLength = hit.distance;
            }
            if (interactableObject is WorldInteractiveObject worldInteractiveObject)
            {
                if (worldInteractiveObject is BufferGateSwitcher bufferGateSwitcher)
                {
                    _ = bufferGateSwitcher.BufferGatesState;
                    if (interactableObject == __instance.InteractableObject)
                    {
                        __instance._nextCastHasForceEvent = true;
                    }
                }
                else
                {
                    EDoorState doorState = worldInteractiveObject.DoorState;
                    if (doorState != EDoorState.Interacting && worldInteractiveObject.Operatable)
                    {
                        if (interactableObject == __instance.InteractableObject && __instance._lastInteractionState != doorState)
                        {
                            __instance._nextCastHasForceEvent = true;
                        }
                    }
                    else
                    {
                        interactableObject = null;
                    }
                }
            }
            
            else if (interactableObject is LootItem lootItem)
            {
                if (lootItem.Item == null || VRGlobals.handsInteractionController.heldItem != null)
                {
                    interactableObject = null;
                }
                else if (lootItem.Item != null && lootItem.Item is Weapon { IsOneOff: not false } weapon && weapon.Repairable?.Durability == 0f)
                {
                    interactableObject = null;
                }
            }
            else if (interactableObject is StationaryWeapon stationaryWeapon)
            {
                if (stationaryWeapon.Locked)
                {
                    interactableObject = null;
                }
                else if (interactableObject == __instance.InteractableObject && __instance._lastInteractionState != stationaryWeapon.State)
                {
                    __instance._nextCastHasForceEvent = true;
                }
            }
            else if (interactableObject != null)
            {
                if (__instance._lastStateUpdateTime != interactableObject.StateUpdateTime)
                {
                    __instance._nextCastHasForceEvent = true;
                }
                __instance._lastStateUpdateTime = interactableObject.StateUpdateTime;
            }
            if (interactableObject != __instance.InteractableObject || __instance._nextCastHasForceEvent)
            {
                __instance._nextCastHasForceEvent = false;
                __instance.InteractableObject = interactableObject;
                if (__instance.InteractableObject is WorldInteractiveObject worldInteractiveObject2)
                {
                    __instance._lastInteractionState = worldInteractiveObject2.DoorState;
                }
                else if (__instance.InteractableObject is StationaryWeapon stationaryWeapon2)
                {
                    __instance._lastInteractionState = stationaryWeapon2.State;
                }
                var eventDelegate = (Action)PossibleInteractionsChangedField?.GetValue(__instance);
                eventDelegate?.Invoke();
            }
            if (player != __instance.InteractablePlayer || __instance._nextCastHasForceEvent)
            {
                __instance._nextCastHasForceEvent = false;
                __instance.InteractablePlayer = ((player != __instance) ? player : null);
                if (player == __instance)
                {
                    UnityEngine.Debug.LogWarning(__instance.Profile.Nickname + " wants to interact to himself");
                }
                var eventDelegate = (Action)PossibleInteractionsChangedField?.GetValue(__instance);
                eventDelegate?.Invoke();
            }
            if (player == null && interactableObject == null)
            {
                float radius = 0.1f * (1f + (float)__instance.Skills.PerceptionLootDot);
                float distance = 1.5f;
                if ((bool)__instance.Skills.PerceptionEliteNoIdea)
                {
                    distance = 2.35f;
                    radius = 1.1f;
                    interactionRay.origin = __instance.Transform.position + Vector3.up * 3f;
                    interactionRay.direction = Vector3.down;
                }
                __instance.Boolean_0 = GameWorld.InteractionSense(Camera.main.transform.position, Camera.main.transform.forward, radius, distance);
            }
            else
            {
                __instance.Boolean_0 = false;
            }
            return false;
        }
        


        //Disables checking item distance when looting - not sure why but 3.11 broke this and it thinks you're too far when you pick up loose loot        
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GetActionsClass.Class1750), "method_0")]
        private static bool DisableLootDistanceCheck(GetActionsClass.Class1750 __instance)
        {
            MagazineItemClass magazineItemClass = __instance.rootItem as MagazineItemClass;
            if (__instance.owner.IsYourPlayer)
            {
                if (magazineItemClass != null && __instance.possibleAction is GClass3411 && __instance.lootItemLastOwner != null && __instance.lootItemLastOwner.ProfileId != __instance.owner.ProfileId)
                    __instance.owner.InventoryController.StrictCheckMagazine(magazineItemClass, false, 0, false, true);
                __instance.owner.InventoryController.RunNetworkTransaction(__instance.possibleAction, new Callback(__instance.method_1));
                return false;
            }
            return true;
        }
        

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static bool FixItemPlacement(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || !VRGlobals.inGame)
                return true;

            __instance.MovementContext?.AnimatorStatesLateUpdate();
            __instance.DistanceDirty = true;
            __instance.OcclusionDirty = true;
            if (__instance.HealthController != null && __instance.HealthController.IsAlive)
            {
                __instance.Physical.LateUpdate();
                __instance.VisualPass();
                __instance._armsupdated = false;
                __instance._bodyupdated = false;
                if (__instance._nFixedFrames > 0)
                {
                    __instance._nFixedFrames = 0;
                    __instance._fixedTime = 0f;
                }
                if (__instance._beaconDummy != null)
                {
                    //if (Physics.Raycast(new Ray(Camera.main.transform.position + Camera.main.transform.forward / 2f, Camera.main.transform.forward), out var hitInfo, 1.5f, LayerMaskClass.HighPolyWithTerrainMask))
                    if (Physics.Raycast(new Ray(VRGlobals.VRCam.transform.position + VRGlobals.VRCam.transform.forward * 0.3f, VRGlobals.VRCam.transform.forward), out var hitInfo, 1.5f, LayerMaskClass.HighPolyWithTerrainMask))
                    {
                        __instance._beaconDummy.transform.position = hitInfo.point;
                        __instance._beaconDummy.transform.rotation = Quaternion.LookRotation(hitInfo.normal);
                        __instance._beaconMaterialSetter.SetAvailable(__instance._beaconPlacer.Available);
                        __instance.AllowToPlantBeacon = __instance._beaconPlacer.Available;
                        if (__instance.AllowToPlantBeacon)
                        {
                            __instance.BeaconPosition = __instance._beaconDummy.transform.position;
                            __instance.BeaconRotation = __instance._beaconDummy.transform.rotation;
                        }
                    }
                    else
                    {
                        //__instance._beaconDummy.transform.position = Camera.main.transform.position + Camera.main.transform.forward;
                        __instance._beaconDummy.transform.position = VRGlobals.VRCam.transform.position + VRGlobals.VRCam.transform.forward;
                        __instance._beaconDummy.transform.rotation = Quaternion.identity;
                        __instance._beaconMaterialSetter.SetAvailable(isAvailable: false);
                        __instance.AllowToPlantBeacon = false;
                    }
                }
                if (__instance.TripwireVisualPlacer_0 != null)
                {
                    __instance.TripwireVisualPlacer_0.ProcessPlacement(new Ray(VRGlobals.VRCam.transform.position + VRGlobals.VRCam.transform.forward * 0.3f, VRGlobals.VRCam.transform.forward), __instance.WeaponRoot.position);
                }
                __instance.ProceduralWeaponAnimation.StartFovCoroutine(__instance);
                __instance.PropUpdate();
            }
            __instance.ComplexLateUpdate(EUpdateQueue.Update, __instance.DeltaTime);
            if (__instance.POM != null && __instance.IsYourPlayer)
            {
                __instance.POM.ExtrudeCamera();
            }
            return false;
        }



        [HarmonyPostfix]
        [HarmonyPatch(typeof(BannerPageToggle), "Init")]
        private static void PositionLoadRaidBannerToggles(BannerPageToggle __instance)
        {
            __instance.transform.localScale = Vector3.one;
            Vector3 newPos = __instance.transform.localPosition;
            newPos.z = 0;
            __instance.transform.localPosition = newPos;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MatchMakerPlayerPreview), "Show")]
        private static void SetLoadRaidPlayerViewCamFoV(MatchMakerPlayerPreview __instance)
        {
            Transform camHolder = __instance._playerModelView.transform.Find("Camera_acceptScreen");
            if (camHolder)
                camHolder.GetComponent<Camera>().fieldOfView = 20;
        }

        public static void HideUiScreens()
        {
            if (VRGlobals.vrPlayer != null)
                VRGlobals.vrPlayer.ForceUnlockHand();
            if (VRGlobals.menuUi)
                VRGlobals.menuUi.GetChild(0).GetComponent<Canvas>().enabled = false;
            VRGlobals.commonUi.GetChild(0).GetComponent<Canvas>().enabled = false;
            VRGlobals.preloaderUi.GetChild(0).GetComponent<Canvas>().enabled = false;
        }
        public static void ShowUiScreens()
        {
            if (VRGlobals.vrPlayer != null)
                VRGlobals.vrPlayer.ForceUnlockHand();
            if (VRGlobals.menuUi)
                VRGlobals.menuUi.GetChild(0).GetComponent<Canvas>().enabled = true;
            VRGlobals.commonUi.GetChild(0).GetComponent<Canvas>().enabled = true;
            VRGlobals.preloaderUi.GetChild(0).GetComponent<Canvas>().enabled = true;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(AnimatedTextPanel), "Show")]
        private static void SetAmmoCountUi(AnimatedTextPanel __instance)
        {
            if (VRGlobals.vrPlayer)
            {
                VRGlobals.vrPlayer.showScopeZoom = true;
            }
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattleUIScreen<EftBattleUIScreen.GClass3865, EEftScreenType>), "ShowAmmoDetails")]
        private static void SetAmmoCountUi(BattleUIScreen<EftBattleUIScreen.GClass3865, EEftScreenType> __instance)
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
        private static bool showAgain = false;
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


        [HarmonyPostfix]
        [HarmonyPatch(typeof(EquipItemWindow), "Show")]
        private static void PositiionEquipItemWindow(EquipItemWindow __instance, Slot slot, InventoryController inventoryController, SkillManager skills, Vector3 position)
        {
            __instance.WindowTransform.localPosition = Vector3.zero;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Tooltip), "method_0")]
        private static bool PositionToolTips(SimpleTooltip __instance, Vector2 position)
        {
            if (MenuPatches.vrUiInteracter)
            {
                __instance._mainTransform.position = MenuPatches.vrUiInteracter.uiPointerPos;
                return false;
            }
            return true;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(OfferView), "method_10")]
        private static void ActivateTooltipHoverArea(OfferView __instance)
        {
            if (__instance.Offer_0.Locked)
            {
                __instance._hoverTooltipArea.gameObject.active = true;
                // The hover area is constantly regenerated which means we need to run another OnEnter function
                // but we need to set the last object to null so it knows its different
                if (MenuPatches.vrUiInteracter.lastHighlightedObject == __instance._hoverTooltipArea.gameObject)
                    MenuPatches.vrUiInteracter.lastHighlightedObject = null;
            }
        }



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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "AcceptItem")]
        private static bool FixAcceptItem(GridView __instance, ItemContextClass itemContext, ItemContextAbstractClass targetItemContext, ref Task __result)
        {
            // Modify the flag argument based on your logic
            bool flag = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftGrip.state : SteamVR_Actions._default.RightGrip.state;

            // Call the original method with the modified flag
            __result = AcceptItemModified(__instance, itemContext, targetItemContext, flag);

            // Skip the original method
            return false;
        }

        private static async Task AcceptItemModified(GridView __instance, ItemContextClass itemContext, ItemContextAbstractClass targetItemContext, bool flag)
        {
            // Your modified version of the AcceptItem method
            if (!__instance.CanAccept(itemContext, targetItemContext, out var operation) || !(await GClass3826.TryShowDestroyItemsDialog(operation.Value)))
            {
                return;
            }
            if (itemContext.Item is AmmoItemClass ammo)
            {
                Item item = __instance.method_8(targetItemContext);
                if (item != null)
                {
                    if (item is MagazineItemClass magazineItemClass)
                    {
                        MagazineItemClass magazineClass2 = magazineItemClass;
                        int loadCount = GridView.smethod_0(magazineClass2, ammo);
                        __instance._itemController.LoadMagazine(ammo, magazineClass2, loadCount).HandleExceptions();
                        return;
                    }
                    if (item is Weapon weapon)
                    {
                        Weapon weapon2 = weapon;
                        if (weapon2.SupportsInternalReload)
                        {
                            MagazineItemClass currentMagazine = weapon2.GetCurrentMagazine();
                            if (currentMagazine != null)
                            {
                                int num = GridView.smethod_0(currentMagazine, ammo);
                                if (num != 0)
                                {
                                    __instance._itemController.LoadWeaponWithAmmo(weapon2, ammo, num).HandleExceptions();
                                    return;
                                }
                            }
                        }
                        else
                        {
                            Weapon weapon3 = weapon;
                            if (weapon3.IsMultiBarrel)
                            {
                                int ammoCount = GridView.smethod_1(weapon3, ammo);
                                __instance._itemController.LoadMultiBarrelWeapon(weapon3, ammo, ammoCount).HandleExceptions();
                                return;
                            }
                        }
                    }
                }
            }
            if (!operation.Failed && __instance._itemController.CanExecute(operation.Value))
            {
                IRaiseEvents value = operation.Value;
                if (value == null)
                {
                    goto IL_0327;
                }
                if (!(value is GClass3424 gClass))
                {
                    if (!(value is GClass3425 gClass2))
                    {
                        goto IL_0327;
                    }
                    GClass3425 gClass3 = gClass2;
                    itemContext.DragCancelled();
                    if (gClass3.Count > 1 && flag)
                    {
                        __instance.itemUiContext_0.SplitDialog.Show(GClass2348.Localized("Transfer"), gClass3.Count, itemContext.CursorPosition, delegate (int count)
                        {
                            __instance.itemUiContext_0.SplitDialog.Hide();
                            __instance._itemController.TryRunNetworkTransaction(gClass3.ExecuteWithNewCount(count, simulate: true));
                        }, delegate
                        {
                            __instance.itemUiContext_0.SplitDialog.Hide();
                        });
                    }
                    else
                    {
                        __instance._itemController.RunNetworkTransaction(gClass3);
                    }
                }
                else
                {
                    GClass3424 gClass4 = gClass;
                    itemContext.DragCancelled();
                    if (gClass4.Count > 1 && flag)
                    {
                        __instance.itemUiContext_0.SplitDialog.Show(GClass2348.Localized("Split"), gClass4.Count, itemContext.CursorPosition, delegate (int count)
                        {
                            __instance.itemUiContext_0.SplitDialog.Hide();
                            gClass4.ExecuteWithNewCount(__instance._itemController, count);
                        }, delegate
                        {
                            __instance.itemUiContext_0.SplitDialog.Hide();
                        });
                    }
                    else
                    {
                        __instance._itemController.RunNetworkTransaction(gClass4);
                    }
                }
                goto IL_033e;
            }
            itemContext.DragCancelled();
            return;
        IL_0327:
            __instance._itemController.RunNetworkTransaction(operation.Value);
            goto IL_033e;
        IL_033e:
            ItemUiContext.PlayOperationSound(itemContext.Item, operation.Value);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "CanAccept")]
        private static bool FixCanAccept(GridView __instance, ItemContextClass itemContext, ItemContextAbstractClass targetItemContext, out GStruct153 operation, ref bool __result)
        {
            if (!__instance.SourceContext.DragAvailable)
            {
                operation = new GClass1550(itemContext.Item);
                return false;
            }
            operation = default(GStruct153);
            if (__instance.Grid == null)
            {
                return false;
            }
            if (__instance._nonInteractable)
            {
                return false;
            }
            Item item = itemContext.Item;
            LocationInGrid locationInGrid = __instance.CalculateItemLocation(itemContext);
            Item item2 = __instance.method_8(targetItemContext);
            GClass3393 gClass = __instance.Grid.CreateItemAddress(locationInGrid);
            ItemAddress itemAddress = itemContext.ItemAddress;
            if (itemAddress == null)
            {
                return false;
            }
            if (targetItemContext != null && !targetItemContext.ModificationAvailable)
            {
                operation = new StashGridClass.GClass1547(__instance.Grid);
                return false;
            }
            if (itemAddress.Container == __instance.Grid && __instance.Grid.GetItemLocation(item) == locationInGrid)
            {
                return false;
            }
            bool partialTransferOnly = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftGrip.state : SteamVR_Actions._default.RightGrip.state;
            if (item.CheckAction(gClass).Failed)
            {
                return false;
            }
            if (__instance.SourceContext.RotationLock.HasValue && itemContext.ItemRotation != __instance.SourceContext.RotationLock.Value)
            {
                return false;
            }
            operation = ((item2 != null) ? __instance._itemController.ExecutePossibleAction(itemContext, item2, partialTransferOnly, simulate: true) : __instance._itemController.ExecutePossibleAction(itemContext, gClass, partialTransferOnly, simulate: true));
            __result = operation.Succeeded;

            return false;
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SplitDialog), "Show", new Type[] { typeof(string), typeof(int), typeof(Vector2), typeof(Action<int>), typeof(Action), typeof(SplitDialog.ESplitDialogType) })]
        private static void RepositionSplitWindow(SplitDialog __instance)
        {

            __instance._window.localPosition = Vector3.zero;
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SplitDialog), "Show", new Type[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(Vector2), typeof(Action<int>), typeof(Action), typeof(SplitDialog.ESplitDialogType), typeof(bool), })]
        private static void RepositionConsumablesWindow(SplitDialog __instance)
        {

            __instance._window.localPosition = Vector3.zero;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlaceItemTrigger), "TriggerEnter")]
        private static void PlaceItemPositionUi(PlaceItemTrigger __instance)
        {
            if (VRGlobals.vrPlayer.interactionUi != null)
            {
                // Set position not local position so it doesn't inherit rotated position from camRoot
                VRGlobals.vrPlayer.interactionUi.position = Camera.main.transform.position +
                                                           Camera.main.transform.forward * 0.4f +
                                                           Camera.main.transform.up * -0.2f +
                                                           Camera.main.transform.right * 0;

                VRGlobals.vrPlayer.interactionUi.LookAt(Camera.main.transform);

                // Need to rotate 180 degrees otherwise it shows up backwards
                VRGlobals.vrPlayer.interactionUi.Rotate(0, 180, 0);
            }
        }

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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GesturesQuickPanel), "Show")]
        private static bool RemoveQuickTip(GesturesQuickPanel __instance)
        {
            __instance.enabled = false;
            return false;
        }

        // Fixes BTR Dialog menu
        
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TraderDialogScreen), "Show")]
        private static void BTROpenDialogUI(TraderDialogScreen __instance)
        {
            UIPatches.HandleOpenInventory();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TraderDialogScreen), "Close")]
        private static void BTRCloseDialogUI(TraderDialogScreen __instance)
        {
            UIPatches.HandleCloseInventory();
        }

        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(ScrollRect), "OnBeginDrag")]
        //private static bool PlaceItemPositwionUi(ScrollRect __instance, PointerEventData eventData)
        //{
        //    if (eventData.button == PointerEventData.InputButton.Left && __instance.IsActive())
        //    {
        //        __instance.UpdateBounds();

        //        // Convert world position to local UI position.
        //        RectTransformUtility.ScreenPointToLocalPointInRectangle(__instance.viewRect,
        //            Camera.main.WorldToScreenPoint(__instance.controllerWorldPos),
        //            Camera.main,
        //            out __instance.m_PointerStartLocalCursor);

        //        __instance.m_ContentStartPosition = __instance.m_Content.anchoredPosition;
        //        __instance.m_Dragging = true;
        //    }
        //}

    }

}
