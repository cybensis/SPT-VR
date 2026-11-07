using EFT.InventoryLogic;
using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class MalfunctionTestPatches
    {
        // VRReloadController.testMalfunctionNextShot: Tarkov's per-shot roll (semi and auto fire both ask it) answers with the asked-for type.
        // Weapon.ValidateMalfunction and the rest of the shot run on it as on a rolled one. Our player only.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), nameof(EFT.Player.FirearmController.GetMalfunctionState))]
        private static void ForceTestMalfunction(EFT.Player.FirearmController __instance, bool hasAmmoInMag, bool doesWeaponHaveBoltCatch, bool isMagazineInserted,
            ref Weapon.EMalfunctionState __result, ref Weapon.EMalfunctionSource malfunctionSource)
        {
            if (VRReloadController.testMalfunctionNextShot == Weapon.EMalfunctionState.None || __instance._player == null || !__instance._player.IsYourPlayer)
                return;
            if (VRReloadController.ForcedMalfunction(__instance.Item, hasAmmoInMag, doesWeaponHaveBoltCatch, isMagazineInserted, out Weapon.EMalfunctionState malfunction))
            {
                __result = malfunction;
                malfunctionSource = Weapon.EMalfunctionSource.Durability;
            }
        }
    }
}
