using Fika.Core.Main.Players;
using HarmonyLib;

namespace TarkovVR.ModSupport.FIKA
{
    // As a FIKA client: the host's answer to a hands change (FikaClient -> FikaPlayer.HandleCallbackResponse). An error = refused:
    // FIKA has already undone the change here (it leaves empty hands). CoopHost keeps when, for the quick moves that need to repair it.
    [HarmonyPatch]
    internal static class FikaRefusals
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FikaPlayer), "HandleCallbackResponse")]
        private static void NoteRefusal(FikaPlayer __instance, string error)
        {
            if (!string.IsNullOrWhiteSpace(error) && __instance != null && __instance.IsYourPlayer)
                CoopHost.NoteRefused(error);
        }
    }
}
