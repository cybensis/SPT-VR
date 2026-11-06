using System;
using FuckThisCulling.Engine;
using HarmonyLib;
using UnityEngine;

namespace TarkovVR.ModSupport.FuckThisCullingSupport
{
    // FuckThisCulling (rootdarkarchon) in VR, written against its 0.4.1 (Plugin applies this to that version only).
    // The mod tests what is hidden once a frame, from the camera's depth in the pass it records the test in. In MultiPass that was
    // the left eye's pass only, so anything only the right eye could see was hidden. Here the eyes take turns, each tested in its
    // own pass, and every result that comes back is MERGED with the other eye's latest one: a box either eye saw counts as seen.
    // (Turns alone were not enough - headset: right-eye-only objects still culled. When results arrive close together the mod
    // consumes only the newest and drops the rest, so one eye's results could be dropped again and again.) Hides and restores
    // happen only before the first eye, so both eyes draw the same objects.
    [HarmonyPatch]
    internal static class StereoCullingPatches
    {
        public static bool enabled = true;               // false = the mod as it is (left eye only)
        public static bool mergeEyes = true;             // false = turns only (the first build: right-eye-only objects still culled)
        public static int maxOtherEyeAgeFrames = 12;     // the other eye's result older than this is not merged (an old "seen" only keeps more drawn)
        public static int minimumOccludedChecks = 2;     // with the eyes taking turns, 1 would hide what only the other eye sees
        public static float behindConeMargin = 1.05f;    // "hide objects behind you": kept off both eyes' views, this much wider

        private static Camera testingCamera;             // the gameplay camera whose eye is recording the test
        private static readonly int[] lastTestFrame = { -1, -1 };
        private static int turnFrame = -1;
        private static int turnEye;
        private static int[] eyeOfSlot = new int[32];    // by the slot's ring index: the eye its current test came from...
        private static int[] tokenOfSlot = new int[32];  // ...for that test (token)
        private static readonly int[] testsByEye = new int[2];
        private static long resultsMerged;
        private static long boxesMerged;
        private static bool loggedFirstTest;
        private static bool loggedSummary;
        private static float summaryAt = -1f;

        // One eye's latest result as it came back, before any merge
        private sealed class EyeResult
        {
            public uint[] data;
            public int generation;
            public int boxCount;
            public int frame;
            public bool present;
        }
        private static readonly EyeResult[] lastResult = { new EyeResult(), new EyeResult() };

        private static bool Stereo(Camera camera) => enabled && camera != null && camera.stereoEnabled;
        private static int EyeIndex(Camera camera) => camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;

        // The eye whose last test is older (frame parity alone gave the right eye 295 tests to the left's 504 in 20 s: a turn with no
        // free slot is lost, so the turn stays with that eye). Fixed for the frame.
        private static int TurnEye
        {
            get
            {
                if (turnFrame != Time.frameCount)
                {
                    turnFrame = Time.frameCount;
                    turnEye = lastTestFrame[1] < lastTestFrame[0] ? 1 : 0;
                }
                return turnEye;
            }
        }

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
            int eyeIndex = (int)eye;
            testsByEye[eyeIndex]++;
            lastTestFrame[eyeIndex] = Time.frameCount;
            if (slot.index >= eyeOfSlot.Length)
            {
                Array.Resize(ref eyeOfSlot, slot.index + 16);
                Array.Resize(ref tokenOfSlot, slot.index + 16);
            }
            if (slot.index >= 0)
            {
                eyeOfSlot[slot.index] = eyeIndex;
                tokenOfSlot[slot.index] = slot.token;
            }
            if (!loggedSummary && summaryAt > 0f && Time.unscaledTime > summaryAt)
            {
                loggedSummary = true;
                Plugin.MyLog.LogInfo($"[FuckThisCulling VR] first 20 s: tests left eye {testsByEye[0]}, right eye {testsByEye[1]}; "
                    + $"{resultsMerged} results merged with the other eye's, {boxesMerged} boxes seen only by the other eye kept visible");
            }
        }

