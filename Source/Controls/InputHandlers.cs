using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using EFT.UI;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.ModSupport;
using TarkovVR.ModSupport.EFTApi;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

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
                if (!VRGlobals.vrPlayer.blockJump && SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y > 0.925f)
                {
                    timeHeld += Time.deltaTime;
                    if (timeHeld >= TIME_HELD_FOR_VAULT) { 
                        command = ECommand.Vaulting;
                        isVaulting = true;
                    }
                    //command = ECommand.Jump;
                }
                else {
                    if (VRGlobals.player && VRGlobals.player.IsVaultingPressed)
                    {
                        isVaulting = false;
                        command = ECommand.VaultingEnd;
                    }
                    else if (timeHeld > 0.05 && timeHeld < TIME_HELD_FOR_VAULT)
                        command = ECommand.Jump;
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
                if (VRGlobals.vrPlayer.blockCrouch)
                    return;

                if (SteamVR_Actions._default.RightJoystick.axis.y < -0.8)
                {
                    VRGlobals.player.ChangePose(-1.5f * Time.deltaTime);
                    VRGlobals.vrPlayer.crouchHeightDiff = 1.6f - VRGlobals.player.MovementContext.CharacterController.height;
                }

                if (VRGlobals.vrPlayer.crouchHeightDiff > 0.01f && SteamVR_Actions._default.RightJoystick.axis.y > 0.8)
                {
                    VRGlobals.player.ChangePose(0.05f);
                    VRGlobals.vrPlayer.crouchHeightDiff = Mathf.Clamp(1.61f - VRGlobals.player.MovementContext.CharacterController.height, 0.01f, 1);
                }
                // Really shit way to do this, but this prevents a jump happening immediately after changing the crouch height diff by 
                // keeping it at 0.01 until the right joystick Y axis goes down
                else if (VRGlobals.vrPlayer.crouchHeightDiff ==  0.01f && SteamVR_Actions._default.RightJoystick.axis.y < 0.5) { 
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
                if (VRGlobals.player is HideoutPlayer)
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
                if (SteamVR_Actions._default.ButtonB.GetStateDown(SteamVR_Input_Sources.Any)) { 
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
            private float x = 0;
            private float y = 0;
            private float z = 1;
            public void UpdateCommand(ref ECommand command)
            { 
                if (VRGlobals.firearmController == null && !WeaponPatches.rangeFinder)
                    return;

                if (WeaponPatches.rangeFinder)
                {
                    if (SteamVR_Actions._default.RightTrigger.axis > 0.5f && !WeaponPatches.rangeFinder.IsAiming)
                    {
                        WeaponPatches.rangeFinder.ToggleAim();
                        VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.13f, -0.1711f, -0.24f);
                        VRGlobals.weaponHolder.transform.localEulerAngles = new Vector3(12, 308, 90);
                    }
                    else if (SteamVR_Actions._default.RightTrigger.axis < 0.5f && WeaponPatches.rangeFinder.IsAiming) {
                        WeaponPatches.rangeFinder.ToggleAim();
                        VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(37, 267, 55);
                        VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.23f, 0.0689f, -0.23f);
                    }
                    return;
                }

                bool isAiming = VRGlobals.firearmController.IsAiming;
                if (VRGlobals.scope != null)
                {
                    Vector3 directionToScope = (VRGlobals.scope.transform.position + (VRGlobals.scope.transform.forward * -0.25f)) - VRGlobals.camHolder.transform.position;
                    directionToScope = directionToScope.normalized;
                    float angleToScope = Vector3.Angle(VRGlobals.scope.transform.forward * -1, directionToScope);
                    float angleFromScope = Vector3.Angle(VRGlobals.camHolder.transform.forward, directionToScope);
                    //Plugin.MyLog.LogWarning("Aiming deets: " + angleToScope + "   |   " + angleFromScope + "   |   " + isAiming);
                    if (!isAiming && angleToScope <= 25f && angleFromScope <= 25f)
                        command = ECommand.ToggleAlternativeShooting;
                    else if (isAiming && (angleToScope > 25f || angleFromScope > 25f))
                    {
                        command = ECommand.EndAlternativeShooting;
                        VRPlayerManager.smoothingFactor = 50f;
                    }
                }
                else {
                    Vector3 direction = VRGlobals.vrPlayer.RightHand.transform.right * -1;
                    Vector3 directionToGun = (VRGlobals.vrPlayer.RightHand.transform.position + direction) - VRGlobals.camHolder.transform.position;
                    directionToGun = directionToGun.normalized;
                    float angleToScope = Vector3.Angle(direction, directionToGun);
                    float angleFromScope = Vector3.Angle(VRGlobals.camHolder.transform.forward, directionToGun);
                    //Plugin.MyLog.LogWarning("Aiming deets: " + angleToScope + "   |   " + angleFromScope + "   |   " + isAiming);
                    if (!isAiming && angleToScope <= 25f && angleFromScope <= 27.5f)
                        command = ECommand.ToggleAlternativeShooting;
                    else if (isAiming && (angleToScope > 25f || angleFromScope > 27.5f))
                    {
                        command = ECommand.EndAlternativeShooting;
                        VRPlayerManager.smoothingFactor = 50f;
                    }
                }

                if (command == ECommand.ToggleAlternativeShooting && VRGlobals.vrPlayer.isSupporting)
                        VRGlobals.player.Transform.rotation = Quaternion.Euler(0, Camera.main.transform.eulerAngles.y, 0);

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
                if (!isShooting && SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f)
                {
                    command = ECommand.ToggleShooting;
                    isShooting = true;
                }
                else if (isShooting && SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) < 0.5f)
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
            public void UpdateCommand(ref ECommand command)
            {
                if (VRGlobals.blockRightJoystick || !VRGlobals.vrPlayer.interactMenuOpen)
                    return;


                if (!isScrolling && SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y > 0.5f)
                {
                    command = ECommand.ScrollNext;
                    isScrolling = true;
                }
                else if (!isScrolling && SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y < -0.5f)
                {
                    command = ECommand.ScrollPrevious;
                    isScrolling = true;
                }
                else if (SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y > -0.5f && SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y < 0.5f)
                    isScrolling = false;
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
                bool isAiming = VRGlobals.firearmController.IsAiming;
                if (!isHoldingBreath && isAiming && SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f)
                {
                    command = ECommand.ToggleBreathing;
                    isHoldingBreath = true;
                    if (VRGlobals.scopeSensitivity * 40f > 0)
                        VRPlayerManager.smoothingFactor = VRGlobals.scopeSensitivity * 40f;
                }
                else if (isHoldingBreath && (SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any) < 0.5f || !isAiming))
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
            public void UpdateCommand(ref ECommand command)
            {
                if (!VRGlobals.player)
                    return;
                if ((swapPrimaryWeapon) || swapWeapon || (WeaponPatches.grenadeEquipped && SteamVR_Actions._default.ButtonB.GetStateDown(SteamVR_Input_Sources.Any)))
                {
                    if (VRGlobals.player.ActiveSlot == null)
                        // If the first weapon slot is null then attempt select secondary
                        if (VRGlobals.player.Equipment.slot_0[0].ContainedItem != null)
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
                if (changeFireMode)
                {
                    command = ECommand.ChangeWeaponMode;
                    changeFireMode = false;
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
            //private ECommand[] quickSlotCommands = {
            //    ECommand.SelectFastSlot4, 
            //    ECommand.SelectFastSlot5, 
            //    ECommand.SelectFastSlot6, 
            //    ECommand.SelectFastSlot7, 
            //    ECommand.SelectFastSlot8, 
            //    ECommand.SelectFastSlot9, 
            //    ECommand.SelectFastSlot0 
            //};

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
                if (SteamVR_Actions._default.ButtonA.GetStateDown(SteamVR_Input_Sources.Any))
                    command = ECommand.BeginInteracting;
                else if (SteamVR_Actions._default.ButtonA.GetStateUp(SteamVR_Input_Sources.Any))
                    command = ECommand.EndInteracting;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class InventoryHandler : IInputHandler
        {
            public void UpdateCommand(ref ECommand command)
            {
                if (SteamVR_Actions._default.ButtonX.GetStateUp(SteamVR_Input_Sources.Any))
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

                if (!shootingToggled && SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5)
                {
                    command = ECommand.ToggleShooting;
                    shootingToggled = true;
                }
                else if (shootingToggled && !grenadePinPulled) { 
                    command = ECommand.TryHighThrow;
                    grenadePinPulled = true;
                    // Change the weapon holder position for grenade after the pin pulling animation

                    PreloaderUI.Instance.WaitSeconds(1f, delegate
                    {
                        if (VRGlobals.player.HandsController as EFT.Player.GrenadeController && (VRGlobals.player.HandsController as EFT.Player.GrenadeController).WaitingForHighThrow) {

                            // You can't hold these smoke grenades after "pulling the pin" so don't set the laser to true
                            if (VRGlobals.player.HandsController.HandsHierarchy.name != "weapon_grenade_rdg2.generated(Clone)") {
                                InitVRPatches.rightPointerFinger.enabled = true;
                                //VRGlobals.handsInteractionController.grenadeLaser.active = true;
                                VRGlobals.weaponHolder.transform.localPosition = new Vector3(-0.1f, -0.43f, -0.25f);
                            }
                            if (VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_m7920.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_rgo.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_rgn.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_m18.generated(Clone)") { 
                                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(30, 273, 116);
                                VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.05f, -0.43f, -0.15f);
                            }
                            WeaponPatches.pinPulled = true;
                            //thin grenades, finger should be rotated 22 and grenade x offset is 8
                            //f1 and RGD grenades, finger should be rotated 22 and grenade x offset is 2
                            //M1 grenade, finger 22 grenade x offset prolly like -7
                            //M7290 whole hand would need to be rotated weaphold pos 0.05 -0.43 -0.15 rrot 30.0956 273.0178 116.7104 then finger is 22 and grenade is 8
                            //green smoke grenades finger and offset 22

                            //weapon_grenade_rgo_container(Clone)
                        }
                    });
                }
                else if (shootingToggled && grenadePinPulled && SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) < 0.5) {
                    command = ECommand.EndShooting;
                    shootingToggled = false;
                    //VRGlobals.handsInteractionController.grenadeLaser.active = false;
                    //VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                    //InitVRPatches.rightPointerFinger.enabled = false;

                }
                else if (!shootingToggled && grenadePinPulled)
                {
                    command = ECommand.FinishHighThrow;
                    grenadePinPulled = false;
                    //WeaponPatches.grenadeEquipped = false;
                }
                //else if (grenadePinPulled && SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) < 0.5) {
                //    command = ECommand.TryHighThrow;
                //    grenadePinPulled = false;
                //}
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public class EscapeHandler : IInputHandler
        {
            public void UpdateCommand(ref ECommand command)
            {
                if (SteamVR_Actions._default.ButtonY.GetStateUp(SteamVR_Input_Sources.Any))
                    command = ECommand.Escape;
                else if ((VRGlobals.menuOpen || !VRGlobals.inGame) && SteamVR_Actions._default.ButtonB.GetStateUp(SteamVR_Input_Sources.Any))
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
        public class EFTConfigHandler : IInputHandler
        {
            public void UpdateCommand(ref ECommand command)
            {
                if (!InstalledMods.EFTApiInstalled)
                    return;
                if (SteamVR_Actions._default.Start.GetStateDown(SteamVR_Input_Sources.Any))
                    EFTApiSupport.OpenCloseConfigUI();
            }
        }
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
    }
}
