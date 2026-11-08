using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Controls;

namespace TarkovVR.Source.Player.Interactions
{
    // Chest sling + support-only hold (grip-HOLD model): while holding a firearm, the gun stays in
    // hand as long as the dominant grip is held at all (the grip boolean - the action manifest has no
    // capacitive-touch action). Fully releasing the dominant grip for slingDelay:
    //  - while the SUPPORT hand grips (isSupporting): the right hand comes off and the gun rides the
    //    LEFT hand (SupportOnly - rigid attach to the support controller). Left release from there =
    //    both hands off = sling; a dominant grip PRESS at the gun = back to normal two-handed.
    //  - otherwise: the gun goes ON THE SLING - a RIGID canonical across-chest mount in the yaw-only
    //    head frame (deterministic pose, NOT the release orientation; world-space blending in round 1
    //    also SPUN under stick turns). A grip PRESS near the gun takes it back.
    // Game-side the gun never leaves the hands (no inventory op); firing is gated while detached.
    //
    // Held-by-default guard: a freshly equipped gun (raid load, menu equip - hand open) stays welded
    // like vanilla until the FIRST grip press on that controller; only then is it grip-bound.
    public static class ChestHang
    {
        public static bool enabled = true;
        public static Vector3 chestOffset = new Vector3(0.25f, -0.05f, -0.08f); // headset-local, yaw-only: where the GRIP end sits - sternum height, dominant-side, close to the chest; x mirrors left-handed
        public static float slingAcross = 0.5f;         // how far the muzzle points across toward the off side (0 = straight down)
        public static float slingForwardTilt = 0.35f;   // muzzle tips out from the body
        public static float slingYawDeg = 15f;          // whole-gun yaw toward the OFF side (headset feedback: "yaw rotated left" for a right-hander); mirrors left-handed
        public static float slingRollDeg = 55f;          // fine roll about the barrel if the canonical pose sits face-on wrong
        public static float attachTime = 0.10f;         // s fly-in to the sling spot, then the mount is rigid
        public static float rotAttachRate = 10f;        // 1/s rotation tuck-in
        public static float slingDelay = 0.1f;          // s the grip must stay fully released before letting go (debounce; 0.25 read as a delay)
        public static float regrabRadius = 0.13f;       // m RightHand rig transform to the regrab zone (0.10, the left hand's foregrip radius, read a little short)
        public static float regrabForwardOffset = 0.07f;// m the zone sits toward the MUZZLE from the R-hand marker (marker alone still read "a bit far back")
        // The other spots (BodyWeapons, SpotPose): where the gun's Weapon_root sits, from the eyes looking level in the body's frame,
        // and which way its muzzle and its grip point (x mirrors left-handed). The second primary over the dominant shoulder,
        // hanging down the back, the grip out behind you; the pistol on the dominant hip, muzzle down, grip to the rear.
        public static Vector3 backOffset = new Vector3(0.17f, -0.17f, -0.17f);
        public static Vector3 backMuzzle = new Vector3(-0.3f, -1f, -0.2f);
        public static Vector3 backGrip = new Vector3(0.3f, 0f, -1f);
        public static Vector3 hipOffset = new Vector3(0.22f, -0.68f, 0.02f);
        public static Vector3 hipMuzzle = new Vector3(0f, -1f, 0.15f);
        public static Vector3 hipGrip = new Vector3(0.2f, 0f, -1f);
        public static bool hangHaptics = true;
        public static bool debugLog = false;            // 1/sec chain dump while detached + save/restore lines (for the prone-corrupts-regrab hunt)
        private static float nextDebugLog;
        public static bool gripSound = true;            // every grab (foregrip / grip retake / off the sling / a gun off the body)
        public static bool gripSoundHandOn = true;      // the gun's own hand-on-the-grip sound; false = the old cloth + a hint of gun ("doesn't sound good and gets annoying")
        public static float gripClothVolume = 0.85f;    // old mix: the gear (rig/backpack) pickup foley = the cloth layer
        public static float gripGunVolume = 0.18f;      // old mix: the weapon's own pickup clip, quiet, underneath
        private static EFT.Player.FirearmController handOnSoundFor;
        private static BaseSoundPlayer handOnSoundPlayer;
        // The gun from one hand straight into the other (right-handed mode): the off hand at the gun hand's grip + a grip press takes it
        // (not a pistol: its support hand cups the gun hand right there), and a pistol held two-handed goes over to the support hand
        // when the gun hand lets go (instead of the support-only hold the long guns get)
        public static bool handSwap = true;
        public static bool pistolSwapOnRelease = true;
        public static float handSwapRadius = 0.13f;     // m off-hand palm / knuckles / fingertips to the gun hand's grip (regrabRadius's reach)

        public static bool Hanging { get; private set; }
        public static bool SupportOnly { get; private set; }   // gun held by the support hand alone
        public static bool Detached => Hanging || SupportOnly; // fire gate
        public static bool HandInRegrabZone { get; private set; } // dominant palm within regrab reach of the detached gun (loot pointer yields to the regrab)
        public static bool OffHandAtGunGrip { get; private set; } // the off hand where a grip press takes the gun into it (the off-hand loot pointer and body grab yield)
        private static bool prevOffGrip;
        private static bool prevOffGripWasLeft = true;  // which controller that was: the off hand changes sides with the gun
        private static bool swapZoneIn;

        private static bool gripSeen;                   // welded like vanilla until the first grip press on this controller
        private static int ignoredPressFrame = -1;      // a grip press BodyWeapons took (a gun off the body), not a regrab
        private static Item gripSeenFor;                // taken off the body with the grip closed: grip-bound from the moment it's in hands
        private static bool prevGrip;
        private static bool prevSupporting;             // for the foregrip-grab foley edge
        private static float releaseTimer;
        private static int lastTickFrame = -10;
        private static object lastHandsController;
        private static object lastPlayer;
        private static float hangOnArrivalUntil = -1f;  // the gun Tarkov hands you (raid start, walking into the range) goes to its spot
        private static EFT.Player.FirearmController detachedGun;   // the gun slung / support-held: only its holder is ever stamped
        // Holder local pose from BEFORE the first detach; the anchor only re-stamps rotation at equip,
        // so it MUST be restored when the gun returns to the hand - and preserved across a
        // SupportOnly -> sling transition (holderSaved guards the save).
        private static bool holderSaved;
        private static Vector3 savedHolderLocalPos;
        private static Quaternion savedHolderLocalRot;
        // sling pose in the yaw-only head frame (rigid under stick/physical turns)
        private static Vector3 slingLocalPos;
        private static Quaternion slingLocalRot = Quaternion.identity;
        private static Vector3 posVel;
        // support-only hold: gun pose rigid in the support-controller frame
        private static Vector3 supportRelPos;
        private static Quaternion supportRelRot = Quaternion.identity;
        private static Vector3 supportGripLever;    // grab point -> WeaponRoot, in GUN space (the weight spring pivots about the grab)
        private static Quaternion supportSmoothedRot = Quaternion.identity;   // the spring's filtered gun rotation

