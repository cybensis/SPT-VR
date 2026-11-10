using TarkovVR.Source.Settings;
using UnityEngine;
using static EFT.Player;

namespace TarkovVR.Source.Player.Interactions
{
    // Tunables (public statics, live-tunable) and the per-eat runtime state. Per-food values are seeded into
    // the statics each spawn (OnSpawnPre): FoodDef is the source of truth.
    internal static partial class EatingInteractionController
    {
        //--- Tunables (public so they can be A/B'd live in the headset) -------------
        public static bool enableManualEating = VRSettings.GetManualEating();

        // Gesture trigger radii (metres), seeded per food on spawn (= the zone radii). mouthForwardDot gates the eat to in front.
        public static float openDistance = 0.18f;
        public static float scoopDistance = 0.18f;
        public static float eatDistance = 0.18f;
        public static float mouthForwardDot = -0.2f;
        // The eat anchor in head-local space: the camera sits between the eyes, so the mouth is a bit below and in front.
        public static Vector3 mouthLocalOffset = new Vector3(0f, -0.08f, 0.08f);

        // Reach offsets (anchor-local), seeded from the FoodDef each spawn. Zero = hand-to-hand / hand-to-mouth triggers.
        public static Vector3 openZoneOffset = Vector3.zero;
        public static Vector3 takeZoneOffset = Vector3.zero;
        public static Vector3 eatZoneOffset = Vector3.zero;

        // true = probe/anchor the zones at the rendered hand's palm (fingertip-leaf centroid, recomputed per frame so it rides
        // the finger curl, see PalmOffsetLocal). false = the rig transform's origin, which is the wrist (the controller pose
        // pushed back controllerLength, ~10cm off the grip): rotating the hand sweeps that point through an arc.
        public static bool palmCollisionPoints = true;
        // Wrist-local stand-in palm offset, used only if the hand skeleton can't be resolved (the centroid needs ikManager's bone3).
        public static Vector3 palmFallbackLocal = new Vector3(0f, 0f, 0.08f);

        // Bag shake-to-pour: with the bag within shakeNearDistance of the left hand, shakeReversalsNeeded velocity reversals
        // (> shakeMinSpeed) within shakeWindow seconds pour the crackers. Head-relative, so walking doesn't count.
        public static float shakeNearDistance = 0.60f;
        public static float shakeMinSpeed = 0.2f;     // m/s, head-relative
        public static int shakeReversalsNeeded = 1;
        public static float shakeWindow = 1.2f;

        // Reach-into-bag (ReachBag): entering the take zone latches the reaching hand onto a rail, the line from where the
        // palm entered toward the zone center (the bag mouth), captured in the holding hand's frame so walking/turning can't
        // scrub it. Depth along the rail drives the reach segment.
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
        // The STATE_USE reach segment. Re-seeded from FoodDef.reachStartTime/reachDeepTime each spawn, so values set
        // between eats are lost: tune them mid-eat, then DumpFoodDef writes them into the def.
        public static float reachStartTime = 0.02f;
        public static float reachDeepTime = 0.3f;

