using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace TarkovVR.Source.Graphics
{
    // The game's FSR2/3 with the VR hidden-area mask: masked pixels hold depth ~1 cm from the eye and the camera motion of
    // such a point, so FSR's nearest-depth motion dilation and its previous-depth scatter spread them into the visible
    // image and its depth clip drops the history there (mask-shaped aliased patches). Before each eye's FSR, depth and
    // motion are copied with masked pixels set to far depth + zero motion (shader Hidden/SPTVR/FsrMaskClean, bundle
    // sptvr/Assets/fsrmask) and FSR reads the copies (Remap). Only while the occlusion mesh is on; no bundle = the originals.
    public static class FsrMaskClean
    {

        // For FSR4 Bridge (typed, behind its SPT-VR check): the same masked copy straight into its depth / motion
        // textures, run now (FSR4 copes with the raw mask; this removes the bad data for two tiny passes). False = not
        // done (cleaner off, no mask, no bundle, sizes not matching) and the caller copies plainly.
        public static bool CopyClean(Texture depthSrc, Texture motionSrc, RenderTexture depthDst, RenderTexture motionDst)
        {
            if (!XRSettings.enabled || !XRSettings.useOcclusionMesh
                || depthSrc == null || motionSrc == null || depthDst == null || motionDst == null)
                return false;
            if (depthSrc.width != depthDst.width || depthSrc.height != depthDst.height
                || motionSrc.width != motionDst.width || motionSrc.height != motionDst.height || !EnsureMaterial())
                return false;
            if (bridgeCb == null) bridgeCb = new CommandBuffer { name = "SPT-VR FSR4 mask clean" };
            bridgeCb.Clear();
            bridgeCb.SetGlobalTexture(MaskDepthId, depthSrc);
            bridgeCb.SetGlobalTexture(SrcId, depthSrc);
            bridgeCb.Blit(depthSrc, depthDst, mat, 0);
            bridgeCb.SetGlobalTexture(SrcId, motionSrc);
            bridgeCb.Blit(motionSrc, motionDst, mat, 1);
            UnityEngine.Graphics.ExecuteCommandBuffer(bridgeCb);
            return true;
        }
        private static CommandBuffer bridgeCb;

        private static Material mat;
        private static bool loadTried;
        private static RenderTexture depthCopy, mvCopy;
        private static bool active;   // the FSR call being recorded reads the copies
        private static CommandBuffer ownCb;
        private static readonly int MaskDepthId = Shader.PropertyToID("_SptvrMaskDepth");
        private static readonly int SrcId = Shader.PropertyToID("_SptvrSrc");
        private const string ShaderName = "Hidden/SPTVR/FsrMaskClean2";   // the explicit-input version; an older bundle = cleaner off
        private static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int CameraMvId = Shader.PropertyToID("_CameraMotionVectorsTexture");
        private static string lastLog;

        private static void LogOnce(string message)
        {
            if (message == lastLog) return;
            lastLog = message;
            Plugin.MyLog.LogWarning(message);
        }

        // Before one eye's FSR. With an external command buffer the cleaning is recorded into it (FSR appends after);
        // otherwise it runs now, ahead of FSR's own buffer, which executes at the end of its PrepareOutput.
        public static void Begin(CommandBuffer external, int width, int height)
        {
            active = false;
            if (!XRSettings.enabled || !XRSettings.useOcclusionMesh || width <= 0 || height <= 0 || !EnsureMaterial())
                return;
            Ensure(ref depthCopy, width, height, RenderTextureFormat.RFloat, "SPT-VR FSR depth (mask as sky)");
            Ensure(ref mvCopy, width, height, RenderTextureFormat.RGHalf, "SPT-VR FSR motion (mask still)");

            // The real textures, not BuiltinRenderTextureType.ResolvedDepth / MotionVectors: those don't resolve as blit
            // sources in a buffer run outside a camera event, and the wrong copies break FSR3 (no AA, raw jitter). Same
            // objects FSR4 Bridge reads.
            Texture depthTexture = Shader.GetGlobalTexture(CameraDepthId), motionTexture = Shader.GetGlobalTexture(CameraMvId);
            if (depthTexture == null || motionTexture == null)
            {
                LogOnce("[VR FSR] mask clean skipped: no camera depth / motion texture bound yet");
                return;
            }
            if (depthTexture.width != width || depthTexture.height != height || motionTexture.width != width || motionTexture.height != height)
            {
                LogOnce($"[VR FSR] mask clean off: depth {depthTexture.width}x{depthTexture.height}, motion {motionTexture.width}x{motionTexture.height}, FSR render size {width}x{height} - not matching, FSR reads the originals");
                return;
            }

            CommandBuffer commandBuffer = external;
            if (commandBuffer == null)
            {
                if (ownCb == null) ownCb = new CommandBuffer { name = "SPT-VR FSR mask clean" };
                ownCb.Clear();
                commandBuffer = ownCb;
            }
            // Inputs as explicit globals: the blit's own _MainTex binding doesn't reach the shader's Texture2D (the
            // copies would read the camera's colour image).
            commandBuffer.SetGlobalTexture(MaskDepthId, depthTexture);
            commandBuffer.SetGlobalTexture(SrcId, depthTexture);
            commandBuffer.Blit(depthTexture, depthCopy, mat, 0);
            commandBuffer.SetGlobalTexture(SrcId, motionTexture);
            commandBuffer.Blit(motionTexture, mvCopy, mat, 1);
            if (external == null)
                UnityEngine.Graphics.ExecuteCommandBuffer(ownCb);
            active = true;
        }

        public static void End() { active = false; }

        // FSR's depth / motion inputs: the cleaned copies while a call is being recorded, else the built-in textures.
        public static RenderTargetIdentifier Remap(BuiltinRenderTextureType type)
        {
            if (active)
            {
                if (type == BuiltinRenderTextureType.MotionVectors) return mvCopy;
                if (type == BuiltinRenderTextureType.ResolvedDepth) return depthCopy;
            }
            return new RenderTargetIdentifier(type);
        }

        private static void Ensure(ref RenderTexture texture, int width, int height, RenderTextureFormat format, string name)
        {
            if (texture != null && texture.width == width && texture.height == height) return;
            if (texture != null) { texture.Release(); UnityEngine.Object.Destroy(texture); }
            texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            texture.Create();
        }

        private static bool EnsureMaterial()
        {
            if (mat != null) return true;
            if (loadTried) return false;
            loadTried = true;
            try
            {
                string path = Path.Combine(BepInEx.Paths.PluginPath, "sptvr", "Assets", "fsrmask");
                if (!File.Exists(path))
                {
                    Plugin.MyLog.LogWarning("[VR FSR] sptvr/Assets/fsrmask missing - the game's FSR2/3 reads the hidden-area mask as is");
                    return false;
                }
                AssetBundle bundle = AssetBundle.LoadFromFile(path);
                Shader shader = bundle != null ? bundle.LoadAsset<Shader>("SPTVRFsrMaskClean") : null;
                if (shader == null || !shader.isSupported || shader.name != ShaderName)
                {
                    Plugin.MyLog.LogWarning("[VR FSR] fsrmask bundle has no usable " + ShaderName + " shader (" + (shader != null ? shader.name : "none")
                                            + ") - rebuild it (Tools/SPT-VR/Build FSR Mask Bundle); FSR2/3 inputs left as is");
                    bundle?.Unload(true);
                    return false;
                }
                mat = new Material(shader) { hideFlags = HideFlags.DontSave };
                bundle.Unload(false);
                Plugin.MyLog.LogInfo("[VR FSR] hidden-area mask cleaned out of the FSR2/3 inputs");
                return true;
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogWarning("[VR FSR] fsrmask bundle failed to load: " + exception.Message);
                return false;
            }
        }
    }
}
