using System.Runtime.CompilerServices;
using EFT;
using Fika.Core.Main.Players;
using SptVrFikaSync;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.ModSupport.FIKA
{
    // Send side of VR arm sync: reads the local VR rig hands and feeds body-relative poses (Player.Transform) to the FikaSync module
    // (SPT-VR-FikaSync.dll), which owns the packet, registration, relay and rendering, so a flatscreen/headless peer
    // with only the module can render too. Apply-side tuning lives on the module's ArmSyncApply.
    // FikaVrSync.enableArmSync is the master gate (send + apply).
    internal static class VRArmSync
    {
        public static bool enableArmSync = true;     // send empty-hand arm poses
        public static bool sendWeaponPose = true;     // send the held gun's pose while armed
        public static bool sendEatingPose = true;     // send manual-eating wrist+prop poses
        public static bool sendFingerPoses = true;    // send finger poses (authored open-hand / Index curls)
        public static bool sendReloadState = true;    // send the manual reload (hands on parts, magazines, bolt / handle) with the weapon pose
        public static float sendInterval = 0.05f;     // ~20/s
        public static float fingerSendInterval = 0.05f; // finger cadence (own timer - the shared throttle belongs to the arm/weapon packets)
        public static bool sendHeadPose = true;       // the headset in the body's frame: the observers' upper body and head follow it

        private static float lastSendTime;
        private static float lastHeadSend;
        private static bool headSyncBroken;           // SPT-VR-FikaSync older than 2.3.0: no head packet to send
        private static float lastFingerSend;
        private static bool fingersWereActive;
        private static bool wasEating;
        private static bool wasSendingReload;
        private static bool loggedReloadBuildError;

        // Called every frame from VRPlayerManager.LateUpdate (guarded by InstalledMods.FIKAInstalled).
        public static void Tick()
        {
            if (!VRGlobals.inGame)
                return;

            // NetId is a FikaPlayer member (not on EFT.Player); the local human player is a FikaPlayer.
            FikaPlayer local = VRGlobals.player as FikaPlayer;

            // Eating stop (once, before the throttle below) so observers restore the food model the moment the eat ends
            bool eatingNow = sendEatingPose && Source.Player.Interactions.EatingInteractionController.ManualActive;
            if (wasEating && !eatingNow)
            {
                if (local != null) FikaVrSync.SendEatingStop(local.NetId);
                wasEating = false;
            }

            // Reload state stop (once, not throttled): no gun in hands, or sprinting with it (the weapon pose isn't sent then either),
            // so observers put their copy of the gun back the moment it ends
            // On a mounted gun the observers' copy of you is Tarkov's own (FIKA syncs taking it and its aim): nothing of ours is sent
            bool onMountedGun = Source.Player.Interactions.MountedGun.Active;
            bool reloadNow = sendReloadState && local != null && local.HandsController is Player.FirearmController && !local.IsSprintEnabled && !onMountedGun;
            if (wasSendingReload && !reloadNow)
            {
                if (local != null) FikaVrSync.SendReloadStop(local.NetId);
                wasSendingReload = false;
            }

            // Finger poses: own cadence, sent while either hand's pose system is driving (buffered by
            // HandsInteractionController.SampleFingerSync at the end of its LateUpdate), plus one all-inactive packet on the
            // falling edge so observers release immediately (the receiver's staleTimeout is only the lost-packet fallback).
            if (sendFingerPoses && local != null)
            {
                bool fingersActive = Source.Player.Interactions.HandsInteractionController.syncLeftFingersActive
                                  || Source.Player.Interactions.HandsInteractionController.syncRightFingersActive;
                if ((fingersActive || fingersWereActive) && Time.time - lastFingerSend >= fingerSendInterval)
                {
                    lastFingerSend = Time.time;
                    FikaVrSync.SendFingerPose(local.NetId,
                        Source.Player.Interactions.HandsInteractionController.syncLeftFingersActive,
                        Source.Player.Interactions.HandsInteractionController.syncLeftFingerEuler,
                        Source.Player.Interactions.HandsInteractionController.syncRightFingersActive,
                        Source.Player.Interactions.HandsInteractionController.syncRightFingerEuler);
                    fingersWereActive = fingersActive;
                }
            }

            // The head: its own cadence, whatever the hands hold. Its own method: with an older module the missing method throws there,
            // not in this one (the arms would go with it).
            if (sendHeadPose && !headSyncBroken && local != null && Time.time - lastHeadSend >= sendInterval)
            {
                lastHeadSend = Time.time;
                try
                {
                    SendHead(local, onMountedGun);
                }
                catch (System.Exception exception)
                {
                    headSyncBroken = true;
                    Plugin.MyLog.LogError($"[VRArmSync] no head sync with this SPT-VR-FikaSync.dll (2.3.0 or newer needed): {exception.Message}");
                }
            }

            if (Time.time - lastSendTime < sendInterval || onMountedGun)
                return;

            VRPlayerManager rig = VRGlobals.vrPlayer;
            if (local == null || rig == null)
                return;
            // Everything in the body's frame (Player.Transform), like the headset: the observers place it on their copy's root, which FIKA
            // keeps where yours is. Not the chest: theirs is solved again from the head on their machine and never quite matches, and
            // every degree it's off turned the gun that much off your aim.
            Transform frame = local.Transform != null ? local.Transform.Original : null;
            if (frame == null)
                return;

            // Manual eating: stream the rendered wrist poses (the module drives the observed arms like empty hands) + every live
            // food prop (the module overrides them so the food rides the synced hands).
            if (eatingNow && local.HandsController is Player.MedsController)
            {
                if (Source.Player.Interactions.EatingInteractionController.TryBuildSync(
                        frame, out Vector3 elPos, out Quaternion elRot, out Vector3 erPos, out Quaternion erRot))
                {
                    lastSendTime = Time.time;
                    wasEating = true;
                    var ec = Source.Player.Interactions.EatingInteractionController.syncNames;
                    FikaVrSync.SendEatingPose(local.NetId, elPos, elRot, erPos, erRot,
                        Source.Player.Interactions.EatingInteractionController.syncAnimHashes,
                        Source.Player.Interactions.EatingInteractionController.syncAnimTimes,
                        ec,
                        Source.Player.Interactions.EatingInteractionController.syncPos,
                        Source.Player.Interactions.EatingInteractionController.syncRot,
                        Source.Player.Interactions.EatingInteractionController.syncVis,
                        ec.Count);
                }
                return;
            }

            DispatchHeldOrEmpty(local, rig, frame);
        }

        // Forward one eat sound the local eater played (by event name) so observers replay it. Lives here so FIKA types only
        // JIT under InstalledMods.FIKAInstalled (the caller, the OnSound gate patch, is FIKA-gated).
        public static void SendEatSound(string name)
        {
            FikaPlayer local = VRGlobals.player as FikaPlayer;
            if (local != null) FikaVrSync.SendEatingSound(local.NetId, name);
        }

        // One reload sound the manual reload played (VRReloadController.PlayFelt), by event name, for the other players' copy of the
        // gun. Its caller is FIKAInstalled-gated and this doesn't inline into it, so FIKA types only JIT with FIKA.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendReloadSound(string name)
        {
            FikaPlayer local = VRGlobals.player as FikaPlayer;
            if (local != null && sendReloadState) FikaVrSync.SendReloadSound(local.NetId, name);
        }

        // FIKA's own "cycle the bolt" packet for a bolt-action shot: the bolt is worked by hand (BoltActionPatches.BoltStaysShut), so
        // the fire operation that would send it never runs. The receiver (ObservedFirearmController.HandleObservedBoltAction) chambers
        // the next round on their copy of you; the sync module's BoltCycleSync does that without the cycle animation.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendBoltCycle(Player.FirearmController controller)
        {
            if (controller is Fika.Core.Main.ClientClasses.HandsControllers.FikaClientFirearmController fikaController)
                fikaController.SendBoltActionReloadPacket();
        }

        // The headset in the body's frame (Player.Transform), rotation-only like the observers' placing of it
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendHead(FikaPlayer local, bool onMountedGun)
        {
            Transform root = local.Transform != null ? local.Transform.Original : null;
            Camera headset = VRGlobals.VRCam;
            if (root == null || headset == null)
                return;
            Quaternion rootInverse = Quaternion.Inverse(root.rotation);
            bool alive = local.HealthController != null && local.HealthController.IsAlive;
            FikaVrSync.SendHeadPose(local.NetId, alive && !VRGlobals.menuOpen && !onMountedGun,
                rootInverse * (headset.transform.position - root.position), rootInverse * headset.transform.rotation);
        }

        private static void DispatchHeldOrEmpty(FikaPlayer local, VRPlayerManager rig, Transform frame)
        {
            var hc = local.HandsController;
            bool isFirearm = hc is Player.FirearmController;
            bool isHeldItem = isFirearm || hc is Player.KnifeController
                || hc is Player.GrenadeHandsController || hc is Player.QuickGrenadeThrowHandsController;

            // Held items (gun/knife/grenade): stream the item's WeaponRoot pose (the module overrides the observed item to it, then
            // re-IKs the hands). WeaponRoot is on the base AbstractHandsController, so one path covers all three. The gun is
            // sprint-gated (a welded gun looks bad running); melee/grenade sync while running. leftOnGrip only applies to the gun.
            if (sendWeaponPose && isHeldItem && hc.WeaponRoot != null)
            {
                if (isFirearm && local.IsSprintEnabled)
                    return;
                lastSendTime = Time.time;
                Transform weaponRoot = hc.WeaponRoot;
                Vector3 wPos = frame.InverseTransformPoint(weaponRoot.position);
                Quaternion wRot = Quaternion.Inverse(frame.rotation) * weaponRoot.rotation;
                bool leftOnGrip = isFirearm && rig.isSupporting;
                Vector3 lPos = Vector3.zero; Quaternion lRot = Quaternion.identity;
                if (rig.LeftHand != null)
                {
                    lPos = frame.InverseTransformPoint(rig.LeftHand.transform.position);
                    lRot = Quaternion.Inverse(frame.rotation) * rig.LeftHand.transform.rotation;
                }
                FikaVrSync.SendWeaponPose(local.NetId, wPos, wRot, leftOnGrip, lPos, lRot);
                // The manual reload on top (the module draws it on the observed gun the pose above just placed). A bad frame is
                // logged once and never breaks this LateUpdate.
                bool reloadBuilt = false;
                if (isFirearm && sendReloadState)
                {
                    try { reloadBuilt = VRReloadController.TryBuildSync((Player.FirearmController)hc); }
                    catch (System.Exception exception)
                    {
                        if (!loggedReloadBuildError)
                        {
                            loggedReloadBuildError = true;
                            Plugin.MyLog.LogError($"[VRArmSync] reload state not built: {exception}");
                        }
                    }
                }
                if (reloadBuilt)
                {
                    FikaVrSync.SendReloadState(local.NetId,
                        VRReloadController.syncManualReload, VRReloadController.syncManualBolt,
                        VRReloadController.syncDriveLeft, VRReloadController.syncDriveRight,
                        VRReloadController.syncLeftPosition, VRReloadController.syncLeftRotation,
                        VRReloadController.syncRightPosition, VRReloadController.syncRightRotation,
                        VRReloadController.syncPropItems, VRReloadController.syncPropPositions, VRReloadController.syncPropRotations,
                        VRReloadController.syncPartPaths, VRReloadController.syncPartPositions, VRReloadController.syncPartRotations,
                        VRReloadController.syncPartShown, VRReloadController.syncHiddenGroundItem,
                        VRReloadController.syncBoltCatch, VRReloadController.syncBoltOpenDriven, VRReloadController.syncBoltOpen,
                        VRReloadController.syncShellsThrown, VRReloadController.syncRoundsShown);
                    wasSendingReload = true;
                }
            }
            // Empty hands: stream both controller hand poses (the module drives the arm IK). Everything else falls back to FIKA's
            // vanilla anim.
            else if (enableArmSync && hc is Player.EmptyHandsController)
            {
                if (rig.LeftHand == null || rig.RightHand == null)
                    return;
                lastSendTime = Time.time;
                Vector3 lPos = frame.InverseTransformPoint(rig.LeftHand.transform.position);
                Quaternion lRot = Quaternion.Inverse(frame.rotation) * rig.LeftHand.transform.rotation;
                Vector3 rPos = frame.InverseTransformPoint(rig.RightHand.transform.position);
                Quaternion rRot = Quaternion.Inverse(frame.rotation) * rig.RightHand.transform.rotation;
                FikaVrSync.SendArmPose(local.NetId, lPos, lRot, rPos, rRot);
            }
        }
    }
}
