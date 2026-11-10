using UnityEngine;
using Valve.VR;
using TarkovVR.Patches.UI;
using static TarkovVR.Source.Controls.InputHandlers;
using TarkovVR.Source.Controls;
using TarkovVR.Patches.Core.Player;
using EFT.Interactive;
using System;
using EFT;
using static EFT.InventoryLogic.ItemManipulator;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using TarkovVR.ModSupport;
using TarkovVR.ModSupport.FIKA;
using EFT.InventoryLogic;
using System.Collections.Generic;
using System.Collections;
//using LiteNetLib.Utils;
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
        // A hand holds a loot item (the pointer's grab, a magazine off the ground)
        internal static bool HoldingLoot => VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.heldItem != null;
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

        // Seconds between releasing the VR hold and running the stow pickup (StowHeldItemToInventory). On release the item
        // goes back onto FIKA's own loot sync; other clients must receive that before the pickup lands there, or the
        // world item never gets RemoveLootItem/DestroyLoot on them.
        public static float stowReleaseDelay = 0.2f;

        /// <summary>
        /// Quick-stow / equip the held loot item into the player's equipment (the behind-the-back backpack gesture and
        /// the use/eat auto-stow). Returns false if the item fits nowhere (the caller keeps holding or drops it).
        /// FIKA: the world LootItem disappears on other clients via LootItem.RemoveLootItem -> GameWorld.DestroyLoot,
        /// fired when the item leaves its GClass3390 loose-loot address. While VR-held the item is on the mod's own
        /// loot sync there (LootSyncSmoother, FIKA's ApplyNetPacket short-circuited) and a pickup applied in that state
        /// never fires it. So release first (ForceDropHeldItem), then pick up a beat later (StowAfterRelease).
        /// Fit is checked while still in hand so an item with nowhere to go is never dropped.
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
            ForceDropHeldItem();                                 // back onto FIKA's native loot path first
            StartCoroutine(StowAfterRelease(item, lastOwner));   // pick it up after the reconcile gap
            return true;
        }

        /// <summary>
        /// Waits out the reconcile gap, then picks the (now normal world) item into the inventory. Bails if the item
        /// was taken, destroyed or grabbed by another player during the wait.
        /// </summary>
        private IEnumerator StowAfterRelease(LootItem item, IPlayer lastOwner)
        {
            yield return new WaitForSeconds(stowReleaseDelay);
            // Co-op client: the host refuses a pickup sent while it still runs a move of yours (a draw we skipped, say)
            float hostWaitSince = Time.time;
            while (CoopHost.HoldsBack(hostWaitSince))
                yield return null;

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
        /// Native gaze "Take" reroute (UIPatches.RerouteHeldItemTake patches GetActionsClass.smethod_10): if rootItem is
        /// the VR-held loot item, stow it through StowHeldItemToInventory's release-then-pickup so it replicates in
        /// FIKA co-op (the plain native Take runs while the item is still on the mod's held sync, which doesn't).
        /// Returns true if we took the Take (the caller skips the native pickup animation and transaction).
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
            // A deferred use/eat item (hidden while eating) must be resolved even when the bail below skips the update
            // (the use ended while sprinting or in a menu)
            if (VRGlobals.inGame && pendingUseLoot != null)
                UpdatePendingUseLoot();

            // Chest sling / support-hold input must run before the bail below: the bail skips the update while
            // isSupporting, which is exactly when the dominant hand comes off a gun the left still holds (the SupportOnly
            // machine lives there too). Runs while sprinting; menus skip (an open hand in the inventory must not sling).
            // Ownership states here are one frame stale.
            if (VRGlobals.inGame && !VRGlobals.menuOpen && primaryHandGrip != null && VRGlobals.vrPlayer != null)
            {
                // The grenade on the chest: before the guns on the body and the sling (a grab there takes the press from them)
                GrenadeSlot.UpdateInput();
                // Guns on the body: a grip at one takes it (before the sling's regrab, which yields that press)
                BodyWeapons.UpdateInput(primaryHandGrip.state, primaryHandGrip.stateDown, rightHandState.inBackHolster, rightHandState.inSidearmHolster);
                // The holster zones only own the grip where they still swap weapons themselves
                bool hangGripOwnedElsewhere =
                    (rightHandState.inBackHolster && !BodyWeapons.ReplacesBackZone)
                    || (rightHandState.inSidearmHolster && !BodyWeapons.ReplacesHipZone)
                    || (VRGlobals.vrPlayer.radialMenu && VRGlobals.vrPlayer.radialMenu.active)
                    || (WeaponPatches.currentGunInteractController != null
                        && WeaponPatches.currentGunInteractController.lastHitCompIndex != -1);
                // The grip of the hand holding the gun (the left one while the gun is in the left hand: Handedness.GunInLeftHand)
                // A press check's gesture (thumb on A/B/the stick, finger on the trigger, one hand on a pistol) holds the gun like the grip:
                // letting go of the grip for it doesn't sling it (VRReloadController.PressCheck)
                bool gunHandHolds = Handedness.GunHandGrip.state
                    || (!ChestHang.Detached && !VRGlobals.vrPlayer.isSupporting && VRReloadController.PressCheckHoldsGun);
                ChestHang.UpdateInput(gunHandHolds, hangGripOwnedElsewhere, VRGlobals.vrPlayer.isSupporting);
                // A gun's body spot placed by hand (the trigger held on it)
                GunSpots.Tick();
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

            UpdateInputReferences();

            float secondaryHandTriggerAxis = Handedness.OffHandTrigger.GetAxis(SteamVR_Input_Sources.Any);

            // Handle laser pointer (only update when state changes)
            /*
            bool shouldShowLaser = secondaryHandTriggerAxis > 0.5f && !VRGlobals.vrPlayer.isSupporting;
            if (useLeftHandForRaycast != shouldShowLaser)
            {
                useLeftHandForRaycast = shouldShowLaser;
                laser.SetActive(shouldShowLaser);
            }
            */

            // Body grab/drag: off hand on a dead-body part with the grip held latches that ragdoll bone to the hand; a quick
            // grip tap opens the corpse loot grid instead. Runs first so the loot pointer and corpse loot-open see the
            // dragging/armed state this frame.
            UpdateBodyGrab();

            ProcessInputStates();

            // Palm-cone loot pointer: finds loose items in the cone, draws the dots, and on grip summons the main one to
            // the palm. Owns all loose-item grabbing (a point-blank item is the near end of the cone). No-op while
            // holding, summoning, or when disabled.
            UpdateLootPointer();
            // The free hand taking hold of the held item too, or letting go of it (HandsInteractionController.TwoHanded)
            UpdateSecondHand();

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
            // A second hand on the held item: still holding it, its arm still on its grip
            KeepSecondHand();
            // While an item flies to the palm the summon drives its position; once it arrives the held-item upkeep
            // takes over (same cached fields)
            if (isSummoningLoot)
                UpdateLootSummon();
            else if (heldItem != null)
                UpdateHeldItemPosition();

            // FIKA co-op: broadcast the held/summoning item's pose (throttled) through FIKA's own LootSyncStruct path
            if ((isSummoningLoot || heldItem != null) && InstalledMods.FIKAInstalled
                && HeldItemAlive() && Time.time - lastLootSyncTime >= lootSyncInterval)
            {
                lastLootSyncTime = Time.time;
                FIKASupport.SyncHeldLootItem(heldItem);
            }

            // The right hand holding the gun alone with the gun in the left hand: the left's foregrip grip as EFT animated it, before
            // the left hand opens below (HandMirror mirrors it onto the right)
            TarkovVR.Source.Player.Body.HandMirror.CaptureSupportGrip();
            // Relax the free off hand open (more when a loot dot is in view). Self-gates; must run after EFT's hand
            // animation so it offsets the final pose.
            UpdateLeftHandFingers();
            // The gun hand's finger off the trigger lies straight along the gun; the mag release press below starts from it
            UpdateTriggerDiscipline();

            // Holding a magazine: the reload animation's finger pose on top
            VRReloadController.ApplyHeldFingerPose();
            // Pulling the charging handle: a handle on the bolt brings the carrier back, then the chambered round rides the bolt
            VRReloadController.ApplyHandleCarrier();
            VRReloadController.ApplyChamberView();
            // The knob the hand took folding out (the MDR's paddles), and a press check's slide
            VRReloadController.ApplyKnobFold();
            VRReloadController.ApplyPressCheck();
            // A bolt-action's bolt left open or being worked: its bones after the animator
            VRReloadController.ApplyBolt();
            // The MDR's deflector held open or being worked
            VRReloadController.ApplyDeflector();
            VRReloadController.ApplyMagazineReleaseButton();
            // A magazine picked up off the ground: its ground item stays hidden while our model of it is held
            VRReloadController.KeepGroundMagazineHidden();

            // The gun in the left hand: the gun grip as EFT animated it on the right hand, before that hand's own update (HandMirror)
            TarkovVR.Source.Player.Body.HandMirror.CaptureGunHandGrip();
            // The same open-hand pose for the right hand while it's off the gun (ChestHang sling / support-only): the
            // animator still curls it in the grip pose otherwise.
            UpdateRightHandFingers();
            // A press check: the gun hand's fingers hooked over the back of the slide (VRReloadController.PressCheck places the hand)
            UpdatePressCheckHook();
            // That hand on the charging handle: the rack clip's finger pose on top
            VRReloadController.ApplyGunHandFingerPose();
            // Left-handed stage 2 test (off by default): the gun hand's grip mirrored onto the free off hand
            TarkovVR.Source.Player.Body.HandMirror.ApplyTest();
            // Real left-handed test (off by default): the gun in the left hand - both hands' fingers swapped onto their mirror images
            TarkovVR.Source.Player.Body.HandMirror.ApplyRealLeftHanded();
            // The gun in the left hand, a press check: its fingers' mirror image on the left (after the grip's, above)
            ApplyPressCheckHookLeft();

            // After both finger updates: buffer the rendered joint locals for the FIKA finger sync (VRArmSync sends them)
            if (InstalledMods.FIKAInstalled)
                SampleFingerSync();
        }

        private void CleanupCache()
        {
            var keysToRemove = new List<Collider>();
            foreach (var entry in interactableCache)
            {
                if (entry.Key == null)
                    keysToRemove.Add(entry.Key);
            }

            foreach (var key in keysToRemove)
            {
                interactableCache.Remove(key);
            }

            // The loot pointer's collider->LootItem cache collects destroyed colliders over a raid: flush it on the same cadence
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
                // Guns on the body: the shoulder gun is taken off the back itself (BodyWeapons), no swap on letting go
                if (primaryHandGrip.stateUp && !BodyWeapons.ReplacesBackZone)
                {
                    selectWeaponHandler.TriggerSwapOtherPrimary();
                }
            }

            if (rightHandState.inSidearmHolster)
            {
                // Guns on the body take the pistol off the holster itself; with no holster Tarkov draws no pistol there to take
                if (primaryHandGrip.stateDown && !BodyWeapons.ReplacesHipZone)
                {
                    selectWeaponHandler.TriggerSwapSidearm();
                }
            }
        }

        private void ProcessLeftHandInputs()
        {
            float secondaryHandTriggerAmount = Handedness.OffHandTrigger.axis;

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
                    // Behind-the-back quick-stow / equip (releases the VR hold before the network transaction, for FIKA)
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

            // Doors/containers: either free hand can open them
            if (leftHandState.inInteractiveCollider)
                ProcessInteractiveObjects(false);
            if (rightHandState.inInteractiveCollider)
                ProcessInteractiveObjects(true);

            UpdateRightCorpseTap();
        }
        // The hand holding the gun and the other one, as Handedness has them: the left-handed setting, or the gun in the left hand
        // (the rig's hands, which leftHandState / rightHandState are read from, follow the controllers the same way). Every frame:
        // the gun changes hands mid-play, and by the setting alone the off-hand grip would be the gun's own grip, always held
        // (the scope zone never clears and blocks the foregrip grab).
        private void UpdateInputReferences()
        {
            primaryHandGrip = Handedness.GunHandGrip;
            secondaryHandGrip = Handedness.OffHandGrip;
            primaryInputSource = Handedness.GunHandSource;
            secondaryInputSource = Handedness.OffHandSource;
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

                    // Point-blank world interactables for the dominant hand (mirrors the left): used by the right-hand
                    // loot pointer / door / container / corpse-tap interactions while the hand is off the gun.
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
                var cached = interactableCache[collider];
                if (cached.Type == InteractableType.None)
                    continue;
                rightHandState.inInteractiveCollider = true;
                switch (cached.Type)
                {
                    case InteractableType.LootItem:
                        rightHandState.lootItem = cached.Component as LootItem;
                        break;
                    case InteractableType.LootableContainer:
                        rightHandState.lootableContainer = cached.Component as LootableContainer;
                        break;
                    case InteractableType.Door:
                        rightHandState.door = cached.Component as Door;
                        break;
                    case InteractableType.Corpse:
                        rightHandState.corpse = cached.Component as Corpse;
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

        // A grip tap on a corpse with the free dominant hand opens its loot grid (like the off hand's tap-to-loot;
        // body dragging stays on the off hand).
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
                Corpse corpse = rightCorpseTapCorpse;
                rightCorpseTapArmed = false;
                rightCorpseTapCorpse = null;
                if (wasTap)
                    OpenCorpseLoot(corpse);
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
                    // Don't enter scope zone while holding a loot item, or with the gun slung (the scope grab freezes the hand on it)
                    if (heldItem != null || ChestHang.Hanging)
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

            // Only if that hand's grip was pressed; the dominant hand only interacts while it's free (off the gun,
            // outside holster zones / the slung-gun regrab).
            if (!(dominant ? primaryHandGrip : secondaryHandGrip).stateDown)
                return;
            if (dominant && !DominantHandFree())
                return;

            // Loose-item grabbing belongs to the palm-cone loot pointer (UpdateLootPointer/BeginSummon).
            // Containers/doors/corpses keep the reach-in interaction below.
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

            // Both hands on it: turned about the anchor's grip toward the second hand (HandsInteractionController.TwoHanded)
            bool twoHanded = TryTwoHandedPose(out Vector3 naturalPosition, out Quaternion twoHandedRotation);
            if (twoHanded)
                heldItem.transform.rotation = twoHandedRotation;
            else
                naturalPosition = cachedHand.TransformPoint(cachedGrabOffset);
            Vector3 clampedPosition = ClampPositionToGeometry(naturalPosition, cachedItemPosition);

            // Forced drop if the constraint has pulled the item too far from where the hand wants it
            if ((clampedPosition - naturalPosition).sqrMagnitude > MaxHoldDistance * MaxHoldDistance)
            {
                ForceDropHeldItem();
                return;
            }

            if ((clampedPosition - naturalPosition).sqrMagnitude > 0.0001f || twoHanded)
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
                        out Vector3 direction, out float dist))
                    {
                        totalPush += direction * dist;
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
        /// True while the held loot still exists as a world item. False once it's been taken into the inventory
        /// (item_0 nulled) or its GameObject destroyed: then clean up our refs without DropObject/InitializeHeldItem,
        /// which both deref item_0.TotalWeight and throw.
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

            // Taken into the inventory or destroyed: clean up without dropping (DropObject would deref the null item_0)
            if (!HeldItemAlive())
            {
                CleanupAfterHeldItemGone();
                return;
            }

            // The grip that holds the item is the one that drops it (either hand can summon)
            if ((heldInDominantHand ? primaryHandGrip : secondaryHandGrip).state)
                return;

            // The other hand holds it too: it's passed to that hand, not dropped
            if (secondHandHolding)
            {
                HandOffToSecondHand();
                return;
            }

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

            // Keep heldItem set through DropObject: it calls LootItem.method_3, and the DisableCoroutineWhenHeldItem guard
            // must still see the item as held. Otherwise the physics-settle coroutine starts on a kinematic body
            // (IsRigidbodyDone() is true there), runs StopPhysics and destroys the rigidbody: the dropped item freezes.
            LootItem dropped = heldItem;
            dropped.transform.SetParent(cachedOriginalParent, worldPositionStays: true);
            WeaponPatches.DropObject(dropped, true, throwingHand);
            heldItem = null;
            cachedRigidbody = null;
            cachedHand = null;
            // FIKA only: the body is non-kinematic now, so re-create FIKA's syncer (the tumble + rest sync to others) and
            // start the settle ourselves so the rigidbody is freed on rest and the syncer reaches Done. Otherwise a
            // settled drop keeps broadcasting as held, which the grab arbitration reads as remotely held.
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
        /// VRReloadController let go of a magazine it picked up off the ground (it held its own model, the ground item
        /// hidden): the ground item is put where the hand let go and dropped through the same release as a held item,
        /// so the throw, stamina and FIKA resync all match.
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
                    Rigidbody body = dropped.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        body.isKinematic = false;
                        body.useGravity = true;
                        body.detectCollisions = true;
                    }
                    dropped.transform.SetParent(cachedOriginalParent, worldPositionStays: true);
                    // heldItem stays set through DropObject so its method_3 doesn't start the settle coroutine on a
                    // kinematic body (StopPhysics would kill the drop). Then start the settle ourselves, FIKA only
                    // (see ReleaseHeldItem).
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
        /// FIKA co-op "steal" hand-off: another player grabbed the item we were holding, so let go without dropping it
        /// to physics (ForceDropHeldItem minus DropObject/ResyncDroppedItem). The new owner broadcasts the item and our
        /// copy follows their packets via FIKASupport.ApplyNetPacket, which calls this when we lose a contested grab.
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
                    // Non-kinematic so the new owner's ApplyNetPacket can set velocity; gravity off so it can't fall out
                    // of their hand while they broadcast a held pose (FIKASupport re-enables gravity on motion/drop/Done).
                    Rigidbody body = given.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        body.isKinematic = false;
                        body.useGravity = false;
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
        /// A use/eat began while we hold a loot item: stow it into the inventory if it fits (same path as the backpack
        /// quick-stow), otherwise drop it. Either way our hold ends, so the finger override (gated on usingItem) stops.
        /// </summary>
        private void ReleaseHeldForUse()
        {
            if (!StowHeldItemToInventory())
                ForceDropHeldItem();
        }

        //--- Deferred use/eat release (default) --------------------------------------
        // With a "use loose loot" mod the item is eaten in place: stowing it into a backpack mid-eat changes its address
        // and stops the eat. So the held item is hidden for the use and its fate decided when the use ends: fully
        // consumed -> gone; cancelled / a sip of a multi-use drink -> stow if it fits, else drop it back into the world.
        // false = stow/drop the moment the use starts (ReleaseHeldForUse).
        public static bool deferUseLootRelease = true;

        private LootItem pendingUseLoot;        // hidden held item awaiting the use/eat outcome
        private Transform pendingUseLootParent; // its original world parent (for the drop-back)
        private float pendingUseGraceUntil;     // hold off resolving until the use has settled

        /// <summary>
        /// Use/eat started while we hold a loose-loot item: take our hold off it and hide it for the duration. Does not
        /// touch the inventory (the in-place use owns the item). Resolved by UpdatePendingUseLoot once the use ends.
        /// </summary>
        private void BeginDeferredUseRelease()
        {
            LootItem item = heldItem;
            if (item == null) return;

            RemoveFikaSyncer(item);             // we're no longer driving its pose
            pendingUseLoot = item;
            pendingUseLootParent = cachedOriginalParent;
            pendingUseGraceUntil = 0f;

            // Drop our hold state without dropping/stowing the item (like CleanupAfterHeldItemGone, but the item stays
            // alive, hidden)
            heldItem = null;
            cachedRigidbody = null;
            cachedHand = null;
            isItemInitialized = false;
            isSummoningLoot = false;

            try { if (item.gameObject != null) item.gameObject.SetActive(false); } catch { }

            try { VRGlobals.player?.GetComponent<GamePlayerOwner>()?.InteractionsChangedHandler(); } catch { }
            NotifyWeightChanged();
            EatingInteractionController.LogStep("[ManualEat] Held loose-loot item hidden for the use/eat — outcome deferred.");
        }

        /// <summary>
        /// Per-frame while a hidden item awaits its outcome. Waits for the use/eat to end (plus a short settle so the
        /// finish's nutrition drain / put-away has run), then resolves:
        ///   item gone           -> consumed by the game/mod: nothing to do.
        ///   food emptied         -> eaten to 0: removal is left to the eat's discard.
        ///   item still has food  -> cancelled / partial: stow if it fits, else drop it back.
        /// Called from Update before the sprint/support/menu early-out so it always runs.
        /// </summary>
        private void UpdatePendingUseLoot()
        {
            if (pendingUseLoot == null) return; // Unity == catches a destroyed object too

            bool usingOrEating = VRGlobals.usingItem || EatingInteractionController.ManualActive;
            if (usingOrEating)
            {
                // Keep pushing the resolve out while the use is live: ManualActive flips off when the finish starts,
                // before the drain/put-away, so the item's state isn't final yet.
                pendingUseGraceUntil = Time.time + 0.4f;
                return;
            }
            if (Time.time < pendingUseGraceUntil)
                return;

            LootItem item = pendingUseLoot;
            pendingUseLoot = null;

            // Consumed (the game removed it, or it was eaten to empty): the eat's own cleanup removes it. DiscardFood
            // (ConsumeFoodWhenSettled) takes the food off its GClass3390 loose-loot address, which fires
            // LootItem.RemoveLootItem -> GameWorld.DestroyLoot and destroys the world object. Don't destroy it here:
            // that handler would then run on a dead object and throw. Just drop our reference.
            if (item == null || item.Item == null || item._item == null || IsFoodEmptied(item.Item))
            {
                EatingInteractionController.LogStep("[ManualEat] Deferred use: item consumed — removal left to the eat's discard.");
                NotifyWeightChanged();
                return;
            }

            // Not consumed (cancelled, or a sip of a multi-use drink): un-hide it and stow if it fits, else drop
            try { if (item.gameObject != null) item.gameObject.SetActive(true); } catch { }
            heldItem = item;
            cachedOriginalParent = pendingUseLootParent;
            cachedHand = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.LeftHand?.transform : null;
            EatingInteractionController.LogStep("[ManualEat] Deferred use: not consumed — returning the item (stow or drop).");
            ReleaseHeldForUse();
        }

        // A FoodDrinkComponent whose resource hit 0 = fully consumed. No component / not food =
        // treat as not-emptied (return it rather than wrongly destroy it).
        private static bool IsFoodEmptied(Item item)
        {
            try
            {
                FoodDrinkComponent foodDrink = item?.GetItemComponent<FoodDrinkComponent>();
                return foodDrink != null && foodDrink.HpPercent <= 0f;
            }
            catch { return false; }
        }

        /// <summary>
        /// FIKA puts an ItemPositionSyncer on loose items to network-sync their physics; once we take control (kinematic
        /// + reparent, or into the inventory) its NotifyDone null-refs every FixedUpdate. Strip it while we hold the
        /// item: GetComponent by name, so there is no dependency on FIKA. FIKA re-adds it on a real physics drop.
        /// </summary>
        private static void RemoveFikaSyncer(LootItem item)
        {
            if (item == null) return;
            Component syncer = item.GetComponent("ItemPositionSyncer");
            if (syncer != null) Destroy(syncer);
            // Also our network-pose interpolator (FIKASupport.LootSyncSmoother): it must not fight our hold. Our own
            // class with no FIKA types in it, so safe to name without FIKA installed.
            Component smoother = item.GetComponent<LootSyncSmoother>();
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
            VRBoolean secondaryHandGrip = Handedness.OffHandGrip;

            if (VRGlobals.menuOpen)
                return;

            if (!isInRange)
            {
                isInRange = true;
                if (!hasEnteredScope)
                {
                    VRInput.Haptic.Execute(0, INTERACT_HAPTIC_LENGTH, 1, INTERACT_HAPTIC_AMOUNT, Handedness.OffHandSource);
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
