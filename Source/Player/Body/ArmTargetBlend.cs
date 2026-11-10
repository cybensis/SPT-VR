using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using UnityEngine;

namespace TarkovVR.Source.Player.Body
{
    // A jump in where the off hand's arm reaches, eased (else the hand flickers as it connects or disconnects). Whenever what the left
    // arm's IK aims at changes - another transform (the controller, a reload part's latch, our point on a pump) or none (Tarkov's own
    // goal: the foregrip it two-hands on, a reload clip's hand) - its goal goes from where it was reaching to the new one over
    // switchBlendTime, eased in and out, carried in the body's frame meanwhile so walking doesn't drag it. One source moving on its
    // own is never touched.
    // Hooked on the arm's own solver, so every switch anywhere goes through it: OnPreUpdate, before the solver reads its target (FinalIK
    // IKSolverTrigonometric.OnUpdate: target -> IKPosition), stands the blended goal in for it; OnPostUpdate puts the target back.
    // Off with the gun in the left hand: that arm is the gun's then, its grip has to stay on the gun.
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

        // Every tick (VRReloadController.Tick): on the arm the rig has now
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

        // The arm's root's parent (the spine): the body the goal is carried with during a blend
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
            // Not initiated yet: the solver skips its post-update, which is what puts the target back
            // Eating pins the wrist onto its own targets after the IK (EatingInteractionController.PinLeftAfterIk): a blended arm under a pinned
            // wrist would stretch it
            if (!enabled || Handedness.GunInLeftHand || switchBlendTime <= 0f || !solver.initiated
                || TarkovVR.Source.Player.Interactions.EatingInteractionController.ManualActive)
            {
                blendStartTime = -1f;
                Remember(body, source, goal, goalRotation);
                return;
            }
            if (hasLast && source != lastSource)
            {
                // From where it was reaching (mid-blend: where the blend had it). Switching back and forth every frame (two things setting
                // the target) carries on the same blend instead of starting over, so it can't stall.
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
