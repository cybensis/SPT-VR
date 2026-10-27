using HarmonyLib;
using TarkovVR.Source.UI;
using UnityEngine;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class VirtualScreenPatches
    {
        // Some game UI reads the mouse directly instead of through the EventSystem (drag icon, tooltips, split
        // dialog), so while the virtual screen is on, the real mouse position is replaced with the laser's
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Input), nameof(Input.mousePosition), MethodType.Getter)]
        private static void UseVirtualMousePosition(ref Vector3 __result)
        {
            if (VirtualScreen.Active)
                __result = new Vector3(VirtualScreen.mousePosition.x, VirtualScreen.mousePosition.y, 0f);
        }
    }
}
