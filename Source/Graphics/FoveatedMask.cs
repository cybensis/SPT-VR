using System;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace TarkovVR.Source.Graphics
{
    // Foveated rendering in software, for D3D11 (no variable-rate shading there; AMD has no extension for it): outside an inner circle
    // of each eye, every other 2x2 pixel block (a checkerboard - the GPU shades in 2x2 quads) is filled from its neighbours instead of
    // being shaded. Shader Hidden/SPTVR/FoveatedMask, bundle sptvr/Assets/foveated (built in the Unity project).
    //  - BeforeGBuffer: near-plane depth in those blocks, so the G-buffer draws fail the depth test there before shading.
    //  - Nothing draws there, so the stencil's _StencilNonBackground bit stays clear and Tarkov's light pass
    //    (Hidden/Internal-DeferredShadingEFT, stencil Equal on that bit) skips them as well.
    //  - BeforeLighting: Unity copies the depth texture only after the AfterGBuffer event, and its built-in Depth targets can't be read
    //    there, so the fill runs here: depth into SPT-VR's own copy, then GBuffer0-2 + depth for the masked blocks. The depth texture
    //    global points at the filled copy for the rest of the eye's render (fog, AO, SSR, FSR read it).
    //  - AfterLighting: the lit image filled from the neighbours (before the skybox, forward and transparent passes).
    // Config section "Foveated Masking" (BepInEx: F12, and SixBench can A/B "Enabled").
    public static class FoveatedMask
    {
        private static ConfigEntry<bool> enabledSetting;
        private static ConfigEntry<float> innerRadiusSetting;
        private static ConfigEntry<bool> showHolesSetting;
        private static ConfigEntry<bool> tintSetting;
        private static ConfigEntry<bool> alternateSetting;
        // Fog / AO / SSR / FSR read the depth texture: point it at the filled copy. false = they read the masked blocks' near depth.
        public static bool redirectDepthTexture = true;

        private const string ShaderName = "Hidden/SPTVR/FoveatedMask";
        private const int PassMask = 0, PassDepthFill = 1, PassGBufferFill = 2, PassColorFill = 3;
        private static readonly int FoveaId = Shader.PropertyToID("_SptvrFovea");
        private static readonly int FoveaSizeId = Shader.PropertyToID("_SptvrFoveaSize");
        private static readonly int FoveaTintId = Shader.PropertyToID("_SptvrFoveaTint");
        private static readonly int FoveaDepthId = Shader.PropertyToID("_SptvrFoveaDepth");
        private static readonly int Gb0Id = Shader.PropertyToID("_SptvrFoveaGb0");
        private static readonly int Gb1Id = Shader.PropertyToID("_SptvrFoveaGb1");
        private static readonly int Gb2Id = Shader.PropertyToID("_SptvrFoveaGb2");
        private static readonly int ColorId = Shader.PropertyToID("_SptvrFoveaColor");
        private static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");

        private static Material material;
        private static bool loadTried;
        private static Camera attachedCamera;
        private static CommandBuffer maskBuffer, fillBuffer, colorBuffer;
        private static RenderTexture depthCopy;
        private static bool builtShowHoles, builtRedirect;
        private static bool loggedAttach, loggedRedirectCheck;
        // Off for this session after an error (never written to the config: the setting stays the user's)
        private static bool faulted;

        public static void Init(ConfigFile config)
        {
            enabledSetting = config.Bind("Foveated Masking", "Enabled", true,
                "Shade every other 2x2 pixel block outside the middle of each eye from its neighbours instead (saves GPU time, softer edges).");
            innerRadiusSetting = config.Bind("Foveated Masking", "Inner Radius", 0.55f,
                new ConfigDescription("Where the masking starts, from the middle of each eye (0 = everywhere, 1 = the edge of the view straight left / right / up / down).",
                    new AcceptableValueRange<float>(0.1f, 1.5f)));
            alternateSetting = config.Bind("Foveated Masking", "Alternate Pattern", false,
                "Swap which blocks are masked every frame (the upscaler's history then sees all of them).");
            showHolesSetting = config.Bind("Foveated Masking", "Show Holes", false,
                "Debug: skip the final fill, so the masked blocks show as the checkerboard Tarkov didn't light.");
            tintSetting = config.Bind("Foveated Masking", "Tint Filled Pixels", false,
                "Debug: tint the filled blocks red, to see where the masking reaches.");
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Every camera, before it renders (once per eye in multi-pass): keep the buffers on the headset camera, then this eye's centre
        private static void OnPreRender(Camera camera)
        {
            try
            {
                Camera headset = VRGlobals.VRCam;
                if (camera == null || camera != headset)
                    return;
                bool wanted = !faulted && enabledSetting != null && enabledSetting.Value && XRSettings.enabled
                    && camera.actualRenderingPath == RenderingPath.DeferredShading && EnsureMaterial();
                int width = camera.pixelWidth, height = camera.pixelHeight;
                if (!wanted || width <= 0 || height <= 0)
                {
                    Detach();
                    return;
                }
                bool sizeChanged = EnsureDepthCopy(width, height);
                if (attachedCamera != camera || sizeChanged || builtShowHoles != showHolesSetting.Value || builtRedirect != redirectDepthTexture)
                    Attach(camera);

                Camera.StereoscopicEye eye = camera.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
                // The GPU projection (flipped for a render texture on D3D) maps the view's forward axis to (-m02, -m12) in its NDC,
                // the same NDC the shader rebuilds from each pixel's position
                Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(camera.GetStereoProjectionMatrix(eye), true);
                float parity = alternateSetting.Value ? (Time.frameCount & 1) : 0f;
                Shader.SetGlobalVector(FoveaId, new Vector4(-gpuProjection.m02, -gpuProjection.m12, innerRadiusSetting.Value, parity));
                Shader.SetGlobalVector(FoveaSizeId, new Vector4(width, height, 0f, 0f));
                Shader.SetGlobalVector(FoveaTintId, tintSetting.Value ? new Vector4(1.6f, 0.35f, 0.35f, 1f) : Vector4.one);

                if (!loggedAttach)
                {
                    loggedAttach = true;
                    Plugin.MyLog.LogInfo($"[Foveated] on '{camera.name}': {width}x{height} an eye, masking past {innerRadiusSetting.Value:0.00} from each eye's centre"
                                         + $" ({eye} centre {-gpuProjection.m02:0.000}, {-gpuProjection.m12:0.000})");
                }
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[Foveated] off for this session after an error: {exception}");
                faulted = true;
                Detach();
            }
        }

        // Once: whether the depth texture still points at the filled copy when the eye has rendered (Unity could rebind it later)
        private static void OnPostRender(Camera camera)
        {
            if (loggedRedirectCheck || camera == null || camera != attachedCamera || !redirectDepthTexture)
                return;
            loggedRedirectCheck = true;
            Texture bound = Shader.GetGlobalTexture(CameraDepthId);
            Plugin.MyLog.LogInfo(bound == depthCopy
                ? "[Foveated] the depth texture is the filled copy to the end of the eye's render"
                : $"[Foveated] the depth texture was rebound after the fill (now '{(bound != null ? bound.name : "none")}'): effects after that read the masked blocks' depth");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void Attach(Camera camera)
        {
            Detach();
            maskBuffer = new CommandBuffer { name = "SPT-VR foveated mask" };
            maskBuffer.DrawProcedural(Matrix4x4.identity, material, PassMask, MeshTopology.Triangles, 3);

            fillBuffer = new CommandBuffer { name = "SPT-VR foveated fill (depth + G-buffer)" };
            fillBuffer.GetTemporaryRT(Gb0Id, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            fillBuffer.GetTemporaryRT(Gb1Id, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            fillBuffer.GetTemporaryRT(Gb2Id, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            fillBuffer.Blit(BuiltinRenderTextureType.GBuffer0, Gb0Id);
            fillBuffer.Blit(BuiltinRenderTextureType.GBuffer1, Gb1Id);
            fillBuffer.Blit(BuiltinRenderTextureType.GBuffer2, Gb2Id);
            fillBuffer.SetRenderTarget(depthCopy);
            fillBuffer.DrawProcedural(Matrix4x4.identity, material, PassDepthFill, MeshTopology.Triangles, 3);
            fillBuffer.SetGlobalTexture(FoveaDepthId, depthCopy);
            fillBuffer.SetRenderTarget(new RenderTargetIdentifier[] { BuiltinRenderTextureType.GBuffer0, BuiltinRenderTextureType.GBuffer1, BuiltinRenderTextureType.GBuffer2 },
                BuiltinRenderTextureType.CameraTarget);
            fillBuffer.DrawProcedural(Matrix4x4.identity, material, PassGBufferFill, MeshTopology.Triangles, 3);
            if (redirectDepthTexture)
                fillBuffer.SetGlobalTexture(CameraDepthId, depthCopy);
            fillBuffer.ReleaseTemporaryRT(Gb0Id);
            fillBuffer.ReleaseTemporaryRT(Gb1Id);
            fillBuffer.ReleaseTemporaryRT(Gb2Id);

            camera.AddCommandBuffer(CameraEvent.BeforeGBuffer, maskBuffer);
            camera.AddCommandBuffer(CameraEvent.BeforeLighting, fillBuffer);
            if (!showHolesSetting.Value)
            {
                colorBuffer = new CommandBuffer { name = "SPT-VR foveated fill (lit image)" };
                colorBuffer.GetTemporaryRT(ColorId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
                colorBuffer.Blit(BuiltinRenderTextureType.CameraTarget, ColorId);
                colorBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                colorBuffer.DrawProcedural(Matrix4x4.identity, material, PassColorFill, MeshTopology.Triangles, 3);
                colorBuffer.ReleaseTemporaryRT(ColorId);
                camera.AddCommandBuffer(CameraEvent.AfterLighting, colorBuffer);
            }
            attachedCamera = camera;
            builtShowHoles = showHolesSetting.Value;
            builtRedirect = redirectDepthTexture;
        }

        private static void Detach()
        {
            if (attachedCamera != null)
            {
                if (maskBuffer != null) attachedCamera.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, maskBuffer);
                if (fillBuffer != null) attachedCamera.RemoveCommandBuffer(CameraEvent.BeforeLighting, fillBuffer);
                if (colorBuffer != null) attachedCamera.RemoveCommandBuffer(CameraEvent.AfterLighting, colorBuffer);
            }
            maskBuffer?.Release();
            fillBuffer?.Release();
            colorBuffer?.Release();
            maskBuffer = fillBuffer = colorBuffer = null;
            attachedCamera = null;
        }

        // The filled depth, one for both eyes (multi-pass renders them one after the other). true = (re)made: the buffers hold the old one
        private static bool EnsureDepthCopy(int width, int height)
        {
            if (depthCopy != null && depthCopy.width == width && depthCopy.height == height && depthCopy.IsCreated())
                return false;
            if (depthCopy != null)
            {
                depthCopy.Release();
                UnityEngine.Object.Destroy(depthCopy);
            }
            depthCopy = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            { name = "SPT-VR foveated depth", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            depthCopy.Create();
            return true;
        }

        private static bool EnsureMaterial()
        {
            if (material != null)
                return true;
            if (loadTried)
                return false;
            loadTried = true;
            try
            {
                string path = Path.Combine(BepInEx.Paths.PluginPath, "sptvr", "Assets", "foveated");
                if (!File.Exists(path))
                {
                    Plugin.MyLog.LogWarning("[Foveated] sptvr/Assets/foveated missing - foveated masking off");
                    return false;
                }
                AssetBundle bundle = AssetBundle.LoadFromFile(path);
                Shader shader = bundle != null ? bundle.LoadAsset<Shader>("SPTVRFoveatedMask") : null;
                if (shader == null || !shader.isSupported || shader.name != ShaderName)
                {
                    Plugin.MyLog.LogWarning($"[Foveated] the foveated bundle has no usable {ShaderName} ({(shader != null ? shader.name : "none")}) - foveated masking off");
                    bundle?.Unload(true);
                    return false;
                }
                material = new Material(shader) { hideFlags = HideFlags.DontSave };
                bundle.Unload(false);
                return true;
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogWarning("[Foveated] the foveated bundle failed to load: " + exception.Message);
                return false;
            }
        }
    }
}
