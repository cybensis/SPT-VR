using UnityEngine.XR;
using UnityEngine;

namespace TarkovVR.Patches.Upscalers
{
    public class VRJitterComponent : MonoBehaviour
    {
        private Camera _camera;
        private SSAAImpl _ssaaImpl;
        public static Vector2 CurrentJitter { get; private set; }

        // The render size the jitter magnitude was computed for (eyeTextureWidth * scale). The FSR bridge compares it with the
        // actual render-target width: a mismatch means a wrong jitter magnitude and sub-pixel edge misalignment. Diagnostic only.
        public static int LastScaledWidth;
        public static int LastScaledHeight;

        // The raster is jittered the way the game's flatscreen upscaler path does it. BSG's scene shaders add the _JitterOffsets
        // global (NDC units) to the clip position in the vertex stage; the projection matrices are untouched. The shaders only read
        // _JitterOffsets in their "TAA" keyword variant, which the game's PP layer (destroyed in VR) enables around every camera
        // render. Without the keyword the raster stays unjittered while FSR is fed a nonzero jitter, and the whole image wobbles.
        private static readonly int JITTER_OFFSETS_ID = Shader.PropertyToID("_JitterOffsets");
        private static bool _wroteJitterGlobal;
        private static bool _loggedViewport;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _ssaaImpl = GetComponent<SSAAImpl>();
        }

        // Written in OnPreCull, like the game's PP layer, so the depth-texture and motion-vector prepasses use this frame's
        // _JitterOffsets. Written only in OnPreRender, they would use last frame's value, and the depth/color mismatch shows as
        // FSR edge crawl. OnPreRender writes it again per eye (same value) in case something overwrites the global mid-frame.
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

            // Both eyes get the same jitter (indexed by Time.frameCount), fed to FSR per eye unchanged.
            CurrentJitter = VRJitterHelper.GetJitterPixelSpace();

            ApplyGameJitterGlobals();
        }

        private void ApplyGameJitterGlobals()
        {
            // The keywords select the shader variant that reads the jitter. They stay on while the upscaler runs (ClearJitterGlobal
            // turns them off): switching them per render could miss one eye or pass and leave it unjittered. Nothing else in VR
            // needs them off: UI shaders don't read _JitterOffsets, and the optic render jitters like flatscreen.
            Shader.EnableKeyword("TAA");
            Shader.EnableKeyword("TAA_HIGH");

            // No matrix is jittered, so the non-jittered projection is the live one. This keeps NonJitteredProj users
            // (the SSAAPropagator optic redraw) current.
            _camera.nonJitteredProjectionMatrix = _camera.projectionMatrix;

            // Pixels to NDC on the actual rect-scaled viewport, in the game's units (GetJitterPerspectiveOffsetsUV).
            int viewWidth = _camera.pixelWidth;
            int viewHeight = _camera.pixelHeight;
            if (!_loggedViewport)
            {
                // pixelWidth must be the render viewport (eye texture * scale). If it follows the mirror window, the jitter
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

        // FSR uses its documented jitter sequence length, ceil(8*n^2) (18 at 1.5x, 24 at 1.7x, 32 at 2x), not one rounded up to a
        // power of two: a longer sequence spreads the samples too far and worsens specular shimmer.

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

        // The Halton index is 1-based like the game's TAA (GetUnityHaltonSequence uses (index & 0x3FF) + 1). A 0-based index
        // hits Halton(0), the (-0.5,-0.5) pixel corner, once per cycle: the worst-case sample.

        // Scales the whole jitter, both the rendered offset and the value fed to FSR (both read CurrentJitter), so they stay
        // matched. Smaller is crisper but more aliased, larger is more stable but softer. The game's TAA uses 0.75.
        // The FSR bridge's "Jitter Scale" only scales the fed side, so the two go out of sync when it isn't 1.
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
