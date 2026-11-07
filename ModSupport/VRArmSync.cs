using System.Runtime.CompilerServices;
using EFT;
using Fika.Core.Main.Players;
using SptVrFikaSync;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.ModSupport.FIKA
{
    // SEND side of VR arm sync. Reads the local VR rig hands and feeds chest-relative poses to the
    // FikaSync module (SPT-VR-FikaSync.dll), which owns the packet, registration, relay and rendering.
    // Only the send lives here because it needs the VR rig; everything network/render-side is in the
    // module so a flatscreen/headless peer (which has only the module) can register + render too.
    //
    // Apply-side tuning (IK weight, hand-rotation offsets, only-when-empty) now lives on the module's
    // ArmSyncApply. FikaVrSync.enableArmSync is the master gate (send + apply).
    internal static class VRArmSync
    {
        public static bool enableArmSync = true;     // send empty-hand arm poses
        public static bool sendWeaponPose = true;     // send the held gun's pose while armed
        public static bool sendEatingPose = true;     // send manual-eating wrist+prop poses
        public static bool sendFingerPoses = true;    // send finger poses (authored open-hand / Index curls)
        public static bool sendReloadState = true;    // send the manual reload (hands on parts, magazines, bolt / handle) with the weapon pose
        public static float sendInterval = 0.05f;     // ~20/s
        public static float fingerSendInterval = 0.05f; // finger cadence (own timer - the shared throttle belongs to the arm/weapon packets)

        private static float lastSendTime;
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

            // Eating STOP transition (fire once, NOT throttled) so observers restore the food model
            // and stop overriding the moment the eat ends — before the throttle gate below.
            bool eatingNow = sendEatingPose && Source.Player.Interactions.EatingInteractionController.ManualActive;
            if (wasEating && !eatingNow)
            {
                if (local != null) FikaVrSync.SendEatingStop(local.NetId);
                wasEating = false;
            }

            // Reload state STOP (once, not throttled): no gun in hands any more, or running with it (the weapon pose isn't sent then
            // either), so observers put their copy of the gun back the moment it ends
            bool reloadNow = sendReloadState && local != null && local.HandsController is Player.FirearmController && !local.IsSprintEnabled;
            if (wasSendingReload && !reloadNow)
            {
                if (local != null) FikaVrSync.SendReloadStop(local.NetId);
                wasSendingReload = false;
            }

            // Finger poses: independent cadence, sent while either hand's pose system is driving
            // (buffered by HandsInteractionController.SampleFingerSync at the end of its LateUpdate),
            // plus ONE final all-inactive packet on the falling edge so observers release immediately
            // (the receiver's staleTimeout is only the lost-packet fallback).
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

            if (Time.time - lastSendTime < sendInterval)
                return;

            VRPlayerManager rig = VRGlobals.vrPlayer;
            if (local == null || rig == null)
                return;
            Transform chest = local.PlayerBones?.Ribcage?.Original;
            if (chest == null)
                return;

            // Manual eating: stream the rendered wrist poses (the module drives the observed arms like
            // empty hands) + every live food prop (the module re-finds + overrides them so the food
            // rides the synced hands). hc is a MedsController here, so it falls outside the gun/empty
            // branches below.
            if (eatingNow && local.HandsController is Player.MedsController)
            {
                if (Source.Player.Interactions.EatingInteractionController.TryBuildSync(
                        chest, out Vector3 elPos, out Quaternion elRot, out Vector3 erPos, out Quaternion erRot))
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

            DispatchHeldOrEmpty(local, rig, chest);
        }

        // Forward one eat sound the local eater played (by event name) so observers replay it. Lives
        // here (FIKA-namespaced) so it only JITs under InstalledMods.FIKAInstalled — the caller (the
        // OnSound gate patch) is FIKA-gated. NetId comes off the local FikaPlayer.
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

        // FIKA's own "cycle the bolt" packet for a bolt-action shot (BoltActionPatches.BoltStaysShut: the bolt is worked by hand, and
        // FIKA would only send it from the fire operation that skip ends). Its other side (ObservedFirearmController.
        // HandleObservedBoltAction) moves the next round into the chamber on their copy of you; the sync module's BoltCycleSync does
        // that without the cycle animation.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendBoltCycle(Player.FirearmController controller)
        {
            if (controller is Fika.Core.Main.ClientClasses.HandsControllers.FikaClientFirearmController fikaController)
                fikaController.SendBoltActionReloadPacket();
        }

        private static void DispatchHeldOrEmpty(FikaPlayer local, VRPlayerManager rig, Transform chest)
        {
            var hc = local.HandsController;
            bool isFirearm = hc is Player.FirearmController;
            bool isHeldItem = isFirearm || hc is Player.KnifeController
                || hc is Player.GrenadeHandsController || hc is Player.QuickGrenadeThrowHandsController;

            // Held items (gun/knife/grenade): stream the item's WeaponRoot pose (the module overrides the
            // observed item to it so it follows the controller, then re-IKs the hands). WeaponRoot is on
            // the base AbstractHandsController, so the same path covers all three. The GUN is sprint-
            // gated (a welded gun looks bad running); melee/grenade sync while running. Only the gun has
            // a foregrip, so leftOnGrip only applies there.
            if (sendWeaponPose && isHeldItem && hc.WeaponRoot != null)
            {
                if (isFirearm && local.IsSprintEnabled)
                    return;
                lastSendTime = Time.time;
                Transform wr = hc.WeaponRoot;
                Vector3 wPos = chest.InverseTransformPoint(wr.position);
                Quaternion wRot = Quaternion.Inverse(chest.rotation) * wr.rotation;
                bool leftOnGrip = isFirearm && rig.isSupporting;
                Vector3 lPos = Vector3.zero; Quaternion lRot = Quaternion.identity;
                if (rig.LeftHand != null)
                {
                    lPos = chest.InverseTransformPoint(rig.LeftHand.transform.position);
                    lRot = Quaternion.Inverse(chest.rotation) * rig.LeftHand.transform.rotation;
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
            // Empty hands: stream both controller hand poses (the module drives the arm IK). (Eating/meds
            // is handled by its own branch above; everything else falls back to FIKA's vanilla anim.)
            else if (enableArmSync && hc is Player.EmptyHandsController)
            {
                if (rig.LeftHand == null || rig.RightHand == null)
                    return;
                lastSendTime = Time.time;
                Vector3 lPos = chest.InverseTransformPoint(rig.LeftHand.transform.position);
                Quaternion lRot = Quaternion.Inverse(chest.rotation) * rig.LeftHand.transform.rotation;
                Vector3 rPos = chest.InverseTransformPoint(rig.RightHand.transform.position);
                Quaternion rRot = Quaternion.Inverse(chest.rotation) * rig.RightHand.transform.rotation;
                FikaVrSync.SendArmPose(local.NetId, lPos, lRot, rPos, rRot);
            }
        }
    }
}
