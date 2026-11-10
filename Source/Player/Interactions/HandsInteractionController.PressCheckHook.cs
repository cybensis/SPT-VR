using System.Collections.Generic;
using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // The press check's fingers (VRReloadController.PressCheck turns the gun in the hand about the thumb's knuckle): as it turns they
    // open (rightRestPoseEuler), then come down onto the top of the slide (that pose bent about curlAxis * rightCurlSign, like the
    // finger tracking and the loot grip). The thumb stays where it grips the frame: from the knuckle out it turns with the gun.
    // Always computed on the right hand: with the gun in the left hand that's the hand EFT holds it with, and the left shows its mirror
    // image (nothing written on the free right hand; HandMirror.ApplyMirroredRight, from ApplyPressCheckHookLeft after the grip's mirror).
    internal partial class HandsInteractionController
    {
        // deg on the slide: knuckle / middle / tip joint. The turn leaves the knuckles beside the slide at about its middle height
        // (1911), so the knuckle bends most to reach over the top, then the rest wraps down onto it.
        // Tuned together with VRReloadController's wrist open 30 and offset:
        public static Vector3 pressCheckHookFinger = new Vector3(30f, 40f, 45f);   // index to ring
        public static Vector3 pressCheckHookPinky = new Vector3(-15f, -5f, 5f);
        // deg on top of the gripping thumb, its base / knuckle / tip joint: + closes it toward the palm, - opens it.
        // Bending the base moves the knuckle the gun turns about, the rest bends from it.
        public static Vector3 pressCheckHookThumb = Vector3.zero;

        private static readonly Quaternion[] pressCheckJointAnimated = new Quaternion[3];
        private static readonly Quaternion[] pressCheckJointShown = new Quaternion[3];
        // The gun in the left hand: the right hand's press-check pose by bone, for the left's mirror image
        private readonly Dictionary<Transform, Quaternion> pressCheckMirrorLocals = new Dictionary<Transform, Quaternion>();
        private bool pressCheckMirrorReady;
        // The thumb's joint the gun turns about, as it grips at the start of this press check, in the marker's frame
        private int pressCheckThumbSerial = -1;
        private Quaternion pressCheckThumbInGun = Quaternion.identity;

        private void UpdatePressCheckHook()
        {
            pressCheckMirrorReady = false;
            float progress = VRReloadController.PressCheckProgress;
            if (progress <= 0f || rightRestPoseEuler == null || rightRestPoseEuler.Length == 0)
                return;
            if (!rightFingersResolved)
                ResolveRightFingers();
            if (rightFingerJoints == null)
                return;
            bool mirrored = VRReloadController.PressCheckOnLeftHand;
            if (mirrored)
                pressCheckMirrorLocals.Clear();
            // Opening over the first part of the turn, closing onto the slide over the rest (both ways round on the way back)
            float closeFrom = Mathf.Clamp(VRReloadController.pressCheckFingersCloseFrom, 0.05f, 0.95f);
            float open = Mathf.SmoothStep(0f, 1f, progress / closeFrom);
            float close = Mathf.SmoothStep(0f, 1f, (progress - closeFrom) / (1f - closeFrom));
            // The slide drawn back: each finger tilts back with it from its knuckle (below, from its own knuckle-to-tip direction)
            bool dragging = VRReloadController.TryGetPressCheckDrag(out Vector3 backInPalm, out float pull);
            int poseIndex = 0;
            for (int finger = 0; finger < rightFingerJoints.Length; finger++)
            {
                Transform[] joints = rightFingerJoints[finger];
                bool thumb = rightFingerIsThumb != null && finger < rightFingerIsThumb.Length && rightFingerIsThumb[finger];
                if (thumb)
                {
                    KeepThumbOnGun(joints, Mathf.SmoothStep(0f, 1f, progress), mirrored);
                    poseIndex += Mathf.Min(joints.Length, 3);
                    continue;
                }
                bool pinky = joints.Length > 0 && joints[0] != null && joints[0].name.IndexOf("Digit5", System.StringComparison.OrdinalIgnoreCase) >= 0;
                Vector3 bends = pinky ? pressCheckHookPinky : pressCheckHookFinger;
                int jointCount = Mathf.Min(joints.Length, 3);
                for (int joint = 0; joint < jointCount; joint++)
                {
                    pressCheckJointAnimated[joint] = Quaternion.identity;
                    pressCheckJointShown[joint] = Quaternion.identity;
                    if (joints[joint] == null || poseIndex + joint >= rightRestPoseEuler.Length)
                        continue;
                    Quaternion animated = RecoverAnimated(joints[joint]);
                    Quaternion openPose = Quaternion.Euler(rightRestPoseEuler[poseIndex + joint]);
                    Quaternion onSlide = openPose * Quaternion.AngleAxis(bends[joint] * rightCurlSign, curlAxis);
                    pressCheckJointAnimated[joint] = animated;
                    pressCheckJointShown[joint] = Quaternion.Slerp(animated, Quaternion.Slerp(openPose, onSlide, close), open);
                }
                poseIndex += jointCount;
                // The finger as posed, knuckle to tip in the palm's frame (to the last joint, along the local chain - the bones' world poses
                // aren't this frame's yet), turned toward the gun's back by as much as keeps the tip with the slide
                if (dragging && jointCount == 3 && joints[0] != null && joints[1] != null && joints[2] != null)
                {
                    Vector3 toTip = pressCheckJointShown[0] * (joints[1].localPosition + pressCheckJointShown[1] * joints[2].localPosition);
                    Vector3 axis = Vector3.Cross(toTip, backInPalm);
                    if (axis.sqrMagnitude > 1e-10f)
                    {
                        float reach = Mathf.Max(VRReloadController.pressCheckFingerReach, 0.01f);
                        pressCheckJointShown[0] = Quaternion.AngleAxis(Mathf.Atan2(pull, reach) * Mathf.Rad2Deg, axis) * pressCheckJointShown[0];
                    }
                }
                for (int joint = 0; joint < jointCount; joint++)
                {
                    if (joints[joint] != null)
                        SetHookJoint(joints[joint], pressCheckJointAnimated[joint], pressCheckJointShown[joint], mirrored);
                }
            }
            pressCheckMirrorReady = mirrored;
        }

        // The right hand's joint as the press check has it: on the bone (the gun in the right hand), or kept for the left's mirror image
        private void SetHookJoint(Transform joint, Quaternion animated, Quaternion shown, bool mirrored)
        {
            if (mirrored)
            {
                pressCheckMirrorLocals[joint] = shown;
                return;
            }
            joint.localRotation = shown;
            fingerBaseRot[joint] = animated;
            fingerOutRot[joint] = shown;
        }

        // After HandMirror.ApplyRealLeftHanded (the grip's mirror on the left): the gun in the left hand, the press check's mirror image
        private void ApplyPressCheckHookLeft()
        {
            if (pressCheckMirrorReady && VRReloadController.PressCheckOnLeftHand)
                TarkovVR.Source.Player.Body.HandMirror.ApplyMirroredRight(pressCheckMirrorLocals);
        }

        // The thumb joint the gun turns about gets the gun's turn against the hand, so the thumb stays where it grips. Worked in the
        // gun's frame (the palm's turn there is known), not from this frame's bone world poses. Reference taken when a press check
        // starts: the palm there and the thumb's grip as EFT animates it. The thumb knobs on top, eased in with the turn.
        private readonly Quaternion[] pressCheckThumbShown = new Quaternion[3];

        private void KeepThumbOnGun(Transform[] joints, float amount, bool mirrored)
        {
            if (!VRReloadController.TryGetPressCheckThumb(out int serial, out int pivotJoint, out Quaternion palmNow, out Quaternion palmAtStart, out Quaternion thumbTurn)
                || pivotJoint >= joints.Length || joints[pivotJoint] == null)
                return;
            if (serial != pressCheckThumbSerial)
            {
                pressCheckThumbSerial = serial;
                pressCheckThumbInGun = palmAtStart;
                for (int joint = 0; joint <= pivotJoint; joint++)
                {
                    if (joints[joint] != null)
                        pressCheckThumbInGun = pressCheckThumbInGun * RecoverAnimated(joints[joint]);
                }
            }
            int jointCount = Mathf.Min(joints.Length, 3);
            for (int joint = 0; joint < jointCount; joint++)
                pressCheckThumbShown[joint] = joints[joint] != null ? RecoverAnimated(joints[joint]) : Quaternion.identity;
            Quaternion parentInGun = palmNow;
            for (int joint = 0; joint < pivotJoint; joint++)
            {
                pressCheckThumbShown[joint] = pressCheckThumbShown[joint] * Quaternion.AngleAxis(pressCheckHookThumb[joint] * amount * rightCurlSign, curlAxis);
                parentInGun = parentInGun * pressCheckThumbShown[joint];
            }
            pressCheckThumbShown[pivotJoint] = Quaternion.Inverse(parentInGun) * thumbTurn * pressCheckThumbInGun
                * Quaternion.AngleAxis(pressCheckHookThumb[pivotJoint] * amount * rightCurlSign, curlAxis);
            for (int joint = pivotJoint + 1; joint < jointCount; joint++)
                pressCheckThumbShown[joint] = pressCheckThumbShown[joint] * Quaternion.AngleAxis(pressCheckHookThumb[joint] * amount * rightCurlSign, curlAxis);
            for (int joint = 0; joint < jointCount; joint++)
            {
                if (joints[joint] == null)
                    continue;
                // Untouched joints (no bend, not the one it turns about) left to EFT on the hand itself
                if (!mirrored && joint != pivotJoint && pressCheckHookThumb[joint] == 0f)
                    continue;
                SetHookJoint(joints[joint], RecoverAnimated(joints[joint]), pressCheckThumbShown[joint], mirrored);
            }
        }
    }
}
