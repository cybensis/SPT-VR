using System.Collections.Generic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // An AK's magazine doesn't slide out of the well. The off hand holds it while its thumb pushes the paddle catch behind it (B on that
    // controller), then the magazine rocks forward about the top of the well until it comes away. Going in, it hooks at that tilt and
    // rocks back until the catch clicks, or is simply pushed up. Which guns rock, about which axis and how far comes from each gun's own
    // magazine clips (Tools/gen_reload_holds). The rocking uses the same track as a straight magazine: railPosition 0 is seated and
    // railLength fully rocked, so the catch, seat and capture logic works unchanged.
    public static partial class VRReloadController
    {
        public static float rockInCatchStart = 0.35f;       // share of the rock left when a magazine going in starts pushing the catch
        public static float rockInCatchFull = 0.1f;         // share left when the catch is pushed all the way in, just before it clicks past
        public static float rockInInsertEscape = 0.15f;     // m the controller may move off a magazine going in before the hand lets go of the track

        private sealed class RockIn
        {
            public Vector3 pivot;       // the hinge point in the well bone's frame: zero = the well itself (an AK), else a belt box's hooked edge
            public Vector3 axis;        // in the well bone's frame; turning about it by a positive angle tilts the magazine out
            public float angle;         // degrees to come free
            public Vector3[] thumb;     // the off thumb's three joints on the catch; null = the clip doesn't press it
        }

        private static readonly Dictionary<string, RockIn> rockInGuns = new Dictionary<string, RockIn>();
        private static FirearmController rockInFor;
        private static RockIn rockInOfGun;
        private static RockIn heldRockIn;                   // the held magazine's rock-in: null on a straight track
        private static Vector3 rockLeverOnHand;             // the point of the magazine the hand holds, in the controller's frame
        private static Vector3 rockLeverStartInWell;        // and where that was in the well bone's frame when the track started

        private static float latchThumbTarget;
        private static float latchThumbShown;
        private static Vector3[] latchThumbPose;
        private static int latchFinger;                     // which of the off hand's fingers that pose is (0 thumb): an MDR's is the index

        // Guns whose catch the off thumb works without the magazine rocking (an MP5's paddle): the thumb's joints on it, keyed by animation set
        private static readonly Dictionary<string, Vector3[]> thumbCatches = new Dictionary<string, Vector3[]>();

        // Guns whose catch the off hand works even though their clips don't put the thumb on it: the AUG's release behind the magazine,
        // and the P90's on the magazine's back end (the hand lifting the magazine squeezes it; the model has no release part)
        private static readonly HashSet<string> offHandCatchGuns = new HashSet<string> { "aug", "p90" };

        // Who presses each gun's release in its own mag_out clip (Tools/gen_reload_holds/gen_release.py): the hand, the finger, and
        // that finger's joints on it. A gun with no clear press isn't listed and keeps the M4's: the gun hand's index.
        private sealed class ReleasePress
        {
            public bool offHand;
            public int finger;          // 0 thumb .. 4 pinky
            public Vector3[] joints;
        }
        private static readonly Dictionary<string, ReleasePress> releasePresses = new Dictionary<string, ReleasePress>();

        private static ReleasePress ReleasePressOf(FirearmController firearmController)
        {
            if (firearmController == null)
                return null;
            LoadRecordedGrips();
            releasePresses.TryGetValue(AnimationFamily(firearmController), out ReleasePress press);
            return press;
        }

        // True when the off hand presses this gun's catch, on the off controller's button, instead of the gun hand's finger
        private static bool ThumbWorksCatch(FirearmController firearmController)
        {
            return RockInOf(firearmController) != null || ThumbCatchOf(firearmController) != null || BeltBoxReleasedByOffHand(firearmController)
                || (firearmController != null && offHandCatchGuns.Contains(AnimationFamily(firearmController)))
                || (ReleasePressOf(firearmController)?.offHand == true);
        }

        // These guns' catch answers either hand's button: the gun hand's, as on an M4, or the off controller's with that hand on the
        // magazine, as on a thumb catch. This overrides the rock-in's off-thumb-only rule (the Mk47, SA58 and the backport's NL545 also
        // rock their magazines in). The PM, PB and APS (and APB) have a heel catch that Tarkov's clips work with the off thumb, but a
        // pistol's off hand is on the grip, not the magazine, so the gun hand's button has to work it too.
        private static readonly HashSet<string> eitherHandCatchGuns = new HashSet<string> { "mk47", "sa58", "nl545_di", "pm", "pb", "aps" };

        private static bool EitherHandWorksCatch(FirearmController firearmController)
        {
            return firearmController != null && eitherHandCatchGuns.Contains(AnimationFamily(firearmController));
        }

        private static Vector3[] ThumbCatchOf(FirearmController firearmController)
        {
            if (firearmController == null)
                return null;
            LoadRecordedGrips();
            thumbCatches.TryGetValue(AnimationFamily(firearmController), out Vector3[] thumb);
            return thumb;
        }

        private static RockIn RockInOf(FirearmController firearmController)
        {
            if (firearmController == null)
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

        // The hinge in world space: the well itself, or a belt box's hooked edge
        private static Vector3 RockPivotWorld() => magazineWell.TransformPoint(heldRockIn.pivot);

        // Where a magazine rocked this far (0 = seated) sits in the well's frame: its seated position turned about the hinge
        private static Vector3 RockedLocalPosition(float position) => heldRockIn.pivot + RockRotation(position) * (seatedLocalPosition - heldRockIn.pivot);

        // The hand holds the magazine rigidly, so one point on it shows how far the hand has rocked it, whether by the arm or the wrist.
        // That point is the magazine's middle, fixed to the controller from the start of the track. The holding hand is MagazineHandRig
        // (a belt box may be in the gun hand). For a belt box the box body's middle is used: the middle of everything it draws sits far
        // off (the belt and spent links, still in the model's rest shape on a box just taken off the gun), which would make a 25-30 cm
        // lever and a long arc for the hand to sweep.
        // rockAwayInWell: the direction the seated box hangs off the well (from the well point to its body's middle).
        private static Vector3 rockAwayInWell;
        private static Quaternion rockHandRotationAtStart = Quaternion.identity;

        private static void StartRockLever()
        {
            Transform controller = MagazineHandRig;
            Vector3 bodyCenter = Vector3.zero;
            bool beltBox = heldRockIn.pivot != Vector3.zero && TryBeltBoxBodyCenter(heldProp, out bodyCenter);
            Vector3 centerInProp = beltBox ? bodyCenter : MagazineCenterInProp(heldProp);
            Vector3 middle = heldProp.transform.TransformPoint(centerInProp);
            rockLeverOnHand = controller.InverseTransformPoint(middle);
            rockHandRotationAtStart = controller.rotation;
            rockLeverStartInWell = magazineWell.InverseTransformVector(middle - RockPivotWorld());
            rockAwayInWell = beltBox ? (seatedLocalRotation * (centerInProp - propWellPointLocal)).normalized : Vector3.zero;
            if (beltBox && beltDebugLog && !railPullingOut)
                Plugin.MyLog.LogInfo($"[VRBelt] box going on: rocked about its edge from its body's middle "
                    + $"{((middle - RockPivotWorld()).magnitude * 100f):0.#} cm off the hinge (the whole model's middle: "
                    + $"{((heldProp.transform.TransformPoint(MagazineCenterInProp(heldProp)) - RockPivotWorld()).magnitude * 100f):0.#} cm)");
        }

        private static Vector3 RockLeverNow()
        {
            return MagazineHandRig.TransformPoint(rockLeverOnHand) - RockPivotWorld();
        }

        // A belt box going in is caught once hooked: its well point near where it sits at the full rock (around the hinge, which is off
        // the well) and turned about that way. AK magazines keep their own catch along the track.
        public static float rockHookCatchRadius = 0.06f;   // m from the box's well point to its hooked spot
        private static bool HookedOnPivot(Transform propTransform)
        {
            Vector3 hookedWellPoint = magazineWell.TransformPoint(heldRockIn.pivot + RockRotation(railLength) * (-heldRockIn.pivot));
            Vector3 wellPointNow = propTransform.TransformPoint(propWellPointLocal);
            Quaternion hookedRotation = magazineWell.rotation * (RockRotation(railLength) * seatedLocalRotation);
            return (wellPointNow - hookedWellPoint).magnitude <= rockHookCatchRadius && Quaternion.Angle(propTransform.rotation, hookedRotation) <= railCaptureAngle;
        }

        // The controller's move since the track started, as a turn about the hinge in track metres, and how far it is off the magazine.
        // Measured along the circle at the start rather than as an angle from the hinge: pushing up brings that point close to the
        // hinge, where the angle jumps around and knocks the magazine off the track. Going in, pushing it up seats it as well as rocking
        // it back does, so the tilt doesn't have to be right.
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
            // A belt box: pushing it straight up onto the gun (the way it hangs) also seats it. Coming off is the same in reverse: rocked out,
            // pulled away from the hinge or pulled straight down, whichever is furthest (BeltBoxComesOff).
            if (rockAwayInWell != Vector3.zero)
            {
                float alongAway = Vector3.Dot(moved, magazineWell.TransformDirection(rockAwayInWell).normalized);
                // The hand's own roll about the hinge's line, in the same sense as the box's. Held by its far end, a roll of the wrist turns
                // the box about the hand, which carries the lever point the other way round the hinge and would count as rocking it back in.
                Quaternion turned = MagazineHandRig.rotation * Quaternion.Inverse(rockHandRotationAtStart);
                if (turned.w < 0f)
                    turned = new Quaternion(-turned.x, -turned.y, -turned.z, -turned.w);
                float rolledDegrees = 2f * Mathf.Atan2(Vector3.Dot(new Vector3(turned.x, turned.y, turned.z), hinge), turned.w) * Mathf.Rad2Deg;
                float rolled = rolledDegrees / heldRockIn.angle * railLength;
                handAlongTrack = railPullingOut ? Mathf.Max(handAlongTrack, Mathf.Max(Mathf.Max(Vector3.Dot(moved, outward), alongAway), rolled))
                    : Mathf.Min(handAlongTrack, alongAway);
                lastRockTerms = new Vector3(rockedDegrees, Vector3.Dot(moved, outward), alongAway);
                lastRollDegrees = rolledDegrees;
            }
            handOffTrack = HandOffRockingMagazine();
        }

        // How far the controller is from where the hand holds the magazine as it sits now. Pushing further in doesn't count as coming off it.
        private static float HandOffRockingMagazine()
        {
            Vector3 hinge = magazineWell.TransformDirection(heldRockIn.axis).normalized;
            float turned = heldRockIn.angle * (railPosition - railPositionAtStart) / railLength;
            Vector3 heldPoint = Quaternion.AngleAxis(turned, hinge) * magazineWell.TransformVector(rockLeverStartInWell);
            Vector3 offset = RockLeverNow() - heldPoint;
            Vector3 outward = heldPoint.normalized;
            float alongOutward = Vector3.Dot(offset, outward);
            // A belt box coming off: pulling it away from the hinge is taking it out, not the hand coming off it (just as pushing toward
            // the hinge going in isn't)
            bool along = alongOutward < 0f || (railPullingOut && heldRockIn.pivot != Vector3.zero);
            return (along ? offset - outward * alongOutward : offset).magnitude;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Taking a belt box off its hinge (the PKM's) is going in, in reverse. There is no catch (the box's latch is on the box, under the
        // gripping hand), pulling away from the hinge takes it out rather than the hand coming off it, and once it has moved at all, a hand
        // too far off the track takes it along. It only rocks out beltBoxRockOutDegrees before the hand takes it: driven round its whole
        // hook arc, the drawn hand on its far end (~25 cm off the hinge) would sweep further than the real one and slide off it.
        public static float beltBoxRockOutDegrees = 8f;     // deg a belt box rocks off its edge before it comes away in the hand (it hooks on at its full angle)
        private static Vector3 lastRockTerms;                // for the take-out log: this frame's rock (deg), away from the hinge and down (m)
        private static float lastRollDegrees;               // and the hand's own roll about the hinge's line
        private static float boxPullLogTime;

        private static void BeltBoxComesOff(float trackPosition, float handOffTrack, bool gripReleased)
        {
            // Off once rocked this far (in track metres: the full hook angle is railLength)
            float freeAt = railLength * Mathf.Clamp(beltBoxRockOutDegrees / Mathf.Max(heldRockIn.angle, 1f), 0.05f, 1f);
            railPosition = Mathf.Clamp(trackPosition, 0f, freeAt);
            if (railPosition >= Mathf.Min(magazineCatchDistance, freeAt * 0.5f))
                magazineCatchPassed = true;
            if (beltDebugLog && Time.time >= boxPullLogTime)
            {
                boxPullLogTime = Time.time + 0.25f;
                Plugin.MyLog.LogInfo($"[VRBelt] box coming off: {railPosition / freeAt * 100f:0}% out (rock {lastRockTerms.x:0.#} deg, hand roll {lastRollDegrees:0.#} deg, away {lastRockTerms.y * 100f:0.#} cm, "
                    + $"down {lastRockTerms.z * 100f:0.#} cm), hand off it {handOffTrack * 100f:0.#} cm");
            }
            if (gripReleased)
            {
                // Let go part way out: it goes back in the gun (as a box let go part way in stays in the hand)
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBelt] box coming off: let go at {railPosition / freeAt * 100f:0}%, back in");
                CancelHold();
                return;
            }
            if (handOffTrack > rockInInsertEscape)
            {
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBelt] box coming off: hand {handOffTrack * 100f:0.#} cm off it at {railPosition / freeAt * 100f:0}% - "
                        + (magazineCatchPassed ? "it comes with the hand" : "it stays (not moved yet)"));
                if (magazineCatchPassed)
                    ComeFreeOfWell();
                else
                    CancelHold();
                return;
            }
            PlaceOnRail();
            if (trackPosition >= freeAt)
            {
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo("[VRBelt] box coming off: off the gun");
                ComeFreeOfWell();
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Past the catch only the hand holds the magazine: let go, it falls; pulled away, it comes with the hand
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