        public static bool eatingHaptics = true;
        // Every audible eating clip also streams its waveform envelope to the controllers (Source/Misc/AudioHaptics).
        // Master scale: AudioHaptics.strength.
        public static bool audioHaptics = true;
        // Drive the body-follow ourselves each eat frame: mid-eat nothing else does (the IKManager/VRPlayerManager gates
        // skip it, the gun's HandsPositioner is disabled), and the rig/IK desync shows as walking jitter. Keep true.
        public static bool driveBodyFollowDuringEat = true;
        // A/B: false (default) parents props to the IK wrist bone (bone3, pinned to the rig target by driveHandsToTargets:
        // no walk lag, no prop/hand mismatch); true parents them to the rig targets.
        public static bool debugParentToTarget = false;
        // Pin bone3 to the rig target after the IK solve so the wrist tracks 1:1. Required for the prop-on-bone3 setup above.
        public static bool driveHandsToTargets = true;
        // Pull-to-open: holding trigger at the open zone latches the opening hand onto the held item (LatchHandToItem), and
        // the hand's travel relative to the holding hand scrubs the open. Release = unlatch, progress stays. Every archetype;
        // the drink recap stays a plain trigger press. false = press once and the open plays.
        public static bool physicalPullToOpen = true;
        // Metres of hand travel (in the holding hand's frame) for a full open pull.
        public static float openPullDistance = 0.28f;
        // The trigger axis scrubs the start of every open grab: pullLatchAxis..pullFullPressAxis drives the first
        // triggerOpenPortion of the open (both directions, floored at the grab). The physical gesture (pull/tilt) engages
        // only at full press. 0 = no squeeze portion (full press still required).
        public static float triggerOpenPortion = 0.2f;
        // Trigger axis thresholds: latch = the squeeze level that grabs (edge-gated); full press = the physical gesture
        // engages (or a Trigger-kind open is fully scrubbed); release = unlatch, and a fully pressed Trigger-kind open completes.
        public static float pullLatchAxis = 0.15f;
        public static float pullFullPressAxis = 0.95f;
        public static float pullReleaseAxis = 0.12f;
        // Degrees the hand tips off its full-press orientation (any direction) for a full open; the squeeze consumes
        // triggerOpenPortion first. A wrist-twist gesture is a dead end: wrist roll measures too inconsistently.
        public static float openTiltDegrees = 70f;
        // Open-state normalizedTime the pull starts from (progress 0); progress 1 maps to pullEndTime. -1 = auto (where the
        // animator froze after the draw, ≈0). The lid scrub, the baked HandPath and the milestone sounds share this time axis.
        // Seeded per food from FoodDef.pullStartTime (PullStart(...)); DumpFoodDef round-trips it.
        public static float pullStartTime = -1f;
        // Open-state normalizedTime the pull ends at (progress 1) = where the hand unlocks. -1 = openReadyTime (the pull covers
        // the whole open). Set it earlier when the clip's tail isn't pull motion: the rest auto-plays to openReadyTime.
        // Seeded from FoodDef.pullEndTime (PullEnd(...)); DumpFoodDef round-trips it.
        public static float pullEndTime = -1f;
        // When a pull-open completes without an OpenGrip bone, log the bones under the held item ranked by how far they
        // moved: the winner is the bone name for OpenGrip(...). Placing the hand from the clip's wrist bones is a dead end
        // (raw reads see last frame's post-pin values, forced Animator.Update(0) reads come back in the wrong frame).
        public static bool logOpenMovers = false;
        public static bool logSteps = false;              // each eat's step-by-step progress ("[ManualEat] Opening / Took / Ate ..."); warnings always
        internal static void LogStep(string message) { if (logSteps) Plugin.MyLog.LogInfo(message); }
        // OpenGrip snap: at the grab the hand jumps onto the lid/cover bone, index fingertip on the bone (deepest *index* bone
        // under the wrist; else the fingertip-leaf centroid, then the palm), re-applied every frame so it follows the finger
        // curl. pullSnapPos = optional nudge in the bone's local frame. false = the latch keeps the grab-moment offset to the bone.
        public static bool pullSnapToGrip = true;
        public static Vector3 pullSnapPos = Vector3.zero;
        // Keep the latched hand's rotation on the controller while its position is locked / riding the lid (the freeze-at-grab
        // latch too). false = the rotation rides the bone (captured at the grab).
        public static bool pullRotationFree = true;
        // HandPath foods only: true = rotation stays on the controller, the baked path gives just the position.
        // false = the authored rotation replays too.
        public static bool pathRotationFree = false;
        // Degrees of controller twist the latched hand may deviate from its anchored rotation (authored path / ridden bone /
        // grab pose), measured from the controller's grab-moment orientation. 0 = rigid.
        public static float pullWiggleDeg = 2f;
        // The reload's hand latch (VRReloadController.LatchHand / ApplyLatchWiggle) for pull-open and reach-in: the hand eases onto
        // the authored pose at the grab and back to the controller at the release, and wiggles about the point it holds by the
        // controller's turn since the grab (measured against the holding hand), capped at latchWiggleDegrees. Tilt opens keep
        // pullWiggleDeg about the wrist: their gesture is the hand's turn. false = the latch jumps on and snaps off.
        public static bool reloadStyleLatch = true;
        public static float latchWiggleDegrees = 15f;   // = VRReloadController.handWiggleDegrees
        public static float latchSnapTime = 0.06f;      // s onto the authored pose (= handSnapTime)
        public static float latchReleaseTime = 0.08f;   // s back onto the controller (= handReleaseTime)

