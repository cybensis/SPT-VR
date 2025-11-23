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
        [HarmonyPatch(typeof(InputBindingsDataClass), "UpdateInput")]
        private static bool MasterVRControls(InputBindingsDataClass __instance, ref List<ECommand> commands, ref float[] axis, ref float deltaTime)
        {
            UpdateBaseInputs(__instance);
            
            if (__instance.Gclass2408_0 != null)
            {
                commands.Clear();
                VRInputManager.UpdateCommands(ref commands);
            }

            ResetAxisValues(axis);
            
            if (__instance.Gclass2409_1 == null)
            {
                return false;
            }
            
            if (VRGlobals.inGame && !VRGlobals.menuOpen)
            {
                ProcessAxisInputs(__instance, ref axis);
            }
            
            return false;
        }
        
        private static void UpdateBaseInputs(InputBindingsDataClass instance)
        {
            // Update primary input interfaces
            if (instance.Ginterface241_0 != null)
            {
                for (int i = 0; i < instance.Ginterface241_0.Length; i++)
                {
                    instance.Ginterface241_0[i].Update();
                }
            }

            // Update secondary input interfaces
            if (instance.Ginterface241_1 != null)
            {
                for (int i = 0; i < instance.Ginterface241_1.Length; i++)
                {
                    instance.Ginterface241_1[i].Update();
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
        
        private static void ProcessAxisInputs(InputBindingsDataClass instance, ref float[] axis)
        {
            for (int i = 0; i < instance.Gclass2409_1.Length; i++)
            {
                int axisIndex = instance.Gclass2409_1[i].IntAxis;
                
                // Apply default axis value if needed
                if (Mathf.Abs(axis[axisIndex]) < 0.0001f)
                {
                    axis[axisIndex] = instance.Gclass2409_1[i].GetValue();
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
        
        private static void ProcessRotationInput(InputBindingsDataClass instance, ref float[] axis, int axisIndex)
        {
            // Reset snap turn when joystick returns to center
            if (_snapTurned && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) < JOYSTICK_DEADZONE)
            {
                _snapTurned = false;
            }

            // Check conditions that disable turning
            bool disableTurn = (WeaponPatches.currentGunInteractController && 
                               WeaponPatches.currentGunInteractController.highlightingMesh) || 
                               Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.y) >= JUMP_OR_STAND_CLAMP_RANGE || 
                               VRGlobals.blockRightJoystick;
            
            if (disableTurn)
            {
                axis[axisIndex] = 0;
                return;
            }

            // Handle rotation based on rotation mode setting
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
        
        private static void ProcessMovementInput(InputBindingsDataClass instance, ref float[] axis, int joystickAxis, int axisIndex)
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