using EFT.InventoryLogic;
using System;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Controls;
using static EFT.Player;
using static EFT.Player.MedsController;

namespace TarkovVR.Source.Player.Interactions
{
    // Manual, two-handed, gesture-driven eating. Each handled food has a FoodDef; foods without one eat the vanilla way.
    //  * The eat is pure bone animation (renderers never toggle), and the animator re-applies its bone pose every frame,
    //    even at speed 0. So the props are reparented out of the animated rig onto the VR hands, and put back on teardown
    //    because the object is pooled.
    //  * Meds raises LEFT_HAND_ANIMATOR_HASH and freezes the left hand; EatingPatches.ForceLeftHandTracking (a prefix on
    //    VRPlayerManager.UpdateLeftHand) keeps it tracking.
    //  * The finish needs the animation to reach END, or the hands stay stuck.
    //
    // Partial-class files:
    //   EatingInteractionController.cs           - lifecycle: spawn, gates, Tick, finish, teardown
    //   EatingInteractionController.State.cs     - tunables and per-eat runtime state
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
        // True while a pull-open has the off hand latched onto the held item. ForceLeftHandTracking then keeps updating the
        // rig target (so release lands on the controller) but must not re-point that arm's IK at it: it runs on the
        // SteamVR pose callback, after Tick aimed the IK at the latch, and would pull the hand off the item.
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
                if (!(instance.Item is FoodDrink)) return;

                FoodDef foodDef = FindDef(instance.Item?.TemplateId);
                if (foodDef == null) return; // no manual sequence -> vanilla

                active = true;
                controller = instance;
                def = foodDef;
                openReadyTime = foodDef.openReadyTime; // per-food timings, copied into statics that stay tunable during the eat
                putAwayStartTime = foodDef.putAwayStartTime;
                pullStartTime = foodDef.pullStartTime;
                pullEndTime = foodDef.pullEndTime;
                deterministicGesturePose = foodDef.deterministicGesture;
                takePoseTime = foodDef.takePoseTime;
                bitePoseTime = foodDef.bitePoseTime;
                reachStartTime = foodDef.reachStartTime; // ReachBag reach segment (unused by other kinds)
                reachDeepTime = foodDef.reachDeepTime;
                eatLoopState = foodDef.eatLoopState;     // timed-eat/gulp loop state (0 = STATE_USE)
                openPlayState = foodDef.openPlayState;   // one-shot open animation (0 = none; noodles' rip)
                openPlayMaxTime = foodDef.openPlayMaxTime;
                takePoseState = STATE_USE_HASH;  // shared by all foods; reset in case they were changed live
                bitePoseState = STATE_EAT_HASH;
                // Interaction zones (radii and offsets), copied into tunable statics like the timings above.
                openDistance = foodDef.openZoneRadius; scoopDistance = foodDef.takeZoneRadius; eatDistance = foodDef.eatZoneRadius;
                openZoneOffset = foodDef.openZoneOffset; takeZoneOffset = foodDef.takeZoneOffset; eatZoneOffset = foodDef.eatZoneOffset;
                style = BuildStyle(foodDef); // per-archetype gesture descriptor
                phase = foodDef.skipOpen ? Phase.Ready : Phase.Closed; // skipOpen = straight to "bring to mouth"
                biteCount = 0;
                spawnAnimSpeed = animationSpeed <= 0f ? 1f : animationSpeed;
                ReadDrinkState(instance.Item); // resource left and full-item use-time (drinks, partly eaten items)

