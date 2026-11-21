using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The VR player owns the gun and hands, Tarkov keeps the rules. Two places where Tarkov's animations would take them away:
    //  - The gun is anchored to the controller through weapon_R_IK_marker (_markers[1]), a bone on the weapon that
    //    reloads, mag checks and malfunctions animate. While any of those runs, the gun stays where it was in the hand.
    //  - Tarkov hands the left arm to the animation through the LEFT_HAND_WEIGHT curve, and the rig freezes the left
    //    hand while ReloadFloat is set. If either stays up (after a malfunction), the hand sticks on the gun, so the
    //    left hand always follows the controller.
    public static class VRGunController
    {
        // The gun arm is kept off the animation the same way. A weapon switch plays the body animator's draw, whose RIGHT_HAND_WEIGHT
        // curve drops the right arm's IK weight (1 - curve) and hands the arm, and with it the gun (PlayerBones.Kinematics below 1), to
        // that take-out. Only a switch plays it: a slung gun taken back never goes through Tarkov.

        public static bool KeepGunArmOffAnimation(EFT.Player player)
        {
            return player != null && player.HandsController is EFT.Player.FirearmController;
        }

        private static Vector3 heldWeaponHolderPosition;
        private static Transform heldForWeaponHolder;
        private static FirearmController heldForFirearm;

        // True while Tarkov animates the gun: the opposite of its own "really idle" test (idling, but not examining or checking)
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
            if (weaponHolder == null)
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
            return VRGlobals.firearmController != null;
        }
    }
}