        // A result came back (QueryPipeline.Read: copied into slot.data, ready): kept as this eye's latest, then every box the other
        // eye's latest result saw is marked seen here too. Packet (0.4.1): 4 header words, one byte per box (the compact words),
        // then at compactWords the previous test's token, then the GPU's changed-word bits. A box's byte: low 2 bits = its state
        // (LaneClassifier: 1 = occluded -> hide; 3 = still occluded while hidden; 0 / 2 = seen), upper bits = behind-hide flags.
        // (Round 2 read the whole byte as a VisibleReason code - wrong, so it never merged anything.)
        private const uint StateMask = 0x3u;
        private static bool Seen(uint state) => state == 0u || state == 2u;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(QueryPipeline), "Read")]
        private static void MergeOtherEye(QueryPipeline __instance, QuerySlot slot)
        {
            if (!enabled || slot == null || !slot.ready || slot.data == null || slot.index < 0 || slot.index >= eyeOfSlot.Length
                || tokenOfSlot[slot.index] != slot.token)
                return;   // not a test from an eye
            int eye = eyeOfSlot[slot.index];
            uint[] data = slot.data;
            EyeResult own = lastResult[eye];
            if (own.data == null || own.data.Length != data.Length)
                own.data = new uint[data.Length];
            Array.Copy(data, own.data, data.Length);
            own.generation = slot.generation;
            own.boxCount = slot.sourceBoxCount;
            own.frame = slot.frame;
            own.present = true;
            EyeResult other = lastResult[1 - eye];
            if (!other.present || other.generation != slot.generation || other.data.Length != data.Length
                || Math.Abs(slot.frame - other.frame) > maxOtherEyeAgeFrames)
                return;
            CompareEyes(eye, own, other);
            if (!mergeEyes)
                return;
            uint[] otherData = other.data;
            int compactWords = __instance._compactWords;
            int boxes = Math.Min(slot.sourceBoxCount, other.boxCount);
            int endWord = Math.Min(Math.Min(data.Length, compactWords), 4 + (boxes + 3) / 4);
            for (int word = 4; word < endWord; word++)
            {
                uint mine = data[word];
                uint theirs = otherData[word];
                if (mine == theirs)
                    continue;
                uint merged = mine;
                for (int lane = 0; lane < 4; lane++)
                {
                    if ((word - 4) * 4 + lane >= boxes)
                        break;
                    int shift = lane * 8;
                    if (!Seen((mine >> shift) & StateMask) && Seen((theirs >> shift) & StateMask))
                    {
                        merged = (merged & ~(0xFFu << shift)) | (theirs & (0xFFu << shift));
                        boxesMerged++;
                    }
                }
                data[word] = merged;
            }
            // The GPU's changed-word bits compare this test with the one before it (the other eye's, unmerged), and the decoder reads
            // only those words while the token chain holds: break the chain so it decodes the whole merged result
            if (compactWords > 0 && compactWords < data.Length)
                data[compactWords] = uint.MaxValue;
            resultsMerged++;
        }

        // Diagnostic (headset round 2: 780 merges, 0 boxes seen by one eye only - two real eye views always differ somewhere):
        // every 2 s, the two eyes' latest raw results compared box by box (by state), and each eye's state counts, first 10 lines
        public static bool logEyeComparison = true;
        private static float nextComparison;
        private static int comparisonLogs;
        private static readonly int[][] stateCounts = { new int[4], new int[4] };

        private static void CompareEyes(int eye, EyeResult own, EyeResult other)
        {
            if (!logEyeComparison || comparisonLogs >= 10 || Time.unscaledTime < nextComparison)
                return;
            nextComparison = Time.unscaledTime + 2f;
            comparisonLogs++;
            EyeResult left = eye == 0 ? own : other;
            EyeResult right = eye == 0 ? other : own;
            int boxes = Math.Min(left.boxCount, right.boxCount);
            int leftOnly = 0, rightOnly = 0, both = 0, neither = 0, differentBytes = 0;
            Array.Clear(stateCounts[0], 0, 4);
            Array.Clear(stateCounts[1], 0, 4);
            for (int box = 0; box < boxes; box++)
            {
                int word = 4 + box / 4;
                int shift = (box & 3) * 8;
                uint leftByte = (left.data[word] >> shift) & 0xFFu;
                uint rightByte = (right.data[word] >> shift) & 0xFFu;
                bool leftSees = Seen(leftByte & StateMask);
                bool rightSees = Seen(rightByte & StateMask);
                if (leftSees && rightSees) both++;
                else if (leftSees) leftOnly++;
                else if (rightSees) rightOnly++;
                else neither++;
                if (leftByte != rightByte) differentBytes++;
                stateCounts[0][leftByte & StateMask]++;
                stateCounts[1][rightByte & StateMask]++;
            }
            Plugin.MyLog.LogInfo($"[FuckThisCulling VR] eyes compared (left test frame {left.frame}, right {right.frame}, {boxes} boxes): "
                + $"seen by both {both}, left only {leftOnly}, right only {rightOnly}, neither {neither}, different bytes {differentBytes}; "
                + $"states 0/1/2/3 left {string.Join("/", stateCounts[0])}, right {string.Join("/", stateCounts[1])}");
        }

        private static Matrix4x4 ReferenceProjection(Camera camera) => camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);

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
                : Time.frameCount - sample.Frame > __instance._settings.MaxAge.Value ? "age"
                : moved > __instance._settings.MotionDistance.Value ? "translation/cut"
                : turned > __instance._settings.MotionAngle.Value ? "rotation/cut"
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
            if (!Stereo(camera))
                return;
            Matrix4x4 given = projection;
            projection = BothEyesProjection(camera, projection);
            if (!loggedCone)
            {
                loggedCone = true;
                Plugin.MyLog.LogInfo($"[FuckThisCulling VR] behind-you cone kept off both eyes: half-angle tangents across {1f / projection.m00:0.000} "
                    + $"up {1f / projection.m11:0.000} (the eye's own {1f / given.m00:0.000} / {1f / given.m11:0.000})");
            }
        }
        private static bool loggedCone;

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
