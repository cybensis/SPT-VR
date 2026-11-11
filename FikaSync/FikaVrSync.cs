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
using JsonType;
using UnityEngine;

namespace SptVrFikaSync
{
    // ============================================================================================
    //  The public API + networking core of the FIKA sync module.
    //
    //  SEND: the VR mod produces the data (it needs the VR rig / the local grab); this module only
    //  packetizes + sends.
    //  REGISTER + RELAY + RECEIVE: all here, so a peer that has only this module (a flatscreen player /
    //  headless host) registers the packets (no "Undefined packet" crash), relays them as host and
    //  applies them.
    //
    //  Handlers run inside FIKA's NetPacketProcessor.ReadAllPackets, which has no try/catch: a throw
    //  would drop the rest of the datagram (incl. weapon packets). Every handler is wrapped.
    // ============================================================================================
    public static class FikaVrSync
    {
        // ---- master tunables (live) -------------------------------------------------------------
        public static bool enableArmSync = true;
        public static bool enableWeaponSync = true;   // sync the held gun's pose (aim) while armed
        public static bool enableMeleeHitSync = true; // replay VR-melee surface hits (sparks/glass) on observers
        public static bool enableEatingSync = true;   // sync manual-eating gestures (food rides the synced hands)
        public static bool enableReloadSync = true;   // sync the manual reload (hands on parts, magazines, bolt / handle, sounds)
        public static bool enableBodyDragSync = true;
        // Sync dragging a downed/revive teammate (a live player, not a Corpse): relocates the teammate's authoritative
        // position so a revive lands at the dragged-to spot, and drives their revive-ragdoll on observers. See DownedDragApply.
        public static bool enableDownedDragSync = true;
        // true = relocate the downed teammate on every drag packet (their view follows). false = one teleport when the drag
        // ends (the downed player is in a death cam and doesn't see their body mid-drag).
        public static bool downedRelocateContinuous = false;
        // Relocate the downed teammate to where their body was dragged, applied at revive (see DownedDragApply / RevivePatch):
        // teleporting the root while still downed drags the decoupled ragdoll bones past the drop spot on observers.
        // false = they revive at the fall spot. Must match on both clients.
        public static bool relocateDownedTeammate = true;

        // ---- body-drag steal arbitration: read-hooks into the VR mod's local grab state ---------
        // Callbacks set once by the VR mod, so this module never references the VR assembly. Unset on a flatscreen peer =
        // "not dragging".
        public static Func<int> getLocalDraggedCorpseNetId;   // -> HandsInteractionController.localDraggedCorpseNetId
        public static Func<float> getLocalBodyGrabTime;        // -> HandsInteractionController.bodyGrabTime
        public static Action onYieldBodyDrag;                  // -> RelinquishBodyDrag (yield a contested body)
        public static float bodyStealGraceTime = 0.4f;         // we yield a contested body after this long

