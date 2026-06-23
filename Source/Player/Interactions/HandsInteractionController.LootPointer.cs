using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using TarkovVR.ModSupport;
using TarkovVR.ModSupport.FIKA;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;

namespace TarkovVR.Source.Player.Interactions
{
    // Palm-cone loose-loot pointer + telekinetic summon.
    //
    // Replaces the old "look at item -> gaze 'Take' menu" loot flow with a physical,
    // native-VR one:
    //   1. A short cone projects out of the (secondary) palm. Every loose item inside it
    //      gets a small white dot at its center.
    //   2. A thin capsule/sphere probe straight out of the palm center picks the "main"
    //      item (most-centered / probe-hit). Its dot grows smoothly; the others shrink.
    //   3. Grip = summon the main item to the palm. It flies in and stops with its collider
    //      surface resting on the palm (the same surface-snap is used for point-blank grabs,
    //      so a close grab forces the item to the outer part of its collider instead of
    //      grabbing it wherever the hand is buried). Collide-and-slide keeps it out of walls.
    //   4. Once it's in hand, looking at it shows the native name + "Take" menu (handled in
    //      UIPatches.Raycaster, gated by suppressWorldLootMenu).
    //
    // Everything is exposed as live-tunable statics so it can be A/B'd in the headset.
    internal partial class HandsInteractionController
    {
        //--- Tunables ----------------------------------------------------------------

        // When true (req #1) loose items in the WORLD no longer pop the gaze "Take" menu —
        // the dots replace it, and the menu only returns for the item that's in your hand
        // (req #5). Read by UIPatches.Raycaster. false reverts to the old gaze-Take.
        public static bool suppressWorldLootMenu = true;

        // Cone reach (~0.6 m ≈ 2 ft) and half-angle. pointBlankRange items count regardless
        // of angle (the hand is basically on/in them).
        public static float coneRange = 0.6f;
        public static float coneHalfAngle = 35f;
        public static float pointBlankRange = 0.13f;

        // The thin center probe that selects the main item (swept-sphere = a small capsule).
        public static float capsuleRadius = 0.05f;

        // The cone aim transform's local pose under the hand. Only its ORIENTATION matters now
        // (the origin comes from the palm bone below); coneAimLocalEuler sets the aim direction.
        public static Vector3 coneAimLocalPos = new Vector3(0f, 0f, 0f);
        public static Vector3 coneAimLocalEuler = new Vector3(75f, 266f, 0f);

        // The cone ORIGIN. The hand's rig transform is the WRIST (~17 cm back from the
        // controller — the palmCollisionPoints lesson), so the cone used to start at the wrist.
        // Instead originate from the actual palm BONE (InitVRPatches.leftPalm = "Base HumanLPalm");
        // this offset (in the palm bone's local frame) nudges it toward the fingers/grip.
        public static Vector3 conePalmLocalOffset = Vector3.zero;

        // Dot sizes (world diameters) and how fast they ease toward their target size.
        public static float dotMinSize = 0.012f;
        public static float dotMaxSize = 0.030f;
        public static float dotGrowSpeed = 14f;
        // 0 = dot exactly at item center (can be buried in big items); 1 = on the near
        // surface facing the palm (always visible). Default biases toward visible.
        public static float dotSurfaceBias = 0.7f;
        // Extra push OUTWARD off the box surface (m), along the item-center->surface normal,
        // so the dot floats just clear of the collider instead of sinking into the mesh on
        // some shapes (the box surface can sit inside a concave item).
        public static float dotSurfaceOffset = 0.03f;

        // Weights the "main" pick: lower angle (more centered) wins; distance is a soft
        // tie-break (degrees-equivalent per meter).
        public static float centerDistWeight = 25f;

        // Summon timing: duration scales with pull distance (so a point-blank grab snaps
        // almost instantly while a 2 ft pull eases in) and the rest depth sinks the item
        // this far past the palm-surface contact so it reads as gripped rather than floating.
        public static float summonSecPerMeter = 0.5f;
        public static float summonMinDuration = 0.04f;
        public static float summonMaxDuration = 0.35f;
        public static float summonRestDepth = 0.02f;

        public static float handPoseWeight = 1f;

        // Per-finger flex DELTA in finger order (index 0 = thumb, then index/middle/ring/pinky).
        // The hand eases from fingerFlexDeg (idle / free hand) to fingerReadyFlexDeg (a loot dot
        // is in view). 0 = that finger sits at the baked pose; negative opens it, positive curls
        // it in. flexSign flips the direction globally; midJointScale tapers the delta per joint.
        public static float[] fingerFlexDeg = new float[] { 5f, -15f, -20f, -27f, -27f };
        public static float[] fingerReadyFlexDeg = new float[] { 0f, -25f, -25f, -25f, -25f };
        public static float flexSign = 1f;
        public static float midJointScale = 0.6f;
        public static float handOpenLerp = 10f;

        // Lateral finger SPREAD (abduction) so the flattened hand fans out instead of looking
        // like a paddle. Degrees between adjacent fingers, applied at the knuckle around
        // spreadAxis (local). Negate the degrees (or flip spreadAxis) if the fingers cross
        // instead of fanning; change spreadAxis if they splay forward/back instead of sideways.
        public static float fingerSpreadDeg = -5f; // DELTA on the baked pose (negative = fan OUT)
        public static Vector3 spreadAxis = new Vector3(0f, 1f, 0f);

