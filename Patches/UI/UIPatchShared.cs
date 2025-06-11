using Comfort.Common;
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Patches.Core.Equippables;
using TarkovVR.Patches.Visuals;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.UI;
using UnityEngine;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class UIPatchShared
    {
        [HarmonyPatch]
        internal class UIPatches
        {
            private static int playerLayer = 8;
            public static CircularSegmentUI quickSlotUi;
            public static EftBattleUIScreen battleScreenUi;
            public static BattleStancePanel stancePanel;
            public static CharacterHealthPanel healthPanel;
            public static GameUI gameUi;
            public static AnimatedTextPanel opticUi;
            public static NotifierView notifierUi;
            public static ExtractionTimersPanel extractionTimerUi;
            public static VRUIInteracter vrUiInteracter;


            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            [HarmonyPostfix]
            [HarmonyPatch(typeof(UsingPanel), "Init")]
            private static void SetGameUI(UsingPanel __instance)
            {

                gameUi = __instance.transform.root.GetComponent<GameUI>();
                battleScreenUi = VRGlobals.commonUi.GetComponent<CommonUI>().EftBattleUIScreen;
                battleScreenUi.transform.SetParent(VRGlobals.camRoot.transform, false);
                battleScreenUi.transform.localPosition = Vector3.zero;
                battleScreenUi.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
                opticUi = battleScreenUi._opticCratePanel;
                opticUi.transform.localScale = new Vector3(0.3f, 0.3f, 0.3f);
                gameUi.GetComponent<RectTransform>().sizeDelta = new Vector2(2560, 1440);
                //VRGlobals.vrPlayer.interactionUi = UIPatches.battleScreenUi.ActionPanel._interactionButtonsContainer;
                if (!VRGlobals.camRoot)
                    return;

                HUDPatches.PositionGameUi(gameUi);

            }


            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            public static void HideUiScreens()
            {
                if (VRGlobals.menuUi)
                    VRGlobals.menuUi.GetChild(0).GetComponent<Canvas>().enabled = false;
                VRGlobals.commonUi.GetChild(0).GetComponent<Canvas>().enabled = false;
                VRGlobals.preloaderUi.GetChild(0).GetComponent<Canvas>().enabled = false;
            }


            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            public static void ShowUiScreens()
            {
                if (VRGlobals.menuUi)
                    VRGlobals.menuUi.GetChild(0).GetComponent<Canvas>().enabled = true;
                VRGlobals.commonUi.GetChild(0).GetComponent<Canvas>().enabled = true;
                VRGlobals.preloaderUi.GetChild(0).GetComponent<Canvas>().enabled = true;
            }


            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            public static void HandleOpenInventory()
            {
                if (VRGlobals.vrPlayer is RaidVRPlayerManager)
                {
                    MemoryControllerClass.Collect();
                }
                else
                {
                    MemoryControllerClass.GCEnabled = true;
                    MemoryControllerClass.Collect();
                }
                if (MemoryControllerClass.Settings.OverrideRamCleanerSettings ? MemoryControllerClass.Settings.RamCleanerEnabled : ((bool)Singleton<SharedGameSettingsClass>.Instance.Game.Settings.AutoEmptyWorkingSet))
                {
                    MemoryControllerClass.EmptyWorkingSet();
                }
                Rendering.ClearRenderTargetPool();
                Cursor.lockState = CursorLockMode.Locked;
                ShowUiScreens();
                if (VRGlobals.player?.PlayerBody?.MeshTransform != null)
                    foreach (var renderer in VRGlobals.player.PlayerBody.MeshTransform.GetComponentsInChildren<Renderer>(true))
                        renderer.enabled = false;
                if (EquippablesShared.currentGunInteractController != null)
                {
                    if (EquippablesShared.currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
                VRGlobals.menuOpen = true;
                VRGlobals.blockRightJoystick = true;
                VRGlobals.blockLeftJoystick = true;
                VRGlobals.vrPlayer.enabled = false;
                VRGlobals.menuVRManager.enabled = true;

                Transform head = Camera.main.transform;
                // Use horizontal forward/right
                Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                Quaternion flatRotation = Quaternion.LookRotation(flatForward, Vector3.up);
                Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward).normalized;

                float distance = 0.9f;
                float heightOffset = -0.4f;
                float horizontalOffset = -0.7f; // Move UI slightly to the left
                float extraYOffset = 0f;

                Vector3 basePos = VRGlobals.camRoot.transform.position; // stable world anchor

                if (VRGlobals.player.IsInPronePose)
                    extraYOffset -= 1.5f;
                else
                {
                    float poseLevel = VRGlobals.player.MovementContext._poseLevel;
                    float crouchOffset = (1 - poseLevel) * 0.6f; // 0.6f can be adjusted to change position of menu based on crouch
                    extraYOffset -= crouchOffset;
                }

                Transform ui = VRGlobals.commonUi;
                ui.SetParent(VRGlobals.camRoot.transform);
                ui.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
                ui.position = VRGlobals.camRoot.transform.position + flatForward * distance + flatRight * horizontalOffset + new Vector3(0, heightOffset + extraYOffset, 0);
                ui.localEulerAngles = Vector3.zero;
                ui.rotation = flatRotation;

                if (VRGlobals.menuUi)
                {
                    Transform menuUi = VRGlobals.menuUi;
                    menuUi.SetParent(VRGlobals.camRoot.transform);
                    menuUi.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
                    menuUi.position = VRGlobals.camRoot.transform.position + flatForward * distance + flatRight * 0.05f + new Vector3(0, -0.05f + extraYOffset, 0);
                    menuUi.rotation = flatRotation;
                }

                if (VRGlobals.preloaderUi)
                {
                    Transform preloader = VRGlobals.preloaderUi;
                    preloader.SetParent(VRGlobals.camRoot.transform);
                    preloader.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
                    preloader.GetChild(0).localScale = new Vector3(1.3333f, 1.3333f, 1.3333f);
                    preloader.position = VRGlobals.camRoot.transform.position + flatForward * distance + flatRight * 0.05f + new Vector3(0, -0.15f + extraYOffset, 0);
                    preloader.rotation = flatRotation;

                    if (UIPatches.notifierUi)
                    {
                        UIPatches.notifierUi.transform.parent = PreloaderUI.Instance._alphaVersionLabel.transform.parent;
                        UIPatches.notifierUi.transform.localPosition = new Vector3(1920, 0, 0);
                        UIPatches.notifierUi.transform.localRotation = Quaternion.identity;
                        UIPatches.notifierUi.transform.localScale = Vector3.one;
                    }
                }
            }


            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            public static void HandleCloseInventory()
            {
                HideUiScreens();

                if (VRGlobals.player?.PlayerBody?.MeshTransform != null)
                    foreach (var renderer in VRGlobals.player.PlayerBody.MeshTransform.GetComponentsInChildren<Renderer>(true))
                        renderer.enabled = true;
                if (EquippablesShared.currentGunInteractController != null)
                {
                    if (EquippablesShared.currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = true;
                }
                //int bitmask = 1 << playerLayer; // 256
                //Camera.main.cullingMask |= bitmask; // -524321 & -257
                VRGlobals.menuOpen = false;
                VRGlobals.blockRightJoystick = false;
                VRGlobals.blockLeftJoystick = false;
                VRGlobals.vrPlayer.enabled = true;
                VRGlobals.menuVRManager.enabled = false;
                VRGlobals.commonUi.parent = null;
                VRGlobals.commonUi.position = new Vector3(1000, 1000, 1000);
                VRGlobals.preloaderUi.parent = null;
                VRGlobals.preloaderUi.position = new Vector3(1000, 1000, 1000);
                VRGlobals.vrPlayer.SetNotificationUi();
                //VRGlobals.vrOffsetter.transform.localRotation = Quaternion.identity;
                //VRGlobals.camRoot.transform.eulerAngles = new Vector3(0, lastCamRootYRot, 0);

            }
        }
    }
}
