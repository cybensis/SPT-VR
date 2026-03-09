using EFT.Rendering.Clouds;
using HarmonyLib;
using System;
using TarkovVR.Source.Settings;
using UnityEngine;
using EFT.Weather;
using System.IO;
using EFT;
using static UnityEngine.ParticleSystem.PlaybackState;
using Comfort.Common;
using UnityEngine.Rendering;
using static GClass3809;
using UnityEngine.XR;
using System.Reflection;
using static SSAAImpl;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class WeatherPatches
    {
        // Cache for expensive operations
        private static Camera fpsCam;
        private static Camera opticCam;
        private static PrismEffects fpsPrism;
        private static PrismEffects opticPrism;
        private static GameObject cloudInstance;
        private static Renderer lowRenderer;
        private static Material lowMaterial;

        // Wind system state
        public static float lastWind = 0.0f;
        public static Vector4 cloudOffset = Vector4.zero;
        public static Vector2 lastWindDirection = new Vector2(1f, 0f);
       

        //Tarkov Clouds dont render correctly in VR so disable them
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Class1821), "RenderClouds")]
        private static bool DisableClouds(Class1821 __instance)
        {
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TOD_Camera), "Update")]
        private static bool FixTODCamera(TOD_Camera __instance)
        {
            if ((bool)__instance.sky && __instance.sky.Initialized)
            {
                __instance.sky.Components.Camera = __instance;
            }
            return false;
            //__instance.enabled = false;
        }

        //Tarkov fog doesn't render correctly in VR so disable it
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MBOIT_Scattering), "Start")]
        private static void DisableMBOIT(CloudController __instance)
        {
            //__instance.enabled = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TOD_Scattering), "OnRenderImage")]
        private static void FixTODScattering(TOD_Scattering __instance, RenderTexture source, RenderTexture destination)
        {

            __instance.enabled = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PrismEffects), "method_5")]
        private static bool FixFogForVR(PrismEffects __instance, Material fogMaterial)
        {
            Camera cam = __instance.GetPrismCamera();

            fogMaterial.SetFloat("_FogHeight", __instance.fogHeight);
            fogMaterial.SetFloat("_FogIntensity", 1f);
            fogMaterial.SetFloat("_FogDistance", __instance.fogDistance);
            fogMaterial.SetFloat("_FogStart", __instance.fogStartPoint);
            fogMaterial.SetColor("_FogColor", __instance.fogColor);
            fogMaterial.SetColor("_FogEndColor", __instance.fogEndColor);
            fogMaterial.SetFloat("_FogBlurSkybox", 0.9999999f);
            Matrix4x4 worldMatrix = Matrix4x4.TRS(
                cam.transform.position,
                Quaternion.identity,
                Vector3.one
            );

            fogMaterial.SetMatrix(Shader.PropertyToID("_InverseView"), worldMatrix);

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeatherController), "method_9")]
        private static void DynamicFog(WeatherController __instance, float fog, GStruct275 interpolatedParams)
        {
            // Initialize cameras only when needed
            if (fpsCam == null || opticCam == null)
            {
                InitializeCameras();
            }

            if (fpsCam == null || __instance.tod_Scattering_0 == null)
                return;

            if (fpsPrism == null)
            {
                fpsPrism = fpsCam.GetComponent<PrismEffects>() ?? fpsCam.gameObject.AddComponent<PrismEffects>();
            }

            float fogDistance = Mathf.Clamp(-6944.44f * __instance.tod_Scattering_0.GlobalDensity + 544.22f, 100f, 500f);
            float fogAlpha = Mathf.Lerp(0.7f, 0.2f, (fogDistance - 100f) / 400f);
            Color fogColor = new Color(1f, 1f, 1f, fogAlpha);

            fpsPrism.useFog = !VRSettings.GetDisableFog();

            if (fpsPrism.useFog)
            {
                fpsPrism.fogDistance = fogDistance;
                fpsPrism.fogColor = fogColor;
                fpsPrism.fogEndColor = fogColor;
            }

            UpdateOpticFog(fogDistance, fogColor);
        }
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PrismEffects), "method_12")]
        private static void ApplyFXAAAfterAllEffects(PrismEffects __instance, RenderTexture source, RenderTexture destination)
        {
            Rendering.LoadFXAAShader();
            if (Rendering._fxaaMat == null || !__instance.useFog) return;

            RenderTexture temp = RenderTexture.GetTemporary(destination.descriptor);
            Graphics.Blit(destination, temp);
            Graphics.Blit(temp, destination, Rendering._fxaaMat);
            RenderTexture.ReleaseTemporary(temp);
        }
        */
        private static void InitializeCameras()
        {
            foreach (var cam in Camera.allCameras)
            {
                if (cam.name == "FPS Camera")
                    fpsCam = cam;
                else if (cam.name == "BaseOpticCamera(Clone)")
                    opticCam = cam;
            }
        }

        private static void UpdateOpticFog(float fogDistance, Color fogColor)
        {
            if (opticCam != null)
            {
                if (opticPrism == null)
                    opticPrism = opticCam.GetComponent<PrismEffects>() ?? opticCam.gameObject.AddComponent<PrismEffects>();

                if (VRSettings.GetDisablePrismEffects())
                {
                    opticPrism.enabled = false;
                    return;
                }

                opticPrism.enabled = true;
                opticPrism.useFog = !VRSettings.GetDisableFog();

                if (opticPrism.useFog)
                {
                    opticPrism.fogDistance = fogDistance;
                    opticPrism.fogColor = fogColor;
                    opticPrism.fogEndColor = fogColor;
                }
            }
        }      
    }

}