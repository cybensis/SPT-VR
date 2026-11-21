using System;
using System.Collections.Generic;
using EFT.Interactive;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receives and shows a downed (revive-state) teammate being dragged. Mirrors BodyDragApply, but a downed teammate
    // is not a Corpse: it is a live FikaPlayer, keyed by NetId. A packet is applied in one of two ways:
    //   (1) On the downed player's own client (NetId == my player), the player is moved to RootPos, because their
    //       authoritative position lives only on their client. The spot from the release packet is applied at revive,
    //       or every packet is applied if downedRelocateContinuous is set. Only while downed (IsAlive == false in FIKA's
    //       downed state), so a late packet can't move a revived player.
    //   (2) Every other client drives its copy of the revive-ragdoll bones to the synced world pose (all bones, smoothed,
    //       like BodyDragApply). The ragdoll is FIKA's ReviveInteractable._ragdoll; if it can't be found, the visual is
    //       skipped but (1) still works. This only matters in raids of 3+ players.
    // The caller wraps the handlers in try/catch, because a throw would drop the rest of FIKA's datagram. RenderSmoothing
    // runs every frame from FikaSyncPlugin's LateUpdate.
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

        // The drop spot to move the local downed player to. It is applied at revive (OnLocalRevived), not on release:
        // teleporting the root while downed drags the separate ragdoll bones past the drop spot on observers. At
        // revive the animator re-poses the bones from the root anyway.
        private static bool _hasPendingRevivePos;
        private static Vector3 _pendingRevivePos;

        public static void ResetState()
        {
            dragging.Clear();
            _hasPendingRevivePos = false;
        }

        // Called from the revive patch when the local player is revived. Moves the player to the saved drop spot.
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

            // (1) This client is the downed player being dragged: move the local player.
            if (player.IsYourPlayer)
            {
                bool downed = player.HealthController != null && !player.HealthController.IsAlive;
                if (!downed || !FikaVrSync.relocateDownedTeammate)
                    return;
                if (FikaVrSync.downedRelocateContinuous)
                {
                    // Teleport on every packet. This overshoots on observers (the root moves while the bones
                    // are separate), so it is for testing only.
                    player.Teleport(p.RootPos);
                }
                else if (p.Released)
                {
                    // Remember the drop spot and apply it at revive
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
                    // Freeze at the last synced pose, as the dragger does on release. EFT never settles a downed
                    // ragdoll by itself, so a dynamic one would slide off the drop spot the player is moved to at revive.
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
                    return; // this body's ragdoll can't be driven; the move in (1) still handles the gameplay

                // Make the ragdoll kinematic and keep it from settling or being torn down while it is driven.
                // The smoothing starts at this pose, so the first frame doesn't jump.
                FreezeKinematic(bones);
                ragdoll._keepRigidbody = true;        // don't tear the rigidbodies down while held
                ragdoll._checkCorpseIsStill = NeverFreeze; // keep FIKA's settle predicate from freezing it while driven
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

            // Later packets only update the smoothing target.
            if (d.targetPos.Length != n)
                return;
            d.lastPacketTime = Time.time;
            for (int i = 0; i < n; i++)
            {
                d.targetPos[i] = p.Positions[i];
                d.targetRot[i] = p.Rotations[i];
            }
        }

        // Every frame (FikaSyncPlugin.LateUpdate): eases each driven body's shown pose toward the latest packet
        // and writes it onto the bones. Same exponential smoothing as BodyDragApply.
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
                // The teammate was revived mid-drag: stop driving (FIKA re-enables their animators).
                if (d.player.HealthController != null && d.player.HealthController.IsAlive)
                {
                    (dead ?? (dead = new List<int>())).Add(kv.Key);
                    continue;
                }
                // No packet for too long (the dragger stopped sending without a Released packet): stop driving it
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
                    Freeze(d.ragdoll, d.bones); // freeze at the last pose
        }

        // Writes each bone's world pose from the synced pose. The skinned mesh reads the bones at render time, so
        // setting them in LateUpdate shows the dragged pose. The bones are kinematic (FreezeKinematic), so physics
        // doesn't move them too. The poses are in world space, so the bone order doesn't matter.
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

        // Makes all bones kinematic so the written poses stick, like RagdollClass.method_1 and the local BeginBodyGrab.
        // Each body is unsupported first: EFT's GClass745 sets the velocity of supported bodies every step, which
        // spams "velocity of kinematic body not supported".
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

        // Freezes the body kinematic at its current pose (like RagdollClass.method_1) and sets _checkCorpseIsStill to
        // AlwaysFreeze, so a settle coroutine still running exits. A released downed teammate stays where it was dropped.
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