        public static bool captureRestPose = false;
        // Baked from EFT's natural NO-WEAPON hand animation (the empty rested hand). This is the
        // BASE pose; flex/spread layer on top as deltas (see ReplayRestPose). finger0 = thumb.
        public static Vector3[] restPoseEuler = new Vector3[]
        {
            new Vector3(57.54f, 83.71f, 65.10f),   // finger0 (thumb) joint0
            new Vector3(359.84f, 0.31f, 10.90f),   // finger0 (thumb) joint1
            new Vector3(358.99f, 359.14f, 12.16f), // finger0 (thumb) joint2
            new Vector3(359.38f, 359.20f, 30.56f), // finger1 joint0
            new Vector3(359.21f, 359.87f, 31.54f), // finger1 joint1
            new Vector3(359.56f, 359.34f, 32.15f), // finger1 joint2
            new Vector3(358.85f, 359.47f, 40.54f), // finger2 joint0
            new Vector3(358.96f, 0.21f, 41.76f),   // finger2 joint1
            new Vector3(359.46f, 359.63f, 42.57f), // finger2 joint2
            new Vector3(358.77f, 359.51f, 44.56f), // finger3 joint0
            new Vector3(358.84f, 0.38f, 46.04f),   // finger3 joint1
            new Vector3(359.35f, 359.68f, 46.82f), // finger3 joint2
            new Vector3(358.86f, 359.46f, 45.41f), // finger4 joint0
            new Vector3(358.85f, 0.55f, 47.17f),   // finger4 joint1
            new Vector3(359.34f, 359.70f, 48.33f), // finger4 joint2
        };

        // Live per-joint editor for authoring/refining the baked pose with real-time feedback
        // (needs useRestPose=true + a seeded restPoseEuler). poseEditJoint = the flat joint index
        // to edit (-1 = off; indices match the CaptureRestPose log labels). CHANGING it loads
        // that joint's current Euler into poseEditEuler; then your edits to poseEditEuler write
        // back live so you watch the joint move. logRestPose=true prints the full current
        // restPoseEuler array to paste into the file once you're happy.
        public static int poseEditJoint = -1;
        public static Vector3 poseEditEuler = Vector3.zero;
        public static bool logRestPose = false;

        // Loot pointing is blocked while the off-hand is within this distance of the foregrip
        // (the two-hand support trigger ~0.1-0.175 m), so you don't grab loot reaching for it.
        public static float foregripBlockDistance = 0.2f;

        // FIKA co-op: how often (seconds) we broadcast a held item's position to other players.
        // ~0.04 = 25/s (FIKA's own item sync uses FixedUpdate ~50/s; 20-30/s is plenty).
        public static float lootSyncInterval = 0.04f;

        //--- Runtime state -----------------------------------------------------------
        private Transform coneAim;
        // 0 = AUTO: use EFT's own interaction raycast mask (EFT.GameWorld.int_0) — the exact
        // mask the head-gaze raycast in UIPatches.Raycaster uses to hit loose loot/containers/
        // doors. We then filter to the LootItem component, so only loose loot becomes a
        // candidate. Set non-zero to override. (First cut hard-coded layer 3 and found nothing.)
        public static int lootLayerMask = 0;
        private int currentLootMask;
        // Flip on in UnityExplorer to log overlap/candidate counts once a second.
        public static bool debugLootPointer = false;
        private float lastPointerLog;
        private int lastOverlapCount;

        private bool isSummoningLoot;
        // Time.time of the most recent grab — read by FIKASupport's steal arbitration so the
        // newest grabber wins a contested item (a player who grabbed >stealGraceTime ago yields).
        public float heldItemGrabTime;
        private float summonT;
        private float currentSummonDuration;
        private Vector3 summonStartOffset, summonRestOffset;

        private LootItem lastMain;

        private struct LootCand
        {
            public LootItem item;
            public Vector3 dotPos;   // where the dot sits (center biased toward the near surface)
            public float dist;       // palm -> bound center
            public float ang;        // angle off the cone axis
            public bool inside;      // the palm is physically INSIDE this item's collider
        }

        private class DotState
        {
            public GameObject go;
            public float scale;
            public float target;
            public bool active;
            public Vector3 pos;
        }

        private readonly List<LootCand> lootCandidates = new List<LootCand>();
        private readonly HashSet<LootItem> lootCandidateSet = new HashSet<LootItem>();
        private readonly Dictionary<LootItem, DotState> lootDots = new Dictionary<LootItem, DotState>();
        private readonly Dictionary<Collider, LootItem> lootColliderCache = new Dictionary<Collider, LootItem>();
        private readonly List<LootItem> lootDotRemove = new List<LootItem>();

        // Hand-opener state.
        private bool lootDotPresent;
        private bool fingersResolved;
        private Transform[][] leftFingerJoints; // [finger][joint] under "Base HumanLPalm"
        private bool[] leftFingerIsThumb;       // parallel to leftFingerJoints
        private float[] leftFingerSpread;       // signed centered ordinal per finger (fan amount)
        private int lastPoseEditJoint = -2;     // tracks poseEditJoint changes for the live editor
        private float handWeightBlend;          // eased override weight (0..1)
        private float flexProgress;             // eased 0 (idle) -> 1 (loot dot in view)
        private readonly Dictionary<Transform, Quaternion> fingerBaseRot = new Dictionary<Transform, Quaternion>();
        private readonly Dictionary<Transform, Quaternion> fingerOutRot = new Dictionary<Transform, Quaternion>();

        // Cone-aim debug gizmo (shown while debugLootPointer is on).
        private GameObject coneOriginViz, coneDirViz, coneEndViz;

