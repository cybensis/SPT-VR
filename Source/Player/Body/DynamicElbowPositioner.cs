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

        // While sprinting with the run anim disabled, the forearm twist this reads is still being pumped by
        // the gait, so the wrist-driven elbow goal swings and the elbows go crazy. Hold the goal at its
        // neutral base position during that sprint instead (the existing lerp eases in/out smoothly).
        public static bool sprintNeutralElbows = true;

        // The Custom elbow driver (IKPatches.DriveElbow) ignores the bend GOALS this component moves, so
        // the wrist-roll deflection is also published here (X delta from base, old goal units; clamped +
        // sprint-frozen like the goals) for the driver to fold into its rest direction. This Update-phase
        // read is load-bearing: it sees last frame's FINAL wrist pose (post-IK/TwistRelax), whereas the
        // driver runs mid-VisualPass where the animator has already stomped the wrist roll.
        public static float leftTwistDeltaX;
        public static float rightTwistDeltaX;

        void Update()
        {
            bool freezeForSprint = sprintNeutralElbows && VRGlobals.player != null &&
                                   VRGlobals.player.IsSprintEnabled && VRSettings.GetDisableRunAnim();

            // Twist input: the wrist driver's measured PHYSICAL hand roll when it's active - the bone
            // euler this used to read now only carries the (1 - w2) residual of the roll, so its meaning
            // (and this component's sensitivity) changed with every w1/w2 tune. Falls back to the euler
            // read when the drivers are off.
            bool useTau = TarkovVR.Patches.Core.Player.IKPatches.naturalWrist &&
                          TarkovVR.Patches.Core.Player.IKPatches.naturalElbow;

            if (leftWristTransform != null && leftBendGoal != null)
            {
                float twist = useTau ? TarkovVR.Patches.Core.Player.IKPatches.wristTauL : ReadEulerTwist(leftWristTransform);
                leftTargetPos = freezeForSprint ? leftBendGoalBasePos
                                                : CalculateElbowPosition(twist, leftBendGoalBasePos, true, !useTau);
                leftBendGoal.localPosition = Vector3.Lerp(leftBendGoal.localPosition, leftTargetPos, Time.deltaTime * smoothSpeed);
                leftTwistDeltaX = leftTargetPos.x - leftBendGoalBasePos.x;
            }
            if (rightWristTransform != null && rightBendGoal != null)
            {
                float twist = useTau ? TarkovVR.Patches.Core.Player.IKPatches.wristTauR : ReadEulerTwist(rightWristTransform);
                rightTargetPos = freezeForSprint ? rightBendGoalBasePos
                                                 : CalculateElbowPosition(twist, rightBendGoalBasePos, false, !useTau);
                rightBendGoal.localPosition = Vector3.Lerp(rightBendGoal.localPosition, rightTargetPos, Time.deltaTime * smoothSpeed);
                rightTwistDeltaX = rightTargetPos.x - rightBendGoalBasePos.x;
            }
        }

        static float ReadEulerTwist(Transform wrist)
        {
            float twist = wrist.localEulerAngles.x;
            if (twist > 180f)
                twist -= 360f;
            return twist;
        }

        // oneSidedClamp: the outward-only floor from the euler days. Keep it for the euler path, but the
        // tau input is signed around its own neutral - with the clamp, an arm whose tau sits near 0 at
        // neutral (no trim) has half its roll range floored to base and reads as dead (the right arm),
        // while a trimmed arm (tau pushed far positive) never touches the clamp at all (the left arm).
        Vector3 CalculateElbowPosition(float wristTwist, Vector3 basePos, bool isLeftHand, bool oneSidedClamp)
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
            {
                newPos.x -= elbowOffset;
                // Prevent left elbow from moving too far right (inward)
                if (oneSidedClamp)
                {
                    if (isLeftHandedMode)
                        newPos.x = Mathf.Max(newPos.x, basePos.x);
                    else
                        newPos.x = Mathf.Min(newPos.x, basePos.x);
                }
            }
            else
            {
                newPos.x += elbowOffset;
                // Prevent right elbow from moving too far left (inward)
                if (oneSidedClamp)
                {
                    if (isLeftHandedMode)
                        newPos.x = Mathf.Min(newPos.x, basePos.x);
                    else
                        newPos.x = Mathf.Max(newPos.x, basePos.x);
                }
            }

            return newPos;
        }
    }
}
