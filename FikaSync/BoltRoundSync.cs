using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.Players;
using HarmonyLib;

namespace SptVrFikaSync
{
    // Accepts a VR player's single hand-loaded round on every peer. SPT-VR loads open bolt-actions (and the guns below) by hand,
    // one round at a time from the rig.
    //
    // After an inventory move that touches a gun in hands is applied, it is passed to that gun's hands controller, on observed copies too
    // (ObservedInventoryController.HandleInProcess -> HandsController.Execute). Tarkov's FirearmController.Idling.Execute refuses a round
    // into the internal magazine unless it is its own reload (LoadMagOperation), and turns a round into the chamber into its
    // LoadCartridgeToChamber animation. A FIKA client only applies a move the host accepted (ClientInventoryController), so a refusal on
    // the host refuses the move for the VR player too. This prefix accepts the VR player's move as it is, with no animation, on every peer
    // that has this module (ObservedIdleOperation inherits Idling.Execute). The VR player's own game does the same in SPT-VR's
    // BoltActionPatches.LetSingleRoundIn, and IsSingleRoundIntoGun is the same rule as VRReloadController.LetsSingleRoundIn.
    //
    // Only VR players (ArmSyncApply.IsVrPlayer) are affected: anyone else's Mosin keeps Tarkov's own chambering. enabled = false turns it off.
    [HarmonyPatch]
    internal static class BoltRoundSync
    {
        public static bool enabled = true;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Player.FirearmController.Idling), nameof(Player.FirearmController.Idling.Execute),
            new[] { typeof(IInventoryOperation), typeof(Callback) })]
        private static bool LetVrRoundIn(Player.FirearmController.Idling __instance, IInventoryOperation operation, Callback callback)
        {
            if (!enabled)
                return true;
            Player.FirearmController controller = __instance.Controller;
            if (controller == null || !(controller._player is ObservedPlayer observed) || !ArmSyncApply.IsVrPlayer(observed.NetId))
                return true;
            if (!IsSingleRoundIntoGun(controller.Item, operation) && !IsBreakActionChamberMove(controller.Item, operation))
                return true;
            callback?.Succeed();
            return false;
        }

        // True for one round moved from outside the gun into its chamber or its magazine's cartridges, other than by Tarkov's own reload.
        // Covers guns loaded with the bolt open (SupportsInternalReload + MustBoltBeOpennedForInternalReload) and tube shotguns, which SPT-VR's
        // VRReloadController.Shotgun loads through the loading port or the ejection port. A revolver's cylinder is excluded (the MC-255 is
        // also a shotgun). A merge arrives as a sub-operation of its own, so the operation it belongs to is checked for the destination.
        internal static bool IsSingleRoundIntoGun(Weapon weapon, IInventoryOperation operation)
        {
            if (weapon == null || !weapon.SupportsInternalReload)
                return false;
            bool tubeShotgun = weapon.WeapClass == "shotgun" && weapon.ReloadMode == Weapon.EReloadMode.InternalMagazine && !weapon.BoltAction
                && !(weapon.GetCurrentMagazine() is CylinderMagazine);
            if (!weapon.MustBoltBeOpennedForInternalReload && !tubeShotgun)
                return false;
            if (operation is ISubOperation subOperation && subOperation.BaseInventoryOperation != null && IsRoundIntoGun(weapon, subOperation.BaseInventoryOperation))
                return true;
            return IsRoundIntoGun(weapon, operation);
        }

        // True for one round moved by hand into or out of a break-action's chamber or a revolver's camora (SPT-VR's
        // VRReloadController.BreakAction), rather than by Tarkov's own reload or discharge, which both animate. Same rule as SPT-VR's
        // LetsBreakMoveThrough.
        internal static bool IsBreakActionChamberMove(Weapon weapon, IInventoryOperation operation)
        {
            if (weapon == null || (weapon.ReloadMode != Weapon.EReloadMode.OnlyBarrel && !(weapon is Revolver && weapon.GetCurrentMagazine() is CylinderMagazine)))
                return false;
            if (operation is ISubOperation subOperation && subOperation.BaseInventoryOperation != null && TouchesChamber(weapon, subOperation.BaseInventoryOperation))
                return true;
            return TouchesChamber(weapon, operation);
        }

        private static bool TouchesChamber(Weapon weapon, IInventoryOperation operation)
        {
            if (!(operation is IOneItemOperation move) || !(move.Item1 is Ammo))
                return false;
            if (IsChamber(weapon, move.From1) || IsChamber(weapon, move.To1))
                return true;
            return operation is ITwoItemOperation twoItems && IsChamber(weapon, twoItems.To2);
        }

        private static bool IsChamber(Weapon weapon, ItemAddress address)
        {
            if (address == null)
                return false;
            Slot[] chambers = weapon is Revolver && weapon.GetCurrentMagazine() is CylinderMagazine cylinder ? cylinder.Camoras : weapon.Chambers;
            foreach (Slot chamber in chambers)
            {
                if (ReferenceEquals(address.Container, chamber))
                    return true;
            }
            return false;
        }

        // The destination is To1 for a move or split, and To2 for a transfer or a merge onto the top stack of the magazine (a transfer's
        // To1 is its own source).
        private static bool IsRoundIntoGun(Weapon weapon, IInventoryOperation operation)
        {
            if (operation is LoadMagOperation || !(operation is IOneItemOperation move) || !(move.Item1 is Ammo))
                return false;
            // The round must come from outside the gun. A round moving inside it is Tarkov's own discharge or chambering.
            if (move.From1 != null && move.From1.IsChildOf(weapon))
                return false;
            return IntoGun(weapon, move.To1) || (operation is ITwoItemOperation twoItems && IntoGun(weapon, twoItems.To2));
        }

        private static bool IntoGun(Weapon weapon, ItemAddress address)
        {
            if (address == null)
                return false;
            object into = address.Container;
            Magazine magazine = weapon.GetCurrentMagazine();
            if (magazine != null && ReferenceEquals(into, magazine.Cartridges))
                return true;
            foreach (Slot chamber in weapon.Chambers)
            {
                if (ReferenceEquals(into, chamber))
                    return true;
            }
            return false;
        }
    }
}
