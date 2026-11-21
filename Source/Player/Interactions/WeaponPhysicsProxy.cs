using System.Collections.Generic;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovVR.Source.Player.Interactions
{
    // Real PhysX collision for the gun, in a sandbox. An invisible Rigidbody shaped like the weapon (fat body + thin barrel capsule)
    // is driven toward the controller pose and collided by the engine, in its own local physics scene that holds static copies of
    // the nearby world colliders. Tarkov runs with autoSimulation off, and Physics.Simulate() would step the whole default scene
    // (double-stepping loot and ragdolls), so the proxy steps only its sandbox. The sandbox is tiny, so it can afford fine substeps,
    // which catch a fast rotational swing tunnelling through a thin wall (PhysX CCD ignores rotation).
    // The world it holds: the bullet geometry, terrain and glass, with tree trunks but not branches (CollisionWorld).
    // Doors the hand can move (PhysicalDoors) are hinged bodies here: the gun pushes a free one, and a held / latched / locked one
    // moves with the real door. Copies of unreadable meshes work (nearly all of Tarkov's are unreadable), at a one-time cooking cost.
    public class WeaponPhysicsProxy : MonoBehaviour
    {
        public static float driveGain = 1f;
        public static float maxSpeed = 40f;         // m/s
        public static float maxAngSpeed = 5000f;    // deg/s (also the body's own limit: Tarkov's physics settings clamp every body to 7 rad/s = 400 deg/s)
        public static float bodySplit = 0.5f;       // rod fraction where the fat body capsule ends and the thin barrel begins
        public static float resyncDistance = 3f;    // m, a body this far from the hand is out of sync (bad frame) and is snapped back
        public static float contactMemory = 0.04f;  // s a contact is remembered after it ends (bridges 1-frame gaps; short = no stick on pull-off)

        public static float queryRegion = 1.5f;     // m half-extent of the box that cooks nearby colliders: covers a swing's reach, so the collider is there before the swing
        public static float retainDistance = 3f;    // m, a cooked copy is kept until this far away (hysteresis, so back-and-forth doesn't re-cook)
        public static float colliderRefreshMove = 0.25f;
        public static float colliderRefreshInterval = 0.3f;

        public static float substepMuzzleDist = 0.015f;  // m the muzzle moves at most per sandbox substep: must be < the thinnest wall or a fast rotation tunnels
        public static int maxSimSubsteps = 40;

        public static float contactLeadMargin = 0.03f;   // m added to gun radius + this frame's travel: geometry that near makes the drive take over
        // Solver iterations for the proxy (Tarkov's physics settings give every body 3 / 1, Unity's default is 6 / 1). A gun resting on
        // a ledge, pressed into a corner or pivoting on an edge holds steadier with more. It's one small body, so the cost is negligible.
        public static int solverIterations = 12;
        public static int solverVelocityIterations = 4;
        // The hold has a limited strength. The drive follows the hand's own move this frame in full (x holdFollow), but an existing gap
        // between gun and hand (something stopped the gun and the hand went on) closes by at most holdGap / holdTurnGap per frame. So a
        // gun pushed past something leans on it instead of being driven at it at gap-per-frame speed (a hand 40 cm past two contacts
        // would be ~36 m/s, and the solve flipping between them shakes the gun). Once free, it glides back to the hand.
        // Big values = a rigid hold, small = a weak hold.
        public static float holdGap = 0.02f;             // m a frame
        public static float holdTurnGap = 3f;            // deg a frame
        public static float holdFollow = 1.25f;
        // Logs a line each time the gun ends up inside something solid in the real world (a pass-through): what, how deep, whether
        // the sandbox had it, how fast the gun was moving and turning, and the substeps.
        public static bool passThroughLog = false;

        public static float proxyMass = 10f;             // kg; only matters against a door body: heavier = the gun shoves doors more easily
        public static float doorMass = 1f;               // kg of a door in the sandbox
        public static float doorJumpDegrees = 10f;       // deg, a held / latched door that moved more than this since its last simulation is placed, not swept
        public static float worldClearDelay = 5f;        // s out of a raid before the copies go (they hold the map's meshes)

        private Rigidbody rb;
        private CapsuleCollider bodyCol;
        private CapsuleCollider barrelCol;
        private bool active;
        private Vector3 targetPos;
        private Quaternion targetRot = Quaternion.identity;
        private Vector3 previousTargetPos;              // last frame's target, for the hand's own move this frame
        private Quaternion previousTargetRot = Quaternion.identity;
        private bool hasPreviousTarget;
        private float builtLen = -1f, builtBodyR = -1f, builtBarrelR = -1f;
        private float lastContactTime = -10f;

        // --- private physics sandbox ---
        private Scene sandbox;
        private PhysicsScene sandboxPhysics;
        private int queryMask;
        private int staticCopyLayer;                 // a layer apart from the proxy's, so door bodies can leave the static copies alone
        private bool separateStaticLayer;
        private readonly Dictionary<int, GameObject> colliderCopies = new Dictionary<int, GameObject>(256);
        private readonly Collider[] overlapBuf = new Collider[512];
        private readonly List<int> staleKeys = new List<int>(128);
        private readonly HashSet<int> ignoredColliders = new HashSet<int>();   // e.g. broken glass - never (re)copy these into the sandbox
        private Vector3 lastQueryCenter = new Vector3(1e9f, 1e9f, 1e9f);
        private float nextQueryTime;
        private readonly Collider[] proximityBuf = new Collider[128];
        private float outOfRaidSince = -1f;

        // What the gun collides with, per world collider (cached: a collider's kind doesn't change)
        private enum ColliderUse : byte { Ignore, Static, Door }
        private readonly Dictionary<int, ColliderUse> colliderUses = new Dictionary<int, ColliderUse>(1024);
        private readonly Dictionary<int, Door> colliderDoors = new Dictionary<int, Door>();
        private float colliderUsesClearTime;

        // A door in the sandbox: one body holding copies of its colliders, rotating about the door's own origin (its hinge)
        private sealed class DoorBody
        {
            public Door door;
            public GameObject root;
            public Rigidbody body;
            public readonly Dictionary<int, GameObject> parts = new Dictionary<int, GameObject>();
            public bool pushable;                    // dynamic this frame: the gun pushes it
            public bool pinned;                      // pushed against its stop: kinematic for the rest of the frame
            public float angle;                      // this frame's simulated angle (CurrentAngle units)
            public float angularSpeed;               // deg/s about the hinge
            public float hitSpeed;                   // deg/s it hit a stop at this frame (the shut sound)
            public float writtenAngle = float.NaN;   // the real door's angle we left it at
            public int lastSimulatedFrame = -10;
            public Quaternion sweepFrom;             // a held / latched door: where this frame's substeps take it from and to
            public Quaternion sweepTo;
        }
        private readonly Dictionary<int, DoorBody> doorBodies = new Dictionary<int, DoorBody>();
        private readonly Dictionary<int, DoorBody> doorPartOwners = new Dictionary<int, DoorBody>();
        private readonly List<int> staleDoors = new List<int>();

        // Our own player's colliders (the body, the guns on it, the gun in hands) and any weapon model are never world geometry here:
        // a freshly drawn gun's own colliders, copied in as world, would push the holder off the hand. Cached by instance id; the cache
        // is cleared every few seconds, because a pooled gun model can come back as loot.
        private readonly Dictionary<int, bool> ownColliders = new Dictionary<int, bool>();
        private float ownCollidersClearTime;

        private bool IsOwnCollider(Collider collider)
        {
            if (Time.time > ownCollidersClearTime)
            {
                ownColliders.Clear();
                ownCollidersClearTime = Time.time + 2f;
            }
            int id = collider.GetInstanceID();
            if (ownColliders.TryGetValue(id, out bool own))
                return own;
            EFT.Player owner = collider.GetComponentInParent<EFT.Player>(true);
            own = (owner != null && owner.IsYourPlayer) || collider.GetComponentInParent<WeaponPrefab>(true) != null;
            ownColliders[id] = own;
            return own;
        }

        public bool RecentlyContacted => active && Time.time - lastContactTime < contactMemory;
        public int LastSubsteps { get; private set; }
        public bool Active => active;
        public Vector3 ResolvedPos => active ? rb.position : targetPos;
        public Quaternion ResolvedRot => active ? rb.rotation : targetRot;

        public static WeaponPhysicsProxy Create(int worldMask)
        {
            GameObject proxyObject = new GameObject("WeaponPhysicsProxy");
            var proxy = proxyObject.AddComponent<WeaponPhysicsProxy>();
            int doorLayer = LayerMask.NameToLayer("DoorLowPolyCollider");
            // Also the movement layer (tree trunks, see CollisionWorld) and the doors' own colliders
            proxy.queryMask = CollisionWorld.Mask(worldMask) | (doorLayer >= 0 ? 1 << doorLayer : 0);

            proxy.rb = proxyObject.AddComponent<Rigidbody>();
            proxy.rb.useGravity = false;
            proxy.rb.drag = 0f;
            proxy.rb.angularDrag = 0f;
            proxy.rb.mass = proxyMass;
            proxy.rb.interpolation = RigidbodyInterpolation.None;
            proxy.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            proxy.rb.maxAngularVelocity = maxAngSpeed * Mathf.Deg2Rad;
            proxy.rb.solverIterations = Mathf.Max(solverIterations, 1);
            proxy.rb.solverVelocityIterations = Mathf.Max(solverVelocityIterations, 1);

            // Frictionless (Minimum combine -> every contact is frictionless whatever the wall's material) so the gun
            // slides / pivots along a surface instead of gripping it. Zero bounce so it doesn't spring off.
            var slick = new PhysicMaterial("WeaponCollisionSlick")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 0f,
                frictionCombine = PhysicMaterialCombine.Minimum,
                bounceCombine = PhysicMaterialCombine.Minimum,
            };
            proxy.bodyCol = proxyObject.AddComponent<CapsuleCollider>();
            proxy.bodyCol.direction = 1;   // local Y = the barrel axis
            proxy.bodyCol.material = slick;
            proxy.barrelCol = proxyObject.AddComponent<CapsuleCollider>();
            proxy.barrelCol.direction = 1;
            proxy.barrelCol.material = slick;

            proxy.sandbox = SceneManager.CreateScene("SPTVR_WeaponPhysics",
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            SceneManager.MoveGameObjectToScene(proxyObject, proxy.sandbox);
            proxy.sandboxPhysics = proxy.sandbox.GetPhysicsScene();

            proxy.SetActive(false);
            return proxy;
        }

        // Sets the proxy's layer (its copies collide with it). Static copies go on a menu-only layer of their own (nothing in a raid is
        // on it) that the proxy includes and the door bodies exclude; otherwise a door body would jam against its own frame.
        public void UseLayer(int proxyLayer)
        {
            gameObject.layer = proxyLayer;
            int menuLayer = LayerMask.NameToLayer("Menu Environment");
            separateStaticLayer = menuLayer >= 0 && menuLayer != proxyLayer;
            staticCopyLayer = separateStaticLayer ? menuLayer : proxyLayer;
            rb.includeLayers = separateStaticLayer ? 1 << staticCopyLayer : 0;
        }

        public void Configure(float weaponLn, float bodyR, float barrelR)
        {
            if (Mathf.Abs(weaponLn - builtLen) < 0.005f &&
                Mathf.Approximately(bodyR, builtBodyR) && Mathf.Approximately(barrelR, builtBarrelR))
                return;
            builtLen = weaponLn;
            builtBodyR = bodyR;
            builtBarrelR = barrelR;

            float bodyLen = bodySplit * weaponLn;
            bodyCol.radius = bodyR;
            bodyCol.height = bodyLen + bodyR * 2f;
            bodyCol.center = new Vector3(0f, -bodyLen * 0.5f, 0f);

            float barrelLen = (1f - bodySplit) * weaponLn;
            barrelCol.radius = barrelR;
            barrelCol.height = barrelLen + barrelR * 2f;
            barrelCol.center = new Vector3(0f, -(bodySplit * weaponLn + barrelLen * 0.5f), 0f);

            // The body turns about the grip, which is the point the drive places. Turning about Unity's computed centre (out along the
            // barrel) would move the grip off its target on every turn, until the next frame.
            rb.centerOfMass = Vector3.zero;
            rb.mass = proxyMass;
        }

        public void SetTarget(Vector3 pos, Quaternion rot)
        {
            if (!active || (pos - rb.position).sqrMagnitude > resyncDistance * resyncDistance)
            {
                SetActive(true);
                rb.position = pos;
                rb.rotation = rot;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                hasPreviousTarget = false;
            }
            if (hasPreviousTarget)
            {
                previousTargetPos = targetPos;
                previousTargetRot = targetRot;
            }
            else
            {
                previousTargetPos = pos;
                previousTargetRot = rot;
                hasPreviousTarget = true;
            }
            targetPos = pos;
            targetRot = rot;
        }

        public void Deactivate() => SetActive(false);

        private void SetActive(bool on)
        {
            if (active == on)
                return;
            active = on;
            rb.isKinematic = !on;
            bodyCol.enabled = on;
            barrelCol.enabled = on;
            hasPreviousTarget = false;
            if (!on)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        // Step the sandbox at render rate (from WeaponCollision.Apply, in Update), not in FixedUpdate. rb.position is then
        // current this frame, so:
        //  - no rb.interpolation is needed (at 50 Hz it causes jitter while walking and adds lag);
        //  - the proxy collides the same frame the hand does, so the visible gun stops at the wall instead of clipping through;
        //  - in free space the gain-1 drive reaches the hand exactly each frame: the gun sits at the true controller pose.
        public void Step(float dt)
        {
            if (!active || rb == null || rb.isKinematic)
                return;
            dt = Mathf.Clamp(dt, 1e-4f, 0.05f);   // bound a frame hitch so a huge dt can't outrun the substep budget

            RefreshNearbyColliders();

            // Fast path in free space: with no world geometry within reach of the whole gun this frame, snap the proxy straight to the
            // controller pose (no lag, no speed cap; the target already includes locomotion, turning and the hand). The speed-capped
            // velocity drive below only runs when something is near enough to collide with. The reach is the gun's grip-to-muzzle
            // capsule plus this frame's travel and a margin, so the drive takes over before the muzzle can reach a wall.
            float length = Mathf.Max(builtLen, 0f);
            Vector3 muzzle = rb.position + rb.rotation * new Vector3(0f, -length, 0f);
            float muzzleTravel = (targetPos - rb.position).magnitude
                               + Quaternion.Angle(rb.rotation, targetRot) * Mathf.Deg2Rad * length;
            float reachRadius = contactLeadMargin + Mathf.Max(builtBodyR, builtBarrelR) + muzzleTravel;
            int found = Physics.OverlapCapsuleNonAlloc(rb.position, muzzle, reachRadius, proximityBuf,
                                                      queryMask, QueryTriggerInteraction.Collide);
            // A full buffer may have missed something solid, so it counts as near
            bool near = found >= proximityBuf.Length;
            for (int i = 0; i < found && !near; i++)
            {
                Collider collider = proximityBuf[i];
                if (collider != null && !IsOwnCollider(collider) && UseOf(collider, out _) != ColliderUse.Ignore)
                    near = true;
            }
            // How far the hold reaches this frame: the hand's own move, and a little of any gap left
            float reach = holdGap + (targetPos - previousTargetPos).magnitude * holdFollow;
            float turnReach = holdTurnGap + Quaternion.Angle(previousTargetRot, targetRot) * holdFollow;
            bool caughtUp = (targetPos - rb.position).sqrMagnitude <= reach * reach && Quaternion.Angle(rb.rotation, targetRot) <= turnReach;
            // Behind the hand in open space (just freed from a wall), it glides back under the drive instead of jumping
            if (!near && caughtUp)
            {
                LastSubsteps = 0;
                rb.position = targetPos;
                rb.rotation = targetRot;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                // A log line with 0 substeps means the snap put it there: the reach test missed what it went into
                if (passThroughLog)
                    ProbePassThrough(dt, 0);
                return;
            }

            Vector3 aimPos = rb.position + Vector3.ClampMagnitude(targetPos - rb.position, reach);
            Quaternion aimRot = Quaternion.RotateTowards(rb.rotation, targetRot, turnReach);
            Vector3 driveVelocity = (aimPos - rb.position) / dt * driveGain;
            float driveSpeed = driveVelocity.magnitude;
            if (driveSpeed > maxSpeed)
                driveVelocity *= maxSpeed / driveSpeed;
            rb.velocity = driveVelocity;

            // Tarkov's default angular speed limit (7 rad/s) would brake a swing as it nears a wall, the moment this drive takes over
            rb.maxAngularVelocity = Mathf.Max(maxAngSpeed * Mathf.Deg2Rad, 7f);
            rb.solverIterations = Mathf.Max(solverIterations, 1);
            rb.solverVelocityIterations = Mathf.Max(solverVelocityIterations, 1);
            Quaternion rotationDelta = aimRot * Quaternion.Inverse(rb.rotation);
            rotationDelta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f)
                angle -= 360f;
            if (Mathf.Abs(angle) > 1e-3f && axis.sqrMagnitude > 1e-8f && !float.IsInfinity(axis.x))
            {
                Vector3 w = axis.normalized * (angle * Mathf.Deg2Rad / dt) * driveGain;
                float angularSpeed = w.magnitude, wmax = maxAngSpeed * Mathf.Deg2Rad;
                if (angularSpeed > wmax)
                    w *= wmax / angularSpeed;
                rb.angularVelocity = w;
            }
            else
            {
                rb.angularVelocity = Vector3.zero;
            }

            // Step only the sandbox (game physics untouched), substepped fine enough that a fast rotational swing can't skip
            // through a thin wall. Velocity is set once above; contact response carries between substeps.
            float muzzleSpeed = rb.velocity.magnitude + rb.angularVelocity.magnitude * builtLen;
            int steps = Mathf.Clamp(Mathf.CeilToInt(muzzleSpeed * dt / substepMuzzleDist), 1, maxSimSubsteps);
            LastSubsteps = steps;
            float substep = dt / steps;
            PrepareDoorBodies();
            for (int i = 0; i < steps; i++)
            {
                SweepHeldDoors(i, steps);
                sandboxPhysics.Simulate(substep);
                KeepDoorsOnHinges();
            }
            ReportDoorMotion();
            if (passThroughLog)
                ProbePassThrough(dt, steps);
        }

        // Pass-through check: the proxy's shapes at their resolved pose against the real world. Only more than 1 cm counts, since a shape
        // a few mm in is just pressed against the surface. One line per pass-through, for its deepest frame, logged when it ends.
        private readonly Collider[] insideBuf = new Collider[32];
        private int insideFrames;
        private float insideDeepest;
        private string insideReport;

        private void ProbePassThrough(float dt, int steps)
        {
            float deepest = 0f;
            Collider deepestCollider = null;
            for (int part = 0; part < 2; part++)
            {
                CapsuleCollider shape = part == 0 ? bodyCol : barrelCol;
                Vector3 center = rb.position + rb.rotation * shape.center;
                Vector3 half = rb.rotation * Vector3.up * Mathf.Max(shape.height * 0.5f - shape.radius, 0f);
                int found = Physics.OverlapCapsuleNonAlloc(center - half, center + half, shape.radius, insideBuf, queryMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < found; i++)
                {
                    Collider collider = insideBuf[i];
                    if (collider == null || IsOwnCollider(collider) || UseOf(collider, out _) == ColliderUse.Ignore)
                        continue;
                    if (!Physics.ComputePenetration(shape, rb.position, rb.rotation, collider, collider.transform.position, collider.transform.rotation, out _, out float depth))
                        continue;
                    if (depth > deepest)
                    {
                        deepest = depth;
                        deepestCollider = collider;
                    }
                }
            }
            if (deepest > 0.01f)
            {
                insideFrames++;
                if (deepest > insideDeepest)
                {
                    insideDeepest = deepest;
                    int id = deepestCollider.GetInstanceID();
                    bool copied = colliderCopies.ContainsKey(id) || doorPartOwners.ContainsKey(id);
                    string path = deepestCollider.name;
                    Transform parent = deepestCollider.transform.parent;
                    for (int depth = 0; depth < 3 && parent != null; depth++, parent = parent.parent)
                        path = parent.name + "/" + path;
                    insideReport = $"{path} ({LayerMask.LayerToName(deepestCollider.gameObject.layer)}, {deepestCollider.GetType().Name}), "
                        + $"{(copied ? "in the sandbox" : "NOT in the sandbox")}, {deepest * 100f:0.0} cm in, moving {rb.velocity.magnitude:0.0} m/s "
                        + $"turning {rb.angularVelocity.magnitude * Mathf.Rad2Deg:0} deg/s, {steps} substeps, dt {dt * 1000f:0.0} ms, "
                        + $"the hand {(targetPos - rb.position).magnitude * 100f:0.0} cm / {Quaternion.Angle(rb.rotation, targetRot):0} deg off";
                }
                return;
            }
            if (insideFrames > 0)
                Plugin.MyLog.LogInfo($"[WeaponCollision] inside something for {insideFrames} frames, deepest: {insideReport}");
            insideFrames = 0;
            insideDeepest = 0f;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The world in the sandbox

        private ColliderUse UseOf(Collider collider, out Door door)
        {
            door = null;
            if (Time.time > colliderUsesClearTime)
            {
                colliderUses.Clear();
                colliderDoors.Clear();
                colliderUsesClearTime = Time.time + 10f;
            }
            int id = collider.GetInstanceID();
            if (!colliderUses.TryGetValue(id, out ColliderUse use))
            {
                use = Classify(collider, out door);
                colliderUses[id] = use;
                if (use == ColliderUse.Door)
                    colliderDoors[id] = door;
                return use;
            }
            if (use == ColliderUse.Door && (!colliderDoors.TryGetValue(id, out door) || door == null))
                return ColliderUse.Static;
            return use;
        }

        private ColliderUse Classify(Collider collider, out Door door)
        {
            door = null;
            // Branches, triggers, window blockers
            if (!CollisionWorld.Solid(collider))
                return ColliderUse.Ignore;
            Door owner = collider.GetComponentInParent<Door>();
            if (owner != null && PhysicalDoors.Supports(owner))
            {
                door = owner;
                return ColliderUse.Door;
            }
            return ColliderUse.Static;
        }

        // Cooks every world collider within queryRegion (a box around the gun) into the sandbox.
        // Cached by instance id, so each cooks once; kept until retainDistance away (hysteresis).
        private void RefreshNearbyColliders()
        {
            if (builtLen <= 0f)
                return;
            if ((rb.position - lastQueryCenter).sqrMagnitude < colliderRefreshMove * colliderRefreshMove &&
                Time.time < nextQueryTime)
                return;
            lastQueryCenter = rb.position;
            nextQueryTime = Time.time + colliderRefreshInterval;

            int overlapCount = Physics.OverlapBoxNonAlloc(rb.position, Vector3.one * queryRegion, overlapBuf,
                Quaternion.identity, queryMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < overlapCount; i++)
            {
                Collider source = overlapBuf[i];
                if (source == null)
                    continue;
                int id = source.GetInstanceID();
                if (ignoredColliders.Contains(id) || IsOwnCollider(source))
                    continue;
                ColliderUse use = UseOf(source, out Door door);
                if (use == ColliderUse.Ignore)
                    continue;
                if (use == ColliderUse.Door)
                {
                    AddDoorPart(source, door);
                    continue;
                }
                // A pooled window piece reused in another window has a different mesh, so it is cooked again
                if (colliderCopies.TryGetValue(id, out GameObject existing) && existing != null && source is MeshCollider sourceMesh
                    && existing.GetComponent<MeshCollider>() is MeshCollider copyMesh && copyMesh.sharedMesh != sourceMesh.sharedMesh)
                {
                    DropCopy(id);
                    existing = null;
                }
                if (existing != null)
                    existing.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                else
                {
                    GameObject copy = CopyCollider(source, staticCopyLayer);
                    if (copy != null)
                        colliderCopies[id] = copy;
                }
            }

            staleKeys.Clear();
            float retainDistanceSquared = retainDistance * retainDistance;
            foreach (var entry in colliderCopies)
            {
                GameObject copyObject = entry.Value;
                if (copyObject == null)
                {
                    staleKeys.Add(entry.Key);
                    continue;
                }
                Collider copyCollider = copyObject.GetComponent<Collider>();
                float distanceSquared = copyCollider != null ? copyCollider.bounds.SqrDistance(rb.position)
                                       : (copyObject.transform.position - rb.position).sqrMagnitude;
                // Far off, or its real collider is gone or off (glass shot out, a window piece fallen)
                Collider source = copyObject.GetComponent<ProxyColliderSource>()?.source;
                if (distanceSquared > retainDistanceSquared || source == null || !source.enabled || !source.gameObject.activeInHierarchy)
                    staleKeys.Add(entry.Key);
            }
            for (int i = 0; i < staleKeys.Count; i++)
            {
                if (colliderCopies.TryGetValue(staleKeys[i], out GameObject go) && go != null)
                    Destroy(go);
                colliderCopies.Remove(staleKeys[i]);
            }

            // Drop doors that are far off. The distance is measured to the hinge, so a door's width is added to the limit.
            staleDoors.Clear();
            float doorRetain = retainDistance + 2f;
            foreach (var entry in doorBodies)
            {
                Door door = entry.Value.door;
                if (door == null || entry.Value.root == null || (door.transform.position - rb.position).sqrMagnitude > doorRetain * doorRetain)
                    staleDoors.Add(entry.Key);
            }
            for (int i = 0; i < staleDoors.Count; i++)
                RemoveDoorBody(staleDoors[i]);
        }

        // A static clone of a world collider inside the sandbox (same shape/mesh/pose/scale; the layer the proxy collides with).
        // Carries the source collider for feedback (glass break / surface sound).
        private GameObject CopyCollider(Collider source, int layer)
        {
            GameObject copy = new GameObject("wc_copy") { layer = layer };
            SceneManager.MoveGameObjectToScene(copy, sandbox);
            Transform sourceTransform = source.transform;
            copy.transform.SetPositionAndRotation(sourceTransform.position, sourceTransform.rotation);
            copy.transform.localScale = sourceTransform.lossyScale;

            bool made = false;
            switch (source)
            {
                case MeshCollider meshCollider when meshCollider.sharedMesh != null:
                    var meshCopy = copy.AddComponent<MeshCollider>();
                    // The same cooking options as the original reuse its cooked mesh (an unreadable one can't be cooked again)
                    meshCopy.cookingOptions = meshCollider.cookingOptions;
                    meshCopy.convex = meshCollider.convex;
                    meshCopy.sharedMesh = meshCollider.sharedMesh;
                    made = true;
                    break;
                case BoxCollider boxCollider:
                    var boxCopy = copy.AddComponent<BoxCollider>();
                    boxCopy.center = boxCollider.center; boxCopy.size = boxCollider.size;
                    made = true;
                    break;
                case SphereCollider sphereCollider:
                    var sphereCopy = copy.AddComponent<SphereCollider>();
                    sphereCopy.center = sphereCollider.center; sphereCopy.radius = sphereCollider.radius;
                    made = true;
                    break;
                case CapsuleCollider capsuleCollider:
                    var capsuleCopy = copy.AddComponent<CapsuleCollider>();
                    capsuleCopy.center = capsuleCollider.center; capsuleCopy.radius = capsuleCollider.radius; capsuleCopy.height = capsuleCollider.height; capsuleCopy.direction = capsuleCollider.direction;
                    made = true;
                    break;
                case TerrainCollider terrainCollider when terrainCollider.terrainData != null:
                    var terrainCopy = copy.AddComponent<TerrainCollider>();
                    terrainCopy.terrainData = terrainCollider.terrainData;
                    made = true;
                    break;
            }
            if (!made)
            {
                Destroy(copy);
                return null;
            }
            copy.AddComponent<ProxyColliderSource>().source = source;
            return copy;
        }

        // Drop a world collider from the sandbox and never re-copy it (broken glass): the real pane's collider is gone after
        // the break, but our static copy would stay until out of retainDistance and the gun would keep hitting it.
        // Blacklisted so a throttled refresh can't re-copy it before the real collider is actually destroyed.
        public void ForgetCollider(int instanceId)
        {
            ignoredColliders.Add(instanceId);
            GameObject copy = null;
            if (colliderCopies.TryGetValue(instanceId, out copy))
                colliderCopies.Remove(instanceId);
            else if (doorPartOwners.TryGetValue(instanceId, out DoorBody doorBody))
            {
                doorBody.parts.TryGetValue(instanceId, out copy);
                doorBody.parts.Remove(instanceId);
                doorPartOwners.Remove(instanceId);
            }
            if (copy != null)
            {
                Collider copyCollider = copy.GetComponent<Collider>();
                if (copyCollider != null)
                    copyCollider.enabled = false;   // immediate: stop colliding this frame too (Destroy is deferred to end of frame)
                Destroy(copy);
            }
        }

        // The pieces a hit knocked out of a broken window (their colliders are off now): their copies go at once. Not blacklisted:
        // Tarkov pools the pieces and puts them in other windows later.
        private readonly List<Collider> windowColliders = new List<Collider>(64);
        public void DropDisabledCopiesUnder(Transform window)
        {
            window.GetComponentsInChildren(true, windowColliders);
            for (int i = 0; i < windowColliders.Count; i++)
            {
                Collider source = windowColliders[i];
                if (source != null && (!source.enabled || !source.gameObject.activeInHierarchy))
                    DropCopy(source.GetInstanceID());
            }
            windowColliders.Clear();
        }

        private void DropCopy(int instanceId)
        {
            if (!colliderCopies.TryGetValue(instanceId, out GameObject copy))
                return;
            colliderCopies.Remove(instanceId);
            if (copy == null)
                return;
            Collider copyCollider = copy.GetComponent<Collider>();
            if (copyCollider != null)
                copyCollider.enabled = false;   // stop colliding this frame too (Destroy is deferred to end of frame)
            Destroy(copy);
        }

        // Out of a raid for a while: the copies go (they hold the map's meshes, which would stay loaded past the raid)
        private void Update()
        {
            if (VRGlobals.inGame)
            {
                outOfRaidSince = -1f;
                return;
            }
            if (outOfRaidSince < 0f)
                outOfRaidSince = Time.time;
            if (Time.time - outOfRaidSince > worldClearDelay && (colliderCopies.Count > 0 || doorBodies.Count > 0))
                ClearWorld();
        }

        private void ClearWorld()
        {
            foreach (GameObject copy in colliderCopies.Values)
                if (copy != null)
                    Destroy(copy);
            colliderCopies.Clear();
            foreach (DoorBody doorBody in doorBodies.Values)
                if (doorBody.root != null)
                    Destroy(doorBody.root);
            doorBodies.Clear();
            doorPartOwners.Clear();
            colliderUses.Clear();
            colliderDoors.Clear();
            ownColliders.Clear();
            ignoredColliders.Clear();
            CollisionWorld.Clear();
            lastQueryCenter = new Vector3(1e9f, 1e9f, 1e9f);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Doors

        private void AddDoorPart(Collider source, Door door)
        {
            int doorId = door.GetInstanceID();
            if (!doorBodies.TryGetValue(doorId, out DoorBody doorBody) || doorBody.root == null)
            {
                GameObject root = new GameObject("wc_door") { layer = gameObject.layer };
                SceneManager.MoveGameObjectToScene(root, sandbox);
                root.transform.SetPositionAndRotation(door.transform.position, door.transform.rotation);
                Rigidbody body = root.AddComponent<Rigidbody>();
                body.useGravity = false;
                body.isKinematic = true;
                body.mass = doorMass;
                body.drag = 0f;
                body.angularDrag = 0f;
                body.interpolation = RigidbodyInterpolation.None;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                if (separateStaticLayer)
                    body.excludeLayers = 1 << staticCopyLayer;
                doorBody = new DoorBody { door = door, root = root, body = body };
                doorBodies[doorId] = doorBody;
            }
            int id = source.GetInstanceID();
            if (doorBody.parts.ContainsKey(id))
                return;
            // Parts sit where they are on the door as it is now
            doorBody.root.transform.SetPositionAndRotation(door.transform.position, door.transform.rotation);
            GameObject part = CopyDoorPart(source, gameObject.layer);
            if (part == null)
                return;
            part.transform.SetParent(doorBody.root.transform, worldPositionStays: true);
            // Not against the other doors (double doors meet in the middle)
            Collider partCollider = part.GetComponent<Collider>();
            foreach (DoorBody other in doorBodies.Values)
            {
                if (other == doorBody)
                    continue;
                foreach (GameObject otherPart in other.parts.Values)
                {
                    Collider otherCollider = otherPart != null ? otherPart.GetComponent<Collider>() : null;
                    if (otherCollider != null)
                        Physics.IgnoreCollision(partCollider, otherCollider, true);
                }
            }
            doorBody.parts[id] = part;
            doorPartOwners[id] = doorBody;
            // It turns about its hinge (the door's origin)
            doorBody.body.centerOfMass = Vector3.zero;
            doorBody.body.mass = doorMass;
        }

        // A piece of a door for its body. A mesh becomes the box of its bounds: Unity drops a non-convex mesh from a body that can
        // move (a pushable door), and an unreadable one can't be cooked convex. Doors are slabs, so the box fits.
        private GameObject CopyDoorPart(Collider source, int layer)
        {
            if (!(source is MeshCollider meshCollider))
                return source is TerrainCollider ? null : CopyCollider(source, layer);
            if (meshCollider.sharedMesh == null)
                return null;
            Bounds bounds = meshCollider.sharedMesh.bounds;
            Transform sourceTransform = source.transform;
            GameObject part = new GameObject("wc_door_part") { layer = layer };
            SceneManager.MoveGameObjectToScene(part, sandbox);
            // A mirrored door's negative scale gives the same box about its centre, so the absolute scale is used
            Vector3 scale = sourceTransform.lossyScale;
            part.transform.SetPositionAndRotation(sourceTransform.TransformPoint(bounds.center), sourceTransform.rotation);
            part.transform.localScale = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            BoxCollider box = part.AddComponent<BoxCollider>();
            box.size = bounds.size;
            part.AddComponent<ProxyColliderSource>().source = source;
            return part;
        }

        private void RemoveDoorBody(int doorId)
        {
            if (!doorBodies.TryGetValue(doorId, out DoorBody doorBody))
                return;
            foreach (int partId in doorBody.parts.Keys)
                doorPartOwners.Remove(partId);
            if (doorBody.root != null)
                Destroy(doorBody.root);
            doorBodies.Remove(doorId);
        }

        // Before this frame's substeps: a door the gun may push becomes a free body, started from the real door's pose and swing speed.
        // Any other door follows the real one, swept through the substeps so the gun meets a moving door.
        private void PrepareDoorBodies()
        {
            int frame = Time.frameCount;
            foreach (DoorBody doorBody in doorBodies.Values)
            {
                Door door = doorBody.door;
                if (door == null || doorBody.root == null)
                    continue;
                Transform doorTransform = door.transform;
                Rigidbody body = doorBody.body;
                bool simulatedLastFrame = doorBody.lastSimulatedFrame == frame - 1;
                doorBody.lastSimulatedFrame = frame;
                doorBody.angle = door.CurrentAngle;
                doorBody.hitSpeed = 0f;
                bool pushable = PhysicalDoors.MayBePushed(door);
                if (pushable)
                {
                    // Start from the real door if something else moved it since (the hand, its swing, another player) or it's newly free
                    bool movedElsewhere = float.IsNaN(doorBody.writtenAngle)
                        || Mathf.Abs(Mathf.DeltaAngle(doorBody.writtenAngle, door.CurrentAngle)) > 0.01f;
                    if (!doorBody.pushable || doorBody.pinned || movedElsewhere || !simulatedLastFrame)
                    {
                        body.isKinematic = false;
                        body.position = doorTransform.position;
                        body.rotation = doorTransform.rotation;
                        body.velocity = Vector3.zero;
                        body.angularVelocity = PhysicalDoors.HingeAxis(door) * (PhysicalDoors.AngularVelocityOf(door) * Mathf.Deg2Rad);
                    }
                    doorBody.angularSpeed = PhysicalDoors.AngularVelocityOf(door);
                }
                else
                {
                    if (!body.isKinematic)
                        body.isKinematic = true;
                    Quaternion target = doorTransform.rotation;
                    if (!simulatedLastFrame || Quaternion.Angle(body.rotation, target) > doorJumpDegrees)
                    {
                        body.position = doorTransform.position;
                        body.rotation = target;
                    }
                    doorBody.sweepFrom = body.rotation;
                    doorBody.sweepTo = target;
                    doorBody.writtenAngle = float.NaN;
                }
                doorBody.pushable = pushable;
                doorBody.pinned = false;
            }
        }

        private void SweepHeldDoors(int step, int steps)
        {
            foreach (DoorBody doorBody in doorBodies.Values)
            {
                if (doorBody.door == null || doorBody.root == null || doorBody.pushable)
                    continue;
                doorBody.body.MoveRotation(Quaternion.Slerp(doorBody.sweepFrom, doorBody.sweepTo, (step + 1f) / steps));
            }
        }

        // After each substep, keeps a pushed door on its hinge and within its range. Pushed against a stop, it is held there (kinematic
        // for the rest of the frame); otherwise the gun would drive the light door through its stop every substep.
        private void KeepDoorsOnHinges()
        {
            foreach (DoorBody doorBody in doorBodies.Values)
            {
                Door door = doorBody.door;
                if (door == null || doorBody.root == null || !doorBody.pushable || doorBody.pinned)
                    continue;
                Transform doorTransform = door.transform;
                Rigidbody body = doorBody.body;
                Vector3 axis = PhysicalDoors.HingeAxis(door);
                Quaternion parentRotation = doorTransform.parent.rotation;
                float twist = PhysicalDoors.TwistDegrees(body.rotation * Quaternion.Inverse(parentRotation), axis);
                float angle = doorBody.angle + Mathf.DeltaAngle(doorBody.angle, twist);
                float speed = Vector3.Dot(body.angularVelocity, axis) * Mathf.Rad2Deg;
                float unclampedSpeed = speed;
                angle = PhysicalDoors.ClampToRange(door, doorBody.angle, angle, ref speed, out bool atStop);
                body.position = doorTransform.position;
                body.rotation = Quaternion.AngleAxis(angle, axis) * parentRotation;
                body.velocity = Vector3.zero;
                body.angularVelocity = axis * (speed * Mathf.Deg2Rad);
                doorBody.angle = angle;
                doorBody.angularSpeed = speed;
                if (atStop && speed != unclampedSpeed)
                {
                    doorBody.hitSpeed = Mathf.Max(doorBody.hitSpeed, Mathf.Abs(unclampedSpeed));
                    doorBody.pinned = true;
                    body.isKinematic = true;
                }
            }
        }

        // Passes the pushed doors' new angles to the real doors (PhysicalDoors handles the state, the sounds and the co-op sync)
        private void ReportDoorMotion()
        {
            foreach (DoorBody doorBody in doorBodies.Values)
            {
                Door door = doorBody.door;
                if (door == null || doorBody.root == null || !doorBody.pushable)
                    continue;
                bool moved = Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, doorBody.angle)) > 1e-3f
                    || Mathf.Abs(doorBody.angularSpeed) > 0.5f || doorBody.hitSpeed > 0f;
                if (moved)
                    PhysicalDoors.Pushed(door, doorBody.angle, doorBody.angularSpeed, doorBody.hitSpeed);
                // If the push was refused (the door was taken meanwhile), the next frame starts from the real door
                doorBody.writtenAngle = Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, doorBody.angle)) < 0.01f ? door.CurrentAngle : float.NaN;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private void OnCollisionEnter(Collision c)
        {
            if (!active || c.contactCount == 0)
                return;
            lastContactTime = Time.time;
            ContactPoint contact = c.GetContact(0);
            Collider source = contact.otherCollider != null ? contact.otherCollider.GetComponent<ProxyColliderSource>()?.source : null;
            if (source != null)
                WeaponCollision.OnProxyContact(source, contact.point, contact.normal);
        }

        private void OnCollisionStay(Collision c)
        {
            if (active && c.contactCount > 0)
            {
                lastContactTime = Time.time;
                WeaponCollision.NoteContact();
            }
        }
    }

    // Marker holding the real world collider a sandbox copy stands in for.
    public class ProxyColliderSource : MonoBehaviour
    {
        public Collider source;
    }
}
