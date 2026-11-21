using TarkovVR.Source.Settings;
using UnityEngine;
using TarkovVR.Source.Controls;

namespace TarkovVR.Source.Player.Body
{
    public class DynamicElbowPositioner : MonoBehaviour
    {
        public Transform leftWristTransform;
        public Transform rightWristTransform;
        public Transform leftBendGoal;
        public Transform rightBendGoal;

        // The mirrored left-handed mode swaps the sides (its arms are scaled -1); a real one, like right-handed, does not
        private Vector3 leftBendGoalBasePos => Handedness.MirrorLeftHanded ? new Vector3(1, -2f, -0.8f) : new Vector3(-1, -2f, -0.8f);
        private Vector3 rightBendGoalBasePos => Handedness.MirrorLeftHanded ? new Vector3(-2.5f, -2f, -0.8f) : new Vector3(2.5f, -2f, -0.8f);

        public float elbowOffsetMultiplier = 0.12f;
        public float smoothSpeed = 8f;

        private Vector3 leftTargetPos;
        private Vector3 rightTargetPos;

        void Start()
        {
            leftTargetPos = leftBendGoalBasePos;
            rightTargetPos = rightBendGoalBasePos;
        }

        // While sprinting with the run anim disabled, the gait still pumps the forearm twist this reads, so the elbow
        // goal swings wildly. Hold it at its neutral base position during that sprint (the lerp eases in/out).
        public static bool sprintNeutralElbows = true;

        // The Custom elbow driver (IKPatches.DriveElbow) ignores the bend goals this component moves, so the wrist-roll
        // deflection is also published here (X delta from base, goal units; clamped + sprint-frozen like the goals).
        // Must be read in Update: it sees last frame's final wrist pose (post-IK/TwistRelax), whereas the driver runs
        // mid-VisualPass where the animator has already stomped the wrist roll.
        public static float leftTwistDeltaX;
        public static float rightTwistDeltaX;

        void Update()
        {
            bool freezeForSprint = sprintNeutralElbows && VRGlobals.player != null &&
                                   VRGlobals.player.IsSprintEnabled && VRSettings.GetDisableRunAnim();

            // Twist input: the wrist driver's measured physical hand roll (IKPatches.DriveWrist; the bone euler only carries the
            // (1 - w2) residual of the roll)
            if (leftWristTransform != null && leftBendGoal != null)
            {
                float twist = TarkovVR.Patches.Core.Player.IKPatches.wristTauL;
                leftTargetPos = freezeForSprint ? leftBendGoalBasePos
                                                : CalculateElbowPosition(twist, leftBendGoalBasePos, true);
                leftBendGoal.localPosition = Vector3.Lerp(leftBendGoal.localPosition, leftTargetPos, Time.deltaTime * smoothSpeed);
                leftTwistDeltaX = leftTargetPos.x - leftBendGoalBasePos.x;
            }
            if (rightWristTransform != null && rightBendGoal != null)
            {
                float twist = TarkovVR.Patches.Core.Player.IKPatches.wristTauR;
                rightTargetPos = freezeForSprint ? rightBendGoalBasePos
                                                 : CalculateElbowPosition(twist, rightBendGoalBasePos, false);
                rightBendGoal.localPosition = Vector3.Lerp(rightBendGoal.localPosition, rightTargetPos, Time.deltaTime * smoothSpeed);
                rightTwistDeltaX = rightTargetPos.x - rightBendGoalBasePos.x;
            }
        }

        // The twist is signed around its own neutral: no outward-only clamp (an untrimmed arm would lose half its roll range)
        Vector3 CalculateElbowPosition(float wristTwist, Vector3 basePos, bool isLeftHand)
        {
            float elbowOffset = wristTwist * elbowOffsetMultiplier;
            bool isLeftHandedMode = Handedness.MirrorLeftHanded;

            // Apply the base inversion for right hand
            if (!isLeftHand)
                elbowOffset = -elbowOffset;

            // In left-handed mode, the scale is flipped, so we need to flip the offset direction
            if (isLeftHandedMode)
                elbowOffset = -elbowOffset;

            Vector3 newPos = basePos;

            if (isLeftHand)
                newPos.x -= elbowOffset;
            else
                newPos.x += elbowOffset;

            return newPos;
        }
    }
}
