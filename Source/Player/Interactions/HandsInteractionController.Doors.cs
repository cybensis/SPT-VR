using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Source.Player.Interactions
{
    internal partial class HandsInteractionController
    {
        // Doors by hand (PhysicalDoors): which hands may take a door this frame. Called before Update's bail: a held door lets go
        // there itself (menus, sprint).
        private void UpdatePhysicalDoors()
        {
            // (out of a raid it lets go of everything)
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
            // A door or key a hand holds stays through the reach zones; it goes when the hand takes something (null = nothing did)
            string offHandTakenBy = armsBusy ? "an item, food or a grenade"
                : VRGlobals.vrPlayer.isSupporting ? "the foregrip"
                : HoldsLoot(false) || (isSummoningLoot && !heldInDominantHand) ? "loot"
                : VRReloadController.OwnsOffHand ? "the reload"
                : Handedness.GunInLeftHand ? "the gun"
                : isDraggingBody ? "a body" : null;
            string dominantHandTakenBy = armsBusy ? "an item, food or a grenade"
                : HoldsLoot(true) || (isSummoningLoot && heldInDominantHand) ? "loot"
                : !(ChestHang.Detached || VRGlobals.player.HandsIsEmpty) || VRReloadController.GunHandHolds || VRReloadController.HandleInGunHand ? "the gun"
                : null;
            PhysicalDoors.Tick(this, offHandFree, dominantHandFree, offHandTakenBy, dominantHandTakenBy);
        }
    }
}
