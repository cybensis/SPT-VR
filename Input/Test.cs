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
        static float ex, ey, ez;
        private Vector3 headOffset = new Vector3(0.05f, 1.56f, 0.25f);

        void LateUpdate() {
            if (Camera.main != null)
            {

                transform.position = Camera.main.transform.position + (Camera.main.transform.right * x) + (Camera.main.transform.up * y) + (Camera.main.transform.forward * z);
                CamPatches.camParent.transform.rotation = Quaternion.Euler(0, Camera.main.transform.eulerAngles.y, 0);
            }
            //if (GamePlayerOwner.MyPlayer != null) {

            //    transform.position = GamePlayerOwner.MyPlayer.Transform.position + (GamePlayerOwner.MyPlayer.Transform.right * headOffset.x) + (GamePlayerOwner.MyPlayer.Transform.up * headOffset.y) + (GamePlayerOwner.MyPlayer.Transform.forward * headOffset.z);
            //    CamPatches.camParent.transform.rotation = Quaternion.Euler(0, GamePlayerOwner.MyPlayer.Transform.eulerAngles.y, 0);
            //}
        }
    }
}
