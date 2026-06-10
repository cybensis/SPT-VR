using System;
using System.Text;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;
using static EFT.Player;

namespace TarkovVR.Patches.Core.Player
{
    // ===========================================================================
    // TEMPORARY RECON — profile a food's VANILLA eat to gather everything needed to
    // author a new FoodDef in EatingInteractionController. Self-contained and decoupled
    // from the manual-eating flow (works on foods that DON'T have a FoodDef yet, since
    // it just watches the normal eat). Delete this whole file when done.
    //
    // USAGE: set EatingRecon.enabled = true (Unity Explorer / UnityExplorer), then eat
    // the target food normally (let the vanilla animation play — do NOT have a FoodDef
    // for it, or it'll be gesture-gated). Read the [FoodRecon] lines in the BepInEx log.
    //
    // What you get:
    //  * [TREE]   — full transform tree under the controller object: prop bone names +
    //               renderer type/mesh/enabled. This is where rootName/spoonName/
    //               foodPieceName come from (and the "Base Human?Palm" bones for grips).
    //  * [SOUND]  — every sound event name + the arms-animator state hash & normalizedTime
    //               when it fired. Maps sounds -> phases (drawSound / openSounds / eatSound)
    //               AND reveals the STATE_* hashes + the times to hold/skip at.
    //  * [STATE]  — arms layer-1 state transitions (OPEN->USE->EAT->END) + normalizedTime.
    //  * [GRIP]   — set gripPropName (a prop bone name from [TREE], e.g. "saira_spoon")
    //               and gripPalmName (the hand holding it). Logs palm->prop pos/rot a few
    //               times/sec; read it at a moment the hand is holding the prop steady —
    //               that's the FoodDef grip offset.
    // ===========================================================================
    internal static class EatingRecon
    {
        public static bool enabled = true;            // master toggle — flip on live

        // Grip measurement: set to a prop bone name + the palm bone holding it.
        public static string gripPropName = "saira_root";        // e.g. "saira_spoon" / "saira_root"
        public static string gripPalmName = "Base HumanLPalm"; // or "Base HumanLPalm"
        public static float gripLogInterval = 0.25f;

        // Auto-averaged stable grip: every frame we sample palm->prop and find the longest
        // run where it barely moves (the prop sitting still in the palm), then print that run's
        // average as a paste-ready V(...)/V(...) at the end of the eat — no eyeballing the raw
        // list. Out-of-hand samples (prop in the can/bag, or mid-air to the mouth) are ignored.
        public static float gripStableMaxDist = 0.25f; // ignore samples this far from the palm (m)
        public static float gripStablePosTol = 0.02f;  // frame-to-frame pos move that still counts as "still" (m)
        public static float gripStableAngTol = 8f;      // frame-to-frame rot move that still counts as "still" (deg)
        public static int gripStableMinRun = 8;         // min frames for a run to be reported

        private static MedsController activeController;
        private static BaseSoundPlayer soundPlayer;
        private static int lastState;
        private static float nextGripLog;

        // Stable-grip run tracking.
        private static bool gripHaveLast;
        private static Vector3 gripLastPos; private static Quaternion gripLastRot;
        private static int gripRunCount; private static Vector3 gripRunPosSum;
        private static Vector4 gripRunRotSum; private static Quaternion gripRunFirstRot;
        private static int gripBestCount; private static Vector3 gripBestPos, gripBestRot;

        public static void OnSpawn(MedsController instance)
        {
            if (!enabled || instance == null) return;
            try
            {
                EFT.Player player = instance._player;
                if (player == null || !player.IsYourPlayer) return;
                if (!(instance.Item is FoodDrinkItemClass)) return;

                activeController = instance;
                lastState = 0;
                nextGripLog = 0f;
                gripHaveLast = false; gripRunCount = 0; gripBestCount = 0;
                soundPlayer = instance._controllerObject != null
                    ? instance._controllerObject.GetComponentInChildren<BaseSoundPlayer>(true)
                    : null;

                Plugin.MyLog.LogInfo($"[FoodRecon] ===== {instance.Item?.TemplateId} ({instance.Item?.ShortName}) =====");
                DumpTree(instance);
                SuggestEntry(instance);
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] OnSpawn error: {ex}"); }
        }

