using EFT.CameraControl;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Patches.Core.Equippables;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Patches.Core.WeaponMods
{
    [HarmonyPatch]
    internal class OpticPatches
    {

        //------------------------------------------------------   OPTICS GLOBALS  ---------------------------------------------------------------------------
        //New scope zoom handler, this patch waits for vrOpticController to be created before going forward with the CopyComponentFromOptic method
        public static SightModVisualControllers visualController;
        public static float zoomLevel;
        public static Transform currentScope;

        //------------------------------------------------------   OPTICS PATCHES  ---------------------------------------------------------------------------
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


        //----------------------------------------------------------------------------------------------------------------------------------------------------
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


        //----------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void WaitForVROpticController(EFT.CameraControl.OpticComponentUpdater __instance, OpticSight opticSight)
        {
            if (!VRGlobals.vrOpticController)
                return;
        }


        //----------------------------------------------------------------------------------------------------------------------------------------------------
        //This actually handles the scope zoom
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void SetOpticCamFoV(EFT.CameraControl.OpticComponentUpdater __instance, OpticSight opticSight)
        {
            float fov;

            if (!visualController)
                visualController = opticSight.transform.parent.GetComponent<SightModVisualControllers>();

            if (EquippablesShared.rangeFinder)
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

        //----------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CollimatorSight), "OnEnable")]
        private static void FixCollimatorParallaxEffect(CollimatorSight __instance)
        {
            // Mark scale pulls the red dot/holo closer to the lens the higher it is, which makes it shift around when looking
            // as this value decreases it gets pushed further away from the lens, gets smaller, but it stops shifting around
            float markScale = __instance.CollimatorMaterial.GetFloat("_MarkScale");
            if (markScale != 1)
            {
                __instance.CollimatorMaterial.SetFloat("_MarkScale", 1f);
                // Mark shift increases the size of the dot/holo the smaller the value, the more negative it gets the smaller it gets
                float newMarkShift = 150 + ((1 - markScale) * 125);
                __instance.CollimatorMaterial.SetVector("_MarkShift", new Vector4(0, newMarkShift * -1, 0, 0));
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
    }
}
