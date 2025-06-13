using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using Open.Nat;
using Sirenix.Serialization;
using System.Collections.Generic;
using TarkovVR;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;
using static RootMotion.FinalIK.FBIKChain;

namespace TarkovVR.Source.Player.VRManager
{
    internal abstract class VRPlayerManager : MonoBehaviour
    {

        public static Transform leftWrist;
        private static Vector3 leftHandedPrimaryHandRotOffset = new Vector3(85, 0, 95);
        private static Vector3 rightHandedPrimaryHandRotOffset = new Vector3(0, 170, 50);
        private static Vector3 rightHandedOtherHandRotOffset = new Vector3(-110, 0, 70);
        private static Vector3 leftHandedOtherHandRotOffset = new Vector3(250, 0, 290);
        // Position offsets only needed for left handed mode
        private static Vector3 leftHandedPrimaryHandPosOffset = new Vector3(-0.03f, -0.055f, 0.03f);
        private static Vector3 leftHandedOtherHandPosOffset = new Vector3(0, 0, 0);

        public Vector3 initPos;
        public Vector3 x;
        private Vector3 mainHandRotOffset = new Vector3(0, 170, 50);
        private Vector3 secondaryHandRotOffset = new Vector3(-110, 0, 70);
        private Vector3 mainHandPosOffset = Vector3.zero;
        private Vector3 secondaryHandPosOffset = Vector3.zero;
        public static Vector3 headOffset = new Vector3(0.04f, 0.175f, 0.07f);
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
        public bool blockJump = false;
        public bool blockCrouch = false;
        public bool interactMenuOpen = false;
        public static int LEFT_HAND_ANIMATOR_HASH = UnityEngine.Animator.StringToHash("ReloadFloat");
        private Transform ammoFireModeUi;
        private bool isAmmoCount = false;
        //public VRInputManager inputManager;
        private bool leftHandInAnimation = false;
        public bool showScopeZoom = false;
        public float crouchHeightDiff = 0;
        public Transform scopeUiPosition;
        public bool isWeapPistol = false;
        public int framesAfterSwitching = 0;

        // Cache these at class level - add to your class fields
        private bool cachedLeftHandedMode;
        private int leftHandedModeCheckFrame = -1;
        private Vector3 lastLocalPos = Vector3.zero;
        private bool lastProneState = false;
        private bool lastOrigArmsActive = true;
        private GunInteractionController cachedCurrentGunController;
        private int gunControllerCacheFrame = -1;
        private Transform cachedAmmoFireModeUi;
        private bool lastInteractMenuOpen = false;

        private Vector3 lastHeadPosition;


        public void SetAmmoFireModeUi(Transform uiObject, bool isAmmoCount)
        {
            if (uiObject == null && ammoFireModeUi != null)
                ammoFireModeUi.position = Vector3.zero;
            this.isAmmoCount = isAmmoCount;
            ammoFireModeUi = uiObject;

        }

        private Vector3 currentRightHandVelocity;

        private Queue<Vector3> velocityHistory = new Queue<Vector3>();
        private int maxVelocitySamples = 5;
        private Vector3 lastRightHandPosition;

        public void TrackVelocity(Transform handTransform)
        {
            //Vector3 currentVelocity = (handTransform.localPosition - lastRightHandPosition) / Time.deltaTime;
            //lastRightHandPosition = handTransform.localPosition;

            //// Add the current velocity to the history
            //if (velocityHistory.Count >= maxVelocitySamples)
            //{
            //    velocityHistory.Dequeue(); // Remove the oldest velocity
            //}
            //velocityHistory.Enqueue(currentVelocity);

            // Convert the hand position to the local space of the VR rig
            Vector3 localPosition = VRGlobals.vrOffsetter.transform.InverseTransformPoint(handTransform.position);

            Vector3 currentVelocity = (localPosition - lastRightHandPosition) / Time.deltaTime;
            lastRightHandPosition = localPosition;

            // Add the current velocity to the history
            if (velocityHistory.Count >= maxVelocitySamples)
            {
                velocityHistory.Dequeue(); // Remove the oldest velocity
            }
            velocityHistory.Enqueue(currentVelocity);
        }
        private Vector3 CalculateAverageVelocity()
        {
            Vector3 sum = Vector3.zero;
            foreach (Vector3 velocity in velocityHistory)
            {
                sum += velocity;
            }
            return sum / velocityHistory.Count;
        }

