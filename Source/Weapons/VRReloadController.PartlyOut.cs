using Diz.LanguageExtensions;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // A pistol's magazine sits inside the grip, so a hand can't take hold of it seated. With Manual Reload, holding the release
    // (B) slides it partly out of the well and keeps it there, hand or no hand ("if you hold B it makes the mag come out
    // partially"); from there the off hand can grab it (grip, or the trigger with a new magazine in the hand, for palming) and push
    // it back in or pull it out. The release let go while it's partly out: it falls ("if you let go while it's partially out, it'll
    // fall to the floor") - so a tap just drops it.
    public static partial class VRReloadController
    {
        public static float partlyOutDistance = 0.045f;     // m the magazine slides out (headset: at 0.03 the hands were too close to take it)
        public static float partlyOutPullLeft = 0.02f;      // m from there to it coming free in the hand
        public static float partlyOutSpeed = 0.4f;          // m/s
        public static float belowWellDepth = 0.10f;         // m below the magazine where the hand takes it partly out
        public static float belowWellRadius = 0.08f;        // m

        private static FirearmController partlyOutFor;
        private static Transform partlyOutView;
        private static Vector3 partlyOutRestLocalPosition;
        private static Vector3 partlyOutAxisInParent;
        private static float partlyOutPosition;
        private static float partlyOutTarget;
        private static bool railStartedPartlyOut;           // the hand took it partly out: pushing it back up seats it
        private static GameObject belowWellZoneViz;

        public static bool MagazinePartlyOut => partlyOutFor != null;

        private static bool MagazineSlidesOntoHand => VRSettings.GetManualReloadOn() && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isWeapPistol;

        // onItToo: the magazine itself counts as well as below it, once it's resting on the hand
        private static bool OffHandUnderMagazine(FirearmController firearmController, Slot magazineSlot, bool onItToo)
        {
            if (!TryGetBelowWellCapsule(firearmController, magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom, out Vector3 zoneEnd))
                return false;
            return OffHandTouchesCapsule(onItToo ? magazineCenter : magazineBottom, zoneEnd, belowWellRadius);
        }

        private static bool TryGetBelowWellCapsule(FirearmController firearmController, Slot magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom, out Vector3 zoneEnd)
        {
            zoneEnd = Vector3.zero;
            if (!TryGetGunMagazineCapsule(firearmController, magazineSlot, out magazineCenter, out magazineBottom))
                return false;
            Vector3 downTheMagazine = magazineBottom - magazineCenter;
            zoneEnd = magazineBottom + (downTheMagazine.sqrMagnitude > 1e-6f ? downTheMagazine.normalized : Vector3.down) * belowWellDepth;
            return true;
        }

        // The release went down: a pistol's magazine slides partly out, the hand under the well or not. False = this doesn't apply,
        // it drops as usual.
        private static bool TrySlideMagazinePartlyOut()
        {
            if (MagazinePartlyOut)
                return true;
            if (!MagazineSlidesOntoHand || !TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
                return false;
            if (!GunIdle(firearmController)
                || ChestHang.Detached || weapon.MalfState.State == Weapon.EMalfunctionState.Feed || weapon.GetCurrentMagazine() == null)
                return false;
            if (!StartPartlyOut(firearmController, magazineSlot, 0f, partlyOutDistance))
                return false;
            PlayFirstGunSound(firearmController, SoundHand.GunBody, MAG_OUT_SOUNDS);
            Pulse(SoundHand.GunBody, 0.3f, 0.04f);
            return true;
        }

        private static bool StartPartlyOut(FirearmController firearmController, Slot magazineSlot, float position, float target)
        {
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazineView == null || magazineView.ItemView == null || magazineView.ItemView.parent == null)
                return false;
            if (!TryGetGunMagazineCapsule(firearmController, magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom))
                return false;
            Vector3 downTheMagazine = magazineBottom - magazineCenter;
            if (downTheMagazine.sqrMagnitude < 1e-6f)
                return false;

            partlyOutFor = firearmController;
            partlyOutView = magazineView.ItemView;
            partlyOutRestLocalPosition = partlyOutView.localPosition;
            LoadRecordedGrips();
            partlyOutAxisInParent = wellAxes.TryGetValue(AnimationFamily(firearmController), out Vector3 wellAxis) ? wellAxis
                : partlyOutView.parent.InverseTransformDirection(downTheMagazine.normalized);
            partlyOutPosition = position;
            partlyOutTarget = target;
            partlyOutView.localPosition = partlyOutRestLocalPosition + partlyOutAxisInParent * partlyOutPosition;
            return true;
        }

        // Every frame while it's partly out: it stays while the release is held (releaseHeld: Tick's MagazineReleaseHeld)
        private static void UpdateMagazinePartlyOut(bool releaseHeld)
        {
            if (!MagazinePartlyOut)
                return;
            FirearmController firearmController = partlyOutFor;
            bool valid = enabled && VRGlobals.inGame && !VRGlobals.menuOpen && VRGlobals.vrPlayer != null && firearmController == VRGlobals.firearmController
                && partlyOutView != null && firearmController.Item != null && firearmController.Item.GetCurrentMagazine() != null
                && firearmController.CurrentOperation is FirearmController.Idling && !ChestHang.Detached;
            if (!valid)
            {
                PutMagazineBackUp();
                return;
            }

            partlyOutPosition = Mathf.MoveTowards(partlyOutPosition, partlyOutTarget, partlyOutSpeed * Time.deltaTime);
            partlyOutView.localPosition = partlyOutRestLocalPosition + partlyOutAxisInParent * partlyOutPosition;

            if (!releaseHeld)
                MagazineFallsOut();
        }

        // The release let go of while it's partly out
        private static void MagazineFallsOut()
        {
            FirearmController firearmController = partlyOutFor;
            Magazine magazine = firearmController.Item.GetCurrentMagazine();
            Vector3 position = partlyOutView.position;
            Quaternion rotation = partlyOutView.rotation;
            PutMagazineBackUp();
            if (CanDropToGround(magazine))
                RunDrop(magazine, firearmController, position, rotation, Vector3.down * 0.5f + VRGlobals.player.Velocity);
            else if (TryBuildMoveToRig(magazine, out OperationResult<MoveResult> rigMove))
                RunMoveToRig(magazine, firearmController, rigMove);
        }

        private static void PutMagazineBackUp()
        {
            if (partlyOutView != null)
                partlyOutView.localPosition = partlyOutRestLocalPosition;
            partlyOutFor = null;
            partlyOutView = null;
        }

        // The hand grabs it: GrabFromGun starts from the seated model, on the track at how far out it is
        private static float TakePartlyOutMagazine()
        {
            if (!MagazinePartlyOut)
                return 0f;
            float position = partlyOutPosition;
            PutMagazineBackUp();
            return position;
        }

        // Let go of it part way, still out: partly out again (and it falls straight away unless the release is still held)
        private static void ReturnToPartlyOut()
        {
            FirearmController firearmController = heldForFirearm;
            Slot magazineSlot = firearmController.Item.GetMagazineSlot();
            float position = railPosition;
            CancelHold();
            StartPartlyOut(firearmController, magazineSlot, position, Mathf.Min(position, partlyOutDistance));
        }

        // Pushed back up the track from partly out: it seats, the catch holds it again
        private static void PushedBackIn(bool gripReleased)
        {
            FirearmController firearmController = heldForFirearm;
            Transform well = magazineWell;
            bool palming = HoldingPalmedMagazine;
            PlayFirstGunSound(firearmController, SoundHand.OffHand.Seating, MAG_IN_SOUNDS);
            Pulse(SoundHand.OffHand.Seating, 0.6f, 0.06f);
            CancelHold();
            if (!gripReleased && !palming)
                BeginSeatedHold(firearmController, well);
        }

        private static void UpdateBelowWellViz(FirearmController firearmController, Slot magazineSlot)
        {
            // Only partly out: that's the only time the hand takes it there
            if (!MagazineSlidesOntoHand || !MagazinePartlyOut || HoldingGunMagazine
                || !TryGetBelowWellCapsule(firearmController, magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom, out Vector3 zoneEnd))
            {
                SetZoneViz(ref belowWellZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }
            bool inReach = OffHandTouchesCapsule(magazineCenter, zoneEnd, belowWellRadius);
            SetCapsuleZoneViz(ref belowWellZoneViz, magazineCenter, zoneEnd, belowWellRadius, inReach ? new Color(0f, 1f, 0f, 0.25f) : new Color(1f, 0f, 1f, 0.25f));
        }
    }
}
