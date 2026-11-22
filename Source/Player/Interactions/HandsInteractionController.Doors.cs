using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Source.Player.Interactions
{
    internal partial class HandsInteractionController
    {
        // Doors by hand (PhysicalDoors): decides which hands may take a door this frame. Called before Update's early return,
        // because PhysicalDoors itself lets go of a held door in those cases (menus, sprint).
        private void UpdatePhysicalDoors()
        {
            // Out of a raid, Tick lets go of everything
            if (!VRGlobals.inGame || VRGlobals.vrPlayer == null || VRGlobals.player == null)
            {
                PhysicalDoors.Tick(this, false, false, "out of a raid", "out of a raid");
                return;
            }
            bool armsBusy = VRGlobals.usingItem || EatingInteractionController.ManualActive || WeaponPatches.grenadeEquipped;
            bool offHandFree = !armsBusy && !VRGlobals.vrPlayer.isSupporting
                && !HoldsLoot(false) && !(isSummoningLoot && !heldInDominantHand) && SecondHandFree(false)
                && !leftHandState.inScope && !leftHandState.inBackpack && !leftHandState.inHeadGear;
            bool dominantHandFree = !armsBusy && !HoldsLoot(true) && !(isSummoningLoot && heldInDominantHand)
                && SecondHandFree(true) && !VRReloadController.GunHandHolds;
            // A held door or key stays held while the hand passes through the reach zones. It is let go only when the hand takes
            // something else (null = nothing did).
            string offHandTakenBy = armsBusy ? "an item, food or a grenade"
                : VRGlobals.vrPlayer.isSupporting ? "the foregrip"
                : HoldsLoot(false) || (isSummoningLoot && !heldInDominantHand) ? "loot"
                : VRReloadController.OwnsOffHand ? "the reload"
                : Handedness.GunInLeftHand ? "the gun"
                : bodyInteractionArmed && !bodyGrabDominant ? "a body" : null;
            string dominantHandTakenBy = armsBusy ? "an item, food or a grenade"
                : HoldsLoot(true) || (isSummoningLoot && heldInDominantHand) ? "loot"
                : !(ChestHang.Detached || VRGlobals.player.HandsIsEmpty) || VRReloadController.GunHandHolds || VRReloadController.ChargingHandle.HandleInGunHand ? "the gun"
                : bodyInteractionArmed && bodyGrabDominant ? "a body"
                : null;
            PhysicalDoors.Tick(this, offHandFree, dominantHandFree, offHandTakenBy, dominantHandTakenBy);
        }
    }
}
