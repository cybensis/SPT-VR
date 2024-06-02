using HarmonyLib;
using System;
using System.Diagnostics;
using static EFT.Player;
using UnityEngine;
using EFT.CameraControl;
using TarkovVR.Source.Player.VR;
using TarkovVR.Source.Weapons;
using TarkovVR.Source.Player.VRManager;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class WeaponPatches
    {
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.EmptyHandsController), "Spawn")]
        private static void ResetWeaponOnEquipHands(EFT.Player.EmptyHandsController __instance)
        {
            if (__instance._player.IsAI)
                return;
            StackTrace stackTrace = new StackTrace();
            bool isBotPlayer = false;

            foreach (var frame in stackTrace.GetFrames())
            {
                var method = frame.GetMethod();
                var declaringType = method.DeclaringType.FullName;
                var methodName = method.Name;

                // Check for bot-specific methods
                if (declaringType.Contains("EFT.BotSpawner") || declaringType.Contains("GClass732") && methodName.Contains("ActivateBot"))
                {
                    isBotPlayer = true;
                    break;
                }
            }
            if (isBotPlayer)
            {
                // This is a bot player, so do not execute the rest of the code
                return;
            }
            VRGlobals.firearmController = null;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            VRGlobals.player = __instance._player;

            if (VRGlobals.oldWeaponHolder && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                Plugin.MyLog.LogWarning("\n\nAAAAAAAAAAA : " + __instance._player.gameObject + " \n");
                VRGlobals.ikManager.rightArmIk.solver.target = VRPlayerManager.RightHand.transform;
                Transform weaponRoot = VRGlobals.ikManager.rightArmIk.transform.GetChild(0);
                weaponRoot.parent = VRGlobals.ikManager.rightArmIk.transform;
                weaponRoot.localPosition = Vector3.zero;
                VRGlobals.ikManager.rightArmIk = null;
            }


        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "IEventsConsumerOnWeapOut")]
        private static void ReturnWeaponToOriginalParentOnChange(EFT.Player.FirearmController __instance)
        {
            if (__instance._player.IsAI)
                return;

            Plugin.MyLog.LogWarning("IEventsConsumerOnWeapOut " + __instance + "   |    " + __instance.WeaponRoot + "   |    " + __instance.WeaponRoot.parent);

            // Check if a weapon is currently equipped, if that weapon isn the same as the one trying to be equipped, and that the weaponHolder actually has something there
            if (VRGlobals.oldWeaponHolder && VRGlobals.weaponHolder == __instance.WeaponRoot.parent.gameObject && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                Plugin.MyLog.LogWarning("\n\n Init ball calc 1 \n\n");
                VRGlobals.ikManager.rightArmIk.solver.target = VRPlayerManager.RightHand.transform;
                Transform weaponRoot = VRGlobals.weaponHolder.transform.GetChild(0);
                weaponRoot.parent = VRGlobals.oldWeaponHolder.transform;
                weaponRoot.localPosition = Vector3.zero;
                VRGlobals.oldWeaponHolder = null;
            }
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void MoveWeaponToIKHands(EFT.Player.FirearmController __instance)
        {
            if (__instance._player.IsAI)
                return;

            VRGlobals.firearmController = __instance;
            VRGlobals.player = __instance._player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            Plugin.MyLog.LogError("\n\n" + VRGlobals.oldWeaponHolder + "\n\n");
            VRGlobals.scope = __instance.gclass1555_0.sightModVisualControllers_0[0].transform;
            // Check if a weapon is currently equipped, if that weapon isn't the same as the one trying to be equipped, and that the weaponHolder actually has something there
            //if (oldWeaponHolder && oldWeaponHolder != __instance.WeaponRoot.parent.gameObject && weaponHolder.transform.childCount > 0)
            //{
            //    Plugin.MyLog.LogWarning("\n\n Init ball calc 1 \n\n");
            //    rightArmIk.solver.target = VRPlayerManager.RightHand.transform;
            //    Transform weaponRoot = weaponHolder.transform.GetChild(0);
            //    weaponRoot.parent = oldWeaponHolder.transform;
            //    weaponRoot.localPosition = Vector3.zero;
            //    oldWeaponHolder = null;
            //}

            if (VRGlobals.ikManager.rightArmIk && VRGlobals.oldWeaponHolder == null)
            {
                Plugin.MyLog.LogWarning("\n\n Init ball calc 2 SET RIGHT HAND \n\n");
                // Weapon_root goes in weapon holder, holder goes in VR right hand, set rot of holder to 15,270,90
                // pos to 0.141 0.0204 -0.1003
                // Pistols rot: 7.8225 278.6073 88.9711  pos: 0.3398 -0.0976 -0.1754

                VRPlayerManager.leftHandGunIK = __instance.HandsHierarchy.Transforms[11];
                //weaponRightHandIKPositioner.gameObject.active = false;
                //Positioner positioner = weaponRightHandIKPositioner.gameObject.AddComponent<Positioner>();
                VRGlobals.ikManager.rightArmIk.solver.target = null;
                //Positioner positioner = rightHandIK.AddComponent<Positioner>();
                //positioner.target = rightHandIK.transform;

                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.gameObject;
                __instance.WeaponRoot.transform.parent = VRGlobals.weaponHolder.transform;
                //__instance.WeaponRoot.localPosition = Vector3.zero;
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                if (!__instance.WeaponRoot.gameObject.GetComponent<IKManager>())
                    __instance.WeaponRoot.gameObject.AddComponent<IKManager>();
                if (__instance.Weapon.WeapClass == "pistol")
                    VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.341f, 0.0904f, -0.0803f);
                else if (__instance.Weapon.WeapClass == "marksmanRifle")
                    VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.241f, 0.0204f, -0.1303f);
                else
                    VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.141f, 0.0204f, -0.1303f);
                //else if (__instance.Weapon.WeapClass == "assaultRifle" || __instance.Weapon.WeapClass == "assaultCarbine" || __instance.Weapon.WeapClass == "smg" || __instance.Weapon.WeapClass == "sniperRifle" || __instance.Weapon.WeapClass == "marksmanRifle")
                //    weaponHolder.transform.localPosition = new Vector3(0.141f, 0.0204f, -0.1303f);
                //    //weaponHolder.transform.localPosition = new Vector3(0.111f, 0.0204f, -0.1003f);
                //else if (__instance.Weapon.WeapClass == "smg" || __instance.Weapon.WeapClass == "sniperRifle" || __instance.Weapon.WeapClass == "marksmanRifle")
                //    weaponHolder.transform.localPosition = new Vector3(0.141f, 0.0204f, -0.1303f);
                //else
                //    weaponHolder.transform.localPosition = new Vector3(0.241f, 0.0204f, -0.1003f);
            }


            // 26.0009 320.911 103.7912
            // -0.089 -0.0796 -0.1970 
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        //NOTE:::::::::::::: Height over bore is the reason why close distances shots aren't hitting, but further distance shots SHOULD be fine - test this
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void SetOpticCamFoV(EFT.CameraControl.OpticComponentUpdater __instance, OpticSight opticSight)
        {
            // NOTE::: I don't think FOV matters at all really for 1x if its not a scope, e.g. collimators or the thermal sights, so all fovs can probs default to 26/27
            //StackTrace stackTrace = new StackTrace();
            //Plugin.MyLog.LogWarning(stackTrace.ToString()); 
            //1x    27 FoV  parent/parent/scope_30mm_s&b_pm_ii_1_8x24(Clone)
            //1x    15 fov parent/parent/scope_all_sig_sauer_echo1_thermal_reflex_sight_1_2x_30hz(Clone) parent/scope_all_torrey_pines_logic_t12_w_30hz(Clone)
            //1x    26 FOV for PARENT/PARENT/scope_30mm_eotech_vudu_1_6x24(Clone)
            //1.5x  18 FOV  parent/scope_g36_hensoldt_hkv_single_optic_carry_handle_1,5x(Clone)
            //1.5x  14 FOV parent/scope_aug_steyr_rail_optic_1,5x(Clone)
            //1.5x  15 FOV parent/scope_aug_steyr_stg77_optic_1,5x
            //2x    4 fov   parent/parent/scope_all_sig_sauer_echo1_thermal_reflex_sight_1_2x_30hz(Clone)
            //2x    11 FOV parent/scope_all_monstrum_compact_prism_scope_2x32(Clone)
            //3x    7.5 FOV  parent/scope_g36_hensoldt_hkv_carry_handle_3x(Clone) parent/3
            //3x    7.6 FoV parent/parent/scope_base_kmz_1p59_3_10x(Clone) parent/parent/scope_all_ncstar_advance_dual_optic_3_9x_42(Clone)
            //3x    9 FOV parent/scope_base_npz_1p78_1_2,8x24(Clone)
            //3x    12 FOV parent/parent/scope_34mm_s&b_pm_ii_3_12x50(Clone)
            //3.5x  6   FOV parent/scope_base_progress_pu_3,5x(Clone)
            //3.5x  6.5 FOV parent/scope_dovetail_npz_nspum_3,5x(Clone)
            //3.5x  7.5 FOV parent/scope_all_swampfox_trihawk_prism_scope_3x30(Clone)
            //3.5x  7 FOV parent/scope_base_trijicon_acog_ta11_3,5x35(Clone)
            //16x   1.2 FoV
            //16x   1 FOV parent/parent/scope_34mm_nightforce_atacr_7_35x56(Clone)


            /////2.5x  10 FOV parent/scope_base_primary_arms_compact_prism_scope_2,5x(Clone)
            //////6x    3.2 FOV 
            ///////10x   2.5 FOV parent/parent/scope_base_kmz_1p59_3_10x(Clone)
            //////20x   1.5 fov PARENT/scope_30mm_leupold_mark4_lr_6,5_20x50(Clone)
            /////9x    2.9 FOV parent/parent/scope_all_ncstar_advance_dual_optic_3_9x_42(Clone)
            /////8x    3   FOV parent/parent/scope_30mm_s&b_pm_ii_1_8x24(Clone)
            //////5x    3.6 FOV parent/parent/scope_34mm_s&b_pm_ii_5_25x56(Clone)
            ////////25x   1.9 FOV parent/parent/scope_34mm_s&b_pm_ii_5_25x56(Clone)



            //////4x    6  FoV parent/scope_25_4mm_vomz_pilad_4x32m(Clone) parent/scope_all_leupold_mark4_hamr(Clone) parent/scope_all_sig_bravo4_4x30(Clone)
            /////12x   1.9 FoV parent/parent/scope_34mm_s&b_pm_ii_3_12x50(Clone)
            ///////7x    1.6 FoV parent/parent/scope_34mm_nightforce_atacr_7_35x56(Clone)


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
                        scopeName = opticSight.transform.parent.name;
                        opticSight.transform.parent.gameObject.layer = 6;
                        scopeCollider = opticSight.transform.parent.GetComponent<BoxCollider>();
                    }
                    else
                    {
                        opticSight.gameObject.layer = 6;
                        scopeCollider = opticSight.GetComponent<BoxCollider>();
                    }
                    if (scopeCollider)
                    {
                        scopeCollider.size = new Vector3(0.01f, 0.04f, 0.02f);
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
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CameraClass), "method_10")]
        private static bool FixSomeAimShit(CameraClass __instance)
        {
            __instance.ReflexController.RefreshReflexCmdBuffers();
            Renderer renderer = __instance.OpticCameraManager.CurrentOpticSight?.LensRenderer;
            __instance.method_11();
            if (__instance.OpticCameraManager.CurrentOpticSight != null)
            {
                //LODGroup[] componentsInChildren = __instance.OpticCameraManager.CurrentOpticSight.gameObject.GetComponentInParent<WeaponPrefab>().gameObject.GetComponentsInChildren<LODGroup>();

                ////////////// Since the weapons are moved around to the right controller object, this needs to be redone here
                LODGroup[] componentsInChildren = VRGlobals.oldWeaponHolder.GetComponentsInChildren<LODGroup>();
                if (__instance.renderer_0 != null)
                {
                    Array.Clear(__instance.renderer_0, 0, __instance.renderer_0.Length);
                }
                int instanceID = renderer.GetInstanceID();
                int num = 0;
                int num2 = 0;
                while (componentsInChildren != null && num2 < componentsInChildren.Length)
                {
                    if (!(componentsInChildren[num2] == null))
                    {
                        LOD[] lODs = componentsInChildren[num2].GetLODs();
                        if (lODs.Length != 0)
                        {
                            Renderer[] renderers = lODs[0].renderers;
                            foreach (Renderer renderer2 in renderers)
                            {
                                if (!(renderer2 == null) && renderer2.GetInstanceID() != instanceID && !__instance.method_9(renderer2.GetInstanceID()))
                                {
                                    num++;
                                }
                            }
                        }
                    }
                    num2++;
                }
                if (num > 0 && (__instance.renderer_0 == null || __instance.renderer_0.Length < num))
                {
                    __instance.renderer_0 = new Renderer[num];
                }
                num = 0;
                int num3 = 0;
                while (componentsInChildren != null && num3 < componentsInChildren.Length)
                {
                    if (!(componentsInChildren[num3] == null))
                    {
                        LOD[] lODs2 = componentsInChildren[num3].GetLODs();
                        if (lODs2.Length != 0)
                        {
                            Renderer[] renderers = lODs2[0].renderers;
                            foreach (Renderer renderer3 in renderers)
                            {
                                if (!(renderer3 == null) && renderer3.GetInstanceID() != instanceID && !__instance.method_9(renderer3.GetInstanceID()))
                                {
                                    __instance.renderer_0[num++] = renderer3;
                                }
                            }
                        }
                    }
                    num3++;
                }
            }
            __instance.SSAA.SetLensRenderer(__instance.OpticCameraManager.CurrentOpticSight?.LensRenderer, __instance.renderer_1, __instance.renderer_0);
            __instance.SSAA.UnityTAAJitterSamplesRepeatCount = 2;
            return false;
        }
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
    }
}