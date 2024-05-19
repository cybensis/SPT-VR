using UnityEngine;
using Valve.VR.Extras;
using Valve.VR;
using System.Collections.Generic;
using UnityEngine.UIElements;
using System.Diagnostics;
using TarkovVR.cam;
using EFT.UI.Ragfair;
using static EFT.Hideout.ShootingRange.RailTarget;
using EFT;
using System.Reflection;

namespace TarkovVR.Input
{
    internal class CameraManager : MonoBehaviour
    {
        public float x, y, z;
        private float rotx, roty, rotz;
        private float supportRotX, supportRotY, supportRotZ;
        public float rx, ry, rz;
        private CanvasCast canvasCast;
        public Vector3 initPos;

        public Vector3 rightHandOffset = new Vector3(0f, 0f, 0f);
        //public Vector3 rightHandOffset = new Vector3(0.05f, -0.1f, -0.2f);
        //private Vector3 headOffset = new Vector3(0f, 0.12f, 0.1f);
        //private Vector3 headOffset = new Vector3(-0.07f, 0.12f, 0.09f);
        public static Vector3 headOffset = new Vector3(0.04f, 0.175f, 0.07f);
        private Vector3 supportRightHandOffset = new Vector3(-0.05f, -0.05f, -0.15f);
        //private Vector3 supportRightHandOffset = new Vector3(0.1f, -0.05f,-0.05f);
        //private Vector3 leftHandOffset = new Vector3(0f, -0.05f, -0.2f);
        private Vector3 leftHandOffset = new Vector3(0f, 0f, 0f);
        //private Vector3 supportLeftHandOffset = new Vector3(-0.05f,0,0);
        private Vector3 supportLeftHandOffset = new Vector3(-0.1f, -0.05f, 0);

        private Vector3 supportingWeaponHolderOffset = new Vector3(0.155f, 0.09f, 0.05f);

        public Transform gunTransform;
        public static Transform leftHandGunIK;

        public static float smoothingFactor = 20f; // Adjust this value to lower to increase aim smoothing - 20 is barely noticable so good baseline

        private Queue<Vector3> positionHistory = new Queue<Vector3>();
        private Queue<Quaternion> rotationHistory = new Queue<Quaternion>();
        private int historyLength = 5; // Number of frames to average over

        // VR Origin and body stuff
        public static GameObject LeftHand = null;
        public static GameObject RightHand = null;
        public bool isSupporting = false;
        private float timeHeld = 0;
        public Transform interactionUi;
        public Vector3 startingPlace;
        private Quaternion camRotation;
        public void Awake()
        {
            //x = rightHandOffset.x;
            //y = rightHandOffset.y;
            //z = rightHandOffset.z;
            //rotx = 70;

            //supportRotY = 135;
            //x = 200f;
            //y = 25f;
            //z = -150f;
            x = 0.7f;
            y = -0.6f;
            z = -0.75f;
            ry = 225;
            SpawnHands();
            if (RightHand)
                RightHand.transform.parent = CamPatches.vrOffsetter.transform;
            if (LeftHand)
                LeftHand.transform.parent = CamPatches.vrOffsetter.transform;

            SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
            camRotation = Camera.main.transform.rotation;

        }


        public void OnDisable()
        {
            SteamVR_Actions._default.RightHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            SteamVR_Actions._default.LeftHandPose.RemoveOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
        }
        public void OnEnable()
        {
            SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
        }



        private void Update() {
            if (Camera.main == null)
                return;
            if (initPos.y == 0)
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


            if (interactionUi)
            {
                //// Get the rotation angles of the camera
                //float rotationX = cameraTransform.rotation.eulerAngles.x;
                //float rotationY = cameraTransform.rotation.eulerAngles.y;

                //// Calculate the movement offsets based on the rotation angles
                //float offsetX = Mathf.Clamp((rotationY - 180) / maxLeftRotation, 0, 1) * movementSpeed;
                //float offsetY = Mathf.Clamp(rotationX / maxUpRotation, 0, 1) * movementSpeed;

                //// Calculate the new position of the UI
                //Vector3 newPosition = originalPosition + new Vector3(offsetX, -offsetY, 0);

                //// Apply the new position to the UI
                //transform.position = newPosition;

                float yRotationDifference = Mathf.Abs(Quaternion.Angle(Camera.main.transform.localRotation, camRotation));
                //Plugin.MyLog.LogWarning(yRotationDifference);    

                // If the Y-axis rotation difference exceeds the maximum allowed, reposition the UI
                if (yRotationDifference > 45)
                {

                    PositionInteractionUI();
                }

            }

        }


