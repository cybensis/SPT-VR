using Comfort.Common;
using EFT.InventoryLogic;
using EFT.UI;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.VRManager;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;
using TarkovVR.Source.Controls;

namespace TarkovVR.Source.Player.Interactions
{
    // Chest sling + support-only hold. The gun stays in hand while the dominant grip is held (the grip boolean: the action
    // manifest has no capacitive-touch action). Releasing it for slingDelay hands the gun to the support hand if that grips
    // (SupportOnly, rigid on the support controller), else hangs it rigidly at its body spot; a grip press at the gun takes it back.
    // Game-side the gun never leaves the hands (no inventory op); firing is gated while detached.
    // A freshly equipped gun stays welded like vanilla until the first grip press on that controller.
    // "Toggle Gun Grip" (VRSettings.GetGunGripToggle): a press lets go instead of the open hand (the foregrip in
    // VRPlayerManager.ForegripGrip and a mounted gun in MountedGun the same).
    public static class ChestHang
    {
        public static Vector3 chestOffset = new Vector3(0.25f, -0.05f, -0.08f); // headset-local, yaw-only: where the grip end sits (sternum height, dominant side); x mirrors left-handed
        public static float slingAcross = 0.5f;         // how far the muzzle points across toward the off side (0 = straight down)
        public static float slingForwardTilt = 0.35f;   // muzzle tips out from the body
        public static float slingYawDeg = 15f;          // whole-gun yaw toward the off side; mirrors left-handed
        public static float slingRollDeg = 55f;          // fine roll about the barrel if the canonical pose sits face-on wrong
        public static float attachTime = 0.10f;         // s fly-in to the sling spot, then the mount is rigid
        public static float rotAttachRate = 10f;        // 1/s rotation tuck-in
        public static float slingDelay = 0.1f;          // s the grip must stay fully released before letting go (debounce)
        public static float regrabRadius = 0.13f;       // m RightHand rig transform to the regrab zone
        public static float regrabForwardOffset = 0.07f;// m the zone sits toward the muzzle from the R-hand marker
        // The other spots (BodyWeapons, SpotPose): where the gun's Weapon_root sits, from the eyes looking level in the body's frame,
        // and which way its muzzle and its grip point (x mirrors left-handed). back = the second primary, over the dominant
        // shoulder, grip out behind you; hip = the pistol, on the dominant hip, muzzle down, grip to the rear.
        public static Vector3 backOffset = new Vector3(0.17f, -0.17f, -0.17f);
        public static Vector3 backMuzzle = new Vector3(-0.3f, -1f, -0.2f);
        public static Vector3 backGrip = new Vector3(0.3f, 0f, -1f);
        public static Vector3 hipOffset = new Vector3(0.22f, -0.68f, 0.02f);
        public static Vector3 hipMuzzle = new Vector3(0f, -1f, 0.15f);
        public static Vector3 hipGrip = new Vector3(0.2f, 0f, -1f);
        // Done eating / using meds: the gun Tarkov draws back goes to its spot like at a raid start, unless the gun hand's grip is held
        public static bool hangAfterMeds = true;
        public static float afterMedsHangWindow = 5f;   // s after the hands change for the gun to go idle and hang
        public static bool debugLog = false;            // 1/sec chain dump while detached + save/restore lines
        private static float nextDebugLog;
        public static bool gripSoundHandOn = true;      // the gun's own hand-on-the-grip sound; false = cloth foley + a hint of gun
        public static float gripClothVolume = 0.85f;    // cloth mix: the gear (rig/backpack) pickup foley = the cloth layer
        public static float gripGunVolume = 0.18f;      // cloth mix: the weapon's own pickup clip, quiet, underneath
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
        // Held by the gun hand on the charging handle alone: the off hand let go with the handle pulled back, so the gun rides the
        // gun hand's controller and the off hand is free for the magazine. Ends on letting go of the handle (to the sling, or the
        // support hold if the off hand is back on the foregrip) or the off hand taking the foregrip again. Right-handed holds only.
        public static bool HandleHeld { get; private set; }
        public static bool Detached => Hanging || SupportOnly || HandleHeld; // fire gate
        public static bool HandInRegrabZone { get; private set; } // dominant palm within regrab reach of the detached gun (loot pointer yields to the regrab)
        public static bool OffHandAtGunGrip { get; private set; } // the off hand where a grip press takes the gun into it (the off-hand loot pointer and body grab yield)
        private static bool prevOffGrip;
        private static bool prevOffGripWasLeft = true;  // which controller that was: the off hand changes sides with the gun
        private static readonly TarkovVR.Source.Misc.ZoneEntryBlip swapZoneBlip = new TarkovVR.Source.Misc.ZoneEntryBlip();

