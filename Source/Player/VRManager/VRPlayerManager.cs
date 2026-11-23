using System.Collections.Generic;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Player.Body;

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
        // Where the support hand grabs the gun: the gun's left-hand marker. With the gun in the left hand it is that marker mirrored
        // onto the gun's right side, through the plane RealLeftHandedPatches mirrors the foregrip pose across.
        public static Vector3 SupportGripPoint()
        {
            // A gun with two support spots (the PKM: the handguard, or the box as its aiming clip holds it) uses the one nearer the hand
            if (VRReloadController.Belt.SupportSpotPoint(out Vector3 spotPoint))
                return spotPoint;
            Vector3 point = leftHandGunIK.position;
            if (Handedness.GunInLeftHand && RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                point = RealLeftHandedPatches.ReflectPoint(point, planePoint, planeNormal);
            return point;
        }
        public bool leftHandOnScope = false;

        public float leftHandYRotation = 0f;
        public float leftHandZRotation = 0f;
        public static float smoothingFactor = 100f; // lower = more aim smoothing; 20 is barely noticeable

        // VR Origin and body stuff
        public GameObject LeftHand = null;
        public GameObject RightHand = null;

        public GameObject radialMenu;
        protected GameObject leftWristUi;


        public bool isSupporting = false;
        public bool wasSupporting = false;
        public Transform interactionUi;
        public Vector3 startingPlace;
        private bool showingHealthUi = false;
        private bool showingExtractionUi = false;
        private bool handLock = false;
        public bool blockJump = false;
        public bool blockCrouch = false;
        public bool interactMenuOpen = false;
        // Selectable (non-disabled) actions in the open interaction menu, set by UIPatches' ActionPanel.method_0 patch.
        // The right stick's Y is shared between the menu scroll (ScrollNext/Prev) and crouch.
        public int interactMenuActionCount = 0;
        // The open interaction menu needs the right stick to navigate, except a single-option menu ("Search", "Take"), which leaves the
        // stick free to crouch. blockCrouch and ScrollHandler both gate on this.
        public bool interactMenuOwnsStick => interactMenuOpen && interactMenuActionCount > 1;
        // The frame on which the gun hand's stick last zoomed the scope being looked through. Set each frame in UpdateRightHand;
        // one frame of slack covers the update order.
        private int scopeZoomStickFrame = -10;
        public bool ScopeZoomOwnsStick => Time.frameCount - scopeZoomStickFrame <= 1;
        public static int LEFT_HAND_ANIMATOR_HASH = UnityEngine.Animator.StringToHash("ReloadFloat");
        private Transform ammoFireModeUi;
        private bool isAmmoCount = false;
        private bool leftHandInAnimation = false;
        public bool showScopeZoom = false;
        public float crouchHeightDiff = 0;

        public static bool drivePoseFromPhysicalCrouch = true;     // true: physically crouching drives the body pose down (the in-game head/anchor follows)
        public Transform scopeUiPosition;
        public bool isWeapPistol = false;
        public int framesAfterSwitching = 0;
        private readonly TarkovVR.Source.Misc.ZoneEntryBlip supportZoneBlip = new TarkovVR.Source.Misc.ZoneEntryBlip();   // the foregrip's reach

        private bool cachedLeftHandedMode;
        private int leftHandedModeCheckFrame = -1;
        private GunInteractionController cachedCurrentGunController;
        private int gunControllerCacheFrame = -1;
        private Transform cachedAmmoFireModeUi;
        private bool lastInteractMenuOpen = false; 



        public void SetAmmoFireModeUi(Transform uiObject, bool isAmmoCount)
        {
            if (uiObject == null && ammoFireModeUi != null)
                ammoFireModeUi.position = Vector3.zero;
            this.isAmmoCount = isAmmoCount;
            ammoFireModeUi = uiObject;

        }

        private Queue<Vector3> velocityHistory = new Queue<Vector3>();
        private int maxVelocitySamples = 5;
        private Vector3 lastRightHandPosition;

        public void TrackVelocity(Transform handTransform)
        {
            // Hand position in the VR rig's local space
            Vector3 localPosition = VRGlobals.vrOffsetter.transform.InverseTransformPoint(handTransform.position);

            Vector3 currentVelocity = (localPosition - lastRightHandPosition) / Time.deltaTime;
            lastRightHandPosition = localPosition;

            if (velocityHistory.Count >= maxVelocitySamples)
            {
                velocityHistory.Dequeue();
            }
            velocityHistory.Enqueue(currentVelocity);
        }

        protected virtual void Awake()
        {
            SpawnHands();
            VRGlobals.VRCam = Camera.main;
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
                }
            }




            if (!VRGlobals.player || !VRGlobals.ikManager)
                return;

            if (VRGlobals.player.HandsIsEmpty)
            {
                HandTargets.Set(VRGlobals.ikManager.leftArmIk, LeftHand.transform, HandOwner.Rig);
                VRGlobals.ikManager.leftArmIk.enabled = true;
                HandTargets.Set(VRGlobals.ikManager.rightArmIk, RightHand.transform, HandOwner.Rig);
                VRGlobals.ikManager.rightArmIk.enabled = true;
            }
            else
            {
                HandTargets.Set(VRGlobals.ikManager.leftArmIk, LeftHand.transform, HandOwner.Rig);
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
            HeldGunDetail.Teardown();
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
            // Runs before the VRCam check: VRCam is null at times in the hideout, and the held gun still needs its detail kept
            HeldGunDetail.Tick();
            if (VRGlobals.VRCam == null)
                return;

            if (initPos.y == 0)
                initPos = VRGlobals.VRCam.transform.localPosition;

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

            ViewHeight.Sample(VRGlobals.player, VRGlobals.VRCam, headOffset);
            Vector3 newLocalPos = initPos * -1 + ViewHeight.HeadOffset(headOffset);
            newLocalPos.y -= crouchHeightDiff;

            // Written every frame: a gate on small changes would apply the body follow's slow initPos compensation in visible steps
            VRGlobals.vrOffsetter.transform.localPosition = newLocalPos;

            bool currentInteractMenuOpen = (interactionUi && interactionUi.GetChild(3) && interactionUi.GetChild(3).gameObject.active);

            // Only update these if interact menu state changed
            if (currentInteractMenuOpen != lastInteractMenuOpen)
            {
                interactMenuOpen = currentInteractMenuOpen;
                lastInteractMenuOpen = currentInteractMenuOpen;
                var gunWithHighlight = cachedCurrentGunController as GunInteractionController;

                bool hasHighlightingMesh = gunWithHighlight != null && gunWithHighlight.highlightingMesh;

                if (cachedLeftHandedMode)
                    VRGlobals.blockLeftJoystick = (radialMenu && radialMenu.active) || (UIPatches.quickSlotUi && UIPatches.quickSlotUi.gameObject.active) || interactMenuOpen && cachedCurrentGunController && cachedCurrentGunController.GetComponent<MonoBehaviour>() && hasHighlightingMesh;                            
            }

            // The right stick's jump/crouch/prone are blocked while the interaction menu or the scope zoom owns the stick.
            // crouchHeightDiff != 0: no jump while crouched.
            blockJump = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOwnsStick || ScopeZoomOwnsStick || crouchHeightDiff != 0;
            blockCrouch = VRGlobals.blockRightJoystick || VRGlobals.menuOpen || interactMenuOwnsStick || ScopeZoomOwnsStick;
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

            // Skipped on a mounted gun: Tarkov places the hands object on the mount, and the pose comes from the seat
            if (VRGlobals.player != null && VRGlobals.player.HandsController != null && VRGlobals.player.HandsController.ControllerGameObject != null && VRGlobals.player.PlayerBones != null && VRGlobals.player.PlayerBones.Ribcage != null && VRGlobals.player.PlayerBones.Ribcage.Original != null
                && !Interactions.MountedGun.Active)
            {
                handsRotation = VRGlobals.player.HandsRotation;
                VRGlobals.player.HandsController.ControllerGameObject.transform.SetPositionAndRotation(VRGlobals.player.PlayerBones.Ribcage.Original.position, handsRotation);

                // Base Height - the height at which crouching begins.
                float baseHeight = initPos.y * 0.90f; // 90% of init height
                                                      // Floor Height - the height at which full prone is achieved.
                float floorHeight = initPos.y * 0.50f; // Significant crouch/prone

                // Current height position normalized between baseHeight and floorHeight.
                float normalizedHeightPosition = (VRGlobals.VRCam.transform.localPosition.y - floorHeight) / (baseHeight - floorHeight);

                // Clamped to 0 (full crouch/prone) .. 1 (full stand). With drivePoseFromPhysicalCrouch off the headset height moves the
                // view only and isn't fed into the body pose. Joystick crouch still drives the pose.
                float crouchLevel = drivePoseFromPhysicalCrouch ? (1 - Mathf.Clamp(normalizedHeightPosition, 0, 1)) : 0f;

                // crouchHeightDiff is 0.4 at full joystick crouch: this combines the joystick and the physical crouch into one pose level
                crouchLevel = Mathf.Clamp((1 - crouchHeightDiff / 0.4f) - crouchLevel, 0, 1);

                VRGlobals.player.MovementContext._poseLevel = crouchLevel;
            }

            // Physical hand collision: free hands can't reach through geometry (see HandCollision).
            Interactions.HandCollision.Apply();
        }
        private float controllerLength = 0.175f;
        private Quaternion initialRightHandRotation;
        public bool isEnteringTwoHandedMode = false;
        public Transform rawRightHand;

        private Vector3 inertiaVelocity;
        private float inertiaDamping = 0.95f;  // Controls how quickly inertia fades

        // The gun's current muzzle direction (WeaponRoot points -up along the barrel); the twist axis for
        // WeaponWeightFeel's swing/twist split.
        private Vector3 BarrelDir()
        {
            Transform weaponRoot = VRGlobals.firearmController ? VRGlobals.firearmController.WeaponRoot : null;
            return weaponRoot != null ? -weaponRoot.up : rawRightHand.transform.forward;
        }

        public void LeftHandedMode()
        {
            VRInput.RightHandPose.RemoveAllListeners(SteamVR_Input_Sources.RightHand);
            VRInput.LeftHandPose.RemoveAllListeners(SteamVR_Input_Sources.LeftHand);
            VRInput.LeftHandPose.AddOnUpdateListener(SteamVR_Input_Sources.LeftHand, UpdateRightHand);
            VRInput.RightHandPose.AddOnUpdateListener(SteamVR_Input_Sources.RightHand, UpdateLeftHand);
            // Mirrored mode flips the arms and the gun (scale -1) and uses offsets tuned for that. Holding the real gun in the left hand
            // is a right-handed-mode state (Handedness.GunInLeftHand).
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
                UIPatches.extractionTimerUi.transform.localPosition = new Vector3(0.01f, 0.12f, -0.05f);
                UIPatches.extractionTimerUi.transform.localEulerAngles = new Vector3(18, 183, 285);
            }
            if (UIPatches.notifierUi)
            {
                UIPatches.notifierUi.transform.localPosition = new Vector3(0.12f, 0f, -0.085f);
                UIPatches.notifierUi.transform.localEulerAngles = new Vector3(272, 163, 283);
            }
        }

        // The right-arm IK is turned off on every gun equip and only re-enabled in EmptyHandsController.Spawn (ResetWeaponOnEquipHands).
        // A transition that skips that re-enable leaves the right hand frozen, so it is re-asserted here while the hands stay empty.
        // This must be debounced: a weapon draw briefly looks empty (EmptyHandsController.Drop disables the IK, the firearm controller
        // takes over a few frames later), and re-enabling the IK in that window wedges the draw ("busy hands" on equip).
        public static float reassertEmptyDelay = 0.25f; // s the hands must stay cleanly empty before the IK is touched
        private float lastNotCleanEmptyTime;

        private void LateUpdate()
        {
            if (VRGlobals.emptyHands && VRGlobals.player && VRGlobals.player.HandsIsEmpty)
            {
                VRGlobals.camRoot.transform.position = new Vector3(VRGlobals.emptyHands.position.x, VRGlobals.player.Transform.position.y + 1.5f, VRGlobals.emptyHands.position.z);
            }
            // On a mounted gun, PinRig puts the rig on the body and leaves the view to the headset alone. It overrides the pin above.
            Interactions.MountedGun.PinRig();
            // Puts empty hands that FIKA left after a refused hands change back on the VR hands (with no gun in them, nothing else would)
            if (VRGlobals.inGame && !VRGlobals.menuOpen)
                WeaponPatches.RepairUnspawnedEmptyHands();

            bool cleanEmpty = VRGlobals.player != null && VRGlobals.player.HandsIsEmpty
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
                    HandTargets.Set(rik, RightHand.transform, HandOwner.EmptyHands);
                    rik.enabled = true;
                }
            }

            // Streams the local VR hand and arm poses to other players over FIKA. The FIKA-referencing Tick() is only JIT-compiled
            // when called, so this stays load-safe without FIKA.
            if (TarkovVR.ModSupport.InstalledMods.FIKAInstalled)
                TarkovVR.ModSupport.FIKA.VRArmSync.Tick();
        }

        public Quaternion initialCombinedRotation;
        private Quaternion rightHandRotationOffset;

        //---------------------------------------------------------------------------------------------------------
        // Free hands on Index controllers use a fixed pose in the controller's frame (the gun-hold sliders only apply to the gun).
        // The rotation is the gun hand's at neutral sliders (its pointing finger is within 3 deg of Valve's openxr_poke axis); the left
        // hand uses its mirror image. The wrist position puts the pointing fingertip on openxr_poke (valve_controller_knu_1_0_*.json;
        // no other controller's model has that point). IK aims the wrist at RightHand / LeftHand, so everything measured from those
        // transforms keeps its relation to the rendered hand.
        public static Vector3 freeRightHandWrist = new Vector3(0.0147f, 0.064f, -0.1715f);
        public static Vector3 freeRightHandRotation = new Vector3(50f, 170f, 70f);
        public static Vector3 freeLeftHandWrist = new Vector3(-0.0246f, 0.0468f, -0.188f);
        public static Vector3 freeLeftHandRotation = new Vector3(-50f, 10f, 70f);

        private bool rightHandPlacedFree;

        private static bool AlignsFreeHands => VRGlobals.vrControllerType == "index" && !VRSettings.GetLeftHandedMode();

        private static bool RightHandIsFree => Interactions.ChestHang.Detached
            || Interactions.EatingInteractionController.ManualActive
            || (VRGlobals.player != null && VRGlobals.player.HandsIsEmpty);

        private void PlaceRightHandForGun(VRPose fromAction)
        {
            Quaternion offset = Quaternion.Euler(VRSettings.GetPrimaryHandVertOffset() + mainHandRotOffset.x, mainHandRotOffset.y, mainHandRotOffset.z + VRSettings.GetPrimaryHandHorOffset());
            // With the gun in the left hand (Handedness.GunInLeftHand), the left controller holds the gun the way the right one does,
            // mirrored, while the gun itself isn't mirrored. Right-handed, controller -> gun = offset * gunInHand. Its mirror image is that
            // reflected through the controller's left-right plane (its x) and through the gun's own (Weapon_root x):
            // hand rotation = F * (offset * gunInHand) * F * gunInHand^-1. Reflecting the offset alone would only work if the gun's x
            // lined up with the hand frame's x, and it doesn't.
            bool real = Handedness.GunInLeftHand;
            // A grenade in the left hand is mirrored whole (GrenadeHold.MirroredInLeftHand: its positioner is scaled -1 on x). Only the
            // right hold's own offset is mirrored here, through the controller's x; the scale mirrors the rest.
            if (real && GrenadeHold.MirroredInLeftHand)
                RightHand.transform.localRotation = fromAction.localRotation * new Quaternion(offset.x, -offset.y, -offset.z, offset.w);
            else
                RightHand.transform.localRotation = fromAction.localRotation * (real ? MirroredGunHold(offset) : offset);
            RightHand.transform.localPosition = (fromAction.localPosition + (real ? MirrorX(mainHandPosOffset) : mainHandPosOffset)) - fromAction.localRotation * Vector3.forward * controllerLength;
        }

        // The gun's rotation in the rig hand's frame (smoothed rig hand -> RightHandPositioner -> weaponHolder -> Weapon_root). It is
        // read once, when the gun goes into the left hand, with the holder in its restored pose (ChestHang, after Reattach). Don't read
        // it live: the weapon collision nudges the holder every frame, and the nudge would feed back into the hold.
        private static Quaternion gunInHandCached = Quaternion.identity;
        private static Transform gunInHandFor;

        // Caches the Weapon_root under weaponHolder (RealLeftHandedPatches) for the gun, or for a grenade the left hand took off the
        // chest (GrenadeSlot)
        public static void CacheGunInHand()
        {
            gunInHandFor = null;
            gunInHandCached = Quaternion.identity;
            Transform weaponRoot = RealLeftHandedPatches.HeldWeaponRoot();
            if (weaponRoot == null || VRGlobals.weaponHolder == null || weaponRoot.parent != VRGlobals.weaponHolder.transform)
                return;
            gunInHandCached = VRGlobals.weaponHolder.transform.localRotation * weaponRoot.localRotation;
            gunInHandFor = weaponRoot;
        }

        // Converts a right-handed holder position in the rig hand's frame to where it goes for the left hand: the right-handed hold's
        // offset mirrored in the controller's frame (PlaceRightHandForGun mirrors the rotation). R' * p' = C * MirrorX(offset * p),
        // with R' = C * hold'. Guns don't need it (the marker anchor places them); GrenadeHold uses it for a grenade's fixed holder offset.
        public Vector3 MirroredHolderPosition(Vector3 rightHanded)
        {
            Quaternion offset = Quaternion.Euler(VRSettings.GetPrimaryHandVertOffset() + mainHandRotOffset.x, mainHandRotOffset.y, mainHandRotOffset.z + VRSettings.GetPrimaryHandHorOffset());
            return Quaternion.Inverse(MirroredGunHold(offset)) * MirrorX(offset * rightHanded);
        }

        private static Quaternion MirroredGunHold(Quaternion offset)
        {
            if (gunInHandFor == null || gunInHandFor != RealLeftHandedPatches.HeldWeaponRoot())
                CacheGunInHand();
            Quaternion gunInHand = gunInHandCached;
            Matrix4x4 flip = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            Matrix4x4 hold = flip * Matrix4x4.Rotate(offset * gunInHand) * flip * Matrix4x4.Rotate(Quaternion.Inverse(gunInHand));
            return Quaternion.LookRotation(hold.GetColumn(2), hold.GetColumn(1));
        }

        // The right hand's own natural pose at the right controller, placed the right-handed way. It is the right arm's target while
        // the gun is in the left hand and the right hand is off it (RealLeftHandedPatches; the rig's hands follow the swapped
        // controllers then). It is a rig child placed from the right controller's pose callback (same sample and moment as the rig
        // hands) and solved by the arm's own IK update, like every other free hand; a world point handed to EFT's arm pass alone
        // jitters. Index controllers use the free-hand wrist pose, others the gun hand's offsets.
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
            if (VRGlobals.vrControllerType == "index")
            {
                rightControllerHand.localPosition = controllerPosition + controllerRotation * freeRightHandWrist;
                rightControllerHand.localRotation = controllerRotation * Quaternion.Euler(freeRightHandRotation);
                return;
            }
            rightControllerHand.localPosition = controllerPosition + rightHandedPrimaryHandPosOffset - controllerRotation * Vector3.forward * controllerLength;
            rightControllerHand.localRotation = controllerRotation * Quaternion.Euler(VRSettings.GetPrimaryHandVertOffset() + rightHandedPrimaryHandRotOffset.x,
                rightHandedPrimaryHandRotOffset.y, rightHandedPrimaryHandRotOffset.z + VRSettings.GetPrimaryHandHorOffset());
        }

        // The same for the left hand: its own free pose at the left controller (PlaceOffHand's, unswapped). It is the left arm's target
        // while the gun belongs in the left hand but only the right hand holds it, by the foregrip (RealLeftHandedPatches.SupportHeld).
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
            if (VRGlobals.vrControllerType == "index")
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

        // With the gun in the left hand, the right controller drives the rig's off hand, so the off hand's offsets are mirrored onto it
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

        // Picks how much the aim is smoothed this frame (smoothingFactor, 50 = none). First the hold-breath slerp (only when AimSteady
        // is off; AimSteady steadies the target before any of these), else the gun's weight (weightFeel: the rotation goes through
        // WeaponWeightFeel and the factor still drives the position), else the Smooth Weapon Aim setting.
        private void ChooseAimSmoothing(out bool weightFeel)
        {
            weightFeel = false;
            if (InputHandlers.BreathingHandler.isHoldingBreath && VRSettings.SmoothScopeAim() && !AimSteady.enabled)
            {
                if (VRGlobals.scopeSensitivity * (VRSettings.GetScopeSensitivity() * 10) > 0)
                    smoothingFactor = VRGlobals.scopeSensitivity * (VRSettings.GetScopeSensitivity() * 10);
                else
                    smoothingFactor = 0.1f * (VRSettings.GetScopeSensitivity() * 10);
            }
            else if (VRGlobals.firearmController && VRSettings.GetWeaponWeightOn())
            {
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
        }

        private void UpdateRightHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
        {
            if (!RightHand)
                return;

            if (VRGlobals.emptyHands != null)
                initialCombinedRotation = VRGlobals.emptyHands.rotation;
            // Last frame's free pose overwrote RightHand. The code below reads RightHand as the gun's frame (two-hand aim, weight
            // filter), so it is placed for the gun first.
            if (rightHandPlacedFree)
                PlaceRightHandForGun(fromAction);
            VRBoolean primaryGripState = Handedness.GunHandGrip;
            bool blockJoystick = (VRSettings.GetLeftHandedMode()) ? VRGlobals.blockLeftJoystick : VRGlobals.blockRightJoystick;
            // Joystick blocked but the grip isn't down: keep it blocked until the stick is back near centre, so the player doesn't
            // move forward right after selecting something from the radial menu
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

                // Looking through a scope that zooms: the turning stick's up / down zooms it with no button held (the grip is always
                // down while holding the gun, so it can't be the modifier). Jump, crouch and prone pause meanwhile (ScopeZoomOwnsStick);
                // turning still works, because it already ignores a stick pushed up or down.
                if (VRGlobals.firearmController.IsAiming && VRGlobals.vrOpticController && VRGlobals.vrOpticController.CanZoomNow)
                {
                    VRGlobals.vrOpticController.handleJoystickZoomDial();
                    scopeZoomStickFrame = Time.frameCount;
                }
                else
                {
                    if (VRSettings.GetLeftHandedMode())
                        VRGlobals.blockLeftJoystick = false;
                    else
                        VRGlobals.blockRightJoystick = false;
                }


                // Virtual gunstock: with the butt seated in the shoulder, the aim pivots about the shoulder instead of the rear hand
                // (ShoulderStock blends the base in and out). Transforms are passed, not positions, because ShoulderStock works in
                // tracking space.
                Vector3 aimBase = ShoulderStock.UpdateAimBase(RightHand.transform, LeftHand.transform, Time.deltaTime);
                Quaternion combinedRotation = Quaternion.LookRotation((LeftHand.transform.position - aimBase).normalized, RightHand.transform.up);

                if (!isEnteringTwoHandedMode)
                {
                    framesAfterSwitching = 0;
                    // Disable other left hand IK tracking
                    HandTargets.Set(VRGlobals.ikManager.leftArmIk, null, HandOwner.SupportGrip);
                    VRGlobals.ikManager.leftArmIk.enabled = false;
                    // Set the left hand IK back to the original gun position
                    VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                    isEnteringTwoHandedMode = true;
                    initialRightHandRotation = RightHand.transform.localRotation;
                    RightHand.transform.rotation = combinedRotation;
                    Quaternion newLocalRotation = RightHand.transform.localRotation;
                    // The one-handed local rotation vs the one pointing at the left hand. Applied to every local rotation while two-handing,
                    // so the gun starts with the exact rotation it had one-handed.
                    rightHandRotationOffset = Quaternion.Inverse(newLocalRotation) * initialRightHandRotation;
                }
                // The rotation offset has to go on before the smoothing slerp: RightHand carries it here, and is overwritten further down
                RightHand.transform.rotation = combinedRotation;
                RightHand.transform.localRotation *= rightHandRotationOffset;

                ChooseAimSmoothing(out bool weightFeel);
                Quaternion aimTarget = AimSteady.Filter(RightHand.transform.rotation, rawRightHand.transform.parent, Time.deltaTime);

                if (weightFeel)
                {
                    rawRightHand.transform.rotation = WeaponWeightFeel.Main.Step(rawRightHand.transform.rotation,
                        aimTarget, BarrelDir(), VRGlobals.firearmController.ErgonomicWeight,
                        true, Time.deltaTime, rawRightHand.transform.parent);
                }
                else if (smoothingFactor != 50)
                {
                    rawRightHand.transform.rotation = Quaternion.Slerp(rawRightHand.transform.rotation, aimTarget, smoothingFactor * Time.deltaTime);
                }
                else if (aimTarget != RightHand.transform.rotation)
                {
                    rawRightHand.transform.rotation = aimTarget;
                }
                else
                {
                    rawRightHand.transform.rotation = combinedRotation;
                    rawRightHand.transform.localRotation *= rightHandRotationOffset;
                }

                PlaceRightHandForGun(fromAction);

                // With the physics weight the gun only turns about the grip, so the grip position stays on the hand
                if (smoothingFactor != 50 && !(weightFeel && WeaponWeightFeel.physicsWeight))
                {
                    Vector3 position = rawRightHand.transform.position - (inertiaVelocity / inertiaDamping);
                    rawRightHand.transform.position = Vector3.Slerp(position, RightHand.transform.position, smoothingFactor * Time.deltaTime);
                }
                else
                {
                    rawRightHand.transform.position = RightHand.transform.position;
                }

                // The first 2 frames after swapping to or from two handed mode are slow to update and the gun shows at an odd angle,
                // so set the weaponholder pos and rotation directly
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
                    HandTargets.Set(VRGlobals.ikManager.leftArmIk, null, HandOwner.SupportGrip);
                    VRGlobals.ikManager.leftArmIk.enabled = false;
                    isEnteringTwoHandedMode = true;
                }
                if (isWeapPistol && !isSupporting && isEnteringTwoHandedMode)
                {
                    HandTargets.Set(VRGlobals.ikManager.leftArmIk, LeftHand.transform, HandOwner.SupportGrip);
                    VRGlobals.ikManager.leftArmIk.enabled = true;
                    isEnteringTwoHandedMode = false;
                }
                if (isEnteringTwoHandedMode && !isWeapPistol)
                {
                    framesAfterSwitching = 0;
                    isEnteringTwoHandedMode = false;
                    VRGlobals.player._markers[0] = LeftHand.transform;
                    HandTargets.Set(VRGlobals.ikManager.leftArmIk, LeftHand.transform, HandOwner.SupportGrip);
                    VRGlobals.ikManager.leftArmIk.enabled = true;
                    rawRightHand.transform.rotation = RightHand.transform.rotation;
                }

                PlaceRightHandForGun(fromAction);

                ChooseAimSmoothing(out bool weightFeel);
                Quaternion aimTarget = AimSteady.Filter(RightHand.transform.rotation, rawRightHand.transform.parent, Time.deltaTime);

                if (weightFeel)
                {
                    rawRightHand.transform.rotation = WeaponWeightFeel.Main.Step(rawRightHand.transform.rotation,
                        aimTarget, BarrelDir(), VRGlobals.firearmController.ErgonomicWeight,
                        isSupporting, Time.deltaTime, rawRightHand.transform.parent);
                }
                else if (smoothingFactor != 50)
                {
                    rawRightHand.transform.rotation = Quaternion.Slerp(rawRightHand.transform.rotation, aimTarget, smoothingFactor * Time.deltaTime);
                }
                else
                {
                    rawRightHand.transform.rotation = aimTarget;
                }

                if (smoothingFactor != 50 && !(weightFeel && WeaponWeightFeel.physicsWeight))
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

            // Done last, so rawRightHand above keeps following the gun's frame and taking the gun back doesn't swing it
            rightHandPlacedFree = AlignsFreeHands && RightHandIsFree;
            if (rightHandPlacedFree)
            {
                RightHand.transform.localPosition = fromAction.localPosition + fromAction.localRotation * freeRightHandWrist;
                RightHand.transform.localRotation = fromAction.localRotation * Quaternion.Euler(freeRightHandRotation);
            }
        }

        private void UpdateLeftHand(VRPose fromAction, SteamVR_Input_Sources fromSource)
        {
            leftHandYRotation = fromAction.localRotation.eulerAngles.y;
            VRBoolean secondaryGripState = Handedness.OffHandGrip;

            bool blockJoystick = (VRSettings.GetLeftHandedMode()) ? VRGlobals.blockRightJoystick : VRGlobals.blockLeftJoystick;

            // Joystick blocked but the grip isn't down: keep it blocked until the stick is back near centre, so the player doesn't
            // move forward right after selecting something from the radial menu
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

            // Holding a loot item: skip all the gun and scope IK logic and place the hand freely. Must come before the scopeTransform
            // check. The same applies while holding or reaching for a magazine, so the grab never turns into a foregrip snap, and with
            // the gun slung, where the scope grab and the foregrip snap below would lock the hand onto the hanging gun.
            if (HandsInteractionController.HoldingLoot
                || VRReloadController.OwnsOffHand || VRReloadController.Shotgun.ShellInOffHand || TarkovVR.Source.Player.Interactions.ChestHang.Hanging)
            {
                handLock = false;
                supportZoneBlip.Rearm();
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

            // Tarkov's reload / malfunction animation takes the left hand here, unless VRGunController keeps it on the controller
            if (!VRGunController.KeepLeftHandOnController() && VRGlobals.player && VRGlobals.player.BodyAnimatorCommon.GetFloat(LEFT_HAND_ANIMATOR_HASH) == 1.0)
            {
                if (!leftHandInAnimation)
                {
                    HandTargets.Set(VRGlobals.ikManager.leftArmIk, null, HandOwner.TarkovAnimation);
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
                    HandTargets.Set(VRGlobals.ikManager.leftArmIk, LeftHand.transform, HandOwner.SupportGrip);
                    VRGlobals.ikManager.leftArmIk.enabled = true;
                    VRGlobals.player._markers[0] = LeftHand.transform;
                }

                leftHandInAnimation = false;
            }
            // A grenade is never two-handed (the other hand on it pulls the ring or takes it over, see GrenadeHold). Otherwise its
            // left-hand marker would take the hand that just handed it over, grip still closed, as a foregrip, and freeze its rotation.
            if (WeaponPatches.grenadeEquipped)
            {
                if (isSupporting || isEnteringTwoHandedMode)
                    ForceUnlockHand();
                PlaceOffHand(fromAction);
            }
            else if (leftHandGunIK)
            {
                Vector3 supportGripPoint = SupportGripPoint();
                bool withinDistance = Vector3.Distance(LeftHand.transform.position, supportGripPoint) < 0.1f;

                // While held, a hand outside the foregrip's reach still counts as inside it, so letting go out there doesn't blip on the way back
                if ((withinDistance || !isSupporting) && supportZoneBlip.Enter(withinDistance))
                    VRInput.Haptic.Execute(0, 0.25f, 1, 0.4f, Handedness.OffHandSource);

                // The grip holds the foregrip. Closing it at the foregrip takes it (handLock, then isSupporting the frame after), and
                // opening it lets go at once (with Toggle Gun Grip, a press each way: see ForegripGrip). While held, the hand stays on
                // however far the controller strays. Skipped for a hand holding a door or a key (PhysicalDoors): pulling a door in
                // toward the chest passes the foregrip.
                if ((withinDistance || handLock) && !PhysicalDoors.HoldsDoor(false))
                {
                    if (!isSupporting && handLock)
                    {
                        if (isWeapPistol)
                            VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
                        initialRightHandRotation = rawRightHand.transform.rotation;
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
                    handLock = ForegripGrip(secondaryGripState, handLock);
                }

                if (isSupporting && handLock)
                {
                    // Position only: the support hand's rotation stays as it was at the grab (the arm follows the foregrip marker)
                    LeftHand.transform.localPosition = fromAction.localPosition;
                }
                else
                {
                    if (isSupporting)
                    {
                        isSupporting = false;
                        if (isWeapPistol)
                            VRGlobals.player._markers[0] = LeftHand.transform;
                    }
                    PlaceOffHand(fromAction);
                }
            }
            else
            {
                PlaceOffHand(fromAction);
            }
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
                            showingHealthUi = true;
                        }

                    }
                    else if (showingHealthUi)
                    {
                        UIPatches.stancePanel.AnimatedHide();
                        UIPatches.healthPanel.AnimatedHide();
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

        // Whether the foregrip is held: the grip button is down, or with "Toggle Gun Grip" a press takes it and the next press lets go.
        // A press is counted once per frame, because the pose callback can run twice.
        private bool ForegripGrip(VRBoolean grip, bool holding)
        {
            if (!VRSettings.GetGunGripToggle())
                return grip.state;
            if (!grip.stateDown || foregripPressFrame == Time.frameCount)
                return holding;
            foregripPressFrame = Time.frameCount;
            return !holding;
        }
        private int foregripPressFrame = -1;

        public void ForceUnlockHand()
        {
            bool wasTwoHanding = isSupporting || isEnteringTwoHandedMode;
            handLock = false;
            isSupporting = false;

            if (isWeapPistol)
                VRGlobals.player._markers[0] = LeftHand.transform;

            // Two-handing can end here rather than by the left hand letting go of the gun (UpdateRightHand's release): the sling taking
            // the gun from the left hand alone, a hand swap, a menu. Undo what two-handing did to the left arm the same way that release
            // does: EFT's left marker back on the left hand, and the arm's own IK back on its controller. Otherwise EFT keeps solving the
            // free left arm toward the gun's foregrip marker with the arm's own IK off. Only done while the arm is still in its
            // two-handed state (no target): a reload latch on it belongs to VRReloadController.
            var leftArmIk = VRGlobals.ikManager != null ? VRGlobals.ikManager.leftArmIk : null;
            if (wasTwoHanding && leftArmIk != null && leftArmIk.solver != null && leftArmIk.solver.target == null && LeftHand != null)
            {
                if (VRGlobals.player != null)
                    VRGlobals.player._markers[0] = LeftHand.transform;
                HandTargets.Set(leftArmIk, LeftHand.transform, HandOwner.SupportGrip);
                leftArmIk.enabled = true;
                isEnteringTwoHandedMode = false;
            }
        }

        // Starts two-handing without a grip press at the support point: a pistol's magazine seated with the grip still held
        // (VRReloadController.UpdateSeatedHold), with the hand already on the support pose. Same entry as UpdateLeftHand's, with
        // handLock set so opening the grip lets go. The reload latch still on the arm hands it to EFT's IK itself once it sees
        // isSupporting (VRReloadController.UpdateHandLatch).
        public void TakeSupportGrip()
        {
            if (isSupporting || LeftHand == null || VRGlobals.player == null || rawRightHand == null)
                return;
            if (isWeapPistol)
                VRGlobals.player._markers[0] = WeaponPatches.previousLeftHandMarker;
            initialRightHandRotation = rawRightHand.transform.rotation;
            isSupporting = true;
            handLock = true;
            supportZoneBlip.MarkInside();
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

    }


}


