using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.Players;
using HarmonyLib;

namespace SptVrFikaSync
{
    // Suppress EFT's aim-down-sights animation for observed VR players whose weapon pose we drive.
    // Their gun's WeaponRoot pose is streamed and planted every frame by ArmSyncApply.DriveRemoteWeapon; EFT's ADS
    // (ObservedFirearmController.IsAiming -> FirearmsAnimator flags + ProceduralWeaponAnimation.IsAiming) would pull the gun
    // to a canned sight pose instead. VR senders only (ArmSyncApply.IsWeaponSynced): flatscreen players keep normal ADS.
    [HarmonyPatch]
    internal static class ObservedAdsPatch
    {
        public static bool disableObservedAds = true;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObservedFirearmController), nameof(ObservedFirearmController.IsAiming), MethodType.Setter)]
        private static void ForceNoAds(ObservedFirearmController __instance, ref bool value)
        {
            // Only intercept entering aim; "stop aiming" goes through untouched
            if (!disableObservedAds || !FikaVrSync.enableWeaponSync || !value)
                return;
            ObservedPlayer op = __instance._observedPlayer;
            if (op != null && ArmSyncApply.IsWeaponSynced(op.NetId))
                value = false; // never enters the ADS animation; the synced WeaponRoot drives the gun
        }
    }
}
