using System.Collections.Generic;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Break-actions by hand (Manual Reload): MP-43-1C, its sawed-off, MP-18.
    //  - B (Y in left-handed mode) works the top lever / locking lever, and a support grip lets go. The barrels unlatch and swing on Tarkov's
    //    hinge (gen_breakactions.py bakes every bone the open clip moves, keyed by the barrel's turn, plus the lever's own swing). Free barrels
    //    swing as a rod on the hinge under gravity and the gun's motion, so a flick shuts them. Fully home with the lever released, they latch.
    //  - The off hand's grip on the fore-end holds the barrels: they follow the hand about the hinge, and pushed home they latch while the
    //    hand stays on as the two-handed grip. While open, the off hand is never the support grip (the gun hand aims alone).
    //  - While open, the off hand's trigger at a chamber draws its shell out along the bore, as long as the trigger is held. A fired case
    //    that is let go flies off with the hand (Tarkov's own shell physics). A live round goes to the rig at the chest pouch, into an
    //    empty chamber at its mouth, or else to the ground.
    //  - Gripping at the chest pouch with a chamber empty takes a shell (the bolt-action rounds' hold code). Lined up on an empty chamber
    //    it slides in along the bore; past breakShellCommit the inventory move into that chamber runs and Tarkov's round model is put in.
    //  - Two barrels work like Tarkov's own reload: with both fired, one draw takes both cases; with both empty, the pouch gives two shells
    //    side by side, which go in together (one move per chamber).
    //  - Moves into or out of a chamber skip Tarkov's own chamber operations (LetsSingleRoundIn), like the bolt-action rounds.
    // Revolvers work the same way (Rhino, RSh-12, MTs-255; gen_revolvers.py): the "barrel" is the crane the cylinder swings out on, the
    // chambers are the cylinder's camoras, the lever is its release (B pops the cylinder out), and the fore-end hold is the off hand on the
    // cylinder. One round per pouch grab, one case per draw. With no live round left, the off hand's trigger at the ejector takes it, and
    // pushing it in carries the fired cases out until they come free (Tarkov's rule: an empty cylinder ejects all, else one at a time).
    // The SP-81 flare pistol is a break-action whose clip cocks the hammer once the barrel is shut; latching it by hand cocks it the same way.
    // The M32A1 is a revolver whose crane carries the barrel, foregrip and optic. B with the off hand on the foregrip swings the crane with
    // that hand, and a detent holds it at its open stop. Tipping an empty cylinder back drops its fired cases (it has no ejector). Tarkov's
    // spring-driven cylinder fires upward from where it is wound and stops at an empty chamber, so it takes rounds next to the ones
    // already in it and is wound on the latch.
    public static partial class VRReloadController
    {
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The data (ReloadHolds.json "breakActions")

        public sealed class SavedBreakAction
        {
            public string barrel;               // the hinge's bone, under the weapon bone
            public float[] hingeAxis;           // in its closed local frame, positive = opening
            public float openDegrees;
            public List<string> chambers;
            public BreakAction.SavedBreakBones open;        // every bone the open moves, keyed by the barrel's turn
            public BreakAction.SavedBreakBones lever;       // the lever's swing
            public BreakAction.SavedBreakHold foreEnd;      // the left palm on the fore-end, in the barrel bone's frame; grabPoint where the fingers close
            public BreakAction.SavedBreakHold loadHold;     // a new shell's bone in the left palm's frame
            public BreakAction.SavedBreakHold carryHold;    // a revolver's round as the hand brings it over (the load hold is the push behind it)
            public BreakAction.SavedBreakHold pluckHold;    // a drawn shell's
            public BreakAction.SavedBreakHold loadPair;     // two at once (the both-barrels clips): the left chamber's, position2 / rotation2 the right's
            public BreakAction.SavedBreakHold pluckPair;
            public bool revolver;               // (ReloadHolds "revolvers"): barrel = the crane, chambers = the camoras' round bones
            public string cylinder;
            public BreakAction.SavedEjector ejector;
            public SavedPumpBone hammer;        // the SP-81's: its local pose down, then cocked
            public bool holdsOpen;              // the M32A1's crane: a detent at its open stop
            public bool supportOnCrane;         // its support grip (the foregrip) rides the crane: B with it held swings the crane with that hand
        }

        internal sealed class BreakData
        {
            public string barrelPath;
            public Vector3 hingeAxis;
            public float openDegrees;
            public int chamberCount;
            public BreakAction.KeyedBones open;
            public BreakAction.KeyedBones lever;
            public BreakAction.LeverPress leverPress;
            public BreakAction.BreakHold foreEnd;
            public BreakAction.BreakHold loadHold;
            public BreakAction.BreakHold carryHold;
            public BreakAction.BreakHold pluckHold;
            public BreakAction.BreakHold loadPair;
            public BreakAction.BreakHold pluckPair;
            public bool revolver;
            public string cylinderPath;
            public BreakAction.Ejector ejector;
            public BreakAction.KeyedBones hammer;
            public bool holdsOpen;
            public bool supportOnCrane;
        }

        private static readonly Dictionary<string, BreakData> breakGuns = new Dictionary<string, BreakData>();

        private static BreakData BreakFrom(SavedBreakAction saved)
        {
            if (saved == null || string.IsNullOrEmpty(saved.barrel) || saved.hingeAxis == null || saved.open == null || saved.foreEnd == null)
                return null;
            BreakData data = new BreakData
            {
                barrelPath = saved.barrel,
                hingeAxis = new Vector3(saved.hingeAxis[0], saved.hingeAxis[1], saved.hingeAxis[2]).normalized,
                openDegrees = Mathf.Max(saved.openDegrees, 1f),
                chamberCount = saved.chambers?.Count ?? 1,
                open = BreakAction.KeyedFrom(saved.open),
                lever = BreakAction.KeyedFrom(saved.lever),
                leverPress = BreakAction.LeverPressFrom(saved.lever?.press),
                foreEnd = BreakAction.BreakHoldFrom(saved.foreEnd),
                loadHold = BreakAction.BreakHoldFrom(saved.loadHold),
                carryHold = BreakAction.BreakHoldFrom(saved.carryHold),
                pluckHold = BreakAction.BreakHoldFrom(saved.pluckHold),
                loadPair = BreakAction.BreakHoldFrom(saved.loadPair),
                pluckPair = BreakAction.BreakHoldFrom(saved.pluckPair),
                revolver = saved.revolver,
                cylinderPath = saved.cylinder,
                ejector = BreakAction.EjectorFrom(saved.ejector),
                hammer = saved.hammer != null ? BreakAction.KeyedFrom(new BreakAction.SavedBreakBones { bones = new List<SavedPumpBone> { saved.hammer } }) : null,
                holdsOpen = saved.holdsOpen,
                supportOnCrane = saved.supportOnCrane,
            };
            return data.open != null && data.foreEnd != null && System.Array.IndexOf(data.open.paths, data.barrelPath) >= 0 ? data : null;
        }

        // Co-op (TryBuildSync): adds the bones that are off their rest pose, as PoseBreakAction writes them this frame
        private static void AddBreakActionParts(FirearmController firearmController, Transform weaponRoot)
        {
            if (BreakAction.breakBonesFor != firearmController || BreakAction.barrelBone == null || !BreakAction.ManualBreakOn(firearmController, out BreakData data)
                || !BreakAction.breakStates.TryGetValue(firearmController.Item, out BreakAction.BreakState state))
                return;
            if (state.open > 0f || BreakAction.HoldingBarrels)
            {
                for (int bone = 0; bone < BreakAction.breakOpenBones.Length; bone++)
                {
                    Transform transform = BreakAction.breakOpenBones[bone];
                    if (transform != null && BreakAction.KeyedPose(data.open, bone, state.open / data.openDegrees, out Vector3 position, out Quaternion rotation))
                        AddPartPose(weaponRoot, transform, position, rotation, transform.gameObject.activeSelf);
                }
            }
            if (BreakAction.breakLeverBones != null && state.lever > 0f)
            {
                for (int bone = 0; bone < BreakAction.breakLeverBones.Length; bone++)
                {
                    Transform transform = BreakAction.breakLeverBones[bone];
                    if (transform != null && BreakAction.KeyedPose(data.lever, bone, state.lever, out Vector3 position, out Quaternion rotation))
                        AddPartPose(weaponRoot, transform, position, rotation, transform.gameObject.activeSelf);
                }
            }
            if (BreakAction.ejectorBones != null && state.ejector > 0f)
            {
                for (int bone = 0; bone < BreakAction.ejectorBones.Length; bone++)
                {
                    Transform transform = BreakAction.ejectorBones[bone];
                    if (transform != null && BreakAction.KeyedPose(data.ejector.bones, bone, state.ejector, out Vector3 position, out Quaternion rotation))
                        AddPartPose(weaponRoot, transform, position, rotation, transform.gameObject.activeSelf);
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // A new shell from the chest pouch

        private static bool breakShellHeld;

        // From UpdateHeldRound while the held round is a break-action's shell
        private static void UpdateHeldBreakShell(bool gripReleased)
        {
            FirearmController firearmController = heldRoundFor;
            BreakData data = BreakAction.breakShellData;
            bool valid = firearmController != null && firearmController == VRGlobals.firearmController && heldRoundProp != null && data != null
                && BreakAction.ManualBreakOn(firearmController, out _) && BreakAction.barrelBone != null && BreakAction.breakBonesFor == firearmController && roundOnRightArm == OnRightArm(false)
                && !VRGlobals.menuOpen && (BreakAction.breakShellCommitted || HeldRoundIsStillValid());
            if (!valid)
            {
                if (BreakAction.breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] the shell's gun or hand changed: {(BreakAction.breakShellCommitted ? "it's in" : "back in the pouch")}");
                BreakAction.EndBreakShell();
                return;
            }
            Weapon weapon = firearmController.Item;
            Firearms firearms = firearmController.Firearms;
            BreakAction.BreakState state = BreakAction.BreakStateOf(weapon);
            if (!BreakAction.breakShellOnBore)
            {
                if (gripReleased)
                {
                    PutRoundBack();
                    PulseRoundHand(false, 0.15f, 0.02f);
                    return;
                }
                bool openEnough = state.open >= data.openDegrees * BreakAction.barrelOpenEnough && GunIdle(firearmController) && firearms != null;
                BreakAction.ApproachBreakShellHand(firearms, weapon, openEnough);
                if (openEnough)
                    BreakAction.TryPutShellOnBore(firearms, weapon);
                return;
            }
            Vector3 moved = BreakAction.barrelBone.InverseTransformPoint(HandRig(gunHand: false).position) - BreakAction.breakShellHandAtCapture;
            float wanted = BreakAction.breakShellOutAtCapture + Vector3.Dot(moved, BreakAction.breakShellBoreInBarrel);
            if (!BreakAction.breakShellCommitted)
            {
                if (gripReleased)
                {
                    PutRoundBack();
                    PulseRoundHand(false, 0.15f, 0.02f);
                    return;
                }
                // Pulled back off the bore, the barrels swung up out of reach, or Tarkov took the gun: back in the hand
                if (wanted > roundLength + BreakAction.breakShellReach + 0.02f || state.open < data.openDegrees * BreakAction.barrelOpenEnough * 0.5f || !GunIdle(firearmController))
                {
                    BreakAction.TakeBreakShellOffBore();
                    return;
                }
                BreakAction.breakShellOut = Mathf.Clamp(wanted, 0f, roundLength + BreakAction.breakShellReach);
                BreakAction.BlendBreakShellHand();
                if (BreakAction.breakShellOut <= roundLength * (1f - BreakAction.breakShellCommit))
                    BreakAction.CommitBreakShell(firearmController, weapon);
                return;
            }
            BreakAction.breakShellOut = Mathf.Max(0f, Mathf.Min(BreakAction.breakShellOut - BreakAction.breakShellFinishSpeed * Time.deltaTime, Mathf.Max(wanted, 0f)));
            BreakAction.BlendBreakShellHand();
            if (BreakAction.breakShellOut <= 0f && BreakAction.breakShellLoaded != 0 && (BreakAction.breakShellLoaded < 0 || BreakAction.breakShellSecondLoaded != 0))
                BreakAction.FinishBreakShell(firearmController, weapon);
        }

        private static void ReleaseBreakShellHand()
        {
            BreakAction.ReleaseApproachLatch();
            if (handLatched && handLatch != null && latchHolder == BreakAction.breakShellRider && BreakAction.breakShellRider != null)
                ReleaseHandLatch();
            if (BreakAction.breakShellRider != null && heldRoundProp != null && BreakAction.breakShellRider.IsChildOf(heldRoundProp.transform))
                BreakAction.breakShellRider.SetParent(null, worldPositionStays: true);
        }

        // The break-action part of EndRoundHold
        private static void ClearBreakShell()
        {
            if (breakShellHeld)
                ReleaseBreakShellHand();
            if (BreakAction.breakShellSecond != null)
            {
                BreakAction.breakShellSecond.transform.SetParent(null, worldPositionStays: true);
                ReturnPropToPool(BreakAction.breakShellSecond);
                BreakAction.breakShellSecond = null;
            }
            BreakAction.breakShellSecondChamber = -1;
            breakShellHeld = false;
            BreakAction.breakShellData = null;
            BreakAction.breakShellOnBore = false;
            BreakAction.breakShellCommitted = false;
        }

        // Places the held shell in the hand (from PlaceHeldRound) at the load clip's hold, mirrored on the right arm
        private static void PlaceHeldBreakShell()
        {
            if (BreakAction.breakShellOnBore || heldRoundProp == null || BreakAction.breakShellData == null || BreakAction.breakShellData.loadHold == null)
                return;
            Transform palm = ArmPalm(roundOnRightArm);
            if (palm == null)
                return;
            // Two shells: the both-barrels clip's hold on the pair, else the second beside the first as the chambers sit. One revolver
            // round: carried as its clip brings it over, relative to the controller's hand (the rendered hand turns around it toward the
            // push near a chamber: ApproachBreakShellHand, BlendBreakShellHand).
            BreakAction.BreakHold pair = BreakAction.breakShellSecond != null ? BreakAction.breakShellData.loadPair : null;
            bool carried = pair == null && BreakAction.breakShellData.carryHold != null;
            Transform frame = carried ? HandRig(gunHand: false) : palm;
            if (frame != null && BreakAction.BreakShellHoldInPalm(pair ?? BreakAction.breakShellData.carryHold ?? BreakAction.breakShellData.loadHold, heldRoundProp.transform, roundOnRightArm, out Vector3 position, out Quaternion rotation))
                SetPoseIn(frame, heldRoundProp.transform, position, rotation);
            if (BreakAction.breakShellSecond == null)
                return;
            if (pair?.second != null && BreakAction.BreakShellHoldInPalm(pair.second, BreakAction.breakShellSecond.transform, roundOnRightArm, out Vector3 secondPosition, out Quaternion secondRotation))
                SetPoseIn(palm, BreakAction.breakShellSecond.transform, secondPosition, secondRotation);
            else
            {
                Transform first = heldRoundProp.transform;
                BreakAction.breakShellSecond.transform.SetPositionAndRotation(first.position + first.rotation * BreakAction.breakShellPairPosition, first.rotation * BreakAction.breakShellPairRotation);
            }
        }

        private static Vector3[] BreakShellFingers(bool rightArm)
        {
            BreakData data = BreakAction.breakShellData;
            if (BreakAction.breakShellSecond == null && data?.carryHold?.fingers != null && data.loadHold?.fingers != null
                && data.carryHold.fingers.Length == data.loadHold.fingers.Length)
            {
                // Blend the carry hold's fingers into the push hold's as the round nears its chamber and goes in
                float push = BreakAction.breakShellBlend;
                Vector3[] from = data.carryHold.fingers, to = data.loadHold.fingers;
                if (BreakAction.breakFingersBlend.Length != from.Length)
                    BreakAction.breakFingersBlend = new Vector3[from.Length];
                for (int joint = 0; joint < from.Length; joint++)
                    BreakAction.breakFingersBlend[joint] = Quaternion.Slerp(Quaternion.Euler(from[joint]), Quaternion.Euler(to[joint]), push).eulerAngles;
                bool rightHanded = data.carryHold.rightHanded;
                return rightArm != rightHanded ? MirroredPose(BreakAction.breakFingersBlend, rightToLeft: rightHanded, BreakAction.breakFingersMirrored) ?? BreakAction.breakFingersBlend : BreakAction.breakFingersBlend;
            }
            return BreakAction.BreakFingers(BreakAction.breakShellSecond != null ? data?.loadPair ?? data?.loadHold : data?.loadHold, rightArm) ?? MAGAZINE_PULL_FINGERS;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // True for our move into or out of one of the break-action's chambers (LetsSingleRoundIn): Tarkov's chamber operation is skipped
        private static bool LetsBreakMoveThrough(FirearmController controller, EFT.InventoryLogic.Operations.IInventoryOperation operation)
        {
            if (!BreakAction.breakMoveInFlight || controller == null || controller != VRGlobals.firearmController || controller.Item == null
                || (controller.Item.ReloadMode != Weapon.EReloadMode.OnlyBarrel && !BreakAction.IsRevolver(controller.Item)))
                return false;
            if (operation is EFT.InventoryLogic.Operations.ISubOperation subOperation && subOperation.BaseInventoryOperation != null && BreakAction.TouchesChamber(controller.Item, subOperation.BaseInventoryOperation))
                return true;
            return BreakAction.TouchesChamber(controller.Item, operation);
        }

        public static bool BreakLeverPressing => BreakAction.breakLeverPress > 0f && BreakAction.breakLeverPressFor != null;

        // From UpdateMagazineReleaseButton (these guns have no magazine): puts the gun hand's finger on the lever, the clip's joints over
        // the animator's (with the magazine release's finger code), as the hand moves onto it. Returns true for a break-action we handle.
        private static bool DriveBreakLeverFinger()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (!BreakAction.ManualBreakOn(firearmController, out BreakData data))
                return false;
            BreakAction.LeverPress press = data.leverPress;
            if (press == null)
                return true;
            if (releaseFingerFor != firearmController || releaseFingerJoints == null)
            {
                Transform[] joints = GunHandFinger(press.finger + 1);
                if (joints == null || joints.Length != press.joints.Length)
                    return true;
                releaseFingerFor = firearmController;
                releaseFingerJoints = joints;
                releaseFingerPressed = press.joints;
                releaseFingerAnimated = new Quaternion[joints.Length];
                releaseFingerWritten = new Quaternion[joints.Length];
            }
            releaseFingerTarget = BreakAction.breakLeverPressFor == firearmController ? BreakAction.breakLeverPress : 0f;
            return true;
        }

        public static class BreakAction
        {
            public static bool breakActions = true;             // false = Tarkov's own break-action reloads only
            public static bool breakDebugLog = true;            // unlatch, holds, cases, shells
            public static float barrelOpenEnough = 0.6f;        // share of the hinge's travel open before the chambers can be reached
            public static float foreEndGrabRadius = 0.1f;       // m from the palm to where the fore-end is taken
            public static float caseGrabRadius = 0.045f;        // m from the palm to a chamber's mouth that takes its shell
            public static float caseFreeShare = 0.85f;          // share of its length a shell comes out before it's free in the hand
            public static float breakShellCaptureRadius = 0.035f;   // m off a chamber's bore line a held shell goes onto it
            public static float breakShellCaptureAngle = 40f;   // deg off the bore
            public static float breakShellReach = 0.03f;        // m behind the chamber, past the shell's own length, where the bore line still catches it
            public static float breakShellCommit = 0.5f;        // share of its length inserted, past which it is loaded and slides home by itself
            public static float breakShellFinishSpeed = 0.5f;   // m/s for the rest of the way
            public static float caseIntoHandTime = 0.12f;       // s a drawn shell eases into the hand's hold
            public static float leverTime = 0.06f;              // s the lever takes to swing
            // The gun hand presses the lever as in Tarkov's open clip: the hand moves onto the clip's IK marker at the press, with the clip's
            // finger joints. The MP-43's marker moves 4.4 cm up and rolls 36 deg to bring the thumb onto the top lever; the MP-18's stays put.
            // false = the hand doesn't move.
            public static bool leverHandPress = true;
            public static float leverPressTime = 0.1f;          // s the hand takes onto the lever and back
            // The barrels on the hinge are a rod turning about its end (inertia 4/3 m r^2, r = distance to its centre of mass). Gravity and the
            // hinge's own acceleration (the gun's motion) act as pseudo-forces, and the gun's turn about the hinge axis leaves the barrels behind.
            // Hinge friction slows them, and they bounce off the stop.
            public static float barrelGravity = 1f;
            public static float barrelInertia = 1f;             // share of the gun's own motion felt (0 = gravity only)
            public static float barrelMotionSmoothing = 0.06f;  // s the gun motion's derivatives are smoothed over (tracking noise)
            public static float barrelFriction = 3f;            // 1/s
            public static float barrelBounce = 0.15f;
            public static float barrelStopSound = 60f;          // deg/s into the open stop needed to play its sound
            public static float openSoundRearm = 0.6f;          // share of the way open it has to come back down to before the stop sounds again
            // A revolver round: as it nears an empty chamber's mouth, the hand turns from the carry hold toward the push hold behind it.
            // carryBlendFar / carryBlendNear = the round's middle's distance from the mouth (m); carryBlendApproach = the share of the turn
            // done by the time the round is caught on the bore. Pushing it in does the rest.
            public static float carryBlendFar = 0.15f;
            public static float carryBlendNear = 0.03f;
            public static float carryBlendApproach = 0.7f;
            // The zone at the back of a revolver's cylinder. The chamber bones sit on the rear face, in a ring around the axis. The zone is a
            // sphere cylinderBackBehind (m) behind the face that reaches cylinderBackReach (m) past the ring. The trigger anywhere in it draws
            // the fired case nearest the hand; a round brought into it goes onto the nearest empty chamber it points within cylinderBackAngle of.
            public static float cylinderBackBehind = 0.02f;
            public static float cylinderBackReach = 0.045f;
            public static float cylinderBackAngle = 75f;
            // Inertia of a revolver's crane and cylinder about the hinge (r^2 + R^2 / 2, including the cylinder's own turn), taken as
            // this x the squared distance of its centre of mass
            public static float cylinderInertia = 1.2f;
            public static float cylinderKickSpeed = 360f;       // deg/s the release pops the cylinder out with
            // On closing, turn the cylinder so the chamber that fires next holds a live round, as Tarkov's own reload leaves it (it loads
            // from there). false = it stays where it was, and an empty chamber clicks. A spring-driven cylinder is always wound, because it
            // can't turn past an empty chamber.
            public static bool revolverIndexToLiveRound = true;
            public static float ejectorGrabRadius = 0.06f;      // m from the palm to where the clip's fingers press the ejector
            public static float ejectorMinPush = 0.015f;        // m, the minimum hand push for the ejector's whole travel
            public static float ejectorEjectAt = 0.9f;          // share of its travel at which the fired cases come free
            public static float ejectorCaseSpeed = 1.5f;        // m/s, minimum case speed out of the back of the cylinder
            public static float ejectorReturnTime = 0.08f;      // s it springs home once let go
            // The SP-81's hammer is cocked hammerCockDelay s after the barrel latches and takes hammerCockTime s
            public static float hammerCockDelay = 0.1f;
            public static float hammerCockTime = 0.12f;
            // Within craneDetentDegrees of its open stop, a detent pulls the M32A1's crane in, with at most craneDetent times the torque its
            // own weight can apply. A gentle turn of the gun doesn't shut it; a flick does.
            public static float craneDetent = 1.5f;
            public static float craneDetentDegrees = 6f;
            public static float craneOpenSoundAt = 0.15f;       // share of the way open the hand has swung it when its swing sound plays
            // Without an ejector (the M32A1), an empty cylinder drops its fired cases when the bore out of its back is within caseDumpAngle (deg)
            // of straight down (Tarkov's clip tips it to ~73). They leave at caseDumpSpeed (m/s) on top of the gun's own motion.
            public static float caseDumpAngle = 60f;
            public static float caseDumpSpeed = 0.4f;

            public static bool HoldingBarrels { get; private set; }
            public static bool HoldingBreakCase { get; private set; }
            public static bool HoldingEjector { get; private set; }

            // The break-action code owns the off hand while the gun is unlatched (open barrels are never two-handed) and while the hand works it
            internal static bool BreakActionOwnsOffHand => HoldingBarrels || HoldingBreakCase || HoldingEjector || breakShellHeld || breakGunOpen;
            private static bool breakGunOpen;                   // this frame: a break-action we handle is unlatched or open

            // No firing while unlatched, open, or with a shell on its way in
            public static bool BreakActionBlocksFiring => breakGunOpen || HoldingBarrels || HoldingBreakCase || HoldingEjector || (breakShellHeld && breakShellOnBore);

            public sealed class SavedEjector
            {
                public List<SavedPumpBone> bones;   // every bone the eject clip moves, keyed by the extractor's travel
                public string bone;                 // the extractor (its parent the cylinder)
                public float[] travel;              // its full travel, local
                public string pressBone;            // the part the clip's finger pushes (the hand rides it)
                public SavedBreakHold hold;         // the left palm on it, in its frame; grabPoint where the fingers close
                public float[] contact;             // the pushing fingertip at the push's start and end, in the crane's frame
                public float[] contactEnd;
            }

            internal sealed class Ejector
            {
                public KeyedBones bones;
                public string pressPath;
                public BreakHold hold;
                public Vector3 push;                // the fingertip's way over the push, in the crane's frame
            }

            public sealed class SavedBreakBones
            {
                public float degrees;
                public List<SavedPumpBone> bones;
                public SavedLeverPress press;       // the gun hand's press on the lever
            }

            public sealed class SavedLeverPress
            {
                public int finger;                  // 0 thumb .. 4 pinky
                public float[] joints;              // its three joints on the lever, local eulers
                public float[] markerRest;          // the gun hand's IK marker at the clip's start and at the press, local to its parent
                public float[] markerPressed;
            }

            internal sealed class LeverPress
            {
                public int finger;
                public Quaternion[] joints;
                public Vector3 markerMove;          // the marker's move, in its parent's frame
                public Quaternion markerTurn;       // and turn, in its parent's frame (applied on top of its own rotation)
            }

            public sealed class SavedBreakHold
            {
                public float[] position;
                public float[] rotation;
                public float[] fingers;
                public float[] grabPoint;
                public float[] position2;
                public float[] rotation2;
                public bool right;                  // taken from the clip's right hand (the M32A1's clips load with the gun hand): mirrored onto the left
            }

            internal sealed class BreakHold
            {
                public Vector3 position;
                public Quaternion rotation;
                public Vector3[] fingers;
                public Vector3 grabPoint;
                public BreakHold second;            // a pair's other shell (the right chamber's)
                public bool rightHanded;
            }

            internal sealed class KeyedBones
            {
                public string[] paths;
                public Vector3[][] positions;
                public Quaternion[][] rotations;
            }
            private static readonly Dictionary<string, string> breakFamilies = new Dictionary<string, string>();    // by template id

            internal static Ejector EjectorFrom(SavedEjector saved)
            {
                if (saved == null || string.IsNullOrEmpty(saved.pressBone) || saved.contact == null || saved.contactEnd == null || saved.hold == null)
                    return null;
                KeyedBones bones = KeyedFrom(new SavedBreakBones { bones = saved.bones });
                BreakHold hold = BreakHoldFrom(saved.hold);
                if (bones == null || hold == null)
                    return null;
                return new Ejector { bones = bones, pressPath = saved.pressBone, hold = hold, push = Vector3From(saved.contactEnd, 0) - Vector3From(saved.contact, 0) };
            }

            internal static KeyedBones KeyedFrom(SavedBreakBones saved)
            {
                if (saved == null || saved.bones == null || saved.bones.Count == 0)
                    return null;
                int count = saved.bones.Count;
                KeyedBones keyed = new KeyedBones { paths = new string[count], positions = new Vector3[count][], rotations = new Quaternion[count][] };
                for (int bone = 0; bone < count; bone++)
                {
                    SavedPumpBone savedBone = saved.bones[bone];
                    int keyCount = savedBone.keys?.Count ?? 0;
                    if (keyCount < 2)
                        return null;
                    keyed.paths[bone] = savedBone.path;
                    keyed.positions[bone] = new Vector3[keyCount];
                    keyed.rotations[bone] = new Quaternion[keyCount];
                    for (int key = 0; key < keyCount; key++)
                    {
                        keyed.positions[bone][key] = Vector3From(savedBone.keys[key], 0);
                        keyed.rotations[bone][key] = QuaternionFrom(savedBone.keys[key], 3);
                    }
                }
                return keyed;
            }

            internal static LeverPress LeverPressFrom(SavedLeverPress saved)
            {
                if (saved == null || saved.joints == null || saved.joints.Length < 9 || saved.markerRest == null || saved.markerPressed == null)
                    return null;
                Vector3[] eulers = FingersFrom(saved.joints);
                Quaternion rest = QuaternionFrom(saved.markerRest, 3);
                return new LeverPress
                {
                    finger = saved.finger,
                    joints = new[] { Quaternion.Euler(eulers[0]), Quaternion.Euler(eulers[1]), Quaternion.Euler(eulers[2]) },
                    markerMove = Vector3From(saved.markerPressed, 0) - Vector3From(saved.markerRest, 0),
                    markerTurn = QuaternionFrom(saved.markerPressed, 3) * Quaternion.Inverse(rest),
                };
            }

            internal static BreakHold BreakHoldFrom(SavedBreakHold saved)
            {
                if (saved == null || saved.position == null || saved.rotation == null)
                    return null;
                return new BreakHold
                {
                    position = Vector3From(saved.position, 0),
                    rotation = QuaternionFrom(saved.rotation, 0),
                    fingers = FingersFrom(saved.fingers),
                    grabPoint = saved.grabPoint != null && saved.grabPoint.Length >= 3 ? Vector3From(saved.grabPoint, 0) : Vector3From(saved.position, 0),
                    second = saved.position2 != null && saved.rotation2 != null
                        ? new BreakHold { position = Vector3From(saved.position2, 0), rotation = QuaternionFrom(saved.rotation2, 0) } : null,
                    rightHanded = saved.right,
                };
            }

            // The gun's family is the longest clip-name prefix before "_reload_start" among the animator's clips that we have data for
            // (the sawed-off's clips also start with the MP-43's name)
            private static bool TryGetBreakAction(FirearmController firearmController, out BreakData data)
            {
                data = null;
                if (firearmController == null || firearmController.Item == null)
                    return false;
                bool revolver = IsRevolver(firearmController.Item);
                if (firearmController.Item.ReloadMode != Weapon.EReloadMode.OnlyBarrel && !revolver)
                    return false;
                LoadRecordedGrips();
                string templateId = firearmController.Item.TemplateId.ToString();
                if (!breakFamilies.TryGetValue(templateId, out string family))
                {
                    RuntimeAnimatorController animatorController = firearmController.FirearmsAnimator?.Animator?.runtimeAnimatorController;
                    if (animatorController == null)
                        return false;
                    family = null;
                    foreach (AnimationClip clip in animatorController.animationClips)
                    {
                        if (clip == null)
                            continue;
                        string clipName = ClipName(clip);
                        int end = clipName.IndexOf("_reload_start", System.StringComparison.OrdinalIgnoreCase);
                        if (end <= 0)
                            continue;
                        string name = clipName.Substring(0, end).ToLowerInvariant();
                        if (breakGuns.ContainsKey(name) && (family == null || name.Length > family.Length))
                            family = name;
                    }
                    breakFamilies[templateId] = family;
                    if (breakDebugLog)
                    {
                        BaseSoundPlayer soundPlayer = GunSoundPlayerOf(firearmController);
                        Plugin.MyLog.LogInfo($"[VRBreak] {firearmController.Item.LocalizedShortName()}: clips '{family ?? "none"}', sounds: "
                            + (soundPlayer != null ? string.Join(", ", soundPlayer.AdditionalSounds.ConvertAll(sound => sound.EventName)) : "no sound player"));
                    }
                }
                return family != null && breakGuns.TryGetValue(family, out data) && data.revolver == revolver;
            }

            internal static bool IsRevolver(Weapon weapon) => weapon is Revolver && weapon.GetCurrentMagazine() is CylinderMagazine;

            // The chambers: a break-action's barrel chambers, or a revolver cylinder's camoras (Tarkov indexes its shell and round models the same way)
            private static Slot[] BreakSlots(Weapon weapon) => weapon is Revolver && weapon.GetCurrentMagazine() is CylinderMagazine cylinder ? cylinder.Camoras : weapon.Chambers;

            internal static bool ManualBreakOn(FirearmController firearmController, out BreakData data)
            {
                data = null;
                return enabled && breakActions && VRSettings.GetManualReloadOn() && firearmController != null && firearmController._player != null
                    && firearmController._player.IsYourPlayer && TryGetBreakAction(firearmController, out data);
            }

            // True when the hands load this gun (used by GunInteractionController's ManualReloadReplacesMenu)
            public static bool HandsLoadBreakAction(FirearmController firearmController)
            {
                return ManualBreakOn(firearmController, out _);
            }

            // B (Y in left-handed mode) is this gun's lever, never Tarkov's reload (InputHandlers). Not during a malfunction: Tarkov's own fix runs.
            public static bool GunHandButtonIsBarrelLever(FirearmController firearmController)
            {
                return VRGlobals.inGame && ManualBreakOn(firearmController, out _) && firearmController.Item.MalfState.State == Weapon.EMalfunctionState.None;
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            // The barrels' state, per gun (it stays across a swap)

            internal sealed class BreakState
            {
                public float open;              // deg off shut
                public float speed;             // deg/s
                public bool latched = true;
                public float lever;             // 0 at rest .. 1 swung over
                public float ejector;           // a revolver's: 0 home .. 1 pushed out
                public bool openSounded;        // the open stop's sound played this opening (bounces off it don't play it again)
                public int restFrames;          // frames the rest pose is still written after reaching it (the animator may not write it)
            }
            internal static readonly Dictionary<Weapon, BreakState> breakStates = new Dictionary<Weapon, BreakState>();

            internal static BreakState BreakStateOf(Weapon weapon)
            {
                if (!breakStates.TryGetValue(weapon, out BreakState state))
                {
                    state = new BreakState();
                    breakStates[weapon] = state;
                }
                return state;
            }

            internal static FirearmController breakBonesFor;
            internal static Transform breakFrame, barrelBone;
            internal static Transform[] breakOpenBones, breakLeverBones, ejectorBones;
            private static Transform breakCylinder, ejectorPressBone;   // a revolver's
            private static Transform breakHammerBone;           // the SP-81's
            private static Vector3 barrelCenterOfMass;          // in the barrel bone's frame
            private static Quaternion barrelClosedLocal;        // its closed local rotation (the keys' first)

            private static bool EnsureBreakBones(FirearmController firearmController, BreakData data)
            {
                if (breakBonesFor == firearmController && barrelBone != null && breakFrame != null)
                    return true;
                breakBonesFor = firearmController;
                breakFrame = ShotgunFrame(firearmController);
                barrelBone = breakFrame != null ? breakFrame.Find(data.barrelPath) : null;
                if (barrelBone == null)
                    return false;
                breakOpenBones = FindKeyedBones(breakFrame, data.open);
                breakLeverBones = FindKeyedBones(breakFrame, data.lever);
                ejectorBones = data.ejector != null ? FindKeyedBones(breakFrame, data.ejector.bones) : null;
                ejectorPressBone = data.ejector != null ? breakFrame.Find(data.ejector.pressPath) : null;
                breakCylinder = !string.IsNullOrEmpty(data.cylinderPath) ? breakFrame.Find(data.cylinderPath) : null;
                breakHammerBone = data.hammer != null ? breakFrame.Find(data.hammer.paths[0]) : null;
                barrelClosedLocal = data.open.rotations[System.Array.IndexOf(data.open.paths, data.barrelPath)][0];
                barrelCenterOfMass = MeshCenterIn(barrelBone);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] {firearmController.Item.LocalizedShortName()}: {(data.revolver ? "crane" : "barrel")} '{barrelBone.name}', {data.openDegrees:0.#} deg about {data.hingeAxis.ToString("F2")}, "
                        + $"centre of mass {(barrelCenterOfMass * 100f).ToString("F1")} cm off the hinge, open bones {CountFound(breakOpenBones)}/{data.open.paths.Length}, "
                        + $"lever {(data.lever != null ? CountFound(breakLeverBones) + "/" + data.lever.paths.Length : "none")}, chambers {data.chamberCount}"
                        + (data.revolver ? $", cylinder {(breakCylinder != null ? breakCylinder.name : "NOT FOUND")}, ejector {(data.ejector != null ? CountFound(ejectorBones) + "/" + data.ejector.bones.paths.Length + " pushed on " + (ejectorPressBone != null ? ejectorPressBone.name : "NOT FOUND") : "none")}" : "")
                        + (data.hammer != null ? $", hammer {(breakHammerBone != null ? breakHammerBone.name : "NOT FOUND")}" : ""));
                return true;
            }

            private static Transform[] FindKeyedBones(Transform frame, KeyedBones keyed)
            {
                if (keyed == null)
                    return null;
                Transform[] bones = new Transform[keyed.paths.Length];
                for (int bone = 0; bone < bones.Length; bone++)
                    bones[bone] = frame.Find(keyed.paths[bone]);
                return bones;
            }

            private static int CountFound(Transform[] bones)
            {
                int found = 0;
                if (bones != null)
                {
                    foreach (Transform bone in bones)
                    {
                        if (bone != null)
                            found++;
                    }
                }
                return found;
            }

            // The barrels' centre in their bone's frame: their meshes' mesh-local bounds, weighted by volume
            private static Vector3 MeshCenterIn(Transform bone)
            {
                Vector3 sum = Vector3.zero;
                float weight = 0f;
                foreach (MeshFilter meshFilter in bone.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (meshFilter.sharedMesh == null)
                        continue;
                    Bounds bounds = meshFilter.sharedMesh.bounds;
                    Vector3 size = Vector3.Scale(bounds.size, meshFilter.transform.lossyScale);
                    float volume = Mathf.Max(size.x * size.y * size.z, 1e-9f);
                    sum += bone.InverseTransformPoint(meshFilter.transform.TransformPoint(bounds.center)) * volume;
                    weight += volume;
                }
                // No meshes found: assume a barrel's length ahead of the hinge
                return weight > 0f ? sum / weight : new Vector3(0f, -0.25f, 0f);
            }

            internal static bool KeyedPose(KeyedBones keyed, int bone, float share, out Vector3 position, out Quaternion rotation)
            {
                Vector3[] positions = keyed.positions[bone];
                Quaternion[] rotations = keyed.rotations[bone];
                float scaled = Mathf.Clamp01(share) * (positions.Length - 1);
                int index = Mathf.Min(Mathf.FloorToInt(scaled), positions.Length - 2);
                float blend = scaled - index;
                position = Vector3.Lerp(positions[index], positions[index + 1], blend);
                rotation = Quaternion.Slerp(rotations[index], rotations[index + 1], blend);
                return true;
            }

            // Poses the barrels and the lever from our state, only while off their rest pose (at rest the animator's pose is the same)
            private static void PoseBreakAction(BreakData data, BreakState state)
            {
                if (state.open > 0f || state.lever > 0f || state.ejector > 0f || HoldingBarrels || HoldingEjector)
                    state.restFrames = 3;
                else if (state.restFrames > 0)
                    state.restFrames--;
                bool shown = state.restFrames > 0;
                if (shown)
                {
                    for (int bone = 0; bone < breakOpenBones.Length; bone++)
                    {
                        Transform transform = breakOpenBones[bone];
                        if (transform != null && KeyedPose(data.open, bone, state.open / data.openDegrees, out Vector3 position, out Quaternion rotation))
                            transform.SetLocalPositionAndRotation(position, rotation);
                    }
                }
                if (breakLeverBones != null && shown)
                {
                    for (int bone = 0; bone < breakLeverBones.Length; bone++)
                    {
                        Transform transform = breakLeverBones[bone];
                        if (transform != null && KeyedPose(data.lever, bone, state.lever, out Vector3 position, out Quaternion rotation))
                            transform.SetLocalPositionAndRotation(position, rotation);
                    }
                }
                if (ejectorBones != null && shown)
                {
                    for (int bone = 0; bone < ejectorBones.Length; bone++)
                    {
                        Transform transform = ejectorBones[bone];
                        if (transform != null && KeyedPose(data.ejector.bones, bone, state.ejector, out Vector3 position, out Quaternion rotation))
                            transform.SetLocalPositionAndRotation(position, rotation);
                    }
                }
            }

            // The hinge axis in world space (positive = opening), through the barrel bone's origin
            private static Vector3 HingeAxisWorld(BreakData data)
            {
                Transform parent = barrelBone.parent;
                return (parent != null ? parent.rotation : Quaternion.identity) * (barrelClosedLocal * data.hingeAxis);
            }

            // Called from the Player.LateUpdate prefix (ShotgunPatches): after the animator, before Tarkov's IK
            public static void ApplyBreakActionBeforeIk()
            {
                FirearmController firearmController = VRGlobals.firearmController;
                if (!ManualBreakOn(firearmController, out BreakData data) || breakBonesFor != firearmController || barrelBone == null
                    || !breakStates.TryGetValue(firearmController.Item, out BreakState state))
                    return;
                if (!(firearmController.CurrentOperation is FirearmController.Idling))
                {
                    hammerCockFor = null;
                    return;
                }
                PoseBreakAction(data, state);
                CockHammer(firearmController, data, state);
                PlaceBreakProps(data);
                ApplyBreakLeverHand(firearmController, data);
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            // From Tick

            private static bool previousLeverButton;
            private static FirearmController breakMotionFor;
            private static int breakMotionFrame = -10;
            private static Vector3 hingePreviousPosition, hingePreviousVelocity, hingeAcceleration;
            private static Quaternion gunPreviousRotation;
            private static Vector3 gunPreviousTurnRate, gunTurnAcceleration;

            // Returns true when the break-action took the off hand's grip press this frame (the fore-end). tookTrigger = it took the
            // trigger press (a shell in a chamber).
            internal static bool TickBreakAction(bool gripPressed, bool gripReleased, bool triggerPressed, bool triggerReleased, bool handBusy, out bool tookTrigger)
            {
                tookTrigger = false;
                breakGunOpen = false;
                bool pressing = false;
                try
                {
                    return TickBreakActionWith(gripPressed, gripReleased, triggerPressed, triggerReleased, handBusy, out tookTrigger, out pressing);
                }
                finally
                {
                    // Eases the gun hand onto the lever while B is held (the hand: ApplyBreakLeverHand, the finger: DriveBreakLeverFinger)
                    breakLeverPress = Mathf.MoveTowards(breakLeverPress, pressing ? 1f : 0f, Time.deltaTime / Mathf.Max(leverPressTime, 0.001f));
                    if (!pressing && breakLeverPress <= 0f)
                        breakLeverPressFor = null;
                }
            }

            private static bool TickBreakActionWith(bool gripPressed, bool gripReleased, bool triggerPressed, bool triggerReleased, bool handBusy, out bool tookTrigger,
                out bool pressing)
            {
                tookTrigger = false;
                pressing = false;
                FirearmController firearmController = VRGlobals.firearmController;
                if (!VRGlobals.inGame || VRGlobals.vrPlayer == null || VRGlobals.player == null || !ManualBreakOn(firearmController, out BreakData data)
                    || !EnsureBreakBones(firearmController, data))
                {
                    EndBreakHolds();
                    breakMotionFor = null;
                    return false;
                }
                Weapon weapon = firearmController.Item;
                BreakState state = BreakStateOf(weapon);
                // Tarkov's own reload works the barrels with its clip and leaves them shut
                if (firearmController.IsInReloadOperation() && expectedOperation == ExpectedOperation.None)
                {
                    EndBreakHolds();
                    state.open = 0f;
                    state.speed = 0f;
                    state.latched = true;
                    state.lever = 0f;
                    return false;
                }
                if (!(firearmController.CurrentOperation is FirearmController.Idling))
                {
                    breakGunOpen = !state.latched || state.open > 0f;
                    hammerCockFor = null;
                    return false;
                }
                float deltaTime = Mathf.Max(Time.deltaTime, 1e-4f);
                TrackGunMotion(firearmController, deltaTime);

                // The lever is the gun hand's B. Not right after a menu closes (B backed out of it), and not during a malfunction.
                bool leverButton = Handedness.GunHandUpperButton.GetState(SteamVR_Input_Sources.Any) && !MenuExitButtonWait.Blocks
                    && !VRGlobals.menuOpen && !ChestHang.Detached && weapon.MalfState.State == Weapon.EMalfunctionState.None;
                bool leverPressed = leverButton && !previousLeverButton;
                previousLeverButton = leverButton;
                pressing = leverButton && leverHandPress && data.leverPress != null && !Handedness.GunInLeftHand;
                if (pressing)
                    breakLeverPressFor = firearmController;
                if (leverPressed)
                {
                    // The SP-81 has no lever of its own: its barrel just comes free
                    if (data.lever != null)
                        PlayFirstGunSound(firearmController, SoundHand.GunHand, "BarrelLock", "CylinderBtn", "Hammer");
                    PulseGunHand(0.25f, 0.03f);
                    if (state.latched)
                    {
                        state.latched = false;
                        // A revolver's cylinder pops out on the release (the thumb pushes it), a break-action's barrels just come free
                        state.speed = data.revolver ? cylinderKickSpeed : 0f;
                        if (data.revolver)
                        {
                            revolverOpenSoundFor = firearmController;
                            revolverOpenSoundAt = Time.time + cylinderOpenSoundDelay;
                        }
                        // The M32A1's foregrip is on the crane, so the support hand on it takes the crane from here and swings it (the hand
                        // moves onto the clip's hold on the foregrip)
                        bool craneInHand = data.supportOnCrane && !handBusy && !HoldingBarrels && VRGlobals.vrPlayer.isSupporting;
                        if (craneInHand)
                            BeginBarrelHold(firearmController, data, state);
                        if (breakDebugLog)
                            Plugin.MyLog.LogInfo($"[VRBreak] unlatched{(craneInHand ? ", the crane in the support hand" : "")}, chambers {ChambersText(firearmController, weapon)}, models {ChamberModelsText(firearmController.Firearms, weapon)}");
                    }
                }
                // A break-action's lever stays over while open; a revolver's release springs back as the thumb comes off it
                bool leverOver = leverButton || (!state.latched && !data.revolver);
                state.lever = Mathf.MoveTowards(state.lever, leverOver ? 1f : 0f, deltaTime / Mathf.Max(leverTime, 0.001f));
                RevolverOpenSound(firearmController, data, state);

                if (HoldingBarrels)
                    UpdateBarrelHold(firearmController, data, state, gripPressed, gripReleased, leverButton, deltaTime);
                else if (!state.latched)
                    SwingBarrels(firearmController, data, state, leverButton, deltaTime);

                // The off hand's grabs: its trigger takes a shell in a chamber (or a revolver's ejector), its grip takes the fore-end
                bool taken = false;
                bool handFree = !handBusy && !HoldingBarrels && !HoldingBreakCase && !HoldingEjector && !ChestHang.Detached;
                if (triggerPressed && handFree && TryTakeEjector(firearmController, data, state))
                    tookTrigger = true;
                else if (triggerPressed && handFree && TryPluckCase(firearmController, data, state))
                    tookTrigger = true;
                else if (gripPressed && handFree && (!state.latched || state.open > 0f) && OffHandAtForeEnd(data, state))
                {
                    BeginBarrelHold(firearmController, data, state);
                    taken = true;
                }
                if (HoldingBreakCase)
                    UpdateCasePluck(firearmController, data, state, triggerReleased);
                if (HoldingEjector)
                    UpdateEjector(firearmController, data, state, triggerReleased, deltaTime);
                else if (state.ejector > 0f)
                    state.ejector = Mathf.MoveTowards(state.ejector, 0f, deltaTime / Mathf.Max(ejectorReturnTime, 0.001f));
                if (data.revolver && data.ejector == null)
                    TryDumpCases(firearmController, data, state);

                breakGunOpen = !state.latched || state.open > 0f;
                PoseBreakAction(data, state);
                PlaceBreakProps(data);
                UpdateBreakZones(firearmController, data, state);
                return taken;
            }

            // The hinge's acceleration and the gun's turn acceleration, smoothed, so the barrels feel the gun's motion. Measured in the rig's
            // frame: a snap turn or a height reset moves the rig in one frame, which isn't motion of the hand.
            private static Transform BreakMotionFrame => VRGlobals.vrOffsetter != null ? VRGlobals.vrOffsetter.transform : null;

            private static void TrackGunMotion(FirearmController firearmController, float deltaTime)
            {
                Transform rig = BreakMotionFrame;
                Quaternion toRig = rig != null ? Quaternion.Inverse(rig.rotation) : Quaternion.identity;
                Vector3 hinge = toRig * (barrelBone.position - (rig != null ? rig.position : Vector3.zero));
                Quaternion rotation = toRig * (barrelBone.parent != null ? barrelBone.parent.rotation : barrelBone.rotation);
                bool continuous = breakMotionFor == firearmController && Time.frameCount - breakMotionFrame == 1;
                breakMotionFor = firearmController;
                breakMotionFrame = Time.frameCount;
                if (!continuous)
                {
                    hingePreviousPosition = hinge;
                    hingePreviousVelocity = Vector3.zero;
                    hingeAcceleration = Vector3.zero;
                    gunPreviousRotation = rotation;
                    gunPreviousTurnRate = Vector3.zero;
                    gunTurnAcceleration = Vector3.zero;
                    return;
                }
                float smoothing = 1f - Mathf.Exp(-deltaTime / Mathf.Max(barrelMotionSmoothing, 0.001f));
                Vector3 velocity = Vector3.Lerp(hingePreviousVelocity, (hinge - hingePreviousPosition) / deltaTime, smoothing);
                hingeAcceleration = Vector3.Lerp(hingeAcceleration, (velocity - hingePreviousVelocity) / deltaTime, smoothing);
                hingePreviousVelocity = velocity;
                hingePreviousPosition = hinge;
                (rotation * Quaternion.Inverse(gunPreviousRotation)).ToAngleAxis(out float turn, out Vector3 axis);
                if (turn > 180f)
                    turn -= 360f;
                Vector3 turnRate = float.IsNaN(axis.x) || float.IsInfinity(axis.x) ? Vector3.zero : axis * (turn * Mathf.Deg2Rad / deltaTime);
                turnRate = Vector3.Lerp(gunPreviousTurnRate, turnRate, smoothing);
                gunTurnAcceleration = Vector3.Lerp(gunTurnAcceleration, (turnRate - gunPreviousTurnRate) / deltaTime, smoothing);
                gunPreviousTurnRate = turnRate;
                gunPreviousRotation = rotation;
            }

            // Barrels free on the hinge: a rod turning about its end, driven by gravity and the gun's motion
            private static void SwingBarrels(FirearmController firearmController, BreakData data, BreakState state, bool leverHeld, float deltaTime)
            {
                Vector3 axis = HingeAxisWorld(data);
                Vector3 lever = barrelBone.rotation * barrelCenterOfMass;
                Vector3 across = Vector3.ProjectOnPlane(lever, axis);
                float inertia = Mathf.Max((data.revolver ? cylinderInertia : 4f / 3f) * across.sqrMagnitude, 1e-4f);
                Transform rig = BreakMotionFrame;
                Quaternion fromRig = rig != null ? rig.rotation : Quaternion.identity;
                Vector3 pull = Physics.gravity * barrelGravity - fromRig * hingeAcceleration * barrelInertia;
                float acceleration = Vector3.Dot(Vector3.Cross(lever, pull), axis) / inertia - Vector3.Dot(fromRig * gunTurnAcceleration, axis) * barrelInertia;
                if (data.holdsOpen && state.open >= data.openDegrees - craneDetentDegrees)
                    acceleration += craneDetent * Physics.gravity.magnitude * barrelGravity * across.magnitude / inertia;
                float speed = state.speed * Mathf.Deg2Rad;
                speed += acceleration * deltaTime;
                speed *= Mathf.Exp(-barrelFriction * deltaTime);
                state.speed = speed * Mathf.Rad2Deg;
                MoveBarrels(firearmController, data, state, state.open + state.speed * deltaTime, leverHeld, SoundHand.GunBody);
            }

            // Sets the barrels to that angle. At the open stop they bounce (with its knock); shut with the lever released, they latch.
            private static void MoveBarrels(FirearmController firearmController, BreakData data, BreakState state, float open, bool leverHeld, SoundHand hand)
            {
                float before = state.open;
                if (open < data.openDegrees * openSoundRearm)
                    state.openSounded = false;
                if (open >= data.openDegrees)
                {
                    open = data.openDegrees;
                    if (state.speed > 0f)
                    {
                        if (state.speed >= barrelStopSound && before < data.openDegrees)
                            OpenStopHit(firearmController, data, state, hand, state.speed);
                        state.speed = data.holdsOpen ? 0f : -state.speed * barrelBounce;
                    }
                }
                if (open <= 0f)
                {
                    open = 0f;
                    if (state.speed < 0f)
                        state.speed = 0f;
                    if (!leverHeld && !state.latched)
                    {
                        state.open = 0f;
                        Latch(firearmController, state, hand);
                        return;
                    }
                }
                state.open = open;
            }

            // Hitting the open stop: the knock plays once per opening (bouncing off it and back is the same stop), and the hand feels every hit.
            // A revolver's swing-out sound is not the stop's; it plays as the cylinder starts out (RevolverOpenSound).
            private static void OpenStopHit(FirearmController firearmController, BreakData data, BreakState state, SoundHand hand, float speed)
            {
                Pulse(hand.Seating, Mathf.Clamp01(speed / 600f) * 0.6f + 0.2f, 0.04f);
                if (state.openSounded || data.revolver)
                    return;
                state.openSounded = true;
                PlayFirstGunSound(firearmController, hand, "BarrelOpen");
            }

            // A revolver's swing-out sound after the release. In Tarkov's open clips the Rhino's and MTs-255's CylinderOpen plays 0.1 s after
            // CylinderBtn, as the cylinder starts out. The RSh-12 has none: its PurgeDrumOpen is switched off in every clip.
            public static float cylinderOpenSoundDelay = 0.1f;  // s after the release
            private static FirearmController revolverOpenSoundFor;
            private static float revolverOpenSoundAt;

            private static void RevolverOpenSound(FirearmController firearmController, BreakData data, BreakState state)
            {
                if (revolverOpenSoundFor == null)
                    return;
                // The M32A1's crane plays it as the hand swings it out, not on the release, and not at all if it went back in
                if (revolverOpenSoundFor == firearmController && data.holdsOpen)
                {
                    if (state.latched)
                    {
                        revolverOpenSoundFor = null;
                        return;
                    }
                    if (state.open < data.openDegrees * craneOpenSoundAt)
                        return;
                }
                else if (Time.time < revolverOpenSoundAt)
                    return;
                if (revolverOpenSoundFor == firearmController)
                    PlayFirstGunSound(firearmController, SoundHand.GunBody, "CylinderOpen");
                revolverOpenSoundFor = null;
            }

            private static void Latch(FirearmController firearmController, BreakState state, SoundHand hand)
            {
                state.latched = true;
                state.speed = 0f;
                PlayFirstGunSound(firearmController, hand, "BarrelClose", "CylinderClose", "ReloadEnd");
                Pulse(hand.Seating, 0.6f, 0.05f);
                if (IsRevolver(firearmController.Item))
                {
                    if (revolverIndexToLiveRound || firearmController.Item.GetCurrentMagazine() is SpringDrivenCylinder)
                        TurnCylinderToLiveRound(firearmController);
                    SetBreakAnimatorCounts(firearmController);
                }
                // The SP-81's hammer is cocked once the barrel is shut, as its clip does
                if (breakHammerBone != null && breakBonesFor == firearmController && !firearmController.Item.Armed)
                {
                    hammerCockFor = firearmController;
                    hammerCockStart = Time.time + hammerCockDelay;
                    hammerCockSounded = false;
                }
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] latched shut, chambers {ChambersText(firearmController, firearmController.Item)}{(hammerCockFor == firearmController ? ", cocking the hammer" : "")}");
            }

            private static FirearmController hammerCockFor;
            private static float hammerCockStart;
            private static bool hammerCockSounded;

            // Moves the hammer from down to cocked after the latch (after the animator), then sets Tarkov's own state with
            // FirearmController.SetArm: the animator's armed pose and Weapon.Armed, as the clip's Arm event sets them.
            // If a shot or any other Tarkov operation comes first, it isn't cocked (the gun fires as it is).
            private static void CockHammer(FirearmController firearmController, BreakData data, BreakState state)
            {
                if (hammerCockFor == null)
                    return;
                if (hammerCockFor != firearmController || breakHammerBone == null || data.hammer == null || !state.latched || firearmController.Item.Armed)
                {
                    hammerCockFor = null;
                    return;
                }
                float share = (Time.time - hammerCockStart) / Mathf.Max(hammerCockTime, 0.001f);
                if (share < 0f)
                    return;
                if (!hammerCockSounded)
                {
                    hammerCockSounded = true;
                    PlayFirstGunSound(firearmController, SoundHand.GunHand, "Arm");
                    PulseGunHand(0.2f, 0.02f);
                }
                if (share >= 1f)
                {
                    firearmController.SetArm(true);
                    hammerCockFor = null;
                    return;
                }
                KeyedPose(data.hammer, 0, Mathf.SmoothStep(0f, 1f, share), out Vector3 position, out Quaternion rotation);
                breakHammerBone.SetLocalPositionAndRotation(position, rotation);
            }

            // On closing, turns the cylinder (and the animator's cylinder) so the chamber that fires next is the first live round from there on.
            // The chamber that fires next is the current camora in single action, the one after it in double action
            // (Tarkov's GetCamoraFireOrLoadStartIndex).
            private static void TurnCylinderToLiveRound(FirearmController firearmController)
            {
                Weapon weapon = firearmController.Item;
                if (!(weapon.GetCurrentMagazine() is CylinderMagazine cylinder) || cylinder.MaxCount == 0)
                    return;
                bool singleAction = !weapon.CylinderHammerClosed;
                int count = cylinder.MaxCount;
                // A spring-driven cylinder fires the current chamber either way and only counts up, so it is wound back to its first live round
                bool spring = cylinder is SpringDrivenCylinder;
                int fires = spring ? 0 : cylinder.GetCamoraFireOrLoadStartIndex(singleAction);
                for (int step = 0; step < count; step++)
                {
                    int camora = (fires + step) % count;
                    if (!(cylinder.Camoras[camora].ContainedItem is Ammo round) || round.IsUsed)
                        continue;
                    int current = singleAction || spring ? camora : (camora - 1 + count) % count;
                    if (current == cylinder.CurrentCamoraIndex)
                        return;
                    cylinder.SetCurrentCamoraIndex(current);
                    firearmController.FirearmsAnimator?.SetCamoraIndex(current);
                    if (breakDebugLog)
                        Plugin.MyLog.LogInfo($"[VRBreak] cylinder {(spring ? "wound" : "turned")}: chamber {camora} fires next ({(spring ? "spring" : singleAction ? "single action" : "double action")}, current {current})");
                    return;
                }
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            // The off hand on the fore-end

            private static FirearmController barrelsHeldFor;
            private static Transform barrelRider;               // our transform on the barrel bone, which the hand latches onto
            private static Vector3 barrelAcrossAtGrab;          // the controller about the hinge at the grab, in the barrel's parent frame
            private static float barrelOpenAtGrab;
            private static bool barrelHandOnRightArm;

            // True when the palm is at the fore-end: where the close clip's fingers close on it, with the barrels at their current angle
            private static bool OffHandAtForeEnd(BreakData data, BreakState state)
            {
                Vector3 grab = ForeEndGrabWorld(data, OffHandOnRightArm);
                return (OffHandPalm() - grab).sqrMagnitude <= foreEndGrabRadius * foreEndGrabRadius;
            }

            private static Vector3 ForeEndGrabWorld(BreakData data, bool rightArm)
            {
                if (!data.revolver)
                    return barrelBone.TransformPoint(MirroredForArm(data.foreEnd.grabPoint, rightArm));
                Vector3 grab = barrelBone.TransformPoint(data.foreEnd.grabPoint);
                return rightArm ? MirroredThroughCylinder(grab) : grab;
            }

            private static Vector3 MirroredForArm(Vector3 pointInBarrel, bool rightArm) => rightArm ? new Vector3(-pointInBarrel.x, pointInBarrel.y, pointInBarrel.z) : pointInBarrel;

            // A revolver's hold for the right arm is mirrored through the plane along the gun that contains the cylinder's axis: the cylinder
            // swings out to the side, away from the gun's own centre plane. The weapon bone's x is the gun's right.
            private static Vector3 MirroredThroughCylinder(Vector3 world)
            {
                Vector3 normal = breakFrame.right;
                Vector3 point = breakCylinder != null ? breakCylinder.position : barrelBone.position;
                return world - 2f * Vector3.Dot(world - point, normal) * normal;
            }

            private static Quaternion MirroredThroughCylinder(Quaternion world)
            {
                Quaternion local = Quaternion.Inverse(breakFrame.rotation) * world;
                return TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(local, Vector3.right, rightToLeft: false, out Quaternion mirrored)
                    ? breakFrame.rotation * mirrored : world;
            }

            private static void BeginBarrelHold(FirearmController firearmController, BreakData data, BreakState state)
            {
                HoldingBarrels = true;
                barrelsHeldFor = firearmController;
                barrelHandOnRightArm = OffHandOnRightArm;
                barrelOpenAtGrab = state.open;
                state.speed = 0f;
                Transform controller = HandRig(gunHand: false);
                barrelAcrossAtGrab = AcrossBarrelHinge(data, controller.position);
                if (barrelRider == null)
                    barrelRider = new GameObject("VRBarrelHand").transform;
                barrelRider.SetParent(barrelBone, worldPositionStays: false);
                barrelRider.SetPositionAndRotation(controller.position, controller.rotation);
                // Move the hand onto the clip's hold on the fore-end; from there it rides the barrels
                LatchHand(barrelRider, gunHand: false);
                ForeEndHoldWorld(data, barrelHandOnRightArm, out Vector3 palmPosition, out Quaternion palmRotation);
                SnapHandTo(barrelRider, palmPosition, palmRotation);
                Pulse(SoundHand.OffHand, 0.3f, 0.03f);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] fore-end taken, {state.open:0.#} deg open");
            }

            // The clip's palm on the fore-end in world space. For the right arm it is mirrored through the gun's centre plane.
            private static void ForeEndHoldWorld(BreakData data, bool rightArm, out Vector3 position, out Quaternion rotation)
            {
                Vector3 local = data.foreEnd.position;
                Quaternion localRotation = data.foreEnd.rotation;
                if (data.revolver)
                {
                    position = barrelBone.position + barrelBone.rotation * local;
                    rotation = barrelBone.rotation * localRotation;
                    if (rightArm)
                    {
                        position = MirroredThroughCylinder(position);
                        rotation = MirroredThroughCylinder(rotation);
                    }
                    return;
                }
                if (rightArm)
                {
                    local.x = -local.x;
                    if (TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(localRotation, Vector3.right, rightToLeft: false, out Quaternion mirrored))
                        localRotation = mirrored;
                }
                position = barrelBone.position + barrelBone.rotation * local;
                rotation = barrelBone.rotation * localRotation;
            }

            // A world point relative to the hinge, in the barrel's parent frame (so turning the gun doesn't count), projected off the hinge axis
            private static Vector3 AcrossBarrelHinge(BreakData data, Vector3 world)
            {
                Transform parent = barrelBone.parent;
                Vector3 axis = barrelClosedLocal * data.hingeAxis;
                Vector3 local = parent != null ? parent.InverseTransformPoint(world) : world;
                return Vector3.ProjectOnPlane(local - barrelBone.localPosition, axis);
            }

            private static void UpdateBarrelHold(FirearmController firearmController, BreakData data, BreakState state, bool gripPressed, bool gripReleased, bool leverHeld,
                float deltaTime)
            {
                // With "Toggle Gun Grip", a press lets go, as on the foregrip
                bool toggle = VRSettings.GetGunGripToggle();
                bool letGo = toggle ? gripPressed : gripReleased;
                if (firearmController != barrelsHeldFor || barrelHandOnRightArm != OffHandOnRightArm || ChestHang.Detached || VRGlobals.menuOpen)
                {
                    LetGoOfBarrels(keepSupport: false);
                    return;
                }
                Vector3 across = AcrossBarrelHinge(data, HandRig(gunHand: false).position);
                float open = state.open;
                if (across.sqrMagnitude > 1e-6f && barrelAcrossAtGrab.sqrMagnitude > 1e-6f)
                    open = barrelOpenAtGrab + Vector3.SignedAngle(barrelAcrossAtGrab, across, barrelClosedLocal * data.hingeAxis);
                float before = state.open;
                float target = Mathf.Clamp(open, 0f, data.openDegrees);
                // The speed carries on after the let-go (a push up and away shuts them)
                state.speed = Mathf.Lerp(state.speed, (target - before) / deltaTime, 1f - Mathf.Exp(-deltaTime / 0.03f));
                if (open <= 0f && !leverHeld && !letGo)
                {
                    // Home with the lever released: latch, and the hand stays on as the support grip
                    state.open = 0f;
                    Latch(firearmController, state, SoundHand.OffHand);
                    LetGoOfBarrels(keepSupport: toggle || Handedness.OffHandGrip.state);
                    return;
                }
                state.open = target;
                if (state.open < data.openDegrees * openSoundRearm)
                    state.openSounded = false;
                if (state.open >= data.openDegrees && before < data.openDegrees && state.speed > barrelStopSound)
                    OpenStopHit(firearmController, data, state, SoundHand.OffHand, state.speed);
                if (letGo)
                    LetGoOfBarrels(keepSupport: false);
            }

            private static void LetGoOfBarrels(bool keepSupport)
            {
                if (!HoldingBarrels)
                    return;
                HoldingBarrels = false;
                barrelsHeldFor = null;
                ReleaseHandLatch();
                // Two-handed from here (VRPlayerManager's own entry, handLock: opening the grip lets go). The releasing latch hands the
                // arm to Tarkov's IK once it sees it (UpdateHandLatch).
                if (keepSupport && VRGlobals.vrPlayer != null && !Handedness.GunInLeftHand)
                    VRGlobals.vrPlayer.TakeSupportGrip();
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            // A shell drawn out of a chamber

            private static FirearmController caseFor;
            private static int caseChamber;
            private static bool caseSpent;                      // a fired case (Tarkov's shell model), else the live round's model
            private static AmmoTemplate caseTemplate;           // the fired case's template, for Weapon.ShellsInChambers if it goes back
            private static Ammo caseRound;                      // the live round, which stays in its chamber in the inventory
            private static GameObject caseProp;
            private static Transform caseRestParent;
            private static Vector3 caseRestLocalPosition;
            private static Quaternion caseRestLocalRotation;
            private static Vector3 caseRestInBarrelPosition;    // where it sits in its chamber, in the barrel bone's frame
            private static Quaternion caseRestInBarrelRotation;
            private static Vector3 caseBoreInBarrel;            // the direction out of the chamber
            private static float caseLength;
            private static float caseOut;
            internal static bool caseFree;
            private static float caseFreeTime;
            private static Vector3 caseHandAtGrab;              // the controller at the grab, in the barrel bone's frame
            private static Vector3 caseInPalmPosition;          // as it came free, in the palm's frame
            private static Quaternion caseInPalmRotation;
            private static Transform caseRider;
            private static bool caseOnRightArm;
            // The other barrel's fired case, drawn together with the first when both fired. It moves along its own bore, then keeps its
            // place beside the first in the hand.
            private static GameObject case2Prop;
            private static int case2Chamber = -1;
            private static AmmoTemplate case2Template;
            private static Transform case2RestParent;
            private static Vector3 case2RestLocalPosition;
            private static Quaternion case2RestLocalRotation;
            private static Vector3 case2RestInBarrelPosition;
            private static Quaternion case2RestInBarrelRotation;
            private static Vector3 case2InFirstPosition;
            private static Quaternion case2InFirstRotation;

            private static bool TryPluckCase(FirearmController firearmController, BreakData data, BreakState state)
            {
                if (state.open < data.openDegrees * barrelOpenEnough)
                    return false;
                Weapon weapon = firearmController.Item;
                Firearms firearms = firearmController.Firearms;
                if (firearms == null)
                    return false;
                Vector3 palm = OffHandPalm();
                int best = -1;
                // On a revolver, anywhere around the back of the cylinder takes the fired case nearest the hand
                bool atBack = data.revolver && AtCylinderBack(firearms, weapon, palm, 0f);
                float bestDistance = atBack ? float.MaxValue : caseGrabRadius * caseGrabRadius;
                bool bestSpent = false;
                GameObject bestModel = null;
                for (int chamber = 0; chamber < BreakSlots(weapon).Length; chamber++)
                {
                    GameObject model = ChamberModel(firearms, weapon, chamber, out bool spent);
                    // On a revolver only fired cases: the chambers sit a finger apart, and the live rounds stay put
                    if (model == null || (data.revolver && !spent))
                        continue;
                    float distance = (model.transform.position - palm).sqrMagnitude;
                    if (distance <= bestDistance)
                    {
                        best = chamber;
                        bestDistance = distance;
                        bestSpent = spent;
                        bestModel = model;
                    }
                }
                if (best < 0)
                    return false;
                // Take the model out of Tarkov's arrays: it is ours until it goes back in or out
                if (bestSpent)
                {
                    caseTemplate = weapon.ShellsInChambers != null && best < weapon.ShellsInChambers.Length ? weapon.ShellsInChambers[best] : null;
                    firearms._shellsInShellPort[best] = null;
                    caseRound = null;
                }
                else
                {
                    firearms._ammoObjectInWeapon[best] = null;
                    caseRound = BreakSlots(weapon)[best].ContainedItem as Ammo;
                    caseTemplate = null;
                }
                caseFor = firearmController;
                caseChamber = best;
                caseSpent = bestSpent;
                caseProp = bestModel;
                caseRestParent = bestModel.transform.parent;
                caseRestLocalPosition = bestModel.transform.localPosition;
                caseRestLocalRotation = bestModel.transform.localRotation;
                caseRestInBarrelPosition = barrelBone.InverseTransformPoint(bestModel.transform.position);
                caseRestInBarrelRotation = Quaternion.Inverse(barrelBone.rotation) * bestModel.transform.rotation;
                Transform chamberBone = ChamberBone(firearms, best);
                caseBoreInBarrel = Quaternion.Inverse(barrelBone.rotation) * (chamberBone != null ? chamberBone.up : barrelBone.up);
                MeasureLongAxis(bestModel, out _, out _, out caseLength);
                caseLength = Mathf.Clamp(caseLength, 0.03f, 0.12f);
                caseOut = 0f;
                caseFree = false;
                caseOnRightArm = OffHandOnRightArm;
                Transform controller = HandRig(gunHand: false);
                caseHandAtGrab = barrelBone.InverseTransformPoint(controller.position);
                bestModel.transform.SetParent(barrelBone, worldPositionStays: true);
                case2Prop = null;
                case2Chamber = -1;
                // A revolver's cases come out one at a time; an empty cylinder is the ejector's job
                if (bestSpent && !data.revolver)
                {
                    for (int chamber = 0; chamber < BreakSlots(weapon).Length; chamber++)
                    {
                        if (chamber == best)
                            continue;
                        GameObject other = ChamberModel(firearms, weapon, chamber, out bool otherSpent);
                        if (other == null || !otherSpent)
                            continue;
                        case2Prop = other;
                        case2Chamber = chamber;
                        case2Template = weapon.ShellsInChambers != null && chamber < weapon.ShellsInChambers.Length ? weapon.ShellsInChambers[chamber] : null;
                        firearms._shellsInShellPort[chamber] = null;
                        case2RestParent = other.transform.parent;
                        case2RestLocalPosition = other.transform.localPosition;
                        case2RestLocalRotation = other.transform.localRotation;
                        case2RestInBarrelPosition = barrelBone.InverseTransformPoint(other.transform.position);
                        case2RestInBarrelRotation = Quaternion.Inverse(barrelBone.rotation) * other.transform.rotation;
                        other.transform.SetParent(barrelBone, worldPositionStays: true);
                        break;
                    }
                }
                // The hand rides the shell along the bore from where it took it
                if (caseRider == null)
                    caseRider = new GameObject("VRCaseHand").transform;
                caseRider.SetParent(bestModel.transform, worldPositionStays: false);
                caseRider.SetPositionAndRotation(controller.position, controller.rotation);
                LatchHand(caseRider, gunHand: false);
                SetLatchPivot(bestModel.transform.position);
                HoldingBreakCase = true;
                if (data.revolver)
                {
                    if (bestSpent)
                        PlayFirstGunSound(firearmController, SoundHand.OffHand, "ShellRemove", "ShellOut", "CylinderPurgeOne", "ShellExtract");
                    else
                        PlayFirstGunSound(firearmController, SoundHand.OffHand, "AmmoOut", "ShellExtract", "ShellOut");
                }
                else if (case2Prop == null || !PlayFirstGunSound(firearmController, SoundHand.OffHand, "UnloadBooth"))
                    PlayFirstGunSound(firearmController, SoundHand.OffHand, "UnloadOne", "ShellOut", "ShellUnload");
                Pulse(SoundHand.OffHand, 0.3f, 0.03f);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] took the {(bestSpent ? "fired case" : "live round")} in chamber {best}{(case2Prop != null ? " and the one in chamber " + case2Chamber : "")} ({caseLength * 100f:0.#} cm long)");
                return true;
            }

            // The chamber's shell model: the fired case Tarkov keeps on its shell port, or the live round on the chamber bone
            private static GameObject ChamberModel(Firearms firearms, Weapon weapon, int chamber, out bool spent)
            {
                spent = false;
                if (BreakSlots(weapon)[chamber].ContainedItem is Ammo round && !round.IsUsed)
                {
                    AmmoPoolObject live = firearms._ammoObjectInWeapon != null && chamber < firearms._ammoObjectInWeapon.Length ? firearms._ammoObjectInWeapon[chamber] : null;
                    return live != null ? live.gameObject : null;
                }
                AmmoPoolObject fired = firearms._shellsInShellPort != null && chamber < firearms._shellsInShellPort.Length ? firearms._shellsInShellPort[chamber] : null;
                spent = fired != null;
                return fired != null ? fired.gameObject : null;
            }

            private static Transform ChamberBone(Firearms firearms, int chamber)
            {
                Transform[] bones = firearms._patronInWeaponTransforms;
                return bones != null && chamber < bones.Length ? bones[chamber] : null;
            }

            // Empty for a new shell: no round in it in the inventory and no fired case to draw out. A fired case Tarkov counts without a model
            // (after a re-equip) can't be drawn, so it doesn't block the chamber; loading the chamber clears it.
            private static bool ChamberEmpty(Firearms firearms, Weapon weapon, int chamber)
            {
                if (BreakSlots(weapon)[chamber].ContainedItem != null)
                    return false;
                return firearms._shellsInShellPort == null || chamber >= firearms._shellsInShellPort.Length || firearms._shellsInShellPort[chamber] == null;
            }

            // Empty, and in a spring-driven cylinder also next to the rounds in it, or the lowest empty one if it has none
            // (the M32A1 fires upward from where it is wound, and an empty chamber stops it)
            private static bool ChamberTakesRound(Firearms firearms, Weapon weapon, int chamber)
            {
                if (!ChamberEmpty(firearms, weapon, chamber))
                    return false;
                if (!(weapon.GetCurrentMagazine() is SpringDrivenCylinder cylinder))
                    return true;
                Slot[] camoras = cylinder.Camoras;
                int lowest = -1, highest = -1;
                for (int camora = 0; camora < camoras.Length; camora++)
                {
                    if (camoras[camora].ContainedItem is Ammo round && !round.IsUsed)
                    {
                        if (lowest < 0)
                            lowest = camora;
                        highest = camora;
                    }
                }
                if (lowest >= 0)
                    return chamber == lowest - 1 || chamber == highest + 1;
                for (int camora = 0; camora < camoras.Length; camora++)
                {
                    if (ChamberEmpty(firearms, weapon, camora))
                        return camora == chamber;
                }
                return false;
            }

            private static void ForgetFiredCase(Weapon weapon, int chamber)
            {
                if (weapon.ShellsInChambers != null && chamber < weapon.ShellsInChambers.Length)
                    weapon.ShellsInChambers[chamber] = null;
            }

            private static void UpdateCasePluck(FirearmController firearmController, BreakData data, BreakState state, bool released)
            {
                if (caseProp == null || caseFor != firearmController || caseOnRightArm != OffHandOnRightArm || VRGlobals.menuOpen)
                {
                    EndCasePluck(putBack: true);
                    return;
                }
                if (!caseFree)
                {
                    Vector3 moved = barrelBone.InverseTransformPoint(HandRig(gunHand: false).position) - caseHandAtGrab;
                    caseOut = Mathf.Clamp(Vector3.Dot(moved, caseBoreInBarrel), 0f, caseLength);
                    if (released)
                    {
                        EndCasePluck(putBack: true);
                        return;
                    }
                    if (caseOut >= caseLength * caseFreeShare)
                    {
                        // Out of the chamber: now in the hand, easing into the clip's hold
                        caseFree = true;
                        caseFreeTime = Time.time;
                        ReleaseHandLatch();
                        caseProp.transform.SetParent(null, worldPositionStays: true);
                        Transform palm = ArmPalm(caseOnRightArm);
                        if (palm != null)
                            GetPoseIn(palm, caseProp.transform, out caseInPalmPosition, out caseInPalmRotation);
                        if (case2Prop != null)
                        {
                            Transform first = caseProp.transform;
                            case2InFirstPosition = Quaternion.Inverse(first.rotation) * (case2Prop.transform.position - first.position);
                            case2InFirstRotation = Quaternion.Inverse(first.rotation) * case2Prop.transform.rotation;
                            case2Prop.transform.SetParent(null, worldPositionStays: true);
                        }
                        Pulse(SoundHand.OffHand, 0.25f, 0.03f);
                        if (breakDebugLog)
                            Plugin.MyLog.LogInfo($"[VRBreak] {(caseSpent ? "fired case" : "live round")} out of chamber {caseChamber}");
                    }
                    return;
                }
                if (!released)
                    return;
                if (caseSpent)
                {
                    GameObject prop = caseProp;
                    GameObject second = case2Prop;
                    ClearCase();
                    PlayFirstGunSound(firearmController, SoundHand.OffHand, "ShellThrow");
                    Vector3 velocity = HandWorldVelocity(gunHand: false);
                    ThrowJamCase(prop.GetComponent<AmmoPoolObject>(), firearmController.Firearms, velocity);
                    if (second != null)
                        ThrowJamCase(second.GetComponent<AmmoPoolObject>(), firearmController.Firearms, velocity + Random.insideUnitSphere * 0.2f);
                    if (breakDebugLog)
                        Plugin.MyLog.LogInfo($"[VRBreak] fired case{(second != null ? "s" : "")} thrown");
                    return;
                }
                LetGoOfLiveRound(firearmController, state);
            }

            // A live round let go outside its chamber goes into an empty chamber at its mouth, into the rig at the chest pouch, or else to the ground
            private static void LetGoOfLiveRound(FirearmController firearmController, BreakState state)
            {
                Weapon weapon = firearmController.Item;
                Firearms firearms = firearmController.Firearms;
                Ammo round = caseRound;
                GameObject prop = caseProp;
                int from = caseChamber;
                ClearCase();
                if (round == null || prop == null || round.Parent == null || !ReferenceEquals(round.Parent.Container, BreakSlots(weapon)[from]))
                {
                    if (prop != null)
                        AssetPoolObject.ReturnToPool(prop);
                    return;
                }
                InventoryController inventoryController = VRGlobals.player.InventoryController;
                int into = -1;
                if (state.open >= BreakOpenEnoughDegrees(firearmController))
                {
                    float nearest = caseGrabRadius * caseGrabRadius;
                    for (int chamber = 0; chamber < BreakSlots(weapon).Length; chamber++)
                    {
                        Transform bone = ChamberBone(firearms, chamber);
                        float distance = bone != null ? (prop.transform.position - bone.position).sqrMagnitude : float.MaxValue;
                        if ((chamber == from || ChamberTakesRound(firearms, weapon, chamber)) && distance <= nearest)
                        {
                            into = chamber;
                            nearest = distance;
                        }
                    }
                }
                if (into == from)
                {
                    // Back where it was: no inventory move needed
                    ReturnLiveModel(firearms, from, prop);
                    return;
                }
                Vector3 position = prop.transform.position;
                Quaternion rotation = prop.transform.rotation;
                AssetPoolObject.ReturnToPool(prop);
                SoundHand hand = SoundHand.OffHand;
                if (into >= 0)
                {
                    var move = ItemManipulator.Move(round, BreakSlots(weapon)[into].CreateItemAddress(), inventoryController, simulate: true);
                    if (!move.Failed)
                    {
                        RunBreakMove(firearmController, move.Cast<MoveResult, IItemOperationResult>(), "live round into chamber " + into, succeeded =>
                        {
                            if (firearms != null && BreakSlots(weapon)[into].ContainedItem is Ammo moved)
                            {
                                ForgetFiredCase(weapon, into);
                                firearms.SetRoundIntoWeapon(moved, into);
                            }
                            else if (!succeeded && firearms != null)
                                firearms.SetRoundIntoWeapon(round, from);
                            SetBreakAnimatorCounts(firearmController);
                        });
                        if (IsRevolver(weapon))
                            PlayFirstGunSound(firearmController, hand, "ShellInsert", "AmmoIn");
                        else
                            PlayFirstGunSound(firearmController, hand, "LoadOne", "ShellLoad");
                        return;
                    }
                }
                if (InChestPouchZone(OffHandPalm()) && TryBuildRoundToRig(round, out var rigMove))
                {
                    RunBreakMove(firearmController, rigMove.Cast<MoveResult, IItemOperationResult>(), "live round into the rig", succeeded =>
                    {
                        if (!succeeded && firearms != null)
                            firearms.SetRoundIntoWeapon(round, from);
                        SetBreakAnimatorCounts(firearmController);
                    });
                    PlayFirstGunSound(firearmController, SoundHand.OffHandAlone, "ShellOutRig");
                    return;
                }
                GameWorld gameWorld = Singleton<GameWorld>.Instance;
                if (gameWorld != null && !(gameWorld is HideoutGameWorld) && inventoryController.CanThrow(round))
                {
                    pendingDropMagazine = round;
                    pendingDropPosition = position;
                    pendingDropRotation = rotation;
                    pendingDropVelocity = HandWorldVelocity(gunHand: false);
                    pendingDropAngularVelocity = null;
                    pendingDropDeadline = Time.time + 3f;
                    breakMoveInFlight = true;
                    inventoryController.ThrowItem(round, false, result =>
                    {
                        breakMoveInFlight = false;
                        if (!result.Succeed && firearms != null && BreakSlots(weapon)[from].ContainedItem == round)
                            firearms.SetRoundIntoWeapon(round, from);
                        SetBreakAnimatorCounts(firearmController);
                        if (breakDebugLog)
                            Plugin.MyLog.LogInfo($"[VRBreak] live round {(result.Succeed ? "dropped" : "NOT dropped: " + result.Error)}");
                    });
                    return;
                }
                // Nowhere for it: back in its chamber
                if (firearms != null)
                    firearms.SetRoundIntoWeapon(round, from);
            }

            private static float BreakOpenEnoughDegrees(FirearmController firearmController)
            {
                return TryGetBreakAction(firearmController, out BreakData data) ? data.openDegrees * barrelOpenEnough : 0f;
            }

            private static void ReturnLiveModel(Firearms firearms, int chamber, GameObject prop)
            {
                if (firearms == null || firearms._ammoObjectInWeapon == null || chamber >= firearms._ammoObjectInWeapon.Length || firearms._ammoObjectInWeapon[chamber] != null)
                {
                    AssetPoolObject.ReturnToPool(prop);
                    return;
                }
                Transform bone = ChamberBone(firearms, chamber);
                if (bone == null)
                {
                    AssetPoolObject.ReturnToPool(prop);
                    return;
                }
                Firearms.ParentShellToTransform(prop, bone);
                firearms._ammoObjectInWeapon[chamber] = prop.GetComponent<AmmoPoolObject>();
            }

            // Let go before it was out, or the gun or hand changed: put it back in its chamber as Tarkov had it
            private static void EndCasePluck(bool putBack)
            {
                if (!HoldingBreakCase)
                    return;
                GameObject prop = caseProp;
                FirearmController firearmController = caseFor;
                int chamber = caseChamber;
                bool spent = caseSpent;
                AmmoTemplate template = caseTemplate;
                Transform restParent = caseRestParent;
                Vector3 restPosition = caseRestLocalPosition;
                Quaternion restRotation = caseRestLocalRotation;
                GameObject second = case2Prop;
                int secondChamber = case2Chamber;
                AmmoTemplate secondTemplate = case2Template;
                Transform secondParent = case2RestParent;
                Vector3 secondPosition = case2RestLocalPosition;
                Quaternion secondRotation = case2RestLocalRotation;
                ClearCase();
                Firearms firearms = firearmController != null ? firearmController.Firearms : null;
                if (second != null)
                {
                    if (putBack && firearms != null && secondParent != null && firearms._shellsInShellPort != null && secondChamber < firearms._shellsInShellPort.Length
                        && firearms._shellsInShellPort[secondChamber] == null)
                    {
                        second.transform.SetParent(secondParent, worldPositionStays: false);
                        second.transform.SetLocalPositionAndRotation(secondPosition, secondRotation);
                        firearms._shellsInShellPort[secondChamber] = second.GetComponent<AmmoPoolObject>();
                        Weapon weapon = firearmController.Item;
                        if (weapon != null && weapon.ShellsInChambers != null && secondChamber < weapon.ShellsInChambers.Length)
                            weapon.ShellsInChambers[secondChamber] = secondTemplate;
                    }
                    else
                        AssetPoolObject.ReturnToPool(second);
                }
                if (prop == null)
                    return;
                if (!putBack || firearms == null || restParent == null)
                {
                    AssetPoolObject.ReturnToPool(prop);
                    return;
                }
                prop.transform.SetParent(restParent, worldPositionStays: false);
                prop.transform.SetLocalPositionAndRotation(restPosition, restRotation);
                if (spent && firearms._shellsInShellPort != null && chamber < firearms._shellsInShellPort.Length && firearms._shellsInShellPort[chamber] == null)
                {
                    firearms._shellsInShellPort[chamber] = prop.GetComponent<AmmoPoolObject>();
                    Weapon weapon = firearmController.Item;
                    if (weapon != null && weapon.ShellsInChambers != null && chamber < weapon.ShellsInChambers.Length)
                        weapon.ShellsInChambers[chamber] = template;
                }
                else if (!spent && firearms._ammoObjectInWeapon != null && chamber < firearms._ammoObjectInWeapon.Length && firearms._ammoObjectInWeapon[chamber] == null)
                    firearms._ammoObjectInWeapon[chamber] = prop.GetComponent<AmmoPoolObject>();
                else
                    AssetPoolObject.ReturnToPool(prop);
                SetBreakAnimatorCounts(firearmController);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] {(spent ? "fired case" : "live round")} left in chamber {chamber}");
            }

            // Done holding it: release the hand, and remove a fired case from the chamber's records for good
            private static void ClearCase()
            {
                if (HoldingBreakCase && !caseFree)
                    ReleaseHandLatch();
                if (caseRider != null && caseProp != null && caseRider.IsChildOf(caseProp.transform))
                    caseRider.SetParent(null, worldPositionStays: true);
                if (handLatch != null && caseProp != null && handLatch.IsChildOf(caseProp.transform))
                    handLatch.SetParent(null, worldPositionStays: true);
                if (caseSpent && caseFor != null && caseFor.Item != null)
                {
                    Weapon weapon = caseFor.Item;
                    if (weapon.ShellsInChambers != null && caseChamber < weapon.ShellsInChambers.Length)
                        weapon.ShellsInChambers[caseChamber] = null;
                    if (case2Prop != null && weapon.ShellsInChambers != null && case2Chamber >= 0 && case2Chamber < weapon.ShellsInChambers.Length)
                        weapon.ShellsInChambers[case2Chamber] = null;
                    SetBreakAnimatorCounts(caseFor);
                }
                HoldingBreakCase = false;
                caseProp = null;
                case2Prop = null;
                case2Chamber = -1;
                case2Template = null;
                caseFor = null;
                caseRound = null;
                caseTemplate = null;
                caseFree = false;
            }

            // Places the drawn shell: on the bore while it comes out, then in the hand, eased onto the clip's hold (mirrored on the right arm)
            private static void PlaceCase(BreakData data)
            {
                if (!HoldingBreakCase || caseProp == null || barrelBone == null)
                    return;
                if (!caseFree)
                {
                    caseProp.transform.SetPositionAndRotation(barrelBone.TransformPoint(caseRestInBarrelPosition + caseBoreInBarrel * caseOut),
                        barrelBone.rotation * caseRestInBarrelRotation);
                    if (case2Prop != null)
                        case2Prop.transform.SetPositionAndRotation(barrelBone.TransformPoint(case2RestInBarrelPosition + caseBoreInBarrel * caseOut),
                            barrelBone.rotation * case2RestInBarrelRotation);
                    return;
                }
                Transform palm = ArmPalm(caseOnRightArm);
                if (palm == null)
                    return;
                Vector3 position = caseInPalmPosition;
                Quaternion rotation = caseInPalmRotation;
                float ease = Mathf.SmoothStep(0f, 1f, (Time.time - caseFreeTime) / Mathf.Max(caseIntoHandTime, 0.001f));
                BreakHold hold = case2Prop != null ? data.pluckPair ?? data.pluckHold : data.pluckHold;
                if (hold != null && BreakShellHoldInPalm(hold, caseProp.transform, caseOnRightArm, out Vector3 holdPosition, out Quaternion holdRotation))
                {
                    position = Vector3.Lerp(caseInPalmPosition, holdPosition, ease);
                    rotation = Quaternion.Slerp(caseInPalmRotation, holdRotation, ease);
                }
                SetPoseIn(palm, caseProp.transform, position, rotation);
                if (case2Prop == null)
                    return;
                // The other case eases from its place beside the first onto the pair hold's second shell (from the both-barrels clip)
                Transform first = caseProp.transform;
                Vector3 besidePosition = first.position + first.rotation * case2InFirstPosition;
                Quaternion besideRotation = first.rotation * case2InFirstRotation;
                if (hold?.second != null && BreakShellHoldInPalm(hold.second, case2Prop.transform, caseOnRightArm, out Vector3 secondPosition, out Quaternion secondRotation))
                {
                    Vector3 heldPosition = palm.position + palm.rotation * secondPosition;
                    Quaternion heldRotation = palm.rotation * secondRotation;
                    case2Prop.transform.SetPositionAndRotation(Vector3.Lerp(besidePosition, heldPosition, ease), Quaternion.Slerp(besideRotation, heldRotation, ease));
                }
                else
                    case2Prop.transform.SetPositionAndRotation(besidePosition, besideRotation);
            }

            // A shell held as a clip holds it. Its model sits on the shell's bone turned 90 deg about x (Firearms.ParentShellToTransform).
            // The other palm's hold is mirrored through a plane along the shell (the M32A1's holds come from its clips' right hand).
            internal static bool BreakShellHoldInPalm(BreakHold hold, Transform prop, bool rightArm, out Vector3 position, out Quaternion rotation)
            {
                position = hold.position;
                rotation = hold.rotation * Quaternion.Euler(90f, 0f, 0f);
                if (rightArm == hold.rightHanded)
                    return true;
                MeasureLongAxis(prop.gameObject, out Vector3 axisInProp, out Vector3 centerInProp, out _);
                Vector3 axis = axisInProp.sqrMagnitude > 1e-6f ? axisInProp.normalized : Vector3.forward;
                Vector3 normal = Vector3.Cross(axis, Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
                return MirrorHold(normal, Vector3.Scale(centerInProp, prop.lossyScale), leftToRight: !hold.rightHanded, ref position, ref rotation);
            }

            // Runs in each pass after the bones are posed: places the drawn case, a new shell on the bore, and the cases on a revolver's ejector
            private static void PlaceBreakProps(BreakData data)
            {
                if (HoldingBreakCase && !caseFree)
                    PlaceCase(data);
                if (breakShellHeld && breakShellOnBore)
                    PlaceBreakShellOnBore();
                PlaceEjectorCases(data);
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            // A revolver's ejector: the off hand's trigger at it takes it, only with no live round left. Pushed in, it carries the fired cases
            // out of their chambers along the bore, and near the end of its travel they come free (Tarkov's shell physics). Let go, it springs home.

            private static FirearmController ejectorFor;
            private static Transform ejectorRider;              // our transform on the part the hand pushes, which the hand latches onto
            private static Vector3 ejectorHandAtGrab;           // the controller at the grab, in the crane's frame
            private static float ejectorShareAtGrab;
            private static bool ejectorOnRightArm;
            private static bool ejectorEjected;                 // this push's cases are out
            private static float ejectorSpeed;                  // m/s along the push, smoothed
            // The fired cases riding out on it: their chambers, and their models' local positions at rest
            private static FirearmController ejectorCasesFor;
            private static readonly List<int> ejectorCaseChambers = new List<int>();
            private static readonly List<Vector3> ejectorCaseRest = new List<Vector3>();

            // Where the clip's fingers press it
            private static Vector3 EjectorGrabWorld(BreakData data, bool rightArm)
            {
                Vector3 grab = ejectorPressBone.TransformPoint(data.ejector.hold.grabPoint);
                return rightArm ? MirroredThroughCylinder(grab) : grab;
            }

            private static void EjectorHoldWorld(BreakData data, bool rightArm, out Vector3 position, out Quaternion rotation)
            {
                position = ejectorPressBone.position + ejectorPressBone.rotation * data.ejector.hold.position;
                rotation = ejectorPressBone.rotation * data.ejector.hold.rotation;
                if (rightArm)
                {
                    position = MirroredThroughCylinder(position);
                    rotation = MirroredThroughCylinder(rotation);
                }
            }

            // Returns true when the off hand's trigger went to the ejector: taken, or refused with live rounds in the cylinder (a double buzz)
            private static bool TryTakeEjector(FirearmController firearmController, BreakData data, BreakState state)
            {
                if (data.ejector == null || ejectorPressBone == null || state.open < data.openDegrees * barrelOpenEnough)
                    return false;
                Vector3 palm = OffHandPalm();
                float distance = (palm - EjectorGrabWorld(data, OffHandOnRightArm)).magnitude;
                if (distance > ejectorGrabRadius)
                    return false;
                Weapon weapon = firearmController.Item;
                Firearms firearms = firearmController.Firearms;
                if (firearms == null)
                    return false;
                // A fired case nearer the palm is drawn instead
                Slot[] slots = BreakSlots(weapon);
                for (int chamber = 0; chamber < slots.Length; chamber++)
                {
                    GameObject model = ChamberModel(firearms, weapon, chamber, out bool spent);
                    if (model != null && spent && (model.transform.position - palm).magnitude < Mathf.Min(distance, caseGrabRadius))
                        return false;
                }
                // With live rounds in it, cases come out one at a time (Tarkov's rule: the ejector only works on an empty cylinder).
                // A fired case the trigger can draw from here is drawn; otherwise the ejector is refused.
                if (weapon.GetCurrentMagazine() is CylinderMagazine cylinder && cylinder.Count > 0)
                {
                    if (FiredCaseInReach(firearms, weapon, palm))
                        return false;
                    Pulse(SoundHand.OffHand, 0.2f, 0.03f);
                    Pulse(SoundHand.OffHand, 0.2f, 0.03f, 0.08f);
                    if (breakDebugLog)
                        Plugin.MyLog.LogInfo($"[VRBreak] ejector refused: {cylinder.Count} live round(s) in the cylinder, chambers {ChambersText(firearmController, weapon)}");
                    return true;
                }
                HoldingEjector = true;
                ejectorFor = firearmController;
                ejectorOnRightArm = OffHandOnRightArm;
                ejectorEjected = false;
                ejectorSpeed = 0f;
                ejectorShareAtGrab = state.ejector;
                Transform controller = HandRig(gunHand: false);
                ejectorHandAtGrab = barrelBone.InverseTransformPoint(controller.position);
                if (ejectorRider == null)
                    ejectorRider = new GameObject("VREjectorHand").transform;
                ejectorRider.SetParent(ejectorPressBone, worldPositionStays: false);
                ejectorRider.SetPositionAndRotation(controller.position, controller.rotation);
                LatchHand(ejectorRider, gunHand: false);
                EjectorHoldWorld(data, ejectorOnRightArm, out Vector3 palmPosition, out Quaternion palmRotation);
                SnapHandTo(ejectorRider, palmPosition, palmRotation);
                // The fired cases ride out on it
                PutEjectorCasesBack();
                ejectorCasesFor = firearmController;
                for (int chamber = 0; chamber < slots.Length; chamber++)
                {
                    AmmoPoolObject fired = firearms._shellsInShellPort != null && chamber < firearms._shellsInShellPort.Length ? firearms._shellsInShellPort[chamber] : null;
                    if (fired == null)
                        continue;
                    ejectorCaseChambers.Add(chamber);
                    ejectorCaseRest.Add(fired.transform.localPosition);
                }
                Pulse(SoundHand.OffHand, 0.3f, 0.03f);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] ejector taken, {ejectorCaseChambers.Count} fired case(s) on it, {Mathf.Max(data.ejector.push.magnitude, ejectorMinPush) * 100f:0.#} cm to push");
                return true;
            }

            private static void UpdateEjector(FirearmController firearmController, BreakData data, BreakState state, bool released, float deltaTime)
            {
                if (firearmController != ejectorFor || ejectorOnRightArm != OffHandOnRightArm || VRGlobals.menuOpen || ejectorPressBone == null
                    || state.open < data.openDegrees * barrelOpenEnough * 0.5f)
                {
                    LetGoOfEjector();
                    return;
                }
                // How far the controller has moved along the fingertip's push path since the grab
                Vector3 push = data.ejector.push;
                float length = Mathf.Max(push.magnitude, ejectorMinPush);
                Vector3 moved = barrelBone.InverseTransformPoint(HandRig(gunHand: false).position) - ejectorHandAtGrab;
                float along = push.sqrMagnitude > 1e-10f ? Vector3.Dot(moved, push.normalized) : 0f;
                float share = Mathf.Clamp01(ejectorShareAtGrab + along / length);
                ejectorSpeed = Mathf.Lerp(ejectorSpeed, (share - state.ejector) * length / deltaTime, 1f - Mathf.Exp(-deltaTime / 0.03f));
                state.ejector = share;
                if (!ejectorEjected && share >= ejectorEjectAt)
                    EjectCases(firearmController);
                if (released)
                    LetGoOfEjector();
            }

            private static void LetGoOfEjector()
            {
                if (!HoldingEjector)
                    return;
                HoldingEjector = false;
                ejectorFor = null;
                ReleaseHandLatch();
                if (ejectorRider != null)
                    ejectorRider.SetParent(null, worldPositionStays: true);
                Pulse(SoundHand.OffHand, 0.12f, 0.02f);
            }

            // Throws the cases out of the back of the cylinder along each chamber's bore at the push speed (Tarkov's shell physics from there)
            // and clears them from the chambers' records, including fired cases Tarkov counts without a model (after a re-equip)
            private static void EjectCases(FirearmController firearmController)
            {
                ejectorEjected = true;
                Weapon weapon = firearmController.Item;
                Firearms firearms = firearmController.Firearms;
                int thrown = 0;
                if (firearms != null && firearms._shellsInShellPort != null)
                {
                    Vector3 gunVelocity = HandWorldVelocity(gunHand: true);
                    foreach (int chamber in ejectorCaseChambers)
                    {
                        if (chamber >= firearms._shellsInShellPort.Length)
                            continue;
                        AmmoPoolObject fired = firearms._shellsInShellPort[chamber];
                        if (fired == null)
                            continue;
                        firearms._shellsInShellPort[chamber] = null;
                        Transform bone = ChamberBone(firearms, chamber);
                        Vector3 bore = bone != null ? bone.up : breakCylinder != null ? breakCylinder.up : barrelBone.up;
                        ThrowJamCase(fired, firearms, gunVelocity + bore * Mathf.Max(ejectorCaseSpeed, ejectorSpeed) + Random.insideUnitSphere * 0.15f);
                        thrown++;
                    }
                }
                Slot[] slots = BreakSlots(weapon);
                for (int chamber = 0; chamber < slots.Length; chamber++)
                {
                    if (slots[chamber].ContainedItem == null)
                        ForgetFiredCase(weapon, chamber);
                }
                ejectorCaseChambers.Clear();
                ejectorCaseRest.Clear();
                ejectorCasesFor = null;
                PlayFirstGunSound(firearmController, SoundHand.OffHand, "Extractor");
                if (thrown > 0)
                    PlayFirstGunSound(firearmController, SoundHand.GunBody, "CylinderPurgeAll", "PurgeShellsOut");
                Pulse(SoundHand.OffHand, 0.4f, 0.04f);
                PulseGunHand(0.3f, 0.03f);
                SetBreakAnimatorCounts(firearmController);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] ejected {thrown} fired case(s) at {ejectorSpeed:0.##} m/s, chambers {ChambersText(firearmController, weapon)}");
            }

            // Moves the fired cases out along their bores with the ejector, until they come free
            private static void PlaceEjectorCases(BreakData data)
            {
                if (ejectorCasesFor == null || data.ejector == null || ejectorBones == null || ejectorBones.Length == 0 || ejectorBones[0] == null)
                    return;
                Firearms firearms = ejectorCasesFor.Firearms;
                if (firearms == null || firearms._shellsInShellPort == null || !breakStates.TryGetValue(ejectorCasesFor.Item, out BreakState state))
                {
                    PutEjectorCasesBack();
                    return;
                }
                Transform extractor = ejectorBones[0];
                KeyedPose(data.ejector.bones, 0, 0f, out Vector3 rest, out _);
                KeyedPose(data.ejector.bones, 0, state.ejector, out Vector3 now, out _);
                Vector3 travel = extractor.parent != null ? extractor.parent.TransformVector(now - rest) : now - rest;
                for (int i = 0; i < ejectorCaseChambers.Count; i++)
                {
                    int chamber = ejectorCaseChambers[i];
                    AmmoPoolObject fired = chamber < firearms._shellsInShellPort.Length ? firearms._shellsInShellPort[chamber] : null;
                    if (fired != null && fired.transform.parent != null)
                        fired.transform.localPosition = ejectorCaseRest[i] + fired.transform.parent.InverseTransformVector(travel);
                }
                if (!HoldingEjector && state.ejector <= 0f)
                    PutEjectorCasesBack();
            }

            private static void PutEjectorCasesBack()
            {
                AmmoPoolObject[] port = ejectorCasesFor != null && ejectorCasesFor.Firearms != null ? ejectorCasesFor.Firearms._shellsInShellPort : null;
                if (port != null)
                {
                    for (int i = 0; i < ejectorCaseChambers.Count; i++)
                    {
                        int chamber = ejectorCaseChambers[i];
                        if (chamber < port.Length && port[chamber] != null)
                            port[chamber].transform.localPosition = ejectorCaseRest[i];
                    }
                }
                ejectorCaseChambers.Clear();
                ejectorCaseRest.Clear();
                ejectorCasesFor = null;
            }

            // No ejector (the M32A1): tipping an empty cylinder back drops its fired cases. Tarkov's rule: all of them from an empty cylinder,
            // otherwise one at a time by hand.
            private static void TryDumpCases(FirearmController firearmController, BreakData data, BreakState state)
            {
                Weapon weapon = firearmController.Item;
                Firearms firearms = firearmController.Firearms;
                if (state.open < data.openDegrees * barrelOpenEnough || firearms == null || firearms._shellsInShellPort == null || HoldingBreakCase
                    || !(weapon.GetCurrentMagazine() is CylinderMagazine cylinder) || cylinder.Count > 0)
                    return;
                Transform first = ChamberBone(firearms, 0);
                Vector3 outOfBack = first != null ? first.up : breakCylinder != null ? breakCylinder.up : barrelBone.up;
                if (Vector3.Angle(outOfBack, Vector3.down) > caseDumpAngle)
                    return;
                Vector3 gunVelocity = HandWorldVelocity(gunHand: true);
                Slot[] slots = BreakSlots(weapon);
                int thrown = 0;
                for (int chamber = 0; chamber < slots.Length && chamber < firearms._shellsInShellPort.Length; chamber++)
                {
                    AmmoPoolObject fired = firearms._shellsInShellPort[chamber];
                    if (fired == null)
                        continue;
                    firearms._shellsInShellPort[chamber] = null;
                    Transform bone = ChamberBone(firearms, chamber);
                    ThrowJamCase(fired, firearms, gunVelocity + (bone != null ? bone.up : outOfBack) * caseDumpSpeed + Random.insideUnitSphere * 0.1f);
                    thrown++;
                }
                if (thrown == 0)
                    return;
                for (int chamber = 0; chamber < slots.Length; chamber++)
                {
                    if (slots[chamber].ContainedItem == null)
                        ForgetFiredCase(weapon, chamber);
                }
                PlayFirstGunSound(firearmController, SoundHand.GunBody, "CylinderPurgeAll", "PurgeShellsOut");
                PulseGunHand(0.3f, 0.04f);
                SetBreakAnimatorCounts(firearmController);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] {thrown} fired case(s) fell out of the tipped cylinder ({Vector3.Angle(outOfBack, Vector3.down):0} deg off straight down), chambers {ChambersText(firearmController, weapon)}");
            }
            internal static BreakData breakShellData;
            internal static bool breakShellOnBore;
            private static int breakShellChamber;
            internal static float breakShellOut;                 // m behind the chamber along its bore
            internal static float breakShellOutAtCapture;
            internal static Vector3 breakShellHandAtCapture;     // the controller at the capture, in the barrel bone's frame
            private static Vector3 breakShellSeatInBarrel;      // the model seated, in the barrel bone's frame
            private static Quaternion breakShellSeatRotationInBarrel;
            internal static Vector3 breakShellBoreInBarrel;
            internal static bool breakShellCommitted;
            internal static int breakShellLoaded;                // the move's result: 0 = no answer yet, 1 = done, -1 = refused
            internal static Transform breakShellRider;
            private static Vector3 breakShellSnapFrom;          // the shell as the hand had it at the capture, in the barrel bone's frame
            private static Quaternion breakShellSnapFromRotation;
            private static float breakShellSnapStart;
            public static float breakShellSnapTime = 0.08f;     // s it eases from the hand onto the bore
            internal static bool breakMoveInFlight;              // our move into / out of a chamber is running (LetsSingleRoundIn)
            // With both barrels empty, a second shell from the same stack is held beside the first, offset as the chambers are (chamber 0 to 1),
            // and goes into the other chamber with it. Its own move runs once the first's is in.
            internal static GameObject breakShellSecond;
            internal static Vector3 breakShellPairPosition;      // the second in the first's frame
            internal static Quaternion breakShellPairRotation;
            internal static int breakShellSecondChamber = -1;
            private static Vector3 breakShellSecondSeatInBarrel;
            private static Quaternion breakShellSecondSeatRotationInBarrel;
            private static Vector3 breakShellSecondSnapFrom;
            private static Quaternion breakShellSecondSnapFromRotation;
            internal static int breakShellSecondLoaded;          // its move's result: 0 = no answer yet, 1 = done, -1 = refused

            // A grip at the chest (like TryGrabRound). Returns true when this gun took it: a shell was taken, or none fits (a double buzz).
            internal static bool TryGrabBreakShell(bool gunHand)
            {
                if (gunHand || HoldingBarrels || HoldingBreakCase)
                    return false;
                FirearmController firearmController = VRGlobals.firearmController;
                if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.player == null)
                    return false;
                if (!ManualBreakOn(firearmController, out BreakData data) || !EnsureBreakBones(firearmController, data) || !GunIdle(firearmController)
                    || HandsInteractionController.HoldingLoot || ChestHang.Detached)
                    return false;
                Weapon weapon = firearmController.Item;
                Firearms firearms = firearmController.Firearms;
                if (firearms == null || !breakStates.TryGetValue(weapon, out BreakState state) || state.open < data.openDegrees * barrelOpenEnough)
                    return false;
                int empty = -1;
                for (int chamber = 0; chamber < BreakSlots(weapon).Length && empty < 0; chamber++)
                {
                    if (ChamberTakesRound(firearms, weapon, chamber))
                        empty = chamber;
                }
                if (empty < 0 || !InChestPouchZone(OffHandPalm()))
                    return false;
                Ammo source = FindBreakShell(weapon, empty);
                if (source == null)
                {
                    PulseRoundHand(false, 0.2f, 0.03f);
                    PulseRoundHand(false, 0.2f, 0.03f, 0.08f);
                    return true;
                }
                GameObject prop = CreateProp(source);
                if (prop == null)
                    return true;
                heldRoundSource = source;
                heldRoundProp = prop;
                heldRoundFor = firearmController;
                roundInGunHand = false;
                roundOnRightArm = OnRightArm(false);
                roundLoading = false;
                breakShellHeld = true;
                breakShellData = data;
                breakShellOnBore = false;
                breakShellCommitted = false;
                breakShellBlend = 0f;
                breakShellBackBlocked = false;
                breakShellSecond = null;
                breakShellSecondChamber = -1;
                // Both chambers empty and two in the stack: take both, chamber 0's shell beside chamber 1's as they sit in the gun
                Transform first = ChamberBone(firearms, 0);
                Transform second = ChamberBone(firearms, 1);
                if (!data.revolver && BreakSlots(weapon).Length == 2 && ChamberEmpty(firearms, weapon, 0) && ChamberEmpty(firearms, weapon, 1) && source.StackObjectsCount >= 2
                    && first != null && second != null)
                {
                    Quaternion firstSeat = first.rotation * Quaternion.Euler(90f, 0f, 0f);
                    breakShellPairPosition = Quaternion.Inverse(firstSeat) * (second.position - first.position);
                    breakShellPairRotation = Quaternion.Inverse(firstSeat) * second.rotation * Quaternion.Euler(90f, 0f, 0f);
                    breakShellSecond = CreateProp(source);
                }
                MeasureRound(prop);
                PointRoundAlongBore(firearmController);
                HoldingRound = true;
                PlaceHeldRound();
                if (data.revolver)
                    PlayFirstGunSound(firearmController, SoundHand.OffHandAlone, "ShellGrab", "AmmoGrab", "AmmoInHand");
                else
                    PlayFirstGunSound(firearmController, SoundHand.OffHandAlone, "ShellOutRig", "AmmoCatch", "MagPullOut");
                PulseRoundHand(false, 0.25f, 0.03f);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] took {(breakShellSecond != null ? "two shells" : "a shell")} of {source.LocalizedShortName()} ({source.StackObjectsCount} in that stack), chambers {ChambersText(firearmController, weapon)}");
                return true;
            }

            // Finds a loose stack (in a grid) that fits that chamber, preferring the round type already in the gun
            private static Ammo FindBreakShell(Weapon weapon, int chamber)
            {
                reachableRounds.Clear();
                VRGlobals.player.InventoryController.GetReachableItemsOfTypeNonAlloc(reachableRounds, (Ammo ammo) => ammo.StackObjectsCount > 0 && ammo.Parent is GridItemAddress);
                Item inGun = null;
                foreach (Slot slot in BreakSlots(weapon))
                {
                    if (slot.ContainedItem != null)
                        inGun = slot.ContainedItem;
                }
                for (int pass = 0; pass < 2; pass++)
                {
                    foreach (Ammo ammo in reachableRounds)
                    {
                        if (pass == 0 && (inGun == null || ammo.TemplateId != inGun.TemplateId))
                            continue;
                        if (TryBuildLoadIntoChamber(ammo, weapon, chamber, out _))
                            return ammo;
                    }
                }
                return null;
            }

            private static bool TryBuildLoadIntoChamber(Ammo source, Weapon weapon, int chamber, out OperationResult<IItemOperationResult> operation)
            {
                InventoryController inventoryController = VRGlobals.player.InventoryController;
                ItemAddress to = BreakSlots(weapon)[chamber].CreateItemAddress();
                operation = source.StackObjectsCount == 1
                    ? ItemManipulator.Move(source, to, inventoryController, simulate: true).Cast<MoveResult, IItemOperationResult>()
                    : ItemManipulator.SplitExact(source, 1, to, inventoryController, inventoryController, simulate: true).Cast<SplitResult, IItemOperationResult>();
                return operation.Succeeded;
            }

            private static bool TryBuildRoundToRig(Ammo round, out OperationResult<MoveResult> moveResult)
            {
                moveResult = default;
                InventoryController inventoryController = VRGlobals.player.InventoryController;
                GridItemAddress bestAddress = null;
                int bestGridSize = int.MaxValue;
                foreach (var grid in inventoryController.Inventory.Equipment.GetPrioritizedGridsForUnloadedObject())
                {
                    GridItemAddress address = grid.FindLocationForItem(round);
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
                moveResult = ItemManipulator.Move(round, bestAddress, inventoryController, simulate: true);
                return !moveResult.Failed && moveResult.Value.CanExecute(inventoryController);
            }

            private static void RunBreakMove(FirearmController firearmController, OperationResult<IItemOperationResult> operation, string what, System.Action<bool> done)
            {
                breakMoveInFlight = true;
                VRGlobals.player.InventoryController.TryRunNetworkTransaction(operation, result =>
                {
                    breakMoveInFlight = false;
                    if (breakDebugLog)
                        Plugin.MyLog.LogInfo($"[VRBreak] {what}: {(result.Succeed ? "done" : "NOT done: " + result.Error)}, chambers {ChambersText(firearmController, firearmController.Item)}");
                    done?.Invoke(result.Succeed);
                });
            }

            // A shell lined up behind an empty chamber, on its bore, goes onto that bore. For a pair, either shell lined up on either chamber
            // puts it there and the other shell on the other chamber.
            internal static void TryPutShellOnBore(Firearms firearms, Weapon weapon)
            {
                if (breakShellData != null && breakShellData.revolver && breakShellSecond == null && TryPutShellAtCylinderBack(firearms, weapon))
                    return;
                if (breakShellSecond != null)
                {
                    for (int shell = 0; shell < 2; shell++)
                    {
                        for (int chamber = 0; chamber < 2; chamber++)
                        {
                            Transform held = (shell == 0 ? heldRoundProp : breakShellSecond).transform;
                            if (!LinedUpOnBore(firearms, weapon, chamber, held, out float along))
                                continue;
                            // The first shell's chamber, as if the first were the one lined up
                            int firstChamber = shell == 0 ? chamber : 1 - chamber;
                            if (!ChamberEmpty(firearms, weapon, 1 - firstChamber))
                                continue;
                            PutShellOnBore(firearms, firstChamber, along);
                            PutSecondShellOnBore(firearms, 1 - firstChamber);
                            return;
                        }
                    }
                    return;
                }
                for (int chamber = 0; chamber < BreakSlots(weapon).Length; chamber++)
                {
                    if (LinedUpOnBore(firearms, weapon, chamber, heldRoundProp.transform, out float along))
                    {
                        PutShellOnBore(firearms, chamber, along);
                        return;
                    }
                }
            }

            // The zone at the back of a revolver's cylinder: centred cylinderBackBehind along the bore from the middle of its chamber bones
            // (on the rear face), with a radius that reaches cylinderBackReach past their ring
            private static bool CylinderBack(Firearms firearms, Weapon weapon, out Vector3 centre, out float radius)
            {
                centre = Vector3.zero;
                radius = 0f;
                Slot[] slots = BreakSlots(weapon);
                Vector3 sum = Vector3.zero, bore = Vector3.zero;
                int count = 0;
                for (int chamber = 0; chamber < slots.Length; chamber++)
                {
                    Transform bone = ChamberBone(firearms, chamber);
                    if (bone == null)
                        continue;
                    sum += bone.position;
                    bore += bone.up;
                    count++;
                }
                if (count < 2 || bore.sqrMagnitude < 1e-8f)
                    return false;
                Vector3 face = sum / count;
                float ring = 0f;
                for (int chamber = 0; chamber < slots.Length; chamber++)
                {
                    Transform bone = ChamberBone(firearms, chamber);
                    if (bone != null)
                        ring = Mathf.Max(ring, (bone.position - face).magnitude);
                }
                centre = face + bore.normalized * cylinderBackBehind;
                radius = ring + cylinderBackReach;
                return true;
            }

            private static bool AtCylinderBack(Firearms firearms, Weapon weapon, Vector3 point, float extra)
            {
                return CylinderBack(firearms, weapon, out Vector3 centre, out float radius) && (point - centre).sqrMagnitude <= (radius + extra) * (radius + extra);
            }

            // A fired case the trigger would draw with the hand here
            private static bool FiredCaseInReach(Firearms firearms, Weapon weapon, Vector3 palm)
            {
                bool atBack = AtCylinderBack(firearms, weapon, palm, 0f);
                Slot[] slots = BreakSlots(weapon);
                for (int chamber = 0; chamber < slots.Length; chamber++)
                {
                    GameObject model = ChamberModel(firearms, weapon, chamber, out bool spent);
                    if (model != null && spent && (atBack || (model.transform.position - palm).sqrMagnitude <= caseGrabRadius * caseGrabRadius))
                        return true;
                }
                return false;
            }

            private static bool breakShellBackBlocked;          // pulled back off a chamber: the cylinder back takes it again only after it left the zone

            // A revolver round near the back of the cylinder goes onto the bore of the empty chamber whose mouth is nearest, just behind it
            private static bool TryPutShellAtCylinderBack(Firearms firearms, Weapon weapon)
            {
                Transform prop = heldRoundProp.transform;
                Vector3 middle = prop.TransformPoint(roundCenterInProp);
                float reach = roundLength * 0.5f;
                if (breakShellBackBlocked)
                {
                    if (AtCylinderBack(firearms, weapon, middle, reach + 0.03f))
                        return false;
                    breakShellBackBlocked = false;
                }
                if (!AtCylinderBack(firearms, weapon, middle, reach))
                    return false;
                Slot[] slots = BreakSlots(weapon);
                int best = -1;
                float nearest = float.MaxValue;
                for (int chamber = 0; chamber < slots.Length; chamber++)
                {
                    Transform bone = ChamberBone(firearms, chamber);
                    if (bone == null || !ChamberTakesRound(firearms, weapon, chamber))
                        continue;
                    Quaternion seated = bone.rotation * Quaternion.Euler(90f, 0f, 0f);
                    if (Vector3.Angle(prop.rotation * roundAxisInProp, seated * roundAxisInProp) > cylinderBackAngle)
                        continue;
                    Vector3 mouth = bone.position + seated * roundCenterInProp + bone.up * roundLength;
                    float distance = (middle - mouth).sqrMagnitude;
                    if (distance < nearest)
                    {
                        nearest = distance;
                        best = chamber;
                    }
                }
                if (best < 0)
                    return false;
                Transform chamberBone = ChamberBone(firearms, best);
                Vector3 seatMiddle = chamberBone.position + chamberBone.rotation * Quaternion.Euler(90f, 0f, 0f) * roundCenterInProp;
                float along = Mathf.Clamp(Vector3.Dot(middle - seatMiddle, chamberBone.up), roundLength * 0.9f, roundLength + breakShellReach);
                PutShellOnBore(firearms, best, along);
                return true;
            }

            // True when a held shell is lined up behind an empty chamber, on its bore; along = how far out it is
            private static bool LinedUpOnBore(Firearms firearms, Weapon weapon, int chamber, Transform prop, out float along)
            {
                along = 0f;
                Transform bone = ChamberBone(firearms, chamber);
                if (bone == null || !ChamberEmpty(firearms, weapon, chamber))
                    return false;
                Quaternion seatedRotation = bone.rotation * Quaternion.Euler(90f, 0f, 0f);
                Vector3 bore = bone.up;
                Vector3 off = prop.TransformPoint(roundCenterInProp) - (bone.position + seatedRotation * roundCenterInProp);
                along = Vector3.Dot(off, bore);
                float lateral = (off - bore * along).magnitude;
                return along >= 0f && along <= roundLength + breakShellReach && lateral <= breakShellCaptureRadius
                    && Vector3.Angle(prop.rotation * roundAxisInProp, seatedRotation * roundAxisInProp) <= breakShellCaptureAngle;
            }

            // Seated as Tarkov seats a round (ParentShellToTransform: on the chamber bone, turned 90 deg about x), but rolled about the bore to
            // the roll it is held at. A round looks the same at any roll, and each of a cylinder's chamber bones is rolled differently around
            // it (the hand riding the round in would turn with the chamber). The pivot moves so the round's own centre stays on the bore.
            private static void SeatedKeepingRoll(Transform bone, Transform prop, out Vector3 seatPosition, out Quaternion seatRotation)
            {
                Quaternion seated = bone.rotation * Quaternion.Euler(90f, 0f, 0f);
                Vector3 bore = bone.up;
                Vector3 axis = roundAxisInProp.sqrMagnitude > 1e-8f ? roundAxisInProp.normalized : Vector3.forward;
                Vector3 across = Vector3.Cross(axis, Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up);
                Vector3 held = Vector3.ProjectOnPlane(prop.rotation * across, bore);
                Vector3 seat = Vector3.ProjectOnPlane(seated * across, bore);
                seatRotation = held.sqrMagnitude > 1e-8f && seat.sqrMagnitude > 1e-8f ? Quaternion.AngleAxis(Vector3.SignedAngle(seat, held, bore), bore) * seated : seated;
                seatPosition = bone.position + seated * roundCenterInProp - seatRotation * roundCenterInProp;
            }

            private static void PutSecondShellOnBore(Firearms firearms, int chamber)
            {
                Transform bone = ChamberBone(firearms, chamber);
                if (bone == null || breakShellSecond == null)
                    return;
                breakShellSecondChamber = chamber;
                SeatedKeepingRoll(bone, breakShellSecond.transform, out Vector3 secondSeatPosition, out Quaternion secondSeatRotation);
                breakShellSecondSeatInBarrel = barrelBone.InverseTransformPoint(secondSeatPosition);
                breakShellSecondSeatRotationInBarrel = Quaternion.Inverse(barrelBone.rotation) * secondSeatRotation;
                Transform prop = breakShellSecond.transform;
                breakShellSecondSnapFrom = barrelBone.InverseTransformPoint(prop.position);
                breakShellSecondSnapFromRotation = Quaternion.Inverse(barrelBone.rotation) * prop.rotation;
                prop.SetParent(barrelBone, worldPositionStays: true);
                PlaceBreakShellOnBore();
            }

            private static void PutShellOnBore(Firearms firearms, int chamber, float along)
            {
                Transform prop = heldRoundProp.transform;
                Transform bone = ChamberBone(firearms, chamber);
                SeatedKeepingRoll(bone, prop, out Vector3 seatPosition, out Quaternion seatedRotation);
                Vector3 bore = bone.up;
                breakShellOnBore = true;
                breakShellChamber = chamber;
                breakShellOut = along;
                breakShellOutAtCapture = along;
                breakShellHandAtCapture = barrelBone.InverseTransformPoint(HandRig(gunHand: false).position);
                breakShellSeatInBarrel = barrelBone.InverseTransformPoint(seatPosition);
                breakShellSeatRotationInBarrel = Quaternion.Inverse(barrelBone.rotation) * seatedRotation;
                breakShellBoreInBarrel = Quaternion.Inverse(barrelBone.rotation) * bore;
                breakShellSnapFrom = barrelBone.InverseTransformPoint(prop.position);
                breakShellSnapFromRotation = Quaternion.Inverse(barrelBone.rotation) * prop.rotation;
                breakShellSnapStart = Time.time;
                prop.SetParent(barrelBone, worldPositionStays: true);
                PlaceBreakShellOnBore();
                // The hand rides the shell in from where it holds it
                Transform controller = HandRig(gunHand: false);
                if (breakShellRider == null)
                    breakShellRider = new GameObject("VRBreakShellHand").transform;
                breakShellRider.SetParent(prop, worldPositionStays: false);
                breakShellRider.SetPositionAndRotation(controller.position, controller.rotation);
                breakShellBlendAtCapture = breakShellBlend;
                if (breakShellApproachLatched && handLatched && !handLatchReleasing && handLatch != null)
                {
                    // The hand already turned toward the push on the way in: move the latch onto the round where the hand is
                    handLatch.SetParent(breakShellRider, worldPositionStays: true);
                    RebaseLatch();
                    breakShellApproachLatched = false;
                }
                else
                {
                    ReleaseApproachLatch();
                    LatchHand(breakShellRider, gunHand: false);
                }
                PulseRoundHand(false, 0.25f, 0.03f);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] shell onto chamber {chamber}'s bore{(breakShellSecond != null ? " (the second onto the other's)" : "")}, {along * 100f:0.#} cm out");
            }

            private static void PlaceBreakShellOnBore()
            {
                if (heldRoundProp == null || barrelBone == null)
                    return;
                float snap = Mathf.SmoothStep(0f, 1f, (Time.time - breakShellSnapStart) / Mathf.Max(breakShellSnapTime, 0.001f));
                Vector3 position = Vector3.Lerp(breakShellSnapFrom, breakShellSeatInBarrel + breakShellBoreInBarrel * breakShellOut, snap);
                Quaternion rotation = Quaternion.Slerp(breakShellSnapFromRotation, breakShellSeatRotationInBarrel, snap);
                heldRoundProp.transform.SetPositionAndRotation(barrelBone.TransformPoint(position), barrelBone.rotation * rotation);
                if (breakShellSecond != null && breakShellSecondChamber >= 0)
                {
                    Vector3 secondPosition = Vector3.Lerp(breakShellSecondSnapFrom, breakShellSecondSeatInBarrel + breakShellBoreInBarrel * breakShellOut, snap);
                    Quaternion secondRotation = Quaternion.Slerp(breakShellSecondSnapFromRotation, breakShellSecondSeatRotationInBarrel, snap);
                    breakShellSecond.transform.SetPositionAndRotation(barrelBone.TransformPoint(secondPosition), barrelBone.rotation * secondRotation);
                }
            }

            internal static void TakeBreakShellOffBore()
            {
                breakShellOnBore = false;
                breakShellBackBlocked = true;
                breakShellSecondChamber = -1;
                ReleaseBreakShellHand();
                if (heldRoundProp != null)
                    heldRoundProp.transform.SetParent(null, worldPositionStays: true);
                if (breakShellSecond != null)
                    breakShellSecond.transform.SetParent(null, worldPositionStays: true);
                PulseRoundHand(false, 0.12f, 0.02f);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo("[VRBreak] shell drawn back off the bore");
            }

            internal static void CommitBreakShell(FirearmController firearmController, Weapon weapon)
            {
                if (!TryBuildLoadIntoChamber(heldRoundSource, weapon, breakShellChamber, out var operation))
                {
                    if (breakDebugLog)
                        Plugin.MyLog.LogInfo($"[VRBreak] the shell won't go in: {operation.Error}");
                    TakeBreakShellOffBore();
                    PulseRoundHand(false, 0.2f, 0.03f);
                    PulseRoundHand(false, 0.2f, 0.03f, 0.08f);
                    return;
                }
                breakShellCommitted = true;
                breakShellLoaded = 0;
                roundLoading = true;
                Pulse(SoundHand.OffHand, 0.3f, 0.03f);
                int chamber = breakShellChamber;
                int secondChamber = breakShellSecond != null ? breakShellSecondChamber : -1;
                breakShellSecondLoaded = secondChamber >= 0 ? 0 : 1;
                Ammo source = heldRoundSource;
                RunBreakMove(firearmController, operation, "shell into chamber " + chamber, succeeded =>
                {
                    breakShellLoaded = succeeded ? 1 : -1;
                    if (secondChamber < 0)
                        return;
                    if (!succeeded || source == null || source.StackObjectsCount <= 0 || !TryBuildLoadIntoChamber(source, weapon, secondChamber, out var secondMove))
                    {
                        breakShellSecondLoaded = -1;
                        return;
                    }
                    RunBreakMove(firearmController, secondMove, "second shell into chamber " + secondChamber, secondDone => breakShellSecondLoaded = secondDone ? 1 : -1);
                });
            }

            // Seated: Tarkov's round model in the chamber replaces ours
            internal static void FinishBreakShell(FirearmController firearmController, Weapon weapon)
            {
                int chamber = breakShellChamber;
                int secondChamber = breakShellSecond != null ? breakShellSecondChamber : -1;
                bool loaded = breakShellLoaded > 0;
                bool secondLoaded = secondChamber >= 0 && breakShellSecondLoaded > 0;
                EndBreakShell();
                Firearms firearms = firearmController.Firearms;
                if (!loaded)
                {
                    PulseRoundHand(false, 0.2f, 0.03f);
                    PulseRoundHand(false, 0.2f, 0.03f, 0.08f);
                    return;
                }
                ForgetFiredCase(weapon, chamber);
                if (firearms != null && BreakSlots(weapon)[chamber].ContainedItem is Ammo round)
                    firearms.SetRoundIntoWeapon(round, chamber);
                if (secondLoaded)
                {
                    ForgetFiredCase(weapon, secondChamber);
                    if (firearms != null && BreakSlots(weapon)[secondChamber].ContainedItem is Ammo secondRound)
                        firearms.SetRoundIntoWeapon(secondRound, secondChamber);
                }
                SetBreakAnimatorCounts(firearmController);
                if (IsRevolver(weapon))
                    PlayFirstGunSound(firearmController, SoundHand.OffHand, "ShellInsert", "AmmoIn");
                else if (!secondLoaded || !PlayFirstGunSound(firearmController, SoundHand.OffHand, "LoadBooth"))
                    PlayFirstGunSound(firearmController, SoundHand.OffHand, "LoadOne", "ShellIn", "ShellLoad");
                Pulse(SoundHand.OffHand.Seating, 0.45f, 0.04f);
            }

            internal static void EndBreakShell()
            {
                ReleaseBreakShellHand();
                DestroyRoundProp();
                EndRoundHold();
            }

            // The fingers holding a break-action's shell / drawn case / the fore-end (pose order, palm first), mirrored on the right arm
            internal static readonly Vector3[] breakFingersMirrored = new Vector3[16];

            internal static Vector3[] BreakFingers(BreakHold hold, bool rightArm)
            {
                if (hold == null || hold.fingers == null)
                    return null;
                return rightArm != hold.rightHanded ? MirroredPose(hold.fingers, rightToLeft: hold.rightHanded, breakFingersMirrored) ?? hold.fingers : hold.fingers;
            }

            internal static Vector3[] breakFingersBlend = new Vector3[16];
            private static readonly BreakHold breakShellBlendHold = new BreakHold();

            // How far in a round on the bore is, from where it was caught (0) to seated (1)
            private static float BreakShellPushShare()
            {
                return Mathf.SmoothStep(0f, 1f, 1f - Mathf.Clamp01(breakShellOut / Mathf.Max(breakShellOutAtCapture, 0.005f)));
            }

            // A revolver round on its way in: the hand riding it blends from the carry hold into the push hold behind it (two moments of
            // the load clip)
            internal static void BlendBreakShellHand()
            {
                BreakData data = breakShellData;
                if (!breakShellOnBore || breakShellSecond != null || data?.carryHold == null || data.loadHold == null || heldRoundProp == null || breakShellRider == null
                    || !handLatched || latchHolder != breakShellRider)
                    return;
                breakShellBlend = Mathf.Lerp(breakShellBlendAtCapture, 1f, BreakShellPushShare());
                Transform prop = heldRoundProp.transform;
                if (!BlendedShellHold(data, prop, breakShellBlend, out Vector3 inPalm, out Quaternion inPalmRotation))
                    return;
                Quaternion palmRotation = prop.rotation * Quaternion.Inverse(inPalmRotation);
                SnapHandTo(breakShellRider, prop.position - palmRotation * inPalm, palmRotation);
            }

            internal static float breakShellBlend;               // 0 = the carry hold .. 1 = the push hold behind it
            private static float breakShellBlendAtCapture;
            private static bool breakShellApproachLatched;      // the hand latched to its own controller, to turn around the round on the way in

            // The round's pose in the palm, blended between the carry hold and the push hold
            private static bool BlendedShellHold(BreakData data, Transform prop, float blend, out Vector3 position, out Quaternion rotation)
            {
                breakShellBlendHold.position = Vector3.Lerp(data.carryHold.position, data.loadHold.position, blend);
                breakShellBlendHold.rotation = Quaternion.Slerp(data.carryHold.rotation, data.loadHold.rotation, blend);
                breakShellBlendHold.rightHanded = data.carryHold.rightHanded;
                return BreakShellHoldInPalm(breakShellBlendHold, prop, roundOnRightArm, out position, out rotation);
            }

            // Off the bore, the round stays where the controller carries it (PlaceHeldBreakShell: the carry hold relative to the controller's
            // hand). As it nears an empty chamber's mouth, the hand turns around it toward the push hold, latched to its own controller.
            internal static void ApproachBreakShellHand(Firearms firearms, Weapon weapon, bool openEnough)
            {
                BreakData data = breakShellData;
                if (breakShellSecond != null || data?.carryHold == null || data.loadHold == null || heldRoundProp == null)
                    return;
                Transform prop = heldRoundProp.transform;
                Vector3 roundMiddle = prop.TransformPoint(roundCenterInProp);
                float nearest = float.MaxValue;
                if (openEnough)
                {
                    Slot[] slots = BreakSlots(weapon);
                    for (int chamber = 0; chamber < slots.Length; chamber++)
                    {
                        Transform bone = ChamberBone(firearms, chamber);
                        if (bone == null || !ChamberTakesRound(firearms, weapon, chamber))
                            continue;
                        Vector3 mouth = bone.position + bone.rotation * Quaternion.Euler(90f, 0f, 0f) * roundCenterInProp + bone.up * roundLength;
                        nearest = Mathf.Min(nearest, (roundMiddle - mouth).magnitude);
                    }
                }
                breakShellBlend = carryBlendApproach * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(carryBlendFar, carryBlendNear, nearest));
                Transform controller = HandRig(gunHand: false);
                if (breakShellBlend <= 0.001f || controller == null)
                {
                    ReleaseApproachLatch();
                    return;
                }
                if (!breakShellApproachLatched || !handLatched || handLatchReleasing || latchHolder != controller)
                {
                    LatchHand(controller, gunHand: false);
                    breakShellApproachLatched = true;
                }
                // Place the palm around the round: the round's carry pose comes from the controller, the palm from the round by the blended hold
                if (!BreakShellHoldInPalm(data.carryHold, prop, roundOnRightArm, out Vector3 carryPosition, out Quaternion carryRotation)
                    || !BlendedShellHold(data, prop, breakShellBlend, out Vector3 inPalm, out Quaternion inPalmRotation))
                    return;
                Quaternion roundRotation = controller.rotation * carryRotation;
                Vector3 roundPosition = controller.position + controller.rotation * carryPosition;
                Quaternion palmRotation = roundRotation * Quaternion.Inverse(inPalmRotation);
                SnapHandTo(controller, roundPosition - palmRotation * inPalm, palmRotation);
            }

            internal static void ReleaseApproachLatch()
            {
                if (!breakShellApproachLatched)
                    return;
                breakShellApproachLatched = false;
                if (handLatched && !handLatchReleasing && latchHolder == HandRig(gunHand: false))
                    ReleaseHandLatch();
            }

            // For OffHandPose: the fingers for the barrels or a drawn case in the off hand
            internal static Vector3[] BreakHandFingers(bool rightArm)
            {
                if (!TryGetBreakAction(VRGlobals.firearmController, out BreakData data))
                    return null;
                if (HoldingBreakCase)
                    return BreakFingers(case2Prop != null ? data.pluckPair ?? data.pluckHold : data.pluckHold, rightArm) ?? MAGAZINE_PULL_FINGERS;
                if (HoldingEjector && data.ejector != null)
                    return BreakFingers(data.ejector.hold, rightArm);
                return BreakFingers(data.foreEnd, rightArm);
            }

            // From ApplyHeldFingerPose / ApplyGunHandFingerPose: places the drawn case in the fingers once it is out
            internal static void PlaceBreakCaseInHand(bool rightArm)
            {
                if (HoldingBreakCase && caseFree && caseOnRightArm == rightArm && TryGetBreakAction(caseFor, out BreakData data))
                    PlaceCase(data);
            }

            internal static bool TouchesChamber(Weapon weapon, EFT.InventoryLogic.Operations.IInventoryOperation operation)
            {
                if (!(operation is EFT.InventoryLogic.Operations.IOneItemOperation move) || !(move.Item1 is Ammo))
                    return false;
                if (IsChamber(weapon, move.From1) || IsChamber(weapon, move.To1))
                    return true;
                return operation is EFT.InventoryLogic.Operations.ITwoItemOperation twoItems && IsChamber(weapon, twoItems.To2);
            }

            private static bool IsChamber(Weapon weapon, ItemAddress address)
            {
                if (address == null)
                    return false;
                foreach (Slot chamber in BreakSlots(weapon))
                {
                    if (ReferenceEquals(address.Container, chamber))
                        return true;
                }
                return false;
            }

            private static void SetBreakAnimatorCounts(FirearmController firearmController)
            {
                if (firearmController == null || firearmController.FirearmsAnimator == null || firearmController.Item == null)
                    return;
                // A revolver's rounds are counted in its cylinder, as Tarkov's own cylinder operations count them
                if (firearmController.Item.GetCurrentMagazine() is CylinderMagazine cylinder && IsRevolver(firearmController.Item))
                {
                    firearmController.FirearmsAnimator.SetAmmoOnMag(cylinder.Count);
                    firearmController.FirearmsAnimator.SetShellsInWeapon(firearmController.Item.GetShellsInWeaponCount());
                    return;
                }
                firearmController.FirearmsAnimator.SetAmmoInChamber(firearmController.Item.ChamberAmmoCount);
                firearmController.FirearmsAnimator.SetShellsInWeapon(firearmController.Item.ShellsInWeaponCount);
            }

            private static string ChambersText(FirearmController firearmController, Weapon weapon)
            {
                Firearms firearms = firearmController != null ? firearmController.Firearms : null;
                var text = new System.Text.StringBuilder();
                for (int chamber = 0; chamber < BreakSlots(weapon).Length; chamber++)
                {
                    if (chamber > 0)
                        text.Append('/');
                    Item item = BreakSlots(weapon)[chamber].ContainedItem;
                    bool fired = weapon.ShellsInChambers != null && chamber < weapon.ShellsInChambers.Length && weapon.ShellsInChambers[chamber] != null;
                    text.Append(item != null ? "live" : fired ? "fired" : "empty");
                }
                return text.ToString();
            }

            // Which bone each chamber's model is on (a fired case on its shell port, a round on its chamber bone)
            private static string ChamberModelsText(Firearms firearms, Weapon weapon)
            {
                if (firearms == null)
                    return "none";
                var text = new System.Text.StringBuilder();
                for (int chamber = 0; chamber < BreakSlots(weapon).Length; chamber++)
                {
                    GameObject model = ChamberModel(firearms, weapon, chamber, out bool spent);
                    text.Append(chamber > 0 ? ", " : "").Append(model == null ? "-" : (spent ? "case on " : "round on ") + (model.transform.parent != null ? model.transform.parent.name : "nothing"));
                }
                return text.ToString();
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            // The gun hand on the lever

            internal static float breakLeverPress;               // 0 on the grip .. 1 on the lever
            internal static FirearmController breakLeverPressFor;
            private static Transform leverHandMarker;           // our transform: Tarkov's arm IK target for the gun hand while pressing
            private static Transform leverSavedMarker;          // what _markers[1] / _ikTargets[1] were, put back after the arm IK
            private static Transform leverSavedIkTarget;
            private static bool leverMarkerSwapped;

            // Player.LateUpdate prefix, before the arm IK. Tarkov puts the gun hand on its IK marker, pulled onto the grip's pose when near
            // it (IkProcess: the grip reference within 10 cm). Here _markers[1] is swapped for our marker, which blends from exactly that pose
            // toward the clip's marker at the press. The gun was already anchored on its own marker in Update, so only the hand moves.
            // RestoreBreakLeverMarker swaps it back after the IK.
            private static void ApplyBreakLeverHand(FirearmController firearmController, BreakData data)
            {
                EFT.Player player = VRGlobals.player;
                if (!BreakLeverPressing || breakLeverPressFor != firearmController || data.leverPress == null || player == null || player._markers == null
                    || player._markers.Length < 2 || ChestHang.Detached || Handedness.GunInLeftHand || firearmController.HandsHierarchy == null)
                    return;
                Transform gunMarker = firearmController.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon_R_IK_marker);
                if (gunMarker == null || gunMarker.parent == null || player._markers[1] != gunMarker)
                    return;
                Vector3 restPosition = gunMarker.position;
                Quaternion restRotation = gunMarker.rotation;
                Transform ikTarget = player._ikTargets != null && player._ikTargets.Length > 1 ? player._ikTargets[1] : null;
                Transform gripReference = player._gripReferences != null && player._gripReferences.Length > 1 ? player._gripReferences[1] : null;
                if (ikTarget != null && gripReference != null)
                {
                    float onGrip = Mathf.InverseLerp(0.1f, 0f, Vector3.Distance(gunMarker.position, gripReference.position));
                    restPosition = Vector3.Lerp(restPosition, ikTarget.position, onGrip);
                    restRotation = Quaternion.Lerp(restRotation, ikTarget.rotation, onGrip);
                }
                Transform parent = gunMarker.parent;
                Vector3 pressedPosition = parent.TransformPoint(gunMarker.localPosition + data.leverPress.markerMove);
                Quaternion pressedRotation = parent.rotation * data.leverPress.markerTurn * gunMarker.localRotation;
                float press = Mathf.SmoothStep(0f, 1f, breakLeverPress);
                if (leverHandMarker == null)
                    leverHandMarker = new GameObject("VRLeverHand").transform;
                leverHandMarker.SetPositionAndRotation(Vector3.Lerp(restPosition, pressedPosition, press), Quaternion.Slerp(restRotation, pressedRotation, press));
                leverSavedMarker = player._markers[1];
                leverSavedIkTarget = ikTarget;
                player._markers[1] = leverHandMarker;
                if (ikTarget != null)
                    player._ikTargets[1] = null;
                leverMarkerSwapped = true;
            }

            // Called from the Player.LateUpdate postfix (ShotgunPatches)
            public static void RestoreBreakLeverMarker()
            {
                if (!leverMarkerSwapped)
                    return;
                leverMarkerSwapped = false;
                EFT.Player player = VRGlobals.player;
                if (player == null || player._markers == null || player._markers.Length < 2)
                    return;
                if (player._markers[1] == leverHandMarker)
                    player._markers[1] = leverSavedMarker;
                if (leverSavedIkTarget != null && player._ikTargets != null && player._ikTargets.Length > 1 && player._ikTargets[1] == null)
                    player._ikTargets[1] = leverSavedIkTarget;
                leverSavedMarker = null;
                leverSavedIkTarget = null;
            }

            // The gun went away, the hands changed, or Tarkov took over: release everything held
            private static void EndBreakHolds()
            {
                if (HoldingBarrels)
                    LetGoOfBarrels(keepSupport: false);
                if (HoldingBreakCase)
                    EndCasePluck(putBack: true);
                if (HoldingEjector)
                    LetGoOfEjector();
                PutEjectorCasesBack();
            }

            // debugZones: the fore-end (orange, green in reach) and each reachable chamber (blue / green)
            private const int ChamberZones = 6;        // the most chambers a zone is drawn for

            private static void UpdateBreakZones(FirearmController firearmController, BreakData data, BreakState state)
            {
                bool open = !state.latched || state.open > 0f;
                Vector3 grab = ForeEndGrabWorld(data, OffHandOnRightArm);
                bool atForeEnd = (OffHandPalm() - grab).sqrMagnitude <= foreEndGrabRadius * foreEndGrabRadius;
                SetZoneViz(debugZones && open && !HoldingBarrels, grab, foreEndGrabRadius, atForeEnd ? new Color(0f, 1f, 0f, 0.25f) : new Color(1f, 0.6f, 0.1f, 0.25f));
                bool showEjector = debugZones && data.ejector != null && ejectorPressBone != null && state.open >= data.openDegrees * barrelOpenEnough && !HoldingEjector;
                Vector3 ejectorAt = showEjector ? EjectorGrabWorld(data, OffHandOnRightArm) : Vector3.zero;
                bool atEjector = showEjector && (OffHandPalm() - ejectorAt).sqrMagnitude <= ejectorGrabRadius * ejectorGrabRadius;
                SetZoneViz(showEjector, ejectorAt, ejectorGrabRadius, atEjector ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.2f, 0.8f, 0.3f));
                Vector3 backCentre = Vector3.zero;
                float backRadius = 0f;
                bool showBack = debugZones && data.revolver && firearmController.Firearms != null && state.open >= data.openDegrees * barrelOpenEnough && !HoldingBreakCase
                    && CylinderBack(firearmController.Firearms, firearmController.Item, out backCentre, out backRadius);
                bool atBackZone = showBack && (OffHandPalm() - backCentre).sqrMagnitude <= backRadius * backRadius;
                SetZoneViz(showBack, backCentre, backRadius, atBackZone ? new Color(0f, 1f, 0f, 0.2f) : new Color(1f, 1f, 0.2f, 0.2f));
                Firearms firearms = firearmController.Firearms;
                Weapon weapon = firearmController.Item;
                for (int chamber = 0; chamber < ChamberZones; chamber++)
                {
                    bool show = debugZones && firearms != null && chamber < BreakSlots(weapon).Length && state.open >= data.openDegrees * barrelOpenEnough && !HoldingBreakCase;
                    bool zoneSpent = false;
                    GameObject model = show ? ChamberModel(firearms, weapon, chamber, out zoneSpent) : null;
                    if (data.revolver && model != null && !zoneSpent)
                        model = null;
                    Transform bone = show ? ChamberBone(firearms, chamber) : null;
                    Vector3 at = model != null ? model.transform.position : bone != null ? bone.position : Vector3.zero;
                    bool reach = model != null && (OffHandPalm() - at).sqrMagnitude <= caseGrabRadius * caseGrabRadius;
                    SetZoneViz(show && bone != null, at, caseGrabRadius, reach ? new Color(0f, 1f, 0f, 0.3f) : new Color(0.2f, 0.5f, 1f, 0.3f));
                }
            }
        }
    }
}
