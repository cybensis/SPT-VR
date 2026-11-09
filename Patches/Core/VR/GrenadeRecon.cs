using System.Collections.Generic;
using System.Text;
using EFT;
using HarmonyLib;
using UnityEngine;
using GrenadeHandsController = EFT.Player.GrenadeHandsController;

namespace TarkovVR.Patches.Core.VR
{
    // Recon for the ring pull (his grenade plan, round 3): what each grenade's model has to pull, and how Tarkov's own pin-pull
    // animation moves it and the left hand. Logging only. Every grenade type's bones once, when it's first taken in hand; every pin
    // pull (HighThrowOperation.Start = the pin-pull clip) sampled for sampleTime: the arms animator's layer 1, the parts whose names
    // look like a ring / pin / lever, and the clip's left palm and index tip, all in the grenade's Weapon_root frame - which part
    // moves, when, along which axis, and where the finger is when it starts (the grab pose).
    //
    // Frame rule: read in Player.LateUpdate's prefix, where the bones are the animator's pose (before any IK): relative poses inside
    // the grenade's own rig only, like the eating recon.
    [HarmonyPatch]
    internal static class GrenadeRecon
    {
        public static bool enabled = true;
        public static float sampleTime = 2.5f;      // s of a pin pull sampled
        public static float lineEvery = 0.1f;       // s between the timeline's lines

        private static readonly string[] partWords = { "ring", "pin", "chek", "kolc", "kolts", "safety", "lever", "spoon", "fuse", "cap", "cord", "pull" };
        private static readonly HashSet<string> dumpedTemplates = new HashSet<string>();

        private sealed class Part
        {
            public Transform transform;
            public string path;
            public Vector3 rest;
            public float movedAt = -1f;
            public float movedAtStateTime;
            public int movedAtState;
            public Vector3 pullAxis;
            public float maxTravel;
            public bool wasActive;
            public string activeChanges = "";
        }