        // ---- release settle (mirror of the VR owner's HandsInteractionController manager) --------
        // When a drag ends, the receiver keeps the body in active physics until it lands and rests for bodyFreezeDelay, then
        // freezes it kinematic, the same way the owner does (EFT's own settle takes an unpredictable ~15s).
        public static float bodyLandedSpeed = 0.3f;       // max bone speed (m/s) counted as "at rest"
        public static float bodyFreezeDelay = 3f;         // rest this long after landing, then freeze
        public static float bodyFreezeMaxTime = 20f;      // hard fallback freeze (jittery bodies)
        public static float bodyReleaseReorderGuard = 0.3f; // ignore a stale drag packet this long after a release
        // If the observer gets no drag packet for a body this long (the dragger disconnected mid-drag, so no Released arrives),
        // it releases the body locally. Long enough that a lag spike during a real drag doesn't trigger it.
        public static float bodyDragStaleTimeout = 2f;
        // Exponential smoothing rate of the synced ragdoll pose, per frame, to hide the ~20 Hz packet stepping (same model as
        // the arm/weapon sync's smoothRate). 0 = snap each packet. Latency is ~1/rate s.
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
            FingerSyncApply.ResetState();
            ReloadSyncApply.ResetState();
            DoorSyncApply.ResetState();
            if (ev.Manager is FikaServer server)
            {
                server.RegisterPacket<VRDoorPacket, NetPeer>(OnDoorServer);
                server.RegisterPacket<VRReloadPacket, NetPeer>(OnReloadServer);
                server.RegisterPacket<VRReloadSoundPacket, NetPeer>(OnReloadSoundServer);
                server.RegisterPacket<VRArmsPacket, NetPeer>(OnArmsServer);
                server.RegisterPacket<VRFingersPacket, NetPeer>(OnFingersServer);
                server.RegisterPacket<VRWeaponPacket, NetPeer>(OnWeaponServer);
                server.RegisterPacket<VRMeleeHitPacket, NetPeer>(OnMeleeHitServer);
                server.RegisterPacket<VREatingPacket, NetPeer>(OnEatingServer);
                server.RegisterPacket<VREatingSoundPacket, NetPeer>(OnEatingSoundServer);
                server.RegisterPacket<BodyDragPacket, NetPeer>(OnBodyDragServer);
                server.RegisterPacket<DownedDragPacket, NetPeer>(OnDownedDragServer);
                server.RegisterPacket<CorpseLootPacket, NetPeer>(OnCorpseLootServer);
            }
            else if (ev.Manager is FikaClient client)
            {
                client.RegisterPacket<VRDoorPacket>(OnDoorClient);
                client.RegisterPacket<VRReloadPacket>(OnReloadClient);
                client.RegisterPacket<VRReloadSoundPacket>(OnReloadSoundClient);
                client.RegisterPacket<VRArmsPacket>(OnArmsClient);
                client.RegisterPacket<VRFingersPacket>(OnFingersClient);
                client.RegisterPacket<VRWeaponPacket>(OnWeaponClient);
                client.RegisterPacket<VRMeleeHitPacket>(OnMeleeHitClient);
                client.RegisterPacket<VREatingPacket>(OnEatingClient);
                client.RegisterPacket<VREatingSoundPacket>(OnEatingSoundClient);
                client.RegisterPacket<BodyDragPacket>(OnBodyDragClient);
                client.RegisterPacket<DownedDragPacket>(OnDownedDragClient);
                client.RegisterPacket<CorpseLootPacket>(OnCorpseLootClient);
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

        // ===== FINGER POSES (authored open-hand / Index skeletal curls) =====
        public static bool enableFingerSync = true;

        public static void SendFingerPose(int netId, bool leftActive, Vector3[] leftEuler, bool rightActive, Vector3[] rightEuler)
        {
            if (!enableFingerSync)
                return;
            VRFingersPacket p = default;
            p.NetId = netId;
            p.LeftActive = leftActive;   p.Left = leftEuler;
            p.RightActive = rightActive; p.Right = rightEuler;
            SendUnreliableBroadcast(ref p);
        }

        private static void OnFingersServer(VRFingersPacket p, NetPeer sender)
        {
            try { FingerSyncApply.Store(p); }
            catch (Exception e) { LogOnce(ref _armErr, "finger recv", e); }
            Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, sender);
        }

        private static void OnFingersClient(VRFingersPacket p)
        {
            try { FingerSyncApply.Store(p); }
            catch (Exception e) { LogOnce(ref _armErr, "finger recv", e); }
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

        // ===== DOORS BY HAND (the angle and the handle; FIKA syncs open / shut) =====
        public static bool enableDoorSync = true;

        // SPT-VR's PhysicalDoors: true while the local VR player's hand moves that door (theirs wins on this machine)
        public static Func<WorldInteractiveObject, bool> isDoorDrivenLocally;

        public static void SendDoor(string doorId, float angle, float handleTurn, bool final)
        {
            if (!enableDoorSync || string.IsNullOrEmpty(doorId))
                return;
            VRDoorPacket p = default;
            p.DoorId = doorId; p.Angle = angle; p.HandleTurn = handleTurn; p.Final = final;
            if (final)
                SendReliableBroadcast(ref p);
            else
                SendUnreliableBroadcast(ref p);
        }

        private static void OnDoorServer(VRDoorPacket p, NetPeer sender)
        {
            try { DoorSyncApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _doorErr, "door recv", e); }
            Singleton<FikaServer>.Instance?.SendData(ref p, p.Final ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, sender);
        }

