using System;
using System.Collections;
using System.Collections.Generic;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Source.Graphics
{
    // The desktop window, for recording: one eye's finished image (VR Graphics: "Desktop View Uses Right Eye"), cropped to the
    // window's shape and stabilised. Unity's own mirror copies the whole eye texture: nearly square, the headset's wide field of
    // view, the lenses' black corners, and every head movement. Instead a virtual camera at the eye follows the head's turning
    // smoothed (with a cap on how far it can lag, so the crop stays inside the eye image), and every window point is looked up in
    // the eye image through that eye's own view and projection. Rotation only: where the eye was is what the image shows.
    // Drawn at the end of the frame, after every camera: no camera of its own, so other mods' per-camera work doesn't run for it.
    public class DesktopMirror : MonoBehaviour
    {
        public static bool stabilized = true;           // false = Unity's own mirror of the eye texture
        public static float fieldOfView = 80f;          // deg across the window
        public static float smoothingTime = 0.15f;      // s the view takes to catch up with a head turn (0 = follows it exactly)
        public static float maxLagDegrees = 12f;        // how far it can trail the head (more = smoother, but the crop nears the eye image's edge)
        public static float resetDegrees = 60f;         // further off than this at once (a snap of the camera, a new scene): no catching up, it's there
        public static float edgeMargin = 0.05f;         // share of the eye image kept clear at each edge: the lenses' black mask comes in there
        public static int edgeChecks = 4;               // points checked along each side of the window to keep it inside the eye image
        public static bool flipVertical = true;         // the eye texture is stored top row first (headset: upside down without it)
        public static int gridColumns = 24;             // the window is drawn as a grid; each corner looked up exactly, straight in between
        public static int gridRows = 14;

        // The game's (Unity 2022) XRMirrorViewBlitMode values: Default 0 = Unity's own mirror, None -6. Not the constants: they compile
        // in from the 2019 references this builds against, where None is 0 (= 2022's Default).
        private const int UnityMirror = 0;
        private const int NoMirror = -6;
        private static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        private int mirrorModeSet = int.MinValue;
        private XRDisplaySubsystem mirrorModeSetOn;
        private Material material;
        private Quaternion smoothedRotation = Quaternion.identity;
        private bool haveSmoothedRotation;
        private Camera smoothedFor;
        private bool loggedStart;
        private bool loggedError;
        private bool loggedTooWide;

        private IEnumerator Start()
        {
            WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;
                try
                {
                    DrawWindow();
                }
                catch (Exception exception)
                {
                    if (!loggedError)
                    {
                        loggedError = true;
                        Plugin.MyLog.LogError($"[DesktopMirror] drawing the window failed, Unity's mirror instead: {exception}");
                    }
                    stabilized = false;
                    SetMirrorMode(Display(), UnityMirror);
                }
            }
        }

        private void DrawWindow()
        {
            XRDisplaySubsystem display = Display();
            if (display == null)
                return;
            if (!stabilized || Screen.width <= 0 || Screen.height <= 0 || !TryGetEye(display, out RenderTexture eyeTexture, out Camera eyeCamera, out Camera.StereoscopicEye eye))
            {
                SetMirrorMode(display, UnityMirror);
                haveSmoothedRotation = false;
                return;
            }
            if (!EnsureMaterial())
            {
                stabilized = false;
                SetMirrorMode(display, UnityMirror);
                return;
            }
            SetMirrorMode(display, NoMirror);

            Matrix4x4 view = eyeCamera.GetStereoViewMatrix(eye);
            Matrix4x4 projection = eyeCamera.GetStereoProjectionMatrix(eye);
            Quaternion eyeRotation = RotationOfView(view);
            float tanHalfAcross = Mathf.Tan(Mathf.Clamp(fieldOfView, 10f, 150f) * 0.5f * Mathf.Deg2Rad);
            float tanHalfUp = tanHalfAcross * Screen.height / Screen.width;
            Smooth(eyeRotation, eyeCamera);
            KeepInsideEyeImage(eyeRotation, view, projection, tanHalfAcross, tanHalfUp);

            if (!loggedStart)
            {
                loggedStart = true;
                Plugin.MyLog.LogInfo($"[DesktopMirror] stabilised window from the {eye} eye ({eyeTexture.width}x{eyeTexture.height} {eyeTexture.format}) of '{eyeCamera.name}', "
                    + $"window {Screen.width}x{Screen.height}, {fieldOfView:0} deg across, render passes {display.GetRenderPassCount()}");
            }
            Draw(eyeTexture, view, projection, tanHalfAcross, tanHalfUp);
        }

        // MultiPass: one render pass per eye, each its own texture (pass 0 left, 1 right). Single-pass would be one texture array,
        // which this can't sample: Unity's mirror then.
        private static bool TryGetEye(XRDisplaySubsystem display, out RenderTexture eyeTexture, out Camera eyeCamera, out Camera.StereoscopicEye eye)
        {
            eyeTexture = null;
            eye = VRSettings.GetDesktopViewRightEye() ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            eyeCamera = EyeCamera();
            if (eyeCamera == null || display.GetRenderPassCount() < 2)
                return false;
            eyeTexture = display.GetRenderTextureForRenderPass(eye == Camera.StereoscopicEye.Right ? 1 : 0);
            return eyeTexture != null && eyeTexture.dimension == UnityEngine.Rendering.TextureDimension.Tex2D;
        }

        // The camera that rendered the eyes: the headset camera in a raid, Camera.main in the hideout and the menus (VRCam is unset there)
        private static Camera EyeCamera()
        {
            Camera camera = VRGlobals.VRCam;
            if (camera != null && camera.isActiveAndEnabled && camera.stereoEnabled)
                return camera;
            camera = Camera.main;
            return camera != null && camera.isActiveAndEnabled && camera.stereoEnabled ? camera : null;
        }

        // The eye's turn as a transform's rotation (+Z forward) from its view matrix (which looks down -Z)
        private static Quaternion RotationOfView(Matrix4x4 view)
        {
            Matrix4x4 cameraToWorld = view.inverse;
            Vector3 forward = -(Vector3)cameraToWorld.GetColumn(2);
            Vector3 up = cameraToWorld.GetColumn(1);
            return forward.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(forward, up) : Quaternion.identity;
        }

        private void Smooth(Quaternion eyeRotation, Camera eyeCamera)
        {
            if (!haveSmoothedRotation || smoothedFor != eyeCamera || Quaternion.Angle(smoothedRotation, eyeRotation) > resetDegrees)
            {
                smoothedRotation = eyeRotation;
                haveSmoothedRotation = true;
                smoothedFor = eyeCamera;
                return;
            }
            if (smoothingTime > 0f)
                smoothedRotation = Quaternion.Slerp(smoothedRotation, eyeRotation, 1f - Mathf.Exp(-Time.unscaledDeltaTime / smoothingTime));
            else
                smoothedRotation = eyeRotation;
            if (Quaternion.Angle(smoothedRotation, eyeRotation) > maxLagDegrees)
                smoothedRotation = Quaternion.RotateTowards(eyeRotation, smoothedRotation, maxLagDegrees);
        }

        // Each eye's image reaches less far toward the nose than outward, and the trailing view moved the crop toward it on a turn
        // (headset: turning left with the left eye, the black mask came in on the right). The window's whole border has to land inside
        // the eye image, less edgeMargin: where it wouldn't, the view trails less, just as far as fits (between the head's own turn
        // and the smoothed one).
        private void KeepInsideEyeImage(Quaternion eyeRotation, Matrix4x4 view, Matrix4x4 projection, float tanHalfAcross, float tanHalfUp)
        {
            if (BorderInsideEyeImage(smoothedRotation, view, projection, tanHalfAcross, tanHalfUp))
                return;
            float fits = 0f;
            float doesNot = 1f;
            for (int step = 0; step < 8; step++)
            {
                float middle = (fits + doesNot) * 0.5f;
                if (BorderInsideEyeImage(Quaternion.Slerp(eyeRotation, smoothedRotation, middle), view, projection, tanHalfAcross, tanHalfUp))
                    fits = middle;
                else
                    doesNot = middle;
            }
            smoothedRotation = Quaternion.Slerp(eyeRotation, smoothedRotation, fits);
            if (fits <= 0f && !loggedTooWide && !BorderInsideEyeImage(eyeRotation, view, projection, tanHalfAcross, tanHalfUp))
            {
                loggedTooWide = true;
                Plugin.MyLog.LogWarning($"[DesktopMirror] {fieldOfView:0} deg across is wider than this eye's image: its edges show even looking straight ahead");
            }
        }

        private bool BorderInsideEyeImage(Quaternion rotation, Matrix4x4 view, Matrix4x4 projection, float tanHalfAcross, float tanHalfUp)
        {
            int checks = Mathf.Max(1, edgeChecks);
            for (int index = 0; index <= checks; index++)
            {
                float along = (float)index / checks;
                if (!InsideEyeImage(EyeImagePoint(rotation, along, 0f, tanHalfAcross, tanHalfUp, view, projection))
                    || !InsideEyeImage(EyeImagePoint(rotation, along, 1f, tanHalfAcross, tanHalfUp, view, projection))
                    || !InsideEyeImage(EyeImagePoint(rotation, 0f, along, tanHalfAcross, tanHalfUp, view, projection))
                    || !InsideEyeImage(EyeImagePoint(rotation, 1f, along, tanHalfAcross, tanHalfUp, view, projection)))
                    return false;
            }
            return true;
        }

        private static bool InsideEyeImage(Vector2 point)
        {
            return point.x >= edgeMargin && point.x <= 1f - edgeMargin && point.y >= edgeMargin && point.y <= 1f - edgeMargin;
        }

        // A window point (0..1 across and up): its direction from a camera turned this way, where that direction is in the eye image
        // (0..1, the eye's own projection; the texture's row order aside)
        private static Vector2 EyeImagePoint(Quaternion rotation, float across, float up, float tanHalfAcross, float tanHalfUp, Matrix4x4 view, Matrix4x4 projection)
        {
            Vector3 direction = rotation * new Vector3((across * 2f - 1f) * tanHalfAcross, (up * 2f - 1f) * tanHalfUp, 1f);
            Vector3 inView = view.MultiplyVector(direction);
            Vector4 clip = projection * new Vector4(inView.x, inView.y, inView.z, 0f);
            return clip.w > 1e-5f ? new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f) : new Vector2(-1f, -1f);
        }

        private void Draw(RenderTexture eyeTexture, Matrix4x4 view, Matrix4x4 projection, float tanHalfAcross, float tanHalfUp)
        {
            int columns = Mathf.Max(1, gridColumns);
            int rows = Mathf.Max(1, gridRows);

            RenderTexture.active = null;
            GL.PushMatrix();
            GL.LoadOrtho();
            GL.Clear(true, true, Color.black);
            material.mainTexture = eyeTexture;
            material.SetPass(0);
            GL.Begin(GL.QUADS);
            for (int row = 0; row < rows; row++)
            {
                float y0 = (float)row / rows;
                float y1 = (float)(row + 1) / rows;
                for (int column = 0; column < columns; column++)
                {
                    float x0 = (float)column / columns;
                    float x1 = (float)(column + 1) / columns;
                    Corner(x0, y0, tanHalfAcross, tanHalfUp, view, projection);
                    Corner(x0, y1, tanHalfAcross, tanHalfUp, view, projection);
                    Corner(x1, y1, tanHalfAcross, tanHalfUp, view, projection);
                    Corner(x1, y0, tanHalfAcross, tanHalfUp, view, projection);
                }
            }
            GL.End();
            GL.PopMatrix();
        }

        // A grid corner, from the smoothed camera
        private void Corner(float across, float up, float tanHalfAcross, float tanHalfUp, Matrix4x4 view, Matrix4x4 projection)
        {
            Vector2 uv = EyeImagePoint(smoothedRotation, across, up, tanHalfAcross, tanHalfUp, view, projection);
            if (flipVertical)
                uv.y = 1f - uv.y;
            GL.TexCoord2(uv.x, uv.y);
            GL.Vertex3(across, up, 0f);
        }

        private bool EnsureMaterial()
        {
            if (material != null)
                return true;
            Shader shader = Shader.Find("Hidden/BlitCopy");
            if (shader == null)
                shader = Shader.Find("Unlit/Texture");
            if (shader == null)
            {
                Plugin.MyLog.LogError("[DesktopMirror] no blit shader found, Unity's mirror instead");
                return false;
            }
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        private static XRDisplaySubsystem Display()
        {
#pragma warning disable CS0618 // GetInstances: the 2019 references' name; still in the game's 2022
            SubsystemManager.GetInstances(displays);
#pragma warning restore CS0618
            foreach (XRDisplaySubsystem display in displays)
            {
                if (display.running)
                    return display;
            }
            return null;
        }

        private void SetMirrorMode(XRDisplaySubsystem display, int mode)
        {
            if (display == null || (mirrorModeSet == mode && mirrorModeSetOn == display))
                return;
            display.SetPreferredMirrorBlitMode(mode);
            mirrorModeSet = mode;
            mirrorModeSetOn = display;
        }
    }
}
