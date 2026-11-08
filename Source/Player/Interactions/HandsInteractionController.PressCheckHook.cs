using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // The press check's fingers (VRReloadController.PressCheck turns the gun in the hand about the thumb's knuckle): as it turns they
    // let go of the grip and open (the open-hand pose, rightRestPoseEuler), then come down onto the top of the slide (that pose bent
    // about each joint's flexion axis, the way the finger tracking and the loot grip close this hand: curlAxis, rightCurlSign). The
    // thumb stays exactly where it grips the frame (his design): from the knuckle out it turns with the gun, against the hand.
    internal partial class HandsInteractionController
    {
        // deg on the slide: knuckle / middle / tip joint. The turn leaves the knuckles beside the slide at about its middle height
        // (1911, offline), so the knuckle bends most to reach over the top, then the rest wraps down onto it. The wrist's roll open
        // (VRReloadController.pressCheckWristOpen, 15) tips the fingers that much further over already: 45 headset-looked right
        // with no roll, so 30 here. The pinky a little open (headset).
        // His headset tuning 2026-11-08 (with VRReloadController's wrist open 30 and offset):
        public static Vector3 pressCheckHookFinger = new Vector3(30f, 40f, 45f);   // index to ring
        public static Vector3 pressCheckHookPinky = new Vector3(0f, 15f, 15f);
        // deg on top of the gripping thumb, its base / knuckle / tip joint: + closes it toward the palm, - opens it (his ask: "anything to
        // adjust the thumb open/close"). Bending the base moves the knuckle the gun turns about, the rest bends from it.
        public static Vector3 pressCheckHookThumb = Vector3.zero;

        private void UpdatePressCheckHook()
        {
            float progress = VRReloadController.PressCheckProgress;
            if (progress <= 0f || rightRestPoseEuler == null || rightRestPoseEuler.Length == 0)
                return;
            if (!rightFingersResolved)
                ResolveRightFingers();
            if (rightFingerJoints == null)
                return;
            // Opening over the first part of the turn, closing onto the slide over the rest (both ways round on the way back)
            float closeFrom = Mathf.Clamp(VRReloadController.pressCheckFingersCloseFrom, 0.05f, 0.95f);
            float open = Mathf.SmoothStep(0f, 1f, progress / closeFrom);
            float close = Mathf.SmoothStep(0f, 1f, (progress - closeFrom) / (1f - closeFrom));
            // The slide drawn back: the fingers tilt back with it from their knuckles (a turn in the palm's frame = the first joints' parent)
            Quaternion drag = VRReloadController.PressCheckFingerDrag();
            int poseIndex = 0;
            for (int finger = 0; finger < rightFingerJoints.Length; finger++)
            {
                Transform[] joints = rightFingerJoints[finger];
                bool thumb = rightFingerIsThumb != null && finger < rightFingerIsThumb.Length && rightFingerIsThumb[finger];
                if (thumb)
                {
                    KeepThumbOnGun(joints, Mathf.SmoothStep(0f, 1f, progress));
                    poseIndex += Mathf.Min(joints.Length, 3);
                    continue;
                }
                bool pinky = joints.Length > 0 && joints[0] != null && joints[0].name.IndexOf("Digit5", System.StringComparison.OrdinalIgnoreCase) >= 0;
                Vector3 bends = pinky ? pressCheckHookPinky : pressCheckHookFinger;
                for (int joint = 0; joint < joints.Length && joint < 3; joint++, poseIndex++)
                {
                    if (joints[joint] == null || poseIndex >= rightRestPoseEuler.Length)
                        continue;
                    Quaternion animated = RecoverAnimated(joints[joint]);
                    Quaternion openPose = Quaternion.Euler(rightRestPoseEuler[poseIndex]);
                    Quaternion onSlide = openPose * Quaternion.AngleAxis(bends[joint] * rightCurlSign, curlAxis);
                    Quaternion shown = Quaternion.Slerp(animated, Quaternion.Slerp(openPose, onSlide, close), open);
                    if (joint == 0)
                        shown = drag * shown;
                    joints[joint].localRotation = shown;
                    fingerBaseRot[joints[joint]] = animated;
                    fingerOutRot[joints[joint]] = shown;
                }
            }
        }

        // The thumb joint the gun turns about gets the gun's turn against the hand, so it and the rest of the thumb stay where they
        // grip: worked in the gun's frame (the palm's turn there is known), not from the bones' world poses this frame. The thumb bend
        // knob on top, eased in with the turn: the joints before that one first (they carry it), then it, then the ones after.
        private void KeepThumbOnGun(Transform[] joints, float amount)
        {
            if (!VRReloadController.TryGetPressCheckThumb(out int pivotJoint, out Quaternion palmInGun, out Quaternion thumbJointInGun)
                || pivotJoint >= joints.Length || joints[pivotJoint] == null)
                return;
            Quaternion parentInGun = palmInGun;
            for (int joint = 0; joint < pivotJoint; joint++)
            {
                if (joints[joint] == null)
                    continue;
                BendThumbJoint(joints[joint], pressCheckHookThumb[joint] * amount);
                parentInGun = parentInGun * joints[joint].localRotation;
            }
            Transform pivot = joints[pivotJoint];
            Quaternion animated = RecoverAnimated(pivot);
            Quaternion shown = Quaternion.Inverse(parentInGun) * thumbJointInGun
                * Quaternion.AngleAxis(pressCheckHookThumb[pivotJoint] * amount * rightCurlSign, curlAxis);
            pivot.localRotation = shown;
            fingerBaseRot[pivot] = animated;
            fingerOutRot[pivot] = shown;
            for (int joint = pivotJoint + 1; joint < joints.Length && joint < 3; joint++)
            {
                if (joints[joint] != null)
                    BendThumbJoint(joints[joint], pressCheckHookThumb[joint] * amount);
            }
        }

        // A thumb joint bent on top of how EFT animates it (nothing written for no bend)
        private void BendThumbJoint(Transform joint, float degrees)
        {
            if (degrees == 0f)
                return;
            Quaternion animated = RecoverAnimated(joint);
            Quaternion shown = animated * Quaternion.AngleAxis(degrees * rightCurlSign, curlAxis);
            joint.localRotation = shown;
            fingerBaseRot[joint] = animated;
            fingerOutRot[joint] = shown;
        }
    }
}
