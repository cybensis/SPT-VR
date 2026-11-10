using System;
using EFT;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class BodyWeaponPatches
    {
        // Walking into the hideout's shooting range: Tarkov fills your slots and puts the first gun in your hands; with guns on
        // the body it goes to its spot instead. Armed long enough for the range's bundles to load before the draw.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HideoutPlayer), nameof(HideoutPlayer.UpdateHideoutPlayerInventory))]
        private static void HangTheRangeGun(HideoutPlayer __instance)
        {
            if (__instance.IsYourPlayer)
                ChestHang.HangOnArrival(30f);
        }

        // Tarkov's draw of a gun into your hands (the swing up and its sound), skipped the moment it starts. It must happen here, inside
        // Player.SpawnController's own call (HandsController set, then Spawn): by the next HandsInteractionController.Update the animator
        // has already run the draw's first frame, which carries its sound event. WeaponPatches.SkipDrawOrPutAway does the VR hand-over the
        // clip's WeapIn event would have done, then Tarkov's own FastForward (the arms layer to idle + WeaponAppeared).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), nameof(FirearmController.Spawn), new[] { typeof(float), typeof(Action) })]
        private static void SkipDraw(FirearmController __instance)
        {
            BodyWeapons.OnDrawStarted(__instance);
            GrenadeSlot.OnGunDrawStarted(__instance);
        }
    }
}
