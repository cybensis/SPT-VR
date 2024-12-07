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
using static EFT.Player;
using static InteractionsHandlerClass;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Settings;
using EFT.InventoryLogic;

namespace TarkovVR.Source.Player.Interactions
{
    internal class HandsInteractionController : MonoBehaviour
    {
        public Quaternion initialHandRot;
        private static int INTERACTIVE_LAYER = 22;
        private static int DEAD_BODY_LAYER = 23;
        private static float INTERACT_HAPTIC_AMOUNT = 0.4f;
        private static float INTERACT_HAPTIC_LENGTH = 0.25f;
        private SelectWeaponHandler selectWeaponHandler;
        public Transform scopeTransform;
        public Transform leftHand;
        public LootItem heldItem;
        public Vector3 heldItemOffset = new Vector3(-0.1f, -0.05f, 0);
        private bool changingScopeZoom = false;
        private bool isInRange = false;
        public GameObject laser;
        public GameObject grenadeLaser;
        public bool useLeftHandForRaycast = false;
        private float scopeTimeHeldFor = 0;


        private bool hasEnteredBackHolster = false;
        private bool hasEnteredHeadGear = false;
        private bool hasEnteredBackpack = false;
        private bool hasEnteredSidearmHolster = false;
        private bool hasEnteredScope = false;
        private bool hasEnteredRigCollider = false;
        private bool hasEnteredLootCollider = false;
        private void Awake()
        {
            IInputHandler baseHandler;
            VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.SelectFirstPrimaryWeapon, out baseHandler);
            if (baseHandler != null)
            {
                selectWeaponHandler = (SelectWeaponHandler)baseHandler;
            }
            if (laser == null)
                AddLeftHandLaser();
            if (grenadeLaser == null)
                AddRightHandLaser();
        }
        // left hand laser pos -0.4 -0.05 0 and rot 9.1637 266.6361 0 then add cube child with local scale 0.002 0.002 0.5 then go to mesh renderer->material and set the color to 0.1912 0.1924 0.1896 1
        private void AddLeftHandLaser()
        {
            GameObject laserHolder = new GameObject("LaserHolder");
            laserHolder.transform.parent = VRGlobals.vrPlayer.LeftHand.transform;
            laser = GameObject.CreatePrimitive(PrimitiveType.Cube);
            laser.transform.parent = laserHolder.transform;
            //-0.3037 0.0415 0.0112
            laserHolder.transform.localPosition = new Vector3(-0.4f, -0.05f, 0f);
            //351 273.6908 0
            laserHolder.transform.localRotation = Quaternion.Euler(9, 266, 0);
            laser.transform.localPosition = Vector3.zero;
            laser.transform.localRotation = Quaternion.identity;
            laser.transform.localScale = new Vector3(0.002f, 0.002f, 0.5f);
            laser.transform.GetComponent<Renderer>().material = new Material(Shader.Find("Unlit/Texture"));
            laser.transform.GetComponent<Renderer>().material.color = new Color(0.139f, 0.402f, 0.418f, 1);
            GameObject.Destroy(laser.GetComponent<BoxCollider>());
            laser.active = false;

        }


        private void AddRightHandLaser()
        {
            GameObject laserHolder = new GameObject("LaserHolder");
            laserHolder.transform.parent = VRGlobals.vrPlayer.RightHand.transform;
            grenadeLaser = GameObject.CreatePrimitive(PrimitiveType.Cube);
            grenadeLaser.transform.parent = laserHolder.transform;
            laserHolder.transform.localPosition = new Vector3(-0.37f, -0.02f, -0.04f);
            laserHolder.transform.localRotation = Quaternion.Euler(0, 270, 0);
            grenadeLaser.transform.localPosition = Vector3.zero;
            grenadeLaser.transform.localRotation = Quaternion.identity;
            grenadeLaser.transform.localScale = new Vector3(0.002f, 0.002f, 0.5f);
            // Set the material to the Standard shader with transparency
            grenadeLaser.transform.GetComponent<Renderer>().material = new Material(Shader.Find("Unlit/Texture"));
            grenadeLaser.transform.GetComponent<Renderer>().material.color = new Color(0.139f, 0.402f, 0.418f, 1);

            GameObject.Destroy(grenadeLaser.GetComponent<BoxCollider>());
            grenadeLaser.active = false;

            if (InitVRPatches.rightPointerFinger != null)
            {
                grenadeLaser.transform.parent = InitVRPatches.rightPointerFinger.transform;
                grenadeLaser.transform.localEulerAngles = new Vector3(351f, 273.6908f, 0);
                grenadeLaser.transform.localPosition = new Vector3(-0.3037f, 0.0415f, 0.0112f);
            }
        }


