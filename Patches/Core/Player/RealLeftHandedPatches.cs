using HarmonyLib;
using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Body;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;

namespace TarkovVR.Patches.Core.Player
{
    // The gun in the left hand (Handedness.GunInLeftHand: right-handed mode, the left hand took it off the sling). The gun sits on the rig's
    // gun hand as always (driven by the left controller, the hold mirrored once by VRPlayerManager.MirroredGunHold). EFT aims its right arm at
    // the grip and its left at the support (Player.IkProcess sets both, IkApply solves them); this runs first among IkProcess's postfixes
    // (the elbow bend normals read the targets after it):
    //  - left arm: the right hand's grip pose mirrored across the gun's centre plane (HandMirror.TryMirrorPalm)
    //  - right arm on the foregrip: the left hand's foregrip pose mirrored
    //  - right arm off the gun: VRPlayerManager.RightControllerHand, a rig child placed in the right controller's pose callback
    //  - the right hand holding the gun alone (SupportHeld): right arm on the foregrip as above, left arm free on LeftControllerHand
    // Hands on the gun are solved by EFT's arm pass alone. A free hand is also solved by its own IK update onto its rig child: as a world
    // point for EFT's pass alone it jitters. Centre plane = Weapon_root x = 0 (bore along -y, sights +z, hand markers at x = +3 / -4 cm).
    // The mod's targets on the gun hands (solver.target wins over IKPosition) are taken off for the solve and put back after IkApply.
    // Fingers: HandMirror.CaptureGunHandGrip / ApplyRealLeftHanded (HandsInteractionController.LateUpdate).
    [HarmonyPatch]
    internal static class RealLeftHandedPatches
    {
        // Targets taken off for this frame's solve, put back after IkApply
        private static bool leftSwapped, rightSwapped;
        private static Transform leftTargetBefore, rightTargetBefore;
        // The free right hand's own target, left on the arm between frames (its own update solves it in LateUpdate); what was there before
        private static bool freeRightOn;
        private static Transform freeRightTarget, rightTargetOriginal;
        // The limbs' own update, set while the gun is in the left hand, put back as it was when that ends (left switched off, the left
        // hand jitters / lags after handing the gun back)
        private static bool limbUpdatesOff;
        private static bool leftEnabledBefore, rightEnabledBefore;
        private static Transform swayFrameFor, swayFrame;
        // The free left hand's own target while the right hand holds the gun alone (SupportHeld), like the free right hand's
        private static bool freeLeftOn;
        private static Transform freeLeftTarget, leftTargetOriginal;

        // A gun, or a grenade the left hand took off the chest (GrenadeSlot): both held on the rig's gun hand, under weaponHolder
        internal static bool Active => Handedness.GunInLeftHand && VRGlobals.vrPlayer != null && HeldWeaponRoot() != null && !ChestHang.Detached;

        internal static Transform HeldWeaponRoot()
        {
            // The grenade first: firearmController still names the last gun (a slung one included) while a grenade is in the hands
            if (Source.Player.Interactions.GrenadeSlot.KeepsLeftHand && VRGlobals.player?.HandsController is EFT.Player.BaseGrenadeHandsController grenade)
                return grenade.WeaponRoot;
            if (VRGlobals.firearmController != null && VRGlobals.firearmController.WeaponRoot != null)
                return VRGlobals.firearmController.WeaponRoot;
            return null;
        }

        // The gun in the left hand, let go by the left with the right on the foregrip: the right hand holds it alone (ChestHang.SupportOnly,
        // the gun riding the right controller). The right arm stays on the foregrip, the left is free on VRPlayerManager.LeftControllerHand.
        internal static bool SupportHeld => Handedness.GunInLeftHand && ChestHang.SupportOnly && VRGlobals.vrPlayer != null
            && VRGlobals.firearmController != null && VRGlobals.firearmController.WeaponRoot != null;

        // Both arms back as they were. Handedness.SetGunInLeftHand(false) calls it straight away, so whatever frees or takes the arms
        // next (the sling's KeepGunHandFree) starts from the arms as they were; SwapArms also calls it the first frame it isn't Active.
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

        // The mirrored grenade's last left-hand target (GrenadeHold's hold line): how far EFT's right target was off the grip marker, the
        // right arm's weight then, the target itself
        internal static float GrenadeIkGap, GrenadeRightWeight, GrenadeRotationGap;
        internal static Vector3 GrenadeLeftTarget;
        internal static Quaternion GrenadeLeftTargetRotation = Quaternion.identity;

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

