using System.Collections.Generic;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Gun controller v2: magazines by hand. The off hand grabs the gun's magazine and pulls it out, takes a new one
    // from the chest, and seats it in the mag well. Each step runs Tarkov's own inventory move (the same moves the
    // inventory screen uses, so FIKA syncs them) and finishes Tarkov's operation the same frame, skipping its
    // animation, so the gun is never left mid-operation.
    //
    // Nothing in the inventory changes until the outcome is known:
    //  - the gun's magazine is only hidden while you hold it; letting go decides: back in the well = nothing changed,
    //    at the chest = into your rig/pockets, anywhere else = it drops to the ground where you let go
    //  - a new magazine stays in your rig until it is seated; letting go of it anywhere else puts it back
    // B double-tap (Y left-handed) drops the magazine straight out of the gun.
    public static class VRMagazineController
    {
        public static bool enabled = true;
        public static float magazineGrabRadius = 0.08f;     // m palm to the gun magazine's center to grab it
        public static float seatDistance = 0.045f;          // m magazine top to the mag well that seats it
        public static float unseatDistance = 0.07f;         // m the gun's own magazine must leave the well before it can be seated again
        public static Vector3 chestPouchOffset = new Vector3(0f, -0.42f, 0.05f);    // headset-local, yaw-only: the chest pouch zone center (headset round 1: 0.12 forward was too far out)
        public static float chestPouchRadius = 0.16f;       // m palm to the chest pouch center
        public static Vector3 newMagazineHoldPosition = new Vector3(-0.10f, 0.02f, 0.11f);    // off hand local, used until a magazine is pulled off a gun (average of his headset grabs)
        public static Vector3 newMagazineHoldRotation = new Vector3(320f, 250f, 265f);        // off hand local euler, same
        public static bool reuseLastGrabPose = true;        // new magazines sit in the hand the way the last one pulled off a gun did
        public static float dropAngularSpeed = 3f;          // rad/s tumble on a dropped magazine
        public static bool sounds = true;
        public static float soundVolume = 0.8f;
        public static bool haptics = true;
        public static bool debugZones = false;              // spheres: grab zone (blue), chest pouch (yellow), mag well seat radius (green)
        public static bool debugLog = false;

        private enum HeldMagazineSource { None, FromGun, FromPouch }
        private enum ExpectedOperation { None, PullOut, Insert }

        // Off hand state, read by VRPlayerManager / the loot pointer / the weapon interact
        public static bool OwnsOffHand { get; private set; }            // holding a magazine, or reaching for one
        public static bool HoldingMagazine => heldSource != HeldMagazineSource.None;
        public static bool HoldingGunMagazine => heldSource == HeldMagazineSource.FromGun;   // still in the gun as far as Tarkov knows: no firing

        private static HeldMagazineSource heldSource;
        private static Magazine heldMagazine;
        private static FirearmController heldForFirearm;
        private static GameObject heldProp;
        private static Transform hiddenGunMagazineView;
        private static Transform magazineWell;
        private static Vector3 propWellPointLocal;          // the point on the prop that sits at the mag well when seated
        private static bool magazineLeftWell;
        private static List<Collider> disabledPropColliders = new List<Collider>();
        private static Dictionary<Transform, int> propOriginalLayers = new Dictionary<Transform, int>();

        private static bool hasLastGrabPose;
        private static Vector3 lastGrabPosition;
        private static Quaternion lastGrabRotation;

        // Committed to an inventory move: the prop stays in the hand and the gun's magazine stays hidden until
        // Tarkov's operation is finished, so neither flashes for the frame in between
        private static GameObject pendingProp;
        private static Transform pendingHiddenView;

        private static bool previousGripState;
        private static bool previousReleaseState;
        private static int lastTickFrame = -10;

        private static ExpectedOperation expectedOperation;
        private static FirearmController expectedForFirearm;
        private static float expectedOperationDeadline;
        private static int expectedOperationStartFrame;

        // Placement for the magazine Tarkov throws into the world (VRMagazinePatches)
        private static Item pendingDropMagazine;
        private static Vector3 pendingDropPosition;
        private static Quaternion pendingDropRotation;
        private static Vector3 pendingDropVelocity;
        private static float pendingDropDeadline;

        private static readonly List<Magazine> reachableMagazines = new List<Magazine>();
        private static Transform cachedRendererRoot;
        private static Renderer[] cachedRenderers;
        private static GameObject grabZoneViz, pouchZoneViz, wellZoneViz;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Called every frame from the top of HandsInteractionController.Update
        public static void Tick()
        {
            // Resync the grip edge after skipped frames so a held grip doesn't read as a fresh press
            VRBoolean offHandGrip = VRSettings.GetLeftHandedMode() ? VRInput.RightGrip : VRInput.LeftGrip;
            bool gripState = offHandGrip.state;
            if (Time.frameCount - lastTickFrame > 1)
                previousGripState = gripState;
            lastTickFrame = Time.frameCount;
            bool gripPressed = gripState && !previousGripState;
            bool gripReleased = !gripState && previousGripState;
            previousGripState = gripState;

            // A click on the gun hand when the mag release goes down
            bool releaseState = MagazineReleaseHeld();
            if (releaseState && !previousReleaseState)
                PulseGunHand(0.25f, 0.03f);
            previousReleaseState = releaseState;

            if (expectedOperation != ExpectedOperation.None)
                TryForceExpectedOperation();

            if (HoldingMagazine)
            {
                if (!HoldIsStillValid())
                    CancelHold();
                else
                    UpdateHeldMagazine(gripReleased);
            }

            bool reaching = false;
            if (!HoldingMagazine && CanStartGrab(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
            {
                Vector3 palm = OffHandPalm();
                bool supporting = VRGlobals.vrPlayer.isSupporting;
                bool inGrabZone = InGunMagazineGrabZone(firearmController, weapon, magazineSlot, palm);
                bool inPouchZone = !supporting && InChestPouchZone(palm);
                // A hand on the support grip is only taken over by an actual grab, never by passing through a zone
                // (a pistol's support hand cups right over its magazine)
                reaching = !supporting && (inGrabZone || inPouchZone);
                if (gripPressed && inGrabZone)
                    GrabFromGun(firearmController, weapon, magazineSlot);
                else if (gripPressed && inPouchZone)
                    GrabFromPouch(firearmController, weapon, magazineSlot);
            }
            OwnsOffHand = HoldingMagazine || reaching;

            UpdateZoneViz();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Grabbing

        private static bool CanStartGrab(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot)
        {
            weapon = null;
            magazineSlot = null;
            if (!TryGetMagazineGun(out firearmController, out weapon, out magazineSlot))
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None)
                return false;
            if (ChestHang.Detached)
                return false;
            if (VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.heldItem != null)
                return false;
            return true;
        }

        // Manual reload on a gun with a detachable magazine: B is the mag release, not Tarkov's reload
        public static bool ManualReloadActive()
        {
            return VRSettings.GetManualReloadOn() && TryGetMagazineGun(out _, out _, out _);
        }

        // Holding B (Y left-handed) with manual reload on
        private static bool MagazineReleaseHeld()
        {
            if (!ManualReloadActive())
                return false;
            VRBoolean releaseButton = VRSettings.GetLeftHandedMode() ? VRInput.ButtonY : VRInput.ButtonB;
            return releaseButton.state;
        }

        // A held gun with a detachable magazine, in our player's hands
        private static bool TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot)
        {
            firearmController = null;
            weapon = null;
            magazineSlot = null;
            if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.LeftHand == null
                || VRGlobals.player == null)
                return false;
            firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController.Firearms == null || firearmController.IsInLauncherMode())
                return false;
            weapon = firearmController.Item;
            if (weapon == null)
                return false;
            if (weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazine && weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport)
                return false;
            magazineSlot = weapon.GetMagazineSlot();
            return magazineSlot != null;
        }

        private static bool InGunMagazineGrabZone(FirearmController firearmController, Weapon weapon, Slot magazineSlot, Vector3 palm)
        {
            if (weapon.GetCurrentMagazine() == null)
                return false;
            // Manual reload: only while the mag release is held, on any gun. Otherwise grab-and-pull, but not on
            // pistols (the magazine sits inside the grip where the support hand cups it) or from the support grip.
            if (VRSettings.GetManualReloadOn())
            {
                if (!MagazineReleaseHeld())
                    return false;
            }
            else if (VRGlobals.vrPlayer.isWeapPistol || VRGlobals.vrPlayer.isSupporting)
            {
                return false;
            }
            if (!TryGetGunMagazineCenter(firearmController, magazineSlot, out Vector3 magazineCenter))
                return false;
            return (palm - magazineCenter).sqrMagnitude <= magazineGrabRadius * magazineGrabRadius;
        }

        private static bool InChestPouchZone(Vector3 palm)
        {
            if (!TryGetChestPouchCenter(out Vector3 pouchCenter))
                return false;
            return (palm - pouchCenter).sqrMagnitude <= chestPouchRadius * chestPouchRadius;
        }

        private static void GrabFromGun(FirearmController firearmController, Weapon weapon, Slot magazineSlot)
        {
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            Magazine magazine = weapon.GetCurrentMagazine();
            if (magazineView == null || magazineView.ItemView == null || magazine == null)
                return;
            // Tarkov won't unload a Feed malfunction either (it wants the jam examined first)
            if (weapon.MalfState.State == Weapon.EMalfunctionState.Feed)
            {
                Pulse(0.2f, 0.03f);
                Pulse(0.2f, 0.03f, 0.08f);
                return;
            }

            GameObject prop = CreateProp(magazine);
            if (prop == null)
                return;

            // The prop takes the exact pose of the magazine on the gun, then rides the hand from there
            Transform propTransform = prop.transform;
            propTransform.SetPositionAndRotation(magazineView.ItemView.position, magazineView.ItemView.rotation);
            SetWorldScale(propTransform, magazineView.ItemView.lossyScale);
            propTransform.SetParent(VRGlobals.vrPlayer.LeftHand.transform, worldPositionStays: true);
            propWellPointLocal = propTransform.InverseTransformPoint(magazineView.Bone.position);

            lastGrabPosition = propTransform.localPosition;
            lastGrabRotation = propTransform.localRotation;
            hasLastGrabPose = true;
            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] grabbed gun magazine {magazine.LocalizedShortName()}: hand-local position {lastGrabPosition.ToString("F3")} rotation {lastGrabRotation.eulerAngles.ToString("F1")}");

            hiddenGunMagazineView = magazineView.ItemView;
            hiddenGunMagazineView.gameObject.SetActive(false);

            heldSource = HeldMagazineSource.FromGun;
            heldMagazine = magazine;
            heldForFirearm = firearmController;
            heldProp = prop;
            magazineWell = magazineView.Bone;
            magazineLeftWell = false;

            PlayMagazineSound(magazine, EInventorySoundType.pickup, propTransform.position);
            Pulse(0.35f, 0.05f);
        }

        private static void GrabFromPouch(FirearmController firearmController, Weapon weapon, Slot magazineSlot)
        {
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            Magazine magazine = FindMagazineToReload(weapon, magazineSlot);
            if (magazineView == null || magazine == null)
            {
                // Nothing that fits: a short double buzz
                Pulse(0.2f, 0.03f);
                Pulse(0.2f, 0.03f, 0.08f);
                return;
            }

            GameObject prop = CreateProp(magazine);
            if (prop == null)
                return;

            Transform propTransform = prop.transform;
            propTransform.SetParent(VRGlobals.vrPlayer.LeftHand.transform, worldPositionStays: false);
            bool useLastGrab = reuseLastGrabPose && hasLastGrabPose;
            propTransform.localPosition = useLastGrab ? lastGrabPosition : newMagazineHoldPosition;
            propTransform.localRotation = useLastGrab ? lastGrabRotation : Quaternion.Euler(newMagazineHoldRotation);
            propWellPointLocal = SeatedWellPointLocal(prop);
            Vector3 seatedScale = SeatedScale(prop);
            SetWorldScale(propTransform, Vector3.Scale(magazineView.Bone.lossyScale, seatedScale));

            heldSource = HeldMagazineSource.FromPouch;
            heldMagazine = magazine;
            heldForFirearm = firearmController;
            heldProp = prop;
            magazineWell = magazineView.Bone;
            magazineLeftWell = true;

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] took {magazine.LocalizedShortName()} ({magazine.Count} rounds) from the pouch");
            PlayMagazineSound(magazine, EInventorySoundType.pickup, propTransform.position);
            Pulse(0.35f, 0.05f);
        }

        // Tarkov's reload pick (FirearmHandsInputTranslator.ReloadExternalMagazine): the fullest reachable magazine that fits
        private static Magazine FindMagazineToReload(Weapon weapon, Slot magazineSlot)
        {
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            Magazine magazineInGun = weapon.GetCurrentMagazine();
            reachableMagazines.Clear();
            inventoryController.GetReachableItemsOfTypeNonAlloc(reachableMagazines, (Magazine magazine) => magazine.Count > 0);
            reachableMagazines.Sort((Magazine first, Magazine second) => second.Count.CompareTo(first.Count));
            foreach (Magazine magazine in reachableMagazines)
            {
                if (magazine == magazineInGun)
                    continue;
                if (ItemManipulator.CheckMoveIgnoringTargetItem(magazine, magazineSlot, inventoryController).Succeeded)
                    return magazine;
            }
            return null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Holding

        private static bool HoldIsStillValid()
        {
            if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null)
                return false;
            if (VRGlobals.firearmController == null || VRGlobals.firearmController != heldForFirearm)
                return false;
            return heldProp != null && magazineWell != null && heldMagazine != null;
        }

        private static void UpdateHeldMagazine(bool gripReleased)
        {
            Vector3 propWellPoint = heldProp.transform.TransformPoint(propWellPointLocal);
            float distanceToWell = Vector3.Distance(propWellPoint, magazineWell.position);

            if (!magazineLeftWell && distanceToWell > unseatDistance)
            {
                magazineLeftWell = true;
                Pulse(0.15f, 0.03f);
            }

            if (magazineLeftWell && distanceToWell < seatDistance)
            {
                if (heldSource == HeldMagazineSource.FromGun)
                {
                    // Pushed back in: nothing changed in the inventory
                    PlayMagazineSound(heldMagazine, EInventorySoundType.drop, magazineWell.position);
                    Pulse(0.6f, 0.06f);
                    CancelHold();
                    return;
                }
                if (heldForFirearm.Item.GetCurrentMagazine() == null && heldForFirearm.CurrentOperation is FirearmController.Idling && TryInsert())
                    return;
            }

            if (gripReleased)
                ReleaseHeldMagazine();
        }

        private static void ReleaseHeldMagazine()
        {
            if (heldSource == HeldMagazineSource.FromPouch)
            {
                // Never left the rig
                DestroyProp();
                ClearHold();
                return;
            }

            if (!magazineLeftWell || !(heldForFirearm.CurrentOperation is FirearmController.Idling))
            {
                CancelHold();
                return;
            }

            Magazine magazine = heldMagazine;
            FirearmController firearmController = heldForFirearm;
            Vector3 handPosition = heldProp.transform.position;
            Quaternion handRotation = heldProp.transform.rotation;
            bool atChest = InChestPouchZone(OffHandPalm());

            // At the chest: into the rig, or the ground if it's full. Anywhere else: the ground, or the rig where
            // nothing can be dropped (the hideout).
            if (atChest && TryBuildMoveToRig(magazine, out OperationResult<MoveResult> rigMove))
            {
                CommitHold();
                RunMoveToRig(magazine, firearmController, rigMove, handPosition);
            }
            else if (CanDropToGround(magazine))
            {
                CommitHold();
                RunDrop(magazine, firearmController, handPosition, handRotation, OffHandWorldVelocity());
            }
            else if (!atChest && TryBuildMoveToRig(magazine, out rigMove))
            {
                CommitHold();
                RunMoveToRig(magazine, firearmController, rigMove, handPosition);
            }
            else
            {
                CancelHold();
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Inventory moves (each starts Tarkov's operation; TryForceExpectedOperation finishes it)

        private static bool TryInsert()
        {
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            Slot magazineSlot = heldForFirearm.Item.GetMagazineSlot();
            OperationResult<MoveResult> moveResult = ItemManipulator.Move(heldMagazine, magazineSlot.CreateItemAddress(), inventoryController, simulate: true);
            if (moveResult.Failed || !moveResult.Value.CanExecute(inventoryController))
            {
                if (debugLog)
                    Plugin.MyLog.LogWarning($"[VRMagazine] can't seat {heldMagazine.LocalizedShortName()}: {(moveResult.Failed ? moveResult.Error.ToString() : "can't execute")}");
                return false;
            }

            Vector3 wellPosition = magazineWell.position;
            Magazine magazine = heldMagazine;
            FirearmController firearmController = heldForFirearm;
            CommitHold();

            ExpectOperation(ExpectedOperation.Insert, firearmController);
            inventoryController.TryRunNetworkTransaction(moveResult);
            TryForceExpectedOperation();

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] seated {magazine.LocalizedShortName()}");
            PlayMagazineSound(magazine, EInventorySoundType.drop, wellPosition);
            Pulse(0.8f, 0.07f);
            return true;
        }

        // Tarkov's unload (FirearmHandsInputTranslator.UnloadMag): the smallest rig/pocket grid that fits
        private static bool TryBuildMoveToRig(Magazine magazine, out OperationResult<MoveResult> moveResult)
        {
            moveResult = default;
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            GridItemAddress bestAddress = null;
            int bestGridSize = int.MaxValue;
            foreach (var grid in inventoryController.Inventory.Equipment.GetPrioritizedGridsForUnloadedObject())
            {
                GridItemAddress address = grid.FindLocationForItem(magazine);
                if (address == null)
                    continue;
                int gridSize = address.Grid.GridWidth * address.Grid.GridHeight;
                if (gridSize < bestGridSize)
                {
                    bestGridSize = gridSize;
                    bestAddress = address;
                }
            }
            if (bestAddress == null)
                return false;

            moveResult = ItemManipulator.Move(magazine, bestAddress, inventoryController, simulate: true);
            return !moveResult.Failed && moveResult.Value.CanExecute(inventoryController);
        }

        private static void RunMoveToRig(Magazine magazine, FirearmController firearmController, OperationResult<MoveResult> moveResult, Vector3 soundPosition)
        {
            ExpectOperation(ExpectedOperation.PullOut, firearmController);
            VRGlobals.player.InventoryController.TryRunNetworkTransaction(moveResult);
            TryForceExpectedOperation();

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] {magazine.LocalizedShortName()} put in the rig");
            PlayMagazineSound(magazine, EInventorySoundType.drop, soundPosition);
            Pulse(0.4f, 0.05f);
        }

        // Tarkov only drops to the ground in a raid (ItemUiContext.UnloadWeapon); in the hideout FIKA's item sync also
        // has no world to report to and throws every physics step
        private static bool CanDropToGround(Magazine magazine)
        {
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || gameWorld is HideoutGameWorld)
                return false;
            return VRGlobals.player.InventoryController.CanThrow(magazine);
        }

        private static void RunDrop(Magazine magazine, FirearmController firearmController, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            pendingDropMagazine = magazine;
            pendingDropPosition = position;
            pendingDropRotation = rotation;
            pendingDropVelocity = velocity;
            pendingDropDeadline = Time.time + 3f;

            ExpectOperation(ExpectedOperation.PullOut, firearmController);
            VRGlobals.player.InventoryController.ThrowItem(magazine, false, null);
            TryForceExpectedOperation();

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] {magazine.LocalizedShortName()} dropped");
        }

        // B double-tap (Y left-handed): the magazine falls out of the well (into the rig in the hideout). Returns false
        // when it doesn't apply, so B keeps its old job on that gun.
        public static bool TryReleaseMagazine()
        {
            // The gun's magazine is already in the off hand. A new one in the off hand is fine: drop the old, then seat.
            if (HoldingGunMagazine)
                return true;
            if (!TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None
                || ChestHang.Detached || weapon.MalfState.State == Weapon.EMalfunctionState.Feed)
                return false;
            Magazine magazine = weapon.GetCurrentMagazine();
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazine == null || magazineView == null || magazineView.ItemView == null)
                return false;

            if (CanDropToGround(magazine))
            {
                // Falls out of the well with the gun's motion
                RunDrop(magazine, firearmController, magazineView.ItemView.position, magazineView.ItemView.rotation,
                    Vector3.down * 0.5f + VRGlobals.player.Velocity);
                return true;
            }
            if (TryBuildMoveToRig(magazine, out OperationResult<MoveResult> rigMove))
            {
                RunMoveToRig(magazine, firearmController, rigMove, magazineView.ItemView.position);
                return true;
            }
            return false;
        }

        // Called from the GameWorld.ThrowItem postfix: put our dropped magazine where the hand let go of it
        public static void PlaceDroppedMagazine(Item item, LootItem lootItem)
        {
            if (item == null || lootItem == null || item != pendingDropMagazine)
                return;
            pendingDropMagazine = null;
            if (Time.time > pendingDropDeadline)
                return;

            lootItem.transform.SetPositionAndRotation(pendingDropPosition, pendingDropRotation);
            Rigidbody rigidBody = lootItem.RigidBody;
            if (rigidBody != null)
            {
                rigidBody.velocity = pendingDropVelocity;
                rigidBody.angularVelocity = UnityEngine.Random.insideUnitSphere * dropAngularSpeed;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Finishing Tarkov's operation the same frame (skips its animation; see firearm-operations-reference)

        private static void ExpectOperation(ExpectedOperation operation, FirearmController firearmController)
        {
            expectedOperation = operation;
            expectedForFirearm = firearmController;
            expectedOperationDeadline = Time.time + 2f;
            expectedOperationStartFrame = Time.frameCount;
        }

        private static void TryForceExpectedOperation()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController != expectedForFirearm)
            {
                ClearPending();
                expectedOperation = ExpectedOperation.None;
                return;
            }

            try
            {
                FirearmController.FirearmOperation operation = firearmController.CurrentOperation;
                if (expectedOperation == ExpectedOperation.PullOut && operation is FirearmController.PullOutMagOperation)
                {
                    // Visible again right before RemoveMod returns it to the pool, so the pool gets it as it found it
                    RestorePendingView();
                    // BSG's own skip: runs the remaining mag-out callbacks in order, then plays idle
                    firearmController.FastForwardCurrentState();
                    ReturnPendingProp();
                    FinishForcedOperation(firearmController);
                }
                else if (expectedOperation == ExpectedOperation.Insert && operation is FirearmController.InsertMagOperation insertOperation)
                {
                    // Not BSG's FastForward: it can switch to idle twice when there's no round to chamber
                    insertOperation.OnMagAppeared();
                    ReturnPendingProp();
                    insertOperation.OnMagInsertedToWeapon();
                    if (insertOperation.State != EFT.Player.EOperationState.Finished)
                        insertOperation.OnAddAmmoInChamber();
                    firearmController.FirearmsAnimator.Animator.Play(firearmController.FirearmsAnimator.FullIdleStateName, 1, 0.1f);
                    FinishForcedOperation(firearmController);
                }
                else if (Time.time > expectedOperationDeadline)
                {
                    Plugin.MyLog.LogWarning($"[VRMagazine] expected {expectedOperation} never started (current: {operation?.GetType().Name ?? "none"})");
                    ClearPending();
                    expectedOperation = ExpectedOperation.None;
                }
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[VRMagazine] forcing {expectedOperation} failed: {exception}");
                ClearPending();
                expectedOperation = ExpectedOperation.None;
            }
        }

        private static void RestorePendingView()
        {
            if (pendingHiddenView != null)
                pendingHiddenView.gameObject.SetActive(true);
            pendingHiddenView = null;
        }

        private static void ReturnPendingProp()
        {
            ReturnPropToPool(pendingProp);
            pendingProp = null;
        }

        // The move never happened: the magazine is still in the gun, show it again
        private static void ClearPending()
        {
            RestorePendingView();
            ReturnPendingProp();
        }

        // ReloadFloat and the "reload in progress" block only clear on Idling's IdleStart event, which the skipped
        // animation never sends
        private static void FinishForcedOperation(FirearmController firearmController)
        {
            if (firearmController.CurrentOperation is FirearmController.Idling idling)
                idling.OnIdleStartEvent();
            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] finished {expectedOperation} after {Time.frameCount - expectedOperationStartFrame} frames");
            expectedOperation = ExpectedOperation.None;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The magazine in the hand

        // Tarkov's own magazine model, borrowed from its pool. Colliders off (the weapon collision proxy copies nearby
        // colliders) and drawn on the gun's layer; both are put back before it returns to the pool.
        private static GameObject CreateProp(Magazine magazine)
        {
            ObjectsFactory objectsFactory = Singleton<ObjectsFactory>.Instance;
            if (objectsFactory == null)
                return null;
            GameObject prop = objectsFactory.CreateItem(magazine, isAnimated: false);
            if (prop == null)
                return null;

            disabledPropColliders.Clear();
            foreach (Collider collider in prop.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled)
                    continue;
                collider.enabled = false;
                disabledPropColliders.Add(collider);
            }
            propOriginalLayers.Clear();
            int gunLayer = LayerMask.NameToLayer("Player");
            foreach (Transform child in prop.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                propOriginalLayers[child] = child.gameObject.layer;
                child.gameObject.layer = gunLayer;
            }
            prop.SetActive(true);
            return prop;
        }

        private static void DestroyProp()
        {
            ReturnPropToPool(heldProp);
            heldProp = null;
        }

        private static void ReturnPropToPool(GameObject prop)
        {
            if (prop == null)
                return;
            foreach (Collider collider in disabledPropColliders)
            {
                if (collider != null)
                    collider.enabled = true;
            }
            disabledPropColliders.Clear();
            foreach (KeyValuePair<Transform, int> originalLayer in propOriginalLayers)
            {
                if (originalLayer.Key != null)
                    originalLayer.Key.gameObject.layer = originalLayer.Value;
            }
            propOriginalLayers.Clear();
            prop.transform.SetParent(null, worldPositionStays: false);
            AssetPoolObject.ReturnToPool(prop);
        }

        // Where the mag well sits in the prop's frame when seated (ContainerCollectionView.SlotView.InsertItem)
        private static Vector3 SeatedWellPointLocal(GameObject prop)
        {
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            Vector3 seatedPosition = modPlacer != null ? modPlacer.ModPosition : Vector3.zero;
            Quaternion seatedRotation = modPlacer != null ? Quaternion.Euler(modPlacer.ModRotation) : Quaternion.Euler(90f, 0f, 0f);
            Vector3 seatedScale = SeatedScale(prop);
            Vector3 wellInPropFrame = Quaternion.Inverse(seatedRotation) * -seatedPosition;
            return new Vector3(wellInPropFrame.x / seatedScale.x, wellInPropFrame.y / seatedScale.y, wellInPropFrame.z / seatedScale.z);
        }

        private static Vector3 SeatedScale(GameObject prop)
        {
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            if (modPlacer == null || modPlacer.ModScale.x == 0f || modPlacer.ModScale.y == 0f || modPlacer.ModScale.z == 0f)
                return Vector3.one;
            return modPlacer.ModScale;
        }

        private static void SetWorldScale(Transform target, Vector3 worldScale)
        {
            Vector3 parentScale = target.parent != null ? target.parent.lossyScale : Vector3.one;
            target.localScale = new Vector3(worldScale.x / parentScale.x, worldScale.y / parentScale.y, worldScale.z / parentScale.z);
        }

        private static void RestoreGunMagazineView()
        {
            if (hiddenGunMagazineView != null)
                hiddenGunMagazineView.gameObject.SetActive(true);
            hiddenGunMagazineView = null;
        }

        // Committed to an inventory move: the prop and the hidden gun magazine go to the expected operation, which
        // cleans them up when Tarkov finishes (or puts the magazine back if it never does)
        private static void CommitHold()
        {
            pendingProp = heldProp;
            pendingHiddenView = hiddenGunMagazineView;
            heldProp = null;
            hiddenGunMagazineView = null;
            ClearHold();
        }

        // Puts everything back as it was: the gun's magazine visible again, nothing in the hand
        private static void CancelHold()
        {
            RestoreGunMagazineView();
            DestroyProp();
            ClearHold();
        }

        private static void ClearHold()
        {
            heldSource = HeldMagazineSource.None;
            heldMagazine = null;
            heldForFirearm = null;
            magazineWell = null;
            magazineLeftWell = false;
            OwnsOffHand = false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Hand and zones

        // The rendered hand's knuckle line (the loot pointer's palm). The LeftHand rig transform sits at the wrist.
        private static Vector3 OffHandPalm()
        {
            if (VRGlobals.handsInteractionController != null)
                return VRGlobals.handsInteractionController.PalmOrigin(dominant: false);
            Transform leftHand = VRGlobals.vrPlayer.LeftHand.transform;
            return leftHand.position + leftHand.forward * GunInteractionController.interactProbeForward;
        }

        private static Vector3 OffHandWorldVelocity()
        {
            // Controller velocity is tracking-space only; add the body's so a magazine dropped while walking carries on
            Vector3 trackingVelocity = ControllerVelocity.GetSteamVRVelocity(OffHandSource());
            Vector3 handVelocity = VRGlobals.vrOffsetter != null ? VRGlobals.vrOffsetter.transform.TransformDirection(trackingVelocity) : trackingVelocity;
            return handVelocity + VRGlobals.player.Velocity;
        }

        private static bool TryGetGunMagazineCenter(FirearmController firearmController, Slot magazineSlot, out Vector3 magazineCenter)
        {
            magazineCenter = Vector3.zero;
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazineView == null || magazineView.ItemView == null)
                return false;
            if (cachedRendererRoot != magazineView.ItemView)
            {
                cachedRendererRoot = magazineView.ItemView;
                cachedRenderers = magazineView.ItemView.GetComponentsInChildren<Renderer>();
            }
            if (cachedRenderers == null || cachedRenderers.Length == 0)
            {
                magazineCenter = magazineView.ItemView.position;
                return true;
            }
            Bounds bounds = cachedRenderers[0].bounds;
            for (int i = 1; i < cachedRenderers.Length; i++)
            {
                if (cachedRenderers[i] != null)
                    bounds.Encapsulate(cachedRenderers[i].bounds);
            }
            magazineCenter = bounds.center;
            return true;
        }

        private static bool TryGetChestPouchCenter(out Vector3 pouchCenter)
        {
            pouchCenter = Vector3.zero;
            if (!ChestHang.HeadPose(out _, out _))
                return false;
            Quaternion headYaw = ChestHang.HeadYaw(out Vector3 headPosition);
            Vector3 offset = chestPouchOffset;
            if (VRSettings.GetLeftHandedMode())
                offset.x = -offset.x;
            pouchCenter = headPosition + headYaw * offset;
            return true;
        }

        private static SteamVR_Input_Sources OffHandSource()
        {
            return VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand;
        }

        private static void Pulse(float amplitude, float duration, float delay = 0f)
        {
            if (haptics)
                VRInput.Haptic.Execute(delay, duration, 120f, amplitude, OffHandSource());
        }

        private static void PulseGunHand(float amplitude, float duration)
        {
            if (!haptics)
                return;
            SteamVR_Input_Sources gunHand = VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
            VRInput.Haptic.Execute(0f, duration, 120f, amplitude, gunHand);
        }

        private static void PlayMagazineSound(Item magazine, EInventorySoundType soundType, Vector3 position)
        {
            if (!sounds || magazine == null)
                return;
            GUISounds guiSounds = Singleton<GUISounds>.Instance;
            BetterAudio betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (guiSounds == null || betterAudio == null)
                return;
            AudioClip clip = guiSounds.GetItemClip(magazine.ItemSound, soundType);
            if (clip != null)
                betterAudio.TryPlayAtPoint(out _, position, clip, BetterAudio.AudioSourceGroupType.Character, 25, soundVolume);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Debug spheres

        private static void UpdateZoneViz()
        {
            FirearmController firearmController = null;
            Weapon weapon = null;
            Slot magazineSlot = null;
            bool show = debugZones && TryGetMagazineGun(out firearmController, out weapon, out magazineSlot);
            if (!show)
            {
                SetZoneViz(ref grabZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref pouchZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref wellZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }

            Vector3 palm = OffHandPalm();
            bool hasMagazineCenter = TryGetGunMagazineCenter(firearmController, magazineSlot, out Vector3 magazineCenter);
            bool inGrabZone = InGunMagazineGrabZone(firearmController, weapon, magazineSlot, palm);
            SetZoneViz(ref grabZoneViz, hasMagazineCenter && !HoldingMagazine, magazineCenter, magazineGrabRadius,
                inGrabZone ? new Color(0f, 1f, 0f, 0.3f) : new Color(0f, 0.4f, 1f, 0.3f));

            bool hasPouchCenter = TryGetChestPouchCenter(out Vector3 pouchCenter);
            SetZoneViz(ref pouchZoneViz, hasPouchCenter, pouchCenter, chestPouchRadius,
                InChestPouchZone(palm) ? new Color(0f, 1f, 0f, 0.25f) : new Color(1f, 0.9f, 0f, 0.25f));

            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            bool hasWell = magazineView != null && magazineView.Bone != null;
            SetZoneViz(ref wellZoneViz, hasWell && HoldingMagazine, hasWell ? magazineView.Bone.position : Vector3.zero, seatDistance, new Color(0f, 1f, 0.4f, 0.4f));
        }

        private static void SetZoneViz(ref GameObject sphere, bool show, Vector3 position, float radius, Color color)
        {
            if (!show)
            {
                if (sphere != null)
                    sphere.SetActive(false);
                return;
            }
            if (sphere == null)
            {
                sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "VRMagazineZone";
                UnityEngine.Object.Destroy(sphere.GetComponent<Collider>());
                Renderer sphereRenderer = sphere.GetComponent<Renderer>();
                sphereRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                sphereRenderer.receiveShadows = false;
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                    shader = Shader.Find("Standard");
                sphereRenderer.material = new Material(shader);
            }
            sphere.SetActive(true);
            sphere.transform.position = position;
            sphere.transform.localScale = Vector3.one * (radius * 2f);
            sphere.GetComponent<Renderer>().material.color = color;
        }
    }
}
