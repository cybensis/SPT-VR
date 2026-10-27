using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Settings;

namespace TarkovVR.Source.Player.Interactions
{
    // SteamVR skeletal finger tracking (Index/Knuckles).
    //
    // The manifest already binds SkeletonLeftHand/SkeletonRightHand, and SteamVR exposes per-finger
    // CURL scalars (0 = open .. 1 = closed) + a skeletalTrackingLevel. We CURL FROM THE BAKED OPEN
    // POSE (restPoseEuler / rightRestPoseEuler - the good authored hands) toward a fist by bending
    // each joint around curlAxis proportional to its curl. NOTE (round 1 fix): the first cut drove
    // the procedural flatten (ApplyFlatFlex) instead - the same imperfect pose the bakes REPLACED -
    // which slanted the fingers and over-curled the thumb. Bending from the bake fixes both: curl 0 is
    // exactly the authored open pose. No 31-bone retarget (a curl is a scalar; the bake handles each
    // bone's frame). Gated on skeletalTrackingLevel >= Partial so ONLY Index/Knuckles track; every
    // other controller (Estimated) keeps the baked pose.
    internal partial class HandsInteractionController
    {
        public static bool fingerTracking = true;          // master: Index skeletal finger tracking
        public static float curlToDeg = 90f;               // full curl swing at the base joint (tapers down by curlJointScale)
        public static float thumbCurlToDeg = 28f;          // thumbs curl far less than fingers (40 read as "a bit too much")
        public static float curlJointScale = 0.8f;         // per-joint curl falloff - own knob, NOT midJointScale (0.6): at 0.6 the tips got 36% of the bend and never closed into the fist
        // Fingers RESTING on an Index controller do NOT read curl 0 - they sit ~0.4-0.6 (thumb higher,
        // it's on the stick). The authored rest pose maps to the RESTING curl: at rest the hand shows
        // exactly the bake ("broke my poses" round: mapping the bake to curl 0 added the resting curl
        // on top); squeezing curls toward a fist; lifting the fingers straight opens PAST the bake,
        // capped by maxExtendDeg so it can't hyperextend.
        public static float curlRestLevel = 0.45f;
        public static float thumbCurlRestLevel = 0.6f;
        public static float maxExtendDeg = 12f;            // deg past the bake when fingers lift off the controller
        public static Vector3 curlAxis = Vector3.forward;  // the joints' flexion axis (local); ReplayRestPose bends the idle flex around forward too
        // BOTH rigs curl with POSITIVE rotation about forward (the old right -1 was a mis-carry of
        // rightFlexSign, which compensated for the NEGATIVE idle-flex constants - headset symptom:
        // right hand hyperextended). Flip one only if a hand demonstrably closes backward.
        public static float leftCurlSign = 1f;
        public static float rightCurlSign = 1f;
        public static bool fingerTrackingDebug = false;    // 1/sec log of tracking level + curls per hand

        private float nextFingerTrackLog;

        // The skeleton action for a logical hand. Dominant = the physical RIGHT controller unless
        // left-handed mode swaps the hands.
        private static SteamVR_Action_Skeleton HandSkeleton(bool dominant)
        {
            bool physRight = dominant ^ VRSettings.GetLeftHandedMode();
            return physRight ? SteamVR_Actions._default.SkeletonRightHand
                             : SteamVR_Actions._default.SkeletonLeftHand;
        }

        // True only when the controller actually senses fingers (Index = Partial/Full). Estimated
        // (standard controllers - curl faked from grip/trigger) keeps the baked pose.
        private static bool SkeletalActive(bool dominant)
        {
            if (!fingerTracking)
                return false;
            var sk = HandSkeleton(dominant);
            return sk != null && sk.active
                && (int)sk.skeletalTrackingLevel >= (int)EVRSkeletalTrackingLevel.VRSkeletalTracking_Partial;
        }

