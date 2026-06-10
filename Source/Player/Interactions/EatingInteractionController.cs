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
        private enum FoodKind { CannedFood, Handheld, Bag, Pack, Drink }

        // ===== Gesture archetype descriptor =====================================
        // What the shared open->take->eat loop needs that differs per archetype. Built per food
        // (BuildStyle), so the control flow is written ONCE; a new TYPE is mostly one BuildStyle
        // case + its prop-setup/late-zero tails. The hooks call the existing per-archetype routines.
        private enum Hand { Dominant, Off }        // Dominant = the right hand always; Off = left
        private enum TakeKind { None, HandNear, Shake }

        private sealed class EatStyle
        {
            public string label;          // for logs ("can"/"wrapper"/"bag"/"pack")
            public Hand openHand;         // hand that performs the open gesture
            public Hand eatHand;          // hand that goes to the mouth
            public Hand takeHand;         // hand that performs a HandNear take (unused for Shake/None)
            public bool hasTakeStep;      // false = open then eat directly (Handheld)
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
            // Handheld/Pack: wrapperName = a group glued to the held item (carries sub-pieces like
            // the chocolate); coverName = the wrapper mesh that DETACHES to the off hand once
            // peeled. Either can be null.
            public string wrapperName;
            public string coverName;
            public int bites = 3;

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

            // Arms layer-1 normalizedTime knobs (see EatingRecon [SOUND]/[STATE]).
            public float openHoldTime = 0.92f; // freeze STATE_OPEN here (lid rolled / spoon grabbed)
            public float endStartTime = 0.3f;  // where in STATE_END to start the put-away

            // deterministicGesture: each grab/eat SNAPS the animator to a fixed (state, time) so
            // every bite replays the SAME segment — else the timeline drifts and each bite looks
            // different (the sprats symptom). grab=STATE_USE, eat=STATE_EAT.
            public bool deterministicGesture = false;
            public float grabPoseTime = 0f;   // STATE_USE normalizedTime the grab pulse starts from
            public float eatPoseTime = 0f;    // STATE_EAT normalizedTime the eat pulse starts from

            // Interaction zones = WHERE you reach to trigger each gesture (distinct from the grips
            // above = where the prop SITS). Fires when the acting hand is within <radius> of an
            // anchor + offset. Defaults (0 offset + the radius) = the original hand-to-hand /
            // hand-to-mouth triggers; set offsets via Zones() for off-palm items (bottle cap,
            // spout). open/take anchor on the holding hand; eat on the eating hand vs the head.
            public Vector3 openZoneOffset; public float openZoneRadius = 0.18f;
            public Vector3 takeZoneOffset; public float takeZoneRadius = 0.18f;
            public Vector3 eatZoneOffset; public float eatZoneRadius = 0.23f;

            public bool HasSpoon => !string.IsNullOrEmpty(spoonName);
            public bool HasCap => !string.IsNullOrEmpty(capName);
        }

        // ===== Food registry ====================================================
        // Add a food = one line: pick the archetype factory, pass the template id + prop names.
        // Use EatingRecon for a paste-ready line, then DumpFoodDef() to bake in-headset tuning.
        //   CanSpoon — can in left hand, lid roll, spoon scoops in right (tushonka)
        //   CanHand  — same, no spoon: grab the food by the right hand (sprats)
        //   Wrapper  — bar in right hand, left hand peels the wrapper (chocolate)
        //   Bag      — bag in right hand, shake crackers into the left hand (croutons)
        //   Pack     — pack in right hand, left hand takes a piece and eats it (galette)
        // Off-palm item? Wrap in Zones(...): Zones(CanHand(...), eatOffset: V(0f, 0.1f, 0f)).
        // 1. add a FoodKind value
        // 2. add a BuildStyle case   ← the gesture behavior(hand roles + hooks)
        // 3. add a Setup* Props method + a LateZeroProps case   ← the prop wiring
        // 4. add a factory

        // Zones - X: moves trigger points to left+/right- (palm facing up), Y: Moves trigger point up-/down+, Z: moves trigger point forward+/back-
        private static readonly FoodDef[] Defs =
        {
            CanSpoon("57347d7224597744596b4e72", "saira_root", "saira_spoon", "saira_foodpiece"), // Tushonka (small can)
            CanSpoon("5673de654bdc2d180f8b456d", "saira_root", "saira_spoon", "saira_foodpiece"), // Saury
            CanSpoon("57347d5f245977448b40fa81", "saira_root", "saira_spoon", "saira_foodpiece"), // Humpback salmon
            CanSpoon("57347d9c245977448b40fa85", "saira_root", "saira_spoon", "saira_foodpiece"), // Herring
            CanSpoon("69774bb0a247161ff1068335", "saira_root", "saira_spoon", "saira_foodpiece"), // Duck Pate
            Zones(CanSpoon("57347da92459774491567cf5", "saira_root", "saira_spoon", "saira_foodpiece", bigCan: true), 
                takeOffset: V(-0.15f, -0.1f, 0f), openOffset: V(-0.15f, -0.1f, 0f)), // Tushonka (big can)
            Zones(CanSpoon("57347d692459774491567cf1", "saira_root", "saira_spoon", "saira_foodpiece", bigCan: true), 
                takeOffset: V(-0.15f, -0.1f, 0f), openOffset: V(-0.15f, -0.1f, 0f)), // Peas
            Zones(CanSpoon("57347d8724597744596b4e76", "saira_root", "saira_spoon", "saira_foodpiece", bigCan: true), 
                takeOffset: V(-0.15f, -0.1f, 0f), openOffset: V(-0.15f, -0.1f, 0f)), // Squash
            CanHand("5bc9c29cd4351e003562b8a3", "sprats_root", "sprats_foodpiece"), // sprats
            Wrapper("544fb6cc4bdc2d34748b456e", "item_slickers_LOD0", "sn_CAT", "sn_cover"), // Chocolate bar (slickers)
            Drink("60b0f93284c20f0feb453da7", "tc_root", null), // Rat Cola
            Drink("5751435d24597720a27126d1", "tc_root", null), // Max Energy
            Drink("575062b524597720a31c09a1", "tc_root", null), // Green tea
            Drink("5751496424597720a27126da", "hr_root", null), // Hotrod
            Drink("544fb62a4bdc2dfb738b4568", "tetrapak_root", null), // Pineapple Juice
            Drink("575146b724597720a27126d5", "tetrapak_root", null), // Milk
            Drink("62a09f32621468534a797acb", "mod_item", "cap"), // Pevko Beer
            Drink("60098b1705871270cd5352a1", "mod_item", null), // Emergency water ration
            Drink("5d40407c86f774318526545a", "mod_item", "cap"), // Vodka
            Drink("5d403f9186f7743cac3f229b", "mod_item", "cap"), // Whiskey
            Drink("5d1b376e86f774252519444e", "mod_item", "cap"), // Moonshine
            Drink("5d1b33a686f7742523398398", "mod_item", "cap"), // Superwater
            Drink("5734773724597737fd047c14", "saira_root", null), // Condensed milk
            Bag("5751487e245977207e26a315", "bone_upakovka", "bone_suharik_hold"), // Emelya croutons
            Bag("57347d3d245977448f7b7f61", "bone_upakovka", "bone_suharik_hold"), // Rye croutons
            // Held root is the bone group pack_CAT (drives the skinned mesh + holds everything),
            // NOT item_galettte_pack_LOD0 (a SkinnedMeshRenderer whose transform is a phantom).
            Pack("5448ff904bdc2d6f028b456e", "pack_CAT", null, "item_galette_LOD0"), // Galette (crackers)
        };

        private static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        // CanSpoon (tushonka): can in LEFT hand, lid roll, spoon scoops in RIGHT, N bites.
        // Defaults = tushonka's measured grips/sounds; override only what differs for a new can.
        private static FoodDef CanSpoon(string id, string root, string spoon, string food,
            int bites = 3,
            bool bigCan = false,
            Vector3? canPos = null, Vector3? canRot = null,
            Vector3? spoonPos = null, Vector3? spoonRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            float openHoldTime = 0.92f, float endStartTime = 0.3f,
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
                scoopSound = scoopSound,
                eatSound = eatSound,
                basePos = canPos ?? (bigCan ? V(-0.126f, -0.058f, 0.021f) : V(-0.1135f, -0.0298f, -0.0034f)),
                baseRot = canRot ?? (bigCan ? V(357.8f, 326.6f, 73.4f) : V(80.72f, 248.34f, 303.57f)),
                spoonPos = spoonPos ?? V(-0.1247f, -0.0537f, -0.0113f),
                spoonRot = spoonRot ?? V(40.67f, 194.91f, 210.26f),
                foodPos = foodPos ?? V(0f, 0.05f, 0.007f),
                foodRot = foodRot ?? Vector3.zero,
                openHoldTime = openHoldTime,
                endStartTime = endStartTime,
                deterministicGesture = false,
            };

        // CanHand (sprats): CanSpoon with no spoon — grab the food by the RIGHT hand. Deterministic
        // poses so every grab is the same clean reach->pinch->lift (STATE_USE@grabPoseTime).
        private static FoodDef CanHand(string id, string root, string food,
            int bites = 3,
            Vector3? canPos = null, Vector3? canRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            float openHoldTime = 0.9f, float endStartTime = 0.3f,
            float grabPoseTime = 0.11f, float eatPoseTime = 0.9f,
            string[] openSounds = null, string scoopSound = "Take", string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.CannedFood,
                rootName = root,
                spoonName = null,
                foodPieceName = food,
                bites = bites,
                drawSound = "Draw",
                openSounds = openSounds ?? new[] { "Open", "Open2" },
                scoopSound = scoopSound,
                eatSound = eatSound,
                basePos = canPos ?? V(-0.106f, -0.013f, 0f),
                baseRot = canRot ?? V(80.2f, 248.9f, 303.5f),
                foodPos = foodPos ?? V(-0.1247f, -0.0537f, -0.0113f),
                foodRot = foodRot ?? V(40.67f, 194.91f, 210.26f),
                openHoldTime = openHoldTime,
                endStartTime = endStartTime,
                deterministicGesture = true,
                grabPoseTime = grabPoseTime,
                eatPoseTime = eatPoseTime,
            };

        // Wrapper (chocolate): bar in RIGHT hand, LEFT peels it. wrapperGroup stays glued to the bar
        // (carries sub-pieces); cover DETACHES to the left hand once peeled. barPos/coverPos = the
        // bar's / peeled wrapper's grips. Defaults = chocolate.
        private static FoodDef Wrapper(string id, string root, string wrapperGroup, string cover,
            int bites = 1,
            Vector3? barPos = null, Vector3? barRot = null,
            Vector3? coverPos = null, Vector3? coverRot = null,
            float openHoldTime = 0.9f, float endStartTime = 0.3f,
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
                scoopSound = null,
                eatSound = eatSound,
                basePos = barPos ?? V(-0.121f, -0.037f, -0.054f),
                baseRot = barRot ?? V(57.7f, 110.3f, 258.6f),
                foodPos = coverPos ?? V(-0.137f, -0.078f, 0.04f),
                foodRot = coverRot ?? V(27.4f, 105.2f, 354.7f),
                openHoldTime = openHoldTime,
                endStartTime = endStartTime,
                deterministicGesture = false,
            };

        private static FoodDef Drink(string id, string root, string cap,
            int bites = 1,
            Vector3? drinkPos = null, Vector3? drinkRot = null,
            Vector3? capPos = null, Vector3? capRot = null,
            float openHoldTime = 0.9f, float endStartTime = 0.3f,
            string[] openSounds = null, string eatSound = "Drink") =>
            new FoodDef
            {
                templateId = id,
                kind = FoodKind.Drink,
                rootName = root,
                capName = cap,
                spoonName = null,
                foodPieceName = null,
                bites = bites,
                drawSound = "Draw",
                openSounds = openSounds ?? new[] { "Open" },
                scoopSound = null,
                eatSound = eatSound,
                basePos = drinkPos ?? V(-0.106f, -0.055f, 0.039f),
                baseRot = drinkRot ?? V(0.1f, 193.2f, 294.8f),
                foodPos = capPos ?? V(-0.137f, -0.078f, 0.04f),
                foodRot = capRot ?? V(27.4f, 105.2f, 354.7f),
                openHoldTime = openHoldTime,
                endStartTime = endStartTime,
                deterministicGesture = false,
            };

        // Bag (croutons): bag (root, holds everything) in RIGHT hand, LEFT opens it, then SHAKE
        // near the left hand to pour the crackerPrefix-matched crackers into the LEFT hand, eaten
        // left-handed. bites = shake->eat rounds. bagPos/crackerPos = the bag's / clump's grips.
        private static FoodDef Bag(string id, string root, string crackerPrefix,
            int bites = 2,
            Vector3? bagPos = null, Vector3? bagRot = null,
            Vector3? crackerPos = null, Vector3? crackerRot = null,
            float openHoldTime = 0.85f, float endStartTime = 0.3f,
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
                openHoldTime = openHoldTime,
                endStartTime = endStartTime,
                deterministicGesture = false,
            };

        // Pack (galette): wrapped pack (root) in RIGHT hand, wrapperGroup glued to it (cover opens
        // in place, no detach). LEFT opens it, takes the food piece into the LEFT hand, eats there.
        // Mirror of CanHand. bites = take->eat rounds. packPos/foodPos = the pack's / piece's grips.
        private static FoodDef Pack(string id, string root, string wrapperGroup, string food,
            int bites = 2,
            Vector3? packPos = null, Vector3? packRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            float openHoldTime = 0.9f, float endStartTime = 0.3f,
            float grabPoseTime = 0.3f, float eatPoseTime = 1f,
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
                foodPos = foodPos ?? V(-0.15f, -0.031f, 0.033f),
                foodRot = foodRot ?? V(38.4f, 27.3f, 286.7f),
                openHoldTime = openHoldTime,
                endStartTime = endStartTime,
                // Deterministic grab/eat like CanHand; also keeps the animator out of STATE_OPEN
                // so the finish cancels cleanly.
                deterministicGesture = true,
                grabPoseTime = grabPoseTime,
                eatPoseTime = eatPoseTime,
            };

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
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)})";
            }
            else if (def.kind == FoodKind.Pack)
            {
                core = $"Pack(\"{def.templateId}\", \"{def.rootName}\", \"{def.wrapperName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"packPos: {VStr(HolderPos(baseHolder, def.basePos))}, packRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)})";
            }
            else if (def.kind == FoodKind.Handheld)
            {
                core = $"Wrapper(\"{def.templateId}\", \"{def.rootName}\", \"{def.wrapperName}\", \"{def.coverName}\", bites: {def.bites}, "
                     + $"barPos: {VStr(HolderPos(baseHolder, def.basePos))}, barRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"coverPos: {VStr(HolderPos(foodHolder, def.foodPos))}, coverRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)})";
            }
            else if (def.HasSpoon)
            {
                core = $"CanSpoon(\"{def.templateId}\", \"{def.rootName}\", \"{def.spoonName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"canPos: {VStr(HolderPos(baseHolder, def.basePos))}, canRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"spoonPos: {VStr(HolderPos(spoonHolder, def.spoonPos))}, spoonRot: {VStr(HolderRot(spoonHolder, def.spoonRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)})";
            }
            else
            {
                core = $"CanHand(\"{def.templateId}\", \"{def.rootName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"basePos: {VStr(HolderPos(baseHolder, def.basePos))}, baseRot: {VStr(HolderRot(baseHolder, def.baseRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)}, "
                     + $"grabPoseTime: {FStr(grabPoseTime)}, eatPoseTime: {FStr(eatPoseTime)})";
            }
            Plugin.MyLog.LogInfo("[ManualEat] === paste into Defs (tuned) ===\n            " + WrapZones(core) + ",");
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
            if (eatDistance != 0.23f) sb.Append($", eatRadius: {FStr(eatDistance)}");
            return sb.ToString();
        }

        //--- Tunables (public so they can be A/B'd live in the headset) -------------
        public static bool enableManualEating = VRSettings.GetManualEating();

        // Gesture trigger radii (metres); generous since the prop sits offset from the controller.
        // Seeded per-food on spawn (= the zone radii). mouthForwardDot gates the eat to in-front.
        public static float openDistance = 0.18f;
        public static float scoopDistance = 0.18f;
        public static float eatDistance = 0.23f;
        public static float mouthForwardDot = -0.2f;

        // Reach offsets (anchor-local), seeded from the FoodDef each spawn; public for live A/B.
        // Zero = the original hand-to-hand / hand-to-mouth triggers. Radii are the statics above.
        public static Vector3 openZoneOffset = Vector3.zero;
        public static Vector3 takeZoneOffset = Vector3.zero;
        public static Vector3 eatZoneOffset = Vector3.zero;

        // Bag shake-to-pour: the bag must be within shakeNearDistance of the left hand, and you
        // wiggle it — shakeReversalsNeeded head-relative velocity reversals (> shakeMinSpeed)
        // within shakeWindow seconds pour the crackers (head-relative so walking doesn't count).
        public static float shakeNearDistance = 0.60f;
        public static float shakeMinSpeed = 0.2f;     // m/s, head-relative
        public static int shakeReversalsNeeded = 1;
        public static float shakeWindow = 1.2f;

        public static bool eatingHaptics = true;
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
        private static Transform medsBody;                // the meds controller object (pinned to the ribcage); rig body anchor
        private static Renderer spoonR, foodR, capR;
        private static BaseSoundPlayer soundPlayer;
        // Holders sit between hand and prop: the animator still writes the prop's local (we zero
        // it in LateUpdate) but never the holder, so the holder is the clean, tunable hold offset.
        private static GameObject baseHolder, spoonHolder, foodHolder, capHolder;

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
        private static bool reparented;

        private static bool effectFired;
        private static MedsController.ObservedMedsControllerClass pendingOp;
        private static float prevTriggerAxis;
        private static float prevOffTriggerAxis; // off-hand trigger edge (handheld wrapper-open)
        private static bool playingManualSound; // true while WE call OnSound (so it's not suppressed)
        private static bool playingOpen;        // playing the STATE_OPEN lid-roll segment

        // Arms layer-1 states, shared across foods (confirmed in recon).
        private const int STATE_OPEN_HASH = 492683391;  // draw -> roll lid -> [grab spoon]
        private const int STATE_USE_HASH = -735675743;  // scoop / take (the grab motion)
        private const int STATE_EAT_HASH = 719885042;   // bite
        private const int STATE_END_HASH = -1014941517; // put-away

        // Per-gesture pose determinism (loaded from the FoodDef on spawn; public for live A/B).
        // See FoodDef.deterministicGesture + StartGesturePulse. *State = the replayed segment,
        // *Time = where in it the pulse starts.
        public static bool deterministicGesturePose = false;
        public static int grabPoseState = STATE_USE_HASH;
        public static float grabPoseTime = 0f;
        public static int eatPoseState = STATE_EAT_HASH;
        public static float eatPoseTime = 0f;
        // Log each grab/eat pulse's starting arms state+normalizedTime (to find the exact
        // "perfect" segment to bake into grabPoseTime/eatPoseTime).
        public static bool logGesturePose = true;
        // Active normalizedTime to freeze STATE_OPEN at — set from the current FoodDef on
        // spawn; public so it can still be A/B'd live for the current eat.
        public static float openHoldTime = 0.92f;
        // Last bite: auto-fire the trigger/fire cancel (fast put-away + weapon draw).
        // Off = wait out the operation's use-time.
        public static bool cancelToFinish = true;
        // Last bite: jump the animator straight to STATE_END instead of playing out STATE_EAT.
        public static bool skipLastBiteAnim = true;
        // Where in STATE_END to start (skips the can-hold bite at the start). Seeded per-food, A/B live.
        public static float endStartTime = 0.3f;
        // Each scoop/eat gesture plays the animation forward this long (finger/hand
        // motion) then freezes. Set 0 to keep bites fully frozen.
        public static float bitePlayTime = 0.5f;
        private static float playUntil;        // animation plays (speed>0) while Time.time < this
        private static bool cancelAction = false;

        // Used by the UpdateLeftHand prefix to know when to keep the left hand live.
        public static bool ManualActive => active && !manualDone;

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
                openHoldTime = d.openHoldTime;   // load per-food timings (still A/B-able live)
                endStartTime = d.endStartTime;
                deterministicGesturePose = d.deterministicGesture;
                grabPoseTime = d.grabPoseTime;
                eatPoseTime = d.eatPoseTime;
                grabPoseState = STATE_USE_HASH;  // segments are shared; reset in case A/B'd
                eatPoseState = STATE_EAT_HASH;
                // Interaction-zone reach points (radii into the existing distance statics,
                // offsets into the new ones) — same A/B-able-live seeding as the timings above.
                openDistance = d.openZoneRadius; scoopDistance = d.takeZoneRadius; eatDistance = d.eatZoneRadius;
                openZoneOffset = d.openZoneOffset; takeZoneOffset = d.takeZoneOffset; eatZoneOffset = d.eatZoneOffset;
                style = BuildStyle(d.kind); // per-archetype gesture descriptor
                phase = Phase.Closed;
                biteCount = 0;
                spawnAnimSpeed = animationSpeed <= 0f ? 1f : animationSpeed;

                Plugin.MyLog.LogInfo($"[ManualEat] Armed manual sequence for {d.templateId}.");
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[ManualEat] OnSpawnPre error: {ex}");
                Reset();
            }
        }

        // POSTFIX on Spawn: props are built now — find them, freeze, REPARENT to hands.
        public static void OnSpawnPost(MedsController instance)
        {
            if (!active || instance != controller) return;
            try
            {
                instance.FirearmsAnimator?.SetAnimationSpeed(0f);

                Transform root = instance._controllerObject != null ? instance._controllerObject.transform : null;
                if (root == null) { Reset(); return; }
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
                        : def.kind == FoodKind.Pack ? SetupPackProps(root, rightHandBone)
                        : SetupCannedProps(root, leftHandBone, rightHandBone);
                if (!ok) return; // failure already logged + Reset

                if (driveHandsToTargets) SubscribePinAfterIk();

                Plugin.MyLog.LogInfo($"[ManualEat] Props reparented to hands (kind={def.kind}).");
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[ManualEat] OnSpawnPost error: {ex}");
                End();
            }
        }

        // Canned (tushonka, sprats): can in the LEFT hand; spoon (if any) + food in the RIGHT.
        // Food hangs off the spoon holder (HasSpoon) or the right hand. False+Reset if a prop's missing.
        private static bool SetupCannedProps(Transform root, Transform leftHandBone, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);
            foodT = FindDeep(root, def.foodPieceName);
            spoonT = def.HasSpoon ? FindDeep(root, def.spoonName) : null;

            if (baseT == null || foodT == null || (def.HasSpoon && spoonT == null))
            {
                Plugin.MyLog.LogError($"[ManualEat] Missing props (base={baseT != null} spoon={(def.HasSpoon ? (spoonT != null).ToString() : "n/a")} food={foodT != null}) — vanilla fallback.");
                Reset();
                return false;
            }

            spoonR = spoonT != null ? spoonT.GetComponentInChildren<Renderer>(true) : null;
            foodR = foodT.GetComponentInChildren<Renderer>(true);

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (spoonT != null) Save(spoonT, out spoonParent0, out spoonPos0, out spoonRot0);
            Save(foodT, out foodParent0, out foodPos0, out foodRot0);

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

            baseT.SetParent(baseHolder.transform, false);
            if (spoonT != null) spoonT.SetParent(spoonHolder.transform, false);
            foodT.SetParent(foodHolder.transform, false);
            reparented = true;

            SetRenderer(spoonR, false); // appears on "open" (no-op if no spoon)
            SetRenderer(foodR, false);  // appears on "scoop"/grab
            // (Draw/Open/Open2[/SpoonTake] fire from the STATE_OPEN segment itself.)
            return true;          
        }

        private static bool SetupDrinkProps(Transform root, Transform leftHandBone, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);
            capT = def.HasCap ? FindDeep(root, def.capName) : null;

            if (baseT == null || (def.HasCap && capT == null))
            {
                Plugin.MyLog.LogError($"[ManualEat] Missing props (base={baseT != null} cap={(def.HasCap ? (capT != null).ToString() : "n/a")}");
                Reset();
                return false;
            }

            capR = capT != null ? capT.GetComponentInChildren<Renderer>(true) : null;

            Save(baseT, out baseParent0, out basePos0, out baseRot0);
            if (capT != null) Save(capT, out capParent0, out capPos0, out capRot0);

            baseHolder = NewHolder("EatBaseHolder", rightHandBone, def.basePos, def.baseRot);

            if (def.HasCap)
                capHolder = NewHolder("EatCapHolder", leftHandBone, def.capPos, def.capRot);
            else
                capHolder = null;

            baseT.SetParent(baseHolder.transform, false);
            if (capT != null) capT.SetParent(capHolder.transform, false);
            reparented = true;

            SetRenderer(capR, false); // appears on "open" (no-op if no cap)
            // (Draw/Open/Open2[/CapTake] fire from the STATE_OPEN segment itself.)
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
            {
                Plugin.MyLog.LogError($"[ManualEat] Handheld missing bar '{def.rootName}' — vanilla fallback.");
                Reset();
                return false;
            }

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
            {
                Plugin.MyLog.LogError($"[ManualEat] Bag missing root '{def.rootName}' — vanilla fallback.");
                Reset();
                return false;
            }

            var found = new System.Collections.Generic.List<Transform>();
            FindAllByPrefix(root, def.foodPieceName, found);
            int n = found.Count;
            if (n == 0)
            {
                Plugin.MyLog.LogError($"[ManualEat] Bag found no crackers with prefix '{def.foodPieceName}' — vanilla fallback.");
                Reset();
                return false;
            }
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
        private static bool SetupPackProps(Transform root, Transform rightHandBone)
        {
            baseT = FindDeep(root, def.rootName);                                            // the pack (held)
            wrapperT = string.IsNullOrEmpty(def.wrapperName) ? null : FindDeep(root, def.wrapperName);
            foodT = FindDeep(root, def.foodPieceName);                                      // the piece to take
            if (baseT == null || foodT == null)
            {
                Plugin.MyLog.LogError($"[ManualEat] Pack missing prop (pack={baseT != null} food={foodT != null}) — vanilla fallback.");
                Reset();
                return false;
            }
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
            float leftTriggerAxis = SteamVR_Actions._default.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any);
            float rightTriggerAxis = SteamVR_Actions._default.RightTrigger.GetAxis(SteamVR_Input_Sources.Any);
            bool bothTriggersPressed = leftTriggerAxis > 0.5f && rightTriggerAxis > 0.5f;
            if (!active || manualDone || instance != controller) return;
            if (VRGlobals.vrPlayer == null || VRGlobals.ikManager == null) return;

            // Animator stays frozen except during the lid-roll (StepGesture drives it) and the
            // per-bite play pulse (Time.time < playUntil).
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
            StepGesture();
            if (bothTriggersPressed)
            {
                FinishSequence();
                cancelAction = true;
            }
        }

        public static void End()
        {
            try { controller?.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed); } catch { }
            UnsubscribePinAfterIk();
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
            SetArmIk(VRGlobals.ikManager.rightArmIk, VRGlobals.vrPlayer.RightHand);
            SetArmIk(VRGlobals.ikManager.leftArmIk, VRGlobals.vrPlayer.LeftHand);
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

        private static void SetArmIk(LimbIK ik, GameObject hand)
        {
            if (ik == null || hand == null) return;
            if (ik.solver.target != hand.transform) ik.solver.target = hand.transform;
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
                    ZeroLocal(baseT); // pack sits on its holder
                    // wrapper group (cover + pile + food) stays glued to the pack.
                    if (wrapperT != null) { wrapperT.localPosition = wrapperLocalPos; wrapperT.localRotation = wrapperLocalRot; }
                    // once taken, the food piece sits on the left-hand holder.
                    if (foodHolder != null && foodT != null && foodT.parent == foodHolder.transform) ZeroLocal(foodT);
                    break;
                case FoodKind.Drink:
                    ZeroLocal(baseT); // drink sits on its holder
                    if (capT != null && capHolder != null && capT.parent == capHolder.transform) ZeroLocal(capT); // cap sits on its holder once taken
                    break;
                default: // FoodKind.CannedFood
                    ZeroLocal(baseT);
                    ZeroLocal(spoonT);
                    ZeroLocal(foodT);
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
        private static void PinLeftAfterIk() { if (active && !manualDone && driveHandsToTargets) PinBoneToTarget(VRGlobals.ikManager?.leftArmIk, LeftHand()); }
        private static void PinRightAfterIk() { if (active && !manualDone && driveHandsToTargets) PinBoneToTarget(VRGlobals.ikManager?.rightArmIk, RightHand()); }

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
                Restore(wrapperT, wrapperParent0, wrapperPos0, wrapperRot0); // handheld (null-safe)
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
                if (baseHolder != null) UnityEngine.Object.Destroy(baseHolder);
                if (spoonHolder != null) UnityEngine.Object.Destroy(spoonHolder);
                if (foodHolder != null) UnityEngine.Object.Destroy(foodHolder);
                if (crackerHolder != null) UnityEngine.Object.Destroy(crackerHolder);
                if (capHolder != null) UnityEngine.Object.Destroy(capHolder);
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] RestoreProps error: {ex}"); }
            baseHolder = spoonHolder = foodHolder = crackerHolder = capHolder = null;
            reparented = false;
        }

        //--- Gesture state machine --------------------------------------------------
        // All archetypes share ONE open->take->eat loop; only the hand roles, the arms state
        // the open plays, and a few reveal/advance hooks differ. Those live in the per-archetype
        // EatStyle descriptor (built per food on spawn), so the control flow below is written
        // once. The genuinely-unique routines (DetachWrapperToLeftHand / ShakeOutCrackers /
        // TakeFoodToLeftHand / EnterUseState / DetectShake) are kept as-is and just called from
        // the hooks. To add a TYPE: a new case here + its prop setup / late-zero tails.
        private static EatStyle BuildStyle(FoodKind kind)
        {
            switch (kind)
            {
                // Chocolate bar: held in the RIGHT hand, the LEFT (off) hand peels the wrapper,
                // then bite straight to the mouth — NO take step. The whole clip is STATE_USE.
                case FoodKind.Handheld:
                    return new EatStyle
                    {
                        label = "wrapper",
                        openHand = Hand.Off,
                        eatHand = Hand.Dominant,
                        takeHand = Hand.Off,
                        hasTakeStep = false,
                        takeKind = TakeKind.None,
                        openStateHash = STATE_USE_HASH,
                        logsGesturePose = false,
                        onOpened = () => DetachWrapperToLeftHand(), // peeled -> stays in the left hand
                    };
                case FoodKind.Drink:
                    return new EatStyle
                    {
                        label = "drink",
                        openHand = Hand.Off,
                        eatHand = Hand.Dominant,
                        takeHand = Hand.Off,
                        hasTakeStep = false,
                        takeKind = TakeKind.None,
                        openStateHash = STATE_OPEN_HASH,
                        logsGesturePose = false,
                        onOpened = () => DetachCapToLeftHand(), // cap opened -> stays in the left hand
                    };
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
                        onTake = () => { TakeFoodToLeftHand(); StartGesturePulse(grabPoseState, grabPoseTime); Pulse(); },
                        onEatHide = () => SetRenderer(foodR, false),
                        onAfterBite = () => StartGesturePulse(eatPoseState, eatPoseTime),
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
                        onTake = () => { SetRenderer(foodR, true); StartGesturePulse(grabPoseState, grabPoseTime); Pulse(); },
                        onEatHide = () => SetRenderer(foodR, false),
                        onAfterBite = () => StartGesturePulse(eatPoseState, eatPoseTime),
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
                    // With a take step you take first (-> Holding); without one (Handheld) you
                    // eat straight from Ready.
                    if (s.hasTakeStep) StepTake(s); else StepEat(s);
                    break;
                case Phase.Holding:
                    StepEat(s);
                    break;
                case Phase.Done:
                    break;
            }
        }

        // Open: bring the opening hand to the held item's open-zone + that hand's trigger ->
        // play the open state forward to openHoldTime (lid rolls / wrapper peels / bag tears),
        // then freeze and run the per-archetype onOpened reveal.
        private static void StepOpen(EatStyle s)
        {
            if (!playingOpen)
            {
                Transform acting = HandT(s.openHand);
                Transform holding = HandT(Other(s.openHand)); // the held item rides this hand
                if (ZoneReached(acting, holding, openZoneOffset, openDistance) && TriggerEdgeImpl(s.openHand == Hand.Dominant))
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
                bool past = st.fullPathHash != s.openStateHash || st.normalizedTime >= openHoldTime;
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

        // Take: the take gesture (reach the holding hand, or SHAKE the bag) reveals/moves the
        // food onto the eating hand and advances the animator. -> Holding.
        private static void StepTake(EatStyle s)
        {
            bool triggered = s.takeKind == TakeKind.Shake
                ? DetectShake()
                : ZoneReached(HandT(s.takeHand), HandT(Other(s.takeHand)), takeZoneOffset, scoopDistance);
            if (!triggered) return;

            if (logGesturePose && s.logsGesturePose)
                Plugin.MyLog.LogInfo($"[ManualEat] take #{biteCount + 1} pulse from {CurStateStr()}");
            s.onTake?.Invoke();
            PlaySound(def.scoopSound);
            phase = Phase.Holding;
            Plugin.MyLog.LogInfo($"[ManualEat] Took {s.label} (round {biteCount + 1}/{def.bites}).");
        }

        // Eat: bring the eating hand (+ eat-zone offset) to the mouth -> hide the eaten piece,
        // count the bite, then finish (last bite) or advance for the next round. Shared by the
        // Holding phase (foods with a take step) and the Ready phase (Handheld: open then bite).
        private static void StepEat(EatStyle s)
        {
            if (!EatZoneReached(HandT(s.eatHand))) return;

            if (logGesturePose && s.logsGesturePose)
                Plugin.MyLog.LogInfo($"[ManualEat] eat #{biteCount + 1} pulse from {CurStateStr()}");
            s.onEatHide?.Invoke();
            biteCount++;
            Pulse();
            PlaySound(def.eatSound);
            Plugin.MyLog.LogInfo($"[ManualEat] Ate {biteCount}/{def.bites}.");

            if (biteCount >= def.bites)
            {
                FinishSequence(); // drives STATE_END itself; no eat pulse needed
            }
            else
            {
                s.onAfterBite?.Invoke();
                phase = Phase.Ready;
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

        private static void DetachCapToLeftHand()
        {
            if (capT == null || capDetached || leftHandBoneRef == null) return;
            try
            {
                capHolder = NewHolder("EatCapHolder", leftHandBoneRef, def.foodPos, def.foodRot);
                capT.SetParent(capHolder.transform, false);
                capDetached = true;
                Plugin.MyLog.LogInfo("[ManualEat] Cap detached to the left hand.");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] DetachCap error: {ex}"); }
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

            Vector3 cur = head.InverseTransformPoint(rh.position);
            Vector3 vel = (cur - shakePrevPos) / dt;
            shakePrevPos = cur;

            bool near = LeftHand() != null && Vector3.Distance(RightHand().position, LeftHand().position) < shakeNearDistance;
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

        // Final bite: apply the nutrition (method_5) and tell the animator the use is
        // DONE (SetActiveParam(false) — what the game's own method_9 does), which
        // drives it straight to the END/put-away state regardless of the effect timer.
        // The END animation then fires the weapon-out event -> we switch back to the
        // weapon. manualDone stays false so Tick keeps the animator playing to END.
        private static void FinishSequence()
        {
            phase = Phase.Done;
            manualDone = true; // stop the hand-pin/IK so the END animation moves the hands (put-away)
            ReleaseArms();

            if (effectFired) { Plugin.MyLog.LogWarning("[ManualEat] Finish: effect already fired."); return; }
            if (pendingOp == null) { Plugin.MyLog.LogError("[ManualEat] Finish: pendingOp NULL — may stick!"); return; }

            effectFired = true;
            Plugin.MyLog.LogInfo("[ManualEat] Finish: method_5 + SetActiveParam(false) -> play END.");
            try
            {
                var fa = controller.FirearmsAnimator;
                fa?.SetNextLimb(false);

                if (cancelToFinish)
                {
                    // The fire-cancel aborts the over-time heal effect, so apply the
                    // food's full energy/hydration INSTANTLY first (otherwise no nutrition).
                    if(!cancelAction)
                        ApplyInstantNutrition();
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
                        try { controller._player.ArmsAnimatorCommon.Play(STATE_END_HASH, 1, endStartTime); }
                        catch (Exception ex) { Plugin.MyLog.LogWarning($"[ManualEat] skip-bite Play failed: {ex.Message}"); }
                    }
                    // Auto-fire the trigger/fire cancel (press then release on consecutive
                    // input frames) — puts the food away and draws the weapon fast.
                    TarkovVR.Source.Controls.VRInputManager.ForceCommand(EFT.InputSystem.ECommand.ToggleShooting);
                    TarkovVR.Source.Controls.VRInputManager.ForceCommand(EFT.InputSystem.ECommand.EndShooting);
                    // The cancel aborts the vanilla resource depletion, so the food never
                    // gets removed. Discard it ourselves once the put-away has settled it
                    // back into a container (need a stable parent; the discard itself is
                    // simulate->RunNetworkTransaction so it executes exactly once).
                    if (VRGlobals.vrPlayer != null && !cancelAction)
                        VRGlobals.vrPlayer.StartCoroutine(ConsumeFoodWhenSettled(controller.Item));
                    cancelAction = false;
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

        // Apply the food's full energy/hydration immediately (the fire-cancel aborts the
        // vanilla over-time heal effect before it ticks). Mirrors how the effect derives
        // the totals: item HealthEffectsComponent -> Energy/Hydration -> CutPiece(amount).
        private static void ApplyInstantNutrition()
        {
            try
            {
                EFT.Player player = controller?._player;
                EFT.InventoryLogic.Item item = controller?.Item;
                if (player == null || item == null) return;

                var hec = item.GetItemComponent<EFT.InventoryLogic.HealthEffectsComponent>();
                var hc = player.ActiveHealthController;
                if (hec == null || hc == null) return;

                float amount = 1f;
                var fdc = item.GetItemComponent<EFT.InventoryLogic.FoodDrinkComponent>();
                if (fdc != null) amount = fdc.MaxResource;

                var effects = hec.HealthEffects;
                if (effects.TryGetValue(EFT.HealthSystem.EHealthFactorType.Energy, out var en))
                {
                    float v = en.CutPiece(amount).Value;
                    if (v != 0f) hc.ChangeEnergy(v);
                }
                if (effects.TryGetValue(EFT.HealthSystem.EHealthFactorType.Hydration, out var hy))
                {
                    float v = hy.CutPiece(amount).Value;
                    if (v != 0f) hc.ChangeHydration(v);
                }
                Plugin.MyLog.LogInfo("[ManualEat] Applied instant nutrition.");
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] ApplyInstantNutrition error: {ex}"); }
        }

        //--- Helpers ----------------------------------------------------------------
        private static Transform RightHand() => VRGlobals.vrPlayer?.RightHand != null ? VRGlobals.vrPlayer.RightHand.transform : null;
        private static Transform LeftHand() => VRGlobals.vrPlayer?.LeftHand != null ? VRGlobals.vrPlayer.LeftHand.transform : null;

        // Resolve a hand role to its transform. RightHand is ALWAYS the dominant hand and
        // LeftHand the off hand (the SteamVR pose listeners swap in left-handed mode), so no
        // handedness branch is needed here.
        private static Transform HandT(Hand h) => h == Hand.Dominant ? RightHand() : LeftHand();
        private static Hand Other(Hand h) => h == Hand.Dominant ? Hand.Off : Hand.Dominant;

        // A reach gesture fires when the acting hand is within <radius> of an anchor point +
        // local offset. Offset zero => the anchor's own position = the original hand-to-hand
        // trigger. The offset is in the anchor's local frame, so it rides the held item (e.g.
        // up a bottle's axis to its cap).
        private static bool ZoneReached(Transform actingHand, Transform anchor, Vector3 offset, float radius)
        {
            if (actingHand == null || anchor == null) return false;
            return Vector3.Distance(actingHand.position, anchor.TransformPoint(offset)) < radius;
        }

        // The eat gesture: the eating hand (+ eatZoneOffset, e.g. a spout) is near the head and
        // roughly in front of it. Offset zero + the forward gate = the original HandAtMouth.
        private static bool EatZoneReached(Transform eatHand)
        {
            Transform head = GetHead();
            if (head == null || eatHand == null) return false;
            Vector3 delta = eatHand.TransformPoint(eatZoneOffset) - head.position;
            if (delta.magnitude > eatDistance) return false;
            return Vector3.Dot(delta.normalized, head.forward) > mouthForwardDot;
        }

        private static Transform GetHead()
        {
            if (VRGlobals.VRCam != null) return VRGlobals.VRCam.transform;
            return Camera.main != null ? Camera.main.transform : null;
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

        private static void Pulse()
        {
            if (!eatingHaptics) return;
            SteamVR_Input_Sources src = VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
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
        public static bool AllowSound(BaseSoundPlayer sp)
        {
            if (!active || sp != soundPlayer) return true;
            if (playingOpen) return true;       // let the open segment's own sounds play
            return playingManualSound;
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
            wrapperT = coverT = null;
            coverParent0 = null;
            coverDetached = false;
            capDetached = false;
            leftHandBoneRef = null;
            crackerT = null; crackerR = null;
            crackerParent0 = null; crackerPos0 = null; crackerRot0 = null;
            crackerLocalPos = null; crackerLocalRot = null;
            crackerHolder = null; crackersShown = false;
            shakeReversals = 0; shakeWindowEnd = 0f; shakePrevPos = shakePrevVel = Vector3.zero;
            medsBody = null;
            spoonR = foodR = null;
            soundPlayer = null;
            baseHolder = spoonHolder = foodHolder = capHolder = null;
            baseParent0 = spoonParent0 = foodParent0 = wrapperParent0 = capParent0 = null;
            reparented = false;
            effectFired = false;
            pendingOp = null;
            prevTriggerAxis = 0f;
            prevOffTriggerAxis = 0f;
            playingOpen = false;
            playUntil = 0f;
        }
    }
}
