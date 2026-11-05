using System.Collections;
using System.Collections.Generic;
using System.Text;
using TarkovVR.Source.XR;
using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Source.Misc
{
    // Ctrl+F11, the alternate-eye experiment (measurement only): each press steps off -> both eyes (baseline) -> alternating eyes
    // -> off. Alternating renders one eye a frame, left and right in turn (Camera.stereoTargetEye on every camera that drew both
    // eyes), the CPU cost of left-only, the way the finished mode would. Nothing fixes up the eye left out yet: Unity's OpenXR plugin
    // submits it however it does, which is what this finds out. Every reportSeconds an "[AltEye]" line: Unity's frame interval,
    // render passes a frame, and what the native frame watcher (OpenXRFrameWatch) saw: views a frame, whether each eye's swapchain was
    // taken this frame, whether each eye's pose was the same as the frame before, and how long the frame calls waited. The first
    // frames of each step are described in full. It will judder while alternating: expected, the eye left out isn't reprojected.
    [DefaultExecutionOrder(32000)]   // our LateUpdate last: the eye is set right before the cameras render
    internal class AlternateEyeExperiment : MonoBehaviour
    {
        public static bool hotkeyEnabled = true;
        public static float reportSeconds = 3f;

        private enum Step { Off, BothEyes, Alternating }
        private Step step;
        private readonly List<Camera> eyeCameras = new List<Camera>();
        private readonly List<StereoTargetEyeMask> originalMasks = new List<StereoTargetEyeMask>();
        private readonly List<float> frameIntervals = new List<float>();
        private readonly int[] renderPassCounts = new int[4];
        private float reportAt;
        private static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();

        private void Update()
        {
            if (hotkeyEnabled && Input.GetKeyDown(KeyCode.F11) && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
                NextStep();
            if (step != Step.Off)
                frameIntervals.Add(Time.unscaledDeltaTime * 1000f);
        }

        private void LateUpdate()
        {
            if (step != Step.Alternating)
                return;
            StereoTargetEyeMask eye = Time.frameCount % 2 == 0 ? StereoTargetEyeMask.Left : StereoTargetEyeMask.Right;
            foreach (Camera camera in eyeCameras)
            {
                if (camera != null)
                    camera.stereoTargetEye = eye;
            }
        }

        private IEnumerator Start()
        {
            WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;
                if (step == Step.Off)
                    continue;
                XRDisplaySubsystem display = Display();
                if (display != null)
                    renderPassCounts[Mathf.Clamp(display.GetRenderPassCount(), 0, renderPassCounts.Length - 1)]++;
                if (Time.unscaledTime >= reportAt)
                    Report();
            }
        }

        private void NextStep()
        {
            if (step == Step.Off)
            {
                step = Step.BothEyes;
                Plugin.MyLog.LogWarning($"[AltEye] step 1: both eyes (baseline). Stand still; Ctrl+F11 again for alternating eyes. "
                    + $"Frame watcher: {(OpenXRFrameWatch.Loaded ? $"on (functions {OpenXRFrameWatch.Hooked()})" : "OFF")}");
            }
            else if (step == Step.BothEyes)
            {
                FindEyeCameras();
                step = Step.Alternating;
                Plugin.MyLog.LogWarning($"[AltEye] step 2: alternating eyes on {eyeCameras.Count} camera(s) ({CameraNames()}). It will judder: "
                    + "look at what each eye shows, then Ctrl+F11 to stop");
            }
            else
            {
                RestoreEyeCameras();
                step = Step.Off;
                Plugin.MyLog.LogWarning("[AltEye] off: both eyes back");
                return;
            }
            // A clean start for the new step: its first frames in full, totals from here
            OpenXRFrameWatch.TakeStats();
            OpenXRFrameWatch.TakeDetail();
            OpenXRFrameWatch.DescribeNextFrames(4);
            frameIntervals.Clear();
            System.Array.Clear(renderPassCounts, 0, renderPassCounts.Length);
            reportAt = Time.unscaledTime + reportSeconds;
        }

        // Every enabled camera that draws both eyes (the headset camera, SPT-VR's UI camera)
        private void FindEyeCameras()
        {
            eyeCameras.Clear();
            originalMasks.Clear();
            foreach (Camera camera in Camera.allCameras)
            {
                if (camera != null && camera.stereoEnabled && camera.stereoTargetEye == StereoTargetEyeMask.Both)
                {
                    eyeCameras.Add(camera);
                    originalMasks.Add(camera.stereoTargetEye);
                }
            }
        }

        private void RestoreEyeCameras()
        {
            for (int index = 0; index < eyeCameras.Count; index++)
            {
                if (eyeCameras[index] != null)
                    eyeCameras[index].stereoTargetEye = originalMasks[index];
            }
            eyeCameras.Clear();
            originalMasks.Clear();
        }

        private string CameraNames()
        {
            StringBuilder names = new StringBuilder();
            foreach (Camera camera in eyeCameras)
                names.Append(names.Length > 0 ? ", " : "").Append(camera != null ? camera.name : "gone");
            return names.ToString();
        }

        private void Report()
        {
            reportAt = Time.unscaledTime + reportSeconds;
            double[] watch = OpenXRFrameWatch.TakeStats();
            double frames = System.Math.Max(1.0, watch[(int)OpenXRFrameWatch.Stat.Frames]);
            double waitFrames = System.Math.Max(1.0, watch[(int)OpenXRFrameWatch.Stat.WaitFrames]);
            double beginFrames = System.Math.Max(1.0, watch[(int)OpenXRFrameWatch.Stat.BeginFrames]);
            StringBuilder line = new StringBuilder();
            line.Append($"[AltEye] {(step == Step.Alternating ? "ALTERNATING" : "both eyes")}: frame {Median(frameIntervals):0.00} ms median ({frameIntervals.Count} frames), "
                + $"render passes a frame 0/1/2/3+: {renderPassCounts[0]}/{renderPassCounts[1]}/{renderPassCounts[2]}/{renderPassCounts[3]}");
            if (OpenXRFrameWatch.Loaded)
            {
                line.Append($" | OpenXR {watch[(int)OpenXRFrameWatch.Stat.Frames]:0} frames: layers {watch[(int)OpenXRFrameWatch.Stat.Layers] / frames:0.00}, "
                    + $"views {watch[(int)OpenXRFrameWatch.Stat.Views] / frames:0.00}, views share a swapchain {100 * watch[(int)OpenXRFrameWatch.Stat.ViewsShareSwapchain] / frames:0}%, "
                    + $"slices {watch[(int)OpenXRFrameWatch.Stat.View0ArraySlice]:0}/{watch[(int)OpenXRFrameWatch.Stat.View1ArraySlice]:0}, "
                    + $"left/right swapchain taken {100 * watch[(int)OpenXRFrameWatch.Stat.View0Acquired] / frames:0}%/{100 * watch[(int)OpenXRFrameWatch.Stat.View1Acquired] / frames:0}%, "
                    + $"left/right pose same as last frame {100 * watch[(int)OpenXRFrameWatch.Stat.View0SamePose] / frames:0}%/{100 * watch[(int)OpenXRFrameWatch.Stat.View1SamePose] / frames:0}%, "
                    + $"swapchains taken {watch[(int)OpenXRFrameWatch.Stat.SwapchainsTaken] / frames:0.00}, acquires {watch[(int)OpenXRFrameWatch.Stat.Acquires] / frames:0.00} releases {watch[(int)OpenXRFrameWatch.Stat.Releases] / frames:0.00} a frame | "
                    + $"waits (ms, avg/max): xrWaitFrame {watch[(int)OpenXRFrameWatch.Stat.WaitFrameMs] / waitFrames:0.00}/{watch[(int)OpenXRFrameWatch.Stat.WaitFrameMax]:0.00}, "
                    + $"xrBeginFrame {watch[(int)OpenXRFrameWatch.Stat.BeginFrameMs] / beginFrames:0.00}/{watch[(int)OpenXRFrameWatch.Stat.BeginFrameMax]:0.00}, "
                    + $"image waits {watch[(int)OpenXRFrameWatch.Stat.WaitImageMs] / frames:0.00} a frame/{watch[(int)OpenXRFrameWatch.Stat.WaitImageMax]:0.00}, "
                    + $"acquires {watch[(int)OpenXRFrameWatch.Stat.AcquireMs] / frames:0.00} a frame, "
                    + $"xrEndFrame {watch[(int)OpenXRFrameWatch.Stat.EndFrameMs] / frames:0.00}/{watch[(int)OpenXRFrameWatch.Stat.EndFrameMax]:0.00}");
            }
            Plugin.MyLog.LogInfo(line.ToString());
            string detail = OpenXRFrameWatch.TakeDetail();
            if (detail.Length > 0)
                Plugin.MyLog.LogInfo("[AltEye] first frames of this step:\n" + detail.TrimEnd());
            frameIntervals.Clear();
            System.Array.Clear(renderPassCounts, 0, renderPassCounts.Length);
        }

        private static float Median(List<float> values)
        {
            if (values.Count == 0)
                return float.NaN;
            float[] sorted = values.ToArray();
            System.Array.Sort(sorted);
            return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : 0.5f * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);
        }

        private static XRDisplaySubsystem Display()
        {
#pragma warning disable CS0618 // GetInstances: the 2019 references' name; still in the game's 2022
            SubsystemManager.GetInstances(displays);
#pragma warning restore CS0618
            foreach (XRDisplaySubsystem display in displays)
            {
                if (display.running)
                    return display;
            }
            return null;
        }
    }
}
