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
    // A/B/the stick and the finger on the trigger (OpenXR touch sensors) = the gun hand comes off the grip into the hook of his
    // pictures (John Wick's, a real one, another VR game's - all the same grip): the hand on the gun's RIGHT side, palm against it,
    // wrist low by the grip, the fingers up the side and hooked over the top of the slide's back (index at the back of the slide).
    // The trigger then draws the slide back, as far as the trigger goes, up to just far enough to see the round. Touching the grip
    // again (its value, no squeeze) puts the hand back on it.
    //
    // The hook is posed from the gun itself: the slide's back-top-right edge (its meshes), the middle knuckle forward of the back
    // face, below the top and just out from the right side, the fingers pointing up (tilted toward the muzzle), index toward the
    // back, so the palm faces the gun; the fingers curled from the open-hand pose (HandsInteractionController.PressCheckHook).
    // Earlier versions, all headset-rejected: Tarkov's own chamber check (the OTHER hand pinching the slide over the top, mirrored
    // onto the gun hand: "this looks terrible"), the hand BEHIND the gun, palm forward ("not right"), then on the LEFT side (his:
    // "palm is against the right side").
    // Only Tarkov's slide bone and how far the slide comes back are used (pressChecks in ReloadHolds.json).
    //
    // The hand is put there through EFT's own right marker (_markers[1] = a transform of ours next to the gun's weapon_R_IK_marker):
    // EFT's arm IK solves the hand onto it, and the gun's anchor (GunInteractionController.UpdateWeaponPosition) lands that marker on
    // your hand - so the gun drops into the hook at your hand, and as the slide comes back with the fingers the gun body goes forward
    // under them, as a real press check works (the fingers hold the slide, the hand pushes the frame).
    public static partial class VRReloadController
    {
        public static bool pressCheck = true;
        public static float pressCheckBlendTime = 0.15f;    // s the hand takes onto the slide and back to the grip
        public static float pressCheckTravelScale = 1f;     // the slide's travel: x the gun's own check distance (Tarkov's clip: 1.35-3.3 cm)
        public static float pressCheckSlideSpeed = 0.4f;    // m/s the slide can follow the trigger at
        public static float pressCheckGripReturn = 0.6f;    // the grip's value (how far the fingers wrap it, no squeeze) that puts the hand back on it
        // The hook: the middle finger's knuckle against the slide's back-top-right edge, the hand tilted forward from straight up
        public static float pressCheckHookForward = 0.025f; // m forward of the slide's back face
        public static float pressCheckHookBelow = 0.035f;   // m below the slide's top (the fingers reach up and over it)
        public static float pressCheckHookOut = 0.012f;     // m out from the slide's right side
        public static float pressCheckHookTilt = 10f;       // deg toward the muzzle
        public static Vector3 pressCheckHandOffset = Vector3.zero;   // m on top: x right, y back, z up along the gun (live tuning)
        public static Vector3 pressCheckHandTurn = Vector3.zero;     // deg on top, in the hand's own frame
        public static bool pressCheckRoundRides = true;     // the chambered round comes back with the slide (the extractor has it)
        public static bool pressCheckDebug = false;

        private sealed class PressCheckData
        {
            public string slideBone;
            public bool rightHand;          // Tarkov's chamber-check hand (unused since the hook)
            public HandleHold hold;         // its pinch (unused since the hook)
            public float travel;            // m the slide comes back in Tarkov's chamber check
        }
        private static readonly Dictionary<string, PressCheckData> pressChecks = new Dictionary<string, PressCheckData>();

        private static FirearmController pressCheckFor;
        private static PressCheckData pressCheckData;
        private static Transform pressCheckSlide;
        private static Vector3 pressCheckSlideRest;
        private static Vector3 pressCheckSlideAxis;         // one metre back, in the slide's parent's frame
        private static Vector3 pressCheckRearTop;           // the slide's back-top-right edge, in the slide's frame (unscaled)
        private static Transform pressCheckGunMarker;       // the gun's own weapon_R_IK_marker
        private static Transform pressCheckMarker;          // ours, next to it: EFT's _markers[1] while the hand is on the slide
        private static Vector3 pressCheckKnuckle;           // the middle finger's knuckle, from the wrist, in the palm's frame (unscaled)
        private static Vector3 pressCheckAcross;            // pinky knuckle -> index knuckle, in the palm's frame
        private static Vector3 pressCheckPalmInMarker;      // where EFT's IK puts the palm against its marker, in the marker's frame
        private static Quaternion pressCheckPalmTurnInMarker = Quaternion.identity;
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
        // How far the hand is into the hook (HandsInteractionController poses the fingers by it)
        public static float PressCheckWeight => pressCheckFor != null ? Mathf.SmoothStep(0f, 1f, pressCheckShown) : 0f;

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

        // The sling taking the gun mid press check (its release delay is shorter than the ease back): the marker back first. Else it
        // saved OUR marker as the gun's and gave it back on the next grab - headset: "the next time I go to grab the pistol from its
        // slot, my hand flies away", until another press check put the marker back.
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

            // Another gun, or this one's state gone: straight back
            if (pressCheckFor != null && (pressCheckFor != VRGlobals.firearmController || !ReferenceEquals(VRGlobals.player != null ? VRGlobals.player.HandsController : null, pressCheckFor)))
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
            if (slide == null || slide.parent == null || gunMarker == null || gunMarker.parent == null || palm == null
                || !TryMeasureHand(palm) || !TryMeasureSlide(slide, firearmController.WeaponRoot))
                return false;
            // Where EFT's IK has the palm against the gun's marker right now (the hand is on the grip): kept against ours
            Quaternion markerInverse = Quaternion.Inverse(gunMarker.rotation);
            pressCheckPalmInMarker = markerInverse * (palm.position - gunMarker.position);
            pressCheckPalmTurnInMarker = markerInverse * palm.rotation;

            pressCheckFor = firearmController;
            pressCheckData = data;
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
                    $"back-top-right edge {pressCheckRearTop * 100f} cm in the slide's frame, knuckle {pressCheckKnuckle * 100f} cm from the wrist, round shown: {pressCheckRound != null}");
            return true;
        }

        // The hand's own shape: wrist -> middle knuckle, and across the knuckles (pinky -> index), in the palm bone's frame
        private static bool TryMeasureHand(Transform palm)
        {
            Transform[] bones = FingerBonesInPoseOrder(palm);
            Transform index = bones[4], middle = bones[7], pinky = bones[13];
            if (index == null || middle == null || pinky == null)
                return false;
            Quaternion palmInverse = Quaternion.Inverse(palm.rotation);
            pressCheckKnuckle = palmInverse * (middle.position - palm.position);
            pressCheckAcross = palmInverse * (index.position - pinky.position);
            return pressCheckKnuckle.sqrMagnitude > 1e-6f && pressCheckAcross.sqrMagnitude > 1e-6f;
        }

        // The slide's back-top-right edge: the furthest its meshes (sights on it included) reach back, up and to the right
        private static bool TryMeasureSlide(Transform slide, Transform weaponRoot)
        {
            Vector3 back = weaponRoot.up, up = weaponRoot.forward, right = weaponRoot.right;
            float mostBack = float.MinValue, mostUp = float.MinValue, rightMost = float.MinValue;
            foreach (MeshFilter meshFilter in slide.GetComponentsInChildren<MeshFilter>())
            {
                if (meshFilter == null || meshFilter.sharedMesh == null)
                    continue;
                Bounds bounds = meshFilter.sharedMesh.bounds;
                Transform meshTransform = meshFilter.transform;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f));
                    Vector3 offset = meshTransform.TransformPoint(local) - slide.position;
                    mostBack = Mathf.Max(mostBack, Vector3.Dot(offset, back));
                    mostUp = Mathf.Max(mostUp, Vector3.Dot(offset, up));
                    rightMost = Mathf.Max(rightMost, Vector3.Dot(offset, right));
                }
            }
            if (mostBack == float.MinValue)
                return false;
            Vector3 edge = back * mostBack + up * mostUp + right * rightMost;
            pressCheckRearTop = Quaternion.Inverse(slide.rotation) * edge;
            return true;
        }

        // Our marker between the gun's grip marker (0) and the hook on the slide as it is now (1); EFT's right marker while it's off the grip
        private static void PlacePressCheckMarker()
        {
            if (pressCheckMarker == null || pressCheckGunMarker == null || pressCheckSlide == null)
                return;
            Transform weaponRoot = pressCheckFor.WeaponRoot;
            Vector3 back = weaponRoot.up, up = weaponRoot.forward, right = weaponRoot.right;
            // The back-top-right edge where the slide is now (drawn back by the pull)
            Vector3 slideAt = pressCheckSlide.parent.TransformPoint(pressCheckSlideRest + pressCheckSlideAxis * pressCheckPull);
            Vector3 edge = slideAt + pressCheckSlide.rotation * pressCheckRearTop;
            // The palm: fingers pointing up (tilted toward the muzzle), index toward the back - a right hand that way has its palm
            // toward the gun's left, i.e. flat on its right side - the middle knuckle forward of the back face, below the top, just
            // out from the side
            Vector3 fingersUp = Quaternion.AngleAxis(pressCheckHookTilt, right) * up;
            Quaternion palmRotation = Quaternion.LookRotation(fingersUp, back) * Quaternion.Inverse(Quaternion.LookRotation(pressCheckKnuckle, pressCheckAcross));
            palmRotation = palmRotation * Quaternion.Euler(pressCheckHandTurn);
            Vector3 knuckle = edge - back * pressCheckHookForward - up * pressCheckHookBelow + right * pressCheckHookOut
                + right * pressCheckHandOffset.x + back * pressCheckHandOffset.y + up * pressCheckHandOffset.z;
            Vector3 palm = knuckle - palmRotation * pressCheckKnuckle;
            // The marker that puts the palm there, as EFT's IK has it against the grip marker
            Quaternion markerRotation = palmRotation * Quaternion.Inverse(pressCheckPalmTurnInMarker);
            Vector3 markerPosition = palm - markerRotation * pressCheckPalmInMarker;

            Transform frame = pressCheckMarker.parent;
            float blend = Mathf.SmoothStep(0f, 1f, pressCheckShown);
            pressCheckMarker.localPosition = Vector3.Lerp(pressCheckGunMarker.localPosition, frame.InverseTransformPoint(markerPosition), blend);
            pressCheckMarker.localRotation = Quaternion.Slerp(pressCheckGunMarker.localRotation, Quaternion.Inverse(frame.rotation) * markerRotation, blend);

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
