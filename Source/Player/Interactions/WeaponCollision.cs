using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.CameraControl;
using EFT.Interactive;
using JsonType;
using SptVrFikaSync;
using System.Runtime.CompilerServices;
using TarkovVR.ModSupport;
using TarkovVR.Patches.Core.Player;
using UnityEngine;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Player.Interactions
{
    // Physical weapon collision through a sandboxed PhysX rigidbody (WeaponPhysicsProxy): an invisible Rigidbody shaped like the
    // gun (a fat body capsule and a thin barrel capsule), driven toward the controller pose and collided in its own local physics
    // scene against copies of the nearby world colliders, so stepping it can't disturb the game's loot or ragdolls.
    // It is stepped at render rate (proxy.Step from Apply, not FixedUpdate) and the visible gun is placed on its resolved
    // pose every frame. Rig motion (locomotion and turning) is part of the target, so near geometry it also goes through the
    // collision; in open space the proxy reaches the target anyway.
    // Vanilla's TurnAwayEffector pose is suppressed for the local player (IKPatches); its OverlapValue still runs (ADS-stop
    // etc.). The fireport is a child of WeaponRoot, so shots come from the clamped muzzle, and the FIKA arm sync
    // samples WeaponRoot's world pose, so observers see the clamp too.
    // Kinematic depenetration (ComputePenetration) clips through thin walls: don't use it.
    public static class WeaponCollision
    {
        // ---- live tunables ----
        [Tunable("Sling, stock & weight", TunableKind.Switch, "The gun collides with the world (also switches hand collision and the turn-away suppression).")]
        public static bool enabled = true;                 // master switch (also gates HandCollision + the TurnAway suppression)
        public static float bodyRadius = 0.05f;            // m; gun body/receiver capsule
        public static float barrelRadius = 0.02f;          // m; gun barrel capsule
        public static float impactHapticStrength = 1f;     // amplitude ~ approach speed
        public static float impactSoundVolume = 0.3f;
        public static float impactSoundMinSpeed = 1.2f;    // m/s the gun meets the surface at (from the hand's motion) below which it stays silent
        public static float impactSoundFullSpeed = 4f;     // m/s at which it plays at impactSoundVolume
        public static float impactSoundRetrigger = 0.8f;   // s between impact sounds
        public static float freshContactGap = 0.3f;        // s off a surface before touching it again counts as a new impact
        [Tunable("Sling, stock & weight", TunableKind.Tune, "How hard the gun has to hit a whole window to break it.", Unit = "m/s")]
        public static float glassBreakMinSpeed = 2.0f;     // m/s approach needed to break
        public static float glassBreakDamage = 30f;        // melee damage carried by the synthetic hit (~knife slash)
        [Tunable("Sling, stock & weight", TunableKind.Switch, "What's left of a broken window breaks as soon as the gun touches it. Off: the leftover glass needs the same "
            + "hit as a whole window.")]
        public static bool leftoverGlassAtTouch = true;    // what's left of a broken window breaks at any touch (false = it needs the whack too)
        [Tunable("Sling, stock & weight", TunableKind.Log, "A [WeaponCollision] hit line each time the gun bumps into something: the object's path, layer, collider shape "
            + "and size, and its bullet surface. For finding what the gun catches on that it shouldn't.")]
        public static bool logContacts = false;            // a line per new contact: what the gun hit (path, layer, collider, bullet surface)
        // Logs a line a second while the gun touches something: how much the drawn muzzle shakes frame to frame (its second difference,
        // in the rig's frame so walking and turning don't count) against the hand's own shake, how deep the hand is in, and the substeps.
        // The drawn gun shaking more than the hand points at the contact solve; the same amount is the hand's own tremor carried along
        // the surface.
        public static bool contactJitterLog = true;

        public static int proxyLayerOverride = -1;         // force the proxy/sandbox layer if the automatic pick misbehaves
        // Solves on the gun's final pose of the frame (Player.IkProcess prefix, after LateGunPlacement) instead of in Update. The gun's
        // parent is put back on the hand after Update (HandsPositioner.Update can run after GunInteractionController.Update, and the
        // end-of-frame placement moves it again), so a collision offset solved in Update is carried one frame of hand motion further:
        // on a hard swing the gun is drawn 20-30 cm into what stopped it for a frame or two. false = solve in Update.
        public static bool atFinalPlacement = true;

        private static WeaponPhysicsProxy proxy;
        private static Vector3 lastTargetGrip;             // the hand's gun pose last frame, for how fast the gun meets a surface
        private static Quaternion lastTargetRotation;
        private static bool hasLastTarget;
        private static Vector3 targetGrip;
        private static Vector3 targetVelocity;
        private static Vector3 targetAngularVelocity;      // rad/s
        private static float contactEndStamp = -10f;       // time of the last contact event (impact re-arm)
        private static float lastImpactSoundStamp = -10f;
        private static int occlusionMask;
        private static int glassLayer = -1;

        // Barrel tip: the farthest of the fireport and the muzzle-device transforms (suppressors extend past the fireport), re-picked
        // every second so attachment changes are caught. The length is the live world distance, not WeaponLn: vanilla's value carries
        // a lossyScale correction for EFT's shrunken first-person rig that inflates it by ~15 cm in the VR rig.
        private static Transform muzzleTip;
        private static object muzzleTipOwner;
        private static float muzzleTipRefresh;

        // The collision offset is applied to weaponHolder after the marker anchor each frame and removed right before the next
        // frame's anchor (PreAnchor). Without that it would accumulate: the anchor rewrites the holder's localPosition every frame
        // but its rotation only at equip.
        private static bool enacted;
        private static Vector3 preEnactLocalPos;
        private static Quaternion preEnactLocalRot;
        private static Vector3 lastEnactLocalPos;
        private static Quaternion lastEnactLocalRot;

        public static void Reset()
        {
            hasResolvedOffset = false;
            StripEnacted();
            hasLastTarget = false;
            proxy?.Deactivate();
        }

        // Called at the top of UpdateWeaponPosition, before the anchor measures weaponHolder. Restores the holder's local pose from
        // before the collision offset, so the anchor starts from the controller pose. If something else (the equip path) has written
        // the holder since, that write is kept.
        public static void PreAnchor()
        {
            StripEnacted();
        }

        private static void StripEnacted()
        {
            if (!enacted)
                return;
            enacted = false;
            if (VRGlobals.weaponHolder == null)
                return;
            Transform holder = VRGlobals.weaponHolder.transform;
            if (Mathf.Abs(Quaternion.Dot(holder.localRotation, lastEnactLocalRot)) > 0.99999f)
                holder.localRotation = preEnactLocalRot;
            if ((holder.localPosition - lastEnactLocalPos).sqrMagnitude < 1e-8f)
                holder.localPosition = preEnactLocalPos;
        }

        private static int deferredFrame = -1;              // a solve asked for in Update and not yet done at the final placement
        private static Vector3 resolvedInTargetPosition;    // the last solve's resolved pose in its target's frame (the hand's gun pose)
        private static Quaternion resolvedInTargetRotation = Quaternion.identity;
        private static bool hasResolvedOffset;

        // The gun's pose as drawn this frame, for the co-op arm sync (VRPlayerManager.LateUpdate, which Unity may run before or after
        // Player.LateUpdate). If this frame's solve hasn't run yet, the last solve's offset is applied to the hand's gun pose.
        public static void DrawnPose(Transform weaponRoot, out Vector3 position, out Quaternion rotation)
        {
            position = weaponRoot.position;
            rotation = weaponRoot.rotation;
            if (deferredFrame != Time.frameCount || !hasResolvedOffset || !enabled)
                return;
            position += rotation * resolvedInTargetPosition;
            rotation *= resolvedInTargetRotation;
        }

        // Called from UpdateWeaponPosition after the marker anchor. Defers the solve to this frame's final placement
        // (ApplyAtFinalPlacement), or solves now if atFinalPlacement is off or the previous deferred solve never ran
        public static void Request()
        {
            if (!atFinalPlacement || deferredFrame >= 0)
            {
                deferredFrame = -1;
                Apply();
                return;
            }
            deferredFrame = Time.frameCount;
        }

        // Called from the Player.IkProcess prefix (LateGunPlacementPatches), after the gun is put on the hand at the frame's final rig pose
        public static void ApplyAtFinalPlacement()
        {
            if (deferredFrame != Time.frameCount)
                return;
            deferredFrame = -1;
            Apply();
        }

        // Runs on the gun at its controller pose (the marker anchor's) and must be the last write to weaponHolder this frame
        public static void Apply()
        {
            if (!enabled || VRGlobals.firearmController == null ||
                VRGlobals.player == null || VRGlobals.weaponHolder == null || WeaponPatches.grenadeEquipped)
            {
                Reset();
                return;
            }

            var firearmController = VRGlobals.firearmController;
            Transform weaponRoot = firearmController.WeaponRoot;
            if (weaponRoot == null || VRGlobals.player.MovementContext?.StationaryWeapon != null)
            {
                Reset();
                return;
            }
            float weaponLn = MuzzleLength(firearmController, weaponRoot);
            if (weaponLn <= 0.05f || !EnsureProxy())
            {
                Reset();
                return;
            }

            Vector3 rawGrip = weaponRoot.position;
            Quaternion weaponRootRotation = weaponRoot.rotation;

            // How fast the hand moves the gun (locomotion and turning included), for the speed it meets a surface at
            float deltaTime = Mathf.Max(Time.deltaTime, 1e-4f);
            if (hasLastTarget && (rawGrip - lastTargetGrip).sqrMagnitude < 1f)
            {
                targetVelocity = (rawGrip - lastTargetGrip) / deltaTime;
                (weaponRootRotation * Quaternion.Inverse(lastTargetRotation)).ToAngleAxis(out float turnDegrees, out Vector3 turnAxis);
                if (turnDegrees > 180f)
                    turnDegrees -= 360f;
                targetAngularVelocity = float.IsInfinity(turnAxis.x) || float.IsNaN(turnAxis.x) ? Vector3.zero
                    : turnAxis * (turnDegrees * Mathf.Deg2Rad / deltaTime);
            }
            else
            {
                targetVelocity = Vector3.zero;
                targetAngularVelocity = Vector3.zero;
            }
            lastTargetGrip = rawGrip;
            lastTargetRotation = weaponRootRotation;
            targetGrip = rawGrip;
            hasLastTarget = true;

            proxy.Configure(weaponLn, bodyRadius, barrelRadius);
            proxy.SetTarget(rawGrip, weaponRootRotation);               // drive the proxy to the controller pose
            proxy.Step(Time.deltaTime);                    // step the sandbox now, at render rate (see WeaponPhysicsProxy.Step)

            // Draw the gun on the PhysX-resolved pose every frame. The proxy was stepped this frame: in free space it
            // reaches the hand exactly and EnactHolderPose no-ops (its epsilon check); against a wall the visible gun
            // stops the same frame the proxy does. Feedback comes from the proxy's collision events through OnProxyContact.
            EnactHolderPose(rawGrip, weaponRootRotation, proxy.ResolvedPos, proxy.ResolvedRot);
            Quaternion toTarget = Quaternion.Inverse(weaponRootRotation);
            resolvedInTargetPosition = toTarget * (proxy.ResolvedPos - rawGrip);
            resolvedInTargetRotation = toTarget * proxy.ResolvedRot;
            hasResolvedOffset = true;
            if (contactJitterLog)
                TrackContactJitter(rawGrip, weaponRootRotation, proxy.ResolvedPos, proxy.ResolvedRot, weaponLn);
        }

        private static Vector3 drawnMuzzleBefore, drawnMuzzleBeforeThat, handMuzzleBefore, handMuzzleBeforeThat;
        private static int jitterSamples, jitterLastFrame = -10, jitterFrames, jitterSubsteps;
        private static float jitterDrawnSum, jitterHandSum, jitterDrawnMost, jitterHandMost, jitterInsideSum, jitterNextLog;

        private static void TrackContactJitter(Vector3 handGrip, Quaternion handRotation, Vector3 drawnGrip, Quaternion drawnRotation, float length)
        {
            Transform rig = VRGlobals.camRoot != null ? VRGlobals.camRoot.transform : null;
            bool touching = proxy.RecentlyContacted && rig != null;
            if (!touching || Time.frameCount - jitterLastFrame != 1)
                jitterSamples = 0;
            jitterLastFrame = Time.frameCount;
            if (touching)
            {
                Vector3 muzzle = new Vector3(0f, -length, 0f);
                Vector3 drawn = rig.InverseTransformPoint(drawnGrip + drawnRotation * muzzle);
                Vector3 hand = rig.InverseTransformPoint(handGrip + handRotation * muzzle);
                if (jitterSamples >= 2)
                {
                    float drawnShake = (drawn - 2f * drawnMuzzleBefore + drawnMuzzleBeforeThat).magnitude;
                    float handShake = (hand - 2f * handMuzzleBefore + handMuzzleBeforeThat).magnitude;
                    jitterDrawnSum += drawnShake;
                    jitterHandSum += handShake;
                    jitterDrawnMost = Mathf.Max(jitterDrawnMost, drawnShake);
                    jitterHandMost = Mathf.Max(jitterHandMost, handShake);
                    jitterInsideSum += (hand - drawn).magnitude;
                    jitterSubsteps += proxy.LastSubsteps;
                    jitterFrames++;
                }
                drawnMuzzleBeforeThat = drawnMuzzleBefore;
                drawnMuzzleBefore = drawn;
                handMuzzleBeforeThat = handMuzzleBefore;
                handMuzzleBefore = hand;
                jitterSamples = Mathf.Min(jitterSamples + 1, 2);
            }
            if (jitterFrames > 0 && Time.time >= jitterNextLog)
            {
                jitterNextLog = Time.time + 1f;
                Plugin.MyLog.LogInfo($"[WeaponCollision] touching {jitterFrames} frames: muzzle shake drawn {jitterDrawnSum / jitterFrames * 1000f:0.0} mm "
                    + $"(most {jitterDrawnMost * 1000f:0.0}) vs hand {jitterHandSum / jitterFrames * 1000f:0.0} mm (most {jitterHandMost * 1000f:0.0}), "
                    + $"hand {jitterInsideSum / jitterFrames * 100f:0.0} cm in, {jitterSubsteps / (float)jitterFrames:0.#} substeps, solver {WeaponPhysicsProxy.solverIterations}/{WeaponPhysicsProxy.solverVelocityIterations}");
                jitterFrames = 0;
                jitterSubsteps = 0;
                jitterDrawnSum = jitterHandSum = jitterDrawnMost = jitterHandMost = jitterInsideSum = 0f;
            }
        }

        // Moves weaponHolder so the gun's root goes from its current (un-collided) pose to the resolved pose, as a rigid move of the
        // holder (weaponRoot is its descendant). The poses before and after are saved so PreAnchor can undo it next frame.
        private static void EnactHolderPose(Vector3 currentPosition, Quaternion currentRotation, Vector3 targetPosition, Quaternion targetRotation)
        {
            Transform holder = VRGlobals.weaponHolder.transform;
            Quaternion turn = targetRotation * Quaternion.Inverse(currentRotation);
            bool applyRotation = Quaternion.Angle(turn, Quaternion.identity) > 0.01f;
            bool applyPosition = (targetPosition - currentPosition).sqrMagnitude > 1e-10f;
            if (!applyRotation && !applyPosition)
                return;
            preEnactLocalPos = holder.localPosition;
            preEnactLocalRot = holder.localRotation;
            Vector3 offset = holder.position - currentPosition;
            holder.SetPositionAndRotation(targetPosition + turn * offset, turn * holder.rotation);
            lastEnactLocalPos = holder.localPosition;
            lastEnactLocalRot = holder.localRotation;
            enacted = true;
        }

        // Creates the sandboxed physics proxy. The world mask selects which colliders are copied into the sandbox: the static
        // world and glass only, not Player/Deadbody (the proxy would jam against your own body or corpses).
        // The layer only needs the proxy and its copies to collide with each other.
        private static bool EnsureProxy()
        {
            if (proxy != null)
                return true;

            int layer = proxyLayerOverride;
            if (layer < 0)
                for (int i = 31; i >= 8; i--)
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                    {
                        layer = i;
                        break;
                    }
            if (layer < 0)
                layer = LayerMask.NameToLayer("Weapon Preview");
            if (layer < 0)
                layer = 0;
            Physics.IgnoreLayerCollision(layer, layer, false);   // proxy vs its world-copies (same layer)

            int mask = GetOcclusionMask();
            int player = LayerMask.NameToLayer("Player");
            int deadbody = LayerMask.NameToLayer("Deadbody");
            int pctest = LayerMask.NameToLayer("PlayerCollisionTest");
            if (player >= 0) mask &= ~(1 << player);
            if (deadbody >= 0) mask &= ~(1 << deadbody);
            if (pctest >= 0) mask &= ~(1 << pctest);

            proxy = WeaponPhysicsProxy.Create(mask);
            proxy.UseLayer(layer);
            Plugin.MyLog.LogInfo($"[WeaponCollision] physics proxy (sandboxed) on layer {layer} ('{LayerMask.LayerToName(layer)}')");
            return true;
        }

        private static float MuzzleLength(EFT.Player.FirearmController firearmController, Transform weaponRoot)
        {
            if (!ReferenceEquals(muzzleTipOwner, firearmController) || muzzleTip == null || Time.time > muzzleTipRefresh)
            {
                muzzleTipOwner = firearmController;
                muzzleTipRefresh = Time.time + 1f;
                muzzleTip = firearmController.CurrentFireport?.Original;
                float best = muzzleTip != null ? Vector3.Distance(weaponRoot.position, muzzleTip.position) : 0f;
                GameObject[] muzzles = firearmController.Firearms?.MuzzleTransforms();
                if (muzzles != null)
                {
                    foreach (GameObject muzzle in muzzles)
                    {
                        if (muzzle == null)
                            continue;
                        float distance = Vector3.Distance(weaponRoot.position, muzzle.transform.position);
                        if (distance > best)
                        {
                            best = distance;
                            muzzleTip = muzzle.transform;
                        }
                    }
                }
            }
            return muzzleTip != null ? Vector3.Distance(weaponRoot.position, muzzleTip.position) : firearmController.WeaponLn;
        }

        internal static int GlassLayer => glassLayer;

        private static int lowPolyLayer = -2, doorLowPolyLayer = -2;

        private static bool IsMovementLayer(int layer)
        {
            if (lowPolyLayer == -2)
            {
                lowPolyLayer = LayerMask.NameToLayer("LowPolyCollider");
                doorLowPolyLayer = LayerMask.NameToLayer("DoorLowPolyCollider");
            }
            return layer == lowPolyLayer || layer == doorLowPolyLayer;
        }

        internal static int GetOcclusionMask()
        {
            if (occlusionMask == 0)
            {
                // Same layers EFT's own weapon overlap uses; fallback = the mod's usual solid-world mask.
                LayerMask mask = EFTHardSettings.Instance != null
                    ? EFTHardSettings.Instance.WEAPON_OCCLUSION_LAYERS
                    : (LayerMask)LayerMask.GetMask("Default", "Terrain", "HighPolyCollider");
                occlusionMask = mask.value != 0 ? mask.value : LayerMask.GetMask("Default", "Terrain", "HighPolyCollider");
                glassLayer = LayerMask.NameToLayer("TransparentCollider");
                if (glassLayer >= 0)
                    occlusionMask |= 1 << glassLayer;      // windows are solid to the gun (and breakable)
            }
            return occlusionMask;
        }

        // Any contact (pressing, sliding): a new impact needs freshContactGap off the surface first
        internal static void NoteContact() => contactEndStamp = Time.time;

        // The physics proxy reports its collisions here. Impact feedback fires only on a fresh contact (freshContactGap since the
        // last contact event); contactEndStamp advances on every event, so pressing or sliding stays quiet.
        internal static void OnProxyContact(Collider col, Vector3 point, Vector3 normal)
        {
            bool fresh = Time.time - contactEndStamp > freshContactGap;
            contactEndStamp = Time.time;
            if (col == null)
                return;
            Vector3 barrelDir = VRGlobals.firearmController?.WeaponRoot != null
                ? -VRGlobals.firearmController.WeaponRoot.up : -normal;
            // What's left of a broken window breaks at a touch, as in vanilla where walking into it breaks it. Every piece is its own
            // collider, so pushing on into the next piece is a new contact.
            bool leftoverGlass = leftoverGlassAtTouch && IsLeftoverGlass(col);
            if (leftoverGlass)
                TryBreakGlass(col, point, normal, barrelDir);
            if (!fresh)
                return;
            if (logContacts)
                LogContact(col);

            // How fast the hand brings that point of the gun into the surface. Not the contact's relative velocity: that is the
            // proxy's catch-up toward a hand already inside the wall, large whenever the gun is pressed in, however gently.
            Vector3 pointVelocity = targetVelocity + Vector3.Cross(targetAngularVelocity, point - targetGrip);
            float approach = Mathf.Max(0f, Vector3.Dot(pointVelocity, -normal));
            float amplitude = Mathf.Clamp01(approach / 2.5f) * impactHapticStrength;
            if (amplitude > 0.05f)
                Pulse(0.08f, 85f, amplitude);
            if (approach > impactSoundMinSpeed &&
                Time.time - lastImpactSoundStamp > impactSoundRetrigger)
            {
                lastImpactSoundStamp = Time.time;
                PlayImpactSound(col, point, approach);
            }
            if (!leftoverGlass && approach >= glassBreakMinSpeed)
                TryBreakGlass(col, point, normal, barrelDir);
            Door door = CollisionWorld.DoorOf(col);
            if (door != null)
                PhysicalDoors.GunHit(door, point, normal, approach);
        }

        // Which world collider the gun hit (the real one its sandbox copy stands in for)
        private static void LogContact(Collider col)
        {
            string path = col.name;
            Transform parent = col.transform.parent;
            for (int depth = 0; depth < 4 && parent != null; depth++, parent = parent.parent)
                path = parent.name + "/" + path;
            string shape = col is MeshCollider meshCollider
                ? $"mesh '{(meshCollider.sharedMesh != null ? meshCollider.sharedMesh.name : "none")}'"
                : col.GetType().Name;
            BallisticCollider ballistic = col.GetComponent<BallisticCollider>();
            Vector3 size = col.bounds.size;
            Plugin.MyLog.LogInfo($"[WeaponCollision] hit {path} on {LayerMask.LayerToName(col.gameObject.layer)}: {shape}"
                + $"{(col.isTrigger ? ", trigger" : "")}, {size.x:0.00} x {size.y:0.00} x {size.z:0.00} m"
                + $"{(ballistic != null ? ", bullet surface " + ballistic.TypeOfMaterial : "")}");
        }

        // Plays the gun's surface-aware drop sound bank (what vanilla plays when a weapon hits the ground) at the contact point.
        // The surface comes from the hit collider's BaseBallistic, like LootDropSoundPatches.
        private static void PlayImpactSound(Collider hitCollider, Vector3 point, float approach)
        {
            BetterAudio betterAudio = Singleton<BetterAudio>.Instance;
            if (betterAudio == null || betterAudio.ItemDropSounds == null)
                return;

            BaseBallistic.ESurfaceSound surface = BaseBallistic.ESurfaceSound.Concrete;
            BaseBallistic ballistic = hitCollider.GetComponent<BaseBallistic>();
            // A movement-layer collider (a door's, a tree trunk's) has no surface; the door's or tree's bullet mesh has it
            if (ballistic == null && IsMovementLayer(hitCollider.gameObject.layer))
            {
                Door door = hitCollider.GetComponentInParent<Door>();
                Transform owner = door != null ? door.transform : hitCollider.transform.parent;
                if (owner != null)
                    ballistic = owner.GetComponentInChildren<BaseBallistic>();
            }
            if (ballistic != null)
                surface = ballistic.GetSurfaceSound(point);

            EItemDropSoundType type = EItemDropSoundType.Rifle;
            var weapon = VRGlobals.firearmController?.Item;
            if (weapon != null && weapon.DropSoundType != EItemDropSoundType.None)
                type = weapon.DropSoundType;

            SoundBank bank = betterAudio.ItemDropSounds.GetSoundBank(surface, type);
            if (bank == null)
                return;

            // Louder the harder you hit, rising slowly: a knock is soft, only a real whack is loud
            float hardness = Mathf.InverseLerp(impactSoundMinSpeed, impactSoundFullSpeed, approach);
            float volume = impactSoundVolume * Mathf.Lerp(0.25f, 1f, hardness * hardness);
            float distance = CameraManager.Instance.Distance(point);
            betterAudio.PlayAtPoint(point, bank, distance, volume, -1f, EnvironmentType.Outdoor, EOcclusionTest.OneShotPropagation);
        }

        // A hard hit on a window shatters it like a VR melee hit: a melee DamageInfo sent through GameWorld.HackShot (glass-break
        // handler and knife-hit effects). Mirrors FikaSync/MeleeHitApply; observers replay the same melee-hit packet.
        // Glass only: a gun bump must not chip walls or leave knife decals.
        private static void TryBreakGlass(Collider hitCollider, Vector3 point, Vector3 normal, Vector3 barrelDir)
        {
            BallisticCollider ballisticCollider = hitCollider.GetComponent<BallisticCollider>();
            if (ballisticCollider == null || ballisticCollider is BodyPartCollider ||
                ballisticCollider.TypeOfMaterial.ToString().IndexOf("Glass", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
                return;
            // The real glass is already gone (a fallen piece, a shot-out pane): only remove our copy
            if (!ballisticCollider.enabled || !hitCollider.enabled || !hitCollider.gameObject.activeInHierarchy)
            {
                DropBrokenCopies(hitCollider);
                return;
            }

            DamageInfo damageInfo = default;
            damageInfo.DamageType = EDamageType.Melee;
            damageInfo.Damage = glassBreakDamage;
            damageInfo.ArmorDamage = 1f;
            damageInfo.HitPoint = point;
            damageInfo.HitNormal = normal;
            damageInfo.Direction = barrelDir;
            damageInfo.HitCollider = hitCollider;
            damageInfo.HittedBallisticCollider = ballisticCollider;
            damageInfo.IsForwardHit = true;
            if (gameWorld.MainPlayer != null)
                damageInfo.Player = gameWorld.GetEverExistedBridgeByProfileID(gameWorld.MainPlayer.ProfileId);
            gameWorld.HackShot(damageInfo);

            DropBrokenCopies(hitCollider);

            if (InstalledMods.FIKAInstalled)
                FikaSendGunSurfaceHit(point, normal, barrelDir, glassBreakDamage);
        }

        // Is this a piece still stuck in a broken window? Tarkov creates them at the break (WindowBreaker.UpdateToStuck): a pooled
        // object under the window with a convex mesh, the window's glass BallisticCollider and a BrokenWindowPieceCollider trigger
        // child (walking into it breaks the piece). A hit or a fall turns its BallisticCollider off.
        private static bool IsLeftoverGlass(Collider collider)
        {
            if (collider.gameObject.layer != glassLayer || !IsWindowPiece(collider))
                return false;
            WindowBreaker window = collider.GetComponentInParent<WindowBreaker>();
            BallisticCollider ballistic = collider.GetComponent<BallisticCollider>();
            return window != null && window.IsDamaged && ballistic != null && ballistic.enabled;
        }

        private static bool IsWindowPiece(Collider collider) => collider.GetComponentInChildren<BrokenWindowPieceCollider>(true) != null;

        // The real colliders are gone, so remove their sandbox copies too, or the gun keeps hitting a phantom window until you walk
        // more than WeaponPhysicsProxy.retainDistance away and back. Hitting a piece knocks out the pieces around it as well, and
        // pieces are pooled (other windows reuse them), so their copies are dropped but not blacklisted. A pane is blacklisted.
        private static void DropBrokenCopies(Collider hitCollider)
        {
            WindowBreaker window = hitCollider.GetComponentInParent<WindowBreaker>();
            if (window != null && IsWindowPiece(hitCollider))
                proxy?.DropDisabledCopiesUnder(window.transform);
            else
                proxy?.ForgetCollider(hitCollider.GetInstanceID());
        }

        // Companion-DLL isolation (like VRMeleeController.FikaSendMeleeHit): NoInlining so the cross-assembly reference
        // is only JITted when called, i.e. with FIKA installed.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FikaSendGunSurfaceHit(Vector3 point, Vector3 normal, Vector3 direction, float damage)
            => FikaVrSync.SendMeleeHit(point, normal, direction, damage);

        // The gun hitting something: every hand on the gun feels it (GunHaptics)
        private static void Pulse(float duration, float freq, float amplitude)
        {
            TarkovVR.Source.Weapons.GunHaptics.Pulse(null, TarkovVR.Source.Weapons.GunHaptics.Feel.Mechanism, amplitude, duration, freq);
        }
    }
}
