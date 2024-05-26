using EFT;
using HarmonyLib;

namespace TarkovVR.Patches.Misc
{
    [HarmonyPatch]
    internal class HideoutPatches
    {
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // When at the shooting range, if you look or point the gun away from down range it lowers the weapon
        // which is annoying so do this to prevent it from happening
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HideoutPlayer), "SetPatrol")]
        private static bool PreventGunBlockInHideout(HideoutPlayer __instance, ref bool patrol)
        {
            if (VRGlobals.oldWeaponHolder != null)
                patrol = false;

            return true;
        }
    }
}
