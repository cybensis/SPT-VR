using EFT;
using EFT.Rendering.Clouds;
using EFT.Weather;
using HarmonyLib;
using UnityEngine;

namespace TarkovVR.Patches.Visuals
{
    [HarmonyPatch]
    internal class WeatherPatches
    {
        // Cache for expensive operations
        private static Camera fpsCam;
        private static PrismEffects fpsPrism;

        // Wind system state
        public static float lastWind = 0.0f;
        public static Vector4 cloudOffset = Vector4.zero;
        public static Vector2 lastWindDirection = new Vector2(1f, 0f);


        // Tarkov's clouds (CloudController + CloudLayerRenderer, a port of HDRP's Cloud Layer) are a full-screen pass at
        // AfterForwardOpaque whose only view input is _PixelCoordToViewDirWS (pixel -> sky direction; the shader reads no camera
        // position and no depth). Vanilla builds it from the camera's mono projection with a centred lens: in VR each eye's
        // frustum is off-centre, so both eyes got one pixel -> direction map = the clouds in a different place per eye. Rebuilt
        // per eye below (OnPreRenderCamera runs once per eye in MultiPass). CloudSix skips RenderClouds itself.
        public static bool vanillaClouds = true;    // false = Tarkov's clouds not drawn (their shadow cookie stays)

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CloudLayerRenderer), "RenderClouds")]
        private static bool ShowVanillaClouds()
        {
            return vanillaClouds;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CloudController), "UpdateParameters")]
        private static void CloudDirectionsPerEye(BuiltinSkyParameters parameters, Camera cam)
        {
            if (cam == null || !cam.stereoEnabled || cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Mono)
                return;
            Camera.StereoscopicEye eye = cam.stereoActiveEye == Camera.MonoOrStereoscopicEye.Right ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            Matrix4x4 projection = cam.GetStereoProjectionMatrix(eye);
            Matrix4x4 view = cam.GetStereoViewMatrix(eye);
            // The eye's frustum the way the helper takes it: vertical FOV, aspect and the off-centre lens shift (m02 / m12 = 2 x shift)
            float verticalFov = 2f * Mathf.Atan(1f / projection.m11);
            float aspect = projection.m11 / projection.m00;
            Vector2 lensShift = new Vector2(projection.m02 * 0.5f, projection.m12 * 0.5f);
            parameters.PixelCoordToViewDirMatrix = RenderingUtils.ComputePixelCoordToWorldSpaceViewDirectionMatrix(
                verticalFov, lensShift, parameters.ScreenSize, view, false, aspect);
            parameters.ViewMatrix = view;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TOD_Camera), "Update")]
        private static bool FixTODCamera(TOD_Camera __instance)
        {
            if ((bool)__instance.sky && __instance.sky.Initialized)
            {
                __instance.sky.Components.Camera = __instance;
            }
            return false;
            //__instance.enabled = false;
        }

        //Tarkov fog doesn't render correctly in VR so disable it
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MBOIT_Scattering), "Start")]
        private static void DisableMBOIT(CloudController __instance)
        {
            //__instance.enabled = false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PrismEffects), "SetFogShaderValues")]
        private static bool FixFogForVR(PrismEffects __instance, Material fogMaterial)
        {
            Camera camera = __instance.GetPrismCamera();

            fogMaterial.SetFloat("_FogHeight", __instance.fogHeight);
            fogMaterial.SetFloat("_FogIntensity", 1f);
            fogMaterial.SetFloat("_FogDistance", __instance.fogDistance);
            fogMaterial.SetFloat("_FogStart", __instance.fogStartPoint);
            fogMaterial.SetColor("_FogColor", __instance.fogColor);
            fogMaterial.SetColor("_FogEndColor", __instance.fogEndColor);
            fogMaterial.SetFloat("_FogBlurSkybox", 0.9999999f);
            Matrix4x4 worldMatrix = Matrix4x4.TRS(
                camera.transform.position,
                Quaternion.identity,
                Vector3.one
            );

            fogMaterial.SetMatrix(Shader.PropertyToID("_InverseView"), worldMatrix);

            return false;
        }

        // Forces the fps PrismEffects fog off. FogSix fogs the scope through the optic's own tod_Scattering_1:
        // don't add a PrismEffects fog on the scope, it double-fogs it.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeatherController), "ControlScattering")]
        private static void DynamicFog(WeatherController __instance, float fog, ShowStatNotificationPacket interpolatedParams)
        {
            if (fpsCam == null)
            {
                InitializeCameras();
            }

            if (fpsCam == null || __instance.tod_Scattering_0 == null)
                return;

            if (fpsPrism == null)
            {
                fpsPrism = fpsCam.GetComponent<PrismEffects>() ?? fpsCam.gameObject.AddComponent<PrismEffects>();
            }

            fpsPrism.useFog = false;
        }
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PrismEffects), "method_12")]
        private static void ApplyFXAAAfterAllEffects(PrismEffects __instance, RenderTexture source, RenderTexture destination)
        {
            Rendering.LoadFXAAShader();
            if (Rendering._fxaaMat == null || !__instance.useFog) return;

            RenderTexture temp = RenderTexture.GetTemporary(destination.descriptor);
            Graphics.Blit(destination, temp);
            Graphics.Blit(temp, destination, Rendering._fxaaMat);
            RenderTexture.ReleaseTemporary(temp);
        }
        */
        private static void InitializeCameras()
        {
            foreach (var camera in Camera.allCameras)
            {
                if (camera.name == "FPS Camera")
                    fpsCam = camera;
            }
        }
    }

}