        // Called from HandsInteractionController.Update after ProcessInputStates (so this frame's
        // holster/radial gestures are already reflected in the ownership flag).
        public static void UpdateInput(bool grip, bool gripOwnedElsewhere, bool supporting)
        {
            // The off hand's grip press, from the same controller as last frame only (a hand swap changes which one that is)
            bool offGrip = Handedness.OffHandGrip.state;
            bool offIsLeft = !Handedness.GunHandIsLeft;
            bool offGripPressed = offGrip && !prevOffGrip && offIsLeft == prevOffGripWasLeft;
            prevOffGrip = offGrip;
            prevOffGripWasLeft = offIsLeft;
            OffHandAtGunGrip = false;

            if (!enabled || !(VRGlobals.vrPlayer is RaidVRPlayerManager || VRGlobals.vrPlayer is HideoutVRPlayerManager))
            {
                gripSeen = false;
                releaseTimer = 0f;
                Reattach(restore: false);
                Handedness.SetGunInLeftHand(false);
                return;
            }
            // The LEFT controller's grip, for taking the slung gun into the left hand (right-handed mode: that controller doesn't change)
            bool leftGrip = VRInput.LeftGrip.state;
            bool leftGripPressed = leftGrip && !prevLeftGrip;
            prevLeftGrip = leftGrip;

            var player = VRGlobals.player;
            if (!ReferenceEquals(player, lastPlayer))
            {
                lastPlayer = player;
                if (player != null)
                    HangOnArrival();   // a raid start: its gun is already in your hands
            }
            if (hangOnArrivalUntil > 0f && Time.time > hangOnArrivalUntil)
                hangOnArrivalUntil = -1f;
            var fc = VRGlobals.firearmController;
            bool holdingFirearm = player != null && fc != null && fc.Item != null
                && !WeaponPatches.grenadeEquipped
                && ReferenceEquals(player.HandsController, fc); // guards a stale firearmController ref

            // Controller switched (holster swap, weapon change): the equip path re-stamps the holder
            // and owns the arms - clear our state, restore nothing. New gun starts welded (gripSeen off).
            object hc = player != null ? player.HandsController : null;
            if (!ReferenceEquals(hc, lastHandsController))
            {
                lastHandsController = hc;
                gripSeen = false;
                releaseTimer = 0f;
                prevGrip = grip;
                Reattach(restore: false);
                // A new gun (or none) starts in the right hand
                Handedness.SetGunInLeftHand(false);
            }

            if (!holdingFirearm)
            {
                releaseTimer = 0f;
                Reattach(restore: false);
                Handedness.SetGunInLeftHand(false);
                return;
            }
            // Taken off the body (BodyWeapons): the grip that took it holds it, so opening the hand lets it go to its spot
            if (gripSeenFor != null && fc.Item == gripSeenFor)
            {
                gripSeen = true;
                gripSeenFor = null;
            }
            bool pressed = grip && !prevGrip && Time.frameCount != ignoredPressFrame;

            // Update() skips frames (menu/sprint) - after a gap restart the release debounce so the
            // gun can't let go the instant a menu closes on an open hand.
            bool resumed = Time.frameCount - lastTickFrame > 1;
            lastTickFrame = Time.frameCount;
            if (resumed)
            {
                prevGrip = grip;
                prevSupporting = supporting;
                releaseTimer = 0f;
                return;
            }

            // Foregrip-grab foley on the isSupporting EDGE seen HERE, not inside UpdateLeftHand's
            // engage branches - which branch sets it depends on the snap/hold-toggle settings, and
            // hooking one branch missed the user's control setup (headset-confirmed silent).
            if (supporting && !prevSupporting && !Detached)
                PlayGripSound();
            prevSupporting = supporting;

            // The off hand taking the gun by its grip, out of the gun hand (also with the sling off: the gun stays in a hand)
            if (!Detached && TryGrabFromGunHand(fc, supporting, offGripPressed))
                return;

            // "Chest Sling" off in the VR menu: the gun stays in the hand whatever the grip does. Switched
            // off while slung / support-held -> back into the hand (same gun: the switch check ran above).
            if (!VRSettings.GetChestSlingOn())
            {
                gripSeen = false;
                releaseTimer = 0f;
                if (Detached)
                    Reattach(restore: true);
                prevGrip = grip;
                return;
            }

            // The gun Tarkov put in your hands at a raid start or the range: straight to its spot (headset: "the gun doesn't start
            // in your hand but on the sling"). Its draw skipped, and the hang only once it's idle: the holder is stamped for it by then.
            if (hangOnArrivalUntil > 0f && !Detached && !grip && !supporting && BodyWeapons.IsActive)
            {
                if (!(fc.CurrentOperation is EFT.Player.FirearmController.Idling))
                    WeaponPatches.SkipDrawOrPutAway(fc);
                else
                {
                    BeginHang(fc, snap: true);
                    if (Hanging)
                        hangOnArrivalUntil = -1f;
                }
            }

            if (Detached)
            {
                KeepGunHandFree();
                DebugChainDump();
            }

            if (SupportOnly)
            {
                // The gun in the left hand: the right hand is the one holding it, the left takes it back (at the grip, as off the sling)
                bool near = Handedness.GunInLeftHand ? LeftHandAtGrip(fc) : HandNearGun(fc);
                HandInRegrabZone = near;
                RegrabZonePulse(near);
                if (!supporting)
                {
                    // support hand let go too - both hands off -> straight to the sling (no flash of
                    // the marker anchor in between)
                    SupportOnly = false;
                    BeginHang(fc);
                }
                else if (pressed && near && !VRReloadController.HandleInGunHand && !VRReloadController.BoltInGunHand)
                {
                    // dominant hand takes the grip back -> normal (two-handed) hold. Reattach clears
                    // SupportOnly itself - clearing it HERE first made wasDetached/fromSupport read
                    // false inside = no retake sound/haptic (headset-confirmed silent).
                    Reattach(restore: true);
                    if (Handedness.GunInLeftHand)
                        VRPlayerManager.CacheGunInHand();   // the holder back in its clean pose, as off the sling
                    gripSeen = true;
                }
                prevGrip = grip;
                return;
            }

            if (Hanging)
            {
                bool near = HandNearGun(fc);
                HandInRegrabZone = near;
                RegrabZonePulse(near);
                // taking it back is deliberate: a fresh grip PRESS with the palm at the gun
                if (pressed && near)
                {
                    Reattach(restore: true);
                    gripSeen = true;
                }
                // ...or with the LEFT hand at its grip: then it's held left-handed (Handedness.GunInLeftHand: not mirrored, the hold, arms
                // and fingers mirrored onto the left). Right-handed mode only; the left hand free (nothing in it, not on a reload part).
                else if (!Handedness.LeftHanded && leftGripPressed && LeftHandAtGrip(fc) && !VRReloadController.OwnsOffHand
                    && (VRGlobals.handsInteractionController == null || VRGlobals.handsInteractionController.heldItem == null))
                {
                    Handedness.SetGunInLeftHand(true);
                    Reattach(restore: true);
                    // The holder is back in its clean pose: the left-handed hold is built from it once (not from the live, nudged one)
                    VRPlayerManager.CacheGunInHand();
                    gripSeen = true;
                    // From here the gun hand's grip is the left one, held now: no fresh press next frame
                    prevGrip = true;
                    return;
                }
                prevGrip = grip;
                return;
            }

            GiveGunArmBack();

            if (grip)
            {
                gripSeen = true;   // gun is now grip-bound: open the hand and it lets go
                releaseTimer = 0f;
            }
            else if (gripSeen && !gripOwnedElsewhere)
            {
                releaseTimer += Time.deltaTime;
                if (releaseTimer >= slingDelay)
                {
                    releaseTimer = 0f;
                    // A pistol two-handed: the support hand becomes its gun hand (its support grip cups the gun hand, nothing to hold up)
                    if (supporting && PistolSwapsOnRelease())
                    {
                        SwapHands();
                        return;
                    }
                    // the support hand still on it - hand the gun to that hand. With the gun in the left hand that's the right hand:
                    // GunInLeftHand stays on (the rig's LeftHand, the support pose and isSupporting all follow the right controller
                    // then, so the hold below is the same), RealLeftHandedPatches.SupportHeld draws the arms
                    if (supporting)
                        BeginSupportHold(fc);
                    else
                        BeginHang(fc);
                }
            }
            else
            {
                releaseTimer = 0f;
            }
            prevGrip = grip;
        }

