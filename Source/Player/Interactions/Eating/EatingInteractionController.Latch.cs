using System;
using System.Text;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // The hand-latch toolkit: glue a rendered hand onto the held item (pull latch), ride a clip-animated bone (OpenGrip),
    // replay a baked palm path (HandPath), or slide along the reach rail (ReachBag).
    internal static partial class EatingInteractionController
    {
        // Latches the acting hand onto the held item: a latch transform at the hand's current pose, parented to the
        // holding hand's rig target so it rides that controller's full 6dof. DriveArms aims the latched arm's IK at it and
        // the bone3 pin glues the rendered wrist to it (Left/RightPinTarget); the physical pull only drives the scrub.
        // The acting rig target keeps tracking its controller throughout.
        private static void LatchHandToItem(Hand hand, Transform acting, Transform anchor)
        {
            CaptureLatchStart(hand, acting);
            latchedHandIsDominant = hand == Hand.Dominant;
            if (pullLatch == null) pullLatch = new GameObject("EatPullLatch");
            pullLatch.transform.SetParent(anchor, false);
            pullLatch.transform.position = acting.position;
            pullLatch.transform.rotation = acting.rotation;
            latchGrabActRot = acting.rotation;                     // wiggle reference
            latchGrabLocalRot = pullLatch.transform.localRotation; // freeze-mode rotation anchor (rides the item)
            latchFrozenLocalPosition = pullLatch.transform.localPosition;
            BeginLatchEase(anchor, acting);

            // OpenGrip: the hand rides the lid/cover bone from here (DriveLatch). pullSnapToGrip snaps it onto the bone
            // (+ pullSnapPos); otherwise it keeps the offset from the trigger press. Re-grabs capture against the lid's
            // current mid-roll pose.
            latchRidesProp = openGripT != null;
            if (latchRidesProp)
            {
                if (pullSnapToGrip)
                    propGrabLocalPos = pullSnapPos; // DriveLatch puts the fingertip on the bone this same frame (pre-IK)
                else
                    propGrabLocalPos = openGripT.InverseTransformPoint(pullLatch.transform.position);
                propGrabLocalRot = Quaternion.Inverse(openGripT.rotation) * pullLatch.transform.rotation;
            }
            // No ride bone configured: snapshot the held item's bones once (first grab = the closed pose) so a completed
            // pull can rank and log the lid bone.
            else if (logOpenMovers && moverBones == null && baseT != null)
            {
                moverBones = baseT.GetComponentsInChildren<Transform>(true);
                moverStartLocal = new Vector3[moverBones.Length];
                for (int i = 0; i < moverBones.Length; i++)
                    moverStartLocal[i] = baseT.InverseTransformPoint(moverBones[i].position);
            }
            handLatched = true;
        }

        // immediate: the finish / cancel, whose put-away owns the arms from this frame
        private static void EndPull(bool immediate = false)
        {
            pullingOpen = false;
            pullSqueezeDone = false;  // per-grab: the next grab squeezes again before the physical part
            pullAwaitRelease = false;
            ReleaseLatch(immediate);
        }

        // Off the latch: DriveArms re-aims the IK at the rig target next Tick -> the hand snaps back. reloadStyleLatch eases it back
        // first (DriveLatchRelease), the latch still holding the arm meanwhile.
        private static void ReleaseLatch(bool immediate)
        {
            if (reloadStyleLatch && !immediate && handLatched && pullLatch != null && latchReleaseTime > 0f)
            {
                if (!latchReleasing)
                {
                    latchReleasing = true;
                    latchReleaseStartTime = Time.time;
                }
                return;
            }
            handLatched = false;
            latchReleasing = false;
            if (pullLatch != null) { UnityEngine.Object.Destroy(pullLatch); pullLatch = null; }
        }

        // A grab while the last release is still easing back starts from where that hand is drawn, not from its controller
        private static void CaptureLatchStart(Hand grabbingHand, Transform acting)
        {
            bool sameHandEasingBack = latchReleasing && handLatched && pullLatch != null && latchedHandIsDominant == (grabbingHand == Hand.Dominant);
            latchStartWorldPosition = sameHandEasingBack ? pullLatch.transform.position : acting.position;
            latchStartWorldRotation = sameHandEasingBack ? pullLatch.transform.rotation : acting.rotation;
            latchReleasing = false;
        }

        // After the latch is parented and placed: the ease-in start and the wiggle's reference, in the holding target's frame
        private static void BeginLatchEase(Transform anchor, Transform acting)
        {
            latchStartLocalPosition = anchor.InverseTransformPoint(latchStartWorldPosition);
            latchStartLocalRotation = Quaternion.Inverse(anchor.rotation) * latchStartWorldRotation;
            latchControllerAtGrabInHolder = Quaternion.Inverse(anchor.rotation) * acting.rotation;
            latchGrabTime = Time.time;
        }

        // reloadStyleLatch, after a latch mode has written this frame's whole pose: eased in from where the hand was at the grab, then the
        // wiggle (VRReloadController.ApplyLatchWiggle): the controller's turn since the grab in the holding target's frame, capped, about
        // the point the hand holds (pivotInHand, in the latch's frame = the pinned wrist's)
        private static void ShapeLatch(bool wiggle, Vector3 pivotInHand)
        {
            Transform latch = pullLatch.transform;
            Transform holder = latch.parent;
            if (!reloadStyleLatch || holder == null)
                return;
            float sinceGrab = Time.time - latchGrabTime;
            if (latchSnapTime > 0f && sinceGrab < latchSnapTime * 3f)
            {
                float blend = 1f - Mathf.Exp(-sinceGrab / (latchSnapTime / 3f));
                latch.localPosition = Vector3.Lerp(latchStartLocalPosition, latch.localPosition, blend);
                latch.localRotation = Quaternion.Slerp(latchStartLocalRotation, latch.localRotation, blend);
            }
            if (!wiggle || latchWiggleDegrees <= 0f)
                return;
            Transform controller = latchedHandIsDominant ? RightHand() : LeftHand();
            if (controller == null)
                return;
            Quaternion turnInHolder = Quaternion.Inverse(holder.rotation) * controller.rotation * Quaternion.Inverse(latchControllerAtGrabInHolder);
            turnInHolder = Quaternion.RotateTowards(Quaternion.identity, turnInHolder, latchWiggleDegrees);
            Quaternion turn = holder.rotation * turnInHolder * Quaternion.Inverse(holder.rotation);
            Vector3 pivot = latch.position + latch.rotation * pivotInHand;
            latch.SetPositionAndRotation(pivot + turn * (latch.position - pivot), turn * latch.rotation);
        }

        // After a release (reloadStyleLatch): the hand eases from where it's drawn back onto its controller, then the arm is the controller's
        // again. LateZeroProps, after the latch modes.
        private static void DriveLatchRelease()
        {
            if (!latchReleasing)
                return;
            Transform controller = latchedHandIsDominant ? RightHand() : LeftHand();
            if (!handLatched || pullLatch == null || controller == null)
            {
                ReleaseLatch(immediate: true);
                return;
            }
            Transform latch = pullLatch.transform;
            float ease = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(latchReleaseTime / 3f, 0.001f));
            latch.SetPositionAndRotation(Vector3.Lerp(latch.position, controller.position, ease), Quaternion.Slerp(latch.rotation, controller.rotation, ease));
            // There, or after a while regardless: it trails a hand that keeps moving and would never quite arrive
            bool arrived = (latch.position - controller.position).sqrMagnitude < 0.003f * 0.003f && Quaternion.Angle(latch.rotation, controller.rotation) < 2f;
            if (arrived || Time.time - latchReleaseStartTime > latchReleaseTime * 4f)
                ReleaseLatch(immediate: true);
        }

        // Per-frame latch update, at the end of LateZeroProps (after the prop re-zeroing). Relative frames only: in
        // Player.LateUpdate every bone world pose is the pre-pin animated pose (the visible pose exists only after FinalIK
        // + the wrist pins later this frame), so never read bone world poses here. The grip pose is taken in the holding
        // wrist's local frame (wrist -> holder -> item -> lid is rigid within the frame) and re-anchored on the holding
        // rig target, which is where that wrist gets pinned.
        //  * pullRotationFree: rotation stays on the controller even while the position is locked or riding; otherwise
        //    it rides the bone with the offset captured at the grab.
        private static void DriveLatch()
        {
            if (!pullingOpen || !handLatched || pullLatch == null) return;
            Transform holdingTarget = pullLatch.transform.parent; // the holding hand's rig target (latch anchor)
            Transform holdingWrist = (latchedHandIsDominant ? VRGlobals.ikManager?.leftArmIk : VRGlobals.ikManager?.rightArmIk)?.solver?.bone3?.transform;

            // A Trigger-kind open has no gradual physical gesture: the axis can sweep the whole scrub in a frame, and the
            // path/bone ride would teleport the hand through it. It keeps the freeze-at-grab latch.
            bool triggerKind = def != null && def.openGesture == OpenGestureKind.Trigger;
            // reloadStyleLatch: a pull's wiggle turns the hand about the fingertip on the lid (ShapeLatch). A tilt's gesture is the
            // hand's turn: it keeps the small wiggle about the wrist (WiggleDelta).
            bool contactWiggle = reloadStyleLatch && (def == null || def.openGesture == OpenGestureKind.Pull);

            // Baked HandPath: the authored palm pose (item-root space, from the vanilla clip) at the current scrub time,
            // re-anchored through the rigid chain item -> holding wrist -> pinned target. The path is the palm-pin pose, so
            // no fingertip offset applies. Before the first key it holds that key.
            if (!triggerKind && def?.openHandPath != null && baseT != null && holdingTarget != null && holdingWrist != null)
            {
                SampleHandPath(def.openHandPath, CurrentScrubTime(), out Vector3 lp, out Quaternion lr);
                Vector3 worldPos = holdingTarget.TransformPoint(holdingWrist.InverseTransformPoint(baseT.TransformPoint(lp)));
                Quaternion worldRot = holdingTarget.rotation * (Quaternion.Inverse(holdingWrist.rotation) * (baseT.rotation * lr));
                if (pathRotationFree)
                {
                    Transform act = latchedHandIsDominant ? RightHand() : LeftHand();
                    if (act != null) worldRot = act.rotation;
                }
                else if (!contactWiggle)
                    worldRot = WiggleDelta() * worldRot; // authored rotation + clamped controller wiggle
                pullLatch.transform.SetPositionAndRotation(worldPos, worldRot);
                bool pathWiggle = contactWiggle && !pathRotationFree;
                ShapeLatch(pathWiggle, pathWiggle ? FingerOffsetInWristFrame() : Vector3.zero);
                return;
            }

            bool riding = !triggerKind && latchRidesProp && openGripT != null && holdingTarget != null && holdingWrist != null;

            // Rotation first: the fingertip offset below rotates with the hand. Anchor = the ridden bone's rotation (riding)
            // or the grab pose riding the item (frozen), plus the clamped controller wiggle.
            Quaternion rotation;
            if (pullRotationFree)
            {
                Transform act = latchedHandIsDominant ? RightHand() : LeftHand();
                rotation = act != null ? act.rotation : pullLatch.transform.rotation;
            }
            else if (riding)
                rotation = (contactWiggle ? Quaternion.identity : WiggleDelta()) * (holdingTarget.rotation * (Quaternion.Inverse(holdingWrist.rotation) * openGripT.rotation) * propGrabLocalRot);
            else
                rotation = (contactWiggle ? Quaternion.identity : WiggleDelta()) * (holdingTarget != null ? holdingTarget.rotation * latchGrabLocalRot : pullLatch.transform.rotation);
            pullLatch.transform.rotation = rotation;
            // Frozen where it grabbed: written every frame here, since ShapeLatch moves the latch off it
            if (reloadStyleLatch && !riding)
                pullLatch.transform.localPosition = latchFrozenLocalPosition;

            if (riding)
            {
                // lid -> holding-wrist-local (rigid) -> re-anchored where the wrist is pinned.
                Vector3 gripLocal = holdingWrist.InverseTransformPoint(openGripT.TransformPoint(propGrabLocalPos));
                Vector3 gripWorld = holdingTarget.TransformPoint(gripLocal);
                // The latch is the wrist pin target: back it off the grip point by the fingertip's offset so the fingertip
                // lands on it.
                pullLatch.transform.position = gripWorld
                    - (pullSnapToGrip ? rotation * FingerOffsetInWristFrame() : Vector3.zero);
            }
            bool grabWiggle = contactWiggle && !pullRotationFree;
            ShapeLatch(grabWiggle, grabWiggle ? FingerOffsetInWristFrame() : Vector3.zero);
        }

        // The controller's rotation off its grab-moment orientation, clamped to pullWiggleDeg: applied on top of an
        // anchored rotation so the latched hand follows the wrist a little without leaving the authored pose.
        private static Quaternion WiggleDelta()
        {
            if (pullWiggleDeg <= 0f) return Quaternion.identity;
            Transform act = latchedHandIsDominant ? RightHand() : LeftHand();
            if (act == null) return Quaternion.identity;
            Quaternion delta = act.rotation * Quaternion.Inverse(latchGrabActRot);
            return Quaternion.RotateTowards(Quaternion.identity, delta, pullWiggleDeg);
        }

        // What lands on the OpenGrip bone: the index finger's most distal bone, else the centroid of the near fingertip
        // leaves, else the palm (zero offset). The wrist (bone3) is the root of the hand skeleton; our prop holders
        // ("Eat*") hang off it too and are skipped. EFT names the fingers Digit11..Digit53 (first digit = finger,
        // 2 = index; second = segment). Bones farther than hand distance from the wrist are marker/attach dummies: rejected.
        private static void ResolveFingerAnchor()
        {
            fingerAnchorResolved = true;
            fingerAnchors = null;
            actingWristT = (latchedHandIsDominant ? VRGlobals.ikManager?.rightArmIk : VRGlobals.ikManager?.leftArmIk)?.solver?.bone3?.transform;
            if (actingWristT == null) return;
            Vector3 wristPos = actingWristT.position;

            Transform best = null; int bestDepth = -1;
            var leaves = new System.Collections.Generic.List<Transform>();
            var dump = new StringBuilder("[ManualEat] wrist-subtree leaves (paste if the snap still lands wrong): ");
            void Walk(Transform t, int depth)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform c = t.GetChild(i);
                    if (c.name.StartsWith("Eat", StringComparison.Ordinal)) continue; // our holders/latch
                    bool indexy = c.name.IndexOf("index", StringComparison.OrdinalIgnoreCase) >= 0
                               || c.name.IndexOf("digit2", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (indexy && depth + 1 > bestDepth) { best = c; bestDepth = depth + 1; }
                    if (c.childCount == 0)
                    {
                        leaves.Add(c);
                        dump.Append($"'{c.name}'({(c.position - wristPos).magnitude * 100f:F0}cm) ");
                    }
                    else Walk(c, depth + 1);
                }
            }
            Walk(actingWristT, 0);

            // A name match farther than a hand's length from the wrist is a misnamed dummy.
            if (best != null && (best.position - wristPos).magnitude > 0.25f) best = null;
            if (best != null) fingerAnchors = new[] { best };
            else
            {
                var kept = new System.Collections.Generic.List<Transform>();
                for (int i = 0; i < leaves.Count; i++)
                    if ((leaves[i].position - wristPos).magnitude < 0.18f) kept.Add(leaves[i]);
                if (kept.Count > 0) fingerAnchors = kept.ToArray();
            }

            LogStep(dump.ToString());
            LogStep(fingerAnchors == null
                ? "[ManualEat] pull-snap: no usable finger bones — the palm lands on the OpenGrip bone."
                : best != null
                    ? $"[ManualEat] pull-snap finger anchor: '{best.name}' ({(best.position - wristPos).magnitude * 100f:F0}cm from wrist)."
                    : $"[ManualEat] pull-snap finger anchor: centroid of {fingerAnchors.Length} near fingertip leaves.");
        }

        // The fingertip's offset in the wrist's frame, from this frame's animated curl. World-delta math (not local chains)
        // so bone scales don't skew it; valid whatever the wrist's stale world pose, the fingers being rigid descendants.
        private static Vector3 FingerOffsetInWristFrame()
        {
            if (!fingerAnchorResolved) ResolveFingerAnchor();
            if (actingWristT == null || fingerAnchors == null) return Vector3.zero;
            Vector3 sum = Vector3.zero; int n = 0;
            for (int i = 0; i < fingerAnchors.Length; i++)
            {
                Transform f = fingerAnchors[i];
                if (f == null) continue;
                sum += f.position; n++;
            }
            if (n == 0) return Vector3.zero;
            return Quaternion.Inverse(actingWristT.rotation) * (sum / n - actingWristT.position);
        }

        // Samples the packed hand path (stride 8: t, pos, quat) at normalizedTime t, clamped to the endpoints. Keys are
        // sign-aligned at capture, so Slerp takes the short way.
        private static void SampleHandPath(float[] p, float t, out Vector3 position, out Quaternion rotation)
        {
            const int S = 8;
            int n = p.Length / S;
            int hi = 0;
            while (hi < n && p[hi * S] < t) hi++;
            if (hi <= 0) { ReadPathKey(p, 0, out position, out rotation); return; }
            if (hi >= n) { ReadPathKey(p, n - 1, out position, out rotation); return; }
            ReadPathKey(p, hi - 1, out Vector3 p0, out Quaternion q0);
            ReadPathKey(p, hi, out Vector3 p1, out Quaternion q1);
            float t0 = p[(hi - 1) * S], t1 = p[hi * S];
            float w = t1 > t0 ? Mathf.Clamp01((t - t0) / (t1 - t0)) : 1f;
            position = Vector3.Lerp(p0, p1, w);
            rotation = Quaternion.Slerp(q0, q1, w);
        }

        private static void ReadPathKey(float[] p, int i, out Vector3 position, out Quaternion rotation)
        {
            int o = i * 8;
            position = new Vector3(p[o + 1], p[o + 2], p[o + 3]);
            rotation = new Quaternion(p[o + 4], p[o + 5], p[o + 6], p[o + 7]);
        }

        // No OpenGrip configured: ranks every bone under the held item by how far it moved (item-local) across the
        // completed pull and logs the top 3; the winner is the lid bone for OpenGrip(...). One report per eat.
        private static void ReportOpenMovers()
        {
            if (!logOpenMovers || moverBones == null || moverStartLocal == null || baseT == null) return;
            var ranked = new System.Collections.Generic.List<(float d, string name)>();
            for (int i = 0; i < moverBones.Length; i++)
            {
                Transform t = moverBones[i];
                if (t == null || t == baseT) continue;
                ranked.Add(((baseT.InverseTransformPoint(t.position) - moverStartLocal[i]).magnitude, t.name));
            }
            ranked.Sort((a, b) => b.d.CompareTo(a.d));
            var builder = new StringBuilder("[ManualEat] open movers (paste the lid bone into OpenGrip(...)): ");
            for (int i = 0; i < Mathf.Min(3, ranked.Count); i++) builder.Append($"'{ranked[i].name}' ({ranked[i].d * 100f:F1}cm)  ");
            Plugin.MyLog.LogInfo(builder.ToString());
            moverBones = null; moverStartLocal = null;
        }

        // Latches the reaching hand onto the bag-mouth rail: freezes its palm offset, captures the entry point and the rail
        // axis (entry -> zone center = into the bag) in the holding hand's frame, parks the latch at the hand's current
        // pose (no snap). Same glue as the pull latch: handLatched aims the IK + bone3 pin at pullLatch and gates
        // ForceLeftHandTracking via OffHandLatched.
        private static void BeginReach(EatStyle s, Transform acting, Transform anchor)
        {
            reachPalmLocal = PalmOffsetLocal(s.takeHand);
            reachEntryPalmLocal = anchor.InverseTransformPoint(acting.TransformPoint(reachPalmLocal));
            Vector3 axis = PalmOffsetLocal(Other(s.takeHand)) + takeZoneOffset - reachEntryPalmLocal;
            // Entered dead-center: no direction to aim the rail at, any stable axis works.
            reachAxisLocal = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.forward;
            reachingIn = true;
            reachGrabbed = false;
            reachProgress = 0f;

            CaptureLatchStart(s.takeHand, acting);
            latchedHandIsDominant = s.takeHand == Hand.Dominant;
            if (pullLatch == null) pullLatch = new GameObject("EatPullLatch");
            pullLatch.transform.SetParent(anchor, false);
            pullLatch.transform.position = acting.position;
            pullLatch.transform.rotation = acting.rotation;
            BeginLatchEase(anchor, acting);
            handLatched = true;

            PulseHand(latchedHandIsDominant);
            LogStep("[ManualEat] Hand locked onto the bag mouth — push in to reach.");
        }

        private static void EndReach(bool immediate = false)
        {
            if (!reachingIn) return;
            reachingIn = false;
            reachGrabbed = false;
            ReleaseLatch(immediate); // the IK re-aims at the rig target -> hand back to the controller
        }

        // Per-frame reach update (LateZeroProps tail, after DriveLatch; same frame rules: rig-target frames only, never
        // animated-bone world reads). With a baked ReachPath the hand replays the authored in-bag palm pose for the
        // current scrub time through DriveLatch's rigid chain (item -> holding wrist -> pinned target); hand and bag mouth
        // run off the same STATE_USE time. Physical depth still drives reachProgress (StepReachTake). reachRotationFree
        // keeps rotation on the controller.
        // No path = the rail fallback: the palm projected onto entry + axis * clamped depth in the holding hand's frame
        // (lateral dropped). The latch is the wrist pin target, so it is backed off the rail point by the frozen palm
        // offset (palm = latch * palmLocal); the path needs no offset, it is the palm-pin pose.
        private static void DriveReachLatch()
        {
            if (!reachingIn || !handLatched || pullLatch == null || style == null) return;
            Transform acting = HandT(style.takeHand);
            Transform anchor = pullLatch.transform.parent; // the holding hand's rig target
            if (acting == null || anchor == null) return;

            Transform holdingWrist = (latchedHandIsDominant ? VRGlobals.ikManager?.leftArmIk : VRGlobals.ikManager?.rightArmIk)?.solver?.bone3?.transform;
            if (def?.reachHandPath != null && baseT != null && holdingWrist != null)
            {
                SampleHandPath(def.reachHandPath, Mathf.Lerp(reachStartTime, reachDeepTime, reachProgress), out Vector3 lp, out Quaternion lr);
                Vector3 worldPos = anchor.TransformPoint(holdingWrist.InverseTransformPoint(baseT.TransformPoint(lp)));
                Quaternion worldRot = reachRotationFree
                    ? acting.rotation
                    : anchor.rotation * (Quaternion.Inverse(holdingWrist.rotation) * (baseT.rotation * lr));
                pullLatch.transform.SetPositionAndRotation(worldPos, worldRot);
                // The authored roll: the wiggle turns the hand about the palm in the bag
                ShapeLatch(!reachRotationFree, reachPalmLocal);
                return;
            }

            Vector3 palmLocal = anchor.InverseTransformPoint(acting.TransformPoint(reachPalmLocal));
            float depth = Mathf.Clamp(Vector3.Dot(palmLocal - reachEntryPalmLocal, reachAxisLocal), 0f, reachDepthDistance);
            Vector3 railPalmWorld = anchor.TransformPoint(reachEntryPalmLocal + reachAxisLocal * depth);
            Quaternion rotation = acting.rotation;
            pullLatch.transform.rotation = rotation;
            pullLatch.transform.position = railPalmWorld - rotation * reachPalmLocal;
            ShapeLatch(false, Vector3.zero); // the rail's rotation is the controller's already: only the ease
        }
    }
}
