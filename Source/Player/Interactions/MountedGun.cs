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
    // Mounted guns (the AGS-30 and the NSV "Utyos") operated by hand.
    // In Tarkov, Player.OperateStationaryWeapon(Occupy) walks you to the gun's OperatorPosition (ApproachState) and enters
    // StationaryPlayerState. That state swaps your hands item for a FirearmController of the mounted gun, and every frame turns the gun
    // about the world Hinge to MovementContext.Rotation, clamped to the mount's limits (MovementContext.UtesRotationFunction /
    // AGSRotationFunction). The world gun follows it (StationaryWeapon.ManualUpdate). FIKA syncs Occupy / Leave
    // (FikaPlayer.OperateStationaryWeapon), and the rotation is your look rotation.
    // Here, closing both grips on its handles takes the gun (the Utyos only needs the right one), if both hands are free: empty, or the
    // gun on the sling. The body faces the aim and stays under your head (no step clips). The hands turn the gun about the hinge, a
    // holding hand's trigger fires it, and your eye at its sight aims it. Opening both hands lets go and returns what you held before.
    // The view stays the headset's the whole time, still in the world while Tarkov moves the body onto the gun.
    internal static class MountedGun
    {
        public static bool enabled = true;
        public static float grabRadius = 0.13f;     // m, controller wrist to a handle for its grip to take the gun
        public static float regrabRadius = 0.18f;   // m, on the gun: a hand that let go within this of its handle takes it again
        // The Utyos is held by its right handle alone (Tarkov's left hand only rests on its stock), so the left hand stays on your controller.
        // The body on the gun faces its aim, so none of Tarkov's step clips play (they play when the aim turns away from the body), and it
        // stands under your head instead of where its steps would take it. false = Tarkov's steps.
        public static bool bodyFollowsHead = true;
        public static float bodyFollowDeadZone = 0.2f;  // m your head may be off the body's own head before the body moves (leaning)
        public static float bodyFollowTime = 0.12f;     // s the body takes to catch up past that
        public static float leaveDelay = 0.3f;      // s both hands must be open before you let go of the gun
        public static float takeTimeout = 8f;       // s a take may last before it's called off (Tarkov's walk onto the gun is 1.5 s at most)
        // As a co-op client: s on the gun before letting go takes effect. The host's copy of you gets onto the gun later than you (it walks
        // there, then spawns the gun from your packets). Letting go before that means the host never finishes the hands change off the gun,
        // and every later move waits for an answer that never comes (no shooting, no reload). Hands opened earlier let go once the time is up.
        public static float coopMinMounted = 2.5f;
        public static float aimEyeReach = 0.2f;     // m, headset to the sight to aim through it
        public static float aimEyeAngle = 25f;      // deg, headset forward off the barrel to aim through it
        public static float aimExitReach = 0.28f;   // m / deg to stop aiming again (wider, so it doesn't flicker at the edge)
        public static float aimExitAngle = 35f;
        // The gun's turn from the hands is One Euro filtered: tracking noise is held still at rest, a fast swing is followed closely.
        public static float aimMinCutoff = 2f;          // Hz at rest: lower = steadier, more lag starting a turn
        public static float aimSpeedCutoff = 0.05f;     // Hz added per deg/s of turn
        public static float aimDerivativeCutoff = 1f;   // Hz, the turn speed's own smoothing
        public static bool debugLog = false;        // a line per take / mount / let-go, plus the live handles against the zones
        public static bool debugViz = false;        // the handle zones as spheres

        // Each gun's hand positions in its idle hold (weapon_L_IK_marker / weapon_R_IK_marker in utes_idle / ags_idle), in its 'weapon'
        // bone's frame. StationaryWeapon.WeaponTransform is that bone in the world (ManualUpdate keeps it there). The hands' IK target is
        // the wrist. The Utyos's left value is where a VR wrist sits holding that handle (the mean of 5 measured grabs): Tarkov's left hand
        // holds that handle turned round, fingers back toward the stock, so its marker is 13 cm off a hand held the usual way.
        private static readonly Vector3 utesLeftHandle = new Vector3(-0.067f, -0.024f, -0.044f);
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
        private static bool takeArmed;                                    // grips closed on the handles: the gun is taken once the host is free
        private static readonly bool[] holding = new bool[2];
        private static readonly bool[] previousGrip = new bool[2];
        private static float allOpenSince = -1f;
        private static readonly ZoneEntryBlip[] handleBlips = { new ZoneEntryBlip(), new ZoneEntryBlip() };

        // Aim: the gun turns by as much as the hands' lever from the hinge has turned since the base was taken
        private static bool aimBased;
        private static int aimBaseHands;
        private static Vector3 aimBaseLever;
        private static Vector3 aimPivot;                                  // the hinge position when the base was taken; the lever starts here
        private static Vector2 aimBaseRotation;
        private static float lastAimToggle;
        private static float nextAimLog;

        // The wrists are sampled once a frame after the rig is final (PinRig, in LateUpdate), so the aim reads the same moment every frame
        private static readonly Vector3[] sampledWrist = new Vector3[2];
        private static bool wristsSampled;

        private static bool aimFiltered;
        private static Vector2 filteredAim;
        private static Vector2 previousRawAim;
        private static Vector2 aimSpeed;
        private static int filteredFrame = -1;

        private static float nextReachLog;

        // Keeps the view still in the world: the rig anchor's moves are taken back out of the offsetter
        private static bool rigPinned;
        private static Vector3 lastAnchor;

        private static readonly List<StationaryWeapon> guns = new List<StationaryWeapon>();
        private static object gunsFor;
        private static float nextGunScan;
        private static int loggedGunCount = -1;
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

        // On the gun, with the gun in the hands
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

        // True if a firearm coming into the hands is the mounted gun. It gets none of the held-gun setup: Tarkov places it on its mount.
        internal static bool IsMountedGun(FirearmController firearmController)
        {
            EFT.Player player = firearmController != null ? firearmController._player : null;
            return enabled && player != null && player.MovementContext != null && player.MovementContext.StationaryWeapon != null
                && firearmController.Item == player.MovementContext.StationaryWeapon.Item;
        }

        // Called from WeaponPatches.MoveWeaponToIKHands when the mounted gun comes into the hands: clears the held-gun state
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
        // Per frame, from HandsInteractionController.Update before everything else there. true = the hands belong to the mounted gun this frame

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
            StationaryWeapon near = NearestGun(player);
            string busy = near != null ? HandsBusy(player, draggingBody) : null;
            if (near != null && busy == null && !near.IsAvailable(player.ProfileId))
                busy = "someone else's (in use, or locked to its operator)";
            StationaryWeapon nearest = busy == null ? near : null;
            if (near != null && debugLog && (leftPressed || rightPressed) && Time.time >= nextReachLog)
                LogReach(near, busy);
            if (nearest == null)
            {
                handleBlips[LEFT].Enter(false);
                handleBlips[RIGHT].Enter(false);
                takeArmed = false;
                ShowZones(false, Vector3.zero, Vector3.zero);
                return false;
            }
            HandlePoints(nearest, out Vector3 leftHandle, out Vector3 rightHandle);
            bool oneHanded = OneHanded(nearest);
            ShowZones(debugViz, leftHandle, rightHandle, showLeft: !oneHanded);
            bool leftAt = !oneHanded && (Wrist(true) - leftHandle).sqrMagnitude < grabRadius * grabRadius;
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
            // With "Toggle Gun Grip", a press with the hands at the handles takes the gun, and the grips may open after
            bool toggle = VRSettings.GetGunGripToggle();
            bool leftOn = leftAt && (leftGrip || toggle);
            bool held = rightAt && (rightGrip || toggle) && (oneHanded || leftOn);
            if (held && (rightPressed || (!oneHanded && leftPressed)))
                takeArmed = true;
            else if (!held)
                takeArmed = false;
            // A FIKA client waits on the host's answer to its last hands change (FikaPlayer refuses the Occupy meanwhile)
            if (takeArmed && !CoopHost.StillAnswering)
            {
                takeArmed = false;
                Take(player, nearest, leftOn);
            }
            // A hand is at a handle: its grip belongs to the gun, not to the loot pointer or a door
            return true;
        }

        private static bool OneHanded(StationaryWeapon stationaryWeapon) =>
            stationaryWeapon != null && stationaryWeapon.Animation != StationaryWeapon.EStationaryAnimationType.AGS_17;

        private static void Take(EFT.Player player, StationaryWeapon stationaryWeapon, bool leftOn)
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
            holding[LEFT] = leftOn;
            holding[RIGHT] = true;
            allOpenSince = -1f;
            aimBased = false;
            if (leftOn)
                HapticMixer.PlayPulse(SteamVR_Input_Sources.LeftHand, 0.45f, 0.06f, 90f);
            HapticMixer.PlayPulse(SteamVR_Input_Sources.RightHand, 0.45f, 0.06f, 90f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[MountedGun] taking {stationaryWeapon.Item.LocalizedShortName()} ({stationaryWeapon.Animation}), from {(cameFromEmptyHands ? "empty hands" : "the sling")}, {(leftOn ? "both hands" : "the right hand")}");
        }

        private static void TickOnGun(EFT.Player player, bool leftGrip, bool rightGrip, bool leftPressed, bool rightPressed)
        {
            if (stage == Stage.Free)
            {
                // On the gun some other way (e.g. a raid joined on the gun): each hand whose grip is closed holds it
                gun = player.MovementContext.StationaryWeapon;
                stage = Stage.Taking;
                takenAt = Time.time;
                cameFromEmptyHands = true;
                bool toggleHolds = VRSettings.GetGunGripToggle();
                holding[LEFT] = (leftGrip || toggleHolds) && !OneHanded(gun);
                holding[RIGHT] = rightGrip || toggleHolds;
            }
            FirearmController firearmController = MountedController(player);
            if (firearmController == null)
            {
                // Walking onto the gun, the gun coming into the hands, or getting off it. A take Tarkov never finishes is called off,
                // because the hands and the sticks belong to the gun until then.
                if (stage == Stage.Taking && Time.time - takenAt > takeTimeout && player.MovementContext.StationaryWeapon != null)
                {
                    Plugin.MyLog.LogWarning($"[MountedGun] not on the gun {takeTimeout:0}s after taking it ({player.MovementContext.CurrentState?.GetType().Name}): called off");
                    takenAt = float.PositiveInfinity;
                    player.OperateStationaryWeapon(player.MovementContext.StationaryWeapon, StationaryWeaponPacket.EStationaryCommand.Denied);
                    // Never reached StationaryPlayerState, whose Exit would let go of the gun, so let go of it here
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
            bool zonesKnown = gun != null && gun.WeaponTransform != null;
            Vector3 leftZone = Vector3.zero, rightZone = Vector3.zero;
            if (zonesKnown)
                HandlePoints(gun, out leftZone, out rightZone);
            bool toggle = VRSettings.GetGunGripToggle();
            for (int hand = OneHanded(gun) ? RIGHT : LEFT; hand <= RIGHT; hand++)
            {
                // Hold mode: opening the grip lets go. Toggle mode: a press does.
                if (holding[hand] && (toggle ? pressed[hand] : !grips[hand]))
                    holding[hand] = false;
                else if (!holding[hand] && pressed[hand])
                {
                    // Distance to its live marker or its zone, whichever is closer (the Utyos's left marker is 13 cm off where a VR wrist sits)
                    Vector3 wrist = Wrist(hand == LEFT);
                    Transform handle = LiveHandle(firearmController, hand == LEFT);
                    float reach = handle != null ? (wrist - handle.position).sqrMagnitude : float.PositiveInfinity;
                    if (zonesKnown)
                        reach = Mathf.Min(reach, (wrist - (hand == LEFT ? leftZone : rightZone)).sqrMagnitude);
                    if (reach < regrabRadius * regrabRadius)
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

        // Gets off the way Tarkov does (StationaryPlayerState.OnInteraction = the interact key). Its Leave puts your weapon in your hands,
        // or empty hands if you came with empty hands (MountedGunPatches). A slung gun goes back to its sling spot (ChestHang.HangOnArrival).
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
        // The aim (MovementPatches.SetPlayerRotate, a MovementState.Rotate prefix). true = the body's rotation is handled here (by us, or
        // left to Tarkov's stationary state), so the patch skips the normal rotation

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
            {
                aimFiltered = false;
                return true;   // let go: the gun stays where it was
            }
            if (!wristsSampled)
                SampleWrists();
            Vector3 grip = hands == 3 ? (sampledWrist[LEFT] + sampledWrist[RIGHT]) * 0.5f : sampledWrist[hands == 1 ? LEFT : RIGHT];
            // Take a new base whenever the hands on the gun change, so a hand letting go or coming back doesn't jump the gun. The pivot is
            // the hinge as it was then: the live hinge can move as the gun turns, and a lever from it would feed the gun's own turn back
            // into the aim.
            if (!aimBased || hands != aimBaseHands)
            {
                aimPivot = gun.Hinge.position;
                Vector3 baseLever = grip - aimPivot;
                if (baseLever.sqrMagnitude < 1e-4f)
                    return true;
                aimBased = true;
                aimBaseHands = hands;
                aimBaseLever = baseLever;
                aimBaseRotation = movementContext.Rotation;
                aimFiltered = false;
            }
            Vector3 lever = grip - aimPivot;
            if (lever.sqrMagnitude < 1e-4f)
                return true;
            // A rigid turn about the hinge: the handles swing about the vertical by as much as the muzzle does, and rise by as much as
            // it dips (pitch is positive down).
            float yaw = aimBaseRotation.x + Mathf.DeltaAngle(YawOf(aimBaseLever), YawOf(lever));
            float pitch = aimBaseRotation.y + (ElevationOf(lever) - ElevationOf(aimBaseLever));
            movementContext.Rotation = FilterAim(movementContext, new Vector2(yaw, pitch));
            return true;
        }

        //---------------------------------------------------------------------------------------------------------
        // The body (MountedGunPatches: a StationaryPlayerState.ManualAnimatorMoveUpdate prefix). true = moved here, Tarkov's update skipped.
        // Tarkov never turns the body directly on the gun: the aim's angle off the body (UpdateStationaryDeltaAngle) picks a step clip,
        // and the clip's root motion walks the body round. Facing the body along the aim leaves no angle to step through.
        // Tarkov's range check is only in Enter.

        internal static bool MoveBody(StationaryPlayerState state, float deltaTime)
        {
            MovementContext movementContext = state.MovementContext;
            EFT.Player player = movementContext != null ? movementContext._player : null;
            if (!enabled || !bodyFollowsHead || player == null || !player.IsYourPlayer || player != VRGlobals.player
                || state.Stage != StationaryPlayerState.EStationaryStage.Main)
                return false;
            Camera headset = VRGlobals.VRCam;
            Transform head = player.PlayerBones != null && player.PlayerBones.Head != null ? player.PlayerBones.Head.Original : null;
            if (headset == null || head == null)
                return false;
            if (state._denied)
                state.DropStationary();
            movementContext.ApplyRotation(Quaternion.Euler(0f, movementContext.Yaw, 0f));
            // Move the body's own head (standing or seated, as its pose has it) under the headset once it is past the dead zone. Moving
            // the body moves its head by the same amount, so this offset is the body's motion.
            Vector3 offHead = headset.transform.position - head.position;
            offHead.y = 0f;
            float distance = offHead.magnitude;
            Vector3 motion = Vector3.zero;
            if (distance > bodyFollowDeadZone)
            {
                float catchUp = bodyFollowTime > 0f ? 1f - Mathf.Exp(-deltaTime / bodyFollowTime) : 1f;
                motion = offHead * ((distance - bodyFollowDeadZone) / distance * catchUp);
            }
            movementContext.ApplyApproachMotion(motion, deltaTime);
            movementContext.UpdateStationaryDeltaAngle();
            return true;
        }

        private static float YawOf(Vector3 direction) => Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        private static float ElevationOf(Vector3 direction) => Mathf.Asin(Mathf.Clamp(direction.y / direction.magnitude, -1f, 1f)) * Mathf.Rad2Deg;

        // One Euro filter on yaw (wrapped) and pitch. The result is clamped to the mount's limits so it never trails behind a stop.
        private static Vector2 FilterAim(MovementContext movementContext, Vector2 raw)
        {
            if (!aimFiltered)
            {
                aimFiltered = true;
                filteredAim = raw;
                previousRawAim = raw;
                aimSpeed = Vector2.zero;
                filteredFrame = Time.frameCount;
                return raw;
            }
            if (filteredFrame == Time.frameCount)
                return filteredAim;
            filteredFrame = Time.frameCount;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            Vector2 rawStep = new Vector2(Mathf.DeltaAngle(previousRawAim.x, raw.x), raw.y - previousRawAim.y);
            previousRawAim = raw;
            aimSpeed = Vector2.Lerp(aimSpeed, rawStep / dt, FilterAlpha(aimDerivativeCutoff, dt));
            float alpha = FilterAlpha(aimMinCutoff + aimSpeedCutoff * aimSpeed.magnitude, dt);
            filteredAim = movementContext.ClampRotation(new Vector2(filteredAim.x + Mathf.DeltaAngle(filteredAim.x, raw.x) * alpha, Mathf.Lerp(filteredAim.y, raw.y, alpha)));
            return filteredAim;
        }

        private static float FilterAlpha(float cutoff, float dt)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(cutoff, 0.01f));
            return 1f / (1f + tau / dt);
        }

        private static void SampleWrists()
        {
            sampledWrist[LEFT] = Wrist(true);
            sampledWrist[RIGHT] = Wrist(false);
            wristsSampled = true;
        }

        //---------------------------------------------------------------------------------------------------------
        // Fire and aim (InputHandlers.FireHandler / AimHandler)

        internal static bool TriggerPulled => (holding[LEFT] && VRInput.LeftTrigger.axis > 0.5f) || (holding[RIGHT] && VRInput.RightTrigger.axis > 0.5f);

        // Aiming = the eye at the sight and looking down the barrel. The AGS's PAG-17 only draws its view while aiming.
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
            // The barrel direction as Tarkov's own PAG-17 code takes it (ProceduralWeaponAnimation.AdjustOpticsPAG17): the fireport's -up
            Transform fireport = animation != null && animation.HandsContainer != null ? animation.HandsContainer.Fireport : null;
            Transform weapon = firearmController.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon);
            Vector3 barrel = fireport != null ? -fireport.up : weapon != null ? -weapon.up : Vector3.zero;
            bool aiming = firearmController.IsAiming;
            if (debugLog && Time.time >= nextAimLog)
            {
                nextAimLog = Time.time + 1f;
                Plugin.MyLog.LogInfo($"[MountedGun] sight: {(sight != null ? $"'{sight.name}' {Vector3.Distance(headset.transform.position, sight.position):F2} m from the eye" : "none")} "
                    + $"(of {(animation != null && animation.ScopeAimTransforms != null ? animation.ScopeAimTransforms.Count : 0)}, optic {(animation != null && animation.CurrentScope != null && animation.CurrentScope.IsOptic)}), "
                    + $"look {(barrel != Vector3.zero ? Vector3.Angle(headset.transform.forward, barrel).ToString("F0") : "-")} deg off the barrel ({(fireport != null ? "fireport" : "weapon bone")}), "
                    + $"barrel {Vector3.Angle(barrel, weapon != null ? -weapon.up : barrel):F0} deg off the weapon bone's -up, aiming {aiming}");
            }
            if (sight == null || barrel == Vector3.zero)
                return;
            float reach = Vector3.Distance(headset.transform.position, sight.position);
            float angle = Vector3.Angle(headset.transform.forward, barrel);
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

        // On a shot, each holding hand gets a haptic pulse
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
        // The arms (IKPatches.RemoveSprintAnimFromHands, a Player.IkProcess prefix: the last change before the IK solves)

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
        // The view (VRPlayerManager.LateUpdate, after its own pin). camRoot stays pinned to the body as usual, but its moves are taken back
        // out of the offsetter so only the headset moves the view while Tarkov slides the body onto the gun, sits it down and bends it.

        internal static void PinRig()
        {
            if (!Active)
            {
                rigPinned = false;
                wristsSampled = false;
                return;
            }
            EFT.Player player = VRGlobals.player;
            Transform ribcage = player.PlayerBones != null && player.PlayerBones.Ribcage != null ? player.PlayerBones.Ribcage.Original : null;
            if (ribcage == null || VRGlobals.camRoot == null || VRGlobals.vrOffsetter == null || VRGlobals.vrOffsetter.transform.parent == null)
                return;
            // The same anchor the empty-hands / gun pins use (the hands object sits on the ribcage), so getting off doesn't jump the view
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
            SampleWrists();
        }

        //---------------------------------------------------------------------------------------------------------
        // Helpers

        // null = both hands are free for a mounted gun, else the reason they aren't
        private static string HandsBusy(EFT.Player player, bool draggingBody)
        {
            if (player.IsInPronePose)
                return "prone";
            if (player.IsSprintEnabled)
                return "sprinting";
            if (VRGlobals.usingItem || EatingInteractionController.ManualActive)
                return "using an item";
            if (draggingBody)
                return "dragging a body";
            if (HandsInteractionController.HoldingLoot)
                return "holding loot";
            if (WeaponPatches.grenadeEquipped)
                return "a grenade in hand";
            if (PhysicalDoors.HoldsRig(RigHand(true)) || PhysicalDoors.HoldsRig(RigHand(false)))
                return "a hand on a door";
            if (player.HandsIsEmpty || (ChestHang.Hanging && player.HandsController is FirearmController))
                return null;
            return $"{player.HandsController?.GetType().Name ?? "nothing"} in the hands{(player.HandsController is FirearmController ? " (not on the sling)" : "")}";
        }

        // Logs why a grip closing near a mounted gun didn't take it: hands busy, or where each hand was relative to its handle
        private static void LogReach(StationaryWeapon stationaryWeapon, string busy)
        {
            nextReachLog = Time.time + 0.5f;
            Transform weapon = stationaryWeapon.WeaponTransform;
            HandlePoints(stationaryWeapon, out Vector3 leftHandle, out Vector3 rightHandle);
            string Local(Vector3 point) => (Quaternion.Inverse(weapon.rotation) * (point - weapon.position)).ToString("F3");
            Plugin.MyLog.LogInfo($"[MountedGun] grip near {stationaryWeapon.Item.LocalizedShortName()} ({stationaryWeapon.Animation}, '{weapon.name}' under '{(weapon.parent != null ? weapon.parent.name : "-")}')"
                + $"{(busy != null ? $", hands not free: {busy}" : "")}: left wrist {Local(Wrist(true))} ({Vector3.Distance(Wrist(true), leftHandle):F2} m off its zone), "
                + $"right wrist {Local(Wrist(false))} ({Vector3.Distance(Wrist(false), rightHandle):F2} m off), grips {VRInput.LeftGrip.state}/{VRInput.RightGrip.state}, "
                + $"available {stationaryWeapon.IsAvailable(VRGlobals.player.ProfileId)}, locked {stationaryWeapon.Locked}");
        }

        // The nearest mounted gun within reach. The map's mounted guns are found once per map.
        private static StationaryWeapon NearestGun(EFT.Player player)
        {
            GameWorld world = Comfort.Common.Singleton<GameWorld>.Instantiated ? Comfort.Common.Singleton<GameWorld>.Instance : null;
            if (!ReferenceEquals(world, gunsFor) || (guns.Count == 0 && Time.time >= nextGunScan))
            {
                if (!ReferenceEquals(world, gunsFor))
                    loggedGunCount = -1;
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
                    // A map without any is scanned again every 10 s, so the count is only logged when it changes
                    if (debugLog && guns.Count != loggedGunCount)
                        Plugin.MyLog.LogInfo($"[MountedGun] {guns.Count} mounted guns on this map");
                    loggedGunCount = guns.Count;
                }
            }
            Vector3 head = VRGlobals.VRCam != null ? VRGlobals.VRCam.transform.position : player.Position;
            StationaryWeapon nearest = null;
            float nearestDistance = 2.5f * 2.5f;
            foreach (StationaryWeapon stationaryWeapon in guns)
            {
                if (stationaryWeapon == null || stationaryWeapon.WeaponTransform == null || stationaryWeapon.Item == null || !stationaryWeapon.gameObject.activeInHierarchy)
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

        // That controller's wrist (the rig hand), without the world-collision clamp
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

        private static void ShowZones(bool show, Vector3 left, Vector3 right, bool showLeft = true)
        {
            DebugShapes.Sphere(ref leftZoneSphere, "MountedGunLeftZone", show && showLeft, left, grabRadius, new Color(0.2f, 0.5f, 1f, 0.35f));
            DebugShapes.Sphere(ref rightZoneSphere, "MountedGunRightZone", show, right, grabRadius, new Color(1f, 0.5f, 0.2f, 0.35f));
        }
    }
}
