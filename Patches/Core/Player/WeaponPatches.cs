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

            //StackTrace stackTrace = new StackTrace();
            //bool isBotPlayer = false;

            //foreach (var frame in stackTrace.GetFrames())
            //{
            //    var method = frame.GetMethod();
            //    var declaringType = method.DeclaringType.FullName;
            //    var methodName = method.Name;

            //    // Check for bot-specific methods
            //    if (declaringType.Contains("EFT.BotSpawner") || declaringType.Contains("GClass732") && methodName.Contains("ActivateBot"))
            //    {
            //        isBotPlayer = true;
            //        break;
            //    }
            //}
            //if (isBotPlayer)
            //{
            //    // This is a bot player, so do not execute the rest of the code
            //    Plugin.MyLog.LogWarning("Wasn't AI but is bot, is my player: " + __instance._player.IsYourPlayer);
            //    return;
            //}
            VRGlobals.firearmController = null;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            VRGlobals.player = __instance._player;

            if (!__instance.WeaponRoot.parent.GetComponent<GunInteractionController>()) {
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
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.KnifeController), "Spawn")]
        private static void SetMeleeWeapon(EFT.Player.KnifeController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            //StackTrace stackTrace = new StackTrace();
            //bool isBotPlayer = false;

            //foreach (var frame in stackTrace.GetFrames())
            //{
            //    var method = frame.GetMethod();
            //    var declaringType = method.DeclaringType.FullName;
            //    var methodName = method.Name;

            //    // Check for bot-specific methods
            //    if (declaringType.Contains("EFT.BotSpawner") || declaringType.Contains("GClass732") && methodName.Contains("ActivateBot"))
            //    {
            //        isBotPlayer = true;
            //        break;
            //    }
            //}
            //if (isBotPlayer)
            //{
            //    // This is a bot player, so do not execute the rest of the code
            //    Plugin.MyLog.LogWarning("Wasn't AI but is bot, is my player: " + __instance._player.IsYourPlayer);
            //    return;
            //}
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
            Plugin.MyLog.LogInfo("KnifeController.Spawn: " + __instance);
            if (grenadeEquipped)
                grenadeEquipped = false;

            VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
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
            }
            VRGlobals.vrPlayer.isWeapPistol = false;
            //if (VRGlobals.oldWeaponHolder && VRGlobals.weaponHolder == __instance.WeaponRoot.parent.gameObject && VRGlobals.weaponHolder.transform.childCount > 0)
            //{
            //    VRGlobals.ikManager.rightArmIk.solver.target = VRGlobals.vrPlayer.RightHand.transform;
            //    Transform weaponRoot = VRGlobals.weaponHolder.transform.GetChild(0);
            //    weaponRoot.parent = VRGlobals.oldWeaponHolder.transform;
            //    weaponRoot.localPosition = Vector3.zero;
            //    VRGlobals.oldWeaponHolder = null;
            //}
        }


        // I found in the hideout when I equipped my primary, then second primary, then primary again and second primary again, the
        // gun would start rotating all over the place for no reaon, so disable it here.
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(ProceduralWeaponAnimation), "ApplyComplexRotation")]
        //private static bool BlockGunRotatingWeird(ProceduralWeaponAnimation __instance, float dt)
        //{
        //    return false;
        //}


        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "IEventsConsumerOnWeapIn")]
        private static void FinishMovingWeaponToVrHands(EFT.Player.FirearmController __instance)
        {


            if (!__instance._player.IsYourPlayer)
                return;
            Plugin.MyLog.LogInfo("FirearmController.IEventsConsumerOnWeapIn: " + __instance);

            if (grenadeEquipped)
                grenadeEquipped = false;
            if (currentGunInteractController) { 
                if (!currentGunInteractController.initialized)
                    currentGunInteractController.CreateRaycastReceiver(__instance.GunBaseTransform, __instance.WeaponLn);
                currentGunInteractController.enabled = true;
            }
            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);

            VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
            VRGlobals.vrPlayer.isWeapPistol = (__instance.Weapon.WeapClass == "pistol");
        }


        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(EFT.Player.FirearmController), "SetAim")]
        //private static void FinishMovingWeapwonToVrHands(EFT.Player.FirearmController __instance, bool value)
        //{
        //    if (__instance._player.ProceduralWeaponAnimation._targetScopeRotationDeg != 0) {
        //        int i = 0;
        //        int firstScope = __instance.Item.AimIndex.Value;
        //        __instance.ChangeAimingMode();
        //        while (__instance.Item.AimIndex.Value != firstScope && __instance._player.ProceduralWeaponAnimation._targetScopeRotationDeg != 0) {
        //            __instance.ChangeAimingMode();
        //        }
        //    }
        //}


        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(MagazineInHandsVisualController), "ReturnToPool")]
        //private static void AddNewMagToInteractionController(MagazineInHandsVisualController __instance)
        //{
        //    if (currentGunInteractController && __instance.transform == currentGunInteractController.magazine) {
        //        currentGunInteractController = null;
        //    }
        //    //Plugin.MyLog.LogWarning(__instance.transform.root+"\n\n\n");
        //    //Plugin.MyLog.LogWarning(new StackTrace());

        //}

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
                        if (__instance.weaponPrefab_0.Renderers[i].transform.parent.GetComponent<MagazineInHandsVisualController>()) { 
                            currentGunInteractController.SetMagazine(__instance.weaponPrefab_0.Renderers[i].transform, false);
                            return;
                        }
                    }

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

            // AmmoCountPanel - on ShowFireMode position on selector position 
            // rotation = ammopanel rotation, localrotation = 0 90 90
            // psosition = ammopanel pos, localpos = -0.0175 0.03 0
            // On BattleUIComponentAnimation.Hide() with name == AmmoPanel stop updating position

            // For Ammo do the exact same vu

            if (grenadeEquipped)
                grenadeEquipped = false;

            if (!__instance._player.IsYourPlayer)
                return;

            if (currentGunInteractController != null)
                currentGunInteractController.enabled = false;

            Plugin.MyLog.LogWarning("Init calc: " + __instance);
            VRGlobals.firearmController = __instance;
            VRGlobals.player = __instance._player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            VRGlobals.usingItem = false;
            if (__instance.weaponManagerClass.sightModVisualControllers_0.Length > 0) { 
                VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[0].transform.FindChild("mod_aim_camera");
                // Some scopes have more than two modes or something which changes the name to 001, 002 etc,
                if (!VRGlobals.scope)
                    VRGlobals.scope = __instance.weaponManagerClass.sightModVisualControllers_0[0].transform.FindChild("mod_aim_camera_001");

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
                    else { 
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
                    if (weaponHighlightParts != null) {
                        foreach (string magazineMesh in weaponHighlightParts.magazine) {
                            if (weaponMeshRoot.FindChildRecursive(magazineMesh))
                                currentGunInteractController.SetMagazine(weaponMeshRoot.FindChildRecursive(magazineMesh), false);
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
                        //foreach (string firingModeSwitch in weaponHighlightParts.stock)
                        //{
                        //    if (weaponMeshRoot.FindChildRecursive(firingModeSwitch))
                        //        currentGunInteractController.SetFireModeSwitch(weaponMeshRoot.FindChildRecursive(firingModeSwitch));
                        //}
                    }
                    if (__instance.weaponPrefab_0)
                    {
                        if (__instance.weaponPrefab_0.gunShadowDisabler_0 != null)
                        {
                            for (int i = 0; i < __instance.weaponPrefab_0.gunShadowDisabler_0.Length; i++)
                            {
                                currentGunInteractController.AddTacticalDevice(__instance.weaponPrefab_0.gunShadowDisabler_0[i].transform, __instance.FirearmsAnimator);
                            }
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
            if (__instance._player.ProceduralWeaponAnimation._targetScopeRotationDeg != 0)
            {
                int i = 0;
                int firstScope = __instance.Item.AimIndex.Value;
                __instance.ChangeAimingMode();
                while (__instance.Item.AimIndex.Value != firstScope && __instance._player.ProceduralWeaponAnimation._targetScopeRotationDeg != 0)
                {
                    __instance.ChangeAimingMode();
                }
            }
            VRGlobals.oldWeaponHolder.transform.localEulerAngles = new Vector3(340, 340, 0);
        }

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
            if (visualController && VRGlobals.vrOpticController)
            {
                VRGlobals.scopeSensitivity = visualController.sightComponent_0.GetCurrentSensitivity;
                if (VRGlobals.vrOpticController.scopeCamera == __instance.camera_0)
                {
                    fov = VRGlobals.vrOpticController.currentFov;
                }
                else
                {


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
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraClass), "method_12")]
        private static bool FixCollimatorSights(CameraClass __instance)
        {
            return false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Not sure if needed
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(CameraClass), "method_10")]
        //private static bool FixSomeAimShit(CameraClass __instance)
        //{
        //    __instance.ReflexController.RefreshReflexCmdBuffers();
        //    Renderer renderer = __instance.OpticCameraManager.CurrentOpticSight?.LensRenderer;
        //    __instance.method_11();
        //    if (__instance.OpticCameraManager.CurrentOpticSight != null)
        //    {
        //        //LODGroup[] componentsInChildren = __instance.OpticCameraManager.CurrentOpticSight.gameObject.GetComponentInParent<WeaponPrefab>().gameObject.GetComponentsInChildren<LODGroup>();

        //        ////////////// Since the weapons are moved around to the right controller object, this needs to be redone here
        //        LODGroup[] componentsInChildren = VRGlobals.oldWeaponHolder.GetComponentsInChildren<LODGroup>();
        //        if (__instance.renderer_0 != null)
        //        {
        //            Array.Clear(__instance.renderer_0, 0, __instance.renderer_0.Length);
        //        }
        //        int instanceID = renderer.GetInstanceID();
        //        int num = 0;
        //        int num2 = 0;
        //        while (componentsInChildren != null && num2 < componentsInChildren.Length)
        //        {
        //            if (!(componentsInChildren[num2] == null))
        //            {
        //                LOD[] lODs = componentsInChildren[num2].GetLODs();
        //                if (lODs.Length != 0)
        //                {
        //                    Renderer[] renderers = lODs[0].renderers;
        //                    foreach (Renderer renderer2 in renderers)
        //                    {
        //                        if (!(renderer2 == null) && renderer2.GetInstanceID() != instanceID && !__instance.method_9(renderer2.GetInstanceID()))
        //                        {
        //                            num++;
        //                        }
        //                    }
        //                }
        //            }
        //            num2++;
        //        }
        //        if (num > 0 && (__instance.renderer_0 == null || __instance.renderer_0.Length < num))
        //        {
        //            __instance.renderer_0 = new Renderer[num];
        //        }
        //        num = 0;
        //        int num3 = 0;
        //        while (componentsInChildren != null && num3 < componentsInChildren.Length)
        //        {
        //            if (!(componentsInChildren[num3] == null))
        //            {
        //                LOD[] lODs2 = componentsInChildren[num3].GetLODs();
        //                if (lODs2.Length != 0)
        //                {
        //                    Renderer[] renderers = lODs2[0].renderers;
        //                    foreach (Renderer renderer3 in renderers)
        //                    {
        //                        if (!(renderer3 == null) && renderer3.GetInstanceID() != instanceID && !__instance.method_9(renderer3.GetInstanceID()))
        //                        {
        //                            __instance.renderer_0[num++] = renderer3;
        //                        }
        //                    }
        //                }
        //            }
        //            num3++;
        //        }
        //    }
        //    __instance.SSAA.SetLensRenderer(__instance.OpticCameraManager.CurrentOpticSight?.LensRenderer, __instance.renderer_1, __instance.renderer_0);
        //    __instance.SSAA.UnityTAAJitterSamplesRepeatCount = 2;
        //    return false;
        //}
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // NOTE: Currently arm stamina lasts way too long, turn it down maybe, or maybe not since the account I'm using has maxed stats
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(GClass601), "Process")]
        //private static bool OnlyConsumeArmStamOnHoldBreath(GClass601 __instance, float dt)
        //{
        //    Plugin.MyLog.LogWarning(dt);
        //    return true;
        //    //if (isHoldingBreath) return true;
        //    //return false;
        //}

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(AbstractHandsController), "IEventsConsumerOnWeapIn")]
        //private static void ResetWeaponOnEquwipHands(AbstractHandsController __instance)
        //{
        //    //StackTrace stackTrace = new StackTrace();
        //    Plugin.MyLog.LogWarning("On weap in " + __instance + "    |    " + __instance.transform.root);
        //}

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(BaseGrenadeController), "IEventsConsumerOnWeapIn")]
        //private static void ResewtWeaponOnEquwipHands(BaseGrenadeController __instance)
        //{
        //    //StackTrace stackTrace = new StackTrace();
        //    Plugin.MyLog.LogWarning("IEventsConsumerOnWeapIn " + __instance + "    |    " + __instance.transform.root);
        //}

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(BaseGrenadeController), "IEventsConsumerOnWeapOut")]
        //private static void ResetWeaponOnEquwipHands(BaseGrenadeController __instance)
        //{
        //    //StackTrace stackTrace = new StackTrace();
        //    Plugin.MyLog.LogWarning("IEventsConsumerOnWeapOut " + __instance + "    |    " + __instance.transform.root);
        //}


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
                //StackTrace stackTrace = new StackTrace();
                //Plugin.MyLog.LogError(stackTrace);
                VRGlobals.emptyHands = __result._controllerObject.transform;
                //VRGlobals.ikManager.rightArmIk.solver.target = null;
                //VRGlobals.ikManager.leftArmIk.solver.target = null;
                VRGlobals.usingItem = true;
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

                grenadeEquipped = true;

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

                    VRGlobals.weaponHolder.transform.localPosition = weaponOffset;
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

                //VRGlobals.ikManager.rightArmIk.solver.target = null;
                //VRGlobals.ikManager.leftArmIk.solver.target = null;
            }
        }

        // 1. Create a list of GClass2804 with names and actions
        // 2. Create a GClass2805 and assign the list to Actions
        // 3. Run HideoutPlayerOwner.AvailableInteractionState.set_Value(Gclass2805)

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
