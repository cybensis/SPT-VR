using System.Collections.Generic;
using TarkovVR.Source.Player.Body;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // The free left hand's open pose is the right hand's baked open pose (rightRestPoseEuler), mirrored by HandMirror (each joint's
    // turn from its bind pose is reflected through the body's centre plane), so both hands show the same pose. It replaces the left
    // bake (restPoseEuler) and its fixed idle flex / spread deltas. The loot pointer's "ready" curl still goes on top, and Index finger
    // tracking curls from it. Recomputed every frame (15 joints), so a live edit of the right bake shows on both hands.
    // mirrorOpenHand = false uses the left bake.
    internal partial class HandsInteractionController
    {
        public static bool mirrorOpenHand = true;

        private static readonly Dictionary<string, Quaternion> mirrorRightLocals = new Dictionary<string, Quaternion>();
        private static readonly Dictionary<string, Quaternion> mirrorLeftLocals = new Dictionary<string, Quaternion>();
        private Vector3[] mirroredLeftRest;

        // The pose is in ReplayRestPose's order: the left hand's fingers in the order found, three joints each
        private bool TryGetMirroredLeftRest(out Vector3[] pose)
        {
            pose = null;
            if (!mirrorOpenHand || rightRestPoseEuler == null || rightRestPoseEuler.Length == 0 || leftFingers.joints == null)
                return false;
            if (!rightFingers.resolved)
                ResolveRightFingers();
            if (rightFingers.joints == null)
                return false;

            mirrorRightLocals.Clear();
            int index = 0;
            foreach (Transform[] joints in rightFingers.joints)
            {
                for (int joint = 0; joint < joints.Length && joint < 3; joint++, index++)
                {
                    if (joints[joint] != null && index < rightRestPoseEuler.Length)
                        mirrorRightLocals[DigitOf(joints[joint].name)] = Quaternion.Euler(rightRestPoseEuler[index]);
                }
            }
            if (!HandMirror.TryMirrorRightToLeft(mirrorRightLocals, mirrorLeftLocals))
                return false;

            int count = 0;
            foreach (Transform[] joints in leftFingers.joints)
                count += Mathf.Min(joints.Length, 3);
            if (mirroredLeftRest == null || mirroredLeftRest.Length != count)
                mirroredLeftRest = new Vector3[count];
            index = 0;
            foreach (Transform[] joints in leftFingers.joints)
            {
                for (int joint = 0; joint < joints.Length && joint < 3; joint++, index++)
                {
                    if (joints[joint] != null && mirrorLeftLocals.TryGetValue(DigitOf(joints[joint].name), out Quaternion local))
                        mirroredLeftRest[index] = local.eulerAngles;
                    else
                        mirroredLeftRest[index] = restPoseEuler != null && index < restPoseEuler.Length ? restPoseEuler[index] : Vector3.zero;
                }
            }
            pose = mirroredLeftRest;
            return true;
        }

        // "Base HumanLDigit21" -> "Digit21" (the same on both hands)
        private static string DigitOf(string boneName)
        {
            int digitStart = boneName.IndexOf("Digit", System.StringComparison.Ordinal);
            return digitStart >= 0 ? boneName.Substring(digitStart) : boneName;
        }
    }
}
