using Comfort.Common;
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

        // A round thrown out by our rechamber (Firearms.ThrowPatronAsLoot calls this 9-argument overload with syncable false)
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameWorld), nameof(GameWorld.ThrowItem),
            new[] { typeof(Item), typeof(IPlayer), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Vector3), typeof(bool), typeof(bool), typeof(float) })]
        private static void ProbeEjectedRound(Item item, IPlayer player, Vector3 velocity, bool syncable, LootItem __result)
        {
            if (player == null || !player.IsYourPlayer || syncable || !(item is Ammo))
                return;
            EjectedRoundProbe.Attach(__result, velocity);
        }

        // Tarkov's magazine and rechamber operations, as they start (after InitiateOperation made each one current). On a FIKA client
        // our moves only land when the host answers, so VRReloadController finishes them here. It checks that this is a co-op client
        // and the local player's controller.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.PullOutMagOperation), nameof(EFT.Player.FirearmController.PullOutMagOperation.Start),
            new[] { typeof(Magazine), typeof(Slot), typeof(Callback) })]
        private static void PullOutStarted(EFT.Player.FirearmController.PullOutMagOperation __instance)
        {
            VRReloadController.OnOperationStarted(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.InsertMagOperation), nameof(EFT.Player.FirearmController.InsertMagOperation.Start),
            new[] { typeof(EFT.Player.FirearmController.InsertMagResult), typeof(Callback) })]
        private static void InsertStarted(EFT.Player.FirearmController.InsertMagOperation __instance)
        {
            VRReloadController.OnOperationStarted(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.RechamberOperation), nameof(EFT.Player.FirearmController.RechamberOperation.Start),
            new[] { typeof(Ammo), typeof(Callback) })]
        private static void RechamberStarted(EFT.Player.FirearmController.RechamberOperation __instance)
        {
            VRReloadController.OnOperationStarted(__instance);
        }

        // Every clip a sound player plays passes through here, so the reload sounds VRReloadController plays also drive the haptics
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseSoundPlayer), nameof(BaseSoundPlayer.PlayClip))]
        private static void FeelReloadSound(BaseSoundPlayer __instance, AudioClip clip)
        {
            VRReloadController.OnGunClipPlayed(__instance, clip);
        }
    }
}
