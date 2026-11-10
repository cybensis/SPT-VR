using EFT.Weather;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovVR.Source.Misc
{
    // Per-item breakdown for the probe, all markers removed again when the run ends:
    //   buffers : every command buffer on the VR camera, each bracketed by a marker pair (the foreign buffers are
    //             re-added in their own order, so what runs and when is unchanged; re-checked every frame and
    //             before each eye, so buffers added later are picked up)
    //   effects : every OnRenderImage on the VR camera (temporary Harmony prefix/postfix)
    //   scripts : Update/LateUpdate + camera callbacks of the graphics mods + WeatherController.LateUpdate (CloudSix's
    //             sky bake rides it), and a few mod hooks timed on their own inside those (innerTimed)
    //   cameras : every other camera's render
    // What no item covers inside a block is printed as that block's "rest" (Unity's own work: G-buffer draws,
    // lights, skybox, transparents, skinning, buffers added mid-frame...).
    internal partial class StereoCostProbe
    {
        public static bool breakdown = true;           // off = the block rows only
        public static float breakdownMinMs = 0.01f;    // items below this (GPU and render thread) are summed into one line
        // Static methods timed on their own though they run inside a timed item (CloudSix's parts of its
        // WeatherController.LateUpdate postfix; its eye-adaptation meter inside PrismEffects). Missing = skipped.
        private static readonly string[] innerTimed =
        {
            "CloudSix.Source.VolCloudRenderer:UpdateSkyMap", "CloudSix.Source.VolCloudRenderer:UpdateCloudShadowMap",
            "CloudSix.Source.VolCloudRenderer:PublishSkyReflMap", "CloudSix.Source.SkyAtmosphere:Bake",
            "CloudSix.Source.EyeAdaptation:Meter",
            // ReflectSix's SSR and FogSix's fog both run inside the game's TOD_Scattering effect (same hook)
            "ReflectSix.Patches.SsrOpaquePatch:Prefix", "FogSix.Patches.FogScatteringPatch:Prefix",
        };
        private static readonly string[] scriptAssemblies = { "CloudSix", "TraceSix", "ReflectSix", "FogSix", "AOSix", "POMSix", "FSR4Bridge", "SPT-VR" };

        private const int EvEyeStart = 9;              // first buffer at BeforeGBuffer = an eye pass begins
        private const int ItemBase = 1000, MaxItems = 3000;   // item k: start id ItemBase + 2k, end id + 1
        private const int Buckets = 3, Blocks = 10;    // bucket 0 = once a frame, 1 = left eye, 2 = right eye

        private enum ItemKind { Buffer, Effect, Script, Camera }
        private class Item
        {
            public string name;
            public bool inner;                              // runs inside another item: kept out of the block sums
            public ItemKind kind;
            public CommandBuffer startCb, endCb;
            public readonly int[] block = { -1, -1, -1 };   // where it was first seen, per bucket
        }
        private static readonly List<Item> items = new List<Item>();
        // Buffers are identified by their native pointer: Camera.GetCommandBuffers hands back fresh managed wrappers on
        // every call, so reference identity never matches.
        private static readonly FieldInfo bufferPtrField = typeof(CommandBuffer).GetField("m_Ptr", BindingFlags.Instance | BindingFlags.NonPublic);
        private static IntPtr Ptr(CommandBuffer commandBuffer) => commandBuffer == null || bufferPtrField == null ? IntPtr.Zero : (IntPtr)bufferPtrField.GetValue(commandBuffer);
        private static readonly HashSet<IntPtr> ownBuffers = new HashSet<IntPtr>();
        private static readonly Dictionary<(IntPtr, CameraEvent), int> bufferItem = new Dictionary<(IntPtr, CameraEvent), int>();
        private static readonly Dictionary<CameraEvent, List<IntPtr>> layout = new Dictionary<CameraEvent, List<IntPtr>>();
        private static readonly Dictionary<CameraEvent, int> layoutLength = new Dictionary<CameraEvent, int>();
        private static readonly Dictionary<object, int> effectItem = new Dictionary<object, int>();
        private static readonly Dictionary<MethodBase, int> scriptItem = new Dictionary<MethodBase, int>();
        private static readonly Dictionary<Camera, int> cameraItem = new Dictionary<Camera, int>();
        private static readonly CameraEvent[] allEvents = (CameraEvent[])Enum.GetValues(typeof(CameraEvent));
        private static CommandBuffer eyeStartCb;
        private static Harmony harmony;
        private static int patchFailures;
        private static bool arrangeBroken;             // a self-check failed: markers stripped, no more arranging this run
        private const int MaxBuffersPerEvent = 200;    // more than this at one event = something is stacking, stop

        private static readonly string[] eyeBlockNames =
        {
            "before G-buffer", "G-buffer", "G-buffer -> lighting", "lighting", "lighting -> fwd opaque done",
            "fwd opaque done -> transparents", "transparents", "transparents -> image effects", "image effects",
            "image effects -> eye end",
        };
        private static readonly string[] frameBlockNames = { "Update -> render start", "render start -> first eye", "between the two eyes", "after the eyes" };

        private static int StartId(int k) => ItemBase + 2 * k;

        private static int NewItem(string name, ItemKind kind)
        {
            if (items.Count >= MaxItems) return -1;
            items.Add(new Item { name = name, kind = kind });
            return items.Count - 1;
        }

        //------------------------------------------------------------------------------------------------------------
        private static void BreakdownAttach(Camera camera)
        {
            items.Clear(); ownBuffers.Clear(); bufferItem.Clear(); layout.Clear(); layoutLength.Clear();
            effectItem.Clear(); scriptItem.Clear(); cameraItem.Clear(); patchFailures = 0;
            arrangeBroken = bufferPtrField == null;
            if (arrangeBroken) Plugin.MyLog.LogWarning("[StereoCost] CommandBuffer.m_Ptr not found - no per-buffer breakdown, block rows only");
            foreach (var (_, commandBuffer) in camAttached) ownBuffers.Add(Ptr(commandBuffer));
            eyeStartCb = new CommandBuffer { name = "SPT-VR StereoCostProbe eye start" };
            eyeStartCb.IssuePluginEvent(eventPtr, EvEyeStart);
            ownBuffers.Add(Ptr(eyeStartCb));
            Arrange(camera);
            if (!breakdown) return;
            harmony = new Harmony("com.matsix.sptvr.stereocostprobe");
            PatchEffects(camera);
            PatchScripts();
        }

        private static void BreakdownDetach(Camera camera)
        {
            Cleanup(camera);
            foreach (Item it in items) { it.startCb?.Release(); it.endCb?.Release(); }
            eyeStartCb?.Release();
            try { harmony?.UnpatchSelf(); }
            catch (Exception exception) { Plugin.MyLog.LogError("[StereoCost] unpatch failed: " + exception); }
            harmony = null;
        }

        private static bool IsProbeBuffer(CommandBuffer commandBuffer) =>
            ownBuffers.Contains(Ptr(commandBuffer)) || (commandBuffer.name != null && commandBuffer.name.StartsWith("SPT-VR StereoCostProbe", StringComparison.Ordinal));

        // Strips every probe buffer off the camera (ours, and by name any left by an earlier broken run); foreign
        // buffers stay as they are, in their order.
        private static void Cleanup(Camera camera)
        {
            if (camera == null) return;
            int removed = 0;
            foreach (CameraEvent cameraEvent in allEvents)
            {
                CommandBuffer[] cur = camera.GetCommandBuffers(cameraEvent);
                var keep = new List<CommandBuffer>();
                foreach (CommandBuffer commandBuffer in cur)
                    if (IsProbeBuffer(commandBuffer)) removed++; else keep.Add(commandBuffer);
                if (keep.Count == cur.Length) continue;
                foreach (CommandBuffer cb in cur) camera.RemoveCommandBuffer(cameraEvent, cb);
                foreach (CommandBuffer cb in keep) camera.AddCommandBuffer(cameraEvent, cb);
            }
            if (removed > 0) Plugin.MyLog.LogWarning($"[StereoCost] removed {removed} probe buffers from the camera");
        }

        // Eye-start marker first at BeforeGBuffer; with the breakdown on, a marker pair round every foreign buffer;
        // our block markers last at their event. Foreign buffers keep their relative order. Every rebuild is checked:
        // a wrong count strips the probe off the camera and stops arranging for the rest of the run.
        private static void Arrange(Camera camera)
        {
            if (arrangeBroken) return;
            foreach (CameraEvent cameraEvent in allEvents)
            {
                if (!breakdown && cameraEvent != CameraEvent.BeforeGBuffer) continue;   // block markers are already last
                CommandBuffer[] cur = camera.GetCommandBuffers(cameraEvent);
                if (cur.Length == 0) continue;
                if (cur.Length > MaxBuffersPerEvent) { Abort(camera, $"{cur.Length} buffers at {cameraEvent}"); return; }
                var foreign = new List<CommandBuffer>();
                var foreignPtr = new List<IntPtr>();
                foreach (CommandBuffer commandBuffer in cur)
                    if (!IsProbeBuffer(commandBuffer)) { foreign.Add(commandBuffer); foreignPtr.Add(Ptr(commandBuffer)); }
                if (layout.TryGetValue(cameraEvent, out var prev) && SameOrder(prev, foreignPtr) && layoutLength[cameraEvent] == cur.Length) continue;
                foreach (CommandBuffer cb in cur) camera.RemoveCommandBuffer(cameraEvent, cb);
                int n = 0;
                if (cameraEvent == CameraEvent.BeforeGBuffer) { camera.AddCommandBuffer(cameraEvent, eyeStartCb); n++; }
                foreach (CommandBuffer f in foreign)
                {
                    int k = breakdown ? BufferItem(f, cameraEvent) : -1;
                    if (k >= 0) { camera.AddCommandBuffer(cameraEvent, items[k].startCb); n++; }
                    camera.AddCommandBuffer(cameraEvent, f); n++;
                    if (k >= 0) { camera.AddCommandBuffer(cameraEvent, items[k].endCb); n++; }
                }
                foreach (var (e, cb) in camAttached) if (e == cameraEvent) { camera.AddCommandBuffer(cameraEvent, cb); n++; }
                int check = camera.GetCommandBuffers(cameraEvent).Length;
                if (check != n) { Abort(camera, $"{cameraEvent}: {check} buffers after arranging, expected {n}"); return; }
                layout[cameraEvent] = foreignPtr;
                layoutLength[cameraEvent] = n;
            }
        }

        private static void Abort(Camera camera, string why)
        {
            arrangeBroken = true;
            Plugin.MyLog.LogError($"[StereoCost] breakdown stopped ({why}) - probe markers removed, the block rows of this run are not usable");
            Cleanup(camera);
        }

        private static bool SameOrder(List<IntPtr> a, List<IntPtr> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static int BufferItem(CommandBuffer f, CameraEvent cameraEvent)
        {
            IntPtr p = Ptr(f);
            if (bufferItem.TryGetValue((p, cameraEvent), out int k)) return k;
            string name = string.IsNullOrEmpty(f.name) ? "(unnamed)" : f.name;
            k = NewItem($"{cameraEvent} | {name}", ItemKind.Buffer);
            if (k < 0) return -1;
            var s = new CommandBuffer { name = "SPT-VR StereoCostProbe item" };
            s.IssuePluginEvent(eventPtr, StartId(k));
            var e = new CommandBuffer { name = "SPT-VR StereoCostProbe item" };
            e.IssuePluginEvent(eventPtr, StartId(k) + 1);
            items[k].startCb = s; items[k].endCb = e;
            ownBuffers.Add(Ptr(s)); ownBuffers.Add(Ptr(e));
            bufferItem[(p, cameraEvent)] = k;
            return k;
        }

        //------------------------------------------------------------------------------------------------------------
        private static void PatchEffects(Camera camera)
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(StereoCostProbe), nameof(EffectPrefix))) { priority = Priority.First };
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(StereoCostProbe), nameof(EffectPostfix))) { priority = Priority.Last };
            var patched = new HashSet<MethodBase>();
            foreach (MonoBehaviour behaviour in camera.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null) continue;
                MethodInfo m = FindMessage(behaviour.GetType(), "OnRenderImage", typeof(RenderTexture), typeof(RenderTexture));
                if (m == null) continue;
                int k = NewItem("[effect] " + behaviour.GetType().Name, ItemKind.Effect);
                if (k < 0) break;
                effectItem[behaviour] = k;
                if (patched.Add(m)) TryPatch(m, prefix, postfix);
            }
        }

        private static void PatchScripts()
        {
            var live = new HashSet<Type>();
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (behaviour != null && behaviour.isActiveAndEnabled) live.Add(behaviour.GetType());
            var targets = new List<MethodInfo>();
            foreach (Type t in live)
            {
                if (t == typeof(StereoCostProbe) || Array.IndexOf(scriptAssemblies, t.Assembly.GetName().Name) < 0) continue;
                foreach (string name in new[] { "Update", "LateUpdate", "OnPreCull", "OnPreRender", "OnPostRender" })
                {
                    MethodInfo m = FindMessage(t, name);
                    if (m != null && !m.IsAbstract && !m.ContainsGenericParameters && !targets.Contains(m)) targets.Add(m);
                }
            }
            MethodInfo weather = AccessTools.Method(typeof(WeatherController), "LateUpdate");
            if (weather != null && !targets.Contains(weather)) targets.Add(weather);
            var inner = new HashSet<MethodInfo>();
            foreach (string spec in innerTimed)
            {
                string[] parts = spec.Split(':');
                Type t = AccessTools.TypeByName(parts[0]);
                MethodInfo m = t == null ? null : AccessTools.FirstMethod(t, mi => mi.Name == parts[1]);
                if (m != null && !targets.Contains(m)) { targets.Add(m); inner.Add(m); }
            }
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(StereoCostProbe), nameof(ScriptPrefix))) { priority = Priority.First };
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(StereoCostProbe), nameof(ScriptPostfix))) { priority = Priority.Last };
            foreach (MethodInfo m in targets)
            {
                string label = m == weather ? "WeatherController.LateUpdate (+ CloudSix)" : $"{m.DeclaringType.Name}.{m.Name}";
                if (inner.Contains(m)) label = "  inside: " + label;
                int k = NewItem($"[script] {label} ({m.DeclaringType.Assembly.GetName().Name})", ItemKind.Script);
                if (k < 0) break;
                items[k].inner = inner.Contains(m);
                scriptItem[m] = k;
                TryPatch(m, prefix, postfix);
            }
        }

        // The most-derived declaration = the one Unity calls.
        private static MethodInfo FindMessage(Type t, string name, params Type[] args)
        {
            for (; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, args, null);
                if (m != null) return m;
            }
            return null;
        }

        private static void TryPatch(MethodInfo m, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            try { harmony.Patch(m, prefix, postfix); }
            catch (Exception exception)
            {
                if (patchFailures++ < 5) Plugin.MyLog.LogWarning($"[StereoCost] could not time {m.DeclaringType?.Name}.{m.Name}: {exception.Message}");
            }
        }

        private static void EffectPrefix(object __instance)
        {
            if (running && effectItem.TryGetValue(__instance, out int k)) GL.IssuePluginEvent(eventPtr, StartId(k));
        }
        private static void EffectPostfix(object __instance)
        {
            if (running && effectItem.TryGetValue(__instance, out int k)) GL.IssuePluginEvent(eventPtr, StartId(k) + 1);
        }
        private static void ScriptPrefix(MethodBase __originalMethod)
        {
            if (running && scriptItem.TryGetValue(__originalMethod, out int k)) GL.IssuePluginEvent(eventPtr, StartId(k));
        }
        private static void ScriptPostfix(MethodBase __originalMethod)
        {
            if (running && scriptItem.TryGetValue(__originalMethod, out int k)) GL.IssuePluginEvent(eventPtr, StartId(k) + 1);
        }

        // Other cameras, from the probe's Camera.onPreCull / onPostRender.
        private static void OtherCameraMark(Camera camera, bool end)
        {
            if (!breakdown || camera == null) return;
            if (!cameraItem.TryGetValue(camera, out int k))
            {
                if (end) return;
                k = NewItem("[camera] " + camera.name, ItemKind.Camera);
                if (k < 0) return;
                cameraItem[camera] = k;
            }
            GL.IssuePluginEvent(eventPtr, StartId(k) + (end ? 1 : 0));
        }

        //------------------------------------------------------------------------------------------------------------
        // Parser side: per-frame scratch, folded into the mode's sums once the frame is accepted.
        private class ItemSums
        {
            public readonly double[,,] item = new double[Sources, Buckets, MaxItems];   // [src, bucket, item]
            public readonly int[,] ran = new int[Buckets, MaxItems];
            public readonly double[,,] blockSpan = new double[Sources, Buckets, Blocks];
            public readonly double[,,] blockItems = new double[Sources, Buckets, Blocks];
        }
        private static readonly double[,,] fItem = new double[Sources, Buckets, MaxItems];
        private static readonly double[,,] fBlockItems = new double[Sources, Buckets, Blocks];
        private static readonly List<(int bucket, int k)> fTouched = new List<(int, int)>();
        private static readonly bool[,] fTouchedFlag = new bool[Buckets, MaxItems];
        private static readonly double[,] openAt = new double[Sources, MaxItems];
        private static readonly int[] openBucket = new int[MaxItems], openBlock = new int[MaxItems];
        private static readonly List<int> fOpen = new List<int>();

        private static void ItemFrameReset()
        {
            foreach (var (b, k) in fTouched) { for (int src = 0; src < Sources; src++) fItem[src, b, k] = 0; fTouchedFlag[b, k] = false; }
            fTouched.Clear();
            Array.Clear(fBlockItems, 0, fBlockItems.Length);
            foreach (int k in fOpen) openAt[SrcWall, k] = double.NaN;
            fOpen.Clear();
        }

        // values: this event's wall / GPU / busy time (NaN where not measured)
        private static void ItemMark(int id, double[] values, int bucket, int block)
        {
            int k = (id - ItemBase) >> 1;
            if (k < 0 || k >= items.Count) return;
            if ((id & 1) == 0)
            {
                if (double.IsNaN(openAt[SrcWall, k])) fOpen.Add(k);
                for (int src = 0; src < Sources; src++) openAt[src, k] = values[src];
                openBucket[k] = bucket; openBlock[k] = block;
                return;
            }
            if (double.IsNaN(openAt[SrcWall, k])) return;
            int b = openBucket[k], blk = openBlock[k];
            if (!fTouchedFlag[b, k]) { fTouchedFlag[b, k] = true; fTouched.Add((b, k)); }
            bool top = !items[k].inner;
            for (int src = 0; src < Sources; src++)
            {
                double d = values[src] - openAt[src, k];
                if (double.IsNaN(d)) continue;
                fItem[src, b, k] += d;
                if (top) fBlockItems[src, b, blk] += d;
            }
            openAt[SrcWall, k] = double.NaN;
            if (items[k].block[b] < 0) items[k].block[b] = blk;
        }

        // Folds the accepted frame in: item sums + block spans (from the parser's per-eye event times).
        private static void ItemFrameCommit(Samples s, bool hasGpu, bool hasBusy, int eyes)
        {
            var sums = s.itemSums;
            foreach (var (b, k) in fTouched)
            {
                sums.item[SrcWall, b, k] += fItem[SrcWall, b, k];
                if (hasGpu) sums.item[SrcGpu, b, k] += fItem[SrcGpu, b, k];
                if (hasBusy) sums.item[SrcBusy, b, k] += fItem[SrcBusy, b, k];
                sums.ran[b, k]++;
            }
            for (int src = 0; src < Sources; src++)
            {
                if ((src == SrcGpu && !hasGpu) || (src == SrcBusy && !hasBusy)) continue;
                double rs = renderStartAt[src], fe = frameEndAt[src];
                // once a frame: an eye-less span after the first eye counts as "after the eyes" in left-only frames
                double[] fspan = { rs, tAll[src, 0, EvEyeStart] - rs, eyes == 2 ? tAll[src, 1, EvEyeStart] - tAll[src, 0, EvAfterEverything] : 0, fe - tAll[src, eyes - 1, EvAfterEverything] };
                if (eyes < 2) { fBlockItems[src, 0, 3] += fBlockItems[src, 0, 2]; fBlockItems[src, 0, 2] = 0; }
                for (int block = 0; block < 4; block++) AddBlock(sums, src, 0, block, fspan[block]);
                for (int e = 0; e < eyes; e++)
                {
                    double[] span =
                    {
                        tAll[src, e, EvBeforeGBuffer] - tAll[src, e, EvEyeStart], tAll[src, e, EvAfterGBuffer] - tAll[src, e, EvBeforeGBuffer],
                        tAll[src, e, EvBeforeLighting] - tAll[src, e, EvAfterGBuffer], tAll[src, e, EvAfterLighting] - tAll[src, e, EvBeforeLighting],
                        tAll[src, e, EvAfterOpaque] - tAll[src, e, EvAfterLighting], tAll[src, e, EvBeforeAlpha] - tAll[src, e, EvAfterOpaque],
                        tAll[src, e, EvAfterAlpha] - tAll[src, e, EvBeforeAlpha], tAll[src, e, EvBeforeFx] - tAll[src, e, EvAfterAlpha],
                        tAll[src, e, EvAfterFx] - tAll[src, e, EvBeforeFx], tAll[src, e, EvAfterEverything] - tAll[src, e, EvAfterFx],
                    };
                    for (int block = 0; block < Blocks; block++) AddBlock(sums, src, e + 1, block, span[block]);
                }
            }
        }
        private static void AddBlock(ItemSums sums, int src, int bucket, int block, double span)
        {
            if (double.IsNaN(span) || double.IsInfinity(span)) return;
            sums.blockSpan[src, bucket, block] += span;
            sums.blockItems[src, bucket, block] += fBlockItems[src, bucket, block];
        }

        //------------------------------------------------------------------------------------------------------------
        private static void ReportBreakdown(StringBuilder builder, Samples s)
        {
            if (!breakdown || items.Count == 0 || s.rtFrames == 0) return;
            var sums = s.itemSums;
            double nC = s.rtFrames, nG = Math.Max(1, s.gpuFrames), nB = Math.Max(1, s.busyFrames);
            builder.AppendLine($"[StereoCost] ===== breakdown, both eyes: mean ms per frame, {items.Count} items timed, {patchFailures} could not be patched =====");
            builder.AppendLine("[StereoCost]   (rt = render thread wall clock; busy = its own CPU work in that span)");
            builder.AppendLine("[StereoCost]   PER EYE                                                          GPU L  GPU R |  rt L   rt R | busy L busy R | ran%");
            for (int block = 0; block < Blocks; block++)
            {
                double bl = sums.blockSpan[1, 1, block] / nG, br = sums.blockSpan[1, 2, block] / nG;
                double cl = sums.blockSpan[0, 1, block] / nC, cr = sums.blockSpan[0, 2, block] / nC;
                double ul = sums.blockSpan[2, 1, block] / nB, ur = sums.blockSpan[2, 2, block] / nB;
                builder.AppendLine($"[StereoCost]   {eyeBlockNames[block],-64} {F(bl)} {F(br)} | {F(cl)} {F(cr)} | {F(ul)} {F(ur)} |");
                var list = new List<int>();
                for (int k = 0; k < items.Count; k++)
                    if (items[k].block[1] == block || (items[k].block[1] < 0 && items[k].block[2] == block)) list.Add(k);
                list.Sort((a, b) => (sums.item[1, 1, b] + sums.item[1, 2, b]).CompareTo(sums.item[1, 1, a] + sums.item[1, 2, a]));
                int small = 0; double smallG = 0, smallC = 0;
                foreach (int k in list)
                {
                    double gl = sums.item[1, 1, k] / nG, gr = sums.item[1, 2, k] / nG, rl = sums.item[0, 1, k] / nC, rr = sums.item[0, 2, k] / nC;
                    double ul2 = sums.item[2, 1, k] / nB, ur2 = sums.item[2, 2, k] / nB;
                    if (Math.Max(gl, gr) < breakdownMinMs && Math.Max(rl, rr) < breakdownMinMs) { small++; smallG += gl + gr; smallC += rl + rr; continue; }
                    string ran = (100.0 * Math.Max(sums.ran[1, k], sums.ran[2, k]) / s.rtFrames).ToString("0");
                    builder.AppendLine($"[StereoCost]     {Trim(items[k].name, 62),-62} {F(gl)} {F(gr)} | {F(rl)} {F(rr)} | {F(ul2)} {F(ur2)} | {ran,4}");
                }
                if (small > 0) builder.AppendLine($"[StereoCost]     {small} more under {breakdownMinMs} ms (both eyes together: GPU {smallG:0.000}, rt {smallC:0.000})");
                double restL = bl - sums.blockItems[1, 1, block] / nG, restR = br - sums.blockItems[1, 2, block] / nG;
                double restCL = cl - sums.blockItems[0, 1, block] / nC, restCR = cr - sums.blockItems[0, 2, block] / nC;
                double restUL = ul - sums.blockItems[2, 1, block] / nB, restUR = ur - sums.blockItems[2, 2, block] / nB;
                if (list.Count > 0 || Math.Abs(restL) >= 0.005)
                    builder.AppendLine($"[StereoCost]     {"(rest: Unity's own work + unbracketed)",-62} {F(restL)} {F(restR)} | {F(restCL)} {F(restCR)} | {F(restUL)} {F(restUR)} |");
            }
            builder.AppendLine("[StereoCost]   ONCE A FRAME                                                       GPU        |   rt          |  busy         | ran%");
            for (int block = 0; block < 4; block++)
            {
                double bg = sums.blockSpan[1, 0, block] / nG, bc = sums.blockSpan[0, 0, block] / nC, bu = sums.blockSpan[2, 0, block] / nB;
                builder.AppendLine($"[StereoCost]   {frameBlockNames[block],-64} {F(bg)}        | {F(bc)}        | {F(bu)}        |");
                var list = new List<int>();
                for (int k = 0; k < items.Count; k++) if (items[k].block[0] == block) list.Add(k);
                list.Sort((a, b) => sums.item[1, 0, b].CompareTo(sums.item[1, 0, a]));
                int small = 0; double smallG = 0, smallC = 0;
                foreach (int k in list)
                {
                    double g = sums.item[1, 0, k] / nG, c = sums.item[0, 0, k] / nC, u = sums.item[2, 0, k] / nB;
                    if (g < breakdownMinMs && c < breakdownMinMs) { small++; smallG += g; smallC += c; continue; }
                    string ran = (100.0 * sums.ran[0, k] / s.rtFrames).ToString("0");
                    builder.AppendLine($"[StereoCost]     {Trim(items[k].name, 62),-62} {F(g)}        | {F(c)}        | {F(u)}        | {ran,4}");
                }
                if (small > 0) builder.AppendLine($"[StereoCost]     {small} more under {breakdownMinMs} ms (together: GPU {smallG:0.000}, rt {smallC:0.000})");
                double rest = bg - sums.blockItems[1, 0, block] / nG, restC = bc - sums.blockItems[0, 0, block] / nC, restU = bu - sums.blockItems[2, 0, block] / nB;
                if (list.Count > 0 || Math.Abs(rest) >= 0.005)
                    builder.AppendLine($"[StereoCost]     {"(rest)",-62} {F(rest)}        | {F(restC)}        | {F(restU)}        |");
            }
        }
        private static string F(double v) => double.IsNaN(v) ? "     -" : v.ToString("0.000").PadLeft(6);
        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "~";
    }
}