        //--- Per-frame entry ---------------------------------------------------------
        private void UpdateLootPointer()
        {
            lootDotPresent = false; // set true below once we actually have candidates

            // A body grab owns the off-hand grip — don't also point at / summon loose loot.
            if (isDraggingBody || bodyInteractionArmed)
            {
                ClearDots();
                return;
            }

            // The gaze "Take" can consume it into the
            // inventory — the item_0 nulls (and the GameObject is destroyed shortly after).
            // Reset our refs so the weight/interaction state clears and pointing resumes.
            if (!ReferenceEquals(heldItem, null) && !HeldItemAlive())
                CleanupAfterHeldItemGone();

            bool usingOrEating = VRGlobals.usingItem || EatingInteractionController.ManualActive;

            // (Req 1) A use/eat started while we still physically hold a loot item — get our
            // hold out of the way: stow it to the inventory if it fits, else drop it.
            if (usingOrEating && HeldItemAlive())
                ReleaseHeldForUse();

            // Pointing only happens with a free off-hand — not while holding/summoning, not
            // mid use/eat (Req 2), and not while the hand is near the foregrip trigger (Req 4).
            if (heldItem != null || isSummoningLoot || usingOrEating || NearForegrip())
            {
                ClearDots();
                return;
            }

            Transform hand = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.LeftHand?.transform : null;
            if (hand == null)
            {
                ClearDots();
                return;
            }

            EnsureConeAim(hand);
            // Mirror the game's interactable mask (what the gaze raycast hits) unless overridden.
            currentLootMask = lootLayerMask != 0 ? lootLayerMask : EFT.GameWorld.int_0;

            // Origin from the actual hand grip (the rig transform is the WRIST). Direction still
            // from the tunable coneAim (child of the hand) so the aim-euler tuning holds.
            Vector3 palm = PalmOrigin();
            Vector3 aim = coneAim.forward;

            GatherCandidates(palm, aim);
            LootItem main = PickMain(palm, aim);
            UpdateDots(main);
            UpdateConeViz(palm, aim); // in-headset gizmo for tuning the aim direction
            lootDotPresent = lootCandidates.Count > 0; // drives the "ready to grab" hand pose

            if (debugLootPointer && Time.time - lastPointerLog > 1f)
            {
                lastPointerLog = Time.time;
                Plugin.MyLog.LogInfo($"[LootPointer] mask=0x{currentLootMask:X} overlap={lastOverlapCount} candidates={lootCandidates.Count} main={(main != null ? main.name : "null")}");
            }

            // Light pulse when the highlighted item changes, like entering an interact zone.
            if (main != null && main != lastMain)
                SteamVR_Actions._default.Haptic.Execute(0, 0.04f, 1, 0.25f, secondaryInputSource);
            lastMain = main;

            // Grip = summon. Guarded on heldItem == null so it never double-grabs.
            if (secondaryHandGrip.stateDown && debugLootPointer)
                Plugin.MyLog.LogInfo($"[LootPointer] GRIP main={(main != null ? main.name : "null")} candidates={lootCandidates.Count} near={(leftHandState.lootItem != null ? leftHandState.lootItem.name : "null")} held={(heldItem != null ? heldItem.name : "null")}");
            if (main != null && heldItem == null && secondaryHandGrip.stateDown)
                BeginSummon(main, palm, aim);
        }

        //--- Detection ---------------------------------------------------------------
        private void GatherCandidates(Vector3 palm, Vector3 aim)
        {
            lootCandidates.Clear();
            lootCandidateSet.Clear();

            Collider[] cols = Physics.OverlapSphere(palm, coneRange, currentLootMask, QueryTriggerInteraction.Collide);
            lastOverlapCount = cols.Length;
            for (int i = 0; i < cols.Length; i++)
            {
                LootItem li = ResolveLootItem(cols[i]);
                if (li == null || li.Item == null)
                    continue;
                if (!lootCandidateSet.Add(li))
                    continue; // an item we already have (multiple colliders)

                Vector3 center = li._boundCollider != null ? li._boundCollider.bounds.center : cols[i].bounds.center;
                Vector3 toItem = center - palm;
                float dist = toItem.magnitude;
                float ang = dist > 1e-4f ? Vector3.Angle(aim, toItem) : 0f;

                // The hand is physically INSIDE this item's collider — a point-blank physical grab,
                // valid at any angle/distance (a large item's CENTRE can be outside the cone while
                // your hand is buried in it — that was the "in the collider but no dot" gap).
                bool inside = li._boundCollider != null && IsInsideBox(li._boundCollider, palm);

                // Otherwise: point-blank (centre within reach) at any angle, or the centre must be
                // inside the cone.
                if (!inside && dist > pointBlankRange && ang > coneHalfAngle)
                {
                    lootCandidateSet.Remove(li);
                    continue;
                }

                Vector3 surf = li._boundCollider != null ? NearestBoxSurfacePoint(li._boundCollider, palm) : center;
                lootCandidates.Add(new LootCand
                {
                    item = li,
                    dotPos = DotPos(center, surf, palm),
                    dist = dist,
                    ang = ang,
                    inside = inside
                });
            }

            // Belt-and-suspenders: the proven 20 Hz reach-in detector
            // (UpdateLeftHandCollisions) already resolves the point-blank loot item, so always
            // include it — a close grab then works regardless of the cone/mask specifics.
            LootItem near = leftHandState != null ? leftHandState.lootItem : null;
            if (near != null && near.Item != null && lootCandidateSet.Add(near))
            {
                Vector3 nc = near._boundCollider != null ? near._boundCollider.bounds.center : near.transform.position;
                Vector3 ns = near._boundCollider != null ? NearestBoxSurfacePoint(near._boundCollider, palm) : nc;
                lootCandidates.Add(new LootCand
                {
                    item = near,
                    dotPos = DotPos(nc, ns, palm),
                    dist = Vector3.Distance(nc, palm),
                    ang = 0f,
                    inside = near._boundCollider != null && IsInsideBox(near._boundCollider, palm)
                });
            }
        }

        private LootItem PickMain(Vector3 palm, Vector3 aim)
        {
            // PHYSICAL GRAB has priority over the cone: if the hand is physically ON an item,
            // grab THAT one — never let the cone's capsule probe pull a different (possibly far)
            // item across a cluster. 1) the proven reach-in detector (hand within 0.125 m);
            // 2) else the closest candidate inside pointBlankRange.
            LootItem near = leftHandState != null ? leftHandState.lootItem : null;
            if (near != null && near.Item != null && lootCandidateSet.Contains(near))
                return near;

            // The item the palm is INSIDE wins (closest centre if several); else the closest
            // candidate within pointBlankRange.
            LootItem insideClosest = null;
            float bestInside = float.MaxValue;
            LootItem closestPointBlank = null;
            float bestPB = pointBlankRange;
            for (int i = 0; i < lootCandidates.Count; i++)
            {
                LootCand c = lootCandidates[i];
                if (c.inside && c.dist < bestInside)
                {
                    bestInside = c.dist;
                    insideClosest = c.item;
                }
                else if (c.dist <= bestPB)
                {
                    bestPB = c.dist;
                    closestPointBlank = c.item;
                }
            }
            if (insideClosest != null)
                return insideClosest;
            if (closestPointBlank != null)
                return closestPointBlank;

            // CONE (aiming from a distance): the thin center probe wins if it lands on a candidate.
            if (Physics.SphereCast(palm, capsuleRadius, aim, out RaycastHit hit, coneRange, currentLootMask, QueryTriggerInteraction.Collide))
            {
                LootItem probed = ResolveLootItem(hit.collider);
                if (probed != null && lootCandidateSet.Contains(probed))
                    return probed;
            }

            // Otherwise the most-centered item (smallest angle), distance as a soft tie-break.
            LootItem main = null;
            float best = float.MaxValue;
            for (int i = 0; i < lootCandidates.Count; i++)
            {
                LootCand c = lootCandidates[i];
                float score = c.ang + c.dist * centerDistWeight;
                if (score < best)
                {
                    best = score;
                    main = c.item;
                }
            }
            return main;
        }

