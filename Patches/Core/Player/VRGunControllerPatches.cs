using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class VRGunControllerPatches
    {
        // VisualPass sets the arm IK weights from the animation (1 - LEFT_HAND_WEIGHT curve) right before IkProcess.
        // During reloads and malfunctions that curve hands the left arm to the animation; put it back on the
        // controller. Outside of those animations the weights are already 1, so nothing changes.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "IkProcess")]
        private static void KeepLeftArmOnController(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || !VRGunController.KeepLeftHandOnController())
                return;

            __instance._limbs[0].solver.IKPositionWeight = 1f;
            __instance._limbs[0].solver.IKRotationWeight = 1f;
            __instance.HandPosers[0].weight = 1f;
        }
    }
}