        // Guess the archetype + prop names from the tree and print a paste-ready factory
        // line for EatingInteractionController.Defs. Heuristics (match BSG naming): the
        // ROOT is the first direct child of 'weapon' that has a Renderer; a "*spoon*" child
        // => CanSpoon; a "*cover*"/"*_CAT*" child => Wrapper; otherwise CanHand. Grips/
        // timings are left to the archetype defaults — paste it, then tune live + DumpFoodDef.
        private static void SuggestEntry(MedsController instance)
        {
            try
            {
                Transform root = instance._controllerObject != null ? instance._controllerObject.transform : null;
                if (root == null) return;
                string id = instance.Item?.TemplateId;
                Transform weapon = FindDeep(root, "weapon");

                string rootName = FirstRendererChildName(weapon) ?? NameContaining(root, "_root");
                string spoon = NameContaining(root, "spoon");
                string food = NameContaining(root, "foodpiece") ?? NameContaining(root, "_food");
                string cover = NameContaining(root, "cover");
                string catGroup = NameContaining(root, "_cat");
                // Bag (pour-into-hand) cues: a packaging root + "hold" food pieces.
                string bagRoot = NameContaining(root, "upakovka");
                string holdPiece = NameContaining(root, "_hold_") ?? NameContaining(root, "suharik");

                string line;
                if (bagRoot != null && holdPiece != null)
                    line = $"Bag     (\"{id}\", \"{bagRoot}\", \"{StripTrailingIndex(holdPiece)}\"),";
                else if (cover != null || catGroup != null)
                    line = $"Wrapper (\"{id}\", \"{rootName ?? "ROOT?"}\", \"{catGroup ?? "WRAPPER_GROUP?"}\", \"{cover ?? "COVER?"}\"),";
                else if (spoon != null)
                    line = $"CanSpoon(\"{id}\", \"{rootName ?? "ROOT?"}\", \"{spoon}\", \"{food ?? "FOODPIECE?"}\"),";
                else
                    line = $"CanHand (\"{id}\", \"{rootName ?? "ROOT?"}\", \"{food ?? "FOODPIECE?"}\"),";

                Plugin.MyLog.LogInfo("[FoodRecon] Suggested Defs entry (paste, then tune holders + DumpFoodDef):\n            " + line);
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] SuggestEntry error: {ex}"); }
        }

        // Strip a trailing numeric index ("bone_suharik_hold_000" -> "bone_suharik_hold") so
        // the suggested cracker prefix matches the whole set.
        private static string StripTrailingIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int i = name.Length;
            while (i > 0 && char.IsDigit(name[i - 1])) i--;
            if (i > 0 && name[i - 1] == '_') i--;
            return i > 0 ? name.Substring(0, i) : name;
        }