        //--- Runtime ----------------------------------------------------------------
        // Closed: can shut. Ready: lid open, utensil/hand empty — ready to take food.
        // Holding: food on the spoon / in the hand — ready to bring to the mouth.
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
        private static Transform medsBody;                // the meds controller object (pinned to the ribcage); rig body anchor
        private static Renderer spoonR, foodR, foodR2, capR;
        private static Renderer[] segR;                   // segmentedBites: the held item's renderers, name-sorted
        private static BaseSoundPlayer soundPlayer;
        // Holders sit between hand and prop: the animator still writes the prop's local (we zero
        // it in LateUpdate) but never the holder, so the holder is the clean, tunable hold offset.
        private static GameObject baseHolder, spoonHolder, foodHolder, foodHolder2, capHolder;

        // Handheld (chocolate): wrapperT glued onto the bar (canT) at its rest offset (carries the
        // chocolate); coverT is the wrapper mesh that DETACHES to a left-hand holder once peeled.
        private static Transform wrapperT, coverT;
        private static Vector3 wrapperLocalPos; private static Quaternion wrapperLocalRot;
        private static Transform coverParent0; private static Vector3 coverPos0; private static Quaternion coverRot0;
        private static bool coverDetached;            // wrapper (cover) moved from the bar to the left hand
        private static bool capDetached;              // drink cap moved from the can to the left hand
        private static Transform leftHandBoneRef;     // resolved left IK hand bone (for the late wrapper/cracker holder)

        // Bag (croutons): bag (canT) in the right hand; on a SHAKE the prefix-matched crackerT[]
        // move to a left-hand holder in their captured clump layout (crackerLocal*, vs anchor
        // crackerT[0]) and are eaten left-handed. Hidden until shaken.
        private static Transform[] crackerT;
        private static Vector3[] crackerLocalPos; private static Quaternion[] crackerLocalRot;
        private static Transform[] crackerParent0; private static Vector3[] crackerPos0; private static Quaternion[] crackerRot0;
        private static Renderer[] crackerR;
        private static GameObject crackerHolder;
        private static bool crackersShown;
        // Shake detection (head-relative so locomotion/turning don't false-trigger): count
        // velocity direction-reversals above a speed while the bag is near the left hand.
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
        private static bool playingManualSound; // true while WE call OnSound (so it's not suppressed)
        private static bool playingOpen;        // playing the STATE_OPEN lid-roll segment
        private static bool playingOpenAnim;    // playing the one-shot open animation (openPlayState — noodles' rip)
        private static bool openAnimPlayed;     // one-shot guard (it fires once per eat)
        private static float openAnimDeadline;  // give-up fallback if the play state never advances
        private static Renderer[] hideOnOpenR;  // renderers hidden at open completion (noodles' torn cover); re-shown on restore
        private static bool hiddenOnOpen;       // one-shot guard

