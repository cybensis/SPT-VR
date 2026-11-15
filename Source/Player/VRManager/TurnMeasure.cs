using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    // Measurement only (changes nothing): where the held gun, the hand it's placed on (rawRightHand) and the view are at four points of
    // a frame - the gun put on the hand (HandsPositioner.Update), before Tarkov's arm IK (Player.IkProcess), after it (Player.IkApply),
    // and as the frame is drawn (the VR camera's first OnPreCull) - and which of camRoot, the camera and the body turned or moved between
    // them. One line a frame while the turn stick is pushed or the stance is changing (and afterTime after), else one a second.
    public static class TurnMeasure
    {
        public static bool enabled = true;
        public static float stickThreshold = 0.15f;
        public static float afterTime = 0.3f;       // s of per-frame lines after the turn / stance change stops
        public static float idleLogInterval = 1f;   // s between lines otherwise

        private struct Sample
        {
            public bool valid;
            public float camRootYaw, cameraYaw, bodyYaw;
            public Vector3 bodyPosition, cameraPosition;
            public float gunYawInHand, gunPitchInHand;   // the barrel in the hand target's frame, deg
            public Vector3 gripInHand;                   // the grip marker in the hand target's frame, m
            public float handYawInView, gunYawInView;    // deg, in the camera's frame
            public float targetOffController;            // deg: the hand target's yaw off the controller's rig hand (the weight / aim filters)
            public Vector3 handInView, gripInView;       // m, in the camera's frame
            public Vector3 palmInHand;                   // the rendered wrist (arm IK bone3) in the hand target's frame, m
        }

        private static Sample placed, beforeIk, afterIk, drawn, lastDrawn;
        private static int placedFrame = -1, beforeIkFrame = -1, afterIkFrame = -1, drawnFrame = -1;
        private static float activeUntil = -1f;
        private static float nextIdleLog;
        private static float lastPoseLevel = -1f;
        private static float lastDrawnTime;
        private static bool hooked;

        public static void AtPlacement(Transform positioner)
        {
            if (!enabled || VRGlobals.weaponHolder == null || VRGlobals.weaponHolder.transform.parent != positioner)
                return;
            Hook();
            if (Take(ref placed))
                placedFrame = Time.frameCount;
        }

        public static void BeforeIk()
        {
            if (enabled && Take(ref beforeIk))
                beforeIkFrame = Time.frameCount;
        }

        public static void AfterIk()
        {
            if (enabled && Take(ref afterIk))
                afterIkFrame = Time.frameCount;
        }

        private static void Hook()
        {
            if (hooked)
                return;
            hooked = true;
            Camera.onPreCull += OnPreCull;
        }

        private static void OnPreCull(Camera camera)
        {
            if (!enabled || VRGlobals.VRCam == null || camera != VRGlobals.VRCam || drawnFrame == Time.frameCount)
                return;
            if (!Take(ref drawn))
                return;
            drawnFrame = Time.frameCount;
            try { Report(); }
            catch (System.Exception exception) { Plugin.MyLog.LogError($"[TurnMeasure] {exception}"); enabled = false; }
            lastDrawn = drawn;
            lastDrawnTime = Time.time;
        }

        private static bool Take(ref Sample sample)
        {
            sample.valid = false;
            EFT.Player player = VRGlobals.player;
            if (player == null || !player.IsYourPlayer || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.rawRightHand == null || VRGlobals.VRCam == null
                || VRGlobals.camRoot == null || VRGlobals.firearmController == null || VRGlobals.firearmController.WeaponRoot == null
                || player._markers == null || player._markers.Length < 2 || player._markers[1] == null)
                return false;
            Transform hand = VRGlobals.vrPlayer.rawRightHand.transform;
            Transform camera = VRGlobals.VRCam.transform;
            Transform weaponRoot = VRGlobals.firearmController.WeaponRoot;
            Vector3 barrel = -weaponRoot.up;
            Vector3 grip = player._markers[1].transform.position;
            Quaternion toHand = Quaternion.Inverse(hand.rotation);
            Quaternion toView = Quaternion.Inverse(camera.rotation);
            sample.camRootYaw = Yaw(VRGlobals.camRoot.transform.forward);
            sample.cameraYaw = Yaw(camera.forward);
            sample.bodyYaw = Yaw(player.Transform.rotation * Vector3.forward);
            sample.bodyPosition = player.Transform.position;
            sample.cameraPosition = camera.position;
            Vector3 barrelInHand = toHand * barrel;
            sample.gunYawInHand = Yaw(barrelInHand);
            sample.gunPitchInHand = Mathf.Asin(Mathf.Clamp(barrelInHand.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
            sample.gripInHand = toHand * (grip - hand.position);
            sample.handYawInView = Yaw(toView * hand.forward);
            sample.targetOffController = VRGlobals.vrPlayer.RightHand != null ? Mathf.DeltaAngle(Yaw(VRGlobals.vrPlayer.RightHand.transform.forward), Yaw(hand.forward)) : 0f;
            sample.gunYawInView = Yaw(toView * barrel);
            sample.handInView = toView * (hand.position - camera.position);
            sample.gripInView = toView * (grip - camera.position);
            Transform palm = VRGlobals.ikManager != null && VRGlobals.ikManager.rightArmIk != null && VRGlobals.ikManager.rightArmIk.solver != null
                && VRGlobals.ikManager.rightArmIk.solver.bone3 != null ? VRGlobals.ikManager.rightArmIk.solver.bone3.transform : null;
            sample.palmInHand = palm != null ? toHand * (palm.position - hand.position) : Vector3.zero;
            sample.valid = true;
            return true;
        }

        private static void Report()
        {
            EFT.Player player = VRGlobals.player;
            float stick = Controls.VRInput.RightJoystick.axis.x;
            float poseLevel = player.MovementContext != null ? player.MovementContext.SmoothedPoseLevel : 0f;
            bool stanceChanging = lastPoseLevel >= 0f && Mathf.Abs(poseLevel - lastPoseLevel) > 1e-4f;
            lastPoseLevel = poseLevel;
            if (Mathf.Abs(stick) > stickThreshold || stanceChanging)
                activeUntil = Time.time + afterTime;
            bool perFrame = Time.time < activeUntil;
            if (!perFrame && Time.time < nextIdleLog)
                return;
            nextIdleLog = Time.time + idleLogInterval;
            if (!lastDrawn.valid)
                return;

            bool havePlaced = placedFrame == Time.frameCount && placed.valid;
            bool haveBeforeIk = beforeIkFrame == Time.frameCount && beforeIk.valid;
            bool haveAfterIk = afterIkFrame == Time.frameCount && afterIk.valid;
            float dt = Mathf.Max(Time.time - lastDrawnTime, 1e-4f);
            var line = new System.Text.StringBuilder();
            line.Append($"[TurnMeasure] f{Time.frameCount} {dt * 1000f:0.0} ms, stick {stick:+0.00;-0.00}, body {Flat(drawn.bodyPosition - lastDrawn.bodyPosition).magnitude / dt:0.00} m/s, pose {poseLevel:0.000}{(perFrame ? "" : " (idle)")}");
            // Each step: last drawn -> placed -> before IK -> after IK -> drawn. Turns in deg (+ = right), moves in cm.
            line.Append(" | camRoot turned");
            Steps(line, havePlaced, haveBeforeIk, haveAfterIk, s => s.camRootYaw);
            line.Append(" | camera turned");
            Steps(line, havePlaced, haveBeforeIk, haveAfterIk, s => s.cameraYaw);
            line.Append(" | body turned");
            Steps(line, havePlaced, haveBeforeIk, haveAfterIk, s => s.bodyYaw);
            line.Append($" | body moved {Flat(drawn.bodyPosition - lastDrawn.bodyPosition).magnitude * 100f:0.0} cm, camera {Flat(drawn.cameraPosition - lastDrawn.cameraPosition).magnitude * 100f:0.0}");
            // The gun on the hand target: off what it was when placed (0 = it stayed on the hand)
            if (havePlaced)
            {
                line.Append($" | gun vs hand, placed yaw {placed.gunYawInHand:+0.00;-0.00} pitch {placed.gunPitchInHand:+0.00;-0.00}; since:");
                if (haveBeforeIk) line.Append($" before IK {GunOff(beforeIk)}");
                if (haveAfterIk) line.Append($", after IK {GunOff(afterIk)}");
                line.Append($", drawn {GunOff(drawn)}");
            }
            else
            {
                line.Append(" | (no placement this frame)");
            }
            // In the view: the hand target and the gun, and how they changed since the last frame
            line.Append($" | in view: hand yaw {drawn.handYawInView:+0.00;-0.00} (frame {Mathf.DeltaAngle(lastDrawn.handYawInView, drawn.handYawInView):+0.00;-0.00}),"
                + $" gun yaw {drawn.gunYawInView:+0.00;-0.00} (frame {Mathf.DeltaAngle(lastDrawn.gunYawInView, drawn.gunYawInView):+0.00;-0.00}),"
                + $" hand moved {(drawn.handInView - lastDrawn.handInView).magnitude * 100f:0.00} cm, grip moved {(drawn.gripInView - lastDrawn.gripInView).magnitude * 100f:0.00} cm");
            line.Append($" | hand target off the controller yaw {drawn.targetOffController:+0.00;-0.00}");
            line.Append($" | rendered wrist off the hand target {drawn.palmInHand.magnitude * 100f:0.0} cm"
                + (haveAfterIk ? $" (after IK {afterIk.palmInHand.magnitude * 100f:0.0})" : ""));
            Plugin.MyLog.LogInfo(line.ToString());
        }

        private static void Steps(System.Text.StringBuilder line, bool havePlaced, bool haveBeforeIk, bool haveAfterIk, System.Func<Sample, float> yaw)
        {
            float from = yaw(lastDrawn);
            if (havePlaced) { line.Append($" before placing {Mathf.DeltaAngle(from, yaw(placed)):+0.00;-0.00}"); from = yaw(placed); }
            if (haveBeforeIk) { line.Append($", to IK {Mathf.DeltaAngle(from, yaw(beforeIk)):+0.00;-0.00}"); from = yaw(beforeIk); }
            if (haveAfterIk) { line.Append($", in IK {Mathf.DeltaAngle(from, yaw(afterIk)):+0.00;-0.00}"); from = yaw(afterIk); }
            line.Append($", to drawn {Mathf.DeltaAngle(from, yaw(drawn)):+0.00;-0.00}");
        }

        private static string GunOff(Sample sample)
        {
            return $"{Mathf.DeltaAngle(placed.gunYawInHand, sample.gunYawInHand):+0.00;-0.00} deg / {(sample.gripInHand - placed.gripInHand).magnitude * 100f:0.00} cm";
        }

        private static Vector3 Flat(Vector3 vector) => new Vector3(vector.x, 0f, vector.z);

        private static float Yaw(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
    }
}
