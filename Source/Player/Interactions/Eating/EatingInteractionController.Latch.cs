using EFT.InventoryLogic;
using RootMotion.FinalIK;
using System;
using System.Text;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using static EFT.Player;

namespace TarkovVR.Source.Player.Interactions
{
    // The hand-latch toolkit: glue a rendered hand onto the held item (pull latch),
    // ride a clip-animated bone (OpenGrip), replay a baked palm path (HandPath), or
    // slide along the reach rail (ReachBag). This is the reusable core for future
    // manual interactions (meds, gun manipulation) - see CLAUDE.md's design notes.
    internal static partial class EatingInteractionController
    {
        // Latch the acting hand onto the held item: park a latch transform at the hand's
        // CURRENT pose, parented to the holding hand's rig target so it rides that
        // controller's full 6dof. DriveArms aims the latched arm's IK at it and the bone3
        // pin glues the rendered wrist to it (Left/RightPinTarget), so the hand stays
        // locked on the item no matter where your physical hand pulls — the pull only
        // drives the scrub. The acting RIG target keeps controller-tracking throughout.
        private static void LatchHandToItem(Hand h, Transform acting, Transform anchor)
        {
            latchedHandIsDominant = h == Hand.Dominant;
            if (pullLatch == null) pullLatch = new GameObject("EatPullLatch");
            pullLatch.transform.SetParent(anchor, false);
            pullLatch.transform.position = acting.position;
            pullLatch.transform.rotation = acting.rotation;
            latchGrabActRot = acting.rotation;                     // wiggle reference
            latchGrabLocalRot = pullLatch.transform.localRotation; // freeze-mode rotation anchor (rides the item)

            // OpenGrip: the hand rides the lid/cover bone from here (DriveLatch). With
            // pullSnapToGrip it JUMPS onto the bone (+ the tunable pullSnapPos offset);
            // otherwise it keeps the offset from where you pressed trigger. Re-grabs
            // capture against the lid's current mid-roll pose.
            latchRidesProp = openGripT != null;
            if (latchRidesProp)
            {
                if (pullSnapToGrip)
                    propGrabLocalPos = pullSnapPos; // DriveLatch puts the FINGERTIP on the bone this same frame (pre-IK)
                else
                    propGrabLocalPos = openGripT.InverseTransformPoint(pullLatch.transform.position);
                propGrabLocalRot = Quaternion.Inverse(openGripT.rotation) * pullLatch.transform.rotation;
            }
            // No ride bone configured: snapshot the held item's bones once (first grab =
            // the closed pose) so a completed pull can rank & log the real lid bone.
            else if (logOpenMovers && moverBones == null && baseT != null)
            {
                moverBones = baseT.GetComponentsInChildren<Transform>(true);
                moverStartLocal = new Vector3[moverBones.Length];
                for (int i = 0; i < moverBones.Length; i++)
                    moverStartLocal[i] = baseT.InverseTransformPoint(moverBones[i].position);
            }
            handLatched = true;
        }

        private static void EndPull()
        {
            pullingOpen = false;
            pullSqueezeDone = false;  // per-grab: the next grab squeezes again before the physical part
            pullAwaitRelease = false;
            handLatched = false; // DriveArms re-aims the IK at the rig target next Tick -> hand snaps back
            if (pullLatch != null) { UnityEngine.Object.Destroy(pullLatch); pullLatch = null; }
        }

