using TarkovVR.Source.Player.VRManager;

namespace TarkovVR.Patches.Core.Player
{
    internal static class LateGunPlacementPatches
    {
        // Runs last among the IkProcess prefixes, because IKPatches' airborne and run-animation zeroing moves the hands root first
        // Player.IkProcess prefix stage (FramePipeline)
        internal static void GunOnTheFinalHand(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || __instance.HandsIsEmpty)
                return;
            LateGunPlacement.BeforeArmIk(__instance);
            // Apply the gun's collision to that final pose, before the arm IK reaches for it
            Source.Player.Interactions.WeaponCollision.ApplyAtFinalPlacement();
        }
    }
}
