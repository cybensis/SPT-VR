using RootMotion.FinalIK;
using System;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Body;

namespace TarkovVR.Source.Player.Interactions
{
    // Rig code shared by every food archetype: arm IK targets and the post-IK wrist pins, pinning the rig to the
    // body, palm probe points, the interaction-zone math and its debug spheres, trigger reads, haptic pulses, and
    // the sound gate.
    internal static partial class EatingInteractionController
    {
        //--- Arms / props -----------------------------------------------------------
        private static void DriveArms()
        {
            // A pull-to-open latches one hand onto the held item (handLatched). That arm's IK then aims at the
            // latch transform, which is attached to the holding hand's rig target, instead of the controller.
            // Both IKs stay enabled; the wrist pin (Left/RightPinTarget) then puts the wrist exactly on the target.
            SetArmIk(VRGlobals.ikManager.rightArmIk, RightPinTarget());
            SetArmIk(VRGlobals.ikManager.leftArmIk, LeftPinTarget());
            DriveElbowBends();
        }

        // Points the arms' elbow bend goals at the VR goals each frame, as the gun and empty-hands states do.
        // MedsController spawns _elbowBends aimed at its own goals in the animation pose. The index-to-hand
        // mapping flips with handedness.
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
            if (VRGlobals.ikManager.rightArmIk != null) { HandTargets.Set(VRGlobals.ikManager.rightArmIk, null, HandOwner.Eating); VRGlobals.ikManager.rightArmIk.enabled = false; }
            if (VRGlobals.ikManager.leftArmIk != null) { HandTargets.Set(VRGlobals.ikManager.leftArmIk, null, HandOwner.Eating); VRGlobals.ikManager.leftArmIk.enabled = false; }
        }

        private static void SetArmIk(LimbIK armIk, Transform target)
        {
            if (armIk == null || target == null) return;
            if (armIk.solver.target != target) HandTargets.Set(armIk, target, HandOwner.Eating);
            if (!armIk.enabled) armIk.enabled = true;
        }

        // Pins the rig root (camRoot) to the body anchor (the meds object, kept on the ribcage), like
        // HandsPositioner does for empty hands. Nothing else does this while eating.
        private static void PinRigToBody()
        {
            if (medsBody == null || VRGlobals.camRoot == null || VRGlobals.player == null) return;
            VRGlobals.camRoot.transform.position = new Vector3(
                medsBody.position.x,
                VRGlobals.player.Transform.position.y + 1.5f,
                medsBody.position.z);
        }

        // The wrist pin runs from the IK solver's OnPostUpdate, because Player.LateUpdate is too early (FinalIK
        // solves after it). It pins the rendered wrist (bone3) onto the rig target, or onto the latch on the item
        // while that hand is latched.
        private static void PinLeftAfterIk() { if (active && !manualDone) PinBoneToTarget(VRGlobals.ikManager?.leftArmIk, LeftPinTarget()); }
        private static void PinRightAfterIk() { if (active && !manualDone) PinBoneToTarget(VRGlobals.ikManager?.rightArmIk, RightPinTarget()); }

        // Where each hand's IK target and wrist pin aim right now: the pull latch while that hand is
        // latched onto the held item, otherwise its normal rig target.
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

        private static void PinBoneToTarget(LimbIK armIk, Transform target)
        {
            if (armIk == null || target == null) return;
            Transform bone = armIk.solver?.bone3?.transform;
            if (bone == null) return;
            bone.position = target.position;
            bone.rotation = target.rotation;
        }

        //--- Helpers ----------------------------------------------------------------
        private static Transform RightHand() => VRGlobals.vrPlayer?.RightHand != null ? VRGlobals.vrPlayer.RightHand.transform : null;
        private static Transform LeftHand() => VRGlobals.vrPlayer?.LeftHand != null ? VRGlobals.vrPlayer.LeftHand.transform : null;

