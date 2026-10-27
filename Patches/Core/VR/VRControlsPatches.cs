using EFT.InputSystem;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Player.VRManager;
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
            if (_snapTurned && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) < JOYSTICK_DEADZONE)
            {
                _snapTurned = false;
            }

            bool disableTurn = (WeaponPatches.currentGunInteractController && 
                               WeaponPatches.currentGunInteractController.highlightingMesh) || 
                               Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.y) >= JUMP_OR_STAND_CLAMP_RANGE || 
                               VRGlobals.blockRightJoystick;
            
            if (disableTurn)
            {
                axis[axisIndex] = 0;
                return;
            }

            if (VRSettings.GetRotationType() == VRSettings.RotationMode.Smooth)
            {
                // Smooth rotation
                axis[axisIndex] = SteamVR_Actions._default.RightJoystick.axis.x * VRSettings.GetRotationSensitivity();
            }
            else
            {
                // Snap rotation
                ProcessSnapTurn(ref axis, axisIndex);
            }

            // Apply rotation to camera root
            if (VRGlobals.camRoot != null)
            {
                //VRGlobals.camRoot.transform.Rotate(0, axis[axisIndex], 0);
                float rotationSpeed = VRSettings.GetRotationSensitivity() * 5;
                VRGlobals.camRoot.transform.Rotate(0, axis[axisIndex] * rotationSpeed * Time.deltaTime, 0);
            }
        }
        
        private static void ProcessSnapTurn(ref float[] axis, int axisIndex)
        {
            if (!_snapTurned && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) > JOYSTICK_DEADZONE)
            {
                _snapTurned = true;
                float snapTurnAmount = (float)VRSettings.GetSnapTurnAmount();
                
                if (SteamVR_Actions._default.RightJoystick.axis.x < 0)
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
            
            float threshold = VRSettings.GetLeftStickSensitivity();
            Vector2 joystickInput = SteamVR_Actions._default.LeftJoystick.axis;
            
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