using System;
using System.Collections;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovVR.Source.Misc
{
    // Measurement only, for drawing the sun's shadow map once for both eyes: are the two eyes' maps the same? Ctrl+F8 (raid, sun up)
    // copies both eyes' sun shadow maps of one frame (raw depth, at LightEvent.AfterShadowMap) and compares them, twice:
    //   as drawn  : each eye culled from its own view (the game as it is)
    //   one view  : both eyes culled through the LEFT eye's culling matrix (Camera.cullingMatrix, set in each eye's pre-cull)
    // A match in the second means Unity lays the cascades out from the culling matrix: the first eye's map and matrices are right for
    // the second. Otherwise it reports each cascade's shift in texels, or that the content differs. Logs the shadow settings too.
    internal class ShadowShareProbe : MonoBehaviour
    {
        public static bool hotkeyEnabled = true;
        public static int captureSize = 2048;          // the map is copied into this (bilinear: same maps stay the same)
        public static float sameDepth = 1e-6f;         // raw depths closer than this count as the same
        public static int shiftSearch = 6;             // texels searched each way for a shifted cascade
        public static int blockSize = 128;             // texels compared per cascade for the shift search

        private static bool running;

        // the frame being captured
        private static int captureFrame = -1;
        private static bool oneView;
        private static Camera headset;
        private static Light sun;
        private static readonly RenderTexture[] eyeMaps = new RenderTexture[2];
        private static RenderTexture latestMap;        // the sun's map, copied by the light's buffer each time it's drawn
        private static CommandBuffer copyBuffer;       // on the sun for the whole capture frame
        private static CommandBuffer keepBuffer;       // after each eye: latest -> that eye's map, latest cleared
        private static int eyePass;                    // eye passes seen in the capture frame (pre-render)
        private static int eyesDone;                   // eye passes finished (post-render)
        private static readonly StringBuilder sequence = new StringBuilder();
        private static Matrix4x4 leftCulling;


        private void Update()
        {
            if (!hotkeyEnabled || running || !Input.GetKeyDown(KeyCode.F8) || !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                return;
            StartCoroutine(Run());
        }

        private static Camera HeadsetCamera()
        {
            Camera camera = VRGlobals.VRCam;
            if (camera != null && camera.isActiveAndEnabled && camera.stereoEnabled) return camera;
            camera = Camera.main;
            return camera != null && camera.isActiveAndEnabled && camera.stereoEnabled ? camera : null;
        }

        private static Light Sun()
        {
            Light best = RenderSettings.sun;
            if (best != null && best.isActiveAndEnabled && best.type == LightType.Directional && best.shadows != LightShadows.None)
                return best;
            foreach (Light light in FindObjectsOfType<Light>())
                if (light != null && light.isActiveAndEnabled && light.type == LightType.Directional && light.shadows != LightShadows.None && light.intensity > 0f)
                    return light;
            return null;
        }

        private IEnumerator Run()
        {
            running = true;
            headset = HeadsetCamera();
            sun = Sun();
            if (headset == null || sun == null)
            {
                Plugin.MyLog.LogWarning($"[ShadowShare] needs the headset camera and a sun casting shadows (camera {(headset != null ? "ok" : "none")}, sun {(sun != null ? "ok" : "none")})");
                running = false;
                yield break;
            }
            var report = new StringBuilder();
            report.AppendLine("[ShadowShare] ===== do the two eyes draw the same sun shadow map? =====");
            report.AppendLine($"[ShadowShare]   settings: shadows {QualitySettings.shadows}, projection {QualitySettings.shadowProjection}, cascades {QualitySettings.shadowCascades}, "
                + $"distance {QualitySettings.shadowDistance:0.#} m, resolution {QualitySettings.shadowResolution}, light '{sun.name}' resolution {sun.shadowResolution} "
                + $"custom {sun.shadowCustomResolution}, screen-space shadows shader {ScreenSpaceShadowShader()}, eye separation {headset.stereoSeparation * 100f:0.0} cm");
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
            try
            {
                foreach (bool variant in new[] { false, true })
                {
                    oneView = variant;
                    Prepare();
                    yield return null;                         // the next frame is the capture (its Update: before any camera)
                    captureFrame = Time.frameCount;
                    eyePass = 0;
                    eyesDone = 0;
                    sequence.Length = 0;
                    sun.AddCommandBuffer(LightEvent.AfterShadowMap, copyBuffer);
                    yield return new WaitForEndOfFrame();
                    int eyes = eyesDone;
                    Finish();
                    yield return null;
                    var left = AsyncGPUReadback.Request(eyeMaps[0]);
                    var right = AsyncGPUReadback.Request(eyeMaps[1]);
                    while (!left.done || !right.done) yield return null;
                    string label = variant ? "one view (both eyes culled through the left eye's matrix)" : "as drawn (each eye culled from its own view)";
                    report.AppendLine($"[ShadowShare]   {label}: headset camera events {sequence}");
                    if (left.hasError || right.hasError || eyes < 2)
                        report.AppendLine($"[ShadowShare]   {label}: no capture (eye passes finished {eyes}, readback errors {left.hasError}/{right.hasError})");
                    else
                        report.Append(Compare(label, left.GetData<float>(), right.GetData<float>()));
                }
            }
            finally
            {
                Camera.onPreCull -= OnPreCull;
                Camera.onPreRender -= OnPreRender;
                Camera.onPostRender -= OnPostRender;
                Finish();
                if (headset != null) headset.ResetCullingMatrix();
                running = false;
            }
            report.Append("[ShadowShare] ===== end =====");
            Plugin.MyLog.LogWarning(report.ToString());
        }

        private static string ScreenSpaceShadowShader()
        {
            BuiltinShaderMode shaderMode = GraphicsSettings.GetShaderMode(BuiltinShaderType.ScreenSpaceShadows);
            Shader custom = GraphicsSettings.GetCustomShader(BuiltinShaderType.ScreenSpaceShadows);
            return shaderMode == BuiltinShaderMode.UseCustom ? $"custom '{(custom != null ? custom.name : "?")}'" : shaderMode.ToString();
        }

        //------------------------------------------------------------------------------------------------------------
        private static RenderTexture MakeMap(string name)
        {
            var map = new RenderTexture(captureSize, captureSize, 0, RenderTextureFormat.RFloat) { name = "SPT-VR ShadowShare " + name };
            map.Create();
            return map;
        }

        private static void Clear(RenderTexture map)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = map;
            GL.Clear(false, true, new Color(-1f, 0f, 0f, 0f));   // -1 = not written
            RenderTexture.active = previous;
        }

        // One copy buffer on the sun for the whole frame: Unity takes a light's buffers before the eyes' pre-render, so swapping one
        // per eye there writes neither map. It copies into latestMap each time the map is drawn; after each eye (post-render, in
        // order on the GPU) latestMap goes into that eye's map and is cleared, so an eye that drew no shadow map of its own shows
        // as not written instead of as a copy of the other's.
        private static void Prepare()
        {
            for (int eye = 0; eye < 2; eye++)
            {
                if (eyeMaps[eye] == null) eyeMaps[eye] = MakeMap("eye " + eye);
                Clear(eyeMaps[eye]);
            }
            if (latestMap == null) latestMap = MakeMap("latest");
            Clear(latestMap);
            if (copyBuffer == null)
            {
                copyBuffer = new CommandBuffer { name = "SPT-VR ShadowShare copy" };
                copyBuffer.SetShadowSamplingMode(BuiltinRenderTextureType.CurrentActive, ShadowSamplingMode.RawDepth);
                copyBuffer.Blit(BuiltinRenderTextureType.CurrentActive, latestMap);
            }
            if (keepBuffer == null) keepBuffer = new CommandBuffer { name = "SPT-VR ShadowShare keep" };
        }

        private static void Finish()
        {
            captureFrame = -1;
            if (sun != null && copyBuffer != null) sun.RemoveCommandBuffer(LightEvent.AfterShadowMap, copyBuffer);
            if (headset != null) headset.ResetCullingMatrix();
        }

        private static string EyeName(Camera camera) => camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Left ? "L"
            : camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? "R" : "mono";

        // Before each eye's cull: the left eye's culling matrix for both (one-view variant)
        private static void OnPreCull(Camera camera)
        {
            if (camera != headset || Time.frameCount != captureFrame)
                return;
            sequence.Append("cull ").Append(EyeName(camera)).Append(", ");
            if (!oneView)
                return;
            if (eyePass == 0)
                leftCulling = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
            camera.cullingMatrix = leftCulling;
        }

        private static void OnPreRender(Camera camera)
        {
            if (camera != headset || Time.frameCount != captureFrame)
                return;
            eyePass++;
            sequence.Append("render ").Append(EyeName(camera)).Append(", ");
        }

        // After each eye: its shadow map (the latest copy) kept
        private static void OnPostRender(Camera camera)
        {
            if (camera != headset || Time.frameCount != captureFrame)
                return;
            int eye = eyesDone++;
            sequence.Append("done ").Append(EyeName(camera)).Append(eye < 2 ? "" : " (ignored)").Append(", ");
            if (eye >= 2 || keepBuffer == null)
                return;
            RenderTexture previous = RenderTexture.active;
            keepBuffer.Clear();
            keepBuffer.CopyTexture(latestMap, eyeMaps[eye]);
            keepBuffer.SetRenderTarget(latestMap);
            keepBuffer.ClearRenderTarget(false, true, new Color(-1f, 0f, 0f, 0f));
            UnityEngine.Graphics.ExecuteCommandBuffer(keepBuffer);
            RenderTexture.active = previous;
        }

        //------------------------------------------------------------------------------------------------------------
        private static string Compare(string label, NativeArray<float> left, NativeArray<float> right)
        {
            int size = captureSize, half = size / 2;
            var builder = new StringBuilder();
            long written = 0, notWritten = 0, different = 0, leftWritten = 0, rightWritten = 0;
            double maxDiff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                float a = left[i], b = right[i];
                if (a >= 0f) leftWritten++;
                if (b >= 0f) rightWritten++;
                if (a < 0f || b < 0f) { notWritten++; continue; }
                written++;
                double d = Math.Abs(a - b);
                if (d > sameDepth) different++;
                if (d > maxDiff) maxDiff = d;
            }
            builder.AppendLine($"[ShadowShare]   {label}: maps written: left {leftWritten * 100.0 / left.Length:0.#}%, right {rightWritten * 100.0 / left.Length:0.#}%");
            if (leftWritten == 0 || rightWritten == 0)
            {
                builder.AppendLine($"[ShadowShare]   {label}: {(leftWritten == 0 && rightWritten == 0 ? "neither eye's map was copied" : "only one eye's map was copied")} - nothing to compare");
                return builder.ToString();
            }
            builder.AppendLine($"[ShadowShare]   {label}: {(different == 0 ? "IDENTICAL" : "different")} - "
                + $"{different * 100.0 / Math.Max(1, written):0.000}% of {written} texels differ, largest raw-depth difference {maxDiff:0.000000}"
                + (notWritten > 0 ? $", {notWritten} texels not written" : ""));
            // Each quarter of the map (the cascades sit 2 x 2): how far the right eye's is shifted against the left's
            string[] names = { "bottom-left", "bottom-right", "top-left", "top-right" };
            for (int quarter = 0; quarter < 4; quarter++)
            {
                int ox = (quarter % 2) * half, oy = (quarter / 2) * half;
                int bx = ox + half / 2 - blockSize / 2, by = oy + half / 2 - blockSize / 2;
                double best = double.MaxValue, atZero = 0;
                int bestX = 0, bestY = 0;
                for (int dy = -shiftSearch; dy <= shiftSearch; dy++)
                    for (int dx = -shiftSearch; dx <= shiftSearch; dx++)
                    {
                        double sum = 0; int n = 0;
                        for (int y = 0; y < blockSize; y += 2)
                            for (int x = 0; x < blockSize; x += 2)
                            {
                                float a = left[(by + y) * size + bx + x];
                                float b = right[(by + y + dy) * size + bx + x + dx];
                                if (a < 0f || b < 0f) continue;
                                sum += Math.Abs(a - b); n++;
                            }
                        double mean = n > 0 ? sum / n : double.MaxValue;
                        if (dx == 0 && dy == 0) atZero = mean;
                        if (mean < best) { best = mean; bestX = dx; bestY = dy; }
                    }
                if (best == double.MaxValue)
                    builder.AppendLine($"[ShadowShare]     {names[quarter],-12}: not written in both");
                else
                    builder.AppendLine($"[ShadowShare]     {names[quarter],-12}: mean difference unshifted {atZero:0.0000000}, best match shifted ({bestX},{bestY}) texels of {size} at {best:0.0000000}");
            }
            return builder.ToString();
        }
    }
}
