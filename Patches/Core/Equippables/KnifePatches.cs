using HarmonyLib;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;

namespace TarkovVR.Patches.Core.Equippables
{
    [HarmonyPatch]
    internal class KnifePatches
    {
        public static Transform knifeTransform = null;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.BaseKnifeController), "IEventsConsumerOnWeapIn")]
        private static void MoveKnifeToIKHands(EFT.Player.BaseKnifeController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            EquippablesShared.DisableEquippedRender();
            if (GrenadePatches.grenadeEquipped)
                GrenadePatches.grenadeEquipped = false;

            GrenadePatches.pinPulled = false;

            if (EquippablesShared.currentGunInteractController != null)
            {
                EquippablesShared.currentGunInteractController.enabled = false;
                EquippablesShared.currentGunInteractController = null;
            }

            VRGlobals.player = __instance._player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            if (VRSettings.GetLeftHandedMode())
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            knifeTransform = VRGlobals.emptyHands;
            VRGlobals.usingItem = false;
            VRPlayerManager.leftHandGunIK = __instance.HandsHierarchy.Transforms[10];

            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.gameObject;
                GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                rightHandPositioner.transform.SetParent(__instance.WeaponRoot.transform.parent, false);
                VRGlobals.weaponHolder = new GameObject("weaponHolder");
                VRGlobals.weaponHolder.transform.SetParent(rightHandPositioner.transform, false);
                rightHandPositioner.AddComponent<HandsPositioner>();
                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

            }
            else
            {
                __instance.WeaponRoot.parent.parent.GetComponent<HandsPositioner>().enabled = true;
                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.parent.parent.gameObject;
                VRGlobals.weaponHolder = __instance.WeaponRoot.parent.gameObject;
                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

            }
            if (VRGlobals.player)
            {
                EquippablesShared.previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                //VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
                //VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
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

            // MELEE WEAPONS

            VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.13f, 0f, -0.43f);
            VRGlobals.weaponHolder.transform.localEulerAngles = new Vector3(33f, 312f, 83f);
            VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;

        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.KnifeController), "method_9")]
        private static void UnequipVrKnife(EFT.Player.KnifeController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            knifeTransform = null;
            if (__instance.WeaponRoot.parent.name == "weaponHolder")
            {
                __instance.WeaponRoot.parent.parent.GetComponent<HandsPositioner>().enabled = false;
            }

            VRGlobals.oldWeaponHolder = null;
        }
    }
}
