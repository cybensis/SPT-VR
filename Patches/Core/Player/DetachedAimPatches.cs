using HarmonyLib;
using TarkovVR.Source.Player.Interactions;

namespace TarkovVR.Patches.Core.Player
{
    // Blocks ADS while the gun hand is off the grip (ChestHang.Detached: the gun is slung, or held by the support hand alone, for
    // example mid-reload on a belt gun). In that state the gun is placed from the support hand (ChestHang.DriveSupportPose) instead of
    // the gun hand's marker. That marker is what absorbs Tarkov's procedural aim shift on Weapon_root_anim, so aiming would move the
    // gun and the hand on it. The trigger is off in that state too (InputHandlers.FireHandler).
    [HarmonyPatch]
    internal class DetachedAimPatches
    {

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), nameof(EFT.Player.FirearmController.SetAim), typeof(bool))]
        private static void NoAimOffTheGrip(EFT.Player.FirearmController __instance, ref bool value)
        {
            if (value && ChestHang.Detached && __instance != null && __instance._player != null && __instance._player.IsYourPlayer)
                value = false;
        }

        // Stops aiming when the gun hand lets go mid-aim. Called by ChestHang when it slings the gun or starts the support-only hold.
        public static void StopAimingOffTheGrip()
        {
            EFT.Player.FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController != null && firearmController.IsAiming)
                firearmController.SetAim(false);
        }
    }
}
