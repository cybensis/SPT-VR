using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.UI;
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
            if (interactionUi)
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
            // Timer panel localpos: 0.047 0.08 0.025
            // local rot = 88.5784 83.1275 174.7802
            // child(0).localeuler = 0 342.1273 0

            // leftwristui localpos = -0.1 0.04 0.035
            // localrot = 304.3265 181 180

            leftWristUi.transform.parent = InitVRPatches.leftWrist;
            leftWristUi.transform.localPosition = new Vector3(-0.1f, 0.04f, 0.035f);
            leftWristUi.transform.localEulerAngles = new Vector3(304, 180, 180);


            UIPatches.healthPanel.transform.parent = leftWristUi.transform;
            UIPatches.healthPanel.transform.localPosition = Vector3.zero;
            UIPatches.healthPanel.transform.localEulerAngles = new Vector3(270, 87, 0);

            UIPatches.stancePanel.transform.parent = leftWristUi.transform;
            UIPatches.stancePanel.transform.localPosition = new Vector3(0.1f, 0, 0.03f);
            UIPatches.stancePanel.transform.localEulerAngles = new Vector3(270, 87, 0);

            UIPatches.notifierUi.transform.parent = leftWristUi.transform;
            UIPatches.notifierUi.transform.localPosition = new Vector3(0.12f, 0f, -0.085f);
            UIPatches.notifierUi.transform.localEulerAngles = new Vector3(272, 163, 283);
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



        //protected override void SpawnHands()
        //{
        //    if (!RightHand && VRGlobals.menuVRManager.RightHand)
        //        RightHand = VRGlobals.menuVRManager.RightHand;
        //    if (!LeftHand && VRGlobals.menuVRManager.LeftHand) { 
        //        LeftHand = VRGlobals.menuVRManager.LeftHand;
        //    }
        //}
    }
}
