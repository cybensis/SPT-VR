using EFT;
using HarmonyLib;
using UnityEngine;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class IKPatches
    {

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
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


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
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


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
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


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "method_20")]
        private static bool RemoveSprintAnimFromHands(EFT.Player __instance)
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


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerAnimator), "EnableSprint")]
        private static void DisableLayer1DuringSprint(PlayerAnimator __instance)
        {
            if (VRSettings.GetDisableRunAnim()) // Or your own condition
            {
                __instance.Animator.SetLayerWeight(1, 0f); // Layer 1: likely upper body
            }
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GClass1375), "SetAnimator")]
        private static void RemoveSprintAnimFromHands(GClass1375 __instance)
        {
            __instance.animator_0.SetLayerWeight(4, 0);
        }
    }
}
