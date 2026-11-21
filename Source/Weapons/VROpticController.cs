using EFT;
using EFT.InputSystem;
using System.Collections.Generic;
using System.Linq;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using static TarkovVR.Source.Controls.InputHandlers;

namespace TarkovVR.Source.Weapons
{
    internal class VROpticController : MonoBehaviour
    {
        // Scope state
        public Camera scopeCamera;
        public float minFov;
        public float maxFov;
        public bool isAdjustable;
        public bool isSmoothZoom;
        public ScopeZoomHandler baseZoomHandler;

        // Tunables
        public float maxScopeTurnDegree = 80f;
        public bool enableMechanicalCatch = true;
        public float mechanicalCatchStrength = 0.15f;
        public float joystickZoomSpeed = 3f;
        // How strongly the visual grip hand follows the real wrist twist. 1 = exact 1:1.
        public float dialHandFollowScale = 1f;

        // Physical zoom state
        public enum PhysicalZoomState { Idle, Gripping }
        private PhysicalZoomState physicalState = PhysicalZoomState.Idle;
        private Quaternion trackingToWorldSpaceMatrix;
        private float initialScopeSpaceTwist;
        private float fovAtStart;

        // Active zoom output (shared between physical and joystick paths)
        private float physicalCurrentFov;
        private float physicalTargetFov;
        private float physicalFovVelocity;

        // Live measured wrist twist (deg) while gripping — drives the visual grip-hand rotation
        private float physicalDialTwist;

        // Joystick state
        private bool joystickZooming;
        private float joystickRawMag;
        private bool pushedUp;
        private bool pushedDown;

        // Discrete dial state
        private bool hasToggledZoom;

        // Detent + limit haptic state
        private bool isCaught;
        private bool wasAtLimit;
        private bool hapticsInitialized;
        private float lastDetentTime;
        private float detentKickTime;
        private float detentKickDirection;
        private const float DetentCooldown = 0.05f;
        private const float DetentKickDuration = 0.12f;
        private const float DetentKickAmplitude = 4f;

        // Wired from base game input handler
        private VRScopeZoomHandler scopeZoomHandler;

        private const float SmoothTime = 0.04f;
        private const float TwistRange = 80f;

        // Haptic plays on whichever hand is performing the zoom action
        private SteamVR_Input_Sources HapticHand =>
            joystickZooming
                ? (Handedness.GunHandSource)
                : (Handedness.OffHandSource);

        public void Awake()
        {
            if (VRInputManager.inputHandlers.TryGetValue(ECommand.ChangeScopeMagnification, out var baseHandler))
                scopeZoomHandler = (VRScopeZoomHandler)baseHandler;
        }

        public void Update()
        {
            if (!scopeCamera || !isSmoothZoom) return;

            float prevFov = scopeCamera.fieldOfView;

            if (physicalState == PhysicalZoomState.Gripping || joystickZooming)
            {
                physicalCurrentFov = Mathf.SmoothDamp(physicalCurrentFov, physicalTargetFov,
                                                      ref physicalFovVelocity, SmoothTime);
                physicalCurrentFov = Mathf.Clamp(physicalCurrentFov, minFov, maxFov);
                scopeCamera.fieldOfView = physicalCurrentFov;

                if (baseZoomHandler != null)
                {
                    baseZoomHandler._filedOfView = physicalCurrentFov;
                    baseZoomHandler.float_2 = physicalCurrentFov;
                    baseZoomHandler.method_11();
                    PersistZoomValue(physicalCurrentFov);
                }
            }
            else if (baseZoomHandler != null)
            {
                scopeCamera.fieldOfView = baseZoomHandler.FiledOfView;
            }

            if (!Mathf.Approximately(prevFov, scopeCamera.fieldOfView))
                ProcessZoomHaptics(scopeCamera.fieldOfView);
        }

