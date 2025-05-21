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

        //This replaces the fog system BSG uses and uses the fog from the Prism Post Process effects instead. This does have a drawback though,
        //fog will appear inside of buildings (I think it's better than no fog at all or broken fog)       
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TOD_Scattering), "OnRenderImage")]
        private static void FixTODScattering(TOD_Scattering __instance, RenderTexture source, RenderTexture destination)
        {
            __instance.enabled = false;
        }

        private static Camera fpsCam;
        private static Camera opticCam;
        private static PrismEffects fpsPrism;
        private static PrismEffects opticPrism;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeatherController), "method_9")]
        private static void DynamicFog(WeatherController __instance, float fog, GStruct275 interpolatedParams)
        {
            if (fpsCam == null)
            {
                foreach (var cam in Camera.allCameras)
                {
                    if (cam.name == "FPS Camera")
                        fpsCam = cam;
                    else if (cam.name == "BaseOpticCamera(Clone)")
                        opticCam = cam;
                }
            }

            if (fpsCam == null || __instance.tod_Scattering_0 == null)
                return;

            if (fpsPrism == null)
                fpsPrism = fpsCam.GetComponent<PrismEffects>() ?? fpsCam.gameObject.AddComponent<PrismEffects>();

            // Enable/disable fog effect
            if (VRSettings.GetDisableFog())
                fpsPrism.useFog = false;
            else
                fpsPrism.useFog = true;

            // Calculate fog properties
            float fogDistance = Mathf.Clamp(-6944.44f * __instance.tod_Scattering_0.GlobalDensity + 544.22f, 100f, 500f);
            float fogAlpha = Mathf.Lerp(0.7f, 0.08f, (fogDistance - 100f) / 400f);
            Color fogColor = new Color(1f, 1f, 1f, fogAlpha);

            // Apply fog settings to FPS camera
            fpsPrism.fogDistance = fogDistance;
            fpsPrism.fogColor = fogColor;
            fpsPrism.fogEndColor = fogColor;

            //Plugin.MyLog.LogError("FPS Camera FogDistance: " + fpsPrism.fogDistance + " - " + __instance.tod_Scattering_0.GlobalDensity);
            // Process optic camera if it exists
            if (opticCam != null)
            {
                if (opticPrism == null)
                    opticPrism = opticCam.GetComponent<PrismEffects>() ?? opticCam.gameObject.AddComponent<PrismEffects>();

                if (VRSettings.GetDisablePrismEffects())
                {
                    opticPrism.enabled = false;
                    return;
                }

                if (VRSettings.GetDisableFog())
                    opticPrism.useFog = false;
                else
                    opticPrism.useFog = true;

                opticPrism.enabled = true;
                opticPrism.fogDistance = fogDistance;
                opticPrism.fogColor = fogColor;
                opticPrism.fogEndColor = fogColor;
            }
        }
        private static GameObject cloudInstance;

        //This is the custom cloud system that replaces the BSG clouds. It uses a custom shader and a prefab to render the clouds and uses the weather system to control the density and color of the clouds.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterControllerSpawner), "Spawn")]
        private static void SpawnClouds(CharacterControllerSpawner __instance)
        {
            EFT.Player player = __instance.transform.root.GetComponent<EFT.Player>();
           
            if (player is not HideoutPlayer)
            {
                if (VRGlobals.cloudPrefab == null)
                {
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
                        cloudBundle.Unload(false); // Unload to free memory, but keep the prefab in VRGlobals
                        Plugin.MyLog.LogInfo("Cloud prefab loaded successfully.");
                    }
                    catch (Exception ex)
                    {
                        Plugin.MyLog.LogError($"Error loading cloud prefab: {ex.Message}");
                    }
                }

                // Instantiate the cloud prefab
                if (VRGlobals.cloudPrefab != null && cloudInstance == null)
                {
                    cloudInstance = GameObject.Instantiate(VRGlobals.cloudPrefab);
                    cloudInstance.transform.position = new Vector3(0, -70, 0);  // Adjust as needed
                    cloudInstance.transform.localScale = new Vector3(10f, 10f, 10f); // Adjust as needed
                    Plugin.MyLog.LogInfo("Cloud prefab instantiated.");
                }
            }
        }

        public static float lastWind = 0.0f;
        public static Vector2 cloudOffset = Vector2.zero;
        public static Vector2 lastWindDirection = new Vector2(1f, 0f);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeatherController), "method_9")]
        private static void DynamicClouds(WeatherController __instance, float fog, GStruct275 interpolatedParams)
        {
            if (VRGlobals.cloudPrefab == null)
                return;

            Transform lowCloud = VRGlobals.cloudPrefab.transform.Find("Low");
            Transform highCloud = VRGlobals.cloudPrefab.transform.Find("High");

            if (lowCloud == null && highCloud == null)
                return;

            Renderer lowRenderer = lowCloud?.GetComponent<Renderer>();
            Renderer highRenderer = highCloud?.GetComponent<Renderer>();

            if (lowRenderer == null && highRenderer == null)
                return;

            // Weather parameters
            float cloudiness = __instance.WeatherCurve.Cloudiness;
            float rain = __instance.WeatherCurve.Rain;
            float timeOfDay = __instance.TOD_Sky_0.Cycle.Hour;
            Vector2 windVector = __instance.WeatherCurve.Wind;

            // Wind constants
            const float WIND_DAMPENING = 0.03f;
            const float MIN_WIND = 0.005f;
            const float MAX_WIND = 0.01f;
            const float MIN_WIND_MAGNITUDE = 0.06f;
            const float MAX_WIND_MAGNITUDE = 0.38f;

            // Calculate wind speed
            float windMagnitude = windVector.magnitude;
            float mappedSpeed = Mathf.Lerp(MIN_WIND, MAX_WIND, Mathf.InverseLerp(MIN_WIND_MAGNITUDE, MAX_WIND_MAGNITUDE, windMagnitude));
            float smoothedWind = Mathf.Lerp(lastWind, mappedSpeed, WIND_DAMPENING * Time.deltaTime);
            lastWind = smoothedWind;

            // Cloud density
            float normalizedCloudiness = Mathf.Clamp((cloudiness + 1f) / 2f, 0f, 1f);
            float density = Mathf.Lerp(2f, 0f, normalizedCloudiness);
            float cloudAlpha = Mathf.Lerp(0.3f, 0.6f, rain);

            // Time-based colors
            Color dayColor = Color.white;
            Color sunsetColor = new Color(0.35f, 0.2f, 0.1f);
            Color nightColor = new Color(0.039f, 0.039f, 0.047f);

            // Calculate cloud color based on time of day and rain
            Color cloudColor = CalculateCloudColor(timeOfDay, rain, dayColor, sunsetColor, nightColor);
            cloudColor.a = cloudAlpha;

            // Update cloud materials
            UpdateCloudMaterial(lowRenderer, density, cloudColor);
            UpdateCloudMaterial(highRenderer, density, cloudColor);

            // Update cloud movement
            Vector2 windDirection = UpdateWindDirection(windVector);
            float offsetMagnitude = Mathf.Max(smoothedWind, MIN_WIND);
            cloudOffset += windDirection * offsetMagnitude * Time.deltaTime;

            // Apply offsets with different speeds for high/low clouds
            if (lowRenderer != null)
                lowRenderer.sharedMaterial.SetVector("_Offset", cloudOffset);

            if (highRenderer != null)
                highRenderer.sharedMaterial.SetVector("_Offset", cloudOffset * 0.7f);
        }

        private static Color CalculateCloudColor(float timeOfDay, float rain, Color dayColor, Color sunsetColor, Color nightColor)
        {
            Color cloudColor;

            // Early sunrise transition (night to sunset)
            if (timeOfDay >= 5.3f && timeOfDay <= 7.0f)
                cloudColor = Color.Lerp(nightColor, sunsetColor, Mathf.InverseLerp(5.3f, 7.0f, timeOfDay));
            // Late sunrise transition (sunset to day)
            else if (timeOfDay > 7.0f && timeOfDay <= 8.3f)
                cloudColor = Color.Lerp(sunsetColor, dayColor, Mathf.InverseLerp(7.0f, 8.3f, timeOfDay));
            // Daytime
            else if (timeOfDay > 8.3f && timeOfDay < 18f)
                cloudColor = dayColor;
            // Sunset transition
            else if (timeOfDay >= 18f && timeOfDay <= 20.3f)
                cloudColor = Color.Lerp(dayColor, sunsetColor, Mathf.InverseLerp(18f, 20.3f, timeOfDay));
            // Transition to night
            else if (timeOfDay > 20.3f && timeOfDay <= 21.3f)
                cloudColor = Color.Lerp(sunsetColor, nightColor, Mathf.InverseLerp(20.3f, 21.3f, timeOfDay));
            // Nighttime
            else
                cloudColor = nightColor;

            // Apply rain darkening effect
            if (rain > 0.2f)
            {
                Color rainDarkColor = new Color(0.03f, 0.03f, 0.03f);
                cloudColor = Color.Lerp(cloudColor, rainDarkColor, rain);
            }

            return cloudColor;
        }

        private static void UpdateCloudMaterial(Renderer renderer, float density, Color cloudColor)
        {
            if (renderer == null)
                return;

            renderer.sharedMaterial.SetFloat("_Density", density);
            renderer.sharedMaterial.SetColor("_CloudColor", cloudColor);
        }

        private static Vector2 UpdateWindDirection(Vector2 windVector)
        {
            // Use last known direction if wind is calm, otherwise update direction
            if (windVector.sqrMagnitude < 0.0001f)
                return lastWindDirection;

            lastWindDirection = windVector.normalized;
            return lastWindDirection;
        }
    }
}
