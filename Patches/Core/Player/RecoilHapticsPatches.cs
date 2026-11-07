using EFT.InventoryLogic;
using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class RecoilHapticsPatches
    {
        // Every shot fired (each barrel of a double too) goes through Shot; FIKA's client controller inherits it unchanged.
        // Our player only: bots and the other players' copies fire through here too.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), nameof(EFT.Player.FirearmController.Shot))]
        private static void RecoilKick(EFT.Player.FirearmController __instance, Weapon weapon)
        {
            try
            {
                if (__instance._player != null && __instance._player.IsYourPlayer)
                    GunHaptics.Recoil(weapon);
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogWarning($"[GunHaptics] recoil kick: {exception.Message}");
            }
        }
    }
}
