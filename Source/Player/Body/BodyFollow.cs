using TarkovVR.Source.Controls;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // Your body under your head. Tarkov's animation decides where the body's head is over its feet: forward over the gun in the rifle
    // stance, further forward walking, lower and forward crouched, forward again as the spine bends with half your head's pitch
    // (MovementPatches.GetBodyRotation), turned with the body's 28 deg stance offset. Vanilla's view is that head's, so the legs always
    // sit right under it. Here the body's own eyes (UpperBodySolver.EyesOf: the head bone + the eye offset) are MEASURED every frame and
    // kept under your real eyes: the legs are where the animation has them under that head, in every stance - no distances guessed.
    // Around that point, lean room: inside it the body stays (lean out, peek, duck), past it the body takes only the overshoot, so your
    // head is never further off the body's than the room. The room is measured along where you look, shrinks while the movement stick
    // is pushed (the body walks under you) and, after you stop, grows back once the body has settled. The rig moves the other way by
    // the same amount in the same frame (MoveBody): your view and hands never move for it. One step a frame, from Update (before the
    // animator, the IK and the camera pin): IKManager.MatchLegsToArms, whose callers in the other hands states are no-ops after it.
    internal static class BodyFollow
    {
        // The measured offset of the body's eyes over its root, smoothed (the walk's bob and the breathing out, stance changes in)
        public static float stanceSmoothTime = 0.25f;   // s
        public static float maxStanceOffset = 0.6f;     // m: a measure further off the root than this is skipped (a bad bone read)
        public static float bodyBack = 0f;              // m the body stands further back than its eyes put it (- = forward)
        // How far your eyes may lean off the body's before it follows: forward, back, sideways, along where you look
        public static float leanRoomForward = 0.20f;
        public static float leanRoomBack = 0.20f;
        public static float leanRoomSide = 0.40f;
        public static float movingRoomScale = 0.1f;     // the room while the movement stick is pushed, of the above
        public static float roomBlendTime = 0.3f;       // s the room takes to shrink / grow back
        public static float stopSettleTime = 0.3f;      // s after you stop before the room grows back (the body settles under you first)
        public static float stickDeadZone = 0.15f;      // the movement stick past this = walking
        public static float followTime = 0.08f;         // s the body takes to take up the overshoot (0 = at once: as shaky as your head)
        public static bool blockedByWalls = false;      // the body's capsule swept along each move: a wall stops it, your head goes on
        public static float wallSkin = 0.02f;           // m kept off a wall
        public static bool debugLog = true;             // a [BodyFollow] line a second: the measured stance, your eyes off it, the room

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

            // Where the animation has the body's eyes over its root (last frame's pose: the bones are posed after Update). Held while
            // prone: lying down the head is a metre ahead, and putting it under yours would drag the body that far back.
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
            // (the room stays small until the body has settled after a stop: grown at once, it would leave the body where the walk had it)
            float roomWanted = walking || standing < 1f ? movingRoomScale : 1f;
            roomScale = Mathf.MoveTowards(roomScale, roomWanted, deltaTime / Mathf.Max(roomBlendTime, 0.01f));

            Transform head = headset.transform;
            Vector3 bodyEyes = body.position + bodyYaw * new Vector3(stance.x, 0f, stance.y - bodyBack);
            Vector3 offset = Flat(head.position - bodyEyes);

            // Along where you look (straight down, the top of the head points that way)
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
            // How far out of the room, 1 = on its edge: the overshoot is what's past the edge along the same line
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

        // The body moves, the rig the other way at once (not at the next camRoot pin / offsetter update): your head and hands stay where
        // they are in the world this frame, whichever runs first
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

        // How far the body's capsule gets along the move before a wall: raised by the step height, so floors, steps and kerbs don't stop
        // it. The layers are the ones the character controller collides with.
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
                // (distance 0 = already touching at the start: not in the way of this move)
                if (hit.distance <= 0f || hit.collider == null || (own != null && hit.collider.transform.IsChildOf(own)))
                    continue;
                allowed = Mathf.Min(allowed, Mathf.Max(0f, hit.distance - wallSkin));
            }
            return direction * allowed;
        }
    }
}
