using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class VRMagazinePatches
    {
        // Tarkov drops a thrown item from the body's center (ThrowOperation.Dispose -> this overload). A magazine
        // VRReloadController let go of is moved to where the hand released it, with the hand's velocity.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.ThrowItem), new[] { typeof(Item), typeof(IPlayer), typeof(Vector3?) })]
        private static void PlaceDroppedMagazine(Item item, IPlayer player, LootItem __result)
        {
            if (player == null || !player.IsYourPlayer)
                return;
            VRReloadController.PlaceDroppedMagazine(item, __result);
        }

        // Every clip a sound player plays comes through here: the reload sounds VRReloadController plays are felt too
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseSoundPlayer), nameof(BaseSoundPlayer.PlayClip))]
        private static void FeelReloadSound(BaseSoundPlayer __instance, AudioClip clip)
        {
            VRReloadController.OnGunClipPlayed(__instance, clip);
        }
    }
}
