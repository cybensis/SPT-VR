using EFT.UI;
using EFT;
using HarmonyLib;
using TarkovVR.Patches.UI;
using UnityEngine;
using TarkovVR.Patches.Misc;
using BepInEx.Configuration;
using Fika.Core;
using EFT.Communications;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using static EFT.HealthSystem.ActiveHealthController;
using static Fika.Core.Main.Components.CoopHandler;
using System;
using Valve.VR.InteractionSystem;
using Valve.VR;
using Comfort.Common;
using TarkovVR.Source.Settings;
using Fika.Core.UI;
using TarkovVR.Patches.Core.Player;
using Fika.Core.Main.Players;
using System.Collections.Generic;
using TMPro;
using System.Linq;
//using static Fika.Core.Networking.FirearmSubPackets;
//using static Fika.Core.Networking.SubPacket;
using static UnityEngine.ParticleSystem.PlaybackState;
using UnityEngine.UIElements;
using TarkovVR.Patches.Core.VR;
using System.Collections;
using EFT.UI.Matchmaker;



namespace TarkovVR.ModSupport.FIKA
{
    [HarmonyPatch]
    internal static class FIKASupport
    {
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MatchMakerUI), "Awake")]
        private static void SetMatchMakerUI(MatchMakerUI __instance)
        {
            __instance.WaitOneFrame(delegate
            {
                __instance.transform.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                __instance.transform.localPosition = new Vector3(0, 700, 0);
                __instance.transform.localScale = new Vector3(1, 1, 1);
            });
        }
        */
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MatchMakerUI), "Awake")]
        private static void SetMatchMakerUI(MatchMakerUI __instance)
        {
            Transform transform = __instance.transform; // Capture only the Transform, not MatchMakerUI
            __instance.WaitOneFrame(delegate
            {
                transform.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                transform.localPosition = new Vector3(0, 700, 0);
                transform.localScale = new Vector3(1, 1, 1);
            });
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TarkovApplication), "method_53")]
        private static bool FixExitRaid(TarkovApplication __instance)
        {
            UIPatches.HideUiScreens();
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
            UIPatches.gameUi.transform.parent = null;

            if (UIPatches.notifierUi != null)
            {
                UIPatches.notifierUi.transform.parent = PreloaderUI.Instance._alphaVersionLabel.transform.parent;
                UIPatches.notifierUi.transform.localScale = Vector3.one;
                UIPatches.notifierUi.transform.localPosition = new Vector3(1920, 0, 0);
                UIPatches.notifierUi.transform.localRotation = Quaternion.identity;
            }

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

            MenuPatches.PositionMainMenuUi();
            return true;
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Main.FreeCamera.FreeCameraController), "ShowExtractMessage")]
        private static bool FixExitRaid(Fika.Core.Main.FreeCamera.FreeCameraController __instance)
        {
            if (FikaPlugin.ShowExtractMessage.Value)
                __instance._extractText = FikaUIGlobals.CreateOverlayText("Press 'B' to extract");
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Main.FreeCamera.FreeCameraController), "Update")]
        private static bool PositionExitRaidUIAndCam(Fika.Core.Main.FreeCamera.FreeCameraController __instance)
        {
            if (__instance._extracted)
            {
                if (__instance._cameraParent != null && Camera.main.transform.parent == null)
                {
                    Camera.main.transform.parent = __instance._cameraParent.transform;
                }
                PreloaderUI.Instance.transform.position = Camera.main.transform.position + (Camera.main.transform.forward * 0.6f) + (Camera.main.transform.up * 0.2f);
                PreloaderUI.Instance.transform.rotation = Quaternion.Euler(0, Camera.main.transform.eulerAngles.y, Camera.main.transform.eulerAngles.z);
                if (Mathf.Abs(SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).y) > VRSettings.GetLeftStickSensitivity())
                {
                    __instance._cameraParent.transform.position += __instance._cameraParent.transform.forward * (SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).y / 10);
                }
                if (Mathf.Abs(SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).x) > VRSettings.GetLeftStickSensitivity())
                {
                    __instance._cameraParent.transform.position += __instance._cameraParent.transform.right * (SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).x / 10);
                }
                if (Mathf.Abs(SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y) > Mathf.Abs(SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).x))
                {
                    __instance._cameraParent.transform.position += new Vector3(0, SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y / 10, 0);
                }
                else if (Mathf.Abs(SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).x) > Mathf.Abs(SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y))
                {
                    __instance._cameraParent.transform.rotation = Quaternion.Euler(0, __instance._cameraParent.transform.eulerAngles.y + (SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).x * 6), 0);
                }

            }
            return true;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fika.Core.Main.GameMode.BaseGameController), "CreateStartButton")]
        private static void AddLaserBackToRaidLoading(Fika.Core.Main.GameMode.BaseGameController __instance)
        {
            VRGlobals.menuVRManager.OnEnable();
        }
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fika.Core.Coop.GameMode.CoopGame), "WaitForOtherPlayersToLoad")]
        private static void RehideLaser(Fika.Core.Coop.GameMode.CoopGame __instance)
        {
            VRGlobals.menuVRManager.enabled = false;
            VRGlobals.vrPlayer.enabled = true;
            VRGlobals.ikManager.enabled = true;
            if (VRGlobals.menuOpen)
            {
                if (VRGlobals.player?.PlayerBody?.MeshTransform != null)
                    foreach (var renderer in VRGlobals.player.PlayerBody.MeshTransform.GetComponentsInChildren<Renderer>(true))
                        renderer.enabled = true;

                if (WeaponPatches.currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                    foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                        renderer.enabled = true;
            }
            VRGlobals.menuOpen = false;
        }
        */

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fika.Core.Main.GameMode.HostGameController), "WaitForOtherPlayersToLoad")]
        private static void RehideLaserHost(Fika.Core.Main.GameMode.ClientGameController __instance)
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
            
            VRGlobals.menuOpen = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fika.Core.Main.GameMode.ClientGameController), "WaitForOtherPlayersToLoad")]
        private static void RehideLaserClient(Fika.Core.Main.GameMode.ClientGameController __instance)
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

            VRGlobals.menuOpen = false;
        }
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Main.Components.CoopHandler), "ProcessQuitting")]
        private static bool OverrideExitRaidButton(Fika.Core.Main.Components.CoopHandler __instance)
        {
            EQuitState quitState = __instance.GetQuitState();

            if ((VRSettings.GetLeftHandedMode() ? !SteamVR_Actions._default.ButtonY.stateDown : !SteamVR_Actions._default.ButtonB.stateDown) || quitState == EQuitState.None || __instance.requestQuitGame)
            {
                return false;
            }
            ConsoleScreen.Log($"{FikaPlugin.ExtractKey.Value} pressed, attempting to extract!");
            Plugin.MyLog.LogInfo((object)$"{FikaPlugin.ExtractKey.Value} pressed, attempting to extract!");
            __instance.requestQuitGame = true;
            CoopGame coopGame = CoopGame.Instance;
            if (!__instance.isClient)
            {
                if (coopGame.ExitStatus == ExitStatus.Transit && __instance.HumanPlayers.Count <= 1)
                {
                    coopGame.Stop(Singleton<GameWorld>.Instance.MainPlayer.ProfileId, coopGame.ExitStatus, coopGame.ExitLocation);
                }
                else if (Singleton<FikaServer>.Instance.NetServer.ConnectedPeersCount > 0 && quitState != EQuitState.None)
                {
                    NotificationManagerClass.DisplayWarningNotification(GClass2348.Localized("F_Client_HostCannotExtract"));
                    __instance.requestQuitGame = false;
                }
                else if (Singleton<FikaServer>.Instance.NetServer.ConnectedPeersCount == 0 && Singleton<FikaServer>.Instance.TimeSinceLastPeerDisconnected > DateTime.Now.AddSeconds(-5.0) && Singleton<FikaServer>.Instance.HasHadPeer)
                {
                    NotificationManagerClass.DisplayWarningNotification(GClass2348.Localized("F_Client_Wait5Seconds"));
                    __instance.requestQuitGame = false;
                }
                else
                {
                    coopGame.Stop(Singleton<GameWorld>.Instance.MainPlayer.ProfileId, coopGame.ExitStatus, __instance.MyPlayer.ActiveHealthController.IsAlive ? coopGame.ExitLocation : null);
                }
            }
            else
            {
                coopGame.Stop(Singleton<GameWorld>.Instance.MainPlayer.ProfileId, coopGame.ExitStatus, __instance.MyPlayer.ActiveHealthController.IsAlive ? coopGame.ExitLocation : null);
            }
            return false;
        }
        */
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Main.Components.CoopHandler), "ProcessQuitting")]
        private static bool OverrideExitRaidButton(Fika.Core.Main.Components.CoopHandler __instance)
        {
            EQuitState quitState = __instance.QuitState;
            // Check VR button press (B for right-handed, Y for left-handed)
            bool vrExtractPressed = VRSettings.GetLeftHandedMode()
                ? SteamVR_Actions._default.ButtonY.stateDown
                : SteamVR_Actions._default.ButtonB.stateDown;

            if (!vrExtractPressed || quitState == EQuitState.None || __instance._requestQuitGame)
                return false;

            ConsoleScreen.Log($"{FikaPlugin.ExtractKey.Value} pressed, attempting to extract!");
            Plugin.MyLog.LogInfo($"{FikaPlugin.ExtractKey.Value} pressed, attempting to extract!");

            __instance._requestQuitGame = true;

            IFikaGame localGameInstance = __instance.LocalGameInstance;
            string exitName = __instance.MyPlayer.ActiveHealthController.IsAlive ? localGameInstance.ExitLocation : null;

            if (__instance._isClient)
            {
                localGameInstance.Stop(__instance.MyPlayer.ProfileId, localGameInstance.ExitStatus, exitName);
                return false;
            }

            FikaServer fikaServer = Singleton<FikaServer>.Instance;
            int connectedPeers = fikaServer.NetServer.ConnectedPeersCount;

            if (localGameInstance.ExitStatus == ExitStatus.Transit && __instance.HumanPlayers.Count <= 1)
            {
                localGameInstance.Stop(__instance.MyPlayer.ProfileId, localGameInstance.ExitStatus, exitName);
                return false;
            }

            if (connectedPeers > 0)
            {
                NotificationManagerClass.DisplayWarningNotification(GClass2348.Localized("F_Client_HostCannotExtract"));
                __instance._requestQuitGame = false;
                return false;
            }

            bool recentDisconnect = fikaServer.TimeSinceLastPeerDisconnected > DateTime.Now.AddSeconds(-5.0);
            if (fikaServer.HasHadPeer && recentDisconnect)
            {
                NotificationManagerClass.DisplayWarningNotification(GClass2348.Localized("F_Client_Wait5Seconds"));
                __instance._requestQuitGame = false;
                return false;
            }

            localGameInstance.Stop(__instance.MyPlayer.ProfileId, localGameInstance.ExitStatus, exitName);
            return false;
        }
    }
}
