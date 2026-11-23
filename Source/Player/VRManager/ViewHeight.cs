using TarkovVR.Source.Controls;
using TarkovVR.Source.Dev;
using TarkovVR.Source.Player.Body;
using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    // Your standing view height = the model's own eyes. camRoot is pinned RIG_HEIGHT above the body's root and the height reset maps
    // the headset to camRoot + headOffset.y: a fixed 1.675 m, whatever the model's stance. Tarkov's standing pose holds the head lower
    // (knees bent, spine and head tipped toward the gun), so the view sat above the model's eyes and the hands, held against the view,
    // came out that much high on the body; on the other players' screens the solve can bend the spine to your head but not stretch it.
    // The model's eyes (UpperBodySolver.EyesOf) are measured standing still, upright and looking level for sampleTime, and that height is
    // taken as the view's: once per player (raid / hideout) and again after each height reset, eased in. Never followed live: the view
    // must not drift on its own.
    internal static class ViewHeight
    {
        [Tunable("Body", TunableKind.Switch, "Your standing view height is the model's own eye height, measured standing still after each height reset. "
            + "Off = the fixed 1.675 m.")]
        public static bool matchModelEyes = true;
        [Tunable("Body", TunableKind.Tune, "How long you stand still, upright and looking level before the model's eye height is taken.", Unit = "s")]
        public static float sampleTime = 1f;
        [Tunable("Body", TunableKind.Tune, "How fast the view moves to a newly measured height.", Unit = "m/s")]
        public static float easeSpeed = 0.2f;
        public static float maxLookPitch = 15f;     // deg the headset may look up or down while measuring (the spine bends with the look)
        public static float stickDeadZone = 0.15f;  // the movement stick past this = moving, no measurement
        public static float minEyeHeight = 1.3f;    // m; a measurement outside these is a bad bone read
        public static float maxEyeHeight = 1.9f;
        [Tunable("Body", TunableKind.Log, "A [ViewHeight] line each time the model's eye height is taken: the height, the fixed view height and the difference.")]
        public static bool debugLog = false;

        private const float RIG_HEIGHT = 1.5f;      // camRoot above the body's root (every camRoot pin)

        private static EFT.Player measuredFor;
        private static bool measuring = true;
        private static bool haveHeight;             // kept across players: the same skeleton, the next measure refines it
        private static float modelEyeHeight;
        private static float steadyTime;
        private static float sampleSum;
        private static int sampleCount;
        private static float shownOffset = float.NaN;
        private static int easedFrame = -1;

        // After a height reset: the next steady stand measures again (the view keeps the current height until then)
        internal static void Remeasure()
        {
            measuring = true;
            ClearSamples();
        }

        // headOffset with its height eased to the model's eyes over camRoot (fixedOffset.y until measured, or with matchModelEyes off)
        internal static Vector3 HeadOffset(Vector3 fixedOffset)
        {
            float target = matchModelEyes && haveHeight ? modelEyeHeight - RIG_HEIGHT : fixedOffset.y;
            if (float.IsNaN(shownOffset))
                shownOffset = target;
            else if (easedFrame != Time.frameCount)
                shownOffset = Mathf.MoveTowards(shownOffset, target, easeSpeed * Time.deltaTime);
            easedFrame = Time.frameCount;
            return new Vector3(fixedOffset.x, shownOffset, fixedOffset.z);
        }

        // Each frame, before the rig's offset is written. The head bone is last frame's final pose (Update).
        internal static void Sample(EFT.Player player, Camera headset, Vector3 fixedOffset)
        {
            if (player == null)
                return;
            if (!ReferenceEquals(player, measuredFor))
            {
                measuredFor = player;
                Remeasure();
            }
            if (!measuring)
                return;

            Transform headBone = player.PlayerBones != null && player.PlayerBones.Head != null ? player.PlayerBones.Head.Original : null;
            Transform root = player.Transform != null ? player.Transform.Original : null;
            if (headBone == null || root == null || headset == null || !StandingStill(player, headset))
            {
                ClearSamples();
                return;
            }
            float eyeHeight = UpperBodySolver.EyesOf(headBone).y - root.position.y;
            if (eyeHeight < minEyeHeight || eyeHeight > maxEyeHeight)
            {
                ClearSamples();
                return;
            }
            sampleSum += eyeHeight;
            sampleCount++;
            steadyTime += Time.deltaTime;
            if (steadyTime < sampleTime)
                return;

            modelEyeHeight = sampleSum / sampleCount;
            haveHeight = true;
            measuring = false;
            if (debugLog)
            {
                float fixedHeight = RIG_HEIGHT + fixedOffset.y;
                Plugin.MyLog.LogInfo($"[ViewHeight] the model's eyes are {modelEyeHeight * 100f:F1} cm above its feet standing still; the fixed view height is "
                    + $"{fixedHeight * 100f:F1} cm ({(fixedHeight - modelEyeHeight) * 100f:+0.0;-0.0} cm off); your view right now "
                    + $"{(headset.transform.position.y - root.position.y) * 100f:F1} cm; {(matchModelEyes ? "the view now uses the model's" : "matchModelEyes off: the fixed height kept")}");
            }
        }

        private static bool StandingStill(EFT.Player player, Camera headset)
        {
            var movement = player.MovementContext;
            if (VRGlobals.menuOpen || movement == null || player.HealthController == null || !player.HealthController.IsAlive)
                return false;
            if (movement.PoseLevel < 0.99f || movement.IsInPronePose || !movement.IsGrounded || player.IsSprintEnabled
                || movement.StationaryWeapon != null || Interactions.MountedGun.Active)
                return false;
            if (player.HandsController != null && player.HandsController.IsAiming)
                return false;
            if (VRInput.LeftJoystick.axis.sqrMagnitude > stickDeadZone * stickDeadZone)
                return false;
            float pitch = Mathf.Asin(Mathf.Clamp(headset.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            return Mathf.Abs(pitch) <= maxLookPitch;
        }

        private static void ClearSamples()
        {
            steadyTime = 0f;
            sampleSum = 0f;
            sampleCount = 0;
        }
    }
}
