using System.Collections.Generic;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    internal partial class HandsInteractionController
    {
        // One hand's fingers for the pose passes (UpdateLeftHandFingers / UpdateRightHandFingers and the layers they call: natural
        // hands, skeletal curls, the bake, the grasp): its joint chains under the palm, which chain is the thumb, and how far the
        // mod's pose is blended over the animator's.
        private sealed class FingerHand
        {
            public readonly bool dominant;       // the right hand's bones (the pose methods' dominant = true)
            public bool resolved;                // the chains have been looked for (Resolve*Fingers)
            public Transform[][] joints;         // [finger][joint] under the palm bone; null until found
            public bool[] isThumb;               // parallel to joints
            public float[] spread;               // left only: signed centered ordinal per finger (the knuckle fan)
            public float weight;                 // eased override weight (0..1)

            public FingerHand(bool dominant)
            {
                this.dominant = dominant;
            }
        }

        private readonly FingerHand leftFingers = new FingerHand(false);
        private readonly FingerHand rightFingers = new FingerHand(true);

        // A hand's fingers, looked for first if they haven't been
        private FingerHand ResolvedFingers(bool dominant)
        {
            FingerHand hand = dominant ? rightFingers : leftFingers;
            if (!hand.resolved)
            {
                if (dominant)
                    ResolveRightFingers();
                else
                    ResolveLeftFingers();
            }
            return hand;
        }

        private static float CurlSign(FingerHand hand) => hand.dominant ? rightCurlSign : leftCurlSign;

        // The hand's finger chains under its palm bone. False (joints left null) without a palm or fingers.
        private bool ResolveFingerChains(FingerHand hand, Transform palm, List<bool> thumbs)
        {
            hand.resolved = true;
            hand.joints = null;
            if (palm == null)
                return false;
            var fingers = new List<Transform[]>();
            FingerChainsUnder(palm, fingers, thumbs);
            if (fingers.Count == 0)
                return false;
            hand.joints = fingers.ToArray();
            hand.isThumb = thumbs.ToArray();
            return true;
        }

        // Blends one joint from its animated pose toward target by weight, and records both so the next frame's RecoverAnimated
        // sees through our own write (no accumulation)
        private void BlendJoint(Transform joint, Quaternion target, float weight) => BlendJoint(joint, RecoverAnimated(joint), target, weight);

        private void BlendJoint(Transform joint, Quaternion animated, Quaternion target, float weight)
        {
            Quaternion outRot = Quaternion.Slerp(animated, target, weight);
            joint.localRotation = outRot;
            fingerBaseRot[joint] = animated;
            fingerOutRot[joint] = outRot;
        }

        //--- Authoring a rest-pose bake (UnityExplorer) -----------------------------
        // The local eulers of every joint, 3 per finger in joints order (the order the bake replays read), logged as a paste-ready
        // array for arrayName: as drawn now, or (animatedPose) the animator's own pose under the mod's (RecoverAnimated: right even
        // on a frame the animator didn't rewrite the fingers, when they still hold the mod's last write)
        private Vector3[] CaptureFingerPose(FingerHand hand, bool animatedPose, string logLabel, string arrayName)
        {
            var list = new List<Vector3>();
            var builder = new System.Text.StringBuilder();
            builder.Append($"[{logLabel}] {arrayName} = new Vector3[] {{\n");
            for (int fingerIndex = 0; fingerIndex < hand.joints.Length; fingerIndex++)
            {
                Transform[] joints = hand.joints[fingerIndex];
                bool isThumb = hand.isThumb != null && fingerIndex < hand.isThumb.Length && hand.isThumb[fingerIndex];
                for (int j = 0; j < joints.Length && j < 3; j++)
                {
                    Vector3 euler = joints[j] == null ? Vector3.zero
                        : animatedPose ? RecoverAnimated(joints[j]).eulerAngles : joints[j].localEulerAngles;
                    list.Add(euler);
                    builder.Append($"    new Vector3({euler.x:F2}f, {euler.y:F2}f, {euler.z:F2}f), // {(isThumb ? "thumb" : ("finger" + fingerIndex))} joint{j}\n");
                }
            }
            builder.Append("};");
            Plugin.MyLog.LogInfo(builder.ToString());
            return list.ToArray();
        }

        private static void LogFingerPose(string logLabel, string arrayName, Vector3[] pose)
        {
            if (pose == null)
            {
                Plugin.MyLog.LogInfo($"[{logLabel}] {arrayName} is null.");
                return;
            }
            var builder = new System.Text.StringBuilder();
            builder.Append($"[{logLabel}] {arrayName} = new Vector3[] {{\n");
            for (int i = 0; i < pose.Length; i++)
            {
                Vector3 euler = pose[i];
                builder.Append($"    new Vector3({euler.x:F2}f, {euler.y:F2}f, {euler.z:F2}f), // index {i}\n");
            }
            builder.Append("};");
            Plugin.MyLog.LogInfo(builder.ToString());
        }

        // Live per-joint editing of a bake: changing editJoint loads that joint's euler into editEuler; while it stays, editEuler
        // is written back every frame. -1 = off.
        private static void EditFingerPoseLive(Vector3[] pose, ref int editJoint, ref Vector3 editEuler, ref int lastEditJoint, string logLabel, string captureFlag)
        {
            if (editJoint < 0)
            {
                lastEditJoint = -2;
                return;
            }
            if (pose == null || editJoint >= pose.Length)
            {
                if (lastEditJoint != editJoint)
                {
                    lastEditJoint = editJoint;
                    Plugin.MyLog.LogWarning($"[{logLabel}] no bake to edit at joint {editJoint} - set {captureFlag}=true first (while the pose looks good).");
                }
                return;
            }
            if (editJoint != lastEditJoint)
            {
                lastEditJoint = editJoint;
                editEuler = pose[editJoint];
            }
            else
            {
                pose[editJoint] = editEuler;
            }
        }
    }
}
