using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static EFT.Player;
using TarkovVR.Source.Controls;
using Valve.VR;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class VRMeleeController
    {

        private const float KNIFE_SWING_THRESHOLD = 4.0f;
        private const float KNIFE_SWING_RESET = 1.0f;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void ManageKnifeCollider(EFT.Player __instance)
        {
            if (__instance == null || !__instance.IsYourPlayer) return;
            BaseKnifeController knifeController = __instance.HandsController as BaseKnifeController;
            if (knifeController?.knifeCollider_0 == null) return;

            Vector3 controllerVelocity = VRSettings.GetLeftHandedMode()
                ? ControllerVelocity.GetSteamVRVelocity(SteamVR_Input_Sources.LeftHand)
                : ControllerVelocity.GetSteamVRVelocity(SteamVR_Input_Sources.RightHand);
            Vector3 worldVelocity = VRGlobals.vrOffsetter.transform.TransformDirection(controllerVelocity);
            float speed = worldVelocity.magnitude;

            // On start of swing
            if (speed > KNIFE_SWING_THRESHOLD && !knifeController.knifeCollider_0.enabled)
            {
                knifeController.LastKickType = EKickType.Slash;
                knifeController.knifeCollider_0.MaxDistance = knifeController.Knife.Template.PrimaryDistance;
                knifeController.knifeCollider_0.enabled = true;
                knifeController.knifeCollider_0.OnHit -= knifeController.method_6;
                knifeController.knifeCollider_0.OnHit += knifeController.method_6;
                knifeController.knifeCollider_0.OnFire();

                float staminaCost = (knifeController.LastKickType == EKickType.Slash)
                    ? knifeController.Knife.Template.PrimaryConsumption
                    : knifeController.Knife.Template.SecondaryConsumption;
                knifeController._player.Physical.ConsumeAsMelee(staminaCost);

                knifeController.firearmsAnimator_0?.SetAnimationSpeed(50f);
                knifeController.firearmsAnimator_0?.SetFire(true);

                knifeController.vector3_1 = knifeController.knifeCollider_0.transform.position;
            }
            // While swinging
            else if (knifeController.knifeCollider_0.enabled && speed > KNIFE_SWING_RESET)
            {
                knifeController.knifeCollider_0.ManualUpdate();

                Vector3 position = knifeController.knifeCollider_0.transform.position;
                Vector3 normalized = (position - knifeController.vector3_1).normalized;
                knifeController.vector3_0 = Vector3.Lerp(knifeController.vector3_0, normalized, 0.9f);
                knifeController.vector3_1 = position;
                knifeController.firearmsAnimator_0?.SetFire(false);
            }
            // When swing ends
            else if (speed < KNIFE_SWING_RESET && knifeController.knifeCollider_0.enabled)
            {
                knifeController.knifeCollider_0.enabled = false;
                knifeController.knifeCollider_0.OnFireEnd();
            }
        }
    }
}
