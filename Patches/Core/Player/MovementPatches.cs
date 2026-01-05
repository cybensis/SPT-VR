using Comfort.Common;
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
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;
using Valve.VR.InteractionSystem;
using static PlayerPhysicalClass;
using static RootMotion.FinalIK.AimPoser;
using static UnityEngine.UIElements.VisualElement;

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

        
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MovementContext), "InitComponents")]
        private static void SetPlayerRotate(MovementContext __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            __instance.TrunkRotationLimit = 0;

        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Prone2StandStateClass), "Exit")]
        private static void SetPlayerRotateOnTransition2Stand(Prone2StandStateClass __instance)
        {

            if (!__instance.MovementContext._player.IsYourPlayer)
                return;
            __instance.MovementContext.TrunkRotationLimit = 0;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Transit2ProneStateClass), "Enter")]
        private static void SetPlayerRotateOnTransition2Prone(Transit2ProneStateClass __instance)
        {

            if (!__instance.MovementContext._player.IsYourPlayer)
                return;
            __instance.MovementContext.TrunkRotationLimit = 0;
        }
        // Found in Player object under CurrentState variable, and is inherited by Gclass1573
        // Can access MovementContext and probably state through player object->HideoutPlayer->MovementContext
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "Rotate")]
        private static bool SetPlayerRotate(MovementState __instance, ref Vector2 deltaRotation)
        {
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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProneIdleStateClass), "Rotate")]
        private static bool SetPlayerRotateOnProneStationary(ProneIdleStateClass __instance, ref Vector2 deltaRotation)
        {

            if (!__instance.MovementContext._player.IsYourPlayer)
                return true;

            if (VRGlobals.menuOpen || !VRGlobals.inGame)
                return false;

            Vector3 bodyForward = Quaternion.Euler(0, 28, 0) * __instance.MovementContext._player.gameObject.transform.forward;
            GetBodyRotation(bodyForward, ref deltaRotation);

            __instance.MovementContext.Rotation = deltaRotation;
            
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProneMoveStateClass), "Rotate")]
        private static bool SetPlayerRotateOnProneMoving(ProneMoveStateClass __instance, ref Vector2 deltaRotation)
        {
            if (!__instance.MovementContext._player.IsYourPlayer)
                return true;

            if (VRGlobals.menuOpen || !VRGlobals.inGame)
                return false;

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
            float rightYAxis = Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.y);
            bool leftJoystickUsed = xAxis > VRSettings.GetLeftStickSensitivity() || yAxis > VRSettings.GetLeftStickSensitivity();
            bool rightJoystickUsed = rightYAxis > 0;

            float dotProduct = Vector3.Dot(Camera.main.transform.up, Vector3.up);
            float headY = (dotProduct < 0) ? (Camera.main.transform.eulerAngles.y - 180) : Camera.main.transform.eulerAngles.y;

            // Get head pitch
            float headPitch = VRGlobals.VRCam.transform.eulerAngles.x;
            float pitchThreshold = 50f;

            // Normalize pitch to -180 to 180 range
            if (headPitch > 180) headPitch -= 360;
            
            float rotDiff = CalculateYawDifference(headY, VRGlobals.player.Transform.rotation.eulerAngles.y) * -1;

            if (leftJoystickUsed)
            {
                if (VRSettings.GetMovementMode() == VRSettings.MovementMode.HeadBased)
                {
                    lastYRot = headY;
                }
                else if (VRSettings.GetMovementMode() == VRSettings.MovementMode.JoyStickOnly)
                {
                    // If the right joystick is used, or the head is rotated more than 80 degrees, or you are aiming down sight, set the legs to rotate based on head Y axis
                    if (rightJoystickUsed || Mathf.Abs(rotDiff) > 80 || (VRGlobals.firearmController != null && VRGlobals.firearmController.IsAiming && Mathf.Abs(rotDiff) > 50))
                        lastYRot = headY;
                }
                else
                    lastYRot = VRGlobals.vrPlayer.leftHandYRotation + VRGlobals.vrOffsetter.transform.eulerAngles.y;

            }
            else if (!(WeaponPatches.currentGunInteractController && WeaponPatches.currentGunInteractController.highlightingMesh) && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) > 0.20f && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) > Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.y))
                lastYRot = headY;
            else if (Mathf.Abs(rotDiff) > 10)
                lastYRot = Mathf.LerpAngle(lastYRot, headY, Time.deltaTime * 3f);   

            // Scale and clamp to -90 to 90 range (less jarring/buggy than 180)
            headPitch = Mathf.Clamp(headPitch / 2, -90, 90);
            if (VRGlobals.usingItem)
                headPitch = 0;
            deltaRotation = new Vector2(deltaRotation.x + lastYRot, headPitch);

            leftJoystickLastUsed = leftJoystickUsed;

            if (yAxis > xAxis)
                VRGlobals.player.MovementContext.RelativeSpeed = yAxis;
            else
                VRGlobals.player.MovementContext.RelativeSpeed = xAxis;

            VRGlobals.player.MovementContext.SetCharacterMovementSpeed(VRGlobals.player.MovementContext.RelativeSpeed * VRGlobals.player.MovementContext.MaxSpeed);
        }


        // GClass1854 inherits MovementState and its version of ProcessUpperbodyRotation is ran when the player is moving, __instance ensures 
        // the rotation when not moving is similar to that when moving otherwise the player can rotate their camera faster than the body
        // can rotate
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "ProcessUpperbodyRotation")]
        private static bool FixBodyRotation(MovementState __instance, float deltaTime)
        {
            //float y = Mathf.Abs(__instance.MovementContext.TransformRotation.eulerAngles.y - camRoot.transform.eulerAngles.y);
            //if (y > 20)
            //    __instance.MovementContext.ApplyRotation(Quaternion.Lerp(__instance.MovementContext.TransformRotation, __instance.MovementContext.TransformRotation * Quaternion.Euler(0f, y, 0f), 30f * deltaTime));
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

        // GClass1913 is a class used by the PlayerCameraController to position and rotate the camera, PlayerCameraController holds the abstract class GClass1943 which this inherits
        
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FirstPersonCameraOperationClass), "ManualLateUpdate")]
        private static bool PositionCamera(FirstPersonCameraOperationClass __instance)
        {
            
            if (!__instance.Player_0.IsYourPlayer || !VRGlobals.inGame || VRGlobals.menuOpen)
                return true;

            // When medding or eating, we need to rely on this code to position the upper body, and it will set the empty hands but the current gun interaction controller should be disabled
            if (VRGlobals.emptyHands && VRGlobals.player.HandsIsEmpty)
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            else if (VRGlobals.emptyHands && (!WeaponPatches.currentGunInteractController || !WeaponPatches.currentGunInteractController.enabled)) {
                if (!WeaponPatches.currentGunInteractController || WeaponPatches.currentGunInteractController.transform.parent != VRGlobals.emptyHands)
                    VRGlobals.ikManager.MatchLegsToArms();
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
                
            }
            else if (!VRGlobals.emptyHands)
                VRGlobals.camRoot.transform.position = new Vector3(__instance.Player_0.Transform.position.x, __instance.Player_0.Transform.position.y + 1.5f, __instance.Player_0.Transform.position.z);
            //Plugin.MyLog.LogError($"playerY={__instance.Player_0.Transform.position.y:F5}, camRootY={VRGlobals.camRoot.transform.position.y:F5}, rightHandY={VRGlobals.vrPlayer.RightHand.transform.position.y:F5}");
            return false;
        }
        
    }
}
