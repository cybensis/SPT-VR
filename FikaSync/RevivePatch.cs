using Fika.Core.Main.Players;
using HarmonyLib;

namespace SptVrFikaSync
{
    // When the local player is revived from a downed state, apply any pending body-drag relocation:
    // teleport them to where their body was dragged. Deferred to the revive (not done on release) because
    // teleporting the player root while still downed drags the ragdoll bones with it, and those sit far
    // from the root after a drag, so they overshoot the drop spot on observers. At revive the animator
    // re-poses the bones from the root. See DownedDragApply.
    //
    // Patches FikaPlayer.ToggleDowned (the local player's version; ObservedPlayer overrides it separately,
    // so observers aren't affected). PatchAll() in FikaSyncPlugin.Awake applies this.
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
