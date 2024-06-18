using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.UI;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.VRManager
{
    internal abstract class VRPlayerManager : MonoBehaviour
    {

        public static Transform leftWrist;



        public Vector3 initPos;


        public static Vector3 headOffset = new Vector3(0.04f, 0.175f, 0.07f);
        private Vector3 supportRightHandOffset = new Vector3(-0.05f, -0.05f, -0.15f);
        private Vector3 supportLeftHandOffset = new Vector3(-0.1f, -0.05f, 0);
        private Vector3 supportingWeaponHolderOffset = new Vector3(0.155f, 0.09f, 0.05f);

        public Transform gunTransform;
        public static Transform leftHandGunIK;

        public static float smoothingFactor = 100f; // Adjust this value to lower to increase aim smoothing - 20 is barely noticable so good baseline

        // VR Origin and body stuff
        public GameObject LeftHand = null;
        public GameObject RightHand = null;

        private GameObject radialMenu;
        private GameObject leftWristUi;

        public bool x = false;

        public bool isSupporting = false;
        private float timeHeld = 0;
        public Transform interactionUi;
        public Vector3 startingPlace;
        private bool showingUI = false;
        private bool handLock = false;
        protected virtual void Awake()
        {

            SpawnHands();
            Plugin.MyLog.LogWarning("Create hands");
            if (RightHand) { 
                RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
                if (!radialMenu) {
                    radialMenu = new GameObject("radialMenu");
                    radialMenu.layer = 5;
                    radialMenu.transform.parent = RightHand.transform;
                    CircularSegmentUI uiComp = radialMenu.AddComponent<CircularSegmentUI>();
                    uiComp.Init();
                    uiComp.CreateGunUi(new string[] { "reload.png", "checkAmmo.png", "inspect.png", "fixMalfunction.png", "fireMode_burst.png" });
                    radialMenu.active = false;
                }
            }
            if (LeftHand) { 
                LeftHand.transform.parent = VRGlobals.vrOffsetter.transform;
                if (!leftWristUi && UIPatches.stancePanel) {
                    leftWristUi = new GameObject("leftWristUi");
                    leftWristUi.layer = 5;
                    leftWristUi.transform.parent = LeftHand.transform;
                    leftWristUi.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                    leftWristUi.transform.localPosition = new Vector3(0, -0.05f, 0.015f);
                    leftWristUi.transform.localEulerAngles = Vector3.zero;

                    UIPatches.healthPanel.transform.parent = leftWristUi.transform;
                    UIPatches.healthPanel.transform.localPosition = Vector3.zero;
                    UIPatches.healthPanel.transform.localEulerAngles = new Vector3(270,87,0);

                    UIPatches.stancePanel.transform.parent = leftWristUi.transform;
                    UIPatches.stancePanel.transform.localPosition = new Vector3(0.1f, 0, 0.03f);
                    UIPatches.stancePanel.transform.localEulerAngles = new Vector3(270, 87, 0);

                }
            }
            SteamVR_Actions._default.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.RightHand);
            SteamVR_Actions._default.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.LeftHand);

            SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);


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


        protected virtual void Update()
        {
            if (Camera.main == null)
                return;
            if (initPos.y == 0)
                initPos = Camera.main.transform.localPosition;

            Vector3 newLocalPos = Vector3.zero;
            // Set the rig collider position to 75% of the cameras height so it appears under the head
            //if (VRGlobals.ikManager)
            //    InitVRPatches.rigCollider.position = newLocalPos;
            //    newLocalPos.y = Camera.main.transform.localPosition.y * 0.75f;

            VRGlobals.vrOffsetter.transform.localPosition = initPos * -1 + headOffset;
            if (SteamVR_Actions._default.ClickRightJoystick.GetState(SteamVR_Input_Sources.Any))
            {
                timeHeld += Time.deltaTime;
                if (Camera.main != null && timeHeld > 0.75f)
                {
                    initPos = Camera.main.transform.localPosition;
                }
            }
            else if (timeHeld != 0)
            {
                timeHeld = 0;
            }

            if (radialMenu)
            {

                RaycastHit hit;
                LayerMask mask = 1 << 7;
                if (SteamVR_Actions._default.RightGrip.state && Physics.Raycast(RightHand.transform.position, RightHand.transform.up * -1, out hit, 2, mask) && hit.collider.name == "camHolder")
                {
                    if (!radialMenu.active)
                    {
                        radialMenu.active = true;
                        VRGlobals.blockRightJoystick = true;
                    }

                }
                else if (VRGlobals.blockRightJoystick && SteamVR_Actions._default.RightJoystick.axis.x == 0 && SteamVR_Actions._default.RightJoystick.axis.y == 0)
                {
                    radialMenu.active = false;
                    VRGlobals.blockRightJoystick = false;
                }
            }

        }




        private float controllerLength = 0.175f;
        private void UpdateRightHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (RightHand)
            {

                if (isSupporting)
                {
                    Vector3 currentRightHandPosition = fromAction.localPosition;

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
                    Vector3 virtualBasePosition = fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength;
                    RightHand.transform.localPosition = virtualBasePosition;
                    //RightHand.transform.localPosition = fromAction.localPosition + rightHandOffset;
                    RightHand.transform.localRotation = fromAction.localRotation;
                    RightHand.transform.Rotate(70, 170, 50);
                }

                //if (CamPatches.stancePanel && CamPatches.leftWrist)
                //{
                //    //CamPatches.stancePanel.transform.position = RightHand.transform.position + new Vector3(x,y,z);
                //    CamPatches.stancePanel.transform.position = CamPatches.leftWrist.position + CamPatches.leftWrist.TransformDirection(new Vector3(0, 0.06f, 0.01f));
                //    CamPatches.stancePanel.transform.localEulerAngles = CamPatches.leftWrist.rotation.eulerAngles;
                //    CamPatches.stancePanel.transform.Rotate(140, 0, 90);

                //    CamPatches.healthPanel.transform.position = CamPatches.leftWrist.position + CamPatches.leftWrist.TransformDirection(new Vector3(-0.1f, 0.01f, 0.05f));
                //    CamPatches.healthPanel.transform.localEulerAngles = CamPatches.leftWrist.rotation.eulerAngles;
                //    CamPatches.healthPanel.transform.Rotate(320, 0, 90);

                //    //Vector3 wristToCamera = (Camera.main.transform.position - CamPatches.leftWrist.position).normalized;
                //    float wristToCamera = Vector3.Dot(CamPatches.healthPanel.transform.transform.forward, (Camera.main.transform.position - CamPatches.healthPanel.transform.position).normalized);

                //    if (wristToCamera > 0.4)
                //    {
                //        if (!showingUI)
                //        {
                //            CamPatches.stancePanel.AnimatedShow(false);
                //            CamPatches.healthPanel.AnimatedShow(false);
                //            showingUI = true;
                //        }
                //    }
                //    else if (showingUI)
                //    {
                //        CamPatches.stancePanel.AnimatedHide();
                //        CamPatches.healthPanel.AnimatedHide();
                //        showingUI = false;
                //    }
                //}

            }
        }


        private void UpdateLeftHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (LeftHand)
            {
                if (leftHandGunIK && (Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.1f || handLock || isSupporting && Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.175f))
                {
                    if (!isSupporting)
                    {
                        // Set target to null so the left hand returns to its normal position on the gun
                        //VRGlobals.ikManager.leftArmIk.solver.target = null;
                        VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                        isSupporting = true;
                        VRGlobals.weaponHolder.transform.localPosition = WeaponPatches.weaponOffset + supportingWeaponHolderOffset;
                        //VRGlobals.weaponHolder.transform.localPosition = supportingWeaponHolderOffset;

                    }
                    if (SteamVR_Actions._default.LeftGrip.state)
                        handLock = true;
                    else
                        handLock = false;

                    LeftHand.transform.localPosition = fromAction.localPosition;
                    //Plugin.MyLog.LogError(Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) + "   |    " + isSupporting);
                }
                else
                {
                    if (isSupporting)
                    {
                        isSupporting = false;
                        VRGlobals.player._markers[0] = LeftHand.transform;
                        //VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;
                        VRGlobals.weaponHolder.transform.localPosition = WeaponPatches.weaponOffset;
                    }
                    Vector3 virtualBasePosition = fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength;
                    LeftHand.transform.localPosition = virtualBasePosition;
                    //if (leftHandGunIK)
                        //Plugin.MyLog.LogWarning(Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) + "   |    " + isSupporting);
                    //LeftHand.transform.localPosition = fromAction.localPosition + leftHandOffset;
                }
                //else
                //{
                //    LeftHand.transform.localPosition = fromAction.localPosition;
                //}
                LeftHand.transform.localRotation = fromAction.localRotation;
                LeftHand.transform.Rotate(-60, 0, 70);

                if (UIPatches.stancePanel)
                {

                    RaycastHit hit;
                    LayerMask mask = 1 << 7;
                    if (Physics.Raycast(LeftHand.transform.position, LeftHand.transform.up * -1, out hit, 2, mask) && hit.collider.name == "camHolder") { 
                        if (!showingUI)
                        {
                            UIPatches.stancePanel.AnimatedShow(false);
                            UIPatches.healthPanel.AnimatedShow(false);
                            //if (UIPatches.quickSlotUi)
                            //    UIPatches.quickSlotUi.active = true;
                            showingUI = true;
                        }

                    }
                    else if (showingUI)
                    {
                        UIPatches.stancePanel.AnimatedHide();
                        UIPatches.healthPanel.AnimatedHide();
                        //if (UIPatches.quickSlotUi)
                        //    UIPatches.quickSlotUi.active = false;
                        showingUI = false;
                    }
                }

            }
        }

        protected void SpawnHands()
        {
            if (!RightHand && VRGlobals.menuVRManager.RightHand)
                RightHand = VRGlobals.menuVRManager.RightHand;
            if (!LeftHand && VRGlobals.menuVRManager.LeftHand)
            {
                LeftHand = VRGlobals.menuVRManager.LeftHand;
            }
        }


        // REMOVE BEFOREGBuFFER maybe

    }


}