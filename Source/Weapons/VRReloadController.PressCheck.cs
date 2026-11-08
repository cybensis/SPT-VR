using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // A press check on a pistol, one-handed (his ask 2026-11-07). The grip let go with the thumb still on A/B/the stick and the finger
    // on the trigger (OpenXR touch sensors) starts it; the trigger then draws the slide back, as far as the trigger goes, up to just
    // far enough to see the round. Touching the grip again (its value, no squeeze) puts the hand back on it.
    //
    // His design (2026-11-08, "option 1"): the hand stays exactly as it holds the gun - the thumb where it is on the frame - and the
    // GUN turns in it about the thumb's big knuckle until the top of the slide's back comes up under the fingers; the fingers let go
    // of the grip and open as it turns, then come down onto the slide (HandsInteractionController.PressCheckHook). The turn is about
    // the gun's sideways axis, the way that takes the fingers from round the grip to pointing up the slide (pressCheckTurnAngle),
    // muzzle tipping down with your hand still: a real hand ends up turned like that in a press check, so you bend your wrist up to
    // point the gun again. Earlier hand-on-the-slide poses (Tarkov's off-hand pinch mirrored, a hook behind the gun, on its left side,
    // on its right side) were all headset-rejected.
    //
    // How: the hand keeps its grip pose against the gun turned the other way about the knuckle. EFT's right marker (_markers[1]) is a
    // transform of ours next to the gun's weapon_R_IK_marker, put where the hand sits on the turned gun: EFT's arm IK puts the hand on
    // it, and the gun's anchor (GunInteractionController.UpdateWeaponPosition) lands that marker on your hand. The gun's turn itself
    // is a rotation of weaponHolder (ApplyPressCheckGunTurn, from UpdateWeaponPosition before the anchor; absolute from its own rest,
    // so collision's per-frame strip can't stack it). The slide comes back with the fingers: the hand moves back with it on the gun,
    // so the gun body goes forward under it, as a real press check works. Only Tarkov's slide bone and how far the slide comes back in
    // its own chamber check are used from the data (pressChecks in ReloadHolds.json).
    public static partial class VRReloadController
    {
        public static bool pressCheck = true;
        public static float pressCheckBlendTime = 0.3f;     // s the turn takes, both ways
        public static float pressCheckTurnAngle = 90f;      // deg the gun turns in the hand about the thumb's knuckle
        public static int pressCheckPivotJoint = 1;         // the thumb joint it turns about: 0 its base, 1 its big knuckle, 2 its middle
        // deg the hand also rolls about the gun's length, the same knuckle (the thumb stays): the heel of the palm off the grip, the
        // fingers further over the slide (headset: "the wrist needs to be open more, it's going through the grip")
        public static float pressCheckWristOpen = 15f;
        public static float pressCheckFingersCloseFrom = 0.6f;   // share of the turn with the fingers opening; after it they close onto the slide
        public static float pressCheckTravelScale = 1f;     // the slide's travel: x the gun's own check distance (Tarkov's clip: 1.35-3.3 cm)
        public static float pressCheckSlideSpeed = 0.4f;    // m/s the slide can follow the trigger at
        public static float pressCheckGripReturn = 0.6f;    // the grip's value (how far the fingers wrap it, no squeeze) that puts the hand back on it
        public static Vector3 pressCheckHandOffset = Vector3.zero;   // m the gun moved in the hand at the full turn: x right, y back, z up along the gun
        public static Vector3 pressCheckHandTurn = Vector3.zero;     // deg more turn at the full turn, about the knuckle, in the gun's frame
        public static bool pressCheckRoundRides = true;     // the chambered round comes back with the slide (the extractor has it)
        public static bool pressCheckDebug = false;

        private sealed class PressCheckData
        {
            public string slideBone;
            public bool rightHand;          // Tarkov's chamber-check hand (unused: the hand keeps its grip)
            public HandleHold hold;         // its pinch (unused)
            public float travel;            // m the slide comes back in Tarkov's chamber check
        }
        private static readonly Dictionary<string, PressCheckData> pressChecks = new Dictionary<string, PressCheckData>();

        private static FirearmController pressCheckFor;
        private static PressCheckData pressCheckData;
        private static Transform pressCheckSlide;
        private static Vector3 pressCheckSlideRest;
        private static Vector3 pressCheckSlideAxis;         // one metre back, in the slide's parent's frame
        private static Transform pressCheckGunMarker;       // the gun's own weapon_R_IK_marker
        private static Transform pressCheckMarker;          // ours, next to it: EFT's _markers[1] during the press check
        // The grip, at the start, in the frame our marker lives in (the gun marker's parent: it sways and breathes with the gun as EFT
        // animates it, so the hand does too - worked in WeaponRoot's frame it held still while the gun swayed, headset: "the hand
        // doesn't follow eft's inertia/breathing idle animation"): the palm, the knuckle it turns about, the turn's axes there
        private static Vector3 pressCheckPalmInFrame;
        private static Quaternion pressCheckPalmTurnInFrame = Quaternion.identity;
        private static Vector3 pressCheckPivotInFrame;
        private static int pressCheckPivotThumbJoint;       // which thumb joint that is (0..2), and its turn in the frame: it stays there
        private static Quaternion pressCheckThumbInFrame = Quaternion.identity;
        private static Vector3 pressCheckSidewaysInFrame;   // the main turn's axis (fingers from the gun's forward toward its up)
        private static Vector3 pressCheckBackInFrame;       // the gun's length, toward the back (the wrist roll's axis)
        private static Vector3 pressCheckBackMetreInFrame;  // one metre back in the frame's own (scaled) units: the slide pull
        private static Quaternion pressCheckFrameInGun = Quaternion.identity;   // the frame in WeaponRoot's, at the start
        private static Vector3 pressCheckGunMarkerStartPosition;    // the gun marker against the frame at the start: its own animation since
        private static Quaternion pressCheckGunMarkerStartRotation = Quaternion.identity;   // carries onto ours
        private static Vector3 pressCheckPalmInMarker;      // where EFT's IK puts the palm against its marker, in the marker's frame
        private static Quaternion pressCheckPalmTurnInMarker = Quaternion.identity;
        private static Transform pressCheckHolder;          // weaponHolder while it's turned, its own rotation, the gun in it
        private static Quaternion pressCheckHolderRest = Quaternion.identity;
        private static Quaternion pressCheckGunInHolder = Quaternion.identity;
        private static Transform pressCheckChamber;
        private static Transform pressCheckRound;
        private static Vector3 pressCheckRoundRest;
        private static bool pressCheckChamberWasActive;
        private static float pressCheckShown;               // 0 = the grip, 1 = turned, fingers on the slide
        private static float pressCheckPull;                // m the slide is back
        private static bool pressCheckPulsedBack;
        private static bool pressCheckTriggerLock;          // ended with the trigger down: no shot until it's let go
        private static FirearmController pressCheckDataFor;
        private static PressCheckData pressCheckDataCached;

        // The hand off the grip (or easing back): no firing, the trigger is the slide's
        public static bool PressCheckActive => pressCheckFor != null;
        private static bool PressCheckBlocksFiring => pressCheckFor != null || pressCheckTriggerLock;
        // How far into it, 0..1 (HandsInteractionController opens and closes the fingers by it)
        public static float PressCheckProgress => pressCheckFor != null ? pressCheckShown : 0f;

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

        // The gun is held: by the gesture, or, in a press check, by the hand touching the grip again (on its way back onto it: the
        // thumb or finger can come off the controls as the hand closes, which let the gun go - headset, "it seems like I need to
        // squeeze to put my hand back onto the grip")
        public static bool PressCheckHoldsGun => PressCheckGesture || (pressCheckFor != null && GripTouchedAgain());

        private static bool GripTouchedAgain()
        {
            return Handedness.TryGetGunHandGripValue(out float value) && value >= Mathf.Max(pressCheckGripReturn, OpenXRInput.gripReleaseThreshold + 0.05f);
        }

        // The sling taking the gun mid press check (its release delay is shorter than the turn back): the marker and the holder back
        // first. Else it saved OUR marker as the gun's and gave it back on the next grab (headset: "the next time I go to grab the
        // pistol from its slot, my hand flies away"), and would save the turned holder the same way.
        public static void CancelPressCheck()
        {
            if (pressCheckFor != null)
                EndPressCheck();
        }

        internal static bool IsPressCheckMarker(Transform marker) => marker != null && marker == pressCheckMarker;

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

            // Another gun, this one's state gone, or Tarkov animating it (a reload started): straight back
            if (pressCheckFor != null && (pressCheckFor != VRGlobals.firearmController || !ReferenceEquals(VRGlobals.player != null ? VRGlobals.player.HandsController : null, pressCheckFor)
                || !(pressCheckFor.CurrentOperation is FirearmController.Idling)))
            {
                EndPressCheck();
                return;
            }
            // The hand closing on the grip again: held from here as if squeezed (the grip's own release takes it from there)
            if (pressCheckFor != null && !gunHandGrip && GripTouchedAgain())
            {
                Handedness.HoldGunHandGrip();
                gunHandGrip = true;
            }

            bool wanted = WantsPressCheck(gunHandGrip, out FirearmController firearmController, out Weapon weapon, out PressCheckData data);
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
            Transform palm = ArmPalm(rightArm: true);
            Transform gun = firearmController.WeaponRoot;
            if (slide == null || slide.parent == null || gunMarker == null || gunMarker.parent == null || palm == null || gun == null)
                return false;
            // The thumb joint it turns about
            Transform[] bones = FingerBonesInPoseOrder(palm);
            int pivotJoint = Mathf.Clamp(pressCheckPivotJoint, 0, 2);
            Transform pivot = bones[1 + pivotJoint];
            if (pivot == null)
                return false;
            // The grip as it is now (the hand on it): the palm and the knuckle in the marker's frame, the palm against the gun's marker
            Transform frame = gunMarker.parent;
            Quaternion frameInverse = Quaternion.Inverse(frame.rotation);
            pressCheckPalmInFrame = frame.InverseTransformPoint(palm.position);
            pressCheckPalmTurnInFrame = frameInverse * palm.rotation;
            pressCheckPivotInFrame = frame.InverseTransformPoint(pivot.position);
            pressCheckPivotThumbJoint = pivotJoint;
            pressCheckThumbInFrame = frameInverse * pivot.rotation;
            // WeaponRoot: -up toward the muzzle, +forward the sights' side
            pressCheckSidewaysInFrame = frameInverse * Vector3.Cross(-gun.up, gun.forward);
            pressCheckBackInFrame = frameInverse * gun.up;
            pressCheckBackMetreInFrame = frame.InverseTransformVector(gun.up);
            pressCheckFrameInGun = Quaternion.Inverse(gun.rotation) * frame.rotation;
            pressCheckGunMarkerStartPosition = gunMarker.localPosition;
            pressCheckGunMarkerStartRotation = gunMarker.localRotation;
            Quaternion markerInverse = Quaternion.Inverse(gunMarker.rotation);
            pressCheckPalmInMarker = markerInverse * (palm.position - gunMarker.position);
            pressCheckPalmTurnInMarker = markerInverse * palm.rotation;

            pressCheckFor = firearmController;
            pressCheckData = data;
            pressCheckSlide = slide;
            pressCheckSlideRest = slide.localPosition;
            pressCheckSlideAxis = slide.parent.InverseTransformVector(gun.up);
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
                    $"turning about '{pivot.name}' at {pressCheckPivotInFrame * 100f} in '{frame.name}', palm at {pressCheckPalmInFrame * 100f}, round shown: {pressCheckRound != null}");
            return true;
        }

        // How far the hand has turned on the gun about the knuckle (the gun turns the other way in the hand), in the marker's frame:
        // the fingers from round the grip (the gun's forward) toward pointing up the slide (its up), about the gun's sideways axis;
        // then the wrist rolled open about the gun's length (the palm on the gun's right: its heel, below the knuckle, out to the right)
        private static Quaternion HandTurnInFrame()
        {
            float turn = Mathf.SmoothStep(0f, 1f, pressCheckShown);
            Quaternion more = Quaternion.Inverse(pressCheckFrameInGun) * Quaternion.Euler(pressCheckHandTurn) * pressCheckFrameInGun;
            return Quaternion.Slerp(Quaternion.identity, more, turn)
                * Quaternion.AngleAxis(-pressCheckWristOpen * turn, pressCheckBackInFrame)
                * Quaternion.AngleAxis(pressCheckTurnAngle * turn, pressCheckSidewaysInFrame);
        }

        // The thumb stays on the gun from the knuckle out (it turns with the gun, against the hand): the joint the gun turns about, the
        // palm's turn on the gun now, that joint's turn at the start, both in the marker's frame (HandsInteractionController.PressCheckHook)
        public static bool TryGetPressCheckThumb(out int thumbJoint, out Quaternion palmInGun, out Quaternion thumbJointInGun)
        {
            thumbJoint = pressCheckPivotThumbJoint;
            palmInGun = HandTurnInFrame() * pressCheckPalmTurnInFrame;
            thumbJointInGun = pressCheckThumbInFrame;
            return pressCheckFor != null;
        }

        // Our marker where the palm sits on the gun now: the grip turned about the knuckle, moved back with the slide (the fingers have
        // it), and that as EFT's marker for the palm
        private static void PlacePressCheckMarker()
        {
            if (pressCheckMarker == null || pressCheckGunMarker == null || pressCheckFor == null)
                return;
            Transform gun = pressCheckFor.WeaponRoot;
            Transform frame = pressCheckMarker.parent;
            Quaternion turn = HandTurnInFrame();
            float amount = Mathf.SmoothStep(0f, 1f, pressCheckShown);
            // The palm on the gun, in the frame: turned about the knuckle, moved back with the slide, the live offset on top
            Vector3 palm = pressCheckPivotInFrame + turn * (pressCheckPalmInFrame - pressCheckPivotInFrame)
                + pressCheckBackMetreInFrame * pressCheckPull - frame.InverseTransformVector(gun.rotation * pressCheckHandOffset) * amount;
            Quaternion palmTurn = turn * pressCheckPalmTurnInFrame;
            // EFT's marker for that palm (the palm's offset against the marker is in metres, the frame may be scaled)
            Quaternion markerTurn = palmTurn * Quaternion.Inverse(pressCheckPalmTurnInMarker);
            Vector3 markerAt = palm - frame.InverseTransformVector(frame.rotation * (markerTurn * pressCheckPalmInMarker));
            // And whatever the gun's own marker has been animated by since the start (it carries the hand's breathing), on ours
            Quaternion markerMoved = pressCheckGunMarker.localRotation * Quaternion.Inverse(pressCheckGunMarkerStartRotation);
            pressCheckMarker.localPosition = pressCheckGunMarker.localPosition + markerMoved * (markerAt - pressCheckGunMarkerStartPosition);
            pressCheckMarker.localRotation = markerMoved * markerTurn;

            EFT.Player player = VRGlobals.player;
            if (player != null && player._markers.Length > 1 && player._markers[1] != pressCheckMarker)
            {
                // Only over the gun's own marker (a weapon change points it at the hand: that's not ours to take)
                if (player._markers[1] == pressCheckGunMarker)
                    player._markers[1] = pressCheckMarker;
            }
        }

        // GunInteractionController.UpdateWeaponPosition, after collision's strip and before the marker anchor: the gun turned in the hand
        // the other way to the hand's turn on it, so the hand (on our marker, on your controller) doesn't turn at all. Absolute from the
        // holder's own rotation, taken the first frame; put back when the press check ends.
        public static void ApplyPressCheckGunTurn(Transform holder)
        {
            if (pressCheckFor == null || holder == null || pressCheckFor.WeaponRoot == null)
            {
                PutPressCheckHolderBack();
                return;
            }
            if (pressCheckHolder != holder)
            {
                PutPressCheckHolderBack();
                pressCheckHolder = holder;
                pressCheckHolderRest = holder.localRotation;
                pressCheckGunInHolder = Quaternion.Inverse(holder.rotation) * pressCheckFor.WeaponRoot.rotation;
            }
            // The gun's rotation in its own frame = the hand's turn on it, undone (the turn's in the marker's frame: into WeaponRoot's
            // by that frame's rest pose in it, so EFT's sway moves gun and hand together on top instead of shaking the turn)
            Quaternion gunTurn = Quaternion.Inverse(pressCheckFrameInGun * HandTurnInFrame() * Quaternion.Inverse(pressCheckFrameInGun));
            holder.localRotation = pressCheckHolderRest * pressCheckGunInHolder * gunTurn * Quaternion.Inverse(pressCheckGunInHolder);
        }

        private static void PutPressCheckHolderBack()
        {
            if (pressCheckHolder != null)
                pressCheckHolder.localRotation = pressCheckHolderRest;
            pressCheckHolder = null;
        }

        private static void EndPressCheck()
        {
            EFT.Player player = VRGlobals.player;
            if (player != null && player._markers.Length > 1 && pressCheckMarker != null && player._markers[1] == pressCheckMarker)
                player._markers[1] = pressCheckGunMarker;
            PutPressCheckHolderBack();
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
    }
}
