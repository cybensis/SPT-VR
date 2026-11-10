using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.HealthSystem;
using EFT.InputSystem;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Builds;
using EFT.UI.DragAndDrop;
using EFT.UI.Gestures;
using EFT.UI.Insurance;
using EFT.UI.Map;
using EFT.UI.Matchmaker;
using EFT.UI.Ragfair;
using EFT.UI.Screens;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.Misc;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.UI;
using TarkovVR.Source.Controls;
using static EFT.InventoryLogic.Grid;
using static EFT.UI.EftBattleUIScreen;
using static EFT.UI.ItemsPanel;
using static EFT.UI.TransferItemsInRaidScreen;
namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class UIPatches
    {
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

        // With a weapon equipped this runs right after the loading screen, when everything is loaded: init the quick slot icons here.
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
        private static void FixInventoryAfterRaid(ItemsPanel __instance, ItemContext sourceContext, CompoundItem lootItem, IEftSession session, InventoryController inventoryController, IHealthController health, Profile profile, InsuranceCompany insurance, EquipmentBuildsStorage buildsStorage, EItemsTab currentTab, bool inRaid, bool isInventoryBlocked, [CanBeNull] InventoryEquipment equipment, Task __result)
        {
            __result.ContinueWith(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    if (lootItem is InventoryEquipment inventoryEquipment)
                    {
                        __instance._complexStashPanel.Show(__instance._inventoryController, sourceContext.CreateChild(inventoryEquipment), inventoryEquipment, __instance._profile.Skills, __instance._insurance, __instance._itemUiContext);
                        __instance.UI.AddDisposable(__instance._complexStashPanel);
                        __instance.UI.AddDisposable(__instance._complexStashPanel.UnConfigure);

                    }
                    else if (lootItem != null)
                    {
                        __instance._simpleStashPanel.Show(lootItem, __instance._inventoryController, sourceContext.CreateChild(lootItem), inRaid, __instance._inventoryController.Inventory.SortingTable, SimpleStashPanel.EStashSearchAvailability.StashOnly, null, __instance._currentTab);
                        __instance.UI.AddDisposable(__instance._simpleStashPanel);
                    }
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

        }

        public static void ResyncWeaponToIK(EFT.Player.FirearmController controller)
        {
            if (controller == null || controller.WeaponRoot == null || !controller._player.IsYourPlayer)
                return;

            if (VRGlobals.weaponHolder == null)
            {
                // fallback: try to find the holder
                Transform rightHandPos = controller.WeaponRoot.parent.Find("RightHandPositioner");
                if (rightHandPos != null)
                    VRGlobals.weaponHolder = rightHandPos.GetChild(0).gameObject;
                else
                    return; // no valid holder found
            }

            controller.WeaponRoot.SetParent(VRGlobals.weaponHolder.transform, false);

            // Default local offset/rotation
            controller.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);
            VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

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

            Transform head = VRGlobals.VRCam.transform;
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
                float poseLevel = VRGlobals.player.MovementContext._poseLevel;
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
        [HarmonyPatch(typeof(ActionPanel), "CG_Show")]
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
        [HarmonyPatch(typeof(ActionPanel), "AvailableInteractionStateChangedHandler")]
        private static void SetTransmitInteractionMenuActive(ActionPanel __instance, [CanBeNull] AvailableInteractionState interactionState)
        {
            try
            {
                if (VRGlobals.vrPlayer == null || !(VRGlobals.vrPlayer is RaidVRPlayerManager manager))
                {
                    return;
                }

                if (interactionState == null)
                {
                    manager.positionTransitUi = false;
                    return;
                }

                if (interactionState.SelectedAction == null || string.IsNullOrEmpty(interactionState.SelectedAction.Name))
                {
                    manager.positionTransitUi = false;
                    return;
                }

                if (interactionState.SelectedAction.Name.Contains("Transit") && VRGlobals.vrPlayer.interactionUi != null)
                {
                    if (Camera.main == null)
                    {
                        manager.positionTransitUi = false;
                        return;
                    }

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
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"Error in SetTransmitInteractionMenuActive: {exception.Message}");
            }
        }

        // The handler bound to GamePlayerOwner.AvailableInteractionState: fires whenever the available interaction actions change
        // (with null on look-away). Count the selectable (non-disabled) actions, which the right joystick cycles through
        // (ScrollNext/Prev), so the crouch/scroll handlers know whether the menu needs the stick.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ActionPanel), "AvailableInteractionStateChangedHandler")]
        private static void TrackInteractionActionCount([CanBeNull] AvailableInteractionState interactionState)
        {
            if (VRGlobals.vrPlayer == null)
                return;

            int count = 0;
            if (interactionState != null && interactionState.Actions != null)
            {
                for (int i = 0; i < interactionState.Actions.Count; i++)
                {
                    if (!interactionState.Actions[i].Disabled)
                        count++;
                }
            }
            VRGlobals.vrPlayer.interactMenuActionCount = count;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransferItemsInRaidScreen), "Show", new Type[] { typeof(TransferItemsInRaidScreenController) })]
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
        [HarmonyPatch(typeof(EftBattleUIScreenController), "ShowAmmoCountZeroingPanel")]
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


        // The root rotation must be 0,0,0 while the grid initializes, otherwise the grid items don't spawn in.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridViewMagnifier), "CG_InitGridView")]
        private static void ReturnCommonUiToZeroRot(GridViewMagnifier __instance)
        {
            __instance.transform.root.rotation = Quaternion.identity;
        }



        // In the hideout the stash panel is shown too, which repositions/rotates the UI: this patch is for raids only,
        // the hideout uses PositionInHideoutInventory()
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TabGroup), "Show")]
        private static void PositionInRaidInventory(TabGroup __instance)
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
        [HarmonyPatch(typeof(GridView), "AddItemView")]
        private static void PreventOffAxisGridItemsViews(GridView __instance, ItemView itemView)
        {
            itemView.transform.localEulerAngles = Vector3.zero;
            itemView.MainImage.transform.localEulerAngles = new Vector3(0, 0, itemView.MainImage.transform.localEulerAngles.z);
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SlotView), "CreateItemView")]
        private static void PreventOffAxisSlotItemsViews(SlotView __instance)
        {
            __instance.ItemView.transform.localEulerAngles = Vector3.zero;
            __instance.ItemView.MainImage.transform.localEulerAngles = new Vector3(0, 0, __instance.ItemView.MainImage.transform.localEulerAngles.z);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ModSlotView), "Show")]
        private static void PreventOffAxisModSlotItemsViews(ModSlotView __instance)
        {
            __instance.transform.localEulerAngles = Vector3.zero;

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UISpawnableToggle), "InitSpawnableButton")]
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
        // Same idea as above, on the method that culls the stash items directly. Items cull when the head turns 90+ degrees
        // from the front of the playspace, but not when turning with the joystick.
        private static Dictionary<GridView, (Vector3 rot, Vector3 pos, Quaternion quat)> _originalTransforms = new Dictionary<GridView, (Vector3, Vector3, Quaternion)>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "MagnifyIfPossible", new Type[] { typeof(Rect), typeof(bool) })]
        private static bool FixMagnifyWithRotationHandling(GridView __instance, ref Rect rect, bool force)
        {
            if (__instance == null || __instance.transform == null || __instance.transform.root == null)
                return true;

            Vector3 currentRotation = __instance.transform.root.eulerAngles;

            if (currentRotation != Vector3.zero)
            {
                if (_originalTransforms == null)
                    _originalTransforms = new Dictionary<GridView, (Vector3, Vector3, Quaternion)>();

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
            if (_originalTransforms.TryGetValue(__instance, out var originalTransform))
            {
                // Wait for the magnify operation to complete
                if (__result != null)
                {
                    await __result;
                }

                __instance.transform.root.eulerAngles = originalTransform.rot;
                __instance.transform.root.position = originalTransform.pos;
                __instance.transform.root.rotation = originalTransform.quat;

                _originalTransforms.Remove(__instance);
            }
        }
        
        private const float InteractionRayRadius = 0.03f;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "InteractionRaycast")]
        private static bool Raycaster(EFT.Player __instance)
        {
            // This prefix replaces InteractionRaycast with the VR raycast, which reads local rig state (VRGlobals.vrPlayer/VRCam):
            // own player only. ObservedPlayer overrides InteractionRaycast and only the local GamePlayerOwner calls it, but
            // gate it explicitly anyway.
            if (!__instance.IsYourPlayer)
                return true; // let vanilla interaction run for non-local players
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
                    if (Physics.SphereCast(rayOrigin, InteractionRayRadius, rayDirection, out hit, 0.66f, EFT.GameWorld.InteractiveLootMaskWPlayer))
                    {
                        gameObject = hit.collider.gameObject;
                        if (!__instance.InteractableObject || __instance.InteractableObject.gameObject != gameObject)
                            manager.PlaceUiInteracter(hit);
                    }
                }
                else
                {
                    Vector3 rayOrigin = VRGlobals.VRCam.transform.position;
                    // Raycasts hit a bit too high so tilt it down for it to hit closer to the centre of vision
                    Vector3 rayDirection = Quaternion.Euler(-5, 0, 0) * VRGlobals.VRCam.transform.forward;
                    float adjustedRayDistance = manager.rayDistance * manager.GetDistanceMultiplier(rayDirection);

                    if (Physics.SphereCast(rayOrigin, InteractionRayRadius, rayDirection, out hit, adjustedRayDistance, EFT.GameWorld.InteractiveLootMaskWPlayer))
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
                var hands = VRGlobals.handsInteractionController;
                bool isHeld = hands != null && lootItem != null && hands.heldItem == lootItem;
                if (lootItem.Item == null)
                {
                    interactableObject = null;
                }
                // A Corpse is a LootItem, but it follows its own toggle: its gaze menu is hidden only when "Disable Corpse
                // Context Menu" is on (the default). Loose loot uses suppressWorldLootMenu below.
                else if (lootItem is Corpse)
                {
                    if (!isHeld && VRSettings.GetDisableCorpseContextMenu())
                        interactableObject = null;
                }
                else if (!isHeld && (HandsInteractionController.suppressWorldLootMenu || (hands != null && hands.heldItem != null)))
                {
                    interactableObject = null;
                }
                else if (lootItem.Item is Weapon { IsOneOff: not false } weapon && weapon.Repairable?.Durability == 0f)
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
                // Player's own: raises PossibleInteractionsChanged, exactly what the original does here
                __instance.ForceInteractionsChanged();
            }
            if (player != __instance.InteractablePlayer || __instance._nextCastHasForceEvent)
            {
                __instance._nextCastHasForceEvent = false;
                __instance.InteractablePlayer = ((player != __instance) ? player : null);
                if (player == __instance)
                {
                    UnityEngine.Debug.LogWarning(__instance.Profile.Nickname + " wants to interact to himself");
                }
                // Player's own: raises PossibleInteractionsChanged, exactly what the original does here
                __instance.ForceInteractionsChanged();
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
                __instance.Sense = GameWorld.InteractionSense(VRGlobals.VRCam.transform.position, VRGlobals.VRCam.transform.forward, radius, distance);
            }
            else
            {
                __instance.Sense = false;
            }
            return false;
        }
        


        // Skips the item distance check when looting: the game thinks you're too far when you pick up loose loot        
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InteractionContextHelper.CG_Class1750), "method_0")]
        private static bool DisableLootDistanceCheck(InteractionContextHelper.CG_Class1750 __instance)
        {
            Magazine magazineItemClass = __instance.rootItem as Magazine;
            if (__instance.owner.IsYourPlayer)
            {
                if (magazineItemClass != null && __instance.possibleAction is MoveResult && __instance.lootItemLastOwner != null && __instance.lootItemLastOwner.ProfileId != __instance.owner.ProfileId)
                    __instance.owner.InventoryController.StrictCheckMagazine(magazineItemClass, false, 0, false, true);
                __instance.owner.InventoryController.RunNetworkTransaction(__instance.possibleAction, new Callback(__instance.method_1));
                return false;
            }
            return true;
        }
        

        // FIKA co-op: the gaze "Take" on a VR-held loot item must stow like the behind-the-back gesture (release -> reconcile gap ->
        // RunNetworkTransaction): a plain RunNetworkTransaction while the item is on the mod's held-sync path doesn't fire
        // RemoveLootItem/DestroyLoot on other clients. smethod_10 is the Take entry point before the pickup animation starts, so
        // the reroute skips the animation. Non-held takes fall through (DisableLootDistanceCheck handles Class1750.method_0).
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InteractionContextHelper), "smethod_10")]
        private static bool RerouteHeldItemTake(Item rootItem)
        {
            var hands = VRGlobals.handsInteractionController;
            if (hands != null && hands.TryStowHeldItem(rootItem))
                return false; // we own this Take now — skip the native animation + transaction
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
                    if (Physics.Raycast(new Ray(VRGlobals.VRCam.transform.position + VRGlobals.VRCam.transform.forward * 0.3f, VRGlobals.VRCam.transform.forward), out var hitInfo, 1.5f, LayersMaskController.HighPolyWithTerrainMask))
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
                if (__instance.TripwirePlanner != null)
                {
                    // Drive the live tripwire ghost off the dominant controller ray (same ray that seeds the first
                    // post in VRGrenadeController.VRTripwireFirstPoint), so the preview tracks where you point.
                    __instance.TripwirePlanner.ProcessPlacement(VRGrenadeController.GetTripwirePlacementRay(), __instance.WeaponRoot.position);
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


        // Suppress BSG's optic-magnification ("zoom times") panel in VR. AnimatedTextPanel is a shared type: match only the
        // captured optic crate panel by reference.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AnimatedTextPanel), "Show")]
        private static bool SuppressOpticZoomPanel(AnimatedTextPanel __instance)
        {
            return !(opticUi != null && __instance == opticUi);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AnimatedTextPanel), "Show")]
        private static void SetAmmoCountUi(AnimatedTextPanel __instance)
        {
            if (VRGlobals.vrPlayer)
            {
                VRGlobals.vrPlayer.showScopeZoom = false;
            }
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattleUIScreen<EftBattleUIScreenController, EEftScreenType>), "ShowAmmoDetails")]
        private static void SetAmmoCountUi(BattleUIScreen<EftBattleUIScreenController, EEftScreenType> __instance)
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
        [HarmonyPatch(typeof(Tooltip), "SetPosition")]
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
        [HarmonyPatch(typeof(OfferView), "AvailableOfferStatus")]
        private static void ActivateTooltipHoverArea(OfferView __instance)
        {
            if (__instance.Offer.Locked)
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
                Vector3 localPosition = child.localPosition;
                localPosition.z = 0;
                child.localPosition = localPosition;

                SetLocalZToZeroRecursively(child.gameObject);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "AcceptItem")]
        private static bool FixAcceptItem(GridView __instance, DragItemContext itemContext, ItemContext targetItemContext, ref Task __result)
        {
            bool flag = Handedness.GunHandGrip.state;

            __result = AcceptItemModified(__instance, itemContext, targetItemContext, flag);

            return false;
        }

        private static async Task AcceptItemModified(GridView __instance, DragItemContext itemContext, ItemContext targetItemContext, bool flag)
        {
            // Modified version of the AcceptItem method
            if (!__instance.CanAccept(itemContext, targetItemContext, out var operation) || !(await UIExtensions.TryShowDestroyItemsDialog(operation.Value)))
            {
                return;
            }
            if (itemContext.Item is Ammo ammo)
            {
                Item item = __instance.GetTargetItem(targetItemContext);
                if (item != null)
                {
                    if (item is Magazine magazineItemClass)
                    {
                        Magazine magazineClass2 = magazineItemClass;
                        int loadCount = GridView.CG_smethod_0(magazineClass2, ammo);
                        __instance._itemController.LoadMagazine(ammo, magazineClass2, loadCount).HandleExceptions();
                        return;
                    }
                    if (item is Weapon weapon)
                    {
                        Weapon weapon2 = weapon;
                        if (weapon2.SupportsInternalReload)
                        {
                            Magazine currentMagazine = weapon2.GetCurrentMagazine();
                            if (currentMagazine != null)
                            {
                                int num = GridView.CG_smethod_0(currentMagazine, ammo);
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
                                int ammoCount = GridView.CG_smethod_1(weapon3, ammo);
                                __instance._itemController.LoadMultiBarrelWeapon(weapon3, ammo, ammoCount).HandleExceptions();
                                return;
                            }
                        }
                    }
                }
            }
            if (!operation.Failed && __instance._itemController.CanExecute(operation.Value))
            {
                IOperationResult value = operation.Value;
                if (value == null)
                {
                    goto IL_0327;
                }
                if (!(value is SplitResult gClass))
                {
                    if (!(value is TransferResult gClass2))
                    {
                        goto IL_0327;
                    }
                    TransferResult gClass3 = gClass2;
                    itemContext.DragCancelled();
                    if (gClass3.Count > 1 && flag)
                    {
                        __instance._itemUiContext.SplitDialog.Show(LocalizationExtensions.Localized("Transfer"), gClass3.Count, itemContext.CursorPosition, delegate (int count)
                        {
                            __instance._itemUiContext.SplitDialog.Hide();
                            __instance._itemController.TryRunNetworkTransaction(gClass3.ExecuteWithNewCount(count, simulate: true));
                        }, delegate
                        {
                            __instance._itemUiContext.SplitDialog.Hide();
                        });
                    }
                    else
                    {
                        __instance._itemController.RunNetworkTransaction(gClass3);
                    }
                }
                else
                {
                    SplitResult gClass4 = gClass;
                    itemContext.DragCancelled();
                    if (gClass4.Count > 1 && flag)
                    {
                        __instance._itemUiContext.SplitDialog.Show(LocalizationExtensions.Localized("Split"), gClass4.Count, itemContext.CursorPosition, delegate (int count)
                        {
                            __instance._itemUiContext.SplitDialog.Hide();
                            gClass4.ExecuteWithNewCount(__instance._itemController, count);
                        }, delegate
                        {
                            __instance._itemUiContext.SplitDialog.Hide();
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
        private static bool FixCanAccept(GridView __instance, DragItemContext itemContext, ItemContext targetItemContext, out OperationResult operation, ref bool __result)
        {
            if (!__instance.SourceContext.DragAvailable)
            {
                operation = new NoPossibleActionsError(itemContext.Item);
                return false;
            }
            operation = default(OperationResult);
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
            Item item2 = __instance.GetTargetItem(targetItemContext);
            GridItemAddress gClass = __instance.Grid.CreateItemAddress(locationInGrid);
            ItemAddress itemAddress = itemContext.ItemAddress;
            if (itemAddress == null)
            {
                return false;
            }
            if (targetItemContext != null && !targetItemContext.ModificationAvailable)
            {
                operation = new ModificationUnavailable(__instance.Grid);
                return false;
            }
            if (itemAddress.Container == __instance.Grid && __instance.Grid.GetItemLocation(item) == locationInGrid)
            {
                return false;
            }
            bool partialTransferOnly = Handedness.GunHandGrip.state;
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
            if (VRGlobals.vrPlayer.interactionUi != null && VRGlobals.VRCam != null)
            {
                // Set position not local position so it doesn't inherit rotated position from camRoot
                VRGlobals.vrPlayer.interactionUi.position = VRGlobals.VRCam.transform.position +
                                                           VRGlobals.VRCam.transform.forward * 0.4f +
                                                           VRGlobals.VRCam.transform.up * -0.2f +
                                                           VRGlobals.VRCam.transform.right * 0;

                VRGlobals.vrPlayer.interactionUi.LookAt(VRGlobals.VRCam.transform);

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
                __instance.Tiles[0].LoadImage();
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

        // Block EFT's world interaction menu (door/container/corpse) while holding a loot item
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GamePlayerOwner), "InteractionsChangedHandler")]
        private static bool BlockInteractionMenuWhenHolding()
        {
            EFT.Player player = VRGlobals.player;

            // A LootItem under the gaze whose Item has gone null (removed/position-synced by another player, or a corpse mid
            // ragdoll-sync): EFT's GetActionsClass.smethod_8 throws every frame while building its action list. Skip the handler
            // and clear the stale interactable.
            if (player != null && player.InteractableObject is LootItem dead && dead.Item == null)
            {
                player.GetComponent<GamePlayerOwner>()?.ClearInteractionState();
                return false;
            }

            var hands = VRGlobals.handsInteractionController;
            if (hands == null || hands.heldItem == null)
                return true; // not holding — normal interaction menu behavior

            // Holding an item: allow EFT context menu only for the item you are holding, block it for everything else (doors, containers, corpses, etc)
            if (player != null && player.InteractableObject is LootItem looked && looked == hands.heldItem)
                return true;

            player?.GetComponent<GamePlayerOwner>()?.ClearInteractionState();
            return false;
        }
    }
}
