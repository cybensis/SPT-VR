using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Patches.UI;
using static TarkovVR.Source.Controls.InputHandlers;
using TarkovVR.Source.Controls;
using TarkovVR.Patches.Core.Player;
using EFT.Interactive;
using System;
using EFT;
using static GClass1859;
using static EFT.Player;
using static InteractionsHandlerClass;

namespace TarkovVR.Source.Player.Interactions
{
    internal class HandsInteractionController : MonoBehaviour
    {
        public Quaternion initialHandRot;
        private static int INTERACTIVE_LAYER = 22;
        private static int DEAD_BODY_LAYER = 23;
        private SelectWeaponHandler selectWeaponHandler;
        public Transform scopeTransform;
        public Transform leftHand;
        public LootItem heldItem;
        public Vector3 heldItemOffset = new Vector3(-0.1f, -0.05f, 0);
        private bool changingScopeZoom = false;
        private bool isInRange = false;
        public GameObject laser;
        public bool useLeftHandForRaycast = false;
        private void Awake() {
            IInputHandler baseHandler;
            VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.SelectFirstPrimaryWeapon, out baseHandler);
            if (baseHandler != null)
            {
                selectWeaponHandler = (SelectWeaponHandler)baseHandler;
            }
            AddLeftHandLaser();
        }
        // left hand laser pos -0.4 -0.05 0 and rot 9.1637 266.6361 0 then add cube child with local scale 0.002 0.002 0.5 then go to mesh renderer->material and set the color to 0.1912 0.1924 0.1896 1
        private void AddLeftHandLaser() {
            GameObject laserHolder = new GameObject("LaserHolder");
            laserHolder.transform.parent = VRGlobals.vrPlayer.LeftHand.transform;
            laser = GameObject.CreatePrimitive(PrimitiveType.Cube);
            laser.transform.parent = laserHolder.transform;
            laserHolder.transform.localPosition = new Vector3(-0.4f, -0.05f, 0f);
            laserHolder.transform.localRotation = Quaternion.Euler(9, 266, 0);
            laser.transform.localPosition = Vector3.zero;
            laser.transform.localRotation = Quaternion.identity;
            laser.transform.localScale = new Vector3(0.002f, 0.002f, 0.5f);
            laser.transform.GetComponent<Renderer>().material = new Material(Shader.Find("Unlit/Texture"));
            laser.transform.GetComponent<Renderer>().material.color = new Color(0.139f, 0.402f, 0.418f, 1);
            GameObject.Destroy(laser.GetComponent<BoxCollider>());
            laser.active = false;
        }

