using EFT;
using HarmonyLib;
using RootMotion.FinalIK;
using System.Diagnostics;
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
    internal class CamPatches
    {
        //private static Transform leftWrist;
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterControllerSpawner), "Spawn")]
        private static void AddVR(CharacterControllerSpawner __instance)
        {
            if (__instance.transform.root.name != "PlayerSuperior(Clone)")
                return;

            if (__instance.transform.root.GetComponent<HideoutPlayer>() != null)
            {
                if (!VRGlobals.vrPlayer)
                {
                    VRGlobals.camHolder.AddComponent<SteamVR_TrackedObject>();
                    VRGlobals.vrPlayer = VRGlobals.camHolder.AddComponent<HideoutVRPlayerManager>();
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.parent = VRPlayerManager.RightHand.transform;
                    VRGlobals.vrOpticController = VRGlobals.camHolder.AddComponent<VROpticController>();
                    VRGlobals.handsInteractionController = VRGlobals.camHolder.AddComponent<HandsInteractionController>();
                }
            }
            else
            {
                VRGlobals.camHolder = new GameObject("camHolder");
                VRGlobals.vrOffsetter = new GameObject("vrOffsetter");
                VRGlobals.camRoot = new GameObject("camRoot");
                VRGlobals.camHolder.transform.parent = VRGlobals.vrOffsetter.transform;
                //Camera.main.transform.parent = vrOffsetter.transform;
                //Camera.main.gameObject.AddComponent<SteamVR_TrackedObject>();
                VRGlobals.vrOffsetter.transform.parent = VRGlobals.camRoot.transform;
                if (!VRGlobals.vrPlayer)
                {
                    VRGlobals.camHolder.AddComponent<SteamVR_TrackedObject>();
                    VRGlobals.vrPlayer = VRGlobals.camHolder.AddComponent<RaidVRPlayerManager>();
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.parent = VRPlayerManager.RightHand.transform;
                    VRGlobals.vrOpticController = VRGlobals.camHolder.AddComponent<VROpticController>();
                    VRGlobals.handsInteractionController = VRGlobals.camHolder.AddComponent<HandsInteractionController>();
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
            }


            if (__instance.name == VRGlobals.LEFT_ARM_OBJECT_NAME)
            {


                __instance.enabled = true;
                VRGlobals.ikManager.leftArmIk = __instance;
                __instance.solver.target = VRPlayerManager.LeftHand.transform;
                // Set the weight to 2.5 so when rotating the hand, the wrist rotates as well, showing the watch time
                Transform leftWrist = __instance.transform.GetChild(0).GetChild(0).GetChild(0).GetChild(0);
                leftWrist.GetComponent<TwistRelax>().weight = 2.5f;
            }
            else if (__instance.name == VRGlobals.RIGHT_ARM_OBJECT_NAME)
            {

                __instance.enabled = true;
                VRGlobals.ikManager.rightArmIk = __instance;
                if (VRGlobals.ikManager.rightHandIK == null)
                    VRGlobals.ikManager.rightHandIK = __instance.transform.GetChild(0).GetChild(0).GetChild(0).GetChild(0).GetChild(0).gameObject;
                __instance.solver.target = VRPlayerManager.RightHand.transform;



            }

        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Don't know why the hell I chose this method for setting the main cam but it works so whatever
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BloodOnScreen), "Start")]
        private static void SetMainCamParent(BloodOnScreen __instance)
        {
            Camera mainCam = __instance.GetComponent<Camera>();
            if (mainCam.name == "FPS Camera")
            {
                Plugin.MyLog.LogWarning("\n\nSetting camera \n\n");
                mainCam.transform.parent = VRGlobals.vrOffsetter.transform;
                mainCam.cullingMask = -1;
                mainCam.nearClipPlane = 0.001f;
                mainCam.gameObject.AddComponent<SteamVR_TrackedObject>();
                //mainCam.gameObject.GetComponent<PostProcessLayer>().enabled = false;
                //cameraManager.initPos = VRCam.transform.localPosition;
            }

        }
    }
}
