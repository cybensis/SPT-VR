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
    //
    // Split across partial-class files (one class, grouped by concern):
    //   EatingInteractionController.cs           - lifecycle: spawn, gates, Tick, finish, teardown
    //   EatingInteractionController.State.cs     - tunables (live A/B knobs) + per-eat runtime state
    //   EatingInteractionController.Foods.cs     - FoodDef, factories/presets/chain wrappers, the Defs registry, DumpFoodDef
    //   EatingInteractionController.HandPaths.cs - baked [HANDPATH]/ReachPath capture data
    //   EatingInteractionController.Props.cs     - prop find/reparent/holders, LateZeroProps, restore, prop actions
    //   EatingInteractionController.Gestures.cs  - EatStyle/BuildStyle + the open/take/eat/drink step machine
    //   EatingInteractionController.Latch.cs     - the hand-latch toolkit (pull latch, bone ride, path replay, reach rail)
    //   EatingInteractionController.Rig.cs       - arm IK/pins, palm probes, zones (+debug viz), triggers, haptics, sounds
    internal static partial class EatingInteractionController
    {
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
                EFT.Player player = instance?._player;
                if (player == null || !player.IsYourPlayer) return;

                Reset();
                if (!VRSettings.GetManualEating()) return;
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
                openPlayState = d.openPlayState;   // one-shot open animation (0 = none; noodles' rip)
                openPlayMaxTime = d.openPlayMaxTime;
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
                    + $"reachPath={(d.reachHandPath != null ? d.reachHandPath.Length / 8 + " keys" : "none")}, "
                    + $"openState={style.openStateHash}).");
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

                // Hide-on-open target: the torn cover/lid mesh (a sibling of the held bones, so
                // resolved under the whole item root, not baseT). Disabled when the open
                // completes so it doesn't float by the bag; re-shown on teardown.
                if (!string.IsNullOrEmpty(def.hideOnOpenName))
                {
                    Transform t = FindDeep(root, def.hideOnOpenName);
                    hideOnOpenR = t != null ? t.GetComponentsInChildren<Renderer>(true) : null;
                    Plugin.MyLog.LogInfo(hideOnOpenR != null
                        ? $"[ManualEat] hideOnOpen '{def.hideOnOpenName}': {hideOnOpenR.Length} renderer(s) will hide after the open."
                        : $"[ManualEat] hideOnOpen '{def.hideOnOpenName}' not found — nothing will hide.");
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

            // Animator stays frozen except during the lid-roll (StepGesture drives it), the
            // one-shot open animation (the rip — playingOpenAnim), and the per-bite / gulp-loop
            // play pulse (Time.time < playUntil).
            if (!playingOpen && !playingOpenAnim)
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

        // Teardown entry point for the MedsController lifecycle patches (WeapOut / Destroy).
        // Those patches fire for EVERY MedsController in the raid (bots/other players included),
        // so only tear down when it's the controller WE armed — otherwise a bot finishing or
        // dropping its meds tears down the LOCAL eat mid-sequence (stuck/looping eat animation;
        // only ever happens with bots present). instance == controller already implies it's the
        // local player (controller is only set in OnSpawnPre after the IsYourPlayer gate).
        public static void OnControllerGone(MedsController instance)
        {
            if (instance == null || instance != controller) return;
            End();
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
            playingOpenAnim = false; // ...or mid one-shot open animation (the rip)
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
            openPlayState = 0; openPlayMaxTime = 1f;
            playingOpenAnim = false; openAnimPlayed = false; openAnimDeadline = 0f;
            hideOnOpenR = null; hiddenOnOpen = false;
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
            nextGulpFallback = 0f;
        }
    }
}
