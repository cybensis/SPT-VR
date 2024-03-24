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
        //private Vector3 headOffset = new Vector3(0.05f, 1.56f, 0.25f);
        private Vector3 headOffset = new Vector3(0.07f, 0.14f, 0.07f);
        private Vector3 armOffset = new Vector3(0.3f, -0.2f, -0.04f);
        private Vector3 headsetInitPos;
        private float moveSpeed = 5;
        private Vector3 lastPos;

        void Awake() { 
            headsetInitPos = this.transform.localPosition;
            lastPos = this.transform.localPosition - headOffset;
            ex = 40;
        }

        void LateUpdate() {


            if (this.name == "Base HumanSpine3")
            {
                Vector3 headsetPos = Camera.main.transform.position;
                Vector3 playerBodyPos = this.transform.root.position + headOffset;
                headsetPos.y = 0;
                playerBodyPos.y = 0;
                float distanceBetweenBodyAndHead = Vector3.Distance(playerBodyPos, headsetPos);
                // if the players head is a certain distance from the bodies center then start tracking the upper body to
                // the headset. This allows the player to look left and right over the shoulder without the arms
                // (attached to the upper body) rotating when looking around
                if (distanceBetweenBodyAndHead >= 0.18) { 
                    Vector3 newLocalPosition = this.transform.parent.InverseTransformPoint(Camera.main.transform.position) + armOffset;
                    this.transform.localPosition = Vector3.Lerp(lastPos, newLocalPosition, Time.deltaTime * moveSpeed);
                    lastPos = this.transform.localPosition;
                }
                else {
                    this.transform.localPosition = Vector3.Lerp(lastPos, headsetInitPos, Time.deltaTime * moveSpeed);
                    lastPos = this.transform.localPosition;
                }
            }
            else { 
                this.transform.localPosition = new Vector3(x,y,z);
            }
            //if (Camera.main != null)
            //{

            //    //transform.position = Camera.main.transform.position + (Camera.main.transform.right * x) + (Camera.main.transform.up * y) + (Camera.main.transform.forward * z);
            //    //CamPatches.camRoot.transform.rotation = Quaternion.Euler(0, Camera.main.transform.eulerAngles.y, 0);
            //    //if (ex == 1)
            //    //    transform.rotation = Camera.main.transform.rotation;
            //    //else if (ex == 2)
            //    //    transform.rotation = Camera.main.transform.localRotation;

            //    //if (ex == 0)
            //    //    transform.localRotation = Camera.main.transform.rotation;
            //    //else if (ex == -1)
            //    //    transform.localRotation = Camera.main.transform.localRotation;

            //    Camera.main.farClipPlane = x; ;

            //}
            //if (GamePlayerOwner.MyPlayer != null) {

            //    transform.position = GamePlayerOwner.MyPlayer.Transform.position + (GamePlayerOwner.MyPlayer.Transform.right * headOffset.x) + (GamePlayerOwner.MyPlayer.Transform.up * headOffset.y) + (GamePlayerOwner.MyPlayer.Transform.forward * headOffset.z);
            //    CamPatches.camParent.transform.rotation = Quaternion.Euler(0, GamePlayerOwner.MyPlayer.Transform.eulerAngles.y, 0);
            //}
        }
    }
}
