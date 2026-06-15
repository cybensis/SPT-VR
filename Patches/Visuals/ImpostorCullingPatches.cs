using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Patches.Visuals
{
    // ------------------------------------------------------------------------------------------------
    // VR tree-impostor frustum culling fix (the "trees pop out on the far-right of the right eye" bug)
    // ------------------------------------------------------------------------------------------------
    // Distant trees (Woods etc.) are NOT drawn one-renderer-per-tree at range — EFT batches their
    // Amplify impostor billboards into one DrawMeshInstancedIndirect via EFT.Impostors.ImpostorsRenderer,
    // and GPU-culls every billboard against a single camera frustum inside Class2098.method_15():
    //
    //     GeometryUtility.CalculateFrustumPlanes(base.Camera, Plane_0);   // -> _Frustum compute global
    //
    // That call uses the camera's MONO/center (effectively left-eye) projection. In MultiPass stereo
    // the RIGHT eye sees further to the right than that frustum covers, so distant tree impostors get
    // wrongly rejected on the far-right edge of the right eye only. This is the exact same root cause
    // as the GPU-Instancer grass cull (see GPUInstancerPatches.FixGpuInstancerCulling) — a per-system
    // frustum that doesn't account for the second eye. PerfectCulling is NOT involved: it resolves
    // visibility from camera POSITION only (GetFrameHashNoOrientation) so it's the same set both eyes.
    //
    // Fix (same idea as the grass fix): replace the plane computation with one built from a
    // horizontally-WIDENED projection so the cull frustum spans both eyes. Only the cull-rejection
    // planes (_Frustum) are touched; _FOVHalfAngle / _LookMatrix / _CamPosition (billboard orientation
    // + size/LOD) are left to vanilla, so the impostors look identical — they just stop being culled
    // at the eye edge. method_15 is recomputed by the renderer whenever the head moves/rotates, so
    // patching it here covers every recompute path.
    [HarmonyPatch]
    internal class ImpostorCullingPatches
    {

        // Horizontal widen applied to the projection used for the impostor cull. <1 = wider FOV used
        // for culling = fewer false-culls at the eye edges, at the cost of a few more billboards drawn.
        // 0.9 (~11% wider) matches the proven grass value. If trees still pop at the right edge, lower
        // this; once the pop is gone, nudge back toward 1.0 to trim cost (the mod is CPU-bound).
        public static float impostorCullHorizontalWiden = 0.9f;
        // Vertical FOV is identical for both eyes in VR, so this stays 1.0 (exposed only for tuning).
        public static float impostorCullVerticalWiden = 1.0f;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Class2098), "method_15")]
        private static bool WidenImpostorCullFrustum(Class2098 __instance)
        {
            Camera cam = __instance.Camera;
            if (cam == null)
                return true;

            // Widen the PROJECTION (before combining with the view) so the cull frustum covers both
            // eyes. Widening must happen on the projection, not the combined VP matrix, or the rotation
            // baked into VP would skew the result. Nobody overrides cam.cullingMatrix here, so
            // projectionMatrix * worldToCameraMatrix matches what vanilla would have used.
            Matrix4x4 proj = cam.projectionMatrix;
            proj.m00 *= impostorCullHorizontalWiden;   // smaller m00 => larger horizontal FOV
            if (impostorCullVerticalWiden != 1.0f)
                proj.m11 *= impostorCullVerticalWiden;

            Matrix4x4 viewProj = proj * cam.worldToCameraMatrix;
            GeometryUtility.CalculateFrustumPlanes(viewProj, __instance.Plane_0);

            // Pack the 6 planes into the Vector4 array the culling compute shader reads (_Frustum),
            // exactly like the original method_15.
            for (int i = 0; i < 6; i++)
            {
                Plane p = __instance.Plane_0[i];
                Vector3 n = p.normal;
                __instance.Vector4_0[i] = new Vector4(n.x, n.y, n.z, p.distance);
            }
            return false;
        }
    }
}
