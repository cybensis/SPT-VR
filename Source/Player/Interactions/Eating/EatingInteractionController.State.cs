using UnityEngine;
using static EFT.Player;

namespace TarkovVR.Source.Player.Interactions
{
    // Tunables (public statics, changeable at runtime) and the per-eat runtime state. Per-food values are copied
    // from the FoodDef into these statics on each spawn (OnSpawnPre), so the FoodDef is the source of truth.
    internal static partial class EatingInteractionController
    {
        //--- Tunables (public so they can be A/B'd live in the headset) -------------

        // Gesture trigger radii (m) = the food's zone radii, set per food on spawn. mouthForwardDot keeps the eat zone in front of the face.
        public static float openDistance = 0.18f;
        public static float scoopDistance = 0.18f;
        public static float eatDistance = 0.18f;
        public static float mouthForwardDot = -0.2f;
        // The eat anchor in head-local space: the camera sits between the eyes, so the mouth is a bit below and in front.
        public static Vector3 mouthLocalOffset = new Vector3(0f, -0.08f, 0.08f);

        // Zone offsets in the anchor's local frame, set from the FoodDef each spawn. Zero = measured straight to the anchor palm or the mouth.
        public static Vector3 openZoneOffset = Vector3.zero;
        public static Vector3 takeZoneOffset = Vector3.zero;
        public static Vector3 eatZoneOffset = Vector3.zero;

        // Zones are probed at the rendered hand's palm (PalmOffsetLocal), not at the rig transform's origin. That origin is the
        // wrist (the controller pose pushed back controllerLength, ~10 cm off the grip) and sweeps through an arc as the hand turns.
        // Wrist-local stand-in palm offset, used only if the hand skeleton can't be resolved (the centroid needs ikManager's bone3).
        public static Vector3 palmFallbackLocal = new Vector3(0f, 0f, 0.08f);

        // Bag shake-to-pour: with the bag within shakeNearDistance of the left hand, shakeReversalsNeeded velocity reversals
        // (> shakeMinSpeed) within shakeWindow seconds pour the crackers. Measured relative to the head, so walking doesn't count.
        public static float shakeNearDistance = 0.60f;
        public static float shakeMinSpeed = 0.2f;     // m/s, head-relative
        public static int shakeReversalsNeeded = 1;
        public static float shakeWindow = 1.2f;

        // Reach-into-bag (ReachBag): entering the take zone latches the reaching hand onto a rail, the line from where the
        // palm entered toward the zone center (the bag mouth). It is captured in the holding hand's frame, so walking or
        // turning doesn't move the hand along it. The depth along the rail drives the reach segment.
        public static float reachDepthDistance = 0.18f; // metres of reach for full depth
        // Fraction of the full reach where the trigger can grab the cracker.
        public static float reachGrabDepth = 0.5f;
        // Withdrawing past the entry point by this many metres releases the rail (with the cracker grabbed it completes the take).
        public static float reachExitDepth = 0.03f;
        // Moving this far sideways off the rail releases it too.
        public static float reachLateralEscape = 0.15f;
        // ReachPath foods only: true = the baked path drives just the position, rotation stays on the controller.
        // false = the authored wrist roll replays too.
        public static bool reachRotationFree = false;
        // The STATE_USE normalizedTime range of the reach. Reset from FoodDef.reachStartTime/reachDeepTime on each spawn, so
        // values changed between eats are lost: tune them during an eat, then DumpFoodDef prints them for the def.
        public static float reachStartTime = 0.02f;
        public static float reachDeepTime = 0.3f;

