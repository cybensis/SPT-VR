using System.Collections.Generic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // An AK's magazine doesn't slide out of the well. The off hand holds it and that thumb pushes the paddle catch behind it (B on
    // that controller), then it rocks forward about the top of the well until it comes away; going in it hooks at that tilt and rocks
    // back until the catch clicks. Which guns, about which axis and how far come from each gun's own magazine clips
    // (Tools/gen_reload_holds). The hinge runs on the same track as a straight magazine: railPosition 0 is seated and railLength fully
    // rocked, so the catch, the seat and the capture read the same.
    public static partial class VRReloadController
    {
        public static bool rockInMagazines = true;          // false = these guns' magazines slide out like an M4's, with its mag release
        public static float rockInCatchStart = 0.35f;       // share of the rock left when a magazine going in starts pushing the catch
        public static float rockInCatchFull = 0.1f;         // and when it has it all the way in, just before it clicks past

        private sealed class RockIn
        {
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

        // The hand holds the magazine rigidly, so a point on it says how far the hand has rocked it, whether that came from the arm or
        // the wrist: its middle, fixed to the controller from here on
        private static void StartRockLever()
        {
            Transform controller = VRGlobals.vrPlayer.LeftHand.transform;
            Vector3 middle = heldProp.transform.TransformPoint(MagazineCenterInProp(heldProp));
            rockLeverOnHand = controller.InverseTransformPoint(middle);
            rockLeverStartInWell = magazineWell.InverseTransformVector(middle - magazineWell.position);
        }

        private static Vector3 RockLeverNow()
        {
            return VRGlobals.vrPlayer.LeftHand.transform.TransformPoint(rockLeverOnHand) - magazineWell.position;
        }

        // The controller's turn about the hinge since the track started, in track metres, and how far it is off the circle that point rides
        private static void MeasureHandOnRock(out float handAlongTrack, out float handOffTrack)
        {
            Vector3 hinge = magazineWell.TransformDirection(heldRockIn.axis).normalized;
            Vector3 start = magazineWell.TransformVector(rockLeverStartInWell);
            Vector3 now = RockLeverNow();
            Vector3 startAcross = Vector3.ProjectOnPlane(start, hinge);
            Vector3 nowAcross = Vector3.ProjectOnPlane(now, hinge);
            handAlongTrack = Vector3.SignedAngle(startAcross, nowAcross, hinge) / heldRockIn.angle * railLength;
            float sideways = Vector3.Dot(now - start, hinge);
            float outward = nowAcross.magnitude - startAcross.magnitude;
            handOffTrack = Mathf.Sqrt(sideways * sideways + outward * outward);
        }

        // The controller away from where the hand holds the magazine as it sits now
        private static float HandOffRockingMagazine()
        {
            Vector3 hinge = magazineWell.TransformDirection(heldRockIn.axis).normalized;
            float turned = heldRockIn.angle * (railPosition - railPositionAtStart) / railLength;
            Vector3 heldPoint = Quaternion.AngleAxis(turned, hinge) * magazineWell.TransformVector(rockLeverStartInWell);
            return (RockLeverNow() - heldPoint).magnitude;
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
            int jointCount = Mathf.Min(latchThumbPose.Length, palmBones.Length - 1);
            for (int joint = 0; joint < jointCount; joint++)
                palmBones[joint + 1].localRotation = Quaternion.Slerp(palmBones[joint + 1].localRotation, Quaternion.Euler(latchThumbPose[joint]), latchThumbShown);
        }
    }
}
