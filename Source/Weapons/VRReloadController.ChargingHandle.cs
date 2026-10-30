using EFT.InventoryLogic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The charging handle: the off-hand trigger takes it, the hand pulls it straight back along the barrel on a track
    // (locked on it, the handle model moving with it) and lets go of the trigger. Like the real thing:
    //  - pulled all the way back with a round in the chamber -> Tarkov's own rechamber right then: that round is thrown
    //    out and the next one chambered as the bolt comes back
    //  - let go from a full pull -> the bolt slams forward; a bolt locked back (a magazine swapped by hand into an empty
    //    gun) closes. Tarkov already put the round in the chamber when the magazine went in; it has no operation that
    //    chambers on its own. Same for a magazine put into a gun with its bolt forward on an empty chamber
    //    (WaitingForRack): that round counts as in the magazine until a full pull.
    // The bolt lock is Tarkov's bolt-catch animator flag. Tarkov clears it in its own reload; while it's on the gun
    // doesn't fire (BlocksFiring).
    // The bolt rides back with the handle so the chamber shows: Tarkov's own locked-back pose (the "Catch" layer, or the
    // IdlePosition blend on guns that use that instead) blended in by how far back the handle is. Only the pose; the
    // bolt-catch flag isn't touched, and letting go puts the pose back to what that flag says.
    public static partial class VRReloadController
    {
        public static bool chargingHandle = true;
        public static float handleGrabRadius = 0.06f;      // m palm to the back of the charging handle to grab it
        public static float handleTravel = 0.08f;          // m the handle comes back (headset: 0.07 a touch short)
        public static float handleFullPull = 0.9f;         // share of handleTravel that counts as all the way back
        public static float handleLateralEscape = 0.10f;   // m the controller moves sideways off the track before the hand lets go
        public static bool boltFollowsHandle = true;       // false = only the handle model moves
        public static bool showChamberedRound = true;      // the round in the chamber shows while the bolt is open, riding the bolt face
        public static float boltLockOnDistance = 0.003f;   // m a part has to come back before it's taken as the bolt the round follows

        public static bool HoldingChargingHandle { get; private set; }
        public static bool OffHandAtChargingHandle { get; private set; }   // the trigger is the handle's here, not a weapon-part interaction

        public static bool BoltLockedBack
        {
            get
            {
                FirearmController firearmController = VRGlobals.firearmController;
                return firearmController != null && firearmController.FirearmsAnimator != null && firearmController.FirearmsAnimator.GetBoltCatch();
            }
        }

        private static FirearmController handleForFirearm;
        private static Transform handleBone;                // the mod_charge slot bone
        private static Transform handleView;                // the handle model, a direct child of that bone
        private static Vector3 handleRestLocalPosition;
        private static Vector3 handleAxisInSlot;            // back toward the stock, in the slot bone's frame
        private static Vector3 handAtHandleGrabInSlot;      // the controller (LeftHand) when the handle was grabbed
        private static float handlePull;                    // m back from rest
        private static bool handleFullyBack;

        // The chambered round while the bolt is open: Tarkov's round model sits on the patron_in_weapon bone, shown and
        // hidden by the clips' events
        private static bool showingChamber;
        private static bool chamberShownEmpty;              // the pull threw the round out, or it's still in the magazine: empty until the bolt goes forward
        private static Transform chamberBone;
        private static bool chamberBoneWasActive;
        private static Transform chamberedRound;
        private static Vector3 chamberedRoundRestLocalPosition;
        private static Transform[] boltParts;               // the gun's parts and where they sat with the bolt closed
        private static Vector3[] boltPartsRestInChamber;
        private static Transform boltPart;                  // the one found moving back with the bolt
        private static Vector3 boltPartRestInChamber;

        // The handle's slot, looked up again now and then (mods only change in the inventory)
        private static FirearmController handleSlotFor;
        private static ContainerCollectionView.SlotView handleSlotView;
        private static float handleSlotLookupTime;
        private static Transform handleMeshesFor;
        private static MeshFilter[] handleMeshes;
        private static GameObject handleZoneViz;

        // The gun a magazine went into with its bolt forward on an empty chamber: Tarkov's insert chambered the top round,
        // but it's still in the magazine until the handle is racked. On the weapon, so it holds across a weapon swap.
        private static Weapon weaponWaitingForRack;
        private static bool insertNeedsRack;                // the insert under way went in on a closed bolt and an empty chamber

        private static bool WaitingForRack(Weapon weapon)
        {
            // The round left some other way (unloaded in the inventory)
            if (weaponWaitingForRack != null && weaponWaitingForRack.ChamberAmmoCount == 0)
                weaponWaitingForRack = null;
            return weapon != null && weapon == weaponWaitingForRack;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static bool TryGetChargingHandle(FirearmController firearmController, Weapon weapon, out ContainerCollectionView.SlotView slotView)
        {
            if (handleSlotFor != firearmController || Time.time > handleSlotLookupTime)
            {
                handleSlotFor = firearmController;
                handleSlotLookupTime = Time.time + 2f;
                handleSlotView = null;
                foreach (Slot slot in weapon.AllSlots)
                {
                    if (slot.ID != EWeaponModType.mod_charge.ToString() || slot.ContainedItem == null)
                        continue;
                    handleSlotView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(slot);
                    break;
                }
            }
            slotView = handleSlotView;
            return slotView != null && slotView.ItemView != null && slotView.Bone != null;
        }

        private static bool InChargingHandleZone(FirearmController firearmController, Weapon weapon, Vector3 palm)
        {
            if (!chargingHandle || !TryGetChargingHandle(firearmController, weapon, out ContainerCollectionView.SlotView slotView))
                return false;
            Vector3 grabPoint = HandleGrabPoint(slotView.ItemView, firearmController.WeaponRoot.up);
            return (palm - grabPoint).sqrMagnitude <= handleGrabRadius * handleGrabRadius;
        }

        // The back end of the handle (the T on an AR): the handle model is a long bar reaching forward into the receiver,
        // so its middle would be nowhere near where you grab it. The furthest point of its meshes' boxes toward the stock.
        private static Vector3 HandleGrabPoint(Transform view, Vector3 backWorld)
        {
            if (handleMeshesFor != view)
            {
                handleMeshesFor = view;
                handleMeshes = view.GetComponentsInChildren<MeshFilter>();
            }
            Vector3 grabPoint = view.position;
            float furthestBack = float.MinValue;
            foreach (MeshFilter meshFilter in handleMeshes)
            {
                if (meshFilter == null)
                    continue;
                if (meshFilter.sharedMesh == null)
                    continue;
                Transform meshTransform = meshFilter.transform;
                Bounds bounds = meshFilter.sharedMesh.bounds;
                Vector3 center = meshTransform.TransformPoint(bounds.center);
                Vector3 halfX = meshTransform.TransformVector(new Vector3(bounds.extents.x, 0f, 0f));
                Vector3 halfY = meshTransform.TransformVector(new Vector3(0f, bounds.extents.y, 0f));
                Vector3 halfZ = meshTransform.TransformVector(new Vector3(0f, 0f, bounds.extents.z));
                // How far the box reaches back from its center
                float reachBack = Mathf.Abs(Vector3.Dot(halfX, backWorld)) + Mathf.Abs(Vector3.Dot(halfY, backWorld)) + Mathf.Abs(Vector3.Dot(halfZ, backWorld));
                float back = Vector3.Dot(center, backWorld) + reachBack;
                if (back > furthestBack)
                {
                    furthestBack = back;
                    grabPoint = center + backWorld * reachBack;
                }
            }
            return grabPoint;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void GrabChargingHandle(FirearmController firearmController, Weapon weapon)
        {
            if (!TryGetChargingHandle(firearmController, weapon, out ContainerCollectionView.SlotView slotView))
                return;

            handleForFirearm = firearmController;
            handleBone = slotView.Bone;
            handleView = slotView.ItemView;
            handleRestLocalPosition = handleView.localPosition;
            // WeaponRoot -up is toward the muzzle, so +up is back toward the stock
            handleAxisInSlot = handleBone.InverseTransformDirection(firearmController.WeaponRoot.up).normalized;
            handAtHandleGrabInSlot = handleBone.InverseTransformPoint(VRGlobals.vrPlayer.LeftHand.transform.position);
            handlePull = 0f;
            handleFullyBack = false;
            HoldingChargingHandle = true;
            LatchHand(handleView);
            BeginChamberView(firearmController, weapon);

            if (debugLog)
            {
                FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
                Transform[] patrons = firearmController.Firearms._patronInWeaponTransforms;
                Transform patron = patrons != null && patrons.Length > 0 ? patrons[0] : null;
                Plugin.MyLog.LogInfo($"[VRReload] grabbed the charging handle '{handleView.name}', bolt locked back: {BoltLockedBack}, chamber: {weapon.ChamberAmmoCount}, " +
                    $"catch layer: {firearmsAnimator.BOLT_CATCH_LAYER_INDEX}, idle position: {firearmsAnimator.HasParameter(AnimationControllerParametersTable.FLOAT_IDLEPOSITION)}, " +
                    $"round model shown: {(patron != null ? $"{patron.gameObject.activeSelf} ({patron.childCount} child)" : "no patron_in_weapon")}");
            }
            Pulse(0.3f, 0.04f);
        }

        private static bool ChargingHandleHoldIsStillValid()
        {
            if (!enabled || !chargingHandle || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null)
                return false;
            if (VRGlobals.firearmController == null || VRGlobals.firearmController != handleForFirearm)
                return false;
            // Our own rechamber runs for a frame while the handle is back
            if (!(handleForFirearm.CurrentOperation is FirearmController.Idling) && expectedOperation != ExpectedOperation.Rechamber)
                return false;
            return handleView != null && handleBone != null;
        }

        // The controller's travel along the barrel moves the handle back; sideways it can't go
        private static void UpdateChargingHandle(bool triggerReleased)
        {
            Vector3 axis = handleBone.TransformDirection(handleAxisInSlot).normalized;
            Vector3 handDelta = VRGlobals.vrPlayer.LeftHand.transform.position - handleBone.TransformPoint(handAtHandleGrabInSlot);
            float handAlongTrack = Vector3.Dot(handDelta, axis);
            float handOffTrack = (handDelta - axis * handAlongTrack).magnitude;

            handlePull = Mathf.Clamp(handAlongTrack, 0f, handleTravel);
            handleView.position = handleBone.TransformPoint(handleRestLocalPosition) + axis * handlePull;

            if (!handleFullyBack && handlePull >= handleTravel * handleFullPull)
            {
                handleFullyBack = true;
                BoltComesBack(handleForFirearm);
            }

            // After the rechamber above: its skip puts the bolt pose back to the catch flag, this frame would flash it closed
            if (boltFollowsHandle)
            {
                FirearmsAnimator firearmsAnimator = handleForFirearm.FirearmsAnimator;
                float boltOpen = Mathf.Clamp01(handlePull / (handleTravel * handleFullPull));
                SetBoltOpen(firearmsAnimator, firearmsAnimator.GetBoltCatch() ? 1f : boltOpen);
            }

            // Once it has been all the way back, letting go slams the bolt forward whatever the handle does after
            if (triggerReleased || handOffTrack > handleLateralEscape)
                LetGoOfChargingHandle(snapForward: handleFullyBack);
        }

        // All the way back: a round in the chamber comes out now, as the bolt comes back, and the next one goes in
        private static void BoltComesBack(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            PlayGunSound(firearmController, "HandleOut");
            Pulse(0.5f, 0.04f);
            // An empty magazine's follower pushes the catch up under the bolt. (With a round to throw out Tarkov's
            // rechamber below sets it.)
            if (!firearmController.FirearmsAnimator.GetBoltCatch() && BoltStaysBack(weapon))
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo("[VRReload] empty magazine: the bolt locks back");
                firearmController.FirearmsAnimator.SetBoltCatch(true);
                return;
            }
            // Nothing in the chamber to throw out (a round still in the magazine only goes in as the bolt closes)
            if (firearmController.FirearmsAnimator.GetBoltCatch() || weapon.ChamberAmmoCount == 0 || WaitingForRack(weapon))
                return;
            if (!(firearmController.CurrentOperation is FirearmController.Idling))
                return;
            if (debugLog)
                Plugin.MyLog.LogInfo("[VRReload] rechamber: the chambered round comes out");
            ExpectOperation(ExpectedOperation.Rechamber, firearmController);
            VRGlobals.player.InventoryController.RechamberWeapon(weapon);
            TryForceExpectedOperation();
            chamberShownEmpty = true;
        }

        private static void LetGoOfChargingHandle(bool snapForward)
        {
            // The hand first, where it is, so it doesn't snap forward with the handle
            ReleaseHandLatch();
            EndChamberView();
            if (handleView != null)
                handleView.localPosition = handleRestLocalPosition;
            HoldingChargingHandle = false;
            FirearmController firearmController = handleForFirearm;
            handleForFirearm = null;
            handleBone = null;
            handleView = null;

            if (firearmController == null)
                return;
            if (firearmController != VRGlobals.firearmController)
            {
                RestoreBoltPose(firearmController);
                return;
            }
            if (snapForward)
                BoltSlamsForward(firearmController);
            else
                PlayGunSound(firearmController, "HandleIn");
            RestoreBoltPose(firearmController);
        }

        // 0 = closed, 1 = Tarkov's locked-back pose
        private static void SetBoltOpen(FirearmsAnimator firearmsAnimator, float open)
        {
            IAnimator animator = firearmsAnimator?.Animator;
            if (animator == null)
                return;
            if (firearmsAnimator.BOLT_CATCH_LAYER_INDEX >= 0)
                animator.SetLayerWeight(firearmsAnimator.BOLT_CATCH_LAYER_INDEX, open);
            if (firearmsAnimator.HasParameter(AnimationControllerParametersTable.FLOAT_IDLEPOSITION))
                AnimationControllerParametersTable.SetIdlePosition(animator, open);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Only a bolt this pull opens: one already locked back (a magazine swapped into an empty gun) has its next round
        // counted in the chamber by Tarkov's insert, but it's still in the magazine until the bolt goes forward. The same
        // round under a bolt that was forward (WaitingForRack) is hidden while it opens.
        private static void BeginChamberView(FirearmController firearmController, Weapon weapon)
        {
            chamberShownEmpty = WaitingForRack(weapon);
            Transform[] chamberBones = firearmController.Firearms._patronInWeaponTransforms;
            chamberBone = chamberBones != null && chamberBones.Length > 0 ? chamberBones[0] : null;
            showingChamber = showChamberedRound && boltFollowsHandle && chamberBone != null
                && !firearmController.FirearmsAnimator.GetBoltCatch() && weapon.ChamberAmmoCount > 0;
            if (!showingChamber)
            {
                chamberBone = null;
                chamberedRound = null;
                return;
            }
            chamberBoneWasActive = chamberBone.gameObject.activeSelf;
            chamberedRound = chamberBone.childCount > 0 ? chamberBone.GetChild(0) : null;
            if (chamberedRound != null)
                chamberedRoundRestLocalPosition = chamberedRound.localPosition;
            boltParts = null;
            boltPart = null;
        }

        // LateUpdate, after the animator has posed the bolt this frame (read in Update it trails a frame behind).
        // The round rides the bolt face; the model itself isn't animated, so its offset stays until the next write.
        // Once the rechamber has thrown it out (its model goes back to Tarkov's pool) the next one is hidden, and so is a
        // round still in the magazine.
        public static void ApplyChamberView()
        {
            if (!showingChamber || chamberBone == null || handleForFirearm == null)
                return;
            if (chamberShownEmpty)
            {
                chamberBone.gameObject.SetActive(false);
                return;
            }
            chamberBone.gameObject.SetActive(true);
            if (chamberedRound == null || chamberedRound.parent != chamberBone)
                return;
            Transform weaponRoot = handleForFirearm.WeaponRoot;
            if (boltParts == null)
                RecordBoltParts(weaponRoot);
            chamberedRound.localPosition = chamberedRoundRestLocalPosition + BoltMovementInChamber(weaponRoot.up);
        }

        // Every part of the gun, and where it sits in the chamber bone's frame with the bolt closed (the grab's first
        // frame). The chamber frame, not the gun's root: the receiver sways against the root with the idle breathing and
        // the chamber sways with it, so only the bolt's own travel is left (measured against the root, the round bobbed).
        // Left out: the handle and the round (we move those) and the gun's own arm and camera bones.
        private static void RecordBoltParts(Transform weaponRoot)
        {
            Transform[] parts = weaponRoot.GetComponentsInChildren<Transform>();
            int kept = 0;
            for (int index = 0; index < parts.Length; index++)
            {
                Transform part = parts[index];
                if (part == weaponRoot || part.IsChildOf(handleView) || part.IsChildOf(chamberBone) || part.name.Contains("Human") || part.name.Contains("Camera"))
                    continue;
                parts[kept++] = part;
            }
            boltParts = new Transform[kept];
            boltPartsRestInChamber = new Vector3[kept];
            for (int index = 0; index < kept; index++)
            {
                boltParts[index] = parts[index];
                boltPartsRestInChamber[index] = chamberBone.InverseTransformPoint(parts[index].position);
            }
        }

        // The bolt = the part that has come furthest back along the barrel (the locked-back pose moves the whole bolt
        // group together); followed on its own once it's clearly moving. In the chamber bone's frame, = the round's.
        private static Vector3 BoltMovementInChamber(Vector3 backWorld)
        {
            if (boltPart == null)
            {
                float furthestBack = 0f;
                int furthestIndex = -1;
                for (int index = 0; index < boltParts.Length; index++)
                {
                    if (boltParts[index] == null)
                        continue;
                    Vector3 move = chamberBone.InverseTransformPoint(boltParts[index].position) - boltPartsRestInChamber[index];
                    float back = Vector3.Dot(chamberBone.TransformVector(move), backWorld);
                    if (back > furthestBack)
                    {
                        furthestBack = back;
                        furthestIndex = index;
                    }
                }
                if (furthestIndex < 0)
                    return Vector3.zero;
                Vector3 furthestMove = chamberBone.InverseTransformPoint(boltParts[furthestIndex].position) - boltPartsRestInChamber[furthestIndex];
                if (furthestBack < boltLockOnDistance || handlePull <= 0f)
                    return furthestMove;
                boltPart = boltParts[furthestIndex];
                boltPartRestInChamber = boltPartsRestInChamber[furthestIndex];
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[VRReload] the round rides '{boltPart.name}' (parent '{(boltPart.parent != null ? boltPart.parent.name : "-")}')");
            }
            return chamberBone.InverseTransformPoint(boltPart.position) - boltPartRestInChamber;
        }

        private static void EndChamberView()
        {
            if (showingChamber && chamberBone != null)
            {
                chamberBone.gameObject.SetActive(chamberBoneWasActive);
                if (chamberedRound != null && chamberedRound.parent == chamberBone)
                    chamberedRound.localPosition = chamberedRoundRestLocalPosition;
            }
            showingChamber = false;
            chamberBone = null;
            chamberedRound = null;
            boltParts = null;
            boltPart = null;
        }

        // The bolt goes where the gun's own catch flag says: forward, or held back by an empty magazine
        private static void RestoreBoltPose(FirearmController firearmController)
        {
            FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
            if (firearmsAnimator != null)
                SetBoltOpen(firearmsAnimator, firearmsAnimator.GetBoltCatch() ? 1f : 0f);
        }

        // Let go from a full pull: a bolt locked back closes (unless an empty magazine holds it), and a round still in the
        // magazine goes into the chamber. With no magazine in there's nothing to strip: it keeps waiting.
        private static void BoltSlamsForward(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            // Held by an empty magazine the bolt only comes onto the catch: no slam
            bool heldOnCatch = firearmController.FirearmsAnimator.GetBoltCatch() && BoltStaysBack(weapon);
            if (!heldOnCatch)
                PlayGunSound(firearmController, "BoltIn");
            PlayGunSound(firearmController, "HandleIn");
            Pulse(heldOnCatch ? 0.4f : 0.8f, 0.06f);
            if (WaitingForRack(weapon) && weapon.GetCurrentMagazine() != null)
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo("[VRReload] racked: the round goes into the chamber");
                weaponWaitingForRack = null;
            }
            if (firearmController.FirearmsAnimator.GetBoltCatch())
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[VRReload] bolt released, chamber: {weapon.ChamberAmmoCount}");
                firearmController.FirearmsAnimator.SetBoltCatch(BoltStaysBack(weapon));
            }
        }

        // An empty magazine catches the bolt (the follower pushes the catch up); no magazine, or rounds left, it closes.
        // Same rule as Tarkov's RechamberOperation.FastForward.
        private static bool BoltStaysBack(Weapon weapon)
        {
            Magazine magazine = weapon.GetCurrentMagazine();
            return weapon.IsBoltCatch && weapon.ChamberAmmoCount == 0 && magazine != null && magazine.Count == 0;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // The grab point (green when the palm is in reach)
        private static void UpdateChargingHandleViz(FirearmController firearmController, Weapon weapon, Vector3 palm, bool show)
        {
            ContainerCollectionView.SlotView slotView = null;
            bool hasHandle = show && chargingHandle && TryGetChargingHandle(firearmController, weapon, out slotView);
            Vector3 grabPoint = hasHandle ? HandleGrabPoint(slotView.ItemView, firearmController.WeaponRoot.up) : Vector3.zero;
            bool inReach = hasHandle && (palm - grabPoint).sqrMagnitude <= handleGrabRadius * handleGrabRadius;
            SetZoneViz(ref handleZoneViz, hasHandle && !HoldingChargingHandle, grabPoint, handleGrabRadius,
                inReach ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.4f, 0f, 0.3f));
        }
    }
}
