using EFT.Impostors;
using HarmonyLib;
using UnityEngine;

namespace TarkovVR.Patches.Visuals
{
    // ------------------------------------------------------------------------------------------------
    // VR tree-impostor frustum culling fix (the "trees pop out on the far-right of the right eye" bug)
    // ------------------------------------------------------------------------------------------------
    // EFT batches distant trees' Amplify impostor billboards into one DrawMeshInstancedIndirect via
    // EFT.Impostors.ImpostorsRenderer, and GPU-culls them against a single camera frustum inside Class2098.method_15():
    //
    //     GeometryUtility.CalculateFrustumPlanes(base.Camera, Plane_0);   // -> _Frustum compute global
    //
    // That is the camera's mono (effectively left-eye) projection: in MultiPass stereo the right eye sees
    // further right than it covers. Same cause as the GPU-Instancer grass cull
    // (GPUInstancerPatches.FixGpuInstancerCulling). PerfectCulling is not involved: it resolves visibility
    // from camera position only (GetFrameHashNoOrientation).
    //
    // Fix: planes built from a horizontally widened projection, so the cull frustum spans both eyes. Only
    // _Frustum is touched; _FOVHalfAngle / _LookMatrix / _CamPosition (billboard orientation + size/LOD) stay
    // vanilla. method_15 is recomputed whenever the head moves, so patching it covers every recompute path.
    [HarmonyPatch]
    internal class ImpostorCullingPatches
    {

        // Horizontal widen applied to the projection used for the impostor cull. <1 = wider FOV used
        // for culling = fewer false-culls at the eye edges, at the cost of a few more billboards drawn.
        // 0.9 (~11% wider) matches the grass value.
        public static float impostorCullHorizontalWiden = 0.9f;
        // Vertical FOV is identical for both eyes in VR, so this stays 1.0 (exposed only for tuning).
        public static float impostorCullVerticalWiden = 1.0f;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ImpostorsMainCameraContext), "UpdateFrustum")]
        private static bool WidenImpostorCullFrustum(ImpostorsMainCameraContext __instance)
        {
            Camera camera = __instance.Camera;
            if (camera == null)
                return true;

            // Widen the projection, not the combined VP matrix: the rotation baked into VP would skew the
            // result. Nobody overrides cam.cullingMatrix here, so projectionMatrix * worldToCameraMatrix
            // matches what vanilla uses.
            Matrix4x4 proj = camera.projectionMatrix;
            proj.m00 *= impostorCullHorizontalWiden;   // smaller m00 => larger horizontal FOV
            if (impostorCullVerticalWiden != 1.0f)
                proj.m11 *= impostorCullVerticalWiden;

            Matrix4x4 viewProj = proj * camera.worldToCameraMatrix;
            GeometryUtility.CalculateFrustumPlanes(viewProj, __instance._planes);

            // Pack the 6 planes into the Vector4 array the culling compute shader reads (_Frustum),
            // exactly like the original method_15.
            for (int i = 0; i < 6; i++)
            {
                Plane plane = __instance._planes[i];
                Vector3 n = plane.normal;
                __instance._frustum[i] = new Vector4(n.x, n.y, n.z, plane.distance);
            }
            return false;
        }
    }
}
