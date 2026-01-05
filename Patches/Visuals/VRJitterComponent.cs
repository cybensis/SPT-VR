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

        public static Vector2 CurrentJitter { get; private set; }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _ssaaImpl = GetComponent<SSAAImpl>();
        }

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

        private void OnPostRender()
        {
            _camera.ResetProjectionMatrix();
        }
    }

    public static class VRJitterHelper
    {
        private static int _sampleIndex = 0;
        public static int CurrentSampleCount { get; private set; } = 8;
        public static void SetSampleCountForScale(float scale)
        {
            const int baseSampleCount = 8;
            CurrentSampleCount = (int)((double)baseSampleCount / (scale * scale) + 0.5);
            CurrentSampleCount = Mathf.Clamp(CurrentSampleCount, 8, 32);
        }

        public static Vector2 GetJitterPixelSpace(int renderWidth, int renderHeight)
        {
            var jitter = GetHaltonValue(_sampleIndex & (CurrentSampleCount - 1));
            _sampleIndex++;

            return jitter;
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
