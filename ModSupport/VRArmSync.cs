using System.Runtime.CompilerServices;
using EFT;
using Fika.Core.Main.Players;
using SptVrFikaSync;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.ModSupport.FIKA
{
    // Send side of the VR arm sync. Reads the local VR rig's hands and passes their poses, relative to the body (Player.Transform), to the
    // FikaSync module (SPT-VR-FikaSync.dll). The module owns the packets, relay and rendering, so a flatscreen or headless peer with only
    // the module installed renders them too. Receive-side tuning is on the module's ArmSyncApply; FikaVrSync.enableArmSync gates both sides.
    internal static class VRArmSync
    {
        public static float sendInterval = 0.05f;     // ~20/s
        public static float fingerSendInterval = 0.05f; // s, finger packets have their own timer (the shared one is for arm/weapon packets)

        private static float lastSendTime;
        private static float lastHeadSend;
        private static bool headSyncBroken;           // set when SPT-VR-FikaSync is older than 2.3.0 (it has no head packet)
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

            // Send the eating stop once, before the throttle below, so observers restore the food model as soon as the eat ends.
            bool eatingNow = Source.Player.Interactions.EatingInteractionController.ManualActive;
            if (wasEating && !eatingNow)
            {
                if (local != null) FikaVrSync.SendEatingStop(local.NetId);
                wasEating = false;
            }

            // Send the reload stop once, not throttled, when there is no gun in the hands or you sprint with it (the weapon pose isn't
            // sent then either), so observers put their copy of the gun back at once.
            // On a mounted gun nothing of ours is sent: FIKA already syncs mounting the gun and its aim.
            bool onMountedGun = Source.Player.Interactions.MountedGun.Active;
            bool reloadNow = local != null && local.HandsController is Player.FirearmController && !local.IsSprintEnabled && !onMountedGun;
            if (wasSendingReload && !reloadNow)
            {
                if (local != null) FikaVrSync.SendReloadStop(local.NetId);
                wasSendingReload = false;
            }

            // Finger poses go on their own timer while either hand's fingers are posed by us (buffered by
            // HandsInteractionController.SampleFingerSync at the end of its LateUpdate). One all-inactive packet follows when both stop,
            // so observers release the fingers at once; the receiver's staleTimeout only covers lost packets.
            if (local != null)
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

            // The head is sent on its own timer, whatever the hands hold. It has its own method so that with an older module the missing
            // method throws there and not in Tick, which would stop the arm sync too.
            if (!headSyncBroken && local != null && Time.time - lastHeadSend >= sendInterval)
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
            // All poses are sent in the body's frame (Player.Transform), like the headset. Observers place them on their copy's root,
            // which FIKA keeps where yours is. Not the chest: the observer solves its chest again from the head and never matches yours
            // exactly, and every degree of difference turns the gun that far off your aim.
            Transform frame = local.Transform != null ? local.Transform.Original : null;
            if (frame == null)
                return;

            // Manual eating: send the rendered wrist poses (the module drives the observed arms like empty hands) and every live food
            // prop (the module overrides them so the food follows the synced hands).
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

        // Forwards one eating sound (by event name) so observers replay it. It lives here so FIKA types are only JIT-compiled when
        // FIKA is installed (the caller, the OnSound gate patch, checks InstalledMods.FIKAInstalled).
        public static void SendEatSound(string name)
        {
            FikaPlayer local = VRGlobals.player as FikaPlayer;
            if (local != null) FikaVrSync.SendEatingSound(local.NetId, name);
        }

        // Forwards one sound of the manual reload (VRReloadController.PlayFelt), by event name, to the other players' copy of the gun.
        // The caller checks FIKAInstalled and this method is not inlined into it, so FIKA types are only JIT-compiled with FIKA.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendReloadSound(string name)
        {
            FikaPlayer local = VRGlobals.player as FikaPlayer;
            if (local != null) FikaVrSync.SendReloadSound(local.NetId, name);
        }

        // Sends FIKA's own "cycle the bolt" packet after a bolt-action shot. The bolt is worked by hand (BoltActionPatches.BoltStaysShut),
        // so the fire operation that would send it never runs. The receiver (ObservedFirearmController.HandleObservedBoltAction) chambers
        // the next round on their copy of you; the sync module's BoltCycleSync does that without the cycle animation.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void SendBoltCycle(Player.FirearmController controller)
        {
            if (controller is Fika.Core.Main.ClientClasses.HandsControllers.FikaClientFirearmController fikaController)
                fikaController.SendBoltActionReloadPacket();
        }

        // The headset pose in the body's frame (Player.Transform), using the root's position and rotation only, the way observers place it.
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

            // Held items (gun, knife, grenade): send the item's WeaponRoot pose. The module moves the observed item there and redoes
            // the hand IK. WeaponRoot is on the base AbstractHandsController, so one path covers all three. The gun is not sent while
            // sprinting (a gun fixed to the aim looks bad running); knives and grenades are. leftOnGrip only applies to the gun.
            if (isHeldItem && hc.WeaponRoot != null)
            {
                if (isFirearm && local.IsSprintEnabled)
                    return;
                lastSendTime = Time.time;
                Transform weaponRoot = hc.WeaponRoot;
                // Use the pose the gun is drawn at: its collision may not be applied yet this frame (it's solved before the arm IK).
                Vector3 drawnPosition = weaponRoot.position;
                Quaternion drawnRotation = weaponRoot.rotation;
                if (isFirearm)
                    TarkovVR.Source.Player.Interactions.WeaponCollision.DrawnPose(weaponRoot, out drawnPosition, out drawnRotation);
                Vector3 wPos = frame.InverseTransformPoint(drawnPosition);
                Quaternion wRot = Quaternion.Inverse(frame.rotation) * drawnRotation;
                bool leftOnGrip = isFirearm && rig.isSupporting;
                Vector3 lPos = Vector3.zero; Quaternion lRot = Quaternion.identity;
                if (rig.LeftHand != null)
                {
                    lPos = frame.InverseTransformPoint(rig.LeftHand.transform.position);
                    lRot = Quaternion.Inverse(frame.rotation) * rig.LeftHand.transform.rotation;
                }
                FikaVrSync.SendWeaponPose(local.NetId, wPos, wRot, leftOnGrip, lPos, lRot);
                // The manual reload state, which the module draws on the observed gun placed above. A failure is logged once and never
                // breaks this LateUpdate.
                bool reloadBuilt = false;
                if (isFirearm)
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
            // Empty hands: send both hand poses (the module drives the arm IK). Any other hands controller keeps FIKA's vanilla animation.
            else if (hc is Player.EmptyHandsController)
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
