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
    // Rig plumbing shared by every archetype: arm IK targets + the post-IK wrist
    // pins, body-follow, palm collision probes, the interaction-zone math (+ debug
    // viz spheres), trigger reads, haptic pulses, and the sound gate.
    internal static partial class EatingInteractionController
    {
        //--- Arms / props -----------------------------------------------------------
        private static void DriveArms()
        {
            // A pull-open latches ONE hand onto the held item (handLatched): aim that arm's IK
            // at the latch transform (glued to the holding hand's rig target) instead of the
            // controller, so the rendered hand rides the item's 6dof; the other stays normal.
            // Both IKs stay enabled — the pin (Left/RightPinTarget) finishes the 1:1 glue.
            SetArmIk(VRGlobals.ikManager.rightArmIk, RightPinTarget());
            SetArmIk(VRGlobals.ikManager.leftArmIk, LeftPinTarget());
            DriveElbowBends();
        }

        // Re-point the arms' elbow bend goals at the VR goals each frame (like gun/empty-hands).
        // Meds spawns _elbowBends at its OWN goals (animation pose) -> elbows bend wrong. The
        // index->hand mapping flips with handedness.
        private static void DriveElbowBends()
        {
            var p = VRGlobals.player;
            if (p == null || p._elbowBends == null || p._elbowBends.Length < 2) return;
            if (VRGlobals.leftArmBendGoal == null || VRGlobals.rightArmBendGoal == null) return;
            if (VRSettings.GetLeftHandedMode())
            {
                p._elbowBends[0] = VRGlobals.rightArmBendGoal;
                p._elbowBends[1] = VRGlobals.leftArmBendGoal;
            }
            else
            {
                p._elbowBends[0] = VRGlobals.leftArmBendGoal;
                p._elbowBends[1] = VRGlobals.rightArmBendGoal;
            }
        }

        private static void ReleaseArms()
        {
            if (VRGlobals.ikManager == null) return;
            if (VRGlobals.ikManager.rightArmIk != null) { VRGlobals.ikManager.rightArmIk.solver.target = null; VRGlobals.ikManager.rightArmIk.enabled = false; }
            if (VRGlobals.ikManager.leftArmIk != null) { VRGlobals.ikManager.leftArmIk.solver.target = null; VRGlobals.ikManager.leftArmIk.enabled = false; }
        }

        private static void SetArmIk(LimbIK ik, Transform target)
        {
            if (ik == null || target == null) return;
            if (ik.solver.target != target) ik.solver.target = target;
            if (!ik.enabled) ik.enabled = true;
        }

        // Pin the rig root (camRoot) to the body anchor (the meds object, kept on the ribcage),
        // like HandsPositioner does for empty hands — the coupling that's missing mid-eat.
        private static void PinRigToBody()
        {
            if (medsBody == null || VRGlobals.camRoot == null || VRGlobals.player == null) return;
            VRGlobals.camRoot.transform.position = new Vector3(
                medsBody.position.x,
                VRGlobals.player.Transform.position.y + 1.5f,
                medsBody.position.z);
        }

        // Hand-pin runs from the IK solver's OnPostUpdate — Player.LateUpdate is too early (FinalIK
        // solves after it). Pins the rendered wrist (bone3) onto the smooth target, no walk lag.
        // A pull-latched hand pins to the LATCH instead (glued to the item) — same 1:1 pin,
        // different destination, so nothing else can drag it back to the controller.
        private static void PinLeftAfterIk() { if (active && !manualDone && driveHandsToTargets) PinBoneToTarget(VRGlobals.ikManager?.leftArmIk, LeftPinTarget()); }
        private static void PinRightAfterIk() { if (active && !manualDone && driveHandsToTargets) PinBoneToTarget(VRGlobals.ikManager?.rightArmIk, RightPinTarget()); }

        // Where each hand's IK target + wrist pin should aim right now: the pull latch while
        // that hand is latched onto the held item, else its normal rig target.
        private static Transform LeftPinTarget() => handLatched && !latchedHandIsDominant && pullLatch != null ? pullLatch.transform : LeftHand();
        private static Transform RightPinTarget() => handLatched && latchedHandIsDominant && pullLatch != null ? pullLatch.transform : RightHand();

        private static void SubscribePinAfterIk()
        {
            var l = VRGlobals.ikManager?.leftArmIk?.solver;
            var r = VRGlobals.ikManager?.rightArmIk?.solver;
            if (l != null) { l.OnPostUpdate -= PinLeftAfterIk; l.OnPostUpdate += PinLeftAfterIk; }
            if (r != null) { r.OnPostUpdate -= PinRightAfterIk; r.OnPostUpdate += PinRightAfterIk; }
        }

        private static void UnsubscribePinAfterIk()
        {
            var l = VRGlobals.ikManager?.leftArmIk?.solver;
            var r = VRGlobals.ikManager?.rightArmIk?.solver;
            if (l != null) l.OnPostUpdate -= PinLeftAfterIk;
            if (r != null) r.OnPostUpdate -= PinRightAfterIk;
        }

        private static void PinBoneToTarget(LimbIK ik, Transform target)
        {
            if (ik == null || target == null) return;
            Transform bone = ik.solver?.bone3?.transform;
            if (bone == null) return;
            bone.position = target.position;
            bone.rotation = target.rotation;
        }

        //--- Helpers ----------------------------------------------------------------
        private static Transform RightHand() => VRGlobals.vrPlayer?.RightHand != null ? VRGlobals.vrPlayer.RightHand.transform : null;
        private static Transform LeftHand() => VRGlobals.vrPlayer?.LeftHand != null ? VRGlobals.vrPlayer.LeftHand.transform : null;

        // Resolve a hand role to its transform. RightHand is ALWAYS the dominant hand and
        // LeftHand the off hand (the SteamVR pose listeners swap in left-handed mode), so no
        // handedness branch is needed here.
        private static Transform HandT(Hand h) => h == Hand.Dominant ? RightHand() : LeftHand();
        private static Hand Other(Hand h) => h == Hand.Dominant ? Hand.Off : Hand.Dominant;

        //--- Palm collision points ---------------------------------------------------
        // The palm point every probe/anchor now uses (palmCollisionPoints): the fingertip-
        // leaf centroid of the RENDERED hand, expressed in the wrist (bone3) frame. bone3
        // is pinned 1:1 onto the rig transform each frame (PinBoneToTarget), so a wrist-
        // local offset IS a rig-local offset and rotates rigidly with the controller.
        // World-DELTA math like FingerOffsetInWristFrame — valid whatever the wrist's stale
        // world pose is (the fingers are rigid descendants of it), so it's safe from both
        // the Update (Tick) and LateUpdate (LateZeroProps/viz) callers.
        private static bool palmAnchorsResolved;
        private static Transform palmWristDom, palmWristOff;     // bone3 per arm
        private static Transform[] palmLeavesDom, palmLeavesOff; // near fingertip leaves

        // Dominant always maps to the RIGHT arm: the handedness swap happens at the SteamVR
        // pose listeners, and the pins aim rightArmIk/leftArmIk at RightHand/LeftHand
        // unconditionally (Right/LeftPinTarget).
        private static void ResolvePalmAnchors()
        {
            palmAnchorsResolved = true;
            ResolvePalmLeaves(VRGlobals.ikManager?.rightArmIk, out palmWristDom, out palmLeavesDom);
            ResolvePalmLeaves(VRGlobals.ikManager?.leftArmIk, out palmWristOff, out palmLeavesOff);
            Plugin.MyLog.LogInfo("[ManualEat] palm anchors: dom="
                + (palmLeavesDom != null ? palmLeavesDom.Length : 0) + " leaves, off="
                + (palmLeavesOff != null ? palmLeavesOff.Length : 0) + " leaves.");
        }

        // Same filtering as ResolveFingerAnchor: the wrist subtree IS the hand, minus our
        // own Eat* holders; leaves farther than hand distance from the wrist are attach/
        // marker dummies, not fingers — kept out or the centroid lands off the hand.
        private static void ResolvePalmLeaves(LimbIK ik, out Transform wrist, out Transform[] leaves)
        {
            wrist = ik?.solver?.bone3?.transform;
            leaves = null;
            if (wrist == null) return;
            Vector3 wristPos = wrist.position;
            var kept = new System.Collections.Generic.List<Transform>();
            void Walk(Transform t)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform c = t.GetChild(i);
                    if (c.name.StartsWith("Eat", StringComparison.Ordinal)) continue; // our holders/latch
                    if (c.childCount == 0)
                    {
                        if ((c.position - wristPos).magnitude < 0.18f) kept.Add(c);
                    }
                    else Walk(c);
                }
            }
            Walk(wrist);
            if (kept.Count > 0) leaves = kept.ToArray();
        }

        // A hand's palm offset in its RIG transform's local frame. Zero (= the old origin
        // behavior) when the feature is off; the static fallback if the skeleton is missing.
        private static Vector3 PalmOffsetLocal(Hand h)
        {
            if (!palmCollisionPoints) return Vector3.zero;
            if (!palmAnchorsResolved) ResolvePalmAnchors();
            Transform wrist = h == Hand.Dominant ? palmWristDom : palmWristOff;
            Transform[] leaves = h == Hand.Dominant ? palmLeavesDom : palmLeavesOff;
            if (wrist == null || leaves == null) return palmFallbackLocal;
            Vector3 sum = Vector3.zero; int n = 0;
            for (int i = 0; i < leaves.Length; i++)
            {
                if (leaves[i] == null) continue;
                sum += leaves[i].position; n++;
            }
            if (n == 0) return palmFallbackLocal;
            return Quaternion.Inverse(wrist.rotation) * (sum / n - wrist.position);
        }

        // The world point a hand reaches WITH. Call only with a live hand (HandT != null).
        private static Vector3 ProbePoint(Hand h) => HandT(h).TransformPoint(PalmOffsetLocal(h));

        // A reach gesture fires when the acting hand's PALM probe is within <radius> of the
        // anchor hand's PALM + local offset. Offset zero => the holding palm itself (where
        // the held item sits) = the original hand-to-hand trigger, now palm-to-palm. The
        // offset is in the anchor rig transform's local frame, so it rides the held item
        // (e.g. up a bottle's axis to its cap).
        private static bool ZoneReached(Hand acting, Hand anchor, Vector3 offset, float radius)
        {
            Transform a = HandT(acting), b = HandT(anchor);
            if (a == null || b == null) return false;
            return Vector3.Distance(ProbePoint(acting), b.TransformPoint(PalmOffsetLocal(anchor) + offset)) < radius;
        }

        // For "bring the held ITEM to your mouth" eats — any eat where the eating hand IS
        // the hand holding the item (drinks, wrapper bars, timed wrapper eats, a CanSpoon's
        // can-at-mouth drink) — probe the mouth with the OPEN zone's item point (palm +
        // openZoneOffset: the cap/lid/bar top, already tuned per food) instead of the bare
        // palm/eatZoneOffset, so it's the item's business end that must reach the mouth.
        // Foods where a PIECE is carried to the mouth in the free hand (spoon scoops,
        // CanHand/Bag/Pack/ReachBag) keep the palm probe — there the piece sits ON the palm.
        // Sausage (skipOpen, no open zone) keeps the old behavior. false = palm everywhere.
        public static bool mouthProbeUsesOpenZone = true;

        private static Vector3 EatProbeOffset(Hand eatHand)
        {
            if (mouthProbeUsesOpenZone && style != null && def != null && !def.skipOpen
                && eatHand == Other(style.openHand)) // the eating hand holds the item
                return openZoneOffset;
            return eatZoneOffset;
        }

        // The eat gesture: the eating hand's PALM probe (+ EatProbeOffset — eatZoneOffset,
        // or the open zone's item point for held-item-to-mouth foods) is near the MOUTH
        // (head + mouthLocalOffset — the camera is at the eyes, the mouth is below it) and
        // roughly in front of the face. The forward gate keeps behind-the-head positions
        // from triggering. radiusScale widens the boundary for hysteresis (the drink hold
        // passes drinkZoneExitScale while held so the clock can't flicker at the edge).
        private static bool EatZoneReached(Hand eatHand, float radiusScale = 1f)
        {
            Transform head = GetHead();
            Transform hand = HandT(eatHand);
            if (head == null || hand == null) return false;
            Vector3 delta = hand.TransformPoint(PalmOffsetLocal(eatHand) + EatProbeOffset(eatHand)) - head.TransformPoint(mouthLocalOffset);
            if (delta.magnitude > eatDistance * radiusScale) return false;
            return Vector3.Dot(delta.normalized, head.forward) > mouthForwardDot;
        }

        private static Transform GetHead()
        {
            if (VRGlobals.VRCam != null) return VRGlobals.VRCam.transform;
            return Camera.main != null ? Camera.main.transform : null;
        }

        //--- Zone debug visualization -------------------------------------------------
        // Toggle debugZones in UnityExplorer while eating. Translucent spheres show each
        // zone EXACTLY as the trigger math sees it (same palm anchor + offset + radius) and
        // turn GREEN while that zone's trigger condition is actually satisfied (computed by
        // the same ZoneReached/EatZoneReached the gestures use, forward gate included) — so
        // if a sphere ever looks bigger than the zone feels, the light-up shows the true
        // boundary. Small opaque dots in the zone's color mark each sphere's exact CENTER,
        // and bright green dots the PALM probe points that must enter them (these ride the
        // hand's rotation). Only the zones the current phase can trigger are drawn. Colors:
        // BLUE=open, YELLOW=take, RED=mouth. A Bag's shake gate draws as the take sphere at
        // its REAL radius (shakeNearDistance, hand-to-hand) instead of the unused scoop zone.
        public static bool debugZones = false;

        private static GameObject zoneOpenViz, zoneTakeViz, zoneEatViz;
        private static GameObject zoneOpenDot, zoneTakeDot, zoneEatDot;
        private static GameObject probeHandViz, probeEatViz;

        private static void UpdateZoneViz()
        {
            if (!debugZones || manualDone || style == null)
            {
                DestroyZoneViz();
                return;
            }
            EatStyle s = style;
            Transform head = GetHead();
            Transform openAnchor = HandT(Other(s.openHand)); // the held item rides this hand
            bool shakeTake = s.takeKind == TakeKind.Shake;
            // The shake gate is palm-to-palm at shakeNearDistance: zone on the pouring (off)
            // palm, probed by the bag (dominant) palm — mirror of DetectShake.
            Hand takeProbeHand = shakeTake ? Hand.Dominant : s.takeHand;
            Hand takeAnchorHand = shakeTake ? Hand.Off : Other(s.takeHand);
            Transform takeAnchor = HandT(takeAnchorHand);
            float takeRadius = shakeTake ? shakeNearDistance : scoopDistance;
            Vector3 takeOffset = shakeTake ? Vector3.zero : takeZoneOffset;

            // Which zones can fire right now (mirror of StepGesture's routing). Drinks keep
            // the open zone live in Ready — that's the recap/done trigger.
            bool showOpen = phase == Phase.Closed || (s.timedSip && phase == Phase.Ready);
            bool showTake = phase == Phase.Ready && !s.timedSip && s.hasTakeStep;
            // mouthDrink: a CanSpoon's can-at-the-mouth drink is live from Ready too.
            bool showEat = phase == Phase.Holding
                || (phase == Phase.Ready && (s.timedSip || !s.hasTakeStep || (def != null && def.mouthDrink)));

            // Centers + lit states, all through the live trigger math.
            Vector3 openC = openAnchor != null ? openAnchor.TransformPoint(PalmOffsetLocal(Other(s.openHand)) + openZoneOffset) : Vector3.zero;
            Vector3 takeC = takeAnchor != null ? takeAnchor.TransformPoint(PalmOffsetLocal(takeAnchorHand) + takeOffset) : Vector3.zero;
            Vector3 eatC = head != null ? head.TransformPoint(mouthLocalOffset) : Vector3.zero;
            bool openLit = showOpen && ZoneReached(s.openHand, Other(s.openHand), openZoneOffset, openDistance);
            bool takeLit = showTake && (shakeTake
                ? takeAnchor != null && HandT(Hand.Dominant) != null && Vector3.Distance(ProbePoint(Hand.Dominant), takeC) < takeRadius
                : ZoneReached(s.takeHand, Other(s.takeHand), takeZoneOffset, scoopDistance));
            bool eatLit = showEat && EatZoneReached(s.eatHand);

            SetZoneViz(ref zoneOpenViz, "VRZoneOpen", showOpen && openAnchor != null,
                openC, openDistance, ZoneColor(new Color(0.25f, 0.55f, 1f, 0.25f), openLit));
            SetZoneViz(ref zoneTakeViz, "VRZoneTake", showTake && takeAnchor != null,
                takeC, takeRadius, ZoneColor(new Color(1f, 0.85f, 0.2f, 0.25f), takeLit));
            SetZoneViz(ref zoneEatViz, "VRZoneEat", showEat && head != null,
                eatC, eatDistance, ZoneColor(new Color(1f, 0.25f, 0.25f, 0.25f), eatLit));

            // Zone CENTER dots (opaque, zone-colored) — make the sphere's true middle/size
            // judgeable in the headset.
            SetZoneViz(ref zoneOpenDot, "VRZoneOpenDot", showOpen && openAnchor != null, openC, 0.01f, new Color(0.25f, 0.55f, 1f, 0.95f));
            SetZoneViz(ref zoneTakeDot, "VRZoneTakeDot", showTake && takeAnchor != null, takeC, 0.01f, new Color(1f, 0.85f, 0.2f, 0.95f));
            SetZoneViz(ref zoneEatDot, "VRZoneEatDot", showEat && head != null, eatC, 0.01f, new Color(1f, 0.25f, 0.25f, 0.95f));

            // Probe points (what must enter a sphere): the acting PALM for open/take, the
            // eat palm + eatZoneOffset for the mouth.
            Hand probeHand = showTake ? takeProbeHand : s.openHand;
            Transform probeT = HandT(probeHand);
            SetZoneViz(ref probeHandViz, "VRZoneProbeHand", (showOpen || showTake) && probeT != null,
                probeT != null ? ProbePoint(probeHand) : Vector3.zero, 0.015f, new Color(0.2f, 1f, 0.3f, 0.9f));
            Transform eatHand = HandT(s.eatHand);
            SetZoneViz(ref probeEatViz, "VRZoneProbeEat", showEat && eatHand != null,
                eatHand != null ? eatHand.TransformPoint(PalmOffsetLocal(s.eatHand) + EatProbeOffset(s.eatHand)) : Vector3.zero, 0.015f, new Color(0.2f, 1f, 0.3f, 0.9f));
        }

        // The zone shell's color: its base hue, or solid-ish green while the trigger
        // condition is satisfied.
        private static Color ZoneColor(Color baseC, bool lit) => lit ? new Color(0.3f, 1f, 0.4f, 0.45f) : baseC;

        // Create/position one debug sphere. scale = diameter (the spheres are unit-sized).
        // Color is re-applied every call (the lit state changes frame to frame).
        private static void SetZoneViz(ref GameObject go, string name, bool show, Vector3 pos, float radius, Color c)
        {
            if (!show) { if (go != null) go.SetActive(false); return; }
            if (go == null)
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = name;
                UnityEngine.Object.Destroy(go.GetComponent<Collider>()); // visual only — no physics
                Renderer r = go.GetComponent<Renderer>();
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                // A shader that supports alpha; Sprites/Default ships with every Unity build.
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Transparent/Diffuse");
                if (sh == null) sh = Shader.Find("Standard");
                r.material = new Material(sh) { color = c };
            }
            go.SetActive(true);
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * (radius * 2f);
            go.GetComponent<Renderer>().material.color = c;
        }

        private static void DestroyZoneViz()
        {
            if (zoneOpenViz != null) UnityEngine.Object.Destroy(zoneOpenViz);
            if (zoneTakeViz != null) UnityEngine.Object.Destroy(zoneTakeViz);
            if (zoneEatViz != null) UnityEngine.Object.Destroy(zoneEatViz);
            if (zoneOpenDot != null) UnityEngine.Object.Destroy(zoneOpenDot);
            if (zoneTakeDot != null) UnityEngine.Object.Destroy(zoneTakeDot);
            if (zoneEatDot != null) UnityEngine.Object.Destroy(zoneEatDot);
            if (probeHandViz != null) UnityEngine.Object.Destroy(probeHandViz);
            if (probeEatViz != null) UnityEngine.Object.Destroy(probeEatViz);
            zoneOpenViz = zoneTakeViz = zoneEatViz = null;
            zoneOpenDot = zoneTakeDot = zoneEatDot = null;
            probeHandViz = probeEatViz = null;
        }

        // The PHYSICAL controller for a hand role: dominant = the right hand in normal
        // mode, the off hand the other one; left-handed mode swaps. The ONE role->physical
        // mapping shared by the trigger reads and haptics below.
        private static SteamVR_Input_Sources HandSource(bool dominant)
        {
            bool useLeft = dominant ? VRSettings.GetLeftHandedMode() : !VRSettings.GetLeftHandedMode();
            return useLeft ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
        }

        // Trigger rising-edge for the opening hand. dominant=true => the dominant (can-opening)
        // hand's trigger; false => the off hand's (wrapper/bag/pack opening). Tracks each hand's
        // previous axis separately so the two edges don't interfere.
        private static bool TriggerEdgeImpl(bool dominant)
        {
            float axis = TriggerAxisImpl(dominant);
            if (dominant)
            {
                bool c = axis > 0.5f && prevTriggerAxis <= 0.5f;
                prevTriggerAxis = axis;
                return c;
            }
            bool co = axis > 0.5f && prevOffTriggerAxis <= 0.5f;
            prevOffTriggerAxis = axis;
            return co;
        }

        // Raw trigger axis for a hand role (the open squeeze/latch scrubs off this);
        // doesn't touch the prev-axis edge state (TriggerEdgeImpl keeps working independently).
        private static float TriggerAxisImpl(bool dominant)
        {
            SteamVR_Input_Sources src = HandSource(dominant);
            SteamVR_Action_Single trig = src == SteamVR_Input_Sources.LeftHand
                ? SteamVR_Actions._default.LeftTrigger : SteamVR_Actions._default.RightTrigger;
            return trig.GetAxis(src);
        }

        // Haptic click on the dominant controller (the historical default pulse).
        private static void Pulse() => PulseHand(true);

        // Haptic on a specific hand role (the pull-open's acting hand is the OFF hand for
        // wrapper/bag/pack/most drinks).
        private static void PulseHand(bool dominant)
        {
            if (!eatingHaptics) return;
            SteamVR_Actions._default.Haptic.Execute(0f, 0.06f, 1f, 0.4f, HandSource(dominant));
        }

        // Does this controller's sound bank actually have an element for the event name?
        // OnSound resolves AdditionalSounds by EventName == name or "Snd"+name (decompiled
        // SoundEventHandler) — a name with no element is a SILENT no-op, which matters for
        // the gulp fallback (it must not claim the throttle for a sound that can't play).
        private static bool HasSoundElement(string name)
        {
            if (soundPlayer == null || string.IsNullOrEmpty(name)) return false;
            var list = soundPlayer.AdditionalSounds;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e != null && (e.EventName == name || e.EventName == "Snd" + name)) return true;
            }
            return false;
        }

        // Play one of the eat's own sound events on demand (frozen-animation safe).
        private static void PlaySound(string name)
        {
            if (soundPlayer == null || string.IsNullOrEmpty(name)) return;
            playingManualSound = true;
            try { soundPlayer.OnSound(name); }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] PlaySound('{name}') error: {ex}"); }
            finally { playingManualSound = false; }
        }

        // Suppress the eat's animation-driven sounds on OUR controller's player (they
        // would otherwise all fire in a burst at the finish). Our own PlaySound calls
        // pass through via playingManualSound.
        public static bool AllowSound(BaseSoundPlayer sp, string name)
        {
            if (!active || sp != soundPlayer) return true;
            if (playingManualSound) return true; // our own PlaySound, never gate it
            if (playingOpen)
            {
                // Let the open segment's own clip sounds play — EXCEPT names the pull
                // milestones already fired this open: a pull-settle replays the clip's tail
                // for real, and an openSound authored in that tail would sound TWICE if the
                // milestone estimate also fired it during the pull. (Non-pull opens have
                // pullSoundsFired == 0, so nothing is gated — the old behavior.)
                string[] fired = def?.openSounds;
                if (fired != null)
                    for (int i = 0; i < pullSoundsFired && i < fired.Length; i++)
                        if (fired[i] == name) return false;
                return true;
            }
            if (drinkingHeld)
            {
                // The gulp loop plays its own Drink sounds, but the animator restarts faster
                // than the clip — throttle so each gulp finishes before the next fires.
                if (Time.time < nextDrinkSound) return false;
                nextDrinkSound = Time.time + drinkGulpInterval;
                // An animation gulp is audible — push the manual fallback past the next
                // interval so it only fires when the events actually go quiet.
                nextGulpFallback = Time.time + drinkGulpInterval + GulpFallbackGrace;
                UpdateSnackSegments(); // segmented snacks shed a piece per audible bite
                return true;
            }
            return false; // everything else is the animation bursting (manual sounds returned above)
        }

        // Replaces BaseSoundPlayer.PlayClip for OUR controller's player (prefix, returns
        // true = handled): play the clip HEAD-LOCKED 2D, the way first-person mouth foley
        // is meant to sound. Why: PlayClip's stereo flag (IsFirstPerson(PointOfView)) only
        // skips occlusion and sets _forceStereo — NOTHING in that chain zeroes the source's
        // spatialBlend (EnableStereo is never called), and the pooled Weaponry source comes
        // back from Clear() with the preset's 3D blend, positioned on the controller's
        // weapon transform. Vanilla gets away with it because that transform hugs the
        // camera; in VR we reparent everything, so eat/drink clips intermittently played
        // 3D from the wrong spot — quiet/mislocated (reported 2026-06-12), occlusion-
        // muffled, or distance/occlusion-skipped into total silence. Forcing EnableStereo
        // (blend 0) + the stereo PlayClipInternal path removes every positional variable;
        // the pool's Release/Clear restores the source for other users afterwards.
        public static bool PlayClipHeadLocked(BaseSoundPlayer sp, AudioClip clip, int rolloff, float volume)
        {
            if (!active || sp == null || sp != soundPlayer || clip == null) return false; // not ours — original runs
            try
            {
                int priority = sp.PriorityCalculator.CalculatePriority(0f, rolloff * rolloff);
                if (sp._isClipSourceReleased) sp.SetupClipSource(clip);
                sp._clipsSource.EnableStereo(true); // blend 0 + spatialization off = in-head
                sp.PlayClipInternal(clip, rolloff, volume, priority, stereo: true);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogWarning($"[ManualEat] PlayClipHeadLocked failed ({ex.Message}) — falling back to vanilla PlayClip.");
                return false;
            }
        }

        // Postfix from BaseSoundPlayer.PlayClip (AudioToHaptics patch): sounds played
        // while OPENING the item (the pull/squeeze scrub + the settle — not the draw,
        // gulps, bites or holster) stream to the controller doing the open motion — the
        // live OnAudioFilterRead tap rides the BetterSource the clip just went out on.
        public static void OnEatAudio(BaseSoundPlayer sp, AudioClip clip, float volume)
        {
            if (!audioHaptics || !eatingHaptics) return;
            if (!active || manualDone || sp == null || sp != soundPlayer) return;
            if (phase != Phase.Closed || (!pullingOpen && !playingOpen)) return; // opening only
            try
            {
                // The OPENING hand's controller (same role->physical mapping as PulseHand).
                bool dominantActs = style == null || style.openHand == Hand.Dominant;
                bool useLeft = HandSource(dominantActs) == SteamVR_Input_Sources.LeftHand;
                TarkovVR.Source.Misc.AudioHaptics.OnClipPlayed(sp._clipsSource, clip, useLeft, !useLeft);
            }
            catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] OnEatAudio: {ex.Message}"); }
        }
    }
}
