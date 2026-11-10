using Comfort.Common;
using EFT;
using EFT.Ballistics;
using EFT.CameraControl;
using JsonType;
using SptVrFikaSync;
using System.Runtime.CompilerServices;
using TarkovVR.ModSupport;
using TarkovVR.Patches.Core.Player;
using UnityEngine;
using TarkovVR.Source.Controls;

namespace TarkovVR.Source.Player.Interactions
{
    // Physical weapon collision via a SANDBOXED PhysX rigidbody ([WeaponPhysicsProxy]): an invisible
    // Rigidbody shaped like the gun (fat body + thin barrel capsule) is driven toward the controller pose
    // and collided by the engine, in its OWN local physics scene with static COPIES of the nearby world
    // colliders (so stepping it can't disturb the game's loot/ragdolls). WeaponCollision drives the proxy,
    // STEPS IT AT RENDER RATE (proxy.Step from Apply, not FixedUpdate), and places the visible EFT gun on the
    // resolved pose EVERY frame (render-body). Because it's stepped in lockstep with rendering, in free space
    // the gain-1 drive reaches the hand exactly so the enact no-ops and the gun sits at the true controller
    // pose (crisp), while a fast swing into a wall stops the visible gun the same frame it stops the proxy
    // (no clip-through). Rig motion (locomotion + turning) is applied to the proxy as a rigid transform
    // (AddRigMotion), not chased by the velocity drive, so moving/turning doesn't jitter.
    // Vanilla TurnAwayEffector pose is suppressed for the local player (IKPatches: effector outputs zeroed
    // + AvoidObstacles IsDirty gate); its OverlapValue bookkeeping still runs (ADS-stop etc.). The fireport
    // is a child of WeaponRoot, so point-blank shots originate at the clamped muzzle, and the FIKA arm sync
    // samples WeaponRoot's world pose, so observers see the clamp too.
    // NOTE: sandbox copies of NON-READABLE meshes fail (CollisionMeshData error) - collision is limited to
    // readable-mesh + primitive geometry. Known limitation the user accepted over the ComputePenetration
    // alternative (which clipped thin walls).
    public static class WeaponCollision
    {
        // ---- live-tunable knobs (UnityExplorer) ----
        public static bool enabled = true;                 // master switch (also gates HandCollision + the TurnAway suppression)
        public static float bodyRadius = 0.05f;            // m; gun body/receiver capsule
        public static float barrelRadius = 0.02f;          // m; gun barrel capsule
        public static bool haptics = true;                 // first-impact thud only
        public static float impactHapticStrength = 1f;     // amplitude ~ approach speed
        public static bool impactSound = true;             // the gun's surface-aware drop bank on first contact
        public static float impactSoundVolume = 0.5f;
        public static float impactSoundMinSpeed = 0.4f;    // m/s approach below which contact stays silent
        public static float impactSoundRetrigger = 0.3f;   // s between impact sounds
        public static bool collideWithGlass = true;        // windows (TransparentCollider) are solid to the gun until broken
        public static bool glassBreak = true;              // whack a window hard enough and it shatters
        public static float glassBreakMinSpeed = 2.0f;     // m/s approach needed to break
        public static float glassBreakDamage = 30f;        // melee damage carried by the synthetic hit (~knife slash)

        public static int proxyLayerOverride = -1;         // force the proxy/sandbox layer (UnityExplorer) if the auto-pick misbehaves

        private static WeaponPhysicsProxy proxy;
        private static Vector3 lastPlayerPos;              // rig root last frame (rig-motion delta for the proxy)
        private static Quaternion lastPlayerRot;
        private static bool hasLastPlayerPos;
        private static Vector3 lastRigVelocity;            // rig world translation velocity this frame (walk-into approach - the proxy's rb.velocity can't see AddRigMotion's teleport)
        private static float contactEndStamp = -10f;       // impact re-arm
        private static float lastImpactSoundStamp = -10f;
        private static int occlusionMask;
        private static bool maskIncludesGlass;
        private static int glassLayer = -1;

        // Barrel tip: farthest of fireport + muzzle-device transforms (suppressors extend past the
        // fireport), re-picked periodically so attachment changes are caught. Live world distance, NOT
        // fc.WeaponLn - vanilla's value carries a lossyScale correction for EFT's shrunken first-person
        // rig that inflated it ~15cm in the VR rig (user-measured).
        private static Transform muzzleTip;
        private static object muzzleTipOwner;
        private static float muzzleTipRefresh;

