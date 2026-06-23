using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using SptVrFikaSync;
using TarkovVR.ModSupport;
using TarkovVR.ModSupport.FIKA;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.VRManager;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.Interactions
{
    // Physical grab-and-drag for dead bodies (ragdolls).
    //
    // A dead body is an EFT `Corpse` whose ragdoll is a set of `RigidbodySpawner` bone
    // rigidbodies. Once a corpse settles, EFT freezes every bone KINEMATIC (a static pose), and
    // after it also leaves the camera view EFT tears the rigidbodies down (corpses are built
    // keepRigidbody:false). We reactivate a settled/torn-down ragdoll the same way the game does
    // on a hit — `corpse.Ragdoll.Start()` — then drive the grabbed bone.
    //
    // Unlike loose loot, there's NO white dot / pointer: you grab a body ONLY by physically
    // putting your (off) hand on a body part and pressing grip. The bone you're touching becomes
    // a kinematic, hand-driven anchor; the rest of the ragdoll follows through its joints, so you
    // can drag the whole body around. Releasing grip lets physics resume (with a throw if you
    // flick).
    //
    // Tap-vs-hold: a quick grip TAP on a body still opens the corpse loot grid (the old
    // grip-on-corpse behavior), so looting isn't lost — HOLD to drag, TAP to loot.
    //
    // FIKA downed/revive teammates are draggable too (so you can pull a downed teammate to safety
    // before reviving). They aren't Corpses — FIKA makes a downed teammate a still-live ObservedPlayer
    // wearing a ragdoll on the Deadbody layer with a (Fika-internal) ReviveInteractable component that
    // holds the RagdollClass. Detection (TryFindBodyBone) handles either owner; the same bone-pin drive
    // / release / settle machinery works for both since both are the same RagdollClass + bone rigidbodies.
    // (Co-op sync is currently corpse-only, so a downed teammate's drag is local to the dragger — see
    // enableDownedPlayerDrag.)
    //
    // All knobs are live-tunable statics so the feel can be A/B'd in the headset.
    internal partial class HandsInteractionController
    {
        //--- Tunables ----------------------------------------------------------------

        // How close the palm must be to a body part's collider to grab it (m). ~0.18 matches the
        // loot/eat reach radii.
        public static float bodyGrabRadius = 0.18f;

        // Release throw: the bone keeps the controller's velocity (scaled) when you let go, so a
        // flick tosses the body. Clamped so it can't launch across the map.
        public static float bodyThrowVelocityScale = 1.0f;
        public static float bodyMaxThrowSpeed = 8f;

        public static float bodyTapMaxTime = 0.25f;
        public static float bodyTapMaxMove = 0.06f;

        public static bool debugBodyGrab = false;

        // Diagnostic: on releasing a DOWNED teammate, log for ~25 frames what moves — the bone's WORLD
        // position vs its LOCAL position (relative to its parent) vs the player root's position. If world
        // moves while local stays constant, the PARENT/root is being moved (the bones just ride it); if
        // local moves, the bone itself is being driven (physics or a direct write). Pinpoints the jump.
        public static bool debugDownedRelease = false;

        // Collision: while dragging, let the ragdoll bones escape geometry faster so the body snags/catches on terrain less,
        // and use continuous detection so a fast drag doesn't tunnel/hook on edges. Restored to
        // bodyDragRestDepenetrationVelocity on release (the low value tames ragdoll explosions
        // when the body is later shot). Too high can make a deeply-clipped body pop — keep it moderate.
        public static float bodyDragDepenetrationVelocity = 5f;
        public static float bodyDragRestDepenetrationVelocity = 1f;

        // Weighted drag (req: "more of a drag than a pickup"). false = the original 1:1 snap, so
        // the body goes exactly where your hand goes (easy to lift/carry). true = the anchor
        // chases your hand at a CAPPED speed (feels heavy/laggy) and RESISTS being lifted, so the
        // body drags along the ground instead of being hoisted. It's a shaped kinematic drive,
        // not true mass — but it reads as a heavy drag. Tune live in the headset.
        public static float bodyDragMaxSpeed = 1.5f; // m/s the anchor can chase the hand (lower = heavier)
        public static float bodyLiftFactor = 0.15f;  // upward-follow scale (0 = can't lift at all, 1 = free)
        // Weighted-mode rotation: the body rotates toward your hand at up to this many deg/s, so you
        // can twist the held part by rotating your wrist (capped for the heavy feel; raise toward
        // ~1000 for near-instant, lower for heavier). Gated by bodyDriveRotation; the non-weighted
        // path follows hand rotation rigidly (1:1).
        public static float bodyDragMaxAngularSpeed = 240f;

        // Movement slowdown while dragging a body — "the weight bears you down." A flat multiplier:
        // every corpse weighs the same (the heavy feel is faked by the weighted-chase drive above, and
        // EFT ragdoll masses are identical across bodies), so there's nothing to scale per-body.
        // bodyDragSpeedMultiplier = locomotion speed while dragging (0.5 = half speed). Sprint already
        // drops the body, so this only shapes walk/run. Tune live in the headset.
        public static bool bodyDragSlowsMovement = true;
        public static float bodyDragSpeedMultiplier = 0.001f;

        // Live output: current locomotion speed multiplier (1 = not dragging / no slowdown). Set on
        // grab, reset to 1 on release. MovementPatches.GetBodyRotation multiplies SetCharacterMovementSpeed by it.
        public static float bodyDragMoveSpeedMultiplier = 1f;

        // Release settle (your model): when you let go, the body STAYS in active physics — kept
        // awake every step so it can't freeze mid-air — until it lands AND has been at rest for
        // bodyFreezeDelay seconds, THEN it's frozen kinematic (stops simulating). This replaces
        // EFT's own unpredictable ~15s settle freeze (which caused both "freezes when I let go" and
        // "physics stays active forever"). bodyLandedSpeed = max bone speed (m/s) counted as "at
        // rest"; bodyFreezeMaxTime = hard fallback so a body that never fully rests (jitter on
        // uneven ground) still freezes. false = old behavior (reactivate once on release).
        public static float bodyLandedSpeed = 0.3f;
        public static float bodyFreezeDelay = 3f;
        public static float bodyFreezeMaxTime = 20f;

        // FIKA co-op: broadcast the dragged body's ragdoll pose so other players see it move
        // (like the loose-loot sync). bodySyncInterval ~0.05 = 20/s. No-op solo / without FIKA.
        public static float bodySyncInterval = 0.05f;

        // The dead player's NetId (GameWorld.ObservedPlayersCorpses key) of the corpse WE are
        // dragging, or -1. Read by FIKASupport to tag our outgoing packets and to ignore echoes
        // for a body we're already driving locally.
        public static int localDraggedCorpseNetId = -1;

        // The DOWNED teammate's NetId (CoopHandler.Players key) we're dragging, or -1. Separate from
        // the corpse path because a downed teammate is a live player, not in ObservedPlayersCorpses —
        // we sync it via FikaVrSync.SendDownedDrag (which relocates them + drives observer ragdolls).
        public static int localDraggedDownedNetId = -1;

        // Time.time of our most recent body grab — read by FIKASupport's steal arbitration so the
        // newest grabber wins a contested corpse (whoever has dragged it longer yields). Same idea
        // as heldItemGrabTime for loose loot.
        public static float bodyGrabTime;

        // Gates for the ragdoll's settle predicate (RagdollClass.Func_0 = (allAsleep, timePassed) ->
        // shouldFreeze). EFT's settle coroutine freezes a corpse (-> kinematic, the "static pose") the
        // first time this returns true. While we hold a body we point Func_0 at NeverFreeze so the game
        // can't freeze it out from under us (this is the clean fix for the whole "freezes mid-drag /
        // freezes on release / stays active forever" family of bugs — no per-frame fighting needed).
        // When WE freeze a settled body we point it at AlwaysFreeze so the coroutine exits cleanly
        // instead of looping forever.
        private static readonly System.Func<bool, float, bool> RagdollNeverFreeze = (a, t) => false;
        private static readonly System.Func<bool, float, bool> RagdollAlwaysFreeze = (a, t) => true;

        //--- Runtime state -----------------------------------------------------------
        private bool isDraggingBody;
        private Rigidbody grabbedBodyRb;
        private Corpse grabbedCorpse;
        private RagdollClass grabbedRagdoll;
        // The FIKA downed/revive teammate we're dragging (their ReviveInteractable component), if this
        // is a downed player rather than a vanilla corpse. grabbedCorpse is null in that case; the
        // ragdoll comes from this component (its _ragdoll). Watched so we release the body if they get
        // revived mid-drag (FIKA destroys the component on revive -> Unity == null).
        // Typed as MonoBehaviour (NOT ReviveInteractable) on purpose: ReviveInteractable is a Fika.Core
        // type, and a FIELD of that type forces Fika.Core to load the moment this (always-loaded)
        // controller's type loads — which crashes the entire mod when FIKA isn't installed
        // (BadImageFormatException: the field type can't resolve). So no Fika.Core type may appear in a
        // field/signature of this class. All Fika-typed access lives behind the FikaSync bridge
        // (FikaVrSync.FindReviveInteractable, gated on InstalledMods.FIKAInstalled); here we only use
        // MonoBehaviour members (.gameObject / .transform / Unity-null), so the base type is enough.
        private MonoBehaviour grabbedReviveInteractable;
        private bool grabbedIsDownedPlayer;
        private Transform grabBodyHand;
        private Vector3 grabBodyPosOffset;
        private Quaternion grabBodyRotOffset;
        private float lastBodySyncTime;

        // Armed on grip-down over a body (even if no grabbable bone — the bound collider still
        // identifies the corpse for tap-to-loot). Cleared on grip-up.
        private bool bodyInteractionArmed;
        private Corpse bodyInteractionCorpse;
        private float bodyInteractionStartTime;
        private Vector3 bodyInteractionStartHandPos;

        private readonly Collider[] bodyOverlap = new Collider[16];

        // Bodies we've let go of that are still falling/settling — kept in active physics until
        // landed + timer, then frozen (see UpdateReleasedBodies). One entry per dragged-then-
        // released ragdoll.
        private sealed class ReleasedBody
        {
            public RagdollClass ragdoll;
            public float restTimer;   // accumulated time below bodyLandedSpeed (resets if disturbed)
            public float releaseTime; // for the bodyFreezeMaxTime hard fallback
        }
        private readonly List<ReleasedBody> releasedBodies = new List<ReleasedBody>();

        private int DeadBodyMask => 1 << DEAD_BODY_LAYER;

        //--- Per-frame entry (called from Update, before the loot pointer) ------------
        private void UpdateBodyGrab()
        {
            Transform hand = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.LeftHand?.transform : null;
            if (hand == null)
            {
                if (isDraggingBody) ReleaseBody();
                bodyInteractionArmed = false;
                return;
            }

            // Grip released: end the drag and/or fire a tap-to-loot.
            if (bodyInteractionArmed && secondaryHandGrip.stateUp)
            {
                bool wasTap = (Time.time - bodyInteractionStartTime) <= bodyTapMaxTime
                              && (hand.position - bodyInteractionStartHandPos).sqrMagnitude <= bodyTapMaxMove * bodyTapMaxMove;
                Corpse tapCorpse = bodyInteractionCorpse;
                if (isDraggingBody) ReleaseBody();
                bodyInteractionArmed = false;
                bodyInteractionCorpse = null;
                if (wasTap)
                    OpenCorpseLoot(tapCorpse);
                return;
            }

            // Lost the grip without a clean stateUp (state simply went false) — release safely.
            if (isDraggingBody && !secondaryHandGrip.state)
            {
                ReleaseBody();
                bodyInteractionArmed = false;
                return;
            }

            if (isDraggingBody)
                return; // FixedUpdate drives the drag

            // New grab on grip-down with a free off-hand.
            if (!secondaryHandGrip.stateDown)
                return;
            if (heldItem != null || isSummoningLoot || VRGlobals.usingItem
                || EatingInteractionController.ManualActive || NearForegrip())
                return;

            Vector3 palm = PalmOrigin();
            if (TryFindBodyBone(palm, out RigidbodySpawner bone, out Corpse corpse, out RagdollClass ragdoll, out MonoBehaviour reviveInteractable))
            {
                bodyInteractionArmed = true;
                bodyInteractionCorpse = corpse;
                bodyInteractionStartTime = Time.time;
                bodyInteractionStartHandPos = hand.position;
                if (bone != null && ragdoll != null)
                    BeginBodyGrab(bone, corpse, ragdoll, reviveInteractable, hand);
            }
        }

        //--- Detection ---------------------------------------------------------------
        // Finds the nearest grabbable ragdoll bone to the palm. The owning body can be either a
        // vanilla dead Corpse OR a FIKA downed/revive teammate (a live ObservedPlayer wearing a
        // ragdoll + ReviveInteractable on the Deadbody layer). Reports the owner (corpse, or the
        // revive component + its RagdollClass) even when the hand is on a non-bone collider (the
        // body's bound collider), so tap-to-loot/arming still work. bone may be null (owner found,
        // no bone); ragdoll is the RagdollClass to drive when bone is non-null.
        private bool TryFindBodyBone(Vector3 palm, out RigidbodySpawner bone, out Corpse corpse,
            out RagdollClass ragdoll, out MonoBehaviour reviveInteractable)
        {
            bone = null;
            corpse = null;
            ragdoll = null;
            reviveInteractable = null;

            int count = Physics.OverlapSphereNonAlloc(palm, bodyGrabRadius, bodyOverlap, DeadBodyMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Collider col = bodyOverlap[i];
                if (col == null) continue;

                // Who owns this Deadbody collider? A vanilla Corpse, or a FIKA downed teammate. The
                // downed/revive owner is a Fika.Core type, so it's resolved through the FikaSync bridge
                // (FindReviveInteractable hands it back as a MonoBehaviour + its EFT RagdollClass) — gated
                // on FIKA being present so it's a clean no-op solo AND the main assembly never has to name
                // a Fika.Core type (which would break loading without FIKA — see the grabbedReviveInteractable
                // note above).
                Corpse c = col.GetComponentInParent<Corpse>();
                MonoBehaviour revive = null;
                RagdollClass reviveRagdoll = null;
                if (c == null && InstalledMods.FIKAInstalled)
                    revive = FikaFindRevive(col, out reviveRagdoll);
                if (c == null && revive == null)
                    continue; // some other Deadbody-layer collider — not a grabbable body

                // Remember any owner (for tap-to-loot / arming) even if this collider has no bone.
                if (corpse == null && reviveInteractable == null && bone == null)
                {
                    corpse = c;
                    reviveInteractable = revive;
                }

                RigidbodySpawner rs = col.GetComponent<RigidbodySpawner>();
                if (rs == null) continue; // not a ragdoll bone (e.g. the loot bound collider)

                float d = (col.ClosestPoint(palm) - palm).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    bone = rs;
                    corpse = c;            // the body that owns the chosen bone wins
                    reviveInteractable = revive;
                    ragdoll = c != null ? c.Ragdoll : reviveRagdoll;
                }
            }
            return corpse != null || reviveInteractable != null;
        }

        //--- Grab / drive / release --------------------------------------------------
        private void BeginBodyGrab(RigidbodySpawner bone, Corpse corpse, RagdollClass ragdoll, MonoBehaviour reviveInteractable, Transform hand)
        {
            grabbedCorpse = corpse;
            grabbedRagdoll = ragdoll;
            grabbedReviveInteractable = reviveInteractable;
            grabbedIsDownedPlayer = corpse == null && reviveInteractable != null;
            grabbedBodyRb = bone.Rigidbody;

            // Reactivate a settled (frozen-kinematic) or torn-down ragdoll the same way the game
            // does when a corpse is shot (EFT's GoreController calls Ragdoll.Start()). Start()
            // (re)creates the bone rigidbodies + joints and makes them dynamic again.
            if (grabbedRagdoll != null && (grabbedBodyRb == null || grabbedBodyRb.isKinematic))
            {
                try { grabbedRagdoll.Start(); } catch { /* ragdoll may be mid-teardown */ }
                grabbedBodyRb = bone.Rigidbody; // Start() may have (re)created it
            }

            if (grabbedBodyRb == null)
            {
                if (debugBodyGrab)
                    Plugin.MyLog.LogWarning("[BodyGrab] no rigidbody on the grabbed bone — cannot grab.");
                ResetBodyGrabState();
                return;
            }

            // Keep this body's ragdoll from ever being torn down. EFT constructs corpses with
            // keepRigidbody=false, so once a corpse has settled AND left the camera view its
            // settle coroutine destroys the joints+rigidbodies (one per frame) — which mid-drag
            // killed the drag and made the re-grab fight the in-progress teardown. Setting
            // keepRigidbody (Ragdoll.Bool_0) = true skips that teardown branch entirely, so a body
            // you've grabbed stays draggable regardless of view/time. Left true on release (a
            // dragged body keeping its rigidbodies is desirable + costs ~nothing once it sleeps).
            if (grabbedRagdoll != null)
                grabbedRagdoll.Bool_0 = true;

            // Stop EFT's settle coroutine from freezing this body while we hold it. The coroutine only
            // freezes when Func_0(...) returns true; NeverFreeze keeps the body a live ragdoll for as
            // long as we hold it, so we no longer have to fight a freeze every frame. (Restored to a
            // terminating predicate when the release-settle manager finally freezes it.)
            if (grabbedRagdoll != null)
                grabbedRagdoll.Func_0 = RagdollNeverFreeze;

            // Weigh the player down: slow locomotion while a body is held.
            bodyDragMoveSpeedMultiplier = bodyDragSlowsMovement ? bodyDragSpeedMultiplier : 1f;

            // If we're re-grabbing a body that was still settling from a previous release, stop
            // managing it (we drive it again now).
            RemoveReleasedBody(grabbedRagdoll);

            ThawRagdoll(); // make sure the rest of the body is dynamic + awake so it follows

            // Pin the grabbed bone to the hand: kinematic = an infinite-mass anchor the joints
            // pull the rest of the body toward. EFT registers every ragdoll bone with its custom
            // physics-support system (GClass745 -> SyncTransformsClass), which sets the body's
            // velocity each step — doing that on a KINEMATIC body spams "Setting velocity of a
            // kinematic body is not supported". The game's own freeze (RagdollClass.method_1)
            // always UNsupports first, so mirror that before going kinematic.
            EFTPhysicsClass.GClass745.UnsupportRigidbody(grabbedBodyRb);
            grabbedBodyRb.isKinematic = true;
            grabbedBodyRb.useGravity = false;
            grabbedBodyRb.detectCollisions = true;
            grabbedBodyRb.maxDepenetrationVelocity = bodyDragDepenetrationVelocity;
            // ContinuousSpeculative is the one continuous mode valid on a kinematic body — it
            // lets the dragged anchor sweep against geometry instead of catching/tunneling.
            grabbedBodyRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            grabBodyHand = hand;
            grabBodyPosOffset = hand.InverseTransformPoint(grabbedBodyRb.position);
            grabBodyRotOffset = Quaternion.Inverse(hand.rotation) * grabbedBodyRb.rotation;

            isDraggingBody = true;

            // FIKA: resolve the network id so the drag can be broadcast (no-op solo). Two paths:
            //   - vanilla corpse: keyed by GameWorld.ObservedPlayersCorpses (the existing body-drag sync).
            //   - downed teammate: keyed by the live player's NetId (CoopHandler) — relocates them so a
            //     revive lands at the dragged-to spot + drives observer ragdolls (FikaVrSync.SendDownedDrag).
            localDraggedCorpseNetId = -1;
            localDraggedDownedNetId = -1;
            lastBodySyncTime = 0f;
            bodyGrabTime = Time.time; // steal arbitration: newest grab wins (see FIKASupport)
            if (InstalledMods.FIKAInstalled)
            {
                if (grabbedIsDownedPlayer && reviveInteractable != null)
                {
                    localDraggedDownedNetId = FikaResolveDownedNetId(reviveInteractable.gameObject);
                }
                else if (corpse != null && TryGetCorpseNetId(corpse, out int corpseNetId))
                {
                    localDraggedCorpseNetId = corpseNetId;
                    // We drive this corpse now, not the remote sync — drop any remote-drag bookkeeping
                    // so a later hand-off back to us re-enters the remote pin cleanly.
                    FikaClearRemoteDraggedCorpse(corpseNetId);
                }
            }

            SteamVR_Actions._default.Haptic.Execute(0, 0.08f, 1, 0.5f, secondaryInputSource);
            if (debugBodyGrab)
                Plugin.MyLog.LogInfo($"[BodyGrab] grabbed {bone.name} on {(corpse != null ? corpse.name : "?")} netId={localDraggedCorpseNetId}");
        }

        // Driven in FixedUpdate so the kinematic anchor moves with proper joint solving.
        private void FixedUpdate()
        {
            // Settle bodies we've let go of (keep them active until landed + timer, then freeze).
            // Runs regardless of whether we're currently dragging.
            UpdateReleasedBodies();

            if (!isDraggingBody)
                return;
            // Release if the body went invalid, or (downed teammate) if they got revived mid-drag:
            // FIKA destroys the ReviveInteractable on revive and re-enables the player's animators,
            // which would fight our kinematic anchor — let go cleanly instead.
            if (grabbedBodyRb == null || grabBodyHand == null || !VRGlobals.inGame
                || (grabbedIsDownedPlayer && grabbedReviveInteractable == null))
            {
                ReleaseBody();
                return;
            }

            // Keep the rest of the ragdoll dynamic + awake every step so it follows the drag
            // (bones auto-sleep or get re-frozen by the corpse's settle timer otherwise).
            ThawRagdoll();

            // Keep the GRABBED bone awake too. It's kinematic and driven by MovePosition; if PhysX
            // sleeps it (e.g. you hold your hand still a moment), MovePosition stops taking effect
            // and that one bone "freezes" until re-grabbed. ThawRagdoll skips the grabbed bone, so
            // wake it here. (This was the "the bone I was grabbing freezes" bug.) Unconditional —
            // WakeUp is a cheap no-op when already awake, and IsSleeping() is unreliable on a
            // kinematic body.
            grabbedBodyRb.WakeUp();

            Vector3 target = grabBodyHand.TransformPoint(grabBodyPosOffset);

            // Heavy drag: chase the hand at a capped speed and resist upward lift, so the
            // body drags along the ground rather than being hoisted.
            Vector3 cur = grabbedBodyRb.position;
            Vector3 delta = target - cur;
            if (delta.y > 0f)
                delta.y *= bodyLiftFactor;
            float maxStep = bodyDragMaxSpeed * Time.fixedDeltaTime;
            grabbedBodyRb.MovePosition(cur + Vector3.ClampMagnitude(delta, maxStep));
            Quaternion targetRot = grabBodyHand.rotation * grabBodyRotOffset;
            grabbedBodyRb.MoveRotation(Quaternion.RotateTowards(
                grabbedBodyRb.rotation, targetRot, bodyDragMaxAngularSpeed * Time.fixedDeltaTime));

            // FIKA: broadcast the FULL ragdoll pose (every bone) so other players see the exact same
            // ragdoll (throttled). Syncing all bones — rather than one pinned bone + local physics —
            // avoids the dangling bones diverging/stretching on the observer's screen. A grab is either a
            // corpse OR a downed teammate, so only one of these fires.
            if (InstalledMods.FIKAInstalled && Time.time - lastBodySyncTime >= bodySyncInterval)
            {
                if (localDraggedCorpseNetId >= 0)
                {
                    lastBodySyncTime = Time.time;
                    FikaSendDraggedBody(localDraggedCorpseNetId, grabbedCorpse);
                }
                else if (localDraggedDownedNetId >= 0)
                {
                    lastBodySyncTime = Time.time;
                    FikaSendDownedDrag(localDraggedDownedNetId, grabbedRagdoll);
                }
            }
        }

        // Keep every bone of the grabbed ragdoll dynamic AND awake (except the hand-driven one)
        // so the joints transmit the drag through the whole body, every physics step.
        //   - The corpse's settle timer can re-freeze bones KINEMATIC mid-drag -> un-kinematic.
        //   - Bones that go still for a moment AUTO-SLEEP; PhysX won't reliably wake a sleeping
        //     bone just because the kinematic bone it's jointed to is MovePosition'd, so it lags
        //     behind and the mesh stretches between your hand and the asleep bones. Force them
        //     awake each step (this was the "freeze + stretch after a few seconds" bug).
        private void ThawRagdoll()
        {
            RigidbodySpawner[] bones = grabbedRagdoll != null ? grabbedRagdoll.RigidbodySpawner_0 : null;
            if (bones == null)
                return;
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody rb = bones[i] != null ? bones[i].Rigidbody : null;
                if (rb == null || rb == grabbedBodyRb)
                    continue;
                // Always assert dynamic + gravity (not only when re-thawing a kinematic bone) so a
                // dragged body's limbs hang/droop under gravity instead of floating rigidly.
                if (rb.isKinematic)
                    rb.isKinematic = false;
                rb.useGravity = true;
                rb.detectCollisions = true;
                if (rb.IsSleeping())
                    rb.WakeUp();
                rb.maxDepenetrationVelocity = bodyDragDepenetrationVelocity;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
        }

        // Restore the bones' (raised) depenetration velocity to the settled value on release, so a
        // later interaction (e.g. shooting the corpse)
        private void RestoreRagdollCollision()
        {
            RigidbodySpawner[] bones = grabbedRagdoll != null ? grabbedRagdoll.RigidbodySpawner_0 : null;
            if (bones == null)
                return;
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody rb = bones[i] != null ? bones[i].Rigidbody : null;
                if (rb != null)
                    rb.maxDepenetrationVelocity = bodyDragRestDepenetrationVelocity;
            }
        }

        private void ReleaseBody()
        {
            if (grabbedBodyRb != null)
            {
                grabbedBodyRb.isKinematic = false;
                grabbedBodyRb.useGravity = true;
                grabbedBodyRb.detectCollisions = true;

                // Throw: carry the controller's release velocity (scaled + clamped) into the bone — a
                // corpse you can flick to toss. NOT for a downed teammate: you're PLACING them, and their
                // body is frozen kinematic in place below (so any velocity here would be ignored anyway).
                if (!grabbedIsDownedPlayer)
                {
                    Vector3 v = ControllerVelocity.GetSteamVRVelocity(secondaryInputSource) * bodyThrowVelocityScale;
                    if (v.sqrMagnitude > 0.0001f)
                        grabbedBodyRb.velocity = Vector3.ClampMagnitude(v, bodyMaxThrowSpeed);
                }

                // Re-register the bone with EFT's physics-support system (we unsupported it on
                // grab); now non-kinematic, it rejoins the ragdoll like its siblings. Mirrors
                // RagdollClass.Start()'s order (velocity, then SupportRigidbody at Low quality).
                EFTPhysicsClass.GClass745.SupportRigidbody(grabbedBodyRb, 0f);
                grabbedBodyRb.WakeUp();

                SteamVR_Actions._default.Haptic.Execute(0, 0.05f, 1, 0.3f, secondaryInputSource);
            }

            // Re-enable physics on the WHOLE ragdoll so the body falls/settles instead of freezing mid-air,
            // then hand it to the release-settle manager (drop -> land -> rest -> freeze). Same path for
            // corpses AND downed teammates — a downed teammate ragdolls/settles naturally. (We tried
            // freezing the downed ragdoll kinematic on release to stop the "teleports a short distance"
            // jump; it did NOT help — so the jump is NOT the ragdoll physics. A kinematic body still
            // follows its parent transform, so something outside the ragdoll is moving the body/root on
            // release — under investigation. Reverted to FIKA's natural behavior.)
            ReactivateRagdollBones();
            RestoreRagdollCollision();

            if (InstalledMods.FIKAInstalled && localDraggedCorpseNetId >= 0)
                FikaSendDraggedBodyReleased(localDraggedCorpseNetId);
            else if (InstalledMods.FIKAInstalled && localDraggedDownedNetId >= 0)
                FikaSendDownedDragReleased(localDraggedDownedNetId, grabbedRagdoll);

            if (grabbedRagdoll != null)
            {
                AddReleasedBody(grabbedRagdoll);
            }
            else if (grabbedRagdoll != null)
            {
                grabbedRagdoll.Func_0 = (allAsleep, t) => allAsleep;
            }

            if (grabbedIsDownedPlayer && debugDownedRelease)
                StartCoroutine(LogDownedRelease(grabbedRagdoll,
                    grabbedReviveInteractable != null ? grabbedReviveInteractable.transform : null));

            ResetBodyGrabState();
        }

        // Diagnostic coroutine: dump bone world/local + root each frame after a downed release so we can
        // see whether the bones move themselves or just ride a moving parent/root. Gated by debugDownedRelease.
        private System.Collections.IEnumerator LogDownedRelease(RagdollClass ragdoll, Transform playerRoot)
        {
            RigidbodySpawner[] bones = ragdoll != null ? ragdoll.RigidbodySpawner_0 : null;
            Transform bone = (bones != null && bones.Length > 0 && bones[0] != null) ? bones[0].transform : null;
            Rigidbody rb = (bones != null && bones.Length > 0 && bones[0] != null) ? bones[0].Rigidbody : null;
            if (bone == null)
            {
                Plugin.MyLog.LogWarning("[DownedRelease] no bone to track");
                yield break;
            }
            for (int f = 0; f < 25; f++)
            {
                bool kin = rb != null && rb.isKinematic;
                Vector3 w = bone.position;
                Vector3 l = bone.localPosition;
                Vector3 root = playerRoot != null ? playerRoot.position : Vector3.zero;
                Plugin.MyLog.LogWarning($"[DownedRelease] f{f} kin={kin} boneWorld={w.ToString("F3")} boneLocal={l.ToString("F3")} root={root.ToString("F3")}");
                yield return null;
            }
        }

        //--- Release-settle manager --------------------------------------------------
        private void AddReleasedBody(RagdollClass ragdoll)
        {
            RemoveReleasedBody(ragdoll);
            releasedBodies.Add(new ReleasedBody { ragdoll = ragdoll, restTimer = 0f, releaseTime = Time.time });
        }

        private void RemoveReleasedBody(RagdollClass ragdoll)
        {
            for (int i = releasedBodies.Count - 1; i >= 0; i--)
                if (ReferenceEquals(releasedBodies[i].ragdoll, ragdoll))
                    releasedBodies.RemoveAt(i);
        }

        // Per physics step: for each let-go body, keep every bone awake + dynamic + gravity (so it
        // FALLS instead of freezing mid-air), and watch its speed. Once all bones have been at rest
        // (< bodyLandedSpeed) continuously for bodyFreezeDelay seconds — i.e. it landed and stopped
        // — freeze it kinematic. bodyFreezeMaxTime is a hard fallback for a body that never fully
        // stills.
        private void UpdateReleasedBodies()
        {
            if (releasedBodies.Count == 0)
                return;
            float dt = Time.fixedDeltaTime;
            float landedSqr = bodyLandedSpeed * bodyLandedSpeed;

            for (int i = releasedBodies.Count - 1; i >= 0; i--)
            {
                ReleasedBody body = releasedBodies[i];
                RigidbodySpawner[] bones = body.ragdoll != null ? body.ragdoll.RigidbodySpawner_0 : null;
                if (bones == null)
                {
                    releasedBodies.RemoveAt(i);
                    continue;
                }

                float maxSpeedSqr = 0f;
                bool anyValid = false;
                for (int b = 0; b < bones.Length; b++)
                {
                    Rigidbody rb = bones[b] != null ? bones[b].Rigidbody : null;
                    if (rb == null)
                        continue;
                    anyValid = true;
                    // Keep it active: a falling body must not sleep/freeze mid-air, and we want it
                    // genuinely simulating (reacting to bumps) during the post-landing timer.
                    if (rb.isKinematic)
                        rb.isKinematic = false;
                    rb.useGravity = true;
                    rb.detectCollisions = true;
                    if (rb.IsSleeping())
                        rb.WakeUp();
                    float s = rb.velocity.sqrMagnitude;
                    if (s > maxSpeedSqr)
                        maxSpeedSqr = s;
                }
                if (!anyValid)
                {
                    releasedBodies.RemoveAt(i);
                    continue;
                }

                if (maxSpeedSqr < landedSqr)
                    body.restTimer += dt;
                else
                    body.restTimer = 0f;

                if (body.restTimer >= bodyFreezeDelay || Time.time - body.releaseTime >= bodyFreezeMaxTime)
                {
                    FreezeReleasedBody(body.ragdoll, bones);
                    releasedBodies.RemoveAt(i);
                }
            }
        }

        // Freeze a settled body kinematic in place (the efficient "done" state), mirroring EFT's own
        // RagdollClass.method_1 (unsupport + Discrete + kinematic). Restores the tame depenetration
        // velocity too so a later shot doesn't pop, and restores Func_0 to a terminating predicate so
        // the settle coroutine we kept alive (NeverFreeze) exits cleanly instead of spinning forever.
        private void FreezeReleasedBody(RagdollClass ragdoll, RigidbodySpawner[] bones)
        {
            for (int b = 0; b < bones.Length; b++)
            {
                Rigidbody rb = bones[b] != null ? bones[b].Rigidbody : null;
                if (rb == null)
                    continue;
                EFTPhysicsClass.GClass745.UnsupportRigidbody(rb);
                rb.maxDepenetrationVelocity = bodyDragRestDepenetrationVelocity;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                rb.isKinematic = true;
            }
            if (ragdoll != null)
                ragdoll.Func_0 = RagdollAlwaysFreeze;
        }

        // Force every bone of the dragged ragdoll back into active physics (dynamic + gravity +
        // awake) so the body resumes falling/settling on release. No SupportRigidbody here — some
        // bones may still be registered with EFT's support system locally (double-registering leaks
        // a List_0 entry), and being unsupported doesn't stop a dynamic body from falling.
        private void ReactivateRagdollBones()
        {
            RigidbodySpawner[] bones = grabbedRagdoll != null ? grabbedRagdoll.RigidbodySpawner_0 : null;
            if (bones == null)
                return;
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody rb = bones[i] != null ? bones[i].Rigidbody : null;
                if (rb == null)
                    continue;
                if (rb.isKinematic)
                    rb.isKinematic = false;
                rb.useGravity = true;
                rb.detectCollisions = true;
                rb.WakeUp();
            }
        }

        private void ResetBodyGrabState()
        {
            isDraggingBody = false;
            grabbedBodyRb = null;
            grabBodyHand = null;
            grabbedCorpse = null;
            grabbedRagdoll = null;
            grabbedReviveInteractable = null;
            grabbedIsDownedPlayer = false;
            localDraggedCorpseNetId = -1;
            localDraggedDownedNetId = -1;
            bodyDragMoveSpeedMultiplier = 1f; // restore full movement speed when we stop dragging
        }

        // FIKA co-op "steal": another player grabbed the body we were dragging more recently, so
        // hand it over — stop our local drive WITHOUT throwing it or broadcasting a release. Their
        // incoming packets now drive our copy (the FikaSync module pins the bone THEY hold and lets the
        // rest ragdoll locally). Invoked via FikaVrSync.onYieldBodyDrag from the module's BodyDragApply
        // when we lose the contest. (Leaves the ragdoll live; the remote pin takes over the bone.)
        public void RelinquishBodyDrag()
        {
            if (!isDraggingBody)
                return;
            RestoreRagdollCollision();
            SteamVR_Actions._default.Haptic.Execute(0, 0.1f, 1, 0.6f, secondaryInputSource);
            ResetBodyGrabState();
            bodyInteractionArmed = false;
            bodyInteractionCorpse = null;
        }

        // Reverse-lookup a corpse's network id (GameWorld.ObservedPlayersCorpses is keyed by the
        // dead player's NetId). EFT-only; only meaningful for networked (ObservedCorpse) bodies —
        // returns false for non-networked corpses (which simply won't sync).
        private static bool TryGetCorpseNetId(Corpse corpse, out int netId)
        {
            netId = -1;
            if (corpse == null)
                return false;
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || gameWorld.ObservedPlayersCorpses == null)
                return false;
            foreach (var kv in gameWorld.ObservedPlayersCorpses)
            {
                if (ReferenceEquals(kv.Value, corpse))
                {
                    netId = kv.Key;
                    return true;
                }
            }
            return false;
        }

        //--- Corpse loot grid (the old grip-on-corpse interaction) --------------------
        // Opens the corpse's loot grid. Used by the tap-to-loot path here and by
        // ProcessInteractiveObjects when body-grab is disabled, so looting is never lost.
        private void OpenCorpseLoot(Corpse corpse)
        {
            if (corpse == null)
                return;
            GetActionsClass.Class1748 corpseInteractionClass = new GetActionsClass.Class1748();
            corpseInteractionClass.compoundItem = (InventoryEquipment)corpse.Item;
            corpseInteractionClass.rootItem = (InventoryEquipment)corpse.Item;
            corpseInteractionClass.lootItemOwner = corpse.ItemOwner;
            corpseInteractionClass.controller = VRGlobals.player.InventoryController;
            corpseInteractionClass.owner = PlayerOwner;
            corpseInteractionClass.method_3();
        }

        //--- FIKA bridge wrappers (companion-DLL isolation) --------------------------
        // These are the ONLY members in this class that name a SptVrFikaSync (SPT-VR-FikaSync.dll)
        // type, and every one is called EXCLUSIVELY from inside an `if (InstalledMods.FIKAInstalled)`
        // block above. Why a separate method per call instead of inlining the FikaVrSync.* call at
        // the gated site: Mono binds a cross-assembly call when it JITs the method CONTAINING the
        // call — not when the call executes — so an inline FikaVrSync.* reference in an always-run
        // method (TryFindBodyBone on left-grip, FixedUpdate, ReleaseBody) throws
        // FileNotFoundException at THAT method's JIT the moment it runs without the companion DLL,
        // even though the call is runtime-gated and never executes. (That's exactly what broke
        // left-grip interactions when running solo / without FIKA.) Isolated into their own methods,
        // these are only JITted when actually CALLED — which only happens with FIKA present — so solo
        // the missing assembly is never bound. [MethodImpl(NoInlining)] stops the JIT from folding the
        // body back into the caller and reintroducing the inline reference. Same isolation pattern as
        // VRArmSync.Tick(); see memory note fika-softdep-no-typed-fields.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static MonoBehaviour FikaFindRevive(Collider col, out RagdollClass ragdoll)
            => FikaVrSync.FindReviveInteractable(col, out ragdoll);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int FikaResolveDownedNetId(GameObject bodyRoot)
            => FikaVrSync.ResolveDownedNetId(bodyRoot);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaClearRemoteDraggedCorpse(int corpseNetId)
            => FikaVrSync.ClearRemoteDraggedCorpse(corpseNetId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendDraggedBody(int corpseNetId, Corpse corpse)
            => FikaVrSync.SendDraggedBody(corpseNetId, corpse);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendDownedDrag(int downedNetId, RagdollClass ragdoll)
            => FikaVrSync.SendDownedDrag(downedNetId, ragdoll);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendDraggedBodyReleased(int corpseNetId)
            => FikaVrSync.SendDraggedBodyReleased(corpseNetId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendDownedDragReleased(int downedNetId, RagdollClass ragdoll)
            => FikaVrSync.SendDownedDragReleased(downedNetId, ragdoll);
    }
}
