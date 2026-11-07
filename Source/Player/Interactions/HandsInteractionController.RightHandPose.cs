using System.Collections.Generic;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // Right-hand "open hand" pose while the dominant hand is OFF the gun (ChestHang sling /
    // support-only hold): the arms animator still thinks the gun is held, so without this the freed
    // right hand keeps its grip curl. Mirrors the left hand's baked rest-pose replay (LootPointer
    // partial): same accumulation-safe RecoverAnimated, same weight easing (handOpenLerp /
    // handPoseWeight). Until a right-hand bake exists the LEFT bake (restPoseEuler) is reused -
    // EFT's finger bones are mirror-rigged so the locals usually transfer; if it reads wrong in the
    // headset, set captureRightRestPose=true (UnityExplorer) while the right hand shows a natural
    // open pose (EMPTY hands idle) to bake a proper rightRestPoseEuler, then logRightRestPose to
    // print the paste-ready array. The resolve log prints the finger roots so an index-order
    // mismatch with the left bake is visible immediately.
    internal partial class HandsInteractionController
    {
        public static bool rightOpenHand = true;            // master switch for the detached right-hand pose
        // Per-joint bake for the RIGHT hand, headset-authored 2026-07-20 (captured off the procedural
        // flatten, thumb hand-tuned via the live editor). null = fall back to the PROCEDURAL flatten
        // (ApplyFlatFlex on the right hand's own animated pose - rig-native). NOTE: transplanting the
        // LEFT bake's absolute eulers was a headset-tested DEAD END in every mirror convention (raw =
        // claw stays; negated flex = fingers bend BACKWARD) - the right bones' numeric frames don't
        // map; re-author via capture if this ever needs redoing.
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
        public static float rightFlexSign = -1f;            // the right rig's flex axis is INVERTED vs the left (headset-confirmed: +1 read as slight hyperextension)

        // Live per-joint editor on an existing right bake (capture first), like the left's poseEditJoint.
        public static int rightPoseEditJoint = -1;
        public static Vector3 rightPoseEditEuler = Vector3.zero;
        private int lastRightPoseEditJoint = -2;

        private bool rightFingersResolved;
        private Transform[][] rightFingerJoints;            // [finger][joint] under the right palm bone
        private bool[] rightFingerIsThumb;
        private float rightHandWeightBlend;

        // LateUpdate, after UpdateLeftHandFingers (and after EFT animates the hand).
        private void UpdateRightHandFingers()
        {
            // Pose the dominant hand when it's OFF the gun: slung/support-held (ChestHang.Detached) OR
            // genuinely empty-handed (no weapon) - the latter matches the left hand, which poses on
            // empty hands too (this was the "left poses empty-handed, right stays in the default
            // claw"). A HELD gun keeps its grip pose.
            bool emptyHands = VRGlobals.player != null && VRGlobals.player.HandsIsEmpty;
            bool free = rightOpenHand && VRGlobals.inGame && !VRGlobals.menuOpen
                        && !VRGlobals.usingItem && !EatingInteractionController.ManualActive
                        // (not while it holds the gun alone by the foregrip, the gun in the left hand: HandMirror's foregrip pose then)
                        && ((ChestHang.Detached && !TarkovVR.Patches.Core.Player.RealLeftHandedPatches.SupportHeld) || emptyHands
                            // the gun in the LEFT hand and this one off the foregrip (HandMirror puts the mirrored foregrip pose on it there)
                            || (TarkovVR.Patches.Core.Player.RealLeftHandedPatches.Active && !VRGlobals.vrPlayer.isSupporting));

            float k = 1f - Mathf.Exp(-handOpenLerp * Time.deltaTime);
            rightHandWeightBlend = Mathf.Lerp(rightHandWeightBlend, free ? Mathf.Clamp01(handPoseWeight) : 0f, k);

            if (!rightFingersResolved)
                ResolveRightFingers();
            if (rightFingerJoints == null)
                return;

            if (logRightRestPose)
            {
                logRightRestPose = false;
                LogRightRestPose();
            }
            EditRightPoseLive();

            if (rightHandWeightBlend >= 0.01f)
            {
                // Detached (sling / support) or empty: Index controllers curl the real fingers from
                // the baked pose; everything else replays the baked/procedural open pose. While
                // HOLDING a loot item in this hand, skeletal is suppressed (the grip squeeze would
                // clench the fist through the item) - the baked rest pose shows instead.
                bool holdingInThisHand = heldItem != null && heldInDominantHand;
                if (!holdingInThisHand
                    && SkeletalActive(true) && ApplySkeletalCurls(true, rightFingerJoints, rightFingerIsThumb, rightRestPoseEuler, rightCurlSign, rightHandWeightBlend, leftDeltas: false))
                {
                    // driven by skeletal curls
                }
                else if (rightRestPoseEuler != null && rightRestPoseEuler.Length > 0)
                {
                    // A captured right bake replays exactly (same mechanism as the left hand).
                    int idx = 0;
                    for (int f = 0; f < rightFingerJoints.Length; f++)
                    {
                        Transform[] joints = rightFingerJoints[f];
                        for (int j = 0; j < joints.Length && j < 3; j++, idx++)
                        {
                            if (joints[j] == null || idx >= rightRestPoseEuler.Length)
                                continue;
                            Quaternion animated = RecoverAnimated(joints[j]);
                            Quaternion outRot = Quaternion.Slerp(animated, Quaternion.Euler(rightRestPoseEuler[idx]), rightHandWeightBlend);
                            joints[j].localRotation = outRot;
                            fingerBaseRot[joints[j]] = animated;
                            fingerOutRot[joints[j]] = outRot;
                        }
                    }
                }
                else
                {
                    // No bake yet: PROCEDURAL flatten - strip the animated grip curl in the right
                    // rig's OWN frame (swing-twist, ApplyFlatFlex) and apply the idle flex.
                    // Rig-native, so it can't hyperextend the way transplanted left values did.
                    for (int f = 0; f < rightFingerJoints.Length; f++)
                    {
                        Transform[] joints = rightFingerJoints[f];
                        float flexBase = RightFlex(f);
                        for (int j = 0; j < joints.Length && j < 3; j++)
                        {
                            if (joints[j] == null)
                                continue;
                            float flex = flexBase * Mathf.Pow(midJointScale, j) * rightFlexSign;
                            ApplyFlatFlex(joints[j], flex, 0f, rightHandWeightBlend);
                        }
                    }
                }
            }

            // Capture LAST, after our pose was written to the bones: the animator re-stomps the
            // fingers every frame BEFORE this LateUpdate, so a capture at the top of the method read
            // the fresh GRIP CURL, not our rendered result (headset-confirmed: "capture reset the
            // hand to the grip pose"). Here the bones hold exactly what's on screen this frame.
            if (captureRightRestPose)
            {
                captureRightRestPose = false;
                if (rightHandWeightBlend < 0.5f)
                    Plugin.MyLog.LogWarning($"[RightHandPose] capture skipped - the open pose isn't active (blend={rightHandWeightBlend:0.00}); sling the gun first so the hand shows the pose you want to bake.");
                else
                    CaptureRightRestPose();
            }
        }

        // The left's idle flex values, remapped by THUMB DETECTION rather than child order (the left
        // arrays assume index 0 = thumb; the right palm's child order may differ).
        private float RightFlex(int f)
        {
            bool isThumb = rightFingerIsThumb != null && f < rightFingerIsThumb.Length && rightFingerIsThumb[f];
            if (isThumb)
                return fingerFlexDeg != null && fingerFlexDeg.Length > 0 ? fingerFlexDeg[0] : 0f;
            // ordinal among the non-thumb fingers -> flex array entries 1..4
            int k = 1;
            for (int i = 0; i < f && i < rightFingerIsThumb.Length; i++)
                if (!rightFingerIsThumb[i])
                    k++;
            return fingerFlexDeg != null && k < fingerFlexDeg.Length ? fingerFlexDeg[k] : 0f;
        }

        // Live per-joint editing on an existing right bake (capture one first), exactly like the
        // left's EditRestPoseLive: changing the joint index LOADS its current euler; further edits
        // write back each frame.
        private void EditRightPoseLive()
        {
            if (rightPoseEditJoint < 0)
            {
                lastRightPoseEditJoint = -2;
                return;
            }
            if (rightRestPoseEuler == null || rightPoseEditJoint >= rightRestPoseEuler.Length)
            {
                if (lastRightPoseEditJoint != rightPoseEditJoint)
                {
                    lastRightPoseEditJoint = rightPoseEditJoint;
                    Plugin.MyLog.LogWarning("[RightHandPose] no right bake to edit - set captureRightRestPose=true first (while the procedural pose looks good).");
                }
                return;
            }
            if (rightPoseEditJoint != lastRightPoseEditJoint)
            {
                lastRightPoseEditJoint = rightPoseEditJoint;
                rightPoseEditEuler = rightRestPoseEuler[rightPoseEditJoint];
            }
            else
            {
                rightRestPoseEuler[rightPoseEditJoint] = rightPoseEditEuler;
            }
        }

        // The right palm = the right arm IK's wrist bone (solver.bone3 - the same bone the eating
        // system pins; its children are the finger roots, like "Base HumanLPalm" on the left).
        private void ResolveRightFingers()
        {
            rightFingersResolved = true;
            rightFingerJoints = null;
            Transform palm = VRGlobals.ikManager != null && VRGlobals.ikManager.rightArmIk != null &&
                             VRGlobals.ikManager.rightArmIk.solver != null && VRGlobals.ikManager.rightArmIk.solver.bone3 != null
                ? VRGlobals.ikManager.rightArmIk.solver.bone3.transform : null;
            if (palm == null)
            {
                Plugin.MyLog.LogWarning("[RightHandPose] right palm not resolved yet - open-hand idle.");
                return;
            }

            var fingers = new List<Transform[]>();
            var thumbs = new List<bool>();
            var names = new List<string>();
            for (int i = 0; i < palm.childCount; i++)
            {
                Transform root = palm.GetChild(i);
                var joints = new List<Transform>();
                Transform j = root;
                while (j != null && joints.Count < 3)
                {
                    joints.Add(j);
                    j = j.childCount > 0 ? j.GetChild(0) : null;
                }
                if (joints.Count >= 1)
                {
                    fingers.Add(joints.ToArray());
                    // EFT thumbs are "...Digit1..." / contain "thumb"; Digit1 = the thumb chain
                    string n = root.name;
                    thumbs.Add(n.IndexOf("thumb", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || n.IndexOf("Digit1", System.StringComparison.OrdinalIgnoreCase) >= 0);
                    names.Add(n);
                }
            }
            if (fingers.Count > 0)
            {
                rightFingerJoints = fingers.ToArray();
                rightFingerIsThumb = thumbs.ToArray();
            }
        }

        private void CaptureRightRestPose()
        {
            var list = new List<Vector3>();
            var sb = new System.Text.StringBuilder();
            sb.Append("[RightHandPose] rightRestPoseEuler = new Vector3[] {\n");
            for (int f = 0; f < rightFingerJoints.Length; f++)
            {
                Transform[] joints = rightFingerJoints[f];
                for (int j = 0; j < joints.Length && j < 3; j++)
                {
                    Vector3 e = joints[j] != null ? joints[j].localEulerAngles : Vector3.zero;
                    list.Add(e);
                    sb.Append($"    new Vector3({e.x:F2}f, {e.y:F2}f, {e.z:F2}f), // finger{f} joint{j}\n");
                }
            }
            sb.Append("};");
            rightRestPoseEuler = list.ToArray();
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        private void LogRightRestPose()
        {
            if (rightRestPoseEuler == null)
            {
                Plugin.MyLog.LogInfo("[RightHandPose] rightRestPoseEuler is null.");
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("[RightHandPose] rightRestPoseEuler = new Vector3[] {\n");
            for (int i = 0; i < rightRestPoseEuler.Length; i++)
            {
                Vector3 e = rightRestPoseEuler[i];
                sb.Append($"    new Vector3({e.x:F2}f, {e.y:F2}f, {e.z:F2}f), // index {i}\n");
            }
            sb.Append("};");
            Plugin.MyLog.LogInfo(sb.ToString());
        }
    }
}
