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
        public static Vector2 cloudOffset = Vector2.zero;
        public static Vector2 lastWindDirection = new Vector2(1f, 0f);
       

        //Tarkov Clouds dont render correctly in VR so disable them
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CloudController), "OnEnable")]
        private static void DisableClouds(CloudController __instance)
        {
            __instance.enabled = false;
        }

        private static RenderTexture[] _perEyeScatteringRT;
        private static CommandBuffer _vrScatteringCmdBuffer;
        private static readonly int int_1 = Shader.PropertyToID("_FrustumCornersWS");
        private static readonly int int_2 = Shader.PropertyToID("_DitheringTexture");
        private static readonly int int_3 = Shader.PropertyToID("_Density");
        private static readonly int int_4 = Shader.PropertyToID("_SunrizeGlow");
        private static readonly int int_5 = Shader.PropertyToID("_ScatteringTex");
        static readonly int _InvVP = Shader.PropertyToID("_InverseViewProjection");
        private static Matrix4x4 view;
        private static Matrix4x4 proj;
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TOD_Scattering), "OnRenderImageNormalMode")]
        private static bool StereoFix_TODScattering(TOD_Scattering __instance, RenderTexture source, RenderTexture destination)
        {
            if (!__instance.CheckSupport(needDepth: true, needHdr: true))
            {
                Graphics.Blit(source, destination);
                return false;
            }

            Camera cam = __instance.cam;
            __instance.Sky.Components.Scattering = __instance;
            Camera.MonoOrStereoscopicEye eye = cam.stereoActiveEye;

            // --- PART 1: GEOMETRY (The Fix for Pitch/Roll/Swimming) ---
            // Instead of manually calculating vectors using generic FOV/Aspect,
            // we ask Unity for the EXACT frustum corners used by the VR eye.
            // This accounts for the asymmetric projection of headsets.

            Vector3[] frustumCorners = new Vector3[4];
            // Calculate corners at the Far Clip Plane for the current eye
            cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), cam.farClipPlane, eye, frustumCorners);

            // Transform them from Local Space (Camera relative) to World Space
            Vector3 bottomLeft = cam.transform.TransformVector(frustumCorners[0]);
            Vector3 topLeft = cam.transform.TransformVector(frustumCorners[1]);
            Vector3 topRight = cam.transform.TransformVector(frustumCorners[2]);
            Vector3 bottomRight = cam.transform.TransformVector(frustumCorners[3]);

            Camera.StereoscopicEye stereoEye = (eye == Camera.MonoOrStereoscopicEye.Left) ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;

            view = cam.GetStereoViewMatrix(stereoEye);
            proj = GL.GetGPUProjectionMatrix(cam.GetStereoProjectionMatrix(stereoEye), false);

            Matrix4x4 vp = proj * view;
            Matrix4x4 invVP = vp.inverse;
            //__instance.material_0.SetMatrix(_InvVP, invVP);
            Shader.SetGlobalMatrix("UNITY_MATRIX_I_VP", invVP);
            // TOD_Scattering expects rows in this order: TL, TR, BR, BL
            Matrix4x4 identity = Matrix4x4.identity;
            identity.SetRow(0, topLeft);
            identity.SetRow(1, topRight);
            identity.SetRow(2, bottomRight);
            identity.SetRow(3, bottomLeft);

            // --- PART 2: DENSITY STABILIZATION (From previous step) ---
            if (__instance.FromLevelSettings)
            {
                LevelSettings instance = Singleton<LevelSettings>.Instance;
                if (instance != null)
                {
                    __instance.HeightFalloff = instance.HeightFalloff;
                    __instance.ZeroLevel = instance.ZeroLevel;
                }
            }

            __instance.material_0.SetMatrix(int_1, identity);
            __instance.material_0.SetTexture(int_2, __instance.DitheringTexture);

            Vector3 camPos = cam.transform.position;
            float densityBaseOffset = camPos.y - __instance.ZeroLevel;

            // Counter-animate the fog height so it stays pinned to the Player Body
            if (VRGlobals.player != null)
            {
                // Formula: Height = CamPos.y - Offset.
                // We want Height to be (Player.y - ZeroLevel).
                // So: Player.y - ZeroLevel = CamPos.y - Offset
                // Offset = CamPos.y - Player.y + ZeroLevel
                densityBaseOffset = camPos.y - VRGlobals.player.Transform.position.y + __instance.ZeroLevel;
            }

            // Note: The shader subtracts this Y value from the World Pos Y.
            // If the shader logic is standard TOD, the second param is 'Height'.
            // We pass the calculated offset here.
            //Shader.SetGlobalVector(int_3, new Vector4(__instance.HeightFalloff, densityBaseOffset, __instance.GlobalDensity, 0f));
            __instance.material_0.SetVector(int_3,
    new Vector4(__instance.HeightFalloff, densityBaseOffset, __instance.GlobalDensity, 0f));

            __instance.material_0.SetFloat(int_4, __instance.SunrizeGlow);
            if (__instance.Lighten)
            {
                __instance.material_0.EnableKeyword("LIGHTEN");
            }
            else
            {
                __instance.material_0.DisableKeyword("LIGHTEN");
            }

            RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
            Vector4 densityVec =
    new Vector4(__instance.HeightFalloff, densityBaseOffset, __instance.GlobalDensity, 0f);

            __instance.material_0.SetVector(int_3, densityVec);
            __instance.CustomBlit(source, temporary, __instance.material_0, 1);
            __instance.material_0.SetVector(int_3, densityVec);
            __instance.material_0.SetTexture(int_5, temporary);
            __instance.CustomBlit(source, destination, __instance.material_0);
            //Shader.SetGlobalTexture(int_5, temporary);
            __instance.material_0.SetTexture(int_5, temporary);
            RenderTexture.ReleaseTemporary(temporary);
            return false;
        }
        */
        /*
        public static void TODScatteringRender(Camera.MonoOrStereoscopicEye eye, TOD_Scattering __instance, RenderTexture source, RenderTexture destination)
        {
            Camera cam = VRGlobals.VRCam;
            Vector3[] frustumCorners = new Vector3[4];
            // Calculate corners at the Far Clip Plane for the current eye
            cam.CalculateFrustumCorners(new Rect(0, 0, 1, 1), cam.farClipPlane, eye, frustumCorners);

            // Transform them from Local Space (Camera relative) to World Space
            Vector3 bottomLeft = cam.transform.TransformVector(frustumCorners[0]);
            Vector3 topLeft = cam.transform.TransformVector(frustumCorners[1]);
            Vector3 topRight = cam.transform.TransformVector(frustumCorners[2]);
            Vector3 bottomRight = cam.transform.TransformVector(frustumCorners[3]);

            Camera.StereoscopicEye stereoEye = (eye == Camera.MonoOrStereoscopicEye.Left) ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;

            view = cam.GetStereoViewMatrix(stereoEye);
            proj = GL.GetGPUProjectionMatrix(cam.GetStereoProjectionMatrix(stereoEye), false);

            Matrix4x4 vp = proj * view;
            Matrix4x4 invVP = vp.inverse;

            Matrix4x4 identity = Matrix4x4.identity;

            if (eye == Camera.MonoOrStereoscopicEye.Right)
            {
                // Instead of swapping rows, we "invert" the horizontal component 
                // relative to the camera's right vector.
                Vector3 camRight = cam.transform.right;

                identity.SetRow(0, topLeft - 2 * Vector3.Project(topLeft, camRight));
                identity.SetRow(1, topRight - 2 * Vector3.Project(topRight, camRight));
                identity.SetRow(2, bottomRight - 2 * Vector3.Project(bottomRight, camRight));
                identity.SetRow(3, bottomLeft - 2 * Vector3.Project(bottomLeft, camRight));
            }
            else
            {
                // Standard order for Left eye - which you said is perfect
                identity.SetRow(0, topLeft);
                identity.SetRow(1, topRight);
                identity.SetRow(2, bottomRight);
                identity.SetRow(3, bottomLeft);
            }

            // --- PART 2: DENSITY STABILIZATION (From previous step) ---
            if (__instance.FromLevelSettings)
            {
                LevelSettings instance = Singleton<LevelSettings>.Instance;
                if (instance != null)
                {
                    __instance.HeightFalloff = instance.HeightFalloff;
                    __instance.ZeroLevel = instance.ZeroLevel;
                }
            }

            __instance.material_0.SetMatrix(int_1, identity);
            __instance.material_0.SetTexture(int_2, __instance.DitheringTexture);

            __instance.material_0.SetVector(int_3, new Vector4(__instance.HeightFalloff, __instance.ZeroLevel, __instance.GlobalDensity, 0f));

            __instance.material_0.SetFloat(int_4, __instance.SunrizeGlow);
            if (__instance.Lighten)
            {
                __instance.material_0.EnableKeyword("LIGHTEN");
            }
            else
            {
                __instance.material_0.DisableKeyword("LIGHTEN");
            }

            RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
            Vector4 densityVec = new Vector4(__instance.HeightFalloff, __instance.ZeroLevel, __instance.GlobalDensity, 0f);

            __instance.material_0.SetVector(int_3, densityVec);
            __instance.CustomBlit(source, temporary, __instance.material_0, 1);
            __instance.material_0.SetVector(int_3, densityVec);
            __instance.material_0.SetTexture(int_5, temporary);
            __instance.CustomBlit(source, destination, __instance.material_0);
            __instance.material_0.SetTexture(int_5, temporary);
            RenderTexture.ReleaseTemporary(temporary);
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TOD_Scattering), "OnRenderImageNormalMode")]
        private static bool StereoFix_TODScattering(TOD_Scattering __instance, RenderTexture source, RenderTexture destination)
        {
            if (!__instance.CheckSupport(needDepth: true, needHdr: true))
            {
                Graphics.Blit(source, destination);
                return false;
            }

            Camera cam = __instance.cam;
            __instance.Sky.Components.Scattering = __instance;
            Camera.MonoOrStereoscopicEye eye = cam.stereoActiveEye;

            // --- PART 1: GEOMETRY (The Fix for Pitch/Roll/Swimming) ---
            // Instead of manually calculating vectors using generic FOV/Aspect,
            // we ask Unity for the EXACT frustum corners used by the VR eye.
            // This accounts for the asymmetric projection of headsets.
            if (eye == Camera.MonoOrStereoscopicEye.Left)
            {
                TODScatteringRender(eye, __instance, source, destination);
            }
            else if (eye == Camera.MonoOrStereoscopicEye.Right)
            {
                TODScatteringRender(eye, __instance, source, destination);
            }
            
            return false;
        }
        */
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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PrismEffects), "method_5")]
        private static bool FixFogForVR(PrismEffects __instance, Material fogMaterial)
        {
            Camera cam = __instance.GetPrismCamera();
            // 1. Set all the standard properties (mirroring original code)
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

                VRGlobals.cloudPrefab = cloudBundle.LoadAsset<GameObject>("Clouds New");
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

            if (lowCloud != null)
            {
                lowCloud.gameObject.layer = 28;

                lowRenderer = lowCloud.GetComponent<Renderer>();
                if (lowRenderer != null)
                {
                    lowRenderer.allowOcclusionWhenDynamic = false;
                    lowRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                    lowMaterial = lowRenderer.sharedMaterial;
                }
            }

            if (lowMaterial != null)
            {
                float tilingVariation0 = UnityEngine.Random.Range(0.3f, 0.8f);
                float tilingVariation1 = UnityEngine.Random.Range(0.3f, 0.8f);

                //lowMaterial.SetTextureScale("_ScatterMap0", new Vector2(tilingVariation0, tilingVariation0));
                //lowMaterial.SetTextureScale("_ScatterMap1", new Vector2(tilingVariation1, tilingVariation1));

                cloudOffset = new Vector4(UnityEngine.Random.Range(0f, 100f), UnityEngine.Random.Range(0f, 100f), UnityEngine.Random.Range(0f, 100f), UnityEngine.Random.Range(0f, 100f));


            }
        }
        private static CommandBuffer cloudCommandBuffer;
        private static Camera lastRegisteredCamera;

        private static void SetupCloudCommandBuffer(Camera cam)
        {
            if (cam == null || lowRenderer == null)
                return;

            if (lastRegisteredCamera != null && lastRegisteredCamera != cam && cloudCommandBuffer != null)
            {
                lastRegisteredCamera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, cloudCommandBuffer);
                cloudCommandBuffer.Dispose();
                cloudCommandBuffer = null;
            }

            if (cloudCommandBuffer != null)
                return;

            lowRenderer.enabled = false;

            cloudCommandBuffer = new CommandBuffer();
            cloudCommandBuffer.name = "VR Clouds";

            cam.AddCommandBuffer(CameraEvent.AfterForwardOpaque, cloudCommandBuffer);
            lastRegisteredCamera = cam;

            Plugin.MyLog.LogInfo("Cloud CommandBuffer initialized.");
        }

        public static void CleanupClouds()
        {
            if (cloudCommandBuffer != null)
            {
                if (lastRegisteredCamera != null)
                {
                    lastRegisteredCamera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, cloudCommandBuffer);
                }
                cloudCommandBuffer.Dispose();
                cloudCommandBuffer = null;
            }

            lastRegisteredCamera = null;

            if (cloudInstance != null)
            {
                GameObject.Destroy(cloudInstance);
                cloudInstance = null;
            }

            lowRenderer = null;
            lowMaterial = null;
            VRGlobals.cloudPrefab = null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeatherController), "method_9")]
        private static void DynamicClouds(WeatherController __instance, float fog, GStruct275 interpolatedParams)
        {
            if (VRGlobals.cloudPrefab == null)
                return;

            if (lowRenderer == null)
            {
                InitializeCloudRenderers();
                if (lowRenderer == null)
                    return;
            }

            Camera vrCam = VRGlobals.VRCam;
            if (vrCam == null || vrCam.name != "FPS Camera")
                return;

            SetupCloudCommandBuffer(vrCam);

            cloudInstance.transform.position = vrCam.transform.position;

            if (cloudCommandBuffer != null && lowRenderer != null && lowMaterial != null)
            {
                cloudCommandBuffer.Clear();
                cloudCommandBuffer.DrawRenderer(lowRenderer, lowMaterial);
            }

            float cloudiness = __instance.WeatherCurve.Cloudiness;
            float rain = __instance.WeatherCurve.Rain;
            float timeOfDay = GClass4.Instance.Cycle.Hour;
            Vector2 windVector = __instance.WeatherCurve.Wind;

            UpdateWindSystem(windVector);
            
            // Cloudiness (-1 to 1) -> Density (0.6 clear to 1.7 overcast)
            float normalizedCloudiness = (cloudiness + 1f) * 0.5f;
            float density = Mathf.Lerp(0.6f, 1.75f, normalizedCloudiness);
            float upperDensity = Mathf.Lerp(0.6f, 1.2f, Mathf.InverseLerp(0.6f, 1.75f, density));

            CalculateLightingParameters(timeOfDay, out Color sunColor, out Color moonColor, out Vector3 sunDir, out Vector3 moonDir, out float sunIntensity, out float moonIntensity);

            // Rain darkens clouds and sun
            //sunColor = ApplyRainEffect(sunColor, rain);

            Color cloudColor = CalculateCloudColor(sunColor, moonColor, timeOfDay, out float upperBrightness);
            UpdateCloudMaterial(density, upperDensity, sunColor, moonColor, sunDir, moonDir, sunIntensity, moonIntensity, cloudColor, upperBrightness);
            UpdateCloudOffsets();
            
        }

        private static void UpdateWindSystem(Vector2 windVector)
        {
            const float WIND_DAMPENING = 0.02f;
            const float MIN_WIND = 0.00025f; 
            const float MAX_WIND = 0.002f;
            const float MIN_WIND_MAGNITUDE = 0.0f;
            const float MAX_WIND_MAGNITUDE = 0.5f;

            float windMagnitude = windVector.magnitude;
            float mappedSpeed = Mathf.Lerp(MIN_WIND, MAX_WIND, Mathf.InverseLerp(MIN_WIND_MAGNITUDE, MAX_WIND_MAGNITUDE, windMagnitude));
            lastWind = Mathf.Lerp(lastWind, mappedSpeed, WIND_DAMPENING * Time.deltaTime);

            if (windVector.sqrMagnitude > 0.0001f)
                lastWindDirection = windVector.normalized;
        }

        private static void UpdateCloudOffsets()
        {
            cloudOffset += lastWindDirection * lastWind * Time.deltaTime;

            if (lowMaterial != null)
                lowMaterial.SetVector("_Offset", cloudOffset);
        }

        private static Color ApplyRainEffect(Color sunColor, float rain)
        {
            if (rain <= 0.2f)
                return sunColor;

            float rainT = Mathf.InverseLerp(0.2f, 1.0f, rain);

            // Dim
            sunColor *= Mathf.Lerp(1.0f, 0.7f, rainT);

            // Desaturate toward gray
            float gray = (sunColor.r + sunColor.g + sunColor.b) / 3f;
            return Color.Lerp(sunColor, new Color(gray, gray, gray, sunColor.a), rainT * 0.5f);
        }

        private static Color CalculateCloudColor(Color sunColor, Color moonColor, float timeOfDay, out float upperBrightness)
        {
            Color sourceColor;
            float desaturateAmount;
            float brightnessMultiplier;

            if (timeOfDay >= 4.3f && timeOfDay <= 8f)
            {
                // Dawn: blend source color, start darker
                float t = Mathf.InverseLerp(4.3f, 8f, timeOfDay);
                sourceColor = Color.Lerp(moonColor, sunColor, t);
                desaturateAmount = Mathf.Lerp(0.7f, 0.6f, t);
                brightnessMultiplier = Mathf.Lerp(0.2f, 0.85f, t);
                // Upper clouds catch light earlier - brighter at dawn start
                upperBrightness = Mathf.Lerp(0.4f, 1.1f, t);
            }
            else if (timeOfDay > 8f && timeOfDay <= 19f)
            {
                // Day
                sourceColor = sunColor;
                desaturateAmount = 0.6f;
                brightnessMultiplier = 0.9f;
                upperBrightness = 1.1f;
            }
            else if (timeOfDay > 19f && timeOfDay <= 22.3f)
            {
                // Dusk: blend source color, end darker
                float t = Mathf.InverseLerp(19f, 22.3f, timeOfDay);
                sourceColor = Color.Lerp(sunColor, moonColor, t);
                desaturateAmount = Mathf.Lerp(0.6f, 0.7f, t);
                brightnessMultiplier = Mathf.Lerp(0.85f, 0.2f, t);
                // Upper clouds hold light longer - brighter at dusk end
                upperBrightness = Mathf.Lerp(1.1f, 0.5f, t);
            }
            else
            {
                // Night
                sourceColor = moonColor;
                desaturateAmount = 0.7f;
                brightnessMultiplier = 0.25f;
                upperBrightness = 0.35f;  // Slightly brighter than lower clouds at night
            }

            // Desaturate: lerp toward white
            Color desaturated = Color.Lerp(sourceColor, Color.white, desaturateAmount);
            // Then darken
            return desaturated * brightnessMultiplier;
        }

        private static void UpdateCloudMaterial(float density, float upperDensity, Color sunColor, Color moonColor,Vector3 sunDir, Vector3 moonDir, float sunIntensity, float moonIntensity, Color cloudColor, float upperBrightness)
        {
            if (lowMaterial == null)
                return;
            lowMaterial.SetFloat("_Density", density);
            lowMaterial.SetFloat("_UpperDensity", upperDensity);
            lowMaterial.SetColor("_SunColor", sunColor);
            lowMaterial.SetColor("_MoonColor", moonColor);
            lowMaterial.SetVector("_SunDirection", sunDir);
            lowMaterial.SetVector("_MoonDirection", moonDir);
            lowMaterial.SetFloat("_SunIntensity", sunIntensity);
            lowMaterial.SetFloat("_MoonIntensity", moonIntensity);
            lowMaterial.SetColor("_CloudColor", cloudColor);
            lowMaterial.SetFloat("_UpperBrightness", upperBrightness);
        }

        private static void CalculateLightingParameters(float timeOfDay, out Color sunColor, out Color moonColor, out Vector3 sunDir, out Vector3 moonDir, out float sunIntensity, out float moonIntensity)
        {
            var todSky = MonoBehaviourSingleton<TOD_Sky>.Instance;
            Color rawSunColor = todSky.SunSkyColor;
            Color rawMoonColor = todSky.MoonLightColor;
            sunDir = todSky.LocalSunDirection;
            moonDir = todSky.LocalMoonDirection;

            // Desaturate sun color more at dawn/dusk when it gets too intense
            float sunDesaturate;
            if (timeOfDay >= 4.3f && timeOfDay <= 7.0f)
            {
                // Early dawn: desaturate more at the start, ease off
                float t = Mathf.InverseLerp(4.3f, 7.0f, timeOfDay);
                sunDesaturate = Mathf.Lerp(0.20f, 0.1f, t);
            }
            else if (timeOfDay > 17.0f && timeOfDay <= 19.8f)
            {
                // Dusk: ramp up desaturation
                float t = Mathf.InverseLerp(17.0f, 19.8f, timeOfDay);
                sunDesaturate = Mathf.Lerp(0.1f, 0.20f, t);
            }
            else
            {
                // Midday: subtle desaturation
                sunDesaturate = 0.1f;
            }

            sunColor = Color.Lerp(rawSunColor, Color.white, sunDesaturate);

            // Slightly desaturate moon color
            moonColor = Color.Lerp(rawMoonColor, Color.white, 0.10f);

            if (timeOfDay >= 4.3f && timeOfDay <= 6.5f)
            {
                // Dawn: moon fading out, sun fading in
                float t = Mathf.InverseLerp(4.3f, 6.5f, timeOfDay);
                sunIntensity = t;
                moonIntensity = 1.0f - t;
            }
            else if (timeOfDay > 6.5f && timeOfDay <= 18.0f)
            {
                // Day
                sunIntensity = 1.0f;
                moonIntensity = 0.0f;
            }
            else if (timeOfDay > 18.0f && timeOfDay <= 19.8f)
            {
                // Dusk: sun fading out, moon fading in
                float t = Mathf.InverseLerp(18.0f, 19.8f, timeOfDay);
                sunIntensity = 1.0f - t;
                moonIntensity = t;
            }
            else
            {
                // Night
                sunIntensity = 0.0f;
                moonIntensity = 1.0f;
            }
        }

    }

}