using System;
using EFT.InventoryLogic;
using RootMotion.FinalIK;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using static EFT.Player;

namespace TarkovVR.Source.Player.Interactions
{
    // Fully manual, two-handed, gesture-driven eating.
    //
    // Recon (item_food_beefstew_2 / tushonka) showed the eat is NOT mesh swaps —
    // every prop renderer stays enabled and the vanilla animation just moves bones.
    // Props all hang off one root:
    //   saira_root                  -> the can (SkinnedMeshRenderer)
    //   saira_root/saira_spoon      -> the spoon (MeshRenderer)
    //   saira_root/saira_foodpiece  -> the bite on the spoon (MeshRenderer)
    //
    // Hard lessons from earlier iterations (all fixed here):
    //  * The controller animator, even frozen at speed 0, RE-APPLIES its bone pose
    //    every frame — so writing prop bone transforms in ManualUpdate gets stomped.
    //    Fix: REPARENT the props out of the animated rig onto the VR hands, so the
    //    animator no longer owns them. Restore on teardown (the object is pooled).
    //  * During meds the game raises LEFT_HAND_ANIMATOR_HASH and
    //    VRPlayerManager.UpdateLeftHand early-returns, freezing LeftHand + disabling
    //    leftArmIk. Suppressing ThirdAction did NOT prevent the flag. Fix: a prefix
    //    on UpdateLeftHand keeps the left hand tracking while we're active (see
    //    EatingPatches.ForceLeftHandTracking).
    //  * Completion (effect + switch back to weapon) needs the animation: freezing
    //    forever leaves you stuck. Fix: on the final bite we unfreeze and fire
    //    method_5 (the proven path), handing back to vanilla to finish.
    //
    // Per-item: prop names differ per food, so each handled food has a FoodDef.
    // Foods without a def fall back to vanilla auto-eat (we don't arm them).
    internal static class EatingInteractionController
    {
        //--- Per-item definitions ---------------------------------------------------
        // Everything that differs between foods lives here. The finish (nutrition,
        // cancel, discard) is food-agnostic; this is the gesture/prop half.
        //
        // Spoon vs. no-spoon: if spoonName is set the food is eaten with a utensil held
        // in the right hand (tushonka). If spoonName is null/empty the food is grabbed
        // directly by the right hand (sprats — the food piece parents to the right hand
        // instead of a spoon). HasSpoon drives that branch.
        //
        // CannedFood vs Handheld: cans (tushonka, sprats) are held in the LEFT hand and
        // opened/eaten with the RIGHT. A Handheld food (chocolate bar) is held in the
        // RIGHT hand, the LEFT hand opens it (peels a wrapper), and it's eaten in N bites
        // straight to the mouth — no can/lid/scoop. FoodKind picks the gesture machine.
        private enum FoodKind { CannedFood, Handheld, Bag, Pack }

        private sealed class FoodDef
        {
            public string templateId;
            public FoodKind kind = FoodKind.CannedFood;
            public string rootName;       // the held prop (can for cans; the bar for handheld)
            public string spoonName;      // utensil; null/empty = grabbed by hand (no spoon)
            public string foodPieceName;  // the bite that appears on the utensil/hand (cans)
            // Handheld only. wrapperName = a group glued onto the held item so it follows
            // the hand while its children animate (e.g. "sn_CAT", which also carries the
            // chocolate sn_feces — that stays with the bar). coverName = the actual wrapper
            // mesh (e.g. "sn_cover") that DETACHES to the off (left) hand once peeled — you
            // take the wrapper off and hold it. Either can be null.
            public string wrapperName;
            public string coverName;
            public int bites = 3;

            // Sound event names (BaseSoundPlayer.OnSound). Null/empty = no sound.
            // drawSound/openSounds fire from the STATE_OPEN animation itself; scoop/eat
            // are played by us per gesture (the animation is frozen between gestures).
            public string drawSound;
            public string[] openSounds;
            public string scoopSound;     // played when the food is taken (scoop / grab)
            public string eatSound;       // played on each bite

            // Hold poses LOCAL TO THE PALM BONE — measured palm->prop at the grip (see
            // EatingRecon [GRIP]). Live-tune on the holder GameObjects in Unity Explorer.
            public Vector3 canPos;        // can in the off (left) hand
            public Vector3 canRot;
            public Vector3 spoonPos;      // utensil in the main (right) hand (HasSpoon only)
            public Vector3 spoonRot;
            public Vector3 foodPos;       // food piece: vs the spoon holder (HasSpoon) or
            public Vector3 foodRot;       //   the right hand (no spoon)

            // Arms layer-1 normalizedTime knobs (see EatingRecon [SOUND]/[STATE]).
            public float openHoldTime = 0.92f; // freeze STATE_OPEN here (lid rolled / spoon grabbed)
            public float endStartTime = 0.3f;  // where in STATE_END to start the put-away

            // Per-gesture pose determinism. When true, each grab/eat SNAPS the arms
            // animator to a fixed (state, normalizedTime) before its play pulse, so every
            // bite replays the SAME segment — identical finger pose + food deform. Without
            // it the pulse free-runs from the last frozen pose and the timeline drifts, so
            // each bite looks different (sprats symptom: each grabbed fish a different
            // orientation, only the 2nd grab clean). grab=STATE_USE, eat=STATE_EAT.
            public bool deterministicGesture = false;
            public float grabPoseTime = 0f;   // STATE_USE normalizedTime the grab pulse starts from
            public float eatPoseTime = 0f;    // STATE_EAT normalizedTime the eat pulse starts from

            public bool HasSpoon => !string.IsNullOrEmpty(spoonName);
        }

        // ===== Food registry ====================================================
        // To add a food: copy a line below and change the template id + prop names (and,
        // if its grips/timings differ from the archetype, the overrides). Use the
        // EatingRecon profiler to get a paste-ready line, then DumpFoodDef() to bake your
        // in-headset holder tuning back into the overrides. Each archetype factory fills
        // the defaults (grips, sounds, timings) so a typical food is ONE line.
        //   CanSpoon — can in the left hand, lid roll, spoon scoop in the right (tushonka)
        //   CanHand  — same but no spoon: grab the food by the right hand (sprats)
        //   Wrapper  — bar in the right hand, left hand peels the wrapper off (chocolate)
        private static readonly FoodDef[] Defs =
        {
            CanSpoon("57347d7224597744596b4e72", "saira_root", "saira_spoon", "saira_foodpiece"),
            CanSpoon("5673de654bdc2d180f8b456d", "saira_root", "saira_spoon", "saira_foodpiece"),
            CanSpoon("57347d5f245977448b40fa81", "saira_root", "saira_spoon", "saira_foodpiece"),
            CanHand("5bc9c29cd4351e003562b8a3", "sprats_root", "sprats_foodpiece"),
            Wrapper("544fb6cc4bdc2d34748b456e", "item_slickers_LOD0", "sn_CAT", "sn_cover"),
            Bag("5751487e245977207e26a315", "bone_upakovka", "bone_suharik_hold"),
            Bag("57347d3d245977448f7b7f61", "bone_upakovka", "bone_suharik_hold"),
            // Held root is pack_CAT (the bone group that drives the skinned pack mesh + holds
            // the cover/pile/galette) — NOT item_galettte_pack_LOD0 (a SkinnedMeshRenderer whose
            // transform is a phantom off near the weapon root). So it's a unified root, no glue.
            Pack("5448ff904bdc2d6f028b456e", "pack_CAT", null, "item_galette_LOD0"),
        };

