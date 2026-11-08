using System.Collections.Generic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // An AK's magazine doesn't slide out of the well. The off hand holds it and that thumb pushes the paddle catch behind it (B on
    // that controller), then it rocks forward about the top of the well until it comes away; going in it hooks at that tilt and rocks
    // back until the catch clicks, or just gets pushed up. Which guns, about which axis and how far come from each gun's own magazine clips
    // (Tools/gen_reload_holds). The hinge runs on the same track as a straight magazine: railPosition 0 is seated and railLength fully
    // rocked, so the catch, the seat and the capture read the same.
    public static partial class VRReloadController
    {
        public static bool rockInMagazines = true;          // false = these guns' magazines slide out like an M4's, with its mag release
        public static float rockInCatchStart = 0.35f;       // share of the rock left when a magazine going in starts pushing the catch
        public static float rockInCatchFull = 0.1f;         // and when it has it all the way in, just before it clicks past
        public static float rockInInsertEscape = 0.15f;     // m the controller moves off a magazine going in before the hand lets go of the track

        private sealed class RockIn
        {
            public Vector3 pivot;       // the hinge's point in the well bone's frame: zero = the well itself (an AK's); a belt box's hooked edge
            public Vector3 axis;        // in the well bone's frame; turning about it by a positive angle tilts the magazine out
            public float angle;         // degrees to come free
            public Vector3[] thumb;     // the off thumb's three joints on the catch; null = the clip doesn't press it
        }

        private static readonly Dictionary<string, RockIn> rockInGuns = new Dictionary<string, RockIn>();
        private static FirearmController rockInFor;
        private static RockIn rockInOfGun;
        private static RockIn heldRockIn;                   // the held magazine's: null on a straight track
        private static Vector3 rockLeverOnHand;             // the point of the magazine the hand holds, in the controller's frame
        private static Vector3 rockLeverStartInWell;        // and where that was in the well bone's frame when the track started

        private static float latchThumbTarget;
        private static float latchThumbShown;
        private static Vector3[] latchThumbPose;
        private static int latchFinger;                     // which of the off hand's fingers that pose is (0 thumb): an MDR's is the index

        // The off thumb works these guns' catch without the magazine rocking (an MP5's paddle): the thumb's joints on it, by animation set
        public static bool offHandThumbCatches = true;      // false = the gun hand's finger and button, like an M4
        private static readonly Dictionary<string, Vector3[]> thumbCatches = new Dictionary<string, Vector3[]>();

        // The off hand works these guns' catch though their clips don't put the thumb on it (the AUG's release behind the magazine)
        private static readonly HashSet<string> offHandCatchGuns = new HashSet<string> { "aug" };

        // Who presses each gun's release in its own mag_out clip (Tools/gen_reload_holds/gen_release.py): the hand, the finger, and
        // that finger's joints on it. A gun with no clear press isn't listed and keeps the M4's: the gun hand's index.
        private sealed class ReleasePress
        {
            public bool offHand;
            public int finger;          // 0 thumb .. 4 pinky
            public Vector3[] joints;
        }
        private static readonly Dictionary<string, ReleasePress> releasePresses = new Dictionary<string, ReleasePress>();
        public static bool releasePressFromClips = true;    // false = the hand-picked lists above only

        private static ReleasePress ReleasePressOf(FirearmController firearmController)
        {
            if (!releasePressFromClips || firearmController == null)
                return null;
            LoadRecordedGrips();
            releasePresses.TryGetValue(AnimationFamily(firearmController), out ReleasePress press);
            return press;
        }

        // The off hand presses this gun's catch, on the off controller's button, not the gun hand's finger
        private static bool ThumbWorksCatch(FirearmController firearmController)
        {
            return RockInOf(firearmController) != null || ThumbCatchOf(firearmController) != null || BeltBoxReleasedByOffHand(firearmController)
                || (offHandThumbCatches && firearmController != null && offHandCatchGuns.Contains(AnimationFamily(firearmController)))
                || (offHandThumbCatches && ReleasePressOf(firearmController)?.offHand == true);
        }

        // These guns' catch answers either hand's button: the gun hand's, as on an M4, or the off controller's with that hand on the
        // magazine, as on a thumb catch. Ahead of the rock-in's off-thumb-only rule (the Mk47 rocks its AK magazines in too).
        public static bool eitherHandCatches = true;
        private static readonly HashSet<string> eitherHandCatchGuns = new HashSet<string> { "mk47" };

        private static bool EitherHandWorksCatch(FirearmController firearmController)
        {
            return eitherHandCatches && firearmController != null && eitherHandCatchGuns.Contains(AnimationFamily(firearmController));
        }

        private static Vector3[] ThumbCatchOf(FirearmController firearmController)
        {
            if (!offHandThumbCatches || firearmController == null)
                return null;
            LoadRecordedGrips();
            thumbCatches.TryGetValue(AnimationFamily(firearmController), out Vector3[] thumb);
            return thumb;
        }

        private static RockIn RockInOf(FirearmController firearmController)
        {
            if (!rockInMagazines || firearmController == null)
                return null;
            if (rockInFor != firearmController)
            {
                rockInFor = firearmController;
                LoadRecordedGrips();
                rockInGuns.TryGetValue(AnimationFamily(firearmController), out rockInOfGun);
            }
            return rockInOfGun;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static Quaternion RockRotation(float position)
        {
            return Quaternion.AngleAxis(heldRockIn.angle * position / railLength, heldRockIn.axis);
        }

        // The way a magazine hooked at full tilt points out of the well, in world space
        private static Vector3 HookAxisWorld()
        {
            return magazineWell.TransformDirection(RockRotation(railLength) * railAxisInWell).normalized;
        }

        // The hinge, world: the well itself, or a belt box's hooked edge
        private static Vector3 RockPivotWorld() => magazineWell.TransformPoint(heldRockIn.pivot);

        // A magazine at this much of the rock (0 seated): where its seated pose turns to about the hinge, in the well's frame
        private static Vector3 RockedLocalPosition(float position) => heldRockIn.pivot + RockRotation(position) * (seatedLocalPosition - heldRockIn.pivot);

        // The hand holds the magazine rigidly, so a point on it says how far the hand has rocked it, whether that came from the arm or
        // the wrist: its middle, fixed to the controller from here on. The hand holding it (MagazineHandRig): a belt box may be the gun hand's.
        private static void StartRockLever()
        {
            Transform controller = MagazineHandRig;
            Vector3 middle = heldProp.transform.TransformPoint(MagazineCenterInProp(heldProp));
            rockLeverOnHand = controller.InverseTransformPoint(middle);
            rockLeverStartInWell = magazineWell.InverseTransformVector(middle - RockPivotWorld());
        }

        private static Vector3 RockLeverNow()
        {
            return MagazineHandRig.TransformPoint(rockLeverOnHand) - RockPivotWorld();
        }

        // A belt box going in is caught hooked: held near where its well point sits at the full rock (on the hinge, that's off the well),
        // turned about that way (AK magazines keep their own catch along the track)
        public static float rockHookCatchRadius = 0.06f;   // m the box's well point to where it is hooked
        private static bool HookedOnPivot(Transform propTransform)
        {
            Vector3 hookedWellPoint = magazineWell.TransformPoint(heldRockIn.pivot + RockRotation(railLength) * (-heldRockIn.pivot));
            Vector3 wellPointNow = propTransform.TransformPoint(propWellPointLocal);
            Quaternion hookedRotation = magazineWell.rotation * (RockRotation(railLength) * seatedLocalRotation);
            return (wellPointNow - hookedWellPoint).magnitude <= rockHookCatchRadius && Quaternion.Angle(propTransform.rotation, hookedRotation) <= railCaptureAngle;
        }

        // The controller's move since the track started as a turn about the hinge, in track metres, and how far it is off the magazine.
        // Measured along the circle at the start rather than as an angle from the hinge: pushing up brings that point close to the
        // hinge, where the angle jumps around (headset: pushing in knocked the magazine off the track). Going in, pushing it up seats it
        // as well as rocking it back does, so the tilt doesn't have to be right.
        private static void MeasureHandOnRock(out float handAlongTrack, out float handOffTrack)
        {
            Vector3 hinge = magazineWell.TransformDirection(heldRockIn.axis).normalized;
            Vector3 start = magazineWell.TransformVector(rockLeverStartInWell);
            Vector3 startAcross = Vector3.ProjectOnPlane(start, hinge);
            Vector3 outward = startAcross.normalized;
            Vector3 moved = RockLeverNow() - start;
            float rockedDegrees = Vector3.Dot(moved, Vector3.Cross(hinge, outward)) / Mathf.Max(startAcross.magnitude, 0.03f) * Mathf.Rad2Deg;
            handAlongTrack = rockedDegrees / heldRockIn.angle * railLength;
            if (!railPullingOut)
                handAlongTrack = Mathf.Min(handAlongTrack, Vector3.Dot(moved, outward));
            handOffTrack = HandOffRockingMagazine();
        }

        // The controller away from where the hand holds the magazine as it sits now. Pushing further in isn't coming off it.
        private static float HandOffRockingMagazine()
        {
            Vector3 hinge = magazineWell.TransformDirection(heldRockIn.axis).normalized;
            float turned = heldRockIn.angle * (railPosition - railPositionAtStart) / railLength;
            Vector3 heldPoint = Quaternion.AngleAxis(turned, hinge) * magazineWell.TransformVector(rockLeverStartInWell);
            Vector3 off = RockLeverNow() - heldPoint;
            Vector3 outward = heldPoint.normalized;
            float alongOutward = Vector3.Dot(off, outward);
            return (alongOutward < 0f ? off - outward * alongOutward : off).magnitude;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Past the catch nothing but the hand holds it: let go it falls, pulled away it comes with the hand
        private static bool RockedMagazineLeaves(bool gripReleased, float handOffMagazine)
        {
            if (heldRockIn == null || !magazineCatchPassed || HoldingPalmedMagazine || (!gripReleased && handOffMagazine <= railLateralEscape))
                return false;
            ComeFreeOfWell();
            if (gripReleased)
                ReleaseHeldMagazine();
            return true;
        }

        private static void ApplyLatchThumb(Transform[] palmBones)
        {
            if (latchThumbShown <= 0f || latchThumbPose == null)
                return;
            // palmBones is the palm then three joints a finger, thumb first
            int firstJoint = 1 + latchFinger * 3;
            int jointCount = Mathf.Min(latchThumbPose.Length, palmBones.Length - firstJoint);
            for (int joint = 0; joint < jointCount; joint++)
                palmBones[firstJoint + joint].localRotation = Quaternion.Slerp(palmBones[firstJoint + joint].localRotation, Quaternion.Euler(latchThumbPose[joint]), latchThumbShown);
        }
    }
}
