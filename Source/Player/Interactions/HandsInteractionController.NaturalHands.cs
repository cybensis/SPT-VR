using System.Collections.Generic;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // Poses the free hands' fingers from Tarkov's own empty-hands poses (empty hands' empty_hands_look, the character looking at his
    // hands: relaxed at t 0.75, fully open at t 1.8, a fist at t 0.25 left / 1.2 right; every joint's local rotation, per hand). Each
    // finger blends between them by its curl: resting = relaxed, closed a little toward the fist (naturalRestClose); squeezed = the
    // fist; lifted = open. Index controllers give each finger's curl. Other controllers rest relaxed (the off hand curls a little
    // toward the fist while the loot pointer has something).
    // A hand holding a loot item or a door keeps the baked rest pose (the grasp solves from it). naturalHands = false uses the baked
    // open pose and its curl.
    internal partial class HandsInteractionController
    {
        public static bool naturalHands = true;
        public static float naturalReadyCurl = 0.25f;       // share of the way to the fist the off hand closes while pointing at loot (no finger tracking)
        // The rest pose sits this share of the way from Tarkov's relaxed hand to its fist (a hand ready to take something).
        // The thumb has its own value.
        public static float naturalRestClose = 0.35f;
        public static float naturalThumbRestClose = 0.2f;
        // deg the fingers fan apart at rest, between neighbours: the index toward the thumb, the ring and pinky away from the middle
        // (the pinky twice as far). The turn is about the knuckle's spread axis, its parent's y, which Tarkov's own poses fan about
        // (negative toward the pinky on the left, positive on the right). It fades out toward the fist.
        public static float naturalRestSpread = 5f;

        // Thumb, index, middle, ring, pinky (Base HumanXDigit11..53), three joints each, local eulers
        private static readonly Vector3[] LEFT_OPEN =
        {
            new Vector3(60.2f, 54.23f, 30.79f), new Vector3(359.97f, 359.97f, 0f), new Vector3(359.69f, 0.06f, 0.14f),
            new Vector3(359.9f, 360f, 0f), new Vector3(359.96f, 359.99f, 0.04f), new Vector3(359.93f, 359.95f, 0.06f),
            new Vector3(359.9f, 360f, 0.01f), new Vector3(359.96f, 359.98f, 0.05f), new Vector3(359.92f, 359.96f, 0.13f),
            new Vector3(359.9f, 356.15f, 0.02f), new Vector3(359.96f, 359.96f, 0.05f), new Vector3(359.93f, 359.94f, 0.14f),
            new Vector3(359.9f, 352.12f, 0.03f), new Vector3(359.97f, 359.94f, 0.05f), new Vector3(359.95f, 359.92f, 0.16f),
        };
        private static readonly Vector3[] LEFT_RELAXED =
        {
            new Vector3(60.2f, 54.23f, 30.79f), new Vector3(359.97f, 359.97f, 0f), new Vector3(359.69f, 0.06f, 0.14f),
            new Vector3(0.75f, 354.69f, 10.12f), new Vector3(359.81f, 359.96f, 10.19f), new Vector3(359.74f, 359.91f, 10.23f),
            new Vector3(1.47f, 354.89f, 18.47f), new Vector3(359.78f, 360f, 18.56f), new Vector3(359.71f, 359.99f, 18.63f),
            new Vector3(2.4f, 352.63f, 19.64f), new Vector3(359.78f, 0.02f, 19.82f), new Vector3(359.7f, 0.02f, 19.91f),
            new Vector3(4.18f, 349.11f, 21.86f), new Vector3(359.8f, 0.01f, 22.28f), new Vector3(359.72f, 0.03f, 22.38f),
        };
        private static readonly Vector3[] LEFT_FIST =
        {
            new Vector3(71.2f, 96.11f, 74.95f), new Vector3(0f, 359.96f, 31.05f), new Vector3(359.92f, 0.04f, 0.41f),
            new Vector3(14.93f, 353.32f, 65.61f), new Vector3(359.95f, 359.86f, 82.8f), new Vector3(359.9f, 359.78f, 50.38f),
            new Vector3(7.5f, 355.57f, 76.48f), new Vector3(359.86f, 359.91f, 74.15f), new Vector3(359.84f, 359.83f, 61.77f),
            new Vector3(6.16f, 350.06f, 78.67f), new Vector3(359.83f, 359.92f, 77.09f), new Vector3(359.81f, 359.84f, 64.85f),
            new Vector3(5.84f, 350.12f, 78.74f), new Vector3(359.83f, 359.93f, 77.17f), new Vector3(359.79f, 359.88f, 58.43f),
        };
        private static readonly Vector3[] RIGHT_OPEN =
        {
            new Vector3(321.6f, 323.2f, 20.29f), new Vector3(0f, 0f, 0f), new Vector3(0.02f, 0.02f, 0.03f),
            new Vector3(0.04f, 5.01f, 0.02f), new Vector3(0.01f, 0.09f, 0.03f), new Vector3(0.02f, 0.17f, 0.07f),
            new Vector3(0.05f, 5.77f, 0.03f), new Vector3(0.02f, 0.11f, 0.06f), new Vector3(0.04f, 0.2f, 0.13f),
            new Vector3(0.05f, 8.67f, 0.03f), new Vector3(0.04f, 0.16f, 0.07f), new Vector3(0.07f, 0.27f, 0.17f),
            new Vector3(0.06f, 11.81f, 0.04f), new Vector3(0.06f, 0.2f, 0.08f), new Vector3(0.1f, 0.32f, 0.2f),
        };
        private static readonly Vector3[] RIGHT_RELAXED =
        {
            new Vector3(321.6f, 323.2f, 20.29f), new Vector3(0.01f, 0.05f, 9.3f), new Vector3(0.05f, 0.24f, 9.38f),
            new Vector3(359.96f, 6.41f, 10.99f), new Vector3(359.81f, 0.29f, 11.02f), new Vector3(359.73f, 0.42f, 11.1f),
            new Vector3(0.02f, 7.99f, 19.61f), new Vector3(359.92f, 0.14f, 19.65f), new Vector3(359.9f, 0.22f, 19.76f),
            new Vector3(0.04f, 11.88f, 24.86f), new Vector3(359.95f, 0.16f, 24.9f), new Vector3(359.93f, 0.24f, 25.02f),
            new Vector3(0.05f, 15f, 28.3f), new Vector3(359.96f, 0.17f, 28.33f), new Vector3(359.94f, 0.24f, 28.46f),
        };
        private static readonly Vector3[] RIGHT_FIST =
        {
            new Vector3(296.75f, 283.11f, 55.4f), new Vector3(360f, 0f, 40f), new Vector3(0.01f, 0.02f, 3.12f),
            new Vector3(346.13f, 8.55f, 70.45f), new Vector3(352.56f, 0.91f, 83.3f), new Vector3(359.92f, 0.16f, 45.65f),
            new Vector3(351.65f, 4.76f, 76.54f), new Vector3(356.36f, 0.97f, 76.63f), new Vector3(359.85f, 0.18f, 76.86f),
            new Vector3(351.72f, 4.78f, 82.09f), new Vector3(2.64f, 359.57f, 76.69f), new Vector3(359.8f, 0.31f, 76.96f),
            new Vector3(351.78f, 4.8f, 82.1f), new Vector3(2.62f, 359.66f, 76.72f), new Vector3(359.54f, 359.79f, 77.02f),
        };

        private sealed class NaturalPose
        {
            public Quaternion[] open, relaxed, fist;
        }
        private static NaturalPose leftNatural, rightNatural;

        private static NaturalPose NaturalPoseOf(bool rightBones)
        {
            ref NaturalPose pose = ref rightBones ? ref rightNatural : ref leftNatural;
            if (pose == null)
                pose = new NaturalPose
                {
                    open = ToQuaternions(rightBones ? RIGHT_OPEN : LEFT_OPEN),
                    relaxed = ToQuaternions(rightBones ? RIGHT_RELAXED : LEFT_RELAXED),
                    fist = ToQuaternions(rightBones ? RIGHT_FIST : LEFT_FIST),
                };
            return pose;
        }

        private static Quaternion[] ToQuaternions(Vector3[] eulers)
        {
            Quaternion[] rotations = new Quaternion[eulers.Length];
            for (int index = 0; index < eulers.Length; index++)
                rotations[index] = Quaternion.Euler(eulers[index]);
            return rotations;
        }

        // A finger joint's index into the pose arrays, from its name (Base HumanLDigit23 = the index finger's third joint: 1 * 3 + 2).
        // Read by name so the palm's child order doesn't matter. -1 = not a finger joint.
        private static readonly Dictionary<Transform, int> naturalSlots = new Dictionary<Transform, int>();

        private static int NaturalSlot(Transform joint)
        {
            if (naturalSlots.TryGetValue(joint, out int slot))
                return slot;
            slot = -1;
            string name = joint.name;
            int at = name.IndexOf("Digit", System.StringComparison.Ordinal);
            if (at >= 0 && name.Length >= at + 7)
            {
                int finger = name[at + 5] - '1';
                int part = name[at + 6] - '1';
                if (finger >= 0 && finger < 5 && part >= 0 && part < 3)
                    slot = finger * 3 + part;
            }
            naturalSlots[joint] = slot;
            return slot;
        }

        // Blends the free hand's fingers between the poses by their curls (Index), else uses the relaxed pose moved toward the fist by
        // ready. The result is slerped from the animated pose by the hand's weight. dominant picks whose controller curls are read,
        // rightBones whose poses are used (the arm the bones are on): both are the hand's side.
        private bool ApplyNaturalPose(FingerHand hand, float ready)
        {
            bool dominant = hand.dominant;
            bool rightBones = hand.dominant;
            Transform[][] fingerJoints = hand.joints;
            bool[] isThumb = hand.isThumb;
            float weight = hand.weight;
            if (fingerJoints == null)
                return false;
            NaturalPose pose = NaturalPoseOf(rightBones);
            float[] curls = SkeletalActive(dominant) ? HandSkeleton(dominant)?.fingerCurls : null;
            if (curls != null && curls.Length < 5)
                curls = null;
            for (int fingerIndex = 0; fingerIndex < fingerJoints.Length; fingerIndex++)
            {
                Transform[] joints = fingerJoints[fingerIndex];
                bool thumb = isThumb != null && fingerIndex < isThumb.Length && isThumb[fingerIndex];
                float rest = Mathf.Clamp(thumb ? thumbCurlRestLevel : curlRestLevel, 0.05f, 0.95f);
                float curl = curls != null ? curls[CurlIndex(isThumb, fingerIndex)] : rest + Mathf.Clamp01(ready) * naturalReadyCurl * (1f - rest);
                for (int j = 0; j < joints.Length && j < 3; j++)
                {
                    Transform joint = joints[j];
                    int slot = joint != null ? NaturalSlot(joint) : -1;
                    if (slot < 0)
                        continue;
                    Quaternion resting = Quaternion.Slerp(pose.relaxed[slot], pose.fist[slot], Mathf.Clamp01(thumb ? naturalThumbRestClose : naturalRestClose));
                    Quaternion target = curl <= rest
                        ? Quaternion.Slerp(pose.open[slot], resting, curl / rest)
                        : Quaternion.Slerp(resting, pose.fist[slot], (curl - rest) / (1f - rest));
                    // Fan the knuckles apart (in steps: index +1, middle 0, ring -1, pinky -2; negative = toward the pinky), fading out toward the fist
                    int digit = slot / 3;
                    if (slot % 3 == 0 && digit >= 1)
                    {
                        float steps = digit == 1 ? 1f : 2f - digit;
                        float open = curl <= rest ? 1f : 1f - (curl - rest) / (1f - rest);
                        target = Quaternion.AngleAxis(naturalRestSpread * steps * open * (rightBones ? -1f : 1f), Vector3.up) * target;
                    }
                    BlendJoint(joint, target, weight);
                }
            }
            return true;
        }
    }
}
