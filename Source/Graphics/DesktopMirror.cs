using System;
using System.Collections;
using System.Collections.Generic;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.XR;

namespace TarkovVR.Source.Graphics
{
    // Draws the desktop window, for recording. It shows one eye's finished image (VR menu, Desktop View: "Show The Right Eye"),
    // cropped to the window's shape and stabilised. Unity's own mirror copies the whole eye texture: nearly square, with the lens
    // corners, and moving with every head movement.
    // A virtual camera at the eye follows the head's rotation, smoothed, with a capped lag that never lets the window reach the
    // lens mask. Every window point is looked up in the eye image through that eye's own view and projection. Rotation only.
    // The field of view (horizontal, and vertical if set apart from the window's shape), smoothing and a downward tilt are VR menu
    // sliders (Desktop View).
    // The window is drawn after the eyes and before the screen overlays (UnityExplorer's canvases, BepInEx's F12 menu), where the
    // chosen eye's picture is finished (see OnAnyCameraPostRender / AfterFinalImage). A frame that wasn't drawn there is drawn at
    // the very end of the frame instead, over the overlays.
    public class DesktopMirror : MonoBehaviour
    {
        public static bool stabilized = true;           // false = Unity's own mirror of the eye texture (also set when drawing fails)
        // The VR menu's "Plain Mirror" option (Desktop View) switches to Unity's own mirror
        internal static void LogDetail(string message) { if (logDetails) Plugin.MyLog.LogInfo(message); }
        private static bool Wanted => stabilized && !VRSettings.GetDesktopViewPlainMirror();
        public static float maxLagDegrees = 12f;        // how far the view can trail the head (more = smoother, but the crop nears the eye image's edge)
        public static float resetDegrees = 60f;         // a jump bigger than this (a camera snap, a new scene) resets the smoothing instead of catching up
        public static float edgeMargin = 0.03f;         // share of the eye image kept clear at each edge
        public static int lensMaskMarginCells = 3;      // the lens mask is grown by this many of its 128 cells (about 2% of the eye image)
        public static int edgeChecks = 6;               // points checked along each side of the window to keep it inside the eye image
        public static int gridColumns = 24;             // the window is drawn as a grid: each corner looked up exactly, straight lines in between
        public static int gridRows = 14;
        public static bool logDetails = false;         // setup and drawing-path lines ("[DesktopMirror]"); warnings always

        // The game's (Unity 2022) XRMirrorViewBlitMode values: Default 0 = Unity's own mirror, None -6. The named constants aren't used
        // because they compile in from the 2019 references this builds against, where None is 0 (2022's Default). The render pass
        // structs below are safe: their layout comes from the game's assembly at run time, and only fields both versions have are read.
        private const int UnityMirror = 0;
        private const int NoMirror = -6;
        private const int LensMaskSize = 128;

        private static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        private int mirrorModeSet = int.MinValue;
        private XRDisplaySubsystem mirrorModeSetOn;
        private Material material;
        private Quaternion smoothedRotation = Quaternion.identity;   // the head's turn, smoothed
        private Quaternion viewTilt = Quaternion.identity;           // the window's tilt relative to that (the tilt slider)
        private bool haveSmoothedRotation;
        private Camera smoothedFor;
        private bool loggedStart;
        private bool loggedError;
        private bool loggedTooWide;
        private int drawnFrame = -1;                    // the frame the window was drawn in (only once a frame: the smoothing steps with each draw)
        private string cameraFailure;                   // why the draw after the eyes didn't happen this frame
        private bool loggedCameraFailure;
        private bool loggedEndOfFrameDraw;

        // The window is drawn without a camera of its own (Unity's work for any camera costs ~0.5 ms of main thread a frame),
        // at the point where the chosen eye's picture is finished:
        //   If the last camera drawing into the headset has the game's final step (SSAAImpl; uiCam skipped), it is drawn right
        //     after that step (Rendering's SSAAImpl.OnRenderImage postfix calls AfterFinalImage), from the eye image.
        //   If it is another camera (uiCam), it is drawn at that camera's post-render, from its own working texture. The camera
        //     copies that texture into the eye image only after post-render, so the eye image there still holds the game's picture
        //     without the UI, stored upside down. The working texture holds the whole picture, stored like any render texture.
        internal static DesktopMirror instance;
        private RenderTexture sourceOverride;           // the picture drawn from, when it isn't the eye image
        private bool sourceTopRowFirst = true;          // the eye image is stored top row first; Unity's render textures the other way round
        private bool drawingAfterEye;
        private Camera[] cameraList = new Camera[16];
        private Camera lastEyeCamera;                   // the last camera drawing into the headset this frame
        private int lastEyeCameraFrame = -1;
        private Camera finalStepCamera;                 // the last camera checked for the game's final step, and its SSAAImpl
        private SSAAImpl finalStep;
        private readonly HashSet<long> loggedPaths = new HashSet<long>();