        // Pull-to-open runtime. pullProgress persists across grabs; pullAnimStart = the normalizedTime progress 0 maps to
        // (captured at the first grab). handLatched: a latch transform under the holding hand's rig target, and the latched
        // arm's IK target and bone3 pin both aim at it. The IK stays on (with IK off the clip doesn't hold the arm). The acting
        // rig target keeps tracking the controller, so unlatching returns the hand to the real hand.
        private static bool pullingOpen;            // trigger held, hand latched to the item
        private static float pullProgress;          // 0..1 open progress (persists across grabs)
        private static float pullGrabProgress;      // progress when the current grab started
        private static Vector3 pullGrabLocal;       // acting PALM probe in the anchor's frame at FULL PRESS
        private static Vector3 pullPalmLocal;       // acting palm offset FROZEN at full press — the scrub
                                                    // animates the latched fingers, so live curl tracking
                                                    // would feed the clip back into the measured travel
        private static bool pullSqueezeDone;        // trigger fully pressed -> the physical phase is live
        private static bool pullAwaitRelease;       // Trigger-kind only: fully scrubbed, completes on full release
        private static Quaternion pullGrabRel = Quaternion.identity; // acting-in-anchor rotation at full press (the tilt reference)
        private static float pullAnimStart = -1f;   // normalizedTime at progress 0 (-1 = uncaptured)
        private static int pullSoundsFired;         // openSounds milestones fired (monotonic)
        private static bool handLatched;            // one hand is glued to the held item right now
        private static bool latchedHandIsDominant;  // which hand (Dominant = rightArmIk)
        private static GameObject pullLatch;        // the glue point (child of the holding hand's rig target)
        private static Quaternion latchGrabActRot = Quaternion.identity;   // acting target rotation at grab (the wiggle reference)
        private static Quaternion latchGrabLocalRot = Quaternion.identity; // latch local rotation at grab (the freeze-mode anchor)
        // reloadStyleLatch: where the rendered hand was at the grab (the ease-in start, holding-target frame), the controller's turn
        // then (the wiggle reference, same frame), and the ease back to the controller after a release (handLatched stays on meanwhile,
        // so the IK + pin keep following the latch)
        private static float latchGrabTime;
        private static Vector3 latchStartWorldPosition;
        private static Quaternion latchStartWorldRotation = Quaternion.identity;
        private static Vector3 latchFrozenLocalPosition;    // the freeze-at-grab latch's spot, re-written each frame under the ease/wiggle
        private static Vector3 latchStartLocalPosition;
        private static Quaternion latchStartLocalRotation = Quaternion.identity;
        private static Quaternion latchControllerAtGrabInHolder = Quaternion.identity;
        private static bool latchReleasing;
        private static float latchReleaseStartTime;
        // OpenGrip prop-ride: the latch keeps a fixed offset to the clip-animated lid/cover
        // bone (captured at the grab), so the hand moves WITH the visible lid as the pull
        // scrubs it. No openGripT (or no name) = the latch just stays where you grabbed.
        private static Transform openGripT;         // resolved FoodDef.openGripName bone
        private static bool latchRidesProp;         // this grab captured a prop offset
        private static Vector3 propGrabLocalPos;    // latch pose in the open prop's frame at grab
        private static Quaternion propGrabLocalRot = Quaternion.identity;
        // Fingertip anchoring (pullSnapToGrip): what actually lands ON the OpenGrip bone.
        // Resolved from the acting hand's skeleton at the grab; the offset is recomputed
        // each frame so it follows the finger-curl animation.
        private static Transform actingWristT;      // the latched hand's wrist bone (bone3)
        private static Transform[] fingerAnchors;   // index bone (preferred) or fingertip leaves
        private static bool fingerAnchorResolved;
        // Open-movers hint (no openGripName): bones under the held item + their item-local
        // positions at the first grab, ranked & logged when a pull completes (logOpenMovers).
        private static Transform[] moverBones;
        private static Vector3[] moverStartLocal;

        // Reach-into-bag runtime (ReachBag take, see StepReachTake). Reuses the latch plumbing (pullLatch/handLatched, the IK +
        // bone3 pin re-aim) but drives the latch along a rail: entry point + axis captured at zone entry in the holding hand's
        // frame. The palm offset is frozen at entry, for the same reason as pullPalmLocal.
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

        // Per-gesture pose determinism (seeded from the FoodDef on spawn), see FoodDef.deterministicGesture + StartGesturePulse.
        // *State = the replayed segment, *Time = where in it the pulse starts.
        public static bool deterministicGesturePose = false;
        public static int takePoseState = STATE_USE_HASH;
        public static float takePoseTime = 0f;
        public static int bitePoseState = STATE_EAT_HASH;
        public static float bitePoseTime = 0f;
        // Log each take/bite pulse's starting arms state + normalizedTime (to find the segment for takePoseTime/bitePoseTime).
        public static bool logGesturePose = false;
        // STATE_OPEN normalizedTime of the finished-open ready pose, where the open clip freezes (a non-pull open plays to
        // here; a pull settles to here after pullEndTime). Seeded from the FoodDef on spawn.
        public static float openReadyTime = 0.92f;
        // Last bite: auto-fire the trigger/fire cancel (fast put-away + weapon draw).
        // Off = wait out the operation's use-time.
        public static bool cancelToFinish = true;
        // Last bite: jump the animator straight to STATE_END instead of playing out STATE_EAT.
        public static bool skipLastBiteAnim = true;
        // Where in STATE_END the put-away starts (skips the can-hold bite at the start). Seeded per food.
        public static float putAwayStartTime = 0.3f;
        // Each scoop/eat gesture plays the animation forward this long (seconds), then freezes. 0 = bites stay frozen.
        public static float bitePlayTime = 0.5f;
        private static float playUntil;        // animation plays (speed>0) while Time.time < this

