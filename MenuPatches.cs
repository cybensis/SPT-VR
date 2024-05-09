using EFT.InputSystem;
using HarmonyLib;
using System;
using System.Collections.Generic;
using Valve.VR;
using UnityEngine;
using EFT.UI;
using UnityEngine.Rendering.PostProcessing;
using CW2.Animations;
using UnityStandardAssets.ImageEffects;
using TarkovVR.Input;
using static EFT.UI.InventoryScreen;
using EFT.UI.Screens;
using System.Diagnostics;
using System.Collections;
using static EFT.UI.PixelPerfectSpriteScaler;
using EFT.UI.DragAndDrop;
using UnityEngine.EventSystems;

using TarkovVR.cam;
using EFT.InventoryLogic;
using EFT.UI.WeaponModding;
using System.Drawing.Printing;
using System.Diagnostics.PerformanceData;
using System.ComponentModel;
using TMPro;
using System.Reflection;
using JetBrains.Annotations;
using EFT.Hideout;
using RootMotion;
using EFT;



namespace TarkovVR
{
    [HarmonyPatch]
    internal class MenuPatches
    {
        //TODO: Set all the different UI Canvas' to worldspace, change the culling mask or mess around with that shit
        // then position everything

        public static BodyRotationFixer bodyRot;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PixelPerfectSpriteScaler), "method_1")]
        private static bool FixMenuImagesScaling(PixelPerfectSpriteScaler __instance)
        {
            Vector3 lossyScale = new Vector3(1.333f, 1.333f,1.333f);
            float num = Math.Min(lossyScale.x, lossyScale.y);
            if (__instance.image_0 != null)
            {
                if (num.ApproxEquals(__instance.image_0.pixelsPerUnitMultiplier))
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



        public static MenuCameraManager cameraManager;
        private static BoxCollider backingCollider;
        private static GameObject cubeUiMover;
        private static MenuMover menuMover;


        [HarmonyPostfix]
        [HarmonyPatch(typeof(MainMenuController), "method_5")]
        private static void AddAndFixMenuVRCam(MainMenuController __instance)
        {
            if (__instance.environmentUI_0 && __instance.environmentUI_0.environmentUIRoot_0) {
                FixMainMenuCamera(__instance.environmentUI_0.environmentUIRoot_0, __instance.environmentUI_0._alignmentCamera);
            }

            Transform menuUIObject = __instance.commonUI_0.transform.GetChild(0);
            if (menuUIObject)
            {
                
                Canvas menuUICanvas = menuUIObject.GetComponent<Canvas>();
                menuUICanvas.renderMode = RenderMode.WorldSpace;
                menuUICanvas.RectTransform().localScale = new Vector3(1.3333f, 1.3333f, 1.3333f);
                menuUICanvas.RectTransform().anchoredPosition = new Vector3(1280, 720, 0);
                menuUICanvas.RectTransform().sizeDelta = new Vector2(1920, 1080);
                __instance.commonUI_0.transform.position = new Vector3(-1.283f, -1000.66f, 0.9748f);
                __instance.commonUI_0.transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);
                BoxCollider menuCollider = menuUIObject.gameObject.AddComponent<BoxCollider>();
                menuCollider.extents = new Vector3(2560, 1440, 0.5f);


                if (backingCollider == null)
                {
                    GameObject colliderHolder = new GameObject("uiMover");
                    colliderHolder.transform.parent = __instance.environmentUI_0.transform.root;
                    colliderHolder.transform.position = new Vector3(-1.283f, -1001.76f, 0.9748f);
                    colliderHolder.transform.localEulerAngles = new Vector3(90, 0, 0);
                    backingCollider = colliderHolder.AddComponent<BoxCollider>();
                    backingCollider.extents = new Vector3(2222, 2222, 0.5f);
                    cubeUiMover = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    cubeUiMover.transform.parent = backingCollider.transform;
                    cubeUiMover.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
                    cubeUiMover.transform.position = new Vector3(0.017f, -1001.26f, 0.8748f);
                    menuMover = cubeUiMover.AddComponent<MenuMover>();
                    menuMover.raycastReceiver = cubeUiMover.AddComponent<Rigidbody>();
                    menuMover.raycastReceiver.isKinematic = true;
                    menuMover.menuCollider = menuCollider;
                    colliderHolder.layer = LayerMask.NameToLayer("UI");
                    cubeUiMover.layer = LayerMask.NameToLayer("UI");
                    menuMover.commonUI = __instance.commonUI_0.gameObject;
                    menuMover.preloaderUI = __instance.preloaderUI_0.gameObject;
                    menuMover.menuUI = __instance.menuUI_0.gameObject;
                }
            }

        }



        private static void FixMainMenuCamera(EnvironmentUIRoot envUIRoot, Camera alignmentCamera)
        {
            if (envUIRoot && envUIRoot.CameraContainer)
            {
                Transform camContainer = envUIRoot.CameraContainer.GetChild(0);
                camContainer.gameObject.AddComponent<SteamVR_TrackedObject>();
                camContainer.transform.parent.localPosition = new Vector3(0, -1.1f, -0.7f);
                camContainer.transform.parent.localRotation = Quaternion.Euler(0, 345, 0);

                camContainer.GetComponent<PostProcessLayer>().m_Camera = alignmentCamera;
                Camera mainMenuCam = camContainer.GetComponent<Camera>();
                camContainer.tag = "MainCamera";
                if (mainMenuCam)
                {
                    mainMenuCam.cullingMask = -1;
                    mainMenuCam.RemoveAllCommandBuffers();

                }
                camContainer.GetComponent<PhysicsSimulator>().enabled = false;
                camContainer.GetComponent<CameraMotionBlur>().enabled = false;
                if (CamPatches.camRoot == null)
                {
                    //Plugin.MyLog.LogWarning("\n\n CharacterControllerSpawner Spawn " + __instance.gameObject + "\n");
                    CamPatches.camHolder = new GameObject("camHolder");
                    CamPatches.vrOffsetter = new GameObject("vrOffsetter");
                    CamPatches.camRoot = new GameObject("camRoot");
                    CamPatches.camHolder.transform.parent = CamPatches.vrOffsetter.transform;
                    CamPatches.vrOffsetter.transform.parent = CamPatches.camRoot.transform;

                    cameraManager = CamPatches.camHolder.AddComponent<MenuCameraManager>();
                }
                CamPatches.camRoot.transform.position = Camera.main.transform.position;

                if (envUIRoot.ScreenAnchors.Length > 0)
                {
                    Transform envProp = envUIRoot.ScreenAnchors[0].transform;
                    if (envProp.name == "CameraContainer") {
                        envProp.localPosition = new Vector3(2.8f, 0.81f, 2.86f);
                        envProp.localRotation = Quaternion.Euler(273f, 43f, 0);
                    }
                    else {
                        envProp.localPosition = new Vector3(2.5f, 0.31f, 2.36f);
                        envProp.localScale = new Vector3(1.7f, 1.7f, 1.7f);
                        envProp.localRotation = Quaternion.Euler(354, 247, 20);
                    }
                }
            }
        }



        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnvironmentUI), "ShowEnvironment")]
        private static void FixDragHighligwhting(EnvironmentUI __instance) {
            if (__instance.environmentUIRoot_0 && __instance.environmentUIRoot_0.ScreenAnchors.Length > 0)
            {
                Transform envProp = __instance.environmentUIRoot_0.ScreenAnchors[0].transform;
                if (envProp.name == "CameraContainer")
                {
                    envProp.localPosition = new Vector3(2.8f, 0.81f, 2.86f);
                    envProp.localRotation = Quaternion.Euler(273f, 43f, 0);
                }
                else
                {
                    envProp.localPosition = new Vector3(2.5f, 0.31f, 2.36f);
                    envProp.localScale = new Vector3(1.7f, 1.7f, 1.7f);
                    envProp.localRotation = Quaternion.Euler(354, 247, 20);
                    
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GridView), "CalculateItemLocation")]
        private static bool FixDragHighlighting(GridView __instance, GClass2633 itemContext, ref LocationInGrid __result)
        {
            //Plugin.MyLog.LogWarning(itemContext.ItemPosition);
            RectTransform rectTransform = __instance.transform.RectTransform();
            Vector2 size = rectTransform.rect.size;
            Vector2 pivot = rectTransform.pivot;
            Vector2 vector = size * pivot;
            Vector2 cellSizes = itemContext.Item.CalculateCellSize();
            Vector2 pointerPos = bodyRot.uiPointerPos;
            // The highlighted spaces place the corner on the pointer position so I need to move the pointer position
            // so it highlight from the center of the pointer, and in world space each grid item is about 0.084 away
            // from each other so use this to center it properly
            if (itemContext.ItemRotation == ItemRotation.Vertical) {
                float tempHolder = cellSizes.x;
                cellSizes.x = cellSizes.y;
                cellSizes.y = tempHolder;
            }
            pointerPos.x = (float)(pointerPos.x - ((cellSizes.x / 2) * 0.084));
            pointerPos.y = (float)(pointerPos.y - ((cellSizes.y / 2) * 0.0837));
            Vector2 vector2 = rectTransform.InverseTransformPoint(pointerPos);
            vector2 += vector;
            GStruct23 gStruct = itemContext.Item.CalculateRotatedSize(itemContext.ItemRotation);
            vector2 /= 63f;
            vector2.y = (float)__instance.Grid.GridHeight.Value - vector2.y;
            vector2.y -= gStruct.Y;
            __result = new LocationInGrid(Mathf.Clamp(Mathf.RoundToInt(vector2.x), 0, __instance.Grid.GridWidth.Value), Mathf.Clamp(Mathf.RoundToInt(vector2.y), 0, __instance.Grid.GridHeight.Value), itemContext.ItemRotation);
            return false;
        }

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(ItemView), "method_5")]
        //private static void PositionMewnuwwTreeBdranch(ItemView __instance, ref bool __result, PointerEventData eventData)
        //{
        //    //Plugin.MyLog.LogWarning("dragging " + eventData.button + " " + __instance.DraggedItemView);
        //    //(eventData.button == PointerEventData.InputButton.Left && !(__instance.DraggedItemView == null)) { 
        //    //}
        //    Plugin.MyLog.LogWarning("dragging " + eventData.button + " " + __instance.Container.CanDrag(__instance.ItemContext) + " " + __instance.IsSearched + " " + __instance.IsTeammateDogtag + " " + __instance.RemoveError.Value);
        //    if (eventData.button == PointerEventData.InputButton.Left && __instance.Container.CanDrag(__instance.ItemContext) && __instance.IsSearched && !__instance.IsTeammateDogtag && __instance.RemoveError.Value == null)
        //    {
        //        Plugin.MyLog.LogWarning("Success " + __instance.DraggedItemView);
        //        __result = __instance.DraggedItemView == null;
        //    }
        //    else {
        //        __result = false;
        //    }

        //}

        //    [HarmonyPostfix]
        //[HarmonyPatch(typeof(ItemView), "OnEndDrag")]
        //private static void PositionMewnuwTreweBdranch(ItemView __instance)
        //{
        //    StackTrace stackTrace = new StackTrace();
        //    Plugin.MyLog.LogError("End dragging " + stackTrace.ToString());
        //    //(eventData.button == PointerEventData.InputButton.Left && !(__instance.DraggedItemView == null)) { 
        //    //}
        //}

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HideoutScreenOverlay), "Show")]
        private static void AutomaticallyEnterHideout(HideoutScreenOverlay __instance)
        {
            __instance.method_5();
            cameraManager.enabled = false;
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(EnvironmentUIRoot), "RandomRotate")]
        private static bool PreventMenuOptionSelectCamRotation(EnvironmentUIRoot __instance)
        {
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnvironmentUIRoot), "Init")]
        private static void RepositionEnvironmentAfterChange(EnvironmentUIRoot __instance, Camera alignmentCamera, IReadOnlyCollection<EEventType> events, bool isMain)
        {
            Plugin.MyLog.LogMessage("SET ENVIRONEMNT " + __instance);
            FixMainMenuCamera(__instance, alignmentCamera);
        }



        [HarmonyPrefix]
        [HarmonyPatch(typeof(SimpleContextMenu), "CorrectPosition")]
        private static bool FixContentMenuPositioning(SimpleContextMenu __instance)
        {
            Vector3 newPos = __instance.Transform.position;
            newPos.z = 1f;
            __instance.Transform.position = newPos;
            return false;
        }




        [HarmonyPrefix]
        [HarmonyPatch(typeof(DraggedItemView), "method_6")]
        private static bool RotateInventoryItem(DraggedItemView __instance)
        {
            if (!bodyRot.rotated && bodyRot.rightJoyTimeHeld > 0.125)
            {   
                bodyRot.rotated = true;
                __instance.method_2((__instance.ItemContext.ItemRotation == ItemRotation.Horizontal) ? ItemRotation.Vertical : ItemRotation.Horizontal);
                if ((UnityEngine.Object)__instance.ginterface322_0 != null)
                {
                    __instance.ginterface322_0.HighlightItemViewPosition(__instance.ItemContext, __instance.gclass2623_0, preview: false);
                }
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIInputNode), "CorrectPosition")]
        private static bool PreventMenuOptionSeledctwswCamRotation(UIInputNode __instance)
        {
            __instance.transform.localPosition = Vector3.zeroVector;
            return false;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemUiContext), "EditTag")]
        private static void RepositionMessageWindow(ItemUiContext __instance) { __instance._children[__instance._children.Count - 1].transform.localPosition = Vector3.zero; }

        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(GClass755), "CorrectPositionResolution", new[] { typeof(RectTransform), typeof(RectTransform), typeof(GStruct55) })]
        //private static bool PreventMenuOptionwSeledctwswCamRotation(RectTransform transform, RectTransform referenceTransform, GStruct55 margins)
        //{

        //    StackTrace stackTrace = new StackTrace();
        //    Plugin.MyLog.LogInfo("CorrectPositionResolution " + stackTrace.ToString());

        //    //Plugin.MyLog.LogInfo("OnPointerClick " + eventData.position);
        //    return true;
        //}

        [HarmonyPostfix]
        [HarmonyPatch(typeof(StretchArea), "Init")]
        private static void DisableItemDisplayWindowStretchComponents(UIDragComponent __instance)
        {
            __instance.gameObject.active = false;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(DialogWindow<GClass2878>), "Show")]
        private static void DisableItemDisplayWindowStretchCowmponents(DialogWindow<GClass2878> __instance, string title, Action acceptAction, [CanBeNull] Action cancelAction)
        {

            __instance.WaitOneFrame(delegate
            {
                __instance.WindowTransform.localPosition = Vector3.zeroVector;
            });

        }

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(ItemUiContext), "ThrowItem")]
        //private static void PositionDiscardItemWindow(ItemUiContext __instance)
        //{
        //    // Need to delay it here because something else is setting the position
        //    __instance.WaitSeconds(0.2f, delegate
        //    {
        //        StackTrace stackTrace = new StackTrace();
        //        Plugin.MyLog.LogInfo("CorrectPositionResolution " + stackTrace.ToString());
        //        if (__instance._children[__instance._children.Count - 1] is MessageWindow)
        //        {
        //            ((MessageWindow)__instance._children[__instance._children.Count - 1]).WindowTransform.localPosition = Vector3.zeroVector;
        //        }

        //    });
        //}


        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeaponPreview), "Init")]
        private static void SetItemPreviewCameraFoV(WeaponPreview __instance)
        {
            // Need to wait a bit before setting the FoV on this cam because
            // something else is changing it
            __instance.WaitSeconds(0.5f, delegate
            {
                if (__instance.WeaponPreviewCamera)
                { 
                    __instance.WeaponPreviewCamera.fieldOfView = 60;
                }
            });
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemView), "OnClick")]
        private static bool SetItemPreviewCameraFoV(ItemView __instance, PointerEventData.InputButton button, Vector2 position, bool doubleClick)
        {
            if (__instance.ItemUiContext == null || !__instance.IsSearched)
            {
                return false;
            }
            bool flag = SteamVR_Actions._default.RightGrip.state;
            bool flag2 = SteamVR_Actions._default.LeftGrip.state;
            bool flag3 = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            GClass2817<EItemInfoButton> newContextInteractions = __instance.NewContextInteractions;
            switch (button)
            {
                case PointerEventData.InputButton.Left:
                    {
                        if (!(flag || flag3) && doubleClick)
                        {
                            
                            bool flag4 = __instance.ItemController is EFT.Player.PlayerInventoryController;
                            bool flag5 = Comfort.Common.Singleton<SharedGameSettingsClass>.Instance.Game.Settings.ItemQuickUseMode.Value switch
                            {
                                GClass878.EItemQuickUseMode.Disabled => false,
                                GClass878.EItemQuickUseMode.InRaidOnly => flag4,
                                GClass878.EItemQuickUseMode.InRaidAndInLobby => true,
                                _ => throw new ArgumentOutOfRangeException(),
                            };
                            if ((__instance.Item is FoodClass || __instance.Item is MedsClass) && flag5)
                            {
                                if (!newContextInteractions.ExecuteInteraction(EItemInfoButton.Use))
                                {
                                    newContextInteractions.ExecuteInteraction(EItemInfoButton.UseAll);
                                }
                                break;
                            }
                            if (newContextInteractions.ExecuteInteraction(__instance.Item.IsContainer ? EItemInfoButton.Open : EItemInfoButton.Inspect))
                            {
                                break;
                            }
                        }
                        SimpleTooltip tooltip = __instance.ItemUiContext.Tooltip;
                        if (flag || flag3)
                        {
                            GStruct374 gStruct = (flag ? __instance.ItemUiContext.QuickFindAppropriatePlace(__instance.ItemContext, __instance.ItemController) : __instance.ItemUiContext.QuickMoveToSortingTable(__instance.Item));
                            if (gStruct.Failed || !__instance.ItemController.CanExecute(gStruct.Value))
                            {
                                break;
                            }
                            if (gStruct.Value is GInterface277 { ItemsDestroyRequired: not false } gInterface)
                            {
                                NotificationManagerClass.DisplayWarningNotification(new GClass3045(__instance.Item, gInterface.ItemsToDestroy).GetLocalizedDescription());
                                break;
                            }
                            string itemSound = __instance.Item.ItemSound;
                            __instance.ItemController.RunNetworkTransaction(gStruct.Value);
                            if (tooltip != null)
                                tooltip.Close();
                            {
                            }
                            Comfort.Common.Singleton<GUISounds>.Instance.PlayItemSound(itemSound, EInventorySoundType.pickup);
                        }
                        else if (flag2)
                        {
                            if (newContextInteractions.IsInteractionAvailable(EItemInfoButton.Equip))
                            {
                                __instance.ItemUiContext.QuickEquip(__instance.Item).HandleExceptions();
                            }
                            if (tooltip != null)
                            {
                                tooltip.Close();
                            }
                        }
                        else if (__instance.IsBeingLoadedMagazine.Value || __instance.IsBeingUnloadedMagazine.Value)
                        {
                            __instance.ItemController.StopProcesses();
                        }
                        break;
                    }
                case PointerEventData.InputButton.Right:
                    __instance.ShowContextMenu(position);
                    break;
                case PointerEventData.InputButton.Middle:
                    if (!__instance.ExecuteMiddleClick())
                    {
                        newContextInteractions.ExecuteInteraction(EItemInfoButton.CheckMagazine);
                    }
                    break;
            }
            return false;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(TraderScreensGroup), "Awake")]
        private static void PositionTraderSeparatorBars(TraderScreensGroup __instance)
        {
            // Need to wait a bit before setting the FoV on this cam because
            // something else is changing it
            if (__instance._traderCardsContainer) {
                RectTransform separator = ((RectTransform)__instance._traderCardsContainer.parent.GetChild(0).transform);
                separator.sizeDelta = new Vector2(1920, 2);
                separator.localPosition = new Vector3(0, 87.5f, 0);
                separator = ((RectTransform)__instance._traderCardsContainer.parent.GetChild(1).transform);
                separator.sizeDelta = new Vector2(1920, 2);
                separator.localPosition = new Vector3(0, -87.5f, 0);
            }
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(ScrollRectNoDrag), "OnScroll")]
        private static void SetItemPwreviewCameraFoV(ScrollRectNoDrag __instance, PointerEventData data)
        {
            Plugin.MyLog.LogError("OnScroll " + data.scrollDelta);

        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(DraggedItemView), "OnDrag")]
        private static void PositionDraggedItemIcon(DraggedItemView __instance, PointerEventData eventData)
        {
            if (!(__instance.RectTransform_0 == null))
            {
                __instance.RectTransform_0.position = eventData.position;
                Vector3 newPos = __instance.RectTransform_0.localPosition;
                //newPos.y -= __instance.RectTransform_0.sizeDelta.y / 2;
                //newPos.x -= __instance.RectTransform_0.sizeDelta.x / 2;
                newPos.z = 0f;
                __instance.RectTransform_0.localPosition = newPos;
                RectTransform rectTransform = __instance.transform.RectTransform();
                Vector2 size = rectTransform.rect.size;
                Vector2 pivot = rectTransform.pivot;
                Vector2 vector = size * pivot * rectTransform.lossyScale;
                Vector2 vector2 = __instance.transform.position;
                __instance.ItemContext.SetPosition(vector2, vector2 - vector);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SlotView), "Start")]
        private static void AddBoxColliderToSlotComponents(SlotView __instance)
        {
            if (!__instance.Transform.GetComponent<BoxCollider>()) { 
                __instance.GameObject.AddComponent<BoxCollider>().extents = new Vector3(__instance.RectTransform.sizeDelta.x, __instance.RectTransform.sizeDelta.y, 1);
            }
        }


      


        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemInfoWindowLabels), "method_4")]
        private static bool ApplyItemDetailWindowViewerRotate(ItemInfoWindowLabels __instance, PointerEventData pointerData)
        {
            float xRot = pointerData.pressPosition.x - (pointerData.position.x * -1) * 10;
            float yRot = ((pointerData.pressPosition.y - pointerData.position.y) * -1) * 10;
            __instance.weaponPreview_0.Rotate(xRot, yRot, 0f, 0f);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIDragComponent), "UnityEngine.EventSystems.IDragHandler.OnDrag")]
        private static bool FixItemDetailWindowDrag(UIDragComponent __instance, PointerEventData eventData)
        {

            //StackTrace stackTrace = new StackTrace();
            //Plugin.MyLog.LogError("End dragging " + stackTrace.ToString());
            if (__instance.method_1())
            {
                //__instance._target.localPosition = eventData.position - __instance.vector2_0;
                __instance._target.position = eventData.position;
                Vector3 newPos = __instance._target.localPosition;
                newPos.y -= ((RectTransform)__instance._target.transform).sizeDelta.y / 2;
                newPos.z = 0f;
                __instance._target.localPosition = newPos;

            }
            return false;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(ScrollRectNoDrag), "OnEnable")]
        private static void WidenScrollbars(ScrollRectNoDrag __instance)
        {
            if (__instance.verticalScrollbar)
                __instance.verticalScrollbar.transform.localScale = new Vector3(2.5f, __instance.verticalScrollbar.transform.localScale.y, __instance.verticalScrollbar.transform.localScale.z);
          
        }


        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(UIDragComponent), "UnityEngine.EventSystems.IBeginDragHandler.OnBeginDrag")]
        //private static bool PreventMenuOptionSeledctCamRotatiwon(UIDragComponent __instance, PointerEventData eventData)
        //{
        //    if (__instance.method_1())
        //    {
        //        __instance._target.position = eventData.position - __instance.vector2_0;
        //    }
        //    //    StackTrace stackTrace = new StackTrace();
        //    //Plugin.MyLog.LogError("End dragging " + stackTrace.ToString());
        //    //Plugin.MyLog.LogWarning("dragging " + eventData.position);
        //    return false;
        //}

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PreloaderUI), "Awake")]
        private static void PositionDisplayOverlay(PreloaderUI __instance)
        {
            if (__instance.transform.childCount > 0) { 
                Canvas overlayCanvas = __instance.transform.GetChild(0).GetComponent<Canvas>();
                if (overlayCanvas) { 
                    overlayCanvas.renderMode = RenderMode.WorldSpace;
                    __instance.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
                    __instance.transform.position = new Vector3(0f, -999.9333f, 1);
                    overlayCanvas.transform.localScale = new Vector3(1, 1, 1);
                    overlayCanvas.transform.localPosition = Vector3.zero;
                }   
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MenuUI), "Awake")]
        private static void PositionOtherMenu(PreloaderUI __instance) {
            if (__instance.transform.childCount > 0)
            {
                Canvas otherMenuCanvas = __instance.transform.GetChild(0).GetComponent<Canvas>();
                if (otherMenuCanvas)
                {
                    otherMenuCanvas.renderMode = RenderMode.WorldSpace;
                    __instance.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
                    __instance.transform.position = new Vector3(0f, -999.9333f, 1);
                    otherMenuCanvas.transform.localScale = new Vector3(1, 1, 1);
                    otherMenuCanvas.transform.localPosition = Vector3.zero;
                }
            }
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(InfoWindow), "Show")]
        private static bool PositionItemDisplayWindow(InfoWindow __instance, GClass2623 itemContext, GInterface266 itemController, TraderClass trader, Action onSelectedAction, ItemUiContext itemUiContext, Action onClosedAction, GClass2817<EItemInfoButton> contextInteractions)
        {
            __instance.Show(onClosedAction);
            __instance.ShowGameObject();
            __instance.action_1 = onSelectedAction;
            __instance.gclass2623_0 = itemContext.CreateChild(itemContext.Item);
            __instance.UI.AddDisposable(__instance.gclass2623_0);
            __instance.item_0 = __instance.gclass2623_0.Item;
            __instance.ginterface266_0 = itemController;
            __instance.iitemOwner_0 = __instance.item_0.Owner;
            __instance.iitemOwner_0?.RegisterView(__instance);
            __instance.gclass1921_0 = trader;
            if (__instance.gclass1921_0 != null)
            {
                __instance.UI.AddDisposable(__instance.gclass1921_0.AssortmentChanged.Bind(__instance.method_0));
            }
            __instance.ginterface266_0.OnChamberCheck += __instance.method_3;
            __instance.ginterface266_0.ExamineEvent += __instance.method_2;
            itemUiContext.InitSpecificationPanel(__instance._itemSpecificationPanel, __instance.gclass2623_0, contextInteractions);
            __instance.method_4();
            System.Random rnd = new System.Random();
            __instance.transform.position = new Vector3(0f, -999.9333f, 1f);
            __instance.transform.localPosition = new Vector3(rnd.Next(-200, 201), rnd.Next(-200, 201), 0f);
            __instance.gclass2623_0.OnCloseWindow += __instance.Close;
                //CorrectPosition();
            __instance.WaitOneFrame(delegate
            {
                //CorrectPosition();
                System.Random rnd = new System.Random();
                __instance.transform.position = new Vector3(0f, -999.9333f, 1f);
                __instance.transform.localPosition = new Vector3(rnd.Next(-200, 201), rnd.Next(-200, 201), 0f);
            });
            return false;
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemView), "Update")]
        private static bool FixDragAndDropButton(ItemView __instance)
        {

            if (__instance.pointerEventData_0 != null && SteamVR_Actions._default.RightTrigger.axis < 0.7)
            {
                bodyRot.EndDrop();
                __instance.OnEndDrag(__instance.pointerEventData_0);
            }
            if (__instance.IsSearched)
            {
                if (__instance.IsBeingDrained.Value)
                {
                    __instance.UpdateInfo();
                }
                if (Math.Abs(__instance.float_1 - __instance._mainImageAlpha) > 0.01f)
                {
                    __instance.float_1 = __instance._mainImageAlpha;
                    Color color = __instance.MainImage.color;
                    color.a = __instance._mainImageAlpha;
                    __instance.MainImage.color = color;
                }
            }
            return false;
        }
    }
}