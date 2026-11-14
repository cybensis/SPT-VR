using EFT;
using RootMotion.FinalIK;
using UnityEngine;
using TarkovVR.Source.Controls;

namespace TarkovVR.Source.Player.VR
{
    internal class IKManager : MonoBehaviour
    {
        private bool matchingHeadToBody = false;
        private Vector3 upperArmPos = new Vector3(-0.1f, 0.1f, 0);
        private readonly float forwardThreshold = 0.20f;    // Distance forward before body follows
        private readonly float backwardThreshold = 0.20f;   // Distance backward before body follows
        private readonly float sideThreshold = 0.40f;       // Distance to sides before body follows
        private readonly float emergencyThreshold = 0.6f;   // Distance for emergency repositioning
        private readonly float followSpeed = 2.5f;          // How quickly body follows head
        private readonly float movementMultiplier = 0.8f;   // Additional follow speed when moving
        private readonly AnimationCurve followCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        private readonly float headLeadDistance = 0.10f;       // How far behind head the legs stay when moving
        private readonly float idleOffsetZ = 0.20f;         // Idle z offset since legs move back when idle
        private readonly float headToLegLength = 0.27f;          // Neck length for pitch compensation
        private readonly float leanCooldownTime = 0.3f;     // Time after stopping before lean activates

        private Vector3 velocitySmoothing = Vector3.zero;
        private readonly float smoothTime = 0.1f;
        private Vector3 lastHeadPosition;
        private float stopTimer = 0f;
        private bool wasUsingControls = false;

        private Transform leftUpperArm;
        private Transform rightUpperArm;
        public GameObject leftHandIK;
        public GameObject rightHandIK;
        public LimbIK leftArmIk;
        public LimbIK rightArmIk;

        // The elbow bend solve is in IKPatches.DriveElbowBendNormals (BendModifier.Custom). Don't use BendModifier.Arm on this
        // rig: the LimbIK sits on the collarbone, so the Arm axis hints resolve in the collarbone's local frame (the right elbow
        // solves backwards, the left misaligned).