        public void Update()
        {

            if (!VRGlobals.inGame || VRGlobals.vrPlayer.isSupporting || (VRGlobals.player && VRGlobals.player.IsSprintEnabled) || VRGlobals.menuOpen)
                return;
            
            
            if (SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f && !VRGlobals.vrPlayer.isSupporting)
            {
                useLeftHandForRaycast = true;
                laser.active = true;
            }
            else {
                useLeftHandForRaycast = false;
                laser.active = false;
            }
            
            
            Collider[] nearbyColliders = Physics.OverlapSphere(VRGlobals.vrPlayer.RightHand.transform.position, 0.125f);
            foreach (Collider collider in nearbyColliders)
            {
                if (collider.gameObject.layer != 3)
                    continue;
                if (collider.gameObject.name == "backHolsterCollider")
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.RightHand);
                    if (SteamVR_Actions._default.RightGrip.stateUp)
                    {
                        selectWeaponHandler.TriggerSwapOtherPrimary();
                    }
                    if (SteamVR_Actions._default.RightGrip.state && VRGlobals.vrPlayer.radialMenu)
                    {
                        if (!VRGlobals.vrPlayer.radialMenu.active)
                        {
                            VRGlobals.vrPlayer.radialMenu.active = true;
                            VRGlobals.blockRightJoystick = true;
                        }
                    }
                }
                else if (collider.gameObject.name == "sidearmHolsterCollider")
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.RightHand);
                    if (SteamVR_Actions._default.RightGrip.stateDown)
                    {
                        selectWeaponHandler.TriggerSwapSidearm();
                    }
                }


            }

            nearbyColliders = Physics.OverlapSphere(VRGlobals.vrPlayer.LeftHand.transform.position, 0.125f);

            bool noScopeHit = true;
            foreach (Collider collider in nearbyColliders)
            {
                if (collider.gameObject.layer == 6)
                {
                    scopeTransform = collider.transform;
                    handleScopeInteraction();
                    noScopeHit = false;
                }
                else if (collider.gameObject.layer == 7 && collider.gameObject.name == "camHolder")
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);
                    if (SteamVR_Actions._default.LeftGrip.stateDown) {
                        IInputHandler baseHandler;
                        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ToggleGoggles, out baseHandler);
                        if (baseHandler != null)
                        {
                            HeadMountedDeviceHandler heeadMountDeviceHandler = baseHandler as HeadMountedDeviceHandler;
                            heeadMountDeviceHandler.TriggerHHeadMount();
                        }
                    }
                }
                else if (collider.gameObject.layer == 3 && collider.gameObject.name == "rigCollider")
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);
                    if (UIPatches.quickSlotUi && SteamVR_Actions._default.LeftGrip.stateDown)
                    {
                        UIPatches.quickSlotUi.active = true;
                    }

                }
                else if (!heldItem && collider.gameObject.layer == INTERACTIVE_LAYER && collider.gameObject.GetComponent<LootItem>())
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);
                    if (SteamVR_Actions._default.LeftGrip.stateDown)
                    {
                        heldItem = collider.gameObject.GetComponent<LootItem>();
                    }
                }
                // Don't try to open something if there is an item to hold, always prioritize held items since you can move them out of the way
                else if (!heldItem && collider.gameObject.layer == INTERACTIVE_LAYER && collider.gameObject.GetComponent<LootableContainer>())
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);
                    if (SteamVR_Actions._default.LeftGrip.stateDown)
                    {
                        LootableContainer container = collider.gameObject.GetComponent<LootableContainer>();
                        VRGlobals.player.vmethod_1(container, new InteractionResult(EInteractionType.Open));
                        float initialDistance = Vector3.Distance(VRGlobals.player.Transform.position, container.transform.position);
                        VRGlobals.player.SetCallbackForInteraction(delegate (Action callback)
                        {
                            GetActionsClass.smethod_18(VRGlobals.player.GetComponent<GamePlayerOwner>(), callback, container, initialDistance);
                        });
                        VRGlobals.player.StartBehaviourTimer(EFTHardSettings.Instance.DelayToOpenContainer, delegate
                        {
                            VRGlobals.player.TryInteractionCallback(container);
                        });
                        //VRGlobals.player.CurrentManagedState.ExecuteDoorInteraction(container, new InteractionResult(EFT.EInteractionType.Open), null, VRGlobals.player);
                        //container.Interact(new InteractionResult(EFT.EInteractionType.Open));
                    }
                }
                // Don't try to open something if there is an item to hold, always prioritize held items since you can move them out of the way
                else if (!heldItem && collider.gameObject.layer == INTERACTIVE_LAYER && collider.gameObject.GetComponent<Door>())
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);
                    if (SteamVR_Actions._default.LeftGrip.stateDown)
                    {
                        Door door = collider.gameObject.GetComponent<Door>();
                        GetActionsClass.Class1515 doorInteractionClass = new GetActionsClass.Class1515();
                        doorInteractionClass.door = door;
                        doorInteractionClass.owner = VRGlobals.player.GetComponent<GamePlayerOwner>();
                        if (door.DoorState == EDoorState.Open)
                            doorInteractionClass.method_5();
                        else if (door.DoorState == EDoorState.Locked)
                            doorInteractionClass.method_1();
                        else if (door.DoorState == EDoorState.Shut)
                            doorInteractionClass.method_0();
                        //VRGlobals.player.CurrentManagedState.ExecuteDoorInteraction(container, new InteractionResult(EFT.EInteractionType.Open), null, VRGlobals.player);
                        //container.Interact(new InteractionResult(EFT.EInteractionType.Open));
                    }
                }
                else if (!heldItem && collider.transform.root.gameObject.layer == DEAD_BODY_LAYER && collider.transform.root.GetComponent<Corpse>())
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);
                    if (SteamVR_Actions._default.LeftGrip.stateDown)
                    {
                        Corpse corpse = collider.transform.root.GetComponent<Corpse>();
                        GetActionsClass.Class1507 corpseInteractionClass = new GetActionsClass.Class1507();
                        corpseInteractionClass.compoundItem = (EquipmentClass) corpse.Item;
                        corpseInteractionClass.rootItem = (EquipmentClass) corpse.Item;
                        corpseInteractionClass.lootItemOwner = corpse.ItemOwner;
                        corpseInteractionClass.controller = VRGlobals.player.InventoryControllerClass;
                        corpseInteractionClass.owner = VRGlobals.player.GetComponent<GamePlayerOwner>();
                        corpseInteractionClass.method_3();
                        //VRGlobals.player.CurrentManagedState.ExecuteDoorInteraction(container, new InteractionResult(EFT.EInteractionType.Open), null, VRGlobals.player);
                        //container.Interact(new InteractionResult(EFT.EInteractionType.Open));
                    }
                }
                else if (heldItem && collider.gameObject.layer == 3 && collider.gameObject.name == "backpackCollider") {
                   SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);
                    if (SteamVR_Actions._default.LeftGrip.stateUp)
                    {
                        GStruct414<GInterface339> pickUpResult = InteractionsHandlerClass.QuickFindAppropriatePlace(heldItem.Item, VRGlobals.player.InventoryControllerClass, VRGlobals.player.InventoryControllerClass.Inventory.Equipment.ToEnumerable(), EMoveItemOrder.PickUp, simulate: true);
                        if (pickUpResult.Succeeded && heldItem.ItemOwner.CanExecute(pickUpResult.Value))
                        {
                            GetActionsClass.smethod_6(VRGlobals.player, pickUpResult.Value, heldItem.Item, heldItem.LastOwner);
                            heldItem = null;
                        }

                    }
                }
            }

            if (heldItem)
            {
                heldItem.transform.position = VRGlobals.vrPlayer.LeftHand.transform.position;
                heldItem.transform.localPosition += (VRGlobals.vrPlayer.LeftHand.transform.right * heldItemOffset.x) + (VRGlobals.vrPlayer.LeftHand.transform.up * heldItemOffset.y) + (VRGlobals.vrPlayer.LeftHand.transform.forward * heldItemOffset.z);
                heldItem.transform.rotation = VRGlobals.vrPlayer.LeftHand.transform.rotation;


                if (!SteamVR_Actions._default.LeftGrip.state) {
                    WeaponPatches.DropObject(heldItem);
                    heldItem = null;
                }

            }
            if (changingScopeZoom)
                handleScopeInteraction() ;
            if (noScopeHit && isInRange && !SteamVR_Actions._default.LeftGrip.state)
            {
                isInRange = false;
                scopeTransform = null;
                WeaponPatches.currentGunInteractController.RemoveScopeHighlight();
            }
        }



        private void handleScopeInteraction()
        {
            if (!isInRange) {
                isInRange = true;
                if (WeaponPatches.currentGunInteractController != null && scopeTransform != null)
                {
                    WeaponPatches.currentGunInteractController.SetScopeHighlight(scopeTransform);
                }
            }

            if (SteamVR_Actions._default.LeftGrip.stateDown)
            {
                VRGlobals.vrOpticController.initZoomDial();
                changingScopeZoom = true;
            }
            if (SteamVR_Actions._default.LeftGrip.state)
            {
                VRGlobals.vrOpticController.handlePhysicalZoomDial();
            }
            else
            {
                if (changingScopeZoom)
                    changingScopeZoom = false;
                SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.2f, SteamVR_Input_Sources.LeftHand);
            }
        }

    }
}
