using EFT;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // The local VR player's upper body solved to the headset (UpperBodySolver), right before the shoulders are placed (UpperBodyPatches:
    // PlayerBones.SetShoulders). Off while prone, in a run with the run animation and on a mounted gun (their animations pose the body).
    // The solved chest is kept in the body's frame: the co-op sync sends the hands in it (VRArmSync.SyncChest), whenever in the frame that
    // runs, and the other players' copy of you is bent the same way (HeadSyncApply). Your own bones keep it only with poseLocalBody.
    internal static class LocalUpperBody
    {
        public static bool enabled = true;
        // true = your own skeleton keeps the solved pose (shadow + hitboxes follow the head). In first person the body sits behind the
        // camera, so it bends far forward, and Tarkov hangs its hands container (PlayerSpring) and the body gun spots off the ribcage:
        // the held and slung gun move out in front. false = solved only for the co-op sync's chest frame, the bones put back.
        public static bool poseLocalBody = false;
        public static float blendTime = 0.25f;      // s it eases in and out
        public static bool logRig = true;           // one line per rig: the bones it turns, or why it can't

        private static UpperBodySolver.Rig rig;
        private static float weight;
        private static int solvedFrame = -10;
        private static Vector3 chestInRootPosition;
        private static Quaternion chestInRootRotation;
        private static Vector3 chestScale = Vector3.one;
        private static Transform syncChest;

        public static void Solve(PlayerBones bones)
        {
            EFT.Player player = bones.Player;
            Transform root = player.Transform != null ? player.Transform.Original : null;
            Camera headset = VRGlobals.VRCam;
            if (root == null)
                return;
            if (rig == null || rig.bones != bones || rig.root != root)
            {
                rig = UpperBodySolver.Build(bones, root);
                if (logRig)
                    Plugin.MyLog.LogInfo($"[UpperBody] {(rig.valid ? "follows the headset" : "off")}: {rig.description}");
            }
            MovementContext movement = player.MovementContext;
            bool wanted = enabled && rig.valid && VRGlobals.inGame && headset != null && player.HealthController != null && player.HealthController.IsAlive
                && movement != null && !movement.IsInPronePose && !movement.IsInMountedState && movement.StationaryWeapon == null
                && !(player.IsSprintEnabled && !VRSettings.GetDisableRunAnim());
            weight = Mathf.MoveTowards(weight, wanted ? 1f : 0f, Time.deltaTime / Mathf.Max(blendTime, 0.01f));
            if (headset == null)
                return;
            UpperBodySolver.Solve(rig, headset.transform.position, headset.transform.rotation, weight);
            if (weight > 0f && rig.valid)
            {
                Transform chest = rig.chest;
                Quaternion rootInverse = Quaternion.Inverse(root.rotation);
                chestInRootPosition = rootInverse * (chest.position - root.position);
                chestInRootRotation = rootInverse * chest.rotation;
                chestScale = chest.lossyScale;
                solvedFrame = Time.frameCount;
            }
            if (!poseLocalBody)
                UpperBodySolver.Restore(rig);
        }

        // The chest to send the hands in: the solved one, carried with the body to now (null = not solved lately: the live one)
        internal static Transform SyncChest(EFT.Player player)
        {
            if (Time.frameCount - solvedFrame > 2 || player == null || player.Transform == null)
                return null;
            Transform root = player.Transform.Original;
            if (root == null)
                return null;
            if (syncChest == null)
            {
                syncChest = new GameObject("SPTVR synced chest").transform;
                Object.DontDestroyOnLoad(syncChest.gameObject);
            }
            syncChest.SetPositionAndRotation(root.position + root.rotation * chestInRootPosition, root.rotation * chestInRootRotation);
            // (the observers place the hands with the real ribcage's TransformPoint: its scale too)
            syncChest.localScale = chestScale;
            return syncChest;
        }
    }
}
