using HarmonyLib;
using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Body;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;

namespace TarkovVR.Patches.Core.Player
{
    // Arm IK for the gun held in the left hand (Handedness.GunInLeftHand: right-handed mode, and the left hand took the gun off the sling).
    // The gun sits on the rig's gun hand as usual, driven by the left controller, with the hold mirrored by VRPlayerManager.MirroredGunHold.
    // EFT aims its right arm at the grip and its left arm at the support point (Player.IkProcess sets both, IkApply solves them). This runs
    // first among IkProcess's postfixes, because the elbow bend normals read the targets after it. It sets:
    //  - left arm: the right hand's grip pose, mirrored across the gun's centre plane (HandMirror.TryMirrorPalm)
    //  - right arm on the foregrip: the left hand's foregrip pose, mirrored
    //  - right arm off the gun: VRPlayerManager.RightControllerHand, a rig child placed in the right controller's pose callback
    //  - right hand holding the gun alone (SupportHeld): right arm on the foregrip as above, left arm free on LeftControllerHand
    // Hands on the gun are solved only by EFT's arm pass. A free hand is also solved by its limb's own IK update onto its rig child;
    // as a world point for EFT's pass alone it jitters.
    // The centre plane is Weapon_root x = 0 (bore along -y, sights +z, hand markers at x = +3 / -4 cm).
    // The mod's targets on the gun hands (solver.target overrides IKPosition) are removed for the solve and restored after IkApply.
    // Fingers: HandMirror.CaptureGunHandGrip / ApplyRealLeftHanded (HandsInteractionController.LateUpdate).
    [HarmonyPatch]
    internal static class RealLeftHandedPatches
    {
        // Targets removed for this frame's solve, restored after IkApply
        private static bool leftSwapped, rightSwapped;
        private static Transform leftTargetBefore, rightTargetBefore;
        // The free right hand's own target, which stays on the arm between frames (the limb's own update solves it in LateUpdate),
        // and the target that was there before
        private static bool freeRightOn;
        private static Transform freeRightTarget, rightTargetOriginal;
        // The limbs' enabled state is changed while the gun is in the left hand and restored when that ends. If the left limb stayed
        // switched off, the left hand would jitter and lag after handing the gun back.
        private static bool limbUpdatesOff;
        private static bool leftEnabledBefore, rightEnabledBefore;
        private static Transform swayFrameFor, swayFrame;
        // The free left hand's own target while the right hand holds the gun alone (SupportHeld), like the free right hand's
        private static bool freeLeftOn;
        private static Transform freeLeftTarget, leftTargetOriginal;

        // True for a gun, or a grenade the left hand took off the chest (GrenadeSlot). Both are held on the rig's gun hand, under weaponHolder.
        internal static bool Active => Handedness.GunInLeftHand && VRGlobals.vrPlayer != null && HeldWeaponRoot() != null && !ChestHang.Detached;

        internal static Transform HeldWeaponRoot()
        {
            // Check the grenade first: firearmController still points at the last gun (even a slung one) while a grenade is in the hands
            if (Source.Player.Interactions.GrenadeSlot.KeepsLeftHand && VRGlobals.player?.HandsController is EFT.Player.BaseGrenadeHandsController grenade)
                return grenade.WeaponRoot;
            if (VRGlobals.firearmController != null && VRGlobals.firearmController.WeaponRoot != null)
                return VRGlobals.firearmController.WeaponRoot;
            return null;
        }

        // The gun was in the left hand, and the left let go while the right held the foregrip, so the right hand holds it alone
        // (ChestHang.SupportOnly, the gun follows the right controller). The right arm stays on the foregrip; the left arm is free on
        // VRPlayerManager.LeftControllerHand.
        internal static bool SupportHeld => Handedness.GunInLeftHand && ChestHang.SupportOnly && VRGlobals.vrPlayer != null
            && VRGlobals.firearmController != null && VRGlobals.firearmController.WeaponRoot != null;