                LogStep($"[ManualEat] Armed manual sequence for {foodDef.templateId} "
                    + $"(openGrip={(string.IsNullOrEmpty(foodDef.openGripName) ? "none" : foodDef.openGripName)}, "
                    + $"handPath={(foodDef.openHandPath != null ? foodDef.openHandPath.Length / 8 + " keys" : "none")}, "
                    + $"reachPath={(foodDef.reachHandPath != null ? foodDef.reachHandPath.Length / 8 + " keys" : "none")}, "
                    + $"openState={style.openStateHash}).");
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[ManualEat] OnSpawnPre error: {exception}");
                Reset();
            }
        }

        // Snapshots the item's resource and timing. HpPercent is the current amount in resource units (not 0-100; it starts
        // at MaxResource), so remaining = HpPercent / MaxResource: a half-drunk bottle has half a use-time left. UseTime (for
        // the whole item) comes from the item template ("foodUseTime") unless the FoodDef overrides it.
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
                if (drinkUseTime <= 0f && item?.Template is FoodDrinkTemplate template)
                    drinkUseTime = template.UseTime;
            }
            catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] ReadDrinkState: {exception.Message}"); }
            if (drinkUseTime <= 0f) drinkUseTime = 5f; // fallback when the template has no usable time
            if (def.kind == FoodKind.Drink)
                LogStep(drinkFdc != null
                    ? $"[ManualEat] Drink: {drinkFdc.HpPercent:F0}/{drinkFdc.MaxResource:F0} left ({drinkRemainingFrac:P0}), {drinkUseTime * drinkRemainingFrac:F1}s at the mouth to finish."
                    : "[ManualEat] Drink has NO FoodDrinkComponent — the bar can't tick down; the finish falls back to the use-time clock.");
        }

        // Postfix on Spawn: the props are built now. Find them, freeze the animator, reparent them to the hands.
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

                // Parent the props to the IK hand bones (solver.bone3). They follow the controllers, unlike the animation
                // rig's palms, and share the palm orientation.
                Transform leftHandBone = VRGlobals.ikManager?.leftArmIk?.solver?.bone3?.transform;
                Transform rightHandBone = VRGlobals.ikManager?.rightArmIk?.solver?.bone3?.transform;
                if (leftHandBone == null || rightHandBone == null)
                {
                    Plugin.MyLog.LogError("[ManualEat] No IK hand bones — vanilla fallback.");
                    Reset();
                    return;
                }
                leftHandBoneRef = leftHandBone; // for the handheld wrapper's left-hand holder (built on open)

                bool ok = def.kind == FoodKind.Handheld ? SetupHandheldProps(root, rightHandBone)
                        : def.kind == FoodKind.Drink ? SetupDrinkProps(root, leftHandBone, rightHandBone)
                        : def.kind == FoodKind.Bag ? SetupBagProps(root, rightHandBone)
                        // ReachBag shares Pack's prop wiring: held root on the right palm, hidden food piece taken into
                        // the left hand (no wrapper).
                        : def.kind == FoodKind.Pack || def.kind == FoodKind.ReachBag ? SetupPackProps(root, rightHandBone)
                        : SetupCannedProps(root, leftHandBone, rightHandBone);
                if (!ok) return; // the failure is already logged and Reset

                // Segmented snack: the held item's renderers, sorted by name. Each audible bite hides one, from the tip inward.
                if (def.segmentedBites && baseT != null)
                {
                    segR = baseT.GetComponentsInChildren<Renderer>(true);
                    Array.Sort(segR, (a, b) => string.CompareOrdinal(a.name, b.name));
                    LogStep($"[ManualEat] segmented bites: {segR.Length} renderers under '{baseT.name}'.");
                }

                // The hide-on-open target is the torn cover or lid mesh. It is a sibling of the held bones, so it is searched
                // under the whole item root, not baseT. It is hidden when the open completes so it doesn't float next to the
                // bag, and shown again on teardown.
                if (!string.IsNullOrEmpty(def.hideOnOpenName))
                {
                    Transform t = FindDeep(root, def.hideOnOpenName);
                    hideOnOpenR = t != null ? t.GetComponentsInChildren<Renderer>(true) : null;
                    LogStep(hideOnOpenR != null
                        ? $"[ManualEat] hideOnOpen '{def.hideOnOpenName}': {hideOnOpenR.Length} renderer(s) will hide after the open."
                        : $"[ManualEat] hideOnOpen '{def.hideOnOpenName}' not found — nothing will hide.");
                }

                // The optional bone the latched hand rides during the pull-open. It is resolved after the props moved to the
                // hands, in the held item (baseT) first: these rigs have duplicate node names, and a same-named node left on
                // the body-space rig also animates with the scrub, far from the item.
                if (!string.IsNullOrEmpty(def.openGripName))
                {
                    openGripT = FindDeep(baseT, def.openGripName);
                    if (openGripT != null)
                        LogStep($"[ManualEat] openGrip bone '{def.openGripName}' resolved under the held item.");
                    else
                    {
                        openGripT = FindDeep(root, def.openGripName);
                        Plugin.MyLog.LogWarning(openGripT != null
                            ? $"[ManualEat] openGrip '{def.openGripName}' only exists OUTSIDE the held item — it stays on the body-space rig and will NOT line up with your hand."
                            : $"[ManualEat] openGrip '{def.openGripName}' not found — the pull latch will freeze at the grab pose.");
                    }
                }

                SubscribePinAfterIk();

                LogStep($"[ManualEat] Props reparented to hands (kind={def.kind}).");
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[ManualEat] OnSpawnPost error: {exception}");
                End();
            }
        }

        // A def matched but its props aren't in this rig (wrong bone names), so fall back to a real vanilla eat. By the Spawn
        // postfix, the gate has already blocked Spawn's method_5 call (the consume) and the animator is frozen. Re-fire the
        // stashed op and unfreeze before Reset, or the food hangs with no effect running.
        private static bool BailToVanilla(string why)
        {
            Plugin.MyLog.LogError($"[ManualEat] {why} — vanilla fallback.");
            try
            {
                if (pendingOp != null && !effectFired)
                {
                    effectFired = true;   // so AllowEffect lets it through
                    pendingOp.GoNextBodyPart(); // run the real consume the gate swallowed
                }
                controller?.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[ManualEat] BailToVanilla: {exception}"); }
            Reset();
            return false;
        }

        // ===== Gates =====
        public static bool AllowEffect(MedsInHandsOperation op)
        {
            MedsController owner = op?._controller;
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

            // The animator stays frozen except during the open (StepGesture drives it), the one-shot open animation
            // (playingOpenAnim) and the short play pulse for each bite or gulp loop (Time.time < playUntil).
            if (!playingOpen && !playingOpenAnim)
                instance.FirearmsAnimator?.SetAnimationSpeed(Time.time < playUntil ? spawnAnimSpeed : 0f);

            // During the eat nothing else pins the rig to the body; without this the IK hands jitter while walking.
            if (VRGlobals.ikManager != null) VRGlobals.ikManager.MatchLegsToArms();
            PinRigToBody();
            DriveArms();

            // A finish queued from STATE_OPEN waits here for STATE_USE to engage (see RequestFinish).
            if (finishPending)
            {
                if (!InOpenState() || Time.time >= finishDeadline)
                {
                    finishPending = false;
                    FinishSequence(finishIsCancel);
                }
                return;
            }

            // Both triggers together cancel: the food is put away unconsumed (a mid-drink cancel still applies what was drunk).
            // Only the press counts, so holding them doesn't fire again.
            bool both = VRInput.LeftTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f
                     && VRInput.RightTrigger.GetAxis(SteamVR_Input_Sources.Any) > 0.5f;
            bool bothEdge = both && !prevBothTriggers;
            prevBothTriggers = both;
            if (bothEdge)
            {
                LogStep("[ManualEat] Both-trigger cancel — putting the food away.");
                ReattachCap(); // no-op unless a drink cap is off
                RequestFinish(cancel: true);
                return;
            }

            StepGesture();
        }

        // Runs the finish, but never from STATE_OPEN. Cancelling a use frozen in the draw/open state leaves the op half-started:
        // the hands stay busy, and the food flashes in the inventory and won't discard. If the arms are still in STATE_OPEN,
        // push them into STATE_USE and let Tick complete the finish once that state is active (finishDeadline = give-up fallback).
        private static void RequestFinish(bool cancel)
        {
            EndPull(immediate: true);  // a cancel can come mid pull-open: unlatch so the put-away animation controls the arms
            EndReach(immediate: true); // or mid reach-in (same latch, same reason)
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

        // Teardown entry point for the MedsController lifecycle patches (WeapOut / Destroy). They fire for every
        // MedsController in the raid, bots and other players too. Only tear down for the controller we armed, or a bot
        // putting its meds away ends the local eat. controller is only set after the IsYourPlayer check.
        public static void OnControllerGone(MedsController instance)
        {
            if (instance == null || instance != controller) return;
            End();
        }

        public static void End()
        {
            try { controller?.FirearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed); } catch { }
            UnsubscribePinAfterIk();
            TarkovVR.Source.Misc.AudioHaptics.Stop(); // stop the audio haptics pump and tap (the controller object is pooled)
            RestoreProps();
            ReleaseArms();
            LogStep("[ManualEat] End() — cleaned up.");
            Reset();
        }

        // Discards the eaten food, but only after the cancel has fully switched off the meds controller. The cancel returns
        // the food to the bag as part of drawing the weapon: discarding earlier corrupts the return (the weapon never
        // comes back, the right hand sticks).
        private static System.Collections.IEnumerator ConsumeFoodWhenSettled(EFT.InventoryLogic.Item item)
        {
            MedsController meds = controller; // capture before teardown nulls it
            float timeout = Time.time + 6f;
            while (Time.time < timeout && StillHoldingMeds(meds))
                yield return null;
            // Wait a little longer so the switch transaction is fully settled before the discard
            yield return new WaitForSeconds(0.5f);
            // As a co-op client, also wait for the host's answer. The host's copy of the player runs the put-away and the draw
            // at full length, and a move sent before that is refused ("Default Inventory is currently being modified").
            float hostWaitSince = Time.time;
            while (ModSupport.CoopHost.HoldsBack(hostWaitSince))
                yield return null;
            if (HasParent(item)) DiscardFood(item);
            else Plugin.MyLog.LogWarning("[ManualEat] food not in a container — skipped discard.");
        }

        private static bool StillHoldingMeds(MedsController meds)
        {
            try { return meds != null && VRGlobals.player != null && ReferenceEquals(VRGlobals.player.HandsController, meds); }
            catch { return false; }
        }

        // Helpers that remove the eaten food from the inventory.
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
                // simulate:true only builds the op, and RunNetworkTransaction executes it once. false would execute it here
                // and also return it, so it would run a second time on a parentless item, which throws.
                var result = ItemManipulator.Discard(item, ic, simulate: true);
                if (result.Succeeded)
                {
                    ic.RunNetworkTransaction(result.Value);
                    LogStep("[ManualEat] Consumed (discarded) food item.");
                }
                else Plugin.MyLog.LogWarning("[ManualEat] DiscardFood: discard not allowed.");
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[ManualEat] DiscardFood error: {exception}"); }
        }

        // Runs after the final bite, a finished drink, or a cancel. Applies the consumed fraction's nutrition and tells the
        // animator the use is done (SetActiveParam(false), as the game's own method_9 does), which drives it to the END
        // (put-away) state regardless of the effect timer. The END animation then fires the weapon-out event.
        // cancel = true consumes nothing extra, but a drink still applies what was drunk before the cancel.
        private static void FinishSequence(bool cancel = false)
        {
            phase = Phase.Done;
            manualDone = true; // stop the hand-pin/IK so the END animation moves the hands (put-away)
            drinkingHeld = false;
            playingOpen = false; // a cancel can come mid open; don't leave the open flag stuck
            playingOpenAnim = false; // or during the one-shot open animation (the rip)
            ReleaseArms();

            if (effectFired) { Plugin.MyLog.LogWarning("[ManualEat] Finish: effect already fired."); return; }
            if (pendingOp == null) { Plugin.MyLog.LogError("[ManualEat] Finish: pendingOp NULL — may stick!"); return; }

            // What this use consumed, as a 0..1 fraction of the whole item (what CutPiece takes). Timed eats (drinks and
            // eatByTime snacks): time at the mouth / full-item use-time, capped at what was left. Bite foods: everything left
            // on a normal finish, or on a cancel only what the bites and the mouth already applied (the item keeps the rest).
            float fraction;
            if (def != null && (def.kind == FoodKind.Drink || def.eatByTime))
                fraction = Mathf.Min(drinkUseTime > 0f ? drinkHeldTime / drinkUseTime : drinkRemainingFrac, drinkRemainingFrac);
            else
                fraction = cancel ? Mathf.Min(drinkAppliedFrac, drinkRemainingFrac) : drinkRemainingFrac;

            effectFired = true;
            LogStep($"[ManualEat] Finish (cancel={cancel}, fraction={fraction:F2}): method_5 + SetActiveParam(false) -> play END.");
            try
            {
                var firearmsAnimator = controller.FirearmsAnimator;
                firearmsAnimator?.SetNextLimb(false);

                // The fire-cancel aborts the over-time heal effect: apply the consumed energy/hydration now and drain the
                // item's resource by the same fraction. The live drink drain and the per-bite drain already applied
                // drinkAppliedFrac of it, so only the remainder goes here; emptied is read off the live resource.
                float remainder = fraction - drinkAppliedFrac;
                if (remainder > 0.0001f)
                {
                    ApplyConsumedNutrition(remainder);
                    drinkAppliedFrac = fraction;
                }
                // Chunked float math can leave a hair of resource when this use consumed everything left: snap it to 0 so
                // the empty-discard fires.
                if (drinkFdc != null && fraction >= drinkRemainingFrac - 0.001f
                    && drinkFdc.HpPercent > 0f && drinkFdc.HpPercent <= drinkFdc.MaxResource * 0.01f)
                    drinkFdc.HpPercent = 0f;
                bool emptied = drinkFdc != null
                    ? drinkFdc.HpPercent <= 0.0001f
                    : fraction >= drinkRemainingFrac - 0.0001f; // no resource component = one-shot
                LogStep(drinkFdc != null
                    ? $"[ManualEat] Consumed {fraction:P0} ({drinkFdc.HpPercent:F1}/{drinkFdc.MaxResource:F0} left{(emptied ? " — empty" : "")})."
                    : $"[ManualEat] Consumed {fraction:P0} (no resource component{(emptied ? " — treated as empty" : "")}).");
                pendingOp.GoNextBodyPart();   // leaves the controller in the state the cancel needs
                // SetActiveParam(false) is required: it flips the controller out of mid-use into the cancellable/put-away
                // state. Without it the controller stays frozen and the fire-cancel has nothing to act on.
                firearmsAnimator?.SetActiveParam(false, false);
                firearmsAnimator?.SetAnimationSpeed(spawnAnimSpeed);
                // Jump the arms animator straight to STATE_END, skipping the rest of STATE_EAT (the auto bite).
                try { controller._player.ArmsAnimatorCommon.Play(STATE_END_HASH, 1, putAwayStartTime); }
                catch (Exception exception) { Plugin.MyLog.LogWarning($"[ManualEat] skip-bite Play failed: {exception.Message}"); }
                // Auto-fire the fire cancel (press, then release on the next input frame): puts the food away and draws
                // the weapon fast.
                TarkovVR.Source.Controls.VRInputManager.ForceCommand(EFT.InputSystem.ECommand.ToggleShooting);
                TarkovVR.Source.Controls.VRInputManager.ForceCommand(EFT.InputSystem.ECommand.EndShooting);
                // The cancel aborts the vanilla resource depletion, so an emptied item is never removed: discard it
                // ourselves once the put-away has settled it back into a container (the discard needs a stable parent).
                // A partially used item keeps its remaining resource and stays.
                if (emptied && VRGlobals.vrPlayer != null)
                    VRGlobals.vrPlayer.StartCoroutine(ConsumeFoodWhenSettled(controller.Item));
            }
            catch (Exception exception) { Plugin.MyLog.LogError($"[ManualEat] Finish error: {exception}"); }
        }

        // Applies the consumed fraction's energy/hydration immediately (the fire-cancel aborts the vanilla over-time heal
        // effect) and drains the item's resource by the same fraction. CutPiece takes a 0..1 fraction of the whole item,
        // not resource units. Returns true when the item is now empty (the caller discards it). quiet = no per-call log
        // (the live drink drain calls this in small chunks).
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

                // Drain the resource (HpPercent is in resource units, not 0-100) and refresh the inventory bar.
                // No FoodDrinkComponent = single-use: treat as emptied.
                if (fdc == null) return true;
                fdc.HpPercent = Mathf.Max(0f, fdc.HpPercent - fdc.MaxResource * fraction);
                item.RaiseRefreshEvent();
                if (!quiet)
                    LogStep($"[ManualEat] Nutrition for {fraction:P0} of the item ({fdc.HpPercent:F1}/{fdc.MaxResource:F0} left).");
                return fdc.HpPercent <= 0.0001f;
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[ManualEat] ApplyConsumedNutrition error: {exception}");
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
            handLatched = false; latchedHandIsDominant = false; latchReleasing = false;
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
