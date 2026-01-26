using EFT;
using EFT.Animations;
using HarmonyLib;
using RootMotion.FinalIK;
using System.Diagnostics;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Player.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;
using Valve.VR;
using static TarkovVR.Source.Controls.InputHandlers;
using UnityStandardAssets.ImageEffects;
using UnityEngine.Rendering.PostProcessing;
using System.Linq;
using System.Runtime.Remoting.Contexts;
using UnityEngine.XR;
using UnityEngine.Rendering;
using System.Collections;
using TarkovVR.Patches.Visuals;
using Valve.VR.InteractionSystem;
using System.IO;
using System;
using EFT.UI.Matchmaker;
using EFT.UI;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Player.Body;
using static DistantShadow;
using TarkovVR.Source.Graphics;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class InitVRPatches
    {
        // just experimental stuff used for flipping camera when using forward rendering with MSAA
        public class FlipCameraRender : MonoBehaviour
        {
            private Camera cam;

            void Awake()
            {
                cam = GetComponent<Camera>();
            }

            void OnPreRender()
            {
                // Flip the projection matrix vertically
                Matrix4x4 mat = cam.projectionMatrix;
                mat *= Matrix4x4.Scale(new Vector3(1, -1, 1));
                cam.projectionMatrix = mat;

                // Invert culling so front faces render correctly
                GL.invertCulling = true;
            }

            void OnPostRender()
            {
                // Reset everything
                cam.ResetProjectionMatrix();
                GL.invertCulling = false;
            }
        }

        private static Transform originalLeftHandMarker;
        private static Transform originalRightHandMarker;
        public static Transform quickSlot;
        public static Transform rigCollider;
        public static Transform leftWrist;
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterControllerSpawner), "Spawn")]
        private static void AddVR(CharacterControllerSpawner __instance)
        {
            EFT.Player player = __instance.transform.root.GetComponent<EFT.Player>();
            if (__instance.transform.root.name != "PlayerSuperior(Clone)" && __instance.transform.root.name != "Main_PlayerSuperior(Clone)")
                return;

            if (player is not HideoutPlayer)
            {
                if (!VRGlobals.vrPlayer)
                {
                    //VRGlobals.camHolder = new GameObject("camHolder");
                    //VRGlobals.vrOffsetter = new GameObject("vrOffsetter");
                    //VRGlobals.camRoot = new GameObject("camRoot");
                    if (UIPatches.gameUi)
                        UIPatches.PositionGameUi(UIPatches.gameUi);

                    VRGlobals.camHolder.transform.parent = VRGlobals.vrOffsetter.transform;
                    //Camera.main.transform.parent = vrOffsetter.transform;
                    //Camera.main.gameObject.AddComponent<SteamVR_TrackedObject>();
                    VRGlobals.vrOffsetter.transform.parent = VRGlobals.camRoot.transform;
                    VRGlobals.camHolder.AddComponent<SteamVR_TrackedObject>();
                    //VRGlobals.menuVRManager = VRGlobals.camHolder.AddComponent<MenuVRManager>();
                    VRGlobals.vrPlayer = VRGlobals.camHolder.AddComponent<RaidVRPlayerManager>();
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.parent = VRGlobals.vrPlayer.RightHand.transform;
                    VRGlobals.vrOpticController = VRGlobals.camHolder.AddComponent<VROpticController>();
                    VRGlobals.handsInteractionController = VRGlobals.camHolder.AddComponent<HandsInteractionController>();
                    SphereCollider collider = VRGlobals.camHolder.AddComponent<SphereCollider>();
                    collider.radius = 0.2f;
                    collider.isTrigger = true;

                    GameObject headGearCollider = new GameObject("headGearCollider");
                    headGearCollider.transform.parent = VRGlobals.camHolder.transform;
                    headGearCollider.transform.localPosition = new Vector3(0.05f, 0f, 0.05f); // Right 0.05, Forward 0.05
                    headGearCollider.transform.localRotation = Quaternion.identity;
                    headGearCollider.layer = 3;
                    collider = headGearCollider.AddComponent<SphereCollider>();
                    collider.radius = 0.075f;
                    collider.isTrigger = true;
                    VRGlobals.camHolder.layer = 7;                  
                    if (!ModSupport.InstalledMods.FIKAInstalled)
                    {
                        VRGlobals.menuVRManager.enabled = false;
                        VRGlobals.menuOpen = false;
                    }
                    else {
                        VRGlobals.vrPlayer.enabled = false;
                        //VRGlobals.camRoot.transform.position = new Vector3(0, -999.8f, -0.5f);
                        //VRGlobals.vrOffsetter.transform.localPosition = Camera.main.transform.localPosition * -1;
                        VRGlobals.vrOffsetter.transform.localPosition = VRGlobals.VRCam.transform.localPosition * -1;
                        VRGlobals.menuOpen = true;
                        VRGlobals.menuVRManager.OnEnable();
                    }
                    
                    if (UIPatches.quickSlotUi == null)
                    {
                        GameObject quickSlotHolder = new GameObject("quickSlotUi");
                        quickSlotHolder.layer = 5;
                        quickSlotHolder.transform.parent = VRGlobals.vrPlayer.LeftHand.transform;
                        UIPatches.quickSlotUi = quickSlotHolder.AddComponent<CircularSegmentUI>();
                        UIPatches.quickSlotUi.Init();
                        //circularSegmentUI.CreateQuickSlotUi(mainImagesList.ToArray());
                    }
                    UIPatches.quickSlotUi.gameObject.active = false;
                    //VRGlobals.vrPlayer.radialMenu.active = false;
                    // The camera on interchange doesn't stay turned off like other maps so try and disable it again here.
                    Camera.main.useOcclusionCulling = false;
                }
            }


            if (VRGlobals.backHolster == null)
            {
                VRGlobals.backHolster = new GameObject("backHolsterCollider").transform;
                VRGlobals.backHolster.parent = VRGlobals.camHolder.transform;
                VRGlobals.backCollider = VRGlobals.backHolster.gameObject.AddComponent<BoxCollider>();
                VRGlobals.backHolster.localScale = new Vector3(0.2f, 0.2f, 0.2f);
                VRGlobals.backHolster.localPosition = (VRSettings.GetLeftHandedMode()) ? new Vector3(-0.2f, -0.1f, -0.2f) : new Vector3(0.2f, -0.1f, -0.2f);
                VRGlobals.backCollider.isTrigger = true;
                VRGlobals.backHolster.gameObject.layer = 3;

                VRGlobals.backpackCollider = new GameObject("backpackCollider").transform;
                VRGlobals.backpackCollider.parent = VRGlobals.camHolder.transform;
                VRGlobals.backpackCollider.gameObject.AddComponent<BoxCollider>().isTrigger = true;
                VRGlobals.backpackCollider.localScale = new Vector3(0.2f, 0.2f, 0.2f);
                VRGlobals.backpackCollider.localPosition = (VRSettings.GetLeftHandedMode()) ? new Vector3(0.2f, -0.1f, -0.2f) : new Vector3(-0.2f, -0.1f, -0.2f);
                VRGlobals.backpackCollider.gameObject.layer = 3;

                VRGlobals.sidearmHolster = new GameObject("sidearmHolsterCollider").transform;
                BoxCollider sidearmCollider = VRGlobals.sidearmHolster.gameObject.AddComponent<BoxCollider>();
                sidearmCollider.isTrigger = true;
                sidearmCollider.size = new Vector3(0.01f, 0.01f, 0.01f);
                VRGlobals.sidearmHolster.transform.parent = player.PlayerBones.HolsterPistol.parent;
                VRGlobals.sidearmHolster.transform.localPosition = new Vector3(0, 0.1f, 0.1f);
                VRGlobals.sidearmHolster.transform.localRotation = Quaternion.identity;
                VRGlobals.sidearmHolster.gameObject.layer = 3;
            }

            //if (VRGlobals.leftArmBendGoal == null) {
            //    VRGlobals.leftArmBendGoal = new GameObject("leftArmBendGoal").transform;
            //    VRGlobals.leftArmBendGoal.parent = VRGlobals.vrOffsetter.transform;
            //    VRGlobals.leftArmBendGoal.localEulerAngles = Vector3.zero;
            //    VRGlobals.leftArmBendGoal.localPosition = new Vector3(-1,-0.5f,-0.8f);
            //}
            //if (VRGlobals.rightArmBendGoal == null)
            //{
            //    VRGlobals.rightArmBendGoal = new GameObject("rightArmBendGoal").transform;
            //    VRGlobals.rightArmBendGoal.parent = VRGlobals.vrOffsetter.transform;
            //    VRGlobals.rightArmBendGoal.localEulerAngles = Vector3.zero;
            //    VRGlobals.rightArmBendGoal.localPosition = new Vector3(1, -0.5f, 0.8f);
            //}


            VRGlobals.inGame = true;
        }      
        
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Don't know why I chose this method for setting the main cam but it works so whatever

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BloodOnScreen), "Start")]
        private static void SetMainCamParent(BloodOnScreen __instance)
        {          
            Camera mainCam = __instance.GetComponent<Camera>();
            if (mainCam.name == "FPS Camera")
            {               
                VRGlobals.VRCam = mainCam;
                GameObject uiCamHolder = new GameObject("uiCam");
                uiCamHolder.transform.parent = __instance.transform;
                uiCamHolder.transform.localRotation = Quaternion.identity;
                uiCamHolder.transform.localPosition = Vector3.zero;
                Camera uiCam = uiCamHolder.AddComponent<Camera>();
                uiCam.nearClipPlane = VRGlobals.NEAR_CLIP_PLANE;
                uiCam.depth = 1;
                uiCam.cullingMask = 32;
                uiCam.clearFlags = CameraClearFlags.Depth;
                mainCam.transform.parent = VRGlobals.vrOffsetter.transform;
                mainCam.nearClipPlane = VRGlobals.NEAR_CLIP_PLANE;
                mainCam.farClipPlane = 5000f;
                mainCam.stereoTargetEye = StereoTargetEyeMask.Both;
                mainCam.gameObject.AddComponent<SteamVR_TrackedObject>();
                
                mainCam.rect = new Rect(0.0f, 0.0f, VRGlobals.upscalingMultiplier, VRGlobals.upscalingMultiplier);
                if (mainCam.GetComponent<VRJitterComponent>() == null)
                {
                    mainCam.gameObject.AddComponent<VRJitterComponent>();
                }
                mainCam.useOcclusionCulling = false;
                if (XRSettings.enabled)
                {
                    XRSettings.useOcclusionMesh = false;
                }
                if (VRGlobals.vrPlayer)
                {
                    if (VRGlobals.vrPlayer.radialMenu)
                        VRGlobals.vrPlayer.radialMenu.active = false;
                    if (VRGlobals.vrPlayer is RaidVRPlayerManager)
                    {
                        VRGlobals.menuVRManager.OnDisable();
                    }
                }
                mainCam.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
                //mainCam.gameObject.GetComponent<PostProcessLayer>().enabled = false;
                //cameraManager.initPos = VRCam.transform.localPosition;
            }
        }

        // Forward rendering experiment
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BloodOnScreen), "Start")]
        private static void SetMainCamParent(BloodOnScreen __instance)
        {
            Camera mainCam = __instance.GetComponent<Camera>();
            if (mainCam.name != "FPS Camera")
                return;

            VRGlobals.VRCam = mainCam;

            // === Setup main camera for deferred rendering (no MSAA) ===
            mainCam.renderingPath = RenderingPath.DeferredShading;
            mainCam.allowMSAA = false;
            mainCam.useOcclusionCulling = false;
            mainCam.layerCullSpherical = true;
            mainCam.nearClipPlane = VRGlobals.NEAR_CLIP_PLANE;
            mainCam.farClipPlane = 5000f;
            mainCam.stereoTargetEye = StereoTargetEyeMask.None; // Deferred cam doesn't render to VR
            mainCam.transform.parent = VRGlobals.vrOffsetter.transform;
            mainCam.gameObject.AddComponent<SteamVR_TrackedObject>(); // VR tracking

            // Cull only UI and effects on main camera
            int uiLayer = 5;
            int rainLayer = 24;
            mainCam.cullingMask = (1 << uiLayer) | (1 << rainLayer);

            // === Create MSAA world camera (forward rendering) ===
            GameObject msaaCamObj = new GameObject("VR_MSAA_WorldCam");
            msaaCamObj.transform.SetParent(mainCam.transform, false); // Parent to mainCam = auto-tracking
            msaaCamObj.transform.localPosition = Vector3.zero;
            msaaCamObj.transform.localRotation = Quaternion.identity;

            Camera msaaCam = msaaCamObj.AddComponent<Camera>();
            msaaCam.CopyFrom(mainCam);
            msaaCam.renderingPath = RenderingPath.Forward;
            msaaCam.allowMSAA = true;
            msaaCam.clearFlags = CameraClearFlags.Skybox;
            msaaCam.ResetProjectionMatrix();
            Matrix4x4 mat = msaaCam.projectionMatrix;
            mat *= Matrix4x4.Scale(new Vector3(1, -1, 1)); // Flip Y axis
            msaaCam.projectionMatrix = mat;
            msaaCam.depth = mainCam.depth - 1; // Render before main cam
            msaaCam.useOcclusionCulling = false;
            msaaCam.layerCullSpherical = true;
            msaaCam.stereoTargetEye = StereoTargetEyeMask.Both; // This renders to VR
            msaaCamObj.AddComponent<FlipCameraRender>();


            // Set layer cull distances
            float[] distances = new float[32];
            for (int i = 0; i < distances.Length; i++)
                distances[i] = 1000f;
            msaaCam.layerCullDistances = distances;

            // Render world geometry only
            msaaCam.cullingMask =
                (1 << 0) |  // Default
                (1 << 4) |  // Water
                (1 << 8) |  // Player
                (1 << 11) | // Terrain
                (1 << 12) | // HighPolyCollider
                (1 << 14) | // DisablerCullingObject
                (1 << 15) | // Loot
                (1 << 16) | // HitCollider
                (1 << 17) | // PlayerRenderers
                (1 << 18) | // LowPolyCollider
                (1 << 22) | // Interactive
                (1 << 23) | // Deadbody
                (1 << 26) | // Foliage
                (1 << 28) | // Sky
                (1 << 29) | // LevelBorder
                (1 << 31);  // Grass

            QualitySettings.antiAliasing = 4;

            // === UI Camera ===
            GameObject uiCamHolder = new GameObject("uiCam");
            uiCamHolder.transform.SetParent(__instance.transform, false);
            uiCamHolder.transform.localPosition = Vector3.zero;
            uiCamHolder.transform.localRotation = Quaternion.identity;

            Camera uiCam = uiCamHolder.AddComponent<Camera>();
            uiCam.nearClipPlane = VRGlobals.NEAR_CLIP_PLANE;
            uiCam.depth = mainCam.depth + 1; // Render after main cam
            uiCam.cullingMask = 1 << uiLayer; // UI only
            uiCam.clearFlags = CameraClearFlags.Depth;
            uiCam.stereoTargetEye = StereoTargetEyeMask.Both;

            // Disable VR menu stuff
            if (VRGlobals.vrPlayer)
            {
                if (VRGlobals.vrPlayer.radialMenu)
                    VRGlobals.vrPlayer.radialMenu.active = false;
                if (VRGlobals.vrPlayer is RaidVRPlayerManager)
                    VRGlobals.menuVRManager.OnDisable();
            }
        }*/

        public class ArmStretcher : MonoBehaviour
        {
            public LimbIK leftArmIk;
            public LimbIK rightArmIk;
            public Transform leftHandTarget;
            public Transform rightHandTarget;

            // --- NEW: We must store individual bone lengths ---
            private float leftUpperArmNaturalLength;
            private float leftForearmNaturalLength;
            private float rightUpperArmNaturalLength;
            private float rightForearmNaturalLength;

            // This now stores the TOTAL length
            private float leftArmTotalNaturalLength;
            private float rightArmTotalNaturalLength;

            private Vector3 leftUpperArmOriginalScale;
            private Vector3 leftForearmOriginalScale;
            private Vector3 rightUpperArmOriginalScale;
            private Vector3 rightForearmOriginalScale;

            public float maxStretchMultiplier = 1.2f; // Allow 20% stretch
            private bool initialized = false;

            void Start()
            {
                InitializeArms();
            }

            void InitializeArms()
            {
                if (initialized) return;

                if (leftArmIk != null)
                {
                    Transform bone1 = leftArmIk.solver.bone1.transform;
                    Transform bone2 = leftArmIk.solver.bone2.transform;
                    Transform bone3 = leftArmIk.solver.bone3.transform;

                    leftUpperArmNaturalLength = Vector3.Distance(bone1.position, bone2.position);
                    leftForearmNaturalLength = Vector3.Distance(bone2.position, bone3.position);
                    leftArmTotalNaturalLength = leftUpperArmNaturalLength + leftForearmNaturalLength;

                    leftUpperArmOriginalScale = bone1.localScale;
                    leftForearmOriginalScale = bone2.localScale;

                }

                if (rightArmIk != null)
                {
                    Transform bone1 = rightArmIk.solver.bone1.transform;
                    Transform bone2 = rightArmIk.solver.bone2.transform;
                    Transform bone3 = rightArmIk.solver.bone3.transform;

                    rightUpperArmNaturalLength = Vector3.Distance(bone1.position, bone2.position);
                    rightForearmNaturalLength = Vector3.Distance(bone2.position, bone3.position);
                    rightArmTotalNaturalLength = rightUpperArmNaturalLength + rightForearmNaturalLength;

                    rightUpperArmOriginalScale = bone1.localScale;
                    rightForearmOriginalScale = bone2.localScale;

                }

                initialized = true;
            }

            void LateUpdate()
            {
                if (!initialized)
                    InitializeArms();

                if (leftArmIk != null && leftHandTarget != null)
                {
                    StretchArm(leftArmIk, leftHandTarget.position,
                               leftArmTotalNaturalLength, leftUpperArmNaturalLength, leftForearmNaturalLength,
                               leftUpperArmOriginalScale, leftForearmOriginalScale, true);
                }

                if (rightArmIk != null && rightHandTarget != null)
                {
                    StretchArm(rightArmIk, rightHandTarget.position,
                               rightArmTotalNaturalLength, rightUpperArmNaturalLength, rightForearmNaturalLength,
                               rightUpperArmOriginalScale, rightForearmOriginalScale, false);
                }
            }
            
            void StretchArm(LimbIK armIk, Vector3 targetPosition,
                            float totalNaturalLength, float upperArmNaturalLength, float forearmNaturalLength,
                            Vector3 upperArmOriginalScale, Vector3 forearmOriginalScale, bool isLeft)
            {
                Transform shoulder = armIk.solver.bone1.transform;
                Transform upperArm = armIk.solver.bone1.transform;
                Transform forearm = armIk.solver.bone2.transform;

                float targetDistance = Vector3.Distance(shoulder.position, targetPosition);

                float maxAllowedDistance = totalNaturalLength * maxStretchMultiplier;

                float clampedTargetDistance = Mathf.Min(targetDistance, maxAllowedDistance);

                if (clampedTargetDistance > totalNaturalLength)
                {

                    float requiredUpperArmStretch = (clampedTargetDistance - forearmNaturalLength) / upperArmNaturalLength;

                    upperArm.localScale = new Vector3(
                        upperArmOriginalScale.x * requiredUpperArmStretch,
                        upperArmOriginalScale.y,
                        upperArmOriginalScale.z
                    );

                    // ALWAYS reset the forearm scale (since you don't want it stretched)
                    forearm.localScale = forearmOriginalScale;
                }
                else
                {
                    upperArm.localScale = upperArmOriginalScale;
                    forearm.localScale = forearmOriginalScale;
                }

                armIk.solver.IKPosition = targetPosition;
                armIk.solver.Update();
            }
            
            /*
            void StretchArm(LimbIK armIk, Vector3 targetPosition,
                float totalNaturalLength, float upperArmNaturalLength, float forearmNaturalLength,
                Vector3 upperArmOriginalScale, Vector3 forearmOriginalScale, bool isLeft)
            {
                Transform shoulder = armIk.solver.bone1.transform;
                Transform upperArm = armIk.solver.bone1.transform;
                Transform forearm = armIk.solver.bone2.transform;

                float targetDistance = Vector3.Distance(shoulder.position, targetPosition);
                float maxAllowedDistance = totalNaturalLength * maxStretchMultiplier;
                float clampedTargetDistance = Mathf.Min(targetDistance, maxAllowedDistance);

                // Store IK state
                bool ikWasEnabled = armIk.enabled;

                // Temporarily disable IK to prevent feedback loop during scaling
                armIk.enabled = false;

                if (clampedTargetDistance > totalNaturalLength)
                {
                    float requiredUpperArmStretch = (clampedTargetDistance - forearmNaturalLength) / upperArmNaturalLength;

                    upperArm.localScale = new Vector3(
                        upperArmOriginalScale.x * requiredUpperArmStretch,
                        upperArmOriginalScale.y,
                        upperArmOriginalScale.z
                    );

                    // ALWAYS reset the forearm scale (since you don't want it stretched)
                    forearm.localScale = forearmOriginalScale;
                }
                else
                {
                    upperArm.localScale = upperArmOriginalScale;
                    forearm.localScale = forearmOriginalScale;
                }

                // Re-enable IK and set target
                armIk.enabled = ikWasEnabled;
                armIk.solver.IKPosition = targetPosition;

                // Force IK to update with new bone scales
                if (armIk.enabled)
                {
                    armIk.solver.Update();
                }
            }
            */
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public static Transform rightWrist;
        public static Transform leftPalm;
        public static GrenadeFingerPositioner rightPointerFinger;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SolverManager), "OnDisable")]
        private static void SetupIK(LimbIK __instance)
        {
            //if (__instance.transform.root.name != "PlayerSuperior(Clone)")
            //    return;
            if (__instance.transform.root.name != "PlayerSuperior(Clone)" && __instance.transform.root.name != "Main_PlayerSuperior(Clone)")
                return;


            StackTrace stackTrace = new StackTrace();
            bool isBotPlayer = false;
            if (__instance.transform.root.GetComponent<EFT.Player>() && !__instance.transform.root.GetComponent<EFT.Player>().IsYourPlayer)
                return;

            foreach (var frame in stackTrace.GetFrames())
            {
                var method = frame.GetMethod();
                var declaringType = method.DeclaringType.FullName;
                var methodName = method.Name;

                // Check for bot-specific methods
                if (declaringType.Contains("EFT.BotSpawner") || declaringType.Contains("GClass794") && methodName.Contains("ActivateBot"))
                {
                    isBotPlayer = true;
                    break;
                }
            }

            // This is a bot player, so do not execute the rest of the code
            if (isBotPlayer)
                return;

            //    //This is for Base HumanSpine3 to stop it doing something, cant remember

            // Disable the weapon holsters so they dont wack the player in the face
            if (__instance.transform.parent.parent.FindChild("weapon_holster"))
                __instance.transform.parent.parent.FindChild("weapon_holster").gameObject.active = false;
            if (__instance.transform.parent.parent.FindChild("weapon_holster1"))
                __instance.transform.parent.parent.FindChild("weapon_holster1").gameObject.active = false;

            if (__instance.transform.parent.parent.GetComponent<IKManager>() == null) { 
                VRGlobals.ikManager = __instance.transform.parent.parent.gameObject.AddComponent<IKManager>();
                if (ModSupport.InstalledMods.FIKAInstalled) {
                    VRGlobals.ikManager.enabled = false;
                    VRGlobals.camRoot.transform.position = new Vector3(0, -999.8f, -0.5f);
                }
            }

            if (__instance.name == "Base HumanLCollarbone") {
                VRGlobals.ikManager.leftArmIk = __instance.transform.GetComponent<LimbIK>();

                leftWrist = __instance.transform.FindChildRecursive("Base HumanLForearm3");
                if (leftWrist != null && leftWrist.GetComponent<TwistRelax>())
                    leftWrist.GetComponent<TwistRelax>().weight = 3f;
                Transform lForearm2 = __instance.transform.FindChildRecursive("Base HumanLForearm2");
                if (lForearm2 != null && lForearm2.GetComponent<TwistRelax>())
                    lForearm2.GetComponent<TwistRelax>().weight = 1.8f;
                Transform lForearm1 = __instance.transform.FindChildRecursive("Base HumanLForearm1");
                if (lForearm1 != null && lForearm1.GetComponent<TwistRelax>())
                    lForearm1.GetComponent<TwistRelax>().weight = 1f;
                Transform lUpperArm = __instance.transform.FindChildRecursive("Base HumanLUpperarm");
                if (lUpperArm != null && lUpperArm.GetComponent<TwistRelax>())
                    lUpperArm.GetComponent<TwistRelax>().weight = 1f;

                IInputHandler baseHandler;
                VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.LeftStanceToggle, out baseHandler);
                if (baseHandler != null)
                {
                    ResetHeightHandler resetHeightHandler = baseHandler as ResetHeightHandler;
                    //resetHeightHandler.SetLeftArmTransform(__instance.transform.FindChildRecursive("Base HumanLForearm1"));
                }
                leftPalm = __instance.transform.FindChildRecursive("Base HumanLPalm");
            }
            if (__instance.name == "Base HumanRCollarbone")
            {
                VRGlobals.ikManager.rightArmIk = __instance.transform.GetComponent<LimbIK>();

                rightWrist = __instance.transform.FindChildRecursive("Base HumanRForearm3");
                if (rightWrist != null && rightWrist.GetComponent<TwistRelax>())
                    rightWrist.GetComponent<TwistRelax>().weight = 3f;
                Transform rForearm2 = __instance.transform.FindChildRecursive("Base HumanRForearm2");
                if (rForearm2 != null && rForearm2.GetComponent<TwistRelax>())
                    rForearm2.GetComponent<TwistRelax>().weight = 1.8f;
                Transform rForearm1 = __instance.transform.FindChildRecursive("Base HumanRForearm1");
                if (rForearm1 != null && rForearm1.GetComponent<TwistRelax>())
                    rForearm1.GetComponent<TwistRelax>().weight = 1f;
                Transform rUpperArm = __instance.transform.FindChildRecursive("Base HumanRUpperarm");
                if (rUpperArm != null && rUpperArm.GetComponent<TwistRelax>())
                    rUpperArm.GetComponent<TwistRelax>().weight = 1f;

                if (VRSettings.GetLeftHandedMode())
                    VRGlobals.ikManager.rightArmIk.transform.parent.localScale = new Vector3(-1, 1, 1);
                IInputHandler baseHandler;
                VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.LeftStanceToggle, out baseHandler);
                if (baseHandler != null)
                {
                    ResetHeightHandler resetHeightHandler = baseHandler as ResetHeightHandler;
                    //resetHeightHandler.SetRightArmTransform(__instance.transform.FindChildRecursive("Base HumanRForearm1"));
                }
                Transform rightFingerTransform = __instance.transform.GetChild(0).GetChild(0).GetChild(0).GetChild(0).GetChild(0).GetChild(1);
                rightPointerFinger = rightFingerTransform.gameObject.AddComponent<GrenadeFingerPositioner>();
                rightPointerFinger.enabled = false;
                if (VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.laser != null) { 
                    VRGlobals.handsInteractionController.grenadeLaser.transform.parent = rightFingerTransform;
                    VRGlobals.handsInteractionController.grenadeLaser.transform.localEulerAngles = new Vector3(351f, 273.6908f, 0);
                    VRGlobals.handsInteractionController.grenadeLaser.transform.localPosition = new Vector3(-0.3037f, 0.0415f, 0.0112f);
                }
                    
            }
            // parent is HumanLForearm3

            // Timer panel localpos: 0.047 0.08 0.025
            // local rot = 88.5784 83.1275 174.7802
            // child(0).localeuler = 0 342.1273 0

            // leftwristui localpos = -0.1 0.04 0.035
            // localrot = 304.3265 181 180

            //GameObject.Destroy(__instance);

            if (VRGlobals.leftArmBendGoal == null)
            {
                VRGlobals.leftArmBendGoal = new GameObject("leftArmBendGoal").transform;
                VRGlobals.leftArmBendGoal.parent = __instance.transform.root.transform;
                VRGlobals.leftArmBendGoal.localEulerAngles = Vector3.zero;
                VRGlobals.leftArmBendGoal.localPosition = VRSettings.GetLeftHandedMode() ? new Vector3(1, -0.5f, -0.8f) : new Vector3(-1, -0.5f, -0.8f);
                //VRGlobals.leftArmBendGoal.localPosition = new Vector3(-0.5f, -0.3f, -0.4f);
            }
            if (VRGlobals.rightArmBendGoal == null)
            {
                VRGlobals.rightArmBendGoal = new GameObject("rightArmBendGoal").transform;
                VRGlobals.rightArmBendGoal.parent = __instance.transform.root.transform;
                VRGlobals.rightArmBendGoal.localEulerAngles = Vector3.zero;
                VRGlobals.rightArmBendGoal.localPosition = VRSettings.GetLeftHandedMode() ? new Vector3(-1.5f, -0.6f, -1.2f) : new Vector3(1.5f, -0.6f, -1.2f);
                //VRGlobals.rightArmBendGoal.localPosition = new Vector3(2, -0.9f, -0.8f);
            }
            if (VRGlobals.rightArmBendGoal != null && VRGlobals.leftArmBendGoal != null)
            {
                if (leftWrist != null && rightWrist != null)
                {
                    
                    if (__instance.transform.root.GetComponent<DynamicElbowPositioner>() == null)
                    {
                        DynamicElbowPositioner elbowPositioner = __instance.transform.root.gameObject.AddComponent<DynamicElbowPositioner>();
                        elbowPositioner.leftWristTransform = leftWrist;
                        elbowPositioner.rightWristTransform = rightWrist;
                        elbowPositioner.leftBendGoal = VRGlobals.leftArmBendGoal;
                        elbowPositioner.rightBendGoal = VRGlobals.rightArmBendGoal;
                    }
                    /*
                    if (__instance.transform.root.GetComponent<ArmStretcher>() == null)
                    {
                        if (VRGlobals.vrPlayer != null &&
                                VRGlobals.vrPlayer.LeftHand != null &&
                                VRGlobals.vrPlayer.RightHand != null)
                        {
                            // Only create if IK managers exist
                            if (VRGlobals.ikManager != null &&
                            VRGlobals.ikManager.leftArmIk != null &&
                            VRGlobals.ikManager.rightArmIk != null)
                            {

                                ArmStretcher armStretcher = __instance.transform.root.gameObject.AddComponent<ArmStretcher>();
                                armStretcher.leftArmIk = VRGlobals.ikManager.leftArmIk;
                                armStretcher.rightArmIk = VRGlobals.ikManager.rightArmIk;

                                // Only set hand targets if VRPlayer is ready

                                armStretcher.leftHandTarget = VRGlobals.vrPlayer.LeftHand.transform;
                                armStretcher.rightHandTarget = VRGlobals.vrPlayer.rawRightHand.transform;
                                Plugin.MyLog.LogError("ArmStretcher created with hand targets!");

                                armStretcher.maxStretchMultiplier = 1.2f;
                            }
                        }
                        else
                        {
                            Plugin.MyLog.LogError("Cannot create ArmStretcher - IK managers not ready");
                        }
                    }
                    */
                }
            }

        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerSpring), "Start")]
        private static void SetRigAndSidearmHolsters(PlayerSpring __instance)
        {
            if ((__instance.transform.root.name != "PlayerSuperior(Clone)" && __instance.transform.root.name != "Main_PlayerSuperior(Clone)") || __instance.name != "Base HumanRibcage") //|| quickSlot != null)
                return;
            
            rigCollider = new GameObject("rigCollider").transform;
            BoxCollider collider = rigCollider.gameObject.AddComponent<BoxCollider>();

            rigCollider.parent = __instance.transform.parent;
            rigCollider.localEulerAngles = Vector3.zero;
            rigCollider.localPosition = new Vector3(0.2f, -0.05f, 0f);
            rigCollider.gameObject.layer = 3;

            collider.isTrigger = true;
            collider.size = new Vector3(0.04f, 0.1f, 0.2f);

            //debug for collider
            /*
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(rigCollider);
            cube.transform.localPosition = Vector3.zero;
            cube.transform.localRotation = Quaternion.identity;
            cube.transform.localScale = collider.size;
            cube.GetComponent<Collider>().enabled = false;
            */
            if (VRGlobals.sidearmHolster)
                VRGlobals.sidearmHolster.gameObject.layer = 3;
        }
        // local pos -0.1 -0.15 -0.1
        // size 0.001 0.005 0.005


    }
}
