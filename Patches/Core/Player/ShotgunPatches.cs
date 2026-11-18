using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class ShotgunPatches
    {
        // A tube shotgun's pump worked by hand (VRReloadController.Shotgun): its bones written after the animator and before Tarkov's IK
        // in this same LateUpdate (VisualPass -> IkProcess reads the left-hand marker the pump carries), so the hand on the gun rides it
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void PumpBeforeIk(EFT.Player __instance)
        {
            if (__instance == null || !__instance.IsYourPlayer)
                return;
            VRReloadController.ApplyShotgunBeforeIk();
            // A belt gun's cover, scope latch and belt (VRReloadController.Belt): the same window, the hand on the cover rides it
            VRReloadController.ApplyBeltGunBeforeIk();
            // A break-action's barrels and lever (VRReloadController.BreakAction): the hand on the fore-end rides them
            VRReloadController.ApplyBreakActionBeforeIk();
        }

        // A break-action's lever pressed: the gun hand's IK marker swapped for Tarkov's IK in this LateUpdate only, put back here
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void LeverHandAfterIk(EFT.Player __instance)
        {
            if (__instance == null || !__instance.IsYourPlayer)
                return;
            VRReloadController.RestoreBreakLeverMarker();
        }

        // A dry fire drops the hammer: a pump gun's action lock lets go (VRReloadController.PumpDryFired)
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
