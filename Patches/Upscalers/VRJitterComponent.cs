using UnityEngine.XR;
using UnityEngine;

namespace TarkovVR.Patches.Upscalers
{
    public class VRJitterComponent : MonoBehaviour
    {
        private Camera _camera;
        private SSAAImpl _ssaaImpl;
        private Matrix4x4 _originalProj;
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
        // false = the matrix-jitter paths.
        public static bool useGameJitterModel = true;

        // useGameJitterModel = false: per-eye stereo-matrix jitter replicating PPv2's
        // ConfigureStereoJitteredProjectionMatrices; false = the mono-projection path.
        public static bool gameStyleStereoJitter = true;

        private static readonly int JITTER_OFFSETS_ID = Shader.PropertyToID("_JitterOffsets");
        private static bool _wroteJitterGlobal;
        private static bool _loggedViewport;

        private bool _didMonoJitter;

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
            if (!useGameJitterModel || _ssaaImpl == null || !(_ssaaImpl.EnableFSR3 || _ssaaImpl.EnableDLSS))
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

            if (useGameJitterModel)
            {
                ApplyGameJitterGlobals();
                return;
            }
            ClearJitterGlobal();   // matrix paths must not run on top of a stale global

            float scale = VRGlobals.upscalingMultiplier;
            int scaledWidth = (int)(XRSettings.eyeTextureWidth * scale);
            int scaledHeight = (int)(XRSettings.eyeTextureHeight * scale);
            LastScaledWidth = scaledWidth;
            LastScaledHeight = scaledHeight;

            var activeEye = _camera.stereoActiveEye;
            if (gameStyleStereoJitter && activeEye != Camera.MonoOrStereoscopicEye.Mono)
            {
                // Per-eye path (replicates PPv2 ConfigureStereoJitteredProjectionMatrices). Re-copying the device proj
                // each frame makes GetStereoNonJitteredProjectionMatrix return the fresh XR matrix; no restore needed.
                var eye = (Camera.StereoscopicEye)activeEye;
                _camera.CopyStereoDeviceProjectionMatrixToNonJittered(eye);
                Matrix4x4 clean = _camera.GetStereoNonJitteredProjectionMatrix(eye);
                _camera.SetStereoProjectionMatrix(eye, JitterFrustum(clean, CurrentJitter, scaledWidth, scaledHeight));
                return;
            }

            // Mono path (flatscreen / fallback): jitter the mono projection, keep a non-jittered copy.
            _originalProj = _camera.projectionMatrix;
            _camera.nonJitteredProjectionMatrix = _camera.projectionMatrix;
            Matrix4x4 projMatrix = _camera.projectionMatrix;
            projMatrix.m02 += CurrentJitter.x * 2f / scaledWidth;
            projMatrix.m12 += CurrentJitter.y * 2f / scaledHeight;
            _camera.projectionMatrix = projMatrix;
            _didMonoJitter = true;
        }

        private void OnPostRender()
        {
            // Only the mono path stashes/needs a restore; the stereo path re-copies the device proj each frame.
            if (_didMonoJitter)
            {
                _camera.projectionMatrix = _originalProj;
                _didMonoJitter = false;
            }
        }

        private void ApplyGameJitterGlobals()
        {
            // The variant gate (see useGameJitterModel). Kept on while the mode is active (off in ClearJitterGlobal): a
            // per-render bracket that failed to re-arm for one eye/pass would leave it unjittered. Nothing else in VR needs
            // the keyword off (UI shaders don't read _JitterOffsets; the optic render jitters like flatscreen).
            Shader.EnableKeyword("TAA");
            Shader.EnableKeyword("TAA_HIGH");

            // No matrix is jittered in this mode, so non-jittered == live; keeps NonJitteredProj consumers (SSAAPropagator
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

        // Mirrors RuntimeUtilities.GenerateJitteredProjectionMatrixFromOriginal: shifts the frustum planes by the jitter
        // scaled to the frustum extent. Both axes positive, matching the game's convention and the mono path, so the FSR
        // jitter feed (CurrentJitter) stays identical.
        private static Matrix4x4 JitterFrustum(Matrix4x4 orig, Vector2 jitter, int screenW, int screenH)
        {
            FrustumPlanes frustum = orig.decomposeProjection;
            float w = Mathf.Abs(frustum.left) + Mathf.Abs(frustum.right);
            float height = Mathf.Abs(frustum.top) + Mathf.Abs(frustum.bottom);
            frustum.left   += jitter.x * w / screenW;
            frustum.right  += jitter.x * w / screenW;
            frustum.top    += jitter.y * height / screenH;
            frustum.bottom += jitter.y * height / screenH;
            return Matrix4x4.Frustum(frustum);
        }

    }

    public static class VRJitterHelper
    {
        public static int CurrentSampleCount { get; private set; } = 8;

        // FSR's documented jitter sequence length, ceil(8*n^2) (18 @1.5x, 23 @1.7x, 32 @2x), instead of rounding up to a
        // power of two: a longer sequence over-spreads the samples and worsens specular shimmer. Reselect the FSR quality
        // to re-apply after flipping it.
        public static bool useExactPhaseCount = true;

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

                if (useExactPhaseCount)
                    CurrentSampleCount = Mathf.Max(8, recommended);   // FSR's exact per-ratio length
                else if (recommended <= 8) CurrentSampleCount = 8;    // power-of-2 stepping
                else if (recommended <= 16) CurrentSampleCount = 16;
                else CurrentSampleCount = 32;
            }
        }

        // 1-based like the game's TAA (GetUnityHaltonSequence uses (index & 0x3FF) + 1): 0-based hits Halton(0) = the
        // (-0.5,-0.5) pixel corner once per cycle, the worst-case sample.
        public static bool haltonOneBased = true;

        // Scales the whole jitter, both rendered and fed to FSR (both read CurrentJitter), so they stay matched. The
        // stability vs sharpness dial: smaller = crisper but more aliased, larger = more stable/softer. The game's TAA
        // uses 0.75. The FSR bridge's "Jitter Scale" only touches the fed side and desyncs when != 1.
        public static float jitterSpread = 0.75f;

        public static Vector2 GetJitterPixelSpace()
        {
            // Time.frameCount: both eyes get the same jitter index
            int index = Time.frameCount % CurrentSampleCount;
            return GetHaltonValue(haltonOneBased ? index + 1 : index) * jitterSpread;
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