        // Enactment bookkeeping: the collision offset is applied to weaponHolder AFTER the marker anchor
        // each frame and stripped again right BEFORE the next frame's anchor (PreAnchor) - without the
        // strip it accumulates (the anchor rewrites holder localPosition per frame but rotation only at
        // equip).
        private static bool enacted;
        private static Vector3 preEnactLocalPos;
        private static Quaternion preEnactLocalRot;
        private static Vector3 lastEnactLocalPos;
        private static Quaternion lastEnactLocalRot;

        public static void Reset()
        {
            StripEnacted();
            hasLastPlayerPos = false;
            proxy?.Deactivate();
        }

        // Called at the top of UpdateWeaponPosition, before the anchor measures weaponHolder: restore the
        // holder to its pre-enactment local pose so the anchor always starts from the pristine
        // controller-desired state. Change-detection lets equip-path re-stamps win.
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

        // Runs right after UpdateWeaponPosition's marker-anchor assignment (the gun sits at the
        // controller-desired pose), as the last write to weaponHolder this frame.
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

            // Rig motion (locomotion translation + turning rotation) = a rigid transform of the whole
            // reference frame; apply it directly to the proxy (not via the velocity chase, whose per-frame
            // lag would be on-stick jitter). Reference = camRoot (the VR rig root): the stick-turn rotates
            // camRoot DIRECTLY (VRControlsPatches) and locomotion tracks camRoot to the player each frame
            // (MovementPatches), so its frame-to-frame delta is the exact rig motion. The old reference
            // (player.Transform) LAGGED on turns - the body yaw follows the headset via the aim system, so
            // the leftover rotation got dumped on the speed-capped drive = the turn jitter.
            Transform rig = VRGlobals.camRoot != null ? VRGlobals.camRoot.transform : VRGlobals.player.Transform.Original;
            Vector3 playerPos = rig.position;
            Quaternion playerRot = rig.rotation;
            if (hasLastPlayerPos)
            {
                proxy.AddRigMotion(playerRot * Quaternion.Inverse(lastPlayerRot), lastPlayerPos, playerPos);
                lastRigVelocity = (playerPos - lastPlayerPos) / Mathf.Max(Time.deltaTime, 1e-4f);
            }
            else
            {
                lastRigVelocity = Vector3.zero;
            }
            lastPlayerPos = playerPos;
            lastPlayerRot = playerRot;
            hasLastPlayerPos = true;

            proxy.Configure(weaponLn, bodyRadius, barrelRadius);
            proxy.SetTarget(rawGrip, weaponRootRotation);               // drive the proxy to the controller pose
            proxy.Step(Time.deltaTime);                    // step the sandbox NOW, at render rate (see WeaponPhysicsProxy.Step)

            // Render the PhysX-resolved pose EVERY frame (render-body). The proxy was stepped this frame, so
            // in free space it reaches the hand exactly -> EnactHolderPose auto-no-ops (its epsilon check) ->
            // the gun sits at the true controller pose, crisp. The instant a fast swing meets a wall the
            // proxy stops at it THIS frame -> the visible gun stops with it (no clip-through, no waiting for a
            // 50Hz contact event). Feedback still comes from the proxy's collision events -> OnProxyContact.
            EnactHolderPose(rawGrip, weaponRootRotation, proxy.ResolvedPos, proxy.ResolvedRot);
        }

        // Move weaponHolder so the gun's root goes from its current (un-collided) pose to the resolved
        // pose - a rigid delta applied to the holder (weaponRoot is its descendant). Captured pre/post so
        // PreAnchor strips it next frame (nothing may accumulate).
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

        // Create the sandboxed physics proxy. The world MASK selects which colliders get replicated into
        // the sandbox: STATIC WORLD (+ glass) only, NOT Player/Deadbody (else the body-sized proxy would
        // jam against your own body / corpses). The layer just needs the proxy + its copies to self-collide.
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
            proxy.gameObject.layer = layer;
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

        internal static int GetOcclusionMask()
        {
            if (occlusionMask == 0 || maskIncludesGlass != collideWithGlass)
            {
                // Same layers EFT's own weapon overlap uses; fallback = the mod's usual solid-world mask.
                LayerMask mask = EFTHardSettings.Instance != null
                    ? EFTHardSettings.Instance.WEAPON_OCCLUSION_LAYERS
                    : (LayerMask)LayerMask.GetMask("Default", "Terrain", "HighPolyCollider");
                occlusionMask = mask.value != 0 ? mask.value : LayerMask.GetMask("Default", "Terrain", "HighPolyCollider");
                glassLayer = LayerMask.NameToLayer("TransparentCollider");
                maskIncludesGlass = collideWithGlass;
                if (collideWithGlass && glassLayer >= 0)
                    occlusionMask |= 1 << glassLayer;      // windows are solid to the gun (and breakable)
            }
            return occlusionMask;
        }

