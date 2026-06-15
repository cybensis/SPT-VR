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
    // The food data model and registry: FoodDef + the archetype factories, shape
    // presets and chain wrappers that build it, the Defs table (one line per food),
    // and the authoring round-trip (DumpFoodDef prints a paste-ready tuned line).
    internal static partial class EatingInteractionController
    {
        //--- Per-item definitions ---------------------------------------------------
        // Everything that differs between foods. The finish (nutrition/cancel/discard) is
        // food-agnostic; this is the gesture/prop half. FoodKind picks the gesture machine.
        private enum FoodKind { CannedFood, Handheld, Bag, Pack, Drink, ReachBag }

        // ===== Gesture archetype descriptor =====================================
        // What the shared open->take->eat loop needs that differs per archetype. Built per food
        // (BuildStyle), so the control flow is written ONCE; a new TYPE is mostly one BuildStyle
        // case + its prop-setup/late-zero tails. The hooks call the existing per-archetype routines.
        private enum Hand { Dominant, Off }        // Dominant = the right hand always; Off = left
        private enum TakeKind { None, HandNear, Shake, ReachIn }

        // How the PHYSICAL part of the open is performed, after the trigger-squeeze start
        // (see triggerOpenPortion — the squeeze scrubs the first chunk on every kind):
        //   Pull    — hand travel away from the full-press point (lids, wrappers, tears)
        //   Tilt    — tip the hand off its full-press orientation (screw caps, flip-tops,
        //             can tabs — a CCW-twist kind for screw caps was tried 2026-06-12 and
        //             REMOVED: measuring wrist roll was too inconsistent in the headset,
        //             and tilting reads the same)
        //   Trigger — NO physical part: the trigger axis scrubs the WHOLE open; completes on
        //             a full press THEN a full release (currently unmapped — see DriveLatch's
        //             freeze-at-grab note)
        private enum OpenGestureKind { Pull, Tilt, Trigger }

        private sealed class EatStyle
        {
            public string label;          // for logs ("can"/"wrapper"/"bag"/"pack")
            public Hand openHand;         // hand that performs the open gesture
            public Hand eatHand;          // hand that goes to the mouth
            public Hand takeHand;         // hand that performs a HandNear take (unused for Shake/None)
            public bool hasTakeStep;      // false = open then eat directly (Handheld)
            public bool timedSip;         // Drink: HOLD at the mouth runs a timer (no bite counting)
            public TakeKind takeKind;     // how the take is triggered
            public int openStateHash;     // arms state the open plays (STATE_OPEN, or STATE_USE)
            public bool logsGesturePose;  // log the take/eat pulse's source state (cans + pack)
            public Action onOpened;       // reveal/detach once the open completes
            public Action onTake;         // reveal/move the food + advance the animator on take
            public Action onEatHide;      // hide the eaten renderer(s) at the start of a bite
            public Action onAfterBite;    // advance the animator after a non-final bite
        }

        private sealed class FoodDef
        {
            public string templateId;
            public FoodKind kind = FoodKind.CannedFood;
            public string rootName;       // the held prop (can for cans; the bar for handheld)
            public string spoonName;      // utensil; null/empty = grabbed by hand (no spoon)
            public string foodPieceName;  // the bite that appears on the utensil/hand (cans)
            public string capName;            // the drink cap if it has one
            // Clip-animated bone the latched hand RIDES during a pull-open (the can's lid /
            // pull-key bone, the wrapper cover...). Set via OpenGrip(def, "bone"). null =
            // the latch freezes where you grabbed. See logOpenMovers to find the name.
            public string openGripName;
            // Baked open-hand path: the opening PALM's pose relative to the held item root,
            // sampled off the VANILLA open animation by the recon ([HANDPATH] — eat the food
            // once with enableManualEating=false). Packed stride 8 per key: t, px,py,pz,
            // qx,qy,qz,qw (t = normalizedTime in the open state, keys ascending). When set
            // it drives the latch (position+rotation) INSTEAD of the OpenGrip ride — the
            // hand replays the authored roll, re-anchored to the item in your hand. Set via
            // HandPath(def, ...floats).
            public float[] openHandPath;
            // Drink: the cap group stays WELDED to the bottle (a hinged flip-top, e.g.
            // moonshine's "hinge") instead of detaching to the free hand — glued at its rest
            // offset like a Pack cover, the child bones still animate the flip open in place.
            public bool capHinged;
            public bool holdInOffHand;        // Drink: container in the LEFT hand (cap hand flips too)
            public float drinkSeconds;        // Drink: at-mouth seconds for a FULL item; 0 = the item's vanilla UseTime
            // Handheld/Pack: wrapperName = a group glued to the held item (carries sub-pieces like
            // the chocolate); coverName = the wrapper mesh that DETACHES to the off hand once
            // peeled. Either can be null.
            public string wrapperName;
            public string coverName;
            public int bites = 3;
            // Skip the open phase entirely: spawn straight into Ready so you just bring the
            // held item to your mouth (sausage — held in one hand, the other does nothing).
            public bool skipOpen;

            // Sound event names (BaseSoundPlayer.OnSound); null = none. draw/open fire from the
            // STATE_OPEN clip itself; scoop/eat we play per-gesture (animation is frozen between).
            public string drawSound;
            public string[] openSounds;
            public string scoopSound;     // played when the food is taken (scoop / grab)
            public string eatSound;       // played on each bite

            // Grip = hold pose LOCAL TO THE PALM BONE (measured; see EatingRecon [GRIP]). Live-tune
            // on the holder GameObjects in Unity Explorer.
            public Vector3 basePos;        // base in the off (left) hand
            public Vector3 baseRot;
            public Vector3 spoonPos;      // utensil in the main (right) hand (HasSpoon only)
            public Vector3 spoonRot;
            public Vector3 foodPos;       // food piece: vs the spoon holder (HasSpoon) or
            public Vector3 foodRot;       //   the right hand (no spoon)
            public Vector3 capPos;        // drink cap if it has one
            public Vector3 capRot;

            // Arms layer-1 normalizedTime knobs (see EatingRecon [SOUND]/[STATE]). The open
            // timeline reads left to right:
            //   pullStartTime --(your physical pull scrubs)--> pullEndTime --(hand unlocks,
            //   clip auto-plays)--> openReadyTime (freeze: the finished-open READY pose).
            public float openReadyTime = 0.92f; // STATE_OPEN freeze point = the ready pose (spoon in hand / hand poised to take)
            public float putAwayStartTime = 0.3f; // where in STATE_END the put-away starts (skips the can-hold beat)
            // Where the PULL starts in the open state (progress 0 maps here; -1 = wherever
            // the animator froze, ≈0). Raise so trigger-press puts the hand straight at the
            // lid (skips the clip's draw/reach beat). Set via PullStart(def, t).
            public float pullStartTime = -1f;
            // Where the PULL ends = the hand UNLOCKS (progress 1 maps here; -1 = openReadyTime,
            // the pull covers the whole open). Set earlier when the clip's tail isn't pull
            // motion (the tushonka spoon grab): after the unlatch the rest auto-plays to
            // openReadyTime. Set via PullEnd(def, t).
            public float pullEndTime = -1f;
            // The physical open gesture (Pull/Tilt/Trigger — see OpenGestureKind). Set via
            // OpenWith(def, kind); the drink presets bake the common ones (every capped or
            // hinged Bottle, JuiceBottle, SodaCan = Tilt; capless bottles stay Pull).
            public OpenGestureKind openGesture = OpenGestureKind.Pull;
            // Authored STATE_OPEN normalizedTime of each openSound event (parallel to
            // openSounds; null = evenly spaced estimates). From the recon [SOUND] log. With
            // real times the first sound can land INSIDE the trigger-squeeze range (the even
            // spacing never reaches it), and times past pullEndTime are left to the settle's
            // native clip events. Set via SoundTimes(def, t...).
            public float[] openSoundTimes;

            // deterministicGesture: each take/bite SNAPS the animator to a fixed (state, time) so
            // every round replays the SAME segment — else the timeline drifts and each bite looks
            // different (the sprats symptom). take=STATE_USE, bite=STATE_EAT.
            public bool deterministicGesture = false;
            public float takePoseTime = 0f;   // STATE_USE normalizedTime each take/grab pulse replays from
            public float bitePoseTime = 0f;   // STATE_EAT normalizedTime each bite pulse replays from

            // ReachBag: the STATE_USE segment the physical reach DEPTH scrubs — depth 0 maps
            // to reachStartTime (hand at the bag mouth), full depth to reachDeepTime (hand
            // all the way in, bag mouth widest). Start may be > deep (the lerp runs either
            // way). The first guess (0.25 -> 0.02, from "Take@0.014 = the clip starts with
            // the hand in the bag") was INVERTED in the headset: entry snapped the hand
            // INSIDE the bag and pushing in pulled it out. The early Take is just the
            // food-appear EVENT firing while the hand is still outside — the authored hand
            // dives IN over ~0 -> 0.3 (the ReachPath deep cluster sits at t≈0.23-0.34).
            // Headset-corrected 2026-06: 0.02 -> 0.3. NOTE: the live statics RE-SEED from
            // here every spawn — tune them MID-EAT (between-eat edits are wiped).
            public float reachStartTime = 0.02f;
            public float reachDeepTime = 0.3f;
            // Baked reach-hand path (ReachBag): the reaching PALM's pose vs the held item
            // root, sampled off the VANILLA clip's STATE_USE reach segment (the recon's
            // [HANDPATH] capture emits a paste-ready ReachPath(...) line for ReachBag
            // defs). Same stride-8 packing as openHandPath, but t = STATE_USE
            // normalizedTime — the same axis the reach scrub seeks on. When set,
            // DriveReachLatch replays it at the scrub time INSTEAD of the synthetic
            // straight rail, so the hand dives into the bag along the authored curve and
            // stays in sync with the bag-mouth animation. null = the rail fallback.
            // Set via ReachPath(def, ...floats).
            public float[] reachHandPath;

            // Eat by TIME instead of bites: HOLD the item at the mouth and the resource/
            // use-time drains like a drink (StepDrink — pause by lowering, live bar tick).
            // Sausage / tarka / noodles "stay true to their use time"; bites is unused.
            public bool eatByTime;
            // Arms layer-1 state the timed-eat / gulp loop replays (0 = STATE_USE).
            // Sausage's clip uses NON-standard hashes, so it overrides this (a recon guess
            // — the live eatLoopState static A/Bs it). Also the EnterUseState target on
            // such graphs (the finish's cancel-from-OPEN escape needs a state that exists).
            // Set via Snack's param or Timed(def, loop).
            public int eatLoopState;
            // Arms layer-1 state the OPEN plays/scrubs (0 = the archetype default:
            // STATE_OPEN, or STATE_USE for Wrapper foods). Foods on a NON-standard graph
            // need this — noodles has no STATE_USE at all, so the Wrapper default made the
            // scrub Play() a state that doesn't exist (a silent no-op: the wrapper never
            // ripped). Set via OpenState(def, hash); recon [STATE] lines give the hashes.
            public int openState;
            // One-shot arms state PLAYED FOR REAL when the open completes (0 = none), for an
            // open visual a scrub can't show: noodles' bag-RIP is in the early frames of its
            // first eat state (2073176132), not STATE_OPEN, so the pull never displayed it (it
            // only showed on the first hold-to-mouth). FinishOpen plays this to openPlayMaxTime
            // then goes Ready; the chew loop (eatLoopState) must DIFFER or eating re-rips.
            // Set via OpenPlay(def, state, maxTime). Emitted by DumpFoodDef.
            public int openPlayState;
            public float openPlayMaxTime = 1f;
            // A transform whose renderers are HIDDEN when the open completes — the torn-off
            // cover/lid that would otherwise FLOAT next to the item once the rip freezes
            // (noodles' Rolton_1st_person_Packet_2_LOD0). Resolved under the item root at
            // spawn, restored on teardown. null = nothing hidden.
            public string hideOnOpenName;
            // Hide the held item's renderers progressively as it's consumed — stepped at
            // the audible bite sounds, synced to the eaten fraction (the sausage shrinks).
            public bool segmentedBites;
            // CanSpoon hybrid: the CAN itself can also be held at the MOUTH and drunk down
            // the item's use-time (condensed milk). The spoon path stays available — each
            // scoop is capped at what's LEFT, so a half-drunk can finishes in fewer scoops.
            public bool mouthDrink;
            // Optional SECOND food piece taken together with the first (sugar grabs two
            // cubes at once); rides its own holder/grip on the same taking hand.
            public string foodPiece2Name;
            public Vector3 food2Pos; public Vector3 food2Rot;

            // Interaction zones = WHERE you reach to trigger each gesture (distinct from the grips
            // above = where the prop SITS). Fires when the acting PALM probe is within <radius> of
            // an anchor + offset (palmCollisionPoints: probes/anchors sit on the rendered palms,
            // not the wrist-origin rig transforms — offsets here are measured FROM the holding
            // palm). Defaults (0 offset + the radius) = palm-to-palm / palm-to-mouth; set offsets
            // via Zones() for off-palm items (bottle cap, spout). open/take anchor on the holding
            // hand; eat on the eating hand vs the head.
            public Vector3 openZoneOffset; public float openZoneRadius = DefaultOpenZoneRadius;
            public Vector3 takeZoneOffset; public float takeZoneRadius = DefaultTakeZoneRadius;
            // Eat zone is anchored at the MOUTH (head + mouthLocalOffset), not the eye
            // center, so the radius no longer needs to over-reach downward. (Was 0.23
            // when the anchor sat between the eyes.)
            public Vector3 eatZoneOffset; public float eatZoneRadius = DefaultEatZoneRadius;

            public bool HasSpoon => !string.IsNullOrEmpty(spoonName);
            public bool HasCap => !string.IsNullOrEmpty(capName);
        }

        // Archetype-default zone radii. Used by the FoodDef initializers above AND by
        // ZoneArgs' "moved off default" checks, so DumpFoodDef only emits a radius when
        // a food actually overrode it (they had drifted apart — the old 0.18 literals in
        // ZoneArgs made every dump emit all three radii).
        private const float DefaultOpenZoneRadius = 0.10f;
        private const float DefaultTakeZoneRadius = 0.10f;
        private const float DefaultEatZoneRadius = 0.08f;

        // ===== Food registry ====================================================
        // Add a food = one line: pick the archetype factory (or a SHAPE preset that wraps
        // it — SairaCan/BigCan, SodaCan/TetraPak/Bottle), pass the template id + whatever
        // differs. Use EatingRecon for a paste-ready line, then DumpFoodDef() to bake
        // in-headset tuning (fold a drink's tuned grips into its shape preset).
        //   CanSpoon — can in left hand, lid roll, spoon scoops in right (tushonka)
        //   CanHand  — same, no spoon: grab the food by the right hand (sprats)
        //   Wrapper  — bar in right hand, left hand peels the wrapper (chocolate)
        //   Bag      — bag in right hand, shake crackers into the left hand (croutons)
        //   Pack     — pack in right hand, left hand takes a piece and eats it (galette)
        //   Drink    — container in one hand, free hand pops the cap, HOLD at the mouth to
        //              drink down the vanilla use-time; capped bottles recap to stop early
        //   ReachBag — pouch in right hand, left rips the top, then the left hand REACHES
        //              INTO the bag (rail-latched, depth scrubs), trigger grabs a cracker,
        //              pull it out, eat from the left hand (iskra / MRE)
        // Off-palm item? Wrap in Zones(...): Zones(CanHand(...), eatOffset: V(0f, 0.1f, 0f)).
        // 1. add a FoodKind value
        // 2. add a BuildStyle case   ← the gesture behavior(hand roles + hooks)
        // 3. add a Setup* Props method + a LateZeroProps case   ← the prop wiring
        // 4. add a factory

        // Zones - X: moves trigger points to left+/right- (palm facing up), Y: Moves trigger point up-/down+, Z: moves trigger point forward+/back-
        private static readonly FoodDef[] Defs =
        {
            //--- Canned food (spoon / hand) ---------------------------------------
            SairaCan("57347d7224597744596b4e72"), // Tushonka (small can)
            SairaCan("5673de654bdc2d180f8b456d"), // Saury
            SairaCan("57347d5f245977448b40fa81"), // Humpback salmon
            SairaCan("57347d9c245977448b40fa85"), // Herring
            SairaCan("69774bb0a247161ff1068335"), // Duck pate
            BigCan("57347da92459774491567cf5"),   // Tushonka (big can)
            BigCan("57347d692459774491567cf1"),   // Peas
            BigCan("57347d8724597744596b4e76"),   // Squash
            Noodles("656df4fec921ad01000481a2"), // Rolton noodles (all wiring in the Noodles preset)
            PullStart(HandPath(OpenGrip(CanHand("5bc9c29cd4351e003562b8a3", "sprats_root", "sprats_foodpiece"), "sprats_key"), SpratsOpenPath), 0.26f), // Sprats
            //--- Bars / bags / packs ----------------------------------------------
            // HandPath = the authored peel; OpenGrip (sn_cover ride) is the fallback.
            PullStart(HandPath(OpenGrip(Wrapper("544fb6cc4bdc2d34748b456e", "item_slickers_LOD0", "sn_CAT", "sn_cover"), "sn_cover"), ChocolateOpenPath), 0.15f), // Chocolate bar (slickers)
            // HandPath = the authored tear; OpenGrip (bone_ugol corner ride, ~45cm tear +
            // toss) is the fallback if the path is removed.
            PullStart(HandPath(OpenGrip(Bag("5751487e245977207e26a315", "bone_upakovka", "bone_suharik_hold"), "bone_ugol"), CroutonOpenPath), 0.31f), // Emelya croutons
            PullStart(HandPath(OpenGrip(Bag("57347d3d245977448f7b7f61", "bone_upakovka", "bone_suharik_hold"), "bone_ugol"), CroutonOpenPath), 0.31f), // Rye croutons
            // Held root is the bone group pack_CAT (drives the skinned mesh + holds everything),
            // NOT item_galettte_pack_LOD0 (a SkinnedMeshRenderer whose transform is a phantom).
            HandPath(Pack("5448ff904bdc2d6f028b456e", "pack_CAT", null, "item_galette_LOD0"), GaletteOpenPath), // Galette (crackers)
            //--- Drinks (open, then HOLD at the mouth; capped bottles recap to stop) ---
            SodaCan("60b0f93284c20f0feb453da7"),  // Rat Cola
            SodaCan("5751435d24597720a27126d1"),  // Max Energy
            SodaCan("575062b524597720a31c09a1"),  // Green tea
            OpenWith(PullStart(HandPath(Drink("5751496424597720a27126da", "hr_root", null, // Hotrod (its own rig; held right)
                drinkPos: V(-0.098f, -0.055f, 0.038f), drinkRot: V(0.6f, 193.3f, 295.2f)), HotrodOpenPath), 0.16f),
                OpenGestureKind.Tilt), // can tab tips open, like the sodas
            TetraPak("544fb62a4bdc2dfb738b4568"), // Pineapple juice
            TetraPak("575146b724597720a27126d5"), // Milk
            JuiceBottle("57513f07245977207e26a311"), // Apple juice
            JuiceBottle("57513fcc24597720a31c09a6"), // Vitajuice
            JuiceBottle("57513f9324597720a7128161"), // Grandma's juice
            // Condensed milk — was a Drink; switched to CanSpoon (the rig IS the saira can,
            // same key-roll open) so it can be SPOONED or DRUNK from the can (mouthDrink:
            // hold the can at your mouth — use-time drain, drink audio; the 2 scoops shrink
            // with what's left, so half-drunk = one scoop and done). Its clip is the drink
            // clip, so openSounds = just "Open"; spoon/food grips = the saira defaults.
            // VERIFY the rig has saira_spoon/saira_foodpiece — a "Missing props" log means
            // it fell back to vanilla and this needs its own prop names.
            PullStart(HandPath(OpenGrip(CanSpoon("5734773724597737fd047c14", "saira_root", null, null, bites: 2,
                canPos: V(-0.125f, -0.056f, 0.02f), canRot: V(358.3f, 326.2f, 73.1f), eatSound: "Drink"), "saira_key"), CondMilkOpenPath), 0.21f),
            PullStart(HandPath(Bottle("62a09f32621468534a797acb", // Pevko Beer
                pos: V(-0.113f, -0.065f, 0.064f), rot: V(6.8f, 349.4f, 278.3f),
                capPos: V(-0.109f, -0.036f, -0.049f), capRot: V(280.1f, 87.5f, 39.1f)), BeerOpenPath), 0.35f),
            // Foil ration pouch: held in the RIGHT hand (override the bottle default), no cap.
            // OpenGrip = the foil tear bones (ration3 = the torn strip, ~5cm). NOTE mirror
            // food: vanilla tears with the R hand, we open with the L — A/B its HandPath.
            PullStart(HandPath(OpenGrip(Bottle("60098b1705871270cd5352a1", cap: false, // Emergency water ration
                pos: V(-0.09f, -0.092f, -0.005f), rot: V(14.6f, 44.2f, 56.9f)), "ration3"), RationOpenPath), 0.27f),
            PullStart(HandPath(Bottle("5d40407c86f774318526545a", // Vodka
                pos: V(-0.114f, -0.064f, 0.062f), rot: V(7.6f, 349.2f, 278f),
                capPos: V(-0.111f, -0.04f, -0.043f), capRot: V(280f, 85.4f, 60.4f)), VodkaOpenPath), 0.3f),
            PullStart(HandPath(Bottle("5d403f9186f7743cac3f229b", // Whiskey
                pos: V(-0.098f, -0.058f, 0.017f), rot: V(341.6f, 347.4f, 287.6f),
                capPos: V(-0.111f, -0.04f, -0.044f), capRot: V(279.6f, 85.1f, 71.6f)), WhiskeyOpenPath), 0.44f),
            PullStart(HandPath(Bottle("5e8f3423fd7471236e6e3b64", // Kvas (same plastic-bottle shape as the water bottle)
                pos: V(-0.128f, -0.051f, -0.03f), rot: V(13.2f, 353.2f, 248.1f),
                capPos: V(-0.121f, -0.042f, -0.052f), capRot: V(283.4f, 36.7f, 169.3f)), KvasOpenPath), 0.29f),
            // Aquamari filter bottle: its own rig (fb_root) with a flip sport-cap (fb_cap, a
            // CHILD of fb_root, so it rides the bottle and animates open on its own — cap:null,
            // nothing to detach). Held in the LEFT hand.
            OpenWith(PullStart(HandPath(Drink("5c0fa877d174af02a012e1cf", "fb_root", null, // Aquamari filter bottle
                drinkPos: V(-0.073f, -0.059f, -0.091f), drinkRot: V(358.7f, 335.1f, 338.7f),
                holdInOffHand: true), AquamariOpenPath), 0.33f),
                OpenGestureKind.Tilt), // flip sport-cap: tip the hand open
            // Hinged flip-top: the whole "hinge" group (a sibling of mod_item) stays welded to
            // the bottle and rides the hand — it animates open in place, nothing detaches.
            // OpenGrip = 'cap', the flip lid INSIDE the hinge group (it animates; the glued
            // hinge root doesn't), ~7cm flip — the fallback under the HandPath.
            PullStart(HandPath(OpenGrip(Bottle("5d1b376e86f774252519444e", hingedCap: "hinge", // Moonshine
                pos: V(-0.116f, -0.065f, 0.062f), rot: V(7.6f, 349.6f, 278.2f)), "cap"), MoonshineOpenPath), 0.27f),
            // Superwater is a big jug held by the TOP HANDLE in the LEFT hand (holdInOffHand,
            // the bottle default) — so mod_item's origin hangs ~0.18m below the grip. Left-palm
            // bottle + right-palm cap from the recon; still approximate (the jug wobbled in the
            // measure), so fine-tune EatBaseHolder/EatCapHolder live + DumpFoodDef.
            PullStart(HandPath(Zones(Bottle("5d1b33a686f7742523398398", // Superwater
                pos: V(-0.021f, -0.175f, 0.067f), rot: V(300.7f, 264f, 89.6f),
                capPos: V(-0.118f, -0.048f, -0.006f), capRot: V(276.7f, 118.7f, 315.4f)), 
                openOffset: V(0f, 0f, 0.1f), takeOffset: V(0f, 0f, 0.1f)), SuperwaterOpenPath), 0.31f),
            PullStart(HandPath(Bottle("5448fee04bdc2dbc018b4567", // Water bottle (60/60 — drinks down partially)
                pos: V(-0.128f, -0.051f, -0.031f), rot: V(13.3f, 353f, 248f),
                capPos: V(-0.121f, -0.043f, -0.053f), capRot: V(283.4f, 35.5f, 162.8f)), WaterBottleOpenPath), 0.29f),

            //=== New foods 2026-06-13 (recon-data batch; grips/sounds measured, HandPaths
            //    still to capture — re-eat each VANILLA once with a FoodDef present to bake them). ===
            // Alyonka chocolate — Wrapper, exactly like slickers (held R, LEFT peels sn_cover).
            // Open@0.258 in STATE_USE.
            SoundTimes(PullStart(HandPath(OpenGrip(Wrapper("57505f6224597709a92585a9", "item_alyonka_LOD0", "sn_CAT", "sn_cover",
                barPos: V(-0.122f, -0.059f, -0.028f), barRot: V(19f, 146.5f, 10f),
                coverPos: V(-0.136f, -0.076f, 0.038f), coverRot: V(3.9f, 60.9f, 356.3f)), "sn_cover"), AlyonkaOpenPath), 0.15f), 0.258f),
            // Mayo — Drink held in the RIGHT hand, screw cap (mayo_cap) -> Tilt. Health 100/100
            // drains live at the mouth. Open@0.358 in STATE_OPEN.
            OpenWith(SoundTimes(PullStart(HandPath(Drink("5bc9b156d4351e00367fbce9", "mayo_root", "mayo_cap",
                drinkPos: V(-0.109f, -0.055f, 0.026f), drinkRot: V(358.6f, 199.1f, 301.5f),
                capPos: V(-0.124f, -0.04f, 0.035f), capRot: V(274.2f, 79.4f, 303.5f)), MayoOpenPath), 0.11f), 0.358f), OpenGestureKind.Tilt),
            // Oat flakes — Bag like croutons (held R, LEFT opens, SHAKE into the left hand).
            // 4 shakes empty the 40/40 box (bites: 4). Corner-tear bone = bone_oatmeal_ugol_000.
            PullStart(HandPath(OpenGrip(Bag("57347d90245977448f7b7f65", "bone_upakovka", "bone_suharik_hold", bites: 4,
                bagPos: V(-0.145f, -0.039f, -0.053f), bagRot: V(1.7f, 193.1f, 268.9f)), "bone_oatmeal_ugol_000"), OatmealOpenPath), 0.31f),
            // Salad box — CanSpoon, held in the LEFT hand (saira-shaped: same Open/Open2/SpoonTake
            // sound timing, so the default openSoundTimes apply). Peel salad_cover_root, spoon scoops.
            PullEnd(PullStart(HandPath(OpenGrip(CanSpoon("67586b7e49c2fa592e0d8ed9", "salad_root", "saira_spoon", "salad_foodpiece",
                canPos: V(-0.115f, -0.025f, -0.014f), canRot: V(79.5f, 243.3f, 321f),
                spoonPos: V(-0.112f, -0.07f, -0.012f), spoonRot: V(22.3f, 153.2f, 138.7f)),
                "salad_cover_root"), SaladOpenPath), 0.21f), 0.6f),
            
            // Tarka dried meat (snacker_beef) — Wrapper: held R on pack_root, LEFT rips
            // pack_cover00, then HOLD at the mouth (Timed — true to the use time). PullStart
            // = the path's first key: without it the auto-captured scrub start sat deep into
            // STATE_USE ("animation already progressed too far" on the first press).
            Timed(PullStart(HandPath(Wrapper("65815f0e647e3d7246384e14", "pack_root", null, "pack_cover00", bites: 1,
                barPos: V(-0.125f, -0.084f, -0.041f), barRot: V(345.9f, 190.3f, 107.7f),
                coverPos: V(-0.106f, -0.06f, -0.006f), coverRot: V(296.5f, 345f, 246f)), TarkaOpenPath), 0.000f)),
            // Sugar — CanHand mirror of sprats: box (item_sugar_box, the bone root driving the
            // skinned mod_item mesh) held in the LEFT hand, RIGHT opens + grabs TWO cubes at
            // once (sugar_piece_000 + _001, each on its own holder — vanilla shows a pair) +
            // eats. The remaining pile (sugar_piecese) is a SIBLING of the box in the rig, so
            // it's GLUED on to ride the hand. Sounds are Open/Open1 (not Open2).
            SoundTimes(PullStart(HandPath(Zones(CanHand("59e3577886f774176a362503", "item_sugar_box", "sugar_piece_000",
                canPos: V(-0.132f, -0.072f, -0.052f), canRot: V(359.3f, 130.2f, 3.5f),
                foodPos: V(-0.143f, -0.041f, -0.023f), foodRot: V(291.9f, 166.6f, 315.2f),
                food2: "sugar_piece_001", food2Pos: V(-0.137f, -0.039f, -0.012f), food2Rot: V(291.9f, 188.5f, 0.1f),
                glue: "sugar_piecese",
                openSounds: new[] { "Open", "Open1" }),
                openOffset: V(0f, 0f, -0.14f), takeOffset: V(0f, 0f, -0.14f)), SugarOpenPath), 0.24f), 0.233f, 0.606f),
            // Sausage — Snack: held R, no open; HOLD it at your mouth and it eats down the
            // item's use-time (Timed + segmented are baked into Snack — a sausage segment
            // hides per audible bite, tip first).
            Snack("635a758bfefc88a93f021b8a", "bone_item_food_sausage"),

            //=== Reach-into-bag: pouch held R on the bone-root group, LEFT rips the strap
            //    top (pull-open; HandPath = the authored rip, baked 2026-06), then the LEFT
            //    hand reaches INTO the bag mouth — see StepReachTake; the latched hand
            //    replays the baked ReachPath. Reach segment 0.02->0.3 (headset-corrected —
            //    tune the live statics MID-EAT only, the spawn re-seeds them). ===
            // Iskra ration: IFR_CAT > ifr_root > ifr_galette (the cracker; the item_ifr_LOD0
            // mesh is a skinned phantom — never hold it). Grip = recon R-palm IFR_CAT.
            PullStart(ReachPath(HandPath(Zones(ReachBag("590c5d4b86f774784e1b9c45", "IFR_CAT", "ifr_galette",
                bagPos: V(-0.179f, -0.098f, -0.032f), bagRot: V(49.8f, 92.7f, 265.2f)),
                openOffset: V(0f, 0f, -0.02f), takeOffset: V(0f, 0f, -0.02f), takeRadius: 0.20f), IskraOpenPath), IskraReachPath), 0.2f),
            // MRE: same family (mre_CAT > mre_root > mre_galette), its own grip; a bigger
            // meal -> 3 reach->grab->eat rounds.
            PullStart(ReachPath(HandPath(Zones(ReachBag("590c5f0d86f77413997acfab", "mre_CAT", "mre_galette", bites: 3,
                bagPos: V(-0.148f, -0.076f, -0.037f), bagRot: V(49.7f, 92.6f, 272.5f)),
                openOffset: V(0f, 0f, -0.02f), takeOffset: V(0f, 0f, -0.02f), takeRadius: 0.20f), MreOpenPath), MreReachPath), 0.2f),
        };

        // --- Canned-food shapes ------------------------------------------------
        // Every small can shares the saira rig (root/spoon/foodpiece); big cans add the
        // shifted reach zones for the larger body. Override per item only when one differs.
        // PullStart 0.21 = the path's hand-on-can key (the lid-roll "Open" sounds begin
        // ~0.23), so trigger-press starts with the hand AT the lid, not mid-reach.
        // PullEnd 0.6 = the lid is fully rolled (the SairaOpenPath keys past ~0.6 are the
        // clip's spoon-grab dive): the hand unlocks there and the spoon grab AUTO-PLAYS to
        // openReadyTime instead of being part of the pull. First guess — tune pullEndTime live.
        private static FoodDef SairaCan(string id, int bites = 3)
            => PullEnd(PullStart(HandPath(OpenGrip(CanSpoon(id, "saira_root", "saira_spoon", "saira_foodpiece", bites: bites), "saira_key"), SairaOpenPath), 0.21f), 0.6f);

        private static FoodDef BigCan(string id, int bites = 3)
            => PullEnd(PullStart(HandPath(OpenGrip(Zones(CanSpoon(id, "saira_root", "saira_spoon", "saira_foodpiece", bites: bites, bigCan: true),
                openOffset: V(0f, 0f, 0.05f), takeOffset: V(0f, 0f, 0.05f)), "saira_key"), SairaOpenPath), 0.21f), 0.6f);

        // --- Drink container shapes --------------------------------------------
        // One preset per container model: bakes that shape's prop names (and, once tuned,
        // its measured grips) so the registry stays one short line per drink. A bottle
        // whose size/shape needs its own grip passes pos/rot here; a drink the vanilla
        // animation holds in the LEFT hand passes holdInOffHand: true (cap hand flips too).
        private static FoodDef SodaCan(string id,
            Vector3? pos = null, Vector3? rot = null, bool holdInOffHand = false, float drinkSeconds = 0f)
            => OpenWith(PullStart(HandPath(Drink(id, "tc_root", null, pos, rot, holdInOffHand: holdInOffHand, drinkSeconds: drinkSeconds), SodaOpenPath), 0.16f),
                OpenGestureKind.Tilt); // tab tips open by TILTING the hand (like aquamari) — keeps the
                                       // gradual HandPath replay; the Trigger kind swept the scrub in a
                                       // frame and teleported the latched hand (user preferred the path)

        // TetraPak carton: held in the RIGHT hand, no cap (you tear the corner). Grip
        // default = milk's measured tetrapak_root in the R palm (shared across cartons).
        private static FoodDef TetraPak(string id,
            Vector3? pos = null, Vector3? rot = null, bool holdInOffHand = false, float drinkSeconds = 0f)
            => PullStart(HandPath(Zones(Drink(id, "tetrapak_root", null,
                pos ?? V(-0.102f, -0.071f, 0.058f), rot ?? V(78.6f, 280.7f, 351.4f),
                holdInOffHand: holdInOffHand, drinkSeconds: drinkSeconds),
                openOffset: V(0f, 0f, -0.05f), takeOffset: V(0f, 0f, -0.05f))
                , TetraPakOpenPath), 0.08f);

        // Juice bottle (apple/vita/grand share the rig): held in the RIGHT hand, screw cap
        // (juice_cap, nested under juice_root) unscrews to the LEFT hand. Grip defaults =
        // the three juices' measured grips (all within ~1mm of each other).
        private static FoodDef JuiceBottle(string id,
            Vector3? pos = null, Vector3? rot = null, Vector3? capPos = null, Vector3? capRot = null)
            => PullStart(HandPath(OpenWith(Zones(Drink(id, "juice_root", "juice_cap",
                pos ?? V(-0.099f, -0.061f, 0.094f), rot ?? V(358.6f, 199.2f, 301.3f),
                capPos ?? V(-0.131f, -0.044f, 0.031f), capRot ?? V(281.5f, 280.8f, 15.8f),
                holdInOffHand: false), openOffset: V(0f, 0f, -0.05f), takeOffset: V(0f, 0f, -0.05f)), OpenGestureKind.Tilt), // screw cap: tilt the hand open
                JuiceOpenPath), 0.09f);

        // Screw-cap bottle: vanilla holds it in the LEFT hand and unscrews the cap with the
        // RIGHT, so holdInOffHand defaults TRUE here (cap hand flips to the right). Each
        // bottle shape differs enough to pass its own measured grips. hingedCap = a flip-top
        // group (e.g. moonshine's "hinge") that stays welded to the bottle instead of
        // detaching — it rides the holding hand and animates open in place (overrides `cap`).
        private static FoodDef Bottle(string id, bool cap = true, string hingedCap = null,
            Vector3? pos = null, Vector3? rot = null, Vector3? capPos = null, Vector3? capRot = null,
            bool holdInOffHand = true, float drinkSeconds = 0f)
        {
            FoodDef d = Zones(Drink(id, "mod_item", hingedCap ?? (cap ? "cap" : null),
                pos, rot, capPos, capRot, holdInOffHand, drinkSeconds), 
                openOffset: V(0f, 0f, 0.05f), takeOffset: V(0f, 0f, 0.05f));
            if (hingedCap != null) d.capHinged = true;
            // Any cap — screw or hinged flip-top — opens by TILTING the hand; a capless
            // bottle (the ration pouch) stays a pull.
            d.openGesture = hingedCap != null || cap ? OpenGestureKind.Tilt : OpenGestureKind.Pull;
            return d;
        }

        private static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        // CanSpoon (tushonka): can in LEFT hand, lid roll, spoon scoops in RIGHT, N bites.
        // Defaults = tushonka's measured grips/sounds; override only what differs for a new can.
        // mouthDrink: every CanSpoon can ALSO be drunk — hold the CAN at your mouth and the
        // resource drains down the item's use-time (drink model); the spoon path stays, with
        // each scoop capped at what's left (drink half, finish in half the scoops).
        private static FoodDef CanSpoon(string id, string root, string spoon, string food,
            int bites = 3,
            bool bigCan = false,
            Vector3? canPos = null, Vector3? canRot = null,
            Vector3? spoonPos = null, Vector3? spoonRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            float openReadyTime = 0.92f, float putAwayStartTime = 0.3f,
            string[] openSounds = null, string scoopSound = null, string eatSound = "Take") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.CannedFood,
                rootName = root,
                spoonName = spoon,
                foodPieceName = food,
                bites = bites,
                drawSound = "Draw",
                openSounds = openSounds ?? new[] { "Open", "Open2", "SpoonTake" },
                // Authored event times of the default sounds (tushonka recon [SOUND]:
                // Open@0.240 Open2@0.394 SpoonTake@0.850) — so "Open" starts under the
                // trigger SQUEEZE (which only covers ~0.21-0.29; the even-spacing estimate
                // put the first slot at 0.39 and the squeeze was silent), and SpoonTake
                // (past PullEnd 0.6) is played natively by the settle. Only valid for the
                // default sound set.
                openSoundTimes = openSounds == null ? new[] { 0.24f, 0.39f, 0.85f } : null,
                scoopSound = scoopSound,
                eatSound = eatSound,
                basePos = canPos ?? (bigCan ? V(-0.126f, -0.058f, 0.021f) : V(-0.1135f, -0.0298f, -0.0034f)),
                baseRot = canRot ?? (bigCan ? V(357.8f, 326.6f, 73.4f) : V(80.72f, 248.34f, 303.57f)),
                spoonPos = spoonPos ?? V(-0.1247f, -0.0537f, -0.0113f),
                spoonRot = spoonRot ?? V(40.67f, 194.91f, 210.26f),
                foodPos = foodPos ?? V(0f, 0.05f, 0.007f),
                foodRot = foodRot ?? Vector3.zero,
                openReadyTime = openReadyTime,
                putAwayStartTime = putAwayStartTime,
                deterministicGesture = false,
                mouthDrink = true,
            };

        // Snack (sausage): held in the RIGHT hand, the other hand does nothing; skipOpen
        // spawns straight into Ready. The eat is TIMED (eatByTime): HOLD it at your mouth
        // and it eats down the item's use-time like a drink, shedding a renderer segment
        // per audible bite (segmentedBites; flip segmentHideFromEnd live if it eats from
        // the wrong end). NOTE: sausage's clip uses non-standard state hashes (no shared
        // STATE_USE/END) — eatLoopState 412676735 is the 2nd recon hash, a GUESS for its
        // chew loop (A/B the live static); the put-away rides the ForceCommand cancel.
        private static FoodDef Snack(string id, string root,
            int bites = 1,
            Vector3? barPos = null, Vector3? barRot = null,
            int eatLoopState = 412676735,
            // "Take1" = sausage's MEASURED bite sound (headset log 2026-06-12: its bank
            // has NO 'Eat' element — the manual gulp was a silent no-op whose throttle
            // then blocked the clip's real 'Take1' events).
            string eatSound = "Take1") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.Handheld,
                rootName = root,
                wrapperName = null,
                coverName = null,
                spoonName = null,
                foodPieceName = null,
                bites = bites,
                skipOpen = true,
                drawSound = "Draw",
                openSounds = null,
                scoopSound = null,
                eatSound = eatSound,
                basePos = barPos ?? V(-0.123f, -0.039f, -0.051f),
                baseRot = barRot ?? V(5.4f, 204.5f, 317.8f),
                openReadyTime = 0.9f,
                putAwayStartTime = 0.3f,
                deterministicGesture = false,
                eatByTime = true,
                segmentedBites = true,
                eatLoopState = eatLoopState,
            };

        // CanHand (sprats): CanSpoon with no spoon — grab the food by the RIGHT hand. Deterministic
        // poses so every grab is the same clean reach->pinch->lift (STATE_USE@takePoseTime).
        // food2 = an optional SECOND piece grabbed together with the first (sugar takes two
        // cubes; default grips = sugar's measured piece_001). glue = a SIBLING group in the
        // rig that must ride the held box (sugar's remaining pile, sugar_piecese — outside
        // the held root, so without the glue it stays behind on the body-space rig).
        private static FoodDef CanHand(string id, string root, string food,
            int bites = 3,
            Vector3? canPos = null, Vector3? canRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            string food2 = null, Vector3? food2Pos = null, Vector3? food2Rot = null,
            string glue = null,
            float openReadyTime = 0.9f, float putAwayStartTime = 0.3f,
            float takePoseTime = 0.11f, float bitePoseTime = 0.9f,
            string[] openSounds = null, string scoopSound = "Take", string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.CannedFood,
                rootName = root,
                spoonName = null,
                foodPieceName = food,
                foodPiece2Name = food2,
                food2Pos = food2Pos ?? V(-0.157f, -0.039f, -0.012f),
                food2Rot = food2Rot ?? V(291.9f, 188.5f, 0.1f),
                wrapperName = glue,
                bites = bites,
                drawSound = "Draw",
                openSounds = openSounds ?? new[] { "Open", "Open2" },
                // Sprats recon: Open2@0.547. Open@ wasn't recorded — estimated at 0.30 so it
                // lands inside the trigger squeeze (sprats pull starts ~0.26), consistent
                // with tushonka's early Open. Re-recon for the exact Open time if it feels off.
                openSoundTimes = openSounds == null ? new[] { 0.30f, 0.547f } : null,
                scoopSound = scoopSound,
                eatSound = eatSound,
                basePos = canPos ?? V(-0.106f, -0.013f, 0f),
                baseRot = canRot ?? V(80.2f, 248.9f, 303.5f),
                foodPos = foodPos ?? V(-0.1247f, -0.0537f, -0.0113f),
                foodRot = foodRot ?? V(40.67f, 194.91f, 210.26f),
                openReadyTime = openReadyTime,
                putAwayStartTime = putAwayStartTime,
                deterministicGesture = true,
                takePoseTime = takePoseTime,
                bitePoseTime = bitePoseTime,
            };

        // Wrapper (chocolate): bar in RIGHT hand, LEFT peels it. wrapperGroup stays glued to the bar
        // (carries sub-pieces); cover DETACHES to the left hand once peeled. barPos/coverPos = the
        // bar's / peeled wrapper's grips. Defaults = chocolate.
        private static FoodDef Wrapper(string id, string root, string wrapperGroup, string cover,
            int bites = 1,
            Vector3? barPos = null, Vector3? barRot = null,
            Vector3? coverPos = null, Vector3? coverRot = null,
            float openReadyTime = 0.6f, float putAwayStartTime = 0.3f,
            string[] openSounds = null, string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.Handheld,
                rootName = root,
                wrapperName = wrapperGroup,
                coverName = cover,
                spoonName = null,
                foodPieceName = null,
                bites = bites,
                drawSound = "Draw",
                openSounds = openSounds ?? new[] { "Open" },
                // Chocolate recon: the open is one STATE_USE clip, Open@0.259 — lands inside
                // the trigger squeeze (the bar pull starts ~0.15). Only valid for the default sound.
                openSoundTimes = openSounds == null ? new[] { 0.259f } : null,
                scoopSound = null,
                eatSound = eatSound,
                basePos = barPos ?? V(-0.121f, -0.037f, -0.054f),
                baseRot = barRot ?? V(57.7f, 110.3f, 258.6f),
                foodPos = coverPos ?? V(-0.137f, -0.078f, 0.04f),
                foodRot = coverRot ?? V(27.4f, 105.2f, 354.7f),
                openReadyTime = openReadyTime,
                putAwayStartTime = putAwayStartTime,
                deterministicGesture = false,
            };

        // Noodles (Rolton) — a unique Wrapper on a NON-standard graph; all the noodle-specific
        // wiring folded into one preset (it was an 8-deep wrapper chain in Defs). Hard-won bits:
        //  - Hold the WHOLE bone group pack_CAT (not pack_root): the skinned packet has wrapper
        //    bones outside pack_root, so a narrower hold left them on the body rig and the
        //    wrapper STRETCHED. Glue nothing — descendant bones ride the held root for free.
        //  - Its graph has NO STATE_USE: the pull/handling scrubs STATE_OPEN (openState), the
        //    bite/split/eat live in 2073176132 / -893874298.
        //  - The bag-RIP VISUAL is in the early frames of the first eat state (2073176132), NOT
        //    STATE_OPEN — the scrub never showed it (it only appeared on the first hold-to-mouth).
        //    openPlayState plays it FOR REAL at open completion; the chew loop runs the OTHER eat
        //    state so it doesn't re-rip.
        //  - hideOnOpen hides the torn cover/lid mesh after the tear (else it floats by the bag).
        //  - Timed (hold-at-mouth down the use-time) + Segmented (shed the 9 Rolton piece
        //    renderers per audible bite). pullStartTime is tuned live — bake your value here.
        private static FoodDef Noodles(string id) => new FoodDef
        {
            templateId = id,
            kind = FoodKind.Handheld,
            rootName = "pack_CAT",
            wrapperName = null,
            coverName = null,
            spoonName = null,
            foodPieceName = null,
            bites = 1,
            drawSound = "Draw",
            openSounds = new[] { "Take3" }, // the rip sound (STATE_OPEN @ 0.784)
            openSoundTimes = new[] { 0.784f },
            eatSound = "Eat",
            basePos = V(-0.115f, -0.069f, -0.03f),
            baseRot = V(338.7f, 345.6f, 257.5f),
            openReadyTime = 0.92f,
            putAwayStartTime = 0.3f,
            deterministicGesture = false,
            openState = STATE_OPEN_HASH,    // graph has no STATE_USE — scrub its STATE_OPEN
            openHandPath = NoodlesOpenPath, // left-palm rip arc (replays during the pull)
            pullStartTime = 0.784f,         // tuned live (user); update if you re-DumpFoodDef a different value
            openPlayState = 2073176132,     // the rip VISUAL lives here, played for real on open
            openPlayMaxTime = 0.34f,
            eatByTime = true,
            eatLoopState = -893874298,      // chew loop (different state = no re-rip)
            segmentedBites = true,          // shed the 9 Rolton piece renderers per bite
            hideOnOpenName = "Rolton_1st_person_Packet_2_LOD0", // the torn cover/lid — hidden after the rip
        };

        // Drink (bottles/cans/cartons): container in ONE hand (right unless holdInOffHand),
        // the free hand pops the cap (glued ON the container until then), then HOLD it at the
        // mouth — the item's vanilla use-time runs down while held. Multi-use bottles (water)
        // drain partially and only discard at 0; capped bottles recap (free hand back at the
        // cap zone + trigger) to stop early. No bite counting.
        private static FoodDef Drink(string id, string root, string cap,
            Vector3? drinkPos = null, Vector3? drinkRot = null,
            Vector3? capPos = null, Vector3? capRot = null,
            bool holdInOffHand = false,
            float drinkSeconds = 0f,           // 0 = read the item's vanilla UseTime
            float openReadyTime = 0.9f, float putAwayStartTime = 0.7f,
            string[] openSounds = null, string eatSound = "Drink") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.Drink,
                rootName = root,
                capName = cap,
                holdInOffHand = holdInOffHand,
                drinkSeconds = drinkSeconds,
                spoonName = null,
                foodPieceName = null,
                bites = 1,
                drawSound = "Draw",
                openSounds = openSounds ?? new[] { "Open" },
                scoopSound = null,
                eatSound = eatSound,
                basePos = drinkPos ?? V(-0.106f, -0.055f, 0.039f),
                baseRot = drinkRot ?? V(0.1f, 193.2f, 294.8f),
                capPos = capPos ?? V(-0.137f, -0.078f, 0.04f),
                capRot = capRot ?? V(27.4f, 105.2f, 354.7f),
                openReadyTime = openReadyTime,
                putAwayStartTime = putAwayStartTime,
                deterministicGesture = false,
            };

        // Bag (croutons): bag (root, holds everything) in RIGHT hand, LEFT opens it, then SHAKE
        // near the left hand to pour the crackerPrefix-matched crackers into the LEFT hand, eaten
        // left-handed. bites = shake->eat rounds. bagPos/crackerPos = the bag's / clump's grips.
        private static FoodDef Bag(string id, string root, string crackerPrefix,
            int bites = 2,
            Vector3? bagPos = null, Vector3? bagRot = null,
            Vector3? crackerPos = null, Vector3? crackerRot = null,
            float openReadyTime = 0.85f, float putAwayStartTime = 0.3f,
            string[] openSounds = null, string scoopSound = "Take", string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.Bag,
                rootName = root,
                foodPieceName = crackerPrefix,
                spoonName = null,
                bites = bites,
                drawSound = "Draw",
                openSounds = openSounds ?? new[] { "Draw1", "Open", "Draw" },
                scoopSound = scoopSound,
                eatSound = eatSound,
                // bag grip measured (palm->bone_upakovka, R palm).
                basePos = bagPos ?? V(-0.148f, -0.040f, -0.026f),
                baseRot = bagRot ?? V(355.4f, 204.9f, 251.6f),
                // cracker clump-anchor grip measured in-hand (palm->...hold_000, L palm).
                foodPos = crackerPos ?? V(-0.067f, -0.042f, -0.003f),
                foodRot = crackerRot ?? V(8f, 73.1f, 93.4f),
                openReadyTime = openReadyTime,
                putAwayStartTime = putAwayStartTime,
                deterministicGesture = false,
            };

        // Pack (galette): wrapped pack (root) in RIGHT hand, wrapperGroup glued to it (cover opens
        // in place, no detach). LEFT opens it, takes the food piece into the LEFT hand, eats there.
        // Mirror of CanHand. bites = take->eat rounds. packPos/foodPos = the pack's / piece's grips.
        private static FoodDef Pack(string id, string root, string wrapperGroup, string food,
            int bites = 2,
            Vector3? packPos = null, Vector3? packRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            float openReadyTime = 0.9f, float putAwayStartTime = 0.3f,
            float takePoseTime = 0.3f, float bitePoseTime = 1f,
            string[] openSounds = null, string scoopSound = "Take", string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.Pack,
                rootName = root,
                wrapperName = wrapperGroup,
                foodPieceName = food,
                spoonName = null,
                bites = bites,
                drawSound = "Draw",
                openSounds = openSounds ?? new[] { "Draw", "Open" },
                scoopSound = scoopSound,
                eatSound = eatSound,
                // pack grip measured (R palm).
                basePos = packPos ?? V(-0.124f, -0.052f, -0.054f),
                baseRot = packRot ?? V(28.2f, 112.6f, 254.7f),
                // piece grip measured (L palm, in-hand).
                foodPos = foodPos ?? V(-0.15f, -0.031f, 0.04f),
                foodRot = foodRot ?? V(38.4f, 27.3f, 286.7f),
                openReadyTime = openReadyTime,
                putAwayStartTime = putAwayStartTime,
                // Deterministic take/bite like CanHand; also keeps the animator out of STATE_OPEN
                // so the finish cancels cleanly.
                deterministicGesture = true,
                takePoseTime = takePoseTime,
                bitePoseTime = bitePoseTime,
            };

        // ReachBag (iskra/MRE): pouch (root, the bone-root group) in the RIGHT hand, LEFT
        // rips the top open (standard pull-open on STATE_OPEN), then the LEFT hand REACHES
        // INTO the bag: entering the take zone locks the hand onto a rail (depth scrubs the
        // reach segment of STATE_USE — the bag mouth widens as you push in), trigger while
        // deep grabs the cracker (foodPieceName) into the reaching hand, pulling back out
        // completes the take; eaten from the LEFT hand. Leaving the zone releases the hand.
        // bites = reach->grab->eat rounds. Defaults = iskra's recon values.
        private static FoodDef ReachBag(string id, string root, string food,
            int bites = 3,
            Vector3? bagPos = null, Vector3? bagRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            float openReadyTime = 0.6f, float putAwayStartTime = 0.3f,
            float takePoseTime = 0.3f, float bitePoseTime = 1f,
            float reachStartTime = 0.02f, float reachDeepTime = 0.3f,
            string[] openSounds = null, string scoopSound = "Take", string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.ReachBag,
                rootName = root,
                foodPieceName = food,
                wrapperName = null,
                spoonName = null,
                bites = bites,
                drawSound = "Draw",
                openSounds = openSounds ?? new[] { "Open" },
                // Iskra/MRE recon: one Open event in STATE_OPEN (@0.371/0.366) — lands in the
                // physical-pull range with the seeded PullStart 0.2. Default sound set only.
                openSoundTimes = openSounds == null ? new[] { 0.37f } : null,
                scoopSound = scoopSound,
                eatSound = eatSound,
                // bag grip = the recon's R-palm bone-root average (per food via bagPos/bagRot).
                basePos = bagPos ?? V(-0.179f, -0.098f, -0.032f),
                baseRot = bagRot ?? V(49.8f, 92.7f, 265.2f),
                // The cracker lands in the LEFT hand but vanilla holds it RIGHT (mirror food —
                // its measured grip is on the wrong palm), so seed from the galette pack's
                // tuned L-palm piece grip: same cracker mesh (item_galette_LOD0).
                foodPos = foodPos ?? V(-0.15f, -0.031f, 0.04f),
                foodRot = foodRot ?? V(38.4f, 27.3f, 286.7f),
                openReadyTime = openReadyTime,
                putAwayStartTime = putAwayStartTime,
                reachStartTime = reachStartTime,
                reachDeepTime = reachDeepTime,
                deterministicGesture = false,
                takePoseTime = takePoseTime,
                bitePoseTime = bitePoseTime,
            };

        // The pull-open hand-ride: name the clip-animated bone the latched hand follows (the
        // lid/pull-key for cans, the peeling cover for the chocolate...). With pullSnapToGrip
        // (default) the index FINGERTIP snaps onto it at the grab; the bone's motion is
        // exactly what you SEE on the item, so the hand stays aligned as your pull scrubs
        // it. Chains onto any factory like Zones: OpenGrip(SairaCan(...), "lid_bone").
        // Omit = the latch freezes where you grabbed. Don't know the name? Complete one
        // pull without it — the top movers under the item get logged (logOpenMovers).
        // Movers data so far (2026-06): can keys (saira_key/sprats_key ~9-11cm), the
        // chocolate cover (~38cm), the bag corner (~45cm), moonshine's flip lid ('cap'
        // inside the hinge, ~7cm) and the ration foil ('ration3', ~5cm) all ride.
        // DETACHABLE-cap drinks measure 0.0cm — OUR glue pins the cap until the pop, so
        // there's nothing to ride and freeze-at-grab is correct; soda cans / hotrod /
        // aquamari also measured 0 (no animated opener in the scrub range). Don't add
        // OpenGrip to those.
        private static FoodDef OpenGrip(FoodDef d, string bone) { d.openGripName = bone; return d; }

        // Baked open-hand path (see FoodDef.openHandPath): paste the recon's [HANDPATH]
        // output. Chains like Zones/OpenGrip; takes precedence over OpenGrip when both set.
        private static FoodDef HandPath(FoodDef d, params float[] packed) { d.openHandPath = packed != null && packed.Length >= 8 ? packed : null; return d; }

        // Baked reach-hand path (see FoodDef.reachHandPath, ReachBag only): paste the
        // recon's ReachPath(...) output. Chains like HandPath; null/short = the rail.
        private static FoodDef ReachPath(FoodDef d, params float[] packed) { d.reachHandPath = packed != null && packed.Length >= 8 ? packed : null; return d; }

        // Per-food pull start time (see FoodDef.pullStartTime). Chains like the others:
        // PullStart(HandPath(...), 0.21f). Tune live via the pullStartTime static, then
        // bake — DumpFoodDef round-trips it.
        private static FoodDef PullStart(FoodDef d, float t) { d.pullStartTime = t; return d; }

        // Per-food pull END time (see FoodDef.pullEndTime): where the hand UNLOCKS from the
        // pull; whatever is left of the open clip (e.g. the tushonka spoon grab) auto-plays
        // from there to openReadyTime. Chains: PullEnd(PullStart(...), 0.6f). Tune live via
        // the pullEndTime static; DumpFoodDef round-trips it.
        private static FoodDef PullEnd(FoodDef d, float t) { d.pullEndTime = t; return d; }

        // Per-food physical open gesture (see OpenGestureKind). Chains like the others:
        // OpenWith(Drink(...), OpenGestureKind.Tilt). The drink presets bake the common
        // mapping (capped/hinged Bottle, JuiceBottle, SodaCan = Tilt); use this for
        // one-offs (aquamari's flip sport-cap, hotrod's can tab).
        private static FoodDef OpenWith(FoodDef d, OpenGestureKind g) { d.openGesture = g; return d; }

        // Eat-by-time chain: the food is consumed by HOLDING it at the mouth for the item's
        // use-time (the drink model — live drain, pause by lowering) instead of counting
        // bites. Chains like the others: Timed(Wrapper(...)). Tarka + noodles; Snack bakes it.
        // Optional loop = the chew-loop state for NON-standard graphs (FoodDef.eatLoopState;
        // 0 keeps STATE_USE) — noodles passes its own take/split/eat state.
        private static FoodDef Timed(FoodDef d, int loop = 0) { d.eatByTime = true; if (loop != 0) d.eatLoopState = loop; return d; }

        // Per-food open-state override (see FoodDef.openState). Chains like the others:
        // OpenState(Wrapper(...), 492683391). Emitted by DumpFoodDef when set — losing it
        // on a paste-over would silently break the open scrub again.
        private static FoodDef OpenState(FoodDef d, int hash) { d.openState = hash; return d; }

        // One-shot open animation (see FoodDef.openPlayState): play `state` for real at open
        // completion (up to `maxTime`) to show an open visual the scrub can't — noodles' rip.
        // Chains: OpenPlay(Wrapper(...), 2073176132, 0.34f). Emitted by DumpFoodDef.
        private static FoodDef OpenPlay(FoodDef d, int state, float maxTime = 1f) { d.openPlayState = state; d.openPlayMaxTime = maxTime; return d; }

        // Segmented-bites chain for non-Snack foods (see FoodDef.segmentedBites): the held
        // item sheds a renderer per audible bite while held at the mouth, fraction-synced
        // (the sausage model — flip segmentHideFromEnd live if it eats from the wrong end).
        // NOT emitted by DumpFoodDef — keep it in the Defs line by hand. Noodles.
        private static FoodDef Segmented(FoodDef d) { d.segmentedBites = true; return d; }

        // Per-food authored open-sound times (see FoodDef.openSoundTimes — recon [SOUND]
        // gives them). Chains: SoundTimes(Bag(...), 0.18f, 0.42f). NOT emitted by
        // DumpFoodDef — keep it in the Defs line by hand.
        private static FoodDef SoundTimes(FoodDef d, params float[] times) { d.openSoundTimes = times != null && times.Length > 0 ? times : null; return d; }

        // Recon bridge: the held-item root bone name / the open-state hash for a known food
        // (the frame + time axis the [HANDPATH] capture must use). Null/0 = no def.
        public static string LookupRootName(string templateId) => FindDef(templateId)?.rootName;
        public static int LookupOpenStateHash(string templateId)
        {
            FoodDef d = FindDef(templateId);
            return d == null ? 0 : BuildStyle(d).openStateHash;
        }
        // ReachBag only: the arms state the reach scrub seeks in (= the time axis a
        // ReachPath capture must sample on). 0 = not a ReachBag def, no reach capture.
        public static int LookupReachStateHash(string templateId)
            => FindDef(templateId)?.kind == FoodKind.ReachBag ? STATE_USE_HASH : 0;

        // Move an off-palm item's reach points (bottle cap up high, spout at the mouth). Chains
        // onto any factory: Zones(CanHand(...), eatOffset: V(0f, 0.1f, 0f)). Leaves grips alone.
        // Offsets are anchor-local (open/take = holding hand; eat = eating hand vs head).
        private static FoodDef Zones(FoodDef d,
            Vector3? openOffset = null, float? openRadius = null,
            Vector3? takeOffset = null, float? takeRadius = null,
            Vector3? eatOffset = null, float? eatRadius = null)
        {
            if (openOffset.HasValue) d.openZoneOffset = openOffset.Value;
            if (openRadius.HasValue) d.openZoneRadius = openRadius.Value;
            if (takeOffset.HasValue) d.takeZoneOffset = takeOffset.Value;
            if (takeRadius.HasValue) d.takeZoneRadius = takeRadius.Value;
            if (eatOffset.HasValue) d.eatZoneOffset = eatOffset.Value;
            if (eatRadius.HasValue) d.eatZoneRadius = eatRadius.Value;
            return d;
        }

        // ===== Authoring helper ==================================================
        // Invoke from UnityExplorer while eating, AFTER tuning the holders live — prints a
        // paste-ready factory line (grips off the tuned holders, timings off the live knobs) so
        // you can bake your tuning into Defs. (Wrapper foods: open the wrapper first.)
        public static void DumpFoodDef()
        {
            if (!active || def == null) { Plugin.MyLog.LogWarning("[ManualEat] DumpFoodDef: no food is being eaten."); return; }
            Vector3 HolderPos(GameObject h, Vector3 fallback) => h != null ? h.transform.localPosition : fallback;
            Vector3 HolderRot(GameObject h, Vector3 fallback) => h != null ? h.transform.localEulerAngles : fallback;

            string core;
            if (def.kind == FoodKind.Bag)
            {
                core = $"Bag(\"{def.templateId}\", \"{def.rootName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"bagPos: {VStr(HolderPos(baseHolder, def.basePos))}, bagRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"crackerPos: {VStr(HolderPos(crackerHolder, def.foodPos))}, crackerRot: {VStr(HolderRot(crackerHolder, def.foodRot))}, "
                     + $"openReadyTime: {FStr(openReadyTime)}, putAwayStartTime: {FStr(putAwayStartTime)})";
            }
            else if (def.kind == FoodKind.Pack)
            {
                core = $"Pack(\"{def.templateId}\", \"{def.rootName}\", \"{def.wrapperName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"packPos: {VStr(HolderPos(baseHolder, def.basePos))}, packRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openReadyTime: {FStr(openReadyTime)}, putAwayStartTime: {FStr(putAwayStartTime)})";
            }
            else if (def.kind == FoodKind.Drink)
            {
                // Raw factory line — fold the tuned grips into the matching shape preset
                // (SodaCan/TetraPak/Bottle) if they should become that shape's defaults.
                core = $"Drink(\"{def.templateId}\", \"{def.rootName}\", {(def.HasCap ? $"\"{def.capName}\"" : "null")}, "
                     + $"drinkPos: {VStr(HolderPos(baseHolder, def.basePos))}, drinkRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + (def.HasCap ? $"capPos: {VStr(HolderPos(capHolder, def.capPos))}, capRot: {VStr(HolderRot(capHolder, def.capRot))}, " : "")
                     + (def.holdInOffHand ? "holdInOffHand: true, " : "")
                     + $"openReadyTime: {FStr(openReadyTime)}, putAwayStartTime: {FStr(putAwayStartTime)})";
            }
            else if (def.kind == FoodKind.ReachBag)
            {
                // Reach times come off the LIVE statics so in-headset tuning round-trips.
                core = $"ReachBag(\"{def.templateId}\", \"{def.rootName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"bagPos: {VStr(HolderPos(baseHolder, def.basePos))}, bagRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openReadyTime: {FStr(openReadyTime)}, putAwayStartTime: {FStr(putAwayStartTime)}, "
                     + $"reachStartTime: {FStr(reachStartTime)}, reachDeepTime: {FStr(reachDeepTime)})";
            }
            else if (def.kind == FoodKind.Handheld)
            {
                core = $"Wrapper(\"{def.templateId}\", \"{def.rootName}\", \"{def.wrapperName}\", \"{def.coverName}\", bites: {def.bites}, "
                     + $"barPos: {VStr(HolderPos(baseHolder, def.basePos))}, barRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"coverPos: {VStr(HolderPos(foodHolder, def.foodPos))}, coverRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openReadyTime: {FStr(openReadyTime)}, putAwayStartTime: {FStr(putAwayStartTime)})";
            }
            else if (def.HasSpoon)
            {
                core = $"CanSpoon(\"{def.templateId}\", \"{def.rootName}\", \"{def.spoonName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"canPos: {VStr(HolderPos(baseHolder, def.basePos))}, canRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"spoonPos: {VStr(HolderPos(spoonHolder, def.spoonPos))}, spoonRot: {VStr(HolderRot(spoonHolder, def.spoonRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openReadyTime: {FStr(openReadyTime)}, putAwayStartTime: {FStr(putAwayStartTime)})";
            }
            else
            {
                // canPos/canRot are the factory's arg names (the old dump said basePos and
                // the paste wouldn't compile). food2/glue round-trip when present.
                string food2 = foodT2 == null ? "" :
                      $"food2: \"{def.foodPiece2Name}\", food2Pos: {VStr(HolderPos(foodHolder2, def.food2Pos))}, food2Rot: {VStr(HolderRot(foodHolder2, def.food2Rot))}, "
                    + (string.IsNullOrEmpty(def.wrapperName) ? "" : $"glue: \"{def.wrapperName}\", ");
                core = $"CanHand(\"{def.templateId}\", \"{def.rootName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"canPos: {VStr(HolderPos(baseHolder, def.basePos))}, canRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + food2
                     + $"openReadyTime: {FStr(openReadyTime)}, putAwayStartTime: {FStr(putAwayStartTime)}, "
                     + $"takePoseTime: {FStr(takePoseTime)}, bitePoseTime: {FStr(bitePoseTime)})";
            }
            Plugin.MyLog.LogInfo("[ManualEat] === paste into Defs (tuned) ===\n            " + WrapPullEnd(WrapPullStart(WrapOpenWith(WrapReachPath(WrapHandPath(WrapOpenGrip(WrapOpenPlay(WrapOpenState(WrapZones(core))))))))) + ",");
        }

        // Wrap in OpenState(...) iff the food overrides the archetype's open state —
        // losing it on a paste-over silently breaks the open scrub (it would Play a
        // state that doesn't exist on that food's graph).
        private static string WrapOpenState(string core)
            => def.openState == 0 ? core : $"OpenState({core}, {def.openState})";

        // Wrap in OpenPlay(...) iff the food plays a one-shot open animation (the rip).
        // Read the LIVE statics so in-headset tuning of openPlayMaxTime round-trips.
        private static string WrapOpenPlay(string core)
            => openPlayState == 0 ? core : $"OpenPlay({core}, {openPlayState}, {FStr(openPlayMaxTime)})";

        // Wrap in OpenWith(...) iff the food's open gesture isn't the default Pull (may be
        // redundant when a preset bakes it — harmless, fold it back when pasting).
        private static string WrapOpenWith(string core)
            => def.openGesture == OpenGestureKind.Pull ? core : $"OpenWith({core}, OpenGestureKind.{def.openGesture})";

        // Wrap in PullStart(...) / PullEnd(...) iff moved off auto. Read the LIVE statics
        // (seeded from the FoodDef) so in-headset tuning round-trips.
        private static string WrapPullStart(string core)
            => pullStartTime < 0f ? core : $"PullStart({core}, {FStr(pullStartTime)})";

        private static string WrapPullEnd(string core)
            => pullEndTime < 0f ? core : $"PullEnd({core}, {FStr(pullEndTime)})";

        // Wrap in OpenGrip(...) iff the food has a pull-open ride bone, so DumpFoodDef
        // round-trips it like the zones.
        private static string WrapOpenGrip(string core)
            => string.IsNullOrEmpty(def.openGripName) ? core : $"OpenGrip({core}, \"{def.openGripName}\")";

        // Wrap in HandPath(...) iff the food has a baked open-hand path (one key per line).
        private static string WrapHandPath(string core) => WrapPath(core, "HandPath", def.openHandPath);

        // Wrap in ReachPath(...) iff the food has a baked reach-hand path (ReachBag).
        private static string WrapReachPath(string core) => WrapPath(core, "ReachPath", def.reachHandPath);

        private static string WrapPath(string core, string wrapper, float[] p)
        {
            if (p == null) return core;
            var sb = new StringBuilder($"{wrapper}({core}");
            for (int i = 0; i + 8 <= p.Length; i += 8)
                sb.Append($",\n                {p[i]:0.###}f, {p[i + 1]:0.###}f, {p[i + 2]:0.###}f, {p[i + 3]:0.###}f, {p[i + 4]:0.####}f, {p[i + 5]:0.####}f, {p[i + 6]:0.####}f, {p[i + 7]:0.####}f");
            sb.Append(")");
            return sb.ToString();
        }

        private static string VStr(Vector3 v) => $"V({v.x:0.###}f, {v.y:0.###}f, {v.z:0.###}f)";
        private static string FStr(float f) => $"{f:0.###}f";

        // Wrap in Zones(...) iff a zone was moved off default. Reads the live statics (seeded from
        // the FoodDef, A/B-tunable) so in-headset zone tuning round-trips into the pasteable line.
        private static string WrapZones(string core)
        {
            string z = ZoneArgs();
            return z.Length == 0 ? core : $"Zones({core}{z})";
        }

        private static string ZoneArgs()
        {
            var sb = new StringBuilder();
            if (openZoneOffset != Vector3.zero) sb.Append($", openOffset: {VStr(openZoneOffset)}");
            if (openDistance != DefaultOpenZoneRadius) sb.Append($", openRadius: {FStr(openDistance)}");
            if (takeZoneOffset != Vector3.zero) sb.Append($", takeOffset: {VStr(takeZoneOffset)}");
            if (scoopDistance != DefaultTakeZoneRadius) sb.Append($", takeRadius: {FStr(scoopDistance)}");
            if (eatZoneOffset != Vector3.zero) sb.Append($", eatOffset: {VStr(eatZoneOffset)}");
            if (eatDistance != DefaultEatZoneRadius) sb.Append($", eatRadius: {FStr(eatDistance)}");
            return sb.ToString();
        }

        private static FoodDef FindDef(string templateId)
        {
            if (string.IsNullOrEmpty(templateId)) return null;
            foreach (FoodDef d in Defs) if (d.templateId == templateId) return d;
            return null;
        }
    }
}
