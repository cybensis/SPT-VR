using HarmonyLib;

namespace TarkovVR.Patches.Core.Player
{
    // ADS without the arms animator's aim pose, on the guns Tarkov aims through the animator (his PKM: "it does an animation state change
    // and moves the gun but also moves the hand to the bottom of the ammo box").
    //
    // How Tarkov aims: the FirearmController.IsAiming setter turns the ADS itself on - Firearms.SetAiming -> ProceduralWeaponAnimation.IsAiming
    // (the optic, the sight alignment, sensitivity), the aim slowdown, the stamina drain - and then, unless the gun carries a mod with a unique
    // animation (bool_7), SetAimingInAnimator: IsAimingFloat 1 + the AimingIn trigger on the arms animator. Templates flagged
    // WithAnimatorAiming (items.json: PKM, PKP, PKTM, and the RShG-2) have an animator built on that: AimingIn plays pk_ironsight_in, and
    // IDLE / FIRE / RECHAMBER / the jams are blend trees on IsAimingFloat between the hip clip and its _ironsight twin, which moves the gun
    // in the hands and the left IK marker to the box's bottom (every other gun's animator ignores those parameters: aim_states.py, all
    // weapon controllers). In VR the gun is held where the hands are, so that pose only drags the gun and the support hand.
    // Here those guns take Tarkov's own no-animator-aim path (what bool_7 does): the aim flags cleared, the animator left in its hip pose.
    // Everything else about ADS is untouched. Tarkov's trigger also waits on that path's flag (bool_0: a belt gun with WithAnimatorAiming
    // fires only once the aim-in clip's idle event clears it) - never set here, so the trigger isn't held up.
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
            // Back to the hip blend should it have been left aimed (the patch switched on mid-aim)
            __instance.FirearmsAnimator?.SetAiming(false);
            return false;
        }
    }
}