        // Pull-to-open: holding the trigger at the open zone latches the opening hand onto the held item (LatchHandToItem), and
        // the hand's travel relative to the holding hand scrubs the open. Releasing unlatches and keeps the progress. Used by
        // every archetype; the drink recap stays a plain trigger press.
        // Metres of hand travel (in the holding hand's frame) for a full open pull.
        public static float openPullDistance = 0.28f;
        // At the start of each open grab the trigger axis drives the open: pullLatchAxis..pullFullPressAxis covers the first
        // triggerOpenPortion, in both directions, never below where the grab started. The physical gesture (pull or tilt) takes
        // over only at full press. 0 = no squeeze portion (full press still required).
        public static float triggerOpenPortion = 0.2f;
        // Trigger axis thresholds. Latch: crossing it upward grabs. Full press: the physical gesture takes over (a Trigger-kind
        // open is then fully scrubbed). Release: below it the hand unlatches, and a fully pressed Trigger-kind open completes.
        public static float pullLatchAxis = 0.15f;
        public static float pullFullPressAxis = 0.95f;
        public static float pullReleaseAxis = 0.12f;
        // Degrees the hand tilts from its full-press orientation, in any direction, for a full open (after the squeeze's
        // triggerOpenPortion). Don't use a wrist-twist gesture instead: wrist roll measures too inconsistently.
        public static float openTiltDegrees = 70f;
        // Open-state normalizedTime at pull progress 0; progress 1 maps to pullEndTime. -1 = automatic (where the animator
        // froze after the draw, about 0). The lid scrub, the baked HandPath and the milestone sounds all use this time axis.
        // Set per food from FoodDef.pullStartTime (PullStart(...)); DumpFoodDef prints it back.
        public static float pullStartTime = -1f;
        // Open-state normalizedTime at pull progress 1, where the hand unlocks. -1 = openReadyTime (the pull covers the whole
        // open). Set it earlier when the end of the clip isn't pull motion; the rest then plays on its own up to openReadyTime.
        // Set from FoodDef.pullEndTime (PullEnd(...)); DumpFoodDef prints it back.
        public static float pullEndTime = -1f;
        // When a pull-open completes without an OpenGrip bone, log the bones under the held item ranked by how far they
        // moved: the top one is the bone name to pass to OpenGrip(...). Don't place the hand from the clip's wrist bones:
        // raw reads return last frame's pinned values, and reads after a forced Animator.Update(0) come back in the wrong frame.
        public static bool logOpenMovers = false;
        public static bool logSteps = false;              // each eat's step-by-step progress ("[ManualEat] Opening / Took / Ate ..."); warnings always
        internal static void LogStep(string message) { if (logSteps) Plugin.MyLog.LogInfo(message); }
        // OpenGrip snap: at the grab the hand jumps so its index fingertip sits on the lid or cover bone (the deepest index bone
        // under the wrist; else the fingertip-leaf centroid, then the palm). Re-applied every frame so it follows the finger
        // curl. pullSnapPos is an optional nudge in the bone's local frame. false = the latch keeps its offset to the bone from the grab.
        public static bool pullSnapToGrip = true;
        public static Vector3 pullSnapPos = Vector3.zero;
        // Keep the latched hand's rotation on the controller while its position is locked or follows the lid (also for the
        // freeze-at-grab latch). false = the rotation follows the bone, with the offset captured at the grab.
        public static bool pullRotationFree = true;
        // HandPath foods only: true = rotation stays on the controller, the baked path gives just the position.
        // false = the authored rotation replays too.
        public static bool pathRotationFree = false;
        // Degrees the latched hand may turn away from its anchored rotation (baked path, followed bone or grab pose) as the
        // controller twists from its orientation at the grab. 0 = rigid.
        public static float pullWiggleDeg = 2f;
        // Use the reload's hand latch (VRReloadController.LatchHand / ApplyLatchWiggle) for pull-to-open and reach-in. The hand
        // eases onto the authored pose at the grab and back to the controller at the release, and turns about the point it holds
        // as the controller turns since the grab (relative to the holding hand), capped at latchWiggleDegrees. Tilt opens keep
        // pullWiggleDeg about the wrist, because their gesture is the hand's turn. false = the latch jumps on and snaps off.
        public static bool reloadStyleLatch = true;
        public static float latchWiggleDegrees = 15f;   // deg at most, one to one with the controller's turn
        public static float latchSnapTime = 0.06f;      // s onto the authored pose (= handSnapTime)
        public static float latchReleaseTime = 0.08f;   // s back onto the controller (= handReleaseTime)

        //--- Runtime ----------------------------------------------------------------
        // Closed: the item is still shut. Ready: opened, spoon or hand empty, ready to take food.
        // Holding: food on the spoon or in the hand, ready to bring to the mouth.
        private enum Phase { Closed, Ready, Holding, Done }

        private static bool active;
        private static bool manualDone;     // handed back to vanilla for the finish
        private static MedsController controller;
        private static FoodDef def;
        private static EatStyle style;      // per-archetype descriptor for the gesture loop
        private static Phase phase;
        private static int biteCount;
        private static float spawnAnimSpeed = 1f;

        private static Transform baseT, spoonT, foodT, capT;
        private static Transform foodT2;                  // optional second taken piece (sugar's 2nd cube)
        private static Transform medsBody;                // the meds controller object (kept on the ribcage), the rig's body anchor
        private static Renderer spoonR, foodR, foodR2, capR;
        private static Renderer[] segR;                   // segmentedBites: the held item's renderers, name-sorted
        private static BaseSoundPlayer soundPlayer;
        // Holders sit between the hand and the prop. The animator still writes the prop's local pose (zeroed in
        // LateUpdate) but never the holder's, so the holder carries the hold offset and can be tuned.
        private static GameObject baseHolder, spoonHolder, foodHolder, foodHolder2, capHolder;

