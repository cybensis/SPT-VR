using EFT;
using EFT.UI;
using HarmonyLib;
using System.ComponentModel.Design;
using System.Drawing.Printing;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class MovementPatches
    {

        private static float lastYRot = 0f;
        private static float timeSinceLastLookRot = 0f;
        private static bool leftJoystickLastUsed = false;
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
        [HarmonyPatch(typeof(GClass1709), "Rotate")]
        private static bool SetPlayerRotateOnProneStationary(GClass1709 __instance, ref Vector2 deltaRotation)
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
        [HarmonyPatch(typeof(GClass1718), "Rotate")]
        private static bool SetPlayerRotateOnProneMoving(GClass1718 __instance, ref Vector2 deltaRotation)
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

        private static void GetBodyRotation(Vector3 bodyForward, ref Vector2 deltaRotation) {
            float xAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x);
            float yAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y);
            bool leftJoystickUsed = xAxis > VRSettings.GetLeftStickSensitivity() || yAxis > VRSettings.GetLeftStickSensitivity();

            Vector3 cameraForward = Camera.main.transform.forward;
            float rotDiff = Vector3.SignedAngle(bodyForward, cameraForward, Vector3.up);
            float angleDifference = Camera.main.transform.eulerAngles.y - VRGlobals.player.Transform.rotation.eulerAngles.y;
            rotDiff = (angleDifference + 180) % 360 - 180;
            Vector3 headEulerAngles = Camera.main.transform.localEulerAngles;
            // Normalize the angle to the range [-180, 180]
            float pitch = headEulerAngles.x;
            if (pitch > 180)
                pitch -= 360;

            if (leftJoystickUsed) {
                if (VRSettings.GetMovementMode() == VRSettings.MovementMode.HeadBased)
                    lastYRot = Camera.main.transform.eulerAngles.y;
                else
                    lastYRot = VRGlobals.vrPlayer.leftHandYRotation + VRGlobals.vrOffsetter.transform.eulerAngles.y;

            }
            
            else if (SteamVR_Actions._default.RightJoystick.axis.x != 0)
                lastYRot = VRGlobals.camRoot.transform.eulerAngles.y;
            // Rotate the player body to match the camera if the player isn't looking down, if the rotation from the body is greater than 80 degrees, and if they haven't already rotated recently
            else if (pitch < 50 && Mathf.Abs(rotDiff) > 50 && timeSinceLastLookRot > 0.25)
            {
                lastYRot += rotDiff;
                timeSinceLastLookRot = 0;
            }
            timeSinceLastLookRot += Time.deltaTime;

            deltaRotation = new Vector2(deltaRotation.x + lastYRot, 0);
            leftJoystickLastUsed = leftJoystickUsed;
            //Plugin.MyLog.LogWarning(rotDiff + "   |   " + deltaRotation + "  |  " + VRGlobals.player.Transform.localRotation.eulerAngles);
            if (yAxis > xAxis)
                VRGlobals.player.MovementContext._relativeSpeed = yAxis;
            else
                VRGlobals.player.MovementContext._relativeSpeed = xAxis;
            VRGlobals.player.MovementContext.SetCharacterMovementSpeed(VRGlobals.player.MovementContext._relativeSpeed * VRGlobals.player.MovementContext.MaxSpeed);
        }


        // GClass1854 inherits MovementState and its version of ProcessUpperbodyRotation is ran when the player is moving, this ensures 
        // the rotation when not moving is similar to that when moving otherwise the player can rotate their camera faster than the body
        // can rotate
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "ProcessUpperbodyRotation")]
        private static bool FixBodyRotation(MovementState __instance, float deltaTime)
        {
            //float y = Mathf.Abs(__instance.MovementContext.TransformRotation.eulerAngles.y - camRoot.transform.eulerAngles.y);
            //if (y > 20)
            //    __instance.MovementContext.ApplyRotation(Quaternion.Lerp(__instance.MovementContext.TransformRotation, __instance.MovementContext.TransformRotation * Quaternion.Euler(0f, y, 0f), 30f * deltaTime));
            if (__instance.MovementContext._player.IsAI || !VRGlobals.inGame)
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
        [HarmonyPatch(typeof(GClass2969), "ManualLateUpdate")]
        private static bool PositionCamera(GClass2969 __instance)
        {
            if (__instance.player_0.IsAI || !VRGlobals.inGame || VRGlobals.menuOpen)
                return true;

            if (VRGlobals.emptyHands)
                VRGlobals.camRoot.transform.position = VRGlobals.emptyHands.position;
            //else
            //    VRGlobals.camRoot.transform.position = __instance.method_1(VRGlobals.camRoot.transform.position, VRGlobals.camRoot.transform.rotation, __instance.transform_0.position);
            VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.camRoot.transform.position.x, __instance.player_0.Transform.position.y + 1.5f, VRGlobals.camRoot.transform.position.z);

            //camHolder.transform.position = __instance.transform_0.position + new Vector3(Test.ex, Test.ey, Test.ez);
            return false;
        }
    }
}
