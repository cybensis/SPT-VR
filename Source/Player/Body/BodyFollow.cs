using TarkovVR.Source.Controls;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // Keeps the body under your head. Tarkov's animation decides where the body's head sits over its feet, and that changes with the
    // stance, walking, crouching, the spine pitch (half your head's, MovementPatches.GetBodyRotation) and the 28 deg stance turn. So the
    // body's own eyes (UpperBodySolver.EyesOf: the head bone + the eye offset) are measured every frame and kept under your real eyes.
    // Inside a lean room around that point the body stays put (lean, peek, duck); past it the body follows only by the overshoot. The
    // room is measured along your look, shrinks while the movement stick is pushed, and grows back once the body settles after a stop.
    // MoveBody moves the rig back by the same amount in the same frame, so your view and hands don't move.
    // Stepped once per frame (frame-guarded) from Update, before the animator, the IK and the camera pin, by IKManager.MatchLegsToArms.
    internal static class BodyFollow
    {
        // Smoothing of the measured offset of the body's eyes over its root: filters out the walk's bob and breathing, follows stance changes
        public static float stanceSmoothTime = 0.25f;   // s
        public static float maxStanceOffset = 0.6f;     // m, a measurement further off the root than this is skipped (bad bone read)
        public static float bodyBack = 0f;              // m, extra distance the body stands behind its eyes (negative = forward)
        // m, how far your eyes may lean off the body's before it follows: forward, back and sideways, relative to your look direction
        public static float leanRoomForward = 0.20f;
        public static float leanRoomBack = 0.20f;
        public static float leanRoomSide = 0.40f;
        public static float movingRoomScale = 0.1f;     // lean room multiplier while the movement stick is pushed
        public static float roomBlendTime = 0.3f;       // s, time for the room to shrink / grow back
        public static float stopSettleTime = 0.3f;      // s after stopping before the room grows back (the body settles under you first)
        public static float stickDeadZone = 0.15f;      // movement stick deflection above which you count as walking
        public static float followTime = 0.08f;         // s, time for the body to take up the overshoot (0 = at once, as shaky as your head)
        public static bool blockedByWalls = false;      // sweep the body's capsule along each move: a wall stops the body, your head goes on
        public static float wallSkin = 0.02f;           // m kept off a wall
        public static bool debugLog = false;            // logs a [BodyFollow] line per second: measured stance, your eye offset, the room

        private static int steppedFrame = -1;
        private static EFT.Player stateFor;
        private static bool haveStance;
        private static Vector2 stance;                  // the body's eyes over its root, in its yaw frame: x right, y forward
        private static Vector2 lastMeasured;
        private static float roomScale = 1f;
        private static float standing = 1f;             // 0 walking .. 1 stopped and settled
        private static float nextLog;
        private static int sweepMaskLayer = -1;
        private static int sweepMask;
        private static readonly RaycastHit[] sweepHits = new RaycastHit[16];

        internal static void Step(Transform body)
        {
            if (steppedFrame == Time.frameCount)
                return;
            steppedFrame = Time.frameCount;
            EFT.Player player = VRGlobals.player;
            Camera headset = VRGlobals.VRCam;
            if (player == null || headset == null || body == null || VRGlobals.vrPlayer == null || VRGlobals.vrOffsetter == null
                || VRGlobals.vrOffsetter.transform.parent == null || VRGlobals.camRoot == null || player.PlayerBones == null || player.PlayerBones.Head == null)
                return;
            if (!ReferenceEquals(player, stateFor))
            {
                stateFor = player;
                haveStance = false;
                roomScale = 1f;
                standing = 1f;
            }
            if (player.HealthController == null || !player.HealthController.IsAlive)
                return;

            float deltaTime = Time.deltaTime;
            Vector3 bodyForward = Flat(body.forward);
            if (bodyForward.sqrMagnitude < 1e-4f)
                return;
            Quaternion bodyYaw = Quaternion.LookRotation(bodyForward.normalized, Vector3.up);

            // Measure where the animation puts the body's eyes relative to its root. This reads last frame's pose, because the bones are
            // posed after Update. Skipped while prone: lying down, the head is about a metre ahead of the root.
            Transform headBone = player.PlayerBones.Head.Original;
            bool prone = player.MovementContext != null && player.MovementContext.IsInPronePose;
            if (headBone != null && !prone)
            {
                Vector3 measured = Quaternion.Inverse(bodyYaw) * (UpperBodySolver.EyesOf(headBone) - body.position);
                Vector2 measuredFlat = new Vector2(measured.x, measured.z);
                if (measuredFlat.sqrMagnitude <= maxStanceOffset * maxStanceOffset)
                {
                    lastMeasured = measuredFlat;
                    stance = haveStance && stanceSmoothTime > 0f
                        ? Vector2.Lerp(stance, measuredFlat, 1f - Mathf.Exp(-deltaTime / stanceSmoothTime)) : measuredFlat;
                    haveStance = true;
                }
            }
            if (!haveStance)
                return;

            bool walking = VRInput.LeftJoystick.axis.sqrMagnitude > stickDeadZone * stickDeadZone;
            standing = Mathf.MoveTowards(standing, walking ? 0f : 1f, deltaTime / Mathf.Max(walking ? roomBlendTime : stopSettleTime, 0.01f));
            // The room stays small until the body has settled after a stop. Grown at once, it would leave the body where the walk left it.
            float roomWanted = walking || standing < 1f ? movingRoomScale : 1f;
            roomScale = Mathf.MoveTowards(roomScale, roomWanted, deltaTime / Mathf.Max(roomBlendTime, 0.01f));

            Transform head = headset.transform;
            Vector3 bodyEyes = body.position + bodyYaw * new Vector3(stance.x, 0f, stance.y - bodyBack);
            Vector3 offset = Flat(head.position - bodyEyes);

            // Measured along your look direction; looking straight down, the top of the head points that way instead
            Vector3 lookForward = Flat(head.forward);
            if (lookForward.sqrMagnitude < 0.01f)
                lookForward = Flat(head.up);
            if (lookForward.sqrMagnitude < 1e-4f)
                lookForward = bodyForward;
            lookForward.Normalize();
            Vector3 lookRight = Vector3.Cross(Vector3.up, lookForward);
            float along = Vector3.Dot(offset, lookForward);
            float across = Vector3.Dot(offset, lookRight);
            float roomAlong = Mathf.Max(0.01f, (along >= 0f ? leanRoomForward : leanRoomBack) * roomScale);
            float roomAcross = Mathf.Max(0.01f, leanRoomSide * roomScale);
            // How far outside the room (1 = on its edge). The overshoot is the part of the offset past the edge, along the same line.
            float outside = Mathf.Sqrt(along * along / (roomAlong * roomAlong) + across * across / (roomAcross * roomAcross));
            Vector3 overshoot = outside > 1f ? offset * (1f - 1f / outside) : Vector3.zero;
            Vector3 move = followTime > 0f ? overshoot * (1f - Mathf.Exp(-deltaTime / followTime)) : overshoot;
            if (blockedByWalls && move.sqrMagnitude > 1e-10f)
                move = SweptMove(player, move);

            if (debugLog && Time.time >= nextLog)
            {
                nextLog = Time.time + 1f;
                Plugin.MyLog.LogInfo($"[BodyFollow] body's eyes {stance.y * 100f:0} cm ahead of its root, {stance.x * 100f:0} right (this frame {lastMeasured.y * 100f:0} / {lastMeasured.x * 100f:0}"
                    + $"{(prone ? ", held prone" : "")}); your eyes off them {along * 100f:0} cm along the look, {across * 100f:0} across; room {roomAlong * 100f:0} / {roomAcross * 100f:0}"
                    + $"{(walking ? " (walking)" : standing < 1f ? " (settling)" : "")}; overshoot {overshoot.magnitude * 100f:0.0} cm");
            }
            if (move.sqrMagnitude < 1e-10f)
                return;
            MoveBody(body, move);
        }

        private static Vector3 Flat(Vector3 vector) => new Vector3(vector.x, 0f, vector.z);

        // Moves the body, and the rig the opposite way at once (not at the next camRoot pin / offsetter update), so your head and hands
        // stay where they are in the world this frame, whichever runs first
        private static void MoveBody(Transform body, Vector3 move)
        {
            body.position += move;
            Transform offsetter = VRGlobals.vrOffsetter.transform;
            Vector3 localMove = offsetter.parent.InverseTransformVector(move);
            localMove.y = 0f;
            VRGlobals.vrPlayer.initPos += localMove;
            VRGlobals.camRoot.transform.position += move;
            offsetter.localPosition -= localMove;
        }

        // How far the body's capsule gets along the move before a wall. The capsule is raised by the step height, so floors, steps and
        // kerbs don't stop it. The layers are the ones the character controller collides with.
        private static Vector3 SweptMove(EFT.Player player, Vector3 move)
        {
            ICharacterController controller = player.CharacterController;
            Collider capsule = controller != null ? controller.GetCollider() : null;
            if (capsule == null)
                return move;
            if (sweepMaskLayer != capsule.gameObject.layer)
            {
                sweepMaskLayer = capsule.gameObject.layer;
                sweepMask = 0;
                for (int layer = 0; layer < 32; layer++)
                {
                    if (!Physics.GetIgnoreLayerCollision(sweepMaskLayer, layer))
                        sweepMask |= 1 << layer;
                }
            }
            Transform capsuleTransform = capsule.transform;
            float radius = controller.radius;
            float half = Mathf.Max(0f, controller.height * 0.5f - radius);
            Vector3 top = capsuleTransform.TransformPoint(controller.center + Vector3.up * half);
            Vector3 bottom = capsuleTransform.TransformPoint(controller.center - Vector3.up * half) + Vector3.up * controller.stepOffset;
            if (bottom.y > top.y)
                bottom = top;
            float distance = move.magnitude;
            Vector3 direction = move / distance;
            int count = Physics.CapsuleCastNonAlloc(bottom, top, radius * 0.95f, direction, sweepHits, distance + wallSkin, sweepMask, QueryTriggerInteraction.Ignore);
            float allowed = distance;
            Transform own = player.Transform != null ? player.Transform.Original : null;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = sweepHits[i];
                // Distance 0 means already touching at the start, which doesn't block this move
                if (hit.distance <= 0f || hit.collider == null || (own != null && hit.collider.transform.IsChildOf(own)))
                    continue;
                allowed = Mathf.Min(allowed, Mathf.Max(0f, hit.distance - wallSkin));
            }
            return direction * allowed;
        }
    }
}
