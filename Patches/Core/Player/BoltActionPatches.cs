using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class BoltActionPatches
    {
        // A bolt-action's shot is out (the FiringBullet event): with the bolt by hand (VRReloadController.Bolt), Tarkov's own bolt cycle
        // after it doesn't play. Its fire operation is skipped to idle right here (FireOperation.FastForward: the shot is already
        // fired, so it only exits and plays the idle state), without the cycle's ShellEject / AddAmmoInChamber: the hand does those.
        // Not on a malfunction (OnFireEvent switched to it already).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.FireOperation), nameof(EFT.Player.FirearmController.FireOperation.OnFireEvent))]
        private static void BoltStaysShut(EFT.Player.FirearmController.FireOperation __instance)
        {
            if (!(__instance is EFT.Player.FirearmController.BoltActionFireOperation))
                return;
            EFT.Player.FirearmController controller = __instance.Controller;
            if (controller == null || controller.CurrentOperation != __instance)
                return;
            if (VRReloadController.BoltShotFired(controller))
                controller.FastForwardCurrentState();
        }
    }
}
