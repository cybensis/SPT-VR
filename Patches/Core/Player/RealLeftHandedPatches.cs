using EFT;
using HarmonyLib;
using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Body;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;

namespace TarkovVR.Patches.Core.Player
{
    // The gun in the LEFT hand (Handedness.GunInLeftHand: right-handed mode, the left hand took it off the sling; nothing is mirrored but
    // the poses below). The gun is placed as always - on the rig's gun hand, which the left controller drives then, with the right-handed
    // hold mirrored once (VRPlayerManager.MirroredGunHold). EFT aims its RIGHT arm at the gun's grip and its LEFT at the support (Player.
    // IkProcess sets both, Player.IkApply solves them); right after IkProcess, first of its postfixes (the elbow bend normals read the
    // targets after it):
    //  - left arm: the right hand's grip pose mirrored across the gun's centre plane (HandMirror.TryMirrorPalm)
    //  - right arm: the left hand's foregrip pose mirrored while supporting, else its own controller the right-handed way
    // The gun's centre plane is its Weapon_root's x = 0 (measured on the AK-74 / M4A1 / M700 models: bore along -y, sights +z, the two
    // hand markers at x = +3 / -4 cm). The mod's own arm targets (solver.target wins over IKPosition) are taken off for the solve and put
    // back after IkApply. The fingers: HandMirror.CaptureGunHandGrip / ApplyRealLeftHanded (HandsInteractionController.LateUpdate).
    [HarmonyPatch]
    internal static class RealLeftHandedPatches
    {
        private static bool swapped;
        private static Transform leftTargetBefore, rightTargetBefore;
        // The limbs' own update, switched off while the gun is in the left hand, put back as it was when that ends (headset: left
        // switched off, the left hand jittered / lagged after handing the gun back until a foregrip grab re-enabled it)
        private static bool limbUpdatesOff;
        private static bool leftEnabledBefore, rightEnabledBefore;
        private static Transform swayFrameFor, swayFrame;

        internal static bool Active => Handedness.GunInLeftHand && VRGlobals.vrPlayer != null && VRGlobals.firearmController != null
            && VRGlobals.firearmController.WeaponRoot != null && !ChestHang.Detached;

        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static void SwapArms(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || __instance._limbs == null || __instance._limbs.Length < 2)
                return;
            if (!Active)
            {
                if (limbUpdatesOff)
                {
                    limbUpdatesOff = false;
                    __instance._limbs[0].enabled = leftEnabledBefore;
                    __instance._limbs[1].enabled = rightEnabledBefore;
                }
                return;
            }
            IKSolverLimb left = __instance._limbs[0].solver;
            IKSolverLimb right = __instance._limbs[1].solver;

            // What EFT aimed each hand at: the right at the gun's grip, the left at the support (its own target, when the mod set one)
            Vector3 gripPosition = right.IKPosition;
            Quaternion gripRotation = right.IKRotation;
            Vector3 supportPosition = left.target != null ? left.target.position : left.IKPosition;
            Quaternion supportRotation = left.target != null ? left.target.rotation : left.IKRotation;

            // The plane rides EFT's sway / inertia: that moves Weapon_root_anim (PlayerBones.ShiftWeaponRoot / Kinematics) under
            // Weapon_root, gun mesh and markers with it, so a plane on Weapon_root mirrored the hand the OPPOSITE way to the gun's sway
            // (headset: "the gun moves slightly out of hand while moving / turning"; the right hand on the foregrip jittered). On the
            // models Weapon_root_anim sits on Weapon_root at rest (zero offset, zero turn), so its x is the same centre plane.
            Transform weaponRoot = VRGlobals.firearmController.WeaponRoot;
            if (swayFrameFor != weaponRoot)
            {
                swayFrameFor = weaponRoot;
                swayFrame = weaponRoot.Find("Weapon_root_anim") ?? weaponRoot;
            }
            Vector3 planeNormal = swayFrame.right;
            Vector3 planePoint = swayFrame.position;
            if (!HandMirror.TryMirrorPalm(gripRotation, planeNormal, rightToLeft: true, out Quaternion leftRotation))
                return;
            Vector3 leftPosition = ReflectPoint(gripPosition, planePoint, planeNormal);
            Vector3 rightPosition;
            Quaternion rightRotation;
            if (VRGlobals.vrPlayer.isSupporting)
            {
                rightPosition = ReflectPoint(supportPosition, planePoint, planeNormal);
                if (!HandMirror.TryMirrorPalm(supportRotation, planeNormal, rightToLeft: false, out rightRotation))
                    return;
            }
            else if (!VRGlobals.vrPlayer.ControllerHandPose(rightController: true, out rightPosition, out rightRotation))
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
            if (!limbUpdatesOff)
            {
                limbUpdatesOff = true;
                leftEnabledBefore = __instance._limbs[0].enabled;
                rightEnabledBefore = __instance._limbs[1].enabled;
            }
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