        // Name of the first DIRECT child of 'parent' that has a Renderer (the held prop root).
        private static string FirstRendererChildName(Transform parent)
        {
            if (parent == null) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c.GetComponent<Renderer>() != null) return c.name;
            }
            return null;
        }

        // Name of the first transform (deep) whose name contains 'sub' (case-insensitive).
        private static string NameContaining(Transform root, string sub)
        {
            if (root == null) return null;
            if (root.name.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) return root.name;
            for (int i = 0; i < root.childCount; i++)
            {
                string found = NameContaining(root.GetChild(i), sub);
                if (found != null) return found;
            }
            return null;
        }

        public static void OnSound(BaseSoundPlayer sp, string name)
        {
            if (!enabled || sp == null) return;
            // Filter to our food's sound player when we know it (else log all — at eat
            // start soundPlayer may not be cached yet).
            if (soundPlayer != null && sp != soundPlayer) return;
            Plugin.MyLog.LogInfo($"[FoodRecon][SOUND] '{name}'{StateStr()}");
        }

        public static void Tick(EFT.Player player)
        {
            if (!enabled || activeController == null || player == null) return;
            if (!player.IsYourPlayer) return;

            // Stop once the food controller has been switched away — print the auto-averaged
            // stable grip on the way out.
            if (!ReferenceEquals(player.HandsController, activeController)) { ReportStableGrip(); activeController = null; return; }

            // State transitions (arms layer 1 — the eat state machine).
            try
            {
                var st = player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                if (st.fullPathHash != lastState)
                {
                    lastState = st.fullPathHash;
                    Plugin.MyLog.LogInfo($"[FoodRecon][STATE] -> {st.fullPathHash} (t={st.normalizedTime:F2})");
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(gripPropName))
            {
                UpdateGripStability(); // every frame (more samples = better average)
                if (Time.time >= nextGripLog)
                {
                    nextGripLog = Time.time + gripLogInterval;
                    LogGrip();
                }
            }
        }

        // palm->prop relative pose. Returns false if the prop/palm aren't found.
        private static bool TryGetGrip(out Vector3 p, out Quaternion q)
        {
            p = Vector3.zero; q = Quaternion.identity;
            Transform root = activeController?._controllerObject != null ? activeController._controllerObject.transform : null;
            if (root == null) return false;
            Transform prop = FindDeep(root, gripPropName);
            Transform palm = FindDeep(root, gripPalmName);
            if (prop == null || palm == null) return false;
            p = palm.InverseTransformPoint(prop.position);
            q = Quaternion.Inverse(palm.rotation) * prop.rotation;
            return true;
        }

        // Logs the raw palm->prop pose (throttled) — kept for spot-checking.
        private static void LogGrip()
        {
            try
            {
                if (!TryGetGrip(out Vector3 p, out Quaternion q)) return;
                Vector3 r = q.eulerAngles;
                Plugin.MyLog.LogInfo($"[FoodRecon][GRIP] {gripPropName} in {gripPalmName}: pos=({p.x:F4},{p.y:F4},{p.z:F4}) rot=({r.x:F2},{r.y:F2},{r.z:F2})");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] LogGrip error: {ex}"); }
        }

        // Per-frame: track the longest run where the grip barely moves (the prop held still in
        // the palm). Out-of-hand samples reset the run, so taken pieces measure their in-hand
        // window, not the time they sit in the can/bag.
        private static void UpdateGripStability()
        {
            try
            {
                if (!TryGetGrip(out Vector3 p, out Quaternion q)) return;
                if (p.magnitude > gripStableMaxDist) { gripRunCount = 0; gripHaveLast = false; return; }

                if (gripHaveLast &&
                    (p - gripLastPos).magnitude <= gripStablePosTol &&
                    Quaternion.Angle(q, gripLastRot) <= gripStableAngTol)
                {
                    gripRunCount++;
                    gripRunPosSum += p;
                    Vector4 v = new Vector4(q.x, q.y, q.z, q.w);
                    Vector4 first = new Vector4(gripRunFirstRot.x, gripRunFirstRot.y, gripRunFirstRot.z, gripRunFirstRot.w);
                    if (Vector4.Dot(v, first) < 0f) v = -v; // sign-align for quaternion averaging
                    gripRunRotSum += v;
                }
                else
                {
                    gripRunCount = 1;
                    gripRunPosSum = p;
                    gripRunFirstRot = q;
                    gripRunRotSum = new Vector4(q.x, q.y, q.z, q.w);
                }
                gripLastPos = p; gripLastRot = q; gripHaveLast = true;

                if (gripRunCount > gripBestCount)
                {
                    gripBestCount = gripRunCount;
                    gripBestPos = gripRunPosSum / gripRunCount;
                    Vector4 s = gripRunRotSum; s.Normalize();
                    gripBestRot = new Quaternion(s.x, s.y, s.z, s.w).eulerAngles;
                }
            }
            catch { }
        }

        // Print the longest stable run's average as a paste-ready V(...)/V(...). If nothing
        // qualified, say WHY (instead of going silent) — usually a SkinnedMeshRenderer phantom
        // transform, or the prop is held by the other hand.
        private static void ReportStableGrip()
        {
            if (string.IsNullOrEmpty(gripPropName)) return;
            if (gripBestCount < gripStableMinRun)
            {
                Plugin.MyLog.LogWarning(
                    $"[FoodRecon][GRIP-AVG] no stable IN-HAND run for '{gripPropName}' vs {gripPalmName} " +
                    $"(best run {gripBestCount} frames). It never sat within {gripStableMaxDist}m of that palm — " +
                    $"likely a SkinnedMeshRenderer transform (a phantom; the visual is driven by its bones, so " +
                    $"measure the bone-root group instead), or it's held by the OTHER hand (try the other palm). " +
                    $"You can also raise gripStableMaxDist to force a value.");
                return;
            }
            Plugin.MyLog.LogInfo(
                $"[FoodRecon][GRIP-AVG] {gripPropName} in {gripPalmName} (stable {gripBestCount} frames):  " +
                $"pos: V({gripBestPos.x:0.###}f, {gripBestPos.y:0.###}f, {gripBestPos.z:0.###}f)  " +
                $"rot: V({gripBestRot.x:0.#}f, {gripBestRot.y:0.#}f, {gripBestRot.z:0.#}f)");
        }

        private static string StateStr()
        {
            try
            {
                var anim = VRGlobals.player?.ArmsAnimatorCommon;
                if (anim == null) return "";
                var l0 = anim.GetCurrentAnimatorStateInfo(0);
                var l1 = anim.GetCurrentAnimatorStateInfo(1);
                return $"  L0(h={l0.fullPathHash} t={l0.normalizedTime:F3})  L1(h={l1.fullPathHash} t={l1.normalizedTime:F3})";
            }
            catch { return ""; }
        }

        private static void DumpTree(MedsController instance)
        {
            try
            {
                if (instance._controllerObject == null) return;
                var sb = new StringBuilder();
                sb.AppendLine($"[FoodRecon][TREE] under {instance._controllerObject.name}:");
                DumpNode(instance._controllerObject.transform, 0, sb);
                Plugin.MyLog.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] DumpTree error: {ex}"); }
        }

        private static void DumpNode(Transform t, int depth, StringBuilder sb)
        {
            string indent = new string(' ', depth * 2);
            string extra = "";
            Renderer r = t.GetComponent<Renderer>();
            if (r != null)
            {
                string mesh = "?";
                if (r is SkinnedMeshRenderer smr) mesh = smr.sharedMesh != null ? smr.sharedMesh.name : "null";
                else { MeshFilter mf = t.GetComponent<MeshFilter>(); mesh = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "null"; }
                extra = $"   <{r.GetType().Name} en={(r.enabled ? 1 : 0)} act={(r.gameObject.activeInHierarchy ? 1 : 0)} mesh='{mesh}'>";
            }
            sb.AppendLine($"{indent}{t.name}{extra}");
            for (int i = 0; i < t.childCount; i++) DumpNode(t.GetChild(i), depth + 1, sb);
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }

    [HarmonyPatch]
    internal class EatingReconPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        private static void ReconSpawn(MedsController __instance) => EatingRecon.OnSpawn(__instance);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "OnSound")]
        private static void ReconSound(BaseSoundPlayer __instance, string StringParam) => EatingRecon.OnSound(__instance, StringParam);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void ReconTick(EFT.Player __instance) => EatingRecon.Tick(__instance);
    }
}
