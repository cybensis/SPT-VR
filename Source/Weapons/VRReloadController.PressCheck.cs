using System.Collections.Generic;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // A one-handed press check on a pistol. The grip let go with the thumb still on A/B/the stick and the finger on the trigger
    // (OpenXR touch sensors) starts it; the trigger then draws the slide back, up to just far enough to see the round. Touching
    // the grip again (its value, no squeeze) puts the hand back on it.
    //
    // The hand keeps its grip pose and the gun turns in it about the thumb's big knuckle (the gun's sideways axis,
    // pressCheckTurnAngle, muzzle tipping down) until the top of the slide comes up under the fingers; they open as it turns, then
    // come down onto the slide (HandsInteractionController.PressCheckHook). Hand-on-the-slide poses are a dead end: don't retry.
    //
    // How: EFT's right marker (_markers[1]) is a transform of ours next to the gun's weapon_R_IK_marker, put where the hand sits on
    // the turned gun: EFT's arm IK puts the hand on it, and the gun's anchor (GunInteractionController.UpdateWeaponPosition) lands
    // that marker on your hand. The gun's turn is a rotation of weaponHolder (ApplyPressCheckGunTurn, before the anchor; absolute
    // from its own rest, so collision's per-frame strip can't stack it). As the slide comes back only the fingers go with it.
    // From the data (pressChecks in ReloadHolds.json) only the slide bone and the slide's travel in Tarkov's chamber check are used.
    public static partial class VRReloadController
    {
        public static float pressCheckBlendTime = 0.3f;     // s the turn takes, both ways
        public static float pressCheckTurnAngle = 90f;      // deg the gun turns in the hand about the thumb's knuckle
        public static int pressCheckPivotJoint = 1;         // the thumb joint it turns about: 0 its base, 1 its big knuckle, 2 its middle
        // deg the hand also rolls about the gun's length, the same knuckle (the thumb stays): the heel of the palm off the grip, the
        // fingers further over the slide
        public static float pressCheckWristOpen = 30f;
        // m from the knuckles to the fingertips on the slide: as the slide comes back only the fingers tilt back from the knuckles, by
        // as much as keeps their tips with it
        public static float pressCheckFingerReach = 0.05f;
        public static float pressCheckFingersCloseFrom = 0.6f;   // share of the turn with the fingers opening; after it they close onto the slide
        public static float pressCheckTravelScale = 1f;     // the slide's travel: x the gun's own check distance (Tarkov's clip: 1.35-3.3 cm)
        public static float pressCheckSlideSpeed = 0.4f;    // m/s the slide can follow the trigger at
        public static float pressCheckGripReturn = 0.6f;    // the grip's value (how far the fingers wrap it, no squeeze) that puts the hand back on it
        public static Vector3 pressCheckHandOffset = new Vector3(-0.04f, 0f, 0f);   // m the gun moved in the hand at the full turn: x right, y back, z up along the gun
        public static Vector3 pressCheckHandTurn = Vector3.zero;     // deg more turn at the full turn, about the knuckle, in the gun's frame
        // deg the thumb turned about its knuckle on the gun, in the gun's frame (x right, y back, z up): z swings it to the sides
        // (HandsInteractionController.pressCheckHookThumb only curls it in or out)
        public static Vector3 pressCheckThumbTurn = new Vector3(0f, 0f, -40f);
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
        // The grip, at the start, in the frame our marker lives in: the palm, the knuckle it turns about, the turn's axes there.
        // That frame is the gun marker's parent: it sways and breathes with the gun as EFT animates it, so the hand does too
        // (in WeaponRoot's frame the hand holds still while the gun sways).
        private static Vector3 pressCheckPalmInFrame;
        private static Quaternion pressCheckPalmTurnInFrame = Quaternion.identity;
        private static Vector3 pressCheckPivotInFrame;
        private static int pressCheckPivotThumbJoint;       // which thumb joint that is (0..2)
        private static int pressCheckSerial;                // a new one each press check (HandsInteractionController takes the thumb's reference then)
        // The gun in the left hand: worked for the right hand EFT holds it with, shown on the left as its mirror image; where the left
        // hand held the gun at the start, in the rig (weaponHolder's parent)
        private static bool pressCheckLeftHand;
        private static Vector3 pressCheckLeftInRigPosition;
        private static Quaternion pressCheckLeftInRigRotation = Quaternion.identity;
        private static Vector3 pressCheckSidewaysInFrame;   // the main turn's axis (fingers from the gun's forward toward its up)
        private static Vector3 pressCheckBackInFrame;       // the gun's length, toward the back (the wrist roll's axis)
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
        private static bool pressCheckSlideOut;             // the slide's out sound played, its in sound not yet
        public static float pressCheckSoundOut = 0.3f;      // share of the slide's travel where its out sound plays (back in under half that)
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
                // (the gun in the left hand works mirrored; left-handed mode, the whole body mirrored, isn't worked out)
                // (the gesture is the grip let go: on "Toggle Gun Grip" the grip is up through every hold)
                if (!enabled || Handedness.LeftHanded || TarkovVR.Source.Settings.VRSettings.GetGunGripToggle())
                    return false;
                if (!Handedness.TryGetGunHandThumbTouch(out bool thumbOn) || !thumbOn)
                    return false;
                if (!Handedness.GunHandTrigger.TryGetTouch(out bool fingerOn) || !fingerOn)
                    return false;
                return PressCheckDataOf(VRGlobals.firearmController) != null;
            }
        }

        // The gun is held: by the gesture, or, in a press check, by the hand touching the grip again (the thumb or finger can come
        // off the controls as the hand closes back onto it, which would let the gun go)
        public static bool PressCheckHoldsGun => PressCheckGesture || (pressCheckFor != null && GripTouchedAgain());

        private static bool GripTouchedAgain()
        {
            return Handedness.TryGetGunHandGripValue(out float value) && value >= Mathf.Max(pressCheckGripReturn, OpenXRInput.gripReleaseThreshold + 0.05f);
        }

        // The sling taking the gun mid press check (its release delay is shorter than the turn back): the marker and the holder back
        // first. Else it saves our marker as the gun's and gives it back on the next grab (the hand flies away), and saves the
        // turned holder the same way.
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
            // The gun's own slide sounds: out as it comes back, in as it goes home
            if (!pressCheckSlideOut && travel > 0f && pressCheckPull >= travel * pressCheckSoundOut)
            {
                pressCheckSlideOut = true;
                PlayFirstGunSound(pressCheckFor, SoundHand.GunHand, "SlideOutCheck", "SlideOutHalf", "OutCheck", "SlideOut", "HandleOut", "BoltOut");
            }
            else if (pressCheckSlideOut && pressCheckPull <= travel * pressCheckSoundOut * 0.5f)
            {
                pressCheckSlideOut = false;
                PlayFirstGunSound(pressCheckFor, SoundHand.GunHand, "SlideInCheck", "SlideInHalf", "InCheck", "SlideIn", "HandleIn", "BoltIn");
            }

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
            // The hand on the gun: the left one with the gun in the left hand (Handedness.GunInLeftHand)
            bool leftHand = Handedness.GunInLeftHand;
            Transform palm = ArmPalm(rightArm: !leftHand);
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
            pressCheckPivotThumbJoint = pivotJoint;
            pressCheckLeftHand = leftHand;
            if (leftHand)
            {
                // The gun in the left hand: EFT holds it with the right hand on its marker and the left is drawn as that hand's mirror image
                // across the gun's centre plane (RealLeftHandedPatches). So the press check is worked for that right hand (its palm the
                // marker itself, its thumb knuckle the left one's mirrored) and shows mirrored on the left, fingers included
                // (HandMirror.ApplyMirroredRight). Where the left hand holds the gun now, in the rig, is kept by HoldPressCheckLeftHand
                // (the offset and the wrist roll move the mirror plane, so the mirror alone wouldn't keep it).
                Transform rig = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform.parent : null;
                if (rig == null || !TarkovVR.Patches.Core.Player.RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal)
                    || !TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(gunMarker.rotation, planeNormal, rightToLeft: true, out Quaternion leftGrip))
                    return false;
                pressCheckPalmInFrame = gunMarker.localPosition;
                pressCheckPalmTurnInFrame = gunMarker.localRotation;
                pressCheckPivotInFrame = frame.InverseTransformPoint(TarkovVR.Patches.Core.Player.RealLeftHandedPatches.ReflectPoint(pivot.position, planePoint, planeNormal));
                pressCheckLeftInRigPosition = rig.InverseTransformPoint(TarkovVR.Patches.Core.Player.RealLeftHandedPatches.ReflectPoint(gunMarker.position, planePoint, planeNormal));
                pressCheckLeftInRigRotation = Quaternion.Inverse(rig.rotation) * leftGrip;
            }
            else
            {
                pressCheckPalmInFrame = frame.InverseTransformPoint(palm.position);
                pressCheckPalmTurnInFrame = frameInverse * palm.rotation;
                pressCheckPivotInFrame = frame.InverseTransformPoint(pivot.position);
            }
            // WeaponRoot: -up toward the muzzle, +forward the sights' side
            pressCheckSidewaysInFrame = frameInverse * Vector3.Cross(-gun.up, gun.forward);
            pressCheckBackInFrame = frameInverse * gun.up;
            pressCheckFrameInGun = Quaternion.Inverse(gun.rotation) * frame.rotation;
            pressCheckGunMarkerStartPosition = gunMarker.localPosition;
            pressCheckGunMarkerStartRotation = gunMarker.localRotation;
            // (the gun in the left hand: the right palm worked for IS the marker)
            Quaternion markerInverse = Quaternion.Inverse(gunMarker.rotation);
            pressCheckPalmInMarker = leftHand ? Vector3.zero : markerInverse * (palm.position - gunMarker.position);
            pressCheckPalmTurnInMarker = leftHand ? Quaternion.identity : markerInverse * palm.rotation;
            pressCheckSerial++;

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
            pressCheckSlideOut = false;

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
                    $"turning about '{pivot.name}' at {pressCheckPivotInFrame * 100f} in '{frame.name}', palm at {pressCheckPalmInFrame * 100f}, round shown: {pressCheckRound != null}, " +
                    $"{(leftHand ? "left hand (mirrored)" : "right hand")}");
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

        // The thumb stays on the gun from the knuckle out (it turns with the gun, against the hand), for HandsInteractionController.
        // PressCheckHook, all in the marker's frame: this press check's number (it takes the thumb's reference on a new one: the palm at
        // the start and the thumb's grip as EFT animates it), the joint the gun turns about, the palm's turn on the gun now and at the
        // start, and the thumb turn knob (eased in with the turn, from the gun's frame into the marker's)
        public static bool TryGetPressCheckThumb(out int serial, out int thumbJoint, out Quaternion palmNow, out Quaternion palmAtStart, out Quaternion thumbTurn)
        {
            serial = pressCheckSerial;
            thumbJoint = pressCheckPivotThumbJoint;
            palmNow = HandTurnInFrame() * pressCheckPalmTurnInFrame;
            palmAtStart = pressCheckPalmTurnInFrame;
            thumbTurn = Quaternion.Slerp(Quaternion.identity,
                Quaternion.Inverse(pressCheckFrameInGun) * Quaternion.Euler(pressCheckThumbTurn) * pressCheckFrameInGun, Mathf.SmoothStep(0f, 1f, pressCheckShown));
            return pressCheckFor != null;
        }

        // The gun in the left hand: the fingers are worked for the right hand and shown mirrored on the left (HandsInteractionController)
        public static bool PressCheckOnLeftHand => pressCheckFor != null && pressCheckLeftHand;

        // GunInteractionController.UpdateWeaponPosition, after the marker anchor (which lands our marker - the right hand's - on the rig):
        // the gun in the left hand, the left hand is drawn as the mirror image of that marker across the gun's centre plane, which the
        // turn's wrist roll and the offset move. The gun moved, as one, by what brings that mirror image back to where the left hand held
        // it at the start, in the rig: the left hand stays on the left controller.
        public static void HoldPressCheckLeftHand(Transform holder)
        {
            if (pressCheckFor == null || !pressCheckLeftHand || holder == null || holder.parent == null || pressCheckMarker == null)
                return;
            if (!TarkovVR.Patches.Core.Player.RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal)
                || !TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(pressCheckMarker.rotation, planeNormal, rightToLeft: true, out Quaternion leftNow))
                return;
            Vector3 leftNowPosition = TarkovVR.Patches.Core.Player.RealLeftHandedPatches.ReflectPoint(pressCheckMarker.position, planePoint, planeNormal);
            Transform rig = holder.parent;
            Vector3 leftWantedPosition = rig.TransformPoint(pressCheckLeftInRigPosition);
            Quaternion fix = rig.rotation * pressCheckLeftInRigRotation * Quaternion.Inverse(leftNow);
            holder.SetPositionAndRotation(leftWantedPosition + fix * (holder.position - leftNowPosition), fix * holder.rotation);
        }

        // The slide drawn back: the fingers on it tilt back from their knuckles toward the back of the gun, far enough for their tips to
        // keep up with it. The gun's back in the palm's frame and how far the slide is back; HandsInteractionController.PressCheckHook
        // turns each finger from its own knuckle-to-tip direction toward it. (A tilt about the gun's sideways axis is wrong: with the
        // knuckles above the slide and the fingers down onto it, it takes the tips forward.)
        public static bool TryGetPressCheckDrag(out Vector3 backInPalm, out float pull)
        {
            backInPalm = Vector3.zero;
            pull = pressCheckFor != null ? pressCheckPull : 0f;
            if (pull <= 0f)
                return false;
            Quaternion palmInFrame = HandTurnInFrame() * pressCheckPalmTurnInFrame;
            backInPalm = Quaternion.Inverse(palmInFrame) * pressCheckBackInFrame;
            return true;
        }

        // Our marker where the palm sits on the gun now: the grip turned about the knuckle, as EFT's marker for the palm
        private static void PlacePressCheckMarker()
        {
            if (pressCheckMarker == null || pressCheckGunMarker == null || pressCheckFor == null)
                return;
            Transform gun = pressCheckFor.WeaponRoot;
            Transform frame = pressCheckMarker.parent;
            Quaternion turn = HandTurnInFrame();
            float amount = Mathf.SmoothStep(0f, 1f, pressCheckShown);
            // The palm on the gun, in the frame: turned about the knuckle, the offset on top. It stays there as the slide comes back:
            // only the fingers go with the slide (PressCheckFingerDrag)
            Vector3 palm = pressCheckPivotInFrame + turn * (pressCheckPalmInFrame - pressCheckPivotInFrame)
                - frame.InverseTransformVector(gun.rotation * pressCheckHandOffset) * amount;
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
            pressCheckSlide.localPosition = PressCheckSlidePosition;
            if (pressCheckChamber == null)
                return;
            pressCheckChamber.gameObject.SetActive(PressCheckChamberShown);
            if (RoundInPressCheckChamber)
                pressCheckRound.localPosition = PressCheckRoundPosition;
        }

        private static Vector3 PressCheckSlidePosition => pressCheckSlideRest + pressCheckSlideAxis * pressCheckPull;
        private static bool PressCheckChamberShown => pressCheckPull > 0.002f || pressCheckChamberWasActive;
        private static bool RoundInPressCheckChamber => pressCheckChamber != null && pressCheckRound != null && pressCheckRound.parent == pressCheckChamber;
        private static Vector3 PressCheckRoundPosition => pressCheckRoundRest
            + (pressCheckChamber.InverseTransformVector(pressCheckFor.WeaponRoot.up * pressCheckPull));

        // Co-op (TryBuildSync): the slide, chamber and round as ApplyPressCheck leaves them. It runs in HandsInteractionController.LateUpdate,
        // maybe after the sync is built: the values, not the bones.
        private static void AddPressCheckParts(FirearmController firearmController, Transform weaponRoot)
        {
            if (pressCheckFor != firearmController || pressCheckSlide == null)
                return;
            AddPartPose(weaponRoot, pressCheckSlide, PressCheckSlidePosition, pressCheckSlide.localRotation, pressCheckSlide.gameObject.activeSelf);
            if (pressCheckChamber == null)
                return;
            AddPartPose(weaponRoot, pressCheckChamber, pressCheckChamber.localPosition, pressCheckChamber.localRotation, PressCheckChamberShown);
            if (RoundInPressCheckChamber)
                AddPartPose(weaponRoot, pressCheckRound, PressCheckRoundPosition, pressCheckRound.localRotation, pressCheckRound.gameObject.activeSelf);
        }
    }
}
