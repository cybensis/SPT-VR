using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using TarkovVR.Source.Player.VRManager;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace TarkovVR.Source.Graphics
{
    // Foveated rendering in software for D3D11, which has no variable-rate shading (AMD has no extension for it). Outside an inner
    // circle of each eye, every other 2x2 pixel block (a checkerboard, because the GPU shades in 2x2 quads) is filled from its
    // neighbours instead of being shaded. Shader Hidden/SPTVR/FoveatedMask, bundle sptvr/Assets/foveated (built in the Unity project).
    //  - BeforeGBuffer: near-plane depth is written in those blocks, so the G-buffer draws fail the depth test there before shading.
    //  - Nothing draws there, so the stencil's _StencilNonBackground bit stays clear and Tarkov's light pass
    //    (Hidden/Internal-DeferredShadingEFT, stencil Equal on that bit) skips them as well.
    //  - BeforeLighting: Unity copies the depth texture only after the AfterGBuffer event, and its built-in Depth targets can't be read
    //    there, so the fill runs here: depth into SPT-VR's own copy, then GBuffer0-2 and depth for the masked blocks. The depth texture
    //    global then points at the filled copy for the rest of the eye's render (fog, AO, SSR and FSR read it).
    //  - AfterLighting: the lit image is filled from the neighbours (before the skybox, forward and transparent passes).
    //  - The circle's centre is the middle of each lens, or where you look on a headset with eye tracking (OpenXR eye gaze).
    // Config section "Foveated Masking" (BepInEx: F12; SixBench can A/B "Enabled").
    public static class FoveatedMask
    {
        private static ConfigEntry<bool> enabledSetting;
        private static ConfigEntry<float> innerRadiusSetting;
        private static ConfigEntry<bool> eyeTrackingSetting;
        private static ConfigEntry<float> gazeInnerRadiusSetting;
        private static ConfigEntry<bool> showHolesSetting;
        private static ConfigEntry<bool> tintSetting;
        private static ConfigEntry<bool> alternateSetting;
        // Fog / AO / SSR / FSR read the depth texture: point it at the filled copy. false = they read the masked blocks' near depth.
        public static bool redirectDepthTexture = true;
        // With a temporal upscaler on (FSR's jitter running), the masked blocks swap every frame, so the upscaler's history gets a real
        // sample at every pixel and the checkerboard averages out. Without an upscaler swapping would flicker (Alternate Pattern forces it).
        public static bool alternateWithUpscaler = true;
        // NDC width past the inner radius over which the masked blocks fade in gradually (a dither) instead of at a hard ring. 0 = hard.
        public static float bandWidth = 0.15f;
        // true = a masked pixel is filled from the nearest rendered pixels along both axes, weighted by distance, with the axis across
        // a depth edge counting less. false = the average of the two pixels 2 px away along one axis.
        public static bool interpolatedFill = true;

        private const string ShaderName = "Hidden/SPTVR/FoveatedMask";
        private const int PassMask = 0, PassDepthFill = 1, PassGBufferFill = 2, PassColorFill = 3;
        private static readonly int FoveaId = Shader.PropertyToID("_SptvrFovea");
        private static readonly int FoveaSizeId = Shader.PropertyToID("_SptvrFoveaSize");
        private static readonly int FoveaTintId = Shader.PropertyToID("_SptvrFoveaTint");
        private static readonly int FoveaBandId = Shader.PropertyToID("_SptvrFoveaBand");
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
        private static bool loggedAttach, loggedRedirectCheck, loggedAlternate;
        private static int lastJitterFrame = -1000;
        // Set after an error: masking stays off for this session. Never written to the config, so the user's setting is kept.
        private static bool faulted;

        // The VR menu's toggle and slider (Graphics)
        public static bool IsEnabled => enabledSetting != null && enabledSetting.Value;
        public static void SetEnabled(bool on)
        {
            if (enabledSetting != null)
                enabledSetting.Value = on;
        }
        public static float InnerRadius
        {
            get => innerRadiusSetting != null ? innerRadiusSetting.Value : 0.55f;
            set { if (innerRadiusSetting != null) innerRadiusSetting.Value = value; }
        }

        public static void Init(ConfigFile config)
        {
            // Shown in the VR menu (VRSettings, Graphics), not in F12. They are still BepInEx entries because SixBench A/Bs "Enabled"
            // through them.
            enabledSetting = config.Bind("Foveated Masking", "Enabled", true,
                new ConfigDescription("Shade every other 2x2 pixel block outside the middle of each eye from its neighbours instead (saves GPU time, softer edges).",
                    null, new TarkovVR.Source.Settings.ConfigurationManagerAttributes { Browsable = false }));
            innerRadiusSetting = config.Bind("Foveated Masking", "Inner Radius", 0.55f,
                new ConfigDescription("Where the masking starts, from the middle of each eye (0 = everywhere, 1 = the edge of the view straight left / right / up / down).",
                    new AcceptableValueRange<float>(0.1f, 1.5f), new TarkovVR.Source.Settings.ConfigurationManagerAttributes { Browsable = false }));
            eyeTrackingSetting = config.Bind("Foveated Masking", "Eye Tracking", true,
                "On a headset that tracks your eyes (OpenXR eye gaze), the sharp circle follows where you look and uses Gaze Inner Radius. Without eye tracking it stays in the middle of each lens.");
            gazeInnerRadiusSetting = config.Bind("Foveated Masking", "Gaze Inner Radius", 0.3f,
                new ConfigDescription("The sharp circle's size while it follows your eyes (smaller than Inner Radius: it's always where you look).",
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

        // True when SPT-VR's upscaler jitter ran recently (FSR2/3, the FSR4 Bridge), which means a temporal upscaler is accumulating frames.
        private static bool UpscalerJittering()
        {
            if (TarkovVR.Patches.Upscalers.VRJitterComponent.CurrentJitter != Vector2.zero)
                lastJitterFrame = Time.frameCount;
            return Time.frameCount - lastJitterFrame < 30;
        }

        // Runs before every camera renders (once per eye in multi-pass). Keeps the buffers on the headset camera, then sets this eye's
        // circle centre.
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
                // The GPU projection (flipped for a render texture on D3D) maps the view's forward axis to (-m02, -m12) in its NDC.
                // That is the lens centre, in the same NDC the shader rebuilds from each pixel's position.
                Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(camera.GetStereoProjectionMatrix(eye), true);
                UpdateGaze();
                Vector2 centre = new Vector2(-gpuProjection.m02, -gpuProjection.m12);
                if (gazeWeight > 0f && DirectionToNdc(camera, eye, gpuProjection, gazeInHead, out Vector2 gazeCentre))
                    centre = Vector2.Lerp(centre, gazeCentre, gazeWeight);
                float radius = Mathf.Lerp(innerRadiusSetting.Value, gazeInnerRadiusSetting.Value, gazeWeight);
                bool alternate = alternateSetting.Value || (alternateWithUpscaler && UpscalerJittering());
                float parity = alternate ? (Time.frameCount & 1) : 0f;
                if (alternate && !loggedAlternate)
                {
                    loggedAlternate = true;
                    Plugin.MyLog.LogInfo($"[Foveated] the masked blocks swap every frame ({(alternateSetting.Value ? "Alternate Pattern" : "the upscaler's jitter is on")})");
                }
                Shader.SetGlobalVector(FoveaId, new Vector4(centre.x, centre.y, radius, parity));
                Shader.SetGlobalVector(FoveaSizeId, new Vector4(width, height, 0f, 0f));
                Shader.SetGlobalVector(FoveaBandId, new Vector4(Mathf.Max(bandWidth, 0f), interpolatedFill ? 1f : 0f, 0f, 0f));
                Shader.SetGlobalVector(FoveaTintId, tintSetting.Value ? new Vector4(1.6f, 0.35f, 0.35f, 1f) : Vector4.one);

                if (!loggedAttach)
                {
                    loggedAttach = true;
                    string eyeGaze = !TarkovVR.Source.XR.OpenXRBootstrap.Active ? "eye gaze needs OpenXR"
                        : TarkovVR.Source.XR.OpenXRBootstrap.EyeGazeEnabled() ? "the runtime has eye gaze" : "no eye gaze from the runtime (no eye tracking)";
                    Plugin.MyLog.LogInfo($"[Foveated] on '{camera.name}': {width}x{height} an eye, masking past {innerRadiusSetting.Value:0.00} from each lens centre"
                                         + $" ({eye} {-gpuProjection.m02:0.000}, {-gpuProjection.m12:0.000}); {eyeGaze}");
                }
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[Foveated] off for this session after an error: {exception}");
                faulted = true;
                Detach();
            }
        }

        // Logs once whether the depth texture still points at the filled copy after the eye has rendered (Unity could rebind it later).
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

        // Eye tracking. The gaze is read through Unity's XR devices by usage name, so no OpenXR plugin type is used here (the OpenVR
        // fallback runs without that DLL). The device is EyeGazeInteraction's "Eye Tracking OpenXR", registered by OpenXRBootstrap.
        public static float gazeHoldTime = 0.3f;      // s a lost gaze (a blink) keeps its last direction before the lens centre takes over
        public static float gazeBlendTime = 0.25f;    // s to move between the lens centre / Inner Radius and the gaze / Gaze Inner Radius
        private static readonly InputFeatureUsage<Quaternion> GazeRotationUsage = new InputFeatureUsage<Quaternion>("gazeRotation");
        private static readonly List<InputDevice> eyeDevices = new List<InputDevice>();
        private static readonly List<InputFeatureUsage> gazeUsages = new List<InputFeatureUsage>();
        private static InputDevice gazeDevice;
        private static float nextGazeLookup;
        private static int gazeFrame = -1;
        private static Vector3 gazeInHead = Vector3.forward;    // the gaze direction in the headset's frame (+z forward)
        private static float lastGazeTime = -10f;
        private static float gazeWeight;                         // 0 = the lens centre and Inner Radius, 1 = the gaze and Gaze Inner Radius
        private static bool gazeFollowing, loggedGazeDevice;
        private static int gazeLogs;

        // Runs once a frame (both eyes use the same gaze)
        private static void UpdateGaze()
        {
            if (gazeFrame == Time.frameCount)
                return;
            float deltaTime = gazeFrame < 0 ? 0f : Time.unscaledDeltaTime;
            gazeFrame = Time.frameCount;
            if (eyeTrackingSetting.Value && ReadGaze())
                lastGazeTime = Time.unscaledTime;
            bool following = eyeTrackingSetting.Value && Time.unscaledTime - lastGazeTime <= gazeHoldTime;
            gazeWeight = Mathf.MoveTowards(gazeWeight, following ? 1f : 0f, gazeBlendTime > 0f ? deltaTime / gazeBlendTime : 1f);
            if (following != gazeFollowing)
            {
                gazeFollowing = following;
                if (gazeLogs++ < 6)
                    Plugin.MyLog.LogInfo(following
                        ? $"[Foveated] eye gaze tracked: the sharp circle ({gazeInnerRadiusSetting.Value:0.00}) follows it (looking {GazeAngles()})"
                        : $"[Foveated] eye gaze lost for {gazeHoldTime:0.0} s: back to the lens centres ({innerRadiusSetting.Value:0.00})");
            }
        }

        private static bool ReadGaze()
        {
            if (!gazeDevice.isValid)
            {
                if (Time.unscaledTime < nextGazeLookup)
                    return false;
                nextGazeLookup = Time.unscaledTime + 2f;
                InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.EyeTracking, eyeDevices);
                if (eyeDevices.Count == 0)
                    return false;
                gazeDevice = eyeDevices[0];
                if (!loggedGazeDevice)
                {
                    loggedGazeDevice = true;
                    gazeUsages.Clear();
                    gazeDevice.TryGetFeatureUsages(gazeUsages);
                    Plugin.MyLog.LogInfo($"[Foveated] eye tracking device '{gazeDevice.name}' ({gazeDevice.characteristics}): {string.Join(", ", gazeUsages.ConvertAll(usage => usage.name))}");
                }
            }
            if (!gazeDevice.TryGetFeatureValue(GazeRotationUsage, out Quaternion gaze) && !gazeDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out gaze))
                return false;
            if (gazeDevice.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && !tracked)
                return false;
            if (gaze.x == 0f && gaze.y == 0f && gaze.z == 0f && gaze.w == 0f)
                return false;
            // Gaze and headset in the same tracking space (both from Unity's XR devices)
            if (!HeadTracking.RawHmdPose(out _, out Quaternion head))
                return false;
            Vector3 direction = Quaternion.Inverse(head) * gaze * Vector3.forward;
            // Not ahead of the face: a bad sample
            if (direction.z < 0.1f)
                return false;
            gazeInHead = direction.normalized;
            return true;
        }

        private static string GazeAngles()
        {
            float right = Mathf.Atan2(gazeInHead.x, gazeInHead.z) * Mathf.Rad2Deg, up = Mathf.Atan2(gazeInHead.y, gazeInHead.z) * Mathf.Rad2Deg;
            return $"{right:0} deg right, {up:0} deg up";
        }

        // Projects a direction in the headset's frame, taken at infinity, into this eye's GPU NDC. The headset frame is +z forward,
        // Unity's view matrices are -z forward. The eye's own view matrix turns it if that eye's display is canted.
        private static bool DirectionToNdc(Camera camera, Camera.StereoscopicEye eye, Matrix4x4 gpuProjection, Vector3 inHead, out Vector2 ndc)
        {
            Vector3 centreView = new Vector3(inHead.x, inHead.y, -inHead.z);
            Vector3 eyeView = (camera.GetStereoViewMatrix(eye) * camera.worldToCameraMatrix.inverse).MultiplyVector(centreView);
            Vector4 clip = gpuProjection * new Vector4(eyeView.x, eyeView.y, eyeView.z, 0f);
            if (clip.w <= 1e-4f)
            {
                ndc = Vector2.zero;
                return false;
            }
            ndc = new Vector2(Mathf.Clamp(clip.x / clip.w, -1.5f, 1.5f), Mathf.Clamp(clip.y / clip.w, -1.5f, 1.5f));
            return true;
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

        // The filled depth copy, one for both eyes (multi-pass renders them one after the other). Returns true when it was (re)made,
        // because the command buffers still hold the old one.
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