        // Handheld (chocolate): wrapperT (which carries the chocolate) is attached to the bar (baseT) at its rest
        // offset; coverT is the wrapper mesh that moves to a left-hand holder once peeled.
        private static Transform wrapperT, coverT;
        private static Vector3 wrapperLocalPos; private static Quaternion wrapperLocalRot;
        private static Transform coverParent0; private static Vector3 coverPos0; private static Quaternion coverRot0;
        private static bool coverDetached;            // wrapper (cover) moved from the bar to the left hand
        private static bool capDetached;              // drink cap moved from the can to the left hand
        private static Transform leftHandBoneRef;     // resolved left IK hand bone (for the late wrapper/cracker holder)

        // Bag (croutons): the bag (baseT) is in the right hand. A shake moves the crackers matched by name prefix
        // (crackerT[]) to a left-hand holder, keeping their captured layout relative to crackerT[0] (crackerLocal*),
        // and they are eaten from the left hand. Hidden until shaken.
        private static Transform[] crackerT;
        private static Vector3[] crackerLocalPos; private static Quaternion[] crackerLocalRot;
        private static Transform[] crackerParent0; private static Vector3[] crackerPos0; private static Quaternion[] crackerRot0;
        private static Renderer[] crackerR;
        private static GameObject crackerHolder;
        private static bool crackersShown;
        // Shake detection, relative to the head so walking and turning don't trigger it: counts
        // velocity reversals above a speed while the bag is near the left hand.
        private static Vector3 shakePrevPos, shakePrevVel;
        private static int shakeReversals;
        private static float shakeWindowEnd;

        // Saved so we can put the props back before the controller object is pooled.
        private static Transform baseParent0, spoonParent0, foodParent0, wrapperParent0, capParent0;
        private static Vector3 basePos0, spoonPos0, foodPos0, wrapperPos0, capPos0;
        private static Quaternion baseRot0, spoonRot0, foodRot0, wrapperRot0, capRot0;
        private static Transform food2Parent0; private static Vector3 food2Pos0; private static Quaternion food2Rot0;
        private static bool reparented;

        private static bool effectFired;
        private static MedsController.MedsInHandsOperation pendingOp;
        private static float prevTriggerAxis;
        private static float prevOffTriggerAxis; // off-hand trigger edge (handheld wrapper-open)
        private static bool playingManualSound; // true while we call OnSound ourselves, so it isn't muted
        private static bool playingOpen;        // playing the STATE_OPEN lid-roll segment
        private static bool playingOpenAnim;    // playing the one-shot open animation (openPlayState, e.g. noodles' rip)
        private static bool openAnimPlayed;     // one-shot guard (it plays once per eat)
        private static float openAnimDeadline;  // give up if the play state never advances
        private static Renderer[] hideOnOpenR;  // renderers hidden when the open completes (noodles' torn cover); shown again on restore
        private static bool hiddenOnOpen;       // one-shot guard

