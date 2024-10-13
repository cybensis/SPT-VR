using HarmonyLib;
using System;
using System.Collections.Generic;
using Valve.VR;
using UnityEngine;
using EFT.UI;
using UnityEngine.Rendering.PostProcessing;
using CW2.Animations;
using UnityStandardAssets.ImageEffects;
using static EFT.UI.PixelPerfectSpriteScaler;
using EFT.UI.DragAndDrop;
using UnityEngine.EventSystems;
using EFT.InventoryLogic;
using EFT.UI.WeaponModding;
using EFT.Hideout;
using EFT;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.UI;
using System.Reflection.Emit;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Diagnostics;
using System.Threading.Tasks;
using TMPro;
using System.Text;
using EFT.UI.Ragfair;
using UnityEngine.SceneManagement;
using static EFT.UI.PlayerProfilePreview;
using UnityEngine.XR;
using static EFT.UI.MenuScreen;
using static EFT.UI.BattleUiVoipPanel;
using EFT.UI.Screens;
using TarkovVR.Patches.UI;
using UnityEngine.UIElements;
using static EFT.UI.WeaponModding.WeaponModdingScreen;
using EFT.UI.SessionEnd;
using static EFT.UI.SessionEnd.SessionResultExitStatus;
using static EFT.UI.TraderDialogScreen;
using EFT.UI.Settings;
using static EFT.UI.ScreenPositionAnchor;
using System.Runtime.InteropServices;
using UnityEngine.UI;
using TarkovVR.Source.Settings;
using static EFT.BaseLocalGame<EFT.HideoutPlayerOwner>;
using EFT.UI.Matchmaker;
using EFT.UI.Builds;
using EFT.UI.Insurance;
using TarkovVR.Source.Weapons;



namespace TarkovVR.Patches.Misc
{
    [HarmonyPatch]
    internal class MenuPatches
    {
        private static BoxCollider backingCollider;
        private static GameObject cubeUiMover;
        private static MenuMover menuMover;
        private static EnvironmentUI environmentUi;


