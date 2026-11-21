using Fika.Core.Main.Players;
using HarmonyLib;

namespace TarkovVR.ModSupport.FIKA
{
    // On a FIKA client, HandleCallbackResponse is the host's answer to a hands change (FikaClient -> FikaPlayer.HandleCallbackResponse).
    // A non-empty error means the host refused it, and FIKA has already undone the change locally (leaving empty hands).
    // CoopHost records when this happened so the quick hands moves that need to repair it can check.
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
