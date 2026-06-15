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
    // Tunables (public statics so they can be A/B'd live in the headset) and the
    // per-eat runtime state. Per-food values are SEEDED into the live statics each
    // spawn (OnSpawnPre), so FoodDef stays the source of truth and the statics stay tunable.
    internal static partial class EatingInteractionController
    {
        //--- Tunables (public so they can be A/B'd live in the headset) -------------
        public static bool enableManualEating = VRSettings.GetManualEating();

        // Gesture trigger radii (metres); generous since the prop sits offset from the controller.
        // Seeded per-food on spawn (= the zone radii). mouthForwardDot gates the eat to in-front.
        public static float openDistance = 0.18f;
        public static float scoopDistance = 0.18f;
        public static float eatDistance = 0.18f;
        public static float mouthForwardDot = -0.2f;
        // The eat anchor in HEAD-local space: the camera sits between the EYES, so anchor the
        // zone a bit below + in front (the mouth) instead of eye-center — the old zero anchor
        // is why "bring food to the mouth" felt too high. (0,0,0) = the old behavior; A/B live.
        public static Vector3 mouthLocalOffset = new Vector3(0f, -0.08f, 0.05f);

        // Reach offsets (anchor-local), seeded from the FoodDef each spawn; public for live A/B.
        // Zero = the original hand-to-hand / hand-to-mouth triggers. Radii are the statics above.
        public static Vector3 openZoneOffset = Vector3.zero;
        public static Vector3 takeZoneOffset = Vector3.zero;
        public static Vector3 eatZoneOffset = Vector3.zero;

        // Palm-anchored collision points. The hand rig transforms' ORIGIN is the WRIST (the
        // controller pose pushed back controllerLength (~17.5cm) along its forward), a good
        // 10cm from where the hand actually grips — probing/anchoring the zones there made
        // physically ROTATING the hand sweep the collision point through an arc (it visibly
        // slid off the hand), and the visible fingers crossed the debug spheres well before
        // the origin did (the zones felt smaller than drawn). true = probe/anchor at the
        // rendered hand's PALM instead (fingertip-leaf centroid of the hand skeleton,
        // recomputed per frame so it rides the finger curl — see PalmOffsetLocal); false =
        // the old origin behavior.
        public static bool palmCollisionPoints = true;
        // Rough wrist-local stand-in palm offset, used only if a hand skeleton can't be
        // resolved (the centroid needs ikManager's bone3 — normally always there mid-eat).
        public static Vector3 palmFallbackLocal = new Vector3(0f, 0f, 0.08f);

        // Bag shake-to-pour: the bag must be within shakeNearDistance of the left hand, and you
        // wiggle it — shakeReversalsNeeded head-relative velocity reversals (> shakeMinSpeed)
        // within shakeWindow seconds pour the crackers (head-relative so walking doesn't count).
        public static float shakeNearDistance = 0.60f;
        public static float shakeMinSpeed = 0.2f;     // m/s, head-relative
        public static int shakeReversalsNeeded = 1;
        public static float shakeWindow = 1.2f;

        // Reach-into-bag (ReachBag): entering the take zone latches the reaching hand onto
        // a RAIL — the line from where the palm entered toward the zone center (the bag
        // mouth), captured at entry in the HOLDING hand's frame so walking/turning can't
        // scrub it. Physical depth along the rail drives the reach segment.
        public static float reachDepthDistance = 0.18f; // metres of reach for FULL depth
        // Fraction of the full reach where the trigger can grab the cracker.
        public static float reachGrabDepth = 0.5f;
        // Withdrawing past the entry point by this many metres releases the rail (the
        // "leave the collider" exit; with the cracker grabbed it completes the take).
        public static float reachExitDepth = 0.03f;
        // Yanking this far SIDEWAYS off the rail releases it too.
        public static float reachLateralEscape = 0.15f;
        // ReachPath foods only: true = rotation stays live on the controller and the baked
        // path drives just the POSITION (reaching into a bag, the wrist is yours — the
        // rail's behavior); false = the authored wrist roll replays too. A/B live.
        public static bool reachRotationFree = false;
        // The STATE_USE reach segment, SEEDED per food from FoodDef.reachStartTime/
        // reachDeepTime each spawn — values set here or between eats are WIPED by the
        // re-seed (the "adjusting them did nothing" trap); tune them LIVE MID-EAT, then
        // DumpFoodDef round-trips them into the def so they stick.
        public static float reachStartTime = 0.02f;
        public static float reachDeepTime = 0.3f;

        public static bool eatingHaptics = true;
        // Audio -> haptics: every audible eating clip also streams its waveform envelope
        // to BOTH controllers (Source/Misc/AudioHaptics) — you feel the lid roll, each
        // gulp, the holster. Master scale lives on AudioHaptics.strength.
        public static bool audioHaptics = true;
        // Drive the body-follow ourselves each eat frame. Mid-eat nothing else does (IKManager/
        // VRPlayerManager gates skip it, the gun's HandsPositioner is disabled) -> rig/IK desync =
        // walking jitter. (A weapon switch left an orphan caller, hence "switching fixed it.") Keep true.
        public static bool driveBodyFollowDuringEat = true;
        // A/B: parent props to the IK wrist bone (bone3) vs the rig target. bone3 (pinned to the
        // smooth target by driveHandsToTargets) tracks to ~2-3mm with no walk lag and removes the
        // last prop/hand mismatch — so false is the good default; true routes to the targets.
        public static bool debugParentToTarget = false;
        // Pin bone3 to the rig target after the IK solve so the wrist tracks 1:1 (no lag).
        // Required for the prop-on-bone3 setup above.
        public static bool driveHandsToTargets = true;
        // Physical pull-to-open: holding trigger at the open zone LATCHES the opening hand onto
        // the held item (LatchHandToItem — the rendered hand glues to the holding controller's
        // 6dof), and your physical pull (travel relative to the holding hand) SCRUBS the
        // lid-roll/peel/cap-pop forward. Release = unlatch, the lid stays where you left it;
        // grab again to continue. Shared by every archetype (the open step is one path); the
        // drink RECAP stays a plain trigger press. false = the old press-once-and-play open.
        public static bool physicalPullToOpen = true;
        // Metres of hand travel (in the holding hand's frame) for a FULL open pull.
        public static float openPullDistance = 0.28f;
        // The trigger AXIS scrubs the START of every open grab: squeezing from pullLatchAxis
        // up to pullFullPressAxis drives the first triggerOpenPortion of the open (both
        // directions, floored at the grab), and the PHYSICAL gesture (pull/tilt) only
        // engages once the trigger is FULLY pressed — so an open = squeeze, then move.
        // 0 = no squeeze portion (full press still required before the physical part).
        public static float triggerOpenPortion = 0.2f;
        // Trigger axis thresholds: latch = the squeeze level that grabs (edge-gated),
        // full press = the physical gesture engages (or a Trigger-kind open is fully
        // scrubbed), release = unlatch — and a fully-pressed Trigger-kind open COMPLETES
        // here (the press-then-fully-depress cycle).
        public static float pullLatchAxis = 0.15f;
        public static float pullFullPressAxis = 0.95f;
        public static float pullReleaseAxis = 0.12f;
        // Tilt scale for a FULL open: degrees the hand tips off its full-press orientation,
        // any direction (the squeeze consumes triggerOpenPortion first, so the physical part
        // needs the remaining fraction of this). 90 headset-tuned 2026-06-12. (A signed CCW
        // wrist-TWIST scale lived here briefly for screw caps — removed: wrist roll measured
        // too inconsistently in the headset and the tilt reads the same.)
        public static float openTiltDegrees = 70f;
        // Open-state normalizedTime the pull STARTS from: progress 0 maps here, progress 1
        // maps to pullEndTime. -1 = auto (wherever the animator froze after the draw, ≈0).
        // Raise it to skip the clip's early draw/reach beat — the lid scrub, the baked
        // HandPath and the milestone sounds all share this time axis. SEEDED per food from
        // FoodDef.pullStartTime (PullStart(...)) each spawn, like openReadyTime; still
        // A/B-able live, and DumpFoodDef round-trips the tuned value.
        public static float pullStartTime = -1f;
        // Open-state normalizedTime the pull ENDS at = where the hand UNLOCKS (progress 1
        // maps here). -1 = openReadyTime, i.e. the pull covers the whole open (the old
        // behavior). Set it EARLIER when the clip's tail isn't pull motion — e.g. the
        // tushonka spoon grab: after the unlatch the remaining clip AUTO-PLAYS from here to
        // openReadyTime (hand back on the controller, fingers still animating) instead of
        // being scrubbed by your pull. Seeded from FoodDef.pullEndTime (PullEnd(...));
        // A/B live; DumpFoodDef round-trips it.
        public static float pullEndTime = -1f;
        // When a pull-open completes WITHOUT an OpenGrip bone, log the bones under the held
        // item ranked by how far they moved across the pull — the winner is the lid/cover
        // bone name to paste into OpenGrip(...). (Reading the CLIP's wrist bones to place
        // the hand was tried twice and is a DEAD END: raw reads see last frame's post-pin
        // values, and forced Animator.Update(0) reads come back in the wrong frame — the
        // hand flew off in the wrong direction. The OpenGrip prop-ride can't misalign: the
        // bone it follows is the same one you SEE moving on the item.)
        public static bool logOpenMovers = false;
        // OpenGrip snap: at the grab, JUMP the hand onto the lid/cover bone instead of
        // keeping the offset from where you pressed trigger. The INDEX FINGERTIP is what
        // lands on the bone — resolved from the hand skeleton at the grab (deepest bone
        // named *index* under the wrist; falls back to the fingertip-leaf centroid, then
        // the palm) and re-applied every frame, so it tracks the finger-curl animation and
        // the hand pivots around the fingertip while you twist. No manual offsets needed;
        // pullSnapPos is an OPTIONAL extra nudge in the bone's local frame (default zero).
        // false = the latch keeps your grab-moment offset to the bone.
        public static bool pullSnapToGrip = true;
        public static Vector3 pullSnapPos = Vector3.zero;
        // Keep the latched hand's ROTATION on your controller (full 3dof twist) while its
        // POSITION is locked in place / riding the lid. Applies to the freeze-at-grab latch
        // too. false = the rotation rides the bone (captured at the grab) like before.
        public static bool pullRotationFree = true;
        // HandPath foods only: true = keep the rotation on the controller and take just the
        // POSITION from the baked path (the authored rotation — the wrist rolling the key —
        // is the point of the path, so false is the default). A/B live.
        public static bool pathRotationFree = false;
        // Degrees of controller twist the latched hand may WIGGLE off its anchored rotation
        // (the authored path / the ridden bone / the grab pose). Measured as the controller's
        // deviation from its grab-moment orientation, clamped — so the hand feels alive in
        // your grip without breaking the authored look. 0 = rigid lock (the old behavior);
        // for fully free rotation use pathRotationFree / pullRotationFree instead.
        public static float pullWiggleDeg = 2f;

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
        private static MedsController.ObservedMedsControllerClass pendingOp;
        private static float prevTriggerAxis;
        private static float prevOffTriggerAxis; // off-hand trigger edge (handheld wrapper-open)
        private static bool playingManualSound; // true while WE call OnSound (so it's not suppressed)
        private static bool playingOpen;        // playing the STATE_OPEN lid-roll segment
        private static bool playingOpenAnim;    // playing the one-shot open animation (openPlayState — noodles' rip)
        private static bool openAnimPlayed;     // one-shot guard (it fires once per eat)
        private static float openAnimDeadline;  // give-up fallback if the play state never advances
        private static Renderer[] hideOnOpenR;  // renderers hidden at open completion (noodles' torn cover); re-shown on restore
        private static bool hiddenOnOpen;       // one-shot guard

        // Pull-to-open runtime. pullProgress persists across grabs (a half-rolled lid stays
        // half-rolled until you grab it again); pullAnimStart = the normalizedTime progress 0
        // maps to (captured at the first grab — wherever the animator froze after the draw).
        // handLatched glues the rendered acting hand to the HELD ITEM at its grab pose: a
        // latch transform parented to the holding hand's rig target, and the latched arm's IK
        // target AND bone3 pin both aim at it. The IK stays ON — an earlier IK-off "let the
        // clip drive the arm" version didn't hold (the hand kept controller-tracking), and
        // item-glue is the better feel anyway: the hand rides the holding controller's full
        // 6dof while your physical pull drives the scrub invisibly. The acting RIG target
        // keeps tracking the controller throughout, so unlatching snaps the hand straight
        // back to your real hand.
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

        // Reach-into-bag runtime (ReachBag take — see StepReachTake). Reuses the latch
        // plumbing (pullLatch/handLatched, the IK + bone3 pin re-aim) but drives the latch
        // along a RAIL instead of the open scrub: entry point + axis captured at zone entry
        // in the HOLDING hand's frame; the palm offset is FROZEN at entry (the scrub
        // animates the latched fingers — live curl tracking would feed the clip back into
        // the depth measure, the pullPalmLocal lesson).
        private static bool reachingIn;             // off hand latched on the bag-mouth rail
        private static bool reachGrabbed;           // cracker grabbed this reach (pull it out)
        private static float reachProgress;         // 0..1 depth along the rail
        private static Vector3 reachEntryPalmLocal; // reaching palm in the anchor frame at entry
        private static Vector3 reachAxisLocal;      // rail direction (anchor-local), entry -> zone center
        private static Vector3 reachPalmLocal;      // reaching palm offset frozen at entry

        // Arms layer-1 states, shared across foods (confirmed in recon).
        private const int STATE_OPEN_HASH = 492683391;  // draw -> roll lid -> [grab spoon]
        private const int STATE_USE_HASH = -735675743;  // scoop / take (the grab motion)
        private const int STATE_EAT_HASH = 719885042;   // bite
        private const int STATE_END_HASH = -1014941517; // put-away

        // Per-gesture pose determinism (loaded from the FoodDef on spawn; public for live A/B).
        // See FoodDef.deterministicGesture + StartGesturePulse. *State = the replayed segment,
        // *Time = where in it the pulse starts.
        public static bool deterministicGesturePose = false;
        public static int takePoseState = STATE_USE_HASH;
        public static float takePoseTime = 0f;
        public static int bitePoseState = STATE_EAT_HASH;
        public static float bitePoseTime = 0f;
        // Log each take/bite pulse's starting arms state+normalizedTime (to find the exact
        // "perfect" segment to bake into takePoseTime/bitePoseTime).
        public static bool logGesturePose = false;
        // STATE_OPEN normalizedTime of the finished-open READY pose — where the open clip
        // FREEZES (a non-pull open plays to here; a pull settles to here after pullEndTime).
        // Set from the current FoodDef on spawn; public so it can still be A/B'd live.
        public static float openReadyTime = 0.92f;
        // Last bite: auto-fire the trigger/fire cancel (fast put-away + weapon draw).
        // Off = wait out the operation's use-time.
        public static bool cancelToFinish = true;
        // Last bite: jump the animator straight to STATE_END instead of playing out STATE_EAT.
        public static bool skipLastBiteAnim = true;
        // Where in STATE_END the put-away starts (skips the can-hold bite at the start). Seeded per-food, A/B live.
        public static float putAwayStartTime = 0.3f;
        // Each scoop/eat gesture plays the animation forward this long (finger/hand
        // motion) then freezes. Set 0 to keep bites fully frozen.
        public static float bitePlayTime = 0.5f;
        private static float playUntil;        // animation plays (speed>0) while Time.time < this

        // Both-trigger cancel + the queued finish. The finish must NEVER run from STATE_OPEN
        // (busy hands + the food won't discard — see FinishSequence), so a finish requested
        // while the arms are still in the draw/open state first kicks them into STATE_USE
        // and completes a few frames later (finishDeadline = give-up fallback).
        private static bool prevBothTriggers;
        private static bool finishPending;
        private static bool finishIsCancel;
        private static float finishDeadline;

        // Drink: resource/timing snapshot. drinkUseTime = seconds at the mouth to drink the
        // WHOLE item (vanilla UseTime unless the FoodDef overrides); drinkRemainingFrac =
        // HpPercent/MaxResource at spawn (a half-drunk bottle only has half left to drink);
        // drinkHeldTime accumulates while the container is held at the mouth.
        private static float drinkUseTime;
        private static float drinkRemainingFrac = 1f;
        private static float drinkHeldTime;
        private static bool drinkingHeld;      // at the mouth right now (gulp loop playing)
        // LIVE drain: while held at the mouth the consumed fraction is applied INCREMENTALLY
        // (energy/hydration + HpPercent + bar refresh, in drinkDrainStep chunks of the whole
        // item) instead of once at the finish — the resource bar visibly ticks down as you
        // drink, and the auto-finish triggers off the LIVE resource hitting empty (health-
        // based), not the use-time math alone. drinkAppliedFrac = what's already applied
        // this eat; the finish only applies the remainder, so nothing doubles.
        public static bool drinkLiveDrain = true;
        public static float drinkDrainStep = 0.01f; // 1% chunks — smooth, near vanilla's per-tick cadence
        private static float drinkAppliedFrac;
        private static float drinkTimeAppliedFrac; // the part of drinkAppliedFrac that came from at-mouth TIME
                                                   // (spoon bites bump only the total — see DrainDrinkLive)
        private static EFT.InventoryLogic.FoodDrinkComponent drinkFdc; // cached at spawn (the live resource)
        // The gulp sound is an animation event, but the drink loop restarts STATE_USE faster
        // than the clip's audio length, so without throttling it retriggers mid-playback (the
        // "repeats too quickly" symptom). Gate it to one play per drinkGulpInterval seconds —
        // the CLIP runs longer than its audible part (trailing silence), so this sits a bit
        // under the clip length or the next gulp feels late (1.6 did — reported).
        public static float drinkGulpInterval = 1.2f;
        private static float nextDrinkSound;
        // Gulp-audio FALLBACK clock: the gulp is normally the clip's own Drink/Eat sound
        // event (allowed through AllowSound on the nextDrinkSound throttle), but after a
        // fast lower->re-raise the animation events were observed to STOP arriving entirely
        // (reported twice, 2026-06: permanent drink silence; the source-side cut was ruled
        // out — Release() vs Stop() made no difference). So whenever a gulp is overdue by a
        // grace past the throttle, DrinkHeldFrame plays the def's own eatSound manually
        // (PlaySound — the same proven path the per-bite sounds use). When animation events
        // ARE flowing they keep beating the fallback, so vanilla timing is unchanged.
        private static float nextGulpFallback;
        private const float GulpFallbackGrace = 0.35f;
        // Mouth-zone HYSTERESIS while drinking: once the drink is held at the mouth, the
        // exit boundary is this multiple of the entry radius. Without it a steady hold
        // flickers across the boundary (superwater log 2026-06-12: ~20 Lowered/Drinking
        // cycles in 3s of holding), which pauses the drink clock — "held it forever, never
        // finished" — and chops the gulp cadence. 1 = old exact-radius behavior.
        public static float drinkZoneExitScale = 1.35f;
        // Cut the glug the moment the bottle leaves the mouth. The cut is BetterSource.Stop()
        // on the player's own clip source — NOT ReleaseClipsSource: releasing returned the
        // pooled source to BetterAudio mid-play, and a fast lower→raise wedged the sound
        // player into permanent silence (reported). false = let the gulp clip finish on its
        // own when lowering (the vanilla auto-release then cleans it up).
        public static bool cutDrinkSoundOnLower = true;
        // Timed-eat loop state, seeded from FoodDef.eatLoopState each spawn (0 = STATE_USE).
        // Live-tunable: sausage's non-standard chew hash is a recon GUESS — paste another
        // hash here in UnityExplorer if its loop doesn't play/sound.
        public static int eatLoopState;
        private static float nextLoopPlay; // DriveDrinkLoop replay throttle (wrong-hash safety)
        // One-shot open animation: an arms state PLAYED FOR REAL (not scrubbed) when the open
        // completes, for open visuals a scrub can't show — noodles' bag-RIP lives in the early
        // frames of its first eat state (2073176132), not STATE_OPEN. Seeded per-food from
        // FoodDef.openPlayState/openPlayMaxTime each spawn; A/B live (max time = stop before the
        // first bite). 0 = none. The chew loop must be a DIFFERENT state or eating re-rips.
        public static int openPlayState;
        public static float openPlayMaxTime = 1f;
        // Segmented bites (sausage): hide from the END of the name-sorted renderer list
        // (the tip) first; flip live if it visibly eats from the wrong end.
        public static bool segmentHideFromEnd = true;
        // The cap's rest offset ON the container (captured before any reparenting) so it can
        // sit glued there until opened and be glued back on a recap.
        private static Vector3 capLocalPos; private static Quaternion capLocalRot;
        private static Transform capHandBoneRef; // the free hand's IK bone (cap goes here on open)
    }
}
