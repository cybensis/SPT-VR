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
using Valve.VR.InteractionSystem;
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
            private const float DOUBLE_TAP_WINDOW = 0.2f;

            private bool reloadTriggered = false;
            private float lastTapTime = 0f;
            private bool waitingForSecondTap = false;

            public void UpdateCommand(ref ECommand command)
            {
                if (!VRGlobals.inGame)
                    return;

                bool reloadButton = VRSettings.GetLeftHandedMode()
                    ? SteamVR_Actions._default.ButtonY.GetStateDown(SteamVR_Input_Sources.Any)
                    : SteamVR_Actions._default.ButtonB.GetStateDown(SteamVR_Input_Sources.Any);

                if (reloadButton)
                {
                    float currentTime = Time.time;
                    float timeSinceLastTap = currentTime - lastTapTime;

                    if (waitingForSecondTap && timeSinceLastTap < DOUBLE_TAP_WINDOW)
                    {
                        command = ECommand.QuickReloadWeapon;
                        waitingForSecondTap = false;
                        lastTapTime = 0f;
                    }
                    else
                    {
                        waitingForSecondTap = true;
                        lastTapTime = currentTime;
                    }

                    reloadTriggered = false;
                }
                else if (waitingForSecondTap && (Time.time - lastTapTime) >= DOUBLE_TAP_WINDOW)
                {
                    command = ECommand.ReloadWeapon;
                    waitingForSecondTap = false;
                }
                else if (reloadTriggered)
                {
                    command = ECommand.ReloadWeapon;
                    reloadTriggered = false;
                }
            }

            public void TriggerReload()
            {
                reloadTriggered = true;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class AimHandler : IInputHandler
        {
            private const float SCOPE_ENTER_ANGLE_ALIGNMENT = 15f;      // How aligned scope must be with head direction
            private const float SCOPE_EXIT_ANGLE_ALIGNMENT = 25f;       // Threshold to exit scope view
            private const float SCOPE_ENTER_ANGLE_VIEW = 25f;           // How aligned head must be looking at scope
            private const float SCOPE_EXIT_ANGLE_VIEW = 35f;            // Threshold to stop looking at scope
            private const float SCOPE_OFFSET_DISTANCE = 0.25f;          // Offset from scope lens for eye position

            private const float IRONS_ENTER_ANGLE_ALIGNMENT = 15f;      // How aligned gun must be with head direction
            private const float IRONS_EXIT_ANGLE_ALIGNMENT = 25f;       // Threshold to exit iron sight view
            private const float IRONS_ENTER_ANGLE_VIEW = 25.5f;         // How aligned head must be looking at gun
            private const float IRONS_EXIT_ANGLE_VIEW = 38.5f;          // Threshold to stop looking at gun

            private const float RANGEFINDER_TRIGGER_THRESHOLD = 0.5f;

            private const float TOGGLE_COOLDOWN = 0.2f;
            private const int MAX_CAMERA_SEARCH_DEPTH = 3;

            private float lastToggleTime = 0f;

            public void UpdateCommand(ref ECommand command)
            {
                if (VRGlobals.firearmController == null && !WeaponPatches.rangeFinder)
                    return;

                float primaryHandTriggerAmount = VRSettings.GetLeftHandedMode()
                    ? SteamVR_Actions._default.LeftTrigger.axis
                    : SteamVR_Actions._default.RightTrigger.axis;

                if (WeaponPatches.rangeFinder)
                {
                    HandleRangefinder(primaryHandTriggerAmount);
                    return;
                }

                // Prevent rapid toggling
                if (Time.time - lastToggleTime < TOGGLE_COOLDOWN)
                    return;

                bool isAiming = VRGlobals.firearmController.IsAiming;

                if (HasValidScope())
                {
                    HandleScopeAiming(ref command, isAiming);
                }
                else
                {
                    HandleIronSightAiming(ref command, isAiming);
                }
            }

            private void HandleRangefinder(float triggerAmount)
            {
                bool shouldBeAiming = triggerAmount > RANGEFINDER_TRIGGER_THRESHOLD;
                bool currentlyAiming = WeaponPatches.rangeFinder.IsAiming;

                if (shouldBeAiming && !currentlyAiming)
                {
                    WeaponPatches.rangeFinder.ToggleAim();
                    VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.13f, -0.1711f, -0.24f);
                    VRGlobals.weaponHolder.transform.localEulerAngles = new Vector3(12, 308, 90);
                }
                else if (!shouldBeAiming && currentlyAiming)
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

            private Transform FindAimCamera(Transform parent, int currentDepth = 0)
            {
                if (currentDepth > MAX_CAMERA_SEARCH_DEPTH)
                    return null;

                // Check's if the current scope is an optic
                string parentName = parent.name;
                if (parentName == "mod_aim_camera" ||
                    parentName == "mod_aim_camera_000" ||
                    parentName == "mod_aim_camera_001")
                {
                    return parent;
                }

                foreach (Transform child in parent)
                {
                    Transform result = FindAimCamera(child, currentDepth + 1);
                    if (result != null)
                        return result;
                }

                return null;
            }

            private void HandleScopeAiming(ref ECommand command, bool isAiming)
            {
                Transform activeScope = VRGlobals.scopes.FirstOrDefault(s => s != null && s.parent != null);
                if (activeScope == null)
                    return;

                Transform aimCamera = FindAimCamera(activeScope);
                Transform scopeTransform = aimCamera ?? activeScope;

                Vector3 headPosition = VRGlobals.camHolder.transform.position;
                Vector3 headForward = VRGlobals.camHolder.transform.forward;
                Vector3 scopeForward = scopeTransform.forward;
                Vector3 eyeTargetPosition = scopeTransform.position + (scopeForward * -SCOPE_OFFSET_DISTANCE);

                Vector3 directionToScope = (eyeTargetPosition - headPosition).normalized;

                // Angle between scope's forward direction and direction from head to scope
                // (measures if scope is pointing at your head)
                float alignmentAngle = Vector3.Angle(scopeForward * -1f, directionToScope);

                // Angle between head's forward direction and direction to scope
                // (measures if you're looking at the scope)
                float viewAngle = Vector3.Angle(headForward, directionToScope);

                if (!isAiming)
                {
                    if (alignmentAngle <= SCOPE_ENTER_ANGLE_ALIGNMENT &&
                        viewAngle <= SCOPE_ENTER_ANGLE_VIEW)
                    {
                        command = ECommand.ToggleAlternativeShooting;
                        lastToggleTime = Time.time;
                    }
                }
                else
                {
                    if (alignmentAngle > SCOPE_EXIT_ANGLE_ALIGNMENT ||
                        viewAngle > SCOPE_EXIT_ANGLE_VIEW)
                    {
                        ExitAimMode(ref command);
                    }
                }
            }

            private void HandleIronSightAiming(ref ECommand command, bool isAiming)
            {
                Transform rightHand = VRGlobals.vrPlayer.RightHand.transform;
                Vector3 headPosition = VRGlobals.camHolder.transform.position;
                Vector3 headForward = VRGlobals.camHolder.transform.forward;

                // Gun's right axis points left, so invert it to get the aiming direction
                Vector3 gunForward = rightHand.right * -1f;
                Vector3 sightPosition = rightHand.position + gunForward;

                Vector3 directionToSight = (sightPosition - headPosition).normalized;

                // Angle between gun's forward direction and direction from head to sight
                // (measures if gun is pointing at your head)
                float alignmentAngle = Vector3.Angle(gunForward, directionToSight);

                // Angle between head's forward direction and direction to sight
                // (measures if you're looking at the gun)
                float viewAngle = Vector3.Angle(headForward, directionToSight);

                if (!isAiming)
                {
                    if (alignmentAngle <= IRONS_ENTER_ANGLE_ALIGNMENT &&
                        viewAngle <= IRONS_ENTER_ANGLE_VIEW)
                    {
                        command = ECommand.ToggleAlternativeShooting;
                        lastToggleTime = Time.time;
                    }
                }
                else
                {
                    if (alignmentAngle > IRONS_EXIT_ANGLE_ALIGNMENT ||
                        viewAngle > IRONS_EXIT_ANGLE_VIEW)
                    {
                        ExitAimMode(ref command);
                    }
                }
            }

            private void ExitAimMode(ref ECommand command)
            {
                command = ECommand.EndAlternativeShooting;
                //VRPlayerManager.smoothingFactor = 50f;
                VRGlobals.scopeSensitivity = 0;
                lastToggleTime = Time.time;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class FireHandler : IInputHandler
        {
            private bool isShooting = false;

            public void UpdateCommand(ref ECommand command)
            {
                if (WeaponPatches.grenadeEquipped || VRGlobals.switchingWeapon)
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
            public static bool isHoldingBreath = false;

            public void UpdateCommand(ref ECommand command)
            {
                if (!VRGlobals.firearmController)
                    return;

                float secondaryHandTriggerAmount = VRSettings.GetLeftHandedMode() ? SteamVR_Actions._default.RightTrigger.axis : SteamVR_Actions._default.LeftTrigger.axis;
                bool isAiming = VRGlobals.firearmController.IsAiming;
                var physical = VRGlobals.player.Physical;

                if (physical != null)
                {
                    if (isHoldingBreath && !physical.HoldingBreath)
                    {
                        isHoldingBreath = false;
                    }
                }

                if (!isHoldingBreath && isAiming && secondaryHandTriggerAmount > 0.5f)
                {
                    command = ECommand.ToggleBreathing;
                    isHoldingBreath = true;
                }
                else if (isHoldingBreath && (secondaryHandTriggerAmount < 0.5f || !isAiming))
                {
                    command = ECommand.EndBreathing;
                    isHoldingBreath = false;
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

                    if (UBGLButtonDown && !UBGLButtonWasDown)
                    {
                        UBGLButtonDownTime = Time.time;
                        commandExecuted = false;
                    }

                    if (UBGLButtonDown)
                    {
                        float holdTime = Time.time - UBGLButtonDownTime;

                        if (holdTime >= HOLD_DURATION && !commandExecuted)
                        {
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
            private bool grenadePinResetting = false;

            public void UpdateCommand(ref ECommand command)
            {
                if (!WeaponPatches.grenadeEquipped)
                    return;

                float primaryTrigger = VRSettings.GetLeftHandedMode()
                    ? SteamVR_Actions._default.LeftTrigger.axis
                    : SteamVR_Actions._default.RightTrigger.axis;

                float secondaryTrigger = VRSettings.GetLeftHandedMode()
                    ? SteamVR_Actions._default.RightTrigger.axis
                    : SteamVR_Actions._default.LeftTrigger.axis;

                if (primaryTrigger < 0.5f && grenadePinResetting)
                {
                    shootingToggled = false;
                    grenadePinResetting = false;
                }

                if (!shootingToggled && primaryTrigger > 0.5f && !grenadePinResetting)
                {
                    command = ECommand.ToggleShooting;
                    shootingToggled = true;
                }
                else if (shootingToggled && grenadePinPulled && secondaryTrigger > 0.5f)
                {
                    PutPinBack();
                }
                else if (shootingToggled && !grenadePinPulled && !grenadePinResetting)
                {
                    command = ECommand.TryHighThrow;
                    grenadePinPulled = true;
                    PreloaderUI.Instance.WaitSeconds(1.25f, PositionGrenadeAfterPinPull);
                }
                else if (shootingToggled && grenadePinPulled && primaryTrigger < 0.5f && !grenadePinResetting)
                {
                    command = ECommand.EndShooting;
                    shootingToggled = false;
                    grenadePinPulled = false;
                    ThrowGrenade();
                }
            }

            private void PutPinBack()
            {
                if (VRGlobals.player.HandsController is BaseGrenadeHandsController grenadeController)
                {
                    grenadeController.method_8();
                    grenadePinResetting = true;
                    grenadePinPulled = false;
                    shootingToggled = false;
                    WeaponPatches.pinPulled = false;
                }
            }

            private void ThrowGrenade()
            {
                if (VRGlobals.player.HandsController is BaseGrenadeHandsController grenadeController)
                {
                    grenadeController.method_9(null, 0f, 1f, Vector3.zero, 1f, false, true);
                }
            }

            private void PositionGrenadeAfterPinPull()
            {
                if (!(VRGlobals.player.HandsController is EFT.Player.GrenadeHandsController controller))
                    return;

                if (!controller.WaitingForHighThrow)
                    return;

                string grenadeName = VRGlobals.player.HandsController.HandsHierarchy.name;

                //if (grenadeName == "weapon_grenade_rdg2.generated(Clone)")
                    //return;

                // Default position for most grenades
                VRGlobals.weaponHolder.transform.localPosition = new Vector3(-0.1f, -0.43f, -0.25f);

                // Special handling for specific grenade types
                if (IsSpecialGrenade(grenadeName))
                {
                    VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(30, 273, 116);
                    VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.05f, -0.43f, -0.15f);
                }

                WeaponPatches.pinPulled = true;

                if (WeaponPatches.currentGunInteractController != null)
                    WeaponPatches.currentGunInteractController.framesAfterEnabled = 0;
            }

            private bool IsSpecialGrenade(string name)
            {
                return name == "weapon_grenade_m7920.generated(Clone)" ||
                       name == "weapon_grenade_rgo.generated(Clone)" ||
                       name == "weapon_grenade_rgn.generated(Clone)" ||
                       name == "weapon_grenade_m18.generated(Clone)";
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
