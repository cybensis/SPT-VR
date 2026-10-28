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
using static EFT.InventoryLogic.ItemManipulator;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using TarkovVR.ModSupport;
using TarkovVR.ModSupport.FIKA;
using EFT.InventoryLogic;
using static EFT.Interactive.WorldInteractiveObject;
using System.Collections.Generic;
using System.Collections;
//using LiteNetLib.Utils;
using Comfort.Common;
using Diz.LanguageExtensions;
using Diz.Utils;
//using LiteNetLib;

namespace TarkovVR.Source.Player.Interactions
{
    internal partial class HandsInteractionController : MonoBehaviour
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
        private const float MaxHoldDistance = 0.35f;

        private VRBoolean primaryHandGrip;
        private VRBoolean secondaryHandGrip;
        private SteamVR_Input_Sources primaryInputSource;
        private SteamVR_Input_Sources secondaryInputSource;

        private bool wasInBackHolster, wasInSidearmHolster, wasInRigCollider;
        private bool wasInBackpack, wasinInteractiveCollider, wasInHeadGear, wasInScope;

        private InteractionState rightHandState = new InteractionState();
        private InteractionState leftHandState = new InteractionState();

        private Dictionary<Collider, CachedInteractable> interactableCache = new Dictionary<Collider, CachedInteractable>();

        private float lastCacheCleanupTime = 0f;
        private const float CACHE_CLEANUP_INTERVAL = 30f;