        // ---- state transitions ----

        // Armed for a while: the range's gun comes after its bundles load
        internal static void HangOnArrival(float seconds = 15f)
        {
            hangOnArrivalUntil = Time.time + seconds;
        }

        // snap: placed at its spot at once, no fly-in from the hand and no haptic (a gun that starts there)
        private static void BeginHang(EFT.Player.FirearmController fc, bool snap = false)
        {
            Transform wr = fc.WeaponRoot;
            if (wr == null || VRGlobals.weaponHolder == null || VRGlobals.ikManager == null ||
                VRGlobals.vrPlayer == null || !HeadPose(out _, out _))
                return;

            // FIRST: strip any live collision enact off the holder. Releasing the grip while the gun
            // is pressed against something (prone transitions - the gun collides with the ground)
            // otherwise SAVES the enacted pose, and every restore then re-applies the collision
            // offset, compounding per sling cycle (headset log: holder rotation drifted 275->257->242
            // across prone sling/unsling cycles). Reset also clears the rig-motion tracking.
            WeaponCollision.Reset();

            VRGlobals.vrPlayer.ForceUnlockHand();      // two-handing can't survive letting go
            SaveHolderOnce();

            // fly-in starts from the release pose, captured in the body's frame (where the spots are)
            Quaternion yawRot = BodyYaw(out Vector3 headPos);
            slingLocalPos = Quaternion.Inverse(yawRot) * (wr.position - headPos);
            slingLocalRot = Quaternion.Inverse(yawRot) * wr.rotation;
            posVel = Vector3.zero;
            if (snap && HandsGunSpotPose(fc, out Vector3 spotPos, out Quaternion spotRot))
            {
                slingLocalPos = Quaternion.Inverse(yawRot) * (spotPos - headPos);
                slingLocalRot = Quaternion.Inverse(yawRot) * spotRot;
            }

            // Out of the left hand too: on the sling the gun is nobody's. Before DetachCommon frees the right arm, so that starts
            // from the arms as they were and not the left-hand hold's own right-arm target (RealLeftHandedPatches.ReleaseArms)
            Handedness.SetGunInLeftHand(false);
            detachedGun = fc;
            DetachCommon();
            Hanging = true;
            if (hangHaptics && !snap)
                Pulse(0.6f);
        }

        // The left hand's palm, knuckles or fingertips at the slung gun's grip (the right hand's regrab point and reach)
        private static bool LeftHandAtGrip(EFT.Player.FirearmController fc)
        {
            if (fc.WeaponRoot == null || VRGlobals.handsInteractionController == null)
                return false;
            Vector3 grip = RegrabPoint(fc);
            VRGlobals.handsInteractionController.GetHandTouchPoints(false, leftTouchPoints);
            foreach (Vector3 point in leftTouchPoints)
            {
                if ((point - grip).sqrMagnitude <= regrabRadius * regrabRadius)
                    return true;
            }
            return false;
        }
        private static readonly System.Collections.Generic.List<Vector3> leftTouchPoints = new System.Collections.Generic.List<Vector3>();
        private static bool prevLeftGrip;

        // ---- the gun from one hand into the other ----

        // The off hand at the gun hand's grip with a grip press: the gun goes into the off hand. Not a pistol (its support grip is right
        // there: PistolSwapsOnRelease), not from the foregrip or near it (short guns: the foregrip's grab wins), not while the off hand
        // is on a reload part or in its zone (VRReloadController.OwnsOffHand, a frame old: the magazine zone wins), holds loot or the
        // scope dial. True = swapped.
        private static bool TryGrabFromGunHand(EFT.Player.FirearmController fc, bool supporting, bool offGripPressed)
        {
            var hands = VRGlobals.handsInteractionController;
            float swapScore = handSwap && !Handedness.LeftHanded && !supporting && !VRGlobals.vrPlayer.isWeapPistol
                && !VRReloadController.OwnsOffHand && hands != null && hands.heldItem == null && hands.scopeTransform == null
                && !OffHandAtForegrip() ? OffHandGripScore(fc) : float.MaxValue;
            bool atGrip = swapScore < 1f;
            // Nearest wins: a magazine that loads through the grip (MP7, MP9, Uzi, Vector, PP-91) sits inside this reach, and a short
            // one is all grip (headset, MP7 20-round: "if I try and grab this smaller mag, it just causes me to move the gun to my
            // other hand"). The hand nearer the magazine than the grip, each over its own reach, leaves the press to the magazine
            // (VRReloadController.Tick, later this frame, takes it with that reach).
            bool magazineNearer = atGrip && VRReloadController.OffHandMagazineScore(offTouchPoints) < swapScore;
            if (magazineNearer)
                atGrip = false;
            OffHandAtGunGrip = atGrip;
            // A light blip on the off hand coming into reach of the grip (as the regrab zone's): only where a press would swap
            if (atGrip && !swapZoneIn && hangHaptics)
                HapticMixer.PlayPulse(Handedness.OffHandSource, 0.25f, 0.07f, 95f);
            swapZoneIn = atGrip;
            if (magazineNearer && offGripPressed)
                GripPressLeftToMagazineFrame = Time.frameCount;
            if (!atGrip || !offGripPressed)
                return false;
            SwapHands();
            return true;
        }

        // A pistol held two-handed, the gun hand letting go: the support hand takes it as its gun hand
        private static bool PistolSwapsOnRelease()
        {
            return pistolSwapOnRelease && !Handedness.LeftHanded && VRGlobals.vrPlayer.isWeapPistol;
        }

        // The frame a grip press in the swap's reach went to the seated magazine instead (nearer it: TryGrabFromGunHand)
        internal static int GripPressLeftToMagazineFrame { get; private set; } = -1;

