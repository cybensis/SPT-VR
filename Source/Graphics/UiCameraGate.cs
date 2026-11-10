using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace TarkovVR.Source.Graphics
{
    // uiCam (the UI-layer camera drawn over the headset image) costs two camera passes a frame (~0.9 ms main thread) even with
    // nothing to draw, which is most of a raid. This switches it off while no visible (not faded out) UI-layer graphic is inside
    // either eye's view, with a margin; on while a menu is open. Checked every few frames, kept on a moment after the last one
    // (fade-outs finish). On the raid / hideout uiCam (InitVRPatches.SetMainCamParent).
    public class UiCameraGate : MonoBehaviour
    {
        public static bool skipWhenNoUiInView = true;  // false = uiCam every frame. (Skipping it flips the headset image unless Rendering.FlipForDestination corrects it)
        public static int checkEveryFrames = 3;
        public static float keepOnSeconds = 0.3f;      // after the last UI seen in view
        public static float marginMeters = 0.05f;      // grown around each graphic, plus...
        public static float marginPerMeter = 0.2f;     // ...this share of its distance from the eyes (about 11 degrees): on before it's in view
        public static float minAlpha = 0.004f;         // below this a graphic counts as faded out
        public static bool logReasons = false;         // each canvas that turns the camera on, once
        public static float logHolderAfterSeconds = 10f; // on this long without a break: the graphic holding it logged (a new one at most every 15 s, first 60)
        public static float minAngularSize = 0.002f;    // radians across (~0.1 degree, ~2 pixels of the eye image): smaller can't be seen

        private const int UiLayer = 5;
        private Camera uiCamera;
        private Camera eyeCamera;
        private float onUntil = float.MaxValue;        // on until the first check
        private int framesToCheck;
        private readonly Plane[][] eyePlanes = { new Plane[6], new Plane[6] };
        private readonly Vector3[] corners = new Vector3[4];
        private readonly HashSet<int> loggedCanvases = new HashSet<int>();
        private Canvas holderCanvas;                   // what the last check found in view
        private Graphic holderGraphic;
        private RenderMode holderRenderMode;
        private float onSince = -1f;
        private Graphic loggedHolder;
        private int holderLogs;
        private float nextHolderLog;

        private void Awake()
        {
            uiCamera = GetComponent<Camera>();
        }

        private void OnDisable()
        {
            if (uiCamera != null) uiCamera.enabled = true;
        }

        // After the wrist's look check and the UI's own fades (Update), before the cameras render
        private void LateUpdate()
        {
            if (uiCamera == null)
                return;
            if (!skipWhenNoUiInView || VRGlobals.menuOpen)
            {
                SetCameraOn(true);
                onUntil = Time.unscaledTime + keepOnSeconds;
                return;
            }
            if (--framesToCheck <= 0)
            {
                framesToCheck = Mathf.Max(1, checkEveryFrames);
                if (AnyUiInView())
                    onUntil = Time.unscaledTime + keepOnSeconds;
                else if (onUntil == float.MaxValue)
                    onUntil = 0f;
            }
            bool on = Time.unscaledTime < onUntil;
            SetCameraOn(on);
            LogLongHolder(on);
        }

        // A camera that never switches off saves nothing: which graphic keeps it on (the per-canvas log names only the first)
        private void LogLongHolder(bool on)
        {
            if (!on)
            {
                onSince = -1f;
                return;
            }
            if (onSince < 0f)
                onSince = Time.unscaledTime;
            if (!logReasons || holderLogs >= 60 || holderGraphic == null || holderGraphic == loggedHolder || holderCanvas == null
                || Time.unscaledTime - onSince < logHolderAfterSeconds || Time.unscaledTime < nextHolderLog || eyeCamera == null)
                return;
            loggedHolder = holderGraphic;
            holderLogs++;
            nextHolderLog = Time.unscaledTime + 15f;
            Describe($"kept on {Time.unscaledTime - onSince:0} s so far by canvas", holderCanvas, holderGraphic, eyeCamera.transform.position, holderRenderMode);
        }

        private void SetCameraOn(bool on)
        {
            if (uiCamera.enabled != on)
                uiCamera.enabled = on;
        }

        // Any UI-layer graphic, not faded out, inside either eye's view (grown by the margin). Unsure = true.
        private bool AnyUiInView()
        {
            if (eyeCamera == null && transform.parent != null)
                eyeCamera = transform.parent.GetComponent<Camera>();
            if (eyeCamera == null || !eyeCamera.isActiveAndEnabled || !eyeCamera.stereoEnabled)
                return true;
            GeometryUtility.CalculateFrustumPlanes(eyeCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) * eyeCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Left), eyePlanes[0]);
            GeometryUtility.CalculateFrustumPlanes(eyeCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) * eyeCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Right), eyePlanes[1]);
            Vector3 head = eyeCamera.transform.position;

            // Every active, enabled graphic is registered with its canvas (Graphic.OnEnable): GraphicRegistry's private
            // Dictionary<Canvas, IndexedSet<Graphic>>, walked through IDictionary / IList (IndexedSet is internal)
            IDictionary registry = Registry();
            if (registry == null)
                return true;
            IDictionaryEnumerator entries = registry.GetEnumerator();
            while (entries.MoveNext())
            {
                Canvas canvas = entries.Key as Canvas;
                if (canvas == null || canvas.gameObject.layer != UiLayer || !canvas.isActiveAndEnabled)
                    continue;
                Canvas root = canvas.rootCanvas;
                RenderMode renderMode = root != null ? root.renderMode : canvas.renderMode;
                if (renderMode == RenderMode.ScreenSpaceOverlay)
                    continue;   // drawn onto the desktop window, not by a camera
                bool inFrontOfCamera = renderMode == RenderMode.ScreenSpaceCamera;
                if (inFrontOfCamera && root.worldCamera != uiCamera)
                    continue;   // another camera's (or none = overlay)
                if (!(entries.Value is IList<Graphic> graphics))
                    return true;
                for (int index = 0; index < graphics.Count; index++)
                {
                    Graphic graphic = graphics[index];
                    if (graphic == null)
                        continue;
                    CanvasRenderer canvasRenderer = graphic.canvasRenderer;
                    if (canvasRenderer == null || canvasRenderer.cull)
                        continue;
                    if (graphic.color.a * canvasRenderer.GetAlpha() * canvasRenderer.GetInheritedAlpha() < minAlpha)
                        continue;
                    Rect area = graphic.rectTransform.rect;
                    if (area.width <= 0f || area.height <= 0f || DrawsNothing(graphic))
                        continue;   // nothing to draw
                    if (inFrontOfCamera || InView(graphic.rectTransform, head))
                    {
                        holderCanvas = canvas;
                        holderGraphic = graphic;
                        holderRenderMode = renderMode;
                        LogReason(canvas, graphic, head, renderMode);
                        return true;
                    }
                }
            }
            return false;
        }

        private static FieldInfo graphicsField;
        private static bool loggedNoRegistry;
        private static IDictionary Registry()
        {
            if (graphicsField == null)
                graphicsField = typeof(GraphicRegistry).GetField("m_Graphics", BindingFlags.Instance | BindingFlags.NonPublic);
            IDictionary registry = graphicsField?.GetValue(GraphicRegistry.instance) as IDictionary;
            if (registry == null && !loggedNoRegistry)
            {
                loggedNoRegistry = true;
                Plugin.MyLog.LogWarning("[UiCamGate] UI graphic registry not found: uiCam stays on every frame");
            }
            return registry;
        }

        private bool InView(RectTransform rect, Vector3 head)
        {
            rect.GetWorldCorners(corners);
            Vector3 min = corners[0], max = corners[0];
            for (int index = 1; index < 4; index++)
            {
                min = Vector3.Min(min, corners[index]);
                max = Vector3.Max(max, corners[index]);
            }
            var bounds = new Bounds((min + max) * 0.5f, max - min);
            float distance = Vector3.Distance(head, bounds.center);
            if (bounds.size.magnitude < minAngularSize * distance)
                return false;   // too small to see from here
            bounds.Expand(2f * (marginMeters + marginPerMeter * distance));
            return GeometryUtility.TestPlanesAABB(eyePlanes[0], bounds) || GeometryUtility.TestPlanesAABB(eyePlanes[1], bounds);
        }

        // An empty text is an enabled graphic with a rect that draws nothing.
        // TMP: its character count, not .text (SetText(char[]) can leave .text stale while characters draw); not laid out yet = draws.
        // A Mask's own graphic with showMaskGraphic off only marks the stencil (its children are checked on their own).
        private static bool DrawsNothing(Graphic graphic)
        {
            if (graphic.TryGetComponent(out Mask mask) && mask.enabled && !mask.showMaskGraphic)
                return true;
            if (graphic is TMPro.TMP_Text textMeshPro)
                return textMeshPro.textInfo != null && textMeshPro.textInfo.characterCount == 0;
            if (graphic is Text text)
                return string.IsNullOrWhiteSpace(text.text);
            return false;
        }

        // What it saw, so a canvas that keeps the camera on all the time can be told apart from one really in view
        private void LogReason(Canvas canvas, Graphic graphic, Vector3 head, RenderMode renderMode)
        {
            if (!logReasons || !loggedCanvases.Add(canvas.GetInstanceID()))
                return;
            Describe("on for canvas", canvas, graphic, head, renderMode);
        }

        private static void Describe(string what, Canvas canvas, Graphic graphic, Vector3 head, RenderMode renderMode)
        {
            Transform parent = canvas.transform.parent;
            RectTransform rect = graphic.rectTransform;
            Rect area = rect.rect;
            Vector3 scale = rect.lossyScale;
            CanvasRenderer canvasRenderer = graphic.canvasRenderer;
            string text = graphic is Text uiText ? uiText.text : graphic is TMPro.TMP_Text tmpText ? tmpText.text : null;
            if (text != null && text.Length > 40)
                text = text.Substring(0, 40) + "...";
            Plugin.MyLog.LogInfo($"[UiCamGate] {what} '{(parent != null ? parent.name + "/" : "")}{canvas.name}' ({renderMode}): '{graphic.name}' "
                + $"{graphic.GetType().Name}{(text != null ? $" text '{text}'" : "")}, {area.width * scale.x * 100f:0.#}x{area.height * scale.y * 100f:0.#} cm "
                + $"{Vector3.Distance(head, rect.position):0.00} m from the eyes, alpha {graphic.color.a:0.##} x {canvasRenderer.GetAlpha():0.##} x {canvasRenderer.GetInheritedAlpha():0.##}"
                + (graphic is Image image ? $", sprite {(image.sprite != null ? $"'{image.sprite.name}'" : "none")} {image.type}{(image.type == Image.Type.Filled ? $" fill {image.fillAmount:0.##}" : "")}" : "")
                + (graphic.TryGetComponent(out Mask mask) ? $", mask (shows its graphic: {mask.showMaskGraphic})" : "")
                + $", material '{(graphic.materialForRendering != null ? graphic.materialForRendering.name : "none")}', depth {graphic.depth}");
        }
    }
}
