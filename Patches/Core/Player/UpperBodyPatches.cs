using EFT;
using HarmonyLib;
using TarkovVR.Source.Player.Body;

namespace TarkovVR.Patches.Core.Player
{
    // The local upper body follows the headset (LocalUpperBody) right before Player.VisualPass places the shoulders: first among the
    // SetShoulders prefixes, so IKPatches.OverrideShoulders and the arm IK after it build on the turned and bent torso
    [HarmonyPatch]
    internal static class UpperBodyPatches
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(PlayerBones), nameof(PlayerBones.SetShoulders))]
        private static void FollowHeadset(PlayerBones __instance)
        {
            if (__instance.Player == null || !__instance.Player.IsYourPlayer)
                return;
            // (a throw here would break Tarkov's VisualPass every frame: the body and arms would stop)
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