        // The off hand's palm, knuckles or fingertips to the gun hand's grip, over handSwapRadius (under 1 = in reach): the regrab
        // point (the R-hand marker), mirrored across the gun's centre plane while the gun is in the left hand (where that hand holds
        // it). Leaves those points in offTouchPoints.
        private static float OffHandGripScore(EFT.Player.FirearmController fc)
        {
            offTouchPoints.Clear();
            if (fc.WeaponRoot == null || VRGlobals.handsInteractionController == null)
                return float.MaxValue;
            Vector3 grip = RegrabPoint(fc);
            if (Handedness.GunInLeftHand && RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                grip = RealLeftHandedPatches.ReflectPoint(grip, planePoint, planeNormal);
            // The off hand is drawn by the right arm while the gun is in the left hand
            VRGlobals.handsInteractionController.GetHandTouchPoints(Handedness.GunInLeftHand, offTouchPoints);
            float nearest = float.MaxValue;
            foreach (Vector3 point in offTouchPoints)
                nearest = Mathf.Min(nearest, (point - grip).magnitude / handSwapRadius);
            return nearest;
        }
        private static readonly System.Collections.Generic.List<Vector3> offTouchPoints = new System.Collections.Generic.List<Vector3>();

        // UpdateLeftHand's foregrip grab range (the rig's LeftHand follows the off controller either way)
        private static bool OffHandAtForegrip()
        {
            var offHand = VRGlobals.vrPlayer.LeftHand;
            return offHand != null && VRPlayerManager.leftHandGunIK != null
                && Vector3.Distance(offHand.transform.position, VRPlayerManager.SupportGripPoint()) < 0.1f;
        }

        // The gun goes into the other hand, held as it would be taken off the sling by that hand
        private static void SwapHands()
        {
            // The holder in its clean pose (the left hand's hold is read off it once: VRPlayerManager.CacheGunInHand; a collision nudge
            // left on it fed back, the round-1 "spazzes out") and the collision's rig-motion tracking re-primed (the gun jumps hands)
            WeaponCollision.Reset();
            bool toLeft = !Handedness.GunInLeftHand;
            // A pistol's support hand is its gun hand from here
            VRGlobals.vrPlayer.ForceUnlockHand();
            Handedness.SetGunInLeftHand(toLeft);
            if (toLeft)
            {
                VRPlayerManager.CacheGunInHand();
            }
            else if (VRGlobals.ikManager != null && VRGlobals.ikManager.leftArmIk != null && VRGlobals.vrPlayer.LeftHand != null)
            {
                // The left hand off the gun: a free hand on its own controller, as on any equip (GunInteractionController.OnEnable)
                VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
                VRGlobals.ikManager.leftArmIk.enabled = true;
            }
            // Bound to the new gun hand's grip when it's closed (it is for a grab; a pistol's support hand on the toggle setting holds
            // with it open), else held like a freshly equipped gun until its first press
            gripSeen = Handedness.GunHandGrip.state;
            prevGrip = gripSeen;
            releaseTimer = 0f;
            prevOffGrip = Handedness.OffHandGrip.state;
            prevOffGripWasLeft = !Handedness.GunHandIsLeft;
            // The old gun hand is at the grip: no blip until it leaves and comes back
            swapZoneIn = true;
            OffHandAtGunGrip = false;
            if (hangHaptics)
                Pulse(0.45f, throughGun: true);
            PlayGripSound();
        }

        private static void BeginSupportHold(EFT.Player.FirearmController fc)
        {
            Transform wr = fc.WeaponRoot;
            var lhObj = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.LeftHand : null;
            if (wr == null || lhObj == null || VRGlobals.weaponHolder == null || VRGlobals.ikManager == null)
                return;

            WeaponCollision.Reset();   // strip any live collision enact BEFORE saving (see BeginHang)
            SaveHolderOnce();
            // Rigid attach to the support controller at the current relative pose (the left hand is
            // already gripping the gun, so this is exactly "the left hand keeps holding it").
            // ROTATION comes from the raw pose action, NOT LeftHand.transform: while isSupporting,
            // UpdateLeftHand only writes localPosition - the transform's rotation is frozen at
            // support-grab time (that was the "left hand only has positional control" bug).
            Transform lh = lhObj.transform;
            Quaternion ctrl = SupportControllerRot(lh);
            supportRelPos = Quaternion.Inverse(ctrl) * (wr.position - lh.position);
            supportRelRot = Quaternion.Inverse(ctrl) * wr.rotation;
            supportGripLever = Quaternion.Inverse(supportRelRot) * supportRelPos;
            supportSmoothedRot = wr.rotation;

            detachedGun = fc;
            DetachCommon();
            SupportOnly = true;
            // The gun hand lets go, and the support hand now carries the whole gun: it feels that through the gun
            if (hangHaptics)
                Pulse(0.35f, throughGun: true);
        }

        // Shared detach work: free the dominant arm (the HandsIsEmpty pattern). NOTE the collision
        // Reset happens EARLIER, in the Begin* methods BEFORE SaveHolderOnce - the strip must precede
        // the save (the prone-sling corruption).
        private static void DetachCommon()
        {
            KeepGunHandFree();
            gripSeen = false;
            // The hand is AT the gun the instant it lets go - start the zone flag "in" so the
            // in-reach blip only fires after leaving and coming back.
            regrabZoneIn = true;
        }

        // EFT solves the gun-hand arm onto _markers[1] (the gun's weapon_R_IK_marker) itself every LateUpdate (Player.IkProcess/IkApply),
        // whatever our IK does: the freed hand only followed the controller when our IK happened to solve after it, and Unity doesn't
        // order the two (headset: now and then the hand stuck back on the slung gun until a regrab). So while detached _markers[1] is our
        // IK's own target, as on empty hands and the free left hand; every frame, since EFT gathers a fresh marker on a weapon change.
        private static Transform gunHandMarker;     // the gun's own, given back when the hand takes the gun again
        private static Transform freedHandMarker;   // what _markers[1] was set to

        internal static void KeepGunHandFree()
        {
            // A press check's marker on the gun hand: the gun's own back first, or it's saved below as the gun's and given back on the
            // next grab (headset: the hand flew off to it when the pistol was taken from its slot)
            VRReloadController.CancelPressCheck();
            var arm = VRGlobals.ikManager != null ? VRGlobals.ikManager.rightArmIk : null;
            var rightHand = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.RightHand : null;
            var player = VRGlobals.player;
            if (arm == null || rightHand == null || player == null)
                return;
            // The gun in the left hand, held by the right hand alone: that arm is on the foregrip, RealLeftHandedPatches has both
            if (Handedness.GunInLeftHand)
                return;
            // The gun hand on the charging handle (VRReloadController's latch) keeps its own target
            if (arm.solver.target == null)
                arm.solver.target = rightHand.transform;
            if (!arm.enabled)
                arm.enabled = true;
            Transform aim = arm.solver.target;
            if (player._markers.Length < 2 || player._markers[1] == aim)
                return;
            if (player._markers[1] != freedHandMarker)
                gunHandMarker = player._markers[1];
            player._markers[1] = aim;
            freedHandMarker = aim;
        }

        private static void GiveGunHandMarkerBack()
        {
            var player = VRGlobals.player;
            // (never a press check's marker: that's no part of the gun)
            if (VRReloadController.IsPressCheckMarker(gunHandMarker))
            {
                var gun = VRGlobals.firearmController;
                gunHandMarker = gun != null && gun.HandsHierarchy != null ? gun.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon_R_IK_marker) : null;
            }
            // Not when something else set it since: a new gun gathered its own, empty hands point it at the hand
            if (player != null && player._markers.Length > 1 && freedHandMarker != null && player._markers[1] == freedHandMarker
                && gunHandMarker != null && !player.HandsIsEmpty)
                player._markers[1] = gunHandMarker;
            gunHandMarker = null;
            freedHandMarker = null;
        }

        // The gun's own right marker, also while detached (IKPatches zeroes the bone above it through a sprint)
        internal static Transform GunRightMarker(EFT.Player player)
        {
            if (player._markers.Length < 2)
                return null;
            return freedHandMarker != null && gunHandMarker != null && player._markers[1] == freedHandMarker ? gunHandMarker : player._markers[1];
        }