        private static void OnDoorClient(VRDoorPacket p)
        {
            try { DoorSyncApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _doorErr, "door recv", e); }
        }

        // ===== MANUAL EATING (food props ride the synced hands) =====
        // FNV-1a-32 hash of a prop transform's name. Must be deterministic across machines (string.GetHashCode is not): the
        // sender and the observer hash the same names in different processes and compare, to match a synced prop to the
        // observed food model.
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

        // Reusable send buffers (one local eater per peer, sent synchronously): grown as needed, never shrunk.
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

        // One eat sound (by event name) the eater played: observers replay it on the observed food model's sound player (the
        // looping vanilla audio is frozen out). Sent as a stable hash; the observer resolves it against the food's sound bank.
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

        // ===== MANUAL RELOAD (hands on parts, magazines, bolt / handle; ReloadSyncApply draws it) =====
        // Reusable send buffers, as the eating's (one local VR player per peer, sent synchronously)
        private static int[] _rPropItems = new int[0];
        private static Vector3[] _rPropPos = new Vector3[0];
        private static Quaternion[] _rPropRot = new Quaternion[0];
        private static int[] _rPartPaths = new int[0];
        private static Vector3[] _rPartPos = new Vector3[0];
        private static Quaternion[] _rPartRot = new Quaternion[0];
        private static bool[] _rPartShown = new bool[0];

        // The local VR player's reload state (SPT-VR's VRReloadController.Sync buffers). Item ids and part paths go as StableHash
        // (no strings on the wire); capped at 255 props / parts.
        public static void SendReloadState(int netId, bool manualReload, bool manualBolt, bool driveLeft, bool driveRight,
            Vector3 leftPos, Quaternion leftRot, Vector3 rightPos, Quaternion rightRot,
            System.Collections.Generic.List<string> propItems,
            System.Collections.Generic.List<Vector3> propPositions,
            System.Collections.Generic.List<Quaternion> propRotations,
            System.Collections.Generic.List<string> partPaths,
            System.Collections.Generic.List<Vector3> partPositions,
            System.Collections.Generic.List<Quaternion> partRotations,
            System.Collections.Generic.List<bool> partShown,
            string hiddenGroundItem, bool boltCatch, bool boltOpenDriven, float boltOpen, int shellsThrown, int roundsShown)
        {
            if (!enableReloadSync)
                return;
            int propCount = System.Math.Min(propItems.Count, 255);
            int partCount = System.Math.Min(partPaths.Count, 255);
            if (_rPropItems.Length < propCount)
            {
                _rPropItems = new int[propCount]; _rPropPos = new Vector3[propCount]; _rPropRot = new Quaternion[propCount];
            }
            if (_rPartPaths.Length < partCount)
            {
                _rPartPaths = new int[partCount]; _rPartPos = new Vector3[partCount];
                _rPartRot = new Quaternion[partCount]; _rPartShown = new bool[partCount];
            }
            for (int i = 0; i < propCount; i++)
            {
                _rPropItems[i] = StableHash(propItems[i]);
                _rPropPos[i] = propPositions[i];
                _rPropRot[i] = propRotations[i];
            }
            for (int i = 0; i < partCount; i++)
            {
                _rPartPaths[i] = StableHash(partPaths[i]);
                _rPartPos[i] = partPositions[i];
                _rPartRot[i] = partRotations[i];
                _rPartShown[i] = partShown[i];
            }
            VRReloadPacket p = default;
            p.NetId = netId;
            p.Active = true;
            p.Flags = (byte)((manualReload ? VRReloadPacket.ManualReload : 0) | (manualBolt ? VRReloadPacket.ManualBolt : 0)
                | (driveLeft ? VRReloadPacket.DriveLeft : 0) | (driveRight ? VRReloadPacket.DriveRight : 0)
                | (boltCatch ? VRReloadPacket.BoltCatch : 0) | (boltOpenDriven ? VRReloadPacket.BoltOpenDriven : 0));
            p.BoltOpen = boltOpen;
            p.ShellsThrown = shellsThrown;
            p.RoundsShown = roundsShown;
            p.HiddenGroundItem = StableHash(hiddenGroundItem);
            p.LeftPos = leftPos; p.LeftRot = leftRot; p.RightPos = rightPos; p.RightRot = rightRot;
            p.PropCount = (byte)propCount; p.PropItems = _rPropItems; p.PropPositions = _rPropPos; p.PropRotations = _rPropRot;
            p.PartCount = (byte)partCount; p.PartPaths = _rPartPaths; p.PartPositions = _rPartPos; p.PartRotations = _rPartRot;
            p.PartShown = _rPartShown;
            SendUnreliableBroadcast(ref p);
        }

