using HarmonyLib;
using UnityEngine;
using RootMotion.FinalIK;
using TarkovVR.Input;
using Valve.VR;
using Aki.Reflection.Patching;
using EFT;
using System.Reflection;

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

        public static GameObject leftHandIK;
        public static GameObject rightHandIK;
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
            }
            else if (__instance.name == RIGHT_ARM_OBJECT_NAME)
            {
                __instance.enabled = true;
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
                weaponHolder.transform.GetChild(0).parent = oldWeaponHolder.transform;
                oldWeaponHolder = null;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void MoveWeaponToIKHands(EFT.Player.FirearmController __instance)
        {

            // print root
         
            if (!weaponHolder) {
                weaponHolder = new GameObject("WeaponHolder");
               // weaponHolder.transform.parent = rightHandIK.transform;
                weaponHolder.transform.parent = CameraManager.RightHand.transform;

            }

            if (oldWeaponHolder && weaponHolder.transform.GetChild(0)) {
                weaponHolder.transform.GetChild(0).parent = oldWeaponHolder.transform;
                oldWeaponHolder = null;
            }

            if (rightHandIK) {

                // Weapon_root goes in weapon holder, holder goes in VR right hand, set rot of holder to 15,270,90
                // pos to 0.141 0.0204 -0.1003

                // Pistols rot: 7.8225 278.6073 88.9711  pos: 0.3398 -0.0976 -0.1754
                Positioner positioner =  __instance.HandsHierarchy.Transforms[8].gameObject.AddComponent<Positioner>();
                //Positioner positioner = rightHandIK.AddComponent<Positioner>();
                positioner.target = rightHandIK.transform;
                //positioner.target = __instance.HandsHierarchy.Transforms[8];
                oldWeaponHolder = __instance.WeaponRoot.parent.gameObject;
                __instance.WeaponRoot.transform.parent = weaponHolder.transform;
                __instance.WeaponRoot.localPosition = Vector3.zero;
                weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
                weaponHolder.transform.localPosition = new Vector3(0.141f, 0.0204f, -0.1003f);
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



        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void SetOpticCamFoV(EFT.CameraControl.OpticComponentUpdater __instance)
        {

            __instance.camera_0.fieldOfView = 7;

        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(Player), "SetCompensationScale")]
        private static void SetBodyIKScale(Player __instance)
        {
            __instance.RibcageScaleCurrentTarget = 1f;
            __instance.RibcageScaleCurrent = 1f;
        }

        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(TwistRelax), "Relax")]
        //private static bool PreventArmIKStretching(EFT.CameraControl.OpticComponentUpdater __instance)
        //{

        //    return false;

        //}
    }
    
}