        private static GrenadeHandsController sampling;
        private static float sampleStart;
        private static float lastLine;
        private static readonly List<Part> parts = new List<Part>();
        private static Transform root, palm, indexTip;
        private static readonly StringBuilder timeline = new StringBuilder();
        private static readonly Dictionary<int, Vector2> states = new Dictionary<int, Vector2>();   // hash -> first / last seen (s)
        private static string grabPose;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GrenadeHandsController), "Spawn")]
        private static void DumpBones(GrenadeHandsController __instance)
        {
            try
            {
                if (!enabled || __instance?._player == null || !__instance._player.IsYourPlayer || __instance.Item == null)
                    return;
                string template = __instance.Item.TemplateId.ToString();
                if (!dumpedTemplates.Add(template))
                    return;
                Transform top = __instance.ControllerGameObject != null ? __instance.ControllerGameObject.transform : null;
                if (top == null)
                    return;
                var line = new StringBuilder($"[GrenadeRecon] {__instance.Item.LocalizedShortName()} ({template}), model '{top.name}': bones");
                AppendTree(line, top, 0);
                Plugin.MyLog.LogInfo(line.ToString());
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogWarning($"[GrenadeRecon] bones: {exception.Message}");
            }
        }

        private static void AppendTree(StringBuilder line, Transform node, int depth)
        {
            if (depth > 14)
                return;
            line.Append(" | ").Append(new string('.', depth)).Append(node.name);
            if (node.GetComponent<Renderer>() != null)
                line.Append(" [mesh]");
            if (LooksLikePart(node.name))
                line.Append(" [*]");
            if (!node.gameObject.activeSelf)
                line.Append(" [off]");
            for (int i = 0; i < node.childCount; i++)
                AppendTree(line, node.GetChild(i), depth + 1);
        }

        private static bool LooksLikePart(string name)
        {
            string lower = name.ToLowerInvariant();
            foreach (string word in partWords)
            {
                if (lower.Contains(word))
                    return true;
            }
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GrenadeHandsController.HighThrowOperation), nameof(GrenadeHandsController.HighThrowOperation.Start), new System.Type[0])]
        private static void PinPullStarted(GrenadeHandsController.HighThrowOperation __instance)
        {
            try
            {
                GrenadeHandsController controller = __instance?.Controller;
                if (!enabled || controller?._player == null || !controller._player.IsYourPlayer)
                    return;
                Begin(controller);
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogWarning($"[GrenadeRecon] pin pull start: {exception.Message}");
            }
        }

        private static void Begin(GrenadeHandsController controller)
        {
            sampling = controller;
            sampleStart = Time.time;
            lastLine = -1f;
            parts.Clear();
            states.Clear();
            timeline.Clear();
            grabPose = null;
            root = controller.WeaponRoot;
            TransformLinks links = controller.HandsHierarchy;
            palm = links != null ? links.GetTransform(ECharacterWeaponBones.HumanLPalm) : null;
            indexTip = DeepestNamed(palm, "digit2", "index");
            Transform top = controller.ControllerGameObject.transform;
            foreach (Transform node in top.GetComponentsInChildren<Transform>(true))
            {
                if (!LooksLikePart(node.name) || node == root)
                    continue;
                parts.Add(new Part { transform = node, path = PathFrom(top, node), rest = InRoot(node.position), wasActive = node.gameObject.activeInHierarchy });
            }
            Plugin.MyLog.LogInfo($"[GrenadeRecon] pin pull of {controller.Item.LocalizedShortName()}: {parts.Count} ring/pin-like parts, left palm {(palm != null ? palm.name : "NONE")}, index tip {(indexTip != null ? indexTip.name : "NONE")}");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void Sample(EFT.Player __instance)
        {
            if (sampling == null || !__instance.IsYourPlayer)
                return;
            try
            {
                float t = Time.time - sampleStart;
                if (__instance.HandsController != sampling || root == null || t > sampleTime)
                {
                    Finish();
                    return;
                }
                var state = sampling._firearmsAnimator.Animator.GetCurrentAnimatorStateInfo(1);
                int hash = state.fullPathHash;
                states[hash] = states.TryGetValue(hash, out Vector2 seen) ? new Vector2(seen.x, t) : new Vector2(t, t);

                bool firstMoveThisFrame = false;
                foreach (Part part in parts)
                {
                    if (part.transform == null)
                        continue;
                    bool active = part.transform.gameObject.activeInHierarchy;
                    if (active != part.wasActive)
                    {
                        part.activeChanges += $" {(active ? "on" : "off")}@{t:F2}s/{hash}:{state.normalizedTime:F2}";
                        part.wasActive = active;
                    }
                    Vector3 offset = InRoot(part.transform.position) - part.rest;
                    float travel = offset.magnitude;
                    part.maxTravel = Mathf.Max(part.maxTravel, travel);
                    if (part.movedAt < 0f && travel > 0.003f)
                    {
                        part.movedAt = t;
                        part.movedAtState = hash;
                        part.movedAtStateTime = state.normalizedTime;
                        firstMoveThisFrame = true;
                    }
                    if (part.movedAt >= 0f && part.pullAxis == Vector3.zero && travel > 0.01f)
                        part.pullAxis = offset.normalized;
                }
                // The clip's hand where the first part starts moving: the finger's in the ring there
                if (firstMoveThisFrame && grabPose == null && palm != null)
                {
                    Quaternion palmInRoot = Quaternion.Inverse(root.rotation) * palm.rotation;
                    grabPose = $"palm {Vec(InRoot(palm.position))} {Vec(palmInRoot.eulerAngles)}" +
                        (indexTip != null ? $", index tip {Vec(InRoot(indexTip.position))}" : "");
                }
                if (t - lastLine >= lineEvery)
                {
                    lastLine = t;
                    timeline.Append($" | {t:F2}s {hash}:{state.normalizedTime:F2}");
                    if (palm != null)
                        timeline.Append($" palm {Vec(InRoot(palm.position))}");
                    foreach (Part part in parts)
                    {
                        if (part.transform != null && part.maxTravel > 0.003f)
                            timeline.Append($" {part.transform.name} {Vec(InRoot(part.transform.position))}");
                    }
                }
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogWarning($"[GrenadeRecon] sample: {exception.Message}");
                sampling = null;
            }
        }

        private static void Finish()
        {
            GrenadeHandsController controller = sampling;
            sampling = null;
            if (controller == null)
                return;
            var line = new StringBuilder($"[GrenadeRecon] pin pull of {controller.Item?.LocalizedShortName()} summary: states");
            foreach (KeyValuePair<int, Vector2> state in states)
                line.Append($" {state.Key} ({state.Value.x:F2}-{state.Value.y:F2}s)");
            line.Append($"; grab pose (Weapon_root frame) {grabPose ?? "none: nothing moved"}; parts:");
            foreach (Part part in parts)
            {
                line.Append($" | {part.path}: travel {part.maxTravel * 100f:F1} cm");
                if (part.movedAt >= 0f)
                    line.Append($", moves from {part.movedAt:F2}s ({part.movedAtState}:{part.movedAtStateTime:F2}) along {Vec(part.pullAxis)}");
                if (part.activeChanges.Length > 0)
                    line.Append($", shown/hidden{part.activeChanges}");
            }
            Plugin.MyLog.LogInfo(line.ToString());
            Plugin.MyLog.LogInfo($"[GrenadeRecon] timeline{timeline}");
        }

        private static Vector3 InRoot(Vector3 world)
        {
            return root != null ? root.InverseTransformPoint(world) : world;
        }

        private static string PathFrom(Transform top, Transform node)
        {
            string path = node.name;
            for (Transform up = node.parent; up != null && up != top; up = up.parent)
                path = up.name + "/" + path;
            return path;
        }

        // The deepest descendant whose name has one of the words (the index finger's tip: EFT names fingers Digit11..Digit53)
        private static Transform DeepestNamed(Transform from, params string[] words)
        {
            if (from == null)
                return null;
            Transform best = null;
            int bestDepth = -1;
            foreach (Transform node in from.GetComponentsInChildren<Transform>(true))
            {
                string lower = node.name.ToLowerInvariant();
                bool match = false;
                foreach (string word in words)
                    match |= lower.Contains(word);
                if (!match)
                    continue;
                int depth = 0;
                for (Transform up = node; up != null && up != from; up = up.parent)
                    depth++;
                if (depth > bestDepth)
                {
                    bestDepth = depth;
                    best = node;
                }
            }
            return best;
        }

        private static string Vec(Vector3 value)
        {
            return $"({value.x:F3}, {value.y:F3}, {value.z:F3})";
        }
    }
}
