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

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The controllers

        public static SteamVR_Input_Sources GunHandSource => LeftHanded ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
        public static SteamVR_Input_Sources OffHandSource => LeftHanded ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand;

        public static VRBoolean GunHandGrip => LeftHanded ? VRInput.LeftGrip : VRInput.RightGrip;
        public static VRBoolean OffHandGrip => LeftHanded ? VRInput.RightGrip : VRInput.LeftGrip;

        public static VRSingle GunHandTrigger => LeftHanded ? VRInput.LeftTrigger : VRInput.RightTrigger;
        public static VRSingle OffHandTrigger => LeftHanded ? VRInput.RightTrigger : VRInput.LeftTrigger;

        public static VRVector2 GunHandJoystick => LeftHanded ? VRInput.LeftJoystick : VRInput.RightJoystick;
        public static VRVector2 OffHandJoystick => LeftHanded ? VRInput.RightJoystick : VRInput.LeftJoystick;

        // Face buttons: lower = A / X, upper = B / Y (A and B are on the right controller, X and Y on the left)
        public static VRBoolean GunHandLowerButton => LeftHanded ? VRInput.ButtonX : VRInput.ButtonA;
        public static VRBoolean GunHandUpperButton => LeftHanded ? VRInput.ButtonY : VRInput.ButtonB;
        public static VRBoolean OffHandLowerButton => LeftHanded ? VRInput.ButtonA : VRInput.ButtonX;
        public static VRBoolean OffHandUpperButton => LeftHanded ? VRInput.ButtonB : VRInput.ButtonY;

        public static VRPose GunHandPose => LeftHanded ? VRInput.LeftHandPose : VRInput.RightHandPose;
        public static VRPose OffHandPose => LeftHanded ? VRInput.RightHandPose : VRInput.LeftHandPose;

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
