using HarmonyLib;
using System;
using System.Diagnostics;
using static EFT.Player;
using UnityEngine;
using EFT.CameraControl;
using TarkovVR.Source.Player.VR;
using TarkovVR.Source.Weapons;
using TarkovVR.Source.Player.VRManager;
using EFT.InventoryLogic;
using static EFT.Player.ItemHandsController;
using System.Reflection;
using TarkovVR.Source.Misc;
using EFT.Animations;
using UnityEngine.SocialPlatforms;
using System.Collections.Generic;
using EFT;
using UnityEngine.UI;
using EFT.UI;
using Sirenix.Serialization;
using Valve.VR.InteractionSystem;
using JetBrains.Annotations;
using EFT.AssetsManager;
using EFT.Interactive;
using System.Linq;
using static CoverPointMaster;
using static EFT.Player.GrenadeController;
using static EFT.Player.QuickGrenadeThrowController;
using static UnityEngine.ParticleSystem.PlaybackState;
using UnityEngine.UIElements;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class WeaponPatches
    {
        public static Vector3 weaponOffset = Vector3.zero;
        public static bool grenadeEquipped;
        private static Transform oldGrenadeHolder;
        public static Transform previousLeftHandMarker;
        public static GunInteractionController currentGunInteractController;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.EmptyHandsController), "Spawn")]
        private static void ResetWeaponOnEquipHands(EFT.Player.EmptyHandsController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            VRGlobals.firearmController = null;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            VRGlobals.player = __instance._player;

            if (!__instance.WeaponRoot.parent.GetComponent<GunInteractionController>())
            {
                currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
            }

            if (VRGlobals.vrPlayer)
            {
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
            }
            Plugin.MyLog.LogInfo("EmptyHandsController.Spawn: " + __instance);
            if (grenadeEquipped)
                grenadeEquipped = false;

            VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
            VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;
        }

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(EFT.Player.KnifeController), "Spawn")]
        //private static void SetMeleeWeapon(EFT.Player.KnifeController __instance)
        //{
        //    if (!__instance._player.IsYourPlayer)
        //        return;

        //    //StackTrace stackTrace = new StackTrace();
        //    //bool isBotPlayer = false;

        //    //foreach (var frame in stackTrace.GetFrames())
        //    //{
        //    //    var method = frame.GetMethod();
        //    //    var declaringType = method.DeclaringType.FullName;
        //    //    var methodName = method.Name;

        //    //    // Check for bot-specific methods
        //    //    if (declaringType.Contains("EFT.BotSpawner") || declaringType.Contains("GClass732") && methodName.Contains("ActivateBot"))
        //    //    {
        //    //        isBotPlayer = true;
        //    //        break;
        //    //    }
        //    //}
        //    //if (isBotPlayer)
        //    //{
        //    //    // This is a bot player, so do not execute the rest of the code
        //    //    Plugin.MyLog.LogWarning("Wasn't AI but is bot, is my player: " + __instance._player.IsYourPlayer);
        //    //    return;
        //    //}
        //    VRGlobals.firearmController = null;
        //    VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
        //    VRGlobals.player = __instance._player;

        //    if (!__instance.WeaponRoot.parent.GetComponent<GunInteractionController>())
        //    {
        //        currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
        //    }

        //    if (VRGlobals.vrPlayer)
        //    {
        //        VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
        //        VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
        //    }
        //    Plugin.MyLog.LogInfo("KnifeController.Spawn: " + __instance);
        //    if (grenadeEquipped)
        //        grenadeEquipped = false;

        //    VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
        //}


        public static PortableRangeFinderController rangeFinder;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UsableItemController), "IEventsConsumerOnWeapIn")]
        private static void SetRangeFinder(UsableItemController __instance)
        {
            if (!__instance._player.IsYourPlayer || !(__instance as PortableRangeFinderController))
                return;
            PortableRangeFinderController instance = (PortableRangeFinderController) __instance;
            Plugin.MyLog.LogWarning("PortableRangeFinderController::IEventsConsumerOnWeapIn");
            rangeFinder = instance;
            instance.tacticalRangeFinderController_0._boneToCastRay.parent.FindChild("linza").gameObject.SetActive(false);
            VRGlobals.emptyHands = instance.ControllerGameObject.transform;
            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                VRGlobals.oldWeaponHolder = instance.WeaponRoot.parent.gameObject;
                if (instance.WeaponRoot.parent.FindChild("RightHandPositioner"))
                {
                    VRGlobals.weaponHolder = instance.WeaponRoot.parent.FindChild("RightHandPositioner").GetChild(0).gameObject;
                }
                else
                {
                    GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                    rightHandPositioner.transform.parent = instance.WeaponRoot.transform.parent;
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.parent = rightHandPositioner.transform;
                    HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                    handsPositioner.rightHandIk = rightHandPositioner.transform;
                }
                //__instance.WeaponRoot.transform.parent.GetComponent<Animator>().updateMode = AnimatorUpdateMode.AnimatePhysics;

                instance.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
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
                previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                //VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
            }
            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);
            VRGlobals.oldWeaponHolder.transform.localEulerAngles = new Vector3(340, 340, 0);



        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(PortableRangeFinderController), "method_16")]
        private static void UnequipRangeFinder(PortableRangeFinderController __instance)
        {
            if (!__instance._player.IsYourPlayer )
                return;

            //Plugin.MyLog.LogWarning("PortableRangeFinderController::IEventsConsumerOnWeapOut  " + VRGlobals.emptyHands + "   |   " + VRGlobals.weaponHolder + "   |   " + VRGlobals.oldWeaponHolder);
            if (VRGlobals.oldWeaponHolder != null && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                VRGlobals.weaponHolder.transform.GetChild(0).parent = VRGlobals.oldWeaponHolder.transform;
                VRGlobals.oldWeaponHolder.transform.localRotation = Quaternion.identity;
                VRGlobals.oldWeaponHolder = null;
                VRGlobals.emptyHands = null;
            }
            rangeFinder = null;
        }



        private static bool compassEquipped = false;
        //This method will set the compass position to the right hand
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "PropUpdate")]
        private static bool FixCompassToLeftHand(EFT.Player __instance)
        {
            // Bad way of doing this, it does not need to be set every frame but trying to set in SetCompassState aint working
            if (__instance.IsYourPlayer && __instance._propActive && __instance._compassArrow) {
                //__instance._compassArrow.transform.parent.localPosition = new Vector3(0.1851f, -0.4271f, -0.0684f);
                //__instance._compassArrow.transform.parent.localEulerAngles = new Vector3(55, 40, 11);

                __instance._compassArrow.transform.parent.localPosition = new Vector3(-0.09f, -0.04f, 0);
                __instance._compassArrow.transform.parent.localEulerAngles = new Vector3(85, 304, 10);
                __instance._propTransforms[1].localRotation = Quaternion.identity;
                __instance._propTransforms[2].localRotation = Quaternion.identity;
                return false;
            }
            else
                return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), "SetCompassState")]
        private static void SetCompassEquippedFirearm(FirearmController __instance, bool active)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (InitVRPatches.leftPalm && __instance._player._compassArrow) {
                compassEquipped = active;
                __instance._player._compassArrow.transform.parent.parent = InitVRPatches.leftPalm;
                //__instance.WaitFrames(5, delegate {
                //    __instance._player._compassArrow.transform.parent.localPosition = new Vector3(0.1851f, -0.4271f, -0.0684f);
                //    __instance._player._compassArrow.transform.parent.localEulerAngles = new Vector3(55, 40, 11);
                //});
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GrenadeController), "SetCompassState")]
        private static void SetCompassEquippedGrenade(GrenadeController __instance, bool active)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (InitVRPatches.leftPalm && __instance._player._compassArrow)
            {
                compassEquipped = active;
                __instance._player._compassArrow.transform.parent.parent = InitVRPatches.leftPalm;
                //__instance.WaitFrames(5, delegate { 
                //    __instance._player._compassArrow.transform.parent.localPosition = new Vector3(0.1851f, -0.4271f, -0.0684f);
                //    __instance._player._compassArrow.transform.parent.localEulerAngles = new Vector3(55, 40, 11);
                //});
            }
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "IEventsConsumerOnWeapOut")]
        private static void ReturnWeaponToOriginalParentOnChange(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            // Check if a weapon is currently equipped, if that weapon isn the same as the one trying to be equipped, and that the weaponHolder actually has something there
            if (VRGlobals.oldWeaponHolder != null && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                VRGlobals.weaponHolder.transform.GetChild(0).parent = VRGlobals.oldWeaponHolder.transform;
                VRGlobals.oldWeaponHolder.transform.localRotation = Quaternion.identity;
                VRGlobals.oldWeaponHolder = null;
                currentGunInteractController.enabled = false;
                VRGlobals.emptyHands = null;
            }
            VRGlobals.vrPlayer.isWeapPistol = false;

        }



        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "IEventsConsumerOnWeapIn")]
        private static void FinishMovingWeaponToVrHands(EFT.Player.FirearmController __instance)
        {


            if (!__instance._player.IsYourPlayer)
                return;
            Plugin.MyLog.LogInfo("FirearmController.IEventsConsumerOnWeapIn: " + __instance);

            if (grenadeEquipped)
                grenadeEquipped = false;
            if (currentGunInteractController)
            {
                if (!currentGunInteractController.initialized)
                    currentGunInteractController.CreateRaycastReceiver(__instance.GunBaseTransform, __instance.WeaponLn);
                currentGunInteractController.enabled = true;
            }

            if (!VRGlobals.vrPlayer.isSupporting)
                __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);
            else {
                VRGlobals.weaponHolder.transform.localPosition = new Vector3(-0.1927f, -0.1642f, -0.2195f);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(355, 15, 85);
            }

            VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
            VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;

            VRGlobals.vrPlayer.isWeapPistol = (__instance.Weapon.WeapClass == "pistol");
        }


        private static Transform knifeTransform = null;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.BaseKnifeController), "IEventsConsumerOnWeapIn")]
        private static void MoveKnifeToIKHands(EFT.Player.BaseKnifeController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (grenadeEquipped)
                grenadeEquipped = false;

            if (currentGunInteractController != null) { 
                currentGunInteractController.enabled = false;
                currentGunInteractController = null;
            }

            Plugin.MyLog.LogWarning("Move knife to VR hands: " + __instance);
            VRGlobals.player = __instance._player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            knifeTransform = VRGlobals.emptyHands;
            VRGlobals.usingItem = false;
            VRPlayerManager.leftHandGunIK = __instance.HandsHierarchy.Transforms[10];

            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.gameObject;
                GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                rightHandPositioner.transform.parent = __instance.WeaponRoot.transform.parent;
                VRGlobals.weaponHolder = new GameObject("weaponHolder");
                VRGlobals.weaponHolder.transform.parent = rightHandPositioner.transform;
                rightHandPositioner.AddComponent<HandsPositioner>();
                //if (__instance.WeaponRoot.parent.FindChild("RightHandPositioner"))
                //{
                //    VRGlobals.weaponHolder = __instance.WeaponRoot.parent.FindChild("RightHandPositioner").GetChild(0).gameObject;
                //    VRGlobals.weaponHolder.GetComponent<HandsPositioner>().enabled = true;
                //}
                //else
                //{
                //    //HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                //    //handsPositioner.rightHandIk = rightHandPositioner.transform;
                //}
                //__instance.WeaponRoot.transform.parent.GetComponent<Animator>().updateMode = AnimatorUpdateMode.AnimatePhysics;

                __instance.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

                //weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset(__instance.weaponPrefab_0.name, __instance.Weapon.WeapClass);
                //float weaponAngleOffset = VRSettings.GetWeaponAngleOffset();
                //if (weaponAngleOffset < 50)
                //{
                //    // if the angle is less than 50, get how much less than 50 it is, divide by 100 to get a percent, then multiply our offset by it
                //    float rotOffsetMultiplier = (50 - weaponAngleOffset) / 100;
                //    weaponOffset += new Vector3(0.08f, 0, -0.02f) * rotOffsetMultiplier;
                //}
                //else if (weaponAngleOffset > 50)
                //{
                //    // if the angle is less than 50, get how much less than 50 it is, divide by 100 to get a percent, then multiply our offset by it
                //    float rotOffsetMultiplier = (weaponAngleOffset - 50) / 100;
                //    weaponOffset += new Vector3(-0.01f, -0.01f, +0.04f) * rotOffsetMultiplier;
                //}
                //VRGlobals.weaponHolder.transform.localPosition = weaponOffset;
            }
            else {
                __instance.WeaponRoot.parent.parent.GetComponent<HandsPositioner>().enabled = true;
                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.parent.parent.gameObject;
                VRGlobals.weaponHolder = __instance.WeaponRoot.parent.gameObject;
                __instance.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

            }
            if (VRGlobals.player)
            {
                previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                //VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
            }
            // MELEE WEAPONS
            // weaponHolder local pos = 0.13 0 -0.43
            // weaponHolder local rot = 33.2821 312.3405 83.0464
            VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.13f, 0f, -0.43f);
            VRGlobals.weaponHolder.transform.localEulerAngles = new Vector3(33f, 312f, 83f);
            VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.KnifeController), "method_10")]
        private static void UnequipVrKnife(EFT.Player.KnifeController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            knifeTransform = null;
            Plugin.MyLog.LogWarning("BaseKnifeController unequip");
            if (__instance.WeaponRoot.parent.name == "weaponHolder")
            {
                __instance.WeaponRoot.parent.parent.GetComponent<HandsPositioner>().enabled = false;
            }

            // Check if a weapon is currently equipped, if that weapon isn the same as the one trying to be equipped, and that the weaponHolder actually has something there
            //if (VRGlobals.oldWeaponHolder != null && VRGlobals.weaponHolder.transform.childCount > 0)
            //{
            //    VRGlobals.weaponHolder.transform.GetChild(0).parent = VRGlobals.oldWeaponHolder.transform;
            //    VRGlobals.oldWeaponHolder.transform.localRotation = Quaternion.identity;
            //}
                VRGlobals.oldWeaponHolder = null;
                //VRGlobals.emptyHands = null;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "method_46")]
        private static void AddNewMagToInteractionController(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;


            if (__instance.weaponPrefab_0)
            {
                if (__instance.weaponPrefab_0.Renderers != null)
                {
                    for (int i = 0; i < __instance.weaponPrefab_0.Renderers.Length; i++)
                    {
                        if (__instance.weaponPrefab_0.Renderers[i].transform.parent.GetComponent<MagazineInHandsVisualController>())
                        {
                            currentGunInteractController.SetMagazine(__instance.weaponPrefab_0.Renderers[i].transform, false);
                            return;
                        }
                    }

                }
            }
            //Plugin.MyLog.LogWarning(__instance.transform.root+"\n\n\n");
            //Plugin.MyLog.LogWarning(new StackTrace());

        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "method_43")]
        private static void AddNewTacDeviceToInteractionController(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer || __instance.weaponManagerClass == null)
                return;


            if (__instance.weaponManagerClass.tacticalComboVisualController_0 != null) { 
                for (int i = 0; i < __instance.weaponManagerClass.tacticalComboVisualController_0.Length; i++)
                {
                    if (!currentGunInteractController.TacDeviceAlreadyRegistered(__instance.weaponManagerClass.tacticalComboVisualController_0[i].transform))
                        currentGunInteractController.AddTacticalDevice(__instance.weaponManagerClass.tacticalComboVisualController_0[i].transform, __instance.FirearmsAnimator);
                }
            }
            if (__instance.weaponManagerClass.sightModVisualControllers_0 == null)
                return;


            for (int i = 0; i < __instance.weaponManagerClass.sightModVisualControllers_0.Length; i++)
            {

                //if (__instance.weaponManagerClass.sightModVisualControllers_0[i].name == "scope_all_eotech_hhs_1_tan(Clone)") { 

                //}
                if (__instance.weaponManagerClass.sightModVisualControllers_0[i].scopePrefabCache_0 == null)
                    continue;
                VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.FindChild("mod_aim_camera");
                // Some scopes have more than two modes or something which changes the name to 001, 002 etc,
                if (!VRGlobals.scope)
                    VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.FindChild("mod_aim_camera_001");

                if (VRGlobals.scope != null) {
                    SightModVisualControllers visualController = __instance.weaponManagerClass.sightModVisualControllers_0[i].GetComponent<SightModVisualControllers>();
                    if (!visualController)
                        visualController = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.parent.GetComponent<SightModVisualControllers>();
                    
                    if (visualController && VRGlobals.vrOpticController)
                    {
                        BoxCollider scopeCollider = visualController.GetComponent<BoxCollider>();
                     
                        if (scopeCollider)
                        {
                            scopeCollider.gameObject.layer = 6;
                            scopeCollider.size = new Vector3(0.09f, 0.04f, 0.02f);
                            scopeCollider.center = new Vector3(-0.04f, 0, -0.075f);
                            scopeCollider.enabled = true;
                        }
                    }
                    break;
                }
            }



            //Plugin.MyLog.LogWarning(__instance.transform.root+"\n\n\n");
            //Plugin.MyLog.LogWarning(new StackTrace());

        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public static List<Transform> weaponInteractables;
        public static Transform gunCollider;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void MoveWeaponToIKHands(EFT.Player.FirearmController __instance)
        {



            if (!__instance._player.IsYourPlayer)
                return;


            if (grenadeEquipped)
                grenadeEquipped = false;

            if (currentGunInteractController != null)
                currentGunInteractController.enabled = false;

            Plugin.MyLog.LogWarning("Init calc: " + __instance);
            VRGlobals.firearmController = __instance;
            VRGlobals.player = __instance._player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            VRGlobals.usingItem = false;
            VRGlobals.scope = null;
            for (int i = 0; i < __instance.weaponManagerClass.sightModVisualControllers_0.Length; i++)
            {
                VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.FindChild("mod_aim_camera");
                if (__instance.weaponManagerClass.sightModVisualControllers_0[i].scopePrefabCache_0 == null)
                    continue;
                // Some scopes have more than two modes or something which changes the name to 001, 002 etc,
                if (!VRGlobals.scope)
                    VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.FindChild("mod_aim_camera_001");

                if (VRGlobals.scope != null)
                {
                    SightModVisualControllers visualController = __instance.weaponManagerClass.sightModVisualControllers_0[i].GetComponent<SightModVisualControllers>();
                    if (!visualController)
                        visualController = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.parent.GetComponent<SightModVisualControllers>();

                    if (visualController && VRGlobals.vrOpticController)
                    {
                        BoxCollider scopeCollider = visualController.GetComponent<BoxCollider>();

                        if (scopeCollider)
                        {
                            scopeCollider.gameObject.layer = 6;
                            scopeCollider.size = new Vector3(0.09f, 0.04f, 0.02f);
                            scopeCollider.center = new Vector3(-0.04f, 0, -0.075f);
                            scopeCollider.isTrigger = true;
                            scopeCollider.enabled = true;
                        }
                    }
                    break;
                }
            }
            VRPlayerManager.leftHandGunIK = __instance.HandsHierarchy.Transforms[10];

            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.gameObject;
                if (__instance.WeaponRoot.parent.FindChild("RightHandPositioner"))
                {
                    currentGunInteractController = __instance.WeaponRoot.parent.GetComponent<GunInteractionController>();
                    currentGunInteractController.SetPlayerOwner(__instance._player.gameObject.GetComponent<GamePlayerOwner>());
                    VRGlobals.weaponHolder = __instance.WeaponRoot.parent.FindChild("RightHandPositioner").GetChild(0).gameObject;
                }
                else
                {
                    currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
                    currentGunInteractController.Init();
                    if (Camera.main.gameObject.GetComponent<HighLightMesh>())
                        currentGunInteractController.SetHighlightComponent(Camera.main.gameObject.GetComponent<HighLightMesh>());
                    else
                    {
                        HighLightMesh highLightMesh = Camera.main.gameObject.AddComponent<HighLightMesh>();
                        highLightMesh.Mat = new Material(Shader.Find("Hidden/HighLightMesh"));
                        highLightMesh.LineWidth = 2;
                        highLightMesh.Always = true;
                        highLightMesh.Color = Color.white;
                        currentGunInteractController.SetHighlightComponent(highLightMesh);
                    }
                    currentGunInteractController.SetPlayerOwner(__instance._player.gameObject.GetComponent<GamePlayerOwner>());
                    WeaponMeshParts weaponHighlightParts = WeaponMeshList.GetWeaponMeshList(__instance.WeaponRoot.transform.root.name);
                    Transform weaponMeshRoot = __instance.GunBaseTransform.GetChild(0);
                    if (weaponHighlightParts != null)
                    {
                        foreach (string magazineMesh in weaponHighlightParts.magazine)
                        {
                            Transform magazineMeshTransform = weaponMeshRoot.FindChildRecursive(magazineMesh);
                            if (magazineMeshTransform) { 
                                if (magazineMeshTransform.childCount > 0)
                                    currentGunInteractController.SetMagazine(magazineMeshTransform.GetChild(0), false);
                                else 
                                    currentGunInteractController.SetMagazine(weaponMeshRoot.FindChildRecursive(magazineMesh), false);
                            }
                        }
                        foreach (string chamberMesh in weaponHighlightParts.chamber)
                        {
                            if (weaponMeshRoot.FindChildRecursive(chamberMesh))
                                currentGunInteractController.SetChargingHandleOrBolt(weaponMeshRoot.FindChildRecursive(chamberMesh), false);
                        }
                        foreach (string firingModeSwitch in weaponHighlightParts.firingModeSwitch)
                        {
                            if (weaponMeshRoot.FindChildRecursive(firingModeSwitch))
                                currentGunInteractController.SetFireModeSwitch(weaponMeshRoot.FindChildRecursive(firingModeSwitch));
                        }

                        for (int i = 0; i < __instance.weaponManagerClass.sightModVisualControllers_0.Length; i++)
                        {

                            //if (__instance.weaponManagerClass.sightModVisualControllers_0[i].name == "scope_all_eotech_hhs_1_tan(Clone)") { 

                            //}
                            if (__instance.weaponManagerClass.sightModVisualControllers_0[i].scopePrefabCache_0 == null)
                                continue;
                            VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.FindChild("mod_aim_camera");

                            // Some scopes have more than two modes or something which changes the name to 001, 002 etc,
                            if (!VRGlobals.scope)
                                VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.FindChild("mod_aim_camera_001");

                            if (VRGlobals.scope != null)
                            {
                                SightModVisualControllers visualController = __instance.weaponManagerClass.sightModVisualControllers_0[i].GetComponent<SightModVisualControllers>();
                                if (!visualController)
                                    visualController = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.parent.GetComponent<SightModVisualControllers>();

                                if (visualController && VRGlobals.vrOpticController)
                                {
                                    BoxCollider scopeCollider = visualController.GetComponent<BoxCollider>();

                                    if (scopeCollider)
                                    {
                                        scopeCollider.gameObject.layer = 6;
                                        scopeCollider.size = new Vector3(0.09f, 0.04f, 0.02f);
                                        scopeCollider.center = new Vector3(-0.04f, 0, -0.075f);
                                        scopeCollider.isTrigger = true;
                                        scopeCollider.enabled = true;
                                    }
                                }
                                break;
                            }
                        }
                        //foreach (string firingModeSwitch in weaponHighlightParts.stock)
                        //{
                        //    if (weaponMeshRoot.FindChildRecursive(firingModeSwitch))
                        //        currentGunInteractController.SetFireModeSwitch(weaponMeshRoot.FindChildRecursive(firingModeSwitch));
                        //}
                    }
                    if (__instance.weaponManagerClass != null && __instance.weaponManagerClass.tacticalComboVisualController_0 != null)
                    {
                        for (int i = 0; i < __instance.weaponManagerClass.tacticalComboVisualController_0.Length; i++)
                        {
                            currentGunInteractController.AddTacticalDevice(__instance.weaponManagerClass.tacticalComboVisualController_0[i].transform, __instance.FirearmsAnimator);
                        }
                    }


                    GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                    rightHandPositioner.transform.parent = __instance.WeaponRoot.transform.parent;
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.parent = rightHandPositioner.transform;
                    HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                    handsPositioner.rightHandIk = rightHandPositioner.transform;
                }
                //__instance.WeaponRoot.transform.parent.GetComponent<Animator>().updateMode = AnimatorUpdateMode.AnimatePhysics;

                __instance.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
                //__instance.WeaponRoot.localPosition = Vector3.zero;
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

                weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset(__instance.weaponPrefab_0.name, __instance.Weapon.WeapClass);
                float weaponAngleOffset = VRSettings.GetWeaponAngleOffset();
                if (weaponAngleOffset < 50) {
                    // if the angle is less than 50, get how much less than 50 it is, divide by 100 to get a percent, then multiply our offset by it
                    float rotOffsetMultiplier = (50 - weaponAngleOffset) / 100;
                    weaponOffset += new Vector3(0.08f, 0, -0.02f) * rotOffsetMultiplier;
                }
                else if (weaponAngleOffset > 50)
                {
                    // if the angle is less than 50, get how much less than 50 it is, divide by 100 to get a percent, then multiply our offset by it
                    float rotOffsetMultiplier = (weaponAngleOffset - 50) / 100;
                    weaponOffset += new Vector3(-0.01f, -0.01f, +0.04f) * rotOffsetMultiplier;
                }
                VRGlobals.weaponHolder.transform.localPosition = weaponOffset;
            }
            else if (__instance.WeaponRoot.parent.FindChild("RightHandPositioner"))
            {

                VRGlobals.weaponHolder = __instance.WeaponRoot.parent.FindChild("RightHandPositioner").gameObject;
                __instance.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
            }
            if (VRGlobals.player)
            {
                previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                //VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
            }
            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f); 
            // Don't use canted sights or rear sights
            if (__instance._player.ProceduralWeaponAnimation._targetScopeRotationDeg != 0 || __instance._player.ProceduralWeaponAnimation.CurrentScope.Bone.parent.name.Contains("rear"))
            {
                int i = 0;
                int firstScope = __instance.Item.AimIndex.Value;
                __instance.ChangeAimingMode();
                while (__instance.Item.AimIndex.Value != firstScope && (__instance._player.ProceduralWeaponAnimation._targetScopeRotationDeg != 0 || __instance._player.ProceduralWeaponAnimation.CurrentScope.Bone.parent.name.Contains("rear")))
                {
                    __instance.ChangeAimingMode();
                }
            }
            //VRGlobals.oldWeaponHolder.transform.localEulerAngles = new Vector3(340, 340, 0);
            //VRGlobals.weaponHolder.transform.localPosition = new Vector3(0, 0, -0.34f);
            //VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(0, 0, 90);
            //VRGlobals.weaponHolder.transform.GetChild(0).localPosition = Vector3.zero;
            VRGlobals.vrPlayer.isWeapPistol = (__instance.Weapon.WeapClass == "pistol");
        }
        private static Transform currentScope;
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        //NOTE:::::::::::::: Height over bore is the reason why close distances shots aren't hitting, but further distance shots SHOULD be fine - test this
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void SetOpticCamFoV(EFT.CameraControl.OpticComponentUpdater __instance, OpticSight opticSight)
        {

            SightModVisualControllers visualController = opticSight.GetComponent<SightModVisualControllers>();
            if (!visualController)
                visualController = opticSight.transform.parent.GetComponent<SightModVisualControllers>();
            float fov = 27;
            if (rangeFinder) { 
                fov = 3.2f;
                opticSight.transform.FindChild("linza").gameObject.SetActive(true);
            }


            if (visualController && VRGlobals.vrOpticController)
            {
                VRGlobals.scopeSensitivity = visualController.sightComponent_0.GetCurrentSensitivity;
                // Check if it's the scope we're using then just assign the current fov to it
                if (currentScope == __instance.transform_0)
                {
                    fov = VRGlobals.vrOpticController.currentFov;
                }
                else
                {
                    currentScope = __instance.transform_0;
                    VRGlobals.vrOpticController.scopeCamera = __instance.camera_0;
                    float zoomLevel = visualController.sightComponent_0.GetCurrentOpticZoom();
                    string scopeName = opticSight.name;
                    // For scopes that have multiple levels of zoom of different zoom effects (e.g. changing sight lines from black to red), opticSight will be stored in 
                    // mode_000, mode_001, etc, and that will be stored in the scope game object, so we need to get parent name for scopes with multiple settings
                    BoxCollider scopeCollider;
                    if (scopeName.Contains("mode_"))
                    {
                        if (__instance.transform_0)
                            VRGlobals.vrPlayer.scopeUiPosition = __instance.transform_0.parent.FindChild("backLens");
                        scopeName = opticSight.transform.parent.name;
                        opticSight.transform.parent.gameObject.layer = 6;
                        scopeCollider = opticSight.transform.parent.GetComponent<BoxCollider>();
                    }
                    else
                    {
                        if (__instance.transform_0)
                            VRGlobals.vrPlayer.scopeUiPosition = __instance.transform_0.FindChild("backLens");
                        opticSight.gameObject.layer = 6;
                        scopeCollider = opticSight.GetComponent<BoxCollider>();
                    }
                    if (scopeCollider)
                    {
                        scopeCollider.size = new Vector3(0.09f, 0.04f, 0.02f);
                        scopeCollider.center = new Vector3(-0.04f, 0, -0.075f);
                        scopeCollider.isTrigger = true;
                        scopeCollider.enabled = true;
                    }
                    fov = ScopeManager.GetFOV(scopeName, zoomLevel);
                    VRGlobals.vrOpticController.minFov = ScopeManager.GetMinFOV(scopeName);
                    VRGlobals.vrOpticController.maxFov = ScopeManager.GetMaxFOV(scopeName);
                    VRGlobals.vrOpticController.currentFov = fov;
                }

                if (opticSight.name.Contains("mode_"))
                    opticSight.transform.parent.GetComponent<BoxCollider>().enabled = true;
                else
                    opticSight.GetComponent<BoxCollider>().enabled = true;



            }
            __instance.camera_0.fieldOfView = fov;


            // The SightModeVisualControllers on the scopes contains sightComponent_0 which has a function GetCurrentOpticZoom which returns the zoom

        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Collimators try to do some stupid shit which stops them from displaying so disable it here
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(CameraClass), "method_12")]
        //private static bool FixCollimatorSights(CameraClass __instance)
        //{
        //    return false;
        //}

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GetActionsClass.Class1519), "method_0")]
        private static bool PreventUsingStationaryWeapon(GetActionsClass.Class1519 __instance)
        {
            return false;
        }

        public static void DropObject(LootItem val)
        {
            AssetPoolObject component = val.GetComponent<AssetPoolObject>();
            GameObject gameObject = val.gameObject;

            float makeVisibleAfterDelay = 0.15f;
            val._rigidBody = val.gameObject.GetComponent<Rigidbody>();
            if (val._rigidBody == null)
            {
                val._rigidBody = val.gameObject.AddComponent<Rigidbody>();
            }
            if (gameObject.activeInHierarchy)
            {
                val.method_3();
            }
            else
            {
                val.bool_2 = true;
            }
            if (component != null)
            {
                component.RegisteredComponentsToClean.Add(val._rigidBody);
            }
            val._rigidBody.mass = val.item_0.GetSingleItemTotalWeight();
            val._rigidBody.isKinematic = false;
            val._currentPhysicsTime = 0f;
            val.method_1(val._rigidBody.centerOfMass);
            List<Collider> colliders = component.GetColliders(includeNestedAssetPoolObjects: true);
            if (colliders.Count == 0)
            {
                Plugin.MyLog.LogError("No colliders found on item: " + gameObject.name);
            }
            else
            {
                LootItem.smethod_1(gameObject, colliders, val._boundCollider);
            }
            val._cullingRegisterRadius = 0.005f;
            Vector3 size = val._boundCollider.size;
            if (size.x * size.y * size.z <= EFTHardSettings.Instance.LootVolumeForHighQuallityPhysicsClient)
            {
                val._rigidBody.collisionDetectionMode = CollisionDetectionMode.Continuous;
            }
            else
            {
                val._rigidBody.collisionDetectionMode = CollisionDetectionMode.Discrete;
            }
            val.OnRigidbodyStarted();

            if (makeVisibleAfterDelay > 0f)
            {
                val.method_10(isVisible: false);
                val.StartCoroutine(val.method_11(makeVisibleAfterDelay));
            }
        }

        [HarmonyPatch]
        public class ItemHandsControllerPatch
        {
            //// This method is called to dynamically determine the method to patch
            static MethodBase TargetMethod()
            {
                // Get the method info for the generic method
                MethodInfo method = typeof(MedsController).GetMethod("smethod_5", BindingFlags.Static | BindingFlags.Public);

                // Make the generic method
                MethodInfo genericMethod = method.MakeGenericMethod(typeof(MedsController));

                return genericMethod;
            }

            //// Define the prefix method
            static void Postfix(EFT.Player player, Item item, EBodyPart bodyPart, float amount, int animationVariant, MedsController __result)
            {
                if (!player.IsYourPlayer)
                    return;

                VRGlobals.emptyHands = __result._controllerObject.transform;
                //VRGlobals.ikManager.rightArmIk.solver.target = null;
                //VRGlobals.ikManager.leftArmIk.solver.target = null;
                VRGlobals.usingItem = true;

                //    grenadeEquipped = true;
                //    InitVRPatches.rightPointerFinger.enabled = false;
                //    if (currentGunInteractController != null)
                //        currentGunInteractController.enabled = false;

                //    //VRGlobals.firearmController = __result;
                //    VRGlobals.player = player;
                //    VRGlobals.emptyHands = __result._controllerObject.transform;
                //    VRGlobals.usingItem = true;

                //    VRPlayerManager.leftHandGunIK = __result.HandsHierarchy.Transforms[10];
                //    VRGlobals.oldWeaponHolder = __result.HandsHierarchy.gameObject;
                //    if (__result.WeaponRoot.parent.name != "weaponHolder")
                //    {

                //        if (__result.WeaponRoot.parent.FindChild("RightHandPositioner"))
                //        {
                //            VRGlobals.weaponHolder = __result.WeaponRoot.parent.FindChild("RightHandPositioner").GetChild(0).gameObject;
                //        }
                //        else
                //        {
                //            GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                //            rightHandPositioner.transform.parent = __result.WeaponRoot.transform.parent;
                //            VRGlobals.weaponHolder = new GameObject("weaponHolder");
                //            VRGlobals.weaponHolder.transform.parent = rightHandPositioner.transform;
                //            HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                //            handsPositioner.rightHandIk = rightHandPositioner.transform;
                //        }
                //        //__instance.WeaponRoot.transform.parent.GetComponent<Animator>().updateMode = AnimatorUpdateMode.AnimatePhysics;
                //        __result.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
                //        //__instance.WeaponRoot.localPosition = Vector3.zero;
                //        VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                //        weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset("", "grenade");

                //    }
                //    else if (__result.WeaponRoot.parent.parent.name == "RightHandPositioner")
                //    {
                //        VRGlobals.weaponHolder = __result.WeaponRoot.parent.gameObject;
                //        __result.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
                //    }
                //    if (VRGlobals.player)
                //    {
                //        previousLeftHandMarker = VRGlobals.player._markers[0];
                //        VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                //        //VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
                //    }

                //    __result.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);

                //    VRGlobals.oldWeaponHolder.transform.localEulerAngles = new Vector3(340, 340, 0);
                //    VRGlobals.weaponHolder.transform.localPosition = weaponOffset;
                //    //VRGlobals.ikManager.rightArmIk.solver.target = null;
                //    //VRGlobals.ikManager.leftArmIk.solver.target = null;
            }
        }


        [HarmonyPatch]
        public class GrenadeHandsControllerPatch
        {
            //// This method is called to dynamically determine the method to patch
            static MethodBase TargetMethod()
            {

                // Get the method info for the generic method
                MethodInfo method = typeof(GrenadeController).GetMethod("smethod_8", BindingFlags.Static | BindingFlags.Public);

                // Make the generic method
                MethodInfo genericMethod = method.MakeGenericMethod(typeof(GrenadeController));

                return genericMethod;
            }

            //// Define the prefix method
            static void Postfix(EFT.Player player, GClass2739 item, GrenadeController __result)
            {
                if (!player.IsYourPlayer)
                    return;


                Plugin.MyLog.LogWarning("Equipping grenade  " + __result);
                grenadeEquipped = true;
                InitVRPatches.rightPointerFinger.enabled = false;
                if (currentGunInteractController != null)
                    currentGunInteractController.enabled = false;

                //VRGlobals.firearmController = __result;
                VRGlobals.player = player;
                VRGlobals.emptyHands = __result.ControllerGameObject.transform;
                VRGlobals.usingItem = false;

                VRPlayerManager.leftHandGunIK = __result.HandsHierarchy.Transforms[10];
                VRGlobals.oldWeaponHolder = __result.HandsHierarchy.gameObject;
                if (__result.WeaponRoot.parent.name != "weaponHolder")
                {

                    if (__result.WeaponRoot.parent.FindChild("RightHandPositioner"))
                    {
                        currentGunInteractController = __result.WeaponRoot.parent.GetComponent<GunInteractionController>();
                        currentGunInteractController.SetPlayerOwner(__result._player.gameObject.GetComponent<GamePlayerOwner>());
                        VRGlobals.weaponHolder = __result.WeaponRoot.parent.FindChild("RightHandPositioner").GetChild(0).gameObject;
                    }
                    else
                    {
                        currentGunInteractController = __result.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
                        currentGunInteractController.Init();
                        currentGunInteractController.SetPlayerOwner(__result._player.gameObject.GetComponent<GamePlayerOwner>());

                        GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                        rightHandPositioner.transform.parent = __result.WeaponRoot.transform.parent;
                        VRGlobals.weaponHolder = new GameObject("weaponHolder");
                        VRGlobals.weaponHolder.transform.parent = rightHandPositioner.transform;
                        HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                        handsPositioner.rightHandIk = rightHandPositioner.transform;
                    }
                    //__instance.WeaponRoot.transform.parent.GetComponent<Animator>().updateMode = AnimatorUpdateMode.AnimatePhysics;
                    __result.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
                    //__instance.WeaponRoot.localPosition = Vector3.zero;
                    VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                    weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset("", "grenade");

                }
                else if (__result.WeaponRoot.parent.parent.name == "RightHandPositioner")
                {
                    VRGlobals.weaponHolder = __result.WeaponRoot.parent.gameObject;
                    __result.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
                }
                if (VRGlobals.player)
                {
                    previousLeftHandMarker = VRGlobals.player._markers[0];
                    VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                    //VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
                }

                __result.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);

                VRGlobals.oldWeaponHolder.transform.localEulerAngles = new Vector3(340, 340, 0);
                VRGlobals.weaponHolder.transform.localPosition = weaponOffset;
                //VRGlobals.ikManager.rightArmIk.solver.target = null;
                //VRGlobals.ikManager.leftArmIk.solver.target = null;
            }
        }

        // 1. Create a list of GClass2804 with names and actions
        // 2. Create a GClass2805 and assign the list to Actions
        // 3. Run HideoutPlayerOwner.AvailableInteractionState.set_Value(Gclass2805)
        //public static float grenadeOffset = 0;

        public static bool pinPulled = false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseGrenadeController), "method_7")]
        private static bool RepositionGrenadeThrow(BaseGrenadeController __instance, float timeSinceSafetyLevelRemoved, float lowHighThrow, Vector3 direction, float forcePower, bool lowThrow)
        {
            if (!__instance._player.IsYourPlayer)
                return true;

            //Vector3 newDirection = (InitVRPatches.rightPointerFinger.transform.forward * VRGlobals.grenadeOffset) * VRGlobals.randomMultiplier;


            //Vector3 newDirection = InitVRPatches.rightPointerFinger.transform.forward;
            float grenadeOffset = 3;

            if (VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_chattabka_vog17.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_chattabka_vog25.generated(Clone)")
                grenadeOffset = 13f;
            else if (VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_m67.generated(Clone)")
                grenadeOffset = -2;
            else if (VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_m7920.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_rgo.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_rgn.generated(Clone)" || VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_m18.generated(Clone)")
                grenadeOffset = 8;

            else if (VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_rdg2.generated(Clone)")
                grenadeOffset = -17;
            else if (VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_m18.generated(Clone)")
                grenadeOffset = 23;
            else if (VRGlobals.player.HandsController.HandsHierarchy.name == "weapon_grenade_zarya.generated(Clone)")
                grenadeOffset = 15;


            // When throwing without pulling the pin first it skews to the right, so offset it with this
            if (!pinPulled)
                grenadeOffset -= 25;



            Vector3 newDirection = VRGlobals.vrPlayer.RightHand.transform.right * -1;

            Quaternion rotation = Quaternion.AngleAxis(grenadeOffset, Vector3.up);
            Quaternion rotation2 = Quaternion.AngleAxis(-10, Vector3.right);
            Quaternion rotation3 = Quaternion.AngleAxis(0, Vector3.forward);
            newDirection = rotation * rotation2 * rotation3 * newDirection;
            //if (VRGlobals.grenadeOffset.x != 0)
            //    newDirection = InitVRPatches.rightPointerFinger.transform.up * VRGlobals.randomMultiplier;
            //if (VRGlobals.grenadeOffset.y != 0)
            //    newDirection = InitVRPatches.rightPointerFinger.transform.right * VRGlobals.randomMultiplier;
            //if (VRGlobals.grenadeOffset.z != 0)
            //    newDirection = newDirection * -1;

            Vector3 force = newDirection * (forcePower * lowHighThrow) + __instance._player.Velocity;
            //Vector3 vector = __instance.transform_1.position + __instance.transform_1.rotation * __instance.grenadePrefab_0.GrenadeItself.Offset;
            //Vector3 vector = VRGlobals.vrPlayer.RightHand.transform.position + VRGlobals.handsInteractionController.grenadeLaser.transform.rotation * __instance.grenadePrefab_0.GrenadeItself.Offset;
            Vector3 vector = VRGlobals.vrPlayer.RightHand.transform.position;
            //Quaternion rotation = Quaternion.Euler(VRGlobals.handsInteractionController.grenadeLaser.transform.eulerAngles + new Vector3(0,170,0));
            //Quaternion rotation = Quaternion.Euler(VRGlobals.handsInteractionController.grenadeLaser.transform.eulerAngles );
            Vector3 newforce = (VRGlobals.vrPlayer.RightHand.transform.rotation * force) * VRGlobals.randomMultiplier;

            //if (BaseGrenadeController.CheckHandsToBodyObstacles(__instance._player, vector, out var _, out var correctedPoint))
            //{
            //    vector = correctedPoint;
            //}
            __instance.vmethod_2(timeSinceSafetyLevelRemoved, vector, VRGlobals.vrPlayer.RightHand.transform.rotation, force, lowThrow);
            VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
            InitVRPatches.rightPointerFinger.enabled = false;
            pinPulled = false;
            VRGlobals.emptyHands = null;
            return false;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseGrenadeController), "IEventsConsumerOnWeapOut")]
        private static bool DisableGrenadeStuffAfterCancel(BaseGrenadeController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return true;

            VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
            InitVRPatches.rightPointerFinger.enabled = false;
            VRGlobals.emptyHands = null;
            pinPulled = false;
            return true;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(CollimatorSight), "OnEnable")]
        private static void FixCollimatorParallaxEffect(CollimatorSight __instance)
        {
            // Mark scale pulls the red dot/holo closer to the lens the higher it is, which makes it shift around when looking
            // as this value decreases it gets pushed further away from the lens, gets smaller, but it stops shifting around
            float markScale = __instance.CollimatorMaterial.GetFloat("_MarkScale");
            if (markScale != 1) {
                __instance.CollimatorMaterial.SetFloat("_MarkScale", 1f);
                // Mark shift increases the size of the dot/holo the smaller the value, the more negative it gets the smaller it gets
                float newMarkShift = 150 + ((1 - markScale) * 125);
                __instance.CollimatorMaterial.SetVector("_MarkShift",new Vector4(0, newMarkShift * -1, 0,0));
            }
        }


    }
}
public static class GameObjectExtensions
{
    public static Transform FindChildRecursive(this Transform parent, string childName)
    {
        foreach (Transform child in parent)
        {
            if (child.name.Equals(childName, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
            Transform found = child.FindChildRecursive(childName);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}