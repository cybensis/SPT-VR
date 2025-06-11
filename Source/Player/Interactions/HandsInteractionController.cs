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
using static EFT.Interactive.WorldInteractiveObject;
using System.Collections.Generic;
using LiteNetLib.Utils;
using Fika.Core.Coop.Utils;
using Fika.Core.Networking;
using Fika.Core.Networking;
using Comfort.Common;
using LiteNetLib;
using static TarkovVR.Patches.UI.UIPatchShared;
using TarkovVR.Patches.Core.Equippables;

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

        private GamePlayerOwner _playerOwner;
        private GamePlayerOwner PlayerOwner => _playerOwner ??= VRGlobals.player.GetComponent<GamePlayerOwner>();

        private bool hasEnteredBackHolster = false;
        private bool hasEnteredHeadGear = false;
        private bool hasEnteredBackpack = false;
        private bool hasEnteredSidearmHolster = false;
        private bool hasEnteredScope = false;
        private bool hasEnteredRigCollider = false;
        private bool hasEnteredLootCollider = false;

        private bool cachedLeftHandedMode;
        private float leftHandedModeCheckTimer;
        private float lastPhysicsCheckTime;
        private const float PHYSICS_CHECK_INTERVAL = 0.05f;

        private SteamVR_Action_Boolean primaryHandGrip;
        private SteamVR_Action_Boolean secondaryHandGrip;
        private SteamVR_Input_Sources primaryInputSource;
        private SteamVR_Input_Sources secondaryInputSource;

        private bool wasInBackHolster, wasInSidearmHolster, wasInRigCollider;
        private bool wasInBackpack, wasinInteractiveCollider, wasInHeadGear, wasInScope;

        private InteractionState rightHandState = new InteractionState();
        private InteractionState leftHandState = new InteractionState();

        private Dictionary<Collider, CachedInteractable> interactableCache = new Dictionary<Collider, CachedInteractable>();

        private float lastCacheCleanupTime = 0f;
        private const float CACHE_CLEANUP_INTERVAL = 30f;

        private Rigidbody cachedRigidbody;
        private Transform cachedHand;
        private bool isItemInitialized = false;

        // Called in Awake
        private void InitializeInteractions()
        {
            cachedLeftHandedMode = VRSettings.GetLeftHandedMode();
            leftHandedModeCheckTimer = 0f;
            lastPhysicsCheckTime = 0f;
            UpdateInputReferences();
        }

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
            InitializeInteractions();
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
            grenadeLaser.SetActive(false);

            if (InitVRPatches.rightPointerFinger != null)
            {
                grenadeLaser.transform.parent = InitVRPatches.rightPointerFinger.transform;
                grenadeLaser.transform.localEulerAngles = new Vector3(355f, 273.6908f, 0);
                grenadeLaser.transform.localPosition = new Vector3(-0.3037f, 0.0415f, 0.0112f);
            }
        }

        //Used for the backpack - This completely bypasses the pickup animation and puts the loot directly in your bag
        private void DirectItemTransfer(EFT.Player owner, GInterface398 possibleAction, Item rootItem, IPlayer lootItemLastOwner)
        {
            isItemInitialized = false;
            // Handle magazine check if needed
            MagazineItemClass magazineItemClass = rootItem as MagazineItemClass;
            if (magazineItemClass != null && possibleAction is GClass3203 && lootItemLastOwner != null && lootItemLastOwner.ProfileId != owner.ProfileId)
            {
                owner.InventoryController.StrictCheckMagazine(magazineItemClass, false, 0, false, true);
            }

            // Execute the network transaction directly
            owner.InventoryController.RunNetworkTransaction(possibleAction, result => {
                // Update interaction if successful
                if (result.Succeed)
                {
                    owner.UpdateInteractionCast();
                }

                // Handle pickup state cleanup
                var pickupState = owner.CurrentState as PickupStateClass;
                pickupState?.Pickup(false, null);
            });
        }
        public void Update()
        {
            // Early exit conditions
            if (!VRGlobals.inGame || VRGlobals.vrPlayer.isSupporting ||
                (VRGlobals.player && VRGlobals.player.IsSprintEnabled) || VRGlobals.menuOpen)
                return;

            // Cache expensive VR settings check
            if (Time.time > leftHandedModeCheckTimer)
            {
                cachedLeftHandedMode = VRSettings.GetLeftHandedMode();
                leftHandedModeCheckTimer = Time.time + 0.1f;
                UpdateInputReferences();
            }

            // Cache trigger axis calculation
            float secondaryHandTriggerAxis = cachedLeftHandedMode ?
                SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) :
                SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any);

            // Handle laser pointer (only update when state changes)
            bool shouldShowLaser = secondaryHandTriggerAxis > 0.5f && !VRGlobals.vrPlayer.isSupporting;
            if (useLeftHandForRaycast != shouldShowLaser)
            {
                useLeftHandForRaycast = shouldShowLaser;
                laser.SetActive(shouldShowLaser);
            }

            ProcessInputStates();

            // Throttle expensive physics checks to 20 FPS
            if (Time.time - lastPhysicsCheckTime > PHYSICS_CHECK_INTERVAL)
            {
                UpdateCollisionStates();
                lastPhysicsCheckTime = Time.time;
            }

            if (Time.time - lastCacheCleanupTime > CACHE_CLEANUP_INTERVAL)
            {
                CleanupCache();
                lastCacheCleanupTime = Time.time;
            }

            // Handle held item physics
            UpdateHeldItemPhysics();

            // Handle scope interaction
            if (changingScopeZoom)
                HandleScopeInteraction();

            // Update exit states
            UpdateInteractionExitStates();
        }
        private void CleanupCache()
        {
            var keysToRemove = new List<Collider>();
            foreach (var kvp in interactableCache)
            {
                if (kvp.Key == null)
                    keysToRemove.Add(kvp.Key);
            }

            foreach (var key in keysToRemove)
            {
                interactableCache.Remove(key);
            }
        }

        private void ProcessInputStates()
        {
            ProcessRightHandInputs();
            ProcessLeftHandInputs();
        }

        private void ProcessRightHandInputs()
        {
            // Handle backHolster interactions with cached state
            if (rightHandState.inBackHolster)
            {
                if (primaryHandGrip.stateUp)
                {
                    selectWeaponHandler.TriggerSwapOtherPrimary();
                }
                if (primaryHandGrip.state && VRGlobals.vrPlayer.radialMenu)
                {
                    if (!VRGlobals.vrPlayer.radialMenu.active)
                    {
                        VRGlobals.vrPlayer.radialMenu.SetActive(true);
                        if (cachedLeftHandedMode)
                            VRGlobals.blockLeftJoystick = true;
                        else
                            VRGlobals.blockRightJoystick = true;
                    }
                }
            }

            // Handle sidearmHolster interactions with cached state
            if (rightHandState.inSidearmHolster)
            {
                if (primaryHandGrip.stateDown)
                {
                    selectWeaponHandler.TriggerSwapSidearm();
                }
            }
        }

        private void ProcessLeftHandInputs()
        {           
            // Handle rig interactions with cached state
            if (leftHandState.inRigCollider)
            {
                if (UIPatches.quickSlotUi && secondaryHandGrip.stateDown)
                {
                    UIPatches.quickSlotUi.CreateQuickSlotUi();
                    UIPatches.quickSlotUi.gameObject.SetActive(true);
                }
            }

            // Handle backpack interactions with cached state
            if (leftHandState.inBackpack && heldItem)
            {
                if (secondaryHandGrip.stateUp)
                {
                    GStruct455<GInterface398> pickUpResult = InteractionsHandlerClass.QuickFindAppropriatePlace(
                        heldItem.Item,
                        VRGlobals.player.InventoryController,
                        VRGlobals.player.InventoryController.Inventory.Equipment.ToEnumerable(),
                        EMoveItemOrder.PickUp,
                        simulate: true
                    );

                    if (pickUpResult.Succeeded && heldItem.ItemOwner.CanExecute(pickUpResult.Value))
                    {                        
                        DirectItemTransfer(VRGlobals.player, pickUpResult.Value, heldItem.Item, heldItem.LastOwner);
                        if (heldItem._rigidBody == null)
                            heldItem._rigidBody = heldItem.GetComponent<Rigidbody>();
                        heldItem._rigidBody.useGravity = true;
                        heldItem._rigidBody.detectCollisions = true;
                        heldItem = null;
                    }
                }
            }

            // Handle headGear interactions with cached state
            if (leftHandState.inHeadGear)
            {
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

            // Handle scope interactions with cached state
            if (leftHandState.inScope)
            {
                HandleScopeInteraction();
            }

            // Handle scope exit logic
            if (!leftHandState.inScope && isInRange && !secondaryHandGrip.state)
            {
                isInRange = false;
                scopeTransform = null;
                EquippablesShared.currentGunInteractController.RemoveScopeHighlight();
                hasEnteredScope = false;
            }

            // Handle loot interactions
            if (leftHandState.inInteractiveCollider)
            {
                // Process loot interactions using cached lootItem, lootableContainer, door, corpse
                ProcessInteractiveObjects();
            }
        }
        private void UpdateInputReferences()
        {
            if (cachedLeftHandedMode)
            {
                primaryHandGrip = SteamVR_Actions._default.LeftGrip;
                secondaryHandGrip = SteamVR_Actions._default.RightGrip;
                primaryInputSource = SteamVR_Input_Sources.LeftHand;
                secondaryInputSource = SteamVR_Input_Sources.RightHand;
            }
            else
            {
                primaryHandGrip = SteamVR_Actions._default.RightGrip;
                secondaryHandGrip = SteamVR_Actions._default.LeftGrip;
                primaryInputSource = SteamVR_Input_Sources.RightHand;
                secondaryInputSource = SteamVR_Input_Sources.LeftHand;
            }
        }
        private void UpdateCollisionStates()
        {
            UpdateRightHandCollisions();
            UpdateLeftHandCollisions();
        }
        private void UpdateRightHandCollisions()
        {
            var previousState = rightHandState.Clone();
            rightHandState.Reset();

            Collider[] nearbyColliders = Physics.OverlapSphere(VRGlobals.vrPlayer.RightHand.transform.position, 0.125f);

            foreach (Collider collider in nearbyColliders)
            {
                if (collider.gameObject.layer != 3) continue;

                // Use cached interactable lookup
                if (!interactableCache.ContainsKey(collider))
                {
                    interactableCache[collider] = new CachedInteractable(collider.gameObject);
                }

                var cachedInteractable = interactableCache[collider];

                switch (cachedInteractable.Type)
                {
                    case InteractableType.BackHolster:
                        rightHandState.inBackHolster = true;
                        rightHandState.backHolsterCollider = collider;
                        if (!previousState.inBackHolster)
                        {
                            SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, primaryInputSource);
                            hasEnteredBackHolster = true;
                        }
                        break;

                    case InteractableType.SidearmHolster:
                        rightHandState.inSidearmHolster = true;
                        rightHandState.sidearmHolsterCollider = collider;
                        if (!previousState.inSidearmHolster)
                        {
                            SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, primaryInputSource);
                            hasEnteredSidearmHolster = true;
                        }
                        break;
                }
            }

            // Reset enter flags when leaving areas
            if (previousState.inBackHolster && !rightHandState.inBackHolster)
                hasEnteredBackHolster = false;
            if (previousState.inSidearmHolster && !rightHandState.inSidearmHolster)
                hasEnteredSidearmHolster = false;
        }

        private void UpdateLeftHandCollisions()
        {
            var previousState = leftHandState.Clone();
            leftHandState.Reset();

            Collider[] nearbyColliders = Physics.OverlapSphere(VRGlobals.vrPlayer.LeftHand.transform.position, 0.125f);
            bool noScopeHit = true;

            foreach (Collider collider in nearbyColliders)
            {
                if (collider.gameObject.layer == 6)
                {
                    scopeTransform = collider.transform;
                    leftHandState.inScope = true;
                    noScopeHit = false;
                }
                else if (collider.gameObject.layer == 3)
                {
                    // Use cached interactable lookup
                    if (!interactableCache.ContainsKey(collider))
                    {
                        interactableCache[collider] = new CachedInteractable(collider.gameObject);
                    }

                    var cachedInteractable = interactableCache[collider];

                    switch (cachedInteractable.Type)
                    {
                        case InteractableType.RigCollider:
                            leftHandState.inRigCollider = true;
                            if (!previousState.inRigCollider)
                            {
                                SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, secondaryInputSource);
                                hasEnteredRigCollider = true;
                            }
                            break;

                        case InteractableType.BackpackCollider:
                            if (heldItem)
                            {
                                leftHandState.inBackpack = true;
                                if (!previousState.inBackpack)
                                {
                                    SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, secondaryInputSource);
                                    hasEnteredBackpack = true;
                                }
                            }
                            break;

                        case InteractableType.HeadGearCollider:
                            if (noScopeHit)
                            {
                                leftHandState.inHeadGear = true;
                                if (!previousState.inHeadGear)
                                {
                                    SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, secondaryInputSource);
                                    hasEnteredHeadGear = true;
                                }
                            }
                            break;

                        case InteractableType.LootItem:
                            leftHandState.inInteractiveCollider = true;
                            leftHandState.lootItem = cachedInteractable.Component as LootItem;
                            break;

                        case InteractableType.LootableContainer:
                            leftHandState.inInteractiveCollider = true;
                            leftHandState.lootableContainer = cachedInteractable.Component as LootableContainer;
                            break;

                        case InteractableType.Door:
                            leftHandState.inInteractiveCollider = true;
                            leftHandState.door = cachedInteractable.Component as Door;
                            break;
                    }
                }
                else if (collider.gameObject.layer == INTERACTIVE_LAYER || collider.transform.root.gameObject.layer == DEAD_BODY_LAYER)
                {
                    // Use cached interactable lookup for interactive objects too
                    if (!interactableCache.ContainsKey(collider))
                    {
                        interactableCache[collider] = new CachedInteractable(collider.gameObject);
                    }

                    var cachedInteractable = interactableCache[collider];

                    if (cachedInteractable.Type != InteractableType.None)
                    {
                        leftHandState.inInteractiveCollider = true;

                        // Cache the specific component based on type
                        switch (cachedInteractable.Type)
                        {
                            case InteractableType.LootItem:
                                leftHandState.lootItem = cachedInteractable.Component as LootItem;
                                break;
                            case InteractableType.LootableContainer:
                                leftHandState.lootableContainer = cachedInteractable.Component as LootableContainer;
                                break;
                            case InteractableType.Door:
                                leftHandState.door = cachedInteractable.Component as Door;
                                break;
                            case InteractableType.Corpse:
                                leftHandState.corpse = cachedInteractable.Component as Corpse;
                                break;
                        }

                        // Handle haptic feedback for entering loot area
                        if (!previousState.inInteractiveCollider)
                        {
                            SteamVR_Actions._default.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, secondaryInputSource);
                            hasEnteredLootCollider = true;
                        }
                    }
                }
            }

            // Reset enter flags when leaving areas
            if (previousState.inRigCollider && !leftHandState.inRigCollider)
                hasEnteredRigCollider = false;
            if (previousState.inBackpack && !leftHandState.inBackpack)
                hasEnteredBackpack = false;
            if (previousState.inHeadGear && !leftHandState.inHeadGear)
                hasEnteredHeadGear = false;
            if (previousState.inScope && !leftHandState.inScope)
                hasEnteredScope = false;
        }

        private void ProcessInteractiveObjects()
        {
            // Only process if we don't already have an item and button is pressed
            if (heldItem != null || !secondaryHandGrip.stateDown)
                return;

            // Handle different types of loot interactions using cached components
            if (leftHandState.lootItem != null)
            {
                heldItem = leftHandState.lootItem;
            }
            else if (leftHandState.lootableContainer != null)
            {
                var container = leftHandState.lootableContainer;
                VRGlobals.player.vmethod_1(container, new InteractionResult(EInteractionType.Open));
                float initialDistance = Vector3.Distance(VRGlobals.player.Transform.position, container.transform.position);
                VRGlobals.player.SetCallbackForInteraction(delegate (Action callback)
                {
                    GetActionsClass.smethod_22(PlayerOwner, callback, container, initialDistance);
                });
                VRGlobals.player.StartBehaviourTimer(EFTHardSettings.Instance.DelayToOpenContainer, delegate
                {
                    VRGlobals.player.TryInteractionCallback(container);
                });
            }
            else if (leftHandState.door != null)
            {
                var door = leftHandState.door;
                GetActionsClass.Class1630 doorInteractionClass = new GetActionsClass.Class1630();
                doorInteractionClass.door = door;
                doorInteractionClass.owner = PlayerOwner;

                if (door.DoorState == EDoorState.Open)
                    doorInteractionClass.method_5();
                else if (door.DoorState == EDoorState.Locked)
                    doorInteractionClass.method_1();
                else if (door.DoorState == EDoorState.Shut)
                    doorInteractionClass.method_0();
            }
            else if (leftHandState.corpse != null)
            {
                var corpse = leftHandState.corpse;
                GetActionsClass.Class1622 corpseInteractionClass = new GetActionsClass.Class1622();
                corpseInteractionClass.compoundItem = (InventoryEquipment)corpse.Item;
                corpseInteractionClass.rootItem = (InventoryEquipment)corpse.Item;
                corpseInteractionClass.lootItemOwner = corpse.ItemOwner;
                corpseInteractionClass.controller = VRGlobals.player.InventoryController;
                corpseInteractionClass.owner = PlayerOwner;
                corpseInteractionClass.method_3();
            }
        }
        /*
        private void UpdateHeldItemPhysics()
        {

            if (heldItem == null) return;

            Rigidbody rb = heldItem.GetComponent<Rigidbody>() ?? heldItem.gameObject.AddComponent<Rigidbody>();
            
            if (rb != null)
            {
                heldItem._rigidBody = rb;
                Transform hand = VRGlobals.vrPlayer.LeftHand.transform;
                Vector3 offsetPosition = hand.position
                    + hand.right * heldItemOffset.x
                    + hand.up * heldItemOffset.y
                    + hand.forward * heldItemOffset.z;              
                Quaternion targetRotation = hand.rotation;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.useGravity = false;
                rb.detectCollisions = true;
                heldItem._rigidBody.mass = heldItem.item_0.TotalWeight;
                rb.MovePosition(offsetPosition);
                rb.MoveRotation(targetRotation);
            }

            if (!secondaryHandGrip.state)
            {
                EquippablesShared.DropObject(heldItem, true);
                heldItem = null;
            }
        }
        */
        

        private void UpdateHeldItemPhysics()
        {
            if (heldItem == null) return;

            // Only initialize once when item is first held
            if (!isItemInitialized)
            {
                InitializeHeldItem();
                isItemInitialized = true;
            }

            // Only do the positioning math every frame
            Vector3 offsetPosition = cachedHand.position
                + cachedHand.right * heldItemOffset.x
                + cachedHand.up * heldItemOffset.y
                + cachedHand.forward * heldItemOffset.z;
            Quaternion rotation = cachedHand.rotation;

            cachedRigidbody.MovePosition(offsetPosition);
            cachedRigidbody.MoveRotation(rotation);
            // Check for drop
            if (!secondaryHandGrip.state)
            {
                isItemInitialized = false;
                PickupAndThrowables.DropObject(heldItem, true);
                heldItem = null;               
                cachedRigidbody = null;
                cachedHand = null;
            }
        }

        private void InitializeHeldItem()
        {
            cachedRigidbody = heldItem.GetComponent<Rigidbody>() ?? heldItem.gameObject.AddComponent<Rigidbody>();
            cachedHand = VRGlobals.vrPlayer.LeftHand.transform;

            heldItem._rigidBody = cachedRigidbody;
            cachedRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            cachedRigidbody.useGravity = false;
            cachedRigidbody.detectCollisions = true;
            cachedRigidbody.mass = heldItem.item_0.TotalWeight;
        }
        private void UpdateInteractionExitStates()
        {
            if (hasEnteredScope && !wasInScope)
                hasEnteredScope = false;
            if (hasEnteredBackHolster && !wasInBackHolster)
                hasEnteredBackHolster = false;
            if (hasEnteredSidearmHolster && !wasInSidearmHolster)
                hasEnteredSidearmHolster = false;
            if (hasEnteredRigCollider && !wasInRigCollider)
                hasEnteredRigCollider = false;
            if (hasEnteredBackpack && !wasInBackpack)
                hasEnteredBackpack = false;
            if (hasEnteredLootCollider && !wasinInteractiveCollider)
                hasEnteredLootCollider = false;
            if (hasEnteredHeadGear && !wasInHeadGear)
                hasEnteredHeadGear = false;
        }

        
        private void HandleScopeInteraction()
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
                if (EquippablesShared.currentGunInteractController != null && scopeTransform != null)
                {
                    EquippablesShared.currentGunInteractController.SetScopeHighlight(scopeTransform);
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

    public class InteractionState
    {
        public bool inBackHolster;
        public bool inSidearmHolster;
        public bool inRigCollider;
        public bool inBackpack;
        public bool inHeadGear;
        public bool inInteractiveCollider;
        public bool inScope;
        public Collider backHolsterCollider;
        public Collider sidearmHolsterCollider;
        public LootItem lootItem;
        public LootableContainer lootableContainer;
        public Door door;
        public Corpse corpse;

        public void Reset()
        {
            inBackHolster = inSidearmHolster = inRigCollider = inBackpack =
            inHeadGear = inInteractiveCollider = inScope = false;
            backHolsterCollider = sidearmHolsterCollider = null;
            lootItem = null;
            lootableContainer = null;
            door = null;
            corpse = null;
        }

        public InteractionState Clone()
        {
            return (InteractionState)this.MemberwiseClone();
        }
    }

    public enum InteractableType
    {
        None, BackHolster, SidearmHolster, RigCollider, BackpackCollider,
        HeadGearCollider, LootItem, LootableContainer, Door, Corpse
    }

    public class CachedInteractable
    {
        public InteractableType Type { get; private set; }
        public Component Component { get; private set; }

        public CachedInteractable(GameObject go)
        {
            // Cache component lookups by name/type
            switch (go.name)
            {
                case "backHolsterCollider":
                    Type = InteractableType.BackHolster;
                    break;
                case "sidearmHolsterCollider":
                    Type = InteractableType.SidearmHolster;
                    break;
                case "rigCollider":
                    Type = InteractableType.RigCollider;
                    break;
                case "backpackCollider":
                    Type = InteractableType.BackpackCollider;
                    break;
                case "headGearCollider":
                    Type = InteractableType.HeadGearCollider;
                    break;
                default:
                    // Check for components on interactive objects
                    if ((Component = go.GetComponent<LootItem>()) != null)
                        Type = InteractableType.LootItem;
                    else if ((Component = go.GetComponent<LootableContainer>()) != null)
                        Type = InteractableType.LootableContainer;
                    else if ((Component = go.GetComponent<Door>()) != null)
                        Type = InteractableType.Door;
                    else if (go.layer == 23 && (Component = go.transform.root.GetComponent<Corpse>()) != null)
                        Type = InteractableType.Corpse;
                    else
                        Type = InteractableType.None;
                    break;
            }
        }
    }

}
