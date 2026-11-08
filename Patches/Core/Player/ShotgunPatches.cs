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
        }
    }
}
