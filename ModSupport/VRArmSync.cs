using EFT;
using Fika.Core.Main.Players;
using SptVrFikaSync;
using TarkovVR.Source.Player.VRManager;
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
        public static float sendInterval = 0.05f;     // ~20/s

        private static float lastSendTime;

        // Called every frame from VRPlayerManager.LateUpdate (guarded by InstalledMods.FIKAInstalled).
        public static void Tick()
        {
            if (!VRGlobals.inGame)
                return;
            if (Time.time - lastSendTime < sendInterval)
                return;

            // NetId is a FikaPlayer member (not on EFT.Player); the local human player is a FikaPlayer.
            FikaPlayer local = VRGlobals.player as FikaPlayer;
            VRPlayerManager rig = VRGlobals.vrPlayer;
            if (local == null || rig == null)
                return;
            Transform chest = local.PlayerBones?.Ribcage?.Original;
            if (chest == null)
                return;

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
            }
            // Empty hands: stream both controller hand poses (the module drives the arm IK). Eating/meds
            // intentionally NOT synced here (the remote keeps FIKA's vanilla observed-meds animation,
            // which reads fine; proper VR eating sync is a separate, larger job -- see notes).
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
