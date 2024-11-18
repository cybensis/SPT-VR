using EFT;
using RootMotion.FinalIK;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.VR
{
    internal class IKManager : MonoBehaviour
    {

        private float moveSpeed = 5;
        private bool matchingHeadToBody = false;
        private Vector3 upperArmPos = new Vector3(-0.1f, 0.1f, 0);

        private Transform leftUpperArm;
        private Transform rightUpperArm;
        public GameObject leftHandIK;
        public GameObject rightHandIK;
        public LimbIK leftArmIk;
        public LimbIK rightArmIk;


        private void Awake()
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child.name == "Base HumanRibcage")
                {
                    for (int a = 0; a < child.childCount; a++)
                    {
                        Transform innerChild = child.GetChild(a);
                        if (innerChild.name == "Base HumanLCollarbone")
                            leftUpperArm = innerChild.GetChild(0);
                        else if (innerChild.name == "Base HumanRCollarbone")
                            rightUpperArm = innerChild.GetChild(0);
                    }
                }
            }
        }

        public void MatchLegsToArms() {

            // Can't remember what this is for, I'm guessing to check if the player height and position is initialized properly
            if (VRGlobals.vrPlayer.initPos.y == 0)
                return;

            if (VRGlobals.inGame && VRGlobals.player && VRGlobals.player.PointOfView == EPointOfView.FirstPerson && name == "Base HumanSpine3")
            {
                // Position the player torso under the head
                // Set the position of the spin under the camera and a little bit backwards so it matches with the camera better
                //Vector3 cameraPosition = Camera.main.transform.position + new Vector3(0, -0.5f, 0);
                //transform.position = cameraPosition + Camera.main.transform.forward * -0.3f;

                //// Set the spine rotation to have x and z static but y (left and right) to the camera + offset to face forward
                //Vector3 cameraEulerAngles = new Vector3(0, Camera.main.transform.eulerAngles.y + 280, -105);
                //transform.eulerAngles = cameraEulerAngles;



                Vector3 headsetPos = Camera.main.transform.position;
                // Set the position of the body forward and to the right a bit because the actual center of the body kind of leans back too far
                Vector3 playerBodyPos = transform.root.position + transform.root.forward * 0.12f + transform.root.right * 0.05f;
                headsetPos.y = 0;
                playerBodyPos.y = 0;
                float distanceBetweenBodyAndHead = Vector3.Distance(playerBodyPos, headsetPos);

                // Calculate the direction from the body to the headset
                Vector3 directionToHeadset = headsetPos - transform.root.position;

                // Determine if the headset is behind the body, we do this because the body stretches weirdly when leaning back, so try to always
                // keep the body and head in the same place if they're leaning backwards.
                float dotProduct = Vector3.Dot(transform.root.forward, directionToHeadset.normalized);

                float distanceFromSideOfBody = Vector3.Dot(transform.root.right, directionToHeadset.normalized);

                //Plugin.MyLog.LogWarning(adotProduct + "  |   " + distanceBetweenBodyAndHead + "   |    " + dotProduct);

                float xAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x);
                float yAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y);
                bool matchToBodyWhenMoving = (xAxis > VRSettings.GetLeftStickSensitivity() || yAxis > VRSettings.GetLeftStickSensitivity()) && distanceBetweenBodyAndHead > 0.135f;


                bool shouldMatchToBody = distanceBetweenBodyAndHead >= 0.25 || dotProduct < -0.5f;
                if (Mathf.Abs(distanceFromSideOfBody) > 0.8)
                    shouldMatchToBody = distanceBetweenBodyAndHead >= 0.35 || dotProduct < -0.5f;
                bool stillNeedsToMatchToBody = matchingHeadToBody && distanceBetweenBodyAndHead > 0.135f;

                // If the distance to the body is >= 0.25f, or if its currently being matched to the body, or if the head is behind the body
                if (matchToBodyWhenMoving || shouldMatchToBody || stillNeedsToMatchToBody)
                {

                    if (distanceBetweenBodyAndHead > 1)
                    {
                        // The head shouldn't ever naturally be this far away from the body, so just set the camera to the body.
                        VRGlobals.vrPlayer.initPos = Camera.main.transform.localPosition;
                        VRGlobals.camRoot.transform.position = transform.root.position;

                    }
                    else
                    {
                        // We start moving the body towards the head if they aren't near each other, then offset that distance from the 
                        // initPos because the camRoot and player pos are linked, so any movements to the player are made to camRoot and
                        // need to be offset via vrOffsetter/initPos so the headset doesn't move with the camRoot.
                        matchingHeadToBody = true;
                        headsetPos.y = transform.root.position.y;
                        Vector3 newPosition = Vector3.MoveTowards(transform.root.position, headsetPos, Time.deltaTime);
                        Vector3 movementDelta = newPosition - transform.root.position; // The actual movement vector

                        transform.root.position = newPosition;
                        // Now, counteract this movement for vrOffsetter to keep the camera stable in world space
                        Vector3 localMovementDelta = VRGlobals.vrOffsetter.transform.parent.InverseTransformVector(movementDelta);
                        localMovementDelta.y = 0;
                        VRGlobals.vrPlayer.initPos += localMovementDelta; // Apply inverse local movement to vrOffsetter
                    }
                }
                else
                    matchingHeadToBody = false;
            }
            else
            {
                transform.localPosition = Vector3.zero;
            }
        }

        // NOTE: I tried extending the arms but that offsets the hands from the gun, so instead set the local position of
        // the upper arm, or the collarbone, for the upper arm the +y value goes forward
        // For collarbone setting everything to 0 and z = 0.1 seemed to work fine

        void Update()
        {
            if (!VRGlobals.vrPlayer || VRGlobals.menuOpen)
                return;

            if (leftUpperArm)
                leftUpperArm.localPosition = upperArmPos;
            if (rightUpperArm)
                rightUpperArm.localPosition = upperArmPos;

            if (!VRGlobals.emptyHands || VRGlobals.player.HandsIsEmpty) { 
                MatchLegsToArms();
            }
           


        }
    }
}