        // Resolves a hand role to its transform. RightHand is always the dominant hand and LeftHand the off
        // hand (the SteamVR pose listeners swap in left-handed mode), so no handedness branch is needed.
        private static Transform HandT(Hand hand) => hand == Hand.Dominant ? RightHand() : LeftHand();
        private static Hand Other(Hand hand) => hand == Hand.Dominant ? Hand.Off : Hand.Dominant;

        //--- Palm collision points ---------------------------------------------------
        // The palm point every probe and anchor uses: the centroid of the rendered hand's fingertip leaves, in
        // the wrist (bone3) frame. bone3 is pinned exactly onto the rig transform each frame (PinBoneToTarget), so
        // a wrist-local offset is also a rig-local offset. It only uses world deltas inside the rigid hand
        // subtree, so it is valid even when the wrist's world pose is stale. That makes it safe to call from both
        // Update (Tick) and LateUpdate (LateZeroProps, the debug spheres).
        private static bool palmAnchorsResolved;
        private static Transform palmWristDom, palmWristOff;     // bone3 per arm
        private static Transform[] palmLeavesDom, palmLeavesOff; // fingertip leaves near the wrist

        // Dominant always maps to the right arm: the handedness swap happens at the SteamVR pose listeners,
        // and the pins always aim rightArmIk/leftArmIk at RightHand/LeftHand (Right/LeftPinTarget).
        private static void ResolvePalmAnchors()
        {
            palmAnchorsResolved = true;
            ResolvePalmLeaves(VRGlobals.ikManager?.rightArmIk, out palmWristDom, out palmLeavesDom);
            ResolvePalmLeaves(VRGlobals.ikManager?.leftArmIk, out palmWristOff, out palmLeavesOff);
            LogStep("[ManualEat] palm anchors: dom="
                + (palmLeavesDom != null ? palmLeavesDom.Length : 0) + " leaves, off="
                + (palmLeavesOff != null ? palmLeavesOff.Length : 0) + " leaves.");
        }