        // A light blip when the dominant hand comes into regrab reach of the detached gun (mirrors the
        // support hand's snap-zone haptic), so you can find the grip without looking.
        private static bool regrabZoneIn;
        private static void RegrabZonePulse(bool near)
        {
            if (near && !regrabZoneIn && hangHaptics)
                Pulse(0.3f);
            regrabZoneIn = near;
        }

        // Back to the dominant hand (restore = true), or just clear state because something else owns
        // the gun now (controller switch, raid end - restore NOTHING, the equip path re-stamps).
        private static void Reattach(bool restore)
        {
            bool wasDetached = Detached;
            Hanging = false;
            SupportOnly = false;
            detachedGun = null;
            HandInRegrabZone = false;
            GiveGunHandMarkerBack();
            if (!restore)
            {
                holderSaved = false;
                return;
            }
            if (holderSaved && VRGlobals.weaponHolder != null)
            {
                Transform holder = VRGlobals.weaponHolder.transform;
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[ChestHang] RESTORE pre={holder.localPosition:F3}/{holder.localRotation.eulerAngles:F1} -> saved={savedHolderLocalPos:F3}/{savedHolderLocalRot.eulerAngles:F1} wrL={(VRGlobals.firearmController?.WeaponRoot != null ? VRGlobals.firearmController.WeaponRoot.localPosition : Vector3.zero):F3}");
                holder.localPosition = savedHolderLocalPos;
                holder.localRotation = savedHolderLocalRot;
                holderSaved = false;
            }
            if (VRGlobals.ikManager != null && VRGlobals.ikManager.rightArmIk != null)
            {
                // back to gun-held: the gun drives the right side directly (same as the equip path)
                VRGlobals.ikManager.rightArmIk.solver.target = null;
                VRGlobals.ikManager.rightArmIk.enabled = false;
            }
            if (hangHaptics && wasDetached)
                Pulse(0.45f, throughGun: true);
            // Same cloth-grab foley for every grab - off the sling included ("it just sounds better"
            // than the full weapon pickup clatter, headset-picked).
            if (wasDetached)
                PlayGripSound();
        }

        // Hands going onto the held gun: the gun's own hand-on-grip sound (gripSoundHandOn), else the old
        // "cloth grab with a hint of gun" = the generic gear (rig/backpack) pickup foley with the weapon's
        // own pickup clip quiet under it. Fired for the foregrip grab (the isSupporting edge), the grip
        // retake, the hand swap and a gun drawn off the body (BodyWeapons).
        public static void PlayGripSound()
        {
            var fc = VRGlobals.firearmController;
            if (!gripSound || fc == null || fc.WeaponRoot == null)
                return;
            if (gripSoundHandOn)
            {
                PlayHandOnSound(fc);
                return;
            }
            var gui = Singleton<GUISounds>.Instance;
            var ba = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (gui == null || ba == null)
                return;
            Vector3 pos = fc.WeaponRoot.position;

            // cloth layer with a fallback chain, in case a sound-group name doesn't exist in the bundle
            AudioClip cloth = gui.GetItemClip("gear_generic", EInventorySoundType.pickup);
            string clothSrc = "gear_generic";
            if (cloth == null)
            {
                cloth = gui.GetItemClip("generic", EInventorySoundType.pickup);
                clothSrc = "generic";
            }
            var item = fc.Item;
            AudioClip gun = item != null ? gui.GetItemClip(item.ItemSound, EInventorySoundType.pickup) : null;

            if (cloth != null)
                ba.TryPlayAtPoint(out _, pos, cloth, BetterAudio.AudioSourceGroupType.Character, 25, gripClothVolume);
            if (gun != null)
                ba.TryPlayAtPoint(out _, pos, gun, BetterAudio.AudioSourceGroupType.Character, 25, cloth != null ? gripGunVolume : gripClothVolume);
        }

        // The sound Tarkov's reload plays as the support hand goes back onto the gun, from the gun's own sound player (WeaponRecon,
        // M4A1: HandOn = hand_grip4, HandOff = hand_grip3). A gun without either stays silent. Not through VRReloadController's
        // PlayFelt: no audio haptics (the grab already pulses) and nothing sent over FIKA, as before.
        private static void PlayHandOnSound(EFT.Player.FirearmController fc)
        {
            if (handOnSoundFor != fc)
            {
                handOnSoundFor = fc;
                handOnSoundPlayer = fc._controllerObject != null ? fc._controllerObject.GetComponentInChildren<BaseSoundPlayer>(true) : null;
            }
            if (handOnSoundPlayer == null || handOnSoundPlayer.AdditionalSounds == null)
                return;
            foreach (string soundName in new[] { "HandOn", "HandOff" })
            {
                string eventName = "Snd" + soundName;
                if (handOnSoundPlayer.AdditionalSounds.Exists(sound => sound != null && (sound.EventName == soundName || sound.EventName == eventName)))
                {
                    handOnSoundPlayer.SoundEventHandler(soundName);
                    return;
                }
            }
        }

        private static void SaveHolderOnce()
        {
            // A press check turns the holder: its own rotation back before it's saved (else every restore would keep the turn)
            VRReloadController.CancelPressCheck();
            if (holderSaved || VRGlobals.weaponHolder == null)
                return;
            Transform holder = VRGlobals.weaponHolder.transform;
            savedHolderLocalPos = holder.localPosition;
            savedHolderLocalRot = holder.localRotation;
            holderSaved = true;
            if (debugLog)
                Plugin.MyLog.LogInfo($"[ChestHang] SAVE holderL={savedHolderLocalPos:F3}/{savedHolderLocalRot.eulerAngles:F1}");
        }

        // 1/sec while detached: every transform in the hand->gun chain whose corruption would change
        // the hand<->gun relationship after regrab. Compare a prone/stand cycle's lines to find the
        // one that moves.
        private static void DebugChainDump()
        {
            if (!debugLog || Time.time < nextDebugLog)
                return;
            nextDebugLog = Time.time + 1f;
            var fc = VRGlobals.firearmController;
            Transform holder = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform : null;
            Transform wr = fc != null ? fc.WeaponRoot : null;
            if (holder == null || wr == null)
                return;
            Transform positioner = holder.parent;
            Plugin.MyLog.LogInfo(
                $"[ChestHang] holderL={holder.localPosition:F3}/{holder.localRotation.eulerAngles:F1}" +
                $" wrL={wr.localPosition:F3}/{wr.localRotation.eulerAngles:F1} wrParent='{(wr.parent != null ? wr.parent.name : "null")}'" +
                $" posL={(positioner != null ? positioner.localPosition : Vector3.zero):F3} saved={savedHolderLocalPos:F3}/{savedHolderLocalRot.eulerAngles:F1}");
        }

        // Mirrors the left hand's PROVEN foregrip check (LeftHand rig transform vs the weapon's
        // weapon_L_hand_marker, Transforms[10]): the anchor is the weapon's own RIGHT-GRIP marker
        // (ECharacterWeaponBones.weapon_R_hand_marker = Transforms[8]) - the exact transform the
        // rendered right hand sits on when gripping - measured against the RightHand rig transform.
        // Earlier attempts anchored on WeaponRoot, which sits at the receiver BEHIND the pistol grip
        // on most guns ("zone too far back", headset-confirmed).
        // Anywhere on the hanging gun takes it back, as on the body guns (BodyWeapons.ReachScore): under 1 = in reach
        private static bool HandNearGun(EFT.Player.FirearmController fc)
        {
            return RegrabScore(fc) < 1f;
        }

