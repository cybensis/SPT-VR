using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Patches.UI;
using static TarkovVR.Source.Controls.InputHandlers;
using TarkovVR.Source.Controls;
using TarkovVR.Patches.Core.Player;

namespace TarkovVR.Source.Player.Interactions
{
    internal class HandsInteractionController : MonoBehaviour
    {
        public Quaternion initialHandRot;

        private SelectWeaponHandler selectWeaponHandler;
        public Transform scopeTransform;
        public Transform leftHand;
        private bool changingScopeZoom = false;
        private bool isInRange = false;
        private void Awake() {
            IInputHandler baseHandler;
            VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.SelectFirstPrimaryWeapon, out baseHandler);
            if (baseHandler != null)
            {
                selectWeaponHandler = (SelectWeaponHandler)baseHandler;
            }
        }
        public void Update()
        {

            if (VRGlobals.vrPlayer.isSupporting || (VRGlobals.player && VRGlobals.player.IsSprintEnabled) || VRGlobals.menuOpen)
                return;
            Collider[] nearbyColliders = Physics.OverlapSphere(VRGlobals.vrPlayer.RightHand.transform.position, 0.125f);
            foreach (Collider collider in nearbyColliders)
            {
                if (collider.gameObject.layer != 3)
                    continue;
                if (collider.gameObject.name == "backHolsterCollider")
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.RightHand);
                    if (SteamVR_Actions._default.RightGrip.stateUp)
                    {
                        selectWeaponHandler.TriggerSwapOtherPrimary();
                    }
                    if (SteamVR_Actions._default.RightGrip.state && VRGlobals.vrPlayer.radialMenu)
                    {
                        if (!VRGlobals.vrPlayer.radialMenu.active)
                        {
                            VRGlobals.vrPlayer.radialMenu.active = true;
                            VRGlobals.blockRightJoystick = true;
                        }
                    }
                }
                else if (collider.gameObject.name == "sidearmHolsterCollider")
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.RightHand);
                    if (SteamVR_Actions._default.RightGrip.stateDown)
                    {
                        selectWeaponHandler.TriggerSwapSidearm();
                    }
                }
            }

            nearbyColliders = Physics.OverlapSphere(VRGlobals.vrPlayer.LeftHand.transform.position, 0.125f);

            bool noScopeHit = true;
            foreach (Collider collider in nearbyColliders)
            {
                if (collider.gameObject.layer == 6)
                {
                    scopeTransform = collider.transform;
                    handleScopeInteraction();
                    noScopeHit = false;
                }
                else if (collider.gameObject.layer == 3 && collider.gameObject.name == "rigCollider")
                {
                    SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.4f, SteamVR_Input_Sources.LeftHand);
                    if (UIPatches.quickSlotUi && SteamVR_Actions._default.LeftGrip.stateDown)
                    {
                        UIPatches.quickSlotUi.active = true;
                    }
                }
            }
            if (changingScopeZoom)
                handleScopeInteraction() ;
            if (noScopeHit && isInRange && !SteamVR_Actions._default.LeftGrip.state)
            {
                isInRange = false;
                scopeTransform = null;
                WeaponPatches.currentGunInteractController.RemoveScopeHighlight();
            }
        }

        private void handleScopeInteraction()
        {
            if (!isInRange) {
                isInRange = true;
                if (WeaponPatches.currentGunInteractController != null && scopeTransform != null)
                {
                    WeaponPatches.currentGunInteractController.SetScopeHighlight(scopeTransform);
                }
            }

            if (SteamVR_Actions._default.LeftGrip.stateDown)
            {
                VRGlobals.vrOpticController.initZoomDial();
                changingScopeZoom = true;
            }
            if (SteamVR_Actions._default.LeftGrip.state)
            {
                VRGlobals.vrOpticController.handlePhysicalZoomDial();
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
