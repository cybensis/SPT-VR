using EFT.InputSystem;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.VR
{
    [HarmonyPatch]
    internal class VRControlsPatches
    {

        private static readonly float JUMP_OR_STAND_CLAMP_RANGE = 0.75f;
        private static bool snapTurned = false;
        //------------------------------------------------------------------------------------------------------------------------------------------------------------


        [HarmonyPrefix]
        [HarmonyPatch(typeof(GClass2132), "UpdateInput")]
        private static bool MasterVRControls(GClass2132 __instance, ref List<ECommand> commands, ref float[] axis, ref float deltaTime)
        {

            bool isAiming = false;
            bool interactMenuOpen = (VRGlobals.vrPlayer && VRGlobals.vrPlayer.interactionUi && VRGlobals.vrPlayer.interactionUi.gameObject.active);
            if (VRGlobals.firearmController)
                isAiming = VRGlobals.firearmController.IsAiming;


            if (__instance.ginterface202_0 != null)
            {
                for (int i = 0; i < __instance.ginterface202_0.Length; i++)
                {
                    __instance.ginterface202_0[i].Update();
                }
            }

            if (__instance.ginterface202_1 != null)
            {
                for (int j = 0; j < __instance.ginterface202_1.Length; j++)
                {
                    __instance.ginterface202_1[j].Update();
                }
            }
            if (__instance.gclass2127_0 != null)
            {
                if (commands.Count > 0)
                {
                    commands.Clear();
                }

                VRInputManager.UpdateCommands(ref commands);
            }

            for (int l = 0; l < axis.Length; l++)
            {
                axis[l] = 0f;
            }

            if (__instance.gclass2128_1 == null)
            {
                return false;
            }
            if (VRGlobals.inGame && !VRGlobals.menuOpen )
            {
                for (int m = 0; m < __instance.gclass2128_1.Length; m++)
                {
                    if (Mathf.Abs(axis[__instance.gclass2128_1[m].IntAxis]) < 0.0001f)
                    {

                        axis[__instance.gclass2128_1[m].IntAxis] = __instance.gclass2128_1[m].GetValue();
                    }
                    if (m == 3)
                        axis[__instance.gclass2128_1[m].IntAxis] = 0;
                    else if (m == 2)
                    {
                        // Set snap turn to false before checking for disabled conditions
                        if (snapTurned && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) < 0.7) 
                                snapTurned = false;

                        bool disableTurn = ((WeaponPatches.currentGunInteractController && WeaponPatches.currentGunInteractController.hightlightingMesh) || Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.y) >= JUMP_OR_STAND_CLAMP_RANGE || VRGlobals.blockRightJoystick) ;
                        if (disableTurn)
                        {
                            axis[__instance.gclass2128_1[m].IntAxis] = 0;
                            continue;
                        }

                        if (VRSettings.GetRotationType() == VRSettings.RotationMode.Smooth)
                            axis[__instance.gclass2128_1[m].IntAxis] = SteamVR_Actions._default.RightJoystick.axis.x * VRSettings.GetRotationSensitivity();
                        else {
                            if (!snapTurned && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) > 0.7) {
                                snapTurned = true;
                                float snapTurnAmount = (float)VRSettings.GetSnapTurnAmount();
                                if (SteamVR_Actions._default.RightJoystick.axis.x < 0)
                                    snapTurnAmount *= -1;
                                axis[__instance.gclass2128_1[m].IntAxis] = snapTurnAmount;
                            }
                        }


                        if (VRGlobals.camRoot != null)
                            VRGlobals.camRoot.transform.Rotate(0, axis[__instance.gclass2128_1[m].IntAxis], 0);
                    }
                    // Controls movement
                    else if (!VRGlobals.blockLeftJoystick) { 
                        if (m == 0 && Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x) > VRSettings.GetLeftStickSensitivity())
                            axis[__instance.gclass2128_1[m].IntAxis] = SteamVR_Actions._default.LeftJoystick.axis.x;
                        else if (m == 1 && Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y) > VRSettings.GetLeftStickSensitivity())
                            axis[__instance.gclass2128_1[m].IntAxis] = SteamVR_Actions._default.LeftJoystick.axis.y;
                    }



                }

            }


            return false;


        }

    }
}