        private float lastLootSyncTime;
        private Rigidbody cachedRigidbody;
        private Vector3 cachedGrabOffset;
        private Vector3 cachedItemPosition;
        private Quaternion cachedGrabRotation;
        private Transform cachedHand;
        private Transform cachedOriginalParent;
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
        private void DirectItemTransfer(EFT.Player owner, IItemOperationResult possibleAction, Item rootItem, IPlayer lootItemLastOwner)
        {
            isItemInitialized = false;
            // Handle magazine check if needed
            Magazine magazineItemClass = rootItem as Magazine;
            if (magazineItemClass != null && possibleAction is MoveResult && lootItemLastOwner != null && lootItemLastOwner.ProfileId != owner.ProfileId)
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
                var pickupState = owner.CurrentState as PickUpState;
                pickupState?.Pickup(false, null);
            });
        }

        // Gap between releasing the VR hold and running the stow pickup (see StowHeldItemToInventory).
        // On release the item flips from the mod's custom held-sync back onto FIKA's native loot
        // networking; the other clients need a beat to receive that before the (separate-channel)
        // pickup op lands there, or the world item never gets RemoveLootItem/DestroyLoot'd on them.
        // Drops reconcile ~instantly, so this can be small — it's the code version of the human gap
        // in "let go of grip, THEN press Take". Live-tunable in the headset.
        public static float stowReleaseDelay = 0.2f;

        /// <summary>
        /// Quick-stow / equip the currently-held loot item into the player's equipment (the
        /// behind-the-back backpack gesture, and the use/eat auto-stow). Returns true if the stow
        /// was started, false if the item won't fit anywhere (the caller keeps holding / drops it).
        ///
        /// FIKA co-op (2026-06-19, traced via decompile): the world LootItem disappears on other
        /// clients via LootItem.RemoveLootItem -> GameWorld.DestroyLoot, fired when the item leaves
        /// its GClass3390 loose-loot address. Native "Take" uses the IDENTICAL
        /// InventoryController.RunNetworkTransaction this does (GetActionsClass.Class1750), so the
        /// mechanism is the same — the ONLY difference is item STATE. While VR-held, the item is on
        /// the mod's CUSTOM loot-sync path on other clients (LootSyncSmoother + NeutralizeRemoteHeld-
        /// LootPhysics, FIKA's ApplyNetPacket short-circuited); a pickup applied in that state does
        /// NOT produce the RemoveLootItem there, so it floats / the equip never lands. Dropping the
        /// item flips it back onto FIKA's NATIVE loot path, and only THEN does the pickup replicate.
        /// So: release first (ForceDropHeldItem), then run the pickup a beat later (StowAfterRelease)
        /// once that reconcile has reached the other clients — i.e. "let go, THEN Take", which works.
        /// Fit is checked while still in hand so we never drop an item that has nowhere to go.
        /// </summary>
        private bool StowHeldItemToInventory()
        {
            if (heldItem == null || !HeldItemAlive())
                return false;

            LootItem item = heldItem;
            try
            {
                var ctrl = VRGlobals.player.InventoryController;
                OperationResult<IItemOperationResult> probe = ItemManipulator.QuickFindAppropriatePlace(
                    item.Item,
                    ctrl,
                    ctrl.Inventory.Equipment.ToEnumerable(),
                    EMoveItemOrder.PickUp,
                    simulate: true);

                // Won't fit / can't equip — leave it in hand for the caller to decide.
                if (!probe.Succeeded || !item.ItemOwner.CanExecute(probe.Value))
                    return false;
            }
            catch
            {
                return false;
            }

            IPlayer lastOwner = item.LastOwner;
            ForceDropHeldItem();                                 // flip back to FIKA's native loot path NOW
            StartCoroutine(StowAfterRelease(item, lastOwner));   // pick it up after the reconcile gap
            return true;
        }

        /// <summary>
        /// One-shot helper for StowHeldItemToInventory: wait out the cross-channel reconcile gap,
        /// then pick the (now normal world) item into the inventory. Self-terminating; bails if the
        /// item was taken/destroyed or grabbed by another player during the wait.
        /// </summary>
        private IEnumerator StowAfterRelease(LootItem item, IPlayer lastOwner)
        {
            yield return new WaitForSeconds(stowReleaseDelay);

            if (item == null || item.Item == null)
                yield break;
            // Another player grabbed it in the gap (FIKA steal) — leave it to them.
            if (InstalledMods.FIKAInstalled && FIKASupport.IsRemotelyHeldRaw(item))
                yield break;

            bool ok = false;
            IItemOperationResult action = default;
            try
            {
                var ctrl = VRGlobals.player.InventoryController;
                OperationResult<IItemOperationResult> place = ItemManipulator.QuickFindAppropriatePlace(
                    item.Item,
                    ctrl,
                    ctrl.Inventory.Equipment.ToEnumerable(),
                    EMoveItemOrder.PickUp,
                    simulate: true);
                if (place.Succeeded && item.ItemOwner.CanExecute(place.Value))
                {
                    action = place.Value;
                    ok = true;
                }
            }
            catch
            {
                ok = false;
            }

            if (ok)
                DirectItemTransfer(VRGlobals.player, action, item.Item, lastOwner);
        }

        /// <summary>
        /// Native gaze "Take" reroute (UIPatches.RerouteHeldItemTake patches GetActionsClass.smethod_10):
        /// if rootItem is the currently VR-held loot item, stow it through the SAME release-then-pickup
        /// flow the behind-the-back gesture uses (StowHeldItemToInventory) so the equip/removal
        /// replicates in FIKA co-op. The plain native Take runs RunNetworkTransaction while the item is
        /// still on the mod's custom held-sync path, which doesn't replicate. Returns true if we took
        /// ownership of the Take (the caller skips the native pickup animation + transaction entirely).
        /// </summary>
        public bool TryStowHeldItem(Item rootItem)
        {
            if (heldItem == null || !HeldItemAlive() || rootItem == null || rootItem != heldItem.Item)
                return false;
            StowHeldItemToInventory();
            return true;
        }

        public void Update()
        {
            // Resolve a deferred use/eat item (hidden while eating) regardless of the bail below —
            // it must still be returned/cleaned even if the player ends the use sprinting or in a menu.
            if (VRGlobals.inGame && pendingUseLoot != null)
                UpdatePendingUseLoot();

            // Chest sling / support-hold input: MUST run BEFORE the bail below — the bail skips the
            // whole update while isSupporting, but taking the dominant hand off the gun while the left
            // holds the foregrip happens exactly IN that state (and the SupportOnly machine — regrab,
            // left-release→sling — lives there too; this was why the right hand couldn't be released
            // while supporting). Runs while sprinting as well; menus still skip (an open hand in the
            // inventory must not sling). Ownership states here are one frame stale — fine, the holster/
            // radial gestures span many frames.
            if (VRGlobals.inGame && !VRGlobals.menuOpen && primaryHandGrip != null && VRGlobals.vrPlayer != null)
            {
                bool hangGripOwnedElsewhere =
                    rightHandState.inBackHolster || rightHandState.inSidearmHolster
                    || (VRGlobals.vrPlayer.radialMenu && VRGlobals.vrPlayer.radialMenu.active)
                    || (WeaponPatches.currentGunInteractController != null
                        && WeaponPatches.currentGunInteractController.lastHitCompIndex != -1);
                ChestHang.UpdateInput(primaryHandGrip.state, hangGripOwnedElsewhere, VRGlobals.vrPlayer.isSupporting);
            }

            // Magazines by hand: also before the bail (it cancels a held magazine itself on menus)
            if (VRGlobals.inGame && VRGlobals.vrPlayer != null)
                VRReloadController.Tick();

            if (!VRGlobals.inGame || VRGlobals.vrPlayer.isSupporting ||
                (VRGlobals.player && VRGlobals.player.IsSprintEnabled) || VRGlobals.menuOpen)
            {
                // Don't leave stale loot dots floating when we bail (sprint/support/menu).
                ClearDots();
                // Don't keep a body welded to the hand through a sprint/support/menu bail.
                if (isDraggingBody) ReleaseBody();
                return;
            }

            if (Time.time > leftHandedModeCheckTimer)
            {
                cachedLeftHandedMode = VRSettings.GetLeftHandedMode();
                leftHandedModeCheckTimer = Time.time + 0.1f;
                UpdateInputReferences();
            }

            float secondaryHandTriggerAxis = cachedLeftHandedMode ?
                VRInput.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) :
                VRInput.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any);

            // Handle laser pointer (only update when state changes)
            /*
            bool shouldShowLaser = secondaryHandTriggerAxis > 0.5f && !VRGlobals.vrPlayer.isSupporting;
            if (useLeftHandForRaycast != shouldShowLaser)
            {
                useLeftHandForRaycast = shouldShowLaser;
                laser.SetActive(shouldShowLaser);
            }
            */

            // Physical body grab/drag: if the off-hand is on a dead-body part and grip is pressed,
            // latch that ragdoll bone to the hand and drag the whole body. Runs first so the loot
            // pointer and corpse loot-open see the dragging/armed state this frame. A quick grip
            // tap on a body instead opens the corpse loot grid (HOLD = drag, TAP = loot).
            UpdateBodyGrab();

            ProcessInputStates();

            // Palm-cone loot pointer: detect loose items in the cone, render the white dots,
            // and on grip summon the "main" one to the palm. This now owns ALL loose-item
            // grabbing (a point-blank item is just the near end of the cone), so the grab
            // always snaps the item's collider surface onto the palm. No-op while holding,
            // summoning, or when disabled.
            UpdateLootPointer();

            // Drop check runs after ProcessInputStates so backpack pickup (stateUp) is handled first
            if (heldItem != null)
                UpdateHeldItemDrop();

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

            if (changingScopeZoom && heldItem == null)
                HandleScopeInteraction();

            UpdateInteractionExitStates();
        }

        private void LateUpdate()
        {
            // While an item is flying to the palm, the summon drives its position; once it
            // arrives the normal held-item maintenance takes over (same cached fields).
            if (isSummoningLoot)
                UpdateLootSummon();
            else if (heldItem != null)
                UpdateHeldItemPosition();

            // FIKA co-op: broadcast the held/summoning item's pose so other players see it move
            // (throttled). Reuses FIKA's own LootSyncStruct path; no-op solo / non-networked item.
            if ((isSummoningLoot || heldItem != null) && InstalledMods.FIKAInstalled
                && HeldItemAlive() && Time.time - lastLootSyncTime >= lootSyncInterval)
            {
                lastLootSyncTime = Time.time;
                FIKASupport.SyncHeldLootItem(heldItem);
            }

            // Relax the free off-hand open (more when a loot dot is in view). Self-gates;
            // runs after EFT's hand animation. Must come last so it offsets the final pose.
            UpdateLeftHandFingers();

            // Holding a magazine: the reload animation's finger pose on top
            VRReloadController.ApplyHeldFingerPose();
            // Pulling the charging handle: the chambered round rides the bolt the animator just posed
            VRReloadController.ApplyChamberView();
            // A magazine picked up off the ground: its ground item stays hidden while our model of it is held
            VRReloadController.KeepGroundMagazineHidden();

            // Same open-hand treatment for the RIGHT hand while it's off the gun (ChestHang
            // sling / support-only) - the animator still curls it in the grip pose otherwise.
            UpdateRightHandFingers();

            // AFTER both finger updates: buffer the rendered joint locals for the FIKA finger sync
            // (VRArmSync sends them on its own cadence).
            if (InstalledMods.FIKAInstalled)
                SampleFingerSync();
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

            // The loot-pointer collider->LootItem cache can accumulate destroyed colliders
            // across a raid; flush it on the same cadence.
            lootColliderCache.Clear();
        }

        private void ProcessInputStates()
        {
            ProcessRightHandInputs();
            ProcessLeftHandInputs();
        }

        private void ProcessRightHandInputs()
        {
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
            float secondaryHandTriggerAmount = cachedLeftHandedMode ? VRInput.RightTrigger.axis : VRInput.LeftTrigger.axis;

            if (leftHandState.inBackpack)
            {
                if (UIPatches.quickSlotUi && secondaryHandGrip.stateDown)
                {
                    UIPatches.quickSlotUi.CreateQuickSlotUi();
                    UIPatches.quickSlotUi.gameObject.SetActive(true);
                }
            }

            if (leftHandState.inBackpack && heldItem)
            {
                if (secondaryHandGrip.stateUp)
                {
                    // Behind-the-back quick-stow / equip. StowHeldItemToInventory releases the VR
                    // hold BEFORE running the network transaction — required for FIKA co-op, else
                    // the equip/stow doesn't replicate to other clients (see that method).
                    StowHeldItemToInventory();
                }
            }

            if (leftHandState.inBackpack && !heldItem)
            {             
                if (secondaryHandTriggerAmount > 0.5f)
                {
                    IInputHandler baseHandler;
                    VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.DropBackpack, out baseHandler);
                    if (baseHandler != null)
                    {
                        DropBackpackHandler dropBackpackHandler = baseHandler as DropBackpackHandler;
                        dropBackpackHandler.TriggerDropBackpack();
                    }

                }
            }

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
                if (secondaryHandTriggerAmount > 0.5f)
                {
                    IInputHandler baseHandler;
                    VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ToggleHeadLight, out baseHandler);
                    if (baseHandler != null)
                    {
                        HeadLightHandler headLightHandler = baseHandler as HeadLightHandler;
                        headLightHandler.TriggerHeadLight();
                    }

                }
            }

            if (leftHandState.inScope)
            {
                // Don't interact with scope while holding a loot item
                if (heldItem == null)
                    HandleScopeInteraction();
            }

            // Handle scope exit logic
            if (!leftHandState.inScope && isInRange && !secondaryHandGrip.state)
            {
                isInRange = false;
                scopeTransform = null;
                WeaponPatches.currentGunInteractController.RemoveScopeHighlight();
                hasEnteredScope = false;
            }

            // Handle loot interactions (either free hand can open doors/containers now)
            if (leftHandState.inInteractiveCollider)
                ProcessInteractiveObjects(false);
            if (rightHandState.inInteractiveCollider)
                ProcessInteractiveObjects(true);

            UpdateRightCorpseTap();
        }
        private void UpdateInputReferences()
        {
            if (cachedLeftHandedMode)
            {
                primaryHandGrip = VRInput.LeftGrip;
                secondaryHandGrip = VRInput.RightGrip;
                primaryInputSource = SteamVR_Input_Sources.LeftHand;
                secondaryInputSource = SteamVR_Input_Sources.RightHand;
            }
            else
            {
                primaryHandGrip = VRInput.RightGrip;
                secondaryHandGrip = VRInput.LeftGrip;
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
                            VRInput.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, primaryInputSource);
                            hasEnteredBackHolster = true;
                        }
                        break;

                    case InteractableType.SidearmHolster:
                        rightHandState.inSidearmHolster = true;
                        rightHandState.sidearmHolsterCollider = collider;
                        if (!previousState.inSidearmHolster)
                        {
                            VRInput.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, primaryInputSource);
                            hasEnteredSidearmHolster = true;
                        }
                        break;

                    // Point-blank world interactables for the DOMINANT hand (mirrors the left) -
                    // feed the right-hand loot pointer / door / container / corpse-tap interactions
                    // when the hand is off the gun.
                    case InteractableType.LootItem:
                        rightHandState.inInteractiveCollider = true;
                        rightHandState.lootItem = cachedInteractable.Component as LootItem;
                        break;

                    case InteractableType.LootableContainer:
                        rightHandState.inInteractiveCollider = true;
                        rightHandState.lootableContainer = cachedInteractable.Component as LootableContainer;
                        break;

                    case InteractableType.Door:
                        rightHandState.inInteractiveCollider = true;
                        rightHandState.door = cachedInteractable.Component as Door;
                        break;
                }
            }

            // Second pass over the interactive/dead-body layers (the left scans these too) so the
            // right hand also finds doors/containers/corpses whose colliders aren't on layer 3.
            foreach (Collider collider in nearbyColliders)
            {
                if (collider.gameObject.layer != INTERACTIVE_LAYER && collider.transform.root.gameObject.layer != DEAD_BODY_LAYER)
                    continue;
                if (!interactableCache.ContainsKey(collider))
                    interactableCache[collider] = new CachedInteractable(collider.gameObject);
                var ci = interactableCache[collider];
                if (ci.Type == InteractableType.None)
                    continue;
                rightHandState.inInteractiveCollider = true;
                switch (ci.Type)
                {
                    case InteractableType.LootItem:
                        rightHandState.lootItem = ci.Component as LootItem;
                        break;
                    case InteractableType.LootableContainer:
                        rightHandState.lootableContainer = ci.Component as LootableContainer;
                        break;
                    case InteractableType.Door:
                        rightHandState.door = ci.Component as Door;
                        break;
                    case InteractableType.Corpse:
                        rightHandState.corpse = ci.Component as Corpse;
                        break;
                }
                if (!previousState.inInteractiveCollider)
                    VRInput.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, primaryInputSource);
            }

            // Reset enter flags when leaving areas
            if (previousState.inBackHolster && !rightHandState.inBackHolster)
                hasEnteredBackHolster = false;
            if (previousState.inSidearmHolster && !rightHandState.inSidearmHolster)
                hasEnteredSidearmHolster = false;
        }

        // Grip TAP on a corpse with the free DOMINANT hand opens its loot grid (mirrors the off
        // hand's tap-to-loot, minus the drag - body dragging stays on the off hand).
        private bool rightCorpseTapArmed;
        private Corpse rightCorpseTapCorpse;
        private float rightCorpseTapTime;
        private Vector3 rightCorpseTapPos;

        private void UpdateRightCorpseTap()
        {
            Transform hand = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.RightHand != null
                ? VRGlobals.vrPlayer.RightHand.transform : null;
            if (hand == null || !DominantHandFree() || heldItem != null || isSummoningLoot)
            {
                rightCorpseTapArmed = false;
                rightCorpseTapCorpse = null;
                return;
            }

            if (primaryHandGrip.stateDown && rightHandState.corpse != null)
            {
                rightCorpseTapArmed = true;
                rightCorpseTapCorpse = rightHandState.corpse;
                rightCorpseTapTime = Time.time;
                rightCorpseTapPos = hand.position;
                return;
            }

            if (rightCorpseTapArmed && primaryHandGrip.stateUp)
            {
                bool wasTap = (Time.time - rightCorpseTapTime) <= bodyTapMaxTime
                              && (hand.position - rightCorpseTapPos).sqrMagnitude <= bodyTapMaxMove * bodyTapMaxMove;
                Corpse c = rightCorpseTapCorpse;
                rightCorpseTapArmed = false;
                rightCorpseTapCorpse = null;
                if (wasTap)
                    OpenCorpseLoot(c);
            }
            else if (rightCorpseTapArmed && !primaryHandGrip.state)
            {
                rightCorpseTapArmed = false;   // lost the grip without a clean stateUp
                rightCorpseTapCorpse = null;
            }
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
                    // Don't enter scope zone while holding a loot item
                    if (heldItem != null) 
                        continue;
                    
                    scopeTransform = collider.transform;
                    leftHandState.inScope = true;
                    noScopeHit = false;
                }
                else if (collider.gameObject.layer == 3)
                {
                    if (!interactableCache.ContainsKey(collider))
                    {
                        interactableCache[collider] = new CachedInteractable(collider.gameObject);
                    }

                    var cachedInteractable = interactableCache[collider];

                    switch (cachedInteractable.Type)
                    {
                        // Rig Collider disabled but left here for possible future use
                        case InteractableType.RigCollider:
                            leftHandState.inRigCollider = true;
                            if (!previousState.inRigCollider)
                            {
                                //VRInput.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, secondaryInputSource);
                                hasEnteredRigCollider = true;
                            }
                            break;

                        case InteractableType.BackpackCollider:
                            leftHandState.inBackpack = true;
                            if (!previousState.inBackpack)
                            {
                                VRInput.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, secondaryInputSource);
                                hasEnteredBackpack = true;
                            }
                            break;

                        case InteractableType.HeadGearCollider:
                            if (noScopeHit)
                            {
                                leftHandState.inHeadGear = true;
                                if (!previousState.inHeadGear)
                                {
                                    VRInput.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, secondaryInputSource);
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
                    if (!interactableCache.ContainsKey(collider))
                    {
                        interactableCache[collider] = new CachedInteractable(collider.gameObject);
                    }

                    var cachedInteractable = interactableCache[collider];

                    if (cachedInteractable.Type != InteractableType.None)
                    {
                        leftHandState.inInteractiveCollider = true;

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
                            VRInput.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, secondaryInputSource);
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

        private void ProcessInteractiveObjects(bool dominant)
        {
            // If heldItem is destroyed (e.g. picked up into inventory), clear the reference
            if (heldItem != null && heldItem is not UnityEngine.Object)
            {
                ForceDropHeldItem();
            }

            // Block all world interactions while holding an item
            if (heldItem != null)
                return;

            InteractionState state = dominant ? rightHandState : leftHandState;

            // Only process if THAT hand's grip was pressed; the dominant hand only interacts while
            // it's genuinely free (off the gun, outside holster zones / the slung-gun regrab).
            if (!(dominant ? primaryHandGrip : secondaryHandGrip).stateDown)
                return;
            if (dominant && !DominantHandFree())
                return;

            // NOTE: loose-item grabbing is now owned by the palm-cone loot pointer
            // (UpdateLootPointer/BeginSummon) so that even a point-blank grab snaps the
            // item's collider surface onto the palm. Containers/doors/corpses keep the
            // reach-in interaction below.
            if (state.lootableContainer != null)
            {
                var container = state.lootableContainer;
                VRGlobals.player.ExecuteInteraction(container, new InteractionResult(EInteractionType.Open));
                float initialDistance = Vector3.Distance(VRGlobals.player.Transform.position, container.transform.position);
                VRGlobals.player.SetCallbackForInteraction(delegate (Action callback)
                {
                    InteractionContextHelper.OnContainerOpen(PlayerOwner, callback, container, initialDistance);
                });
                VRGlobals.player.StartBehaviourTimer(EFTHardSettings.Instance.DelayToOpenContainer, delegate
                {
                    VRGlobals.player.TryInteractionCallback(container);
                });
            }
            else if (state.door != null)
            {
                var door = state.door;
                InteractionContextHelper.CG_GetAvailableActions15 doorInteractionClass = new InteractionContextHelper.CG_GetAvailableActions15();
                doorInteractionClass.door = door;
                doorInteractionClass.owner = PlayerOwner;

                if (door.DoorState == EDoorState.Open)
                    doorInteractionClass.method_5();
                else if (door.DoorState == EDoorState.Locked)
                    doorInteractionClass.method_1();
                else if (door.DoorState == EDoorState.Shut)
                    doorInteractionClass.method_0();
            }
        }

        private void UpdateHeldItemPosition()
        {
            if (heldItem == null) return;
            // Taken into inventory / destroyed — drop our refs without touching the dead item.
            if (!HeldItemAlive()) { CleanupAfterHeldItemGone(); return; }
            if (!isItemInitialized) { InitializeHeldItem(); isItemInitialized = true; }

            cachedRigidbody.isKinematic = true;
            cachedRigidbody.detectCollisions = true;
            cachedRigidbody.useGravity = false;

            Vector3 naturalPosition = cachedHand.TransformPoint(cachedGrabOffset);
            Vector3 clampedPosition = ClampPositionToGeometry(naturalPosition, cachedItemPosition);

            // Forced drop if the constraint has pulled the item too far from where the hand wants it
            if ((clampedPosition - naturalPosition).sqrMagnitude > MaxHoldDistance * MaxHoldDistance)
            {
                ForceDropHeldItem();
                return;
            }

            if ((clampedPosition - naturalPosition).sqrMagnitude > 0.0001f)
                heldItem.transform.position = clampedPosition;
            else
                heldItem.transform.localPosition = cachedGrabOffset;

            cachedItemPosition = clampedPosition;

            if (VRSettings.GetHeldItemWeight())
                DrainHoldingStamina();
        }

        private Vector3 ClampPositionToGeometry(Vector3 targetPosition, Vector3 previousPosition)
        {
            if (heldItem._boundCollider == null)
                return targetPosition;

            int layerMask = LayerMask.GetMask("Default", "Terrain", "HighPolyCollider");
            Vector3 localCenter = heldItem._boundCollider.center;
            Vector3 halfSize = heldItem._boundCollider.size * 0.5f;
            Quaternion rotation = heldItem.transform.rotation;
            float itemRadius = Mathf.Min(halfSize.x, halfSize.y, halfSize.z);

            // Collide-and-slide: iteratively cast, hit, project leftover motion onto the hit plane
            Vector3 currentPos = previousPosition;
            Vector3 remainingMove = targetPosition - previousPosition;
            const float skinWidth = 0.001f;

            for (int i = 0; i < 4; i++)
            {
                float moveDist = remainingMove.magnitude;
                if (moveDist < 0.0001f)
                    break;

                Vector3 moveDir = remainingMove / moveDist;
                Ray ray = new Ray(currentPos, moveDir);

                if (Physics.SphereCast(ray, itemRadius, out RaycastHit hit, moveDist, layerMask, QueryTriggerInteraction.Ignore))
                {
                    // Advance up to just before the hit
                    float safeDist = Mathf.Max(0f, hit.distance - itemRadius - skinWidth);
                    currentPos += moveDir * safeDist;

                    // The portion of motion we didn't get to use
                    Vector3 leftover = remainingMove - moveDir * safeDist;

                    // Slide: project remaining motion onto the hit surface
                    remainingMove = Vector3.ProjectOnPlane(leftover, hit.normal);
                }
                else
                {
                    // Clear path — finish the move
                    currentPos += remainingMove;
                    break;
                }
            }

            // Depenetration safety net (handles grabbed-inside-wall and accumulated drift)
            Vector3 result = currentPos;
            Collider[] hits = new Collider[8];
            for (int iter = 0; iter < 4; iter++)
            {
                Vector3 boxCenter = result + rotation * localCenter;
                int count = Physics.OverlapBoxNonAlloc(boxCenter, halfSize, hits, rotation, layerMask, QueryTriggerInteraction.Ignore);

                if (count == 0)
                    break;

                Vector3 totalPush = Vector3.zero;
                bool penetrated = false;
                for (int i = 0; i < count; i++)
                {
                    Collider other = hits[i];
                    if (other == heldItem._boundCollider)
                        continue;

                    if (Physics.ComputePenetration(
                        heldItem._boundCollider, result, rotation,
                        other, other.transform.position, other.transform.rotation,
                        out Vector3 dir, out float dist))
                    {
                        totalPush += dir * dist;
                        penetrated = true;
                    }
                }

                if (!penetrated || totalPush.sqrMagnitude < 0.0001f)
                    break;

                result += totalPush;
            }

            return result;
        }

        private void DrainHoldingStamina()
        {
            const float baseHandsCapacity = 150f;
            try
            {
                if (VRGlobals.player?.Physical is not Physical physical) 
                    return;

                if (physical.HandsStamina.Current <= 0f)
                {
                    ForceDropHeldItem();
                    return;
                }

                float itemWeight = heldItem._item.TotalWeight;
                float drainPerSec = baseHandsCapacity * 0.0004f * Mathf.Pow(itemWeight, 1.20f);
                physical.ConsumeAsMelee(drainPerSec * Time.deltaTime);
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// True while the held loot still exists as a world item. False once it's been TAKEN
        /// into the inventory (item_0 nulled) or its GameObject destroyed — in which case we
        /// must clean up our refs WITHOUT calling DropObject/InitializeHeldItem, both of which
        /// deref item_0.TotalWeight and throw. (This was the reason Take-while-holding was
        /// disabled originally.)
        /// </summary>
        private bool HeldItemAlive()
        {
            return heldItem != null && heldItem.Item != null && heldItem._item != null;
        }

        // The physical controller holding the loot item (left-handed mode swaps primary/secondary)
        private SteamVR_Input_Sources HeldItemInputSource()
        {
            return heldInDominantHand ? primaryInputSource : secondaryInputSource;
        }

        /// <summary>
        /// Checks for grip release and drops the item. Only called when full input is active (not sprinting).
        /// </summary>
        private void UpdateHeldItemDrop()
        {
            if (heldItem == null)
                return;

            // Item was Taken into the inventory (or destroyed) — clean up without dropping it
            // (DropObject would deref the now-null item_0 and throw).
            if (!HeldItemAlive())
            {
                CleanupAfterHeldItemGone();
                return;
            }

            // The grip that HOLDS the item is the one that drops it (either hand can summon now).
            if ((heldInDominantHand ? primaryHandGrip : secondaryHandGrip).state)
                return;

            ReleaseHeldItem();
        }

        // Lets go of the held item: it drops with the hand's velocity, back on FIKA's own loot sync
        private void ReleaseHeldItem()
        {
            // Releasing grip mid-summon cancels it and drops the item where it is.
            isSummoningLoot = false;
            isItemInitialized = false;
            cachedRigidbody.isKinematic = false;
            SteamVR_Input_Sources throwingHand = HeldItemInputSource();
            ConsumeThrowStamina(heldItem, throwingHand);

            // Keep heldItem set THROUGH DropObject: it calls LootItem.method_3, and the
            // DisableCoroutineWhenHeldItem guard must still see it as held so it does NOT start the
            // physics-settle coroutine yet — the body is kinematic at that point, IsRigidbodyDone()
            // is true on a kinematic body, and the coroutine's first tick would StopPhysics and
            // destroy the rigidbody before the drop gets any physics (dropped items would freeze).
            LootItem dropped = heldItem;
            dropped.transform.SetParent(cachedOriginalParent, worldPositionStays: true);
            WeaponPatches.DropObject(dropped, true, throwingHand);
            heldItem = null;
            cachedRigidbody = null;
            cachedHand = null;
            // FIKA co-op only: now that the body is non-kinematic, re-create FIKA's syncer (so the
            // tumble+rest sync to others) and start the settle ourselves so the rigidbody is freed
            // on rest and the syncer reaches Done (instead of broadcasting a settled drop as "still
            // held" forever, which would mis-read as remotely-held to the grab arbitration). Solo
            // keeps the original behavior (no early-start, no premature StopPhysics).
            if (InstalledMods.FIKAInstalled)
            {
                FIKASupport.ResyncDroppedItem(dropped);
                WeaponPatches.StartSettleCoroutine(dropped);
            }

            // Restore EFT interaction state so player can interact again after drop
            try
            {
                VRGlobals.player?.GetComponent<GamePlayerOwner>()?.InteractionsChangedHandler();
            }
            catch
            {
                // ignored
            }

            NotifyWeightChanged();
        }

        /// <summary>
        /// Consumes hand stamina on throw, scaled by item weight and throw speed.
        /// No cost if the item is just dropped (velocity below threshold).
        /// </summary>
        private void ConsumeThrowStamina(LootItem item, SteamVR_Input_Sources throwingHand)
        {
            // Minimum controller speed (m/s) to count as an intentional throw
            const float throwVelocityThreshold = 1.5f;

            if (!VRSettings.GetHeldItemWeight())
                return;

            try
            {
                if (VRGlobals.player?.Physical is not Physical physical)
                    return;

                // Velocity of the controller that let go, at release
                Vector3 throwVelocity = ControllerVelocity.GetSteamVRVelocity(throwingHand);

                float speed = throwVelocity.magnitude;

                // Below threshold = just letting go, no stamina cost
                if (speed < throwVelocityThreshold)
                    return;

                float itemWeight = item._item.TotalWeight;
                float speedFactor = Mathf.Clamp01((speed - throwVelocityThreshold) / 3f);
                const float throwA = 1.340f;
                const float throwP = 0.90f;
                const float throwSf = 0.20f;
                
                // cost = a * w^p * lerp(sf, 1, speedFactor)
                float cost = throwA * Mathf.Pow(itemWeight, throwP) * Mathf.Lerp(throwSf, 1f, speedFactor);

                if (cost <= 0f)
                    return;

                physical.ConsumeAsMelee(cost);
            }
            catch
            {
                // ignored
            }
        }

        // A loot item that has arrived in the off hand (not mid-summon)
        public bool HoldsLootInOffHand(out LootItem item)
        {
            item = heldItem;
            return heldItem != null && !heldInDominantHand && !isSummoningLoot && HeldItemAlive();
        }

        /// <summary>
        /// VRReloadController let go of a magazine it picked up off the ground (it held its own model
        /// of it, the ground item hidden): the ground item is put where the hand let go and dropped
        /// through the same release as a held item, so the throw, stamina and FIKA resync all match.
        /// </summary>
        public bool ThrowFromOffHand(LootItem item, Vector3 position, Quaternion rotation)
        {
            if (heldItem != null || item == null || item.Item == null)
                return false;
            RemoveFikaSyncer(item);
            cachedRigidbody = item.GetComponent<Rigidbody>() ?? item.gameObject.AddComponent<Rigidbody>();
            item._rigidBody = cachedRigidbody;
            cachedOriginalParent = item.transform.parent;
            item.transform.SetPositionAndRotation(position, rotation);
            heldItem = item;
            heldInDominantHand = false;
            ReleaseHeldItem();
            return true;
        }

        public void ForceDropHeldItem()
        {
            LootItem dropped = heldItem;
            isItemInitialized = false;
            isSummoningLoot = false;

            if (dropped != null && dropped is UnityEngine.Object)
            {
                try
                {
                    Rigidbody rb = dropped.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = false;
                        rb.useGravity = true;
                        rb.detectCollisions = true;
                    }
                    dropped.transform.SetParent(cachedOriginalParent, worldPositionStays: true);
                    // heldItem stays set THROUGH DropObject so its method_3 doesn't start the
                    // settle coroutine while the body is kinematic (that would StopPhysics and kill
                    // the drop). Release after, then start the settle (FIKA only — see
                    // UpdateHeldItemDrop). Solo keeps the original behavior.
                    WeaponPatches.DropObject(dropped, true, HeldItemInputSource());
                    heldItem = null;
                    if (InstalledMods.FIKAInstalled)
                    {
                        FIKASupport.ResyncDroppedItem(dropped);
                        WeaponPatches.StartSettleCoroutine(dropped);
                    }
                }
                catch
                {
                     /* item may already be in an invalid state */
                }
            }

            heldItem = null;
            cachedRigidbody = null;
            cachedHand = null;

            // Restore EFT interaction state so player can interact again after drop
            try
            {
                VRGlobals.player?.GetComponent<GamePlayerOwner>()?.InteractionsChangedHandler();
            }
            catch
            {
                // ignored
            }

            NotifyWeightChanged();
        }

        /// <summary>
        /// FIKA co-op "steal" hand-off: another player grabbed the item we were holding more
        /// recently, so we hand authority to them — let go WITHOUT dropping it to physics. This is
        /// a mirror of ForceDropHeldItem MINUS DropObject/ResyncDroppedItem: we're not the owner
        /// anymore, the NEW owner broadcasts the item, and our copy just follows their packets via
        /// FIKASupport.ApplyNetPacket (which keeps gravity off while they hold it). Called from that
        /// patch when this player loses a contested grab.
        /// </summary>
        public void RelinquishHeldItem()
        {
            LootItem given = heldItem;
            heldItem = null;
            heldItemGrabTime = 0f;
            isSummoningLoot = false;
            isItemInitialized = false;

            if (given != null && given is UnityEngine.Object)
            {
                try
                {
                    // Non-kinematic so the new owner's ApplyNetPacket (which sets velocity) is
                    // valid; gravity off so it can't fall out from under their hand while they
                    // broadcast a HELD pose (FIKASupport re-enables gravity on motion/drop/Done).
                    Rigidbody rb = given.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = false;
                        rb.useGravity = false;
                    }
                    given.transform.SetParent(cachedOriginalParent, worldPositionStays: true);
                }
                catch
                {
                    // ignored
                }
            }

            cachedRigidbody = null;
            cachedHand = null;

            try
            {
                VRGlobals.player?.GetComponent<GamePlayerOwner>()?.InteractionsChangedHandler();
            }
            catch
            {
                // ignored
            }

            NotifyWeightChanged();

            // A noticeable pulse so it feels like the item was taken out of your hand.
            VRInput.Haptic.Execute(0, 0.1f, 1, 0.6f, secondaryInputSource);
        }

        /// <summary>
        /// (Req 1) A use/eat began while we still physically hold a loot item. Get our hold out
        /// of the way so the use/eat animation looks right: stow the item into the inventory if
        /// it fits (the natural place, same path as the backpack quick-stow), otherwise drop it.
        /// Either way our hold ends, so the finger override (gated on usingItem) also stops.
        /// </summary>
        private void ReleaseHeldForUse()
        {
            // Stow into the inventory if it fits (releasing the VR hold first so it replicates in
            // FIKA co-op — see StowHeldItemToInventory); otherwise just drop it out of the way.
            if (!StowHeldItemToInventory())
                ForceDropHeldItem();
        }

        //--- Deferred use/eat release (default) --------------------------------------
        // The old ReleaseHeldForUse stowed the held loose-loot item into the inventory the instant
        // a use/eat started. With the "use loose loot" mod that eats the item IN PLACE, moving it
        // into a backpack mid-eat changes its address out from under the use and STOPS the eat.
        // Instead: HIDE the held item for the duration of the use, then decide its fate when the
        // use ENDS — fully consumed -> it's gone; cancelled / a sip of a multi-use drink -> stow
        // if it fits, else drop it back into the world (it "reappears"). Flag so the old immediate
        // stow/drop can be A/B'd in the headset.
        public static bool deferUseLootRelease = true;

        private LootItem pendingUseLoot;        // hidden held item awaiting the use/eat outcome
        private Transform pendingUseLootParent; // its original world parent (for the drop-back)
        private float pendingUseGraceUntil;     // hold off resolving until the use has settled

        /// <summary>
        /// Use/eat started while we physically hold a loose-loot item: take our hold off it and
        /// HIDE it for the duration. Does NOT touch the inventory — the in-place use owns the item
        /// now. Resolved later by UpdatePendingUseLoot once the use ends.
        /// </summary>
        private void BeginDeferredUseRelease()
        {
            LootItem item = heldItem;
            if (item == null) return;

            RemoveFikaSyncer(item);             // we're no longer driving its pose
            pendingUseLoot = item;
            pendingUseLootParent = cachedOriginalParent;
            pendingUseGraceUntil = 0f;

            // Drop our active-hold plumbing WITHOUT dropping/stowing the item — mirror of
            // CleanupAfterHeldItemGone, except the item stays alive (just hidden).
            heldItem = null;
            cachedRigidbody = null;
            cachedHand = null;
            isItemInitialized = false;
            isSummoningLoot = false;

            try { if (item.gameObject != null) item.gameObject.SetActive(false); } catch { }

            try { VRGlobals.player?.GetComponent<GamePlayerOwner>()?.InteractionsChangedHandler(); } catch { }
            NotifyWeightChanged();
            Plugin.MyLog.LogInfo("[ManualEat] Held loose-loot item hidden for the use/eat — outcome deferred.");
        }

        /// <summary>
        /// Per-frame while a hidden item awaits its outcome. Waits for the use/eat to END (plus a
        /// short settle so the finish's nutrition drain / put-away has run), then resolves:
        ///   item gone           -> consumed by the game/mod: nothing to do.
        ///   food emptied         -> eaten to 0 but the loose item lingered: remove the world rep.
        ///   item still has food  -> cancelled / partial: stow if it fits, else drop it back.
        /// Called from Update before the sprint/support/menu early-out so it always runs.
        /// </summary>
        private void UpdatePendingUseLoot()
        {
            if (pendingUseLoot == null) return; // Unity == catches a destroyed object too

            bool usingOrEating = VRGlobals.usingItem || EatingInteractionController.ManualActive;
            if (usingOrEating)
            {
                // Keep pushing the resolve out while the use is live (ManualActive flips off the
                // instant the finish starts, before the drain/put-away — don't read item state yet).
                pendingUseGraceUntil = Time.time + 0.4f;
                return;
            }
            if (Time.time < pendingUseGraceUntil)
                return;

            LootItem item = pendingUseLoot;
            pendingUseLoot = null;

            // Consumed — either the game already removed it (Item null) or it was eaten to empty.
            // Either way the eat's OWN cleanup owns the removal: DiscardFood (ConsumeFoodWhenSettled)
            // pulls the food off its GClass3390 loose-loot address, which fires
            // LootItem.RemoveLootItem -> GameWorld.DestroyLoot and destroys the world GO for us. So
            // DON'T destroy it here — pre-destroying the GameObject makes that handler run on a dead
            // object (base.transform == null) -> NullReferenceException (the bug this replaced). Just
            // drop our hidden reference; the still-inactive GO is torn down by the discard's event.
            if (item == null || item.Item == null || item._item == null || IsFoodEmptied(item.Item))
            {
                Plugin.MyLog.LogInfo("[ManualEat] Deferred use: item consumed — removal left to the eat's discard.");
                NotifyWeightChanged();
                return;
            }

            // Not consumed (cancelled, or a sip of a multi-use drink): give the physical item
            // back — un-hide it and run the original stow-if-fits-else-drop path.
            try { if (item.gameObject != null) item.gameObject.SetActive(true); } catch { }
            heldItem = item;
            cachedOriginalParent = pendingUseLootParent;
            cachedHand = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.LeftHand?.transform : null;
            Plugin.MyLog.LogInfo("[ManualEat] Deferred use: not consumed — returning the item (stow or drop).");
            ReleaseHeldForUse();
        }

        // A FoodDrinkComponent whose resource hit 0 = fully consumed. No component / not food =
        // treat as not-emptied (return it rather than wrongly destroy it).
        private static bool IsFoodEmptied(Item item)
        {
            try
            {
                FoodDrinkComponent fdc = item?.GetItemComponent<FoodDrinkComponent>();
                return fdc != null && fdc.HpPercent <= 0f;
            }
            catch { return false; }
        }

        /// <summary>
        /// FIKA attaches an ItemPositionSyncer to loose items to network-sync their physics;
        /// once we take control (make it kinematic + reparent, or remove it into the inventory)
        /// that syncer's NotifyDone null-refs every FixedUpdate. Strip it while we hold the item
        /// — GetComponent BY NAME so there's no compile/run dependency on FIKA (returns null when
        /// FIKA isn't installed). FIKA re-adds its syncer on a real physics drop.
        /// </summary>
        private static void RemoveFikaSyncer(LootItem item)
        {
            if (item == null) return;
            Component syncer = item.GetComponent("ItemPositionSyncer");
            if (syncer != null) Destroy(syncer);
            // Also drop our network-pose interpolator (FIKASupport.LootSyncSmoother) if one was
            // running — we drive the item directly now, so the smoother must not fight our hold.
            // By name so this file keeps no FIKA compile dependency.
            Component smoother = item.GetComponent("LootSyncSmoother");
            if (smoother != null) Destroy(smoother);
        }

        private void InitializeHeldItem()
        {
            RemoveFikaSyncer(heldItem);
            cachedRigidbody = heldItem.GetComponent<Rigidbody>() ?? heldItem.gameObject.AddComponent<Rigidbody>();
            cachedHand = VRGlobals.vrPlayer.LeftHand.transform;

            heldItem._rigidBody = cachedRigidbody;
            cachedRigidbody.isKinematic = true;
            cachedRigidbody.detectCollisions = true;
            cachedRigidbody.useGravity = false;
            cachedRigidbody.mass = heldItem._item.TotalWeight;

            cachedGrabOffset = cachedHand.InverseTransformPoint(heldItem.transform.position);
            cachedGrabRotation = Quaternion.Inverse(cachedHand.rotation) * heldItem.transform.rotation;

            // Parent to hand — transform hierarchy handles inherited motion with perfect timing
            cachedOriginalParent = heldItem.transform.parent;
            heldItem.transform.SetParent(cachedHand, worldPositionStays: true);

            cachedItemPosition = heldItem.transform.position;

            try { VRGlobals.player?.GetComponent<GamePlayerOwner>()?.ClearInteractionState(); } catch { }
            NotifyWeightChanged();
        }

        /// <summary>
        /// Triggers EFT's weight recalculation so held item weight is reflected immediately.
        /// </summary>
        private static void NotifyWeightChanged()
        {
            if (!VRSettings.GetHeldItemWeight()) 
                return;
            
            try
            {
                VRGlobals.player?.Physical?.OnWeightUpdated();
            }
            catch
            {
                // ignored
            }
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
            VRBoolean secondaryHandGrip = (VRSettings.GetLeftHandedMode()) ? VRInput.RightGrip : VRInput.LeftGrip;

            if (VRGlobals.menuOpen)
                return;

            if (!isInRange)
            {
                isInRange = true;
                if (!hasEnteredScope)
                {
                    VRInput.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, (VRSettings.GetLeftHandedMode()) ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                    hasEnteredScope = true;
                }
            }

            if (secondaryHandGrip.state)
            {
                scopeTimeHeldFor += Time.deltaTime;
                if (scopeTimeHeldFor >= 0.3f)
                {
                    if (!changingScopeZoom && VRGlobals.vrOpticController.scopeCamera != null)
                    {
                        VRGlobals.vrOpticController.BeginPhysicalZoom();
                        changingScopeZoom = true;
                    }
                    VRGlobals.vrOpticController.TickPhysicalZoom();
                    VRGlobals.vrOpticController.UpdateGripHandPose();
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
                {
                    changingScopeZoom = false;
                    VRGlobals.vrOpticController.EndPhysicalZoom();
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
