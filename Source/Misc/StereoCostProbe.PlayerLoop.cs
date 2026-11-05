using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;

namespace TarkovVR.Source.Misc
{
    // The main thread's whole frame, step by step:
    //   player loop   : a marker system in front of every step of Unity's player loop (+ one closing each phase), so a step's time is
    //                   the gap to the next marker, wall and busy. FixedUpdate's steps add up over all of the frame's physics ticks.
    //   other cameras : every camera but the headset's, pre-cull -> post-render on the main thread (a camera drawn from inside another
    //                   camera's pass counts in both)
    //   scripts       : in a segment of their own at the end (so the timing's own cost stays out of the other segments), every live
    //                   script's Update / LateUpdate / FixedUpdate
    internal partial class StereoCostProbe
    {
        public static bool loopBreakdown = true;
        public static bool scriptSegment = true;
        public static float loopMinMs = 0.05f;       // steps / cameras / scripts below this are summed into one line
        public static int scriptRows = 40;

        private class LoopStep
        {
            public string name;
            public readonly double[] wall = new double[ModeCount], busy = new double[ModeCount];
        }
        private static readonly List<LoopStep> loopSteps = new List<LoopStep>();
        private static PlayerLoopSystem originalLoop;
        private static bool loopInstalled;
        private static int openStep = -1;
        private static long openStepT;
        private static ulong openStepC;

        private class OtherCamera
        {
            public string name;
            public long openT;
            public ulong openC;
            public readonly double[] wall = new double[ModeCount], busy = new double[ModeCount];
            public readonly long[] passes = new long[ModeCount];
        }
        private static readonly Dictionary<Camera, OtherCamera> otherCameras = new Dictionary<Camera, OtherCamera>();

        private static Harmony scriptHarmony;
        private static int scriptMethods, scriptPatchFailures;
        private static double scriptPatchSeconds;

        //------------------------------------------------------------------------------------------------------------
        private static void LoopAttach()
        {
            loopSteps.Clear(); otherCameras.Clear(); openStep = -1; loopInstalled = false;
            scriptMethods = scriptPatchFailures = 0; scriptPatchSeconds = 0;
            if (!loopBreakdown) return;
            try
            {
                originalLoop = PlayerLoop.GetCurrentPlayerLoop();
                PlayerLoopSystem root = originalLoop;
                PlayerLoopSystem[] phases = root.subSystemList;
                if (phases == null) return;
                // New arrays all the way down: the struct copies share the original's arrays, which must stay as they are to put back
                var newPhases = new PlayerLoopSystem[phases.Length];
                for (int p = 0; p < phases.Length; p++)
                {
                    PlayerLoopSystem phase = phases[p];
                    string phaseName = phase.type != null ? phase.type.Name : "phase " + p;
                    PlayerLoopSystem[] steps = phase.subSystemList ?? new PlayerLoopSystem[0];
                    var list = new List<PlayerLoopSystem>(steps.Length * 2 + 1);
                    foreach (PlayerLoopSystem step in steps)
                    {
                        loopSteps.Add(new LoopStep { name = phaseName + "." + (step.type != null ? step.type.Name : "?") });
                        list.Add(LoopMarker(loopSteps.Count - 1));
                        list.Add(step);
                    }
                    list.Add(LoopMarker(-1));
                    phase.subSystemList = list.ToArray();
                    newPhases[p] = phase;
                }
                root.subSystemList = newPhases;
                PlayerLoop.SetPlayerLoop(root);
                loopInstalled = true;
            }
            catch (Exception e) { Plugin.MyLog.LogWarning("[StereoCost] player-loop timing unavailable: " + e.Message); }
        }

        private static void LoopDetach()
        {
            if (!loopInstalled) return;
            try { PlayerLoop.SetPlayerLoop(originalLoop); }
            catch (Exception e) { Plugin.MyLog.LogError("[StereoCost] could not put the player loop back: " + e); }
            loopInstalled = false;
            openStep = -1;
        }

