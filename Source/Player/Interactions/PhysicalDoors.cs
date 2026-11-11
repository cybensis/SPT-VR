using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Diz.LanguageExtensions;
using EFT;
using EFT.Interactive;
using RootMotion.FinalIK;
using TarkovVR.ModSupport;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.Interactions
{
    // Doors by hand. A grip on the handle takes it: the hand's arc about the handle's own axis turns it, and once it's turned far
    // enough (unlatchTurn) the door follows the hand, only within its one-way range (CloseAngle..OpenAngle). A grip on the door
    // itself moves an open door, or a door without a turnable handle. A locked door's handle stops short and the door only rattles
    // in its frame. Tarkov's own Open / Close run when the door leaves / reaches its frame, so FIKA, bots, quests and sound occlusion
    // follow the state; only their automatic swing is skipped (DoorSwingPatches). FIKA syncs the state only: the angle and the
    // handle go to the other players through SPT-VR-FikaSync.
    internal static class PhysicalDoors
    {
        public static bool enabled = true;
        public static float handleReach = 0.09f;        // m from the palm to the handle's meshes that takes the handle
        public static float leverReach = 0.14f;         // m off the handle's turning axis, and...
        public static float axisReach = 0.16f;          // ...m along it from the pivot: the lever on the far side of the door, a knob
        public static float panelReach = 0.07f;         // m from the palm to the door that takes the door itself
        public static float searchRadius = 0.3f;
        public static float unlatchTurn = 0.6f;         // handle turn (0 rest .. 1 fully down) that frees the latch
        public static float lockedHandleTurn = 0.25f;   // a locked door's handle stops here
        public static float handleReturnTime = 0.15f;   // s the let-go handle takes to spring back up
        public static float twistLeverRadius = 0.025f;  // m: a hold closer than this to the axis turns the handle by wrist twist
        public static float openStateAngle = 4f;        // deg off its frame that tells the game the door is open
        public static float rattleDegrees = 0.8f;       // how far a latched door gives
        public static float rattleVolume = 0.15f;
        public static float maxSwingSpeed = 720f;       // deg/s the hand can swing it
        public static float minHingeRadius = 0.08f;     // m: a hold closer to the hinge than this doesn't swing it
        public static float breakDistance = 0.35f;      // m the controller may get from the held point before the hand slips off
        public static float flingMinSpeed = 15f;        // deg/s at the release that keeps it swinging
        // The release swing from the hand's own tracked velocity as the grip opens (like a grenade throw), not the door's smoothed speed,
        // which lags and drops as the hand slows letting go. false = the smoothed speed.
        public static bool releaseFromHandVelocity = true;
        public static float swingDamping = 3f;          // 1/s
        public static float swingStopSpeed = 8f;        // deg/s
        public static float slamSpeed = 180f;           // deg/s that shuts it at full volume
        public static float handReleaseTime = 0.12f;    // s the hand takes back to the controller
        public static float handleSnapTime = 0.08f;     // s the hand takes onto the handle at the grab
        public static float handleSnapSkin = 0.03f;     // m from the palm point (the knuckle line) to the handle's surface
        public static float pushLeadDegrees = 0.3f;     // a door pushed by a hand turns this much beyond the contact (not touching it again at once)
        public static float syncInterval = 1f / 15f;    // s between the angle packets of a moving door
        public static bool logGrabs = true;             // a line per grab: the door, its handle, its angles
        internal static bool syncBroken;                // SPT-VR-FikaSync older than 2.2.0: no door packet to send
        public static bool logCensus = true;            // a line per raid: how many doors have a turnable handle

        public static bool Active => enabled && VRSettings.GetPhysicalDoorsOn();

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private sealed class Hold
        {
            public bool dominant;
            public bool held;
            public bool releasing;
            public Door door;
            public bool onHandle;
            public Transform rig;
            public LimbIK armIk;
            public Transform savedTarget;
            public Transform latch;
            public Vector3 palmInRig;                   // the palm in the rig hand's frame at the grab
            public Vector3 controllerAtGrab;            // world: that palm on the controller (HandCollision's clamp left out)
            public Vector3 heldPointAtGrab;             // world: where the palm took the door
            public Vector3 heldPointInDoor;
            public float angleAtGrab;
            public float turnAtGrab;
            public Vector3 leverAtGrab;                 // the handle's parent frame, across its axis
            public bool turnByTwist;
            public Quaternion controllerInHandleParentAtGrab;
            public float releaseStartTime;
            public Vector3 releaseStartPosition;        // in the rig's parent frame
            public Quaternion releaseStartRotation;
            public float grabTime;
            public Vector3 latchBaseLocal;              // where it took hold, in what it holds
            public Vector3 latchSnapLocal;              // onto the handle's surface from there
        }

        // A door a hand moves, or that still swings / has its handle springing back
        private sealed class DoorRun
        {
            public Door door;
            public DoorHandle handle;
            public float handleFullTurn;                // deg from rest to fully down; 0 = no turnable handle
            public Vector3 handleAxisLocal;             // in the handle's parent frame
            public float turn;
            public bool handleOffRest;
            public bool latchClicked;
            public bool lockedStopFelt;
            public bool latched;
            public Hold driver;                         // the hand moving the door (the last to take it)
            public Hold turner;                         // the hand on the handle
            public bool swinging;
            public float angularVelocity;               // deg/s of CurrentAngle
            public bool rattleAtStop;
            public float lastRattleTime = -10f;
            public float nextStateTryTime;
            public float lastSyncTime = -10f;
            public float syncedAngle = float.NaN;
            public float syncedTurn;
            public bool sent;
            public int gunFrame = -10;                  // the last frame the gun's physics body moved it (WeaponPhysicsProxy)
            public bool gunMoved;
            public float hitSpeed;                      // deg/s the gun drove it into a stop at (the shut's volume)
            public bool HasTurnableHandle => handle != null && handleFullTurn > 0f;
        }

        private static readonly Hold[] holds = { new Hold { dominant = false }, new Hold { dominant = true } };
        private static readonly Dictionary<Door, DoorRun> runs = new Dictionary<Door, DoorRun>();
        private static readonly List<Door> finishedRuns = new List<Door>();
        private static readonly Collider[] overlapBuffer = new Collider[48];
        private static readonly HashSet<Door> visitedDoors = new HashSet<Door>();
        private static readonly HashSet<string> loggedDoors = new HashSet<string>();
        private static Door interactionInProgress;
        private static int censusPlayerId;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // For the rest of the mod

        // A hand holds a door: its grip is taken (the loot pointer, the body grab and the corpse tap skip it)
        internal static bool HoldsDoor(bool dominant) => holds[dominant ? 1 : 0].held;

        // The fingers close on this (HandsInteractionController.Grasp): the handle, or the door taken by its panel
        internal static bool GraspTarget(bool dominant, out Component target, out Transform latch)
        {
            Hold hold = holds[dominant ? 1 : 0];
            target = null;
            latch = null;
            if (!hold.held || hold.door == null || hold.latch == null)
                return false;
            target = hold.onHandle && runs.TryGetValue(hold.door, out DoorRun run) && run.handle != null ? run.handle : (Component)hold.door;
            latch = hold.latch;
            return true;
        }

        // HandCollision leaves this rig hand unclamped: the controller pulls the door through where the door is
        internal static bool HoldsRig(Transform rig)
        {
            if (rig == null)
                return false;
            for (int i = 0; i < holds.Length; i++)
                if ((holds[i].held || holds[i].releasing) && holds[i].rig == rig)
                    return true;
            return false;
        }

        // The doors this mod moves by hand (the rest keep the vanilla grip: open / close)
        internal static bool Supports(Door door)
        {
            if (door == null || !door.isActiveAndEnabled || !door.Operatable || door.NoInteractionsAllowed || door._keepRotation
                || door.transform.parent == null)
                return false;
            // (BTR doors and the like have their own logic)
            if (door.GetType() != typeof(Door) && !(door is KeycardDoor))
                return false;
            return Mathf.Abs(Mathf.DeltaAngle(door.CloseAngle, door.OpenAngle)) > 5f;
        }

        internal static bool SkipsSwing(WorldInteractiveObject door) => door != null && door == interactionInProgress;

        // The state Tarkov's swing would leave the door in, at once (DoorSwingPatches)
        internal static void SettleState(WorldInteractiveObject door, EDoorState state)
        {
            EDoorState result = state == EDoorState.Breaching ? EDoorState.Open : state;
            WorldInteractiveObject.InteractionState interaction = door._interaction;
            interaction.ProgressTime = interaction.Duration;
            door.DoorState = result;
            door._previousInteraction.SetEndData(interaction);
            if (result == EDoorState.Open)
            {
                Vector2 volume = door.OpenSoundVolumeRange;
                door.PlaySound(EDoorState.Open, Random.Range(volume.x, volume.y));
            }
            if (result == EDoorState.Shut && door is KeycardDoor keycardDoor && keycardDoor._lockOnShut)
                keycardDoor.Lock();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Per frame (HandsInteractionController.Update, before its bail)

        public static void Tick(HandsInteractionController hands, bool offHandFree, bool dominantHandFree)
        {
            if (!VRGlobals.inGame || VRGlobals.player == null || VRGlobals.vrPlayer == null || VRGlobals.ikManager == null)
            {
                ResetAll();
                return;
            }
            int playerId = VRGlobals.player.GetInstanceID();
            if (playerId != censusPlayerId)
            {
                ResetAll();
                censusPlayerId = playerId;
                if (logCensus)
                    LogCensus();
            }

            bool canHold = Active && !VRGlobals.menuOpen && !VRGlobals.player.IsSprintEnabled;
            UpdateHand(hands, holds[0], canHold && offHandFree, Handedness.OffHandGrip);
            UpdateHand(hands, holds[1], canHold && dominantHandFree, Handedness.GunHandGrip);

            float deltaTime = Time.deltaTime;
            finishedRuns.Clear();
            foreach (DoorRun run in runs.Values)
            {
                if (!UpdateRun(run, deltaTime))
                    finishedRuns.Add(run.door);
            }
            for (int i = 0; i < finishedRuns.Count; i++)
                runs.Remove(finishedRuns[i]);

            for (int i = 0; i < holds.Length; i++)
                UpdateLatch(holds[i]);
        }

        private static void UpdateHand(HandsInteractionController hands, Hold hold, bool free, VRBoolean grip)
        {
            if (hold.held)
            {
                if (!free || grip == null || !grip.state || !StillHeld(hold))
                    Release(hold);
                return;
            }
            if (free && grip != null && grip.stateDown && hands != null)
                TryGrab(hands, hold);
        }

        private static bool StillHeld(Hold hold)
        {
            Door door = hold.door;
            if (door == null || !door.isActiveAndEnabled || hold.rig == null || hold.armIk == null || hold.latch == null)
                return false;
            // Someone else's interaction (a bot, another player, the gaze menu) has the door now
            return door.DoorState != EDoorState.Interacting && door.DoorState != EDoorState.Breaching;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Taking hold

        private static void TryGrab(HandsInteractionController hands, Hold hold)
        {
            GameObject rigHand = hold.dominant ? VRGlobals.vrPlayer.RightHand : VRGlobals.vrPlayer.LeftHand;
            Transform rig = rigHand != null ? rigHand.transform : null;
            LimbIK armIk = hold.dominant ? VRGlobals.ikManager.rightArmIk : VRGlobals.ikManager.leftArmIk;
            if (rig == null || armIk == null || armIk.solver == null)
                return;
            Vector3 palm = hands.PalmOrigin(hold.dominant);
            if (!FindDoorAt(palm, out Door door, out bool onHandle))
                return;
            if (door.DoorState == EDoorState.Interacting || door.DoorState == EDoorState.Breaching)
                return;

            DoorRun run = RunFor(door);
            onHandle &= run.handle != null;
            if (hold.latch == null)
                hold.latch = new GameObject("VRDoorHandLatch").transform;
            hold.door = door;
            hold.onHandle = onHandle;
            hold.rig = rig;
            hold.armIk = armIk;
            // (a release still easing back hands the arm over from its own latch)
            hold.savedTarget = hold.releasing ? hold.savedTarget : armIk.solver.target;
            hold.releasing = false;
            hold.held = true;

            // The hand stays where it took hold, riding the handle (it turns) or the door
            hold.latch.SetParent(onHandle ? run.handle.transform : door.transform, worldPositionStays: false);
            hold.latch.SetPositionAndRotation(rig.position, rig.rotation);
            armIk.solver.target = hold.latch;
            // On a handle the hand eases onto its surface (the mesh near the palm; not the far side's lever, across the door)
            hold.grabTime = Time.time;
            hold.latchBaseLocal = hold.latch.localPosition;
            hold.latchSnapLocal = Vector3.zero;
            if (onHandle && HandleContact(run.handle, palm, out Vector3 contact) && (contact - palm).sqrMagnitude < handleReach * handleReach)
            {
                Vector3 outward = palm - contact;
                Vector3 palmTarget = outward.sqrMagnitude > 1e-8f ? contact + outward.normalized * handleSnapSkin : palm;
                hold.latchSnapLocal = hold.latch.parent.InverseTransformVector(palmTarget - palm);
            }

            hold.palmInRig = rig.InverseTransformPoint(palm);
            hold.controllerAtGrab = ControllerPoint(hold);
            hold.heldPointAtGrab = palm;
            hold.heldPointInDoor = door.transform.InverseTransformPoint(palm);
            hold.angleAtGrab = door.CurrentAngle;

            if (onHandle && run.HasTurnableHandle)
            {
                Transform handleParent = run.handle.transform.parent;
                Quaternion parentRotation = handleParent != null ? handleParent.rotation : Quaternion.identity;
                Vector3 pointLocal = handleParent != null ? handleParent.InverseTransformPoint(hold.controllerAtGrab) : hold.controllerAtGrab;
                hold.leverAtGrab = Vector3.ProjectOnPlane(pointLocal - run.handle.transform.localPosition, run.handleAxisLocal);
                float leverRadius = handleParent != null ? handleParent.TransformVector(hold.leverAtGrab).magnitude : hold.leverAtGrab.magnitude;
                hold.turnByTwist = leverRadius < twistLeverRadius;
                hold.controllerInHandleParentAtGrab = Quaternion.Inverse(parentRotation) * rig.rotation;
                hold.turnAtGrab = run.turn;
                run.turner = hold;
            }
            run.driver = hold;
            run.swinging = false;

            Pulse(hold, 0.2f, 0.03f, 90f);
            if (logGrabs)
                LogGrab(hold, run);
        }

        // The door the palm is on: its handle first (the meshes, or anywhere around its turning axis: the far side's lever, a knob),
        // else the door itself
        private static bool FindDoorAt(Vector3 palm, out Door found, out bool onHandle)
        {
            found = null;
            onHandle = false;
            float bestScore = float.MaxValue;
            int mask = (1 << 22) | (1 << LayersMaskController.DoorLayer) | (int)LayersMaskController.PlayerStaticCollisionsMask;
            int count = Physics.OverlapSphereNonAlloc(palm, searchRadius, overlapBuffer, mask, QueryTriggerInteraction.Collide);
            visitedDoors.Clear();
            for (int i = 0; i < count; i++)
            {
                Collider collider = overlapBuffer[i];
                if (collider == null)
                    continue;
                Door door = collider.GetComponentInParent<Door>();
                if (door == null || !Supports(door))
                    continue;
                if (visitedDoors.Add(door))
                {
                    float handleDistance = HandleDistance(door, palm);
                    if (handleDistance <= handleReach && handleDistance < bestScore)
                    {
                        bestScore = handleDistance;
                        found = door;
                        onHandle = true;
                    }
                }
                // The door itself: its solid colliders (its interaction trigger can reach past it)
                if (collider.isTrigger)
                    continue;
                float panelDistance = ColliderDistance(collider, palm);
                if (panelDistance <= panelReach && panelDistance + 1f < bestScore)
                {
                    bestScore = panelDistance + 1f;
                    found = door;
                    onHandle = false;
                }
            }
            return found != null;
        }

        // The nearest point of the handle's meshes (their own boxes) to a point
        private static bool HandleContact(DoorHandle handle, Vector3 point, out Vector3 contact)
        {
            contact = point;
            if (handle == null)
                return false;
            float best = float.MaxValue;
            foreach (Renderer renderer in handle.GetComponentsInChildren<Renderer>())
            {
                if (renderer == null || !renderer.enabled)
                    continue;
                Transform rendererTransform = renderer.transform;
                Vector3 closest = rendererTransform.TransformPoint(renderer.localBounds.ClosestPoint(rendererTransform.InverseTransformPoint(point)));
                float distance = (closest - point).sqrMagnitude;
                if (distance < best)
                {
                    best = distance;
                    contact = closest;
                }
            }
            return best < float.MaxValue;
        }

        private static float HandleDistance(Door door, Vector3 palm)
        {
            DoorHandle handle = door._handle;
            if (handle == null)
                return float.MaxValue;
            float best = float.MaxValue;
            foreach (Renderer renderer in handle.GetComponentsInChildren<Renderer>())
            {
                if (renderer == null || !renderer.enabled)
                    continue;
                Transform rendererTransform = renderer.transform;
                Vector3 closest = rendererTransform.TransformPoint(renderer.localBounds.ClosestPoint(rendererTransform.InverseTransformPoint(palm)));
                best = Mathf.Min(best, (closest - palm).magnitude);
            }
            if (TurnOf(handle, out _, out Vector3 axisLocal))
            {
                Transform handleParent = handle.transform.parent;
                Vector3 axis = (handleParent != null ? handleParent.TransformDirection(axisLocal) : axisLocal).normalized;
                Vector3 offset = palm - handle.transform.position;
                float along = Vector3.Dot(offset, axis);
                float acrossAxis = (offset - along * axis).magnitude;
                if (Mathf.Abs(along) <= axisReach && acrossAxis <= leverReach)
                    best = Mathf.Min(best, handleReach * 0.9f);
            }
            return best;
        }

        private static float ColliderDistance(Collider collider, Vector3 point)
        {
            bool exact = collider is BoxCollider || collider is SphereCollider || collider is CapsuleCollider
                || (collider is MeshCollider meshCollider && meshCollider.convex);
            if (exact)
                return (collider.ClosestPoint(point) - point).magnitude;
            return Mathf.Sqrt(collider.bounds.SqrDistance(point));
        }

        // The handle's turn from rest to fully down, about an axis in its parent's frame (localRotation = turn * DefaultRotation)
        private static bool TurnOf(DoorHandle handle, out float degrees, out Vector3 axisLocal)
        {
            degrees = 0f;
            axisLocal = Vector3.forward;
            if (handle == null || !IsRotation(handle.DefaultRotation) || !IsRotation(handle.OpenRotation))
                return false;
            Quaternion turn = handle.OpenRotation * Quaternion.Inverse(handle.DefaultRotation);
            turn.ToAngleAxis(out degrees, out axisLocal);
            if (degrees > 180f)
            {
                degrees = 360f - degrees;
                axisLocal = -axisLocal;
            }
            if (float.IsNaN(degrees) || degrees < 2f || axisLocal.sqrMagnitude < 0.5f)
            {
                degrees = 0f;
                return false;
            }
            axisLocal.Normalize();
            return true;
        }

        private static bool IsRotation(Quaternion rotation)
        {
            float length = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            return length > 0.5f && !float.IsNaN(length);
        }

        private static DoorRun RunFor(Door door)
        {
            if (runs.TryGetValue(door, out DoorRun run))
                return run;
            run = new DoorRun { door = door, handle = door._handle };
            if (TurnOf(run.handle, out float degrees, out Vector3 axisLocal))
            {
                run.handleFullTurn = degrees;
                run.handleAxisLocal = axisLocal;
            }
            run.latched = StartsLatched(door, run.HasTurnableHandle);
            runs[door] = run;
            return run;
        }

        // In its frame and locked, or shut with a handle to turn
        private static bool StartsLatched(Door door, bool turnableHandle)
        {
            bool inFrame = Mathf.Abs(Mathf.DeltaAngle(door.CloseAngle, door.CurrentAngle)) < 0.05f;
            return inFrame && (door.DoorState == EDoorState.Locked || (door.DoorState == EDoorState.Shut && turnableHandle));
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The gun's physics body (WeaponPhysicsProxy) pushing doors

        // Free to move and nobody else moving it: open a bit or without a handle to turn, not locked, no hand on it
        internal static bool MayBePushed(Door door)
        {
            if (!Active || !Supports(door))
                return false;
            EDoorState state = door.DoorState;
            if (state == EDoorState.Locked || state == EDoorState.Interacting || state == EDoorState.Breaching)
                return false;
            if (runs.TryGetValue(door, out DoorRun run))
                return !run.latched && run.driver == null;
            return !StartsLatched(door, TurnOf(door._handle, out _, out _));
        }

        internal static float AngularVelocityOf(Door door) => door != null && runs.TryGetValue(door, out DoorRun run) ? run.angularVelocity : 0f;

        // Where the gun's push left it this frame: the state, the sounds and the co-op sync follow from here (UpdateRun)
        internal static void Pushed(Door door, float angle, float angularVelocity, float hitSpeed)
        {
            if (!MayBePushed(door))
                return;
            DoorRun run = RunFor(door);
            door.CurrentAngle = angle;
            run.angularVelocity = angularVelocity;
            run.hitSpeed = Mathf.Max(run.hitSpeed, hitSpeed);
            run.swinging = false;
            run.gunFrame = Time.frameCount;
        }

        // A free hand pushing a door at a point (HandCollision): the door turns so that point goes the part of the move along its swing,
        // plus pushLeadDegrees. false = it couldn't (a stop in the way): the hand is blocked as by a wall.
        internal static bool PushedBy(Door door, Vector3 contactPoint, Vector3 displacement, float deltaTime)
        {
            if (!MayBePushed(door))
                return false;
            Vector3 axis = HingeAxis(door);
            Vector3 lever = Vector3.ProjectOnPlane(contactPoint - door.transform.position, axis);
            float radius = lever.magnitude;
            if (radius < minHingeRadius)
                return false;
            // A positive angle moves the point along axis x lever
            float travel = Vector3.Dot(displacement, Vector3.Cross(axis, lever) / radius);
            if (Mathf.Abs(travel) < 1e-5f)
                return true;
            float degrees = travel / radius * Mathf.Rad2Deg;
            degrees += Mathf.Sign(degrees) * pushLeadDegrees;
            float current = door.CurrentAngle;
            float speed = degrees / Mathf.Max(deltaTime, 1e-4f);
            float pushedSpeed = speed;
            float angle = ClampToRange(door, current, current + degrees, ref pushedSpeed, out bool atStop);
            float smoothed = Mathf.Lerp(AngularVelocityOf(door), pushedSpeed, 1f - Mathf.Exp(-Mathf.Max(deltaTime, 1e-4f) * 20f));
            Pushed(door, angle, smoothed, atStop ? Mathf.Abs(speed) : 0f);
            return !(atStop && pushedSpeed != speed);
        }

        // An angle kept within the door's range, measured from a known in-range angle (a wide door's range comes near 180 degrees,
        // where an absolute DeltaAngle would wrap); a speed out past the stop is stopped
        internal static float ClampToRange(Door door, float referenceAngle, float angle, ref float angularVelocity, out bool atStop)
        {
            float span = Span(door);
            float openness = Openness(door, referenceAngle) + Mathf.DeltaAngle(referenceAngle, angle) / span;
            float opening = angularVelocity * Mathf.Sign(span);
            atStop = false;
            if (openness >= 1f)
            {
                atStop = true;
                if (opening > 0f)
                    angularVelocity = 0f;
                return AngleAt(door, 1f);
            }
            if (openness <= 0f)
            {
                atStop = true;
                if (opening < 0f)
                    angularVelocity = 0f;
                return AngleAt(door, 0f);
            }
            return angle;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Letting go

        private static void Release(Hold hold)
        {
            if (!hold.held)
                return;
            hold.held = false;
            if (hold.door != null && runs.TryGetValue(hold.door, out DoorRun run))
            {
                if (run.turner == hold)
                    run.turner = OtherHold(hold, onHandle: true);
                if (run.driver == hold)
                {
                    run.driver = OtherHold(hold, onHandle: false);
                    if (run.driver == null)
                    {
                        if (run.latched)
                            run.door.CurrentAngle = run.door.CloseAngle;    // the give of a latched door back
                        else
                        {
                            if (releaseFromHandVelocity && TryReleaseSpeed(hold, run.door, out float releaseSpeed))
                                run.angularVelocity = releaseSpeed;
                            if (Mathf.Abs(run.angularVelocity) >= flingMinSpeed)
                                run.swinging = true;
                            else
                                run.angularVelocity = 0f;
                        }
                    }
                }
            }
            // The hand eases back onto the controller from where it held the door
            if (hold.latch != null && hold.rig != null)
            {
                hold.latch.SetParent(null, worldPositionStays: true);
                Transform frame = hold.rig.parent;
                hold.releaseStartPosition = frame != null ? frame.InverseTransformPoint(hold.latch.position) : hold.latch.position;
                hold.releaseStartRotation = frame != null ? Quaternion.Inverse(frame.rotation) * hold.latch.rotation : hold.latch.rotation;
                hold.releaseStartTime = Time.time;
                hold.releasing = true;
            }
            else
                EndRelease(hold);
            hold.door = null;
        }

        // The door's speed (deg/s) from the hand's velocity at the point it held, as the tracking runtime reports it this frame (the
        // grenade's throw velocity: OpenXR's palm, else SteamVR's controller), along the door's swing there
        private static bool TryReleaseSpeed(Hold hold, Door door, out float degreesPerSecond)
        {
            degreesPerSecond = 0f;
            if (door == null || VRGlobals.vrOffsetter == null)
                return false;
            SteamVR_Input_Sources source = hold.dominant ? Handedness.GunHandSource : Handedness.OffHandSource;
            Vector3 trackingVelocity = VRInput.UsingOpenXR
                ? OpenXRInput.GetGripVelocity(source == SteamVR_Input_Sources.LeftHand ? OpenXRInput.LEFT_HAND : OpenXRInput.RIGHT_HAND)
                : ControllerVelocity.GetSteamVRVelocity(source);
            Vector3 velocity = VRGlobals.vrOffsetter.transform.TransformDirection(trackingVelocity);
            Vector3 axis = HingeAxis(door);
            Vector3 lever = Vector3.ProjectOnPlane(door.transform.TransformPoint(hold.heldPointInDoor) - door.transform.position, axis);
            float radius = lever.magnitude;
            if (radius < minHingeRadius || float.IsNaN(velocity.x))
                return false;
            degreesPerSecond = Vector3.Dot(velocity, Vector3.Cross(axis, lever) / radius) / radius * Mathf.Rad2Deg;
            degreesPerSecond = Mathf.Clamp(degreesPerSecond, -maxSwingSpeed, maxSwingSpeed);
            return true;
        }

        private static Hold OtherHold(Hold hold, bool onHandle)
        {
            Hold other = holds[hold.dominant ? 0 : 1];
            return other.held && other.door == hold.door && (!onHandle || other.onHandle) ? other : null;
        }

        private static void UpdateLatch(Hold hold)
        {
            if (hold.held)
            {
                // (the two-handing edges and the equip paths set the arm's target: ours while the door is held)
                if (hold.armIk != null && hold.armIk.solver.target != hold.latch)
                    hold.armIk.solver.target = hold.latch;
                if (hold.latch != null && hold.latchSnapLocal != Vector3.zero)
                {
                    float snap = Mathf.SmoothStep(0f, 1f, (Time.time - hold.grabTime) / Mathf.Max(handleSnapTime, 0.001f));
                    hold.latch.localPosition = hold.latchBaseLocal + hold.latchSnapLocal * snap;
                }
                return;
            }
            if (!hold.releasing)
                return;
            float progress = (Time.time - hold.releaseStartTime) / Mathf.Max(handReleaseTime, 0.001f);
            // Something else took the arm while it eased back: it's theirs
            if (progress >= 1f || hold.rig == null || hold.latch == null || hold.armIk == null || hold.armIk.solver.target != hold.latch)
            {
                EndRelease(hold);
                return;
            }
            Transform frame = hold.rig.parent;
            Vector3 from = frame != null ? frame.TransformPoint(hold.releaseStartPosition) : hold.releaseStartPosition;
            Quaternion fromRotation = frame != null ? frame.rotation * hold.releaseStartRotation : hold.releaseStartRotation;
            float ease = Mathf.SmoothStep(0f, 1f, progress);
            hold.latch.SetPositionAndRotation(Vector3.Lerp(from, hold.rig.position, ease), Quaternion.Slerp(fromRotation, hold.rig.rotation, ease));
        }

        private static void EndRelease(Hold hold)
        {
            if (hold.armIk != null && hold.latch != null && hold.armIk.solver.target == hold.latch)
                hold.armIk.solver.target = hold.savedTarget;
            hold.releasing = false;
            hold.savedTarget = null;
            hold.rig = null;
            hold.armIk = null;
        }

        private static void ResetAll()
        {
            for (int i = 0; i < holds.Length; i++)
            {
                Hold hold = holds[i];
                if (hold.held || hold.releasing)
                {
                    if (hold.armIk != null && hold.latch != null && hold.armIk.solver.target == hold.latch)
                        hold.armIk.solver.target = hold.savedTarget;
                    if (hold.latch != null)
                        hold.latch.SetParent(null, worldPositionStays: true);
                }
                hold.held = false;
                hold.releasing = false;
                hold.door = null;
                hold.savedTarget = null;
                hold.rig = null;
                hold.armIk = null;
            }
            runs.Clear();
            interactionInProgress = null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // A door being moved

        // false = done with it
        private static bool UpdateRun(DoorRun run, float deltaTime)
        {
            Door door = run.door;
            if (door == null || !door.isActiveAndEnabled)
                return false;
            if (door.DoorState == EDoorState.Interacting || door.DoorState == EDoorState.Breaching)
            {
                // Someone else's interaction swings it now (and no last packet of ours stops it on the other players)
                run.swinging = false;
                run.angularVelocity = 0f;
                run.sent = false;
            }
            bool gunMoves = Time.frameCount - run.gunFrame <= 1;
            if (door.DoorState != EDoorState.Interacting && door.DoorState != EDoorState.Breaching)
            {
                UpdateHandle(run, deltaTime);
                if (run.driver != null)
                    DriveByHand(run, run.driver, deltaTime);
                else if (gunMoves)
                    run.gunMoved = true;            // the gun's physics body moves it this frame
                else
                {
                    // The gun off it: it carries on with the speed the push left it
                    if (run.gunMoved)
                    {
                        run.gunMoved = false;
                        run.swinging = Mathf.Abs(run.angularVelocity) >= swingStopSpeed;
                        if (!run.swinging)
                            run.angularVelocity = 0f;
                    }
                    if (run.swinging)
                        Swing(run, deltaTime);
                }
                UpdateLatched(run);
                UpdateGameState(run);
            }
            bool moving = run.driver != null || run.turner != null || run.swinging || run.handleOffRest || gunMoves;
            Sync(run, final: !moving);
            return moving;
        }

        private static void UpdateHandle(DoorRun run, float deltaTime)
        {
            if (!run.HasTurnableHandle)
                return;
            bool locked = run.door.DoorState == EDoorState.Locked;
            float turn = run.turner != null && run.turner.held
                ? HandTurn(run, run.turner)
                : Mathf.MoveTowards(run.turn, 0f, deltaTime / Mathf.Max(handleReturnTime, 0.01f));
            float limit = locked ? lockedHandleTurn : 1f;
            turn = Mathf.Clamp(turn, 0f, limit);
            if (turn != run.turn || run.handleOffRest)
            {
                Transform handleTransform = run.handle.transform;
                handleTransform.localRotation = Quaternion.Slerp(run.handle.DefaultRotation, run.handle.OpenRotation, turn);
                if (run.handle.pos)
                    handleTransform.localPosition = Vector3.Lerp(run.handle.DefaultPosition, run.handle.OpenPosition, turn);
            }

            // The latch giving way, the handle back up; a locked handle hitting its stop
            if (!locked && !run.latchClicked && turn >= unlatchTurn)
            {
                run.latchClicked = true;
                PlayClip(run.door, run.handle.DownSound, run.handle.transform.position, 14, 0.8f);
                if (run.turner != null)
                    Pulse(run.turner, 0.45f, 0.03f, 150f);
            }
            else if (run.latchClicked && turn < unlatchTurn * 0.4f)
            {
                run.latchClicked = false;
                PlayClip(run.door, run.handle.UpSound, run.handle.transform.position, 14, 0.8f);
            }
            if (locked && run.turner != null && !run.lockedStopFelt && turn >= limit - 0.001f)
            {
                run.lockedStopFelt = true;
                PlayClip(run.door, run.handle.DownSound, run.handle.transform.position, 14, 0.4f);
                Pulse(run.turner, 0.6f, 0.05f, 60f);
            }
            else if (run.lockedStopFelt && turn < limit * 0.5f)
                run.lockedStopFelt = false;

            run.turn = turn;
            run.handleOffRest = turn > 0.0001f;
        }

        private static float HandTurn(DoorRun run, Hold hold)
        {
            Transform handleTransform = run.handle.transform;
            Transform handleParent = handleTransform.parent;
            float degrees;
            if (hold.turnByTwist)
            {
                Quaternion parentRotation = handleParent != null ? handleParent.rotation : Quaternion.identity;
                Quaternion now = Quaternion.Inverse(parentRotation) * hold.rig.rotation;
                degrees = TwistDegrees(now * Quaternion.Inverse(hold.controllerInHandleParentAtGrab), run.handleAxisLocal);
            }
            else
            {
                Vector3 controller = ControllerPoint(hold);
                Vector3 pointLocal = handleParent != null ? handleParent.InverseTransformPoint(controller) : controller;
                Vector3 lever = Vector3.ProjectOnPlane(pointLocal - handleTransform.localPosition, run.handleAxisLocal);
                degrees = Vector3.SignedAngle(hold.leverAtGrab, lever, run.handleAxisLocal);
            }
            return hold.turnAtGrab + degrees / run.handleFullTurn;
        }

        // The part of a rotation about an axis, signed by it
        internal static float TwistDegrees(Quaternion rotation, Vector3 axis)
        {
            Vector3 along = Vector3.Project(new Vector3(rotation.x, rotation.y, rotation.z), axis);
            Quaternion twist = new Quaternion(along.x, along.y, along.z, rotation.w);
            float length = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (length < 1e-6f)
                return 0f;
            twist = new Quaternion(twist.x / length, twist.y / length, twist.z / length, twist.w / length);
            twist.ToAngleAxis(out float angle, out Vector3 twistAxis);
            if (angle > 180f)
                angle -= 360f;
            return Vector3.Dot(twistAxis, axis) >= 0f ? angle : -angle;
        }

        // The door's held point goes where the hand has moved since the grab: the angle about the hinge that puts it closest
        private static void DriveByHand(DoorRun run, Hold hold, float deltaTime)
        {
            Door door = run.door;
            float span = Span(door);
            float current = door.CurrentAngle;
            float openness = Openness(door, current);
            Vector3 target = hold.heldPointAtGrab + (ControllerPoint(hold) - hold.controllerAtGrab);

            // Pulled away (the door at its stop, walked off): the hand slips off
            Vector3 heldNow = door.transform.TransformPoint(hold.heldPointInDoor);
            if ((target - heldNow).sqrMagnitude > breakDistance * breakDistance)
            {
                Release(hold);
                return;
            }

            Vector3 axis = HingeAxis(door);
            Vector3 hinge = door.transform.position;
            Vector3 from = Vector3.ProjectOnPlane(hold.heldPointAtGrab - hinge, axis);
            Vector3 to = Vector3.ProjectOnPlane(target - hinge, axis);
            float desiredOpenness = openness;
            if (from.sqrMagnitude > minHingeRadius * minHingeRadius && to.sqrMagnitude > minHingeRadius * minHingeRadius)
            {
                float desiredAngle = hold.angleAtGrab + Vector3.SignedAngle(from, to, axis);
                desiredOpenness = openness + Mathf.DeltaAngle(current, desiredAngle) / span;
            }

            float newOpenness;
            if (!run.latched)
            {
                newOpenness = Mathf.Clamp01(Mathf.MoveTowards(openness, desiredOpenness, maxSwingSpeed * deltaTime / Mathf.Abs(span)));
                if (newOpenness >= 1f && openness < 1f && Mathf.Abs(run.angularVelocity) > 60f)
                    Pulse(hold, 0.3f, 0.04f, 70f);      // the door at its stop
            }
            else
            {
                // In its frame: it gives a little, and knocks against the latch
                float give = rattleDegrees / Mathf.Abs(span);
                newOpenness = Mathf.Clamp(desiredOpenness, 0f, give);
                bool atStop = desiredOpenness > give * 1.5f;
                if (atStop && !run.rattleAtStop && Time.time - run.lastRattleTime > 0.25f)
                {
                    run.lastRattleTime = Time.time;
                    float volume = door.DoorState == EDoorState.Locked ? rattleVolume * 1.4f : rattleVolume;
                    PlayClip(door, door.ShutSound, door.GetSoundPos(), door._shutSoundRolloff, volume);
                    Pulse(hold, 0.45f, 0.04f, 110f);
                }
                run.rattleAtStop = atStop;
            }
            SetAngle(run, AngleAt(door, newOpenness), deltaTime);
        }

        // Let go of while moving: it carries on, slowing down
        private static void Swing(DoorRun run, float deltaTime)
        {
            Door door = run.door;
            float span = Span(door);
            float next = Openness(door, door.CurrentAngle) + run.angularVelocity * deltaTime / span;
            if (next >= 1f)
            {
                next = 1f;
                run.swinging = false;
                run.angularVelocity = 0f;
            }
            else if (next <= 0f)
            {
                next = 0f;
                run.swinging = false;     // the speed is kept for the shut's volume (UpdateGameState)
            }
            door.CurrentAngle = AngleAt(door, next);
            if (run.swinging)
            {
                run.angularVelocity *= Mathf.Exp(-swingDamping * deltaTime);
                if (Mathf.Abs(run.angularVelocity) < swingStopSpeed)
                {
                    run.swinging = false;
                    run.angularVelocity = 0f;
                }
            }
        }

        // In its frame with the handle up (or locked): the latch holds it. A turned handle (or no handle to turn) frees it.
        private static void UpdateLatched(DoorRun run)
        {
            Door door = run.door;
            bool locked = door.DoorState == EDoorState.Locked;
            bool inFrame = Mathf.Abs(Mathf.DeltaAngle(door.CloseAngle, door.CurrentAngle)) < 0.05f;
            if (run.latched)
            {
                if (!locked && (!run.HasTurnableHandle || run.turn >= unlatchTurn))
                    run.latched = false;
            }
            else if (inFrame && (locked || (run.HasTurnableHandle && run.turn < unlatchTurn)))
                run.latched = true;
        }

        // Tarkov's own Open when it leaves its frame, Close when it's back in it
        private static void UpdateGameState(DoorRun run)
        {
            Door door = run.door;
            float hitSpeed = run.hitSpeed;
            run.hitSpeed = 0f;
            if (Time.time < run.nextStateTryTime)
                return;
            float degreesOpen = Mathf.Abs(Mathf.DeltaAngle(door.CloseAngle, door.CurrentAngle));
            if (door.DoorState == EDoorState.Shut && degreesOpen >= openStateAngle)
            {
                if (!TryInteraction(door, EInteractionType.Open))
                    run.nextStateTryTime = Time.time + 0.3f;
            }
            else if (door.DoorState == EDoorState.Open && degreesOpen < 0.05f)
            {
                if (TryInteraction(door, EInteractionType.Close))
                {
                    PlayShut(door, Mathf.Max(Mathf.Abs(run.angularVelocity), hitSpeed));
                    run.angularVelocity = 0f;
                    if (run.driver != null)
                        Pulse(run.driver, 0.5f, 0.05f, 80f);
                }
                else
                    run.nextStateTryTime = Time.time + 0.3f;
            }
        }

        // Tarkov's door interaction (the FIKA packet, the triggers, the state), without its swing or the hands' push / pull clip
        private static bool TryInteraction(Door door, EInteractionType type)
        {
            EFT.Player player = VRGlobals.player;
            // (its door interaction stands the player up from prone)
            if (player == null || player.MovementContext == null || player.MovementContext.IsInPronePose)
                return false;
            player.MovementContext.ResetCanUsePropState();
            Option<InteractionResult> option = Door.Interact(player, type);
            if (!option.Succeeded)
            {
                if (refusalsLogged < 10)
                {
                    refusalsLogged++;
                    Plugin.MyLog.LogWarning($"[Doors] can't {type} '{door.name}' now: {option.Error}");
                }
                return false;
            }
            bool withoutAnimation = door.interactWithoutAnimation;
            door.interactWithoutAnimation = true;
            interactionInProgress = door;
            try
            {
                player.ExecuteInteraction(door, option.Value);
            }
            finally
            {
                interactionInProgress = null;
                door.interactWithoutAnimation = withoutAnimation;
            }
            bool taken = door.DoorState == (type == EInteractionType.Open ? EDoorState.Open : EDoorState.Shut)
                || (type == EInteractionType.Close && door.DoorState == EDoorState.Locked);
            if (!taken && refusalsLogged < 10)
            {
                refusalsLogged++;
                Plugin.MyLog.LogWarning($"[Doors] Tarkov didn't take the {type} of '{door.name}': door {door.DoorState}, "
                    + $"player {player.MovementContext.CurrentState?.Name}");
            }
            return taken;
        }

        private static int refusalsLogged;

        private static void SetAngle(DoorRun run, float angle, float deltaTime)
        {
            float previous = run.door.CurrentAngle;
            float change = Mathf.DeltaAngle(previous, angle);
            if (Mathf.Abs(change) > 1e-4f)
                run.door.CurrentAngle = angle;
            if (deltaTime > 1e-5f)
                run.angularVelocity = Mathf.Lerp(run.angularVelocity, change / deltaTime, 1f - Mathf.Exp(-deltaTime * 25f));
        }

        // Signed degrees from shut to open; openness 0 = shut, 1 = fully open
        private static float Span(Door door) => Mathf.DeltaAngle(door.CloseAngle, door.OpenAngle);

        private static float Openness(Door door, float angle) => Mathf.DeltaAngle(door.CloseAngle, angle) / Span(door);

        private static float AngleAt(Door door, float openness) => door.CloseAngle + Mathf.Clamp01(openness) * Span(door);

        internal static Vector3 HingeAxis(Door door) => WorldInteractiveObject.GetRotationAxis(door.DoorAxis, door.transform.parent).normalized;

        // The palm as the controller has it: the rig hand without HandCollision's clamp (the door is in the way of it)
        private static Vector3 ControllerPoint(Hold hold)
        {
            Transform rig = hold.rig;
            return rig.TransformPoint(hold.palmInRig) - rig.position + HandCollision.RawPosition(rig);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Sound, haptics, co-op

        private static void PlayClip(Door door, AudioClip[] clips, Vector3 position, int rolloff, float volume)
        {
            if (clips == null || clips.Length == 0)
                return;
            AudioClip clip = clips[Random.Range(0, clips.Length)];
            if (clip != null)
                door.PlaySoundAtPoint(position, clip, BetterAudio.AudioSourceGroupType.InteractiveObjects, rolloff, volume);
        }

        // Louder the faster it hits the frame
        private static void PlayShut(Door door, float speed)
        {
            Vector2 range = door.ShutSoundVolumeRange;
            float volume = Mathf.Lerp(Random.Range(range.x, range.y), 0.8f, Mathf.InverseLerp(30f, slamSpeed, speed));
            PlayClip(door, door.ShutSound, door.GetSoundPos(), door._shutSoundRolloff, volume);
        }

        private static void Pulse(Hold hold, float amplitude, float duration, float frequency)
        {
            HapticMixer.PlayPulse(hold.dominant ? Handedness.GunHandSource : Handedness.OffHandSource, amplitude, duration, frequency);
        }

        // FIKA syncs a door's open / shut only: the angle and the handle while a hand moves it, then a last one (reliable)
        private static void Sync(DoorRun run, bool final)
        {
            Door door = run.door;
            if (!InstalledMods.FIKAInstalled || syncBroken || door.ForceLocalInteraction || door.DoorState == EDoorState.Locked
                || string.IsNullOrEmpty(door.Id))
                return;
            float angle = door.CurrentAngle;
            if (final)
            {
                if (run.sent)
                    SendDoor(door.Id, angle, run.turn, true);
                return;
            }
            bool changed = float.IsNaN(run.syncedAngle) || Mathf.Abs(Mathf.DeltaAngle(run.syncedAngle, angle)) > 0.2f
                || Mathf.Abs(run.turn - run.syncedTurn) > 0.02f;
            if (!changed || Time.time - run.lastSyncTime < syncInterval)
                return;
            run.lastSyncTime = Time.time;
            run.syncedAngle = angle;
            run.syncedTurn = run.turn;
            run.sent = true;
            SendDoor(door.Id, angle, run.turn, false);
        }

        private static void SendDoor(string doorId, float angle, float handleTurn, bool final)
        {
            try
            {
                FikaSendDoor(doorId, angle, handleTurn, final);
            }
            catch (System.Exception exception)
            {
                syncBroken = true;
                Plugin.MyLog.LogError($"[Doors] no door sync with this SPT-VR-FikaSync.dll (2.2.0 or newer needed): {exception.Message}");
            }
        }

        // Companion-DLL isolation: the SPT-VR-FikaSync reference is only JITted with FIKA installed
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendDoor(string doorId, float angle, float handleTurn, bool final)
            => SptVrFikaSync.FikaVrSync.SendDoor(doorId, angle, handleTurn, final);

        // Another VR player's door (SPT-VR-FikaSync): ours wins while our hand is on it
        internal static bool DrivesDoor(WorldInteractiveObject door)
        {
            return door is Door ownDoor && runs.TryGetValue(ownDoor, out DoorRun run) && (run.driver != null || run.swinging);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Logs

        private static void LogGrab(Hold hold, DoorRun run)
        {
            Door door = hold.door;
            if (door == null || !loggedDoors.Add(door.Id ?? door.name))
                return;
            string handle = run.handle == null ? "none"
                : run.HasTurnableHandle ? $"'{run.handle.name}' turns {run.handleFullTurn:0.#} deg{(run.handle.pos ? " and slides" : "")}"
                : $"'{run.handle.name}' doesn't turn";
            Plugin.MyLog.LogInfo($"[Doors] {(hold.dominant ? "gun" : "off")} hand on {(hold.onHandle ? "the handle of " : "")}'{door.name}' "
                + $"({door.GetType().Name} {door.Id}): {door.DoorState}, angle {door.CurrentAngle:0.#} (shut {door.CloseAngle:0.#}, open {door.OpenAngle:0.#}), "
                + $"handle {handle}{(hold.turnByTwist ? " (by twist)" : "")}, lock handle {(door.LockHandle != null ? door.LockHandle.name : "none")}, "
                + $"key {(string.IsNullOrEmpty(door.KeyId) ? "none" : "yes")}");
        }

        private static void LogCensus()
        {
            Door[] doors = Object.FindObjectsOfType<Door>();
            int supported = 0, turnable = 0, stiff = 0, locked = 0, keycard = 0;
            foreach (Door door in doors)
            {
                if (!Supports(door))
                    continue;
                supported++;
                if (TurnOf(door._handle, out _, out _))
                    turnable++;
                else if (door._handle != null)
                    stiff++;
                if (door.DoorState == EDoorState.Locked)
                    locked++;
                if (door is KeycardDoor)
                    keycard++;
            }
            Plugin.MyLog.LogInfo($"[Doors] {doors.Length} doors, {supported} by hand: {turnable} with a turnable handle, {stiff} with a handle that "
                + $"doesn't turn, {supported - turnable - stiff} without one; {locked} locked, {keycard} keycard");
            loggedDoors.Clear();
        }
    }
}
