using EFT.UI.Matchmaker;
using EFT.UI.Settings;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TarkovVR.Source.Weapons;
using UnityEngine.UI;
using UnityEngine;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using static TarkovVR.Patches.UI.UIPatchShared;

namespace TarkovVR.Patches.Misc
{
    [HarmonyPatch]
    internal class SettingsPatches
    {
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SettingsScreen), "Show", new Type[] { })]
        private static void SetVRSettings(SettingsScreen __instance)
        {
            if (VRSettings.vrSettingsObject == null)
            {
                VRSettings.initVrSettings(__instance);
            }
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SettingsScreen), "method_8")]
        private static void CloseVRSettings(SettingsScreen __instance)
        {
            VRSettings.CloseVRSettings();
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(SettingsScreen), "method_12")]
        private static void SaveVRSettings(SettingsScreen __instance)
        {
            VRSettings.SaveSettings();
            Camera.main.farClipPlane = 5000f;

            if (VRGlobals.weaponHolder && VRGlobals.firearmController)
            {
                Vector3 weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset(VRGlobals.firearmController.weaponPrefab_0.name, VRGlobals.firearmController.Weapon.WeapClass);
                float weaponAngleOffset = VRSettings.GetRightHandVerticalOffset();
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
                weaponOffset += new Vector3(0.05f, 0, -0.05f);
                VRGlobals.weaponHolder.transform.localPosition = weaponOffset;
                //Plugin.MyLog.LogError($"[OffsetApply] Final weapon offset: {weaponOffset}");
            }

        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ToggleGroup), "NotifyToggleOn")]
        private static void SaveVRSettings(ToggleGroup __instance, UnityEngine.UI.Toggle toggle, bool sendCallback = true)
        {
            if (toggle.name == "vrSettingsToggle")
                VRSettings.ShowVRSettings();

            Camera.main.useOcclusionCulling = false;
            //Camera.main.useOcclusionCulling = true;
            Camera.main.layerCullSpherical = true;
            float[] distances = new float[32];
            for (int i = 0; i < distances.Length; i++)
            {
                distances[i] = 1000f; // Adjust as needed
            }
            Camera.main.layerCullDistances = distances;

            Camera.main.farClipPlane = 5000f;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UserInterfaceClass<EFT.UI.Screens.EEftScreenType>.GClass3572<EFT.UI.MenuScreen.GClass3587, EFT.UI.MenuScreen>), "CloseScreen")]
        private static void CloseOverlayWindows(UserInterfaceClass<EFT.UI.Screens.EEftScreenType>.GClass3572<EFT.UI.MenuScreen.GClass3587, EFT.UI.MenuScreen> __instance)
        {
            UIPatches.HideUiScreens();
            VRGlobals.vrPlayer.enabled = true;
            VRGlobals.menuVRManager.enabled = false;
            VRGlobals.menuOpen = false;
            VRGlobals.vrPlayer.RightHand.transform.parent = VRGlobals.vrOffsetter.transform;
            // Disabling the FPS cam stops it being main so we need to re-enable it another way
            VRGlobals.VRCam.enabled = true;
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------
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

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RaidSettingsWindow), "Show")]
        private static void SaveVRSettings(RaidSettingsWindow __instance)
        {
            __instance.transform.localPosition = new Vector3(0, 520, 0);
        }
    }
}