        // Curl the fingers from their BAKED open pose (basePose, packed 3-per-finger in fingerJoints
        // order) toward a fist, driven by the live curls. leftDeltas layers the LEFT hand's idle
        // flex + spread deltas on the bake first (exactly ReplayRestPose's composition) - the left's
        // authored look IS bake+deltas, and skipping them under tracking visibly changed its rest pose
        // (headset round 3); the right's bake is standalone. Returns false if there's no curl data
        // this frame (caller falls back to the static baked pose).
        private bool ApplySkeletalCurls(bool dominant, Transform[][] fingerJoints, bool[] isThumb, Vector3[] basePose, float sign, float weight, bool leftDeltas)
        {
            var sk = HandSkeleton(dominant);
            float[] curls = sk != null ? sk.fingerCurls : null;
            if (curls == null || curls.Length < 5 || fingerJoints == null || basePose == null)
                return false;

            int idx = 0;
            for (int f = 0; f < fingerJoints.Length; f++)
            {
                Transform[] joints = fingerJoints[f];
                bool thumb = isThumb != null && f < isThumb.Length && isThumb[f];
                float gain = thumb ? thumbCurlToDeg : curlToDeg;
                float rest = thumb ? thumbCurlRestLevel : curlRestLevel;
                // recentred: resting curl = the authored pose exactly; clamp the open side
                float bendBase = Mathf.Max((curls[CurlIndex(isThumb, f)] - rest) * gain, -maxExtendDeg);
                float flexBase = leftDeltas ? PerFingerFlex(f) : 0f;
                float spread = leftDeltas && leftFingerSpread != null && f < leftFingerSpread.Length
                    ? leftFingerSpread[f] * fingerSpreadDeg : 0f;
                for (int j = 0; j < joints.Length && j < 3; j++, idx++)
                {
                    if (joints[j] == null || idx >= basePose.Length)
                        continue;
                    // The curl uses its own (gentler) taper so the TIPS close into the fist; the left's
                    // idle-flex delta keeps midJointScale to match ReplayRestPose's look exactly.
                    float bend = bendBase * Mathf.Pow(curlJointScale, j) * sign
                               + (leftDeltas ? flexBase * Mathf.Pow(midJointScale, j) * flexSign : 0f);
                    Quaternion target = Quaternion.Euler(basePose[idx])
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
                Plugin.MyLog.LogInfo($"[FingerTracking] {(dominant ? "dom" : "off")} level={sk.skeletalTrackingLevel} curls=[{curls[0]:0.00},{curls[1]:0.00},{curls[2]:0.00},{curls[3]:0.00},{curls[4]:0.00}]");
            }
            return true;
        }

        // ---- FIKA finger sync buffers (read by VRArmSync.Tick; filled at the END of LateUpdate so
        // they hold the RENDERED joint locals - the frame rule again). 15 = 5 fingers x 3 joints in
        // palm child-order, the same flat order FingerSyncApply reconstructs on the observed rig. ----
        public static bool syncLeftFingersActive;
        public static bool syncRightFingersActive;
        public static readonly Vector3[] syncLeftFingerEuler = new Vector3[15];
        public static readonly Vector3[] syncRightFingerEuler = new Vector3[15];

        private void SampleFingerSync()
        {
            syncLeftFingersActive = handWeightBlend >= 0.01f && leftFingerJoints != null;
            syncRightFingersActive = rightHandWeightBlend >= 0.01f && rightFingerJoints != null;
            if (syncLeftFingersActive)
                PackFingerEulers(leftFingerJoints, syncLeftFingerEuler);
            if (syncRightFingersActive)
                PackFingerEulers(rightFingerJoints, syncRightFingerEuler);
        }

        private static void PackFingerEulers(Transform[][] fingers, Vector3[] dst)
        {
            int idx = 0;
            for (int f = 0; f < fingers.Length && idx < dst.Length; f++)
            {
                Transform[] joints = fingers[f];
                for (int j = 0; j < joints.Length && j < 3 && idx < dst.Length; j++, idx++)
                    dst[idx] = joints[j] != null ? joints[j].localEulerAngles : Vector3.zero;
            }
            for (; idx < dst.Length; idx++)
                dst[idx] = Vector3.zero;
        }

        // Map a finger slot to the SteamVR curl index order [thumb, index, middle, ring, pinky].
        private static int CurlIndex(bool[] isThumb, int f)
        {
            if (isThumb != null && f < isThumb.Length && isThumb[f])
                return 0;
            int k = 1;
            for (int i = 0; i < f && isThumb != null && i < isThumb.Length; i++)
                if (!isThumb[i])
                    k++;
            return Mathf.Clamp(k, 0, 4);
        }
    }
}
