using EFT.CameraControl;
using HarmonyLib;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Patches.UI;
using TarkovVR.Patches.Visuals;

namespace TarkovVR.Patches
{
    // Every hook the mod has in the player's frame, in the order they run. One Harmony patch per game method, kind and priority;
    // each calls its stages in the order written here (the stages live with the code they belong to).
    //
    // Player.LateUpdate, as Tarkov runs it:
    //   [LateUpdate prefix stages]
    //   Physical.LateUpdate                  [Physical.LateUpdate prefix stage]
    //   VisualPass
    //     ProceduralWeaponAnimation.ProcessEffectors
    //     HeightInterpolation                [HeightInterpolation prefix stage]
    //     FBBIK, MouseLook
    //     PlayerBones.SetShoulders           [SetShoulders prefix stages]
    //     PlayerBones.ShiftWeaponRoot, RotateHead, the arm IK weights
    //     IkProcess                          [IkProcess prefix / postfix stages]
    //     AdjustElbows, IkApply, ProceduralWeaponAnimation.LateTransformations, HandsController.ManualLateUpdate
    //   [VisualPass postfix stage]
    //   PropUpdate                           [PropUpdate prefix stage]
    //   ComplexLateUpdate
    //   [LateUpdate postfix stages]
    // The camera state's ManualLateUpdate runs from the camera's own LateUpdate (Unity decides its order against the player's).
    //
    // A prefix stage that returns false skips the game method. As with separate Harmony prefixes, the stages after it still run,
    // and the game method runs only when every stage returned true. An exception in a stage stops the stages after it, as it
    // stopped the patches after it.
    [HarmonyPatch]
    internal static class FramePipeline
    {
        //--- Player.LateUpdate ------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.LateUpdate))]
        private static bool PlayerLateUpdateBefore(EFT.Player __instance)
        {
            bool runOriginal = UIPatches.FixItemPlacement(__instance);
            VRGrenadeController.CheckGrenadeAnimation(__instance);
            ShotgunPatches.PumpBeforeIk(__instance);
            return runOriginal;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PhysicalBase), "LateUpdate")]
        private static void PhysicalLateUpdateBefore(PhysicalBase __instance)
        {
            WeaponPatches.ForceWeightRecalcWhileHoldingItem(__instance);
        }

        //--- Player.VisualPass ------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.HeightInterpolation))]
        private static bool HeightInterpolationBefore(EFT.Player __instance, float timeDeltatime)
        {
            return IKPatches.DisableYAxisSmoothing(__instance, timeDeltatime);
        }

        // First among the SetShoulders prefixes: the upper body follows the headset before the shoulders are placed
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(PlayerBones), nameof(PlayerBones.SetShoulders))]
        private static void SetShouldersFirst(PlayerBones __instance)
        {
            UpperBodyPatches.FollowHeadset(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerBones), nameof(PlayerBones.SetShoulders))]
        private static bool SetShouldersBefore(PlayerBones __instance)
        {
            return IKPatches.OverrideShoulders(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static bool IkProcessBefore(EFT.Player __instance)
        {
            bool runOriginal = IKPatches.RemoveSprintAnimFromHands(__instance);
            VRGunControllerPatches.KeepLeftArmOnController(__instance);
            return runOriginal;
        }

        // Last among the IkProcess prefixes: the rig gets its end-of-frame pin, then the gun goes back on the hand at that pose
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static void IkProcessLast(EFT.Player __instance)
        {
            LateGunPlacementPatches.GunOnTheFinalHand(__instance);
        }

        // First among the IkProcess postfixes: with the gun in the left hand, the hand targets Tarkov just wrote are mirrored
        // onto the other hands (RealLeftHandedPatches)
        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static void IkProcessAfterFirst(EFT.Player __instance)
        {
            RealLeftHandedPatches.SwapArms(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static void IkProcessAfter(EFT.Player __instance)
        {
            BeltGunPatches.SupportSpotGrip(__instance);
            IKPatches.DriveElbowBendNormals(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.VisualPass))]
        private static void VisualPassAfter()
        {
            AOPatches.CountAOTimer();
        }

        //--- the rest of Player.LateUpdate ------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.PropUpdate))]
        private static bool PropUpdateBefore(EFT.Player __instance)
        {
            return WeaponPatches.FixCompassToLeftHand(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.LateUpdate))]
        private static void PlayerLateUpdateAfter(EFT.Player __instance)
        {
            VRMeleeController.ManageKnifeCollider(__instance);
            EatingPatches.LateZeroProps(__instance);
            EatingRecon.Tick(__instance);
            ShotgunPatches.LeverHandAfterIk(__instance);
            WeaponRecon.Tick(__instance);
        }

        //--- the camera ---------------------------------------------------------------
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FirstPersonCameraState), "ManualLateUpdate")]
        private static bool CameraLateUpdateBefore(FirstPersonCameraState __instance)
        {
            return MovementPatches.PositionCamera(__instance);
        }
    }
}