        private LootItem ResolveLootItem(Collider col)
        {
            if (col == null)
                return null;
            if (!lootColliderCache.TryGetValue(col, out LootItem li))
            {
                li = col.GetComponentInParent<LootItem>();
                // A Corpse IS a LootItem; bodies are grabbed physically (BodyGrab), never via the
                // loot pointer — keep them out so no white dot ever appears on a body.
                if (li is Corpse) li = null;
                lootColliderCache[col] = li; // may cache null (collider with no LootItem) on purpose
            }
            return li;
        }

        //--- Dots --------------------------------------------------------------------
        private void UpdateDots(LootItem main)
        {
            foreach (KeyValuePair<LootItem, DotState> kv in lootDots)
                kv.Value.active = false;

            for (int i = 0; i < lootCandidates.Count; i++)
            {
                LootCand c = lootCandidates[i];
                if (!lootDots.TryGetValue(c.item, out DotState ds))
                {
                    ds = new DotState { go = CreateDot(), scale = 0f };
                    lootDots[c.item] = ds;
                }
                ds.active = true;
                ds.target = c.item == main ? dotMaxSize : dotMinSize;
                ds.pos = c.dotPos;
            }

            // Frame-rate-independent ease toward the target size; cull faded-out / dead dots.
            float k = 1f - Mathf.Exp(-dotGrowSpeed * Time.deltaTime);
            lootDotRemove.Clear();
            foreach (KeyValuePair<LootItem, DotState> kv in lootDots)
            {
                DotState ds = kv.Value;
                if (kv.Key == null || ds.go == null)
                {
                    if (ds.go != null) Destroy(ds.go);
                    lootDotRemove.Add(kv.Key);
                    continue;
                }

                float tgt = ds.active ? ds.target : 0f;
                ds.scale = Mathf.Lerp(ds.scale, tgt, k);
                if (!ds.active && ds.scale < 0.0006f)
                {
                    Destroy(ds.go);
                    lootDotRemove.Add(kv.Key);
                    continue;
                }

                ds.go.transform.position = ds.pos;
                ds.go.transform.localScale = Vector3.one * ds.scale;
                ds.go.SetActive(ds.scale > 0.0006f);
            }
            for (int i = 0; i < lootDotRemove.Count; i++)
                lootDots.Remove(lootDotRemove[i]);
        }

        private GameObject CreateDot()
        {
            GameObject go = CreateMarker(PrimitiveType.Sphere, Color.white);
            go.name = "VRLootDot";
            go.transform.localScale = Vector3.zero;
            return go;
        }

        // A collider-less, shadow-less primitive with the always-on-top marker material.
        private static GameObject CreateMarker(PrimitiveType type, Color c)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = "VRLootGizmo";
            Destroy(go.GetComponent<Collider>());

            // Internal-Colored multiplies vertex color by _Color, so paint the mesh white (the
            // primitive meshes have no color channel) or the marker renders black. mf.mesh
            // returns a unique instance, so this doesn't touch the shared primitive mesh.
            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.mesh != null)
            {
                Mesh mesh = mf.mesh;
                var cols = new Color[mesh.vertexCount];
                for (int i = 0; i < cols.Length; i++) cols[i] = Color.white;
                mesh.colors = cols;
            }