        private void Awake()
        {
            VRGlobals.VRCam = Camera.main;
            
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child.name == "Base HumanRibcage")
                {
                    for (int a = 0; a < child.childCount; a++)
                    {
                        Transform innerChild = child.GetChild(a);
                        if (innerChild.name == "Base HumanLCollarbone")
                            leftUpperArm = innerChild.GetChild(0);
                        else if (innerChild.name == "Base HumanRCollarbone")
                            rightUpperArm = innerChild.GetChild(0);
                    }
                }
            }
            
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The body under the head (FollowHead). The neck point (HeadTracking.neckLocalOffset off the headset: still under head turns and
        // nods) has lean room around where it sits over the standing body: inside it the body stays (lean out, peek, duck), past it the
        // body takes only the overshoot, so the head is never further off than the room. The room is measured along where you look,
        // shrinks while the movement stick is pushed (the body walks under you) and grows back when it's let go. One step a frame,
        // whoever calls first (the hands states each call it).

        public static bool legacyBodyFollow = false;    // the old dead-zone / three-mode follow (AdaptiveBodySync), for an A/B
        public static float neckAheadOfBody = 0.12f;    // m the neck point sits ahead of the body's root standing straight, along its facing
        public static float neckAheadWalking = 0.02f;   // the same walking (Tarkov's idle stands the legs further back than its walk)
        public static float leanRoomForward = 0.20f;    // m the neck may lean off that before the body follows: forward, back, sideways
        public static float leanRoomBack = 0.20f;
        public static float leanRoomSide = 0.40f;
        public static float movingRoomScale = 0.1f;     // the room while the movement stick is pushed, of the above
        public static float roomBlendTime = 0.3f;       // s the room takes to shrink / grow back
        public static float stickDeadZone = 0.15f;      // the movement stick past this = walking
        public static float followTime = 0.08f;         // s the body takes to take up the overshoot (0 = at once: as jittery as the head)
        public static bool blockedByWalls = false;      // the body's capsule swept along each move: a wall stops it, the head goes on
        public static float wallSkin = 0.02f;           // m kept off a wall
        public static bool debugLog = false;            // a line a second: the neck's offset, the room, the overshoot

        private int followedFrame = -1;
        private float roomScale = 1f;
        private float nextFollowLog;
        private int sweepMaskLayer = -1;
        private int sweepMask;
        private static readonly RaycastHit[] sweepHits = new RaycastHit[16];

        public void MatchLegsToArms()
        {
            // On a mounted gun the body stays where Tarkov sat it (Interactions.MountedGun)
            if (VRGlobals.vrPlayer.initPos.y == 0 || Interactions.MountedGun.Active)
                return;

            if (VRGlobals.inGame && VRGlobals.player &&
                VRGlobals.player.PointOfView == EPointOfView.FirstPerson &&
                name == "Base HumanSpine3")
            {
                if (legacyBodyFollow)
                    AdaptiveBodySync();
                else
                    FollowHead();
            }
            else
            {
                transform.localPosition = Vector3.zero;
            }
        }

        private void FollowHead()
        {
            if (followedFrame == Time.frameCount)
                return;
            followedFrame = Time.frameCount;
            Camera headset = VRGlobals.VRCam;
            Transform body = transform.root;
            if (headset == null || body == null || VRGlobals.vrOffsetter == null || VRGlobals.vrOffsetter.transform.parent == null || VRGlobals.camRoot == null)
                return;

            float deltaTime = Time.deltaTime;
            bool walking = VRInput.LeftJoystick.axis.sqrMagnitude > stickDeadZone * stickDeadZone;
            roomScale = Mathf.MoveTowards(roomScale, walking ? movingRoomScale : 1f, deltaTime / Mathf.Max(roomBlendTime, 0.01f));

            Transform head = headset.transform;
            Vector3 bodyForward = FlattenVector(body.forward);
            if (bodyForward.sqrMagnitude < 1e-4f)
                return;
            bodyForward.Normalize();
            Vector3 neck = head.position + head.rotation * TarkovVR.Source.Player.VRManager.HeadTracking.neckLocalOffset;
            float standing = movingRoomScale < 1f ? Mathf.Clamp01((roomScale - movingRoomScale) / (1f - movingRoomScale)) : 1f;
            Vector3 offset = FlattenVector(neck - body.position) - bodyForward * Mathf.Lerp(neckAheadWalking, neckAheadOfBody, standing);

            // Along where you look (straight down, the top of the head points that way)
            Vector3 lookForward = FlattenVector(head.forward);
            if (lookForward.sqrMagnitude < 0.01f)
                lookForward = FlattenVector(head.up);
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
                move = SweptMove(move);

            if (debugLog && Time.time >= nextFollowLog)
            {
                nextFollowLog = Time.time + 1f;
                Plugin.MyLog.LogInfo($"[BodyFollow] neck off the body {along * 100f:0} cm along the look, {across * 100f:0} across; room {roomAlong * 100f:0} / {roomAcross * 100f:0} cm"
                    + $"{(walking ? " (walking)" : "")}; overshoot {overshoot.magnitude * 100f:0.0} cm, moved {move.magnitude * 100f:0.00} cm this frame");
            }
            if (move.sqrMagnitude < 1e-10f)
                return;
            MoveBody(body, move);
        }

        // The body moves, the rig the other way at once (not at the next camRoot pin / offsetter update): the head and the hands stay
        // where they are in the world this frame, whichever runs first
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

        // How far the body's capsule gets along the move before a wall: raised by the step height, so floors, steps and kerbs don't stop it.
        // The layers are the ones the character controller collides with.
        private Vector3 SweptMove(Vector3 move)
        {
            EFT.Player player = VRGlobals.player;
            ICharacterController controller = player != null ? player.CharacterController : null;
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

        private void AdaptiveBodySync()
        {
            Vector3 headPos = VRGlobals.VRCam.transform.position;
            Quaternion headRot = VRGlobals.VRCam.transform.rotation;
            Vector3 headPosFlat = FlattenVector(headPos);

            // Calculate head offset relative to body orientation
            Vector3 localHeadOffset = transform.root.InverseTransformPoint(headPosFlat);
            float forwardLean = localHeadOffset.z - idleOffsetZ;
            float sideLean = localHeadOffset.x;
            float totalLeanDistance = new Vector2(forwardLean, sideLean).magnitude;

            bool isUsingControls = IsUsingMovementControls();

            // Track stop timer for cooldown between moving and stopping
            if (isUsingControls)
            {
                stopTimer = 0f;
                wasUsingControls = true;
            }
            else if (wasUsingControls)
            {
                stopTimer += Time.deltaTime;
                if (stopTimer >= leanCooldownTime)
                {
                    wasUsingControls = false;
                }
            }

            // Snaps head back to leg position if too far away. Unlikely to happen normally.
            if (totalLeanDistance > emergencyThreshold + 0.2f)
            {
                EmergencySnapToHead(headPosFlat);
                matchingHeadToBody = false;
                lastHeadPosition = headPosFlat;
                return;
            }

            bool shouldFollow = false;
            bool isLeaning = false;

            if (isUsingControls)
            {
                // Using movement controls - follow head directly
                shouldFollow = true;
            }
            else if (!wasUsingControls)
            {
                // Not using controls and movement cooldown complete - check for leaning
                bool forwardLeanTriggered = forwardLean > forwardThreshold;
                bool backwardLeanTriggered = forwardLean < -backwardThreshold;
                bool sideLeanTriggered = Mathf.Abs(sideLean) > sideThreshold;

                if (forwardLeanTriggered || backwardLeanTriggered || sideLeanTriggered)
                {
                    shouldFollow = true;
                    isLeaning = true;
                }
            }

            if (shouldFollow)
            {
                matchingHeadToBody = true;

                // The further the lean, the quicker the body follows
                float forwardIntensity = Mathf.Clamp01(Mathf.Abs(forwardLean) / emergencyThreshold * 2f);
                float sideIntensity = Mathf.Clamp01(Mathf.Abs(sideLean) / emergencyThreshold * 2f);
                float followIntensity = followCurve.Evaluate(Mathf.Max(forwardIntensity, sideIntensity));

                float currentSpeed = followSpeed * (isUsingControls ? movementMultiplier : 1.0f);

                SmartFollowHead(headPosFlat, headRot, currentSpeed, followIntensity, isUsingControls, isLeaning);
            }
            else
            {
                matchingHeadToBody = false;
            }

            lastHeadPosition = headPosFlat;
        }

        private Vector3 FlattenVector(Vector3 vector)
        {
            return new Vector3(vector.x, 0, vector.z);
        }

        private bool IsUsingMovementControls()
        {
            float xAxis = Mathf.Abs(VRInput.LeftJoystick.axis.x);
            float yAxis = Mathf.Abs(VRInput.LeftJoystick.axis.y);
            return (xAxis > 0 || yAxis > 0);
        }

        private void EmergencySnapToHead(Vector3 headPosFlat)
        {
            Vector3 localPos = VRGlobals.VRCam.transform.localPosition;
            localPos.y = VRGlobals.vrPlayer.initPos.y;
            VRGlobals.vrPlayer.initPos = localPos;
            VRGlobals.camRoot.transform.position = transform.root.position;
            matchingHeadToBody = false;
        }

        private void SmartFollowHead(Vector3 headPosFlat, Quaternion headRot, float speed, float intensity, bool isActivelyMoving, bool isLeaning)
        {
            if (transform == null || transform.root == null || VRGlobals.vrPlayer == null ||
                VRGlobals.vrOffsetter == null || VRGlobals.vrOffsetter.transform == null ||
                VRGlobals.vrOffsetter.transform.parent == null)
                return;

            matchingHeadToBody = true;

            // Calculate pitch compensation once
            Vector3 headForward = FlattenVector(headRot * Vector3.forward).normalized;
            float pitchAngle = VRGlobals.VRCam.transform.eulerAngles.x;
            Vector3 compensatedHeadPos = headPosFlat - (headForward * headToLegLength * Mathf.Sin(pitchAngle * Mathf.Deg2Rad));

            Vector3 targetPos;

            if (isActivelyMoving)
            {
                // Using movement controls - keep legs behind head
                targetPos = compensatedHeadPos - (headForward * headLeadDistance);
            }
            else if (isLeaning)
            {
                // Leaning without controls - move only past threshold
                Vector3 toHead = compensatedHeadPos - transform.root.position;
                Vector3 localOffset = transform.root.InverseTransformVector(toHead);
                float adjustedLocalZ = localOffset.z - idleOffsetZ;

                float forwardOver = Mathf.Max(0f, adjustedLocalZ - forwardThreshold);
                float backwardOver = Mathf.Max(0f, -adjustedLocalZ - backwardThreshold);
                float sideOver = Mathf.Max(0f, Mathf.Abs(localOffset.x) - sideThreshold);

                float offsetZ = localOffset.z > 0 ? forwardOver : -backwardOver;
                float offsetX = Mathf.Sign(localOffset.x) * sideOver;

                Vector3 clampedLocalOffset = new Vector3(offsetX, 0f, offsetZ);
                Vector3 clampedWorldOffset = transform.root.TransformVector(clampedLocalOffset);

                float normalizedLeanDistance = Mathf.Clamp01(clampedLocalOffset.magnitude / 0.4f);
                float dynamicIntensity = Mathf.Lerp(0.2f, 1f, normalizedLeanDistance);

                targetPos = transform.root.position + clampedWorldOffset * dynamicIntensity;
            }
            else
            {
                targetPos = transform.root.position;
            }

            targetPos.y = transform.root.position.y;

            Vector3 newPosition = Vector3.SmoothDamp(
                transform.root.position,
                targetPos,
                ref velocitySmoothing,
                smoothTime,
                speed * intensity
            );

            Vector3 movementDelta = newPosition - transform.root.position;
            transform.root.position = newPosition;

            // Adjust VR origin offset to compensate for body movement
            Vector3 localDelta = VRGlobals.vrOffsetter.transform.parent.InverseTransformVector(movementDelta);
            localDelta.y = 0;
            VRGlobals.vrPlayer.initPos += localDelta;
        }

        void Update()
        {
            if (!VRGlobals.vrPlayer || VRGlobals.menuOpen)
                return;

            if (leftUpperArm)
                leftUpperArm.localPosition = upperArmPos;
            if (rightUpperArm)
                rightUpperArm.localPosition = upperArmPos;
            
            // (the new follow every frame in every hands state: the others' calls in the same frame are no-ops)
            if (!legacyBodyFollow || !VRGlobals.emptyHands || VRGlobals.player.HandsIsEmpty)
            {
                MatchLegsToArms();
            }
        }
    }
}