        // Restores both arms. Handedness.SetGunInLeftHand(false) calls it immediately, so whatever takes the arms next (the sling's
        // KeepGunHandFree) starts from their original state. SwapArms also calls it on the first frame it isn't Active.
        internal static void ReleaseArms()
        {
            EFT.Player player = VRGlobals.player;
            if (player == null || player._limbs == null || player._limbs.Length < 2)
            {
                freeRightOn = false;
                freeLeftOn = false;
                limbUpdatesOff = false;
                return;
            }
            EndFreeLeft(player);
            if (freeRightOn)
            {
                freeRightOn = false;
                if (player._limbs[1].solver.target == freeRightTarget)
                    player._limbs[1].solver.target = rightTargetOriginal;
            }
            if (limbUpdatesOff)
            {
                limbUpdatesOff = false;
                player._limbs[0].enabled = leftEnabledBefore;
                player._limbs[1].enabled = rightEnabledBefore;
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkProcess))]
        private static void SwapArms(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || __instance._limbs == null || __instance._limbs.Length < 2)
                return;
            bool supportHeld = !Active && SupportHeld;
            if (!Active && !supportHeld)
            {
                if (freeRightOn || freeLeftOn || limbUpdatesOff)
                    ReleaseArms();
                return;
            }
            if (!supportHeld)
                EndFreeLeft(__instance);
            IKSolverLimb left = __instance._limbs[0].solver;
            IKSolverLimb right = __instance._limbs[1].solver;

            // Read what EFT aimed each hand at: the right at the gun's grip, the left at the support point (or the mod's target, if set).
            // IkProcess just wrote IKPosition from the markers, on the right arm too while the free target is on it. When the right hand
            // holds the gun alone, the left's target is its free target (or a reload latch), so the support is the left marker EFT aimed at.
            Vector3 gripPosition = right.IKPosition;
            Quaternion gripRotation = right.IKRotation;
            bool supportFromTarget = !supportHeld && left.target != null;
            Vector3 supportPosition = supportFromTarget ? left.target.position : left.IKPosition;
            Quaternion supportRotation = supportFromTarget ? left.target.rotation : left.IKRotation;

