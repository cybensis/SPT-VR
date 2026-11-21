using System;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;

namespace TarkovVR.Patches.Core.Player
{
    // When a key is turned in its lock by hand (PhysicalDoors), Tarkov's unlock still runs, and FIKA's Start packet with the key was
    // already sent before this. Skipped: the walk-up to the door and the unlock animation, which would take over the body and the hands.
    [HarmonyPatch(typeof(MovementContext), nameof(MovementContext.StartInteraction),
        new[] { typeof(WorldInteractiveObject), typeof(InteractionResult), typeof(Action) })]
    internal static class DoorKeyPatches
    {
        private static bool Prefix(MovementContext __instance, WorldInteractiveObject interactive, InteractionResult interactionResult, Action callback)
        {
            if (!PhysicalDoors.UnlocksByHand(__instance, interactive))
                return true;
            PhysicalDoors.UnlockNow(__instance, interactive, interactionResult, callback);
            return false;
        }
    }
}
