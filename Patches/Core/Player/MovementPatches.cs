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

        // --- Proper analog movement direction (see AnalogMovementDirection below) -----------------------------------
        // Below this planar root-motion magnitude the motion isn't redirected (avoids normalizing noise while idle or stopping).
        public static float analogMinPlanarMotion = 0.0001f;
        // deg, how far off the sprint's forward heading you can steer while sprinting. Vanilla sprint is forward-only (there is
        // no sideways sprint animation), so without this cap the redirect would let you sprint nearly sideways.
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
        // MovementState is the Player's CurrentState (GClass1573 inherits it).
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "Rotate")]
        private static bool SetPlayerRotate(MovementState __instance, ref Vector2 deltaRotation)
        {
            if (!__instance.MovementContext._player.IsYourPlayer)
                return true;

            if (VRGlobals.menuOpen || !VRGlobals.inGame)
                return true;

            // On a mounted gun the hands turn the gun, and Tarkov turns the player with it (see MountedGun)
            if (Source.Player.Interactions.MountedGun.DriveRotation(__instance))
                return false;

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
        // Tarkov stands left foot forward, which feels wrong in VR. The body is turned 28 degrees so both feet are in front when
        // standing still.
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


            if (yAxis > xAxis)
                VRGlobals.player.MovementContext._relativeSpeed = yAxis;
            else
                VRGlobals.player.MovementContext._relativeSpeed = xAxis;

            // Dragging a body slows you: the multiplier is BodyGrab's bodyDragSpeedMultiplier while dragging, 1 otherwise
            VRGlobals.player.MovementContext.SetCharacterMovementSpeed(
                VRGlobals.player.MovementContext._relativeSpeed * VRGlobals.player.MovementContext.MaxSpeed
                * Source.Player.Interactions.HandsInteractionController.bodyDragMoveSpeedMultiplier);
        }


        // GClass1854 inherits MovementState, and its ProcessUpperbodyRotation runs while the player moves. This makes the body turn the
        // same way when not moving; otherwise the camera could turn faster than the body.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "ProcessUpperbodyRotation")]
        private static bool FixBodyRotation(MovementState __instance, float deltaTime)
        {
            if (!__instance.MovementContext._player.IsYourPlayer || !VRGlobals.inGame)
                return true;

            __instance.UpdateRotationSpeed(deltaTime);
            float float_3 = __instance.MovementContext.TransformRotation.eulerAngles.y;
            float f = Mathf.DeltaAngle(__instance.MovementContext.Yaw, float_3);
            float num = Mathf.InverseLerp(10f, 45f, Mathf.Abs(f)) + 1f;
            float_3 = Mathf.LerpAngle(float_3, __instance.MovementContext.Yaw, EFTHardSettings.Instance.TRANSFORM_ROTATION_LERP_SPEED * deltaTime * num);
            __instance.MovementContext.ApplyRotation(Quaternion.AngleAxis(float_3, Vector3.up) * __instance.MovementContext.AnimatorDeltaRotation);
            return false;
             
        }

        // -------------------------------------------------------------------------------------------------------------
        // Analog (full 360°) movement direction.
        //
        // Tarkov locomotion is driven by root motion (RunStateClass.UpdatePosition -> motion = PlayerAnimatorDeltaPosition ->
        // MovementContext.ApplyMotion -> CharacterController.Move). The walk clip is picked by binning the stick into one of
        // 8 directions (GClass2076.ConvertToMovementDirection -> SetUpDiscreteDirection, with hysteresis), so the body can only
        // travel in those 8. This rotates the horizontal part of the motion onto the true stick heading
        // (MovementContext.AbsoluteMovementDirection) and keeps its length and its y, so EFT's acceleration, inertia and speed
        // limits still apply. The legs still animate in 8 directions.
        //
        // This is the base MovementState.ApplyMotion (RunStateClass doesn't override it), so it runs before EFT's
        // BlendMotion/ProjectMotionToSurface/ApplyGravity, and slope projection and gravity use the corrected heading.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "ApplyMotion")]
        private static void AnalogMovementDirection(MovementState __instance, ref Vector3 motion)
        {
            if (VRGlobals.menuOpen || !VRGlobals.inGame)
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

            // Vanilla sprint is forward-only, so the steer is clamped to a cone around the sprint's own heading (the root motion
            // before the redirect, which is the body's forward while sprinting). This stops you running sideways.
            if (movementContext.IsSprintEnabled)
            {
                Vector3 forward = horizontal / planarMag; // root-motion heading (≈ body forward during sprint)
                heading = Vector3.RotateTowards(forward, heading, analogSprintMaxAngle * Mathf.Deg2Rad, 0f);
            }

            Vector3 redirected = heading * planarMag;
            motion = new Vector3(redirected.x, motion.y, redirected.z);
        }

        // GClass1913 is used by the PlayerCameraController to position and rotate the camera. PlayerCameraController holds the abstract
        // class GClass1943, which this inherits.

        // FirstPersonCameraState.ManualLateUpdate prefix stage (FramePipeline)
        internal static bool PositionCamera(FirstPersonCameraState __instance)
        {
            
            if (!__instance._player.IsYourPlayer || !VRGlobals.inGame || VRGlobals.menuOpen)
                return true;

            // While using meds or eating, this code positions the upper body. emptyHands is set then, but the current gun interaction
            // controller is disabled.
            if (VRGlobals.emptyHands && VRGlobals.player.HandsIsEmpty)
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            else if (VRGlobals.emptyHands && (!WeaponPatches.currentGunInteractController || !WeaponPatches.currentGunInteractController.enabled)) {
                if (!WeaponPatches.currentGunInteractController || WeaponPatches.currentGunInteractController.transform.parent != VRGlobals.emptyHands)
                    VRGlobals.ikManager.MatchLegsToArms();
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
                
            }
            else if (!VRGlobals.emptyHands)
                VRGlobals.camRoot.transform.position = new Vector3(__instance._player.Transform.position.x, __instance._player.Transform.position.y + 1.5f, __instance._player.Transform.position.z);
            return false;
        }
        
    }
}
