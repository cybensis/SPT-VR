using Comfort.Common;
using EFT;
using EFT.Interactive;
using JsonType;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receives and renders a remote VR player's body drag.
    //
    // Every ragdoll bone is synced; syncing only one bone lets local physics stretch the body. The first packet freezes the
    // ragdoll kinematic (ForceApplyTransformSync). After that, each bone's displayed pose is smoothed toward the latest
    // packet every frame (RenderSmoothing, in LateUpdate), so the ~20 Hz packets render smoothly.
    //
    // On release the ragdoll is unfrozen. A settle manager (per physics step, FixedUpdate) keeps it active until it lands
    // and rests, then freezes it kinematic. While the body is held, RagdollClass.Func_0 (EFT's settle predicate) is set to
    // NeverFreeze so EFT's own coroutine can't freeze it; it is restored when we freeze the body.
    //
    // A throw here would drop the rest of FIKA's datagram (its weapon/shot packets), so the Unity calls are guarded too.
    internal static class BodyDragApply
    {
        // Values for RagdollClass.Func_0, the settle predicate: (allAsleep, timePassed) -> shouldFreeze.
        private static readonly Func<bool, float, bool> NeverFreeze = (a, t) => false;
        private static readonly Func<bool, float, bool> AlwaysFreeze = (a, t) => true;

        // A corpse currently dragged by a remote player. Per bone: the latest packet pose (target) and the smoothed
        // pose we actually render (disp).
        private sealed class RemoteDrag
        {
            public ObservedCorpse corpse;
            public CorpseRagdoll ragdoll;
            public Vector3[] targetPos;
            public Quaternion[] targetRot;
            public Vector3[] dispPos;
            public Quaternion[] dispRot;
            public TransformSync[] applyBuf; // reused each frame to avoid per-frame allocation
            public float lastPacketTime;  // for the stale timeout (the dragger disconnected mid-drag)
        }
        private static readonly Dictionary<int, RemoteDrag> dragging = new Dictionary<int, RemoteDrag>();
        private static bool _startErr; // one-shot log latch for a failed ragdoll reactivate

        // A let-go body falling / settling locally before it freezes.
        private sealed class Settling
        {
            public ObservedCorpse corpse;
            public CorpseRagdoll ragdoll;
            public float restTimer;   // accumulated time below bodyLandedSpeed (resets if disturbed)
            public float releaseTime; // for the reorder guard and the forced freeze after bodyFreezeMaxTime
        }
        private static readonly List<Settling> settling = new List<Settling>();

        public static void ResetState()
        {
            dragging.Clear();
            settling.Clear();
        }

        // Called when the local player takes a body over. Drops our remote bookkeeping so that if the remote player
        // takes it back, the first packet runs ForceApplyTransformSync again.
        public static void ClearRemote(int netId)
        {
            dragging.Remove(netId);
            for (int i = settling.Count - 1; i >= 0; i--)
                if (settling[i].corpse != null && netId == LookupNetId(settling[i].corpse))
                    settling.RemoveAt(i);
        }

        public static void Apply(BodyDragPacket packet)
        {
            if (!FikaVrSync.enableBodyDragSync)
                return;

            // We are dragging this same body locally (read through the VR mod hooks). The newest grab wins: if we have held
            // it longer than bodyStealGraceTime, yield and render their drag, otherwise keep ours. Our own broadcasts never
            // loop back. With no hooks set, this peer is flatscreen and never drags.
            int localDraggedId = FikaVrSync.getLocalDraggedCorpseNetId != null ? FikaVrSync.getLocalDraggedCorpseNetId() : -1;
            if (packet.CorpseId == localDraggedId)
            {
                float grabTime = FikaVrSync.getLocalBodyGrabTime != null ? FikaVrSync.getLocalBodyGrabTime() : 0f;
                bool yield = !packet.Released && Time.time - grabTime > FikaVrSync.bodyStealGraceTime;
                if (yield)
                    FikaVrSync.onYieldBodyDrag?.Invoke();
                else
                    return;
            }

            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || gameWorld.ObservedPlayersCorpses == null)
                return;
            if (!gameWorld.ObservedPlayersCorpses.TryGetValue(packet.CorpseId, out ObservedCorpse corpse)
                || corpse == null || !corpse.HasRagdoll || corpse.Ragdoll == null)
                return;

            if (packet.Released)
            {
                // Stop driving the body and let it fall. The settle manager freezes it once it rests.
                dragging.Remove(packet.CorpseId);
                ReactivateRagdoll(corpse.Ragdoll);
                AddSettling(corpse);
                return;
            }

            int n = packet.BoneCount;
            RigidbodySpawner[] bones = corpse.Ragdoll._rigidbodySpawners;
            // Guard: a bone-count mismatch makes Corpse.ApplyTransformSync call Kill() on the body.
            if (bones == null || bones.Length != n || packet.Positions == null || packet.Rotations == null)
                return;

            if (!dragging.TryGetValue(packet.CorpseId, out RemoteDrag d))
            {
                // A drag packet for a body we're settling is either a late packet from before the release (Unreliable
                // packets can arrive out of order; ignore it, or it would re-grab the body mid-settle) or a real re-grab
                // (take it over and drop it from the settle manager).
                Settling st = FindSettling(corpse);
                if (st != null)
                {
                    if (Time.time - st.releaseTime < FikaVrSync.bodyReleaseReorderGuard)
                        return;
                    RemoveSettling(corpse);
                }

                // First drag packet for this body. On this client it may be frozen (settled, kinematic) or torn down
                // (EFT removes a settled corpse's rigidbodies once it leaves view). ApplyTransformSync only moves bone
                // transforms, so the drag shows either way, but without live rigidbodies the body can't fall on release.
                // So recreate a live ragdoll with Ragdoll.Start() first, like the local owner's BeginBodyGrab, unless it
                // is already a live dynamic ragdoll.
                Rigidbody probe = bones[0] != null ? bones[0].Rigidbody : null;
                if (probe == null || probe.isKinematic)
                {
                    try
                    {
                        corpse.Ragdoll.Start();
                    }
                    catch (Exception e)
                    {
                        if (!_startErr) { _startErr = true; FikaSyncPlugin.Log.LogError($"[FikaSync] ragdoll reactivate error: {e}"); }
                    }
                }

                // Freeze the ragdoll kinematic, snap it to this pose, and start both the displayed and target poses
                // at this pose so the first frame doesn't jump.
                corpse.Ragdoll._keepRigidbody = true;        // never tear down while held
                corpse.Ragdoll._checkCorpseIsStill = NeverFreeze; // stop EFT's settle coroutine freezing it while held
                d = new RemoteDrag
                {
                    corpse = corpse,
                    ragdoll = corpse.Ragdoll,
                    targetPos = (Vector3[])packet.Positions.Clone(),
                    targetRot = (Quaternion[])packet.Rotations.Clone(),
                    dispPos = (Vector3[])packet.Positions.Clone(),
                    dispRot = (Quaternion[])packet.Rotations.Clone(),
                    applyBuf = new TransformSync[n],
                    lastPacketTime = Time.time,
                };
                dragging[packet.CorpseId] = d;
                corpse.ForceApplyTransformSync(BuildSyncs(d.applyBuf, d.dispPos, d.dispRot));
                return;
            }

            // Later packets only update the target. RenderSmoothing eases the displayed pose toward it every frame.
            // The arrays are sized to the corpse's fixed bone count, so n matches.
            if (d.targetPos.Length != n)
                return;
            d.lastPacketTime = Time.time;
            for (int i = 0; i < n; i++)
            {
                d.targetPos[i] = packet.Positions[i];
                d.targetRot[i] = packet.Rotations[i];
            }
        }

        // Every frame (FikaSyncPlugin.LateUpdate): eases each dragged body's displayed pose toward the latest packet
        // (exponential smoothing, as in ArmSyncApply) and applies it. bodyDragSmoothRate <= 0 snaps.
        public static void RenderSmoothing()
        {
            if (dragging.Count == 0)
                return;
            float rate = FikaVrSync.bodyDragSmoothRate;
            float t = rate > 0f ? 1f - Mathf.Exp(-rate * Time.deltaTime) : 1f;

            List<int> dead = null;
            List<RemoteDrag> stale = null;
            foreach (var kv in dragging)
            {
                RemoteDrag d = kv.Value;
                if (d.corpse == null || d.ragdoll == null || d.ragdoll._rigidbodySpawners == null
                    || d.ragdoll._rigidbodySpawners.Length != d.dispPos.Length)
                {
                    (dead ?? (dead = new List<int>())).Add(kv.Key);
                    continue;
                }
                // No drag packet for too long (the dragger disconnected and no Released packet arrived):
                // release the body locally so it falls instead of staying frozen.
                if (Time.time - d.lastPacketTime > FikaVrSync.bodyDragStaleTimeout)
                {
                    (dead ?? (dead = new List<int>())).Add(kv.Key);
                    (stale ?? (stale = new List<RemoteDrag>())).Add(d);
                    continue;
                }
                for (int i = 0; i < d.dispPos.Length; i++)
                {
                    d.dispPos[i] = Vector3.Lerp(d.dispPos[i], d.targetPos[i], t);
                    d.dispRot[i] = Quaternion.Slerp(d.dispRot[i], d.targetRot[i], t);
                }
                d.corpse.ApplyTransformSync(BuildSyncs(d.applyBuf, d.dispPos, d.dispRot));
            }
            if (dead != null)
                foreach (int k in dead)
                    dragging.Remove(k);
            if (stale != null)
                foreach (RemoteDrag d in stale)
                {
                    ReactivateRagdoll(d.ragdoll);
                    if (d.corpse != null)
                        AddSettling(d.corpse);
                }
        }

        // Every physics step (FikaSyncPlugin.FixedUpdate): settles released bodies.
        public static void Tick()
        {
            SettleReleasedBodies();
        }

        private static TransformSync[] BuildSyncs(TransformSync[] buf, Vector3[] pos, Quaternion[] rot)
        {
            for (int i = 0; i < buf.Length; i++)
            {
                buf[i].Position = pos[i];
                buf[i].Rotation = rot[i];
            }
            return buf;
        }

        private static void SettleReleasedBodies()
        {
            if (settling.Count == 0)
                return;
            float dt = Time.fixedDeltaTime;
            float landedSqr = FikaVrSync.bodyLandedSpeed * FikaVrSync.bodyLandedSpeed;

            for (int i = settling.Count - 1; i >= 0; i--)
            {
                Settling s = settling[i];
                RigidbodySpawner[] bones = (s.ragdoll != null) ? s.ragdoll._rigidbodySpawners : null;
                if (bones == null)
                {
                    settling.RemoveAt(i);
                    continue;
                }

                float maxSqr = 0f;
                bool anyValid = false;
                for (int b = 0; b < bones.Length; b++)
                {
                    Rigidbody rb = bones[b] != null ? bones[b].Rigidbody : null;
                    if (rb == null)
                        continue;
                    anyValid = true;
                    if (rb.isKinematic)
                        rb.isKinematic = false;
                    rb.useGravity = true;
                    rb.detectCollisions = true;
                    if (rb.IsSleeping())
                        rb.WakeUp();
                    float v = rb.velocity.sqrMagnitude;
                    if (v > maxSqr)
                        maxSqr = v;
                }
                if (!anyValid)
                {
                    settling.RemoveAt(i);
                    continue;
                }

                if (maxSqr < landedSqr)
                    s.restTimer += dt;
                else
                    s.restTimer = 0f;

                if (s.restTimer >= FikaVrSync.bodyFreezeDelay || Time.time - s.releaseTime >= FikaVrSync.bodyFreezeMaxTime)
                {
                    Freeze(s.ragdoll, bones);
                    settling.RemoveAt(i);
                }
            }
        }

        // Puts every bone back into active physics so the body falls on release. ForceApplyTransformSync calls
        // ForceStopRigidBody, which made every bone kinematic and unsupported it (removed it from EFT's GClass745
        // physics-support system). Each bone must be supported again here, in RagdollClass.Start's order (non-kinematic,
        // gravity, support, wake). Otherwise the body simulates but its transforms and mesh don't follow, and it freezes in
        // place on observers. Supporting every bone can't register one twice, because ForceStop unsupported all of them
        // (the local owner only unsupports the grabbed bone). Func_0 stays NeverFreeze; the settle manager freezes the body.
        private static void ReactivateRagdoll(CorpseRagdoll ragdoll)
        {
            RigidbodySpawner[] bones = ragdoll != null ? ragdoll._rigidbodySpawners : null;
            if (bones == null)
                return;
            ragdoll._keepRigidbody = true; // keep it from tearing down while it settles
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody rb = bones[i] != null ? bones[i].Rigidbody : null;
                if (rb == null)
                    continue;
                if (rb.isKinematic)
                    rb.isKinematic = false;
                rb.useGravity = true;
                rb.detectCollisions = true;
                PhysicsExtensions.UpdateController.SupportRigidbody(rb, 0f);
                rb.WakeUp();
            }
        }

        // Freezes a settled body kinematic in place, the same way EFT's RagdollClass.method_1 does (unsupport, Discrete,
        // kinematic). Func_0 is restored so any settle coroutine still running exits on its next iteration instead of
        // looping forever.
        private static void Freeze(CorpseRagdoll ragdoll, RigidbodySpawner[] bones)
        {
            for (int b = 0; b < bones.Length; b++)
            {
                Rigidbody rb = bones[b] != null ? bones[b].Rigidbody : null;
                if (rb == null)
                    continue;
                PhysicsExtensions.UpdateController.UnsupportRigidbody(rb);
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                rb.isKinematic = true;
            }
            if (ragdoll != null)
                ragdoll._checkCorpseIsStill = AlwaysFreeze;
        }

        private static void AddSettling(ObservedCorpse corpse)
        {
            RemoveSettling(corpse);
            settling.Add(new Settling { corpse = corpse, ragdoll = corpse.Ragdoll, restTimer = 0f, releaseTime = Time.time });
        }

        private static void RemoveSettling(ObservedCorpse corpse)
        {
            for (int i = settling.Count - 1; i >= 0; i--)
                if (ReferenceEquals(settling[i].corpse, corpse))
                    settling.RemoveAt(i);
        }

        private static Settling FindSettling(ObservedCorpse corpse)
        {
            for (int i = 0; i < settling.Count; i++)
                if (ReferenceEquals(settling[i].corpse, corpse))
                    return settling[i];
            return null;
        }

        // Finds a corpse's network id. Only used by ClearRemote.
        private static int LookupNetId(ObservedCorpse corpse)
        {
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw == null || gw.ObservedPlayersCorpses == null)
                return -1;
            foreach (var kv in gw.ObservedPlayersCorpses)
                if (ReferenceEquals(kv.Value, corpse))
                    return kv.Key;
            return -1;
        }
    }
}
