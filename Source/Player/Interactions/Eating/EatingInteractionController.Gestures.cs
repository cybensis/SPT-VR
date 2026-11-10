using System;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // The gesture state machine: one shared open->take->eat/drink loop driven by the
    // per-archetype EatStyle descriptor (BuildStyle). A new archetype is a BuildStyle
    // case + prop wiring (Props.cs) + a factory (Foods.cs).
    internal static partial class EatingInteractionController
    {
        //--- Gesture state machine --------------------------------------------------
        // Per-archetype differences (hand roles, the arms state the open plays, reveal/advance hooks) live in the
        // EatStyle built per food on spawn. To add a type: a new case here + its prop setup / late-zero tails.
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
                // Chocolate bar: held in the right hand, the left (off) hand peels the wrapper, then bite straight to
                // the mouth, no take step. The whole clip is STATE_USE. eatByTime foods (sausage/tarka/noodles) hold
                // at the mouth and drain the use-time like a drink instead of a bite.
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
                        onOpened = () => DetachWrapperToLeftHand(), // peeled -> stays in the left hand (no-op without a cover)
                    };
                // Drink: container in the holding hand (right unless holdInOffHand), the free hand pops the cap, then
                // hold it at the mouth: StepDrink runs the use-time down while held. Capped bottles recap to stop early.
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
                        onOpened = () => DetachCapToFreeHand(), // cap popped -> rides the free hand
                    };
                }
                // Bag (croutons): held in the right hand, left opens, a shake pours crackers into the left hand, eaten
                // from there. The shake/eat don't advance the animator, so the take pushes it into STATE_USE (EnterUseState).
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
                // Reach-into-bag (iskra/MRE): held right, left rips the top, then the left hand reaches into the bag
                // (StepReachTake: the grab fires onTake, the pull-out flips to Holding) and eats from there. The reach
                // scrub parks the arms in STATE_USE, so the finish never sees STATE_OPEN.
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
                // Pack (galette): mirror of CanHand: held right, left opens, left takes a piece and eats it.
                // Deterministic grab/eat pulses (see StartGesturePulse).
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
                // Canned (tushonka spoon + sprats hand): can in the left hand, right rolls the lid, right takes the
                // food (onto the spoon, or grabbed directly) and eats it.
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

        // Open: opening hand at the held item's open zone + that hand's trigger -> the open state plays to
        // openReadyTime, then freezes and runs onOpened. With physicalPullToOpen the hand latches on and the pull
        // scrubs the clip up to pullEndTime; the playingOpen wait branch below also serves the pull's settle
        // (pullEnd -> openReadyTime auto-plays after the unlatch).
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

        // The open is done (pull settled / clip reached openReadyTime): go Ready, or first play the one-shot
        // openPlayState for real. Some rigs put the visible open in a state the pull can't scrub: noodles' bag rip
        // is in the early frames of its first eat state (2073176132), not STATE_OPEN. The chew loop must then run
        // a different eat state or it re-rips.
        private static void FinishOpen(EatStyle s)
        {
            if (openPlayState != 0 && !openAnimPlayed)
            {
                openAnimPlayed = true; // one-shot
                playingOpenAnim = true;
                openAnimDeadline = Time.time + 2f; // give-up fallback (wrong/short state)
                try { controller._player.ArmsAnimatorCommon.Play(openPlayState, 1, 0f); }
                catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] open-anim Play({openPlayState}) failed: {exception.Message}"); }
                controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                PulseHand(s.openHand == Hand.Dominant);
                LogStep($"[ManualEat] Playing open animation ({openPlayState}) to {openPlayMaxTime:F2}...");
                return;
            }
            s.onOpened?.Invoke();
            HideOnOpenRenderers(); // drop the torn cover/lid (noodles) so it doesn't float
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

        // Wait out the one-shot open animation (the rip), then freeze + go Ready. Stops at
        // openPlayMaxTime (so it doesn't run on into the first bite) or when the state ends/
        // changes; openAnimDeadline is the safety net for a wrong hash.
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
            HideOnOpenRenderers(); // the rip just played — now drop the torn cover so it doesn't float
            phase = Phase.Ready;
            LogStep($"[ManualEat] Open animation done — ready (take={s.hasTakeStep}).");
        }

        // Physical open. Squeezing the trigger at the open zone latches the opening hand onto the held item
        // (LatchHandToItem: IK target + wrist pin aim at a latch under the holding hand's rig target). Each grab
        // maps two stages onto the open clip's normalizedTime between the pull start and pullEndTime:
        //   1. Squeeze: the trigger axis scrubs the first triggerOpenPortion (Trigger kind: the whole open,
        //      completed by a full press then a full release).
        //   2. Physical: once fully pressed, def.openGesture drives the rest (Pull = palm travel from the
        //      full-press point, Tilt = tipping the hand off its full-press orientation), measured relative to
        //      the holding hand so walking/turning doesn't scrub; backing off rolls it back.
        // Trigger released = unlatch; progress keeps. The clip still animates the latched hand's fingers (the pin
        // only owns the wrist). Completion goes through CompleteOpenPull (settle or Ready).
        private static void StepOpenPull(EatStyle s)
        {
            Transform acting = HandT(s.openHand);
            Transform anchor = HandT(Other(s.openHand)); // the held item rides this hand
            if (acting == null || anchor == null) return;

            bool dominant = s.openHand == Hand.Dominant;
            float axis = TriggerAxisImpl(dominant);
            // Keep the shared edge state fresh: the drink recap (StepDrink) edges off these prevs via TriggerEdgeImpl,
            // and a stale 0 with the trigger still held from the open would fire a false recap when the phase flips.
            float prevAxis = dominant ? prevTriggerAxis : prevOffTriggerAxis;
            if (dominant) prevTriggerAxis = axis; else prevOffTriggerAxis = axis;

            OpenGestureKind kind = def != null ? def.openGesture : OpenGestureKind.Pull;
            float triggerSpan = kind == OpenGestureKind.Trigger ? 1f : Mathf.Clamp01(triggerOpenPortion);

            if (!pullingOpen)
            {
                // Begin on a squeeze edge through pullLatchAxis at the open zone (edge so a trigger already held
                // while reaching in doesn't auto-grab).
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

            // Trigger kind, fully scrubbed: hold at full and wait for the release to complete. Checked before the
            // unlatch: this release means done, not let-go.
            if (pullAwaitRelease)
            {
                if (axis <= pullReleaseAxis) CompleteOpenPull(s);
                return;
            }

            // Let go: unlatch (the hand goes back to the controller); progress keeps.
            if (axis <= pullReleaseAxis)
            {
                EndPull();
                LogStep($"[ManualEat] Open released at {pullProgress:P0}.");
                return;
            }

            if (!pullSqueezeDone)
            {
                // Squeeze: the trigger axis scrubs the first triggerSpan of the open (both directions, floored where
                // this grab started; Trigger kind: the whole open). At full press the physical gesture takes over;
                // its references are captured here so only motion made while fully gripping counts.
                float a = Mathf.InverseLerp(pullLatchAxis, pullFullPressAxis, axis);
                pullProgress = Mathf.Max(pullGrabProgress, Mathf.Clamp01(a * triggerSpan));
                ScrubOpen(s);
                FirePullMilestoneSounds();
                if (axis >= pullFullPressAxis)
                {
                    if (kind == OpenGestureKind.Trigger) { pullAwaitRelease = true; return; }
                    pullSqueezeDone = true;
                    pullGrabProgress = pullProgress; // the physical phase's floor
                    // Measured relative to the holding hand (palm position in its frame, rotation vs its rotation) so
                    // walking/body turns don't scrub. The palm offset is frozen here: live curl tracking would feed
                    // the clip's own finger animation back into the travel.
                    pullPalmLocal = PalmOffsetLocal(s.openHand);
                    pullGrabLocal = anchor.InverseTransformPoint(acting.TransformPoint(pullPalmLocal));
                    pullGrabRel = Quaternion.Inverse(anchor.rotation) * acting.rotation;
                    PulseHand(dominant); // the "engaged" click
                }
                return;
            }

            // Physical: pull travel / tilt since the full press -> progress.
            float measure = MeasureOpenGesture(kind, acting, anchor);
            float scale = kind == OpenGestureKind.Tilt ? openTiltDegrees : openPullDistance;
            pullProgress = Mathf.Clamp01(pullGrabProgress + Mathf.Max(0f, measure) / Mathf.Max(0.01f, scale));
            ScrubOpen(s);
            FirePullMilestoneSounds();
            if (pullProgress >= 1f) CompleteOpenPull(s);
        }

        // The physical gesture's measure since the full-press capture, anchor-relative (Pull = metres of palm
        // travel, Tilt = degrees). Wrist-roll twist reads too inconsistently for screw caps: they use Tilt.
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

        // A finished open (pull reached 1 / Trigger press-then-release done): unlatch, then settle or go Ready.
        // The pull only covers pullStart -> pullEnd; a clip tail past that (the tushonka spoon grab) auto-plays
        // pullEnd -> openReadyTime with the hand unlocked, and StepOpen's wait branch freezes it there and runs
        // onOpened. With pullEnd at openReadyTime (default) this opens immediately.
        private static void CompleteOpenPull(EatStyle s)
        {
            ReportOpenMovers(); // the lid-bone hint (no-op once reported / with OpenGrip set)
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

        // Where the pull's progress-1 lands: pullEndTime, capped at openReadyTime; unset
        // (-1) = openReadyTime (no settle segment).
        private static float PullEndResolved()
            => pullEndTime > 0f ? Mathf.Min(pullEndTime, openReadyTime) : openReadyTime;

        // Fire the food's openSounds once each (the high-water counter never refires on a re-scrub). The slots sit
        // on the open-state time axis, evenly spaced between the pull start and openReadyTime. A slot past
        // pullEndTime is not fired here: the settle plays the clip's tail for real, so its own sound events cover
        // it (firing it from the pull too doubles the sound, e.g. tushonka's SpoonTake).
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

        // Take: the take gesture (reach the holding hand, or shake the bag) reveals/moves the food onto the eating
        // hand and advances the animator. -> Holding. The reach-into-bag take is a multi-frame latch with its own step.
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

        // Reach-into-bag take (iskra/MRE). Entering the take zone (the bag mouth) latches the reaching hand onto a
        // rail: its position locks to the entry->zone-center line (lateral motion dropped, rotation stays on the
        // controller, DriveReachLatch) while the reach depth scrubs the clip's reach segment. Trigger while deep
        // grabs the cracker into the reaching hand; withdrawing past the entry point completes the take
        // (-> Holding). Backing out empty-handed or yanking sideways off the rail just releases the hand.
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

            // Keep the trigger edge state fresh every latched frame: a stale prev would fire a false grab the moment
            // the depth gate opened under a held trigger.
            bool trigEdge = TriggerEdgeImpl(s.takeHand == Hand.Dominant);

            // Depth + lateral in the anchor frame, against the frozen entry refs: the scrub animates the latched
            // fingers, so a live palm offset would feed the clip back into the measure (same as pullPalmLocal).
            Vector3 palmLocal = anchor.InverseTransformPoint(acting.TransformPoint(reachPalmLocal));
            float depth = Vector3.Dot(palmLocal - reachEntryPalmLocal, reachAxisLocal);
            float lateral = (palmLocal - (reachEntryPalmLocal + reachAxisLocal * depth)).magnitude;

            // Leaving (backwards past the entry point, or sideways off the rail) releases the hand. With the cracker
            // grabbed either exit completes the take.
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

            // Physical depth -> the reach segment (backing out rolls it back). Same speed-0 seek as ScrubOpen;
            // reachStartTime may be greater than reachDeepTime, the lerp runs either way.
            reachProgress = Mathf.Clamp01(depth / Mathf.Max(0.01f, reachDepthDistance));
            try { controller._player.ArmsAnimatorCommon.Play(STATE_USE_HASH, 1, Mathf.Lerp(reachStartTime, reachDeepTime, reachProgress)); }
            catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] reach scrub Play failed: {exception.Message}"); }

            // Trigger while deep = grab: the cracker appears in the reaching hand (its holder hangs off that wrist,
            // which is pinned to the latch). No animator pulse: it would fight the scrub.
            if (!reachGrabbed && trigEdge && reachProgress >= reachGrabDepth)
            {
                reachGrabbed = true;
                if (logGesturePose && s.logsGesturePose)
                    LogStep($"[ManualEat] grab #{biteCount + 1} from {CurStateStr()}");
                s.onTake?.Invoke(); // cracker -> the reaching hand's holder, shown
                PlaySound(def.scoopSound);
                PulseHand(s.takeHand == Hand.Dominant);
                LogStep($"[ManualEat] Grabbed the cracker at {reachProgress:P0} deep — pull it out.");
            }
        }

        // Eat: eating hand (+ eat-zone offset) at the mouth -> hide the eaten piece, count the bite, then finish
        // (last bite) or advance for the next round. Shared by the Holding phase (foods with a take step) and the
        // Ready phase (Handheld: open then bite).
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

            // Resource foods drain per bite: the def's bites divide what was left at spawn (oat flakes 40/40 at
            // bites:4 = 10 a shake), each chunk capped at the live remaining, so a mouth-drunk CanSpoon finishes in
            // fewer scoops. The finish applies only the unapplied remainder (drinkAppliedFrac).
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

            // Finish on the bite count or the live resource hitting empty: a mixed mouth+spoon eat can empty the
            // can before the counter runs out.
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

        // One held-at-the-mouth drink frame, shared by StepDrink (Drink/eatByTime foods) and StepCanDrink (the
        // CanSpoon mouthDrink hybrid): runs the gulp loop + live drain while the item is at the mouth (mouthHand =
        // the hand holding it), pauses (freezes the gulp, cuts the audio) the frame it lowers. Finishes when the
        // live resource empties (the use-time clock when the item has no resource component). Returns true while
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
                    nextGulpFallback = Time.time + GulpFallbackGrace; // animation event gets first shot
                    Pulse();
                    LogStep("[ManualEat] Drinking...");
                }
                drinkHeldTime += Time.deltaTime;
                DriveDrinkLoop();
                DrainDrinkLive();
                // Gulp fallback: no animation-driven gulp arrived in time (events die after a fast lower->re-raise, see
                // nextGulpFallback in State.cs), so play the def's sound manually, shed a snack segment (normally done
                // per allowed animation sound in AllowSound), and sync the throttle so a late animation event can't
                // double-gulp. Only when the name resolves in the player's bank: a missing element plays nothing but
                // would still claim nextDrinkSound and throttle the clip's real events into silence.
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
                bool done = drinkFdc != null && drinkLiveDrain
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
                StopDrinkSound(); // cut the glug — re-raising restarts it from the top
                LogStep($"[ManualEat] Lowered it at {drinkHeldTime:F1}/{drinkUseTime * drinkRemainingFrac:F1}s.");
            }
            return false;
        }

        // CanSpoon hybrid (mouthDrink): hold the can at the mouth and it drinks down the use-time like a Drink.
        // The spoon path stays available from Ready; a scoop after a partial drink is capped at what's left (the
        // per-bite drain in StepEat). The can rides the off hand (eatHand = the spoon hand), so the mouth check
        // anchors there.
        private static bool StepCanDrink(EatStyle s) => DrinkHeldFrame(Other(s.eatHand));

        // Drink: hold the container at the mouth and the resource drains live (DrinkHeldFrame). Lowering it
        // pauses; the free hand back at the cap/open zone + trigger recaps and finishes with just what was drunk
        // (the item keeps the rest).
        private static void StepDrink(EatStyle s)
        {
            if (DrinkHeldFrame(s.eatHand)) return;

            // Recap/done: free hand back at the open zone (the cap area) + trigger.
            if (ZoneReached(s.openHand, Other(s.openHand), openZoneOffset, openDistance)
                && TriggerEdgeImpl(s.openHand == Hand.Dominant))
            {
                ReattachCap();
                Pulse();
                LogStep($"[ManualEat] Recapped after {drinkHeldTime:F1}s.");
                RequestFinish(cancel: false); // fraction comes from the held time; 0 drunk = just put it away
            }
        }

        // Incremental at-mouth drain: apply the not-yet-applied held-time fraction in drinkDrainStep chunks
        // (CutPiece clones and scales without mutating, so chunks sum exactly to the single-shot total), capped at
        // what the bottle had left. The rate is vanilla's: MaxResource over the template UseTime, nutrition
        // proportional, the same model DoMedEffect runs.
        private static void DrainDrinkLive()
        {
            if (!drinkLiveDrain) return;
            // Time-based progress (drinkTimeAppliedFrac) is tracked apart from the total applied (drinkAppliedFrac):
            // a CanSpoon's spoon bites also bump the total, and draining against it would stall the bar until the
            // held time caught up. The time delta is capped at the live remaining, so mixed mouth+spoon can't over-apply.
            float timeTarget = drinkUseTime > 0f ? drinkHeldTime / drinkUseTime : drinkRemainingFrac;
            float live = drinkFdc != null && drinkFdc.MaxResource > 0f
                ? Mathf.Clamp01(drinkFdc.HpPercent / drinkFdc.MaxResource)
                : Mathf.Max(0f, drinkRemainingFrac - drinkAppliedFrac); // no component: budget the unapplied rest
            float delta = Mathf.Min(timeTarget - drinkTimeAppliedFrac, live);
            // Once the held time covers everything left, the final sliver is due even though it's under a full
            // chunk; without this the bar stalls one step short and the empty-finish never fires.
            bool last = live > 0f && timeTarget - drinkTimeAppliedFrac >= live - 0.0001f;
            if (delta >= drinkDrainStep || (last && delta > 0f))
            {
                ApplyConsumedNutrition(delta, quiet: true);
                drinkTimeAppliedFrac += delta;
                drinkAppliedFrac += delta;
            }
            // Chunked float math can leave a hair of resource after the last application: snap it to a true 0 so
            // the finish/discard checks fire.
            if (last && drinkFdc != null && drinkFdc.HpPercent > 0f && drinkFdc.HpPercent <= drinkFdc.MaxResource * 0.01f)
                drinkFdc.HpPercent = 0f;
        }

        // Cut the playing gulp audio. BaseSoundPlayer routes every clip through one pooled BetterSource. Use
        // BetterSource.Stop(), not ReleaseClipsSource: Release hands the source back to BetterAudio's pool mid-play
        // and a fast lower->re-raise leaves the player permanently silent. Stop() keeps the source owned
        // (_isClipSourceReleased stays false), the next gulp's PlayClipInternal reuses it, and
        // BaseSoundPlayer.Update releases it later.
        private static void StopDrinkSound()
        {
            try { if (cutDrinkSoundOnLower) soundPlayer?._clipsSource?.Stop(); }
            catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] StopDrinkSound: {exception.Message}"); }
            TarkovVR.Source.Misc.AudioHaptics.Stop(); // the hand buzz cuts with the audio
        }

        // Keep the gulp/chew animation running while the item is at the mouth. The USE<->EAT transitions are
        // effect-driven and the effect is suppressed, so whenever the animator runs out of the loop segment or
        // strays toward OPEN/END, replay it from the top. The loop state is per-food (eatLoopState; 0 = STATE_USE):
        // sausage's clip uses non-standard hashes.
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

        // Move the arms animator from STATE_OPEN into STATE_USE; the brief playUntil lets Tick run it a few frames
        // so the state engages, then it freezes. FinishSequence must cancel from STATE_USE: cancelling from
        // STATE_OPEN = busy hands + the food won't discard. Used by the Bag (shake) and Pack (take) gestures, whose
        // grabs don't otherwise advance the animator.
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

            Vector3 bagPalm = ProbePoint(Hand.Dominant); // the bag rides the dominant palm
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

        // Begin a per-gesture animation pulse (the finger/hand motion played on each grab/eat). With
        // deterministicGesturePose it first snaps the arms animator to a fixed (state, normalizedTime) so every
        // grab/eat replays the same segment; without it the pulse free-runs from wherever the last bite froze and
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