        public void PositionInteractionUI() {
            camRotation = Camera.main.transform.localRotation;

            // Set position not local position so it doesn't inherit rotated position from camRoot
            interactionUi.position = Camera.main.transform.position + Camera.main.transform.forward * 0.4f + Camera.main.transform.up * -0.2f + Camera.main.transform.right * 0;
            interactionUi.LookAt(Camera.main.transform);
            // Need to rotate 180 degrees otherwise it shows up backwards
            interactionUi.Rotate(0, 180, 0);
        }


        private bool wasAimingPreviously = false;
        private float controllerLength = 0.175f;
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
                    Vector3 currentRightHandPosition = fromAction.localPosition + rightHandOffset;

                    Vector3 toLeftHand = LeftHand.transform.position - RightHand.transform.position;
                    Vector3 flatToHand = new Vector3(toLeftHand.x, 0, toLeftHand.z); // For yaw

                    // Calculate yaw to face the left hand horizontally
                    Quaternion yawRotation = Quaternion.LookRotation(flatToHand, Vector3.up);

                    // Correcting pitch calculation: 
                    float pitchAngle = Mathf.Atan2(toLeftHand.y, flatToHand.magnitude) * Mathf.Rad2Deg;

                    // Separate rotation offsets for clearer control
                    Quaternion offsetRotation = Quaternion.Euler(340, 0, 0); // Apply pitch offset here

                    // Now, extract the roll from the right hand's rotation
                    // This captures the wrist twist/roll
                    float rollAngle = fromAction.localRotation.eulerAngles.z;
                    // Ensure the roll is correctly oriented; you might need to adjust this calculation
                    Quaternion rollRotation = Quaternion.Euler(0, 0, rollAngle - 90); // Adjusting based on initial hand orientation

                    Quaternion combinedRotation = yawRotation * Quaternion.Euler(-pitchAngle, 0, 0) * offsetRotation * rollRotation;
                    // Additional correction for yaw and roll offsets if necessary
                    combinedRotation *= Quaternion.Euler(0, 130, -30);

                    RightHand.transform.rotation = Quaternion.Slerp(RightHand.transform.rotation, combinedRotation, smoothingFactor * Time.deltaTime);

                    RightHand.transform.localPosition = currentRightHandPosition;
                }
                else
                {
                    // Directly update transform without smoothing
                    Vector3 virtualBasePosition = fromAction.localPosition - (fromAction.localRotation * Vector3.forward * controllerLength);
                    RightHand.transform.localPosition = virtualBasePosition + rightHandOffset;
                    //RightHand.transform.localPosition = fromAction.localPosition + rightHandOffset;
                    RightHand.transform.localRotation = fromAction.localRotation;
                    RightHand.transform.Rotate(70, 170, 50);
                }

