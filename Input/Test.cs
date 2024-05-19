using EFT;
using EFT.Visual;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static RootMotion.FinalIK.InteractionTrigger;

namespace TarkovVR.Input
{
    internal class Test : MonoBehaviour
    {
        public float x, y, z;
        public static float ex, ey, ez;
        //private Vector3 headOffset = new Vector3(0.05f, 1.56f, 0.25f);
        private Vector3 headOffset = new Vector3(0.04f, 0.175f, 0.07f);
        private Vector3 armOffset = new Vector3(0.3f, -0.2f, -0.04f);
        private Vector3 headsetInitPos;
        private float moveSpeed = 5;
        private Vector3 lastPos;
        private bool matchingHeadToBody = false;

        void Awake() { 
            headsetInitPos = this.transform.localPosition;
            lastPos = this.transform.localPosition - headOffset;
            ex = 40;
        }

        void LateUpdate() {


            if (CamPatches.inGame && CamPatches.player && CamPatches.player.PointOfView == EPointOfView.FirstPerson && this.name == "Base HumanSpine3")
            {
                Vector3 headsetPos = Camera.main.transform.position;
                Vector3 playerBodyPos = this.transform.root.position + headOffset;
                headsetPos.y = 0;
                playerBodyPos.y = 0;
                float distanceBetweenBodyAndHead = Vector3.Distance(playerBodyPos, headsetPos);

                //// Assuming 'camRoot' represents the player's body orientation
                //Vector3 bodyForward = Quaternion.Euler(0, 28, 0) * this.transform.root.forward ;
                //Vector3 cameraForward = Camera.main.transform.forward;

                //// Project vectors onto the horizontal plane by zeroing the y component
                //bodyForward.y = 0;
                //cameraForward.y = 0;

                //// Normalize vectors to ensure they are purely directional (unit length)
                //bodyForward.Normalize();
                //cameraForward.Normalize();

                //// Calculate the angle between the body's forward direction and the camera's
                //float angle = Vector3.Angle(bodyForward, cameraForward);

                //// Determine the direction of the rotation using a cross product
                //Vector3 cross = Vector3.Cross(bodyForward, cameraForward);
                //Plugin.MyLog.LogError(angle + " " + cross);


                // if the players head is a certain distance from the bodies center then start tracking the upper body to
                // the headset. This allows the player to look left and right over the shoulder without the arms
                // (attached to the upper body) rotating when looking around
                //if (distanceBetweenBodyAndHead >= 0.18 || (angle > 90)) { 
                if (distanceBetweenBodyAndHead >= 0.18 || (matchingHeadToBody && distanceBetweenBodyAndHead > 0.05))
                {
                    Vector3 newLocalPosition = this.transform.parent.InverseTransformPoint(Camera.main.transform.position) + armOffset;
                    this.transform.localPosition = Vector3.Lerp(lastPos, newLocalPosition, Time.deltaTime * moveSpeed);
                    lastPos = this.transform.localPosition;

                    if (CamPatches.cameraManager.initPos.y != 0 && (distanceBetweenBodyAndHead >= 0.325 || (matchingHeadToBody && distanceBetweenBodyAndHead > 0.075)))
                    {
                        // Add code for headset to body difference
                        matchingHeadToBody = true;
                        float moveSpeed = 0.5f;
                        Vector3 newPosition = Vector3.MoveTowards(this.transform.root.position, headsetPos, moveSpeed * Time.deltaTime);
                        Vector3 movementDelta = newPosition - this.transform.root.position; // The actual movement vector
                        this.transform.root.position = newPosition;
                        // Now, counteract this movement for vrOffsetter to keep the camera stable in world space
                        Vector3 localMovementDelta = CamPatches.vrOffsetter.transform.parent.InverseTransformVector(movementDelta);
                        localMovementDelta.y = 0;
                        CamPatches.cameraManager.initPos += localMovementDelta; // Apply inverse local movement to vrOffsetter
                    }
                    else
                        matchingHeadToBody = false;
                    //this.transform.rotation = Camera.main.transform.rotation;
                }
                else
                {
                    matchingHeadToBody = false;
                    this.transform.localPosition = Vector3.Lerp(lastPos, headsetInitPos, Time.deltaTime * moveSpeed);
                    lastPos = this.transform.localPosition;
                }
            }
            //else if (this.name == CamPatches.fingiename) { 
            //    this.transform.position = CameraManager.RightHand.transform.position;
            //    this.transform.rotation = CameraManager.RightHand.transform.rotation;
            //}
            else
            {
                this.transform.localPosition = new Vector3(x, y, z);
            }


            // TODO!!!!!!!!!!!!
            // Remove the weapon offset from the WeaponHolder object because that cascades to the weapon when rotating and makes it roll instead of rotating
            // so if you move the offset to the rightHandOffset this will maintain proper offsetting without rolling


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