        // Per-frame latch update. Runs at the END of LateZeroProps — AFTER the prop
        // re-zeroing — and works ONLY in relative frames: in Player.LateUpdate every bone
        // WORLD pose is the PRE-pin animated pose (the animator just re-stomped the arms
        // AND the prop roots; the visible pose only exists after FinalIK + the wrist pins
        // later this frame). Reading openGripT.position directly here anchored the hand to
        // wherever the ANIMATION's holding wrist was — "nowhere near the item" (observed;
        // never read bone world poses in this window). So: the grip pose is taken in the
        // holding WRIST's local frame — the chain wrist -> holder -> item -> lid is rigid
        // within the frame, valid whatever the wrist's world pose is — and re-anchored on
        // the holding RIG TARGET, which is exactly where that wrist gets pinned, i.e.
        // where the can visibly sits.
        //  * pullRotationFree: ROTATION stays live on the player's controller (full 3dof
        //    twist) even while the position is locked or riding; otherwise it rides the
        //    bone with the offset captured at the grab.
        private static void DriveLatch()
        {
            if (!pullingOpen || !handLatched || pullLatch == null) return;
            Transform holdingTarget = pullLatch.transform.parent; // the holding hand's rig target (latch anchor)
            Transform holdingWrist = (latchedHandIsDominant ? VRGlobals.ikManager?.leftArmIk : VRGlobals.ikManager?.rightArmIk)?.solver?.bone3?.transform;

            // A Trigger-kind open (soda tab) has no gradual physical gesture — the axis can
            // sweep the whole scrub in a frame, and replaying the authored path/bone ride
            // teleports the latched hand through it (observed: "hand jumps to the wrong
            // spot" on the press). Keep the freeze-at-grab latch instead: the hand stays
            // where it grabbed (rotation live via pullRotationFree) while the clip cracks
            // the tab under it.
            bool triggerKind = def != null && def.openGesture == OpenGestureKind.Trigger;

            // Baked HandPath: replay the authored palm pose for the current scrub time —
            // sampled in ITEM-root space off the vanilla clip, re-anchored through the same
            // rigid chain (item -> holding wrist -> pinned target). The path IS the palm-pin
            // pose, so no fingertip offset applies. Before the first key (the clip's hand
            // arriving at the item — the draw arc is trimmed at capture) it holds that key.
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
                else
                    worldRot = WiggleDelta() * worldRot; // authored rotation + clamped controller wiggle
                pullLatch.transform.SetPositionAndRotation(worldPos, worldRot);
                return;
            }

            bool riding = !triggerKind && latchRidesProp && openGripT != null && holdingTarget != null && holdingWrist != null;

            // Rotation first — the fingertip offset below rotates with the hand. Anchor =
            // the ridden bone's rotation (riding) or the grab pose riding the item (frozen),
            // plus the clamped controller wiggle; pullRotationFree = fully controller-driven.
            Quaternion rot;
            if (pullRotationFree)
            {
                Transform act = latchedHandIsDominant ? RightHand() : LeftHand();
                rot = act != null ? act.rotation : pullLatch.transform.rotation;
            }
            else if (riding)
                rot = WiggleDelta() * (holdingTarget.rotation * (Quaternion.Inverse(holdingWrist.rotation) * openGripT.rotation) * propGrabLocalRot);
            else
                rot = WiggleDelta() * (holdingTarget != null ? holdingTarget.rotation * latchGrabLocalRot : pullLatch.transform.rotation);
            pullLatch.transform.rotation = rot;

            if (riding)
            {
                // lid -> holding-wrist-local (rigid) -> re-anchored where the wrist is PINNED.
                Vector3 gripLocal = holdingWrist.InverseTransformPoint(openGripT.TransformPoint(propGrabLocalPos));
                Vector3 gripWorld = holdingTarget.TransformPoint(gripLocal);
                // The latch is the WRIST pin target: back the wrist off the grip point by
                // the fingertip's offset so the FINGERTIP (not the palm) lands on it.
                pullLatch.transform.position = gripWorld
                    - (pullSnapToGrip ? rot * FingerOffsetInWristFrame() : Vector3.zero);
            }
        }

        // The controller's rotation deviation from its grab-moment orientation, clamped to
        // pullWiggleDeg — the bounded "wiggle" applied on top of an anchored rotation so the
        // latched hand responds to your wrist without leaving the authored pose.
        private static Quaternion WiggleDelta()
        {
            if (pullWiggleDeg <= 0f) return Quaternion.identity;
            Transform act = latchedHandIsDominant ? RightHand() : LeftHand();
            if (act == null) return Quaternion.identity;
            Quaternion delta = act.rotation * Quaternion.Inverse(latchGrabActRot);
            return Quaternion.RotateTowards(Quaternion.identity, delta, pullWiggleDeg);
        }