        private static PlayerLoopSystem LoopMarker(int step)
        {
            return new PlayerLoopSystem { type = typeof(StereoCostProbe), updateDelegate = () => LoopMark(step) };
        }

        // Closes the step that was running, opens the next (-1 = a phase ended)
        private static void LoopMark(int step)
        {
            long t = Stopwatch.GetTimestamp();
            ulong c = MainCycles();
            if (openStep >= 0 && running && sampling)
            {
                LoopStep s = loopSteps[openStep];
                s.wall[(int)mode] += (t - openStepT) * tickMs;
                if (tscPerMs > 0 && c >= openStepC && openStepC != 0) s.busy[(int)mode] += (c - openStepC) / tscPerMs;
            }
            openStep = step; openStepT = t; openStepC = c;
        }

        //------------------------------------------------------------------------------------------------------------
        private static void OtherCameraMain(Camera cam, bool end)
        {
            if (!loopBreakdown || cam == null) return;
            long t = Stopwatch.GetTimestamp();
            ulong c = MainCycles();
            if (!otherCameras.TryGetValue(cam, out OtherCamera oc))
            {
                if (end) return;
                oc = new OtherCamera { name = CameraLabel(cam) };
                otherCameras[cam] = oc;
            }
            if (!end) { oc.openT = t; oc.openC = c; return; }
            if (oc.openT == 0) return;
            if (sampling)
            {
                oc.wall[(int)mode] += (t - oc.openT) * tickMs;
                if (tscPerMs > 0 && c >= oc.openC && oc.openC != 0) oc.busy[(int)mode] += (c - oc.openC) / tscPerMs;
                oc.passes[(int)mode]++;
            }
            oc.openT = 0;
        }

        // The camera's name and the object it hangs under (which screen / system it belongs to)
        private static string CameraLabel(Camera cam)
        {
            Transform parent = cam.transform.parent;
            string under = parent == null ? "" : parent.parent == null ? $" (under {parent.name})" : $" (under {parent.parent.name}/{parent.name})";
            return cam.name + under + (cam.targetTexture != null ? $" -> {cam.targetTexture.width}x{cam.targetTexture.height}" : "");
        }

