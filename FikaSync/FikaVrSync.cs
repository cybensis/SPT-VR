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
        public static bool enableEatingSync = true;   // sync manual-eating gestures (food rides the synced hands)
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
        // Safety net: if the observer gets NO drag packet for a body this long (dragger disconnected /
        // crashed mid-drag, so no Released ever arrives), auto-release it locally so it can't stay frozen.
        // Generous so a lag spike during a real drag doesn't false-trigger (the reliable Released handles
        // the normal case immediately; this only catches hard drops).
        public static float bodyDragStaleTimeout = 2f;
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
            EatingSyncApply.ResetState();
            BodyDragApply.ResetState();
            if (ev.Manager is FikaServer server)
            {
                server.RegisterPacket<VRArmsPacket, NetPeer>(OnArmsServer);
                server.RegisterPacket<VRWeaponPacket, NetPeer>(OnWeaponServer);
                server.RegisterPacket<VRMeleeHitPacket, NetPeer>(OnMeleeHitServer);
                server.RegisterPacket<VREatingPacket, NetPeer>(OnEatingServer);
                server.RegisterPacket<VREatingSoundPacket, NetPeer>(OnEatingSoundServer);
                server.RegisterPacket<BodyDragPacket, NetPeer>(OnBodyDragServer);
            }
            else if (ev.Manager is FikaClient client)
            {
                client.RegisterPacket<VRArmsPacket>(OnArmsClient);
                client.RegisterPacket<VRWeaponPacket>(OnWeaponClient);
                client.RegisterPacket<VRMeleeHitPacket>(OnMeleeHitClient);
                client.RegisterPacket<VREatingPacket>(OnEatingClient);
                client.RegisterPacket<VREatingSoundPacket>(OnEatingSoundClient);
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

        // ===== MANUAL EATING (food props ride the synced hands) =====
        // Stable FNV-1a-32 hash of a prop transform's name. MUST be deterministic across machines
        // (string.GetHashCode is NOT), since the sender and the observer hash the same names on
        // different processes and compare. Used to match a synced prop to the observed food model.
        public static int StableHash(string s)
        {
            if (string.IsNullOrEmpty(s))
                return 0;
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return (int)h;
            }
        }

        // Reusable send buffers (one local eater per peer, sent synchronously, so a single static
        // set is safe — grown as needed, never shrunk).
        private static int[] _eHash = new int[0];
        private static Vector3[] _ePos = new Vector3[0];
        private static Quaternion[] _eRot = new Quaternion[0];
        private static bool[] _eVis = new bool[0];
        private static int[] _eaHash = new int[0];
        private static float[] _eaTime = new float[0];

        // Send the local eater's wrist poses + food-animator layer states + prop poses/visibility.
        // names/pos/rot/vis are parallel (chest-local), count props taken; animHashes/animTimes are
        // parallel per layer. Capped at 255 props / 8 anim layers.
        public static void SendEatingPose(int netId,
            Vector3 lPos, Quaternion lRot, Vector3 rPos, Quaternion rRot,
            System.Collections.Generic.List<int> animHashes,
            System.Collections.Generic.List<float> animTimes,
            System.Collections.Generic.List<string> names,
            System.Collections.Generic.List<Vector3> pos,
            System.Collections.Generic.List<Quaternion> rot,
            System.Collections.Generic.List<bool> vis, int count)
        {
            if (!enableEatingSync)
                return;
            if (count > 255) count = 255;
            if (_eHash.Length < count)
            {
                _eHash = new int[count]; _ePos = new Vector3[count];
                _eRot = new Quaternion[count]; _eVis = new bool[count];
            }
            for (int i = 0; i < count; i++)
            {
                _eHash[i] = StableHash(names[i]);
                _ePos[i] = pos[i]; _eRot[i] = rot[i]; _eVis[i] = vis[i];
            }
            int aCount = animHashes != null ? animHashes.Count : 0;
            if (aCount > 8) aCount = 8;
            if (_eaHash.Length < aCount) { _eaHash = new int[aCount]; _eaTime = new float[aCount]; }
            for (int i = 0; i < aCount; i++) { _eaHash[i] = animHashes[i]; _eaTime[i] = animTimes[i]; }
            VREatingPacket p = default;
            p.NetId = netId; p.Active = true;
            p.LeftPos = lPos; p.LeftRot = lRot; p.RightPos = rPos; p.RightRot = rRot;
            p.AnimLayerCount = (byte)aCount; p.AnimHashes = _eaHash; p.AnimTimes = _eaTime;
            p.PropCount = (byte)count;
            p.NameHashes = _eHash; p.Positions = _ePos; p.Rotations = _eRot; p.Visible = _eVis;
            SendUnreliableBroadcast(ref p);
        }

        // Eating ended — tell observers to restore hidden renderers and drop the override.
        public static void SendEatingStop(int netId)
        {
            if (!enableEatingSync)
                return;
            VREatingPacket p = default;
            p.NetId = netId; p.Active = false;
            SendUnreliableBroadcast(ref p);
        }

        private static void OnEatingServer(VREatingPacket p, NetPeer sender)
        {
            try { EatingSyncApply.Store(p); }
            catch (Exception e) { LogOnce(ref _eatErr, "eating recv", e); }
            Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, sender);
        }

        private static void OnEatingClient(VREatingPacket p)
        {
            try { EatingSyncApply.Store(p); }
            catch (Exception e) { LogOnce(ref _eatErr, "eating recv", e); }
        }

        // One eat sound (by event name) the eater actually played — observers replay it on the observed
        // food model's sound player (the looping vanilla audio is frozen out). Sent as a stable hash
        // (no Put(string) at runtime); the observer reverse-resolves it against the food's sound bank.
        public static void SendEatingSound(int netId, string name)
        {
            if (!enableEatingSync || string.IsNullOrEmpty(name))
                return;
            VREatingSoundPacket p = default;
            p.NetId = netId; p.NameHash = StableHash(name);
            SendUnreliableBroadcast(ref p);
        }

        private static void OnEatingSoundServer(VREatingSoundPacket p, NetPeer sender)
        {
            try { EatingSyncApply.QueueSound(p.NetId, p.NameHash); }
            catch (Exception e) { LogOnce(ref _eatErr, "eating sound recv", e); }
            Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, sender);
        }

        private static void OnEatingSoundClient(VREatingSoundPacket p)
        {
            try { EatingSyncApply.QueueSound(p.NetId, p.NameHash); }
            catch (Exception e) { LogOnce(ref _eatErr, "eating sound recv", e); }
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
            // RELIABLE: this is a one-shot "stop syncing, go client-only" signal — if it dropped, the
            // observer would hold the body frozen at its last pose forever. The drag stream stays
            // Unreliable (high-frequency, fine to lose a frame).
            SendReliableBroadcast(ref p);
        }

        private static void OnBodyDragServer(BodyDragPacket p, NetPeer sender)
        {
            try { BodyDragApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _bodyErr, "body recv", e); }
            // Relay regardless so a client-driven drag is still seen everywhere. The release marker must
            // relay reliably too (same reason as the send).
            Singleton<FikaServer>.Instance?.SendData(ref p,
                p.Released ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, sender);
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

        private static void SendReliableBroadcast<T>(ref T p) where T : INetSerializable
        {
            if (FikaBackendUtils.IsServer)
                Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.ReliableOrdered, broadcast: true);
            else
                Singleton<FikaClient>.Instance?.SendData(ref p, DeliveryMethod.ReliableOrdered, broadcast: true);
        }

        private static bool _armErr, _bodyErr, _meleeErr, _eatErr;
        private static void LogOnce(ref bool latch, string what, Exception e)
        {
            if (latch)
                return;
            latch = true;
            FikaSyncPlugin.Log.LogError($"[FikaSync] {what} error: {e}");
        }
    }
}
