using HarmonyLib;
using UnityEngine;
using EFT;
using EFT.UI;
using JetBrains.Annotations;
using System;
using TarkovVR.Source.Player.VRManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using System.Reflection;
using Comfort.Common;
using static TarkovVR.Patches.UI.UIPatchShared;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class InteractionUIPatches
    {
        private static readonly FieldInfo PossibleInteractionsChangedField = typeof(Player).GetField("PossibleInteractionsChanged", BindingFlags.Instance | BindingFlags.NonPublic);

        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ActionPanel), "Start")]
        private static void DisableUiPointer(ActionPanel __instance)
        {
            __instance._pointer.gameObject.SetActive(false);
            //VRGlobals.vrPlayer.interactionUi = __instance._interactionButtonsContainer;
            VRGlobals.vrPlayer.interactionUi = __instance.transform;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ActionPanel), "method_6")]
        private static void CopyInteractionUi(ActionPanel __instance)
        {
            __instance._pointer.gameObject.SetActive(false);
            //VRGlobals.vrPlayer.interactionUi = __instance._interactionButtonsContainer;
            VRGlobals.vrPlayer.interactionUi = __instance.transform;
        }


        //-----------------------------------------------------------------------------------------------------------------
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


        //-----------------------------------------------------------------------------------------------------------------
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
                    if (Physics.Raycast(rayOrigin, rayDirection, out hit, 0.66f, EFT.GameWorld.int_0))
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


                    if (Physics.Raycast(rayOrigin, rayDirection, out hit, adjustedRayDistance, EFT.GameWorld.int_0))
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
                if (VRGlobals.handsInteractionController.heldItem != null)
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


        //-----------------------------------------------------------------------------------------------------------------
        //Disables checking item distance when looting - not sure why but 3.11 broke this and it thinks you're too far when you pick up loose loot        
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GetActionsClass.Class1624), "method_0")]
        private static bool DisableLootDistanceCheck(GetActionsClass.Class1624 __instance)
        {
            MagazineItemClass magazineItemClass = __instance.rootItem as MagazineItemClass;
            if (__instance.owner.IsYourPlayer)
            {
                if (magazineItemClass != null && __instance.possibleAction is GClass3203 && __instance.lootItemLastOwner != null && __instance.lootItemLastOwner.ProfileId != __instance.owner.ProfileId)
                    __instance.owner.InventoryController.StrictCheckMagazine(magazineItemClass, false, 0, false, true);
                __instance.owner.InventoryController.RunNetworkTransaction(__instance.possibleAction, new Callback(__instance.method_1));
                return false;
            }
            return true;
        }


        //-----------------------------------------------------------------------------------------------------------------
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


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattleUIPanelExtraction), "Show", new Type[] { typeof(string), typeof(float) })]
        private static void PositionPlaceItemUI(BattleUIPanelExtraction __instance)
        {
            UIPatches.gameUi.transform.parent = VRGlobals.player.gameObject.transform;
            UIPatches.gameUi.transform.localScale = new Vector3(0.0008f, 0.0008f, 0.0008f);
            UIPatches.gameUi.transform.localPosition = new Vector3(0.02f, 1.7f, 0.48f);
            UIPatches.gameUi.transform.localEulerAngles = new Vector3(29.7315f, 0.4971f, 0f);
        }
    }
}