        private static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);

        // CanSpoon archetype (tushonka): can held in the LEFT hand, pull-tab lid roll, a
        // spoon scoops from the can in the RIGHT hand, N bites to the mouth. Defaults below
        // are tushonka's measured grips/sounds — override only what differs for a new can.
        private static FoodDef CanSpoon(string id, string root, string spoon, string food,
            int bites = 3,
            Vector3? canPos = null, Vector3? canRot = null,
            Vector3? spoonPos = null, Vector3? spoonRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            float openHoldTime = 0.92f, float endStartTime = 0.3f,
            string[] openSounds = null, string scoopSound = null, string eatSound = "Take") =>
            new FoodDef
            {
                templateId = id, kind = FoodKind.CannedFood,
                rootName = root, spoonName = spoon, foodPieceName = food, bites = bites,
                drawSound = "Draw", openSounds = openSounds ?? new[] { "Open", "Open2", "SpoonTake" },
                scoopSound = scoopSound, eatSound = eatSound,
                canPos = canPos ?? V(-0.1135f, -0.0298f, -0.0034f), canRot = canRot ?? V(80.72f, 248.34f, 303.57f),
                spoonPos = spoonPos ?? V(-0.1247f, -0.0537f, -0.0113f), spoonRot = spoonRot ?? V(40.67f, 194.91f, 210.26f),
                foodPos = foodPos ?? V(0f, 0.05f, 0.007f), foodRot = foodRot ?? Vector3.zero,
                openHoldTime = openHoldTime, endStartTime = endStartTime,
                deterministicGesture = false,
            };

        // CanHand archetype (sprats): like CanSpoon but NO spoon — the food piece is grabbed
        // directly by the RIGHT hand. Uses deterministic gesture poses so every grab is the
        // same clean "reach in -> pinch -> lift" (STATE_USE@grabPoseTime). Defaults = sprats.
        private static FoodDef CanHand(string id, string root, string food,
            int bites = 3,
            Vector3? canPos = null, Vector3? canRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            float openHoldTime = 0.9f, float endStartTime = 0.3f,
            float grabPoseTime = 0.11f, float eatPoseTime = 0.9f,
            string[] openSounds = null, string scoopSound = "Take", string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id, kind = FoodKind.CannedFood,
                rootName = root, spoonName = null, foodPieceName = food, bites = bites,
                drawSound = "Draw", openSounds = openSounds ?? new[] { "Open", "Open2" },
                scoopSound = scoopSound, eatSound = eatSound,
                canPos = canPos ?? V(-0.106f, -0.013f, 0f), canRot = canRot ?? V(80.2f, 248.9f, 303.5f),
                foodPos = foodPos ?? V(-0.1247f, -0.0537f, -0.0113f), foodRot = foodRot ?? V(40.67f, 194.91f, 210.26f),
                openHoldTime = openHoldTime, endStartTime = endStartTime,
                deterministicGesture = true, grabPoseTime = grabPoseTime, eatPoseTime = eatPoseTime,
            };

        // Wrapper archetype (chocolate bar): bar (root) held in the RIGHT hand; the LEFT hand
        // peels the wrapper. wrapperGroup (e.g. sn_CAT) stays glued to the bar and carries any
        // sub-pieces; cover (e.g. sn_cover) is the wrapper that DETACHES to the left hand once
        // peeled. barPos/barRot = the bar's right-hand grip; coverPos/coverRot = the peeled
        // wrapper's left-hand grip. Defaults = chocolate.
        private static FoodDef Wrapper(string id, string root, string wrapperGroup, string cover,
            int bites = 1,
            Vector3? barPos = null, Vector3? barRot = null,
            Vector3? coverPos = null, Vector3? coverRot = null,
            float openHoldTime = 0.35f, float endStartTime = 0.3f,
            string[] openSounds = null, string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id, kind = FoodKind.Handheld,
                rootName = root, wrapperName = wrapperGroup, coverName = cover,
                spoonName = null, foodPieceName = null, bites = bites,
                drawSound = "Draw", openSounds = openSounds ?? new[] { "Open" }, scoopSound = null, eatSound = eatSound,
                canPos = barPos ?? V(-0.121f, -0.037f, -0.054f), canRot = barRot ?? V(57.7f, 110.3f, 258.6f),
                foodPos = coverPos ?? V(-0.137f, -0.078f, 0.04f), foodRot = coverRot ?? V(27.4f, 105.2f, 354.7f),
                openHoldTime = openHoldTime, endStartTime = endStartTime,
                deterministicGesture = false,
            };

        // Bag archetype (Emelya/Borodinsky croutons): the BAG (root, e.g. bone_upakovka,
        // which holds everything) is held in the RIGHT hand; the LEFT hand opens it, then you
        // SHAKE the bag near the left hand to pour N "hold" crackers (crackerPrefix matches
        // every bone, e.g. "bone_suharik_hold") into the LEFT hand, and eat from the LEFT hand.
        // `bites` = shake→eat rounds. bagPos/bagRot = bag's right-hand grip; crackerPos/
        // crackerRot = the cracker clump's left-hand grip. Grips seeded — recon + tune.
        private static FoodDef Bag(string id, string root, string crackerPrefix,
            int bites = 2,
            Vector3? bagPos = null, Vector3? bagRot = null,
            Vector3? crackerPos = null, Vector3? crackerRot = null,
            float openHoldTime = 0.85f, float endStartTime = 0.3f,
            string[] openSounds = null, string scoopSound = "Take", string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id, kind = FoodKind.Bag,
                rootName = root, foodPieceName = crackerPrefix, spoonName = null, bites = bites,
                drawSound = "Draw", openSounds = openSounds ?? new[] { "Draw1", "Open", "Draw" },
                scoopSound = scoopSound, eatSound = eatSound,
                // bag grip MEASURED (palm->bone_upakovka, Base HumanRPalm, averaged).
                canPos = bagPos ?? V(-0.148f, -0.040f, -0.026f), canRot = bagRot ?? V(355.4f, 204.9f, 251.6f),
                // cracker (clump anchor) grip MEASURED in-hand (palm->bone_suharik_hold_000,
                // Base HumanLPalm, settled STATE_USE/EAT cluster).
                foodPos = crackerPos ?? V(-0.067f, -0.042f, -0.003f), foodRot = crackerRot ?? V(8f, 73.1f, 93.4f),
                openHoldTime = openHoldTime, endStartTime = endStartTime,
                deterministicGesture = false,
            };

