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
        // the body it goes to its spot instead (headset: "when you first enter shooting range, make gun start on its slot").
        // Armed long enough for the range's bundles to load before the draw.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HideoutPlayer), nameof(HideoutPlayer.UpdateHideoutPlayerInventory))]
        private static void HangTheRangeGun(HideoutPlayer __instance)
        {
            if (__instance.IsYourPlayer)
                ChestHang.HangOnArrival(30f);
        }

        // Tarkov's draw of a gun into your hands, skipped the moment it starts (his report: switching to another gun still plays "the
        // animation base game plays when you switch between weapons", the swing up and its sound). BodyWeapons.UpdateSwitch only got to
        // it from the next HandsInteractionController.Update: by then the animator had run the draw's first frame (its sound event is
        // on it), and only while the switch still counted as under way. Here, inside Player.SpawnController's own call
        // (HandsController set, then Spawn), the draw finishes before the animator ever plays it (WeaponPatches.SkipDrawOrPutAway: the
        // VR hand-over the clip's WeapIn event would have done, then Tarkov's own FastForward: the arms layer to idle + WeaponAppeared).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), nameof(FirearmController.Spawn), new[] { typeof(float), typeof(Action) })]
        private static void SkipDraw(FirearmController __instance)
        {
            BodyWeapons.OnDrawStarted(__instance);
            GrenadeSlot.OnGunDrawStarted(__instance);
        }
    }
}
