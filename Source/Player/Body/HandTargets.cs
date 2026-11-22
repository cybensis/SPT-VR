using RootMotion.FinalIK;
using TarkovVR.Source.Dev;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // Who an arm reaches for. Every system that takes a hand sets the arm IK's target through Set, which writes it at once (exactly
    // as a direct write would) and remembers which system did. Nothing is arbitrated: the last write before the IK solves still
    // wins, as before - but each hand's owner is known (OwnerOf, LeftOwner / RightOwner) and logChanges prints every handover.
    // ArmTargetBlend's swap inside one solve (the target lifted for the blend, put back after) is not a handover and stays raw.
    internal enum HandOwner
    {
        None,
        Rig,                // the rig's own setup: the hand on its controller
        SupportGrip,        // two-handing: the off hand on the foregrip (null target = Tarkov's support marker) or back on its controller
        Gun,                // the gun controller taking the hands at equip
        EmptyHands,         // going to empty hands
        HandsChange,        // leaving a hands controller
        TarkovAnimation,    // released to one of Tarkov's own hand animations (a reload / malfunction clip takes the left hand)
        Meds,               // a meds / food controller spawning
        Sling,              // ChestHang: slung / support-only gun, a freed gun hand
        Eating,
        LeftHandedGun,      // RealLeftHandedPatches: the gun in the left hand
        TwoHandedLoot,
        MountedGun,
        Door,
        DoorKey,
        Grenade,            // the off hand on the grenade's ring / pin
        ReloadLatch,        // a hand locked onto a gun part (VRReloadController's shared hand latch)
        ChargingHandle,     // the gun hand pinned on the charging handle
        ShotgunPump,
        BeltSupportSpot,    // PKM: the support hand on the handguard or the box
    }

    internal static class HandTargets
    {
        [Tunable("Loot & hands", TunableKind.Log, "A [HandTargets] line whenever a hand changes hands: the frame, which arm, and the system "
            + "and target before and after. Two systems fighting over a hand show as lines every frame.", Suggested = true)]
        public static bool logChanges = false;      // a line whenever an arm's owner or target changes: frame, arm, before -> after

        private static LimbIK leftArm, rightArm;
        private static HandOwner leftOwner, rightOwner;
        private static Transform leftTarget, rightTarget;

        [Tunable("Loot & hands", TunableKind.Status, "Which system the left arm reaches for right now (HandTargets).")]
        internal static HandOwner LeftOwner => leftOwner;
        [Tunable("Loot & hands", TunableKind.Status, "Which system the right arm reaches for right now (HandTargets).")]
        internal static HandOwner RightOwner => rightOwner;

        internal static void Set(LimbIK arm, Transform target, HandOwner owner)
        {
            arm.solver.target = target;
            Note(arm, target, owner);
        }

        internal static HandOwner OwnerOf(LimbIK arm)
        {
            if (arm != null && arm == leftArm)
                return leftOwner;
            if (arm != null && arm == rightArm)
                return rightOwner;
            return HandOwner.None;
        }

        private static void Note(LimbIK arm, Transform target, HandOwner owner)
        {
            // The rig's arms are Tarkov's own limbs (Player._limbs); a new raid / rig brings new ones
            bool left;
            if (VRGlobals.ikManager != null && arm == VRGlobals.ikManager.leftArmIk)
                left = true;
            else if (VRGlobals.ikManager != null && arm == VRGlobals.ikManager.rightArmIk)
                left = false;
            else if (VRGlobals.player != null && VRGlobals.player._limbs != null && VRGlobals.player._limbs.Length > 1)
            {
                if (arm == VRGlobals.player._limbs[0])
                    left = true;
                else if (arm == VRGlobals.player._limbs[1])
                    left = false;
                else
                    return;
            }
            else
                return;

            if (left)
            {
                if (arm != leftArm)
                {
                    leftArm = arm;
                    leftOwner = HandOwner.None;
                    leftTarget = null;
                }
                if (logChanges && (owner != leftOwner || target != leftTarget))
                    Log("left", leftOwner, leftTarget, owner, target);
                leftOwner = owner;
                leftTarget = target;
            }
            else
            {
                if (arm != rightArm)
                {
                    rightArm = arm;
                    rightOwner = HandOwner.None;
                    rightTarget = null;
                }
                if (logChanges && (owner != rightOwner || target != rightTarget))
                    Log("right", rightOwner, rightTarget, owner, target);
                rightOwner = owner;
                rightTarget = target;
            }
        }

        private static void Log(string side, HandOwner fromOwner, Transform fromTarget, HandOwner toOwner, Transform toTarget)
        {
            Plugin.MyLog.LogInfo($"[HandTargets] frame {Time.frameCount} {side} arm: {fromOwner} ({TargetName(fromTarget)}) -> {toOwner} ({TargetName(toTarget)})");
        }

        private static string TargetName(Transform target) => target == null ? "Tarkov's goal" : target.name;
    }
}
