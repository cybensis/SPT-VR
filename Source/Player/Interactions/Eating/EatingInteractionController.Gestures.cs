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
    // The gesture state machine: one shared open->take->eat/drink loop driven by the
    // per-archetype EatStyle descriptor (BuildStyle). A new archetype is a BuildStyle
    // case + prop wiring (Props.cs) + a factory (Foods.cs).
    internal static partial class EatingInteractionController
    {
        //--- Gesture state machine --------------------------------------------------
        // All archetypes share ONE open->take->eat loop; only the hand roles, the arms state
        // the open plays, and a few reveal/advance hooks differ. Those live in the per-archetype
        // EatStyle descriptor (built per food on spawn), so the control flow below is written
        // once. The genuinely-unique routines (DetachWrapperToLeftHand / ShakeOutCrackers /
        // TakeFoodToLeftHand / EnterUseState / DetectShake) are kept as-is and just called from
        // the hooks. To add a TYPE: a new case here + its prop setup / late-zero tails.
        private static EatStyle BuildStyle(FoodDef d)
        {
            EatStyle s = BuildStyleCore(d);
            // Per-food open-state override (OpenState(...)): the archetype defaults assume
            // the SHARED state hashes, but some rigs run their own graph — noodles has no
            // STATE_USE at all, so the Wrapper default scrubbed a state that doesn't exist
            // (Play() is a silent no-op → the wrapper never ripped).
            if (d.openState != 0) s.openStateHash = d.openState;
            return s;
        }

        private static EatStyle BuildStyleCore(FoodDef d)
        {
            switch (d.kind)
            {
                // Chocolate bar: held in the RIGHT hand, the LEFT (off) hand peels the wrapper,
                // then bite straight to the mouth — NO take step. The whole clip is STATE_USE.
                // eatByTime foods (sausage/tarka/noodles) ride the timed-sip path instead of
                // a bite: HOLD at the mouth and the use-time drains like a drink.
                case FoodKind.Handheld:
                    return new EatStyle
                    {
                        label = "wrapper",
                        openHand = Hand.Off,
                        eatHand = Hand.Dominant,
                        takeHand = Hand.Off,
                        hasTakeStep = false,
                        timedSip = d.eatByTime,
                        takeKind = TakeKind.None,
                        openStateHash = STATE_USE_HASH,
                        logsGesturePose = false,
                        onOpened = () => DetachWrapperToLeftHand(), // peeled -> stays in the left hand (no-op without a cover)
                    };
                // Drink: container in the holding hand (right unless holdInOffHand), the FREE
                // hand pops the cap, then HOLD the container at the mouth — StepDrink runs the
                // use-time down while held. Capped bottles recap to stop early.
                case FoodKind.Drink:
                {
                    Hand hold = d.holdInOffHand ? Hand.Off : Hand.Dominant;
                    return new EatStyle
                    {
                        label = "drink",
                        openHand = Other(hold), // the free hand works the cap
                        eatHand = hold,         // you drink from the holding hand
                        takeHand = Other(hold),
                        hasTakeStep = false,
                        timedSip = true,
                        takeKind = TakeKind.None,
                        openStateHash = STATE_OPEN_HASH,
                        logsGesturePose = false,
                        onOpened = () => DetachCapToFreeHand(), // cap popped -> rides the free hand
                    };
                }
                // Bag (croutons): held in the RIGHT hand, LEFT opens, then SHAKE pours crackers
                // into the LEFT hand; eaten from the LEFT hand. The shake/eat don't advance the
                // animator, so the take pushes it into STATE_USE (EnterUseState) for a clean finish.
                case FoodKind.Bag:
                    return new EatStyle
                    {
                        label = "bag",
                        openHand = Hand.Off,
                        eatHand = Hand.Off,
                        takeHand = Hand.Off,
                        hasTakeStep = true,
                        takeKind = TakeKind.Shake,
                        openStateHash = STATE_OPEN_HASH,
                        logsGesturePose = false,
                        onOpened = () => ResetShake(),
                        onTake = () => { ShakeOutCrackers(); EnterUseState(); }, // no haptic pulse on a shake
                        onEatHide = HideCrackers,
                        onAfterBite = () => ResetShake(),
                    };
                // Reach-into-bag (iskra/MRE): held RIGHT, LEFT rips the top, then the LEFT
                // hand reaches INTO the bag (StepReachTake — rail latch + depth scrub; the
                // grab fires onTake, the pull-out flips to Holding), eaten from the LEFT
                // hand. No animator hooks beyond the take: the reach scrub itself parks the
                // arms in STATE_USE, so the finish never sees STATE_OPEN.
                case FoodKind.ReachBag:
                    return new EatStyle
                    {
                        label = "reach-bag",
                        openHand = Hand.Off,
                        eatHand = Hand.Off,
                        takeHand = Hand.Off,
                        hasTakeStep = true,
                        takeKind = TakeKind.ReachIn,
                        openStateHash = STATE_OPEN_HASH,
                        logsGesturePose = true,
                        onTake = () => TakeFoodToLeftHand(), // cracker -> the reaching hand
                        onEatHide = () => SetRenderer(foodR, false),
                    };
                // Pack (galette): mirror of CanHand — held RIGHT, LEFT opens, LEFT takes a piece
                // and eats it. Deterministic grab/eat pulses (see StartGesturePulse).
                case FoodKind.Pack:
                    return new EatStyle
                    {
                        label = "pack",
                        openHand = Hand.Off,
                        eatHand = Hand.Off,
                        takeHand = Hand.Off,
                        hasTakeStep = true,
                        takeKind = TakeKind.HandNear,
                        openStateHash = STATE_OPEN_HASH,
                        logsGesturePose = true,
                        onTake = () => { TakeFoodToLeftHand(); StartGesturePulse(takePoseState, takePoseTime); Pulse(); },
                        onEatHide = () => SetRenderer(foodR, false),
                        onAfterBite = () => StartGesturePulse(bitePoseState, bitePoseTime),
                    };
                // Canned (tushonka spoon + sprats hand): can in the LEFT hand, RIGHT rolls the
                // lid, RIGHT takes the food (onto the spoon, or grabbed directly) and eats it.
                default: // FoodKind.CannedFood
                    return new EatStyle
                    {
                        label = "can",
                        openHand = Hand.Dominant,
                        eatHand = Hand.Dominant,
                        takeHand = Hand.Dominant,
                        hasTakeStep = true,
                        takeKind = TakeKind.HandNear,
                        openStateHash = STATE_OPEN_HASH,
                        logsGesturePose = true,
                        onOpened = () => SetRenderer(spoonR, true), // reveal the spoon (no-op if none)
                        onTake = () => { SetRenderer(foodR, true); SetRenderer(foodR2, true); StartGesturePulse(takePoseState, takePoseTime); Pulse(); },
                        onEatHide = () => { SetRenderer(foodR, false); SetRenderer(foodR2, false); },
                        onAfterBite = () => StartGesturePulse(bitePoseState, bitePoseTime),
                    };
            }
        }

        private static void StepGesture()
        {
            EatStyle s = style;
            if (s == null) return;
            switch (phase)
            {
                case Phase.Closed:
                    StepOpen(s);
                    break;
                case Phase.Ready:
                    // Drinks hold-to-drink from Ready; with a take step you take first
                    // (-> Holding); without one (Handheld) you eat straight from Ready.
                    if (s.timedSip) StepDrink(s);
                    else if (s.hasTakeStep) StepTake(s);
                    else StepEat(s);
                    break;
                case Phase.Holding:
                    StepEat(s);
                    break;
                case Phase.Done:
                    break;
            }
        }

        // Open: bring the opening hand to the held item's open-zone + that hand's trigger ->
        // play the open state forward to openReadyTime (lid rolls / wrapper peels / bag tears),
        // then freeze and run the per-archetype onOpened reveal. With physicalPullToOpen the
        // clip doesn't free-run — your hand latches onto it and your physical pull scrubs it
        // up to pullEndTime; the playingOpen wait branch below is then SHARED by the pull's
        // settle (pullEnd -> openReadyTime auto-plays after the unlatch) and the non-pull open.
        private static void StepOpen(EatStyle s)
        {
            if (playingOpenAnim) { WaitOpenAnim(s); return; } // riding the one-shot open animation (rip)
            if (physicalPullToOpen && !playingOpen)
            {
                StepOpenPull(s);
                return;
            }
            if (!physicalPullToOpen && pullingOpen) EndPull(); // flag toggled off live mid-pull — don't strand the latch

            if (!playingOpen)
            {
                // the held item rides the OTHER (holding) hand
                if (ZoneReached(s.openHand, Other(s.openHand), openZoneOffset, openDistance) && TriggerEdgeImpl(s.openHand == Hand.Dominant))
                {
                    playingOpen = true;
                    controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                    Pulse();
                    Plugin.MyLog.LogInfo($"[ManualEat] Opening {s.label}...");
                }
            }
            else
            {
                var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                bool past = st.fullPathHash != s.openStateHash || st.normalizedTime >= openReadyTime;
                if (past)
                {
                    playingOpen = false;
                    controller.FirearmsAnimator?.SetAnimationSpeed(0f);
                    FinishOpen(s);
                }
            }
        }

        // The open is done (pull settled / non-pull clip reached openReadyTime). Either play
        // a one-shot open animation FOR REAL (openPlayState) before going Ready, or go Ready
        // straight away. The one-shot exists because some rigs put the visible "open" in a
        // state the pull can't scrub: noodles' bag-RIP lives in the early frames of its first
        // eat state (2073176132), NOT in STATE_OPEN — scrubbing/settling STATE_OPEN never
        // showed it, so the rip only appeared on the first hold-to-mouth. Playing that state
        // for real here makes the rip happen as part of the open; the chew loop then runs a
        // DIFFERENT eat state so it doesn't re-rip.
        private static void FinishOpen(EatStyle s)
        {
            if (openPlayState != 0 && !openAnimPlayed)
            {
                openAnimPlayed = true; // one-shot
                playingOpenAnim = true;
                openAnimDeadline = Time.time + 2f; // give-up fallback (wrong/short state)
                try { controller._player.ArmsAnimatorCommon.Play(openPlayState, 1, 0f); }
                catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] open-anim Play({openPlayState}) failed: {ex.Message}"); }
                controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                PulseHand(s.openHand == Hand.Dominant);
                Plugin.MyLog.LogInfo($"[ManualEat] Playing open animation ({openPlayState}) to {openPlayMaxTime:F2}...");
                return;
            }
            s.onOpened?.Invoke();
            HideOnOpenRenderers(); // drop the torn cover/lid (noodles) so it doesn't float
            phase = Phase.Ready;
            PulseHand(s.openHand == Hand.Dominant);
            Plugin.MyLog.LogInfo($"[ManualEat] Opened {s.label} — ready (take={s.hasTakeStep}).");
        }

        // Hide the configured torn-cover renderers once the open is done (FoodDef.hideOnOpenName).
        // One-shot; RestoreProps re-shows them. No-op unless the food set a name.
        private static void HideOnOpenRenderers()
        {
            if (hideOnOpenR == null || hiddenOnOpen) return;
            for (int i = 0; i < hideOnOpenR.Length; i++) SetRenderer(hideOnOpenR[i], false);
            hiddenOnOpen = true;
            Plugin.MyLog.LogInfo($"[ManualEat] Hid the torn cover ({def.hideOnOpenName}).");
        }

        // Wait out the one-shot open animation (the rip), then freeze + go Ready. Stops at
        // openPlayMaxTime (so it doesn't run on into the first bite) or when the state ends/
        // changes; openAnimDeadline is the safety net for a wrong hash.
        private static void WaitOpenAnim(EatStyle s)
        {
            bool past;
            try
            {
                var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                past = st.fullPathHash != openPlayState || st.normalizedTime >= openPlayMaxTime;
            }
            catch { past = true; }
            if (!past && Time.time < openAnimDeadline) return;
            playingOpenAnim = false;
            controller.FirearmsAnimator?.SetAnimationSpeed(0f);
            s.onOpened?.Invoke();
            HideOnOpenRenderers(); // the rip just played — now drop the torn cover so it doesn't float
            phase = Phase.Ready;
            Plugin.MyLog.LogInfo($"[ManualEat] Open animation done — ready (take={s.hasTakeStep}).");
        }

        // Physical open. Squeezing the trigger at the open zone latches the opening hand
        // ONTO THE HELD ITEM (LatchHandToItem — IK target + wrist pin aim at a latch glued
        // to the holding hand's rig target, so the rendered hand rides that controller's
        // 6dof). Each grab then runs two stages mapped onto the open clip's normalizedTime
        // between the pull start and pullEndTime:
        //   1. SQUEEZE — the trigger axis itself scrubs the first triggerOpenPortion (for
        //      the Trigger kind: the whole open, completed by fully pressing then fully
        //      releasing).
        //   2. PHYSICAL — once fully pressed, the per-food gesture (def.openGesture) drives
        //      the rest: Pull = palm travel away from the full-press point, Tilt = tipping
        //      the hand off its full-press orientation. All measured RELATIVE to the
        //      holding hand so walking/turning doesn't scrub; backing off rolls it back
        //      down (to where this grab started).
        // Trigger released = unlatch (hand snaps back to the controller); progress KEEPS,
        // so a half-rolled lid stays half-rolled until you grab it again. The lid/cap/
        // wrapper are bone-animated children of the HELD prop, so the scrub deforms the
        // item in your actual hand, and the clip still animates the latched hand's FINGERS
        // (the pin only owns the wrist). Completion goes through CompleteOpenPull
        // (settle-or-Ready), so everything downstream is unchanged.
        private static void StepOpenPull(EatStyle s)
        {
            Transform acting = HandT(s.openHand);
            Transform anchor = HandT(Other(s.openHand)); // the held item rides this hand
            if (acting == null || anchor == null) return;

            bool dominant = s.openHand == Hand.Dominant;
            float axis = TriggerAxisImpl(dominant);
            // Keep the shared edge state fresh: the drink recap (StepDrink, next phase) edges
            // via TriggerEdgeImpl off these prevs — a stale prev of 0 with the trigger still
            // held from the open would fire a FALSE recap the moment the phase flips.
            float prevAxis = dominant ? prevTriggerAxis : prevOffTriggerAxis;
            if (dominant) prevTriggerAxis = axis; else prevOffTriggerAxis = axis;

            OpenGestureKind g = def != null ? def.openGesture : OpenGestureKind.Pull;
            float triggerSpan = g == OpenGestureKind.Trigger ? 1f : Mathf.Clamp01(triggerOpenPortion);

            if (!pullingOpen)
            {
                // Begin on a squeeze EDGE through pullLatchAxis at the open zone (edge so a
                // trigger already held while reaching in doesn't auto-grab).
                if (axis < pullLatchAxis || prevAxis >= pullLatchAxis
                    || !ZoneReached(s.openHand, Other(s.openHand), openZoneOffset, openDistance)) return;

                // First grab: map progress 0 to wherever the animator froze after the draw
                // (Wrapper foods spawn straight into STATE_USE — the capture handles both).
                if (pullAnimStart < 0f)
                {
                    float t0 = 0f;
                    try
                    {
                        var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                        if (st.fullPathHash == s.openStateHash)
                            t0 = Mathf.Clamp(st.normalizedTime, 0f, PullEndResolved() - 0.01f);
                    }
                    catch { }
                    pullAnimStart = t0;
                }

                pullingOpen = true;
                pullSqueezeDone = false;
                pullAwaitRelease = false;
                pullGrabProgress = pullProgress;
                LatchHandToItem(s.openHand, acting, anchor);
                ScrubOpen(s);
                PulseHand(dominant);
                Plugin.MyLog.LogInfo($"[ManualEat] Open grab ({s.label}, {g}) at {pullProgress:P0}.");
                return;
            }

            // Trigger-kind, fully scrubbed: completing needs the full press-then-DEPRESS
            // cycle, so hold at full and wait for the release. (Checked before the unlatch —
            // this release means DONE, not let-go.)
            if (pullAwaitRelease)
            {
                if (axis <= pullReleaseAxis) CompleteOpenPull(s);
                return;
            }

            // Let go: unlatch (the IK snaps the hand back to the controller); progress keeps.
            if (axis <= pullReleaseAxis)
            {
                EndPull();
                Plugin.MyLog.LogInfo($"[ManualEat] Open released at {pullProgress:P0}.");
                return;
            }

            if (!pullSqueezeDone)
            {
                // SQUEEZE: the trigger axis scrubs the first triggerSpan of the open (both
                // directions, floored where this grab started). For the Trigger kind the
                // span is the WHOLE open. At full press the physical gesture takes over —
                // its references are captured HERE, so only motion made while fully
                // gripping counts (no drift from the reach-in).
                float a = Mathf.InverseLerp(pullLatchAxis, pullFullPressAxis, axis);
                pullProgress = Mathf.Max(pullGrabProgress, Mathf.Clamp01(a * triggerSpan));
                ScrubOpen(s);
                FirePullMilestoneSounds();
                if (axis >= pullFullPressAxis)
                {
                    if (g == OpenGestureKind.Trigger) { pullAwaitRelease = true; return; }
                    pullSqueezeDone = true;
                    pullGrabProgress = pullProgress; // the physical phase's floor
                    // Physical measures are RELATIVE to the holding hand (palm position in
                    // its frame, rotation vs its rotation) so walking/body turns don't
                    // scrub. Palm offset frozen here (curl tracking would feed the clip's
                    // own finger animation back into the travel).
                    pullPalmLocal = PalmOffsetLocal(s.openHand);
                    pullGrabLocal = anchor.InverseTransformPoint(acting.TransformPoint(pullPalmLocal));
                    pullGrabRel = Quaternion.Inverse(anchor.rotation) * acting.rotation;
                    PulseHand(dominant); // the "engaged" click
                }
                return;
            }

            // PHYSICAL: pull travel / tilt since the full press -> progress.
            float measure = MeasureOpenGesture(g, acting, anchor);
            float scale = g == OpenGestureKind.Tilt ? openTiltDegrees : openPullDistance;
            pullProgress = Mathf.Clamp01(pullGrabProgress + Mathf.Max(0f, measure) / Mathf.Max(0.01f, scale));
            ScrubOpen(s);
            FirePullMilestoneSounds();
            if (pullProgress >= 1f) CompleteOpenPull(s);
        }

        // The physical gesture's measure since the full-press capture, in its native units
        // (Pull = metres of palm travel, Tilt = degrees). All anchor-relative. (A signed
        // CCW-twist measure for screw caps was removed — wrist roll read too inconsistently;
        // tilting the hand serves the same purpose.)
        private static float MeasureOpenGesture(OpenGestureKind g, Transform acting, Transform anchor)
        {
            if (g == OpenGestureKind.Pull)
                // Magnitude (not a fixed axis) so any pull direction works for any container
                // orientation; moving back toward the grab rolls it back down.
                return (anchor.InverseTransformPoint(acting.TransformPoint(pullPalmLocal)) - pullGrabLocal).magnitude;

            // Tilt: any direction counts — how far the hand's forward tipped off the
            // full-press orientation (caps, flip-top lids, can tabs).
            Quaternion rel = Quaternion.Inverse(anchor.rotation) * acting.rotation;
            return Vector3.Angle(pullGrabRel * Vector3.forward, rel * Vector3.forward);
        }

        // A finished open (pull reached 1 / Trigger press-then-release cycle done): unlatch
        // and either SETTLE or go Ready. The pull only covers the authored segment
        // (pullStart -> pullEnd); if the open clip has a tail past that (the tushonka spoon
        // grab), it settles: hand unlocked (back on the controller), the clip auto-plays
        // pullEnd -> openReadyTime, and StepOpen's shared wait branch freezes it there and
        // runs onOpened. With pullEnd at openReadyTime (default) this opens immediately.
        private static void CompleteOpenPull(EatStyle s)
        {
            ReportOpenMovers(); // the lid-bone hint (no-op once reported / with OpenGrip set)
            EndPull();
            if (PullEndResolved() < openReadyTime - 0.001f)
            {
                playingOpen = true;
                controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                PulseHand(s.openHand == Hand.Dominant);
                Plugin.MyLog.LogInfo($"[ManualEat] Open done ({s.label}) — settling to the ready pose...");
                return;
            }
            FinishOpen(s); // -> Ready, or play the one-shot open animation (rip) first
        }

        // Drive the open clip's playhead to the pull progress. The animator stays at speed 0
        // (Tick keeps it there while !playingOpen); Play() re-enters the state at the mapped
        // normalizedTime and the animator re-applies its bone pose every frame even at speed
        // 0, so this is a clean scrub. Clip sound events never fire from a seek — the
        // milestone sounds below stand in for them.
        private static void ScrubOpen(EatStyle s)
        {
            try { controller._player.ArmsAnimatorCommon.Play(s.openStateHash, 1, CurrentScrubTime()); }
            catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] pull scrub Play failed: {ex.Message}"); }
        }

        // The open-state normalizedTime the current pull progress maps to (shared by the
        // animator scrub and the HandPath sampling, so the hand and the lid stay in sync).
        // pullStartTime (when set) overrides the auto-captured start, live.
        private static float CurrentScrubTime()
        {
            float end = PullEndResolved();
            float start = pullStartTime >= 0f ? Mathf.Min(pullStartTime, end - 0.01f)
                        : pullAnimStart < 0f ? 0f : pullAnimStart;
            return Mathf.Lerp(start, end, pullProgress);
        }

        // Where the pull's progress-1 lands: pullEndTime, capped at openReadyTime; unset
        // (-1) = openReadyTime (no settle segment).
        private static float PullEndResolved()
            => pullEndTime > 0f ? Mathf.Min(pullEndTime, openReadyTime) : openReadyTime;

        // Fire the food's openSounds at evenly spaced milestones, each once — the high-water
        // counter never refires one on a re-scrub. The slots live on the open-state TIME
        // axis, evenly spaced between the pull start and openReadyTime (when the pull covers
        // the whole open this is identical to the old progress-fraction spacing). A sound
        // whose slot lands PAST pullEndTime is NOT fired here: the settle plays the clip's
        // tail for real, so its own sound events cover it at the authored moment — firing it
        // from the pull too was the "open sound plays again" double (tushonka's SpoonTake).
        private static void FirePullMilestoneSounds()
        {
            string[] sounds = def?.openSounds;
            if (sounds == null || sounds.Length == 0) return;
            float[] times = def?.openSoundTimes; // authored event times, when known
            float end = PullEndResolved();
            float start = pullStartTime >= 0f ? Mathf.Min(pullStartTime, end - 0.01f)
                        : pullAnimStart < 0f ? 0f : pullAnimStart;
            float t = CurrentScrubTime();
            while (pullSoundsFired < sounds.Length)
            {
                float slot = times != null && pullSoundsFired < times.Length
                    ? times[pullSoundsFired] // the real clip moment (can land inside the squeeze)
                    : Mathf.Lerp(start, openReadyTime, (pullSoundsFired + 1f) / (sounds.Length + 1f));
                if (slot > end || t < slot) break; // slots are monotonic — later ones can't be due either
                PlaySound(sounds[pullSoundsFired]);
                PulseHand(style != null && style.openHand == Hand.Dominant);
                pullSoundsFired++;
            }
        }

        // Take: the take gesture (reach the holding hand, or SHAKE the bag) reveals/moves the
        // food onto the eating hand and advances the animator. -> Holding. The reach-into-bag
        // take is a multi-frame latch, not a one-shot trigger — it runs its own step.
        private static void StepTake(EatStyle s)
        {
            if (s.takeKind == TakeKind.ReachIn) { StepReachTake(s); return; }
            // CanSpoon hybrid: holding the CAN itself at the mouth drinks it down instead
            // of scooping (condensed milk). Returns true while the mouth path owns the frame.
            if (def != null && def.mouthDrink && StepCanDrink(s)) return;
            bool triggered = s.takeKind == TakeKind.Shake
                ? DetectShake()
                : ZoneReached(s.takeHand, Other(s.takeHand), takeZoneOffset, scoopDistance);
            if (!triggered) return;

            if (logGesturePose && s.logsGesturePose)
                Plugin.MyLog.LogInfo($"[ManualEat] take #{biteCount + 1} pulse from {CurStateStr()}");
            s.onTake?.Invoke();
            PlaySound(def.scoopSound);
            phase = Phase.Holding;
            Plugin.MyLog.LogInfo($"[ManualEat] Took {s.label} (round {biteCount + 1}/{def.bites}).");
        }

        // Reach-into-bag take (iskra/MRE — the user's spec mapped onto the pull-latch
        // primitives). Entering the take zone (the bag mouth) LATCHES the reaching hand
        // onto a RAIL: its position locks to the entry->zone-center line (lateral motion
        // dropped, rotation stays on the controller — DriveReachLatch) while the physical
        // reach DEPTH scrubs the clip's reach segment, so the bag mouth widens as you push
        // in and closes as you back out. Trigger while deep grabs the cracker into the
        // reaching hand (it rides the latched hand — you still have to pull it out);
        // withdrawing past the entry point completes the take (-> Holding, eat at the
        // mouth). Backing out empty-handed or yanking sideways off the rail just releases
        // the hand ("when you leave collision, you should have control over hand again") —
        // re-enter the zone to reach again.
        private static void StepReachTake(EatStyle s)
        {
            Transform acting = HandT(s.takeHand);
            Transform anchor = HandT(Other(s.takeHand)); // the bag rides this hand
            if (acting == null || anchor == null) return;

            if (!reachingIn)
            {
                if (!ZoneReached(s.takeHand, Other(s.takeHand), takeZoneOffset, scoopDistance)) return;
                BeginReach(s, acting, anchor);
                return;
            }

            // Keep the trigger edge state fresh every latched frame — a stale prev would
            // fire a false grab the moment the depth gate opened under a held trigger.
            bool trigEdge = TriggerEdgeImpl(s.takeHand == Hand.Dominant);

            // Depth + lateral in the anchor frame, against the FROZEN entry refs (the scrub
            // animates the latched fingers — a live palm offset would feed the clip back
            // into the measure, the pullPalmLocal lesson).
            Vector3 palmLocal = anchor.InverseTransformPoint(acting.TransformPoint(reachPalmLocal));
            float depth = Vector3.Dot(palmLocal - reachEntryPalmLocal, reachAxisLocal);
            float lateral = (palmLocal - (reachEntryPalmLocal + reachAxisLocal * depth)).magnitude;

            // Leaving the collider = control of the hand again: backwards past the entry
            // point, or a sideways yank off the rail. WITH the cracker grabbed either exit
            // completes the take — it's in your hand, it doesn't go back in the bag.
            if (depth < -reachExitDepth || lateral > reachLateralEscape)
            {
                bool grabbed = reachGrabbed;
                EndReach();
                if (grabbed)
                {
                    phase = Phase.Holding;
                    Plugin.MyLog.LogInfo($"[ManualEat] Pulled the cracker out (round {biteCount + 1}/{def.bites}).");
                }
                else Plugin.MyLog.LogInfo("[ManualEat] Left the bag empty-handed — hand released.");
                return;
            }

            // Physical depth -> the reach segment (deeper = further in; backing out rolls
            // it back down). Same speed-0 seek as ScrubOpen; the lerp runs either way
            // (start may be > deep — see the FoodDef note on the inverted first guess).
            reachProgress = Mathf.Clamp01(depth / Mathf.Max(0.01f, reachDepthDistance));
            try { controller._player.ArmsAnimatorCommon.Play(STATE_USE_HASH, 1, Mathf.Lerp(reachStartTime, reachDeepTime, reachProgress)); }
            catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] reach scrub Play failed: {ex.Message}"); }

            // Trigger while deep = grab: the cracker appears in the reaching hand (the
            // holder hangs off that wrist, which is pinned to the latch — it rides the
            // rail out with you). No animator pulse — it would fight the scrub.
            if (!reachGrabbed && trigEdge && reachProgress >= reachGrabDepth)
            {
                reachGrabbed = true;
                if (logGesturePose && s.logsGesturePose)
                    Plugin.MyLog.LogInfo($"[ManualEat] grab #{biteCount + 1} from {CurStateStr()}");
                s.onTake?.Invoke(); // cracker -> the reaching hand's holder, shown
                PlaySound(def.scoopSound);
                PulseHand(s.takeHand == Hand.Dominant);
                Plugin.MyLog.LogInfo($"[ManualEat] Grabbed the cracker at {reachProgress:P0} deep — pull it out.");
            }
        }

        // Eat: bring the eating hand (+ eat-zone offset) to the mouth -> hide the eaten piece,
        // count the bite, then finish (last bite) or advance for the next round. Shared by the
        // Holding phase (foods with a take step) and the Ready phase (Handheld: open then bite).
        private static void StepEat(EatStyle s)
        {
            if (!EatZoneReached(s.eatHand)) return;

            if (logGesturePose && s.logsGesturePose)
                Plugin.MyLog.LogInfo($"[ManualEat] eat #{biteCount + 1} pulse from {CurStateStr()}");
            s.onEatHide?.Invoke();
            biteCount++;
            Pulse();
            PlaySound(def.eatSound);
            Plugin.MyLog.LogInfo($"[ManualEat] Ate {biteCount}/{def.bites}.");

            // Resource foods tick their bar down PER BITE: the def's bites divide what was
            // left at spawn (oat flakes 40/40 at bites:4 = 10 a shake; the HUD bar updates
            // each bite). Each chunk is capped at the LIVE remaining, so a mouth-drunk
            // CanSpoon (condensed milk) finishes in proportionally fewer scoops. The finish
            // applies only the unapplied remainder (drinkAppliedFrac), so nothing doubles.
            if (drinkFdc != null && def.kind != FoodKind.Drink && def.bites > 0)
            {
                float live = drinkFdc.MaxResource > 0f ? Mathf.Clamp01(drinkFdc.HpPercent / drinkFdc.MaxResource) : 0f;
                float chunk = Mathf.Min(drinkRemainingFrac / def.bites, live);
                if (chunk > 0f)
                {
                    ApplyConsumedNutrition(chunk);
                    drinkAppliedFrac += chunk;
                }
            }

            // Finish on the bite count OR the live resource hitting empty — a mixed
            // mouth+spoon eat can empty the can before the counter runs out.
            bool resourceEmpty = drinkFdc != null && drinkFdc.HpPercent <= 0.0001f;
            if (biteCount >= def.bites || resourceEmpty)
            {
                RequestFinish(cancel: false); // drives STATE_END itself; no eat pulse needed
            }
            else
            {
                s.onAfterBite?.Invoke();
                phase = Phase.Ready;
            }
        }

        // One held-at-the-mouth drink frame, shared by StepDrink (Drink/eatByTime foods)
        // and StepCanDrink (the CanSpoon mouthDrink hybrid): start/continue the gulp loop +
        // live drain while the item is at the mouth (mouthHand = the hand HOLDING it);
        // pause (freeze the gulp + cut the glug) the frame it lowers. Auto-finishes the
        // moment the LIVE resource empties (falls back to the use-time clock when the item
        // has no resource component). Returns true while the item is at the mouth (the
        // frame belongs to drinking).
        private static bool DrinkHeldFrame(Hand mouthHand)
        {
            // Hysteresis: entering uses the exact zone, but once drinking the exit boundary
            // is wider (drinkZoneExitScale) so a steady hold can't flicker the clock on/off.
            if (EatZoneReached(mouthHand, drinkingHeld ? drinkZoneExitScale : 1f))
            {
                if (!drinkingHeld)
                {
                    drinkingHeld = true;
                    nextDrinkSound = 0f; // first gulp plays immediately
                    nextGulpFallback = Time.time + GulpFallbackGrace; // animation event gets first shot
                    Pulse();
                    Plugin.MyLog.LogInfo("[ManualEat] Drinking...");
                }
                drinkHeldTime += Time.deltaTime;
                DriveDrinkLoop();
                DrainDrinkLive();
                // Gulp fallback: no animation-driven gulp arrived in time (see nextGulpFallback
                // in State.cs — events die after a fast lower->re-raise) — play the def's own
                // sound manually and keep the throttle in sync so a late animation event can't
                // double-gulp. Manual gulps shed snack segments too (normally done per allowed
                // animation sound in AllowSound). ONLY when the name actually resolves in the
                // player's bank: a missing element is a silent no-op that would still claim
                // nextDrinkSound and throttle the clip's REAL events into silence (sausage's
                // 'Eat'-vs-'Take1' bug, 2026-06-12) — in that case stand down and leave the
                // throttle to the animation events.
                if (Time.time >= nextGulpFallback)
                {
                    nextGulpFallback = Time.time + drinkGulpInterval;
                    if (HasSoundElement(def?.eatSound))
                    {
                        PlaySound(def.eatSound);
                        UpdateSnackSegments();
                        nextDrinkSound = Time.time + drinkGulpInterval;
                    }
                }
                // Health-based finish: the LIVE resource just hit empty. Items without a
                // resource component finish on the use-time clock as before.
                bool done = drinkFdc != null && drinkLiveDrain
                    ? drinkFdc.HpPercent <= 0.0001f
                    : drinkHeldTime >= drinkUseTime * drinkRemainingFrac;
                if (done)
                {
                    Plugin.MyLog.LogInfo($"[ManualEat] Drank it all ({drinkHeldTime:F1}s).");
                    RequestFinish(cancel: false);
                }
                return true;
            }
            if (drinkingHeld)
            {
                drinkingHeld = false;
                playUntil = 0f;   // freeze the gulp mid-loop
                StopDrinkSound(); // cut the glug — re-raising restarts it from the top
                Plugin.MyLog.LogInfo($"[ManualEat] Lowered it at {drinkHeldTime:F1}/{drinkUseTime * drinkRemainingFrac:F1}s.");
            }
            return false;
        }

        // CanSpoon hybrid (mouthDrink): HOLD the CAN at your mouth and it drinks down the
        // item's use-time exactly like a Drink. The spoon path stays available from Ready;
        // a spoon scoop after a partial drink is capped at what's LEFT (see the per-bite
        // drain in StepEat), so "drink half, then one scoop finishes it". The can rides
        // the OFF hand (eatHand = the spoon hand), so the mouth check anchors there.
        private static bool StepCanDrink(EatStyle s) => DrinkHeldFrame(Other(s.eatHand));

        // Drink: HOLD the container at the mouth — the resource drains LIVE while held
        // (DrinkHeldFrame). Lowering it pauses; bringing the free hand back to the
        // cap/open zone + trigger recaps and finishes with just what was drunk (the
        // item keeps the rest — already applied by the live drain).
        private static void StepDrink(EatStyle s)
        {
            if (DrinkHeldFrame(s.eatHand)) return;

            // Recap/done: free hand back at the open zone (the cap area) + trigger.
            if (ZoneReached(s.openHand, Other(s.openHand), openZoneOffset, openDistance)
                && TriggerEdgeImpl(s.openHand == Hand.Dominant))
            {
                ReattachCap();
                Pulse();
                Plugin.MyLog.LogInfo($"[ManualEat] Recapped after {drinkHeldTime:F1}s.");
                RequestFinish(cancel: false); // fraction comes from the held time; 0 drunk = just put it away
            }
        }

        // Incremental at-mouth drain: apply the held-time fraction that hasn't been applied
        // yet, in drinkDrainStep chunks (CutPiece is non-mutating — it clones and scales —
        // so chunked applications sum EXACTLY to the single-shot total). Capped at what the
        // bottle had left, so over-holding can't over-apply. The drain rate IS the vanilla
        // rate: MaxResource over the template UseTime, nutrition proportional — the same
        // model DoMedEffect runs.
        private static void DrainDrinkLive()
        {
            if (!drinkLiveDrain) return;
            // Time-based progress is tracked SEPARATELY from the total applied
            // (drinkTimeAppliedFrac vs drinkAppliedFrac): a CanSpoon's spoon bites also
            // bump the total, and measuring the mouth drain against it would stall the bar
            // until the held time "caught up" to what the spoon already ate. The time
            // delta is capped at the LIVE remaining, so mixed mouth+spoon can't over-apply.
            float timeTarget = drinkUseTime > 0f ? drinkHeldTime / drinkUseTime : drinkRemainingFrac;
            float live = drinkFdc != null && drinkFdc.MaxResource > 0f
                ? Mathf.Clamp01(drinkFdc.HpPercent / drinkFdc.MaxResource)
                : Mathf.Max(0f, drinkRemainingFrac - drinkAppliedFrac); // no component: budget the unapplied rest
            float delta = Mathf.Min(timeTarget - drinkTimeAppliedFrac, live);
            // Once the held time covers everything that's left, the FINAL sliver is due
            // even though it's under a full chunk — without this the bar stalled one step
            // short (observed: water stuck at 1/60, the empty-finish never fired and only
            // the recap's remainder pass drained it).
            bool last = live > 0f && timeTarget - drinkTimeAppliedFrac >= live - 0.0001f;
            if (delta >= drinkDrainStep || (last && delta > 0f))
            {
                ApplyConsumedNutrition(delta, quiet: true);
                drinkTimeAppliedFrac += delta;
                drinkAppliedFrac += delta;
            }
            // Chunked float math can leave a hair of resource after the last application —
            // snap it to a true 0 so the health-based finish/discard checks fire.
            if (last && drinkFdc != null && drinkFdc.HpPercent > 0f && drinkFdc.HpPercent <= drinkFdc.MaxResource * 0.01f)
                drinkFdc.HpPercent = 0f;
        }

        // Cut the currently-playing gulp audio. BaseSoundPlayer routes every clip through one
        // pooled BetterSource. The cut is BetterSource.Stop() — NOT ReleaseClipsSource:
        // Release hands the source back to BetterAudio's pool mid-play, and a fast
        // lower→re-raise wedged the player into permanent silence (reported 2026-06-12).
        // Stop() keeps the source owned by the sound player (_isClipSourceReleased stays
        // false), the next gulp's PlayClipInternal just reuses it, and the vanilla
        // BaseSoundPlayer.Update auto-release handles eventual cleanup. The drinkingHeld
        // transition resets nextDrinkSound, so re-raising restarts the glug instantly.
        private static void StopDrinkSound()
        {
            try { if (cutDrinkSoundOnLower) soundPlayer?._clipsSource?.Stop(); }
            catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] StopDrinkSound: {ex.Message}"); }
            TarkovVR.Source.Misc.AudioHaptics.Stop(); // the hand buzz cuts with the audio
        }

        // Keep the gulp/chew animation running while the item is at the mouth. The natural
        // USE<->EAT transitions are effect-driven (and the effect is suppressed), so whenever
        // the animator runs out of the loop segment — or strays toward OPEN/END — replay it
        // from the top. The loop state is per-food (eatLoopState; 0 = STATE_USE): sausage's
        // clip uses non-standard hashes, so it overrides.
        private static void DriveDrinkLoop()
        {
            playUntil = Time.time + 0.1f; // animator keeps playing while held
            try
            {
                int loop = eatLoopState != 0 ? eatLoopState : STATE_USE_HASH;
                var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                bool inDrink = st.fullPathHash == loop || st.fullPathHash == STATE_USE_HASH || st.fullPathHash == STATE_EAT_HASH;
                // Throttled: a WRONG loop hash (sausage's is a guess) never enters the
                // state, and an unthrottled retry would hammer Play every frame.
                if ((!inDrink || st.normalizedTime >= 1f) && Time.time >= nextLoopPlay)
                {
                    nextLoopPlay = Time.time + 0.25f;
                    controller._player.ArmsAnimatorCommon.Play(loop, 1, 0f);
                }
            }
            catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] drink loop Play failed: {ex.Message}"); }
        }

        // Move the arms animator from STATE_OPEN into STATE_USE (the "use" phase). The brief
        // playUntil makes Tick run the animator a few frames so the state actually engages;
        // then it freezes. Needed so FinishSequence cancels from STATE_USE like every other
        // food (cancelling from STATE_OPEN = busy hands + the food won't discard). Used by the
        // Bag (shake) and Pack (take) gestures, whose grabs don't otherwise advance the animator.
        private static void EnterUseState()
        {
            // Non-standard graphs (noodles) have no STATE_USE — push into the food's own
            // use/chew state instead (eatLoopState, same resolution as DriveDrinkLoop).
            // With the bare STATE_USE the Play was a no-op there: the arms stayed in OPEN
            // and the finish rode the deadline fallback straight into the busy-hands
            // cancel-from-OPEN bug.
            int use = eatLoopState != 0 ? eatLoopState : STATE_USE_HASH;
            try { controller._player.ArmsAnimatorCommon.Play(use, 1, 0f); }
            catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] EnterUseState failed: {ex.Message}"); }
            playUntil = Time.time + 0.15f;
        }

        // Reset shake tracking on entering a shake-ready phase (so the first frame's velocity
        // isn't a huge jump and stale reversals don't carry over).
        private static void ResetShake()
        {
            shakeReversals = 0;
            shakeWindowEnd = 0f;
            shakePrevVel = Vector3.zero;
            Transform rh = RightHand(); Transform head = GetHead();
            shakePrevPos = (rh != null && head != null) ? head.InverseTransformPoint(rh.position) : Vector3.zero;
        }

        // Detect a shake: the bag (right hand) wiggling near the left hand. Velocity is
        // measured in HEAD-LOCAL space so walking/turning doesn't count. Each direction
        // reversal above shakeMinSpeed is a wiggle; shakeReversalsNeeded within shakeWindow
        // seconds = a shake.
        private static bool DetectShake()
        {
            Transform rh = RightHand(); Transform head = GetHead();
            float dt = Time.deltaTime;
            if (rh == null || head == null || dt <= 0f) return false;

            Vector3 bagPalm = ProbePoint(Hand.Dominant); // the bag rides the dominant palm
            Vector3 cur = head.InverseTransformPoint(bagPalm);
            Vector3 vel = (cur - shakePrevPos) / dt;
            shakePrevPos = cur;

            bool near = LeftHand() != null && Vector3.Distance(bagPalm, ProbePoint(Hand.Off)) < shakeNearDistance;
            bool reversal = near
                && vel.magnitude > shakeMinSpeed && shakePrevVel.magnitude > shakeMinSpeed
                && Vector3.Dot(vel.normalized, shakePrevVel.normalized) < -0.2f;
            shakePrevVel = vel;

            if (!reversal) return false;
            if (Time.time > shakeWindowEnd) shakeReversals = 0; // window lapsed -> start over
            shakeReversals++;
            shakeWindowEnd = Time.time + shakeWindow;
            if (shakeReversals >= shakeReversalsNeeded) { shakeReversals = 0; return true; }
            return false;
        }

        // Begin a per-gesture animation pulse (the bit of finger/hand motion played on
        // each grab/eat). With deterministicGesturePose it first SNAPS the arms animator to
        // a fixed (state, normalizedTime) so every grab/eat replays the SAME segment —
        // identical finger pose + food deform each bite. Without it the pulse free-runs from
        // wherever the last bite froze, so the timeline drifts and each bite looks different
        // (the sprats symptom: each grabbed fish a different orientation). bitePlayTime is
        // how long the segment plays before it freezes again.
        private static void StartGesturePulse(int stateHash, float startTime)
        {
            if (deterministicGesturePose)
            {
                try { controller._player.ArmsAnimatorCommon.Play(stateHash, 1, startTime); }
                catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] gesture Play({stateHash}) failed: {ex.Message}"); }
            }
            playUntil = Time.time + bitePlayTime;
        }

        // Current arms layer-1 state hash + normalizedTime, for the gesture-pose log.
        private static string CurStateStr()
        {
            try { var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1); return $"state={st.fullPathHash} t={st.normalizedTime:F3}"; }
            catch { return "state=?"; }
        }
    }
}
