using System;
using System.Collections.Generic;
using System.Text;
using EFT.InventoryLogic;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;
using static EFT.Player;

namespace TarkovVR.Patches.Core.Player
{
    // ===========================================================================
    // Temporary recon tool: watches a food's vanilla eat and logs what a new FoodDef in EatingInteractionController needs.
    // Usage: set EatingRecon.enabled = true (UnityExplorer), eat the food the vanilla way (no FoodDef for it, or Manual
    // Eating off), then read the [FoodRecon] log lines:
    //  * [TREE]   — the transform tree under the controller object: prop bone names, renderer type, mesh, enabled
    //  * [SOUND]  — every sound event name, with the arms-animator state hash and normalizedTime when it fired
    //  * [STATE]  — arms layer-1 state transitions (OPEN->USE->EAT->END) with normalizedTime
    //  * [GRIP]   — every plausible prop tracked against both palms; at eat end the longest stable in-hand run per
    //               (prop, palm) is printed as a paste-ready V(...)/V(...)
    // ===========================================================================
    internal static class EatingRecon
    {
        public static bool enabled = false;            // turns the recon on; can be switched live

        // Optional extra (prop, palm) pair. It is tracked along with the auto-discovered props, and its raw pose is logged
        // every gripLogInterval seconds. Leave gripPropName empty for auto-discovery only.
        public static string gripPropName = "";
        public static string gripPalmName = "Base HumanLPalm"; // or "Base HumanRPalm"
        public static float gripLogInterval = 0.25f;

        // Each tracked (prop, palm) pair is sampled every frame. The longest run where the prop barely moves in the palm
        // is averaged and printed at eat end. Samples with the prop out of the hand (in the can or bag, or on its way to
        // the mouth) reset the run, so a taken piece measures only its time in the hand.
        public static float gripStableMaxDist = 0.25f; // ignore samples this far from the palm (m)
        public static float gripStablePosTol = 0.02f;  // frame-to-frame pos move that still counts as "still" (m)
        public static float gripStableAngTol = 8f;      // frame-to-frame rot move that still counts as "still" (deg)
        public static int gripStableMinRun = 8;         // min frames for a run to be reported
        public static int maxGripCandidates = 24;       // discovery cap (keeps bag-cracker swarms sane)

        private static MedsController activeController;
        private static BaseSoundPlayer soundPlayer;
        private static int lastState;
        private static float nextGripLog;

        // One stability tracker per (prop, palm) pair. Built at spawn, reported and cleared at eat end.
        private sealed class GripTrack
        {
            public string propName; public string palmLabel;
            public Transform prop; public Transform palm;
            public bool haveLast; public Vector3 lastPos; public Quaternion lastRot;
            public int runCount; public Vector3 runPosSum;
            public Vector4 runRotSum; public Quaternion runFirstRot;
            public int bestCount; public Vector3 bestPos, bestRot;
        }
        private static readonly List<GripTrack> gripTracks = new List<GripTrack>();
        private static Transform manualProp, manualPalm; // the optional manual spot-log pair

        // --- Open-hand path capture ([HANDPATH] — the pull-to-open HandPath bake) -----
        // While the arms are in the food's open state, both palms are sampled every frame relative to the held item root.
        // In a vanilla eat this is the raw clip pose. At eat end, the palm that moved relative to the item (the opening
        // hand) has its leading draw arc trimmed, is resampled to handPathKeys keys, and is printed as a HandPath(...) wrapper.
        // Needs an existing FoodDef (for the root name and open state) and a vanilla eat (Manual Eating off).
        public static int handPathKeys = 10;
        public static float handPathMaxDist = 0.35f; // m; leading samples farther than this from the root are the draw arc and are trimmed
        private static Transform pathRoot, pathPalmL, pathPalmR;
        private static int pathOpenHash;
        private static readonly List<float> pathTimes = new List<float>();
        private static readonly List<Vector3> pathPosL = new List<Vector3>();
        private static readonly List<Vector3> pathPosR = new List<Vector3>();
        private static readonly List<Quaternion> pathRotL = new List<Quaternion>();
        private static readonly List<Quaternion> pathRotR = new List<Quaternion>();
        // ReachBag only: a second capture over the STATE_USE reach segment, the state the reach scrub seeks in. Only the
        // first pass through the state (the first reach) is kept. Printed as a ReachPath(...) wrapper for DriveReachLatch.
        // Hash 0 = off.
        private static int pathReachHash;
        private static readonly List<float> reachTimes = new List<float>();
        private static readonly List<Vector3> reachPosL = new List<Vector3>();
        private static readonly List<Vector3> reachPosR = new List<Vector3>();
        private static readonly List<Quaternion> reachRotL = new List<Quaternion>();
        private static readonly List<Quaternion> reachRotR = new List<Quaternion>();

