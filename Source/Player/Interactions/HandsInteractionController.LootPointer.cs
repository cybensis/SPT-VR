using System.Collections.Generic;
using EFT;
using EFT.Interactive;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Player.VRManager;
using UnityEngine;
using UnityEngine.Rendering;
using Valve.VR;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Source.Player.Interactions
{
    // Palm-cone loose-loot pointer + summon. A short cone out of the palm puts a white dot on every loose item inside it;
    // a thin probe down the cone axis picks the "main" item (its dot grows). Grip pulls the main item to the palm, where
    // its collider surface rests on the palm (collide-and-slide keeps it out of walls). Looking at the held item shows
    // the native name + "Take" menu (UIPatches.Raycaster, gated by suppressWorldLootMenu).
    internal partial class HandsInteractionController
    {
        //--- Tunables ----------------------------------------------------------------

        // true: loose items in the world don't pop the gaze "Take" menu, only the item in your hand does.
        // Read by UIPatches.Raycaster. false = gaze "Take" on world items.
        public static bool suppressWorldLootMenu = true;

        // Cone reach (m) and half-angle (deg). Items within pointBlankRange count at any angle.
        public static float coneRange = 0.6f;
        public static float coneHalfAngle = 35f;
        public static float pointBlankRange = 0.13f;

        // Radius of the swept-sphere center probe that selects the main item.
        public static float capsuleRadius = 0.05f;

        // The cone aim transform's local pose under the hand. Only its orientation matters (coneAimLocalEuler = the aim
        // direction); the origin comes from the palm, below.
        public static Vector3 coneAimLocalPos = new Vector3(0f, 0f, 0f);
        public static Vector3 coneAimLocalEuler = new Vector3(75f, 266f, 0f);

        // Nudge of the cone origin, in the palm bone's local frame (InitVRPatches.leftPalm = "Base HumanLPalm"). The origin
        // is taken from the palm because the hand's rig transform is the wrist (~17 cm back from the controller).
        public static Vector3 conePalmLocalOffset = Vector3.zero;

        // Dot sizes (world diameters) and how fast they ease toward their target size.
        public static float dotMinSize = 0.012f;
        public static float dotMaxSize = 0.030f;
        public static float dotGrowSpeed = 14f;
        // 0 = dot at the item center (can be buried in big items); 1 = on the near surface facing the palm.
        public static float dotSurfaceBias = 0.7f;
        // Extra push outward off the box surface (m), along item-center -> surface, so the dot floats clear of the mesh
        // (the box surface can sit inside a concave item).
        public static float dotSurfaceOffset = 0.03f;

        // "Main" pick: the smallest angle wins; distance is a soft tie-break (degrees-equivalent per meter).
        public static float centerDistWeight = 25f;

        // Summon duration scales with pull distance (clamped). summonRestDepth sinks the item this far (m) past the
        // palm-surface contact so it reads as gripped.
        public static float summonSecPerMeter = 0.5f;
        public static float summonMinDuration = 0.04f;
        public static float summonMaxDuration = 0.35f;
        public static float summonRestDepth = 0.02f;

        public static float handPoseWeight = 1f;

        // Per-finger flex delta (deg) on the baked pose, index 0 = thumb, then index/middle/ring/pinky. Eases from
        // fingerFlexDeg (idle) to fingerReadyFlexDeg (a loot dot in view). 0 = the baked pose; negative opens, positive curls.
        // flexSign flips the direction; midJointScale tapers the delta per joint.
        public static float[] fingerFlexDeg = new float[] { 5f, -15f, -20f, -27f, -27f };
        public static float[] fingerReadyFlexDeg = new float[] { 0f, -25f, -25f, -25f, -25f };
        public static float flexSign = 1f;
        public static float midJointScale = 0.6f;
        public static float handOpenLerp = 10f;

        // Lateral finger spread: degrees between adjacent fingers, applied at the knuckle around spreadAxis (local).
        public static float fingerSpreadDeg = -5f; // delta on the baked pose (negative = fan out)
        public static Vector3 spreadAxis = new Vector3(0f, 1f, 0f);

        public static bool captureRestPose = false;
        // The base pose, baked from EFT's no-weapon hand animation. Flex/spread layer on top as deltas (ReplayRestPose).
        // finger0 = thumb.
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

        // Live per-joint editor for the baked pose. poseEditJoint = the flat joint index (-1 = off; indices match the
        // CaptureRestPose log labels): changing it loads that joint's Euler into poseEditEuler, edits to poseEditEuler
        // write back live. logRestPose = true prints restPoseEuler as a paste-ready array.
        public static int poseEditJoint = -1;
        public static Vector3 poseEditEuler = Vector3.zero;
        public static bool logRestPose = false;

        // Loot pointing is blocked while the off hand is within this distance (m) of the foregrip
        // (the two-hand support trigger is ~0.1-0.175 m).
        public static float foregripBlockDistance = 0.2f;

        // FIKA co-op: seconds between broadcasts of a held item's position (0.04 = 25/s; FIKA's own item sync runs ~50/s).
        public static float lootSyncInterval = 0.04f;

        // The cone aim euler for the dominant hand (the hands' rig transforms have different local frames).
        // Tune with debugLootPointer + debugVizDominantHand.
        public static Vector3 coneAimLocalEulerRight = new Vector3(75f, 94f, 0f);

        //--- Runtime state -----------------------------------------------------------
        private readonly Transform[] coneAims = new Transform[2];   // [0]=off hand, [1]=dominant
        private readonly LootItem[] lastMains = new LootItem[2];
        private bool heldInDominantHand;                            // which grip drops the held item
        public static bool debugVizDominantHand = false;            // cone gizmo shows the right hand instead of the left
        // 0 = auto: EFT's own interaction raycast mask (the one the head-gaze raycast in UIPatches.Raycaster uses), then
        // filtered to LootItem. Non-zero overrides. Layer 3 alone finds nothing.
        public static int lootLayerMask = 0;
        private int currentLootMask;
        // Logs overlap/candidate counts once a second.
        public static bool debugLootPointer = false;
        private float lastPointerLog;
        private int lastOverlapCount;

        private bool isSummoningLoot;
        // Time.time of the most recent grab. FIKASupport's steal arbitration reads it: the newest grabber wins a contested
        // item (a grab older than stealGraceTime yields).
        public float heldItemGrabTime;
        private float summonT;
        private float currentSummonDuration;
        private Vector3 summonStartOffset, summonRestOffset;

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
        // Runs the pointer for both hands (the dominant hand only while it's off the gun: empty hands / gun slung or
        // support-held). Dots are swept once, each hand contributes, then one finalize pass renders them.
        private void UpdateLootPointer()
        {
            lootDotPresent = false; // set true below once there are candidates

            // A body grab owns the off-hand grip — don't also point at / summon loose loot.
            if (isDraggingBody || bodyInteractionArmed)
            {
                ClearDots();
                return;
            }

            // The gaze "Take" can consume the held item into the inventory: item_0 nulls and the GameObject is destroyed
            // shortly after. Reset our refs so the weight/interaction state clears and pointing resumes.
            if (!ReferenceEquals(heldItem, null) && !HeldItemAlive())
                CleanupAfterHeldItemGone();

            bool usingOrEating = VRGlobals.usingItem || EatingInteractionController.ManualActive;

            // A use/eat started while a loot item is still held. deferUseLootRelease: hide the item and decide its fate when
            // the use ends (stowing it mid-eat moves the item's address under the in-place use and stops the eat).
            // Otherwise it is stowed/dropped right away.
            if (usingOrEating && HeldItemAlive())
            {
                if (deferUseLootRelease) BeginDeferredUseRelease();
                else ReleaseHeldForUse();
            }

            // Pointing only happens with a free hand — not while holding/summoning, not mid use/eat.
            if (heldItem != null || isSummoningLoot || usingOrEating)
            {
                ClearDots();
                return;
            }

            // Mirror the game's interactable mask (what the gaze raycast hits) unless overridden.
            currentLootMask = lootLayerMask != 0 ? lootLayerMask : EFT.GameWorld._interactiveLootMaskWPlayer;

            SweepDots();
            PointWithHand(dominant: false);
            PointWithHand(dominant: true);
            FinalizeDots();
        }

        // The dominant hand is free for world interactions (loot pointing, doors, containers, corpse taps) when it's off the
        // gun (empty hands / gun slung or support-held), not holding a grenade, outside the holster zones (their grip
        // gestures own the hand there) and not reaching for the slung gun's regrab.
        private bool DominantHandFree()
        {
            bool handFree = ChestHang.Detached || (VRGlobals.player != null && VRGlobals.player.HandsIsEmpty);
            // (not on the charging handle: off the grip, but holding the gun by it through a magazine swap, ChestHang.HandleHeld)
            return handFree && !TarkovVR.Source.Weapons.VRReloadController.HandleInGunHand
                && !TarkovVR.Patches.Core.Player.WeaponPatches.grenadeEquipped
                && !rightHandState.inBackHolster && !rightHandState.inSidearmHolster
                && !ChestHang.HandInRegrabZone
                && !BodyWeapons.HandAtBodyGun && !BodyWeapons.Switching && !BodyWeapons.GrabbedThisPress;
        }

        // One hand's full pointer pass: eligibility -> cone -> candidates -> main -> dots/haptic -> grip = summon (to that hand).
        private void PointWithHand(bool dominant)
        {
            if (VRGlobals.vrPlayer == null)
                return;
            GameObject handObject = dominant ? VRGlobals.vrPlayer.RightHand : VRGlobals.vrPlayer.LeftHand;
            Transform hand = handObject != null ? handObject.transform : null;
            if (hand == null)
                return;

            if (dominant)
            {
                // Only when the hand is free (DominantHandFree) and no corpse tap owns its grip.
                if (!DominantHandFree() || rightCorpseTapArmed)
                    return;
            }
            // A hand on a door (PhysicalDoors) points at nothing
            if (PhysicalDoors.HoldsDoor(dominant))
                return;
            else
            {
                // Off hand: not near the foregrip trigger, nor holding / reaching for a magazine. Nor with the gun in the left
                // hand: the off hand is the right one then, and this pointer is built on the left palm (the hand on the gun).
                // Nor at the gun hand's grip: a grip press there takes the gun into this hand (ChestHang)
                if (NearForegrip() || VRReloadController.OwnsOffHand || Handedness.GunInLeftHand || ChestHang.OffHandAtGunGrip)
                    return;
            }

            EnsureConeAim(dominant, hand);
            InteractionState nearState = dominant ? rightHandState : leftHandState;

            // Origin from the palm (the rig transform is the wrist); direction from the tunable coneAim child of the hand.
            Vector3 palm = PalmOrigin(dominant);
            Vector3 aim = coneAims[dominant ? 1 : 0].forward;

            GatherCandidates(palm, aim, nearState);
            LootItem main = PickMain(palm, aim, nearState);
            ContributeDots(main);
            if ((dominant && debugVizDominantHand) || (!dominant && !debugVizDominantHand))
                UpdateConeViz(palm, aim); // gizmo for tuning the aim direction
            lootDotPresent |= lootCandidates.Count > 0; // drives the "ready to grab" hand pose

            if (debugLootPointer && Time.time - lastPointerLog > 1f)
            {
                lastPointerLog = Time.time;
                Plugin.MyLog.LogInfo($"[LootPointer] hand={(dominant ? "dom" : "off")} mask=0x{currentLootMask:X} overlap={lastOverlapCount} candidates={lootCandidates.Count} main={(main != null ? main.name : "null")}");
            }

            // Light pulse when the highlighted item changes.
            int slot = dominant ? 1 : 0;
            SteamVR_Input_Sources source = dominant ? primaryInputSource : secondaryInputSource;
            if (main != null && main != lastMains[slot])
                VRInput.Haptic.Execute(0, 0.04f, 1, 0.25f, source);
            lastMains[slot] = main;

            // This hand's grip = summon. Guarded on heldItem == null so it never double-grabs.
            VRBoolean grip = dominant ? primaryHandGrip : secondaryHandGrip;
            if (grip.stateDown && debugLootPointer)
                Plugin.MyLog.LogInfo($"[LootPointer] GRIP hand={(dominant ? "dom" : "off")} main={(main != null ? main.name : "null")} candidates={lootCandidates.Count} near={(nearState.lootItem != null ? nearState.lootItem.name : "null")} held={(heldItem != null ? heldItem.name : "null")}");
            if (main != null && heldItem == null && grip.stateDown)
                BeginSummon(main, palm, aim, dominant);
        }

        //--- Detection ---------------------------------------------------------------
        private void GatherCandidates(Vector3 palm, Vector3 aim, InteractionState nearState)
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

                // The hand is inside this item's collider: valid at any angle/distance (a large item's centre can be outside
                // the cone while the hand is buried in it).
                bool inside = li._boundCollider != null && IsInsideBox(li._boundCollider, palm);

                // Otherwise: centre within pointBlankRange at any angle, or inside the cone.
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

            // The 20 Hz reach-in detector (UpdateCollisionStates) already resolves this hand's point-blank loot item: always
            // include it, so a close grab works whatever the cone/mask.
            LootItem near = nearState != null ? nearState.lootItem : null;
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

        private LootItem PickMain(Vector3 palm, Vector3 aim, InteractionState nearState)
        {
            // A physical grab beats the cone: the item the hand is on wins over whatever the probe hits further out.
            // First the reach-in detector's item (hand within 0.125 m).
            LootItem near = nearState != null ? nearState.lootItem : null;
            if (near != null && near.Item != null && lootCandidateSet.Contains(near))
                return near;

            // Then the item the palm is inside (closest centre if several), else the closest candidate within pointBlankRange.
            LootItem insideClosest = null;
            float bestInside = float.MaxValue;
            LootItem closestPointBlank = null;
            float bestPB = pointBlankRange;
            for (int i = 0; i < lootCandidates.Count; i++)
            {
                LootCand candidate = lootCandidates[i];
                if (candidate.inside && candidate.dist < bestInside)
                {
                    bestInside = candidate.dist;
                    insideClosest = candidate.item;
                }
                else if (candidate.dist <= bestPB)
                {
                    bestPB = candidate.dist;
                    closestPointBlank = candidate.item;
                }
            }
            if (insideClosest != null)
                return insideClosest;
            if (closestPointBlank != null)
                return closestPointBlank;

            // Cone (aiming from a distance): the center probe wins if it lands on a candidate.
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
                LootCand candidate = lootCandidates[i];
                float score = candidate.ang + candidate.dist * centerDistWeight;
                if (score < best)
                {
                    best = score;
                    main = candidate.item;
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
                // A Corpse is a LootItem. Bodies are grabbed through BodyGrab, never the loot pointer: no dot on a body.
                if (li is Corpse) li = null;
                lootColliderCache[col] = li; // may cache null (collider with no LootItem) on purpose
            }
            return li;
        }

        //--- Dots --------------------------------------------------------------------
        // Sweep -> per-hand contribute -> finalize: both hands feed one shared dot set (an item pointed by both keeps the
        // larger size).
        private void SweepDots()
        {
            foreach (KeyValuePair<LootItem, DotState> entry in lootDots)
                entry.Value.active = false;
        }

        private void ContributeDots(LootItem main)
        {
            for (int i = 0; i < lootCandidates.Count; i++)
            {
                LootCand candidate = lootCandidates[i];
                if (!lootDots.TryGetValue(candidate.item, out DotState ds))
                {
                    ds = new DotState { go = CreateDot(), scale = 0f };
                    lootDots[candidate.item] = ds;
                }
                float target = candidate.item == main ? dotMaxSize : dotMinSize;
                ds.target = ds.active ? Mathf.Max(ds.target, target) : target;
                ds.active = true;
                ds.pos = candidate.dotPos;
            }
        }

        private void FinalizeDots()
        {
            // Frame-rate-independent ease toward the target size; cull faded-out / dead dots.
            float k = 1f - Mathf.Exp(-dotGrowSpeed * Time.deltaTime);
            lootDotRemove.Clear();
            foreach (KeyValuePair<LootItem, DotState> entry in lootDots)
            {
                DotState ds = entry.Value;
                if (entry.Key == null || ds.go == null)
                {
                    if (ds.go != null) Destroy(ds.go);
                    lootDotRemove.Add(entry.Key);
                    continue;
                }

                float tgt = ds.active ? ds.target : 0f;
                ds.scale = Mathf.Lerp(ds.scale, tgt, k);
                if (!ds.active && ds.scale < 0.0006f)
                {
                    Destroy(ds.go);
                    lootDotRemove.Add(entry.Key);
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
        private static GameObject CreateMarker(PrimitiveType type, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = "VRLootGizmo";
            Destroy(go.GetComponent<Collider>());

            // Internal-Colored multiplies vertex color by _Color and primitive meshes have no color channel: paint the
            // vertices white or the marker renders black. .mesh is a unique instance, the shared primitive mesh is untouched.
            MeshFilter meshFilter = go.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.mesh != null)
            {
                Mesh mesh = meshFilter.mesh;
                var cols = new Color[mesh.vertexCount];
                for (int i = 0; i < cols.Length; i++) cols[i] = Color.white;
                mesh.colors = cols;
            }

            Renderer partRenderer = go.GetComponent<Renderer>();
            partRenderer.shadowCastingMode = ShadowCastingMode.Off;
            partRenderer.receiveShadows = false;
            partRenderer.material = MakeMarkerMaterial(color);
            return go;
        }

        private static Material MakeMarkerMaterial(Color color)
        {
            // "Hidden/Internal-Colored" honors _ZTest (Sprites/Default silently ignores it): ZTest Always draws the marker on
            // top of all geometry.
            Shader sh = Shader.Find("Hidden/Internal-Colored");
            if (sh != null)
            {
                Material m = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                m.SetColor("_Color", color);
                m.SetInt("_ZTest", (int)CompareFunction.Always);
                m.SetInt("_ZWrite", 0);
                m.SetInt("_Cull", (int)CullMode.Off);
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.renderQueue = 5000;
                return m;
            }
            Shader s2 = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            return new Material(s2) { color = color };
        }

        // True while the off hand is within foregripBlockDistance of the foregrip trigger (loot pointing is suppressed
        // there). activeInHierarchy guards a stale ref left over after the weapon was put away.
        private bool NearForegrip()
        {
            Transform grip = VRPlayerManager.leftHandGunIK;
            if (grip == null || !grip.gameObject.activeInHierarchy) return false;
            Transform lh = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.LeftHand != null
                ? VRGlobals.vrPlayer.LeftHand.transform : null;
            if (lh == null) return false;
            return Vector3.Distance(lh.position, VRPlayerManager.SupportGripPoint()) < foregripBlockDistance;
        }

        // A hand's thumb and index fingertips and the index's base knuckle (the index runs base -> tip), for a small item held in a
        // pinch (VRReloadController's single rounds). Fingertips as in GetHandTouchPoints; the index is EFT's Digit2.
        internal bool TryGetPinch(bool dominant, out Vector3 thumbTip, out Vector3 indexTip, out Vector3 indexBase)
        {
            thumbTip = indexTip = indexBase = Vector3.zero;
            if (dominant)
            {
                if (!rightFingersResolved) ResolveRightFingers();
            }
            else if (!fingersResolved) ResolveLeftFingers();
            Transform[][] fingerJoints = dominant ? rightFingerJoints : leftFingerJoints;
            bool[] isThumb = dominant ? rightFingerIsThumb : leftFingerIsThumb;
            if (fingerJoints == null || isThumb == null)
                return false;
            Transform[] thumb = null, index = null, firstFinger = null;
            for (int finger = 0; finger < fingerJoints.Length && finger < isThumb.Length; finger++)
            {
                Transform[] joints = fingerJoints[finger];
                if (joints == null || joints.Length == 0 || joints[0] == null || joints[joints.Length - 1] == null)
                    continue;
                if (isThumb[finger])
                    thumb = thumb ?? joints;
                else if (joints[0].name.IndexOf("Digit2", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    index = index ?? joints;
                else
                    firstFinger = firstFinger ?? joints;
            }
            index = index ?? firstFinger;
            if (thumb == null || index == null)
                return false;
            thumbTip = thumb[thumb.Length - 1].TransformPoint(-0.025f, 0f, 0f);
            indexTip = index[index.Length - 1].TransformPoint(-0.025f, 0f, 0f);
            indexBase = index[0].position;
            return true;
        }

        // A hand's palm, knuckles and fingertips, for a zone the hand counts as in as soon as any of it is. The rig has no
        // bone at the fingertip: about 2.5cm past the last joint, along the finger.
        internal void GetHandTouchPoints(bool dominant, List<Vector3> points)
        {
            points.Clear();
            points.Add(PalmOrigin(dominant));
            if (dominant)
            {
                if (!rightFingersResolved) ResolveRightFingers();
            }
            else if (!fingersResolved) ResolveLeftFingers();
            Transform[][] fingerJoints = dominant ? rightFingerJoints : leftFingerJoints;
            if (fingerJoints == null)
                return;
            foreach (Transform[] joints in fingerJoints)
            {
                if (joints == null || joints.Length == 0)
                    continue;
                if (joints[0] != null)
                    points.Add(joints[0].position);
                Transform lastJoint = joints[joints.Length - 1];
                if (lastJoint != null)
                    points.Add(lastJoint.TransformPoint(-0.025f, 0f, 0f));
            }
        }

        // The cone/probe origin: the centroid of the finger-base joints (the knuckle line, well forward of the wrist; the
        // palm bone sits close to the wrist) + conePalmLocalOffset. Falls back to the palm bone, then the cone transform.
        internal Vector3 PalmOrigin(bool dominant)
        {
            // The thumb is skipped: it's off to the side and would pull the origin sideways.
            if (!fingersResolved) ResolveLeftFingers();
            if (!rightFingersResolved) ResolveRightFingers();
            Transform[][] fingers = dominant ? rightFingerJoints : leftFingerJoints;
            bool[] thumbs = dominant ? rightFingerIsThumb : leftFingerIsThumb;
            Transform palmBone = dominant
                ? (VRGlobals.ikManager?.rightArmIk?.solver?.bone3 != null ? VRGlobals.ikManager.rightArmIk.solver.bone3.transform : null)
                : InitVRPatches.leftPalm;
            if (fingers != null && fingers.Length > 0)
            {
                Vector3 sum = Vector3.zero;
                int n = 0;
                for (int fingerIndex = 0; fingerIndex < fingers.Length; fingerIndex++)
                {
                    if (thumbs != null && fingerIndex < thumbs.Length && thumbs[fingerIndex])
                        continue;
                    Transform[] joints = fingers[fingerIndex];
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
            Transform cone = coneAims[dominant ? 1 : 0];
            if (cone != null) return cone.position;
            GameObject handObject = dominant ? VRGlobals.vrPlayer.RightHand : VRGlobals.vrPlayer.LeftHand;
            return handObject != null ? handObject.transform.position : Vector3.zero;
        }

        private void ClearDots()
        {
            DestroyConeViz();
            lastMains[0] = null;
            lastMains[1] = null;
            if (lootDots.Count == 0)
                return;
            foreach (KeyValuePair<LootItem, DotState> entry in lootDots)
                if (entry.Value != null && entry.Value.go != null)
                    Destroy(entry.Value.go);
            lootDots.Clear();
        }

        //--- Cone direction gizmo (debugLootPointer) ---------------------------------
        // A cyan sphere at the palm origin + a ray down the cone axis + an orange end sphere at coneRange, for tuning
        // coneAimLocalEuler live.
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

            Vector3 direction = aim.sqrMagnitude > 1e-6f ? aim : Vector3.forward;
            coneDirViz.transform.position = palm + direction * (coneRange * 0.5f);
            coneDirViz.transform.rotation = Quaternion.LookRotation(direction);
            coneDirViz.transform.localScale = new Vector3(0.004f, 0.004f, coneRange);

            coneEndViz.transform.position = palm + direction * coneRange;
            coneEndViz.transform.localScale = Vector3.one * 0.03f;
        }

        private void DestroyConeViz()
        {
            if (coneOriginViz != null) Destroy(coneOriginViz);
            if (coneDirViz != null) Destroy(coneDirViz);
            if (coneEndViz != null) Destroy(coneEndViz);
            coneOriginViz = coneDirViz = coneEndViz = null;
        }

        //--- Cone aim transforms (one per hand) --------------------------------------
        private void EnsureConeAim(bool dominant, Transform hand)
        {
            int slot = dominant ? 1 : 0;
            Transform cone = coneAims[slot];
            if (cone == null || cone.parent != hand)
            {
                if (cone != null)
                    Destroy(cone.gameObject);
                cone = new GameObject(dominant ? "LootConeAimR" : "LootConeAim").transform;
                cone.SetParent(hand, false);
                coneAims[slot] = cone;
            }
            // Re-apply every frame so live-tuning the statics takes effect immediately.
            cone.localPosition = coneAimLocalPos;
            cone.localEulerAngles = dominant ? coneAimLocalEulerRight : coneAimLocalEuler;
        }

        //--- Summon ------------------------------------------------------------------
        private void BeginSummon(LootItem item, Vector3 palm, Vector3 aim, bool dominant)
        {
            RemoveFikaSyncer(item); // stop FIKA syncing an item we've taken control of
            cachedRigidbody = item.GetComponent<Rigidbody>() ?? item.gameObject.AddComponent<Rigidbody>();
            item._rigidBody = cachedRigidbody;
            cachedRigidbody.isKinematic = true;
            cachedRigidbody.detectCollisions = true;
            cachedRigidbody.useGravity = false;
            cachedRigidbody.mass = item._item.TotalWeight;

            heldInDominantHand = dominant;
            cachedHand = (dominant ? VRGlobals.vrPlayer.RightHand : VRGlobals.vrPlayer.LeftHand).transform;
            Vector3 itemPos = item.transform.position;

            // Rest pose: the collider surface nearest the palm sits on the palm, sunk summonRestDepth further in.
            // NearestBoxSurfacePoint also handles the hand inside the box: the item is pushed out to its near face.
            Vector3 surface = item._boundCollider != null ? NearestBoxSurfacePoint(item._boundCollider, palm) : itemPos;
            Vector3 moveDir = palm - surface;
            if (moveDir.sqrMagnitude < 1e-6f) moveDir = palm - itemPos;
            if (moveDir.sqrMagnitude < 1e-6f) moveDir = -aim;
            moveDir.Normalize();
            Vector3 targetWorld = itemPos + (palm - surface) + moveDir * summonRestDepth;
            // The fingers close on it (graspItems): against the palm's skin, not sunk into the hand
            if (TryGraspRestPosition(item, dominant, out Vector3 graspRest))
                targetWorld = graspRest;

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

            VRInput.Haptic.Execute(0, 0.08f, 1, 0.5f, dominant ? primaryInputSource : secondaryInputSource);
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
                // Hand off to UpdateHeldItemPosition: cachedGrabOffset is the rest pose relative to the hand.
                cachedGrabOffset = cachedHand.InverseTransformPoint(heldItem.transform.position);
                isSummoningLoot = false;
            }
        }

        // Resets the held-item state after the gaze "Take" consumed the in-hand item (its GameObject is gone). The tail of
        // ForceDropHeldItem, without touching the destroyed transform/rigidbody.
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

        // Where a dot sits: between the item center and its near box surface (dotSurfaceBias), then pushed outward
        // (item-center -> surface) by dotSurfaceOffset.
        private static Vector3 DotPos(Vector3 center, Vector3 surf, Vector3 palm)
        {
            Vector3 outward = surf - center;
            if (outward.sqrMagnitude < 1e-6f) outward = palm - surf; // tiny/round items: face the palm
            if (outward.sqrMagnitude < 1e-6f) outward = Vector3.up;
            return Vector3.Lerp(center, surf, dotSurfaceBias) + outward.normalized * dotSurfaceOffset;
        }

        //--- Left-hand "ready to grab" pose ------------------------------------------
        // Poses the free off hand's fingers (EFT's animated curl replaced), more open when a loot dot is in view. Runs in
        // LateUpdate (after EFT animates the hand); doesn't accumulate whether or not EFT re-wrote the bone (RecoverAnimated).
        private void UpdateLeftHandFingers()
        {
            // EFT raises the "ReloadFloat" animator param to 1 while a left-hand weapon animation plays (reload, examine,
            // check ammo/chamber, malfunction fix). VRPlayerManager releases leftArmIk on it; the fingers yield too
            // (free = false eases handWeightBlend to 0 and stops touching the bones).
            // VRGunController keeps the left hand on the controller during those animations, so the fingers stay ours too
            bool leftHandAnimating = !VRGunController.KeepLeftHandOnController()
                && VRGlobals.player != null && VRGlobals.player.BodyAnimatorCommon != null
                && VRGlobals.player.BodyAnimatorCommon.GetFloat(VRPlayerManager.LEFT_HAND_ANIMATOR_HASH) == 1f;

            bool free = VRGlobals.inGame && !VRGlobals.menuOpen
                        && !VRGlobals.usingItem && !EatingInteractionController.ManualActive
                        && VRGlobals.vrPlayer != null
                        // supporting = this hand on the foregrip, except with the gun in the left hand held by the right alone (free then)
                        && (!VRGlobals.vrPlayer.isSupporting || TarkovVR.Patches.Core.Player.RealLeftHandedPatches.SupportHeld)
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

            // Replay the baked rest pose instead of the procedural flatten/flex/spread.
            if (restPoseEuler != null && restPoseEuler.Length > 0)
            {
                EditRestPoseLive();
                // The right hand's open pose mirrored (MirroredOpenHand), else the left bake with its own deltas
                bool mirrored = TryGetMirroredLeftRest(out Vector3[] mirroredPose);
                Vector3[] basePose = mirrored ? mirroredPose : restPoseEuler;
                if (handWeightBlend >= 0.01f)
                {
                    // Index controllers curl the fingers from the baked pose (with the left's flex/spread deltas, as in
                    // ReplayRestPose); everything else replays the baked pose. Holding a loot item in this hand suppresses
                    // the skeletal curls: the grip squeeze would clench the fist through the item.
                    bool holdingInThisHand = HoldsLoot(false) || PhysicalDoors.HoldsDoor(false);
                    if (holdingInThisHand
                        || !(SkeletalActive(false) && ApplySkeletalCurls(false, leftFingerJoints, leftFingerIsThumb, basePose, leftCurlSign, handWeightBlend, leftDeltas: !mirrored)))
                        ReplayRestPose(basePose, staticDeltas: !mirrored);
                    // Closed on a held loot item (HandsInteractionController.Grasp), from the pose just written
                    ApplyGrasp(false, leftFingerJoints, leftFingerIsThumb, leftCurlSign, handWeightBlend);
                }
                return;
            }

            if (handWeightBlend < 0.01f)
                return; // fully back to the animated pose — stop touching the bones

            for (int fingerIndex = 0; fingerIndex < leftFingerJoints.Length; fingerIndex++)
            {
                Transform[] joints = leftFingerJoints[fingerIndex];
                float fingerSpread = (leftFingerSpread != null && fingerIndex < leftFingerSpread.Length)
                    ? leftFingerSpread[fingerIndex] * fingerSpreadDeg : 0f;
                // The thumb is flattened like the fingers but has its own flex value (it over-curls with the fingers').
                float flexBase = PerFingerFlex(fingerIndex);
                for (int j = 0; j < joints.Length && j < 3; j++) // base, middle and tip
                {
                    if (joints[j] == null) continue;
                    // Flex tapers down the finger; the flatten weight is full on every joint. Spread is only at the knuckle.
                    float flex = flexBase * Mathf.Pow(midJointScale, j) * flexSign;
                    float sp = (j == 0) ? fingerSpread : 0f;
                    ApplyFlatFlex(joints[j], flex, sp, handWeightBlend);
                }
            }
            ApplyGrasp(false, leftFingerJoints, leftFingerIsThumb, leftCurlSign, handWeightBlend);
        }

        // The animated pose of a bone. If its localRotation differs from our last output, EFT re-animated it this frame and
        // that is the pose; if it matches, EFT left it alone: reuse the pose we last derived from, so the override never
        // accumulates frame to frame.
        private Quaternion RecoverAnimated(Transform t)
        {
            Quaternion cur = t.localRotation;
            if (fingerOutRot.TryGetValue(t, out Quaternion lastOut) && Quaternion.Angle(cur, lastOut) < 0.05f
                && fingerBaseRot.TryGetValue(t, out Quaternion lastBase))
                return lastBase;
            return cur;
        }

        // Captures the rendered left-finger local rotations into restPoseEuler (+ a paste-ready log). Same iteration order
        // as ReplayRestPose, so the flat indices line up.
        private void CaptureRestPose()
        {
            var list = new List<Vector3>();
            var builder = new System.Text.StringBuilder();
            builder.Append("[LootPointer] restPoseEuler = new Vector3[] {\n");
            for (int fingerIndex = 0; fingerIndex < leftFingerJoints.Length; fingerIndex++)
            {
                Transform[] joints = leftFingerJoints[fingerIndex];
                bool isThumb = leftFingerIsThumb != null && fingerIndex < leftFingerIsThumb.Length && leftFingerIsThumb[fingerIndex];
                for (int j = 0; j < joints.Length && j < 3; j++)
                {
                    Vector3 e = joints[j] != null ? joints[j].localEulerAngles : Vector3.zero;
                    list.Add(e);
                    builder.Append($"    new Vector3({e.x:F2}f, {e.y:F2}f, {e.z:F2}f), // {(isThumb ? "thumb" : ("finger" + fingerIndex))} joint{j}\n");
                }
            }
            builder.Append("};");
            restPoseEuler = list.ToArray();
            Plugin.MyLog.LogInfo(builder.ToString());
        }

        // Live per-joint editing: changing poseEditJoint loads that joint's Euler; otherwise poseEditEuler writes back into
        // the baked array each frame.
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
            var builder = new System.Text.StringBuilder();
            builder.Append("[LootPointer] restPoseEuler = new Vector3[] {\n");
            for (int i = 0; i < restPoseEuler.Length; i++)
            {
                Vector3 e = restPoseEuler[i];
                builder.Append($"    new Vector3({e.x:F2}f, {e.y:F2}f, {e.z:F2}f), // index {i}\n");
            }
            builder.Append("};");
            Plugin.MyLog.LogInfo(builder.ToString());
        }

        // Per-finger flex delta (0 = thumb), eased between the idle and "dot in view" arrays by flexProgress.
        // Out-of-range fingers get 0.
        private float IdleFlex(int fingerIndex)
        {
            return fingerFlexDeg != null && fingerIndex < fingerFlexDeg.Length ? fingerFlexDeg[fingerIndex] : 0f;
        }

        private float PerFingerFlex(int fingerIndex)
        {
            float idle = (fingerFlexDeg != null && fingerIndex < fingerFlexDeg.Length) ? fingerFlexDeg[fingerIndex] : 0f;
            float ready = (fingerReadyFlexDeg != null && fingerIndex < fingerReadyFlexDeg.Length) ? fingerReadyFlexDeg[fingerIndex] : idle;
            return Mathf.Lerp(idle, ready, flexProgress);
        }

        // Drives every left-finger joint to its baked local rotation with the flex and spread deltas on top (0 = the baked
        // pose exactly), Slerped from the live animation by handWeightBlend. Same iteration order as CaptureRestPose.
        // basePose: the left bake, or the right's open pose mirrored. staticDeltas: the idle flex + spread tuned for the left bake
        // (off on the mirrored pose: it is the look); the loot-dot "ready" curl (its move from idle) shows either way.
        private void ReplayRestPose(Vector3[] basePose, bool staticDeltas)
        {
            int index = 0;
            for (int fingerIndex = 0; fingerIndex < leftFingerJoints.Length; fingerIndex++)
            {
                Transform[] joints = leftFingerJoints[fingerIndex];
                float fingerSpread = staticDeltas && leftFingerSpread != null && fingerIndex < leftFingerSpread.Length
                    ? leftFingerSpread[fingerIndex] * fingerSpreadDeg : 0f;
                float flexBase = staticDeltas ? PerFingerFlex(fingerIndex) : PerFingerFlex(fingerIndex) - IdleFlex(fingerIndex);
                for (int j = 0; j < joints.Length && j < 3; j++, index++)
                {
                    if (joints[j] == null || index >= basePose.Length) continue;
                    Quaternion animated = RecoverAnimated(joints[j]);
                    float flex = flexBase * Mathf.Pow(midJointScale, j) * flexSign;
                    float sp = (j == 0) ? fingerSpread : 0f;
                    Quaternion target = Quaternion.Euler(basePose[index])
                        * Quaternion.AngleAxis(flex, Vector3.forward)
                        * Quaternion.AngleAxis(sp, spreadAxis);
                    Quaternion outRot = Quaternion.Slerp(animated, target, handWeightBlend);
                    joints[j].localRotation = outRot;
                    fingerBaseRot[joints[j]] = animated;
                    fingerOutRot[joints[j]] = outRot;
                }
            }
        }

        // Replaces the animated finger curl with an absolute flex (+ a lateral spread), blended by weight.
        private void ApplyFlatFlex(Transform t, float flexDeg, float spreadDeg, float weight)
        {
            Quaternion animated = RecoverAnimated(t);

            // Swing-twist split around local Z (the finger flex axis): the animated curl (twist) is replaced by the target
            // flex, the swing (natural splay) is kept. Then the finger fans sideways around spreadAxis.
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

        // The finger chains under a palm: each child is a finger root, its first-child chain gives up to 3 joints (base / middle /
        // tip). The thumb is EFT's "...Digit1..." ("thumb" alone never matches its bone names).
        private static void FingerChainsUnder(Transform palm, List<Transform[]> fingers, List<bool> thumbs)
        {
            for (int fingerIndex = 0; fingerIndex < palm.childCount; fingerIndex++)
            {
                Transform fingerRoot = palm.GetChild(fingerIndex);
                var joints = new List<Transform>();
                Transform joint = fingerRoot;
                while (joint != null && joints.Count < 3)
                {
                    joints.Add(joint);
                    joint = joint.childCount > 0 ? joint.GetChild(0) : null;
                }
                if (joints.Count >= 1)
                {
                    fingers.Add(joints.ToArray());
                    string fingerName = fingerRoot.name;
                    thumbs.Add(fingerName.IndexOf("thumb", System.StringComparison.OrdinalIgnoreCase) >= 0
                            || fingerName.IndexOf("Digit1", System.StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
        }

        // The left hand's finger chains, under "Base HumanLPalm"
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
            FingerChainsUnder(palm, fingers, thumbs);
            if (fingers.Count > 0)
            {
                leftFingerJoints = fingers.ToArray();
                leftFingerIsThumb = thumbs.ToArray();

                // Signed centered ordinal among the non-thumb fingers (e.g. 4 fingers -> -1.5,-0.5,0.5,1.5), so the knuckle
                // fan splays outward from the middle.
                int nonThumb = 0;
                for (int i = 0; i < thumbs.Count; i++) if (!thumbs[i]) nonThumb++;
                float center = (nonThumb - 1) * 0.5f;
                leftFingerSpread = new float[fingers.Count];
                int k = 0;
                for (int i = 0; i < fingers.Count; i++)
                    leftFingerSpread[i] = thumbs[i] ? 0f : (k++ - center);
            }
        }

        // NearestBoxSurfacePoint: the point on an oriented BoxCollider's surface nearest a world point. Unlike
        // Collider.ClosestPoint it returns a surface point even when the query point is inside the box (the least-penetrated
        // axis is pushed out to its face), so a point-blank grab snaps to the outside of the collider.
        // IsInsideBox: true if a world point is inside the oriented box collider (the same local-frame test).
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
