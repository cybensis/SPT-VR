using EFT;
using RootMotion.FinalIK;
using System.Threading;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.VR
{
    internal class IKManager : MonoBehaviour
    {

        private bool matchingHeadToBody = false;
        private Vector3 upperArmPos = new Vector3(-0.1f, 0.1f, 0);
        private readonly float forwardThreshold = 0.50f;    // Distance forward before body follows
        private readonly float backwardThreshold = 0.20f; // Distance backward before body follows
        private readonly float sideThreshold = 0.40f;      // Distance to sides before body follows
        private readonly float emergencyThreshold = 0.6f;  // Distance for emergency repositioning
        private readonly float followSpeed = 2.5f;         // How quickly body follows head
        private readonly float movementMultiplier = 0.8f;  // Additional follow speed when moving
        private readonly AnimationCurve followCurve = AnimationCurve.EaseInOut(0, 0, 1, 1); // Smoothing curve
        private readonly float headLeadDistance = 0.15f;    // How far ahead the head stays from body
        private readonly float minHeadLeadDistance = 0.15f; // Minimum lead distance even when stationary
        private readonly float idleOffsetZ = 0.20f; // The actual idle z offset since the legs move back a bit when idle

        private Vector3 velocitySmoothing = Vector3.zero;
        private Vector3 smoothedTargetPos;
        private readonly float smoothTime = 0.1f;
        private Vector3 lastHeadPosition;
        private readonly float stopSmoothingTime = 1.0f; // Time to smoothly transition when stopping
        private float stopTimer = 0f; // Timer to track stopping transition
        private bool wasMoving = false; // Track if we were moving last frame
        private Vector3 idleAnchorPosition;

        private Transform leftUpperArm;
        private Transform rightUpperArm;
        public GameObject leftHandIK;
        public GameObject rightHandIK;
        public LimbIK leftArmIk;
        public LimbIK rightArmIk;
        // ADD THESE:
        private Transform leftForearm;
        private Transform rightForearm;
        public static float armLengthScale = 1.0f; // Current arm scale

        // ADD THESE: Store original scales
        private Vector3 leftUpperArmOriginalScale = Vector3.one;
        private Vector3 rightUpperArmOriginalScale = Vector3.one;
        private Vector3 leftForearmOriginalScale = Vector3.one;
        private Vector3 rightForearmOriginalScale = Vector3.one;
        private bool scalesInitialized = false;

        
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
        

        public void MatchLegsToArms()
        {
            // Skip if not initialized
            if (VRGlobals.vrPlayer.initPos.y == 0)
                return;

            if (VRGlobals.inGame && VRGlobals.player &&
                VRGlobals.player.PointOfView == EPointOfView.FirstPerson &&
                name == "Base HumanSpine3")
            {
                AdaptiveBodySync();
            }
            else
            {
                // Reset position for non-spine bones or when not in first-person VR
                transform.localPosition = Vector3.zero;
            }
        }
        
        private void AdaptiveBodySync()
        {
            // Get current positions
            //Vector3 headPos = Camera.main.transform.position;
            Vector3 headPos = VRGlobals.VRCam.transform.position;

            //Quaternion headRot = Camera.main.transform.rotation;
            Quaternion headRot = VRGlobals.VRCam.transform.rotation;

            // Calculate current state
            Vector3 headPosFlat = FlattenVector(headPos);

            // Calculate head offset relative to body orientation
            Vector3 localHeadOffset = transform.root.InverseTransformPoint(headPosFlat);

            // Calculate facing angle difference (horizontal plane only)
            Vector3 headForward = FlattenVector(headRot * Vector3.forward).normalized;
            Vector3 bodyForward = FlattenVector(transform.root.forward).normalized;
            float angleDiff = Vector3.SignedAngle(bodyForward, headForward, Vector3.up);

            // Extract leaning values
            //float forwardLean = localHeadOffset.z;
            float forwardLean = localHeadOffset.z - idleOffsetZ;
            float sideLean = localHeadOffset.x;
            float totalLeanDistance = new Vector2(forwardLean, sideLean).magnitude;

            // Check if moving - only using joystick movement now
            bool isUsingMovementControls = IsUsingMovementControls();
           
            // Calculate adaptive thresholds
            float adaptiveForwardThreshold = isUsingMovementControls ? forwardThreshold * 0.7f : forwardThreshold;
            float adaptiveBackwardThreshold = isUsingMovementControls ? backwardThreshold * 0.7f : backwardThreshold;
            float adaptiveSideThreshold = isUsingMovementControls ? sideThreshold * 0.7f : sideThreshold;
            // Determine if body should follow head based on joystick input only
            bool forwardLeanTriggered = forwardLean > adaptiveForwardThreshold;
            bool backwardLeanTriggered = forwardLean < -adaptiveBackwardThreshold;
            bool sideLeanTriggered = Mathf.Abs(sideLean) > adaptiveSideThreshold;

            //Check movement type            
            bool isMoving = isUsingMovementControls;
            bool isLeaning = !isMoving && (forwardLeanTriggered || backwardLeanTriggered || sideLeanTriggered);

            //Check if we should start following the head
            bool shouldStartFollowing = isMoving || forwardLeanTriggered || backwardLeanTriggered || sideLeanTriggered;

            // Check if still in the process of returning to neutral
            bool shouldContinueFollowing = matchingHeadToBody &&
                (Mathf.Abs(angleDiff) > 10f);
            //Plugin.MyLog.LogError($"isLeaning: {isLeaning} - forwardLeanTiggered: {forwardLeanTriggered} - backwardLeanTriggered: {backwardLeanTriggered} - FowardLean: {forwardLean}");
            // Handle movement state transitions
            if (isMoving)
            {
                // Reset stop timer when moving
                stopTimer = 0f;
                wasMoving = true;
            }
            else if (wasMoving)
            {
                // Just stopped moving - start the timer
                stopTimer += Time.deltaTime;

                // Force continue following during stop transition period
                if (stopTimer < stopSmoothingTime)
                {
                    shouldContinueFollowing = true;
                }
                else
                {
                    // Transition complete
                    wasMoving = false;
                    stopTimer = 0f;
                }
            }

            // Apply body movement
            if (shouldStartFollowing || shouldContinueFollowing)
            {

                if (totalLeanDistance > emergencyThreshold + 0.2f)
                {
                    // Emergency snap when leaned too far
                    EmergencySnapToHead(headPosFlat);
                }
                else
                {

                    // Gradual movement with smoothing
                    float currentSpeed = followSpeed;

                    // Increase follow speed when using movement controls
                    if (isUsingMovementControls)
                    {
                        currentSpeed *= movementMultiplier;
                    }

                    // If we're in the stopping transition, gradually reduce movement speed
                    if (!isMoving && wasMoving)
                    {
                        float stopProgress = stopTimer / stopSmoothingTime;
                        currentSpeed *= (1.0f - stopProgress * 0.7f); // Don't completely reduce to zero
                    }

                    // Calculate how aggressively to follow based on how far the lean is
                    float forwardIntensity = Mathf.Clamp01(Mathf.Abs(forwardLean) / emergencyThreshold * 2f);
                    float sideIntensity = Mathf.Clamp01(Mathf.Abs(sideLean) / emergencyThreshold * 2f);

                    float followIntensity = followCurve.Evaluate(Mathf.Max(forwardIntensity, sideIntensity));
                    //followIntensity = followCurve.Evaluate(followIntensity);

                    SmartFollowHead(headPosFlat, headRot, currentSpeed, followIntensity, isMoving, isLeaning);
                }
            }
            else
            {
                matchingHeadToBody = false;
            }

            // Update position tracking for next frame
            lastHeadPosition = headPosFlat;
        }
        

        private Vector3 FlattenVector(Vector3 vector)
        {
            return new Vector3(vector.x, 0, vector.z);
        }

        private bool IsUsingMovementControls()
        {
            float xAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x);
            float yAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y);
            return (xAxis > 0 || yAxis > 0);
        }

        private void EmergencySnapToHead(Vector3 headPosFlat)
        {
            Vector3 localPos = VRGlobals.VRCam.transform.localPosition;
            localPos.y = VRGlobals.vrPlayer.initPos.y; // Keep previous height
            VRGlobals.vrPlayer.initPos = localPos;
            VRGlobals.camRoot.transform.position = transform.root.position;
            matchingHeadToBody = false; // Reset state after emergency snap
        }
        
        private void SmartFollowHead(Vector3 headPosFlat, Quaternion headRot, float speed, float intensity, bool isActivelyMoving, bool isLeaning)
        {

            if (transform == null || transform.root == null || VRGlobals.vrPlayer == null || VRGlobals.vrOffsetter == null || VRGlobals.vrOffsetter.transform == null || VRGlobals.vrOffsetter.transform.parent == null || VRGlobals.firearmController == null)
                return;

            matchingHeadToBody = true;

            // Calculate current velocity of head movement
            Vector3 headVelocity = (headPosFlat - lastHeadPosition) / Time.deltaTime;
            float headSpeed = headVelocity.magnitude;

            // Create target position
            Vector3 targetPos;

            if (isActivelyMoving)
            {

                float dynamicLeadDistance = Mathf.Lerp(
                    minHeadLeadDistance,
                    headLeadDistance,
                    Mathf.Clamp01(headSpeed / 2.0f)
                );

                // Always move the legs backward relative to where the head is looking,
                // not in the direction of movement
                Vector3 headForward = FlattenVector(headRot * Vector3.forward).normalized;
                targetPos = headPosFlat - (headForward * dynamicLeadDistance);
            }
            else
            {
                if (isLeaning)
                {
                    Vector3 toHead = headPosFlat - transform.root.position;
                    Vector3 localOffset = transform.root.InverseTransformVector(toHead);

                    float adjustedLocalZ = localOffset.z - idleOffsetZ;

                    //float forwardOver = Mathf.Max(0f, localOffset.z - forwardThreshold);
                    //float backwardOver = Mathf.Max(0f, -localOffset.z - backwardThreshold);
                    float forwardOver = Mathf.Max(0f, adjustedLocalZ - forwardThreshold);
                    float backwardOver = Mathf.Max(0f, -adjustedLocalZ - backwardThreshold - 0.1f);
                    float sideOver = Mathf.Max(0f, Mathf.Abs(localOffset.x) - sideThreshold);

                    
                    float offsetZ = localOffset.z > 0 ? forwardOver : -backwardOver;
                    float offsetX = Mathf.Sign(localOffset.x) * sideOver;

                    Vector3 clampedLocalOffset = new Vector3(offsetX, 0f, offsetZ);
                    Vector3 clampedWorldOffset = transform.root.TransformVector(clampedLocalOffset);

                    float normalizedLeanDistance = Mathf.Clamp01(clampedLocalOffset.magnitude / 0.4f);
                    float dynamicIntensity = Mathf.Lerp(0.2f, 1f, normalizedLeanDistance);

                    Vector3 desiredPos = transform.root.position + clampedWorldOffset * dynamicIntensity;

                    //float leanLerpSpeed = Mathf.Lerp(10f, 20f, normalizedLeanDistance); // tune these values
                    //targetPos = Vector3.Lerp(transform.root.position, desiredPos, Time.deltaTime * leanLerpSpeed);
                    targetPos = desiredPos;
                }
                else if (!isActivelyMoving && wasMoving)
                {
                    //When you stop moving with joystick legs will position themselves a bit behind the head
                    float behindDistance = 0.20f;
                    Vector3 headForward = FlattenVector(headRot * Vector3.forward).normalized;
                    targetPos = headPosFlat - (headForward * behindDistance);
                }
                else
                {
                    // Do nothing special — hold still
                    targetPos = transform.root.position;
                }
            }

            // Maintain body Y position
            targetPos.y = transform.root.position.y;
            
            Vector3 newPosition = Vector3.SmoothDamp(
                transform.root.position,
                targetPos,
                ref velocitySmoothing,
                isActivelyMoving ? smoothTime : smoothTime * 2.0f, // Use longer smoothing time when stopping
                speed * intensity
            );
            
            // Calculate movement delta
            Vector3 movementDelta = newPosition - transform.root.position;

            // Apply movement to root
            transform.root.position = newPosition;

            // Adjust VR origin offset to compensate for body movement
            Vector3 localDelta = VRGlobals.vrOffsetter.transform.parent.InverseTransformVector(movementDelta);
            localDelta.y = 0; // Only apply horizontal movement
            VRGlobals.vrPlayer.initPos += localDelta;
        }
        

        // NOTE: I tried extending the arms but that offsets the hands from the gun, so instead set the local position of
        // the upper arm, or the collarbone, for the upper arm the +y value goes forward
        // For collarbone setting everything to 0 and z = 0.1 seemed to work fine

        void Update()
        {
            if (!VRGlobals.vrPlayer || VRGlobals.menuOpen)
                return;

            if (leftUpperArm)
                leftUpperArm.localPosition = upperArmPos;
            if (rightUpperArm)
                rightUpperArm.localPosition = upperArmPos;
            
            if (!VRGlobals.emptyHands || VRGlobals.player.HandsIsEmpty)
            {
                MatchLegsToArms();
            }
        }
    }
}








