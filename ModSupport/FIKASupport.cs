using EFT.UI;
using EFT;
using HarmonyLib;
using TarkovVR.Patches.UI;
using UnityEngine;
using TarkovVR.Patches.Misc;
using BepInEx.Configuration;
using Fika.Core;
using EFT.Communications;
using Fika.Core.Coop.GameMode;
using Fika.Core.Coop.Utils;
using Fika.Core.Networking;
using static EFT.HealthSystem.ActiveHealthController;
using static Fika.Core.Coop.Components.CoopHandler;
using System;
using Valve.VR.InteractionSystem;
using Valve.VR;
using Comfort.Common;
using TarkovVR.Source.Settings;
using Fika.Core.UI;
using TarkovVR.Patches.Core.Player;
using Fika.Core.Coop.Components;
using Fika.Core.Coop.Players;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;


namespace TarkovVR.ModSupport.FIKA
{
    [HarmonyPatch]
    internal static class FIKASupport
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MatchMakerUI), "Awake")]
        private static void SetMatchMakerUI(MatchMakerUI __instance)
        {
            __instance.transform.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            __instance.transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);
            __instance.transform.localPosition = new Vector3(0.117f, -999.7602f, 0.9748f);
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(TarkovApplication), "method_49")]
        private static bool FixExitRaid(TarkovApplication __instance)
        {
            UIPatches.gameUi.transform.parent = null;
            UIPatches.HandleCloseInventory();

            if (UIPatches.notifierUi != null) { 
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
        [HarmonyPatch(typeof(Fika.Core.Coop.FreeCamera.FreeCameraController), "ShowExtractMessage")]
        private static bool FixExitRaid(Fika.Core.Coop.FreeCamera.FreeCameraController __instance)
        {
            if (FikaPlugin.ShowExtractMessage.Value)
            {
                __instance.extractText = FikaUIGlobals.CreateOverlayText("Press 'B' to extract");
            }
            return false;
        }
        
        static PropertyInfo _cameraParentProp = null;
        static bool _checkedOnce = false;
        //Changes to this patch to fix null spams
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Coop.FreeCamera.FreeCameraController), "Update")]
        private static bool PositionExitRaidUIAndCam(Fika.Core.Coop.FreeCamera.FreeCameraController __instance)
        {

            if (!__instance.extracted)
                return true;


            if (!_checkedOnce)
            {
                _cameraParentProp = AccessTools.Property(__instance.GetType(), "CameraParent");
                if (_cameraParentProp == null)
                    Plugin.MyLog.LogWarning("CameraParent property not found on FreeCameraController.");
                _checkedOnce = true;
            }

            GameObject cameraParent = null;
            if (_cameraParentProp != null)
            {
                cameraParent = _cameraParentProp.GetValue(__instance) as GameObject;
            }

            // Proceed only if cameraParent exists
            if (cameraParent != null && Camera.main.transform.parent == null)
            {
                Camera.main.transform.parent = cameraParent.transform;
            }

            var camTransform = Camera.main.transform;
            PreloaderUI.Instance.transform.position = camTransform.position + (camTransform.forward * 0.6f) + (camTransform.up * 0.2f);
            PreloaderUI.Instance.transform.rotation = Quaternion.Euler(0, camTransform.eulerAngles.y, camTransform.eulerAngles.z);

            var leftJoy = SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any);
            var rightJoy = SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any);

            if (cameraParent != null)
            {
                if (Mathf.Abs(leftJoy.y) > VRSettings.GetLeftStickSensitivity())
                    cameraParent.transform.position += cameraParent.transform.forward * (leftJoy.y / 10f);

                if (Mathf.Abs(leftJoy.x) > VRSettings.GetLeftStickSensitivity())
                    cameraParent.transform.position += cameraParent.transform.right * (leftJoy.x / 10f);

                if (Mathf.Abs(rightJoy.y) > Mathf.Abs(rightJoy.x))
                    cameraParent.transform.position += new Vector3(0f, rightJoy.y / 10f, 0f);
                else if (Mathf.Abs(rightJoy.x) > Mathf.Abs(rightJoy.y))
                    cameraParent.transform.rotation = Quaternion.Euler(0f, cameraParent.transform.eulerAngles.y + (rightJoy.x * 6f), 0f);
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fika.Core.Coop.GameMode.CoopGame), "CreateStartButton")]
        private static void AddLaserBackToRaidLoading(Fika.Core.Coop.GameMode.CoopGame __instance) {
            VRGlobals.menuVRManager.OnEnable();
        }

        //Added here to make gun and body reappear at start of raid
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
        
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Coop.Components.CoopHandler), "ProcessQuitting")]
        private static bool OverrideExitRaidButton(Fika.Core.Coop.Components.CoopHandler __instance)
        {
            if (FikaPlugin.ExtractKey.Value.IsDown())
                return true;

            var quitState = Traverse.Create(__instance).Method("GetQuitState").GetValue<CoopHandler.EQuitState>();
            if (quitState == CoopHandler.EQuitState.None || __instance.requestQuitGame)
                return false;

            ConsoleScreen.Log($"{FikaPlugin.ExtractKey.Value} pressed, attempting to extract!");
            Plugin.MyLog.LogInfo($"{FikaPlugin.ExtractKey.Value} pressed, attempting to extract!");
            __instance.requestQuitGame = true;

            CoopGame coopGame = (CoopGame)Singleton<IFikaGame>.Instance;

            if (!__instance.isClient)
            {
                try
                {
                    var humanPlayersProp = AccessTools.Property(__instance.GetType(), "HumanPlayers");
                    var humanPlayers = humanPlayersProp?.GetValue(__instance) as List<CoopPlayer>;

                    if (coopGame.ExitStatus == ExitStatus.Transit && (humanPlayers?.Count ?? 0) <= 1)
                    {
                        coopGame.Stop(Singleton<GameWorld>.Instance.MainPlayer.ProfileId, coopGame.ExitStatus, coopGame.ExitLocation, 0f);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.MyLog.LogError($"Failed to access HumanPlayers: {ex}");
                }

                if (Singleton<FikaServer>.Instance.NetServer.ConnectedPeersCount > 0 && quitState != CoopHandler.EQuitState.None)
                {
                    NotificationManagerClass.DisplayWarningNotification(GClass2112.Localized("F_Client_HostCannotExtract", null), ENotificationDurationType.Default);
                    __instance.requestQuitGame = false;
                    return false;
                }

                if (Singleton<FikaServer>.Instance.NetServer.ConnectedPeersCount == 0 &&
                    Singleton<FikaServer>.Instance.TimeSinceLastPeerDisconnected > DateTime.Now.AddSeconds(-5.0) &&
                    Singleton<FikaServer>.Instance.HasHadPeer)
                {
                    NotificationManagerClass.DisplayWarningNotification(GClass2112.Localized("F_Client_Wait5Seconds", null), ENotificationDurationType.Default);
                    __instance.requestQuitGame = false;
                    return false;
                }

                coopGame.Stop(Singleton<GameWorld>.Instance.MainPlayer.ProfileId, coopGame.ExitStatus,
                    __instance.MyPlayer.ActiveHealthController.IsAlive ? coopGame.ExitLocation : null, 0f);
                return false;
            }

            coopGame.Stop(Singleton<GameWorld>.Instance.MainPlayer.ProfileId, coopGame.ExitStatus,
                __instance.MyPlayer.ActiveHealthController.IsAlive ? coopGame.ExitLocation : null, 0f);

            return false;
        }
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Coop.Components.CoopHandler), "ProcessQuitting")]
        private static bool OverrideExitRaidButton(Fika.Core.Coop.Components.CoopHandler __instance)
        {
            EQuitState quitState = __instance.GetQuitState();
            if ( (VRSettings.GetLeftHandedMode() ? !SteamVR_Actions._default.ButtonY.stateDown : !SteamVR_Actions._default.ButtonB.stateDown) || quitState == EQuitState.None || __instance.requestQuitGame)
            {
                return false;
            }
            ConsoleScreen.Log($"{FikaPlugin.ExtractKey.Value} pressed, attempting to extract!");
            Plugin.MyLog.LogInfo((object)$"{FikaPlugin.ExtractKey.Value} pressed, attempting to extract!");
            __instance.requestQuitGame = true;
            CoopGame coopGame = (CoopGame)Singleton<IFikaGame>.Instance;
            var humanPlayersField = AccessTools.Property(__instance.GetType(), "HumanPlayers");
            if (!__instance.isClient)
            {
                if (coopGame.ExitStatus == ExitStatus.Transit && __instance.HumanPlayers.Count <= 1)
                {
                    coopGame.Stop(Singleton<GameWorld>.Instance.MainPlayer.ProfileId, coopGame.ExitStatus, coopGame.ExitLocation);
                }
                else if (Singleton<FikaServer>.Instance.NetServer.ConnectedPeersCount > 0 && quitState != EQuitState.None)
                {
                    NotificationManagerClass.DisplayWarningNotification(GClass2112.Localized("F_Client_HostCannotExtract"));
                    __instance.requestQuitGame = false;
                }
                else if (Singleton<FikaServer>.Instance.NetServer.ConnectedPeersCount == 0 && Singleton<FikaServer>.Instance.TimeSinceLastPeerDisconnected > DateTime.Now.AddSeconds(-5.0) && Singleton<FikaServer>.Instance.HasHadPeer)
                {
                    NotificationManagerClass.DisplayWarningNotification(GClass2112.Localized("F_Client_Wait5Seconds"));
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
    }
}
