using HarmonyLib;
using TarkovVR.Source.Player.Interactions;

namespace TarkovVR.Patches.Core.Player
{
    // No ADS while the gun hand is off the grip (ChestHang: the gun slung, or held up by the support hand alone - mid-reload on a belt gun).
    // Held that way the gun is placed off the support hand (ChestHang.DriveSupportPose) rather than through the gun hand's marker, which is
    // what absorbs Tarkov's procedural aim shift on Weapon_root_anim, so aiming then moved the gun and the hand on it (his PKM: "still doing
    // the ADS movement if you ADS while in the middle of the reload"). The trigger is off in that state too (InputHandlers.FireHandler).
    // INFERRED from the code (not measured in the headset): if he sees it with both hands on the gun, this isn't it.
    [HarmonyPatch]
    internal class DetachedAimPatches
    {
        public static bool enabled = true;      // false = aiming with the gun hand off the grip as before

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), nameof(EFT.Player.FirearmController.SetAim), typeof(bool))]
        private static void NoAimOffTheGrip(EFT.Player.FirearmController __instance, ref bool value)
        {
            if (enabled && value && ChestHang.Detached && __instance != null && __instance._player != null && __instance._player.IsYourPlayer)
                value = false;
        }

        // Already aiming as the gun hand lets go: out of it (ChestHang, the sling and the support-only hold)
        public static void StopAimingOffTheGrip()
        {
            EFT.Player.FirearmController firearmController = VRGlobals.firearmController;
            if (enabled && firearmController != null && firearmController.IsAiming)
                firearmController.SetAim(false);
        }
    }
}
