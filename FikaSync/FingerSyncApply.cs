using System;
using System.Collections.Generic;
using Fika.Core.Main.Players;
using HarmonyLib;
using UnityEngine;

namespace SptVrFikaSync
{
    // Applies remote VR players' finger poses in an ObservedPlayer.LateUpdate postfix. It runs after FIKA's visual pass, so these
    // rotations override the observed animator. Joints are matched by order, not name: 3 per finger, in palm child order under
    // "Base HumanLPalm" / "Base HumanRPalm", the same order the sender uses (every player has the same EFT hands prefab).
    // A hand is driven only while its Active flag is set and the data is fresh. Otherwise the animator keeps the fingers.
    [HarmonyPatch]
    internal static class FingerSyncApply
    {
        public static float staleTimeout = 0.5f;   // s without a packet -> hand back to the animator

        private class RemoteFingers
        {
            public bool leftActive, rightActive;
            public readonly Vector3[] left = new Vector3[VRFingersPacket.JointCount];
            public readonly Vector3[] right = new Vector3[VRFingersPacket.JointCount];
            public float recvTime;
        }
        private static readonly Dictionary<int, RemoteFingers> remote = new Dictionary<int, RemoteFingers>();

        private class JointCache
        {
            public Transform[] left;    // flat 15 (3 per finger, palm child-order); entries may be null
            public Transform[] right;
        }
        private static readonly Dictionary<int, JointCache> joints = new Dictionary<int, JointCache>();

        public static void ResetState()
        {
            remote.Clear();
            joints.Clear();
        }

        public static void Store(VRFingersPacket p)
        {
            if (!remote.TryGetValue(p.NetId, out RemoteFingers rf))
            {
                rf = new RemoteFingers();
                remote[p.NetId] = rf;
            }
            rf.leftActive = p.LeftActive;
            rf.rightActive = p.RightActive;
            if (p.LeftActive && p.Left != null)
                Array.Copy(p.Left, rf.left, VRFingersPacket.JointCount);
            if (p.RightActive && p.Right != null)
                Array.Copy(p.Right, rf.right, VRFingersPacket.JointCount);
            rf.recvTime = Time.time;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ObservedPlayer), nameof(ObservedPlayer.LateUpdate))]
        private static void DriveRemoteFingers(ObservedPlayer __instance)
        {
            try
            {
                if (!FikaVrSync.enableFingerSync || __instance.IsAI)
                    return;
                if (!remote.TryGetValue(__instance.NetId, out RemoteFingers rf))
                    return;
                if (Time.time - rf.recvTime > staleTimeout || (!rf.leftActive && !rf.rightActive))
                    return;

                JointCache jc = ResolveJoints(__instance);
                if (jc == null)
                    return;
                if (rf.leftActive && jc.left != null)
                    ApplyHand(jc.left, rf.left);
                if (rf.rightActive && jc.right != null)
                    ApplyHand(jc.right, rf.right);
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        private static void ApplyHand(Transform[] handJoints, Vector3[] eulers)
        {
            for (int i = 0; i < handJoints.Length && i < eulers.Length; i++)
                if (handJoints[i] != null)
                    handJoints[i].localRotation = Quaternion.Euler(eulers[i]);
        }

        // Finds and caches the observed player's finger joints. The cache entry is dropped when the rig is rebuilt
        // (its first cached bone is destroyed).
        private static JointCache ResolveJoints(ObservedPlayer p)
        {
            if (joints.TryGetValue(p.NetId, out JointCache jc))
            {
                bool alive = (jc.left != null && jc.left.Length > 0 && jc.left[0] != null)
                          || (jc.right != null && jc.right.Length > 0 && jc.right[0] != null);
                if (alive)
                    return jc;
                joints.Remove(p.NetId);
            }

            Transform root = p.Transform?.Original;
            if (root == null)
                return null;
            Transform leftPalm = FindDeep(root, "Base HumanLPalm");
            Transform rightPalm = FindDeep(root, "Base HumanRPalm");
            if (leftPalm == null && rightPalm == null)
                return null;   // rig not spawned yet - retry next frame (uncached)

            jc = new JointCache
            {
                left = leftPalm != null ? FlattenFingers(leftPalm) : null,
                right = rightPalm != null ? FlattenFingers(rightPalm) : null
            };
            joints[p.NetId] = jc;
            return jc;
        }

        // 3 joints per finger (the base joint, then its first child, then that one's first child), in palm child order.
        // This must match the sender's order exactly.
        private static Transform[] FlattenFingers(Transform palm)
        {
            var flat = new Transform[VRFingersPacket.JointCount];
            int idx = 0;
            for (int i = 0; i < palm.childCount && idx < flat.Length; i++)
            {
                Transform j = palm.GetChild(i);
                for (int n = 0; n < 3 && idx < flat.Length; n++, idx++)
                {
                    flat[idx] = j;
                    j = j != null && j.childCount > 0 ? j.GetChild(0) : null;
                }
            }
            return flat;
        }

        private static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name)
                return t;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform r = FindDeep(t.GetChild(i), name);
                if (r != null)
                    return r;
            }
            return null;
        }

        private static bool logged;
        private static void LogOnce(Exception e)
        {
            if (logged)
                return;
            logged = true;
            FikaSyncPlugin.Log?.LogWarning($"[FingerSync] apply failed: {e}");
        }
    }
}
