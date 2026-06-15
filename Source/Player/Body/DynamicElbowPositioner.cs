using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    public class DynamicElbowPositioner : MonoBehaviour
    {
        public Transform leftWristTransform;
        public Transform rightWristTransform;
        public Transform leftBendGoal;
        public Transform rightBendGoal;

        private Vector3 leftBendGoalBasePos = VRSettings.GetLeftHandedMode() ? new Vector3(1, -2f, -0.8f) : new Vector3(-1, -2f, -0.8f);
        private Vector3 rightBendGoalBasePos = VRSettings.GetLeftHandedMode() ? new Vector3(-2.5f, -2f, -0.8f) : new Vector3(2.5f, -2f, -0.8f);

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

        void Update()
        {
            bool freezeForSprint = sprintNeutralElbows && VRGlobals.player != null &&
                                   VRGlobals.player.IsSprintEnabled && VRSettings.GetDisableRunAnim();

            if (leftWristTransform != null && leftBendGoal != null)
            {
                leftTargetPos = freezeForSprint ? leftBendGoalBasePos
                                                : CalculateElbowPosition(leftWristTransform, leftBendGoalBasePos, true);
                leftBendGoal.localPosition = Vector3.Lerp(leftBendGoal.localPosition, leftTargetPos, Time.deltaTime * smoothSpeed);
            }
            if (rightWristTransform != null && rightBendGoal != null)
            {
                rightTargetPos = freezeForSprint ? rightBendGoalBasePos
                                                 : CalculateElbowPosition(rightWristTransform, rightBendGoalBasePos, false);
                rightBendGoal.localPosition = Vector3.Lerp(rightBendGoal.localPosition, rightTargetPos, Time.deltaTime * smoothSpeed);
            }
        }

        Vector3 CalculateElbowPosition(Transform wrist, Vector3 basePos, bool isLeftHand)
        {
            float wristTwist = wrist.localEulerAngles.x;
            if (wristTwist > 180f)
                wristTwist -= 360f;

            float elbowOffset = wristTwist * elbowOffsetMultiplier;
            bool isLeftHandedMode = VRSettings.GetLeftHandedMode();

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
                if (isLeftHandedMode)
                    newPos.x = Mathf.Max(newPos.x, basePos.x);
                else
                    newPos.x = Mathf.Min(newPos.x, basePos.x);
            }
            else
            {
                newPos.x += elbowOffset;
                // Prevent right elbow from moving too far left (inward)
                if (isLeftHandedMode)
                    newPos.x = Mathf.Min(newPos.x, basePos.x);
                else
                    newPos.x = Mathf.Max(newPos.x, basePos.x);
            }

            return newPos;
        }
    }
}
