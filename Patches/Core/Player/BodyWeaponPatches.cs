using EFT;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class BodyWeaponPatches
    {
        // Walking into the hideout's shooting range: Tarkov fills your slots and puts the first gun in your hands; with guns on
        // the body it goes to its spot instead (headset: "when you first enter shooting range, make gun start on its slot").
        // Armed long enough for the range's bundles to load before the draw.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HideoutPlayer), nameof(HideoutPlayer.UpdateHideoutPlayerInventory))]
        private static void HangTheRangeGun(HideoutPlayer __instance)
        {
            if (__instance.IsYourPlayer)
                ChestHang.HangOnArrival(30f);
        }
    }
}