        public void BeginPhysicalZoom()
        {
            if (!scopeCamera || physicalState == PhysicalZoomState.Gripping) return;

            joystickZooming = false;

            var zoomSource = Handedness.OffHandSource;
            Transform scopeXform = VRGlobals.handsInteractionController?.scopeTransform;

            if (scopeXform != null && VRGlobals.vrPlayer?.LeftHand != null)
            {
                // Use the off hand's own pose action: LeftHandPose read for the right controller returns identity (gun in the left
                // hand, or left-handed mode)
                Quaternion localHandRot = Handedness.OffHandPose.GetLocalRotation(zoomSource);
                Quaternion worldHandRot = VRGlobals.vrPlayer.LeftHand.transform.rotation;
                trackingToWorldSpaceMatrix = worldHandRot * Quaternion.Inverse(localHandRot);

                Vector3 handUpInScopeSpace = (Quaternion.Inverse(scopeXform.rotation) * worldHandRot) * Vector3.up;
                initialScopeSpaceTwist = Mathf.Atan2(handUpInScopeSpace.x, handUpInScopeSpace.y) * Mathf.Rad2Deg;
            }

            fovAtStart = scopeCamera.fieldOfView;
            physicalCurrentFov = fovAtStart;
            physicalTargetFov = fovAtStart;
            physicalFovVelocity = 0f;
            isCaught = false;
            hapticsInitialized = false;
            physicalState = PhysicalZoomState.Gripping;
        }

        public void EndPhysicalZoom()
        {
            if (physicalState != PhysicalZoomState.Gripping) return;

            physicalState = PhysicalZoomState.Idle;

            if (baseZoomHandler != null)
            {
                baseZoomHandler._filedOfView = physicalCurrentFov;
                baseZoomHandler.float_2 = physicalCurrentFov;
                baseZoomHandler.float_4 = physicalCurrentFov;
                baseZoomHandler.method_8();
                PersistZoomValue(physicalCurrentFov);
            }

            hasToggledZoom = false;
            isCaught = false;
        }

        public void TickPhysicalZoom()
        {
            if (physicalState != PhysicalZoomState.Gripping) return;
            if (!scopeCamera) return;
            if (VRGlobals.scopes.Any(s => s != null && s.parent != null
                                           && s.parent.name.Contains("scope_all_eotech_hhs_1")))
                return;

            Transform scopeXform = VRGlobals.handsInteractionController?.scopeTransform;
            if (scopeXform == null) return;

            var zoomSource = Handedness.OffHandSource;
            Quaternion localHandRot = Handedness.OffHandPose.GetLocalRotation(zoomSource);
            Quaternion currentWorldHandRot = trackingToWorldSpaceMatrix * localHandRot;

            Vector3 handUpInScopeSpace = (Quaternion.Inverse(scopeXform.rotation) * currentWorldHandRot) * Vector3.up;
            float currentTwist = Mathf.Atan2(handUpInScopeSpace.x, handUpInScopeSpace.y) * Mathf.Rad2Deg;
            float twist = Mathf.DeltaAngle(initialScopeSpaceTwist, currentTwist);

            // Cache the live twist (clamped to the dial's travel) for the visual hand pose
            physicalDialTwist = Mathf.Clamp(twist, -TwistRange, TwistRange);

            if (isSmoothZoom)
            {
                twist = Mathf.Clamp(twist, -TwistRange, TwistRange);

                float t = Mathf.Abs(twist) / TwistRange;
                float invStart = 1f / fovAtStart;
                float invEnd = (twist >= 0f) ? (1f / minFov) : (1f / maxFov);
                float invFov = Mathf.Lerp(invStart, invEnd, t);
                float rawTargetFov = Mathf.Clamp(1f / invFov, minFov, maxFov);

                ApplyMechanicalCatch(rawTargetFov);
            }
            else
            {
                const float TOGGLE_THRESHOLD = 20f;
                if (!hasToggledZoom && Mathf.Abs(twist) > TOGGLE_THRESHOLD)
                {
                    scopeZoomHandler?.TriggerSwapZooms();
                    hasToggledZoom = true;
                    HapticMajorDetent();
                }
                else if (hasToggledZoom && Mathf.Abs(twist) < 10f)
                {
                    hasToggledZoom = false;
                }
            }

            SetScopeSensitivity();
        }

