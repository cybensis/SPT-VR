using HarmonyLib;
using UnityEngine;
using EFT;
using EFT.UI;
using EFT.UI.DragAndDrop;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using static EFT.UI.PixelPerfectSpriteScaler;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class GridSlotIconPatches
    {

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PixelPerfectSpriteScaler), "method_1")]
        private static bool FixMenuImagesScaling(PixelPerfectSpriteScaler __instance)
        {
            Vector3 lossyScale = new Vector3(1.333f, 1.333f, 1.333f);
            float num = 1.333f;
            if (__instance.image_0 != null)
            {
                //if (num.ApproxEquals(__instance.image_0.pixelsPerUnitMultiplier))
                if (GClass834.ApproxEquals(num, __instance.image_0.pixelsPerUnitMultiplier))
                {
                    return false;
                }
                __instance.image_0.pixelsPerUnitMultiplier = num;
                __instance.image_0.SetVerticesDirty();
            }
            Vector2 offsetMin = __instance.rectTransform_0.offsetMin;
            Vector2 offsetMax = __instance.rectTransform_0.offsetMax;
            if (__instance._sidesToScale.HasFlag(EScaleSide.Top))
            {
                offsetMax.y = __instance.vector2_1.y / num;
            }
            if (__instance._sidesToScale.HasFlag(EScaleSide.Left))
            {
                offsetMin.x = __instance.vector2_0.x / num;
            }
            if (__instance._sidesToScale.HasFlag(EScaleSide.Bottom))
            {
                offsetMin.y = __instance.vector2_0.y / num;
            }
            if (__instance._sidesToScale.HasFlag(EScaleSide.Right))
            {
                offsetMax.x = __instance.vector2_1.x / num;
            }
            __instance.rectTransform_0.offsetMin = offsetMin;
            __instance.rectTransform_0.offsetMax = offsetMax;

            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Icons get rotated when loading mags and other stuff
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GridItemView), "OnRefreshItem")]
        private static void ResetRotationOnInvIcons(ItemView __instance)
        {
            __instance.MainImage.transform.localRotation = Quaternion.Euler(0, 0, __instance.MainImage.transform.localEulerAngles.z);
        }


        //-----------------------------------------------------------------------------------------------------------------
        // When the grid is being initialized we need to make sure the rotation is 0,0,0 otherwise the grid items don't
        // spawn in because of their weird code.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridViewMagnifier), "method_3")]
        private static void ReturnCommonUiToZeroRot(GridViewMagnifier __instance)
        {
            __instance.transform.root.rotation = Quaternion.identity;
        }


        //-----------------------------------------------------------------------------------------------------------------
        // If the canvas roots rotation isn't 0,0,0 the grid/slot items display on an angle
        // so these patches prevent them from being on an angle
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GridView), "method_5")]
        private static void PreventOffAxisGridItemsViews(GridView __instance, ItemView itemView)
        {
            itemView.transform.localEulerAngles = Vector3.zero;
            itemView.MainImage.transform.localEulerAngles = new Vector3(0, 0, itemView.MainImage.transform.localEulerAngles.z);
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SlotView), "method_5")]
        private static void PreventOffAxisSlotItemsViews(SlotView __instance)
        {
            __instance.itemView_0.transform.localEulerAngles = Vector3.zero;
            __instance.itemView_0.MainImage.transform.localEulerAngles = new Vector3(0, 0, __instance.itemView_0.MainImage.transform.localEulerAngles.z);
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ModSlotView), "Show")]
        private static void PreventOffAxisModSlotItemsViews(ModSlotView __instance)
        {
            __instance.transform.localEulerAngles = Vector3.zero;

        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UISpawnableToggle), "method_2")]
        private static void PreventOffAxisSettingsTabText(UISpawnableToggle __instance)
        {
            __instance.transform.localEulerAngles = Vector3.zero;

        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(QuickSlotView), "SetItem")]
        private static void PreventOffAxisQuickSlotItemsViews(QuickSlotView __instance)
        {
            __instance.ItemView.transform.localEulerAngles = Vector3.zero;
            __instance.ItemView.MainImage.transform.localEulerAngles = new Vector3(0, 0, __instance.ItemView.MainImage.transform.localEulerAngles.z);
        }


        //-----------------------------------------------------------------------------------------------------------------
        // This code is somehow responsible for determining which items in the stash/inv grid are shown and it shits the bed if
        // the CommonUI rotation isn't 0,0,0 so set it to that before running this code then set it back
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridViewMagnifier), "method_1")]
        private static bool StopGridFromHidingItemsWhenUiRotated(GridViewMagnifier __instance, bool calculate, bool forceMagnify)
        {
            if ((object)__instance.rectTransform_0 == null || (object)__instance._gridView == null || (object)__instance._scrollRect == null)
            {
                return false;
            }
            Vector3 originalRot = __instance.transform.root.eulerAngles;
            __instance.transform.root.eulerAngles = Vector3.zero;
            if (calculate)
            {

                Rect rect = __instance.rectTransform_0.rect;
                Vector3 vector = __instance.rectTransform_0.TransformPoint(rect.position);
                Vector3 vector2 = __instance.rectTransform_0.TransformPoint(rect.position + rect.size) - vector;
                rect = new Rect(vector, vector2);

                if (!forceMagnify && __instance.nullable_0 == rect)
                {
                    __instance.transform.root.eulerAngles = originalRot;
                    return false;
                }
                __instance.nullable_0 = rect;
            }
            if (__instance.nullable_0.HasValue)
            {
                __instance._gridView.MagnifyIfPossible(__instance.nullable_0.Value, forceMagnify);
            }
            __instance.transform.root.eulerAngles = originalRot;
            return false;
        }
        */


        //-----------------------------------------------------------------------------------------------------------------
        //This sorta does the same thing as above but works better for how I'm now handling the inventory. This targets the method that directly handles the culling of items in stash.
        //For some reason, items cull when turning head 90+ degrees from front of playspace, but doesn't cull if you turn using the joystick
        private static Dictionary<GridView, (Vector3 rot, Vector3 pos, Quaternion quat)> _originalTransforms = new Dictionary<GridView, (Vector3, Vector3, Quaternion)>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "MagnifyIfPossible", new Type[] { typeof(Rect), typeof(bool) })]
        private static bool FixMagnifyWithRotationHandling(GridView __instance, ref Rect rect, bool force)
        {
            // Add null checks
            if (__instance == null || __instance.transform == null || __instance.transform.root == null)
                return true;

            Vector3 currentRotation = __instance.transform.root.eulerAngles;

            // Only apply fix if there's rotation
            if (currentRotation != Vector3.zero)
            {
                // Make sure dictionary is initialized
                if (_originalTransforms == null)
                    _originalTransforms = new Dictionary<GridView, (Vector3, Vector3, Quaternion)>();

                // Store original transform values
                _originalTransforms[__instance] = (
                    __instance.transform.root.eulerAngles,
                    __instance.transform.root.position,
                    __instance.transform.root.rotation
                );

                // Temporarily reset transform for calculation
                __instance.transform.root.eulerAngles = Vector3.zero;
                __instance.transform.root.position = Vector3.zero;
                __instance.transform.root.rotation = Quaternion.identity;

                // Use moderate rect for VR
                rect = new Rect(-1024f, -1024f, 2048f, 2048f);
            }
            return true;
        }


        //-----------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GridView), "MagnifyIfPossible", new Type[] { typeof(Rect), typeof(bool) })]
        private static async void RestoreTransformAfterMagnify(GridView __instance, Task __result)
        {
            // Check if we stored transforms for this instance
            if (_originalTransforms.TryGetValue(__instance, out var originalTransform))
            {
                // Wait for the magnify operation to complete
                if (__result != null)
                {
                    await __result;
                }

                // Restore original transform
                __instance.transform.root.eulerAngles = originalTransform.rot;
                __instance.transform.root.position = originalTransform.pos;
                __instance.transform.root.rotation = originalTransform.quat;

                // Clean up stored data
                _originalTransforms.Remove(__instance);
            }
        }

    }
}