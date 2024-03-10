using UnityEngine;
using Valve.VR.Extras;
using Valve.VR;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace TarkovVR.Input
{
    internal class CameraManager : MonoBehaviour
    {
        private float x, y, z;
        private float rotx, roty, rotz;
        private float supportRotX, supportRotY, supportRotZ;
        private float rx, ry, rz;

        public Vector3 initPos;

        private Vector3 rightHandOffset = new Vector3(0.05f, -0.1f, -0.2f);
        private Vector3 headOffset = new Vector3(0f, 0.075f, 0.1f);
        private Vector3 supportRightHandOffset = new Vector3(0.1f, -0.05f,-0.05f);
        private Vector3 leftHandOffset = new Vector3(-0.04f, -0.05f, -0.1f);
        private Vector3 supportLeftHandOffset = new Vector3(-0.05f,0,0);

        public Transform gunTransform;
        public static Transform leftHandGunIK;

        public static float smoothingFactor = 20f; // Adjust this value to lower to increase aim smoothing - 20 is barely noticable so good baseline

        private Queue<Vector3> positionHistory = new Queue<Vector3>();
        private Queue<Quaternion> rotationHistory = new Queue<Quaternion>();
        private int historyLength = 5; // Number of frames to average over

        // VR Origin and body stuff
        public static GameObject LeftHand = null;
        public static GameObject RightHand = null;
        private bool isSupporting = false;
        private float timeHeld = 0;

        public void Awake()
        {
            //x = rightHandOffset.x;
            //y = rightHandOffset.y;
            //z = rightHandOffset.z;
            //rotx = 70;
            roty = 170;
            rotx = 50;
            ry = 90;
            supportRotX = 330;
            
            //supportRotY = 135;

            SpawnHands();
            if (RightHand)
                RightHand.transform.parent = CamPatches.vrOffsetter.transform;
            if (LeftHand)
                LeftHand.transform.parent = CamPatches.vrOffsetter.transform;

            SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.Any, UpdateRightHand);
            SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.Any, UpdateLeftHand);
        }

        private void Update() {
            if (initPos.y == 0 && Camera.main != null)
                initPos = Camera.main.transform.localPosition;
    
            if (initPos.y != 0)
                CamPatches.vrOffsetter.transform.localPosition = (initPos * -1) + headOffset;
            else
                CamPatches.vrOffsetter.transform.localPosition = (Camera.main.transform.localPosition * -1) + headOffset;
           
            if (SteamVR_Actions._default.ClickRightJoystick.GetState(SteamVR_Input_Sources.Any))
            {
                timeHeld += Time.deltaTime;
                if (Camera.main != null && timeHeld > 0.75f)
                {
                    initPos = Camera.main.transform.localPosition;
                }
            }
            else if (timeHeld != 0) { 
                timeHeld = 0;
            }
        }
        private bool wasAimingPreviously = false;
        private void UpdateRightHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (RightHand)
            {
                bool isAiming = SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f;

                if (isAiming && !wasAimingPreviously) // You need a boolean flag to track the previous state
                {
                    positionHistory.Clear();
                    rotationHistory.Clear();
                }
                wasAimingPreviously = isAiming;
                if (isSupporting) { 
                    //if (isAiming)
                    //{
                    //    // Update position and rotation history for smoothing
                    //    UpdateHistory(fromAction.localPosition + rightHandOffset, fromAction.localRotation * Quaternion.Euler(70, 170, 50));
                    //    // Apply smoothed position and rotation
                    //    ApplySmoothedTransform();
                    //}
                    Vector3 currentRightHandPosition = fromAction.localPosition + supportRightHandOffset;

                    Vector3 toLeftHand = LeftHand.transform.position - RightHand.transform.position;
                    Vector3 flatToHand = new Vector3(toLeftHand.x, 0, toLeftHand.z); // For yaw

                    // Calculate yaw to face the left hand horizontally
                    Quaternion yawRotation = Quaternion.LookRotation(flatToHand, Vector3.up);

                    // Correcting pitch calculation: 
                    float pitchAngle = Mathf.Atan2(toLeftHand.y, flatToHand.magnitude) * Mathf.Rad2Deg;

                    // Separate rotation offsets for clearer control
                    Quaternion offsetRotation = Quaternion.Euler(330, 0, 0); // Apply pitch offset here

                    // Now, extract the roll from the right hand's rotation
                    // This captures the wrist twist/roll
                    float rollAngle = fromAction.localRotation.eulerAngles.z;
                    // Ensure the roll is correctly oriented; you might need to adjust this calculation
                    Quaternion rollRotation = Quaternion.Euler(0, 0, rollAngle - 90); // Adjusting based on initial hand orientation

                    Quaternion combinedRotation = yawRotation * Quaternion.Euler(-pitchAngle, 0, 0) * offsetRotation * rollRotation;
                    // Additional correction for yaw and roll offsets if necessary
                    combinedRotation *= Quaternion.Euler(0, 120, -45);

                    RightHand.transform.rotation = Quaternion.Slerp(RightHand.transform.rotation, combinedRotation, smoothingFactor * Time.deltaTime);

                    RightHand.transform.localPosition = currentRightHandPosition;
                }
                else
                {
                    // Directly update transform without smoothing
                    RightHand.transform.localPosition = fromAction.localPosition + rightHandOffset;
                    RightHand.transform.localRotation = fromAction.localRotation;
                    RightHand.transform.Rotate(70, 170, 50);
                }


            }
        }
        private bool handLock = false;
        private void UpdateLeftHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (LeftHand)
            {
                if (leftHandGunIK && (Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.1f || handLock || (isSupporting && Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.15f)))
                {
                    CamPatches.leftArmIk.solver.target = leftHandGunIK;
                    if (SteamVR_Actions._default.LeftGrip.state)
                        handLock = true;
                    else 
                        handLock = false;
                    isSupporting = true;
                    LeftHand.transform.localPosition = fromAction.localPosition + supportLeftHandOffset;
                }
                else {
                    isSupporting = false;
                    CamPatches.leftArmIk.solver.target = LeftHand.transform;
                    LeftHand.transform.localPosition = fromAction.localPosition + leftHandOffset;
                }
                    LeftHand.transform.localRotation = fromAction.localRotation;
                    LeftHand.transform.Rotate(-20, 0, 70);

            }
        }

        public void SpawnHands()
        {
            if (!RightHand)
            {
                //RightHand = GameObject.Instantiate(AssetLoader.RightHandBase, Vector3.zero, Quaternion.identity);
                RightHand = new GameObject("RightHand");
                RightHand.AddComponent<SteamVR_Behaviour_Pose>();
                //RightHand.AddComponent<SteamVR_Skeleton_Poser>();
                RightHand.transform.parent = CamPatches.camHolder.transform.parent;

            }
            if (!LeftHand)
            {
                //LeftHand = GameObject.Instantiate(AssetLoader.LeftHandBase, Vector3.zero, Quaternion.identity);
                LeftHand = new GameObject("LeftHand");
                LeftHand.AddComponent<SteamVR_Behaviour_Pose>();
                LeftHand.transform.parent = CamPatches.camHolder.transform.parent;
            }
        }

        private void UpdateHistory(Vector3 position, Quaternion rotation)
        {
            if (positionHistory.Count >= historyLength) positionHistory.Dequeue();
            if (rotationHistory.Count >= historyLength) rotationHistory.Dequeue();

            positionHistory.Enqueue(position);
            rotationHistory.Enqueue(rotation);
        }

        private void ApplySmoothedTransform()
        {
            Vector3 averagePosition = AveragePosition(positionHistory);
            Quaternion averageRotation = AverageRotation(rotationHistory);

            RightHand.transform.localPosition = Vector3.Lerp(RightHand.transform.localPosition, averagePosition, smoothingFactor * Time.deltaTime);
            RightHand.transform.localRotation = Quaternion.Slerp(RightHand.transform.localRotation, averageRotation, smoothingFactor * Time.deltaTime);
        }


        private Vector3 AveragePosition(Queue<Vector3> positions)
        {
            Vector3 sum = Vector3.zero;
            foreach (var pos in positions)
            {
                sum += pos;
            }
            return sum / positions.Count;
        }

        private Quaternion AverageRotation(Queue<Quaternion> rotations)
        {
            Quaternion average = rotations.Peek(); // Start with the first rotation in the queue
            float weight = 1f / rotations.Count;

            foreach (var rotation in rotations)
            {
                average = Quaternion.Slerp(average, rotation, weight);
            }

            return average.normalized;
        }

    }
}