        // Which parts of each eye's image the lens mask covers (0..1 both ways, rows counted up from the bottom of the view)
        private sealed class LensMask
        {
            public Mesh mesh;
            public bool[] hidden;
            public bool checkedMesh;
        }
        private readonly LensMask[] lensMasks = { new LensMask(), new LensMask() };
        private LensMask currentLensMask;

        // This component must sit on the plugin's own object. On a separate object none of it ran, not even the end-of-frame draw.
        private void Awake()
        {
            instance = this;
        }

        private void OnEnable() { Camera.onPostRender += OnAnyCameraPostRender; }
        private void OnDisable() { Camera.onPostRender -= OnAnyCameraPostRender; }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Drawn after the eyes

        // Draws at the chosen eye's post-render of the last camera drawing into the headset, unless that camera has the game's
        // final step, which runs after post-render (AfterFinalImage draws then)
        private void OnAnyCameraPostRender(Camera camera)
        {
            if (camera == null || drawnFrame == Time.frameCount || !Wanted)
                return;
            if (camera != LastEyeCamera() || camera.stereoActiveEye != ChosenEye() || HasFinalStep(camera))
                return;
            RenderTexture working = camera.activeTexture;
            RenderTexture eyeImage = EyeImage(camera.stereoActiveEye);
            bool ownTexture = working != null && working != eyeImage;
            DrawAfterEye(camera, ownTexture ? working : null, ownTexture ? "at its post-render, from its own working texture" : "at its post-render, from the eye image", ownTexture ? 1 : 2);
        }

        // Called right after the game's final step drew the picture (from Rendering's SSAAImpl.OnRenderImage postfix)
        internal static void AfterFinalImage(Camera camera, RenderTexture destination)
        {
            DesktopMirror mirror = instance;
            if (mirror == null || camera == null || mirror.drawnFrame == Time.frameCount || !Wanted)
                return;
            if (camera != mirror.LastEyeCamera() || camera.stereoActiveEye != ChosenEye())
                return;
            // If the step drew into the eye image (null = the camera's own target, which is the eye image), the picture is finished
            // there. If it drew into a texture of its own, that texture holds the picture (Unity copies it on afterwards), stored
            // like any render texture.
            bool intoEyeImage = destination == null || destination == mirror.EyeImage(camera.stereoActiveEye)
                || (destination.name != null && destination.name.StartsWith("XR Texture"));
            mirror.DrawAfterEye(camera, intoEyeImage ? null : destination,
                intoEyeImage ? "after its final step, from the eye image" : "after its final step, from its own texture", intoEyeImage ? 3 : 4);
        }

        private void DrawAfterEye(Camera camera, RenderTexture source, string where, int path)
        {
            RenderTexture active = RenderTexture.active;
            sourceOverride = source;
            sourceTopRowFirst = source == null;
            drawingAfterEye = true;
            if (loggedPaths.Add(((long)camera.GetInstanceID() << 8) | (long)path))
                LogDetail($"[DesktopMirror] drawn {where}: '{camera.name}' (depth {camera.depth:0}), {camera.stereoActiveEye} eye, "
                    + $"{(source != null ? $"'{source.name}' {source.width}x{source.height}" : "eye image")}, rows {(sourceTopRowFirst ? "turned over" : "as stored")}");
            try
            {
                cameraFailure = Guarded(fromCamera: true);
            }
            finally
            {
                drawingAfterEye = false;
                sourceOverride = null;
                RenderTexture.active = active;   // restore the camera's own target for whatever Unity does next
            }
        }

        private static Camera.MonoOrStereoscopicEye ChosenEye() =>
            VRSettings.GetDesktopViewRightEye() ? Camera.MonoOrStereoscopicEye.Right : Camera.MonoOrStereoscopicEye.Left;

        private RenderTexture EyeImage(Camera.MonoOrStereoscopicEye eye)
        {
            XRDisplaySubsystem display = Display();
            return display != null && display.GetRenderPassCount() >= 2
                ? display.GetRenderTextureForRenderPass(eye == Camera.MonoOrStereoscopicEye.Right ? 1 : 0) : null;
        }

