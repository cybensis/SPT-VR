using HarmonyLib;

namespace TarkovVR.Patches.Core.Player
{
    // ADS without the arms animator's aim pose, on the guns Tarkov aims through the animator (the PKM's aim pose moves the gun and drags
    // the support hand to the bottom of the ammo box).
    //
    // The FirearmController.IsAiming setter turns ADS on (Firearms.SetAiming -> ProceduralWeaponAnimation.IsAiming: optic, sight alignment,
    // sensitivity; aim slowdown; stamina drain), then, unless the gun carries a mod with a unique animation (bool_7), SetAimingInAnimator:
    // IsAimingFloat 1 + the AimingIn trigger on the arms animator. Templates flagged WithAnimatorAiming (PKM, PKP, PKTM, RShG-2) build on
    // that: AimingIn plays pk_ironsight_in, and IDLE / FIRE / RECHAMBER / the jams are blend trees on IsAimingFloat between the hip clip and
    // its _ironsight twin, moving the gun in the hands and the left IK marker (every other gun's animator ignores those parameters). In VR
    // the gun is where the hands are, so that pose only drags the gun and the support hand.
    // Here those guns take Tarkov's no-animator-aim path (what bool_7 does): aim flags cleared, animator left in its hip pose; the rest of
    // ADS is untouched. The trigger also waits on that path's flag (bool_0: cleared by the aim-in clip's idle event); never set here.
    [HarmonyPatch]
    internal class AnimatorAimingPatches
    {
        public static bool enabled = true;      // false = Tarkov's aim pose on those guns as it is

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), nameof(EFT.Player.FirearmController.SetAimingInAnimator))]
        private static bool NoAnimatorAim(EFT.Player.FirearmController __instance)
        {
            if (!enabled || __instance == null || __instance._player == null || !__instance._player.IsYourPlayer || __instance.Item == null
                || !__instance.Item.WithAnimatorAiming || __instance.Item is EFT.InventoryLogic.RocketLauncher)
                return true;
            __instance.ResetAimingAnimationsFlags();
            // Back to the hip blend in case it was left aimed (enabled switched on mid-aim)
            __instance.FirearmsAnimator?.SetAiming(false);
            return false;
        }
    }
}
