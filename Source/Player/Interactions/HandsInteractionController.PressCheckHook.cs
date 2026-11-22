using System.Collections.Generic;
using TarkovVR.Source.Weapons;
using UnityEngine;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Player.Interactions
{
    // Finger pose for the pistol press check. VRReloadController.PressCheck turns the gun in the hand about the thumb's knuckle. As it
    // turns, the fingers open (rightRestPoseEuler), then come down onto the top of the slide (that pose bent about curlAxis *
    // rightCurlSign, like the finger tracking and the loot grip). The thumb stays where it grips the frame: from the knuckle out it
    // turns with the gun.
    // Always computed on the right hand, which is the hand EFT holds the gun with. With the gun in the left hand, nothing is written
    // to the free right hand; the left shows the mirror image (HandMirror.ApplyMirroredRight, called from ApplyPressCheckHookLeft
    // after the grip's mirror).
    internal partial class HandsInteractionController
    {
        // deg on the slide: knuckle / middle / tip joint. The turn leaves the knuckles beside the slide at about its middle height
        // (on a 1911), so the knuckle bends most to reach over the top and the other joints wrap down onto it.
        // Tuned together with VRReloadController's wrist opening (30) and offset:
        [Tunable("Press check", TunableKind.Tune, "Index, middle and ring on the slide, bent from the open hand: knuckle, middle joint, tip.", Unit = "°")]
        public static Vector3 pressCheckHookFinger = new Vector3(30f, 40f, 45f);   // index to ring
        [Tunable("Press check", TunableKind.Tune, "The pinky's bend: knuckle, middle joint, tip.", Unit = "°")]
        public static Vector3 pressCheckHookPinky = new Vector3(-15f, -5f, 5f);
        // deg added to the gripping thumb's base / knuckle / tip joint: + closes it toward the palm, - opens it.
        // Bending the base moves the knuckle the gun turns about; the other joints bend from it.
        [Tunable("Press check", TunableKind.Tune, "The thumb on top of its grip: base, knuckle, tip. Positive closes it toward the palm, negative opens it.", Unit = "°")]
        public static Vector3 pressCheckHookThumb = Vector3.zero;

        private static readonly Quaternion[] pressCheckJointAnimated = new Quaternion[3];
        private static readonly Quaternion[] pressCheckJointShown = new Quaternion[3];
        // With the gun in the left hand: the right hand's press-check pose per bone, used for the left hand's mirror image
        private readonly Dictionary<Transform, Quaternion> pressCheckMirrorLocals = new Dictionary<Transform, Quaternion>();
        private bool pressCheckMirrorReady;
        // The thumb joint the gun turns about, as it grips at the start of this press check, in the marker's frame
        private int pressCheckThumbSerial = -1;
        private Quaternion pressCheckThumbInGun = Quaternion.identity;

        private void UpdatePressCheckHook()
        {
            pressCheckMirrorReady = false;
            float progress = VRReloadController.PressCheck.PressCheckProgress;
            if (progress <= 0f || rightRestPoseEuler == null || rightRestPoseEuler.Length == 0)
                return;
            if (!rightFingers.resolved)
                ResolveRightFingers();
            if (rightFingers.joints == null)
                return;
            bool mirrored = VRReloadController.PressCheck.PressCheckOnLeftHand;
            if (mirrored)
                pressCheckMirrorLocals.Clear();
            // The fingers open over the first part of the turn and close onto the slide over the rest (reversed on the way back)
            float closeFrom = Mathf.Clamp(VRReloadController.PressCheck.pressCheckFingersCloseFrom, 0.05f, 0.95f);
            float open = Mathf.SmoothStep(0f, 1f, progress / closeFrom);
            float close = Mathf.SmoothStep(0f, 1f, (progress - closeFrom) / (1f - closeFrom));
            // While the slide is drawn back, each finger tilts back with it from its knuckle (below, using its own knuckle-to-tip direction)
            bool dragging = VRReloadController.PressCheck.TryGetPressCheckDrag(out Vector3 backInPalm, out float pull);
            int poseIndex = 0;
            for (int finger = 0; finger < rightFingers.joints.Length; finger++)
            {
                Transform[] joints = rightFingers.joints[finger];
                bool thumb = rightFingers.isThumb != null && finger < rightFingers.isThumb.Length && rightFingers.isThumb[finger];
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
                // The posed finger's knuckle-to-tip vector in the palm's frame, built from the local chain because the bones' world poses
                // aren't this frame's yet. The knuckle is turned toward the gun's back by the angle that keeps the tip on the slide.
                if (dragging && jointCount == 3 && joints[0] != null && joints[1] != null && joints[2] != null)
                {
                    Vector3 toTip = pressCheckJointShown[0] * (joints[1].localPosition + pressCheckJointShown[1] * joints[2].localPosition);
                    Vector3 axis = Vector3.Cross(toTip, backInPalm);
                    if (axis.sqrMagnitude > 1e-10f)
                    {
                        float reach = Mathf.Max(VRReloadController.PressCheck.pressCheckFingerReach, 0.01f);
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

        // Applies the press-check rotation of a right-hand joint: written to the bone (gun in the right hand), or stored for the left
        // hand's mirror image
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

        // Runs after HandMirror.ApplyRealLeftHanded (the grip's mirror on the left). With the gun in the left hand, applies the press
        // check's mirror image.
        private void ApplyPressCheckHookLeft()
        {
            if (pressCheckMirrorReady && VRReloadController.PressCheck.PressCheckOnLeftHand)
                TarkovVR.Source.Player.Body.HandMirror.ApplyMirroredRight(pressCheckMirrorLocals);
        }

        // The thumb joint the gun turns about is rotated by the gun's turn relative to the hand, so the thumb stays where it grips.
        // Computed in the gun's frame (where the palm's rotation is known), not from this frame's bone world poses. The reference is
        // taken when a press check starts: the palm at that time and the thumb's grip as EFT animates it. The pressCheckHookThumb
        // bends are added on top, eased in with the turn.
        private readonly Quaternion[] pressCheckThumbShown = new Quaternion[3];

        private void KeepThumbOnGun(Transform[] joints, float amount, bool mirrored)
        {
            if (!VRReloadController.PressCheck.TryGetPressCheckThumb(out int serial, out int pivotJoint, out Quaternion palmNow, out Quaternion palmAtStart, out Quaternion thumbTurn)
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
                // Joints with no bend that aren't the pivot are left to EFT's animation on the hand itself
                if (!mirrored && joint != pivotJoint && pressCheckHookThumb[joint] == 0f)
                    continue;
                SetHookJoint(joints[joint], RecoverAnimated(joints[joint]), pressCheckThumbShown[joint], mirrored);
            }
        }
    }
}
