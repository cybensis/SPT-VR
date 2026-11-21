using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class BeltGunPatches
    {
        // When a belt box goes on or off the gun, Tarkov toggles the hands-and-gun animator's object to bind the box's bones.
        // VRReloadController.Belt puts Weapon_root back in its place for the binding and keeps the animator's state through our own box moves.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(WeaponPrefab), nameof(WeaponPrefab.UpdateAnimatorHierarchy))]
        private static void BeforeRebind(WeaponPrefab __instance, out VRReloadController.AnimatorRebind __state)
        {
            __state = VRReloadController.BeforeAnimatorRebind(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeaponPrefab), nameof(WeaponPrefab.UpdateAnimatorHierarchy))]
        private static void AfterRebind(VRReloadController.AnimatorRebind __state)
        {
            VRReloadController.AfterAnimatorRebind(__state);
        }

        // Keeps Tarkov's grip fingers on the PKM's support hand while it holds the handguard and aims (VRReloadController.Belt support spots).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static void SupportSpotGrip(EFT.Player __instance)
        {
            if (__instance != null && __instance.IsYourPlayer)
                VRReloadController.AfterSupportIkProcess(__instance);
        }
    }
}
