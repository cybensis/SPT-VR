using System;
using FuckThisCulling.Engine;
using HarmonyLib;
using UnityEngine;

namespace TarkovVR.ModSupport.FuckThisCullingSupport
{
    // Makes FuckThisCulling 0.4.1 work in VR (Plugin applies this to that version only). Its GPU visibility test runs once a frame,
    // in the pass it is recorded in. In MultiPass that is the left eye's pass only, so objects only the right eye saw were hidden.
    // Here the eyes take turns running the test, and each result is merged with the other eye's latest one, because the mod uses
    // only the newest result. Objects are hidden and restored only before the left eye, so both eyes draw the same objects.
    // During an eye's pre-render the camera's plain matrices are that eye's, so the test itself needs no matrix changes.
    [HarmonyPatch]
    internal static class StereoCullingPatches
    {
        public static int maxOtherEyeAgeFrames = 12;     // frames; an older result from the other eye is not merged
        public static int minimumOccludedChecks = 2;     // occluded results in a row before an object is hidden
        public static float behindConeMargin = 1.05f;    // the "slowly hide objects behind you" cone is this much wider than both eyes' views

        private static Camera testingCamera;
        private static readonly int[] lastTestFrame = { -1, -1 };
        private static readonly int[] lastDrawnFrame = { -1, -1 };
        private static int turnFrame = -1;
        private static int turnEye;
        private static readonly int[] eyeOfSlot = new int[64];     // by the slot's ring index: the eye of its current test...
        private static readonly int[] tokenOfSlot = new int[64];   // ...and that test's token
        private static readonly int[] testsByEye = new int[2];
        private static long boxesKept;
        private static float summaryAt = -1f;
        private static bool loggedSummary;

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

        private static bool Stereo(Camera camera) => camera != null && camera.stereoEnabled;
        private static int EyeIndex(Camera camera) => camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0;
        private static Matrix4x4 ReferenceProjection(Camera camera) => camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);

        // The eye whose turn it is: the drawn eye whose last test is older. A turn that finds no free slot is lost, so the turn stays
        // with that eye. An eye that isn't being drawn never gets the turn. Fixed for the frame.
        private static int TurnEye
        {
            get
            {
                int frame = Time.frameCount;
                if (turnFrame != frame)
                {
                    turnFrame = frame;
                    bool leftDrawn = lastDrawnFrame[0] >= frame - 2;
                    bool rightDrawn = lastDrawnFrame[1] >= frame - 2;
                    turnEye = leftDrawn && !rightDrawn ? 0
                        : rightDrawn && !leftDrawn ? 1
                        : lastTestFrame[1] < lastTestFrame[0] ? 1 : 0;
                }
                return turnEye;
            }
        }

        // Hides and restores run in the gameplay camera's pre-cull, which runs once per eye. Let it run only before the left eye.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VisibilityEngine), "PreCull")]
        private static bool ConsumeBeforeFirstEyeOnly(VisibilityEngine __instance, Camera c)
        {
            return !(c != null && c == __instance._camera && Stereo(c) && c.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right);
        }

        // The test is recorded in an eye's pre-render and runs in that eye's pass. Record it only in the eye whose turn it is.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(VisibilityEngine), "PreRender")]
        private static bool TestInTurnEyeOnly(VisibilityEngine __instance, Camera c)
        {
            if (c == null || c != __instance._camera || !Stereo(c))
                return true;
            int eye = EyeIndex(c);
            lastDrawnFrame[eye] = Time.frameCount;
            if (eye != TurnEye)
                return false;
            testingCamera = c;
            return true;
        }

        // Records which eye a test came from (for the merge), and stores the left eye's projection as the stale check's reference
        // (StaleStereo).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(QueryPipeline), "CaptureSource")]
        private static void TagEyeTest(QueryPipeline __instance, QuerySlot slot)
        {
            Camera camera = __instance._seams.GameplayCamera();
            if (!Stereo(camera) || camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Mono || slot.index < 0 || slot.index >= eyeOfSlot.Length)
                return;
            int eye = EyeIndex(camera);
            slot.projection = ReferenceProjection(camera);
            eyeOfSlot[slot.index] = eye;
            tokenOfSlot[slot.index] = slot.token;
            lastTestFrame[eye] = Time.frameCount;
            testsByEye[eye]++;
            if (summaryAt < 0f)
                summaryAt = Time.unscaledTime + 20f;
            else if (!loggedSummary && Time.unscaledTime > summaryAt)
            {
                loggedSummary = true;
                Plugin.MyLog.LogInfo($"[FuckThisCulling VR] first 20 s: tests left eye {testsByEye[0]}, right eye {testsByEye[1]}; {boxesKept} boxes seen only by the other eye kept visible");
            }
        }

        // A result came back (QueryPipeline.Read copied it into slot.data and set ready). It is kept as this eye's latest, then every
        // box the other eye's latest result saw is marked as seen in this one too.
        // Result layout: 4 header words, one byte per box up to compactWords, then the previous test's token, then the GPU's
        // changed-word bits. In a box's byte, the low 2 bits are its state (LaneClassifier: 1 = occluded, so hide it; 3 = still
        // occluded while hidden; 0 or 2 = seen) and the upper bits are behind-hide flags.
        private const uint StateMask = 0x3u;
        private static bool Seen(uint state) => state == 0u || state == 2u;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(QueryPipeline), "Read")]
        private static void MergeOtherEye(QueryPipeline __instance, QuerySlot slot)
        {
            if (slot == null || !slot.ready || slot.data == null || slot.index < 0 || slot.index >= eyeOfSlot.Length
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
                for (int lane = 0; lane < 4 && (word - 4) * 4 + lane < boxes; lane++)
                {
                    int shift = lane * 8;
                    if (!Seen((mine >> shift) & StateMask) && Seen((theirs >> shift) & StateMask))
                    {
                        merged = (merged & ~(0xFFu << shift)) | (theirs & (0xFFu << shift));
                        boxesKept++;
                    }
                }
                data[word] = merged;
            }
            // The GPU's changed-word bits compare this test with the one before it (the other eye's, unmerged), and while the token
            // chain holds, the decoder reads only those words. Breaking the chain makes it decode the whole merged result.
            if (compactWords > 0 && compactWords < data.Length)
                data[compactWords] = uint.MaxValue;
        }

        // RestoreTriggers.Stale (0.4.1), but compared against the reference projection stored in the slots (TagEyeTest). The original
        // reads the camera's plain projection at consume time, which a right-eye test never matches.
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
        private static void AtLeastMinimumOccludedChecks(ref int __result)
        {
            __result = Math.Max(__result, minimumOccludedChecks);
        }

        // "Slowly hide objects behind you" builds its cone from the projection it is given and the camera's rotation. Give it a
        // projection that covers both eyes.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BehindHides), "Bind")]
        private static void ConeOffBothEyes(ref Matrix4x4 projection)
        {
            if (Stereo(testingCamera))
                projection = BothEyesProjection(testingCamera, projection);
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
