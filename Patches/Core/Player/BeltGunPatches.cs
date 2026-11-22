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
        private static void BeforeRebind(WeaponPrefab __instance, out VRReloadController.Belt.AnimatorRebind __state)
        {
            __state = VRReloadController.Belt.BeforeAnimatorRebind(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeaponPrefab), nameof(WeaponPrefab.UpdateAnimatorHierarchy))]
        private static void AfterRebind(VRReloadController.Belt.AnimatorRebind __state)
        {
            VRReloadController.Belt.AfterAnimatorRebind(__state);
        }

        // Keeps Tarkov's grip fingers on the PKM's support hand while it holds the handguard and aims (VRReloadController.Belt support spots).
        // Player.IkProcess postfix stage (FramePipeline)
        internal static void SupportSpotGrip(EFT.Player __instance)
        {
            if (__instance != null && __instance.IsYourPlayer)
                VRReloadController.Belt.AfterSupportIkProcess(__instance);
        }
    }
}
