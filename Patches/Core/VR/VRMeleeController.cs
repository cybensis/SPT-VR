using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static EFT.Player;
using EFT.Ballistics;
using SptVrFikaSync;
using TarkovVR.ModSupport;
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

        // Set true to log every surface hit we broadcast (pair with MeleeHitApply.debug on the observer
        // to see send vs. apply in the BepInEx log).
        public static bool debugMeleeSend = false;

        // When OUR VR melee swing hits a SURFACE (wall sparks / glass break), broadcast it so observers
        // replay the effect -- the custom collider swing runs only locally, so FIKA never replicates it
        // (unlike a normal melee/shot). Player/AI hits are NOT sent: their damage already syncs through
        // ApplyShot. method_6 is the knife collider's OnHit handler (non-virtual, fires only on the
        // swinger's collision), so this is a reliable, local-only hook.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseKnifeController), "method_6")]
        private static void SyncMeleeSurfaceHit(BaseKnifeController __instance, GStruct182 other)
        {
            if (!InstalledMods.FIKAInstalled || __instance?._player == null || !__instance._player.IsYourPlayer)
                return;
            if (other.collider == null)
                return;
            BallisticCollider bc = other.collider.GetComponent<BallisticCollider>();
            if (bc == null || bc is BodyPartCollider)
                return; // surface only -- players/AI sync via the normal damage path

            // Mirror method_6's own point fix-up (uses the collider position if the hit point is ~0).
            Vector3 point = (other.point.sqrMagnitude < 0.1f) ? other.collider.transform.position : other.point;
            float dmg = (__instance.LastKickType == EKickType.Slash)
                ? __instance.Knife.Template.KnifeHitSlashDam
                : __instance.Knife.Template.KnifeHitStabDam;
            if (debugMeleeSend)
                Plugin.MyLog.LogWarning($"[FikaSync] melee SEND surface hit: {other.collider.name} mat={bc.TypeOfMaterial} point={point} n={other.normal} dmg={dmg}");
            FikaVrSync.SendMeleeHit(point, other.normal, __instance.vector3_0, dmg);
        }

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