        // What lands ON the OpenGrip bone: the index finger's most distal bone, else the
        // centroid of the hand's NEAR fingertip leaves, else the palm (zero offset). The
        // wrist (bone3) is the root of the whole hand skeleton, so its subtree IS the
        // fingers — minus our own prop holders ("Eat*"), which hang off the same bone.
        // EFT names the fingers Digit11..Digit53 (first digit = finger, 2 = index; second =
        // segment), so "digit2" is matched alongside "index". Anything farther than hand
        // distance from the wrist is a marker/attach dummy, not a finger — rejected, or the
        // centroid lands metres off the item (observed).
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

            // Sanity: a finger is hand-sized close to the wrist; a "match" farther out is a
            // misnamed dummy.
            if (best != null && (best.position - wristPos).magnitude > 0.25f) best = null;
            if (best != null) fingerAnchors = new[] { best };
            else
            {
                var kept = new System.Collections.Generic.List<Transform>();
                for (int i = 0; i < leaves.Count; i++)
                    if ((leaves[i].position - wristPos).magnitude < 0.18f) kept.Add(leaves[i]);
                if (kept.Count > 0) fingerAnchors = kept.ToArray();
            }

            Plugin.MyLog.LogInfo(dump.ToString());
            Plugin.MyLog.LogInfo(fingerAnchors == null
                ? "[ManualEat] pull-snap: no usable finger bones — the palm lands on the OpenGrip bone."
                : best != null
                    ? $"[ManualEat] pull-snap finger anchor: '{best.name}' ({(best.position - wristPos).magnitude * 100f:F0}cm from wrist)."
                    : $"[ManualEat] pull-snap finger anchor: centroid of {fingerAnchors.Length} near fingertip leaves.");
        }

        // The fingertip's offset in the wrist's frame, from THIS frame's animated curl.
        // World-delta math (not local chains) so bone scales don't skew it; valid whatever
        // the wrist's stale world pose is, because the fingers are rigid descendants of it.
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

        // Sample the packed open-hand path (stride 8: t, pos, quat) at normalizedTime t,
        // clamped to the endpoints; between keys position lerps and rotation slerps (keys
        // are sign-aligned at capture, so Slerp takes the short way).
        private static void SampleHandPath(float[] p, float t, out Vector3 pos, out Quaternion rot)
        {
            const int S = 8;
            int n = p.Length / S;
            int hi = 0;
            while (hi < n && p[hi * S] < t) hi++;
            if (hi <= 0) { ReadPathKey(p, 0, out pos, out rot); return; }
            if (hi >= n) { ReadPathKey(p, n - 1, out pos, out rot); return; }
            ReadPathKey(p, hi - 1, out Vector3 p0, out Quaternion q0);
            ReadPathKey(p, hi, out Vector3 p1, out Quaternion q1);
            float t0 = p[(hi - 1) * S], t1 = p[hi * S];
            float w = t1 > t0 ? Mathf.Clamp01((t - t0) / (t1 - t0)) : 1f;
            pos = Vector3.Lerp(p0, p1, w);
            rot = Quaternion.Slerp(q0, q1, w);
        }

        private static void ReadPathKey(float[] p, int i, out Vector3 pos, out Quaternion rot)
        {
            int o = i * 8;
            pos = new Vector3(p[o + 1], p[o + 2], p[o + 3]);
            rot = new Quaternion(p[o + 4], p[o + 5], p[o + 6], p[o + 7]);
        }

        // No OpenGrip configured: rank every bone under the held item by how far it moved
        // (item-local) across the completed pull and log the top 3 — the winner is the lid
        // bone name to paste into OpenGrip(...). One report per eat.
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
            var sb = new StringBuilder("[ManualEat] open movers (paste the lid bone into OpenGrip(...)): ");
            for (int i = 0; i < Mathf.Min(3, ranked.Count); i++) sb.Append($"'{ranked[i].name}' ({ranked[i].d * 100f:F1}cm)  ");
            Plugin.MyLog.LogInfo(sb.ToString());
            moverBones = null; moverStartLocal = null;
        }

        // Latch the reaching hand onto the bag-mouth rail: freeze its palm offset, capture
        // the entry point and the rail axis (entry -> zone center = INTO the bag) in the
        // holding hand's frame, and park the latch at the hand's current pose (no snap —
        // the rail starts where you are). Reuses the pull-latch glue: handLatched aims the
        // IK + bone3 pin at pullLatch (Left/RightPinTarget) and gates ForceLeftHandTracking
        // via OffHandLatched, exactly like the pull-open.
        private static void BeginReach(EatStyle s, Transform acting, Transform anchor)
        {
            reachPalmLocal = PalmOffsetLocal(s.takeHand);
            reachEntryPalmLocal = anchor.InverseTransformPoint(acting.TransformPoint(reachPalmLocal));
            Vector3 axis = PalmOffsetLocal(Other(s.takeHand)) + takeZoneOffset - reachEntryPalmLocal;
            // Entered dead-center: no direction to aim the rail at — any stable axis works
            // (you're already at full entry; the first push defines nothing better).
            reachAxisLocal = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.forward;
            reachingIn = true;
            reachGrabbed = false;
            reachProgress = 0f;

            latchedHandIsDominant = s.takeHand == Hand.Dominant;
            if (pullLatch == null) pullLatch = new GameObject("EatPullLatch");
            pullLatch.transform.SetParent(anchor, false);
            pullLatch.transform.position = acting.position;
            pullLatch.transform.rotation = acting.rotation;
            handLatched = true;

            PulseHand(latchedHandIsDominant);
            Plugin.MyLog.LogInfo("[ManualEat] Hand locked onto the bag mouth — push in to reach.");
        }

        private static void EndReach()
        {
            if (!reachingIn) return;
            reachingIn = false;
            reachGrabbed = false;
            handLatched = false; // the IK re-aims at the rig target next Tick -> hand snaps back to the controller
            if (pullLatch != null) { UnityEngine.Object.Destroy(pullLatch); pullLatch = null; }
        }

        // Per-frame reach update (LateZeroProps tail, right after DriveLatch — same frame
        // rules: rig-target frames only, never animated-bone world reads). With a baked
        // ReachPath the rendered hand replays the AUTHORED in-bag palm pose for the
        // current scrub time, re-anchored through the same rigid chain as DriveLatch's
        // HandPath branch (item -> holding wrist -> pinned target) — the hand dives along
        // the clip's curve and can't drift from the bag-mouth animation, because both are
        // driven off the SAME STATE_USE time. The physical depth still drives
        // reachProgress (StepReachTake); the path only decides WHERE the rendered hand
        // sits for that depth. Rotation stays live on the controller (reachRotationFree —
        // reaching into a bag, the wrist is yours) unless A/B'd off to the authored roll.
        // No path baked = the synthetic RAIL fallback: the reaching palm projected onto
        // entry + axis * clamped depth in the holding hand's frame (lateral wobble
        // dropped). The latch is the WRIST pin target, so the rail backs it off the rail
        // point by the frozen palm offset (bone3 is pinned position+rotation, so
        // palm = latch * palmLocal); the path needs no offset — it IS the palm-pin pose.
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
                return;
            }

            Vector3 palmLocal = anchor.InverseTransformPoint(acting.TransformPoint(reachPalmLocal));
            float depth = Mathf.Clamp(Vector3.Dot(palmLocal - reachEntryPalmLocal, reachAxisLocal), 0f, reachDepthDistance);
            Vector3 railPalmWorld = anchor.TransformPoint(reachEntryPalmLocal + reachAxisLocal * depth);
            Quaternion rot = acting.rotation;
            pullLatch.transform.rotation = rot;
            pullLatch.transform.position = railPalmWorld - rot * reachPalmLocal;
        }
    }
}