        private static bool gripSeen;                   // welded like vanilla until the first grip press on this controller
        private static int ignoredPressFrame = -1;      // a grip press BodyWeapons took (a gun off the body), not a regrab
        private static Item gripSeenFor;                // taken off the body with the grip closed: grip-bound from the moment it's in hands
        private static bool prevGrip;
        private static bool toggleModeSeen;             // the "Toggle Gun Grip" setting as the last frame held it
        private static bool prevSupporting;             // for the foregrip-grab foley edge
        private static float releaseTimer;
        private static int lastTickFrame = -10;
        private static object lastHandsController;
        private static object lastPlayer;
        private static float hangOnArrivalUntil = -1f;  // the gun Tarkov hands you (raid start, walking into the range) goes to its spot
        private static bool medsSinceGun;               // a food / med came into your hands since the last hands change seen here

        // MedsController.Spawn (EatingPatches) for your player: runs with menus open too
        internal static void NoteMedsInHands()
        {
            medsSinceGun = true;
        }
        private static EFT.Player.FirearmController detachedGun;   // the gun slung / support-held: only its holder is ever stamped
        // Holder local pose from before the first detach. The anchor only re-stamps rotation at equip, so it must be restored
        // when the gun returns to the hand, and kept across a SupportOnly -> sling transition (holderSaved guards the save).
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
        // HandleHeld: the gun's pose rigid in the gun-hand controller's frame (rigid: a swinging gun would slide the held handle along its track)
        private static Vector3 handleHoldRelPos;
        private static Quaternion handleHoldRelRot = Quaternion.identity;

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

            if (!(VRGlobals.vrPlayer is RaidVRPlayerManager || VRGlobals.vrPlayer is HideoutVRPlayerManager))
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
            var firearmController = VRGlobals.firearmController;
            bool holdingFirearm = player != null && firearmController != null && firearmController.Item != null
                && !WeaponPatches.grenadeEquipped
                && ReferenceEquals(player.HandsController, firearmController); // guards a stale firearmController ref

            // Controller switched (holster swap, weapon change): the equip path re-stamps the holder
            // and owns the arms - clear our state, restore nothing. New gun starts welded (gripSeen off).
            object handsController = player != null ? player.HandsController : null;
            if (!ReferenceEquals(handsController, lastHandsController))
            {
                // From food / meds straight back to a gun (Tarkov's draw of the item you had): hang it once it's idle (the branch
                // below). MedsController covers every food, drink and med; medsSinceGun also catches one used from the inventory
                // screen (this doesn't run in menus, so the meds controller can come and go unseen)
                if (hangAfterMeds && (medsSinceGun || lastHandsController is EFT.Player.MedsController) && handsController is EFT.Player.FirearmController)
                    HangOnArrival(afterMedsHangWindow);
                if (!(handsController is EFT.Player.MedsController))
                    medsSinceGun = false;
                lastHandsController = handsController;
                gripSeen = false;
                releaseTimer = 0f;
                prevGrip = grip;
                Reattach(restore: false);
                // A new gun (or none) starts in the right hand; a grenade the left hand took off the chest stays in it (GrenadeSlot), and
                // so does a gun it took off the body (BodyWeapons)
                if (!GrenadeSlot.KeepsLeftHand && !BodyWeapons.KeepsLeftHand)
                    Handedness.SetGunInLeftHand(false);
            }

            if (!holdingFirearm)
            {
                releaseTimer = 0f;
                Reattach(restore: false);
                if (!GrenadeSlot.KeepsLeftHand && !BodyWeapons.KeepsLeftHand)
                    Handedness.SetGunInLeftHand(false);
                return;
            }
            // Taken off the body (BodyWeapons): the grip that took it holds it, so opening the hand lets it go to its spot
            if (gripSeenFor != null && firearmController.Item == gripSeenFor)
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

            // Foregrip-grab foley on the isSupporting edge seen here, not inside UpdateLeftHand's engage branches: which
            // branch sets it depends on the snap/hold-toggle settings.
            if (supporting && !prevSupporting && !Detached)
                PlayGripSound();
            prevSupporting = supporting;

            // The off hand taking the gun by its grip, out of the gun hand (also with the sling off: the gun stays in a hand)
            if (!Detached && TryGrabFromGunHand(firearmController, supporting, offGripPressed))
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

