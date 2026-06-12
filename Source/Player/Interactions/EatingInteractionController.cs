using EFT.InventoryLogic;
using MonoMod.RuntimeDetour;
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
    // Fully manual, two-handed, gesture-driven eating. Each handled food has a FoodDef; foods
    // without one fall back to vanilla auto-eat. Facts that shaped this (don't re-derive):
    //  * The eat is pure BONE animation (renderers never toggle), so props are moved by
    //    reparenting their roots — not by mesh swaps.
    //  * The controller animator re-applies its bone pose every frame even at speed 0, so
    //    per-frame writes to a prop's transform get stomped. Fix: REPARENT props out of the
    //    animated rig onto the VR hands (restored on teardown — the object is pooled).
    //  * Meds raises LEFT_HAND_ANIMATOR_HASH and freezes the left hand; a prefix on
    //    VRPlayerManager.UpdateLeftHand (EatingPatches.ForceLeftHandTracking) keeps it tracking.
    //  * The finish needs the animation to reach END — freezing forever leaves you stuck.
    internal static class EatingInteractionController
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
            // — the live eatLoopState static A/Bs it).
            public int eatLoopState;
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
            public Vector3 openZoneOffset; public float openZoneRadius = 0.10f;
            public Vector3 takeZoneOffset; public float takeZoneRadius = 0.10f;
            // Eat zone is anchored at the MOUTH (head + mouthLocalOffset), not the eye
            // center, so the radius no longer needs to over-reach downward. (Was 0.23
            // when the anchor sat between the eyes.)
            public Vector3 eatZoneOffset; public float eatZoneRadius = 0.08f;

            public bool HasSpoon => !string.IsNullOrEmpty(spoonName);
            public bool HasCap => !string.IsNullOrEmpty(capName);
        }

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
            PullStart(HandPath(Bottle("5d1b33a686f7742523398398", // Superwater
                pos: V(-0.021f, -0.175f, 0.067f), rot: V(300.7f, 264f, 89.6f),
                capPos: V(-0.118f, -0.048f, -0.006f), capRot: V(276.7f, 118.7f, 315.4f)), SuperwaterOpenPath), 0.31f),
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
            // Noodles (Rolton) — Wrapper, but the packet is ONE skinned mesh: DETACHING
            // pack_cover00 to the left hand stretched the skin between the hands (reported),
            // so the cover is GLUED as the wrapper group instead (rides the pack; the clip
            // animates the rip in place, Pack-cover style — nothing detaches). If the rip
            // stops animating, the rip motion lives ON pack_cover00 itself and the glue
            // stomps it — then revert to cover with the detach disabled another way. Timed:
            // eaten by HOLDING at the mouth for the item's use-time. PullStart 0 pins the
            // scrub to the clip start (the auto-capture over-progresses on this rig — tarka).
            Timed(PullStart(Wrapper("656df4fec921ad01000481a2", "pack_root", "pack_cover00", null, bites: 1,
                barPos: V(-0.115f, -0.069f, -0.03f), barRot: V(338.7f, 345.6f, 257.5f)), 0f)),
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
                openOffset: V(0f, 0f, -0.14f), takeOffset: V(0f, 0f, -0.14f)), IskraOpenPath), IskraReachPath), 0.2f),
            // MRE: same family (mre_CAT > mre_root > mre_galette), its own grip; a bigger
            // meal -> 3 reach->grab->eat rounds.
            PullStart(ReachPath(HandPath(Zones(ReachBag("590c5f0d86f77413997acfab", "mre_CAT", "mre_galette", bites: 3,
                bagPos: V(-0.148f, -0.076f, -0.037f), bagRot: V(49.7f, 92.6f, 272.5f)),
                openOffset: V(0f, 0f, -0.14f), takeOffset: V(0f, 0f, -0.14f)), MreOpenPath), MreReachPath), 0.2f),
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

        // ===== Baked open-hand paths (recon [HANDPATH], 2026-06) =================
        // The vanilla clip's opening-palm pose vs the item root, replayed by the pull
        // (see FoodDef.openHandPath). Packed stride 8: t, px,py,pz, qx,qy,qz,qw.
        // Trailing keys where the vanilla hand LEAVES the item (the tushonka clip's
        // spoon-grab dive, the bottles carrying the cap away) are trimmed — the replay
        // holds the last kept key instead of yanking the hand off the food.
        // PROPERTIES, not readonly fields: C# initializes static fields in DECLARATION
        // order and Defs is declared ABOVE these — as fields they were still null while
        // Defs built itself, so every HandPath silently stored null (observed: all foods
        // fell back to the OpenGrip/freeze latch). A getter evaluates at call time.

        // Tushonka-measured; shared by every can on the saira rig (big cans too — same
        // clip, slightly larger body). Spoon-dive keys (t≈0.80/0.90, 0.5-0.6m off) trimmed.
        private static float[] SairaOpenPath => new float[]
        {
            0.108f, 0.089f, -0.126f, 0.308f, 0.0748f, -0.5211f, -0.3189f, 0.7881f,
            0.207f, -0.08f, -0.131f, 0.079f, 0.3442f, -0.4858f, -0.7011f, 0.3924f,
            0.306f, -0.078f, -0.141f, 0.054f, 0.4403f, -0.4614f, -0.6888f, 0.3448f,
            0.404f, -0.101f, -0.089f, 0.14f, 0.3142f, -0.7651f, -0.3754f, 0.4183f,
            0.503f, -0.115f, -0.024f, 0.151f, 0.223f, -0.8777f, -0.1889f, 0.3799f,
            0.602f, -0.138f, 0.075f, 0.042f, 0.1106f, -0.9928f, -0.0153f, 0.0438f,
            0.7f, -0.184f, 0.052f, 0.071f, 0.232f, -0.9376f, -0.2067f, 0.1562f,
            0.996f, -0.19f, -0.013f, 0.153f, 0.2383f, -0.9308f, -0.2562f, 0.1063f,
        };

        private static float[] SpratsOpenPath => new float[]
        {
            0.163f, 0.119f, -0.12f, 0.281f, 0.0419f, -0.4838f, -0.346f, 0.8028f,
            0.256f, -0.08f, -0.133f, 0.086f, 0.3455f, -0.4833f, -0.7008f, 0.3949f,
            0.349f, -0.082f, -0.133f, 0.082f, 0.3565f, -0.4804f, -0.7003f, 0.3895f,
            0.441f, -0.081f, -0.139f, 0.066f, 0.4303f, -0.4771f, -0.6817f, 0.3499f,
            0.534f, -0.101f, -0.096f, 0.142f, 0.3139f, -0.749f, -0.4091f, 0.4161f,
            0.627f, -0.104f, -0.059f, 0.157f, 0.2692f, -0.818f, -0.2586f, 0.4376f,
            0.72f, -0.137f, 0.021f, 0.123f, 0.1777f, -0.942f, -0.1617f, 0.2343f,
            0.812f, -0.134f, 0.091f, 0.045f, 0.0842f, -0.9957f, 0.0055f, 0.0375f,
            0.905f, -0.169f, 0.043f, 0.082f, 0.2297f, -0.9392f, -0.2042f, 0.1533f,
            0.998f, -0.232f, -0.064f, 0.12f, 0.2581f, -0.8293f, -0.4525f, 0.2023f,
        };

        // L palm peeling the wrapper (vs item_slickers_LOD0).
        private static float[] ChocolateOpenPath => new float[]
        {
            0.048f, 0.194f, 0.232f, 0.168f, 0.1461f, -0.4374f, -0.0836f, 0.8833f,
            0.148f, 0.038f, 0.135f, 0.09f, 0.1159f, -0.479f, 0.2922f, 0.8196f,
            0.247f, 0.019f, 0.128f, 0.091f, 0.019f, -0.6009f, 0.3008f, 0.7404f,
            0.346f, 0.06f, 0.127f, 0.135f, -0.3256f, -0.7773f, -0.0833f, 0.5318f,
            0.446f, 0.125f, 0.061f, 0.252f, -0.3344f, -0.7755f, -0.0628f, 0.5319f,
            0.545f, 0.139f, 0.006f, 0.262f, -0.5182f, -0.7198f, 0.0803f, 0.4548f,
            0.644f, 0.104f, -0.084f, 0.327f, -0.774f, -0.4794f, 0.0702f, 0.4077f,
            0.744f, -0.029f, -0.272f, 0.263f, -0.6738f, -0.4812f, -0.0554f, 0.558f,
            0.843f, 0.108f, -0.154f, 0.387f, -0.5447f, -0.6732f, 0.1442f, 0.4788f,
            0.942f, 0.292f, 0.034f, 0.149f, -0.1875f, -0.6027f, 0.2202f, 0.7437f,
        };

        // L palm opening/tearing the bag (vs bone_upakovka); shared by both croutons.
        private static float[] CroutonOpenPath => new float[]
        {
            0.222f, 0.192f, 0.202f, 0.209f, -0.5399f, 0.0275f, 0.0033f, -0.8413f,
            0.308f, 0.185f, 0.184f, 0.088f, -0.5013f, -0.1179f, -0.0013f, -0.8572f,
            0.394f, 0.132f, 0.083f, 0.086f, -0.7314f, -0.1787f, 0.0162f, -0.6579f,
            0.48f, 0.088f, 0.06f, 0.096f, -0.6901f, -0.2643f, -0.152f, -0.6564f,
            0.566f, 0.087f, 0.051f, 0.098f, -0.6688f, -0.1874f, -0.1236f, -0.7088f,
            0.652f, 0.021f, 0.077f, 0.122f, -0.8215f, -0.0722f, 0.1153f, -0.5538f,
            0.738f, 0.034f, 0.065f, 0.151f, -0.8558f, -0.0687f, 0.1744f, -0.4822f,
            0.824f, 0.142f, -0.03f, 0.174f, -0.2733f, 0.006f, 0.2283f, -0.9344f,
            0.91f, 0.162f, 0.024f, 0.175f, 0.2972f, 0.0682f, 0.3235f, -0.8957f,
            0.996f, 0.198f, 0.036f, 0.142f, 0.4435f, 0.1367f, 0.458f, -0.7582f,
        };

        // L palm popping the drink tab (vs hr_root / tc_root); last key (hand pulling
        // away) trimmed on both.
        private static float[] HotrodOpenPath => new float[]
        {
            0.057f, 0.285f, 0.009f, 0.194f, 0.6403f, -0.034f, -0.3103f, 0.7019f,
            0.161f, 0.156f, -0.067f, 0.2f, 0.3845f, -0.1034f, -0.3646f, 0.8417f,
            0.265f, 0.106f, -0.069f, 0.196f, 0.5662f, -0.2278f, -0.344f, 0.7135f,
            0.37f, 0.097f, -0.075f, 0.182f, 0.5375f, -0.2142f, -0.426f, 0.6955f,
            0.474f, 0.138f, -0.125f, 0.199f, 0.6923f, -0.2699f, -0.2597f, 0.6168f,
            0.578f, 0.151f, -0.062f, 0.158f, 0.4642f, -0.203f, -0.3909f, 0.7684f,
            0.683f, 0.136f, -0.022f, 0.118f, 0.2569f, -0.029f, -0.4107f, 0.8743f,
            0.787f, 0.148f, 0.01f, 0.119f, 0.1635f, -0.0193f, -0.3656f, 0.9161f,
            0.891f, 0.212f, -0.076f, 0.062f, -0.1837f, -0.2365f, -0.4058f, 0.8635f,
        };

        private static float[] SodaOpenPath => new float[]
        {
            0.053f, 0.295f, 0.016f, 0.18f, 0.6567f, -0.058f, -0.312f, 0.6841f,
            0.158f, 0.16f, -0.069f, 0.163f, 0.3839f, -0.0908f, -0.3635f, 0.844f,
            0.263f, 0.106f, -0.071f, 0.162f, 0.5687f, -0.2257f, -0.3415f, 0.7134f,
            0.368f, 0.095f, -0.078f, 0.149f, 0.528f, -0.2132f, -0.4321f, 0.6994f,
            0.473f, 0.138f, -0.124f, 0.164f, 0.7035f, -0.2575f, -0.2563f, 0.6109f,
            0.578f, 0.15f, -0.072f, 0.135f, 0.4842f, -0.2178f, -0.3819f, 0.7565f,
            0.683f, 0.135f, -0.028f, 0.086f, 0.2607f, -0.0295f, -0.4244f, 0.8666f,
            0.788f, 0.149f, 0.003f, 0.088f, 0.1721f, -0.0233f, -0.3492f, 0.9208f,
            0.893f, 0.216f, -0.063f, 0.016f, -0.1749f, -0.1509f, -0.3677f, 0.9008f,
        };

        // R palm unscrewing/working the cap (vs mod_item) — the carry-the-cap-away tails
        // (hand 0.4-0.65m off the bottle) trimmed on all of these.
        private static float[] VodkaOpenPath => new float[]
        {
            0.218f, 0.24f, 0.018f, 0.251f, -0.6847f, 0.0075f, -0.541f, -0.4884f,
            0.304f, 0.152f, 0.04f, 0.129f, -0.7882f, -0.246f, -0.2503f, -0.5056f,
            0.391f, 0.107f, 0.052f, 0.132f, -0.6865f, -0.2996f, -0.2722f, -0.604f,
            0.478f, 0.105f, 0.069f, 0.13f, -0.6607f, -0.3761f, -0.2987f, -0.577f,
            0.565f, 0.113f, 0.062f, 0.124f, -0.6599f, -0.3561f, -0.2468f, -0.6139f,
            0.652f, 0.113f, 0.024f, 0.164f, -0.6865f, -0.157f, -0.2666f, -0.658f,
            0.739f, 0.282f, 0.131f, 0.19f, -0.6458f, -0.5687f, -0.261f, -0.4375f,
        };

        private static float[] MoonshineOpenPath => new float[]
        {
            0.178f, 0.236f, -0.028f, 0.245f, -0.68f, 0.045f, -0.5004f, -0.534f,
            0.269f, 0.133f, 0.044f, 0.106f, -0.7916f, -0.3183f, -0.2112f, -0.4769f,
            0.361f, 0.11f, 0.036f, 0.154f, -0.6852f, -0.1822f, -0.2636f, -0.6541f,
            0.452f, 0.133f, 0.055f, 0.156f, -0.7675f, -0.2996f, -0.2726f, -0.4969f,
            0.543f, 0.172f, 0.013f, 0.2f, -0.6431f, -0.2849f, -0.4473f, -0.5524f,
        };

        private static float[] WhiskeyOpenPath => new float[]
        {
            0.373f, 0.237f, -0.013f, -0.256f, -0.8972f, -0.0758f, 0.0522f, -0.4318f,
            0.442f, 0.127f, 0.044f, 0.097f, -0.7754f, -0.3453f, -0.1988f, -0.4899f,
            0.512f, 0.11f, 0.034f, 0.156f, -0.6928f, -0.1796f, -0.2611f, -0.6478f,
            0.581f, 0.104f, 0.061f, 0.14f, -0.6774f, -0.3291f, -0.3038f, -0.5835f,
            0.651f, 0.109f, 0.058f, 0.127f, -0.6694f, -0.3539f, -0.2555f, -0.6011f,
            0.72f, 0.111f, 0.02f, 0.169f, -0.6867f, -0.1522f, -0.2747f, -0.6556f,
            0.79f, 0.315f, 0.141f, 0.17f, -0.6466f, -0.5759f, -0.2545f, -0.4306f,
        };

        private static float[] WaterBottleOpenPath => new float[]
        {
            0.201f, 0.186f, -0.009f, 0.291f, -0.6413f, -0.2242f, -0.3054f, -0.6673f,
            0.29f, 0.065f, 0.105f, 0.22f, -0.5347f, -0.3472f, -0.5066f, -0.5805f,
            0.378f, 0.038f, 0.114f, 0.219f, -0.4849f, -0.4145f, -0.5785f, -0.5084f,
            0.466f, 0.029f, 0.118f, 0.219f, -0.466f, -0.4371f, -0.6004f, -0.4809f,
            0.555f, 0.04f, 0.115f, 0.222f, -0.4855f, -0.4146f, -0.5779f, -0.5084f,
            0.643f, 0.029f, 0.118f, 0.228f, -0.4658f, -0.4379f, -0.6006f, -0.4802f,
            0.731f, 0.035f, 0.116f, 0.232f, -0.4739f, -0.4283f, -0.592f, -0.4915f,
            0.82f, 0.094f, 0.107f, 0.236f, -0.6747f, -0.4626f, -0.4458f, -0.3633f,
        };

        private static float[] KvasOpenPath => new float[]
        {
            0.2f, 0.186f, -0.008f, 0.292f, -0.6398f, -0.2413f, -0.3052f, -0.6628f,
            0.289f, 0.067f, 0.104f, 0.221f, -0.5361f, -0.3469f, -0.501f, -0.5841f,
            0.377f, 0.038f, 0.114f, 0.22f, -0.4793f, -0.4207f, -0.5812f, -0.5055f,
            0.466f, 0.03f, 0.117f, 0.22f, -0.4623f, -0.4403f, -0.6003f, -0.4817f,
            0.555f, 0.041f, 0.114f, 0.223f, -0.4812f, -0.4184f, -0.5784f, -0.5087f,
            0.643f, 0.029f, 0.118f, 0.228f, -0.4599f, -0.4422f, -0.6029f, -0.479f,
            0.732f, 0.037f, 0.115f, 0.233f, -0.472f, -0.4285f, -0.5905f, -0.4948f,
            0.821f, 0.097f, 0.106f, 0.236f, -0.6784f, -0.4677f, -0.4439f, -0.352f,
        };

        // R palm flipping the sport cap (vs fb_root); hand-drifts-off tail trimmed.
        private static float[] AquamariOpenPath => new float[]
        {
            0.242f, 0.226f, -0.201f, 0.167f, 0.9498f, -0.2468f, 0.1514f, 0.1186f,
            0.326f, 0.079f, -0.156f, 0.145f, 0.9317f, -0.3218f, -0.0807f, 0.1477f,
            0.409f, 0.079f, -0.139f, 0.151f, 0.938f, -0.2968f, -0.0855f, 0.1574f,
            0.493f, 0.098f, -0.095f, 0.125f, 0.9741f, -0.1021f, -0.1485f, 0.1362f,
            0.576f, 0.109f, -0.107f, 0.126f, 0.9552f, -0.0344f, -0.1888f, 0.2252f,
            0.66f, 0.136f, -0.177f, 0.143f, 0.9545f, -0.1632f, -0.1093f, 0.2242f,
            0.743f, 0.167f, -0.257f, 0.167f, 0.9118f, -0.3428f, 0.0694f, 0.2154f,
        };

        // R palm rolling the can key (vs saira_root) — the condensed milk uses the can
        // clip; its spoon-dive tail trimmed like SairaOpenPath.
        private static float[] CondMilkOpenPath => new float[]
        {
            0.109f, 0.092f, -0.122f, 0.315f, 0.072f, -0.5104f, -0.3096f, 0.799f,
            0.208f, -0.083f, -0.133f, 0.08f, 0.3497f, -0.4819f, -0.7002f, 0.3939f,
            0.307f, -0.082f, -0.14f, 0.059f, 0.4361f, -0.4569f, -0.6916f, 0.3502f,
            0.406f, -0.103f, -0.095f, 0.141f, 0.3133f, -0.7421f, -0.4209f, 0.4171f,
            0.505f, -0.108f, -0.034f, 0.162f, 0.2255f, -0.8451f, -0.2166f, 0.4336f,
            0.604f, -0.136f, 0.078f, 0.047f, 0.0993f, -0.9934f, -0.0391f, 0.0414f,
            0.703f, -0.169f, 0.067f, 0.073f, 0.1908f, -0.9608f, -0.1119f, 0.1671f,
        };

        // Vanilla tears the pouch with the R hand while WE hold it right (mirror food) —
        // the path is item-relative so it may still read fine; judge in the headset.
        private static float[] RationOpenPath => new float[]
        {
            0.18f, -0.285f, 0.141f, 0.142f, 0.0085f, -0.7366f, 0.3071f, 0.6026f,
            0.271f, -0.162f, 0.058f, 0.168f, 0.31f, -0.6766f, 0.424f, 0.516f,
            0.362f, -0.108f, 0.017f, 0.136f, 0.4596f, -0.5766f, 0.4921f, 0.4628f,
            0.453f, -0.112f, 0.012f, 0.138f, 0.4594f, -0.5829f, 0.5104f, 0.4344f,
            0.544f, -0.102f, 0.024f, 0.13f, 0.4368f, -0.5473f, 0.4788f, 0.5295f,
            0.635f, -0.095f, 0.041f, 0.165f, 0.3454f, -0.5047f, 0.505f, 0.6091f,
            0.727f, -0.124f, 0.053f, 0.156f, 0.216f, -0.3195f, 0.7806f, 0.4919f,
            0.818f, -0.125f, -0.038f, 0.081f, 0.2215f, -0.2313f, 0.9297f, 0.182f,
            0.909f, -0.109f, -0.055f, 0.079f, 0.271f, -0.2348f, 0.9242f, 0.1317f,
            1f, -0.108f, -0.054f, 0.08f, 0.2756f, -0.2483f, 0.9169f, 0.1476f,
        };

        private static float[] SuperwaterOpenPath => new float[]
        {
            0.222f, 0.117f, -0.107f, 0.305f, -0.6832f, 0.1958f, -0.2122f, -0.6707f,
            0.309f, 0.107f, -0.137f, 0.226f, -0.6145f, 0.1914f, 0.1041f, -0.7582f,
            0.395f, 0.119f, -0.126f, 0.197f, -0.643f, 0.0721f, 0.1011f, -0.7558f,
            0.481f, 0.094f, -0.132f, 0.276f, -0.4772f, 0.3163f, 0.1374f, -0.8083f,
            0.568f, 0.122f, -0.116f, 0.187f, -0.6916f, -0.0105f, 0.0782f, -0.7179f,
            0.654f, 0.104f, -0.137f, 0.29f, -0.473f, 0.3186f, 0.1561f, -0.8065f,
            0.74f, 0.195f, -0.283f, 0.145f, -0.9606f, -0.0589f, -0.1134f, -0.2469f,
            0.827f, 0.08f, -0.279f, -0.047f, -0.8572f, 0.1521f, -0.488f, 0.0629f,
            0.913f, -0.015f, -0.237f, -0.061f, -0.7347f, 0.0794f, -0.6678f, 0.0888f,
            0.999f, -0.014f, -0.238f, -0.062f, -0.7352f, 0.0791f, -0.6673f, 0.0895f,
        };

        // ===== 2026-06-13 batch (the "logs for food" recon run) =====
        // Tetrapak corner tear (milk-measured; shared by both cartons). Last two keys
        // (the torn nose carried away, z 0.19->0.41) trimmed.
        private static float[] TetraPakOpenPath => new float[]
        {
            0.077f, -0.232f, 0.081f, 0.242f, -0.6145f, 0.4164f, -0.3035f, -0.5974f,
            0.18f, -0.176f, -0.042f, 0.198f, -0.739f, 0.361f, -0.5285f, -0.2107f,
            0.282f, -0.184f, -0.047f, 0.199f, -0.7243f, 0.3787f, -0.5508f, -0.1688f,
            0.384f, -0.184f, 0.056f, 0.141f, -0.3434f, 0.5719f, -0.2926f, -0.6851f,
            0.487f, -0.137f, 0.038f, 0.158f, -0.4092f, 0.5557f, -0.0899f, -0.7181f,
            0.589f, -0.11f, -0.033f, 0.195f, -0.596f, 0.5527f, -0.1148f, -0.5711f,
            0.691f, -0.093f, -0.025f, 0.19f, -0.6346f, 0.516f, -0.1116f, -0.5644f,
            0.794f, -0.075f, -0.04f, 0.193f, -0.609f, 0.5491f, -0.1059f, -0.5625f,
        };

        // Juice screw cap (vita-measured; apple/vita/grand share the rig). Last three keys
        // (the unscrewed cap carried aside) trimmed.
        private static float[] JuiceOpenPath => new float[]
        {
            0.085f, 0.279f, -0.045f, 0.196f, 0.5257f, -0.4313f, -0.1751f, 0.712f,
            0.186f, 0.17f, -0.088f, 0.271f, 0.7581f, -0.1207f, -0.2623f, 0.5847f,
            0.287f, 0.143f, -0.065f, 0.255f, 0.8491f, -0.0529f, -0.2f, 0.486f,
            0.388f, 0.131f, -0.074f, 0.254f, 0.8441f, -0.0702f, -0.2269f, 0.4808f,
            0.489f, 0.143f, -0.066f, 0.251f, 0.8516f, -0.0501f, -0.1982f, 0.4826f,
            0.59f, 0.124f, -0.082f, 0.238f, 0.8285f, -0.0824f, -0.292f, 0.4707f,
            0.691f, 0.117f, -0.115f, 0.241f, 0.6618f, -0.0707f, -0.4281f, 0.6114f,
        };

        // Galette pack open (modest 0.4m travel, no runaway — all keys kept).
        private static float[] GaletteOpenPath => new float[]
        {
            0f, 0.15f, 0.235f, 0.037f, -0.0357f, 0.273f, -0.008f, -0.9613f,
            0.111f, 0.118f, 0.199f, 0.023f, -0.0681f, 0.1828f, -0.1126f, -0.9743f,
            0.221f, 0.056f, 0.139f, -0.007f, -0.142f, 0.0013f, -0.2463f, -0.9587f,
            0.331f, 0.044f, 0.127f, -0.013f, -0.1594f, -0.0365f, -0.2658f, -0.9501f,
            0.442f, 0.045f, 0.126f, -0.011f, -0.1593f, -0.0361f, -0.2664f, -0.9499f,
            0.552f, 0.042f, 0.121f, 0.063f, -0.1606f, 0.4224f, -0.2418f, -0.8587f,
            0.663f, 0.05f, 0.166f, 0.099f, 0.0579f, 0.7087f, -0.2307f, -0.6642f,
            0.773f, 0.046f, 0.164f, 0.092f, 0.0734f, 0.6979f, -0.2355f, -0.6724f,
            0.884f, 0.028f, 0.157f, 0.075f, 0.1122f, 0.7042f, -0.2467f, -0.6562f,
            0.994f, 0.014f, 0.162f, 0.052f, 0.1543f, 0.6506f, -0.3626f, -0.6492f,
        };

        // Oat flakes box tear (croutons-like; tail toss kept like CroutonOpenPath).
        private static float[] OatmealOpenPath => new float[]
        {
            0.321f, 0.334f, 0.029f, 0.087f, -0.4929f, -0.0535f, -0.0083f, -0.8684f,
            0.396f, 0.251f, 0.01f, 0.073f, -0.8088f, -0.161f, 0.0994f, -0.5568f,
            0.471f, 0.142f, -0.016f, 0.106f, -0.9224f, -0.1021f, -0.0112f, -0.3724f,
            0.546f, 0.123f, -0.02f, 0.118f, -0.9498f, -0.1259f, -0.095f, -0.2703f,
            0.621f, 0.129f, -0.028f, 0.125f, -0.858f, 0.0094f, 0.0525f, -0.5108f,
            0.696f, 0.123f, 0f, 0.105f, -0.6136f, 0.0873f, 0.1116f, -0.7768f,
            0.771f, 0.136f, 0.01f, 0.099f, -0.3726f, 0.0164f, 0.1611f, -0.9137f,
            0.846f, 0.181f, 0.014f, 0.111f, -0.0124f, 0.0204f, 0.1391f, -0.99f,
            0.921f, 0.183f, 0.077f, 0.116f, 0.2897f, 0.0785f, 0.1828f, -0.9362f,
            0.996f, 0.191f, 0.077f, 0.135f, 0.4357f, 0.1245f, 0.3458f, -0.8216f,
        };

        // Alyonka wrapper peel (slickers-family clip; all keys kept like ChocolateOpenPath).
        private static float[] AlyonkaOpenPath => new float[]
        {
            0.054f, 0.247f, -0.193f, 0.139f, 0.1952f, 0.3774f, 0.7166f, -0.5531f,
            0.156f, 0.169f, -0.033f, 0.09f, 0.2113f, 0.4007f, 0.3818f, -0.8056f,
            0.257f, 0.173f, -0.028f, 0.085f, 0.364f, 0.3344f, 0.3708f, -0.7863f,
            0.358f, 0.285f, -0.125f, 0.124f, 0.7817f, 0.1533f, 0.1869f, -0.5749f,
            0.46f, 0.203f, -0.184f, 0.28f, 0.4437f, 0.4424f, 0.2148f, -0.7492f,
            0.561f, 0.151f, -0.184f, 0.283f, 0.571f, 0.3363f, 0.1398f, -0.7357f,
            0.662f, 0.162f, -0.177f, 0.359f, 0.6862f, 0.2319f, 0.1079f, -0.681f,
            0.764f, 0.1f, -0.11f, 0.481f, 0.7252f, 0.3859f, 0.1827f, -0.5402f,
            0.865f, -0.04f, -0.118f, 0.518f, 0.6288f, 0.5487f, 0.2517f, -0.4902f,
            0.966f, 0.115f, -0.35f, 0.238f, 0.1885f, 0.6532f, 0.4817f, -0.5529f,
        };

        // Sugar box open (RIGHT hand works the lid; no runaway — all keys kept).
        private static float[] SugarOpenPath => new float[]
        {
            0.193f, 0.31f, -0.066f, 0.136f, 0.4721f, 0.1612f, 0.6328f, -0.5921f,
            0.283f, 0.178f, 0.073f, 0.161f, 0.4914f, 0.4879f, 0.4751f, -0.5428f,
            0.372f, 0.124f, 0.077f, 0.144f, 0.6055f, 0.5621f, 0.4264f, -0.3684f,
            0.462f, 0.154f, 0.069f, 0.132f, 0.7619f, 0.4675f, 0.3903f, -0.2205f,
            0.551f, 0.179f, 0.06f, 0.129f, 0.808f, 0.3877f, 0.3985f, -0.1949f,
            0.641f, 0.175f, 0.04f, 0.132f, 0.7755f, 0.3881f, 0.4047f, -0.2901f,
            0.731f, 0.169f, -0.012f, 0.079f, 0.722f, 0.495f, 0.3439f, -0.3397f,
            0.82f, 0.175f, 0.019f, 0.12f, 0.6356f, 0.5885f, 0.3717f, -0.334f,
            0.91f, 0.141f, 0.001f, 0.196f, 0.6537f, 0.478f, 0.497f, -0.3116f,
            1f, 0.102f, -0.042f, 0.198f, 0.75f, 0.1894f, 0.583f, -0.2486f,
        };

        // Iskra strap rip (L palm vs IFR_CAT). Last two keys (the torn strip carried up and
        // away, y -> 0.27) trimmed; the pull only samples to openReadyTime 0.6 anyway.
        private static float[] IskraOpenPath => new float[]
        {
            0f, 0.136f, 0.185f, 0.073f, -0.1181f, -0.4629f, -0.1456f, 0.8664f,
            0.111f, 0.095f, 0.185f, 0.06f, -0.0201f, -0.3673f, -0.0196f, 0.9297f,
            0.221f, 0.028f, 0.187f, 0.04f, 0.1623f, -0.1774f, 0.1098f, 0.9644f,
            0.332f, 0.02f, 0.185f, 0.038f, 0.1917f, -0.1523f, 0.1228f, 0.9618f,
            0.442f, 0.017f, 0.184f, 0.049f, 0.1505f, -0.2842f, 0.1278f, 0.9382f,
            0.553f, 0.01f, 0.173f, 0.017f, -0.1826f, -0.7203f, 0.143f, 0.6537f,
            0.663f, 0.08f, 0.173f, -0.017f, -0.483f, -0.8628f, 0.074f, 0.1297f,
            0.774f, 0.14f, 0.208f, 0.02f, -0.5626f, -0.7761f, 0.0496f, 0.2805f,
        };

        // MRE strap rip (L palm vs mre_CAT). Same family/treatment as IskraOpenPath.
        private static float[] MreOpenPath => new float[]
        {
            0f, 0.197f, 0.161f, 0.07f, -0.1479f, -0.4542f, -0.2016f, 0.8551f,
            0.111f, 0.145f, 0.166f, 0.049f, -0.1112f, -0.3557f, -0.1024f, 0.9223f,
            0.222f, 0.065f, 0.176f, 0.014f, -0.0199f, -0.1825f, 0.0164f, 0.9829f,
            0.333f, 0.06f, 0.175f, 0.01f, -0.0018f, -0.1631f, 0.0313f, 0.9861f,
            0.444f, 0.055f, 0.176f, 0.024f, 0.0028f, -0.2451f, 0.0344f, 0.9689f,
            0.556f, 0.036f, 0.188f, 0.026f, -0.2271f, -0.7397f, 0.0989f, 0.6257f,
            0.667f, 0.141f, 0.158f, -0.017f, -0.5333f, -0.8276f, 0.0859f, 0.1526f,
            0.778f, 0.206f, 0.187f, 0.019f, -0.6106f, -0.7392f, 0.0314f, 0.2826f,
        };

        // Iskra reach-in (L palm vs IFR_CAT, STATE_USE time axis). Headset-verified layout:
        // low t = hand OUTSIDE at the bag mouth (Take@0.014 is just the food-appear event),
        // deep IN the bag ≈ t 0.23-0.34, mid keys 0.45-0.89 are the mouth trip. The scrub
        // samples t between reachStartTime and reachDeepTime (0.02 -> 0.3); the FULL pass
        // is kept so the segment can be retuned anywhere on the clip without a recapture.
        private static float[] IskraReachPath => new float[]
        {
            0.007f, -0.017f, 0.26f, 0.105f, -0.4258f, -0.7702f, 0.1603f, 0.4469f,
            0.117f, -0.031f, 0.218f, 0.054f, -0.4493f, -0.7943f, 0.2474f, 0.3256f,
            0.227f, -0.049f, 0.215f, 0.025f, -0.4735f, -0.7859f, 0.3239f, 0.2309f,
            0.337f, -0.045f, 0.224f, 0.012f, -0.4681f, -0.7625f, 0.383f, 0.2299f,
            0.447f, -0.013f, 0.244f, 0.064f, -0.4994f, -0.8095f, 0.1742f, 0.2548f,
            0.557f, -0.008f, 0.262f, 0.104f, -0.3197f, -0.7113f, 0.1644f, 0.604f,
            0.667f, -0.033f, 0.288f, 0.066f, -0.3575f, -0.3622f, -0.0298f, 0.8603f,
            0.777f, -0.088f, 0.221f, 0.088f, -0.6112f, -0.2928f, -0.2888f, 0.6763f,
            0.887f, -0.102f, 0.277f, 0.068f, -0.4994f, -0.5899f, -0.1266f, 0.6218f,
            0.997f, -0.018f, 0.249f, 0.088f, -0.4226f, -0.7793f, 0.1716f, 0.4297f,
        };

        // MRE reach-in (L palm vs mre_CAT). Same family/treatment as IskraReachPath.
        private static float[] MreReachPath => new float[]
        {
            0.005f, 0.026f, 0.264f, 0.095f, -0.5188f, -0.6976f, 0.1217f, 0.479f,
            0.116f, 0.007f, 0.232f, 0.049f, -0.4953f, -0.759f, 0.2237f, 0.3586f,
            0.226f, -0.002f, 0.236f, 0.028f, -0.5093f, -0.7265f, 0.3052f, 0.3459f,
            0.336f, -0.018f, 0.23f, 0.006f, -0.4317f, -0.7205f, 0.425f, 0.3375f,
            0.446f, -0.003f, 0.245f, 0.031f, -0.5513f, -0.7762f, 0.157f, 0.2624f,
            0.556f, 0.014f, 0.267f, 0.09f, -0.36f, -0.6877f, 0.1292f, 0.6171f,
            0.667f, 0.033f, 0.292f, 0.071f, -0.3776f, -0.3393f, -0.0835f, 0.8575f,
            0.777f, -0.02f, 0.225f, 0.084f, -0.6249f, -0.2563f, -0.329f, 0.6599f,
            0.887f, -0.027f, 0.284f, 0.066f, -0.534f, -0.5578f, -0.1661f, 0.6133f,
            0.997f, 0.053f, 0.258f, 0.105f, -0.4556f, -0.7503f, 0.1064f, 0.4671f,
        };

        // Mayo screw cap. Last four keys (the cap carried aside, x 0.1->0.28) trimmed.
        private static float[] MayoOpenPath => new float[]
        {
            0.105f, 0.307f, 0f, 0.141f, -0.5126f, 0.1638f, 0.375f, -0.7549f,
            0.204f, 0.129f, -0.016f, 0.153f, -0.6818f, 0.0912f, 0.1634f, -0.7072f,
            0.303f, 0.124f, -0.006f, 0.166f, -0.6758f, 0.1471f, 0.0938f, -0.7161f,
            0.403f, 0.127f, -0.008f, 0.163f, -0.6757f, 0.1463f, 0.1004f, -0.7155f,
            0.502f, 0.103f, -0.073f, 0.164f, -0.6298f, 0.3263f, 0.2656f, -0.6529f,
            0.601f, 0.096f, -0.069f, 0.196f, -0.4871f, 0.3587f, 0.3016f, -0.7369f,
        };

        // Tarka dried-meat pack rip. Last two keys (the cover strip yanked away, y -> 0.45)
        // trimmed.
        private static float[] TarkaOpenPath => new float[]
        {
            0.004f, -0.069f, 0.069f, 0.146f, 0.2624f, 0.5207f, 0.7354f, 0.3453f,
            0.114f, -0.163f, 0.136f, 0.11f, 0.1757f, 0.6747f, 0.6381f, 0.3266f,
            0.225f, -0.243f, 0.11f, 0.007f, 0.1054f, 0.2185f, 0.8187f, 0.5205f,
            0.335f, -0.097f, 0.103f, -0.083f, -0.1335f, -0.7138f, 0.441f, 0.5274f,
            0.446f, -0.072f, 0.082f, -0.044f, -0.078f, -0.7701f, 0.31f, 0.552f,
            0.556f, -0.071f, 0.08f, -0.043f, -0.0687f, -0.7694f, 0.2998f, 0.5598f,
            0.667f, -0.066f, 0.08f, -0.052f, -0.1249f, -0.7583f, 0.3403f, 0.5418f,
            0.777f, -0.072f, 0.083f, -0.05f, -0.1147f, -0.7646f, 0.347f, 0.5309f,
        };

        // Salad box lid peel (saira-family clip): the 0.80/0.90 spoon-dive keys trimmed,
        // final settle key kept — exactly the SairaOpenPath treatment.
        private static float[] SaladOpenPath => new float[]
        {
            0.113f, 0.214f, -0.149f, 0.209f, -0.0371f, -0.4195f, -0.4625f, 0.7802f,
            0.211f, -0.012f, -0.2f, 0.073f, 0.3643f, -0.3943f, -0.7538f, 0.3789f,
            0.31f, -0.018f, -0.149f, 0.15f, 0.1666f, -0.561f, -0.672f, 0.4538f,
            0.408f, -0.063f, -0.131f, 0.121f, 0.1082f, -0.397f, -0.878f, 0.2446f,
            0.507f, -0.098f, 0.134f, 0.191f, 0.1339f, -0.8938f, -0.3749f, 0.2066f,
            0.605f, -0.171f, 0.039f, 0.152f, 0.2101f, -0.6787f, -0.7037f, 0.0077f,
            0.704f, -0.3f, -0.046f, 0.038f, 0.2729f, -0.7202f, -0.5934f, 0.2339f,
            0.999f, -0.148f, -0.093f, 0.12f, 0.6261f, -0.6873f, -0.3658f, 0.0435f,
        };

        // Beer cap pop. Last two keys (the cap carried away, x/z grow) trimmed.
        private static float[] BeerOpenPath => new float[]
        {
            0.352f, 0.252f, 0.047f, 0.232f, -0.681f, 0.1064f, -0.4073f, -0.5992f,
            0.424f, 0.174f, 0.043f, 0.147f, -0.743f, -0.0957f, -0.2885f, -0.5963f,
            0.496f, 0.12f, 0.053f, 0.11f, -0.7453f, -0.346f, -0.2126f, -0.5287f,
            0.568f, 0.103f, 0.059f, 0.12f, -0.6815f, -0.3397f, -0.2656f, -0.5913f,
            0.639f, 0.111f, 0.03f, 0.158f, -0.6883f, -0.1608f, -0.2626f, -0.6569f,
            0.711f, 0.111f, 0.02f, 0.17f, -0.6963f, -0.1438f, -0.2823f, -0.6441f,
            0.783f, 0.184f, -0.015f, 0.16f, -0.7345f, -0.1317f, -0.3404f, -0.5721f,
            0.855f, 0.27f, 0.027f, 0.111f, -0.7542f, -0.0263f, -0.4957f, -0.4298f,
        };

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
            string eatSound = "Eat") =>
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
        private static FoodDef Timed(FoodDef d) { d.eatByTime = true; return d; }

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
            Plugin.MyLog.LogInfo("[ManualEat] === paste into Defs (tuned) ===\n            " + WrapPullEnd(WrapPullStart(WrapOpenWith(WrapReachPath(WrapHandPath(WrapOpenGrip(WrapZones(core))))))) + ",");
        }

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
            if (openDistance != 0.18f) sb.Append($", openRadius: {FStr(openDistance)}");
            if (takeZoneOffset != Vector3.zero) sb.Append($", takeOffset: {VStr(takeZoneOffset)}");
            if (scoopDistance != 0.18f) sb.Append($", takeRadius: {FStr(scoopDistance)}");
            if (eatZoneOffset != Vector3.zero) sb.Append($", eatOffset: {VStr(eatZoneOffset)}");
            if (eatDistance != 0.18f) sb.Append($", eatRadius: {FStr(eatDistance)}");
            return sb.ToString();
        }

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
        // Timed-eat loop state, seeded from FoodDef.eatLoopState each spawn (0 = STATE_USE).
        // Live-tunable: sausage's non-standard chew hash is a recon GUESS — paste another
        // hash here in UnityExplorer if its loop doesn't play/sound.
        public static int eatLoopState;
        private static float nextLoopPlay; // DriveDrinkLoop replay throttle (wrong-hash safety)
        // Segmented bites (sausage): hide from the END of the name-sorted renderer list
        // (the tip) first; flip live if it visibly eats from the wrong end.
        public static bool segmentHideFromEnd = true;
        // The cap's rest offset ON the container (captured before any reparenting) so it can
        // sit glued there until opened and be glued back on a recap.
        private static Vector3 capLocalPos; private static Quaternion capLocalRot;
        private static Transform capHandBoneRef; // the free hand's IK bone (cap goes here on open)

        // Used by the UpdateLeftHand prefix to know when to keep the left hand live.
        public static bool ManualActive => active && !manualDone;
        // True while a pull-open has the LEFT (off) hand latched onto the held item — the
        // ForceLeftHandTracking prefix must keep updating the rig target (so release lands
        // on the controller) but must NOT re-point that arm's IK at it, or it yanks the
        // rendered hand off the item back to the controller mid-pull (it runs on the SteamVR
        // pose callback, after our Tick aimed the IK at the latch).
        public static bool OffHandLatched => active && !manualDone && handLatched && !latchedHandIsDominant;

        // ===== Spawn (PREFIX on MedsController.Spawn — before Start()->method_5) =====
        public static void OnSpawnPre(MedsController instance, float animationSpeed)
        {
            try
            {
                Reset();
                if (!VRSettings.GetManualEating()) return;

                EFT.Player player = instance?._player;
                if (player == null || !player.IsYourPlayer) return;
                if (!(instance.Item is FoodDrinkItemClass)) return;

                FoodDef d = FindDef(instance.Item?.TemplateId);
                if (d == null) return; // no manual sequence -> vanilla

                active = true;
                controller = instance;
                def = d;
                openReadyTime = d.openReadyTime; // load per-food timings (still A/B-able live)
                putAwayStartTime = d.putAwayStartTime;
                pullStartTime = d.pullStartTime;
                pullEndTime = d.pullEndTime;
                deterministicGesturePose = d.deterministicGesture;
                takePoseTime = d.takePoseTime;
                bitePoseTime = d.bitePoseTime;
                reachStartTime = d.reachStartTime; // ReachBag reach segment (harmless otherwise)
                reachDeepTime = d.reachDeepTime;
                eatLoopState = d.eatLoopState;     // timed-eat/gulp loop state (0 = STATE_USE)
                takePoseState = STATE_USE_HASH;  // segments are shared; reset in case A/B'd
                bitePoseState = STATE_EAT_HASH;
                // Interaction-zone reach points (radii into the existing distance statics,
                // offsets into the new ones) — same A/B-able-live seeding as the timings above.
                openDistance = d.openZoneRadius; scoopDistance = d.takeZoneRadius; eatDistance = d.eatZoneRadius;
                openZoneOffset = d.openZoneOffset; takeZoneOffset = d.takeZoneOffset; eatZoneOffset = d.eatZoneOffset;
                style = BuildStyle(d); // per-archetype gesture descriptor
                phase = d.skipOpen ? Phase.Ready : Phase.Closed; // skipOpen = straight to "bring to mouth"
                biteCount = 0;
                spawnAnimSpeed = animationSpeed <= 0f ? 1f : animationSpeed;
                ReadDrinkState(instance.Item); // resource left + full-item use-time (drinks/partials)

                Plugin.MyLog.LogInfo($"[ManualEat] Armed manual sequence for {d.templateId} "
                    + $"(openGrip={(string.IsNullOrEmpty(d.openGripName) ? "none" : d.openGripName)}, "
                    + $"handPath={(d.openHandPath != null ? d.openHandPath.Length / 8 + " keys" : "none")}, "
                    + $"reachPath={(d.reachHandPath != null ? d.reachHandPath.Length / 8 + " keys" : "none")}).");
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[ManualEat] OnSpawnPre error: {ex}");
                Reset();
            }
        }

        // Snapshot the item's resource + timing. HpPercent is the CURRENT amount in resource
        // units (NOT 0-100; starts at MaxResource), so remaining = HpPercent/MaxResource — a
        // half-drunk bottle only has half a use-time left to drink. UseTime comes off the
        // item template ("foodUseTime") unless the FoodDef overrides it.
        private static void ReadDrinkState(EFT.InventoryLogic.Item item)
        {
            drinkUseTime = def.drinkSeconds;
            drinkRemainingFrac = 1f;
            drinkHeldTime = 0f;
            drinkingHeld = false;
            drinkAppliedFrac = 0f;
            drinkTimeAppliedFrac = 0f;
            drinkFdc = null;
            try
            {
                drinkFdc = item?.GetItemComponent<EFT.InventoryLogic.FoodDrinkComponent>();
                if (drinkFdc != null && drinkFdc.MaxResource > 0f)
                    drinkRemainingFrac = Mathf.Clamp01(drinkFdc.HpPercent / drinkFdc.MaxResource);
                if (drinkUseTime <= 0f && item?.Template is FoodDrinkTemplateClass t)
                    drinkUseTime = t.UseTime;
            }
            catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] ReadDrinkState: {ex.Message}"); }
            if (drinkUseTime <= 0f) drinkUseTime = 5f; // template missing/odd — sane fallback
            if (def.kind == FoodKind.Drink)
                Plugin.MyLog.LogInfo(drinkFdc != null
                    ? $"[ManualEat] Drink: {drinkFdc.HpPercent:F0}/{drinkFdc.MaxResource:F0} left ({drinkRemainingFrac:P0}), {drinkUseTime * drinkRemainingFrac:F1}s at the mouth to finish."
                    : "[ManualEat] Drink has NO FoodDrinkComponent — the bar can't tick down; the finish falls back to the use-time clock.");
        }

        // POSTFIX on Spawn: props are built now — find them, freeze, REPARENT to hands.
        public static void OnSpawnPost(MedsController instance)
        {
            if (!active || instance != controller) return;
            try
            {
                instance.FirearmsAnimator?.SetAnimationSpeed(0f);

                Transform root = instance._controllerObject != null ? instance._controllerObject.transform : null;
                if (root == null) { BailToVanilla("no controller object"); return; }
                medsBody = root; // rig body anchor for the per-frame camRoot/rotation pins

                soundPlayer = instance._controllerObject.GetComponentInChildren<BaseSoundPlayer>(true);

                // Parent to the IK'd hand bones (solver.bone3): they follow your controllers (the
                // animation-rig palms don't) and share the palm orientation, so the grip lands right.
                Transform leftHandBone = VRGlobals.ikManager?.leftArmIk?.solver?.bone3?.transform;
                Transform rightHandBone = VRGlobals.ikManager?.rightArmIk?.solver?.bone3?.transform;
                if (leftHandBone == null || rightHandBone == null)
                {
                    Plugin.MyLog.LogError("[ManualEat] No IK hand bones — vanilla fallback.");
                    Reset();
                    return;
                }

                // DIAGNOSTIC: route props to the rig hand targets instead of the IK bones.
                if (debugParentToTarget)
                {
                    leftHandBone = VRGlobals.vrPlayer.LeftHand.transform;
                    rightHandBone = VRGlobals.vrPlayer.RightHand.transform;
                    Plugin.MyLog.LogInfo("[ManualEat] DIAG: props parented to IK TARGETS (RightHand/LeftHand).");
                }
                leftHandBoneRef = leftHandBone; // for the handheld wrapper's left-hand holder (built on open)

                bool ok = def.kind == FoodKind.Handheld ? SetupHandheldProps(root, rightHandBone)
                        : def.kind == FoodKind.Drink ? SetupDrinkProps(root, leftHandBone, rightHandBone)
                        : def.kind == FoodKind.Bag ? SetupBagProps(root, rightHandBone)
                        // ReachBag's prop wiring IS Pack's: held root on the right palm,
                        // hidden food piece taken into the left hand (no wrapper).
                        : def.kind == FoodKind.Pack || def.kind == FoodKind.ReachBag ? SetupPackProps(root, rightHandBone)
                        : SetupCannedProps(root, leftHandBone, rightHandBone);
                if (!ok) return; // failure already logged + Reset

                // Segmented snack: collect the held item's renderers (name-sorted) so each
                // audible bite can shed one from the tip inward (the sausage shrinks as eaten).
                if (def.segmentedBites && baseT != null)
                {
                    segR = baseT.GetComponentsInChildren<Renderer>(true);
                    Array.Sort(segR, (a, b) => string.CompareOrdinal(a.name, b.name));
                    Plugin.MyLog.LogInfo($"[ManualEat] segmented bites: {segR.Length} renderers under '{baseT.name}'.");
                }

                // The pull-open hand-ride bone (optional). Resolve AFTER the props moved to
                // the hands, and search the HELD ITEM first: these rigs have duplicate node
                // names, and a same-named node left on the body-space rig ALSO animates with
                // the scrub — riding that one parks the hand far off the item while still
                // "following along" (observed in-headset). Only the baseT subtree is in
                // your hand.
                if (!string.IsNullOrEmpty(def.openGripName))
                {
                    openGripT = FindDeep(baseT, def.openGripName);
                    if (openGripT != null)
                        Plugin.MyLog.LogInfo($"[ManualEat] openGrip bone '{def.openGripName}' resolved under the held item.");
                    else
                    {
                        openGripT = FindDeep(root, def.openGripName);
                        Plugin.MyLog.LogWarning(openGripT != null
                            ? $"[ManualEat] openGrip '{def.openGripName}' only exists OUTSIDE the held item — it stays on the body-space rig and will NOT line up with your hand."
                            : $"[ManualEat] openGrip '{def.openGripName}' not found — the pull latch will freeze at the grab pose.");
                    }
                }

                if (driveHandsToTargets) SubscribePinAfterIk();

                Plugin.MyLog.LogInfo($"[ManualEat] Props reparented to hands (kind={def.kind}).");
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[ManualEat] OnSpawnPost error: {ex}");
                End();
            }
        }

        // A def matched but its props aren't in this rig (wrong bone names): bail back to a
        // REAL vanilla eat. By the Spawn postfix the gate already swallowed the Spawn-body
        // method_5 (the consume) and the animator is frozen — just Reset()ing left the food
        // hung with no effect running. Re-fire the stashed op and unfreeze first.
        private static bool BailToVanilla(string why)
        {
            Plugin.MyLog.LogError($"[ManualEat] {why} — vanilla fallback.");
            try
            {
                if (pendingOp != null && !effectFired)
                {
                    effectFired = true;   // AllowEffect passes it through
                    pendingOp.method_5(); // run the real consume the gate swallowed
                }
                controller?.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] BailToVanilla: {ex}"); }
            Reset();
            return false;
        }

        // Canned (tushonka, sprats): can in the LEFT hand; spoon (if any) + food in the RIGHT.
        // Food hangs off the spoon holder (HasSpoon) or the right hand. False+Reset if a prop's
        // missing. Optional extras: a SECOND food piece (sugar's pair of cubes, own holder on
        // the same hand) and a glued SIBLING group (sugar's remaining pile sits NEXT TO the box
        // in the rig — without the glue it stays behind on the body-space rig).
        private static bool SetupCannedProps(Transform root, Transform leftHandBone, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);
            foodT = FindDeep(root, def.foodPieceName);
            spoonT = def.HasSpoon ? FindDeep(root, def.spoonName) : null;
            foodT2 = string.IsNullOrEmpty(def.foodPiece2Name) ? null : FindDeep(root, def.foodPiece2Name);
            wrapperT = string.IsNullOrEmpty(def.wrapperName) ? null : FindDeep(root, def.wrapperName);

            if (baseT != null && foodT == null && spoonT == null && wrapperT == null)
            {
                Save(baseT, out baseParent0, out basePos0, out baseRot0);

                Transform holdBone = def.holdInOffHand ? leftHandBone : rightHandBone;

                baseHolder = NewHolder("EatBaseHolder", holdBone, def.basePos, def.baseRot);
                baseT.SetParent(baseHolder.transform, false);
                reparented = true;
                return true;
            }

            if (baseT == null || foodT == null || (def.HasSpoon && spoonT == null))
                return BailToVanilla($"Missing props (base={baseT != null} spoon={(def.HasSpoon ? (spoonT != null).ToString() : "n/a")} food={foodT != null})");
            if (!string.IsNullOrEmpty(def.foodPiece2Name) && foodT2 == null)
                Plugin.MyLog.LogWarning($"[ManualEat] second piece '{def.foodPiece2Name}' not found — taking one.");

            spoonR = spoonT != null ? spoonT.GetComponentInChildren<Renderer>(true) : null;
            foodR = foodT.GetComponentInChildren<Renderer>(true);
            foodR2 = foodT2 != null ? foodT2.GetComponentInChildren<Renderer>(true) : null;

            // Capture the glued group's rest offset BEFORE anything moves.
            if (wrapperT != null)
            {
                wrapperLocalPos = baseT.InverseTransformPoint(wrapperT.position);
                wrapperLocalRot = Quaternion.Inverse(baseT.rotation) * wrapperT.rotation;
                Save(wrapperT, out wrapperParent0, out wrapperPos0, out wrapperRot0);
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (spoonT != null) Save(spoonT, out spoonParent0, out spoonPos0, out spoonRot0);
            Save(foodT, out foodParent0, out foodPos0, out foodRot0);
            if (foodT2 != null) Save(foodT2, out food2Parent0, out food2Pos0, out food2Rot0);

            baseHolder = NewHolder("EatBaseHolder", leftHandBone, def.basePos, def.baseRot);
            if (def.HasSpoon)
            {
                // Utensil in the right hand; the food piece sits in the utensil bowl.
                spoonHolder = NewHolder("EatSpoonHolder", rightHandBone, def.spoonPos, def.spoonRot);
                foodHolder = NewHolder("EatFoodHolder", spoonHolder.transform, def.foodPos, def.foodRot);
            }
            else
            {
                // No utensil: the food piece is grabbed directly by the right hand.
                spoonHolder = null;
                foodHolder = NewHolder("EatFoodHolder", rightHandBone, def.foodPos, def.foodRot);
            }
            foodHolder2 = foodT2 != null
                ? NewHolder("EatFood2Holder", def.HasSpoon ? spoonHolder.transform : rightHandBone, def.food2Pos, def.food2Rot)
                : null;

            baseT.SetParent(baseHolder.transform, false);
            if (spoonT != null) spoonT.SetParent(spoonHolder.transform, false);
            foodT.SetParent(foodHolder.transform, false);
            if (foodT2 != null) foodT2.SetParent(foodHolder2.transform, false);
            if (wrapperT != null)
            {
                wrapperT.SetParent(baseT, false); // ride the held box at its rest offset
                wrapperT.localPosition = wrapperLocalPos;
                wrapperT.localRotation = wrapperLocalRot;
            }
            reparented = true;

            SetRenderer(spoonR, false); // appears on "open" (no-op if no spoon)
            SetRenderer(foodR, false);  // appears on "scoop"/grab
            SetRenderer(foodR2, false); // the pair cube appears with it
            // (Draw/Open/Open2[/SpoonTake] fire from the STATE_OPEN segment itself.)
            return true;
        }

        // Drink: container in the HOLDING hand (right unless holdInOffHand). The cap (if any)
        // is a SIBLING of the container in the rig (cap + mod_item both under weapon), so
        // reparenting the container alone would strand it — instead it's glued ON the
        // container at its rest offset (visible from the draw), detached to the free hand by
        // the open gesture, and glued back on a recap.
        private static bool SetupDrinkProps(Transform root, Transform leftHandBone, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);
            capT = def.HasCap ? FindDeep(root, def.capName) : null;

            if (baseT == null || (def.HasCap && capT == null))
                return BailToVanilla($"Missing props (base={baseT != null} cap={(def.HasCap ? (capT != null).ToString() : "n/a")})");

            capR = capT != null ? capT.GetComponentInChildren<Renderer>(true) : null;

            // Capture the cap's on-container offset BEFORE anything moves.
            if (capT != null)
            {
                capLocalPos = baseT.InverseTransformPoint(capT.position);
                capLocalRot = Quaternion.Inverse(baseT.rotation) * capT.rotation;
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (capT != null) Save(capT, out capParent0, out capPos0, out capRot0);

            Transform holdBone = def.holdInOffHand ? leftHandBone : rightHandBone;
            capHandBoneRef = def.holdInOffHand ? rightHandBone : leftHandBone;

            baseHolder = NewHolder("EatBaseHolder", holdBone, def.basePos, def.baseRot);
            baseT.SetParent(baseHolder.transform, false);
            if (capT != null)
            {
                capT.SetParent(baseT, false);
                capT.localPosition = capLocalPos;
                capT.localRotation = capLocalRot;
            }
            reparented = true;
            // (Draw/Open fire from the STATE_OPEN segment itself.)
            return true;
        }

        // Handheld (chocolate bar): bar (rootName) in the RIGHT hand; the wrapper group is reparented
        // UNDER the bar (pinned to its rest offset each frame in LateZeroProps) while its sn_cover
        // child still animates the peel. No food-piece toggling.
        private static bool SetupHandheldProps(Transform root, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);                       // the bar (held item)
            wrapperT = string.IsNullOrEmpty(def.wrapperName) ? null : FindDeep(root, def.wrapperName);
            coverT = string.IsNullOrEmpty(def.coverName) ? null : FindDeep(root, def.coverName);
            if (baseT == null)
                return BailToVanilla($"Handheld missing bar '{def.rootName}'");

            // Capture the wrapper's offset relative to the bar at the rest pose so it stays glued.
            if (wrapperT != null)
            {
                wrapperLocalPos = baseT.InverseTransformPoint(wrapperT.position);
                wrapperLocalRot = Quaternion.Inverse(baseT.rotation) * wrapperT.rotation;
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (wrapperT != null) Save(wrapperT, out wrapperParent0, out wrapperPos0, out wrapperRot0);
            if (coverT != null) Save(coverT, out coverParent0, out coverPos0, out coverRot0);

            baseHolder = NewHolder("EatBarHolder", rightHandBone, def.basePos, def.baseRot);
            baseT.SetParent(baseHolder.transform, false);
            if (wrapperT != null)
            {
                wrapperT.SetParent(baseT, false);                 // ride the bar (carries sn_feces + sn_cover)
                wrapperT.localPosition = wrapperLocalPos;        // glue at the captured offset
                wrapperT.localRotation = wrapperLocalRot;
            }
            reparented = true;
            // Everything visible from the start; the wrapper (coverT) stays under wrapperT
            // and peels in place until the open gesture detaches it to the left hand.
            return true;
        }

        // Bag (croutons): bag root (rootName) in the RIGHT hand. The hold-crackers (names starting
        // with foodPieceName) ride the bag, hidden, until a SHAKE pours them into the left hand;
        // each is captured relative to the clump anchor (crackerT[0]) to keep its arrangement.
        private static bool SetupBagProps(Transform root, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName); // the bag (held item, holds everything)
            if (baseT == null)
                return BailToVanilla($"Bag missing root '{def.rootName}'");

            var found = new System.Collections.Generic.List<Transform>();
            FindAllByPrefix(root, def.foodPieceName, found);
            int n = found.Count;
            if (n == 0)
                return BailToVanilla($"Bag found no crackers with prefix '{def.foodPieceName}'");
            crackerT = found.ToArray();
            crackerParent0 = new Transform[n]; crackerPos0 = new Vector3[n]; crackerRot0 = new Quaternion[n];
            crackerLocalPos = new Vector3[n]; crackerLocalRot = new Quaternion[n]; crackerR = new Renderer[n];
            Transform anchor = crackerT[0];
            for (int i = 0; i < n; i++)
            {
                Save(crackerT[i], out crackerParent0[i], out crackerPos0[i], out crackerRot0[i]);
                // clump layout relative to the anchor (so anchor lands on the holder, the
                // rest cluster around it regardless of where the holder grip is tuned).
                crackerLocalPos[i] = anchor.InverseTransformPoint(crackerT[i].position);
                crackerLocalRot[i] = Quaternion.Inverse(anchor.rotation) * crackerT[i].rotation;
                crackerR[i] = crackerT[i].GetComponentInChildren<Renderer>(true);
                SetRenderer(crackerR[i], false); // hidden until the shake pours them out
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            baseHolder = NewHolder("EatBagHolder", rightHandBone, def.basePos, def.baseRot);
            baseT.SetParent(baseHolder.transform, false);
            reparented = true;
            return true;
        }

        // Pack (galette): pack (rootName) in the RIGHT hand; the wrapper group is glued to it
        // (carries the cover, which opens in place, + the food piece). The piece (foodPieceName)
        // is hidden until the LEFT hand takes it onto a left-hand holder. Mirror of CanHand.
        // ReachBag (iskra/MRE) shares this wiring verbatim (held root + hidden piece, no wrapper).
        private static bool SetupPackProps(Transform root, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);                                            // the pack (held)
            wrapperT = string.IsNullOrEmpty(def.wrapperName) ? null : FindDeep(root, def.wrapperName);
            foodT = FindDeep(root, def.foodPieceName);                                      // the piece to take
            if (baseT == null || foodT == null)
                return BailToVanilla($"{def.kind} missing prop (held={baseT != null} food={foodT != null})");
            foodR = foodT.GetComponentInChildren<Renderer>(true);

            // Glue the wrapper group to the pack at its rest offset (carries the cover + food).
            if (wrapperT != null)
            {
                wrapperLocalPos = baseT.InverseTransformPoint(wrapperT.position);
                wrapperLocalRot = Quaternion.Inverse(baseT.rotation) * wrapperT.rotation;
            }

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (wrapperT != null) Save(wrapperT, out wrapperParent0, out wrapperPos0, out wrapperRot0);
            Save(foodT, out foodParent0, out foodPos0, out foodRot0);

            baseHolder = NewHolder("EatPackHolder", rightHandBone, def.basePos, def.baseRot);
            baseT.SetParent(baseHolder.transform, false);
            if (wrapperT != null)
            {
                wrapperT.SetParent(baseT, false);
                wrapperT.localPosition = wrapperLocalPos;
                wrapperT.localRotation = wrapperLocalRot;
            }
            SetRenderer(foodR, false); // appears in the left hand on "take"
            reparented = true;
            return true;
        }

        // ===== Gates =====
        public static bool AllowEffect(MedsController.ObservedMedsControllerClass op)
        {
            MedsController owner = op?.MedsController_0;
            if (!active || owner != controller) return true;
            pendingOp = op;
            return effectFired;
        }

        public static bool AllowThirdAction(MedsController instance)
        {
            return !(active && !manualDone && instance == controller);
        }

        // ===== Per-frame =====
        public static void Tick(MedsController instance)
        {
            if (!active || manualDone || instance != controller) return;
            if (VRGlobals.vrPlayer == null || VRGlobals.ikManager == null) return;

            // Animator stays frozen except during the lid-roll (StepGesture drives it) and the
            // per-bite / gulp-loop play pulse (Time.time < playUntil).
            if (!playingOpen)
                instance.FirearmsAnimator?.SetAnimationSpeed(Time.time < playUntil ? spawnAnimSpeed : 0f);

            // Keep the rig glued to the body while walking (see driveBodyFollowDuringEat) — mid-eat
            // nothing else does, so without this the IK hands jitter.
            if (driveBodyFollowDuringEat)
            {
                if (VRGlobals.ikManager != null) VRGlobals.ikManager.MatchLegsToArms();
                PinRigToBody();
            }
            DriveArms();

            // A finish queued from STATE_OPEN waits here for STATE_USE to engage (the finish
            // must never cancel out of OPEN — busy hands + the food won't discard).
            if (finishPending)
            {
                if (!InOpenState() || Time.time >= finishDeadline)
                {
                    finishPending = false;
                    FinishSequence(finishIsCancel);
                }
                return;
            }

            // Both triggers together = cancel: put the food away without consuming it (a
            // mid-drink cancel still applies whatever was actually drunk). Edge-gated so
            // holding them doesn't refire.
            bool both = SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f
                     && SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f;
            bool bothEdge = both && !prevBothTriggers;
            prevBothTriggers = both;
            if (bothEdge)
            {
                Plugin.MyLog.LogInfo("[ManualEat] Both-trigger cancel — putting the food away.");
                ReattachCap(); // no-op unless a drink cap is off
                RequestFinish(cancel: true);
                return;
            }

            StepGesture();
        }

        // Run the finish — but NEVER from STATE_OPEN (cancelling a use frozen in the draw/
        // open state half-starts the op -> "busy hands" + the food flashes in inventory and
        // won't discard). If the arms are still in OPEN, kick them into STATE_USE and let
        // Tick complete the finish once it engages (deadline = give-up fallback).
        private static void RequestFinish(bool cancel)
        {
            EndPull();  // a cancel can land mid pull-open — unlatch so the put-away owns the arms
            EndReach(); // ...or mid reach-in (same latch plumbing, same reason)
            if (InOpenState())
            {
                EnterUseState();
                finishPending = true;
                finishIsCancel = cancel;
                finishDeadline = Time.time + 0.5f;
                return;
            }
            FinishSequence(cancel);
        }

        private static bool InOpenState()
        {
            try { return controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1).fullPathHash == STATE_OPEN_HASH; }
            catch { return false; }
        }

        public static void End()
        {
            try { controller?.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed); } catch { }
            UnsubscribePinAfterIk();
            DestroyZoneViz();
            TarkovVR.Source.Misc.AudioHaptics.Stop(); // pump + tap off (the controller object is pooled)
            RestoreProps();
            ReleaseArms();
            Plugin.MyLog.LogInfo("[ManualEat] End() — cleaned up.");
            Reset();
        }

        // Discard the eaten food, but ONLY after the cancel has fully switched off the meds
        // controller. The cancel returns the food to the bag AS PART OF drawing the weapon, so
        // discarding earlier corrupts the return (weapon never comes back, right hand sticks).
        private static System.Collections.IEnumerator ConsumeFoodWhenSettled(EFT.InventoryLogic.Item item)
        {
            MedsController meds = controller; // capture before teardown nulls it
            float timeout = Time.time + 6f;
            while (Time.time < timeout && StillHoldingMeds(meds))
                yield return null;
            // buffer so the switch transaction is fully settled before we discard
            yield return new WaitForSeconds(0.5f);
            if (HasParent(item)) DiscardFood(item);
            else Plugin.MyLog.LogWarning("[ManualEat] food not in a container — skipped discard.");
        }

        private static bool StillHoldingMeds(MedsController meds)
        {
            try { return meds != null && VRGlobals.player != null && ReferenceEquals(VRGlobals.player.HandsController, meds); }
            catch { return false; }
        }

        // Remove the eaten food from the inventory (we apply the full can's nutrition, so
        // a full discard matches).
        private static bool HasParent(EFT.InventoryLogic.Item item)
        {
            if (item == null) return false;
            try { return item.Parent != null; }
            catch { return false; } // get_Parent throws when the item has no parent
        }

        private static void DiscardFood(EFT.InventoryLogic.Item item)
        {
            try
            {
                var ic = VRGlobals.player?.InventoryController;
                if (ic == null || !HasParent(item))
                {
                    Plugin.MyLog.LogWarning("[ManualEat] DiscardFood: item not settled — skipped.");
                    return;
                }
                // simulate:true BUILDS the op; RunNetworkTransaction executes it once. false would
                // execute it here AND return it -> double-executed on a parentless item -> throw.
                var result = InteractionsHandlerClass.Discard(item, ic, simulate: true);
                if (result.Succeeded)
                {
                    ic.RunNetworkTransaction(result.Value);
                    Plugin.MyLog.LogInfo("[ManualEat] Consumed (discarded) food item.");
                }
                else Plugin.MyLog.LogWarning("[ManualEat] DiscardFood: discard not allowed.");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] DiscardFood error: {ex}"); }
        }

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

        private static GameObject NewHolder(string name, Transform parent, Vector3 pos, Vector3 rot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(rot);
            return go;
        }

        // LateUpdate (after the animator): the animator keeps writing each prop's local, so zero
        // it back onto its holder (the holder = the clean, tunable hold offset).
        public static void LateZeroProps()
        {
            if (!active || !reparented || def == null) return;
            // Re-pin each archetype's props onto their holders (the animator keeps rewriting the
            // props' locals every frame). One case per FoodKind — a new type adds its case here.
            switch (def.kind)
            {
                case FoodKind.Handheld:
                    ZeroLocal(baseT); // bar sits on its holder
                    // wrapper stays glued to the bar; cover zeroes onto the left-hand holder once detached.
                    if (wrapperT != null) { wrapperT.localPosition = wrapperLocalPos; wrapperT.localRotation = wrapperLocalRot; }
                    if (coverT != null && coverDetached) ZeroLocal(coverT);
                    break;
                case FoodKind.Bag:
                    ZeroLocal(baseT); // bag sits on its holder (carries the in-bag crackers + corner)
                    // Once shaken out, the hold-crackers live under the left-hand holder; pin
                    // each to its captured clump layout so the animator can't drag them off.
                    if (crackersShown && crackerT != null)
                        for (int i = 0; i < crackerT.Length; i++)
                        {
                            if (crackerT[i] == null) continue;
                            crackerT[i].localPosition = crackerLocalPos[i];
                            crackerT[i].localRotation = crackerLocalRot[i];
                        }
                    break;
                case FoodKind.Pack:
                case FoodKind.ReachBag: // same wiring as Pack (held root + taken piece, no wrapper)
                    ZeroLocal(baseT); // pack sits on its holder
                    // wrapper group (cover + pile + food) stays glued to the pack.
                    if (wrapperT != null) { wrapperT.localPosition = wrapperLocalPos; wrapperT.localRotation = wrapperLocalRot; }
                    // once taken, the food piece sits on the left-hand holder.
                    if (foodHolder != null && foodT != null && foodT.parent == foodHolder.transform) ZeroLocal(foodT);
                    break;
                case FoodKind.Drink:
                    ZeroLocal(baseT); // drink sits on its holder
                    if (capT != null)
                    {
                        if (capDetached) ZeroLocal(capT); // on its free-hand holder
                        else { capT.localPosition = capLocalPos; capT.localRotation = capLocalRot; } // glued on the container
                    }
                    break;
                default: // FoodKind.CannedFood
                    ZeroLocal(baseT);
                    ZeroLocal(spoonT);
                    ZeroLocal(foodT);
                    ZeroLocal(foodT2);
                    // glued sibling group (sugar's remaining pile) stays welded to the held box
                    if (wrapperT != null) { wrapperT.localPosition = wrapperLocalPos; wrapperT.localRotation = wrapperLocalRot; }
                    break;
            }

            // LateUpdate-phase rig pins (after the IK solve), like HandsPositioner.LateUpdate —
            // without this mid-eat the rig drifts post-solve and the hands jitter while walking.
            if (driveBodyFollowDuringEat)
            {
                if (medsBody != null && VRGlobals.vrPlayer != null)
                    medsBody.rotation = VRGlobals.vrPlayer.handsRotation;
                PinRigToBody();
            }

            // Pull-to-open latch LAST: it needs the prop re-zeroing above already applied
            // (the grip is read through the holding wrist's rigid chain, which must have
            // baseT back on its holder) and the rig pins final (the latch's parent target
            // rides camRoot). Still pre-FinalIK, so the IK + pin aim at the result.
            DriveLatch();
            DriveReachLatch(); // the ReachBag rail (mutually exclusive with the pull latch)

            // Zone debug spheres last, off the final hand/head poses for this frame
            // (self-cleans when debugZones is toggled off mid-eat).
            UpdateZoneViz();
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

        private static void ZeroLocal(Transform t)
        {
            if (t == null) return;
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
        }

        private static void RestoreProps()
        {
            if (!reparented) return;
            try
            {
                // Props back to their original rig parents, then drop the holders. Restore
                // the wrapper group BEFORE the cover (the cover's original parent sn_root
                // lives under the wrapper group, so it must be back in place first).
                Restore(baseT, baseParent0, basePos0, baseRot0);
                Restore(spoonT, spoonParent0, spoonPos0, spoonRot0);
                Restore(foodT, foodParent0, foodPos0, foodRot0);
                Restore(foodT2, food2Parent0, food2Pos0, food2Rot0);         // sugar's 2nd cube (null-safe)
                Restore(wrapperT, wrapperParent0, wrapperPos0, wrapperRot0); // handheld/glued sibling (null-safe)
                Restore(coverT, coverParent0, coverPos0, coverRot0);         // handheld (null-safe)
                Restore(capT, capParent0, capPos0, capRot0);                     // drink (null-safe)
                if (crackerT != null) // bag crackers back to the bag, re-shown
                    for (int i = 0; i < crackerT.Length; i++)
                    {
                        Restore(crackerT[i], crackerParent0[i], crackerPos0[i], crackerRot0[i]);
                        SetRenderer(crackerR[i], true);
                    }
                SetRenderer(spoonR, true);
                SetRenderer(foodR, true);
                SetRenderer(foodR2, true);
                SetRenderer(capR, true);
                if (segR != null) // segmented snack: re-show whatever bites hid
                    for (int i = 0; i < segR.Length; i++) SetRenderer(segR[i], true);
                if (baseHolder != null) UnityEngine.Object.Destroy(baseHolder);
                if (spoonHolder != null) UnityEngine.Object.Destroy(spoonHolder);
                if (foodHolder != null) UnityEngine.Object.Destroy(foodHolder);
                if (foodHolder2 != null) UnityEngine.Object.Destroy(foodHolder2);
                if (crackerHolder != null) UnityEngine.Object.Destroy(crackerHolder);
                if (capHolder != null) UnityEngine.Object.Destroy(capHolder);
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] RestoreProps error: {ex}"); }
            baseHolder = spoonHolder = foodHolder = foodHolder2 = crackerHolder = capHolder = null;
            reparented = false;
        }

        //--- Gesture state machine --------------------------------------------------
        // All archetypes share ONE open->take->eat loop; only the hand roles, the arms state
        // the open plays, and a few reveal/advance hooks differ. Those live in the per-archetype
        // EatStyle descriptor (built per food on spawn), so the control flow below is written
        // once. The genuinely-unique routines (DetachWrapperToLeftHand / ShakeOutCrackers /
        // TakeFoodToLeftHand / EnterUseState / DetectShake) are kept as-is and just called from
        // the hooks. To add a TYPE: a new case here + its prop setup / late-zero tails.
        private static EatStyle BuildStyle(FoodDef d)
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
                    s.onOpened?.Invoke();
                    phase = Phase.Ready;
                    Plugin.MyLog.LogInfo($"[ManualEat] Opened {s.label} — ready (take={s.hasTakeStep}).");
                }
            }
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
            s.onOpened?.Invoke();
            phase = Phase.Ready;
            PulseHand(s.openHand == Hand.Dominant);
            Plugin.MyLog.LogInfo($"[ManualEat] Opened {s.label} — ready (take={s.hasTakeStep}).");
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

        // CanSpoon hybrid (mouthDrink): HOLD the CAN at your mouth and it drinks down the
        // item's use-time exactly like a Drink — live drain (the bar ticks), the gulp loop
        // plays the clip's own drink audio, lowering pauses. The spoon path stays available
        // from Ready; a spoon scoop after a partial drink is capped at what's LEFT (see the
        // per-bite drain in StepEat), so "drink half, then one scoop finishes it". The can
        // rides the OFF hand (eatHand = the spoon hand), so the mouth check anchors there.
        // Returns true while the can is at the mouth (the frame belongs to drinking).
        private static bool StepCanDrink(EatStyle s)
        {
            Hand canHand = Other(s.eatHand);
            if (EatZoneReached(canHand))
            {
                if (!drinkingHeld)
                {
                    drinkingHeld = true;
                    nextDrinkSound = 0f; // first gulp plays immediately
                    Pulse();
                    Plugin.MyLog.LogInfo("[ManualEat] Drinking from the can...");
                }
                drinkHeldTime += Time.deltaTime;
                DriveDrinkLoop();
                DrainDrinkLive();
                bool done = drinkFdc != null && drinkLiveDrain
                    ? drinkFdc.HpPercent <= 0.0001f
                    : drinkHeldTime >= drinkUseTime * drinkRemainingFrac;
                if (done)
                {
                    Plugin.MyLog.LogInfo($"[ManualEat] Drank the can down ({drinkHeldTime:F1}s).");
                    RequestFinish(cancel: false);
                }
                return true;
            }
            if (drinkingHeld)
            {
                drinkingHeld = false;
                playUntil = 0f;   // freeze the gulp mid-loop
                StopDrinkSound(); // cut the glug — re-raising restarts it
                Plugin.MyLog.LogInfo("[ManualEat] Lowered the can — spoon still works.");
            }
            return false;
        }

        // Drink: HOLD the container at the mouth — the resource drains LIVE while held (the
        // gulp loop plays + its own Drink sounds; the bar ticks down). Auto-finishes the
        // moment the live resource empties (falls back to the use-time clock when the item
        // has no resource component). Lowering it pauses; bringing the free hand back to
        // the cap/open zone + trigger recaps and finishes with just what was drunk (the
        // item keeps the rest — already applied by the live drain).
        private static void StepDrink(EatStyle s)
        {
            if (EatZoneReached(s.eatHand))
            {
                if (!drinkingHeld)
                {
                    drinkingHeld = true;
                    nextDrinkSound = 0f; // first gulp plays immediately
                    Pulse();
                    Plugin.MyLog.LogInfo("[ManualEat] Drinking...");
                }
                drinkHeldTime += Time.deltaTime;
                DriveDrinkLoop();
                DrainDrinkLive();
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
                return;
            }

            if (drinkingHeld)
            {
                drinkingHeld = false;
                playUntil = 0f;   // freeze the gulp mid-loop
                StopDrinkSound(); // cut the glug — re-raising restarts it from the top
                Plugin.MyLog.LogInfo($"[ManualEat] Paused at {drinkHeldTime:F1}/{drinkUseTime * drinkRemainingFrac:F1}s.");
            }

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
        // pooled BetterSource; ReleaseClipsSource -> BetterSource.Release -> Stop (verified
        // in the real DLL) silences it NOW, and the next gulp event sets up a fresh source —
        // so lowering the bottle stops the sound and raising it restarts it from the top
        // (the drinkingHeld transition resets nextDrinkSound, so the first gulp is instant).
        private static void StopDrinkSound()
        {
            try { soundPlayer?.ReleaseClipsSource(); }
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

        // Segmented snack (sausage): hide the held item's renderers progressively — stepped
        // at the audible bite sounds (called when a sound passes while held at the mouth)
        // but SYNCED to the consumed fraction, so the sausage is gone exactly when the
        // resource is, however many sounds fired. Hides from the tip inward
        // (segmentHideFromEnd flips the end). All re-shown on restore.
        private static void UpdateSnackSegments()
        {
            if (def == null || !def.segmentedBites || segR == null || segR.Length == 0) return;
            float progress = drinkRemainingFrac > 0f ? Mathf.Clamp01(drinkAppliedFrac / drinkRemainingFrac) : 0f;
            int want = Mathf.Min(Mathf.CeilToInt(progress * segR.Length), segR.Length);
            for (int i = 0; i < segR.Length; i++)
            {
                int idx = segmentHideFromEnd ? segR.Length - 1 - i : i;
                SetRenderer(segR[idx], i >= want); // the first `want` from the hide-end are gone
            }
        }

        // Hide every poured cracker (Bag onEatHide). Named (not a lambda) so the loop body in
        // the for-each stays readable.
        private static void HideCrackers()
        {
            if (crackerR != null)
                for (int i = 0; i < crackerR.Length; i++) SetRenderer(crackerR[i], false);
            crackersShown = false;
        }

        // Handheld: once peeled, move the WRAPPER (coverT, e.g. sn_cover) from the bar onto
        // a LEFT-hand holder so the player holds the peeled wrapper in their off hand (like
        // the spoon for a can). The bar (canT) AND the chocolate (sn_feces, still under
        // wrapperT) stay in the right hand. From here coverT's local is pinned to zero so it
        // sits on the (tunable) left-hand holder; its mesh keeps its own shape.
        private static void DetachWrapperToLeftHand()
        {
            if (coverT == null || coverDetached || leftHandBoneRef == null) return;
            try
            {
                foodHolder = NewHolder("EatWrapperHolder", leftHandBoneRef, def.foodPos, def.foodRot);
                coverT.SetParent(foodHolder.transform, false);
                coverDetached = true;
                Plugin.MyLog.LogInfo("[ManualEat] Wrapper (cover) detached to the left hand.");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] DetachWrapper error: {ex}"); }
        }

        // Drink: the open gesture pops the cap off the container onto a holder in the FREE
        // hand (the one that worked the cap — flips with holdInOffHand).
        private static void DetachCapToFreeHand()
        {
            // Hinged flip-top: never detaches — it stays welded to the bottle (LateZeroProps
            // keeps it glued while !capDetached) and animated open in place.
            if (def != null && def.capHinged) return;
            if (capT == null || capDetached || capHandBoneRef == null) return;
            try
            {
                if (capHolder == null) capHolder = NewHolder("EatCapHolder", capHandBoneRef, def.capPos, def.capRot);
                capT.SetParent(capHolder.transform, false);
                capDetached = true;
                Plugin.MyLog.LogInfo("[ManualEat] Cap popped to the free hand.");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] DetachCap error: {ex}"); }
        }

        // Drink: glue the cap back onto the container at its captured rest offset (the recap
        // that stops a partial drink — also makes the put-away animation look right).
        private static void ReattachCap()
        {
            if (capT == null || !capDetached) return;
            try
            {
                capT.SetParent(baseT, false);
                capT.localPosition = capLocalPos;
                capT.localRotation = capLocalRot;
                capDetached = false;
                Plugin.MyLog.LogInfo("[ManualEat] Cap back on.");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] ReattachCap error: {ex}"); }
        }

        // Pour the hold-crackers into the LEFT hand: move them onto a left-hand holder in
        // their captured clump layout and show them. (Re-shows them for later rounds too.)
        private static void ShakeOutCrackers()
        {
            if (crackerT == null) return;
            if (crackerHolder == null && leftHandBoneRef != null)
                crackerHolder = NewHolder("EatCrackerHolder", leftHandBoneRef, def.foodPos, def.foodRot);
            for (int i = 0; i < crackerT.Length; i++)
            {
                if (crackerT[i] == null) continue;
                if (crackerHolder != null)
                {
                    crackerT[i].SetParent(crackerHolder.transform, false);
                    crackerT[i].localPosition = crackerLocalPos[i];
                    crackerT[i].localRotation = crackerLocalRot[i];
                }
                SetRenderer(crackerR[i], true);
            }
            crackersShown = true;
        }

        // Move the arms animator from STATE_OPEN into STATE_USE (the "use" phase). The brief
        // playUntil makes Tick run the animator a few frames so the state actually engages;
        // then it freezes. Needed so FinishSequence cancels from STATE_USE like every other
        // food (cancelling from STATE_OPEN = busy hands + the food won't discard). Used by the
        // Bag (shake) and Pack (take) gestures, whose grabs don't otherwise advance the animator.
        private static void EnterUseState()
        {
            try { controller._player.ArmsAnimatorCommon.Play(STATE_USE_HASH, 1, 0f); }
            catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] EnterUseState failed: {ex.Message}"); }
            playUntil = Time.time + 0.15f;
        }

        // Move the single food piece (foodT) onto a left-hand holder and show it (re-shows for
        // later picks too). Mirrors the bag/cracker move, but for one piece into the off hand.
        private static void TakeFoodToLeftHand()
        {
            if (foodT == null) return;
            if (foodHolder == null && leftHandBoneRef != null)
                foodHolder = NewHolder("EatPackFoodHolder", leftHandBoneRef, def.foodPos, def.foodRot);
            if (foodHolder != null) foodT.SetParent(foodHolder.transform, false);
            SetRenderer(foodR, true);
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

        // Final bite / drink done / cancel: apply the consumed fraction's nutrition, tell the
        // animator the use is DONE (SetActiveParam(false) — what the game's own method_9
        // does), which drives it straight to the END/put-away state regardless of the effect
        // timer. The END animation then fires the weapon-out event -> we switch back to the
        // weapon. manualDone stays false so Tick keeps the animator playing to END.
        // cancel=true consumes nothing extra (the both-trigger bail) — but a drink still
        // applies whatever was actually drunk before the cancel.
        private static void FinishSequence(bool cancel = false)
        {
            phase = Phase.Done;
            manualDone = true; // stop the hand-pin/IK so the END animation moves the hands (put-away)
            drinkingHeld = false;
            playingOpen = false; // a cancel can land mid lid-roll; don't leave the open flag stuck
            ReleaseArms();

            if (effectFired) { Plugin.MyLog.LogWarning("[ManualEat] Finish: effect already fired."); return; }
            if (pendingOp == null) { Plugin.MyLog.LogError("[ManualEat] Finish: pendingOp NULL — may stick!"); return; }

            // What this use actually consumed, as a 0..1 fraction of the WHOLE item (what
            // CutPiece wants): timed eats (drinks + eatByTime snacks) = at-mouth time vs the
            // full-item use-time, capped at what was left; bite foods = everything left on a
            // normal finish, or just what the bites/mouth already applied on a cancel (eaten
            // bites STAY eaten — the item keeps the rest).
            float fraction;
            if (def != null && (def.kind == FoodKind.Drink || def.eatByTime))
                fraction = Mathf.Min(drinkUseTime > 0f ? drinkHeldTime / drinkUseTime : drinkRemainingFrac, drinkRemainingFrac);
            else
                fraction = cancel ? Mathf.Min(drinkAppliedFrac, drinkRemainingFrac) : drinkRemainingFrac;

            effectFired = true;
            Plugin.MyLog.LogInfo($"[ManualEat] Finish (cancel={cancel}, fraction={fraction:F2}): method_5 + SetActiveParam(false) -> play END.");
            try
            {
                var fa = controller.FirearmsAnimator;
                fa?.SetNextLimb(false);

                if (cancelToFinish)
                {
                    // The fire-cancel aborts the over-time heal effect, so apply the consumed
                    // energy/hydration INSTANTLY first (otherwise no nutrition) and drain the
                    // item's resource by the same fraction. The live drink/mouth drain and the
                    // per-bite drain already applied most of it incrementally
                    // (drinkAppliedFrac) — only the unapplied remainder goes here, so nothing
                    // doubles, and emptied comes off the live resource itself.
                    float remainder = fraction - drinkAppliedFrac;
                    if (remainder > 0.0001f)
                    {
                        ApplyConsumedNutrition(remainder);
                        drinkAppliedFrac = fraction;
                    }
                    // Chunked float math can leave a hair of resource when this use consumed
                    // everything that was left — snap it to a true 0 so the empty-discard fires.
                    if (drinkFdc != null && fraction >= drinkRemainingFrac - 0.001f
                        && drinkFdc.HpPercent > 0f && drinkFdc.HpPercent <= drinkFdc.MaxResource * 0.01f)
                        drinkFdc.HpPercent = 0f;
                    bool emptied = drinkFdc != null
                        ? drinkFdc.HpPercent <= 0.0001f
                        : fraction >= drinkRemainingFrac - 0.0001f; // no resource component = one-shot
                    Plugin.MyLog.LogInfo(drinkFdc != null
                        ? $"[ManualEat] Consumed {fraction:P0} ({drinkFdc.HpPercent:F1}/{drinkFdc.MaxResource:F0} left{(emptied ? " — empty" : "")})."
                        : $"[ManualEat] Consumed {fraction:P0} (no resource component{(emptied ? " — treated as empty" : "")}).");
                    pendingOp.method_5();   // keep — leaves the controller in the state the cancel needs
                    // SetActiveParam(false) is REQUIRED — it flips the controller out of
                    // the mid-use state into the cancellable/put-away state. Without it the
                    // controller stays frozen and the fire-cancel has nothing to act on.
                    fa?.SetActiveParam(false, false);
                    fa?.SetAnimationSpeed(spawnAnimSpeed);
                    // Jump the arms animator straight to STATE_END so the hands go down
                    // immediately instead of playing the rest of STATE_EAT (the auto bite).
                    if (skipLastBiteAnim)
                    {
                        try { controller._player.ArmsAnimatorCommon.Play(STATE_END_HASH, 1, putAwayStartTime); }
                        catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] skip-bite Play failed: {ex.Message}"); }
                    }
                    // Auto-fire the trigger/fire cancel (press then release on consecutive
                    // input frames) — puts the food away and draws the weapon fast.
                    TarkovVR.Source.Controls.VRInputManager.ForceCommand(EFT.InputSystem.ECommand.ToggleShooting);
                    TarkovVR.Source.Controls.VRInputManager.ForceCommand(EFT.InputSystem.ECommand.EndShooting);
                    // The cancel aborts the vanilla resource depletion, so an EMPTIED item
                    // never gets removed — discard it ourselves once the put-away has settled
                    // it back into a container (need a stable parent; the discard itself is
                    // simulate->RunNetworkTransaction so it executes exactly once). A
                    // partially-drunk bottle keeps its remaining resource and stays.
                    if (emptied && VRGlobals.vrPlayer != null)
                        VRGlobals.vrPlayer.StartCoroutine(ConsumeFoodWhenSettled(controller.Item));
                }
                else
                {
                    pendingOp.method_5();   // nutrition (persists in background)
                    fa?.SetActiveParam(false, false); // use done -> animator goes to END/put-away
                    fa?.SetAnimationSpeed(spawnAnimSpeed);
                }
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] Finish error: {ex}"); }
        }

        // Apply the consumed fraction's energy/hydration immediately (the fire-cancel aborts
        // the vanilla over-time heal effect before it ticks) and drain the item's resource
        // bar by the same fraction. CutPiece takes a 0..1 FRACTION of the whole item — NOT
        // resource units (passing MaxResource over-applied multi-use drinks N-fold). Returns
        // true when the item is now empty (caller discards it). quiet = no per-call log
        // (the live drink drain calls this in small chunks every fraction of a second).
        private static bool ApplyConsumedNutrition(float fraction, bool quiet = false)
        {
            EFT.InventoryLogic.Item item = controller?.Item;
            var fdc = item?.GetItemComponent<EFT.InventoryLogic.FoodDrinkComponent>();
            if (fraction <= 0f) return false;
            try
            {
                EFT.Player player = controller?._player;
                if (player == null || item == null) return false;

                var hec = item.GetItemComponent<EFT.InventoryLogic.HealthEffectsComponent>();
                var hc = player.ActiveHealthController;
                if (hec != null && hc != null)
                {
                    var effects = hec.HealthEffects;
                    if (effects.TryGetValue(EFT.HealthSystem.EHealthFactorType.Energy, out var en))
                    {
                        float v = en.CutPiece(fraction).Value;
                        if (v != 0f) hc.ChangeEnergy(v);
                    }
                    if (effects.TryGetValue(EFT.HealthSystem.EHealthFactorType.Hydration, out var hy))
                    {
                        float v = hy.CutPiece(fraction).Value;
                        if (v != 0f) hc.ChangeHydration(v);
                    }
                }

                // Drain the resource (HpPercent is in RESOURCE UNITS, not 0-100) and refresh
                // the inventory bar. No FoodDrinkComponent = single-use -> treat as emptied.
                if (fdc == null) return true;
                fdc.HpPercent = Mathf.Max(0f, fdc.HpPercent - fdc.MaxResource * fraction);
                item.RaiseRefreshEvent();
                if (!quiet)
                    Plugin.MyLog.LogInfo($"[ManualEat] Nutrition for {fraction:P0} of the item ({fdc.HpPercent:F1}/{fdc.MaxResource:F0} left).");
                return fdc.HpPercent <= 0.0001f;
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[ManualEat] ApplyConsumedNutrition error: {ex}");
                // Resource state unknown — don't discard a bottle that may still have water.
                try { return fdc == null || fdc.HpPercent <= 0.0001f; } catch { return false; }
            }
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

        // The eat gesture: the eating hand's PALM probe (+ eatZoneOffset, e.g. a spout) is
        // near the MOUTH (head + mouthLocalOffset — the camera is at the eyes, the mouth is
        // below it) and roughly in front of the face. The forward gate keeps behind-the-head
        // positions from triggering.
        private static bool EatZoneReached(Hand eatHand)
        {
            Transform head = GetHead();
            Transform hand = HandT(eatHand);
            if (head == null || hand == null) return false;
            Vector3 delta = hand.TransformPoint(PalmOffsetLocal(eatHand) + eatZoneOffset) - head.TransformPoint(mouthLocalOffset);
            if (delta.magnitude > eatDistance) return false;
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
                eatHand != null ? eatHand.TransformPoint(PalmOffsetLocal(s.eatHand) + eatZoneOffset) : Vector3.zero, 0.015f, new Color(0.2f, 1f, 0.3f, 0.9f));
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

        // Trigger rising-edge for the opening hand. dominant=true => the dominant (can-opening)
        // hand's trigger; false => the off hand's (wrapper/bag/pack opening). Tracks each hand's
        // previous axis separately so the two edges don't interfere.
        private static bool TriggerEdgeImpl(bool dominant)
        {
            bool lefty = VRSettings.GetLeftHandedMode();
            // dominant hand = right in normal mode; the off hand is the other one.
            bool useLeft = dominant ? lefty : !lefty;
            SteamVR_Action_Single trig = useLeft ? SteamVR_Actions._default.LeftTrigger : SteamVR_Actions._default.RightTrigger;
            SteamVR_Input_Sources src = useLeft ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
            float axis = trig.GetAxis(src);
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

        // Trigger LEVEL (not edge) for the pull-open hold. Releases at a lower threshold than
        // the 0.5 press edge so the latch doesn't flutter at the boundary; doesn't touch the
        // prev-axis edge state (TriggerEdgeImpl keeps working independently).
        // Raw trigger axis for a hand role (dominant = the right hand in normal mode; the
        // SteamVR source swap mirrors TriggerEdgeImpl's). The open squeeze scrubs off this.
        private static float TriggerAxisImpl(bool dominant)
        {
            bool lefty = VRSettings.GetLeftHandedMode();
            bool useLeft = dominant ? lefty : !lefty;
            SteamVR_Action_Single trig = useLeft ? SteamVR_Actions._default.LeftTrigger : SteamVR_Actions._default.RightTrigger;
            SteamVR_Input_Sources src = useLeft ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
            return trig.GetAxis(src);
        }

        private static void Pulse()
        {
            if (!eatingHaptics) return;
            SteamVR_Input_Sources src = VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
            SteamVR_Actions._default.Haptic.Execute(0f, 0.06f, 1f, 0.4f, src);
        }

        // Haptic on a specific hand role (Pulse() always buzzes the dominant controller; the
        // pull-open's acting hand is the OFF hand for wrapper/bag/pack/most drinks).
        private static void PulseHand(bool dominant)
        {
            if (!eatingHaptics) return;
            bool lefty = VRSettings.GetLeftHandedMode();
            bool useLeft = dominant ? lefty : !lefty;
            SteamVR_Input_Sources src = useLeft ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
            SteamVR_Actions._default.Haptic.Execute(0f, 0.06f, 1f, 0.4f, src);
        }

        private static void SetRenderer(Renderer r, bool on) { if (r != null) r.enabled = on; }

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
                UpdateSnackSegments(); // segmented snacks shed a piece per audible bite
                return true;
            }
            return false; // everything else is the animation bursting (manual sounds returned above)
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
                bool lefty = VRSettings.GetLeftHandedMode();
                bool useLeft = dominantActs ? lefty : !lefty;
                TarkovVR.Source.Misc.AudioHaptics.OnClipPlayed(sp._clipsSource, clip, useLeft, !useLeft);
            }
            catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] OnEatAudio: {ex.Message}"); }
        }

        private static void Save(Transform t, out Transform parent, out Vector3 pos, out Quaternion rot)
        {
            parent = t.parent; pos = t.localPosition; rot = t.localRotation;
        }

        private static void Restore(Transform t, Transform parent, Vector3 pos, Quaternion rot)
        {
            if (t == null) return;
            t.SetParent(parent, false);
            t.localPosition = pos;
            t.localRotation = rot;
        }

        private static FoodDef FindDef(string templateId)
        {
            if (string.IsNullOrEmpty(templateId)) return null;
            foreach (FoodDef d in Defs) if (d.templateId == templateId) return d;
            return null;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        // Collect every transform whose name starts with 'prefix' (the bag's hold-crackers).
        private static void FindAllByPrefix(Transform root, string prefix, System.Collections.Generic.List<Transform> into)
        {
            if (root == null || string.IsNullOrEmpty(prefix)) return;
            if (root.name.StartsWith(prefix, StringComparison.Ordinal)) into.Add(root);
            for (int i = 0; i < root.childCount; i++) FindAllByPrefix(root.GetChild(i), prefix, into);
        }

        private static void Reset()
        {
            active = false;
            manualDone = false;
            controller = null;
            def = null;
            style = null;
            phase = Phase.Closed;
            biteCount = 0;
            baseT = spoonT = foodT = capT = null;
            foodT2 = null; foodR2 = null; food2Parent0 = null;
            foodHolder2 = null;
            segR = null;
            eatLoopState = 0;
            wrapperT = coverT = null;
            coverParent0 = null;
            coverDetached = false;
            capDetached = false;
            leftHandBoneRef = null;
            capHandBoneRef = null;
            capLocalPos = Vector3.zero; capLocalRot = Quaternion.identity;
            drinkUseTime = 0f; drinkRemainingFrac = 1f; drinkHeldTime = 0f; drinkingHeld = false;
            drinkAppliedFrac = 0f; drinkTimeAppliedFrac = 0f; drinkFdc = null;
            nextDrinkSound = 0f; nextLoopPlay = 0f;
            prevBothTriggers = false;
            finishPending = false; finishIsCancel = false; finishDeadline = 0f;
            crackerT = null; crackerR = null;
            crackerParent0 = null; crackerPos0 = null; crackerRot0 = null;
            crackerLocalPos = null; crackerLocalRot = null;
            crackerHolder = null; crackersShown = false;
            shakeReversals = 0; shakeWindowEnd = 0f; shakePrevPos = shakePrevVel = Vector3.zero;
            medsBody = null;
            spoonR = foodR = capR = null;
            soundPlayer = null;
            baseHolder = spoonHolder = foodHolder = capHolder = null;
            baseParent0 = spoonParent0 = foodParent0 = wrapperParent0 = capParent0 = null;
            reparented = false;
            effectFired = false;
            pendingOp = null;
            prevTriggerAxis = 0f;
            prevOffTriggerAxis = 0f;
            playingOpen = false;
            pullingOpen = false;
            pullProgress = 0f; pullGrabProgress = 0f; pullGrabLocal = Vector3.zero; pullPalmLocal = Vector3.zero;
            pullSqueezeDone = false; pullAwaitRelease = false; pullGrabRel = Quaternion.identity;
            pullAnimStart = -1f; pullSoundsFired = 0;
            handLatched = false; latchedHandIsDominant = false;
            if (pullLatch != null) { UnityEngine.Object.Destroy(pullLatch); pullLatch = null; }
            reachingIn = false; reachGrabbed = false; reachProgress = 0f;
            reachEntryPalmLocal = reachAxisLocal = reachPalmLocal = Vector3.zero;
            latchGrabActRot = Quaternion.identity; latchGrabLocalRot = Quaternion.identity;
            openGripT = null; latchRidesProp = false;
            propGrabLocalPos = Vector3.zero; propGrabLocalRot = Quaternion.identity;
            actingWristT = null; fingerAnchors = null; fingerAnchorResolved = false;
            palmAnchorsResolved = false; palmWristDom = palmWristOff = null; palmLeavesDom = palmLeavesOff = null;
            moverBones = null; moverStartLocal = null;
            playUntil = 0f;
        }
    }
}