        // True when the sight being aimed through is an optic that can change magnification (a variable ring or zoom steps).
        // scopeCamera stays set after a scope was last used, so the aimed sight is checked too (irons or a red dot on a scoped gun).
        // Not the HHS-1, which the stick doesn't zoom.
        internal bool CanZoomNow
        {
            get
            {
                if (!scopeCamera || !(isSmoothZoom || isAdjustable))
                    return false;
                var currentScope = VRGlobals.player != null ? VRGlobals.player.ProceduralWeaponAnimation?.CurrentScope : null;
                if (currentScope == null || !currentScope.IsOptic)
                    return false;
                return VRGlobals.scopes == null || !VRGlobals.scopes.Any(s => s != null && s.parent != null && s.parent.name.Contains("scope_all_eotech_hhs_1"));
            }
        }

        public void handleJoystickZoomDial()
        {
            if (!scopeCamera) return;

            // The turning stick's up/down zooms; jump and crouch pause while in the scope. The movement stick is never used.
            float input = TarkovVR.Patches.Core.VR.VRControlsPatches.ScopeZoomAxis();

            if (VRGlobals.scopes.Any(s => s != null && s.parent != null
                                           && s.parent.name.Contains("scope_all_eotech_hhs_1")))
            {
                joystickZooming = false;
                return;
            }

            const float deadzone = 0.1f;

            if (isSmoothZoom)
            {
                if (Mathf.Abs(input) > deadzone && physicalState == PhysicalZoomState.Idle)
                {
                    if (!joystickZooming)
                    {
                        joystickZooming = true;
                        physicalCurrentFov = scopeCamera.fieldOfView;
                        physicalTargetFov = physicalCurrentFov;
                        physicalFovVelocity = 0f;
                        joystickRawMag = maxFov / physicalCurrentFov;
                        isCaught = false;
                        hapticsInitialized = false;
                    }

                    joystickRawMag += input * VRSettings.GetVariableZoomSensitivity() * joystickZoomSpeed * 4f * Time.deltaTime;
                    joystickRawMag = Mathf.Clamp(joystickRawMag, 1f, maxFov / minFov);
                    ApplyMechanicalCatch(maxFov / joystickRawMag);
                }
                else if (joystickZooming)
                {
                    joystickZooming = false;
                    if (physicalState == PhysicalZoomState.Idle && baseZoomHandler != null)
                    {
                        baseZoomHandler._filedOfView = physicalCurrentFov;
                        baseZoomHandler.float_2 = physicalCurrentFov;
                        baseZoomHandler.float_4 = physicalCurrentFov;
                        baseZoomHandler.method_8();
                        PersistZoomValue(physicalCurrentFov);
                    }
                }
            }
            else
            {
                if (input > deadzone && !pushedUp)
                {
                    scopeZoomHandler?.TriggerSwapZooms();
                    pushedUp = true; pushedDown = false;
                    HapticMajorDetent();
                }
                else if (input < -deadzone && !pushedDown)
                {
                    scopeZoomHandler?.TriggerSwapZooms();
                    pushedDown = true; pushedUp = false;
                    HapticMajorDetent();
                }
                else if (Mathf.Abs(input) < deadzone)
                {
                    pushedUp = false; pushedDown = false;
                }
            }

            SetScopeSensitivity();
        }

