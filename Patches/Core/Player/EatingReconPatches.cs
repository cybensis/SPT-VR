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
    // TEMPORARY RECON — profile a food's VANILLA eat to gather everything needed to
    // author a new FoodDef in EatingInteractionController. Self-contained and decoupled
    // from the manual-eating flow (works on foods that DON'T have a FoodDef yet, since
    // it just watches the normal eat).
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
    //  * [GRIP]   — AUTOMATIC: every plausible prop under the controller object (renderer
    //               groups, *_root / *_CAT / cap / cover / spoon / foodpiece / hold bones)
    //               is tracked against BOTH palms simultaneously; at eat end the longest
    //               stable in-hand run per (prop, palm) prints as a paste-ready
    //               V(...)/V(...). ONE vanilla eat = every grip (bottle AND cap, can AND
    //               spoon AND foodpiece) — no per-prop restarts. gripPropName/gripPalmName
    //               are now just an optional EXTRA pair (something discovery missed) plus
    //               the throttled raw [GRIP] spot log.
    // ===========================================================================
    internal static class EatingRecon
    {
        public static bool enabled = false;            // master toggle — flip on live

        // Optional MANUAL pair: tracked in addition to the auto-discovered candidates (in
        // case discovery missed an exotic prop) and spot-logged raw at gripLogInterval.
        // Leave gripPropName empty for pure auto mode.
        public static string gripPropName = "";
        public static string gripPalmName = "Base HumanLPalm"; // or "Base HumanRPalm"
        public static float gripLogInterval = 0.25f;

        // Auto-averaged stable grips: every frame we sample palm->prop for EVERY tracked
        // (prop, palm) pair and find the longest run where it barely moves (the prop sitting
        // still in that palm), then print each pair's run average as a paste-ready
        // V(...)/V(...) at the end of the eat — no eyeballing the raw list. Out-of-hand
        // samples (prop in the can/bag, or mid-air to the mouth) reset the run, so a taken
        // piece measures its in-hand window, not the time it sits in the can.
        public static float gripStableMaxDist = 0.25f; // ignore samples this far from the palm (m)
        public static float gripStablePosTol = 0.02f;  // frame-to-frame pos move that still counts as "still" (m)
        public static float gripStableAngTol = 8f;      // frame-to-frame rot move that still counts as "still" (deg)
        public static int gripStableMinRun = 8;         // min frames for a run to be reported
        public static int maxGripCandidates = 24;       // discovery cap (keeps bag-cracker swarms sane)

        private static MedsController activeController;
        private static BaseSoundPlayer soundPlayer;
        private static int lastState;
        private static float nextGripLog;

        // One stability tracker per (prop, palm) pair — built at spawn (transforms cached,
        // no per-frame FindDeep), reported + cleared at eat end.
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
        // While the arms are in the food's OPEN state, sample BOTH palms relative to the
        // held item root every frame (this window holds the RAW clip pose — same proven
        // timing as [GRIP-AVG]); at eat end the palm that MOVED vs the item (the opener —
        // the holding palm is near-static) is trimmed of its leading draw arc, resampled
        // to handPathKeys keys and printed as a paste-ready HandPath(...) wrapper.
        // Needs an existing FoodDef (root-name/open-state lookup) and a VANILLA eat
        // (enableManualEating=false) — a manual eat captures nothing. ReachBag defs get a
        // SECOND capture on the STATE_USE reach segment (first pass through the state =
        // the first reach), emitted as a ReachPath(...) wrapper for DriveReachLatch.
        public static int handPathKeys = 10;
        public static float handPathMaxDist = 0.35f; // leading samples farther than this from the root = the draw arc, trimmed
        private static Transform pathRoot, pathPalmL, pathPalmR;
        private static int pathOpenHash;
        private static readonly List<float> pathTimes = new List<float>();
        private static readonly List<Vector3> pathPosL = new List<Vector3>();
        private static readonly List<Vector3> pathPosR = new List<Vector3>();
        private static readonly List<Quaternion> pathRotL = new List<Quaternion>();
        private static readonly List<Quaternion> pathRotR = new List<Quaternion>();
        // ReachBag only: a SECOND capture on the STATE_USE reach segment (the axis the
        // reach scrub seeks on), emitted as a ReachPath(...) wrapper. Hash 0 = off.
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
                if (!(instance.Item is FoodDrinkItemClass)) return;

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
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] OnSpawn error: {ex}"); }
        }

        // Guess the archetype + prop names from the tree and print a paste-ready factory
        // line for EatingInteractionController.Defs. Heuristics (match BSG naming): drink
        // container roots ("tetrapak_root"/"tc_root"/"hr_root"/"mod_item" ± an exact "cap"
        // node) => the matching shape preset; a packaging root + "hold" pieces => Bag; the
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
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] SuggestEntry error: {ex}"); }
        }

        // --- Auto grip-candidate discovery ---------------------------------------
        // Build a (prop, palm) tracker for every plausible prop vs BOTH palms. Candidates =
        // renderer-bearing nodes (LOD meshes resolve to their parent group, e.g. the beer
        // LODs -> mod_item) + def-ish name cues for bone groups WITHOUT renderers (pack_CAT,
        // tc_root, the exact "cap" node...). The arms/finger rigs, cameras, fx and markers
        // are skipped wholesale.
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

                // Optional manual extra pair (something discovery missed) + raw spot log.
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
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] BuildGripTracks error: {ex}"); }
        }

        private static void CollectGripCandidates(Transform t, List<Transform> into)
        {
            if (into.Count >= maxGripCandidates) return;
            string n = t.name;
            // Whole subtrees that can't be a held prop: arm/finger rigs, cameras, fx,
            // markers, bend goals — and our own Eat*Holder objects during a manual eat.
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

        // A node is a grip candidate if it matches a def-ish prop-name cue (bone-group
        // roots without renderers) or bears a Renderer itself. LOD mesh nodes resolve to
        // their PARENT group (mod_item) — unless the parent is the weapon node itself (the
        // chocolate bar's root IS its LOD0 mesh). The weapon chain itself never qualifies.
        // "cap" is matched EXACTLY (drinks) so the saira lid-bone chain (saira_cap01…) —
        // which never leaves the can — doesn't flood the report.
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
            // Recon profiles VANILLA eats. During a manual eat the pull-to-open scrub
            // re-enters the open state every frame, re-firing the clip's sound events
            // (all suppressed by the AllowSound gate anyway) — logging them floods the
            // log with thousands of meaningless [SOUND] lines per pull.
            if (EatingInteractionController.ManualActive) return;
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
            // stable grips + the open-hand path on the way out.
            if (!ReferenceEquals(player.HandsController, activeController)) { ReportStableGrips(); ReportHandPath(); activeController = null; return; }

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
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] SetupHandPathCapture error: {ex}"); pathRoot = null; }
        }

        // Sample both palms vs the item root while the arms are in the open state. Gated to
        // VANILLA eats (a manual eat has the props reparented + wrists pinned = garbage) and
        // to monotonically increasing normalizedTime (= the FIRST pass through the state;
        // later re-entries, e.g. the chocolate's reused STATE_USE, reset to 0 and are dropped).
        private static void CaptureHandPath(EFT.Player player)
        {
            if (pathRoot == null || EatingInteractionController.ManualActive) return;
            try
            {
                var st = player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                if (st.fullPathHash == pathOpenHash)
                    SamplePathFrame(st.normalizedTime, pathTimes, pathPosL, pathRotL, pathPosR, pathRotR);
                else if (pathReachHash != 0 && st.fullPathHash == pathReachHash)
                    SamplePathFrame(st.normalizedTime, reachTimes, reachPosL, reachRotL, reachPosR, reachRotR);
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

        private static float PathTravel(List<Vector3> pos, int start)
        {
            float d = 0f;
            for (int i = start + 1; i < pos.Count; i++) d += (pos[i] - pos[i - 1]).magnitude;
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
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] ReportHandPath error: {ex}"); }
            finally
            {
                pathRoot = pathPalmL = pathPalmR = null;
                pathReachHash = 0;
                pathTimes.Clear(); pathPosL.Clear(); pathPosR.Clear(); pathRotL.Clear(); pathRotR.Clear();
                reachTimes.Clear(); reachPosL.Clear(); reachPosR.Clear(); reachRotL.Clear(); reachRotR.Clear();
            }
        }

        // Pick the palm that MOVED vs the item (the holding palm is near-static), trim the
        // leading approach arc (samples farther than handPathMaxDist from the root),
        // resample to handPathKeys evenly spaced keys and print the paste-ready wrapper.
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
            List<Vector3> pos = useR ? posR : posL;
            List<Quaternion> rot = useR ? rotR : rotL;

            int start = 0;
            while (start < times.Count && pos[start].magnitude > handPathMaxDist) start++;
            if (times.Count - start < 5)
            {
                Plugin.MyLog.LogWarning($"[FoodRecon][HANDPATH] the {what} palm never came within {handPathMaxDist}m of the item root — no path (raise handPathMaxDist?).");
                return;
            }

            // Resample to evenly spaced keys over the kept t-range; quats sign-aligned
            // key-to-key so the replay's Slerp takes the short way.
            int keys = Mathf.Max(2, handPathKeys);
            var sb = new StringBuilder();
            sb.AppendLine($"[FoodRecon][HANDPATH] {what} path: {(useR ? "R" : "L")} palm vs '{pathRoot.name}', "
                        + $"{keys} keys, t {times[start]:0.###}->{times[times.Count - 1]:0.###} "
                        + $"(travel L {travL:0.##}m / R {travR:0.##}m) — wrap the food's def line:");
            sb.Append($"            {wrapper}(<def>");
            Quaternion prevQ = Quaternion.identity; bool havePrev = false;
            for (int k = 0; k < keys; k++)
            {
                float t = Mathf.Lerp(times[start], times[times.Count - 1], k / (float)(keys - 1));
                int hi = start;
                while (hi < times.Count && times[hi] < t) hi++;
                Vector3 sp; Quaternion sq;
                if (hi <= start) { sp = pos[start]; sq = rot[start]; }
                else if (hi >= times.Count) { sp = pos[times.Count - 1]; sq = rot[times.Count - 1]; }
                else
                {
                    float t0 = times[hi - 1], t1 = times[hi];
                    float w = t1 > t0 ? (t - t0) / (t1 - t0) : 1f;
                    sp = Vector3.Lerp(pos[hi - 1], pos[hi], w);
                    sq = Quaternion.Slerp(rot[hi - 1], rot[hi], w);
                }
                if (havePrev && Quaternion.Dot(sq, prevQ) < 0f) sq = new Quaternion(-sq.x, -sq.y, -sq.z, -sq.w);
                prevQ = sq; havePrev = true;
                sb.Append($",\n                {t:0.###}f, {sp.x:0.###}f, {sp.y:0.###}f, {sp.z:0.###}f, {sq.x:0.####}f, {sq.y:0.####}f, {sq.z:0.####}f, {sq.w:0.####}f");
            }
            sb.Append("),");
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        // Logs the manual pair's raw palm->prop pose (throttled) — kept for spot-checking.
        private static void LogGrip()
        {
            try
            {
                Vector3 p = manualPalm.InverseTransformPoint(manualProp.position);
                Vector3 r = (Quaternion.Inverse(manualPalm.rotation) * manualProp.rotation).eulerAngles;
                Plugin.MyLog.LogInfo($"[FoodRecon][GRIP] {gripPropName} in {gripPalmName}: pos=({p.x:F4},{p.y:F4},{p.z:F4}) rot=({r.x:F2},{r.y:F2},{r.z:F2})");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] LogGrip error: {ex}"); }
        }

        // Per-frame, per pair: track the longest run where the grip barely moves (the prop
        // held still in that palm). Out-of-hand samples reset the run, so taken pieces
        // measure their in-hand window, not the time they sit in the can/bag.
        private static void UpdateGripTracks()
        {
            for (int i = 0; i < gripTracks.Count; i++)
            {
                GripTrack g = gripTracks[i];
                try
                {
                    if (g.prop == null || g.palm == null) continue;
                    Vector3 p = g.palm.InverseTransformPoint(g.prop.position);
                    Quaternion q = Quaternion.Inverse(g.palm.rotation) * g.prop.rotation;
                    if (p.magnitude > gripStableMaxDist) { g.runCount = 0; g.haveLast = false; continue; }

                    if (g.haveLast &&
                        (p - g.lastPos).magnitude <= gripStablePosTol &&
                        Quaternion.Angle(q, g.lastRot) <= gripStableAngTol)
                    {
                        g.runCount++;
                        g.runPosSum += p;
                        Vector4 v = new Vector4(q.x, q.y, q.z, q.w);
                        Vector4 first = new Vector4(g.runFirstRot.x, g.runFirstRot.y, g.runFirstRot.z, g.runFirstRot.w);
                        if (Vector4.Dot(v, first) < 0f) v = -v; // sign-align for quaternion averaging
                        g.runRotSum += v;
                    }
                    else
                    {
                        g.runCount = 1;
                        g.runPosSum = p;
                        g.runFirstRot = q;
                        g.runRotSum = new Vector4(q.x, q.y, q.z, q.w);
                    }
                    g.lastPos = p; g.lastRot = q; g.haveLast = true;

                    if (g.runCount > g.bestCount)
                    {
                        g.bestCount = g.runCount;
                        g.bestPos = g.runPosSum / g.runCount;
                        Vector4 s = g.runRotSum; s.Normalize();
                        g.bestRot = new Quaternion(s.x, s.y, s.z, s.w).eulerAngles;
                    }
                }
                catch { }
            }
        }

        // Print every qualifying (prop, palm) pair's longest stable run as a paste-ready
        // V(...)/V(...), longest first — ONE vanilla eat covers every prop in both hands
        // (a piece that rides the can in the L palm AND gets grabbed by the R hand prints
        // both windows; pick the one that matches the def's holder). Props that never sat
        // still in either hand are listed once at the end (SkinnedMeshRenderer phantoms,
        // or simply never held) instead of going silent.
        private static void ReportStableGrips()
        {
            if (gripTracks.Count == 0) return;
            try
            {
                var ok = new List<GripTrack>();
                foreach (GripTrack g in gripTracks)
                    if (g.bestCount >= gripStableMinRun) ok.Add(g);
                ok.Sort((a, b) => b.bestCount.CompareTo(a.bestCount));

                var qualified = new HashSet<string>();
                foreach (GripTrack g in ok) qualified.Add(g.propName);
                var missed = new List<string>();
                foreach (GripTrack g in gripTracks)
                    if (!qualified.Contains(g.propName) && !missed.Contains(g.propName)) missed.Add(g.propName);

                var sb = new StringBuilder();
                sb.AppendLine("[FoodRecon][GRIP-AVG] ===== stable in-hand grips (longest still runs, both palms) =====");
                foreach (GripTrack g in ok)
                    sb.AppendLine($"[FoodRecon][GRIP-AVG] {g.propName,-24} in {g.palmLabel} ({g.bestCount,4}f):  " +
                                  $"pos: V({g.bestPos.x:0.###}f, {g.bestPos.y:0.###}f, {g.bestPos.z:0.###}f)  " +
                                  $"rot: V({g.bestRot.x:0.#}f, {g.bestRot.y:0.#}f, {g.bestRot.z:0.#}f)");
                if (ok.Count == 0)
                    sb.AppendLine($"[FoodRecon][GRIP-AVG] nothing held still within {gripStableMaxDist}m of a palm — raise gripStableMaxDist to force values.");
                if (missed.Count > 0)
                    sb.AppendLine($"[FoodRecon][GRIP-AVG] no stable run: {string.Join(", ", missed.ToArray())} (SkinnedMeshRenderer phantom or never held — see [TREE]).");
                Plugin.MyLog.LogInfo(sb.ToString());
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[FoodRecon] ReportStableGrips error: {ex}"); }
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
