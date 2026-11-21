using EFT.InputSystem;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class VRControlsPatches
    {
        private const float JOYSTICK_DEADZONE = 0.7f;
        private const float JUMP_OR_STAND_CLAMP_RANGE = 0.75f;
        
        private static bool _snapTurned = false;

        // While looking through a scope that zooms on the turning stick (VRPlayerManager.ScopeZoomOwnsStick), the stick only turns when pushed
        // nearly straight sideways, and not at all after a zoom push until it is back near the centre (so a zoom push drifting sideways doesn't turn)
        public static bool scopeTurnGuard = true;
        public static float scopeTurnMaxAngle = 45f;    // deg off straight sideways the stick may point and still turn
        public static float scopeTurnDeadzone = 0.15f;  // sideways push below which it doesn't turn; smooth turning ramps up from here
        public static float scopeZoomPush = 0.3f;       // an up / down push past this, more up / down than sideways, is a zoom
        public static float stickCentre = 0.2f;         // back within this of the centre, it may turn / zoom again
        public static float scopeTurnPush = 0.3f;       // a sideways push past this, more sideways than up / down, is a turn: no zoom until centred
        private static bool zoomPushSeen;
        private static bool turnPushSeen;

        // The joystick zoom input for VROpticController: the turning stick's up / down, counted only when pushed more up / down than sideways,
        // and not after a turn push until the stick is back near the centre
        internal static float ScopeZoomAxis()
        {
            Vector2 stick = VRInput.RightJoystick.axis;
            if (!scopeTurnGuard)
                return stick.y;
            if (Mathf.Abs(stick.x) > scopeTurnPush && Mathf.Abs(stick.x) > Mathf.Abs(stick.y))
                turnPushSeen = true;
            else if (stick.magnitude < stickCentre)
                turnPushSeen = false;
            return turnPushSeen || Mathf.Abs(stick.y) < Mathf.Abs(stick.x) ? 0f : stick.y;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InputPreset), "UpdateInput")]
        private static bool MasterVRControls(InputPreset __instance, ref List<ECommand> commands, ref float[] axis, ref float deltaTime)
        {
            UpdateBaseInputs(__instance);
            
            if (__instance._workingKeyCombinations != null)
            {
                commands.Clear();
                VRInputManager.UpdateCommands(ref commands);
            }

            ResetAxisValues(axis);
            
            if (__instance._workingAxisCombinations == null)
            {
                return false;
            }
            
            if (VRGlobals.inGame && !VRGlobals.menuOpen)
            {
                ProcessAxisInputs(__instance, ref axis);
            }
            
            return false;
        }
        
        private static void UpdateBaseInputs(InputPreset instance)
        {
            // Update primary input interfaces
            if (instance._updateInputs != null)
            {
                for (int i = 0; i < instance._updateInputs.Length; i++)
                {
                    instance._updateInputs[i].Update();
                }
            }

            // Update secondary input interfaces
            if (instance._laterUpdateInputs != null)
            {
                for (int i = 0; i < instance._laterUpdateInputs.Length; i++)
                {
                    instance._laterUpdateInputs[i].Update();
                }
            }
        }
        
        private static void ResetAxisValues(float[] axis)
        {
            for (int i = 0; i < axis.Length; i++)
            {
                axis[i] = 0f;
            }
        }
        
        private static void ProcessAxisInputs(InputPreset instance, ref float[] axis)
        {
            for (int i = 0; i < instance._workingAxisCombinations.Length; i++)
            {
                int axisIndex = instance._workingAxisCombinations[i].IntAxis;
                
                // Apply default axis value if needed
                if (Mathf.Abs(axis[axisIndex]) < 0.0001f)
                {
                    axis[axisIndex] = instance._workingAxisCombinations[i].GetValue();
                }
                
                // Handle specific axis behaviors
                switch (i)
                {
                    case 3: // Unknown axis - explicitly disable
                        axis[axisIndex] = 0;
                        break;
                    
                    case 2: // Rotation control
                        ProcessRotationInput(instance, ref axis, axisIndex);
                        break;
                    
                    case 0: // Horizontal movement
                    case 1: // Vertical movement
                        ProcessMovementInput(instance, ref axis, i, axisIndex);
                        break;
                }
            }
        }
        
        private static void ProcessRotationInput(InputPreset instance, ref float[] axis, int axisIndex)
        {
            // Reset snap turn when joystick returns to center
            if (_snapTurned && Mathf.Abs(VRInput.RightJoystick.axis.x) < JOYSTICK_DEADZONE)
            {
                _snapTurned = false;
            }

            bool disableTurn = (WeaponPatches.currentGunInteractController && 
                               WeaponPatches.currentGunInteractController.highlightingMesh) || 
                               Mathf.Abs(VRInput.RightJoystick.axis.y) >= JUMP_OR_STAND_CLAMP_RANGE ||
                               VRGlobals.blockRightJoystick ||
                               // on a mounted gun the hands turn it, the body stays on it
                               Source.Player.Interactions.MountedGun.Active;
            
            if (disableTurn)
            {
                axis[axisIndex] = 0;
                return;
            }

            float turnScale = 1f;
            Vector2 stick = VRInput.RightJoystick.axis;
            if (scopeTurnGuard && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.ScopeZoomOwnsStick)
            {
                if (Mathf.Abs(stick.y) > scopeZoomPush && Mathf.Abs(stick.y) > Mathf.Abs(stick.x))
                    zoomPushSeen = true;
                else if (stick.magnitude < stickCentre)
                    zoomPushSeen = false;
                float sideways = Mathf.Abs(stick.x);
                float offSideways = Mathf.Atan2(Mathf.Abs(stick.y), sideways) * Mathf.Rad2Deg;
                if (zoomPushSeen || offSideways > scopeTurnMaxAngle || sideways <= scopeTurnDeadzone)
                {
                    axis[axisIndex] = 0;
                    return;
                }
                turnScale = (sideways - scopeTurnDeadzone) / Mathf.Max(1f - scopeTurnDeadzone, 1e-3f) / sideways;
            }
            else
            {
                zoomPushSeen = false;
            }

            if (VRSettings.GetRotationType() == VRSettings.RotationMode.Smooth)
            {
                axis[axisIndex] = VRInput.RightJoystick.axis.x * VRSettings.GetRotationSensitivity() * turnScale;
            }
            else
            {
                ProcessSnapTurn(ref axis, axisIndex);
            }

            // Apply rotation to camera root
            if (VRGlobals.camRoot != null)
            {
                float rotationSpeed = VRSettings.GetRotationSensitivity() * 5;
                VRGlobals.camRoot.transform.Rotate(0, axis[axisIndex] * rotationSpeed * Time.deltaTime, 0);
            }
        }
        
        private static void ProcessSnapTurn(ref float[] axis, int axisIndex)
        {
            if (!_snapTurned && Mathf.Abs(VRInput.RightJoystick.axis.x) > JOYSTICK_DEADZONE)
            {
                _snapTurned = true;
                float snapTurnAmount = (float)VRSettings.GetSnapTurnAmount();
                
                if (VRInput.RightJoystick.axis.x < 0)
                {
                    snapTurnAmount *= -1;
                }
                
                axis[axisIndex] = snapTurnAmount;
            }
        }
        
        private static void ProcessMovementInput(InputPreset instance, ref float[] axis, int joystickAxis, int axisIndex)
        {
            if (VRGlobals.blockLeftJoystick)
            {
                return;
            }
            // On a mounted gun you don't walk (getting off is letting go of it)
            if (Source.Player.Interactions.MountedGun.Active)
            {
                axis[axisIndex] = 0f;
                return;
            }
            
            float threshold = VRSettings.GetLeftStickSensitivity();
            Vector2 joystickInput = VRInput.LeftJoystick.axis;
            
            if (joystickAxis == 0 && Mathf.Abs(joystickInput.x) > threshold)
            {
                axis[axisIndex] = joystickInput.x; // Horizontal movement
            }
            else if (joystickAxis == 1 && Mathf.Abs(joystickInput.y) > threshold)
            {
                axis[axisIndex] = joystickInput.y; // Vertical movement
            }
        }    
    }
}