                if (CamPatches.stancePanel && CamPatches.leftWrist) {
                    //CamPatches.stancePanel.transform.position = RightHand.transform.position + new Vector3(x,y,z);
                    CamPatches.stancePanel.transform.position = CamPatches.leftWrist.position + CamPatches.leftWrist.TransformDirection(new Vector3(0,0.06f,0.01f));
                    CamPatches.stancePanel.transform.localEulerAngles = CamPatches.leftWrist.rotation.eulerAngles;
                    CamPatches.stancePanel.transform.Rotate(140,0,90);

                    CamPatches.healthPanel.transform.position = CamPatches.leftWrist.position + CamPatches.leftWrist.TransformDirection(new Vector3(-0.1f, 0.01f, 0.05f));
                    CamPatches.healthPanel.transform.localEulerAngles = CamPatches.leftWrist.rotation.eulerAngles;
                    CamPatches.healthPanel.transform.Rotate(320,0,90);

                    //Vector3 wristToCamera = (Camera.main.transform.position - CamPatches.leftWrist.position).normalized;
                    float wristToCamera = Vector3.Dot(CamPatches.healthPanel.transform.transform.forward, (Camera.main.transform.position - CamPatches.healthPanel.transform.position).normalized);

                    if (wristToCamera > 0.4)
                    {
                        if (!showingUI)
                        {
                            CamPatches.stancePanel.AnimatedShow(false);
                            CamPatches.healthPanel.AnimatedShow(false);
                            showingUI = true;
                        }
                    }
                    else if (showingUI)
                    {
                        CamPatches.stancePanel.AnimatedHide();
                        CamPatches.healthPanel.AnimatedHide();
                        showingUI = false;
                    }


                }

            }
        }

        private bool showingUI = false;

        private bool handLock = false;
        private Vector3 supportInitPos = Vector3.zero;
        private Vector3 weaponHolderOriginalOffset;
        private Vector3 initialLeftHandLocalPositionAtSnap;

        private void UpdateLeftHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (LeftHand)
            {
                if (leftHandGunIK && (Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.1f || handLock || (isSupporting && Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.175f)))
                {
                    if (!isSupporting)
                    {
                        // Set target to null so the left hand returns to its normal position on the gun
                        CamPatches.leftArmIk.solver.target = null;
                        isSupporting = true;
                        weaponHolderOriginalOffset = CamPatches.weaponHolder.transform.localPosition;
                        CamPatches.weaponHolder.transform.localPosition = weaponHolderOriginalOffset + supportingWeaponHolderOffset;

                    }
                    if (SteamVR_Actions._default.LeftGrip.state)
                        handLock = true;
                    else
                        handLock = false;

                    LeftHand.transform.localPosition = fromAction.localPosition + leftHandOffset;
                }
                else if (CamPatches.leftArmIk)
                {
                    if (isSupporting) { 
                        isSupporting = false;
                        CamPatches.leftArmIk.solver.target = LeftHand.transform;
                        CamPatches.weaponHolder.transform.localPosition = weaponHolderOriginalOffset;
                    }
                    Vector3 virtualBasePosition = fromAction.localPosition - (fromAction.localRotation * Vector3.forward * controllerLength);
                    LeftHand.transform.localPosition = virtualBasePosition + leftHandOffset;
                    //LeftHand.transform.localPosition = fromAction.localPosition + leftHandOffset;
                }
                else {
                    LeftHand.transform.localPosition = fromAction.localPosition + leftHandOffset;
                }
                LeftHand.transform.localRotation = fromAction.localRotation;
                LeftHand.transform.Rotate(-60,0,70);

            }
        }



        public void SpawnHands()
        {
            if (!RightHand)
            {
                if (MenuCameraManager.RightHand)
                    RightHand = MenuCameraManager.RightHand;
                else { 
                    //RightHand = GameObject.Instantiate(AssetLoader.RightHandBase, Vector3.zero, Quaternion.identity);
                    RightHand = new GameObject("RightHand");
                    RightHand.AddComponent<SteamVR_Behaviour_Pose>();
                    //RightHand.AddComponent<SteamVR_Skeleton_Poser>();
                    RightHand.transform.parent = CamPatches.camHolder.transform.parent;
                    MenuPatches.vrUiInteracter = RightHand.AddComponent<VRUIInteracter>();
                
                }

            }
            if (!LeftHand)
            {
                if (MenuCameraManager.LeftHand)
                    LeftHand = MenuCameraManager.LeftHand;
                else
                {
                    //LeftHand = GameObject.Instantiate(AssetLoader.LeftHandBase, Vector3.zero, Quaternion.identity);
                    LeftHand = new GameObject("LeftHand");
                    LeftHand.AddComponent<SteamVR_Behaviour_Pose>();
                    LeftHand.transform.parent = CamPatches.camHolder.transform.parent;
                }
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
