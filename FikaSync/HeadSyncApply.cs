using System;
using System.Collections.Generic;
using Fika.Core.Main.Players;
using HarmonyLib;
using TarkovVR.Source.Player.Body;
using UnityEngine;

namespace SptVrFikaSync
{
    // Poses a VR player's head and upper body on the other players' screens. VRHeadPacket carries the headset pose in the body's frame, and it
    // goes through the same solve the VR player's own body runs (UpperBodySolver, linked in from SPT-VR): the chest (and the hips, while standing
    // still) turns toward the head, the spine bends to put the head where the headset is, and the head turns to match the headset.
    // Runs first among the ObservedPlayer.LateUpdate postfixes, so the arm, weapon and eating drives after it start from the bent shoulders.
    [HarmonyPatch]
    internal static class HeadSyncApply
    {
        // Exponential smoothing rate of the head pose (packets arrive at ~20 Hz), like the arms' ArmSyncApply.smoothRate. 0 = snap.
        public static float smoothRate = 18f;
        public static float staleTimeout = 0.5f;    // s without a packet before the solve fades out
        public static float blendTime = 0.25f;      // s to fade in and out
        public static float maxDistance = 80f;      // m from the camera past which the body stays as animated
        public static bool logRig = true;           // one line per player's rig: the bones it turns, or why it can't
        // The hips square to the body (whose facing follows the head) only while standing still (UpperBodySolver.hipShare). Full up to
        // hipStillSpeed, none from hipMovingSpeed (m/s across the ground; walking legs point the way you walk), eased over hipBlendTime.
        // Off while prone, on a mounted gun or sprinting.
        public static bool turnHips = true;
        public static float hipStillSpeed = 0.3f;
        public static float hipMovingSpeed = 1.0f;
        public static float hipBlendTime = 0.3f;
        // A VR player's body turns with their look as it does on their own screen (SPT-VR sets the local player's trunk limit to 0).
        // Tarkov gives the other players a 45 deg limit (EFTHardSettings.HANDS_TO_BODY_MAX_ANGLE): standing still their body stays up to
        // 45 deg off their look, and every pose they send in their body's frame (head, hands, gun) was drawn turned by that much.
        public static bool turnBodyWithLook = true;

        private static readonly HashSet<int> trunkFreed = new HashSet<int>();

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
            public float hipWeight;
            public bool loggedHips;
            public Vector3 lastRootPosition;
            public bool haveRootPosition;
            public UpperBodySolver.Rig rig;
        }

        private static readonly Dictionary<int, Remote> remotes = new Dictionary<int, Remote>();
        private static bool loggedError;

        public static void ResetState()
        {
            remotes.Clear();
            trunkFreed.Clear();
        }

        // Player.TrunkRotationLimit: the body turns once the look is this far off it, only by the excess (MovementState.ProcessRotation).
        // Set every frame: Tarkov's prone transitions write it too.
        private static void TurnBodyWithLook(ObservedPlayer player)
        {
            if (turnBodyWithLook)
            {
                player.TrunkRotationLimit = 0f;
                trunkFreed.Add(player.NetId);
            }
            else if (trunkFreed.Remove(player.NetId))
            {
                player.TrunkRotationLimit = EFTHardSettings.Instance.HANDS_TO_BODY_MAX_ANGLE;
            }
        }

        // A player that sends its headset pose is a VR player.
        internal static bool IsVrPlayer(int netId) => remotes.ContainsKey(netId);

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
                if (__instance.IsAI)
                    return;
                bool vrPlayer = remotes.ContainsKey(__instance.NetId) || ArmSyncApply.IsVrPlayer(__instance.NetId);
                if (vrPlayer)
                    TurnBodyWithLook(__instance);
                if (!FikaVrSync.enableHeadSync || !remotes.TryGetValue(__instance.NetId, out Remote remote))
                    return;
                Transform root = __instance.Transform != null ? __instance.Transform.Original : null;
                if (root == null || __instance.PlayerBones == null)
                    return;
                if (remote.rig == null || remote.rig.bones != __instance.PlayerBones || remote.rig.root != root)
                {
                    remote.rig = UpperBodySolver.Build(__instance.PlayerBones, root);
                    remote.loggedHips = false;
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

                Vector3 moved = remote.haveRootPosition ? root.position - remote.lastRootPosition : Vector3.zero;
                moved.y = 0f;
                remote.lastRootPosition = root.position;
                remote.haveRootPosition = true;
                float speed = Time.deltaTime > 1e-4f ? moved.magnitude / Time.deltaTime : 0f;
                float hipsWanted = turnHips && wanted && movement.StationaryWeapon == null
                    ? 1f - Mathf.InverseLerp(hipStillSpeed, Mathf.Max(hipMovingSpeed, hipStillSpeed + 0.01f), speed) : 0f;
                remote.hipWeight = Mathf.MoveTowards(remote.hipWeight, hipsWanted, Time.deltaTime / Mathf.Max(hipBlendTime, 0.01f));

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
                UpperBodySolver.Solve(remote.rig, root.position + root.rotation * remote.shownPosition, root.rotation * remote.shownRotation, remote.weight, remote.hipWeight);
                if (logRig && !remote.loggedHips && remote.rig.valid && remote.weight >= 1f && remote.hipWeight >= 1f)
                {
                    remote.loggedHips = true;
                    FikaSyncPlugin.Log?.LogInfo($"[HeadSync] {__instance.Profile?.Nickname}: hips squared to their body standing: the animation had them {remote.rig.hipsOffBody:F0} deg off it, turned {remote.rig.hipsTurned:F0}; "
                        + $"the chest then {remote.rig.chestOffBody:F0} deg off it (squareChest {UpperBodySolver.squareChest}); body {Mathf.DeltaAngle(root.eulerAngles.y, __instance.MovementContext.Yaw):F0} deg off their look");
                }
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
