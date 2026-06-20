using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.Players;
using HarmonyLib;

namespace SptVrFikaSync
{
    // Suppress EFT's aim-down-sights animation for observed VR players whose WEAPON pose we drive.
    //
    // For a VR player the real aim comes from the controller and we stream the gun's WeaponRoot pose,
    // which ArmSyncApply.DriveRemoteWeapon plants on the observer every frame. EFT's flatscreen ADS
    // animation fights that: ObservedFirearmController.IsAiming pulls the gun to a canned sight pose
    // (via the FirearmsAnimator flags + ProceduralWeaponAnimation.IsAiming) that doesn't match where
    // the VR player actually points. Forcing the observed aim OFF leaves the synced pose as the only
    // thing positioning the gun.
    //
    // Scoped to VR senders only (ArmSyncApply.IsWeaponSynced) so a flatscreen player in the same lobby
    // keeps a normal ADS animation. Live A/B toggle: disableObservedAds.
    [HarmonyPatch]
    internal static class ObservedAdsPatch
    {
        public static bool disableObservedAds = true;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObservedFirearmController), nameof(ObservedFirearmController.IsAiming), MethodType.Setter)]
        private static void ForceNoAds(ObservedFirearmController __instance, ref bool value)
        {
            // Only intercept ENTERING aim; let "stop aiming" through untouched.
            if (!disableObservedAds || !FikaVrSync.enableWeaponSync || !value)
                return;
            ObservedPlayer op = __instance._observedPlayer;
            if (op != null && ArmSyncApply.IsWeaponSynced(op.NetId))
                value = false; // never enters the ADS animation; the synced WeaponRoot drives the gun
        }
    }
}
