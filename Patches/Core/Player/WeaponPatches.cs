using HarmonyLib;
using System;
using static EFT.Player;
using UnityEngine;
using EFT.CameraControl;
using TarkovVR.Source.Weapons;
using TarkovVR.Source.Player.VRManager;
using System.Collections.Generic;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using System.Linq;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Settings;
using Comfort.Common;
using Valve.VR;
using TarkovVR.Source.Controls;
using static EFT.Player.FirearmController;
using static PhysicsExtensions;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class WeaponPatches
    {
        public static Vector3 weaponOffset = Vector3.zero;
        public static bool grenadeEquipped;
        public static bool pinPulled = false;
        public static Transform previousLeftHandMarker;
        public static GunInteractionController currentGunInteractController;
        public static Transform currentScope;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AutomaticFireOperation), "SwitchToMalfunction")]
        private static void UnlockHand(AutomaticFireOperation __instance)
        {
            if (!__instance.Player.IsYourPlayer)
                return;
            /*
            if (__instance.Weapon_0.MalfState.State != 0)
                VRGlobals.vrPlayer.ForceUnlockHand();
            */
        }

        // This removed the sway/tilt of the gun when turning and walking
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MotionEffector), "Process")]
        private static bool RemoveGunTilt(MotionEffector __instance)
        {
            if (VRSettings.GetWeaponInertiaOn() == true)
                return true;
            else
                return false;
        }
       /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "IsAiming", MethodType.Setter)]
        private static bool BypassADSAnimation(EFT.Player.FirearmController __instance, ref bool value)
        {
            var player = __instance._player;
            if (player == null)
                return true;

            if (!value)
            {
                player.Physical.HoldBreath(false);
            }

            __instance._isAiming = value;

            player.Skills.FastAimTimer.Target = value ? 0f : 2f;
            player.MovementContext.SetAimingSlowdown(__instance.IsAiming, 0.33f + __instance.gclass2250_0.AimMovementSpeed);

            float mass = (!value || player.MovementContext.StationaryWeapon != null) ? 0f : __instance.ErgonomicWeight;
            player.Physical.Aim(mass);

            __instance.weaponManagerClass.SetAiming(value);
            __instance.UpdateSensitivity();
            __instance.AimingChanged(value);

            return false;
        }
       */
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.EmptyHandsController), "Spawn")]
        private static void ResetWeaponOnEquipHands(EFT.Player.EmptyHandsController __instance)
        {         
            if (!__instance._player.IsYourPlayer)
                return;
            VRGlobals.firearmController = null;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            if (Handedness.MirrorLeftHanded)
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            VRGlobals.player = __instance._player;

            //if (!__instance.WeaponRoot.parent.GetComponent<GunInteractionController>())
            //    currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
            // Empty hands must put a VR-held weapon back on its original parent: Escape/cancel gets here without the
            // switch-out event (IEventsConsumerOnWeapOut). No-op if that already cleaned up.
            if (VRGlobals.oldWeaponHolder != null && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                VRGlobals.weaponHolder.transform.GetChild(0).SetParent(VRGlobals.oldWeaponHolder.transform, false);
                VRGlobals.oldWeaponHolder.transform.localRotation = Quaternion.identity;
                VRGlobals.oldWeaponHolder = null;
                // Don't null emptyHands here (ReturnWeaponToOriginalParentOnChange does, when switching to a weapon):
                // HandsPositioner's per-frame pins (emptyHands.rotation, camRoot) only run while it's set, else the hands jitter.
            }

            if (VRGlobals.vrPlayer)
            {
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.player._markers[1] = VRGlobals.vrPlayer.RightHand.transform;
                VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
                VRGlobals.ikManager.rightArmIk.solver.target = VRGlobals.vrPlayer.RightHand.transform;
                VRGlobals.ikManager.rightArmIk.enabled = true;
            }
            if (grenadeEquipped)
                grenadeEquipped = false;

            pinPulled = false;

            if (VRSettings.GetLeftHandedMode())
            {
                VRGlobals.player._elbowBends[0] = VRGlobals.rightArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.leftArmBendGoal;
            }
            else {
                VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.EmptyHandsController), "Drop")]
        private static void ResetIKOnExitHands(EFT.Player.EmptyHandsController __instance)
        {

            if (VRGlobals.vrPlayer)
            {
                VRGlobals.ikManager.rightArmIk.solver.target = null;
                VRGlobals.ikManager.rightArmIk.enabled = false;
            }
        }

        public static PortableRangeFinderController rangeFinder;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UsableItemController), "IEventsConsumerOnWeapIn")]
        private static void SetRangeFinder(UsableItemController __instance)
        {
            if (!__instance._player.IsYourPlayer || !(__instance as PortableRangeFinderController))
                return;
            PortableRangeFinderController instance = (PortableRangeFinderController) __instance;
            rangeFinder = instance;
            instance._rangeFinderController._boneToCastRay.parent.Find("linza").gameObject.SetActive(false);
            VRGlobals.emptyHands = instance.ControllerGameObject.transform;
            if (Handedness.MirrorLeftHanded)
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                VRGlobals.oldWeaponHolder = instance.WeaponRoot.parent.gameObject;
                if (instance.WeaponRoot.parent.Find("RightHandPositioner"))
                {
                    VRGlobals.weaponHolder = instance.WeaponRoot.parent.Find("RightHandPositioner").Find("weaponHolder").gameObject;
                }
                else
                {
                    GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                    rightHandPositioner.transform.SetParent(instance.WeaponRoot.transform.parent, false);
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.SetParent(rightHandPositioner.transform, false);
                    HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                    handsPositioner.rightHandIk = rightHandPositioner.transform;
                }
                //__instance.WeaponRoot.transform.parent.GetComponent<Animator>().updateMode = AnimatorUpdateMode.AnimatePhysics;

                instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                //__instance.WeaponRoot.localPosition = Vector3.zero;
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(37, 267, 55);

                //weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset(__instance.weaponPrefab_0.name, __instance.Weapon.WeapClass);

                VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.23f, 0.0689f, -0.23f);
            }
            else if (instance.WeaponRoot.parent.parent.name == "RightHandPositioner")
            {

                VRGlobals.weaponHolder = instance.WeaponRoot.parent.parent.gameObject;
            }
            if (VRGlobals.player)
            {
                previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
            }
            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);
            VRGlobals.oldWeaponHolder.transform.localEulerAngles = new Vector3(340, 340, 0);



        }


        [HarmonyPostfix]
        [HarmonyPatch(typeof(PortableRangeFinderController), "CG_GetOperationFactoryDelegates1")]
        private static void UnequipRangeFinder(PortableRangeFinderController __instance)
        {
            if (!__instance._player.IsYourPlayer )
                return;

            //Plugin.MyLog.LogWarning("PortableRangeFinderController::IEventsConsumerOnWeapOut  " + VRGlobals.emptyHands + "   |   " + VRGlobals.weaponHolder + "   |   " + VRGlobals.oldWeaponHolder);
            if (VRGlobals.oldWeaponHolder != null && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                VRGlobals.weaponHolder.transform.GetChild(0).SetParent(VRGlobals.oldWeaponHolder.transform, false);
                VRGlobals.oldWeaponHolder.transform.localRotation = Quaternion.identity;
                VRGlobals.oldWeaponHolder = null;
                VRGlobals.emptyHands = null;
            }
            rangeFinder = null;
        }



        private static bool compassEquipped = false;
        //This method will set the compass position to the right hand
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "PropUpdate")]
        private static bool FixCompassToLeftHand(EFT.Player __instance)
        {
            // Set every frame: setting it once in SetCompassState doesn't work
            if (__instance.IsYourPlayer && __instance._propActive && __instance._compassArrow) {
                //__instance._compassArrow.transform.parent.localPosition = new Vector3(0.1851f, -0.4271f, -0.0684f);
                //__instance._compassArrow.transform.parent.localEulerAngles = new Vector3(55, 40, 11);

                __instance._compassArrow.transform.parent.localPosition = new Vector3(-0.09f, -0.04f, 0);
                __instance._compassArrow.transform.parent.localEulerAngles = new Vector3(85, 304, 10);
                __instance._propTransforms[1].localRotation = Quaternion.identity;
                __instance._propTransforms[2].localRotation = Quaternion.identity;
                return false;
            }
            else
                return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), "SetCompassState")]
        private static void SetCompassEquippedFirearm(FirearmController __instance, bool active)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (InitVRPatches.leftPalm && __instance._player._compassArrow) {
                compassEquipped = active;
                __instance._player._compassArrow.transform.parent.SetParent(InitVRPatches.leftPalm, false);
                //__instance.WaitFrames(5, delegate {
                //    __instance._player._compassArrow.transform.parent.localPosition = new Vector3(0.1851f, -0.4271f, -0.0684f);
                //    __instance._player._compassArrow.transform.parent.localEulerAngles = new Vector3(55, 40, 11);
                //});
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GrenadeHandsController), "SetCompassState")]
        private static void SetCompassEquippedGrenade(GrenadeHandsController __instance, bool active)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (InitVRPatches.leftPalm && __instance._player._compassArrow)
            {
                compassEquipped = active;
                __instance._player._compassArrow.transform.parent.SetParent(InitVRPatches.leftPalm, false);
                //__instance.WaitFrames(5, delegate { 
                //    __instance._player._compassArrow.transform.parent.localPosition = new Vector3(0.1851f, -0.4271f, -0.0684f);
                //    __instance._player._compassArrow.transform.parent.localEulerAngles = new Vector3(55, 40, 11);
                //});
            }
        }


        //------------------------------------------------------------------------------------------------------------------------------------------------------------       
        // Tarkov's skip of a draw or put-away (FastForwardCurrentState) finishes the op without the clip's WeapIn / WeapOut events
        // (the draw's plays IDLE and calls WeaponAppeared, the put-away's calls HideWeaponComplete). Every VR skip goes through
        // here to do that event work first: else the old gun stays on its holder, the drawn one's GunInteractionController stays
        // off (not anchored to the hand) and switchingWeapon stays set (FireHandler bails).
        internal static void SkipDrawOrPutAway(EFT.Player.FirearmController firearmController)
        {
            if (firearmController.IsInSpawnOperation())
                FinishMovingWeaponToVrHands(firearmController);
            else if (firearmController.IsInRemoveOperation())
                ReturnWeaponToOriginalParentOnChange(firearmController);
            firearmController.FastForwardCurrentState();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "IEventsConsumerOnWeapOut")]
        private static void ReturnWeaponToOriginalParentOnChange(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            //If menu is open turn gun renderer off
            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }
            // Only when a weapon is equipped and the weaponHolder actually has something there
            if (VRGlobals.oldWeaponHolder != null && VRGlobals.weaponHolder.transform.childCount > 0)
            {
                VRGlobals.weaponHolder.transform.GetChild(0).SetParent(VRGlobals.oldWeaponHolder.transform, false);
                VRGlobals.oldWeaponHolder.transform.localRotation = Quaternion.identity;
                VRGlobals.oldWeaponHolder = null;
                currentGunInteractController.enabled = false;
                VRGlobals.emptyHands = null;
            }
            
            VRGlobals.vrPlayer.isWeapPistol = false;

        }

        // Some guns' switch animation gets stuck (mainly in the inventory), possibly because VR controls own the hands.
        // This forces WeapIn to finish the switch, which also puts the hands in the right position at once.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AnimationEventSystem.AnimationEventsEmitter), "EmitEvents")]
        public static void CheckQueue(AnimationEventSystem.AnimationEventsEmitter __instance)
        {
            if (VRGlobals.switchingWeapon)
            {
                var animator = VRGlobals.player.ArmsAnimatorCommon;
                var state = animator.GetCurrentAnimatorStateInfo(1);

                if (state.normalizedTime == 0f)
                {
                    animator.Update(0.01f);

                    var firearmController = VRGlobals.player.HandsController as FirearmController;
                    if (firearmController != null)
                    {
                        firearmController.IEventsConsumerOnWeapIn();

                        //VRGlobals.switchingWeapon = false;
                    }
                }
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "IEventsConsumerOnWeapIn")]
        private static void FinishMovingWeaponToVrHands(EFT.Player.FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;               
            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }           
            if (grenadeEquipped)
                grenadeEquipped = false;

            pinPulled = false;
            VRGlobals.switchingWeapon = false;           
            if (currentGunInteractController)
            {
                if (!currentGunInteractController.initialized)
                    currentGunInteractController.CreateRaycastReceiver(__instance.GunBaseTransform, __instance.WeaponLn);
                currentGunInteractController.enabled = true;
            }
            
            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);
          
            VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
            VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;

            VRGlobals.vrPlayer.isWeapPistol = (__instance.Weapon.WeapClass == "pistol");           
        }
        

        public static Transform knifeTransform = null;
        
        [HarmonyPostfix]
        [HarmonyPatch(typeof(KnifeController), "Spawn")]
        private static void MoveKnifeToIKHands(KnifeController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            

            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }
            if (grenadeEquipped)
                grenadeEquipped = false;

            pinPulled = false;

            if (currentGunInteractController != null) { 
                currentGunInteractController.enabled = false;
                currentGunInteractController = null;
            }
            
            VRGlobals.player = __instance._player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            if (Handedness.MirrorLeftHanded)
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            knifeTransform = VRGlobals.emptyHands;
            VRGlobals.usingItem = false;
            VRPlayerManager.leftHandGunIK = __instance.HandsHierarchy.Transforms[10];

            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.gameObject;
                GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                rightHandPositioner.transform.SetParent(__instance.WeaponRoot.transform.parent, false);
                VRGlobals.weaponHolder = new GameObject("weaponHolder");
                VRGlobals.weaponHolder.transform.SetParent(rightHandPositioner.transform, false);
                rightHandPositioner.AddComponent<HandsPositioner>();
                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

            }
            else {
                __instance.WeaponRoot.parent.parent.GetComponent<HandsPositioner>().enabled = true;
                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.parent.parent.gameObject;
                VRGlobals.weaponHolder = __instance.WeaponRoot.parent.gameObject;
                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

            }
            if (VRGlobals.player)
            {
                previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
                //VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
            }
            if (VRSettings.GetLeftHandedMode())
            {
                VRGlobals.player._elbowBends[0] = VRGlobals.rightArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.leftArmBendGoal;
            }
            else
            {
                VRGlobals.player._elbowBends[0] = VRGlobals.leftArmBendGoal;
                VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;
            }

            // MELEE WEAPONS

            VRGlobals.weaponHolder.transform.localPosition = new Vector3(0.13f, 0f, -0.43f);
            VRGlobals.weaponHolder.transform.localEulerAngles = new Vector3(33f, 312f, 83f);
            VRGlobals.player._elbowBends[1] = VRGlobals.rightArmBendGoal;

        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(KnifeController), "method_9")]
        private static void UnequipVrKnife(KnifeController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;
            knifeTransform = null;
            if (__instance.WeaponRoot.parent.name == "weaponHolder")
            {
                __instance.WeaponRoot.parent.parent.GetComponent<HandsPositioner>().enabled = false;
            }

            VRGlobals.oldWeaponHolder = null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), "method_49")]
        private static void AddNewMagToInteractionController(FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer)
                return;

            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }

            if (__instance._weaponPrefab)
            {
                if (__instance._weaponPrefab.Renderers != null)
                {
                    for (int i = 0; i < __instance._weaponPrefab.Renderers.Length; i++)
                    {
                        if (__instance._weaponPrefab.Renderers[i].transform.parent.GetComponent<MagazineInHandsVisualController>())
                        {
                            currentGunInteractController.SetMagazine(__instance._weaponPrefab.Renderers[i].transform, false);
                            return;
                        }
                    }

                }
            }
            //Plugin.MyLog.LogWarning(__instance.transform.root+"\n\n\n");
            //Plugin.MyLog.LogWarning(new StackTrace());

        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticRetrice), "UpdateTransform")]
        public static bool UpdateTransform(EFT.CameraControl.OpticRetrice __instance, OpticSight opticSight)
        {
            try
            {

                ScopeReticle reticle = opticSight.ScopeData.Reticle;
                // Reticle can still be null when ScopeZoomHandler.Update calls this (scope just equipped, bundle not loaded,
                // sight mode with no reticle) and vanilla doesn't guard it in UpdateTransform: skip the frame.
                if (reticle == null)
                    return false;
                __instance._renderer.transform.localPosition = reticle.Position;
                __instance._renderer.transform.localEulerAngles = reticle.Rotation;
                __instance._scale = reticle.Scale * 0.1f;
                return false;
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[UpdateTransform] Failed: {exception.Message}\n{exception.StackTrace}");
                return true;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), "method_46")]
        public static void AddNewTacDeviceToInteractionController(FirearmController __instance)
        {
            if (!__instance._player.IsYourPlayer || __instance.Firearms == null)
                return;

            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }

            var weaponManager = __instance.Firearms;

            var sightControllers = weaponManager._sightModVisualControllers;
            if (sightControllers == null)
            {
                VRGlobals.scopes.Clear();
                return;
            }

            VRGlobals.scopes.Clear();

            foreach (var sightController in sightControllers)
            {
                if (sightController._scopePrefabCache == null) continue;

                Transform scopeTransform = null;

                // Find normal EFT Scope
                scopeTransform = sightController.transform.Find("mod_aim_camera");
                if (scopeTransform == null)
                    scopeTransform = sightController.transform.Find("mod_aim_camera_001");

                // Deep search for modded scopes such as Epic's AIO
                if (scopeTransform == null)
                {
                    scopeTransform = TryGetDeepScopeTransform(sightController.transform);
                }

                if (scopeTransform == null) continue;

                // Check if deep scope was found (likely modded scope), if so set that as scopeTransform
                Transform deepScopeChild = TryGetDeepScopeTransform(sightController.transform);
                if (scopeTransform == deepScopeChild && deepScopeChild != null)
                {
                    Transform child0 = GetChildSafe(sightController.transform, 0);
                    if (child0 != null)
                        scopeTransform = child0;
                }

                VRGlobals.scopes.Add(scopeTransform);

                var visualController = sightController.GetComponent<SightModVisualControllers>() ??
                                     sightController.transform.parent?.GetComponent<SightModVisualControllers>();

                if (visualController == null || !VRGlobals.vrOpticController) continue;

                if (visualController.TryGetComponent<BoxCollider>(out var collider))
                {
                    collider.gameObject.layer = 6;
                    collider.size = new Vector3(0.09f, 0.04f, 0.02f);
                    collider.center = new Vector3(-0.04f, 0, -0.075f);
                    collider.isTrigger = true;
                    collider.enabled = true;
                }
            }

            // This goes through all the sights currently on your gun and selects the first optic it finds as the selected sight
            // It ensures that a sight using the camera will always be the active sight
            if (__instance.Firearms?._sightModVisualControllers != null &&
                __instance.Firearms._sightModVisualControllers.Length > 1)
            {
                try
                {
                    int startingAimIndex = __instance.Item?.AimIndex ?? -1;
                    if (startingAimIndex == -1)
                        return;

                    if (__instance._player?.ProceduralWeaponAnimation == null)
                        return;

                    bool foundOptic = false;
                    int sightCount = __instance.Firearms._sightModVisualControllers.Length;

                    for (int i = 0; i < sightCount; i++)
                    {
                        // Directly set the aim index and check if it's an optic
                        __instance.ChangeAimingMode(i);

                        if (__instance._player.ProceduralWeaponAnimation.CurrentScope != null &&
                            __instance._player.ProceduralWeaponAnimation.CurrentScope.IsOptic)
                        {
                            foundOptic = true;
                            break;
                        }
                    }

                    // If no optic found, revert to first sight
                    if (!foundOptic)
                        __instance.ChangeAimingMode(startingAimIndex);
                }
                catch (Exception exception)
                {
                    Plugin.MyLog.LogError($"MoveWeaponToIKHands: Error while changing aiming mode - {exception.Message}");
                    Plugin.MyLog.LogError($"Stack trace: {exception.StackTrace}");
                }
            }

        }

        private static Transform GetChildSafe(Transform parent, int index)
        {
            if (parent == null || index < 0 || index >= parent.childCount)
                return null;
            return parent.GetChild(index);
        }

        // Search for modded scope camera
        private static Transform TryGetDeepScopeTransform(Transform root)
        {
            Transform child0 = GetChildSafe(root, 0);
            if (child0 == null) return null;

            Transform child1 = GetChildSafe(child0, 1);
            if (child1 == null) return null;

            Transform child2 = GetChildSafe(child1, 0);
            if (child2 == null) return null;

            return child2.Find("mod_aim_camera_000");
        }

        // MoveWeaponToIKHands adds HighLightMesh to the camera, so its Awake runs before a material is attached (null on entering
        // a raid): give it a dummy invisible Material
        /*
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HighLightMesh), "Awake")]
        private static void HandleHighLightMeshAwake(HighLightMesh __instance)
        {

            if (__instance.Mat == null && VRGlobals.player && (VRGlobals.vrPlayer is HideoutVRPlayerManager || VRGlobals.vrPlayer is RaidVRPlayerManager))
            {
                var shader = Shader.Find("Hidden/HighLightMesh");
                __instance.Mat = new Material(shader);
                __instance.Mat.SetColor(HighLightMesh.int_2, Color.clear);
                __instance.Mat.SetTexture(HighLightMesh.int_4, __instance.renderTexture_0);
            }
        }
        */
        private static readonly Dictionary<HighLightMesh, System.Action> _highLightHandlers
            = new Dictionary<HighLightMesh, System.Action>();
        private static bool _highLightAllowUpdate = false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HighLightMesh), "Update")]
        private static bool BlockHighLightMeshUpdate() => _highLightAllowUpdate;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HighLightMesh), "Awake")]
        private static void HandleHighLightMeshAwake(HighLightMesh __instance)
        {
            if (__instance.Mat == null && VRGlobals.player && (VRGlobals.vrPlayer is HideoutVRPlayerManager || VRGlobals.vrPlayer is RaidVRPlayerManager))
            {
                var shader = Shader.Find("Hidden/HighLightMesh");
                __instance.Mat = new Material(shader);
                __instance.Mat.SetColor(HighLightMesh._color, Color.clear);
                __instance.Mat.SetTexture(HighLightMesh._maskRTId, __instance._maskRT);
            }

            if (!_highLightHandlers.ContainsKey(__instance))
            {
                HighLightMesh captured = __instance;
                System.Action handler = () =>
                {
                    if (captured != null && captured.enabled && captured.gameObject.activeInHierarchy)
                    {
                        _highLightAllowUpdate = true;
                        try { captured.Update(); }
                        finally { _highLightAllowUpdate = false; }
                    }
                };
                _highLightHandlers[__instance] = handler;
                Application.onBeforeRender += handler.Invoke;
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(HighLightMesh), "OnDestroy")]
        private static void UnregisterHighLightOnBeforeRender(HighLightMesh __instance)
        {
            if (_highLightHandlers.TryGetValue(__instance, out var handler))
            {
                Application.onBeforeRender -= handler.Invoke;
                _highLightHandlers.Remove(__instance);
            }
        }

        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        [HarmonyPrefix]
        private static void CleanupHighlightMesh(EFT.Player.FirearmController __instance)
        {
            // Our own weapon switch only: else a bot drawing a gun takes our weapon-part highlight off the camera
            if (__instance == null || __instance._player == null || !__instance._player.IsYourPlayer)
                return;
            RemoveHighlightFromCamera();
        }

        // The weapon-part highlight's command buffer off the headset camera
        private static void RemoveHighlightFromCamera()
        {
            if (VRGlobals.VRCam == null)
                return;
            HighLightMesh existingHighlight = VRGlobals.VRCam.gameObject.GetComponent<HighLightMesh>();
            if (existingHighlight != null && existingHighlight._cmb != null)
                VRGlobals.VRCam.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterImageEffectsOpaque, existingHighlight._cmb);
        }
        
        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        public static List<Transform> weaponInteractables;
        public static Transform gunCollider;

        // Custom left-grip points for the few guns whose left hand IK marker is in the wrong spot
        private static readonly Dictionary<string, Vector3> LeftHandIKOverrides = new()
        {
            { "weapon_sako_trg_m10_86x70_container", new Vector3(-0.099f, -0.480f, -0.054f) },
            { "weapon_toz_tt_gold_762x25tt_container", new Vector3(-0.058f, -0.416f, -0.187f) },
        };

        // The same for the right-grip marker (weapon_R_hand_marker, HandsHierarchy.Transforms[8]): the Sako's sits 58 cm out to the side
        // in all its clips, which throws off its gun spot on the back / hip, the reach to take it back and the stock length. A sound
        // marker sits on the right palm in the gun's idle pose, so this is the Sako's palm there (sako_idle_armed, Weapon_root frame).
        private static readonly Dictionary<string, Vector3> RightHandMarkerOverrides = new()
        {
            { "weapon_sako_trg_m10_86x70_container", new Vector3(0.047f, -0.150f, -0.103f) },
        };
        private const string RIGHT_MARKER_OVERRIDE = "VR_RightHandMarker_Override";

        // A gun's right-grip marker: its own, or for a gun above a stand-in child of its Weapon_root at the right spot. weaponPrefab is the
        // gun in hands' (_weaponPrefab) or a model on the body's; null = its own marker.
        internal static Transform RightGripMarker(WeaponPrefab weaponPrefab, TransformLinks links, Transform weaponRoot)
        {
            if (weaponPrefab != null && weaponRoot != null && RightHandMarkerOverrides.TryGetValue(weaponPrefab.name.Replace("(Clone)", ""), out Vector3 localPosition))
            {
                Transform stand = weaponRoot.Find(RIGHT_MARKER_OVERRIDE);
                if (stand == null)
                {
                    stand = new GameObject(RIGHT_MARKER_OVERRIDE).transform;
                    stand.SetParent(weaponRoot, false);
                }
                stand.localPosition = localPosition;
                return stand;
            }
            return links != null && links.Transforms != null && links.Transforms.Length > 8 ? links.Transforms[8] : null;
        }

        internal static Transform RightGripMarker(EFT.Player.FirearmController controller)
        {
            return controller != null ? RightGripMarker(controller._weaponPrefab, controller.HandsHierarchy, controller.WeaponRoot) : null;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "InitBallisticCalculator")]
        private static void MoveWeaponToIKHands(EFT.Player.FirearmController __instance)
        {

            if (!__instance._player.IsYourPlayer || __instance._player == null)
                return;
            if (VRGlobals.menuOpen)
            {
                if (WeaponPatches.currentGunInteractController != null)
                {
                    if (currentGunInteractController?.transform.Find("RightHandPositioner") is Transform rightHand)
                        foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            renderer.enabled = false;
                }
            }

            if (grenadeEquipped)
                grenadeEquipped = false;

            pinPulled = false;
            VRGlobals.switchingWeapon = true;

            if (currentGunInteractController != null)
                currentGunInteractController.enabled = false;

            VRGlobals.firearmController = __instance;
            VRGlobals.player = __instance._player;
            VRGlobals.emptyHands = __instance.ControllerGameObject.transform;
            if (Handedness.MirrorLeftHanded)
                VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
            VRGlobals.usingItem = false;

            VRGlobals.scopes.Clear();

            for (int i = 0; i < __instance.Firearms._sightModVisualControllers.Length; i++)
            {
                if (__instance.Firearms._sightModVisualControllers[i]._scopePrefabCache == null)
                    continue;

                Transform baseTransform = __instance.Firearms._sightModVisualControllers[i].transform;

                // Try to find in direct children
                Transform scopeTransform = baseTransform.Find("mod_aim_camera") ?? baseTransform.Find("mod_aim_camera_001");

                // Only check deep scope child if the hierarchy exists
                Transform deepScopeChild = null;
                if (baseTransform.childCount > 0)
                {
                    Transform child0 = baseTransform.GetChild(0);
                    if (child0 != null && child0.childCount > 1)
                    {
                        Transform child1 = child0.GetChild(1);
                        if (child1 != null && child1.childCount > 0)
                        {
                            Transform child2 = child1.GetChild(0);
                            if (child2 != null)
                            {
                                deepScopeChild = child2.Find("mod_aim_camera_000");
                            }
                        }
                    }
                }

                // Only reassign if we found a valid deep scope child AND it matches
                if (deepScopeChild != null) //&& scopeTransform == deepScopeChild)
                    scopeTransform = baseTransform.GetChild(0);

                // If not found, try in the first child's children
                if (scopeTransform == null && baseTransform.childCount > 0)
                {
                    Transform firstChild = baseTransform.GetChild(0);
                    scopeTransform = firstChild.Find("mod_aim_camera") ?? firstChild.Find("mod_aim_camera_001");
                }

                if (scopeTransform == null)
                    continue;

                VRGlobals.scopes.Add(scopeTransform);
                SightModVisualControllers visualController = __instance.Firearms._sightModVisualControllers[i].GetComponent<SightModVisualControllers>();
                if (!visualController)
                    visualController = __instance.Firearms._sightModVisualControllers[i].transform.parent.GetComponent<SightModVisualControllers>();

                if (visualController && VRGlobals.vrOpticController)
                {
                    BoxCollider scopeCollider = visualController.GetComponent<BoxCollider>();
                    if (scopeCollider)
                    {
                        scopeCollider.gameObject.layer = 6;
                        scopeCollider.size = new Vector3(0.09f, 0.04f, 0.02f);
                        scopeCollider.center = new Vector3(-0.04f, 0, -0.075f);
                        scopeCollider.isTrigger = true;
                        scopeCollider.enabled = true;
                    }
                }
            }

            Transform leftHandMarker = __instance.HandsHierarchy.Transforms[10];

            // Setup custom left hand marker position for a few guns that are very misaligned
            string prefabKey = __instance._weaponPrefab.name.Replace("(Clone)", "");
            if (LeftHandIKOverrides.TryGetValue(prefabKey, out Vector3 localOffset))
            {
                Transform existing = __instance.WeaponRoot.Find("VR_LeftHandIK_Override");
                if (existing == null)
                {
                    var go = new GameObject("VR_LeftHandIK_Override");
                    go.transform.SetParent(__instance.WeaponRoot, false);
                    existing = go.transform;
                }
                existing.localPosition = localOffset;
                leftHandMarker = existing;
            }

            VRPlayerManager.leftHandGunIK = leftHandMarker;


            // after the leftHandGunIK assignment in MoveWeaponToIKHands
            /*
            var dbg = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            GameObject.Destroy(dbg.GetComponent<Collider>());
            dbg.transform.SetParent(VRPlayerManager.leftHandGunIK, false);
            dbg.transform.localPosition = Vector3.zero;
            dbg.transform.localScale = Vector3.one * 0.03f;
            dbg.GetComponent<Renderer>().material.color = Color.magenta;
            */

            if (__instance.WeaponRoot.parent.name != "weaponHolder")
            {
                VRGlobals.oldWeaponHolder = __instance.WeaponRoot.parent.gameObject;
                if (__instance.WeaponRoot.parent.Find("RightHandPositioner"))
                {
                    currentGunInteractController = __instance.WeaponRoot.parent.GetComponent<GunInteractionController>();
                    currentGunInteractController.SetPlayerOwner(__instance._player.gameObject.GetComponent<GamePlayerOwner>());
                    VRGlobals.weaponHolder = __instance.WeaponRoot.parent.Find("RightHandPositioner").GetChild(0).gameObject;
                }
                else
                {
                    currentGunInteractController = __instance.WeaponRoot.parent.gameObject.AddComponent<GunInteractionController>();
                    currentGunInteractController.Init();

                    if (Camera.main.stereoEnabled)
                    {
                        HighLightMesh highLightMesh = Camera.main.gameObject.AddComponent<HighLightMesh>();
                        highLightMesh.enabled = false;
                        highLightMesh.Mat = new Material(Shader.Find("Hidden/HighLightMesh"));
                        highLightMesh.LineWidth = 2;
                        highLightMesh.Always = true;
                        highLightMesh.Color = Color.white;
                        currentGunInteractController.SetHighlightComponent(highLightMesh);
                    }
                    currentGunInteractController.SetPlayerOwner(__instance._player.gameObject.GetComponent<GamePlayerOwner>());
                    WeaponMeshParts weaponHighlightParts = WeaponMeshList.GetWeaponMeshList(__instance.WeaponRoot);
                    Transform weaponMeshRoot = __instance.GunBaseTransform.GetChild(0);
                    if (weaponHighlightParts != null)
                    {
                        foreach (var switchName in weaponHighlightParts.firingModeSwitch)
                        {
                            var found = weaponMeshRoot.FindChildRecursive(switchName);
                        }
                        foreach (string magazineMesh in weaponHighlightParts.magazine)
                        {
                            Transform magazineMeshTransform = weaponMeshRoot.FindChildRecursive(magazineMesh);
                            if (magazineMeshTransform)
                            {
                                if (magazineMeshTransform.childCount > 0)
                                    currentGunInteractController.SetMagazine(magazineMeshTransform.GetChild(0), false);
                                else
                                    currentGunInteractController.SetMagazine(weaponMeshRoot.FindChildRecursive(magazineMesh), false);
                            }
                        }
                        foreach (string chamberMesh in weaponHighlightParts.chamber)
                        {
                            if (weaponMeshRoot.FindChildRecursive(chamberMesh))
                                currentGunInteractController.SetChargingHandleOrBolt(weaponMeshRoot.FindChildRecursive(chamberMesh), false);
                        }
                        
                        foreach (string firingModeSwitch in weaponHighlightParts.firingModeSwitch)
                        {
                            if (weaponMeshRoot.FindChildRecursive(firingModeSwitch))
                                currentGunInteractController.SetFireModeSwitch(weaponMeshRoot.FindChildRecursive(firingModeSwitch));
                        }
                    }

                    GameObject rightHandPositioner = new GameObject("RightHandPositioner");
                    rightHandPositioner.transform.SetParent(__instance.WeaponRoot.transform.parent, false);
                    VRGlobals.weaponHolder = new GameObject("weaponHolder");
                    VRGlobals.weaponHolder.transform.SetParent(rightHandPositioner.transform, false);
                    HandsPositioner handsPositioner = rightHandPositioner.AddComponent<HandsPositioner>();
                    handsPositioner.rightHandIk = rightHandPositioner.transform;
                }

                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);

                weaponOffset = WeaponHolderOffsets.GetWeaponHolderOffset(__instance._weaponPrefab.name, __instance.Weapon.WeapClass);
                float weaponAngleOffset = VRSettings.GetRightHandVerticalOffset();
                if (weaponAngleOffset < 50)
                {
                    // How far below 50 the angle is, as a fraction, scales the offset
                    float rotOffsetMultiplier = (50 - weaponAngleOffset) / 100;
                    weaponOffset += new Vector3(0.08f, 0, -0.02f) * rotOffsetMultiplier;
                }
                else if (weaponAngleOffset > 50)
                {
                    // How far above 50 the angle is, as a fraction, scales the offset
                    float rotOffsetMultiplier = (weaponAngleOffset - 50) / 100;
                    weaponOffset += new Vector3(-0.01f, -0.01f, +0.04f) * rotOffsetMultiplier;
                }
                VRGlobals.weaponHolder.transform.localPosition = weaponOffset;
            }
            else if (__instance.WeaponRoot.parent.Find("RightHandPositioner"))
            {
                VRGlobals.weaponHolder = __instance.WeaponRoot.parent.Find("RightHandPositioner").gameObject;
                __instance.WeaponRoot.transform.SetParent(VRGlobals.weaponHolder.transform, false);
            }
            else if (__instance.WeaponRoot.transform.parent && __instance.WeaponRoot.transform.parent.parent.name == "RightHandPositioner")
            {
                __instance.WeaponRoot.transform.parent = __instance.WeaponRoot.transform.parent.parent.parent;
                MoveWeaponToIKHands(__instance);
                return;
            }
            if (VRGlobals.player)
            {
                previousLeftHandMarker = VRGlobals.player._markers[0];
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
            }
            __instance.WeaponRoot.localPosition = new Vector3(0.1327f, -0.0578f, -0.0105f);

            // This goes through all the sights currently on your gun and selects the first optic it finds as the selected sight
            // It ensures that a sight using the camera will always be the active sight
            if (__instance.Firearms?._sightModVisualControllers != null &&
                __instance.Firearms._sightModVisualControllers.Length > 1)
            {
                try
                {
                    int startingAimIndex = __instance.Item?.AimIndex ?? -1;
                    if (startingAimIndex == -1)
                        return;

                    if (__instance._player?.ProceduralWeaponAnimation == null)
                        return;

                    bool foundOptic = false;
                    int sightCount = __instance.Firearms._sightModVisualControllers.Length;

                    for (int i = 0; i < sightCount; i++)
                    {
                        // Directly set the aim index and check if it's an optic
                        __instance.ChangeAimingMode(i);

                        if (__instance._player.ProceduralWeaponAnimation.CurrentScope != null &&
                            __instance._player.ProceduralWeaponAnimation.CurrentScope.IsOptic)
                        {
                            foundOptic = true;
                            break;
                        }
                    }

                    // If no optic found, revert to first sight
                    if (!foundOptic)
                        __instance.ChangeAimingMode(startingAimIndex);
                }
                catch (Exception exception)
                {
                    Plugin.MyLog.LogError($"MoveWeaponToIKHands: Error while changing aiming mode - {exception.Message}");
                    Plugin.MyLog.LogError($"Stack trace: {exception.StackTrace}");
                }
            }
            VRGlobals.vrPlayer.isWeapPistol = (__instance.Weapon.WeapClass == "pistol");          
        }     

        // The optic camera renders the whole scene into a square RT (GClass3687.RenderTexture_0, vanilla 1024) bound as the global
        // _CamTex the lens samples. We shrink that camera's own target; redirecting it to a different target breaks GPU-Instancer grass.
        // decoupleOpticReticle = true: _CamTex points at a separate full-res lensTex, and OpticReticleDecoupler upscales RenderTexture_0
        // into it and re-draws the reticle sharp each frame. false: the lens samples the shrunk RT directly (cheapest, soft reticle).
        public static bool decoupleOpticReticle = true;
        public static int opticLensResolution = 1024;     // lens/reticle texture size when decoupling (sharp).

        [HarmonyPrefix]
        [HarmonyPatch(typeof(OpticCameraManager), "SetResolution")]
        private static void ScaleOpticResolution(ref int resolution)
        {
            // The "Scope Render Resolution" VR Graphics setting: same target object, just smaller (grass-safe)
            int res = VRSettings.GetOpticRenderResolution();
            if (res > 0)
                resolution = Mathf.Clamp(res, 256, 1024);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(OpticCameraManager), "SetResolution")]
        private static void SetupOpticReticleDecouple(OpticCameraManager __instance)
        {
            if (__instance == null || __instance.Camera == null || __instance._renderTexture == null)
                return;

            var decoupler = __instance.Camera.GetComponent<TarkovVR.Source.Weapons.OpticReticleDecoupler>();

            if (!decoupleOpticReticle)
            {
                // Soft-reticle mode: the lens samples the shrunk RenderTexture_0 directly (the vanilla binding SetResolution
                // just set). Drop the decoupler and point _CamTex back at RenderTexture_0.
                if (decoupler != null)
                {
                    UnityEngine.Shader.SetGlobalTexture("_CamTex", __instance._renderTexture);
                    UnityEngine.Object.Destroy(decoupler);
                }
                return;
            }

            int lensSize = Mathf.Max(opticLensResolution, __instance._renderTexture.width);

            if (decoupler == null)
                decoupler = __instance.Camera.gameObject.AddComponent<TarkovVR.Source.Weapons.OpticReticleDecoupler>();

            // Full-res texture the lens samples; the camera keeps rendering into the shrunk RenderTexture_0
            if (decoupler.lensTex == null || decoupler.lensTex.width != lensSize)
            {
                if (decoupler.lensTex != null)
                {
                    decoupler.lensTex.Release();
                    UnityEngine.Object.Destroy(decoupler.lensTex);
                }
                RenderTextureFormat fmt = __instance.Camera.allowHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                decoupler.lensTex = new RenderTexture(lensSize, lensSize, 24, fmt) { name = "OpticLensFullRes" };
                decoupler.lensTex.Create();
            }

            decoupler.retrice = __instance.OpticRetrice;
            // Point the lens at our full-res texture instead of the shrunk camera target.
            UnityEngine.Shader.SetGlobalTexture("_CamTex", decoupler.lensTex);
        }

        // When decoupling, OpticReticleDecoupler draws the reticle sharp into the full-res _CamTex. Vanilla OpticRetrice also draws
        // it low-res into the scene RT, which upscales to a blurry halo under the sharp one: suppress the vanilla draw.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticRetrice), "OnPreCull")]
        private static bool SuppressLowResReticle(EFT.CameraControl.OpticRetrice __instance)
        {
            if (!decoupleOpticReticle)
                return true; // vanilla low-res draw
            __instance._commandBuffer?.Clear();
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "LateUpdate")]
        private static bool FixAdjustableOptic(EFT.CameraControl.OpticComponentUpdater __instance)
        {
            if (__instance.MainCamera == null || __instance.cameraPivot == null)
            {
                return false;
            }
            // The updater lives on the optic camera, so __instance.transform is the optic camera: move it onto the pivot
            // (the main camera). cameraPivot (transform_0 before 4.1) is the source here, never the assignment target.
            __instance.transform.position = __instance.cameraPivot.position;
            __instance.transform.rotation = __instance.cameraPivot.rotation;

            //__instance.opticCamera.useOcclusionCulling = __instance.MainCamera.useOcclusionCulling;
            if (__instance.undithering != null && __instance.mainUndithering != null)
            {
                __instance.undithering.enabled = __instance.mainUndithering.enabled;
                __instance.undithering.shader = __instance.mainUndithering.shader;
            }
            if (__instance.volumetricLightRenderer != null && __instance.mainVolumetricLightRenderer != null)
            {
                __instance.volumetricLightRenderer.enabled = __instance.mainVolumetricLightRenderer.enabled;
                __instance.volumetricLightRenderer.DefaultSpotCookie = __instance.mainVolumetricLightRenderer.DefaultSpotCookie;
                __instance.volumetricLightRenderer.Resolution = __instance.mainVolumetricLightRenderer.Resolution;
            }
            TOD_Scattering tod_Scattering_1 = __instance.tod_Scattering_1;
            TOD_Scattering tod_Scattering_0 = __instance.tod_Scattering_0;
            MBOIT_Scattering mboit_Scattering_1 = __instance.mboit_Scattering_1;
            MBOIT_Scattering mboit_Scattering_0 = __instance.mboit_Scattering_0;
            if (tod_Scattering_1 != null && tod_Scattering_0 != null)
            {
                // Keep enabled: FogSix draws its fog through TOD_Scattering's image effect, so this is what puts fog in the scope
                tod_Scattering_1.enabled = tod_Scattering_0.enabled;
                tod_Scattering_1.DitheringTexture = tod_Scattering_0.DitheringTexture;
                tod_Scattering_1.GlobalDensity = tod_Scattering_0.GlobalDensity;
                tod_Scattering_1.HeightFalloff = tod_Scattering_0.HeightFalloff;
                tod_Scattering_1.SunrizeGlow = tod_Scattering_0.SunrizeGlow;
                tod_Scattering_1.ZeroLevel = tod_Scattering_0.ZeroLevel;
                tod_Scattering_1.MBOIT = tod_Scattering_0.MBOIT;
                if (mboit_Scattering_1 != null && mboit_Scattering_0 != null)
                {
                    mboit_Scattering_1.Sky = mboit_Scattering_0.Sky;
                    mboit_Scattering_1.enabled = mboit_Scattering_0.enabled;
                    mboit_Scattering_1.DitheringTexture = mboit_Scattering_0.DitheringTexture;
                    mboit_Scattering_1.GlobalDensity = mboit_Scattering_0.GlobalDensity;
                    mboit_Scattering_1.HeightFalloff = mboit_Scattering_0.HeightFalloff;
                    mboit_Scattering_1.SunrizeGlow = mboit_Scattering_0.SunrizeGlow;
                    mboit_Scattering_1.ZeroLevel = mboit_Scattering_0.ZeroLevel;
                    mboit_Scattering_1.MBOITDitherScale = mboit_Scattering_0.MBOITDitherScale;
                    mboit_Scattering_1.ScatterColorMultiplier = mboit_Scattering_0.ScatterColorMultiplier;
                    mboit_Scattering_1.ScatterDensityBias = mboit_Scattering_0.ScatterDensityBias;
                    mboit_Scattering_1.ScatterDensityMultiplier = mboit_Scattering_0.ScatterDensityMultiplier;
                    mboit_Scattering_1.SlicesDistributionExponent = mboit_Scattering_0.SlicesDistributionExponent;
                    mboit_Scattering_1.SOFT_SCALE = mboit_Scattering_0.SOFT_SCALE;
                    float clampedFov = Mathf.Min(Mathf.Max(__instance.opticSightFov, 1f), 11f);
                    clampedFov = (clampedFov - 1f) / 10f;
                    float num2 = 1f + clampedFov * __instance.OpticScatterReductionStrength;
                    mboit_Scattering_1.ScatterDensityPower = mboit_Scattering_0.ScatterDensityPower * num2;
                    mboit_Scattering_1.ScatterGreyscale = mboit_Scattering_0.ScatterGreyscale;
                    mboit_Scattering_1.HeightFalloffFactor = mboit_Scattering_0.HeightFalloffFactor;
                    mboit_Scattering_1.ScatteringMBOITComputeShader = mboit_Scattering_0.ScatteringMBOITComputeShader;
                    mboit_Scattering_1.ScatteringMBOITFinalPassComputeShader = mboit_Scattering_0.ScatteringMBOITFinalPassComputeShader;
                    mboit_Scattering_1.ScatteringMBOITRCPComputeShader = mboit_Scattering_0.ScatteringMBOITRCPComputeShader;
                    mboit_Scattering_1.ScatteringSlicesComputeShader = mboit_Scattering_0.ScatteringSlicesComputeShader;
                }
            }
            if (__instance._opticSight.CameraData.IsAdjustableOptic)
            {
                __instance._scopeZoomHandler.UpdateScope();
            }
            return false;
        }

        //Waits for vrOpticController to be created before going forward with the CopyComponentFromOptic method
        static SightModVisualControllers visualController;
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void WaitForVROpticController(EFT.CameraControl.OpticComponentUpdater __instance, OpticSight opticSight)
        {
            if (!VRGlobals.vrOpticController)
                return;
        }

        // This actually handles the scope zoom 
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.CameraControl.OpticComponentUpdater), "CopyComponentFromOptic")]
        private static void SetOpticCamFoV(EFT.CameraControl.OpticComponentUpdater __instance, OpticSight opticSight)
        {
            if (!visualController)
                visualController = opticSight.transform.parent.GetComponent<SightModVisualControllers>();
            if (!visualController)
                visualController = opticSight.transform.parent.parent.GetComponent<SightModVisualControllers>();

            if (rangeFinder)
            {
                opticSight.transform.Find("linza").gameObject.SetActive(true);
                VRGlobals.vrOpticController.isSmoothZoom = false;
                VRGlobals.vrOpticController.isAdjustable = false;
                VRGlobals.vrOpticController.minFov = 3.2f;
                VRGlobals.vrOpticController.maxFov = 3.2f;
            }

            if (visualController && VRGlobals.vrOpticController)
            {
                VRGlobals.vrOpticController.scopeCamera = __instance.opticCamera;
                currentScope = __instance.cameraPivot;

                if (Handedness.MirrorLeftHanded)
                    currentScope.parent.localScale = new Vector3(-1, 1, 1);

                string scopeName = opticSight.name;
                BoxCollider scopeCollider;

                if (scopeName.Contains("mode") || scopeName.Contains("Mode"))
                {
                    if (currentScope)
                        VRGlobals.vrPlayer.scopeUiPosition = currentScope.parent.Cast<Transform>()
                            .FirstOrDefault(child => child.name.Equals("backLens", StringComparison.OrdinalIgnoreCase));
                    opticSight.transform.parent.gameObject.layer = 6;
                    scopeCollider = opticSight.transform.parent.GetComponent<BoxCollider>();
                }
                else
                {
                    if (currentScope)
                        VRGlobals.vrPlayer.scopeUiPosition = currentScope.Cast<Transform>()
                            .FirstOrDefault(child => child.name.Equals("backLens", StringComparison.OrdinalIgnoreCase));
                    opticSight.gameObject.layer = 6;
                    scopeCollider = opticSight.GetComponent<BoxCollider>()
                                    ?? opticSight.transform.parent.GetComponent<BoxCollider>();
                }

                if (scopeCollider)
                {
                    scopeCollider.size = new Vector3(0.09f, 0.04f, 0.02f);
                    scopeCollider.center = new Vector3(-0.04f, 0, -0.075f);
                    scopeCollider.isTrigger = true;
                    scopeCollider.enabled = true;
                }

                // Read scope characteristics straight from base data
                var camData = opticSight.CameraData;
                VRGlobals.vrOpticController.isAdjustable = camData.IsAdjustableOptic;
                VRGlobals.vrOpticController.isSmoothZoom = camData is ScopeSmoothCameraData;

                if (camData is ScopeSmoothCameraData smoothData)
                {
                    VRGlobals.vrOpticController.minFov = smoothData.MinMaxFieldOfView.y;
                    VRGlobals.vrOpticController.maxFov = smoothData.MinMaxFieldOfView.x;
                    VRGlobals.vrOpticController.baseZoomHandler = smoothData.ScopeZoomHandler;
                }
                else if (camData is ScopeCameraData scopeData)
                {
                    // Discrete toggle or fixed — single FOV per mode
                    VRGlobals.vrOpticController.minFov = scopeData.FieldOfView;
                    VRGlobals.vrOpticController.maxFov = scopeData.FieldOfView;
                }
                else
                {
                    VRGlobals.vrOpticController.baseZoomHandler = null;
                }
                VRGlobals.vrOpticController.SetScopeSensitivity();
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Collimators try to do some stupid shit which stops them from displaying so disable it here
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(CameraClass), "method_12")]
        //private static bool FixCollimatorSights(CameraClass __instance)
        //{
        //    return false;
        //}

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(InteractionContextHelper.CG_Class1750), "method_0")]
        private static bool PreventUsingStationaryWeapon(InteractionContextHelper.CG_Class1750 __instance)
        {
            return false;
        }
        
        // Tarkov's physics-stop coroutine breaks physically held items: don't start it until the held item is dropped
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LootItem), "StartPhysics")]
        private static bool DisableCoroutineWhenHeldItem(LootItem __instance)
        {
            __instance._isWorkCollisionEnter = false;
            
            if (__instance._rigidBody == null) 
                return false;
            
            Rigidbody rigidBody = __instance._rigidBody;
            ObjectVisibility visibilityChecker = __instance.GetVisibilityChecker();
            UpdateController.SupportRigidbody(rigidBody, __instance.PhysicsQuality, visibilityChecker);
            __instance._physicsStopWorker = __instance.PhysicsStopWorker();

            bool isHeld = VRGlobals.handsInteractionController != null &&
                          VRGlobals.handsInteractionController.heldItem == __instance;

            if (!isHeld)
                __instance.StartCoroutine(__instance._physicsStopWorker);
            
            return false;
        }

        // Prevent EFT from destroying the Rigidbody on a held item via StopPhysics
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LootItem), "StopPhysics")]
        private static bool PreventStopPhysicsOnHeldItem(LootItem __instance)
        {
            if (VRGlobals.handsInteractionController == null)
                return true;

            return VRGlobals.handsInteractionController.heldItem != __instance;
        }

        // Patch PlayerBridge.TotalWeight getter to include the weight of the currently held item
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PhysicalBase.PlayerBridge), "TotalWeight", MethodType.Getter)]
        private static void AddHeldItemWeight(PhysicalBase.PlayerBridge __instance, ref float __result)
        {
            // Every player's weight comes through here: only ours carries the item in our hand
            if (__instance == null || __instance.iPlayer == null || !__instance.iPlayer.IsYourPlayer)
                return;
            if (!VRSettings.GetHeldItemWeight())
                return;
            if (VRGlobals.handsInteractionController == null)
                return;

            LootItem heldItem = VRGlobals.handsInteractionController.heldItem;
            
            if (heldItem == null)
                return;

            try
            {
                __result += heldItem._item.TotalWeight;
            }
            catch
            {
                // ignored
            }
        }

        // PlayerPhysicalClass.OnWeightUpdated reads Inventory.TotalWeight directly,
        // bypassing PlayerBridge.TotalWeight. Postfix recalculates with the held item weight added.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Physical), "OnWeightUpdated")]
        private static void FixWeightAfterOnWeightUpdated(Physical __instance)
        {
            if (!VRSettings.GetHeldItemWeight()) 
                return;
            
            if (VRGlobals.handsInteractionController == null) 
                return;
            
            LootItem heldItem = VRGlobals.handsInteractionController.heldItem;
            
            if (heldItem == null) 
                return;
            
            if (!__instance._playerBridge.iPlayer.IsYourPlayer) 
                return;

            try
            {
                float extraWeight = heldItem._item.TotalWeight;
                bool elite = __instance._playerBridge.Skills.StrengthBuffElite;
                float baseWeight = elite
                    ? (float)__instance._player.InventoryController.Inventory.TotalWeightEliteSkill
                    : (float)__instance._player.InventoryController.Inventory.TotalWeight;
                float totalWeight = baseWeight + extraWeight;

                // Inertia and acceleration
                GlobalConfiguration.InertiaSettings inertia = Singleton<GlobalConfiguration>.Instance.Inertia;
                __instance.Inertia = __instance.CalculateValue(__instance.BaseInertiaLimits, totalWeight);
                __instance.SprintAcceleration = inertia.SprintAccelerationLimits.InverseLerp(__instance.Inertia);
                __instance.PreSprintAcceleration = inertia.PreSprintAccelerationLimits.Evaluate(__instance.Inertia);
                float num2 = Mathf.Lerp(inertia.MinMovementAccelerationRangeRight.x, inertia.MaxMovementAccelerationRangeRight.x, __instance.Inertia);
                float num3 = Mathf.Lerp(inertia.MinMovementAccelerationRangeRight.y, inertia.MaxMovementAccelerationRangeRight.y, __instance.Inertia);
                EFTHardSettings.Instance.MovementAccelerationRange.MoveKey(1, new Keyframe(num2, num3));
                __instance.MoveSideInertia = inertia.SideTime.Evaluate(__instance.Inertia);
                __instance.MoveDiagonalInertia = inertia.DiagonalTime.Evaluate(__instance.Inertia);

                // Overweight and speed
                __instance.Overweight = __instance.BaseOverweightLimits.InverseLerp(totalWeight);
                __instance.WalkOverweight = __instance.WalkOverweightLimits.InverseLerp(totalWeight);
                __instance.WalkSpeedLimit = 1f - __instance.WalkSpeedOverweightLimits.InverseLerp(totalWeight);
                __instance.SprintOverweight = __instance.SprintOverweightLimits.InverseLerp(totalWeight);

                // Consumptions
                __instance.Consumptions[Physical.EConsumptionType.OverweightIdle]
                    .SetActive(__instance, __instance.Overweight >= 1f);
                __instance.Consumptions[Physical.EConsumptionType.OverweightIdle].Delta.SetDirty();
                __instance.Consumptions[Physical.EConsumptionType.SitToStand].AllowsRestoration = __instance.Overweight >= 1f;
                __instance.Consumptions[Physical.EConsumptionType.StandUp].AllowsRestoration = __instance.Overweight >= 1f;
                __instance.Consumptions[Physical.EConsumptionType.Walk].Delta.SetDirty();
                __instance.Consumptions[Physical.EConsumptionType.Sprint].Delta.SetDirty();
                __instance.Consumptions[Physical.EConsumptionType.VaultLegs].Delta.SetDirty();
                __instance.Consumptions[Physical.EConsumptionType.VaultHands].Delta.SetDirty();
                __instance.Consumptions[Physical.EConsumptionType.ClimbLegs].Delta.SetDirty();
                __instance.Consumptions[Physical.EConsumptionType.ClimbHands].Delta.SetDirty();

                __instance.MaxPoseLevel = (__instance.Overweight >= 1f) ? 0.9f : 1f;
                __instance.FallDamageMultiplier = Mathf.Lerp(1f, __instance.StaminaParameters.FallDamageMultiplier, __instance.Overweight);
                __instance.SoundRadius = __instance.StaminaParameters.SoundRadius.Evaluate(__instance.Overweight);
                __instance.TransitionSpeed.SetDirty();
                __instance.PoseLevelDecreaseSpeed.SetDirty();
                __instance.PoseLevelIncreaseSpeed.SetDirty();
                __instance.MinStepSound.SetDirty();

                // Re-fire WeightRelatedValuesUpdated on MovementContext so WalkInertia,
                // TiltInertia, SprintBrakeInertia etc. are recalculated with the correct Inertia
                __instance._player.MovementContext.WeightRelatedValuesUpdated();
            }
            catch
            {
                // ignored
            }
        }

        // Triggers OnWeightUpdated (and FixWeightAfterOnWeightUpdated with it) every frame while holding an item, so stamina stays current
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PhysicalBase), "LateUpdate")]
        private static void ForceWeightRecalcWhileHoldingItem(PhysicalBase __instance)
        {
            if (!VRSettings.GetHeldItemWeight()) 
                return;
            
            if (VRGlobals.handsInteractionController?.heldItem == null) 
                return;
            
            if (__instance._playerBridge == null) 
                return;
            
            if (!__instance._playerBridge.iPlayer.IsYourPlayer) 
                return;
            
            __instance._weightIsOutOfDate = true;
        }
        
        // throwingHand = the physical controller that let go
        public static void DropObject(LootItem val, bool useThrowVelocity = false, SteamVR_Input_Sources throwingHand = SteamVR_Input_Sources.LeftHand)
        {
            AssetPoolObject component = val.GetComponent<AssetPoolObject>();
            GameObject gameObject = val.gameObject;

            float makeVisibleAfterDelay = 0.05f;
            val._rigidBody = gameObject.GetComponent<Rigidbody>() ?? gameObject.AddComponent<Rigidbody>();

            if (gameObject.activeInHierarchy)
                val.StartPhysics();
            else
                val._startPhysicsOnEnable = true;

            if (component != null)
                component.RegisteredComponentsToClean.Add(val._rigidBody);

            val._rigidBody.mass = val._item.TotalWeight;
            val._rigidBody.isKinematic = false;
            val._rigidBody.useGravity = true;
            val._rigidBody.detectCollisions = true;

            if (useThrowVelocity)
            {
                Vector3 throwVelocity = ControllerVelocity.GetSteamVRVelocity(throwingHand);
                Vector3 angularVelocity = ControllerVelocity.GetSteamVRAngularVelocity(throwingHand);

                if (throwVelocity.magnitude > 0.1f)
                {
                    // Transform from controller local space to world space
                    Vector3 worldSpaceVelocity = VRGlobals.vrOffsetter.transform.TransformDirection(throwVelocity);

                    // Apply velocity multiplier if needed
                    float velocityMultiplier = 1.0f;
                    worldSpaceVelocity *= velocityMultiplier;

                    // Cap max speed
                    if (worldSpaceVelocity.magnitude > 10f)
                        worldSpaceVelocity = worldSpaceVelocity.normalized * 10f;

                    val._rigidBody.velocity = worldSpaceVelocity;
                    val._rigidBody.angularVelocity = angularVelocity; // Angular velocity might also need transformation if it's not working right
                }
            }

            val._currentPhysicsTime = 0f;

            List<Collider> colliders = component.GetColliders(includeNestedAssetPoolObjects: true);
            if (colliders.Count == 0)
            {
                Plugin.MyLog.LogError("No colliders found on item: " + gameObject.name);
            }
            else
            {
                LootItem.SetupBoundingCollider(gameObject, colliders, val._boundCollider);
            }

            val._cullingRegisterRadius = 0.005f;
            Vector3 size = val._boundCollider.size;
            val._rigidBody.collisionDetectionMode =
                size.x * size.y * size.z <= EFTHardSettings.Instance.LootVolumeForHighQuallityPhysicsClient
                ? CollisionDetectionMode.Continuous
                : CollisionDetectionMode.Discrete;

            val.OnRigidbodyStarted();

            if (makeVisibleAfterDelay > 0f)
            {
                val.MakeVisible(isVisible: false);
                val.StartCoroutine(val.MakeVisibleTimer(makeVisibleAfterDelay));
            }
        }

        // Starts the loot item's physics-settle coroutine on a just-dropped item. DropObject doesn't start it: while the item is
        // held the body is kinematic, IsRigidbodyDone() is true for a kinematic body (IsSleeping()), and the coroutine's first
        // tick would StopPhysics and destroy the rigidbody. Call this after the body is non-kinematic and the hold is released.
        // WakeUp makes the first IsSleeping() check false. The coroutine frees the rigidbody at rest, which is also what lets
        // FIKA's ItemPositionSyncer reach Done.
        public static void StartSettleCoroutine(LootItem item)
        {
            if (item == null || item._rigidBody == null || !item.gameObject.activeInHierarchy)
                return;
            item._rigidBody.WakeUp();
            item._physicsStopWorker = item.PhysicsStopWorker();
            item.StartCoroutine(item._physicsStopWorker);
        }

        [HarmonyPatch(typeof(MedsController), "Spawn")]
        [HarmonyPrefix]
        private static void CleanupHighlightMeshMeds(MedsController __instance)
        {
            // Our own meds only: else a bot healing takes our weapon-part highlight off the camera
            if (__instance == null || __instance._player == null || !__instance._player.IsYourPlayer)
                return;
            RemoveHighlightFromCamera();
        }

        /*
        private static int lastStateHash = 0;
        private static FieldInfo delayedEventsField;
        private static FieldInfo animEventField;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AnimationEventSystem.AnimationEventsEmitter), "EmitEvents")]
        public static bool logItemAnimationEvents = false;   // every arms-animator state change and event while using an item ("[EatLog]")
        public static void LogEvents(AnimationEventSystem.AnimationEventsEmitter __instance)
        {
            if (!logItemAnimationEvents || !VRGlobals.usingItem) return;

            var animator = VRGlobals.player.ArmsAnimatorCommon;
            if (animator == null) return;
            var state = animator.GetCurrentAnimatorStateInfo(1);

            if (state.fullPathHash != lastStateHash)
            {
                Plugin.MyLog.LogInfo(
                    $"[EatLog] STATE: {lastStateHash} -> {state.fullPathHash} (t={state.normalizedTime:F3})");
                lastStateHash = state.fullPathHash;
            }

            if (delayedEventsField == null)
            {
                delayedEventsField = __instance.GetType().GetField(
                    "DelayedEvents",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (delayedEventsField == null) return;
            }

            var queue = delayedEventsField.GetValue(__instance) as System.Collections.IEnumerable;
            if (queue == null) return;

            foreach (var entry in queue)
            {
                if (entry == null) continue;
                if (animEventField == null)
                {
                    animEventField = entry.GetType().GetField(
                        "AnimationEvent",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                }
                var ev = animEventField?.GetValue(entry);
                Plugin.MyLog.LogInfo($"[EatLog] EVENT @ t={state.normalizedTime:F3}: {ev}");
            }
        }
        */

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MedsController), "Spawn")]
        private static void SpawnMedsController(MedsController __instance, float animationSpeed, Action callback)
        {
            try
            {
                EFT.Player player = __instance._player;

                if (player == null || !player.IsYourPlayer)
                    return;

                // Manual eating (EatingPatches / EatingInteractionController) is a separate postfix on this same Spawn.
                // The body below runs for food too (both arm IKs off, usingItem = true): don't early-return for food here.

                if (VRGlobals.menuOpen)
                {
                    if (WeaponPatches.currentGunInteractController != null)
                    {
                        Transform rightHand = WeaponPatches.currentGunInteractController?.transform.Find("RightHandPositioner");
                        if (rightHand != null)
                        {
                            foreach (var renderer in rightHand.GetComponentsInChildren<Renderer>(true))
                            {
                                if (renderer != null)
                                    renderer.enabled = false;
                            }
                        }
                    }
                }

                if (__instance._controllerObject != null)
                {
                    VRGlobals.emptyHands = __instance._controllerObject.transform;

                    if (Handedness.MirrorLeftHanded)
                        VRGlobals.emptyHands.localScale = new Vector3(-1, 1, 1);
                }
                
                if (VRGlobals.ikManager != null)
                {
                    VRGlobals.ikManager.rightArmIk.solver.target = null;
                    VRGlobals.ikManager.rightArmIk.enabled = false;
                    VRGlobals.ikManager.leftArmIk.solver.target = null;
                    VRGlobals.ikManager.leftArmIk.enabled = false;
                }
                
                VRGlobals.usingItem = true;

                // Safely handle player marker
                if (VRGlobals.player?._markers != null && VRGlobals.player._markers.Length > 0)
                {
                    previousLeftHandMarker = VRGlobals.player._markers[0];
                }
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"Error in MedsControllerSpawnPatch: {exception.Message}");
            }
            //Plugin.MyLog.LogError($"[SpawnMedsController] Spawning meds controller for {player} with item {item}");
        }      
        
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CollimatorSight), "OnEnable")]
        private static void FixCollimatorParallaxEffect(CollimatorSight __instance)
        {
            if (!__instance.CollimatorMaterial.HasProperty("_MarkScale"))
                return;
            // Mark scale pulls the red dot/holo closer to the lens the higher it is, which makes it shift around when looking
            // as this value decreases it gets pushed further away from the lens, gets smaller, but it stops shifting around
            float markScale = __instance.CollimatorMaterial.GetFloat("_MarkScale");
            if (markScale != 1) {
                __instance.CollimatorMaterial.SetFloat("_MarkScale", 1f);
                // Mark shift increases the size of the dot/holo the smaller the value, the more negative it gets the smaller it gets
                float newMarkShift = 150 + ((1 - markScale) * 125);
                __instance.CollimatorMaterial.SetVector("_MarkShift",new Vector4(0, newMarkShift * -1, 0,0));
            }
        }


    }
}
public static class GameObjectExtensions
{
    public static Transform FindChildRecursive(this Transform parent, string childName)
    {
        foreach (Transform child in parent)
        {
            if (child.name.Equals(childName, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
            Transform found = child.FindChildRecursive(childName);
            if (found != null)
            {
                return found;
            }
        }
        return null;
    }
}