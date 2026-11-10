using System;
using System.Collections.Generic;
using EFT.Interactive;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receiver / renderer for a downed (revive-state) teammate being dragged. Mirrors BodyDragApply, but a downed
    // teammate is not a Corpse: it's a still-live FikaPlayer keyed by NetId. A packet is applied two ways:
    //   (1) The downed player's own client (NetId == my player) relocates itself to RootPos: their authoritative
    //       position lives only on their client. From the release marker's spot (applied at revive), or every packet
    //       if downedRelocateContinuous. Only while downed (IsAlive == false in FIKA's downed state), so a late packet
    //       can't move a revived player.
    //   (2) Any other client drives its copy of the revive-ragdoll bones to the synced world pose (all bones, smoothed,
    //       as BodyDragApply). The ragdoll lives on FIKA's ReviveInteractable (_ragdoll); if it can't be resolved the
    //       visual is skipped but (1) still works. Only matters in raids of 3+ players.
    // Handlers are wrapped in try/catch upstream (a throw would drop the rest of FIKA's datagram). RenderSmoothing runs
    // per frame (LateUpdate), pumped from FikaSyncPlugin.
    internal static class DownedDragApply
    {
        private static readonly Func<bool, float, bool> NeverFreeze = (a, t) => false;
        private static readonly Func<bool, float, bool> AlwaysFreeze = (a, t) => true;

        private sealed class Remote
        {
            public FikaPlayer player;
            public CorpseRagdoll ragdoll;
            public RigidbodySpawner[] bones;
            public Vector3[] targetPos;
            public Quaternion[] targetRot;
            public Vector3[] dispPos;
            public Quaternion[] dispRot;
            public float lastPacketTime;
        }
        private static readonly Dictionary<int, Remote> dragging = new Dictionary<int, Remote>();

        // The drop spot to relocate the local downed player to, applied at revive (OnLocalRevived), not on release:
        // teleporting the root while downed drags the decoupled ragdoll bones past the drop spot on observers. At
        // revive the animator re-poses the bones from the root anyway.
        private static bool _hasPendingRevivePos;
        private static Vector3 _pendingRevivePos;

        public static void ResetState()
        {
            dragging.Clear();
            _hasPendingRevivePos = false;
        }

        // Called from the revive patch when the local player is revived: apply the deferred drop-spot relocation
        public static void OnLocalRevived(FikaPlayer player)
        {
            if (!_hasPendingRevivePos || player == null)
                return;
            _hasPendingRevivePos = false;
            try { player.Teleport(_pendingRevivePos); }
            catch (Exception e) { FikaSyncPlugin.Log.LogError($"[FikaSync] revive relocate error: {e}"); }
        }

        public static void Apply(DownedDragPacket p)
        {
            if (!FikaVrSync.enableDownedDragSync)
                return;
            if (!CoopHandler.TryGetCoopHandler(out CoopHandler coop) || coop.Players == null)
                return;
            if (!coop.Players.TryGetValue(p.NetId, out FikaPlayer player) || player == null)
                return;

            // (1) I'm the downed player being dragged -> relocate myself.
            if (player.IsYourPlayer)
            {
                bool downed = player.HealthController != null && !player.HealthController.IsAlive;
                if (!downed || !FikaVrSync.relocateDownedTeammate)
                    return;
                if (FikaVrSync.downedRelocateContinuous)
                {
                    // A/B path: teleport on every packet. Overshoots on observers (the root moves while the bones
                    // are decoupled); for testing only.
                    player.Teleport(p.RootPos);
                }
                else if (p.Released)
                {
                    // Default: remember the drop spot and apply it at revive
                    // (FikaPlayer_ToggleDowned_RevivePatch -> OnLocalRevived)
                    _hasPendingRevivePos = true;
                    _pendingRevivePos = p.RootPos;
                }
                return;
            }

            // (2) Observer: drive this downed teammate's revive-ragdoll to the synced pose.
            if (p.Released)
            {
                if (dragging.TryGetValue(p.NetId, out Remote rr))
                {
                    dragging.Remove(p.NetId);
                    // Freeze at the last synced pose, as the dragger does on release: EFT never auto-settles a
                    // downed ragdoll, so letting it go dynamic would move it off the drop spot (= the relocate spot)
                    Freeze(rr.ragdoll, rr.bones);
                }
                return;
            }

            int n = p.BoneCount;
            if (n == 0 || p.Positions == null || p.Rotations == null)
                return;

            if (!dragging.TryGetValue(p.NetId, out Remote d))
            {
                CorpseRagdoll ragdoll = ResolveRagdoll(player);
                RigidbodySpawner[] bones = ragdoll != null ? ragdoll._rigidbodySpawners : null;
                if (ragdoll == null || bones == null || bones.Length != n)
                    return; // can't resolve/drive this body — relocation (1) still handles the gameplay

                // Freeze the ragdoll kinematic + keep it from settling/tearing down while we drive it, then
                // seed the smoothing (displayed = target = this pose, so no first-frame jump).
                FreezeKinematic(bones);
                ragdoll._keepRigidbody = true;        // keepRigidbody: don't tear it down while held
                ragdoll._checkCorpseIsStill = NeverFreeze; // stop FIKA's settle predicate freezing it under us
                d = new Remote
                {
                    player = player,
                    ragdoll = ragdoll,
                    bones = bones,
                    targetPos = (Vector3[])p.Positions.Clone(),
                    targetRot = (Quaternion[])p.Rotations.Clone(),
                    dispPos = (Vector3[])p.Positions.Clone(),
                    dispRot = (Quaternion[])p.Rotations.Clone(),
                    lastPacketTime = Time.time,
                };
                dragging[p.NetId] = d;
                ApplyPose(bones, d.dispPos, d.dispRot);
                return;
            }

            // Subsequent packet: just update the smoothing target.
            if (d.targetPos.Length != n)
                return;
            d.lastPacketTime = Time.time;
            for (int i = 0; i < n; i++)
            {
                d.targetPos[i] = p.Positions[i];
                d.targetRot[i] = p.Rotations[i];
            }
        }

        // Per FRAME (FikaSyncPlugin.LateUpdate): ease each driven body's displayed pose toward the latest
        // packet and stamp it onto the bone transforms. Same exponential smoothing as BodyDragApply.
        public static void RenderSmoothing()
        {
            if (dragging.Count == 0)
                return;
            float rate = FikaVrSync.bodyDragSmoothRate;
            float t = rate > 0f ? 1f - Mathf.Exp(-rate * Time.deltaTime) : 1f;

            List<int> dead = null;
            List<Remote> stale = null;
            foreach (var kv in dragging)
            {
                Remote d = kv.Value;
                if (d.player == null || d.ragdoll == null || d.bones == null
                    || d.bones.Length != d.dispPos.Length)
                {
                    (dead ?? (dead = new List<int>())).Add(kv.Key);
                    continue;
                }
                // Teammate got revived mid-drag -> stop driving (FIKA re-enables their animators).
                if (d.player.HealthController != null && d.player.HealthController.IsAlive)
                {
                    (dead ?? (dead = new List<int>())).Add(kv.Key);
                    continue;
                }
                // No packet for too long (dragger dropped mid-drag, no Released arrived) -> stop driving it
                if (Time.time - d.lastPacketTime > FikaVrSync.bodyDragStaleTimeout)
                {
                    (dead ?? (dead = new List<int>())).Add(kv.Key);
                    (stale ?? (stale = new List<Remote>())).Add(d);
                    continue;
                }
                for (int i = 0; i < d.dispPos.Length; i++)
                {
                    d.dispPos[i] = Vector3.Lerp(d.dispPos[i], d.targetPos[i], t);
                    d.dispRot[i] = Quaternion.Slerp(d.dispRot[i], d.targetRot[i], t);
                }
                ApplyPose(d.bones, d.dispPos, d.dispRot);
            }
            if (dead != null)
                foreach (int k in dead)
                    dragging.Remove(k);
            if (stale != null)
                foreach (Remote d in stale)
                    Freeze(d.ragdoll, d.bones); // dragger dropped mid-drag -> freeze at last pose
        }

        // Stamp each bone's world transform from the synced pose. The skinned mesh samples bone transforms
        // at render time, so setting them in LateUpdate shows the dragged pose; bones are kinematic
        // (FreezeKinematic) so physics doesn't also move them. World poses, so bone order doesn't matter.
        private static void ApplyPose(RigidbodySpawner[] bones, Vector3[] pos, Quaternion[] rot)
        {
            int n = Mathf.Min(bones.Length, pos.Length);
            for (int i = 0; i < n; i++)
            {
                Transform tr = bones[i] != null ? bones[i].transform : null;
                if (tr == null)
                    continue;
                tr.SetPositionAndRotation(pos[i], rot[i]);
            }
        }

        // Freeze all bones kinematic so our transform stamps stick. Unsupport first (EFT's GClass745 sets
        // velocity on supported bodies each step -> "velocity of kinematic body not supported" spam),
        // mirroring RagdollClass.method_1 / the local owner's BeginBodyGrab.
        private static void FreezeKinematic(RigidbodySpawner[] bones)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody rb = bones[i] != null ? bones[i].Rigidbody : null;
                if (rb == null)
                    continue;
                PhysicsExtensions.UpdateController.UnsupportRigidbody(rb);
                rb.isKinematic = true;
                rb.detectCollisions = true;
            }
        }

        // Freeze the body kinematic at its current pose (mirrors RagdollClass.method_1) + restore Func_0 so any
        // still-running settle coroutine exits. Keeps a released downed teammate where dropped, as the dragger does.
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

        private static CorpseRagdoll ResolveRagdoll(FikaPlayer player)
        {
            if (player == null)
                return null;
            ReviveInteractable ri = player.gameObject.GetComponent<ReviveInteractable>();
            return ri != null ? ri._ragdoll : null;
        }
    }
}