        private bool HasFinalStep(Camera camera)
        {
            if (camera != finalStepCamera)
            {
                finalStepCamera = camera;
                finalStep = camera.GetComponent<SSAAImpl>();
            }
            return finalStep != null && finalStep.isActiveAndEnabled;
        }

        // The highest-depth enabled camera drawing into the headset (stereo, no texture of its own), found once a frame
        private Camera LastEyeCamera()
        {
            if (lastEyeCameraFrame == Time.frameCount)
                return lastEyeCamera;
            lastEyeCameraFrame = Time.frameCount;
            lastEyeCamera = null;
            if (Camera.allCamerasCount > cameraList.Length)
                cameraList = new Camera[Camera.allCamerasCount + 8];
            int count = Camera.GetAllCameras(cameraList);
            for (int index = 0; index < count; index++)
            {
                Camera candidate = cameraList[index];
                cameraList[index] = null;
                if (candidate == null || !candidate.stereoEnabled || candidate.targetTexture != null
                    || candidate.stereoTargetEye == StereoTargetEyeMask.None)
                    continue;
                if (lastEyeCamera == null || candidate.depth > lastEyeCamera.depth)
                    lastEyeCamera = candidate;
            }
            return lastEyeCamera;
        }

        // A frame that wasn't drawn after the eyes is drawn here at the very end (over the overlays), or falls back to Unity's mirror
        private IEnumerator Start()
        {
            LogDetail("[DesktopMirror] end-of-frame draw running");
            WaitForEndOfFrame endOfFrame = new WaitForEndOfFrame();
            string loggedFailure = null;
            while (true)
            {
                yield return endOfFrame;
                bool wanted = Wanted;
                if (drawnFrame == Time.frameCount)
                    continue;
                string failure = Guarded(fromCamera: false);
                if (failure != null && failure != loggedFailure && failure != "switched off")
                {
                    loggedFailure = failure;
                    LogDetail($"[DesktopMirror] not drawn at the end of the frame either ({failure}): Unity's mirror");
                }
                if (failure != null || !wanted)
                    continue;
                if (!loggedEndOfFrameDraw)
                {
                    loggedEndOfFrameDraw = true;
                    string why = loggedPaths.Count > 0 ? cameraFailure ?? "no reason" : $"no draw after the last headset camera's eye ('{(LastEyeCamera() != null ? LastEyeCamera().name : "none")}')";
                    Plugin.MyLog.LogWarning($"[DesktopMirror] not drawn after the eyes ({why}): "
                        + "drawn at the end of the frame instead, over the F7 / F12 menus");
                }
            }
        }

        private string Guarded(bool fromCamera)
        {
            try
            {
                return DrawWindow(fromCamera);
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
                return "failed";
            }
        }

