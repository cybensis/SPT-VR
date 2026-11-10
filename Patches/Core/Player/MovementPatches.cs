using EFT;
using EFT.CameraControl;
using HarmonyLib;
using TarkovVR.Source.Settings;
using UnityEngine;
using TarkovVR.Source.Controls;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class MovementPatches
    {

        private static float lastYRot = 0f;
        private static bool leftJoystickLastUsed = false;

        // --- Proper analog movement direction (see AnalogMovementDirection below) -----------------------------------
        // ON = body physically travels in the exact analog stick heading (full 360°). OFF = vanilla EFT behaviour,
        // i.e. travel snapped to the 8 directions Tarkov has walk animations for.
        public static bool analogMovementDirection = true;
        // Don't bother redirecting below this planar root-motion magnitude (avoids normalizing noise while idle/stopping).
        public static float analogMinPlanarMotion = 0.0001f;
        // While SPRINTING, cap how far off the sprint's forward heading you can steer (deg). Vanilla sprint is
        // forward-only (no sideways sprint animation), so without this the redirect lets you sprint nearly sideways.
        public static float analogSprintMaxAngle = 5f;

        
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MovementContext), "InitComponents")]
        private static void SetPlayerRotate(MovementContext __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            __instance.TrunkRotationLimit = 0;

        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Prone2StandState), "Exit")]
        private static void SetPlayerRotateOnTransition2Stand(Prone2StandState __instance)
        {

            if (!__instance.MovementContext._player.IsYourPlayer)
                return;
            __instance.MovementContext.TrunkRotationLimit = 0;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Transit2ProneState), "Enter")]
        private static void SetPlayerRotateOnTransition2Prone(Transit2ProneState __instance)
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
                return true;

            TurnBodyWithHead(__instance.MovementContext, ref deltaRotation);
           
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PronePlayerState), "Rotate")]
        private static bool SetPlayerRotateOnProneStationary(PronePlayerState __instance, ref Vector2 deltaRotation)
        {

            if (!__instance.MovementContext._player.IsYourPlayer)
                return true;

            if (VRGlobals.menuOpen || !VRGlobals.inGame)
                return false;

            TurnBodyWithHead(__instance.MovementContext, ref deltaRotation);
            
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProneMovePlayerState), "Rotate")]
        private static bool SetPlayerRotateOnProneMoving(ProneMovePlayerState __instance, ref Vector2 deltaRotation)
        {
            if (!__instance.MovementContext._player.IsYourPlayer)
                return true;

            if (VRGlobals.menuOpen || !VRGlobals.inGame)
                return false;

            TurnBodyWithHead(__instance.MovementContext, ref deltaRotation);

            return false;
        }      
        // Tarkov stands left foot forward, which feels wrong in VR: the body is turned 28 degrees so both feet are in front when
        // standing still
        private static void TurnBodyWithHead(MovementContext movementContext, ref Vector2 deltaRotation)
        {
            Vector3 bodyForward = Quaternion.Euler(0, 28, 0) * movementContext._player.gameObject.transform.forward;
            GetBodyRotation(bodyForward, ref deltaRotation);
            movementContext.Rotation = deltaRotation;
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
            float xAxis = Mathf.Abs(VRInput.LeftJoystick.axis.x);
            float yAxis = Mathf.Abs(VRInput.LeftJoystick.axis.y);

            float rightXAxis = Mathf.Abs(VRInput.RightJoystick.axis.x);
            float rightYAxis = Mathf.Abs(VRInput.RightJoystick.axis.y);
            bool leftJoystickUsed = xAxis > VRSettings.GetLeftStickSensitivity() || yAxis > VRSettings.GetLeftStickSensitivity();
            bool rightJoystickUsed = rightXAxis > 0.2f && rightXAxis > rightYAxis;

            float dotProduct = Vector3.Dot(VRGlobals.VRCam.transform.up, Vector3.up);
            float headY = (dotProduct < 0) ? (VRGlobals.VRCam.transform.eulerAngles.y - 180) : VRGlobals.VRCam.transform.eulerAngles.y;

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
                    // Right joystick used, or the head turned far off the body: the legs rotate to the head's Y axis
                    if (rightJoystickUsed || Mathf.Abs(rotDiff) > 100)
                        lastYRot = headY;
                }
                else
                    lastYRot = VRGlobals.vrPlayer.leftHandYRotation + VRGlobals.vrOffsetter.transform.eulerAngles.y;

            }
            else if (!(WeaponPatches.currentGunInteractController && WeaponPatches.currentGunInteractController.highlightingMesh) && Mathf.Abs(VRInput.RightJoystick.axis.x) > 0.20f && Mathf.Abs(VRInput.RightJoystick.axis.x) > Mathf.Abs(VRInput.RightJoystick.axis.y))
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
                VRGlobals.player.MovementContext._relativeSpeed = yAxis;
            else
                VRGlobals.player.MovementContext._relativeSpeed = xAxis;

            // Dragging a body slows you: the multiplier (1 when not dragging) comes from the corpse's ragdoll mass on grab (BodyGrab.cs)
            VRGlobals.player.MovementContext.SetCharacterMovementSpeed(
                VRGlobals.player.MovementContext._relativeSpeed * VRGlobals.player.MovementContext.MaxSpeed
                * Source.Player.Interactions.HandsInteractionController.bodyDragMoveSpeedMultiplier);
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

        // -------------------------------------------------------------------------------------------------------------
        // Proper analog (full-360°) movement direction.
        //
        // Tarkov locomotion is root-motion driven (RunStateClass.UpdatePosition -> motion = PlayerAnimatorDeltaPosition ->
        // MovementContext.ApplyMotion -> CharacterController.Move), and the walk clip is picked by binning the stick into one
        // of 8 directions (GClass2076.ConvertToMovementDirection -> SetUpDiscreteDirection, with hysteresis), so the body can
        // only travel in those 8. This rotates the horizontal part of the motion onto the true analog heading
        // (MovementContext.AbsoluteMovementDirection), keeping its magnitude (EFT's accel/inertia/speed limits stay) and its y.
        // The legs still animate in 8 directions.
        //
        // Runs before EFT's BlendMotion/ProjectMotionToSurface/ApplyGravity (this is the base MovementState.ApplyMotion,
        // not overridden by RunStateClass), so slope projection and gravity are computed from the corrected heading.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "ApplyMotion")]
        private static void AnalogMovementDirection(MovementState __instance, ref Vector3 motion)
        {
            if (!analogMovementDirection || VRGlobals.menuOpen || !VRGlobals.inGame)
                return;

            MovementContext movementContext = __instance.MovementContext;
            if (movementContext == null || movementContext._player == null || !movementContext._player.IsYourPlayer)
                return;

            // Only the normal upright locomotion state moves via the 8-way directional clips; everything else stays vanilla.
            if (!(__instance is MovePlayerState) || !movementContext.IsGrounded)
                return;

            Vector3 horizontal = new Vector3(motion.x, 0f, motion.z);
            float planarMag = horizontal.magnitude;
            if (planarMag < analogMinPlanarMotion)
                return;

            // Smoothed analog stick heading in world space (body-relative MovementDirection -> world via PlayerTransform).
            Vector3 heading = movementContext.AbsoluteMovementDirection;
            heading.y = 0f;
            if (heading.sqrMagnitude < 1e-6f)
                return;
            heading.Normalize();

            // Sprint is forward-only in vanilla; clamp the steer to a cone around the sprint's own forward heading
            // (the un-redirected root motion direction == body forward while sprinting) so you can't run sideways.
            if (movementContext.IsSprintEnabled)
            {
                Vector3 forward = horizontal / planarMag; // root-motion heading (≈ body forward during sprint)
                heading = Vector3.RotateTowards(forward, heading, analogSprintMaxAngle * Mathf.Deg2Rad, 0f);
            }

            Vector3 redirected = heading * planarMag;
            motion = new Vector3(redirected.x, motion.y, redirected.z);
        }

        // GClass1913 is a class used by the PlayerCameraController to position and rotate the camera, PlayerCameraController holds the abstract class GClass1943 which this inherits

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FirstPersonCameraState), "ManualLateUpdate")]
        private static bool PositionCamera(FirstPersonCameraState __instance)
        {
            
            if (!__instance._player.IsYourPlayer || !VRGlobals.inGame || VRGlobals.menuOpen)
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
                VRGlobals.camRoot.transform.position = new Vector3(__instance._player.Transform.position.x, __instance._player.Transform.position.y + 1.5f, __instance._player.Transform.position.z);
            //Plugin.MyLog.LogError($"playerY={__instance.Player_0.Transform.position.y:F5}, camRootY={VRGlobals.camRoot.transform.position.y:F5}, rightHandY={VRGlobals.vrPlayer.RightHand.transform.position.y:F5}");
            return false;
        }
        
    }
}
