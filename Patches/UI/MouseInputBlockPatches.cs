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
            if (VirtualScreen.Active)
            {
                if (!(__instance.inputOverride is VirtualMouseInput))
                {
                    VirtualMouseInput virtualMouse = __instance.GetComponent<VirtualMouseInput>();
                    if (virtualMouse == null)
                        virtualMouse = __instance.gameObject.AddComponent<VirtualMouseInput>();
                    __instance.inputOverride = virtualMouse;
                }
                return;
            }
            if (__instance.inputOverride is VirtualMouseInput)
                __instance.inputOverride = null;

            if (!VRSettings.GetDisableMouseInput())
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
