using EFT.Animations;
using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class RecoilPosePatches
    {
        // Every procedural strategy that turns the gun goes through ApplyComplexRotation; RecoilPose keeps our player's only
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ProceduralWeaponAnimation), nameof(ProceduralWeaponAnimation.ApplyComplexRotation))]
        private static void CaptureRecoil(ProceduralWeaponAnimation __instance)
        {
            try
            {
                RecoilPose.Capture(__instance);
            }
            catch (System.Exception exception)
            {
                if (!warned)
                    Plugin.MyLog.LogWarning($"[RecoilPose] capture: {exception}");
                warned = true;
            }
        }

        private static bool warned;     // every frame: once
    }
}
