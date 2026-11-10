using EFT.AssetsManager;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;

namespace TarkovVR.Patches.Core.Player
{
    // A body we hold or are letting settle is never put to sleep by EFT's settle coroutine: its Sleep() zeroes the bone's velocity
    // every frame, so the body floats down slowly (HandsInteractionController.bonesKeptAwake). Only those bones; every other
    // corpse sleeps as in Tarkov.
    [HarmonyPatch]
    internal static class BodyDragPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerRigidbodySleepHierarchy), nameof(PlayerRigidbodySleepHierarchy.TryPutToSleep))]
        private static bool KeepHeldBodiesAwake(PlayerRigidbodySleepHierarchy __instance, ref bool __result)
        {
            if (!HandsInteractionController.keepHeldBodiesAwake || HandsInteractionController.bonesKeptAwake.Count == 0
                || !HandsInteractionController.bonesKeptAwake.Contains(__instance))
                return true;
            __result = false;
            return false;
        }
    }
}