        // Pack archetype (galette crackers): a wrapped pack (root, e.g. item_galettte_pack_LOD0)
        // held in the RIGHT hand; wrapperGroup (e.g. pack_CAT) is glued to it (carries the cover
        // — which just opens in place, no detach — plus the pile and the single food piece). The
        // LEFT hand opens it, then takes the food piece (food, e.g. item_galette_LOD0) into the
        // LEFT hand and eats from there. Mirror of CanHand. `bites` = take→eat rounds. packPos/
        // packRot = pack's right-hand grip; foodPos/foodRot = the piece's left-hand grip.
        private static FoodDef Pack(string id, string root, string wrapperGroup, string food,
            int bites = 2,
            Vector3? packPos = null, Vector3? packRot = null,
            Vector3? foodPos = null, Vector3? foodRot = null,
            float openHoldTime = 0.9f, float endStartTime = 0.3f,
            float grabPoseTime = 0.11f, float eatPoseTime = 0.7f,
            string[] openSounds = null, string scoopSound = "Take", string eatSound = "Eat") =>
            new FoodDef
            {
                templateId = id, kind = FoodKind.Pack,
                rootName = root, wrapperName = wrapperGroup, foodPieceName = food, spoonName = null, bites = bites,
                drawSound = "Draw", openSounds = openSounds ?? new[] { "Draw", "Open" },
                scoopSound = scoopSound, eatSound = eatSound,
                // pack grip MEASURED (right hand) — recon pack_CAT / Base HumanRPalm.
                canPos = packPos ?? V(-0.124f, -0.052f, -0.054f), canRot = packRot ?? V(28.2f, 112.6f, 254.7f),
                // piece grip MEASURED (left hand) — recon item_galette_LOD0 / Base HumanLPalm in-hand.
                foodPos = foodPos ?? V(-0.15f, -0.031f, 0.033f), foodRot = foodRot ?? V(38.4f, 27.3f, 286.7f),
                openHoldTime = openHoldTime, endStartTime = endStartTime,
                // Deterministic grab/eat like CanHand: each take replays STATE_USE@grabPoseTime
                // (hand opens -> reach -> pinch) and each eat STATE_EAT@eatPoseTime. Also keeps
                // the animator out of STATE_OPEN so the finish cancels cleanly.
                deterministicGesture = true, grabPoseTime = grabPoseTime, eatPoseTime = eatPoseTime,
            };

