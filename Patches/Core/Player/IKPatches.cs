using EFT;
using HarmonyLib;
using System;
using UnityEngine;
using TarkovVR.Source.Settings;
using static EFT.Player;
using EFT.Animations;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class IKPatches
    {
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
        [HarmonyPatch(typeof(GClass1375), "SetAnimator")]
        private static void ReemoveSprintAnimFromHands(GClass1375 __instance)
        {
            __instance.animator_0.SetLayerWeight(4, 0);
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
