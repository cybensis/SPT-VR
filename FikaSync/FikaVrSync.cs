using System;
using Comfort.Common;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace SptVrFikaSync
{
    // ============================================================================================
    //  The public API + networking core of the FIKA sync module.
    //
    //  SEND: the VR mod calls SendArmPose / SendDraggedBody(Released). Those need the VR rig (arms) or
    //  the local grab (body), so the VR mod produces the data; this module only packetizes + sends.
    //  REGISTER + RELAY + RECEIVE: handled entirely here so a peer that has ONLY this module (a
    //  flatscreen player / headless host) registers the packets (no "Undefined packet" crash),
    //  relays them as host, and applies them (ArmSyncApply / BodyDragApply).
    //
    //  Handlers run inside FIKA's NetPacketProcessor.ReadAllPackets, which has NO try/catch around it,
    //  so a throw would drop the rest of the datagram (incl. weapon packets). Every handler is wrapped.
    // ============================================================================================
    public static class FikaVrSync
    {
        // ---- master tunables (live) -------------------------------------------------------------
        public static bool enableArmSync = true;
        public static bool enableWeaponSync = true;   // sync the held gun's pose (aim) while armed
        public static bool enableMeleeHitSync = true; // replay VR-melee surface hits (sparks/glass) on observers
        public static bool enableBodyDragSync = true;

        // ---- body-drag steal arbitration: read-hooks into the VR mod's local grab state ---------
        // Callbacks (set once by the VR mod) so the VR mod stays the single source of truth and we
        // never reference up into the VR assembly. Null/unset on a flatscreen peer = "not dragging".
        public static Func<int> getLocalDraggedCorpseNetId;   // -> HandsInteractionController.localDraggedCorpseNetId
        public static Func<float> getLocalBodyGrabTime;        // -> HandsInteractionController.bodyGrabTime
        public static Action onYieldBodyDrag;                  // -> RelinquishBodyDrag (yield a contested body)
        public static float bodyStealGraceTime = 0.4f;         // we yield a contested body after this long

        // ---- release settle (mirror of the VR owner's HandsInteractionController manager) --------
        // When a drag ends, the receiver keeps the body in ACTIVE physics until it lands AND rests
        // for bodyFreezeDelay, then freezes it kinematic — so remotes settle a let-go body the same
        // deterministic way the owner does (instead of EFT's unpredictable ~15s settle).
        public static float bodyLandedSpeed = 0.3f;       // max bone speed (m/s) counted as "at rest"
        public static float bodyFreezeDelay = 3f;         // rest this long after landing, then freeze
        public static float bodyFreezeMaxTime = 20f;      // hard fallback freeze (jittery bodies)
        public static float bodyReleaseReorderGuard = 0.3f; // ignore a stale drag packet this long after a release
        // Exponential smoothing of the synced ragdoll pose, per frame, to hide the ~20 Hz packet stepping
        // (same model as the arm/weapon sync's smoothRate). 0 = snap each packet. ~18 = smooth + responsive
        // (~1/rate s of latency, which reads as natural drag weight).
        public static float bodyDragSmoothRate = 18f;

        public static void Init()
        {
            FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnNetworkManagerCreated);
        }

        // (Re)register on every raid's network manager.
        private static void OnNetworkManagerCreated(FikaNetworkManagerCreatedEvent ev)
        {
            ArmSyncApply.ResetState();
            BodyDragApply.ResetState();
            if (ev.Manager is FikaServer server)
            {
                server.RegisterPacket<VRArmsPacket, NetPeer>(OnArmsServer);
                server.RegisterPacket<VRWeaponPacket, NetPeer>(OnWeaponServer);
                server.RegisterPacket<VRMeleeHitPacket, NetPeer>(OnMeleeHitServer);
                server.RegisterPacket<BodyDragPacket, NetPeer>(OnBodyDragServer);
            }
            else if (ev.Manager is FikaClient client)
            {
                client.RegisterPacket<VRArmsPacket>(OnArmsClient);
                client.RegisterPacket<VRWeaponPacket>(OnWeaponClient);
                client.RegisterPacket<VRMeleeHitPacket>(OnMeleeHitClient);
                client.RegisterPacket<BodyDragPacket>(OnBodyDragClient);
            }
        }

        // ===== ARM SYNC =====
        public static void SendArmPose(int netId, Vector3 leftPos, Quaternion leftRot, Vector3 rightPos, Quaternion rightRot)
        {
            if (!enableArmSync)
                return;
            VRArmsPacket p = default;
            p.NetId = netId;
            p.LeftPos = leftPos;   p.LeftRot = leftRot;
            p.RightPos = rightPos; p.RightRot = rightRot;
            SendUnreliableBroadcast(ref p);
        }

        private static void OnArmsServer(VRArmsPacket p, NetPeer sender)
        {
            try { ArmSyncApply.Store(p); }
            catch (Exception e) { LogOnce(ref _armErr, "arm recv", e); }
            // Relay client-driven poses to every OTHER client (NetPeer overload = all-except-sender).
            Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, sender);
        }

        private static void OnArmsClient(VRArmsPacket p)
        {
            try { ArmSyncApply.Store(p); }
            catch (Exception e) { LogOnce(ref _armErr, "arm recv", e); }
        }

        // ===== WEAPON (held-gun pose) =====
        public static void SendWeaponPose(int netId, Vector3 pos, Quaternion rot, bool leftOnGrip, Vector3 leftPos, Quaternion leftRot)
        {
            if (!enableWeaponSync)
                return;
            VRWeaponPacket p = default;
            p.NetId = netId; p.Pos = pos; p.Rot = rot;
            p.LeftOnGrip = leftOnGrip; p.LeftPos = leftPos; p.LeftRot = leftRot;
            SendUnreliableBroadcast(ref p);
        }

        private static void OnWeaponServer(VRWeaponPacket p, NetPeer sender)
        {
            try { ArmSyncApply.StoreWeapon(p); }
            catch (Exception e) { LogOnce(ref _armErr, "weapon recv", e); }
            Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, sender);
        }

        private static void OnWeaponClient(VRWeaponPacket p)
        {
            try { ArmSyncApply.StoreWeapon(p); }
            catch (Exception e) { LogOnce(ref _armErr, "weapon recv", e); }
        }

        // ===== MELEE SURFACE HIT (sparks / glass break) =====
        public static void SendMeleeHit(Vector3 point, Vector3 normal, Vector3 direction, float damage)
        {
            if (!enableMeleeHitSync)
                return;
            VRMeleeHitPacket p = default;
            p.Point = point; p.Normal = normal; p.Direction = direction; p.Damage = damage;
            SendUnreliableBroadcast(ref p);
        }

        private static void OnMeleeHitServer(VRMeleeHitPacket p, NetPeer sender)
        {
            try { MeleeHitApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _meleeErr, "melee recv", e); }
            Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, sender);
        }

        private static void OnMeleeHitClient(VRMeleeHitPacket p)
        {
            try { MeleeHitApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _meleeErr, "melee recv", e); }
        }

        // ===== BODY DRAG =====
        public static void ClearRemoteDraggedCorpse(int netId) => BodyDragApply.ClearRemote(netId);

        // Broadcast the dragged corpse's FULL ragdoll pose (every bone), so observers render the exact
        // same ragdoll. method_20() returns FRESH world bone transforms (the TransformSyncs property
        // caches, so we use the method directly).
        public static void SendDraggedBody(int corpseNetId, Corpse corpse)
        {
            if (!enableBodyDragSync || corpse == null)
                return;
            GStruct138[] syncs = corpse.method_20();
            if (syncs == null || syncs.Length == 0 || syncs.Length > 255)
                return;
            BodyDragPacket p = default;
            p.CorpseId = corpseNetId;
            p.Released = false;
            p.BoneCount = (byte)syncs.Length;
            p.Positions = new Vector3[syncs.Length];
            p.Rotations = new Quaternion[syncs.Length];
            for (int i = 0; i < syncs.Length; i++)
            {
                p.Positions[i] = syncs[i].Position;
                p.Rotations[i] = syncs[i].Rotation;
            }
            SendUnreliableBroadcast(ref p);
        }

        public static void SendDraggedBodyReleased(int corpseNetId)
        {
            if (!enableBodyDragSync)
                return;
            BodyDragPacket p = default;
            p.CorpseId = corpseNetId;
            p.Released = true;
            SendUnreliableBroadcast(ref p);
        }

        private static void OnBodyDragServer(BodyDragPacket p, NetPeer sender)
        {
            try { BodyDragApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _bodyErr, "body recv", e); }
            // Relay regardless so a client-driven drag is still seen everywhere.
            Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, sender);
        }

        private static void OnBodyDragClient(BodyDragPacket p)
        {
            try { BodyDragApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _bodyErr, "body recv", e); }
        }

        // ===== helpers =====
        private static void SendUnreliableBroadcast<T>(ref T p) where T : INetSerializable
        {
            if (FikaBackendUtils.IsServer)
                Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, broadcast: true);
            else
                Singleton<FikaClient>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, broadcast: true);
        }

        private static bool _armErr, _bodyErr, _meleeErr;
        private static void LogOnce(ref bool latch, string what, Exception e)
        {
            if (latch)
                return;
            latch = true;
            FikaSyncPlugin.Log.LogError($"[FikaSync] {what} error: {e}");
        }
    }
}
