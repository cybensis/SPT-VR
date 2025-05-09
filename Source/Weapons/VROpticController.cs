using EFT.InputSystem;
using EFT.InventoryLogic;
using Sirenix.Serialization;
using System;
using System.Linq;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using static TarkovVR.Source.Controls.InputHandlers;

namespace TarkovVR.Source.Weapons
{
    internal class VROpticController : MonoBehaviour
    {
        public Camera scopeCamera;

        public float minFov;
        public float maxFov;
        public float maxScopeTurnDegree = 80f;
        private Quaternion initialHandRot;
        public float currentFov;
        public string currentScope;
        private float fovAtStart;
        public bool swapZooms;
        private InputHandlers.ScopeZoomHandler scopeZoomHandler;

        public void Awake() {
            IInputHandler baseHandler;
            VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ChangeScopeMagnification, out baseHandler);
            if (baseHandler != null)
            {
                scopeZoomHandler = (InputHandlers.ScopeZoomHandler)baseHandler;
            }
        }
        public void initZoomDial()
        {
            if (scopeCamera)
            {
                fovAtStart = scopeCamera.fieldOfView;
                initialHandRot = SteamVR_Actions._default.LeftHandPose.GetLocalRotation(SteamVR_Input_Sources.LeftHand);
                swapZooms = false;
            }


        }


        public void changeScopeMode() {
            if (VRGlobals.scope && VRGlobals.scope.parent.GetComponent<SightModVisualControllers>() != null)
            {
                SightComponent sightComponent = VRGlobals.scope.parent.GetComponent<SightModVisualControllers>().sightComponent_0;
                int scopeIndex = sightComponent.SelectedScopeIndex;
                int maxScopeModes = sightComponent.GetScopeModesCount(scopeIndex);
                int nextScopeMode = (sightComponent.SelectedScopeMode + 1) % maxScopeModes;
                FirearmScopeStateStruct scopeState = new FirearmScopeStateStruct();
                scopeState.ScopeMode = nextScopeMode;
                scopeState.Id = sightComponent.Item.Id;

                VRGlobals.firearmController.SetScopeMode(new FirearmScopeStateStruct[] { scopeState });
            //if (VRGlobals.scope.name == "scope_all_eotech_hhs_1_tan(Clone)" && VRGlobals.scope.GetComponent<SightModVisualControllers>().sightComponent_0.SelectedScopeMode == 1)
            //{
            //}
            }
        }
        //Joystick zoom redone to be smooth zoom all the way in and out only with variable scopes
        //Other scopes like the Elcan will switch between its two zooms
        public void handleJoystickZoomDial()
        {
            // Early return checks
            if (!scopeCamera) return;

            float primaryHandJoystickYAxis = VRSettings.GetLeftHandedMode() ?
                SteamVR_Actions._default.LeftJoystick.axis.y :
                SteamVR_Actions._default.RightJoystick.axis.y;

            if (Mathf.Abs(primaryHandJoystickYAxis) < VRSettings.GetRightStickSensitivity() ||
                VRGlobals.scope.parent.name == "scope_all_eotech_hhs_1_tan(Clone)")
                return;

            // Cache the current camera FOV for threshold checking
            float previousFov = scopeCamera.fieldOfView;

            // Check for smooth zoom scope
            bool isVariableZoomScope = Array.IndexOf(new string[] {
                "scope_30mm_eotech_vudu_1_6x24(Clone)",
                "scope_30mm_razor_hd_gen_2_1_6x24(Clone)",
                "scope_30mm_s&b_pm_ii_1_8x24(Clone)",
                "scope_34mm_s&b_pm_ii_5_25x56(Clone)",
                "scope_30mm_burris_fullfield_tac30_1_4x24(Clone)",
                "scope_30mm_sig_tango6t_1_6x24(Clone)"
            }, VRGlobals.scope.parent.name) >= 0;

            // Deadzone threshold
            const float deadzone = 0.1f;
            float variableZoomSensitivity = VRSettings.GetVariableZoomSensitivity();

            // Handle smooth zoom scopes
            if (isVariableZoomScope)
            {
                // Only modify FOV when stick movement exceeds threshold
                if (Mathf.Abs(primaryHandJoystickYAxis) > deadzone)
                {
                    // Apply sensitivity to the zoom speed
                    float zoomDelta = primaryHandJoystickYAxis * variableZoomSensitivity;
                    currentFov -= zoomDelta;
                    currentFov = Mathf.Clamp(currentFov, minFov, maxFov);
                }
            }
            // Handle regular scopes
            else
            {
                bool isZoomedIn = (previousFov <= minFov + 0.1f);
                bool isZoomedOut = (previousFov >= maxFov - 0.1f);

                // Pushing up to zoom in
                if (primaryHandJoystickYAxis > deadzone && !isZoomedIn)
                {
                    currentFov = minFov;
                }
                // Pushing down to zoom out
                else if (primaryHandJoystickYAxis < -deadzone && !isZoomedOut)
                {
                    currentFov = maxFov;
                }
            }

            // Check for threshold crossing that triggers zoom swap
            if ((previousFov / maxFov < 0.3 && currentFov / maxFov >= 0.3) ||
                (previousFov / maxFov >= 0.3 && currentFov / maxFov < 0.3))
            {
                if (scopeZoomHandler != null)
                    scopeZoomHandler.TriggerSwapZooms();
            }

            // Always update the camera's FOV to match current value
            scopeCamera.fieldOfView = currentFov;
        }


