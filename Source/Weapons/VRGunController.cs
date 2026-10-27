using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The start of the VR player controller: the VR player owns the gun and hands, Tarkov keeps the rules.
    //
    // Version 1 fixes the two places where Tarkov's animations took the gun or the left hand away from you:
    //  - The gun is anchored to your controller through weapon_R_IK_marker (_markers[1]), which is a bone ON
    //    the weapon that reloads, mag checks and malfunctions animate. Anchoring that moving marker shoved the gun
    //    around in your hand. While any of those animations runs, the gun now stays where it was in your hand.
    //  - Tarkov hands the left arm to the animation through the LEFT_HAND_WEIGHT curve, and the old rig froze the
    //    left hand while ReloadFloat was set. If either stayed up (after a malfunction), the hand stayed stuck on
    //    the gun. The left hand now always follows your controller.
    public static class VRGunController
    {
        public static bool enabled = true;    // false = the old behavior, for comparing in the headset

        private static Vector3 heldWeaponHolderPosition;
        private static Transform heldForWeaponHolder;
        private static FirearmController heldForFirearm;

        // Tarkov's own "really idle" test (FirearmController line ~13287): idling, but not examining or checking
        public static bool IsTarkovAnimatingTheGun()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController == null)
                return false;

            FirearmController.FirearmOperation operation = firearmController.CurrentOperation;
            if (operation == null)
                return false;
            if (operation is FirearmController.UtilityOperation || operation is FirearmController.LauncherUtilityOperation)
                return true;
            if (operation is FirearmController.Idling || operation is FirearmController.LauncherIdling)
                return false;
            return true;
        }

        // Called from GunInteractionController.UpdateWeaponPosition. Returns true when it placed the gun itself.
        public static bool TryHoldGunInHand(Transform weaponHolder)
        {
            if (!enabled || weaponHolder == null)
                return false;

            if (!IsTarkovAnimatingTheGun())
                return false;

            // Only hold a position remembered for this same gun, never one left over from the previous weapon
            if (heldForWeaponHolder != weaponHolder || heldForFirearm != VRGlobals.firearmController)
                return false;

            weaponHolder.localPosition = heldWeaponHolderPosition;
            return true;
        }

        // Called after the normal anchor placed the gun while idle, so the latest in-hand position is remembered
        public static void RememberGunPosition(Transform weaponHolder)
        {
            if (weaponHolder == null)
                return;
            heldWeaponHolderPosition = weaponHolder.localPosition;
            heldForWeaponHolder = weaponHolder;
            heldForFirearm = VRGlobals.firearmController;
        }

        public static bool KeepLeftHandOnController()
        {
            return enabled && VRGlobals.firearmController != null;
        }
    }
}
