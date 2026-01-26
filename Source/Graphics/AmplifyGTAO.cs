using EFT.Settings.Graphics;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace TarkovVR.Source.Graphics
{
    public class AmplifyGTAO : MonoBehaviour
    {
        private static AssetBundle amplifyAOBundle; // Keep reference - don't unload shader bundles
        private Camera currentCamera;
        private static AmplifyOcclusionEffect aoEffect;

        public static Material occlusionMat;
        public static Material blurMat;
        public static Material applyMat;
        
        public static Shader occlusionShader;
        public static Shader blurShader;
        public static Shader applyShader;

        private static float lastUpscale;

        private void Awake()
        {
            LoadAmplifyAOShaders();

            if (occlusionShader == null || blurShader == null || applyShader == null)
            {
                Plugin.MyLog.LogError("AO shaders failed to load, disabling AO");
                Destroy(this);
                return;
            }

            occlusionMat = new Material(occlusionShader) { hideFlags = HideFlags.DontSave };
            blurMat = new Material(blurShader) { hideFlags = HideFlags.DontSave };
            applyMat = new Material(applyShader) { hideFlags = HideFlags.DontSave };

            currentCamera = GetComponent<Camera>();
            aoEffect = currentCamera.gameObject.AddComponent<AmplifyOcclusionEffect>();
            SetAOSettings(VRGlobals.ssaoMode);
        }
        private static void LoadAmplifyAOShaders()
        {
            if (amplifyAOBundle != null)
                return;

            try
            {
                string bundlePath = Path.Combine(BepInEx.Paths.PluginPath, "sptvr", "Assets", "amplifyao");
                amplifyAOBundle = AssetBundle.LoadFromFile(bundlePath);

                if (amplifyAOBundle == null)
                {
                    Plugin.MyLog.LogError("Failed to load Amplify AO AssetBundle.");
                    return;
                }

                // List all asset names in the bundle
                string[] assetNames = amplifyAOBundle.GetAllAssetNames();
                foreach (string name in assetNames)
                {
                    Plugin.MyLog.LogInfo($"Asset in bundle: {name}");
                }

                // Load all shaders and log their actual names
                Shader[] allShaders = amplifyAOBundle.LoadAllAssets<Shader>();
                foreach (Shader s in allShaders)
                {
                    Plugin.MyLog.LogInfo($"Shader found: {s.name}");

                    // Assign based on actual shader name
                    if (s.name == "Hidden/Amplify Occlusion/Occlusion")
                        occlusionShader = s;
                    else if (s.name == "Hidden/Amplify Occlusion/Blur")
                        blurShader = s;
                    else if (s.name == "Hidden/Amplify Occlusion/Apply")
                        applyShader = s;
                }
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"Error loading Amplify AO shaders: {ex.Message}");
            }
        }
        public void SetAOSettings(ESSAOMode ssaoMode)
        {
            if (aoEffect == null)
                return;

            if (ssaoMode == ESSAOMode.Off)
            {
                aoEffect.enabled = false;
                return;
            }

            aoEffect.enabled = true;

            SampleCountLevel sampleCountLevel = ssaoMode switch
            {
                ESSAOMode.FastestPerformance => SampleCountLevel.Low,
                ESSAOMode.FastPerformance => SampleCountLevel.Medium,
                ESSAOMode.HighQuality => SampleCountLevel.High,
                ESSAOMode.HighestQuality => SampleCountLevel.VeryHigh,
                ESSAOMode.ColoredHighestQuality => SampleCountLevel.VeryHigh,
                _ => SampleCountLevel.Medium
            };

            if (aoEffect != null)
            {
                aoEffect.SampleCount = sampleCountLevel;
                aoEffect.Radius = 0.8f;
                aoEffect.Bias = 0.1f;
                aoEffect.Thickness = 0.35f;
                aoEffect.Intensity = 0.5f;
                aoEffect.PowerExponent = 1.0f;
                aoEffect.Downsample = false;
                aoEffect.CacheAware = true;
                aoEffect.BlurEnabled = true;
                aoEffect.BlurRadius = 2;
                aoEffect.BlurSharpness = 20;
                aoEffect.BlurPasses = 2;
                aoEffect.FilterEnabled = false;
                aoEffect.FilterBlending = 0.15f;
                aoEffect.FilterDownsample = false;
                aoEffect.FadeEnabled = true;
                aoEffect.FadeStart = 20f;
                aoEffect.FadeLength = 50f;
                aoEffect.FadeToIntensity = 0f;
                aoEffect.Tint = new Color32(12, 12, 12, 255);
                aoEffect.ApplyMethod = AmplifyOcclusionEffect.ApplicationMethod.PostEffect;
                aoEffect.PerPixelNormals = AmplifyOcclusionEffect.PerPixelNormalSource.Camera;
            }
        }
    }
}
