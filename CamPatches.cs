using HarmonyLib;
using UnityEngine;
using RootMotion.FinalIK;
using TarkovVR.Input;
using Valve.VR;
using Aki.Reflection.Patching;
using EFT;
using System.Reflection;
using TarkovVR.cam;
using EFT.InputSystem;
using static EFT.ClientPlayer;
using System.Collections.Generic;

namespace TarkovVR
{
    [HarmonyPatch]
    internal class CamPatches
    {
        const string LEFT_ARM_OBJECT_NAME = "Base HumanLCollarbone";
        const string RIGHT_ARM_OBJECT_NAME = "Base HumanRCollarbone";


        public static Camera VRCam;
        public static CameraManager cameraManager;
        public static GameObject camHolder;
        public static GameObject vrOffsetter;
        public static GameObject camParent;

        public static Transform playerCam;

        public static GameObject leftHandIK;
        public static GameObject rightHandIK;

        public static LimbIK leftArmIk;
        public static LimbIK rightArmIk;

        public static GameObject weaponHolder;
        public static GameObject oldWeaponHolder;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterControllerSpawner), "Spawn")]
        private static void AddVR(CharacterControllerSpawner __instance)
        {
            if (VRCam == null) {
                camHolder = new GameObject("camHolder");
                vrOffsetter = new GameObject("vrOffsetter");
                camParent = new GameObject("camParent");
                camHolder.transform.parent = vrOffsetter.transform;
                vrOffsetter.transform.parent = camParent.transform;
                VRCam = camHolder.AddComponent<Camera>();
                VRCam.nearClipPlane = 0.001f;
                cameraManager = camHolder.AddComponent<CameraManager>();
                camParent.AddComponent<TarkovVR.Input.Test>();
                camHolder.AddComponent<SteamVR_TrackedObject>();

                weaponHolder = new GameObject("weaponHolder");
                weaponHolder.transform.parent = CameraManager.RightHand.transform;
            }
        }




        [HarmonyPostfix]
        [HarmonyPatch(typeof(SolverManager), "OnDisable")]
        private static void AddVRHands(LimbIK __instance)
        {
            
            if (__instance.name == LEFT_ARM_OBJECT_NAME)
            {
                __instance.enabled = true;
                __instance.solver.target = CameraManager.LeftHand.transform;
                leftArmIk = __instance;
            }
            else if (__instance.name == RIGHT_ARM_OBJECT_NAME)
            {
                __instance.enabled = true;
                rightArmIk = __instance;
                __instance.solver.target = CameraManager.RightHand.transform;
                if (rightHandIK == null)
                    rightHandIK = __instance.transform.GetChild(0).GetChild(0).GetChild(0).GetChild(0).GetChild(0).gameObject;

            }

        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.EmptyHandsController), "Spawn")]
        private static void ResetWeaponOnEquipHands(EFT.Player.EmptyHandsController __instance)
        {
            Plugin.MyLog.LogWarning("\n\nAAAAAAAAAAA\n");
            if (oldWeaponHolder && weaponHolder.transform.GetChild(0))
            {
                Plugin.MyLog.LogWarning("\n\nBBBBBBBBBB\n");
                weaponHolder.transform.GetChild(0).parent = oldWeaponHolder.transform;
                oldWeaponHolder = null;
                rightArmIk.solver.target = CameraManager.RightHand.transform;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void MoveWeaponToIKHands(EFT.Player.FirearmController __instance)
        {

            // print root
            Plugin.MyLog.LogWarning("CONTROLLER START");
            Plugin.MyLog.LogWarning(weaponHolder);
            Plugin.MyLog.LogWarning(weaponHolder.transform.childCount > 0);
            if (oldWeaponHolder && weaponHolder.transform.childCount > 0) {
                Transform weaponRoot = weaponHolder.transform.GetChild(0);
                Plugin.MyLog.LogWarning("SET TO OLD");
                weaponRoot.parent = oldWeaponHolder.transform;
                weaponRoot.localPosition = Vector3.zero;
                oldWeaponHolder = null;
                rightArmIk.solver.target = CameraManager.RightHand.transform;
            }

            if (rightHandIK) {

                // Weapon_root goes in weapon holder, holder goes in VR right hand, set rot of holder to 15,270,90
                // pos to 0.141 0.0204 -0.1003
                // Pistols rot: 7.8225 278.6073 88.9711  pos: 0.3398 -0.0976 -0.1754
                Transform weaponRightHandIKPositioner = __instance.HandsHierarchy.Transforms[8];
                Positioner positioner = weaponRightHandIKPositioner.gameObject.AddComponent<Positioner>();
                rightArmIk.solver.target = weaponRightHandIKPositioner;
                //Positioner positioner = rightHandIK.AddComponent<Positioner>();
                positioner.target = rightHandIK.transform;

                oldWeaponHolder = __instance.WeaponRoot.parent.gameObject;
                __instance.WeaponRoot.transform.parent = weaponHolder.transform;
                __instance.WeaponRoot.localPosition = Vector3.zero;
                weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                weaponHolder.transform.localPosition = new Vector3(0.141f, 0.0204f, -0.1003f);
                Plugin.MyLog.LogWarning("SET RIGHT HAND");
            }
            Plugin.MyLog.LogWarning(__instance.WeaponRoot.transform.root);

            //if (!weaponHolder)
            //{
            //    weaponOffsetter = new GameObject("WeaponOffsetter");
            //    weaponHolder = new GameObject("WeaponHolder");
            //    weaponOffsetter.transform.parent = weaponHolder.transform;
            //    weaponHolder.AddComponent<Positioner>();
            //}
            //if (rightHandIK)
            //{

            //    // Weapon_root goes in weapon holder, holder goes in VR right hand, set rot of holder to 15,270,90
            //    // pos to 0.141 0.0204 -0.1003

            //    // Pistols rot: 7.8225 278.6073 88.9711  pos: 0.3398 -0.0976 -0.1754

            //    __instance.WeaponRoot.transform.parent = weaponOffsetter.transform;
            //    __instance.WeaponRoot.localPosition = Vector3.zero;
            //    weaponOffsetter.transform.rotation = Quaternion.Euler(15, 275, 90);
            //    weaponOffsetter.transform.localPosition = new Vector3(0.141f, 0.0204f, -0.1003f);
            //}

        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GClass1805), "UpdateInput")]
        private static bool BlockLookAxis(GClass1805 __instance, ref List<ECommand> commands, ref float[] axis, ref float deltaTime)
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


            if (__instance.ginterface143_0 != null)
            {
                for (int i = 0; i < __instance.ginterface143_0.Length; i++)
                {
                    __instance.ginterface143_0[i].Update();
                    //if (__instance.ginterface143_0[i].GetValue() != 0)
                        //Plugin.MyLog.LogWarning(i + ": " + __instance.ginterface143_0[i].GetValue() + "\n");
                    
                }
            }

            // ginterface143_1 Has two elements, scroll up and down
            if (__instance.ginterface143_1 != null)
            {
                for (int j = 0; j < __instance.ginterface143_1.Length; j++)
                {
                    __instance.ginterface143_1[j].Update();
                    //if (__instance.ginterface143_1[j].GetValue() != 0)
                        //Plugin.MyLog.LogError(j + ": " + __instance.ginterface143_1[j].GetValue() + "\n");
                }
            }
            if (__instance.gclass1800_0 != null)
            {
                if (commands.Count > 0)
                {
                    commands.Clear();
                }
                for (int k = 0; k < __instance.gclass1800_0.Length; k++)
                {
                    __instance.ecommand_0 = __instance.gclass1800_0[k].UpdateCommand(deltaTime);
                    if (k == 50) {
                        if (SteamVR_Actions._default.ButtonA.GetStateDown(SteamVR_Input_Sources.Any))
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.BeginInteracting;
                        else if (SteamVR_Actions._default.ButtonA.GetStateUp(SteamVR_Input_Sources.Any))
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.EndInteracting;
                    }
                        //Plugin.MyLog.LogError(k + ": " + __instance.ecommand_0 + "\n");
                    //__instance.ecommand_0 = SteamVR_Actions._default.ButtonA.GetStateDown(SteamVR_Input_Sources.Any);
                    if (__instance.ecommand_0 != 0)
                    {
                        commands.Add(__instance.ecommand_0);
                        Plugin.MyLog.LogError(k + ": " + (__instance.gclass1800_0[k] as GClass1802).GameKey + "\n");
                    }
                    //if (__instance.gclass1800_0[k].GetInputCount() != 0)
                    //    Plugin.MyLog.LogWarning(i + ": " + __instance.ginterface143_0[i].GetValue() + "\n");
                }
            }

            for (int l = 0; l < axis.Length; l++)
            {
                axis[l] = 0f;
            }

            if (__instance.gclass1801_1 == null)
            {
                return false;
            }
            for (int m = 0; m < __instance.gclass1801_1.Length; m++)
            {
                if (Mathf.Abs(axis[__instance.gclass1801_1[m].IntAxis]) < 0.0001f)
                {

                    axis[__instance.gclass1801_1[m].IntAxis] = __instance.gclass1801_1[m].GetValue();
                }
                if (m == 3)
                    axis[__instance.gclass1801_1[m].IntAxis] = 0;
                else if (m == 2)
                    axis[__instance.gclass1801_1[m].IntAxis] = SteamVR_Actions._default.RightJoystick.axis.x * 35;
                else if (m == 0 && Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x) > 0.5f)
                    axis[__instance.gclass1801_1[m].IntAxis] = SteamVR_Actions._default.LeftJoystick.axis.x;
                else if (m == 1 && Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y) > 0.5f)
                    axis[__instance.gclass1801_1[m].IntAxis] = SteamVR_Actions._default.LeftJoystick.axis.y;
            }
            //Plugin.MyLog.LogWarning("\n");
            return false;
            //if (__instance.gclass1801_1 == null)
            //{
            //    return;
            //}
            //for (int m = 0; m < __instance.gclass1801_1.Length; m++)
            //{
            //    if (Mathf.Abs(axis[__instance.gclass1801_1[m].IntAxis]) < 0.0001f)
            //    {
            //        axis[__instance.gclass1801_1[m].IntAxis] = 0;
            //    }
            //}

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void SetOpticCamFoV(EFT.CameraControl.OpticComponentUpdater __instance)
        {

            __instance.camera_0.fieldOfView = 7;

        }




        // Gclass1946 is a class used by the PlayerCameraController to position and rotate the camera, PlayerCameraController holds the abstract class GClass1943 which this inherits
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GClass1946), "ManualLateUpdate")]
        private static bool StopCamXRotation(GClass1946 __instance)
        {
            __instance.transform_1.rotation = Quaternion.Euler(0, __instance.transform_0.eulerAngles.y, __instance.transform_0.eulerAngles.z);
            __instance.transform_1.position = __instance.method_1(__instance.transform_1.position, __instance.transform_1.rotation, __instance.transform_0.position);

            return false;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "SetCompensationScale")]
        private static void SetBodyIKScale(Player __instance)
        {
            // If this isn't set to one, then the hands start to stretch or squish when rotating them around
            __instance.RibcageScaleCurrentTarget = 1f;
            __instance.RibcageScaleCurrent = 1f;
            //playerCam = __instance.CameraPosition;
            //Transform[] spine = __instance._fbbik.references.spine;
            //if (spine[0].GetComponent<BodyRotationFixer>() == false) { 
            //    spine[0].gameObject.AddComponent<BodyRotationFixer>();
            //    spine[1].gameObject.AddComponent<BodyRotationFixer>();
            //    spine[2].gameObject.AddComponent<BodyRotationFixer>();
            //    __instance.CameraContainer.AddComponent<BodyRotationFixer>();

            //}
        }

        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(TwistRelax), "Relax")]
        //private static bool PreventArmIKStretching(EFT.CameraControl.OpticComponentUpdater __instance)
        //{

        //    return false;

        //}
    }
    
}
