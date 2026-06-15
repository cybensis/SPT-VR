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
            else if (VRGlobals.player.MovementContext.PoseLevel_1 == 0f)
                return false;

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

            __instance.SmoothedPoseLevel = __instance.PoseLevel_1;
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

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerBones), "SetShoulders")]
        private static bool OverrideShoulders(PlayerBones __instance)
        {
            if (__instance.Player == null || !__instance.Player.IsYourPlayer ||
                VRGlobals.vrPlayer == null || VRGlobals.ikManager == null ||
                VRSettings.GetLeftHandedMode())
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
            float armLengthOffset = (heightRatio - 1f) * 0.25f;

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

            // Body-relative directions
            float bodyYaw = __instance.Player.Transform.eulerAngles.y;
            Quaternion yawRotation = Quaternion.Euler(0f, bodyYaw, 0f);
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

            TransformHelperClass.LerpPositionAndRotation(
                __instance.Shoulders[0],
                leftTarget,
                smoothedShoulderRotL,
                0.65f);

            TransformHelperClass.LerpPositionAndRotation(
                __instance.Shoulders[1],
                rightTarget,
                smoothedShoulderRotR,
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

        // Re-apply the frozen arm pose in Application.onBeforeRender (the LAST write of the frame, after every
        // LateUpdate + IK solve, right before render) instead of trusting the method_20 prefix to be the final
        // word. The method_20 freeze runs MID-frame (inside VisualPass / Player.LateUpdate), so several systems
        // can overwrite the arm bones AFTER it the same frame: EFT's own per-frame hand IK solve (method_19,
        // which runs right after method_20), the FinalIK LimbIK/TwistRelax SolverManager LateUpdate, and the
        // SteamVR off-hand pose callback. Whether the freeze or one of those wins is Unity LateUpdate/callback
        // ORDER, which Unity does not guarantee -> on the LEFT arm it's a per-session coin-flip whether the
        // freeze survives ("totally random; sometimes the left wrist twists"). The RIGHT arm wins reliably only
        // because its competitor (rightArmIk) is hard-disabled while a gun is held. Re-stamping the frozen
        // rotations in onBeforeRender makes the freeze deterministically the last writer for BOTH arms, so the
        // left becomes as reliable as the right. false = old behavior (method_20-only apply; left stays random).
        public static bool freezeArmPoseInBeforeRender = true;
        private static bool beforeRenderHooked = false;

        // Lazily hook onBeforeRender the first time the freeze path runs (cheap idempotent guard). The handler
        // self-gates, so registering once for the session is fine.
        private static void EnsureBeforeRenderHook()
        {
            if (beforeRenderHooked) return;
            Application.onBeforeRender += ReapplyFrozenArmPoseBeforeRender;
            beforeRenderHooked = true;
        }

        // Last-writer re-stamp of the frozen arm rotations. Only acts while armPoseFrozen (set/cleared by the
        // method_20 prefix), so it stops the instant a run-anim-disabled sprint ends - no stuck freeze.
        private static void ReapplyFrozenArmPoseBeforeRender()
        {
            if (!freezeArmPoseInBeforeRender || !armPoseFrozen)
                return;

            var p = VRGlobals.player;
            if (p == null || !p.IsYourPlayer || p.HandsIsEmpty)
                return;

            if (rightArmBones != null && rightFrozenArmRot != null)
                for (int i = 0; i < rightArmBones.Length; i++)
                    if (rightArmBones[i] != null) rightArmBones[i].localRotation = rightFrozenArmRot[i];

            if (leftArmBones != null && leftFrozenArmRot != null)
                for (int i = 0; i < leftArmBones.Length; i++)
                    if (leftArmBones[i] != null) leftArmBones[i].localRotation = leftFrozenArmRot[i];
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

            // Proceed as long as we've found at least one arm
            return rightArmBones != null || leftArmBones != null;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), "method_20")]
        private static bool RemoveSprintAnimFromHands(EFT.Player __instance)
        {
            if (!__instance.IsYourPlayer)
                return true;
            if (__instance.HandsIsEmpty)
                return false;

            bool trulyInAir = airborneFreefallTime > 0f &&
                              __instance.MovementContext.FreefallTime > airborneFreefallTime;

            bool disableAnim = (__instance.IsSprintEnabled && VRGlobals.player.MovementContext.PoseLevel_1 != 0 && VRSettings.GetDisableRunAnim()) || trulyInAir ;

            float currentTime = Time.time;

            if (wasSprinting && !disableAnim)
                zeroOutEndTime = currentTime + ZERO_OUT_DURATION;

            wasSprinting = disableAnim;

            bool shouldStayZeroed = disableAnim || (currentTime < zeroOutEndTime);

            if (shouldStayZeroed && __instance._markers.Length > 1 && __instance._markers[1]?.transform?.parent?.parent != null)
            {
                var ik = __instance._markers[1].transform.parent.parent;
                ik.localPosition = Vector3.zero;
                ik.localEulerAngles = Vector3.zero;
            }

            // Hold BOTH arms at their pre-sprint grip pose so the run animation's wrist twist / arm swing
            // is removed while the hands keep gripping the gun.
            bool runAnimDisabledSprint = __instance.IsSprintEnabled && VRSettings.GetDisableRunAnim();

            // Removed !VRSettings.GetLeftHandedMode() so this executes universally.
            if (VRGlobals.firearmController != null && ResolveArmBones())
            {
                EnsureBeforeRenderHook(); // make the freeze the deterministic last writer (see flag note)

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
        [HarmonyPatch(typeof(GClass1446), "SetAnimator")]
        private static void ReemoveSprintAnimFromHands(GClass1446 __instance)
        {
            __instance.Animator_0.SetLayerWeight(4, 0);
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
