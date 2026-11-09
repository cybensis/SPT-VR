using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // A throw's velocity from how the controller actually moved: its raw tracking point (the one the pose listeners place the hands
    // from) over the last few frames, in tracking space - the space the old velocity came in, so the throw code is unchanged.
    //
    // Why (his report 2026-11-09: "throws seem to be going a bit more right than I expect" since OpenXR): SteamVR gave the raw point's
    // velocity itself; OpenXR has no raw point, so OpenXRInput rebuilds it as the grip's velocity + the spin x the 10-15 cm lever from
    // the grip to the raw point. A throw spins the controller fast, so that term is big, and a spin read in the wrong frame bends the
    // throw sideways. Measuring the point's own motion needs neither. One log line per throw compares the two (and the spin itself).
    public static class ThrowVelocity
    {
        // OpenXR: the palm's (grip pose's) velocity as the runtime reports it - its IMU-backed own, no spin term (the raw point's rebuilt
        // one bent throws sideways) and no prediction overshoot (the measured one below: his log had it 1.5-2x the runtime's and up to
        // 60 deg off, toward the arc's centre - the predicted poses run ahead of an accelerating hand). The grenade's in the palm.
        public static bool usePalmVelocity = true;
        public static float palmScale = 1.25f;      // the palm moves slower than the controller's top (the raw point the throws used, ~1.25x in his log): kept to that strength
        public static bool releaseFromHand = true;  // the thrown grenade starts where it is in the hand, not at the controller's tracking point
        public static bool enabled = false;         // the measured velocity (pose slope) when the palm's isn't used; false = the runtime's
        public static float window = 0.05f;         // s of samples fitted (about 4-5 frames): short, so a late curve of the arm doesn't lag in
        public static bool throwLog = true;         // one line per throw: measured vs the runtime's, the angle between, the spin check

        private const int CAPACITY = 16;

        private sealed class History
        {
            public readonly Vector3[] positions = new Vector3[CAPACITY];
            public readonly Quaternion[] rotations = new Quaternion[CAPACITY];
            public readonly float[] times = new float[CAPACITY];
            public int count;
            public int next;
        }

        private static readonly History left = new History();
        private static readonly History right = new History();
        private static int sampledFrame = -1;

        // Every frame, from VRInputManager.UpdateCommands (before the handlers, the grenade throw among them)
        public static void Tick()
        {
            if (sampledFrame == Time.frameCount)
                return;
            sampledFrame = Time.frameCount;
            Sample(left, SteamVR_Input_Sources.LeftHand);
            Sample(right, SteamVR_Input_Sources.RightHand);
        }

        private static void Sample(History history, SteamVR_Input_Sources source)
        {
            VRPose pose = source == SteamVR_Input_Sources.LeftHand ? VRInput.LeftHandPose : VRInput.RightHandPose;
            if (pose == null)
                return;
            history.positions[history.next] = pose.GetLocalPosition(source);
            history.rotations[history.next] = pose.GetLocalRotation(source);
            history.times[history.next] = Time.time;
            history.next = (history.next + 1) % CAPACITY;
            history.count = Mathf.Min(history.count + 1, CAPACITY);
        }

        // The raw point's velocity at the newest sample (tracking space): a least-squares quadratic through the samples of the last
        // `window`, its slope taken at the end (no half-window lag, as a plain average would have), and the spin from the
        // newest rotation against the oldest in the window
        public static bool TryMeasure(SteamVR_Input_Sources source, out Vector3 velocity, out Vector3 angularVelocity)
        {
            Tick();
            History history = source == SteamVR_Input_Sources.LeftHand ? left : right;
            velocity = Vector3.zero;
            angularVelocity = Vector3.zero;
            if (history.count < 3)
                return false;
            int newest = (history.next - 1 + CAPACITY) % CAPACITY;
            float newestTime = history.times[newest];
            int used = 0;
            int oldest = newest;
            // Sums for the fit in t' = t - newestTime (<= 0)
            float s0 = 0f, s1 = 0f, s2 = 0f, s3 = 0f, s4 = 0f;
            Vector3 p0 = Vector3.zero, p1 = Vector3.zero, p2 = Vector3.zero;
            for (int back = 0; back < history.count; back++)
            {
                int index = (newest - back + CAPACITY) % CAPACITY;
                float t = history.times[index] - newestTime;
                if (-t > window && used >= 3)
                    break;
                Vector3 p = history.positions[index];
                float t2 = t * t;
                s0 += 1f; s1 += t; s2 += t2; s3 += t2 * t; s4 += t2 * t2;
                p0 += p; p1 += p * t; p2 += p * t2;
                used++;
                oldest = index;
            }
            float span = newestTime - history.times[oldest];
            if (used < 3 || span < 1e-4f)
                return false;

            // Normal equations of p(t) = a + b t + c t^2; the velocity at t' = 0 is b
            float determinant = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
            if (Mathf.Abs(determinant) < 1e-12f)
            {
                velocity = (history.positions[newest] - history.positions[oldest]) / span;
            }
            else
            {
                // Cramer's rule for b
                float bDeterminantX = s0 * (p1.x * s4 - s3 * p2.x) - p0.x * (s1 * s4 - s3 * s2) + s2 * (s1 * p2.x - p1.x * s2);
                float bDeterminantY = s0 * (p1.y * s4 - s3 * p2.y) - p0.y * (s1 * s4 - s3 * s2) + s2 * (s1 * p2.y - p1.y * s2);
                float bDeterminantZ = s0 * (p1.z * s4 - s3 * p2.z) - p0.z * (s1 * s4 - s3 * s2) + s2 * (s1 * p2.z - p1.z * s2);
                velocity = new Vector3(bDeterminantX, bDeterminantY, bDeterminantZ) / determinant;
            }

            // The spin over the window, world-frame (tracking space): delta = newest * oldest^-1
            Quaternion delta = history.rotations[newest] * Quaternion.Inverse(history.rotations[oldest]);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f)
                angle -= 360f;
            if (!float.IsNaN(axis.x) && !float.IsInfinity(axis.x) && Mathf.Abs(angle) > 1e-3f)
                angularVelocity = axis.normalized * (angle * Mathf.Deg2Rad / span);
            return true;
        }

        // The throw's line in the log: what the runtime said, what the tracking showed, and with OpenXR what the spin term added
        // The palm's velocity, tracking space, scaled by palmScale (OpenXR only)
        public static bool TryPalmVelocity(SteamVR_Input_Sources source, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (!usePalmVelocity || !VRInput.UsingOpenXR)
                return false;
            velocity = OpenXRInput.GetGripVelocity(source == SteamVR_Input_Sources.LeftHand ? OpenXRInput.LEFT_HAND : OpenXRInput.RIGHT_HAND) * palmScale;
            return true;
        }

        public static void LogThrow(SteamVR_Input_Sources source, Vector3 runtimeVelocity, Vector3 measuredVelocity, Vector3 measuredSpin, string used, Vector3 bodyVelocity)
        {
            if (!throwLog)
                return;
            string spin = "";
            VRPose pose = source == SteamVR_Input_Sources.LeftHand ? VRInput.LeftHandPose : VRInput.RightHandPose;
            Vector3 runtimeSpin = pose != null ? pose.GetAngularVelocity(source) : Vector3.zero;
            if (VRInput.UsingOpenXR)
            {
                int hand = source == SteamVR_Input_Sources.LeftHand ? OpenXRInput.LEFT_HAND : OpenXRInput.RIGHT_HAND;
                Vector3 grip = OpenXRInput.GetGripVelocity(hand);
                Vector3 lever = OpenXRInput.GetGripToRawLever(hand);
                spin = $", grip {Format(grip)} (spin term adds {Format(runtimeVelocity - grip)}, lever {lever.magnitude * 100f:F0} cm, " +
                    $"measured vs grip {AngleBetween(measuredVelocity, grip):F0} deg)";
            }
            Plugin.MyLog.LogInfo($"[Throw] {(source == SteamVR_Input_Sources.LeftHand ? "left" : "right")}: used {used}; walking {Format(bodyVelocity)} (world); measured {Format(measuredVelocity)}, " +
                $"runtime {Format(runtimeVelocity)}, {AngleBetween(measuredVelocity, runtimeVelocity):F0} deg apart{spin}; spin runtime {runtimeSpin.magnitude:F1} rad/s vs " +
                $"measured {measuredSpin.magnitude:F1} rad/s, axes {AngleBetween(runtimeSpin, measuredSpin):F0} deg apart");
        }

        private static float AngleBetween(Vector3 a, Vector3 b)
        {
            return a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f ? 0f : Vector3.Angle(a, b);
        }

        private static string Format(Vector3 velocity)
        {
            return $"{velocity.magnitude:F1} m/s ({velocity.x:F1}, {velocity.y:F1}, {velocity.z:F1})";
        }
    }
}
