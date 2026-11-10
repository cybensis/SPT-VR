using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovVR.Source.Weapons
{
    // Decouples the scope's scene resolution from its reticle resolution without redirecting the optic camera's render target:
    // pointing it at a different RT freezes/blues GPU-Instancer grass (GPUI's motion-vector MRT is tied to the registered target);
    // shrinking the camera's own target is safe.
    //
    // The optic camera renders into its own RenderTexture_0 (GClass3687), shrunk in place to opticRenderResolution. Each frame a
    // CommandBuffer at AfterEverything (after scene + grass + post FX are composited) upscales the camera target into the full-res
    // lensTex and draws the reticle into it sharp. WeaponPatches points the lens's global _CamTex at lensTex.
    internal class OpticReticleDecoupler : MonoBehaviour
    {
        public RenderTexture lensTex;   // full-res texture the lens samples (_CamTex). We own/release it.
        public OpticRetrice retrice;    // source of the reticle mesh (Renderer is public)

        public static bool FlipBlit = false; // flip the upscale blit, if the scope renders upside-down

        private Camera cam;
        private CommandBuffer cmd;
        private const CameraEvent EVT = CameraEvent.AfterEverything;
        private static readonly int SCALE = Shader.PropertyToID("_Scale");
        private static readonly int NONJITTERED = Shader.PropertyToID("_NonJitteredProj");

        private void Awake()
        {
            cam = GetComponent<Camera>();
        }

        private void OnEnable()
        {
            if (cam == null) cam = GetComponent<Camera>();
            if (cmd == null) cmd = new CommandBuffer { name = "OpticReticleDecouple" };
            if (cam != null)
            {
                cam.RemoveCommandBuffer(EVT, cmd);
                cam.AddCommandBuffer(EVT, cmd);
            }
        }

        private void OnDisable()
        {
            if (cam != null && cmd != null)
                cam.RemoveCommandBuffer(EVT, cmd);
        }

        // Rebuild the AfterEverything command buffer each frame (current matrices/reticle)
        private void OnPreRender()
        {
            if (cmd == null || lensTex == null || cam == null)
                return;

            cmd.Clear();

            // Upscale the finished low-res optic image (camera target == the shrunk RenderTexture_0) into the full-res lens texture
            if (FlipBlit)
                cmd.Blit(BuiltinRenderTextureType.CameraTarget, lensTex, new Vector2(1f, -1f), new Vector2(0f, 1f));
            else
                cmd.Blit(BuiltinRenderTextureType.CameraTarget, lensTex);

            // Re-draw the reticle at full res on top (the vanilla low-res draw is suppressed in WeaponPatches).
            // Material + scale read live from the current sight, so scope swaps stay correct.
            Renderer partRenderer = retrice != null ? retrice.Renderer : null;
            if (partRenderer != null && partRenderer.gameObject.activeInHierarchy)
            {
                Material mat = null;
                float scale = 0.1f;
                OpticSight sight = CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
                if (sight != null && sight.ScopeData != null && sight.ScopeData.Reticle != null)
                {
                    mat = sight.ScopeData.Reticle.Material;
                    scale = sight.ScopeData.Reticle.Scale * 0.1f;
                }

                if (mat != null)
                {
                    cmd.SetRenderTarget(lensTex);
                    cmd.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);
                    cmd.SetGlobalFloat(SCALE, scale);
                    cmd.SetGlobalMatrix(NONJITTERED, GL.GetGPUProjectionMatrix(cam.nonJitteredProjectionMatrix, true));
                    cmd.DrawRenderer(partRenderer, mat, 0, 0);
                }
            }
        }

        private void OnDestroy()
        {
            if (cam != null && cmd != null)
                cam.RemoveCommandBuffer(EVT, cmd);
            cmd?.Release();
            if (lensTex != null)
            {
                lensTex.Release();
                Destroy(lensTex);
                lensTex = null;
            }
        }
    }
}
