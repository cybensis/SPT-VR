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
using TarkovVR.Patches.Visuals;



namespace TarkovVR.ModSupport.FIKA
{
    [HarmonyPatch]
    internal static class FIKASupport
    {

        private static bool _vrSpectateMode = false;
        private static FikaPlayer _spectatedPlayer = null;
        private static Vector3 _worldOffset = Vector3.zero;
        private static bool _spectateInitialized = false;
        private static readonly float _spectateRadius = 3f;
        private static float _spectateYaw = 0f;


        [HarmonyPostfix]
        [HarmonyPatch(typeof(LoadingScreenUI), "Awake")]
        private static void SetLoadingScreenUI(LoadingScreenUI __instance)
        {
            Transform transform = __instance.transform;
            __instance.WaitOneFrame(delegate
            {
                var canvasTransform = transform.GetChild(0);
                var canvas = canvasTransform.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;

                canvasTransform.gameObject.layer = LayerMask.NameToLayer("UI");
                canvasTransform.localPosition = new Vector3(0, -999.9f, 1);
                canvasTransform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
            });
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MatchMakerUI), "Awake")]
        private static void SetMatchMakerUI(MatchMakerUI __instance)
        {
            Transform transform = __instance.transform;
            __instance.WaitOneFrame(delegate
            {
                transform.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                transform.localPosition = new Vector3(0, 700, 0);
                transform.localScale = new Vector3(1, 1, 1);
            });
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fika.Core.UI.Custom.MainMenuUIScript), "OnEnable")]
        private static void SetMainMenuUI(Fika.Core.UI.Custom.MainMenuUIScript __instance)
        {
            if (__instance == null)
                return;

            if (__instance._mainMenuUI == null)
                return;

            Transform mainMenuUiChild = __instance._mainMenuUI.transform;
            if (mainMenuUiChild == null)
                return;

            if (mainMenuUiChild == null || mainMenuUiChild.childCount == 0)
                return;

            Transform firstChild = mainMenuUiChild.GetChild(0);
            if (firstChild == null)
                return;

            Canvas canvas = firstChild.GetComponent<Canvas>();
            if (canvas == null)
                return;

            canvas.renderMode = RenderMode.WorldSpace;

            mainMenuUiChild.GetChild(0).localPosition = new Vector3(0, 0, 0);
            mainMenuUiChild.localPosition = new Vector3(900, -100, 0);
            mainMenuUiChild.localScale = new Vector3(0.8f, 0.8f, 0.8f);
            __instance.WaitOneFrame(delegate
            {
                mainMenuUiChild.GetChild(0).GetChild(0).localPosition = new Vector3(0, 0, 0);
            });
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(SendItemUI), "Awake")]
        private static void SetSendItemUI(SendItemUI __instance)
        {
            Transform transform = __instance.transform;
            __instance.WaitOneFrame(delegate
            {
                transform.GetChild(0).GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                transform.localPosition = new Vector3(-1000, -800, 0);
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
            if (FikaPlugin.Instance.Settings.ShowExtractMessage.Value)
                __instance._extractText = FikaUIGlobals.CreateOverlayText("Press 'B' to extract");
            return false;
        }


        // MAIN VR CAMERA POSITIONING — handles both spectate and extraction.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Main.FreeCamera.FreeCameraController), "Update")]
        private static bool PositionExitRaidUIAndCam(Fika.Core.Main.FreeCamera.FreeCameraController __instance)
        {
            // --- SPECTATE CAMERA (after death) ---
            if (_vrSpectateMode)
            {
                if (_spectatedPlayer == null || _spectatedPlayer.gameObject == null)
                {
                    _vrSpectateMode = false;
                    _spectatedPlayer = null;
                    return true;
                }

                if (__instance._cameraParent == null)
                    return true;

                if (!_spectateInitialized)
                {
                    Vector3 neckPos = _spectatedPlayer.PlayerBones.Neck.position;
                    Vector3 playerForward = _spectatedPlayer.gameObject.transform.forward;

                    _worldOffset = -playerForward * 2f + Vector3.up * 0.5f;
                    _spectateYaw = 0f;

                    __instance._cameraParent.transform.position = neckPos + _worldOffset;
                    __instance._cameraParent.transform.rotation = Quaternion.identity;

                    if (Camera.main.transform.parent != __instance._cameraParent.transform)
                    {
                        Camera.main.transform.parent = __instance._cameraParent.transform;
                        Camera.main.transform.localPosition = Vector3.zero;
                        Camera.main.transform.localRotation = Quaternion.identity;
                    }

                    _spectateInitialized = true;
                }

                // HMD directions for left stick movement
                Vector3 hmdForward = Camera.main.transform.forward;
                Vector3 hmdRight = Camera.main.transform.right;
                hmdForward.y = 0; hmdForward.Normalize();
                hmdRight.y = 0; hmdRight.Normalize();

                // Right joystick: X = rotate view, Y = up/down
                float rX = SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).x;
                float rY = SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y;

                // Slightly other constants than for the extraction free camera (slower movement)
                if (Mathf.Abs(rY) > Mathf.Abs(rX))
                {
                    _worldOffset += new Vector3(0, rY / 22, 0);
                }
                else if (Mathf.Abs(rX) > Mathf.Abs(rY))
                {
                    _spectateYaw += rX * 2.5f;
                }

                // Left joystick: strafe in HMD directions
                float lX = SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).x;
                float lY = SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).y;
                if (Mathf.Abs(lX) > VRSettings.GetLeftStickSensitivity() || Mathf.Abs(lY) > VRSettings.GetLeftStickSensitivity())
                {
                    _worldOffset += (hmdRight * lX + hmdForward * lY) * 2f * Time.deltaTime;
                }

                // Constrain to sphere
                if (_worldOffset.magnitude > _spectateRadius)
                {
                    _worldOffset = _worldOffset.normalized * _spectateRadius;
                }

                // Apply rotation to _cameraParent
                __instance._cameraParent.transform.rotation = Quaternion.Euler(0, _spectateYaw, 0);

                // Position _cameraParent, compensating for Camera.main's VR head offset
                Vector3 neckPosition = _spectatedPlayer.PlayerBones.Neck.position;
                Vector3 cameraLocalOffset = Camera.main.transform.localPosition;
                Vector3 rotatedOffset = __instance._cameraParent.transform.rotation * cameraLocalOffset;
                __instance._cameraParent.transform.position = neckPosition + _worldOffset - rotatedOffset;

                return true;
            }

            // --- EXTRACTION CAMERA ---
            if (__instance._extracted)
            {
                if (__instance._cameraParent != null && Camera.main.transform.parent == null)
                {
                    // position _cameraParent at last known location BEFORE parenting.
                    // previously _cameraParent was at (0,0,0) when camera got parented to it which resulted in a teleport
                    __instance._cameraParent.transform.position = __instance._lastKnownPosition;
                    __instance._cameraParent.transform.rotation = Quaternion.identity;

                    Camera.main.transform.parent = __instance._cameraParent.transform;
                    Camera.main.transform.localPosition = Vector3.zero;
                    Camera.main.transform.localRotation = Quaternion.identity;
                }

                PreloaderUI.Instance.transform.position = Camera.main.transform.position
                    + (Camera.main.transform.forward * 0.6f)
                    + (Camera.main.transform.up * 0.2f);
                PreloaderUI.Instance.transform.rotation = Quaternion.Euler(0,
                    Camera.main.transform.eulerAngles.y,
                    Camera.main.transform.eulerAngles.z);

                // Use HMD directions for movement, not _cameraParent directions.
                // Previously used _cameraParent.transform.forward which doesn't update when you turn your head.
                Vector3 hmdForward = Camera.main.transform.forward;
                Vector3 hmdRight = Camera.main.transform.right;
                hmdForward.y = 0; hmdForward.Normalize();
                hmdRight.y = 0; hmdRight.Normalize();

                float lY = SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).y;
                if (Mathf.Abs(lY) > VRSettings.GetLeftStickSensitivity())
                {
                    __instance._cameraParent.transform.position += hmdForward * (lY / 10);
                }
                float lX = SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).x;
                if (Mathf.Abs(lX) > VRSettings.GetLeftStickSensitivity())
                {
                    __instance._cameraParent.transform.position += hmdRight * (lX / 10);
                }

                float rY = SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y;
                float rX = SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).x;
                if (Mathf.Abs(rY) > Mathf.Abs(rX))
                {
                    __instance._cameraParent.transform.position += new Vector3(0, rY / 10, 0);
                }
                else if (Mathf.Abs(rX) > Mathf.Abs(rY))
                {
                    __instance._cameraParent.transform.rotation = Quaternion.Euler(0,
                        __instance._cameraParent.transform.eulerAngles.y + (rX * 2.5f), 0);
                }
            }

            return true;
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Main.GameMode.BaseGameController), "CreateStartButton")]
        private static void AddLaserBackToRaidLoading(Fika.Core.Main.GameMode.BaseGameController __instance)
        {
            VRGlobals.menuVRManager.OnEnable();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "ShowRaidStartInfo")]
        private static void DisableLaser(PreloaderUI __instance)
        {
            if (ModSupport.InstalledMods.FIKAInstalled)
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
        }

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

            ConsoleScreen.Log($"{FikaPlugin.Instance.Settings.ExtractKey.Value} pressed, attempting to extract!");
            Plugin.MyLog.LogInfo($"{FikaPlugin.Instance.Settings.ExtractKey.Value} pressed, attempting to extract!");

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


        // Death camera fixes

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Main.FreeCamera.FreeCamera), "Attach3rdPerson")]
        private static bool VRAttach3rdPerson(Fika.Core.Main.FreeCamera.FreeCamera __instance)
        {
            var currentPlayerField = AccessTools.Field(typeof(Fika.Core.Main.FreeCamera.FreeCamera), "_currentPlayer");
            var currentPlayer = currentPlayerField.GetValue(__instance) as FikaPlayer;

            if (currentPlayer == null)
                return true;

            // Set _isFollowing so FreeCamera.Update stays in the "following" branch
            AccessTools.Field(typeof(Fika.Core.Main.FreeCamera.FreeCamera), "_isFollowing").SetValue(__instance, true);

            // Set _cameraState so cycling players later calls Attach3rdPerson again
            var cameraStateField = AccessTools.Field(typeof(Fika.Core.Main.FreeCamera.FreeCamera), "_cameraState");
            cameraStateField.SetValue(__instance, Enum.Parse(cameraStateField.FieldType, "Follow3rdPerson"));

            // Flags for FreeCameraController.Update to use
            _spectatedPlayer = currentPlayer;
            _vrSpectateMode = true;
            _spectateInitialized = false;

#if DEBUG
            FikaGlobals.LogInfo($"VR Freecam: Spectate activated for {currentPlayer.Profile.GetCorrectedNickname()}");
#endif

            return false; // Dont run original
        }

        // Reset globals if spectate ends
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fika.Core.Main.FreeCamera.FreeCamera), "DetachCamera")]
        private static void ResetVRSpectateOnDetach()
        {
            _vrSpectateMode = false;
            _spectatedPlayer = null;
            _spectateInitialized = false;
            _spectateYaw = 0f;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Fika.Core.Main.FreeCamera.FreeCamera), "AttachToPlayer")]
        private static void DisableVRSpectateForHeadcam()
        {
            _vrSpectateMode = false;
            _spectateInitialized = false;
        }

    }
}
