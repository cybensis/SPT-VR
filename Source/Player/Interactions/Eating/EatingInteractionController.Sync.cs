using System.Collections.Generic;
using TarkovVR.Source.Player.VRManager;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // FIKA co-op sync — SEND side data gathering. Kept FIKA-agnostic (no Fika/module types here, so
    // it JITs without FIKA): it only fills buffers with the rendered wrist poses + every live prop's
    // pose/visibility in the local CHEST frame. VRArmSync (FIKA-gated) reads these and ships them via
    // the module, which re-finds the matching prop on each observer's copy of the food model and
    // overrides its world pose so the food rides the synced hands. See [[eating-sync-plan]].
    internal static partial class EatingInteractionController
    {
        // One local eater at a time -> static reusable buffers (cleared each build, never shrunk).
        public static readonly List<string> syncNames = new List<string>(24);
        public static readonly List<Vector3> syncPos = new List<Vector3>(24);
        public static readonly List<Quaternion> syncRot = new List<Quaternion>(24);
        public static readonly List<bool> syncVis = new List<bool>(24);
        // Food animator state per layer (the skinned-deformation seek targets).
        public static readonly List<int> syncAnimHashes = new List<int>(8);
        public static readonly List<float> syncAnimTimes = new List<float>(8);
        // Log the eater's per-layer food-animator state (~1/s) so a food whose eat lives on an
        // unexpected layer is visible. Toggle in UnityExplorer while eating.
        public static bool debugEatSync = false;

        // Fill the buffers from the current eat, expressed in `chest`'s frame. Returns false when not
        // actively eating or the rig isn't ready (caller then sends nothing / a stop). The wrists are
        // the rendered IK hand bones (NOT the raw controllers) so a pull-latched hand on the item is
        // carried correctly. Visibility mirrors the local renderer-enabled state so observers hide
        // un-taken pieces / un-poured crackers the same way.
        public static bool TryBuildSync(Transform chest,
            out Vector3 lPos, out Quaternion lRot, out Vector3 rPos, out Quaternion rRot)
        {
            lPos = rPos = Vector3.zero; lRot = rRot = Quaternion.identity;
            syncAnimHashes.Clear(); syncAnimTimes.Clear();
            if (!ManualActive || chest == null) return false;
            Transform lw = VRGlobals.ikManager?.leftArmIk?.solver?.bone3?.transform;
            Transform rw = VRGlobals.ikManager?.rightArmIk?.solver?.bone3?.transform;
            if (lw == null || rw == null) return false;

            Quaternion invChest = Quaternion.Inverse(chest.rotation);
            lPos = chest.InverseTransformPoint(lw.position); lRot = invChest * lw.rotation;
            rPos = chest.InverseTransformPoint(rw.position); rRot = invChest * rw.rotation;

            // The food animator's state on EVERY layer (hash + normalized time) — whatever's driving the
            // food's own skinned deformation right now. Observers seek their copy of the food animator to
            // these so the lid/rip/chew matches, instead of staying frozen at spawn. All layers because a
            // food's eat can live on any of them; debugEatSync logs them so a missing one is visible.
            try
            {
                var fa = controller?.FirearmsAnimator;
                if (fa != null)
                {
                    int n = Mathf.Min(fa.AnimatorLayersCount, 8);
                    for (int i = 0; i < n; i++)
                    {
                        var si = fa.Animator.GetCurrentAnimatorStateInfo(i);
                        syncAnimHashes.Add(si.fullPathHash);
                        syncAnimTimes.Add(si.normalizedTime);
                    }
                    if (debugEatSync && Time.frameCount % 60 == 0)
                    {
                        var sb = new System.Text.StringBuilder("[ManualEat] food anim layers: ");
                        for (int i = 0; i < n; i++) sb.Append($"L{i}={syncAnimHashes[i]}@{syncAnimTimes[i]:F2} ");
                        Plugin.MyLog.LogInfo(sb.ToString());
                    }
                }
            }
            catch { syncAnimHashes.Clear(); syncAnimTimes.Clear(); }

            syncNames.Clear(); syncPos.Clear(); syncRot.Clear(); syncVis.Clear();
            // baseT/cover/wrapper are always-visible (never hidden locally); the piece props carry
            // their live renderer state. Order doesn't matter — the observer depth-sorts.
            AddProp(chest, invChest, baseT, true);
            AddProp(chest, invChest, spoonT, spoonR == null || spoonR.enabled);
            AddProp(chest, invChest, foodT, foodR == null || foodR.enabled);
            AddProp(chest, invChest, foodT2, foodR2 == null || foodR2.enabled);
            AddProp(chest, invChest, capT, capR == null || capR.enabled);
            AddProp(chest, invChest, coverT, true);
            AddProp(chest, invChest, wrapperT, true);
            if (crackerT != null)
                for (int i = 0; i < crackerT.Length; i++)
                {
                    bool vis = crackerR != null && i < crackerR.Length && crackerR[i] != null ? crackerR[i].enabled : true;
                    AddProp(chest, invChest, crackerT[i], vis);
                }
            return true;
        }

        private static void AddProp(Transform chest, Quaternion invChest, Transform t, bool vis)
        {
            if (t == null) return;
            syncNames.Add(t.name);
            syncPos.Add(chest.InverseTransformPoint(t.position));
            syncRot.Add(invChest * t.rotation);
            syncVis.Add(vis);
        }

        // True when `sp` is OUR active eat's sound player — i.e. a sound that passes AllowSound on it
        // is one we actually played and want observers to hear. Used by the OnSound gate to forward
        // the real gesture audio (observers freeze out the looping vanilla observed-meds sound).
        public static bool IsOwnEatSound(BaseSoundPlayer sp) => active && !manualDone && sp != null && sp == soundPlayer;
    }
}
