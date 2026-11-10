using EFT;
using EFT.EnvironmentEffect;
using EFT.Rendering.Clouds;
using EFT.Weather;
using HarmonyLib;
using System;
using System.Reflection;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using static SSAAImpl;
using static UnityEngine.ParticleSystem.PlaybackState;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class WeatherPatches
    {
        // Cache for expensive operations
        private static Camera fpsCam;
        private static PrismEffects fpsPrism;
        private static GameObject cloudInstance;
        private static Renderer lowRenderer;
        private static Material lowMaterial;

        // Wind system state
        public static float lastWind = 0.0f;
        public static Vector4 cloudOffset = Vector4.zero;
        public static Vector2 lastWindDirection = new Vector2(1f, 0f);


        //Tarkov Clouds dont render correctly in VR so disable them
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CloudLayerRenderer), "RenderClouds")]
        private static bool DisableClouds(CloudLayerRenderer __instance)
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
        [HarmonyPatch(typeof(PrismEffects), "SetFogShaderValues")]
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

        // Old pre-FogSix fog hack: this used to drive a white PrismEffects fog on the scope (UpdateOpticFog)
        // since the scope had no working fog in VR. FogSix now fogs the scope properly through the optic's
        // own tod_Scattering_1, so that hack double-fogged it with a wrong-colored constant haze — removed.
        // Kept only to force the fps prism fog off.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeatherController), "ControlScattering")]
        private static void DynamicFog(WeatherController __instance, float fog, ShowStatNotificationPacket interpolatedParams)
        {
            if (fpsCam == null)
            {
                InitializeCameras();
            }

            if (fpsCam == null || __instance.tod_Scattering_0 == null)
                return;

            if (fpsPrism == null)
            {
                fpsPrism = fpsCam.GetComponent<PrismEffects>() ?? fpsCam.gameObject.AddComponent<PrismEffects>();
            }

            fpsPrism.useFog = false;
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
            }
        }
    }

}
