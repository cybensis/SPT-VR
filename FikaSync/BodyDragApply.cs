using Comfort.Common;
using EFT;
using EFT.Interactive;
using JsonType;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receiver / renderer for a remote VR player's body drag.
    //
    // Every bone of the ragdoll is synced (syncing one bone lets local physics stretch the body). The ragdoll is frozen
    // kinematic on the first packet (ForceApplyTransformSync); after that a displayed per-bone pose is exponentially
    // smoothed toward the latest packet every frame (RenderSmoothing, like ArmSyncApply.SmoothArms) so the ~20 Hz packets
    // render smoothly. bodyDragSmoothRate = 0 -> snap.
    //
    // On release the ragdoll is un-frozen and a velocity-based settle manager keeps it active until it lands + rests, then
    // freezes it kinematic. RagdollClass.Func_0 (the settle predicate) is gated to NeverFreeze while held so EFT's own
    // coroutine can't freeze it, and restored when we freeze it.
    //
    // RenderSmoothing runs per frame (LateUpdate); the settle manager per physics step (FixedUpdate).
    // A throw here would drop the rest of FIKA's datagram (its weapon/shot packets), so the Unity calls are guarded too.
    internal static class BodyDragApply
    {
        // Gates for RagdollClass.Func_0 (the settle predicate (allAsleep, timePassed) -> shouldFreeze).
        private static readonly Func<bool, float, bool> NeverFreeze = (a, t) => false;
        private static readonly Func<bool, float, bool> AlwaysFreeze = (a, t) => true;

        // A corpse currently dragged by a remote player: the latest packet pose (target) + the smoothed
        // pose we actually render (displayed), per bone.
        private sealed class RemoteDrag
        {
            public ObservedCorpse corpse;
            public CorpseRagdoll ragdoll;
            public Vector3[] targetPos;
            public Quaternion[] targetRot;
            public Vector3[] dispPos;
            public Quaternion[] dispRot;
            public TransformSync[] applyBuf; // reused each frame to avoid per-frame allocation
            public float lastPacketTime;  // for the stale-timeout safety net (dragger dropped mid-drag)
        }
        private static readonly Dictionary<int, RemoteDrag> dragging = new Dictionary<int, RemoteDrag>();
        private static bool _startErr; // one-shot log latch for a failed ragdoll reactivate

        // A let-go body falling / settling locally before it freezes.
        private sealed class Settling
        {
            public ObservedCorpse corpse;
            public CorpseRagdoll ragdoll;
            public float restTimer;   // accumulated time below bodyLandedSpeed (resets if disturbed)
            public float releaseTime; // for the reorder guard + the hard freeze fallback
        }
        private static readonly List<Settling> settling = new List<Settling>();

        public static void ResetState()
        {
            dragging.Clear();
            settling.Clear();
        }

        // Called when the local player takes over a body (steal): drop our remote bookkeeping so a later
        // hand-off back re-enters (re-ForceApplies) cleanly.
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

            // Contention: we drag this same body locally (read via the VR mod hooks). Newest grab wins: held longer
            // than the grace = yield and render their drag, else keep ours. Our own broadcasts never loop back.
            // (No hooks set = flatscreen peer, never dragging.)
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
                // Stop driving + let the body fall, then the settle manager freezes it once it rests.
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
                // A drag packet for a body we're settling is either a stale reorder right after a release
                // (Unreliable packets can arrive late: ignore it, or it'd re-grab the body mid-settle) or
                // a real re-grab later (take over: drop it from the manager).
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
                // So recreate a live ragdoll first via Ragdoll.Start(), like the local owner's BeginBodyGrab
                // (skipped if it's already a live dynamic ragdoll).
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

                // Freeze the ragdoll kinematic + snap to this pose, and seed the smoothing (displayed =
                // target = this pose, so no jump on the first frame).
                corpse.Ragdoll._keepRigidbody = true;        // keepRigidbody: never tear down while held
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

            // Subsequent packet: just update the target. RenderSmoothing eases the displayed pose toward it
            // every frame (arrays are sized to the corpse's fixed bone count, so n matches).
            if (d.targetPos.Length != n)
                return;
            d.lastPacketTime = Time.time;
            for (int i = 0; i < n; i++)
            {
                d.targetPos[i] = packet.Positions[i];
                d.targetRot[i] = packet.Rotations[i];
            }
        }

        // Per frame (FikaSyncPlugin.LateUpdate): ease each dragged body's displayed pose toward the latest
        // packet and apply it (exponential smoothing, as ArmSyncApply). bodyDragSmoothRate <= 0 snaps (t = 1).
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
                // Safety net: no drag packet for too long (dragger dropped mid-drag, no Released arrived)
                // -> release it locally so it falls instead of staying frozen.
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

        // Per physics step (FikaSyncPlugin.FixedUpdate): settle let-go bodies.
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

        // Force every bone back into active physics so the body falls on release. ForceApplyTransformSync ->
        // ForceStopRigidBody froze every bone kinematic and also unsupported it (removed it from EFT's GClass745
        // physics-support system). Each bone must be re-supported here, in RagdollClass.Start's order (un-kinematic,
        // gravity, support, wake), else the body simulates but its transforms/mesh don't follow and it freezes in place
        // on observers. Supporting all can't double-register: ForceStop unsupported all of them (the local owner only
        // unsupports the grabbed bone). Func_0 stays NeverFreeze; the settle manager freezes it.
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

        // Freeze a settled body kinematic in place (the efficient "done" state), mirroring EFT's own
        // RagdollClass.method_1 (unsupport + Discrete + kinematic). Restore Func_0 so any still-spinning
        // settle coroutine exits next iteration instead of looping forever.
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

        // Reverse-lookup a corpse's network id (only used by ClearRemote's settling sweep).
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
