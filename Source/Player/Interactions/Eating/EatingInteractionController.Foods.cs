using System;
using System.Text;
using UnityEngine;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Player.Interactions
{
    // The food data model and registry: FoodDef, the archetype factories, shape presets
    // and chain wrappers that build it, the Defs table (one line per food), and
    // DumpFoodDef, which prints a tuned line ready to paste back into Defs.
    internal static partial class EatingInteractionController
    {
        //--- Per-item definitions ---------------------------------------------------
        // Everything that differs between foods. The finish (nutrition, cancel, discard) is the same
        // for every food; this is the gesture and prop half. FoodKind picks the gesture machine.
        private enum FoodKind { CannedFood, Handheld, Bag, Pack, Drink, ReachBag }

        // ===== Gesture archetype descriptor =====================================
        // What the shared open -> take -> eat loop needs that differs per archetype. Built per food by BuildStyle;
        // the hooks call the per-archetype routines.
        private enum Hand { Dominant, Off }        // Dominant = the right hand always; Off = left
        private enum TakeKind { None, HandNear, Shake, ReachIn }

        // How the physical part of the open is done, after the trigger squeeze. On every kind the squeeze
        // scrubs the first triggerOpenPortion of the open.
        //   Pull    - move the hand away from where the trigger was fully pressed (lids, wrappers, tears)
        //   Tilt    - tip the hand away from its orientation at the full press (screw caps, flip-tops, can tabs).
        //             Wrist roll is measured too inconsistently: don't add a twist kind.
        //   Trigger - no physical part: the trigger axis scrubs the whole open, which completes on a full press
        //             and then a full release. No food uses it now; DriveLatch freezes the latch at the grab for it.
        private enum OpenGestureKind { Pull, Tilt, Trigger }

        private sealed class EatStyle
        {
            public string label;          // for logs ("can"/"wrapper"/"bag"/"pack")
            public Hand openHand;         // hand that performs the open gesture
            public Hand eatHand;          // hand that goes to the mouth
            public Hand takeHand;         // hand that performs a HandNear take (unused for Shake/None)
            public bool hasTakeStep;      // false = open then eat directly (Handheld)
            public bool timedSip;         // Drink: holding it at the mouth runs a timer (no bite counting)
            public TakeKind takeKind;     // how the take is triggered
            public int openStateHash;     // arms state the open plays (STATE_OPEN, or STATE_USE)
            public bool logsGesturePose;  // log the take/eat pulse's source state (cans + pack)
            public Action onOpened;       // reveal/detach once the open completes
            public Action onTake;         // reveal/move the food and advance the animator on take
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
            // Clip-animated bone the latched hand follows during a pull-open (the can's lid or pull-key bone, the
            // wrapper cover). Set via OpenGrip(def, "bone"). null = the latch freezes where you grabbed.
            // logOpenMovers finds the name.
            public string openGripName;
            // Baked open-hand path: the opening palm's pose relative to the held item root, sampled from the vanilla
            // open animation by EatingRecon ([HANDPATH], with the Manual Eating setting off). 8 floats per key: t,
            // px,py,pz, qx,qy,qz,qw (t = normalizedTime in the open state, ascending). When set, it drives the latch
            // instead of the OpenGrip bone. Set via HandPath(def, ...floats).
            public float[] openHandPath;
            // Drink: the cap group stays attached to the bottle (a hinged flip-top, e.g. moonshine's "hinge")
            // instead of detaching to the free hand. Its child bones still animate the flip open in place.
            public bool capHinged;
            public bool holdInOffHand;        // Drink: container in the left hand (the cap hand switches too)
            public float drinkSeconds;        // Drink: seconds at the mouth for a full item; 0 = the item's vanilla UseTime
            // Handheld/Pack: wrapperName = a group attached to the held item (carries sub-pieces, as on
            // the chocolate); coverName = the wrapper mesh that detaches to the off hand once
            // peeled. Either can be null.
            public string wrapperName;
            public string coverName;
            public int bites = 3;
            // Skip the open phase: spawn straight into Ready, so you just bring the held item to your
            // mouth (sausage: held in one hand, the other does nothing).
            public bool skipOpen;

            // Sound event names (BaseSoundPlayer.OnSound); null = none. draw/open fire from the
            // STATE_OPEN clip itself; scoop/eat are played per gesture (the animation is frozen in between).
            public string drawSound;
            public string[] openSounds;
            public string scoopSound;     // played when the food is taken (scoop / grab)
            public string eatSound;       // played on each bite

            // Grip = the hold pose local to the palm bone (measured; see EatingRecon [GRIP]). Tune it live
            // on the holder GameObjects in Unity Explorer.
            public Vector3 basePos;        // base in the off (left) hand
            public Vector3 baseRot;
            public Vector3 spoonPos;      // utensil in the main (right) hand (HasSpoon only)
            public Vector3 spoonRot;
            public Vector3 foodPos;       // food piece: relative to the spoon holder (HasSpoon) or
            public Vector3 foodRot;       //   the right hand (no spoon)
            public Vector3 capPos;        // drink cap if it has one
            public Vector3 capRot;

            // Arms layer-1 normalizedTime knobs (see EatingRecon [SOUND]/[STATE]). The open
            // timeline, left to right:
            //   pullStartTime --(your physical pull scrubs)--> pullEndTime --(the hand unlocks and the
            //   clip plays on its own)--> openReadyTime (frozen in the finished-open ready pose).
            public float openReadyTime = 0.92f; // STATE_OPEN freeze point = the ready pose (spoon in hand / hand ready to take)
            public float putAwayStartTime = 0.3f; // where in STATE_END the put-away starts (skips the can-hold beat)
            // Where the pull starts in the open state (progress 0 maps here; -1 = wherever the animator froze, about 0).
            // Raise it to skip the clip's draw/reach beat, so the hand starts at the lid. Set via PullStart(def, t).
            public float pullStartTime = -1f;
            // Where the pull ends and the hand unlocks (progress 1 maps here; -1 = openReadyTime). Set it earlier when
            // the end of the clip isn't pull motion (the tushonka spoon grab): the rest plays on its own up to
            // openReadyTime. Set via PullEnd(def, t).
            public float pullEndTime = -1f;
            // The physical open gesture (see OpenGestureKind). Set via OpenWith(def, kind). The drink presets set
            // Tilt for every capped or hinged Bottle, JuiceBottle and SodaCan (capless bottles stay Pull).
            public OpenGestureKind openGesture = OpenGestureKind.Pull;
            // Authored STATE_OPEN normalizedTime of each openSound event, parallel to openSounds, from EatingRecon's
            // [SOUND] log. null = evenly spaced estimates, which never reach the trigger-squeeze range. Times past
            // pullEndTime are left to the clip's own events while it plays on. Set via SoundTimes(def, t...).
            public float[] openSoundTimes;

            // deterministicGesture: each take/bite snaps the animator to a fixed (state, time), so every round
            // replays the same segment and the timeline doesn't drift. take = STATE_USE, bite = STATE_EAT.
            public bool deterministicGesture = false;
            public float takePoseTime = 0f;   // STATE_USE normalizedTime each take/grab pulse replays from
            public float bitePoseTime = 0f;   // STATE_EAT normalizedTime each bite pulse replays from

            // ReachBag: the STATE_USE segment the physical reach depth scrubs. Depth 0 = reachStartTime (hand at
            // the bag mouth), full depth = reachDeepTime (hand all the way in); start may be greater than deep. The
            // authored hand dives in over about 0 -> 0.3 (the Take event at 0.014 only makes the food appear, with
            // the hand still outside). The live statics are re-seeded from here every spawn, so tune them mid-eat.
            public float reachStartTime = 0.02f;
            public float reachDeepTime = 0.3f;
            // Baked reach-hand path (ReachBag): like openHandPath, but sampled from the vanilla STATE_USE reach
            // segment, with t = STATE_USE normalizedTime (the axis the reach scrub seeks on). DriveReachLatch replays
            // it at the scrub time; null = a straight line. Set via ReachPath(def, ...floats).
            public float[] reachHandPath;

            // Eat by time instead of bites: hold the item at the mouth and its resource drains over the use-time,
            // like a drink (StepDrink: lowering it pauses, the bar goes down live). bites is unused.
            public bool eatByTime;
            // Arms layer-1 state the timed-eat / gulp loop replays (0 = STATE_USE); sausage's non-standard graph
            // overrides it. It is also the EnterUseState target on such graphs (the finish has to leave STATE_OPEN
            // into a state that exists). Set via Snack's parameter or Timed(def, loop).
            public int eatLoopState;
            // Arms layer-1 state the open plays/scrubs (0 = the archetype default: STATE_OPEN, or STATE_USE for
            // Wrapper foods). Noodles has no STATE_USE, and Play() on a state the graph doesn't have silently does
            // nothing. Set via OpenState(def, hash); EatingRecon's [STATE] lines give the hashes.
            public int openState;
            // Arms state played once for real when the open completes (0 = none), for a visual a scrub can't show:
            // noodles' bag rip is in the early frames of its first eat state (2073176132), not in STATE_OPEN.
            // FinishOpen plays it up to openPlayMaxTime, then goes to Ready. The chew loop (eatLoopState) must be a
            // different state, or every bite rips the bag again. Set via OpenPlay(def, state, maxTime).
            public int openPlayState;
            public float openPlayMaxTime = 1f;
            // A transform whose renderers are hidden when the open completes: the torn-off cover/lid that would
            // otherwise float next to the item once the rip freezes (noodles' Rolton_1st_person_Packet_2_LOD0).
            // Resolved under the item root at spawn, shown again on teardown. null = nothing hidden.
            public string hideOnOpenName;
            // Hide the held item's renderers one by one as it is eaten, stepped at the audible bite
            // sounds and synced to the eaten fraction (the sausage gets shorter).
            public bool segmentedBites;
            // CanSpoon: the can itself can also be held at the mouth and drunk over the item's use-time
            // (condensed milk). The spoon still works; each scoop is capped at what is left.
            public bool mouthDrink;
            // Optional second food piece taken together with the first (sugar takes two cubes at once). It has
            // its own holder and grip on the same hand.
            public string foodPiece2Name;
            public Vector3 food2Pos; public Vector3 food2Rot;

            // Interaction zones = where you reach to trigger each gesture (the grips above are where the prop
            // sits). A zone fires when the acting palm probe is within <radius> of anchor + offset. Probes and
            // anchors sit on the rendered palms (PalmOffsetLocal), so offsets are measured from the holding palm.
            // open/take are anchored on the holding hand; eat checks the eating hand against the head. Set
            // offsets via Zones() for items held off the palm (bottle cap, spout).
            public Vector3 openZoneOffset; public float openZoneRadius = DefaultOpenZoneRadius;
            public Vector3 takeZoneOffset; public float takeZoneRadius = DefaultTakeZoneRadius;
            // The eat zone is anchored at the mouth (head + mouthLocalOffset), not between the eyes.
            public Vector3 eatZoneOffset; public float eatZoneRadius = DefaultEatZoneRadius;

            public bool HasSpoon => !string.IsNullOrEmpty(spoonName);
            public bool HasCap => !string.IsNullOrEmpty(capName);
        }

        // Default zone radii. Used by the FoodDef initializers above and by ZoneArgs' "changed from the default"
        // checks, so DumpFoodDef only prints a radius a food actually overrides.
        private const float DefaultOpenZoneRadius = 0.10f;
        private const float DefaultTakeZoneRadius = 0.10f;
        private const float DefaultEatZoneRadius = 0.08f;

        // ===== Food registry ====================================================
        // Adding a food is one line: pick the archetype factory (or a shape preset that wraps it:
        // SairaCan/BigCan, SodaCan/TetraPak/Bottle) and pass the template id and whatever differs.
        // EatingRecon prints a line ready to paste; DumpFoodDef() prints one with the live tuning.
        //   CanSpoon - can in the left hand, lid roll, spoon scoops with the right (tushonka)
        //   CanHand  - the same without a spoon: the right hand takes the food (sprats)
        //   Wrapper  - bar in the right hand, the left hand peels the wrapper (chocolate)
        //   Bag      - bag in the right hand, shake crackers into the left hand (croutons)
        //   Pack     - pack in the right hand, the left hand takes a piece and eats it (galette)
        //   Drink    - container in one hand, the free hand pops the cap, hold it at the mouth to
        //              drink over the vanilla use-time; capped bottles can be recapped to stop early
        //   ReachBag - pouch in the right hand, the left rips the top, then the left hand reaches
        //              into the bag (latched, the depth scrubs the clip), trigger takes a cracker,
        //              pull it out, eat from the left hand (iskra / MRE)
        // Item held off the palm? Wrap it in Zones(...): Zones(CanHand(...), eatOffset: V(0f, 0.1f, 0f)).
        // A new archetype:
        // 1. add a FoodKind value
        // 2. add a BuildStyle case   ← the gesture behavior (hand roles + hooks)
        // 3. add a Setup* Props method + a LateZeroProps case   ← the prop wiring
        // 4. add a factory

        // Zone offset axes (palm facing up): +X moves the trigger point left, -X right; +Y down, -Y up; +Z forward, -Z back.
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
            PullEnd(PullStart(HandPath(OpenGrip(CanSpoon("67586b7e49c2fa592e0d8ed9", "salad_root", "saira_spoon", "salad_foodpiece",
                canPos: V(-0.115f, -0.025f, -0.014f), canRot: V(79.5f, 243.3f, 321f),
                spoonPos: V(-0.112f, -0.07f, -0.012f), spoonRot: V(22.3f, 153.2f, 138.7f)),
                "salad_cover_root"), SaladOpenPath), 0.21f), 0.6f),
            //--- Bars / bags / packs ----------------------------------------------
            PullStart(HandPath(OpenGrip(Wrapper("544fb6cc4bdc2d34748b456e", "item_slickers_LOD0", "sn_CAT", "sn_cover"), "sn_cover"), ChocolateOpenPath), 0.15f), // Chocolate bar (slickers)
            PullStart(HandPath(OpenGrip(Bag("5751487e245977207e26a315", "bone_upakovka", "bone_suharik_hold"), "bone_ugol"), CroutonOpenPath), 0.31f), // Emelya croutons
            PullStart(HandPath(OpenGrip(Bag("57347d3d245977448f7b7f61", "bone_upakovka", "bone_suharik_hold"), "bone_ugol"), CroutonOpenPath), 0.31f), // Rye croutons
            HandPath(Pack("5448ff904bdc2d6f028b456e", "pack_CAT", null, "item_galette_LOD0"), GaletteOpenPath), // Galette (crackers)
            SoundTimes(PullStart(HandPath(OpenGrip(Wrapper("57505f6224597709a92585a9", "item_alyonka_LOD0", "sn_CAT", "sn_cover",
                barPos: V(-0.122f, -0.059f, -0.028f), barRot: V(19f, 146.5f, 10f),
                coverPos: V(-0.136f, -0.076f, 0.038f), coverRot: V(3.9f, 60.9f, 356.3f)), "sn_cover"), AlyonkaOpenPath), 0.15f), 0.258f),
            PullStart(HandPath(OpenGrip(Bag("57347d90245977448f7b7f65", "bone_upakovka", "bone_suharik_hold", bites: 4,
                bagPos: V(-0.145f, -0.039f, -0.053f), bagRot: V(1.7f, 193.1f, 268.9f)), "bone_oatmeal_ugol_000"), OatmealOpenPath), 0.31f),
            Timed(PullStart(HandPath(Wrapper("65815f0e647e3d7246384e14", "pack_root", null, "pack_cover00", bites: 1,
                barPos: V(-0.125f, -0.084f, -0.041f), barRot: V(345.9f, 190.3f, 107.7f),
                coverPos: V(-0.106f, -0.06f, -0.006f), coverRot: V(296.5f, 345f, 246f)), TarkaOpenPath), 0.000f)),
            SoundTimes(PullStart(HandPath(Zones(CanHand("59e3577886f774176a362503", "item_sugar_box", "sugar_piece_000",
                canPos: V(-0.132f, -0.072f, -0.052f), canRot: V(359.3f, 130.2f, 3.5f),
                foodPos: V(-0.143f, -0.041f, -0.023f), foodRot: V(291.9f, 166.6f, 315.2f),
                food2: "sugar_piece_001", food2Pos: V(-0.137f, -0.039f, -0.012f), food2Rot: V(291.9f, 188.5f, 0.1f),
                glue: "sugar_piecese",
                openSounds: new[] { "Open", "Open1" }),
                openOffset: V(0f, 0f, -0.14f), takeOffset: V(0f, 0f, -0.14f)), SugarOpenPath), 0.24f), 0.233f, 0.606f),
            PullStart(ReachPath(HandPath(Zones(ReachBag("590c5d4b86f774784e1b9c45", "IFR_CAT", "ifr_galette",
                bagPos: V(-0.179f, -0.098f, -0.032f), bagRot: V(49.8f, 92.7f, 265.2f)),
                openOffset: V(0f, 0f, -0.02f), takeOffset: V(0f, 0f, -0.02f), takeRadius: 0.20f), IskraOpenPath), IskraReachPath), 0.2f),
            PullStart(ReachPath(HandPath(Zones(ReachBag("590c5f0d86f77413997acfab", "mre_CAT", "mre_galette", bites: 3,
                bagPos: V(-0.148f, -0.076f, -0.037f), bagRot: V(49.7f, 92.6f, 272.5f)),
                openOffset: V(0f, 0f, -0.02f), takeOffset: V(0f, 0f, -0.02f), takeRadius: 0.20f), MreOpenPath), MreReachPath), 0.2f),
            //--- Drinks (open, then HOLD at the mouth; capped bottles recap to stop) ---
            SodaCan("57514643245977207f2c2d09"),  // Tar Cola
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
            PullStart(HandPath(OpenGrip(CanSpoon("5734773724597737fd047c14", "saira_root", null, null, bites: 2,
                canPos: V(-0.125f, -0.056f, 0.02f), canRot: V(358.3f, 326.2f, 73.1f), eatSound: "Drink"), "saira_key"), CondMilkOpenPath), 0.21f),
            PullStart(HandPath(Bottle("62a09f32621468534a797acb", // Pevko Beer
                position: V(-0.113f, -0.065f, 0.064f), rotation: V(6.8f, 349.4f, 278.3f),
                capPos: V(-0.109f, -0.036f, -0.049f), capRot: V(280.1f, 87.5f, 39.1f)), BeerOpenPath), 0.35f),
            PullStart(HandPath(OpenGrip(Bottle("60098b1705871270cd5352a1", cap: false, // Emergency water ration
                position: V(-0.09f, -0.092f, -0.005f), rotation: V(14.6f, 44.2f, 56.9f)), "ration3"), RationOpenPath), 0.27f),
            PullStart(HandPath(Bottle("5d40407c86f774318526545a", // Vodka
                position: V(-0.114f, -0.064f, 0.062f), rotation: V(7.6f, 349.2f, 278f),
                capPos: V(-0.111f, -0.04f, -0.043f), capRot: V(280f, 85.4f, 60.4f)), VodkaOpenPath), 0.3f),
            PullStart(HandPath(Bottle("5d403f9186f7743cac3f229b", // Whiskey
                position: V(-0.098f, -0.058f, 0.017f), rotation: V(341.6f, 347.4f, 287.6f),
                capPos: V(-0.111f, -0.04f, -0.044f), capRot: V(279.6f, 85.1f, 71.6f)), WhiskeyOpenPath), 0.44f),
            PullStart(HandPath(Bottle("5e8f3423fd7471236e6e3b64", // Kvas (same plastic-bottle shape as the water bottle)
                position: V(-0.128f, -0.051f, -0.03f), rotation: V(13.2f, 353.2f, 248.1f),
                capPos: V(-0.121f, -0.042f, -0.052f), capRot: V(283.4f, 36.7f, 169.3f)), KvasOpenPath), 0.29f),
            OpenWith(PullStart(HandPath(Drink("5c0fa877d174af02a012e1cf", "fb_root", null, // Aquamari filter bottle
                drinkPos: V(-0.073f, -0.059f, -0.091f), drinkRot: V(358.7f, 335.1f, 338.7f),
                holdInOffHand: true), AquamariOpenPath), 0.33f),
                OpenGestureKind.Tilt),
            PullStart(HandPath(OpenGrip(Bottle("5d1b376e86f774252519444e", hingedCap: "hinge", // Moonshine
                position: V(-0.116f, -0.065f, 0.062f), rotation: V(7.6f, 349.6f, 278.2f)), "cap"), MoonshineOpenPath), 0.27f),
            PullStart(HandPath(Zones(Bottle("5d1b33a686f7742523398398", // Superwater
                position: V(-0.021f, -0.175f, 0.067f), rotation: V(300.7f, 264f, 89.6f),
                capPos: V(-0.118f, -0.048f, -0.006f), capRot: V(276.7f, 118.7f, 315.4f)), 
                openOffset: V(0f, 0f, 0.1f), takeOffset: V(0f, 0f, 0.1f)), SuperwaterOpenPath), 0.31f),
            PullStart(HandPath(Bottle("5448fee04bdc2dbc018b4567", // Water bottle (60/60 — drinks down partially)
                position: V(-0.128f, -0.051f, -0.031f), rotation: V(13.3f, 353f, 248f),
                capPos: V(-0.121f, -0.043f, -0.053f), capRot: V(283.4f, 35.5f, 162.8f)), WaterBottleOpenPath), 0.29f),         
            OpenWith(SoundTimes(PullStart(HandPath(Drink("5bc9b156d4351e00367fbce9", "mayo_root", "mayo_cap",
                drinkPos: V(-0.109f, -0.055f, 0.026f), drinkRot: V(358.6f, 199.1f, 301.5f),
                capPos: V(-0.124f, -0.04f, 0.035f), capRot: V(274.2f, 79.4f, 303.5f)), MayoOpenPath), 0.11f), 0.358f), OpenGestureKind.Tilt),                    
            Snack("635a758bfefc88a93f021b8a", "bone_item_food_sausage"),          
        };

        // --- Canned-food shapes ------------------------------------------------
        // Every small can uses the saira rig (root/spoon/foodpiece); big cans also move the reach zones out for
        // the larger body. PullStart 0.21 = the path key where the hand reaches the can (the lid-roll "Open" sounds
        // begin at ~0.23). PullEnd 0.6 = the lid is fully rolled: the SairaOpenPath keys past ~0.6 are the clip's
        // reach for the spoon, which plays on its own up to openReadyTime after the hand unlocks.
        private static FoodDef SairaCan(string id, int bites = 3)
            => PullEnd(PullStart(HandPath(OpenGrip(CanSpoon(id, "saira_root", "saira_spoon", "saira_foodpiece", bites: bites), "saira_key"), SairaOpenPath), 0.21f), 0.6f);

        private static FoodDef BigCan(string id, int bites = 3)
            => PullEnd(PullStart(HandPath(OpenGrip(Zones(CanSpoon(id, "saira_root", "saira_spoon", "saira_foodpiece", bites: bites, bigCan: true),
                openOffset: V(0f, 0f, 0.05f), takeOffset: V(0f, 0f, 0.05f)), "saira_key"), SairaOpenPath), 0.21f), 0.6f);

        // --- Drink container shapes --------------------------------------------
        // One preset per container model, holding that shape's prop names and measured grips, so the registry stays
        // one short line per drink. A bottle that needs its own grip passes pos/rot; a drink the vanilla
        // animation holds in the left hand passes holdInOffHand: true (the cap hand switches too).
        private static FoodDef SodaCan(string id,
            Vector3? position = null, Vector3? rotation = null, bool holdInOffHand = false, float drinkSeconds = 0f)
            => OpenWith(PullEnd(PullStart(HandPath(Drink(id, "tc_root", null, position, rotation, holdInOffHand: holdInOffHand, drinkSeconds: drinkSeconds), SodaOpenPath), 0.16f), 0.4f),
                OpenGestureKind.Tilt); // the tab opens by tilting the hand (like aquamari). Not the Trigger kind:
                                       // it sweeps the scrub in one frame and teleports the latched hand

        // TetraPak carton: held in the right hand, no cap (you tear the corner). Default grip =
        // milk's measured tetrapak_root in the right palm (the same for every carton).
        private static FoodDef TetraPak(string id,
            Vector3? position = null, Vector3? rotation = null, bool holdInOffHand = false, float drinkSeconds = 0f)
            => PullStart(HandPath(Zones(Drink(id, "tetrapak_root", null,
                position ?? V(-0.102f, -0.071f, 0.058f), rotation ?? V(78.6f, 280.7f, 351.4f),
                holdInOffHand: holdInOffHand, drinkSeconds: drinkSeconds),
                openOffset: V(0f, 0f, -0.05f), takeOffset: V(0f, 0f, -0.05f))
                , TetraPakOpenPath), 0.08f);

        // Juice bottle (apple, vita and grandma's use the same rig): held in the right hand, the screw
        // cap (juice_cap, under juice_root) comes off into the left hand. Default grips = the three
        // juices' measured grips (all within ~1 mm of each other).
        private static FoodDef JuiceBottle(string id,
            Vector3? position = null, Vector3? rotation = null, Vector3? capPos = null, Vector3? capRot = null)
            => PullStart(HandPath(OpenWith(Zones(Drink(id, "juice_root", "juice_cap",
                position ?? V(-0.099f, -0.061f, 0.094f), rotation ?? V(358.6f, 199.2f, 301.3f),
                capPos ?? V(-0.131f, -0.044f, 0.031f), capRot ?? V(281.5f, 280.8f, 15.8f),
                holdInOffHand: false), openOffset: V(0f, 0f, -0.05f), takeOffset: V(0f, 0f, -0.05f)), OpenGestureKind.Tilt), // screw cap: tilt the hand open
                JuiceOpenPath), 0.09f);

        // Screw-cap bottle: vanilla holds it in the left hand and unscrews the cap with the right, so
        // holdInOffHand defaults to true here. Each bottle shape passes its own measured grips. hingedCap = a
        // flip-top group (e.g. moonshine's "hinge") that stays attached to the bottle instead of detaching
        // (overrides `cap`).
        private static FoodDef Bottle(string id, bool cap = true, string hingedCap = null,
            Vector3? position = null, Vector3? rotation = null, Vector3? capPos = null, Vector3? capRot = null,
            bool holdInOffHand = true, float drinkSeconds = 0f)
        {
            FoodDef foodDef = Zones(Drink(id, "mod_item", hingedCap ?? (cap ? "cap" : null),
                position, rotation, capPos, capRot, holdInOffHand, drinkSeconds), 
                openOffset: V(0f, 0f, 0.05f), takeOffset: V(0f, 0f, 0.05f));
            if (hingedCap != null) foodDef.capHinged = true;
            // Any cap (screw or hinged flip-top) opens by tilting the hand; a bottle without a cap (the ration
            // pouch) opens with a pull.
            foodDef.openGesture = hingedCap != null || cap ? OpenGestureKind.Tilt : OpenGestureKind.Pull;
            return foodDef;
        }

        private static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        // CanSpoon (tushonka): can in the left hand, lid roll, the spoon scoops with the right, N bites.
        // Defaults = tushonka's measured grips and sounds; override only what differs for a new can.
        // mouthDrink: every CanSpoon can can also be drunk. Hold the can at the mouth and the resource drains over
        // the item's use-time; the spoon still works, and each scoop is capped at what is left.
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
                // Authored event times of the default sounds (tushonka [SOUND]: Open@0.240 Open2@0.394
                // SpoonTake@0.850). "Open" lands inside the trigger squeeze (~0.21-0.29). SpoonTake is past
                // PullEnd 0.6, so the clip plays it itself after the unlock. Only valid for the default sound set.
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

        // Snack (sausage): held in the right hand, the other hand does nothing; skipOpen spawns straight into
        // Ready. The eat is timed (eatByTime): hold it at the mouth and it is eaten over the item's use-time like
        // a drink, hiding one renderer segment per audible bite (segmentedBites; segmentHideFromEnd picks the end).
        // Sausage's clip uses its own state hashes (no shared STATE_USE/END). eatLoopState 412676735 is the second
        // hash EatingRecon logged, a guess for its chew loop. The put-away is done by the ForceCommand cancel.
        private static FoodDef Snack(string id, string root,
            int bites = 1,
            Vector3? barPos = null, Vector3? barRot = null,
            int eatLoopState = 412676735,
            // "Take1" = sausage's bite sound. Its bank has no 'Eat' element: playing 'Eat' silently does nothing,
            // and its throttle then blocks the clip's real 'Take1' events.
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

        // CanHand (sprats): CanSpoon without a spoon; the right hand takes the food. Deterministic poses, so every
        // grab is the same reach -> pinch -> lift (STATE_USE at takePoseTime).
        // food2 = an optional second piece taken together with the first (sugar takes two cubes; the default
        // grips are sugar's measured piece_001). glue = a sibling group in the rig that must move with the held
        // box (sugar's remaining pile, sugar_piecese: it is outside the held root and would stay on the body rig).
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
                // Sprats [SOUND]: Open2@0.547. Open's time wasn't recorded; it is estimated at 0.30 so it lands
                // inside the trigger squeeze (the sprats pull starts at ~0.26).
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

        // Wrapper (chocolate): bar in the right hand, the left peels it. wrapperGroup stays attached to the bar
        // (it carries sub-pieces); cover detaches to the left hand once peeled. barPos/coverPos = the grips of
        // the bar and of the peeled wrapper. Defaults = chocolate.
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
                // Chocolate [SOUND]: the open is one STATE_USE clip, Open@0.259, which lands inside
                // the trigger squeeze (the bar pull starts at ~0.15). Only valid for the default sound.
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

        // Noodles (Rolton): a Wrapper on a non-standard animator graph, with all its settings in one preset.
        //  - Hold the whole bone group pack_CAT, not pack_root: the skinned packet has wrapper bones outside
        //    pack_root that would stay on the body rig and stretch the wrapper. Attach nothing else: child
        //    bones move with the held root.
        //  - Its graph has no STATE_USE: the pull and handling scrub STATE_OPEN (openState), and the
        //    bite/split/eat are in 2073176132 / -893874298.
        //  - The bag-rip visual is in the early frames of the first eat state (2073176132), not STATE_OPEN,
        //    so a scrub can't show it. openPlayState plays it for real when the open completes; the chew loop
        //    uses the other eat state so the bag doesn't rip again.
        //  - hideOnOpenName hides the torn cover/lid mesh after the tear (otherwise it floats next to the bag).
        //  - Timed (hold it at the mouth over the use-time) and segmented (hide the 9 Rolton piece
        //    renderers one per audible bite).
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
            openState = STATE_OPEN_HASH,    // the graph has no STATE_USE, so scrub its STATE_OPEN
            openHandPath = NoodlesOpenPath, // the left palm's rip arc (replayed during the pull)
            pullStartTime = 0.784f,         // tuned live; update it if DumpFoodDef prints a different value
            openPlayState = 2073176132,     // the rip visual is in this state, played for real on open
            openPlayMaxTime = 0.34f,
            eatByTime = true,
            eatLoopState = -893874298,      // chew loop (a different state, so the bag doesn't rip again)
            segmentedBites = true,          // hide the 9 Rolton piece renderers one per bite
            hideOnOpenName = "Rolton_1st_person_Packet_2_LOD0", // the torn cover/lid, hidden after the rip
        };

        // Drink (bottles, cans, cartons): container in one hand (right unless holdInOffHand), the free hand pops
        // the cap (attached to the container until then), then hold it at the mouth: the item's vanilla use-time
        // runs down while it is held. Multi-use bottles (water) can be drunk partly and are only discarded at 0.
        // Capped bottles can be recapped to stop early (free hand back at the cap zone + trigger). No bite counting.
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

        // Bag (croutons): the bag (root, holds everything) in the right hand, the left opens it, then shake it
        // near the left hand to pour the crackers (matched by crackerPrefix) into the left hand, which eats them.
        // bites = shake -> eat rounds. bagPos/crackerPos = the grips of the bag and of the cracker clump.
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
                // measured bag grip (palm -> bone_upakovka, right palm)
                basePos = bagPos ?? V(-0.148f, -0.040f, -0.026f),
                baseRot = bagRot ?? V(355.4f, 204.9f, 251.6f),
                // cracker clump grip, measured in the hand (palm -> ...hold_000, left palm)
                foodPos = crackerPos ?? V(-0.067f, -0.042f, -0.003f),
                foodRot = crackerRot ?? V(8f, 73.1f, 93.4f),
                openReadyTime = openReadyTime,
                putAwayStartTime = putAwayStartTime,
                deterministicGesture = false,
            };

        // Pack (galette): the wrapped pack (root) in the right hand, wrapperGroup attached to it (the cover opens
        // in place, nothing detaches). The left hand opens it, takes the food piece and eats it.
        // The mirror of CanHand. bites = take -> eat rounds. packPos/foodPos = the grips of the pack and the piece.
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
                // measured pack grip (right palm)
                basePos = packPos ?? V(-0.124f, -0.052f, -0.054f),
                baseRot = packRot ?? V(28.2f, 112.6f, 254.7f),
                // piece grip, measured in the hand (left palm)
                foodPos = foodPos ?? V(-0.15f, -0.031f, 0.04f),
                foodRot = foodRot ?? V(38.4f, 27.3f, 286.7f),
                openReadyTime = openReadyTime,
                putAwayStartTime = putAwayStartTime,
                // Deterministic take/bite like CanHand. This also keeps the animator out of STATE_OPEN,
                // so the finish cancels cleanly.
                deterministicGesture = true,
                takePoseTime = takePoseTime,
                bitePoseTime = bitePoseTime,
            };

        // ReachBag (iskra, MRE): the pouch (root, the bone-root group) in the right hand, the left rips the top
        // open (the standard pull-open on STATE_OPEN), then the left hand reaches into the bag. Entering the take
        // zone latches the hand, and the depth scrubs the reach segment of STATE_USE (the bag mouth widens as you
        // push in). The trigger while deep takes the cracker (foodPieceName) into the reaching hand, and pulling
        // back out completes the take; it is eaten from the left hand. Leaving the zone releases the hand.
        // bites = reach -> grab -> eat rounds. Defaults = iskra's values from EatingRecon.
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
                // Iskra/MRE [SOUND]: one Open event in STATE_OPEN (@0.371 / 0.366), inside the
                // physical-pull range with PullStart 0.2. Default sound set only.
                openSoundTimes = openSounds == null ? new[] { 0.37f } : null,
                scoopSound = scoopSound,
                eatSound = eatSound,
                // bag grip = EatingRecon's right-palm average for the bone root (set per food via bagPos/bagRot)
                basePos = bagPos ?? V(-0.179f, -0.098f, -0.032f),
                baseRot = bagRot ?? V(49.8f, 92.7f, 265.2f),
                // The cracker ends up in the left hand, but vanilla holds it in the right (a mirrored
                // food: its measured grip is on the other palm). So start from the galette pack's
                // tuned left-palm piece grip, which uses the same cracker mesh (item_galette_LOD0).
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

        // Names the clip-animated bone the latched hand follows during a pull-open (the lid or pull-key on cans,
        // the peeling cover on the chocolate). With pullSnapToGrip the index fingertip snaps onto it at the grab.
        // Chains onto any factory like Zones: OpenGrip(SairaCan(...), "lid_bone"). Without it the latch freezes
        // where you grabbed. To find the name, complete one pull without it: logOpenMovers logs the bones under
        // the item that moved most.
        // Don't add OpenGrip to drinks with a detachable cap (the cap is pinned to the container until it pops),
        // soda cans, hotrod or aquamari: nothing moves in the scrub range there, so freezing at the grab is right.
        private static FoodDef OpenGrip(FoodDef foodDef, string bone) { foodDef.openGripName = bone; return foodDef; }

        // Baked open-hand path (see FoodDef.openHandPath): paste EatingRecon's [HANDPATH]
        // output. Chains like Zones/OpenGrip, and wins over OpenGrip when both are set.
        private static FoodDef HandPath(FoodDef foodDef, params float[] packed) { foodDef.openHandPath = packed != null && packed.Length >= 8 ? packed : null; return foodDef; }

        // Baked reach-hand path (see FoodDef.reachHandPath, ReachBag only): paste
        // EatingRecon's ReachPath(...) output. Chains like HandPath; null or too short = a straight line.
        private static FoodDef ReachPath(FoodDef foodDef, params float[] packed) { foodDef.reachHandPath = packed != null && packed.Length >= 8 ? packed : null; return foodDef; }

        // Per-food pull start time (see FoodDef.pullStartTime). Chains like the others:
        // PullStart(HandPath(...), 0.21f). Tune it live with the pullStartTime static;
        // DumpFoodDef prints the tuned value.
        private static FoodDef PullStart(FoodDef foodDef, float t) { foodDef.pullStartTime = t; return foodDef; }

        // Per-food pull end time (see FoodDef.pullEndTime): where the hand unlocks from the pull. The rest of
        // the open clip (e.g. the tushonka spoon grab) plays on its own from there to openReadyTime.
        // Chains: PullEnd(PullStart(...), 0.6f). Tune it live with the pullEndTime static; DumpFoodDef prints it.
        private static FoodDef PullEnd(FoodDef foodDef, float t) { foodDef.pullEndTime = t; return foodDef; }

        // Per-food physical open gesture (see OpenGestureKind). Chains like the others:
        // OpenWith(Drink(...), OpenGestureKind.Tilt). The drink presets already set the common
        // cases (capped/hinged Bottle, JuiceBottle, SodaCan = Tilt); use this for
        // one-offs (aquamari's flip sport cap, hotrod's can tab).
        private static FoodDef OpenWith(FoodDef foodDef, OpenGestureKind kind) { foodDef.openGesture = kind; return foodDef; }

        // Eat by time: the food is eaten by holding it at the mouth for the item's use-time, like a drink
        // (it drains live, lowering it pauses), instead of counting bites. Chains like the others:
        // Timed(Wrapper(...)). Snack sets it already. Optional loop = the chew-loop state for non-standard
        // graphs (FoodDef.eatLoopState; 0 keeps STATE_USE).
        private static FoodDef Timed(FoodDef foodDef, int loop = 0) { foodDef.eatByTime = true; if (loop != 0) foodDef.eatLoopState = loop; return foodDef; }

        // Per-food open-state override (see FoodDef.openState). Chains like the others:
        // OpenState(Wrapper(...), 492683391). DumpFoodDef prints it when set, because losing it
        // when pasting over a line silently breaks the open scrub.
        private static FoodDef OpenState(FoodDef foodDef, int hash) { foodDef.openState = hash; return foodDef; }

        // One-shot open animation (see FoodDef.openPlayState): plays `state` for real when the open completes
        // (up to `maxTime`), to show an open visual the scrub can't, such as noodles' rip.
        // Chains: OpenPlay(Wrapper(...), 2073176132, 0.34f). DumpFoodDef prints it.
        private static FoodDef OpenPlay(FoodDef foodDef, int state, float maxTime = 1f) { foodDef.openPlayState = state; foodDef.openPlayMaxTime = maxTime; return foodDef; }

        // Segmented bites for foods other than Snack (see FoodDef.segmentedBites): the held item hides one
        // renderer per audible bite while held at the mouth, synced to the eaten fraction (segmentHideFromEnd
        // picks the end). DumpFoodDef does not print it: keep it in the Defs line by hand.
        private static FoodDef Segmented(FoodDef foodDef) { foodDef.segmentedBites = true; return foodDef; }

        // Per-food authored open-sound times (see FoodDef.openSoundTimes; EatingRecon's [SOUND]
        // lines give them). Chains: SoundTimes(Bag(...), 0.18f, 0.42f). DumpFoodDef does not
        // print it: keep it in the Defs line by hand.
        private static FoodDef SoundTimes(FoodDef foodDef, params float[] times) { foodDef.openSoundTimes = times != null && times.Length > 0 ? times : null; return foodDef; }

        // For EatingRecon: the held-item root bone name and the open-state hash of a known food
        // (the frame and time axis the [HANDPATH] capture must use). null / 0 = no def.
        public static string LookupRootName(string templateId) => FindDef(templateId)?.rootName;
        public static int LookupOpenStateHash(string templateId)
        {
            FoodDef foodDef = FindDef(templateId);
            return foodDef == null ? 0 : BuildStyle(foodDef).openStateHash;
        }
        // ReachBag only: the arms state the reach scrub seeks in (the time axis a
        // ReachPath capture must sample on). 0 = not a ReachBag def, no reach capture.
        public static int LookupReachStateHash(string templateId)
            => FindDef(templateId)?.kind == FoodKind.ReachBag ? STATE_USE_HASH : 0;

        // Moves the reach points of an item held off the palm (bottle cap up high, spout at the mouth). Chains
        // onto any factory: Zones(CanHand(...), eatOffset: V(0f, 0.1f, 0f)). Grips are not changed.
        // Offsets are in the anchor's local space (open/take = holding hand; eat = eating hand against the head).
        private static FoodDef Zones(FoodDef foodDef,
            Vector3? openOffset = null, float? openRadius = null,
            Vector3? takeOffset = null, float? takeRadius = null,
            Vector3? eatOffset = null, float? eatRadius = null)
        {
            if (openOffset.HasValue) foodDef.openZoneOffset = openOffset.Value;
            if (openRadius.HasValue) foodDef.openZoneRadius = openRadius.Value;
            if (takeOffset.HasValue) foodDef.takeZoneOffset = takeOffset.Value;
            if (takeRadius.HasValue) foodDef.takeZoneRadius = takeRadius.Value;
            if (eatOffset.HasValue) foodDef.eatZoneOffset = eatOffset.Value;
            if (eatRadius.HasValue) foodDef.eatZoneRadius = eatRadius.Value;
            return foodDef;
        }

        // ===== Authoring helper ==================================================
        // Call from UnityExplorer while eating, after tuning the holders live. Prints a factory
        // line ready to paste into Defs (grips from the tuned holders, timings from the live knobs).
        // For Wrapper foods, open the wrapper first.
        [Tunable("Eating", TunableKind.Action, "While eating: print the tuned food line to paste into the food list.")]
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
                // Raw factory line: move the tuned grips into the matching shape preset
                // (SodaCan/TetraPak/Bottle) if they should become that shape's defaults.
                core = $"Drink(\"{def.templateId}\", \"{def.rootName}\", {(def.HasCap ? $"\"{def.capName}\"" : "null")}, "
                     + $"drinkPos: {VStr(HolderPos(baseHolder, def.basePos))}, drinkRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + (def.HasCap ? $"capPos: {VStr(HolderPos(capHolder, def.capPos))}, capRot: {VStr(HolderRot(capHolder, def.capRot))}, " : "")
                     + (def.holdInOffHand ? "holdInOffHand: true, " : "")
                     + $"openReadyTime: {FStr(openReadyTime)}, putAwayStartTime: {FStr(putAwayStartTime)})";
            }
            else if (def.kind == FoodKind.ReachBag)
            {
                // Reach times come from the live statics, so live tuning is printed.
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
                // canPos/canRot are the factory's argument names. food2 and glue are printed when present.
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

        // Wrap in OpenState(...) if the food overrides the archetype's open state. Losing it
        // when pasting over a line silently breaks the open scrub (it would Play a state
        // that doesn't exist in that food's graph).
        private static string WrapOpenState(string core)
            => def.openState == 0 ? core : $"OpenState({core}, {def.openState})";

        // Wrap in OpenPlay(...) if the food plays a one-shot open animation (the rip).
        // Reads the live statics, so a tuned openPlayMaxTime is printed.
        private static string WrapOpenPlay(string core)
            => openPlayState == 0 ? core : $"OpenPlay({core}, {openPlayState}, {FStr(openPlayMaxTime)})";

        // Wrap in OpenWith(...) if the food's open gesture isn't the default Pull. This may be
        // redundant when a preset already sets it; that is harmless, drop it when pasting.
        private static string WrapOpenWith(string core)
            => def.openGesture == OpenGestureKind.Pull ? core : $"OpenWith({core}, OpenGestureKind.{def.openGesture})";

        // Wrap in PullStart(...) / PullEnd(...) if set (not -1). Reads the live statics
        // (seeded from the FoodDef), so live tuning is printed.
        private static string WrapPullStart(string core)
            => pullStartTime < 0f ? core : $"PullStart({core}, {FStr(pullStartTime)})";

        private static string WrapPullEnd(string core)
            => pullEndTime < 0f ? core : $"PullEnd({core}, {FStr(pullEndTime)})";

        // Wrap in OpenGrip(...) if the food has a bone for the hand to follow during the pull-open.
        private static string WrapOpenGrip(string core)
            => string.IsNullOrEmpty(def.openGripName) ? core : $"OpenGrip({core}, \"{def.openGripName}\")";

        // Wrap in HandPath(...) if the food has a baked open-hand path (one key per line).
        private static string WrapHandPath(string core) => WrapPath(core, "HandPath", def.openHandPath);

        // Wrap in ReachPath(...) if the food has a baked reach-hand path (ReachBag).
        private static string WrapReachPath(string core) => WrapPath(core, "ReachPath", def.reachHandPath);

        private static string WrapPath(string core, string wrapper, float[] p)
        {
            if (p == null) return core;
            var builder = new StringBuilder($"{wrapper}({core}");
            for (int i = 0; i + 8 <= p.Length; i += 8)
                builder.Append($",\n                {p[i]:0.###}f, {p[i + 1]:0.###}f, {p[i + 2]:0.###}f, {p[i + 3]:0.###}f, {p[i + 4]:0.####}f, {p[i + 5]:0.####}f, {p[i + 6]:0.####}f, {p[i + 7]:0.####}f");
            builder.Append(")");
            return builder.ToString();
        }

        private static string VStr(Vector3 v) => $"V({v.x:0.###}f, {v.y:0.###}f, {v.z:0.###}f)";
        private static string FStr(float f) => $"{f:0.###}f";

        // Wrap in Zones(...) if a zone differs from the default. Reads the live statics (seeded from
        // the FoodDef), so live zone tuning ends up in the printed line.
        private static string WrapZones(string core)
        {
            string z = ZoneArgs();
            return z.Length == 0 ? core : $"Zones({core}{z})";
        }

        private static string ZoneArgs()
        {
            var builder = new StringBuilder();
            if (openZoneOffset != Vector3.zero) builder.Append($", openOffset: {VStr(openZoneOffset)}");
            if (openDistance != DefaultOpenZoneRadius) builder.Append($", openRadius: {FStr(openDistance)}");
            if (takeZoneOffset != Vector3.zero) builder.Append($", takeOffset: {VStr(takeZoneOffset)}");
            if (scoopDistance != DefaultTakeZoneRadius) builder.Append($", takeRadius: {FStr(scoopDistance)}");
            if (eatZoneOffset != Vector3.zero) builder.Append($", eatOffset: {VStr(eatZoneOffset)}");
            if (eatDistance != DefaultEatZoneRadius) builder.Append($", eatRadius: {FStr(eatDistance)}");
            return builder.ToString();
        }

        private static FoodDef FindDef(string templateId)
        {
            if (string.IsNullOrEmpty(templateId)) return null;
            foreach (FoodDef foodDef in Defs) if (foodDef.templateId == templateId) return foodDef;
            return null;
        }
    }
}
