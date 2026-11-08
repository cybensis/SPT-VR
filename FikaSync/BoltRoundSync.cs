using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using Fika.Core.Main.Players;
using HarmonyLib;

namespace SptVrFikaSync
{
    // A VR player's single round into an open bolt-action (SPT-VR loads those by hand, one round at a time from the rig).
    //
    // Every inventory move touching a player's gun in hands is put to that gun's hands controller after it's applied, and on a copy of
    // another player FIKA does the same (ObservedInventoryController.HandleInProcess -> HandsController.Execute). Tarkov's
    // FirearmController.Idling.Execute refuses a round into the internal magazine that isn't its own reload (LoadMagOperation) and turns
    // one into the chamber into its LoadCartridgeToChamber animation. The host refusing the move refuses it for the VR player too: a FIKA
    // client only applies a move the host accepted (ClientInventoryController). So on every peer with this module the VR player's move is
    // accepted as it is, no animation (ObservedIdleOperation inherits Idling.Execute). The VR player's own game does the same for itself
    // (SPT-VR's BoltActionPatches.LetSingleRoundIn); IsSingleRoundIntoGun is the same rule as its VRReloadController.LetsSingleRoundIn.
    //
    // Only VR players (ArmSyncApply.IsVrPlayer): anyone else's Mosin keeps Tarkov's own chambering. Live A/B switch: enabled.
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
            if (!IsSingleRoundIntoGun(controller.Item, operation))
                return true;
            callback?.Succeed();
            return false;
        }

        // One round from outside an open-bolt-loaded gun (SupportsInternalReload + MustBoltBeOpennedForInternalReload), or a tube shotgun's
        // shell (SPT-VR's VRReloadController.Shotgun: through the loading port or the ejection port), into its chamber or its magazine's
        // cartridges, not as Tarkov's own reload. Not a revolver's cylinder (the MC-255 is a shotgun too). A merge asks with a sub-operation
        // of its own: the operation it belongs to says where.
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

        // The destination is To1 for a move / split, To2 for a transfer or merge onto the stack on top of the magazine (a transfer's To1
        // is its own source)
        private static bool IsRoundIntoGun(Weapon weapon, IInventoryOperation operation)
        {
            if (operation is LoadMagOperation || !(operation is IOneItemOperation move) || !(move.Item1 is Ammo))
                return false;
            // From outside the gun, not a round moving inside it (Tarkov's own discharge / chambering)
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
