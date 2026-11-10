using GPUInstancer;
using HarmonyLib;
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class GPUInstancerPatches
    {
        // Grass (GPUI) motion vectors in stereo. GPUI's MotionVectors pass writes uv(A * pos with this frame's wind) -
        // uv(B * pos with last frame's wind), A/B = the _NonJitteredVP / _PreviousVP globals, and BSG's camera motion-vector
        // pass (Hidden/Internal-MotionVectorsEFT) ADDS that where grass is in front, on top of the camera motion it gets from
        // depth (decoded 2026-10-19). So the added term must be the grass's own wind motion only: A = B = this eye's current VP,
        // set per eye in SetEyeGrassMatrices. Vanilla fills A/B once a frame from the mono camera (wrong in each eye); the
        // identity matrices used before (perEyeGrassMotion = false) turned the wind sway into world metres read as screen
        // motion, so FSR dropped the history on swaying grass = the grass/leaf aliasing. The scope's own managers stay vanilla.
        public static bool perEyeGrassMotion = true;
        private static readonly int NonJitteredVP = Shader.PropertyToID("_NonJitteredVP");
        private static readonly int PreviousVP = Shader.PropertyToID("_PreviousVP");
        private static bool eyeHookAdded;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GPUInstancerManager), "ClearMotionVectorData")]
        private static bool OverrideGrassMotionVectorMatrices(GPUInstancerManager __instance)
        {
            if (__instance.IsOptic)
                return true;
            if (__instance.IsMotionVectorDataClear || __instance.Camera == null || __instance.MotionVectorsCommandBuffer == null)
            {
                return false;
            }
            if (!eyeHookAdded)
            {
                eyeHookAdded = true;
                Camera.onPreRender += SetEyeGrassMatrices;
            }

            __instance.MotionVectorsCommandBuffer.Clear();
            __instance.MotionVectorsAfterCommandBuffer.Clear();

            if (GPUInstancerManager.bGenerateMotionVectors)
            {
                if (!perEyeGrassMotion)
                {
                    __instance.MotionVectorsCommandBuffer.SetGlobalMatrix("_PreviousVP", Matrix4x4.identity);
                    __instance.MotionVectorsCommandBuffer.SetGlobalMatrix("_NonJitteredVP", Matrix4x4.identity);
                }

                __instance.MotionVectorsCommandBuffer.SetRenderTarget(__instance.MRT, BuiltinRenderTextureType.CameraTarget);
                __instance.MotionVectorsCommandBuffer.ClearRenderTarget(clearDepth: false, clearColor: true, Color.black);

                __instance.MotionVectorsAfterCommandBuffer.SetGlobalTexture("_CameraMotionVectorsAddition", __instance.MotionVectorsTexture);
                __instance.MotionVectorsAfterCommandBuffer.SetGlobalTexture("_CameraMotionVectorsDepth", __instance.MotionVectorsDepth);
            }
            else
            {
                __instance.MotionVectorsCommandBuffer.SetRenderTarget(__instance.MotionVectorsDepth, BuiltinRenderTextureType.CameraTarget);
                __instance.MotionVectorsCommandBuffer.ClearRenderTarget(clearDepth: false, clearColor: true, Color.black);
                __instance.MotionVectorsCommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.CameraTarget);
            }

            __instance.IsMotionVectorDataClear = true;
            return false;
        }

        // Before each eye: the eye's own current VP in both slots (jitter cancels in the difference, but the non-jittered
        // matrix keeps it out entirely). The engine sets its own matrices for its camera motion pass, so this only feeds
        // GPUI's grass pass (else the identity globals above would have broken FSR everywhere, not only on grass).
        private static void SetEyeGrassMatrices(Camera cam)
        {
            if (!perEyeGrassMotion || cam == null || cam != VRGlobals.VRCam)
                return;
            Matrix4x4 viewProjection;
            if (cam.stereoEnabled)
            {
                var eye = cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
                viewProjection = GL.GetGPUProjectionMatrix(cam.GetStereoNonJitteredProjectionMatrix(eye), true) * cam.GetStereoViewMatrix(eye);
            }
            else
            {
                viewProjection = GL.GetGPUProjectionMatrix(cam.nonJitteredProjectionMatrix, true) * cam.worldToCameraMatrix;
            }
            Shader.SetGlobalMatrix(NonJitteredVP, viewProjection);
            Shader.SetGlobalMatrix(PreviousVP, viewProjection);
        }

        // This is where GPU Instancing handles culling, it does not work properly in multipass, it only takes left eye into account.
        // Adding toggles for it because this can heavily impact performance depending on the scene and hardware.
        // This isn't needed anymore so I have it commented out, leaving here for reference.
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GPUInstancerManager), "UpdateBuffers")]
        private static void DisableAllCulling(GPUInstancerManager __instance)
        {

            if (VRSettings.GetOccCulling())
                __instance.isOcclusionCulling = false;
            else
                __instance.isOcclusionCulling = true;
            if (VRSettings.GetFrusCulling())
                __instance.isFrustumCulling = false;
            else
                __instance.isFrustumCulling = true;
        }
        */

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GPUInstancerManager), "Awake")]
        private static void ForceVRComputeShader(GPUInstancerManager __instance)
        {
            // Force load the VR compute shader
            var vrShader = (ComputeShader)Resources.Load(GPUInstancerConstants.CAMERA_VR_COMPUTE_RESOURCE_PATH);
            if (vrShader != null)
            {
                var kernelIDs = new int[GPUInstancerConstants.CAMERA_COMPUTE_KERNELS.Length];
                for (int i = 0; i < kernelIDs.Length; i++)
                {
                    kernelIDs[i] = vrShader.FindKernel(GPUInstancerConstants.CAMERA_COMPUTE_KERNELS[i]);
                }
                // Both fields are protected static in the game (public in our publicized reference; AllowUnsafeBlocks lets Mono skip the
                // access check, as for every other private member we touch). Written directly; the reflection that did this before stays
                // only as a fallback, in case an access check ever refuses the direct write (it's in its own method, so a refusal at JIT
                // time lands in this catch too)
                try
                {
                    SetCameraComputeShader(vrShader, kernelIDs);
                }
                catch (MemberAccessException exception)
                {
                    Plugin.MyLog.LogWarning($"[GPUInstancer] direct write refused ({exception.GetType().Name}), set by reflection instead");
                    AccessTools.StaticFieldRefAccess<ComputeShader>(typeof(GPUInstancerManager), "_cameraComputeShader") = vrShader;
                    AccessTools.StaticFieldRefAccess<int[]>(typeof(GPUInstancerManager), "_cameraComputeKernelIDs") = kernelIDs;
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void SetCameraComputeShader(ComputeShader shader, int[] kernelIDs)
        {
            GPUInstancerManager._cameraComputeShader = shader;
            GPUInstancerManager._cameraComputeKernelIDs = kernelIDs;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GPUInstancerCameraData), "CalculateCameraData")]
        private static bool FixGpuInstancerCulling(GPUInstancerCameraData __instance)
        {
            if (__instance.mainCamera == null)
            {
                return false;
            }
            __instance.hasOcclusionGenerator = __instance.hiZOcclusionGenerator != null && __instance.hiZOcclusionGenerator.hiZDepthTexture != null;
            Matrix4x4 matrix4x;
            if (__instance.hasOcclusionGenerator && __instance.hiZOcclusionGenerator.isVREnabled)
            {
                // Use center projection and widen slightly
                Matrix4x4 projection = __instance.mainCamera.projectionMatrix;
                projection.m00 *= 0.9f; // Less widening needed from center
                matrix4x = projection * __instance.mainCamera.worldToCameraMatrix;
            }
            else
            {
                matrix4x = __instance.mainCamera.projectionMatrix * __instance.mainCamera.worldToCameraMatrix;
            }
            if (__instance.mvpMatrixFloats == null || __instance.mvpMatrixFloats.Length != 16)
            {
                __instance.mvpMatrixFloats = new float[16];
            }
            GPUInstancerUtility.Matrix4x4ToFloatArray(matrix4x, __instance.mvpMatrixFloats);
            if (__instance.hasOcclusionGenerator && __instance.hiZOcclusionGenerator.isVREnabled && GPUInstancerConstants.gpuiSettings.testBothEyesForVROcclusion)
            {
                Matrix4x4 matrix4x2 = __instance.mainCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) * __instance.mainCamera.worldToCameraMatrix;
                if (__instance.mvpMatrix2Floats == null || __instance.mvpMatrix2Floats.Length != 16)
                {
                    __instance.mvpMatrix2Floats = new float[16];
                }
                GPUInstancerUtility.Matrix4x4ToFloatArray(matrix4x2, __instance.mvpMatrix2Floats);
            }
            __instance.cameraPosition = __instance.mainCamera.transform.position;
            return false;
        }
    }
}
