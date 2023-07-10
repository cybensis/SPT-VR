using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Input;
using UnityEngine;

namespace TarkovVR
{
    internal class Positioner : MonoBehaviour
    {

        private float x, y, z;
        private float rx, ry, rz;
        public Transform target;
        private void Awake() {
            //x = 0.141f;
            //y = 0.0204f;
            //z = -0.1003f;

            //rx = 15;
            //ry = 275;
            //rz = 90f;
        }

        private void Update()
        {
            if (target) {
                 //transform.position = target.position + new Vector3(x, y, z);
                //transform.rotation = target.rotation * Quaternion.Euler(rx, ry, rz);

                target.position = transform.position;
                target.rotation = transform.rotation;
                // transform.position = CameraManager.RightHand.transform.position + new Vector3(x, y, z);
                //transform.rotation = CameraManager.RightHand.transform.rotation * Quaternion.Euler(rx, ry, rz);
            }
            else
            {
                //transform.position = CameraManager.RightHand.transform.position + new Vector3(x, y, z);
                //transform.rotation = CameraManager.RightHand.transform.rotation * Quaternion.Euler(rx, ry, rz);
                transform.rotation = Quaternion.Euler(rx, ry, rz);
            }
        }

        private void LateUpdate() {
            //transform.position = CamPatches.rightHandIK.transform.position;
            //transform.rotation = CamPatches.rightHandIK.transform.rotation;

            if (target)
            {
                //transform.position = target.position + new Vector3(x, y, z);
                //transform.rotation = target.rotation * Quaternion.Euler(rx, ry, rz);
                target.position = transform.position;
                target.rotation = transform.rotation;

            }
            else
            {
                //transform.position = CameraManager.RightHand.transform.position + new Vector3(x, y, z);
                //transform.rotation = CameraManager.RightHand.transform.rotation * Quaternion.Euler(rx, ry, rz);
                transform.rotation = Quaternion.Euler(rx, ry, rz);

            }

            //transform.localPosition = new Vector3(x, y, z);
            //transform.localRotation = Quaternion.Euler(x,y,z);

            // this.GetComponent<Camera>().fieldOfView = 7;
            // Set the Weapon_root to the right hand IK I think then mess around with the values till it looks good

            // Weapon_root goes in weapon holder, holder goes in VR right hand, set rot of holder to 15,270,90
            // pos to 0.141 0.0204 -0.1003

            // Pistols rot: 7.8225 278.6073 88.9711  pos: 0.3398 -0.0976 -0.1754



            // 28.8354 295.806 90
        }
    }
}
