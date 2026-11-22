using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // Eases sudden changes in where the off hand's arm reaches, so the hand doesn't flicker as it connects to or lets go of something.
    // Whenever the left arm IK's target changes - to another transform (the controller, a reload part's latch, our point on a pump) or
    // to none (Tarkov's own goal: the foregrip, a reload clip's hand) - the goal moves from where it was to the new one over
    // switchBlendTime, eased in and out. During the blend it is carried in the body's frame, so walking doesn't drag it behind.
    // A target that moves on its own is never touched.
    // It hooks the arm's own solver, so every target switch goes through it. OnPreUpdate runs before the solver reads its target
    // (FinalIK IKSolverTrigonometric.OnUpdate copies target into IKPosition) and puts the blended goal in its place; OnPostUpdate
    // restores the target. Off while the gun is in the left hand: that arm holds the gun then, and its grip has to stay on it.
    internal static class ArmTargetBlend
    {
        public static bool enabled = true;
        public static float switchBlendTime = 0.14f;        // s

        private static LimbIK hookedArm;
        private static bool hasLast;
        private static Transform lastSource;
        private static Vector3 lastGoalInBody;
        private static Quaternion lastGoalRotationInBody = Quaternion.identity;
        private static Vector3 fromInBody;
        private static Quaternion fromRotationInBody = Quaternion.identity;
        private static float blendStartTime = -1f;
        private static int lastSwitchFrame = -10;
        private static Transform heldTarget;
        private static bool targetHeld;

        // Called every tick from VRReloadController.Tick. Moves the hooks onto the left arm the rig currently has.
        public static void EnsureHooked()
        {
            LimbIK arm = VRGlobals.ikManager != null ? VRGlobals.ikManager.leftArmIk : null;
            if (arm == hookedArm)
                return;
            if (hookedArm != null && hookedArm.solver != null)
            {
                hookedArm.solver.OnPreUpdate -= BeforeSolve;
                hookedArm.solver.OnPostUpdate -= AfterSolve;
            }
            hookedArm = arm;
            hasLast = false;
            blendStartTime = -1f;
            targetHeld = false;
            heldTarget = null;
            if (arm != null && arm.solver != null)
            {
                arm.solver.OnPreUpdate += BeforeSolve;
                arm.solver.OnPostUpdate += AfterSolve;
            }
        }

        // The parent of the arm's root (the spine). The goal is carried in this frame during a blend.
        private static Transform Body => hookedArm != null && hookedArm.transform.parent != null ? hookedArm.transform.parent : null;

        private static void BeforeSolve()
        {
            if (hookedArm == null || hookedArm.solver == null)
                return;
            IKSolverLimb solver = hookedArm.solver;
            Transform body = Body;
            Transform source = solver.target;
            Vector3 goal = source != null ? source.position : solver.IKPosition;
            Quaternion goalRotation = source != null ? source.rotation : solver.IKRotation;
            if (body == null)
                return;
            // A solver that isn't initiated yet skips its post-update, which is what restores the target.
            // Eating pins the wrist onto its own targets after the IK (EatingInteractionController.PinLeftAfterIk), so a blended arm
            // would be stretched by the pinned wrist.
            if (!enabled || Handedness.GunInLeftHand || switchBlendTime <= 0f || !solver.initiated
                || TarkovVR.Source.Player.Interactions.EatingInteractionController.ManualActive)
            {
                blendStartTime = -1f;
                Remember(body, source, goal, goalRotation);
                return;
            }
            if (hasLast && source != lastSource)
            {
                // Start from where the arm was reaching (mid-blend, where the blend had it). When two things switch the target back and
                // forth every frame, the running blend continues instead of restarting, so it can't stall.
                if (blendStartTime < 0f || Time.frameCount - lastSwitchFrame > 2)
                {
                    fromInBody = lastGoalInBody;
                    fromRotationInBody = lastGoalRotationInBody;
                    blendStartTime = Time.time;
                }
                lastSwitchFrame = Time.frameCount;
            }
            if (blendStartTime >= 0f)
            {
                float progress = (Time.time - blendStartTime) / switchBlendTime;
                if (progress >= 1f)
                    blendStartTime = -1f;
                else
                {
                    float blend = Mathf.SmoothStep(0f, 1f, progress);
                    goal = Vector3.Lerp(body.TransformPoint(fromInBody), goal, blend);
                    goalRotation = Quaternion.Slerp(body.rotation * fromRotationInBody, goalRotation, blend);
                    heldTarget = source;
                    targetHeld = true;
                    // lifted for this solve and put back after it: not a handover, so not through HandTargets
                    solver.target = null;
                    solver.IKPosition = goal;
                    solver.IKRotation = goalRotation;
                }
            }
            Remember(body, source, goal, goalRotation);
        }

        private static void Remember(Transform body, Transform source, Vector3 goal, Quaternion goalRotation)
        {
            lastSource = source;
            lastGoalInBody = body.InverseTransformPoint(goal);
            lastGoalRotationInBody = Quaternion.Inverse(body.rotation) * goalRotation;
            hasLast = true;
        }

        private static void AfterSolve()
        {
            if (targetHeld && hookedArm != null && hookedArm.solver != null)
                hookedArm.solver.target = heldTarget;
            targetHeld = false;
            heldTarget = null;
        }
    }
}
