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
                transform.localPosition = Vector3.zero;
            }
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

                // Calculate follow intensity based on lean distance
                // The further/faster the lean, the quicker the body follows
                float forwardIntensity = Mathf.Clamp01(Mathf.Abs(forwardLean) / emergencyThreshold * 2f);
                float sideIntensity = Mathf.Clamp01(Mathf.Abs(sideLean) / emergencyThreshold * 2f);
                float followIntensity = followCurve.Evaluate(Mathf.Max(forwardIntensity, sideIntensity));

                float currentSpeed = followSpeed * (isUsingControls ? movementMultiplier : 1.0f);

                // This is the function that handles the legs/body following the head
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
            float xAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x);
            float yAxis = Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y);
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
            
            if (!VRGlobals.emptyHands || VRGlobals.player.HandsIsEmpty)
            {
                MatchLegsToArms();
            }
        }
    }
}