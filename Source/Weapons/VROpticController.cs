using EFT.InputSystem;
using EFT.InventoryLogic;
using Sirenix.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TarkovVR.Patches.UI;
using TarkovVR.Patches.Visuals;
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
        private bool hasToggledZoom = false;
        private GameObject sightRaycastReciever;

        public void Awake() {
            IInputHandler baseHandler;
            VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ChangeScopeMagnification, out baseHandler);
            if (baseHandler != null)
            {
                scopeZoomHandler = (InputHandlers.ScopeZoomHandler)baseHandler;
            }
        }

        private bool IsVariableZoomScope()
        {
            foreach (Transform scope in VRGlobals.scopes)
            {
                //Plugin.MyLog.LogInfo($"Checking scope: {scope?.parent.name}");
                if (scope != null && scope.parent != null && (ScopeManager.IsVariableZoom(scope.parent.name) || ScopeManager.IsVariableZoom(scope.parent.parent.name)))
                    return true;
            }
            return false;
        }

        public bool SetScopeSensitivity()
        {
            foreach (Transform scope in VRGlobals.scopes)
            {
                if (scope != null && scope.parent != null)
                {
                    var visualController = scope.parent.GetComponent<SightModVisualControllers>();
                    if (visualController != null)
                    {
                        SightComponent sightComponent = visualController.sightComponent_0;
                        if (sightComponent != null)
                        {
                            float zoomRatio = Mathf.InverseLerp(minFov, maxFov, currentFov);
                            float magnificationFactor = maxFov / minFov;
                            float minSensitivity = 0.1f / Mathf.Sqrt(magnificationFactor);
                            float maxSensitivity = 0.1f;
                            if (VRSettings.SmoothScopeAim())
                            {
                                // Use a cubic curve for faster ramp-up at higher zoom
                                // Lower zoom ratio = more zoomed in = more smoothing (lower sensitivity)
                                VRGlobals.scopeSensitivity = Mathf.Lerp(minSensitivity, maxSensitivity, zoomRatio * zoomRatio * zoomRatio);
                            }
                            else
                                VRGlobals.scopeSensitivity = visualController.sightComponent_0.GetCurrentSensitivity;
                            //Plugin.MyLog.LogError($"Setting scope sensitivity to {VRGlobals.scopeSensitivity} for scope {scope.parent.name} (FOV: {currentFov}/{minFov}-{maxFov}, Mag: {magnificationFactor:F1}x, Ratio: {zoomRatio:F2})");
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        public void initZoomDial()
        {

            if (scopeCamera)
            {
                fovAtStart = scopeCamera.fieldOfView;
                initialHandRot = SteamVR_Actions._default.LeftHandPose.GetLocalRotation(SteamVR_Input_Sources.LeftHand);
                swapZooms = false;
                if (scopeCamera.GetComponent<VRJitterComponent>() == null)
                    scopeCamera.gameObject.AddComponent<VRJitterComponent>();
            }


        }

        public void changeScopeMode()
        {
            if (VRGlobals.scopes == null || VRGlobals.scopes.Count == 0)
                return;

            // Find all scopes that have multiple modes
            List<FirearmScopeStateStruct> scopeStates = new List<FirearmScopeStateStruct>();

            foreach (Transform scope in VRGlobals.scopes)
            {
                if (scope == null || scope.parent == null)
                    continue;

                var visualController = scope.parent.GetComponent<SightModVisualControllers>();
                if (visualController == null)
                    continue;

                SightComponent sightComponent = visualController.sightComponent_0;
                if (sightComponent == null)
                    continue;

                int scopeIndex = sightComponent.SelectedScopeIndex;
                int maxScopeModes = sightComponent.GetScopeModesCount(scopeIndex);
                
                // Only change mode if the scope has multiple modes
                if (maxScopeModes > 1)
                {
                    int nextScopeMode = (sightComponent.SelectedScopeMode + 1) % maxScopeModes;

                    FirearmScopeStateStruct scopeState = new FirearmScopeStateStruct
                    {
                        ScopeMode = nextScopeMode,
                        Id = sightComponent.Item.Id
                    };

                    scopeStates.Add(scopeState);
                }
            }

            if (scopeStates.Count > 0)
            {
                VRGlobals.firearmController.SetScopeMode(scopeStates.ToArray());
            }
        }
        //Joystick zoom redone to be smooth zoom all the way in and out only with variable scopes
        //Other scopes like the Elcan will switch between its two zooms
        public void handleJoystickZoomDial()
        {
            if (!scopeCamera) return;

            float primaryHandJoystickYAxis = VRSettings.GetLeftHandedMode() ?
                SteamVR_Actions._default.LeftJoystick.axis.y :
                SteamVR_Actions._default.RightJoystick.axis.y;

            if (Mathf.Abs(primaryHandJoystickYAxis) < VRSettings.GetRightStickSensitivity() ||
                VRGlobals.scopes.Any(s => s != null && s.parent != null && s.parent.name.Contains("scope_all_eotech_hhs_1")))
                return;

            // Cache the current camera FOV for scope crosshair trigger
            float previousFov = scopeCamera.fieldOfView;            

            const float deadzone = 0.1f;
            float variableZoomSensitivity = VRSettings.GetVariableZoomSensitivity();

            // Handle smooth zoom scopes
            if (IsVariableZoomScope())
            {
                if (Mathf.Abs(primaryHandJoystickYAxis) > deadzone)
                {
                    // Apply sensitivity to the zoom speed
                    float zoomDelta = primaryHandJoystickYAxis * variableZoomSensitivity;
                    currentFov -= zoomDelta;
                    currentFov = Mathf.Clamp(currentFov, minFov, maxFov);
                }
            }
            // Handle non-smooth zoom scopes
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

            // Check for threshold crossing that triggers zoom swap (changes crosshair if scope has different one)
            if ((previousFov / maxFov < 0.3 && currentFov / maxFov >= 0.3) ||
                (previousFov / maxFov >= 0.3 && currentFov / maxFov < 0.3))
            {
                if (scopeZoomHandler != null)
                    scopeZoomHandler.TriggerSwapZooms();
            }
            SetScopeSensitivity();
            // Always update the camera's FOV to match current value
            scopeCamera.fieldOfView = currentFov;
        }
        public void handlePhysicalZoomDial()
        {
            if (VRGlobals.scopes.Any(s => s != null && s.parent != null && s.parent.name.Contains("scope_all_eotech_hhs_1")))
                return;

            if (!scopeCamera)
                return;

            if (IsVariableZoomScope())
            {
                Quaternion relativeRotation = Quaternion.Inverse(initialHandRot) *
                    SteamVR_Actions._default.LeftHandPose.GetLocalRotation(SteamVR_Input_Sources.LeftHand);

                float degreesHandTurned = relativeRotation.eulerAngles.z;
                if (degreesHandTurned > 180) degreesHandTurned -= 360;
                degreesHandTurned = Mathf.Clamp(degreesHandTurned, -maxScopeTurnDegree, maxScopeTurnDegree);

                float fovScaler = degreesHandTurned / maxScopeTurnDegree;

                if (degreesHandTurned > 0)
                {
                    float fovDif = maxFov - fovAtStart;
                    currentFov = fovAtStart + fovDif * fovScaler;
                }
                else if (degreesHandTurned < 0)
                {
                    float fovDif = fovAtStart - minFov;
                    currentFov = fovAtStart + fovDif * fovScaler;
                }

                currentFov = Mathf.Clamp(currentFov, minFov, maxFov);

                if (currentFov >= maxFov || currentFov <= minFov)
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f,
                        VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                }

                if (scopeCamera.fieldOfView / maxFov < 0.5 && currentFov / maxFov >= 0.5 ||
                    scopeCamera.fieldOfView / maxFov >= 0.5 && currentFov / maxFov < 0.5)
                {
                    scopeZoomHandler?.TriggerSwapZooms();
                }

                if (!VRGlobals.vrPlayer.isSupporting)
                {
                    float normalizedValue = Mathf.InverseLerp(minFov, maxFov, currentFov);
                    float rotation = Mathf.Lerp(30, -30, normalizedValue);
                    VRGlobals.vrPlayer.LeftHand.transform.rotation = VRGlobals.handsInteractionController.scopeTransform.rotation;
                    VRGlobals.vrPlayer.LeftHand.transform.Rotate(rotation, 160, -40);
                    VRGlobals.vrPlayer.LeftHand.transform.position = VRGlobals.handsInteractionController.scopeTransform.position;
                    VRGlobals.vrPlayer.LeftHand.transform.position +=
                        (VRGlobals.handsInteractionController.scopeTransform.right * -0.17f) +
                        (VRGlobals.handsInteractionController.scopeTransform.up * -0.02f) +
                        (VRGlobals.handsInteractionController.scopeTransform.forward * -0.05f);
                }

                scopeCamera.fieldOfView = currentFov;
            }
            else
            {
                Quaternion relativeRotation = Quaternion.Inverse(initialHandRot) *
                    SteamVR_Actions._default.LeftHandPose.GetLocalRotation(SteamVR_Input_Sources.LeftHand);

                float degreesHandTurned = relativeRotation.eulerAngles.z;
                if (degreesHandTurned > 180) degreesHandTurned -= 360;

                const float TOGGLE_THRESHOLD = 20f;

                if (!hasToggledZoom)
                {
                    if (degreesHandTurned > TOGGLE_THRESHOLD)
                    {
                        scopeCamera.fieldOfView = minFov; // zoom in
                        hasToggledZoom = true;
                        SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f,
                            VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        scopeZoomHandler?.TriggerSwapZooms();
                    }
                    else if (degreesHandTurned < -TOGGLE_THRESHOLD)
                    {
                        scopeCamera.fieldOfView = maxFov; // zoom out
                        hasToggledZoom = true;
                        SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f,
                            VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        scopeZoomHandler?.TriggerSwapZooms();
                    }
                }
                else
                {
                    // Reset toggle once hand returns near center
                    if (Mathf.Abs(degreesHandTurned) < 10f)
                        hasToggledZoom = false;
                }
            }
            SetScopeSensitivity();
        }
    }
}