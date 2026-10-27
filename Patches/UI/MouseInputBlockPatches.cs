using HarmonyLib;
using TarkovVR.Source.Settings;
using TarkovVR.Source.UI;
using UnityEngine.EventSystems;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class MouseInputBlockPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StandaloneInputModule), "Process")]
        private static void ForceNoMouseInput(StandaloneInputModule __instance)
        {
            // OPENXR SPIKE: no controller input yet, so the mouse is the only way through the menus.
            if (!VRSettings.GetDisableMouseInput() || TarkovVR.Source.XR.OpenXRBootstrap.Active)
            {
                if (__instance.inputOverride is VRNoMouseInput)
                    __instance.inputOverride = null;
                return;
            }

            if (__instance.inputOverride is VRNoMouseInput)
                return;

            VRNoMouseInput noMouse = __instance.GetComponent<VRNoMouseInput>();
            if (noMouse == null)
                noMouse = __instance.gameObject.AddComponent<VRNoMouseInput>();
            __instance.inputOverride = noMouse;
        }
    }
}
