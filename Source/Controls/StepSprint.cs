using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // "Step Or Lean To Sprint" (VRSettings.GetStepToSprint, his design): a quick step or lean forward starts the run while the move
    // stick is pushed forward (or is pushed within goGrace after), a quick step or lean back ends it. Easing off the stick still ends
    // it and the stick click still toggles it (InputHandlers.SprintInputHandler).
    //
    // Measured on the NECK point under the raw headset pose (HeadTracking.RawHmdPose + neckLocalOffset): the headset swings ~10 cm
    // round the neck when you look around or nod, the neck only moves when the body does. Raw tracking space, so the sticks'
    // moving and turning never show up in it. Forward = the way the character faces (the way it runs), taken into tracking space
    // through the rig.
    //
    // "Where you stand" (settled) follows the neck slowly. A start has to end ahead of it and a stop behind it: coming back upright
    // after the lean that started the run is a quick move back, but only back to where you stood, so it doesn't stop the run (and
    // straightening after a lean back doesn't start it again). Held a while, a lean becomes where you stand.
    public static class StepSprint
    {
        public static bool enabled = true;          // dev kill-switch; the menu toggle is VRSettings.GetStepToSprint
        public static float moveTime = 0.2f;        // s a quick move is measured over...
        public static float startDistance = 0.08f;  // ...m the neck has to go forward in that time to start the run (0.4 m/s)
        public static float stopDistance = 0.08f;   // ...and back to stop it
        public static float aheadMargin = 0.04f;    // m ahead of where you stand a start has to end (behind, a stop)
        public static float settleTime = 2.5f;      // s "where you stand" takes to follow you (time constant): longer = a held lean stays a lean longer
        public static float sidewaysShare = 0.7f;   // sideways movement allowed, as a share of the forward / back
        public static float verticalShare = 1f;     // up / down allowed, as a share of it: crouching and standing up move the neck forward / back too
        public static float glitchSpeed = 3f;       // m/s faster than a body moves: a tracking jump or a recenter, history dropped
        public static float goGrace = 0.35f;        // s a step before the stick is pushed forward still starts the run
        public static float cooldown = 0.5f;        // s after a start / stop before another (the game's sprint state lags the command)
        // A start also needs the head looking roughly the way you'd run (his ask: leaning out to look down off a ledge mustn't start a
        // run). Stops don't care where you look.
        public static float maxLookDown = 35f;      // deg below level
        public static float maxLookUp = 45f;        // deg above level
        public static float maxLookSideways = 60f;  // deg off the way the character faces
        public static float pulseAmplitude = 0.3f;  // the buzz once the game has started the run, both controllers; a stop gets half
        public static float pulseDuration = 0.06f;
        public static float pulseFrequency = 90f;
        public static bool debugLog = true;         // a line per start / stop and per near miss while walking or running (round 1 tuning)

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

        // The biggest move of the one going on, for the near-miss line
        private static float candidateAlong;
        private static string candidateLine;
        private static bool candidateFired;

        // Once a frame from the sprint handler: +1 = start the run, -1 = stop it, 0 = nothing
        public static int Update(bool running, bool stickForward)
        {
            // Asked twice in a frame, the second gets nothing: a start handed out twice would toggle the run straight back off
            if (Time.frameCount == lastFrame)
                return 0;
            lastFrame = Time.frameCount;
            if (!enabled || !VRSettings.GetStepToSprint() || VRGlobals.player == null)
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

            // A gap (menus, a hitch) or a jump no body makes: the history doesn't describe this movement
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
                    // The grace runs from the end of the move, logged once
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
                    // Looked away (down off the ledge) between the lean and the stick
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

            // The move is over: its near miss, if it never got through
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

        // The game confirmed the run (or its end): the buzz
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

        // The headset's look (raw tracking space, as `forward` is) within maxLookDown / maxLookUp of level and maxLookSideways of the run
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

        // The way the character faces, in tracking space (the rig's local frame = the poses' frame); the head's yaw without a rig
        private static Vector3 TrackingForward(Quaternion headRot)
        {
            Vector3 forward;
            if (VRGlobals.vrOffsetter != null && VRGlobals.player != null)
                forward = Quaternion.Inverse(VRGlobals.vrOffsetter.transform.rotation) * VRGlobals.player.Transform.forward;
            else
            {
                forward = headRot * Vector3.forward;
                // Looking straight down the face points at the floor; the top of the head points forward
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
