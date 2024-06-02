using EFT;
using HarmonyLib;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class MovementPatches
    {

        private static float lastYRot = 0f;
        private static float timeSinceLastLookRot = 0f;
        // Found in Player object under CurrentState variable, and is inherited by Gclass1573
        // Can access MovementContext and probably state through player object->HideoutPlayer->MovementContext
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "Rotate")]
        private static bool SetPlayerRotate(MovementState __instance, ref Vector2 deltaRotation)
        {
            if (__instance.MovementContext.IsAI)
                return true;

            if (VRGlobals.menuOpen || !VRGlobals.inGame)
                return false;

            bool leftJoystickUsed = (Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x) > VRGlobals.MIN_JOYSTICK_AXIS_FOR_MOVEMENT || Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y) > VRGlobals.MIN_JOYSTICK_AXIS_FOR_MOVEMENT);
            bool leftJoystickLastUsed = (Mathf.Abs(SteamVR_Actions._default.LeftJoystick.lastAxis.x) > VRGlobals.MIN_JOYSTICK_AXIS_FOR_MOVEMENT || Mathf.Abs(SteamVR_Actions._default.LeftJoystick.lastAxis.y) > VRGlobals.MIN_JOYSTICK_AXIS_FOR_MOVEMENT);

            // Normally you'd stand with your left foot forward and right foot back, which doesn't feel natural in VR so rotate 28 degrees to have both feet in front when standing still
            Vector3 bodyForward = Quaternion.Euler(0, 28, 0) * __instance.MovementContext._player.gameObject.transform.forward;
            Vector3 cameraForward = Camera.main.transform.forward;
            float rotDiff = Vector3.SignedAngle(bodyForward, cameraForward, Vector3.up);


            if (leftJoystickUsed)
            {
                lastYRot = Camera.main.transform.eulerAngles.y;
            }
            else if (SteamVR_Actions._default.RightJoystick.axis.x != 0)
            {
                lastYRot = VRGlobals.camRoot.transform.eulerAngles.y;
            }
            else if (Mathf.Abs(rotDiff) > 80 && timeSinceLastLookRot > 0.25)
            {
                lastYRot += rotDiff;
                timeSinceLastLookRot = 0;
            }
            timeSinceLastLookRot += Time.deltaTime;
            if (!leftJoystickUsed && leftJoystickLastUsed)
                lastYRot -= 40;
            deltaRotation = new Vector2(deltaRotation.x + lastYRot, 0);


            // If difference between cam and body exceed something when using the right joystick, then turn the body.
            // Keep it a very tight amount before the body starts to rotate since the arms will become fucky otherwise

            //------- MovementContext has some interesting variables like trunk rotation limit, worth looking at maybe

            // ------- When body 45 degrees in either direction thats when it moves and it goes to the other extreme, e.g. exceeding 45 goes to 315

            //camRoot.transform.Rotate(0, __instance.MovementContext.Rotation.x - deltaRotation.x,0);

            __instance.MovementContext.Rotation = deltaRotation;
            //__instance.MovementContext._player.Transform.rotation = Quaternion.Euler(0, Camera.main.transform.eulerAngles.y-40, 0);


            return false;
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
            if (__instance.MovementContext._player.IsAI || VRGlobals.inGame)
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
        [HarmonyPatch(typeof(GClass1916), "ManualLateUpdate")]
        private static bool StopCamXRotation(GClass1916 __instance)
        {
            if (__instance.player_0.IsAI || !VRGlobals.inGame)
                return true;

            if (VRGlobals.emptyHands)
                VRGlobals.camRoot.transform.position = VRGlobals.emptyHands.position;
            else
                VRGlobals.camRoot.transform.position = __instance.method_1(VRGlobals.camRoot.transform.position, VRGlobals.camRoot.transform.rotation, __instance.transform_0.position);
            VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.camRoot.transform.position.x, __instance.player_0.Transform.position.y + 1.5f, VRGlobals.camRoot.transform.position.z);

            //camHolder.transform.position = __instance.transform_0.position + new Vector3(Test.ex, Test.ey, Test.ez);
            return false;
        }
    }
}