        public static VRUIInteracter vrUiInteracter;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PixelPerfectSpriteScaler), "method_1")]
        private static bool FixMenuImagesScaling(PixelPerfectSpriteScaler __instance)
        {
            Vector3 lossyScale = new Vector3(1.333f, 1.333f, 1.333f);
            float num = 1.333f;
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




        //[HarmonyPatch(typeof(PixelPerfectSpriteScaler), "method_1")]
        //public static class FixMenuImagesScaling
        //{
        //    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        //    {
        //        var codes = new List<CodeInstruction>(instructions);

        //        for (int i = 0; i < codes.Count; i++)
        //        {
        //            // Find the call to get_lossyScale and Math.Min
        //            if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand is MethodInfo methodInfo && methodInfo.Name == "get_lossyScale")
        //            {
        //                // Replace the call to get_lossyScale with the constant value 1.333
        //                codes[i] = new CodeInstruction(OpCodes.Ldc_R4, 1.333f); // Load constant 1.333
        //                                                                        // Remove the instructions that load x and y and call Math.Min
        //                //codes.RemoveAt(i - 1); // Remove stloc.0
        //                //codes.RemoveAt(i); // Remove stloc.0
        //                codes.RemoveAt(i + 1); // Remove stloc.0
        //                codes.RemoveAt(i + 1); // Remove ldloc.0
        //                codes.RemoveAt(i + 1); // Remove ldfld float32 x
        //                codes.RemoveAt(i + 1); // Remove ldloc.0
        //                codes.RemoveAt(i + 1); // Remove ldfld float32 y
        //                codes.RemoveAt(i + 1); // Remove call Math.Min
        //                codes.Insert(i + 1, new CodeInstruction(OpCodes.Stloc_1)); // Store the constant into num (stloc.1)
        //                break;
        //            }
        //        }

        //        return codes.AsEnumerable();
        //    }
        //}





        [HarmonyPostfix]
        [HarmonyPatch(typeof(MainMenuController), "method_5")]
        private static void AddAndFixMenuVRCam(MainMenuController __instance)
        {

            UnityEngine.Cursor.lockState = CursorLockMode.Locked;

            if (__instance.environmentUI_0 && __instance.environmentUI_0.environmentUIRoot_0)
            {
                FixMainMenuCamera();
            }
            VRGlobals.commonUi = __instance.commonUI_0.transform;
            //__instance.commonUI_0.transform.GetChild(0).RectTransform().sizeDelta = new Vector2(2560, 1440);

            VRGlobals.preloaderUi = __instance.preloaderUI_0.transform;
            //__instance.preloaderUI_0.transform.GetChild(0).RectTransform().sizeDelta = new Vector2(2560, 1440);

            VRGlobals.menuUi = __instance.menuUI_0.transform;
            //__instance.menuUI_0.transform.GetChild(0).RectTransform().sizeDelta = new Vector2(2560, 1440);

            environmentUi = __instance.environmentUI_0;

            if (VRGlobals.menuVRManager) { 
                VRGlobals.menuVRManager.RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
            }

            PositionMainMenuUi();

        }



        public static void PositionMainMenuUi()
        {
            PositionCommonUi();
            PositionMenuUi();
            PositionPreloaderUi();
        }


        private static void PositionMenuUi()
        {
            if (VRGlobals.menuUi == null || VRGlobals.menuUi.transform.childCount == 0)
                return;
            VRGlobals.menuUi.transform.parent = null;
            Canvas otherMenuCanvas = VRGlobals.menuUi.GetChild(0).GetComponent<Canvas>();
            if (otherMenuCanvas)
            {
                otherMenuCanvas.enabled = true;
                PreloaderUI.DontDestroyOnLoad(VRGlobals.menuUi.gameObject);
                VRGlobals.menuUi.transform.eulerAngles = Vector3.zero;
                otherMenuCanvas.renderMode = RenderMode.WorldSpace;
                VRGlobals.menuUi.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
                VRGlobals.menuUi.transform.position = new Vector3(0f, -999.9333f, 1);
                otherMenuCanvas.transform.localScale = new Vector3(1, 1, 1);
                otherMenuCanvas.transform.localPosition = Vector3.zero;
            }
        }
        private static void PositionPreloaderUi()
        {
            if (VRGlobals.preloaderUi.transform.childCount > 0)
            {
                Canvas overlayCanvas = VRGlobals.preloaderUi.GetChild(0).GetComponent<Canvas>();
                if (overlayCanvas)
                {
                    overlayCanvas.enabled = true;
                    PreloaderUI.DontDestroyOnLoad(VRGlobals.preloaderUi.gameObject);
                    VRGlobals.preloaderUi.transform.eulerAngles = Vector3.zero;
                    overlayCanvas.renderMode = RenderMode.WorldSpace;
                    VRGlobals.preloaderUi.transform.localScale = new Vector3(0.0015f, 0.0015f, 0.0015f);
                    VRGlobals.preloaderUi.transform.position = new Vector3(0f, -999.9333f, 1);
                    overlayCanvas.transform.localScale = new Vector3(1, 1, 1);
                    overlayCanvas.transform.localPosition = Vector3.zero;
                }
            }
        }



        private static void PositionCommonUi()
        {
            Transform menuUIObject = VRGlobals.commonUi.transform.GetChild(0);
            if (menuUIObject)
            {

                Canvas menuUICanvas = menuUIObject.GetComponent<Canvas>();
                menuUICanvas.enabled = true;
                menuUICanvas.renderMode = RenderMode.WorldSpace;
                menuUICanvas.RectTransform().localScale = new Vector3(1.3333f, 1.3333f, 1.3333f);
                menuUICanvas.RectTransform().anchoredPosition = new Vector3(1280, 720, 0);
                menuUICanvas.RectTransform().sizeDelta = new Vector2(1920, 1080);
                CommonUI.DontDestroyOnLoad(VRGlobals.commonUi.gameObject);
                VRGlobals.commonUi.transform.position = new Vector3(-1.283f, -1000.66f, 0.9748f);
                VRGlobals.commonUi.transform.localScale = new Vector3(0.001f, 0.001f, 0.001f);
                VRGlobals.commonUi.transform.eulerAngles = Vector3.zero;
                if (!menuUIObject.GetComponent<BoxCollider>())
                {
                    BoxCollider menuCollider = menuUIObject.gameObject.AddComponent<BoxCollider>();
                    menuCollider.extents = new Vector3(2560, 1440, 0.5f);
                }
                else if (!VRGlobals.inGame) {
                    // Someetimes when coming out of a raid the colliders bounds is way off so fix it here,
                    PreloaderUI.Instance.WaitOneFrame(delegate {
                        BoxCollider menuCollider = menuUIObject.gameObject.GetComponent<BoxCollider>();
                        menuCollider.enabled = false;
                        menuCollider.enabled = true;
                        menuCollider.center = Vector3.zero;
                    });

                }

                //3.4132 1.9199 0.0007
                //if (backingCollider == null)
                //{
                //    GameObject colliderHolder = new GameObject("uiMover");
                //    colliderHolder.transform.parent = environmentUi.transform.root;
                //    colliderHolder.transform.position = new Vector3(-1.283f, -1001.76f, 0.9748f);
                //    colliderHolder.transform.localEulerAngles = new Vector3(90, 0, 0);
                //    backingCollider = colliderHolder.AddComponent<BoxCollider>();
                //    backingCollider.extents = new Vector3(2222, 2222, 0.5f);
                //    cubeUiMover = GameObject.CreatePrimitive(PrimitiveType.Cube);
                //    cubeUiMover.transform.parent = backingCollider.transform;
                //    cubeUiMover.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
                //    cubeUiMover.transform.position = new Vector3(0.017f, -1001.26f, 0.8748f);
                //    menuMover = cubeUiMover.AddComponent<MenuMover>();
                //    menuMover.raycastReceiver = cubeUiMover.AddComponent<Rigidbody>();
                //    menuMover.raycastReceiver.isKinematic = true;
                //    menuMover.menuCollider = menuCollider;
                //    colliderHolder.layer = LayerMask.NameToLayer("UI");
                //    cubeUiMover.layer = LayerMask.NameToLayer("UI");
                //    menuMover.commonUI = VRGlobals.commonUi.gameObject;
                //    menuMover.preloaderUI = VRGlobals.preloaderUi.gameObject;
                //    menuMover.menuUI = VRGlobals.menuUi.gameObject;
                //}
            }
        }
        private static Quaternion camHolderRot;

        public static void FixMainMenuCamera()
        {
            if (environmentUi)
            {
                Transform camContainer = environmentUi.environmentUIRoot_0.CameraContainer.GetChild(0);
                if (!camContainer.gameObject.GetComponent<SteamVR_TrackedObject>())
                    camContainer.gameObject.AddComponent<SteamVR_TrackedObject>();

                //camContainer.GetComponent<PostProcessLayer>().m_Camera = environmentUi._alignmentCamera;
                Camera mainMenuCam = camContainer.GetComponent<Camera>();
                Vector3 newCamHolderPos = mainMenuCam.transform.localPosition * -1;
                newCamHolderPos.y += 0.1f;
                newCamHolderPos.z = -0.6f;
                camContainer.transform.parent.localPosition = newCamHolderPos;
                camContainer.localRotation = Quaternion.identity;
                camHolderRot = Quaternion.Euler(0, mainMenuCam.transform.localEulerAngles.y * -1, 0);
                camContainer.transform.parent.localRotation = camHolderRot;
                camContainer.tag = "MainCamera";
                if (mainMenuCam)
                {
                    if (!camContainer.FindChild("uiCam"))
                    {
                        GameObject uiCamHolder = new GameObject("uiCam");
                        uiCamHolder.transform.parent = camContainer.transform;
                        uiCamHolder.transform.localRotation = Quaternion.identity;
                        uiCamHolder.transform.localPosition = Vector3.zero;
                        Camera uiCam = uiCamHolder.AddComponent<Camera>();
                        uiCam.depth = 12;
                        uiCam.nearClipPlane = VRGlobals.NEAR_CLIP_PLANE;
                        uiCam.cullingMask = 32;
                        uiCam.clearFlags = CameraClearFlags.Depth;
                    }
                    //mainMenuCam.cullingMask = -1;
                    mainMenuCam.RemoveAllCommandBuffers();

                }
                camContainer.GetComponent<PhysicsSimulator>().enabled = false;
                camContainer.GetComponent<CameraMotionBlur>().enabled = false;
                if (VRGlobals.camRoot == null)
                {
                    //Plugin.MyLog.LogWarning("\n\n CharacterControllerSpawner Spawn " + __instance.gameObject + "\n");
                    VRGlobals.camHolder = new GameObject("camHolder");
                    VRGlobals.vrOffsetter = new GameObject("vrOffsetter");
                    VRGlobals.camRoot = new GameObject("camRoot");
                    VRGlobals.camHolder.transform.parent = VRGlobals.vrOffsetter.transform;
                    VRGlobals.vrOffsetter.transform.parent = VRGlobals.camRoot.transform;
                    VRGlobals.vrOffsetter.transform.localRotation = camHolderRot;

                    VRGlobals.menuVRManager = VRGlobals.camHolder.AddComponent<MenuVRManager>();
                }
                VRGlobals.camRoot.transform.position = camContainer.transform.parent.position;
                PositionMenuEnvironmentProps();
            }
        }



        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnvironmentUI), "RefreshEnvironmentAsync")]
        public static  void PositionMenuPropsAfterRefresh(Task __result)
        {
            __result.ContinueWith(task =>
            {
                if (task.IsCompleted)
                {
                    PositionMenuEnvironmentProps();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }


        public static void PositionMenuEnvironmentProps()
        {
            if (environmentUi && environmentUi.environmentUIRoot_0.ScreenAnchors.Length > 0)
            {
                Transform envProp = environmentUi.environmentUIRoot_0.ScreenAnchors[0].transform;
                Plugin.MyLog.LogInfo("Position Environment Props: " + envProp);
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
        private static bool FixDragHighlighting(GridView __instance, ItemContextClass itemContext, ref LocationInGrid __result)
        {
            //Plugin.MyLog.LogWarning(itemContext.ItemPosition);
            RectTransform rectTransform = __instance.transform.RectTransform();
            Vector2 size = rectTransform.rect.size;
            Vector2 pivot = rectTransform.pivot;
            Vector2 vector = size * pivot;
            Vector2 cellSizes = itemContext.Item.CalculateCellSize();
            Vector3 pointerPos = vrUiInteracter.uiPointerPos;
            // The highlighted spaces place the corner on the pointer position so I need to move the pointer position
            // so it highlight from the center of the pointer, and in world space each grid item is about 0.084 away
            // from each other so use this to center it properly
            if (itemContext.ItemRotation == ItemRotation.Vertical)
            {
                float tempHolder = cellSizes.x;
                cellSizes.x = cellSizes.y;
                cellSizes.y = tempHolder;
            }
            //Vector2 should come out to an x,y value that represents the position in the grid
            Vector2 vector2 = rectTransform.InverseTransformPoint(pointerPos);
            //Plugin.MyLog.LogWarning( vector2 + "   |     " + pointerPos);

            vector2 += vector;
            XYCellSizeStruct gStruct = itemContext.Item.CalculateRotatedSize(itemContext.ItemRotation);
            vector2 /= 63f;
            vector2.y = __instance.Grid.GridHeight.Value - vector2.y;
            vector2.y -= gStruct.Y;
            vector2.x = vector2.x - cellSizes.x / 2;
            vector2.y = vector2.y + cellSizes.y / 2;

            __result = new LocationInGrid(Mathf.Clamp(Mathf.RoundToInt(vector2.x), 0, __instance.Grid.GridWidth.Value), Mathf.Clamp(Mathf.RoundToInt(vector2.y), 0, __instance.Grid.GridHeight.Value), itemContext.ItemRotation);
            //Quaternion gridRotation = rectTransform.rotation;
            //vector2 = gridRotation * vector2;
            // a uiPointerPos of -3.4 and 1.5 on X left most grid and Y in the middle, seems to be working just fine
            return false;
        }



        [HarmonyPrefix]
        [HarmonyPatch(typeof(EnvironmentUIRoot), "RandomRotate")]
        private static bool PreventMenuOptionSelectCamRotation(EnvironmentUIRoot __instance)
        {
            return false;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EnvironmentUIRoot), "method_2")]
        private static bool PreventMenuReturnCamRotatiown(EnvironmentUIRoot __instance)
        {
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnvironmentUIRoot), "Init")]
        private static void RepositionEnvironmentAfterChange(EnvironmentUIRoot __instance, Camera alignmentCamera, IReadOnlyCollection<EEventType> events, bool isMain)
        {
            if (environmentUi = __instance.transform.parent.GetComponent<EnvironmentUI>())
            {
                FixMainMenuCamera();
            }
        }



        [HarmonyPrefix]
        [HarmonyPatch(typeof(SimpleContextMenu), "CorrectPosition")]
        private static bool FixContentMenuPositioning(SimpleContextMenu __instance)
        {
            if (!VRGlobals.inGame)
            {
                Vector3 newRootPos = __instance.Transform.position;
                newRootPos.z = 1f;
                __instance.Transform.position = newRootPos;
            }
            else {
                __instance.transform.position = MenuPatches.vrUiInteracter.pressPosition;
            }


            Vector3 newPos = __instance.transform.localPosition;
            if (__instance.transform.parent.name == "InteractionButtonsContainer")
            {
                newPos.z = 0;
                newPos.x = (__instance.transform.parent as RectTransform).sizeDelta.x;
            }
            __instance.WaitOneFrame(delegate { 
                if (__instance.name == "ItemContextSubMenu(Clone)" && vrUiInteracter.hitObject && vrUiInteracter.hitObject.transform as RectTransform)
                {
                    __instance.transform.position = vrUiInteracter.hitObject.transform.position;
                    Vector3 newPos = __instance.transform.localPosition;
                    newPos.z = 0;
                    newPos.y = newPos.y + (__instance.RectTransform.sizeDelta.y / 2);
                    newPos.x = (vrUiInteracter.hitObject.transform as RectTransform).sizeDelta.x;
                    if (__instance.transform.parent.name == "ItemInfoWindowTemplate(Clone)")
                        newPos.x = ((vrUiInteracter.hitObject.transform as RectTransform).sizeDelta.x / 2) * -1;

                    __instance.transform.localPosition = newPos;
                }
            });
            newPos.z = 0;
            __instance.transform.localPosition = newPos;

            return false;
        }




        [HarmonyPrefix]
        [HarmonyPatch(typeof(DraggedItemView), "method_6")]
        private static bool RotateInventoryItem(DraggedItemView __instance)
        {
            if (!vrUiInteracter.rotated && vrUiInteracter.rightJoyTimeHeld > 0.125)
            {
                vrUiInteracter.rotated = true;
                __instance.method_2(__instance.ItemContext.ItemRotation == ItemRotation.Horizontal ? ItemRotation.Vertical : ItemRotation.Horizontal);
                if ((UnityEngine.Object)__instance.iContainer != null)
                {
                    __instance.iContainer.HighlightItemViewPosition(__instance.ItemContext, __instance.itemContextAbstractClass, preview: false);
                }
                Vector3 newRot = __instance._mainImage.transform.localEulerAngles;
                newRot.y = Quaternion.Inverse(__instance.transform.localRotation).eulerAngles.y;
                __instance._mainImage.transform.localEulerAngles = newRot;

            }
            
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIInputNode), "CorrectPosition")]
        private static bool PreventMenuOptionSelectCamRotation(UIInputNode __instance)
        {
            __instance.transform.localPosition = Vector3.zeroVector;
            return false;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(ItemUiContext), "EditTag")]
        private static void RepositionMessageWindow(ItemUiContext __instance) { __instance._children[__instance._children.Count - 1].transform.localPosition = Vector3.zero; }



        [HarmonyPostfix]
        [HarmonyPatch(typeof(StretchArea), "Init")]
        private static void DisableItemDisplayWindowStretchComponents(UIDragComponent __instance)
        {
            __instance.gameObject.active = false;
        }


        // Some piece of code keeps repositioning this shit and I can't figure out what 
        // so just wait a frame and set it. This is for the delete/confirm windows
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DialogWindow<GClass3109>), "Show")]
        private static void DisplayDialogWindow(DialogWindow<GClass3109> __instance, string title, Action acceptAction, Action cancelAction)
        {
            __instance.WaitOneFrame(delegate
            {
                __instance.WindowTransform.localPosition = Vector3.zero;
            });
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RepairWindow), "Show")]
        private static void DisplayDialogWindow(RepairWindow __instance)
        {
            __instance.WaitOneFrame(delegate
            {
                __instance.transform.localPosition = Vector3.zero;
            });
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(WeaponPreview), "method_2")]
        private static bool FixWeaponPreviewCamera(WeaponPreview __instance)
        {
            // Need to wait a bit before setting the FoV on this cam because
            // something else is changing it
            __instance._cameraTemplate.stereoTargetEye = StereoTargetEyeMask.None;
            __instance._cameraTemplate.fieldOfView = 22;

            return true;
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemView), "OnClick")]
        private static bool HandleItemClick(ItemView __instance, PointerEventData.InputButton button, Vector2 position, bool doubleClick)
        {
            if (__instance.ItemUiContext == null || !__instance.IsSearched)
            {
                return false;
            }
            // Use left and right grip to simulate ccontrol and alt click for items
            bool flag = SteamVR_Actions._default.RightGrip.state;
            bool flag2 = SteamVR_Actions._default.LeftGrip.state;
            bool flag3 = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
            global::ItemInfoInteractionsAbstractClass<EItemInfoButton> newContextInteractions = __instance.NewContextInteractions;
            switch (button)
            {
                case PointerEventData.InputButton.Left:
                    {
                        if (!(flag || flag3) && doubleClick)
                        {

                            bool flag4 = __instance.ItemController is EFT.Player.PlayerInventoryController;
                            bool flag5 = Comfort.Common.Singleton<SharedGameSettingsClass>.Instance.Game.Settings.ItemQuickUseMode.Value switch
                            {
                                GClass960.EItemQuickUseMode.Disabled => false,
                                GClass960.EItemQuickUseMode.InRaidOnly => flag4,
                                GClass960.EItemQuickUseMode.InRaidAndInLobby => true,
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
                            GStruct413 gStruct = flag ? __instance.ItemUiContext.QuickFindAppropriatePlace(__instance.ItemContext, __instance.ItemController) : __instance.ItemUiContext.QuickMoveToSortingTable(__instance.Item);
                            if (gStruct.Failed || !__instance.ItemController.CanExecute(gStruct.Value))
                            {
                                break;
                            }
                            if (gStruct.Value is IDestroyResult { ItemsDestroyRequired: not false } destroyResult)
                            {
                                NotificationManagerClass.DisplayWarningNotification(new GClass3344(__instance.Item, destroyResult.ItemsToDestroy).GetLocalizedDescription());
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
            if (__instance._traderCardsContainer)
            {
                RectTransform separator = (RectTransform)__instance._traderCardsContainer.parent.FindChild("SeparatorTop");
                separator.sizeDelta = new Vector2(1920, 2);
                separator.localPosition = new Vector3(0, 87.5f, 0);
                separator = (RectTransform)__instance._traderCardsContainer.parent.FindChild("SeparatorBottom");
                separator.sizeDelta = new Vector2(1920, 2);
                separator.localPosition = new Vector3(0, -87.5f, 0);
            }
        }



        [HarmonyPostfix]
        [HarmonyPatch(typeof(DraggedItemView), "OnDrag")]
        private static void PositionDraggedItemIcon(DraggedItemView __instance, PointerEventData eventData)
        {
            if (!(__instance.RectTransform_0 == null))
            {
                __instance.RectTransform_0.position = vrUiInteracter.uiPointerPos;
                __instance.RectTransform_0.localEulerAngles = VRGlobals.commonUi.eulerAngles;

            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SlotView), "Start")]
        private static void AddBoxColliderToSlotComponents(SlotView __instance)
        {
            if (!__instance.Transform.GetComponent<BoxCollider>())
                __instance.GameObject.AddComponent<BoxCollider>().extents = new Vector3(__instance.RectTransform.sizeDelta.x, __instance.RectTransform.sizeDelta.y, 1);
        }


        // Stupid hover thingy blocks the autofill from being selected
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HoverTooltipArea), "Show")]
        private static void HideTraderAutoFillHover(HoverTooltipArea __instance)
        {
            if (__instance.name == "Hover") { 
                __instance.gameObject.active = false;
                if (__instance.GetComponent<UnityEngine.UI.Image>()) {
                    __instance.GetComponent<UnityEngine.UI.Image>().enabled = false;
                }
            }
        }



        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemInfoWindowLabels), "method_4")]
        private static bool ApplyItemDetailWindowViewerRotate(ItemInfoWindowLabels __instance, PointerEventData pointerData)
        {
            //float xRot = (MenuPatches.vrUiInteracter.pressPosition.x - MenuPatches.vrUiInteracter.uiPointerPos.x) * -1 * 10;
            //float yRot = (MenuPatches.vrUiInteracter.pressPosition.y - MenuPatches.vrUiInteracter.uiPointerPos.y) * -1 * 10;

            // Get the world positions
            Vector3 pressPosition = vrUiInteracter.pressPosition;
            Vector3 currentPointerPosition = vrUiInteracter.uiPointerPos;

            // Transform the world positions to the local space of the UI/object
            Vector3 localPressPosition = VRGlobals.preloaderUi.transform.InverseTransformPoint(pressPosition);
            Vector3 localCurrentPointerPosition = VRGlobals.preloaderUi.transform.InverseTransformPoint(currentPointerPosition);

            // Calculate the difference in local space, invert it to make it rotate the correction direction, then divide by 15 so it doesn't rotate so quickly
            float yRot = (localPressPosition.y - localCurrentPointerPosition.y) * -1;
            float xRot = (localPressPosition.x - localCurrentPointerPosition.x) * -1;
            __instance.weaponPreview_0.Rotate(xRot, yRot, 0f, 0f);

            vrUiInteracter.pressPosition = vrUiInteracter.uiPointerPos;
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
                __instance._target.position = eventData.worldPosition;
                Vector3 newPos = __instance._target.localPosition;
                if (__instance.name != "CharacteristicsPanel")
                {
                    //newPos.x -= ((RectTransform)__instance._target.transform).sizeDelta.x / 2;
                    newPos.y -= ((RectTransform)__instance._target.transform).sizeDelta.y / 2;
                }
                else { 
                    newPos.x -= ((RectTransform)__instance._target.transform).sizeDelta.x / 2;
                    //newPos.x -= ((RectTransform)__instance._target.transform).sizeDelta.y / 2;
                }
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
                __instance.verticalScrollbar.transform.localScale = new Vector3(1.5f, __instance.verticalScrollbar.transform.localScale.y, __instance.verticalScrollbar.transform.localScale.z);

        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(InfoWindow), "Show")]
        private static bool PositionItemDisplayWindow(InfoWindow __instance, ItemContextAbstractClass itemContext, IInventoryController itemController, TraderClass trader, Action onSelectedAction, ItemUiContext itemUiContext, Action onClosedAction, global::ItemInfoInteractionsAbstractClass<EItemInfoButton> contextInteractions)
        {
            __instance.Show(onClosedAction);
            __instance.ShowGameObject();
            __instance.action_1 = onSelectedAction;
            __instance.itemContextAbstractClass = itemContext.CreateChild(itemContext.Item);
            __instance.UI.AddDisposable(__instance.itemContextAbstractClass);
            __instance.item_0 = __instance.itemContextAbstractClass.Item;
            __instance.iInventoryController = itemController;
            __instance.iitemOwner_0 = __instance.item_0.Owner;
            __instance.iitemOwner_0?.RegisterView(__instance);
            __instance.traderClass = trader;
            if (__instance.traderClass != null)
            {
                __instance.UI.AddDisposable(__instance.traderClass.AssortmentChanged.Bind(__instance.method_2));
            }
            __instance.iInventoryController.OnChamberCheck += __instance.method_5;
            __instance.iInventoryController.ExamineEvent += __instance.method_4;
            itemUiContext.InitSpecificationPanel(__instance._itemSpecificationPanel, __instance.itemContextAbstractClass, contextInteractions);
            __instance.method_6();
            System.Random rnd = new System.Random();
            __instance.transform.position = new Vector3(0f, -999.9333f, 1f);
            __instance.transform.localPosition = new Vector3(rnd.Next(-200, 201), rnd.Next(-200, 201), 0f);
            __instance.itemContextAbstractClass.OnCloseWindow += __instance.Close;
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
                vrUiInteracter.EndDrop();
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

        // 0 1.7 -0.9


        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseDropDownBox), "ToggleMenu")]
        private static bool PositionHeadVoiceDropdownMenus(BaseDropDownBox __instance)
        {
            DropDownBox dropDown = (DropDownBox) __instance;
            if (dropDown) { 
                if (dropDown.name == "VoiceSelectorDropDown" || dropDown.name == "FaceSelectorDropdown") {
                    dropDown.rectTransform_1 = dropDown.GetComponent<RectTransform>();
                }
            }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerProfilePreview), "ChangeCameraPosition")]
        private static void PositionLoginPlayerModelPreview(PlayerProfilePreview __instance, ECameraViewType viewType, float duration)
        {
            __instance._camera.stereoTargetEye = StereoTargetEyeMask.None;
            __instance._camera.fieldOfView = 41;

            if (__instance.name == "UsecPanel")
                __instance.PlayerModelView.transform.localPosition = new Vector3(300f, 0, 0);
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerModelView), "method_0")]
        private static void PositionRaidPlayerModelPreview(PlayerModelView __instance, Task __result)
        {
            __result.ContinueWith(task =>
            {
                if (task.IsCompleted)
                {
                    if (__instance.transform.parent.name == "ScavPlayerMV")
                    {
                        __instance.transform.FindChild("Camera_matchmaker").GetComponent<Camera>().fieldOfView = 45;
                        //Transform camBodyViewer = __instance.transform.FindChild("Camera_matchmaker");
                        //camBodyViewer.position = new Vector3(-0.41f, -999.2792f, 4.68f);
                        //Transform scavBody = __instance.PlayerBody.transform;
                        //scavBody.position = new Vector3(0.0818f, -1000.139f, 5.9f);
                    }
                    else if (__instance.transform.parent.name == "PMCPlayerMV")
                    {
                        Transform camera = __instance.transform.FindChild("Camera_matchmaker");
                        camera.localEulerAngles = new Vector3(353, 19, 0);
                        camera.GetComponent<Camera>().fieldOfView = 45;
                        Transform pmcBody = __instance.PlayerBody.transform;
                        pmcBody.localPosition = new Vector3(2, -1, 5);
                        __instance.transform.FindChild("Lights").transform.localEulerAngles = new Vector3(17, 114, 0);
                    }
                    else if (__instance.transform.FindChild("Camera_timehascome0"))
                    {
                        Transform camera = __instance.transform.FindChild("Camera_timehascome0");
                        camera.localPosition = new Vector3(-1.4f, 0.6f, 3.45f);
                        camera.GetComponent<Camera>().fieldOfView = 41;
                    }
                    else if (__instance.transform.root.name == "Session End UI") {
                        Transform camera = __instance.transform.FindChild("Camera_matchmaker");
                        camera.GetComponent<Camera>().fieldOfView = 35;
                    }
                    //else if (__instance.transform.parent.parent.name == "UsecPanel")
                    //{
                    //    Transform camera = __instance.transform.FindChildRecursive("Camera_matchmaker");
                    //    camera.localEulerAngles = new Vector3(353, 356, 0);
                    //    camera.GetComponent<Camera>().fieldOfView = 41;
                    //    Transform pmcBody = __instance.PlayerBody.transform;
                    //    pmcBody.localPosition = new Vector3(0.4f,0,0);
                    //}
                    //else if (__instance.transform.parent.parent.name == "BearPanel")
                    //{
                    //    Transform camera = __instance.transform.FindChildRecursive("Camera_matchmaker");
                    //    camera.GetComponent<Camera>().fieldOfView = 41;
                    //}
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

        }
        // 0 20.9346 0

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TMP_InputField), "OnPointerClick")]
        private static void OpenVRKeyboard(TMP_InputField __instance)
        {
            SteamVR.instance.overlay.ShowKeyboard(
                (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
                (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine,
                (uint)EKeyboardFlags.KeyboardFlag_Modal, "Description", 256, "", 0);

            var keyboardDoneAction =
            SteamVR_Events.SystemAction(EVREventType.VREvent_KeyboardDone, ev => {
                StringBuilder stringBuilder = new StringBuilder(256);
                SteamVR.instance.overlay.GetKeyboardText(stringBuilder, 256);
                string value = stringBuilder.ToString();
                __instance.SetText(value, true);
            });
            keyboardDoneAction.enabled = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AddOfferWindow), "method_11")]
        private static void PositionOfferWindow(AddOfferWindow __instance)
        {
             __instance.transform.localPosition = Vector3.zero;

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TransferItemsScreen), "Show", new Type[] { typeof(TransferItemsScreen.GClass3163) })]
        private static void UndoRotationOnTransferItems(TransferItemsScreen __instance)
        {
            __instance.WaitOneFrame(delegate
            {
                VRGlobals.camRoot.transform.rotation = Quaternion.identity;
            });

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(RagfairFilterWindow), "Show")]
        private static void PositionFleaMarketFilterWindow(RagfairFilterWindow __instance)
        {
            __instance.transform.localPosition = Vector3.zero;
        }

        //Dunno what this is for
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Window<GClass3107>), "Show")]
        private static void PositionSomeWindow(Window<GClass3107> __instance)
        {
            __instance.transform.localPosition = Vector3.zero;
            __instance.transform.GetChild(0).localPosition = Vector3.zero;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WelcomeScreen<EftWelcomeScreen.GClass3171, EEftScreenType>), "Show", new Type[] { typeof(EftWelcomeScreen.GClass3171) })]
        private static void PositionLoginWelcomeScreen(WelcomeScreen<EftWelcomeScreen.GClass3171, EEftScreenType> __instance)
        {
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;

            __instance.transform.parent.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            __instance.transform.parent.localScale = new Vector3(0.002f, 0.002f, 0.002f);
            __instance.transform.parent.localPosition = new Vector3(0.0478f, -999.938f, 1.7484f);
            __instance.transform.parent.gameObject.layer = 5;
            if (VRGlobals.camRoot == null)
            {
                //Plugin.MyLog.LogWarning("\n\n CharacterControllerSpawner Spawn " + __instance.gameObject + "\n");
                VRGlobals.camRoot = new GameObject("camRoot");
                VRGlobals.camHolder = new GameObject("camHolder");
                VRGlobals.vrOffsetter = new GameObject("vrOffsetter");
                VRGlobals.camHolder.transform.parent = VRGlobals.vrOffsetter.transform;
                VRGlobals.vrOffsetter.transform.parent = VRGlobals.camRoot.transform;
                

                VRGlobals.menuVRManager = VRGlobals.camHolder.AddComponent<MenuVRManager>();
                VRGlobals.camRoot.transform.position = new Vector3(0, -999.8f, -0.5f);
                VRGlobals.menuVRManager.RightHand.transform.parent = Camera.main.transform.parent;
                Camera.main.transform.parent.localPosition = Camera.main.transform.localPosition * -1;

                BoxCollider loginCollider = __instance.transform.parent.gameObject.AddComponent<BoxCollider>();
                loginCollider.size = new Vector3(5120, 2880, 1);
            }
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(GClass3150), "ShowAction")]
        private static void PositionInRaidMenu(GClass3150 __instance)
        {
            if (!VRGlobals.inGame)
                return;

            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            Transform mainMenuCam = EnvironmentUI.Instance.environmentUIRoot_0.CameraContainer.FindChild("MainMenuCamera");
            PositionMenuEnvironmentProps();
            PositionMainMenuUi();
            UIPatches.ShowUiScreens();
            VRGlobals.vrPlayer.enabled = false;
            VRGlobals.menuVRManager.enabled = true;
            VRGlobals.menuOpen = true;
            // Move the right hand over so its synced up with the env UI cam
            VRGlobals.vrPlayer.RightHand.transform.parent = mainMenuCam.parent;

            // The FPS cam messes with UI selection so disable it temporarily
            if (Camera.main.name == "FPS Camera") { 
                VRGlobals.VRCam = Camera.main;
                Camera.main.enabled = false;
            }

            Plugin.MyLog.LogWarning("Opening menu in raid");
            
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UserInterfaceClass<EFT.UI.Screens.EEftScreenType>.GClass3132<EFT.UI.MenuScreen.GClass3147, EFT.UI.MenuScreen>), "CloseScreen")]
        private static void CloseOverlayWindows(UserInterfaceClass<EFT.UI.Screens.EEftScreenType>.GClass3132<EFT.UI.MenuScreen.GClass3147, EFT.UI.MenuScreen> __instance)
        {
            UIPatches.HideUiScreens();
            VRGlobals.vrPlayer.enabled = true;
            VRGlobals.menuVRManager.enabled = false;
            VRGlobals.menuOpen = false;
            VRGlobals.vrPlayer.RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
            // Disabling the FPS cam stops it being main so we need to re-enable it another way
            VRGlobals.VRCam.enabled = true;
           
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SettingsScreen), "Close")]
        private static void CloseSettingsInGame(SettingsScreen __instance)
        {
            if (!VRGlobals.inGame || VRGlobals.vrPlayer is HideoutVRPlayerManager)
                return;
            UIPatches.HideUiScreens();
            VRGlobals.vrPlayer.enabled = true;
            VRGlobals.menuVRManager.enabled = false;
            VRGlobals.menuOpen = false;
            VRGlobals.vrPlayer.RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
            // Disabling the FPS cam stops it being main so we need to re-enable it another way
            VRGlobals.VRCam.enabled = true;

        }

        //[HarmonyPostfix]
        //[HarmonyPatch(typeof(ReconnectionScreen), "method_5")]
        //private static void CloseInGameMenuFromDisconnectWindow(ReconnectionScreen __instance)
        //{
        //    if (!VRGlobals.inGame || VRGlobals.vrPlayer is HideoutVRPlayerManager)
        //        return;
        //    UIPatches.HideUiScreens();
        //    VRGlobals.vrPlayer.enabled = true;
        //    VRGlobals.menuVRManager.enabled = false;
        //    VRGlobals.menuOpen = false;
        //    Camera.main.enabled = false;
        //    VRGlobals.vrPlayer.RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
        //    // Disabling the FPS cam stops it being main so we need to re-enable it another way
        //    VRGlobals.VRCam.enabled = true;

        //}


        [HarmonyPostfix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "Start")]
        private static void ActivateWeaponModdingDropDown(ModdingScreenSlotView __instance)
        {
            __instance._dropDownButton.gameObject.SetActive(true); 
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "method_1")]
        private static bool PreventHidingDropDownMethod1(ModdingScreenSlotView __instance)
        {
            if (__instance.bool_0)
            {
                __instance.simpleTooltip_0.Close();
                return false;
            }

            __instance.ginterface386_0.HideModHighlight(overriding: true);
            return false;
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "method_5")]
        private static bool PreventHidingDropDownMethod5(ModdingScreenSlotView __instance, ModdingScreenSlotView slotView)
        {
            __instance.method_6(slotView == __instance);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "CheckVisibility")]
        private static bool PreventHidingDropDownCheckVisibility(ModdingScreenSlotView __instance, EModClass visibleClasses)
        {
            bool flag = (visibleClasses & __instance.EModClass_0) != 0;
            __instance.gameObject.SetActive(flag);
            if (!flag && __instance.dropDownMenu_0.Open)
            {
                if (__instance.dropDownMenu_0.Open)
                    __instance.dropDownMenu_0.Close();
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DropDownMenu), "method_1")]
        private static bool FixWeaponModdingDropDownPosition(DropDownMenu __instance)
        {
            __instance.transform.position = __instance.moddingScreenSlotView_0.MenuAnchor.position;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacteristicsPanel), "Show")]
        private static void FixWeapCharactericsPanel(CharacteristicsPanel __instance)
        {
                __instance.transform.localPosition = new Vector3(-1050, 381, 0);
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacteristicsPanel), "Close")]
        private static void FixWeapCharactericsPanelOnClose(CharacteristicsPanel __instance)
        {
                __instance.transform.localPosition = new Vector3(-950, 381, 0);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacteristicsPanel), "method_3")]
        private static void FixWeapCharactericsPanelOnClick(CharacteristicsPanel __instance, bool expanded)
        {
            if (expanded)
                __instance.transform.localPosition = new Vector3(-1050, 381, 0);
            else
                __instance.transform.localPosition = new Vector3(-950, 381, 0);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ModdingScreenSlotView), "Show")]
        private static void HideModdingLines(ModdingScreenSlotView __instance)
        {
            __instance._boneIcon.gameObject.active = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WeaponModdingScreen), "Show", new Type[] { typeof(GClass3185) })]
        private static void PositionWeaponModdingCamera(WeaponModdingScreen __instance, GClass3185 controller)
        {
            __instance._viewporter.GetComponent<DragTrigger>().enabled = false;
            GameObject weaponPreviewCamContainer = new GameObject("weaponPreviewCamContainer");
            Transform weaponCam = __instance.highLightMesh_0.transform;
            weaponPreviewCamContainer.transform.SetParent(weaponCam.parent);
            weaponCam.parent = weaponPreviewCamContainer.transform;

            weaponPreviewCamContainer.transform.position = (Camera.main.transform.localPosition * -1) + new Vector3(0, 0.1f, -1.6f);
            weaponCam.gameObject.GetComponent<CC_BrightnessContrastGamma>().enabled = false;
            Camera cam = weaponCam.gameObject.GetComponent<Camera>();
            weaponCam.gameObject.AddComponent<SteamVR_TrackedObject>();
            cam.depth = 11;
            cam.stereoTargetEye = StereoTargetEyeMask.Both;
            __instance._weaponPreview.Rotator.localScale = Vector3.one * 2;

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EditBuildScreen), "Show", new Type[] { typeof(EditBuildScreen.GClass3151) })]
        private static void PositionWeaponModdingCamera(EditBuildScreen __instance, EditBuildScreen.GClass3151 controller)
        {
            __instance._viewporter.GetComponent<DragTrigger>().enabled = false;
            GameObject weaponPreviewCamContainer = new GameObject("weaponPreviewCamContainer");
            Transform weaponCam = __instance.highLightMesh_0.transform;
            weaponPreviewCamContainer.transform.SetParent(weaponCam.parent);
            weaponCam.parent = weaponPreviewCamContainer.transform;

            weaponPreviewCamContainer.transform.position = (Camera.main.transform.localPosition * -1) + new Vector3(0, 0.1f, -1.6f);
            weaponCam.gameObject.GetComponent<CC_BrightnessContrastGamma>().enabled = false;
            Camera cam = weaponCam.gameObject.GetComponent<Camera>();
            weaponCam.gameObject.AddComponent<SteamVR_TrackedObject>();
            cam.depth = 11;
            cam.stereoTargetEye = StereoTargetEyeMask.Both;
            __instance._weaponPreview.Rotator.localScale = Vector3.one * 2;

        }




        [HarmonyPrefix]
        [HarmonyPatch(typeof(Class1386), "method_1")]
        private static bool SetUiOnExtractOrDeath(Class1386 __instance)
        {
            if (!__instance.baseLocalGame_0.PlayerOwner.player_0.IsYourPlayer)
                return true;

            GameObject deathPositioner = new GameObject("DeathPos");
            deathPositioner.transform.position = VRGlobals.emptyHands.position;
            deathPositioner.transform.rotation = VRGlobals.emptyHands.rotation;
            VRGlobals.emptyHands = deathPositioner.transform;

            UIPatches.gameUi.transform.parent = null;
            UIPatches.HandleCloseInventory();
            if (UIPatches.notifierUi != null)
                UIPatches.notifierUi.transform.parent = PreloaderUI.Instance._alphaVersionLabel.transform.parent;

            if (UIPatches.extractionTimerUi != null)
                UIPatches.extractionTimerUi.transform.parent = UIPatches.gameUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.healthPanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.stancePanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.battleScreenUi != null)
                UIPatches.battleScreenUi.transform.parent = VRGlobals.commonUi.GetChild(0);


            PreloaderUI.DontDestroyOnLoad(UIPatches.gameUi);
            PreloaderUI.DontDestroyOnLoad(Camera.main.gameObject);
            VRGlobals.inGame = false;
            VRGlobals.menuOpen = true;
            PositionMainMenuUi();
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.BaseLocalGame<EftGamePlayerOwner>.Class1386), "method_1")]
        private static bool SetUiOnExtractOrDeath(EFT.BaseLocalGame<EftGamePlayerOwner>.Class1386 __instance)
        {
            if (!__instance.baseLocalGame_0.PlayerOwner.player_0.IsYourPlayer)
                return true;

            GameObject deathPositioner = new GameObject("DeathPos");
            deathPositioner.transform.position = VRGlobals.emptyHands.position;
            deathPositioner.transform.rotation = VRGlobals.emptyHands.rotation;
            VRGlobals.emptyHands = deathPositioner.transform;

            UIPatches.gameUi.transform.parent = null;
            UIPatches.HandleCloseInventory();
            if (UIPatches.notifierUi != null)
                UIPatches.notifierUi.transform.parent = PreloaderUI.Instance._alphaVersionLabel.transform.parent;

            if (UIPatches.extractionTimerUi != null)
                UIPatches.extractionTimerUi.transform.parent = UIPatches.gameUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.healthPanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.stancePanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.battleScreenUi != null)
                UIPatches.battleScreenUi.transform.parent = VRGlobals.commonUi.GetChild(0);


            PreloaderUI.DontDestroyOnLoad(UIPatches.gameUi);
            PreloaderUI.DontDestroyOnLoad(Camera.main.gameObject);
            VRGlobals.inGame = false;
            VRGlobals.menuOpen = true;
            PositionMainMenuUi();
            return true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.BaseLocalGame<EftGamePlayerOwner>.Class1386), "method_0")]
        private static bool SetUiOnExtractOrDeawth(EFT.BaseLocalGame<EftGamePlayerOwner>.Class1386 __instance)
        {


            if (!__instance.baseLocalGame_0.PlayerOwner.player_0.IsYourPlayer)
                return true;

            GameObject deathPositioner = new GameObject("DeathPos");
            deathPositioner.transform.position = VRGlobals.emptyHands.position;
            deathPositioner.transform.rotation = VRGlobals.emptyHands.rotation;
            VRGlobals.emptyHands = deathPositioner.transform;

            UIPatches.gameUi.transform.parent = null;
            UIPatches.HandleCloseInventory();
            
            if (UIPatches.notifierUi != null)
                UIPatches.notifierUi.transform.parent = PreloaderUI.Instance._alphaVersionLabel.transform.parent;

            if (UIPatches.extractionTimerUi != null)
                UIPatches.extractionTimerUi.transform.parent = UIPatches.gameUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.healthPanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.healthPanel != null)
                UIPatches.stancePanel.transform.parent = UIPatches.battleScreenUi.transform;
            if (UIPatches.battleScreenUi != null)
                UIPatches.battleScreenUi.transform.parent = VRGlobals.commonUi.GetChild(0);


            PreloaderUI.DontDestroyOnLoad(UIPatches.gameUi);
            PreloaderUI.DontDestroyOnLoad(Camera.main.gameObject);
            VRGlobals.inGame = false;
            VRGlobals.menuOpen = true;

            PositionMainMenuUi();
            return true;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(SessionEndUI), "Awake")]
        private static void SetSessionEndUI(SessionEndUI __instance)
        {
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            BoxCollider colllider = __instance.gameObject.AddComponent<BoxCollider>();
            colllider.extents = new Vector3(2560, 1440, 0.5f);
            __instance.transform.eulerAngles = Vector3.zero;
            Canvas canvas = __instance.gameObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            __instance.transform.localScale = new Vector3(0.0011f, 0.0011f, 0.0011f);
            __instance.transform.position = new Vector3(0f, -999.9333f, 1);
            FixMainMenuCamera();
            VRGlobals.inGame = false;
            VRGlobals.menuOpen = true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(RagfairOfferItemView), "Show")]
        private static void ResetOfferImageRotation(RagfairOfferItemView __instance)
        {
            __instance.MainImage.transform.localRotation = Quaternion.Euler(0, 0, __instance.MainImage.transform.localEulerAngles.z);
        }

        // Icons get rotated when loading mags and other stuff
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GridItemView), "OnRefreshItem")]
        private static void ResetRotationOnInvIcons(ItemView __instance)
        {
            __instance.MainImage.transform.localRotation = Quaternion.Euler(0,0, __instance.MainImage.transform.localEulerAngles.z);
        }


        [HarmonyPrefix]
        [HarmonyPatch(typeof(ScreenPositionAnchor), "Update")]
        private static bool FixMenuPropsPositioning(ScreenPositionAnchor __instance)
        {
            if (!(__instance._camera == null) && (Screen.width != __instance.int_1 || Screen.height != __instance.int_0))
            {
                __instance.int_1 = Screen.width;
                __instance.int_0 = Screen.height;
                __instance._camera.gameObject.SetActive(value: true);
                __instance._camera.enabled = true;
                Vector2 vector = new Vector2(Screen.width, Screen.height);
                Vector2 vector2 = __instance._type switch
                {
                    EAnchorType.RelativeByHeight => __instance._position * vector.y,
                    EAnchorType.RelativeByWidth => __instance._position * vector.x,
                    EAnchorType.Absolute => __instance._position,
                    _ => throw new ArgumentOutOfRangeException(),
                };
                switch (__instance._alignment)
                {
                    case TextAnchor.UpperCenter:
                    case TextAnchor.MiddleCenter:
                    case TextAnchor.LowerCenter:
                        vector2.x += vector.x / 2f;
                        break;
                    case TextAnchor.UpperRight:
                    case TextAnchor.MiddleRight:
                    case TextAnchor.LowerRight:
                        vector2.x += vector.x;
                        break;
                }
                switch (__instance._alignment)
                {
                    case TextAnchor.MiddleLeft:
                    case TextAnchor.MiddleCenter:
                    case TextAnchor.MiddleRight:
                        vector2.y += vector.y / 2f;
                        break;
                    case TextAnchor.UpperLeft:
                    case TextAnchor.UpperCenter:
                    case TextAnchor.UpperRight:
                        vector2.y += vector.y;
                        break;
                }

                __instance._camera.gameObject.SetActive(value: false);
            }
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SettingsScreen), "Show", new Type[] { })]
        private static void SetVRSettings(SettingsScreen __instance)
        {
            if (VRSettings.vrSettingsObject == null) { 
                VRSettings.initVrSettings(__instance);
            }
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SettingsScreen), "method_8")]
        private static void CloseVRSettings(SettingsScreen __instance)
        {
            VRSettings.CloseVRSettings();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SettingsScreen), "method_12")]
        private static void SaveVRSettings(SettingsScreen __instance)
        {
            VRSettings.SaveSettings();
            Camera.main.farClipPlane = 1000f;

            if (VRGlobals.weaponHolder && VRGlobals.firearmController) {
                Vector3 weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset(VRGlobals.firearmController.weaponPrefab_0.name, VRGlobals.firearmController.Weapon.WeapClass);
                float weaponAngleOffset = VRSettings.GetWeaponAngleOffset();
                if (weaponAngleOffset < 50)
                {
                    // if the angle is less than 50, get how much less than 50 it is, divide by 100 to get a percent, then multiply our offset by it
                    float rotOffsetMultiplier = (50 - weaponAngleOffset) / 100;
                    weaponOffset += new Vector3(0.08f, 0, -0.01f) * rotOffsetMultiplier;
                }
                else if (weaponAngleOffset > 50)
                {
                    // if the angle is less than 50, get how much less than 50 it is, divide by 100 to get a percent, then multiply our offset by it
                    float rotOffsetMultiplier = (weaponAngleOffset - 50) / 100;
                    weaponOffset += new Vector3(-0.01f, -0.01f, +0.04f) * rotOffsetMultiplier;
                }
                VRGlobals.weaponHolder.transform.localPosition = weaponOffset;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ToggleGroup), "NotifyToggleOn")]
        private static void SaveVRSettings(ToggleGroup __instance, UnityEngine.UI.Toggle toggle, bool sendCallback = true)
        {
            if (toggle.name == "vrSettingsToggle")
                VRSettings.ShowVRSettings();

            Camera.main.useOcclusionCulling = false;
            Camera.main.farClipPlane = 1000f;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(RaidSettingsWindow), "Show")]
        private static void SaveVRSettings(RaidSettingsWindow __instance)
        {
            __instance.transform.localPosition = new Vector3(0, 520, 0);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TasksScreen), "Show")]
        private static void HideDefaultTaskDesc(TasksScreen __instance)
        {
            __instance._notesTaskDescription.active = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(OpenBuildWindow), "Show")]
        private static void ShowBuildWindowWeaponSelector(OpenBuildWindow __instance)
        {
            __instance.transform.localPosition = new UnityEngine.Vector3(638, -167, 0);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EditBuildNameWindow), "Show")]
        private static void ShowBuildWindowSave(EditBuildNameWindow __instance)
        {
            __instance.WindowTransform.localPosition = Vector3.zero;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(OverallScreen), "Show")]
        private static void FixOverallScreenPlayerSize(OverallScreen __instance)
        {
            __instance.WaitOneFrame(delegate { 
                __instance.PlayerModelWithStatsWindow._playerModelView.transform.FindChild("Camera_inventory").GetComponent<Camera>().fieldOfView = 35;
            });
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AchievementsScreen), "Show")]
        private static void FixAchievementsScreenPlayerSize(AchievementsScreen __instance)
        {
            __instance.WaitOneFrame(delegate {
                __instance.PlayerModelWithStatsWindow._playerModelView.transform.FindChild("Camera_inventory").GetComponent<Camera>().fieldOfView = 35;
            });
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InsuranceWindow), "Show")]
        private static void FixInsuranceWindowPosition(InsuranceWindow __instance)
        {
            __instance.transform.localPosition = Vector3.zero;
        }
    }

}


// In game menu CommonUI pos -0.0795 -999.9302 0.4915 rot 0,0,0, same for preloaddergggg


