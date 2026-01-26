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
using Valve.VR;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class IKPatches
    {       

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
        [HarmonyPatch(typeof(WalkEffector), "Process")]
        private static bool DisableWalkEffector(WalkEffector __instance, float deltaTime)
        {
            return false;
        }
        

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerBones), "SetShoulders")]
        private static bool OverrideShoulders(PlayerBones __instance)
        {
            if (__instance.Player == null || !__instance.Player.IsYourPlayer ||
                VRGlobals.vrPlayer == null || VRGlobals.ikManager == null ||
                VRSettings.GetLeftHandedMode())
                return true;

            // Base shoulder dimensions
            const float ShoulderWidth = 0.13f;
            const float ShoulderHeight = -0.165f;
            const float ShoulderDepth = -0.10f;
            const float NeckLength = 0.15f;
            const float STANDING_BASELINE = 1.7f;
            const float SITTING_BASELINE = 1.25f;

            // Height-based scaling
            float baselineHeight = VRSettings.GetSeatedMode() ? SITTING_BASELINE : STANDING_BASELINE;
            float heightRatio = VRGlobals.vrPlayer.initPos.y / baselineHeight;
            float armLengthOffset = (heightRatio - 1f) * 0.25f;

            // State-based offsets
            bool isSprinting = __instance.Player.IsSprintEnabled;
            bool isAiming = VRGlobals.firearmController?.IsAiming ?? false;
            bool isUsingItem = VRGlobals.usingItem;

            bool sprintAnimEnabled = isSprinting && !VRSettings.GetDisableRunAnim();
            if (sprintAnimEnabled) return true;

            float aimOffset = isAiming ? -0.02f : 0.02f;
            float sprintOffset = isSprinting ? 0.10f : 0f;
            float usingItemOffset = isUsingItem ? -0.10f : 0f;

            // Calculate neck base position
            Vector3 headPos = VRGlobals.VRCam.transform.position;
            Vector3 headForward = VRGlobals.VRCam.transform.forward;
            Vector3 headForwardFlat = new Vector3(headForward.x, 0f, headForward.z).normalized;

            float headPitch = VRGlobals.VRCam.transform.eulerAngles.x * Mathf.Deg2Rad;
            Vector3 neckBase = headPos - (headForwardFlat * NeckLength * Mathf.Sin(headPitch));
            neckBase.y = headPos.y;

            // Body-relative directions
            float bodyYaw = __instance.Player.Transform.eulerAngles.y;
            Quaternion yawRotation = Quaternion.Euler(0f, bodyYaw, 0f);
            Vector3 right = yawRotation * Vector3.right;
            Vector3 forward = yawRotation * Vector3.forward;

            // Calculate shoulder offsets
            float leftLateral = -ShoulderWidth - sprintOffset;
            float leftDepth = ShoulderDepth + sprintOffset + armLengthOffset; //+ usingItemOffset;

            float rightLateral = ShoulderWidth + sprintOffset;
            float rightDepth = ShoulderDepth + aimOffset + sprintOffset + armLengthOffset; //+ usingItemOffset;

            Vector3 leftOffset = right * leftLateral + Vector3.up * ShoulderHeight + forward * leftDepth;
            Vector3 rightOffset = right * rightLateral + Vector3.up * ShoulderHeight + forward * rightDepth;

            // Apply positions
            TransformHelperClass.LerpPositionAndRotation(
                __instance.Shoulders[0],
                neckBase + leftOffset,
                __instance.Shoulders_Anim[0].rotation,
                0.65f);

            TransformHelperClass.LerpPositionAndRotation(
                __instance.Shoulders[1],
                neckBase + rightOffset,
                __instance.Shoulders_Anim[1].rotation,
                0.65f);

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

        // This can also be used to disable hand animations when needed
        private static float zeroOutEndTime = 0f;
        private const float ZERO_OUT_DURATION = 1f;
        private static bool wasSprinting = false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "method_20")]
        private static bool RemoveSprintAnimFromHands(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return true;
            if (__instance.HandsIsEmpty)
                return false;

            float fallingValue = __instance.MovementContext.PlayerAnimator_1.Animator.GetFloat(
                PlayerAnimator.FALLINGDOWN_FLOAT_PARAM_HASH
            );

            bool stableGrounded = fallingValue < 0.6f;

            bool disableAnim = (__instance.IsSprintEnabled && VRSettings.GetDisableRunAnim()) || !stableGrounded;

            float currentTime = Time.time;

            if (wasSprinting && !disableAnim)
                zeroOutEndTime = currentTime + ZERO_OUT_DURATION;

            wasSprinting = disableAnim;

            bool shouldStayZeroed = disableAnim || (currentTime < zeroOutEndTime);

            if (shouldStayZeroed && __instance._markers.Length > 1 && __instance._markers[1]?.transform?.parent?.parent != null)
            {
                var ik = __instance._markers[1].transform.parent.parent;
                ik.localPosition = Vector3.zero;
                ik.localEulerAngles = Vector3.zero;
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerAnimator), "EnableSprint")]
        private static void DisableLayer1DuringSprint(PlayerAnimator __instance)
        {
            if (VRSettings.GetDisableRunAnim())
            {
                __instance.Animator.SetLayerWeight(1, 0f);
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
