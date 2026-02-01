using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.Rendering;
using UnityEngine;
using System.IO;
using UnityEngine.XR;
using EFT.Visual;
using static SSAAImpl;
using static DLSSWrapper;
using UnityEngine.Experimental.Rendering;
using EFT.Settings.Graphics;
using Comfort.Common;
using EFT.UI.Settings;
using EFT.UI;
using Valve.VR;
using System.Reflection;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class Rendering
    {
        private static Dictionary<string, Queue<RenderTexture>> _renderTargetPool = new Dictionary<string, Queue<RenderTexture>>();
        private static readonly int MAX_POOLED_TARGETS = 6;
        public static Material _fxaaMat;

        //NOTEEEEEEEEEEEE: Removing the SSAApropagator (i think) from the PostProcessingLayer/Volume will restore some visual fidelity but still not as good as no ssaa

        //ANOTHER NOTE: I'm pretty sure if you delete or disable the SSAA shit you still get all the nice visual effects from the post processing without the blur,
        // its just the night vision doessn't work, so maybe only enable SSAA when enabling night/thermal vision

        // FIGURED IT OUT Delete the SSAAPropagator, SSAA, and SSAAImpl and it just works

        // Also remove the distant shadows command buffer from the camera
        // MotionVectorsPASS is whats causing the annoying [Error  : Unity Log] Dimensions of color surface does not match dimensions of depth surface    error to occur 
        // but its also needed for grass and maybe other stuff

        // SSAA causes a bunch of issues like thermal/nightvision rendering all fucky, and the
        // s also render in 
        // with 2 other lenses on either side of the main lense, Although SSAA is nice for fixing the jagged edges, it 
        // also adds a strong layer of blur over everything so it's definitely best to keep it disabled. Might look into
        // keeping it around later on if I can figure a way to get it to look nice without messing with everything else

        // In hideout, don't notice any real fps difference when changing object LOD quality and overall visibility

        // anti aliasing is off or on FXAA - no FPS difference noticed - seems like scopes won't work without it
        // Resampling x1 OFF 
        // DLSS and FSR OFF
        // HBAO - Looks better but takes a massive hit on performance - off gets about around 10-20 fps increase
        // SSR - turning low to off raises FPS by about 2-5, turning ultra to off raises fps by about 5ish. I don't know if it looks better but it seems like if you have it on, you may as well go to ultra
        // Anistrophic filtering - per texture or on maybe cos it looks bettter, or just off - No real FPS difference
        // Sharpness at 1-1.5 I think it the gain falls off after around 1.5+
        // Uncheck all boxes on bottom - CHROMATIC ABBERATIONS probably causing scope issues so always have it off
        // Uncheck all boxes on bottom - CHROMATIC ABBERATIONS probably causing scope issues so always have it off
        // POST FX - Turning it off gains about 8-10 FPS

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // These two functions would return the screen resolution setting and would result in the game
        // being very blurry
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "GetOutputHeight")]
        private static bool ReturnVROutputHeight(SSAAImpl __instance, ref int __result)
        {
            __result = XRSettings.eyeTextureHeight;
            return false;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "GetOutputWidth")]
        private static bool ReturnVROutputWidth(SSAAImpl __instance, ref int __result)
        {
            __result = XRSettings.eyeTextureWidth;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAA), "GetOutputHeight")]
        private static bool SSAAVROutputHeight(SSAAImpl __instance, ref int __result)
        {
            __result = XRSettings.eyeTextureHeight;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAA), "GetOutputWidth")]
        private static bool SSAAVROutputWidth(SSAAImpl __instance, ref int __result)
        {
            __result = XRSettings.eyeTextureWidth;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "ApplySwitch")]
        private static bool SSAAVROutputWidth(SSAAImpl __instance)
        {
            //__instance._currentCamera.rect = new Rect(0f, 0f, 1f, 1f);
            __instance._cmdBufAfterForwardAlpha.Clear();
            __instance._cmdBufAfterForwardOpaque.Clear();
            __instance._currentCamera.GetCommandBuffers(CameraEvent.AfterDepthNormalsTexture);
            if (__instance._currentRT != null)
            {
                __instance._currentCamera.targetTexture = null;
                __instance._currentRT.Release();
                UnityEngine.Object.DestroyImmediate(__instance._currentRT);
                __instance._currentRT = null;
            }
            if (__instance._currentSSRatio == 1f)
            {
                __instance._currentCamera.targetTexture = null;
                __instance.CurrentState = SSState.NOSCALE;
            }
            else
            {
                if (__instance._currentSSRatio < 1f)
                {
                    //__instance._currentCamera.rect = new Rect(0f, 0f, __instance._currentSSRatio, __instance._currentSSRatio);
                    __instance.CurrentState = SSState.UPSCALE;
                }
                if (__instance._dlssWrapper != null)
                {
                    __instance._dlssWrapper.Quality = __instance._DLSSCurrentQuality;
                }
            }
            //__instance.RenderTexturesAreChanged?.Invoke();
            return false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SSAA), "Awake")]
        private static void DisableSSAA(SSAA __instance)
        {
            __instance.FlippedV = true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static int scaledWidth;
        private static int scaledHeight;
        public static bool IsInjectingFog = false;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAPropagator), "OnRenderImage")]
        private static bool ProcessImageRendering(SSAAPropagator __instance, RenderTexture source, RenderTexture destination)
        {

            if (__instance._postProcessLayer != null)
            {
                Graphics.Blit(source, destination);
                return false;
            }

            int nativeWidth = XRSettings.eyeTextureWidth;
            int nativeHeight = XRSettings.eyeTextureHeight;
            
            float resolutionScale = VRGlobals.upscalingMultiplier;
            if (resolutionScale > 1.0f)
            {
                scaledWidth = (int)(nativeWidth * resolutionScale);
                scaledHeight = (int)(nativeHeight * resolutionScale);
            } 
            else
            {
                scaledWidth = nativeWidth;
                scaledHeight = nativeHeight;
            }

            VRGlobals.VRCam.useOcclusionCulling = false;

            ResetRenderingState(__instance);
            InitializeOptimizedHDRRenderTargets(__instance, scaledWidth, scaledHeight);
            InitializeOptimizedLDRRenderTargets(__instance, scaledWidth, scaledHeight);

            __instance.m_ssaa.RenderImage(source, __instance._resampledColorTargetHDR[0], true, null);

            if (__instance._cmdBuf == null)
            {
                __instance._cmdBuf = new CommandBuffer { name = "SSAAPropagator" };
            }

            __instance._cmdBuf.Clear();

            if (!__instance._thermalVisionIsOn && HasOpticalRenderers(__instance))
            {
                InitializeOptimizedDepthRenderTarget(__instance, scaledWidth, scaledHeight);
                RenderOpticalEffects(__instance);
            }

            ApplyVisionEffects(__instance);
            Graphics.ExecuteCommandBuffer(__instance._cmdBuf);            
            return false;
        }

        private static void ResetRenderingState(SSAAPropagator __instance)
        {
            __instance._currentDestinationHDR = 0;
            __instance._currentDestinationLDR = 0;
            __instance._HDRSourceDestination = true;
        }

        private static void InitializeOptimizedHDRRenderTargets(SSAAPropagator __instance, int width, int height)
        {
            var format = RenderTextureFormat.RGB111110Float;

            bool needsUpdate = __instance._resampledColorTargetHDR[0] == null ||
                              __instance._resampledColorTargetHDR[0].width != width ||
                              __instance._resampledColorTargetHDR[0].height != height ||
                              __instance._resampledColorTargetHDR[0].format != format;

            if (!needsUpdate) return;

            // Return old targets to pool before creating new ones
            for (int i = 0; i < 2; i++)
            {
                if (__instance._resampledColorTargetHDR[i] != null)
                {
                    ReturnToPool(__instance._resampledColorTargetHDR[i]);
                    __instance._resampledColorTargetHDR[i] = null;
                }
            }

            // Get new targets from pool or create if needed
            for (int i = 0; i < 2; i++)
            {
                __instance._resampledColorTargetHDR[i] = GetFromPoolOrCreate(
                    width, height, 0, format, $"SSAAPropagator{i}HDR",
                    rt => {
                        rt.enableRandomWrite = true;
                        rt.filterMode = VRGlobals.upscalingMultiplier > 1.0f ? FilterMode.Point : FilterMode.Bilinear;
                        rt.wrapMode = TextureWrapMode.Clamp;
                        rt.useMipMap = false;
                        rt.autoGenerateMips = false;
                        rt.anisoLevel = 0;
                    }
                );
            }
        }

        private static void InitializeOptimizedLDRRenderTargets(SSAAPropagator __instance, int width, int height)
        {
            bool needsUpdate = __instance._resampledColorTargetLDR[0] == null ||
                              __instance._resampledColorTargetLDR[0].width != width ||
                              __instance._resampledColorTargetLDR[0].height != height;

            if (!needsUpdate) return;

            // Return old targets to pool
            for (int i = 0; i < 3; i++)
            {
                if (__instance._resampledColorTargetLDR[i] != null)
                {
                    ReturnToPool(__instance._resampledColorTargetLDR[i]);
                    __instance._resampledColorTargetLDR[i] = null;
                }
            }

            var format = RenderTextureFormat.ARGB32;

            __instance._resampledColorTargetLDR[0] = GetFromPoolOrCreate(
                width, height, 0, format, "SSAAPropagator0LDR",
                rt => rt.filterMode = FilterMode.Bilinear
            );

            __instance._resampledColorTargetLDR[1] = GetFromPoolOrCreate(
                width, height, 0, format, "SSAAPropagator1LDR",
                rt => rt.filterMode = FilterMode.Bilinear
            );

            __instance._resampledColorTargetLDR[2] = GetFromPoolOrCreate(
                width, height, 0, RenderTextureFormat.R8, "Stub",
                rt => rt.filterMode = FilterMode.Point
            );
        }

        private static bool HasOpticalRenderers(SSAAPropagator __instance)
        {
            return __instance._opticLensRenderer != null || __instance._collimatorRenderer != null;
        }

        private static void InitializeOptimizedDepthRenderTarget(SSAAPropagator __instance, int width, int height)
        {
            bool needsUpdate = __instance._resampledDepthTarget == null ||
                              __instance._resampledDepthTarget.width != width ||
                              __instance._resampledDepthTarget.height != height;

            if (!needsUpdate) return;

            if (__instance._resampledDepthTarget != null)
            {
                ReturnToPool(__instance._resampledDepthTarget);
                __instance._resampledDepthTarget = null;
            }

            __instance._resampledDepthTarget = GetFromPoolOrCreate(
                width, height, 24, RenderTextureFormat.Depth, "SSAAPropagatorDepth",
                rt => rt.filterMode = FilterMode.Point
            );
        }

        private static RenderTexture GetFromPoolOrCreate(int width, int height, int depth, RenderTextureFormat format, string name, System.Action<RenderTexture> configure = null)
        {
            string key = $"{width}x{height}_{depth}_{format}";

            if (_renderTargetPool.TryGetValue(key, out var pool) && pool.Count > 0)
            {
                var rt = pool.Dequeue();
                if (rt != null && rt.IsCreated())
                {
                    rt.name = name;
                    return rt;
                }
            }
            var newRT = new RenderTexture(width, height, depth, format)
            {
                name = name,
                filterMode = VRGlobals.upscalingMultiplier > 1.0f ? FilterMode.Point : FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                anisoLevel = 0
            };

            configure?.Invoke(newRT);
            newRT.Create();
            return newRT;
        }
        private static void ReturnToPool(RenderTexture rt)
        {
            if (rt == null) return;

            rt.Release();
            RuntimeUtilities.SafeDestroy(rt);
        }
        public static void ClearRenderTargetPool()
        {
            foreach (var pool in _renderTargetPool.Values)
            {
                while (pool.Count > 0)
                {
                    var rt = pool.Dequeue();
                    if (rt != null)
                    {
                        rt.Release();
                        RuntimeUtilities.SafeDestroy(rt);
                    }
                }
            }
            _renderTargetPool.Clear();
        }

        private static void RenderOpticalEffects(SSAAPropagator __instance)
        {
            var cmd = __instance._cmdBuf;
            cmd.BeginSample("OutputOptic");

            cmd.EnableShaderKeyword(SSAAPropagator.KWRD_TAA);
            cmd.EnableShaderKeyword(SSAAPropagator.KWRD_NON_JITTERED);

            cmd.SetGlobalMatrix(
                SSAAPropagator.ID_NONJITTEREDPROJ,
                GL.GetGPUProjectionMatrix(__instance._camera.nonJitteredProjectionMatrix, true)
            );

            cmd.SetRenderTarget(__instance._resampledColorTargetHDR[0], __instance._resampledDepthTarget);
            cmd.ClearRenderTarget(true, false, Color.black);

            if (__instance._opticLensRenderer == null && __instance._collimatorRenderer != null)
            {
                cmd.DrawRenderer(__instance._collimatorRenderer, __instance._collimatorMaterial);
            }

            if (__instance._opticLensRenderer != null)
            {
                RenderSightComponents(__instance);
                cmd.SetRenderTarget(__instance._resampledColorTargetHDR[0], __instance._resampledDepthTarget);
                cmd.DrawRenderer(__instance._opticLensRenderer, __instance._opticLensMaterial);
            }

            cmd.DisableShaderKeyword(SSAAPropagator.KWRD_NON_JITTERED);
            cmd.DisableShaderKeyword(SSAAPropagator.KWRD_TAA);
            cmd.EndSample("OutputOptic");
        }

        private static void RenderSightComponents(SSAAPropagator __instance)
        {
            if (__instance._sightNonLensRenderers == null || __instance._sightNonLensRenderers.Length == 0) return;

            var cmd = __instance._cmdBuf;
            cmd.BeginSample("DEPTH_PREPASS");
            cmd.SetRenderTarget(__instance._resampledColorTargetLDR[2], __instance._resampledDepthTarget);

            // Batch renderer validation
            var validSightRenderers = new List<(Renderer renderer, Material material)>(__instance._sightNonLensRenderers.Length);
            var validWeaponRenderers = new List<(Renderer renderer, Material material)>(__instance._otherWeaponRenderers.Length);

            // Pre-validate all renderers
            for (int i = 0; i < __instance._sightNonLensRenderers.Length; i++)
            {
                if (IsRendererValid(__instance._sightNonLensRenderers[i], __instance._sightNonLensRenderersMaterials[i]))
                {
                    validSightRenderers.Add((__instance._sightNonLensRenderers[i], __instance._sightNonLensRenderersMaterials[i]));
                }
            }

            for (int i = 0; i < __instance._otherWeaponRenderers.Length; i++)
            {
                if (IsRendererValid(__instance._otherWeaponRenderers[i], __instance._otherWeaponRenderersMaterials[i]))
                {
                    validWeaponRenderers.Add((__instance._otherWeaponRenderers[i], __instance._otherWeaponRenderersMaterials[i]));
                }
            }

            // Render sight components
            if (validSightRenderers.Count > 0)
            {
                cmd.BeginSample("SIGHT_DEPTH");
                foreach (var (renderer, material) in validSightRenderers)
                {
                    cmd.DrawRenderer(renderer, material);
                }
                cmd.EndSample("SIGHT_DEPTH");
            }

            // Render weapon components
            if (validWeaponRenderers.Count > 0)
            {
                cmd.BeginSample("WEAPON_DEPTH");
                foreach (var (renderer, material) in validWeaponRenderers)
                {
                    cmd.DrawRenderer(renderer, material);
                }
                cmd.EndSample("WEAPON_DEPTH");
            }

            cmd.EndSample("DEPTH_PREPASS");
        }

        private static bool IsRendererValid(Renderer renderer, Material material)
        {
            return renderer != null && material != null && renderer.gameObject.activeSelf;
        }

        private static void ApplyVisionEffects(SSAAPropagator __instance)
        {
            if (__instance._nightVisionMaterial)
            {
                __instance._cmdBuf.EnableShaderKeyword(SSAAPropagator.KWRD_NIGHTVISION_NOISE);
                __instance._cmdBuf.Blit(
                    __instance._resampledColorTargetHDR[0],
                    __instance._resampledColorTargetHDR[1],
                    __instance._nightVisionMaterial
                );
                __instance._cmdBuf.DisableShaderKeyword(SSAAPropagator.KWRD_NIGHTVISION_NOISE);
                __instance._currentDestinationHDR = 1;
            }
            else if (__instance._thermalVisionIsOn && __instance._thermalVisionMaterial != null)
            {
                __instance._cmdBuf.Blit(
                    __instance._resampledColorTargetHDR[0],
                    __instance._resampledColorTargetHDR[1],
                    __instance._thermalVisionMaterial,
                    1
                );
                __instance._currentDestinationHDR = 1;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public static void LoadFXAAShader()
        {
            if (_fxaaMat != null) return;

            try
            {
                string bundlePath = Path.Combine(BepInEx.Paths.PluginPath, "sptvr", "Assets", "fxaa");
                AssetBundle fxaaBundle = AssetBundle.LoadFromFile(bundlePath);
                if (fxaaBundle == null)
                {
                    Plugin.MyLog.LogError("Failed to load FXAA AssetBundle.");
                    return;
                }

                _fxaaMat = fxaaBundle.LoadAsset<Material>("fxaaMat");

                if (_fxaaMat == null)
                {
                    Plugin.MyLog.LogError("FXAA Material not found in bundle.");
                    return;
                }

                Plugin.MyLog.LogInfo("FXAA Material loaded successfully.");

                fxaaBundle.Unload(false); // Shader remains in memory
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"Error loading FXAA shader: {ex.Message}");
            }
        }

        private static void ApplyFXAA(RenderTexture source, RenderTexture destination)
        {
            LoadFXAAShader();
            if (_fxaaMat == null)
            {
                Graphics.Blit(source, destination);
                return;
            }

            int width = destination != null ? destination.width : Screen.width;
            int height = destination != null ? destination.height : Screen.height;

            if (VRGlobals.VRCam != null)
            {
                width = VRGlobals.VRCam.pixelWidth;
                height = VRGlobals.VRCam.pixelHeight;
            }
            Graphics.Blit(source, destination, _fxaaMat);
        }       

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "RenderImage", new Type[] { typeof(RenderTexture), typeof(RenderTexture), typeof(bool), typeof(CommandBuffer) })]
        private static bool FixSSAAImplRenderImage(SSAAImpl __instance, RenderTexture source, RenderTexture destination, bool flipV, CommandBuffer externalCommandBuffer)
        {
            if (source == null || destination == null)
            {
                Graphics.Blit(source, destination);
                return false;
            }

            if (__instance.CurrentState == SSState.UPSCALE)
            {
                bool flag = ((__instance.EnableDLSS && !__instance._failedToInitializeDLSS && !__instance.NeedToApplySwitch()) || (__instance.EnableDLSS && (__instance.DLSSDebugDisable || DLSSWrapper.WantToDebugDLSSViaRenderdoc))) && !__instance.InventoryBlurIsEnabled;
                bool flag2 = __instance.EnableFSR && !__instance._failedToInitializeFSR && !flag && !__instance.NeedToApplySwitch();
                bool flag3 = __instance.EnableFSR2 && !__instance._failedToInitializeFSR2 && !flag && !__instance.NeedToApplySwitch();
                bool flag4 = __instance.EnableFSR3 && !__instance._failedToInitializeFSR3 && !flag && !__instance.NeedToApplySwitch();
                if ((flag && __instance.TryRenderDLSS(source, destination, externalCommandBuffer)) || (flag2 && __instance.TryRenderFSR(source, destination, externalCommandBuffer)) || (flag3 && __instance.TryRenderFSR2(source, destination, externalCommandBuffer)) || (flag4 && __instance.TryRenderFSR3(source, destination, externalCommandBuffer)))
                {
                    return false;
                }
            }

            EAntialiasingMode currentAA = Singleton<SharedGameSettingsClass>.Instance.Graphics.Settings.AntiAliasing;

            if (currentAA == EAntialiasingMode.FXAA && __instance.CurrentState != SSState.UPSCALE)
                ApplyFXAA(source, destination);
            else
                Graphics.Blit(source, destination);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAA), "LateUpdate")]
        private static bool SampleCountSet(SSAA __instance)
        {
            TemporalAntialiasing.JitterSamplesGeneratorRepeatCount = __instance.UnityTAAJitterSamplesRepeatCount;
            if (__instance._impl != null && (__instance._impl.EnableDLSS || __instance._impl.EnableFSR2 || __instance._impl.EnableFSR3))
            {
                if (XRSettings.eyeTextureResolutionScale == 1)
                    TemporalAntialiasing.k_SampleCount = 16;
                else
                    TemporalAntialiasing.k_SampleCount = (int)((double)__instance.BasePhaseSamplesCount * (1.0 / (double)(__instance._currentSSRatio * __instance._currentSSRatio)) + 0.5);
            }
            else
            {
                TemporalAntialiasing.k_SampleCount = 8;
            }
            RuntimeUtilities.UseProj = __instance.UseProjectionMatrix;
            return false;

        }

        //---------------------------------------------------------------------------------------------------------------------------------------------------------------
        public static float[] TarkovLayers()
        {
            float[] distances = new float[32];

            distances[0] = 200f;   // Default
            distances[1] = 150f;   // TransparentFX
            distances[2] = 50f;    // Ignore Raycast
            distances[3] = 0f;     // <unused>
            distances[4] = 300f;   // Water
            distances[5] = 5f;     // UI
            distances[6] = 0f;     // <unused>
            distances[7] = 0f;     // <unused>
            distances[8] = 400f;   // Player
            distances[9] = 100f;   // DoorLowPolyCollider
            distances[10] = 150f;  // PlayerCollisionTest
            distances[11] = 500f;  // Terrain
            distances[12] = 200f;  // HighPolyCollider
            distances[13] = 100f;  // Triggers
            distances[14] = 300f;  // DisablerCullingObject
            distances[15] = 150f;  // Loot
            distances[16] = 300f;  // HitCollider
            distances[17] = 400f;  // PlayerRenderers
            distances[18] = 150f;  // LowPolyCollider
            distances[19] = 10f;   // Weapon Preview
            distances[20] = 50f;   // Shells
            distances[21] = 250f;  // CullingMask
            distances[22] = 100f;  // Interactive
            distances[23] = 200f;  // Deadbody
            distances[24] = 30f;   // RainDrops
            distances[25] = 100f;  // Menu Environment
            distances[26] = 150f;  // Foliage
            distances[27] = 50f;   // PlayerSpiritAura
            distances[28] = 1000f; // Sky
            distances[29] = 800f;  // LevelBorder
            distances[30] = 100f;  // TransparentCollider
            distances[31] = 100f;  // Grass

            return distances;
        }
    }
}
