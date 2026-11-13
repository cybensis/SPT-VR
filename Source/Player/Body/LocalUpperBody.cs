using EFT;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // The local VR player's own upper body solved to the headset (UpperBodySolver), right before the shoulders are placed (UpperBodyPatches:
    // PlayerBones.SetShoulders), only with poseLocalBody. Off while prone, in a run with the run animation and on a mounted gun (their
    // animations pose the body). The other players' copy of you is bent from your headset either way (HeadSyncApply, from VRHeadPacket).
    internal static class LocalUpperBody
    {
        public static bool enabled = true;
        // true = your own skeleton keeps the solved pose (shadow + hitboxes follow the head). In first person the body sits behind the
        // camera, so it bends far forward, and Tarkov hangs its hands container (PlayerSpring) and the body gun spots off the ribcage:
        // the held and slung gun move out in front.
        public static bool poseLocalBody = false;
        public static float blendTime = 0.25f;      // s it eases in and out
        public static bool logRig = true;           // one line per rig: the bones it turns, or why it can't

        private static UpperBodySolver.Rig rig;
        private static float weight;

        public static void Solve(PlayerBones bones)
        {
            if (!poseLocalBody)
                return;
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
            if (headset != null)
                UpperBodySolver.Solve(rig, headset.transform.position, headset.transform.rotation, weight);
        }
    }
}
