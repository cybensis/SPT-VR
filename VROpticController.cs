using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;
using Valve.VR;

namespace TarkovVR
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
        public void initZoomDial() {
            if (scopeCamera) {
                fovAtStart = scopeCamera.fieldOfView;
                initialHandRot = SteamVR_Actions._default.LeftHandPose.GetLocalRotation(SteamVR_Input_Sources.LeftHand);
                swapZooms = false;
            }
        }

        public void handleZoomDial()
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
                    currentFov = fovAtStart + (fovDif * fovScaler);
                }
                if (degreesHandTurned < 0)
                {
                    float fovDif = fovAtStart - minFov;
                    currentFov = fovAtStart + (fovDif * fovScaler);
                }

                currentFov = Mathf.Clamp(currentFov, minFov, maxFov);

                if (currentFov >= maxFov || currentFov <= minFov)
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);

                if ((scopeCamera.fieldOfView / maxFov < 0.5 && currentFov / maxFov  >= 0.5 ) || (scopeCamera.fieldOfView / maxFov  >= 0.5 && currentFov / maxFov < 0.5))
                    swapZooms = true;

                scopeCamera.fieldOfView = currentFov;
            }
        }
    }
}
