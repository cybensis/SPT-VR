using EFT.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TarkovVR.Patches.UI
{
    // The version watermark ("FIKA x | SPT x - build | PvE", PreloaderUI's corner label) shows while a menu or the inventory is open
    // (VRGlobals.menuOpen) and is hidden the rest of the time: PreloaderUI stays where the last menu put it, so in play the label
    // floats ~3 m wide in front of the player, and as a visible UI-layer graphic it keeps uiCam drawing every frame (UiCameraGate).
    // Only its text component is switched, not the object, because the wrist notifier is parented next to it.
    [HarmonyPatch]
    internal class VersionLabelPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "Awake")]
        private static void FollowMenusFromAwake(PreloaderUI __instance)
        {
            if (__instance == null)
                return;
            if (__instance.GetComponent<VersionLabelGate>() == null)
                __instance.gameObject.AddComponent<VersionLabelGate>();
            VersionLabelGate.Apply(__instance);
        }

        // Tarkov rewrites the label on every refresh
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "RefreshCornerLabel")]
        private static void FollowMenusOnRefresh(PreloaderUI __instance) { VersionLabelGate.Apply(__instance); }
    }

    // Keeps the label's text in step with VRGlobals.menuOpen every frame, whichever path opened or closed the menu
    internal class VersionLabelGate : MonoBehaviour
    {
        private PreloaderUI preloader;

        private void Awake() { preloader = GetComponent<PreloaderUI>(); }

        private void LateUpdate() { Apply(preloader); }

        internal static void Apply(PreloaderUI preloader)
        {
            if (preloader == null || preloader._alphaVersionLabel == null)
                return;
            Graphic text = preloader._alphaVersionLabel.GetComponent<Graphic>() ?? preloader._alphaVersionLabel.GetComponentInChildren<Graphic>(true);
            bool show = VRGlobals.menuOpen;
            if (text != null && text.enabled != show)
                text.enabled = show;
        }
    }
}