        // Returns null when drawn, else why not. An attempt from a camera never switches back to Unity's mirror; the end-of-frame
        // attempt decides that, once a frame.
        private string DrawWindow(bool fromCamera)
        {
            if (drawnFrame == Time.frameCount)
                return null;
            XRDisplaySubsystem display = Display();
            RenderTexture eyeTexture = null;
            Camera eyeCamera = null;
            Camera.StereoscopicEye eye = Camera.StereoscopicEye.Left;
            string failure = display == null ? "no running XR display"
                : !Wanted ? "switched off"
                : Screen.width <= 0 || Screen.height <= 0 ? "no window"
                : TryGetEye(display, out eyeTexture, out eyeCamera, out eye);
            if (failure == null && sourceOverride != null)
                eyeTexture = sourceOverride;   // after an eye pass: the texture where the picture is finished (DrawAfterEye)
            if (failure == null && !EnsureMaterial())
            {
                stabilized = false;
                failure = "no blit shader";
            }
            if (failure != null)
            {
                if (fromCamera && !loggedCameraFailure)
                {
                    loggedCameraFailure = true;
                    Plugin.MyLog.LogWarning($"[DesktopMirror] {(drawingAfterEye ? "can't draw after the eyes" : "the window camera can't draw")}: {failure}");
                }
                if (!fromCamera)
                {
                    SetMirrorMode(display, UnityMirror);
                    haveSmoothedRotation = false;
                }
                return failure;
            }
            drawnFrame = Time.frameCount;
            SetMirrorMode(display, NoMirror);

            int pass = eye == Camera.StereoscopicEye.Right ? 1 : 0;
            currentLensMask = LensMaskOf(display, pass, eyeCamera);
            Matrix4x4 view = eyeCamera.GetStereoViewMatrix(eye);
            Matrix4x4 projection = eyeCamera.GetStereoProjectionMatrix(eye);
            Quaternion eyeRotation = RotationOfView(view);
            float fieldOfView = VRSettings.GetDesktopViewFieldOfView();
            float tanHalfAcross = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            float verticalFieldOfView = VRSettings.GetDesktopViewVerticalFieldOfView();
            float tanHalfUp = verticalFieldOfView > 0f ? Mathf.Tan(verticalFieldOfView * 0.5f * Mathf.Deg2Rad) : tanHalfAcross * Screen.height / Screen.width;
            // Positive pitch about the camera's own x turns it down
            viewTilt = Quaternion.Euler(VRSettings.GetDesktopViewTiltDown(), 0f, 0f);
            Smooth(eyeRotation, eyeCamera, VRSettings.GetDesktopViewSmoothing());
            KeepInsideEyeImage(eyeRotation, view, projection, tanHalfAcross, tanHalfUp, fieldOfView);

            if (!loggedStart)
            {
                loggedStart = true;
                Plugin.MyLog.LogInfo($"[DesktopMirror] stabilised window from the {eye} eye ({eyeTexture.width}x{eyeTexture.height} {eyeTexture.format}) of '{eyeCamera.name}', "
                    + $"window {Screen.width}x{Screen.height}, {fieldOfView:0} deg across, render passes {display.GetRenderPassCount()}");
            }
            Draw(eyeTexture, view, projection, tanHalfAcross, tanHalfUp);
            return null;
        }

