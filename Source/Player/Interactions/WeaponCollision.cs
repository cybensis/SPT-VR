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

namespace TarkovVR.Source.Player.Interactions
{
    // Physical weapon collision via a sandboxed PhysX rigidbody (WeaponPhysicsProxy): an invisible Rigidbody shaped like the
    // gun (fat body + thin barrel capsule), driven toward the controller pose and collided in its own local physics scene
    // against copies of the nearby world colliders (so stepping it can't disturb the game's loot/ragdolls).
    // It is stepped at render rate (proxy.Step from Apply, not FixedUpdate) and the visible gun is placed on its resolved
    // pose every frame. Rig motion (locomotion + turning) is in the target, so near geometry it goes through the collision
    // (rigMotionThroughPhysics); in open space the proxy snaps to the target anyway.
    // Vanilla's TurnAwayEffector pose is suppressed for the local player (IKPatches); its OverlapValue still runs (ADS-stop
    // etc.). The fireport is a child of WeaponRoot, so shots originate at the clamped muzzle, and the FIKA arm sync
    // samples WeaponRoot's world pose, so observers see the clamp too.
    // The ComputePenetration alternative clips thin walls: don't use it.
    public static class WeaponCollision
    {
        // ---- live-tunable knobs (UnityExplorer) ----
        public static bool enabled = true;                 // master switch (also gates HandCollision + the TurnAway suppression)
        public static float bodyRadius = 0.05f;            // m; gun body/receiver capsule
        public static float barrelRadius = 0.02f;          // m; gun barrel capsule
        // false = walking / turning moves the proxy rigidly (AddRigMotion): no lag, but it can shove the gun into a wall
        public static bool rigMotionThroughPhysics = true;
        public static bool haptics = true;                 // first-impact thud only
        public static float impactHapticStrength = 1f;     // amplitude ~ approach speed
        public static bool impactSound = true;             // the gun's surface-aware drop bank on first contact
        public static float impactSoundVolume = 0.3f;
        public static float impactSoundMinSpeed = 1.2f;    // m/s the gun meets the surface at (from the hand's motion) below which it stays silent
        public static float impactSoundFullSpeed = 4f;     // m/s at which it plays at impactSoundVolume
        public static float impactSoundRetrigger = 0.8f;   // s between impact sounds
        public static float freshContactGap = 0.3f;        // s off a surface before touching it again counts as a new impact
        public static bool collideWithGlass = true;        // windows (TransparentCollider) are solid to the gun until broken
        public static bool glassBreak = true;              // whack a window hard enough and it shatters
        public static float glassBreakMinSpeed = 2.0f;     // m/s approach needed to break
        public static float glassBreakDamage = 30f;        // melee damage carried by the synthetic hit (~knife slash)
        public static bool leftoverGlassAtTouch = true;    // what's left of a broken window breaks at any touch (false = it needs the whack too)

        public static int proxyLayerOverride = -1;         // force the proxy/sandbox layer (UnityExplorer) if the auto-pick misbehaves

        private static WeaponPhysicsProxy proxy;
        private static Vector3 lastPlayerPos;              // rig root last frame (rig-motion delta for the proxy)
        private static Quaternion lastPlayerRot;
        private static bool hasLastPlayerPos;
        private static Vector3 lastTargetGrip;             // the hand's gun pose last frame: how fast the gun meets a surface
        private static Quaternion lastTargetRotation;
        private static bool hasLastTarget;
        private static Vector3 targetGrip;
        private static Vector3 targetVelocity;
        private static Vector3 targetAngularVelocity;      // rad/s
        private static float contactEndStamp = -10f;       // impact re-arm
        private static float lastImpactSoundStamp = -10f;
        private static int occlusionMask;
        private static bool maskIncludesGlass;
        private static int glassLayer = -1;

