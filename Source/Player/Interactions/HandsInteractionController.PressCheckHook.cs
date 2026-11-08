using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // The press check's hook (his pictures, VRReloadController.PressCheck puts the hand on the gun's right side): the fingers up the
    // side and over the top of the slide's back, the pinky (shortest) curled a bit more, the thumb as the open hand's. Bent from the open-hand
    // pose (rightRestPoseEuler, the hand you see off the gun) about each joint's flexion axis, the way the finger tracking and the
    // loot grip close this hand (curlAxis, rightCurlSign).
    internal partial class HandsInteractionController
    {
        public static Vector3 pressCheckHookFinger = new Vector3(10f, 90f, 70f);   // deg knuckle / middle / tip joint: index, middle, ring
        public static Vector3 pressCheckHookPinky = new Vector3(20f, 100f, 70f);   // deg the same for the pinky
        public static Vector3 pressCheckHookThumb = Vector3.zero;                  // deg the thumb's three joints (0 = the open hand's, up)

        private void UpdatePressCheckHook()
        {
            float weight = VRReloadController.PressCheckWeight;
            if (weight <= 0f || rightRestPoseEuler == null || rightRestPoseEuler.Length == 0)
                return;
            if (!rightFingersResolved)
                ResolveRightFingers();
            if (rightFingerJoints == null)
                return;
            int poseIndex = 0;
            for (int finger = 0; finger < rightFingerJoints.Length; finger++)
            {
                Transform[] joints = rightFingerJoints[finger];
                bool thumb = rightFingerIsThumb != null && finger < rightFingerIsThumb.Length && rightFingerIsThumb[finger];
                bool pinky = joints.Length > 0 && joints[0] != null && joints[0].name.IndexOf("Digit5", System.StringComparison.OrdinalIgnoreCase) >= 0;
                Vector3 bends = thumb ? pressCheckHookThumb : pinky ? pressCheckHookPinky : pressCheckHookFinger;
                for (int joint = 0; joint < joints.Length && joint < 3; joint++, poseIndex++)
                {
                    if (joints[joint] == null || poseIndex >= rightRestPoseEuler.Length)
                        continue;
                    Quaternion target = Quaternion.Euler(rightRestPoseEuler[poseIndex]) * Quaternion.AngleAxis(bends[joint] * rightCurlSign, curlAxis);
                    Quaternion animated = RecoverAnimated(joints[joint]);
                    Quaternion shown = Quaternion.Slerp(animated, target, weight);
                    joints[joint].localRotation = shown;
                    fingerBaseRot[joints[joint]] = animated;
                    fingerOutRot[joints[joint]] = shown;
                }
            }
        }
    }
}
