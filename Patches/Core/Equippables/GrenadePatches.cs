using EFT;
using HarmonyLib;
using System;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Weapons;
using UnityEngine;
using static EFT.Player;
using static FirearmsAnimator;
using Valve.VR;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.Equippables
{
    [HarmonyPatch]
    internal class GrenadePatches
    {
        public static bool pinPulled = false;
        public static bool grenadeEquipped;
        private static Transform oldGrenadeHolder;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GrenadeHandsController), "Spawn")]
        static void HandleGrenade(GrenadeHandsController __instance, float animationSpeed, Action callback)
        {
            var player = __instance._player;

            if (!player.IsYourPlayer)
                return;

            grenadeEquipped = true;
            pinPulled = false;
            InitVRPatches.rightPointerFinger.enabled = false;

            if (EquippablesShared.currentGunInteractController != null)
                EquippablesShared.currentGunInteractController.enabled = false;

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
                    EquippablesShared.currentGunInteractController = __instance.WeaponRoot.parent.GetComponent<GunInteractionController>();
                    EquippablesShared.currentGunInteractController.enabled = true;
                    EquippablesShared.currentGunInteractController.SetPlayerOwner(player.gameObject.GetComponent<GamePlayerOwner>());
                    VRGlobals.weaponHolder = __instance.WeaponRoot.parent.Find("RightHandPositioner").Find("weaponHolder").gameObject;

                }
                else
                {
                    EquippablesShared.currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
                    EquippablesShared.currentGunInteractController.Init();
                    EquippablesShared.currentGunInteractController.initialized = true;
                    EquippablesShared.currentGunInteractController.SetPlayerOwner(player.gameObject.GetComponent<GamePlayerOwner>());

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
                EquippablesShared.weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset("", "grenade");
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
                EquippablesShared.previousLeftHandMarker = VRGlobals.player._markers[0];
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
            VRGlobals.weaponHolder.transform.localPosition = EquippablesShared.weaponOffset;
        }

        // 1. Create a list of GClass2804 with names and actions
        // 2. Create a GClass2805 and assign the list to Actions
        // 3. Run HideoutPlayerOwner.AvailableInteractionState.set_Value(Gclass2805)
        //public static float grenadeOffset = 0;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseGrenadeHandsController), "method_9")]
        private static bool RepositionGrenadeThrow(BaseGrenadeHandsController __instance, ref Vector3? throwPosition, float timeSinceSafetyLevelRemoved, float lowHighThrow, Vector3 direction, float forcePower, bool lowThrow, bool withVelocity)
        {
            if (!__instance._player.IsYourPlayer)
                return true;
            if (!pinPulled)
                return false;

            Vector3 throwVelocity = PickupAndThrowables. GetSteamVRVelocity(SteamVR_Input_Sources.RightHand);
            Vector3 force;
            Vector3 throwPos = PickupAndThrowables.GetSteamVRPosition(SteamVR_Input_Sources.RightHand);
            Quaternion throwRot = PickupAndThrowables.GetSteamVRRotation(SteamVR_Input_Sources.RightHand);

            __instance.firearmsAnimator_0.SetGrenadeFire(EGrenadeFire.Throw);
            __instance.firearmsAnimator_0.SetAnimationSpeed(2f);

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
            /*
            __instance.vmethod_2(
                timeSinceSafetyLevelRemoved,
                VRGlobals.vrPlayer.RightHand.transform.position,
                VRGlobals.vrPlayer.RightHand.transform.rotation,
                force,
                lowThrow
            );
            */
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
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BaseGrenadeHandsController), "vmethod_2")]
        private static void GrenadeAnimationSpeedReset(BaseGrenadeHandsController __instance, float timeSinceSafetyLevelRemoved, Vector3 position, Quaternion rotation, Vector3 force, bool lowThrow)
        {
            __instance.firearmsAnimator_0.SetAnimationSpeed(1f);
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
            pinPulled = false;

            return true;
        }
    }
}
