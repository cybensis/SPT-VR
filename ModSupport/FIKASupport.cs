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
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using TarkovVR.Source.Player.Interactions;
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
using EFT.Interactive;
using Fika.Core.Main.Components;
using Fika.Core.Main.HostClasses;
using Fika.Core.Main.ClientClasses;
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

        // Safety net for the loose-loot grab/summon/take: those reparent items, make them
        // kinematic, or remove them into the inventory while FIKA's ItemPositionSyncer is still
        // tracking them, so NotifyDone can null-ref on an item that's no longer where/what it
        // expects (HandsInteractionController.RemoveFikaSyncer strips the syncer on grab, but a
        // syncer added/active on another path would still spam). The sync is moot for an item
        // we've taken control of — swallow that NRE (only) so it doesn't spam every FixedUpdate.
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Fika.Core.Main.Components.ItemPositionSyncer), "NotifyDone")]
        private static Exception SilenceItemSyncerNotifyDone(Exception __exception)
        {
            return __exception is NullReferenceException ? null : __exception;
        }

        // Same syncer, same cause from the other end: ItemPositionSyncer.Start() NREs when it's
        // created on an item whose rigidbody/state isn't valid yet (a grabbed/summoned loose item we
        // re-sync on drop, or FIKA's own ThrowItem path racing our reparent). The component just
        // fails to initialize (the drop won't network-sync that item) -- harmless, but it spams the
        // log every time. Swallow ONLY the NRE so a genuine Start error still surfaces.
        [HarmonyFinalizer]
        [HarmonyPatch(typeof(Fika.Core.Main.Components.ItemPositionSyncer), "Start")]
        private static Exception SilenceItemSyncerStart(Exception __exception)
        {
            return __exception is NullReferenceException ? null : __exception;
        }

        // ===== Loose-loot position sync for the VR loot pointer =====
        // FIKA only syncs items the LOCAL player throws (ItemPositionSyncer is created in the
        // ThrowItem patch), and that syncer self-destroys once the item settles. A resting item
        // we grab with the loot pointer has none, so other players don't see it move. These two
        // helpers reuse FIKA's exact mechanism: SyncHeldLootItem pushes the same LootSyncStruct
        // the syncer's FixedUpdate sends (receivers apply it via ObservedLootItem.ApplyNetPacket),
        // and ResyncDroppedItem re-creates FIKA's syncer on release so the physics drop syncs
        // natively. Both no-op for items that aren't networked (ObservedLootItem) — e.g. solo.
        // Only ever called when InstalledMods.FIKAInstalled, so they don't JIT without FIKA.

        public static void SyncHeldLootItem(LootItem item)
        {
            if (item == null || !(item is ObservedLootItem))
                return;

            LootSyncStruct data = default;
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
            // ItemPositionSyncer.Start requires a live rigidbody (it reads velocity each tick
            // until the item settles, then sends the final Done position).
            if (item.RigidBody == null)
                return;
            ItemPositionSyncer.Create(item.gameObject, FikaBackendUtils.IsServer, observed);
        }

        // ===== Receiver-side: don't let a LOCAL rigidbody fight a remote player's hold =====
        // ObservedLootItem.ApplyNetPacket hard-sets transform + velocity from each network packet.
        // For a FRESH world item that has no rigidbody this is a clean teleport, so a remote
        // player grabbing an item looks perfect. But once WE have grabbed-then-dropped an item,
        // OUR machine left a non-kinematic rigidbody on it (from DropObject). When the OTHER
        // player then grabs it, they broadcast their hand pose at ~25 Hz while our rigidbody keeps
        // applying gravity at 50 Hz between those packets — the item visibly "fights"/stutters
        // between their hand and falling. (This is exactly the after-a-hand-off symptom.)
        //
        // FIKA tags a HELD item with zero velocity + Done==false (see SyncHeldLootItem); a thrown
        // or settling item carries real velocity. So: while a remote hold is being applied, turn
        // OUR rigidbody's gravity OFF so it sits exactly where their packets put it; restore it
        // the moment real motion (a throw/drop) or a Done arrives, so the physics drop still
        // simulates in sync. And if WE are the one physically holding the item, skip the apply
        // entirely so a stale/echoing syncer on another client can never disturb our own hold.
        //
        // How a grab of an item ANOTHER player is already holding resolves: newest grab wins —
        // the new grabber steals it and the previous holder yields (no dual-ownership).

        // NetId -> Time.time after which the item stops counting as remote-held. Populated from
        // ApplyNetPacket whenever we receive ANOTHER player's "held" broadcast (Done=false + zero
        // velocity, i.e. SyncHeldLootItem). Our own held broadcasts never loop back to us, so any
        // held packet we receive for an item is necessarily from another player.
        private static readonly Dictionary<int, float> remoteHeldUntil = new Dictionary<int, float>();
        // Holders broadcast at ~25/s (lootSyncInterval 0.04), so ~0.3s = ~7 missed packets before
        // we treat the item as free again (covers a dropped packet without a long false positive).
        public static float remoteHeldGrace = 0.3f;
        // Steal mode: a player who has held the contested item longer than this yields to the more
        // recent grabber. Keep it well above the ~80ms a hand-off takes but below a realistic gap
        // between two deliberate grabs, so back-and-forth steals each resolve to the newest grab.
        // (The leading "Steal mode" framing predates the removal of an alternative "Block" mode;
        // stealing is now the only behavior.)
        public static float stealGraceTime = 0.4f;

        // Smoothing for RECEIVED held-item motion. A held item is position-synced at ~25 Hz; hard-
        // snapping each packet looks choppy on observers. When on, a HELD item is driven through a
        // per-frame exponential interpolator (the SAME smoothing the IK arm sync uses) instead of
        // FIKA's hard SetPositionAndRotation. Only held items are smoothed; thrown/dropped/settling
        // items keep FIKA's physics-driven sync (velocity already interpolates those). false = snap.
        public static bool lootSyncSmoothing = true;
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
        private static bool NeutralizeRemoteHeldLootPhysics(ObservedLootItem __instance, LootSyncStruct packet)
        {
            // Copy to a local so the Harmony analyzer doesn't mistake the struct-field reads
            // for writes to a by-value patch parameter.
            LootSyncStruct p = packet;
            bool incomingHeld = !p.Done
                                && p.Velocity.sqrMagnitude < 1e-6f
                                && p.AngularVelocity.sqrMagnitude < 1e-6f;

            var hic = VRGlobals.handsInteractionController;
            if (hic != null && ReferenceEquals(hic.heldItem, __instance))
            {
                // We're holding it locally AND a held broadcast for it just arrived — another
                // player has grabbed the same item. Newest grab wins: if we grabbed it more than
                // stealGraceTime ago we yield (hand authority to them and fall through to apply
                // their pose); the more-recent grabber keeps it. Otherwise we keep it.
                bool yield = incomingHeld
                             && Time.time - hic.heldItemGrabTime > stealGraceTime;
                if (yield)
                    hic.RelinquishHeldItem();
                else
                    return false; // keep authority — our own code drives it
            }

            // Track who's being held remotely (cleared the moment real motion or a drop/Done
            // arrives) so the steal arbitration above can resolve a contested grab.
            int netId = __instance.GetNetId();
            if (incomingHeld)
            {
                remoteHeldUntil[netId] = Time.time + remoteHeldGrace;

                // BUG FIX: if WE dropped this item and still own its ItemPositionSyncer, that syncer
                // keeps broadcasting the item's pose. Once another player GRABS the item, our syncer
                // echoes their pose back at vel≈0/Done=false — byte-identical to a "held" broadcast —
                // which trips the steal arbitration on the new holder: after stealGraceTime they
                // think someone grabbed it from them and RelinquishHeldItem() → the item "detaches
                // and floats" a second or two after pickup. We're no longer the authority for an
                // item someone else is holding, so kill our syncer the instant we see their hold.
                ItemPositionSyncer syncer = __instance.GetComponent<ItemPositionSyncer>();
                if (syncer != null)
                    UnityEngine.Object.Destroy(syncer);
            }
            else
                remoteHeldUntil.Remove(netId);

            // Don't let a leftover local rigidbody (from when WE last dropped/held it) fall between
            // the remote holder's pose updates and fight them; restore gravity on motion/drop/Done.
            Rigidbody rb = __instance.RigidBody;
            if (rb != null && !rb.isKinematic)
            {
                rb.useGravity = !incomingHeld;
                // For a held item the smoother (below) drives the transform and FIKA's own
                // velocity-zeroing is skipped (we return false), so zero it here to stop any
                // leftover velocity drifting the body between the smoother's per-frame writes.
                if (incomingHeld)
                    rb.velocity = Vector3.zero;
            }

            // SMOOTHING: drive a HELD item through a per-frame interpolator (same exp smoothing as
            // the IK arm sync) instead of FIKA's hard 25 Hz snap. Motion/drop/Done packets tear the
            // smoother down below so FIKA's native physics sync resumes for the throw/settle.
            if (lootSyncSmoothing && incomingHeld)
            {
                LootSyncSmoother sm = __instance.GetComponent<LootSyncSmoother>()
                                      ?? __instance.gameObject.AddComponent<LootSyncSmoother>();
                sm.SetTarget(p.Position, p.Rotation);
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
    }

    // Per-frame interpolator for a remotely-HELD loose item. FIKASupport.ApplyNetPacket stores the
    // latest network pose here and skips FIKA's hard snap; this lerps the transform toward it every
    // frame so the item glides between the ~25 Hz packets instead of stepping. Same exponential
    // smoothing the IK arm sync uses (1 - e^(-rate·dt)); snaps on the first packet so a pickup
    // doesn't ease in from the resting spot. Torn down (FIKASupport) the moment a motion/drop/Done
    // packet arrives, or when WE grab the item (RemoveFikaSyncer), so it never fights another driver.
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
                transform.SetPositionAndRotation(pos, rot); // snap on first packet (like the IK displayed init)
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