        public static void OnSpawn(MedsController instance)
        {
            if (!enabled || instance == null) return;
            try
            {
                EFT.Player player = instance._player;
                if (player == null || !player.IsYourPlayer) return;
                if (!(instance.Item is FoodDrink)) return;

                activeController = instance;
                lastState = 0;
                nextGripLog = 0f;
                soundPlayer = instance._controllerObject != null
                    ? instance._controllerObject.GetComponentInChildren<BaseSoundPlayer>(true)
                    : null;

                Plugin.MyLog.LogInfo($"[FoodRecon] ===== {instance.Item?.TemplateId} ({instance.Item?.ShortName}) =====");
                DumpTree(instance);
                SuggestEntry(instance);
                BuildGripTracks(instance);
                SetupHandPathCapture(instance);
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[FoodRecon] OnSpawn error: {exception}"); }
        }

        // Guesses the archetype and prop names from the tree (BSG naming) and prints a paste-ready factory line for
        // EatingInteractionController.Defs. The root is the first direct child of 'weapon' that has a Renderer.
        // Grips and timings stay at the archetype defaults: tune them live, then use DumpFoodDef.
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
                // Bag cues (food poured into the hand): a packaging root and "hold" food pieces.
                string bagRoot = NameContaining(root, "upakovka");
                string holdPiece = NameContaining(root, "_hold_") ?? NameContaining(root, "suharik");
                bool hasCap = FindDeep(root, "cap") != null; // drinks' cap node is exactly "cap"

                string line;
                if (bagRoot != null && holdPiece != null)
                    line = $"Bag     (\"{id}\", \"{bagRoot}\", \"{StripTrailingIndex(holdPiece)}\"),";
                else if (FindDeep(root, "tetrapak_root") != null)
                    line = $"TetraPak(\"{id}\"),";
                else if (FindDeep(root, "tc_root") != null)
                    line = $"SodaCan (\"{id}\"),";
                else if (FindDeep(root, "hr_root") != null)
                    line = $"Drink   (\"{id}\", \"hr_root\", null),";
                else if (FindDeep(root, "mod_item") != null)
                    line = $"Bottle  (\"{id}\"{(hasCap ? "" : ", cap: false")}),";
                else if (FindDeep(root, "item_sugar_box") != null)
                    line = $"Sugar  (\"{id}\"),";
                else if (cover != null || catGroup != null)
                    line = $"Wrapper (\"{id}\", \"{rootName ?? "ROOT?"}\", \"{catGroup ?? "WRAPPER_GROUP?"}\", \"{cover ?? "COVER?"}\"),";
                else if (spoon != null)
                    line = $"CanSpoon(\"{id}\", \"{rootName ?? "ROOT?"}\", \"{spoon}\", \"{food ?? "FOODPIECE?"}\"),";
                else
                    line = $"CanHand (\"{id}\", \"{rootName ?? "ROOT?"}\", \"{food ?? "FOODPIECE?"}\"),";

                Plugin.MyLog.LogInfo("[FoodRecon] Suggested Defs entry (paste, then tune holders + DumpFoodDef):\n            " + line);
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[FoodRecon] SuggestEntry error: {exception}"); }
        }

