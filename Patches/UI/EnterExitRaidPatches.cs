using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Matchmaker;
using EFT.UI.SessionEnd;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Patches.Misc;
using UnityEngine;
using static EFT.UI.MenuScreen;
using static TarkovVR.Patches.UI.UIPatchShared;


namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class EnterExitRaidPatches
    {

        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BannerPageToggle), "Init")]
        private static void PositionLoadRaidBannerToggles(BannerPageToggle __instance)
        {
            __instance.transform.localScale = Vector3.one;
            Vector3 newPos = __instance.transform.localPosition;
            newPos.z = 0;
            __instance.transform.localPosition = newPos;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MatchMakerPlayerPreview), "Show")]
        private static void SetLoadRaidPlayerViewCamFoV(MatchMakerPlayerPreview __instance)
        {
            Transform camHolder = __instance._playerModelView.transform.Find("Camera_acceptScreen");
            if (camHolder)
                camHolder.GetComponent<Camera>().fieldOfView = 20;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattleUIPanelExitTrigger), "Show")]
        private static void PositionExtractPanel(BattleUIPanelExitTrigger __instance)
        {
            UIPatches.gameUi.transform.parent = VRGlobals.player.gameObject.transform;
            UIPatches.gameUi.transform.localScale = new Vector3(0.0008f, 0.0008f, 0.0008f);
            UIPatches.gameUi.transform.localPosition = new Vector3(0.02f, 1.7f, 0.48f);
            UIPatches.gameUi.transform.localEulerAngles = new Vector3(29.7315f, 0.4971f, 0f);
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.BaseLocalGame<EftGamePlayerOwner>.Class1513), "method_0")]
        private static bool SetUiOnExtractOrDeath(EFT.BaseLocalGame<EftGamePlayerOwner>.Class1513 __instance)
        {
            if (!__instance.baseLocalGame_0.PlayerOwner.player_0.IsYourPlayer)
                return true;

            GameObject deathPositioner = new GameObject("DeathPos");
            deathPositioner.transform.position = VRGlobals.emptyHands.position;
            deathPositioner.transform.rotation = VRGlobals.emptyHands.rotation;
            VRGlobals.emptyHands = deathPositioner.transform;

            UIPatches.gameUi.transform.parent = null;
            UIPatches.HandleCloseInventory();
            if (UIPatches.notifierUi != null)
                UIPatches.notifierUi.transform.parent = PreloaderUI.Instance._alphaVersionLabel.transform.parent;

            if (UIPatches.extractionTimerUi != null)
                UIPatches.extractionTimerUi.transform.parent = UIPatches.gameUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.healthPanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.stancePanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.battleScreenUi != null)
                UIPatches.battleScreenUi.transform.parent = VRGlobals.commonUi.GetChild(0);


            PreloaderUI.DontDestroyOnLoad(UIPatches.gameUi);
            PreloaderUI.DontDestroyOnLoad(Camera.main.gameObject);
            VRGlobals.inGame = false;
            VRGlobals.menuOpen = true;
            MainMenuUIPatches.PositionMainMenuUi();
            return true;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerModelView), "Show", new Type[] { typeof(GClass1952), typeof(InventoryController), typeof(Action), typeof(float), typeof(Vector3), typeof(bool) })]
        private static void PositionRaidPlayerModelPreview(PlayerModelView __instance, Task __result)
        {
            __result.ContinueWith(task =>
            {
                if (task.IsCompleted)
                {
                    if (__instance.transform.parent.name == "ScavPlayerMV")
                    {
                        __instance.transform.Find("Camera_matchmaker").GetComponent<Camera>().fieldOfView = 45;
                        //Transform camBodyViewer = __instance.transform.FindChild("Camera_matchmaker");
                        //camBodyViewer.position = new Vector3(-0.41f, -999.2792f, 4.68f);
                        //Transform scavBody = __instance.PlayerBody.transform;
                        //scavBody.position = new Vector3(0.0818f, -1000.139f, 5.9f);
                    }
                    else if (__instance.transform.parent.name == "PMCPlayerMV")
                    {
                        Transform camera = __instance.transform.Find("Camera_matchmaker");
                        camera.localEulerAngles = new Vector3(353, 19, 0);
                        camera.GetComponent<Camera>().fieldOfView = 45;
                        Transform pmcBody = __instance.PlayerBody.transform;
                        pmcBody.localPosition = new Vector3(2, -1, 5);
                        __instance.transform.Find("Lights").transform.localEulerAngles = new Vector3(17, 114, 0);
                    }
                    else if (__instance.transform.Find("Camera_timehascome0"))
                    {
                        Transform camera = __instance.transform.Find("Camera_timehascome0");
                        camera.localPosition = new Vector3(-1.4f, 0.6f, 3.45f);
                        camera.GetComponent<Camera>().fieldOfView = 41;
                    }
                    else if (__instance.transform.root.name == "Session End UI")
                    {
                        Transform camera = __instance.transform.Find("Camera_matchmaker");
                        camera.GetComponent<Camera>().fieldOfView = 35;
                    }
                    //else if (__instance.transform.parent.parent.name == "UsecPanel")
                    //{
                    //    Transform camera = __instance.transform.FindChildRecursive("Camera_matchmaker");
                    //    camera.localEulerAngles = new Vector3(353, 356, 0);
                    //    camera.GetComponent<Camera>().fieldOfView = 41;
                    //    Transform pmcBody = __instance.PlayerBody.transform;
                    //    pmcBody.localPosition = new Vector3(0.4f,0,0);
                    //}
                    //else if (__instance.transform.parent.parent.name == "BearPanel")
                    //{
                    //    Transform camera = __instance.transform.FindChildRecursive("Camera_matchmaker");
                    //    camera.GetComponent<Camera>().fieldOfView = 41;
                    //}
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.BaseLocalGame<EftGamePlayerOwner>.Class1512), "method_0")]
        private static bool SetUiOnExtractOrDeathOther(EFT.BaseLocalGame<EftGamePlayerOwner>.Class1512 __instance)
        {


            if (!__instance.baseLocalGame_0.PlayerOwner.player_0.IsYourPlayer)
                return true;

            GameObject deathPositioner = new GameObject("DeathPos");
            deathPositioner.transform.position = VRGlobals.emptyHands.position;
            deathPositioner.transform.rotation = VRGlobals.emptyHands.rotation;
            VRGlobals.emptyHands = deathPositioner.transform;

            UIPatches.gameUi.transform.parent = null;
            UIPatches.HandleCloseInventory();

            if (UIPatches.notifierUi != null)
                UIPatches.notifierUi.transform.parent = PreloaderUI.Instance._alphaVersionLabel.transform.parent;

            if (UIPatches.extractionTimerUi != null)
                UIPatches.extractionTimerUi.transform.parent = UIPatches.gameUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.healthPanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.stancePanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.battleScreenUi != null)
                UIPatches.battleScreenUi.transform.parent = VRGlobals.commonUi.GetChild(0);


            PreloaderUI.DontDestroyOnLoad(UIPatches.gameUi);
            PreloaderUI.DontDestroyOnLoad(Camera.main.gameObject);
            VRGlobals.inGame = false;
            VRGlobals.menuOpen = true;

            MainMenuUIPatches.PositionMainMenuUi();
            return true;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SessionEndUI), "Awake")]
        private static void SetSessionEndUI(SessionEndUI __instance)
        {
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            BoxCollider colllider = __instance.gameObject.AddComponent<BoxCollider>();
            colllider.extents = new Vector3(2560, 1440, 0.5f);
            __instance.transform.eulerAngles = Vector3.zero;
            Canvas canvas = __instance.gameObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            __instance.transform.localScale = new Vector3(0.0011f, 0.0011f, 0.0011f);
            __instance.transform.position = new Vector3(0f, -999.9333f, 1);
            MenuPatches.FixMainMenuCamera();
            VRGlobals.inGame = false;
            VRGlobals.menuOpen = true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GClass3590), "ShowAction")]
        private static void PositionInRaidMenu(GClass3590 __instance)
        {
            if (!VRGlobals.inGame)
                return;

            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            Transform mainMenuCam = EnvironmentUI.Instance.environmentUIRoot_0.CameraContainer.Find("MainMenuCamera");
            MainMenuUIPatches.PositionMenuEnvironmentProps();
            MainMenuUIPatches.PositionMainMenuUi();
            UIPatches.ShowUiScreens();
            VRGlobals.vrPlayer.enabled = false;
            VRGlobals.menuVRManager.enabled = true;
            VRGlobals.menuOpen = true;
            // Move the right hand over so its synced up with the env UI cam
            VRGlobals.vrPlayer.RightHand.transform.parent = mainMenuCam.parent;

            // The FPS cam messes with UI selection so disable it temporarily
            if (Camera.main.name == "FPS Camera")
            {
                VRGlobals.VRCam = Camera.main;
                Camera.main.enabled = false;
            }

            //Plugin.MyLog.LogWarning("Opening menu in raid");

        }

    }
}
