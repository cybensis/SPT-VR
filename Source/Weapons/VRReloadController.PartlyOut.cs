using Diz.LanguageExtensions;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // A pistol's magazine sits inside the grip, so a hand can't take hold of it seated. With Manual Reload the release lets it
    // drop onto the hand under the well: it slides out a little and rests there, and from there the hand can grab it (grip, or
    // the trigger with a new magazine in the hand, for palming) and push it back in or pull it out. Take the hand away first
    // and it falls. No hand under the well: the release drops it straight away, as on any gun.
    public static partial class VRReloadController
    {
        public static float partlyOutDistance = 0.045f;     // m the magazine slides out onto the hand (headset: at 0.03 the hands were too close to take it)
        public static float partlyOutPullLeft = 0.02f;      // m from there to it coming free in the hand
        public static float partlyOutSpeed = 0.4f;          // m/s
        public static float belowWellDepth = 0.10f;         // m below the magazine that counts as the hand under it
        public static float belowWellRadius = 0.08f;        // m
        public static float partlyOutHandAwayTime = 0.2f;   // s the hand can be away before it falls, so a shaky hand doesn't drop it

        private static FirearmController partlyOutFor;
        private static Slot partlyOutSlot;
        private static Transform partlyOutView;
        private static Vector3 partlyOutRestLocalPosition;
        private static Vector3 partlyOutAxisInParent;
        private static float partlyOutPosition;
        private static float partlyOutTarget;
        private static float partlyOutHandLeftTime;
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

        // The release went down. False = this doesn't apply, it drops as usual.
        private static bool TrySlideMagazineOntoHand()
        {
            if (MagazinePartlyOut)
                return true;
            if (!MagazineSlidesOntoHand || !TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None
                || ChestHang.Detached || weapon.MalfState.State == Weapon.EMalfunctionState.Feed || weapon.GetCurrentMagazine() == null)
                return false;
            if (!OffHandUnderMagazine(firearmController, magazineSlot, onItToo: false))
                return false;
            if (!StartPartlyOut(firearmController, magazineSlot, 0f, partlyOutDistance))
                return false;
            PlayGunSound(firearmController, "MagOut");
            Pulse(0.3f, 0.04f);
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
            partlyOutSlot = magazineSlot;
            partlyOutView = magazineView.ItemView;
            partlyOutRestLocalPosition = partlyOutView.localPosition;
            partlyOutAxisInParent = partlyOutView.parent.InverseTransformDirection(downTheMagazine.normalized);
            partlyOutPosition = position;
            partlyOutTarget = target;
            partlyOutHandLeftTime = -1f;
            partlyOutView.localPosition = partlyOutRestLocalPosition + partlyOutAxisInParent * partlyOutPosition;
            return true;
        }

        // Every frame while it's resting on the hand
        private static void UpdateMagazinePartlyOut()
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

            if (OffHandUnderMagazine(firearmController, partlyOutSlot, onItToo: true))
            {
                partlyOutHandLeftTime = -1f;
                return;
            }
            if (partlyOutHandLeftTime < 0f)
                partlyOutHandLeftTime = Time.time;
            else if (Time.time - partlyOutHandLeftTime >= partlyOutHandAwayTime)
                MagazineFallsOut();
        }

        // Nothing under it any more
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
            partlyOutSlot = null;
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

        // Let go of it part way, still out: it rests on the hand again (and falls if the hand isn't there)
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
            PlayGunSound(firearmController, "MagIn");
            Pulse(0.6f, 0.06f);
            CancelHold();
            if (!gripReleased && !palming)
                BeginSeatedHold(firearmController, well);
        }

        private static void UpdateBelowWellViz(FirearmController firearmController, Slot magazineSlot)
        {
            if (!MagazineSlidesOntoHand || HoldingGunMagazine
                || !TryGetBelowWellCapsule(firearmController, magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom, out Vector3 zoneEnd))
            {
                SetZoneViz(ref belowWellZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }
            Vector3 zoneStart = MagazinePartlyOut ? magazineCenter : magazineBottom;
            bool inReach = OffHandTouchesCapsule(zoneStart, zoneEnd, belowWellRadius);
            SetCapsuleZoneViz(ref belowWellZoneViz, zoneStart, zoneEnd, belowWellRadius, inReach ? new Color(0f, 1f, 0f, 0.25f) : new Color(1f, 0f, 1f, 0.25f));
        }
    }
}
