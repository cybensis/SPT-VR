using EFT;
using HarmonyLib;
using System;
using UnityEngine;
using TarkovVR.Source.Settings;
using static EFT.Player;
using EFT.Animations;
using EFT.ItemInHandSubsystem;
using EFT.UI.Ragfair;
using RootMotion.FinalIK;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class IKPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MotionEffector), "Process")]
        private static bool RemoveGunTilt1(MotionEffector __instance)
        {
            // Skip all processing that moves/tilts the weapon
            return false;

        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "HeightInterpolation")]
        private static bool DisableYAxisSmoothing(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return true;
            return false;
        }

        //This disables the gun shifting closer to the camera when aiming down sights on certains guns, specifically ones without a stock
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProceduralWeaponAnimation), "CheckShouldMoveWeaponCloser")]
        private static bool DisableGunShift(ProceduralWeaponAnimation __instance)
        {
            return false;

        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerBones), "SetShoulders")]
        private static bool OverrideShoulders(PlayerBones __instance)
        {
            if (__instance == null || __instance.Shoulders_Anim == null || __instance.Shoulders_Anim.Length < 2 ||
                __instance.Player == null || !__instance.Player.IsYourPlayer ||
                VRGlobals.switchingWeapon || VRGlobals.usingItem ||
                VRGlobals.vrPlayer == null || VRGlobals.ikManager == null || VRSettings.GetLeftHandedMode())
                return true;

            // Start with the animated shoulder positions
            Vector3 leftShoulderPos = __instance.Shoulders_Anim[0].position;
            Vector3 rightShoulderPos = __instance.Shoulders_Anim[1].position;

            // Get yaw-only rotation to apply offsets relative to body direction
            float bodyYaw = __instance.Player.Transform.eulerAngles.y;
            Quaternion yawOnlyRotation = Quaternion.Euler(0f, bodyYaw, 0f);
            Vector3 forwardYawOnly = yawOnlyRotation * Vector3.forward;
            Vector3 rightYawOnly = yawOnlyRotation * Vector3.right;


            // Adjust these values to position shoulders
            Vector3 offsetLeft = forwardYawOnly * (VRSettings.GetLeftHandedMode() ? -0.08f : -0.06f)     // Forward/backward
                                + rightYawOnly * (VRSettings.GetLeftHandedMode() ? 0.05f : -0.05f)      // Left/right
                                + Vector3.up * -0.08f;        // Up/down
            leftShoulderPos += offsetLeft;

            Vector3 offsetRight = forwardYawOnly * 0.08f      // Forward/backward
                                 + rightYawOnly * (VRSettings.GetLeftHandedMode() ? -0.07f : 0.05f)       // Left/right
                                  + Vector3.up * -0.08f;       // Up/down
            rightShoulderPos += offsetRight;

            // Apply the offset positions
            TransformHelperClass.LerpPositionAndRotation(
                __instance.Shoulders[0],
                leftShoulderPos,
                __instance.Shoulders_Anim[0].rotation,
                0.65f);

            TransformHelperClass.LerpPositionAndRotation(
                __instance.Shoulders[1],
                rightShoulderPos,
                __instance.Shoulders_Anim[1].rotation,
                0.65f);

            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "VisualPass")]
        private static bool IKVisualPassTesting(EFT.Player __instance)
        {
            if (__instance.CustomAnimationsAreProcessing)
            {
                return false;
            }
            float num = 0f;
            if (!__instance.FirstPersonPointOfView)
            {
                num = CameraClass.Instance.Distance(__instance.Transform.position);
            }
            bool flag = __instance.FirstPersonPointOfView || (BackendConfigAbstractClass.Config.UseSpiritPlayer && !__instance.Spirit.IsActive) || (__instance.IsVisible && num <= EFTHardSettings.Instance.CULL_GROUNDER);
            if ((__instance._armsupdated || __instance.ArmsUpdateMode == EUpdateMode.Auto) && flag && (__instance.EnabledAnimators & EAnimatorMask.Procedural) != 0 && !__instance.UsedSimplifiedSkeleton)
            {
                __instance.ProceduralWeaponAnimation.ProcessEffectors((__instance._nFixedFrames > 0) ? __instance._fixedTime : __instance._armsTime, Mathf.Max(0, __instance._nFixedFrames), __instance.Motion, __instance.Velocity);
                __instance.PlayerBones.Offset = __instance.ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim.localPosition;
                __instance.PlayerBones.DeltaRotation = __instance.ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim.localRotation;
            }
            if (__instance._bodyupdated)
            {
                if (flag && !__instance.UsedSimplifiedSkeleton)
                {
                    __instance.RestoreIKPos();
                    __instance.HeightInterpolation(__instance._bodyTime);
                    __instance.FBBIKUpdate(num);
                    __instance.MouseLook();
                    if ((__instance.EnabledAnimators & EAnimatorMask.IK) != 0)
                    {
                        float num2 = (__instance.FirstPersonPointOfView ? __instance.method_25(PlayerAnimator.FIRST_PERSON_CURVE_WEIGHT) : 1f);
                        float positionCacheValue = __instance.method_25(PlayerAnimator.POSITION_CACHE_FOR_WEAPON_PROCEDURAL) * num2;
                        float num3 = __instance.method_25(PlayerAnimator.LEFT_STANCE_CURVE);
                        __instance.ProceduralWeaponAnimation.GetLeftStanceCurrentCurveValue(num3);
                        __instance._firstPersonRightHand = 1f - __instance.method_25(PlayerAnimator.RIGHT_HAND_WEIGHT) * num2;
                        __instance._firstPersonLeftHand = 1f - __instance.method_25(PlayerAnimator.LEFT_HAND_WEIGHT) * num2;
                        __instance.ThirdPersonWeaponRootAuthority = (__instance.MovementContext.IsInMountedState ? 0f : (__instance.method_25(PlayerAnimator.WEAPON_ROOT_3RD) * num2));
                        if (__instance.FirstPersonPointOfView)
                        {
                            __instance._smoothLW = ((__instance._smoothLW > __instance._firstPersonLeftHand) ? __instance._firstPersonLeftHand : Mathf.SmoothDamp(__instance._smoothLW, __instance._firstPersonLeftHand, ref __instance._shoulderVel, 0.2f));
                            if (__instance.MovementContext.IsInMountedState && !__instance.IsInPronePose)
                            {
                                __instance.PlayerBones.SetShoulders(1f, 1f);
                            }
                            
                            else
                            {
                                __instance.PlayerBones.SetShoulders(1f - __instance.method_25(PlayerAnimator.LEFT_SHOULDER_WEIGHT), 1f - __instance.method_25(PlayerAnimator.RIGHT_SHOULDER_WEIGHT));
                            }
                            
                        }
                        else
                        {
                            __instance.method_23(num);
                        }
                        if (__instance._armsupdated || __instance.ArmsUpdateMode == EUpdateMode.Auto)
                        {
                            float thirdPersonAuthority = __instance.ThirdPersonWeaponRootAuthority;
                            if (__instance.PointOfView == EPointOfView.ThirdPerson && __instance.MovementContext.StationaryWeapon != null)
                            {
                                thirdPersonAuthority = 0f;
                            }
                            bool inSprint = __instance.MovementContext.CurrentState.Name == EPlayerState.Sprint;
                            bool lastAnimValue = __instance.MovementContext.LeftStanceController.LastAnimValue;
                            bool leftStance = __instance.MovementContext.LeftStanceController.LeftStance;
                            if (__instance.MovementContext.PlayerAnimator.AnimatedInteractions.IsInteractionPlaying)
                            {
                                __instance.MovementContext.LeftStanceController.DisableLeftStanceAnimFromBodyAction();
                            }
                            if (__instance._isInteractionPlayeingLastFrame && !__instance.MovementContext.PlayerAnimator.AnimatedInteractions.IsInteractionPlaying)
                            {
                                __instance.MovementContext.LeftStanceController.SetAnimatorLeftStanceToCacheFromBodyAction();
                            }
                            __instance._isInteractionPlayeingLastFrame = __instance.MovementContext.PlayerAnimator.AnimatedInteractions.IsInteractionPlaying;
                            __instance.PlayerBones.ShiftWeaponRoot(__instance._bodyTime, __instance.PointOfView, thirdPersonAuthority, armsupdated: false, positionCacheValue, num3, inSprint, lastAnimValue, leftStance, __instance.ProceduralWeaponAnimation.IsAiming, __instance.MovementContext.PlayerAnimator.AnimatedInteractions.IsInteractionPlaying, __instance._leftHandController.IsUsing);
                        }
                        __instance.PlayerBones.RotateHead(0f, __instance.ProceduralWeaponAnimation.GetHeadRotation(), __instance.MovementContext.LeftStanceEnabled && __instance.HasFirearmInHands(), num3, __instance.ProceduralWeaponAnimation.IsAiming);
                        __instance.HandPosers[0].weight = __instance._firstPersonLeftHand;
                        __instance._limbs[0].solver.IKRotationWeight = (__instance._limbs[0].solver.IKPositionWeight = __instance._firstPersonLeftHand);
                        __instance._limbs[1].solver.IKRotationWeight = (__instance._limbs[1].solver.IKPositionWeight = __instance._firstPersonRightHand);
                        __instance.method_20(num);
                        __instance.method_24(num2);
                        __instance.method_19(num);
                        if (__instance._firstPersonRightHand < 1f)
                        {
                            __instance.PlayerBones.Kinematics(__instance._markers[1], __instance._firstPersonRightHand);
                        }
                    }
                    float num4 = __instance.method_25(PlayerAnimator.AIMING_LAYER_CURVE);
                    __instance.MovementContext.PlayerAnimator.Animator.SetLayerWeight(6, 1f - num4);
                    __instance._prevHeight = __instance.Transform.position.y;
                }
                else
                {
                    __instance.method_14();
                    __instance.MouseLook();
                }
            }
            if (num > EFTHardSettings.Instance.AnimatorCullDistance)
            {
                __instance.BodyAnimatorCommon.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
                __instance.ArmsAnimatorCommon.cullingMode = ((!(__instance._handsController is EmptyHandsController) && !(__instance._handsController is KnifeController) && !(__instance._handsController is UsableItemController)) ? AnimatorCullingMode.CullUpdateTransforms : AnimatorCullingMode.AlwaysAnimate);
            }
            else
            {
                __instance.BodyAnimatorCommon.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                __instance.ArmsAnimatorCommon.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            if (__instance._armsupdated || __instance.ArmsUpdateMode == EUpdateMode.Auto)
            {
                __instance.ProceduralWeaponAnimation.LateTransformations(Time.deltaTime);
                if (__instance.HandsController != null)
                {
                    __instance.HandsController.ManualLateUpdate(Time.deltaTime);
                }
            }
            if (__instance.UsedSimplifiedSkeleton)
            {
                Transform child = __instance.PlayerBones.Weapon_Root_Anim.GetChild(0);
                child.localPosition = Vector3.zero;
                child.localRotation = Quaternion.identity;
            }
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "UpdateBonesOnWeaponChange")]
        private static void FixLeftArmBendGoal(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return;
            // Change the elbow bend from the weapons left arm goal to the player bodies bend goal, otherwise the left arms bend goal acts like its
            // still attached to the gun even when its not

            __instance._elbowBends[0] = __instance.PlayerBones.BendGoals[0];
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "SetCompensationScale")]
        private static void SetBodyIKScale(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return;
            // If this isn't set to 1, then the hands start to stretch or squish when rotating them around
            __instance.RibcageScaleCurrentTarget = 1f;
            __instance.RibcageScaleCurrent = 1f;
        }

        //Leaving this here for reference
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerAnimator), nameof(PlayerAnimator.EnableSprint))]
        private static bool DisableSprintAnimation(PlayerAnimator __instance, ref bool enabled)
        {
            if (VRSettings.GetDisableRunAnim())
            {
                enabled = false;
            }
            return true;
        }
        */
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "method_20")]
        private static bool ReemoveSprintAnimFromHands(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return true;

            if (__instance.HandsIsEmpty)
                return false;

            Vector3 surfaceNormal = __instance.MovementContext.SurfaceNormal;
            bool onSlope = surfaceNormal.y < 1.00f;

            bool disableAnim =
                (__instance.IsSprintEnabled && VRSettings.GetDisableRunAnim()) ||
                (!__instance.MovementContext.IsGrounded && !onSlope);

            if (disableAnim)
            {
                var ik = __instance._markers[1].transform.parent.parent;
                ik.localPosition = Vector3.zero;
                ik.localEulerAngles = Vector3.zero;
            }

            return true;
        }
        */
        // Static variables to track grounded state with hysteresis
        // Hysteresis state
        // This can also be used to disable hand animations when needed
        private const float groundedThreshold = 0.2f; // seconds before considering ungrounded
        private static bool wasGrounded = true;
        private static float ungroundedStartTime = 0f;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "method_20")]
        private static bool RemoveSprintAnimFromHands(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return true;
            if (__instance.HandsIsEmpty)
                return false;           

            bool isGroundedNow = __instance.MovementContext.IsGrounded;
            float currentTime = Time.time;

            bool stableGrounded;

            if (isGroundedNow)
            {
                // Immediately treat as grounded
                stableGrounded = true;
                ungroundedStartTime = 0f; // reset
            }
            else
            {
                if (wasGrounded)
                {
                    // Just became ungrounded
                    if (ungroundedStartTime == 0f)
                        ungroundedStartTime = currentTime;

                    // Only treat as ungrounded if time threshold has passed
                    stableGrounded = (currentTime - ungroundedStartTime) < groundedThreshold;
                }
                else
                {
                    // Already ungrounded
                    stableGrounded = false;
                }
            }

            wasGrounded = stableGrounded;

            bool disableAnim =               
                (__instance.IsSprintEnabled && VRSettings.GetDisableRunAnim()) ||
                !stableGrounded;
            
            if (disableAnim && __instance._markers.Length > 1 && __instance._markers[1]?.transform?.parent?.parent != null)
            {
                var ik = __instance._markers[1].transform.parent.parent;
                ik.localPosition = Vector3.zero;
                ik.localEulerAngles = Vector3.zero;
            }

            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "OnStateEnter")]
        private static void DisableHandAnimationOnEnter(MovementState __instance)
        {

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MovementState), "OnStateExit")]
        private static void DisableHandAnimationOnExit(MovementState __instance)
        {
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerAnimator), "EnableSprint")]
        private static void DisableLayer1DuringSprint(PlayerAnimator __instance)
        {
            if (VRSettings.GetDisableRunAnim()) // Or your own condition
            {
                __instance.Animator.SetLayerWeight(1, 0f); // Layer 1: likely upper body
            }
        }

        private static bool test = true;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GClass1446), "SetAnimator")]
        private static void ReemoveSprintAnimFromHands(GClass1446 __instance)
        {
            __instance.Animator_0.SetLayerWeight(4, 0);
        }
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(EFT.Player), "method_22")]
        //private static bool SetHandIKPosition(EFT.Player __instance, float distance2Camera)
        //{
        //    for (int i = 0; i < 2; i++)
        //    {
        //        if (i == 1)
        //            __instance._markers[i].localPosition += new Vector3(-0.06f, 0, 0);
        //        else { 
        //            __instance._markers[i].localPosition += VRGlobals.test;
        //            __instance._markers[i].localEulerAngles += VRGlobals.testRot;
        //        }

        //        if (!(__instance._markers[i] == null) && !(Math.Abs(__instance._limbs[i].solver.IKPositionWeight) < float.Epsilon))
        //        {
        //            if (__instance._ikTargets[i] != null && distance2Camera < 40f)
        //            {
        //                float value = Vector3.Distance(__instance._markers[i].position, __instance._gripReferences[i].position);
        //                float num = Mathf.InverseLerp(0.1f, 0f, value);
        //                __instance.HandPosers[i].GripWeight = num;
        //                __instance._ikPosition = Vector3.Lerp(__instance._markers[i].position, __instance._ikTargets[i].position, num);
        //                __instance._ikRotation = Quaternion.Lerp(__instance._markers[i].rotation, __instance._ikTargets[i].rotation, num);
        //            }
        //            else
        //            {
        //                __instance._ikPosition = __instance._markers[i].position;
        //                __instance._ikRotation = __instance._markers[i].rotation;
        //            }
        //            if (__instance.LeftHandInteractionTarget != null && i == 0)
        //            {
        //                __instance._ikPosition = Vector3.Lerp(__instance._ikPosition, __instance.LeftHandInteractionTarget.transform.position, __instance.ThirdIkWeight.Value);
        //                __instance._ikRotation = Quaternion.Slerp(__instance._ikRotation, __instance.LeftHandInteractionTarget.transform.rotation, __instance.ThirdIkWeight.Value);
        //            }
        //            __instance._limbs[i].solver.SetIKPosition(__instance._ikPosition);
        //            __instance._limbs[i].solver.SetIKRotation(__instance._ikRotation);
        //        }
        //    }
        //    return false;
        //}


        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(FirearmsAnimator), "SetSprint")]
        //private static bool DisableSprintAnimation(FirearmsAnimator __instance)
        //{
        //    return false;
        //}

    }
}
