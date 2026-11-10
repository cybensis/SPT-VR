using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Source.Graphics
{
    // "Disable Hideout Shadows" (VR Graphics): no realtime shadows while the hideout is loaded. Its ~100 shadowed spot / point lights are
    // the hideout's main cost (shadows off: main thread 17.5 -> 11.0 ms, GPU 14.0 -> 8.7).
    // QualitySettings.shadows is global, so it's set back to what it was the moment the hideout is gone (a raid, the menu). Re-applied
    // every frame: EFT's own graphics settings write it when they're applied.
    public class HideoutShadows : MonoBehaviour
    {
        // The hideout's player (InitVRPatches.AddVR): Unity-null once the hideout scene is gone
        internal static EFT.Player hideoutPlayer;

        private static bool overriding;
        private static ShadowQuality shadowsBefore;

        private void Update()
        {
            bool inHideout = hideoutPlayer != null && hideoutPlayer.isActiveAndEnabled;
            if (inHideout && VRSettings.GetHideoutShadowsOff())
            {
                if (!overriding)
                {
                    overriding = true;
                    shadowsBefore = QualitySettings.shadows;
                    Plugin.MyLog.LogInfo($"[HideoutShadows] hideout shadows off (were {shadowsBefore})");
                }
                else if (QualitySettings.shadows != ShadowQuality.Disable)
                    shadowsBefore = QualitySettings.shadows;   // EFT's settings applied meanwhile: theirs is what comes back
                QualitySettings.shadows = ShadowQuality.Disable;
            }
            else if (overriding)
            {
                overriding = false;
                QualitySettings.shadows = shadowsBefore;
                Plugin.MyLog.LogInfo($"[HideoutShadows] shadows back to {shadowsBefore}");
            }
        }
    }
}
