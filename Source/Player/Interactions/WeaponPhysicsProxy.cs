using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TarkovVR.Source.Player.Interactions
{
    // Real-PhysX collision for the gun, SANDBOXED. An invisible Rigidbody shaped like the weapon (fat body
    // + thin barrel capsule) is driven toward the controller pose and collided by the engine, in its OWN
    // local physics scene with static COPIES of the nearby world colliders. Tarkov runs autoSimulation OFF
    // and Physics.Simulate() would step the WHOLE default scene (double-steps loot/ragdolls = "sped up"),
    // so the proxy steps ONLY its sandbox -> game physics untouched, and because the sandbox is tiny it can
    // afford FINE substepping (which is what catches a fast ROTATIONAL swing tunnelling a thin wall - PhysX
    // CCD is blind to rotation). Known limitation: non-readable world meshes can't be copied (collision
    // there is absent) + a one-time mesh-cook cost as regions come into range.
    public class WeaponPhysicsProxy : MonoBehaviour
    {
        public static float driveGain = 1f;
        public static float maxSpeed = 40f;         // m/s
        public static float maxAngSpeed = 5000f;    // deg/s
        public static float bodySplit = 0.5f;       // rod fraction where the fat body capsule ends and the thin barrel begins
        public static float resyncDistance = 3f;    // if the body is this far from the hand it's desynced (garbage frame) -> snap
        public static float contactMemory = 0.04f;  // s a contact is remembered after it ends (small = no "stick" on pull-off; bridges single-frame gaps)

        public static float queryRegion = 1.5f;     // m half-extent of the box that COOKS nearby colliders - big enough to cover a swing's reach so the collider is present before the swing arrives
        public static float retainDistance = 3f;    // m a cooked copy is kept until this far - hysteresis so back-and-forth doesn't re-cook
        public static float colliderRefreshMove = 0.25f;
        public static float colliderRefreshInterval = 0.3f;

        public static float substepMuzzleDist = 0.015f;  // m; sandbox sim split so the muzzle moves at most this per substep - MUST be < the thinnest wall or a fast rotation tunnels (cheap here: tiny scene)
        public static int maxSimSubsteps = 40;

        public static float contactLeadMargin = 0.03f;   // m; STATIC lead on the free-space fast path: physics mode engages when geometry is within (gun radius + this-frame travel + this) of the gun. The this-frame-travel term is the real onset guard (always catches the approach), so this can be small = physics mode only when the gun is quite close. Bigger = engages earlier/further out.

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
        private readonly Collider[] proximityBuf = new Collider[8];

        public bool RecentlyContacted => active && Time.time - lastContactTime < contactMemory;
        public bool Active => active;
        public Vector3 ResolvedPos => active ? rb.position : targetPos;
        public Quaternion ResolvedRot => active ? rb.rotation : targetRot;

        public static WeaponPhysicsProxy Create(int worldMask)
        {
            GameObject go = new GameObject("WeaponPhysicsProxy");
            var p = go.AddComponent<WeaponPhysicsProxy>();
            p.worldMask = worldMask;

            p.rb = go.AddComponent<Rigidbody>();
            p.rb.useGravity = false;
            p.rb.drag = 0f;
            p.rb.angularDrag = 0f;
            p.rb.mass = 1f;
            p.rb.interpolation = RigidbodyInterpolation.None;
            p.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            // Frictionless (Minimum combine -> every contact is frictionless regardless of the wall's
            // material) so the gun SLIDES / PIVOTS along a surface instead of gripping it ("stuck pointing
            // at the wall"). Zero bounce so it doesn't spring off.
            var slick = new PhysicMaterial("WeaponCollisionSlick")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 0f,
                frictionCombine = PhysicMaterialCombine.Minimum,
                bounceCombine = PhysicMaterialCombine.Minimum,
            };
            p.bodyCol = go.AddComponent<CapsuleCollider>();
            p.bodyCol.direction = 1;   // local Y = the barrel axis
            p.bodyCol.material = slick;
            p.barrelCol = go.AddComponent<CapsuleCollider>();
            p.barrelCol.direction = 1;
            p.barrelCol.material = slick;

            p.sandbox = SceneManager.CreateScene("SPTVR_WeaponPhysics",
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            SceneManager.MoveGameObjectToScene(go, p.sandbox);
            p.sandboxPhysics = p.sandbox.GetPhysicsScene();

            p.SetActive(false);
            return p;
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

        // Step the sandbox at RENDER rate (called from WeaponCollision.Apply, which runs in Update), NOT in
        // FixedUpdate. Stepping in lockstep with rendering means rb.position is current THIS frame, so:
        //  - no rb.interpolation is needed (interpolation at 50Hz was the root cause of the render-body pose
        //    jittering while walking - it fought AddRigMotion's teleport - and of the "weighty" lag);
        //  - contact detection has zero lag (the proxy collides the same frame the hand does, so the visible
        //    gun stops at the wall instead of clipping through until the next 50Hz tick);
        //  - in free space the gain-1 drive reaches the hand exactly each frame, so the rendered gun sits at
        //    the true controller pose (crisp, no lag).
        public void Step(float dt)
        {
            if (!active || rb == null || rb.isKinematic)
                return;
            dt = Mathf.Clamp(dt, 1e-4f, 0.05f);   // bound a frame hitch so a huge dt can't outrun the substep budget

            RefreshNearbyColliders();

            // FREE-SPACE FAST PATH: if no world geometry is within reach of the whole gun THIS frame, snap the
            // proxy straight to the controller pose - zero lag, no speed cap, and NO rig-motion reconstruction
            // needed (the target already includes locomotion + turn + hand). This is what keeps fast swings and
            // stick-turns crisp; the speed-capped velocity drive below (capped so substepping can catch a fast
            // rotation tunnelling a thin wall) only runs when something is actually nearby to collide with.
            // "reach" spans the gun grip->muzzle capsule PLUS this frame's travel + a margin, so we drop into
            // physics mode BEFORE the muzzle can reach a wall = no clip-through on the onset frame.
            float len = Mathf.Max(builtLen, 0f);
            Vector3 muzzle = rb.position + rb.rotation * new Vector3(0f, -len, 0f);
            float muzzleTravel = (targetPos - rb.position).magnitude
                               + Quaternion.Angle(rb.rotation, targetRot) * Mathf.Deg2Rad * len;
            float reachRadius = contactLeadMargin + Mathf.Max(builtBodyR, builtBarrelR) + muzzleTravel;
            int near = Physics.OverlapCapsuleNonAlloc(rb.position, muzzle, reachRadius, proximityBuf,
                                                      worldMask, QueryTriggerInteraction.Collide);
            if (near == 0)
            {
                rb.position = targetPos;
                rb.rotation = targetRot;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                return;
            }

            Vector3 v = (targetPos - rb.position) / dt * driveGain;
            float vs = v.magnitude;
            if (vs > maxSpeed)
                v *= maxSpeed / vs;
            rb.velocity = v;

            Quaternion dq = targetRot * Quaternion.Inverse(rb.rotation);
            dq.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f)
                ang -= 360f;
            if (Mathf.Abs(ang) > 1e-3f && axis.sqrMagnitude > 1e-8f && !float.IsInfinity(axis.x))
            {
                Vector3 w = axis.normalized * (ang * Mathf.Deg2Rad / dt) * driveGain;
                float ws = w.magnitude, wmax = maxAngSpeed * Mathf.Deg2Rad;
                if (ws > wmax)
                    w *= wmax / ws;
                rb.angularVelocity = w;
            }
            else
            {
                rb.angularVelocity = Vector3.zero;
            }

            // Step ONLY the sandbox (game physics untouched), SUBSTEPPED fine enough that a fast rotational
            // swing can't skip through a thin wall. Cheap because the sandbox holds only the gun + nearby
            // static copies. Velocity set once above; contact response carries between substeps.
            float muzzleSpeed = rb.velocity.magnitude + rb.angularVelocity.magnitude * builtLen;
            int steps = Mathf.Clamp(Mathf.CeilToInt(muzzleSpeed * dt / substepMuzzleDist), 1, maxSimSubsteps);
            float sub = dt / steps;
            for (int i = 0; i < steps; i++)
                sandboxPhysics.Simulate(sub);
        }

        // Cook every world collider within queryRegion (a generous box around the gun) into the sandbox.
        // Cached by instance id -> each cooks ONCE; kept until retainDistance away (hysteresis).
        private void RefreshNearbyColliders()
        {
            if (builtLen <= 0f)
                return;
            if ((rb.position - lastQueryCenter).sqrMagnitude < colliderRefreshMove * colliderRefreshMove &&
                Time.time < nextQueryTime)
                return;
            lastQueryCenter = rb.position;
            nextQueryTime = Time.time + colliderRefreshInterval;

            int n = Physics.OverlapBoxNonAlloc(rb.position, Vector3.one * queryRegion, overlapBuf,
                Quaternion.identity, worldMask, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                Collider src = overlapBuf[i];
                if (src == null)
                    continue;
                int id = src.GetInstanceID();
                if (ignoredColliders.Contains(id))
                    continue;
                if (colliderCopies.TryGetValue(id, out GameObject existing))
                {
                    if (existing != null)
                        existing.transform.SetPositionAndRotation(src.transform.position, src.transform.rotation);
                }
                else
                {
                    GameObject copy = CopyCollider(src);
                    if (copy != null)
                        colliderCopies[id] = copy;
                }
            }

            staleKeys.Clear();
            float retSqr = retainDistance * retainDistance;
            foreach (var kv in colliderCopies)
            {
                GameObject g = kv.Value;
                if (g == null)
                {
                    staleKeys.Add(kv.Key);
                    continue;
                }
                Collider c = g.GetComponent<Collider>();
                float dSqr = c != null ? c.bounds.SqrDistance(rb.position)
                                       : (g.transform.position - rb.position).sqrMagnitude;
                if (dSqr > retSqr)
                    staleKeys.Add(kv.Key);
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
        private GameObject CopyCollider(Collider src)
        {
            GameObject go = new GameObject("wc_copy") { layer = gameObject.layer };
            SceneManager.MoveGameObjectToScene(go, sandbox);
            Transform t = src.transform;
            go.transform.SetPositionAndRotation(t.position, t.rotation);
            go.transform.localScale = t.lossyScale;

            bool made = false;
            switch (src)
            {
                case MeshCollider mc when mc.sharedMesh != null:
                    var nmc = go.AddComponent<MeshCollider>();
                    nmc.sharedMesh = mc.sharedMesh;
                    nmc.convex = false;
                    made = true;
                    break;
                case BoxCollider bc:
                    var nbc = go.AddComponent<BoxCollider>();
                    nbc.center = bc.center; nbc.size = bc.size;
                    made = true;
                    break;
                case SphereCollider sc:
                    var nsc = go.AddComponent<SphereCollider>();
                    nsc.center = sc.center; nsc.radius = sc.radius;
                    made = true;
                    break;
                case CapsuleCollider cc:
                    var ncc = go.AddComponent<CapsuleCollider>();
                    ncc.center = cc.center; ncc.radius = cc.radius; ncc.height = cc.height; ncc.direction = cc.direction;
                    made = true;
                    break;
                case TerrainCollider tc when tc.terrainData != null:
                    var ntc = go.AddComponent<TerrainCollider>();
                    ntc.terrainData = tc.terrainData;
                    made = true;
                    break;
            }
            if (!made)
            {
                Destroy(go);
                return null;
            }
            go.AddComponent<ProxyColliderSource>().source = src;
            return go;
        }

        // Drop a world collider from the sandbox and never re-copy it (broken glass): the real pane's
        // collider is gone after the break, but our static copy would persist until it drifted out of
        // retainDistance - so the gun kept hitting the phantom pane. Blacklist so a throttled refresh can't
        // re-copy it in the window before the real collider is actually destroyed.
        public void ForgetCollider(int instanceId)
        {
            ignoredColliders.Add(instanceId);
            if (colliderCopies.TryGetValue(instanceId, out GameObject go))
            {
                colliderCopies.Remove(instanceId);
                if (go != null)
                {
                    Collider c = go.GetComponent<Collider>();
                    if (c != null)
                        c.enabled = false;   // immediate: stop colliding this frame too (Destroy is deferred to end of frame)
                    Destroy(go);
                }
            }
        }

        private void OnCollisionEnter(Collision c)
        {
            if (!active || c.contactCount == 0)
                return;
            lastContactTime = Time.time;
            ContactPoint cp = c.GetContact(0);
            Collider src = cp.otherCollider != null ? cp.otherCollider.GetComponent<ProxyColliderSource>()?.source : null;
            if (src != null)
                WeaponCollision.OnProxyContact(src, cp.point, cp.normal, c.relativeVelocity.magnitude);
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
