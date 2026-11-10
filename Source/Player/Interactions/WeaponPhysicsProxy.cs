using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovVR.Source.Player.Interactions
{
    // Real-PhysX collision for the gun, sandboxed. An invisible Rigidbody shaped like the weapon (fat body + thin barrel capsule)
    // is driven toward the controller pose and collided by the engine, in its own local physics scene with static copies of the
    // nearby world colliders. Tarkov runs autoSimulation off and Physics.Simulate() would step the whole default scene
    // (double-steps loot/ragdolls), so the proxy steps only its sandbox. The sandbox is tiny, so it can afford fine substepping,
    // which is what catches a fast rotational swing tunnelling a thin wall (PhysX CCD is blind to rotation).
    // Known limitation: non-readable world meshes can't be copied (no collision there) + a one-time mesh-cook cost per region.
    public class WeaponPhysicsProxy : MonoBehaviour
    {
        public static float driveGain = 1f;
        public static float maxSpeed = 40f;         // m/s
        public static float maxAngSpeed = 5000f;    // deg/s
        public static float bodySplit = 0.5f;       // rod fraction where the fat body capsule ends and the thin barrel begins
        public static float resyncDistance = 3f;    // if the body is this far from the hand it's desynced (garbage frame) -> snap
        public static float contactMemory = 0.04f;  // s a contact is remembered after it ends (small = no "stick" on pull-off; bridges single-frame gaps)

        public static float queryRegion = 1.5f;     // m half-extent of the box that cooks nearby colliders: covers a swing's reach, so the collider is there before the swing
        public static float retainDistance = 3f;    // m a cooked copy is kept until this far - hysteresis so back-and-forth doesn't re-cook
        public static float colliderRefreshMove = 0.25f;
        public static float colliderRefreshInterval = 0.3f;

        public static float substepMuzzleDist = 0.015f;  // m the muzzle moves at most per sandbox substep: must be < the thinnest wall or a fast rotation tunnels
        public static int maxSimSubsteps = 40;

        public static float contactLeadMargin = 0.03f;   // m; physics mode engages when geometry is within (gun radius + this-frame travel + this); bigger = earlier

        private Rigidbody rb;
        private CapsuleCollider bodyCol;
        private CapsuleCollider barrelCol;
        private bool active;
        private Vector3 targetPos;
        private Quaternion targetRot = Quaternion.identity;
        private float builtLen = -1f, builtBodyR = -1f, builtBarrelR = -1f;
        private float lastContactTime = -10f;

        // --- private physics sandbox ---
        private Scene sandbox;
        private PhysicsScene sandboxPhysics;
        private int worldMask;
        private readonly Dictionary<int, GameObject> colliderCopies = new Dictionary<int, GameObject>(128);
        private readonly Collider[] overlapBuf = new Collider[128];
        private readonly List<int> staleKeys = new List<int>(128);
        private readonly HashSet<int> ignoredColliders = new HashSet<int>();   // e.g. broken glass - never (re)copy these into the sandbox
        private Vector3 lastQueryCenter = new Vector3(1e9f, 1e9f, 1e9f);
        private float nextQueryTime;
        private readonly Collider[] proximityBuf = new Collider[32];   // room for our own colliders (skipped) without crowding out the world's

        // Our own player's colliders (the body, the guns on it, the gun in hands) and any weapon model are never world geometry here:
        // a freshly drawn gun's own colliders, copied in as world, push the holder off the hand. Cached by instance id, the cache
        // cleared now and then (a pooled gun model can come back as loot).
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
        public bool Active => active;
        public Vector3 ResolvedPos => active ? rb.position : targetPos;
        public Quaternion ResolvedRot => active ? rb.rotation : targetRot;

        public static WeaponPhysicsProxy Create(int worldMask)
        {
            GameObject proxyObject = new GameObject("WeaponPhysicsProxy");
            var proxy = proxyObject.AddComponent<WeaponPhysicsProxy>();
            proxy.worldMask = worldMask;

            proxy.rb = proxyObject.AddComponent<Rigidbody>();
            proxy.rb.useGravity = false;
            proxy.rb.drag = 0f;
            proxy.rb.angularDrag = 0f;
            proxy.rb.mass = 1f;
            proxy.rb.interpolation = RigidbodyInterpolation.None;
            proxy.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

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
            }
            targetPos = pos;
            targetRot = rot;
        }

        // Apply the rig's rigid motion (locomotion + turning) directly - see WeaponCollision.
        public void AddRigMotion(Quaternion dRot, Vector3 pivotPrev, Vector3 pivotCur)
        {
            if (!active || rb == null)
                return;
            rb.position = pivotCur + dRot * (rb.position - pivotPrev);
            rb.rotation = dRot * rb.rotation;
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
            if (!on)
            {
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        // Step the sandbox at render rate (from WeaponCollision.Apply, in Update), not in FixedUpdate. rb.position is then
        // current this frame, so:
        //  - no rb.interpolation is needed (at 50Hz it fights AddRigMotion's teleport = jitter while walking, and it lags);
        //  - the proxy collides the same frame the hand does, so the visible gun stops at the wall instead of clipping through;
        //  - in free space the gain-1 drive reaches the hand exactly each frame: the gun sits at the true controller pose.
        public void Step(float dt)
        {
            if (!active || rb == null || rb.isKinematic)
                return;
            dt = Mathf.Clamp(dt, 1e-4f, 0.05f);   // bound a frame hitch so a huge dt can't outrun the substep budget

            RefreshNearbyColliders();

            // Free-space fast path: no world geometry within reach of the whole gun this frame -> snap the proxy straight to
            // the controller pose (zero lag, no speed cap; the target already includes locomotion + turn + hand). The
            // speed-capped velocity drive below only runs when something is nearby to collide with. "Reach" = the gun's
            // grip->muzzle capsule plus this frame's travel + a margin, so physics mode starts before the muzzle can reach a wall.
            float length = Mathf.Max(builtLen, 0f);
            Vector3 muzzle = rb.position + rb.rotation * new Vector3(0f, -length, 0f);
            float muzzleTravel = (targetPos - rb.position).magnitude
                               + Quaternion.Angle(rb.rotation, targetRot) * Mathf.Deg2Rad * length;
            float reachRadius = contactLeadMargin + Mathf.Max(builtBodyR, builtBarrelR) + muzzleTravel;
            int found = Physics.OverlapCapsuleNonAlloc(rb.position, muzzle, reachRadius, proximityBuf,
                                                      worldMask, QueryTriggerInteraction.Collide);
            int near = 0;
            for (int i = 0; i < found; i++)
            {
                if (proximityBuf[i] != null && !IsOwnCollider(proximityBuf[i]))
                    near++;
            }
            if (near == 0)
            {
                rb.position = targetPos;
                rb.rotation = targetRot;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                return;
            }

            Vector3 driveVelocity = (targetPos - rb.position) / dt * driveGain;
            float driveSpeed = driveVelocity.magnitude;
            if (driveSpeed > maxSpeed)
                driveVelocity *= maxSpeed / driveSpeed;
            rb.velocity = driveVelocity;

            Quaternion rotationDelta = targetRot * Quaternion.Inverse(rb.rotation);
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
            float substep = dt / steps;
            for (int i = 0; i < steps; i++)
                sandboxPhysics.Simulate(substep);
        }

        // Cook every world collider within queryRegion (a box around the gun) into the sandbox.
        // Cached by instance id -> each cooks once; kept until retainDistance away (hysteresis).
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
                Quaternion.identity, worldMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < overlapCount; i++)
            {
                Collider source = overlapBuf[i];
                if (source == null)
                    continue;
                int id = source.GetInstanceID();
                if (ignoredColliders.Contains(id) || IsOwnCollider(source))
                    continue;
                if (colliderCopies.TryGetValue(id, out GameObject existing))
                {
                    if (existing != null)
                        existing.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                }
                else
                {
                    GameObject copy = CopyCollider(source);
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
                if (distanceSquared > retainDistanceSquared)
                    staleKeys.Add(entry.Key);
            }
            for (int i = 0; i < staleKeys.Count; i++)
            {
                if (colliderCopies.TryGetValue(staleKeys[i], out GameObject go) && go != null)
                    Destroy(go);
                colliderCopies.Remove(staleKeys[i]);
            }
        }

        // A static clone of a world collider inside the sandbox (same shape/mesh/pose/scale, same layer as
        // the proxy so they collide). Carries the source collider for feedback (glass break / surface sound).
        private GameObject CopyCollider(Collider source)
        {
            GameObject copy = new GameObject("wc_copy") { layer = gameObject.layer };
            SceneManager.MoveGameObjectToScene(copy, sandbox);
            Transform sourceTransform = source.transform;
            copy.transform.SetPositionAndRotation(sourceTransform.position, sourceTransform.rotation);
            copy.transform.localScale = sourceTransform.lossyScale;

            bool made = false;
            switch (source)
            {
                case MeshCollider meshCollider when meshCollider.sharedMesh != null:
                    var meshCopy = copy.AddComponent<MeshCollider>();
                    meshCopy.sharedMesh = meshCollider.sharedMesh;
                    meshCopy.convex = false;
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
            if (colliderCopies.TryGetValue(instanceId, out GameObject copy))
            {
                colliderCopies.Remove(instanceId);
                if (copy != null)
                {
                    Collider copyCollider = copy.GetComponent<Collider>();
                    if (copyCollider != null)
                        copyCollider.enabled = false;   // immediate: stop colliding this frame too (Destroy is deferred to end of frame)
                    Destroy(copy);
                }
            }
        }

        private void OnCollisionEnter(Collision c)
        {
            if (!active || c.contactCount == 0)
                return;
            lastContactTime = Time.time;
            ContactPoint contact = c.GetContact(0);
            Collider source = contact.otherCollider != null ? contact.otherCollider.GetComponent<ProxyColliderSource>()?.source : null;
            if (source != null)
                WeaponCollision.OnProxyContact(source, contact.point, contact.normal, c.relativeVelocity.magnitude);
        }

        private void OnCollisionStay(Collision c)
        {
            if (active && c.contactCount > 0)
                lastContactTime = Time.time;
        }
    }

    // Marker holding the real world collider a sandbox copy stands in for.
    public class ProxyColliderSource : MonoBehaviour
    {
        public Collider source;
    }
}