        public void changeScopeMode()
        {
            if (VRGlobals.scopes == null || VRGlobals.scopes.Count == 0) return;

            var scopeStates = new List<ScopeState>();
            foreach (Transform scope in VRGlobals.scopes)
            {
                if (scope?.parent == null) continue;
                var sightComponent = scope.parent.GetComponent<SightModVisualControllers>()?._sightMod;
                if (sightComponent == null) continue;

                int scopeIndex = sightComponent.SelectedScopeIndex;
                int maxScopeModes = sightComponent.GetScopeModesCount(scopeIndex);
                if (maxScopeModes > 1)
                {
                    scopeStates.Add(new ScopeState
                    {
                        ScopeMode = (sightComponent.SelectedScopeMode + 1) % maxScopeModes,
                        Id = sightComponent.Item.Id
                    });
                }
            }
            if (scopeStates.Count > 0)
                VRGlobals.firearmController.SetScopeMode(scopeStates.ToArray());
        }

        public void UpdateGripHandPose()
        {
            if (physicalState != PhysicalZoomState.Gripping) return;
            if (VRGlobals.vrPlayer.isSupporting) return;
            if (VRGlobals.handsInteractionController == null
                || VRGlobals.handsInteractionController.heldItem != null) return;

            // The real wrist twist, not the smoothed FOV value: the grip hand turns 1:1 with the hand instead of lagging the zoom curve
            float rotation = physicalDialTwist * dialHandFollowScale;

            // Decaying kick from detent escape — brief overshoot in motion direction
            float kickElapsed = Time.time - detentKickTime;
            if (kickElapsed < DetentKickDuration)
            {
                float decay = 1f - (kickElapsed / DetentKickDuration);
                rotation += decay * decay * DetentKickAmplitude * detentKickDirection;
            }

            // Raw twist turns the hand opposite the real wrist, so flip it
            rotation = -rotation;

            var leftHand = VRGlobals.vrPlayer.LeftHand.transform;
            var scopeXform = VRGlobals.handsInteractionController.scopeTransform;

            leftHand.rotation = scopeXform.rotation;
            leftHand.Rotate(Handedness.GunInLeftHand ? rotation * rightHandDialTwistSign : rotation, 160, -40);
            leftHand.position = scopeXform.position
                + (scopeXform.right * -0.17f)
                + (scopeXform.up * -0.02f)
                + (scopeXform.forward * -0.05f);
            PlaceRightHandOnDial(scopeXform, leftHand.position, leftHand.rotation);
        }

        // With the gun in the left hand, the right hand grips the dial. Its arm targets the left hand's dial pose mirrored across the
        // gun's centre plane (RealLeftHandedPatches uses it as that arm's target while it is set; in that mode the rig's LeftHand,
        // which the left arm follows, tracks the right controller). The mirror reverses the turn about axes in that plane: if the
        // right hand on the dial turns against the wrist, flip rightHandDialTwistSign.
        public static float rightHandDialTwistSign = 1f;
        private Transform rightHandDial;
        private int rightHandDialFrame = -10;

        public Transform RightHandDialTarget => rightHandDial != null && Time.frameCount - rightHandDialFrame <= 1 ? rightHandDial : null;

