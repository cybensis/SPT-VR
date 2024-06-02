using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Player.VRManager;

namespace TarkovVR.Source.Player.Interactions
{
    internal class HandsInteractionController : MonoBehaviour
    {
        public Quaternion initialHandRot;

        public bool swapWeapon = false;
        private bool changingScopeZoom = false;
        public void Update()
        {

            Collider[] nearbyColliders = Physics.OverlapSphere(VRPlayerManager.RightHand.transform.position, 0.125f);
            swapWeapon = false;
            foreach (Collider collider in nearbyColliders)
            {
                if (collider.gameObject.layer == 3)
                {
                    if (!VRGlobals.vrPlayer.isSupporting)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.RightHand);
                        if (collider.gameObject.name == "backHolsterCollider" && SteamVR_Actions._default.RightGrip.state)
                        {
                            swapWeapon = true;
                        }
                    }
                }
            }

            nearbyColliders = Physics.OverlapSphere(VRPlayerManager.LeftHand.transform.position, 0.125f);

            foreach (Collider collider in nearbyColliders)
            {
                if (collider.gameObject.layer == 6)
                {
                    handleScopeInteraction();
                }
            }
            if (changingScopeZoom)
                handleScopeInteraction();
        }

        private void handleScopeInteraction()
        {
            if (SteamVR_Actions._default.LeftGrip.stateDown)
            {
                VRGlobals.vrOpticController.initZoomDial();
                changingScopeZoom = true;
            }
            if (SteamVR_Actions._default.LeftGrip.state)
            {
                VRGlobals.vrOpticController.handleZoomDial();
            }
            else
            {
                if (changingScopeZoom)
                    changingScopeZoom = false;
                SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.2f, SteamVR_Input_Sources.LeftHand);
            }
        }

    }
}
