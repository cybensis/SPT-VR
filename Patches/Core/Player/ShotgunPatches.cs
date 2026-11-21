using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class ShotgunPatches
    {
        // A tube shotgun's pump worked by hand (VRReloadController.Shotgun). Its bones are written after the animator and before
        // Tarkov's IK in this same LateUpdate (VisualPass -> IkProcess reads the left-hand marker on the pump), so the hand follows it.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void PumpBeforeIk(EFT.Player __instance)
        {
            if (__instance == null || !__instance.IsYourPlayer)
                return;
            VRReloadController.ApplyShotgunBeforeIk();
            // A belt-fed gun's cover, scope latch and belt (VRReloadController.Belt), at the same point; the hand on the cover follows it
            VRReloadController.ApplyBeltGunBeforeIk();
            // A break-action's barrels and lever (VRReloadController.BreakAction); the hand on the fore-end follows them
            VRReloadController.ApplyBreakActionBeforeIk();
        }

        // While a break-action's lever is pressed, the gun hand's IK marker is swapped only for Tarkov's IK in this LateUpdate.
        // It is put back here.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void LeverHandAfterIk(EFT.Player __instance)
        {
            if (__instance == null || !__instance.IsYourPlayer)
                return;
            VRReloadController.RestoreBreakLeverMarker();
        }

        // A dry fire drops the hammer, which releases a pump gun's action lock (VRReloadController.PumpDryFired)
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), nameof(EFT.Player.FirearmController.DryShot))]
        private static void DryFireFreesPump(EFT.Player.FirearmController __instance)
        {
            if (__instance == null || __instance._player == null || !__instance._player.IsYourPlayer)
                return;
            VRReloadController.PumpDryFired(__instance);
        }
    }
}