        // The physics proxy reports its collisions here (fires every frame while touching). Impact-only:
        // fire once on a FRESH contact (a 0.15s gap since the last contact = you weren't touching), so
        // pressing/sliding doesn't re-fire. contactEndStamp advances every event so sustained contact stays
        // quiet.
        internal static void OnProxyContact(Collider col, Vector3 point, Vector3 normal, float approach)
        {
            bool fresh = Time.time - contactEndStamp > 0.15f;
            contactEndStamp = Time.time;
            if (col == null || !fresh)
                return;

            // Walking into a wall delivers the closing speed via AddRigMotion (a rigid teleport), which the
            // proxy's rb.velocity - and thus Collision.relativeVelocity (the `approach` passed in) - never
            // sees, so a walk-in reported ~0 approach and stayed silent (a hand-swing works because its speed
            // IS in the drive velocity). Add the rig's closing speed along the contact normal.
            approach += Mathf.Max(0f, Vector3.Dot(lastRigVelocity, -normal));

            Vector3 barrelDir = VRGlobals.firearmController?.WeaponRoot != null
                ? -VRGlobals.firearmController.WeaponRoot.up : -normal;
            if (haptics)
            {
                float amplitude = Mathf.Clamp01(approach / 2.5f) * impactHapticStrength;
                if (amplitude > 0.05f)
                    Pulse(0.08f, 85f, amplitude);
            }
            if (impactSound && approach > impactSoundMinSpeed &&
                Time.time - lastImpactSoundStamp > impactSoundRetrigger)
            {
                lastImpactSoundStamp = Time.time;
                PlayImpactSound(col, point, approach);
            }
            if (glassBreak && approach >= glassBreakMinSpeed)
                TryBreakGlass(col, point, normal, barrelDir);
        }

        // The gun's own surface-aware drop bank (the same banks vanilla plays when a weapon hits the
        // ground), played at the contact point; surface resolved off the hit collider's BaseBallistic
        // exactly like LootDropSoundPatches.
        private static void PlayImpactSound(Collider hitCollider, Vector3 point, float approach)
        {
            BetterAudio betterAudio = Singleton<BetterAudio>.Instance;
            if (betterAudio == null || betterAudio.ItemDropSounds == null)
                return;

            BaseBallistic.ESurfaceSound surface = BaseBallistic.ESurfaceSound.Concrete;
            BaseBallistic ballistic = hitCollider.GetComponent<BaseBallistic>();
            if (ballistic != null)
                surface = ballistic.GetSurfaceSound(point);

            EItemDropSoundType type = EItemDropSoundType.Rifle;
            var weapon = VRGlobals.firearmController?.Item;
            if (weapon != null && weapon.DropSoundType != EItemDropSoundType.None)
                type = weapon.DropSoundType;

            SoundBank bank = betterAudio.ItemDropSounds.GetSoundBank(surface, type);
            if (bank == null)
                return;

            // Louder the harder you hit; gentle grazes stay quiet.
            float volume = impactSoundVolume * Mathf.Lerp(0.35f, 1f, Mathf.InverseLerp(0.5f, 3.5f, approach));
            float distance = CameraManager.Instance.Distance(point);
            betterAudio.PlayAtPoint(point, bank, distance, volume, -1f, EnvironmentType.Outdoor, EOcclusionTest.OneShotPropagation);
        }

        // Whack a window with the gun -> shatter it, exactly like a VR melee surface hit: a melee
        // DamageInfoStruct through GameWorld.HackShot (glass-break handler + knife-hit effects). Mirrors
        // FikaSync/MeleeHitApply; observers replay the same melee-hit packet, so co-op needs no new
        // netcode. GLASS ONLY: a gun bump must not chip walls or leave knife decals.
        private static void TryBreakGlass(Collider hitCollider, Vector3 point, Vector3 normal, Vector3 barrelDir)
        {
            BallisticCollider ballisticCollider = hitCollider.GetComponent<BallisticCollider>();
            if (ballisticCollider == null || ballisticCollider is BodyPartCollider ||
                ballisticCollider.TypeOfMaterial.ToString().IndexOf("Glass", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
                return;

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

            // The real pane's collider is now gone - drop the sandbox copy so the gun stops colliding with
            // the phantom window (else it stays "there" until you walk >retainDistance away and back).
            proxy?.ForgetCollider(hitCollider.GetInstanceID());

            if (InstalledMods.FIKAInstalled)
                FikaSendGunSurfaceHit(point, normal, barrelDir, glassBreakDamage);
        }

        // Companion-DLL isolation (same pattern as VRMeleeController.FikaSendMeleeHit): NoInlining so the
        // cross-assembly reference is only JITted when actually called (i.e. with FIKA installed).
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
