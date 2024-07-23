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

        private static bool isScrolling;
        private static int lookSensitivity = 4;
        //private static bool isAiming = false;
        private static bool isHoldingBreath = false;
        private static bool isSprinting = false;
        private static bool isShooting = false;
        private static readonly float JUMP_OR_STAND_CLAMP_RANGE = 0.75f;
        private static ECommand[] quickSlotCommands = { ECommand.SelectFastSlot4, ECommand.SelectFastSlot5, ECommand.SelectFastSlot6, ECommand.SelectFastSlot7, ECommand.SelectFastSlot8, ECommand.SelectFastSlot9, ECommand.SelectFastSlot0 };
        //------------------------------------------------------------------------------------------------------------------------------------------------------------


        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(GClass1765), "UpdateBindings")]
        //private static bool BlockLookAwxis(GClass1765 __instance, KeyGroup[] keyGroups, AxisGroup[] axisGroups, float doubleClickTimeout)
        //{
        //    for (int i = 0; i < keyGroups.Length; i++) {
        //        Plugin.MyLog.LogWarning(i + ": " + keyGroups[i].keyName + "\n");

        //    }
        //    return true;
        //}

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GClass1914), "UpdateInput")]
        private static bool MasterVRControls(GClass1914 __instance, ref List<ECommand> commands, ref float[] axis, ref float deltaTime)
        {
            // 14: Shoot/F
            // 28: Open Inv/Tab
            // 13: Mousewheel ??
            // 2 & 62: Left Click
            // 3 & 63: Right Click
            // 6:  Middle mouse
            // 29: Jump/Space
            // 22: Crouch/C

            // 0:  Lean Right/E
            // 1:  Lean Left/Q
            // 24: Forward/W
            // 25: Backwards/S
            // 64: Right/D
            // 65: Left/A



            bool isAiming = false;
            bool interactMenuOpen = (VRGlobals.vrPlayer && VRGlobals.vrPlayer.interactionUi && VRGlobals.vrPlayer.interactionUi.gameObject.active);
            if (VRGlobals.firearmController)
                isAiming = VRGlobals.firearmController.IsAiming;


            if (__instance.ginterface173_0 != null)
            {
                for (int i = 0; i < __instance.ginterface173_0.Length; i++)
                {
                    __instance.ginterface173_0[i].Update();
                    //if (__instance.ginterface141_0 [i].GetValue() != 0)
                    //Plugin.MyLog.LogWarning(i + ": " + __instance.ginterface141_0 [i].GetValue() + "\n");

                }
            }

            // ginterface141_1 Has two elements, scroll up and down
            if (__instance.ginterface173_1 != null)
            {
                for (int j = 0; j < __instance.ginterface173_1.Length; j++)
                {
                    __instance.ginterface173_1[j].Update();
                    //if (__instance.ginterface141_1[j].GetValue() != 0)
                    //Plugin.MyLog.LogError(j + ": " + __instance.ginterface141_1[j].GetValue() + "\n");
                }
            }
            if (__instance.gclass1909_0 != null)
            {
                if (commands.Count > 0)
                {
                    commands.Clear();
                }

                VRInputManager.UpdateCommands(ref commands);

                //for (int k = 0; k < __instance.gclass1760_0.Length; k++)
                //{
                //    __instance.ecommand_0 = __instance.gclass1760_0[k].UpdateCommand(deltaTime);
                //    if (VRGlobals.inGame && !VRGlobals.menuOpen)
                //    {
                        

                //    }
                //    if (k == ((int)ECommand.Escape) && SteamVR_Actions._default.ButtonB.GetStateDown(SteamVR_Input_Sources.Any) && !WeaponPatches.returnAfterGrenade) { 
                //        if (VRGlobals.usingItem)
                //            __instance.ecommand_0 = EFT.InputSystem.ECommand.ToggleShooting;
                //        else
                //            __instance.ecommand_0 = EFT.InputSystem.ECommand.Escape;
                //    }
                //    if (__instance.ecommand_0 != 0)
                //    {
                //        commands.Add(__instance.ecommand_0);
                //        //Plugin.MyLog.LogError(k + ": " + (__instance.gclass1760_0[k] as GClass1802).GameKey + "\n");
                //    }
                //    //if (__instance.gclass1760_0[k].GetInputCount() != 0)
                //    //    Plugin.MyLog.LogWarning(i + ": " + __instance.ginterface141_0 [i].GetValue() + "\n");
                //}
            }

            for (int l = 0; l < axis.Length; l++)
            {
                axis[l] = 0f;
            }

            if (__instance.gclass1910_1 == null)
            {
                return false;
            }
            if (VRGlobals.inGame && !VRGlobals.menuOpen )
            {
                for (int m = 0; m < __instance.gclass1910_1.Length; m++)
                {
                    if (Mathf.Abs(axis[__instance.gclass1910_1[m].IntAxis]) < 0.0001f)
                    {

                        axis[__instance.gclass1910_1[m].IntAxis] = __instance.gclass1910_1[m].GetValue();
                    }
                    if (m == 3)
                        axis[__instance.gclass1910_1[m].IntAxis] = 0;
                    else if (m == 2)
                    {
                        if (Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.y) < JUMP_OR_STAND_CLAMP_RANGE && !VRGlobals.blockRightJoystick && Mathf.Abs(SteamVR_Actions._default.RightJoystick.axis.x) > VRSettings.GetRightStickSensitivity())
                            axis[__instance.gclass1910_1[m].IntAxis] = SteamVR_Actions._default.RightJoystick.axis.x * VRSettings.GetRotationSensitivity();
                        else
                            axis[__instance.gclass1910_1[m].IntAxis] = 0;
                        if (VRGlobals.camRoot != null)
                            VRGlobals.camRoot.transform.Rotate(0, axis[__instance.gclass1910_1[m].IntAxis], 0);
                    }
                    else if (m == 0 && Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x) > VRSettings.GetLeftStickSensitivity())
                        axis[__instance.gclass1910_1[m].IntAxis] = SteamVR_Actions._default.LeftJoystick.axis.x;
                    else if (m == 1 && Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y) > VRSettings.GetLeftStickSensitivity())
                        axis[__instance.gclass1910_1[m].IntAxis] = SteamVR_Actions._default.LeftJoystick.axis.y;



                }

            }


            return false;


        }

    }
}