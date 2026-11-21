using EFT.UI;
using EFT;
using HarmonyLib;
using TarkovVR.Patches.UI;
using UnityEngine;
using TarkovVR.Patches.Misc;
using Fika.Core;
using EFT.Communications;
using Fika.Core.Main.GameMode;
using Fika.Core.Main.Utils;
using Fika.Core.Networking;
using static Fika.Core.Main.Components.CoopHandler;
using System;
using Valve.VR;
using Comfort.Common;
using TarkovVR.Source.Settings;
using Fika.Core.UI;
using EFT.Interactive;
using Fika.Core.Main.Components;
using System.Collections.Generic;
using TarkovVR.Source.Controls;



namespace TarkovVR.ModSupport.FIKA
{
    [HarmonyPatch]
    internal static class FIKASupport
    {
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

        // Grabbing, summoning or taking loose loot reparents the item, makes it kinematic or moves it into the inventory, while FIKA's
        // ItemPositionSyncer may still track it (RemoveFikaSyncer only removes it on grab). NotifyDone can then throw a
        // NullReferenceException every FixedUpdate. The sync doesn't matter for an item we control, so only that exception is swallowed.
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Fika.Core.Main.Components.ItemPositionSyncer), "NotifyDone")]
        private static Exception SilenceItemSyncerNotifyDone(Exception __exception)
        {
            return __exception is NullReferenceException ? null : __exception;
        }

        // ItemPositionSyncer.Start throws a NullReferenceException when the syncer is created on an item whose rigidbody or state isn't
        // valid yet (a loose item we re-sync on drop, or FIKA's ThrowItem path racing our reparent). That drop just won't sync over the
        // network. Only that exception is swallowed.
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Fika.Core.Main.Components.ItemPositionSyncer), "Start")]
        private static Exception SilenceItemSyncerStart(Exception __exception)
        {
            return __exception is NullReferenceException ? null : __exception;
        }

        // ===== Loose-loot position sync for the VR loot pointer =====
        // FIKA only syncs items the local player throws. Its ThrowItem patch creates an ItemPositionSyncer, which destroys itself once
        // the item settles, so a resting item grabbed with the loot pointer has no syncer. SyncHeldLootItem sends the same
        // LootSyncStruct that the syncer's FixedUpdate sends (receivers apply it in ObservedLootItem.ApplyNetPacket). ResyncDroppedItem
        // re-creates FIKA's syncer on release so the drop syncs the normal way. Both do nothing for items that aren't ObservedLootItem.
        // Only called when InstalledMods.FIKAInstalled, so they are never JIT-compiled without FIKA.

        public static void SyncHeldLootItem(LootItem item)
        {
            if (item == null || !(item is ObservedLootItem))
                return;

            LootSyncPacket data = default;
            data.Id = item.GetNetId();
            data.Position = item.transform.position;
            data.Rotation = item.transform.rotation;
            data.Velocity = Vector3.zero;          // kinematic in-hand — no physics velocity
            data.AngularVelocity = Vector3.zero;
            data.Done = false;                     // still being moved by the holder

            if (FikaBackendUtils.IsServer)
                Singleton<FikaServer>.Instance?.FikaHostWorld?.AddLootSyncStruct(data);
            else
                Singleton<FikaClient>.Instance?.FikaClientWorld?.AddLootSyncStruct(data);
        }

        public static void ResyncDroppedItem(LootItem item)
        {
            if (item == null || !(item is ObservedLootItem observed))
                return;
            // ItemPositionSyncer.Start needs a live rigidbody (it reads velocity each tick until the item settles, then sends Done)
            if (item.RigidBody == null)
                return;
            // Nothing to send to in the hideout, which has no FIKA session (see StopSyncerWithoutWorld)
            if (!HasLootSyncWorld(FikaBackendUtils.IsServer))
                return;
            ItemPositionSyncer.Create(item.gameObject, FikaBackendUtils.IsServer, observed);
        }

        // FIKA's host / client world that takes the loot position packets; none outside a FIKA session (the hideout)
        private static bool HasLootSyncWorld(bool isServer)
        {
            return isServer ? Singleton<FikaServer>.Instance?.FikaHostWorld != null : Singleton<FikaClient>.Instance?.FikaClientWorld != null;
        }

        // In the hideout there is no FIKA session, so an ItemPositionSyncer has no host / client world to send to. Its FixedUpdate
        // dereferences the missing world every physics tick while the item moves and never reaches its own Destroy, so it is destroyed here.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemPositionSyncer), "FixedUpdate")]
        private static bool StopSyncerWithoutWorld(ItemPositionSyncer __instance)
        {
            bool hasWorld = __instance._isServer
                ? __instance._server != null && __instance._server.FikaHostWorld != null
                : __instance._client != null && __instance._client.FikaClientWorld != null;
            if (hasWorld)
                return true;
            UnityEngine.Object.Destroy(__instance);
            return false;
        }

        // ===== Receiver side: a local rigidbody must not fight a remote player's hold =====
        // ObservedLootItem.ApplyNetPacket sets the transform and velocity from each packet. An item we grabbed and dropped keeps our
        // non-kinematic rigidbody (DropObject). When another player then holds it (poses at ~25 Hz), our 50 Hz gravity makes it
        // stutter between packets. SyncHeldLootItem marks a held item with zero velocity and Done == false; a thrown or settling item
        // carries real velocity. So while a remote hold is applied, our rigidbody's gravity is off; it comes back on real motion or Done.
        // While we hold the item ourselves, the packet is ignored. If two players hold one item, the newest grab wins.

        // NetId -> Time.time until which the item counts as held by another player. Set in ApplyNetPacket on a "held" packet
        // (Done == false and zero velocity). Our own held broadcasts never come back to us, so a held packet is always another player's.
        private static readonly Dictionary<int, float> remoteHeldUntil = new Dictionary<int, float>();
        // s. Holders broadcast at ~25/s (lootSyncInterval 0.04), so 0.3 s = ~7 missed packets before the item counts as free again
        public static float remoteHeldGrace = 0.3f;
        // s. A player who has held the contested item longer than this yields to the more recent grabber. Well above the ~80 ms a
        // hand-off takes, below a realistic gap between two deliberate grabs.
        public static float stealGraceTime = 0.4f;

        // Received held-item poses (~25 Hz) go through a per-frame exponential interpolator (LootSyncSmoother, the same smoothing as the
        // arm sync) instead of FIKA's SetPositionAndRotation snap. Held items only; thrown, dropped and settling items keep FIKA's sync.
        public static float lootSmoothRate = 18f;          // matches ArmSyncApply.smoothRate
        public static float lootSmoothStaleTimeout = 0.5f; // stop lerping if the holder goes quiet

        /// <summary>True while another player is actively holding this loose item.</summary>
        public static bool IsRemotelyHeldRaw(LootItem item)
        {
            if (item == null)
                return false;
            return remoteHeldUntil.TryGetValue(item.GetNetId(), out float until) && Time.time < until;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObservedLootItem), "ApplyNetPacket")]
        private static bool NeutralizeRemoteHeldLootPhysics(ObservedLootItem __instance, LootSyncPacket packet)
        {
            // Copied to a local because the Harmony analyzer treats struct-field reads on a by-value patch parameter as writes
            LootSyncPacket packetCopy = packet;
            bool incomingHeld = !packetCopy.Done
                                && packetCopy.Velocity.sqrMagnitude < 1e-6f
                                && packetCopy.AngularVelocity.sqrMagnitude < 1e-6f;

            var hands = VRGlobals.handsInteractionController;
            if (hands != null && ReferenceEquals(hands.heldItem, __instance))
            {
                // We hold it and a held broadcast for it arrived: another player grabbed the same item. Newest grab wins: held longer
                // than stealGraceTime we yield (and fall through to apply their pose), otherwise we keep it.
                bool yield = incomingHeld
                             && Time.time - hands.heldItemGrabTime > stealGraceTime;
                if (yield)
                    hands.RelinquishHeldItem();
                else
                    return false; // keep authority — our own code drives it
            }

            // Remember remote holds for the newest-grab-wins check above. Cleared on real motion, a drop or Done.
            int netId = __instance.GetNetId();
            if (incomingHeld)
            {
                remoteHeldUntil[netId] = Time.time + remoteHeldGrace;

                // If we dropped this item and still own its ItemPositionSyncer, the syncer would echo the new holder's pose back with
                // zero velocity and Done == false. That looks like a "held" broadcast, so the new holder would yield to us after
                // stealGraceTime. We aren't the authority for an item someone else holds, so our syncer is destroyed.
                ItemPositionSyncer syncer = __instance.GetComponent<ItemPositionSyncer>();
                if (syncer != null)
                    UnityEngine.Object.Destroy(syncer);
            }
            else
                remoteHeldUntil.Remove(netId);

            // A leftover local rigidbody must not fall between the remote holder's pose updates. Gravity comes back on motion, drop or Done.
            Rigidbody body = __instance.RigidBody;
            if (body != null && !body.isKinematic)
            {
                body.useGravity = !incomingHeld;
                // Held: we return false below, so FIKA's own velocity-zeroing is skipped. Zero it here or leftover velocity drifts
                // the body between the smoother's writes.
                if (incomingHeld)
                    body.velocity = Vector3.zero;
            }

            // A held item goes through the per-frame smoother instead of FIKA's snap. Motion, drop and Done packets remove the
            // smoother below so FIKA's physics sync takes over again.
            if (incomingHeld)
            {
                LootSyncSmoother sm = __instance.GetComponent<LootSyncSmoother>()
                                      ?? __instance.gameObject.AddComponent<LootSyncSmoother>();
                sm.SetTarget(packetCopy.Position, packetCopy.Rotation);
                return false; // the smoother lerps the transform; skip FIKA's hard SetPositionAndRotation
            }

            LootSyncSmoother stale = __instance.GetComponent<LootSyncSmoother>();
            if (stale != null)
                UnityEngine.Object.Destroy(stale);

            return true; // let FIKA apply the pose/velocity as usual
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
        [HarmonyPatch(typeof(TarkovApplication), "ShowSessionResult")]
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
            if (UIPatches.stancePanel != null)
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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Main.FreeCamera.FreeCameraController), "Update")]
        private static bool PositionExitRaidUIAndCam(Fika.Core.Main.FreeCamera.FreeCameraController __instance)
        {
            if (__instance._extracted)
            {
                if (__instance._cameraParent != null && VRGlobals.VRCam.transform.parent == null)
                {
                    VRGlobals.VRCam.transform.parent = __instance._cameraParent.transform;
                }
                PreloaderUI.Instance.transform.position = VRGlobals.VRCam.transform.position + (VRGlobals.VRCam.transform.forward * 0.6f) + (VRGlobals.VRCam.transform.up * 0.2f);
                PreloaderUI.Instance.transform.rotation = Quaternion.Euler(0, VRGlobals.VRCam.transform.eulerAngles.y, VRGlobals.VRCam.transform.eulerAngles.z);
                if (Mathf.Abs(VRInput.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).y) > VRSettings.GetLeftStickSensitivity())
                {
                    __instance._cameraParent.transform.position += __instance._cameraParent.transform.forward * (VRInput.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).y / 10);
                }
                if (Mathf.Abs(VRInput.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).x) > VRSettings.GetLeftStickSensitivity())
                {
                    __instance._cameraParent.transform.position += __instance._cameraParent.transform.right * (VRInput.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).x / 10);
                }
                if (Mathf.Abs(VRInput.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y) > Mathf.Abs(VRInput.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).x))
                {
                    __instance._cameraParent.transform.position += new Vector3(0, VRInput.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y / 10, 0);
                }
                else if (Mathf.Abs(VRInput.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).x) > Mathf.Abs(VRInput.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y))
                {
                    __instance._cameraParent.transform.rotation = Quaternion.Euler(0, __instance._cameraParent.transform.eulerAngles.y + (VRInput.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).x * 6), 0);
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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Fika.Core.Main.Components.CoopHandler), "ProcessQuitting")]
        private static bool OverrideExitRaidButton(Fika.Core.Main.Components.CoopHandler __instance)
        {
            EQuitState quitState = __instance.QuitState;
            // B for right-handed, Y for left-handed
            bool vrExtractPressed = Handedness.GunHandUpperButton.stateDown;

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
                NotificationManager.DisplayWarningNotification(LocalizationExtensions.Localized("F_Client_HostCannotExtract"));
                __instance._requestQuitGame = false;
                return false;
            }

            bool recentDisconnect = fikaServer.TimeSinceLastPeerDisconnected > DateTime.Now.AddSeconds(-5.0);
            if (fikaServer.HasHadPeer && recentDisconnect)
            {
                NotificationManager.DisplayWarningNotification(LocalizationExtensions.Localized("F_Client_Wait5Seconds"));
                __instance._requestQuitGame = false;
                return false;
            }

            localGameInstance.Stop(__instance.MyPlayer.ProfileId, localGameInstance.ExitStatus, exitName);
            return false;
        }
    }

    // Per-frame interpolator for a loose item held by another player. FIKASupport's ApplyNetPacket prefix stores the latest network
    // pose here and skips FIKA's snap; this eases the transform toward it (1 - e^(-rate·dt)) between the ~25 Hz packets. The first
    // packet snaps, so a pickup doesn't ease in from the resting spot. Removed on a motion, drop or Done packet, or when we grab the
    // item (RemoveFikaSyncer), so it never fights another driver.
    internal class LootSyncSmoother : MonoBehaviour
    {
        private Vector3 _targetPos;
        private Quaternion _targetRot;
        private bool _hasTarget;
        private float _lastPacket;

        public void SetTarget(Vector3 pos, Quaternion rot)
        {
            _targetPos = pos;
            _targetRot = rot;
            _lastPacket = Time.time;
            if (!_hasTarget)
            {
                _hasTarget = true;
                transform.SetPositionAndRotation(pos, rot); // snap on first packet
            }
        }

        private void Update()
        {
            if (!_hasTarget)
                return;
            // Holder went quiet (lag/disconnect) — stop lerping and leave it at the last pose.
            if (Time.time - _lastPacket > FIKASupport.lootSmoothStaleTimeout)
            {
                _hasTarget = false;
                return;
            }
            float t = 1f - Mathf.Exp(-FIKASupport.lootSmoothRate * Time.deltaTime);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, _targetPos, t),
                Quaternion.Slerp(transform.rotation, _targetRot, t));
        }
    }

}
