using System;
using BepInEx.Configuration;
using FuckThisCulling;
using FuckThisCulling.Engine;
using HarmonyLib;
using UnityEngine;

namespace TarkovVR.ModSupport.FuckThisCullingSupport
{
    // FuckThisCulling (rootdarkarchon) in VR, written against its 0.4.1 (Plugin applies this to that version only).
    // The mod tests what is hidden once a frame, from the camera's depth in the pass it records the test in. In MultiPass that was
    // the left eye's pass only, so anything only the right eye could see was hidden. Here the eyes take turns (left on even frames,
    // right on odd), each tested with its own view and projection. The mod hides only after several occluded results in a row
    // (OccludedConfirmationQueries; at least minimumOccludedChecks here), so an object one eye sees - "visible" every second test -
    // stays drawn. Hides and restores happen only before the first eye, so both eyes draw the same objects.
    [HarmonyPatch]
    internal static class StereoCullingPatches
    {
        public static bool enabled = true;               // false = the mod as it is (left eye only)
        public static int minimumOccludedChecks = 2;     // with the eyes taking turns, 1 would hide what only the other eye sees
        public static float behindConeMargin = 1.05f;    // "hide objects behind you": kept off both eyes' views, this much wider

        private static Camera testingCamera;             // the gameplay camera whose eye is recording the test
        private static readonly int[] testsByEye = new int[2];
        private static bool loggedFirstTest;
        private static bool loggedSummary;
        private static float summaryAt = -1f;

        private static bool Stereo(Camera camera) => enabled && camera != null && camera.stereoEnabled;
        private static int TurnEye => Time.frameCount & 1;
        private static int EyeIndex(Camera camera) => camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;

