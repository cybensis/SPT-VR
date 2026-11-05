using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using TarkovVR.Patches.Core.Player;
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
        // The second primary's spot (BodyWeapons): over the dominant shoulder, hanging down the back. Where its Weapon_root sits,
        // headset-local and yaw-only like the sling; x mirrors left-handed
        public static Vector3 backOffset = new Vector3(0.17f, -0.17f, -0.17f);
        public static float backAcross = 0.3f;          // the muzzle points across the back toward the off side (0 = straight down)
        public static float backOut = -0.25f;           // and tips away behind the body
        public static float backRollDeg = 0f;           // fine roll about the barrel
        public static bool hangHaptics = true;
        public static bool debugLog = false;            // 1/sec chain dump while detached + save/restore lines (for the prone-corrupts-regrab hunt)
        private static float nextDebugLog;
        public static bool gripSound = true;            // every grab (foregrip / grip retake / off the sling): cloth + a hint of gun
        public static float gripClothVolume = 0.85f;    // the gear (rig/backpack) pickup foley = the cloth layer
        public static float gripGunVolume = 0.18f;      // the weapon's own pickup clip, quiet, underneath

        public static bool Hanging { get; private set; }
        public static bool SupportOnly { get; private set; }   // gun held by the support hand alone
        public static bool Detached => Hanging || SupportOnly; // fire gate
        public static bool HandInRegrabZone { get; private set; } // dominant palm within regrab reach of the detached gun (loot pointer yields to the regrab)

        private static bool gripSeen;                   // welded like vanilla until the first grip press on this controller
        private static int ignoredPressFrame = -1;      // a grip press BodyWeapons took (a gun off the body), not a regrab
        private static Item gripSeenFor;                // taken off the body with the grip closed: grip-bound from the moment it's in hands
        private static bool prevGrip;
        private static bool prevSupporting;             // for the foregrip-grab foley edge
        private static float releaseTimer;
        private static int lastTickFrame = -10;
        private static object lastHandsController;
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
            if (!enabled || !(VRGlobals.vrPlayer is RaidVRPlayerManager || VRGlobals.vrPlayer is HideoutVRPlayerManager))
            {
                gripSeen = false;
                releaseTimer = 0f;
                Reattach(restore: false);
                return;
            }

            var player = VRGlobals.player;
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
            }

            if (!holdingFirearm)
            {
                releaseTimer = 0f;
                Reattach(restore: false);
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

            if (Detached)
            {
                KeepGunHandFree();
                DebugChainDump();
            }

            if (SupportOnly)
            {
                bool near = HandNearGun(fc);
                HandInRegrabZone = near;
                RegrabZonePulse(near);
                if (!supporting)
                {
                    // support hand let go too - both hands off -> straight to the sling (no flash of
                    // the marker anchor in between)
                    SupportOnly = false;
                    BeginHang(fc);
                }
                else if (pressed && near && !VRReloadController.HandleInGunHand)
                {
                    // dominant hand takes the grip back -> normal (two-handed) hold. Reattach clears
                    // SupportOnly itself - clearing it HERE first made wasDetached/fromSupport read
                    // false inside = no retake sound/haptic (headset-confirmed silent).
                    Reattach(restore: true);
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
                prevGrip = grip;
                return;
            }

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
                    if (supporting)
                        BeginSupportHold(fc);   // left hand still on it - hand the gun to the support hand
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

        private static void BeginHang(EFT.Player.FirearmController fc)
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

            // fly-in starts from the release pose, captured in the yaw frame
            Quaternion yawRot = HeadYaw(out Vector3 headPos);
            slingLocalPos = Quaternion.Inverse(yawRot) * (wr.position - headPos);
            slingLocalRot = Quaternion.Inverse(yawRot) * wr.rotation;
            posVel = Vector3.zero;

            DetachCommon();
            Hanging = true;
            if (hangHaptics)
                Pulse(0.6f);
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

            DetachCommon();
            SupportOnly = true;
            if (hangHaptics)
                Pulse(0.35f);
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
            var arm = VRGlobals.ikManager != null ? VRGlobals.ikManager.rightArmIk : null;
            var rightHand = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.RightHand : null;
            var player = VRGlobals.player;
            if (arm == null || rightHand == null || player == null)
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
                Pulse(0.45f);
            // Same cloth-grab foley for every grab - off the sling included ("it just sounds better"
            // than the full weapon pickup clatter, headset-picked).
            if (wasDetached)
                PlayGripSound();
        }

        // "Cloth grab with a hint of gun" (headset spec) for hands-going-onto-the-held-gun: the generic
        // gear (rig/backpack) pickup foley = the cloth, with the weapon's own pickup clip quiet under
        // it. Used for the foregrip grab (VRPlayerManager, isSupporting engage) and the grip retake
        // while supported. Public so VRPlayerManager can fire it.
        private static bool loggedGripClips;
        public static void PlayGripSound()
        {
            var fc = VRGlobals.firearmController;
            if (!gripSound || fc == null || fc.WeaponRoot == null)
                return;
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

            if (!loggedGripClips)
            {
                loggedGripClips = true;
                Plugin.MyLog.LogInfo($"[ChestHang] grip sound clips: cloth={(cloth != null ? clothSrc : "NONE")} gun={(gun != null ? item.ItemSound : "NONE")}");
            }
            if (cloth != null)
                ba.TryPlayAtPoint(out _, pos, cloth, BetterAudio.AudioSourceGroupType.Character, 25, gripClothVolume);
            if (gun != null)
                ba.TryPlayAtPoint(out _, pos, gun, BetterAudio.AudioSourceGroupType.Character, 25, cloth != null ? gripGunVolume : gripClothVolume);
        }

        private static void SaveHolderOnce()
        {
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
        private static bool HandNearGun(EFT.Player.FirearmController fc)
        {
            return RegrabDistance(fc) < regrabRadius;
        }

        internal static float RegrabDistance()
        {
            return VRGlobals.firearmController != null ? RegrabDistance(VRGlobals.firearmController) : float.MaxValue;
        }

        private static float RegrabDistance(EFT.Player.FirearmController fc)
        {
            var hand = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.RightHand : null;
            if (hand == null)
                return float.MaxValue;
            var links = fc.HandsHierarchy;
            Transform anchor = links != null && links.Transforms != null && links.Transforms.Length > 8
                ? links.Transforms[8] : null;
            if (anchor == null)
                anchor = fc.WeaponRoot;   // marker missing on this weapon - the old behavior beats no regrab
            if (anchor == null)
                return float.MaxValue;
            Vector3 anchorPos = anchor.position;
            if (fc.WeaponRoot != null)
                anchorPos += -fc.WeaponRoot.up * regrabForwardOffset;   // WeaponRoot -up = toward the muzzle
            return Vector3.Distance(hand.transform.position, anchorPos);
        }

        // BodyWeapons took this frame's grip press for a gun off the body: not a regrab of the one hanging
        internal static void IgnoreGripPress()
        {
            ignoredPressFrame = Time.frameCount;
        }

        internal static void TakenFromBody(Item item)
        {
            gripSeenFor = item;
        }

        // Where a gun lives on the body when it isn't in your hand, as its Weapon_root's pose: BodyWeapons draws the others there,
        // and the gun in hand goes there when you let go. The first primary on the chest sling, the second over the dominant
        // shoulder, the pistol on Tarkov's holster bone (Tarkov sets a body model's Weapon_root on its bone). False = no spot.
        internal static bool SpotPose(EquipmentSlot slot, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!HeadPose(out _, out _))
                return false;
            bool leftHanded = VRSettings.GetLeftHandedMode();
            Quaternion yawRot = HeadYaw(out Vector3 headPos);
            switch (slot)
            {
                case EquipmentSlot.FirstPrimaryWeapon:
                    Vector3 chest = chestOffset;
                    if (leftHanded)
                        chest.x = -chest.x;
                    position = headPos + yawRot * chest;
                    rotation = yawRot * CanonicalSlingRot(leftHanded);
                    return true;
                case EquipmentSlot.SecondPrimaryWeapon:
                    Vector3 back = backOffset;
                    if (leftHanded)
                        back.x = -back.x;
                    position = headPos + yawRot * back;
                    rotation = yawRot * BackRot(leftHanded);
                    return true;
                case EquipmentSlot.Holster:
                    Transform bone = BodyWeapons.HolsterBone();
                    if (bone == null)
                        return false;
                    position = bone.position;
                    rotation = bone.rotation;
                    return true;
            }
            return false;
        }

        // Muzzle down the back and across toward the off side, the gun's face out behind you, the grip end up by the shoulder
        private static Quaternion BackRot(bool leftHanded)
        {
            float side = leftHanded ? 1f : -1f;
            Vector3 muzzle = new Vector3(side * backAcross, -1f, backOut).normalized;
            Vector3 butt = -muzzle;                 // WeaponRoot +up
            Vector3 face = Vector3.back - Vector3.Dot(Vector3.back, butt) * butt;
            face = face.sqrMagnitude < 1e-4f ? Vector3.up : face.normalized;
            return Quaternion.AngleAxis(backRollDeg, muzzle) * Quaternion.LookRotation(face, butt);
        }

        // The spot of the gun in hand: its slot's, or the chest sling's for anything else (or with BodyWeapons off)
        private static bool HandsGunSpotPose(EFT.Player.FirearmController fc, out Vector3 position, out Quaternion rotation)
        {
            if (BodyWeapons.enabled && BodyWeapons.SlotOf(fc.Item, out EquipmentSlot slot) && SpotPose(slot, out position, out rotation))
                return true;
            return SpotPose(EquipmentSlot.FirstPrimaryWeapon, out position, out rotation);
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

        public static void DriveHangPose()
        {
            var fc = VRGlobals.firearmController;
            if (fc == null || fc.WeaponRoot == null || VRGlobals.weaponHolder == null || !HeadPose(out _, out _))
                return;

            float dt = Mathf.Clamp(Time.deltaTime, 1e-4f, 0.05f);
            Quaternion yawRot = HeadYaw(out Vector3 headPos);
            // Its own spot (the chest sling for the first primary, the shoulder for the second, the holster for the pistol), in the
            // yaw-local frame the tuck-in runs in
            if (!HandsGunSpotPose(fc, out Vector3 spotPos, out Quaternion spotRot))
                return;
            Vector3 off = Quaternion.Inverse(yawRot) * (spotPos - headPos);
            Quaternion spotLocalRot = Quaternion.Inverse(yawRot) * spotRot;

            // tuck-in blend runs in the yaw-local frame (turning carries the gun 1:1 - world-space
            // blending was the "gun spins while I turn" bug), then the mount is rigid: no dangle.
            slingLocalPos = Vector3.SmoothDamp(slingLocalPos, off, ref posVel, attachTime);
            slingLocalRot = Quaternion.Slerp(slingLocalRot, spotLocalRot, Mathf.Min(1f, rotAttachRate * dt));

            StampWeaponRoot(headPos + yawRot * slingLocalPos, yawRot * slingLocalRot);
        }

        public static void DriveSupportPose()
        {
            var fc = VRGlobals.firearmController;
            var lhObj = VRGlobals.vrPlayer != null ? VRGlobals.vrPlayer.LeftHand : null;
            if (fc == null || fc.WeaponRoot == null || VRGlobals.weaponHolder == null || lhObj == null)
                return;
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
        }

        // The support hand's LIVE controller rotation in world space: the raw pose action's local
        // rotation lifted through LeftHand's parent (the rig - LeftHand is a child of vrOffsetter and
        // its localRotation comes from this same action when it IS updated). LeftHand.transform itself
        // is rotation-frozen while supporting (UpdateLeftHand only writes position then).
        private static Quaternion SupportControllerRot(Transform lh)
        {
            var pose = VRSettings.GetLeftHandedMode()
                ? VRInput.RightHandPose : VRInput.LeftHandPose;
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
            if (!Detached || stampedFrame != Time.frameCount || VRGlobals.firearmController == null
                || VRGlobals.firearmController.WeaponRoot == null || VRGlobals.weaponHolder == null)
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

        internal static void Pulse(float amp)
        {
            SteamVR_Input_Sources gunHand = VRSettings.GetLeftHandedMode()
                ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
            VRInput.Haptic.Execute(0f, 0.07f, 95f, amp, gunHand);
        }
    }
}