            // The plane follows EFT's sway and inertia, which move Weapon_root_anim (PlayerBones.ShiftWeaponRoot / Kinematics) under
            // Weapon_root, together with the gun mesh and markers. A plane on Weapon_root would mirror the hand opposite to the sway.
            // At rest Weapon_root_anim sits exactly on Weapon_root, so its x = 0 is the same centre plane.
            if (!GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                return;
            Vector3 leftPosition = Vector3.zero;
            Quaternion leftRotation = Quaternion.identity;
            Transform freeLeft = null;
            if (supportHeld)
            {
                freeLeft = VRGlobals.vrPlayer.LeftControllerHand;
                if (freeLeft == null)
                    return;
            }
            else if (Source.Weapons.GrenadeHold.TryMirrorNormal(out Vector3 mirrorNormal))
            {
                // A grenade is mirrored as a whole (its positioner is scaled -1 on x), so the grip marker is already on the left grip.
                // Unity reports the marker's rotation without the scale's sign, so the palm is mirrored through the positioner's x.
                // Read the marker itself, not the right arm's IK target: EFT only writes that while the right arm's weight
                // (1 - the clip's RIGHT_HAND_WEIGHT, VisualPass) isn't 0, otherwise it still holds the free right hand's pose.
                Transform gripMarker = __instance._markers != null && __instance._markers.Length > 1 ? __instance._markers[1] : null;
                Vector3 grip = gripMarker != null ? gripMarker.position : gripPosition;
                Quaternion gripTurn = gripMarker != null ? gripMarker.rotation : gripRotation;
                // Take the palm from the grip marker's own matrix, which includes the -1 scale, so it is correct wherever the scale's
                // sign ends up. The rotation-based mirror is only the fallback.
                if (gripMarker == null || !HandMirror.TryOtherPalmFromMirroredFrame(gripMarker.localToWorldMatrix, rightToLeft: true, out leftRotation))
                {
                    if (!HandMirror.TryMirrorPalm(gripTurn, mirrorNormal, rightToLeft: true, out leftRotation))
                        return;
                }
                leftPosition = grip;
            }
            else
            {
                if (!HandMirror.TryMirrorPalm(gripRotation, planeNormal, rightToLeft: true, out leftRotation))
                    return;
                leftPosition = ReflectPoint(gripPosition, planePoint, planeNormal);
            }
            bool supporting = VRGlobals.vrPlayer.isSupporting;
            Vector3 rightPosition = Vector3.zero;
            Quaternion rightRotation = Quaternion.identity;
            Transform freeTarget = null;
            if (supporting)
            {
                rightPosition = ReflectPoint(supportPosition, planePoint, planeNormal);
                if (!HandMirror.TryMirrorPalm(supportRotation, planeNormal, rightToLeft: false, out rightRotation))
                    return;
            }
            else
            {
                freeTarget = VRGlobals.vrPlayer.RightControllerHand;
                if (freeTarget == null)
                    return;
            }

            if (!limbUpdatesOff)
            {
                limbUpdatesOff = true;
                leftEnabledBefore = __instance._limbs[0].enabled;
                rightEnabledBefore = __instance._limbs[1].enabled;
            }

            left.IKPositionWeight = left.IKRotationWeight = 1f;
            if (supportHeld)
            {
                // Left arm off the gun: its own target, which its own update also solves later this frame (like the free right hand).
                // On a bolt, handle or round the target is the reload latch (the gun hand's latch, which is on this arm then).
                if (!freeLeftOn)
                {
                    freeLeftOn = true;
                    leftTargetOriginal = left.target;
                }
                freeLeftTarget = freeLeft;
                Transform held = Source.Weapons.VRReloadController.ArmLatch(rightArm: false);
                left.target = held != null ? held : freeLeft;
                __instance._limbs[0].enabled = true;
            }
            else
            {
                // Left arm on the gun: EFT solves it in IkApply. The limb's own update is disabled, or it would solve again onto the mod's target.
                if (!leftSwapped)
                {
                    leftTargetBefore = left.target;
                    leftSwapped = true;
                }
                left.target = null;
                __instance._limbs[0].enabled = false;
                left.SetIKPosition(leftPosition);
                left.SetIKRotation(leftRotation);
            }

            right.IKPositionWeight = right.IKRotationWeight = 1f;
            if (supporting)
            {
                // On the foregrip: same as the left arm on the gun
                if (freeRightOn)
                {
                    freeRightOn = false;
                    if (right.target == freeRightTarget)
                        right.target = rightTargetOriginal;
                }
                if (!rightSwapped)
                {
                    rightTargetBefore = right.target;
                    rightSwapped = true;
                }
                right.target = null;
                __instance._limbs[1].enabled = false;
                right.SetIKPosition(rightPosition);
                right.SetIKRotation(rightRotation);
            }
            else
            {
                // Off the gun: its own target, which its own update also solves later this frame. A hand on a magazine, handle or bolt
                // keeps VRReloadController's latch as its target (the off hand's latch, on this arm then). A hand on the scope's zoom
                // dial targets the dial grip (VROpticController.RightHandDialTarget).
                if (!freeRightOn)
                {
                    freeRightOn = true;
                    rightTargetOriginal = right.target;
                }
                freeRightTarget = freeTarget;
                Transform held = Source.Weapons.VRReloadController.ArmLatch(rightArm: true);
                if (held == null && VRGlobals.vrOpticController != null)
                    held = VRGlobals.vrOpticController.RightHandDialTarget;
                right.target = held != null ? held : freeTarget;
                __instance._limbs[1].enabled = true;
            }
        }