        internal static float RegrabScore()
        {
            return VRGlobals.firearmController != null ? RegrabScore(VRGlobals.firearmController) : float.MaxValue;
        }

        private static float RegrabScore(EFT.Player.FirearmController fc)
        {
            if (fc.WeaponRoot == null)
                return float.MaxValue;
            return BodyWeapons.ReachScore(fc.WeaponRoot, fc.WeaponRoot, RegrabPoint(fc), regrabRadius, alongBarrel: true);
        }

        // The grip, a little toward the muzzle (falls back to WeaponRoot)
        private static Vector3 RegrabPoint(EFT.Player.FirearmController fc)
        {
            Transform anchor = WeaponPatches.RightGripMarker(fc);
            if (anchor == null)
                anchor = fc.WeaponRoot;
            return anchor.position - fc.WeaponRoot.up * regrabForwardOffset;
        }

        // A gun in hand and not hanging is the gun's arm (Tarkov IKs it onto the grip). A gun drawn while the last one still hung
        // got its arm freed by its own GunInteractionController.OnEnable (it saw Detached), and nothing gave it back, so its hand
        // followed the controller instead of the grip until one sling cycle (headset: "only when I first take the gun out; after
        // letting go and picking it back up it's correct"). Checked every frame, so no order of Tarkov's switch can leave it.
        // The gun hand on the charging handle has its own target (VRReloadController's latch), not RightHand.
        private static void GiveGunArmBack()
        {
            var arm = VRGlobals.ikManager != null ? VRGlobals.ikManager.rightArmIk : null;
            var rightHand = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.RightHand : null;
            if (arm == null || rightHand == null)
                return;
            if (arm.enabled && arm.solver.target == rightHand.transform)
            {
                arm.solver.target = null;
                arm.enabled = false;
                if (debugLog)
                    Plugin.MyLog.LogInfo("[ChestHang] gave the gun arm back to the gun in hand");
            }
            // And EFT's own right marker (KeepGunHandFree's), which it solves that arm onto: the gun's grip marker
            var player = VRGlobals.player;
            var fc = VRGlobals.firearmController;
            if (player != null && player._markers.Length > 1 && player._markers[1] == rightHand.transform && fc != null && fc.HandsHierarchy != null)
            {
                Transform gunMarker = fc.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon_R_IK_marker);
                if (gunMarker != null)
                    player._markers[1] = gunMarker;
                gunHandMarker = null;
                freedHandMarker = null;
            }
        }

        // BodyWeapons took this frame's grip press for a gun off the body: not a regrab of the one hanging
        internal static void IgnoreGripPress()
        {
            ignoredPressFrame = Time.frameCount;
        }

        internal static void TakenFromBody(Item item)
        {
            gripSeenFor = item;
            hangOnArrivalUntil = -1f;   // a gun you took yourself
        }

