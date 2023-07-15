using EFT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TarkovVR.Input
{
    internal class Test : MonoBehaviour 
    {
        static float x, y, z;
        public static float ex, ey, ez;
        private Vector3 headOffset = new Vector3(0.05f, 1.56f, 0.25f);

        void LateUpdate() {
            if (x == 0)
                x = 1000f;
            if (Camera.main != null)
            {

                //transform.position = Camera.main.transform.position + (Camera.main.transform.right * x) + (Camera.main.transform.up * y) + (Camera.main.transform.forward * z);
                //CamPatches.camRoot.transform.rotation = Quaternion.Euler(0, Camera.main.transform.eulerAngles.y, 0);
                //if (ex == 1)
                //    transform.rotation = Camera.main.transform.rotation;
                //else if (ex == 2)
                //    transform.rotation = Camera.main.transform.localRotation;

                //if (ex == 0)
                //    transform.localRotation = Camera.main.transform.rotation;
                //else if (ex == -1)
                //    transform.localRotation = Camera.main.transform.localRotation;

                Camera.main.farClipPlane = x; ;

            }
            //if (GamePlayerOwner.MyPlayer != null) {

            //    transform.position = GamePlayerOwner.MyPlayer.Transform.position + (GamePlayerOwner.MyPlayer.Transform.right * headOffset.x) + (GamePlayerOwner.MyPlayer.Transform.up * headOffset.y) + (GamePlayerOwner.MyPlayer.Transform.forward * headOffset.z);
            //    CamPatches.camParent.transform.rotation = Quaternion.Euler(0, GamePlayerOwner.MyPlayer.Transform.eulerAngles.y, 0);
            //}
        }
    }
}
