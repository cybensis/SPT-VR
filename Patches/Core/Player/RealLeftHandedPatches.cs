using EFT;
using HarmonyLib;
using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Body;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;

namespace TarkovVR.Patches.Core.Player
{
    // Real left-handed mode, stage 2b test (Handedness.RealLeftHanded): the gun in the LEFT hand, not mirrored. The gun itself is placed
    // as always (on the rig's gun hand, now driven by the left controller with the right-handed offsets mirrored). EFT then aims its
    // RIGHT arm at the gun's grip and its LEFT arm at the support (Player.IkProcess sets both, Player.IkApply solves them); right after
    // IkProcess both are swapped onto their mirror images across the gun's centre plane:
    //  - left arm: the right hand's grip pose, mirrored (HandMirror.TryMirrorPalm: reflected, then into the left palm's bone frame)
    //  - right arm: supporting = the left hand's foregrip pose, mirrored; free = the right controller, the right-handed way
    // The mod's own arm targets (solver.target overrides IKPosition) are taken off for the solve and put back after IkApply. The fingers
    // swap at the end of HandsInteractionController.LateUpdate (HandMirror.SwapFingers).
    // Not in this test: the slung / support-only gun, Index finger tracking (it would show on the other hand), the wrist UI.
    [HarmonyPatch]
    internal static class RealLeftHandedPatches
    {
        private static bool swapped;
        private static Transform leftTargetBefore, rightTargetBefore;

        private static bool Active(EFT.Player player)
        {
            return Handedness.RealLeftHanded && player.IsYourPlayer && VRGlobals.firearmController != null && VRGlobals.firearmController.WeaponRoot != null
                && VRGlobals.vrPlayer != null && !ChestHang.Detached && player._limbs != null && player._limbs.Length >= 2;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static void SwapArmsOntoGun(EFT.Player __instance)
        {
            if (!Active(__instance))
                return;
            IKSolverLimb left = __instance._limbs[0].solver;
            IKSolverLimb right = __instance._limbs[1].solver;

            // What EFT aimed each hand at: the right at the gun's grip, the left at the support (its own target, when the mod set one)
            Vector3 gripPosition = right.IKPosition;
            Quaternion gripRotation = right.IKRotation;
            Vector3 supportPosition = left.target != null ? left.target.position : left.IKPosition;
            Quaternion supportRotation = left.target != null ? left.target.rotation : left.IKRotation;

            // The gun's centre plane: through its bore (Weapon_root up = toward the butt) and the grip below it
            Transform weaponRoot = VRGlobals.firearmController.WeaponRoot;
            Vector3 bore = weaponRoot.up;
            Vector3 toGrip = gripPosition - weaponRoot.position;
            Vector3 down = toGrip - Vector3.Dot(toGrip, bore) * bore;
            if (down.sqrMagnitude < 1e-8f)
                return;
            Vector3 planeNormal = Vector3.Cross(bore, down.normalized).normalized;
            Vector3 planePoint = weaponRoot.position;

            if (!HandMirror.TryMirrorPalm(gripRotation, planeNormal, rightToLeft: true, out Quaternion leftGripRotation))
                return;
            Vector3 leftGripPosition = ReflectPoint(gripPosition, planePoint, planeNormal);

            Vector3 rightPosition;
            Quaternion rightRotation;
            if (VRGlobals.vrPlayer.isSupporting)
            {
                rightPosition = ReflectPoint(supportPosition, planePoint, planeNormal);
                if (!HandMirror.TryMirrorPalm(supportRotation, planeNormal, rightToLeft: false, out rightRotation))
                    return;
            }
            else
            {
                // The right hand the right-handed way at its controller, with the grip marker's turn off the rig's gun hand
                if (!VRGlobals.vrPlayer.RightControllerHandPose(out rightPosition, out Quaternion controllerHand))
                    return;
                Transform gunRigHand = Handedness.GunRigHand;
                rightRotation = gunRigHand != null ? controllerHand * (Quaternion.Inverse(gunRigHand.rotation) * gripRotation) : controllerHand;
            }

            if (!swapped)
            {
                leftTargetBefore = left.target;
                rightTargetBefore = right.target;
                swapped = true;
            }
            left.target = null;
            right.target = null;
            // EFT solves both by hand (IkApply); the components' own update would solve the left again onto the mod's target
            __instance._limbs[0].enabled = false;
            __instance._limbs[1].enabled = false;
            left.IKPositionWeight = left.IKRotationWeight = 1f;
            right.IKPositionWeight = right.IKRotationWeight = 1f;
            left.SetIKPosition(leftGripPosition);
            left.SetIKRotation(leftGripRotation);
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
