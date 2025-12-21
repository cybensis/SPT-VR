using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static EFT.Player;
using TarkovVR.Source.Controls;
using UnityEngine;
using Valve.VR;
using EFT;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Weapons;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class VRGrenadeController
    {
        
        public static bool throwInProgress = false;
        public static BaseGrenadeHandsController activeGrenadeController = null;
        private static float lastNormalizedTime = 0f;
        private static float freezeStartTime = 0f;
        private static bool isFrozen = false;
        private static readonly float freezeDuration = 0.5f; // Or use Random.Range(2f, 3f) when setting

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GrenadeHandsController), "Spawn")]
        static void HandleGrenade(GrenadeHandsController __instance, float animationSpeed, Action callback)
        {
            var player = __instance._player;

            if (!player.IsYourPlayer)
                return;

            WeaponPatches.grenadeEquipped = true;
            WeaponPatches.pinPulled = false;
            VRGlobals.switchingWeapon = true;
            InitVRPatches.rightPointerFinger.enabled = false;

            if (WeaponPatches.currentGunInteractController != null)
                WeaponPatches.currentGunInteractController.enabled = false;

            VRGlobals.player = player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            if (VRSettings.GetLeftHandedMode())
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            VRGlobals.usingItem = false;

            VRPlayerManager.leftHandGunIK = __instance.HandsHierarchy.Transforms[10];
            VRGlobals.oldWeaponHolder = __instance.HandsHierarchy.gameObject;
            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                if (__instance.WeaponRoot.parent.Find("RightHandPositioner"))
                {
                    WeaponPatches.currentGunInteractController = __instance.WeaponRoot.parent.GetComponent<GunInteractionController>();
                    WeaponPatches.currentGunInteractController.enabled = true;
                    WeaponPatches.currentGunInteractController.SetPlayerOwner(player.gameObject.GetComponent<GamePlayerOwner>());
                    VRGlobals.weaponHolder = __instance.WeaponRoot.parent.Find("RightHandPositioner").Find("weaponHolder").gameObject;

                }
                else
                {
                    WeaponPatches.currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
                    WeaponPatches.currentGunInteractController.Init();
                    WeaponPatches.currentGunInteractController.initialized = true;
                    WeaponPatches.currentGunInteractController.SetPlayerOwner(player.gameObject.GetComponent<GamePlayerOwner>());

                    GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                    rightHandPositioner.transform.SetParent(__instance.WeaponRoot.transform.parent, false);
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.SetParent(rightHandPositioner.transform, false);
                    HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                    handsPositioner.rightHandIk = rightHandPositioner.transform;
                }
                //Transform handTransform = VRGlobals.vrPlayer.RightHand.transform; //figure out why this works
                //VRGlobals.weaponHolder.transform.SetParent(handTransform, false);

                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                WeaponPatches.weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset("", "grenade");
            }
            else if (__instance.WeaponRoot.parent.parent.name == "RightHandPositioner")
            {
                __instance.WeaponRoot.transform.parent = __instance.WeaponRoot.transform.parent.parent.parent;
                HandleGrenade(__instance, animationSpeed, callback);
                return;
            }
            //Plugin.MyLog.LogError($"Weaponroot parent: {__instance.WeaponRoot.parent.name}");
            if (VRGlobals.player)
            {
                WeaponPatches.previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                //VRGlobals.leftArmBendGoal.localPosition = new Vector3(-0.5f, -0.3f, -0.4f);
                //VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
            }

            if (VRSettings.GetLeftHandedMode())
            {
                VRGlobals.player._elbowBends[0] = VRGlobals.rightArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.leftArmBendGoal;
            }
            else
            {
                VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;
            }

            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);

            VRGlobals.oldWeaponHolder.transform.localEulerAngles = new Vector3(340, 340, 0);
            VRGlobals.weaponHolder.transform.localPosition = WeaponPatches.weaponOffset;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void CheckGrenadeAnimation(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || !throwInProgress || activeGrenadeController == null || activeGrenadeController.firearmsAnimator_0 == null)
                return;

            AnimatorStateInfoWrapper stateInfo = activeGrenadeController.firearmsAnimator_0.Animator.GetCurrentAnimatorStateInfo(1);

            if (isFrozen)
            {
                if (stateInfo.normalizedTime < lastNormalizedTime && lastNormalizedTime >= 0.3f)
                    activeGrenadeController.firearmsAnimator_0.Animator.speed = 0f;

                if (Time.time >= freezeStartTime + freezeDuration)
                {
                    activeGrenadeController.firearmsAnimator_0.Animator.speed = 1f;
                    activeGrenadeController.method_2(); // Calls OnDropFinishedAction()
                    throwInProgress = false;
                    activeGrenadeController = null;
                    isFrozen = false;
                    lastNormalizedTime = 0f;
                }
                return;
            }

            if (stateInfo.normalizedTime < lastNormalizedTime && lastNormalizedTime > 0.45f)
            {
                activeGrenadeController.firearmsAnimator_0.Animator.Play(590329303, 1, 0f);
                activeGrenadeController.firearmsAnimator_0.Animator.speed = 0f;
                freezeStartTime = Time.time;
                isFrozen = true;
            }

            lastNormalizedTime = stateInfo.normalizedTime;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseGrenadeHandsController), "method_9")]
        private static bool RepositionGrenadeThrow(BaseGrenadeHandsController __instance, ref Vector3? throwPosition, float timeSinceSafetyLevelRemoved, float lowHighThrow, Vector3 direction, float forcePower, bool lowThrow, bool withVelocity)
        {
            if (!__instance._player.IsYourPlayer)
                return true;
            if (!WeaponPatches.pinPulled)
                return false;

            Vector3 throwVelocity = ControllerVelocity.GetSteamVRVelocity(SteamVR_Input_Sources.RightHand);
            Vector3 force;
            Vector3 throwPos = ControllerVelocity.GetSteamVRPosition(SteamVR_Input_Sources.RightHand);
            Quaternion throwRot = ControllerVelocity.GetSteamVRRotation(SteamVR_Input_Sources.RightHand);

            // Starts animation around the point where hand is opening to throw
            __instance.firearmsAnimator_0.Animator.Play(590329303, 1, 0.40f);
            throwInProgress = true;
            activeGrenadeController = __instance;

            if (throwVelocity.magnitude > 0.1f)
            {
                // Transform from controller local space to world space
                Vector3 worldSpaceVelocity = VRGlobals.vrOffsetter.transform.TransformDirection(throwVelocity);

                float grenadeVelocityMultiplier = 1.5f;
                force = worldSpaceVelocity * grenadeVelocityMultiplier;

                if (force.magnitude > 15f)
                {
                    force = force.normalized * 15f;
                }
            }
            else
            {
                Vector3 defaultDirection = VRGlobals.vrPlayer.RightHand.transform.forward;
                force = defaultDirection * (forcePower * lowHighThrow * 0.5f);
            }

            if (withVelocity)
            {
                force += __instance._player.Velocity;
            }

            __instance.vmethod_2(
                timeSinceSafetyLevelRemoved,
                VRGlobals.vrOffsetter.transform.TransformPoint(throwPos),
                VRGlobals.vrOffsetter.transform.rotation * throwRot,
                force,
                lowThrow
            );

            VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
            InitVRPatches.rightPointerFinger.enabled = false;
            VRGlobals.emptyHands = null;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseGrenadeHandsController), "IEventsConsumerOnWeapOut")]
        private static bool DisableGrenadeStuffAfterCancel(BaseGrenadeHandsController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return true;

            VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
            InitVRPatches.rightPointerFinger.enabled = false;
            VRGlobals.emptyHands = null;
            WeaponPatches.pinPulled = false;

            return true;
        }
    }
}
