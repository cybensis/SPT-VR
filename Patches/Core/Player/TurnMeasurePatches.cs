using HarmonyLib;
using TarkovVR.Source.Player.VRManager;

namespace TarkovVR.Patches.Core.Player
{
    // TurnMeasure's samples around Tarkov's arm IK (measurement only)
    [HarmonyPatch]
    internal static class TurnMeasurePatches
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(EFT.Player), "IkProcess")]
        private static void BeforeArmIk(EFT.Player __instance)
        {
            if (__instance.IsYourPlayer)
                TurnMeasure.BeforeIk();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "IkApply")]
        private static void AfterArmIk(EFT.Player __instance)
        {
            if (__instance.IsYourPlayer)
                TurnMeasure.AfterIk();
        }
    }
}
