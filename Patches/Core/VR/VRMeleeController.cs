using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        [HarmonyPatch(typeof(BaseKnifeController), "ProcessOverlap")]
        private static void SyncMeleeSurfaceHit(BaseKnifeController __instance, EFT.Player.KnifeRaycastHit other)
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
            FikaSendMeleeHit(point, other.normal, __instance.GlobalSwingDirection, dmg);
        }

        // Companion-DLL isolation: the FikaVrSync (SPT-VR-FikaSync.dll) call lives in its own
        // NoInlining method so it's only JITted when actually CALLED — which only happens after the
        // InstalledMods.FIKAInstalled early-return above. Inlining it into SyncMeleeSurfaceHit would
        // bind the cross-assembly reference at that patch method's JIT (on the first surface hit) and
        // throw FileNotFoundException when running without the companion DLL. See the same pattern in
        // HandsInteractionController.BodyGrab + memory note fika-softdep-no-typed-fields.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendMeleeHit(Vector3 point, Vector3 normal, Vector3 direction, float damage)
            => FikaVrSync.SendMeleeHit(point, normal, direction, damage);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void ManageKnifeCollider(EFT.Player __instance)
        {
            if (__instance == null || !__instance.IsYourPlayer) return;
            BaseKnifeController knifeController = __instance.HandsController as BaseKnifeController;
            if (knifeController?._knifeCollider == null) return;

            Vector3 controllerVelocity = VRSettings.GetLeftHandedMode()
                ? ControllerVelocity.GetSteamVRVelocity(SteamVR_Input_Sources.LeftHand)
                : ControllerVelocity.GetSteamVRVelocity(SteamVR_Input_Sources.RightHand);
            Vector3 worldVelocity = VRGlobals.vrOffsetter.transform.TransformDirection(controllerVelocity);
            float speed = worldVelocity.magnitude;

            // On start of swing
            if (speed > KNIFE_SWING_THRESHOLD && !knifeController._knifeCollider.enabled)
            {
                knifeController.LastKickType = EKickType.Slash;
                knifeController._knifeCollider.MaxDistance = knifeController.Knife.Template.PrimaryDistance;
                knifeController._knifeCollider.enabled = true;
                knifeController._knifeCollider.OnHit -= knifeController.ProcessOverlap;
                knifeController._knifeCollider.OnHit += knifeController.ProcessOverlap;
                knifeController._knifeCollider.OnFire();

                float staminaCost = (knifeController.LastKickType == EKickType.Slash)
                    ? knifeController.Knife.Template.PrimaryConsumption
                    : knifeController.Knife.Template.SecondaryConsumption;
                knifeController._player.Physical.ConsumeAsMelee(staminaCost);

                knifeController._firearmsAnimator?.SetAnimationSpeed(50f);
                knifeController._firearmsAnimator?.SetFire(true);

                knifeController.PreviousColliderPosition = knifeController._knifeCollider.transform.position;
            }
            // While swinging
            else if (knifeController._knifeCollider.enabled && speed > KNIFE_SWING_RESET)
            {
                knifeController._knifeCollider.ManualUpdate();

                Vector3 position = knifeController._knifeCollider.transform.position;
                Vector3 normalized = (position - knifeController.PreviousColliderPosition).normalized;
                knifeController.GlobalSwingDirection = Vector3.Lerp(knifeController.GlobalSwingDirection, normalized, 0.9f);
                knifeController.PreviousColliderPosition = position;
                knifeController._firearmsAnimator?.SetFire(false);
            }
            // When swing ends
            else if (speed < KNIFE_SWING_RESET && knifeController._knifeCollider.enabled)
            {
                knifeController._knifeCollider.enabled = false;
                knifeController._knifeCollider.OnFireEnd();
            }
        }
    }
}