        // No gun in hands any more (or running with it): observers put their copy of the gun back. Reliable: a lost stop would leave
        // it overridden until the stale timeout.
        public static void SendReloadStop(int netId)
        {
            if (!enableReloadSync)
                return;
            VRReloadPacket p = default;
            p.NetId = netId;
            p.Active = false;
            SendReliableBroadcast(ref p);
        }

        public static void SendReloadSound(int netId, string name)
        {
            if (!enableReloadSync || string.IsNullOrEmpty(name))
                return;
            VRReloadSoundPacket p = default;
            p.NetId = netId; p.NameHash = StableHash(name);
            SendUnreliableBroadcast(ref p);
        }

        private static void OnReloadServer(VRReloadPacket p, NetPeer sender)
        {
            try { ReloadSyncApply.Store(p); }
            catch (Exception e) { LogOnce(ref _reloadErr, "reload recv", e); }
            Singleton<FikaServer>.Instance?.SendData(ref p, p.Active ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered, sender);
        }

        private static void OnReloadClient(VRReloadPacket p)
        {
            try { ReloadSyncApply.Store(p); }
            catch (Exception e) { LogOnce(ref _reloadErr, "reload recv", e); }
        }

        private static void OnReloadSoundServer(VRReloadSoundPacket p, NetPeer sender)
        {
            try { ReloadSyncApply.QueueSound(p.NetId, p.NameHash); }
            catch (Exception e) { LogOnce(ref _reloadErr, "reload sound recv", e); }
            Singleton<FikaServer>.Instance?.SendData(ref p, DeliveryMethod.Unreliable, sender);
        }

        private static void OnReloadSoundClient(VRReloadSoundPacket p)
        {
            try { ReloadSyncApply.QueueSound(p.NetId, p.NameHash); }
            catch (Exception e) { LogOnce(ref _reloadErr, "reload sound recv", e); }
        }

        // ===== BODY DRAG =====
        public static void ClearRemoteDraggedCorpse(int netId) => BodyDragApply.ClearRemote(netId);

        // Broadcast the dragged corpse's full ragdoll pose (every bone). GetTransformSync() returns fresh world bone
        // transforms (the TransformSyncs property caches).
        public static void SendDraggedBody(int corpseNetId, Corpse corpse)
        {
            if (!enableBodyDragSync || corpse == null)
                return;
            TransformSync[] syncs = corpse.GetTransformSync();
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
            // Reliable: a one-shot "stop syncing" signal. If it dropped, the observer would hold the body frozen at its last
            // pose. The drag stream stays Unreliable.
            SendReliableBroadcast(ref p);
        }

