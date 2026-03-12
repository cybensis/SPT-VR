using Comfort.Common;
using DynamicMaps;
using DynamicMaps.Data;
using DynamicMaps.UI;
using DynamicMaps.UI.Components;
using DynamicMaps.UI.Controls;
using DynamicMaps.Utils;
using EFT;
using EFT.UI;
using HarmonyLib;
using KmyTarkovConfiguration.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Patches.Misc;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.UI;
using Valve.VR;
using Valve.VR.InteractionSystem;

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
            var rt = __result.GetComponent<RectTransform>();
            var s = rt.localScale;
            rt.localScale = new Vector3(s.x, s.y, 1f);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MapMarker), "Create", new Type[] { typeof(GameObject), typeof(MapMarkerDef), typeof(Vector2), typeof(float), typeof(float) })]
        private static void FixMarkerLabelScale(MapMarker __result)
        {
            var rt = __result.GetComponent<RectTransform>();
            var s = rt.localScale;
            rt.localScale = new Vector3(s.x, s.y, 1f);
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

        // Fixes texture resolutions used by Dynamic maps to not eat VRAM
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TextureUtils), "LoadTexture2DFromPath")]
        private static bool FixTextureResolution(string absolutePath, ref Texture2D __result)
        {
            if (!File.Exists(absolutePath))
            {
                __result = null;
                return false;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(File.ReadAllBytes(absolutePath));

            // Downscale to half, rounded to multiple of 4 for compression
            int newWidth = ((tex.width / 2) + 3) & ~3;
            int newHeight = ((tex.height / 2) + 3) & ~3;

            RenderTexture rt = RenderTexture.GetTemporary(newWidth, newHeight, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(tex, rt);

            Texture2D result = new Texture2D(newWidth, newHeight, TextureFormat.RGBA32, false);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            result.ReadPixels(new Rect(0, 0, newWidth, newHeight), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.Destroy(tex);

            result.Compress(false);
            result.Apply(false, true);

            __result = result;
            return false;
        }

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

            // Cast inside the method — only resolves MapView type when actually called
            dynamic mapView = _mapViewRef;

            bool triggerHeld = SteamVR_Actions._default.RightTrigger.axis > 0.5f;
            if (triggerHeld)
            {
                Vector3 currentPos = interacter.uiPointerPos;
                if (_isDragging)
                {
                    Vector3 worldDelta = currentPos - _lastPointerPos;
                    RectTransform rt = _mapViewRef.transform as RectTransform;
                    Vector3 localDelta = rt.parent.InverseTransformVector(worldDelta);
                    mapView.ShiftMap(new Vector2(localDelta.x, localDelta.y), 0, false);

                    Vector2 rightStick = SteamVR_Actions._default.RightJoystick.axis;
                    if (Mathf.Abs(rightStick.y) > 0.1f)
                    {
                        Vector3 localPoint = rt.InverseTransformPoint(currentPos);
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