            // What EFT aimed each hand at: the right at the gun's grip, the left at the support (its own target, when the mod set one).
            // IkProcess just wrote IKPosition from the markers, also on the right while the free target is on it. Held by the right hand
            // alone, the left's target is its own free one (or a reload latch): the support is the gun's left marker EFT aimed it at.
            Vector3 gripPosition = right.IKPosition;
            Quaternion gripRotation = right.IKRotation;
            bool supportFromTarget = !supportHeld && left.target != null;
            Vector3 supportPosition = supportFromTarget ? left.target.position : left.IKPosition;
            Quaternion supportRotation = supportFromTarget ? left.target.rotation : left.IKRotation;

            // The plane rides EFT's sway / inertia, which moves Weapon_root_anim (PlayerBones.ShiftWeaponRoot / Kinematics) under
            // Weapon_root, gun mesh and markers with it: a plane on Weapon_root would mirror the hand the opposite way to the sway.
            // At rest Weapon_root_anim sits on Weapon_root (zero offset, zero turn), so its x is the same centre plane.
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
                // A grenade mirrored whole (its positioner scaled -1 on x): the grip marker is on the left grip already; Unity reports
                // its rotation without the scale's sign, so the palm is mirrored through the positioner's x. Read off the marker itself,
                // not the right arm's IK target: EFT only writes that while the right arm's weight (1 - the clip's RIGHT_HAND_WEIGHT,
                // VisualPass) isn't 0, else it keeps the free right hand's
                Transform gripMarker = __instance._markers != null && __instance._markers.Length > 1 ? __instance._markers[1] : null;
                Vector3 grip = gripMarker != null ? gripMarker.position : gripPosition;
                Quaternion gripTurn = gripMarker != null ? gripMarker.rotation : gripRotation;
                GrenadeIkGap = Vector3.Distance(gripPosition, grip);
                GrenadeRightWeight = right.IKPositionWeight;
                // The palm off the grip marker's own matrix (a mirrored frame: the -1 is in it), which is right wherever the scale's
                // sign lands; the rotation-based mirror is only the fallback
                if (gripMarker == null || !HandMirror.TryOtherPalmFromMirroredFrame(gripMarker.localToWorldMatrix, rightToLeft: true, out leftRotation))
                {
                    if (!HandMirror.TryMirrorPalm(gripTurn, mirrorNormal, rightToLeft: true, out leftRotation))
                        return;
                }
                GrenadeRotationGap = HandMirror.TryMirrorPalm(gripTurn, mirrorNormal, rightToLeft: true, out Quaternion byRotation)
                    ? Quaternion.Angle(byRotation, leftRotation) : -1f;
                leftPosition = grip;
                GrenadeLeftTarget = leftPosition;
                GrenadeLeftTargetRotation = leftRotation;
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
                // Left arm, off the gun: its own target, also for its own update later this frame (the free right hand's way). On a
                // bolt, handle or round (the gun hand's reload latch, on this arm then) the latch.
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
                // Left arm, on the gun: EFT solves it by hand (IkApply); the component's own update would solve it again onto the mod's target
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
                // On the foregrip: as the left arm
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
                // Off the gun: its own target, also for its own update later this frame. The hand on a magazine, a handle or a bolt
                // (VRReloadController's latch, the off hand's on this arm then) keeps the latch as its target; on the scope's zoom dial,
                // the dial grip (VROpticController.RightHandDialTarget).
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

        // The free left hand's target off, back to what was there (only if it's still ours: a reload latch is VRReloadController's)
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
            // Through the bore: on every gun but one that's Weapon_root_anim's own x = 0 plane, but the VPO-101's model sits 2.2 cm off it
            // (its bolt, stock, magazine and muzzle), which put the mirrored left hand 4.4 cm into the stock
            Transform muzzle = VRGlobals.firearmController != null && VRGlobals.firearmController.WeaponRoot == weaponRoot
                ? VRGlobals.firearmController.CurrentFireport?.Original : null;
            if (muzzle != null && muzzle.IsChildOf(swayFrame))
                planePoint = muzzle.position;
            // A grenade: through the grenade itself. Its Weapon_root_anim isn't on it (idle hold: the right grip 11-12 cm off that plane,
            // the grenade 2-3.5 cm), which would put the mirrored left hand ~6 cm off the grenade
            if (GrenadeCentre(weaponRoot, out Vector3 centre))
                planePoint = centre;
            return true;
        }

        private static Transform grenadeCentreFor;
        private static Renderer grenadeBody;

        // The middle of the held grenade's body (its biggest mesh: the ring and lever would pull a bounds of all of them off it)
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