        public void Update()
        {
            bool inBackHolster = false;
            bool inHeadGear = false;
            bool inBackpack = false;
            bool inSidearmHolster = false;
            bool inScope = false;
            bool inRigCollider = false;
            bool inLootCollider = false;
            bool leftHandedMode = VRSettings.GetLeftHandedMode();

            float secondaryHandTriggerAxis = (leftHandedMode) ? SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) : SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any);
            SteamVR_Action_Boolean primaryHandGrip = (leftHandedMode) ? SteamVR_Actions._default.LeftGrip : SteamVR_Actions._default.RightGrip;
            SteamVR_Action_Boolean secondaryHandGrip = (leftHandedMode) ? SteamVR_Actions._default.RightGrip : SteamVR_Actions._default.LeftGrip;

            if (!VRGlobals.inGame || VRGlobals.vrPlayer.isSupporting || (VRGlobals.player && VRGlobals.player.IsSprintEnabled) || VRGlobals.menuOpen)
                return;

            if (secondaryHandTriggerAxis > 0.5f && !VRGlobals.vrPlayer.isSupporting)
            {
                useLeftHandForRaycast = true;
                laser.active = true;
            }
            else
            {
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
                    inBackHolster = true;
                    if (!hasEnteredBackHolster)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (leftHandedMode) ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand);
                        hasEnteredBackHolster = true;
                    }
                    if (primaryHandGrip.stateUp)
                    {
                        selectWeaponHandler.TriggerSwapOtherPrimary();
                    }
                    if (primaryHandGrip.state && VRGlobals.vrPlayer.radialMenu)
                    {
                        if (!VRGlobals.vrPlayer.radialMenu.active)
                        {
                            VRGlobals.vrPlayer.radialMenu.active = true;
                            if (VRSettings.GetLeftHandedMode())
                                VRGlobals.blockLeftJoystick = true;
                            else
                                VRGlobals.blockRightJoystick = true;
                        }
                    }
                }
                else if (collider.gameObject.name == "sidearmHolsterCollider")
                {
                    inSidearmHolster = true;
                    if (!hasEnteredSidearmHolster)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (leftHandedMode) ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand);
                        hasEnteredSidearmHolster = true;
                    }
                    if (primaryHandGrip.stateDown)
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
                else if (collider.gameObject.layer == 3 && collider.gameObject.name == "rigCollider")
                {
                    inRigCollider = true;
                    if (!hasEnteredRigCollider)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (leftHandedMode) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        hasEnteredRigCollider = true;
                    }
                    if (UIPatches.quickSlotUi && secondaryHandGrip.stateDown)
                    {
                        UIPatches.quickSlotUi.CreateQuickSlotUi();
                        UIPatches.quickSlotUi.gameObject.SetActive(true);
                    }
                }
                else if (!heldItem && collider.gameObject.layer == INTERACTIVE_LAYER && collider.gameObject.GetComponent<LootItem>())
                {
                    inLootCollider = true;
                    if (!hasEnteredLootCollider)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (leftHandedMode) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        hasEnteredLootCollider = true;
                    }
                    if (secondaryHandGrip.stateDown)
                    {
                        heldItem = collider.gameObject.GetComponent<LootItem>();
                    }
                }
                // Don't try to open something if there is an item to hold, always prioritize held items since you can move them out of the way
                else if (!heldItem && collider.gameObject.layer == INTERACTIVE_LAYER && collider.gameObject.GetComponent<LootableContainer>())
                {
                    inLootCollider = true;
                    if (!hasEnteredLootCollider)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (leftHandedMode) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        hasEnteredLootCollider = true;
                    }
                    if (secondaryHandGrip.stateDown)
                    {
                        LootableContainer container = collider.gameObject.GetComponent<LootableContainer>();
                        VRGlobals.player.vmethod_1(container, new InteractionResult(EInteractionType.Open));
                        float initialDistance = Vector3.Distance(VRGlobals.player.Transform.position, container.transform.position);
                        VRGlobals.player.SetCallbackForInteraction(delegate (Action callback)
                        {
                            GetActionsClass.smethod_21(VRGlobals.player.GetComponent<GamePlayerOwner>(), callback, container, initialDistance);
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
                    inLootCollider = true;
                    if (!hasEnteredLootCollider)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (leftHandedMode) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        hasEnteredLootCollider = true;
                    }
                    if (secondaryHandGrip.stateDown)
                    {
                        Door door = collider.gameObject.GetComponent<Door>();
                        GetActionsClass.Class1620 doorInteractionClass = new GetActionsClass.Class1620();
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
                    inLootCollider = true;
                    if (!hasEnteredLootCollider)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (leftHandedMode) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        hasEnteredLootCollider = true;
                    }
                    if (secondaryHandGrip.stateDown)
                    {
                        Corpse corpse = collider.transform.root.GetComponent<Corpse>();
                        GetActionsClass.Class1612 corpseInteractionClass = new GetActionsClass.Class1612();
                        corpseInteractionClass.compoundItem = (InventoryEquipment)corpse.Item;
                        corpseInteractionClass.rootItem = (InventoryEquipment)corpse.Item;
                        corpseInteractionClass.lootItemOwner = corpse.ItemOwner;
                        corpseInteractionClass.controller = VRGlobals.player.InventoryController;
                        corpseInteractionClass.owner = VRGlobals.player.GetComponent<GamePlayerOwner>();
                        corpseInteractionClass.method_3();
                        //VRGlobals.player.CurrentManagedState.ExecuteDoorInteraction(container, new InteractionResult(EFT.EInteractionType.Open), null, VRGlobals.player);
                        //container.Interact(new InteractionResult(EFT.EInteractionType.Open));
                    }
                }
                else if (heldItem && collider.gameObject.layer == 3 && collider.gameObject.name == "backpackCollider")
                {
                    inBackpack = true;
                    if (!hasEnteredBackpack)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (leftHandedMode) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        hasEnteredBackpack = true;
                    }
                    if (secondaryHandGrip.stateUp)
                    {
                        GStruct446<GInterface385> pickUpResult = InteractionsHandlerClass.QuickFindAppropriatePlace(heldItem.Item, VRGlobals.player.InventoryController, VRGlobals.player.InventoryController.Inventory.Equipment.ToEnumerable(), EMoveItemOrder.PickUp, simulate: true);
                        if (pickUpResult.Succeeded && heldItem.ItemOwner.CanExecute(pickUpResult.Value))
                        {
                            GetActionsClass.smethod_9(VRGlobals.player, pickUpResult.Value, heldItem.Item, heldItem.LastOwner);
                            heldItem = null;
                        }

                    }
                }
                else if (noScopeHit && collider.gameObject.layer == 3 && collider.gameObject.name == "headGearCollider")
                {
                    inHeadGear = true;
                    if (!hasEnteredHeadGear)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (leftHandedMode) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        hasEnteredHeadGear = true;
                    }
                    if (secondaryHandGrip.stateDown)
                    {
                        IInputHandler baseHandler;
                        VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ToggleGoggles, out baseHandler);
                        if (baseHandler != null)
                        {
                            HeadMountedDeviceHandler heeadMountDeviceHandler = baseHandler as HeadMountedDeviceHandler;
                            heeadMountDeviceHandler.TriggerHHeadMount();
                        }
                    }
                }
            }




            if (heldItem)
            {
                heldItem.transform.position = VRGlobals.vrPlayer.LeftHand.transform.position;
                heldItem.transform.localPosition += (VRGlobals.vrPlayer.LeftHand.transform.right * heldItemOffset.x) + (VRGlobals.vrPlayer.LeftHand.transform.up * heldItemOffset.y) + (VRGlobals.vrPlayer.LeftHand.transform.forward * heldItemOffset.z);
                heldItem.transform.rotation = VRGlobals.vrPlayer.LeftHand.transform.rotation;


                if (!secondaryHandGrip.state)
                {
                    WeaponPatches.DropObject(heldItem);
                    heldItem = null;
                }

            }
            if (changingScopeZoom)
                handleScopeInteraction();

            if (hasEnteredScope && noScopeHit)
                hasEnteredScope = false;
            if (hasEnteredBackHolster && !inBackHolster)
                hasEnteredBackHolster = false;
            if (hasEnteredSidearmHolster && !inSidearmHolster)
                hasEnteredSidearmHolster = false;
            if (hasEnteredRigCollider && !inRigCollider)
                hasEnteredRigCollider = false;
            if (hasEnteredBackpack && !inBackpack)
                hasEnteredBackpack = false;
            if (hasEnteredLootCollider && !inLootCollider)
                hasEnteredLootCollider = false;
            if (hasEnteredHeadGear && !inHeadGear)
                hasEnteredHeadGear = false;


            if (noScopeHit && isInRange && !secondaryHandGrip.state)
            {
                isInRange = false;
                scopeTransform = null;
                WeaponPatches.currentGunInteractController.RemoveScopeHighlight();
                hasEnteredScope = false;  // Reset scope entry flag
            }

        }
        private void handleScopeInteraction()
        {
            SteamVR_Action_Boolean secondaryHandGrip = (VRSettings.GetLeftHandedMode()) ? SteamVR_Actions._default.RightGrip : SteamVR_Actions._default.LeftGrip;

            if (!isInRange)
            {
                isInRange = true;
                if (!hasEnteredScope)
                {
                    SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (VRSettings.GetLeftHandedMode()) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                    hasEnteredScope = true;
                }
                if (WeaponPatches.currentGunInteractController != null && scopeTransform != null)
                {
                    WeaponPatches.currentGunInteractController.SetScopeHighlight(scopeTransform);
                }
            }

            if (secondaryHandGrip.state)
            {
                scopeTimeHeldFor += Time.deltaTime;
                if (scopeTimeHeldFor >= 0.3f)
                {
                    if (!changingScopeZoom)
                    {
                        VRGlobals.vrOpticController.initZoomDial();
                        changingScopeZoom = true;
                    }
                    VRGlobals.vrOpticController.handlePhysicalZoomDial();
                }
            }
            else if (secondaryHandGrip.stateUp && scopeTimeHeldFor < 0.3f)
            {
                VRGlobals.vrOpticController.changeScopeMode();
            }
            else
            {
                scopeTimeHeldFor = 0;
                if (changingScopeZoom)
                    changingScopeZoom = false;
                if (!hasEnteredScope)
                {
                    SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (VRSettings.GetLeftHandedMode()) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                    hasEnteredScope = true;
                }
            }
        }

    }
}
