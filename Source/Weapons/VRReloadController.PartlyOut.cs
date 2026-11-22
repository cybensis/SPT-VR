using Diz.LanguageExtensions;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // A pistol's magazine sits inside the grip, so a hand can't take hold of it while seated. With Manual Reload, holding the release
    // (B) slides it partly out of the well and keeps it there. From there the off hand can grab it (with the grip, or with the trigger
    // while holding a new magazine, for palming) and push it back in or pull it out. Letting go of the release while it is partly out
    // drops it, so a tap just drops it.
    public static partial class VRReloadController
    {
        private static FirearmController partlyOutFor;
        private static Transform partlyOutView;

        public static bool MagazinePartlyOut => partlyOutFor != null;

        private static bool MagazineSlidesOntoHand => VRSettings.GetManualReloadOn() && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isWeapPistol;

        // onItToo: the magazine itself counts too, not only the zone below it (once it rests on the hand)
        private static bool OffHandUnderMagazine(FirearmController firearmController, Slot magazineSlot, bool onItToo)
        {
            if (!PartlyOut.TryGetBelowWellCapsule(firearmController, magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom, out Vector3 zoneEnd))
                return false;
            return OffHandTouchesCapsule(onItToo ? magazineCenter : magazineBottom, zoneEnd, PartlyOut.belowWellRadius);
        }

        public static class PartlyOut
        {
            public static float partlyOutDistance = 0.045f;     // m the magazine slides out (at 0.03 the hands are too close to take it)
            public static float partlyOutPullLeft = 0.02f;      // m of further pull until it comes free in the hand
            public static float partlyOutSpeed = 0.4f;          // m/s
            public static float belowWellDepth = 0.10f;         // m below the magazine where the hand can take it while partly out
            public static float belowWellRadius = 0.08f;        // m
            private static Vector3 partlyOutRestLocalPosition;
            private static Vector3 partlyOutAxisInParent;
            private static float partlyOutPosition;
            private static float partlyOutTarget;
            internal static bool railStartedPartlyOut;           // the hand took it while partly out, so pushing it back up seats it
            internal static GameObject belowWellZoneViz;

            internal static bool TryGetBelowWellCapsule(FirearmController firearmController, Slot magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom, out Vector3 zoneEnd)
            {
                zoneEnd = Vector3.zero;
                if (!TryGetGunMagazineCapsule(firearmController, magazineSlot, out magazineCenter, out magazineBottom))
                    return false;
                Vector3 downTheMagazine = magazineBottom - magazineCenter;
                zoneEnd = magazineBottom + (downTheMagazine.sqrMagnitude > 1e-6f ? downTheMagazine.normalized : Vector3.down) * belowWellDepth;
                return true;
            }

            // Called when the release is pressed: a pistol's magazine slides partly out, whether or not a hand is under the well.
            // Returns false when this doesn't apply and the magazine drops as usual.
            internal static bool TrySlideMagazinePartlyOut()
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

            // Runs every frame while the magazine is partly out; it stays out while the release is held (releaseHeld = Tick's MagazineReleaseHeld)
            internal static void UpdateMagazinePartlyOut(bool releaseHeld)
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

            // The release was let go while the magazine is partly out
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

            // The hand grabs it: the model goes back to seated, and the returned distance tells GrabFromGun where on the track to start
            internal static float TakePartlyOutMagazine()
            {
                if (!MagazinePartlyOut)
                    return 0f;
                float position = partlyOutPosition;
                PutMagazineBackUp();
                return position;
            }

            // The hand let go of it part way out: it goes back to partly out (and falls straight away unless the release is still held)
            internal static void ReturnToPartlyOut()
            {
                FirearmController firearmController = heldForFirearm;
                Slot magazineSlot = firearmController.Item.GetMagazineSlot();
                float position = railPosition;
                CancelHold();
                StartPartlyOut(firearmController, magazineSlot, position, Mathf.Min(position, partlyOutDistance));
            }

            // Pushed back up the track from partly out: it seats and the catch holds it again
            internal static void PushedBackIn(bool gripReleased)
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

            internal static void UpdateBelowWellViz(FirearmController firearmController, Slot magazineSlot)
            {
                // Shown only while partly out, the only time the hand can take it there
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
}
