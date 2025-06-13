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

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class Rendering
    {
        private static Dictionary<string, Queue<RenderTexture>> _renderTargetPool = new Dictionary<string, Queue<RenderTexture>>();
        private static readonly int MAX_POOLED_TARGETS = 6;
        private static Material _fxaaMat;

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
            __result = __instance.GetInputHeight();
            return false;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "GetOutputWidth")]
        private static bool ReturnVROutputWidth(SSAAImpl __instance, ref int __result)
        {
            __result = __instance.GetInputWidth();
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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAPropagator), "OnRenderImage")]
        private static bool ProcessImageRendering(SSAAPropagator __instance, RenderTexture source, RenderTexture destination)
        {
            if (__instance._postProcessLayer != null)
            {
                Graphics.Blit(source, destination);
                return false;
            }

            int width = VRGlobals.VRCam.pixelWidth;
            int height = VRGlobals.VRCam.pixelHeight;
            VRGlobals.VRCam.useOcclusionCulling = false;

            ResetRenderingState(__instance);

            InitializeOptimizedHDRRenderTargets(__instance, width, height);
            InitializeOptimizedLDRRenderTargets(__instance, width, height);

            __instance.m_ssaa.RenderImage(source, __instance._resampledColorTargetHDR[0], true, null);

            if (__instance._cmdBuf == null)
            {
                __instance._cmdBuf = new CommandBuffer { name = "SSAAPropagator" };
            }
            __instance._cmdBuf.Clear();

            if (!__instance._thermalVisionIsOn && HasOpticalRenderers(__instance))
            {
                InitializeOptimizedDepthRenderTarget(__instance, width, height);
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
                        rt.filterMode = FilterMode.Bilinear;
                        rt.wrapMode = TextureWrapMode.Clamp;
                        rt.useMipMap = false;
                        rt.autoGenerateMips = false;
                        rt.anisoLevel = 1;
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
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                anisoLevel = 1
            };

            configure?.Invoke(newRT);
            newRT.Create();
            return newRT;
        }

        private static void ReturnToPool(RenderTexture rt)
        {
            if (rt == null) return;

            string key = $"{rt.width}x{rt.height}_{rt.depth}_{rt.format}";

            if (!_renderTargetPool.ContainsKey(key))
            {
                _renderTargetPool[key] = new Queue<RenderTexture>();
            }

            var pool = _renderTargetPool[key];
            if (pool.Count < MAX_POOLED_TARGETS)
            {
                rt.name = "Pooled_" + key;
                pool.Enqueue(rt);
            }
            else
            {
                // Pool is full, destroy the render texture
                rt.Release();
                RuntimeUtilities.SafeDestroy(rt);
            }
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

        private static void LoadFXAAShader()
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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "RenderImage", new Type[] { typeof(RenderTexture), typeof(RenderTexture), typeof(bool), typeof(CommandBuffer) })]
        private static bool FixSSAAImplRenderImage(SSAAImpl __instance, RenderTexture source, RenderTexture destination, bool flipV, CommandBuffer externalCommandBuffer)
        {
            LoadFXAAShader(); // Make sure FXAA material is loaded
            if (_fxaaMat == null)
            {
                // If FXAA material failed to load, just pass through the image
                Graphics.Blit(source, destination);
                return false;
            }
            // Allocate a temporary render target for the FXAA result
            int width = destination != null ? destination.width : Screen.width;
            int height = destination != null ? destination.height : Screen.height;
            if (VRGlobals.VRCam != null)
            {
                width = VRGlobals.VRCam.pixelWidth;
                height = VRGlobals.VRCam.pixelHeight;
            }
            Graphics.Blit(source, destination, _fxaaMat);
            return false;
        }

        //Attempting to fix DLSS by forcing DLAA. not quite there yet but I think its getting somewhere... Using custom jitter because post processing is disabled for VR
        //Maybe ill try getting PostProcessing working again...
        //-matsix
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "RenderImage", new Type[] { typeof(RenderTexture), typeof(RenderTexture), typeof(bool), typeof(CommandBuffer) })]
        private static bool FixSSAAImplRenderImage(SSAAImpl __instance, RenderTexture source, RenderTexture destination, bool flipV, CommandBuffer externalCommandBuffer)
        {
            __instance.Switch(1.0f); // DLAA mode

            __instance.EnableDLSS = true;

            if (__instance.TryRenderDLSS(source, destination, externalCommandBuffer))
            {
                //Plugin.MyLog.LogInfo("DLAA via native TryRenderDLSS successful.");
                return false;
            }

            Plugin.MyLog.LogWarning("DLSS DLAA failed — falling back to FXAA.");

            // === FXAA Fallback ===
            LoadFXAAShader();
            if (_fxaaMat != null)
            {
                int width = destination != null ? destination.width : Screen.width;
                int height = destination != null ? destination.height : Screen.height;

                if (VRGlobals.VRCam != null)
                {
                    width = VRGlobals.VRCam.pixelWidth;
                    height = VRGlobals.VRCam.pixelHeight;
                }

                Graphics.Blit(source, destination, _fxaaMat);
                Plugin.MyLog.LogWarning("DLSS unavailable — fallback to FXAA.");
            }
            else
            {
                Graphics.Blit(source, destination); // pass-through
                Plugin.MyLog.LogError("FXAA material missing. Pass-through blit.");
            }

            return false; // skip original method
        }
        */

        /*
        private static readonly Vector2[] ImprovedJitterSequence = new Vector2[]
{
            new Vector2(0.125f, -0.375f),
            new Vector2(-0.375f, 0.125f),
            new Vector2(0.375f, 0.125f),
            new Vector2(-0.125f, -0.375f),
            new Vector2(-0.125f, 0.375f),
            new Vector2(0.375f, -0.125f),
            new Vector2(-0.375f, -0.125f),
            new Vector2(0.125f, 0.375f)
        };
        private static int _jitterIndex = 0;
        
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SSAAImpl), "TryRenderDLSS", new Type[] { typeof(RenderTexture), typeof(RenderTexture), typeof(CommandBuffer) })]
        private static bool FixDLAAOnlyRender(SSAAImpl __instance, RenderTexture source, RenderTexture destination, CommandBuffer externalCommandBuffer)
        {
            //Camera fpsCamera = GameObject.Find("FPS Camera")?.GetComponent<Camera>();

            if (!VRGlobals.VRCam)
            {
                return false;
            }

            // DLSS Wrapper initialization
            if (__instance._dlssWrapper == null)
            {
                if (!(__instance._ssaaPropagator != null) ||
                    !(__instance._ssaaPropagator.CopyDLSSResources != null) ||
                    !(__instance._ssaaPropagator.DLSSDebugOutput != null))
                {
                    __instance._failedToInitializeDLSS = true;
                    return false;
                }
                __instance._dlssWrapper = new DLSSWrapper(
                    __instance._ssaaPropagator.CopyDLSSResources,
                    __instance._ssaaPropagator.DLSSDebugOutput
                );
            }
            // Configure DLSS wrapper
            if (__instance._dlssWrapper != null)
            {
                __instance._dlssWrapper.DebugMode = __instance.DLSSDebug;
                __instance._dlssWrapper.DebugDisable = (__instance.DLSSDebugDisable || DLSSWrapper.WantToDebugDLSSViaRenderdoc);
                __instance._dlssWrapper.Quality = __instance._DLSSCurrentQuality;
                __instance._dlssWrapper.JitterOffsets = __instance.DLSSJitter;
                __instance._dlssWrapper.MVScale = __instance.DLSSMVScale;
            }

            // Check DLSS library initialization
            if (!__instance._dlssWrapper.IsDLSSLibraryLoaded())
            {
                DLSSWrapper.InitErrors initErrors = __instance._dlssWrapper.InitializeDLSS();
                __instance._failedToInitializeDLSS = (initErrors > DLSSWrapper.InitErrors.INIT_SUCCESS);
                if (initErrors != DLSSWrapper.InitErrors.INIT_SUCCESS)
                {
                    Plugin.MyLog.LogError($"Failed to initialize DLSS library: {initErrors}");
                    return false;
                }
            }

            // In SinglePassInstanced, we need to handle the combined eye texture
            // This is critical - get combined eye resolution, not just single eye
            //int renderWidth = XRSettings.eyeTextureWidth;
            //int renderHeight = XRSettings.eyeTextureHeight;
            int renderWidth = VRGlobals.VRCam.pixelWidth;
            int renderHeight = VRGlobals.VRCam.pixelHeight;

            // For SinglePassInstanced, we might need to handle the combined texture differently
            // Force DLAA mode (0) by setting input and output resolutions to be the same
            DLSSWrapper.SetCreateDLSSFeatureParameters(renderWidth, renderHeight, renderWidth, renderHeight, 0);

            // Copy depth and motion vectors
            __instance._dlssWrapper.CopyDepthMotion(source, destination, __instance.DepthCopyMode, externalCommandBuffer);
            __instance._dlssWrapper.Sharpness = __instance.DLSSSharpness;

            // Get jitter from our improved sequence
            Vector2 jitter = ImprovedJitterSequence[_jitterIndex++ % ImprovedJitterSequence.Length];

            // Scale jitter appropriately - may need adjustment for SinglePassInstanced
            //jitter *= new Vector2(__instance.DLSSJitterXScale, __instance.DLSSJitterYScale);
            Camera cam = Camera.main;
            Vector2 jitterUV = new Vector2(
                jitter.x / cam.pixelWidth,
                jitter.y / cam.pixelHeight
            );
            Matrix4x4 originalProjection = cam.projectionMatrix;
            Matrix4x4 proj = cam.projectionMatrix;
            proj.m02 += jitterUV.x * 2f;
            proj.m12 += jitterUV.y * 2f;
            cam.projectionMatrix = proj;
            Plugin.MyLog.LogError("Before OnRenderImage");
            //__instance._dlssWrapper.OnRenderImage(source, destination, __instance.SwapDLSSUpDown, jitterUV, externalCommandBuffer);
            //__instance._dlssWrapper.OnRenderImage(source, destination, __instance.SwapDLSSUpDown, jitter, externalCommandBuffer);
            RenderDLSS(__instance._dlssWrapper, source, destination, __instance.SwapDLSSUpDown, jitterUV, externalCommandBuffer);
            cam.projectionMatrix = originalProjection;
            return true;
        }

        public static void RenderDLSS(DLSSWrapper dlssWrapper, RenderTexture src, RenderTexture dest, bool flipOutputUpDown, Vector2 jitterOffset, CommandBuffer externalCommandBuffer)
        {
            // Initialize resources if needed
            dlssWrapper.InitializeResourcesIfNeeded(src, dest);

            // Prepare command buffer
            if (dlssWrapper._cmdBufEvaluate == null)
            {
                dlssWrapper._cmdBufEvaluate = new CommandBuffer();
                dlssWrapper._cmdBufEvaluate.name = "DLSSEvaluate";
            }
            dlssWrapper._cmdBufEvaluate.Clear();
            CommandBuffer commandBuffer = (externalCommandBuffer == null) ? dlssWrapper._cmdBufEvaluate : externalCommandBuffer;

            // Cleanup released handles
            if (dlssWrapper._dlssHandlesToRelease.Count > 0)
            {
                foreach (int item in dlssWrapper._dlssHandlesToRelease)
                {
                    DLSSWrapper.ReleaseHandleInt(item, out var _);
                }
                dlssWrapper._dlssHandlesToRelease.Clear();
            }

            // Setup DLSS if needed
            if (!dlssWrapper.DebugDisable && !DLSSWrapper.WantToDebugDLSSViaRenderdoc)
            {
                if (dlssWrapper._dlssHandle == -1)
                {
                   DLSSWrapper.SetCreateDLSSFeatureParameters(dlssWrapper._featureInWidth, dlssWrapper._featureInHeight, dlssWrapper._featureOutWidth, dlssWrapper._featureOutHeight, dlssWrapper._featureQuality);
                    dlssWrapper._dlssHandle = DLSSWrapper.PrepareHandle();
                }

                // Validate texture sizes
                if (src.width != dlssWrapper._featureInWidth || src.height != dlssWrapper._featureInHeight ||
                    dlssWrapper._motionVectorsCopy.width != dlssWrapper._featureInWidth || dlssWrapper._motionVectorsCopy.height != dlssWrapper._featureInHeight ||
                    dlssWrapper._srcCopyUAV.width != dlssWrapper._featureInWidth || dlssWrapper._srcCopyUAV.height != dlssWrapper._featureInHeight)
                {
                    Plugin.MyLog.LogErrorError("Wrong DLSS SIZE!");
                }

                // Configure DLSS evaluation
                DLSSWrapper.SetDLSSEvaluateParametersExtInt(
                    dlssWrapper._srcCopyUAVPtr,
                    dlssWrapper._textureBufferPtr,
                    dlssWrapper._depthCopyPtr,
                    dlssWrapper._motionVectorsCopyPtr,
                    0,
                    src.width,
                    src.height,
                    dlssWrapper.Sharpness,
                    -dlssWrapper._motionVectorsCopy.width,
                    dlssWrapper._motionVectorsCopy.height,
                    jitterOffset.x,
                    jitterOffset.y,
                    dlssWrapper._dlssHandle
                );

                commandBuffer.IssuePluginEvent(DLSSWrapper.GetDLSSEvaluateFuncExtInt(), dlssWrapper._dlssHandle);
            }

            // Configure shader keywords and rendering
            bool shouldFlipVertically = DLSSWrapper.IsDLSSLoaded();
            if (flipOutputUpDown)
            {
                shouldFlipVertically = !shouldFlipVertically;
            }

            if (shouldFlipVertically)
            {
                commandBuffer.EnableShaderKeyword("UPDOWN");
            }
            else
            {
                commandBuffer.DisableShaderKeyword("UPDOWN");
            }

            // Setup render target
            int width = (dest != null) ? dest.width : Screen.width;
            int height = (dest != null) ? dest.height : Screen.height;
            commandBuffer.SetRenderTarget(dest);
            commandBuffer.SetViewport(new Rect(0f, 0f, width, height));

            // Render final output
            if (!dlssWrapper.DebugMode)
            {
                if (dlssWrapper._propertiesOut == null)
                {
                    dlssWrapper._propertiesOut = new MaterialPropertyBlock();
                }

                if (!dlssWrapper.DebugDisable && !DLSSWrapper.WantToDebugDLSSViaRenderdoc)
                {
                    dlssWrapper._propertiesOut.SetTexture("_MainTex", dlssWrapper._textureBuffer);
                }
                else
                {
                    dlssWrapper._propertiesOut.SetTexture("_MainTex", src);
                }

                commandBuffer.DrawMesh(dlssWrapper._mesh, Matrix4x4.identity, dlssWrapper._matCopySources, 0, 1, dlssWrapper._propertiesOut);
            }
            else
            {
                MaterialPropertyBlock debugProperties = new MaterialPropertyBlock();
                debugProperties.SetTexture("_MainTex", dlssWrapper._textureBuffer);
                debugProperties.SetTexture("_DepthTex", dlssWrapper._depthCopy);
                debugProperties.SetTexture("_MotionVectorsTex", dlssWrapper._motionVectorsCopy);
                debugProperties.SetTexture("_SrcColorTex", dlssWrapper._srcCopyUAV);
                commandBuffer.DrawMesh(dlssWrapper._mesh, Matrix4x4.identity, dlssWrapper._debugMaterial, 0, 0, debugProperties);
            }

            // Execute command buffer if no external one was provided
            if (externalCommandBuffer == null)
            {
                Graphics.ExecuteCommandBuffer(commandBuffer);
            }
        }

        */
        //---------------------------------------------------------------------------------------------------------------------------------------------------------------
        private static float[] TarkovLayers()
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
