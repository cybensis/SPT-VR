using EFT;
using HarmonyLib;
using System;
using UnityEngine;
using TarkovVR.Source.Settings;
using static EFT.Player;
using EFT.Animations;
using EFT.ItemInHandSubsystem;
using EFT.UI.Ragfair;
using RootMotion.FinalIK;
using Valve.VR;
using EFT.InputSystem;
using AnimationSystem;
using TarkovVR.Source.Controls;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class IKPatches
    {
        /*
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "MouseLook")]
        private static void StabilizeGunHeight(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || __instance.UsedSimplifiedSkeleton ||
                VRGlobals.VRCam == null || __instance.HandsController?.ControllerGameObject == null)
                return;

            var t = __instance.HandsController.ControllerGameObject.transform;
            Vector3 p = t.position;
            p.y = VRGlobals.VRCam.transform.position.y - 0.4f; // tune offset
            t.position = p;
        }
        */
        // VR body-stance vertical smoothing (continuous low-pass on the BODY MODEL only).
        // The body's vertical stance STEPS ~15cm at idle<->walk and at joystick-crouch start/stop; under the
        // fixed VR headset that step makes the torso/arms bounce up on move-start and down on move-stop. We
        // low-pass the stance and offset PlayerBones.AnimatedTransform (the rendered body model only - the VR
        // camera rig and the controller-pinned gun are separate), so the step is eased. A continuous low-pass
        // IS a transient absorber: its offset only grows while the stance is CHANGING and decays to ~0 once
        // the stance is steady, so steady state is ~1:1. (It also low-passes the body model's own torso bob,
        // but that is HARMLESS - the WEAPON bob comes from EFT's WalkEffector, a separate system, NOT from
        // this; an earlier note wrongly blamed this for "muting the bob" - it was WalkEffector being off.)
        // bodyStanceSmoothTime = how gently the step eases; 0 = absorber off (raw snap).
        public static float bodyStanceSmoothTime = 0.12f;
        // Physical head crouch/peek should stay 1:1. When the real headset is moving fast
        // vertically we drop to a tiny smooth time so this layer doesn't lag your physical crouch. Joystick
        // crouch/locomotion doesn't move VRCam.localPosition.y, so only REAL head motion trips this.
        public static float physMoveBypassSpeed = 0.12f;
        private const float BODY_STANCE_CLAMP = 0.2f;      // matches EFT's own clamp
        private static float bodyStanceVel = 0f;
        private static float smoothedStanceY = float.NaN;
        private static float prevCamLocalY = float.NaN;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "HeightInterpolation")]
        private static bool DisableYAxisSmoothing(EFT.Player __instance, float timeDeltatime)
        {
            if (!__instance.IsYourPlayer)
                return true; // bots / other players keep vanilla smoothing
            else if (VRGlobals.player.MovementContext._poseLevel == 0f)
            {
                // PRONE: both vanilla height interpolation and our easing are suppressed here - but a
                // mid-glide offset left on AnimatedTransform (entering prone from a crouch glide) used
                // to FREEZE on the body for the whole prone (nobody wrote it again): the body/gun sat
                // displaced up to the clamp = the long-standing "weapon instability while prone".
                // Clear it and reset the easing state so stand-up re-seeds cleanly.
                smoothedStanceY = float.NaN;
                var atProne = __instance.PlayerBones?.AnimatedTransform;
                if (atProne != null && atProne.Original != null)
                {
                    Vector3 lpProne = atProne.localPosition;
                    if (lpProne.y != 0f)
                        atProne.localPosition = new Vector3(lpProne.x, 0f, lpProne.z);
                }
                return false;
            }

            var bones = __instance.PlayerBones;
            var at = bones?.AnimatedTransform; // BifacialTransform (same one EFT's HeightInterpolation writes)

            // Disabled or not ready -> no body offset (raw snap)
            if (bodyStanceSmoothTime <= 0f || at == null || at.Original == null || bones.Ribcage?.Original == null || Mathf.Approximately(timeDeltatime, 0f))
            {
                smoothedStanceY = float.NaN;
                if (at != null)
                {
                    Vector3 lp0 = at.localPosition;
                    if (lp0.y != 0f)
                        at.localPosition = new Vector3(lp0.x, 0f, lp0.z);
                }
                return false;
            }

            // Stance height = ribcage world Y minus the body root world Y; both share our offset and global
            // movement, so this is purely the animator's stance + gait bob (feedback-free, no world motion).
            float stanceY = bones.Ribcage.Original.position.y - at.Original.position.y;
            if (float.IsNaN(smoothedStanceY))
                smoothedStanceY = stanceY;

            // Teleport guard (respawn, vault): snap rather than glide the clamp over the smooth time.
            if (Mathf.Abs(stanceY - smoothedStanceY) > BODY_STANCE_CLAMP * 4f)
                smoothedStanceY = stanceY;

            // Keep physical crouch 1:1: when the real headset is moving fast vertically, drop to a tiny smooth
            // time so this layer doesn't lag your physical crouch (variable time constant, no branch/stutter).
            float camLocalY = VRGlobals.VRCam != null ? VRGlobals.VRCam.transform.localPosition.y : 0f;
            float camSpeed = float.IsNaN(prevCamLocalY) ? 0f : Mathf.Abs(camLocalY - prevCamLocalY) / timeDeltatime;
            prevCamLocalY = camLocalY;
            float smoothTime = camSpeed > physMoveBypassSpeed ? 0.04f : bodyStanceSmoothTime;

            // Rendered body Y = stanceY + offset, and we want that to track the low-passed value, so
            // offset = smoothedStanceY - stanceY. Eases the step as one glide; decays to ~0 when steady.
            smoothedStanceY = Mathf.SmoothDamp(smoothedStanceY, stanceY, ref bodyStanceVel, smoothTime, Mathf.Infinity, timeDeltatime);

            float offset = Mathf.Clamp(smoothedStanceY - stanceY, -BODY_STANCE_CLAMP, BODY_STANCE_CLAMP);
            Vector3 lp = at.localPosition;
            at.localPosition = new Vector3(lp.x, offset, lp.z);

            return false;
        }

        // VR body crouch tracks your head 1:1 (no lag). EFT eases SmoothedPoseLevel toward PoseLevel_1 each
        // frame (MovementContext.SmoothPoseLevel, a Lerp at POSE_CHANGING_SPEED), and SmoothedPoseLevel is what
        // drives the body's crouch animation. The mod sets PoseLevel_1 to follow your head 1:1, but this
        // smoothing makes the BODY (and the arms hanging off it) trail your 1:1 view/controllers when you
        // crouch up/down - so the arms lag the view and JITTER during the catch-up (the [CROUCH] logs showed
        // the body/IK lagging the smooth controller target by a dt-dependent amount). For our player we snap
        // SmoothedPoseLevel straight to the target so the body crouches exactly with your head. The view rides
        // the rig and was already crisp; this brings the body up to match. false = vanilla smoothing.
        // NOTE: skips the original's stance-change stamina drain (ConsumePoseLevelChange) - restore if kept.
        public static bool bodyPose1to1 = true;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MovementContext), nameof(MovementContext.SmoothPoseLevel))]
        private static bool InstantBodyPoseLevel(MovementContext __instance)
        {
            if (!bodyPose1to1 || __instance._player == null || !__instance._player.IsYourPlayer)
                return true; // bots / other players / disabled -> vanilla smoothing

            __instance.SmoothedPoseLevel = __instance._poseLevel;
            return false; // skip the per-frame Lerp -> body is 1:1 with the head
        }

        //This disables the gun shifting closer to the camera when aiming down sights on certains guns, specifically ones without a stock
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProceduralWeaponAnimation), "CheckShouldMoveWeaponCloser")]
        private static bool DisableGunShift(ProceduralWeaponAnimation __instance)
        {
            return false;

        }
        [HarmonyPrefix]
        [HarmonyPatch(typeof(WalkEffector), "Process")]
        private static bool DisableWalkEffector(WalkEffector __instance, float deltaTime)
        {
            // Return value = whether the original runs. Toggle in VR settings ("Turn On EFT Weapon Walk
            // Effector"): on -> let it run (weapon walk bob), off -> skip it (default; steadier weapon).
            return VRSettings.GetWalkEffectorOn();
        }

        // Physical weapon collision replaces EFT's turn-away POSE: when the barrel overlaps geometry,
        // vanilla eases the gun through canned animation curves (Position/Rotation/elbow/camera shifts) -
        // in VR that reads as the game steering your gun away from where you point it. Zero the effector's
        // pose outputs for the local player AFTER Process so nothing consumes them, while OverlapValue
        // keeps its vanilla dynamics (the aim-stop/resume and blindfire gating read it). WeaponCollision
        // supplies the actual response (gun pressed against the surface). Bots/other players keep vanilla.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TurnAwayEffector), "Process")]
        private static void DisableTurnAwayPose(TurnAwayEffector __instance)
        {
            if (!TarkovVR.Source.Player.Interactions.WeaponCollision.enabled || VRGlobals.player == null ||
                !ReferenceEquals(VRGlobals.player.ProceduralWeaponAnimation?.TurnAway, __instance))
                return;
            __instance._position = Vector3.zero;
            __instance._rotation = Vector3.zero;
            __instance._cameraShift = Vector3.zero;
            __instance._lElbowShift = Vector3.zero;
            __instance._rElbowShift = Vector3.zero;
            __instance._cameraZShift = 0f;
        }

        // Consumption-side kill for the same pose: AvoidObstacles (Player.VisualPass, LateUpdate - AFTER
        // all the mod's Update-phase writes) applies TurnAway.Position/Rotation to WeaponRootAnim, whose
        // locals ride PlayerBones.Offset/DeltaRotation into ShiftWeaponRoot and move the weapon-root bone
        // chain _markers[1] hangs off - i.e. the very marker the VR gun is anchored to. The apply is gated
        // on TurnAway.IsDirty, so clear it around the call for the local player (restored after, in case
        // anything else reads it). Belt to DisableTurnAwayPose's suspenders.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ProceduralWeaponAnimation), "AvoidObstacles")]
        private static void SuppressTurnAwayApply(ProceduralWeaponAnimation __instance, ref bool __state)
        {
            __state = false;
            if (!TarkovVR.Source.Player.Interactions.WeaponCollision.enabled || VRGlobals.player == null ||
                !ReferenceEquals(VRGlobals.player.ProceduralWeaponAnimation, __instance))
                return;
            __state = __instance.TurnAway.IsDirty;
            __instance.TurnAway.IsDirty = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ProceduralWeaponAnimation), "AvoidObstacles")]
        private static void RestoreTurnAwayDirty(ProceduralWeaponAnimation __instance, bool __state)
        {
            if (__state)
                __instance.TurnAway.IsDirty = true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerBones), "SetShoulders")]
        private static bool OverrideShoulders(PlayerBones __instance)
        {
            if (__instance.Player == null || !__instance.Player.IsYourPlayer ||
                VRGlobals.vrPlayer == null || VRGlobals.ikManager == null ||
                Handedness.MirrorLeftHanded)
                return true;

            // Base shoulder dimensions
            const float ShoulderWidth = 0.13f;
            const float ShoulderHeight = -0.18f;
            const float ShoulderDepth = -0.10f;
            const float NeckLength = 0.15f;
            const float STANDING_BASELINE = 1.7f;
            const float SITTING_BASELINE = 1.25f;

            // Height-based scaling
            float baselineHeight = VRSettings.GetSeatedMode() ? SITTING_BASELINE : STANDING_BASELINE;
            float heightRatio = VRGlobals.vrPlayer.initPos.y / baselineHeight;
            // The arm itself is the right length now (ArmLength), so the shoulders stay put
            float armLengthOffset = TarkovVR.Source.Player.VR.ArmLength.enabled ? 0f : (heightRatio - 1f) * 0.25f;

            // State-based offsets
            bool isSprinting = __instance.Player.IsSprintEnabled;
            bool isAiming = VRGlobals.firearmController?.IsAiming ?? false;
            bool isUsingItem = VRGlobals.usingItem;

            bool sprintAnimEnabled = isSprinting && !VRSettings.GetDisableRunAnim();
            if (sprintAnimEnabled) return true;

            float aimOffset = isAiming ? -0.02f : 0.02f;
            // isSprinting here ALWAYS implies the run anim is disabled (anim-enabled sprints hit the
            // sprintAnimEnabled early-return above and go vanilla). The old hardcoded 0.10 shoved the
            // shoulders forward/out while sprinting - that's the "shoulders move forward" - so it's now a
            // tunable that defaults to 0 (no push; shoulders stay at the standing head-tracked spot).
            float sprintOffset = isSprinting ? sprintShoulderForward : 0f;
            float usingItemOffset = isUsingItem ? -0.10f : 0f;

            // Calculate neck base position
            Vector3 headPos = VRGlobals.VRCam.transform.position;
            Vector3 headForward = VRGlobals.VRCam.transform.forward;
            Vector3 headForwardFlat = new Vector3(headForward.x, 0f, headForward.z).normalized;

            float headPitch = VRGlobals.VRCam.transform.eulerAngles.x * Mathf.Deg2Rad;
            Vector3 neckBase = headPos - (headForwardFlat * NeckLength * Mathf.Sin(headPitch));
            neckBase.y = headPos.y;

            // Body-relative directions. The shoulders normally yaw with the BODY (Player.Transform.y).
            // That's wrong in JoyStickOnly turning: the body faces your movement/stick direction, so when
            // you turn your head to aim, the body (and the shoulders pinned to it) stay put while your
            // hands/gun go to the head/sight line -> the arm reaches across from a mis-yawed shoulder and
            // the IK twists (GetBodyRotation in MovementPatches only snaps the body to the head past a
            // 50-80 deg threshold, so under that it never lines up). Fix: yaw the shoulders toward the
            // HEAD while aiming so the arm ROOT squares up with where you're looking, independent of body
            // facing. No-op when head~=body (head-based mode), and eased so ADS doesn't pop the shoulders.
            float bodyYaw = __instance.Player.Transform.eulerAngles.y;
            float shoulderYaw = bodyYaw;
            if (shoulderFollowHeadYaw && VRGlobals.VRCam != null)
            {
                bool wantFollow = !shoulderFollowHeadYawOnlyWhenAiming || (VRGlobals.firearmController?.IsAiming ?? false);
                float targetFollow = wantFollow ? Mathf.Clamp01(shoulderHeadYawFollowAmount) : 0f;
                shoulderFollowLerp = Mathf.MoveTowards(shoulderFollowLerp, targetFollow,
                    Time.deltaTime / Mathf.Max(0.0001f, shoulderFollowEaseTime));
                if (shoulderFollowLerp > 0f)
                {
                    // Head yaw with the same upside-down guard GetBodyRotation uses (avoids a 180 flip when
                    // you look straight up/down and the camera's up crosses world-down).
                    float upDot = Vector3.Dot(VRGlobals.VRCam.transform.up, Vector3.up);
                    float headYaw = (upDot < 0f) ? (VRGlobals.VRCam.transform.eulerAngles.y - 180f)
                                                 : VRGlobals.VRCam.transform.eulerAngles.y;
                    shoulderYaw = bodyYaw + Mathf.DeltaAngle(bodyYaw, headYaw) * shoulderFollowLerp;
                }
            }
            // Export for DriveElbowBendNormals so the elbow reference frame turns with the head-follow.
            lastShoulderYaw = shoulderYaw;
            shoulderYawFrame = Time.frameCount;

            // Rotate the collarbone rotation by the same yaw delta so the arm's parent frame (and the
            // shoulder mesh) stay consistent with the shifted shoulder positions.
            Quaternion shoulderYawDelta = Quaternion.Euler(0f, shoulderYaw - bodyYaw, 0f);
            Quaternion yawRotation = Quaternion.Euler(0f, shoulderYaw, 0f);
            Vector3 right = yawRotation * Vector3.right;
            Vector3 forward = yawRotation * Vector3.forward;

            // Calculate shoulder offsets
            float leftLateral = -ShoulderWidth - sprintOffset;
            float leftDepth = ShoulderDepth + sprintOffset + armLengthOffset; //+ usingItemOffset;

            float rightLateral = ShoulderWidth + sprintOffset;
            float rightDepth = ShoulderDepth + aimOffset + sprintOffset + armLengthOffset; //+ usingItemOffset;

            Vector3 leftOffset = right * leftLateral + Vector3.up * ShoulderHeight + forward * leftDepth;
            Vector3 rightOffset = right * rightLateral + Vector3.up * ShoulderHeight + forward * rightDepth;

            // Apply positions
            Vector3 leftTarget = neckBase + leftOffset;
            Vector3 rightTarget = neckBase + rightOffset;

            // Shoulder ROTATION normally comes straight from the animated Shoulders_Anim. While sprinting
            // with the run anim disabled that rotation PUMPS with the (still-running, base-layer) sprint
            // gait, swinging the arm root -> the elbows flare even though the hands are pinned. Low-pass the
            // rotation ONLY during that sprint (smoothTime 0 otherwise = exact 1:1, so standing/aiming is
            // untouched); the smoothed value tracks continuously so there's no pop entering the sprint.
            Quaternion leftAnimRot = __instance.Shoulders_Anim[0].rotation;
            Quaternion rightAnimRot = __instance.Shoulders_Anim[1].rotation;
            float rotSmoothTime = (steadyShouldersDuringSprint && isSprinting) ? sprintShoulderRotSmoothTime : 0f;
            if (!shoulderRotInit || rotSmoothTime <= 0f)
            {
                smoothedShoulderRotL = leftAnimRot;
                smoothedShoulderRotR = rightAnimRot;
                shoulderRotInit = true;
            }
            else
            {
                float rt = Mathf.Clamp01(Time.deltaTime / rotSmoothTime);
                smoothedShoulderRotL = Quaternion.Slerp(smoothedShoulderRotL, leftAnimRot, rt);
                smoothedShoulderRotR = Quaternion.Slerp(smoothedShoulderRotR, rightAnimRot, rt);
            }

            TransformTools.LerpPositionAndRotation(
                __instance.Shoulders[0],
                leftTarget,
                shoulderYawDelta * smoothedShoulderRotL,
                0.65f);

            TransformTools.LerpPositionAndRotation(
                __instance.Shoulders[1],
                rightTarget,
                shoulderYawDelta * smoothedShoulderRotR,
                0.65f);

            // The 0.65 lerp leaves 35% of the shoulder on the body crouch ANIMATION, which hunches the
            // shoulders ~10cm lower than the head drops. Measured: head/controller/both hands all drop by the
            // SAME ~0.27 when crouching, but the shoulders drop ~0.30-0.32 - the extra ~3-5cm sinks the arm
            // root past the hands, tilting the elbow/forearm = the "funky IK when crouched." We pin the
            // shoulder HEIGHT to the head-tracked target (Y only; X/Z keep the animation blend) so the arm
            // root drops with the hands and the shoulder->hand geometry stays constant through a crouch.
            // false = old 0.65-blended Y (shoulders over-drop when crouched).
            if (shoulderHeightFromHead)
            {
                Vector3 ls = __instance.Shoulders[0].position; ls.y = leftTarget.y; __instance.Shoulders[0].position = ls;
                Vector3 rs = __instance.Shoulders[1].position; rs.y = rightTarget.y; __instance.Shoulders[1].position = rs;
            }

            return false;
        }
        // Drive the shoulder HEIGHT purely from the head instead of 65%-blending it with the crouch
        // animation, which over-drops the shoulders ~3-5cm when crouched and distorts the arm IK. See use site.
        public static bool shoulderHeightFromHead = true;

        // --- Shoulder yaw follows the head (JoyStickOnly-turning aim fix) ---------------------------
        // With JoyStickOnly turning the body faces your movement/stick direction, not your head, so a
        // head-turned aim leaves the shoulders mis-yawed and the arm IK twists. Yaw the shoulders toward
        // the head so the arm root squares up with the sight line. Default: only while AIMING (that's when
        // the hands are at the head/sight line; when hip-holding/empty the hands sit at body yaw and
        // following the head would instead PULL the shoulders off the hands as you glance around). Set
        // OnlyWhenAiming=false to also square the shoulders to your look direction while holding a weapon.
        // Amount 0..1 (1 = shoulders fully at head yaw). No-op in head-based turning (head~=body).
        public static bool shoulderFollowHeadYaw = true;
        public static bool shoulderFollowHeadYawOnlyWhenAiming = false;
        public static float shoulderHeadYawFollowAmount = 1f;
        public static float shoulderFollowEaseTime = 0.15f;  // ease in/out so ADS doesn't pop the shoulders
        private static float shoulderFollowLerp = 0f;
        internal static float lastShoulderYaw = 0f;          // shoulder yaw OverrideShoulders used this frame
        internal static int shoulderYawFrame = -1;

        // --- Custom elbow bend (we own the pole; replaces the Goal/Arm bend modifiers) ---------------
        // BendModifier.Goal has a pole singularity: a FIXED goal point vs the hand direction - when
        // shoulder->goal goes near-parallel to shoulder->hand the cross product collapses and FLIPS the
        // elbow ("elbow shoots up"); moving the goal only relocates the bad cone. BendModifier.Arm is
        // unusable on this rig (see IKManager - hints resolve in the collarbone's local frame, mirrored on
        // the right). Custom mode: each frame we set solver.bendNormal ourselves. Desired elbow direction =
        // a shoulder-yaw-relative rest direction PROJECTED onto the circle of valid elbow positions (the
        // plane perpendicular to shoulder->hand). At the old singularity (hand along the rest direction)
        // the projection degenerates and we HOLD the previous direction instead of flipping - hysteresis
        // makes a flip geometrically impossible - plus a short low-pass so the elbow glides. Rest
        // directions seeded from the old tuned bend goals' direction from the shoulder. Left-handed mode
        // keeps vanilla Goal (mirrored rig untested). naturalElbow=false = vanilla Goal, live-revertible.
        public static bool naturalElbow = true;
        public static Vector3 elbowRestDirRight = new Vector3(1.35f, -0.81f, -0.40f); // out/down/slightly back
        public static Vector3 elbowRestDirLeft = new Vector3(-1.8f, -0.96f, -0.20f);
        public static float elbowDirSmoothTime = 0.06f;  // seconds; 0 = raw projection
        // Wrist-roll -> elbow deflection, ported from DynamicElbowPositioner (which moves the Goal-mode
        // bend goals this driver ignores). Its published X delta scales onto the unit rest direction here.
        // THE elbow-roll sensitivity knob (w1/w2 are skin-roll distribution, not this). DEP's input is now
        // the driver's measured FULL hand roll (bigger than the old euler residual), hence the lower gain.
        public static float elbowTwistGain = -0.04f;
        private static Vector3 lastElbowDirL, lastElbowDirR;
        private static bool elbowDirInitL, elbowDirInitR;
        private static IKSolverLimb.BendModifier origBendModL, origBendModR;
        private static bool bendModCapturedL, bendModCapturedR;

        // method_20 just wrote the solvers' IK positions and method_19 (the actual solve) runs immediately
        // after, so the bendNormal written here is exactly what this frame solves with - same phase, no lag.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "IkProcess")]
        private static void DriveElbowBendNormals(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || VRGlobals.ikManager == null)
                return;

            TarkovVR.Source.Player.VR.ArmLength.Apply();

            bool on = naturalElbow && !Handedness.MirrorLeftHanded;
            // Elbow reference frame: the yaw OverrideShoulders placed the shoulders with this frame
            // (head-follow aware); body yaw when it didn't run (e.g. mounted/vanilla path).
            float yaw = shoulderYawFrame == Time.frameCount ? lastShoulderYaw : __instance.Transform.eulerAngles.y;

            DriveElbow(VRGlobals.ikManager.leftArmIk, elbowRestDirLeft,
                       Source.Player.Body.DynamicElbowPositioner.leftTwistDeltaX, yaw, on,
                       ref lastElbowDirL, ref elbowDirInitL, ref origBendModL, ref bendModCapturedL);
            DriveElbow(VRGlobals.ikManager.rightArmIk, elbowRestDirRight,
                       Source.Player.Body.DynamicElbowPositioner.rightTwistDeltaX, yaw, on,
                       ref lastElbowDirR, ref elbowDirInitR, ref origBendModR, ref bendModCapturedR);
        }

        private static void DriveElbow(LimbIK arm, Vector3 restDir, float twistDeltaX, float yaw, bool on,
                                       ref Vector3 lastDir, ref bool dirInit,
                                       ref IKSolverLimb.BendModifier origMod, ref bool captured)
        {
            if (arm == null || arm.solver == null || arm.solver.bone1?.transform == null)
                return;
            if (!captured) { origMod = arm.solver.bendModifier; captured = true; }
            if (!on)
            {
                if (arm.solver.bendModifier == IKSolverLimb.BendModifier.Custom)
                    arm.solver.bendModifier = origMod; // back to vanilla Goal
                dirInit = false;
                return;
            }
            arm.solver.bendModifier = IKSolverLimb.BendModifier.Custom;

            Vector3 shoulder = arm.solver.bone1.transform.position;
            // This frame's hand target: solver.target wins inside solver.Update (empty hands/foregrip);
            // otherwise method_20 just wrote IKPosition from the weapon markers.
            Vector3 hand = arm.solver.target != null ? arm.solver.target.position : arm.solver.IKPosition;
            Vector3 d = hand - shoulder;
            float dMag = d.magnitude;
            if (dMag < 0.05f)
                return; // degenerate reach; last frame's normal stays in the solver
            d /= dMag;

            // Rest direction + the wrist-roll deflection (already outward-clamped/sprint-frozen upstream).
            Vector3 rest = restDir.normalized;
            rest.x += twistDeltaX * elbowTwistGain;
            Vector3 e = Quaternion.Euler(0f, yaw, 0f) * rest.normalized;
            e -= d * Vector3.Dot(e, d);      // project onto the elbow circle's plane
            if (e.sqrMagnitude < 1e-4f)      // hand along the rest direction = the old flip pose
            {
                if (!dirInit) return;
                e = lastDir - d * Vector3.Dot(lastDir, d); // hold the previous elbow direction through it
                if (e.sqrMagnitude < 1e-6f) return;
            }
            e.Normalize();

            if (dirInit && elbowDirSmoothTime > 0f)
            {
                e = Vector3.Slerp(lastDir, e, Time.deltaTime / elbowDirSmoothTime);
                e = (e - d * Vector3.Dot(e, d)).normalized; // re-seat on the circle after smoothing
            }
            lastDir = e;
            dirInit = true;

            // The solve bends bone2 along Cross(handDir, bendNormal), so this puts the elbow exactly at +e.
            arm.solver.bendNormal = Vector3.Cross(e, d);
        }

        // --- Deterministic forearm/wrist twist (owns the twist chain like DriveElbow owns the pole) ---
        // The wrist bone itself (solver.bone3) is already IK-driven to the marker/controller rotation.
        // The "wrist randomly twists while running" is the twist chain BETWEEN elbow and wrist (Forearm1 =
        // solver bone2, plus Forearm2): the chain isn't direct-hierarchy so the solver re-captures bone
        // roll from the ANIMATED pose every frame (the sprint clip leaks in), and TwistRelax is stateful
        // (relaxes from current values), so once a bad roll gets in it wanders instead of correcting.
        // Same cure as the elbow: post-solve, rebuild the forearm frame from geometry we own - forearm
        // axis + our bend normal (= the elbow hinge axis, always perpendicular to the forearm) - and
        // distribute the wrist's measured roll along the twist bones with fixed weights. Bind offsets are
        // captured AT RIG SPAWN from the rest pose (bone axes are arbitrary on this rig - the Arm-mode
        // lesson; spawn = deterministic, identical hideout/raid, no dependence on what the hands hold);
        // RecaptureWristBind() re-calibrates live. Authority scales with the solver's IK weight so
        // reload/interaction anims (weight < 1) keep their hand posing. Works with naturalElbow on or off
        // (frame falls back to the geometric elbow plane). false = old TwistRelax / sprint-freeze behavior.
        public static bool naturalWrist = true;
        public static float wristTwistW1 = 0.35f;       // share of the wrist roll on Forearm1 (elbow side)
        public static float wristTwistW2 = 0.70f;       // share on Forearm2 (nearer the wrist = more roll)
        public static float wristTwistClampDeg = 200f;  // wrap-around safety on the measured roll
        // Constant roll trim (deg) added to the measured twist - corrects a bind captured with the hand
        // slightly rolled (symptom: forearm looks twisted a few degrees AT NEUTRAL; w1/w2 can't fix that,
        // they only scale roll RESPONSE).
        public static float wristTwistTrimL = 0f;
        public static float wristTwistTrimR = 0f;
        // The measured physical hand roll (deg, post-trim) - published for DynamicElbowPositioner, whose
        // old bone-euler read now changes meaning with w1/w2 (the local euler only carries the 1-w2
        // residual). This is the stable "how far is the hand rolled" signal regardless of distribution.
        internal static float wristTauL, wristTauR;
        private static bool wristBindInitL, wristBindInitR;
        private static Quaternion wristOff1L, wristOff2L, wristOff3L;
        private static Quaternion wristOff1R, wristOff2R, wristOff3R;

        // Capture the twist-chain bind (the "zero roll" reference for the arbitrary bone axes) from the
        // CURRENT pose of whichever arms are wired. Called from SetupIK at rig spawn - BEFORE any weapon
        // is drawn, the same rest pose in hideout and raid - so calibration no longer depends on what the
        // hands happen to hold at some gameplay frame (the raid-first "arms broken" bug: the old lazy
        // capture sampled the gun-draw pose and kept that zero all session). Idempotent; also callable
        // live from UnityExplorer to re-zero against whatever pose you're holding.
        public static void RecaptureWristBind()
        {
            if (VRGlobals.ikManager == null)
                return;
            wristBindInitL = CaptureWristBind(VRGlobals.ikManager.leftArmIk, ref wristOff1L, ref wristOff2L, ref wristOff3L);
            wristBindInitR = CaptureWristBind(VRGlobals.ikManager.rightArmIk, ref wristOff1R, ref wristOff2R, ref wristOff3R);
        }

        // Per-rig capture plumbing for SetupIK. SetupIK re-fires on EVERY LimbIK disable (weapon equips
        // disable rightArmIk), so an unconditional capture there would re-zero on live gameplay poses -
        // the raid-first bug through a side door. Instead: the fresh-rig add-block CLEARS, and each
        // collarbone block captures its own arm ONLY IF unset - so exactly one capture per arm per rig,
        // always at the spawn rest pose.
        internal static void ClearWristBind() { wristBindInitL = wristBindInitR = false; }
        internal static void CaptureWristBindLeftIfNeeded()
        {
            if (!wristBindInitL && VRGlobals.ikManager != null)
                wristBindInitL = CaptureWristBind(VRGlobals.ikManager.leftArmIk, ref wristOff1L, ref wristOff2L, ref wristOff3L);
        }
        internal static void CaptureWristBindRightIfNeeded()
        {
            if (!wristBindInitR && VRGlobals.ikManager != null)
                wristBindInitR = CaptureWristBind(VRGlobals.ikManager.rightArmIk, ref wristOff1R, ref wristOff2R, ref wristOff3R);
        }

        // The forearm frame the twist is measured in: forearm axis + an elbow-plane normal. When the
        // elbow driver owns the solve (Custom) use its bendNormal - smooth, hysteresis-held, and
        // well-conditioned even with the arm fully extended. Otherwise (the spawn capture runs before the
        // driver ever solves; or naturalElbow off) use the GEOMETRIC elbow-plane normal
        // Cross(upperarm, forearm) - the solve puts the elbow on the driver's plane, so for any bent arm
        // the two conventions are parallel and agree; a near-straight arm gets a deterministic fallback.
        private static bool ForearmFrame(LimbIK arm, out Transform f1, out Transform f2, out Transform wrist,
                                         out Vector3 axis, out Quaternion F)
        {
            f1 = f2 = wrist = null; axis = Vector3.zero; F = Quaternion.identity;
            if (arm == null || arm.solver == null || arm.solver.bone1?.transform == null ||
                arm.solver.bone2?.transform == null || arm.solver.bone3?.transform == null)
                return false;
            Transform shoulder = arm.solver.bone1.transform; // Upperarm
            f1 = arm.solver.bone2.transform;                 // Forearm1 (the elbow-side bone)
            wrist = arm.solver.bone3.transform;              // Forearm3 (the IK wrist)
            f2 = wrist.parent == f1 ? null : wrist.parent;   // Forearm2 twist bone, or none if direct

            axis = wrist.position - f1.position;
            if (axis.sqrMagnitude < 1e-8f)
                return false;
            axis.Normalize();

            Vector3 n = arm.solver.bendModifier == IKSolverLimb.BendModifier.Custom
                ? arm.solver.bendNormal
                : Vector3.Cross(f1.position - shoulder.position, wrist.position - f1.position);
            n -= axis * Vector3.Dot(n, axis);
            if (n.sqrMagnitude < 1e-8f)
                n = Vector3.ProjectOnPlane(Vector3.up, axis);      // straight arm: deterministic fallback
            if (n.sqrMagnitude < 1e-8f)
                n = Vector3.ProjectOnPlane(Vector3.forward, axis); // vertical straight arm
            if (n.sqrMagnitude < 1e-8f)
                return false;
            F = Quaternion.LookRotation(axis, n.normalized);
            return true;
        }

        private static bool CaptureWristBind(LimbIK arm, ref Quaternion off1, ref Quaternion off2, ref Quaternion off3)
        {
            if (!ForearmFrame(arm, out Transform f1, out Transform f2, out Transform wrist, out _, out Quaternion F))
                return false;
            Quaternion inv = Quaternion.Inverse(F);
            off1 = inv * f1.rotation;
            off2 = f2 != null ? inv * f2.rotation : Quaternion.identity;
            off3 = inv * wrist.rotation;
            return true;
        }

        // method_19 ran the limb solves + TwistRelax; we overwrite the twist bones after all of it.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "IkApply")]
        private static void DriveWristTwist(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer || VRGlobals.ikManager == null)
                return;
            if (!naturalWrist || Handedness.MirrorLeftHanded)
                return;

            ApplyWristTwist(VRGlobals.ikManager.leftArmIk, wristTwistTrimL, ref wristTauL, wristBindInitL, wristOff1L, wristOff2L, wristOff3L);
            ApplyWristTwist(VRGlobals.ikManager.rightArmIk, wristTwistTrimR, ref wristTauR, wristBindInitR, wristOff1R, wristOff2R, wristOff3R);
        }

        private static void ApplyWristTwist(LimbIK arm, float trimDeg, ref float tauOut, bool bindInit,
                                            Quaternion off1, Quaternion off2, Quaternion off3)
        {
            // No bind captured (spawn capture didn't land) -> leave the vanilla twist alone rather than
            // guess a zero off a live gameplay pose (the old raid-first bug). RecaptureWristBind() re-arms.
            if (!bindInit || arm == null || arm.solver == null)
                return;
            float w = arm.solver.IKPositionWeight;
            if (w <= 0.001f)
                return;
            if (!ForearmFrame(arm, out Transform f1, out Transform f2, out Transform wrist, out Vector3 axis, out Quaternion F))
                return;

            // The wrist's roll beyond its bind pose, measured around the forearm axis (swing-twist).
            float tau = TwistAngle(wrist.rotation * Quaternion.Inverse(F * off3), axis);
            tau = Mathf.Clamp(tau + trimDeg, -wristTwistClampDeg, wristTwistClampDeg);
            tauOut = tau;

            // Re-pose the twist bones: hinge-locked base frame + their share of the wrist roll. Writing
            // f1 moves its children, so the IK-solved wrist orientation is saved and re-asserted.
            Quaternion wristRot = wrist.rotation;
            Quaternion target1 = Quaternion.AngleAxis(tau * wristTwistW1, axis) * F * off1;
            f1.rotation = w >= 1f ? target1 : Quaternion.Slerp(f1.rotation, target1, w);
            if (f2 != null)
            {
                Quaternion target2 = Quaternion.AngleAxis(tau * wristTwistW2, axis) * F * off2;
                f2.rotation = w >= 1f ? target2 : Quaternion.Slerp(f2.rotation, target2, w);
            }
            wrist.rotation = wristRot;
        }

        // Signed twist (deg) of q around axis, in (-180, 180] (swing-twist decomposition).
        private static float TwistAngle(Quaternion q, Vector3 axis)
        {
            float proj = q.x * axis.x + q.y * axis.y + q.z * axis.z;
            float mag = Mathf.Sqrt(proj * proj + q.w * q.w);
            if (mag < 1e-6f)
                return 0f;
            float w = q.w / mag, s = proj / mag;
            if (w < 0f) { w = -w; s = -s; }   // canonical hemisphere
            return 2f * Mathf.Atan2(s, w) * Mathf.Rad2Deg;
        }

        // --- Sprint (run-anim-disabled) shoulder/arm steadying ---------------------------------------
        // RemoveSprintAnimFromHands pins the HANDS while running, but the arm ROOT above them was still
        // driven by the sprint gait: the shoulders got shoved forward (sprintShoulderForward) and their
        // rotation pumped, which flared the elbows. These hold the shoulders in their standing VR pose
        // during a run-anim-disabled sprint. All A/B-able in the headset.
        public static float sprintShoulderForward = 0f;        // forward/out shoulder push while sprinting (was 0.10)
        public static bool steadyShouldersDuringSprint = true; // low-pass the pumping shoulder rotation while sprinting
        public static float sprintShoulderRotSmoothTime = 0.15f; // higher = steadier but laggier into a sprint
        private static Quaternion smoothedShoulderRotL = Quaternion.identity;
        private static Quaternion smoothedShoulderRotR = Quaternion.identity;
        private static bool shoulderRotInit = false;
       

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "UpdateBonesOnWeaponChange")]
        private static void FixLeftArmBendGoal(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return;
            // Change the elbow bend from the weapons left arm goal to the player bodies bend goal, otherwise the left arms bend goal acts like its
            // still attached to the gun even when its not

            __instance._elbowBends[0] = __instance.PlayerBones.BendGoals[0];
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "SetCompensationScale")]
        private static void SetBodyIKScale(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return;
            // If this isn't set to 1, then the hands start to stretch or squish when rotating them around
            __instance.RibcageScaleCurrentTarget = 1f;
            __instance.RibcageScaleCurrent = 1f;
        }

        // This can also be used to disable hand animations when needed
        private static float zeroOutEndTime = 0f;
        private const float ZERO_OUT_DURATION = 1f;
        private static bool wasSprinting = false;


        // airborneFreefallTime = seconds you must be CONTINUOUSLY off the ground before the hands go to
        // VR control. Higher = more edge-proof but a touch more lag into a jump; lower = snappier but can
        // let a brief edge flicker through. 0 = disable the air path entirely (sprint-only).
        public static float airborneFreefallTime = 0.20f;

        // Freeze the right arm at its pre-sprint grip pose during a run-anim-disabled sprint.
        // While a gun is held the right-arm IK (_limbs[1] == rightArmIk) is OFF, so the right arm - elbow
        // AND wrist - is pure body animation while the gun is pinned separately. That's why the sprint clip
        // twists the wrist off the grip. Since no IK fights us, we just hold the arm still: snapshot the
        // arm-bone local rotations every NON-sprint frame (the live aim/idle grip pose), then re-apply that
        // last snapshot each frame of the run. The sprint motion (incl. the wrist twist) is gone and the
        // grip pose is preserved. Right-handed only for now (left-handed swaps the dominant arm).
        // Freeze both arms at their pre-sprint grip pose during a run-anim-disabled sprint.

        // Right Arm State
        private static Transform[] rightArmBones;        // [collarbone, upperarm, forearm1, forearm2, forearm3/wrist, palm]
        private static Quaternion[] rightFrozenArmRot;   // the snapshot we hold during the run
        private static Quaternion[] rightLastNonSprintArmRot; // rolling pre-sprint capture

        // Left Arm State
        private static Transform[] leftArmBones;         // [collarbone, upperarm, forearm1, forearm2, forearm3/wrist, palm]
        private static Quaternion[] leftFrozenArmRot;    // the snapshot we hold during the run
        private static Quaternion[] leftLastNonSprintArmRot; // rolling pre-sprint capture

        private static bool armPoseFrozen = false;

        // The forearm/upperarm bones each carry a TwistRelax (FinalIK) component (set up in InitVRPatches,
        // weights 3/1.8/1/1) that RE-ROLLS the forearm twist every LateUpdate - i.e. AFTER our method_20
        // freeze - so it overrides the frozen twist on exactly the twist DOF. On the RIGHT arm the hand is
        // stably pinned to the gun marker, so TwistRelax relaxes toward ~0 roll and looks fine; on the LEFT
        // the reference is less stable, so it intermittently rolls the frozen forearm while the IK keeps the
        // HAND pinned to the gun -> the observed "left wrist twists, hand stays on the gun" that "works for a
        // while then starts twisting." While a run-anim-disabled sprint holds the arm frozen we zero these
        // weights so the frozen twist survives, then restore them. This touches the twist bones ONLY, never
        // the IK hand pin, so the hands stay on the gun. false = leave TwistRelax alone (old behavior).
        public static bool holdForearmTwistDuringSprint = true;
        // Indices into the arm-bone chain that own a TwistRelax (upperarm, forearm1, forearm2, forearm3/wrist).
        private static readonly int[] twistRelaxBoneIdx = { 1, 2, 3, 4 };
        private static TwistRelax[] rightTwistRelax;
        private static float[] rightTwistRelaxWeights; // original weights, restored when not frozen
        private static TwistRelax[] leftTwistRelax;
        private static float[] leftTwistRelaxWeights;
        private static bool armTwistRelaxed = false;   // true while we've zeroed the weights for a freeze

        // Resolve the TwistRelax components (+ their configured weights) on an already-resolved arm chain.
        private static void ResolveTwistRelax(Transform[] armBones, ref TwistRelax[] trs, ref float[] weights)
        {
            if (armBones == null || trs != null) return; // resolve once per arm
            trs = new TwistRelax[twistRelaxBoneIdx.Length];
            weights = new float[twistRelaxBoneIdx.Length];
            for (int k = 0; k < twistRelaxBoneIdx.Length; k++)
            {
                Transform t = armBones[twistRelaxBoneIdx[k]];
                TwistRelax tr = t != null ? t.GetComponent<TwistRelax>() : null;
                trs[k] = tr;
                weights[k] = tr != null ? tr.weight : 0f;
            }
        }

        // frozen=true -> zero the weights (hold the frozen twist); false -> restore the configured weights.
        private static void SetTwistRelaxFrozen(TwistRelax[] trs, float[] weights, bool frozen)
        {
            if (trs == null) return;
            for (int i = 0; i < trs.Length; i++)
                if (trs[i] != null) trs[i].weight = frozen ? 0f : weights[i];
        }

        // Resolve both arm bone chains off the wrists. Returns true if AT LEAST ONE arm resolves successfully.
        private static bool ResolveArmBones()
        {
            // Resolve Right Arm
            if (rightArmBones == null)
            {
                Transform rWrist = TarkovVR.Patches.Core.VR.InitVRPatches.rightWrist; // Base HumanRForearm3
                if (rWrist != null)
                {
                    Transform rForearm2 = rWrist.parent;
                    Transform rForearm1 = rForearm2 != null ? rForearm2.parent : null;
                    Transform rUpperarm = rForearm1 != null ? rForearm1.parent : null;
                    Transform rCollar = rUpperarm != null ? rUpperarm.parent : null; // Base HumanRCollarbone - freeze the arm ROOT too or the elbow still swings

                    if (rForearm2 != null && rForearm1 != null && rUpperarm != null)
                    {
                        Transform rPalm = null;
                        for (int i = 0; i < rWrist.childCount; i++)
                            if (rWrist.GetChild(i).name.IndexOf("Palm", StringComparison.OrdinalIgnoreCase) >= 0)
                            { rPalm = rWrist.GetChild(i); break; }

                        rightArmBones = new Transform[] { rCollar, rUpperarm, rForearm1, rForearm2, rWrist, rPalm };
                        rightFrozenArmRot = new Quaternion[rightArmBones.Length];
                        rightLastNonSprintArmRot = new Quaternion[rightArmBones.Length];

                        for (int i = 0; i < rightArmBones.Length; i++)
                            if (rightArmBones[i] != null) rightLastNonSprintArmRot[i] = rightArmBones[i].localRotation;
                    }
                }
            }

            // Resolve Left Arm
            if (leftArmBones == null)
            {
                Transform lWrist = TarkovVR.Patches.Core.VR.InitVRPatches.leftWrist; // Base HumanLForearm3
                if (lWrist != null)
                {
                    Transform lForearm2 = lWrist.parent;
                    Transform lForearm1 = lForearm2 != null ? lForearm2.parent : null;
                    Transform lUpperarm = lForearm1 != null ? lForearm1.parent : null;
                    Transform lCollar = lUpperarm != null ? lUpperarm.parent : null; // Base HumanLCollarbone - freeze the arm ROOT too or the elbow still swings

                    if (lForearm2 != null && lForearm1 != null && lUpperarm != null)
                    {
                        Transform lPalm = null;
                        for (int i = 0; i < lWrist.childCount; i++)
                            if (lWrist.GetChild(i).name.IndexOf("Palm", StringComparison.OrdinalIgnoreCase) >= 0)
                            { lPalm = lWrist.GetChild(i); break; }

                        leftArmBones = new Transform[] { lCollar, lUpperarm, lForearm1, lForearm2, lWrist, lPalm };
                        leftFrozenArmRot = new Quaternion[leftArmBones.Length];
                        leftLastNonSprintArmRot = new Quaternion[leftArmBones.Length];

                        for (int i = 0; i < leftArmBones.Length; i++)
                            if (leftArmBones[i] != null) leftLastNonSprintArmRot[i] = leftArmBones[i].localRotation;
                    }
                }
            }

            // Cache the TwistRelax components (once per arm) now that the chains are resolved.
            ResolveTwistRelax(rightArmBones, ref rightTwistRelax, ref rightTwistRelaxWeights);
            ResolveTwistRelax(leftArmBones, ref leftTwistRelax, ref leftTwistRelaxWeights);

            // Proceed as long as we've found at least one arm
            return rightArmBones != null || leftArmBones != null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "IkProcess")]
        private static bool RemoveSprintAnimFromHands(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return true;
            if (__instance.HandsIsEmpty)
                return false;

            bool trulyInAir = airborneFreefallTime > 0f &&
                              __instance.MovementContext.FreefallTime > airborneFreefallTime;

            bool disableAnim = (__instance.IsSprintEnabled && VRGlobals.player.MovementContext._poseLevel != 0 && VRSettings.GetDisableRunAnim()) || trulyInAir;

            float currentTime = Time.time;

            if (wasSprinting && !disableAnim)
                zeroOutEndTime = currentTime + ZERO_OUT_DURATION;

            wasSprinting = disableAnim;

            bool shouldStayZeroed = disableAnim || (currentTime < zeroOutEndTime);

            // The gun's marker even while slung: then _markers[1] is the freed hand's IK target, whose grandparent is the VR rig
            Transform gunRightMarker = TarkovVR.Source.Player.Interactions.ChestHang.GunRightMarker(__instance);
            if (shouldStayZeroed && gunRightMarker?.parent?.parent != null)
            {
                var ik = gunRightMarker.parent.parent;
                ik.localPosition = Vector3.zero;
                ik.localEulerAngles = Vector3.zero;
            }

            // Hold BOTH arms at their pre-sprint grip pose so the run animation's wrist twist / arm swing
            // is removed while the hands keep gripping the gun.
            bool runAnimDisabledSprint = __instance.IsSprintEnabled && VRSettings.GetDisableRunAnim();

            // Removed !VRSettings.GetLeftHandedMode() so this executes universally.
            if (VRGlobals.firearmController != null && ResolveArmBones())
            {
                // Hold (zero) the TwistRelax forearm-roll during the freeze so it can't re-twist the frozen
                // arm; restore the configured weights otherwise. Touches the twist DOF only, not the hand pin.
                if (holdForearmTwistDuringSprint)
                {
                    SetTwistRelaxFrozen(rightTwistRelax, rightTwistRelaxWeights, runAnimDisabledSprint);
                    SetTwistRelaxFrozen(leftTwistRelax, leftTwistRelaxWeights, runAnimDisabledSprint);
                    armTwistRelaxed = runAnimDisabledSprint;
                }
                else if (armTwistRelaxed) // flag turned off mid-freeze -> restore once so weights don't stick at 0
                {
                    SetTwistRelaxFrozen(rightTwistRelax, rightTwistRelaxWeights, false);
                    SetTwistRelaxFrozen(leftTwistRelax, leftTwistRelaxWeights, false);
                    armTwistRelaxed = false;
                }

                if (runAnimDisabledSprint)
                {
                    if (!armPoseFrozen)
                    {
                        if (rightArmBones != null) Array.Copy(rightLastNonSprintArmRot, rightFrozenArmRot, rightArmBones.Length);
                        if (leftArmBones != null) Array.Copy(leftLastNonSprintArmRot, leftFrozenArmRot, leftArmBones.Length);
                        armPoseFrozen = true;
                    }

                    if (rightArmBones != null)
                    {
                        for (int i = 0; i < rightArmBones.Length; i++)
                            if (rightArmBones[i] != null) rightArmBones[i].localRotation = rightFrozenArmRot[i];
                    }

                    if (leftArmBones != null)
                    {
                        for (int i = 0; i < leftArmBones.Length; i++)
                            if (leftArmBones[i] != null) leftArmBones[i].localRotation = leftFrozenArmRot[i];
                    }
                }
                else
                {
                    armPoseFrozen = false;

                    if (rightArmBones != null)
                    {
                        for (int i = 0; i < rightArmBones.Length; i++)
                            if (rightArmBones[i] != null) rightLastNonSprintArmRot[i] = rightArmBones[i].localRotation;
                    }

                    if (leftArmBones != null)
                    {
                        for (int i = 0; i < leftArmBones.Length; i++)
                            if (leftArmBones[i] != null) leftLastNonSprintArmRot[i] = leftArmBones[i].localRotation;
                    }
                }
            }

            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayerAnimator), "EnableSprint")]
        private static void DisableLayer1DuringSprint(PlayerAnimator __instance)
        {
            if (VRSettings.GetDisableRunAnim())
            {
                __instance.Animator.SetLayerWeight(1, 0f);
            }
        }

        private static bool test = true;
        [HarmonyPostfix]
        [HarmonyPatch(typeof(UnityAnimatorWrapper), "SetAnimator")]
        private static void ReemoveSprintAnimFromHands(UnityAnimatorWrapper __instance)
        {
            __instance.Animator.SetLayerWeight(4, 0);
        }
        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(EFT.Player), "method_22")]
        //private static bool SetHandIKPosition(EFT.Player __instance, float distance2Camera)
        //{
        //    for (int i = 0; i < 2; i++)
        //    {
        //        if (i == 1)
        //            __instance._markers[i].localPosition += new Vector3(-0.06f, 0, 0);
        //        else { 
        //            __instance._markers[i].localPosition += VRGlobals.test;
        //            __instance._markers[i].localEulerAngles += VRGlobals.testRot;
        //        }

        //        if (!(__instance._markers[i] == null) && !(Math.Abs(__instance._limbs[i].solver.IKPositionWeight) < float.Epsilon))
        //        {
        //            if (__instance._ikTargets[i] != null && distance2Camera < 40f)
        //            {
        //                float value = Vector3.Distance(__instance._markers[i].position, __instance._gripReferences[i].position);
        //                float num = Mathf.InverseLerp(0.1f, 0f, value);
        //                __instance.HandPosers[i].GripWeight = num;
        //                __instance._ikPosition = Vector3.Lerp(__instance._markers[i].position, __instance._ikTargets[i].position, num);
        //                __instance._ikRotation = Quaternion.Lerp(__instance._markers[i].rotation, __instance._ikTargets[i].rotation, num);
        //            }
        //            else
        //            {
        //                __instance._ikPosition = __instance._markers[i].position;
        //                __instance._ikRotation = __instance._markers[i].rotation;
        //            }
        //            if (__instance.LeftHandInteractionTarget != null && i == 0)
        //            {
        //                __instance._ikPosition = Vector3.Lerp(__instance._ikPosition, __instance.LeftHandInteractionTarget.transform.position, __instance.ThirdIkWeight.Value);
        //                __instance._ikRotation = Quaternion.Slerp(__instance._ikRotation, __instance.LeftHandInteractionTarget.transform.rotation, __instance.ThirdIkWeight.Value);
        //            }
        //            __instance._limbs[i].solver.SetIKPosition(__instance._ikPosition);
        //            __instance._limbs[i].solver.SetIKRotation(__instance._ikRotation);
        //        }
        //    }
        //    return false;
        //}


        //[HarmonyPrefix]
        //[HarmonyPatch(typeof(FirearmsAnimator), "SetSprint")]
        //private static bool DisableSprintAnimation(FirearmsAnimator __instance)
        //{
        //    return false;
        //}
    }
}
