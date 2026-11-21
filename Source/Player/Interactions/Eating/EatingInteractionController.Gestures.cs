using System;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // The gesture state machine: one shared open -> take -> eat/drink loop, driven by the
    // per-archetype EatStyle descriptor (BuildStyle). A new archetype needs a BuildStyle
    // case, its prop wiring (Props.cs) and a factory (Foods.cs).
    internal static partial class EatingInteractionController
    {
        //--- Gesture state machine --------------------------------------------------
        // Per-archetype differences (hand roles, the arms state the open plays, the reveal/advance hooks) live in the
        // EatStyle built for each food at spawn. A new type also needs its prop setup and its LateZeroProps case.
        private static EatStyle BuildStyle(FoodDef foodDef)
        {
            EatStyle s = BuildStyleCore(foodDef);
            // Per-food open-state override (OpenState(...)): some rigs run their own animator graph (noodles has no
            // STATE_USE), and Play() on a state the graph lacks is a silent no-op.
            if (foodDef.openState != 0) s.openStateHash = foodDef.openState;
            return s;
        }

        private static EatStyle BuildStyleCore(FoodDef foodDef)
        {
            switch (foodDef.kind)
            {
                // Chocolate bar: held in the right hand, the left (off) hand peels the wrapper, then it goes straight to
                // the mouth (no take step). The whole clip is STATE_USE. eatByTime foods (sausage, tarka, noodles) are held
                // at the mouth and drain the use time like a drink instead of being bitten.
                case FoodKind.Handheld:
                    return new EatStyle
                    {
                        label = "wrapper",
                        openHand = Hand.Off,
                        eatHand = Hand.Dominant,
                        takeHand = Hand.Off,
                        hasTakeStep = false,
                        timedSip = foodDef.eatByTime,
                        takeKind = TakeKind.None,
                        openStateHash = STATE_USE_HASH,
                        logsGesturePose = false,
                        onOpened = () => DetachWrapperToLeftHand(), // the peeled cover stays in the left hand (no-op without a cover)
                    };
                // Drink: the container is in the holding hand (right unless holdInOffHand) and the free hand pops the cap.
                // Held at the mouth, StepDrink runs the use time down. Capped bottles can be recapped to stop early.
                case FoodKind.Drink:
                {
                    Hand hold = foodDef.holdInOffHand ? Hand.Off : Hand.Dominant;
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
                        onOpened = () => DetachCapToFreeHand(), // the popped cap goes with the free hand
                    };
                }
                // Bag (croutons): held in the right hand and opened by the left. A shake pours crackers into the left hand,
                // which eats them. The shake and eat don't advance the animator, so the take pushes it into STATE_USE (EnterUseState).
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
                // Reach-into-bag (iskra, MRE): held in the right hand. The left rips the top open, reaches into the bag and
                // eats from there (StepReachTake: the grab fires onTake, pulling out switches to Holding). The reach scrub
                // leaves the arms in STATE_USE, so the finish never sees STATE_OPEN.
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
                        onTake = () => TakeFoodToLeftHand(), // the cracker goes to the reaching hand
                        onEatHide = () => SetRenderer(foodR, false),
                    };
                // Pack (galette): the mirror of CanHand. Held in the right hand; the left opens it, takes a piece and eats it.
                // The grab and the eat play gesture pulses (see StartGesturePulse).
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
                // Canned (tushonka with a spoon, sprats by hand): the can is in the left hand. The right rolls the lid open,
                // takes the food (onto the spoon, or by hand) and eats it.
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
                    // Drinks are held to the mouth from Ready. With a take step you take first
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

        // Open: StepOpenPull latches the opening hand onto the held item and the pull scrubs the clip up to pullEndTime.
        // Once playingOpen is set, the wait branch below lets the clip play on to openReadyTime (the settle after the
        // unlatch), then freezes it and runs onOpened.
        private static void StepOpen(EatStyle s)
        {
            if (playingOpenAnim) { WaitOpenAnim(s); return; } // the one-shot open animation (the rip) is playing
            if (!playingOpen)
            {
                StepOpenPull(s);
                return;
            }

            if (!playingOpen)
            {
                // the held item is in the other (holding) hand
                if (ZoneReached(s.openHand, Other(s.openHand), openZoneOffset, openDistance) && TriggerEdgeImpl(s.openHand == Hand.Dominant))
                {
                    playingOpen = true;
                    controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                    Pulse();
                    LogStep($"[ManualEat] Opening {s.label}...");
                }
            }
            else
            {
                var stateInfo = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                bool past = stateInfo.fullPathHash != s.openStateHash || stateInfo.normalizedTime >= openReadyTime;
                if (past)
                {
                    playingOpen = false;
                    controller.FirearmsAnimator?.SetAnimationSpeed(0f);
                    FinishOpen(s);
                }
            }
        }

        // The open is done (the pull settled, or the clip reached openReadyTime): go Ready, or first play the one-shot
        // openPlayState for real. Some rigs put the visible open in a state the pull can't scrub: noodles' bag rip
        // is in the early frames of its first eat state (2073176132), not STATE_OPEN. The chew loop must then use
        // a different eat state, or every gulp rips the bag again.
        private static void FinishOpen(EatStyle s)
        {
            if (openPlayState != 0 && !openAnimPlayed)
            {
                openAnimPlayed = true; // one-shot
                playingOpenAnim = true;
                openAnimDeadline = Time.time + 2f; // fallback for a wrong or too-short state
                try { controller._player.ArmsAnimatorCommon.Play(openPlayState, 1, 0f); }
                catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] open-anim Play({openPlayState}) failed: {exception.Message}"); }
                controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                PulseHand(s.openHand == Hand.Dominant);
                LogStep($"[ManualEat] Playing open animation ({openPlayState}) to {openPlayMaxTime:F2}...");
                return;
            }
            s.onOpened?.Invoke();
            HideOnOpenRenderers(); // hide the torn cover/lid (noodles) so it doesn't float
            phase = Phase.Ready;
            PulseHand(s.openHand == Hand.Dominant);
            LogStep($"[ManualEat] Opened {s.label} — ready (take={s.hasTakeStep}).");
        }

        // Hide the configured torn-cover renderers once the open is done (FoodDef.hideOnOpenName).
        // One-shot; RestoreProps re-shows them. No-op unless the food set a name.
        private static void HideOnOpenRenderers()
        {
            if (hideOnOpenR == null || hiddenOnOpen) return;
            for (int i = 0; i < hideOnOpenR.Length; i++) SetRenderer(hideOnOpenR[i], false);
            hiddenOnOpen = true;
            LogStep($"[ManualEat] Hid the torn cover ({def.hideOnOpenName}).");
        }

        // Wait for the one-shot open animation (the rip) to end, then freeze and go Ready. It stops at
        // openPlayMaxTime (so it doesn't run on into the first bite) or when the state ends or changes.
        // openAnimDeadline is the safety net for a wrong hash.
        private static void WaitOpenAnim(EatStyle s)
        {
            bool past;
            try
            {
                var stateInfo = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                past = stateInfo.fullPathHash != openPlayState || stateInfo.normalizedTime >= openPlayMaxTime;
            }
            catch { past = true; }
            if (!past && Time.time < openAnimDeadline) return;
            playingOpenAnim = false;
            controller.FirearmsAnimator?.SetAnimationSpeed(0f);
            s.onOpened?.Invoke();
            HideOnOpenRenderers(); // the rip has played: hide the torn cover so it doesn't float
            phase = Phase.Ready;
            LogStep($"[ManualEat] Open animation done — ready (take={s.hasTakeStep}).");
        }

        // Physical open. Squeezing the trigger at the open zone latches the opening hand onto the held item
        // (LatchHandToItem: the IK target and wrist pin aim at a latch under the holding hand's rig target). Each grab
        // maps two stages onto the open clip's normalizedTime, between the pull start and pullEndTime:
        //   1. Squeeze: the trigger axis scrubs the first triggerOpenPortion. The Trigger kind scrubs the whole open,
        //      which completes on a full press followed by a full release.
        //   2. Physical: once the trigger is fully pressed, def.openGesture drives the rest (Pull = palm travel from the
        //      full-press point, Tilt = how far the hand tips off its full-press orientation). It is measured relative to
        //      the holding hand so walking or turning doesn't scrub, and backing off rolls it back.
        // Releasing the trigger unlatches the hand and keeps the progress. The clip still animates the latched hand's
        // fingers (the pin only owns the wrist). Completion goes through CompleteOpenPull (settle or Ready).
        private static void StepOpenPull(EatStyle s)
        {
            Transform acting = HandT(s.openHand);
            Transform anchor = HandT(Other(s.openHand)); // the held item rides this hand
            if (acting == null || anchor == null) return;

            bool dominant = s.openHand == Hand.Dominant;
            float axis = TriggerAxisImpl(dominant);
            // Keep the shared trigger-edge state current. The drink recap (StepDrink) detects its edge from these previous
            // values via TriggerEdgeImpl, and a stale 0 with the trigger still held from the open would fire a false recap.
            float prevAxis = dominant ? prevTriggerAxis : prevOffTriggerAxis;
            if (dominant) prevTriggerAxis = axis; else prevOffTriggerAxis = axis;

            OpenGestureKind kind = def != null ? def.openGesture : OpenGestureKind.Pull;
            float triggerSpan = kind == OpenGestureKind.Trigger ? 1f : Mathf.Clamp01(triggerOpenPortion);

            if (!pullingOpen)
            {
                // Start when the trigger crosses pullLatchAxis at the open zone. It must be an edge, so a trigger
                // already held while reaching in doesn't grab by itself.
                if (axis < pullLatchAxis || prevAxis >= pullLatchAxis
                    || !ZoneReached(s.openHand, Other(s.openHand), openZoneOffset, openDistance)) return;

                // First grab: map progress 0 to wherever the animator froze after the draw (Wrapper foods spawn
                // straight into STATE_USE).
                if (pullAnimStart < 0f)
                {
                    float t0 = 0f;
                    try
                    {
                        var stateInfo = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                        if (stateInfo.fullPathHash == s.openStateHash)
                            t0 = Mathf.Clamp(stateInfo.normalizedTime, 0f, PullEndResolved() - 0.01f);
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
                LogStep($"[ManualEat] Open grab ({s.label}, {kind}) at {pullProgress:P0}.");
                return;
            }

            // Trigger kind, fully scrubbed: wait at full for the release, which completes the open. Checked before the
            // unlatch, because this release means done, not let go.
            if (pullAwaitRelease)
            {
                if (axis <= pullReleaseAxis) CompleteOpenPull(s);
                return;
            }

            // Released: unlatch (the hand goes back to the controller). The progress is kept.
            if (axis <= pullReleaseAxis)
            {
                EndPull();
                LogStep($"[ManualEat] Open released at {pullProgress:P0}.");
                return;
            }

            if (!pullSqueezeDone)
            {
                // Squeeze: the trigger axis scrubs the first triggerSpan of the open, both ways, but never below where this
                // grab started (the Trigger kind scrubs the whole open). At full press the physical gesture takes over.
                // Its references are captured here, so only motion made while fully pressed counts.
                float a = Mathf.InverseLerp(pullLatchAxis, pullFullPressAxis, axis);
                pullProgress = Mathf.Max(pullGrabProgress, Mathf.Clamp01(a * triggerSpan));
                ScrubOpen(s);
                FirePullMilestoneSounds();
                if (axis >= pullFullPressAxis)
                {
                    if (kind == OpenGestureKind.Trigger) { pullAwaitRelease = true; return; }
                    pullSqueezeDone = true;
                    pullGrabProgress = pullProgress; // the physical phase's floor
                    // Measured relative to the holding hand (palm position in its frame, rotation against its rotation), so
                    // walking and body turns don't scrub. The palm offset is frozen here: tracking the live finger curl
                    // would feed the clip's own finger animation back into the travel.
                    pullPalmLocal = PalmOffsetLocal(s.openHand);
                    pullGrabLocal = anchor.InverseTransformPoint(acting.TransformPoint(pullPalmLocal));
                    pullGrabRel = Quaternion.Inverse(anchor.rotation) * acting.rotation;
                    PulseHand(dominant); // haptic click: the physical stage is engaged
                }
                return;
            }

            // Physical stage: the pull travel or tilt since the full press sets the progress.
            float measure = MeasureOpenGesture(kind, acting, anchor);
            float scale = kind == OpenGestureKind.Tilt ? openTiltDegrees : openPullDistance;
            pullProgress = Mathf.Clamp01(pullGrabProgress + Mathf.Max(0f, measure) / Mathf.Max(0.01f, scale));
            ScrubOpen(s);
            FirePullMilestoneSounds();
            if (pullProgress >= 1f) CompleteOpenPull(s);
        }

        // How far the physical gesture has gone since the full press, relative to the anchor hand (Pull = metres of palm
        // travel, Tilt = degrees). Wrist-roll twist reads too inconsistently for screw caps, so they use Tilt.
        private static float MeasureOpenGesture(OpenGestureKind kind, Transform acting, Transform anchor)
        {
            if (kind == OpenGestureKind.Pull)
                // Magnitude (not a fixed axis) so any pull direction works; moving back toward the grab rolls it back.
                return (anchor.InverseTransformPoint(acting.TransformPoint(pullPalmLocal)) - pullGrabLocal).magnitude;

            // Tilt: how far the hand's forward tipped off the full-press orientation, any direction (caps, flip-top
            // lids, can tabs).
            Quaternion rel = Quaternion.Inverse(anchor.rotation) * acting.rotation;
            return Vector3.Angle(pullGrabRel * Vector3.forward, rel * Vector3.forward);
        }

        // A finished open (the pull reached 1, or the Trigger press and release are done): unlatch, then settle or go Ready.
        // The pull only covers pullStart -> pullEnd. A clip tail past that (the tushonka spoon grab) plays on its own from
        // pullEnd to openReadyTime with the hand unlatched, and StepOpen's wait branch freezes it there and runs
        // onOpened. With pullEnd at openReadyTime (the default) the open finishes at once.
        private static void CompleteOpenPull(EatStyle s)
        {
            ReportOpenMovers(); // logs the lid-bone hint (no-op once reported, or with OpenGrip set)
            EndPull();
            if (PullEndResolved() < openReadyTime - 0.001f)
            {
                playingOpen = true;
                controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                PulseHand(s.openHand == Hand.Dominant);
                LogStep($"[ManualEat] Open done ({s.label}) — settling to the ready pose...");
                return;
            }
            FinishOpen(s); // -> Ready, or play the one-shot open animation (rip) first
        }

        // Drive the open clip's playhead to the pull progress. The animator stays at speed 0 (Tick keeps it there
        // while !playingOpen) and still re-applies the bone pose every frame. Clip sound events never fire from a
        // seek: the milestone sounds below stand in for them.
        private static void ScrubOpen(EatStyle s)
        {
            try { controller._player.ArmsAnimatorCommon.Play(s.openStateHash, 1, CurrentScrubTime()); }
            catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] pull scrub Play failed: {exception.Message}"); }
        }

        // The open-state normalizedTime the current pull progress maps to (shared by the animator scrub and the
        // HandPath sampling, so the hand and the lid stay in sync). pullStartTime (when set) overrides the
        // auto-captured start.
        private static float CurrentScrubTime()
        {
            float end = PullEndResolved();
            float start = pullStartTime >= 0f ? Mathf.Min(pullStartTime, end - 0.01f)
                        : pullAnimStart < 0f ? 0f : pullAnimStart;
            return Mathf.Lerp(start, end, pullProgress);
        }

        // Where progress 1 of the pull lands: pullEndTime, capped at openReadyTime.
        // Unset (-1) means openReadyTime (no settle segment).
        private static float PullEndResolved()
            => pullEndTime > 0f ? Mathf.Min(pullEndTime, openReadyTime) : openReadyTime;

        // Play each of the food's openSounds once (pullSoundsFired only counts up, so scrubbing back doesn't replay them).
        // The slots sit on the open-state time axis, evenly spaced between the pull start and openReadyTime unless
        // openSoundTimes gives them. A slot past pullEndTime is not played here: the settle plays the clip's tail for
        // real and its own sound events cover it (playing it from the pull too doubles the sound, e.g. tushonka's SpoonTake).
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
                if (slot > end || t < slot) break; // slots are in order, so later ones can't be due either
                PlaySound(sounds[pullSoundsFired]);
                PulseHand(style != null && style.openHand == Hand.Dominant);
                pullSoundsFired++;
            }
        }

        // Take: the take gesture (reaching the holding hand, or shaking the bag) shows or moves the food onto the eating
        // hand, advances the animator and goes to Holding. The reach-into-bag take is a multi-frame latch with its own step.
        private static void StepTake(EatStyle s)
        {
            if (s.takeKind == TakeKind.ReachIn) { StepReachTake(s); return; }
            // CanSpoon hybrid: holding the can itself at the mouth drinks it down instead of scooping (condensed
            // milk). Returns true while the mouth path owns the frame.
            if (def != null && def.mouthDrink && StepCanDrink(s)) return;
            bool triggered = s.takeKind == TakeKind.Shake
                ? DetectShake()
                : ZoneReached(s.takeHand, Other(s.takeHand), takeZoneOffset, scoopDistance);
            if (!triggered) return;

            if (logGesturePose && s.logsGesturePose)
                LogStep($"[ManualEat] take #{biteCount + 1} pulse from {CurStateStr()}");
            s.onTake?.Invoke();
            PlaySound(def.scoopSound);
            phase = Phase.Holding;
            LogStep($"[ManualEat] Took {s.label} (round {biteCount + 1}/{def.bites}).");
        }

        // Reach-into-bag take (iskra, MRE). Entering the take zone (the bag mouth) latches the reaching hand, which
        // DriveReachLatch then places, and the reach depth scrubs the clip's reach segment. The trigger while deep grabs
        // the cracker into the reaching hand. Pulling back past the entry point completes the take (-> Holding).
        // Backing out empty-handed, or pulling sideways off the reach line, just releases the hand.
        private static void StepReachTake(EatStyle s)
        {
            Transform acting = HandT(s.takeHand);
            Transform anchor = HandT(Other(s.takeHand)); // the bag is in this hand
            if (acting == null || anchor == null) return;

            if (!reachingIn)
            {
                if (!ZoneReached(s.takeHand, Other(s.takeHand), takeZoneOffset, scoopDistance)) return;
                BeginReach(s, acting, anchor);
                return;
            }

            // Update the trigger edge state on every latched frame. A stale previous value would fire a false grab the
            // moment the depth gate opens with the trigger held.
            bool trigEdge = TriggerEdgeImpl(s.takeHand == Hand.Dominant);

            // Depth and sideways distance in the anchor frame, from the entry values frozen at the latch. The scrub animates
            // the latched fingers, so a live palm offset would feed the clip back into the measurement (as with pullPalmLocal).
            Vector3 palmLocal = anchor.InverseTransformPoint(acting.TransformPoint(reachPalmLocal));
            float depth = Vector3.Dot(palmLocal - reachEntryPalmLocal, reachAxisLocal);
            float lateral = (palmLocal - (reachEntryPalmLocal + reachAxisLocal * depth)).magnitude;

            // Leaving (back past the entry point, or sideways off the reach line) releases the hand. With the cracker
            // grabbed, either exit completes the take.
            if (depth < -reachExitDepth || lateral > reachLateralEscape)
            {
                bool grabbed = reachGrabbed;
                EndReach();
                if (grabbed)
                {
                    phase = Phase.Holding;
                    LogStep($"[ManualEat] Pulled the cracker out (round {biteCount + 1}/{def.bites}).");
                }
                else LogStep("[ManualEat] Left the bag empty-handed — hand released.");
                return;
            }

            // Physical depth scrubs the reach segment (backing out rolls it back), with the same speed-0 seek as ScrubOpen.
            // reachStartTime may be greater than reachDeepTime; the lerp works either way.
            reachProgress = Mathf.Clamp01(depth / Mathf.Max(0.01f, reachDepthDistance));
            try { controller._player.ArmsAnimatorCommon.Play(STATE_USE_HASH, 1, Mathf.Lerp(reachStartTime, reachDeepTime, reachProgress)); }
            catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] reach scrub Play failed: {exception.Message}"); }

            // The trigger while deep grabs: the cracker appears in the reaching hand (its holder hangs off that wrist,
            // which is pinned to the latch). No animator pulse here: it would fight the scrub.
            if (!reachGrabbed && trigEdge && reachProgress >= reachGrabDepth)
            {
                reachGrabbed = true;
                if (logGesturePose && s.logsGesturePose)
                    LogStep($"[ManualEat] grab #{biteCount + 1} from {CurStateStr()}");
                s.onTake?.Invoke(); // the cracker goes onto the reaching hand's holder and is shown
                PlaySound(def.scoopSound);
                PulseHand(s.takeHand == Hand.Dominant);
                LogStep($"[ManualEat] Grabbed the cracker at {reachProgress:P0} deep — pull it out.");
            }
        }

        // Eat: when the eating hand (plus the eat-zone offset) reaches the mouth, hide the eaten piece and count the bite,
        // then finish (last bite) or go on to the next round. Used by the Holding phase (foods with a take step) and the
        // Ready phase (Handheld: open, then bite).
        private static void StepEat(EatStyle s)
        {
            if (!EatZoneReached(s.eatHand)) return;

            if (logGesturePose && s.logsGesturePose)
                LogStep($"[ManualEat] eat #{biteCount + 1} pulse from {CurStateStr()}");
            s.onEatHide?.Invoke();
            biteCount++;
            Pulse();
            PlaySound(def.eatSound);
            LogStep($"[ManualEat] Ate {biteCount}/{def.bites}.");

            // Foods with a resource drain per bite: what was left at spawn is divided by the def's bites (oat flakes 40/40
            // at bites: 4 = 10 per shake), and each chunk is capped at what is left now, so a partly drunk CanSpoon takes
            // fewer scoops. The finish applies only the remainder not yet applied (drinkAppliedFrac).
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

            // Finish on the bite count or when the resource is empty: drinking from the can as well as spooning can
            // empty it before the count runs out.
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

        // One frame of drinking at the mouth, used by StepDrink (Drink and eatByTime foods) and StepCanDrink (the
        // CanSpoon mouthDrink hybrid). While the item is at the mouth (mouthHand = the hand holding it) it runs the gulp
        // loop and the live drain; the frame it is lowered, it pauses (freezes the gulp, cuts the audio). It finishes
        // when the resource is empty (by the use-time clock if the item has no resource component). Returns true while
        // the item is at the mouth.
        private static bool DrinkHeldFrame(Hand mouthHand)
        {
            // Hysteresis: entering uses the exact zone, but once drinking the exit boundary is wider
            // (drinkZoneExitScale) so a steady hold can't flicker the clock on/off.
            if (EatZoneReached(mouthHand, drinkingHeld ? drinkZoneExitScale : 1f))
            {
                if (!drinkingHeld)
                {
                    drinkingHeld = true;
                    nextDrinkSound = 0f; // first gulp plays immediately
                    nextGulpFallback = Time.time + GulpFallbackGrace; // the animation event gets the first chance
                    Pulse();
                    LogStep("[ManualEat] Drinking...");
                }
                drinkHeldTime += Time.deltaTime;
                DriveDrinkLoop();
                DrainDrinkLive();
                // Gulp fallback: no gulp came from the animation in time (its events stop after a fast lower and re-raise,
                // see nextGulpFallback in State.cs). Play the def's sound by hand, shed a snack segment (normally done per
                // allowed animation sound in AllowSound), and sync the throttle so a late animation event can't gulp twice.
                // Only when the sound name exists in the player's bank: a missing one plays nothing but would still set
                // nextDrinkSound and silence the clip's real events.
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
                // Finish when the live resource hits empty; items without a resource component use the use-time clock.
                bool done = drinkFdc != null
                    ? drinkFdc.HpPercent <= 0.0001f
                    : drinkHeldTime >= drinkUseTime * drinkRemainingFrac;
                if (done)
                {
                    LogStep($"[ManualEat] Drank it all ({drinkHeldTime:F1}s).");
                    RequestFinish(cancel: false);
                }
                return true;
            }
            if (drinkingHeld)
            {
                drinkingHeld = false;
                playUntil = 0f;   // freeze the gulp mid-loop
                StopDrinkSound(); // cut the glug; raising it again restarts it from the start
                LogStep($"[ManualEat] Lowered it at {drinkHeldTime:F1}/{drinkUseTime * drinkRemainingFrac:F1}s.");
            }
            return false;
        }

        // CanSpoon hybrid (mouthDrink): the can held at the mouth drinks down the use time like a Drink.
        // The spoon stays usable from Ready, and a scoop after a partial drink is capped at what's left (the
        // per-bite drain in StepEat). The can is in the off hand (eatHand is the spoon hand), so the mouth check
        // uses that hand.
        private static bool StepCanDrink(EatStyle s) => DrinkHeldFrame(Other(s.eatHand));

        // Drink: holding the container at the mouth drains the resource live (DrinkHeldFrame). Lowering it pauses.
        // The free hand back at the open zone (the cap) plus the trigger recaps it and finishes with just what was
        // drunk; the item keeps the rest.
        private static void StepDrink(EatStyle s)
        {
            if (DrinkHeldFrame(s.eatHand)) return;

            if (ZoneReached(s.openHand, Other(s.openHand), openZoneOffset, openDistance)
                && TriggerEdgeImpl(s.openHand == Hand.Dominant))
            {
                ReattachCap();
                Pulse();
                LogStep($"[ManualEat] Recapped after {drinkHeldTime:F1}s.");
                RequestFinish(cancel: false); // fraction comes from the held time; 0 drunk = just put it away
            }
        }

        // Live drain at the mouth: apply the held-time fraction not yet applied, in drinkDrainStep chunks, capped at what
        // the bottle has left. CutPiece clones and scales without changing the original, so the chunks add up exactly to
        // the single-shot total. The rate is vanilla's (MaxResource over the template UseTime, nutrition in proportion),
        // the same model DoMedEffect uses.
        private static void DrainDrinkLive()
        {
            // Time-based progress (drinkTimeAppliedFrac) is tracked apart from the total applied (drinkAppliedFrac).
            // A CanSpoon's spoon bites also add to the total, and draining against it would stall the bar until the held
            // time caught up. The time delta is capped at what is left, so drinking plus spooning can't apply too much.
            float timeTarget = drinkUseTime > 0f ? drinkHeldTime / drinkUseTime : drinkRemainingFrac;
            float live = drinkFdc != null && drinkFdc.MaxResource > 0f
                ? Mathf.Clamp01(drinkFdc.HpPercent / drinkFdc.MaxResource)
                : Mathf.Max(0f, drinkRemainingFrac - drinkAppliedFrac); // no component: the part not yet applied
            float delta = Mathf.Min(timeTarget - drinkTimeAppliedFrac, live);
            // Once the held time covers everything left, the final sliver is due even though it's under a full
            // chunk. Without this the bar stops one step short and the finish on empty never fires.
            bool last = live > 0f && timeTarget - drinkTimeAppliedFrac >= live - 0.0001f;
            if (delta >= drinkDrainStep || (last && delta > 0f))
            {
                ApplyConsumedNutrition(delta, quiet: true);
                drinkTimeAppliedFrac += delta;
                drinkAppliedFrac += delta;
            }
            // Chunked float math can leave a tiny bit of resource after the last application: snap it to a true 0 so
            // the finish and discard checks fire.
            if (last && drinkFdc != null && drinkFdc.HpPercent > 0f && drinkFdc.HpPercent <= drinkFdc.MaxResource * 0.01f)
                drinkFdc.HpPercent = 0f;
        }

        // Cut the playing gulp audio. BaseSoundPlayer routes every clip through one pooled BetterSource. Use
        // BetterSource.Stop(), not ReleaseClipsSource: Release hands the source back to BetterAudio's pool mid-play,
        // and a fast lower and re-raise then leaves the player silent for good. Stop() keeps the source owned
        // (_isClipSourceReleased stays false), the next gulp's PlayClipInternal reuses it, and
        // BaseSoundPlayer.Update releases it later.
        private static void StopDrinkSound()
        {
            try { soundPlayer?._clipsSource?.Stop(); }
            catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] StopDrinkSound: {exception.Message}"); }
            TarkovVR.Source.Misc.AudioHaptics.Stop(); // the hand buzz cuts with the audio
        }

        // Keep the gulp/chew animation running while the item is at the mouth. The USE<->EAT transitions are
        // effect-driven and the effect is suppressed, so whenever the animator runs out of the loop segment or
        // drifts toward OPEN/END, replay it from the start. The loop state is set per food (eatLoopState; 0 = STATE_USE)
        // because sausage's clip uses non-standard hashes.
        private static void DriveDrinkLoop()
        {
            playUntil = Time.time + 0.1f; // animator keeps playing while held
            try
            {
                int loop = eatLoopState != 0 ? eatLoopState : STATE_USE_HASH;
                var stateInfo = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                bool inDrink = stateInfo.fullPathHash == loop || stateInfo.fullPathHash == STATE_USE_HASH || stateInfo.fullPathHash == STATE_EAT_HASH;
                // Throttled: a wrong loop hash (sausage's is a guess) never enters the state, and an unthrottled
                // retry would hammer Play every frame.
                if ((!inDrink || stateInfo.normalizedTime >= 1f) && Time.time >= nextLoopPlay)
                {
                    nextLoopPlay = Time.time + 0.25f;
                    controller._player.ArmsAnimatorCommon.Play(loop, 1, 0f);
                }
            }
            catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] drink loop Play failed: {exception.Message}"); }
        }

        // Move the arms animator from STATE_OPEN into STATE_USE. The short playUntil lets Tick run it for a few frames
        // so the state takes hold, then it freezes. FinishSequence must cancel from STATE_USE: cancelling from
        // STATE_OPEN leaves the hands busy and the food can't be discarded. Used by the Bag (shake) and Pack (take)
        // gestures, whose grabs don't otherwise advance the animator.
        private static void EnterUseState()
        {
            // Non-standard graphs (noodles) have no STATE_USE: push into the food's own use/chew state (eatLoopState,
            // as in DriveDrinkLoop). Play(STATE_USE) is a no-op there and the arms would stay in OPEN.
            int use = eatLoopState != 0 ? eatLoopState : STATE_USE_HASH;
            try { controller._player.ArmsAnimatorCommon.Play(use, 1, 0f); }
            catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] EnterUseState failed: {exception.Message}"); }
            playUntil = Time.time + 0.15f;
        }

        // Reset shake tracking on entering a shake-ready phase (so the first frame's velocity
        // isn't a huge jump and stale reversals don't carry over).
        private static void ResetShake()
        {
            shakeReversals = 0;
            shakeWindowEnd = 0f;
            shakePrevVel = Vector3.zero;
            Transform rightHand = RightHand(); Transform head = GetHead();
            shakePrevPos = (rightHand != null && head != null) ? head.InverseTransformPoint(rightHand.position) : Vector3.zero;
        }

        // Detect a shake: the bag (right hand) wiggling near the left hand. Velocity is measured in head-local
        // space so walking/turning doesn't count. Each direction reversal above shakeMinSpeed is a wiggle;
        // shakeReversalsNeeded within shakeWindow seconds = a shake.
        private static bool DetectShake()
        {
            Transform rightHand = RightHand(); Transform head = GetHead();
            float deltaTime = Time.deltaTime;
            if (rightHand == null || head == null || deltaTime <= 0f) return false;

            Vector3 bagPalm = ProbePoint(Hand.Dominant); // the bag is held in the dominant palm
            Vector3 cur = head.InverseTransformPoint(bagPalm);
            Vector3 vel = (cur - shakePrevPos) / deltaTime;
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

        // Start a per-gesture animation pulse (the finger/hand motion played on each grab or eat). With
        // deterministicGesturePose it first snaps the arms animator to a fixed (state, normalizedTime), so every
        // grab or eat replays the same segment. Without it the pulse runs on from wherever the last bite froze, and
        // each bite looks different. bitePlayTime = how long the segment plays before it freezes again.
        private static void StartGesturePulse(int stateHash, float startTime)
        {
            if (deterministicGesturePose)
            {
                try { controller._player.ArmsAnimatorCommon.Play(stateHash, 1, startTime); }
                catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] gesture Play({stateHash}) failed: {exception.Message}"); }
            }
            playUntil = Time.time + bitePlayTime;
        }

        // Current arms layer-1 state hash + normalizedTime, for the gesture-pose log.
        private static string CurStateStr()
        {
            try { var stateInfo = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1); return $"state={stateInfo.fullPathHash} t={stateInfo.normalizedTime:F3}"; }
            catch { return "state=?"; }
        }
    }
}
