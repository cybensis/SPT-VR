using EFT;
using EFT.Animations;
using EFT.UI;
using HarmonyLib;
using RootMotion.FinalIK;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Drawing.Printing;
using System.Reflection;
using System.Runtime.CompilerServices;
using TarkovVR.Patches.Core.Equippables;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;
using Valve.VR.InteractionSystem;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class MovementPatches
    {

        private static float lastYRot = 0f;
        private static float timeSinceLastLookRot = 0f;
        private static bool leftJoystickLastUsed = false;
        private static bool isRotatingToHead = false;
        private static float rotationStartY = 0f;
        private static float rotationTargetY = 0f;
        private static float rotationProgress = 0f;


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MovementContext), "InitComponents")]
        private static void RestrictTrunkRotation(MovementContext __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            __instance.TrunkRotationLimit = 0;

        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        //------------------------------------------------------- PLAYER BODY ROTATION -------------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "Rotate")]
        private static bool SetPlayerRotate(MovementState __instance, ref Vector2 deltaRotation)
        {
            return PlayerRotationHandler(__instance, ref deltaRotation);
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProneIdleStateClass), "Rotate")]
        private static bool SetPlayerRotateOnProneStationary(ProneIdleStateClass __instance, ref Vector2 deltaRotation)
        {
            return PlayerRotationHandler(__instance, ref deltaRotation);
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProneMoveStateClass), "Rotate")]
        private static bool SetPlayerRotateOnProneMoving(ProneMoveStateClass __instance, ref Vector2 deltaRotation)
        {
            return PlayerRotationHandler(__instance, ref deltaRotation);
        }

        private static bool PlayerRotationHandler(MovementState __instance, ref Vector2 deltaRotation) {
            if (!__instance.MovementContext._player.IsYourPlayer)
                return true;

            if (VRGlobals.menuOpen || !VRGlobals.inGame)
                return false;

            // Normally you'd stand with your left foot forward and right foot back, which doesn't feel natural in VR so rotate 28 degrees to have both feet in front when standing still
            Vector3 bodyForward = Quaternion.Euler(0, 28, 0) * __instance.MovementContext._player.gameObject.transform.forward;
            GetBodyRotation(bodyForward, ref deltaRotation);

            __instance.MovementContext.Rotation = deltaRotation;

            return false;
        }
        private static float CalculateYawDifference(float yawA, float yawB)
        {
            float difference = yawB - yawA;

            // Normalize the difference to the range of -180 to 180 degrees
            if (difference > 180)
            {
                difference -= 360;
            }
            else if (difference < -180)
            {
                difference += 360;
            }

            return difference;
        }
        private static void GetBodyRotation(Vector3 bodyForward, ref Vector2 deltaRotation) {
            float xAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x);
            float yAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y);
            bool leftJoystickUsed = xAxis > VRSettings.GetLeftStickSensitivity() || yAxis > VRSettings.GetLeftStickSensitivity();

            float dotProduct = Vector3.Dot(Camera.main.transform.up, Vector3.up);
            float headY = (dotProduct < 0) ? (Camera.main.transform.eulerAngles.y - 180) : Camera.main.transform.eulerAngles.y;
            float rotDiff = CalculateYawDifference(headY, VRGlobals.player.Transform.rotation.eulerAngles.y) * -1;


            if (leftJoystickUsed)
            {
                if (VRSettings.GetMovementMode() == VRSettings.MovementMode.HeadBased)
                    lastYRot = headY;
                else
                    lastYRot = VRGlobals.vrPlayer.leftHandYRotation + VRGlobals.vrOffsetter.transform.eulerAngles.y;

            }
            else if (!(EquippablesShared.currentGunInteractController && EquippablesShared.currentGunInteractController.highlightingMesh) && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) > 0.20f && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) > Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.y))
                lastYRot = headY;
            
            else
            {
                lastYRot = Mathf.LerpAngle(lastYRot, headY, Time.deltaTime * 10f); // Smooth follow
            }
            
            timeSinceLastLookRot += Time.deltaTime;
            
            deltaRotation = new Vector2(deltaRotation.x + lastYRot, 0);
            leftJoystickLastUsed = leftJoystickUsed;
            if (yAxis > xAxis)
                VRGlobals.player.MovementContext._relativeSpeed = yAxis;
            else
                VRGlobals.player.MovementContext._relativeSpeed = xAxis;
            VRGlobals.player.MovementContext.SetCharacterMovementSpeed(VRGlobals.player.MovementContext._relativeSpeed * VRGlobals.player.MovementContext.MaxSpeed);

        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // When the player is stationary and uses joysticks to rotate, this ensures the bodies rotates with the camera instead of the body only rotating every 45 degrees or so with a camera turn
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "ProcessUpperbodyRotation")]
        private static bool MatchBodyToCameraOnInputTurn(MovementState __instance, float deltaTime)
        {
            if (!__instance.MovementContext._player.IsYourPlayer || !VRGlobals.inGame)
                return true;

            __instance.UpdateRotationSpeed(deltaTime);
            float float_3 = __instance.MovementContext.TransformRotation.eulerAngles.y;
            float f = Mathf.DeltaAngle(__instance.MovementContext.Yaw, float_3);
            float num = Mathf.InverseLerp(10f, 45f, Mathf.Abs(f)) + 1f;
            float_3 = Mathf.LerpAngle(float_3, __instance.MovementContext.Yaw, EFTHardSettings.Instance.TRANSFORM_ROTATION_LERP_SPEED * deltaTime * num);
            __instance.MovementContext.ApplyRotation(Quaternion.AngleAxis(float_3, Vector3.up) * __instance.MovementContext.AnimatorDeltaRotation);
            //Plugin.MyLog.LogError("Process upper " + (Quaternion.AngleAxis(float_3, Vector3.up) * __instance.MovementContext.AnimatorDeltaRotation).eulerAngles);
            return false;
             
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Positions the camera in line with the body in a manner better suited for VR
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GClass3407), "ManualLateUpdate")]
        private static bool PositionCamera(GClass3407 __instance)
        {
            
            if (!__instance.player_0.IsYourPlayer || !VRGlobals.inGame || VRGlobals.menuOpen)
                return true;

            // When medding or eating, we need to rely on this code to position the upper body, and it will set the empty hands but the current gun interaction controller should be disabled
            if (VRGlobals.emptyHands && VRGlobals.player.HandsIsEmpty)
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            else if (VRGlobals.emptyHands && (!EquippablesShared.currentGunInteractController || !EquippablesShared.currentGunInteractController.enabled)) {
                if (!EquippablesShared.currentGunInteractController || EquippablesShared.currentGunInteractController.transform.parent != VRGlobals.emptyHands)
                    VRGlobals.ikManager.MatchLegsToArms();
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
                
            }
            else if (!VRGlobals.emptyHands)
                VRGlobals.camRoot.transform.position = new Vector3(__instance.player_0.Transform.position.x, __instance.player_0.Transform.position.y + 1.5f, __instance.player_0.Transform.position.z);

            return false;
        }
    }
}
