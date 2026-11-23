using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Controls
{
    // "Step Or Lean To Sprint" (VRSettings.GetStepToSprint). A quick step or lean forward starts the run while the move stick is pushed
    // forward (or if it is pushed within goGrace afterwards). A quick step or lean back ends it. Releasing the stick still ends the run,
    // and the stick click still toggles it (InputHandlers.SprintInputHandler).
    // Movement is measured at the neck point of the raw headset pose (HeadTracking.RawHmdPose + neckLocalOffset), because the headset
    // itself swings ~10 cm around the neck when you look around or nod. Raw tracking space excludes stick movement and turning.
    // `settled` (where you stand) follows the neck slowly. A start has to end ahead of it and a stop behind it, so straightening up
    // after a lean doesn't undo it. A lean held for a while becomes the new `settled`.
    public static class StepSprint
    {
        [Tunable("Moving", TunableKind.Tune, "The time a quick move is measured over. Longer = slower moves count.", Unit = "s")]
        public static float moveTime = 0.2f;        // s, the window a quick move is measured over
        [Tunable("Moving", TunableKind.Tune, "How far forward the neck has to go within moveTime to start the run. Lower = easier to trigger.", Unit = "m")]
        public static float startDistance = 0.08f;  // m the neck has to move forward within moveTime to start the run (0.4 m/s)
        [Tunable("Moving", TunableKind.Tune, "How far back it has to go within moveTime to stop it.", Unit = "m")]
        public static float stopDistance = 0.08f;   // m the neck has to move back within moveTime to stop it
        [Tunable("Moving", TunableKind.Tune, "How far ahead of where you stand a start has to end (behind, a stop). Keeps coming back upright from counting.", Unit = "m")]
        public static float aheadMargin = 0.04f;    // m ahead of `settled` a start has to end (behind it, for a stop)
        [Tunable("Moving", TunableKind.Tune, "How long 'where you stand' takes to follow you. Longer = a held lean stays a lean longer before straightening "
            + "up counts as a stop.", Unit = "s")]
        public static float settleTime = 2.5f;      // s, time constant of `settled` following the neck: longer = a held lean counts as a lean longer
        [Tunable("Moving", TunableKind.Tune, "Sideways movement allowed, as a share of the forward / back.")]
        public static float sidewaysShare = 0.7f;   // sideways movement allowed, as a fraction of the forward / back movement
        [Tunable("Moving", TunableKind.Tune, "Up / down allowed, as a share of the forward / back (crouching moves the neck forward too).")]
        public static float verticalShare = 1f;     // up / down movement allowed, same fraction: crouching and standing up also move the neck
        public static float glitchSpeed = 3f;       // m/s, faster than a body moves = a tracking jump or recenter, and the history is dropped
        [Tunable("Moving", TunableKind.Tune, "A step this long before the stick is pushed forward still starts the run.", Unit = "s")]
        public static float goGrace = 0.35f;        // s, a step this long before the stick is pushed forward still starts the run
        [Tunable("Moving", TunableKind.Tune, "Time after a start or stop before another counts.", Unit = "s")]
        public static float cooldown = 0.5f;        // s after a start / stop before another (the game's sprint state lags the command)
        // A start also needs the head looking roughly the way you'd run, so leaning out to look down off a ledge doesn't start a run.
        // Stops ignore where you look.
        [Tunable("Moving", TunableKind.Tune, "A start needs the head looking no further down than this (leaning out to look off a ledge doesn't start a "
            + "run). Stops ignore where you look.", Unit = "°")]
        public static float maxLookDown = 35f;      // deg below level
        [Tunable("Moving", TunableKind.Tune, "...and no further up than this.", Unit = "°")]
        public static float maxLookUp = 45f;        // deg above level
        [Tunable("Moving", TunableKind.Tune, "...and no further to the side of the way the character faces than this.", Unit = "°")]
        public static float maxLookSideways = 60f;  // deg off the way the character faces
        [Tunable("Moving", TunableKind.Tune, "The buzz once the run has started (a stop gets half). 0 = none.")]
        public static float pulseAmplitude = 0.3f;  // haptic pulse on both controllers once the game has started the run; a stop gets half
        public static float pulseDuration = 0.06f;
        public static float pulseFrequency = 90f;
        [Tunable("Moving", TunableKind.Log, "A line per start and stop, and per near miss while walking or running, with its numbers. On for the first "
            + "tuning round.", Suggested = false)]
        public static bool debugLog = false;         // logs a line per start / stop and per near miss while walking or running

        private const int Capacity = 256;
        private static readonly float[] sampleTime = new float[Capacity];
        private static readonly Vector3[] sampleNeck = new Vector3[Capacity];
        private static int newest = -1;
        private static int stored;
        private static Vector3 settled;

        private static int lastFrame = -1;
        private static float startPendingUntil = -1f;
        private static float cooldownUntil;
        private static float confirmUntil = -1f;
        private static bool confirmRunning;

        // The biggest movement of the current move, for the near-miss log line
        private static float candidateAlong;
        private static string candidateLine;
        private static bool candidateFired;

        // Called once a frame from the sprint handler. Returns +1 = start the run, -1 = stop it, 0 = nothing.
        public static int Update(bool running, bool stickForward)
        {
            // A second call in the same frame returns 0: a start returned twice would toggle the run straight back off
            if (Time.frameCount == lastFrame)
                return 0;
            lastFrame = Time.frameCount;
            if (!VRSettings.GetStepToSprint() || VRGlobals.player == null)
            {
                Clear();
                return 0;
            }
            return Measure(running, stickForward);
        }

        private static int Measure(bool running, bool stickForward)
        {
            float now = Time.unscaledTime;
            Confirm(now, running);
            if (!HeadTracking.RawHmdPose(out Vector3 headPos, out Quaternion headRot))
            {
                Clear();
                return 0;
            }
            Vector3 neck = headPos + headRot * HeadTracking.neckLocalOffset;

            // After a time gap (menus, a hitch) or a jump faster than a body moves, the history no longer describes this movement
            if (stored > 0)
            {
                float gap = now - sampleTime[newest];
                if (gap > 0.15f || (neck - sampleNeck[newest]).magnitude > glitchSpeed * gap + 0.02f)
                    Clear();
            }
            if (stored == 0)
                settled = neck;
            settled = Vector3.Lerp(settled, neck, 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(0.05f, settleTime)));
            Push(now, neck);

            int from = OldestSince(now - moveTime);
            float span = now - sampleTime[from];
            if (span < 0.05f)
                return 0;
            Vector3 forward = TrackingForward(headRot);
            Vector3 moved = neck - sampleNeck[from];
            Vector3 flatMoved = new Vector3(moved.x, 0f, moved.z);
            float along = Vector3.Dot(flatMoved, forward);
            float sideways = Mathf.Abs(Vector3.Dot(flatMoved, Vector3.Cross(Vector3.up, forward)));
            float vertical = Mathf.Abs(moved.y);
            Vector3 fromSettled = neck - settled;
            float ahead = Vector3.Dot(new Vector3(fromSettled.x, 0f, fromSettled.z), forward);

            bool backward = along < 0f;
            float distance = backward ? stopDistance : startDistance;
            float size = Mathf.Abs(along);
            bool clean = sideways <= size * sidewaysShare && vertical <= size * verticalShare;
            bool pastWhereYouStand = backward ? ahead <= -aheadMargin : ahead >= aheadMargin;
            bool quick = size >= distance;

            string blocked = null;
            if (!quick)
                blocked = "too slow / too short";
            else if (!clean)
                blocked = sideways > size * sidewaysShare ? "too far sideways" : "too far up / down (crouching?)";
            else if (!pastWhereYouStand)
                blocked = backward ? "only back to where you stand" : "only back to where you stand (after a lean back)";
            else if (now < cooldownUntil)
                blocked = "cooldown";
            else if (!backward && !CanStart(headRot, forward, out string why))
                blocked = why;

            int result = 0;
            if (blocked == null)
            {
                if (!backward && !running)
                {
                    // goGrace counts from the end of the move; logged once
                    bool fresh = startPendingUntil < 0f;
                    startPendingUntil = now + goGrace;
                    if (fresh)
                        Note(along, span, sideways, vertical, ahead, "start" + (stickForward ? "" : " (waiting for the stick)"), true);
                }
                else if (backward && running)
                {
                    result = -1;
                    startPendingUntil = -1f;
                    cooldownUntil = now + cooldown;
                    confirmUntil = now + 0.4f;
                    confirmRunning = false;
                    Note(along, span, sideways, vertical, ahead, "stop", true);
                }
            }
            else if (size >= distance * 0.5f && (backward ? running : stickForward))
                Note(along, span, sideways, vertical, ahead, "near miss: " + blocked, false);

            if (startPendingUntil >= 0f)
            {
                if (running)
                    startPendingUntil = -1f;
                else if (stickForward && now <= startPendingUntil && !LookingForward(headRot, forward, out string lookWhy))
                {
                    // The head looked away (e.g. down off a ledge) between the lean and the stick push
                    startPendingUntil = -1f;
                    if (debugLog)
                        Plugin.MyLog.LogInfo($"[StepSprint] start dropped: {lookWhy}");
                }
                else if (stickForward && now <= startPendingUntil)
                {
                    result = 1;
                    startPendingUntil = -1f;
                    cooldownUntil = now + cooldown;
                    confirmUntil = now + 0.4f;
                    confirmRunning = true;
                }
                else if (now > startPendingUntil)
                {
                    startPendingUntil = -1f;
                    if (debugLog)
                        Plugin.MyLog.LogInfo($"[StepSprint] start dropped: the stick wasn't pushed forward within {goGrace:0.##} s");
                }
            }

            // The move is over: log its near miss if it never fired
            if (size < distance * 0.25f)
            {
                if (debugLog && candidateLine != null && !candidateFired)
                    Plugin.MyLog.LogInfo(candidateLine);
                candidateLine = null;
                candidateFired = false;
                candidateAlong = 0f;
            }
            return result;
        }

        // Plays the haptic pulse once the game has actually started (or stopped) the run
        private static void Confirm(float now, bool running)
        {
            if (confirmUntil < 0f)
                return;
            if (running == confirmRunning)
            {
                float amplitude = confirmRunning ? pulseAmplitude : pulseAmplitude * 0.5f;
                HapticMixer.PlayPulse(SteamVR_Input_Sources.LeftHand, amplitude, pulseDuration, pulseFrequency);
                HapticMixer.PlayPulse(SteamVR_Input_Sources.RightHand, amplitude, pulseDuration, pulseFrequency);
                confirmUntil = -1f;
            }
            else if (now > confirmUntil)
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[StepSprint] the game didn't {(confirmRunning ? "start" : "stop")} the run (stamina / pose / obstacle)");
                confirmUntil = -1f;
            }
        }

        private static bool CanStart(Quaternion headRot, Vector3 forward, out string why)
        {
            why = null;
            EFT.Player player = VRGlobals.player;
            if (player.IsInPronePose)
                why = "prone";
            else if (player.HandsController is EFT.Player.FirearmController firearm && firearm.IsAiming)
                why = "aiming";
            else
                LookingForward(headRot, forward, out why);
            return why == null;
        }

        // True when the headset's look direction (raw tracking space, like `forward`) is within maxLookDown / maxLookUp of level
        // and within maxLookSideways of the run direction
        private static bool LookingForward(Quaternion headRot, Vector3 forward, out string why)
        {
            why = null;
            Vector3 look = headRot * Vector3.forward;
            float pitch = Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg;   // + = up
            Vector3 flatLook = new Vector3(look.x, 0f, look.z);
            float sideways = flatLook.sqrMagnitude > 1e-4f ? Vector3.Angle(flatLook, forward) : 0f;
            if (pitch < -maxLookDown)
                why = $"looking down ({-pitch:0} deg, starts up to {maxLookDown:0})";
            else if (pitch > maxLookUp)
                why = $"looking up ({pitch:0} deg, starts up to {maxLookUp:0})";
            else if (sideways > maxLookSideways)
                why = $"looking to the side ({sideways:0} deg off the run, starts up to {maxLookSideways:0})";
            return why == null;
        }

        // The way the character faces, in tracking space (the rig's local frame, the same frame as the poses).
        // Without a rig, the head's yaw is used.
        private static Vector3 TrackingForward(Quaternion headRot)
        {
            Vector3 forward;
            if (VRGlobals.vrOffsetter != null && VRGlobals.player != null)
                forward = Quaternion.Inverse(VRGlobals.vrOffsetter.transform.rotation) * VRGlobals.player.Transform.forward;
            else
            {
                forward = headRot * Vector3.forward;
                // Looking straight down, the face points at the floor, so the top of the head gives the forward direction
                if (new Vector2(forward.x, forward.z).sqrMagnitude < 0.04f)
                    forward = headRot * Vector3.up;
            }
            forward.y = 0f;
            return forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
        }

        private static void Note(float along, float span, float sideways, float vertical, float ahead, string what, bool fired)
        {
            if (fired)
                candidateFired = true;
            if (!debugLog || (!fired && Mathf.Abs(along) <= candidateAlong))
                return;
            string line = $"[StepSprint] {what}: {along * 100f:0.#} cm in {span:0.00} s (needs {(along < 0f ? stopDistance : startDistance) * 100f:0.#}), " +
                $"{ahead * 100f:0.#} cm from where you stand, sideways {sideways * 100f:0.#}, up/down {vertical * 100f:0.#}";
            if (fired)
                Plugin.MyLog.LogInfo(line);
            else
            {
                candidateAlong = Mathf.Abs(along);
                candidateLine = line;
            }
        }

        private static void Push(float time, Vector3 neck)
        {
            newest = (newest + 1) % Capacity;
            sampleTime[newest] = time;
            sampleNeck[newest] = neck;
            stored = Mathf.Min(stored + 1, Capacity);
        }

        // The oldest sample no older than `since`
        private static int OldestSince(float since)
        {
            int index = newest;
            for (int i = 1; i < stored; i++)
            {
                int older = (newest - i + Capacity) % Capacity;
                if (sampleTime[older] < since)
                    break;
                index = older;
            }
            return index;
        }

        private static void Clear()
        {
            stored = 0;
            newest = -1;
            startPendingUntil = -1f;
            candidateLine = null;
            candidateFired = false;
            candidateAlong = 0f;
        }
    }
}
