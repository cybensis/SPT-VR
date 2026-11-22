using System.Collections.Generic;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // Open-hand pose for the right hand while it is off the gun (ChestHang sling or support-only hold) or empty. The arms animator
    // still thinks the gun is held, so without this the freed right hand keeps its grip curl. Works like the left hand's baked
    // rest-pose replay: the same RecoverAnimated (no accumulation) and the same weight easing (handOpenLerp / handPoseWeight).
    // To re-bake, set captureRightRestPose = true (UnityExplorer) while the hand shows the pose you want; logRightRestPose prints
    // the array to paste.
    internal partial class HandsInteractionController
    {
        // Baked local eulers per right-hand joint, captured from the procedural flatten. null = use the procedural flatten
        // (ApplyFlatFlex on the right hand's own animated pose). The left bake's eulers don't transfer to the right
        // bones in any mirror convention: re-bake with a capture instead.
        public static Vector3[] rightRestPoseEuler = new Vector3[]
        {
            new Vector3(300.00f, 300.00f, 40.00f), // finger0 (thumb) joint0
            new Vector3(326.17f, 358.53f, 0.45f),  // finger0 (thumb) joint1
            new Vector3(4.01f, 356.19f, 10.07f),   // finger0 (thumb) joint2
            new Vector3(359.82f, 9.60f, 14.99f),   // finger1 joint0
            new Vector3(359.82f, 9.60f, 24.99f),   // finger1 joint1
            new Vector3(0.00f, 0.01f, 5.40f),      // finger1 joint2
            new Vector3(357.85f, 10.79f, 19.70f),  // finger2 joint0
            new Vector3(347.85f, 0.00f, 29.70f),   // finger2 joint1
            new Vector3(0.00f, 0.01f, 7.20f),      // finger2 joint2
            new Vector3(359.75f, 19.91f, 26.96f),  // finger3 joint0
            new Vector3(0.00f, 0.00f, 16.20f),     // finger3 joint1
            new Vector3(0.00f, 0.00f, 9.72f),      // finger3 joint2
            new Vector3(4.63f, 25.65f, 28.05f),    // finger4 joint0
            new Vector3(0.00f, 0.00f, 16.20f),     // finger4 joint1
            new Vector3(0.00f, 0.00f, 9.72f),      // finger4 joint2
        };
        public static bool captureRightRestPose = false;
        public static bool logRightRestPose = false;
        public static float rightFlexSign = -1f;            // the right rig's flex axis is inverted vs the left

        // Live per-joint editor on an existing right bake (capture first), like the left's poseEditJoint.
        public static int rightPoseEditJoint = -1;
        public static Vector3 rightPoseEditEuler = Vector3.zero;
        private int lastRightPoseEditJoint = -2;


        // Runs in LateUpdate, after UpdateLeftHandFingers and after EFT animates the hand.
        private void UpdateRightHandFingers()
        {
            // Pose the dominant hand when it is off the gun (slung or support-held: ChestHang.Detached) or empty, like the left.
            // A held gun keeps its grip pose.
            bool emptyHands = VRGlobals.player != null && VRGlobals.player.HandsIsEmpty;
            bool free = VRGlobals.inGame && !VRGlobals.menuOpen
                        && !VRGlobals.usingItem && !EatingInteractionController.ManualActive
                        // Not while this hand holds the gun alone by the foregrip with the gun in the left hand: HandMirror poses it then
                        && ((ChestHang.Detached && !TarkovVR.Patches.Core.Player.RealLeftHandedPatches.SupportHeld) || emptyHands
                            // The gun is in the left hand and this hand is off its foregrip (on it, HandMirror applies the mirrored pose)
                            || (TarkovVR.Patches.Core.Player.RealLeftHandedPatches.Active && !VRGlobals.vrPlayer.isSupporting)
                            // Off a mounted gun's handle while still on the mounted gun
                            || (MountedGun.Mounted && !MountedGun.HandOnHandle(left: false)));

            float k = 1f - Mathf.Exp(-handOpenLerp * Time.deltaTime);
            rightFingers.weight = Mathf.Lerp(rightFingers.weight, free ? Mathf.Clamp01(handPoseWeight) : 0f, k);

            if (!rightFingers.resolved)
                ResolveRightFingers();
            if (rightFingers.joints == null)
                return;

            if (logRightRestPose)
            {
                logRightRestPose = false;
                LogFingerPose("RightHandPose", "rightRestPoseEuler", rightRestPoseEuler);
            }
            EditFingerPoseLive(rightRestPoseEuler, ref rightPoseEditJoint, ref rightPoseEditEuler, ref lastRightPoseEditJoint, "RightHandPose", "captureRightRestPose");

            if (rightFingers.weight >= 0.01f)
            {
                // Index controllers curl the fingers from the baked pose; other controllers replay the baked or procedural open pose.
                // While this hand holds loot or a door, the natural and skeletal poses are skipped: the grip squeeze would clench the
                // fist through the item.
                bool holdingInThisHand = HoldsLoot(true) || PhysicalDoors.HoldsDoor(true);
                // Empty hand: Tarkov's own relaxed / open / fist poses, blended by the finger curls (HandsInteractionController.NaturalHands)
                if (!holdingInThisHand && naturalHands && ApplyNaturalPose(rightFingers, ready: 0f))
                {
                }
                else if (!holdingInThisHand
                    && SkeletalActive(true) && ApplySkeletalCurls(rightFingers, rightRestPoseEuler, leftDeltas: false))
                {
                    // driven by skeletal curls
                }
                else if (rightRestPoseEuler != null && rightRestPoseEuler.Length > 0)
                {
                    // A captured right bake replays exactly (same mechanism as the left hand).
                    int poseIndex = 0;
                    for (int fingerIndex = 0; fingerIndex < rightFingers.joints.Length; fingerIndex++)
                    {
                        Transform[] joints = rightFingers.joints[fingerIndex];
                        for (int j = 0; j < joints.Length && j < 3; j++, poseIndex++)
                        {
                            if (joints[j] == null || poseIndex >= rightRestPoseEuler.Length)
                                continue;
                            BlendJoint(joints[j], Quaternion.Euler(rightRestPoseEuler[poseIndex]), rightFingers.weight);
                        }
                    }
                }
                else
                {
                    // No bake: the procedural flatten strips the animated grip curl in the right rig's own frame
                    // (swing-twist, ApplyFlatFlex) and applies the idle flex.
                    for (int fingerIndex = 0; fingerIndex < rightFingers.joints.Length; fingerIndex++)
                    {
                        Transform[] joints = rightFingers.joints[fingerIndex];
                        float flexBase = RightFlex(fingerIndex);
                        for (int j = 0; j < joints.Length && j < 3; j++)
                        {
                            if (joints[j] == null)
                                continue;
                            float flex = flexBase * Mathf.Pow(midJointScale, j) * rightFlexSign;
                            ApplyFlatFlex(joints[j], flex, 0f, rightFingers.weight);
                        }
                    }
                }
                // Close the fingers on a held loot item (HandsInteractionController.Grasp), starting from the pose just written
                ApplyGrasp(rightFingers);
            }

            // Capture last, after the pose is written: the animator overwrites the fingers every frame before this
            // LateUpdate, so earlier in the method the bones hold the grip curl, not what is on screen.
            if (captureRightRestPose)
            {
                captureRightRestPose = false;
                if (rightFingers.weight < 0.5f)
                    Plugin.MyLog.LogWarning($"[RightHandPose] capture skipped - the open pose isn't active (blend={rightFingers.weight:0.00}); sling the gun first so the hand shows the pose you want to bake.");
                else
                    rightRestPoseEuler = CaptureFingerPose(rightFingers, animatedPose: false, "RightHandPose", "rightRestPoseEuler");
            }
        }

        // The left's idle flex values, remapped by thumb detection rather than child order (the left arrays assume
        // index 0 = thumb; the right palm's child order may differ).
        private float RightFlex(int fingerIndex)
        {
            bool isThumb = rightFingers.isThumb != null && fingerIndex < rightFingers.isThumb.Length && rightFingers.isThumb[fingerIndex];
            if (isThumb)
                return fingerFlexDeg != null && fingerFlexDeg.Length > 0 ? fingerFlexDeg[0] : 0f;
            // ordinal among the non-thumb fingers -> flex array entries 1..4
            int k = 1;
            for (int i = 0; i < fingerIndex && i < rightFingers.isThumb.Length; i++)
                if (!rightFingers.isThumb[i])
                    k++;
            return fingerFlexDeg != null && k < fingerFlexDeg.Length ? fingerFlexDeg[k] : 0f;
        }

        // The right palm is the right arm IK's wrist bone (solver.bone3, the bone eating pins). Its children are the
        // finger roots, like "Base HumanLPalm" on the left.
        private void ResolveRightFingers()
        {
            Transform palm = VRGlobals.ikManager != null && VRGlobals.ikManager.rightArmIk != null &&
                             VRGlobals.ikManager.rightArmIk.solver != null && VRGlobals.ikManager.rightArmIk.solver.bone3 != null
                ? VRGlobals.ikManager.rightArmIk.solver.bone3.transform : null;
            if (!ResolveFingerChains(rightFingers, palm, new List<bool>()) && palm == null)
                Plugin.MyLog.LogWarning("[RightHandPose] right palm not resolved yet - open-hand idle.");
        }
    }
}
