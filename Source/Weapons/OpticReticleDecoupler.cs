using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace TarkovVR.Source.Weapons
{
    // Decouples the scope's SCENE resolution from its RETICLE resolution - WITHOUT redirecting the optic
    // camera's render target (redirecting to a different RT breaks GPU-Instancer grass: confirmed in-headset
    // that shrinking the camera's OWN target keeps grass correct, but pointing it at a separate RT froze/blued
    // it, because GPUI's motion-vector MRT is tied to the registered target).
    //
    // So: the optic camera still renders into its own RenderTexture_0 (GClass3687), just shrunk in place to
    // opticRenderResolution (cheap, grass-safe). For the LENS we keep a separate FULL-res lensTex: each frame,
    // in a CommandBuffer at AfterEverything (after scene + grass + post FX are composited into the camera
    // target), we upscale the camera target into lensTex and draw the reticle into it sharp. WeaponPatches then
    // points the lens's global _CamTex at lensTex. Net: grass-safe cheap soft scene + crisp reticle.
    internal class OpticReticleDecoupler : MonoBehaviour
    {
        public RenderTexture lensTex;   // full-res texture the lens samples (_CamTex). We own/release it.
        public OpticRetrice retrice;    // source of the reticle mesh (Renderer is public)

        public static bool FlipBlit = false; // toggle in-headset if the scope renders upside-down

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

        // Rebuild the end-of-frame command buffer each frame (current matrices/reticle). It runs at
        // AfterEverything, after the camera + grass + post FX have written the final image to its target.
        private void OnPreRender()
        {
            if (cmd == null || lensTex == null || cam == null)
                return;

            cmd.Clear();

            // Upscale the finished low-res optic image (camera target == the shrunk RenderTexture_0) into the
            // full-res lens texture. Scene + grass + atmospherics are already composited into the camera target.
            if (FlipBlit)
                cmd.Blit(BuiltinRenderTextureType.CameraTarget, lensTex, new Vector2(1f, -1f), new Vector2(0f, 1f));
            else
                cmd.Blit(BuiltinRenderTextureType.CameraTarget, lensTex);

            // Re-draw the reticle at FULL res on top (vanilla low-res draw is suppressed in WeaponPatches).
            // Material + scale read live from the current sight so scope swaps stay correct.
            Renderer r = retrice != null ? retrice.Renderer : null;
            if (r != null && r.gameObject.activeInHierarchy)
            {
                Material mat = null;
                float scale = 0.1f;
                OpticSight sight = CameraClass.Instance?.OpticCameraManager?.CurrentOpticSight;
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
                    cmd.DrawRenderer(r, mat, 0, 0);
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
