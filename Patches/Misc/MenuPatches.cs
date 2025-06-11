using HarmonyLib;
using System;
using System.Collections.Generic;
using Valve.VR;
using UnityEngine;
using EFT.UI;
using CW2.Animations;
using UnityStandardAssets.ImageEffects;
using static EFT.UI.PixelPerfectSpriteScaler;
using EFT.UI.DragAndDrop;
using UnityEngine.EventSystems;
using EFT.InventoryLogic;
using EFT.UI.WeaponModding;
using EFT;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.UI;
using System.Threading.Tasks;
using TMPro;
using System.Text;
using EFT.UI.Ragfair;
using static EFT.UI.PlayerProfilePreview;
using static EFT.UI.MenuScreen;
using EFT.UI.Screens;
using TarkovVR.Patches.UI;
using static EFT.UI.WeaponModding.WeaponModdingScreen;
using EFT.UI.SessionEnd;
using EFT.UI.Settings;
using static EFT.UI.ScreenPositionAnchor;
using UnityEngine.UI;
using TarkovVR.Source.Settings;
using EFT.UI.Matchmaker;
using EFT.UI.Builds;
using EFT.UI.Insurance;
using TarkovVR.Source.Weapons;




namespace TarkovVR.Patches.Misc
{
    [HarmonyPatch]
    internal class MenuPatches
    {
        private static BoxCollider backingCollider;
        private static GameObject cubeUiMover;
        private static MenuMover menuMover;
        private static EnvironmentUI environmentUi;


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MainMenuControllerClass), "method_5")]
        private static void AddAndFixMenuVRCam(MainMenuControllerClass __instance)
        {

            UnityEngine.Cursor.lockState = CursorLockMode.Locked;

            if (__instance.environmentUI_0 && __instance.environmentUI_0.environmentUIRoot_0)
            {
                FixMainMenuCamera();
            }
            VRGlobals.commonUi = __instance.commonUI_0.transform;
            //__instance.commonUI_0.transform.GetChild(0).RectTransform().sizeDelta = new Vector2(2560, 1440);

            VRGlobals.preloaderUi = __instance.preloaderUI_0.transform;
            //__instance.preloaderUI_0.transform.GetChild(0).RectTransform().sizeDelta = new Vector2(2560, 1440);

            VRGlobals.menuUi = __instance.menuUI_0.transform;
            //__instance.menuUI_0.transform.GetChild(0).RectTransform().sizeDelta = new Vector2(2560, 1440);

            environmentUi = __instance.environmentUI_0;

            if (VRGlobals.menuVRManager) {
                VRGlobals.menuVRManager.RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
            }

            MainMenuUIPatches.PositionMainMenuUi();

        }



       
        private static Quaternion camHolderRot;

        public static void FixMainMenuCamera()
        {
            if (environmentUi)
            {
                Transform camContainer = environmentUi.environmentUIRoot_0.CameraContainer.GetChild(0);
                if (!camContainer.gameObject.GetComponent<SteamVR_TrackedObject>())
                    camContainer.gameObject.AddComponent<SteamVR_TrackedObject>();

                //camContainer.GetComponent<PostProcessLayer>().m_Camera = environmentUi._alignmentCamera;
                Camera mainMenuCam = camContainer.GetComponent<Camera>();
                Vector3 newCamHolderPos = mainMenuCam.transform.localPosition * -1;
                newCamHolderPos.y += 0.1f;
                newCamHolderPos.z = -0.6f;
                camContainer.transform.parent.localPosition = newCamHolderPos;
                camContainer.localRotation = Quaternion.identity;
                camHolderRot = Quaternion.Euler(0, mainMenuCam.transform.localEulerAngles.y * -1, 0);
                camContainer.transform.parent.localRotation = camHolderRot;
                camContainer.tag = "MainCamera";
                if (mainMenuCam)
                {
                    if (!camContainer.Find("uiCam"))
                    {
                        GameObject uiCamHolder = new GameObject("uiCam");
                        uiCamHolder.transform.parent = camContainer.transform;
                        uiCamHolder.transform.localRotation = Quaternion.identity;
                        uiCamHolder.transform.localPosition = Vector3.zero;
                        Camera uiCam = uiCamHolder.AddComponent<Camera>();
                        uiCam.depth = 12;
                        uiCam.nearClipPlane = VRGlobals.NEAR_CLIP_PLANE;
                        uiCam.cullingMask = 32;
                        uiCam.clearFlags = CameraClearFlags.Depth;
                    }
                    //mainMenuCam.cullingMask = -1;
                    mainMenuCam.RemoveAllCommandBuffers();

                }
                camContainer.GetComponent<PhysicsSimulator>().enabled = false;
                camContainer.GetComponent<CameraMotionBlur>().enabled = false;
                if (VRGlobals.camRoot == null)
                {
                    //Plugin.MyLog.LogWarning("\n\n CharacterControllerSpawner Spawn " + __instance.gameObject + "\n");
                    VRGlobals.camHolder = new GameObject("camHolder");
                    VRGlobals.vrOffsetter = new GameObject("vrOffsetter");
                    VRGlobals.camRoot = new GameObject("camRoot");
                    VRGlobals.camHolder.transform.parent = VRGlobals.vrOffsetter.transform;
                    VRGlobals.vrOffsetter.transform.parent = VRGlobals.camRoot.transform;
                    VRGlobals.vrOffsetter.transform.localRotation = camHolderRot;

                    VRGlobals.menuVRManager = VRGlobals.camHolder.AddComponent<MenuVRManager>();
                }
                VRGlobals.camRoot.transform.position = camContainer.transform.parent.position;
                MainMenuUIPatches.PositionMenuEnvironmentProps();
            }
        }


     





        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ScrollRectNoDrag), "OnEnable")]
        private static void WidenScrollbars(ScrollRectNoDrag __instance)
        {
            if (__instance.verticalScrollbar)
                __instance.verticalScrollbar.transform.localScale = new Vector3(1.5f, __instance.verticalScrollbar.transform.localScale.y, __instance.verticalScrollbar.transform.localScale.z);

        }



        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseDropDownBox), "ToggleMenu")]
        private static bool PositionHeadVoiceDropdownMenus(BaseDropDownBox __instance)
        {
            DropDownBox dropDown = (DropDownBox)__instance;
            if (dropDown) {
                if (dropDown.name == "VoiceSelectorDropDown" || dropDown.name == "FaceSelectorDropdown") {
                    dropDown.rectTransform_1 = dropDown.GetComponent<RectTransform>();
                }
            }
            return true;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TMP_InputField), "OnPointerClick")]
        private static void OpenVRKeyboard(TMP_InputField __instance)
        {
            SteamVR.instance.overlay.ShowKeyboard(
                (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
                (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine,
                (uint)EKeyboardFlags.KeyboardFlag_Modal, "Description", 256, "", 0);

            var keyboardDoneAction =
            SteamVR_Events.SystemAction(EVREventType.VREvent_KeyboardDone, ev => {
                StringBuilder stringBuilder = new StringBuilder(256);
                SteamVR.instance.overlay.GetKeyboardText(stringBuilder, 256);
                string value = stringBuilder.ToString();
                __instance.SetText(value, true);
            });
            keyboardDoneAction.enabled = true;
        }



        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        //Dunno what this is for
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Window<GClass3542>), "Show")]
        private static void PositionSomeWindow(Window<GClass3542> __instance)
        {
            __instance.transform.localPosition = Vector3.zero;
            __instance.transform.GetChild(0).localPosition = Vector3.zero;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(ReconnectionScreen), "method_5")]
        //private static void CloseInGameMenuFromDisconnectWindow(ReconnectionScreen __instance)
        //{
        //    if (!VRGlobals.inGame || VRGlobals.vrPlayer is HideoutVRPlayerManager)
        //        return;
        //    UIPatches.HideUiScreens();
        //    VRGlobals.vrPlayer.enabled = true;
        //    VRGlobals.menuVRManager.enabled = false;
        //    VRGlobals.menuOpen = false;
        //    Camera.main.enabled = false;
        //    VRGlobals.vrPlayer.RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
        //    // Disabling the FPS cam stops it being main so we need to re-enable it another way
        //    VRGlobals.VRCam.enabled = true;

        //}

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ScavengerInventoryScreen), "method_6")]
        private static void ResetRotOnScavInvScreen(ScavengerInventoryScreen __instance)
        {
            HideoutPatches.camRootRot = Vector3.zeroVector;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TasksPanel), "Show")]
        private static void HideDefaultTaskDesc(TasksPanel __instance)
        {
            __instance._notesTaskDescription.active = false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(OverallScreen), "Show")]
        private static void FixOverallScreenPlayerSize(OverallScreen __instance)
        {
            __instance.WaitOneFrame(delegate { 
                __instance.PlayerModelWithStatsWindow._playerModelView.transform.Find("Camera_inventory").GetComponent<Camera>().fieldOfView = 35;
            });
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AchievementsScreen), "Show")]
        private static void FixAchievementsScreenPlayerSize(AchievementsScreen __instance)
        {
            __instance.WaitOneFrame(delegate {
                __instance.PlayerModelWithStatsWindow._playerModelView.transform.Find("Camera_inventory").GetComponent<Camera>().fieldOfView = 35;
            });
        }
 
    }
}