        // ===== Authoring helper ==================================================
        // Call this from UnityExplorer (static method on EatingInteractionController) while
        // a food is being eaten and AFTER you've tuned its holders live (EatCanHolder /
        // EatSpoonHolder / EatFoodHolder / EatBarHolder / EatWrapperHolder). It prints a
        // paste-ready archetype factory line — grips read straight off the tuned holders,
        // timings off the live knobs — so you can bake your tuning into Defs above without
        // re-measuring. (For Wrapper foods, open the wrapper first so the wrapper holder
        // exists.)
        public static void DumpFoodDef()
        {
            if (!active || def == null) { Plugin.MyLog.LogWarning("[ManualEat] DumpFoodDef: no food is being eaten."); return; }
            Vector3 HolderPos(GameObject h, Vector3 fallback) => h != null ? h.transform.localPosition : fallback;
            Vector3 HolderRot(GameObject h, Vector3 fallback) => h != null ? h.transform.localEulerAngles : fallback;

            string line;
            if (def.kind == FoodKind.Bag)
            {
                line = $"Bag(\"{def.templateId}\", \"{def.rootName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"bagPos: {VStr(HolderPos(canHolder, def.canPos))}, bagRot: {VStr(HolderRot(canHolder, def.canRot))}, "
                     + $"crackerPos: {VStr(HolderPos(crackerHolder, def.foodPos))}, crackerRot: {VStr(HolderRot(crackerHolder, def.foodRot))}, "
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)}),";
            }
            else if (def.kind == FoodKind.Pack)
            {
                line = $"Pack(\"{def.templateId}\", \"{def.rootName}\", \"{def.wrapperName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"packPos: {VStr(HolderPos(canHolder, def.canPos))}, packRot: {VStr(HolderRot(canHolder, def.canRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)}),";
            }
            else if (def.kind == FoodKind.Handheld)
            {
                line = $"Wrapper(\"{def.templateId}\", \"{def.rootName}\", \"{def.wrapperName}\", \"{def.coverName}\", bites: {def.bites}, "
                     + $"barPos: {VStr(HolderPos(canHolder, def.canPos))}, barRot: {VStr(HolderRot(canHolder, def.canRot))}, "
                     + $"coverPos: {VStr(HolderPos(foodHolder, def.foodPos))}, coverRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)}),";
            }
            else if (def.HasSpoon)
            {
                line = $"CanSpoon(\"{def.templateId}\", \"{def.rootName}\", \"{def.spoonName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"canPos: {VStr(HolderPos(canHolder, def.canPos))}, canRot: {VStr(HolderRot(canHolder, def.canRot))}, "
                     + $"spoonPos: {VStr(HolderPos(spoonHolder, def.spoonPos))}, spoonRot: {VStr(HolderRot(spoonHolder, def.spoonRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)}),";
            }
            else
            {
                line = $"CanHand(\"{def.templateId}\", \"{def.rootName}\", \"{def.foodPieceName}\", bites: {def.bites}, "
                     + $"canPos: {VStr(HolderPos(canHolder, def.canPos))}, canRot: {VStr(HolderRot(canHolder, def.canRot))}, "
                     + $"foodPos: {VStr(HolderPos(foodHolder, def.foodPos))}, foodRot: {VStr(HolderRot(foodHolder, def.foodRot))}, "
                     + $"openHoldTime: {FStr(openHoldTime)}, endStartTime: {FStr(endStartTime)}, "
                     + $"grabPoseTime: {FStr(grabPoseTime)}, eatPoseTime: {FStr(eatPoseTime)}),";
            }
            Plugin.MyLog.LogInfo("[ManualEat] === paste into Defs (tuned) ===\n            " + line);
        }

        private static string VStr(Vector3 v) => $"V({v.x:0.###}f, {v.y:0.###}f, {v.z:0.###}f)";
        private static string FStr(float f) => $"{f:0.###}f";

        //--- Tunables (public so they can be A/B'd live in the headset) -------------
        public static bool enableManualEating = true;

        // Gesture distances (metres). The gesture uses the controller positions, but
        // the spoon/can sit offset from the controllers, so these are generous.
        public static float openDistance  = 0.18f;
        public static float scoopDistance = 0.18f;
        public static float eatDistance   = 0.23f;
        public static float mouthForwardDot = -0.2f;

        // Bag (croutons) shake-to-pour. The bag (right hand) must be within shakeNearDistance
        // of the left hand, and you must wiggle it: a head-relative velocity reversal above
        // shakeMinSpeed counts as one wiggle; shakeReversalsNeeded of them within shakeWindow
        // seconds pours the crackers out.
        public static float shakeNearDistance = 0.60f;
        public static float shakeMinSpeed = 0.2f;     // m/s, head-relative
        public static int   shakeReversalsNeeded = 1;
        public static float shakeWindow = 1.2f;

        // Hold-pose grip offsets are now PER-FOOD (FoodDef.canPos/spoonPos/foodPos, all
        // LOCAL TO THE PALM BONE). Live-tune them on the EatCanHolder / EatSpoonHolder /
        // EatFoodHolder GameObjects in Unity Explorer, then bake into the FoodDef.

        public static bool eatingHaptics = true;
        // Drive the body-follow (IKManager.MatchLegsToArms) ourselves each frame during
        // the eat. Normally that runs from a live HandsPositioner or IKManager.Update,
        // but mid-eat IKManager.Update's gate skips it (emptyHands set + !HandsIsEmpty)
        // AND the gun's HandsPositioner is disabled — so with no caller the body never
        // follows the head while walking and the rig/IK-targets desync = jitter. (A
        // weapon switch left an orphan HandsPositioner calling it, which is why
        // switching "fixed" the jitter.) Keep true.
        public static bool driveBodyFollowDuringEat = true;
        // Parent the props to the IK'd hand bone (bone3, the rendered wrist) rather
        // than the rig target. CONFIRMED in-headset: with driveHandsToTargets pinning
        // bone3 onto the smooth target each frame, the wrist tracks the target to
        // ~2-3mm with no walk lag — and parenting the prop to that SAME bone (not the
        // target, which lives on a different transform chain) removes the last ~2-3mm
        // prop/hand mismatch. So the prop rides the rendered hand exactly, and the hand
        // rides the smooth target. Set true only to A/B against target-parenting.
        public static bool debugParentToTarget = false;
        // Pin the rendered hand bone (bone3) to the rig target after the IK solve so the
        // wrist tracks the smooth target 1:1 during locomotion (no lag). Required for
        // the prop-on-bone3 setup above to stay smooth.
        public static bool driveHandsToTargets = true;

        //--- Runtime ----------------------------------------------------------------
        // Closed: can shut. Ready: lid open, utensil/hand empty — ready to take food.
        // Holding: food on the spoon / in the hand — ready to bring to the mouth.
        private enum Phase { Closed, Ready, Holding, Done }

        private static bool active;
        private static bool manualDone;     // handed back to vanilla for the finish
        private static MedsController controller;
        private static FoodDef def;
        private static Phase phase;
        private static int biteCount;
        private static float spawnAnimSpeed = 1f;

        private static Transform canT, spoonT, foodT;
        private static Transform medsBody;                // the meds controller object (pinned to the ribcage); rig body anchor
        private static Renderer spoonR, foodR;
        private static BaseSoundPlayer soundPlayer;
        // Holders sit between the hand and the prop. The animator still writes the
        // prop's LOCAL transform (we zero it in LateUpdate), but it never touches the
        // holder — so the holder is the clean, Unity-Explorer-tunable hold offset.
        private static GameObject canHolder, spoonHolder, foodHolder;

        // Handheld props (chocolate):
        //  wrapperT (e.g. sn_CAT) — glued onto the held bar (canT) at the captured rest
        //    offset; it carries the chocolate (sn_feces) so that stays with the bar, and
        //    its child sn_cover animates the peel.
        //  coverT (e.g. sn_cover) — the wrapper mesh. Once peeled (the open gesture) it
        //    DETACHES from wrapperT onto a LEFT-hand holder (foodHolder) — you take the
        //    wrapper off and hold it in the off hand, like the spoon for a can.
        private static Transform wrapperT, coverT;
        private static Vector3 wrapperLocalPos; private static Quaternion wrapperLocalRot;
        private static Transform coverParent0; private static Vector3 coverPos0; private static Quaternion coverRot0;
        private static bool coverDetached;            // wrapper (cover) moved from the bar to the left hand
        private static Transform leftHandBoneRef;     // resolved left IK hand bone (for the late wrapper/cracker holder)

        // Bag props (croutons): the bag (canT) is held in the right hand; on a SHAKE the
        // hold-crackers (crackerT[], found by prefix) move from the bag onto a left-hand
        // holder (crackerHolder) in their captured clump layout (crackerLocal*, relative to
        // the clump anchor crackerT[0]) and are eaten from the LEFT hand. Hidden until shaken.
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
        private static Transform canParent0, spoonParent0, foodParent0, wrapperParent0;
        private static Vector3 canPos0, spoonPos0, foodPos0, wrapperPos0;
        private static Quaternion canRot0, spoonRot0, foodRot0, wrapperRot0;
        private static bool reparented;

        private static bool effectFired;
        private static MedsController.ObservedMedsControllerClass pendingOp;
        private static float prevTriggerAxis;
        private static float prevOffTriggerAxis; // off-hand trigger edge (handheld wrapper-open)
        private static bool playingManualSound; // true while WE call OnSound (so it's not suppressed)
        private static bool playingOpen;        // playing the STATE_OPEN lid-roll segment

        // The eat's arms layer-1 states. The hashes are shared across foods (generic
        // arms-animator states — confirmed identical for tushonka and sprats in recon).
        private const int STATE_OPEN_HASH = 492683391;  // draw -> roll lid -> [grab spoon]
        private const int STATE_USE_HASH = -735675743;  // scoop / take (the grab motion)
        private const int STATE_EAT_HASH = 719885042;   // bite
        private const int STATE_END_HASH = -1014941517; // put-away

        // Per-gesture pose determinism (active values, loaded from the FoodDef on spawn;
        // public so they can be A/B'd live). See FoodDef.deterministicGesture and
        // StartGesturePulse. grabPoseState/eatPoseState are the segments each gesture
        // replays; the *Time fields are where in that segment the pulse starts.
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
        // On the last bite, auto-fire the trigger/fire command (ToggleShooting) — the
        // same press that cancels an eat and draws the weapon fast. Confirmed the manual
        // cancel works after the last bite; this just does it for you. Off = wait out the
        // operation's use-time.
        public static bool cancelToFinish = true;
        // On the last bite, force the arms animator straight to STATE_END (put-away)
        // instead of letting SetActiveParam transition through the rest of STATE_EAT
        // (the "auto last bite"). Off = keep the auto-bite.
        public static bool skipLastBiteAnim = true;
        // Where in STATE_END to start (normalizedTime 0..1). The clip opens by holding
        // the can out for a beat; starting later skips that and goes straight to the
        // hands-down put-away. Set from the current FoodDef on spawn; A/B live.
        public static float endStartTime = 0.3f;
        // Each scoop/eat gesture plays the animation forward this long (finger/hand
        // motion) then freezes. Set 0 to keep bites fully frozen.
        public static float bitePlayTime = 0.5f;
        private static float playUntil;        // animation plays (speed>0) while Time.time < this

        // Used by the UpdateLeftHand prefix to know when to keep the left hand live.
        public static bool ManualActive => active && !manualDone;

        // ===== Spawn (PREFIX on MedsController.Spawn — before Start()->method_5) =====
        public static void OnSpawnPre(MedsController instance, float animationSpeed)
        {
            try
            {
                Reset();
                if (!enableManualEating) return;

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

                // Parent to the IK'd HAND BONES (solver.bone3) — these follow your
                // controllers (the animation-rig palms don't) and share the palm-bone
                // orientation, so the measured grip offset still lands right.
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
                        : def.kind == FoodKind.Bag      ? SetupBagProps(root, rightHandBone)
                        : def.kind == FoodKind.Pack     ? SetupPackProps(root, rightHandBone)
                        :                                 SetupCannedProps(root, leftHandBone, rightHandBone);
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

        // Canned food (tushonka, sprats): can in the LEFT hand; spoon (if any) + food piece
        // in the RIGHT hand. The food piece hangs off the spoon holder (HasSpoon) or the
        // right hand directly (no spoon). Returns false (and Resets) if a prop is missing.
        private static bool SetupCannedProps(Transform root, Transform leftHandBone, Transform rightHandBone)
        {
            canT   = FindDeep(root, def.rootName);
            foodT  = FindDeep(root, def.foodPieceName);
            spoonT = def.HasSpoon ? FindDeep(root, def.spoonName) : null;

            if (canT == null || foodT == null || (def.HasSpoon && spoonT == null))
            {
                Plugin.MyLog.LogError($"[ManualEat] Missing props (can={canT != null} spoon={(def.HasSpoon ? (spoonT != null).ToString() : "n/a")} food={foodT != null}) — vanilla fallback.");
                Reset();
                return false;
            }

            spoonR = spoonT != null ? spoonT.GetComponentInChildren<Renderer>(true) : null;
            foodR  = foodT.GetComponentInChildren<Renderer>(true);

            Save(canT, out canParent0, out canPos0, out canRot0);
            if (spoonT != null) Save(spoonT, out spoonParent0, out spoonPos0, out spoonRot0);
            Save(foodT, out foodParent0, out foodPos0, out foodRot0);

            canHolder = NewHolder("EatCanHolder", leftHandBone, def.canPos, def.canRot);
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

            canT.SetParent(canHolder.transform, false);
            if (spoonT != null) spoonT.SetParent(spoonHolder.transform, false);
            foodT.SetParent(foodHolder.transform, false);
            reparented = true;

            SetRenderer(spoonR, false); // appears on "open" (no-op if no spoon)
            SetRenderer(foodR, false);  // appears on "scoop"/grab
            // (Draw/Open/Open2[/SpoonTake] fire from the STATE_OPEN segment itself.)
            return true;
        }

        // Handheld food (chocolate bar): the bar (rootName) is held in the RIGHT hand; the
        // wrapper+piece group (wrapperName) is reparented UNDER the bar so it follows the
        // hand, but its local is pinned to the captured rest offset each frame (LateZeroProps)
        // while its own children (sn_cover) still animate the peel. No food-piece toggling.
        private static bool SetupHandheldProps(Transform root, Transform rightHandBone)
        {
            canT = FindDeep(root, def.rootName);                       // the bar (held item)
            wrapperT = string.IsNullOrEmpty(def.wrapperName) ? null : FindDeep(root, def.wrapperName);
            coverT = string.IsNullOrEmpty(def.coverName) ? null : FindDeep(root, def.coverName);
            if (canT == null)
            {
                Plugin.MyLog.LogError($"[ManualEat] Handheld missing bar '{def.rootName}' — vanilla fallback.");
                Reset();
                return false;
            }

            // Capture the wrapper group's offset RELATIVE TO THE BAR at the rest pose (both
            // are siblings under 'weapon' right now) so it stays glued after the bar moves.
            if (wrapperT != null)
            {
                wrapperLocalPos = canT.InverseTransformPoint(wrapperT.position);
                wrapperLocalRot = Quaternion.Inverse(canT.rotation) * wrapperT.rotation;
            }

            Save(canT, out canParent0, out canPos0, out canRot0);
            if (wrapperT != null) Save(wrapperT, out wrapperParent0, out wrapperPos0, out wrapperRot0);
            if (coverT != null) Save(coverT, out coverParent0, out coverPos0, out coverRot0);

            canHolder = NewHolder("EatBarHolder", rightHandBone, def.canPos, def.canRot);
            canT.SetParent(canHolder.transform, false);
            if (wrapperT != null)
            {
                wrapperT.SetParent(canT, false);                 // ride the bar (carries sn_feces + sn_cover)
                wrapperT.localPosition = wrapperLocalPos;        // glue at the captured offset
                wrapperT.localRotation = wrapperLocalRot;
            }
            reparented = true;
            // Everything visible from the start; the wrapper (coverT) stays under wrapperT
            // and peels in place until the open gesture detaches it to the left hand.
            return true;
        }

        // Bag food (croutons): the bag root (rootName, holds everything) is held in the RIGHT
        // hand. The hold-crackers (every transform whose name starts with foodPieceName) are
        // hidden and left riding the bag until a SHAKE pours them into the left hand. We
        // capture each cracker's pose relative to the clump anchor (crackerT[0]) so they keep
        // their arrangement when moved. In-bag crackers + the torn corner ride the bag (we
        // don't touch them, like sn_feces).
        private static bool SetupBagProps(Transform root, Transform rightHandBone)
        {
            canT = FindDeep(root, def.rootName); // the bag (held item, holds everything)
            if (canT == null)
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

            Save(canT, out canParent0, out canPos0, out canRot0);
            canHolder = NewHolder("EatBagHolder", rightHandBone, def.canPos, def.canRot);
            canT.SetParent(canHolder.transform, false);
            reparented = true;
            return true;
        }

        // Pack food (galette): the pack (rootName) is held in the RIGHT hand; the wrapper group
        // (wrapperName, e.g. pack_CAT) is glued to the pack (like Wrapper — it carries the cover,
        // which opens in place, plus the food piece). The food piece (foodPieceName) is hidden
        // until the LEFT hand takes it, then it moves to a left-hand holder. Mirror of CanHand.
        private static bool SetupPackProps(Transform root, Transform rightHandBone)
        {
            canT = FindDeep(root, def.rootName);                                            // the pack (held)
            wrapperT = string.IsNullOrEmpty(def.wrapperName) ? null : FindDeep(root, def.wrapperName);
            foodT = FindDeep(root, def.foodPieceName);                                      // the piece to take
            if (canT == null || foodT == null)
            {
                Plugin.MyLog.LogError($"[ManualEat] Pack missing prop (pack={canT != null} food={foodT != null}) — vanilla fallback.");
                Reset();
                return false;
            }
            foodR = foodT.GetComponentInChildren<Renderer>(true);

            // Glue the wrapper group to the pack at its rest offset (carries the cover + food).
            if (wrapperT != null)
            {
                wrapperLocalPos = canT.InverseTransformPoint(wrapperT.position);
                wrapperLocalRot = Quaternion.Inverse(canT.rotation) * wrapperT.rotation;
            }

            Save(canT, out canParent0, out canPos0, out canRot0);
            if (wrapperT != null) Save(wrapperT, out wrapperParent0, out wrapperPos0, out wrapperRot0);
            Save(foodT, out foodParent0, out foodPos0, out foodRot0);

            canHolder = NewHolder("EatPackHolder", rightHandBone, def.canPos, def.canRot);
            canT.SetParent(canHolder.transform, false);
            if (wrapperT != null)
            {
                wrapperT.SetParent(canT, false);
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

            // The lid-roll drives its own speed in StepGesture. Otherwise the animator
            // is frozen, except during a per-bite play pulse (Time.time < playUntil).
            if (!playingOpen)
                instance.FirearmsAnimator?.SetAnimationSpeed(Time.time < playUntil ? spawnAnimSpeed : 0f);

            // Keep the rig glued to the body while walking. Mid-eat nothing else does
            // this (IKManager.Update body-follow + VRPlayerManager's camRoot pins are
            // gated out, and the gun's HandsPositioner is disabled), so the rig root
            // drifts from the body and the IK hands jitter. Replicate the live
            // HandsPositioner's per-frame work (Update phase).
            if (driveBodyFollowDuringEat)
            {
                if (VRGlobals.ikManager != null) VRGlobals.ikManager.MatchLegsToArms();
                PinRigToBody();
            }
            DriveArms();
            StepGesture();
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

        // Discard the eaten food — but ONLY after the cancel has fully switched off the
        // meds controller (food returned to the bag + weapon drawn). The game's cancel
        // sequence returns the food to inventory AS PART OF drawing the weapon, so
        // removing it any earlier corrupts the return: the weapon never comes back and the
        // right hand (which the returning weapon controller drives) is left stuck while the
        // left hand still works. Once HandsController has left the meds controller, the
        // return is done and discarding the (now backpacked) food is safe.
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
                // simulate: true => BUILD the operation only. RunNetworkTransaction then
                // executes it once (and replicates). Passing false here executes the
                // discard immediately AND returns the op, so RunNetworkTransaction ran it
                // a second time on the now-detached item -> get_Parent() threw.
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

        // Point the arms' elbow bend goals at the VR bend goals (DynamicElbowPositioner moves
        // them), exactly like the gun/empty-hands states do. The meds controller spawns with
        // _elbowBends pointed at its OWN Bend_Goal_Left/Right (at the animation pose), which
        // bends the elbows wrong — so we re-assert the VR goals each frame while eating. The
        // index->hand mapping flips with handedness (mirrors VRPlayerManager.Right/LeftHandedMode).
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
            if (VRGlobals.ikManager.leftArmIk != null)  { VRGlobals.ikManager.leftArmIk.solver.target = null;  VRGlobals.ikManager.leftArmIk.enabled = false; }
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

        // Runs in LateUpdate (after the animator). The animator keeps writing the
        // prop's local transform every frame; we zero it so the prop sits exactly on
        // its holder. Then the HOLDER (which the animator never touches) is the clean
        // hold offset you can tune in Unity Explorer.
        public static void LateZeroProps()
        {
            if (!active || !reparented) return;
            if (def != null && def.kind == FoodKind.Handheld)
            {
                ZeroLocal(canT); // bar sits on its holder
                // The animator keeps rewriting these locals each frame, so re-pin them:
                //  - wrapperT (sn_CAT, carrying the chocolate) stays glued to the bar.
                //  - coverT (sn_cover) peels in place until detached, then sits on the
                //    left-hand holder (local zero) once it's in the off hand.
                if (wrapperT != null) { wrapperT.localPosition = wrapperLocalPos; wrapperT.localRotation = wrapperLocalRot; }
                if (coverT != null && coverDetached) ZeroLocal(coverT);
            }
            else if (def != null && def.kind == FoodKind.Bag)
            {
                ZeroLocal(canT); // bag sits on its holder (carries the in-bag crackers + corner)
                // Once shaken out, the hold-crackers live under the left-hand holder; pin
                // each to its captured clump layout so the animator can't drag them off.
                if (crackersShown && crackerT != null)
                    for (int i = 0; i < crackerT.Length; i++)
                    {
                        if (crackerT[i] == null) continue;
                        crackerT[i].localPosition = crackerLocalPos[i];
                        crackerT[i].localRotation = crackerLocalRot[i];
                    }
            }
            else if (def != null && def.kind == FoodKind.Pack)
            {
                ZeroLocal(canT); // pack sits on its holder
                // wrapper group (cover + pile + food) stays glued to the pack.
                if (wrapperT != null) { wrapperT.localPosition = wrapperLocalPos; wrapperT.localRotation = wrapperLocalRot; }
                // once taken, the food piece sits on the left-hand holder.
                if (foodHolder != null && foodT != null && foodT.parent == foodHolder.transform) ZeroLocal(foodT);
            }
            else
            {
                ZeroLocal(canT);
                ZeroLocal(spoonT);
                ZeroLocal(foodT);
            }

            // LateUpdate-phase rig pins (after the IK solve), mirroring the live
            // HandsPositioner.LateUpdate: re-pin the body rotation to handsRotation and
            // the rig root to the body. Without this mid-eat the rig drifts post-solve
            // and the hands jitter while walking.
            if (driveBodyFollowDuringEat)
            {
                if (medsBody != null && VRGlobals.vrPlayer != null)
                    medsBody.rotation = VRGlobals.vrPlayer.handsRotation;
                PinRigToBody();
            }
        }

        // Pins the rig root (camRoot) to the body anchor (the meds controller object,
        // which VRPlayerManager keeps on the ribcage), exactly like the live
        // HandsPositioner does for empty hands. This is the per-frame coupling that's
        // missing mid-eat and causes the walking jitter.
        private static void PinRigToBody()
        {
            if (medsBody == null || VRGlobals.camRoot == null || VRGlobals.player == null) return;
            VRGlobals.camRoot.transform.position = new Vector3(
                medsBody.position.x,
                VRGlobals.player.Transform.position.y + 1.5f,
                medsBody.position.z);
        }

        // Hand-pin runs from the IK solver's OnPostUpdate (see Subscribe/Unsubscribe
        // PinAfterIk) — Player.LateUpdate is too early (FinalIK solves after it and
        // overwrites). OnPostUpdate fires right after each arm solves.
        private static void PinLeftAfterIk()  { if (active && !manualDone && driveHandsToTargets) PinBoneToTarget(VRGlobals.ikManager?.leftArmIk, LeftHand()); }
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
                Restore(canT, canParent0, canPos0, canRot0);
                Restore(spoonT, spoonParent0, spoonPos0, spoonRot0);
                Restore(foodT, foodParent0, foodPos0, foodRot0);
                Restore(wrapperT, wrapperParent0, wrapperPos0, wrapperRot0); // handheld (null-safe)
                Restore(coverT, coverParent0, coverPos0, coverRot0);         // handheld (null-safe)
                if (crackerT != null) // bag crackers back to the bag, re-shown
                    for (int i = 0; i < crackerT.Length; i++)
                    {
                        Restore(crackerT[i], crackerParent0[i], crackerPos0[i], crackerRot0[i]);
                        SetRenderer(crackerR[i], true);
                    }
                SetRenderer(spoonR, true);
                SetRenderer(foodR, true);
                if (canHolder != null) UnityEngine.Object.Destroy(canHolder);
                if (spoonHolder != null) UnityEngine.Object.Destroy(spoonHolder);
                if (foodHolder != null) UnityEngine.Object.Destroy(foodHolder);
                if (crackerHolder != null) UnityEngine.Object.Destroy(crackerHolder);
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] RestoreProps error: {ex}"); }
            canHolder = spoonHolder = foodHolder = crackerHolder = null;
            reparented = false;
        }

        //--- Gesture state machine --------------------------------------------------
        private static void StepGesture()
        {
            if (def != null && def.kind == FoodKind.Handheld) { StepGestureHandheld(); return; }
            if (def != null && def.kind == FoodKind.Bag) { StepGestureBag(); return; }
            if (def != null && def.kind == FoodKind.Pack) { StepGesturePack(); return; }
            switch (phase)
            {
                case Phase.Closed:
                    if (!playingOpen)
                    {
                        // Right hand to the can + trigger -> roll the lid open by
                        // playing STATE_OPEN forward (real animation: lid rolls, the
                        // hand grabs the spoon (if any), and the eat's own sounds fire).
                        if (RightHandNear(LeftHand(), openDistance) && TriggerEdge())
                        {
                            playingOpen = true;
                            controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                            Pulse();
                            Plugin.MyLog.LogInfo("[ManualEat] Opening — rolling lid...");
                        }
                    }
                    else
                    {
                        // Hold once the lid is open (+ spoon grabbed, if this food uses one).
                        var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                        bool past = st.fullPathHash != STATE_OPEN_HASH || st.normalizedTime >= openHoldTime;
                        if (past)
                        {
                            playingOpen = false;
                            controller.FirearmsAnimator?.SetAnimationSpeed(0f);
                            SetRenderer(spoonR, true); // no-op if no spoon
                            phase = Phase.Ready;
                            Plugin.MyLog.LogInfo($"[ManualEat] Opened — ready to take food (spoon={def.HasSpoon}).");
                        }
                    }
                    break;

                case Phase.Ready:
                    // Right hand to the can -> take food (onto the spoon, or grabbed in hand).
                    if (RightHandNear(LeftHand(), scoopDistance))
                    {
                        if (logGesturePose) Plugin.MyLog.LogInfo($"[ManualEat] grab #{biteCount + 1} pulse from {CurStateStr()}");
                        SetRenderer(foodR, true);
                        phase = Phase.Holding;
                        StartGesturePulse(grabPoseState, grabPoseTime); // play the scoop/grab motion
                        Pulse();
                        PlaySound(def.scoopSound);
                        Plugin.MyLog.LogInfo($"[ManualEat] Took food (bite {biteCount + 1}/{def.bites}).");
                    }
                    break;

                case Phase.Holding:
                    if (HandAtMouth())
                    {
                        if (logGesturePose) Plugin.MyLog.LogInfo($"[ManualEat] eat #{biteCount + 1} pulse from {CurStateStr()}");
                        SetRenderer(foodR, false);
                        biteCount++;
                        Pulse();
                        PlaySound(def.eatSound);
                        Plugin.MyLog.LogInfo($"[ManualEat] Ate bite {biteCount}/{def.bites}.");

                        if (biteCount >= def.bites)
                        {
                            FinishSequence(); // drives STATE_END itself; no eat pulse needed
                        }
                        else
                        {
                            StartGesturePulse(eatPoseState, eatPoseTime); // play the bite motion
                            phase = Phase.Ready;
                        }
                    }
                    break;

                case Phase.Done:
                    break;
            }
        }

        // Handheld (chocolate bar): bar held in the RIGHT hand. Bring the OFF (left) hand to
        // the bar + its trigger -> peel the wrapper (play STATE_USE forward to openHoldTime;
        // sn_cover peels, Open sound fires). Then bring the bar (right hand) to the mouth ->
        // one bite -> finish. The whole clip is STATE_USE, so the open monitors that state.
        private static void StepGestureHandheld()
        {
            switch (phase)
            {
                case Phase.Closed:
                    if (!playingOpen)
                    {
                        if (LeftHandNear(RightHand(), openDistance) && OffTriggerEdge())
                        {
                            playingOpen = true;
                            controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                            Pulse();
                            Plugin.MyLog.LogInfo("[ManualEat] Opening wrapper...");
                        }
                    }
                    else
                    {
                        var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                        bool past = st.fullPathHash != STATE_USE_HASH || st.normalizedTime >= openHoldTime;
                        if (past)
                        {
                            playingOpen = false;
                            controller.FirearmsAnimator?.SetAnimationSpeed(0f);
                            DetachWrapperToLeftHand(); // you peeled it off -> it stays in your left hand
                            phase = Phase.Ready;
                            Plugin.MyLog.LogInfo("[ManualEat] Wrapper open — ready to bite.");
                        }
                    }
                    break;

                case Phase.Ready:
                    // Bring the bar (right hand) to the mouth -> bite (Eat sound) -> finish
                    // immediately (no chew wait).
                    if (HandAtMouth())
                    {
                        biteCount++;
                        Pulse();
                        PlaySound(def.eatSound);
                        Plugin.MyLog.LogInfo($"[ManualEat] Ate bite {biteCount}/{def.bites}.");
                        if (biteCount >= def.bites)
                            FinishSequence();
                        // else: stay in Ready — bring it to the mouth again for the next bite.
                    }
                    break;

                case Phase.Done:
                    break;
            }
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

        // Bag (croutons): bag held in the RIGHT hand. LEFT hand to the bag + off-trigger ->
        // open (tear the corner, STATE_OPEN). Then SHAKE the bag near the left hand -> pour
        // the crackers into the left hand. Bring the LEFT hand to the mouth -> eat. Repeat
        // for `bites` rounds, then put away.
        private static void StepGestureBag()
        {
            switch (phase)
            {
                case Phase.Closed:
                    if (!playingOpen)
                    {
                        if (LeftHandNear(RightHand(), openDistance) && OffTriggerEdge())
                        {
                            playingOpen = true;
                            controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                            Pulse();
                            Plugin.MyLog.LogInfo("[ManualEat] Opening bag...");
                        }
                    }
                    else
                    {
                        var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                        bool past = st.fullPathHash != STATE_OPEN_HASH || st.normalizedTime >= openHoldTime;
                        if (past)
                        {
                            playingOpen = false;
                            controller.FirearmsAnimator?.SetAnimationSpeed(0f);
                            phase = Phase.Ready;
                            ResetShake();
                            Plugin.MyLog.LogInfo("[ManualEat] Bag open — shake it over your left hand.");
                        }
                    }
                    break;

                case Phase.Ready:
                    // Shake the bag (right hand) near the left hand to pour crackers out.
                    if (DetectShake())
                    {
                        ShakeOutCrackers();
                        // Push the arms animator OUT of STATE_OPEN into the use phase (like the
                        // cans' scoop). The finish cancels from STATE_USE cleanly; cancelling
                        // while still frozen in STATE_OPEN leaves the use op half-started ->
                        // "busy hands" + the food-return collides with our discard (it flashes
                        // instead of leaving).
                        EnterUseState();
                        PlaySound(def.scoopSound);
                        phase = Phase.Holding;
                        Plugin.MyLog.LogInfo($"[ManualEat] Shook out crackers (round {biteCount + 1}/{def.bites}).");
                    }
                    break;

                case Phase.Holding:
                    // Eat from the LEFT hand.
                    if (LeftHandAtMouth())
                    {
                        for (int i = 0; i < crackerR.Length; i++) SetRenderer(crackerR[i], false);
                        crackersShown = false;
                        biteCount++;
                        Pulse();
                        PlaySound(def.eatSound);
                        Plugin.MyLog.LogInfo($"[ManualEat] Ate handful {biteCount}/{def.bites}.");
                        if (biteCount >= def.bites)
                            FinishSequence();
                        else { phase = Phase.Ready; ResetShake(); } // shake again
                    }
                    break;

                case Phase.Done:
                    break;
            }
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

        // Pack (galette): pack held in the RIGHT hand. LEFT hand to the pack + off-trigger ->
        // open (cover animates in place, stays on the pack). LEFT hand to the pack -> take a
        // piece into the left hand. LEFT hand to the mouth -> eat. Repeat for `bites`, then
        // put away. Mirror of CanHand (the take/eat hand is the LEFT).
        private static void StepGesturePack()
        {
            switch (phase)
            {
                case Phase.Closed:
                    if (!playingOpen)
                    {
                        if (LeftHandNear(RightHand(), openDistance) && OffTriggerEdge())
                        {
                            playingOpen = true;
                            controller.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                            Pulse();
                            Plugin.MyLog.LogInfo("[ManualEat] Opening pack...");
                        }
                    }
                    else
                    {
                        var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                        bool past = st.fullPathHash != STATE_OPEN_HASH || st.normalizedTime >= openHoldTime;
                        if (past)
                        {
                            playingOpen = false;
                            controller.FirearmsAnimator?.SetAnimationSpeed(0f);
                            phase = Phase.Ready;
                            Plugin.MyLog.LogInfo("[ManualEat] Pack open — pick out a cracker.");
                        }
                    }
                    break;

                case Phase.Ready:
                    // LEFT hand to the pack -> take a piece into the left hand. Replays
                    // STATE_USE@grabPoseTime (hand open -> reach -> pinch), which also leaves
                    // STATE_OPEN so the finish cancels cleanly (see StartGesturePulse).
                    if (LeftHandNear(RightHand(), scoopDistance))
                    {
                        if (logGesturePose) Plugin.MyLog.LogInfo($"[ManualEat] take #{biteCount + 1} pulse from {CurStateStr()}");
                        TakeFoodToLeftHand();
                        StartGesturePulse(grabPoseState, grabPoseTime);
                        Pulse();
                        PlaySound(def.scoopSound);
                        phase = Phase.Holding;
                        Plugin.MyLog.LogInfo($"[ManualEat] Took a cracker (bite {biteCount + 1}/{def.bites}).");
                    }
                    break;

                case Phase.Holding:
                    // Eat from the LEFT hand.
                    if (LeftHandAtMouth())
                    {
                        if (logGesturePose) Plugin.MyLog.LogInfo($"[ManualEat] eat #{biteCount + 1} pulse from {CurStateStr()}");
                        SetRenderer(foodR, false);
                        biteCount++;
                        Pulse();
                        PlaySound(def.eatSound);
                        Plugin.MyLog.LogInfo($"[ManualEat] Ate cracker {biteCount}/{def.bites}.");
                        if (biteCount >= def.bites)
                            FinishSequence();
                        else
                        {
                            StartGesturePulse(eatPoseState, eatPoseTime); // play the bite motion
                            phase = Phase.Ready; // pick another
                        }
                    }
                    break;

                case Phase.Done:
                    break;
            }
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
                    if (VRGlobals.vrPlayer != null)
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
        private static Transform LeftHand()  => VRGlobals.vrPlayer?.LeftHand  != null ? VRGlobals.vrPlayer.LeftHand.transform  : null;

        private static bool RightHandNear(Transform target, float dist)
        {
            Transform right = RightHand();
            if (right == null || target == null) return false;
            return Vector3.Distance(right.position, target.position) < dist;
        }

        private static bool LeftHandNear(Transform target, float dist)
        {
            Transform left = LeftHand();
            if (left == null || target == null) return false;
            return Vector3.Distance(left.position, target.position) < dist;
        }

        private static bool HandAtMouth() => HandAtMouth(RightHand());
        private static bool LeftHandAtMouth() => HandAtMouth(LeftHand());

        private static bool HandAtMouth(Transform hand)
        {
            Transform head = GetHead();
            if (head == null || hand == null) return false;
            Vector3 delta = hand.position - head.position;
            if (delta.magnitude > eatDistance) return false;
            return Vector3.Dot(delta.normalized, head.forward) > mouthForwardDot;
        }

        private static Transform GetHead()
        {
            if (VRGlobals.VRCam != null) return VRGlobals.VRCam.transform;
            return Camera.main != null ? Camera.main.transform : null;
        }

        // Dominant-hand trigger edge (right in normal mode) — the can-opening hand.
        private static bool TriggerEdge() => TriggerEdgeImpl(dominant: true);
        // Off-hand trigger edge (left in normal mode) — the handheld wrapper-peeling hand.
        private static bool OffTriggerEdge() => TriggerEdgeImpl(dominant: false);

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
            phase = Phase.Closed;
            biteCount = 0;
            canT = spoonT = foodT = null;
            wrapperT = coverT = null;
            coverParent0 = null;
            coverDetached = false;
            leftHandBoneRef = null;
            crackerT = null; crackerR = null;
            crackerParent0 = null; crackerPos0 = null; crackerRot0 = null;
            crackerLocalPos = null; crackerLocalRot = null;
            crackerHolder = null; crackersShown = false;
            shakeReversals = 0; shakeWindowEnd = 0f; shakePrevPos = shakePrevVel = Vector3.zero;
            medsBody = null;
            spoonR = foodR = null;
            soundPlayer = null;
            canHolder = spoonHolder = foodHolder = null;
            canParent0 = spoonParent0 = foodParent0 = wrapperParent0 = null;
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
