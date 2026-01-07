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
        public static Vector2 CurrentJitter { get; private set; }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _ssaaImpl = GetComponent<SSAAImpl>();
        }
        /*
        private void OnPreRender()
        {
            if (_ssaaImpl == null) return;

            if (!(_ssaaImpl.EnableFSR3 || _ssaaImpl.EnableFSR2 || _ssaaImpl.EnableDLSS || _ssaaImpl.EnableFSR)) return; // Not upscaling


            float scale = VRGlobals.upscalingMultiplier;
            int nativeWidth = XRSettings.eyeTextureWidth;
            int nativeHeight = XRSettings.eyeTextureHeight;
            int scaledWidth = (int)(nativeWidth * scale);
            int scaledHeight = (int)(nativeHeight * scale);

            // Calculate jitter for scaled resolution (what the camera will actually render)
            if (scale >= 1f)
                CurrentJitter = VRJitterHelper.GetJitterPixelSpace(nativeWidth, nativeHeight);
            else
                CurrentJitter = VRJitterHelper.GetJitterPixelSpace(scaledWidth, scaledHeight);
            // Apply jitter for scaled resolution
            Matrix4x4 projMatrix = _camera.projectionMatrix;
            projMatrix.m02 += CurrentJitter.x * 2f / scaledWidth;
            projMatrix.m12 += CurrentJitter.y * 2f / scaledHeight;

            _camera.nonJitteredProjectionMatrix = _camera.projectionMatrix;
            _camera.projectionMatrix = projMatrix;
        }
        */
        private void OnPreRender()
        {
            if (_ssaaImpl == null || !(_ssaaImpl.EnableFSR3 || _ssaaImpl.EnableDLSS)) return;

            float scale = VRGlobals.upscalingMultiplier;
            int scaledWidth = (int)(XRSettings.eyeTextureWidth * scale);
            int scaledHeight = (int)(XRSettings.eyeTextureHeight * scale);

            // Get the jitter for the scaled space
            CurrentJitter = VRJitterHelper.GetJitterPixelSpace(scaledWidth, scaledHeight);

            // Save the original matrix before we mess with it
            _camera.nonJitteredProjectionMatrix = _camera.projectionMatrix;

            Matrix4x4 projMatrix = _camera.projectionMatrix;
            // Map -0.5..0.5 jitter to the projection space
            //projMatrix.m02 += CurrentJitter.x * 2f / scaledWidth;
            //projMatrix.m12 += CurrentJitter.y * 2f / scaledHeight;
            projMatrix.m02 += (CurrentJitter.x * 2f) / XRSettings.eyeTextureWidth;
            projMatrix.m12 += (CurrentJitter.y * 2f) / XRSettings.eyeTextureHeight;
            _camera.projectionMatrix = projMatrix;
        }
        private void OnPostRender()
        {
            _camera.ResetProjectionMatrix();
        }
        
        /*
        private void OnPreRender()
        {
            if (!XRSettings.enabled) return;
            if (_ssaaImpl == null) return;

            if (!(_ssaaImpl.EnableFSR3 || _ssaaImpl.EnableFSR2 || _ssaaImpl.EnableDLSS || _ssaaImpl.EnableFSR)) return;

            float scale = VRGlobals.upscalingMultiplier;
            int nativeWidth = XRSettings.eyeTextureWidth;
            int nativeHeight = XRSettings.eyeTextureHeight;
            int scaledWidth = (int)(nativeWidth * scale);
            int scaledHeight = (int)(nativeHeight * scale);

            if (scale >= 1f)
                CurrentJitter = VRJitterHelper.GetJitterPixelSpace(nativeWidth, nativeHeight);
            else
                CurrentJitter = VRJitterHelper.GetJitterPixelSpace(scaledWidth, scaledHeight);

            originalPosition = _camera.transform.position;

            // Calculate jitter in VIEW space (independent of camera rotation)
            float halfHeight = _camera.nearClipPlane * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float halfWidth = halfHeight * _camera.aspect;

            float worldJitterX = (CurrentJitter.x / scaledWidth) * 2f * halfWidth;
            float worldJitterY = (CurrentJitter.y / scaledHeight) * 2f * halfHeight;

            // Create view-space offset vector
            Vector3 viewSpaceOffset = new Vector3(worldJitterX, worldJitterY, 0f);

            // Transform to world space using camera's rotation matrix
            Vector3 worldSpaceOffset = _camera.transform.TransformDirection(viewSpaceOffset);

            // Apply offset
            _camera.transform.position += worldSpaceOffset;
        }

        private void OnPostRender()
        {
            if (!XRSettings.enabled) return;

            // Restore original position
            _camera.transform.position = originalPosition;
        }
        */
    }

    public static class VRJitterHelper
    {
        private static int _sampleIndex = 0;
        public static int CurrentSampleCount { get; private set; } = 8;
        public static void SetSampleCountForScale(float scale, bool isDLSS)
        {
            /*
            const int baseSampleCount = 8;
            CurrentSampleCount = (int)((double)baseSampleCount / (scale * scale) + 0.5);
            CurrentSampleCount = Mathf.Clamp(CurrentSampleCount, 8, 32);
            */
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
        /*
        public static Vector2 GetJitterPixelSpace(int renderWidth, int renderHeight)
        {
            var jitter = GetHaltonValue(_sampleIndex & (CurrentSampleCount - 1));
            _sampleIndex++;

            return jitter;
        }
        */

        public static Vector2 GetJitterPixelSpace(int renderWidth, int renderHeight)
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
