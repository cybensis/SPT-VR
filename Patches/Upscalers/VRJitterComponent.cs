using UnityEngine.XR;
using UnityEngine;

namespace TarkovVR.Patches.Upscalers
{
    public class VRJitterComponent : MonoBehaviour
    {
        private Camera _camera;
        private SSAAImpl _ssaaImpl;
        public static Vector2 CurrentJitter { get; private set; }

        // The render dimensions the jitter magnitude was sized against (eyeTextureWidth * scale), so the FSR bridge can
        // compare against the actual render-target width (a mismatch = wrong jitter magnitude = sub-pixel edge
        // misalignment). Diagnostic.
        public static int LastScaledWidth;
        public static int LastScaledHeight;

        // Jitter the raster like the game's flatscreen upscaler path: BSG's scene shaders add the _JitterOffsets global
        // (NDC units) to clip position in the vertex stage; the projection matrices are untouched. The shaders only read
        // _JitterOffsets in their "TAA" keyword variant, which the game's PP layer (destroyed in VR) enables around every
        // camera render; without the keyword the raster stays unjittered under a nonzero FSR feed (whole-image wobble).
        private static readonly int JITTER_OFFSETS_ID = Shader.PropertyToID("_JitterOffsets");
        private static bool _wroteJitterGlobal;
        private static bool _loggedViewport;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _ssaaImpl = GetComponent<SSAAImpl>();
        }

        // Written at OnPreCull like the game's PP layer, before the depth-texture/motion-vector prepasses: written only in
        // OnPreRender, those passes would raster with last frame's _JitterOffsets (depth vs color misaligned = FSR edge
        // crawl). Re-written per eye in OnPreRender (same value) in case something stomps the global mid-frame.
        private void OnPreCull()
        {
            if (_ssaaImpl == null || !(_ssaaImpl.EnableFSR3 || _ssaaImpl.EnableDLSS))
                return;
            CurrentJitter = VRJitterHelper.GetJitterPixelSpace();
            ApplyGameJitterGlobals();
        }

        private void OnPreRender()
        {
            if (_ssaaImpl == null || !(_ssaaImpl.EnableFSR3 || _ssaaImpl.EnableDLSS))
            {
                ClearJitterGlobal();
                return;
            }

            // Same jitter for both eyes (Time.frameCount-indexed); fed to FSR per-eye unchanged.
            CurrentJitter = VRJitterHelper.GetJitterPixelSpace();

            ApplyGameJitterGlobals();
        }

        private void ApplyGameJitterGlobals()
        {
            // The shaders' variant gate. Kept on while the upscaler runs (off in ClearJitterGlobal): a
            // per-render bracket that failed to re-arm for one eye/pass would leave it unjittered. Nothing else in VR needs
            // the keyword off (UI shaders don't read _JitterOffsets; the optic render jitters like flatscreen).
            Shader.EnableKeyword("TAA");
            Shader.EnableKeyword("TAA_HIGH");

            // No matrix is jittered, so non-jittered == live; keeps NonJitteredProj consumers (SSAAPropagator
            // optic redraw) fresh.
            _camera.nonJitteredProjectionMatrix = _camera.projectionMatrix;

            // Game units (GetJitterPerspectiveOffsetsUV): pixels -> NDC on the actual rect-scaled viewport.
            int viewWidth = _camera.pixelWidth;
            int viewHeight = _camera.pixelHeight;
            if (!_loggedViewport)
            {
                // pixelWidth must be the render viewport (eyeTex*scale); if it tracks the mirror window, the jitter
                // magnitude is wrong.
                _loggedViewport = true;
                int eyeWidth = (int)(XRSettings.eyeTextureWidth * VRGlobals.upscalingMultiplier);
                int eyeHeight = (int)(XRSettings.eyeTextureHeight * VRGlobals.upscalingMultiplier);
                if (Mathf.Abs(viewWidth - eyeWidth) > 2 || Mathf.Abs(viewHeight - eyeHeight) > 2)
                    Plugin.MyLog.LogWarning($"[VRJitter] game-jitter viewport {viewWidth}x{viewHeight} vs eye texture x scale {eyeWidth}x{eyeHeight}: jitter magnitude wrong");
            }
            LastScaledWidth = viewWidth;
            LastScaledHeight = viewHeight;
            Shader.SetGlobalVector(JITTER_OFFSETS_ID,
                new Vector4(CurrentJitter.x / (0.5f * viewWidth), CurrentJitter.y / (0.5f * viewHeight), 0f, 0f));
            _wroteJitterGlobal = true;
        }

        private void OnDisable()
        {
            ClearJitterGlobal();
        }

        private static void ClearJitterGlobal()
        {
            if (_wroteJitterGlobal)
            {
                Shader.SetGlobalVector(JITTER_OFFSETS_ID, Vector4.zero);
                Shader.DisableKeyword("TAA");
                Shader.DisableKeyword("TAA_HIGH");
                _wroteJitterGlobal = false;
            }
        }

    }

    public static class VRJitterHelper
    {
        public static int CurrentSampleCount { get; private set; } = 8;

        // FSR's documented jitter sequence length, ceil(8*n^2) (18 @1.5x, 23 @1.7x, 32 @2x), instead of rounding up to a
        // power of two: a longer sequence over-spreads the samples and worsens specular shimmer

        public static void SetSampleCountForScale(float scale, bool isDLSS)
        {
            if (isDLSS)
            {
                // DLSS is tuned for 8 or 16; 16 for the DLAA (1.0x) mode.
                CurrentSampleCount = scale >= 0.99f ? 16 : 8;
            }
            else
            {
                // FSR needs more samples as resolution drops to maintain stability.
                float scalingFactor = 1.0f / scale;
                int recommended = Mathf.CeilToInt(8f * (scalingFactor * scalingFactor));

                CurrentSampleCount = Mathf.Max(8, recommended);   // FSR's exact per-ratio length
            }
        }

        // 1-based like the game's TAA (GetUnityHaltonSequence uses (index & 0x3FF) + 1): 0-based hits Halton(0) = the
        // (-0.5,-0.5) pixel corner once per cycle, the worst-case sample.

        // Scales the whole jitter, both rendered and fed to FSR (both read CurrentJitter), so they stay matched. The
        // stability vs sharpness dial: smaller = crisper but more aliased, larger = more stable/softer. The game's TAA
        // uses 0.75. The FSR bridge's "Jitter Scale" only touches the fed side and desyncs when != 1.
        public static float jitterSpread = 0.75f;

        public static Vector2 GetJitterPixelSpace()
        {
            // Time.frameCount: both eyes get the same jitter index
            int index = Time.frameCount % CurrentSampleCount;
            return GetHaltonValue(index + 1) * jitterSpread;
        }
        private static Vector2 GetHaltonValue(int index)
        {

            return new Vector2(
                HaltonSequence(index, 2) - 0.5f,
                HaltonSequence(index, 3) - 0.5f
            );
        }

        private static float HaltonSequence(int index, int radix)
        {
            float result = 0f;
            float fraction = 1f / radix;
            while (index > 0)
            {
                result += index % radix * fraction;
                index /= radix;
                fraction /= radix;
            }
            return result;
        }
    }
}
