using System;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Components;
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
        // Sync dragging a DOWNED/revive teammate (a live player, not a Corpse). Relocates the downed
        // teammate's authoritative position so a revive lands at the dragged-to spot, and drives their
        // revive-ragdoll on 3rd-party observers. See DownedDragApply.
        public static bool enableDownedDragSync = true;
        // Relocate the downed teammate on EVERY drag packet (their view follows continuously) instead of
        // only on release. Off by default = one clean teleport at the safe spot when you let go (safest;
        // the downed player is in a death cam and doesn't see their body mid-drag anyway).
        public static bool downedRelocateContinuous = false;
        // Relocate the downed teammate to where their body was dragged. Applied at REVIVE, not on release
        // (see DownedDragApply / RevivePatch): teleporting the root while still downed drags the decoupled
        // ragdoll bones past the drop spot on observers (the "teleports a short distance" jump). false =
        // no relocation at all (they'd revive at the fall spot). Must match on both clients.
        public static bool relocateDownedTeammate = true;

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
            DownedDragApply.ResetState();
            if (ev.Manager is FikaServer server)
            {
                server.RegisterPacket<VRArmsPacket, NetPeer>(OnArmsServer);
                server.RegisterPacket<VRWeaponPacket, NetPeer>(OnWeaponServer);
                server.RegisterPacket<VRMeleeHitPacket, NetPeer>(OnMeleeHitServer);
                server.RegisterPacket<VREatingPacket, NetPeer>(OnEatingServer);
                server.RegisterPacket<VREatingSoundPacket, NetPeer>(OnEatingSoundServer);
                server.RegisterPacket<BodyDragPacket, NetPeer>(OnBodyDragServer);
                server.RegisterPacket<DownedDragPacket, NetPeer>(OnDownedDragServer);
            }
            else if (ev.Manager is FikaClient client)
            {
                client.RegisterPacket<VRArmsPacket>(OnArmsClient);
                client.RegisterPacket<VRWeaponPacket>(OnWeaponClient);
                client.RegisterPacket<VRMeleeHitPacket>(OnMeleeHitClient);
                client.RegisterPacket<VREatingPacket>(OnEatingClient);
                client.RegisterPacket<VREatingSoundPacket>(OnEatingSoundClient);
                client.RegisterPacket<BodyDragPacket>(OnBodyDragClient);
                client.RegisterPacket<DownedDragPacket>(OnDownedDragClient);
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

        // ===== DOWNED/REVIVE TEAMMATE DRAG =====
        // Resolve a downed/revive teammate's ReviveInteractable from a collider, for the VR mod's body
        // grab. ReviveInteractable is a Fika.Core type the VR assembly (SPT-VR.dll) must NOT name in a
        // field or signature: doing so hard-links Fika.Core into a type that ALWAYS loads, so the whole
        // mod fails to load when FIKA isn't installed (the field type can't resolve ->
        // BadImageFormatException). This module legitimately references Fika.Core and only loads when FIKA
        // is present, so the lookup lives here; it hands the component back as a plain MonoBehaviour plus
        // the EFT RagdollClass to drive. Returns null when the collider isn't a downed teammate.
        public static MonoBehaviour FindReviveInteractable(Component col, out RagdollClass ragdoll)
        {
            ragdoll = null;
            if (col == null)
                return null;
            ReviveInteractable revive = col.GetComponentInParent<ReviveInteractable>();
            if (revive == null)
                return null;
            ragdoll = revive._ragdoll;
            return revive;
        }

        // Resolve a downed teammate's NetId from the GameObject their ReviveInteractable lives on (= their
        // player GameObject). Used by the VR mod at grab time; -1 if not a known FIKA player.
        public static int ResolveDownedNetId(GameObject bodyRoot)
        {
            if (bodyRoot == null)
                return -1;
            Player p = bodyRoot.GetComponentInParent<Player>();
            if (p == null)
                return -1;
            if (!CoopHandler.TryGetCoopHandler(out CoopHandler coop) || coop.Players == null)
                return -1;
            foreach (var kv in coop.Players)
                if (ReferenceEquals(kv.Value, p))
                    return kv.Key;
            return -1;
        }

        // Broadcast a downed teammate's ragdoll pose (+ a ground-projected centroid as the relocate
        // anchor). The downed player's own client teleports there; observers drive their ragdoll copy.
        public static void SendDownedDrag(int netId, RagdollClass ragdoll)
        {
            if (!enableDownedDragSync || netId < 0)
                return;
            if (!ReadRagdollWorld(ragdoll, out Vector3[] pos, out Quaternion[] rot, out Vector3 root))
                return;
            DownedDragPacket p = default;
            p.NetId = netId;
            p.Released = false;
            p.RootPos = root;
            p.BoneCount = (byte)pos.Length;
            p.Positions = pos;
            p.Rotations = rot;
            SendUnreliableBroadcast(ref p);
        }

        // Drag ended: send the FINAL relocate spot (reliable, so a dropped packet can't strand the
        // teammate). No bones — observers reactivate + settle their ragdoll locally from its last pose.
        public static void SendDownedDragReleased(int netId, RagdollClass ragdoll)
        {
            if (!enableDownedDragSync || netId < 0)
                return;
            Vector3 root = ReadRagdollWorld(ragdoll, out _, out _, out Vector3 r) ? r : Vector3.zero;
            DownedDragPacket p = default;
            p.NetId = netId;
            p.Released = true;
            p.RootPos = root;
            p.BoneCount = 0;
            p.Positions = Array.Empty<Vector3>();
            p.Rotations = Array.Empty<Quaternion>();
            SendReliableBroadcast(ref p);
        }

        // Read fresh world pose per ragdoll bone, plus a relocate anchor = body centroid in XZ at the
        // lowest bone Y (≈ ground contact, so teleporting the downed player's root there keeps them grounded).
        private static bool ReadRagdollWorld(RagdollClass ragdoll, out Vector3[] pos, out Quaternion[] rot, out Vector3 root)
        {
            pos = null; rot = null; root = Vector3.zero;
            RigidbodySpawner[] bones = ragdoll != null ? ragdoll.RigidbodySpawner_0 : null;
            if (bones == null || bones.Length == 0 || bones.Length > 255)
                return false;
            int n = bones.Length;
            pos = new Vector3[n];
            rot = new Quaternion[n];
            Vector3 sum = Vector3.zero;
            float minY = float.MaxValue;
            int valid = 0;
            for (int i = 0; i < n; i++)
            {
                Transform t = bones[i] != null ? bones[i].transform : null;
                if (t == null)
                {
                    pos[i] = Vector3.zero;
                    rot[i] = Quaternion.identity;
                    continue;
                }
                pos[i] = t.position;
                rot[i] = t.rotation;
                sum += t.position;
                if (t.position.y < minY) minY = t.position.y;
                valid++;
            }
            if (valid == 0)
                return false;
            root = sum / valid;
            root.y = minY;
            return true;
        }

        private static void OnDownedDragServer(DownedDragPacket p, NetPeer sender)
        {
            try { DownedDragApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _bodyErr, "downed recv", e); }
            Singleton<FikaServer>.Instance?.SendData(ref p,
                p.Released ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, sender);
        }

        private static void OnDownedDragClient(DownedDragPacket p)
        {
            try { DownedDragApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _bodyErr, "downed recv", e); }
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
