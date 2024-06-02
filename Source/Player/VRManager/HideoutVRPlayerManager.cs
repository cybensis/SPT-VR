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


        public void PositionInteractionUI()
        {
            camRotation = Camera.main.transform.localRotation;

            // Set position not local position so it doesn't inherit rotated position from camRoot
            interactionUi.position = Camera.main.transform.position + Camera.main.transform.forward * 0.4f + Camera.main.transform.up * -0.2f + Camera.main.transform.right * 0;
            interactionUi.LookAt(Camera.main.transform);
            // Need to rotate 180 degrees otherwise it shows up backwards
            interactionUi.Rotate(0, 180, 0);
        }



        protected override void SpawnHands()
        {
            if (!RightHand && VRGlobals.menuVRManager.RightHand)
                RightHand = VRGlobals.menuVRManager.RightHand;
            if (!LeftHand && VRGlobals.menuVRManager.LeftHand) { 
                LeftHand = VRGlobals.menuVRManager.LeftHand;
            }
        }
    }
}
