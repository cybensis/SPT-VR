using EFT.Rendering.Clouds;
using HarmonyLib;
using System;
using TarkovVR.Source.Settings;
using UnityEngine;
using EFT.Weather;
using System.IO;
using EFT;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class CloudsAndFog
    {
        // Cache for expensive operations
        private static Camera fpsCam;
        private static Camera opticCam;
        private static PrismEffects fpsPrism;
        private static PrismEffects opticPrism;
        private static GameObject cloudInstance;
        private static Renderer lowRenderer;
        private static Renderer highRenderer;
        private static Material lowMaterial;
        private static Material highMaterial;

        // Wind system state
        public static float lastWind = 0.0f;
        public static Vector2 cloudOffset = Vector2.zero;
        public static Vector2 lastWindDirection = new Vector2(1f, 0f);

        //Tarkov Clouds dont render correctly in VR so disable them
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CloudController), "OnEnable")]
        private static void DisableClouds(CloudController __instance)
        {
            __instance.enabled = false;
        }

        //Tarkov fog doesn't render correctly in VR so disable it
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MBOIT_Scattering), "Start")]
        private static void DisableMBOIT(CloudController __instance)
        {
            __instance.enabled = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TOD_Scattering), "OnRenderImage")]
        private static void FixTODScattering(TOD_Scattering __instance, RenderTexture source, RenderTexture destination)
        {
            __instance.enabled = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CloudController), "UpdateAmbient")]
        private static bool FixAmbientErrors(CloudController __instance)
        {
            __instance.enabled = false;
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

            // Initialize fog effects only once
            if (fpsPrism == null)
                fpsPrism = fpsCam.GetComponent<PrismEffects>() ?? fpsCam.gameObject.AddComponent<PrismEffects>();
            //Plugin.MyLog.LogError("Density: " + __instance.tod_Scattering_0.GlobalDensity);
            // Calculate fog properties every frame for smoothness
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

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterControllerSpawner), "Spawn")]
        private static void SpawnClouds(CharacterControllerSpawner __instance)
        {
            EFT.Player player = __instance.transform.root.GetComponent<EFT.Player>();

            if (player is not HideoutPlayer)
            {
                LoadCloudPrefab();
                InstantiateCloudPrefab();
            }
        }
        private static void LoadCloudPrefab()
        {
            if (VRGlobals.cloudPrefab != null)
                return;

            try
            {
                string bundlePath = Path.Combine(BepInEx.Paths.PluginPath, "sptvr", "Assets", "customclouds");
                AssetBundle cloudBundle = AssetBundle.LoadFromFile(bundlePath);
                if (cloudBundle == null)
                {
                    Plugin.MyLog.LogError("Failed to load cloud AssetBundle.");
                    return;
                }

                VRGlobals.cloudPrefab = cloudBundle.LoadAsset<GameObject>("Clouds");
                cloudBundle.Unload(false);
                Plugin.MyLog.LogInfo("Cloud prefab loaded successfully.");
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"Error loading cloud prefab: {ex.Message}");
            }
        }

        private static void InstantiateCloudPrefab()
        {
            if (VRGlobals.cloudPrefab != null && cloudInstance == null)
            {
                cloudInstance = GameObject.Instantiate(VRGlobals.cloudPrefab);
                cloudInstance.transform.position = new Vector3(0, -70, 0);
                cloudInstance.transform.localScale = new Vector3(10f, 10f, 10f);
                Plugin.MyLog.LogInfo("Cloud prefab instantiated.");
            }
        }

        private static void InitializeCloudRenderers()
        {
            if (VRGlobals.cloudPrefab == null)
                return;

            Transform lowCloud = VRGlobals.cloudPrefab.transform.Find("Low");
            Transform highCloud = VRGlobals.cloudPrefab.transform.Find("High");

            if (lowCloud != null)
            {
                lowCloud.gameObject.layer = 28;

                lowRenderer = lowCloud.GetComponent<Renderer>();
                if (lowRenderer != null)
                {
                    lowRenderer.allowOcclusionWhenDynamic = false;
                    lowMaterial = lowRenderer.sharedMaterial;
                }
            }

            if (highCloud != null)
            {
                highCloud.gameObject.layer = 28;

                highRenderer = highCloud.GetComponent<Renderer>();
                if (highRenderer != null)
                {
                    highRenderer.allowOcclusionWhenDynamic = false;
                    highMaterial = highRenderer.sharedMaterial;
                }
            }
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeatherController), "method_9")]
        private static void DynamicClouds(WeatherController __instance, float fog, GStruct275 interpolatedParams)
        {
            if (VRGlobals.cloudPrefab == null)
                return;

            // Initialize renderers and materials only when needed
            if (lowRenderer == null || highRenderer == null)
            {
                InitializeCloudRenderers();
            }

            if (lowRenderer == null && highRenderer == null)
                return;

            // Weather parameters
            float cloudiness = __instance.WeatherCurve.Cloudiness;
            float rain = __instance.WeatherCurve.Rain;
            float timeOfDay = GClass4.Instance.Cycle.Hour;
            Vector2 windVector = __instance.WeatherCurve.Wind;

            UpdateWindSystem(windVector);

            float normalizedCloudiness = Mathf.Clamp((cloudiness + 1f) / 2f, 0f, 1f);
            float slowedCloudiness = Mathf.Pow(normalizedCloudiness, 3f);
            float density = Mathf.Lerp(2f, 0f, slowedCloudiness);
            float cloudAlpha = Mathf.Lerp(0.4f, 0.7f, rain);

            Color cloudColor = CalculateCloudColor(timeOfDay, rain, density);
            cloudColor.a = cloudAlpha;

            UpdateCloudMaterials(density, cloudColor);

            UpdateCloudOffsets();
        }



        private static void UpdateWindSystem(Vector2 windVector)
        {
            const float WIND_DAMPENING = 0.03f;
            const float MIN_WIND = 0.005f;
            const float MAX_WIND = 0.01f;
            const float MIN_WIND_MAGNITUDE = 0.06f;
            const float MAX_WIND_MAGNITUDE = 0.38f;

            float windMagnitude = windVector.magnitude;
            float mappedSpeed = Mathf.Lerp(MIN_WIND, MAX_WIND, Mathf.InverseLerp(MIN_WIND_MAGNITUDE, MAX_WIND_MAGNITUDE, windMagnitude));
            float smoothedWind = Mathf.Lerp(lastWind, mappedSpeed, WIND_DAMPENING * Time.deltaTime);
            lastWind = smoothedWind;

            // Update wind direction
            if (windVector.sqrMagnitude > 0.0001f)
                lastWindDirection = windVector.normalized;
        }

        private static void UpdateCloudOffsets()
        {
            float offsetMagnitude = Mathf.Max(lastWind, 0.005f);
            cloudOffset += lastWindDirection * offsetMagnitude * Time.deltaTime;

            if (lowMaterial != null)
                lowMaterial.SetVector("_Offset", cloudOffset);

            if (highMaterial != null)
                highMaterial.SetVector("_Offset", cloudOffset * 0.7f);
        }

        private static void UpdateCloudMaterials(float density, Color cloudColor)
        {
            if (lowMaterial != null)
            {
                lowMaterial.SetFloat("_Density", density);
                lowMaterial.SetColor("_CloudColor", cloudColor);
            }

            if (highMaterial != null)
            {
                highMaterial.SetFloat("_Density", density);
                highMaterial.SetColor("_CloudColor", cloudColor);
            }
        }

        private static Color CalculateCloudColor(float timeOfDay, float rain, float density)
        {
            Color dayColor = Color.white;
            Color sunsetColor = new Color(0.35f, 0.2f, 0.1f);
            Color nightColor = new Color(0.039f, 0.039f, 0.047f);

            Color cloudColor;

            // Time-based color calculation
            if (timeOfDay >= 5.3f && timeOfDay <= 7.0f)
                cloudColor = Color.Lerp(nightColor, sunsetColor, Mathf.InverseLerp(5.3f, 7.0f, timeOfDay));
            else if (timeOfDay > 7.0f && timeOfDay <= 8.3f)
                cloudColor = Color.Lerp(sunsetColor, dayColor, Mathf.InverseLerp(7.0f, 8.3f, timeOfDay));
            else if (timeOfDay > 8.3f && timeOfDay < 18f)
                cloudColor = dayColor;
            else if (timeOfDay >= 18f && timeOfDay <= 20.3f)
                cloudColor = Color.Lerp(dayColor, sunsetColor, Mathf.InverseLerp(18f, 20.3f, timeOfDay));
            else if (timeOfDay > 20.3f && timeOfDay <= 21.3f)
                cloudColor = Color.Lerp(sunsetColor, nightColor, Mathf.InverseLerp(20.3f, 21.3f, timeOfDay));
            else
                cloudColor = nightColor;

            // Apply rain darkening effect
            if (rain > 0.2f)
            {
                Color rainDarkColor = new Color(0.03f, 0.03f, 0.03f);
                cloudColor = Color.Lerp(cloudColor, rainDarkColor, rain);
            }

            // Apply density-based darkening
            float densityFactor = Mathf.InverseLerp(2f, 0f, density);
            float darkeningAmount = densityFactor * 0.4f;
            cloudColor = Color.Lerp(cloudColor, Color.black, darkeningAmount);

            return cloudColor;
        }
    }
}