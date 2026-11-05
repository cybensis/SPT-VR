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
    // If the maps match in the second, Unity lays the cascades out from the culling matrix: with one culling matrix for both eyes, the
    // first eye's map and matrices are exactly right for the second (nothing to change in any shader). If they differ, it reports by how
    // many texels each cascade is shifted (the cascades follow the eye itself) or that the content differs. Logs the shadow settings too.
    internal class ShadowShareProbe : MonoBehaviour
    {
        public static bool hotkeyEnabled = true;
        public static int captureSize = 2048;          // the map is copied into this (bilinear: same maps stay the same)
        public static float sameDepth = 1e-6f;         // raw depths closer than this count as the same
        public static int shiftSearch = 6;             // texels searched each way for a shifted cascade
        public static int blockSize = 128;             // texels compared per cascade for the shift search

        private static ShadowShareProbe instance;
        private static bool running;

        // the frame being captured
        private static int captureFrame = -1;
        private static bool oneView;
        private static Camera headset;
        private static Light sun;
        private static readonly RenderTexture[] eyeMaps = new RenderTexture[2];
        private static readonly CommandBuffer[] copyBuffers = new CommandBuffer[2];
        private static int eyePass;                    // eye passes seen in the capture frame
        private static Matrix4x4 leftCulling;

        private void Awake() { instance = this; }

        private void Update()
        {
            if (!hotkeyEnabled || running || !Input.GetKeyDown(KeyCode.F8) || !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                return;
            StartCoroutine(Run());
        }

        private static Camera HeadsetCamera()
        {
            Camera cam = VRGlobals.VRCam;
            if (cam != null && cam.isActiveAndEnabled && cam.stereoEnabled) return cam;
            cam = Camera.main;
            return cam != null && cam.isActiveAndEnabled && cam.stereoEnabled ? cam : null;
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
            try
            {
                foreach (bool variant in new[] { false, true })
                {
                    oneView = variant;
                    Prepare();
                    yield return null;                         // the next frame is the capture
                    captureFrame = Time.frameCount;
                    eyePass = 0;
                    yield return new WaitForEndOfFrame();
                    int eyes = eyePass;
                    Finish();
                    yield return null;
                    var left = AsyncGPUReadback.Request(eyeMaps[0]);
                    var right = AsyncGPUReadback.Request(eyeMaps[1]);
                    while (!left.done || !right.done) yield return null;
                    string label = variant ? "one view (both eyes culled through the left eye's matrix)" : "as drawn (each eye culled from its own view)";
                    if (left.hasError || right.hasError || eyes < 2)
                        report.AppendLine($"[ShadowShare]   {label}: no capture (eye passes {eyes}, readback errors {left.hasError}/{right.hasError})");
                    else
                        report.Append(Compare(label, left.GetData<float>(), right.GetData<float>()));
                }
            }
            finally
            {
                Camera.onPreCull -= OnPreCull;
                Camera.onPreRender -= OnPreRender;
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
        private static void Prepare()
        {
            for (int eye = 0; eye < 2; eye++)
            {
                if (eyeMaps[eye] == null)
                {
                    eyeMaps[eye] = new RenderTexture(captureSize, captureSize, 0, RenderTextureFormat.RFloat) { name = "SPT-VR ShadowShare eye " + eye };
                    eyeMaps[eye].Create();
                }
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = eyeMaps[eye];
                GL.Clear(false, true, new Color(-1f, 0f, 0f, 0f));   // -1 = not written
                RenderTexture.active = previous;
                if (copyBuffers[eye] == null)
                {
                    copyBuffers[eye] = new CommandBuffer { name = "SPT-VR ShadowShare copy eye " + eye };
                    copyBuffers[eye].SetShadowSamplingMode(BuiltinRenderTextureType.CurrentActive, ShadowSamplingMode.RawDepth);
                    copyBuffers[eye].Blit(BuiltinRenderTextureType.CurrentActive, eyeMaps[eye]);
                }
            }
        }

        private static void Finish()
        {
            captureFrame = -1;
            if (sun != null)
                for (int eye = 0; eye < 2; eye++)
                    if (copyBuffers[eye] != null) sun.RemoveCommandBuffer(LightEvent.AfterShadowMap, copyBuffers[eye]);
            if (headset != null) headset.ResetCullingMatrix();
        }

        // Before each eye's cull: the left eye's culling matrix for both (one-view variant)
        private static void OnPreCull(Camera camera)
        {
            if (camera != headset || Time.frameCount != captureFrame || !oneView)
                return;
            if (eyePass == 0)
                leftCulling = camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
            camera.cullingMatrix = leftCulling;
        }

        // Before each eye's rendering (its shadow map comes after): that eye's copy goes on the sun
        private static void OnPreRender(Camera camera)
        {
            if (camera != headset || Time.frameCount != captureFrame || sun == null)
                return;
            int eye = eyePass++;
            if (eye > 0 && copyBuffers[eye - 1] != null) sun.RemoveCommandBuffer(LightEvent.AfterShadowMap, copyBuffers[eye - 1]);
            if (eye < 2) sun.AddCommandBuffer(LightEvent.AfterShadowMap, copyBuffers[eye]);
        }

        //------------------------------------------------------------------------------------------------------------
        private static string Compare(string label, NativeArray<float> left, NativeArray<float> right)
        {
            int size = captureSize, half = size / 2;
            var sb = new StringBuilder();
            long written = 0, notWritten = 0, different = 0;
            double maxDiff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                float a = left[i], b = right[i];
                if (a < 0f || b < 0f) { notWritten++; continue; }
                written++;
                double d = Math.Abs(a - b);
                if (d > sameDepth) different++;
                if (d > maxDiff) maxDiff = d;
            }
            sb.AppendLine($"[ShadowShare]   {label}: {(notWritten == left.Length ? "NOT WRITTEN (copy didn't run)" : different == 0 ? "IDENTICAL" : "different")} - "
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
                sb.AppendLine($"[ShadowShare]     {names[quarter],-12}: mean difference unshifted {atZero:0.0000000}, best match shifted ({bestX},{bestY}) texels of {size} at {best:0.0000000}");
            }
            return sb.ToString();
        }
    }
}