        // Both-trigger cancel + the queued finish. The finish must never run from STATE_OPEN (busy hands, the food won't
        // discard, see FinishSequence): a finish requested there first kicks the arms into STATE_USE and completes a few
        // frames later (finishDeadline = give-up fallback).
        private static bool prevBothTriggers;
        private static bool finishPending;
        private static bool finishIsCancel;
        private static float finishDeadline;

        // Drink snapshot. drinkUseTime = seconds at the mouth to drink the whole item (vanilla UseTime unless the FoodDef
        // overrides); drinkRemainingFrac = HpPercent/MaxResource at spawn; drinkHeldTime accumulates while held at the mouth.
        private static float drinkUseTime;
        private static float drinkRemainingFrac = 1f;
        private static float drinkHeldTime;
        private static bool drinkingHeld;      // at the mouth right now (gulp loop playing)
        // Live drain: while held at the mouth the consumed fraction is applied in drinkDrainStep chunks of the whole item
        // (energy/hydration + HpPercent + bar refresh), and the auto-finish fires when the live resource hits empty.
        // drinkAppliedFrac = what's already applied this eat; the finish applies only the remainder.
        public static bool drinkLiveDrain = true;
        public static float drinkDrainStep = 0.01f; // 1% chunks — smooth, near vanilla's per-tick cadence
        private static float drinkAppliedFrac;
        private static float drinkTimeAppliedFrac; // the part of drinkAppliedFrac that came from at-mouth TIME
                                                   // (spoon bites bump only the total — see DrainDrinkLive)
        private static EFT.InventoryLogic.FoodDrinkComponent drinkFdc; // cached at spawn (the live resource)
        // The gulp sound is an animation event, and the drink loop restarts STATE_USE faster than the clip's audio length:
        // gate it to one play per drinkGulpInterval seconds. The clip has trailing silence, so this sits a bit under its length.
        public static float drinkGulpInterval = 1.2f;
        private static float nextDrinkSound;
        // Gulp-audio fallback clock: the gulp is normally the clip's own Drink/Eat sound event (let through AllowSound on the
        // nextDrinkSound throttle), but after a fast lower -> re-raise the animation events can stop arriving. When a gulp is
        // overdue by GulpFallbackGrace past the throttle, DrinkHeldFrame plays the def's eatSound itself (PlaySound).
        private static float nextGulpFallback;
        private const float GulpFallbackGrace = 0.35f;
        // Mouth-zone hysteresis while drinking: the exit boundary is this multiple of the entry radius. Without it a steady
        // hold flickers across the boundary, which pauses the drink clock and chops the gulp cadence. 1 = exact radius.
        public static float drinkZoneExitScale = 1.35f;
        // Cut the glug when the bottle leaves the mouth, with BetterSource.Stop() on the player's own clip source. Not
        // ReleaseClipsSource: returning the pooled source to BetterAudio mid-play can leave the sound player silent after a
        // fast lower -> raise. false = the gulp clip finishes on its own (the vanilla auto-release cleans it up).
        public static bool cutDrinkSoundOnLower = true;
        // Timed-eat loop state, seeded from FoodDef.eatLoopState each spawn (0 = STATE_USE). Live-tunable: sausage's
        // non-standard chew hash is a guess.
        public static int eatLoopState;
        private static float nextLoopPlay; // DriveDrinkLoop replay throttle (wrong-hash safety)
        // One-shot open animation: an arms state played for real (not scrubbed) when the open completes, for visuals a scrub
        // can't show: noodles' bag rip is in the early frames of its first eat state (2073176132), not STATE_OPEN. Seeded from
        // FoodDef.openPlayState/openPlayMaxTime (max time = stop before the first bite). 0 = none. The chew loop must be a
        // different state or eating re-rips.
        public static int openPlayState;
        public static float openPlayMaxTime = 1f;
        // Segmented bites (sausage): hide from the end of the name-sorted renderer list (the tip) first.
        public static bool segmentHideFromEnd = true;
        // The cap's rest offset on the container (captured before any reparenting), so it can sit glued there until opened
        // and be glued back on a recap.
        private static Vector3 capLocalPos; private static Quaternion capLocalRot;
        private static Transform capHandBoneRef; // the free hand's IK bone (cap goes here on open)
    }
}
