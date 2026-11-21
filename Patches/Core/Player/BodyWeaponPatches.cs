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
        // When you walk into the hideout's shooting range, Tarkov fills your slots and puts the first gun in your hands. With guns
        // carried on the body, that gun goes to its body spot instead. It stays armed for 30 s so the range's bundles can load before the draw.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HideoutPlayer), nameof(HideoutPlayer.UpdateHideoutPlayerInventory))]
        private static void HangTheRangeGun(HideoutPlayer __instance)
        {
            if (__instance.IsYourPlayer)
                ChestHang.HangOnArrival(30f);
        }

        // Skips Tarkov's draw animation of a gun into your hands (the swing up and its sound) as soon as it starts. This must run here,
        // inside Player.SpawnController's own call (HandsController is set, then Spawn runs). By the next HandsInteractionController.Update
        // the animator has already played the draw's first frame, which carries its sound event. WeaponPatches.SkipDrawOrPutAway does the
        // VR hand-over that the clip's WeapIn event would have done, then Tarkov's own FastForward (arms layer to idle + WeaponAppeared).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), nameof(FirearmController.Spawn), new[] { typeof(float), typeof(Action) })]
        private static void SkipDraw(FirearmController __instance)
        {
            BodyWeapons.OnDrawStarted(__instance);
            GrenadeSlot.OnGunDrawStarted(__instance);
        }
    }
}
