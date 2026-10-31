using System;
using HarmonyLib;
using TarkovVR.Source.Weapons;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class DrawRackPatches
    {
        // Drawn with an empty chamber and rounds in the magazine, Tarkov chambers one right here and racks it in the draw animation
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController.SpawnOperation), "Start", new[] { typeof(Action) })]
        private static void LeaveTheRackToTheHand(FirearmController.SpawnOperation __instance)
        {
            VRReloadController.TakeRackOutOfDraw(__instance);
        }
    }
}
