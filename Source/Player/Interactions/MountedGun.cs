using System.Collections.Generic;
using EFT;
using EFT.InputSystem;
using EFT.Interactive;
using RootMotion.FinalIK;
using TarkovVR.ModSupport;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Player.Interactions
{
    // Mounted guns (the AGS-30, the NSV "Utyos") by hand. Tarkov's side: Player.OperateStationaryWeapon(Occupy) walks you to the gun's
    // OperatorPosition (ApproachState) into StationaryPlayerState, which swaps your hands item for a FirearmController of the mounted gun
    // and every frame turns that gun about the world Hinge to MovementContext.Rotation, clamped to the mount's limits
    // (MovementContext.UtesRotationFunction / AGSRotationFunction); the world gun follows it (StationaryWeapon.ManualUpdate). FIKA syncs
    // Occupy / Leave (FikaPlayer.OperateStationaryWeapon) and the rotation is your look rotation.
    // Ours: both grips closed on its handles takes it (both hands free: empty, or the gun on the sling); the hands turn it about the
    // hinge; a holding hand's trigger fires it; the eye at its sight aims it; both hands open lets go of it, back to what you had.
    // The view is the headset's throughout, still in the world while Tarkov moves the body onto the gun.
    internal static class MountedGun
    {
        public static bool enabled = true;
        public static float grabRadius = 0.13f;     // m controller wrist to a handle for its grip to take the gun
        public static float regrabRadius = 0.18f;   // m on the gun: a hand that let go takes its handle again
        public static float leaveDelay = 0.3f;      // s both hands open before you let go of the gun
        public static float takeTimeout = 8f;       // s a take may take before it's called off (Tarkov's walk onto it is 1.5 s at most)
        // Co-op as a client: s on the gun before letting go takes effect. The host's copy of you gets onto it later than you (its walk onto
        // it, then the gun's spawn from your packets); let go before it's on, it never finishes the hands change off it and every later
        // move waits on an answer that doesn't come (no shooting, no reload). Open hands before then leave once the time's up.
        public static float coopMinMounted = 2.5f;
        public static float aimEyeReach = 0.2f;     // m headset to the sight to aim through it
        public static float aimEyeAngle = 25f;      // deg headset forward off the barrel to aim through it
        public static float aimExitReach = 0.28f;   // m / deg out of the aim again (wider: no flicker on the edge)
        public static float aimExitAngle = 35f;
        public static bool debugLog = true;         // a line per take / mount / let-go, plus the live handles against the zones
        public static bool debugViz = false;        // the handle zones as spheres

        // Each gun's hands in its idle hold (weapon_L_IK_marker / weapon_R_IK_marker, utes_idle / ags_idle), in its 'weapon' bone's frame:
        // StationaryWeapon.WeaponTransform is that bone in the world (ManualUpdate keeps it there). The hands' IK target is the wrist.
        private static readonly Vector3 utesLeftHandle = new Vector3(-0.105f, -0.157f, -0.112f);
        private static readonly Vector3 utesRightHandle = new Vector3(0.062f, -0.197f, -0.158f);
        private static readonly Vector3 agsLeftHandle = new Vector3(-0.27f, -0.285f, -0.267f);
        private static readonly Vector3 agsRightHandle = new Vector3(0.092f, -0.288f, -0.276f);

        private const int LEFT = 0, RIGHT = 1;

        private enum Stage { Free, Taking, Mounted }
        private static Stage stage;
        private static StationaryWeapon gun;
        private static bool cameFromEmptyHands;
        private static float takenAt;
        private static float mountedAt;
        private static bool takeArmed;                                    // both grips closed on the handles: taken once the host is free
        private static readonly bool[] holding = new bool[2];
        private static readonly bool[] previousGrip = new bool[2];
        private static float allOpenSince = -1f;
        private static readonly ZoneEntryBlip[] handleBlips = { new ZoneEntryBlip(), new ZoneEntryBlip() };

        // The aim: the hands' lever off the hinge turns the gun by as much as it turned since the base was taken
        private static bool aimBased;
        private static int aimBaseHands;
        private static Vector3 aimBaseLever;
        private static Vector2 aimBaseRotation;
        private static float lastAimToggle;

        // The view held still in the world: the rig anchor's moves taken out of the offsetter
        private static bool rigPinned;
        private static Vector3 lastAnchor;

        private static readonly List<StationaryWeapon> guns = new List<StationaryWeapon>();
        private static object gunsFor;
        private static float nextGunScan;
        private static GameObject leftZoneSphere, rightZoneSphere;

        //---------------------------------------------------------------------------------------------------------
        // State

        // Tarkov has you on a mounted gun (walking to it, on it, or getting off it)
        internal static bool Active
        {
            get
            {
                EFT.Player player = VRGlobals.player;
                return enabled && player != null && player.IsYourPlayer && player.MovementContext != null && player.MovementContext.StationaryWeapon != null
                    && player.HealthController != null && player.HealthController.IsAlive;
            }
        }

        // On it, the gun in the hands
        internal static bool Mounted => MountedController(VRGlobals.player) != null;

        // The body's left / right hand holding its handle (the left controller drives the left arm)
        internal static bool HandOnHandle(bool left) => Mounted && holding[left ? LEFT : RIGHT];

        // The mounted gun's controller, while on it
        internal static FirearmController MountedController(EFT.Player player)
        {
            if (!enabled || player == null || player.MovementContext == null)
                return null;
            StationaryWeapon stationaryWeapon = player.MovementContext.StationaryWeapon;
            if (stationaryWeapon == null || !(player.MovementContext.CurrentState is StationaryPlayerState state) || state.Stage != StationaryPlayerState.EStationaryStage.Main)
                return null;
            return player.HandsController is FirearmController firearmController && firearmController.Item == stationaryWeapon.Item ? firearmController : null;
        }

        // A firearm coming into the hands is the mounted gun (none of the held-gun setup for it: Tarkov places it on its mount)
        internal static bool IsMountedGun(FirearmController firearmController)
        {
            EFT.Player player = firearmController != null ? firearmController._player : null;
            return enabled && player != null && player.MovementContext != null && player.MovementContext.StationaryWeapon != null
                && firearmController.Item == player.MovementContext.StationaryWeapon.Item;
        }

        // WeaponPatches.MoveWeaponToIKHands, the mounted gun's turn: the hands are nobody's gun hands now
        internal static void OnGunInHands(FirearmController firearmController)
        {
            VRGlobals.player = firearmController._player;
            VRGlobals.firearmController = null;
            VRGlobals.emptyHands = null;
            VRGlobals.switchingWeapon = false;
            VRGlobals.usingItem = false;
            WeaponPatches.grenadeEquipped = false;
            if (WeaponPatches.currentGunInteractController != null)
                WeaponPatches.currentGunInteractController.enabled = false;
            if (VRGlobals.vrPlayer != null)
                VRGlobals.vrPlayer.isWeapPistol = false;
            if (debugLog)
                Plugin.MyLog.LogInfo($"[MountedGun] {firearmController.Item.LocalizedShortName()} in the hands");
        }

        //---------------------------------------------------------------------------------------------------------
        // Per frame (HandsInteractionController.Update, before everything else there): true = the hands are the mounted gun's this frame

        internal static bool Tick(bool draggingBody)
        {
            EFT.Player player = VRGlobals.player;
            VRPlayerManager rig = VRGlobals.vrPlayer;
            bool leftGrip = VRInput.LeftGrip.state, rightGrip = VRInput.RightGrip.state;
            bool leftPressed = leftGrip && !previousGrip[LEFT], rightPressed = rightGrip && !previousGrip[RIGHT];
            previousGrip[LEFT] = leftGrip;
            previousGrip[RIGHT] = rightGrip;
            if (!enabled || player == null || !(rig is RaidVRPlayerManager))
            {
                stage = Stage.Free;
                takeArmed = false;
                ShowZones(false, Vector3.zero, Vector3.zero);
                return false;
            }
            if (Active)
            {
                ShowZones(false, Vector3.zero, Vector3.zero);
                TickOnGun(player, leftGrip, rightGrip, leftPressed, rightPressed);
                return true;
            }
            if (stage != Stage.Free)
                OffTheGun();
            return TickReach(player, draggingBody, leftGrip, rightGrip, leftPressed, rightPressed);
        }

        private static bool TickReach(EFT.Player player, bool draggingBody, bool leftGrip, bool rightGrip, bool leftPressed, bool rightPressed)
        {
            StationaryWeapon nearest = HandsFree(player, draggingBody) ? NearestGun(player) : null;
            if (nearest == null)
            {
                handleBlips[LEFT].Enter(false);
                handleBlips[RIGHT].Enter(false);
                takeArmed = false;
                ShowZones(false, Vector3.zero, Vector3.zero);
                return false;
            }
            HandlePoints(nearest, out Vector3 leftHandle, out Vector3 rightHandle);
            ShowZones(debugViz, leftHandle, rightHandle);
            bool leftAt = (Wrist(true) - leftHandle).sqrMagnitude < grabRadius * grabRadius;
            bool rightAt = (Wrist(false) - rightHandle).sqrMagnitude < grabRadius * grabRadius;
            if (handleBlips[LEFT].Enter(leftAt))
                HapticMixer.PlayPulse(SteamVR_Input_Sources.LeftHand, 0.2f, 0.04f, 120f);
            if (handleBlips[RIGHT].Enter(rightAt))
                HapticMixer.PlayPulse(SteamVR_Input_Sources.RightHand, 0.2f, 0.04f, 120f);
            if (!leftAt && !rightAt)
            {
                takeArmed = false;
                return false;
            }
            bool bothHeld = leftAt && rightAt && leftGrip && rightGrip;
            if (bothHeld && (leftPressed || rightPressed))
                takeArmed = true;
            else if (!bothHeld)
                takeArmed = false;
            // A FIKA client waits on the host's answer to its last hands change (FikaPlayer refuses the Occupy meanwhile)
            if (takeArmed && !CoopHost.StillAnswering)
            {
                takeArmed = false;
                Take(player, nearest);
            }
            // A hand on a handle: its grip is the gun's, not the loot pointer's or a door's
            return true;
        }

        private static void Take(EFT.Player player, StationaryWeapon stationaryWeapon)
        {
            if (!stationaryWeapon.IsAvailable(player.ProfileId) || player.IsInPronePose)
                return;
            cameFromEmptyHands = player.HandsIsEmpty;
            player.OperateStationaryWeapon(stationaryWeapon, StationaryWeaponPacket.EStationaryCommand.Occupy);
            if (player.MovementContext.StationaryWeapon != stationaryWeapon)
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo("[MountedGun] Tarkov didn't take the gun (hands busy)");
                return;
            }
            gun = stationaryWeapon;
            stage = Stage.Taking;
            takenAt = Time.time;
            holding[LEFT] = holding[RIGHT] = true;
            allOpenSince = -1f;
            aimBased = false;
            HapticMixer.PlayPulse(SteamVR_Input_Sources.LeftHand, 0.45f, 0.06f, 90f);
            HapticMixer.PlayPulse(SteamVR_Input_Sources.RightHand, 0.45f, 0.06f, 90f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[MountedGun] taking {stationaryWeapon.Item.LocalizedShortName()} ({stationaryWeapon.Animation}), from {(cameFromEmptyHands ? "empty hands" : "the sling")}");
        }

        private static void TickOnGun(EFT.Player player, bool leftGrip, bool rightGrip, bool leftPressed, bool rightPressed)
        {
            if (stage == Stage.Free)
            {
                // On it some other way (a raid joined on the gun): both hands on it while the grips are closed
                gun = player.MovementContext.StationaryWeapon;
                stage = Stage.Taking;
                takenAt = Time.time;
                cameFromEmptyHands = true;
                holding[LEFT] = leftGrip;
                holding[RIGHT] = rightGrip;
            }
            FirearmController firearmController = MountedController(player);
            if (firearmController == null)
            {
                // Walking onto it, the gun coming into the hands, or getting off it. A take Tarkov never finishes is called off:
                // the hands and the sticks are the gun's until then.
                if (stage == Stage.Taking && Time.time - takenAt > takeTimeout && player.MovementContext.StationaryWeapon != null)
                {
                    Plugin.MyLog.LogWarning($"[MountedGun] not on the gun {takeTimeout:0}s after taking it ({player.MovementContext.CurrentState?.GetType().Name}): called off");
                    takenAt = float.PositiveInfinity;
                    player.OperateStationaryWeapon(player.MovementContext.StationaryWeapon, StationaryWeaponPacket.EStationaryCommand.Denied);
                    // Never got to the gun's own state, whose Exit lets go of it: let go of it here
                    if (!(player.MovementContext.CurrentState is StationaryPlayerState))
                        player.MovementContext.StationaryWeapon = null;
                }
                return;
            }
            if (stage == Stage.Taking)
            {
                stage = Stage.Mounted;
                mountedAt = Time.time;
                aimBased = false;
                if (debugLog)
                    LogHandles(firearmController);
            }

            bool[] grips = { leftGrip, rightGrip };
            bool[] pressed = { leftPressed, rightPressed };
            for (int hand = LEFT; hand <= RIGHT; hand++)
            {
                if (holding[hand] && !grips[hand])
                    holding[hand] = false;
                else if (!holding[hand] && pressed[hand])
                {
                    Transform handle = LiveHandle(firearmController, hand == LEFT);
                    if (handle != null && (Wrist(hand == LEFT) - handle.position).sqrMagnitude < regrabRadius * regrabRadius)
                    {
                        holding[hand] = true;
                        HapticMixer.PlayPulse(hand == LEFT ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand, 0.3f, 0.05f, 90f);
                    }
                }
            }

            if (holding[LEFT] || holding[RIGHT])
            {
                allOpenSince = -1f;
                return;
            }
            if (allOpenSince < 0f)
                allOpenSince = Time.time;
            if (Time.time - allOpenSince >= leaveDelay && (!CoopHost.IsClient || Time.time - mountedAt >= coopMinMounted))
                LetGo(player);
        }

        // Tarkov's own way off (StationaryPlayerState.OnInteraction = the interact key): its Leave puts your weapon in your hands, made
        // empty hands when you came with them (MountedGunPatches). A slung gun goes back to its spot (ChestHang.HangOnArrival).
        private static void LetGo(EFT.Player player)
        {
            if (!(player.MovementContext.CurrentState is StationaryPlayerState state) || state.Stage != StationaryPlayerState.EStationaryStage.Main)
                return;
            allOpenSince = -1f;
            if (!cameFromEmptyHands)
                ChestHang.HangOnArrival(5f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[MountedGun] letting go, back to {(cameFromEmptyHands ? "empty hands" : "the slung gun")}");
            state.OnInteraction();
        }

        // Player.OperateStationaryWeapon(Leave) prefix: true = do it as empty hands instead of the active weapon
        internal static bool LeavesToEmptyHands(EFT.Player player)
        {
            return enabled && player != null && player.IsYourPlayer && stage != Stage.Free && cameFromEmptyHands;
        }

        private static void OffTheGun()
        {
            if (debugLog)
                Plugin.MyLog.LogInfo("[MountedGun] off the gun");
            stage = Stage.Free;
            gun = null;
            holding[LEFT] = holding[RIGHT] = false;
            aimBased = false;
            takeArmed = false;
            // Hands still at the handles: no blip until they've been away
            handleBlips[LEFT].MarkInside();
            handleBlips[RIGHT].MarkInside();
        }

        //---------------------------------------------------------------------------------------------------------
        // The aim (MovementPatches.SetPlayerRotate, MovementState.Rotate prefix): true = the body's rotation is ours / Tarkov's here

        internal static bool DriveRotation(MovementState state)
        {
            if (!Active)
                return false;
            // Walking onto / off the gun: Tarkov turns the body to it, not the headset
            if (!(state is StationaryPlayerState stationary) || stationary.Stage != StationaryPlayerState.EStationaryStage.Main)
                return true;
            EFT.Player player = VRGlobals.player;
            MovementContext movementContext = state.MovementContext;
            if (MountedController(player) == null || gun == null || gun.Hinge == null)
                return true;
            int hands = (holding[LEFT] ? 1 : 0) | (holding[RIGHT] ? 2 : 0);
            if (hands == 0)
                return true;   // let go: the gun stays where it was
            Vector3 grip = hands == 3 ? (Wrist(true) + Wrist(false)) * 0.5f : Wrist(hands == 1);
            Vector3 lever = grip - gun.Hinge.position;
            if (lever.sqrMagnitude < 1e-4f)
                return true;
            // A new base whenever the hands on it change, so a hand letting go or coming back doesn't jump the gun
            if (!aimBased || hands != aimBaseHands)
            {
                aimBased = true;
                aimBaseHands = hands;
                aimBaseLever = lever;
                aimBaseRotation = movementContext.Rotation;
            }
            // A rigid turn about the hinge: the handles swing about the vertical by as much as the muzzle does, and rise by as much as
            // it dips (pitch is positive down)
            float yaw = aimBaseRotation.x + Mathf.DeltaAngle(YawOf(aimBaseLever), YawOf(lever));
            float pitch = aimBaseRotation.y + (ElevationOf(lever) - ElevationOf(aimBaseLever));
            movementContext.Rotation = new Vector2(yaw, pitch);
            return true;
        }

        private static float YawOf(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        private static float ElevationOf(Vector3 direction) => Mathf.Asin(Mathf.Clamp(direction.y / direction.magnitude, -1f, 1f)) * Mathf.Rad2Deg;

        //---------------------------------------------------------------------------------------------------------
        // Fire and aim (InputHandlers.FireHandler / AimHandler)

        internal static bool TriggerPulled => (holding[LEFT] && VRInput.LeftTrigger.axis > 0.5f) || (holding[RIGHT] && VRInput.RightTrigger.axis > 0.5f);

        // The eye at the sight and looking down the barrel: aiming (the AGS's PAG-17 only draws its view while aiming)
        internal static void UpdateAim(ref ECommand command)
        {
            EFT.Player player = VRGlobals.player;
            FirearmController firearmController = MountedController(player);
            Camera headset = VRGlobals.VRCam;
            if (firearmController == null || headset == null || Time.time - lastAimToggle < 0.3f)
                return;
            EFT.Animations.ProceduralWeaponAnimation animation = player.ProceduralWeaponAnimation;
            Transform sight = animation != null && animation.ScopeAimTransforms != null && animation.ScopeAimTransforms.Count > 0 && animation.CurrentScope != null
                ? animation.CurrentScope.Bone : null;
            Transform weapon = firearmController.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon);
            if (sight == null || weapon == null)
                return;
            float reach = Vector3.Distance(headset.transform.position, sight.position);
            float angle = Vector3.Angle(headset.transform.forward, -weapon.up);
            bool aiming = firearmController.IsAiming;
            if (!aiming && reach <= aimEyeReach && angle <= aimEyeAngle)
            {
                command = ECommand.ToggleAlternativeShooting;
                lastAimToggle = Time.time;
            }
            else if (aiming && (reach > aimExitReach || angle > aimExitAngle))
            {
                command = ECommand.EndAlternativeShooting;
                lastAimToggle = Time.time;
            }
        }

        // A shot: both holding hands feel it
        internal static bool Recoil(float kick)
        {
            if (!Mounted)
                return false;
            if (holding[LEFT])
                HapticMixer.PlayPulse(SteamVR_Input_Sources.LeftHand, kick, 0.05f, 60f);
            if (holding[RIGHT])
                HapticMixer.PlayPulse(SteamVR_Input_Sources.RightHand, kick, 0.05f, 60f);
            return true;
        }

        //---------------------------------------------------------------------------------------------------------
        // The arms (IKPatches.RemoveSprintAnimFromHands, Player.IkProcess prefix: the last word before the IK solves)

        internal static bool DriveArms(EFT.Player player)
        {
            if (!Active || player != VRGlobals.player || VRGlobals.ikManager == null || VRGlobals.vrPlayer == null)
                return false;
            FirearmController firearmController = MountedController(player);
            Transform left = holding[LEFT] && firearmController != null ? LiveHandle(firearmController, true) : null;
            Transform right = holding[RIGHT] && firearmController != null ? LiveHandle(firearmController, false) : null;
            DriveArm(VRGlobals.ikManager.leftArmIk, left ?? RigHand(true));
            DriveArm(VRGlobals.ikManager.rightArmIk, right ?? RigHand(false));
            player._markers[0] = left ?? RigHand(true);
            player._markers[1] = right ?? RigHand(false);
            bool leftHanded = VRSettings.GetLeftHandedMode();
            player._elbowBends[0] = leftHanded ? VRGlobals.rightArmBendGoal : VRGlobals.leftArmBendGoal;
            player._elbowBends[1] = leftHanded ? VRGlobals.leftArmBendGoal : VRGlobals.rightArmBendGoal;
            return true;
        }

        private static void DriveArm(LimbIK arm, Transform target)
        {
            if (arm == null || target == null)
                return;
            arm.solver.target = target;
            arm.enabled = true;
        }

        // The gun's own hand markers (the arms animator holds them on the handles)
        private static Transform LiveHandle(FirearmController firearmController, bool left)
        {
            return firearmController.HandsHierarchy != null
                ? firearmController.HandsHierarchy.GetTransform(left ? ECharacterWeaponBones.weapon_L_IK_marker : ECharacterWeaponBones.weapon_R_IK_marker)
                : null;
        }

        //---------------------------------------------------------------------------------------------------------
        // The view (VRPlayerManager.LateUpdate, after its own pin): camRoot on the body like always, its moves taken back out of the
        // offsetter so the headset alone moves the view (Tarkov slides the body onto the gun, sits it down, the upper body bends)

        internal static void PinRig()
        {
            if (!Active)
            {
                rigPinned = false;
                return;
            }
            EFT.Player player = VRGlobals.player;
            Transform ribcage = player.PlayerBones != null && player.PlayerBones.Ribcage != null ? player.PlayerBones.Ribcage.Original : null;
            if (ribcage == null || VRGlobals.camRoot == null || VRGlobals.vrOffsetter == null || VRGlobals.vrOffsetter.transform.parent == null)
                return;
            // The same anchor the empty-hands / gun pins use (the hands object sits on the ribcage), so getting off doesn't jump
            Vector3 anchor = new Vector3(ribcage.position.x, player.Transform.position.y + 1.5f, ribcage.position.z);
            if (rigPinned)
            {
                Vector3 moved = anchor - lastAnchor;
                moved.y = 0f;
                Vector3 local = VRGlobals.vrOffsetter.transform.parent.InverseTransformVector(moved);
                local.y = 0f;
                VRGlobals.vrPlayer.initPos += local;
                VRGlobals.vrOffsetter.transform.localPosition -= local;
            }
            VRGlobals.camRoot.transform.position = anchor;
            lastAnchor = anchor;
            rigPinned = true;
        }

        //---------------------------------------------------------------------------------------------------------
        // Helpers

        private static bool HandsFree(EFT.Player player, bool draggingBody)
        {
            if (player.IsInPronePose || player.IsSprintEnabled || VRGlobals.usingItem || draggingBody || HandsInteractionController.HoldingLoot
                || EatingInteractionController.ManualActive || WeaponPatches.grenadeEquipped)
                return false;
            if (PhysicalDoors.HoldsRig(RigHand(true)) || PhysicalDoors.HoldsRig(RigHand(false)))
                return false;
            return player.HandsIsEmpty || (ChestHang.Hanging && player.HandsController is FirearmController);
        }

        // The nearest mounted gun in reach of the hands (the mounted guns found once per map)
        private static StationaryWeapon NearestGun(EFT.Player player)
        {
            GameWorld world = Comfort.Common.Singleton<GameWorld>.Instantiated ? Comfort.Common.Singleton<GameWorld>.Instance : null;
            if (!ReferenceEquals(world, gunsFor) || (guns.Count == 0 && Time.time >= nextGunScan))
            {
                guns.Clear();
                gunsFor = world;
                nextGunScan = Time.time + 10f;
                if (world != null)
                {
                    foreach (StationaryWeapon stationaryWeapon in LocationScene.GetAllObjects<StationaryWeapon>())
                    {
                        if (stationaryWeapon != null)
                            guns.Add(stationaryWeapon);
                    }
                    if (debugLog)
                        Plugin.MyLog.LogInfo($"[MountedGun] {guns.Count} mounted guns on this map");
                }
            }
            Vector3 head = VRGlobals.VRCam != null ? VRGlobals.VRCam.transform.position : player.Position;
            StationaryWeapon nearest = null;
            float nearestDistance = 2.5f * 2.5f;
            foreach (StationaryWeapon stationaryWeapon in guns)
            {
                if (stationaryWeapon == null || stationaryWeapon.WeaponTransform == null || stationaryWeapon.Item == null || !stationaryWeapon.gameObject.activeInHierarchy
                    || !stationaryWeapon.IsAvailable(player.ProfileId))
                    continue;
                float distance = (stationaryWeapon.WeaponTransform.position - head).sqrMagnitude;
                if (distance < nearestDistance)
                {
                    nearest = stationaryWeapon;
                    nearestDistance = distance;
                }
            }
            return nearest;
        }

        private static void HandlePoints(StationaryWeapon stationaryWeapon, out Vector3 left, out Vector3 right)
        {
            bool ags = stationaryWeapon.Animation == StationaryWeapon.EStationaryAnimationType.AGS_17;
            Transform weapon = stationaryWeapon.WeaponTransform;
            left = weapon.position + weapon.rotation * (ags ? agsLeftHandle : utesLeftHandle);
            right = weapon.position + weapon.rotation * (ags ? agsRightHandle : utesRightHandle);
        }

        // The rig hand on the left / right controller (left-handed mode puts the rig's right hand on the left controller)
        private static Transform RigHand(bool left)
        {
            VRPlayerManager rig = VRGlobals.vrPlayer;
            if (rig == null)
                return null;
            GameObject hand = left != VRSettings.GetLeftHandedMode() ? rig.LeftHand : rig.RightHand;
            return hand != null ? hand.transform : null;
        }

        // That controller's wrist (the rig hand), the world clamp left out
        private static Vector3 Wrist(bool left)
        {
            Transform hand = RigHand(left);
            return hand != null ? HandCollision.RawPosition(hand) : Vector3.zero;
        }

        private static void LogHandles(FirearmController firearmController)
        {
            if (gun == null || gun.WeaponTransform == null)
                return;
            Transform weapon = gun.WeaponTransform;
            Transform left = LiveHandle(firearmController, true), right = LiveHandle(firearmController, false);
            string Local(Transform t) => t != null ? (Quaternion.Inverse(weapon.rotation) * (t.position - weapon.position)).ToString("F3") : "none";
            Plugin.MyLog.LogInfo($"[MountedGun] on {gun.Item.LocalizedShortName()} ({gun.Animation}): live handles in its weapon frame left {Local(left)} right {Local(right)}"
                + $" (zones {(gun.Animation == StationaryWeapon.EStationaryAnimationType.AGS_17 ? agsLeftHandle : utesLeftHandle).ToString("F3")}"
                + $" / {(gun.Animation == StationaryWeapon.EStationaryAnimationType.AGS_17 ? agsRightHandle : utesRightHandle).ToString("F3")}), rotation {VRGlobals.player.MovementContext.Rotation}");
        }

        private static void ShowZones(bool show, Vector3 left, Vector3 right)
        {
            DebugShapes.Sphere(ref leftZoneSphere, "MountedGunLeftZone", show, left, grabRadius, new Color(0.2f, 0.5f, 1f, 0.35f));
            DebugShapes.Sphere(ref rightZoneSphere, "MountedGunRightZone", show, right, grabRadius, new Color(1f, 0.5f, 0.2f, 0.35f));
        }
    }
}
