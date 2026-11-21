using HarmonyLib;

namespace TarkovVR.Patches.Core.Player
{
    // Aims down sights without the arms animator's aim pose on the guns Tarkov aims through the animator. On the PKM that pose moves the
    // gun and drags the support hand to the bottom of the ammo box.
    //
    // The FirearmController.IsAiming setter turns ADS on (Firearms.SetAiming: optic, sight alignment, sensitivity, aim slowdown, stamina
    // drain). Then, unless the gun has a mod with a unique animation (bool_7), SetAimingInAnimator sets IsAimingFloat to 1 and fires the
    // AimingIn trigger on the arms animator. On templates flagged WithAnimatorAiming (PKM, PKP, PKTM, RShG-2), AimingIn plays
    // pk_ironsight_in, and the idle, fire, rechamber and jam states blend on IsAimingFloat between the hip clip and its _ironsight twin,
    // which moves the gun and the left IK marker in the hands. In VR the gun is where the hands are, so that pose only drags them.
    // This patch sends those guns down Tarkov's no-animator-aim path (what bool_7 does): the aim flags are cleared and the animator stays
    // in its hip pose. The rest of ADS is untouched. The trigger also waits on that path's flag (bool_0, which the aim-in clip's idle
    // event clears), so it is never set here.
    [HarmonyPatch]
    internal class AnimatorAimingPatches
    {

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), nameof(EFT.Player.FirearmController.SetAimingInAnimator))]
        private static bool NoAnimatorAim(EFT.Player.FirearmController __instance)
        {
            if (__instance == null || __instance._player == null || !__instance._player.IsYourPlayer || __instance.Item == null
                || !__instance.Item.WithAnimatorAiming || __instance.Item is EFT.InventoryLogic.RocketLauncher)
                return true;
            __instance.ResetAimingAnimationsFlags();
            // Return the animator to the hip blend in case it was left aiming.
            __instance.FirearmsAnimator?.SetAiming(false);
            return false;
        }
    }
}