        // Removes the free left hand's target and restores the original, only if the target is still ours (a reload latch is
        // VRReloadController's)
        private static void EndFreeLeft(EFT.Player player)
        {
            if (!freeLeftOn)
                return;
            freeLeftOn = false;
            if (player._limbs[0].solver.target == freeLeftTarget)
                player._limbs[0].solver.target = leftTargetOriginal;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.IkApply))]
        private static void PutTargetsBack(EFT.Player __instance)
        {
            if (!(leftSwapped || rightSwapped) || !__instance.IsYourPlayer || __instance._limbs == null || __instance._limbs.Length < 2)
                return;
            if (leftSwapped)
                __instance._limbs[0].solver.target = leftTargetBefore;
            if (rightSwapped)
                __instance._limbs[1].solver.target = rightTargetBefore;
            leftSwapped = rightSwapped = false;
        }

        // The gun's centre plane, on Weapon_root_anim (see SwapArms)
        internal static bool GunPlane(out Vector3 planePoint, out Vector3 planeNormal)
        {
            planePoint = Vector3.zero;
            planeNormal = Vector3.right;
            Transform weaponRoot = HeldWeaponRoot();
            if (weaponRoot == null)
                return false;
            if (swayFrameFor != weaponRoot)
            {
                swayFrameFor = weaponRoot;
                swayFrame = weaponRoot.Find("Weapon_root_anim") ?? weaponRoot;
            }
            planeNormal = swayFrame.right;
            planePoint = swayFrame.position;
            // Put the plane through the bore. On every gun but one that is Weapon_root_anim's x = 0 plane, but the VPO-101's model sits
            // 2.2 cm off it, which would put the mirrored left hand 4.4 cm into the stock.
            EFT.Player.FirearmController firearmController = VRGlobals.firearmController;
            Transform muzzle = firearmController != null && firearmController.WeaponRoot == weaponRoot ? firearmController.CurrentFireport?.Original : null;
            if (muzzle != null && muzzle.IsChildOf(swayFrame))
            {
                planePoint = muzzle.position;
                // A side-by-side's current fireport is one barrel's (fireport_l / _r, 1.3 cm each side of the middle): use the midpoint
                var barrels = firearmController.Item != null && firearmController.Item.IsMultiBarrel ? firearmController.MultiBarrelsFireports : null;
                if (barrels != null && barrels.Length == 2 && barrels[0]?.Original != null && barrels[1]?.Original != null)
                    planePoint = (barrels[0].Original.position + barrels[1].Original.position) * 0.5f;
            }
            // For a grenade, put the plane through the grenade itself. Its Weapon_root_anim isn't centred on it (in the idle hold the right
            // grip is 11-12 cm off that plane and the grenade 2-3.5 cm), which would put the mirrored left hand ~6 cm off the grenade.
            if (GrenadeCentre(weaponRoot, out Vector3 centre))
                planePoint = centre;
            return true;
        }

        private static Transform grenadeCentreFor;
        private static Renderer grenadeBody;

        // The centre of the held grenade's body, taken from its biggest mesh (the ring and lever would pull a combined bounds off centre)
        internal static bool GrenadeCentre(Transform weaponRoot, out Vector3 centre)
        {
            centre = Vector3.zero;
            if (!(VRGlobals.player?.HandsController is EFT.Player.BaseGrenadeHandsController grenade) || grenade.WeaponRoot != weaponRoot
                || grenade._grenadeTransform == null)
                return false;
            if (grenadeCentreFor != grenade._grenadeTransform)
            {
                grenadeCentreFor = grenade._grenadeTransform;
                grenadeBody = null;
                float biggest = 0f;
                foreach (Renderer renderer in grenade._grenadeTransform.GetComponentsInChildren<Renderer>(true))
                {
                    Vector3 size = renderer.bounds.size;
                    float volume = size.x * size.y * size.z;
                    if (volume > biggest)
                    {
                        biggest = volume;
                        grenadeBody = renderer;
                    }
                }
            }
            centre = grenadeBody != null ? grenadeBody.bounds.center : grenade._grenadeTransform.position;
            return true;
        }

        internal static Vector3 ReflectPoint(Vector3 point, Vector3 planePoint, Vector3 planeNormal)
        {
            return point - 2f * Vector3.Dot(point - planePoint, planeNormal) * planeNormal;
        }
    }
}
