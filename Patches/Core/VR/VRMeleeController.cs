using HarmonyLib;
using System.Runtime.CompilerServices;
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

        // Logs every surface hit that is sent. Turn on MeleeHitApply.debug on the observer too, to compare send and apply.
        public static bool debugMeleeSend = false;

        // When the VR melee swing hits a surface (wall sparks, glass break), send it so observers replay the effect.
        // The collider swing runs only locally, so FIKA never replicates it. Hits on players and AI are not sent (their damage
        // syncs through ApplyShot). ProcessOverlap is the knife collider's OnHit handler and fires only on the swinging player's machine.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseKnifeController), "ProcessOverlap")]
        private static void SyncMeleeSurfaceHit(BaseKnifeController __instance, KnifeRaycastHit other)
        {
            if (!InstalledMods.FIKAInstalled || __instance?._player == null || !__instance._player.IsYourPlayer)
                return;
            if (other.collider == null)
                return;
            BallisticCollider ballisticCollider = other.collider.GetComponent<BallisticCollider>();
            if (ballisticCollider == null || ballisticCollider is BodyPartCollider)
                return; // surfaces only: players and AI sync through the normal damage path

            // Same point fix-up as method_6: use the collider's position if the hit point is near zero.
            Vector3 point = (other.point.sqrMagnitude < 0.1f) ? other.collider.transform.position : other.point;
            float dmg = (__instance.LastKickType == EKickType.Slash)
                ? __instance.Knife.Template.KnifeHitSlashDam
                : __instance.Knife.Template.KnifeHitStabDam;
            if (debugMeleeSend)
                Plugin.MyLog.LogWarning($"[FikaSync] melee SEND surface hit: {other.collider.name} mat={ballisticCollider.TypeOfMaterial} point={point} n={other.normal} dmg={dmg}");
            FikaSendMeleeHit(point, other.normal, __instance.GlobalSwingDirection, dmg);
        }

        // The FikaVrSync (SPT-VR-FikaSync.dll) call is in its own NoInlining method, so it is only JIT-compiled when called, after
        // the FIKAInstalled check. If it were inlined, compiling SyncMeleeSurfaceHit would load the reference and throw
        // FileNotFoundException without the companion DLL.
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
