using System;
using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Players;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receive + render a remote VR player's MANUAL EATING, in the ObservedPlayer.LateUpdate postfix
    // (after FIKA's observed-meds visual pass + the food animator). Two halves, both fed from one
    // VREatingPacket so they can't desync:
    //   1) the ARMS are driven to the sender's rendered wrist poses (exactly like empty-hands sync),
    //   2) every live food PROP is matched on the observer's copy of the food model (same prefab ->
    //      same bone names, matched by a stable name hash) and its world pose + renderer visibility
    //      overridden to the synced values.
    // Because the food is placed from the same synced frame as the hands (not parented to them), the
    // food and hands stay together without re-running the local gesture machine observer-side, and
    // without fighting the vanilla observed-meds animation (we override its result each frame).
    //
    // Runs on ANY peer with this module (incl. flatscreen/headless), so everyone sees the VR eating.
    [HarmonyPatch]
    internal static class EatingSyncApply
    {
        // ---- live tunables ----
        public static float smoothRate = 18f;       // chest-local pose smoothing (0 = snap). Matches arm/weapon sync.
        public static float staleTimeout = 0.6f;    // no packet this long -> restore + stop (disconnect/finish safety)
        // Freeze the observed food animator while synced — exactly like the local controller does at
        // spawn. Without it FIKA's observed-meds animation runs free: the food's own bones loop AND its
        // clip sound events fire on a loop. We override the prop world poses anyway (the moving parts),
        // and forward the real gesture sounds, so the food sits in the synced pose, no loop, no loop
        // audio. false = leave FIKA's looping animation (the old middle-ground).
        public static bool freezeObservedAnimator = true;
        // Mirror the eater's food-animator base-layer state (a SEEK, frozen) so the food's own skinned
        // deformation (lid bone opening, bag rip, chew squash) matches the eater instead of sitting at
        // the spawn pose. Needs freezeObservedAnimator (the seek + speed 0 = a held pose, no loop).
        // false = just freeze at spawn (the simpler middle-ground if the seek ever looks glitchy).
        public static bool syncFoodAnimatorState = true;
        public static bool debug = false;

        private struct PropPose { public Vector3 pos; public Quaternion rot; public bool vis; }

        private sealed class EatState
        {
            public bool active;
            public float recvTime;
            // latest received (chest-local)
            public Vector3 lPos, rPos; public Quaternion lRot, rRot;
            public int animLayerCount; public int[] animHashes; public float[] animTimes; // food animator per-layer seek targets
            public readonly Dictionary<int, PropPose> latest = new Dictionary<int, PropPose>();
            // displayed (smoothed)
            public bool hasDisp; public Vector3 dl, dr; public Quaternion dlr, drr;
            public readonly Dictionary<int, PropPose> disp = new Dictionary<int, PropPose>();
            // resolution cache (cleared when the controller object changes)
            public GameObject ctrl;
            public readonly Dictionary<int, Transform> tf = new Dictionary<int, Transform>();
            public readonly Dictionary<int, Renderer> rend = new Dictionary<int, Renderer>();
            public readonly HashSet<Renderer> hidden = new HashSet<Renderer>(); // renderers WE disabled (to restore)
            // animator freeze + forwarded eat sounds
            public Player.MedsController meds;                       // current observed controller (to unfreeze on stop)
            public BaseSoundPlayer sound;                            // observed food sound player (for forwarded sounds)
            public readonly List<int> pendingSounds = new List<int>(8); // queued by name-hash, drained in Apply
        }

        private static readonly Dictionary<int, EatState> states = new Dictionary<int, EatState>();

        // Observed food sound players whose OWN events we mute (FIKA's looping observed-meds events AND
        // the per-frame animator-seek's clip events — both spam / mis-timed). Only our forwarded gesture
        // sounds get through (played with playingForwarded set). Runs on every peer incl. flatscreen.
        private static readonly HashSet<BaseSoundPlayer> suppressed = new HashSet<BaseSoundPlayer>();
        private static bool playingForwarded;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "OnSound")]
        private static bool MuteObservedEatSound(BaseSoundPlayer __instance)
        {
            try { if (!playingForwarded && __instance != null && suppressed.Contains(__instance)) return false; }
            catch { }
            return true;
        }

        public static void ResetState()
        {
            foreach (var s in states.Values) RestoreHidden(s);
            states.Clear();
            suppressed.Clear();
            playingForwarded = false;
        }

        // Packet handler thread == FIKA's read loop, which runs on the main thread (the existing
        // arm/body sync touches Time.time / Unity here too), so renderer restores are safe.
        public static void Store(VREatingPacket p)
        {
            if (!states.TryGetValue(p.NetId, out EatState s)) { s = new EatState(); states[p.NetId] = s; }
            if (!p.Active) { Deactivate(s); return; }
            s.active = true;
            s.recvTime = Time.time;
            s.lPos = p.LeftPos; s.lRot = p.LeftRot; s.rPos = p.RightPos; s.rRot = p.RightRot;
            s.animLayerCount = p.AnimLayerCount; s.animHashes = p.AnimHashes; s.animTimes = p.AnimTimes;
            s.latest.Clear();
            int n = p.PropCount;
            for (int i = 0; i < n; i++)
                s.latest[p.NameHashes[i]] = new PropPose { pos = p.Positions[i], rot = p.Rotations[i], vis = p.Visible[i] };
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ObservedPlayer), nameof(ObservedPlayer.LateUpdate))]
        private static void Apply(ObservedPlayer __instance)
        {
            try
            {
                if (!FikaVrSync.enableEatingSync)
                    return;
                if (!states.TryGetValue(__instance.NetId, out EatState s) || !s.active)
                    return;
                if (__instance.IsAI || __instance.HealthController == null || !__instance.HealthController.IsAlive)
                {
                    Deactivate(s);
                    return;
                }
                // Lost the stream (disconnect, or a finish whose stop packet dropped) -> let go.
                if (Time.time - s.recvTime > staleTimeout)
                {
                    Deactivate(s);
                    return;
                }
                // Not eating anymore (controller changed) -> let go; FIKA resumes whatever's in hand.
                if (!(__instance.HandsController is Player.MedsController meds))
                {
                    Deactivate(s);
                    return;
                }
                Transform chest = __instance.PlayerBones?.Ribcage?.Original;
                if (chest == null)
                    return;
                GameObject co = meds.ControllerGameObject;
                if (co == null)
                    return;
                if (s.ctrl != co) // pooled/respawned model -> rebuild cache
                {
                    if (s.sound != null) suppressed.Remove(s.sound);
                    ResetResolve(s);
                    s.ctrl = co;
                    s.sound = co.GetComponentInChildren<BaseSoundPlayer>(true);
                    if (s.sound != null) suppressed.Add(s.sound); // mute its OWN events; only forwarded sounds play
                }
                s.meds = meds;

                // Stop FIKA's looping observed-meds animation. Either seek the food animator to the
                // eater's current state (mirrors the skinned deformation, frozen so it can't loop) or
                // just freeze it at spawn. The seek's forced evaluation (SkipTime below) DOES re-fire the
                // clip's sound events, so this food's sound player is muted (suppressed set) and only the
                // forwarded gesture sounds play. The prop world overrides below place the moving parts.
                if (freezeObservedAnimator)
                {
                    try
                    {
                        var fa = meds.FirearmsAnimator;
                        if (fa != null)
                        {
                            if (syncFoodAnimatorState && s.animHashes != null && fa.AnimatorLayersCount > 0)
                            {
                                int n = Mathf.Min(s.animLayerCount, fa.AnimatorLayersCount);
                                for (int i = 0; i < n; i++)
                                    if (s.animHashes[i] != 0) fa.Animator.Play(s.animHashes[i], i, s.animTimes[i]);
                                fa.SetAnimationSpeed(0f);
                                // CRITICAL: at speed 0 a Play() is never evaluated on its own, so the
                                // seek wouldn't apply. Force one zero-length evaluation so the food jumps
                                // to (and holds at) the eater's pose. A 0 step crosses no event frames, so
                                // no clip sounds fire (audio is forwarded separately).
                                fa.SkipTime(0f);
                            }
                            else
                            {
                                fa.SetAnimationSpeed(0f); // plain freeze-at-spawn fallback
                            }
                        }
                    }
                    catch { }
                }

                DriveArms(__instance, s, chest);
                DriveProps(s, co.transform, chest);
                DrainSounds(s);
            }
            catch (Exception e) { LogOnce(e); }
        }

        // Drive both observed arm IKs to the synced (smoothed) wrist poses — same machinery as
        // empty-hands sync; the held food piece is placed separately so the hand reaches it.
        private static void DriveArms(ObservedPlayer p, EatState s, Transform chest)
        {
            LimbIK[] limbs = p._observedLimbs;
            if (limbs == null || limbs.Length < 2)
                return;
            float t = smoothRate > 0f ? 1f - Mathf.Exp(-smoothRate * Time.deltaTime) : 1f;
            if (!s.hasDisp) { s.dl = s.lPos; s.dlr = s.lRot; s.dr = s.rPos; s.drr = s.rRot; s.hasDisp = true; }
            s.dl = Vector3.Lerp(s.dl, s.lPos, t); s.dlr = Quaternion.Slerp(s.dlr, s.lRot, t);
            s.dr = Vector3.Lerp(s.dr, s.rPos, t); s.drr = Quaternion.Slerp(s.drr, s.rRot, t);
            Quaternion lr = chest.rotation * s.dlr * Quaternion.Euler(ArmSyncApply.leftHandRotOffsetEuler);
            Quaternion rr = chest.rotation * s.drr * Quaternion.Euler(ArmSyncApply.rightHandRotOffsetEuler);
            ArmSyncApply.DriveArm(limbs[0], chest.TransformPoint(s.dl), lr);
            ArmSyncApply.DriveArm(limbs[1], chest.TransformPoint(s.dr), rr);
        }

        private static readonly List<KeyValuePair<int, Transform>> _ordered = new List<KeyValuePair<int, Transform>>();
        private static readonly HashSet<Renderer> _claimed = new HashSet<Renderer>();

        private static void DriveProps(EatState s, Transform root, Transform chest)
        {
            float t = smoothRate > 0f ? 1f - Mathf.Exp(-smoothRate * Time.deltaTime) : 1f;

            // Resolve every synced prop to a transform on the observed model.
            _ordered.Clear();
            foreach (var kv in s.latest)
            {
                Transform tf = Resolve(s, root, kv.Key);
                if (tf != null) _ordered.Add(new KeyValuePair<int, Transform>(kv.Key, tf));
            }

            // POSE: shallow-first so a parent's world override doesn't drag a child off (the props
            // we override are NOT reparented observer-side, so a glued cover IS a child of its
            // wrapper here — set the parent before the child).
            _ordered.Sort(CompareDepthAsc);
            for (int i = 0; i < _ordered.Count; i++)
            {
                int hash = _ordered[i].Key;
                Transform tf = _ordered[i].Value;
                PropPose target = s.latest[hash];
                if (!s.disp.TryGetValue(hash, out PropPose d)) d = target;
                d.pos = Vector3.Lerp(d.pos, target.pos, t);
                d.rot = Quaternion.Slerp(d.rot, target.rot, t);
                d.vis = target.vis;
                s.disp[hash] = d;
                tf.SetPositionAndRotation(chest.TransformPoint(d.pos), chest.rotation * d.rot);
            }

            // VISIBILITY: deep-first claiming, so a piece (deep) owns its mesh before its container
            // (shallow) — a container's GetComponentInChildren can otherwise alias a child piece's
            // renderer and re-show it. We only ever re-enable renderers we ourselves hid.
            _ordered.Sort(CompareDepthDesc);
            _claimed.Clear();
            for (int i = 0; i < _ordered.Count; i++)
            {
                int hash = _ordered[i].Key;
                Renderer r = ResolveRend(s, _ordered[i].Value, hash);
                if (r == null || _claimed.Contains(r)) continue;
                _claimed.Add(r);
                bool vis = s.latest[hash].vis;
                if (!vis)
                {
                    if (r.enabled) { r.enabled = false; s.hidden.Add(r); }
                }
                else if (s.hidden.Contains(r))
                {
                    r.enabled = true; s.hidden.Remove(r);
                }
            }
        }

        // ---- resolution ----
        private static Transform Resolve(EatState s, Transform root, int hash)
        {
            if (s.tf.TryGetValue(hash, out Transform t)) { if (t != null) return t; s.tf.Remove(hash); }
            Transform found = FindDeepByHash(root, hash);
            if (found != null) s.tf[hash] = found;
            return found;
        }

        private static Renderer ResolveRend(EatState s, Transform t, int hash)
        {
            if (s.rend.TryGetValue(hash, out Renderer r)) { if (r != null) return r; s.rend.Remove(hash); }
            Renderer found = t.GetComponentInChildren<Renderer>(true);
            if (found != null) s.rend[hash] = found;
            return found;
        }

        // DFS pre-order first match — mirrors the local FindDeep so duplicate-named rigs resolve the
        // SAME node both sides (same prefab, same traversal).
        private static Transform FindDeepByHash(Transform root, int hash)
        {
            if (root == null) return null;
            if (FikaVrSync.StableHash(root.name) == hash) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeepByHash(root.GetChild(i), hash);
                if (found != null) return found;
            }
            return null;
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            Transform p = t;
            while (p != null && d < 64) { p = p.parent; d++; }
            return d;
        }

        private static int CompareDepthAsc(KeyValuePair<int, Transform> a, KeyValuePair<int, Transform> b)
            => Depth(a.Value).CompareTo(Depth(b.Value));
        private static int CompareDepthDesc(KeyValuePair<int, Transform> a, KeyValuePair<int, Transform> b)
            => Depth(b.Value).CompareTo(Depth(a.Value));

        // ---- forwarded eat sounds ----
        // Queued by the network handler (NetId + name-hash), drained in Apply (where we have the
        // observed sound player). Reverse-resolved against the food model's own sound bank (same
        // prefab -> same event names) and played by event name — positioned at the eater.
        public static void QueueSound(int netId, int nameHash)
        {
            if (!states.TryGetValue(netId, out EatState s)) { s = new EatState(); states[netId] = s; }
            if (s.pendingSounds.Count < 16) s.pendingSounds.Add(nameHash);
        }

        private static void DrainSounds(EatState s)
        {
            if (s.pendingSounds.Count == 0) return;
            if (s.sound != null)
            {
                playingForwarded = true; // let our forwarded sounds past the mute gate above
                try
                {
                    for (int i = 0; i < s.pendingSounds.Count; i++)
                        PlayByHash(s.sound, s.pendingSounds[i]);
                }
                finally { playingForwarded = false; }
            }
            s.pendingSounds.Clear();
        }

        // Find the sound-bank entry whose event name hashes to `hash` and play it. The sender hashed
        // the raw name it passed to OnSound (e.g. "Take"); the bank entry's EventName may be that name
        // OR "Snd"+name (mirrors EFT's OnSound resolution), so test both candidates per entry.
        private static void PlayByHash(BaseSoundPlayer sp, int hash)
        {
            var list = sp.AdditionalSounds;
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null || string.IsNullOrEmpty(e.EventName)) continue;
                string name = e.EventName;
                if (FikaVrSync.StableHash(name) == hash
                    || (name.Length > 3 && name.StartsWith("Snd", StringComparison.Ordinal) && FikaVrSync.StableHash(name.Substring(3)) == hash))
                {
                    try { sp.OnSound(name); } catch { }
                    return;
                }
            }
        }

        // ---- teardown ----
        private static void Deactivate(EatState s)
        {
            RestoreHidden(s);
            // Un-freeze the food animator so FIKA's put-away / next state can play (the controller
            // usually gets destroyed right after, but don't leave it stuck at speed 0 if it lingers).
            if (s.meds != null) { try { s.meds.FirearmsAnimator?.SetAnimationSpeed(1f); } catch { } }
            if (s.sound != null) suppressed.Remove(s.sound); // un-mute (pooled sound players get reused)
            ResetResolve(s);
            s.active = false;
            s.hasDisp = false;
            s.meds = null;
            s.sound = null;
            s.latest.Clear();
            s.disp.Clear();
            s.pendingSounds.Clear();
        }

        private static void ResetResolve(EatState s)
        {
            s.tf.Clear();
            s.rend.Clear();
            s.ctrl = null;
        }

        private static void RestoreHidden(EatState s)
        {
            foreach (Renderer r in s.hidden)
                if (r != null) { try { r.enabled = true; } catch { } }
            s.hidden.Clear();
        }

        private static bool _err;
        private static void LogOnce(Exception e)
        {
            if (_err) return;
            _err = true;
            FikaSyncPlugin.Log.LogError($"[FikaSync] eating drive error: {e}");
        }
    }
}
