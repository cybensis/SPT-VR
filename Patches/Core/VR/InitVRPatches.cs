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
using UnityEngine.XR;
using EFT.UI;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Player.Body;
using TarkovVR.Patches.Upscalers;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class InitVRPatches
    {
        // The UI-layer camera draws only unlit canvases, usually nothing at all. Unity's default camera is deferred + HDR, which costs a
        // G-buffer pass, a lighting pass and an HDR copy of the eye image per eye every frame (1.3 ms GPU + 0.84 ms render thread in the
        // hideout). Forward without HDR/MSAA draws straight into the eye image.
        internal static void MakeUiCameraLight(Camera uiCam)
        {
            if (uiCam == null) return;
            uiCam.renderingPath = RenderingPath.Forward;
            uiCam.allowHDR = false;
            uiCam.allowMSAA = false;
            uiCam.useOcclusionCulling = false;   // the UI is drawn over the world, so baked occluders must not hide it (and the query costs time)
        }

        // Experimental: flips the camera for forward rendering with MSAA
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
                cam.ResetProjectionMatrix();
                GL.invertCulling = false;
            }
        }

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

            if (player is HideoutPlayer)
                TarkovVR.Source.Graphics.HideoutShadows.hideoutPlayer = player;

            if (player is not HideoutPlayer)
            {
                if (!VRGlobals.vrPlayer)
                {
                    if (UIPatches.gameUi)
                        UIPatches.PositionGameUi(UIPatches.gameUi);

                    VRGlobals.camHolder.transform.parent = VRGlobals.vrOffsetter.transform;
                    VRGlobals.vrOffsetter.transform.parent = VRGlobals.camRoot.transform;
                    VRGlobals.camHolder.AddComponent<SteamVR_TrackedObject>();
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
                    }
                    else {
                        VRGlobals.vrPlayer.enabled = false;
                        VRGlobals.vrOffsetter.transform.localPosition = VRGlobals.VRCam.transform.localPosition * -1;
                        VRGlobals.menuOpen = true;
                    }
                    
                    if (UIPatches.quickSlotUi == null)
                    {
                        GameObject quickSlotHolder = new GameObject("quickSlotUi");
                        quickSlotHolder.layer = 5;
                        quickSlotHolder.transform.parent = VRGlobals.vrPlayer.LeftHand.transform;
                        UIPatches.quickSlotUi = quickSlotHolder.AddComponent<CircularSegmentUI>();
                        UIPatches.quickSlotUi.Init();
                    }
                    UIPatches.quickSlotUi.gameObject.active = false;
                    // On Interchange the camera's occlusion culling doesn't stay off like on other maps, so turn it off again here.
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

        }

        // Disables the laser right after the raid-start timer finishes, so the laser isn't lost partway through the loading screen
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "ShowRaidStartInfo")]
        private static void DisableLaser(PreloaderUI __instance)
        {
            VRGlobals.menuVRManager.enabled = false;
            VRGlobals.vrPlayer.enabled = true;
            VRGlobals.ikManager.enabled = true;

            if (VRGlobals.menuOpen)
            {
                if (VRGlobals.player?.PlayerBody?.MeshTransform != null)
                    foreach (var renderer in VRGlobals.player.PlayerBody.MeshTransform.GetComponentsInChildren<Renderer>(true))
                        renderer.enabled = true;
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (WeaponPatches.currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = true;
                }
            }
            VRGlobals.inGame = true;
            VRGlobals.menuOpen = false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // BloodOnScreen.Start is used only as a hook to reach the main camera ("FPS Camera")

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
                MakeUiCameraLight(uiCam);
                uiCamHolder.AddComponent<TarkovVR.Source.Graphics.UiCameraGate>();   // off while no UI is in view
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

                TarkovVR.Patches.Visuals.Rendering.ApplyCullDistances(mainCam);
                if (XRSettings.enabled)
                {
                    XRSettings.useOcclusionMesh = true;
                }
                if (VRGlobals.vrPlayer)
                {
                    if (VRGlobals.vrPlayer.radialMenu)
                        VRGlobals.vrPlayer.radialMenu.active = false;
                }
                mainCam.depthTextureMode |= DepthTextureMode.MotionVectors | DepthTextureMode.Depth;
            }
        }

        public class ArmStretcher : MonoBehaviour
        {
            public LimbIK leftArmIk;
            public LimbIK rightArmIk;
            public Transform leftHandTarget;
            public Transform rightHandTarget;

            // Individual bone lengths
            private float leftUpperArmNaturalLength;
            private float leftForearmNaturalLength;
            private float rightUpperArmNaturalLength;
            private float rightForearmNaturalLength;

            // Upper arm + forearm
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

                    // Only the upper arm stretches: the forearm keeps its scale
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

        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public static Transform rightWrist;
        public static Transform leftPalm;
        public static GrenadeFingerPositioner rightPointerFinger;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SolverManager), "OnDisable")]
        private static void SetupIK(LimbIK __instance)
        {
            if (__instance.transform.root.name != "PlayerSuperior(Clone)" && __instance.transform.root.name != "Main_PlayerSuperior(Clone)")
                return;


            StackTrace stackTrace = new StackTrace();
            bool isBotPlayer = false;
            if (__instance.transform.root.GetComponent<EFT.Player>() && !__instance.transform.root.GetComponent<EFT.Player>().IsYourPlayer)
                return;

            // The check above passes when the root has no EFT.Player yet (a bot's Player is attached later in its activation), and the
            // stack check below names a stale obfuscated class. A bot that got through would rewire ikManager/leftArmIk/rightArmIk onto
            // its skeleton, so two identity guards follow. Destroyed references compare as null and skip them, so a new raid still wires up.
            if (VRGlobals.player != null && __instance.transform.root != VRGlobals.player.gameObject.transform.root)
            {
                Plugin.MyLog.LogWarning($"[SetupIK] Ignoring non-local rig '{__instance.transform.root.name}' ({__instance.name}).");
                return;
            }
            if (VRGlobals.ikManager != null && __instance.transform.root != VRGlobals.ikManager.transform.root)
            {
                Plugin.MyLog.LogWarning($"[SetupIK] Rejected rig rewire from '{__instance.transform.root.name}' ({__instance.name}) - ikManager already wired to the local rig.");
                return;
            }

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

            if (isBotPlayer)
                return;

            // Disable the weapon holsters so they don't hit the player in the face
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
                // New rig: clear the wrist-twist binds so the collarbone blocks below capture them again from this rig's spawn rest pose.
                // Only once per rig: this method runs again on every weapon equip (rightArmIk gets disabled) and must not reset them
                // from a live pose.
                TarkovVR.Patches.Core.Player.IKPatches.ClearWristBind();
            }

            if (__instance.name == "Base HumanLCollarbone") {
                VRGlobals.ikManager.leftArmIk = __instance.transform.GetComponent<LimbIK>();
                TarkovVR.Patches.Core.Player.IKPatches.CaptureWristBindLeftIfNeeded();

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
                }
                leftPalm = __instance.transform.FindChildRecursive("Base HumanLPalm");
            }
            if (__instance.name == "Base HumanRCollarbone")
            {
                VRGlobals.ikManager.rightArmIk = __instance.transform.GetComponent<LimbIK>();
                TarkovVR.Patches.Core.Player.IKPatches.CaptureWristBindRightIfNeeded();

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

            if (VRGlobals.leftArmBendGoal == null)
            {
                VRGlobals.leftArmBendGoal = new GameObject("leftArmBendGoal").transform;
                VRGlobals.leftArmBendGoal.parent = __instance.transform.root.transform;
                VRGlobals.leftArmBendGoal.localEulerAngles = Vector3.zero;
                VRGlobals.leftArmBendGoal.localPosition = VRSettings.GetLeftHandedMode() ? new Vector3(1, -2f, -0.8f) : new Vector3(-1, -2f, -0.8f);
            }
            if (VRGlobals.rightArmBendGoal == null)
            {
                VRGlobals.rightArmBendGoal = new GameObject("rightArmBendGoal").transform;
                VRGlobals.rightArmBendGoal.parent = __instance.transform.root.transform;
                VRGlobals.rightArmBendGoal.localEulerAngles = Vector3.zero;
                VRGlobals.rightArmBendGoal.localPosition = VRSettings.GetLeftHandedMode() ? new Vector3(-2.5f, -2f, -0.8f) : new Vector3(2.5f, -2f, -0.8f);
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
                }
            }

        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerSpring), "Start")]
        private static void SetRigAndSidearmHolsters(PlayerSpring __instance)
        {
            if ((__instance.transform.root.name != "PlayerSuperior(Clone)" && __instance.transform.root.name != "Main_PlayerSuperior(Clone)") || __instance.name != "Base HumanRibcage")
                return;
            
            rigCollider = new GameObject("rigCollider").transform;
            BoxCollider collider = rigCollider.gameObject.AddComponent<BoxCollider>();

            rigCollider.parent = __instance.transform.parent;
            rigCollider.localEulerAngles = Vector3.zero;
            rigCollider.localPosition = new Vector3(0.2f, -0.05f, 0f);
            rigCollider.gameObject.layer = 3;

            collider.isTrigger = true;
            collider.size = new Vector3(0.04f, 0.1f, 0.2f);

            if (VRGlobals.sidearmHolster)
                VRGlobals.sidearmHolster.gameObject.layer = 3;
        }


    }
}
