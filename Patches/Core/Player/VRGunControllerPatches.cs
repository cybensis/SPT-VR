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
            if (!__instance.IsYourPlayer)
                return;
            // The gun arm too (VRGunController.keepGunArmOffAnimation): a weapon switch's body draw drops it through RIGHT_HAND_WEIGHT.
            // The field as well: VisualPass checks it right after the IK for PlayerBones.Kinematics, which pulls the gun to the animated hand.
            if (VRGunController.KeepGunArmOffAnimation(__instance))
            {
                __instance._firstPersonRightHand = 1f;
                __instance._limbs[1].solver.IKPositionWeight = 1f;
                __instance._limbs[1].solver.IKRotationWeight = 1f;
            }
            if (!VRGunController.KeepLeftHandOnController())
                return;

            __instance._limbs[0].solver.IKPositionWeight = 1f;
            __instance._limbs[0].solver.IKRotationWeight = 1f;
            __instance.HandPosers[0].weight = 1f;
        }
    }
}
