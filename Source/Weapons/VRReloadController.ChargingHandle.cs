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
    //    chambers on its own.
    // The bolt lock is Tarkov's bolt-catch animator flag. Tarkov clears it in its own reload; while it's on the gun
    // doesn't fire (BlocksFiring).
    public static partial class VRReloadController
    {
        public static bool chargingHandle = true;
        public static float handleGrabRadius = 0.06f;      // m palm to the back of the charging handle to grab it
        public static float handleTravel = 0.08f;          // m the handle comes back (headset: 0.07 a touch short)
        public static float handleFullPull = 0.9f;         // share of handleTravel that counts as all the way back
        public static float handleLateralEscape = 0.10f;   // m the controller moves sideways off the track before the hand lets go

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

        // The handle's slot, looked up again now and then (mods only change in the inventory)
        private static FirearmController handleSlotFor;
        private static ContainerCollectionView.SlotView handleSlotView;
        private static float handleSlotLookupTime;
        private static Transform handleMeshesFor;
        private static MeshFilter[] handleMeshes;
        private static GameObject handleZoneViz;

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

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRReload] grabbed the charging handle '{handleView.name}', bolt locked back: {BoltLockedBack}, chamber: {weapon.ChamberAmmoCount}");
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
            if (firearmController.FirearmsAnimator.GetBoltCatch() || weapon.ChamberAmmoCount == 0)
                return;
            if (!(firearmController.CurrentOperation is FirearmController.Idling))
                return;
            if (debugLog)
                Plugin.MyLog.LogInfo("[VRReload] rechamber: the chambered round comes out");
            ExpectOperation(ExpectedOperation.Rechamber, firearmController);
            VRGlobals.player.InventoryController.RechamberWeapon(weapon);
            TryForceExpectedOperation();
        }

        private static void LetGoOfChargingHandle(bool snapForward)
        {
            // The hand first, where it is, so it doesn't snap forward with the handle
            ReleaseHandLatch();
            if (handleView != null)
                handleView.localPosition = handleRestLocalPosition;
            HoldingChargingHandle = false;
            FirearmController firearmController = handleForFirearm;
            handleForFirearm = null;
            handleBone = null;
            handleView = null;

            if (firearmController == null || firearmController != VRGlobals.firearmController)
                return;
            if (snapForward)
                BoltSlamsForward(firearmController);
            else
                PlayGunSound(firearmController, "HandleIn");
        }

        // Let go from a full pull: a bolt locked back closes (unless an empty magazine holds it)
        private static void BoltSlamsForward(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            PlayGunSound(firearmController, "BoltIn");
            PlayGunSound(firearmController, "HandleIn");
            Pulse(0.8f, 0.06f);
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
