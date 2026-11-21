using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.VR
{
    // Makes the rendered arm as long as the player's real one. Holding the height reset with both arms straight
    // out measures it; until then it's estimated from eye height.
    // The bones are moved along the arm instead of scaled: a scale carries down into the hand and shears it, this
    // only stretches the skin between the joints. The arm solver re-reads the bone lengths every frame
    // (IKSolverTrigonometric.OnUpdate), so it just follows.
    internal static class ArmLength
    {
        public static float minWristSpan = 1f;            // m; arms at the sides or held forward stay well under this
        public static float shoulderBelowHead = 0.18f;    // m; same drop OverrideShoulders places the shoulders at
        public static float maxOffShoulderHeight = 0.3f;  // m; how far above or below the shoulders still counts as arms out
        public static float maxSideDot = -0.6f;           // the two arms seen from the head, flat: -1 = exactly opposite sides
        public static float shoulderShareOfSpan = 0.23f;  // the shoulder joints take about this much of the wrist to wrist span
        public static float armPerEyeHeight = 0.337f;     // joint to joint arm length against eye height (body proportions)
        public static float minScale = 0.8f;
        public static float maxScale = 1.3f;              // the skin stretch starts to show past about 15%; this only guards a bad measurement

        // Valve's hand skeleton measured off an Index controller (the [HandAlign] log): right hand, mirrored in X for the left
        private static readonly Vector3 indexWristInController = new Vector3(0.034f, 0.037f, -0.165f);
        private const float OTHER_CONTROLLER_WRIST_BACK = 0.175f;

        private class ArmBones
        {
            public Transform wrist;
            public Transform[] joints;       // elbow, the forearm's twist bones, the palm: each one sits along the arm from its parent
            public Vector3[] naturalLocals;
            public float naturalLength;
        }

        private static readonly ArmBones leftArm = new ArmBones();
        private static readonly ArmBones rightArm = new ArmBones();

        //---------------------------------------------------------------------------------------------------------
        // Called right before the arm solve (the IkProcess postfix), after the animator has run
        public static void Apply()
        {
            if (VRGlobals.ikManager == null)
                return;
            ApplyToArm(leftArm, VRGlobals.ikManager.leftArmIk);
            ApplyToArm(rightArm, VRGlobals.ikManager.rightArmIk);
        }

        private static void ApplyToArm(ArmBones arm, LimbIK armIk)
        {
            if (armIk == null || armIk.solver == null || armIk.solver.bone2?.transform == null || armIk.solver.bone3?.transform == null)
                return;
            if (arm.wrist != armIk.solver.bone3.transform)
                CaptureArm(arm, armIk);
            if (arm.joints == null)
                return;

            float scale = ScaleFor(arm);
            for (int i = 0; i < arm.joints.Length; i++)
                arm.joints[i].localPosition = arm.naturalLocals[i] * scale;
        }

        // Only on a new rig, so the lengths captured are always the model's own and never an already lengthened arm
        private static void CaptureArm(ArmBones arm, LimbIK armIk)
        {
            Transform elbow = armIk.solver.bone2.transform;
            arm.wrist = armIk.solver.bone3.transform;
            arm.joints = null;

            int count = 1;
            Transform walk = arm.wrist;
            while (walk != null && walk != elbow)
            {
                count++;
                walk = walk.parent;
            }
            if (walk == null)
            {
                Plugin.MyLog.LogWarning($"[ArmLength] {arm.wrist.name} isn't below {elbow.name}, leaving this arm alone");
                return;
            }

            Transform[] joints = new Transform[count];
            Vector3[] locals = new Vector3[count];
            float length = 0f;
            walk = arm.wrist;
            for (int i = count - 1; i >= 0; i--)
            {
                joints[i] = walk;
                locals[i] = walk.localPosition;
                length += locals[i].magnitude;
                walk = walk.parent;
            }
            arm.joints = joints;
            arm.naturalLocals = locals;
            arm.naturalLength = length;
        }

        private static float ScaleFor(ArmBones arm)
        {
            if (VRSettings.GetLeftHandedMode() || arm.naturalLength <= 0f)
                return 1f;
            float wantedLength = WantedLength();
            return wantedLength > 0f ? Mathf.Clamp(wantedLength / arm.naturalLength, minScale, maxScale) : 1f;
        }

        private static float WantedLength()
        {
            float measured = VRSettings.GetArmLength();
            if (measured > 0f)
                return measured;
            // Sitting says nothing about how tall you are
            if (VRSettings.GetSeatedMode() || VRGlobals.vrPlayer == null)
                return 0f;
            return VRGlobals.vrPlayer.initPos.y * armPerEyeHeight;
        }

        //---------------------------------------------------------------------------------------------------------
        // Called by the height reset. With the arms straight out it also measures the arm; with them anywhere else
        // the saved length stays. Hand action poses + the raw headset pose: the only head and hand pair that's in the
        // same space in the hideout too.
        public static void TryMeasure()
        {
            if (VRSettings.GetLeftHandedMode() || !HeadTracking.RawHmdPose(out Vector3 head, out _))
                return;

            Vector3 leftWrist = WristOf(VRInput.LeftHandPose, SteamVR_Input_Sources.LeftHand, -1f);
            Vector3 rightWrist = WristOf(VRInput.RightHandPose, SteamVR_Input_Sources.RightHand, 1f);
            float span = Vector3.Distance(leftWrist, rightWrist);
            float shoulderHeight = head.y - shoulderBelowHead;
            float sideDot = Vector3.Dot(FlatDirection(leftWrist - head), FlatDirection(rightWrist - head));

            if (span < minWristSpan || sideDot > maxSideDot
                || Mathf.Abs(leftWrist.y - shoulderHeight) > maxOffShoulderHeight
                || Mathf.Abs(rightWrist.y - shoulderHeight) > maxOffShoulderHeight)
            {
                Plugin.MyLog.LogInfo($"[ArmLength] Height reset only, arms not out: wrist span {span:0.00}, sides {sideDot:0.00}, "
                    + $"wrists from shoulder height {leftWrist.y - shoulderHeight:0.00} / {rightWrist.y - shoulderHeight:0.00}");
                return;
            }

            float armLength = span * (1f - shoulderShareOfSpan) / 2f;
            VRSettings.SetArmLength(armLength);
            VRInput.Haptic.Execute(0f, 0.2f, 60f, 0.6f, SteamVR_Input_Sources.LeftHand);
            VRInput.Haptic.Execute(0f, 0.2f, 60f, 0.6f, SteamVR_Input_Sources.RightHand);

            string shoulderSpan = "?";
            if (VRGlobals.ikManager?.leftArmIk?.solver?.bone1?.transform != null && VRGlobals.ikManager.rightArmIk?.solver?.bone1?.transform != null)
                shoulderSpan = Vector3.Distance(VRGlobals.ikManager.leftArmIk.solver.bone1.transform.position,
                    VRGlobals.ikManager.rightArmIk.solver.bone1.transform.position).ToString("0.00");
            Plugin.MyLog.LogInfo($"[ArmLength] Measured: wrist span {span:0.00}, arm {armLength:0.000}, Tarkov's arm {rightArm.naturalLength:0.000}, "
                + $"scale {ScaleFor(rightArm):0.00}, rendered shoulder span {shoulderSpan}");
        }

        private static Vector3 WristOf(VRPose pose, SteamVR_Input_Sources source, float side)
        {
            Vector3 wristInController = VRGlobals.vrControllerType == "index"
                ? new Vector3(indexWristInController.x * side, indexWristInController.y, indexWristInController.z)
                : new Vector3(0f, 0f, -OTHER_CONTROLLER_WRIST_BACK);
            return pose.GetLocalPosition(source) + pose.GetLocalRotation(source) * wristInController;
        }

        private static Vector3 FlatDirection(Vector3 direction)
        {
            direction.y = 0f;
            return direction.normalized;
        }
    }
}
