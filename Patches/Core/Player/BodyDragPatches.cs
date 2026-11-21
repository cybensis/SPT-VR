using EFT.AssetsManager;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;

namespace TarkovVR.Patches.Core.Player
{
    // Stops EFT's settle coroutine from putting a body we hold, or are letting settle, to sleep. Its Sleep() zeroes the bone's
    // velocity every frame, so the body would float down slowly. Only the bones in HandsInteractionController.bonesKeptAwake are
    // affected; every other corpse sleeps as in Tarkov.
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
