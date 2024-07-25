using EFT.InventoryLogic;
using Sirenix.Serialization;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;
using static UnityEngine.UIElements.UIRAtlasAllocator;

namespace TarkovVR.Source.Player.VRManager
{
    internal abstract class VRPlayerManager : MonoBehaviour
    {

        public static Transform leftWrist;



        public Vector3 initPos;
        private Vector3 x;
        private Vector3 y;
        private bool useRightHandRot = false;
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
            // Set the rig collider position to 75% of the cameras height so it appears under the head
            //if (VRGlobals.ikManager)
            //    InitVRPatches.rigCollider.position = newLocalPos;
            //    newLocalPos.y = Camera.main.transform.localPosition.y * 0.75f;


            VRGlobals.vrOffsetter.transform.localPosition = newLocalPos;

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

            //if (radialMenu)
            //{

            //    RaycastHit hit;
            //    LayerMask mask = 1 << 7;
            //    if (SteamVR_Actions._default.RightGrip.state && Physics.Raycast(RightHand.transform.position, RightHand.transform.up * -1, out hit, 2, mask) && hit.collider.name == "camHolder")
            //    {
            //        if (!radialMenu.active)
            //        {
            //            radialMenu.active = true;
            //            VRGlobals.blockRightJoystick = true;
            //        }

            //    }
            //    else if (VRGlobals.blockRightJoystick && SteamVR_Actions._default.RightJoystick.axis.x == 0 && SteamVR_Actions._default.RightJoystick.axis.y == 0)
            //    {
            //        radialMenu.active = false;
            //        VRGlobals.blockRightJoystick = false;
            //    }
            //}
            interactMenuOpen = (interactionUi && interactionUi.gameObject.active);
            blockJump = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOpen || crouchHeightDiff != 0 || (VRGlobals.firearmController && VRGlobals.firearmController.IsAiming);
            blockCrouch = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOpen || (VRGlobals.firearmController && VRGlobals.firearmController.IsAiming);

            // AmmoCountPanel - on ShowFireMode position on selector position 
            // rotation = ammopanel rotation, localrotation = 0 90 90
            // psosition = ammopanel pos, localpos = -0.0175 0.03 0
            // On BattleUIComponentAnimation.Hide() with name == AmmoPanel stop updating position

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
            if (RightHand)
            {

                if (VRGlobals.blockRightJoystick == true && !SteamVR_Actions._default.RightGrip.GetState(SteamVR_Input_Sources.RightHand)) {
                    Vector2 joystickInput = SteamVR_Actions._default.RightJoystick.axis;
                    if (Mathf.Abs(joystickInput.x) < 0.2f && Mathf.Abs(joystickInput.y) < 0.2)
                        VRGlobals.blockRightJoystick = false;
                }
                if (isSupporting && !isWeapPistol)
                {
                    if (VRGlobals.firearmController.IsAiming && VRGlobals.vrOpticController)
                        VRGlobals.vrOpticController.handleJoystickZoomDial();
                    Vector3 toLeftHand = LeftHand.transform.position - RightHand.transform.position;
                    Vector3 flatToHand = new Vector3(toLeftHand.x, 0, toLeftHand.z); // For yaw

                    // Calculate yaw to face the left hand horizontally
                    Quaternion yawRotation = Quaternion.LookRotation(flatToHand, Vector3.up);
                    // Correcting pitch calculation: 
                    float pitchAngle = Mathf.Atan2(toLeftHand.y, flatToHand.magnitude) * Mathf.Rad2Deg;
                    //float pitchAngle = Mathf.Atan2(0, flatToHand.magnitude) * Mathf.Rad2Deg;

                    // Separate rotation offsets for clearer control
                    Quaternion offsetRotation = Quaternion.Euler(340, 0, -90); // Apply pitch offset here


                    //Quaternion combinedRotation = yawRotation * Quaternion.Euler(-pitchAngle, 0, 0) * offsetRotation * rollRotation;
                    Quaternion combinedRotation = yawRotation * Quaternion.Euler(-pitchAngle, 0, 0) * offsetRotation;
                    // Additional correction for yaw and roll offsets if necessary
                    combinedRotation *= Quaternion.Euler(0, 130, -30);


                    // Interpolate between the two Z rotation angles
                    float interpolatedZRotation = Mathf.LerpAngle(fromAction.localRotation.eulerAngles.z, leftHandZRotation, 0.5f);
                    combinedRotation *= Quaternion.Euler(interpolatedZRotation * -1, 0, 0);

                    //combinedRotation *= Quaternion.Euler(fromAction.localRotation.eulerAngles.z * -1, 0, 0);


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
                }
                else
                {
                    RightHand.transform.localRotation = fromAction.localRotation;
                    RightHand.transform.Rotate(70, 170, 50);
                }
                Vector3 virtualBasePosition = fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength;
                RightHand.transform.localPosition = virtualBasePosition;
            }
        }


        private void UpdateLeftHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            leftHandYRotation = fromAction.localRotation.eulerAngles.y;
            leftHandZRotation = fromAction.localRotation.eulerAngles.z;
            if (VRGlobals.handsInteractionController && VRGlobals.handsInteractionController.scopeTransform && SteamVR_Actions._default.LeftGrip.state) 
                return;

            if (LeftHand)
            {
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

                if (leftHandGunIK && (Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.1f || handLock || isSupporting && Vector3.Distance(LeftHand.transform.position, leftHandGunIK.position) < 0.175f))
                {
                    if (!isSupporting)
                    {
                        // Set target to null so the left hand returns to its normal position on the gun
                        //VRGlobals.ikManager.leftArmIk.solver.target = null;
                        VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                        isSupporting = true;
                        //VRGlobals.weaponHolder.transform.localPosition = WeaponPatches.weaponOffset + supportingWeaponHolderOffset;
                        //VRGlobals.weaponHolder.transform.localPosition = supportingWeaponHolderOffset;

                    }
                    if (SteamVR_Actions._default.LeftGrip.state)
                        handLock = true;
                    else
                        handLock = false;

                    //Vector3 leftHandWorldPosition = WeaponPatches.previousLeftHandMarker.TransformPoint(leftHandSupportOffset);
                    //LeftHand.transform.position = leftHandWorldPosition;
                    LeftHand.transform.localPosition = fromAction.localPosition + supportingLeftHandOffset;
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