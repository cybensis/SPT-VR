using EFT.UI.Screens;
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Source.Player.VRManager;
using UnityEngine;
using static EFT.UI.PlayerProfilePreview;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class LoginUIPatches
    {

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WelcomeScreen<EftWelcomeScreen.GClass3617, EEftScreenType>), "Show", new Type[] { typeof(EftWelcomeScreen.GClass3617) })]
        private static void PositionLoginWelcomeScreen(WelcomeScreen<EftWelcomeScreen.GClass3617, EEftScreenType> __instance)
        {
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;

            __instance.transform.parent.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            __instance.transform.parent.localScale = new Vector3(0.002f, 0.002f, 0.002f);
            __instance.transform.parent.localPosition = new Vector3(0.0478f, -999.938f, 1.7484f);
            __instance.transform.parent.gameObject.layer = 5;
            if (VRGlobals.camRoot == null)
            {
                //Plugin.MyLog.LogWarning("\n\n CharacterControllerSpawner Spawn " + __instance.gameObject + "\n");
                VRGlobals.camRoot = new GameObject("camRoot");
                VRGlobals.camHolder = new GameObject("camHolder");
                VRGlobals.vrOffsetter = new GameObject("vrOffsetter");
                VRGlobals.camHolder.transform.parent = VRGlobals.vrOffsetter.transform;
                VRGlobals.vrOffsetter.transform.parent = VRGlobals.camRoot.transform;


                VRGlobals.menuVRManager = VRGlobals.camHolder.AddComponent<MenuVRManager>();
                VRGlobals.camRoot.transform.position = new Vector3(0, -999.8f, -0.5f);
                VRGlobals.menuVRManager.RightHand.transform.parent = Camera.main.transform.parent;
                Camera.main.transform.parent.localPosition = Camera.main.transform.localPosition * -1;

                BoxCollider loginCollider = __instance.transform.parent.gameObject.AddComponent<BoxCollider>();
                loginCollider.size = new Vector3(5120, 2880, 1);
            }
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AccountSideSelectionScreen<EftAccountSideSelectionScreen.GClass3615, EEftScreenType>), "Awake")]
        private static void PositionLoginWelcomeScreen(AccountSideSelectionScreen<EftAccountSideSelectionScreen.GClass3615, EEftScreenType> __instance)
        {
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;

            __instance.transform.parent.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            __instance.transform.parent.localScale = new Vector3(0.002f, 0.002f, 0.002f);
            __instance.transform.parent.localPosition = new Vector3(0.0478f, -999.938f, 1.7484f);
            __instance.transform.parent.gameObject.layer = 5;
            if (VRGlobals.camRoot == null)
            {
                //Plugin.MyLog.LogWarning("\n\n CharacterControllerSpawner Spawn " + __instance.gameObject + "\n");
                VRGlobals.camRoot = new GameObject("camRoot");
                VRGlobals.camHolder = new GameObject("camHolder");
                VRGlobals.vrOffsetter = new GameObject("vrOffsetter");
                VRGlobals.camHolder.transform.parent = VRGlobals.vrOffsetter.transform;
                VRGlobals.vrOffsetter.transform.parent = VRGlobals.camRoot.transform;


                VRGlobals.menuVRManager = VRGlobals.camHolder.AddComponent<MenuVRManager>();
                VRGlobals.camRoot.transform.position = new Vector3(0, -999.8f, -0.5f);
                VRGlobals.menuVRManager.RightHand.transform.parent = Camera.main.transform.parent;
                Camera.main.transform.parent.localPosition = Camera.main.transform.localPosition * -1;

                BoxCollider loginCollider = __instance.transform.parent.gameObject.AddComponent<BoxCollider>();
                loginCollider.size = new Vector3(5120, 2880, 1);
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerProfilePreview), "ChangeCameraPosition", new Type[] { typeof(ECameraViewType), typeof(float) })]
        private static void PositionLoginPlayerModelPreview(PlayerProfilePreview __instance, ECameraViewType viewType, float duration)
        {
            __instance._camera.stereoTargetEye = StereoTargetEyeMask.None;
            __instance._camera.fieldOfView = 41;

            if (__instance.name == "UsecPanel")
                __instance.PlayerModelView.transform.localPosition = new Vector3(300f, 0, 0);
        }

    }
}