        // Hides / restores run in the gameplay camera's pre-cull, which comes once an eye: only before the left eye
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VisibilityEngine), "PreCull")]
        private static bool ConsumeBeforeFirstEyeOnly(VisibilityEngine __instance, Camera c)
        {
            return !(c != null && c == __instance._camera && Stereo(c) && c.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right);
        }

        // The test is recorded in an eye's pre-render and runs in that eye's pass: only in the eye whose turn it is. (Post-render
        // clears the recorded test after each eye; after the other eye there is nothing to clear.)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VisibilityEngine), "PreRender")]
        private static bool TestInTurnEyeOnly(VisibilityEngine __instance, Camera c)
        {
            if (c == null || c != __instance._camera || !Stereo(c))
                return true;
            if (EyeIndex(c) != TurnEye)
                return false;
            testingCamera = c;
            return true;
        }

        // The test's matrices from the eye being drawn (the mod reads the camera's plain ones). slot.projection is only the stale
        // check's reference (StaleStereo): one fixed eye's, so a test from either eye compares like with like.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(QueryPipeline), "CaptureSource")]
        private static void EyeMatrices(QueryPipeline __instance, QuerySlot slot)
        {
            Camera camera = __instance._seams.GameplayCamera();
            if (!Stereo(camera) || camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Mono)
                return;
            Camera.StereoscopicEye eye = camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            Matrix4x4 eyeProjection = camera.GetStereoProjectionMatrix(eye);
            Matrix4x4 eyeView = camera.GetStereoViewMatrix(eye);
            if (!loggedFirstTest)
            {
                loggedFirstTest = true;
                Matrix4x4 plain = camera.projectionMatrix;
                Plugin.MyLog.LogInfo($"[FuckThisCulling VR] eyes take turns testing; first test from the {eye} eye: camera's plain projection "
                    + $"m00 {plain.m00:0.0000} m02 {plain.m02:0.0000}, the eye's m00 {eyeProjection.m00:0.0000} m02 {eyeProjection.m02:0.0000}, "
                    + $"plain view = eye view: {camera.worldToCameraMatrix == eyeView}");
                summaryAt = Time.unscaledTime + 20f;
            }
            slot.projection = ReferenceProjection(camera);
            slot.queryProjection = StableProjection.StableQueryProjection(eyeProjection, camera.GetStereoNonJitteredProjectionMatrix(eye), __instance._width, __instance._height);
            slot.sourceView = eyeView;
            slot.sourceQueryVP = __instance.CensusQueryMatrix(slot, eyeView);
            slot.sourceRasterVP = GL.GetGPUProjectionMatrix(eyeProjection, false) * eyeView;
            testsByEye[(int)eye]++;
            if (!loggedSummary && summaryAt > 0f && Time.unscaledTime > summaryAt)
            {
                loggedSummary = true;
                Plugin.MyLog.LogInfo($"[FuckThisCulling VR] tests in the first 20 s: left eye {testsByEye[0]}, right eye {testsByEye[1]}");
            }
        }

        private static Matrix4x4 ReferenceProjection(Camera camera) => camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);

        // PluginSettings' entries are internal properties, which the publicized DLL leaves internal (its fields are opened)
        private static readonly Func<PluginSettings, ConfigEntry<int>> MaxAge = Getter<ConfigEntry<int>>("MaxAge");
        private static readonly Func<PluginSettings, ConfigEntry<float>> MotionDistance = Getter<ConfigEntry<float>>("MotionDistance");
        private static readonly Func<PluginSettings, ConfigEntry<float>> MotionAngle = Getter<ConfigEntry<float>>("MotionAngle");

        private static Func<PluginSettings, T> Getter<T>(string property) =>
            AccessTools.MethodDelegate<Func<PluginSettings, T>>(AccessTools.PropertyGetter(typeof(PluginSettings), property));

        // RestoreTriggers.Stale (0.4.1) with the current projection read the same way the slots now store it. The original reads the
        // camera's plain projection, which would not match a slot from the other eye.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(RestoreTriggers), "Stale")]
        private static bool StaleStereo(RestoreTriggers __instance, in RetainedSample sample, Camera current, int generation, int width, int height, int mask, ref string __result)
        {
            if (!Stereo(current))
                return true;
            Transform cameraTransform = current.transform;
            float moved = Vector3.Distance(sample.Position, cameraTransform.position);
            float turned = Quaternion.Angle(sample.Rotation, cameraTransform.rotation);
            Matrix4x4 now = ReferenceProjection(current);
            Matrix4x4 then = sample.Projection;
            float largestChange = 0f;
            for (int index = 0; index < 16; index++)
            {
                if (index != 8 && index != 9)
                    largestChange = Math.Max(largestChange, Math.Abs(now[index] - then[index]));
            }
            __instance._stats.RecordSourceDelta(moved, turned, largestChange);
            __result = sample.Generation != generation ? "generation"
                : current.pixelWidth != width || current.pixelHeight != height || current.cullingMask != mask || current.orthographic ? "camera layout"
                : Time.frameCount - sample.Frame > MaxAge(__instance._settings).Value ? "age"
                : moved > MotionDistance(__instance._settings).Value ? "translation/cut"
                : turned > MotionAngle(__instance._settings).Value ? "rotation/cut"
                : largestChange > 0.0001f ? "projection"
                : Math.Abs(now.m02 - then.m02) > 8f / width || Math.Abs(now.m12 - then.m12) > 8f / height ? "projection jitter"
                : "";
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(JitterConfirmation), "RequiredFor")]
        private static void AtLeastTwoOccludedChecks(ref int __result)
        {
            if (enabled)
                __result = Math.Max(__result, minimumOccludedChecks);
        }

        // "Slowly hide objects behind you" keeps its cone off the view it is given: give it both eyes' views (from the camera, whose
        // rotation it is passed with)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BehindHides), "Bind")]
        private static void ConeOffBothEyes(ref Matrix4x4 projection)
        {
            Camera camera = testingCamera;
            if (Stereo(camera))
                projection = BothEyesProjection(camera, projection);
        }

        private static Matrix4x4 BothEyesProjection(Camera camera, Matrix4x4 fallback)
        {
            float tanAcross = 0f;
            float tanUp = 0f;
            Transform cameraTransform = camera.transform;
            for (int eyeIndex = 0; eyeIndex < 2; eyeIndex++)
            {
                Camera.StereoscopicEye eye = (Camera.StereoscopicEye)eyeIndex;
                Matrix4x4 clipToView = camera.GetStereoProjectionMatrix(eye).inverse;
                Matrix4x4 viewToWorld = camera.GetStereoViewMatrix(eye).inverse;
                for (int corner = 0; corner < 4; corner++)
                {
                    Vector4 viewPoint = clipToView * new Vector4((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, 0f, 1f);
                    if (Mathf.Abs(viewPoint.w) < 1e-8f)
                        return fallback;
                    Vector3 local = cameraTransform.InverseTransformDirection(viewToWorld.MultiplyVector((Vector3)viewPoint / viewPoint.w));
                    if (local.z <= 1e-4f)
                        return fallback;
                    tanAcross = Mathf.Max(tanAcross, Mathf.Abs(local.x / local.z));
                    tanUp = Mathf.Max(tanUp, Mathf.Abs(local.y / local.z));
                }
            }
            if (tanAcross <= 0f || tanUp <= 0f)
                return fallback;
            Matrix4x4 bothEyes = fallback;
            bothEyes.m00 = 1f / (tanAcross * behindConeMargin);
            bothEyes.m11 = 1f / (tanUp * behindConeMargin);
            return bothEyes;
        }
    }
}
