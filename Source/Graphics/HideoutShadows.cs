using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Source.Graphics
{
    // "Disable Hideout Shadows" (VR menu, Graphics): no realtime shadows while the hideout is loaded. Its ~100 shadowed spot and point
    // lights are the hideout's main cost (with shadows off: main thread 17.5 -> 11.0 ms, GPU 14.0 -> 8.7 ms).
    // QualitySettings.shadows is global, so it is restored as soon as the hideout is gone (a raid, the menu). It is re-applied
    // every frame because EFT's own graphics settings write it whenever they are applied.
    public class HideoutShadows : MonoBehaviour
    {
        // The hideout's player (set in InitVRPatches.AddVR). It becomes Unity-null once the hideout scene is gone.
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
                    shadowsBefore = QualitySettings.shadows;   // EFT applied its settings in the meantime: restore those instead
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
