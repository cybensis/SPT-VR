using System.Collections.Generic;
using EFT.InventoryLogic;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.Interactions
{
    // The guns' body spots placed by hand (his design, 2026-11-06): a free hand on a gun at its spot (the chest sling, the shoulder, the
    // hip - the gun in hands too while it hangs there), the trigger held holdSeconds -> the gun comes off its spot and rides that
    // controller, position and angle -> letting go of the trigger leaves the slot's spot there, saved in ModSettings.json straight away.
    //
    // Per slot, not per gun: the grip point (the R-hand marker) and the Weapon_root's rotation in the body's frame (ChestHang.BodyYaw:
    // the neck and the body's yaw, the sling's own frame), so any gun in that slot puts its grip where you left one. ChestHang.SpotPose
    // draws a placed spot instead of the default, for the guns on the body (BodyWeapons) and the gun in hands hanging (ChestHang). The
    // shoulder and hip zones stop taking a placed slot's gun (BodyWeapons: the reach finds it where it is). "Reset Placed Gun Spots" in
    // the VR tab clears them. Not mirrored when the left-handed setting changes (a placed spot is where you put it).
    //
    // Free hand: the off hand off the foregrip and not on a reload part, the scope, a weapon part, loot or the breath hold; the gun hand
    // only with its gun slung or the hands empty (its trigger fires the gun in hand otherwise).
    public static class GunSpots
    {
        public static bool enabled = true;
        public static float holdSeconds = 1f;   // trigger held on the gun before it comes off its spot into the hand
        public static float holdSlack = 1.5f;   // the hold lasts while the hand stays within this much of grab reach (BodyWeapons' measure)
        public static bool debugLog = false;

        private static readonly EquipmentSlot[] slots = { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster };

        private sealed class Hand
        {
            public bool gunHand;
            public bool triggerDown;
            public bool holding;        // trigger held on a gun, counting up to holdSeconds
            public float heldFor;
            public EquipmentSlot slot;
            public Transform weaponRoot;
            public readonly List<Vector3> touchPoints = new List<Vector3>();
        }
        private static readonly Hand offHand = new Hand { gunHand = false };
        private static readonly Hand gunHand = new Hand { gunHand = true };

        // The placement under way: the gun rides placingHand's controller at the pose it had when it came off its spot
        private static Hand placingHand;
        private static EquipmentSlot placingSlot;
        private static Transform placingRoot;
        private static Vector3 placingGripLocal;
        private static Vector3 rootInHandPosition;
        private static Quaternion rootInHandRotation = Quaternion.identity;

        private static int lastTickFrame = -10;
        private static bool lastGunInLeftHand;

        private static bool Ticking => Time.frameCount - lastTickFrame <= 1;
        // A hold or a placement under way (the off hand's weapon-part selection yields)
        public static bool Busy => Ticking && (offHand.holding || gunHand.holding || placingHand != null);
        internal static bool IsPlacing(EquipmentSlot slot) => Ticking && placingHand != null && placingSlot == slot;
        // The slot's gun is somewhere else than its old zone (BodyWeapons' shoulder / hip zones leave it alone)
        internal static bool MovedOffZone(EquipmentSlot slot) => IsPlacing(slot) || (enabled && VRSettings.GetGunSpot(SlotIndex(slot), out _, out _));

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // From HandsInteractionController.Update (before its bail: also while supporting, menus excepted)
        public static void Tick()
        {
            // After a gap (a menu), or with the gun changed hands (the off / gun hand are the other controllers now): nothing carries
            // over, and a trigger already held isn't a fresh press
            bool resync = !Ticking || Handedness.GunInLeftHand != lastGunInLeftHand;
            lastTickFrame = Time.frameCount;
            lastGunInLeftHand = Handedness.GunInLeftHand;
            if (resync)
            {
                CancelAll();
                offHand.triggerDown = TriggerAxis(offHand) > 0.5f;
                gunHand.triggerDown = TriggerAxis(gunHand) > 0.5f;
            }
            bool offPressed = ReadTrigger(offHand);
            bool gunPressed = ReadTrigger(gunHand);
            if (!enabled || !Allowed())
            {
                CancelAll();
                return;
            }
            if (placingHand != null)
            {
                UpdatePlacing();
                return;
            }
            UpdateHold(offHand, offPressed, other: gunHand);
            UpdateHold(gunHand, gunPressed, other: offHand);
        }

        // Called by ChestHang.SpotPose for every gun it places: the spot being placed (on the hand), or the slot's placed spot
        internal static bool TryGetSpot(EquipmentSlot slot, Vector3 gripLocal, out Vector3 position, out Quaternion rotation)
        {
            if (IsPlacing(slot) && HandPose(placingHand, out Vector3 handPosition, out Quaternion handRotation))
            {
                rotation = handRotation * rootInHandRotation;
                position = handPosition + handRotation * rootInHandPosition;
                return true;
            }
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!enabled || !VRSettings.GetGunSpot(SlotIndex(slot), out Vector3 grip, out Quaternion spotRotation))
                return false;
            Quaternion body = ChestHang.BodyYaw(out Vector3 neck);
            rotation = body * spotRotation;
            position = neck + body * grip - rotation * gripLocal;
            return true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void UpdateHold(Hand hand, bool pressed, Hand other)
        {
            if (!hand.holding)
            {
                // A fresh press on a gun at its spot, the other hand not already at it
                if (!pressed || other.holding || !HandFree(hand) || !NearestSpotGun(hand, out EquipmentSlot slot, out Transform weaponRoot, out _))
                    return;
                hand.holding = true;
                hand.heldFor = 0f;
                hand.slot = slot;
                hand.weaponRoot = weaponRoot;
                Haptic(hand, 0.15f, 0.04f);
                return;
            }
            // Let go, gone off the gun, or the hand taken by something else: no placement
            if (!hand.triggerDown || !HandFree(hand) || ReachOf(hand, hand.slot, hand.weaponRoot) > holdSlack)
            {
                hand.holding = false;
                return;
            }
            hand.heldFor += Time.deltaTime;
            if (hand.heldFor >= holdSeconds)
                BeginPlacing(hand);
        }

        private static void BeginPlacing(Hand hand)
        {
            hand.holding = false;
            if (!HandPose(hand, out Vector3 handPosition, out Quaternion handRotation)
                || !SpotGun(hand.slot, out Transform weaponRoot, out _, out Vector3 gripLocal, out _) || weaponRoot != hand.weaponRoot)
                return;
            Quaternion toHand = Quaternion.Inverse(handRotation);
            rootInHandRotation = toHand * weaponRoot.rotation;
            rootInHandPosition = toHand * (weaponRoot.position - handPosition);
            placingGripLocal = gripLocal;
            placingHand = hand;
            placingSlot = hand.slot;
            placingRoot = weaponRoot;
            Haptic(hand, 0.6f, 0.08f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GunSpots] placing the {placingSlot} spot with the {(hand.gunHand ? "gun" : "off")} hand");
        }

        private static void UpdatePlacing()
        {
            // The gun left its spot meanwhile (taken into hands, a switch): nothing to place
            if (!SpotGun(placingSlot, out Transform weaponRoot, out _, out _, out _) || weaponRoot != placingRoot)
            {
                CancelAll();
                return;
            }
            if (!placingHand.triggerDown)
                FinishPlacing();
        }

        private static void FinishPlacing()
        {
            Hand hand = placingHand;
            placingHand = null;
            if (!HandPose(hand, out Vector3 handPosition, out Quaternion handRotation))
                return;
            Quaternion rootRotation = handRotation * rootInHandRotation;
            Vector3 rootPosition = handPosition + handRotation * rootInHandPosition;
            Quaternion body = ChestHang.BodyYaw(out Vector3 neck);
            Quaternion toBody = Quaternion.Inverse(body);
            Vector3 grip = toBody * (rootPosition + rootRotation * placingGripLocal - neck);
            VRSettings.SetGunSpot(SlotIndex(placingSlot), grip, toBody * rootRotation);
            Haptic(hand, 0.45f, 0.06f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GunSpots] {placingSlot} spot placed: grip {grip:F3} from the neck (body frame)");
        }

        private static void CancelAll()
        {
            offHand.holding = false;
            gunHand.holding = false;
            placingHand = null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static bool Allowed()
        {
            return VRGlobals.inGame && !VRGlobals.menuOpen && VRGlobals.vrPlayer != null && VRGlobals.player != null && VRGlobals.vrOffsetter != null
                && VRGlobals.handsInteractionController != null && !VRGlobals.usingItem && !EatingInteractionController.ManualActive
                && !WeaponPatches.grenadeEquipped && !BodyWeapons.Switching;
        }

        private static bool HandFree(Hand hand)
        {
            var hands = VRGlobals.handsInteractionController;
            if (hands.heldItem != null)
                return false;
            if (hand.gunHand)
                return (ChestHang.Hanging || VRGlobals.player.HandsIsEmpty) && !VRReloadController.GunHandHolds;
            // Holding, not OwnsOffHand: its zones are grip zones, and the chest pouch's overlaps the chest sling
            GunInteractionController parts = WeaponPatches.currentGunInteractController;
            return !VRGlobals.vrPlayer.isSupporting && !ChestHang.SupportOnly && !VRReloadController.OffHandHolds
                && !VRReloadController.OffHandAtChargingHandle && !VRReloadController.OffHandAtBolt && hands.scopeTransform == null
                && !InputHandlers.BreathingHandler.isHoldingBreath && (parts == null || parts.lastHitCompIndex == -1);
        }

        // The gun at a slot's spot: the one on the body, or the gun in hands hanging there
        private static bool SpotGun(EquipmentSlot slot, out Transform weaponRoot, out Transform meshRoot, out Vector3 gripLocal, out Vector3 grabPoint)
        {
            if (ChestHang.HangingGun(out EFT.Player.FirearmController hanging, out EquipmentSlot hangingSlot, out gripLocal, out grabPoint) && hangingSlot == slot)
            {
                weaponRoot = hanging.WeaponRoot;
                meshRoot = hanging.WeaponRoot;
                return true;
            }
            if (BodyWeapons.DrawnBodyGun(slot, out GameObject model, out weaponRoot, out gripLocal, out grabPoint))
            {
                meshRoot = model.transform;
                return true;
            }
            weaponRoot = null;
            meshRoot = null;
            return false;
        }

        private static bool NearestSpotGun(Hand hand, out EquipmentSlot nearestSlot, out Transform nearestRoot, out float nearest)
        {
            nearestSlot = EquipmentSlot.FirstPrimaryWeapon;
            nearestRoot = null;
            nearest = 1f;
            TouchPoints(hand);
            foreach (EquipmentSlot slot in slots)
            {
                if (!SpotGun(slot, out Transform weaponRoot, out Transform meshRoot, out _, out Vector3 grabPoint))
                    continue;
                float score = BodyWeapons.HandReach(weaponRoot, meshRoot, grabPoint, hand.touchPoints);
                if (score < nearest)
                {
                    nearest = score;
                    nearestSlot = slot;
                    nearestRoot = weaponRoot;
                }
            }
            return nearestRoot != null;
        }

        // How near the hand is to that gun, still at that spot (MaxValue when it's gone)
        private static float ReachOf(Hand hand, EquipmentSlot slot, Transform expectedRoot)
        {
            if (!SpotGun(slot, out Transform weaponRoot, out Transform meshRoot, out _, out Vector3 grabPoint) || weaponRoot != expectedRoot)
                return float.MaxValue;
            TouchPoints(hand);
            return BodyWeapons.HandReach(weaponRoot, meshRoot, grabPoint, hand.touchPoints);
        }

        // The rendered hand's palm, knuckles and fingertips: the arm that draws it (the off hand on the right arm while the gun is in the left hand)
        private static void TouchPoints(Hand hand)
        {
            bool rightArm = hand.gunHand != Handedness.GunInLeftHand;
            VRGlobals.handsInteractionController.GetHandTouchPoints(rightArm, hand.touchPoints);
        }

        // The controller's pose in the world: its tracking pose through the rig, the same frame the rig's hands are placed in
        private static bool HandPose(Hand hand, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (VRGlobals.vrOffsetter == null)
                return false;
            VRPose pose = hand.gunHand ? Handedness.GunHandPose : Handedness.OffHandPose;
            Transform rig = VRGlobals.vrOffsetter.transform;
            position = rig.TransformPoint(pose.localPosition);
            rotation = rig.rotation * pose.localRotation;
            return true;
        }

        private static float TriggerAxis(Hand hand)
        {
            return (hand.gunHand ? Handedness.GunHandTrigger : Handedness.OffHandTrigger).axis;
        }

        // The trigger with the mod's usual 0.5 / 0.35 hysteresis; true on the press
        private static bool ReadTrigger(Hand hand)
        {
            float axis = TriggerAxis(hand);
            bool down = hand.triggerDown ? axis > 0.35f : axis > 0.5f;
            bool pressed = down && !hand.triggerDown;
            hand.triggerDown = down;
            return pressed;
        }

        private static void Haptic(Hand hand, float amplitude, float duration)
        {
            SteamVR_Input_Sources source = hand.gunHand ? Handedness.GunHandSource : Handedness.OffHandSource;
            VRInput.Haptic.Execute(0f, duration, 95f, amplitude, source);
        }

        private static int SlotIndex(EquipmentSlot slot)
        {
            switch (slot)
            {
                case EquipmentSlot.FirstPrimaryWeapon: return 0;
                case EquipmentSlot.SecondPrimaryWeapon: return 1;
                case EquipmentSlot.Holster: return 2;
                default: return -1;
            }
        }
    }
}
