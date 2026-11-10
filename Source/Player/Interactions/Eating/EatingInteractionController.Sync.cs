using System.Collections.Generic;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // FIKA co-op eating sync, send-side data gathering. No FIKA/module types here (it JITs without FIKA): fills buffers with the
    // rendered wrist poses + every live prop's pose/visibility in the local chest frame. VRArmSync (FIKA-gated) ships them via the
    // module, which finds the matching prop on each observer's copy of the food model and overrides its world pose.
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
        // Log the eater's per-layer food-animator state (~1/s), so a food whose eat lives on an unexpected layer is visible
        public static bool debugEatSync = false;

        // Fills the buffers from the current eat, in `chest`'s frame. False when not eating or the rig isn't ready (the caller
        // sends nothing / a stop). Wrists are the rendered IK hand bones, not the raw controllers, so a pull-latched hand is
        // carried correctly. Visibility mirrors the local renderer state (un-taken pieces / un-poured crackers stay hidden).
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

            // The food animator's state on every layer (hash + normalized time), which drives the food's skinned deformation.
            // Observers seek their copy to these so the lid/rip/chew matches. All layers: a food's eat can live on any of them.
            try
            {
                var firearmsAnimator = controller?.FirearmsAnimator;
                if (firearmsAnimator != null)
                {
                    int layerCount = Mathf.Min(firearmsAnimator._animatorLayersCount, 8);
                    for (int i = 0; i < layerCount; i++)
                    {
                        var si = firearmsAnimator.Animator.GetCurrentAnimatorStateInfo(i);
                        syncAnimHashes.Add(si.fullPathHash);
                        syncAnimTimes.Add(si.normalizedTime);
                    }
                    if (debugEatSync && Time.frameCount % 60 == 0)
                    {
                        var builder = new System.Text.StringBuilder("[ManualEat] food anim layers: ");
                        for (int i = 0; i < layerCount; i++) builder.Append($"L{i}={syncAnimHashes[i]}@{syncAnimTimes[i]:F2} ");
                        Plugin.MyLog.LogInfo(builder.ToString());
                    }
                }
            }
            catch { syncAnimHashes.Clear(); syncAnimTimes.Clear(); }

            syncNames.Clear(); syncPos.Clear(); syncRot.Clear(); syncVis.Clear();
            // baseT/cover/wrapper are never hidden locally; the piece props carry their live renderer state. Order doesn't
            // matter (the observer depth-sorts).
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

        // True when `sp` is our active eat's sound player: a sound passing AllowSound on it is one we played. The OnSound gate
        // forwards it to observers (who suppress the looping vanilla observed-meds sound).
        public static bool IsOwnEatSound(BaseSoundPlayer sp) => active && !manualDone && sp != null && sp == soundPlayer;
    }
}