        // Same filtering as ResolveFingerAnchor: the wrist subtree is the hand, minus our own Eat* holders.
        // Leaves farther from the wrist than a hand's length are attach or marker dummies, not fingers.
        private static void ResolvePalmLeaves(LimbIK armIk, out Transform wrist, out Transform[] leaves)
        {
            wrist = armIk?.solver?.bone3?.transform;
            leaves = null;
            if (wrist == null) return;
            Vector3 wristPos = wrist.position;
            var kept = new System.Collections.Generic.List<Transform>();
            void Walk(Transform t)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform c = t.GetChild(i);
                    if (c.name.StartsWith("Eat", StringComparison.Ordinal)) continue; // our holders and latch
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

        // A hand's palm offset in its rig transform's local frame, or palmFallbackLocal if the skeleton is missing.
        private static Vector3 PalmOffsetLocal(Hand hand)
        {
            if (!palmAnchorsResolved) ResolvePalmAnchors();
            Transform wrist = hand == Hand.Dominant ? palmWristDom : palmWristOff;
            Transform[] leaves = hand == Hand.Dominant ? palmLeavesDom : palmLeavesOff;
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

        // The world point a hand reaches with. Call only when the hand exists (HandT != null).
        private static Vector3 ProbePoint(Hand hand) => HandT(hand).TransformPoint(PalmOffsetLocal(hand));

        // A reach gesture fires when the acting hand's palm probe is within <radius> of the anchor hand's
        // palm plus a local offset. The offset is in the anchor rig transform's local frame, so it moves with the
        // held item (e.g. up a bottle's axis to its cap); zero = the holding palm itself.
        private static bool ZoneReached(Hand acting, Hand anchor, Vector3 offset, float radius)
        {
            Transform a = HandT(acting), b = HandT(anchor);
            if (a == null || b == null) return false;
            return Vector3.Distance(ProbePoint(acting), b.TransformPoint(PalmOffsetLocal(anchor) + offset)) < radius;
        }

        // When the eating hand is the hand holding the item (drinks, wrapper bars, timed wrapper eats, a CanSpoon's
        // can-at-mouth drink), the mouth is probed with the open zone's point on the item (palm + openZoneOffset:
        // the cap, lid or bar top) instead of palm + eatZoneOffset. Foods where a piece is carried in the free
        // hand, and sausage (skipOpen, no open zone), use eatZoneOffset.

        private static Vector3 EatProbeOffset(Hand eatHand)
        {
            if (style != null && def != null && !def.skipOpen
                && eatHand == Other(style.openHand)) // the eating hand holds the item
                return openZoneOffset;
            return eatZoneOffset;
        }

        // The eat gesture: the eating hand's palm probe (plus EatProbeOffset) is near the mouth (head +
        // mouthLocalOffset, since the camera is at the eyes) and roughly in front of the face (the forward check
        // rejects positions behind the head). radiusScale widens the zone for hysteresis: the drink hold passes
        // drinkZoneExitScale while drinking so the drink timer can't flicker on and off at the edge.
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
        // Set debugZones (e.g. in UnityExplorer) while eating. Translucent spheres show each zone exactly as the
        // trigger checks see it (same palm anchor, offset and radius) and turn green while that zone's condition
        // is met (the same ZoneReached/EatZoneReached the gestures use). Opaque dots mark each sphere's center;
        // bright green dots are the palm probe points. Only the zones the current phase can trigger are drawn:
        // blue = open, yellow = take, red = mouth. A Bag's shake check is drawn as the take sphere at its real
        // hand-to-hand radius (shakeNearDistance).
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
            // The shake check is palm-to-palm within shakeNearDistance: the zone sits on the receiving (off) palm and
            // is probed by the palm holding the bag (dominant), matching DetectShake.
            Hand takeProbeHand = shakeTake ? Hand.Dominant : s.takeHand;
            Hand takeAnchorHand = shakeTake ? Hand.Off : Other(s.takeHand);
            Transform takeAnchor = HandT(takeAnchorHand);
            float takeRadius = shakeTake ? shakeNearDistance : scoopDistance;
            Vector3 takeOffset = shakeTake ? Vector3.zero : takeZoneOffset;

            // Which zones can fire right now (matches StepGesture's routing). Drinks keep the open zone active
            // in Ready, because it is the recap trigger.
            bool showOpen = phase == Phase.Closed || (s.timedSip && phase == Phase.Ready);
            bool showTake = phase == Phase.Ready && !s.timedSip && s.hasTakeStep;
            // mouthDrink: a CanSpoon's can-at-the-mouth drink is also active from Ready.
            bool showEat = phase == Phase.Holding
                || (phase == Phase.Ready && (s.timedSip || !s.hasTakeStep || (def != null && def.mouthDrink)));

            // Centers and lit states, computed with the same checks the triggers use.
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

            // Zone center dots (opaque, zone-colored)
            SetZoneViz(ref zoneOpenDot, "VRZoneOpenDot", showOpen && openAnchor != null, openC, 0.01f, new Color(0.25f, 0.55f, 1f, 0.95f));
            SetZoneViz(ref zoneTakeDot, "VRZoneTakeDot", showTake && takeAnchor != null, takeC, 0.01f, new Color(1f, 0.85f, 0.2f, 0.95f));
            SetZoneViz(ref zoneEatDot, "VRZoneEatDot", showEat && head != null, eatC, 0.01f, new Color(1f, 0.25f, 0.25f, 0.95f));

            // Probe points (what must enter a sphere): the acting palm for open and take, and the
            // eating palm plus EatProbeOffset for the mouth.
            Hand probeHand = showTake ? takeProbeHand : s.openHand;
            Transform probeT = HandT(probeHand);
            SetZoneViz(ref probeHandViz, "VRZoneProbeHand", (showOpen || showTake) && probeT != null,
                probeT != null ? ProbePoint(probeHand) : Vector3.zero, 0.015f, new Color(0.2f, 1f, 0.3f, 0.9f));
            Transform eatHand = HandT(s.eatHand);
            SetZoneViz(ref probeEatViz, "VRZoneProbeEat", showEat && eatHand != null,
                eatHand != null ? eatHand.TransformPoint(PalmOffsetLocal(s.eatHand) + EatProbeOffset(s.eatHand)) : Vector3.zero, 0.015f, new Color(0.2f, 1f, 0.3f, 0.9f));
        }

        // The zone sphere's color: its base color, or a stronger green while the trigger condition is met.
        private static Color ZoneColor(Color baseC, bool lit) => lit ? new Color(0.3f, 1f, 0.4f, 0.45f) : baseC;

        // Creates or positions one debug sphere. Its scale is the diameter (the primitive is unit-sized).
        // The color is set on every call because the lit state changes from frame to frame.
        private static void SetZoneViz(ref GameObject go, string name, bool show, Vector3 position, float radius, Color color)
        {
            if (!show) { if (go != null) go.SetActive(false); return; }
            if (go == null)
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = name;
                UnityEngine.Object.Destroy(go.GetComponent<Collider>()); // visual only, no physics
                Renderer partRenderer = go.GetComponent<Renderer>();
                partRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                partRenderer.receiveShadows = false;
                // A shader that supports alpha; Sprites/Default ships with every Unity build.
                Shader sh = Shader.Find("Sprites/Default");
                if (sh == null) sh = Shader.Find("Legacy Shaders/Transparent/Diffuse");
                if (sh == null) sh = Shader.Find("Standard");
                partRenderer.material = new Material(sh) { color = color };
            }
            go.SetActive(true);
            go.transform.position = position;
            go.transform.localScale = Vector3.one * (radius * 2f);
            go.GetComponent<Renderer>().material.color = color;
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

        // The physical controller for a hand role: dominant is the right controller in normal mode and the
        // left one in left-handed mode. The trigger reads and haptics below all use this one mapping.
        private static SteamVR_Input_Sources HandSource(bool dominant)
        {
            bool useLeft = dominant ? VRSettings.GetLeftHandedMode() : !VRSettings.GetLeftHandedMode();
            return useLeft ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
        }

        // Trigger press edge for a hand role. dominant = true reads the dominant hand (which opens cans);
        // false reads the off hand (which opens wrappers, bags and packs). Each hand's previous axis is kept
        // separately so the two edges don't interfere.
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

        // Raw trigger axis for a hand role (the open squeeze and latch use it). It doesn't touch the
        // previous-axis state, so TriggerEdgeImpl is unaffected.
        private static float TriggerAxisImpl(bool dominant)
        {
            SteamVR_Input_Sources src = HandSource(dominant);
            VRSingle trig = src == SteamVR_Input_Sources.LeftHand
                ? VRInput.LeftTrigger : VRInput.RightTrigger;
            return trig.GetAxis(src);
        }

        // Haptic click on the dominant controller.
        private static void Pulse() => PulseHand(true);

        // Haptic click on a given hand role (the hand that opens is the off hand for wrappers, bags,
        // packs and most drinks).
        private static void PulseHand(bool dominant)
        {
            VRInput.Haptic.Execute(0f, 0.06f, 1f, 0.4f, HandSource(dominant));
        }

        // Whether this controller's sound bank has an element for the event name. SoundEventHandler matches
        // AdditionalSounds by EventName == name or "Snd" + name, and a name with no element silently does
        // nothing. The gulp fallback needs this so it doesn't take the throttle for a sound that can't play.
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

        // Plays one of the eat's own sound events on demand (works while the animation is frozen).
        private static void PlaySound(string name)
        {
            if (soundPlayer == null || string.IsNullOrEmpty(name)) return;
            playingManualSound = true;
            try { soundPlayer.SoundEventHandler(name); }
            catch (Exception exception) { Plugin.MyLog.LogError($"[ManualEat] PlaySound('{name}') error: {exception}"); }
            finally { playingManualSound = false; }
        }

        // Mutes the eat's animation-driven sounds on our controller's sound player (they would otherwise all
        // play at once at the finish). Our own PlaySound calls pass through via playingManualSound.
        public static bool AllowSound(BaseSoundPlayer sp, string name)
        {
            if (!active || sp != soundPlayer) return true;
            if (playingManualSound) return true; // our own PlaySound, never muted
            if (playingOpen)
            {
                // Let the open's own clip sounds play, except names the pull milestones already played during this
                // open. The settle after the pull plays the clip's tail for real, so an openSound in that tail would
                // play twice. Opens without a pull have pullSoundsFired == 0, so nothing is muted.
                string[] fired = def?.openSounds;
                if (fired != null)
                    for (int i = 0; i < pullSoundsFired && i < fired.Length; i++)
                        if (fired[i] == name) return false;
                return true;
            }
            if (drinkingHeld)
            {
                // The gulp loop plays its own Drink sounds, but the animator restarts faster than the clip,
                // so throttle them and let each gulp finish before the next one plays.
                if (Time.time < nextDrinkSound) return false;
                nextDrinkSound = Time.time + drinkGulpInterval;
                // An animation gulp played, so push the manual fallback past the next interval. It only plays
                // when the animation events go quiet.
                nextGulpFallback = Time.time + drinkGulpInterval + GulpFallbackGrace;
                UpdateSnackSegments(); // segmented snacks lose one piece per audible bite
                return true;
            }
            return false; // everything else is animation sound that would pile up (our own sounds returned above)
        }

        // Replaces BaseSoundPlayer.PlayClip for our controller's sound player (prefix; true = handled) and plays
        // the clip head-locked in 2D. PlayClip's stereo flag (IsFirstPerson(PointOfView)) only skips occlusion and
        // sets _forceStereo. The pooled Weaponry source keeps the preset's 3D blend at the controller's weapon
        // transform, which VR reparents away from the camera, so clips come out quiet, misplaced, muffled or
        // silent. EnableStereo (blend 0) plus the stereo PlayClipInternal path removes the positioning; the
        // pool's Release/Clear restores the source for its next user.
        public static bool PlayClipHeadLocked(BaseSoundPlayer sp, AudioClip clip, int rolloff, float volume)
        {
            if (!active || sp == null || sp != soundPlayer || clip == null) return false; // not ours: the original runs
            try
            {
                int priority = sp.PriorityCalculator.CalculatePriority(0f, rolloff * rolloff);
                if (sp._isClipSourceReleased) sp.SetupClipSource(clip);
                sp._clipsSource.EnableStereo(true); // blend 0 + spatialization off = in-head
                sp.PlayClipInternal(clip, rolloff, volume, priority, stereo: true);
                return true;
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogWarning($"[ManualEat] PlayClipHeadLocked failed ({exception.Message}) — falling back to vanilla PlayClip.");
                return false;
            }
        }

        // Called from the BaseSoundPlayer.PlayClip postfix (AudioToHaptics). Sounds played while opening the item
        // (the squeeze and pull scrub, and the settle; not the draw, gulps, bites or put-away) are streamed to the
        // controllers as haptics, through the OnAudioFilterRead tap on the BetterSource that played the clip.
        public static void OnEatAudio(BaseSoundPlayer sp, AudioClip clip, float volume)
        {
            if (!active || manualDone || sp == null || sp != soundPlayer) return;
            if (phase != Phase.Closed || (!pullingOpen && !playingOpen)) return; // opening only
            try
            {
                // The opening hand's controller (same role-to-controller mapping as PulseHand) gets full strength. The
                // other hand holds the item being opened and feels it through the item, weaker and lower.
                bool dominantActs = style == null || style.openHand == Hand.Dominant;
                bool openerIsLeft = HandSource(dominantActs) == SteamVR_Input_Sources.LeftHand;
                float holderGain = TarkovVR.Source.Misc.HapticMixer.throughGain;
                float holderPitch = TarkovVR.Source.Misc.HapticMixer.throughPitch;
                TarkovVR.Source.Misc.AudioHaptics.OnClipPlayed(sp._clipsSource, clip,
                    openerIsLeft ? 1f : holderGain, openerIsLeft ? holderGain : 1f,
                    openerIsLeft ? 1f : holderPitch, openerIsLeft ? holderPitch : 1f);
            }
            catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] OnEatAudio: {exception.Message}"); }
        }
    }
}
