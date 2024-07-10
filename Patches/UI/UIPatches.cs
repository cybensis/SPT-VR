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
namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class UIPatches
    {
        private static int playerLayer = 8;
        public static GameObject quickSlotUi;
        public static BattleStancePanel stancePanel;
        public static CharacterHealthPanel healthPanel;
        public static GameUI gameUi;
        public static OpticCratePanel opticUi;
        public static NotifierView notifierUi;
        public static ExtractionTimersPanel extractionTimerUi;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameUI), "Awake")]
        private static void SetGameUI(GameUI __instance)
        {
            gameUi = __instance;
            if (!VRGlobals.camRoot)
                return;

            PositionGameUi(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NotifierView), "Awake")]
        private static void SetNotificationsUi(NotifierView __instance)
        {
            notifierUi = __instance;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NotificationView), "Init")]
        private static void DisableComponentThatBlocksText(NotificationView __instance)
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

        public static void PositionGameUi(GameUI __instance) {
            __instance.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            __instance.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
            stancePanel = __instance.BattleUiScreen._battleStancePanel;
            stancePanel.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
            stancePanel._battleStances[0].StanceObject.transform.parent.gameObject.active = false;

            healthPanel = __instance.BattleUiScreen._characterHealthPanel;
            healthPanel.transform.localScale = new Vector3(0.20f, 0.20f, 0.20f);
            
            __instance.transform.parent = VRGlobals.camRoot.transform;
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
                if (!__instance.Boolean_0)
                {
                    // If the menu is closed get rid of it, there would be better ways to do this but oh well 
                    HandleCloseInventory();
                }
            }
            if (command.IsCommand(ECommand.ToggleInventory))
            {
                HandleCloseInventory();
            }
        }

        
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemsPanel), "Show")]
        private static void FixInventoryAfterRaid(ItemsPanel __instance, LootItemClass lootItem, InventoryControllerClass inventoryController, IHealthController health, Profile profile, InsuranceCompanyClass insurance, GClass2969 buildsStorage, EItemsTab currentTab, EItemViewType viewType, bool inRaid, Task __result)
        {
            __result.ContinueWith(task =>
            {
                if (task.IsCanceled || task.IsFaulted)
                {
                    if (lootItem is EquipmentClass item)
                    {
                        __instance._complexStashPanel.Configure(__instance.gclass2572_0, new GClass2625<EquipmentClass>(item, viewType), __instance.profile_0.Skills, __instance.gclass2966_0, __instance.itemUiContext_0);
                        __instance.ginterface314_0 = __instance._complexStashPanel;
                        __instance.ginterface314_0?.Show(__instance.gclass2572_0, __instance.eitemsTab_0);
                    }
                    else if (lootItem != null)
                    {
                        __instance._simpleStashPanel.Configure(lootItem, __instance.gclass2572_0, new GClass2627(lootItem, viewType));
                        __instance.ginterface314_0 = __instance._simpleStashPanel;
                        __instance.ginterface314_0?.Show(__instance.gclass2572_0, __instance.eitemsTab_0);
                    }
                    else
                    {
                        __instance.ginterface314_0?.Close();
                        __instance.ginterface314_0 = null;
                    }
                    if (__instance.ginterface314_0 != null)
                    {
                        __instance.UI.AddDisposable(__instance.ginterface314_0);
                    }
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

        }
        public static void HandleOpenInventory()
        {
            ShowUiScreens();
            int bitmask = 1 << playerLayer; // 256
            Camera.main.cullingMask &= ~bitmask; // -524321 & -257

            VRGlobals.menuOpen = true;
            VRGlobals.blockRightJoystick = true;
            VRGlobals.vrPlayer.enabled = false;
            VRGlobals.menuVRManager.enabled = true;
            VRGlobals.commonUi.parent = VRGlobals.camRoot.transform;
            VRGlobals.commonUi.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
            VRGlobals.commonUi.localPosition = new Vector3(-0.8f, -0.5f - VRGlobals.vrPlayer.crouchHeightDiff, 0.8f);
            VRGlobals.commonUi.localEulerAngles = Vector3.zero;
            if (VRGlobals.preloaderUi) {

                VRGlobals.preloaderUi.transform.parent = VRGlobals.camRoot.transform;
                VRGlobals.preloaderUi.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
                VRGlobals.preloaderUi.GetChild(0).localScale = new Vector3(1.3333f, 1.3333f, 1.3333f);

                VRGlobals.preloaderUi.localPosition = new Vector3(-0.03f, -0.1f - VRGlobals.vrPlayer.crouchHeightDiff, 0.8f);
                
                VRGlobals.preloaderUi.localRotation = Quaternion.identity;

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
            int bitmask = 1 << playerLayer; // 256
            Camera.main.cullingMask |= bitmask; // -524321 & -257
            VRGlobals.menuOpen = false;
            VRGlobals.blockRightJoystick = false;
            VRGlobals.vrPlayer.enabled = true;
            VRGlobals.menuVRManager.enabled = false;
            VRGlobals.commonUi.parent = null;
            VRGlobals.commonUi.position = new Vector3(1000, 1000, 1000);
            VRGlobals.preloaderUi.parent = null;
            VRGlobals.preloaderUi.position = new Vector3(1000, 1000, 1000);
            VRGlobals.vrPlayer.SetNotificationUi();

        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ActionPanel), "Start")]
        private static void DisableUiPointer(ActionPanel __instance)
        {
            __instance._pointer.gameObject.SetActive(false);
            VRGlobals.vrPlayer.interactionUi = __instance._interactionButtonsContainer;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryScreenQuickAccessPanel), "Show", new Type[] { typeof(InventoryControllerClass), typeof(ItemUiContext), typeof(GamePlayerOwner), typeof(InsuranceCompanyClass) })]
        private static void YoinkQuickSlotImages(InventoryScreenQuickAccessPanel __instance)
        {
            if (!VRGlobals.inGame)
                return;

            List<Sprite> mainImagesList = new List<Sprite>();
            foreach (KeyValuePair<EBoundItem, BoundItemView> boundItem in __instance._boundItems)
            {
                if (boundItem.Value.ItemView)
                {
                    mainImagesList.Add(boundItem.Value.ItemView.MainImage.sprite);
                }
            }
            if (!quickSlotUi)
            {
                quickSlotUi = new GameObject("quickSlotUi");
                quickSlotUi.layer = 5;
                quickSlotUi.transform.parent = VRGlobals.vrPlayer.LeftHand.transform;
                CircularSegmentUI circularSegmentUI = quickSlotUi.AddComponent<CircularSegmentUI>();
                circularSegmentUI.Init();
                circularSegmentUI.CreateQuickSlotUi(mainImagesList.ToArray());
            }
            else
            {
                CircularSegmentUI circularSegmentUI = quickSlotUi.GetComponent<CircularSegmentUI>();
                circularSegmentUI.CreateQuickSlotUi(mainImagesList.ToArray());
            }
            quickSlotUi.active = false;

        }


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
        [HarmonyPatch(typeof(ItemsPanel), "Show")]
        private static void PositionInRaidInventory(ItemsPanel __instance)
        {
            // Dont open inv if not in game, player is in hideout, game player isn't set and the menu isn't already open
            if (!VRGlobals.inGame || VRGlobals.vrPlayer is HideoutVRPlayerManager || !VRGlobals.player || VRGlobals.menuOpen)
                return;

            HandleOpenInventory();

        }




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
        [HarmonyPatch(typeof(SlotView), "method_5")]
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

                Vector3 rayOrigin = Camera.main.transform.position;
                // Raycasts hit a bit too high so tilt it down for it to hit closer to the centre of vision
                Vector3 rayDirection = Quaternion.Euler(-5, 0, 0) * Camera.main.transform.forward;
                float adjustedRayDistance = manager.rayDistance * manager.GetDistanceMultiplier(rayDirection);

                GameObject gameObject = null;

                if (Physics.Raycast(rayOrigin, rayDirection, out hit, adjustedRayDistance, EFT.GameWorld.int_0))
                {
                    gameObject = hit.collider.gameObject;
                    if (!__instance.InteractableObject || __instance.InteractableObject.gameObject != gameObject)
                        manager.PlaceUiInteracter(hit);
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
                if (lootItem.Item is Weapon { IsOneOff: not false } weapon && weapon.Repairable.Durability == 0f)
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
                var eventInfo = typeof(Player).GetEvent("PossibleInteractionsChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                var field = typeof(Player).GetField("PossibleInteractionsChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                var eventDelegate = (Action)field.GetValue(__instance);
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
                var eventInfo = typeof(Player).GetEvent("PossibleInteractionsChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                var field = typeof(Player).GetField("PossibleInteractionsChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                var eventDelegate = (Action)field.GetValue(__instance);
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



        [HarmonyPostfix]
        [HarmonyPatch(typeof(BannerPageToggle), "Init")]
        private static void PositionLoadRaidBannerToggles(BannerPageToggle __instance) {
            __instance.transform.localScale = Vector3.one;
            Vector3 newPos = __instance.transform.localPosition;
            newPos.z = 0;
            __instance.transform.localPosition = newPos;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MatchMakerPlayerPreview), "Show")]
        private static void SetLoadRaidPlayerViewCamFoV(MatchMakerPlayerPreview __instance)
        {
            Transform camHolder = __instance._playerModelView.transform.FindChild("Camera_acceptScreen");
            if (camHolder)
                camHolder.GetComponent<Camera>().fieldOfView = 20;
        }

        public static void HideUiScreens() {
            if (VRGlobals.menuUi)
                VRGlobals.menuUi.GetChild(0).GetComponent<Canvas>().enabled = false;
            VRGlobals.commonUi.GetChild(0).GetComponent<Canvas>().enabled = false;
            VRGlobals.preloaderUi.GetChild(0).GetComponent<Canvas>().enabled = false;
        }
        public static void ShowUiScreens()
        {
            if (VRGlobals.menuUi)
                VRGlobals.menuUi.GetChild(0).GetComponent<Canvas>().enabled = true;
            VRGlobals.commonUi.GetChild(0).GetComponent<Canvas>().enabled = true;
            VRGlobals.preloaderUi.GetChild(0).GetComponent<Canvas>().enabled = true;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(OpticCratePanel), "Show")]
        private static void SetAmmoCountUi(OpticCratePanel __instance)
        {
            if (VRGlobals.vrPlayer) {
                VRGlobals.vrPlayer.showScopeZoom = true;
            }
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameUI), "Awake")]
        private static void SetOpticUi(GameUI __instance)
        {
            if (__instance.BattleUiScreen) { 
                opticUi = __instance.BattleUiScreen._opticCratePanel;
                opticUi.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattleUIScreen), "ShowAmmoDetails")]
        private static void SetAmmoCountUi(BattleUIScreen __instance)
        {
            __instance._ammoCountPanel.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
            if (VRGlobals.vrPlayer) { 
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
            __instance.transform.localScale = new Vector3(0.25f, 0.25f, 0.25f);
            if (VRGlobals.vrPlayer) { 
                VRGlobals.vrPlayer.SetAmmoFireModeUi(__instance.transform, false);
                showAgain = true;
            }
        }
        // On BattleUIComponentAnimation.Hide() with name == AmmoPanel stop updating position
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattleUIComponentAnimation), "Hide")]
        private static bool HideFireModeUi(BattleUIComponentAnimation __instance, ref float delaySeconds)
        {
            showAgain = false ;
            if (__instance.name == "AmmoPanel" && VRGlobals.vrPlayer) {
                delaySeconds = 5f;
                __instance.WaitSeconds(delaySeconds + 2, () => { if (!showAgain) VRGlobals.vrPlayer.SetAmmoFireModeUi(null, false); });
            }
            else if (__instance.name == "OpticCratePanel" && VRGlobals.vrPlayer) { 
                __instance.WaitSeconds(delaySeconds + 2, () => { if (!showAgain) VRGlobals.vrPlayer.showScopeZoom = false; });
            }
            return true;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(EquipItemWindow), "Show")]
        private static void PositiionEquipItemWindow(EquipItemWindow __instance, Slot slot, InventoryControllerClass inventoryController, SkillManager skills, Vector3 position)
        {
            __instance.WindowTransform.localPosition = Vector3.zero;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Tooltip), "method_0")]
        private static bool PositionToolTips(SimpleTooltip __instance, Vector2 position)
        {
            if (MenuPatches.vrUiInteracter) {
                __instance._mainTransform.position = MenuPatches.vrUiInteracter.uiPointerPos;
                return false;
            }
            return true;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(OfferView), "method_10")]
        private static void ActivateTooltipHoverArea(OfferView __instance)
        {
            if (__instance.Offer_0.Locked) { 
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
    }
}