        // Barrel tip: farthest of fireport + muzzle-device transforms (suppressors extend past the fireport), re-picked
        // periodically so attachment changes are caught. Live world distance, not fc.WeaponLn: vanilla's value carries a
        // lossyScale correction for EFT's shrunken first-person rig that inflates it ~15cm in the VR rig.
        private static Transform muzzleTip;
        private static object muzzleTipOwner;
        private static float muzzleTipRefresh;

        // Enactment bookkeeping: the collision offset is applied to weaponHolder after the marker anchor each frame and
        // stripped right before the next frame's anchor (PreAnchor). Without the strip it accumulates (the anchor
        // rewrites holder localPosition per frame but rotation only at equip).
        private static bool enacted;
        private static Vector3 preEnactLocalPos;
        private static Quaternion preEnactLocalRot;
        private static Vector3 lastEnactLocalPos;
        private static Quaternion lastEnactLocalRot;

        public static void Reset()
        {
            StripEnacted();
            hasLastPlayerPos = false;
            hasLastTarget = false;
            proxy?.Deactivate();
        }

        // Called at the top of UpdateWeaponPosition, before the anchor measures weaponHolder: restores the holder's
        // pre-enactment local pose so the anchor starts from the controller-desired state. Change detection lets
        // equip-path re-stamps win.
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

            // How the hand moves the gun (locomotion and turning included): the speed it meets a surface at
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

            // Optional: rig motion (locomotion + turning) as a rigid transform of the proxy. Reference = camRoot, the VR rig
            // root: the stick turn rotates it directly (VRControlsPatches) and locomotion tracks it to the player each
            // frame (MovementPatches). player.Transform lags on turns (the body yaw follows the headset through the aim
            // system): don't use it as the reference.
            Transform rig = VRGlobals.camRoot != null ? VRGlobals.camRoot.transform : VRGlobals.player.Transform.Original;
            Vector3 playerPos = rig.position;
            Quaternion playerRot = rig.rotation;
            if (hasLastPlayerPos && !rigMotionThroughPhysics)
                proxy.AddRigMotion(playerRot * Quaternion.Inverse(lastPlayerRot), lastPlayerPos, playerPos);
            lastPlayerPos = playerPos;
            lastPlayerRot = playerRot;
            hasLastPlayerPos = true;

            proxy.Configure(weaponLn, bodyRadius, barrelRadius);
            proxy.SetTarget(rawGrip, weaponRootRotation);               // drive the proxy to the controller pose
            proxy.Step(Time.deltaTime);                    // step the sandbox now, at render rate (see WeaponPhysicsProxy.Step)

            // Draw the gun on the PhysX-resolved pose every frame. The proxy was stepped this frame: in free space it
            // reaches the hand exactly and EnactHolderPose no-ops (its epsilon check); against a wall the visible gun
            // stops the same frame the proxy does. Feedback comes from the proxy's collision events -> OnProxyContact.
            EnactHolderPose(rawGrip, weaponRootRotation, proxy.ResolvedPos, proxy.ResolvedRot);
        }

        // Moves weaponHolder so the gun's root goes from its current (un-collided) pose to the resolved pose: a rigid
        // delta on the holder (weaponRoot is its descendant). Pre/post poses are captured so PreAnchor strips it next frame.
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

        // Creates the sandboxed physics proxy. The world mask selects which colliders are copied into the sandbox: static
        // world (+ glass) only, not Player/Deadbody (the body-sized proxy would jam against your own body / corpses).
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

        // Any contact (pressing, sliding): a new impact needs freshContactGap off the surface first
        internal static void NoteContact() => contactEndStamp = Time.time;

