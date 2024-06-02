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
namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class UIPatches
    {

        public static BattleStancePanel stancePanel;
        public static CharacterHealthPanel healthPanel;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameUI), "Awake")]
        private static void SetGameUI(GameUI __instance)
        {
            //if (!inGame)
            //    return;


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
        private static void HandleCloseInventory(InventoryScreen __instance, ECommand command)
        {
            if (!VRGlobals.inGame)
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
        //private static Material uiTopMaterial;

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(InventoryScreen), "Awake")]
        //private static void HandleCloseIwnventory(InventoryScreen __instance)
        //{
        //    foreach (Transform child in __instance.transform) {
        //        if (child.name == "Left Glow") {
        //            uiTopMaterial = child.GetComponent<UnityEngine.UI.Image>().material;
        //            Plugin.MyLog.LogError("FOUND UI MATERIAL " + uiTopMaterial);
        //        }
        //    }
        //}


        private static void HandleOpenInventory()
        {
            menuOpen = true;
            VRGlobals.vrPlayer.enabled = false;
            VRGlobals.menuVRManager.enabled = true;
        }

        private static void HandleCloseInventory()
        {
            menuOpen = false;
            VRGlobals.vrPlayer.enabled = true;
            VRGlobals.menuVRManager.enabled = false;

            VRGlobals.menuVRManager.enabled = false;
            VRGlobals.commonUi.position = new Vector3(1000, 1000, 1000);
            VRGlobals.preloaderUi.position = new Vector3(1000, 1000, 1000);
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ActionPanel), "Start")]
        private static void DisableUiPointer(ActionPanel __instance)
        {
            __instance._pointer.gameObject.SetActive(false);
            VRGlobals.vrPlayer.interactionUi = __instance._interactionButtonsContainer;
        }


        // When the grid is being initialized we need to make sure the rotation is 0,0,0 otherwise the grid items don't
        // spawn in because of their weird code.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridViewMagnifier), "method_3")]
        private static void ReturnCommonUiToZeroRot(GridViewMagnifier __instance)
        {
            __instance.transform.root.rotation = Quaternion.identity;
        }

        // Position inventory in front of player
        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(ItemsPanel), "Show")]
        //private static void PositionInGamweInventory(ItemsPanel __instance)
        //{
        //    Plugin.MyLog.LogWarning("show " + Time.deltaTime);
        //}

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemsPanel), "Show")]
        private static void PositionInGamweInventory(ItemsPanel __instance)
        {
            Plugin.MyLog.LogWarning("Awake " + Time.deltaTime);
            if (!VRGlobals.inGame)
                return;
            if (VRGlobals.player && !menuOpen)
            {
                Plugin.MyLog.LogWarning("Awake " + Time.deltaTime);
                __instance.transform.root.rotation = Quaternion.identity;
                Transform commonUI = __instance.transform.root;
                commonUI.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
                Vector3 newUiPos = Camera.main.transform.position + (Camera.main.transform.forward * 0.7f) + (Camera.main.transform.right * -0.75f);
                newUiPos.y = Camera.main.transform.position.y + -0.6f;
                commonUI.position = newUiPos;
                commonUI.LookAt(Camera.main.transform);
                commonUI.Rotate(0, 225, 0);
                Vector3 newRot = commonUI.eulerAngles;
                newRot.x = 0;
                newRot.z = 0;
                commonUI.eulerAngles = newRot;
                HandleOpenInventory();
                if (VRGlobals.preloaderUi)
                {
                    VRGlobals.preloaderUi.localScale = new Vector3(0.0008f, 0.0008f, 0.0008f);

                    newUiPos = Camera.main.transform.position + (Camera.main.transform.forward * 0.7f);
                    newUiPos.y = Camera.main.transform.position.y + -0.2f;
                    VRGlobals.preloaderUi.position = newUiPos;
                    VRGlobals.preloaderUi.eulerAngles = newRot;

                }

                //if (uiTopMaterial) { 
                //    Plugin.MyLog.LogError("SETTTING UI MATERIAL " + uiTopMaterial);
                //    foreach (CanvasRenderer renderer in commonUI.GetComponentsInChildren<CanvasRenderer>()) {
                //        renderer.SetMaterial(uiTopMaterial,0);
                //    }
                //}
            }
        }


        private static bool menuOpen = false;
        // Position inventory in front of player
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GridViewMagnifier), "method_3")]
        private static void PositionInGameInventory(GridViewMagnifier __instance)
        {

            Plugin.MyLog.LogWarning("method_3 " + Time.deltaTime);
            if (!VRGlobals.inGame)
                return;
            if (VRGlobals.player && !menuOpen)
            {
                Plugin.MyLog.LogWarning("inside " + Time.deltaTime);
                __instance.transform.root.rotation = Quaternion.identity;
                Transform commonUI = __instance.transform.root;
                commonUI.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
                Vector3 newUiPos = Camera.main.transform.position + (Camera.main.transform.forward * 0.7f) + (Camera.main.transform.right * -0.75f);
                newUiPos.y = Camera.main.transform.position.y + -0.6f;
                commonUI.position = newUiPos;
                commonUI.LookAt(Camera.main.transform);
                commonUI.Rotate(0, 225, 0);
                Vector3 newRot = commonUI.eulerAngles;
                newRot.x = 0;
                newRot.z = 0;
                commonUI.eulerAngles = newRot;
                HandleOpenInventory();
                if (VRGlobals.preloaderUi)
                {
                    VRGlobals.preloaderUi.localScale = new Vector3(0.0008f, 0.0008f, 0.0008f);

                    newUiPos = Camera.main.transform.position + (Camera.main.transform.forward * 0.7f);
                    newUiPos.y = Camera.main.transform.position.y + -0.2f;
                    VRGlobals.preloaderUi.position = newUiPos;
                    VRGlobals.preloaderUi.eulerAngles = newRot;

                }

                //if (uiTopMaterial) { 
                //    Plugin.MyLog.LogError("SETTTING UI MATERIAL " + uiTopMaterial);
                //    foreach (CanvasRenderer renderer in commonUI.GetComponentsInChildren<CanvasRenderer>()) {
                //        renderer.SetMaterial(uiTopMaterial,0);
                //    }
                //}
            }
        }

        // Method_1 starts to despawn the grid items if its rotated
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(GridViewMagnifier), "Update")]
        //private static bool StopGridFromHidingItemsThroughUpdate(GridViewMagnifier __instance)
        //{
        //    if (__instance.enabled)
        //    {
        //        //__instance.method_1(calculate: true, forceMagnify: false);
        //        __instance.method_0();
        //    }
        //    return false;
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
        [HarmonyPatch(typeof(SlotView), "method_5")]
        private static void PreventOffAxisSlotItemsViews(SlotView __instance)
        {
            __instance.itemView_0.transform.localEulerAngles = Vector3.zero;
            __instance.itemView_0.MainImage.transform.localEulerAngles = new Vector3(0, 0, __instance.itemView_0.MainImage.transform.localEulerAngles.z);
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(QuickSlotView), "SetItem")]
        private static void PreventOffAxisSlowtItwemsViews(QuickSlotView __instance)
        {
            __instance.ItemView.transform.localEulerAngles = Vector3.zero;
            __instance.ItemView.MainImage.transform.localEulerAngles = new Vector3(0, 0, __instance.ItemView.MainImage.transform.localEulerAngles.z);
        }

        // This code is somehow responsible for determining which items in the stash/inv grid are shown and it shits the bed if
        // the CommonUI rotation isn't 0,0,0 so set it to that before running this code
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
            if (__instance.CurrentState.CanInteract && (bool)__instance.HandsController && __instance.HandsController.CanInteract())
            {
                RaycastHit hit;

                Vector3 rayOrigin = Camera.main.transform.position;
                Vector3 rayDirection = Camera.main.transform.forward;
                rayDirection.y -= manager.downwardOffset;
                float adjustedRayDistance = manager.rayDistance * manager.GetDistanceMultiplier(rayDirection);

                GameObject gameObject = null;

                if (Physics.Raycast(rayOrigin, rayDirection, out hit, adjustedRayDistance, EFT.GameWorld.int_0))
                {
                    gameObject = hit.collider.gameObject;
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
                    // Move the cube slightly closer to the player
                    //Vector3 offsetDirection = (rayOrigin - hit.point).normalized;
                    //hitPoint = hit.point + offsetDirection * RaidVRPlayerManager.dirMultiplier; // Adjust the offset distance as needed
                                                                                          //if (interactableObject != __instance.InteractableObject || __instance._nextCastHasForceEvent) { 
                                                                                          //    cameraManager.PlaceInteractorAfterDelay(gameObject);
                                                                                          //}
                                                                                          //cameraManager.interactionUi.transform.position = interactorPosition;
                                                                                          //cameraManager.interactionUi.transform.LookAt(rayOrigin); // Make the interactor face the pl

                    //if (interactableObject != null && interactiveProxy == null)
                    //{
                    //    if (interactableObject.InteractsFromAppropriateDirection(__instance.LookDirection))
                    //    {
                    //        if (!(hit.distance > EFTHardSettings.Instance.LOOT_RAYCAST_DISTANCE + EFTHardSettings.Instance.BEHIND_CAST) && interactableObject.isActiveAndEnabled)
                    //        {
                    //            if (hit.distance > EFTHardSettings.Instance.DOOR_RAYCAST_DISTANCE + EFTHardSettings.Instance.BEHIND_CAST && interactableObject is Door)
                    //            {
                    //                interactableObject = null;
                    //            }
                    //        }
                    //        else
                    //        {
                    //            interactableObject = null;
                    //        }
                    //    }
                    //    else
                    //    {
                    //        interactableObject = null;
                    //    }
                    //}
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
                Plugin.MyLog.LogWarning("place interacter");
                manager.PlaceUiInteracter();
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

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(ActionPanel), "method_0")]
        //private static void PositionInteractableUi(ActionPanel __instance, GClass2805 interactionState)
        //{
        //    Plugin.MyLog.LogWarning("Method_0    " + interactionState);
        //    if (interactionState == null) {
        //        cameraManager.interactionUi = null;
        //    }
        //    else
        //    {
        //        if (cameraManager.interactionUi == null) { 
        //            cameraManager.interactionUi = __instance._interactionButtonsContainer;
        //            cameraManager.interactionUi.position = hitPoint;
        //            cameraManager.interactionUi.LookAt(Camera.main.transform);
        //            // Need to rotate 180 degrees otherwise it shows up backwards
        //            cameraManager.interactionUi.Rotate(0, 180, 0);
        //        }
        //        else { 
        //            cameraManager.interactionUi = __instance._interactionButtonsContainer;
        //        }
        //        cameraManager.interactionUi.localScale = new Vector3(0.5f, 0.5f, 0.5f);
        //    }
        //}
    }
}