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

        // The render dimensions the jitter magnitude was sized against (eyeTextureWidth * scale). Exposed so
        // the FSR bridge can compare against the ACTUAL render-target width — a mismatch = wrong jitter
        // magnitude = static sub-pixel edge misalignment. Diagnostic.
        public static int LastScaledWidth;
        public static int LastScaledHeight;

        // Jitter the raster the way the game's own upscaler path does on flatscreen: BSG's scene shaders
        // add the _JitterOffsets global (NDC units) to clip position in the vertex stage — the camera and
        // stereo projection matrices are never touched. CRITICAL missing piece found after the first test:
        // the shaders only consume _JitterOffsets in their "TAA" SHADER-KEYWORD variant, which the game's
        // PP layer (destroyed in VR) brackets around every camera render (EnableKeyword in OnPreCull,
        // DisableKeyword in OnPostRender). Without the keyword the global is a dead uniform — raster
        // unjittered under a nonzero feed = the whole-image wobble the first test showed. This mode now
        // sets the keyword bracket per eye too. false = old matrix-jitter paths.
        public static bool useGameJitterModel = true;

        // OLD PATH (useGameJitterModel=false): per-eye stereo-matrix jitter replicating PPv2's
        // ConfigureStereoJitteredProjectionMatrices; false = the even older mono-projection path.
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

        // The game applies its jitter state in the PP layer's OnPreCull — BEFORE the depth-texture/
        // motion-vector prepasses. Writing only in OnPreRender risks those passes rasterizing with LAST
        // frame's _JitterOffsets (depth jittered j[t-1] vs color j[t]) — a per-frame depth<->color
        // misalignment that destabilizes FSR's disocclusion checks exactly at depth edges =
        // sign-independent edge crawl. So: write at OnPreCull like the game, re-write per eye in
        // OnPreRender (same value; protects against something stomping the global mid-frame).
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
                // Per-eye path (replicates PPv2 ConfigureStereoJitteredProjectionMatrices). Re-copy the
                // device proj each frame so GetStereoNonJitteredProjectionMatrix returns the fresh XR matrix,
                // not last frame's jittered one — no restore needed.
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
            // The variant gate (see header comment). STICKY while the mode is active (disabled in
            // ClearJitterGlobal): a per-render bracket that failed to re-arm for one eye/pass would
            // leave it unjittered = sign-independent wobble. Nothing else in VR needs the keyword off
            // (UI shaders don't consume _JitterOffsets; the optic render jitters like flatscreen).
            Shader.EnableKeyword("TAA");
            Shader.EnableKeyword("TAA_HIGH");

            // No matrix is jittered in this mode, so non-jittered == live; keeps NonJitteredProj
            // consumers (SSAAPropagator optic redraw) fresh instead of stale.
            _camera.nonJitteredProjectionMatrix = _camera.projectionMatrix;

            // Game units (GetJitterPerspectiveOffsetsUV): pixels -> NDC on the actual rect-scaled viewport.
            int viewWidth = _camera.pixelWidth;
            int viewHeight = _camera.pixelHeight;
            if (!_loggedViewport)
            {
                // pixelWidth must be the render viewport (eyeTex*scale) — if it tracks the mirror
                // window instead, the applied jitter magnitude is wrong and edges wobble sign-independently.
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

        // Mirrors RuntimeUtilities.GenerateJitteredProjectionMatrixFromOriginal: shift the frustum planes by
        // the jitter scaled to the frustum extent (= a sub-pixel image shift on that eye's true frustum).
        // Both axes positive, matching the game's own convention and the mono path, so the FSR jitter feed
        // (and CurrentJitter) stays identical.
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

        // Use FSR's exact documented jitter sequence length, ceil(8*n^2) (18 @1.5x, 23 @1.7x, 32 @2x),
        // instead of rounding up to a power of two (which fed 32 at Quality/Balanced). AMD tunes FSR/FSR4
        // for this exact length; a longer sequence over-spreads the sub-pixel samples and worsens
        // specular shimmer. Live-tunable for A/B — flip it, then reselect the FSR quality to re-apply.
        public static bool useExactPhaseCount = true;

        public static void SetSampleCountForScale(float scale, bool isDLSS)
        {
            if (isDLSS)
            {
                // DLSS AI is optimized for 8 or 16.
                // 16 is better for the "DLAA" (1.0x) mode you've added.
                CurrentSampleCount = scale >= 0.99f ? 16 : 8;
            }
            else
            {
                // FSR needs more samples as resolution drops to maintain stability.
                float scalingFactor = 1.0f / scale;
                int recommended = Mathf.CeilToInt(8f * (scalingFactor * scalingFactor));

                if (useExactPhaseCount)
                    CurrentSampleCount = Mathf.Max(8, recommended);   // FSR's exact per-ratio length
                else if (recommended <= 8) CurrentSampleCount = 8;    // old power-of-2 stepping
                else if (recommended <= 16) CurrentSampleCount = 16;
                else CurrentSampleCount = 32;
            }
        }

        // 1-based like the game's TAA (GetUnityHaltonSequence uses (index & 0x3FF) + 1). The old 0-based
        // version hit Halton(0) = the (-0.5,-0.5) pixel CORNER once per cycle — the worst-case jitter
        // sample — which the game deliberately skips. Live-tunable for A/B.
        public static bool haltonOneBased = true;

        // Scales the WHOLE jitter — both what's rendered (VRJitterComponent applies CurrentJitter) and what's
        // fed to FSR (the bridge reads CurrentJitter), so they always stay matched. This is the real VR
        // stability<->sharpness dial: smaller = crisper but more aliased, larger = more stable/softer. The
        // game's own TAA uses 0.75. (Distinct from the FSR bridge's "Jitter Scale", which only touches the
        // FED side and therefore DESYNCS the moment it's != 1.) Live-tunable; 1.0 = current behavior.
        public static float jitterSpread = 0.75f;

        public static Vector2 GetJitterPixelSpace()
        {
            // Use Time.frameCount to ensure both eyes get the exact same jitter index
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
