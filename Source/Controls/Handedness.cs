using RootMotion.FinalIK;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Controls
{
    // Who is the gun hand and who is the off hand, in one place (the groundwork for a real left-handed mode, 2026-11-06).
    //  - The CONTROLLERS by role: the gun hand's controller is the right one, or the left one in left-handed mode. Its grip, trigger,
    //    stick, face buttons and haptics. These read the same in today's mirrored left-handed mode and in a real one.
    //  - The RIG hands by role: which of the rig's hands (and EFT arms) holds the gun. Today's left-handed mode keeps the gun on the
    //    rig's RIGHT hand and mirrors the arms, so the gun rig hand is always the right one; a real left-handed mode flips these.
    // New code should go through here rather than VRSettings.GetLeftHandedMode() ? left : right.
    public static class Handedness
    {
        public static bool LeftHanded => VRSettings.GetLeftHandedMode();

        // Today's left-handed mode: the arms + gun mirrored (scale -1), everything right-handed underneath. Untouched by the below.
        public static bool MirrorLeftHanded => LeftHanded;

        // The gun in the LEFT hand, right-handed mode (no mirroring anywhere - his call 2026-11-06, after the test built on the mirrored
        // mode stacked mirror on mirror): the left hand took it off the sling (ChestHang). The rig's gun hand follows the left controller
        // with the right-handed hold mirrored once (VRPlayerManager.MirroredGunHold), EFT's arms go onto the gun mirrored
        // (RealLeftHandedPatches), the fingers swap (HandMirror). Back to the sling = back to normal.
        public static bool GunInLeftHand { get; private set; }

        public static void SetGunInLeftHand(bool inLeftHand)
        {
            if (LeftHanded)
                inLeftHand = false;
            if (GunInLeftHand == inLeftHand)
                return;
            GunInLeftHand = inLeftHand;
            // The arms back as they were before anything frees or takes them next (the sling)
            if (!inLeftHand)
                TarkovVR.Patches.Core.Player.RealLeftHandedPatches.ReleaseArms();
            // Which controller drives the rig's gun hand
            if (VRGlobals.vrPlayer != null)
                VRGlobals.vrPlayer.RightHandedMode();
            Plugin.MyLog.LogInfo($"[Handedness] gun in the {(inLeftHand ? "LEFT" : "right")} hand");
        }

        // Which controller holds the gun: the left in left-handed mode, or while the gun is in the left hand
        public static bool GunHandIsLeft => LeftHanded || GunInLeftHand;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The controllers. Grip, trigger, face buttons, pose and haptics follow the gun; the sticks stay where the layout puts them
        // (movement / turning don't swap sides when the gun does).

        public static SteamVR_Input_Sources GunHandSource => GunHandIsLeft ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
        public static SteamVR_Input_Sources OffHandSource => GunHandIsLeft ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand;

        public static VRBoolean GunHandGrip => GunHandIsLeft ? VRInput.LeftGrip : VRInput.RightGrip;
        public static VRBoolean OffHandGrip => GunHandIsLeft ? VRInput.RightGrip : VRInput.LeftGrip;

        public static VRSingle GunHandTrigger => GunHandIsLeft ? VRInput.LeftTrigger : VRInput.RightTrigger;
        public static VRSingle OffHandTrigger => GunHandIsLeft ? VRInput.RightTrigger : VRInput.LeftTrigger;

        public static VRVector2 GunHandJoystick => LeftHanded ? VRInput.LeftJoystick : VRInput.RightJoystick;
        public static VRVector2 OffHandJoystick => LeftHanded ? VRInput.RightJoystick : VRInput.LeftJoystick;

        // Face buttons: lower = A / X, upper = B / Y (A and B are on the right controller, X and Y on the left)
        public static VRBoolean GunHandLowerButton => GunHandIsLeft ? VRInput.ButtonX : VRInput.ButtonA;
        public static VRBoolean GunHandUpperButton => GunHandIsLeft ? VRInput.ButtonY : VRInput.ButtonB;
        public static VRBoolean OffHandLowerButton => GunHandIsLeft ? VRInput.ButtonA : VRInput.ButtonX;
        public static VRBoolean OffHandUpperButton => GunHandIsLeft ? VRInput.ButtonB : VRInput.ButtonY;

        public static VRPose GunHandPose => GunHandIsLeft ? VRInput.LeftHandPose : VRInput.RightHandPose;
        public static VRPose OffHandPose => GunHandIsLeft ? VRInput.RightHandPose : VRInput.LeftHandPose;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The rig

        // Today always the rig's right hand (see the top): the left-handed mode mirrors instead of moving the gun
        public static bool GunOnRigLeft => false;

        public static Transform GunRigHand => RigHand(GunOnRigLeft);
        public static Transform OffRigHand => RigHand(!GunOnRigLeft);
        public static LimbIK GunArmIk => ArmIk(GunOnRigLeft);
        public static LimbIK OffArmIk => ArmIk(!GunOnRigLeft);

        private static Transform RigHand(bool left)
        {
            if (VRGlobals.vrPlayer == null)
                return null;
            GameObject hand = left ? VRGlobals.vrPlayer.LeftHand : VRGlobals.vrPlayer.RightHand;
            return hand != null ? hand.transform : null;
        }

        private static LimbIK ArmIk(bool left)
        {
            if (VRGlobals.ikManager == null)
                return null;
            return left ? VRGlobals.ikManager.leftArmIk : VRGlobals.ikManager.rightArmIk;
        }
    }
}
