using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using SptVrFikaSync;
using TarkovVR.ModSupport;
using TarkovVR.ModSupport.ORBIT;
using TarkovVR.Source.Controls;
using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Player.Interactions
{
    // Physical grab-and-drag for dead bodies (ragdolls). Put a free hand's palm on a body part and hold the grip to drag it (either hand,
    // one body at a time). That hand's trigger opens the corpse loot grid (HandsInteractionController.TriggerInteract); with
    // triggerInteract off, a quick grip tap opens it instead. There is no pointer or white dot for bodies.
    //
    // A dead body is an EFT `Corpse` whose ragdoll is a set of `RigidbodySpawner` bone rigidbodies. Once it settles, EFT
    // freezes every bone kinematic, and after it leaves the camera view tears the rigidbodies down (keepRigidbody:false).
    // `corpse.Ragdoll.Start()` reactivates it, as the game does on a hit. The touched bone becomes a kinematic, hand-driven
    // anchor; the rest of the ragdoll follows through its joints.
    //
    // FIKA downed teammates are draggable too: a live ObservedPlayer wearing a ragdoll on the Deadbody layer, with a Fika
    // ReviveInteractable component that holds the RagdollClass. Same drive / release / settle code for both.
    internal partial class HandsInteractionController
    {
        //--- Tunables ----------------------------------------------------------------

        // How close the palm must be to a body part's collider to grab it (m)
        public static float bodyGrabRadius = 0.18f;

        // Release throw: the bone keeps the controller's velocity (scaled, clamped to bodyMaxThrowSpeed m/s)
        public static float bodyThrowVelocityScale = 1.0f;
        public static float bodyMaxThrowSpeed = 8f;

        [Tunable("Loot & hands", TunableKind.Tune, "With triggerInteract off: longest grip on a body that still counts as a tap (opens its loot). Longer = a drag.", Unit = "s")]
        public static float bodyTapMaxTime = 0.25f;
        [Tunable("Loot & hands", TunableKind.Tune, "With triggerInteract off: how far the hand may move during a tap on a body.", Unit = "m")]
        public static float bodyTapMaxMove = 0.06f;

        public static bool debugBodyGrab = false;

        // maxDepenetrationVelocity of the ragdoll bones while dragged (higher = snags on terrain less; too high pops a
        // deeply clipped body) and at rest (low = no ragdoll explosion when the body is shot later)
        public static float bodyDragDepenetrationVelocity = 5f;
        public static float bodyDragRestDepenetrationVelocity = 1f;

        // Weighted drag: the anchor chases the hand at a capped speed and resists being lifted, so the body drags along
        // the ground. A shaped kinematic drive, not true mass.
        public static float bodyDragMaxSpeed = 1.5f; // m/s the anchor can chase the hand (lower = heavier)
        public static float bodyLiftFactor = 0.15f;  // upward-follow scale (0 = can't lift at all, 1 = free)
        // deg/s the held part can rotate toward the hand (lower = heavier)
        public static float bodyDragMaxAngularSpeed = 240f;

        // Locomotion speed multiplier while dragging a body. Flat: EFT ragdoll masses are identical across bodies.
        // Sprint already drops the body, so this only shapes walk/run.
        public static bool bodyDragSlowsMovement = true;
        public static float bodyDragSpeedMultiplier = 0.001f;

        // Live output: current locomotion speed multiplier (1 = not dragging). MovementPatches.GetBodyRotation multiplies
        // SetCharacterMovementSpeed by it.
        public static float bodyDragMoveSpeedMultiplier = 1f;

        // Release settle: a let-go body stays in active physics, kept awake, until every bone has been under
        // bodyLandedSpeed (m/s) for bodyFreezeDelay seconds; then it is frozen kinematic. bodyFreezeMaxTime (s) is the
        // fallback for a body that never fully rests. Replaces EFT's own ~15s settle freeze.
        public static float bodyLandedSpeed = 0.3f;
        public static float bodyFreezeDelay = 3f;
        public static float bodyFreezeMaxTime = 20f;

        // FIKA: seconds between broadcasts of the dragged body's ragdoll pose (0.05 = 20/s)
        public static float bodySyncInterval = 0.05f;

        // NetId (GameWorld.ObservedPlayersCorpses key) of the corpse we are dragging, or -1. FIKASupport tags our outgoing
        // packets with it and ignores echoes for a body we drive locally.
        public static int localDraggedCorpseNetId = -1;

        // NetId (CoopHandler.Players key) of the downed teammate we are dragging, or -1. A downed teammate is a live
        // player, not in ObservedPlayersCorpses: synced via FikaVrSync.SendDownedDrag.
        public static int localDraggedDownedNetId = -1;

        // Time.time of our latest body grab. FIKASupport's steal arbitration: the newest grabber wins a contested corpse.
        public static float bodyGrabTime;

        // The ragdoll's settle predicate (RagdollClass.Func_0 = (allAsleep, timePassed) -> shouldFreeze): EFT's settle
        // coroutine freezes the corpse kinematic the first time it returns true. NeverFreeze while we hold or settle a
        // body; AlwaysFreeze once we freeze it ourselves, so the coroutine exits.
        private static readonly System.Func<bool, float, bool> RagdollNeverFreeze = (a, t) => false;
        private static readonly System.Func<bool, float, bool> RagdollAlwaysFreeze = (a, t) => true;

        //--- Runtime state -----------------------------------------------------------
        private bool isDraggingBody;
        private Rigidbody grabbedBodyRb;
        private Corpse grabbedCorpse;
        private CorpseRagdoll grabbedRagdoll;
        // The dragged downed teammate's ReviveInteractable component (grabbedCorpse is null then). FIKA destroys it on
        // revive, which is how the drag notices and lets go.
        // Typed MonoBehaviour on purpose: a field or signature of a Fika.Core type makes this class fail to load without
        // FIKA (BadImageFormatException). Fika-typed access goes through the FikaSync bridge.
        private MonoBehaviour grabbedReviveInteractable;
        private bool grabbedIsDownedPlayer;
        private Transform grabBodyHand;
        private Vector3 grabBodyPosOffset;
        private Quaternion grabBodyRotOffset;
        private float lastBodySyncTime;

        // Armed on grip-down over a body, even with no grabbable bone (the bound collider still identifies the corpse for
        // tap-to-loot). Cleared on grip-up.
        private bool bodyInteractionArmed;
        private Corpse bodyInteractionCorpse;
        private float bodyInteractionStartTime;
        private Vector3 bodyInteractionStartHandPos;

        private readonly Collider[] bodyOverlap = new Collider[48];

        // Bodies we let go of that are still falling/settling: kept in active physics, then frozen (UpdateReleasedBodies)
        private sealed class ReleasedBody
        {
            public CorpseRagdoll ragdoll;
            public Corpse corpse;     // the corpse this ragdoll belongs to (null for a downed teammate), so we
                                      // can relocate its AI loot position once it settles (OrbitSupport)
            public float restTimer;   // accumulated time below bodyLandedSpeed (resets if disturbed)
            public float releaseTime; // for the bodyFreezeMaxTime hard fallback
        }
        private readonly List<ReleasedBody> releasedBodies = new List<ReleasedBody>();

        // EFT's settle coroutine (CorpseRagdoll.WorkingCycle) calls TryPutToSleep on each bone every frame. A bone whose kinetic
        // energy drops under EFTHardSettings.CorpseEnergyToSleep (0.05) is marked MustBeSleeping and Sleep()'d every frame after that.
        // PhysX zeroes a body's velocity when it sleeps, so a held or falling body would only sink about g*dt per frame.
        // While true, the bones of every body we hold or are letting settle skip that call (BodyDragPatches). HollywoodFX sets
        // CorpseEnergyToSleep to -1, which hides the problem while it's installed.
        [Tunable("Loot & hands", TunableKind.Switch, "A body you drag, or one you've let go of that's still settling, falls at full speed. Off: Tarkov puts its "
            + "resting bones to sleep every frame, and the body floats down slowly.")]
        public static bool keepHeldBodiesAwake = true;
        internal static readonly HashSet<PlayerRigidbodySleepHierarchy> bonesKeptAwake = new HashSet<PlayerRigidbodySleepHierarchy>();

        // Rebuilt whenever a body is grabbed, let go, frozen or handed over
        private void RefreshBonesKeptAwake()
        {
            bonesKeptAwake.Clear();
            if (isDraggingBody)
                AddBonesKeptAwake(grabbedRagdoll);
            foreach (ReleasedBody body in releasedBodies)
                AddBonesKeptAwake(body.ragdoll);
        }

        private static void AddBonesKeptAwake(CorpseRagdoll ragdoll)
        {
            List<PlayerRigidbodySleepHierarchy> bones = ragdoll != null ? ragdoll._rigidbodySleepHierarchy : null;
            if (bones == null)
                return;
            foreach (PlayerRigidbodySleepHierarchy bone in bones)
            {
                if (bone != null)
                    bonesKeptAwake.Add(bone);
            }
        }

        // No references to a raid's bodies kept past it
        private void OnDestroy()
        {
            bonesKeptAwake.Clear();
        }

        private int DeadBodyMask => 1 << DEAD_BODY_LAYER;

        // Logs one line, with the reason, for each grip on a body that didn't take (a gate blocked it, or the press was too long or moved
        // too far for a tap)
        [Tunable("Loot & hands", TunableKind.Log, "A line per grip on a body that didn't take: what the hand was doing (holster zone, gun grip...), or (grip "
            + "mode) a press too long / moved for a tap. Also an [Interact] line per trigger on a body / container / door "
            + "that didn't take, with what that hand had.")]
        public static bool logBodyGrips = true;

        // The hand the body interaction belongs to (one at a time): false = the off hand, true = the dominant one
        private bool bodyGrabDominant;

        private VRBoolean BodyGrip(bool dominant) => dominant ? primaryHandGrip : secondaryHandGrip;
        private SteamVR_Input_Sources BodyHandSource(bool dominant) => dominant ? primaryInputSource : secondaryInputSource;

        private static Transform BodyHand(bool dominant)
        {
            var rig = VRGlobals.vrPlayer;
            GameObject hand = rig == null ? null : dominant ? rig.RightHand : rig.LeftHand;
            return hand != null ? hand.transform : null;
        }

        // Called from Update, before the loot pointer. Either free hand's palm on a body part plus the grip drags it, and that hand's
        // trigger loots it (with triggerInteract; otherwise a quick grip tap does).
        private void UpdateBodyGrab()
        {
            // A hand dragging the body can't loot it with its trigger, but the other hand can
            if (TryTriggerBodyLoot(false) || TryTriggerBodyLoot(true))
                return;
            if (bodyInteractionArmed)
            {
                TickArmedBodyInteraction();
                return;
            }
            if (!TryArmBodyInteraction(dominant: false))
                TryArmBodyInteraction(dominant: true);
        }

        private void TickArmedBodyInteraction()
        {
            Transform hand = BodyHand(bodyGrabDominant);
            VRBoolean grip = BodyGrip(bodyGrabDominant);
            if (hand == null || grip == null)
            {
                if (isDraggingBody) ReleaseBody();
                ResetBodyInteraction();
                return;
            }
            // Grip released: end the drag and/or fire a tap-to-loot
            if (grip.stateUp)
            {
                float heldFor = Time.time - bodyInteractionStartTime;
                float moved = (hand.position - bodyInteractionStartHandPos).magnitude;
                bool wasTap = !triggerInteract && heldFor <= bodyTapMaxTime && moved <= bodyTapMaxMove;
                Corpse tapCorpse = bodyInteractionCorpse;
                if (!wasTap && !triggerInteract && logBodyGrips && heldFor < 0.6f)
                    Plugin.MyLog.LogInfo($"[BodyGrab] {(bodyGrabDominant ? "dominant" : "off")} hand's grip on a body wasn't a tap: held {heldFor:F2} s (a tap: {bodyTapMaxTime:F2}), moved {moved * 100f:F0} cm ({bodyTapMaxMove * 100f:F0})");
                if (isDraggingBody) ReleaseBody();
                ResetBodyInteraction();
                if (wasTap)
                    OpenCorpseLoot(tapCorpse);
                return;
            }
            // Lost the grip without a clean stateUp: let go
            if (!grip.state)
            {
                if (isDraggingBody) ReleaseBody();
                ResetBodyInteraction();
            }
            // Else FixedUpdate drives the drag
        }

        private void ResetBodyInteraction()
        {
            bodyInteractionArmed = false;
            bodyInteractionCorpse = null;
        }

        // A grip press with that hand's palm on a body arms the interaction and grabs the part under the palm. True = the body took this press.
        private bool TryArmBodyInteraction(bool dominant)
        {
            VRBoolean grip = BodyGrip(dominant);
            Transform hand = BodyHand(dominant);
            if (grip == null || hand == null || !grip.stateDown)
                return false;
            if (!TryFindBodyBone(PalmOrigin(dominant), out RigidbodySpawner bone, out Corpse corpse, out CorpseRagdoll ragdoll, out MonoBehaviour reviveInteractable))
                return false;
            string busy = BodyHandBusy(dominant);
            if (busy != null)
            {
                if (logBodyGrips)
                    Plugin.MyLog.LogInfo($"[BodyGrab] {(dominant ? "dominant" : "off")} hand's grip on a body not taken: {busy}");
                return false;
            }
            bodyGrabDominant = dominant;
            bodyInteractionArmed = true;
            bodyInteractionCorpse = corpse;
            bodyInteractionStartTime = Time.time;
            bodyInteractionStartHandPos = hand.position;
            if (bone != null && ragdoll != null)
                BeginBodyGrab(bone, corpse, ragdoll, reviveInteractable, hand);
            return true;
        }

        // null = that hand is free to take a body, else what it's doing
        private string BodyHandBusy(bool dominant)
        {
            if (heldItem != null)
                return "holding loot";
            if (isSummoningLoot)
                return "loot coming to the hand";
            if (VRGlobals.usingItem || EatingInteractionController.ManualActive)
                return "using an item";
            if (PhysicalDoors.HoldsDoor(dominant))
                return "on a door";
            // With the gun in the left hand, the free hand is the right one, but it is drawn by a different arm than these grabs are built on
            if (Handedness.GunInLeftHand)
                return "the gun is in the left hand";
            if (dominant)
                return DominantHandBusy();
            if (NearForegrip())
                return "at the gun's foregrip";
            // This press takes the gun into this hand instead (ChestHang)
            if (ChestHang.OffHandAtGunGrip)
                return "at the gun's grip";
            // Or it takes a gun off the body (BodyWeapons)
            if (BodyWeapons.OffHandAtBodyGun)
                return "at a gun on the body";
            return null;
        }

        //--- Detection ---------------------------------------------------------------
        // Finds the ragdoll bone nearest the palm, on a Corpse or a FIKA downed teammate. The owner (corpse, or the revive
        // component) is reported even when the hand is only on the body's bound collider, so tap-to-loot still works:
        // bone may be null then. ragdoll is the RagdollClass to drive when bone is non-null.
        private bool TryFindBodyBone(Vector3 palm, out RigidbodySpawner bone, out Corpse corpse,
            out CorpseRagdoll ragdoll, out MonoBehaviour reviveInteractable)
        {
            bone = null;
            corpse = null;
            ragdoll = null;
            reviveInteractable = null;

            int count = Physics.OverlapSphereNonAlloc(palm, bodyGrabRadius, bodyOverlap, DeadBodyMask, QueryTriggerInteraction.Ignore);
            Transform ownRoot = VRGlobals.player != null && VRGlobals.player.Transform != null ? VRGlobals.player.Transform.Original.root : null;
            float best = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Collider col = bodyOverlap[i];
                if (col == null || (ownRoot != null && col.transform.root == ownRoot)) continue;

                // Owner of this Deadbody collider: a Corpse, or a FIKA downed teammate (a Fika.Core type, so resolved through
                // the FikaSync bridge as a MonoBehaviour + its RagdollClass)
                Corpse c = col.GetComponentInParent<Corpse>();
                MonoBehaviour revive = null;
                CorpseRagdoll reviveRagdoll = null;
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
        private void BeginBodyGrab(RigidbodySpawner bone, Corpse corpse, CorpseRagdoll ragdoll, MonoBehaviour reviveInteractable, Transform hand)
        {
            grabbedCorpse = corpse;
            grabbedRagdoll = ragdoll;
            grabbedReviveInteractable = reviveInteractable;
            grabbedIsDownedPlayer = corpse == null && reviveInteractable != null;
            grabbedBodyRb = bone.Rigidbody;

            // Reactivate a settled or torn-down ragdoll as the game does when a corpse is shot (GoreController):
            // Ragdoll.Start() recreates the bone rigidbodies + joints and makes them dynamic.
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

            // EFT builds corpses with keepRigidbody (Ragdoll.Bool_0) = false: once settled and out of the camera view, the
            // settle coroutine destroys the joints and rigidbodies, which would end the drag. Setting it true skips that teardown;
            // it stays true after release.
            if (grabbedRagdoll != null)
                grabbedRagdoll._keepRigidbody = true;

            // EFT's settle coroutine can't freeze the body while this predicate returns false
            if (grabbedRagdoll != null)
                grabbedRagdoll._checkCorpseIsStill = RagdollNeverFreeze;

            bodyDragMoveSpeedMultiplier = bodyDragSlowsMovement ? bodyDragSpeedMultiplier : 1f;

            // Re-grabbing a body that was still settling: we drive it again now
            RemoveReleasedBody(grabbedRagdoll);

            ThawRagdoll(); // make sure the rest of the body is dynamic + awake so it follows

            // Pin the grabbed bone to the hand: kinematic = an infinite-mass anchor the joints pull the body toward.
            // EFT registers every ragdoll bone with its physics-support system (GClass745 -> SyncTransformsClass), which
            // sets the body's velocity each step; on a kinematic body that spams "Setting velocity of a kinematic body is
            // not supported". Unsupport first, as the game's own freeze (RagdollClass.method_1) does.
            PhysicsExtensions.UpdateController.UnsupportRigidbody(grabbedBodyRb);
            grabbedBodyRb.isKinematic = true;
            grabbedBodyRb.useGravity = false;
            grabbedBodyRb.detectCollisions = true;
            grabbedBodyRb.maxDepenetrationVelocity = bodyDragDepenetrationVelocity;
            // ContinuousSpeculative is the one continuous mode valid on a kinematic body
            grabbedBodyRb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            grabBodyHand = hand;
            grabBodyPosOffset = hand.InverseTransformPoint(grabbedBodyRb.position);
            grabBodyRotOffset = Quaternion.Inverse(hand.rotation) * grabbedBodyRb.rotation;

            isDraggingBody = true;
            RefreshBonesKeptAwake();

            // FIKA: resolve the network id the drag is broadcast under.
            //   - corpse: its GameWorld.ObservedPlayersCorpses key.
            //   - downed teammate: the live player's NetId (CoopHandler); the sync relocates them so a revive lands at
            //     the dragged-to spot, and drives observer ragdolls (FikaVrSync.SendDownedDrag).
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
                    // We drive this corpse now: drop the remote-drag bookkeeping so a later hand-off re-enters the
                    // remote pin cleanly
                    FikaClearRemoteDraggedCorpse(corpseNetId);
                }
            }

            // Pull the corpse's loot waypoint for the drag so bots can't loot it in transit (re-added where it settles).
            // Local: this machine runs ORBIT (single player, or a VR host).
            if (!grabbedIsDownedPlayer && grabbedCorpse != null)
                OrbitSupport.OnCorpseGrabbed(grabbedCorpse);
            // Co-op: the bots + ORBIT run on the host, which may not run this mod; tell it to pull the waypoint there
            if (!grabbedIsDownedPlayer && grabbedCorpse != null && InstalledMods.FIKAInstalled)
                FikaSendCorpseLootGrab(grabbedCorpse.PlayerProfileID);

            VRInput.Haptic.Execute(0, 0.08f, 1, 0.5f, BodyHandSource(bodyGrabDominant));
            if (debugBodyGrab)
                Plugin.MyLog.LogInfo($"[BodyGrab] grabbed {bone.name} on {(corpse != null ? corpse.name : "?")} netId={localDraggedCorpseNetId}");
        }

        // Driven in FixedUpdate so the kinematic anchor moves with proper joint solving.
        private void FixedUpdate()
        {
            // Let-go bodies settle whether or not we are dragging
            UpdateReleasedBodies();

            if (!isDraggingBody)
                return;
            // Let go if the body went invalid, or a downed teammate was revived mid-drag: FIKA destroys the
            // ReviveInteractable and re-enables the player's animators, which would fight the kinematic anchor
            if (grabbedBodyRb == null || grabBodyHand == null || !VRGlobals.inGame
                || (grabbedIsDownedPlayer && grabbedReviveInteractable == null))
            {
                ReleaseBody();
                return;
            }

            ThawRagdoll();

            // The held bone is kinematic: once PhysX sleeps it, MovePosition stops moving it. ThawRagdoll skips it, so
            // wake it every step (IsSleeping is unreliable on kinematic bodies).
            grabbedBodyRb.WakeUp();

            Vector3 target = grabBodyHand.TransformPoint(grabBodyPosOffset);

            // Heavy drag: chase the hand at a capped speed and resist upward lift
            Vector3 cur = grabbedBodyRb.position;
            Vector3 delta = target - cur;
            if (delta.y > 0f)
                delta.y *= bodyLiftFactor;
            float maxStep = bodyDragMaxSpeed * Time.fixedDeltaTime;
            grabbedBodyRb.MovePosition(cur + Vector3.ClampMagnitude(delta, maxStep));
            Quaternion targetRot = grabBodyHand.rotation * grabBodyRotOffset;
            grabbedBodyRb.MoveRotation(Quaternion.RotateTowards(
                grabbedBodyRb.rotation, targetRot, bodyDragMaxAngularSpeed * Time.fixedDeltaTime));

            // FIKA: broadcast the pose of every bone (throttled). Sending only the pinned bone and leaving the rest to the observer's
            // local physics lets the dangling bones drift apart and stretch. A grab is a corpse or a downed teammate, never both.
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

        // Every physics step: keep every bone of the grabbed ragdoll (except the hand-driven one) dynamic and awake, so
        // the joints carry the drag through the whole body.
        //   - The corpse's settle timer can re-freeze bones kinematic mid-drag.
        //   - PhysX won't reliably wake a sleeping bone when the kinematic bone it's jointed to is MovePosition'd: it
        //     lags behind and the mesh stretches.
        private void ThawRagdoll()
        {
            RigidbodySpawner[] bones = grabbedRagdoll != null ? grabbedRagdoll._rigidbodySpawners : null;
            if (bones == null)
                return;
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody body = bones[i] != null ? bones[i].Rigidbody : null;
                if (body == null || body == grabbedBodyRb)
                    continue;
                // Gravity asserted every step, not only when thawing: the limbs must hang, not float rigidly
                if (body.isKinematic)
                    body.isKinematic = false;
                body.useGravity = true;
                body.detectCollisions = true;
                if (body.IsSleeping())
                    body.WakeUp();
                body.maxDepenetrationVelocity = bodyDragDepenetrationVelocity;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
        }

        // On release: the bones' raised depenetration velocity back to the settled value
        private void RestoreRagdollCollision()
        {
            RigidbodySpawner[] bones = grabbedRagdoll != null ? grabbedRagdoll._rigidbodySpawners : null;
            if (bones == null)
                return;
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody body = bones[i] != null ? bones[i].Rigidbody : null;
                if (body != null)
                    body.maxDepenetrationVelocity = bodyDragRestDepenetrationVelocity;
            }
        }

        private void ReleaseBody()
        {
            if (grabbedBodyRb != null)
            {
                grabbedBodyRb.isKinematic = false;
                grabbedBodyRb.useGravity = true;
                grabbedBodyRb.detectCollisions = true;

                // Throw: the controller's release velocity (scaled + clamped) goes into the bone. Not for a downed
                // teammate: they are placed, not thrown.
                if (!grabbedIsDownedPlayer)
                {
                    Vector3 v = ControllerVelocity.GetSteamVRVelocity(BodyHandSource(bodyGrabDominant)) * bodyThrowVelocityScale;
                    if (v.sqrMagnitude > 0.0001f)
                        grabbedBodyRb.velocity = Vector3.ClampMagnitude(v, bodyMaxThrowSpeed);
                }

                // Re-register the bone with EFT's physics-support system (unsupported on grab). Same order as
                // RagdollClass.Start(): velocity, then SupportRigidbody at Low quality.
                PhysicsExtensions.UpdateController.SupportRigidbody(grabbedBodyRb, 0f);
                grabbedBodyRb.WakeUp();

                VRInput.Haptic.Execute(0, 0.05f, 1, 0.3f, BodyHandSource(bodyGrabDominant));
            }

            // Turn physics back on for the whole ragdoll so it falls; then the release-settle manager takes it (land, rest,
            // freeze). Same path for corpses and downed teammates. A downed ragdoll makes a short jump on release; freezing it
            // kinematic doesn't stop that, because something outside the ragdoll moves the body root.
            ReactivateRagdollBones();
            RestoreRagdollCollision();

            if (InstalledMods.FIKAInstalled && localDraggedCorpseNetId >= 0)
                FikaSendDraggedBodyReleased(localDraggedCorpseNetId);
            else if (InstalledMods.FIKAInstalled && localDraggedDownedNetId >= 0)
                FikaSendDownedDragReleased(localDraggedDownedNetId, grabbedRagdoll);

            if (grabbedRagdoll != null)
            {
                // The corpse goes along so its AI loot position can be relocated once it lands (null for a downed
                // teammate: those relocate via Teleport-on-revive)
                AddReleasedBody(grabbedRagdoll, grabbedIsDownedPlayer ? null : grabbedCorpse);
            }

            ResetBodyGrabState();
        }

        //--- Release-settle manager --------------------------------------------------
        private void AddReleasedBody(CorpseRagdoll ragdoll, Corpse corpse)
        {
            RemoveReleasedBody(ragdoll);
            releasedBodies.Add(new ReleasedBody { ragdoll = ragdoll, corpse = corpse, restTimer = 0f, releaseTime = Time.time });
        }

        private void RemoveReleasedBody(CorpseRagdoll ragdoll)
        {
            for (int i = releasedBodies.Count - 1; i >= 0; i--)
                if (ReferenceEquals(releasedBodies[i].ragdoll, ragdoll))
                    releasedBodies.RemoveAt(i);
        }

        // Per physics step, for each let-go body: keep every bone awake + dynamic + under gravity so it falls, and once
        // all bones have stayed under bodyLandedSpeed for bodyFreezeDelay seconds (or bodyFreezeMaxTime passed) freeze it.
        private void UpdateReleasedBodies()
        {
            if (releasedBodies.Count == 0)
                return;
            int settlingBefore = releasedBodies.Count;
            float deltaTime = Time.fixedDeltaTime;
            float landedSqr = bodyLandedSpeed * bodyLandedSpeed;

            for (int i = releasedBodies.Count - 1; i >= 0; i--)
            {
                ReleasedBody body = releasedBodies[i];
                RigidbodySpawner[] bones = body.ragdoll != null ? body.ragdoll._rigidbodySpawners : null;
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
                    // A falling body must not sleep or freeze mid-air, and stays simulated during the rest timer
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
                    body.restTimer += deltaTime;
                else
                    body.restTimer = 0f;

                if (body.restTimer >= bodyFreezeDelay || Time.time - body.releaseTime >= bodyFreezeMaxTime)
                {
                    FreezeReleasedBody(body.ragdoll, bones);
                    // Repoint the looting AI: bots loot the body where it now lies, not where it died
                    if (body.corpse != null)
                    {
                        // Local: this machine runs ORBIT
                        OrbitSupport.OnDraggedCorpseSettled(body.corpse);
                        // Co-op: the host/headless runs the bots but may not run this mod. Given the rest spot, it
                        // re-adds the ORBIT waypoint and moves its ragdoll copy there (live scanners like LootingBots follow).
                        if (InstalledMods.FIKAInstalled)
                        {
                            Transform pelvis = body.corpse.TrackableTransform != null
                                ? body.corpse.TrackableTransform : body.corpse.transform;
                            FikaSendCorpseLootSettle(body.corpse.PlayerProfileID, pelvis.position);
                        }
                    }
                    releasedBodies.RemoveAt(i);
                }
            }
            if (releasedBodies.Count != settlingBefore)
                RefreshBonesKeptAwake();
        }

        // Freezes a settled body kinematic in place, like EFT's own RagdollClass.method_1 (unsupport + Discrete +
        // kinematic). Also the rest depenetration velocity (a later shot must not pop it) and a Func_0 that returns true,
        // so the settle coroutine kept alive by NeverFreeze exits.
        private void FreezeReleasedBody(CorpseRagdoll ragdoll, RigidbodySpawner[] bones)
        {
            for (int b = 0; b < bones.Length; b++)
            {
                Rigidbody body = bones[b] != null ? bones[b].Rigidbody : null;
                if (body == null)
                    continue;
                PhysicsExtensions.UpdateController.UnsupportRigidbody(body);
                body.maxDepenetrationVelocity = bodyDragRestDepenetrationVelocity;
                body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                body.isKinematic = true;
            }
            if (ragdoll != null)
                ragdoll._checkCorpseIsStill = RagdollAlwaysFreeze;
        }

        // Every bone of the dragged ragdoll back into active physics (dynamic + gravity + awake) on release. No
        // SupportRigidbody here: some bones may still be registered with EFT's support system (double-registering leaks a
        // List_0 entry), and an unsupported dynamic body still falls.
        private void ReactivateRagdollBones()
        {
            RigidbodySpawner[] bones = grabbedRagdoll != null ? grabbedRagdoll._rigidbodySpawners : null;
            if (bones == null)
                return;
            for (int i = 0; i < bones.Length; i++)
            {
                Rigidbody body = bones[i] != null ? bones[i].Rigidbody : null;
                if (body == null)
                    continue;
                if (body.isKinematic)
                    body.isKinematic = false;
                body.useGravity = true;
                body.detectCollisions = true;
                body.WakeUp();
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
            RefreshBonesKeptAwake(); // after ReleaseBody's AddReleasedBody: a let-go body stays awake while it settles
        }

        // FIKA "steal": another player grabbed the body we were dragging more recently. Stop our drive without a throw
        // or a release broadcast; their packets drive our copy now (the FikaSync module pins the bone they hold, the
        // ragdoll stays live). Called through FikaVrSync.onYieldBodyDrag from the module's BodyDragApply.
        public void RelinquishBodyDrag()
        {
            if (!isDraggingBody)
                return;
            RestoreRagdollCollision();
            VRInput.Haptic.Execute(0, 0.1f, 1, 0.6f, BodyHandSource(bodyGrabDominant));
            ResetBodyGrabState();
            ResetBodyInteraction();
        }

        // Reverse lookup of a corpse's network id (GameWorld.ObservedPlayersCorpses is keyed by the dead player's NetId).
        // False for a non-networked corpse, which then doesn't sync.
        private static bool TryGetCorpseNetId(Corpse corpse, out int netId)
        {
            netId = -1;
            if (corpse == null)
                return false;
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || gameWorld.ObservedPlayersCorpses == null)
                return false;
            foreach (var entry in gameWorld.ObservedPlayersCorpses)
            {
                if (ReferenceEquals(entry.Value, corpse))
                {
                    netId = entry.Key;
                    return true;
                }
            }
            return false;
        }

        //--- Corpse loot grid --------------------------------------------------------
        // Opens the corpse's loot grid: the tap-to-loot path here, and ProcessInteractiveObjects
        private void OpenCorpseLoot(Corpse corpse)
        {
            if (corpse == null)
                return;
            InteractionContextHelper.CG_GetAvailableInteractionState1 corpseInteractionClass = new InteractionContextHelper.CG_GetAvailableInteractionState1();
            corpseInteractionClass.compoundItem = (InventoryEquipment)corpse.Item;
            corpseInteractionClass.rootItem = (InventoryEquipment)corpse.Item;
            corpseInteractionClass.lootItemOwner = corpse.ItemOwner;
            corpseInteractionClass.controller = VRGlobals.player.InventoryController;
            corpseInteractionClass.owner = PlayerOwner;
            corpseInteractionClass.method_3();
        }

        //--- FIKA bridge wrappers (companion-DLL isolation) --------------------------
        // The only members of this class that name a SptVrFikaSync (SPT-VR-FikaSync.dll) type; call them only inside an
        // `if (InstalledMods.FIKAInstalled)` block. Mono binds a cross-assembly call when it JITs the method containing
        // it, not when the call runs: an inline FikaVrSync.* call in an always-run method throws FileNotFoundException
        // without the companion DLL, even behind a runtime gate. One NoInlining method per call keeps the reference out
        // of the callers.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static MonoBehaviour FikaFindRevive(Collider bodyCollider, out CorpseRagdoll ragdoll)
            => FikaVrSync.FindReviveInteractable(bodyCollider, out ragdoll);

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
        private static void FikaSendDownedDrag(int downedNetId, CorpseRagdoll ragdoll)
            => FikaVrSync.SendDownedDrag(downedNetId, ragdoll);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendDraggedBodyReleased(int corpseNetId)
            => FikaVrSync.SendDraggedBodyReleased(corpseNetId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendDownedDragReleased(int downedNetId, CorpseRagdoll ragdoll)
            => FikaVrSync.SendDownedDragReleased(downedNetId, ragdoll);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendCorpseLootGrab(string profileId)
            => FikaVrSync.SendCorpseLootGrab(profileId);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendCorpseLootSettle(string profileId, Vector3 restPos)
            => FikaVrSync.SendCorpseLootSettle(profileId, restPos);
    }
}
