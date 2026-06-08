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
        private sealed class FoodDef
        {
            public string templateId;
            public string rootName;       // prop root (and the can mesh)
            public string spoonName;      // utensil that goes to the mouth
            public string foodPieceName;  // the bite that appears on the utensil
            public int bites = 3;
            // Sound event names (BaseSoundPlayer.OnSound). Null/empty = no sound.
            public string drawSound;      // played when the item is drawn
            public string[] openSounds;   // played on the "open" gesture
            public string scoopSound;     // played on each scoop  (TODO: capture name)
            public string eatSound;       // played on each bite   (TODO: capture name)
        }

        private static readonly FoodDef[] Defs =
        {
            new FoodDef
            {
                templateId    = "57347d7224597744596b4e72", // tushonka / beef stew
                rootName      = "saira_root",
                spoonName     = "saira_spoon",
                foodPieceName = "saira_foodpiece",
                bites         = 3,
                drawSound     = "Draw",
                openSounds    = new[] { "Open", "Open2", "SpoonTake" },
                scoopSound    = null,     // no distinct scoop sound in vanilla
                eatSound      = "Take",   // the per-bite "take food" sound
            },
        };

        //--- Tunables (public so they can be A/B'd live in the headset) -------------
        public static bool enableManualEating = true;

        // Gesture distances (metres). The gesture uses the controller positions, but
        // the spoon/can sit offset from the controllers, so these are generous.
        public static float openDistance  = 0.18f;
        public static float scoopDistance = 0.18f;
        public static float eatDistance   = 0.23f;
        public static float mouthForwardDot = -0.2f;

        // Hold pose, LOCAL TO THE PALM BONE — auto-derived from the animation's own
        // grip (measured palm->prop at the lid-open hold). Tweak in Unity Explorer
        // on EatCanHolder / EatSpoonHolder if needed.
        public static Vector3 canPosOffset  = new Vector3(-0.1135f, -0.0298f, -0.0034f);
        public static Vector3 canRotOffset  = new Vector3(80.72f, 248.34f, 303.57f);
        public static Vector3 spoonPosOffset = new Vector3(-0.1247f, -0.0537f, -0.0113f);
        public static Vector3 spoonRotOffset = new Vector3(40.67f, 194.91f, 210.26f);
        public static Vector3 foodPosOffset  = new Vector3(0f, 0.05f, 0.007f); // sits in the spoon bowl

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
        private enum Phase { Closed, EmptySpoon, FullSpoon, Done }

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

        // Saved so we can put the props back before the controller object is pooled.
        private static Transform canParent0, spoonParent0, foodParent0;
        private static Vector3 canPos0, spoonPos0, foodPos0;
        private static Quaternion canRot0, spoonRot0, foodRot0;
        private static bool reparented;

        private static bool effectFired;
        private static MedsController.ObservedMedsControllerClass pendingOp;
        private static float prevTriggerAxis;
        private static bool playingManualSound; // true while WE call OnSound (so it's not suppressed)
        private static bool playingOpen;        // playing the STATE_OPEN lid-roll segment

        // The eat's STATE_OPEN (draw->roll lid->grab spoon) on arms layer 1, and the
        // normalizedTime to hold it at (just past SpoonTake@0.85 → lid open, spoon grabbed).
        private const int STATE_OPEN_HASH = 492683391;
        private const int STATE_END_HASH = -1014941517;
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
        // hands-down put-away. Tune live in Unity Explorer.
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

                canT   = FindDeep(root, def.rootName);
                spoonT = FindDeep(root, def.spoonName);
                foodT  = FindDeep(root, def.foodPieceName);

                if (canT == null || spoonT == null || foodT == null)
                {
                    Plugin.MyLog.LogError($"[ManualEat] Missing props (can={canT != null} spoon={spoonT != null} food={foodT != null}) — vanilla fallback.");
                    Reset();
                    return;
                }

                spoonR = spoonT.GetComponentInChildren<Renderer>(true);
                foodR  = foodT.GetComponentInChildren<Renderer>(true);
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

                // Save originals for restore-before-pool.
                Save(canT, out canParent0, out canPos0, out canRot0);
                Save(spoonT, out spoonParent0, out spoonPos0, out spoonRot0);
                Save(foodT, out foodParent0, out foodPos0, out foodRot0);

                canHolder = NewHolder("EatCanHolder", leftHandBone, canPosOffset, canRotOffset);
                spoonHolder = NewHolder("EatSpoonHolder", rightHandBone, spoonPosOffset, spoonRotOffset);
                foodHolder = NewHolder("EatFoodHolder", spoonHolder.transform, foodPosOffset, Vector3.zero);

                canT.SetParent(canHolder.transform, false);
                spoonT.SetParent(spoonHolder.transform, false);
                foodT.SetParent(foodHolder.transform, false);
                reparented = true;

                SetRenderer(spoonR, false); // appears on "open"
                SetRenderer(foodR, false);  // appears on "scoop"
                // (Draw/Open/Open2/SpoonTake fire from the STATE_OPEN segment itself.)

                if (driveHandsToTargets) SubscribePinAfterIk();

                Plugin.MyLog.LogInfo("[ManualEat] Props reparented to hands.");
            }
            catch (Exception ex)
            {
                Plugin.MyLog.LogError($"[ManualEat] OnSpawnPost error: {ex}");
                End();
            }
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
            ZeroLocal(canT);
            ZeroLocal(spoonT);
            ZeroLocal(foodT);

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
                // Props back to their original rig parents, then drop the holders.
                Restore(canT, canParent0, canPos0, canRot0);
                Restore(spoonT, spoonParent0, spoonPos0, spoonRot0);
                Restore(foodT, foodParent0, foodPos0, foodRot0);
                SetRenderer(spoonR, true);
                SetRenderer(foodR, true);
                if (canHolder != null) UnityEngine.Object.Destroy(canHolder);
                if (spoonHolder != null) UnityEngine.Object.Destroy(spoonHolder);
                if (foodHolder != null) UnityEngine.Object.Destroy(foodHolder);
            }
            catch (Exception ex) { Plugin.MyLog.LogError($"[ManualEat] RestoreProps error: {ex}"); }
            canHolder = spoonHolder = foodHolder = null;
            reparented = false;
        }

        //--- Gesture state machine --------------------------------------------------
        private static void StepGesture()
        {
            switch (phase)
            {
                case Phase.Closed:
                    if (!playingOpen)
                    {
                        // Right hand to the can + trigger -> roll the lid open by
                        // playing STATE_OPEN forward (real animation: lid rolls, the
                        // hand grabs the spoon, and the eat's own sounds fire).
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
                        // Hold once the lid is open + spoon grabbed.
                        var st = controller._player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                        bool past = st.fullPathHash != STATE_OPEN_HASH || st.normalizedTime >= openHoldTime;
                        if (past)
                        {
                            playingOpen = false;
                            controller.FirearmsAnimator?.SetAnimationSpeed(0f);
                            SetRenderer(spoonR, true);
                            phase = Phase.EmptySpoon;
                            Plugin.MyLog.LogInfo("[ManualEat] Opened — spoon in hand.");
                        }
                    }
                    break;

                case Phase.EmptySpoon:
                    if (RightHandNear(LeftHand(), scoopDistance))
                    {
                        SetRenderer(foodR, true);
                        phase = Phase.FullSpoon;
                        playUntil = Time.time + bitePlayTime; // play the scoop motion a bit
                        Pulse();
                        PlaySound(def.scoopSound);
                        Plugin.MyLog.LogInfo($"[ManualEat] Scooped (bite {biteCount + 1}/{def.bites}).");
                    }
                    break;

                case Phase.FullSpoon:
                    if (HandAtMouth())
                    {
                        SetRenderer(foodR, false);
                        biteCount++;
                        playUntil = Time.time + bitePlayTime; // play the eat motion a bit
                        Pulse();
                        PlaySound(def.eatSound);
                        Plugin.MyLog.LogInfo($"[ManualEat] Ate bite {biteCount}/{def.bites}.");

                        if (biteCount >= def.bites)
                            FinishSequence();
                        else
                            phase = Phase.EmptySpoon;
                    }
                    break;

                case Phase.Done:
                    break;
            }
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

        private static bool HandAtMouth()
        {
            Transform head = GetHead();
            Transform right = RightHand();
            if (head == null || right == null) return false;
            Vector3 delta = right.position - head.position;
            if (delta.magnitude > eatDistance) return false;
            return Vector3.Dot(delta.normalized, head.forward) > mouthForwardDot;
        }

        private static Transform GetHead()
        {
            if (VRGlobals.VRCam != null) return VRGlobals.VRCam.transform;
            return Camera.main != null ? Camera.main.transform : null;
        }

        private static bool TriggerEdge()
        {
            SteamVR_Action_Single trig = VRSettings.GetLeftHandedMode()
                ? SteamVR_Actions._default.LeftTrigger
                : SteamVR_Actions._default.RightTrigger;
            SteamVR_Input_Sources src = VRSettings.GetLeftHandedMode()
                ? SteamVR_Input_Sources.LeftHand
                : SteamVR_Input_Sources.RightHand;
            float axis = trig.GetAxis(src);
            bool crossed = axis > 0.5f && prevTriggerAxis <= 0.5f;
            prevTriggerAxis = axis;
            return crossed;
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
            if (playingOpen) return true;       // let the lid-roll segment's own sounds play
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

        private static void Reset()
        {
            active = false;
            manualDone = false;
            controller = null;
            def = null;
            phase = Phase.Closed;
            biteCount = 0;
            canT = spoonT = foodT = null;
            medsBody = null;
            spoonR = foodR = null;
            soundPlayer = null;
            canHolder = spoonHolder = foodHolder = null;
            canParent0 = spoonParent0 = foodParent0 = null;
            reparented = false;
            effectFired = false;
            pendingOp = null;
            prevTriggerAxis = 0f;
            playingOpen = false;
            playUntil = 0f;
        }
    }
}
