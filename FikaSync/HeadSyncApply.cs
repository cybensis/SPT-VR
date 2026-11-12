using System;
using System.Collections.Generic;
using Fika.Core.Main.Players;
using HarmonyLib;
using TarkovVR.Source.Player.Body;
using UnityEngine;

namespace SptVrFikaSync
{
    // A VR player's head and upper body on the other players' screens: the headset pose in their body's frame (VRHeadPacket) through the
    // same solve the VR player's own body runs (UpperBodySolver, linked in from SPT-VR): the chest turns toward the head, the spine bends to
    // put the head where theirs is, the head turns to the headset. First among the ObservedPlayer.LateUpdate postfixes: the arm, weapon and
    // eating drives after it place the hands on the bent chest (their poses come in that chest's frame).
    [HarmonyPatch]
    internal static class HeadSyncApply
    {
        // Exponential smoothing of the head pose (the ~20 Hz packets), as the arms (ArmSyncApply.smoothRate). 0 = snap.
        public static float smoothRate = 18f;
        public static float staleTimeout = 0.5f;    // s without a packet before it lets go
        public static float blendTime = 0.25f;      // s it eases in and out
        public static float maxDistance = 80f;      // m from the camera past which the body stays as animated
        public static bool logRig = true;           // one line per player's rig: the bones it turns, or why it can't

        private sealed class Remote
        {
            public bool active;
            public Vector3 position;
            public Quaternion rotation;
            public float receivedAt;
            public bool shown;
            public Vector3 shownPosition;
            public Quaternion shownRotation;
            public float weight;
            public UpperBodySolver.Rig rig;
        }

        private static readonly Dictionary<int, Remote> remotes = new Dictionary<int, Remote>();
        private static bool loggedError;

        public static void ResetState() => remotes.Clear();

        public static void Store(VRHeadPacket p)
        {
            if (!remotes.TryGetValue(p.NetId, out Remote remote))
            {
                remote = new Remote();
                remotes[p.NetId] = remote;
            }
            remote.active = p.Active;
            remote.position = p.Position;
            remote.rotation = p.Rotation;
            remote.receivedAt = Time.time;
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(ObservedPlayer), nameof(ObservedPlayer.LateUpdate))]
        private static void FollowSyncedHead(ObservedPlayer __instance)
        {
            try
            {
                if (__instance.IsAI || !FikaVrSync.enableHeadSync || !remotes.TryGetValue(__instance.NetId, out Remote remote))
                    return;
                Transform root = __instance.Transform != null ? __instance.Transform.Original : null;
                if (root == null || __instance.PlayerBones == null)
                    return;
                if (remote.rig == null || remote.rig.bones != __instance.PlayerBones || remote.rig.root != root)
                {
                    remote.rig = UpperBodySolver.Build(__instance.PlayerBones, root);
                    if (logRig)
                        FikaSyncPlugin.Log?.LogInfo($"[HeadSync] {__instance.Profile?.Nickname}: {(remote.rig.valid ? "head and upper body follow their headset" : "off")}: {remote.rig.description}");
                }

                var movement = __instance.MovementContext;
                Camera viewer = Camera.main;
                bool near = viewer == null || (viewer.transform.position - root.position).sqrMagnitude < maxDistance * maxDistance;
                bool wanted = remote.active && Time.time - remote.receivedAt < staleTimeout && near
                    && __instance.HealthController != null && __instance.HealthController.IsAlive
                    && movement != null && !movement.IsInPronePose && !movement.IsInMountedState && !__instance.IsSprintEnabled;
                remote.weight = Mathf.MoveTowards(remote.weight, wanted ? 1f : 0f, Time.deltaTime / Mathf.Max(blendTime, 0.01f));

                float follow = smoothRate > 0f ? 1f - Mathf.Exp(-smoothRate * Time.deltaTime) : 1f;
                if (!remote.shown)
                {
                    remote.shownPosition = remote.position;
                    remote.shownRotation = remote.rotation;
                    remote.shown = true;
                }
                else
                {
                    remote.shownPosition = Vector3.Lerp(remote.shownPosition, remote.position, follow);
                    remote.shownRotation = Quaternion.Slerp(remote.shownRotation, remote.rotation, follow);
                }
                UpperBodySolver.Solve(remote.rig, root.position + root.rotation * remote.shownPosition, root.rotation * remote.shownRotation, remote.weight);
            }
            catch (Exception e)
            {
                if (!loggedError)
                {
                    loggedError = true;
                    FikaSyncPlugin.Log?.LogError($"[HeadSync] {e}");
                }
            }
        }
    }
}
