using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using EFT.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
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
        // Jump / vault. Vive wands: a physical click of the right trackpad, accepted only with the finger in the upper half of the pad
        // (so clicking down for crouch never jumps). Short click = jump, held past VRSettings.GetVaultHoldTime() = vault.
        public class JumpInputHandler : IInputHandler
        {
            private enum JumpState { Idle, Held, Fired }
            private JumpState _state = JumpState.Idle;
            private float _heldTime = 0f;

            // Finger y above this = upper half of the Vive trackpad; the small guard keeps the dead centre from triggering
            private const float UPPER_HALF_Y = 0.05f;

            public void UpdateCommand(ref ECommand command)
            {
                if (VRGlobals.vrPlayer == null ||
                    SteamVR_Actions._default?.RightJoystick == null ||
                    SteamVR_Actions._default?.ClickRightJoystick == null)
                    return;

                if (VRGlobals.vrControllerType == "vive")
                {
                    UpdateVive(ref command);
                }
                else
                {
                    UpdateDefault(ref command);
                }
            }

            // Vive Wand: physical trackpad click + upper-half guard
            private void UpdateVive(ref ECommand command)
            {
                // Source-less: with Swap Sticks this is the left controller's click (VRInput)
                bool clickHeld = VRInput.ClickRightJoystick.state;
                bool clickDown = VRInput.ClickRightJoystick.stateDown;

                // Touch position: only a guard to separate jump/vault from crouch
                float fingerY = VRInput.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y;
                bool fingerInUpperHalf = fingerY > UPPER_HALF_Y;

                command = ECommand.None;

                switch (_state)
                {
                    case JumpState.Idle:
                        if (clickDown && fingerInUpperHalf && !VRGlobals.vrPlayer.blockJump)
                        {
                            _state = JumpState.Held;
                            _heldTime = 0f;
                        }
                        break;

                    case JumpState.Held:
                        if (!clickHeld)
                        {
                            // Released before vault threshold → Jump
                            command = ECommand.Jump;
                            _state = JumpState.Idle;
                        }
                        else
                        {
                            _heldTime += Time.deltaTime;
                            if (_heldTime >= VRSettings.GetVaultHoldTime())
                            {
                                command = ECommand.Vaulting;
                                _state = JumpState.Fired;
                            }
                        }
                        break;

                    case JumpState.Fired:
                        if (!clickHeld)
                        {
                            if (VRGlobals.player != null && VRGlobals.player.IsVaultingPressed)
                                command = ECommand.VaultingEnd;
                            _state = JumpState.Idle;
                        }
                        else
                        {
                            command = ECommand.Vaulting;
                        }
                        break;
                }
            }

            // Default (Index, Oculus, etc.): joystick axis
            private bool _isVaulting = false;
            private float _timeHeld = 0f;
            private const float TIME_HELD_FOR_VAULT = 0.3f;

            private void UpdateDefault(ref ECommand command)
            {
                float yAxis = VRInput.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y;

                if (!VRGlobals.vrPlayer.blockJump && yAxis > 0.925f)
                {
                    _timeHeld += Time.deltaTime;
                    if (_timeHeld >= TIME_HELD_FOR_VAULT)
                    {
                        command = ECommand.Vaulting;
                        _isVaulting = true;
                    }
                }
                else
                {
                    if (VRGlobals.player && VRGlobals.player.IsVaultingPressed)
                    {
                        _isVaulting = false;
                        command = ECommand.VaultingEnd;
                    }
                    else if (_timeHeld > 0.05f && _timeHeld < TIME_HELD_FOR_VAULT)
                    {
                        command = ECommand.Jump;
                    }
                    _timeHeld = 0f;
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class CrouchHandler : IInputHandler
        {

            public void UpdateCommand(ref ECommand command)
            {
                if (VRGlobals.vrPlayer == null || VRGlobals.player == null || VRGlobals.vrPlayer.blockCrouch)
                    return;

                var movementContext = VRGlobals.player.MovementContext;
                if (movementContext == null || movementContext.CharacterController == null)
                    return;

                float joystickY = VRInput.RightJoystick.axis.y;
                float crouchThreshold = VRGlobals.vrControllerType == "vive"
                    ? VRSettings.GetCrouchThreshold()
                    : 0.8f;
                // Right joystick down: lower the pose (crouch)
                if (joystickY < -crouchThreshold)
                {
                    float poseDelta = -1.5f * Time.deltaTime;
                    VRGlobals.player.ChangePose(poseDelta);
                    //float poseLevel = VRGlobals.player.MovementContext.PoseLevel + poseDelta;
                    //float poseLevelResult = Mathf.Clamp(poseLevel, 0f, VRGlobals.player.Physical.MaxPoseLevel);
                    //float characterControllerHeight = Mathf.Lerp(1.2f, 1.6f, poseLevelResult);
                    VRGlobals.vrPlayer.crouchHeightDiff = 1.6f - movementContext.CharacterController.height;
                }

                // Joystick up while crouched: raise the pose (stand)
                if (VRGlobals.vrPlayer.crouchHeightDiff > 0.01f && joystickY > crouchThreshold)
                {
                    float poseDelta = 0.05f;
                    VRGlobals.player.ChangePose(poseDelta);
                    //float poseLevel = VRGlobals.player.MovementContext.PoseLevel + poseDelta;
                    //float poseLevelResult = Mathf.Clamp(poseLevel, 0f, VRGlobals.player.Physical.MaxPoseLevel);
                    //float characterControllerHeight = Mathf.Lerp(1.2f, 1.6f, poseLevelResult);
                    VRGlobals.vrPlayer.crouchHeightDiff = Mathf.Clamp(1.61f - movementContext.CharacterController.height, 0.01f, 1f);
                    //Plugin.MyLog.LogWarning(VRGlobals.player.MovementContext.PoseLevel + "   |  " + poseLevel + "   |  " + poseLevelResult + "  |   " + characterControllerHeight + "   |   " + VRGlobals.vrPlayer.crouchHeightDiff);
                }

                // crouchHeightDiff stays at 0.01 until the right joystick Y comes back down, so standing up doesn't jump straight after
                else if (VRGlobals.vrPlayer.crouchHeightDiff == 0.01f && joystickY < 0.5f)
                {
                    VRGlobals.vrPlayer.crouchHeightDiff = 0f;
                }
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class ProneInputHandler : IInputHandler
        {
            private static float MIN_TIME_BETWEEN_PRESSES = 1f;
            private static float MAX_CROUCH_HEIGHT_DIFF = 0.4f;
            float timeSinceLastPress = 0;
            private static bool releasedPullAfterFullCrouch = false;
            public void UpdateCommand(ref ECommand command)
            {
                // No prone in the hideout or while the right joystick is blocked
                if (VRGlobals.player is HideoutPlayer || VRGlobals.blockRightJoystick
                    || (VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.blockCrouch))
                    return;

                float crouchThreshold = VRGlobals.vrControllerType == "vive"
                    ? VRSettings.GetCrouchThreshold()
                    : 0.8f;
                if (VRInput.RightJoystick.axis.y > -crouchThreshold && ((float)Math.Round(VRGlobals.vrPlayer.crouchHeightDiff, 1) == MAX_CROUCH_HEIGHT_DIFF))
                    releasedPullAfterFullCrouch = true;
                else if (releasedPullAfterFullCrouch && (float)Math.Round(VRGlobals.vrPlayer.crouchHeightDiff, 1) != MAX_CROUCH_HEIGHT_DIFF)
                    releasedPullAfterFullCrouch = false;

                if (VRInput.RightJoystick.axis.y < -crouchThreshold && releasedPullAfterFullCrouch && Time.time - timeSinceLastPress > MIN_TIME_BETWEEN_PRESSES)
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

                if (VRInput.ClickRightJoystick.state && !heightReset)
                {
                    timeHeld += Time.deltaTime;
                    if (timeHeld > HEIGH_RESET_TIME_THRESHOLD)
                    {
                        VRGlobals.vrPlayer.initPos = VRGlobals.VRCam.transform.localPosition;
                        TarkovVR.Source.Player.VR.ArmLength.TryMeasure();
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
                // Step Or Lean To Sprint: measured every frame, acted on below when neither the click nor the stick decides
                bool stickForward = !VRGlobals.blockLeftJoystick && VRInput.LeftJoystick.axis.y >= VRSettings.GetLeftStickSensitivity();
                int step = StepSprint.Update(VRGlobals.player != null && VRGlobals.player.IsSprintEnabled, stickForward);

                if (VRInput.ClickLeftJoystick.GetStateDown(SteamVR_Input_Sources.Any))
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
                else if (isSprinting && VRInput.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).y < VRSettings.GetLeftStickSensitivity())
                {
                    command = ECommand.EndSprinting;
                    isSprinting = false;
                }
                else if (step > 0)
                {
                    // Only sent while the game isn't running (StepSprint): ToggleSprinting on a run would end it
                    command = ECommand.ToggleSprinting;
                    isSprinting = true;
                    VRGlobals.vrPlayer.crouchHeightDiff = 0;
                }
                else if (step < 0)
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

                // Just out of a menu, B still backing out of it (MenuExitButtonWait): no reload, no drop, and a tap from before the menu
                // doesn't fire one on its way out either
                if (MenuExitButtonWait.Blocks)
                {
                    waitingForSecondTap = false;
                    reloadTriggered = false;
                    return;
                }

                // Tarkov's reload would act on the magazine the off hand is holding out of the gun
                if (VRReloadController.HoldingGunMagazine)
                {
                    waitingForSecondTap = false;
                    reloadTriggered = false;
                    return;
                }

                // A pump gun the hands work: B is its slide release (VRReloadController.Shotgun), never Tarkov's reload; a break-action's,
                // its lever (VRReloadController.BreakAction)
                if (VRReloadController.GunHandButtonIsSlideRelease(VRGlobals.firearmController) || VRReloadController.GunHandButtonIsBarrelLever(VRGlobals.firearmController))
                {
                    waitingForSecondTap = false;
                    reloadTriggered = false;
                    return;
                }

                // Manual reload on a gun with a detachable magazine: B is only the mag release (VRReloadController:
                // a press drops the magazine, held it lets the off hand pull it out), never Tarkov's reload
                bool manualReload = VRReloadController.ManualReloadActive();

                bool reloadButton = Handedness.GunHandUpperButton.GetStateDown(SteamVR_Input_Sources.Any);

                if (reloadButton)
                {
                    float currentTime = Time.time;
                    float timeSinceLastTap = currentTime - lastTapTime;

                    if (waitingForSecondTap && timeSinceLastTap < DOUBLE_TAP_WINDOW)
                    {
                        // Detachable magazines: the double tap drops the magazine out of the gun (Manual Reload: the
                        // first press already did)
                        if (!manualReload && !VRReloadController.TryReleaseMagazine())
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
                    // A new magazine already in the off hand: you're reloading by hand, no automatic reload
                    if (!manualReload && !VRReloadController.HoldingMagazine)
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
                // A mounted gun: aimed with the eye at its sight (MountedGun)
                if (TarkovVR.Source.Player.Interactions.MountedGun.Mounted)
                {
                    TarkovVR.Source.Player.Interactions.MountedGun.UpdateAim(ref command);
                    return;
                }
                if (VRGlobals.firearmController == null && !WeaponPatches.rangeFinder)
                    return;

                float primaryHandTriggerAmount = Handedness.GunHandTrigger.axis;

                if (WeaponPatches.rangeFinder)
                {
                    HandleRangefinder(primaryHandTriggerAmount);
                    return;
                }

                if (Time.time - lastToggleTime < TOGGLE_COOLDOWN)
                    return;

                bool isAiming = VRGlobals.firearmController.IsAiming;

                // Slung (ChestHang): the gun hangs on the chest, no aiming down its sights. Let go into the sling while aiming: out of it.
                if (TarkovVR.Source.Player.Interactions.ChestHang.Hanging)
                {
                    if (isAiming)
                        ExitAimMode(ref command);
                    return;
                }

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

                // An optic's aim camera
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

                // Is the scope pointing at your head
                float alignmentAngle = Vector3.Angle(scopeForward * -1f, directionToScope);

                // Are you looking at the scope
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

                // Is the gun pointing at your head
                float alignmentAngle = Vector3.Angle(gunForward, directionToSight);

                // Are you looking at the gun
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
            private bool isDryFiring = false;

            public void UpdateCommand(ref ECommand command)
            {
                // A mounted gun: the trigger of a hand on its handle (MountedGun)
                if (TarkovVR.Source.Player.Interactions.MountedGun.Mounted)
                {
                    bool pulled = TarkovVR.Source.Player.Interactions.MountedGun.TriggerPulled;
                    if (!isShooting && pulled)
                    {
                        command = ECommand.ToggleShooting;
                        isShooting = true;
                    }
                    else if (isShooting && !pulled)
                    {
                        command = ECommand.EndShooting;
                        isShooting = false;
                    }
                    return;
                }

                if (WeaponPatches.grenadeEquipped || VRGlobals.switchingWeapon)
                    return;

                float primaryHandTriggerAmount = Handedness.GunHandTrigger.axis;
                bool detached = TarkovVR.Source.Player.Interactions.ChestHang.Detached;

                // No trigger finger on the grip (slung, or support-hand-only hold) = no firing; a burst under way ends. Same while
                // VRReloadController has the gun apart: the magazine out in the hand (Tarkov still thinks it's in the gun), the charging
                // handle being worked, or the chambered round not racked yet.
                if (detached || VRReloadController.BlocksFiring)
                {
                    if (isShooting)
                    {
                        command = ECommand.EndShooting;
                        isShooting = false;
                    }
                    // On the round Tarkov's insert chambered, not racked yet: the trigger only clicks
                    bool dryFiring = !detached && VRReloadController.TriggerOnlyClicks && primaryHandTriggerAmount > 0.5f;
                    if (dryFiring != isDryFiring)
                    {
                        isDryFiring = dryFiring;
                        VRReloadController.DryFireTrigger(dryFiring);
                    }
                    return;
                }

                // Racked with the trigger still held from a click: it fires on the next pull, not this one
                if (isDryFiring)
                {
                    isDryFiring = false;
                    VRReloadController.DryFireTrigger(pressed: false);
                    isShooting = primaryHandTriggerAmount > 0.5f;
                }

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
            public void UpdateCommand(ref ECommand command)
            {
                if (VRGlobals.vrPlayer == null || !VRGlobals.vrPlayer.interactMenuOwnsStick)
                    return;

                float primaryHandScrollAxis = 0f;

                if (VRSettings.GetLeftHandedMode() &&
                    WeaponPatches.currentGunInteractController != null &&
                    WeaponPatches.currentGunInteractController.highlightingMesh)
                {
                    primaryHandScrollAxis = VRInput.LeftJoystick.axis.y;
                }
                else
                {
                    primaryHandScrollAxis = VRInput.RightJoystick.axis.y;
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

                float secondaryHandTriggerAmount = Handedness.OffHandTrigger.axis;
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
                VRBoolean UBGLButtonAction = Handedness.OffHandLowerButton;
                float secondaryHandTriggerAmount = Handedness.OffHandTrigger.axis;
                bool cancelGrenadeButtonClick = (Handedness.GunHandUpperButton.GetStateDown(SteamVR_Input_Sources.Any));
                bool UBGLButtonDown = UBGLButtonAction.state;

                if ((swapPrimaryWeapon) || swapWeapon || (WeaponPatches.grenadeEquipped && cancelGrenadeButtonClick))
                {
                    if (VRGlobals.player.ActiveSlot == null || WeaponPatches.rangeFinder)
                        // If the first weapon slot is null then attempt select secondary
                        if (VRGlobals.player.Equipment._cachedSlots[0].ContainedItem != null)
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
        /*
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
        */
        public class VRScopeZoomHandler : IInputHandler
        {
            private bool swapZooms = false;

            public void UpdateCommand(ref ECommand command)
            {
                if (!VRGlobals.firearmController || !VRGlobals.firearmController.IsAiming)
                    return;

                if (swapZooms)
                {
                    command = ECommand.ChangeScopeMagnification;
                    swapZooms = false;
                }
            }

            public void TriggerSwapZooms() => swapZooms = true;
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
                bool changeFireModeButtonClick = Handedness.GunHandLowerButton.GetStateDown(SteamVR_Input_Sources.Any);

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
            private bool offTriggerWasDown;

            public void UpdateCommand(ref ECommand command)
            {
                // On a mounted gun the interact key is Tarkov's way off it: letting go of it is ours (MountedGun)
                if (TarkovVR.Source.Player.Interactions.MountedGun.Active)
                {
                    offTriggerWasDown = false;
                    return;
                }
                bool interactButtonUp = Handedness.GunHandLowerButton.GetStateDown(SteamVR_Input_Sources.Any);
                bool interactButtonDown = Handedness.GunHandLowerButton.GetStateUp(SteamVR_Input_Sources.Any);

                // Off-hand trigger executes a reach-selected weapon part (offHandInteract) with the A-button's edge mapping. Only while a
                // part is selected: the off trigger otherwise belongs to breath-hold (ADS) and UI, and selection excludes aiming/supporting.
                bool offTriggerPress = false, offTriggerRelease = false;
                if (WeaponPatches.currentGunInteractController != null
                    && WeaponPatches.currentGunInteractController.lastHitCompIndex != -1)
                {
                    float triggerAxis = Handedness.OffHandTrigger.axis;
                    bool down = triggerAxis > 0.5f;
                    offTriggerPress = down && !offTriggerWasDown;
                    offTriggerRelease = !down && offTriggerWasDown;
                    offTriggerWasDown = down;
                }
                else
                {
                    offTriggerWasDown = false;
                }

                if (interactButtonDown || offTriggerRelease)
                    command = ECommand.BeginInteracting;
                else if (interactButtonUp || offTriggerPress)
                    command = ECommand.EndInteracting;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class InventoryHandler : IInputHandler
        {
            public void UpdateCommand(ref ECommand command)
            {
                MenuModifiers.Tick();
                bool inventoryButtonUp = Handedness.OffHandLowerButton.GetStateUp(SteamVR_Input_Sources.Any);
                float secondaryHandTriggerAmount = Handedness.OffHandTrigger.axis;

                // Not after a click made with it held as Alt (MenuModifiers): that press was the modifier
                if (inventoryButtonUp && secondaryHandTriggerAmount < 0.1f && !MenuModifiers.ReleaseWasModifier(Handedness.OffHandLowerButton))
                    command = ECommand.ToggleInventory;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class GrenadeHandler : IInputHandler
        {
            private bool grenadePinPulled = false;
            private bool shootingToggled = false;
            private bool grenadePinResetting = false;

            private float plantHoldStart = -1f;     // when the dominant face button started being held (-1 = up)
            private bool tripwireToggled = false; // one mode-toggle per hold
            private bool tripwireFireHeld = false;    // edge-detect the place-post trigger

            public void UpdateCommand(ref ECommand command)
            {
                if (!WeaponPatches.grenadeEquipped)
                    return;

                GrenadeHandsController grenadeController = VRGlobals.player?.HandsController as GrenadeHandsController;
                if (grenadeController != null)
                {
                    HandleTripwireToggle(grenadeController);
                    if (grenadeController.CurrentFireMode == Weapon.EFireMode.greanadePlanting)
                    {
                        HandleTripwirePlacement(grenadeController);
                        return;
                    }
                    // Held by its button, the ring pulled by the other hand (GrenadeHold); below = the flow without it (trigger = pin, let go = throw)
                    if (GrenadeHold.enabled)
                    {
                        GrenadeHold.UpdateCommand(grenadeController, ref command);
                        return;
                    }
                }

                float primaryTrigger = Handedness.GunHandTrigger.axis;

                float secondaryTrigger = Handedness.OffHandTrigger.axis;

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
                    grenadeController.PutGrenadeBack();
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
                    grenadeController.ThrowGrenadeInner(null, 0f, 1f, Vector3.zero, 1f, false, true);
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

            private void HandleTripwireToggle(GrenadeHandsController grenadeController)
            {
                VRBoolean plantButton = Handedness.GunHandLowerButton;

                if (plantButton.state)
                {
                    if (plantHoldStart < 0f)
                        plantHoldStart = Time.time;
                    else if (!tripwireToggled && Time.time - plantHoldStart >= VRGrenadeController.tripwireHoldDuration)
                    {
                        bool planting = grenadeController.CurrentFireMode == Weapon.EFireMode.greanadePlanting;
                        grenadeController.ChangeFireMode(planting ? Weapon.EFireMode.grenadeThrowing : Weapon.EFireMode.greanadePlanting);
                        tripwireToggled = true;
                    }
                }
                else
                {
                    plantHoldStart = -1f;
                    tripwireToggled = false;
                }
            }

            private void HandleTripwirePlacement(GrenadeHandsController grenadeController)
            {
                float primaryTrigger = Handedness.GunHandTrigger.axis;

                if (!tripwireFireHeld && primaryTrigger > 0.5f)
                {
                    grenadeController.HandleFireInput();
                    tripwireFireHeld = true;
                }
                else if (tripwireFireHeld && primaryTrigger < 0.5f)
                {
                    tripwireFireHeld = false;
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
                VRBoolean tacticalButtonAction = Handedness.OffHandUpperButton;

                // Only with the hand on the foregrip: the same grip also holds magazines and loot
                bool tacticalButtonDown = tacticalButtonAction.state && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting;

                if (tacticalButtonDown && !tacticalButtonWasDown)
                {
                    tacticalButtonDownTime = Time.time;
                    commandExecuted = false;
                }

                if (tacticalButtonDown)
                {
                    float holdTime = Time.time - tacticalButtonDownTime;

                    // Held for HOLD_DURATION: next device
                    if (holdTime >= HOLD_DURATION && !commandExecuted)
                    {
                        command = ECommand.NextTacticalDevice;
                        commandExecuted = true;
                    }
                }

                if (!tacticalButtonDown && tacticalButtonWasDown)
                {
                    float holdTime = Time.time - tacticalButtonDownTime;

                    // Released before HOLD_DURATION: toggle the device
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
                bool escapeButtonUp = Handedness.OffHandUpperButton.GetStateDown(SteamVR_Input_Sources.Any);
                bool backButtonUp = Handedness.GunHandUpperButton.GetStateDown(SteamVR_Input_Sources.Any);
                VRBoolean secondaryGripState = Handedness.OffHandGrip;

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
        /*
        public class EFTConfigHandler : IInputHandler
        {
            public void UpdateCommand(ref ECommand command)
            {
                if (!InstalledMods.EFTApiInstalled)
                    return;
                if (VRInput.Start.GetStateDown(SteamVR_Input_Sources.Any))
                    EFTApiSupport.OpenCloseConfigUI();
            }
        }
        */
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
