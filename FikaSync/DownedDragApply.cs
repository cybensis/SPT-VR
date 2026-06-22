using System;
using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receiver / renderer for a DOWNED (revive-state) teammate being dragged. Mirrors BodyDragApply, but
    // a downed teammate is NOT a Corpse — it's a still-live FikaPlayer keyed by NetId. So a packet is
    // applied two completely different ways depending on who receives it:
    //
    //   (1) The DOWNED player's OWN client (NetId == my player): teleport MYSELF to RootPos. This is what
    //       actually relocates the teammate — their authoritative position lives only on their client, so
    //       nothing the dragger does locally can move it. Done on the reliable release marker (the final
    //       safe spot) by default, or every packet if downedRelocateContinuous. Guarded to fire only while
    //       genuinely downed (IsAlive == false in FIKA's downed state) so a stray/late packet can't yank a
    //       revived player.
    //
    //   (2) Any OTHER client (an observer of the downed teammate): drive their copy of the revive-ragdoll
    //       bones to the synced world pose so they SEE the drag — same all-bones, smoothed, settle-on-
    //       release model as BodyDragApply. The ragdoll lives on FIKA's internal ReviveInteractable
    //       component (private _ragdoll), reached by reflection; if that can't be resolved the observer
    //       visual is skipped but (1) still works.
    //
    // In a 2-player raid there's no third observer, so (1) alone fully delivers "drag to safety": the
    // dragger sees it via their own local drag, the downed teammate is relocated, and the revive lands at
    // the safe spot. (2) is the visual for 3+ player raids.
    //
    // Handlers are wrapped in try/catch upstream (a throw would drop the rest of FIKA's datagram), and the
    // Unity/reflection calls here are guarded too. RenderSmoothing runs per FRAME (LateUpdate); the settle
    // manager runs per physics step (FixedUpdate) — both pumped from FikaSyncPlugin.
    internal static class DownedDragApply
    {
        private static readonly Func<bool, float, bool> NeverFreeze = (a, t) => false;
        private static readonly Func<bool, float, bool> AlwaysFreeze = (a, t) => true;

        private sealed class Remote
        {
            public FikaPlayer player;
            public RagdollClass ragdoll;
            public RigidbodySpawner[] bones;
            public Vector3[] targetPos;
            public Quaternion[] targetRot;
            public Vector3[] dispPos;
            public Quaternion[] dispRot;
            public float lastPacketTime;
        }
        private static readonly Dictionary<int, Remote> dragging = new Dictionary<int, Remote>();

        // The drop spot to relocate the LOCAL downed player to — applied at REVIVE, not on release. (See
        // OnLocalRevived. Teleporting the root while downed drags the decoupled ragdoll bones past the
        // drop spot on observers; the animator re-poses the bones from the root at revive anyway, so
        // teleporting then is clean.)
        private static bool _hasPendingRevivePos;
        private static Vector3 _pendingRevivePos;

        public static void ResetState()
        {
            dragging.Clear();
            _hasPendingRevivePos = false;
        }

        // Called from the revive patch when the LOCAL player is revived: apply the deferred drop-spot
        // relocation now (root + animator re-pose happen together, so no decoupled-bone overshoot).
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
                    // A/B path: teleport immediately (every packet). This reproduces the observer overshoot
                    // (root moves while bones are decoupled) — kept only for testing.
                    player.Teleport(p.RootPos);
                }
                else if (p.Released)
                {
                    // Default: DON'T teleport now. Remember the drop spot and apply it at revive
                    // (FikaPlayer_ToggleDowned_RevivePatch -> OnLocalRevived), when the animator re-poses the
                    // ragdoll from the root anyway, so the body lands cleanly with no decoupled-bone overshoot.
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
                    // Freeze in place at the last synced pose — mirrors the dragger, who freezes the body
                    // kinematic on release (a downed ragdoll is never auto-settled by EFT, so letting it go
                    // dynamic would snap it off the drop spot). Stays where dropped == the relocate spot.
                    Freeze(rr.ragdoll, rr.bones);
                }
                return;
            }

            int n = p.BoneCount;
            if (n == 0 || p.Positions == null || p.Rotations == null)
                return;

            if (!dragging.TryGetValue(p.NetId, out Remote d))
            {
                RagdollClass ragdoll = ResolveRagdoll(player);
                RigidbodySpawner[] bones = ragdoll != null ? ragdoll.RigidbodySpawner_0 : null;
                if (ragdoll == null || bones == null || bones.Length != n)
                    return; // can't resolve/drive this body — relocation (1) still handles the gameplay

                // Freeze the ragdoll kinematic + keep it from settling/tearing down while we drive it, then
                // seed the smoothing (displayed = target = this pose, so no first-frame jump).
                FreezeKinematic(bones);
                ragdoll.Bool_0 = true;        // keepRigidbody: don't tear it down while held
                ragdoll.Func_0 = NeverFreeze; // stop FIKA's settle predicate freezing it under us
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
                // Safety net: no packet for too long (dragger dropped mid-drag, no Released arrived) ->
                // release locally so the body falls instead of staying frozen.
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
        // (FreezeKinematic) so physics doesn't also move them. World poses, so bone order within the
        // hierarchy doesn't matter for the final frame.
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

        // Freeze all bones kinematic so our transform stamps stick. UNSUPPORT first (EFT's GClass745 sets
        // velocity on supported bodies each step -> "velocity of kinematic body not supported" spam),
        // mirroring RagdollClass.method_1 / the local owner's BeginBodyGrab.
        private static void FreezeKinematic(RigidbodySpawner[] bones)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody rb = bones[i] != null ? bones[i].Rigidbody : null;
                if (rb == null)
                    continue;
                EFTPhysicsClass.GClass745.UnsupportRigidbody(rb);
                rb.isKinematic = true;
                rb.detectCollisions = true;
            }
        }

        // Freeze the body kinematic in place at its current pose (mirrors RagdollClass.method_1) + restore
        // Func_0 so any still-spinning settle coroutine exits cleanly. This is what keeps a released downed
        // teammate exactly where dropped (the dragger does the same locally).
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

        private static RagdollClass ResolveRagdoll(FikaPlayer player)
        {
            if (player == null)
                return null;
            ReviveInteractable ri = player.gameObject.GetComponent<ReviveInteractable>();
            return ri != null ? ri._ragdoll : null;
        }
    }
}
