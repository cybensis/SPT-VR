using EFT.AssetsManager;
using EFT.Interactive;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Patches.Core.Equippables
{
    [HarmonyPatch]
    internal class PickupAndThrowables
    {
        //------------------------------------------------------   PICKUP AND THROWABLES GLOBALS  ---------------------------------------------------------------------------

        private static Vector3 lastHandPosition = Vector3.zero;
        private static Queue<Vector3> velocityHistory = new Queue<Vector3>();
        private static int maxVelocitySamples = 10;


        //------------------------------------------------------   PICKUP AND THROWABLES PATCHES  ---------------------------------------------------------------------------
        //BSG changed something with how physics was being killed which caused physically holding items to break
        //This disables the coroutine that checks to disable physics until you drop the item you're holding
        [HarmonyPrefix]
        [HarmonyPatch(typeof(LootItem), "method_3")]
        private static bool DisableCoroutineWhenHeldItem(LootItem __instance)
        {
            __instance.bool_1 = false;
            if (__instance._rigidBody != null)
            {
                Rigidbody rigidBody = __instance._rigidBody;
                GClass812 visibilityChecker = __instance.GetVisibilityChecker();
                EFTPhysicsClass.GClass723.SupportRigidbody(rigidBody, __instance.PhysicsQuality, visibilityChecker);
                __instance.ienumerator_0 = __instance.method_4();
                if (VRGlobals.handsInteractionController.heldItem == null)
                    __instance.StartCoroutine(__instance.ienumerator_0);
            }
            return false;
        }




        //------------------------------------------------------   PICKUP AND THROWABLES METHODS  ---------------------------------------------------------------------------
        //These two method handle getting velocity of your controller straight from SteamVR to handle throwing items/grenades
        public static Vector3 GetSteamVRVelocity(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            if (poseAction != null)
            {
                Vector3 velocity = poseAction.GetVelocity(inputSource);
                return velocity;
            }
            return Vector3.zero;
        }
        public static Vector3 GetSteamVRPosition(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            return poseAction != null ? poseAction.GetLocalPosition(inputSource) : Vector3.zero;
        }

        public static Quaternion GetSteamVRRotation(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            return poseAction != null ? poseAction.GetLocalRotation(inputSource) : Quaternion.identity;
        }

        public static Vector3 GetSteamVRAngularVelocity(SteamVR_Input_Sources inputSource)
        {
            SteamVR_Action_Pose poseAction = inputSource == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftHandPose
                : SteamVR_Actions._default.RightHandPose;

            if (poseAction != null)
            {
                Vector3 angularVelocity = poseAction.GetAngularVelocity(inputSource);
                return angularVelocity;
            }
            return Vector3.zero;
        }
        public static void DropObject(LootItem val, bool useThrowVelocity = false)
        {
            AssetPoolObject component = val.GetComponent<AssetPoolObject>();
            GameObject gameObject = val.gameObject;

            float makeVisibleAfterDelay = 0.05f;
            val._rigidBody = gameObject.GetComponent<Rigidbody>() ?? gameObject.AddComponent<Rigidbody>();

            if (gameObject.activeInHierarchy)
                val.method_3();
            else
                val.bool_2 = true;

            if (component != null)
                component.RegisteredComponentsToClean.Add(val._rigidBody);

            val._rigidBody.mass = val.item_0.TotalWeight;
            val._rigidBody.isKinematic = false;
            val._rigidBody.useGravity = true;
            val._rigidBody.detectCollisions = true;

            if (useThrowVelocity)
            {
                SteamVR_Input_Sources throwingHand = SteamVR_Input_Sources.LeftHand;
                Vector3 throwVelocity = GetSteamVRVelocity(throwingHand);
                Vector3 angularVelocity = GetSteamVRAngularVelocity(throwingHand);

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
                LootItem.smethod_1(gameObject, colliders, val._boundCollider);
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
                val.method_10(isVisible: false);
                val.StartCoroutine(val.method_11(makeVisibleAfterDelay));
            }
        }

    }
}
