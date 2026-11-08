using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // A press check on a pistol, one-handed (his ask 2026-11-07, a picture of John Wick's). The grip let go with the thumb still on
    // A/B/the stick and the finger on the trigger (OpenXR touch sensors) = the gun hand comes off the grip and pinches the back of
    // the slide; the trigger then draws the slide back, as far as the trigger goes, up to just far enough to see the round.
    //
    // The pinch is Tarkov's own chamber check (<gun>_check_chamber, gen_extras: the other hand pinches the slide over the top while
    // the gun hand holds the grip), mirrored onto the gun hand, at the slide's place in the clip. The hand is put there through
    // EFT's own right marker (_markers[1] = a transform of ours next to the gun's weapon_R_IK_marker): EFT's arm IK solves the hand
    // onto it, and the gun's anchor (GunInteractionController.UpdateWeaponPosition) lands that marker on your hand - so the gun
    // drops into the pinch at your hand, and as the slide comes back with the fingers the gun body goes forward under them, as a
    // real press check works (the fingers hold the slide, the hand pushes the frame).
    public static partial class VRReloadController
    {
        public static bool pressCheck = true;
        public static float pressCheckBlendTime = 0.15f;    // s the hand takes onto the slide and back to the grip
        public static float pressCheckTravelScale = 1f;     // the slide's travel: x the gun's own check distance (Tarkov's clip: 1.35-3.3 cm)
        public static float pressCheckSlideSpeed = 0.4f;    // m/s the slide can follow the trigger at
        public static Vector3 pressCheckHandOffset = Vector3.zero;   // m the pinching hand moved, in the slide's frame (live tuning)
        public static Vector3 pressCheckHandTurn = Vector3.zero;     // deg the pinching hand turned, in its own frame
        public static bool pressCheckRoundRides = true;     // the chambered round comes back with the slide (the extractor has it)
        public static bool pressCheckDebug = false;

        private sealed class PressCheckData
        {
            public string slideBone;
            public bool rightHand;          // recorded on the right hand; false = the left's, mirrored onto the gun hand
            public HandleHold hold;         // the palm in the slide bone's frame, fingers
            public float travel;            // m the slide comes back in the clip
        }
        private static readonly Dictionary<string, PressCheckData> pressChecks = new Dictionary<string, PressCheckData>();

        private static FirearmController pressCheckFor;
        private static PressCheckData pressCheckData;
        private static HandleHold pressCheckHold;           // on the gun hand (mirrored when the clip's is the left)
        private static Transform pressCheckSlide;
        private static Vector3 pressCheckSlideRest;
        private static Vector3 pressCheckSlideAxis;         // one metre back, in the slide's parent's frame
        private static Transform pressCheckGunMarker;       // the gun's own weapon_R_IK_marker
        private static Transform pressCheckMarker;          // ours, next to it: EFT's _markers[1] while the hand is on the slide
        private static Transform pressCheckChamber;
        private static Transform pressCheckRound;
        private static Vector3 pressCheckRoundRest;
        private static bool pressCheckChamberWasActive;
        private static float pressCheckShown;               // 0 = the hand on the grip, 1 = on the slide
        private static float pressCheckPull;                // m the slide is back
        private static bool pressCheckPulsedBack;
        private static bool pressCheckTriggerLock;          // ended with the trigger down: no shot until it's let go
        private static FirearmController pressCheckDataFor;
        private static PressCheckData pressCheckDataCached;

        // The hand off the grip (or easing back): no firing, the trigger is the slide's
        public static bool PressCheckActive => pressCheckFor != null;
        private static bool PressCheckBlocksFiring => pressCheckFor != null || pressCheckTriggerLock;

        // The gesture alone: the gun hand's thumb on A/B/the stick and its finger on the trigger, holding a pistol with a chamber
        // check. ChestHang counts it as the grip still held (HandsInteractionController): the gun stays in the hand with the grip let go.
        public static bool PressCheckGesture
        {
            get
            {
                if (!pressCheck || !enabled || Handedness.GunInLeftHand || Handedness.LeftHanded)
                    return false;
                if (!Handedness.TryGetGunHandThumbTouch(out bool thumbOn) || !thumbOn)
                    return false;
                if (!Handedness.GunHandTrigger.TryGetTouch(out bool fingerOn) || !fingerOn)
                    return false;
                return PressCheckDataOf(VRGlobals.firearmController) != null;
            }
        }

        private static PressCheckData PressCheckDataOf(FirearmController firearmController)
        {
            if (firearmController == null || firearmController.Item == null)
                return null;
            if (pressCheckDataFor != firearmController)
            {
                pressCheckDataFor = firearmController;
                LoadRecordedGrips();
                pressChecks.TryGetValue(AnimationFamily(firearmController), out pressCheckDataCached);
            }
            return pressCheckDataCached;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // From Tick

        private static void UpdatePressCheck(bool gunHandGrip)
        {
            if (pressCheckTriggerLock && Handedness.GunHandTrigger.axis < 0.1f)
                pressCheckTriggerLock = false;

            bool wanted = WantsPressCheck(gunHandGrip, out FirearmController firearmController, out Weapon weapon, out PressCheckData data);
            // Another gun, or this one's state gone: straight back
            if (pressCheckFor != null && (pressCheckFor != VRGlobals.firearmController || !ReferenceEquals(VRGlobals.player != null ? VRGlobals.player.HandsController : null, pressCheckFor)))
            {
                EndPressCheck();
                return;
            }
            if (wanted && pressCheckFor == null && !BeginPressCheck(firearmController, weapon, data))
                return;
            if (pressCheckFor == null)
                return;

            float trigger = Handedness.GunHandTrigger.axis;
            pressCheckShown = Mathf.MoveTowards(pressCheckShown, wanted ? 1f : 0f, Time.deltaTime / Mathf.Max(pressCheckBlendTime, 0.001f));
            // The slide only once the fingers are on it
            float travel = pressCheckData.travel * pressCheckTravelScale;
            float pullTarget = wanted && pressCheckShown >= 1f ? Mathf.Clamp01(trigger) * travel : 0f;
            pressCheckPull = Mathf.MoveTowards(pressCheckPull, pullTarget, Time.deltaTime * pressCheckSlideSpeed);
            if (!pressCheckPulsedBack && travel > 0f && pressCheckPull >= travel * 0.95f)
            {
                pressCheckPulsedBack = true;
                PulseGunHand(0.3f, 0.03f);
            }
            else if (pressCheckPulsedBack && pressCheckPull < travel * 0.5f)
                pressCheckPulsedBack = false;

            PlacePressCheckMarker();
            if (!wanted && pressCheckShown <= 0f && pressCheckPull <= 0f)
            {
                // Back on the grip with the trigger still down (a grip taken mid-check): that pull isn't a shot
                pressCheckTriggerLock = trigger > 0.2f;
                EndPressCheck();
            }
        }

        private static bool WantsPressCheck(bool gunHandGrip, out FirearmController firearmController, out Weapon weapon, out PressCheckData data)
        {
            firearmController = null;
            weapon = null;
            data = null;
            if (gunHandGrip || VRGlobals.menuOpen || ChestHang.Detached || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.isSupporting || !PressCheckGesture)
                return false;
            if (!CanWorkGun(out firearmController, out weapon, out _))
                return false;
            // The hands doing something else with the gun, a malfunction, or the slide locked back already
            if (HoldingMagazine || HoldingChargingHandle || HoldingSeatedMagazine || HoldingPalmedMagazine || HoldingBolt || HoldingRound || HoldingJamCase
                || HoldingDeflector || MagazinePartlyOut || weapon.MalfState.State != Weapon.EMalfunctionState.None || firearmController.FirearmsAnimator.GetBoltCatch())
                return false;
            data = PressCheckDataOf(firearmController);
            return data != null;
        }

        private static bool BeginPressCheck(FirearmController firearmController, Weapon weapon, PressCheckData data)
        {
            Transform slide = FindRigBone(firearmController, data.slideBone);
            Transform gunMarker = firearmController.HandsHierarchy != null ? firearmController.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon_R_IK_marker) : null;
            if (slide == null || slide.parent == null || gunMarker == null || gunMarker.parent == null)
                return false;
            HandleHold hold = data.hold;
            if (!data.rightHand)
            {
                // The left hand's pinch onto the gun hand: its mirror image through the gun's centre plane, in the slide's frame
                if (!TarkovVR.Patches.Core.Player.RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                    return false;
                Quaternion toSlide = Quaternion.Inverse(slide.rotation);
                hold = MirroredHandleHold(data.hold, (toSlide * planeNormal).normalized, toSlide * (planePoint - slide.position));
                if (hold == null)
                    return false;
            }
            pressCheckFor = firearmController;
            pressCheckData = data;
            pressCheckHold = hold;
            pressCheckSlide = slide;
            pressCheckSlideRest = slide.localPosition;
            pressCheckSlideAxis = slide.parent.InverseTransformVector(firearmController.WeaponRoot.up);
            pressCheckGunMarker = gunMarker;
            if (pressCheckMarker == null)
                pressCheckMarker = new GameObject("VRPressCheckHand").transform;
            pressCheckMarker.SetParent(gunMarker.parent, worldPositionStays: false);
            pressCheckMarker.localPosition = gunMarker.localPosition;
            pressCheckMarker.localRotation = gunMarker.localRotation;
            pressCheckShown = 0f;
            pressCheckPull = 0f;
            pressCheckPulsedBack = false;

            // The chambered round, to see once the slide is back (none = an empty chamber, which is what you'd see)
            Transform[] chamberBones = firearmController.Firearms._patronInWeaponTransforms;
            pressCheckChamber = chamberBones != null && chamberBones.Length > 0 && weapon.ChamberAmmoCount > 0 && !WaitingForRack(weapon) ? chamberBones[0] : null;
            pressCheckRound = null;
            if (pressCheckChamber != null)
            {
                pressCheckChamberWasActive = pressCheckChamber.gameObject.activeSelf;
                pressCheckRound = pressCheckChamber.childCount > 0 ? pressCheckChamber.GetChild(0) : null;
                if (pressCheckRound != null)
                    pressCheckRoundRest = pressCheckRound.localPosition;
            }
            PulseGunHand(0.15f, 0.02f);
            if (pressCheckDebug)
                Plugin.MyLog.LogInfo($"[PressCheck] start on '{AnimationFamily(firearmController)}': slide '{slide.name}', travel {data.travel * 100f:0.00} cm, " +
                    $"{(data.rightHand ? "right-hand" : "mirrored left-hand")} pinch, round shown: {pressCheckRound != null}");
            return true;
        }

        // Our marker between the gun's grip marker (0) and the pinch on the slide as it is now (1); EFT's right marker while it's off the grip
        private static void PlacePressCheckMarker()
        {
            if (pressCheckMarker == null || pressCheckGunMarker == null || pressCheckSlide == null)
                return;
            Transform frame = pressCheckMarker.parent;
            Transform slideParent = pressCheckSlide.parent;
            Vector3 slideAt = slideParent.TransformPoint(pressCheckSlideRest + pressCheckSlideAxis * pressCheckPull);
            Quaternion slideRotation = pressCheckSlide.rotation;
            Vector3 palm = slideAt + slideRotation * (pressCheckHold.position + pressCheckHandOffset);
            Quaternion palmRotation = slideRotation * Quaternion.Euler(pressCheckHold.rotation) * Quaternion.Euler(pressCheckHandTurn);
            float blend = Mathf.SmoothStep(0f, 1f, pressCheckShown);
            pressCheckMarker.localPosition = Vector3.Lerp(pressCheckGunMarker.localPosition, frame.InverseTransformPoint(palm), blend);
            pressCheckMarker.localRotation = Quaternion.Slerp(pressCheckGunMarker.localRotation, Quaternion.Inverse(frame.rotation) * palmRotation, blend);

            EFT.Player player = VRGlobals.player;
            if (player != null && player._markers.Length > 1 && player._markers[1] != pressCheckMarker)
            {
                // Only over the gun's own marker (a weapon change points it at the hand: that's not ours to take)
                if (player._markers[1] == pressCheckGunMarker)
                    player._markers[1] = pressCheckMarker;
            }
        }

        private static void EndPressCheck()
        {
            EFT.Player player = VRGlobals.player;
            if (player != null && player._markers.Length > 1 && pressCheckMarker != null && player._markers[1] == pressCheckMarker)
                player._markers[1] = pressCheckGunMarker;
            if (pressCheckSlide != null)
                pressCheckSlide.localPosition = pressCheckSlideRest;
            if (pressCheckChamber != null)
            {
                pressCheckChamber.gameObject.SetActive(pressCheckChamberWasActive);
                if (pressCheckRound != null && pressCheckRound.parent == pressCheckChamber)
                    pressCheckRound.localPosition = pressCheckRoundRest;
            }
            if (pressCheckMarker != null)
                pressCheckMarker.SetParent(null, worldPositionStays: false);
            if (pressCheckDebug && pressCheckFor != null)
                Plugin.MyLog.LogInfo("[PressCheck] end");
            pressCheckFor = null;
            pressCheckData = null;
            pressCheckHold = null;
            pressCheckSlide = null;
            pressCheckGunMarker = null;
            pressCheckChamber = null;
            pressCheckRound = null;
            pressCheckShown = 0f;
            pressCheckPull = 0f;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // LateUpdate, after the animator

        // The slide drawn back, and the round with it once there's a gap to see it through
        public static void ApplyPressCheck()
        {
            if (pressCheckFor == null || pressCheckSlide == null)
                return;
            pressCheckSlide.localPosition = pressCheckSlideRest + pressCheckSlideAxis * pressCheckPull;
            if (pressCheckChamber == null)
                return;
            bool open = pressCheckPull > 0.002f;
            pressCheckChamber.gameObject.SetActive(open || pressCheckChamberWasActive);
            if (pressCheckRound != null && pressCheckRound.parent == pressCheckChamber)
            {
                Vector3 ride = pressCheckRoundRides ? pressCheckChamber.InverseTransformVector(pressCheckFor.WeaponRoot.up * pressCheckPull) : Vector3.zero;
                pressCheckRound.localPosition = pressCheckRoundRest + ride;
            }
        }

        // The gun hand's fingers in the pinch, after its own finger update (from ApplyGunHandFingerPose)
        private static Transform pressCheckPalm;
        private static Transform[] pressCheckFingerBones;

        private static void ApplyPressCheckFingers()
        {
            if (pressCheckFor == null || pressCheckHold == null || pressCheckHold.fingers == null || pressCheckShown <= 0f)
                return;
            Transform palm = ArmPalm(rightArm: true);
            if (palm == null)
                return;
            if (pressCheckFingerBones == null || pressCheckPalm != palm)
            {
                pressCheckPalm = palm;
                pressCheckFingerBones = FingerBonesInPoseOrder(palm);
            }
            float weight = Mathf.SmoothStep(0f, 1f, pressCheckShown);
            int boneCount = Mathf.Min(pressCheckFingerBones.Length, pressCheckHold.fingers.Length);
            for (int index = 1; index < boneCount; index++)
            {
                if (pressCheckFingerBones[index] != null)
                    pressCheckFingerBones[index].localRotation = Quaternion.Slerp(pressCheckFingerBones[index].localRotation, Quaternion.Euler(pressCheckHold.fingers[index]), weight);
            }
        }
    }
}
