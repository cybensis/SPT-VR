using Cinemachine;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using TarkovVR.Patches.UI;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Weapons;
using UnityEngine;
using UnityEngine.UI;
using Valve.VR;
using static EFT.Hideout.SelectItemContextMenu;
using static EFT.UI.InventoryScreen;
using static EFT.UI.MenuTaskBar;
using static GClass3082;

namespace TarkovVR.Patches.Misc
{
    [HarmonyPatch]
    internal class HideoutPatches
    {
        private static Camera hideoutUiCam;
        private static bool hideoutOverlayActive = false;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // When at the shooting range, if you look or point the gun away from down range it lowers the weapon
        // which is annoying so do this to prevent it from happening
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HideoutPlayer), "SetPatrol")]
        private static bool PreventGunBlockInHideout(HideoutPlayer __instance, ref bool patrol)
        {
            if (VRGlobals.oldWeaponHolder != null)
                patrol = false;

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HideoutScreenRear), "Update")]
        private static void PositionHideoutUiCamera(HideoutScreenRear __instance)
        {
            if (hideoutUiCam && VRGlobals.camRoot && hideoutOverlayActive)
            {
                VRGlobals.camRoot.transform.position = hideoutUiCam.transform.position;
                VRGlobals.camRoot.transform.eulerAngles = new Vector3(0, hideoutUiCam.transform.eulerAngles.y, 0);
            }

        }


        // Returns the movement controls back to the player after clicking the back button on the inventory screen in hideout
        [HarmonyPostfix]
        [HarmonyPatch(typeof(InventoryScreen), "method_8")]
        private static void ReturnControlsOnInventoryBackButton(InventoryScreen __instance)
        {
            UIPatches.HandleCloseInventory();

        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(HideoutScreenOverlay), "Show")]
        private static void HandleHideoutOverlay(HideoutScreenOverlay __instance)
        {
            UIPatches.ShowUiScreens();
            if (!hideoutUiCam)
            {
                GameObject camHolder = new GameObject("hideoutUiCam");
                hideoutUiCam = camHolder.AddComponent<Camera>();
                camHolder.AddComponent<CinemachineBrain>();
                hideoutUiCam.enabled = false;
            }
            if (VRGlobals.vrPlayer)
                VRGlobals.vrOffsetter.transform.localPosition = VRGlobals.vrPlayer.initPos * -1 + VRPlayerManager.headOffset;
            else
                VRGlobals.vrOffsetter.transform.localPosition = Camera.main.transform.localPosition * -1 + VRPlayerManager.headOffset;
            VRGlobals.commonUi.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
            VRGlobals.commonUi.parent = VRGlobals.camRoot.transform;
            VRGlobals.commonUi.localPosition = new Vector3(-0.8f, -0.5f, 0.8f);
            VRGlobals.commonUi.localRotation = Quaternion.identity;
            VRGlobals.preloaderUi.transform.parent = VRGlobals.camRoot.transform;
            VRGlobals.preloaderUi.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
            VRGlobals.preloaderUi.GetChild(0).localScale = new Vector3(1.3333f, 1.3333f, 1.3333f);
            VRGlobals.preloaderUi.localPosition = new Vector3(-0.03f, -0.1f, 0.8f);
            VRGlobals.preloaderUi.localRotation = Quaternion.identity;
            PositionMenuUiHideout();
            Camera.main.clearFlags = CameraClearFlags.SolidColor;
            if (VRGlobals.vrPlayer)
                VRGlobals.vrPlayer.enabled = false;
            VRGlobals.menuVRManager.enabled = true;
            VRGlobals.menuOpen = true;
            hideoutOverlayActive = true;
            VRGlobals.inGame = false;
            //__instance.method_5();
            //VRGlobals.menuVRManager.enabled = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HideoutScreenOverlay), "method_5")]
        private static void OnEnterHideout(HideoutScreenOverlay __instance)
        {
            UIPatches.HideUiScreens();
            if (!VRGlobals.vrPlayer)
            {
                VRGlobals.camHolder.AddComponent<SteamVR_TrackedObject>();
                VRGlobals.vrPlayer = VRGlobals.camHolder.AddComponent<HideoutVRPlayerManager>();
                VRGlobals.weaponHolder = new GameObject("weaponHolder");
                VRGlobals.weaponHolder.transform.parent = VRGlobals.vrPlayer.RightHand.transform;
                VRGlobals.vrOpticController = VRGlobals.camHolder.AddComponent<VROpticController>();
                VRGlobals.handsInteractionController = VRGlobals.camHolder.AddComponent<HandsInteractionController>();
                SphereCollider collider = VRGlobals.camHolder.AddComponent<SphereCollider>();
                collider.radius = 0.2f;
                collider.isTrigger = true;
                VRGlobals.camHolder.layer = 7;
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
            }
            VRGlobals.vrPlayer.enabled = true;
            VRGlobals.menuVRManager.enabled = false;
            VRGlobals.menuOpen = false;
            hideoutOverlayActive = false;
            VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
            VRGlobals.ikManager.rightArmIk.solver.target = VRGlobals.vrPlayer.RightHand.transform;
            VRGlobals.inGame = true;
        }

        private static Vector3 camRootRot;
        // Occasionally opening the inventory will set camRoot rot to 0,0,0 so get it here and set later
        [HarmonyPrefix]
        [HarmonyPatch(typeof(InventoryScreen), "Show", new Type[] { typeof(GClass2907) })]
        private static bool SetOriiginalCamRotOnInvOpen(GridViewMagnifier __instance)
        {
            camRootRot = VRGlobals.camRoot.transform.eulerAngles;
            return true;
        }

        // Position inventory in front of player
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GridViewMagnifier), "method_3")]
        private static void PositionInHideoutInventory(GridViewMagnifier __instance)
        {
            VRGlobals.camRoot.transform.eulerAngles = camRootRot;
            if (!VRGlobals.inGame)
                return;
            if (VRGlobals.player && !VRGlobals.menuOpen)
                UIPatches.HandleOpenInventory();
        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HideoutScreenOverlay), "method_7")]
        private static bool ReturnFromHideout(HideoutScreenOverlay __instance)
        {
            __instance.action_0?.Invoke();
            ReturnToMainMenuFromHideout();
            return false;
        }

        // The pointer on exit is fucked up so remove it altogether
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HoverTrigger), "Init")]
        private static void RemoveOnExitTriggerFromLightSelection(HoverTrigger __instance)
        {
            if (__instance.name == "ChangeLightButton") {
                __instance.action_1 = null;
            }
        }


        ////------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MainMenuController), "method_17")]
        private static void ReturnFromHideoutThroughPreloader(MainMenuController __instance)
        {
            ReturnToMainMenuFromHideout();
        }

        private static void ReturnToMainMenuFromHideout()
        {
            VRGlobals.vrOffsetter.transform.localPosition = Vector3.zero;
            VRGlobals.commonUi.parent = null;
            VRGlobals.preloaderUi.parent = null;
            VRGlobals.inGame = false;
            // Player can enter hideout UI and not physically enter it and init the vrPlayer
            if (VRGlobals.vrPlayer)
                VRGlobals.vrPlayer.enabled = false;
            VRGlobals.menuVRManager.enabled = true;
            MenuPatches.PositionMainMenuUi();
            MenuPatches.FixMainMenuCamera();
            VRGlobals.ikManager.leftArmIk.solver.target = null;
            VRGlobals.ikManager.rightArmIk.solver.target = null;
            VRGlobals.menuOpen = false;
            VRGlobals.camRoot.transform.eulerAngles = Vector3.zero;
            Camera.main.farClipPlane = 500f;
        }

        private static void PositionMenuUiHideout()
        {
            VRGlobals.menuUi.transform.parent = VRGlobals.camRoot.transform;
            VRGlobals.menuUi.transform.localPosition = new Vector3(-0.025f, -0.1f, 0.8f);
            VRGlobals.menuUi.transform.localScale = new Vector3(0.0006f, 0.0006f, 0.0006f);
            VRGlobals.menuUi.transform.localRotation = Quaternion.identity;
            VRGlobals.menuUi.GetChild(0).localScale = new Vector3(1.4f, 1.4f, 1.4f);
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Changes to in game when selecting the hideout option in the preloader UI
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MainMenuController), "method_16")]
        private static void SetInGameOnReturnToHideout_Preloader(MainMenuController __instance)
        {
            // Only set inGame if the player is set. If this is the first time going to hideout when starting the game
            // we only want inGame set if everythings been loaded, and if player is set then that means the hideout
            // has already been opened once, and swapping between hideout and the menu is fast now
            if (VRGlobals.player)
            {
                if (VRGlobals.vrPlayer)
                {
                    //VRGlobals.inGame = true;
                    VRGlobals.vrPlayer.enabled = true;
                    VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
                    VRGlobals.ikManager.rightArmIk.solver.target = VRGlobals.vrPlayer.RightHand.transform;

                }
                VRGlobals.menuVRManager.enabled = false;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Changes to in game when selecting the hideout option in the main menu Common UI
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MenuScreen), "method_6")]
        private static void SetInGameOnReturnToHideout_Common(MenuScreen __instance, EMenuType menuType)
        {
            if (menuType == EMenuType.Hideout && VRGlobals.player)
            {
                //VRGlobals.inGame = true;
                if (VRGlobals.vrPlayer)
                {
                    VRGlobals.vrPlayer.enabled = true;
                    VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
                    VRGlobals.ikManager.rightArmIk.solver.target = VRGlobals.vrPlayer.RightHand.transform;

                }
                VRGlobals.menuVRManager.enabled = false;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Changes to in game when selecting the hideout option in the main menu Common UI
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LoadingScreen), "Show")]
        private static void SetInGameOnReturnToHideout_Common(LoadingScreen __instance)
        {
            if (__instance.name == "HideoutLoadingScreen")
            {
                __instance._background.color = new Color(0f, 0.0177f, 0.0313f, 1f);
                Camera.main.backgroundColor = new Color(0, 0, 0, 1);
                Camera.main.farClipPlane = 1f;
            }
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Position the fuel container list on the generator 
        private static Vector3 listPosition = Vector3.zero;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemSelectionCell), "method_2")]
        private static bool GetItemContainerListPosition(ItemSelectionCell __instance)
        {
            listPosition = __instance.transform.position;
            return true;
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SelectItemContextMenu), "method_1")]
        private static bool PositionItemContainerList(SelectItemContextMenu __instance, RectTransform parentPosition, Vector2 offset, EContextPriorDirection direction)
        {
            Transform parent = __instance.RectTransform.parent;

            if (parent != null)
            {
                parent.SetAsLastSibling();
            }
            __instance._container.localPosition = Vector3.zero;
            __instance.transform.position = listPosition;
            __instance.transform.localPosition += new Vector3(50, -25,0);

            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(SelectItemContextMenu), "GetItem")]
        private static void SetItemContainerListPos(SelectItemContextMenu __instance)
        {
            __instance._container.localPosition = Vector3.zero;
        }

        // The UI is closed when the player selects a preloader task bar option twice
        // so close the VR inventory.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Class2462), "method_0")]
        private static void CloseUiOnDoubleClick(Class2462 __instance, bool arg)
        {
            if (VRGlobals.inGame && (
                    (!arg && __instance.menuType != EMenuType.Chat && __instance.menuType != EMenuType.MainMenu ) || 
                    (arg && __instance.menuType != EMenuType.Hideout & __instance.menuType != EMenuType.MainMenu)
                ))
                UIPatches.HandleCloseInventory();
        }
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TemplatedGridsView), "Show")]
        private static void PositionWeaponRackTransferWindow(TemplatedGridsView __instance)
        {
            __instance.SwitchZoneTabsPosition.parent.localPosition = Vector3.zero;
        }
    }
}
