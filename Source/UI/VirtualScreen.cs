using System.Collections.Generic;
using TarkovVR.Patches.Misc;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.UI;

namespace TarkovVR.Source.UI
{
    // PROTOTYPE (Ctrl+F9 toggles): shows Tarkov's whole UI on one virtual screen instead of fixing every screen.
    // The game's UI canvases render as if on a normal monitor into a texture, the texture is shown on a panel
    // in front of you, and the laser's hit point on the panel becomes the mouse.
    //
    // Vanilla UI is screen space overlay, where a UI element's world position IS its screen pixel position, and
    // some game code relies on that (the drag icon does RectTransform.position = Input.mousePosition). So the
    // capture camera is orthographic, sized to the texture and placed so that pixel space maps 1:1 onto world
    // space. Canvases in screen space camera mode are only drawn by their own camera, so this pixel-sized UI
    // never shows up in the headset view.
    public class VirtualScreen : MonoBehaviour
    {
        public static bool Active { get; private set; }

        // Read by VirtualMouseInput and the Input.mousePosition patch
        public static Vector2 mousePosition = new Vector2(-100f, -100f);
        public static Vector2 scrollDelta = Vector2.zero;

        public static int screenWidth = 1920;
        public static int screenHeight = 1080;
        public static float panelWidthMeters = 2.4f;
        public static float panelDistance = 1.6f;
        public static float triggerPressThreshold = 0.5f;
        public static float triggerReleaseThreshold = 0.35f;
        public static float scrollDeadzone = 0.4f;
        public static float scrollSpeed = 0.3f;

        private const float CANVAS_PLANE_DISTANCE = 100f;
        private const float CANVAS_RESCAN_INTERVAL = 0.25f;

        private static readonly bool[] buttonHeld = new bool[2];          // 0 = left click, 1 = right click
        private static readonly bool[] buttonWasHeld = new bool[2];

        private RenderTexture screenTexture;
        private Camera captureCamera;
        private GameObject panel;
        private RectTransform cursor;
        private float nextCanvasScan = 0f;
        private int uiLayer;

        // How each canvas looked before we took it over, so turning the screen off puts it back
        private class CanvasState
        {
            public RenderMode renderMode;
            public Camera worldCamera;
            public float planeDistance;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
            public Vector2 sizeDelta;
            public Vector3 anchoredPosition;
            public Vector2 anchorMin;
            public Vector2 anchorMax;
            public Vector2 pivot;
        }
        private readonly Dictionary<Canvas, CanvasState> takenOverCanvases = new Dictionary<Canvas, CanvasState>();
        private readonly List<Canvas> canvasesFound = new List<Canvas>();

        public static bool IsButtonHeld(int button) => button >= 0 && button < 2 && buttonHeld[button];
        public static bool WasButtonPressed(int button) => button >= 0 && button < 2 && buttonHeld[button] && !buttonWasHeld[button];
        public static bool WasButtonReleased(int button) => button >= 0 && button < 2 && !buttonHeld[button] && buttonWasHeld[button];

        private void Awake()
        {
            uiLayer = LayerMask.NameToLayer("UI");
        }

        private void Update()
        {
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKeyDown(KeyCode.F9))
            {
                if (Active)
                    Deactivate();
                else
                    Activate();
            }

            if (!Active)
                return;

            UpdatePointer();
        }