        protected virtual void Awake()
        {
            SpawnHands();
            VRGlobals.VRCam = Camera.main;
            if (VRGlobals.VRCam != null)
                lastHeadPosition = VRGlobals.VRCam.transform.position;
            x.x = 0.075f;
            if (RightHand)
            {
                RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
                if (!radialMenu)
                {
                    radialMenu = new GameObject("radialMenu");
                    radialMenu.layer = 5;
                    radialMenu.transform.parent = RightHand.transform;
                    CircularSegmentUI uiComp = radialMenu.AddComponent<CircularSegmentUI>();
                    uiComp.Init();
                    //uiComp.CreateGunUi(new string[] { "reload.png", "checkAmmo.png", "inspect.png", "fixMalfunction.png", "fireMode_burst.png" });
                    uiComp.CreateGunUi(new string[] { "firstPrimary.png", "secondPrimary.png", "pistol.png", "knife.png" });
                    radialMenu.active = false;
                }
            }
            if (LeftHand)
            {
                LeftHand.transform.parent = VRGlobals.vrOffsetter.transform;
                if (!leftWristUi)
                {
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




            if (!VRGlobals.player || !VRGlobals.ikManager)
                return;

            if (VRGlobals.player.HandsIsEmpty)
            {
                VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
                VRGlobals.ikManager.rightArmIk.solver.target = RightHand.transform;
                VRGlobals.ikManager.rightArmIk.enabled = true;
            }
            else
            {
                VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
            }

            SteamVR_Actions._default.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.Any);
            SteamVR_Actions._default.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.Any);
            
            if (VRSettings.GetLeftHandedMode())
                LeftHandedMode();
            else
                RightHandedMode();           
        }

        public abstract void PositionLeftWristUi();
        public void SetNotificationUi()
        {
            if (UIPatches.notifierUi && leftWristUi)
            {
                UIPatches.notifierUi.transform.SetParent(leftWristUi.transform, false);
                UIPatches.notifierUi.transform.localPosition = new Vector3(0.12f, 0f, -0.085f);
                UIPatches.notifierUi.transform.localEulerAngles = new Vector3(272, 163, 283);
                UIPatches.notifierUi.transform.localScale = new Vector3(0.0003f, 0.0003f, 0.0003f);
            }
        }

