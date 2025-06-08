using EFT;
using HarmonyLib;
using System;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Patches.Core.Equippables
{
    [HarmonyPatch]
    internal class FirearmPatches
    {

        //---------------------------------------------------------- UN/EQUIP FIREARM PATCHES ----------------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void MoveWeaponToIKHands(EFT.Player.FirearmController __instance)
        {

            if (!__instance._player.IsYourPlayer || __instance._player == null)
                return;

            EquippablesShared.DisableEquippedRender();

            if (GrenadePatches.grenadeEquipped)
                GrenadePatches.grenadeEquipped = false;

            GrenadePatches.pinPulled = false;

            if (EquippablesShared.currentGunInteractController != null)
                EquippablesShared.currentGunInteractController.enabled = false;

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
                    EquippablesShared.currentGunInteractController = __instance.WeaponRoot.parent.GetComponent<GunInteractionController>();
                    EquippablesShared.currentGunInteractController.SetPlayerOwner(__instance._player.gameObject.GetComponent<GamePlayerOwner>());
                    VRGlobals.weaponHolder = __instance.WeaponRoot.parent.Find("RightHandPositioner").GetChild(0).gameObject;
                }
                else
                {
                    EquippablesShared.currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
                    EquippablesShared.currentGunInteractController.Init();

                    if (Camera.main.stereoEnabled)
                    {
                        HighLightMesh highLightMesh = Camera.main.gameObject.AddComponent<HighLightMesh>();
                        highLightMesh.enabled = false;
                        highLightMesh.Mat = new Material(Shader.Find("Hidden/HighLightMesh"));
                        highLightMesh.LineWidth = 2;
                        highLightMesh.Always = true;
                        highLightMesh.Color = Color.white;
                        EquippablesShared.currentGunInteractController.SetHighlightComponent(highLightMesh);
                    }
                    EquippablesShared.currentGunInteractController.SetPlayerOwner(__instance._player.gameObject.GetComponent<GamePlayerOwner>());
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
                                    EquippablesShared.currentGunInteractController.SetMagazine(magazineMeshTransform.GetChild(0), false);
                                else
                                    EquippablesShared.currentGunInteractController.SetMagazine(weaponMeshRoot.FindChildRecursive(magazineMesh), false);
                            }
                        }
                        foreach (string chamberMesh in weaponHighlightParts.chamber)
                        {
                            if (weaponMeshRoot.FindChildRecursive(chamberMesh))
                                EquippablesShared.currentGunInteractController.SetChargingHandleOrBolt(weaponMeshRoot.FindChildRecursive(chamberMesh), false);
                        }

                        foreach (string firingModeSwitch in weaponHighlightParts.firingModeSwitch)
                        {
                            if (weaponMeshRoot.FindChildRecursive(firingModeSwitch))
                                EquippablesShared.currentGunInteractController.SetFireModeSwitch(weaponMeshRoot.FindChildRecursive(firingModeSwitch));
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
                            EquippablesShared.currentGunInteractController.AddTacticalDevice(__instance.weaponManagerClass.tacticalComboVisualController_0[i].transform, __instance.FirearmsAnimator);
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

                EquippablesShared.weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset(__instance.weaponPrefab_0.name, __instance.Weapon.WeapClass);
                float weaponAngleOffset = VRSettings.GetRightHandVerticalOffset();
                if (weaponAngleOffset < 50)
                {
                    // if the angle is less than 50, get how much less than 50 it is, divide by 100 to get a percent, then multiply our offset by it
                    float rotOffsetMultiplier = (50 - weaponAngleOffset) / 100;
                    EquippablesShared.weaponOffset += new Vector3(0.08f, 0, -0.02f) * rotOffsetMultiplier;
                }
                else if (weaponAngleOffset > 50)
                {
                    // if the angle is less than 50, get how much less than 50 it is, divide by 100 to get a percent, then multiply our offset by it
                    float rotOffsetMultiplier = (weaponAngleOffset - 50) / 100;
                    EquippablesShared.weaponOffset += new Vector3(-0.01f, -0.01f, +0.04f) * rotOffsetMultiplier;
                }
                VRGlobals.weaponHolder.transform.localPosition = EquippablesShared.weaponOffset;
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
                EquippablesShared.previousLeftHandMarker = VRGlobals.player._markers[0];
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


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "IEventsConsumerOnWeapIn")]
        private static void FinishMovingWeaponToVrHands(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            EquippablesShared.DisableEquippedRender();
            if (GrenadePatches.grenadeEquipped)
                GrenadePatches.grenadeEquipped = false;

            GrenadePatches.pinPulled = false;

            if (EquippablesShared.currentGunInteractController)
            {
                if (!EquippablesShared.currentGunInteractController.initialized)
                    EquippablesShared.currentGunInteractController.CreateRaycastReceiver(__instance.GunBaseTransform, __instance.WeaponLn);
                EquippablesShared.currentGunInteractController.enabled = true;
            }

            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);


            VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
            VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;

            VRGlobals.vrPlayer.isWeapPistol = (__instance.Weapon.WeapClass == "pistol");
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "IEventsConsumerOnWeapOut")]
        private static void ReturnWeaponToOriginalParentOnChange(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            EquippablesShared.DisableEquippedRender();

            if (VRGlobals.oldWeaponHolder != null && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                VRGlobals.weaponHolder.transform.GetChild(0).SetParent(VRGlobals.oldWeaponHolder.transform, false);
                VRGlobals.oldWeaponHolder.transform.localRotation = Quaternion.identity;
                VRGlobals.oldWeaponHolder = null;
                EquippablesShared.currentGunInteractController.enabled = false;
                VRGlobals.emptyHands = null;
            }
            VRGlobals.vrPlayer.isWeapPistol = false;

        }


        //---------------------------------------------------------- WEAPON MESH HIGHLIGHTING PATCHES ------------------------------------------------------------------
        //-----------------------------------------------------------------------------------------------------------------
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


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        [HarmonyPrefix]
        private static void CleanupHighlightMesh()
        {
            // Clean up any existing highlight mesh before switching weapons
            if (Camera.main != null)
            {
                HighLightMesh existingHighlight = Camera.main.gameObject.GetComponent<HighLightMesh>();
                if (existingHighlight != null)
                {
                    // Remove any command buffers
                    if (existingHighlight.commandBuffer_0 != null)
                    {
                        Camera.main.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, existingHighlight.commandBuffer_0);
                    }
                }
            }
        }
    }
}
