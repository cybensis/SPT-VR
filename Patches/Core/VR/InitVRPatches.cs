using EFT;
using EFT.Animations;
using HarmonyLib;
using RootMotion.FinalIK;
using System.Collections.Generic;
using System.Diagnostics;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Player.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Weapons;
using UnityEngine;
using Valve.VR;
using Valve.VR.InteractionSystem;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class InitVRPatches
    {
        private static Transform originalLeftHandMarker;
        private static Transform originalRightHandMarker;
        public static Transform rigCollider;
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
                    VRGlobals.camHolder.layer = 7;
                    VRGlobals.menuVRManager.enabled = false;
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

                VRGlobals.sidearmHolster = new GameObject("sidearmHolsterCollider").transform;
                BoxCollider sidearmCollider = VRGlobals.sidearmHolster.gameObject.AddComponent<BoxCollider>();
                sidearmCollider.isTrigger = true;
                sidearmCollider.size = new Vector3(0.01f, 0.01f, 0.01f);
                VRGlobals.sidearmHolster.transform.parent = player.PlayerBones.HolsterPistol.parent;
                VRGlobals.sidearmHolster.transform.localPosition = new Vector3(0, 0.1f, 0.1f);
                VRGlobals.sidearmHolster.transform.localRotation = Quaternion.identity;
                VRGlobals.sidearmHolster.gameObject.layer = 3;
            }



            VRGlobals.inGame = true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------



        [HarmonyPostfix]
        [HarmonyPatch(typeof(SolverManager), "OnDisable")]
        private static void AddVRHands(LimbIK __instance)
        {
            //if (__instance.transform.root.name != "PlayerSuperior(Clone)")
            //    return;
            if (__instance.transform.root.name != "PlayerSuperior(Clone)")
                return;


            StackTrace stackTrace = new StackTrace();
            bool isBotPlayer = false;
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
            {
                return;
            }

            //    //This is for Base HumanSpine3 to stop it doing something, cant remember

            if (__instance.transform.parent.parent.GetComponent<IKManager>() == null)
            {
                VRGlobals.ikManager = __instance.transform.parent.parent.gameObject.AddComponent<IKManager>();
                //VRGlobals.ikManager.enabled = false;
            }
            if (__instance.name == "Base HumanLCollarbone") {
                Transform wrist = __instance.transform.FindChildRecursive("Base HumanLForearm3");
                if (wrist != null && wrist.GetComponent<TwistRelax>())
                {
                    wrist.GetComponent<TwistRelax>().weight = 3;
                }
            }
            //GameObject.Destroy(__instance);


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
                uiCam.nearClipPlane = 0.001f;
                uiCam.depth = 1;
                uiCam.cullingMask = 32;
                uiCam.clearFlags = CameraClearFlags.Depth;
                mainCam.transform.parent = VRGlobals.vrOffsetter.transform;
                //mainCam.cullingMask = -1;
                mainCam.nearClipPlane = 0.001f;
                mainCam.farClipPlane = 1000f;
                mainCam.gameObject.AddComponent<SteamVR_TrackedObject>();
                mainCam.useOcclusionCulling = false;

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


        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransformLinks), "CacheTransforms")]
        private static void SetRigAndSidearmHolsters(TransformLinks __instance, Transform parent, IEnumerable<string> cachedBoneNames)
        {
            Plugin.MyLog.LogWarning("Cache transform: " + __instance + "   |   " + parent);
            foreach (string boneName in cachedBoneNames)
            {
                Plugin.MyLog.LogWarning("\t - name " + boneName);

            }
        }
    }
}
