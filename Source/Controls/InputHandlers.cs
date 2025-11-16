using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using EFT.UI;
using RootMotion.FinalIK;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.ModSupport;
//using TarkovVR.ModSupport.EFTApi;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Player.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using static EFT.Player;

namespace TarkovVR.Source.Controls
{
    public static class InputHandlers
    {
        public interface IInputHandler
        {
            void UpdateCommand(ref ECommand command);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class JumpInputHandler : IInputHandler
        {
            private bool isVaulting = false;
            private float timeHeld = 0f;
            private static float TIME_HELD_FOR_VAULT = 0.3f;
            public void UpdateCommand(ref ECommand command)
            {
                // Safety checks
                if (VRGlobals.vrPlayer == null || SteamVR_Actions._default?.RightJoystick == null)
                    return;

                float yAxis = SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y;

                if (!VRGlobals.vrPlayer.blockJump && yAxis > 0.925f)
                {
                    timeHeld += Time.deltaTime;
                    if (timeHeld >= TIME_HELD_FOR_VAULT)
                    {
                        command = ECommand.Vaulting;
                        isVaulting = true;
                    }
                }
                else
                {
                    if (VRGlobals.player && VRGlobals.player.IsVaultingPressed)
                    {
                        isVaulting = false;
                        command = ECommand.VaultingEnd;
                    }
                    else if (timeHeld > 0.05f && timeHeld < TIME_HELD_FOR_VAULT)
                    {
                        command = ECommand.Jump;
                    }
                    timeHeld = 0f;
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class CrouchHandler : IInputHandler
        {
            private static float MAX_CROUCH_HEIGHT_DIFF = 0.4f;

            public void UpdateCommand(ref ECommand command)
            {
                // Exit early if required objects are not initialized or crouching is blocked
                if (VRGlobals.vrPlayer == null || VRGlobals.player == null || VRGlobals.vrPlayer.blockCrouch)
                    return;

                // Ensure movement context and character controller exist
                var movementContext = VRGlobals.player.MovementContext;
                if (movementContext == null || movementContext.CharacterController == null)
                    return;

                float joystickY = SteamVR_Actions._default.RightJoystick.axis.y;

                // When the right joystick is pulled down, lower player pose (crouch)
                if (joystickY < -0.8f)
                {
                    float poseDelta = -1.5f * Time.deltaTime;
                    VRGlobals.player.ChangePose(poseDelta);
                    //float poseLevel = VRGlobals.player.MovementContext.PoseLevel + poseDelta;
                    //float poseLevelResult = Mathf.Clamp(poseLevel, 0f, VRGlobals.player.Physical.MaxPoseLevel);
                    //float characterControllerHeight = Mathf.Lerp(1.2f, 1.6f, poseLevelResult);
                    VRGlobals.vrPlayer.crouchHeightDiff = 1.6f - movementContext.CharacterController.height;
                }

                // When the joystick is pushed up and player has crouched, raise pose (stand)
                if (VRGlobals.vrPlayer.crouchHeightDiff > 0.01f && joystickY > 0.8f)
                {
                    float poseDelta = 0.05f;
                    VRGlobals.player.ChangePose(poseDelta);
                    //float poseLevel = VRGlobals.player.MovementContext.PoseLevel + poseDelta;
                    //float poseLevelResult = Mathf.Clamp(poseLevel, 0f, VRGlobals.player.Physical.MaxPoseLevel);
                    //float characterControllerHeight = Mathf.Lerp(1.2f, 1.6f, poseLevelResult);
                    VRGlobals.vrPlayer.crouchHeightDiff = Mathf.Clamp(1.61f - movementContext.CharacterController.height, 0.01f, 1f);
                    //Plugin.MyLog.LogWarning(VRGlobals.player.MovementContext.PoseLevel + "   |  " + poseLevel + "   |  " + poseLevelResult + "  |   " + characterControllerHeight + "   |   " + VRGlobals.vrPlayer.crouchHeightDiff);
                }

                // Really shit way to do this, but this prevents a jump happening immediately after changing the crouch height diff by 
                // keeping it at 0.01 until the right joystick Y axis goes down
                else if (VRGlobals.vrPlayer.crouchHeightDiff == 0.01f && joystickY < 0.5f)
                {
                    VRGlobals.vrPlayer.crouchHeightDiff = 0f;
                }
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class ProneInputHandler : IInputHandler
        {
            float timeHeld = 0f;
            private static float MIN_TIME_BETWEEN_PRESSES = 1f;
            private static float MAX_CROUCH_HEIGHT_DIFF = 0.4f;
            float timeSinceLastPress = 0;
            private static bool releasedPullAfterFullCrouch = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (VRGlobals.player is HideoutPlayer || VRGlobals.blockRightJoystick)
                    return;

                if (SteamVR_Actions._default.RightJoystick.axis.y > -0.8 && ((float)Math.Round(VRGlobals.vrPlayer.crouchHeightDiff, 1) == MAX_CROUCH_HEIGHT_DIFF))
                    releasedPullAfterFullCrouch = true;
                else if (releasedPullAfterFullCrouch && (float)Math.Round(VRGlobals.vrPlayer.crouchHeightDiff, 1) != MAX_CROUCH_HEIGHT_DIFF)
                    releasedPullAfterFullCrouch = false;

                if (SteamVR_Actions._default.RightJoystick.axis.y < -0.8 && releasedPullAfterFullCrouch && Time.time - timeSinceLastPress > MIN_TIME_BETWEEN_PRESSES)
                {
                    releasedPullAfterFullCrouch = false;
                    command = ECommand.ToggleProne;
                    timeSinceLastPress = Time.time;
                    if (!VRGlobals.player.IsInPronePose && VRGlobals.player.MovementContext.CanProne)
                        VRGlobals.vrPlayer.crouchHeightDiff = 1.3f;
                    else if (VRGlobals.player.IsInPronePose && VRGlobals.player.MovementContext.CanStandAt(VRGlobals.player.MovementContext.PoseLevel) && VRGlobals.player.MovementContext.CanSit)
                        VRGlobals.vrPlayer.crouchHeightDiff = 0f;

                }
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        /*
        public class ResetHeightHandler : IInputHandler
        {
            float timeHeld = 0f;
            public static float HEIGH_RESET_TIME_THRESHOLD = 0.5f;
            public static float ARM_SCALING_BASE_PLAYER_HEIGHT = 1.4147f;
            public static float FOREARM_BASE_LENGTH = 1.15f;
            private Transform leftForearm;
            private Transform rightForearm;
            private bool heightReset = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (!leftForearm || !rightForearm)
                    return;

                if (SteamVR_Actions._default.ClickRightJoystick.state && !heightReset)
                {
                    timeHeld += Time.deltaTime;
                    if (timeHeld > HEIGH_RESET_TIME_THRESHOLD)
                    {
                        VRGlobals.vrPlayer.initPos = Camera.main.transform.localPosition;
                        float heightDiffScale = (VRGlobals.vrPlayer.initPos.y / ARM_SCALING_BASE_PLAYER_HEIGHT);
                        heightDiffScale += (1 - heightDiffScale) * 0.5f;
                        Vector3 forearmLength = new Vector3(heightDiffScale, 1, 1) * FOREARM_BASE_LENGTH;


                        Transform leftForearmCounterObj = leftForearm.GetChild(0);
                        Transform rightForearmCounterObj = rightForearm.GetChild(0);
                        // Calculate the inverse scale for forearm1
                        Vector3 forearmInverseScale = new Vector3(
                            1 / forearmLength.x,
                            1 / forearmLength.y,
                            1 / forearmLength.z
                        );

                        // Apply the inverse scale to forearm1
                        leftForearmCounterObj.localScale = forearmInverseScale;
                        rightForearmCounterObj.localScale = forearmInverseScale;

                        leftForearm.transform.localScale = forearmLength;
                        rightForearm.transform.localScale = forearmLength;

                        heightReset = true;
                    }
                }
                else { 
                    timeHeld = 0f;
                    heightReset = false;
                }
            }

            public void SetRightArmTransform(Transform rightForearm)
            {
                this.rightForearm = rightForearm;
            }
            public void SetLeftArmTransform(Transform leftForearm)
            {
                this.leftForearm = leftForearm;
            }
        }
        */
        
        public class ResetHeightHandler : IInputHandler
        {
            float timeHeld = 0f;
            public static float HEIGH_RESET_TIME_THRESHOLD = 0.5f;
            private bool heightReset = false;
            public void UpdateCommand(ref ECommand command)
            {

                if (SteamVR_Actions._default.ClickRightJoystick.state && !heightReset)
                {
                    timeHeld += Time.deltaTime;
                    if (timeHeld > HEIGH_RESET_TIME_THRESHOLD)
                    {
                        VRGlobals.vrPlayer.initPos = VRGlobals.VRCam.transform.localPosition;

                        heightReset = true;
                    }
                }
                else
                {
                    timeHeld = 0f;
                    heightReset = false;
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class SprintInputHandler : IInputHandler
        {
            private bool isSprinting = false;

            public void UpdateCommand(ref ECommand command)
            {
                if (SteamVR_Actions._default.ClickLeftJoystick.GetStateDown(SteamVR_Input_Sources.Any))
                {
                    if (!isSprinting)
                        command = ECommand.ToggleSprinting;
                    else
                        command = ECommand.EndSprinting;

                    isSprinting = !isSprinting;
                    if (VRGlobals.player.IsInPronePose)
                        command = ECommand.ToggleProne;
                    VRGlobals.vrPlayer.crouchHeightDiff = 0;
                }
                else if (isSprinting && SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).y < VRSettings.GetLeftStickSensitivity())
                {
                    command = ECommand.EndSprinting;
                    isSprinting = false;
                }

            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class ReloadInputHandler : IInputHandler
        {
            private bool toggleReload = false; 
            public void UpdateCommand(ref ECommand command)
            {
                bool reloadButtonClick = (VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.ButtonY.GetStateDown(SteamVR_Input_Sources.Any)  : SteamVR_Actions._default.ButtonB.GetStateDown(SteamVR_Input_Sources.Any));
                if (reloadButtonClick) { 
                    command = ECommand.ReloadWeapon;
                    toggleReload = false;
                }
                if (toggleReload) {
                    command = ECommand.ReloadWeapon;
                    toggleReload = false;
                }
            }

            public void TriggerReload()
            {
                toggleReload = true;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class AimHandler : IInputHandler
        {
            // Hysteresis thresholds to prevent spam toggling
            private const float SCOPE_ENTER_ANGLE = 20f;
            private const float SCOPE_EXIT_ANGLE = 30f;
            private const float IRONS_ENTER_ANGLE_TO = 15f;
            private const float IRONS_EXIT_ANGLE_TO = 30f;
            private const float IRONS_ENTER_ANGLE_FROM = 17.5f;
            private const float IRONS_EXIT_ANGLE_FROM = 32.5f;

            // Distance check to prevent scope activation when too far
            private const float MAX_SCOPE_DISTANCE = 0.45f;

            // Cooldown to prevent rapid toggling
            private const float TOGGLE_COOLDOWN = 0.2f;
            private float lastToggleTime = 0f;

            public void UpdateCommand(ref ECommand command)
            {
                if (VRGlobals.firearmController == null && !WeaponPatches.rangeFinder)
                    return;

                float primaryHandTriggerAmount = VRSettings.GetLeftHandedMode()
                    ? SteamVR_Actions._default.LeftTrigger.axis
                    : SteamVR_Actions._default.RightTrigger.axis;

                // Handle rangefinder special case
                if (WeaponPatches.rangeFinder)
                {
                    HandleRangefinder(primaryHandTriggerAmount);
                    return;
                }

                bool isAiming = VRGlobals.firearmController.IsAiming;
                float currentTime = Time.time;

                // Prevent rapid toggling
                if (currentTime - lastToggleTime < TOGGLE_COOLDOWN)
                    return;

                // Handle scope or iron sight aiming
                if (HasValidScope())
                {
                    HandleScopeAiming(ref command, isAiming, currentTime);
                }
                else
                {
                    HandleIronSightAiming(ref command, isAiming, currentTime);
                }

                // Adjust player rotation when entering aim mode while supporting weapon
                if (command == ECommand.ToggleAlternativeShooting && VRGlobals.vrPlayer.isSupporting)
                {
                    VRGlobals.player.Transform.rotation = Quaternion.Euler(0, Camera.main.transform.eulerAngles.y, 0);
                }
            }

            private void HandleRangefinder(float triggerAmount)
            {
                const float TRIGGER_THRESHOLD = 0.5f;

                if (triggerAmount > TRIGGER_THRESHOLD && !WeaponPatches.rangeFinder.IsAiming)
                {
                    WeaponPatches.rangeFinder.ToggleAim();
                    VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.13f, -0.1711f, -0.24f);
                    VRGlobals.weaponHolder.transform.localEulerAngles = new Vector3(12, 308, 90);
                }
                else if (triggerAmount < TRIGGER_THRESHOLD && WeaponPatches.rangeFinder.IsAiming)
                {
                    WeaponPatches.rangeFinder.ToggleAim();
                    VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(37, 267, 55);
                    VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.23f, 0.0689f, -0.23f);
                }
            }

            private bool HasValidScope()
            {
                return VRGlobals.scopes != null && VRGlobals.scopes.Count > 0;
            }

            private void HandleScopeAiming(ref ECommand command, bool isAiming, float currentTime)
            {
                Transform activeScope = VRGlobals.scopes.FirstOrDefault(s => s != null && s.parent != null);
                if (activeScope == null)
                    return;

                float distanceAdd = 0f;
                // Check if the deep child exists and is named "mod_aim_camera_000"
                if (activeScope.childCount > 1)
                {
                    Transform child1 = activeScope.GetChild(1);
                    if (child1 != null && child1.childCount > 0)
                    {
                        Transform child1_0 = child1.GetChild(0);
                        if (child1_0 != null && child1_0.childCount > 2)
                        {
                            Transform child1_0_2 = child1_0.Find("mod_aim_camera_000");
                            //Plugin.MyLog.LogError("Found deep scope child: " + (child1_0_2 != null ? child1_0_2.name : "null"));
                            if (child1_0_2 != null && child1_0_2.name == "mod_aim_camera_000")
                            {
                                activeScope = child1_0_2;
                            }
                        }
                    }
                }

                Vector3 scopeTargetPosition = activeScope.position + (activeScope.forward * -0.25f);
                Vector3 headPosition = VRGlobals.camHolder.transform.position;
                Vector3 directionToScope = scopeTargetPosition - headPosition;
                float distanceToScope = directionToScope.magnitude;
                directionToScope.Normalize();
                float angleToScope = Vector3.Angle(activeScope.forward * -1f, directionToScope);
                float angleFromScope = Vector3.Angle(VRGlobals.camHolder.transform.forward, directionToScope);

                // Check if within distance and angle thresholds
                if (!isAiming &&
                    distanceToScope <= MAX_SCOPE_DISTANCE &&
                    angleToScope <= SCOPE_ENTER_ANGLE &&
                    angleFromScope <= SCOPE_ENTER_ANGLE)
                {
                    command = ECommand.ToggleAlternativeShooting;
                    lastToggleTime = currentTime;
                }
                else if (isAiming && (angleToScope > SCOPE_EXIT_ANGLE ||
                                      angleFromScope > SCOPE_EXIT_ANGLE ||
                                      distanceToScope > MAX_SCOPE_DISTANCE))
                {
                    command = ECommand.EndAlternativeShooting;
                    VRPlayerManager.smoothingFactor = 50f;
                    lastToggleTime = currentTime;
                }
            }

            private void HandleIronSightAiming(ref ECommand command, bool isAiming, float currentTime)
            {
                Vector3 direction = VRGlobals.vrPlayer.RightHand.transform.right * -1;
                Vector3 directionToGun = (VRGlobals.vrPlayer.RightHand.transform.position + direction) - VRGlobals.camHolder.transform.position;
                float distanceToGun = directionToGun.magnitude;
                directionToGun = directionToGun.normalized;               
                float angleToScope = Vector3.Angle(direction, directionToGun);
                float angleFromScope = Vector3.Angle(VRGlobals.camHolder.transform.forward, directionToGun);

                // Use different thresholds based on current state (hysteresis)
                if (!isAiming && angleToScope <= IRONS_ENTER_ANGLE_TO && angleFromScope <= IRONS_ENTER_ANGLE_FROM)
                {
                    command = ECommand.ToggleAlternativeShooting;
                    lastToggleTime = currentTime;
                }
                else if (isAiming && (angleToScope > IRONS_EXIT_ANGLE_TO || angleFromScope > IRONS_EXIT_ANGLE_FROM))
                {
                    command = ECommand.EndAlternativeShooting;
                    VRPlayerManager.smoothingFactor = 50f;
                    lastToggleTime = currentTime;
                }
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class FireHandler : IInputHandler
        {
            private bool isShooting = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (WeaponPatches.grenadeEquipped)
                    return;

                float primaryHandTriggerAmount = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftTrigger.axis : SteamVR_Actions._default.RightTrigger.axis;


                if (!isShooting && primaryHandTriggerAmount > 0.5f)
                {
                    command = ECommand.ToggleShooting;
                    isShooting = true;
                }
                else if (isShooting && primaryHandTriggerAmount < 0.5f)
                {
                    command = ECommand.EndShooting;
                    isShooting = false;
                }
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class ScrollHandler : IInputHandler
        {
            private bool isScrolling = false;
            private bool scrolledLastFrame = false;
            // When the weapon/item/door/etc interaction menu is open, in right handed mode the player can still rotate left or right since
            // the menu is just up and down, but in left handed mode it makes less sense to be able to only walk left or right so we only
            // care if the right joystick has been disabled.
            //if ((!VRSettings.GetLeftHandedMode() && VRGlobals.blockRightJoystick) || !VRGlobals.vrPlayer.interactMenuOpen)
            public void UpdateCommand(ref ECommand command)
            {
                // Early-out if vrPlayer is null
                if (VRGlobals.vrPlayer == null || !VRGlobals.vrPlayer.interactMenuOpen)
                    return;

                float primaryHandScrollAxis = 0f;

                if (VRSettings.GetLeftHandedMode() &&
                    WeaponPatches.currentGunInteractController != null &&
                    WeaponPatches.currentGunInteractController.highlightingMesh)
                {
                    primaryHandScrollAxis = SteamVR_Actions._default.LeftJoystick.axis.y;
                }
                else
                {
                    primaryHandScrollAxis = SteamVR_Actions._default.RightJoystick.axis.y;
                }

                if (!isScrolling && primaryHandScrollAxis > 0.5f)
                {
                    command = ECommand.ScrollNext;
                    isScrolling = true;
                }
                else if (!isScrolling && primaryHandScrollAxis < -0.5f)
                {
                    command = ECommand.ScrollPrevious;
                    isScrolling = true;
                }
                else if (primaryHandScrollAxis > -0.5f && primaryHandScrollAxis < 0.5f)
                {
                    isScrolling = false;
                }
            }

        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class BreathingHandler : IInputHandler
        {
            private bool isHoldingBreath = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (!VRGlobals.firearmController)
                    return;
                float secondaryHandTriggerAmount = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.RightTrigger.axis : SteamVR_Actions._default.LeftTrigger.axis;
                bool isAiming = VRGlobals.firearmController.IsAiming;
                if (!isHoldingBreath && isAiming && secondaryHandTriggerAmount > 0.5f)
                {
                    command = ECommand.ToggleBreathing;
                    isHoldingBreath = true;
                    if (VRGlobals.scopeSensitivity * (VRSettings.GetScopeSensitivity() * 10) > 0)
                        VRPlayerManager.smoothingFactor = VRGlobals.scopeSensitivity * (VRSettings.GetScopeSensitivity() * 10);
                }
                else if (isHoldingBreath && (secondaryHandTriggerAmount < 0.5f || !isAiming))
                {
                    command = ECommand.EndBreathing;
                    isHoldingBreath = false;
                    VRPlayerManager.smoothingFactor = 50f;
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class SelectWeaponHandler : IInputHandler
        {
            private bool swapWeapon = false;
            private bool swapPrimaryWeapon = false;
            private bool swapSecondaryWeapon = false;
            private bool swapSidearm = false;
            private bool swapToMelee = false;

            private float UBGLButtonDownTime = 0f;
            private bool UBGLButtonWasDown = false;
            private bool commandExecuted = false;
            private const float HOLD_DURATION = 0f;
          
            public void UpdateCommand(ref ECommand command)
            {
                if (!VRGlobals.player)
                    return;
                SteamVR_Action_Boolean UBGLButtonAction = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.ButtonA : SteamVR_Actions._default.ButtonX;
                float secondaryHandTriggerAmount = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.RightTrigger.axis : SteamVR_Actions._default.LeftTrigger.axis;
                bool cancelGrenadeButtonClick = (VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.ButtonY.GetStateDown(SteamVR_Input_Sources.Any) : SteamVR_Actions._default.ButtonB.GetStateDown(SteamVR_Input_Sources.Any));
                bool UBGLButtonDown = UBGLButtonAction.state;

                if ((swapPrimaryWeapon) || swapWeapon || (WeaponPatches.grenadeEquipped && cancelGrenadeButtonClick))
                {
                    if (VRGlobals.player.ActiveSlot == null || WeaponPatches.rangeFinder)
                        // If the first weapon slot is null then attempt select secondary
                        if (VRGlobals.player.Equipment.CachedSlots[0].ContainedItem != null)
                            command = ECommand.SelectFirstPrimaryWeapon;
                        else
                            command = ECommand.SelectSecondPrimaryWeapon;

                    else if (VRGlobals.player.ActiveSlot.ID == "FirstPrimaryWeapon")
                        if (WeaponPatches.grenadeEquipped)
                            command = ECommand.SelectFirstPrimaryWeapon;
                        else
                            command = ECommand.SelectSecondPrimaryWeapon;
                    else
                        if (WeaponPatches.grenadeEquipped)
                            command = ECommand.SelectSecondPrimaryWeapon;
                        else
                            command = ECommand.SelectFirstPrimaryWeapon;

                    swapPrimaryWeapon = false;
                    swapWeapon = false;
                    swapSecondaryWeapon = false;
                }
                if (swapSidearm) {
                    command = ECommand.SelectSecondaryWeapon;
                    swapSidearm = false;
                }
                if (swapSecondaryWeapon)
                {
                    command = ECommand.SelectSecondPrimaryWeapon;
                    swapSecondaryWeapon = false;
                }
                if (swapToMelee) {
                    command = ECommand.SelectKnife;
                    swapToMelee = false;
                }

                //Handle UBGL weapon swapping
                if (secondaryHandTriggerAmount >= 0.5f)
                {

                    // Button just pressed
                    if (UBGLButtonDown && !UBGLButtonWasDown)
                    {
                        UBGLButtonDownTime = Time.time;
                        commandExecuted = false;
                    }

                    // Button is being held
                    if (UBGLButtonDown)
                    {
                        float holdTime = Time.time - UBGLButtonDownTime;

                        // Execute command after holding for HOLD_DURATION
                        if (holdTime >= HOLD_DURATION && !commandExecuted)
                        {
                            // Only execute if we have a valid active weapon
                            if (VRGlobals.player.ActiveSlot != null && !WeaponPatches.rangeFinder)
                            {
                                if (VRGlobals.player.ActiveSlot.ID == "FirstPrimaryWeapon")
                                    command = ECommand.SelectFirstPrimaryWeapon;
                                else
                                    command = ECommand.SelectSecondPrimaryWeapon;
                            }

                            commandExecuted = true;
                        }
                    }
                }
                else
                {
                    if (UBGLButtonWasDown)
                    {
                        commandExecuted = false;
                    }
                }

                UBGLButtonWasDown = UBGLButtonDown;
            }

            public void TriggerSwapOtherPrimary() {
                swapWeapon = true;
            }
            public void TriggerSwapSidearm()
            {
                swapSidearm = true;
            }
            public void TriggerSwapPrimaryWeapon() {
                swapPrimaryWeapon = true;
            }
            public void TriggerSwapSecondaryWeapon() {
                swapSecondaryWeapon = true;
            }
            public void TriggerSwapToMelee()
            {
                swapToMelee = true;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class ScopeZoomHandler : IInputHandler
        {
            private bool swapZooms = false;

            public void UpdateCommand(ref ECommand command)
            {
                if (!VRGlobals.firearmController)
                    return;
                if (VRGlobals.firearmController.IsAiming && swapZooms) {
                    command = ECommand.ChangeScopeMagnification;
                    swapZooms = false;
                }
            }
            public void TriggerSwapZooms()
            {
                swapZooms = true;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class CheckAmmoHandler : IInputHandler
        {
            private bool checkAmmo = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (checkAmmo)
                {
                    command = ECommand.CheckAmmo;
                    checkAmmo = false;
                }
                
            }
            public void TriggerCheckAmmo()
            {
                checkAmmo = true;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class FireModeHandler : IInputHandler
        {
            private bool changeFireMode = false;

            public void UpdateCommand(ref ECommand command)
            {
                bool changeFireModeButtonClick = VRSettings.GetLeftHandedMode()
                    ? SteamVR_Actions._default.ButtonX.GetStateDown(SteamVR_Input_Sources.Any)
                    : SteamVR_Actions._default.ButtonA.GetStateDown(SteamVR_Input_Sources.Any);

                if (changeFireMode)
                {
                    command = ECommand.ChangeWeaponMode;
                    changeFireMode = false;
                    return;
                }

                if (VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting && !VRGlobals.vrPlayer.interactMenuOpen && changeFireModeButtonClick && WeaponPatches.currentGunInteractController.GetFireModeSwitch() != null)
                {
                    command = ECommand.ChangeWeaponMode;
                }
            }
            public void TriggerChangeFireMode()
            {
                changeFireMode = true;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class CheckChamberHandler : IInputHandler
        {
            private bool checkChamber = false;
            public void UpdateCommand(ref ECommand command)
            {

                if (checkChamber)
                {
                    command = ECommand.CheckChamber;
                    checkChamber = false;
                    WeaponPatches.currentGunInteractController.hasExaminedAfterMalfunction = false;
                }
            }
            public void TriggerCheckChamber()
            {
                checkChamber = true;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class QuickSlotHandler : IInputHandler
        {
            private EBoundItem currentSlot;
            // The user set quick slots start at 4, 1-3 are weapon slots
            private Dictionary<EBoundItem, ECommand> quickSlotCommands = new Dictionary<EBoundItem, ECommand>();
            public QuickSlotHandler() {
                quickSlotCommands.Add(EBoundItem.Item4, ECommand.SelectFastSlot4);
                quickSlotCommands.Add(EBoundItem.Item5, ECommand.SelectFastSlot5);
                quickSlotCommands.Add(EBoundItem.Item6, ECommand.SelectFastSlot6);
                quickSlotCommands.Add(EBoundItem.Item7, ECommand.SelectFastSlot7);
                quickSlotCommands.Add(EBoundItem.Item8, ECommand.SelectFastSlot8);
                quickSlotCommands.Add(EBoundItem.Item9, ECommand.SelectFastSlot9);
                quickSlotCommands.Add(EBoundItem.Item10, ECommand.SelectFastSlot0);
            }
            public void UpdateCommand(ref ECommand command)
            {

                if (currentSlot != 0)
                {
                    command = quickSlotCommands[currentSlot];
                    currentSlot = 0;
                }
            }
            public void TriggerUseQuickSlot(EBoundItem slot)
            {
                currentSlot = slot;
            }
            public EBoundItem GetQuickUseSlot()
            {
                return currentSlot;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class InteractHandler : IInputHandler
        {
            public void UpdateCommand(ref ECommand command)
            {
                bool interactButtonUp = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.ButtonX.GetStateDown(SteamVR_Input_Sources.Any) : SteamVR_Actions._default.ButtonA.GetStateDown(SteamVR_Input_Sources.Any);
                bool interactButtonDown = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.ButtonX.GetStateUp(SteamVR_Input_Sources.Any) : SteamVR_Actions._default.ButtonA.GetStateUp(SteamVR_Input_Sources.Any);

                if (interactButtonDown)
                    command = ECommand.BeginInteracting;
                else if (interactButtonUp)
                    command = ECommand.EndInteracting;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class InventoryHandler : IInputHandler
        {
            public void UpdateCommand(ref ECommand command)
            {
                bool inventoryButtonUp = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.ButtonA.GetStateUp(SteamVR_Input_Sources.Any) : SteamVR_Actions._default.ButtonX.GetStateUp(SteamVR_Input_Sources.Any);
                float secondaryHandTriggerAmount = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.RightTrigger.axis : SteamVR_Actions._default.LeftTrigger.axis;

                if (inventoryButtonUp && secondaryHandTriggerAmount < 0.1f)
                    command = ECommand.ToggleInventory;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class GrenadeHandler : IInputHandler
        {
            private bool grenadePinPulled = false;
            private bool shootingToggled = false;
            private bool releaseGrenade = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (!WeaponPatches.grenadeEquipped)
                    return;
                float primaryHandTriggerAmount = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.LeftTrigger.axis : SteamVR_Actions._default.RightTrigger.axis;


                if (!shootingToggled && primaryHandTriggerAmount > 0.5)
                {
                    command = ECommand.ToggleShooting;
                    shootingToggled = true;
                }
                else if (shootingToggled && !grenadePinPulled) { 
                    command = ECommand.TryHighThrow;
                    grenadePinPulled = true;
                    // Change the weapon holder position for grenade after the pin pulling animation
                    PreloaderUI.Instance.WaitSeconds(1.25f, delegate
                    {
                        if (VRGlobals.player.HandsController as EFT.Player.GrenadeHandsController && (VRGlobals.player.HandsController as EFT.Player.GrenadeHandsController).WaitingForHighThrow) {
                            if (VRGlobals.player.HandsController.HandsHierarchy.name != "weapon_grenade_rdg2.generated(Clone)") {                               
                                //InitVRPatches.rightPointerFinger.enabled = true;
                                //VRGlobals.handsInteractionController.grenadeLaser.SetActive(true);
                                VRGlobals.weaponHolder.transform.localPosition = new Vector3(-0.1f, -0.43f, -0.25f);
                            }
                            if (VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_m7920.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_rgo.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_rgn.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_m18.generated(Clone)") { 
                                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(30, 273, 116);
                                VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.05f, -0.43f, -0.15f);
                            }
                            WeaponPatches.pinPulled = true;
                            // Do this to recalculate hand position
                            if (WeaponPatches.currentGunInteractController != null)
                                WeaponPatches.currentGunInteractController.framesAfterEnabled = 0;
                        }
                    });
                }
                else if (shootingToggled && grenadePinPulled && primaryHandTriggerAmount < 0.5) {
                    command = ECommand.EndShooting;
                    shootingToggled = false;
                }
                else if (!shootingToggled && grenadePinPulled)
                {
                    //Trigger throwing grenade, check if pin is actually pulled before initiatiating
                    if (VRGlobals.player.HandsController is BaseGrenadeHandsController grenadeController)
                    {
                        grenadeController.method_9(
                            null, // throwPosition
                            0f,   // timeSinceSafetyLevelRemoved
                            1f,   // lowHighThrow
                            Vector3.zero, // direction
                            1f,   // forcePower
                            false, // lowThrow
                            true   // withVelocity
                        );
                    }

                    //This command isn't needed as I finish the command by triggering the throw animation in method_9
                    //command = ECommand.FinishHighThrow;
                    grenadePinPulled = false;
                    
                }

            }
        }

        public class TacticalHandler : IInputHandler
        {
            private float tacticalButtonDownTime = 0f;
            private bool tacticalButtonWasDown = false;
            private bool commandExecuted = false;
            private const float HOLD_DURATION = 0.3f;

            public void UpdateCommand(ref ECommand command)
            {
                SteamVR_Action_Boolean secondaryGripState = (VRSettings.GetLeftHandedMode()) ? SteamVR_Actions._default.RightGrip : SteamVR_Actions._default.LeftGrip;
                SteamVR_Action_Boolean tacticalButtonAction = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.ButtonB : SteamVR_Actions._default.ButtonY;

                bool tacticalButtonDown = tacticalButtonAction.state && secondaryGripState.state;

                // Button just pressed
                if (tacticalButtonDown && !tacticalButtonWasDown)
                {
                    tacticalButtonDownTime = Time.time;
                    commandExecuted = false;
                }

                // Button is being held
                if (tacticalButtonDown)
                {
                    float holdTime = Time.time - tacticalButtonDownTime;

                    // Execute next device after holding for HOLD_DURATION
                    if (holdTime >= HOLD_DURATION && !commandExecuted)
                    {
                        command = ECommand.NextTacticalDevice;
                        commandExecuted = true;
                    }
                }

                // Button just released
                if (!tacticalButtonDown && tacticalButtonWasDown)
                {
                    float holdTime = Time.time - tacticalButtonDownTime;

                    // If released before 1 second and command wasn't already executed, toggle device
                    if (holdTime < HOLD_DURATION && !commandExecuted)
                    {
                        command = ECommand.ToggleTacticalDevice;
                    }
                }

                tacticalButtonWasDown = tacticalButtonDown;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class EscapeHandler : IInputHandler
        {
            public void UpdateCommand(ref ECommand command)
            {
                bool escapeButtonUp = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.ButtonB.GetStateDown(SteamVR_Input_Sources.Any) : SteamVR_Actions._default.ButtonY.GetStateDown(SteamVR_Input_Sources.Any);
                bool backButtonUp = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.ButtonY.GetStateDown(SteamVR_Input_Sources.Any) : SteamVR_Actions._default.ButtonB.GetStateDown(SteamVR_Input_Sources.Any);
                SteamVR_Action_Boolean secondaryGripState = (VRSettings.GetLeftHandedMode()) ? SteamVR_Actions._default.RightGrip : SteamVR_Actions._default.LeftGrip;

                if (secondaryGripState.state && !VRGlobals.menuOpen)
                    return;
                if (escapeButtonUp)
                    command = ECommand.Escape;
                else if ((VRGlobals.menuOpen || !VRGlobals.inGame) && backButtonUp)
                    command = ECommand.Escape;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class ExamineWeaponHandler : IInputHandler
        {
            private bool examineWeapon = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (examineWeapon) { 
                    command = ECommand.ExamineWeapon;
                    examineWeapon = false;
                    WeaponPatches.currentGunInteractController.hasExaminedAfterMalfunction = true;
                }
            }

            public void TriggerExamineWeapon() {
                examineWeapon = true;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        /*public class EFTConfigHandler : IInputHandler
        {
            public void UpdateCommand(ref ECommand command)
            {
                if (!InstalledMods.EFTApiInstalled)
                    return;
                if (SteamVR_Actions._default.Start.GetStateDown(SteamVR_Input_Sources.Any))
                    EFTApiSupport.OpenCloseConfigUI();
            }
        }*/
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class HeadMountedDeviceHandler : IInputHandler
        {
            private bool toggleHeadMountedDevice = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (toggleHeadMountedDevice) { 
                    command =  ECommand.ToggleGoggles;
                    toggleHeadMountedDevice = false;
                }
            }

            public void TriggerHHeadMount() {
                toggleHeadMountedDevice = true;
            }
        }

        public class HeadLightHandler : IInputHandler
        {
            private bool toggleHeadLight = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (toggleHeadLight)
                {
                    command = ECommand.ToggleHeadLight;
                    toggleHeadLight = false;
                }
            }

            public void TriggerHeadLight()
            {
                toggleHeadLight = true;
            }
        }

        public class DropBackpackHandler : IInputHandler
        {
            private bool dropBackpack = false;
            public void UpdateCommand(ref ECommand command)
            {
                if (dropBackpack)
                {
                    command = ECommand.DropBackpack;
                    dropBackpack = false;
                }
            }

            public void TriggerDropBackpack()
            {
                dropBackpack = true;
            }
        }
    }
}
