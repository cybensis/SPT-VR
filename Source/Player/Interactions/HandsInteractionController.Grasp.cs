using System;
using EFT.Interactive;
using RootMotion.FinalIK;
using UnityEngine;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Player.Interactions
{
    // Closes the fingers of a hand holding a loot item onto it, with the item kept in the pose it was grabbed in. On the grab the item is
    // moved against the palm (TryGraspRestPosition), or between the index and the thumb if it is small (a pinch). Each finger then curls
    // from its rest pose with coupled joints until any part of it touches the item, and its middle and tip joints keep wrapping until they
    // touch too; the thumb is moved to the surface nearest its tip. The item's shape (GraspShape) starts as its parts' boxes and becomes
    // their convex hulls as those arrive. The grip is solved once per grab, and again (eased in) when the item moves in the hand or a hull
    // arrives. On Index controllers each finger closes onto the item unless the real finger is lifted off the controller, never past it.
    // Runs inside each hand's finger pass, on the pose that pass just wrote, and keeps that pass's RecoverAnimated bookkeeping up to date.
    internal partial class HandsInteractionController
    {
        [Tunable("Loot & hands", TunableKind.Log, "A line per grab: the shape and where it came from, pinch or palm, solve time, each finger's curl and contact.", Suggested = true)]
        public static bool graspDebug = false;                  // logs a line per grab: the shape, the grip, the solve time, each finger's contact
        [Tunable("Loot & hands", TunableKind.Tune, "How long the fingers take to close on an item.", Unit = "s")]
        public static float graspCloseTime = 0.12f;             // s the fingers take to close on it
        public static float graspOpenTime = 0.1f;               // s to open again on letting go
        public static float graspRetargetTime = 0.06f;          // s a re-solved grip eases over (a hull arriving, the item knocked in the hand)
        [Tunable("Loot & hands", TunableKind.Tune, "Knuckle bend at a full fist.", Unit = "°")]
        public static float graspMaxCurlDeg = 95f;              // the knuckle's bend in a full fist (the middle joint the same, the tip 0.7 of it)
        public static float graspNoContactCurlDeg = 70f;        // a finger that never reaches the item stops here (relaxed, not a fist)
        public static float graspOpenDeg = 10f;                 // how far a finger may open past its rest pose for an item already against it
        public static float graspWrapDeg = 60f;                 // how much further the middle and tip joints may wrap past the coupled curl
        public static float graspSweepStepDeg = 5f;
        public static float[] graspFingerRadius = { 0.0095f, 0.0085f, 0.0075f };    // m: half a finger's thickness, base, middle, tip
        public static float graspThumbPad = 0.008f;             // m the thumb's tip stays off the surface
        public static float graspThumbMaxBendDeg = 55f;         // per joint, from its rest pose
        public static float graspThumbReach = 0.08f;            // m: a thumb tip further than this from the item stays as it is
        public static float graspPalmAlong = 0.6f;              // 0..1, the palm's contact point between the palm bone (the wrist end) and the knuckles
        public static float graspPalmSkin = 0.016f;             // m from the bones to the palm's skin
        public static float graspPalmHalfWidth = 0.03f;         // m: the palm's patch an item can't be inside of, across...
        public static float graspPalmHalfLength = 0.035f;       // ...and along (wrist to knuckles)
        public static float pinchMaxLength = 0.075f;            // m: an item this short and...
        public static float pinchMaxWidth = 0.03f;              // ...this narrow is pinched between the index and the thumb
        public static float pinchIndexCurlDeg = 35f;            // the index curl at which the pinch point is measured
        public static float pinchOtherCurlDeg = 55f;            // the curl of the fingers that don't pinch (relaxed)
        // On Index controllers a finger resting on the controller counts as holding, so it closes onto the item; only a lifted one opens.
        // These are the real curls (0 open .. 1 closed) at which a finger is fully open and fully on the item, for the fingers and for
        // the thumb (which rests higher, on the stick).
        public static float graspFingerOpenCurl = 0.15f, graspFingerClosedCurl = 0.5f;
        public static float graspThumbOpenCurl = 0.3f, graspThumbClosedCurl = 0.65f;

        private static readonly float[] coupledCurl = { 1f, 1f, 0.7f };   // knuckle : middle : tip, for a finger closing freely

        private sealed class GraspHand
        {
            public Component item;                              // the loot item, or the door / door handle (PhysicalDoors)
            public Transform rig;                               // what this hand's arm reaches for while it holds it
            public GraspShape shape;
            public bool ownsShape;                              // false = the other hand's (two hands on one item share it)
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
                if (ownsShape)
                    shape?.Dispose();
                shape = null;
                ownsShape = false;
                item = null;
                rig = null;
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

        // sharedShape: the other hand's shape, when this hand takes hold of an item the other hand already holds (the other hand disposes it)
        private void BeginGrasp(GraspHand hand, Component item, GraspShape sharedShape = null)
        {
            hand.Clear();
            hand.item = item;
            if (sharedShape != null)
            {
                hand.shape = sharedShape;
                return;
            }
            hand.ownsShape = true;
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
            hand.pinch = hand.shape != null && hand.shape.FitsAPinch(pinchMaxLength, pinchMaxWidth);
        }

        // For BeginSummon: where the item comes to rest in the hand. Its rotation is kept. It is moved until its nearest point is on the
        // palm's contact point (the index's pad for a pinch), then out along the palm's normal until no point of a palm-sized patch just
        // under the skin is inside it. Only that patch is tested: a long item held at an angle has ends well behind the palm's plane,
        // which must not push it off the hand. Uses the parts' boxes and the hulls of readable meshes (built at the grab).
        private bool TryGraspRestPosition(LootItem item, bool dominant, out Vector3 itemTarget)
        {
            itemTarget = item.transform.position;
            GraspHand hand = graspHands[dominant ? 1 : 0];
            BeginGrasp(hand, item);
            if (hand.shape == null || !TryGraspItemShift(dominant, hand.shape, hand.pinch, item.transform.position, item.transform.rotation, out Vector3 shift))
                return false;
            itemTarget = item.transform.position + shift;
            return true;
        }

        // How far (in world space) the item would move to sit in this hand's grasp: its nearest point onto the palm's contact point, then out
        // until the palm's patch isn't inside it. When a second hand takes a held item, that hand moves by minus this instead of the item
        // (HandsInteractionController.TwoHanded).
        private bool TryGraspItemShift(bool dominant, GraspShape shape, bool pinch, Vector3 itemPosition, Quaternion itemRotation, out Vector3 shift)
        {
            shift = Vector3.zero;
            if (!TryGetGraspContact(dominant, pinch, out Vector3 contact, out Vector3 normal, out Vector3 along))
                return false;
            Vector3 movedTo = itemPosition;
            // This also works with the palm inside the item: the nearest way out is moved onto the palm
            if (shape.NearestSurface(contact, movedTo, itemRotation, out Vector3 surface, out _, out _))
                movedTo += contact - surface;
            Vector3 across = Vector3.Cross(normal, along);
            float halfAcross = pinch ? 0.008f : graspPalmHalfWidth;
            float halfAlong = pinch ? 0.01f : graspPalmHalfLength;
            for (int step = 0; step < 20; step++)
            {
                bool palmInside = false;
                for (int row = -1; row <= 1 && !palmInside; row++)
                {
                    for (int column = -1; column <= 1 && !palmInside; column++)
                    {
                        Vector3 underSkin = contact + along * (row * halfAlong) + across * (column * halfAcross) - normal * 0.002f;
                        palmInside = shape.Touches(underSkin, 0.0005f, movedTo, itemRotation);
                    }
                }
                if (!palmInside)
                    break;
                movedTo += normal * 0.003f;
            }
            shift = movedTo - itemPosition;
            return true;
        }

        // Gets the palm's contact point, the palm's outward normal there (the direction the fingers close) and the direction along the palm
        // toward the knuckles. For a pinch, the index's pad instead (along = toward its tip).
        private bool TryGetGraspContact(bool dominant, bool pinch, out Vector3 contact, out Vector3 normal, out Vector3 along)
        {
            contact = normal = along = Vector3.zero;
            FingerHand hand = ResolvedFingers(dominant);
            Transform[][] fingers = hand.joints;
            bool[] thumbs = hand.isThumb;
            float curlSign = CurlSign(hand);
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
            // The index's pad with the finger partly curled: where a pinched item sits, facing the way that fingertip closes
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

        // Runs after a hand's finger pass has written its pose (weight = that pass's own blend). dominant = this is the dominant hand's pass.
        private void ApplyGrasp(FingerHand fingerHand)
        {
            bool dominant = fingerHand.dominant;
            Transform[][] fingers = fingerHand.joints;
            bool[] thumbs = fingerHand.isThumb;
            float curlSign = CurlSign(fingerHand);
            float weight = fingerHand.weight;
            GraspHand hand = graspHands[dominant ? 1 : 0];
            // What this hand holds: a loot item, or a door's handle / the door itself (PhysicalDoors)
            bool lootHere = HoldsLoot(dominant) && GraspRig(dominant) != null && HeldItemAlive();
            Component holding = null;
            Transform holdingRig = null;
            if (lootHere)
            {
                holding = heldItem;
                holdingRig = GraspRig(dominant);
            }
            else if (PhysicalDoors.GraspTarget(dominant, out Component doorPart, out Transform doorLatch))
            {
                holding = doorPart;
                holdingRig = doorLatch;
            }
            bool keyHere = false;
            if (holding == null && PhysicalDoors.KeyGraspTarget(dominant, out Component key))
            {
                // The key is parented to the palm bone itself (PhysicalDoors.SpawnKey)
                holding = key;
                holdingRig = key.transform.parent;
                keyHere = true;
            }
            bool holdingHere = holding != null;
            // A hold that didn't start in BeginSummon, or a second hand on the item (HandsInteractionController.TwoHanded). A door's
            // shape isn't shared, because either hand may let go first.
            if (holdingHere && hand.item != holding)
            {
                GraspHand otherHand = graspHands[dominant ? 0 : 1];
                BeginGrasp(hand, holding, lootHere && dominant != heldInDominantHand && otherHand.item == heldItem ? otherHand.shape : null);
                // A key is pinched whatever its size: Tarkov holds it on the index
                if (keyHere)
                    hand.pinch = hand.shape != null;
            }
            if (holdingHere)
                hand.rig = holdingRig;
            if (hand.item == null)
                return;
            hand.released = !holdingHere;
            float closed = !holdingHere ? 0f : lootHere && isSummoningLoot && dominant == heldInDominantHand ? Mathf.InverseLerp(0.5f, 1f, summonT) : 1f;
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

        // On Index controllers: how far a finger closes onto the item, from how curled the real finger is (resting on the controller = fully on it)
        private static float GraspCurlAmount(float[] curls, bool[] thumbs, int finger)
        {
            if (curls == null || curls.Length < 5)
                return 1f;
            float curl = curls[CurlIndex(thumbs, finger)];
            return IsThumb(thumbs, finger)
                ? Mathf.InverseLerp(graspThumbOpenCurl, graspThumbClosedCurl, curl)
                : Mathf.InverseLerp(graspFingerOpenCurl, graspFingerClosedCurl, curl);
        }

        // Re-solves the grip when the item sits differently in the palm (a summon landing, the hand pushed against a wall) or a hull has arrived
        private void UpdateGraspSolve(GraspHand hand, bool dominant, Transform[][] fingers, bool[] thumbs, float curlSign)
        {
            Transform palm = FingersPalm(fingers);
            if (palm == null)
                return;
            hand.palm = palm;
            // The item in the frame of what this hand's arm reaches for (its rig hand, the second hand's grip on the item, or the latch on a
            // door): where the summon will put it, or where it is now
            Transform rig = hand.rig;
            if (rig == null || hand.item == null)
                return;
            Transform item = hand.item.transform;
            Vector3 itemInRigPosition;
            Quaternion itemInRigRotation;
            Quaternion rigInverse = Quaternion.Inverse(rig.rotation);
            if (isSummoningLoot && rig == cachedHand && hand.item == heldItem)
            {
                itemInRigPosition = rigInverse * (cachedHand.TransformPoint(summonRestOffset) - cachedHand.position);
                itemInRigRotation = cachedGrabRotation;
            }
            else
            {
                itemInRigPosition = rigInverse * (item.position - rig.position);
                itemInRigRotation = rigInverse * item.rotation;
            }
            // ...and in the palm's frame: through the arm IK's last result, or, if none was captured, assuming the palm is on the rig hand (the IK's target)
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
        // The solve: every joint's pose on the item, starting from the pose the finger pass gave this frame (the bones are put back to it afterwards)

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

        // The finger curls as one (coupledCurl) until any part of it touches, then its middle and tip joints keep wrapping until they touch
        // too. A finger that never reaches the item stops at graspNoContactCurlDeg.
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

        // Moves the thumb's tip to the surface nearest it by CCD, tip joint first (as in ThumbOntoCase), each joint kept within
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

        // Whether any part of the finger from fromSegment outward touches the item. Two spheres per segment (its middle and its end);
        // a segment's start is the joint that bends it, which doesn't move.
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

        // The furthest amount in [from, to] with nothing touching: found in steps, then refined by halving where it starts touching
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

        // Captures the rig hand in the palm's frame as the arm IK leaves it. The IK solves after the Player's LateUpdate (EatingInteractionController's
        // pins hook the same place), so during our finger pass the palm can still be where the animator put it.
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
            if (this == null || armIk == null || armIk.solver == null || armIk.solver.bone3 == null)
                return;
            Transform bone = armIk.solver.bone3.transform;
            for (int handIndex = 0; handIndex < graspHands.Length; handIndex++)
            {
                GraspHand hand = graspHands[handIndex];
                Transform rig = hand.rig;
                if (bone == null || rig == null || hand.item == null || hand.palm != bone)
                    continue;
                Quaternion boneInverse = Quaternion.Inverse(bone.rotation);
                hand.rigInPalmPosition = boneInverse * (rig.position - bone.position);
                hand.rigInPalmRotation = boneInverse * rig.rotation;
                hand.rigInPalmTime = Time.time;
            }
        }

        // The palm bone a hand's fingers hang from (PhysicalDoors parents the key to it)
        internal Transform PalmBone(bool dominant) => FingersPalm(ResolvedFingers(dominant).joints);

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

        // The direction a point on the finger moves as this joint curls (flexion axis curlAxis, closing with curlSign); it points out of the palm
        private static Vector3 ClosingDirection(Transform joint, Vector3 point, float curlSign)
        {
            Vector3 swing = Vector3.Cross(joint.rotation * curlAxis, point - joint.position) * Mathf.Sign(curlSign);
            return swing.sqrMagnitude > 1e-10f ? swing.normalized : Vector3.zero;
        }
    }
}
