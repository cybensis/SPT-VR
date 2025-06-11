using EFT;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static EFT.Player;
using UnityEngine;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.Equippables
{
    [HarmonyPatch]
    internal class OtherEquppablesPatches
    {

        //------------------------------------------------------- EMPTY HANDS PATCHES-------------------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.EmptyHandsController), "Spawn")]
        private static void ResetWeaponOnEmptyHands(EFT.Player.EmptyHandsController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            VRGlobals.firearmController = null;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            if (VRSettings.GetLeftHandedMode())
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            VRGlobals.player = __instance._player;

            if (VRGlobals.vrPlayer)
            {
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
                VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
                VRGlobals.ikManager.rightArmIk.solver.target = VRGlobals.vrPlayer.RightHand.transform;
                VRGlobals.ikManager.rightArmIk.enabled = true;
            }
            if (GrenadePatches.grenadeEquipped)
                GrenadePatches.grenadeEquipped = false;

            GrenadePatches.pinPulled = false;

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
        }

        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.EmptyHandsController), "Drop")]
        private static void ResetIKOnExitHands(EFT.Player.EmptyHandsController __instance)
        {

            if (VRGlobals.vrPlayer)
            {
                VRGlobals.ikManager.rightArmIk.solver.target = null;
                VRGlobals.ikManager.rightArmIk.enabled = false;
            }
        }



        //------------------------------------------------------- RANGE FINDER PATCHES-------------------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UsableItemController), "IEventsConsumerOnWeapIn")]
        private static void SetupRangeFinderOnEquip(UsableItemController __instance)
        {
            if (!__instance._player.IsYourPlayer || !(__instance as PortableRangeFinderController))
                return;
            PortableRangeFinderController instance = (PortableRangeFinderController)__instance;
            EquippablesShared.rangeFinder = instance;
            instance.tacticalRangeFinderController_0._boneToCastRay.parent.Find("linza").gameObject.SetActive(false);
            VRGlobals.emptyHands = instance.ControllerGameObject.transform;
            if (VRSettings.GetLeftHandedMode())
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                VRGlobals.oldWeaponHolder = instance.WeaponRoot.parent.gameObject;
                if (instance.WeaponRoot.parent.Find("RightHandPositioner"))
                {
                    VRGlobals.weaponHolder = instance.WeaponRoot.parent.Find("RightHandPositioner").Find("weaponHolder").gameObject;
                }
                else
                {
                    GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                    rightHandPositioner.transform.SetParent(instance.WeaponRoot.transform.parent, false);
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.SetParent(rightHandPositioner.transform, false);
                    HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                    handsPositioner.rightHandIk = rightHandPositioner.transform;
                }
                //__instance.WeaponRoot.transform.parent.GetComponent<Animator>().updateMode = AnimatorUpdateMode.AnimatePhysics;

                instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                //__instance.WeaponRoot.localPosition = Vector3.zero;
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(37, 267, 55);

                //weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset(__instance.weaponPrefab_0.name, __instance.Weapon.WeapClass);

                VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.23f, 0.0689f, -0.23f);
            }
            else if (instance.WeaponRoot.parent.parent.name == "RightHandPositioner")
            {

                VRGlobals.weaponHolder = instance.WeaponRoot.parent.parent.gameObject;
            }
            if (VRGlobals.player)
            {
                EquippablesShared.previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                //VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
            }
            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);
            VRGlobals.oldWeaponHolder.transform.localEulerAngles = new Vector3(340, 340, 0);



        }

        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PortableRangeFinderController), "method_16")]
        private static void UnequipRangeFinder(PortableRangeFinderController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (VRGlobals.oldWeaponHolder != null && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                VRGlobals.weaponHolder.transform.GetChild(0).SetParent(VRGlobals.oldWeaponHolder.transform, false);
                VRGlobals.oldWeaponHolder.transform.localRotation = Quaternion.identity;
                VRGlobals.oldWeaponHolder = null;
                VRGlobals.emptyHands = null;
            }
            EquippablesShared.rangeFinder = null;
        }




        //------------------------------------------------------- COMPASS PATCHES-------------------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------------------------------
        //This method will set the compass position to the right hand
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "PropUpdate")]
        private static bool FixCompassToLeftHand(EFT.Player __instance)
        {
            // Bad way of doing this, it does not need to be set every frame but trying to set in SetCompassState aint working
            if (__instance.IsYourPlayer && __instance._propActive && __instance._compassArrow)
            {
                __instance._compassArrow.transform.parent.localPosition = new Vector3(-0.09f, -0.04f, 0);
                __instance._compassArrow.transform.parent.localEulerAngles = new Vector3(85, 304, 10);
                __instance._propTransforms[1].localRotation = Quaternion.identity;
                __instance._propTransforms[2].localRotation = Quaternion.identity;
                return false;
            }
            else
                return true;
        }

        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), "SetCompassState")]
        private static void SetCompassEquippedFirearm(FirearmController __instance, bool active)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (InitVRPatches.leftPalm && __instance._player._compassArrow)
            {
                __instance._player._compassArrow.transform.parent.SetParent(InitVRPatches.leftPalm, false);
            }
        }

        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GrenadeHandsController), "SetCompassState")]
        private static void SetCompassEquippedGrenade(GrenadeHandsController __instance, bool active)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (InitVRPatches.leftPalm && __instance._player._compassArrow)
            {
                __instance._player._compassArrow.transform.parent.SetParent(InitVRPatches.leftPalm, false);
            }
        }




        //------------------------------------------------------- MEDS/CONSUMABLES PATCHES---------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        private static void SpawnMedsController(MedsController __instance, float animationSpeed, Action callback)
        {
            try
            {
                EFT.Player player = __instance._player;

                if (player == null || !player.IsYourPlayer)
                    return;

                if (VRGlobals.menuOpen)
                {
                    if (EquippablesShared.currentGunInteractController != null)
                    {
                        Transform rightHand = EquippablesShared.currentGunInteractController?.transform.Find("RightHandPositioner");
                        if (rightHand != null)
                        {
                            foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            {
                                if (renderer != null)
                                    renderer.enabled = false;
                            }
                        }
                    }
                }

                if (__instance._controllerObject != null)
                {
                    VRGlobals.emptyHands = __instance._controllerObject.transform;

                    if (VRSettings.GetLeftHandedMode())
                        VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
                }

                if (VRGlobals.ikManager != null)
                {
                    VRGlobals.ikManager.rightArmIk.solver.target = null;
                    VRGlobals.ikManager.rightArmIk.enabled = false;
                    VRGlobals.ikManager.leftArmIk.solver.target = null;
                    VRGlobals.ikManager.leftArmIk.enabled = false;
                }

                VRGlobals.usingItem = true;

                // Safely handle player marker
                if (VRGlobals.player?._markers != null && VRGlobals.player._markers.Length > 0)
                {
                    EquippablesShared.previousLeftHandMarker = VRGlobals.player._markers[0];
                }
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"Error in MedsControllerSpawnPatch: {ex.Message}");
            }
            //Plugin.MyLog.LogError($"[SpawnMedsController] Spawning meds controller for {player} with item {item}");
        }


        //------------------------------------------------------- MISCELLANEOUS PATCHES-------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GetActionsClass.Class1624), "method_0")]
        private static bool PreventUsingStationaryWeapon(GetActionsClass.Class1624 __instance)
        {
            return false;
        }

    }
}
