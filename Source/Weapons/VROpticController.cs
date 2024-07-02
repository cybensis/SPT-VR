using Sirenix.Serialization;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Controls;
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
        private float fovAtStart;
        public bool swapZooms;
        private ScopeZoomHandler scopeZoomHandler;

        public void Awake() {
            IInputHandler baseHandler;
            VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.ChangeScopeMagnification, out baseHandler);
            if (baseHandler != null)
            {
                scopeZoomHandler = (ScopeZoomHandler)baseHandler;
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


        public void handleJoystickZoomDial() {
            if (scopeCamera)
            {
                currentFov -= SteamVR_Actions._default.RightJoystick.axis.y / 2;
                currentFov = Mathf.Clamp(currentFov, minFov, maxFov);
                if (scopeCamera.fieldOfView / maxFov < 0.5 && currentFov / maxFov >= 0.5 || scopeCamera.fieldOfView / maxFov >= 0.5 && currentFov / maxFov < 0.5)
                    if (scopeZoomHandler != null)
                        scopeZoomHandler.TriggerSwapZooms();

                scopeCamera.fieldOfView = currentFov;
            }
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
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);

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