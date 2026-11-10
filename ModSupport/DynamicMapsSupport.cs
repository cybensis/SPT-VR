using DynamicMaps.Data;
using DynamicMaps.UI;
using DynamicMaps.UI.Components;
using DynamicMaps.UI.Controls;
using HarmonyLib;
using System;
using TarkovVR.Patches.Misc;
using UnityEngine;
using UnityEngine.UI;
using TarkovVR.Source.Controls;

namespace TarkovVR.ModSupport.DynamicMaps
{
    [HarmonyPatch]
    internal static class DynamicMapsSupport
    {
        // Sets the dynamic map's map to be on the UI layer, otherwise it does not appear in world space
        // Also disables ScrollRect, this does not work in world space so we add our own VRMapDragger component to handle dragging and zooming the map with the VR controller
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MapView), "LoadMap")]
        private static void SetMapViewWorldCamera(MapView __instance)
        {
            Transform transform = __instance.transform;
            transform.gameObject.layer = LayerMask.NameToLayer("UI");
            transform.localPosition = new Vector3(0, 0, 0);
            __instance.GetComponentInParent<ScrollRect>().enabled = false;
            if (__instance.GetComponent<VRMapDragger>() == null)
            {
                var dragger = __instance.gameObject.AddComponent<VRMapDragger>();
                dragger.Init((Component)__instance);
            }
        }

        // Just disable minimap for now, minimaps aren't nice in VR, may add some custom solution, maybe a certain gesture to make it appear
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MapPeekComponent), "HandleMinimapState")]
        private static bool DisableMinimap(MapPeekComponent __instance)
        {
            __instance.EndMiniMap();
            __instance.WasMiniMapActive = false;
            return false;
        }

        // Fixes the position of the map when in a raid
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ModdedMapScreen), "OnShowInRaid")]
        private static void FixMapPositionInRaid(ModdedMapScreen __instance)
        {
            Transform transform = __instance.transform;
            transform.localPosition = new Vector3(0, 0, 0);
            transform.localScale = new Vector3(0.96f, 0.96f, 0.96f);
            transform.localRotation = Quaternion.Euler(0, 0, 0);
        }

        // Map labels and map marker labels have a Z scale of 0 which breaks fonts in world space, the two patches below fix this by setting the Z scale to 1 after they are created
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MapLabel), "Create")]
        private static void FixMapLabelScale(MapLabel __result)
        {
            var rectTransform = __result.GetComponent<RectTransform>();
            var s = rectTransform.localScale;
            rectTransform.localScale = new Vector3(s.x, s.y, 1f);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MapMarker), "Create", new Type[] { typeof(GameObject), typeof(MapMarkerDef), typeof(Vector2), typeof(float), typeof(float) })]
        private static void FixMarkerLabelScale(MapMarker __result)
        {
            var rectTransform = __result.GetComponent<RectTransform>();
            var s = rectTransform.localScale;
            rectTransform.localScale = new Vector3(s.x, s.y, 1f);
        }

        // Updates the cursor position text to show the correct position of the VR pointer on the map, instead of the mouse cursor position which is not used in VR
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CursorPositionText), "Update")]
        private static bool VRCursorUpdate(CursorPositionText __instance)
        {
            var interacter = MenuPatches.vrUiInteracter;
            if (interacter == null) return false;

            if (__instance._mapViewTransform == null) return false;

            Vector3 localPoint = __instance._mapViewTransform.InverseTransformPoint(interacter.uiPointerPos);
            __instance.Text.text = $"Cursor: {localPoint.x:F} {localPoint.y:F}";

            return false;
        }

        // (The map-texture downscale is gone: since DynamicMaps 1.1.0 the map layers are SVGs tessellated into meshes
        // (SvgUtils.LoadSvgFromPath), so LoadTexture2DFromPath only loads the small marker icons - halving those just blurred them.)

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(ModdedMapScreen), "ReadConfig")]
        private static Exception SuppressReadConfigError()
        {
            return null;
        }
    }
    public class VRMapDragger : MonoBehaviour
    {
        private Component _mapViewRef;
        private Vector3 _lastPointerPos;
        private bool _isDragging;
        private static float _zoomSpeed = 2f;

        public void Init(Component mapView)
        {
            _mapViewRef = mapView;
        }

        private void Update()
        {
            if (_mapViewRef == null) return;
            var interacter = MenuPatches.vrUiInteracter;
            if (interacter == null) return;

            // Cast inside the method: MapView (DynamicMaps') only resolves when this runs, and this component only exists once the
            // DynamicMaps patch above has added it. (Was C# dynamic = the runtime binder on every frame the map was open.)
            MapView mapView = (MapView)_mapViewRef;

            // The laser's hand ("Laser Pointer In Left Hand"): its trigger drags, its own stick zooms
            bool triggerHeld = Handedness.PointerHandTrigger.axis > 0.5f;
            if (triggerHeld)
            {
                Vector3 currentPos = interacter.uiPointerPos;
                if (_isDragging)
                {
                    Vector3 worldDelta = currentPos - _lastPointerPos;
                    RectTransform rectTransform = _mapViewRef.transform as RectTransform;
                    Vector3 localDelta = rectTransform.parent.InverseTransformVector(worldDelta);
                    mapView.ShiftMap(new Vector2(localDelta.x, localDelta.y), 0, false);

                    Vector2 rightStick = Handedness.PointerHandJoystick.axis;
                    if (Mathf.Abs(rightStick.y) > 0.1f)
                    {
                        Vector3 localPoint = rectTransform.InverseTransformPoint(currentPos);
                        Vector2 mouseRelative = new Vector2(localPoint.x, localPoint.y);
                        float zoomDelta = rightStick.y * (float)mapView.ZoomCurrent * _zoomSpeed * Time.deltaTime;
                        mapView.IncrementalZoomInto(zoomDelta, mouseRelative, 0f);
                    }
                }
                _isDragging = true;
                _lastPointerPos = currentPos;
            }
            else
            {
                _isDragging = false;
            }
        }
    }
}
