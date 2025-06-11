using EFT.UI.WeaponModding;
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static EFT.UI.WeaponModding.WeaponModdingScreen;
using UnityEngine;
using Valve.VR;
using EFT.UI.Insurance;
using EFT.UI.Builds;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class WeaponsAndModdingUIPatches
    {

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "Start")]
        private static void ActivateWeaponModdingDropDown(ModdingScreenSlotView __instance)
        {
            __instance._dropDownButton.gameObject.SetActive(true);
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "method_1")]
        private static bool PreventHidingDropDownMethod1(ModdingScreenSlotView __instance)
        {
            if (__instance.bool_0)
            {
                __instance.simpleTooltip_0.Close();
                return false;
            }

            __instance.ginterface453_0.HideModHighlight(overriding: true);
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "method_5")]
        private static bool PreventHidingDropDownMethod5(ModdingScreenSlotView __instance, ModdingScreenSlotView slotView)
        {
            __instance.method_6(slotView == __instance);
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "CheckVisibility")]
        private static bool PreventHidingDropDownCheckVisibility(ModdingScreenSlotView __instance, EModClass visibleClasses)
        {
            bool flag = (visibleClasses & __instance.EModClass_0) != 0;
            __instance.gameObject.SetActive(flag);
            if (!flag && __instance.dropDownMenu_0.Open)
            {
                if (__instance.dropDownMenu_0.Open)
                    __instance.dropDownMenu_0.Close();
            }
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DropDownMenu), "method_1")]
        private static bool FixWeaponModdingDropDownPosition(DropDownMenu __instance)
        {
            __instance.transform.position = __instance.moddingScreenSlotView_0.MenuAnchor.position;
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacteristicsPanel), "Show")]
        private static void FixWeapCharactericsPanel(CharacteristicsPanel __instance)
        {
            Plugin.MyLog.LogError($"[TarkovVR] FixWeapCharactericsPanel: {__instance.name}");
            __instance.transform.localPosition = new Vector3(-1050, 381, 0);
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacteristicsPanel), "Close")]
        private static void FixWeapCharactericsPanelOnClose(CharacteristicsPanel __instance)
        {
            __instance.transform.localPosition = new Vector3(-950, 381, 0);
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacteristicsPanel), "method_3")]
        private static void FixWeapCharactericsPanelOnClick(CharacteristicsPanel __instance, bool expanded)
        {
            if (expanded)
                __instance.transform.localPosition = new Vector3(-1050, 381, 0);
            else
                __instance.transform.localPosition = new Vector3(-950, 381, 0);
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "Show")]
        private static void HideModdingLines(ModdingScreenSlotView __instance)
        {
            __instance._boneIcon.gameObject.SetActive(false);
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        //Redid the way WeaponModdingScreen and EditBuildScreen weapon previews work for SPT 3.11 - old way caused camera freeze
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeaponModdingScreen), "Show", new Type[] { typeof(GClass3632) })]
        private static void PositionWeaponModdingCamera(WeaponModdingScreen __instance, GClass3632 controller)
        {
            int previewLayer = LayerMask.NameToLayer("Weapon Preview");
            GameObject camObj = new GameObject("VRWeaponPreviewCamera");
            Camera cam = camObj.GetComponent<Camera>();
            Transform weaponCam = __instance.highLightMesh_0.transform;

            if (cam == null)
            {
                cam = camObj.AddComponent<Camera>();
                //camObj.transform.SetParent(weaponCam.parent);
                camObj.AddComponent<SteamVR_TrackedObject>();
                cam.stereoTargetEye = StereoTargetEyeMask.Both;
                cam.clearFlags = CameraClearFlags.Depth;
                cam.cullingMask = 1 << previewLayer;
                cam.depth = 11;
            }
            __instance._weaponPreview.Rotator.localPosition = (Camera.main.transform.localPosition) + new Vector3(0, 0.1f, 1.6f);
            __instance._weaponPreview.Rotator.localScale = Vector3.one * 1.5f;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EditBuildScreen), "Show", new Type[] { typeof(EditBuildScreen.GClass3591) })]
        private static void PositionWeaponModdingCamera(EditBuildScreen __instance, EditBuildScreen.GClass3591 controller)
        {
            int previewLayer = LayerMask.NameToLayer("Weapon Preview");
            GameObject camObj = new GameObject("VRWeaponPreviewCamera");
            Camera cam = camObj.GetComponent<Camera>();
            Transform weaponCam = __instance.highLightMesh_0.transform;

            if (cam == null)
            {
                cam = camObj.AddComponent<Camera>();
                //camObj.transform.SetParent(weaponCam.parent);
                camObj.AddComponent<SteamVR_TrackedObject>();
                cam.stereoTargetEye = StereoTargetEyeMask.Both;
                cam.clearFlags = CameraClearFlags.Depth;
                cam.cullingMask = 1 << previewLayer;
                cam.depth = 11;
            }
            __instance._weaponPreview.Rotator.localPosition = (Camera.main.transform.localPosition) + new Vector3(0, 0.1f, 1.6f);
            __instance._weaponPreview.Rotator.localScale = Vector3.one * 1.5f;
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(OpenBuildWindow), "Show")]
        private static void ShowBuildWindowWeaponSelector(OpenBuildWindow __instance)
        {
            __instance.transform.localPosition = new UnityEngine.Vector3(638, -167, 0);
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EditBuildNameWindow), "Show")]
        private static void ShowBuildWindowSave(EditBuildNameWindow __instance)
        {
            __instance.WindowTransform.localPosition = Vector3.zero;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(WeaponPreview), "method_2")]
        private static bool FixWeaponPreviewCamera(WeaponPreview __instance)
        {
            // Need to wait a bit before setting the FoV on this cam because
            // something else is changing it
            __instance._cameraTemplate.stereoTargetEye = StereoTargetEyeMask.None;
            __instance._cameraTemplate.fieldOfView = 22;

            return true;
        }
    }
}
