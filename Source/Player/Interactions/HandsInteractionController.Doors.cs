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
                PhysicalDoors.Tick(this, false, false);
                return;
            }
            bool armsBusy = VRGlobals.usingItem || EatingInteractionController.ManualActive || WeaponPatches.grenadeEquipped;
            bool offHandFree = !armsBusy && !VRGlobals.vrPlayer.isSupporting
                && !HoldsLoot(false) && !(isSummoningLoot && !heldInDominantHand) && SecondHandFree(false)
                && !leftHandState.inScope && !leftHandState.inBackpack && !leftHandState.inHeadGear;
            bool dominantHandFree = !armsBusy && !HoldsLoot(true) && !(isSummoningLoot && heldInDominantHand)
                && SecondHandFree(true) && !VRReloadController.GunHandHolds;
            PhysicalDoors.Tick(this, offHandFree, dominantHandFree);
        }
    }
}