        private void LateUpdate()
        {
            if (!Active)
                return;

            if (Time.unscaledTime >= nextCanvasScan)
            {
                nextCanvasScan = Time.unscaledTime + CANVAS_RESCAN_INTERVAL;
                FindGameCanvases();
            }
            // Other code (our old world space fixes included) may switch a canvas back, so this runs every frame
            foreach (Canvas canvas in takenOverCanvases.Keys)
            {
                if (canvas != null)
                    PutCanvasOnScreen(canvas);
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Turning the screen on and off

        private void Activate()
        {
            CreateCaptureCamera();
            CreatePanel();
            PlacePanelInFrontOfHead();
            FindGameCanvases();

            if (MenuPatches.vrUiInteracter != null)
                MenuPatches.vrUiInteracter.enabled = false;

            Active = true;
            Plugin.MyLog.LogInfo($"[VirtualScreen] On, {takenOverCanvases.Count} canvases on the screen");
        }

        private void Deactivate()
        {
            Active = false;

            foreach (KeyValuePair<Canvas, CanvasState> entry in takenOverCanvases)
                RestoreCanvas(entry.Key, entry.Value);
            takenOverCanvases.Clear();

            if (MenuPatches.vrUiInteracter != null)
                MenuPatches.vrUiInteracter.enabled = true;
            if (panel != null)
                panel.SetActive(false);
            if (captureCamera != null)
                captureCamera.enabled = false;

            mousePosition = new Vector2(-100f, -100f);
            buttonHeld[0] = buttonHeld[1] = false;
            Plugin.MyLog.LogInfo("[VirtualScreen] Off, canvases restored");
        }

        private void CreateCaptureCamera()
        {
            if (screenTexture == null)
            {
                // 24 bit depth also gives the stencil buffer UI masks need
                screenTexture = new RenderTexture(screenWidth, screenHeight, 24, RenderTextureFormat.ARGB32);
                screenTexture.name = "VRVirtualScreen";
                screenTexture.useMipMap = true;
                screenTexture.autoGenerateMips = true;
                screenTexture.Create();
            }

            if (captureCamera == null)
            {
                GameObject cameraHolder = new GameObject("VRVirtualScreenCamera");
                DontDestroyOnLoad(cameraHolder);
                captureCamera = cameraHolder.AddComponent<Camera>();
            }

            // Centered on the screen in pixel space, so world position == pixel position like overlay UI
            captureCamera.transform.position = new Vector3(screenWidth / 2f, screenHeight / 2f, -CANVAS_PLANE_DISTANCE);
            captureCamera.transform.rotation = Quaternion.identity;
            captureCamera.orthographic = true;
            captureCamera.orthographicSize = screenHeight / 2f;
            captureCamera.nearClipPlane = 1f;
            captureCamera.farClipPlane = CANVAS_PLANE_DISTANCE * 2f;
            captureCamera.cullingMask = 1 << uiLayer;
            captureCamera.clearFlags = CameraClearFlags.SolidColor;
            captureCamera.backgroundColor = new Color(0.05f, 0.05f, 0.05f, 1f);
            captureCamera.targetTexture = screenTexture;
            captureCamera.stereoTargetEye = StereoTargetEyeMask.None;
            captureCamera.renderingPath = RenderingPath.Forward;
            captureCamera.allowHDR = false;
            captureCamera.allowMSAA = false;
            captureCamera.useOcclusionCulling = false;
            captureCamera.depth = -50f;
            captureCamera.enabled = true;
        }

        private void CreatePanel()
        {
            if (panel != null)
            {
                panel.SetActive(true);
                return;
            }

            panel = new GameObject("VRVirtualScreenPanel");
            panel.layer = uiLayer;
            DontDestroyOnLoad(panel);
            Canvas panelCanvas = panel.AddComponent<Canvas>();
            panelCanvas.renderMode = RenderMode.WorldSpace;
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(screenWidth, screenHeight);
            panelRect.localScale = Vector3.one * (panelWidthMeters / screenWidth);

            GameObject imageHolder = new GameObject("Screen");
            imageHolder.layer = uiLayer;
            imageHolder.transform.SetParent(panel.transform, false);
            RawImage screenImage = imageHolder.AddComponent<RawImage>();
            screenImage.texture = screenTexture;
            screenImage.raycastTarget = false;
            Stretch(screenImage.rectTransform);

            GameObject cursorHolder = new GameObject("Cursor");
            cursorHolder.layer = uiLayer;
            cursorHolder.transform.SetParent(panel.transform, false);
            Image cursorImage = cursorHolder.AddComponent<Image>();
            cursorImage.color = new Color(0.3f, 0.9f, 1f, 0.9f);
            cursorImage.raycastTarget = false;
            cursor = cursorImage.rectTransform;
            cursor.sizeDelta = new Vector2(12f, 12f);
        }

        private static void Stretch(RectTransform rectTransform)
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
        }

        // In front of the headset at eye height, facing you. Parented to the rig so it stays put when the rig moves.
        private void PlacePanelInFrontOfHead()
        {
            Camera headCamera = VRGlobals.VRCam != null ? VRGlobals.VRCam : Camera.main;
            if (headCamera == null)
                return;

            Transform head = headCamera.transform;
            Vector3 flatForward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (flatForward.sqrMagnitude < 0.01f)
                flatForward = Vector3.forward;

            panel.transform.SetParent(head.parent, true);
            panel.transform.position = head.position + flatForward * panelDistance;
            panel.transform.rotation = Quaternion.LookRotation(flatForward, Vector3.up);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Game canvases

        private void FindGameCanvases()
        {
            AddRootCanvases(VRGlobals.commonUi);
            AddRootCanvases(VRGlobals.menuUi);
            AddRootCanvases(VRGlobals.preloaderUi);
        }

        private void AddRootCanvases(Transform uiRoot)
        {
            if (uiRoot == null)
                return;

            canvasesFound.Clear();
            uiRoot.GetComponentsInChildren(true, canvasesFound);
            foreach (Canvas canvas in canvasesFound)
            {
                if (canvas.rootCanvas != canvas || takenOverCanvases.ContainsKey(canvas))
                    continue;

                RectTransform canvasRect = (RectTransform)canvas.transform;
                takenOverCanvases[canvas] = new CanvasState
                {
                    renderMode = canvas.renderMode,
                    worldCamera = canvas.worldCamera,
                    planeDistance = canvas.planeDistance,
                    localPosition = canvasRect.localPosition,
                    localRotation = canvasRect.localRotation,
                    localScale = canvasRect.localScale,
                    sizeDelta = canvasRect.sizeDelta,
                    anchoredPosition = canvasRect.anchoredPosition3D,
                    anchorMin = canvasRect.anchorMin,
                    anchorMax = canvasRect.anchorMax,
                    pivot = canvasRect.pivot,
                };
                PutCanvasOnScreen(canvas);
            }
        }

        private void PutCanvasOnScreen(Canvas canvas)
        {
            if (canvas.renderMode != RenderMode.ScreenSpaceCamera)
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
            if (canvas.worldCamera != captureCamera)
                canvas.worldCamera = captureCamera;
            if (canvas.planeDistance != CANVAS_PLANE_DISTANCE)
                canvas.planeDistance = CANVAS_PLANE_DISTANCE;
        }

        private static void RestoreCanvas(Canvas canvas, CanvasState state)
        {
            if (canvas == null)
                return;
            canvas.renderMode = state.renderMode;
            canvas.worldCamera = state.worldCamera;
            canvas.planeDistance = state.planeDistance;
            RectTransform canvasRect = (RectTransform)canvas.transform;
            canvasRect.anchorMin = state.anchorMin;
            canvasRect.anchorMax = state.anchorMax;
            canvasRect.pivot = state.pivot;
            canvasRect.sizeDelta = state.sizeDelta;
            canvasRect.anchoredPosition3D = state.anchoredPosition;
            canvasRect.localPosition = state.localPosition;
            canvasRect.localRotation = state.localRotation;
            canvasRect.localScale = state.localScale;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Laser -> mouse

        private void UpdatePointer()
        {
            buttonWasHeld[0] = buttonHeld[0];
            buttonWasHeld[1] = buttonHeld[1];
            scrollDelta = Vector2.zero;

            bool leftHanded = VRSettings.GetLeftHandedMode();
            float trigger = leftHanded ? VRInput.LeftTrigger.axis : VRInput.RightTrigger.axis;
            if (buttonHeld[0])
                buttonHeld[0] = trigger > triggerReleaseThreshold;
            else
                buttonHeld[0] = trigger >= triggerPressThreshold;
            buttonHeld[1] = leftHanded ? VRInput.ButtonX.state : VRInput.ButtonA.state;

            float stickY = leftHanded ? VRInput.LeftJoystick.axis.y : VRInput.RightJoystick.axis.y;
            if (Mathf.Abs(stickY) > scrollDeadzone)
                scrollDelta = new Vector2(0f, stickY * scrollSpeed);

            if (TryGetPointerPixel(out Vector2 pixel))
            {
                mousePosition = pixel;
                cursor.gameObject.SetActive(true);
                cursor.anchoredPosition = pixel - new Vector2(screenWidth / 2f, screenHeight / 2f);
            }
            else
            {
                cursor.gameObject.SetActive(false);
                // Keep the last position while dragging off the edge, otherwise move the mouse off the screen
                if (!buttonHeld[0] && !buttonHeld[1])
                    mousePosition = new Vector2(-100f, -100f);
            }
        }

        private bool TryGetPointerPixel(out Vector2 pixel)
        {
            pixel = Vector2.zero;
            Transform pointerHand = GetPointerHand();
            if (pointerHand == null || panel == null)
                return false;

            Plane panelPlane = new Plane(-panel.transform.forward, panel.transform.position);
            Ray pointerRay = new Ray(pointerHand.position, pointerHand.forward);
            if (!panelPlane.Raycast(pointerRay, out float hitDistance))
                return false;

            // Panel local units are canvas pixels, with the pivot in the middle
            Vector3 localHit = panel.transform.InverseTransformPoint(pointerRay.GetPoint(hitDistance));
            pixel = new Vector2(localHit.x + screenWidth / 2f, localHit.y + screenHeight / 2f);
            return pixel.x >= 0f && pixel.x <= screenWidth && pixel.y >= 0f && pixel.y <= screenHeight;
        }

        // The "RightHand" objects follow the dominant controller in both handed modes
        private static Transform GetPointerHand()
        {
            if (VRGlobals.menuVRManager != null && VRGlobals.menuVRManager.RightHand != null && VRGlobals.menuVRManager.RightHand.activeInHierarchy)
                return VRGlobals.menuVRManager.RightHand.transform;
            if (VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.RightHand != null)
                return VRGlobals.vrPlayer.RightHand.transform;
            return null;
        }

        private void OnDestroy()
        {
            if (screenTexture != null)
                screenTexture.Release();
        }
    }
}