        //------------------------------------------------------------------------------------------------------------
        // Every live script's Update / LateUpdate / FixedUpdate, timed by the same prefix/postfix as the camera messages
        private static void PatchAllScripts()
        {
            scriptMethods = scriptPatchFailures = 0;
            var watch = Stopwatch.StartNew();
            scriptHarmony = new Harmony("com.matsix.sptvr.stereocostprobe.scripts");
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(StereoCostProbe), nameof(MainPrefix))) { priority = Priority.First };
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(StereoCostProbe), nameof(MainPostfix))) { priority = Priority.Last };
            var types = new HashSet<Type>();
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (mb != null && mb.isActiveAndEnabled) types.Add(mb.GetType());
            foreach (Type type in types)
            {
                if (type.Assembly == typeof(StereoCostProbe).Assembly && type.Name.StartsWith("StereoCostProbe", StringComparison.Ordinal)) continue;
                foreach (string message in new[] { "Update", "LateUpdate", "FixedUpdate" })
                {
                    MethodInfo method = FindMessage(type, message);
                    if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.DeclaringType.IsGenericType
                        || mainMethodItem.ContainsKey(method)) continue;
                    int k = NewMainItem($"{method.DeclaringType.Name}.{method.Name} ({method.DeclaringType.Assembly.GetName().Name})", false);
                    mainItems[k].script = true;
                    mainMethodItem[method] = k;
                    try { scriptHarmony.Patch(method, prefix, postfix); scriptMethods++; }
                    catch (Exception e)
                    {
                        mainMethodItem.Remove(method);
                        if (scriptPatchFailures++ < 5) Plugin.MyLog.LogWarning($"[StereoCost] could not time {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
                    }
                }
            }
            scriptPatchSeconds = watch.Elapsed.TotalSeconds;
        }

        private static void UnpatchAllScripts()
        {
            try { scriptHarmony?.UnpatchSelf(); }
            catch (Exception e) { Plugin.MyLog.LogError("[StereoCost] script unpatch failed: " + e); }
            scriptHarmony = null;
        }

        //------------------------------------------------------------------------------------------------------------
        private static readonly Mode[] loopModes = { Mode.Both, Mode.LeftOnly, Mode.NoShadows, Mode.Occlusion };

        private static void ReportLoop(StringBuilder sb)
        {
            if (!loopBreakdown) return;
            if (loopSteps.Count > 0)
            {
                sb.AppendLine($"[StereoCost] ===== MAIN THREAD step by step (Unity's player loop, {loopSteps.Count} steps): mean ms a frame, wall / BUSY =====");
                sb.AppendLine("[StereoCost]   step                                                     both wall  busy |  left wall  busy | no-shad wall  busy | occl-on wall busy");
                var order = new List<int>();
                for (int k = 0; k < loopSteps.Count; k++) order.Add(k);
                order.Sort((a, b) => loopSteps[b].wall[0].CompareTo(loopSteps[a].wall[0]));
                double[] totalWall = new double[ModeCount], totalBusy = new double[ModeCount], smallWall = new double[ModeCount], smallBusy = new double[ModeCount];
                int small = 0;
                foreach (int k in order)
                {
                    LoopStep st = loopSteps[k];
                    bool shown = false;
                    foreach (Mode m in loopModes)
                    {
                        totalWall[(int)m] += PerFrame(st.wall[(int)m], m);
                        totalBusy[(int)m] += PerFrame(st.busy[(int)m], m);
                        if (PerFrame(st.wall[(int)m], m) >= loopMinMs) shown = true;
                    }
                    if (!shown)
                    {
                        small++;
                        foreach (Mode m in loopModes) { smallWall[(int)m] += PerFrame(st.wall[(int)m], m); smallBusy[(int)m] += PerFrame(st.busy[(int)m], m); }
                        continue;
                    }
                    sb.AppendLine($"[StereoCost]     {Trim(st.name, 52),-52} {ModeColumns(st.wall, st.busy)}");
                }
                if (small > 0) sb.AppendLine($"[StereoCost]     {$"{small} more under {loopMinMs} ms",-52} {ModeColumns(smallWall, smallBusy, true)}");
                sb.AppendLine($"[StereoCost]   {"all steps (= the frame)",-54} {ModeColumns(totalWall, totalBusy, true)}");
            }
            if (otherCameras.Count > 0)
            {
                sb.AppendLine("[StereoCost] ===== MAIN THREAD, cameras other than the headset's (pre-cull -> post-render): mean ms a frame, wall / BUSY =====");
                sb.AppendLine("[StereoCost]   camera                                                   both wall  busy |  left wall  busy | no-shad wall  busy | occl-on wall busy | passes a frame");
                var cams = new List<OtherCamera>(otherCameras.Values);
                cams.Sort((a, b) => b.wall[0].CompareTo(a.wall[0]));
                double[] totalWall = new double[ModeCount], totalBusy = new double[ModeCount];
                foreach (OtherCamera oc in cams)
                {
                    foreach (Mode m in loopModes) { totalWall[(int)m] += PerFrame(oc.wall[(int)m], m); totalBusy[(int)m] += PerFrame(oc.busy[(int)m], m); }
                    if (PerFrame(oc.wall[0], Mode.Both) < 0.005 && PerFrame(oc.wall[1], Mode.LeftOnly) < 0.005) continue;
                    sb.AppendLine($"[StereoCost]     {Trim(oc.name, 52),-52} {ModeColumns(oc.wall, oc.busy)} | {PerFrame(oc.passes[0], Mode.Both):0.0}");
                }
                sb.AppendLine($"[StereoCost]   {"all other cameras",-54} {ModeColumns(totalWall, totalBusy, true)}");
            }
        }

        // Per-frame means for the three compared modes: "wall busy | wall busy | wall busy"
        private static string ModeColumns(double[] wall, double[] busy, bool perFrameAlready = false)
        {
            var sb = new StringBuilder();
            foreach (Mode m in loopModes)
            {
                double w = perFrameAlready ? wall[(int)m] : PerFrame(wall[(int)m], m);
                double b = perFrameAlready ? busy[(int)m] : PerFrame(busy[(int)m], m);
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append($"{F(w)} {F(b)}");
            }
            return sb.ToString();
        }

        private static double PerFrame(double sum, Mode m)
        {
            int frames = samples != null && samples[(int)m] != null ? samples[(int)m].frames : 0;
            return frames > 0 ? sum / frames : double.NaN;
        }

        private static void ReportScripts(StringBuilder sb)
        {
            if (scriptHarmony == null && scriptMethods == 0) return;
            Samples s = samples[(int)Mode.Scripts];
            if (s.frames == 0) { sb.AppendLine("[StereoCost] ===== scripts segment: no frames sampled ====="); return; }
            int seg = (int)Mode.Scripts;
            sb.AppendLine($"[StereoCost] ===== MAIN THREAD scripts (own segment, both eyes, {s.frames} frames): every live Update / LateUpdate / FixedUpdate, "
                + $"{scriptMethods} methods timed, {scriptPatchFailures} could not be, patching took {scriptPatchSeconds:0.0} s; mean ms a frame (wall) =====");
            sb.AppendLine($"[StereoCost]   (this segment's frame: main busy {Fmt(Median(s.mainFrameBusy))} vs {Fmt(Median(samples[0].mainFrameBusy))} untimed = the timing's own cost)");
            var order = new List<int>();
            for (int k = 0; k < mainItems.Count; k++) if (mainItems[k].script) order.Add(k);
            order.Sort((a, b) => Total(mainItems[b], seg).CompareTo(Total(mainItems[a], seg)));
            double total = 0, rest = 0; long totalCalls = 0, restCalls = 0; int shown = 0, restN = 0;
            foreach (int k in order)
            {
                double ms = Total(mainItems[k], seg) / s.frames;
                long calls = mainItems[k].calls[seg, 0] + mainItems[k].calls[seg, 1] + mainItems[k].calls[seg, 2];
                total += ms; totalCalls += calls;
                if (shown < scriptRows && ms >= mainMinMs)
                {
                    sb.AppendLine($"[StereoCost]     {Trim(mainItems[k].name, 70),-70} {F(ms)} | calls a frame {calls / (double)s.frames:0.0}");
                    shown++;
                }
                else if (calls > 0) { rest += ms; restCalls += calls; restN++; }
            }
            sb.AppendLine($"[StereoCost]     {$"{restN} more",-70} {F(rest)} | calls a frame {restCalls / (double)s.frames:0.0}");
            sb.AppendLine($"[StereoCost]   {"all timed scripts",-72} {F(total)} | calls a frame {totalCalls / (double)s.frames:0.0}");
        }

        private static double Total(MainItem item, int seg) => item.ms[seg, 0] + item.ms[seg, 1] + item.ms[seg, 2];

        // Lights that draw shadow maps right now, by type
        private static string ShadowLightCount()
        {
            int directional = 0, spot = 0, point = 0, other = 0;
            foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>())
            {
                if (l == null || !l.isActiveAndEnabled || l.shadows == LightShadows.None || l.intensity <= 0) continue;
                switch (l.type)
                {
                    case LightType.Directional: directional++; break;
                    case LightType.Spot: spot++; break;
                    case LightType.Point: point++; break;
                    default: other++; break;
                }
            }
            return $"lights with shadows on: sun/directional {directional}, spot {spot}, point {point}{(other > 0 ? $", other {other}" : "")} (shadow quality {QualitySettings.shadows})";
        }
    }
}
