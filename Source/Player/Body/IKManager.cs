using EFT;
using RootMotion.FinalIK;
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
        private float forwardThreshold = 0.40f;    // Distance forward before body follows
        private float backwardThreshold = 0.10f; // Distance backward before body follows
        private float sideThreshold = 0.50f;      // Distance to sides before body follows
        private float emergencyThreshold = 0.8f;  // Distance for emergency repositioning
        private float followSpeed = 2.5f;         // How quickly body follows head
        private float movementMultiplier = 0.8f;  // Additional follow speed when moving
        private AnimationCurve followCurve = AnimationCurve.EaseInOut(0, 0, 1, 1); // Smoothing curve
        private float headLeadDistance = 0.2f;    // How far ahead the head stays from body
        private float minHeadLeadDistance = 0.1f; // Minimum lead distance even when stationary

        private Vector3 velocitySmoothing = Vector3.zero;
        private float smoothTime = 0.1f;
        private Vector3 lastHeadPosition;
        private float stopSmoothingTime = 1.0f; // Time to smoothly transition when stopping
        private float stopTimer = 0f; // Timer to track stopping transition
        private bool wasMoving = false; // Track if we were moving last frame

        private Transform leftUpperArm;
        private Transform rightUpperArm;
        public GameObject leftHandIK;
        public GameObject rightHandIK;
        public LimbIK leftArmIk;
        public LimbIK rightArmIk;


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
            Vector3 bodyPos = transform.root.position;
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
            float forwardLean = localHeadOffset.z;
            float sideLean = localHeadOffset.x;
            float totalLeanDistance = new Vector2(forwardLean, sideLean).magnitude;

            // Check if moving - only using joystick movement now
            bool isUsingMovementControls = IsUsingMovementControls();
            bool isPhysicallyMoving = IsPhysicallyMoving(headPosFlat, lastHeadPosition); // Keep for debugging
            bool isMoving = isUsingMovementControls; // Only consider joystick movement

            // Calculate adaptive thresholds
            float adaptiveForwardThreshold = isUsingMovementControls ? forwardThreshold * 0.7f : forwardThreshold;
            float adaptiveBackwardThreshold = isUsingMovementControls ? backwardThreshold * 0.7f : backwardThreshold;
            float adaptiveSideThreshold = isUsingMovementControls ? sideThreshold * 0.7f : sideThreshold;
            // Determine if body should follow head based on joystick input only
            bool forwardLeanTriggered = forwardLean > adaptiveForwardThreshold;
            bool backwardLeanTriggered = forwardLean < -adaptiveBackwardThreshold;
            bool sideLeanTriggered = Mathf.Abs(sideLean) > adaptiveSideThreshold;

            bool shouldStartFollowing = forwardLeanTriggered || backwardLeanTriggered || sideLeanTriggered || isUsingMovementControls;

            // Check if still in the process of returning to neutral
            bool shouldContinueFollowing = matchingHeadToBody &&
                (Mathf.Abs(angleDiff) > 10f);

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
                
                if (totalLeanDistance > emergencyThreshold)
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
                float followIntensity = Mathf.Clamp01(totalLeanDistance / emergencyThreshold * 2);
                followIntensity = followCurve.Evaluate(followIntensity);

                SmartFollowHead(headPosFlat, headRot, currentSpeed, followIntensity, isMoving);
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
            float threshold = VRSettings.GetLeftStickSensitivity();
            return (xAxis > threshold || yAxis > threshold);
        }

        private bool IsPhysicallyMoving(Vector3 currentHeadPos, Vector3 lastHeadPos)
        {
            // This function is now only used for debugging purposes
            // It is no longer used to determine if the body should follow the head
            if (lastHeadPos == Vector3.zero) return false;

            // Check if head has moved significantly since last frame
            float moveDelta = Vector3.Distance(currentHeadPos, lastHeadPos);
            return moveDelta > (Time.deltaTime * 0.3f); // Adjust threshold as needed
        }

        private void EmergencySnapToHead(Vector3 headPosFlat)
        {
            //VRGlobals.vrPlayer.initPos = Camera.main.transform.localPosition;
            VRGlobals.vrPlayer.initPos = VRGlobals.VRCam.transform.localPosition;
            VRGlobals.camRoot.transform.position = transform.root.position;
            matchingHeadToBody = false; // Reset state after emergency snap
        }

        private void SmartFollowHead(Vector3 headPosFlat, Quaternion headRot, float speed, float intensity, bool isActivelyMoving)
        {

            if (transform == null || transform.root == null || VRGlobals.vrPlayer == null || VRGlobals.vrOffsetter?.transform?.parent == null || VRGlobals.firearmController == null)
                return;

            matchingHeadToBody = true;

            // Calculate current velocity of head movement
            Vector3 headVelocity = (headPosFlat - lastHeadPosition) / Time.deltaTime;
            float headSpeed = headVelocity.magnitude;

            // Get local head velocity relative to body orientation
            Vector3 localHeadVelocity = transform.root.InverseTransformDirection(headVelocity);

            // Create target position
            Vector3 targetPos;

            if (isActivelyMoving)
            {
                
                // Only apply lag when moving in forward direction
                bool hasForwardComponent = localHeadVelocity.z > 0.1f;

                //IsAiming check can be removed here but it helps keep aim stabilized when moving. It becomes more unstable the higher the lead distance is.
                if (headSpeed > 0.1f && hasForwardComponent && !VRGlobals.firearmController.IsAiming) 
                {
                    // Calculate dynamic lead distance based on head movement speed
                    float dynamicLeadDistance = Mathf.Lerp(
                        minHeadLeadDistance,  // Minimum distance when slow
                        headLeadDistance,     // Maximum distance at full speed
                        Mathf.Clamp01(headSpeed / 2.0f)  // Normalized speed factor
                    );

                    // Use actual movement direction for lead calculation
                    targetPos = headPosFlat - (headVelocity.normalized * dynamicLeadDistance);
                }
                else
                {
                    float dynamicLeadDistance = Mathf.Lerp(
                        minHeadLeadDistance,  // Minimum distance when slow
                        headLeadDistance,     // Maximum distance at full speed
                        Mathf.Clamp01(headSpeed / 2.0f)  // Normalized speed factor
                    );
                    // When moving backward, sideways, or not moving significantly,
                    // target exact head position
                    targetPos = headPosFlat + (headVelocity.normalized * dynamicLeadDistance) * 0.2f;
                }
            }
            else
            {

                // When stopping movement, position the body slightly behind the head
                // for a more natural standing pose
                float behindDistance = 0.25f; // Distance behind the head (in meters)
                Vector3 headForward = FlattenVector(headRot * Vector3.forward).normalized;
                targetPos = headPosFlat - (headForward * behindDistance);
            }

            // Maintain body Y position
            targetPos.y = transform.root.position.y;

            // Calculate smooth movement - use SmoothDamp for more natural motion
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
