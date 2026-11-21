using Fika.Core.Main.Players;
using HarmonyLib;

namespace SptVrFikaSync
{
    // When the local player is revived from the downed state, moves them to where their body was dragged (see DownedDragApply).
    // This waits for the revive because teleporting the root while downed drags the ragdoll bones with it. After a drag the bones
    // sit far from the root, so they overshoot the drop spot on observers. At revive the animator re-poses the bones from the root.
    // Patches the local player's FikaPlayer.ToggleDowned; ObservedPlayer overrides it, so observers aren't affected.
    [HarmonyPatch(typeof(FikaPlayer), nameof(FikaPlayer.ToggleDowned))]
    internal static class FikaPlayer_ToggleDowned_RevivePatch
    {
        [HarmonyPostfix]
        private static void Postfix(FikaPlayer __instance, bool downed)
        {
            // downed == false here means "being revived".
            if (!downed && __instance != null && __instance.IsYourPlayer)
                DownedDragApply.OnLocalRevived(__instance);
        }
    }
}