        // Where a gun lives on the body when it isn't in your hand, as its Weapon_root's pose: BodyWeapons draws the others there,
        // and the gun in hand goes there when you let go. The first primary on the chest sling, the second over the dominant
        // shoulder, the pistol on the dominant hip (not Tarkov's thigh holster: "not in good spots"). In the body's frame (BodyYaw),
        // not the head's ("they turn with me, which makes them harder to grab"). The back and hip guns are turned by where their
        // muzzle and their grip point (gripLocal = the gun's R-hand marker in its Weapon_root frame), so any gun sits the same way.
        internal static bool SpotPose(EquipmentSlot slot, Vector3 gripLocal, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!HeadPose(out _, out _))
                return false;
            // A spot placed by hand, or the one being placed (GunSpots)
            if (GunSpots.TryGetSpot(slot, gripLocal, out position, out rotation))
                return true;
            bool leftHanded = VRSettings.GetLeftHandedMode();
            Quaternion bodyRot = BodyYaw(out Vector3 neck);
            Vector3 offset;
            switch (slot)
            {
                case EquipmentSlot.FirstPrimaryWeapon:
                    offset = Mirrored(chestOffset, leftHanded);
                    rotation = bodyRot * CanonicalSlingRot(leftHanded);
                    break;
                case EquipmentSlot.SecondPrimaryWeapon:
                    offset = Mirrored(backOffset, leftHanded);
                    rotation = bodyRot * PointGun(Mirrored(backMuzzle, leftHanded), Mirrored(backGrip, leftHanded), gripLocal);
                    // Its grip in the over-the-shoulder zone (the proven spot the old swap used; it rides the head)
                    if (VRGlobals.backHolster != null)
                    {
                        position = VRGlobals.backHolster.position - rotation * gripLocal;
                        return true;
                    }
                    break;
                case EquipmentSlot.Holster:
                    offset = Mirrored(hipOffset, leftHanded);
                    rotation = bodyRot * PointGun(Mirrored(hipMuzzle, leftHanded), Mirrored(hipGrip, leftHanded), gripLocal);
                    // Its grip in the hip zone (on the body's holster bone's parent)
                    if (VRGlobals.sidearmHolster != null)
                    {
                        position = VRGlobals.sidearmHolster.position - rotation * gripLocal;
                        return true;
                    }
                    break;
                default:
                    return false;
            }
            // The offsets are from the eyes looking level; the anchor is the neck, so take that part off
            position = neck + bodyRot * (offset - ShoulderStock.neckLocalOffset);
            return true;
        }

        private static Vector3 Mirrored(Vector3 vector, bool leftHanded)
        {
            if (leftHanded)
                vector.x = -vector.x;
            return vector;
        }

        // A gun's Weapon_root rotation with its muzzle along `muzzle` and its grip (the R-hand marker, off the bore) toward `grip`.
        // Weapon_root +up is the butt, -up the muzzle; the grip side comes from the gun itself, so no axis of it has to be known.
        private static Quaternion PointGun(Vector3 muzzle, Vector3 grip, Vector3 gripLocal)
        {
            Vector3 gripSideLocal = Vector3.ProjectOnPlane(gripLocal, Vector3.up);
            gripSideLocal = gripSideLocal.sqrMagnitude < 1e-6f ? Vector3.forward : gripSideLocal.normalized;
            Vector3 butt = -muzzle.normalized;
            Vector3 gripSide = Vector3.ProjectOnPlane(grip, butt);
            gripSide = gripSide.sqrMagnitude < 1e-6f ? Vector3.ProjectOnPlane(Vector3.back, butt).normalized : gripSide.normalized;
            return Quaternion.LookRotation(gripSide, butt) * Quaternion.Inverse(Quaternion.LookRotation(gripSideLocal, Vector3.up));
        }

        // The body's facing, for the guns hung on it: it follows the head's yaw only past bodyTurnDeadZone (a glance doesn't swing
        // them away from the hand reaching for them), and comes round to the head once it has held a new facing for
        // bodyRecenterAfter. Kept relative to the rig, so stick turns carry it 1:1. The anchor is the neck (ShoulderStock's): the
        // HMD swings ~10 cm about it as the head turns.
        public static float bodyTurnDeadZone = 40f;
        public static float bodyRecenterAfter = 0.75f;  // s of a still head (under bodyStillSpeed) before the body comes round
        public static float bodyRecenterRate = 60f;     // deg/s it comes round at
        public static float bodyStillSpeed = 30f;       // deg/s of head yaw that still counts as holding a facing
        private static float bodyYawInRig;
        private static float lastHeadYawInRig;
        private static float headStillFor;
        private static bool bodyYawSet;
        private static int bodyYawFrame = -1;

        // The body's turn in the rig advances once a frame; the neck and the rig's own yaw are read at every call. BodyWeapons places
        // the guns on the body at onBeforeRender, after the rig's LateUpdate pins (FirstPersonCameraState.ManualLateUpdate, the body
        // follow) have moved it for the frame: a neck cached by the frame's first caller (Update) left them a step behind while
        // walking (headset: the guns on the body "sorta lag/jitter while moving around", the hanging one, under the rig, steady).
        internal static Quaternion BodyYaw(out Vector3 neck)
        {
            HeadPose(out Vector3 headPos, out Quaternion headRot);
            float rigYaw = VRGlobals.vrOffsetter != null ? VRGlobals.vrOffsetter.transform.eulerAngles.y : 0f;
            if (bodyYawFrame != Time.frameCount)
            {
                bodyYawFrame = Time.frameCount;
                float headYaw = HeadYaw(out _).eulerAngles.y;
                float headYawInRig = Mathf.DeltaAngle(rigYaw, headYaw);
                float dt = Mathf.Clamp(Time.deltaTime, 1e-4f, 0.1f);
                if (!bodyYawSet)
                {
                    bodyYawSet = true;
                    bodyYawInRig = headYawInRig;
                    lastHeadYawInRig = headYawInRig;
                }
                float fromBody = Mathf.DeltaAngle(bodyYawInRig, headYawInRig);
                if (Mathf.Abs(fromBody) > bodyTurnDeadZone)
                    bodyYawInRig += fromBody - Mathf.Sign(fromBody) * bodyTurnDeadZone;
                headStillFor = Mathf.Abs(Mathf.DeltaAngle(lastHeadYawInRig, headYawInRig)) / dt < bodyStillSpeed ? headStillFor + dt : 0f;
                if (headStillFor > bodyRecenterAfter)
                    bodyYawInRig = Mathf.MoveTowardsAngle(bodyYawInRig, headYawInRig, bodyRecenterRate * dt);
                lastHeadYawInRig = headYawInRig;
            }
            neck = headPos + headRot * ShoulderStock.neckLocalOffset;
            return Quaternion.Euler(0f, rigYaw + bodyYawInRig, 0f);
        }

        // The gun in hand's own spot: its slot's, or the chest sling's for anything else (or with BodyWeapons off)
        private static bool HandsGunSpotPose(EFT.Player.FirearmController fc, out Vector3 position, out Quaternion rotation)
        {
            Vector3 gripLocal = GripInWeaponRoot(fc);
            if (BodyWeapons.enabled && BodyWeapons.SlotOf(fc.Item, out EquipmentSlot slot) && SpotPose(slot, gripLocal, out position, out rotation))
                return true;
            return SpotPose(EquipmentSlot.FirstPrimaryWeapon, gripLocal, out position, out rotation);
        }

        // The slot whose spot the gun in hand hangs at (HandsGunSpotPose's choice)
        internal static EquipmentSlot HandsGunSlot(EFT.Player.FirearmController fc)
        {
            return BodyWeapons.enabled && BodyWeapons.SlotOf(fc.Item, out EquipmentSlot slot) ? slot : EquipmentSlot.FirstPrimaryWeapon;
        }

        // The gun in hands hanging at its spot (GunSpots places it like the ones on the body): its slot, grip in its Weapon_root frame,
        // and where the hand takes it
        internal static bool HangingGun(out EFT.Player.FirearmController fc, out EquipmentSlot slot, out Vector3 gripLocal, out Vector3 grabPoint)
        {
            fc = null;
            slot = EquipmentSlot.FirstPrimaryWeapon;
            gripLocal = Vector3.forward;
            grabPoint = Vector3.zero;
            if (!Hanging || DetachedGunGone() || detachedGun.WeaponRoot == null || !DetachedGunOnHolder())
                return false;
            fc = detachedGun;
            slot = HandsGunSlot(fc);
            gripLocal = GripInWeaponRoot(fc);
            grabPoint = RegrabPoint(fc);
            return true;
        }

        // The gun's R-hand marker in its Weapon_root's frame (rotation only: the grip's side of the bore)
        private static Vector3 GripInWeaponRoot(EFT.Player.FirearmController fc)
        {
            Transform grip = WeaponPatches.RightGripMarker(fc);
            return grip != null && fc.WeaponRoot != null ? Quaternion.Inverse(fc.WeaponRoot.rotation) * (grip.position - fc.WeaponRoot.position) : Vector3.forward;
        }

        // ---- per-frame pose drives (called from GunInteractionController.UpdateWeaponPosition) ----

        // Canonical across-chest orientation in the yaw frame: muzzle down and across toward the off
        // side, gun's face out from the chest. Deterministic - the same pose every sling, per feedback
        // (round 2 kept the release orientation, which read as random). Recomputed per frame so the
        // knobs are live-tunable.
        private static Quaternion CanonicalSlingRot(bool leftHanded)
        {
            float side = leftHanded ? 1f : -1f;   // muzzle crosses to the OFF-hand side
            Vector3 m = new Vector3(side * slingAcross, -1f, slingForwardTilt).normalized;
            Vector3 u = -m;                        // WeaponRoot +up = butt direction (muzzle = -up)
            Vector3 f = Vector3.forward - Vector3.Dot(Vector3.forward, u) * u;   // gun face out from the chest
            if (f.sqrMagnitude < 1e-4f)
                f = Vector3.up;
            else
                f.Normalize();
            // whole-gun yaw about the vertical (side-mirrored so positive = toward the off side)
            return Quaternion.AngleAxis(side * slingYawDeg, Vector3.up)
                 * Quaternion.AngleAxis(slingRollDeg, m) * Quaternion.LookRotation(f, u);
        }

        // A switch away from the detached gun (a gun taken off the body while it hangs) hands Tarkov's hands,
        // VRGlobals.firearmController and VRGlobals.weaponHolder to the next gun a few frames before UpdateInput sees the new
        // controller; a stamp in between moved THAT gun's holder, and it was held at a wrong angle from then on (headset:
        // "switching between weapons ... the gun being angled wrong"). Not the detached gun any more = the detach ends here.
        private static bool DetachedGunGone()
        {
            var player = VRGlobals.player;
            return detachedGun == null || player == null || !ReferenceEquals(player.HandsController, detachedGun)
                || !ReferenceEquals(VRGlobals.firearmController, detachedGun);
        }

        // Still in hands but off our holder: Tarkov has it back for its put-away, nothing of ours to place
        private static bool DetachedGunOnHolder()
        {
            return detachedGun.WeaponRoot != null && VRGlobals.weaponHolder != null && detachedGun.WeaponRoot.parent == VRGlobals.weaponHolder.transform;
        }

        // false = the detached gun is gone: place the gun in hands the usual way
        public static bool DriveHangPose()
        {
            if (DetachedGunGone())
            {
                Reattach(restore: false);
                return false;
            }
            if (!DetachedGunOnHolder())
                return true;
            var fc = VRGlobals.firearmController;
            if (fc == null || fc.WeaponRoot == null || VRGlobals.weaponHolder == null || !HeadPose(out _, out _))
                return true;

            float dt = Mathf.Clamp(Time.deltaTime, 1e-4f, 0.05f);
            Quaternion yawRot = BodyYaw(out Vector3 headPos);
            // Its own spot (the chest sling for the first primary, the shoulder for the second, the hip for the pistol), in the
            // body-local frame the tuck-in runs in (from the neck, so a head turn doesn't swing it)
            if (!HandsGunSpotPose(fc, out Vector3 spotPos, out Quaternion spotRot))
                return true;
            Vector3 off = Quaternion.Inverse(yawRot) * (spotPos - headPos);
            Quaternion spotLocalRot = Quaternion.Inverse(yawRot) * spotRot;

            // tuck-in blend runs in the yaw-local frame (turning carries the gun 1:1 - world-space
            // blending was the "gun spins while I turn" bug), then the mount is rigid: no dangle.
            // Being placed by hand (GunSpots): it's in the hand, no tuck-in
            if (GunSpots.IsPlacing(HandsGunSlot(fc)))
            {
                slingLocalPos = off;
                slingLocalRot = spotLocalRot;
                posVel = Vector3.zero;
            }
            else
            {
                slingLocalPos = Vector3.SmoothDamp(slingLocalPos, off, ref posVel, attachTime);
                slingLocalRot = Quaternion.Slerp(slingLocalRot, spotLocalRot, Mathf.Min(1f, rotAttachRate * dt));
            }

            StampWeaponRoot(headPos + yawRot * slingLocalPos, yawRot * slingLocalRot);
            return true;
        }

        public static bool DriveSupportPose()
        {
            if (DetachedGunGone())
            {
                Reattach(restore: false);
                return false;
            }
            if (!DetachedGunOnHolder())
                return true;
            var fc = VRGlobals.firearmController;
            var lhObj = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.LeftHand : null;
            if (fc == null || fc.WeaponRoot == null || VRGlobals.weaponHolder == null || lhObj == null)
                return true;
            Transform lh = lhObj.transform;
            Quaternion ctrl = SupportControllerRot(lh);
            Quaternion target = ctrl * supportRelRot;

            // Weight feel on the one-handed support hold: the same swing/twist spring as the main hand,
            // on its OWN momentum state (SupportHold instance) so the two paths can never double-filter
            // - when both hands hold, Main runs and this method isn't even called. Position stays
            // PINNED at the grab point (grip in the palm); the weight lives in rotation about it, so
            // the filtered rotation swings WeaponRoot through the grip lever.
            if (VRSettings.GetWeaponWeightOn())
            {
                Vector3 barrel = supportSmoothedRot * Vector3.down;   // current muzzle dir (WeaponRoot -up)
                supportSmoothedRot = WeaponWeightFeel.SupportHold.Step(supportSmoothedRot, target, barrel,
                    fc.ErgonomicWeight, false, Time.deltaTime);
            }
            else
            {
                supportSmoothedRot = target;
            }
            StampWeaponRoot(lh.position + supportSmoothedRot * supportGripLever, supportSmoothedRot);
            return true;
        }

        // The support hand's LIVE controller rotation in world space: the raw pose action's local
        // rotation lifted through LeftHand's parent (the rig - LeftHand is a child of vrOffsetter and
        // its localRotation comes from this same action when it IS updated). LeftHand.transform itself
        // is rotation-frozen while supporting (UpdateLeftHand only writes position then).
        private static Quaternion SupportControllerRot(Transform lh)
        {
            var pose = Handedness.OffHandPose;
            return (lh.parent != null ? lh.parent.rotation : Quaternion.identity) * pose.localRotation;
        }

        private static Vector3 stampedPosition;
        private static Quaternion stampedRotation = Quaternion.identity;
        private static int stampedFrame = -1;

        private static void StampWeaponRoot(Vector3 pos, Quaternion rot)
        {
            stampedPosition = pos;
            stampedRotation = rot;
            stampedFrame = Time.frameCount;
            PlaceWeaponRoot(pos, rot);
        }

        // The holder hangs off RightHandPositioner, which HandsPositioner moves onto the free gun hand every Update, and Unity doesn't
        // order that against the stamp: when it runs after, the detached gun rides that hand's motion for the frame (headset: the
        // support-only hold "jittery", where the gun hand's hold is local to that same parent). HandsPositioner calls this after it moves.
        internal static void RestampDetached()
        {
            if (!Detached || stampedFrame != Time.frameCount || DetachedGunGone() || !DetachedGunOnHolder())
                return;
            PlaceWeaponRoot(stampedPosition, stampedRotation);
        }

        // Place WeaponRoot at the given world pose via the holder (rigid delta re-derived from the
        // current state every frame - absolute target, nothing accumulates).
        private static void PlaceWeaponRoot(Vector3 pos, Quaternion rot)
        {
            Transform wr = VRGlobals.firearmController.WeaponRoot;
            Transform holder = VRGlobals.weaponHolder.transform;
            Quaternion R = rot * Quaternion.Inverse(wr.rotation);
            holder.SetPositionAndRotation(pos + R * (holder.position - wr.position), R * holder.rotation);
        }

        // The headset pose in the HANDS' space: the HMD tracking pose (the camera's LOCAL transform -
        // SteamVR_TrackedObject writes it regardless of parent) lifted through the rig (vrOffsetter),
        // the same parent the hand transforms are driven under - so it can NEVER be in a different
        // scene chunk than the hands. Do NOT use VRCam.position as a head reference: in the hideout
        // VRCam is parented ~1000m from the player rig (headset log: dist=1000.x) while its local pose
        // still tracks the HMD; world-space camera reads are only valid in raids (direct rig child).
        internal static bool HeadPose(out Vector3 pos, out Quaternion rot)
        {
            Camera cam = VRGlobals.VRCam != null ? VRGlobals.VRCam : Camera.main;
            if (cam == null)
            {
                pos = Vector3.zero;
                rot = Quaternion.identity;
                return false;
            }
            var rig = VRGlobals.vrOffsetter;
            if (rig != null)
            {
                Transform rt = rig.transform;
                pos = rt.TransformPoint(cam.transform.localPosition);
                rot = rt.rotation * cam.transform.localRotation;
            }
            else
            {
                pos = cam.transform.position;
                rot = cam.transform.rotation;
            }
            return true;
        }

        // Yaw-only head frame (nodding doesn't drag the chest around; projected-up fallback keeps yaw
        // sane looking straight up/down).
        internal static Quaternion HeadYaw(out Vector3 headPos)
        {
            HeadPose(out headPos, out Quaternion headRot);
            Vector3 fwd = headRot * Vector3.forward;
            Vector3 flat = fwd;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f)
            {
                flat = (headRot * Vector3.up) * Mathf.Sign(-fwd.y);
                flat.y = 0f;
            }
            return flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;
        }

        // The gun hand; throughGun = it took hold of the gun, so a hand already on it feels that through the gun (GunHaptics)
        internal static void Pulse(float amp, bool throughGun = false)
        {
            GunHaptics.Pulse(true, throughGun ? GunHaptics.Feel.Carried : GunHaptics.Feel.Alone, amp, 0.07f, 95f);
        }
    }
}