        // The physics proxy reports its collisions here. Impact only: fires once on a fresh contact (freshContactGap since the
        // last contact event); contactEndStamp advances every event so pressing/sliding stays quiet.
        internal static void OnProxyContact(Collider col, Vector3 point, Vector3 normal)
        {
            bool fresh = Time.time - contactEndStamp > freshContactGap;
            contactEndStamp = Time.time;
            if (col == null)
                return;
            Vector3 barrelDir = VRGlobals.firearmController?.WeaponRoot != null
                ? -VRGlobals.firearmController.WeaponRoot.up : -normal;
            // What's left of a broken window gives at a touch (vanilla: walking into it breaks it). Every piece is its own collider,
            // so pushing on into the next one is a new contact.
            bool leftoverGlass = glassBreak && leftoverGlassAtTouch && IsLeftoverGlass(col);
            if (leftoverGlass)
                TryBreakGlass(col, point, normal, barrelDir);
            if (!fresh)
                return;

            // How fast the hand brings that point of the gun into the surface. Not the contact's relative velocity: that is the
            // proxy's catch-up toward a hand already inside the wall, large whenever the gun is pressed in, however gently.
            Vector3 pointVelocity = targetVelocity + Vector3.Cross(targetAngularVelocity, point - targetGrip);
            float approach = Mathf.Max(0f, Vector3.Dot(pointVelocity, -normal));
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
            if (glassBreak && !leftoverGlass && approach >= glassBreakMinSpeed)
                TryBreakGlass(col, point, normal, barrelDir);
            Door door = CollisionWorld.DoorOf(col);
            if (door != null)
                PhysicalDoors.GunHit(door, point, normal, approach);
        }

        // The gun's own surface-aware drop bank (what vanilla plays when a weapon hits the ground), played at the contact
        // point; surface resolved off the hit collider's BaseBallistic like LootDropSoundPatches.
        private static void PlayImpactSound(Collider hitCollider, Vector3 point, float approach)
        {
            BetterAudio betterAudio = Singleton<BetterAudio>.Instance;
            if (betterAudio == null || betterAudio.ItemDropSounds == null)
                return;

            BaseBallistic.ESurfaceSound surface = BaseBallistic.ESurfaceSound.Concrete;
            BaseBallistic ballistic = hitCollider.GetComponent<BaseBallistic>();
            // A movement collider (a door's, a tree trunk's) has no surface: its door's / tree's bullet mesh does
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

        // A hard whack on a window shatters it like a VR melee surface hit: a melee DamageInfoStruct through
        // GameWorld.HackShot (glass-break handler + knife-hit effects). Mirrors FikaSync/MeleeHitApply; observers replay
        // the same melee-hit packet. Glass only: a gun bump must not chip walls or leave knife decals.
        private static void TryBreakGlass(Collider hitCollider, Vector3 point, Vector3 normal, Vector3 barrelDir)
        {
            BallisticCollider ballisticCollider = hitCollider.GetComponent<BallisticCollider>();
            if (ballisticCollider == null || ballisticCollider is BodyPartCollider ||
                ballisticCollider.TypeOfMaterial.ToString().IndexOf("Glass", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
                return;
            // Our copy of glass that's already gone (a fallen piece, a shot-out pane): only the copy goes
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

        // A piece still stuck in a broken window. Tarkov makes them at the break (WindowBreaker.UpdateToStuck): a pooled object
        // under the window, a convex mesh + the window's glass BallisticCollider, a BrokenWindowPieceCollider trigger child (the
        // player walking into it breaks it). A hit or a fall turns its BallisticCollider off.
        private static bool IsLeftoverGlass(Collider collider)
        {
            if (collider.gameObject.layer != glassLayer || !IsWindowPiece(collider))
                return false;
            WindowBreaker window = collider.GetComponentInParent<WindowBreaker>();
            BallisticCollider ballistic = collider.GetComponent<BallisticCollider>();
            return window != null && window.IsDamaged && ballistic != null && ballistic.enabled;
        }

        private static bool IsWindowPiece(Collider collider) => collider.GetComponentInChildren<BrokenWindowPieceCollider>(true) != null;

        // The real colliders are gone: the sandbox copies go too, or the gun keeps hitting a phantom window until you walk more
        // than retainDistance away and back. A piece's hit knocks out the pieces around it as well, and pieces are pooled (other
        // windows reuse them): those copies go, not blacklisted. A pane is blacklisted.
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
