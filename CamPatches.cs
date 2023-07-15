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
using UnityEngine.Rendering.PostProcessing;

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
        public static GameObject camRoot;

        public static Transform playerCam;

        public static GameObject leftHandIK;
        public static GameObject rightHandIK;

        public static LimbIK leftArmIk;
        public static LimbIK rightArmIk;

        public static GameObject weaponHolder;
        public static GameObject oldWeaponHolder;


        private static float MIN_JOYSTICK_AXIS_FOR_MOVEMENT = 0.5f;
        private static bool isAiming = false;
        private static bool isSprinting = false;
        private static bool isShooting = false;



        [HarmonyPostfix]
        [HarmonyPatch(typeof(CharacterControllerSpawner), "Spawn")]
        private static void AddVR(CharacterControllerSpawner __instance)
        {
            if (__instance.name != "PlayerSuperior(Clone)")
                return;
            if (VRCam == null) {
                camHolder = new GameObject("camHolder");
                vrOffsetter = new GameObject("vrOffsetter");
                camRoot = new GameObject("camRoot");
                camHolder.transform.parent = vrOffsetter.transform;
                //Camera.main.transform.parent = vrOffsetter.transform;
               //Camera.main.gameObject.AddComponent<SteamVR_TrackedObject>();
                vrOffsetter.transform.parent = camRoot.transform;
                VRCam = camHolder.AddComponent<Camera>();
                VRCam.nearClipPlane = 0.001f;
                //camRoot.AddComponent<TarkovVR.Input.Test>();
                camHolder.AddComponent<SteamVR_TrackedObject>();
                cameraManager = camHolder.AddComponent<CameraManager>();

                weaponHolder = new GameObject("weaponHolder");
                weaponHolder.transform.parent = CameraManager.RightHand.transform;
            }
        }




        [HarmonyPostfix]
        [HarmonyPatch(typeof(SolverManager), "OnDisable")]
        private static void AddVRHands(LimbIK __instance)
        {
            if (__instance.transform.root.name != "PlayerSuperior(Clone)")
                return;
            if (__instance.name == LEFT_ARM_OBJECT_NAME)
            {
                __instance.enabled = true;
                __instance.solver.target = CameraManager.LeftHand.transform;
                leftArmIk = __instance;
                // Set the weight to 2.5 so when rotating the hand, the wrist rotates as well, showing the watch time
                __instance.transform.GetChild(0).GetChild(0).GetChild(0).GetChild(0).GetComponent<TwistRelax>().weight = 2.5f;
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
            if (__instance.GetComponent<BotOwner>() != null)
                return;
            Plugin.MyLog.LogWarning("\n\nAAAAAAAAAAA\n");
            if (oldWeaponHolder && weaponHolder.transform.childCount > 0)
            {
                rightArmIk.solver.target = CameraManager.RightHand.transform;
                Plugin.MyLog.LogWarning("\n\nBBBBBBBBBB\n");
                Transform weaponRoot = weaponHolder.transform.GetChild(0);
                weaponRoot.parent = oldWeaponHolder.transform;
                weaponRoot.localPosition = Vector3.zero;
                oldWeaponHolder = null;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void MoveWeaponToIKHands(EFT.Player.FirearmController __instance)
        {
            if (__instance.GetType() is AIFirearmController)
                return;
            // print root
            if (oldWeaponHolder && weaponHolder.transform.childCount > 0) {
                rightArmIk.solver.target = CameraManager.RightHand.transform;
                Transform weaponRoot = weaponHolder.transform.GetChild(0);
                weaponRoot.parent = oldWeaponHolder.transform;
                weaponRoot.localPosition = Vector3.zero;
                oldWeaponHolder = null;
            }

            if (rightHandIK) {

                // Weapon_root goes in weapon holder, holder goes in VR right hand, set rot of holder to 15,270,90
                // pos to 0.141 0.0204 -0.1003
                // Pistols rot: 7.8225 278.6073 88.9711  pos: 0.3398 -0.0976 -0.1754
                Transform weaponRightHandIKPositioner = __instance.HandsHierarchy.Transforms[8];
                //weaponRightHandIKPositioner.gameObject.active = false;
                //Positioner positioner = weaponRightHandIKPositioner.gameObject.AddComponent<Positioner>();
                rightArmIk.solver.target = weaponRightHandIKPositioner;
                //Positioner positioner = rightHandIK.AddComponent<Positioner>();
                //positioner.target = rightHandIK.transform;

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
                    // 50: Interact
                    if (k == 50)
                    {
                        if (SteamVR_Actions._default.ButtonA.GetStateDown(SteamVR_Input_Sources.Any))
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.BeginInteracting;
                        else if (SteamVR_Actions._default.ButtonA.GetStateUp(SteamVR_Input_Sources.Any))
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.EndInteracting;
                    }
                    // 61: Toggle inv
                    else if (k == 61 && SteamVR_Actions._default.ButtonY.GetStateDown(SteamVR_Input_Sources.Any))
                        __instance.ecommand_0 = EFT.InputSystem.ECommand.ToggleInventory;
                    // 62: Jump
                    else if (k == 62 && SteamVR_Actions._default.RightJoystick.GetAxis(SteamVR_Input_Sources.Any).y > 0.8f)
                        __instance.ecommand_0 = EFT.InputSystem.ECommand.Jump;
                    // 57: Sprint
                    else if (k == 57)
                    {
                        if (SteamVR_Actions._default.ClickLeftJoystick.GetStateDown(SteamVR_Input_Sources.Any)) { 
                            if (!isSprinting)
                                __instance.ecommand_0 = EFT.InputSystem.ECommand.ToggleSprinting;
                            else
                                __instance.ecommand_0 = EFT.InputSystem.ECommand.EndSprinting;

                        }
                        else if (isSprinting && SteamVR_Actions._default.LeftJoystick.GetAxis(SteamVR_Input_Sources.Any).y < MIN_JOYSTICK_AXIS_FOR_MOVEMENT)
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.EndSprinting;
                    }
                    // 52: Reload
                    else if (k == 52 && SteamVR_Actions._default.ButtonX.GetStateDown(SteamVR_Input_Sources.Any))
                        __instance.ecommand_0 = EFT.InputSystem.ECommand.ReloadWeapon;
                    // 39: Aim
                    else if (k == 39)
                    {
                        if (!isAiming && SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f)
                        {
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.ToggleAlternativeShooting;
                            isAiming = true;
                        }
                        else if (isAiming && SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any) < 0.5f)
                        {
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.EndAlternativeShooting;
                            isAiming = false;
                        }
                    }
                    // 38: Shooting
                    else if (k == 38)
                    {
                        if (!isShooting && SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f)
                        {
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.ToggleShooting;
                            isShooting = true;
                        }
                        else if (isShooting && SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) < 0.5f)
                        {
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.EndShooting;
                            isShooting = false;
                        }
                    }
                    // 78: breathing
                    else if (k == 78) { 
                        if (SteamVR_Actions._default.ClickRightJoystick.GetStateDown(SteamVR_Input_Sources.Any))
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.ToggleBreathing;
                        else if (SteamVR_Actions._default.ClickRightJoystick.GetStateUp(SteamVR_Input_Sources.Any))
                            __instance.ecommand_0 = EFT.InputSystem.ECommand.EndBreathing;
                    }
                    
                    // 0: ChangeAimScope
                    // 1: ChangeAimScopeMagnification
                    // 5: CheckAmmo??
                    // 9: NextWalkPose - Uncrouching Upwards
                    // 10: PreviousWalkPose - Uncrouching Down
                    // 34: WatchTimerAndExits
                    // 41: ToggleGoggles
                    // 47: Tactical - Toggle tactical device like flashlights I think
                    // 48: Next - Scroll next, walk louder
                    // 48: Previous - Scroll previous, walk quieter
                    // 51: Throw grenade
                    // 54: Shooting mode - Semi or auto
                    // 55: Check chamber
                    // 56: Prone
                    // 58: Duck - Full crouch
                    // 63: Knife
                    // 64: PrimaryWeaponFirst
                    // 65: PrimaryWeaponSecond
                    // 66: SecondaryWeapon
                    // 67-73: Quick slots
                    // 93-94: Enter
                    // 95: Escape
                    //__instance.ecommand_0 = SteamVR_Actions._default.ButtonA.GetStateDown(SteamVR_Input_Sources.Any);
                    if (__instance.ecommand_0 != 0)
                    {
                        commands.Add(__instance.ecommand_0);
                        //Plugin.MyLog.LogError(k + ": " + (__instance.gclass1800_0[k] as GClass1802).GameKey + "\n");
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
                else if (m == 2) { 
                    axis[__instance.gclass1801_1[m].IntAxis] = SteamVR_Actions._default.RightJoystick.axis.x * 35;
                    if (camRoot != null)
                        camRoot.transform.Rotate(0, axis[__instance.gclass1801_1[m].IntAxis],0);
                }
                else if (m == 0 && Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.x) > MIN_JOYSTICK_AXIS_FOR_MOVEMENT)
                    axis[__instance.gclass1801_1[m].IntAxis] = SteamVR_Actions._default.LeftJoystick.axis.x;
                else if (m == 1 && Mathf.Abs(SteamVR_Actions._default.LeftJoystick.axis.y) > MIN_JOYSTICK_AXIS_FOR_MOVEMENT)
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
            //1x 27 FoV
            //3x 12 FoV
            //4x 6  FoV
            //12x 1.9 FoV
            //16x 1 FoV
            //7x 1.6 FoV

            __instance.camera_0.fieldOfView = 7;

        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(BloodOnScreen), "Start")]
        private static void SetMainCamParent(EffectsController __instance)
        {

            Camera mainCam = __instance.GetComponent<Camera>();
            if (mainCam.name == "FPS Camera") {
                mainCam.transform.parent = vrOffsetter.transform;
                mainCam.gameObject.AddComponent<SteamVR_TrackedObject>();
                mainCam.gameObject.GetComponent<PostProcessLayer>().enabled = false;
                cameraManager.initPos = VRCam.transform.localPosition;
            }

        }




        // Gclass1946 is a class used by the PlayerCameraController to position and rotate the camera, PlayerCameraController holds the abstract class GClass1943 which this inherits
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GClass1946), "ManualLateUpdate")]
        private static bool StopCamXRotation(GClass1946 __instance)
        {
            if (__instance.player_0.IsAI)
                return true;
            //__instance.transform_1.localRotation = Quaternion.Euler(0, __instance.transform_0.eulerAngles.y, __instance.transform_0.eulerAngles.z);
            //__instance.transform_1.localPosition = __instance.method_1(__instance.transform_1.position, __instance.transform_1.rotation, __instance.transform_0.position) + new Vector3(Test.ex, Test.ey, Test.ez);
            if (SteamVR_Actions._default.LeftJoystick.axis.x != 0 || SteamVR_Actions._default.LeftJoystick.axis.y != 0)
            {
                //camRoot.transform.Rotate(0, Camera.main.transform.rotation.y, 0);
                //Camera.main.transform.Rotate(0, Camera.main.transform.rotation.y * -1, 0);
                //__instance.transform_0.parent.localRotation = Quaternion.Euler(0, Camera.main.transform.eulerAngles.y, 0);
            }
                //camRoot.transform.rotation = Quaternion.Euler(0, __instance.transform_0.eulerAngles.y, 0);
                //camRoot.transform.rotation = Quaternion.Euler(0, __instance.transform_0.eulerAngles.y, 0);
                //camRoot.transform.rotation = Quaternion.Euler(0, __instance.transform_0.eulerAngles.y, __instance.transform_0.eulerAngles.z);



            camRoot.transform.position = __instance.method_1(camRoot.transform.position, camRoot.transform.rotation, __instance.transform_0.position) + new Vector3(Test.ex, Test.ey, Test.ez);
            //camHolder.transform.position = __instance.transform_0.position + new Vector3(Test.ex, Test.ey, Test.ez);
            return false;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "SetCompensationScale")]
        private static void SetBodyIKScale(Player __instance)
        {
            if (__instance.IsAI)
                return;
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


        // Found in Player object under CurrentState variable, and is inherited by Gclass1615 
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementState), "Rotate")]
        private static bool SetPlayerRotate(MovementState __instance, ref Vector2 deltaRotation)
        {
            if (__instance.MovementContext.IsAI)
                return true;

            if (SteamVR_Actions._default.LeftJoystick.axis.x != 0 || SteamVR_Actions._default.LeftJoystick.axis.y != 0)
                deltaRotation = new Vector2(deltaRotation.x + Camera.main.transform.eulerAngles.y, 0);

            deltaRotation = new Vector2(deltaRotation.x, 0);
            //camRoot.transform.Rotate(0, __instance.MovementContext.Rotation.x - deltaRotation.x,0);

            __instance.MovementContext.Rotation = deltaRotation;


            return false;
        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "UpdateBonesOnWeaponChange")]
        private static void FixLeftArmBendGoal(Player __instance)
        {
            if (__instance.IsAI)
                return;
            // Change the elbow bend from the weapons left arm goal to the player bodies bend goal, otherwise the left arms bend goal acts like its
            // still attached to the gun even when its not
            __instance._elbowBends[0] = __instance.PlayerBones.BendGoals[0];
        }


        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(TwistRelax), "Relax")]
        //private static bool PreventArmIKStretching(EFT.CameraControl.OpticComponentUpdater __instance)
        //{

        //    return false;

        //}
    }
    

    // CROUCHING:
    // Gclass1603 class, found in Player object under gclass1603 variable float_2 is crouching value between 0f and 1f
    //                   Get and Set IsInPronePose for proning, seems to call on action_6 variable Invoke() method with PoseToInt as arg 0. Just need to change the IsInPronePose variable to set prone
                        
    // CHANGE WEAPON:
    // Player class, method smethod_7 takes in a ItemHandsController type as an argument and seems to swap weapons
}
