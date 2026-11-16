using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovVR.Source.Graphics
{
    // Eye diff (dev, Ctrl+F6 with the replay off): one frame of Unity's own two-eye rendering of the headset camera. The native layer records
    // every constant buffer upload and dynamic vertex write of the left pass and of the right pass, puts the left ones through the replay's
    // value patterns and lists what still differs from the right - per-eye values the replay doesn't swap yet. Shader values that scripts
    // set per eye (Material / Shader / property block / compute setters, while the headset camera renders) are listed by name next to it.
    internal partial class StereoReplay
    {
        [DllImport("SPTVRStereoReplay")] private static extern int SR_CaptureReport(byte[] text, int max);

        private const int CaptureArmEvent = 3000, CaptureLeftEvent = 3100, CaptureRightEvent = 3101, CapturePassEndEvent = 3102, CaptureSlot = 7;

        private enum CaptureStage { Idle, Armed, Recording, WaitingForReport }
        private static CaptureStage captureStage;
        private static int captureFrame, capturePasses;
        private static float captureWaitUntil;
        private static Camera captureCamera;
        private static CommandBuffer capturePassEnd;
        private static Harmony captureHarmony;
        private static int captureEye = -1;          // 0 left, 1 right: the pass the headset camera is rendering

        private struct SetValue { public int eye; public string target; public string property; public string value; }
        private static readonly List<SetValue> setValues = new List<SetValue>();
        private static readonly Dictionary<int, string> propertyNames = new Dictionary<int, string>();

        private void UpdateEyeDiff()
        {
            if (Input.GetKeyDown(KeyCode.F6) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                BeginEyeDiff();
            if (captureStage == CaptureStage.Recording && Time.frameCount > captureFrame + 1)
                EndEyeDiffRecording();
            if (captureStage == CaptureStage.WaitingForReport)
            {
                byte[] text = new byte[256 * 1024];
                int length = SR_CaptureReport(text, text.Length);
                if (length > 0)
                {
                    captureStage = CaptureStage.Idle;
                    Plugin.MyLog.LogInfo("[StereoReplay] eye diff - what differs between the eyes and the replay doesn't swap yet:\n"
                        + Encoding.ASCII.GetString(text, 0, length) + NamedDiff());
                }
                else if (Time.unscaledTime > captureWaitUntil)
                {
                    captureStage = CaptureStage.Idle;
                    Plugin.MyLog.LogWarning($"[StereoReplay] eye diff: no report from the native layer (passes seen {capturePasses}) - is the headset camera drawing both eyes?");
                }
            }
        }

        public static void BeginEyeDiff()
        {
            if (Active)
            {
                Plugin.MyLog.LogWarning("[StereoReplay] eye diff: switch the replay off first (Ctrl+F9) - it compares Unity's own two eye passes");
                return;
            }
            if (captureStage != CaptureStage.Idle) return;
            Camera headset = HeadsetCamera();
            if (headset == null)
            {
                Plugin.MyLog.LogWarning("[StereoReplay] eye diff: no headset camera rendering both eyes");
                return;
            }
            if (!TryNative()) return;
            captureCamera = headset;
            // the eyes' offset and the right projection the patterns use, from Unity's two eyes now
            rightEyeFromLeftEye = headset.GetStereoViewMatrix(Camera.StereoscopicEye.Right) * headset.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse;
            projectionRightAtStart = headset.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
            havePrevious = false;
            capturePassEnd = new CommandBuffer { name = "SPT-VR eye diff pass end" };
            capturePassEnd.IssuePluginEvent(eventFunction, CapturePassEndEvent);
            headset.AddCommandBuffer(CameraEvent.AfterEverything, capturePassEnd);
            setValues.Clear();
            PatchSetters();
            capturePasses = 0;
            captureFrame = -1;
            captureEye = -1;
            Camera.onPreCull += CaptureOnPreCull;
            captureStage = CaptureStage.Armed;
            Plugin.MyLog.LogInfo("[StereoReplay] eye diff: recording the next frame's two eye passes");
        }

        // The headset camera's passes of one frame: the first is the left eye (multipass draws left, then right)
        private static void CaptureOnPreCull(Camera culling)
        {
            if (culling != captureCamera) return;
            if (captureStage == CaptureStage.Armed)
            {
                Matrix4x4 viewLeft = culling.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
                Matrix4x4 projectionLeft = culling.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                FillPatterns(viewLeft, rightEyeFromLeftEye * viewLeft, projectionLeft, projectionRightAtStart, false);
                SR_SetFrame(CaptureSlot, patternCount, EndEvent, lengths, tolerances, leftValues, rightValues);
                GL.IssuePluginEvent(eventFunction, CaptureArmEvent + CaptureSlot);
                GL.IssuePluginEvent(eventFunction, CaptureLeftEvent);
                captureStage = CaptureStage.Recording;
                captureFrame = Time.frameCount;
                captureEye = 0;
                capturePasses = 1;
            }
            else if (captureStage == CaptureStage.Recording && Time.frameCount == captureFrame && captureEye == 0)
            {
                GL.IssuePluginEvent(eventFunction, CaptureRightEvent);
                captureEye = 1;
                capturePasses = 2;
            }
        }

        private static void EndEyeDiffRecording()
        {
            Camera.onPreCull -= CaptureOnPreCull;
            captureEye = -1;
            captureHarmony?.UnpatchSelf();
            captureHarmony = null;
            if (captureCamera != null && capturePassEnd != null)
                captureCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, capturePassEnd);
            capturePassEnd?.Release();
            capturePassEnd = null;
            captureStage = CaptureStage.WaitingForReport;
            captureWaitUntil = Time.unscaledTime + 3f;
        }

        //------------------------------------------------------------------------------------------------------------------------------
        // Shader values set from scripts while the headset camera renders, by eye

        private static void PatchSetters()
        {
            captureHarmony = new Harmony("com.matsix.sptvr.eyediff");
            HarmonyMethod prefix = new HarmonyMethod(typeof(StereoReplay).GetMethod(nameof(RecordSet), BindingFlags.NonPublic | BindingFlags.Static));
            var targets = new List<MethodBase>();
            void Add(Type type, string name, Type key, Type value)
            {
                MethodInfo method = AccessTools.Method(type, name, new[] { key, value });
                if (method != null) targets.Add(method);
            }
            foreach (Type key in new[] { typeof(string), typeof(int) })
            {
                Add(typeof(Material), "SetMatrix", key, typeof(Matrix4x4));
                Add(typeof(Material), "SetVector", key, typeof(Vector4));
                Add(typeof(Material), "SetFloat", key, typeof(float));
                Add(typeof(Material), "SetMatrixArray", key, typeof(Matrix4x4[]));
                Add(typeof(Material), "SetVectorArray", key, typeof(Vector4[]));
                Add(typeof(Shader), "SetGlobalMatrix", key, typeof(Matrix4x4));
                Add(typeof(Shader), "SetGlobalVector", key, typeof(Vector4));
                Add(typeof(Shader), "SetGlobalFloat", key, typeof(float));
                Add(typeof(Shader), "SetGlobalMatrixArray", key, typeof(Matrix4x4[]));
                Add(typeof(Shader), "SetGlobalVectorArray", key, typeof(Vector4[]));
                Add(typeof(MaterialPropertyBlock), "SetMatrix", key, typeof(Matrix4x4));
                Add(typeof(MaterialPropertyBlock), "SetVector", key, typeof(Vector4));
                Add(typeof(MaterialPropertyBlock), "SetFloat", key, typeof(float));
                Add(typeof(ComputeShader), "SetMatrix", key, typeof(Matrix4x4));
                Add(typeof(ComputeShader), "SetVector", key, typeof(Vector4));
                Add(typeof(ComputeShader), "SetFloat", key, typeof(float));
                Add(typeof(CommandBuffer), "SetGlobalMatrix", key, typeof(Matrix4x4));
                Add(typeof(CommandBuffer), "SetGlobalVector", key, typeof(Vector4));
                Add(typeof(CommandBuffer), "SetGlobalFloat", key, typeof(float));
            }
            int patched = 0;
            foreach (MethodBase target in targets)
            {
                try { captureHarmony.Patch(target, prefix: prefix); patched++; }
                catch (Exception exception) { Plugin.MyLog.LogWarning($"[StereoReplay] eye diff: {target.DeclaringType?.Name}.{target.Name} not watched: {exception.Message}"); }
            }
        }

        private static void RecordSet(object __instance, object[] __args)
        {
            if (captureEye < 0 || __args == null || __args.Length < 2 || Camera.current != captureCamera)
                return;
            try
            {
                setValues.Add(new SetValue { eye = captureEye, target = TargetName(__instance), property = PropertyName(__instance, __args[0]), value = ValueText(__args[1]) });
            }
            catch
            {
            }
        }

        private static string TargetName(object target)
        {
            switch (target)
            {
                case null: return "global";
                case Material material: return $"material '{(material.shader != null ? material.shader.name : material.name)}'";
                case MaterialPropertyBlock _: return "property block";
                case ComputeShader compute: return $"compute '{compute.name}'";
                case CommandBuffer buffer: return $"command buffer '{buffer.name}'";
                default: return target.GetType().Name;
            }
        }

        private static string PropertyName(object target, object key)
        {
            if (key is string name) return name;
            if (!(key is int id)) return key?.ToString() ?? "?";
            if (propertyNames.TryGetValue(id, out string known)) return known;
            if (target is Material material && material.shader != null)
            {
                Shader shader = material.shader;
                for (int index = 0; index < shader.GetPropertyCount(); index++)
                {
                    if (shader.GetPropertyNameId(index) == id)
                    {
                        propertyNames[id] = shader.GetPropertyName(index);
                        return propertyNames[id];
                    }
                }
            }
            return "#" + id;
        }

        private static string ValueText(object value)
        {
            CultureInfo invariant = CultureInfo.InvariantCulture;
            switch (value)
            {
                case Matrix4x4 matrix:
                {
                    var text = new StringBuilder();
                    for (int index = 0; index < 16; index++) text.Append(index > 0 ? " " : "").Append(matrix[index].ToString("0.#####", invariant));
                    return text.ToString();
                }
                case Vector4 vector: return $"{vector.x.ToString("0.#####", invariant)} {vector.y.ToString("0.#####", invariant)} {vector.z.ToString("0.#####", invariant)} {vector.w.ToString("0.#####", invariant)}";
                case float single: return single.ToString("0.#####", invariant);
                case Matrix4x4[] matrices: return $"{matrices.Length} matrices, first {(matrices.Length > 0 ? ValueText(matrices[0]) : "")}";
                case Vector4[] vectors: return $"{vectors.Length} vectors, first {(vectors.Length > 0 ? ValueText(vectors[0]) : "")}";
                default: return value?.ToString() ?? "null";
            }
        }

        // Per (target, property): the values set in the left pass against the right pass's, where they differ
        private static string NamedDiff()
        {
            var byKey = new Dictionary<string, List<string>[]>();
            foreach (SetValue set in setValues)
            {
                string key = set.target + " " + set.property;
                if (!byKey.TryGetValue(key, out List<string>[] sequences))
                {
                    sequences = new[] { new List<string>(), new List<string>() };
                    byKey[key] = sequences;
                }
                sequences[set.eye].Add(set.value);
            }
            var lines = new StringBuilder();
            int differing = 0, oneEye = 0;
            foreach (var entry in byKey)
            {
                List<string> left = entry.Value[0], right = entry.Value[1];
                if (left.Count == 0 || right.Count == 0) { oneEye++; continue; }
                int pairs = Math.Min(left.Count, right.Count);
                for (int index = 0; index < pairs; index++)
                {
                    if (left[index] == right[index]) continue;
                    differing++;
                    if (differing <= 80)
                        lines.Append($"  {entry.Key} (set {left.Count}x / {right.Count}x): left [{left[index]}] right [{right[index]}]\n");
                    break;
                }
            }
            return $"shader values set from scripts while the headset camera renders: {setValues.Count} sets, {byKey.Count} names, {differing} differ between the eyes"
                + $" ({oneEye} set in one eye only):\n" + lines;
        }
    }
}