            // The gun Tarkov put in your hands at a raid start or the range: straight to its spot. Its draw skipped, and the hang
            // only once it's idle: the holder is stamped for it by then.
            if (hangOnArrivalUntil > 0f && !Detached && !grip && !supporting && BodyWeapons.IsActive)
            {
                if (!(firearmController.CurrentOperation is EFT.Player.FirearmController.Idling))
                    WeaponPatches.SkipDrawOrPutAway(firearmController);
                else
                {
                    BeginHang(firearmController, snap: true);
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
                bool near = Handedness.GunInLeftHand ? LeftHandAtGrip(firearmController) : HandNearGun(firearmController);
                HandInRegrabZone = near;
                RegrabZonePulse(near);
                if (!supporting)
                {
                    // The gun hand holding the charging handle back: the gun hangs from that hand now (HandleHeld), the off hand free
                    if (CanHoldByHandle())
                        BeginHandleHold(firearmController);
                    else
                    {
                        // support hand let go too - both hands off -> straight to the sling (no flash of
                        // the marker anchor in between)
                        SupportOnly = false;
                        BeginHang(firearmController);
                    }
                }
                // (not a press on a belt gun's box: the gun hand takes the box, VRReloadController.GunHandAtBox, a frame old)
                else if (pressed && near && !VRReloadController.HandleInGunHand && !VRReloadController.BoltInGunHand && !VRReloadController.GunHandAtBox)
                {
                    // dominant hand takes the grip back -> normal (two-handed) hold. Reattach clears SupportOnly itself:
                    // clearing it here first makes wasDetached read false inside = no retake sound/haptic.
                    Reattach(restore: true);
                    if (Handedness.GunInLeftHand)
                        VRPlayerManager.CacheGunInHand();   // the holder back in its clean pose, as off the sling
                    gripSeen = true;
                }
                prevGrip = grip;
                return;
            }

            if (HandleHeld)
            {
                HandInRegrabZone = false;
                if (!VRReloadController.HandleInGunHand)
                {
                    // Let go of the handle (the bolt's gone home): held up by the off hand if it's back on the foregrip, else nobody's
                    HandleHeld = false;
                    if (supporting)
                        BeginSupportHold(firearmController);
                    else
                        BeginHang(firearmController);
                }
                else if (supporting)
                {
                    // The off hand back on the foregrip, the handle still back: it holds the gun up again
                    HandleHeld = false;
                    BeginSupportHold(firearmController);
                }
                prevGrip = grip;
                return;
            }

            if (Hanging)
            {
                bool near = HandNearGun(firearmController);
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
                else if (!Handedness.LeftHanded && leftGripPressed && LeftHandAtGrip(firearmController) && !VRReloadController.OwnsOffHand
                    && !HandsInteractionController.HoldingLoot)
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

            // Switching between hold and toggle: held as it is until the next press (else hold mode drops a gun held with the button up)
            bool toggle = VRSettings.GetGunGripToggle();
            if (toggle != toggleModeSeen)
            {
                toggleModeSeen = toggle;
                gripSeen = false;
            }

            // Toggle: a press lets go, the button up holds. Hold: the grip open for slingDelay lets go.
            bool letGo = false;
            if (toggle)
            {
                releaseTimer = 0f;
                letGo = pressed && !gripOwnedElsewhere;
            }
            else if (grip)
            {
                gripSeen = true;   // gun is now grip-bound: open the hand and it lets go
                releaseTimer = 0f;
            }
            else if (gripSeen && !gripOwnedElsewhere)
            {
                releaseTimer += Time.deltaTime;
                letGo = releaseTimer >= slingDelay;
            }
            else
            {
                releaseTimer = 0f;
            }
            if (letGo)
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
                    BeginSupportHold(firearmController);
                else
                    BeginHang(firearmController);
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
        private static void BeginHang(EFT.Player.FirearmController firearmController, bool snap = false)
        {
            Transform weaponRoot = firearmController.WeaponRoot;
            if (weaponRoot == null || VRGlobals.weaponHolder == null || VRGlobals.ikManager == null ||
                VRGlobals.vrPlayer == null || !HeadPose(out _, out _))
                return;

            // Must run before SaveHolderOnce: strip any live collision enact off the holder. Else releasing the grip while the
            // gun is pressed against something (prone: the ground) saves the enacted pose, and every restore re-applies the
            // collision offset, compounding per sling cycle. Reset also clears the rig-motion tracking. The same for a reload jolt.
            WeaponCollision.Reset();
            TarkovVR.Source.Weapons.GunJolt.Reset();

            VRGlobals.vrPlayer.ForceUnlockHand();      // two-handing can't survive letting go
            SaveHolderOnce();

            // fly-in starts from the release pose, captured in the body's frame (where the spots are)
            Quaternion yawRot = BodyYaw(out Vector3 headPos);
            slingLocalPos = Quaternion.Inverse(yawRot) * (weaponRoot.position - headPos);
            slingLocalRot = Quaternion.Inverse(yawRot) * weaponRoot.rotation;
            posVel = Vector3.zero;
            if (snap && HandsGunSpotPose(firearmController, out Vector3 spotPos, out Quaternion spotRot))
            {
                slingLocalPos = Quaternion.Inverse(yawRot) * (spotPos - headPos);
                slingLocalRot = Quaternion.Inverse(yawRot) * spotRot;
            }

            // Out of the left hand too: on the sling the gun is nobody's. Before DetachCommon frees the right arm, so that starts
            // from the arms as they were and not the left-hand hold's own right-arm target (RealLeftHandedPatches.ReleaseArms)
            Handedness.SetGunInLeftHand(false);
            detachedGun = firearmController;
            DetachCommon();
            Hanging = true;
            // No ADS with the gun off the grip (DetachedAimPatches)
            TarkovVR.Patches.Core.Player.DetachedAimPatches.StopAimingOffTheGrip();
            if (!snap)
                Pulse(0.6f);
        }

        // The left hand's palm, knuckles or fingertips at the slung gun's grip (the right hand's regrab point and reach)
        private static bool LeftHandAtGrip(EFT.Player.FirearmController firearmController)
        {
            if (firearmController.WeaponRoot == null || VRGlobals.handsInteractionController == null)
                return false;
            Vector3 grip = RegrabPoint(firearmController);
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

        // The left hand where a grip press takes the slung gun into it (BodyWeapons' left-hand take yields there)
        internal static bool LeftHandAtHangingGrip()
        {
            return Hanging && VRGlobals.firearmController != null && LeftHandAtGrip(VRGlobals.firearmController);
        }

        // ---- the gun from one hand into the other ----

        // The off hand at the gun hand's grip with a grip press: the gun goes into the off hand. Not a pistol (PistolSwapsOnRelease),
        // not at or near the foregrip (short guns: the foregrip's grab wins), not while the off hand is on a reload part or in its
        // zone (VRReloadController.OwnsOffHand, a frame old: the magazine zone wins), holds loot or the scope dial. True = swapped.
        private static bool TryGrabFromGunHand(EFT.Player.FirearmController firearmController, bool supporting, bool offGripPressed)
        {
            var hands = VRGlobals.handsInteractionController;
            float swapScore = handSwap && !Handedness.LeftHanded && !supporting && !VRGlobals.vrPlayer.isWeapPistol
                && !VRReloadController.OwnsOffHand && hands != null && hands.heldItem == null && hands.scopeTransform == null
                && !OffHandAtForegrip() ? OffHandGripScore(firearmController) : float.MaxValue;
            bool atGrip = swapScore < 1f;
            // Nearest wins: a magazine that loads through the grip (MP7, MP9, Uzi, Vector, PP-91) sits inside this reach, and a short
            // one is all grip. The hand nearer the magazine than the grip, each over its own reach, leaves the press to the magazine
            // (VRReloadController.Tick, later this frame, takes it with that reach).
            bool magazineNearer = atGrip && VRReloadController.OffHandMagazineScore(offTouchPoints) < swapScore;
            if (magazineNearer)
                atGrip = false;
            OffHandAtGunGrip = atGrip;
            // A light blip on the off hand coming into reach of the grip (as the regrab zone's): only where a press would swap
            if (swapZoneBlip.Enter(atGrip))
                HapticMixer.PlayPulse(Handedness.OffHandSource, 0.25f, 0.07f, 95f);
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
        private static float OffHandGripScore(EFT.Player.FirearmController firearmController)
        {
            offTouchPoints.Clear();
            if (firearmController.WeaponRoot == null || VRGlobals.handsInteractionController == null)
                return float.MaxValue;
            Vector3 grip = RegrabPoint(firearmController);
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
            // The holder in its clean pose (the left hand's hold is read off it once, VRPlayerManager.CacheGunInHand: a collision nudge
            // left on it would feed back) and the collision's rig-motion tracking re-primed (the gun jumps hands)
            WeaponCollision.Reset();
            TarkovVR.Source.Weapons.GunJolt.Reset();
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
            swapZoneBlip.MarkInside();
            OffHandAtGunGrip = false;
            Pulse(0.45f, throughGun: true);
            PlayGripSound();
        }

        private static void BeginSupportHold(EFT.Player.FirearmController firearmController)
        {
            Transform weaponRoot = firearmController.WeaponRoot;
            var leftHandObject = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.LeftHand : null;
            if (weaponRoot == null || leftHandObject == null || VRGlobals.weaponHolder == null || VRGlobals.ikManager == null)
                return;

            WeaponCollision.Reset();   // strip any live collision enact BEFORE saving (see BeginHang)
            TarkovVR.Source.Weapons.GunJolt.Reset();
            SaveHolderOnce();
            // Rigid attach to the support controller at the current relative pose (the left hand already grips the gun).
            // Rotation comes from the raw pose action, not LeftHand.transform: while isSupporting, UpdateLeftHand only
            // writes localPosition, so the transform's rotation is frozen at support-grab time.
            Transform leftHand = leftHandObject.transform;
            Quaternion controllerRotation = SupportControllerRot(leftHand);
            supportRelPos = Quaternion.Inverse(controllerRotation) * (weaponRoot.position - leftHand.position);
            supportRelRot = Quaternion.Inverse(controllerRotation) * weaponRoot.rotation;
            supportGripLever = Quaternion.Inverse(supportRelRot) * supportRelPos;
            supportSmoothedRot = weaponRoot.rotation;

            detachedGun = firearmController;
            DetachCommon();
            SupportOnly = true;
            TarkovVR.Patches.Core.Player.DetachedAimPatches.StopAimingOffTheGrip();
            // The gun hand lets go, and the support hand now carries the whole gun: it feels that through the gun
            Pulse(0.35f, throughGun: true);
        }

        // The support hold's off hand letting go with the gun hand on the charging handle: that hand carries the gun from here
        private static bool CanHoldByHandle()
        {
            return VRReloadController.HandleInGunHand && !Handedness.GunInLeftHand
                && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.RightHand != null;
        }

        // Rigid on the gun-hand controller at the pose the support hold left it in (the hand already holds the handle)
        private static void BeginHandleHold(EFT.Player.FirearmController firearmController)
        {
            Transform weaponRoot = firearmController.WeaponRoot;
            Transform hand = VRGlobals.vrPlayer.RightHand.transform;
            if (weaponRoot == null)
            {
                SupportOnly = false;
                BeginHang(firearmController);
                return;
            }
            Quaternion controller = GunHandControllerRot(hand);
            handleHoldRelPos = Quaternion.Inverse(controller) * (weaponRoot.position - hand.position);
            handleHoldRelRot = Quaternion.Inverse(controller) * weaponRoot.rotation;
            SupportOnly = false;
            HandleHeld = true;
            // The gun hand off the shared hand latch onto a pin on the handle: the off hand's magazine needs that latch on the track
            VRReloadController.PinGunHandOnHandle();
            Pulse(0.35f, throughGun: true);
        }

        public static bool DriveHandleHoldPose()
        {
            if (DetachedGunGone())
            {
                Reattach(restore: false);
                return false;
            }
            if (!DetachedGunOnHolder())
                return true;
            var firearmController = VRGlobals.firearmController;
            var handObject = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.RightHand : null;
            if (firearmController == null || firearmController.WeaponRoot == null || VRGlobals.weaponHolder == null || handObject == null)
                return true;
            Transform hand = handObject.transform;
            Quaternion controller = GunHandControllerRot(hand);
            StampWeaponRoot(hand.position + controller * handleHoldRelPos, controller * handleHoldRelRot);
            return true;
        }

        // The gun hand's live controller rotation in world space, as SupportControllerRot has the off hand's
        private static Quaternion GunHandControllerRot(Transform hand)
        {
            return (hand.parent != null ? hand.parent.rotation : Quaternion.identity) * Handedness.GunHandPose.localRotation;
        }

        // Shared detach work: free the dominant arm (the HandsIsEmpty pattern). The collision Reset happens earlier, in the
        // Begin* methods before SaveHolderOnce: the strip must precede the save.
        private static void DetachCommon()
        {
            KeepGunHandFree();
            gripSeen = false;
            // The hand is AT the gun the instant it lets go - start the zone flag "in" so the
            // in-reach blip only fires after leaving and coming back.
            regrabZoneBlip.MarkInside();
        }

        // EFT solves the gun-hand arm onto _markers[1] (the gun's weapon_R_IK_marker) itself every LateUpdate (Player.IkProcess/IkApply),
        // whatever our IK does, and Unity doesn't order the two. So while detached _markers[1] is our IK's own target, as on empty
        // hands and the free left hand; every frame, since EFT gathers a fresh marker on a weapon change.
        private static Transform gunHandMarker;     // the gun's own, given back when the hand takes the gun again
        private static Transform freedHandMarker;   // what _markers[1] was set to

        internal static void KeepGunHandFree()
        {
            // A press check's marker on the gun hand: the gun's own back first, or it's saved below as the gun's and given back on the
            // next grab (the hand would fly off to it)
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
        private static readonly TarkovVR.Source.Misc.ZoneEntryBlip regrabZoneBlip = new TarkovVR.Source.Misc.ZoneEntryBlip();
        private static void RegrabZonePulse(bool near)
        {
            if (regrabZoneBlip.Enter(near))
                Pulse(0.3f);
        }

        // Back to the dominant hand (restore = true), or just clear state because something else owns
        // the gun now (controller switch, raid end - restore NOTHING, the equip path re-stamps).
        private static void Reattach(bool restore)
        {
            bool wasDetached = Detached;
            Hanging = false;
            SupportOnly = false;
            HandleHeld = false;
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
            if (wasDetached)
                Pulse(0.45f, throughGun: true);
            // The same grab sound for every grab, off the sling included
            if (wasDetached)
                PlayGripSound();
        }

        // Hands going onto the held gun: the gun's own hand-on-grip sound (gripSoundHandOn), else the generic gear
        // (rig/backpack) pickup foley with the weapon's own pickup clip quiet under it. Fired for the foregrip grab
        // (the isSupporting edge), the grip retake, the hand swap and a gun drawn off the body (BodyWeapons).
        public static void PlayGripSound()
        {
            var firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController.WeaponRoot == null)
                return;
            if (gripSoundHandOn)
            {
                PlayHandOnSound(firearmController);
                return;
            }
            var guiSounds = Singleton<GUISounds>.Instance;
            var betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (guiSounds == null || betterAudio == null)
                return;
            Vector3 position = firearmController.WeaponRoot.position;

            // cloth layer with a fallback chain, in case a sound-group name doesn't exist in the bundle
            AudioClip cloth = guiSounds.GetItemClip("gear_generic", EInventorySoundType.pickup);
            if (cloth == null)
            {
                cloth = guiSounds.GetItemClip("generic", EInventorySoundType.pickup);
            }
            var item = firearmController.Item;
            AudioClip gun = item != null ? guiSounds.GetItemClip(item.ItemSound, EInventorySoundType.pickup) : null;

            if (cloth != null)
                betterAudio.TryPlayAtPoint(out _, position, cloth, BetterAudio.AudioSourceGroupType.Character, 25, gripClothVolume);
            if (gun != null)
                betterAudio.TryPlayAtPoint(out _, position, gun, BetterAudio.AudioSourceGroupType.Character, 25, cloth != null ? gripGunVolume : gripClothVolume);
        }

        // The sound Tarkov's reload plays as the support hand goes back onto the gun, from the gun's own sound player
        // (M4A1: HandOn = hand_grip4, HandOff = hand_grip3). A gun without either stays silent. Not through VRReloadController's
        // PlayFelt: no audio haptics (the grab already pulses) and nothing sent over FIKA.
        private static void PlayHandOnSound(EFT.Player.FirearmController firearmController)
        {
            if (handOnSoundFor != firearmController)
            {
                handOnSoundFor = firearmController;
                handOnSoundPlayer = firearmController._controllerObject != null ? firearmController._controllerObject.GetComponentInChildren<BaseSoundPlayer>(true) : null;
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

        // 1/sec while detached: every transform in the hand->gun chain that could change the hand<->gun relationship after a regrab
        private static void DebugChainDump()
        {
            if (!debugLog || Time.time < nextDebugLog)
                return;
            nextDebugLog = Time.time + 1f;
            var firearmController = VRGlobals.firearmController;
            Transform holder = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform : null;
            Transform weaponRoot = firearmController != null ? firearmController.WeaponRoot : null;
            if (holder == null || weaponRoot == null)
                return;
            Transform positioner = holder.parent;
            Plugin.MyLog.LogInfo(
                $"[ChestHang] holderL={holder.localPosition:F3}/{holder.localRotation.eulerAngles:F1}" +
                $" wrL={weaponRoot.localPosition:F3}/{weaponRoot.localRotation.eulerAngles:F1} wrParent='{(weaponRoot.parent != null ? weaponRoot.parent.name : "null")}'" +
                $" posL={(positioner != null ? positioner.localPosition : Vector3.zero):F3} saved={savedHolderLocalPos:F3}/{savedHolderLocalRot.eulerAngles:F1}");
        }

        // Mirrors the left hand's foregrip check (LeftHand rig transform vs weapon_L_hand_marker, Transforms[10]): the anchor is
        // the weapon's own right-grip marker (ECharacterWeaponBones.weapon_R_hand_marker = Transforms[8]), where the rendered
        // right hand sits when gripping. Not WeaponRoot: that sits at the receiver, behind the pistol grip on most guns.
        // Anywhere on the hanging gun takes it back, as on the body guns (BodyWeapons.ReachScore): under 1 = in reach
        private static bool HandNearGun(EFT.Player.FirearmController firearmController)
        {
            return RegrabScore(firearmController) < 1f;
        }

        internal static float RegrabScore()
        {
            return VRGlobals.firearmController != null ? RegrabScore(VRGlobals.firearmController) : float.MaxValue;
        }

        private static float RegrabScore(EFT.Player.FirearmController firearmController)
        {
            if (firearmController.WeaponRoot == null)
                return float.MaxValue;
            return BodyWeapons.ReachScore(firearmController.WeaponRoot, firearmController.WeaponRoot, RegrabPoint(firearmController), regrabRadius, alongBarrel: true);
        }

        // The grip, a little toward the muzzle (falls back to WeaponRoot)
        private static Vector3 RegrabPoint(EFT.Player.FirearmController firearmController)
        {
            Transform anchor = WeaponPatches.RightGripMarker(firearmController);
            if (anchor == null)
                anchor = firearmController.WeaponRoot;
            return anchor.position - firearmController.WeaponRoot.up * regrabForwardOffset;
        }

        // A gun in hand and not hanging owns the gun arm (Tarkov IKs it onto the grip). A gun drawn while the last one still hung
        // gets its arm freed by its own GunInteractionController.OnEnable (it sees Detached): given back here, every frame.
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
            var firearmController = VRGlobals.firearmController;
            if (player != null && player._markers.Length > 1 && player._markers[1] == rightHand.transform && firearmController != null && firearmController.HandsHierarchy != null)
            {
                Transform gunMarker = firearmController.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon_R_IK_marker);
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
        // and the gun in hand goes there when you let go. First primary on the chest sling, second over the dominant shoulder,
        // pistol on the dominant hip. In the body's frame (BodyYaw), not the head's. The back and hip guns are turned by where
        // their muzzle and grip point (gripLocal = the gun's R-hand marker in its Weapon_root frame), so any gun sits the same way.
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
                    // Its grip in the over-the-shoulder zone (it rides the head)
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
            position = neck + bodyRot * (offset - HeadTracking.neckLocalOffset);
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
        // follow) have moved it for the frame: a neck cached by the frame's first caller (Update) leaves them a step behind while walking.
        internal static Quaternion BodyYaw(out Vector3 neck)
        {
            HeadPose(out Vector3 headPos, out Quaternion headRot);
            float rigYaw = VRGlobals.vrOffsetter != null ? VRGlobals.vrOffsetter.transform.eulerAngles.y : 0f;
            if (bodyYawFrame != Time.frameCount)
            {
                bodyYawFrame = Time.frameCount;
                float headYaw = HeadYaw(out _).eulerAngles.y;
                float headYawInRig = Mathf.DeltaAngle(rigYaw, headYaw);
                float deltaTime = Mathf.Clamp(Time.deltaTime, 1e-4f, 0.1f);
                if (!bodyYawSet)
                {
                    bodyYawSet = true;
                    bodyYawInRig = headYawInRig;
                    lastHeadYawInRig = headYawInRig;
                }
                float fromBody = Mathf.DeltaAngle(bodyYawInRig, headYawInRig);
                if (Mathf.Abs(fromBody) > bodyTurnDeadZone)
                    bodyYawInRig += fromBody - Mathf.Sign(fromBody) * bodyTurnDeadZone;
                headStillFor = Mathf.Abs(Mathf.DeltaAngle(lastHeadYawInRig, headYawInRig)) / deltaTime < bodyStillSpeed ? headStillFor + deltaTime : 0f;
                if (headStillFor > bodyRecenterAfter)
                    bodyYawInRig = Mathf.MoveTowardsAngle(bodyYawInRig, headYawInRig, bodyRecenterRate * deltaTime);
                lastHeadYawInRig = headYawInRig;
            }
            neck = headPos + headRot * HeadTracking.neckLocalOffset;
            return Quaternion.Euler(0f, rigYaw + bodyYawInRig, 0f);
        }

        // The gun in hand's own spot: its slot's, or the chest sling's for anything else (or with BodyWeapons off)
        private static bool HandsGunSpotPose(EFT.Player.FirearmController firearmController, out Vector3 position, out Quaternion rotation)
        {
            Vector3 gripLocal = GripInWeaponRoot(firearmController);
            if (BodyWeapons.enabled && BodyWeapons.SlotOf(firearmController.Item, out EquipmentSlot slot) && SpotPose(slot, gripLocal, out position, out rotation))
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
        private static Vector3 GripInWeaponRoot(EFT.Player.FirearmController firearmController)
        {
            Transform grip = WeaponPatches.RightGripMarker(firearmController);
            return grip != null && firearmController.WeaponRoot != null ? Quaternion.Inverse(firearmController.WeaponRoot.rotation) * (grip.position - firearmController.WeaponRoot.position) : Vector3.forward;
        }

        // ---- per-frame pose drives (called from GunInteractionController.UpdateWeaponPosition) ----

        // Canonical across-chest orientation in the yaw frame: muzzle down and across toward the off side, gun's face out
        // from the chest. The same pose every sling (not the release orientation). Recomputed per frame so the knobs are live-tunable.
        private static Quaternion CanonicalSlingRot(bool leftHanded)
        {
            float side = leftHanded ? 1f : -1f;   // muzzle crosses to the OFF-hand side
            Vector3 muzzle = new Vector3(side * slingAcross, -1f, slingForwardTilt).normalized;
            Vector3 butt = -muzzle;                        // WeaponRoot +up = butt direction (muzzle = -up)
            Vector3 face = Vector3.forward - Vector3.Dot(Vector3.forward, butt) * butt;   // gun face out from the chest
            if (face.sqrMagnitude < 1e-4f)
                face = Vector3.up;
            else
                face.Normalize();
            // whole-gun yaw about the vertical (side-mirrored so positive = toward the off side)
            return Quaternion.AngleAxis(side * slingYawDeg, Vector3.up)
                 * Quaternion.AngleAxis(slingRollDeg, muzzle) * Quaternion.LookRotation(face, butt);
        }

        // A switch away from the detached gun (a gun taken off the body while it hangs) hands Tarkov's hands,
        // VRGlobals.firearmController and VRGlobals.weaponHolder to the next gun a few frames before UpdateInput sees the new
        // controller; a stamp in between would move that gun's holder. Not the detached gun any more = the detach ends here.
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
            var firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController.WeaponRoot == null || VRGlobals.weaponHolder == null || !HeadPose(out _, out _))
                return true;

            float deltaTime = Mathf.Clamp(Time.deltaTime, 1e-4f, 0.05f);
            Quaternion yawRot = BodyYaw(out Vector3 headPos);
            // Its own spot (the chest sling for the first primary, the shoulder for the second, the hip for the pistol), in the
            // body-local frame the tuck-in runs in (from the neck, so a head turn doesn't swing it)
            if (!HandsGunSpotPose(firearmController, out Vector3 spotPos, out Quaternion spotRot))
                return true;
            Vector3 offset = Quaternion.Inverse(yawRot) * (spotPos - headPos);
            Quaternion spotLocalRot = Quaternion.Inverse(yawRot) * spotRot;

            // tuck-in blend runs in the yaw-local frame (turning carries the gun 1:1; a world-space blend spins the gun
            // under turns), then the mount is rigid: no dangle.
            // Being placed by hand (GunSpots): it's in the hand, no tuck-in
            if (GunSpots.IsPlacing(HandsGunSlot(firearmController)))
            {
                slingLocalPos = offset;
                slingLocalRot = spotLocalRot;
                posVel = Vector3.zero;
            }
            else
            {
                slingLocalPos = Vector3.SmoothDamp(slingLocalPos, offset, ref posVel, attachTime);
                slingLocalRot = Quaternion.Slerp(slingLocalRot, spotLocalRot, Mathf.Min(1f, rotAttachRate * deltaTime));
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
            var firearmController = VRGlobals.firearmController;
            var leftHandObject = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.LeftHand : null;
            if (firearmController == null || firearmController.WeaponRoot == null || VRGlobals.weaponHolder == null || leftHandObject == null)
                return true;
            Transform leftHand = leftHandObject.transform;
            Quaternion controllerRotation = SupportControllerRot(leftHand);
            Quaternion target = controllerRotation * supportRelRot;

            // Weight feel on the one-handed support hold: the same swing/twist spring as the main hand, on its own momentum
            // state (SupportHold instance) so the two paths can't double-filter. Position stays pinned at the grab point;
            // the weight lives in rotation about it, so the filtered rotation swings WeaponRoot through the grip lever.
            if (VRSettings.GetWeaponWeightOn())
            {
                Vector3 barrel = supportSmoothedRot * Vector3.down;   // current muzzle dir (WeaponRoot -up)
                supportSmoothedRot = WeaponWeightFeel.SupportHold.Step(supportSmoothedRot, target, barrel,
                    firearmController.ErgonomicWeight, false, Time.deltaTime, VRGlobals.vrOffsetter != null ? VRGlobals.vrOffsetter.transform : null);
            }
            else
            {
                supportSmoothedRot = target;
            }
            StampWeaponRoot(leftHand.position + supportSmoothedRot * supportGripLever, supportSmoothedRot);
            return true;
        }

        // The support hand's live controller rotation in world space: the raw pose action's local rotation lifted through
        // LeftHand's parent (the rig, vrOffsetter). LeftHand.transform itself is rotation-frozen while supporting
        // (UpdateLeftHand only writes position then).
        private static Quaternion SupportControllerRot(Transform leftHand)
        {
            var pose = Handedness.OffHandPose;
            return (leftHand.parent != null ? leftHand.parent.rotation : Quaternion.identity) * pose.localRotation;
        }

        private static Vector3 stampedPosition;
        private static Quaternion stampedRotation = Quaternion.identity;
        private static int stampedFrame = -1;

        private static void StampWeaponRoot(Vector3 position, Quaternion rotation)
        {
            stampedPosition = position;
            stampedRotation = rotation;
            stampedFrame = Time.frameCount;
            PlaceWeaponRoot(position, rotation);
        }

        // The holder hangs off RightHandPositioner, which HandsPositioner moves onto the free gun hand every Update, and Unity doesn't
        // order that against the stamp: when it runs after, the detached gun rides that hand's motion for the frame (jitter).
        // HandsPositioner calls this after it moves.
        internal static void RestampDetached()
        {
            if (!Detached || stampedFrame != Time.frameCount || DetachedGunGone() || !DetachedGunOnHolder())
                return;
            PlaceWeaponRoot(stampedPosition, stampedRotation);
        }

        // Place WeaponRoot at the given world pose via the holder (rigid delta re-derived from the
        // current state every frame - absolute target, nothing accumulates).
        private static void PlaceWeaponRoot(Vector3 position, Quaternion rotation)
        {
            Transform weaponRoot = VRGlobals.firearmController.WeaponRoot;
            Transform holder = VRGlobals.weaponHolder.transform;
            Quaternion turn = rotation * Quaternion.Inverse(weaponRoot.rotation);
            holder.SetPositionAndRotation(position + turn * (holder.position - weaponRoot.position), turn * holder.rotation);
        }

        // The headset pose in the hands' space: the HMD tracking pose (the camera's local transform - SteamVR_TrackedObject
        // writes it regardless of parent) lifted through the rig (vrOffsetter), the parent the hand transforms are driven under.
        // Do not use VRCam.position as a head reference: in the hideout VRCam is parented ~1000m from the player rig while
        // its local pose still tracks the HMD; world-space camera reads are only valid in raids (direct rig child).
        internal static bool HeadPose(out Vector3 pos, out Quaternion rot)
        {
            Camera camera = VRGlobals.VRCam != null ? VRGlobals.VRCam : Camera.main;
            if (camera == null)
            {
                pos = Vector3.zero;
                rot = Quaternion.identity;
                return false;
            }
            var rig = VRGlobals.vrOffsetter;
            if (rig != null)
            {
                Transform rigTransform = rig.transform;
                pos = rigTransform.TransformPoint(camera.transform.localPosition);
                rot = rigTransform.rotation * camera.transform.localRotation;
            }
            else
            {
                pos = camera.transform.position;
                rot = camera.transform.rotation;
            }
            return true;
        }

        // Yaw-only head frame (nodding doesn't drag the chest around; projected-up fallback keeps yaw
        // sane looking straight up/down).
        internal static Quaternion HeadYaw(out Vector3 headPos)
        {
            HeadPose(out headPos, out Quaternion headRot);
            Vector3 forward = headRot * Vector3.forward;
            Vector3 flat = forward;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f)
            {
                flat = (headRot * Vector3.up) * Mathf.Sign(-forward.y);
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