        // Pull-to-open runtime. pullProgress persists across grabs; pullAnimStart is the normalizedTime at progress 0, captured
        // at the first grab. While handLatched, the latched arm's IK target and bone3 pin both aim at a latch transform under
        // the holding hand's rig target. The IK stays on: with the IK off, the clip doesn't hold the arm. The acting hand's rig
        // target keeps tracking the controller, so unlatching returns the hand to the controller.
        private static bool pullingOpen;            // trigger held, hand latched to the item
        private static float pullProgress;          // 0..1 open progress (persists across grabs)
        private static float pullGrabProgress;      // progress when the current grab started
        private static Vector3 pullGrabLocal;       // acting palm probe in the anchor's frame at full press
        private static Vector3 pullPalmLocal;       // acting palm offset frozen at full press: the scrub
                                                    // animates the latched fingers, so tracking the live curl
                                                    // would feed the clip back into the measured travel
        private static bool pullSqueezeDone;        // trigger fully pressed: the physical gesture is active
        private static bool pullAwaitRelease;       // Trigger-kind only: fully scrubbed, completes on full release
        private static Quaternion pullGrabRel = Quaternion.identity; // acting-in-anchor rotation at full press (the tilt reference)
        private static float pullAnimStart = -1f;   // normalizedTime at progress 0 (-1 = uncaptured)
        private static int pullSoundsFired;         // openSounds milestones already played (only increases)
        private static bool handLatched;            // one hand is latched to the held item right now
        private static bool latchedHandIsDominant;  // which hand (Dominant = rightArmIk)
        private static GameObject pullLatch;        // the latch point (child of the holding hand's rig target)
        private static Quaternion latchGrabActRot = Quaternion.identity;   // acting target rotation at grab (the wiggle reference)
        private static Quaternion latchGrabLocalRot = Quaternion.identity; // latch local rotation at grab (the freeze-mode anchor)
        // reloadStyleLatch state: where the rendered hand was at the grab (the ease-in start, in the holding target's frame), the
        // controller's rotation then (the wiggle reference, same frame), and the ease back to the controller after a release
        // (handLatched stays set meanwhile, so the IK and pin keep following the latch)
        private static float latchGrabTime;
        private static Vector3 latchStartWorldPosition;
        private static Quaternion latchStartWorldRotation = Quaternion.identity;
        private static Vector3 latchFrozenLocalPosition;    // the freeze-at-grab latch's position, rewritten each frame under the ease and wiggle
        private static Vector3 latchStartLocalPosition;
        private static Quaternion latchStartLocalRotation = Quaternion.identity;
        private static Quaternion latchControllerAtGrabInHolder = Quaternion.identity;
        private static bool latchReleasing;
        private static float latchReleaseStartTime;
        // OpenGrip: the latch keeps a fixed offset (captured at the grab) to the clip-animated lid or
        // cover bone, so the hand moves with the visible lid as the pull scrubs it. Without openGripT
        // the latch stays where you grabbed.
        private static Transform openGripT;         // resolved FoodDef.openGripName bone
        private static bool latchRidesProp;         // this grab captured a prop offset
        private static Vector3 propGrabLocalPos;    // latch pose in the open prop's frame at grab
        private static Quaternion propGrabLocalRot = Quaternion.identity;
        // Fingertip anchoring (pullSnapToGrip): the part of the hand that is placed on the OpenGrip bone.
        // Resolved from the acting hand's skeleton at the grab; the offset is recomputed each frame
        // so it follows the finger curl animation.
        private static Transform actingWristT;      // the latched hand's wrist bone (bone3)
        private static Transform[] fingerAnchors;   // index bone (preferred) or fingertip leaves
        private static bool fingerAnchorResolved;
        // For logOpenMovers (food with no openGripName): the bones under the held item and their item-local
        // positions at the first grab, ranked and logged when a pull completes.
        private static Transform[] moverBones;
        private static Vector3[] moverStartLocal;

        // Reach-into-bag runtime (the ReachBag take, see StepReachTake). Reuses the latch (pullLatch/handLatched, with the IK and
        // bone3 pin aimed at it) but moves it along a rail: the entry point and axis are captured at zone entry, in the holding
        // hand's frame. The palm offset is frozen at entry for the same reason as pullPalmLocal.
        private static bool reachingIn;             // off hand latched on the bag-mouth rail
        private static bool reachGrabbed;           // cracker grabbed this reach (pull it out)
        private static float reachProgress;         // 0..1 depth along the rail
        private static Vector3 reachEntryPalmLocal; // reaching palm in the anchor frame at entry
        private static Vector3 reachAxisLocal;      // rail direction (anchor-local), entry -> zone center
        private static Vector3 reachPalmLocal;      // reaching palm offset frozen at entry

        // Arms layer-1 states, shared across foods.
        private const int STATE_OPEN_HASH = 492683391;  // draw -> roll lid -> [grab spoon]
        private const int STATE_USE_HASH = -735675743;  // scoop / take (the grab motion)
        private const int STATE_EAT_HASH = 719885042;   // bite
        private const int STATE_END_HASH = -1014941517; // put-away

        // Fixed gesture poses (set from the FoodDef on spawn), see FoodDef.deterministicGesture and StartGesturePulse.
        // *State = the replayed segment, *Time = where in it the pulse starts.
        public static bool deterministicGesturePose = false;
        public static int takePoseState = STATE_USE_HASH;
        public static float takePoseTime = 0f;
        public static int bitePoseState = STATE_EAT_HASH;
        public static float bitePoseTime = 0f;
        // Log each take/bite pulse's starting arms state and normalizedTime (to find the values for takePoseTime/bitePoseTime).
        public static bool logGesturePose = false;
        // STATE_OPEN normalizedTime of the finished-open ready pose, where the open clip freezes (an open without a pull plays
        // to here; a pull's settle plays from pullEndTime to here). Set from the FoodDef on spawn.
        public static float openReadyTime = 0.92f;
        // On the last bite the finish fires the trigger cancel (fast put-away and weapon draw) and jumps the arms animator
        // straight to STATE_END instead of playing out STATE_EAT. putAwayStartTime is where in STATE_END the put-away starts
        // (skipping the can-hold beat at its start). Set per food.
        public static float putAwayStartTime = 0.3f;
        // Each scoop/eat gesture plays the animation forward this long (seconds), then freezes. 0 = bites stay frozen.
        public static float bitePlayTime = 0.5f;
        private static float playUntil;        // animation plays (speed > 0) while Time.time < this

