using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Patches.Misc;
using UnityEngine;
using UnityEngine.EventSystems;
using WeaponCustomizer;

namespace TarkovVR.ModSupport.WeaponCustomizer
{
    [HarmonyPatch]
    internal static class WeaponCustomizerSupport
    {

        private static readonly Dictionary<object, Vector2> _modScreenAtStart = new();
        private static readonly Dictionary<object, Vector3> _pressHitAtStart = new();

        private const float VR_DRAG_SENSITIVITY = 0.33f;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DraggableBone), nameof(DraggableBone.OnBeginDrag))]
        private static void BeginDragPrefix(DraggableBone __instance, PointerEventData eventData)
        {
            if (__instance._viewporter?.TargetCamera == null || __instance._mod == null) return;
            if (MenuPatches.vrUiInteracter == null) return;

            Vector2 modScreen = __instance._viewporter.TargetCamera.WorldToScreenPoint(__instance._mod.position);

            _modScreenAtStart[__instance] = modScreen;
            _pressHitAtStart[__instance] = MenuPatches.vrUiInteracter.uiPointerPos;

            eventData.position = modScreen;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DraggableBone), nameof(DraggableBone.OnDrag))]
        private static void DragPrefix(DraggableBone __instance, PointerEventData eventData)
        {
            if (!_modScreenAtStart.TryGetValue(__instance, out Vector2 modScreen)) return;
            if (!_pressHitAtStart.TryGetValue(__instance, out Vector3 pressHit)) return;

            Camera cam = __instance._viewporter?.TargetCamera;
            if (cam == null || MenuPatches.vrUiInteracter == null) return;

            Vector3 currentHit = MenuPatches.vrUiInteracter.uiPointerPos;
            Vector3 scaledHit = pressHit + (currentHit - pressHit) * VR_DRAG_SENSITIVITY;

            Vector2 pressScreen = cam.WorldToScreenPoint(pressHit);
            Vector2 scaledScreen = cam.WorldToScreenPoint(scaledHit);

            eventData.position = modScreen + (scaledScreen - pressScreen);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DraggableBone), nameof(DraggableBone.OnEndDrag))]
        private static void EndDragPostfix(DraggableBone __instance)
        {
            _modScreenAtStart.Remove(__instance);
            _pressHitAtStart.Remove(__instance);
        }
    }
}
