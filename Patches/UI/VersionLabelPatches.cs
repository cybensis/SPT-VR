using EFT.UI;
using HarmonyLib;
using UnityEngine.UI;

namespace TarkovVR.Patches.UI
{
    // Hides the version watermark ("FIKA x | SPT x - build | PvE", PreloaderUI's corner label). In VR it floats ~3 m wide in front of
    // the player, and as a visible UI-layer graphic it keeps uiCam drawing every frame (UiCameraGate). Only its text component is
    // turned off, not the object, because the wrist notifier is parented next to it. Every corner-label refresh hides it again.
    [HarmonyPatch]
    internal class VersionLabelPatches
    {

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "Awake")]
        private static void HideOnAwake(PreloaderUI __instance) { Hide(__instance); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "RefreshCornerLabel")]
        private static void HideOnRefresh(PreloaderUI __instance) { Hide(__instance); }

        private static void Hide(PreloaderUI preloader)
        {
            if (preloader == null || preloader._alphaVersionLabel == null)
                return;
            Graphic text = preloader._alphaVersionLabel.GetComponent<Graphic>() ?? preloader._alphaVersionLabel.GetComponentInChildren<Graphic>(true);
            if (text != null && text.enabled)
                text.enabled = false;
        }
    }
}
