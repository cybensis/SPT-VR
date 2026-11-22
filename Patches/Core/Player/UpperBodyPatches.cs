using TarkovVR.Source.Player.Body;

namespace TarkovVR.Patches.Core.Player
{
    // Turns and bends the local player's upper body toward the headset (LocalUpperBody) right before Player.VisualPass places the
    // shoulders. It runs first among the SetShoulders prefixes, so IKPatches.OverrideShoulders and the arm IK start from the solved torso.
    internal static class UpperBodyPatches
    {
        // PlayerBones.SetShoulders prefix stage (FramePipeline)
        internal static void FollowHeadset(PlayerBones __instance)
        {
            if (__instance.Player == null || !__instance.Player.IsYourPlayer)
                return;
            // An exception here would break Tarkov's VisualPass every frame and freeze the body and arms.
            try
            {
                LocalUpperBody.Solve(__instance);
            }
            catch (System.Exception exception)
            {
                if (!loggedError)
                {
                    loggedError = true;
                    Plugin.MyLog.LogError($"[UpperBody] {exception}");
                }
            }
        }

        private static bool loggedError;
    }
}