        // --- Auto grip-candidate discovery ---------------------------------------
        // Builds a tracker for every plausible prop against both palms. Candidates are nodes with a renderer (LOD meshes
        // resolve to their parent group, e.g. the beer LODs -> mod_item), plus bone groups without renderers matched by
        // name (pack_CAT, tc_root, the exact "cap" node). Arm and finger rigs, cameras, fx and markers are skipped.
        private static void BuildGripTracks(MedsController instance)
        {
            gripTracks.Clear();
            manualProp = manualPalm = null;
            try
            {
                Transform root = instance._controllerObject != null ? instance._controllerObject.transform : null;
                if (root == null) return;
                Transform palmL = FindDeep(root, "Base HumanLPalm");
                Transform palmR = FindDeep(root, "Base HumanRPalm");
                if (palmL == null && palmR == null) { Plugin.MyLog.LogWarning("[FoodRecon][GRIP] no palm bones found — grips off."); return; }

                var props = new List<Transform>();
                CollectGripCandidates(root, props);

                // Optional manual pair, for a prop the discovery missed.
                if (!string.IsNullOrEmpty(gripPropName))
                {
                    manualProp = FindDeep(root, gripPropName);
                    manualPalm = FindDeep(root, gripPalmName);
                    if (manualProp != null && !props.Contains(manualProp)) props.Add(manualProp);
                }

                var names = new StringBuilder();
                foreach (Transform p in props)
                {
                    if (palmL != null) gripTracks.Add(new GripTrack { propName = p.name, palmLabel = "L palm", prop = p, palm = palmL });
                    if (palmR != null) gripTracks.Add(new GripTrack { propName = p.name, palmLabel = "R palm", prop = p, palm = palmR });
                    if (names.Length > 0) names.Append(", ");
                    names.Append(p.name);
                }
                Plugin.MyLog.LogInfo($"[FoodRecon][GRIP] auto-tracking {props.Count} props vs both palms: {names}");
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[FoodRecon] BuildGripTracks error: {exception}"); }
        }

        private static void CollectGripCandidates(Transform t, List<Transform> into)
        {
            if (into.Count >= maxGripCandidates) return;
            string n = t.name;
            // Skip whole subtrees that can't be a held prop: arm and finger rigs, cameras, fx,
            // markers, bend goals, and our own Eat*Holder objects during a manual eat.
            if (n.StartsWith("Base Human", StringComparison.Ordinal)
                || n.StartsWith("Bend_Goal", StringComparison.Ordinal)
                || n.StartsWith("Camera", StringComparison.Ordinal)
                || n.StartsWith("Eat", StringComparison.Ordinal)
                || n == "aim_camera" || n == "fireport" || n == "smokeport" || n == "shellport"
                || n == "patron_in_weapon"
                || n.IndexOf("muzzle", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("marker", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("HeatHaze", StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            Transform cand = CandidateFor(t);
            if (cand != null && !into.Contains(cand)) into.Add(cand);
            for (int i = 0; i < t.childCount; i++) CollectGripCandidates(t.GetChild(i), into);
        }

        // A grip candidate either matches a prop-name cue (for bone-group roots without renderers) or has a Renderer. LOD
        // mesh nodes resolve to their parent group (mod_item), unless the parent is the weapon node (the chocolate bar's
        // root is its LOD0 mesh). The weapon chain never qualifies. "cap" is matched exactly (the drinks' cap) so the saira
        // lid-bone chain (saira_cap01…), which never leaves the can, doesn't flood the report.
        private static Transform CandidateFor(Transform t)
        {
            string n = t.name;
            if (IsWeaponChain(n)) return null;

            bool cue = n.Equals("cap", StringComparison.OrdinalIgnoreCase)
                || n.Equals("mod_item", StringComparison.OrdinalIgnoreCase)
                || n.Equals("item_sugar_box", StringComparison.OrdinalIgnoreCase)
                || n.IndexOf("_root", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("_cat", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("cover", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("spoon", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("foodpiece", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("upakovka", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("hold", StringComparison.OrdinalIgnoreCase) >= 0;
            if (cue) return t;

            if (t.GetComponent<Renderer>() == null) return null;
            if (n.IndexOf("LOD", StringComparison.OrdinalIgnoreCase) < 0) return t;
            Transform parent = t.parent;
            return (parent == null || IsWeaponChain(parent.name)) ? t : parent;
        }

        private static bool IsWeaponChain(string n)
            => n == "weapon" || n.StartsWith("Weapon_root", StringComparison.OrdinalIgnoreCase);

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
            // Vanilla eats only. In a manual eat the pull-to-open scrub seeks the open state every frame, which re-fires the
            // clip's sound events (the AllowSound gate mutes them) and floods the log.
            if (EatingInteractionController.ManualActive) return;
            // Only our food's sound player once it is known. At eat start soundPlayer may
            // not be cached yet, so everything is logged.
            if (soundPlayer != null && sp != soundPlayer) return;
            Plugin.MyLog.LogInfo($"[FoodRecon][SOUND] '{name}'{StateStr()}");
        }

        public static void Tick(EFT.Player player)
        {
            if (!enabled || activeController == null || player == null) return;
            if (!player.IsYourPlayer) return;

            // Once the hands switch away from the food controller, print the stable grips
            // and the open-hand path, then stop.
            if (!ReferenceEquals(player.HandsController, activeController)) { ReportStableGrips(); ReportHandPath(); activeController = null; return; }

            // State transitions (arms layer 1 — the eat state machine).
            try
            {
                var stateInfo = player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                if (stateInfo.fullPathHash != lastState)
                {
                    lastState = stateInfo.fullPathHash;
                    Plugin.MyLog.LogInfo($"[FoodRecon][STATE] -> {stateInfo.fullPathHash} (t={stateInfo.normalizedTime:F2})");
                }
            }
            catch { }

            UpdateGripTracks(); // every frame, every pair (more samples = better averages)
            CaptureHandPath(player);
            if (manualProp != null && manualPalm != null && Time.time >= nextGripLog)
            {
                nextGripLog = Time.time + gripLogInterval;
                LogGrip();
            }
        }

        // --- Open-hand path capture/emit ------------------------------------------
        private static void SetupHandPathCapture(MedsController instance)
        {
            pathRoot = pathPalmL = pathPalmR = null;
            pathTimes.Clear(); pathPosL.Clear(); pathPosR.Clear(); pathRotL.Clear(); pathRotR.Clear();
            reachTimes.Clear(); reachPosL.Clear(); reachPosR.Clear(); reachRotL.Clear(); reachRotR.Clear();
            try
            {
                string id = instance.Item?.TemplateId;
                string rootName = EatingInteractionController.LookupRootName(id);
                pathOpenHash = EatingInteractionController.LookupOpenStateHash(id);
                pathReachHash = EatingInteractionController.LookupReachStateHash(id); // ReachBag: also bake the reach
                Transform root = instance._controllerObject != null ? instance._controllerObject.transform : null;
                if (root == null || string.IsNullOrEmpty(rootName) || pathOpenHash == 0)
                {
                    if (root != null)
                        Plugin.MyLog.LogInfo("[FoodRecon][HANDPATH] no FoodDef for this item — path capture off (recon the def first, then re-eat).");
                    return;
                }
                pathRoot = FindDeep(root, rootName);
                pathPalmL = FindDeep(root, "Base HumanLPalm");
                pathPalmR = FindDeep(root, "Base HumanRPalm");
                if (pathRoot == null || (pathPalmL == null && pathPalmR == null)) pathRoot = null;
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[FoodRecon] SetupHandPathCapture error: {exception}"); pathRoot = null; }
        }

        // Samples both palms relative to the item root while the arms are in the open state. Vanilla eats only, because a
        // manual eat reparents the props and pins the wrists. Only increasing normalizedTime is kept (the first pass through
        // the state); later re-entries, e.g. the chocolate's reused STATE_USE, restart at 0 and are dropped.
        private static void CaptureHandPath(EFT.Player player)
        {
            if (pathRoot == null || EatingInteractionController.ManualActive) return;
            try
            {
                var stateInfo = player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                if (stateInfo.fullPathHash == pathOpenHash)
                    SamplePathFrame(stateInfo.normalizedTime, pathTimes, pathPosL, pathRotL, pathPosR, pathRotR);
                else if (pathReachHash != 0 && stateInfo.fullPathHash == pathReachHash)
                    SamplePathFrame(stateInfo.normalizedTime, reachTimes, reachPosL, reachRotL, reachPosR, reachRotR);
            }
            catch { }
        }

        private static void SamplePathFrame(float t, List<float> times,
            List<Vector3> posL, List<Quaternion> rotL, List<Vector3> posR, List<Quaternion> rotR)
        {
            if (t > 1f) return;
            if (times.Count > 0 && t <= times[times.Count - 1]) return;
            times.Add(t);
            posL.Add(pathPalmL != null ? pathRoot.InverseTransformPoint(pathPalmL.position) : Vector3.zero);
            rotL.Add(pathPalmL != null ? Quaternion.Inverse(pathRoot.rotation) * pathPalmL.rotation : Quaternion.identity);
            posR.Add(pathPalmR != null ? pathRoot.InverseTransformPoint(pathPalmR.position) : Vector3.zero);
            rotR.Add(pathPalmR != null ? Quaternion.Inverse(pathRoot.rotation) * pathPalmR.rotation : Quaternion.identity);
        }

        private static float PathTravel(List<Vector3> positions, int start)
        {
            float d = 0f;
            for (int i = start + 1; i < positions.Count; i++) d += (positions[i] - positions[i - 1]).magnitude;
            return d;
        }

        private static void ReportHandPath()
        {
            if (pathRoot == null) return;
            try
            {
                EmitPath("HandPath", "open-hand", pathTimes, pathPosL, pathRotL, pathPosR, pathRotR);
                if (pathReachHash != 0) // ReachBag: the STATE_USE reach segment too
                    EmitPath("ReachPath", "reach-hand (STATE_USE)", reachTimes, reachPosL, reachRotL, reachPosR, reachRotR);
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[FoodRecon] ReportHandPath error: {exception}"); }
            finally
            {
                pathRoot = pathPalmL = pathPalmR = null;
                pathReachHash = 0;
                pathTimes.Clear(); pathPosL.Clear(); pathPosR.Clear(); pathRotL.Clear(); pathRotR.Clear();
                reachTimes.Clear(); reachPosL.Clear(); reachPosR.Clear(); reachRotL.Clear(); reachRotR.Clear();
            }
        }

        // Picks the palm that moved relative to the item (the holding palm barely moves), trims the
        // leading approach arc (samples farther than handPathMaxDist from the root), resamples
        // to handPathKeys evenly spaced keys and prints the paste-ready wrapper.
        private static void EmitPath(string wrapper, string what, List<float> times,
            List<Vector3> posL, List<Quaternion> rotL, List<Vector3> posR, List<Quaternion> rotR)
        {
            if (times.Count < 5)
            {
                Plugin.MyLog.LogInfo($"[FoodRecon][HANDPATH] only {times.Count} {what} samples — no {wrapper} emitted.");
                return;
            }
            float travL = PathTravel(posL, 0), travR = PathTravel(posR, 0);
            bool useR = travR >= travL;
            List<Vector3> positions = useR ? posR : posL;
            List<Quaternion> rotation = useR ? rotR : rotL;

            int start = 0;
            while (start < times.Count && positions[start].magnitude > handPathMaxDist) start++;
            if (times.Count - start < 5)
            {
                Plugin.MyLog.LogWarning($"[FoodRecon][HANDPATH] the {what} palm never came within {handPathMaxDist}m of the item root — no path (raise handPathMaxDist?).");
                return;
            }

            // Resample to evenly spaced keys over the kept time range. Each quaternion is sign-aligned
            // with the previous key so the replay's Slerp takes the short way.
            int keys = Mathf.Max(2, handPathKeys);
            var builder = new StringBuilder();
            builder.AppendLine($"[FoodRecon][HANDPATH] {what} path: {(useR ? "R" : "L")} palm vs '{pathRoot.name}', "
                        + $"{keys} keys, t {times[start]:0.###}->{times[times.Count - 1]:0.###} "
                        + $"(travel L {travL:0.##}m / R {travR:0.##}m) — wrap the food's def line:");
            builder.Append($"            {wrapper}(<def>");
            Quaternion prevQ = Quaternion.identity; bool havePrev = false;
            for (int k = 0; k < keys; k++)
            {
                float t = Mathf.Lerp(times[start], times[times.Count - 1], k / (float)(keys - 1));
                int hi = start;
                while (hi < times.Count && times[hi] < t) hi++;
                Vector3 sp; Quaternion sq;
                if (hi <= start) { sp = positions[start]; sq = rotation[start]; }
                else if (hi >= times.Count) { sp = positions[times.Count - 1]; sq = rotation[times.Count - 1]; }
                else
                {
                    float t0 = times[hi - 1], t1 = times[hi];
                    float w = t1 > t0 ? (t - t0) / (t1 - t0) : 1f;
                    sp = Vector3.Lerp(positions[hi - 1], positions[hi], w);
                    sq = Quaternion.Slerp(rotation[hi - 1], rotation[hi], w);
                }
                if (havePrev && Quaternion.Dot(sq, prevQ) < 0f) sq = new Quaternion(-sq.x, -sq.y, -sq.z, -sq.w);
                prevQ = sq; havePrev = true;
                builder.Append($",\n                {t:0.###}f, {sp.x:0.###}f, {sp.y:0.###}f, {sp.z:0.###}f, {sq.x:0.####}f, {sq.y:0.####}f, {sq.z:0.####}f, {sq.w:0.####}f");
            }
            builder.Append("),");
            Plugin.MyLog.LogInfo(builder.ToString());
        }

        // Logs the manual pair's raw palm->prop pose (throttled).
        private static void LogGrip()
        {
            try
            {
                Vector3 p = manualPalm.InverseTransformPoint(manualProp.position);
                Vector3 r = (Quaternion.Inverse(manualPalm.rotation) * manualProp.rotation).eulerAngles;
                Plugin.MyLog.LogInfo($"[FoodRecon][GRIP] {gripPropName} in {gripPalmName}: pos=({p.x:F4},{p.y:F4},{p.z:F4}) rot=({r.x:F2},{r.y:F2},{r.z:F2})");
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[FoodRecon] LogGrip error: {exception}"); }
        }

        // Per frame, per pair: tracks the longest run where the grip barely moves. Out-of-hand samples reset the run.
        private static void UpdateGripTracks()
        {
            for (int i = 0; i < gripTracks.Count; i++)
            {
                GripTrack track = gripTracks[i];
                try
                {
                    if (track.prop == null || track.palm == null) continue;
                    Vector3 p = track.palm.InverseTransformPoint(track.prop.position);
                    Quaternion q = Quaternion.Inverse(track.palm.rotation) * track.prop.rotation;
                    if (p.magnitude > gripStableMaxDist) { track.runCount = 0; track.haveLast = false; continue; }

                    if (track.haveLast &&
                        (p - track.lastPos).magnitude <= gripStablePosTol &&
                        Quaternion.Angle(q, track.lastRot) <= gripStableAngTol)
                    {
                        track.runCount++;
                        track.runPosSum += p;
                        Vector4 v = new Vector4(q.x, q.y, q.z, q.w);
                        Vector4 first = new Vector4(track.runFirstRot.x, track.runFirstRot.y, track.runFirstRot.z, track.runFirstRot.w);
                        if (Vector4.Dot(v, first) < 0f) v = -v; // sign-align for quaternion averaging
                        track.runRotSum += v;
                    }
                    else
                    {
                        track.runCount = 1;
                        track.runPosSum = p;
                        track.runFirstRot = q;
                        track.runRotSum = new Vector4(q.x, q.y, q.z, q.w);
                    }
                    track.lastPos = p; track.lastRot = q; track.haveLast = true;

                    if (track.runCount > track.bestCount)
                    {
                        track.bestCount = track.runCount;
                        track.bestPos = track.runPosSum / track.runCount;
                        Vector4 s = track.runRotSum; s.Normalize();
                        track.bestRot = new Quaternion(s.x, s.y, s.z, s.w).eulerAngles;
                    }
                }
                catch { }
            }
        }

        // Prints every qualifying (prop, palm) pair's longest stable run as a paste-ready V(...)/V(...), longest first.
        // A piece held in both palms prints both: pick the one that matches the def's holder. Props that never sat still
        // in either hand are listed once at the end (SkinnedMeshRenderer phantoms, or props that were never held).
        private static void ReportStableGrips()
        {
            if (gripTracks.Count == 0) return;
            try
            {
                var ok = new List<GripTrack>();
                foreach (GripTrack track in gripTracks)
                    if (track.bestCount >= gripStableMinRun) ok.Add(track);
                ok.Sort((a, b) => b.bestCount.CompareTo(a.bestCount));

                var qualified = new HashSet<string>();
                foreach (GripTrack g in ok) qualified.Add(g.propName);
                var missed = new List<string>();
                foreach (GripTrack g in gripTracks)
                    if (!qualified.Contains(g.propName) && !missed.Contains(g.propName)) missed.Add(g.propName);

                var builder = new StringBuilder();
                builder.AppendLine("[FoodRecon][GRIP-AVG] ===== stable in-hand grips (longest still runs, both palms) =====");
                foreach (GripTrack g in ok)
                    builder.AppendLine($"[FoodRecon][GRIP-AVG] {g.propName,-24} in {g.palmLabel} ({g.bestCount,4}f):  " +
                                  $"pos: V({g.bestPos.x:0.###}f, {g.bestPos.y:0.###}f, {g.bestPos.z:0.###}f)  " +
                                  $"rot: V({g.bestRot.x:0.#}f, {g.bestRot.y:0.#}f, {g.bestRot.z:0.#}f)");
                if (ok.Count == 0)
                    builder.AppendLine($"[FoodRecon][GRIP-AVG] nothing held still within {gripStableMaxDist}m of a palm — raise gripStableMaxDist to force values.");
                if (missed.Count > 0)
                    builder.AppendLine($"[FoodRecon][GRIP-AVG] no stable run: {string.Join(", ", missed.ToArray())} (SkinnedMeshRenderer phantom or never held — see [TREE]).");
                Plugin.MyLog.LogInfo(builder.ToString());
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[FoodRecon] ReportStableGrips error: {exception}"); }
            gripTracks.Clear();
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
                var builder = new StringBuilder();
                builder.AppendLine($"[FoodRecon][TREE] under {instance._controllerObject.name}:");
                DumpNode(instance._controllerObject.transform, 0, builder);
                Plugin.MyLog.LogInfo(builder.ToString());
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[FoodRecon] DumpTree error: {exception}"); }
        }

        private static void DumpNode(Transform t, int depth, StringBuilder builder)
        {
            string indent = new string(' ', depth * 2);
            string extra = "";
            Renderer partRenderer = t.GetComponent<Renderer>();
            if (partRenderer != null)
            {
                string mesh = "?";
                if (partRenderer is SkinnedMeshRenderer smr) mesh = smr.sharedMesh != null ? smr.sharedMesh.name : "null";
                else { MeshFilter meshFilter = t.GetComponent<MeshFilter>(); mesh = meshFilter != null && meshFilter.sharedMesh != null ? meshFilter.sharedMesh.name : "null"; }
                extra = $"   <{partRenderer.GetType().Name} en={(partRenderer.enabled ? 1 : 0)} act={(partRenderer.gameObject.activeInHierarchy ? 1 : 0)} mesh='{mesh}'>";
            }
            builder.AppendLine($"{indent}{t.name}{extra}");
            for (int i = 0; i < t.childCount; i++) DumpNode(t.GetChild(i), depth + 1, builder);
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
        [HarmonyPatch(typeof(BaseSoundPlayer), "SoundEventHandler")]
        private static void ReconSound(BaseSoundPlayer __instance, string soundName) => EatingRecon.OnSound(__instance, soundName);

    }
}
