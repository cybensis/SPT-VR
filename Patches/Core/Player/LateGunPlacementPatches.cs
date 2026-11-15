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
            if (__instance.IsYourPlayer && !__instance.HandsIsEmpty)
                LateGunPlacement.BeforeArmIk(__instance);
        }
    }
}