            Renderer r = go.GetComponent<Renderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.material = MakeMarkerMaterial(c);
            return go;
        }

        private static Material MakeMarkerMaterial(Color c)
        {
            // "Hidden/Internal-Colored" is an engine built-in that actually honors _ZTest
            // (Sprites/Default silently ignores it), so ZTest Always = render ON TOP of all
            // geometry — the dot can never be hidden behind something.
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh != null)
            {
                Material m = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                m.SetColor("_Color", c);
                m.SetInt("_ZTest", (int)CompareFunction.Always);
                m.SetInt("_ZWrite", 0);
                m.SetInt("_Cull", (int)CullMode.Off);
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.renderQueue = 5000;
                return m;
            }
            Shader s2 = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            return new Material(s2) { color = c };
        }

        // True while the off-hand is within foregripBlockDistance of the foregrip trigger, so
        // loot pointing is suppressed there (Req 4). activeInHierarchy guards a stale ref left
        // over after the weapon was put away.
        private bool NearForegrip()
        {
            Transform grip = VRPlayerManager.leftHandGunIK;
            if (grip == null || !grip.gameObject.activeInHierarchy) return false;
            Transform lh = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.LeftHand != null
                ? VRGlobals.vrPlayer.LeftHand.transform : null;
            if (lh == null) return false;
            return Vector3.Distance(lh.position, grip.position) < foregripBlockDistance;
        }

        // The cone/probe origin: the centroid of the FINGER-BASE joints (the knuckle line at the
        // front of the palm — the actual grip point, well forward of the wrist), + a hand-frame
        // nudge (conePalmLocalOffset). The palm BONE sits close to the wrist so it read as
        // "coming from the wrist"; the knuckles are the real palm. Falls back to the palm bone,
        // then the cone transform.
        private Vector3 PalmOrigin()
        {
            if (!fingersResolved) ResolveLeftFingers();
            Transform palmBone = InitVRPatches.leftPalm;
            if (leftFingerJoints != null && leftFingerJoints.Length > 0)
            {
                Vector3 sum = Vector3.zero;
                int n = 0;
                for (int f = 0; f < leftFingerJoints.Length; f++)
                {
                    // Skip the thumb knuckle — it's off to the side and would pull the origin sideways.
                    if (leftFingerIsThumb != null && f < leftFingerIsThumb.Length && leftFingerIsThumb[f])
                        continue;
                    Transform[] joints = leftFingerJoints[f];
                    if (joints != null && joints.Length > 0 && joints[0] != null)
                    {
                        sum += joints[0].position;
                        n++;
                    }
                }
                if (n > 0)
                {
                    Vector3 c = sum / n;
                    if (palmBone != null) c += palmBone.rotation * conePalmLocalOffset;
                    return c;
                }
            }
            if (palmBone != null) return palmBone.TransformPoint(conePalmLocalOffset);
            return coneAim != null ? coneAim.position : VRGlobals.vrPlayer.LeftHand.transform.position;
        }

        private void ClearDots()
        {
            DestroyConeViz();
            if (lootDots.Count == 0)
            {
                lastMain = null;
                return;
            }
            foreach (KeyValuePair<LootItem, DotState> kv in lootDots)
                if (kv.Value != null && kv.Value.go != null)
                    Destroy(kv.Value.go);
            lootDots.Clear();
            lastMain = null;
        }

        //--- Cone direction gizmo (debugLootPointer) ---------------------------------
        // A cyan sphere at the palm origin + a ray down the cone axis + an orange end sphere
        // at coneRange, so the aim can be SEEN and coneAimLocalEuler tuned live in the headset.
        private void UpdateConeViz(Vector3 palm, Vector3 aim)
        {
            if (!debugLootPointer)
            {
                DestroyConeViz();
                return;
            }
            Color axisC = new Color(0.2f, 0.9f, 1f, 0.9f);
            if (coneOriginViz == null) coneOriginViz = CreateMarker(PrimitiveType.Sphere, axisC);
            if (coneDirViz == null) coneDirViz = CreateMarker(PrimitiveType.Cube, axisC);
            if (coneEndViz == null) coneEndViz = CreateMarker(PrimitiveType.Sphere, new Color(1f, 0.45f, 0.1f, 0.9f));

            coneOriginViz.transform.position = palm;
            coneOriginViz.transform.localScale = Vector3.one * 0.02f;

            Vector3 dir = aim.sqrMagnitude > 1e-6f ? aim : Vector3.forward;
            coneDirViz.transform.position = palm + dir * (coneRange * 0.5f);
            coneDirViz.transform.rotation = Quaternion.LookRotation(dir);
            coneDirViz.transform.localScale = new Vector3(0.004f, 0.004f, coneRange);

            coneEndViz.transform.position = palm + dir * coneRange;
            coneEndViz.transform.localScale = Vector3.one * 0.03f;
        }

        private void DestroyConeViz()
        {
            if (coneOriginViz != null) Destroy(coneOriginViz);
            if (coneDirViz != null) Destroy(coneDirViz);
            if (coneEndViz != null) Destroy(coneEndViz);
            coneOriginViz = coneDirViz = coneEndViz = null;
        }

        //--- Cone aim transform ------------------------------------------------------
        private void EnsureConeAim(Transform hand)
        {
            if (coneAim == null || coneAim.parent != hand)
            {
                if (coneAim != null)
                    Destroy(coneAim.gameObject);
                coneAim = new GameObject("LootConeAim").transform;
                coneAim.SetParent(hand, false);
            }
            // Re-apply every frame so live-tuning the statics takes effect immediately.
            coneAim.localPosition = coneAimLocalPos;
            coneAim.localEulerAngles = coneAimLocalEuler;
        }

        //--- Summon ------------------------------------------------------------------
        private void BeginSummon(LootItem item, Vector3 palm, Vector3 aim)
        {
            RemoveFikaSyncer(item); // stop FIKA syncing an item we've taken control of
            cachedRigidbody = item.GetComponent<Rigidbody>() ?? item.gameObject.AddComponent<Rigidbody>();
            item._rigidBody = cachedRigidbody;
            cachedRigidbody.isKinematic = true;
            cachedRigidbody.detectCollisions = true;
            cachedRigidbody.useGravity = false;
            cachedRigidbody.mass = item.item_0.TotalWeight;

            cachedHand = VRGlobals.vrPlayer.LeftHand.transform;
            Vector3 itemPos = item.transform.position;

            // Rest pose: the item's collider surface nearest the palm should sit on the palm,
            // then sink summonRestDepth further in so it reads as held. NearestBoxSurfacePoint
            // also handles the hand being INSIDE the box (point-blank): it pushes the item out
            // to its near face instead of leaving the palm buried at the center.
            Vector3 surface = item._boundCollider != null ? NearestBoxSurfacePoint(item._boundCollider, palm) : itemPos;
            Vector3 moveDir = palm - surface;
            if (moveDir.sqrMagnitude < 1e-6f) moveDir = palm - itemPos;
            if (moveDir.sqrMagnitude < 1e-6f) moveDir = -aim;
            moveDir.Normalize();
            Vector3 targetWorld = itemPos + (palm - surface) + moveDir * summonRestDepth;

            float dist = Vector3.Distance(itemPos, targetWorld);
            currentSummonDuration = Mathf.Clamp(dist * summonSecPerMeter, summonMinDuration, summonMaxDuration);

            cachedOriginalParent = item.transform.parent;
            item.transform.SetParent(cachedHand, worldPositionStays: true);

            summonStartOffset = cachedHand.InverseTransformPoint(item.transform.position);
            summonRestOffset = cachedHand.InverseTransformPoint(targetWorld);
            cachedGrabRotation = Quaternion.Inverse(cachedHand.rotation) * item.transform.rotation;
            cachedItemPosition = item.transform.position;

            heldItem = item;
            heldItemGrabTime = Time.time; // FIKA steal arbitration: newest grab wins (see FIKASupport)
            summonT = 0f;
            isSummoningLoot = true;
            isItemInitialized = true; // we did our own init; keep UpdateHeldItemPosition from re-init

            try { VRGlobals.player?.GetComponent<GamePlayerOwner>()?.ClearInteractionState(); } catch { }
            NotifyWeightChanged();
            ClearDots();

            SteamVR_Actions._default.Haptic.Execute(0, 0.08f, 1, 0.5f, secondaryInputSource);
        }

        private void UpdateLootSummon()
        {
            // Item destroyed (taken/streamed out) mid-flight, or hand lost — bail cleanly.
            if (heldItem == null || cachedHand == null)
            {
                isSummoningLoot = false;
                return;
            }
            if (!HeldItemAlive()) { CleanupAfterHeldItemGone(); return; }

            summonT += Time.deltaTime / Mathf.Max(0.0001f, currentSummonDuration);
            float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(summonT));

            Vector3 localTarget = Vector3.Lerp(summonStartOffset, summonRestOffset, e);
            Vector3 naturalPos = cachedHand.TransformPoint(localTarget);
            // Same collide-and-slide the held item uses, so the pull can't drag through walls.
            Vector3 clamped = ClampPositionToGeometry(naturalPos, cachedItemPosition);
            heldItem.transform.position = clamped;
            cachedItemPosition = clamped;

            if (summonT >= 1f)
            {
                // Hand off to the normal held-item maintenance: cachedGrabOffset is the rest
                // pose relative to the hand, so UpdateHeldItemPosition holds it right there.
                cachedGrabOffset = cachedHand.InverseTransformPoint(heldItem.transform.position);
                isSummoningLoot = false;
            }
        }

        // Resets the cached held-item plumbing after the in-hand item was consumed by the
        // gaze "Take" (its GameObject is gone). Mirrors the tail of ForceDropHeldItem without
        // touching the (already destroyed) transform/rigidbody.
        private void CleanupAfterHeldItemGone()
        {
            heldItem = null;
            cachedRigidbody = null;
            cachedHand = null;
            isItemInitialized = false;
            isSummoningLoot = false;
            try { VRGlobals.player?.GetComponent<GamePlayerOwner>()?.InteractionsChangedHandler(); } catch { }
            NotifyWeightChanged();
        }

        // Where a dot sits: between the item center and its near box surface (dotSurfaceBias),
        // then pushed outward off the surface (dotSurfaceOffset) so it floats clear of the
        // mesh. outward = item-center -> surface (the local outward normal of the near face).
        private static Vector3 DotPos(Vector3 center, Vector3 surf, Vector3 palm)
        {
            Vector3 outward = surf - center;
            if (outward.sqrMagnitude < 1e-6f) outward = palm - surf; // tiny/round items: face the palm
            if (outward.sqrMagnitude < 1e-6f) outward = Vector3.up;
            return Vector3.Lerp(center, surf, dotSurfaceBias) + outward.normalized * dotSurfaceOffset;
        }

        //--- Left-hand "ready to grab" pose ------------------------------------------
        // FLATTENS the free off-hand (strips EFT's animated finger curl) and bends to an
        // absolute flex, more open when a loot dot is in view. Runs in LateUpdate (after EFT
        // animates the hand) and is accumulation-safe whether or not EFT re-stomps the bone.
        private void UpdateLeftHandFingers()
        {
            // When EFT plays a left-hand weapon animation (reload, examine, check ammo/chamber,
            // malfunction fix) it raises the "ReloadFloat" animator param to 1. VRPlayerManager
            // already releases leftArmIk on that signal so the ARM follows the animation — but our
            // baked finger pose runs in LateUpdate independently and would otherwise keep the
            // fingers welded to the open "ready" pose for the whole reload (the supporting/foregrip
            // case works only because isSupporting already makes us yield). Yield the fingers too:
            // free -> false eases handWeightBlend to 0 (handOpenLerp) and stops touching the bones,
            // handing the fingers back to the animation. Re-arms automatically when the float drops.
            bool leftHandAnimating = VRGlobals.player != null && VRGlobals.player.BodyAnimatorCommon != null
                && VRGlobals.player.BodyAnimatorCommon.GetFloat(VRPlayerManager.LEFT_HAND_ANIMATOR_HASH) == 1f;

            bool free = VRGlobals.inGame && !VRGlobals.menuOpen
                        && !VRGlobals.usingItem && !EatingInteractionController.ManualActive
                        && VRGlobals.vrPlayer != null && !VRGlobals.vrPlayer.isSupporting
                        && !leftHandAnimating;

            float wTarget = free ? Mathf.Clamp01(handPoseWeight) : 0f;
            float k = 1f - Mathf.Exp(-handOpenLerp * Time.deltaTime);
            handWeightBlend = Mathf.Lerp(handWeightBlend, wTarget, k);
            flexProgress = Mathf.Lerp(flexProgress, lootDotPresent ? 1f : 0f, k);

            if (!fingersResolved)
                ResolveLeftFingers();
            if (leftFingerJoints == null)
                return;

            // One-shot capture of whatever pose is rendered right now (set in UnityExplorer).
            if (captureRestPose)
            {
                captureRestPose = false;
                CaptureRestPose();
            }
            if (logRestPose)
            {
                logRestPose = false;
                LogRestPose();
            }

            // SURE WAY: replay a baked rest pose instead of the procedural flatten/flex/spread.
            if (restPoseEuler != null && restPoseEuler.Length > 0)
            {
                EditRestPoseLive();
                if (handWeightBlend >= 0.01f)
                    ReplayRestPose();
                return;
            }

            if (handWeightBlend < 0.01f)
                return; // fully back to the animated pose — stop touching the bones

            for (int f = 0; f < leftFingerJoints.Length; f++)
            {
                Transform[] joints = leftFingerJoints[f];
                float fingerSpread = (leftFingerSpread != null && f < leftFingerSpread.Length)
                    ? leftFingerSpread[f] * fingerSpreadDeg : 0f;
                // The thumb is flattened like the fingers but gets its OWN flex (it over-curls
                // toward the palm with the fingers' value).
                float flexBase = PerFingerFlex(f);
                for (int j = 0; j < joints.Length && j < 3; j++) // base, middle AND tip
                {
                    if (joints[j] == null) continue;
                    // Flex tapers down the finger; the FLATTEN (weight) is full on every joint
                    // so the tip straightens too. Spread (abduction) is only at the knuckle.
                    float flex = flexBase * Mathf.Pow(midJointScale, j) * flexSign;
                    float sp = (j == 0) ? fingerSpread : 0f;
                    ApplyFlatFlex(joints[j], flex, sp, handWeightBlend);
                }
            }
        }

        // Recover the ANIMATED pose for a bone: if EFT re-animated it this frame, its current
        // localRotation differs from our last output and IS the fresh pose; if it matches our
        // last output, EFT left it alone, so reuse the pose we last derived from (so our
        // override never accumulates frame to frame). Shared by the flatten and the rest-replay.
        private Quaternion RecoverAnimated(Transform t)
        {
            Quaternion cur = t.localRotation;
            if (fingerOutRot.TryGetValue(t, out Quaternion lastOut) && Quaternion.Angle(cur, lastOut) < 0.05f
                && fingerBaseRot.TryGetValue(t, out Quaternion lastBase))
                return lastBase;
            return cur;
        }

        // Capture the CURRENT rendered left-finger local rotations into restPoseEuler (+ a
        // paste-ready log) so a good pose can be frozen and replayed deterministically. Iterates
        // in the exact order ReplayRestPose does, so the flat indices line up.
        private void CaptureRestPose()
        {
            var list = new List<Vector3>();
            var sb = new System.Text.StringBuilder();
            sb.Append("[LootPointer] restPoseEuler = new Vector3[] {\n");
            for (int f = 0; f < leftFingerJoints.Length; f++)
            {
                Transform[] joints = leftFingerJoints[f];
                bool isThumb = leftFingerIsThumb != null && f < leftFingerIsThumb.Length && leftFingerIsThumb[f];
                for (int j = 0; j < joints.Length && j < 3; j++)
                {
                    Vector3 e = joints[j] != null ? joints[j].localEulerAngles : Vector3.zero;
                    list.Add(e);
                    sb.Append($"    new Vector3({e.x:F2}f, {e.y:F2}f, {e.z:F2}f), // {(isThumb ? "thumb" : ("finger" + f))} joint{j}\n");
                }
            }
            sb.Append("};");
            restPoseEuler = list.ToArray();
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        // Live per-joint editing: changing poseEditJoint LOADS that joint's current Euler so you
        // continue from where it is; otherwise your poseEditEuler edits write back into the baked
        // array each frame so the joint moves in real time.
        private void EditRestPoseLive()
        {
            if (poseEditJoint < 0 || restPoseEuler == null || poseEditJoint >= restPoseEuler.Length)
            {
                lastPoseEditJoint = -2;
                return;
            }
            if (poseEditJoint != lastPoseEditJoint)
            {
                lastPoseEditJoint = poseEditJoint;
                poseEditEuler = restPoseEuler[poseEditJoint]; // load for continued tweaking
            }
            else
            {
                restPoseEuler[poseEditJoint] = poseEditEuler; // write your live edits
            }
        }

        // Print the current baked restPoseEuler as a paste-ready array (after live editing).
        private void LogRestPose()
        {
            if (restPoseEuler == null) { Plugin.MyLog.LogInfo("[LootPointer] restPoseEuler is null."); return; }
            var sb = new System.Text.StringBuilder();
            sb.Append("[LootPointer] restPoseEuler = new Vector3[] {\n");
            for (int i = 0; i < restPoseEuler.Length; i++)
            {
                Vector3 e = restPoseEuler[i];
                sb.Append($"    new Vector3({e.x:F2}f, {e.y:F2}f, {e.z:F2}f), // index {i}\n");
            }
            sb.Append("};");
            Plugin.MyLog.LogInfo(sb.ToString());
        }

        // Per-finger flex delta (finger index f; 0 = thumb), eased between the idle and
        // "dot in view" arrays by flexProgress. Out-of-range fingers get 0.
        private float PerFingerFlex(int f)
        {
            float idle = (fingerFlexDeg != null && f < fingerFlexDeg.Length) ? fingerFlexDeg[f] : 0f;
            float ready = (fingerReadyFlexDeg != null && f < fingerReadyFlexDeg.Length) ? fingerReadyFlexDeg[f] : idle;
            return Mathf.Lerp(idle, ready, flexProgress);
        }

        // Drive every left-finger joint to its baked local rotation, with the flex (curl/open)
        // and spread (fan) knobs layered ON TOP as deltas, then Slerped from the live animation
        // by handWeightBlend. The baked pose is the natural rested BASE; idle/readyFlexDeg and
        // fingerSpreadDeg are small adjustments relative to it (0 = baked pose exactly). Same
        // iteration order as CaptureRestPose for index parity.
        private void ReplayRestPose()
        {
            int idx = 0;
            for (int f = 0; f < leftFingerJoints.Length; f++)
            {
                Transform[] joints = leftFingerJoints[f];
                float fingerSpread = (leftFingerSpread != null && f < leftFingerSpread.Length)
                    ? leftFingerSpread[f] * fingerSpreadDeg : 0f;
                float flexBase = PerFingerFlex(f);
                for (int j = 0; j < joints.Length && j < 3; j++, idx++)
                {
                    if (joints[j] == null || idx >= restPoseEuler.Length) continue;
                    Quaternion animated = RecoverAnimated(joints[j]);
                    float flex = flexBase * Mathf.Pow(midJointScale, j) * flexSign;
                    float sp = (j == 0) ? fingerSpread : 0f;
                    Quaternion target = Quaternion.Euler(restPoseEuler[idx])
                        * Quaternion.AngleAxis(flex, Vector3.forward)
                        * Quaternion.AngleAxis(sp, spreadAxis);
                    Quaternion outRot = Quaternion.Slerp(animated, target, handWeightBlend);
                    joints[j].localRotation = outRot;
                    fingerBaseRot[joints[j]] = animated;
                    fingerOutRot[joints[j]] = outRot;
                }
            }
        }

        // Replace the animated finger curl with an absolute flex (+ a lateral spread), blended
        // by weight.
        private void ApplyFlatFlex(Transform t, float flexDeg, float spreadDeg, float weight)
        {
            Quaternion animated = RecoverAnimated(t);

            // Swing-twist split around local Z (the finger flex axis): drop the animated curl
            // (twist) and replace it with our flat target flex, keeping the swing (natural splay)
            // so the hand stays oriented correctly while the curl is flattened out. Then fan the
            // finger sideways (abduction) around spreadAxis.
            Quaternion twist = TwistAround(animated, Vector3.forward);
            Quaternion swing = animated * Quaternion.Inverse(twist);
            Quaternion flatPose = swing
                * Quaternion.AngleAxis(flexDeg, Vector3.forward)
                * Quaternion.AngleAxis(spreadDeg, spreadAxis);

            Quaternion outRot = Quaternion.Slerp(animated, flatPose, weight);
            t.localRotation = outRot;
            fingerBaseRot[t] = animated;
            fingerOutRot[t] = outRot;
        }

        // The component of q that is a rotation about 'axis' (swing-twist decomposition).
        private static Quaternion TwistAround(Quaternion q, Vector3 axis)
        {
            Vector3 ra = new Vector3(q.x, q.y, q.z);
            Vector3 p = Vector3.Project(ra, axis);
            Quaternion twist = new Quaternion(p.x, p.y, p.z, q.w);
            if (twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w < 1e-8f)
                return Quaternion.identity; // q was ~180° about an axis perpendicular to 'axis'
            twist.Normalize();
            return twist;
        }

        // The finger chains under "Base HumanLPalm": each child is a finger root; walk its
        // first-child chain to collect up to 3 joints (base/middle/tip).
        private void ResolveLeftFingers()
        {
            fingersResolved = true;
            leftFingerJoints = null;
            Transform palm = InitVRPatches.leftPalm;
            if (palm == null)
            {
                Plugin.MyLog.LogWarning("[LootPointer] leftPalm not resolved yet — hand opener idle.");
                return;
            }

            var fingers = new List<Transform[]>();
            var thumbs = new List<bool>();
            var names = new List<string>();
            for (int i = 0; i < palm.childCount; i++)
            {
                Transform root = palm.GetChild(i);
                var joints = new List<Transform>();
                Transform j = root;
                while (j != null && joints.Count < 3)
                {
                    joints.Add(j);
                    j = j.childCount > 0 ? j.GetChild(0) : null;
                }
                if (joints.Count >= 1)
                {
                    fingers.Add(joints.ToArray());
                    thumbs.Add(root.name.IndexOf("thumb", System.StringComparison.OrdinalIgnoreCase) >= 0);
                    names.Add(root.name);
                }
            }
            if (fingers.Count > 0)
            {
                leftFingerJoints = fingers.ToArray();
                leftFingerIsThumb = thumbs.ToArray();

                // Signed centered ordinal among the NON-thumb fingers (e.g. 4 fingers ->
                // -1.5,-0.5,0.5,1.5), so the knuckle fan splays outward from the middle.
                int nonThumb = 0;
                for (int i = 0; i < thumbs.Count; i++) if (!thumbs[i]) nonThumb++;
                float center = (nonThumb - 1) * 0.5f;
                leftFingerSpread = new float[fingers.Count];
                int k = 0;
                for (int i = 0; i < fingers.Count; i++)
                    leftFingerSpread[i] = thumbs[i] ? 0f : (k++ - center);
            }
            Plugin.MyLog.LogInfo($"[LootPointer] left fingers under '{palm.name}': {fingers.Count} [{string.Join(", ", names)}]");
        }

        // The point on a (possibly oriented) BoxCollider surface nearest a world point. Unlike
        // Collider.ClosestPoint this returns a SURFACE point even when the query point is INSIDE
        // the box — it pushes the least-penetrated axis out to its face — which is what makes a
        // point-blank grab snap to the outer part of the collider instead of staying at center.
        // True if a world point is inside the (oriented) box collider — same local-frame test as
        // NearestBoxSurfacePoint's inside branch, used to flag a point-blank physical grab.
        private static bool IsInsideBox(BoxCollider box, Vector3 worldPoint)
        {
            Vector3 local = box.transform.InverseTransformPoint(worldPoint) - box.center;
            Vector3 ext = box.size * 0.5f;
            return Mathf.Abs(local.x) <= ext.x && Mathf.Abs(local.y) <= ext.y && Mathf.Abs(local.z) <= ext.z;
        }

        private static Vector3 NearestBoxSurfacePoint(BoxCollider box, Vector3 worldPoint)
        {
            Transform t = box.transform;
            Vector3 local = t.InverseTransformPoint(worldPoint) - box.center;
            Vector3 ext = box.size * 0.5f;

            bool inside = Mathf.Abs(local.x) <= ext.x && Mathf.Abs(local.y) <= ext.y && Mathf.Abs(local.z) <= ext.z;
            Vector3 s = local;
            if (inside)
            {
                float dx = ext.x - Mathf.Abs(local.x);
                float dy = ext.y - Mathf.Abs(local.y);
                float dz = ext.z - Mathf.Abs(local.z);
                if (dx <= dy && dx <= dz) s.x = (local.x >= 0f ? 1f : -1f) * ext.x;
                else if (dy <= dz) s.y = (local.y >= 0f ? 1f : -1f) * ext.y;
                else s.z = (local.z >= 0f ? 1f : -1f) * ext.z;
            }
            else
            {
                s.x = Mathf.Clamp(local.x, -ext.x, ext.x);
                s.y = Mathf.Clamp(local.y, -ext.y, ext.y);
                s.z = Mathf.Clamp(local.z, -ext.z, ext.z);
            }
            return t.TransformPoint(s + box.center);
        }
    }
}
