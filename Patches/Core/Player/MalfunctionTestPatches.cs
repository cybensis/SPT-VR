using EFT.InventoryLogic;
using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class MalfunctionTestPatches
    {
        // When VRReloadController.testMalfunctionNextShot is set, Tarkov's per-shot malfunction roll (asked by both semi and auto fire)
        // returns that type. Weapon.ValidateMalfunction and the rest of the shot treat it like a rolled one. Our player only.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), nameof(EFT.Player.FirearmController.GetMalfunctionState))]
        private static void ForceTestMalfunction(EFT.Player.FirearmController __instance, bool hasAmmoInMag, bool doesWeaponHaveBoltCatch, bool isMagazineInserted,
            ref Weapon.EMalfunctionState __result, ref Weapon.EMalfunctionSource malfunctionSource)
        {
            if (VRReloadController.Malfunctions.testMalfunctionNextShot == Weapon.EMalfunctionState.None || __instance._player == null || !__instance._player.IsYourPlayer)
                return;
            if (VRReloadController.Malfunctions.ForcedMalfunction(__instance.Item, hasAmmoInMag, doesWeaponHaveBoltCatch, isMagazineInserted, out Weapon.EMalfunctionState malfunction))
            {
                __result = malfunction;
                malfunctionSource = Weapon.EMalfunctionSource.Durability;
            }
        }
    }
}
