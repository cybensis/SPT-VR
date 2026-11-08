using System;
using EFT.Interactive;
using RootMotion.FinalIK;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // The hand holding a loot item closes on it, the item kept the way it was grabbed. On the grab the item comes up against the palm
    // (TryGraspRestPosition: no longer sunk into it, which a closing hand would show), or between the index and the thumb for a small
    // one (a round, a key: a pinch). Each finger then curls from its rest pose with its joints coupled like a real finger's until any
    // part of it touches the item, and its middle and tip joints wrap on until they touch too; the thumb is led to the surface nearest
    // its tip (the stuck case's thumb, ThumbOntoCase, for every item). Against the item's shape (GraspShape): its parts' boxes at once,
    // their convex hulls as they arrive. Solved once per grab and again when the item moves in the hand or a hull arrives, eased in.
    // Index controllers: each finger closes onto it unless the real one is lifted off the controller, never past the item.
    // Runs inside each hand's finger pass, on the pose that pass just wrote, and keeps its RecoverAnimated bookkeeping.
    internal partial class HandsInteractionController
    {
        public static bool graspItems = true;                   // false = the open hand on a held item, and the old placement sunk into the palm
        public static bool graspDebug = false;                  // a line per grab: the shape, the grip, the solve time, each finger's contact
        public static float graspCloseTime = 0.12f;             // s the fingers take to close on it
        public static float graspOpenTime = 0.1f;               // s to open again on letting go
        public static float graspRetargetTime = 0.06f;          // s a re-solved grip eases over (a hull arriving, the item knocked in the hand)
        public static float graspMaxCurlDeg = 95f;              // the knuckle's bend at a full fist (the middle joint the same, the tip 0.7 of it)
        public static float graspNoContactCurlDeg = 70f;        // a finger that never reaches the item stops here (relaxed, not a fist)
        public static float graspOpenDeg = 10f;                 // how far a finger may open past its rest pose for an item already against it
        public static float graspWrapDeg = 60f;                 // how much further the middle and tip joints may wrap past the coupled curl
        public static float graspSweepStepDeg = 5f;
        public static float[] graspFingerRadius = { 0.0095f, 0.0085f, 0.0075f };    // m: half a finger's thickness, base, middle, tip
        public static float graspThumbPad = 0.008f;             // m the thumb's tip stays off the surface
        public static float graspThumbMaxBendDeg = 55f;         // per joint, off its rest pose
        public static float graspThumbReach = 0.08f;            // m: a thumb tip further than this from the item stays as it is
        public static float graspPalmAlong = 0.6f;              // the palm's contact point: from the palm bone (the wrist end) toward the knuckles
        public static float graspPalmSkin = 0.016f;             // m from the bones to the palm's skin
        public static float graspPalmHalfWidth = 0.03f;         // m: the palm's patch an item can't be inside of, across...
        public static float graspPalmHalfLength = 0.035f;       // ...and along (wrist to knuckles)
        public static bool graspPinch = true;
        public static float pinchMaxLength = 0.075f;            // m: an item this short and...
        public static float pinchMaxWidth = 0.03f;              // ...this narrow is pinched between the index and the thumb
        public static float pinchIndexCurlDeg = 35f;            // the index's curl the pinch point is read at
        public static float pinchOtherCurlDeg = 55f;            // the fingers that don't pinch, relaxed
        // Index controllers: a finger resting on the controller is holding it, so it closes onto the item; only one lifted off opens.
        // Curl (0 open .. 1 closed) from fully open to fully on the item, fingers and thumb (it rests higher, on the stick).
        public static float graspFingerOpenCurl = 0.15f, graspFingerClosedCurl = 0.5f;
        public static float graspThumbOpenCurl = 0.3f, graspThumbClosedCurl = 0.65f;

        private static readonly float[] coupledCurl = { 1f, 1f, 0.7f };   // knuckle : middle : tip, a finger closing on its own

        private sealed class GraspHand
        {
            public LootItem item;
            public GraspShape shape;
            public bool pinch;
            public float blend;                                 // 0 open .. 1 closed on it
            public bool released;
            public Transform palm;
            public readonly Quaternion[] target = new Quaternion[15];
            public readonly Quaternion[] shown = new Quaternion[15];
            public readonly bool[] hasTarget = new bool[15];
            public bool solved;
            public bool hasShown;
            public Vector3 solvedItemPosition;                  // in the palm's frame
            public Quaternion solvedItemRotation;
            public int solvedShapeVersion = -1;
            public float lastSolveTime;
            public bool logged;
            // The rig hand the item hangs from, in the palm's frame as the arm IK left it (CaptureRigInPalm)
            public Vector3 rigInPalmPosition;
            public Quaternion rigInPalmRotation = Quaternion.identity;
            public float rigInPalmTime = -1f;

            public void Clear()
            {
                shape?.Dispose();
                shape = null;
                item = null;
                pinch = false;
                blend = 0f;
                released = false;
                solved = false;
                hasShown = false;
                logged = false;
                solvedShapeVersion = -1;
                for (int index = 0; index < hasTarget.Length; index++)
                    hasTarget[index] = false;
            }
        }

        private readonly GraspHand[] graspHands = { new GraspHand(), new GraspHand() };    // [0] the off hand, [1] the dominant hand
        private readonly Quaternion[] graspBasePose = new Quaternion[15];
        private LimbIK graspIkLeft, graspIkRight;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The grab

        private void BeginGrasp(GraspHand hand, LootItem item)
        {
            hand.Clear();
            hand.item = item;
            try
            {
                hand.shape = GraspShape.Build(item);
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogWarning($"[Grasp] {item.name}: no shape, the hand stays open: {exception.Message}");
                hand.shape = null;
            }
            if (hand.shape != null && hand.shape.PartCount == 0)
            {
                hand.shape.Dispose();
                hand.shape = null;
            }
            hand.pinch = graspPinch && hand.shape != null && hand.shape.FitsAPinch(pinchMaxLength, pinchMaxWidth);
        }

        // BeginSummon: where the item comes to rest in the hand. Kept the way it's turned; brought in until its nearest point is on the
        // palm's contact point (the index's pad for a pinch), then out along the palm's normal until no point of the palm (a patch the
        // palm's size, just under its skin) is inside it. Only the palm's patch: a long item held at an angle has ends well behind the
        // palm's plane, which must not push it off the hand. From the parts' boxes, and the hulls of readable meshes (built at the grab).
        private bool TryGraspRestPosition(LootItem item, bool dominant, out Vector3 itemTarget)
        {
            itemTarget = item.transform.position;
            if (!graspItems)
                return false;
            GraspHand hand = graspHands[dominant ? 1 : 0];
            BeginGrasp(hand, item);
            if (hand.shape == null || !TryGetGraspContact(dominant, hand.pinch, out Vector3 contact, out Vector3 normal, out Vector3 along))
                return false;
            Vector3 itemPosition = item.transform.position;
            Quaternion itemRotation = item.transform.rotation;
            // (a palm inside it: its nearest way out comes to the palm, as the old placement did)
            if (hand.shape.NearestSurface(contact, itemPosition, itemRotation, out Vector3 surface, out _, out _))
                itemPosition += contact - surface;
            Vector3 across = Vector3.Cross(normal, along);
            float halfAcross = hand.pinch ? 0.008f : graspPalmHalfWidth;
            float halfAlong = hand.pinch ? 0.01f : graspPalmHalfLength;
            for (int step = 0; step < 20; step++)
            {
                bool palmInside = false;
                for (int row = -1; row <= 1 && !palmInside; row++)
                {
                    for (int column = -1; column <= 1 && !palmInside; column++)
                    {
                        Vector3 underSkin = contact + along * (row * halfAlong) + across * (column * halfAcross) - normal * 0.002f;
                        palmInside = hand.shape.Touches(underSkin, 0.0005f, itemPosition, itemRotation);
                    }
                }
                if (!palmInside)
                    break;
                itemPosition += normal * 0.003f;
            }
            itemTarget = itemPosition;
            return true;
        }

        // The palm's contact point, the way out of the palm there (the way the fingers close) and the way along it toward the knuckles,
        // or the index's pad for a pinch (along = toward its tip)
        private bool TryGetGraspContact(bool dominant, bool pinch, out Vector3 contact, out Vector3 normal, out Vector3 along)
        {
            contact = normal = along = Vector3.zero;
            if (dominant)
            {
                if (!rightFingersResolved) ResolveRightFingers();
            }
            else if (!fingersResolved) ResolveLeftFingers();
            Transform[][] fingers = dominant ? rightFingerJoints : leftFingerJoints;
            bool[] thumbs = dominant ? rightFingerIsThumb : leftFingerIsThumb;
            float curlSign = dominant ? rightCurlSign : leftCurlSign;
            Transform palm = FingersPalm(fingers);
            if (palm == null)
                return false;
            Vector3 knuckles = Vector3.zero;
            Vector3 closing = Vector3.zero;
            int counted = 0;
            Transform[] index = null;
            for (int finger = 0; finger < fingers.Length; finger++)
            {
                Transform[] joints = fingers[finger];
                if (IsThumb(thumbs, finger) || joints == null || joints.Length < 2 || joints[0] == null || joints[1] == null)
                    continue;
                knuckles += joints[0].position;
                closing += ClosingDirection(joints[0], joints[1].position, curlSign);
                counted++;
                if (index == null || joints[0].name.IndexOf("Digit2", StringComparison.OrdinalIgnoreCase) >= 0)
                    index = joints;
            }
            if (counted == 0 || closing.sqrMagnitude < 1e-8f)
                return false;
            knuckles /= counted;
            normal = closing.normalized;
            contact = Vector3.Lerp(palm.position, knuckles, graspPalmAlong) + normal * graspPalmSkin;
            along = Vector3.ProjectOnPlane(knuckles - palm.position, normal);
            along = along.sqrMagnitude > 1e-8f ? along.normalized : Vector3.Cross(normal, Vector3.up).normalized;
            if (!pinch || index == null || index.Length < 3 || index[2] == null)
                return true;
            // The index's pad with the finger part curled: where a pinched item sits, facing the way that fingertip closes
            int jointCount = Mathf.Min(index.Length, 3);
            var rest = new Quaternion[jointCount];
            for (int joint = 0; joint < jointCount; joint++)
                rest[joint] = index[joint].localRotation;
            SetCoupledCurl(index, jointCount, rest, pinchIndexCurlDeg, curlSign);
            Vector3 tip = FingerTip(index[2]);
            Vector3 padNormal = ClosingDirection(index[2], tip, curlSign);
            if (padNormal.sqrMagnitude > 1e-8f)
            {
                normal = padNormal.normalized;
                contact = Vector3.Lerp(index[2].position, tip, 0.6f) + normal * graspFingerRadius[2];
                Vector3 fingerward = Vector3.ProjectOnPlane(tip - index[2].position, normal);
                if (fingerward.sqrMagnitude > 1e-8f)
                    along = fingerward.normalized;
            }
            for (int joint = 0; joint < jointCount; joint++)
                index[joint].localRotation = rest[joint];
            return true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Every frame, from the finger passes

        // After a hand's pass has written its pose (weight = that pass's own blend). dominant = the pass for the dominant hand.
        private void ApplyGrasp(bool dominant, Transform[][] fingers, bool[] thumbs, float curlSign, float weight)
        {
            GraspHand hand = graspHands[dominant ? 1 : 0];
            bool holdingHere = graspItems && heldItem != null && heldInDominantHand == dominant && cachedHand != null && HeldItemAlive();
            // A hold that didn't come through BeginSummon
            if (holdingHere && hand.item != heldItem)
                BeginGrasp(hand, heldItem);
            if (hand.item == null)
                return;
            if (!holdingHere)
                hand.released = true;
            float closed = !holdingHere ? 0f : isSummoningLoot ? Mathf.InverseLerp(0.5f, 1f, summonT) : 1f;
            hand.blend = Mathf.MoveTowards(hand.blend, closed, Time.deltaTime / Mathf.Max(0.01f, closed >= hand.blend ? graspCloseTime : graspOpenTime));
            if (hand.released && hand.blend <= 0f)
            {
                hand.Clear();
                return;
            }
            if (hand.shape == null || fingers == null)
                return;
            EnsureGraspIkHooks();
            if (holdingHere)
                UpdateGraspSolve(hand, dominant, fingers, thumbs, curlSign);
            if (!hand.hasShown)
                return;

            float retarget = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, graspRetargetTime));
            float close = Mathf.SmoothStep(0f, 1f, hand.blend) * weight;
            bool tracked = SkeletalActive(dominant);
            float[] curls = tracked ? HandSkeleton(dominant).fingerCurls : null;
            for (int finger = 0, index = 0; finger < fingers.Length; finger++)
            {
                Transform[] joints = fingers[finger];
                float amount = close * GraspCurlAmount(curls, thumbs, finger);
                for (int joint = 0; joint < 3; joint++, index++)
                {
                    if (joints == null || joint >= joints.Length || joints[joint] == null || index >= hand.target.Length || !hand.hasTarget[index])
                        continue;
                    hand.shown[index] = Quaternion.Slerp(hand.shown[index], hand.target[index], retarget);
                    Transform bone = joints[joint];
                    Quaternion pose = Quaternion.Slerp(bone.localRotation, hand.shown[index], amount);
                    bone.localRotation = pose;
                    // The pass's RecoverAnimated reads this next frame: what's on the bone is ours, not the animator's
                    fingerOutRot[bone] = pose;
                }
            }
        }

        // Index controllers: how far a finger closes onto the item, by how curled the real one is (resting on the controller = on it)
        private static float GraspCurlAmount(float[] curls, bool[] thumbs, int finger)
        {
            if (curls == null || curls.Length < 5)
                return 1f;
            float curl = curls[CurlIndex(thumbs, finger)];
            return IsThumb(thumbs, finger)
                ? Mathf.InverseLerp(graspThumbOpenCurl, graspThumbClosedCurl, curl)
                : Mathf.InverseLerp(graspFingerOpenCurl, graspFingerClosedCurl, curl);
        }

        // Re-solved when the item sits differently in the palm (a summon landing, the hand pushed against a wall), or a hull arrived
        private void UpdateGraspSolve(GraspHand hand, bool dominant, Transform[][] fingers, bool[] thumbs, float curlSign)
        {
            Transform palm = FingersPalm(fingers);
            if (palm == null)
                return;
            hand.palm = palm;
            // The item in the rig hand's frame: where the summon will put it, or where it is
            Vector3 itemInRigPosition;
            Quaternion itemInRigRotation;
            Quaternion rigInverse = Quaternion.Inverse(cachedHand.rotation);
            if (isSummoningLoot)
            {
                itemInRigPosition = rigInverse * (cachedHand.TransformPoint(summonRestOffset) - cachedHand.position);
                itemInRigRotation = cachedGrabRotation;
            }
            else
            {
                itemInRigPosition = rigInverse * (heldItem.transform.position - cachedHand.position);
                itemInRigRotation = rigInverse * heldItem.transform.rotation;
            }
            // ...and in the palm's: through the arm IK's last result, or taking the palm to be on the rig hand (the IK's target)
            bool captured = hand.rigInPalmTime >= 0f && Time.time - hand.rigInPalmTime < 0.5f;
            Vector3 itemInPalmPosition = captured ? hand.rigInPalmPosition + hand.rigInPalmRotation * itemInRigPosition : itemInRigPosition;
            Quaternion itemInPalmRotation = captured ? hand.rigInPalmRotation * itemInRigRotation : itemInRigRotation;

            bool stale = !hand.solved || hand.solvedShapeVersion != hand.shape.Version
                || (itemInPalmPosition - hand.solvedItemPosition).sqrMagnitude > 0.004f * 0.004f
                || Quaternion.Angle(itemInPalmRotation, hand.solvedItemRotation) > 4f;
            if (!stale || (hand.solved && Time.time - hand.lastSolveTime < 0.1f))
                return;

            float started = Time.realtimeSinceStartup;
            Vector3 itemPosition = palm.position + palm.rotation * itemInPalmPosition;
            Quaternion itemRotation = palm.rotation * itemInPalmRotation;
            string contacts = SolveGrasp(hand, fingers, thumbs, curlSign, itemPosition, itemRotation);
            hand.solved = true;
            hand.solvedShapeVersion = hand.shape.Version;
            hand.solvedItemPosition = itemInPalmPosition;
            hand.solvedItemRotation = itemInPalmRotation;
            hand.lastSolveTime = Time.time;
            if (!hand.hasShown)
            {
                Array.Copy(hand.target, hand.shown, hand.target.Length);
                hand.hasShown = true;
            }
            if (graspDebug)
            {
                Plugin.MyLog.LogInfo($"[Grasp] {(dominant ? "dominant" : "off")} hand on {hand.item.name} ({(hand.pinch ? "pinch" : "palm")}, IK palm {(captured ? "captured" : "assumed")}): "
                    + $"{hand.shape.Describe()}; solved in {(Time.realtimeSinceStartup - started) * 1000f:0.0} ms, fingers {contacts}");
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The solve: every joint's pose on the item, from the pose the pass gave this frame (which the bones are put back to after)

        private string SolveGrasp(GraspHand hand, Transform[][] fingers, bool[] thumbs, float curlSign, Vector3 itemPosition, Quaternion itemRotation)
        {
            for (int finger = 0, index = 0; finger < fingers.Length; finger++)
            {
                for (int joint = 0; joint < 3; joint++, index++)
                {
                    Transform[] joints = fingers[finger];
                    bool exists = index < graspBasePose.Length && joints != null && joint < joints.Length && joints[joint] != null;
                    if (exists)
                        graspBasePose[index] = joints[joint].localRotation;
                    if (index < hand.hasTarget.Length)
                        hand.hasTarget[index] = exists;
                }
            }
            var contacts = new System.Text.StringBuilder();
            for (int finger = 0; finger < fingers.Length && finger * 3 < graspBasePose.Length; finger++)
            {
                Transform[] joints = fingers[finger];
                if (joints == null || joints.Length == 0 || joints[0] == null)
                    continue;
                int jointCount = Mathf.Min(joints.Length, 3);
                for (int joint = 1; joint < jointCount; joint++)
                {
                    if (joints[joint] == null)
                        jointCount = joint;
                }
                var start = new Quaternion[jointCount];
                Array.Copy(graspBasePose, finger * 3, start, 0, jointCount);
                if (IsThumb(thumbs, finger))
                    contacts.Append(SolveThumb(hand, joints, jointCount, start, itemPosition, itemRotation) ? "T+ " : "T- ");
                else
                {
                    bool pinching = hand.pinch && joints[0].name.IndexOf("Digit2", StringComparison.OrdinalIgnoreCase) >= 0;
                    float limit = hand.pinch && !pinching ? Mathf.Min(pinchOtherCurlDeg, graspMaxCurlDeg) : graspMaxCurlDeg;
                    float curl = SolveFinger(hand, joints, jointCount, start, curlSign, limit, itemPosition, itemRotation, out bool touched);
                    contacts.Append(touched ? $"{curl:0}+ " : $"{curl:0}- ");
                }
                for (int joint = 0; joint < jointCount; joint++)
                {
                    hand.target[finger * 3 + joint] = joints[joint].localRotation;
                    joints[joint].localRotation = start[joint];
                }
            }
            return contacts.ToString().TrimEnd();
        }

        // The finger curls as one (coupledCurl) until any of it touches, then its middle and tip joints wrap on until they touch too.
        // Never reaching the item: it stops at graspNoContactCurlDeg.
        private float SolveFinger(GraspHand hand, Transform[] joints, int jointCount, Quaternion[] start, float curlSign, float limit, Vector3 itemPosition, Quaternion itemRotation, out bool touched)
        {
            float curl = Sweep(-graspOpenDeg, limit, amount =>
            {
                SetCoupledCurl(joints, jointCount, start, amount, curlSign);
                return FingerTouches(hand.shape, joints, jointCount, 0, itemPosition, itemRotation);
            }, out touched);
            if (!touched)
                curl = Mathf.Min(limit, graspNoContactCurlDeg);
            SetCoupledCurl(joints, jointCount, start, curl, curlSign);
            if (!touched)
                return curl;
            for (int joint = 1; joint < jointCount; joint++)
            {
                Quaternion from = joints[joint].localRotation;
                int bending = joint;
                float wrap = Sweep(0f, graspWrapDeg, amount =>
                {
                    joints[bending].localRotation = from * Quaternion.AngleAxis(amount * curlSign, curlAxis);
                    return FingerTouches(hand.shape, joints, jointCount, bending, itemPosition, itemRotation);
                }, out _);
                joints[bending].localRotation = from * Quaternion.AngleAxis(wrap * curlSign, curlAxis);
            }
            return curl;
        }

        // The thumb led to the surface nearest its tip, tip joint first (ThumbOntoCase's CCD), each joint held within
        // graspThumbMaxBendDeg of its rest pose
        private bool SolveThumb(GraspHand hand, Transform[] joints, int jointCount, Quaternion[] start, Vector3 itemPosition, Quaternion itemRotation)
        {
            Transform tipJoint = joints[jointCount - 1];
            bool reached = false;
            for (int iteration = 0; iteration < 8; iteration++)
            {
                Vector3 tip = FingerTip(tipJoint);
                if (!hand.shape.NearestSurface(tip, itemPosition, itemRotation, out Vector3 surface, out Vector3 outward, out bool inside))
                    return false;
                if (iteration == 0 && !inside && (surface - tip).sqrMagnitude > graspThumbReach * graspThumbReach)
                    return false;
                Vector3 target = surface + outward * graspThumbPad;
                if ((tip - target).sqrMagnitude < 0.0015f * 0.0015f)
                {
                    reached = true;
                    break;
                }
                for (int joint = jointCount - 1; joint >= 0; joint--)
                {
                    Transform bone = joints[joint];
                    tip = FingerTip(tipJoint);
                    bone.rotation = Quaternion.FromToRotation(tip - bone.position, target - bone.position) * bone.rotation;
                    bone.localRotation = Quaternion.RotateTowards(start[joint], bone.localRotation, graspThumbMaxBendDeg);
                }
                reached = true;
            }
            return reached;
        }

        private static void SetCoupledCurl(Transform[] joints, int jointCount, Quaternion[] start, float curl, float curlSign)
        {
            for (int joint = 0; joint < jointCount; joint++)
                joints[joint].localRotation = start[joint] * Quaternion.AngleAxis(curl * coupledCurl[joint] * curlSign, curlAxis);
        }

        // Any of the finger from fromSegment out touches the item: two spheres a segment (its middle and its end), a segment's own start
        // being the joint that bends it, which doesn't move
        private static bool FingerTouches(GraspShape shape, Transform[] joints, int jointCount, int fromSegment, Vector3 itemPosition, Quaternion itemRotation)
        {
            for (int segment = fromSegment; segment < jointCount; segment++)
            {
                Vector3 start = joints[segment].position;
                Vector3 end = segment + 1 < jointCount ? joints[segment + 1].position : FingerTip(joints[segment]);
                float radius = graspFingerRadius[Mathf.Min(segment, graspFingerRadius.Length - 1)];
                if (shape.Touches(Vector3.Lerp(start, end, 0.5f), radius, itemPosition, itemRotation) || shape.Touches(end, radius, itemPosition, itemRotation))
                    return true;
            }
            return false;
        }

        // The furthest amount in [from, to] with nothing touching: stepped, then halved down to a tenth of a step where it starts touching
        private static float Sweep(float from, float to, Func<float, bool> touches, out bool touched)
        {
            touched = true;
            if (touches(from))
                return from;
            float clear = from;
            while (clear < to)
            {
                float next = Mathf.Min(clear + graspSweepStepDeg, to);
                if (touches(next))
                {
                    float blocked = next;
                    for (int halving = 0; halving < 4; halving++)
                    {
                        float middle = (clear + blocked) * 0.5f;
                        if (touches(middle))
                            blocked = middle;
                        else
                            clear = middle;
                    }
                    return clear;
                }
                clear = next;
            }
            touched = false;
            return to;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // The rig hand in the palm's frame, as the arm IK leaves it: the IK solves after the Player's LateUpdate (EatingInteractionController's
        // pins hook the same place), so in our pass the palm can still be where the animator put it
        private void EnsureGraspIkHooks()
        {
            LimbIK left = VRGlobals.ikManager != null ? VRGlobals.ikManager.leftArmIk : null;
            LimbIK right = VRGlobals.ikManager != null ? VRGlobals.ikManager.rightArmIk : null;
            if (left != graspIkLeft)
            {
                if (graspIkLeft != null && graspIkLeft.solver != null) graspIkLeft.solver.OnPostUpdate -= CaptureLeftArmAfterIk;
                graspIkLeft = left;
                if (left != null && left.solver != null) left.solver.OnPostUpdate += CaptureLeftArmAfterIk;
            }
            if (right != graspIkRight)
            {
                if (graspIkRight != null && graspIkRight.solver != null) graspIkRight.solver.OnPostUpdate -= CaptureRightArmAfterIk;
                graspIkRight = right;
                if (right != null && right.solver != null) right.solver.OnPostUpdate += CaptureRightArmAfterIk;
            }
        }

        private void CaptureLeftArmAfterIk() => CaptureRigInPalm(graspIkLeft);
        private void CaptureRightArmAfterIk() => CaptureRigInPalm(graspIkRight);

        private void CaptureRigInPalm(LimbIK armIk)
        {
            if (this == null || armIk == null || armIk.solver == null || armIk.solver.bone3 == null || cachedHand == null || heldItem == null)
                return;
            Transform bone = armIk.solver.bone3.transform;
            GraspHand hand = graspHands[heldInDominantHand ? 1 : 0];
            if (bone == null || hand.item == null || hand.palm != bone)
                return;
            Quaternion boneInverse = Quaternion.Inverse(bone.rotation);
            hand.rigInPalmPosition = boneInverse * (cachedHand.position - bone.position);
            hand.rigInPalmRotation = boneInverse * cachedHand.rotation;
            hand.rigInPalmTime = Time.time;
        }

        private static Transform FingersPalm(Transform[][] fingers)
        {
            if (fingers == null)
                return null;
            foreach (Transform[] joints in fingers)
            {
                if (joints != null && joints.Length > 0 && joints[0] != null)
                    return joints[0].parent;
            }
            return null;
        }

        private static bool IsThumb(bool[] thumbs, int finger) => thumbs != null && finger < thumbs.Length && thumbs[finger];

        // The rig has no fingertip bone: about 2.5 cm past the last joint, along the finger (GetHandTouchPoints, TryGetPinch)
        private static Vector3 FingerTip(Transform lastJoint) => lastJoint.TransformPoint(-0.025f, 0f, 0f);

        // Which way a point on the finger moves as this joint curls (its flexion axis is curlAxis, closing with curlSign): out of the palm
        private static Vector3 ClosingDirection(Transform joint, Vector3 point, float curlSign)
        {
            Vector3 swing = Vector3.Cross(joint.rotation * curlAxis, point - joint.position) * Mathf.Sign(curlSign);
            return swing.sqrMagnitude > 1e-10f ? swing.normalized : Vector3.zero;
        }
    }
}
