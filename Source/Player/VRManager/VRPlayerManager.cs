using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using Sirenix.Serialization;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;

namespace TarkovVR.Source.Player.VRManager
{
    internal abstract class VRPlayerManager : MonoBehaviour
    {

        public static Transform leftWrist;



        public Vector3 initPos;
        public Vector3 x;
        private Vector3 nonSupportRightHandRotOffset = new Vector3(0, 170, 50);
        public static Vector3 headOffset = new Vector3(0.04f, 0.175f, 0.07f);
        private Vector3 supportingLeftHandOffset = new Vector3(0,0f,0);
        public Transform gunTransform;
        public static Transform leftHandGunIK;
        public bool leftHandOnScope = false;

        public float leftHandYRotation = 0f;
        public float leftHandZRotation = 0f;
        public static float smoothingFactor = 100f; // Adjust this value to lower to increase aim smoothing - 20 is barely noticable so good baseline

        // VR Origin and body stuff
        public GameObject LeftHand = null;
        public GameObject RightHand = null;

        public GameObject radialMenu;
        protected GameObject leftWristUi;


        public bool isSupporting = false;
        public bool wasSupporting = false;
        private float timeHeld = 0;
        public Transform interactionUi;
        public Vector3 startingPlace;
        private bool showingHealthUi = false;
        private bool showingExtractionUi = false;
        private bool handLock = false;
        public bool blockJump = true;
        public bool blockCrouch = true;
        public bool interactMenuOpen = false;
        private static int LEFT_HAND_ANIMATOR_HASH = UnityEngine.Animator.StringToHash("ReloadFloat");
        private Transform ammoFireModeUi;
        private bool isAmmoCount = false;
        //public VRInputManager inputManager;
        private bool leftHandInAnimation = false;
        public bool showScopeZoom = false;
        public float crouchHeightDiff = 0;
        public Transform scopeUiPosition;
        public bool isWeapPistol = false;


        public void SetAmmoFireModeUi(Transform uiObject, bool isAmmoCount) {
            if (uiObject == null && ammoFireModeUi != null)
                ammoFireModeUi.position = Vector3.zero;
            this.isAmmoCount = isAmmoCount;
            ammoFireModeUi = uiObject;

        }


        protected virtual void Awake()
        {
            x.y = -1;
            SpawnHands();
            Plugin.MyLog.LogWarning("Create hands");
            if (RightHand) { 
                RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
                if (!radialMenu)
                {
                    radialMenu = new GameObject("radialMenu");
                    radialMenu.layer = 5;
                    radialMenu.transform.parent = RightHand.transform;
                    CircularSegmentUI uiComp = radialMenu.AddComponent<CircularSegmentUI>();
                    uiComp.Init();
                    //uiComp.CreateGunUi(new string[] { "reload.png", "checkAmmo.png", "inspect.png", "fixMalfunction.png", "fireMode_burst.png" });
                    uiComp.CreateGunUi(new string[] { "firstPrimary.png", "secondPrimary.png", "pistol.png"});
                    radialMenu.active = false;
                }
            }
            if (LeftHand) { 
                LeftHand.transform.parent = VRGlobals.vrOffsetter.transform;
                if (!leftWristUi) {
                    leftWristUi = new GameObject("leftWristUi");
                    leftWristUi.layer = 5;
                    leftWristUi.transform.parent = LeftHand.transform;
                    leftWristUi.AddComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                    //leftWristUi.transform.localPosition = new Vector3(0, -0.05f, 0.015f);
                    //leftWristUi.transform.localEulerAngles = Vector3.zero;

                    //UIPatches.healthPanel.transform.parent = leftWristUi.transform;
                    //UIPatches.healthPanel.transform.localPosition = Vector3.zero;
                    //UIPatches.healthPanel.transform.localEulerAngles = new Vector3(270,87,0);

                    //UIPatches.stancePanel.transform.parent = leftWristUi.transform;
                    //UIPatches.stancePanel.transform.localPosition = new Vector3(0.1f, 0, 0.03f);
                    //UIPatches.stancePanel.transform.localEulerAngles = new Vector3(270, 87, 0);

                }
            }
            SteamVR_Actions._default.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.RightHand);
            SteamVR_Actions._default.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.LeftHand);

            SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);

            //if (inputManager == null) {
            //    inputManager = new VRInputManager();
            //}

        }

        public abstract void PositionLeftWristUi();
        public void SetNotificationUi()
        {
            if (UIPatches.notifierUi)
            {
                UIPatches.notifierUi.transform.parent = leftWristUi.transform;
                UIPatches.notifierUi.transform.localPosition = new Vector3(0.12f, 0f, -0.085f);
                UIPatches.notifierUi.transform.localEulerAngles = new Vector3(272, 163, 283);
                UIPatches.notifierUi.transform.localScale = new Vector3(0.0003f, 0.0003f, 0.0003f);
            }
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


        private static Transform lastHitGunComp;
        protected virtual void Update()
        {
            if (Camera.main == null)
                return;
            if (initPos.y == 0)
                initPos = Camera.main.transform.localPosition;

            Vector3 newLocalPos = initPos * -1 + headOffset;
            newLocalPos.y -= crouchHeightDiff;
            VRGlobals.vrOffsetter.transform.localPosition = newLocalPos;

            interactMenuOpen = (interactionUi && interactionUi.gameObject.active);
            blockJump = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOpen || crouchHeightDiff != 0 || (VRGlobals.firearmController && VRGlobals.firearmController.IsAiming && SteamVR_Actions._default.RightGrip.state);
            blockCrouch = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOpen || (VRGlobals.firearmController && VRGlobals.firearmController.IsAiming && SteamVR_Actions._default.RightGrip.state);

            // For Ammo do the exact same vu
            if (ammoFireModeUi != null)
            {
                if (isAmmoCount)
                {
                    ammoFireModeUi.rotation = WeaponPatches.currentGunInteractController.magazine.rotation;
                    ammoFireModeUi.Rotate(0, 90, 90);
                    ammoFireModeUi.position = WeaponPatches.currentGunInteractController.magazine.position;
                    //ammoFireModeUi.localPosition += x;
                    ammoFireModeUi.position += (ammoFireModeUi.right * 0.03f) + (ammoFireModeUi.forward * -0.0175f);
                }
                else {
                    ammoFireModeUi.rotation = WeaponPatches.currentGunInteractController.GetFireModeSwitch().rotation;
                    ammoFireModeUi.Rotate(0, 90, 90);
                    ammoFireModeUi.position = WeaponPatches.currentGunInteractController.GetFireModeSwitch().position;
                    ammoFireModeUi.position += (ammoFireModeUi.right* 0.03f) + (ammoFireModeUi.forward* -0.0175f);

                }
            }

            if (showScopeZoom && UIPatches.opticUi && scopeUiPosition) {
                UIPatches.opticUi.transform.rotation = scopeUiPosition.rotation;
                UIPatches.opticUi.transform.Rotate(90,0,0);
                UIPatches.opticUi.transform.position = scopeUiPosition.position;
                UIPatches.opticUi.transform.position += (scopeUiPosition.right * 0.05f) + (scopeUiPosition.forward * -0.01f);
            }
        }
        // localpos 0.12 0 -0.085
        // Rot 272.0235 163.5639 283.3635
        // scale 0.0003 0.0003 0.0003
        private float controllerLength = 0.175f;

        private void UpdateRightHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (!RightHand)
                return;

            // Block right joystick is usually triggered when in a radial menu, and if you have something selected when you release the 
            // grip you'll start rotating because of the right joystick being pushed, so don't allow for right joystick movement until
            // its below a certain point



            if (VRGlobals.blockRightJoystick == true && !SteamVR_Actions._default.RightGrip.GetState(SteamVR_Input_Sources.RightHand)) {
                Vector2 joystickInput = SteamVR_Actions._default.RightJoystick.axis;
                if (Mathf.Abs(joystickInput.x) < 0.2f && Mathf.Abs(joystickInput.y) < 0.2)
                    VRGlobals.blockRightJoystick = false;
            }

            if (VRGlobals.firearmController && isSupporting && !isWeapPistol)
            {
                if (VRGlobals.firearmController.IsAiming && VRGlobals.vrOpticController && SteamVR_Actions._default.RightGrip.state) { 
                    VRGlobals.vrOpticController.handleJoystickZoomDial();
                    VRGlobals.blockRightJoystick = true;
                }
                else
                    VRGlobals.blockRightJoystick = false;


                // Step 3: Remove the yaw component from the local rotation
                Quaternion rotationWithoutYaw = Quaternion.Euler(0, fromAction.localRotation.eulerAngles.y, 0);
                Quaternion inverseYawRotation = Quaternion.Inverse(rotationWithoutYaw);
                Quaternion rollRotation = inverseYawRotation * fromAction.localRotation;

                // Step 4: Calculate the roll angle from the adjusted rotation
                Vector3 va1 = rollRotation * Vector3.right;
                float rollValue = Mathf.Atan2(va1.y, va1.x) * Mathf.Rad2Deg;


                Vector3 forwardDirection = fromAction.localRotation * Vector3.forward;
                float pitchAngwle = Vector3.Angle(forwardDirection, Vector3.up);


                Vector3 toLeftHand = LeftHand.transform.position - RightHand.transform.position;
                Vector3 flatToHand = new Vector3(toLeftHand.x, 0, toLeftHand.z); // For yaw

                // Calculate yaw to face the left hand horizontally
                Quaternion yawRotation = Quaternion.LookRotation(flatToHand, Vector3.up);
                // Correcting pitch calculation: 
                float pitchAngle = Mathf.Atan2(toLeftHand.y, flatToHand.magnitude) * Mathf.Rad2Deg;
                //float pitchAngle = Mathf.Atan2(0, flatToHand.magnitude) * Mathf.Rad2Deg;

                // Separate rotation offsets for clearer control
                Quaternion offsetRotation = Quaternion.Euler(340, 0, -90); // Apply pitch offset here

                //if (pitchAngwle < 15 || (pitchAngwle < 40 && rollValue < -150))
                //    rollValue = 15;
                //Quaternion combinedRotation = yawRotation * Quaternion.Euler(-pitchAngle, 0, 0) * offsetRotation * rollRotation;
                Quaternion combinedRotation = yawRotation * Quaternion.Euler(-pitchAngle, 0, 0) * offsetRotation;


                combinedRotation *= Quaternion.Euler(rollValue * -1, 130, -30);
                //combinedRotation *= Quaternion.Euler(num * -1, 130, -30);
                //combinedRotation *= Quaternion.Euler(0, 130, -30);



                if (smoothingFactor < 50) {
                    if (VRSettings.SmoothScopeAim())
                        RightHand.transform.rotation = Quaternion.Slerp(RightHand.transform.rotation, combinedRotation, smoothingFactor * Time.deltaTime);
                    else
                        RightHand.transform.rotation = combinedRotation;
                }
                else if (VRSettings.SmoothWeaponAim())
                    RightHand.transform.rotation = Quaternion.Slerp(RightHand.transform.rotation, combinedRotation, VRSettings.GetSmoothingSensitivity() * Time.deltaTime);
                else
                    RightHand.transform.rotation = combinedRotation;


                // Calculate the forward direction based on rotation
                Vector3 forwardMovement = fromAction.localRotation * Vector3.forward * 0.2f;
                // Calculate the up direction based on rotation
                Vector3 upMovement = fromAction.localRotation * Vector3.up * 0.1f;
                // Calculate the right direction based on rotation
                Vector3 rightMovement = fromAction.localRotation * Vector3.right * x.z;
                Vector3 finalPosition = fromAction.localPosition - forwardMovement + upMovement + rightMovement;
                RightHand.transform.localPosition = finalPosition;
            }
            else
            {
                RightHand.transform.localRotation = fromAction.localRotation;
                RightHand.transform.Rotate(VRSettings.GetWeaponAngleOffset(),nonSupportRightHandRotOffset.y, nonSupportRightHandRotOffset.z);
                Vector3 virtualBasePosition = fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength;
                RightHand.transform.localPosition = virtualBasePosition;
            }
            // RightHand.transform.rotation.eulerAngles y should be between 65 and 250
        }
        Vector3 NormalizeEulerAngles(Vector3 eulerAngles)
        {
            eulerAngles.x = NormalizeAngle(eulerAngles.x);
            eulerAngles.y = NormalizeAngle(eulerAngles.y);
            eulerAngles.z = NormalizeAngle(eulerAngles.z);
            return eulerAngles;
        }

        // Normalize a single angle to the range of -180 to 180 degrees
        float NormalizeAngle(float angle)
        {
            while (angle > 180f) angle -= 360f;
            while (angle < -180f) angle += 360f;
            return angle;
        }
        private void UpdateLeftHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            leftHandYRotation = fromAction.localRotation.eulerAngles.y;
            //leftHandZRotation = fromAction.localRotation.eulerAngles.z;
            if (!LeftHand || (VRGlobals.handsInteractionController && VRGlobals.handsInteractionController.scopeTransform && SteamVR_Actions._default.LeftGrip.state)) 
                return;

            if (VRGlobals.player && VRGlobals.player.BodyAnimatorCommon.GetFloat(LEFT_HAND_ANIMATOR_HASH) == 1.0)
            {
                if (!leftHandInAnimation)
                {
                    VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                    leftHandInAnimation = true;
                }
                return;
            }
            else if (leftHandInAnimation) {
                if (isSupporting)
                    VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                else
                    VRGlobals.player._markers[0] = LeftHand.transform;
                leftHandInAnimation = false;
            }
            if (leftHandGunIK)
            {
                bool withinDistance = Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.1f;


                // If the player is already in support position with snap enabled and they aren't holding grip, check for the position similar to above 
                // but give the distance between hand and IK position some tolerance so the hand doesn't rapidly swap between support pos and non-support pos
                // when in close vicinity.
                bool withinDistanceAfterSnap = withinDistance && isSupporting && Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.175f;
                if (withinDistance || handLock || withinDistanceAfterSnap)
                {
                    if (!isSupporting && (!VRSettings.GetSnapToGun() || handLock))
                    {
                        // Set left hand target to the original left hand target
                        VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                        isSupporting = true;
                    }
                    if (VRSettings.GetSupportGunHoldToggle())
                    {
                        if (SteamVR_Actions._default.LeftGrip.state)
                            handLock = true;
                        else
                            handLock = false;
                    }
                    else
                    {
                        if (!isSupporting && SteamVR_Actions._default.LeftGrip.stateDown)
                            handLock = true;
                        else if (isSupporting && SteamVR_Actions._default.LeftGrip.stateDown) {
                            isSupporting = false;
                            VRGlobals.player._markers[0] = LeftHand.transform;
                            //VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;
                            VRGlobals.weaponHolder.transform.localPosition = WeaponPatches.weaponOffset;
                            handLock = false;
                        }
                    }
                    // This condition should only even happen if snapping to the gun is disabled
                    if (!isSupporting)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.1f, SteamVR_Input_Sources.LeftHand);
                        Vector3 virtualBasePosition = fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength;
                        LeftHand.transform.localPosition = virtualBasePosition;
                    }
                    else
                        LeftHand.transform.localPosition = fromAction.localPosition + supportingLeftHandOffset;

                    float heightDifference = LeftHand.transform.position.y - RightHand.transform.position.y;
                    // as the left hand goes above the right hand more, the gun rolls around all wonky, so fix this by drawing a line between
                    // the left and right hand, then moving it closer out and up from that line, or the opposite I dunno, it just works
                    //if (heightDifference > 0)
                    //{
                    //    // Adjust the left hand's position based on the height difference
                    //    // Drop the left hand down and slightly out further based on the line and height difference
                    //    Vector3 leftHandAdjustedPosition = LeftHand.transform.localPosition;
                    //    Vector3 toLeftHand = LeftHand.transform.position - RightHand.transform.position;
                    //    leftHandAdjustedPosition.x -= Mathf.Abs(heightDifference) * x.x; // Drop down (adjust the factor as needed)
                    //    leftHandAdjustedPosition.y -= Mathf.Abs(heightDifference) * x.y; // Drop down (adjust the factor as needed)
                    //    leftHandAdjustedPosition.z -= Mathf.Abs(heightDifference) * x.z; // Drop down (adjust the factor as needed)
                    //    //leftHandAdjustedPosition += toLeftHand.normalized * Mathf.Abs(heightDifference) * x.x; // Move out further (adjust the factor as needed)
                    //    //leftHandAdjustedPosition.y -= Mathf.Abs(heightDifference) * -0.75f; // Drop down (adjust the factor as needed)
                    //    //leftHandAdjustedPosition += toLeftHand.normalized * Mathf.Abs(heightDifference) * -0.75f; // Move out further (adjust the factor as needed)
                    //    LeftHand.transform.localPosition = leftHandAdjustedPosition;
                    //}
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
                    LeftHand.transform.localRotation = fromAction.localRotation;
                    LeftHand.transform.Rotate(-60, 0, 70);
                }
            }
            else {
                Vector3 virtualBasePosition = fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength;
                LeftHand.transform.localPosition = virtualBasePosition;
                LeftHand.transform.localRotation = fromAction.localRotation;
                LeftHand.transform.Rotate(-60, 0, 70);
            }
            //else
            //{
            //    LeftHand.transform.localPosition = fromAction.localPosition;
            //}
            if (!isSupporting)
            {
                if (UIPatches.stancePanel)
                {

                    RaycastHit hit;
                    LayerMask mask = 1 << 7;
                    if (Physics.Raycast(LeftHand.transform.position, LeftHand.transform.up * -1, out hit, 2, mask) && hit.collider.name == "camHolder")
                    {
                        if (!showingHealthUi)
                        {
                            UIPatches.stancePanel.AnimatedShow(false);
                            UIPatches.healthPanel.AnimatedShow(false);
                            //if (UIPatches.quickSlotUi)
                            //    UIPatches.quickSlotUi.active = true;
                            showingHealthUi = true;
                        }

                    }
                    else if (showingHealthUi)
                    {
                        UIPatches.stancePanel.AnimatedHide();
                        UIPatches.healthPanel.AnimatedHide();
                        //if (UIPatches.quickSlotUi)
                        //    UIPatches.quickSlotUi.active = false;
                        showingHealthUi = false;
                    }
                }
                if (UIPatches.extractionTimerUi)
                {
                    RaycastHit hit;
                    LayerMask mask = 1 << 7;
                    if (Physics.Raycast(LeftHand.transform.position, LeftHand.transform.up * 1, out hit, 2, mask) && hit.collider.name == "camHolder")
                    {
                        if (!showingExtractionUi)
                        {
                            UIPatches.extractionTimerUi.Reveal();
                            UIPatches.extractionTimerUi.ShowTimer(true, true);
                            showingExtractionUi = true;
                        }
                    }
                    else if (showingExtractionUi)
                    {
                        UIPatches.extractionTimerUi.Hide();
                        showingExtractionUi = false;
                    }
                }
            }
            if (isSupporting && showingHealthUi) {
                UIPatches.stancePanel.AnimatedHide();
                UIPatches.healthPanel.AnimatedHide();
                showingHealthUi = false;
            }
            if (isSupporting && showingExtractionUi) { 
                UIPatches.extractionTimerUi.Reveal();
                showingExtractionUi = false;
            }
        }

        protected void SpawnHands()
        {
            if (!RightHand && VRGlobals.menuVRManager.RightHand)
                RightHand = VRGlobals.menuVRManager.RightHand;
            if (!LeftHand && VRGlobals.menuVRManager.LeftHand)
                LeftHand = VRGlobals.menuVRManager.LeftHand;
        }


        // REMOVE BEFOREGBuFFER maybe

    }


}