        // Both-trigger cancel and the queued finish. The finish must never run from STATE_OPEN (busy hands and the food won't
        // discard, see FinishSequence). A finish requested there first pushes the arms into STATE_USE and completes a few
        // frames later (finishDeadline = give-up fallback).
        private static bool prevBothTriggers;
        private static bool finishPending;
        private static bool finishIsCancel;
        private static float finishDeadline;

        // Drink state captured at spawn. drinkUseTime = seconds at the mouth to drink the whole item (vanilla UseTime unless the
        // FoodDef overrides it); drinkRemainingFrac = HpPercent/MaxResource at spawn; drinkHeldTime adds up while held at the mouth.
        private static float drinkUseTime;
        private static float drinkRemainingFrac = 1f;
        private static float drinkHeldTime;
        private static bool drinkingHeld;      // at the mouth right now (gulp loop playing)
        // Live drain: while held at the mouth, the consumed fraction is applied in drinkDrainStep chunks of the whole item
        // (energy/hydration, HpPercent and the bar refresh), and the auto-finish fires when the live resource is empty.
        // drinkAppliedFrac = what is already applied this eat; the finish applies only the remainder.
        public static float drinkDrainStep = 0.01f; // 1% chunks: smooth, close to vanilla's per-tick rate
        private static float drinkAppliedFrac;
        private static float drinkTimeAppliedFrac; // the part of drinkAppliedFrac that came from time at the mouth
                                                   // (spoon bites only add to the total, see DrainDrinkLive)
        private static EFT.InventoryLogic.FoodDrinkComponent drinkFdc; // cached at spawn (the live resource)
        // The gulp sound is an animation event, and the drink loop restarts STATE_USE faster than the clip's audio length,
        // so it is limited to one play per drinkGulpInterval seconds. The clip ends in silence, so this is a bit under its length.
        public static float drinkGulpInterval = 1.2f;
        private static float nextDrinkSound;
        // Gulp fallback timer. The gulp is normally the clip's own Drink/Eat sound event (let through by AllowSound on the
        // nextDrinkSound throttle), but after a fast lower and raise the animation events can stop arriving. When a gulp is
        // GulpFallbackGrace overdue past the throttle, DrinkHeldFrame plays the def's eatSound itself (PlaySound).
        private static float nextGulpFallback;
        private const float GulpFallbackGrace = 0.35f;
        // Mouth-zone hysteresis while drinking: the exit boundary is this multiple of the entry radius. Without it a steady
        // hold flickers across the boundary, which pauses the drink timer and breaks up the gulps. 1 = exact radius.
        public static float drinkZoneExitScale = 1.35f;
        // Timed-eat loop state, set from FoodDef.eatLoopState each spawn (0 = STATE_USE). Sausage's non-standard chew hash
        // is a guess, so this can be changed at runtime.
        public static int eatLoopState;
        private static float nextLoopPlay; // DriveDrinkLoop replay throttle, so a wrong hash can't call Play every frame
        // One-shot open animation: an arms state played for real (not scrubbed) when the open completes, for visuals a scrub
        // can't show. Noodles' bag rip is in the early frames of its first eat state (2073176132), not in STATE_OPEN. Set from
        // FoodDef.openPlayState/openPlayMaxTime (the max time stops it before the first bite). 0 = none. The chew loop must use
        // a different state, or every bite rips the bag again.
        public static int openPlayState;
        public static float openPlayMaxTime = 1f;
        // Segmented bites (sausage): hide from the end of the name-sorted renderer list (the tip) first.
        public static bool segmentHideFromEnd = true;
        // The cap's rest offset on the container (captured before any reparenting), so it can stay attached there until
        // opened and be put back on a recap.
        private static Vector3 capLocalPos; private static Quaternion capLocalRot;
        private static Transform capHandBoneRef; // the free hand's IK bone (cap goes here on open)
    }
}
