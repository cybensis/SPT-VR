using HarmonyLib;
using TarkovVR.Source.Player.VRManager;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal static class LateGunPlacementPatches
    {
        // Last among the IkProcess prefixes: IKPatches' airborne / run-animation zeroing moves the hands root before it
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(EFT.Player), "IkProcess")]
        private static void GunOnTheFinalHand(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || __instance.HandsIsEmpty)
                return;
            LateGunPlacement.BeforeArmIk(__instance);
            // The gun's collision on that final pose, before the arm IK reaches for it
            Source.Player.Interactions.WeaponCollision.ApplyAtFinalPlacement();
        }
    }
}
