using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The gun's own jolt from its clips as a part the hands work hits its stop (GunJolt), and how hard it got there
    public static partial class VRReloadController
    {
        public static float pushSpeedSmoothing = 0.03f;     // s the push speed a jolt is scaled by is smoothed over
        // s: a jolt takes the hardest push in this window before the stop. A magazine seats only once it has eased onto the track, by
        // when a fast push has already stopped.
        public static float pushPeakWindow = 0.25f;
        // s: the charging handle's let-go takes how far back it was in this window before (the trigger's let-go registers late, the
        // handle following the hand forward meanwhile)
        public static float handleLetGoWindow = 0.15f;
        public static float heldOnCatchJolt = 0.35f;        // of the slam's jolt when an empty magazine's follower stops the bolt on the catch
        public static float partPullSoundMin = 0.15f;       // of a full pull: a handle let go from part way back this far or more springs home (sound, jolt)

        // The charging handle let go: how far back it still was, 1 = all the way (the spring slams the bolt home), ridden forward first = soft
        private static float handleSlamStrength = 1f;
        private static float handlePullPeak;
        private static float handlePullPeakTime = -10f;

        // Every frame the handle is held (UpdateChargingHandle): the furthest back it's been in handleLetGoWindow
        private static void TrackHandlePull(float pull)
        {
            if (pull >= handlePullPeak || Time.time - handlePullPeakTime > handleLetGoWindow)
            {
                handlePullPeak = pull;
                handlePullPeakTime = Time.time;
            }
        }

        private static float HandlePullAtLetGo(float pull) => Time.time - handlePullPeakTime <= handleLetGoWindow ? Mathf.Max(pull, handlePullPeak) : pull;

        private static void Jolt(FirearmController firearmController, GunJolt.Kind kind, float strength, int magazineType = 0)
        {
            if (firearmController != null && firearmController == VRGlobals.firearmController && firearmController.Item != null)
                GunJolt.Play(firearmController, AnimationFamily(firearmController), kind, magazineType, strength);
        }

        // How fast a part is pushed home: its distance from home (m) each frame -> m/s toward it
        private sealed class PushSpeed
        {
            private float speed;
            private float lastDistance;
            private int lastFrame = -10;
            private float peak;
            private float peakTime = -10f;

            // The hardest push in pushPeakWindow (0 when it wasn't tracked this frame or the last)
            public float Peak
            {
                get
                {
                    if (Time.frameCount - lastFrame > 1)
                        return 0f;
                    return Time.time - peakTime <= pushPeakWindow ? Mathf.Max(peak, speed) : speed;
                }
            }

            public void Track(float distanceFromHome)
            {
                if (lastFrame == Time.frameCount)
                    return;
                float deltaTime = Time.deltaTime;
                bool going = lastFrame == Time.frameCount - 1 && deltaTime > 1e-4f;
                float now = going ? Mathf.Max(0f, (lastDistance - distanceFromHome) / deltaTime) : 0f;
                speed = going ? Mathf.Lerp(speed, now, 1f - Mathf.Exp(-deltaTime / Mathf.Max(pushSpeedSmoothing, 1e-3f))) : 0f;
                if (speed >= peak || Time.time - peakTime > pushPeakWindow)
                {
                    peak = speed;
                    peakTime = Time.time;
                }
                lastDistance = distanceFromHome;
                lastFrame = Time.frameCount;
            }
        }

        private static readonly PushSpeed magazinePush = new PushSpeed();
        private static readonly PushSpeed boltPush = new PushSpeed();
        private static readonly PushSpeed pumpPush = new PushSpeed();
    }
}
