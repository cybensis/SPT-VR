using EFT;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    // The gun sways while you sprint (his ask: "a sort of running movement to my hands/gun while running so that it's not so easy to
    // aim"). Only while your arms don't play Tarkov's run (VR tab "Disable Run Animation" on) - the case where firing while sprinting
    // is allowed (SprintFirePatches); with the run animation on, Tarkov animates the arms and firing stays blocked.
    // A rotation about the grip, laid on the gun's pose at its last stage (HandsPositioner), so the aim filters, the gunstock and the
    // weight spring never see it, and the gun stays in the hand. The shot follows the drawn gun (the fireport rides WeaponRoot), the
    // scope shows it, co-op players see it (the synced WeaponRoot pose). The view never moves.
    // Gait: the nose dips with each footfall (the step rate), swings side to side and rolls with each stride (half of it), plus an
    // irregular drift so it can't be timed; all scaled by how fast you run.
    internal static class RunSway
    {
        public static bool enabled = true;
        public static float strength = 1f;              // x every amplitude below
        public static float pitchDegrees = 2f;          // nose up / down, each step
        public static float yawDegrees = 2.5f;          // side to side, each stride
        public static float rollDegrees = 3f;           // rolling, each stride
        public static float driftShare = 0.35f;         // part of each axis that's irregular drift instead of the gait
        public static float driftFrequency = 0.9f;      // Hz of the drift
        public static float slowStepFrequency = 2.2f;   // Hz: steps per second at a slow run...
        public static float fastStepFrequency = 2.9f;   // ...and at fullSwaySpeed
        public static float fullSwaySpeed = 5.5f;       // m/s across the ground for the full sway and the fast step rate
        public static float twoHandedScale = 0.8f;      // the support hand on the gun steadies it
        public static float fadeInTime = 0.3f;          // s to the full sway once you sprint
        public static float fadeOutTime = 0.25f;        // s back to none when you stop

        private static float blend;
        private static float phase;                     // radians, 0..4 pi = one stride (two steps)
        private static float groundSpeed;
        private static Vector3 lastPosition;
        private static bool havePosition;
        private static int lastFrame = -1;
        private static Quaternion lastOffset = Quaternion.identity;

        // The sway to lay on the gun's rotation this frame (world space, left-multiplied); identity when not sprinting
        internal static Quaternion Offset(Quaternion gunRotation)
        {
            if (lastFrame == Time.frameCount)
                return lastOffset;
            lastFrame = Time.frameCount;
            lastOffset = Quaternion.identity;

            EFT.Player player = VRGlobals.player;
            float deltaTime = Time.deltaTime;
            UpdateGroundSpeed(player, deltaTime);
            bool sprinting = enabled && player != null && player.IsSprintEnabled && player.MovementContext.IsGrounded
                && VRSettings.GetDisableRunAnim() && player.HandsController is EFT.Player.FirearmController && !ChestHang.Detached;
            float speedShare = Mathf.Clamp01(groundSpeed / Mathf.Max(0.1f, fullSwaySpeed));
            float target = sprinting ? speedShare : 0f;
            float fadeTime = target > blend ? fadeInTime : fadeOutTime;
            blend = fadeTime > 0f ? Mathf.MoveTowards(blend, target, deltaTime / fadeTime) : target;
            if (blend <= 0.0001f)
                return lastOffset;

            phase += 2f * Mathf.PI * Mathf.Lerp(slowStepFrequency, fastStepFrequency, speedShare) * deltaTime;
            if (phase > 4f * Mathf.PI)
                phase -= 4f * Mathf.PI;
            float stepWave = Mathf.Sin(phase);
            float strideWave = Mathf.Sin(phase * 0.5f);
            float strideWaveLate = Mathf.Cos(phase * 0.5f);
            float driftTime = Time.time * driftFrequency;
            float gaitShare = 1f - driftShare;

            float pitch = pitchDegrees * (stepWave * gaitShare + Drift(driftTime, 11.3f) * driftShare);
            float yaw = yawDegrees * (strideWaveLate * gaitShare + Drift(driftTime, 47.9f) * driftShare);
            float roll = rollDegrees * (strideWave * gaitShare + Drift(driftTime, 83.1f) * driftShare);
            bool twoHanded = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting;
            float scale = strength * blend * (twoHanded ? twoHandedScale : 1f);

            // The gun's own axes: the barrel is the hand frame's -right (as the iron-sight aim reads it)
            Vector3 barrel = gunRotation * Vector3.left;
            Vector3 pitchAxis = Vector3.Cross(Vector3.up, barrel);
            if (pitchAxis.sqrMagnitude < 1e-4f)
                pitchAxis = gunRotation * Vector3.up;
            lastOffset = Quaternion.AngleAxis(yaw * scale, Vector3.up)
                * Quaternion.AngleAxis(pitch * scale, pitchAxis.normalized)
                * Quaternion.AngleAxis(roll * scale, barrel);
            return lastOffset;
        }

        // -1..1, smooth, irregular
        private static float Drift(float time, float seed)
        {
            return Mathf.PerlinNoise(time, seed) * 2f - 1f;
        }

        // Across-the-ground speed from the character's position, smoothed (a teleport or a respawn is ignored)
        private static void UpdateGroundSpeed(EFT.Player player, float deltaTime)
        {
            if (player == null || deltaTime <= 0f)
            {
                havePosition = false;
                groundSpeed = 0f;
                return;
            }
            Vector3 position = player.Position;
            if (havePosition)
            {
                Vector3 moved = position - lastPosition;
                moved.y = 0f;
                if (moved.sqrMagnitude < 4f)
                    groundSpeed = Mathf.Lerp(groundSpeed, moved.magnitude / deltaTime, 1f - Mathf.Exp(-deltaTime / 0.15f));
            }
            lastPosition = position;
            havePosition = true;
        }
    }
}
