using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.Players;
using HarmonyLib;

namespace SptVrFikaSync
{
    // Suppresses EFT's aim-down-sights animation for observed VR players whose weapon pose we drive.
    // ArmSyncApply.DriveRemoteWeapon places their gun's WeaponRoot from the stream every frame. EFT's ADS
    // (ObservedFirearmController.IsAiming -> FirearmsAnimator flags + ProceduralWeaponAnimation.IsAiming) would pull the gun
    // to a canned sight pose instead. Only VR senders are affected (ArmSyncApply.IsWeaponSynced); flatscreen players keep normal ADS.
    [HarmonyPatch]
    internal static class ObservedAdsPatch
    {
        public static bool disableObservedAds = true;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObservedFirearmController), nameof(ObservedFirearmController.IsAiming), MethodType.Setter)]
        private static void ForceNoAds(ObservedFirearmController __instance, ref bool value)
        {
            // Only entering aim is blocked; stopping aim goes through untouched.
            if (!disableObservedAds || !FikaVrSync.enableWeaponSync || !value)
                return;
            ObservedPlayer op = __instance._observedPlayer;
            // Mounted guns are skipped: no synced pose drives them, so Tarkov's own aim has to.
            if (op != null && ArmSyncApply.IsWeaponSynced(op.NetId) && (op.MovementContext == null || op.MovementContext.StationaryWeapon == null))
                value = false; // never enters the ADS animation; the synced WeaponRoot drives the gun
        }
    }
}