        private void PlaceRightHandOnDial(Transform scope, Vector3 leftPosition, Quaternion leftRotation)
        {
            if (!Handedness.GunInLeftHand || !TarkovVR.Patches.Core.Player.RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                return;
            if (!TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(leftRotation, planeNormal, rightToLeft: false, out Quaternion rightRotation))
                return;
            if (rightHandDial == null)
                rightHandDial = new GameObject("VRRightHandDial").transform;
            // On the scope, so it rides the gun if that moves later this frame
            if (rightHandDial.parent != scope)
                rightHandDial.SetParent(scope, worldPositionStays: false);
            rightHandDial.SetPositionAndRotation(TarkovVR.Patches.Core.Player.RealLeftHandedPatches.ReflectPoint(leftPosition, planePoint, planeNormal), rightRotation);
            rightHandDialFrame = Time.frameCount;
        }

        public bool SetScopeSensitivity()
        {
            foreach (Transform scope in VRGlobals.scopes)
            {
                if (scope?.parent == null) continue;
                var sightComponent = scope.parent.GetComponent<SightModVisualControllers>()?._sightMod;
                if (sightComponent == null) continue;

                if (isSmoothZoom && VRSettings.SmoothScopeAim())
                {
                    float currentFov = scopeCamera ? scopeCamera.fieldOfView : maxFov;
                    float zoomRatio = Mathf.InverseLerp(minFov, maxFov, currentFov);
                    float magnificationFactor = maxFov / minFov;
                    float minSensitivity = 0.1f / Mathf.Sqrt(magnificationFactor);
                    float maxSensitivity = 0.1f;
                    VRGlobals.scopeSensitivity = Mathf.Lerp(minSensitivity, maxSensitivity,
                                                            zoomRatio * zoomRatio * zoomRatio);
                }
                else
                {
                    VRGlobals.scopeSensitivity = sightComponent.GetCurrentSensitivity;
                }
                return true;
            }
            return false;
        }

        // --- Internal helpers ---

        // Tarkov persists scope zoom on SightComponent.ScopeZoomValue (an item component that survives weapon swaps / item
        // use); ScopeZoomHandler.Init() reads it back on re-equip. Writing the handler's fields directly bypasses that
        // write, so mirror it here.
        private void PersistZoomValue(float fov)
        {
            if (baseZoomHandler != null && baseZoomHandler._sightComponent != null)
                baseZoomHandler._sightComponent.ScopeZoomValue = fov;
        }

        private void ApplyMechanicalCatch(float rawTargetFov)
        {
            if (!enableMechanicalCatch)
            {
                physicalTargetFov = rawTargetFov;
                return;
            }

            float rawMag = maxFov / rawTargetFov;
            float nearestMag = Mathf.Round(rawMag);
            float diff = rawMag - nearestMag;
            float absDiff = Mathf.Abs(diff);

            if (absDiff <= mechanicalCatchStrength)
            {
                // Held inside the detent
                physicalTargetFov = Mathf.Clamp(maxFov / nearestMag, minFov, maxFov);
                isCaught = true;
            }
            else
            {
                // Just escaped a detent — fire the click
                if (isCaught)
                {
                    if (Time.time - lastDetentTime > DetentCooldown)
                    {
                        lastDetentTime = Time.time;
                        HapticDetent();
                        detentKickTime = Time.time;
                        detentKickDirection = Mathf.Sign(diff);
                    }
                    isCaught = false;
                }

                float breakProgress = Mathf.InverseLerp(mechanicalCatchStrength, 0.5f, absDiff);
                float adjustedDiff = Mathf.Lerp(0f, 0.5f, breakProgress);
                float snappedMag = nearestMag + (Mathf.Sign(diff) * adjustedDiff);
                physicalTargetFov = Mathf.Clamp(maxFov / snappedMag, minFov, maxFov);
            }
        }

        private void ProcessZoomHaptics(float currentFov)
        {
            bool atLimit = currentFov <= minFov + 0.01f || currentFov >= maxFov - 0.01f;

            if (!hapticsInitialized)
            {
                wasAtLimit = atLimit;
                hapticsInitialized = true;
                return;
            }

            if (atLimit && !wasAtLimit) HapticLimit();
            wasAtLimit = atLimit;
        }

        private void FireHaptic(float duration, float frequency, float amplitude, float delay = 0f)
        {
            VRInput.Haptic.Execute(delay, duration, frequency, amplitude, HapticHand);
        }

        private void HapticDetent() => FireHaptic(0.010f, 110f, 0.4f);
        private void HapticMajorDetent() => FireHaptic(0.015f, 85f, 0.7f);
        private void HapticLimit()
        {
            FireHaptic(0.010f, 150f, 0.9f, delay: 0f);
            FireHaptic(0.030f, 65f, 0.75f, delay: 0.012f);
        }
    }
}