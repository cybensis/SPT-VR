using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class BeltGunPatches
    {
        // A belt box going on or off the gun: Tarkov toggles the hands-and-gun animator's object to bind the box's bones
        // (VRReloadController.Belt: Weapon_root back in its place for the binding, the animator's state kept through our own box moves)
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

        // The PKM's support hand on the handguard while aiming (VRReloadController.Belt support spots): keeps Tarkov's grip fingers
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static void SupportSpotGrip(EFT.Player __instance)
        {
            if (__instance != null && __instance.IsYourPlayer)
                VRReloadController.AfterSupportIkProcess(__instance);
        }
    }
}
