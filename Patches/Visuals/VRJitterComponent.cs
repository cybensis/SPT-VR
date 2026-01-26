using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static SSAAImpl;
using UnityEngine.XR;
using UnityEngine;

namespace TarkovVR.Patches.Visuals
{
    public class VRJitterComponent : MonoBehaviour
    {
        private Camera _camera;
        private SSAAImpl _ssaaImpl;
        private Vector3 originalPosition;
        private Matrix4x4 _originalProj;
        public static Vector2 CurrentJitter { get; private set; }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _ssaaImpl = GetComponent<SSAAImpl>();
        }

        private void OnPreRender()
        {
            if (_ssaaImpl == null || !(_ssaaImpl.EnableFSR3 || _ssaaImpl.EnableDLSS)) return;

            float scale = VRGlobals.upscalingMultiplier;
            int scaledWidth = (int)(XRSettings.eyeTextureWidth * scale);
            int scaledHeight = (int)(XRSettings.eyeTextureHeight * scale);

            // Get the jitter for the scaled space
            CurrentJitter = VRJitterHelper.GetJitterPixelSpace();

            // Save the original matrix before we mess with it
            _originalProj = _camera.projectionMatrix;
            _camera.nonJitteredProjectionMatrix = _camera.projectionMatrix;

            Matrix4x4 projMatrix = _camera.projectionMatrix;
            // Map -0.5..0.5 jitter to the projection space
            projMatrix.m02 += CurrentJitter.x * 2f / scaledWidth;
            projMatrix.m12 += CurrentJitter.y * 2f / scaledHeight;
            //projMatrix.m02 += (CurrentJitter.x * 2f) / XRSettings.eyeTextureWidth;
            //.m12 += (CurrentJitter.y * 2f) / XRSettings.eyeTextureHeight;
            _camera.projectionMatrix = projMatrix;
        }
        private void OnPostRender()
        {
            _camera.projectionMatrix = _originalProj;
        }
       
    }

    public static class VRJitterHelper
    {
        private static int _sampleIndex = 0;
        public static int CurrentSampleCount { get; private set; } = 8;
        public static void SetSampleCountForScale(float scale, bool isDLSS)
        {
            if (isDLSS)
            {
                // DLSS AI is optimized for 8 or 16. 
                // 16 is better for the "DLAA" (1.0x) mode you've added.
                CurrentSampleCount = (scale >= 0.99f) ? 16 : 8;
            }
            else
            {
                // FSR 3.0 needs more samples as resolution drops to maintain stability.
                float scalingFactor = 1.0f / scale;
                int recommended = Mathf.CeilToInt(8f * (scalingFactor * scalingFactor));

                // Step up in powers of 2 (8, 16, 32)
                if (recommended <= 8) CurrentSampleCount = 8;
                else if (recommended <= 16) CurrentSampleCount = 16;
                else CurrentSampleCount = 32;
            }
        }

        public static Vector2 GetJitterPixelSpace()
        {
            // Use Time.frameCount to ensure both eyes get the exact same jitter index
            int index = Time.frameCount % CurrentSampleCount;
            return GetHaltonValue(index);
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
                result += (index % radix) * fraction;
                index /= radix;
                fraction /= radix;
            }
            return result;
        }
    }
}
