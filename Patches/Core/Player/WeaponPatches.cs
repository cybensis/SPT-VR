﻿using HarmonyLib;
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
using static EFT.Player.GrenadeHandsController;
using static EFT.Player.QuickGrenadeThrowHandsController;
using static UnityEngine.ParticleSystem.PlaybackState;
using UnityEngine.UIElements;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Settings;
using UnityEngine.Rendering.PostProcessing;
using static TarkovVR.Source.Controls.InputHandlers;
using UnityEngine.Rendering;
using TarkovVR;
using System.Collections;
using System.Security.Cryptography.X509Certificates;
using TarkovVR.ModSupport.FIKA;
using Fika.Core;
using Fika.Core.Coop.Utils;
using static FirearmsAnimator;
using Comfort.Common;
using Valve.VR;
using Fika.Core.Modding.Events;

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
        public static Transform currentScope;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.EmptyHandsController), "Spawn")]
        private static void ResetWeaponOnEquipHands(EFT.Player.EmptyHandsController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            VRGlobals.firearmController = null;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            if (VRSettings.GetLeftHandedMode())
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            VRGlobals.player = __instance._player;

            //if (!__instance.WeaponRoot.parent.GetComponent<GunInteractionController>())
            //    currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();

            if (VRGlobals.vrPlayer)
            {
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
                VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
                VRGlobals.ikManager.rightArmIk.solver.target = VRGlobals.vrPlayer.RightHand.transform;
                VRGlobals.ikManager.rightArmIk.enabled = true;
            }
            if (grenadeEquipped)
                grenadeEquipped = false;

            pinPulled = false;

            if (VRSettings.GetLeftHandedMode())
            {
                VRGlobals.player._elbowBends[0] = VRGlobals.rightArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.leftArmBendGoal;
            }
            else {
                VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;
            }
        }

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



        public static PortableRangeFinderController rangeFinder;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UsableItemController), "IEventsConsumerOnWeapIn")]
        private static void SetRangeFinder(UsableItemController __instance)
        {
            if (!__instance._player.IsYourPlayer || !(__instance as PortableRangeFinderController))
                return;
            PortableRangeFinderController instance = (PortableRangeFinderController) __instance;
            rangeFinder = instance;
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
                VRGlobals.weaponHolder.transform.GetChild(0).SetParent(VRGlobals.oldWeaponHolder.transform, false);
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
                __instance._player._compassArrow.transform.parent.SetParent(InitVRPatches.leftPalm, false);
                //__instance.WaitFrames(5, delegate {
                //    __instance._player._compassArrow.transform.parent.localPosition = new Vector3(0.1851f, -0.4271f, -0.0684f);
                //    __instance._player._compassArrow.transform.parent.localEulerAngles = new Vector3(55, 40, 11);
                //});
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GrenadeHandsController), "SetCompassState")]
        private static void SetCompassEquippedGrenade(GrenadeHandsController __instance, bool active)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (InitVRPatches.leftPalm && __instance._player._compassArrow)
            {
                compassEquipped = active;
                __instance._player._compassArrow.transform.parent.SetParent(InitVRPatches.leftPalm, false);
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

            //If menu is open turn gun renderer off
            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }

            // Check if a weapon is currently equipped, if that weapon isn the same as the one trying to be equipped, and that the weaponHolder actually has something there
            if (VRGlobals.oldWeaponHolder != null && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                VRGlobals.weaponHolder.transform.GetChild(0).SetParent(VRGlobals.oldWeaponHolder.transform, false);
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
            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }
            if (grenadeEquipped)
                grenadeEquipped = false;

            pinPulled = false;

            if (currentGunInteractController)
            {
                if (!currentGunInteractController.initialized)
                    currentGunInteractController.CreateRaycastReceiver(__instance.GunBaseTransform, __instance.WeaponLn);
                currentGunInteractController.enabled = true;
            }

            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);


            VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
            VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;

            VRGlobals.vrPlayer.isWeapPistol = (__instance.Weapon.WeapClass == "pistol");
        }


        public static Transform knifeTransform = null;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.BaseKnifeController), "IEventsConsumerOnWeapIn")]
        private static void MoveKnifeToIKHands(EFT.Player.BaseKnifeController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }
            if (grenadeEquipped)
                grenadeEquipped = false;

            pinPulled = false;

            if (currentGunInteractController != null) { 
                currentGunInteractController.enabled = false;
                currentGunInteractController = null;
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
            else {
                __instance.WeaponRoot.parent.parent.GetComponent<HandsPositioner>().enabled = true;
                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.parent.parent.gameObject;
                VRGlobals.weaponHolder = __instance.WeaponRoot.parent.gameObject;
                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

            }
            if (VRGlobals.player)
            {
                previousLeftHandMarker = VRGlobals.player._markers[0];
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


        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "method_49")]
        private static void AddNewMagToInteractionController(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }

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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticRetrice), "UpdateTransform")]
        public static bool UpdateTransform(EFT.CameraControl.OpticRetrice __instance, OpticSight opticSight)
        {
            try
            {

                ScopeReticle reticle = opticSight.ScopeData.Reticle;
                __instance._renderer.transform.localPosition = reticle.Position;
                __instance._renderer.transform.localEulerAngles = reticle.Rotation;
                __instance.float_1 = reticle.Scale * 0.1f;
                return false;
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[UpdateTransform] Failed: {ex.Message}\n{ex.StackTrace}");
                return true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "method_46")]
        public static void AddNewTacDeviceToInteractionController(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer || __instance.weaponManagerClass == null)
                return;

            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }

            var weaponManager = __instance.weaponManagerClass;

            // Handle tactical devices
            if (weaponManager.tacticalComboVisualController_0 != null)
                foreach (var tacDevice in weaponManager.tacticalComboVisualController_0)
                    if (!currentGunInteractController.TacDeviceAlreadyRegistered(tacDevice.transform))
                        currentGunInteractController.AddTacticalDevice(tacDevice.transform, __instance.FirearmsAnimator);

            // Handle scopes
            var sightControllers = weaponManager.sightModVisualControllers_0;
            if (sightControllers == null) return;

            // Find valid scope
            foreach (var sightController in sightControllers)
            {
                if (sightController.scopePrefabCache_0 == null) continue;

                // Find scope camera
                VRGlobals.scope = sightController.transform.Find("mod_aim_camera") ??
                                 sightController.transform.Find("mod_aim_camera_001");

                if (VRGlobals.scope == null) continue;

                // Get visual controller
                var visualController = sightController.GetComponent<SightModVisualControllers>() ??
                                     sightController.transform.parent.GetComponent<SightModVisualControllers>();

                if (visualController == null || !VRGlobals.vrOpticController) continue;

                // Setup collider
                if (visualController.TryGetComponent<BoxCollider>(out var collider))
                {
                    collider.gameObject.layer = 6;
                    collider.size = new Vector3(0.09f, 0.04f, 0.02f);
                    collider.center = new Vector3(-0.04f, 0, -0.075f);
                    collider.enabled = true;
                }

                return;
            }

            VRGlobals.scope = null;
        }

        //Damn nulls... This creates a dummy invisible Material when HighLightMesh is activated. This catches a null that happens with the weapon highlight when entering a raid
        //MoveWeaponToIKHands adds the HighLightMesh monobehavior component to the camera if it's not detected. This causes the script to run before a material is attached to it
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HighLightMesh), "Awake")]
        private static void HandleHighLightMeshAwake(HighLightMesh __instance)
        {

            if (Camera.main.stereoEnabled && __instance.Mat == null && VRGlobals.player && (VRGlobals.vrPlayer is HideoutVRPlayerManager || VRGlobals.vrPlayer is RaidVRPlayerManager))
            {
                var shader = Shader.Find("Hidden/HighLightMesh");
                __instance.Mat = new Material(shader);
                __instance.Mat.SetColor(HighLightMesh.int_2, Color.clear);
                __instance.Mat.SetTexture(HighLightMesh.int_4, __instance.renderTexture_0);
            }
        }

        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        [HarmonyPrefix]
        private static void CleanupHighlightMesh()
        {
            // Clean up any existing highlight mesh before switching weapons
            if (VRGlobals.VRCam != null)
            {
                HighLightMesh existingHighlight = VRGlobals.VRCam.gameObject.GetComponent<HighLightMesh>();
                if (existingHighlight != null)
                {
                    // Remove any command buffers
                    if (existingHighlight.commandBuffer_0 != null)
                    {
                        VRGlobals.VRCam.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, existingHighlight.commandBuffer_0);
                    }
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public static List<Transform> weaponInteractables;
        public static Transform gunCollider;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void MoveWeaponToIKHands(EFT.Player.FirearmController __instance)
        {

            if (!__instance._player.IsYourPlayer || __instance._player == null)
                return;

            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }

            if (grenadeEquipped)
                grenadeEquipped = false;

            pinPulled = false;

            if (currentGunInteractController != null)
                currentGunInteractController.enabled = false;

            VRGlobals.firearmController = __instance;
            VRGlobals.player = __instance._player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            if (VRSettings.GetLeftHandedMode())
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            VRGlobals.usingItem = false;
            VRGlobals.scope = null;
            for (int i = 0; i < __instance.weaponManagerClass.sightModVisualControllers_0.Length; i++)
            {
                VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.Find("mod_aim_camera");
                if (__instance.weaponManagerClass.sightModVisualControllers_0[i].scopePrefabCache_0 == null)
                    continue;
                // Some scopes have more than two modes or something which changes the name to 001, 002 etc,
                if (!VRGlobals.scope)
                    VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.Find("mod_aim_camera_001");

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
                if (__instance.WeaponRoot.parent.Find("RightHandPositioner"))
                {
                    currentGunInteractController = __instance.WeaponRoot.parent.GetComponent<GunInteractionController>();
                    currentGunInteractController.SetPlayerOwner(__instance._player.gameObject.GetComponent<GamePlayerOwner>());
                    VRGlobals.weaponHolder = __instance.WeaponRoot.parent.Find("RightHandPositioner").GetChild(0).gameObject;
                }
                else
                {
                    currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
                    currentGunInteractController.Init();

                    if (Camera.main.stereoEnabled)
                    {
                        HighLightMesh highLightMesh = Camera.main.gameObject.AddComponent<HighLightMesh>();
                        highLightMesh.enabled = false;
                        highLightMesh.Mat = new Material(Shader.Find("Hidden/HighLightMesh"));
                        highLightMesh.LineWidth = 2;
                        highLightMesh.Always = true;
                        highLightMesh.Color = Color.white;
                        currentGunInteractController.SetHighlightComponent(highLightMesh);
                    }
                    currentGunInteractController.SetPlayerOwner(__instance._player.gameObject.GetComponent<GamePlayerOwner>());
                    WeaponMeshParts weaponHighlightParts = WeaponMeshList.GetWeaponMeshList(__instance.WeaponRoot);
                    Transform weaponMeshRoot = __instance.GunBaseTransform.GetChild(0);
                    if (weaponHighlightParts != null)
                    {
                        foreach (var switchName in weaponHighlightParts.firingModeSwitch)
                        {
                            var found = weaponMeshRoot.FindChildRecursive(switchName);
                        }
                        foreach (string magazineMesh in weaponHighlightParts.magazine)
                        {
                            Transform magazineMeshTransform = weaponMeshRoot.FindChildRecursive(magazineMesh);
                            if (magazineMeshTransform)
                            {
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
                            if (__instance.weaponManagerClass.sightModVisualControllers_0[i].scopePrefabCache_0 == null)
                                continue;
                            VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.Find("mod_aim_camera");

                            // Some scopes have more than two modes or something which changes the name to 001, 002 etc,
                            if (!VRGlobals.scope)
                                VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[i].transform.Find("mod_aim_camera_001");

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
                    }
                    if (__instance.weaponManagerClass != null && __instance.weaponManagerClass.tacticalComboVisualController_0 != null)
                    {
                        for (int i = 0; i < __instance.weaponManagerClass.tacticalComboVisualController_0.Length; i++)
                        {
                            currentGunInteractController.AddTacticalDevice(__instance.weaponManagerClass.tacticalComboVisualController_0[i].transform, __instance.FirearmsAnimator);
                        }
                    }


                    GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                    rightHandPositioner.transform.SetParent(__instance.WeaponRoot.transform.parent, false);
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.SetParent(rightHandPositioner.transform, false);
                    HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                    handsPositioner.rightHandIk = rightHandPositioner.transform;
                }

                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

                weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset(__instance.weaponPrefab_0.name, __instance.Weapon.WeapClass);
                float weaponAngleOffset = VRSettings.GetRightHandVerticalOffset();
                if (weaponAngleOffset < 50)
                {
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
            else if (__instance.WeaponRoot.parent.Find("RightHandPositioner"))
            {
                VRGlobals.weaponHolder = __instance.WeaponRoot.parent.Find("RightHandPositioner").gameObject;
                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
            }
            else if (__instance.WeaponRoot.transform.parent && __instance.WeaponRoot.transform.parent.parent.name == "RightHandPositioner")
            {
                __instance.WeaponRoot.transform.parent = __instance.WeaponRoot.transform.parent.parent.parent;
                MoveWeaponToIKHands(__instance);
                return;
            }
            if (VRGlobals.player)
            {
                previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
            }
            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);
            // Don't use canted sights or rear sights
            if (__instance._player?.ProceduralWeaponAnimation?.CurrentScope?.Bone?.parent?.name?.Contains("rear") == true || __instance._player?.ProceduralWeaponAnimation?._targetScopeRotationDeg != 0)
            {
                int i = 0;
                int firstScope = __instance.Item?.AimIndex ?? -1;

                if (firstScope == -1)
                {
                    UnityEngine.Debug.LogError("MoveWeaponToIKHands: Invalid AimIndex. Aborting aiming mode change.");
                    return;
                }

                try
                {
                    __instance.ChangeAimingMode();
                    while (__instance.Item?.AimIndex != firstScope
                        && (__instance._player.ProceduralWeaponAnimation._targetScopeRotationDeg != 0
                        || (__instance._player.ProceduralWeaponAnimation.CurrentScope?.Bone?.parent?.name?.Contains("rear") == true)))
                    {
                        __instance.ChangeAimingMode();
                    }
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"MoveWeaponToIKHands: Error while changing aiming mode - {ex.Message}");
                }
            }
            VRGlobals.vrPlayer.isWeapPistol = (__instance.Weapon.WeapClass == "pistol");
        }

        //This might clean up scopes a bit but it was mainly done to fix an issue with how BSG handles variable scopes
        //Now we tell LateUpdate to disable effects (I think?) but mainly to just update scope and nothing more
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "LateUpdate")]
        private static bool FixAdjustableOptic(EFT.CameraControl.OpticComponentUpdater __instance)
        {
            if (__instance.MainCamera == null || __instance.transform_0 == null)
            {
                return false;
            }
            __instance.transform.position = __instance.transform_0.position;
            __instance.transform.rotation = __instance.transform_0.rotation;
            //__instance.camera_0.useOcclusionCulling = __instance.MainCamera.useOcclusionCulling;
            if (__instance.undithering_1 != null && __instance.undithering_0 != null)
            {
                __instance.undithering_1.enabled = __instance.undithering_0.enabled;
                __instance.undithering_1.shader = __instance.undithering_0.shader;
            }
            if (__instance.volumetricLightRenderer_1 != null && __instance.volumetricLightRenderer_0 != null)
            {
                __instance.volumetricLightRenderer_1.enabled = __instance.volumetricLightRenderer_0.enabled;
                __instance.volumetricLightRenderer_1.DefaultSpotCookie = __instance.volumetricLightRenderer_0.DefaultSpotCookie;
                __instance.volumetricLightRenderer_1.Resolution = __instance.volumetricLightRenderer_0.Resolution;
            }
            if (__instance.opticSight_0.CameraData.IsAdjustableOptic)
            {
                __instance.scopeZoomHandler_0.UpdateScope();
            }
            return false;
        }

        //New scope zoom handler, this patch waits for vrOpticController to be created before going forward with the CopyComponentFromOptic method
        static SightModVisualControllers visualController;
        static float zoomLevel;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void WaitForVROpticController(EFT.CameraControl.OpticComponentUpdater __instance, OpticSight opticSight)
        {
            if (!VRGlobals.vrOpticController)
                return;
        }

        //This actually handles the scope zoom
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void SetOpticCamFoV(EFT.CameraControl.OpticComponentUpdater __instance, OpticSight opticSight)
        {
            float fov;

            if (!visualController)
                visualController = opticSight.transform.parent.GetComponent<SightModVisualControllers>();

            if (rangeFinder)
            {
                fov = 3.2f;
                opticSight.transform.Find("linza").gameObject.SetActive(true);
            }

            if (visualController && VRGlobals.vrOpticController)
            {
                float zoomLevel = visualController.sightComponent_0.GetCurrentOpticZoom();
                VRGlobals.vrOpticController.scopeCamera = __instance.camera_0;
                VRGlobals.scopeSensitivity = visualController.sightComponent_0.GetCurrentSensitivity;
                string scopeName = opticSight.name;
                BoxCollider scopeCollider;

                currentScope = __instance.transform_0;
                if (VRSettings.GetLeftHandedMode())
                    currentScope.parent.localScale = new Vector3(-1, 1, 1);

                // For scopes that have multiple levels of zoom of different zoom effects (e.g. changing sight lines from black to red), opticSight will be stored in 
                // mode_000, mode_001, etc, and that will be stored in the scope game object, so we need to get parent name for scopes with multiple settings

                if (scopeName.Contains("mode"))
                {
                    if (__instance.transform_0)
                        VRGlobals.vrPlayer.scopeUiPosition = __instance.transform_0.parent.Find("backLens");
                    scopeName = opticSight.transform.parent.name;
                    opticSight.transform.parent.gameObject.layer = 6;
                    scopeCollider = opticSight.transform.parent.GetComponent<BoxCollider>();
                }
                else
                {
                    if (__instance.transform_0)
                        VRGlobals.vrPlayer.scopeUiPosition = __instance.transform_0.Find("backLens");
                    opticSight.gameObject.layer = 6;
                    scopeCollider = opticSight.GetComponent<BoxCollider>();
                    if (!scopeCollider)
                        scopeCollider = opticSight.transform.parent.GetComponent<BoxCollider>();
                }
                if (scopeCollider)
                {
                    scopeCollider.size = new Vector3(0.09f, 0.04f, 0.02f);
                    scopeCollider.center = new Vector3(-0.04f, 0, -0.075f);
                    scopeCollider.isTrigger = true;
                    scopeCollider.enabled = true;
                }
                VRGlobals.vrOpticController.minFov = ScopeManager.GetMinFOV(scopeName);
                VRGlobals.vrOpticController.maxFov = ScopeManager.GetMaxFOV(scopeName);
                VRGlobals.vrOpticController.currentFov = VRGlobals.vrOpticController.scopeCamera.fieldOfView;


                if (scopeName.Contains("mode"))
                {
                    var parent = opticSight.transform.parent;
                    var collider = parent.GetComponent<BoxCollider>() ?? parent.parent.GetComponent<BoxCollider>();
                    if (collider) collider.enabled = true;
                }
                else
                {
                    var collider = opticSight.GetComponent<BoxCollider>() ?? opticSight.transform.parent.GetComponent<BoxCollider>();
                    if (collider) collider.enabled = true;
                }


            }
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
        [HarmonyPatch(typeof(GetActionsClass.Class1624), "method_0")]
        private static bool PreventUsingStationaryWeapon(GetActionsClass.Class1624 __instance)
        {
            return false;
        }

        //BSG changed something with how physics was being killed which caused physically holding items to break
        //This disables the coroutine that checks to disable physics until you drop the item you're holding
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LootItem), "method_3")]
        private static bool DisableCoroutineWhenHeldItem(LootItem __instance)
        {
            __instance.bool_1 = false;
            if (__instance._rigidBody != null)
            {
                Rigidbody rigidBody = __instance._rigidBody;
                GClass812 visibilityChecker = __instance.GetVisibilityChecker();
                EFTPhysicsClass.GClass723.SupportRigidbody(rigidBody, __instance.PhysicsQuality, visibilityChecker);
                __instance.ienumerator_0 = __instance.method_4();
                if(VRGlobals.handsInteractionController.heldItem == null)
                    __instance.StartCoroutine(__instance.ienumerator_0);
            }
            return false;
        }

        private static Vector3 lastHandPosition = Vector3.zero;
        private static Queue<Vector3> velocityHistory = new Queue<Vector3>();
        private static int maxVelocitySamples = 10;

        //These two method handle getting velocity of your controller straight from SteamVR to handle throwing items/grenades
        private static Vector3 GetSteamVRVelocity(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            if (poseAction != null)
            {
                Vector3 velocity = poseAction.GetVelocity(inputSource);
                return velocity;
            }
            return Vector3.zero;
        }

        private static Vector3 GetSteamVRPosition(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            return poseAction != null ? poseAction.GetLocalPosition(inputSource) : Vector3.zero;
        }

        private static Quaternion GetSteamVRRotation(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            return poseAction != null ? poseAction.GetLocalRotation(inputSource) : Quaternion.identity;
        }

        private static Vector3 GetSteamVRAngularVelocity(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            if (poseAction != null)
            {
                Vector3 angularVelocity = poseAction.GetAngularVelocity(inputSource);
                return angularVelocity;
            }
            return Vector3.zero;
        }
        public static void DropObject(LootItem val, bool useThrowVelocity = false)
        {
            AssetPoolObject component = val.GetComponent<AssetPoolObject>();
            GameObject gameObject = val.gameObject;

            float makeVisibleAfterDelay = 0.05f;
            val._rigidBody = gameObject.GetComponent<Rigidbody>() ?? gameObject.AddComponent<Rigidbody>();

            if (gameObject.activeInHierarchy)
                val.method_3();
            else
                val.bool_2 = true;

            if (component != null)
                component.RegisteredComponentsToClean.Add(val._rigidBody);

            val._rigidBody.mass = val.item_0.TotalWeight;
            val._rigidBody.isKinematic = false;
            val._rigidBody.useGravity = true;
            val._rigidBody.detectCollisions = true;

            if (useThrowVelocity)
            {
                SteamVR_Input_Sources throwingHand = SteamVR_Input_Sources.LeftHand;
                Vector3 throwVelocity = GetSteamVRVelocity(throwingHand);
                Vector3 angularVelocity = GetSteamVRAngularVelocity(throwingHand);

                if (throwVelocity.magnitude > 0.1f)
                {
                    // Transform from controller local space to world space
                    Vector3 worldSpaceVelocity = VRGlobals.vrOffsetter.transform.TransformDirection(throwVelocity);

                    // Apply velocity multiplier if needed
                    float velocityMultiplier = 1.0f;
                    worldSpaceVelocity *= velocityMultiplier;

                    // Cap max speed
                    if (worldSpaceVelocity.magnitude > 10f)
                        worldSpaceVelocity = worldSpaceVelocity.normalized * 10f;

                    val._rigidBody.velocity = worldSpaceVelocity;
                    val._rigidBody.angularVelocity = angularVelocity; // Angular velocity might also need transformation if it's not working right
                }
            }

            val._currentPhysicsTime = 0f;

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
            val._rigidBody.collisionDetectionMode =
                size.x * size.y * size.z <= EFTHardSettings.Instance.LootVolumeForHighQuallityPhysicsClient
                ? CollisionDetectionMode.Continuous
                : CollisionDetectionMode.Discrete;

            val.OnRigidbodyStarted();

            if (makeVisibleAfterDelay > 0f)
            {
                val.method_10(isVisible: false);
                val.StartCoroutine(val.method_11(makeVisibleAfterDelay));
            }
        }
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        [HarmonyPrefix]
        private static void CleanupHighlightMeshMeds()
        {
            // Clean up any existing highlight mesh before switching weapons
            if (VRGlobals.VRCam != null)
            {
                HighLightMesh existingHighlight = VRGlobals.VRCam.gameObject.GetComponent<HighLightMesh>();
                if (existingHighlight != null)
                {
                    // Remove any command buffers
                    if (existingHighlight.commandBuffer_0 != null)
                    {
                        VRGlobals.VRCam.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, existingHighlight.commandBuffer_0);
                    }
                }
            }
        }

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
                    if (WeaponPatches.currentGunInteractController != null)
                    {
                        Transform rightHand = WeaponPatches.currentGunInteractController?.transform.Find("RightHandPositioner");
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
                    previousLeftHandMarker = VRGlobals.player._markers[0];
                }
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"Error in MedsControllerSpawnPatch: {ex.Message}");
            }
            //Plugin.MyLog.LogError($"[SpawnMedsController] Spawning meds controller for {player} with item {item}");
        }

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

            if (currentGunInteractController != null)
                currentGunInteractController.enabled = false;

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
                    currentGunInteractController = __instance.WeaponRoot.parent.GetComponent<GunInteractionController>();
                    currentGunInteractController.enabled = true;
                    currentGunInteractController.SetPlayerOwner(player.gameObject.GetComponent<GamePlayerOwner>());
                    VRGlobals.weaponHolder = __instance.WeaponRoot.parent.Find("RightHandPositioner").Find("weaponHolder").gameObject;

                }
                else
                {
                    currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
                    currentGunInteractController.Init();
                    currentGunInteractController.initialized = true;
                    currentGunInteractController.SetPlayerOwner(player.gameObject.GetComponent<GamePlayerOwner>());

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
                weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset("", "grenade");
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
                previousLeftHandMarker = VRGlobals.player._markers[0];
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
            VRGlobals.weaponHolder.transform.localPosition = weaponOffset;
        }

        // 1. Create a list of GClass2804 with names and actions
        // 2. Create a GClass2805 and assign the list to Actions
        // 3. Run HideoutPlayerOwner.AvailableInteractionState.set_Value(Gclass2805)
        //public static float grenadeOffset = 0;

        public static bool pinPulled = false;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseGrenadeHandsController), "method_9")]
        private static bool RepositionGrenadeThrow(BaseGrenadeHandsController __instance, ref Vector3? throwPosition, float timeSinceSafetyLevelRemoved, float lowHighThrow, Vector3 direction, float forcePower, bool lowThrow, bool withVelocity)
        {
            if (!__instance._player.IsYourPlayer)
                return true;
            if (!pinPulled)
                return false;

            Vector3 throwVelocity = GetSteamVRVelocity(SteamVR_Input_Sources.RightHand);
            Vector3 force;
            Vector3 throwPos = GetSteamVRPosition(SteamVR_Input_Sources.RightHand);
            Quaternion throwRot = GetSteamVRRotation(SteamVR_Input_Sources.RightHand);

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