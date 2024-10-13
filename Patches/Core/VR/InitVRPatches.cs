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
using TarkovVR.Source.Weapons;
using UnityEngine;
using Valve.VR;
using static TarkovVR.Source.Controls.InputHandlers;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class InitVRPatches
    {
        private static Transform originalLeftHandMarker;
        private static Transform originalRightHandMarker;
        public static Transform rigCollider;
        public static Transform leftWrist;
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterControllerSpawner), "Spawn")]
        private static void AddVR(CharacterControllerSpawner __instance)
        {
            EFT.Player player = __instance.transform.root.GetComponent<EFT.Player>();
            if (__instance.transform.root.name != "PlayerSuperior(Clone)")
                return;

            if (player is not HideoutPlayer)
            {
                if (!VRGlobals.vrPlayer)
                {
                    VRGlobals.camHolder = new GameObject("camHolder");
                    VRGlobals.vrOffsetter = new GameObject("vrOffsetter");
                    VRGlobals.camRoot = new GameObject("camRoot");
                    if (UIPatches.gameUi)
                        UIPatches.PositionGameUi(UIPatches.gameUi);

                    VRGlobals.camHolder.transform.parent = VRGlobals.vrOffsetter.transform;
                    //Camera.main.transform.parent = vrOffsetter.transform;
                    //Camera.main.gameObject.AddComponent<SteamVR_TrackedObject>();
                    VRGlobals.vrOffsetter.transform.parent = VRGlobals.camRoot.transform;
                    VRGlobals.camHolder.AddComponent<SteamVR_TrackedObject>();
                    VRGlobals.menuVRManager = VRGlobals.camHolder.AddComponent<MenuVRManager>();
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
                    headGearCollider.transform.localPosition = Vector3.zero;
                    headGearCollider.transform.localRotation = Quaternion.identity;
                    headGearCollider.layer = 3;
                    collider = headGearCollider.AddComponent<SphereCollider>();
                    collider.radius = 0.075f;
                    collider.isTrigger = true;

                    VRGlobals.camHolder.layer = 7;
                    VRGlobals.menuVRManager.enabled = false;
                    VRGlobals.menuOpen = false;
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
                VRGlobals.backHolster.localPosition = new Vector3(0.2f, -0.1f, -0.2f);
                VRGlobals.backCollider.isTrigger = true;
                VRGlobals.backHolster.gameObject.layer = 3;

                VRGlobals.backpackCollider = new GameObject("backpackCollider").transform;
                VRGlobals.backpackCollider.parent = VRGlobals.camHolder.transform;
                VRGlobals.backpackCollider.gameObject.AddComponent<BoxCollider>().isTrigger = true;
                VRGlobals.backpackCollider.localScale = new Vector3(0.2f, 0.2f, 0.2f);
                VRGlobals.backpackCollider.localPosition = new Vector3(-0.2f, -0.1f, -0.2f);
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


        public static GrenadeFingerPositioner rightPointerFinger;
        public static Transform leftPalm;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SolverManager), "OnDisable")]
        private static void SetupIK(LimbIK __instance)
        {
            //if (__instance.transform.root.name != "PlayerSuperior(Clone)")
            //    return;
            if (__instance.transform.root.name != "PlayerSuperior(Clone)")
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
                if (declaringType.Contains("EFT.BotSpawner") || declaringType.Contains("GClass732") && methodName.Contains("ActivateBot"))
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

            if (__instance.transform.parent.parent.GetComponent<IKManager>() == null)
                VRGlobals.ikManager = __instance.transform.parent.parent.gameObject.AddComponent<IKManager>();

            if (__instance.name == "Base HumanLCollarbone") {
                leftWrist = __instance.transform.FindChildRecursive("Base HumanLForearm3");
                if (leftWrist != null && leftWrist.GetComponent<TwistRelax>())
                    leftWrist.GetComponent<TwistRelax>().weight = 3;

                IInputHandler baseHandler;
                VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.LeftStanceToggle, out baseHandler);
                if (baseHandler != null)
                {
                    ResetHeightHandler resetHeightHandler = baseHandler as ResetHeightHandler;
                    resetHeightHandler.SetLeftArmTransform(__instance.transform.FindChildRecursive("Base HumanLForearm1"));
                }
                leftPalm = __instance.transform.FindChildRecursive("Base HumanLPalm");
            }
            if (__instance.name == "Base HumanRCollarbone")
            {
                IInputHandler baseHandler;
                VRInputManager.inputHandlers.TryGetValue(EFT.InputSystem.ECommand.LeftStanceToggle, out baseHandler);
                if (baseHandler != null)
                {
                    ResetHeightHandler resetHeightHandler = baseHandler as ResetHeightHandler;
                    resetHeightHandler.SetRightArmTransform(__instance.transform.FindChildRecursive("Base HumanRForearm1"));
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
                VRGlobals.leftArmBendGoal.localPosition = new Vector3(-1, -0.5f, -0.8f);
            }
            if (VRGlobals.rightArmBendGoal == null)
            {
                VRGlobals.rightArmBendGoal = new GameObject("rightArmBendGoal").transform;
                VRGlobals.rightArmBendGoal.parent = __instance.transform.root.transform;
                VRGlobals.rightArmBendGoal.localEulerAngles = Vector3.zero;
                VRGlobals.rightArmBendGoal.localPosition = new Vector3(1, -0.5f, 0.8f);
            }


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
                Plugin.MyLog.LogWarning("\n\nSetting camera \n\n");
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
                //mainCam.cullingMask = -1;
                mainCam.nearClipPlane = VRGlobals.NEAR_CLIP_PLANE;
                mainCam.farClipPlane = 1000f;
                mainCam.gameObject.AddComponent<SteamVR_TrackedObject>();
                mainCam.useOcclusionCulling = false;
                if (VRGlobals.vrPlayer) { 
                    if (VRGlobals.vrPlayer.radialMenu)
                            VRGlobals.vrPlayer.radialMenu.active = false;
                    if (VRGlobals.vrPlayer is RaidVRPlayerManager) {
                        VRGlobals.menuVRManager.OnDisable();
                    }
                }
                //mainCam.gameObject.GetComponent<PostProcessLayer>().enabled = false;
                //cameraManager.initPos = VRCam.transform.localPosition;
            }

        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerSpring), "Start")]
        private static void SetRigAndSidearmHolsters(PlayerSpring __instance)
        {
            if (__instance.transform.root.name != "PlayerSuperior(Clone)" || __instance.name != "Base HumanRibcage" || rigCollider != null)
                return;

            rigCollider = new GameObject("rigCollider").transform;
            BoxCollider collider = rigCollider.gameObject.AddComponent<BoxCollider>();
            rigCollider.parent = __instance.transform.parent;
            rigCollider.localEulerAngles = Vector3.zero;
            rigCollider.localPosition = Vector3.zero;
            rigCollider.gameObject.layer = 3;
            collider.isTrigger = true;
            collider.size = new Vector3(0.1f, 0.1f, 0.1f);

            if (VRGlobals.sidearmHolster)
                VRGlobals.sidearmHolster.gameObject.layer = 3;
        }
        // local pos -0.1 -0.15 -0.1
        // size 0.001 0.005 0.005


    }
}
