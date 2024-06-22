using UnityEngine;

namespace TarkovVR.Source.Misc
{
    internal class WeaponPositioner : MonoBehaviour
    {

        private float x, y, z;
        private float rx, ry, rz;
        public Transform target;
        public bool left = false;
        private void Awake()
        {
            x = 90f;

            //rx = 15;
            //ry = 275;
            //rz = 90f;
        }

        private void LateUpdate()
        {
            transform.localPosition = Vector3.zero;
            transform.localEulerAngles = Vector3.zero;
        }
        //private void LateUpdate()
        //{
        //    //if (target) {
        //    //     //transform.position = target.position + new Vector3(x, y, z);
        //    //    //transform.rotation = target.rotation * Quaternion.Euler(rx, ry, rz);

        //    //    target.position = transform.position;
        //    //    target.rotation = transform.rotation;
        //    //    // transform.position = VRPlayerManager.RightHand.transform.position + new Vector3(x, y, z);
        //    //    //transform.rotation = VRPlayerManager.RightHand.transform.rotation * Quaternion.Euler(rx, ry, rz);
        //    //}
        //    //else
        //    //{
        //    //    //transform.position = VRPlayerManager.RightHand.transform.position + new Vector3(x, y, z);
        //    //    //transform.rotation = VRPlayerManager.RightHand.transform.rotation * Quaternion.Euler(rx, ry, rz);
        //    //    transform.rotation = Quaternion.Euler(rx, ry, rz);
        //    //}\
        //    if (left && VRGlobals.vrPlayer.LeftHand)
        //    {
        //        transform.position = VRGlobals.vrPlayer.LeftHand.transform.position;
        //        transform.rotation = VRGlobals.vrPlayer.LeftHand.transform.rotation;
        //    }
        //    else if (VRGlobals.vrPlayer.RightHand)
        //    {
        //        transform.position = VRGlobals.vrPlayer.RightHand.transform.position;
        //        transform.rotation = VRGlobals.vrPlayer.RightHand.transform.rotation;
        //    }
        //    //if (CamPatches.VRCam != null)
        //    //{
        //    //    //transform.position = CamPatches.VRCam.transform.position + (CamPatches.VRCam.transform.right * 0) + (CamPatches.VRCam.transform.up * -0.04f) + (CamPatches.VRCam.transform.forward * -0.06f);
        //    //    transform.position = CamPatches.VRCam.transform.position + (CamPatches.VRCam.transform.right * 0.025f) + (CamPatches.VRCam.transform.up * 0.3f) + (CamPatches.VRCam.transform.forward * -0.06f);
        //    //    //transform.localPosition += (transform.right * x) + (transform.up * y) + (transform.forward * z);
        //    //    transform.rotation = CamPatches.VRCam.transform.rotation * Quaternion.Euler(180, 90, 90);
        //    //}
        //}

        /*        private void LateUpdate()
                {

                    if (CamPatches.VRCam != null)
                    {
                        transform.position = CamPatches.VRCam.transform.position + (CamPatches.VRCam.transform.right * 0) + (CamPatches.VRCam.transform.up * -0.04f) + (CamPatches.VRCam.transform.forward * -0.06f);
                        //transform.localPosition += (transform.right * x) + (transform.up * y) + (transform.forward * z);
                        transform.rotation = CamPatches.VRCam.transform.rotation * Quaternion.Euler(180, 90, 90);
                    }

                    //transform.localPosition = new Vector3(x, y, z);
                    //transform.localRotation = Quaternion.Euler(x,y,z);

                    // this.GetComponent<Camera>().fieldOfView = 7;
                    // Set the Weapon_root to the right hand IK I think then mess around with the values till it looks good

                    // Weapon_root goes in weapon holder, holder goes in VR right hand, set rot of holder to 15,270,90
                    // pos to 0.141 0.0204 -0.1003

                    // Pistols rot: 7.8225 278.6073 88.9711  pos: 0.3398 -0.0976 -0.1754



                    // 28.8354 295.806 90
                }*/
    }
}
