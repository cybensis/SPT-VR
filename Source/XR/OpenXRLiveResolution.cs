using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features;

namespace TarkovVR.Source.XR
{
    // SteamVR's resolution slider while the game runs. Unity's OpenXR plugin asks the runtime for the recommended eye size once, when
    // the session starts, and keeps it; this asks again every second and scales Unity's eye textures (eyeTextureResolutionScale, which
    // remakes its swapchains) by the new size over the one the session started with.
    public class OpenXRLiveResolution : OpenXRFeature
    {
        public static bool followSlider = true;
        public static float checkInterval = 1f;      // s between asking the runtime
        public static float minChange = 0.01f;       // a smaller relative change in size is ignored

        // The scale set here (1 = the session's own size): SteamVR's slider, not supersampling
        public static float AppliedScale { get; private set; } = 1f;

        private const int XR_SUCCESS = 0;
        private const int XR_TYPE_VIEW_CONFIGURATION_VIEW = 41;
        private const int XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct ViewConfigurationView
        {
            public int type;
            public IntPtr next;
            public uint recommendedImageRectWidth;
            public uint maxImageRectWidth;
            public uint recommendedImageRectHeight;
            public uint maxImageRectHeight;
            public uint recommendedSwapchainSampleCount;
            public uint maxSwapchainSampleCount;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumerateViewConfigurationViewsDelegate(ulong instance, ulong systemId, int viewConfigurationType,
            uint viewCapacityInput, out uint viewCountOutput, [In, Out] ViewConfigurationView[] views);

        private static EnumerateViewConfigurationViewsDelegate enumerateViews;
        private static ulong instanceHandle, systemHandle;
        private static uint startWidth, startHeight;       // the size the session started with
        private static uint lastWidth, lastHeight;
        private static float nextCheck, verifyAt;
        private static bool loggedStart, loggedFailure;
        private static readonly ViewConfigurationView[] views = new ViewConfigurationView[2];

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            instanceHandle = xrInstance;
            try
            {
                IntPtr function = OpenXRFunctions.Find(xrGetInstanceProcAddr, xrInstance, "xrEnumerateViewConfigurationViews", out int result);
                enumerateViews = function != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<EnumerateViewConfigurationViewsDelegate>(function) : null;
                if (enumerateViews == null)
                    Plugin.MyLog.LogWarning($"[OpenXR] xrEnumerateViewConfigurationViews unavailable (result {result}): SteamVR's resolution applies after a restart");
            }
            catch (Exception exception)
            {
                enumerateViews = null;
                Plugin.MyLog.LogWarning($"[OpenXR] live resolution off: {exception.Message}");
            }
            return true;
        }

        protected override void OnSystemChange(ulong xrSystem)
        {
            systemHandle = xrSystem;
        }

        protected override void OnSessionCreate(ulong xrSession)
        {
            // a new session takes the runtime's size as it is now
            if (AppliedScale != 1f)
            {
                XRSettings.eyeTextureResolutionScale = 1f;
                AppliedScale = 1f;
            }
            startWidth = startHeight = 0;
            loggedStart = false;
            if (TryRecommended(out uint width, out uint height))
            {
                startWidth = lastWidth = width;
                startHeight = lastHeight = height;
            }
        }

        protected override void OnSessionDestroy(ulong xrSession)
        {
            startWidth = startHeight = 0;
        }

        private static bool TryRecommended(out uint width, out uint height)
        {
            width = height = 0;
            if (enumerateViews == null || instanceHandle == 0 || systemHandle == 0)
                return false;
            for (int index = 0; index < views.Length; index++)
            {
                views[index] = default;
                views[index].type = XR_TYPE_VIEW_CONFIGURATION_VIEW;
            }
            int result = enumerateViews(instanceHandle, systemHandle, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, (uint)views.Length, out uint count, views);
            if (result != XR_SUCCESS || count < 1)
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Plugin.MyLog.LogWarning($"[OpenXR] couldn't ask the runtime for the eye size (result {result}, views {count})");
                }
                return false;
            }
            width = views[0].recommendedImageRectWidth;
            height = views[0].recommendedImageRectHeight;
            return width > 0 && height > 0;
        }

        // Once a frame (OpenXRLiveResolutionWatch)
        internal static void Check()
        {
            if (startWidth == 0 || Time.unscaledTime < nextCheck)
                return;
            nextCheck = Time.unscaledTime + checkInterval;
            if (!loggedStart)
            {
                loggedStart = true;
                Plugin.MyLog.LogInfo($"[OpenXR] SteamVR recommends {startWidth}x{startHeight} an eye, Unity's eye textures are {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}"
                    + (followSlider ? ": following SteamVR's resolution slider" : " (live resolution off)"));
            }
            if (verifyAt > 0f && Time.unscaledTime >= verifyAt)
            {
                verifyAt = 0f;
                int expected = Mathf.RoundToInt(startWidth * AppliedScale);
                bool resized = Mathf.Abs(XRSettings.eyeTextureWidth - expected) <= 2;
                Plugin.MyLog.LogInfo($"[OpenXR] Unity's eye textures are now {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}"
                    + (resized ? "" : $" - NOT resized (expected about {expected} wide): Unity's OpenXR plugin didn't take the scale"));
            }
            if (!followSlider || !TryRecommended(out uint width, out uint height) || (width == lastWidth && height == lastHeight))
                return;
            lastWidth = width;
            lastHeight = height;
            float scale = (float)width / startWidth;
            if (Mathf.Abs(scale - AppliedScale) < minChange * AppliedScale)
                return;
            XRSettings.eyeTextureResolutionScale = scale;
            AppliedScale = scale;
            verifyAt = Time.unscaledTime + 2f;
            Plugin.MyLog.LogInfo($"[OpenXR] SteamVR's resolution is now {width}x{height} an eye (the session started at {startWidth}x{startHeight}): "
                + $"eye textures scaled x{scale:0.000}");
        }
    }

    // Drives OpenXRLiveResolution.Check
    internal class OpenXRLiveResolutionWatch : MonoBehaviour
    {
        private void Update()
        {
            OpenXRLiveResolution.Check();
        }
    }
}
