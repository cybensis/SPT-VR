using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receiver / renderer for a remote VR player's body drag.
    //
    // We sync the WHOLE ragdoll (every bone) so observers see the EXACT same pose the dragger does — no
    // local-physics divergence (which made the body stretch when only one bone was synced). The corpse's
    // ragdoll is frozen kinematic on the first packet (ForceApplyTransformSync); after that we DON'T snap
    // each packet — instead we exponentially smooth a "displayed" per-bone pose toward the latest packet
    // EVERY FRAME (RenderSmoothing, from FikaSyncPlugin.LateUpdate) and ApplyTransformSync the smoothed
    // pose. This is the same anti-stepping smoothing the arm/weapon sync uses (ArmSyncApply.SmoothArms),
    // so the ~20 Hz packets render smoothly at the display frame rate. bodyDragSmoothRate = 0 -> snap.
    //
    // On release we un-freeze the ragdoll and hand it to a velocity-based settle manager that keeps it
    // active until it lands + rests, then freezes it kinematic — the deterministic "drop -> timer ->
    // freeze" the local owner also does. RagdollClass.Func_0 (the settle predicate) is gated to NeverFreeze
    // while held so EFT's own coroutine can't freeze it out from under us, restored when WE freeze it.
    //
    // RenderSmoothing runs per FRAME (LateUpdate); the settle manager runs per physics step (FixedUpdate).
    // All packet handlers are wrapped in try/catch upstream — a throw here would drop the rest of FIKA's
    // datagram (its weapon/shot packets), so we also guard the Unity calls defensively.
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
            public RagdollClass ragdoll;
            public Vector3[] targetPos;
            public Quaternion[] targetRot;
            public Vector3[] dispPos;
            public Quaternion[] dispRot;
            public GStruct138[] applyBuf; // reused each frame to avoid per-frame allocation
            public float lastPacketTime;  // for the stale-timeout safety net (dragger dropped mid-drag)
        }
        private static readonly Dictionary<int, RemoteDrag> dragging = new Dictionary<int, RemoteDrag>();
        private static bool _startErr; // one-shot log latch for a failed ragdoll reactivate

        // A let-go body falling / settling locally before it freezes.
        private sealed class Settling
        {
            public ObservedCorpse corpse;
            public RagdollClass ragdoll;
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

            // Contention: we drag this same body locally (read via the VR mod hooks). Newest grab wins —
            // if we've held it longer than the grace we yield (and fall through to render their drag);
            // otherwise we keep ours. Our own broadcasts never loop back, so a packet for a body we drag
            // is necessarily another player. (No hooks set = flatscreen peer, never dragging.)
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
            RigidbodySpawner[] bones = corpse.Ragdoll.RigidbodySpawner_0;
            // Guard: a bone-count mismatch makes Corpse.ApplyTransformSync call Kill() on the body.
            if (bones == null || bones.Length != n || packet.Positions == null || packet.Rotations == null)
                return;

            if (!dragging.TryGetValue(packet.CorpseId, out RemoteDrag d))
            {
                // A drag packet for a body we're settling is either a STALE reorder right after a release
                // (Unreliable packets can arrive late — ignore it, or it'd re-grab the body mid-settle) or
                // a legit re-grab seconds later (take over: drop it from the manager).
                Settling st = FindSettling(corpse);
                if (st != null)
                {
                    if (Time.time - st.releaseTime < FikaVrSync.bodyReleaseReorderGuard)
                        return;
                    RemoveSettling(corpse);
                }

                // First drag packet for this body. It may be FROZEN (settled, kinematic) or fully TORN
                // DOWN (a settled corpse on the ground — EFT removes its rigidbodies once it settles +
                // leaves view) on THIS client. ForceApply/ApplyTransformSync only move the bone
                // TRANSFORMS, so the drag still SHOWS either way — but with no live rigidbodies the body
                // can't FALL on release (ReactivateRagdoll has nothing to drop -> it stays frozen). So
                // recreate a live ragdoll first via Ragdoll.Start(), exactly like the local owner's
                // BeginBodyGrab does on grab. (Skipped if it's already a live dynamic ragdoll, e.g. this
                // client also touched it.) This is the "frozen body picked up by another player stays
                // frozen on release" fix.
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
                corpse.Ragdoll.Bool_0 = true;        // keepRigidbody: never tear down while held
                corpse.Ragdoll.Func_0 = NeverFreeze; // stop EFT's settle coroutine freezing it while held
                d = new RemoteDrag
                {
                    corpse = corpse,
                    ragdoll = corpse.Ragdoll,
                    targetPos = (Vector3[])packet.Positions.Clone(),
                    targetRot = (Quaternion[])packet.Rotations.Clone(),
                    dispPos = (Vector3[])packet.Positions.Clone(),
                    dispRot = (Quaternion[])packet.Rotations.Clone(),
                    applyBuf = new GStruct138[n],
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

        // Per FRAME (FikaSyncPlugin.LateUpdate): ease each dragged body's displayed pose toward the latest
        // packet and apply it — exponential smoothing, identical to ArmSyncApply, so the 20 Hz packets
        // render smoothly at the display frame rate. bodyDragSmoothRate <= 0 snaps (t = 1).
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
                if (d.corpse == null || d.ragdoll == null || d.ragdoll.RigidbodySpawner_0 == null
                    || d.ragdoll.RigidbodySpawner_0.Length != d.dispPos.Length)
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

        private static GStruct138[] BuildSyncs(GStruct138[] buf, Vector3[] pos, Quaternion[] rot)
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
                RigidbodySpawner[] bones = (s.ragdoll != null) ? s.ragdoll.RigidbodySpawner_0 : null;
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

        // Force every bone of the ragdoll back into active physics so the body FALLS on release. The drag
        // froze every bone kinematic via ForceApplyTransformSync -> ForceStopRigidBody, which also
        // UNSUPPORTED every bone (removed it from EFT's GClass745 physics-support system). We MUST
        // re-SupportRigidbody each bone here, mirroring RagdollClass.Start's order (un-kinematic, gravity,
        // support, wake) — otherwise the body simulates but its transforms/mesh don't follow and it
        // "freezes in place" on observers while it actually falls on the owner (the original "body doesn't
        // fall when we let go" bug). Safe to support all here: ForceStop unsupported ALL of them, so this
        // can't double-register (unlike the LOCAL owner, which only unsupported the one grabbed bone).
        // Func_0 stays NeverFreeze; the settle manager freezes it on our schedule.
        private static void ReactivateRagdoll(RagdollClass ragdoll)
        {
            RigidbodySpawner[] bones = ragdoll != null ? ragdoll.RigidbodySpawner_0 : null;
            if (bones == null)
                return;
            ragdoll.Bool_0 = true; // keep it from tearing down while it settles
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody rb = bones[i] != null ? bones[i].Rigidbody : null;
                if (rb == null)
                    continue;
                if (rb.isKinematic)
                    rb.isKinematic = false;
                rb.useGravity = true;
                rb.detectCollisions = true;
                EFTPhysicsClass.GClass745.SupportRigidbody(rb, 0f);
                rb.WakeUp();
            }
        }

        // Freeze a settled body kinematic in place (the efficient "done" state), mirroring EFT's own
        // RagdollClass.method_1 (unsupport + Discrete + kinematic). Restore Func_0 so any still-spinning
        // settle coroutine exits next iteration instead of looping forever.
        private static void Freeze(RagdollClass ragdoll, RigidbodySpawner[] bones)
        {
            for (int b = 0; b < bones.Length; b++)
            {
                Rigidbody rb = bones[b] != null ? bones[b].Rigidbody : null;
                if (rb == null)
                    continue;
                EFTPhysicsClass.GClass745.UnsupportRigidbody(rb);
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                rb.isKinematic = true;
            }
            if (ragdoll != null)
                ragdoll.Func_0 = AlwaysFreeze;
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
