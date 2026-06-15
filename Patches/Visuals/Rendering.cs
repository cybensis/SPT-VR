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
            // Re-applied every frame: the camera is reconfigured across raids, so a one-time set
            // silently reverts (the usual reason a layerCullDistances attempt "does nothing"). Cheap:
            // a cached array is reused, so there's no per-frame GC. See ApplyCullDistances below.
            ApplyCullDistances(VRGlobals.VRCam);

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
            var format = RenderTextureFormat.ARGBHalf;

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
                bool flag = __instance.EnableDLSS && !__instance._failedToInitializeDLSS && !__instance.NeedToApplySwitch();
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
        // PER-LAYER CULL DISTANCES (CPU win)
        // -----------------------------------------------------------------------------------------
        // Engine-level distance culling on the VR render camera. In MultiPass everything is culled
        // AND draw-submitted once PER EYE, so dropping renderers from the set saves CPU twice over.
        // Only RENDER layers matter here — collider/trigger/unused layers are no-ops for rendering,
        // so we leave them at 0. A value of 0 means "no extra cull, use the camera far clip".
        //
        // STRUCTURAL layers (Default world geometry, Terrain, Water, Player, PlayerRenderers, Sky,
        // LevelBorder) are deliberately left at 0 so buildings/terrain/other players never pop in/out.
        // The win comes from the high-COUNT clutter layers (loot, foliage, ragdolls, casings, rain).
        // If you want to push it further, lower cullDistanceMultiplier, or give Default a finite
        // distance (e.g. 1500) to trim very distant world geometry — test for building pop-in first.
        //
        // Tunables are live (UnityExplorer): flip useLayerCullDistances / change cullDistanceMultiplier
        // mid-raid and the next frame rebuilds + re-applies. Watch the BepInEx log for "[CullDist]".
        public static bool useLayerCullDistances = true;   // master toggle
        public static float cullDistanceMultiplier = 1f;   // scales the whole table; <1 = more aggressive

        private static float[] _cullDistances;             // cached scaled array (rebuilt on multiplier change)
        private static float[] _zeroDistances;             // all-0 = far clip everywhere (restore on disable)
        private static float _builtMultiplier = float.NaN;
        private static bool _cullApplied = false;

        // Base per-layer cull radius in meters at multiplier 1.0. 0 = no extra cull (far clip).
        // Layer names match Tarkov's layer indices.
        private static float[] CullDistanceBase()
        {
            float[] d = new float[32];
            //  d[0]  Default            -> 0 (world geometry/buildings; keep, would pop)
            //  d[4]  Water              -> 0 (keep; culling reflective water looks bad)
            //  d[8]  Player             -> 0 (never cull players)
            //  d[11] Terrain            -> 0 (keep)
            //  d[17] PlayerRenderers    -> 0 (never cull player meshes)
            //  d[28] Sky / d[29] Border -> 0 (keep)
            d[15] = 120f;  // Loot
            d[20] = 30f;   // Shells (ejected casings)
            d[22] = 150f;  // Interactive
            d[23] = 100f;  // Deadbody / ragdolls
            d[24] = 30f;   // RainDrops
            d[26] = 120f;  // Foliage (bushes/plants that are real renderers)
            d[31] = 60f;   // Grass (mostly GPU-Instancer-driven; harmless if it's a no-op)
            return d;
        }

        // Applies the cull-distance table to a camera. Safe to call every frame — the array is cached
        // and only rebuilt when the multiplier changes, so there's no allocation on the hot path.
        public static void ApplyCullDistances(Camera cam)
        {
            if (cam == null)
                return;

            if (!useLayerCullDistances)
            {
                // Restore far-clip-everywhere once, so toggling off mid-raid actually reverts.
                if (_cullApplied)
                {
                    if (_zeroDistances == null)
                        _zeroDistances = new float[32];
                    cam.layerCullDistances = _zeroDistances;
                    _cullApplied = false;
                    Plugin.MyLog.LogInfo("[CullDist] Disabled — restored far-clip on all layers.");
                }
                return;
            }

            if (_cullDistances == null || _builtMultiplier != cullDistanceMultiplier)
            {
                float m = Mathf.Max(0.1f, cullDistanceMultiplier);
                float[] baseT = CullDistanceBase();
                _cullDistances = new float[32];
                for (int i = 0; i < 32; i++)
                    _cullDistances[i] = baseT[i] > 0f ? baseT[i] * m : 0f;
                _builtMultiplier = cullDistanceMultiplier;
                _cullApplied = false; // force a re-apply + log with the new table
                Plugin.MyLog.LogInfo($"[CullDist] Built layer cull table (x{m:0.00}).");
            }

            cam.layerCullSpherical = true;   // distances are a radius from the headset, not planar depth
            cam.layerCullDistances = _cullDistances;
            if (!_cullApplied)
            {
                _cullApplied = true;
                Plugin.MyLog.LogInfo($"[CullDist] Applied to '{cam.name}' (spherical, x{_builtMultiplier:0.00}).");
            }
        }
    }
}