        public void handlePhysicalZoomDial()
        {
            if (scopeCamera)
            {

                Quaternion relativeRotation = Quaternion.Inverse(initialHandRot) * SteamVR_Actions._default.LeftHandPose.GetLocalRotation(SteamVR_Input_Sources.LeftHand);
                float degreesHandTurned = relativeRotation.eulerAngles.z;
                // Convert to signed angle (-180 to 180)
                if (degreesHandTurned > 180)
                {
                    degreesHandTurned -= 360;
                }

                degreesHandTurned = Mathf.Clamp(degreesHandTurned, maxScopeTurnDegree * -1, maxScopeTurnDegree);

                float fovScaler = degreesHandTurned / maxScopeTurnDegree;
                if (degreesHandTurned > 0)
                {
                    float fovDif = maxFov - fovAtStart;
                    currentFov = fovAtStart + fovDif * fovScaler;
                }
                if (degreesHandTurned < 0)
                {
                    float fovDif = fovAtStart - minFov;
                    currentFov = fovAtStart + fovDif * fovScaler;
                }

                currentFov = Mathf.Clamp(currentFov, minFov, maxFov);

                if (currentFov >= maxFov || currentFov <= minFov)
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand)  ;

                if (scopeCamera.fieldOfView / maxFov < 0.5 && currentFov / maxFov >= 0.5 || scopeCamera.fieldOfView / maxFov >= 0.5 && currentFov / maxFov < 0.5)
                    if (scopeZoomHandler != null)
                        scopeZoomHandler.TriggerSwapZooms();

                if (!VRGlobals.vrPlayer.isSupporting) { 
                    float normalizedValue = Mathf.InverseLerp(minFov, maxFov, currentFov);
                    float rotation = Mathf.Lerp(30, -30, normalizedValue);
                    VRGlobals.vrPlayer.LeftHand.transform.rotation = VRGlobals.handsInteractionController.scopeTransform.rotation;
                    VRGlobals.vrPlayer.LeftHand.transform.Rotate(rotation, 160, -40);
                    VRGlobals.vrPlayer.LeftHand.transform.position = VRGlobals.handsInteractionController.scopeTransform.position;
                    VRGlobals.vrPlayer.LeftHand.transform.position += (VRGlobals.handsInteractionController.scopeTransform.right * -0.17f) + (VRGlobals.handsInteractionController.scopeTransform.up * -0.02f) + (VRGlobals.handsInteractionController.scopeTransform.forward * -0.05f);
                }
                scopeCamera.fieldOfView = currentFov;
                //if (UIPatches.opticUi)
                //    UIPatches.opticUi.Show($"{(int)currentFov}x");
            }
        }
    }
}