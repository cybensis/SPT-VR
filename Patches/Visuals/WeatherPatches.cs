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
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PrismEffects), "method_5")]
        private static bool FixFogForVR(PrismEffects __instance, Material fogMaterial)
        {
            Camera cam = __instance.GetPrismCamera();

            fogMaterial.SetFloat(Shader.PropertyToID("_FogHeight"), __instance.fogHeight);
            fogMaterial.SetFloat(Shader.PropertyToID("_FogIntensity"), 1f);
            fogMaterial.SetFloat(Shader.PropertyToID("_FogDistance"), __instance.fogDistance);
            fogMaterial.SetFloat(Shader.PropertyToID("_FogStart"), __instance.fogStartPoint);
            fogMaterial.SetColor(Shader.PropertyToID("_FogColor"), __instance.fogColor);
            fogMaterial.SetColor(Shader.PropertyToID("_FogEndColor"), __instance.fogEndColor);
            fogMaterial.SetFloat(Shader.PropertyToID("_FogBlurSkybox"), __instance.fogAffectSkybox ? 1f : 0.9999999f);

            // Convert pixel jitter to world-space offset
            Vector2 jitter = VRJitterComponent.CurrentJitter;
            float scale = VRGlobals.upscalingMultiplier;
            int scaledWidth = (int)(XRSettings.eyeTextureWidth * scale);
            int scaledHeight = (int)(XRSettings.eyeTextureHeight * scale);

            float halfHeight = cam.nearClipPlane * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfWidth = halfHeight * cam.aspect;

            float worldJitterX = (jitter.x / scaledWidth) * 2f * halfWidth;
            float worldJitterY = (jitter.y / scaledHeight) * 2f * halfHeight;

            // Apply jitter offset in view space
            Vector3 viewSpaceOffset = new Vector3(worldJitterX, worldJitterY, 0f);
            Vector3 worldSpaceOffset = cam.transform.TransformDirection(viewSpaceOffset);

            // World-locked fog with jitter compensation
            Vector3 jitteredPosition = cam.transform.position + worldSpaceOffset;
            Matrix4x4 worldMatrix = Matrix4x4.TRS(
                cam.transform.position,//jitteredPosition,    // Camera position WITH jitter offset
                Quaternion.identity, // World rotation (doesn't rotate with head)
                Vector3.one
            );

            fogMaterial.SetMatrix(Shader.PropertyToID("_InverseView"), worldMatrix);

            return false;
        }
        */

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
            //fogMaterial.SetFloat("_FogStart", __instance.fogStartPoint);
            fogMaterial.SetColor("_FogColor", __instance.fogColor);
            fogMaterial.SetColor("_FogEndColor", __instance.fogEndColor);
            fogMaterial.SetFloat("_FogBlurSkybox", 0.9999999f);
            //Matrix4x4 nonJitteredMatrix = __instance.GetComponent<Camera>().worldToCameraMatrix.inverse;
            //fogMaterial.SetMatrix("_InverseView", nonJitteredMatrix);
            Matrix4x4 worldMatrix = Matrix4x4.TRS(
                cam.transform.position,//jitteredPosition,    // Camera position WITH jitter offset
                Quaternion.identity, // World rotation (doesn't rotate with head)
                Vector3.one
            );

            fogMaterial.SetMatrix(Shader.PropertyToID("_InverseView"), worldMatrix);

            return false; // Skip the original method
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
            {
                fpsPrism = fpsCam.GetComponent<PrismEffects>() ?? fpsCam.gameObject.AddComponent<PrismEffects>();
            }
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

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PrismEffects), "method_12")]
        private static void ApplyFXAAAfterAllEffects(RenderTexture source, RenderTexture destination)
        {
            Rendering.LoadFXAAShader();
            if (Rendering._fxaaMat == null) return;

            // Apply FXAA to the final rendered result
            RenderTexture temp = RenderTexture.GetTemporary(destination.descriptor);
            Graphics.Blit(destination, temp);
            Graphics.Blit(temp, destination, Rendering._fxaaMat);
            RenderTexture.ReleaseTemporary(temp);
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
                    lowRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                    lowMaterial = lowRenderer.sharedMaterial;
                    if (lowRenderer.material != null)
                    {
                        lowRenderer.material.SetInt("_ZWrite", 0); // No depth write
                        Plugin.MyLog.LogInfo("[Clouds] Disabled depth write for low clouds");
                    }
                }
            }

            if (highCloud != null)
            {
                highCloud.gameObject.layer = 28;

                highRenderer = highCloud.GetComponent<Renderer>();
                if (highRenderer != null)
                {
                    highRenderer.allowOcclusionWhenDynamic = false;
                    highRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                    highMaterial = highRenderer.sharedMaterial;
                    if (highRenderer.material != null)
                    {
                        highRenderer.material.SetInt("_ZWrite", 0); // No depth write
                        Plugin.MyLog.LogInfo("[Clouds] Disabled depth write for high clouds");
                    }
                }
            }
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeatherController), "method_9")]
        private static void DynamicClouds(WeatherController __instance, float fog, GStruct275 interpolatedParams)
        {
            if (VRGlobals.cloudPrefab == null)
                return;

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