        // MultiPass has one render pass per eye, each with its own texture (pass 0 left, 1 right). Single-pass would be one texture
        // array, which this can't sample, so Unity's mirror is used then. Returns null when found, else what's missing.
        private static string TryGetEye(XRDisplaySubsystem display, out RenderTexture eyeTexture, out Camera eyeCamera, out Camera.StereoscopicEye eye)
        {
            eyeTexture = null;
            eye = VRSettings.GetDesktopViewRightEye() ? Camera.StereoscopicEye.Right : Camera.StereoscopicEye.Left;
            eyeCamera = EyeCamera();
            if (eyeCamera == null)
                return "no headset camera";
            int passes = display.GetRenderPassCount();
            if (passes < 2)
                return $"{passes} render passes";
            eyeTexture = display.GetRenderTextureForRenderPass(eye == Camera.StereoscopicEye.Right ? 1 : 0);
            if (eyeTexture == null)
                return "no eye texture";
            return eyeTexture.dimension == UnityEngine.Rendering.TextureDimension.Tex2D ? null : $"eye texture is a {eyeTexture.dimension}";
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

        // The eye's rotation as a transform rotation (+Z forward), from its view matrix (which looks down -Z)
        private static Quaternion RotationOfView(Matrix4x4 view)
        {
            Matrix4x4 cameraToWorld = view.inverse;
            Vector3 forward = -(Vector3)cameraToWorld.GetColumn(2);
            Vector3 up = cameraToWorld.GetColumn(1);
            return forward.sqrMagnitude > 1e-8f && up.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(forward, up) : Quaternion.identity;
        }

        private void Smooth(Quaternion eyeRotation, Camera eyeCamera, float smoothingTime)
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

        // Each eye's image reaches less far toward the nose than outward, and on a turn the trailing view moves the crop toward
        // that edge. The window's whole border must land inside the eye image (minus edgeMargin) and off the lens mask, which is
        // rounded, so the image's edges alone aren't enough. Where it doesn't fit, the view trails less: the rotation is moved
        // from the smoothed one toward the head's own, only as far as needed.
        private void KeepInsideEyeImage(Quaternion eyeRotation, Matrix4x4 view, Matrix4x4 projection, float tanHalfAcross, float tanHalfUp, float fieldOfView)
        {
            if (BorderInsideEyeImage(smoothedRotation * viewTilt, view, projection, tanHalfAcross, tanHalfUp))
                return;
            float fits = 0f;
            float doesNot = 1f;
            for (int step = 0; step < 8; step++)
            {
                float middle = (fits + doesNot) * 0.5f;
                if (BorderInsideEyeImage(Quaternion.Slerp(eyeRotation, smoothedRotation, middle) * viewTilt, view, projection, tanHalfAcross, tanHalfUp))
                    fits = middle;
                else
                    doesNot = middle;
            }
            smoothedRotation = Quaternion.Slerp(eyeRotation, smoothedRotation, fits);
            if (fits <= 0f && !loggedTooWide && !BorderInsideEyeImage(eyeRotation * viewTilt, view, projection, tanHalfAcross, tanHalfUp))
            {
                loggedTooWide = true;
                Plugin.MyLog.LogWarning($"[DesktopMirror] {fieldOfView:0} deg across ({2f * Mathf.Atan(tanHalfUp) * Mathf.Rad2Deg:0} up, tilted {VRSettings.GetDesktopViewTiltDown():0} down) "
                    + "reaches past this eye's image: its edges show even with the head still");
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

        private bool InsideEyeImage(Vector2 point)
        {
            if (point.x < edgeMargin || point.x > 1f - edgeMargin || point.y < edgeMargin || point.y > 1f - edgeMargin)
                return false;
            if (currentLensMask == null || currentLensMask.hidden == null)
                return true;
            int column = Mathf.Clamp((int)(point.x * LensMaskSize), 0, LensMaskSize - 1);
            int row = Mathf.Clamp((int)(point.y * LensMaskSize), 0, LensMaskSize - 1);
            return !currentLensMask.hidden[row * LensMaskSize + column];
        }

        // Takes a window point (0..1 across and up), finds its direction from a camera with this rotation, and returns where that
        // direction lands in the eye image (0..1, through the eye's own projection; up = up in the view, whatever the texture's row order)
        private static Vector2 EyeImagePoint(Quaternion rotation, float across, float up, float tanHalfAcross, float tanHalfUp, Matrix4x4 view, Matrix4x4 projection)
        {
            Vector3 direction = rotation * new Vector3((across * 2f - 1f) * tanHalfAcross, (up * 2f - 1f) * tanHalfUp, 1f);
            Vector3 inView = view.MultiplyVector(direction);
            Vector4 clip = projection * new Vector4(inView.x, inView.y, inView.z, 0f);
            return clip.w > 1e-5f ? new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f) : new Vector2(-1f, -1f);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The lens mask is the eye's occlusion mesh (the triangles Unity draws black into the eye image where the lens can't show it),
        // read from that eye's render pass, rasterised once into a grid and grown by lensMaskMarginCells. Its vertices are 0..1 over the
        // eye image, rows in the texture's order (top first: flipVertical). Without a usable mesh (none, unreadable, other units) only the
        // image's edges are checked; that is logged once.

        private LensMask LensMaskOf(XRDisplaySubsystem display, int pass, Camera eyeCamera)
        {
            LensMask lensMask = lensMasks[pass];
            display.GetRenderPass(pass, out XRDisplaySubsystem.XRRenderPass renderPass);
            if (renderPass.GetRenderParameterCount() < 1)
                return lensMask;
            renderPass.GetRenderParameter(eyeCamera, 0, out XRDisplaySubsystem.XRRenderParameter renderParameter);
            Mesh mesh = renderParameter.occlusionMesh;
            if (lensMask.checkedMesh && lensMask.mesh == mesh)
                return lensMask;
            lensMask.checkedMesh = true;
            lensMask.mesh = mesh;
            lensMask.hidden = null;
            if (mesh == null)
            {
                LogDetail($"[DesktopMirror] eye {pass}: no lens mask from the headset, the image's edges only");
                return lensMask;
            }
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            Vector2 low = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 high = new Vector2(float.MinValue, float.MinValue);
            foreach (Vector3 vertex in vertices)
            {
                low = Vector2.Min(low, vertex);
                high = Vector2.Max(high, vertex);
            }
            if (triangles.Length < 3 || low.x < -0.01f || low.y < -0.01f || high.x > 1.01f || high.y > 1.01f)
            {
                Plugin.MyLog.LogWarning($"[DesktopMirror] eye {pass}: lens mask not usable ({triangles.Length / 3} triangles, x {low.x:0.00}..{high.x:0.00}, y {low.y:0.00}..{high.y:0.00}), the image's edges only");
                return lensMask;
            }
            bool[] hidden = new bool[LensMaskSize * LensMaskSize];
            for (int index = 0; index + 2 < triangles.Length; index += 3)
                FillTriangle(hidden, MaskPoint(vertices[triangles[index]]), MaskPoint(vertices[triangles[index + 1]]), MaskPoint(vertices[triangles[index + 2]]));
            lensMask.hidden = Grow(hidden, Mathf.Max(0, lensMaskMarginCells));
            int hiddenCells = 0;
            foreach (bool cell in lensMask.hidden)
            {
                if (cell)
                    hiddenCells++;
            }
            LogDetail($"[DesktopMirror] eye {pass}: lens mask {triangles.Length / 3} triangles, x {low.x:0.00}..{high.x:0.00}, y {low.y:0.00}..{high.y:0.00}, "
                + $"{100f * hiddenCells / hidden.Length:0}% of the image kept off (with the margin)");
            return lensMask;
        }

        // Converts a mesh vertex to the view's up direction (the mesh uses the texture's row order)
        private static Vector2 MaskPoint(Vector3 vertex)
        {
            return new Vector2(vertex.x, 1f - vertex.y);
        }

        private static void FillTriangle(bool[] hidden, Vector2 cornerA, Vector2 cornerB, Vector2 cornerC)
        {
            int columnLow = Mathf.Clamp((int)(Mathf.Min(cornerA.x, Mathf.Min(cornerB.x, cornerC.x)) * LensMaskSize), 0, LensMaskSize - 1);
            int columnHigh = Mathf.Clamp((int)(Mathf.Max(cornerA.x, Mathf.Max(cornerB.x, cornerC.x)) * LensMaskSize), 0, LensMaskSize - 1);
            int rowLow = Mathf.Clamp((int)(Mathf.Min(cornerA.y, Mathf.Min(cornerB.y, cornerC.y)) * LensMaskSize), 0, LensMaskSize - 1);
            int rowHigh = Mathf.Clamp((int)(Mathf.Max(cornerA.y, Mathf.Max(cornerB.y, cornerC.y)) * LensMaskSize), 0, LensMaskSize - 1);
            for (int row = rowLow; row <= rowHigh; row++)
            {
                for (int column = columnLow; column <= columnHigh; column++)
                {
                    Vector2 cell = new Vector2((column + 0.5f) / LensMaskSize, (row + 0.5f) / LensMaskSize);
                    if (InTriangle(cell, cornerA, cornerB, cornerC))
                        hidden[row * LensMaskSize + column] = true;
                }
            }
        }

        private static bool InTriangle(Vector2 point, Vector2 cornerA, Vector2 cornerB, Vector2 cornerC)
        {
            float side1 = Cross(cornerB - cornerA, point - cornerA);
            float side2 = Cross(cornerC - cornerB, point - cornerB);
            float side3 = Cross(cornerA - cornerC, point - cornerC);
            bool anyNegative = side1 < 0f || side2 < 0f || side3 < 0f;
            bool anyPositive = side1 > 0f || side2 > 0f || side3 > 0f;
            return !(anyNegative && anyPositive);
        }

        private static float Cross(Vector2 first, Vector2 second)
        {
            return first.x * second.y - first.y * second.x;
        }

        // Every cell within this many cells of a hidden one is hidden too (a square around it)
        private static bool[] Grow(bool[] hidden, int cells)
        {
            if (cells <= 0)
                return hidden;
            bool[] grown = new bool[hidden.Length];
            for (int row = 0; row < LensMaskSize; row++)
            {
                for (int column = 0; column < LensMaskSize; column++)
                {
                    if (!hidden[row * LensMaskSize + column])
                        continue;
                    for (int nearRow = Mathf.Max(0, row - cells); nearRow <= Mathf.Min(LensMaskSize - 1, row + cells); nearRow++)
                    {
                        for (int nearColumn = Mathf.Max(0, column - cells); nearColumn <= Mathf.Min(LensMaskSize - 1, column + cells); nearColumn++)
                            grown[nearRow * LensMaskSize + nearColumn] = true;
                    }
                }
            }
            return grown;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private void Draw(RenderTexture eyeTexture, Matrix4x4 view, Matrix4x4 projection, float tanHalfAcross, float tanHalfUp)
        {
            int columns = Mathf.Max(1, gridColumns);
            int rows = Mathf.Max(1, gridRows);

            RenderTexture.active = null;
            GL.Viewport(new Rect(0f, 0f, Screen.width, Screen.height));   // after an eye pass, the eye's viewport is still set
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
            Vector2 uv = EyeImagePoint(smoothedRotation * viewTilt, across, up, tanHalfAcross, tanHalfUp, view, projection);
            if (!drawingAfterEye || sourceTopRowFirst)
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