        public void OnDisable()
        {
            SteamVR_Actions._default.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.RightHand);
            SteamVR_Actions._default.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.LeftHand);

        }
        public void OnEnable()
        {
            
            if (VRSettings.GetLeftHandedMode())
                LeftHandedMode();
            else
                RightHandedMode();
            
        }


        public Quaternion handsRotation;
        protected virtual void Update()
        {
            //if (Camera.main == null)
            if (VRGlobals.VRCam == null)
                return;

            if (initPos.y == 0)
                initPos = VRGlobals.VRCam.transform.localPosition;
                //initPos = Camera.main.transform.localPosition;

            // Cache expensive calls
            if (leftHandedModeCheckFrame != Time.frameCount)
            {
                cachedLeftHandedMode = VRSettings.GetLeftHandedMode();
                leftHandedModeCheckFrame = Time.frameCount;
            }

            if (gunControllerCacheFrame != Time.frameCount)
            {
                cachedCurrentGunController = WeaponPatches.currentGunInteractController;
                gunControllerCacheFrame = Time.frameCount;
            }

            // Only update position if changed significantly
            Vector3 newLocalPos = initPos * -1 + headOffset;
            newLocalPos.y -= crouchHeightDiff;

            if (Vector3.Distance(newLocalPos, lastLocalPos) > 0.001f)
            {
                VRGlobals.vrOffsetter.transform.localPosition = newLocalPos;
                lastLocalPos = newLocalPos;
            }

            bool currentInteractMenuOpen = (interactionUi && interactionUi.GetChild(3) && interactionUi.GetChild(3).gameObject.active);

            // Only update these if interact menu state changed
            if (currentInteractMenuOpen != lastInteractMenuOpen)
            {
                interactMenuOpen = currentInteractMenuOpen;
                lastInteractMenuOpen = currentInteractMenuOpen;
                var gunWithHighlight = cachedCurrentGunController as GunInteractionController; // Replace with real type

                bool hasHighlightingMesh = gunWithHighlight != null && gunWithHighlight.highlightingMesh;

                if (cachedLeftHandedMode)
                    VRGlobals.blockLeftJoystick = (radialMenu && radialMenu.active) || (UIPatches.quickSlotUi && UIPatches.quickSlotUi.gameObject.active) || interactMenuOpen && cachedCurrentGunController && cachedCurrentGunController.GetComponent<MonoBehaviour>() && hasHighlightingMesh;                            
            }

            blockJump = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOpen || crouchHeightDiff != 0;
            blockCrouch = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOpen;
            // Cache ammoFireModeUi reference
            if (cachedAmmoFireModeUi != ammoFireModeUi)
                cachedAmmoFireModeUi = ammoFireModeUi;

            var gun = cachedCurrentGunController as GunInteractionController;

            if (cachedAmmoFireModeUi != null && gun != null && VRGlobals.player?.HandsController != null && !WeaponPatches.grenadeEquipped)
            {
                if (isAmmoCount)
                {
                    var magazine = gun.magazine;
                    cachedAmmoFireModeUi.rotation = magazine.rotation;
                    cachedAmmoFireModeUi.position = magazine.position;

                    if (cachedLeftHandedMode)
                        cachedAmmoFireModeUi.position += (cachedAmmoFireModeUi.right * -0.07f) + (cachedAmmoFireModeUi.forward * 0.0025f) + (cachedAmmoFireModeUi.up * -0.005f);
                    else
                        cachedAmmoFireModeUi.position += (cachedAmmoFireModeUi.right * 0.03f) + (cachedAmmoFireModeUi.forward * -0.0175f);
                }
                else
                {
                    var fireModeSwitch = gun.GetFireModeSwitch();
                    if (fireModeSwitch != null)
                    {
                        cachedAmmoFireModeUi.rotation = fireModeSwitch.rotation;
                        cachedAmmoFireModeUi.position = fireModeSwitch.position;
                    }

                    if (cachedLeftHandedMode)
                        cachedAmmoFireModeUi.position += (cachedAmmoFireModeUi.right * 0.01f) + (cachedAmmoFireModeUi.forward * 0.0025f) + (cachedAmmoFireModeUi.up * -0.005f);
                    else
                        cachedAmmoFireModeUi.position += (cachedAmmoFireModeUi.right * 0.03f) + (cachedAmmoFireModeUi.forward * -0.0175f);
                }
                cachedAmmoFireModeUi.Rotate(0, 90, 90);
            }

            if (showScopeZoom && UIPatches.opticUi && scopeUiPosition)
            {
                UIPatches.opticUi.transform.rotation = scopeUiPosition.rotation;
                UIPatches.opticUi.transform.Rotate(90, 0, 0);
                UIPatches.opticUi.transform.position = scopeUiPosition.position;
                UIPatches.opticUi.transform.position += (scopeUiPosition.right * 0.05f) + (scopeUiPosition.forward * -0.01f);
            }

            if (VRGlobals.player != null && VRGlobals.player.HandsController != null && VRGlobals.player.HandsController.ControllerGameObject != null && VRGlobals.player.PlayerBones != null && VRGlobals.player.PlayerBones.Ribcage != null && VRGlobals.player.PlayerBones.Ribcage.Original != null)
            {
                handsRotation = VRGlobals.player.HandsRotation;
                VRGlobals.player.HandsController.ControllerGameObject.transform.SetPositionAndRotation(VRGlobals.player.PlayerBones.Ribcage.Original.position, handsRotation);

                // Base Height - the height at which crouching begins.
                float baseHeight = initPos.y * 0.90f; // 90% of init height
                                                      // Floor Height - the height at which full prone is achieved.
                float floorHeight = initPos.y * 0.50f; // Significant crouch/prone

                // Current height position normalized between baseHeight and floorHeight.
                //float normalizedHeightPosition = (Camera.main.transform.localPosition.y - floorHeight) / (baseHeight - floorHeight);
                float normalizedHeightPosition = (VRGlobals.VRCam.transform.localPosition.y - floorHeight) / (baseHeight - floorHeight);

                // Ensure the normalized height is within 0 (full crouch/prone) and 1 (full stand).
                float crouchLevel = 1 - Mathf.Clamp(normalizedHeightPosition, 0, 1);

                // crouchHeightDiff at max will be 0.4 when the joystick is used to crouch which will return a value between 0 and 1 which when subtracted from 1 
                // will return a value that can be used to subtract the physical crouch value from and will combine the physical and joystick crouching
                crouchLevel = Mathf.Clamp((1 - crouchHeightDiff / 0.4f) - crouchLevel, 0, 1);

                VRGlobals.player.MovementContext._poseLevel = crouchLevel;

                // Only change GameObject active states when prone state actually changes
                bool currentProneState = VRGlobals.player.MovementContext.IsInPronePose;
                bool currentOrigArmsActive = VRGlobals.origArmsModel.transform.parent.gameObject.activeSelf;

                if (currentProneState != lastProneState || currentOrigArmsActive != lastOrigArmsActive)
                {
                    if (currentProneState && currentOrigArmsActive)
                    {
                        VRGlobals.origArmsModel.transform.parent.gameObject.SetActive(false);
                        VRGlobals.handsOnlyModel.transform.parent.gameObject.SetActive(true);
                    }
                    else if (!currentProneState && !currentOrigArmsActive && !VRSettings.GetHideArms())
                    {
                        VRGlobals.origArmsModel.transform.parent.gameObject.SetActive(true);
                        VRGlobals.handsOnlyModel.transform.parent.gameObject.SetActive(false);
                    }

                    lastProneState = currentProneState;
                    lastOrigArmsActive = !currentOrigArmsActive; // Will be flipped after SetActive calls
                }
            }
        }
        // localpos 0.12 0 -0.085
        // Rot 272.0235 163.5639 283.3635
        // scale 0.0003 0.0003 0.0003
        private float controllerLength = 0.175f;
        private Quaternion initialRightHandRotation;
        private Quaternion rotDiff;
        private bool isEnteringTwoHandedMode = false;
        public Transform rawRightHand;

        private Vector3 inertiaVelocity;
        private float inertiaDamping = 0.95f;  // Controls how quickly inertia fades
        private float returnSpeed = 5.0f;     // Controls how quickly the gun returns to the hand

        private void ApplyInertia(Transform handTransform, Transform targetHandTransform, Vector3 currentVelocity)
        {
            // Calculate the average velocity from the history
            Vector3 averageVelocity = CalculateAverageVelocity();

            // Trigger inertia when the current frame's velocity drops below the threshold
            if (currentVelocity.magnitude < 0.1f && averageVelocity.magnitude > 0.2f && averageVelocity.magnitude > inertiaVelocity.magnitude)
            {
                inertiaVelocity = averageVelocity;
            }

            // Apply inertia if it's active
            if (inertiaVelocity.magnitude > 0.01f && VRGlobals.firearmController)
            {

                // Apply inertia effect by moving the hand in the direction of inertia velocity in local space
                Vector3 localPosition = VRGlobals.vrOffsetter.transform.InverseTransformPoint(rawRightHand.position);
                localPosition += inertiaVelocity * (x.x * VRGlobals.firearmController.ErgonomicWeight);

                // Convert back to world space and apply the adjusted position
                rawRightHand.position = VRGlobals.vrOffsetter.transform.TransformPoint(localPosition);

                // Dampen the inertia over time
                inertiaVelocity *= inertiaDamping;
            }
        }

        public void LeftHandedMode()
        {
            SteamVR_Actions._default.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.RightHand);
            SteamVR_Actions._default.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.LeftHand);
            SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
            SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
            if (VRGlobals.emptyHands)
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            if (VRGlobals.ikManager && VRGlobals.ikManager.leftArmIk)
                VRGlobals.ikManager.leftArmIk.transform.parent.localScale = new Vector3(-1, 1, 1);
            mainHandRotOffset = leftHandedPrimaryHandRotOffset;
            secondaryHandRotOffset = leftHandedOtherHandRotOffset;

            mainHandPosOffset = leftHandedPrimaryHandPosOffset;
            secondaryHandPosOffset = leftHandedOtherHandPosOffset;

            radialMenu.transform.localPosition = new Vector3(0.0172f, -0.1143f, -0.03f);
            radialMenu.transform.localEulerAngles = new Vector3(270, 127, 80);

            if (WeaponPatches.currentScope)
                WeaponPatches.currentScope.parent.localScale = new Vector3(-1, 1, 1);

            if (VRGlobals.player)
            {
                VRGlobals.player._elbowBends[0] = VRGlobals.rightArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.leftArmBendGoal;
            }

            if (UIPatches.quickSlotUi)
            {
                UIPatches.quickSlotUi.transform.localPosition = new Vector3(0.0472f, -0.1043f, 0.01f);
                UIPatches.quickSlotUi.transform.localEulerAngles = new Vector3(272, 80, 27);
            }
            if (VRGlobals.backpackCollider)
                VRGlobals.backpackCollider.localPosition = new Vector3(0.2f, -0.1f, -0.2f);
            if (VRGlobals.backHolster)
                VRGlobals.backHolster.localPosition = new Vector3(-0.2f, -0.1f, -0.2f);

            if (UIPatches.stancePanel)
            {
                UIPatches.stancePanel.transform.localPosition = new Vector3(0.1f, 0, -0.075f);
                UIPatches.stancePanel.transform.localEulerAngles = new Vector3(90, 93, 180);
            }
            if (UIPatches.healthPanel)
                UIPatches.healthPanel.transform.localEulerAngles = new Vector3(270, 269, 180);
            if (UIPatches.extractionTimerUi)
            {
                UIPatches.extractionTimerUi.transform.localPosition = new Vector3(0.037f, 0.12f, -0.015f);
                UIPatches.extractionTimerUi.transform.localEulerAngles = new Vector3(307, 61, 20);
            }
            if (UIPatches.notifierUi)
            {
                UIPatches.notifierUi.transform.localPosition = new Vector3(0.1247f, 0f, 0.055f);
                UIPatches.notifierUi.transform.localEulerAngles = new Vector3(90, 272, 0);
            }
        }

        public void RightHandedMode()
        {
            SteamVR_Actions._default.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.RightHand);
            SteamVR_Actions._default.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.LeftHand);
            SteamVR_Actions._default.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
            SteamVR_Actions._default.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);

            if (VRGlobals.emptyHands)
                VRGlobals.emptyHands.transform.localScale = new Vector3(1, 1, 1);
            if (VRGlobals.ikManager && VRGlobals.ikManager.leftArmIk)
                VRGlobals.ikManager.leftArmIk.transform.parent.localScale = new Vector3(1, 1, 1);
            mainHandRotOffset = rightHandedPrimaryHandRotOffset;
            secondaryHandRotOffset = rightHandedOtherHandRotOffset;
            mainHandPosOffset = Vector3.zero;
            secondaryHandPosOffset = Vector3.zero;

            radialMenu.transform.localPosition = new Vector3(-0.0728f, -0.1343f, 0);
            radialMenu.transform.localEulerAngles = new Vector3(290, 252, 80);

            if (WeaponPatches.currentScope)
                WeaponPatches.currentScope.parent.localScale = new Vector3(1, 1, 1);

            if (VRGlobals.player)
            {

                VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;
            }
            if (UIPatches.quickSlotUi)
            {
                UIPatches.quickSlotUi.transform.localPosition = new Vector3(-0.0728f, -0.1343f, 0);
                UIPatches.quickSlotUi.transform.localEulerAngles = new Vector3(290, 252, 80);
            }
            if (VRGlobals.backpackCollider)
                VRGlobals.backpackCollider.localPosition = new Vector3(-0.2f, -0.1f, -0.2f);
            if (VRGlobals.backHolster)
                VRGlobals.backHolster.localPosition = new Vector3(0.2f, -0.1f, -0.2f);

            if (UIPatches.stancePanel)
            {
                UIPatches.stancePanel.transform.localPosition = new Vector3(0.1f, 0, 0.03f);
                UIPatches.stancePanel.transform.localEulerAngles = new Vector3(270, 87, 0);
            }
            if (UIPatches.healthPanel)
                UIPatches.healthPanel.transform.localEulerAngles = new Vector3(270, 87, 0);

            if (UIPatches.extractionTimerUi)
            {
                UIPatches.extractionTimerUi.transform.localPosition = new Vector3(0.047f, 0.08f, 0.025f);
                UIPatches.extractionTimerUi.transform.localEulerAngles = new Vector3(88, 83, 175);
            }
            if (UIPatches.notifierUi)
            {
                UIPatches.notifierUi.transform.localPosition = new Vector3(0.12f, 0f, -0.085f);
                UIPatches.notifierUi.transform.localEulerAngles = new Vector3(272, 163, 283);
            }
        }

        private void LateUpdate()
        {
            if (VRGlobals.emptyHands && VRGlobals.player && VRGlobals.player.HandsIsEmpty)
            {
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            }
        }

        public Quaternion initialCombinedRotation;
        private Quaternion rightHandRotationOffset;
        private void UpdateRightHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (!RightHand)
                return;

            if (VRGlobals.emptyHands != null)
                initialCombinedRotation = VRGlobals.emptyHands.rotation;
            SteamVR_Action_Boolean primaryGripState = (VRSettings.GetLeftHandedMode()) ? SteamVR_Actions._default.LeftGrip : SteamVR_Actions._default.RightGrip;
            bool blockJoystick = (VRSettings.GetLeftHandedMode()) ? VRGlobals.blockLeftJoystick : VRGlobals.blockRightJoystick;
            // If the joystick is being blocked but the right grip isn't down, keep the joystick blocked until they stop pushing it beyond a certain threshold so the player
            // doesn't immediately move forward after selecting something from the radial menu
            if (blockJoystick && !primaryGripState.state)
            {
                Vector2 joystickInput = (VRSettings.GetLeftHandedMode()) ? SteamVR_Actions._default.LeftJoystick.axis : SteamVR_Actions._default.RightJoystick.axis;
                if (Mathf.Abs(joystickInput.x) < 0.2f && Mathf.Abs(joystickInput.y) < 0.2)
                {
                    if (VRSettings.GetLeftHandedMode())
                        VRGlobals.blockLeftJoystick = false;
                    else
                        VRGlobals.blockRightJoystick = false;
                }
            }


            if (VRGlobals.firearmController && isSupporting && !isWeapPistol)
            {

                if (VRGlobals.firearmController.IsAiming && VRGlobals.vrOpticController && primaryGripState.state)
                {
                    VRGlobals.vrOpticController.handleJoystickZoomDial();
                    if (VRSettings.GetLeftHandedMode())
                        VRGlobals.blockLeftJoystick = true;
                    else
                        VRGlobals.blockRightJoystick = true;
                }
                else
                {
                    if (VRSettings.GetLeftHandedMode())
                        VRGlobals.blockLeftJoystick = false;
                    else
                        VRGlobals.blockRightJoystick = false;
                }


                Quaternion combinedRotation = Quaternion.LookRotation((LeftHand.transform.position - RightHand.transform.position).normalized, RightHand.transform.up);

                if (!isEnteringTwoHandedMode)
                {
                    framesAfterSwitching = 0;
                    // Disable other left hand IK tracking
                    VRGlobals.ikManager.leftArmIk.solver.target = null;
                    VRGlobals.ikManager.leftArmIk.enabled = false;
                    // Set the left hand IK back to the original gun position
                    VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                    isEnteringTwoHandedMode = true;
                    // First we get the current right hands local rotation
                    initialRightHandRotation = RightHand.transform.localRotation;
                    // Then we get the new right hands rotation for when it points towards the left hand
                    RightHand.transform.rotation = combinedRotation;
                    // Then we get the local rotation for the new rotation
                    Quaternion newLocalRotation = RightHand.transform.localRotation;
                    // Then we get the difference between the two, and this value will be applied to every local rotation when two handing, so that the gun always starts
                    // with the exact same rotation as when you were one handing it.
                    rightHandRotationOffset = Quaternion.Inverse(newLocalRotation) * initialRightHandRotation;
                }
                // If aim smoothing is on, then we need to apply the rotation offset before the slerp is applied, so just use the right hand for this since it's going
                // to be wiped over further down
                RightHand.transform.rotation = combinedRotation;
                RightHand.transform.localRotation *= rightHandRotationOffset;
                // If you're using scopes and holding your breath then the smoothing factor will be below 50 to make it easier to aim with greater zoom levels
                if (smoothingFactor < 50)
                {
                    // Only apply the smoothing if the option is enabled though, otherwise just use the normal rotation
                    if (VRSettings.SmoothScopeAim())
                        rawRightHand.transform.rotation = Quaternion.Slerp(rawRightHand.transform.rotation, RightHand.transform.rotation, smoothingFactor * Time.deltaTime);
                    else
                    {
                        rawRightHand.transform.rotation = combinedRotation;
                        // Now we apply our offset so the rotation matches the rotation from when you were one handing
                        rawRightHand.transform.localRotation *= rightHandRotationOffset;
                    }
                }
                // For non-scoped weapons when the smoothing is still on or weapon weight is on
                else if (VRSettings.SmoothWeaponAim() || VRSettings.GetWeaponWeightOn())
                {
                    float smoothing = smoothingFactor;
                    if (VRSettings.SmoothWeaponAim())
                        smoothing = VRSettings.GetSmoothingSensitivity();
                    if (VRSettings.GetWeaponWeightOn())
                        smoothing = smoothingFactor / VRGlobals.firearmController.ErgonomicWeight;
                    rawRightHand.transform.rotation = Quaternion.Slerp(rawRightHand.transform.rotation, RightHand.transform.rotation, smoothing * Time.deltaTime);
                }
                // If no smoothing is applied, then just use the regular rotation
                else
                {
                    rawRightHand.transform.rotation = combinedRotation;
                    // Now we apply our offset so the rotation matches the rotation from when you were one handing
                    rawRightHand.transform.localRotation *= rightHandRotationOffset;
                }


                //rawRightHand.transform.localRotation *= rightHandRotationOffset;

                RightHand.transform.localRotation = fromAction.localRotation;
                RightHand.transform.Rotate(VRSettings.GetPrimaryHandVertOffset() + mainHandRotOffset.x, mainHandRotOffset.y, mainHandRotOffset.z + VRSettings.GetPrimaryHandHorOffset());
                Vector3 virtualBasePosition = ((fromAction.localPosition + mainHandPosOffset) - fromAction.localRotation * Vector3.forward * controllerLength);
                RightHand.transform.localPosition = virtualBasePosition;

                if (VRSettings.SmoothWeaponAim() || VRSettings.GetWeaponWeightOn())
                {
                    float smoothing = 50;
                    if (VRSettings.SmoothWeaponAim())
                        smoothing = VRSettings.GetSmoothingSensitivity();
                    if (VRSettings.GetWeaponWeightOn())
                        smoothing = smoothingFactor / VRGlobals.firearmController.ErgonomicWeight;

                    Vector3 pos = rawRightHand.transform.position - (inertiaVelocity / inertiaDamping);

                    rawRightHand.transform.position = Vector3.Slerp(pos, RightHand.transform.position, smoothing * Time.deltaTime);

                }
                else
                    rawRightHand.transform.position = RightHand.transform.position;

                // The first 2 frames after swapping to or from two handed mode are slow to update for some reason so the gun will appear at a very weird angle
                // just for a moment, so just manually set the weaponholder pos and rotation here.
                if (framesAfterSwitching < 2)
                {
                    VRGlobals.weaponHolder.transform.parent.position = rawRightHand.transform.position;
                    VRGlobals.weaponHolder.transform.parent.rotation = rawRightHand.transform.rotation;
                    framesAfterSwitching++;
                }
            }
            else
            {
                if (isWeapPistol && isSupporting && !isEnteringTwoHandedMode)
                {
                    // Disable other left hand IK tracking
                    VRGlobals.ikManager.leftArmIk.solver.target = null;
                    VRGlobals.ikManager.leftArmIk.enabled = false;
                    isEnteringTwoHandedMode = true;
                }
                if (isWeapPistol && !isSupporting && isEnteringTwoHandedMode)
                {
                    VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;
                    VRGlobals.ikManager.leftArmIk.enabled = true;
                    isEnteringTwoHandedMode = false;
                }
                if (isEnteringTwoHandedMode && !isWeapPistol)
                {
                    framesAfterSwitching = 0;
                    isEnteringTwoHandedMode = false;
                    VRGlobals.player._markers[0] = LeftHand.transform;
                    VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;
                    VRGlobals.ikManager.leftArmIk.enabled = true;
                    //VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;
                    //VRGlobals.weaponHolder.transform.localPosition = WeaponPatches.weaponOffset;
                    //VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                    //VRGlobals.firearmController.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);
                    rawRightHand.transform.rotation = RightHand.transform.rotation;
                }

                RightHand.transform.localRotation = fromAction.localRotation;
                RightHand.transform.Rotate(VRSettings.GetPrimaryHandVertOffset() + mainHandRotOffset.x, mainHandRotOffset.y, mainHandRotOffset.z + VRSettings.GetPrimaryHandHorOffset());

                Vector3 virtualBasePosition = (fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength);
                RightHand.transform.localPosition = virtualBasePosition + mainHandPosOffset;

                // Smoothing if weight is on
                if (VRGlobals.firearmController && VRSettings.GetWeaponWeightOn())
                {
                    float smoothing = smoothingFactor;
                    if (VRSettings.SmoothWeaponAim())
                        smoothing = VRSettings.GetSmoothingSensitivity();
                    if (VRSettings.GetWeaponWeightOn())
                        smoothing = smoothingFactor / VRGlobals.firearmController.ErgonomicWeight;
                    rawRightHand.transform.rotation = Quaternion.Slerp(rawRightHand.transform.rotation, RightHand.transform.rotation, smoothing * Time.deltaTime);
                }
                else
                    rawRightHand.transform.rotation = RightHand.transform.rotation;

                if (VRGlobals.firearmController && VRSettings.GetWeaponWeightOn())
                {
                    float smoothing = smoothingFactor;
                    if (VRSettings.SmoothWeaponAim())
                        smoothing = VRSettings.GetSmoothingSensitivity();
                    if (VRSettings.GetWeaponWeightOn())
                        smoothing = smoothingFactor / VRGlobals.firearmController.ErgonomicWeight;
                    //rawRightHand.transform.position = Vector3.Slerp(preVelocityRightHandPos, RightHand.transform.position, smoothing * Time.deltaTime);
                    rawRightHand.transform.position = Vector3.Slerp(rawRightHand.transform.position, RightHand.transform.position, smoothing * Time.deltaTime);
                }
                else
                    rawRightHand.transform.position = RightHand.transform.position;

                if ((VRGlobals.firearmController || WeaponPatches.grenadeEquipped || WeaponPatches.rangeFinder) && framesAfterSwitching < 2)
                {
                    VRGlobals.weaponHolder.transform.parent.position = rawRightHand.transform.position;
                    VRGlobals.weaponHolder.transform.parent.rotation = rawRightHand.transform.rotation;
                    framesAfterSwitching++;
                }
            }
        }

        private void UpdateLeftHand(SteamVR_Action_Pose fromAction, SteamVR_Input_Sources fromSource)
        {
            leftHandYRotation = fromAction.localRotation.eulerAngles.y;
            SteamVR_Action_Boolean secondaryGripState = (VRSettings.GetLeftHandedMode()) ? SteamVR_Actions._default.RightGrip : SteamVR_Actions._default.LeftGrip;

            bool blockJoystick = (VRSettings.GetLeftHandedMode()) ? VRGlobals.blockRightJoystick : VRGlobals.blockLeftJoystick;
            // If the joystick is being blocked but the right grip isn't down, keep the joystick blocked until they stop pushing it beyond a certain threshold so the player
            // doesn't immediately move forward after selecting something from the radial menu
            if (blockJoystick && !secondaryGripState.state)
            {
                Vector2 joystickInput = (VRSettings.GetLeftHandedMode()) ? SteamVR_Actions._default.RightJoystick.axis : SteamVR_Actions._default.LeftJoystick.axis;
                if (Mathf.Abs(joystickInput.x) < 0.2f && Mathf.Abs(joystickInput.y) < 0.2)
                {
                    if (VRSettings.GetLeftHandedMode())
                        VRGlobals.blockRightJoystick = false;
                    else
                        VRGlobals.blockLeftJoystick = false;
                }
            }

            if (!LeftHand || (VRGlobals.handsInteractionController && VRGlobals.handsInteractionController.scopeTransform && secondaryGripState.state))
                return;


            if (VRGlobals.player && VRGlobals.player.BodyAnimatorCommon.GetFloat(LEFT_HAND_ANIMATOR_HASH) == 1.0)
            {
                if (!leftHandInAnimation)
                {
                    VRGlobals.ikManager.leftArmIk.solver.target = null;
                    VRGlobals.ikManager.leftArmIk.enabled = false;
                    VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                    leftHandInAnimation = true;
                }
                return;
            }
            else if (leftHandInAnimation)
            {
                if (isSupporting)
                    VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                else
                {
                    VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;
                    VRGlobals.ikManager.leftArmIk.enabled = true;
                    VRGlobals.player._markers[0] = LeftHand.transform;
                }

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
                        if (isWeapPistol)
                            VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                        initialRightHandRotation = rawRightHand.transform.rotation;
                        // Set left hand target to the original left hand target
                        //VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                        isSupporting = true;
                        if (UIPatches.stancePanel)
                            UIPatches.stancePanel.AnimatedHide();
                        if (UIPatches.healthPanel)
                            UIPatches.healthPanel.AnimatedHide();
                        // Stance panel is stubborn and still doesn't go away after AnimatedHide sometimes so set it to inactive
                        if (UIPatches.stancePanel)
                            UIPatches.stancePanel.gameObject.SetActive(false);
                        if (UIPatches.extractionTimerUi)
                            UIPatches.extractionTimerUi.Hide();
                    }
                    if (VRSettings.GetSupportGunHoldToggle())
                    {
                        if (secondaryGripState.state)
                            handLock = true;
                        else
                            handLock = false;
                    }
                    else
                    {
                        if (!isSupporting && secondaryGripState.stateDown)
                            handLock = true;
                        else if (isSupporting && secondaryGripState.stateDown)
                        {
                            isSupporting = false;
                            //VRGlobals.player._markers[0] = LeftHand.transform;
                            ////VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;

                            handLock = false;
                        }
                    }
                    // This condition should only even happen if snapping to the gun is disabled
                    if (!isSupporting)
                    {
                        SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.1f, VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
                        Vector3 virtualBasePosition = (fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength) + secondaryHandPosOffset;
                        LeftHand.transform.localPosition = virtualBasePosition;
                    }
                    else
                    {
                        //Vector3 virtualBasePosition = fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength;
                        //LeftHand.transform.localPosition = virtualBasePosition;
                        LeftHand.transform.localPosition = fromAction.localPosition;
                    }


                }
                else
                {
                    if (isSupporting)
                    {
                        isSupporting = false;
                        if (isWeapPistol)
                            VRGlobals.player._markers[0] = LeftHand.transform;
                        //VRGlobals.player._markers[0] = LeftHand.transform;
                        ////VRGlobals.ikManager.leftArmIk.solver.target = LeftHand.transform;
                        //VRGlobals.weaponHolder.transform.localPosition = WeaponPatches.weaponOffset;
                        //VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                        //VRGlobals.firearmController.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);

                    }
                    Vector3 virtualBasePosition = ((fromAction.localPosition + secondaryHandPosOffset) - fromAction.localRotation * Vector3.forward * controllerLength);
                    LeftHand.transform.localPosition = virtualBasePosition;
                    //if (leftHandGunIK)
                    //LeftHand.transform.localPosition = fromAction.localPosition + leftHandOffset;
                    LeftHand.transform.localRotation = fromAction.localRotation;
                    LeftHand.transform.Rotate(VRSettings.GetSecondaryHandVertOffset() + secondaryHandRotOffset.x, secondaryHandRotOffset.y, VRSettings.GetSecondaryHandHorOffset() + secondaryHandRotOffset.z);

                }
            }
            else
            {
                Vector3 virtualBasePosition = (fromAction.localPosition - fromAction.localRotation * Vector3.forward * controllerLength) + secondaryHandPosOffset;
                LeftHand.transform.localPosition = virtualBasePosition;
                LeftHand.transform.localRotation = fromAction.localRotation;
                LeftHand.transform.Rotate(VRSettings.GetSecondaryHandVertOffset() + secondaryHandRotOffset.x, secondaryHandRotOffset.y, VRSettings.GetSecondaryHandHorOffset() + secondaryHandRotOffset.z);
            }
            //else
            //{
            //    LeftHand.transform.localPosition = fromAction.localPosition;
            //}
            if (UIPatches.stancePanel && UIPatches.healthPanel)
            {
                if (!isSupporting)
                {
                    if (!UIPatches.stancePanel.gameObject.active)
                        UIPatches.stancePanel.gameObject.SetActive(true);

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
                else if (showingHealthUi)
                {
                    UIPatches.stancePanel.AnimatedHide();
                    UIPatches.healthPanel.AnimatedHide();
                    showingHealthUi = false;
                }
            }
            if (UIPatches.extractionTimerUi)
            {
                if (!isSupporting)
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
                else if (showingExtractionUi)
                {
                    UIPatches.extractionTimerUi.Hide();
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

            if (!rawRightHand)
            {

                rawRightHand = new GameObject("rawRightHand").transform;
                rawRightHand.transform.parent = VRGlobals.vrOffsetter.transform;
            }
        }


        // REMOVE BEFOREGBuFFER maybe

    }


}


