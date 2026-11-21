using System.Collections.Generic;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // FIKA co-op eating sync, the sending side's data. No FIKA or module types are used here, so it compiles without FIKA.
    // It fills buffers with the rendered wrist poses and every prop's pose and visibility in the local body frame (Player.Transform).
    // VRArmSync (only with FIKA) sends them through the module, which finds the matching prop on each observer's copy of the
    // food model and overrides its world pose.
    internal static partial class EatingInteractionController
    {
        // There is only one local eater at a time, so the buffers are static and reused (cleared each build, never shrunk).
        public static readonly List<string> syncNames = new List<string>(24);
        public static readonly List<Vector3> syncPos = new List<Vector3>(24);
        public static readonly List<Quaternion> syncRot = new List<Quaternion>(24);
        public static readonly List<bool> syncVis = new List<bool>(24);
        // Food animator state per layer (what observers seek to for the skinned deformation).
        public static readonly List<int> syncAnimHashes = new List<int>(8);
        public static readonly List<float> syncAnimTimes = new List<float>(8);
        // Logs the local food animator's state per layer (~1/s), to spot a food whose eat is on an unexpected layer
        public static bool debugEatSync = false;

        // Fills the buffers from the current eat, relative to `frame` (the body). Returns false when not eating or when the rig isn't
        // ready (the caller then sends nothing or a stop). Wrists are the rendered IK hand bones, not the raw controllers, so a
        // hand latched for a pull-open is sent where it is drawn. Visibility copies the local renderer state (pieces not yet taken
        // and crackers not yet poured stay hidden).
        public static bool TryBuildSync(Transform frame,
            out Vector3 lPos, out Quaternion lRot, out Vector3 rPos, out Quaternion rRot)
        {
            lPos = rPos = Vector3.zero; lRot = rRot = Quaternion.identity;
            syncAnimHashes.Clear(); syncAnimTimes.Clear();
            if (!ManualActive || frame == null) return false;
            Transform lw = VRGlobals.ikManager?.leftArmIk?.solver?.bone3?.transform;
            Transform rw = VRGlobals.ikManager?.rightArmIk?.solver?.bone3?.transform;
            if (lw == null || rw == null) return false;

            Quaternion invFrame = Quaternion.Inverse(frame.rotation);
            lPos = frame.InverseTransformPoint(lw.position); lRot = invFrame * lw.rotation;
            rPos = frame.InverseTransformPoint(rw.position); rRot = invFrame * rw.rotation;

            // The food animator's state on every layer (hash and normalized time), which drives the food's skinned deformation.
            // Observers seek their copy to these so the lid, rip and chew match. All layers, because a food's eat can be on any of them.
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
            // baseT, cover and wrapper are never hidden locally; the piece props send their current renderer state. The order
            // doesn't matter (the observer sorts by depth).
            AddProp(frame, invFrame, baseT, true);
            AddProp(frame, invFrame, spoonT, spoonR == null || spoonR.enabled);
            AddProp(frame, invFrame, foodT, foodR == null || foodR.enabled);
            AddProp(frame, invFrame, foodT2, foodR2 == null || foodR2.enabled);
            AddProp(frame, invFrame, capT, capR == null || capR.enabled);
            AddProp(frame, invFrame, coverT, true);
            AddProp(frame, invFrame, wrapperT, true);
            if (crackerT != null)
                for (int i = 0; i < crackerT.Length; i++)
                {
                    bool vis = crackerR != null && i < crackerR.Length && crackerR[i] != null ? crackerR[i].enabled : true;
                    AddProp(frame, invFrame, crackerT[i], vis);
                }
            return true;
        }

        private static void AddProp(Transform frame, Quaternion invFrame, Transform t, bool vis)
        {
            if (t == null) return;
            syncNames.Add(t.name);
            syncPos.Add(frame.InverseTransformPoint(t.position));
            syncRot.Add(invFrame * t.rotation);
            syncVis.Add(vis);
        }

        // True when `sp` is the sound player of the active manual eat, so a sound passing AllowSound on it is one this mod played.
        // The OnSound gate forwards it to observers, who mute the vanilla observed-meds sound.
        public static bool IsOwnEatSound(BaseSoundPlayer sp) => active && !manualDone && sp != null && sp == soundPlayer;
    }
}
