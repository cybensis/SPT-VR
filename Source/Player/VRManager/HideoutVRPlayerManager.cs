using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    internal class HideoutVRPlayerManager : VRPlayerManager
    {
        private Quaternion camRotation;

        protected override void Awake()
        {
            base.Awake();
            camRotation = Camera.main.transform.rotation;
            PositionLeftWristUi();
        }
        protected override void Update()
        {
            base.Update();
            if (interactionUi && (!WeaponPatches.currentGunInteractController || !WeaponPatches.currentGunInteractController.highlightingMesh))
            {

                float yRotationDifference = Mathf.Abs(Quaternion.Angle(Camera.main.transform.localRotation, camRotation));

                if (yRotationDifference > 30)
                {

                    PositionInteractionUI();
                }

            }
        }

        public override void PositionLeftWristUi()
        {

            leftWristUi.transform.SetParent(InitVRPatches.leftWrist, false);
            leftWristUi.transform.localPosition = new Vector3(-0.1f, 0.04f, 0.035f);
            leftWristUi.transform.localEulerAngles = new Vector3(304, 180, 180);

            UIPatches.healthPanel.transform.SetParent(leftWristUi.transform, false);
            UIPatches.healthPanel.transform.localPosition = Vector3.zero;
            UIPatches.healthPanel.transform.localEulerAngles = (VRSettings.GetLeftHandedMode()) ? new Vector3(270, 269, 180) : new Vector3(270, 87, 0);
            UIPatches.healthPanel.transform.localScale = new Vector3(0.0003f, 0.0003f, 0.0003f);

            UIPatches.stancePanel.transform.SetParent(leftWristUi.transform, false);
            UIPatches.stancePanel.transform.localPosition = (VRSettings.GetLeftHandedMode()) ? new Vector3(0.1f, 0, -0.075f) : new Vector3(0.1f, 0, 0.03f);
            UIPatches.stancePanel.transform.localEulerAngles = (VRSettings.GetLeftHandedMode()) ? new Vector3(90, 93, 180) : new Vector3(270, 87, 0);
            UIPatches.stancePanel.transform.localScale = new Vector3(0.0004f, 0.0004f, 0.0004f);

            UIPatches.notifierUi.transform.SetParent(leftWristUi.transform, false);
            UIPatches.notifierUi.transform.localPosition = (VRSettings.GetLeftHandedMode()) ? new Vector3(0.1247f, 0f, 0.055f) : new Vector3(0.12f, 0f, -0.085f);
            UIPatches.notifierUi.transform.localEulerAngles = (VRSettings.GetLeftHandedMode()) ? new Vector3(90, 272, 0) : new Vector3(272, 163, 283);
            UIPatches.notifierUi.transform.localScale = new Vector3(0.0003f, 0.0003f, 0.0003f);
        }
        public void PositionInteractionUI()
        {
            camRotation = Camera.main.transform.localRotation;

            // Set position not local position so it doesn't inherit rotated position from camRoot
            interactionUi.position = Camera.main.transform.position + Camera.main.transform.forward * 0.4f + Camera.main.transform.up * -0.2f + Camera.main.transform.right * 0;
            interactionUi.LookAt(Camera.main.transform);
            // Need to rotate 180 degrees otherwise it shows up backwards
            interactionUi.Rotate(0, 180, 0);
        }

    }
}
