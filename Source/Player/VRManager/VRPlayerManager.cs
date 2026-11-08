using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
//using Open.Nat;
using Sirenix.Serialization;
using System.Collections;
using System.Collections.Generic;
using TarkovVR;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using TarkovVR.Source.XR;
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

        // Position offsets
        private static Vector3 leftHandedPrimaryHandPosOffset = new Vector3(-0.03f, -0.055f, 0.03f);
        private static Vector3 leftHandedOtherHandPosOffset = new Vector3(0, 0, 0);
        private static Vector3 rightHandedPrimaryHandPosOffset = new Vector3(0f, 0f, 0f);
        private static Vector3 rightHandedOtherHandPosOffset = new Vector3(0f, 0f, -0.05f);

        public Vector3 initPos;
        public Vector3 x;
        private Vector3 mainHandRotOffset = new Vector3(0, 170, 50);
        public Vector3 secondaryHandRotOffset = new Vector3(-110, 0, 70);
        private Vector3 mainHandPosOffset = Vector3.zero;
        public Vector3 secondaryHandPosOffset = Vector3.zero;
        public static Vector3 headOffset = new Vector3(0.04f, 0.175f, 0.07f);
        public Transform gunTransform;
        public static Transform leftHandGunIK;
        // Where the support hand grabs the gun: the gun's left-hand marker, or with the gun in the left hand its mirror on the gun's
        // right side, where the right hand supports (the same plane RealLeftHandedPatches mirrors the foregrip pose across)
        public static Vector3 SupportGripPoint()
        {
            // A gun with two places for it (the PKM: the handguard, or the box as its aiming clip has it): the one the hand is nearer
            if (VRReloadController.SupportSpotPoint(out Vector3 spotPoint))
                return spotPoint;
            Vector3 point = leftHandGunIK.position;
            if (Handedness.GunInLeftHand && RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                point = RealLeftHandedPatches.ReflectPoint(point, planePoint, planeNormal);
            return point;
        }
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
        //private bool secondaryGripHeld = false;
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
        // Number of selectable (non-disabled) actions in the currently-open interaction context
        // menu. Updated from UIPatches' ActionPanel.method_0 patch. The right joystick Y axis is
        // shared between the menu scroll (ScrollNext/Prev) and crouch, so the menu only needs to
        // "own" the stick when there's more than one action to cycle through.
        public int interactMenuActionCount = 0;
        // true: a single-option interaction menu ("Search" on a body/container, "Take" on loot)
        // does NOT block joystick crouch / consume the scroll stick - only a multi-option menu
        // (which actually needs the stick to navigate) does. false = old behavior (any open
        // interaction menu blocks crouch).
        public static bool allowCrouchWithSingleAction = true;
        // True only when the open interaction menu genuinely needs the right joystick to navigate
        // between actions; single-option menus leave the stick free to crouch. Both the blockCrouch
        // calc and the ScrollHandler gate on this so they stay in lockstep.
        public bool interactMenuOwnsStick => interactMenuOpen && !(allowCrouchWithSingleAction && interactMenuActionCount <= 1);
        public static int LEFT_HAND_ANIMATOR_HASH = UnityEngine.Animator.StringToHash("ReloadFloat");
        private Transform ammoFireModeUi;
        private bool isAmmoCount = false;
        //public VRInputManager inputManager;
        private bool leftHandInAnimation = false;
        public bool showScopeZoom = false;
        public float crouchHeightDiff = 0;

        // The joystick crouch drops the view instantly (crouchHeightDiff is derived from the instant
        // CharacterController.height), but the body/arms ease down on SmoothedPoseLevel - so the view
        // outruns the arms. We ease the value actually applied to the rig toward crouchHeightDiff at the
        // body's rate so view + arms descend together. Tune crouchViewSmoothTime to match the arms.
        public static float crouchViewSmoothTime = 0.14f; // 0 = instant (old behavior); only used when useHeadFollowView = false
        private float displayedCrouchHeightDiff = 0f;
        private float crouchViewVel = 0f;

        public static float headViewYOffset = 0f;                  // (only used by head-follow) trims eye height relative to the head bone
        public static bool drivePoseFromPhysicalCrouch = true;     // true: physically crouching drives the body pose down (the in-game head/anchor follows you)
        public Transform scopeUiPosition;
        public bool isWeapPistol = false;
        public int framesAfterSwitching = 0;
        private bool hapticTriggered = false;

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

            VRInput.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.Any);
            VRInput.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.Any);
            
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
            VRInput.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.RightHand);
            VRInput.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.LeftHand);

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
            /*
            // Ease the manual crouch offset toward the joystick target at the body's rate so the
            // view doesn't drop ahead of the arms (raw crouchHeightDiff stays instant for jump-block/pose).
            if (crouchViewSmoothTime > 0f)
                displayedCrouchHeightDiff = Mathf.SmoothDamp(displayedCrouchHeightDiff, crouchHeightDiff, ref crouchViewVel, crouchViewSmoothTime);
            else
                displayedCrouchHeightDiff = crouchHeightDiff;
            newLocalPos.y -= displayedCrouchHeightDiff;
            */

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

            // Jump/crouch/prone via the right stick are all blocked only when the interaction
            // menu actually OWNS the stick (more than one option to scroll) — a single-option
            // menu ("Take"/"Search") leaves jump free. (crouchHeightDiff != 0 keeps the existing
            // "no jump while crouched" rule independent of menus.)
            blockJump = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOwnsStick || crouchHeightDiff != 0;
            blockCrouch = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOwnsStick;
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
                // With Option A the physical headset is a 1:1 VIEW term, so we must NOT also feed it into the
                // body pose here, or physical crouch double-drops the view. Joystick crouch still drives pose.
                float crouchLevel = drivePoseFromPhysicalCrouch ? (1 - Mathf.Clamp(normalizedHeightPosition, 0, 1)) : 0f;

                // crouchHeightDiff at max will be 0.4 when the joystick is used to crouch which will return a value between 0 and 1 which when subtracted from 1
                // will return a value that can be used to subtract the physical crouch value from and will combine the physical and joystick crouching
                crouchLevel = Mathf.Clamp((1 - crouchHeightDiff / 0.4f) - crouchLevel, 0, 1);

                VRGlobals.player.MovementContext._poseLevel = crouchLevel;
                /*
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
                */
            }

            // Physical hand collision: free hands can't reach through geometry (see HandCollision).
            Interactions.HandCollision.Apply();
        }
        // localpos 0.12 0 -0.085
        // Rot 272.0235 163.5639 283.3635
        // scale 0.0003 0.0003 0.0003
        private float controllerLength = 0.175f;
        private Quaternion initialRightHandRotation;
        private Quaternion rotDiff;
        public bool isEnteringTwoHandedMode = false;
        public Transform rawRightHand;

        private Vector3 inertiaVelocity;
        private float inertiaDamping = 0.95f;  // Controls how quickly inertia fades
        private float returnSpeed = 5.0f;     // Controls how quickly the gun returns to the hand

        // The gun's current muzzle direction (WeaponRoot points -up along the barrel); the twist axis for
        // WeaponWeightFeel's swing/twist split.
        private Vector3 BarrelDir()
        {
            Transform wr = VRGlobals.firearmController ? VRGlobals.firearmController.WeaponRoot : null;
            return wr != null ? -wr.up : rawRightHand.transform.forward;
        }

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
            VRInput.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.RightHand);
            VRInput.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.LeftHand);
            VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
            VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
            // The mirrored mode flips the arms + gun (scale -1) and uses offsets tuned for that (always, in left-handed mode: the real
            // gun-in-the-left-hand is a right-handed-mode state, Handedness.GunInLeftHand)
            bool mirror = Handedness.MirrorLeftHanded;
            Vector3 flip = mirror ? new Vector3(-1, 1, 1) : Vector3.one;
            if (VRGlobals.emptyHands)
                VRGlobals.emptyHands.localScale = flip;
            if (VRGlobals.ikManager && VRGlobals.ikManager.leftArmIk)
                VRGlobals.ikManager.leftArmIk.transform.parent.localScale = flip;
            GunInteractionController heldGun = UnityEngine.Object.FindObjectOfType<GunInteractionController>();
            if (heldGun != null && heldGun.transform.parent != null)
                heldGun.transform.parent.localScale = flip;
            mainHandRotOffset = mirror ? leftHandedPrimaryHandRotOffset : rightHandedPrimaryHandRotOffset;
            secondaryHandRotOffset = mirror ? leftHandedOtherHandRotOffset : rightHandedOtherHandRotOffset;

            mainHandPosOffset = mirror ? leftHandedPrimaryHandPosOffset : rightHandedPrimaryHandPosOffset;
            secondaryHandPosOffset = mirror ? leftHandedOtherHandPosOffset : rightHandedOtherHandPosOffset;

            radialMenu.transform.localPosition = new Vector3(0.0172f, -0.1143f, -0.03f);
            radialMenu.transform.localEulerAngles = new Vector3(270, 127, 80);

            if (WeaponPatches.currentScope)
                WeaponPatches.currentScope.parent.localScale = flip;

            if (VRGlobals.player)
            {
                VRGlobals.player._elbowBends[0] = mirror ? VRGlobals.rightArmBendGoal : VRGlobals.leftArmBendGoal;
                VRGlobals.player._elbowBends[1] = mirror ? VRGlobals.leftArmBendGoal : VRGlobals.rightArmBendGoal;
            }
            if (VRGlobals.leftArmBendGoal != null && VRGlobals.rightArmBendGoal != null)
            {
                VRGlobals.leftArmBendGoal.localPosition = mirror ? new Vector3(1, -2f, -0.8f) : new Vector3(-1, -2f, -0.8f);
                VRGlobals.rightArmBendGoal.localPosition = mirror ? new Vector3(-2.5f, -2f, -0.8f) : new Vector3(2.5f, -2f, -0.8f);
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
                //UIPatches.extractionTimerUi.transform.localPosition = new Vector3(0.037f, 0.12f, -0.015f);
                //UIPatches.extractionTimerUi.transform.localEulerAngles = new Vector3(307, 61, 20);
                UIPatches.extractionTimerUi.transform.localPosition = new Vector3(0.017f, 0.12f, -0.015f);
                UIPatches.extractionTimerUi.transform.localEulerAngles = new Vector3(307, 67, 20);
            }
            if (UIPatches.notifierUi)
            {
                UIPatches.notifierUi.transform.localPosition = new Vector3(0.1247f, 0f, 0.055f);
                UIPatches.notifierUi.transform.localEulerAngles = new Vector3(90, 272, 0);
            }
        }

        public void RightHandedMode()
        {
            VRInput.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.RightHand);
            VRInput.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.LeftHand);
            // The gun in the left hand (Handedness.GunInLeftHand): the left controller drives the rig's gun hand, the right the support hand
            if (Handedness.GunInLeftHand)
            {
                VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
                VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
                VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, PlaceRightControllerHand);
                VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, PlaceLeftControllerHand);
            }
            else
            {
                VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateRightHand);
                VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateLeftHand);
            }

            if (VRGlobals.emptyHands)
                VRGlobals.emptyHands.transform.localScale = new Vector3(1, 1, 1);
            if (VRGlobals.ikManager && VRGlobals.ikManager.leftArmIk)
                VRGlobals.ikManager.leftArmIk.transform.parent.localScale = new Vector3(1, 1, 1);
            mainHandRotOffset = rightHandedPrimaryHandRotOffset;
            secondaryHandRotOffset = rightHandedOtherHandRotOffset;
            mainHandPosOffset = rightHandedPrimaryHandPosOffset;
            secondaryHandPosOffset = rightHandedOtherHandPosOffset;

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
                //UIPatches.extractionTimerUi.transform.localPosition = new Vector3(0.047f, 0.08f, 0.025f);
                //UIPatches.extractionTimerUi.transform.localEulerAngles = new Vector3(88, 83, 175);
                UIPatches.extractionTimerUi.transform.localPosition = new Vector3(0.01f, 0.12f, -0.05f);
                UIPatches.extractionTimerUi.transform.localEulerAngles = new Vector3(18, 183, 285);
            }
            if (UIPatches.notifierUi)
            {
                UIPatches.notifierUi.transform.localPosition = new Vector3(0.12f, 0f, -0.085f);
                UIPatches.notifierUi.transform.localEulerAngles = new Vector3(272, 163, 283);
            }
        }

        // The right-arm IK gets toggled OFF every time a gun is equipped (the gun drives the right
        // side) and is only re-enabled on EmptyHandsController.Spawn (ResetWeaponOnEquipHands). The
        // LEFT arm never gets that toggle, which is why only the RIGHT hand freezes: any transition
        // that skips/races that single re-enable -- incl. the very first spawn before the rig is
        // built -- strands the right hand IK off until you cycle a weapon. So re-assert it while
        // STABLY empty-handed. It MUST be debounced (reassertEmptyDelay): a weapon draw briefly looks
        // empty (EmptyHandsController.Drop disables the IK, then the firearm controller takes over a
        // few frames later), and re-enabling the IK inside that window fought the draw and wedged it
        // ("busy hands" on equip). firearmController != null also hard-gates any gun in play.
        public static bool reassertRightArmIkWhenEmpty = true;
        public static float reassertEmptyDelay = 0.25f; // seconds of continuous clean-empty before we touch the IK
        private float lastNotCleanEmptyTime;

        private void LateUpdate()
        {
            if (VRGlobals.emptyHands && VRGlobals.player && VRGlobals.player.HandsIsEmpty)
            {
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            }

            bool cleanEmpty = reassertRightArmIkWhenEmpty
                && VRGlobals.player != null && VRGlobals.player.HandsIsEmpty
                && VRGlobals.firearmController == null   // no gun active OR mid-equip/draw
                && !VRGlobals.usingItem && !isSupporting
                && !Interactions.EatingInteractionController.ManualActive
                && RightHand != null
                && VRGlobals.ikManager != null && VRGlobals.ikManager.rightArmIk != null
                && VRGlobals.ikManager.rightArmIk.solver != null;
            if (!cleanEmpty)
            {
                lastNotCleanEmptyTime = Time.time; // reset the debounce on any transition / non-empty frame
            }
            else if (Time.time - lastNotCleanEmptyTime > reassertEmptyDelay)
            {
                var rik = VRGlobals.ikManager.rightArmIk;
                // Only touch it when actually wrong, so the steady state has zero churn on the solver.
                if (!rik.enabled || rik.solver.target != RightHand.transform)
                {
                    rik.solver.target = RightHand.transform;
                    rik.enabled = true;
                }
            }

            // Stream our VR hand/arm poses to other players over FIKA (no-op without FIKA; the
            // FIKA-referencing Tick() only JITs when actually called, so this stays load-safe).
            if (TarkovVR.ModSupport.InstalledMods.FIKAInstalled)
                TarkovVR.ModSupport.FIKA.VRArmSync.Tick();
        }

        public Quaternion initialCombinedRotation;
        private Quaternion rightHandRotationOffset;

        //---------------------------------------------------------------------------------------------------------
        // Free hands: a fixed pose in the controller's frame, on Index controllers.
        // The gun sliders only mean something for the gun; a free right hand riding them swung its fingertip ~15cm
        // off. Rotation = the gun hand's at neutral sliders (its pointing finger is within 3 deg of Valve's openxr_poke
        // axis), left = the mirror image of that. Wrist = what lands the pointing fingertip on openxr_poke
        // (valve_controller_knu_1_0_*.json, no other controller's model has that point). IK aims the wrist at RightHand /
        // LeftHand, so everything measured from those transforms keeps its relation to the rendered hand.
        public static bool alignFreeHands = true;    // false = the old placement, for comparing in the headset
        public static Vector3 freeRightHandWrist = new Vector3(0.0147f, 0.064f, -0.1715f);
        public static Vector3 freeRightHandRotation = new Vector3(50f, 170f, 70f);
        public static Vector3 freeLeftHandWrist = new Vector3(-0.0246f, 0.0468f, -0.188f);
        public static Vector3 freeLeftHandRotation = new Vector3(-50f, 10f, 70f);
        public static bool logHandAlignment = false; // every 2s: rendered hand next to SteamVR's hand skeleton, both in the controller's frame

        private bool rightHandPlacedFree;
        private float nextHandAlignmentLogTime;

        private static bool AlignsFreeHands => alignFreeHands && VRGlobals.vrControllerType == "index" && !VRSettings.GetLeftHandedMode();

        private static bool RightHandIsFree => Interactions.ChestHang.Detached
            || Interactions.EatingInteractionController.ManualActive
            || (VRGlobals.player != null && VRGlobals.player.HandsIsEmpty);

        private void PlaceRightHandForGun(VRPose fromAction)
        {
            Quaternion offset = Quaternion.Euler(VRSettings.GetPrimaryHandVertOffset() + mainHandRotOffset.x, mainHandRotOffset.y, mainHandRotOffset.z + VRSettings.GetPrimaryHandHorOffset());
            // The gun in the left hand (Handedness.GunInLeftHand): the left controller holds the gun the way the right one does, mirrored, and the gun itself isn't
            // mirrored. Controller -> gun right-handed = offset * gunInHand; its mirror image = that reflected through the controller's
            // left-right plane (its x: the controllers' poses mirror across it) AND through the gun's own (Weapon_root x), which keeps
            // the gun a real gun: hand rotation = F * (offset * gunInHand) * F * gunInHand^-1. (Reflecting the offset alone, headset
            // round 2, only works if the gun's x lines up with the hand frame's: it didn't - the pistol pointed off to the side.)
            bool real = Handedness.GunInLeftHand;
            RightHand.transform.localRotation = fromAction.localRotation * (real ? MirroredGunHold(offset) : offset);
            RightHand.transform.localPosition = (fromAction.localPosition + (real ? MirrorX(mainHandPosOffset) : mainHandPosOffset)) - fromAction.localRotation * Vector3.forward * controllerLength;
        }

        // The gun's rotation in the rig hand's frame (the rig hand (smoothed) -> RightHandPositioner -> weaponHolder -> Weapon_root), read
        // ONCE when the gun goes into the left hand, with the holder in its restored pose (ChestHang, after Reattach). Read live it fed
        // back: the weapon collision nudges the holder every frame, the nudge changed the hold, the hold moved the gun into a new nudge
        // (headset: "spazzes out everywhere for a while, then randomly stabilizes").
        private static Quaternion gunInHandCached = Quaternion.identity;
        private static EFT.Player.FirearmController gunInHandFor;

        public static void CacheGunInHand()
        {
            gunInHandFor = null;
            gunInHandCached = Quaternion.identity;
            Transform weaponRoot = VRGlobals.firearmController != null ? VRGlobals.firearmController.WeaponRoot : null;
            if (weaponRoot == null || VRGlobals.weaponHolder == null || weaponRoot.parent != VRGlobals.weaponHolder.transform)
                return;
            gunInHandCached = VRGlobals.weaponHolder.transform.localRotation * weaponRoot.localRotation;
            gunInHandFor = VRGlobals.firearmController;
        }

        private static Quaternion MirroredGunHold(Quaternion offset)
        {
            if (gunInHandFor != VRGlobals.firearmController)
                CacheGunInHand();
            Quaternion gunInHand = gunInHandCached;
            Matrix4x4 flip = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            Matrix4x4 hold = flip * Matrix4x4.Rotate(offset * gunInHand) * flip * Matrix4x4.Rotate(Quaternion.Inverse(gunInHand));
            return Quaternion.LookRotation(hold.GetColumn(2), hold.GetColumn(1));
        }

        // The right hand's own natural pose at the right controller, the right-handed way - the target of the right arm while it's off the
        // gun in the left hand (RealLeftHandedPatches; the rig's hands follow the swapped controllers then). A rig child placed from the
        // right controller's pose callback, the same sample and the same moment as the rig hands, and solved by the arm's own IK update too,
        // like every other free hand: a world point handed to EFT's arm pass alone jittered (headset, round 2; the free left hand did the
        // same in round 1 while its own update was off). Index: the free-hand wrist pose; else the gun hand's offsets.
        private Transform rightControllerHand;

        public Transform RightControllerHand
        {
            get
            {
                if (RightHand == null || RightHand.transform.parent == null)
                    return null;
                if (rightControllerHand == null || rightControllerHand.parent != RightHand.transform.parent)
                {
                    if (rightControllerHand == null)
                        rightControllerHand = new GameObject("RightControllerHand").transform;
                    rightControllerHand.SetParent(RightHand.transform.parent, worldPositionStays: false);
                    PlaceRightControllerHand(VRInput.RightHandPose, SteamVR_Input_Sources.RightHand);
                }
                return rightControllerHand;
            }
        }

        private void PlaceRightControllerHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (rightControllerHand == null)
                return;
            Vector3 controllerPosition = fromAction.localPosition;
            Quaternion controllerRotation = fromAction.localRotation;
            if (alignFreeHands && VRGlobals.vrControllerType == "index")
            {
                rightControllerHand.localPosition = controllerPosition + controllerRotation * freeRightHandWrist;
                rightControllerHand.localRotation = controllerRotation * Quaternion.Euler(freeRightHandRotation);
                return;
            }
            rightControllerHand.localPosition = controllerPosition + rightHandedPrimaryHandPosOffset - controllerRotation * Vector3.forward * controllerLength;
            rightControllerHand.localRotation = controllerRotation * Quaternion.Euler(VRSettings.GetPrimaryHandVertOffset() + rightHandedPrimaryHandRotOffset.x,
                rightHandedPrimaryHandRotOffset.y, rightHandedPrimaryHandRotOffset.z + VRSettings.GetPrimaryHandHorOffset());
        }

        // The same for the left hand: its own free pose at the left controller (PlaceOffHand's, unswapped), the left arm's target while
        // the right hand holds the gun alone by the foregrip with the gun in the left hand (RealLeftHandedPatches.SupportHeld)
        private Transform leftControllerHand;

        public Transform LeftControllerHand
        {
            get
            {
                if (LeftHand == null || LeftHand.transform.parent == null)
                    return null;
                if (leftControllerHand == null || leftControllerHand.parent != LeftHand.transform.parent)
                {
                    if (leftControllerHand == null)
                        leftControllerHand = new GameObject("LeftControllerHand").transform;
                    leftControllerHand.SetParent(LeftHand.transform.parent, worldPositionStays: false);
                    PlaceLeftControllerHand(VRInput.LeftHandPose, SteamVR_Input_Sources.LeftHand);
                }
                return leftControllerHand;
            }
        }

        private void PlaceLeftControllerHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (leftControllerHand == null)
                return;
            Vector3 controllerPosition = fromAction.localPosition;
            Quaternion controllerRotation = fromAction.localRotation;
            if (alignFreeHands && VRGlobals.vrControllerType == "index")
            {
                leftControllerHand.localPosition = controllerPosition + controllerRotation * freeLeftHandWrist;
                leftControllerHand.localRotation = controllerRotation * Quaternion.Euler(freeLeftHandRotation);
                return;
            }
            leftControllerHand.localPosition = controllerPosition + rightHandedOtherHandPosOffset - controllerRotation * Vector3.forward * controllerLength;
            leftControllerHand.localRotation = controllerRotation * Quaternion.Euler(VRSettings.GetSecondaryHandVertOffset() + rightHandedOtherHandRotOffset.x,
                rightHandedOtherHandRotOffset.y, VRSettings.GetSecondaryHandHorOffset() + rightHandedOtherHandRotOffset.z);
        }

        // A rotation / point reflected through the plane x = 0 (the controllers' own left-right mirror)
        private static Quaternion MirrorX(Quaternion rotation) => new Quaternion(rotation.x, -rotation.y, -rotation.z, rotation.w);
        private static Vector3 MirrorX(Vector3 point) => new Vector3(-point.x, point.y, point.z);

        // With the gun in the left hand the right controller drives the rig's off hand: its offsets mirrored onto that controller
        private Vector3 OffHandPosition(VRPose fromAction)
        {
            bool swapped = Handedness.GunInLeftHand;
            if (AlignsFreeHands)
                return fromAction.localPosition + fromAction.localRotation * (swapped ? MirrorX(freeLeftHandWrist) : freeLeftHandWrist);
            Vector3 offset = swapped ? MirrorX(secondaryHandPosOffset) : secondaryHandPosOffset;
            return (fromAction.localPosition + offset) - fromAction.localRotation * Vector3.forward * controllerLength;
        }

        public void PlaceOffHand(VRPose fromAction)
        {
            LeftHand.transform.localPosition = OffHandPosition(fromAction);
            LeftHand.transform.localRotation = fromAction.localRotation;
            Quaternion offset = AlignsFreeHands ? Quaternion.Euler(freeLeftHandRotation)
                : Quaternion.Euler(VRSettings.GetSecondaryHandVertOffset() + secondaryHandRotOffset.x, secondaryHandRotOffset.y, VRSettings.GetSecondaryHandHorOffset() + secondaryHandRotOffset.z);
            LeftHand.transform.localRotation *= Handedness.GunInLeftHand ? MirrorX(offset) : offset;
        }

        // Runs at render time, so the bones hold this frame's final pose. Hold still to read it: the IK aimed at last frame's controller pose.
        private void LogHandAlignment(VRPose rightPose)
        {
            if (!AlignsFreeHands || Time.unscaledTime < nextHandAlignmentLogTime || VRGlobals.ikManager == null)
                return;
            nextHandAlignmentLogTime = Time.unscaledTime + 2f;

            // Two-handed, the gun's aim comes from the line between the hands, so only the one-handed hold says anything about the hand
            if ((rightHandPlacedFree || !isSupporting) && VRGlobals.ikManager.rightArmIk != null)
                LogOneHandAlignment(rightHandPlacedFree ? "R free" : "R holding", VRGlobals.ikManager.rightArmIk.solver.bone3.transform, "Base HumanRDigit", OpenXRInput.RIGHT_HAND, rightPose.localPosition, rightPose.localRotation);
            if (!isSupporting && VRGlobals.ikManager.leftArmIk != null)
                LogOneHandAlignment("L", VRGlobals.ikManager.leftArmIk.solver.bone3.transform, "Base HumanLDigit", OpenXRInput.LEFT_HAND,
                    VRInput.LeftHandPose.GetLocalPosition(SteamVR_Input_Sources.LeftHand), VRInput.LeftHandPose.GetLocalRotation(SteamVR_Input_Sources.LeftHand));
        }

        private void LogOneHandAlignment(string handName, Transform palm, string digitPrefix, int hand, Vector3 controllerPosition, Quaternion controllerRotation)
        {
            Transform fingerEnd = palm.Find($"{digitPrefix}21/{digitPrefix}22/{digitPrefix}23");
            if (fingerEnd == null)
                return;

            Transform rig = VRGlobals.vrOffsetter.transform;
            Quaternion toController = Quaternion.Inverse(controllerRotation);
            Quaternion rigRotation = Quaternion.Inverse(rig.rotation);
            Vector3 PointInController(Vector3 worldPoint) => toController * (rig.InverseTransformPoint(worldPoint) - controllerPosition);
            Vector3 DirectionInController(Vector3 worldDirection) => toController * (rigRotation * worldDirection);

            // The rig has no bone at the fingertip: about 2.5cm past the last joint
            string line = $"[HandAlign] {handName} rendered wrist {PointInController(palm.position):F3} fingers {DirectionInController(-palm.right):F2} back {DirectionInController(palm.up):F2}"
                + $" index tip {PointInController(fingerEnd.TransformPoint(-0.025f, 0f, 0f)):F3}";

            if (VRInput.UsingOpenXR
                && OpenXRHandTracking.TryGetJoint(hand, OpenXRHandTracking.WRIST_JOINT, out Vector3 wristPosition, out Quaternion wristRotation)
                && OpenXRHandTracking.TryGetJoint(hand, OpenXRHandTracking.INDEX_TIP_JOINT, out Vector3 tipPosition, out _))
            {
                line += $" | skeleton wrist {(toController * (wristPosition - controllerPosition)):F3} fingers {(toController * (wristRotation * Vector3.forward)):F2} back {(toController * (wristRotation * Vector3.up)):F2}"
                    + $" index tip {(toController * (tipPosition - controllerPosition)):F3}";
                float[] curls = OpenXRHandTracking.GetCurls(hand);
                if (curls != null)
                    line += $" index curl {curls[1]:0.00}";
            }
            Plugin.MyLog.LogInfo(line);
        }

        private void UpdateRightHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (!RightHand)
                return;

            if (VRGlobals.emptyHands != null)
                initialCombinedRotation = VRGlobals.emptyHands.rotation;
            // Last frame's free pose overwrote RightHand; the code below reads it as the gun's frame (two-hand aim, weight filter)
            if (rightHandPlacedFree)
                PlaceRightHandForGun(fromAction);
            VRBoolean primaryGripState = Handedness.GunHandGrip;
            bool blockJoystick = (VRSettings.GetLeftHandedMode()) ? VRGlobals.blockLeftJoystick : VRGlobals.blockRightJoystick;
            // If the joystick is being blocked but the right grip isn't down, keep the joystick blocked until they stop pushing it beyond a certain threshold so the player
            // doesn't immediately move forward after selecting something from the radial menu
            if (blockJoystick && !primaryGripState.state)
            {
                Vector2 joystickInput = Handedness.GunHandJoystick.axis;
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

                // Joystick scope zoom rides BREATH-HOLD now, not grip: under the sling's grip-hold
                // model the grip is always down while holding the gun, so grip-as-modifier stole the
                // stick on every two-handed ADS. Breath-hold (off trigger while aiming) is already the
                // deliberate "steady the shot" modifier - zooming while steadied is natural, and jump/
                // crouch on this stick are meaningless mid-breath-hold.
                // Or the gun hand SQUEEZING its grip (Index force), the old grip modifier: squeezing is a deliberate act apart from
                // holding the gun, so it doesn't take the stick on every aim (headset: "can't hold grip and zoom anymore").
                bool squeezingGrip = primaryGripState.TryGetSqueeze(out bool squeezed) && squeezed;
                if (VRGlobals.firearmController.IsAiming && VRGlobals.vrOpticController && (InputHandlers.BreathingHandler.isHoldingBreath || squeezingGrip))
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


                // Virtual gunstock: with the butt seated in the shoulder pocket, the aim pivots about the
                // shoulder instead of the (noisy) rear hand - ShoulderStock blends the base in/out.
                // Transforms, not positions: ShoulderStock works in tracking space (hideout-safe).
                Vector3 aimBase = ShoulderStock.UpdateAimBase(RightHand.transform, LeftHand.transform, Time.deltaTime);
                Quaternion combinedRotation = Quaternion.LookRotation((LeftHand.transform.position - aimBase).normalized, RightHand.transform.up);

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

                bool weightFeel = false;
                if (InputHandlers.BreathingHandler.isHoldingBreath && VRSettings.SmoothScopeAim())
                {
                    if (VRGlobals.scopeSensitivity * (VRSettings.GetScopeSensitivity() * 10) > 0)
                        smoothingFactor = VRGlobals.scopeSensitivity * (VRSettings.GetScopeSensitivity() * 10);
                    else
                        smoothingFactor = 0.1f * (VRSettings.GetScopeSensitivity() * 10);
                }
                else if (VRSettings.GetWeaponWeightOn())
                {
                    // Rotation gets the WeaponWeightFeel spring below; smoothingFactor keeps driving the
                    // POSITION Slerp exactly as shipped.
                    float weight = VRGlobals.firearmController.ErgonomicWeight;
                    float angleError = Quaternion.Angle(rawRightHand.transform.rotation, RightHand.transform.rotation);
                    float elasticBoost = 1f + (Mathf.Sqrt(angleError) * 0.2f);
                    smoothingFactor = Mathf.Clamp((40f / weight) * elasticBoost, 3f, 65f);
                    weightFeel = true;
                }
                else if (VRSettings.SmoothWeaponAim())
                {
                    smoothingFactor = VRSettings.GetSmoothingSensitivity();
                }
                else
                {
                    smoothingFactor = 50;
                }

                if (weightFeel)
                {
                    rawRightHand.transform.rotation = WeaponWeightFeel.Main.Step(rawRightHand.transform.rotation,
                        RightHand.transform.rotation, BarrelDir(), VRGlobals.firearmController.ErgonomicWeight,
                        true, Time.deltaTime);
                }
                else if (smoothingFactor != 50)
                {
                    rawRightHand.transform.rotation = Quaternion.Slerp(rawRightHand.transform.rotation, RightHand.transform.rotation, smoothingFactor * Time.deltaTime);
                }
                else
                {
                    rawRightHand.transform.rotation = combinedRotation;
                    rawRightHand.transform.localRotation *= rightHandRotationOffset;
                }

                PlaceRightHandForGun(fromAction);

                if (smoothingFactor != 50)
                {
                    Vector3 pos = rawRightHand.transform.position - (inertiaVelocity / inertiaDamping);
                    rawRightHand.transform.position = Vector3.Slerp(pos, RightHand.transform.position, smoothingFactor * Time.deltaTime);
                }
                else
                {
                    rawRightHand.transform.position = RightHand.transform.position;
                }

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
                    rawRightHand.transform.rotation = RightHand.transform.rotation;
                }

                PlaceRightHandForGun(fromAction);

                bool weightFeel = false;
                if (InputHandlers.BreathingHandler.isHoldingBreath && VRSettings.SmoothScopeAim())
                {
                    if (VRGlobals.scopeSensitivity * (VRSettings.GetScopeSensitivity() * 10) > 0)
                        smoothingFactor = VRGlobals.scopeSensitivity * (VRSettings.GetScopeSensitivity() * 10);
                    else
                        smoothingFactor = 0.1f * (VRSettings.GetScopeSensitivity() * 10);
                }
                else if (VRGlobals.firearmController && VRSettings.GetWeaponWeightOn())
                {
                    // Rotation gets the WeaponWeightFeel spring below; smoothingFactor keeps driving the
                    // POSITION Slerp exactly as shipped.
                    float weight = VRGlobals.firearmController.ErgonomicWeight;
                    float angleError = Quaternion.Angle(rawRightHand.transform.rotation, RightHand.transform.rotation);
                    float elasticBoost = 1f + (Mathf.Sqrt(angleError) * 0.2f);
                    smoothingFactor = Mathf.Clamp((40f / weight) * elasticBoost, 3f, 65f);
                    weightFeel = true;
                }
                else if (VRSettings.SmoothWeaponAim())
                {
                    smoothingFactor = VRSettings.GetSmoothingSensitivity();
                }
                else
                {
                    smoothingFactor = 50;
                }

                if (weightFeel)
                {
                    rawRightHand.transform.rotation = WeaponWeightFeel.Main.Step(rawRightHand.transform.rotation,
                        RightHand.transform.rotation, BarrelDir(), VRGlobals.firearmController.ErgonomicWeight,
                        isSupporting, Time.deltaTime);
                }
                else if (smoothingFactor != 50)
                {
                    rawRightHand.transform.rotation = Quaternion.Slerp(rawRightHand.transform.rotation, RightHand.transform.rotation, smoothingFactor * Time.deltaTime);
                }
                else
                {
                    rawRightHand.transform.rotation = RightHand.transform.rotation;
                }

                if (smoothingFactor != 50)
                {
                    rawRightHand.transform.position = Vector3.Slerp(rawRightHand.transform.position, RightHand.transform.position, smoothingFactor * Time.deltaTime);
                }
                else
                {
                    rawRightHand.transform.position = RightHand.transform.position;
                }

                if ((VRGlobals.firearmController || WeaponPatches.grenadeEquipped || WeaponPatches.rangeFinder) && framesAfterSwitching < 2)
                {
                    VRGlobals.weaponHolder.transform.parent.position = rawRightHand.transform.position;
                    VRGlobals.weaponHolder.transform.parent.rotation = rawRightHand.transform.rotation;
                    framesAfterSwitching++;
                }
            }

            // Last, so rawRightHand above keeps following the gun's frame and taking the gun back doesn't swing it
            rightHandPlacedFree = AlignsFreeHands && RightHandIsFree;
            if (rightHandPlacedFree)
            {
                RightHand.transform.localPosition = fromAction.localPosition + fromAction.localRotation * freeRightHandWrist;
                RightHand.transform.localRotation = fromAction.localRotation * Quaternion.Euler(freeRightHandRotation);
            }
            if (logHandAlignment)
                LogHandAlignment(fromAction);
        }

        private void UpdateLeftHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
        {
            leftHandYRotation = fromAction.localRotation.eulerAngles.y;
            VRBoolean secondaryGripState = Handedness.OffHandGrip;

            bool blockJoystick = (VRSettings.GetLeftHandedMode()) ? VRGlobals.blockRightJoystick : VRGlobals.blockLeftJoystick;

            // Using this to determine left hand position when two handing a gun. Very few guns have the left hand grip position very misaligned.
            /*
            if (Input.GetKeyDown(KeyCode.F9) && VRGlobals.firearmController != null && VRGlobals.vrPlayer != null)
            {
                Transform root = VRGlobals.firearmController.WeaponRoot;
                Vector3 local = root.InverseTransformPoint(VRGlobals.vrPlayer.LeftHand.transform.position);
                string name = VRGlobals.firearmController.weaponPrefab_0.name.Replace("(Clone)", "");
                Plugin.MyLog.LogInfo($"[L-hand override] {{ \"{name}\", new Vector3({local.x:F3}f, {local.y:F3}f, {local.z:F3}f) }},");
            }
            */

            // If the joystick is being blocked but the right grip isn't down, keep the joystick blocked until they stop pushing it beyond a certain threshold so the player
            // doesn't immediately move forward after selecting something from the radial menu
            if (blockJoystick && !secondaryGripState.state)
            {
                Vector2 joystickInput = Handedness.OffHandJoystick.axis;
                if (Mathf.Abs(joystickInput.x) < 0.2f && Mathf.Abs(joystickInput.y) < 0.2)
                {
                    if (VRSettings.GetLeftHandedMode())
                        VRGlobals.blockRightJoystick = false;
                    else
                        VRGlobals.blockLeftJoystick = false;
                }
            }

            if (VRGlobals.menuOpen || !LeftHand)
                return;

            // If holding a loot item, skip ALL gun/scope IK logic — just position hand freely
            // Must come BEFORE scopeTransform check so heldItem always wins
            // Same while holding or reaching for a magazine, so the grab never turns into a foregrip snap
            // Same with the gun slung (his ask: "disable all the off hand gun hand pose locking while the gun is slung ... I have my hand
            // around the sight location the hand can sometimes get locked and doesn't rotate": the scope grab below froze the hand, and
            // the foregrip snap took it onto the hanging gun)
            if ((VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.heldItem != null)
                || VRReloadController.OwnsOffHand || VRReloadController.ShellInOffHand || TarkovVR.Source.Player.Interactions.ChestHang.Hanging)
            {
                handLock = false;
                hapticTriggered = false;
                if (isSupporting)
                {
                    isSupporting = false;
                    // Same exit as leaving the support grip below: a pistol hands the left IK target back to the hand
                    if (isWeapPistol)
                        VRGlobals.player._markers[0] = LeftHand.transform;
                }
                PlaceOffHand(fromAction);
                return;
            }

            if (VRGlobals.handsInteractionController && VRGlobals.handsInteractionController.scopeTransform && secondaryGripState.state)
                return;

            // The old rig handed the left hand to Tarkov's reload / malfunction animation here; VRGunController keeps it yours
            if (!VRGunController.KeepLeftHandOnController() && VRGlobals.player && VRGlobals.player.BodyAnimatorCommon.GetFloat(LEFT_HAND_ANIMATOR_HASH) == 1.0)
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
                Vector3 supportGripPoint = SupportGripPoint();
                bool withinDistance = Vector3.Distance(LeftHand.transform.position, supportGripPoint) < 0.1f;

                if (withinDistance)
                {
                    if (!hapticTriggered)
                    {
                        hapticTriggered = true;
                        VRInput.Haptic.Execute(0, 0.25f, 1, 0.4f, Handedness.OffHandSource);                       
                    }
                }
                else if (!isSupporting)
                    hapticTriggered = false;

                // If the player is already in support position with snap enabled and they aren't holding grip, check for the position similar to above 
                // but give the distance between hand and IK position some tolerance so the hand doesn't rapidly swap between support pos and non-support pos
                // when in close vicinity.
                bool withinDistanceAfterSnap = withinDistance && isSupporting && Vector3.Distance(LeftHand.transform.position, supportGripPoint) < 0.175f;
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

                    // This condition should only ever happen if snapping to the gun is disabled
                    if (!isSupporting)
                    {
                        LeftHand.transform.localPosition = OffHandPosition(fromAction);
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
                    PlaceOffHand(fromAction);
                }
            }
            else
            {
                PlaceOffHand(fromAction);
            }
            //else
            //{
            //    LeftHand.transform.localPosition = fromAction.localPosition;
            //}
            Vector3 castOffset = new Vector3(0f, 0f, 0.20f);
            if (UIPatches.stancePanel && UIPatches.healthPanel)
            {
                if (!isSupporting)
                {
                    if (!UIPatches.stancePanel.gameObject.active)
                        UIPatches.stancePanel.gameObject.SetActive(true);

                    RaycastHit hit;
                    LayerMask mask = 1 << 7;
                    if (Physics.SphereCast(LeftHand.transform.position + LeftHand.transform.TransformDirection(castOffset), 0.10f, LeftHand.transform.up * -1, out hit, 2, mask) && hit.collider.name == "camHolder")
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
                    if (Physics.SphereCast(LeftHand.transform.position + LeftHand.transform.TransformDirection(castOffset), 0.10f, LeftHand.transform.up, out hit, 2, mask) && hit.collider.name == "camHolder")
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

        public void ForceUnlockHand()
        {
            bool wasTwoHanding = isSupporting || isEnteringTwoHandedMode;
            handLock = false;
            isSupporting = false;
            //secondaryGripHeld = true;

            if (isWeapPistol)
                VRGlobals.player._markers[0] = LeftHand.transform;

            // Two-handing ended here, not by the left hand letting go of the gun (UpdateRightHand's release): the sling taking the gun
            // from the left hand alone, a hand swap, a menu. Undo what two-handing did to the left arm the way that release does - EFT's
            // left marker back on the left hand, the arm's own update on its controller. Else EFT kept solving the free left arm toward
            // the slung gun's foregrip marker with the arm's own update off (headset: "my left hand is jittering/lagging" walking after
            // the gun went to its slot from the left hand alone, until a foregrip grab and release). Only while the arm is still the
            // two-handed one (no target): a reload latch on it is VRReloadController's.
            var leftArmIk = VRGlobals.ikManager != null ? VRGlobals.ikManager.leftArmIk : null;
            if (wasTwoHanding && leftArmIk != null && leftArmIk.solver != null && leftArmIk.solver.target == null && LeftHand != null)
            {
                if (VRGlobals.player != null)
                    VRGlobals.player._markers[0] = LeftHand.transform;
                leftArmIk.solver.target = LeftHand.transform;
                leftArmIk.enabled = true;
                isEnteringTwoHandedMode = false;
            }
        }

        // Two-handing taken without the grip press at the support point: a pistol's magazine seated with the grip still held
        // (VRReloadController.UpdateSeatedHold), the hand already moved onto the support pose. As if the grip were pressed there now -
        // the same entry as UpdateLeftHand's, and handLock so the hold / toggle setting decides when it lets go, like any support grab.
        // The reload latch still on the arm hands it to EFT's IK itself once it sees isSupporting (VRReloadController.UpdateHandLatch).
        public void TakeSupportGrip()
        {
            if (isSupporting || LeftHand == null || VRGlobals.player == null || rawRightHand == null)
                return;
            if (isWeapPistol)
                VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
            initialRightHandRotation = rawRightHand.transform.rotation;
            isSupporting = true;
            handLock = true;
            hapticTriggered = true;
            if (UIPatches.stancePanel)
            {
                UIPatches.stancePanel.AnimatedHide();
                UIPatches.stancePanel.gameObject.SetActive(false);
            }
            if (UIPatches.healthPanel)
                UIPatches.healthPanel.AnimatedHide();
            if (UIPatches.extractionTimerUi)
                UIPatches.extractionTimerUi.Hide();
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


