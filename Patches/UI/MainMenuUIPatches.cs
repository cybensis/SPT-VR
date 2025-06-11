using EFT;
using EFT.UI;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Patches.Misc;
using UnityEngine;
using static EFT.UI.ScreenPositionAnchor;
using static TarkovVR.Patches.UI.UIPatchShared;

namespace TarkovVR.Patches.UI
{
    [HarmonyPatch]
    internal class MainMenuUIPatches
    {
        private static EnvironmentUI environmentUi;


        //-----------------------------------------------------------------------------------------------------------------
        public static void PositionMainMenuUi()
        {
            PositionCommonUi();
            PositionMenuUi();
            PositionPreloaderUi();
        }

        //-----------------------------------------------------------------------------------------------------------------
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

        //-----------------------------------------------------------------------------------------------------------------
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


        //-----------------------------------------------------------------------------------------------------------------
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
                else if (!VRGlobals.inGame)
                {
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

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnvironmentUI), "RefreshEnvironmentAsync")]
        public static void PositionMenuPropsAfterRefresh(Task __result)
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
                //Plugin.MyLog.LogInfo("Position Environment Props: " + envProp);
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

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EnvironmentUIRoot), "RandomRotate")]
        private static bool PreventMenuOptionSelectCamRotation(EnvironmentUIRoot __instance)
        {
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EnvironmentUIRoot), "method_2")]
        private static bool PreventMenuReturnCamRotatiown(EnvironmentUIRoot __instance)
        {
            return false;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EnvironmentUIRoot), "Init")]
        private static void RepositionEnvironmentAfterChange(EnvironmentUIRoot __instance, Camera alignmentCamera, IReadOnlyCollection<EEventType> events, bool isMain)
        {
            if (environmentUi = __instance.transform.parent.GetComponent<EnvironmentUI>())
            {
                MenuPatches.FixMainMenuCamera();
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(UIInputNode), "CorrectPosition")]
        private static bool PreventMenuOptionSelectCamRotation(UIInputNode __instance)
        {
            __instance.transform.localPosition = Vector3.zeroVector;
            return false;
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
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

    }
}
