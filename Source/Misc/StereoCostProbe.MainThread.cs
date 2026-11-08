using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace TarkovVR.Source.Misc
{
    // The probe's main-thread breakdown per eye: what script code runs on the main thread for each eye pass, and how long.
    //   messages  : every live MonoBehaviour's per-camera messages (OnPreCull / OnPreRender / OnPostRender / OnRenderImage /
    //               OnWillRenderObject / OnBecameVisible / OnBecameInvisible / OnRenderObject), timed by a temporary Harmony
    //               prefix/postfix
    //   callbacks : every handler on Camera.onPreCull / onPreRender / onPostRender (the probe's own excepted), each wrapped for
    //               the run and put back after
    // Each call goes to the eye pass it ran in (left / right) when it's for the headset camera, else to "other". What's left of
    // an eye's main-thread time once these are taken out is Unity's own work (culling, render queues, shadow casters...).
    // Harmony adds a fraction of a microsecond a call: the call counts show how much that is.
    internal partial class StereoCostProbe
    {
        public static bool mainBreakdown = true;
        public static float mainMinMs = 0.01f;         // items below this (both eyes and other together) are summed into one line
        private static readonly string[] cameraMessages =
            { "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderImage", "OnWillRenderObject", "OnBecameVisible", "OnBecameInvisible", "OnRenderObject" };
        // Unity's static camera callbacks (public fields): read and written through these, named for the report
        private static readonly (string name, Func<Camera.CameraCallback> get, Action<Camera.CameraCallback> set)[] cameraCallbacks =
        {
            ("onPreCull", () => Camera.onPreCull, callback => Camera.onPreCull = callback),
            ("onPreRender", () => Camera.onPreRender, callback => Camera.onPreRender = callback),
            ("onPostRender", () => Camera.onPostRender, callback => Camera.onPostRender = callback),
        };

        private class MainItem
        {
            public string name;
            public bool onCameraObject;                     // a camera's own message: goes by the object it's on
            public bool script;                             // an Update / LateUpdate / FixedUpdate, timed in the scripts segment only
            public readonly double[,] ms = new double[ModeCount, 3];   // [mode, bucket: 0 other, 1 left eye, 2 right eye]
            public readonly long[,] calls = new long[ModeCount, 3];
        }
        private static readonly List<MainItem> mainItems = new List<MainItem>();
        private static readonly Dictionary<MethodBase, int> mainMethodItem = new Dictionary<MethodBase, int>();
        private static readonly Dictionary<Delegate, Delegate> wrappedCallbacks = new Dictionary<Delegate, Delegate>();
        private static Harmony mainHarmony;
        private static int mainPatchFailures;

        private static int NewMainItem(string name, bool onCameraObject)
        {
            mainItems.Add(new MainItem { name = name, onCameraObject = onCameraObject });
            return mainItems.Count - 1;
        }

        //------------------------------------------------------------------------------------------------------------
        private static void MainThreadAttach(Camera cam)
        {
            mainItems.Clear(); mainMethodItem.Clear(); wrappedCallbacks.Clear(); mainPatchFailures = 0;
            if (!mainBreakdown) return;
            WrapCameraCallbacks();
            mainHarmony = new Harmony("com.matsix.sptvr.stereocostprobe.main");
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(StereoCostProbe), nameof(MainPrefix))) { priority = Priority.First };
            var postfix = new HarmonyMethod(AccessTools.Method(typeof(StereoCostProbe), nameof(MainPostfix))) { priority = Priority.Last };
            var types = new HashSet<Type>();
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (mb != null && mb.isActiveAndEnabled) types.Add(mb.GetType());
            foreach (Type type in types)
            {
                if (type.Assembly == typeof(StereoCostProbe).Assembly && type.Name.StartsWith("StereoCostProbe", StringComparison.Ordinal)) continue;
                foreach (string message in cameraMessages)
                {
                    MethodInfo method = message == "OnRenderImage" ? FindMessage(type, message, typeof(RenderTexture), typeof(RenderTexture)) : FindMessage(type, message);
                    if (method == null || method.IsAbstract || method.ContainsGenericParameters || mainMethodItem.ContainsKey(method)) continue;
                    bool onCamera = message == "OnPreCull" || message == "OnPreRender" || message == "OnPostRender" || message == "OnRenderImage";
                    int k = NewMainItem($"{method.DeclaringType.Name}.{method.Name} ({method.DeclaringType.Assembly.GetName().Name})", onCamera);
                    mainMethodItem[method] = k;
                    try { mainHarmony.Patch(method, prefix, postfix); }
                    catch (Exception e)
                    {
                        mainMethodItem.Remove(method);
                        if (mainPatchFailures++ < 5) Plugin.MyLog.LogWarning($"[StereoCost] could not time {method.DeclaringType?.Name}.{method.Name} on the main thread: {e.Message}");
                    }
                }
            }
        }

        private static void MainThreadDetach()
        {
            UnwrapCameraCallbacks();
            try { mainHarmony?.UnpatchSelf(); }
            catch (Exception e) { Plugin.MyLog.LogError("[StereoCost] main-thread unpatch failed: " + e); }
            mainHarmony = null;
        }

        // Every handler on Unity's camera callbacks, each in a timing wrapper (Camera's static delegate fields)
        private static void WrapCameraCallbacks()
        {
            foreach (var (name, get, set) in cameraCallbacks)
            {
                Camera.CameraCallback current = get();
                if (current == null) continue;
                Camera.CameraCallback rebuilt = null;
                foreach (Delegate handler in current.GetInvocationList())
                {
                    var inner = (Camera.CameraCallback)handler;
                    MethodInfo method = handler.Method;
                    int k = NewMainItem($"[Camera.{name}] {method.DeclaringType?.Name}.{method.Name} ({method.DeclaringType?.Assembly.GetName().Name})", false);
                    Camera.CameraCallback wrapper = camera => TimedCallback(inner, camera, k);
                    wrappedCallbacks[wrapper] = inner;
                    rebuilt += wrapper;
                }
                set(rebuilt);
            }
        }

        // Our wrappers back to the handlers they wrap; anything added during the run stays as it is
        private static void UnwrapCameraCallbacks()
        {
            foreach (var (_, get, set) in cameraCallbacks)
            {
                Camera.CameraCallback current = get();
                if (current == null) continue;
                Camera.CameraCallback rebuilt = null;
                foreach (Delegate handler in current.GetInvocationList())
                    rebuilt += (Camera.CameraCallback)(wrappedCallbacks.TryGetValue(handler, out Delegate original) ? original : handler);
                set(rebuilt);
            }
            wrappedCallbacks.Clear();
        }

        private static void TimedCallback(Camera.CameraCallback inner, Camera camera, int k)
        {
            if (!running || !sampling) { inner(camera); return; }
            long start = Stopwatch.GetTimestamp();
            try { inner(camera); }
            finally { MainAdd(k, camera == probeCam ? EyeBucket() : 0, Stopwatch.GetTimestamp() - start); }
        }

        private static void MainPrefix(out long __state)
        {
            __state = running && sampling ? Stopwatch.GetTimestamp() : 0;
        }

        private static void MainPostfix(MethodBase __originalMethod, object __instance, long __state)
        {
            if (__state == 0 || !running || !sampling || !mainMethodItem.TryGetValue(__originalMethod, out int k)) return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            bool forHeadset;
            if (mainItems[k].onCameraObject)
                forHeadset = __instance is Component component && probeCam != null && component != null && component.gameObject == probeCam.gameObject;
            else
                forHeadset = Camera.current == probeCam;
            MainAdd(k, forHeadset ? EyeBucket() : 0, elapsed);
        }

        private static int EyeBucket() => eyePassIndex < 2 ? eyePassIndex + 1 : 0;

        private static void MainAdd(int k, int bucket, long ticks)
        {
            MainItem item = mainItems[k];
            item.ms[(int)mode, bucket] += ticks * tickMs;
            item.calls[(int)mode, bucket]++;
        }

        // (sums go straight to the mode while sampling; the frame count is the probe's)
        private static void MainFrameReset() { }
        private static void MainFrameCommit(Samples s, int eyePasses) { }

        //------------------------------------------------------------------------------------------------------------
        private static void ReportMainThread(StringBuilder sb, Samples s)
        {
            if (!mainBreakdown || mainItems.Count == 0 || s.frames == 0) return;
            double frames = s.frames;
            const int both = 0;
            sb.AppendLine($"[StereoCost] ===== MAIN THREAD per eye, both eyes: mean ms per frame (wall), {mainItems.Count} items timed, {mainPatchFailures} could not be patched =====");
            sb.AppendLine("[StereoCost]   item                                                              L      R  other | calls a frame L / R / other");
            var order = new List<int>();
            for (int k = 0; k < mainItems.Count; k++) order.Add(k);
            order.Sort((a, b) => (mainItems[b].ms[both, 1] + mainItems[b].ms[both, 2] + mainItems[b].ms[both, 0]).CompareTo(mainItems[a].ms[both, 1] + mainItems[a].ms[both, 2] + mainItems[a].ms[both, 0]));
            double[] total = new double[3];
            int small = 0; double smallMs = 0;
            foreach (int k in order)
            {
                MainItem item = mainItems[k];
                if (item.script) continue;
                double left = item.ms[both, 1] / frames, right = item.ms[both, 2] / frames, other = item.ms[both, 0] / frames;
                total[0] += other; total[1] += left; total[2] += right;
                if (left + right + other < mainMinMs) { if (item.calls[both, 0] + item.calls[both, 1] + item.calls[both, 2] > 0) { small++; smallMs += left + right + other; } continue; }
                sb.AppendLine($"[StereoCost]     {Trim(item.name, 62),-62} {F(left)} {F(right)} {F(other)} | {item.calls[both, 1] / frames:0.0} / {item.calls[both, 2] / frames:0.0} / {item.calls[both, 0] / frames:0.0}");
            }
            if (small > 0) sb.AppendLine($"[StereoCost]     {small} more under {mainMinMs} ms (together {smallMs:0.000})");
            sb.AppendLine($"[StereoCost]   {"timed script work",-64} {F(total[1])} {F(total[2])} {F(total[0])}");
            double eyeLeft = Median(s.mainEyeCullWall[0]) + Median(s.mainEye[0]), eyeRight = Median(s.mainEyeCullWall[1]) + Median(s.mainEye[1]);
            sb.AppendLine($"[StereoCost]   {"eye on the main thread (cull + pass, wall medians)",-64} {F(eyeLeft)} {F(eyeRight)}");
            sb.AppendLine($"[StereoCost]   {"-> Unity's own per-eye work (eye minus timed scripts)",-64} {F(eyeLeft - total[1])} {F(eyeRight - total[2])}");
        }
    }
}