        private static void OnBodyDragServer(BodyDragPacket p, NetPeer sender)
        {
            try { BodyDragApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _bodyErr, "body recv", e); }
            // Relay so a client-driven drag is seen everywhere. The release marker relays reliably, like the send.
            Singleton<FikaServer>.Instance?.SendData(ref p,
                p.Released ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, sender);
        }

        private static void OnBodyDragClient(BodyDragPacket p)
        {
            try { BodyDragApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _bodyErr, "body recv", e); }
        }

        // ===== CORPSE LOOT SYNC (keep the host's bots/AI honest about a dragged body) =====
        // Sent by the dragger (a client) so the host/headless, which runs the bots + looting AI but not the VR mod, pulls the
        // body's ORBIT waypoint on grab and re-adds it (+ relocates the server ragdoll for live scanners like LootingBots) at
        // the rest spot on settle. Reliable: a dropped marker would leave the body lootable in the wrong place or stuck
        // unlootable. Only clients send: the host handles its own drags locally (SPT-VR.dll's OrbitSupport).
        public static void SendCorpseLootGrab(string profileId)
        {
            if (string.IsNullOrEmpty(profileId) || !FikaBackendUtils.IsClient)
                return;
            CorpseLootPacket p = default;
            p.ProfileId = profileId;
            p.Released = false;
            SendReliableBroadcast(ref p);
        }

        public static void SendCorpseLootSettle(string profileId, Vector3 restPos)
        {
            if (string.IsNullOrEmpty(profileId) || !FikaBackendUtils.IsClient)
                return;
            CorpseLootPacket p = default;
            p.ProfileId = profileId;
            p.Released = true;
            p.RestPos = restPos;
            SendReliableBroadcast(ref p);
        }

        private static void OnCorpseLootServer(CorpseLootPacket p, NetPeer sender)
        {
            // No relay — only the host (which received this) runs the bots/AI; other clients have nothing to do.
            try { CorpseLootApply.Apply(p); }
            catch (Exception e) { LogOnce(ref _bodyErr, "corpse-loot recv", e); }
        }

        private static void OnCorpseLootClient(CorpseLootPacket p)
        {
            try { CorpseLootApply.Apply(p); } // self-gates on IsServer (no-op on a client)
            catch (Exception e) { LogOnce(ref _bodyErr, "corpse-loot recv", e); }
        }

        // ===== DOWNED/REVIVE TEAMMATE DRAG =====
        // Resolve a downed/revive teammate's ReviveInteractable from a collider, for the VR mod's body grab. ReviveInteractable
        // is a Fika.Core type, and SPT-VR.dll must not name it in a field or signature: that hard-links Fika.Core into a type
        // that always loads, and the mod fails to load without FIKA (BadImageFormatException). So the lookup lives here and
        // returns a plain MonoBehaviour plus the EFT ragdoll to drive. Null = the collider isn't a downed teammate.
        public static MonoBehaviour FindReviveInteractable(Component col, out CorpseRagdoll ragdoll)
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
        public static void SendDownedDrag(int netId, CorpseRagdoll ragdoll)
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

        // Drag ended: send the final relocate spot (reliable, so a dropped packet can't strand the teammate). No bones:
        // observers reactivate + settle their ragdoll locally from its last pose.
        public static void SendDownedDragReleased(int netId, CorpseRagdoll ragdoll)
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
        private static bool ReadRagdollWorld(CorpseRagdoll ragdoll, out Vector3[] pos, out Quaternion[] rot, out Vector3 root)
        {
            pos = null; rot = null; root = Vector3.zero;
            RigidbodySpawner[] bones = ragdoll != null ? ragdoll._rigidbodySpawners : null;
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

        private static bool _armErr, _bodyErr, _meleeErr, _eatErr, _reloadErr, _doorErr;
        private static void LogOnce(ref bool latch, string what, Exception e)
        {
            if (latch)
                return;
            latch = true;
            FikaSyncPlugin.Log.LogError($"[FikaSync] {what} error: {e}");
        }
    }
}
