using HarmonyLib;
using TarkovVR.Source.UI;
using UnityEngine.EventSystems;

namespace TarkovVR.Patches.UI
{
    // Stops the physical desktop mouse from ever interacting with EFT's UI.
    //
    // Symptom this fixes: moving the real mouse over the game screen highlights/clicks UI elements,
    // which fights the VR laser cursor (e.g. stray hover states, the laser "losing" a button to the
    // mouse, double selections). In VR the mouse is never the intended pointer - the laser is - so we
    // neutralise the mouse at the source rather than fighting it per UI element.
    //
    // How: EFT's EventSystem uses Unity's stock StandaloneInputModule (confirmed: no custom input module
    // exists in Assembly-CSharp). We feed that module a BaseInput override (VRNoMouseInput) that reports
    // mousePresent=false; StandaloneInputModule.Process() then skips ProcessMouseEvent() entirely. The
    // laser is unaffected because VRUIInteracter sends its pointer events through ExecuteEvents directly
    // and never routes through the input module. Keyboard/IME/navigation still pass through untouched.
    [HarmonyPatch]
    internal class MouseInputBlockPatches
    {
        // Master switch (live-tunable). true = mouse can never touch UI (desired VR behavior);
        // false = restore vanilla mouse-driven UI (the known-good fallback for A/B in-headset).
        public static bool blockMouseInput = true;

        // Run as a prefix so the override is in place before the module reads input. The work is a
        // cheap reference check after the first frame, and it's self-healing: if EFT ever swaps the
        // EventSystem / input module out (raid<->menu transitions recreate UI), the override is just
        // re-applied to whatever module is now processing.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StandaloneInputModule), "Process")]
        private static void ForceNoMouseInput(StandaloneInputModule __instance)
        {
            if (!blockMouseInput)
            {
                // Allow toggling back to vanilla without a restart: drop our override if we set one.
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
