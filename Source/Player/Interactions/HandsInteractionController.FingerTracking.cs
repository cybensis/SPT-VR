using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Source.Player.Interactions
{
    // SteamVR skeletal finger tracking (Index/Knuckles).
    //
    // The manifest binds SkeletonLeftHand/SkeletonRightHand; SteamVR exposes per-finger curls (0 = open .. 1 = closed) and a
    // skeletalTrackingLevel. Fingers curl from the baked open pose (restPoseEuler / rightRestPoseEuler) toward a fist, each joint
    // bent around curlAxis by its curl (not from ApplyFlatFlex's pose, which slants the fingers). There's no bone retarget: a curl
    // is a scalar, and the bake handles each bone's frame. Gated on skeletalTrackingLevel >= Partial, so only Index/Knuckles
    // track; other controllers (Estimated) keep the baked pose.
    internal partial class HandsInteractionController
    {
        public static bool fingerTracking = true;          // master: Index skeletal finger tracking
        public static float curlToDeg = 90f;               // full curl swing at the base joint (tapers down by curlJointScale)
        public static float thumbCurlToDeg = 28f;          // thumbs curl far less than fingers
        public static float curlJointScale = 0.8f;         // per-joint curl falloff, separate from midJointScale (0.6 leaves the tips too straight to close a fist)
        // Fingers resting on an Index controller read ~0.4-0.6, not 0 (the thumb higher, since it rests on the stick). The authored
        // rest pose maps to the resting curl: squeezing curls toward a fist, and lifting the fingers opens them past the bake,
        // capped by maxExtendDeg.
        public static float curlRestLevel = 0.45f;
        public static float thumbCurlRestLevel = 0.6f;
        public static float maxExtendDeg = 12f;            // deg past the bake when fingers lift off the controller
        public static Vector3 curlAxis = Vector3.forward;  // the joints' flexion axis (local); ReplayRestPose bends the idle flex around forward too
        // Both rigs curl with positive rotation about forward (unlike rightFlexSign, which compensates for the negative
        // idle-flex constants). Flip one only if a hand closes backward.
        public static float leftCurlSign = 1f;
        public static float rightCurlSign = 1f;
        public static bool fingerTrackingDebug = false;    // logs the tracking level and curls per hand once a second

        private float nextFingerTrackLog;

        // The skeleton action for a logical hand. Dominant = the physical right controller unless
        // left-handed mode swaps the hands.
        private static VRSkeleton HandSkeleton(bool dominant)
        {
            bool physRight = dominant ^ VRSettings.GetLeftHandedMode();
            return physRight ? VRInput.SkeletonRightHand
                             : VRInput.SkeletonLeftHand;
        }

        // True only when the controller actually senses fingers (Index = Partial/Full). Estimated
        // (standard controllers, curl faked from grip/trigger) keeps the baked pose.
        private static bool SkeletalActive(bool dominant)
        {
            if (!fingerTracking)
                return false;
            var skeleton = HandSkeleton(dominant);
            return skeleton != null && skeleton.active
                && (int)skeleton.skeletalTrackingLevel >= (int)EVRSkeletalTrackingLevel.VRSkeletalTracking_Partial;
        }

        // Curls the fingers from their baked open pose (basePose, packed 3 per finger in fingerJoints order) toward a fist,
        // driven by the live curls. leftDeltas first layers the left hand's idle flex and spread deltas on the bake, like
        // ReplayRestPose (the left's authored look is bake + deltas; the right's bake stands alone). Returns false if there's
        // no curl data this frame (the caller falls back to the static baked pose).
        private bool ApplySkeletalCurls(bool dominant, Transform[][] fingerJoints, bool[] isThumb, Vector3[] basePose, float sign, float weight, bool leftDeltas)
        {
            var skeleton = HandSkeleton(dominant);
            float[] curls = skeleton != null ? skeleton.fingerCurls : null;
            if (curls == null || curls.Length < 5 || fingerJoints == null || basePose == null)
                return false;

            int packedIndex = 0;
            for (int fingerIndex = 0; fingerIndex < fingerJoints.Length; fingerIndex++)
            {
                Transform[] joints = fingerJoints[fingerIndex];
                bool thumb = isThumb != null && fingerIndex < isThumb.Length && isThumb[fingerIndex];
                float gain = thumb ? thumbCurlToDeg : curlToDeg;
                float rest = thumb ? thumbCurlRestLevel : curlRestLevel;
                // Recentred: the resting curl gives the authored pose exactly; the open side is clamped
                float bendBase = Mathf.Max((curls[CurlIndex(isThumb, fingerIndex)] - rest) * gain, -maxExtendDeg);
                float flexBase = leftDeltas ? PerFingerFlex(fingerIndex) : 0f;
                float spread = leftDeltas && leftFingerSpread != null && fingerIndex < leftFingerSpread.Length
                    ? leftFingerSpread[fingerIndex] * fingerSpreadDeg : 0f;
                for (int j = 0; j < joints.Length && j < 3; j++, packedIndex++)
                {
                    if (joints[j] == null || packedIndex >= basePose.Length)
                        continue;
                    // The curl uses its own (gentler) taper so the tips close into the fist; the left's
                    // idle-flex delta keeps midJointScale to match ReplayRestPose.
                    float bend = bendBase * Mathf.Pow(curlJointScale, j) * sign
                               + (leftDeltas ? flexBase * Mathf.Pow(midJointScale, j) * flexSign : 0f);
                    Quaternion target = Quaternion.Euler(basePose[packedIndex])
                        * Quaternion.AngleAxis(bend, curlAxis)
                        * Quaternion.AngleAxis(j == 0 ? spread : 0f, spreadAxis);
                    Quaternion animated = RecoverAnimated(joints[j]);
                    Quaternion outRot = Quaternion.Slerp(animated, target, weight);
                    joints[j].localRotation = outRot;
                    fingerBaseRot[joints[j]] = animated;
                    fingerOutRot[joints[j]] = outRot;
                }
            }

            if (fingerTrackingDebug && Time.time >= nextFingerTrackLog)
            {
                nextFingerTrackLog = Time.time + 1f;
                Plugin.MyLog.LogInfo($"[FingerTracking] {(dominant ? "dom" : "off")} level={skeleton.skeletalTrackingLevel} curls=[{curls[0]:0.00},{curls[1]:0.00},{curls[2]:0.00},{curls[3]:0.00},{curls[4]:0.00}]");
            }
            return true;
        }

        // ---- FIKA finger sync buffers (read by VRArmSync.Tick; filled at the end of LateUpdate so
        // they hold the rendered joint locals). 15 = 5 fingers x 3 joints in palm child-order, the
        // same flat order FingerSyncApply rebuilds on the observed rig. ----
        public static bool syncLeftFingersActive;
        public static bool syncRightFingersActive;
        public static readonly Vector3[] syncLeftFingerEuler = new Vector3[15];
        public static readonly Vector3[] syncRightFingerEuler = new Vector3[15];

        private void SampleFingerSync()
        {
            // A press check's fingers on the slide (PressCheckHook) are sent too: the gun hand's, or the left one's with the gun in the left hand
            bool pressCheck = VRReloadController.PressCheckProgress > 0f;
            bool pressCheckLeft = pressCheck && VRReloadController.PressCheckOnLeftHand;
            syncLeftFingersActive = (handWeightBlend >= 0.01f || pressCheckLeft) && leftFingerJoints != null;
            syncRightFingersActive = (rightHandWeightBlend >= 0.01f || (pressCheck && !pressCheckLeft)) && rightFingerJoints != null;
            if (syncLeftFingersActive)
                PackFingerEulers(leftFingerJoints, syncLeftFingerEuler);
            if (syncRightFingersActive)
                PackFingerEulers(rightFingerJoints, syncRightFingerEuler);
        }

        private static void PackFingerEulers(Transform[][] fingers, Vector3[] destination)
        {
            int packedIndex = 0;
            for (int fingerIndex = 0; fingerIndex < fingers.Length && packedIndex < destination.Length; fingerIndex++)
            {
                Transform[] joints = fingers[fingerIndex];
                for (int j = 0; j < joints.Length && j < 3 && packedIndex < destination.Length; j++, packedIndex++)
                    destination[packedIndex] = joints[j] != null ? joints[j].localEulerAngles : Vector3.zero;
            }
            for (; packedIndex < destination.Length; packedIndex++)
                destination[packedIndex] = Vector3.zero;
        }

        // Map a finger slot to the SteamVR curl index order [thumb, index, middle, ring, pinky].
        private static int CurlIndex(bool[] isThumb, int fingerIndex)
        {
            if (isThumb != null && fingerIndex < isThumb.Length && isThumb[fingerIndex])
                return 0;
            int k = 1;
            for (int i = 0; i < fingerIndex && isThumb != null && i < isThumb.Length; i++)
                if (!isThumb[i])
                    k++;
            return Mathf.Clamp(k, 0, 4);
        }
    }
}
