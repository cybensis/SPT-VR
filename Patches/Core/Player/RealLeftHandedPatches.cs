using EFT;
using HarmonyLib;
using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Body;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;

namespace TarkovVR.Patches.Core.Player
{
    // Real left-handed mode, stage 2b test (Handedness.RealLeftHanded): the gun in the LEFT hand, not mirrored. The gun is placed as
    // always (on the rig's gun hand, which the left controller drives in left-handed mode, with the right-handed offsets mirrored). The
    // rig's hands follow the SWAPPED controllers, so EFT aims its right arm at the left controller's side and its left arm at the right
    // one's - the mirrored mode hides that by flipping the arms (scale -1); without the flip, every arm target is set here, right after
    // Player.IkProcess (Player.IkApply then solves them):
    //  - holding the gun: left arm = the right hand's grip pose mirrored across the gun's centre plane (HandMirror.TryMirrorPalm);
    //    right arm = the left hand's foregrip pose mirrored while supporting, else its own controller
    //  - otherwise (empty hands, a knife, the gun slung / support-held): each arm on its OWN controller, the right-handed way
    // The gun's centre plane is its Weapon_root's x = 0 (measured on the AK-74 / M4A1 / M700 models: bore along -y, sights +z, the
    // two hand markers at x = +3 / -4 cm). The mod's own arm targets (solver.target wins over IKPosition) are taken off for the solve
    // and put back after IkApply. The fingers swap at the end of HandsInteractionController.LateUpdate (HandMirror.SwapFingers).
    // Not in this test: the slung / support-only gun, a knife in hand, eating / meds, Index finger tracking, the wrist UI.
    [HarmonyPatch]
    internal static class RealLeftHandedPatches
    {
        private static bool swapped;
        private static Transform leftTargetBefore, rightTargetBefore;

        // Holding the gun the left-handed way: the hands go on it, mirrored
        internal static bool GunInLeftHand => Handedness.RealLeftHanded && VRGlobals.firearmController != null
            && VRGlobals.firearmController.WeaponRoot != null && !ChestHang.Detached;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static void SwapArms(EFT.Player __instance)
        {
            if (!Handedness.RealLeftHanded || !__instance.IsYourPlayer || VRGlobals.vrPlayer == null || __instance._limbs == null || __instance._limbs.Length < 2)
                return;
            IKSolverLimb left = __instance._limbs[0].solver;
            IKSolverLimb right = __instance._limbs[1].solver;

            Vector3 leftPosition, rightPosition;
            Quaternion leftRotation, rightRotation;
            if (GunInLeftHand)
            {
                // What EFT aimed each hand at: the right at the gun's grip, the left at the support (its own target, when the mod set one)
                Vector3 gripPosition = right.IKPosition;
                Quaternion gripRotation = right.IKRotation;
                Vector3 supportPosition = left.target != null ? left.target.position : left.IKPosition;
                Quaternion supportRotation = left.target != null ? left.target.rotation : left.IKRotation;

                Transform weaponRoot = VRGlobals.firearmController.WeaponRoot;
                Vector3 planeNormal = weaponRoot.right;
                Vector3 planePoint = weaponRoot.position;
                if (!HandMirror.TryMirrorPalm(gripRotation, planeNormal, rightToLeft: true, out leftRotation))
                    return;
                leftPosition = ReflectPoint(gripPosition, planePoint, planeNormal);
                if (VRGlobals.vrPlayer.isSupporting)
                {
                    rightPosition = ReflectPoint(supportPosition, planePoint, planeNormal);
                    if (!HandMirror.TryMirrorPalm(supportRotation, planeNormal, rightToLeft: false, out rightRotation))
                        return;
                }
                else if (!VRGlobals.vrPlayer.ControllerHandPose(rightController: true, out rightPosition, out rightRotation))
                    return;
            }
            else if (!VRGlobals.vrPlayer.ControllerHandPose(rightController: false, out leftPosition, out leftRotation)
                || !VRGlobals.vrPlayer.ControllerHandPose(rightController: true, out rightPosition, out rightRotation))
                return;

            if (!swapped)
            {
                leftTargetBefore = left.target;
                rightTargetBefore = right.target;
                swapped = true;
            }
            left.target = null;
            right.target = null;
            // EFT solves both by hand (IkApply); the components' own update would solve them again onto the mod's targets
            __instance._limbs[0].enabled = false;
            __instance._limbs[1].enabled = false;
            left.IKPositionWeight = left.IKRotationWeight = 1f;
            right.IKPositionWeight = right.IKRotationWeight = 1f;
            left.SetIKPosition(leftPosition);
            left.SetIKRotation(leftRotation);
            right.SetIKPosition(rightPosition);
            right.SetIKRotation(rightRotation);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkApply))]
        private static void PutTargetsBack(EFT.Player __instance)
        {
            if (!swapped || !__instance.IsYourPlayer || __instance._limbs == null || __instance._limbs.Length < 2)
                return;
            __instance._limbs[0].solver.target = leftTargetBefore;
            __instance._limbs[1].solver.target = rightTargetBefore;
            swapped = false;
        }

        private static Vector3 ReflectPoint(Vector3 point, Vector3 planePoint, Vector3 planeNormal)
        {
            return point - 2f * Vector3.Dot(point - planePoint, planeNormal) * planeNormal;
        }
    }
}
