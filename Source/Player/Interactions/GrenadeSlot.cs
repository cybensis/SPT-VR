using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.InputSystem;
using EFT.InventoryLogic;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Player.Interactions
{
    // The grenade on the chest (his grenade plan, round 2): the grenade Tarkov would throw next shown on the top left of the chest (its own
    // model, in the body's frame like the guns' spots), taken by either hand with its hold button (VR tab: grip, or the trigger). Which one:
    // Tarkov's own pick for its throw key (PlayerInputTranslator.vmethod_1) - the quick-access panel's grenade (TopPriorityGrenade), else a
    // random reachable one, not the type thrown last. That pick is random when the key is pressed, so it's made here once, shown, and the
    // grab takes that very one (SetItemInHands, as the key does); a new one after it's gone.
    // The grab is Tarkov's own switch with the gun's put-away and the grenade's draw skipped (BodyWeapons' way), the model riding the hand
    // meanwhile. The left hand holds it the way it holds a gun (Handedness.GunInLeftHand: the right-handed hold mirrored,
    // RealLeftHandedPatches for the arms). In the hand, GrenadeHold.
    // Moving the slot: the trigger held 1 s at it with the hand still (holding with the trigger: moving the hand away takes it instead), it
    // rides the hand until the trigger opens, saved like a gun spot.
    public static class GrenadeSlot
    {
        public static bool enabled = true;
        public static Vector3 slotFromNeck = new Vector3(-0.11f, -0.20f, 0.13f);    // m, body frame (x right, y up, z forward): top left of the chest
        public static Vector3 slotEuler = Vector3.zero;                             // the model's turn there, body frame
        public static float grabRadius = 0.08f;     // m any point of the hand (palm, knuckles, fingertips) to the grenade's middle
        public static float holdSeconds = 1f;       // trigger held at the slot with the hand still: moving the slot
        public static float stillRadius = 0.04f;    // m the hand may drift during that hold (trigger holding: farther = taken)
        public static float switchTimeout = 3f;     // s before a grab whose switch never lands is given up
        public static bool debugLog = true;         // one line per grab / pick / placement (first round)

        private sealed class Hand
        {
            public bool left;
            public bool gripDown, triggerDown;
            public bool pending;            // holding with the trigger: pressed at the slot, not yet taken or moving it
            public float pendingSince;
            public Vector3 pendingAt;
            public float triggerHeldAtSlot; // seconds, for moving the slot
            public readonly List<Vector3> touchPoints = new List<Vector3>();
        }
        private static readonly Hand leftHand = new Hand { left = true };
        private static readonly Hand rightHand = new Hand { left = false };

        // The pick
        private static ThrowWeap picked;
        private static float nextPickTime;
        private static MongoID? lastThrownTemplate;
        private static readonly List<ThrowWeap> reachable = new List<ThrowWeap>(10);

        // Its model
        private static GameObject model;
        private static ThrowWeap modelFor;
        private static readonly List<Collider> modelColliders = new List<Collider>();
        private static readonly Dictionary<Transform, int> modelLayers = new Dictionary<Transform, int>();

        // A grab under way
        private static ThrowWeap switchingTo;
        private static float switchStarted;
        private static Hand switchHand;
        private static Vector3 inHandPosition;
        private static Quaternion inHandRotation = Quaternion.identity;
        private static Item handsBefore;
        private static EquipmentSlot slotBefore;
        private static bool slotBeforeKnown;

        // The last grab: which grenade, and with which hand
        private static Item grabbedItem;
        private static bool grabbedLeft;
        private static bool grabbedByHold;

        // Moving the slot
        private static Hand placing;
        private static Vector3 placingPosition;
        private static Quaternion placingRotation = Quaternion.identity;

        private static bool hooked;

        // This frame's press was a grenade grab (BodyWeapons / ChestHang leave it alone)
        public static bool TookPress { get; private set; }
        // The off hand at the grenade (the chest pouch's magazine grab yields to it; read a frame late by VRReloadController)
        public static bool OffHandAtSlot { get; private set; }
        public static bool Busy => placing != null || switchingTo != null || leftHand.pending || rightHand.pending;

        // A grenade the left hand took is held left: ChestHang leaves the left-hand hold on while it's in the hands
        public static bool KeepsLeftHand => grabbedLeft && GrabbedInHands;

        private static bool GrabbedInHands => grabbedItem != null && VRGlobals.player != null
            && VRGlobals.player.HandsController is EFT.Player.BaseGrenadeHandsController grenade && grenade.Item == grabbedItem;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // From HandsInteractionController.Update, before BodyWeapons / ChestHang (a grab here takes the press from them)

        public static void UpdateInput()
        {
            TookPress = false;
            if (!hooked)
            {
                hooked = true;
                Application.onBeforeRender += OnBeforeRender;
            }
            if (grabbedItem != null && !GrabbedInHands && !IsSwitching())
            {
                grabbedItem = null;
                grabbedLeft = false;
                grabbedByHold = false;
            }
            if (!Allowed())
            {
                OffHandAtSlot = false;
                CancelInput();
                return;
            }
            RefreshPick();
            if (switchingTo != null)
            {
                OffHandAtSlot = false;
                UpdateSwitch();
                return;
            }
            if (placing != null)
            {
                OffHandAtSlot = false;
                UpdatePlacing();
                return;
            }
            bool modelShown = model != null && model.activeSelf && modelFor == picked && picked != null;
            OffHandAtSlot = false;
            foreach (Hand hand in new[] { leftHand, rightHand })
            {
                bool gripPressed = ReadGrip(hand);
                bool triggerPressed = ReadTrigger(hand);
                bool atSlot = modelShown && ReachOf(hand) < 1f;
                if (atSlot && !IsGunHand(hand))
                    OffHandAtSlot = true;
                if (!atSlot || !HandFree(hand))
                {
                    hand.pending = false;
                    hand.triggerHeldAtSlot = 0f;
                    continue;
                }
                if (VRSettings.GetGrenadeHoldWithTrigger())
                {
                    // The trigger holds it: pressed here, moving the hand away takes it, keeping it still moves the slot
                    if (triggerPressed)
                    {
                        hand.pending = true;
                        hand.pendingSince = Time.time;
                        hand.pendingAt = ControllerPosition(hand);
                    }
                    if (!hand.pending)
                        continue;
                    if (!hand.triggerDown)
                    {
                        hand.pending = false;
                        continue;
                    }
                    if (Vector3.Distance(ControllerPosition(hand), hand.pendingAt) > stillRadius)
                    {
                        hand.pending = false;
                        Grab(hand);
                        return;
                    }
                    if (Time.time - hand.pendingSince >= holdSeconds)
                    {
                        hand.pending = false;
                        BeginPlacing(hand);
                        return;
                    }
                }
                else
                {
                    // The grip holds it; the trigger held still at it moves the slot (as the guns' spots)
                    if (gripPressed)
                    {
                        Grab(hand);
                        return;
                    }
                    hand.triggerHeldAtSlot = hand.triggerDown ? hand.triggerHeldAtSlot + Time.deltaTime : 0f;
                    if (hand.triggerHeldAtSlot >= holdSeconds)
                    {
                        hand.triggerHeldAtSlot = 0f;
                        BeginPlacing(hand);
                        return;
                    }
                }
            }
        }

        private static bool Allowed()
        {
            return enabled && VRGlobals.inGame && !VRGlobals.menuOpen && VRGlobals.player != null && VRGlobals.player.IsYourPlayer && VRGlobals.vrPlayer != null
                && VRGlobals.vrOffsetter != null && VRGlobals.handsInteractionController != null && !VRGlobals.usingItem
                && !EatingInteractionController.ManualActive && VRGlobals.player.InventoryController != null;
        }

        private static void CancelInput()
        {
            leftHand.pending = rightHand.pending = false;
            leftHand.triggerHeldAtSlot = rightHand.triggerHeldAtSlot = 0f;
            placing = null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Which grenade

        private static void RefreshPick()
        {
            if (Time.time < nextPickTime && (picked == null || picked.Parent != null))
                return;
            nextPickTime = Time.time + 0.5f;
            InventoryController inventory = VRGlobals.player.InventoryController;
            reachable.Clear();
            inventory.GetReachableItemsOfTypeNonAlloc(reachable, (ThrowWeap grenade) => inventory.Examined(grenade));
            Item inHands = HandsItem();
            reachable.RemoveAll(grenade => grenade == inHands);
            ThrowWeap before = picked;
            if (reachable.Count == 0)
            {
                picked = null;
            }
            else
            {
                ThrowWeap top = inventory.Inventory.Equipment.TopPriorityGrenade;
                if (top != null && reachable.Contains(top))
                    picked = top;
                else if (picked == null || !reachable.Contains(picked))
                    picked = RandomPick();
            }
            reachable.Clear();
            if (debugLog && picked != before)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] showing {(picked != null ? picked.LocalizedShortName() : "nothing")}");
        }

        // Tarkov's: a random one, up to three tries at a type other than the one thrown last
        private static ThrowWeap RandomPick()
        {
            ThrowWeap choice = reachable[UnityEngine.Random.Range(0, reachable.Count)];
            for (int tries = 0; tries < 2 && reachable.Count > 1 && lastThrownTemplate.HasValue && choice.TemplateId == lastThrownTemplate.Value; tries++)
                choice = reachable[UnityEngine.Random.Range(0, reachable.Count)];
            return choice;
        }

        // GrenadeHold: one thrown, the next pick avoids its type (Tarkov's _prevGrenadeId)
        public static void NoteThrown(Item item)
        {
            if (item != null)
                lastThrownTemplate = item.TemplateId;
            if (item == picked)
                picked = null;
            nextPickTime = 0f;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Taking it

        private static void Grab(Hand hand)
        {
            string whyNot = WhyNotSwitch(hand);
            if (whyNot != null)
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[GrenadeSlot] can't take {picked?.LocalizedShortName()}: {whyNot}");
                return;
            }
            handsBefore = HandsItem();
            slotBeforeKnown = handsBefore != null && BodyWeapons.SlotOf(handsBefore, out slotBefore);
            switchingTo = picked;
            switchStarted = Time.time;
            switchHand = hand;
            grabbedItem = picked;
            grabbedLeft = hand.left;
            grabbedByHold = true;
            ControllerPose(hand, out Vector3 handPosition, out Quaternion handRotation);
            inHandRotation = Quaternion.Inverse(handRotation) * model.transform.rotation;
            inHandPosition = Quaternion.Inverse(handRotation) * (model.transform.position - handPosition);
            TookPress = true;
            if (IsGunHand(hand))
                ChestHang.IgnoreGripPress();
            Haptic(hand, 0.35f, 0.04f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] {(hand.left ? "left" : "right")} hand takes {picked.LocalizedShortName()}, in hands before: {handsBefore?.LocalizedShortName() ?? "nothing"}");
            VRGlobals.player.SetItemInHands(picked, delegate { });
        }

        private static string WhyNotSwitch(Hand hand)
        {
            if (WeaponPatches.grenadeEquipped) return "a grenade in hand already";
            if (VRReloadController.HoldingMagazine || VRReloadController.HoldingChargingHandle) return "the reload has a hand";
            if (VRGlobals.player.InventoryController.IsChangingWeapon) return "switching weapons";
            object hands = VRGlobals.player.HandsController;
            if (hands is FirearmController firearmController && !(firearmController.CurrentOperation is FirearmController.Idling))
                return $"the gun is busy ({firearmController.CurrentOperation?.GetType().Name})";
            if (!(hands is FirearmController) && !(hands is EFT.Player.EmptyHandsController) && !(hands is EFT.Player.KnifeController))
                return $"hands hold {hands?.GetType().Name ?? "nothing"}";
            return null;
        }

        private static bool IsSwitching() => switchingTo != null;

        private static void UpdateSwitch()
        {
            EFT.Player player = VRGlobals.player;
            if (Time.time - switchStarted > switchTimeout)
            {
                Plugin.MyLog.LogWarning($"[GrenadeSlot] taking {switchingTo.LocalizedShortName()} never landed");
                EndSwitch();
                return;
            }
            // The gun going away: Tarkov's own skip of its put-away (BodyWeapons' fast switch)
            if (player.HandsController is FirearmController firearmController && firearmController.IsInRemoveOperation())
                WeaponPatches.SkipDrawOrPutAway(firearmController);
            if (player.HandsController is EFT.Player.BaseGrenadeHandsController grenade && grenade.Item == switchingTo)
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[GrenadeSlot] {switchingTo.LocalizedShortName()} in hand after {Time.time - switchStarted:0.00} s");
                ChestHang.PlayGripSound();
                Haptic(switchHand, 0.45f, 0.05f);
                EndSwitch();
            }
        }

        private static void EndSwitch()
        {
            switchingTo = null;
            switchHand = null;
        }

        // GrenadeHandsController.Spawn (VRGrenadeController): the grenade a grab asked for, its draw just started. Skipped (its own
        // FastForward = the draw's end), its clip played through; in the left hand if the left took it.
        public static void OnGrenadeSpawned(EFT.Player.GrenadeHandsController grenade)
        {
            try
            {
                if (grenade?._player == null || !grenade._player.IsYourPlayer || switchingTo == null || grenade.Item != switchingTo)
                    return;
                if (grenade.CurrentOperation is EFT.Player.GrenadeHandsController.SpawnOperation spawn)
                    spawn.FastForward();
                GrenadeHold.Rush(grenade, GrenadeHold.drawRushTime);
                if (grabbedLeft)
                    Handedness.SetGunInLeftHand(true);
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[GrenadeSlot] {grenade.Item.LocalizedShortName()}'s draw skipped (+{Time.time - switchStarted:0.000} s), {(grabbedLeft ? "left" : "right")} hand");
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[GrenadeSlot] taking the grenade into the hand failed: {exception}");
            }
        }

        // GrenadeHold: is this grenade held by the button that took it (so letting that go puts it back)
        public static bool TakenByHold(Item item)
        {
            return grabbedByHold && item != null && item == grabbedItem;
        }

        // GrenadeHold, let go before the pin was out: the hands back as they were (the grenade's on the slot again: it's in the bag)
        public static void ReturnHands(EFT.Player.GrenadeHandsController grenade)
        {
            // Taken here: what the hands held before. Drawn another way (the radial menu): Tarkov's own last weapon.
            bool fromHere = grenade != null && grenade.Item == grabbedItem;
            Item back = fromHere ? handsBefore : VRGlobals.player.LastEquippedWeaponOrKnifeItem;
            if (back != null && back.Parent != null && BodyWeapons.SlotOf(back, out EquipmentSlot slot))
                VRInputManager.ForceCommand(SelectCommand(slot));
            else
                VRGlobals.player.SetEmptyHands(delegate { });
            grabbedItem = null;
            grabbedByHold = false;
        }

        private static ECommand SelectCommand(EquipmentSlot slot)
        {
            switch (slot)
            {
                case EquipmentSlot.SecondPrimaryWeapon: return ECommand.SelectSecondPrimaryWeapon;
                case EquipmentSlot.Holster: return ECommand.SelectSecondaryWeapon;
                case EquipmentSlot.Scabbard: return ECommand.SelectKnife;
                default: return ECommand.SelectFirstPrimaryWeapon;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Moving the slot

        private static void BeginPlacing(Hand hand)
        {
            if (model == null)
                return;
            ControllerPose(hand, out Vector3 handPosition, out Quaternion handRotation);
            placingRotation = Quaternion.Inverse(handRotation) * model.transform.rotation;
            placingPosition = Quaternion.Inverse(handRotation) * (model.transform.position - handPosition);
            placing = hand;
            Haptic(hand, 0.6f, 0.08f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] moving the slot with the {(hand.left ? "left" : "right")} hand");
        }

        private static void UpdatePlacing()
        {
            ReadTrigger(placing);
            if (placing.triggerDown)
                return;
            Hand hand = placing;
            placing = null;
            ControllerPose(hand, out Vector3 handPosition, out Quaternion handRotation);
            Quaternion rotation = handRotation * placingRotation;
            Vector3 position = handPosition + handRotation * placingPosition;
            Quaternion body = ChestHang.BodyYaw(out Vector3 neck);
            Quaternion toBody = Quaternion.Inverse(body);
            VRSettings.SetGrenadeSlot(toBody * (position - neck), toBody * rotation);
            Haptic(hand, 0.45f, 0.06f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] slot placed at {(toBody * (position - neck)).ToString("F3")} from the neck (body frame)");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Drawing it

        // After every LateUpdate: where the grenade is (BodyYaw read live, as the guns on the body)
        private static void OnBeforeRender()
        {
            try
            {
                ThrowWeap shown = Allowed() ? (switchingTo ?? picked) : null;
                // In the hands already (not a switch's stand-in): Tarkov's own model there
                if (shown != null && switchingTo == null && HandsItem() == shown)
                    shown = null;
                if (shown == null)
                {
                    if (model != null && model.activeSelf)
                        model.SetActive(false);
                    return;
                }
                if (modelFor != shown || model == null)
                    BuildModel(shown);
                if (model == null)
                    return;
                if (!model.activeSelf)
                    model.SetActive(true);
                if (switchingTo != null && switchHand != null)
                {
                    ControllerPose(switchHand, out Vector3 handPosition, out Quaternion handRotation);
                    model.transform.SetPositionAndRotation(handPosition + handRotation * inHandPosition, handRotation * inHandRotation);
                }
                else if (placing != null)
                {
                    ControllerPose(placing, out Vector3 handPosition, out Quaternion handRotation);
                    model.transform.SetPositionAndRotation(handPosition + handRotation * placingPosition, handRotation * placingRotation);
                }
                else
                {
                    SlotPose(out Vector3 position, out Quaternion rotation);
                    model.transform.SetPositionAndRotation(position, rotation);
                }
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[GrenadeSlot] drawing the slot: {exception.Message}");
                enabled = false;
            }
        }

        private static void SlotPose(out Vector3 position, out Quaternion rotation)
        {
            Quaternion body = ChestHang.BodyYaw(out Vector3 neck);
            if (VRSettings.GetGrenadeSlot(out Vector3 placedPosition, out Quaternion placedRotation))
            {
                position = neck + body * placedPosition;
                rotation = body * placedRotation;
                return;
            }
            position = neck + body * slotFromNeck;
            rotation = body * Quaternion.Euler(slotEuler);
        }

        // Tarkov's own model of it, from its pool: colliders off (the weapon collision copies nearby colliders), drawn on the gun's
        // layer; both put back before it goes back
        private static void BuildModel(ThrowWeap grenade)
        {
            ReturnModel();
            ObjectsFactory factory = Singleton<ObjectsFactory>.Instance;
            if (factory == null)
                return;
            model = factory.CreateItem(grenade, isAnimated: false);
            modelFor = grenade;
            if (model == null)
                return;
            foreach (Collider collider in model.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled)
                    continue;
                collider.enabled = false;
                modelColliders.Add(collider);
            }
            int layer = LayerMask.NameToLayer("Player");
            foreach (Transform node in model.GetComponentsInChildren<Transform>(true))
            {
                modelLayers[node] = node.gameObject.layer;
                node.gameObject.layer = layer;
            }
            model.SetActive(true);
        }

        private static void ReturnModel()
        {
            if (model != null)
            {
                foreach (Collider collider in modelColliders)
                {
                    if (collider != null)
                        collider.enabled = true;
                }
                foreach (KeyValuePair<Transform, int> entry in modelLayers)
                {
                    if (entry.Key != null)
                        entry.Key.gameObject.layer = entry.Value;
                }
                model.transform.SetParent(null, worldPositionStays: false);
                AssetPoolObject.ReturnToPool(model);
            }
            model = null;
            modelFor = null;
            modelColliders.Clear();
            modelLayers.Clear();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The hands

        private static bool IsGunHand(Hand hand) => hand.left == Handedness.GunHandIsLeft;

        // Free to take it: the gun hand with nothing it would let go of (empty, a knife, the gun hanging), the off hand off the gun and holding nothing
        private static bool HandFree(Hand hand)
        {
            var hands = VRGlobals.handsInteractionController;
            if (hands.heldItem != null || GrenadeHold.OffHandBusy)
                return false;
            if (IsGunHand(hand))
                return (ChestHang.Hanging || VRGlobals.player.HandsIsEmpty || VRGlobals.player.HandsController is EFT.Player.KnifeController)
                    && !VRReloadController.GunHandHolds;
            return !VRGlobals.vrPlayer.isSupporting && !ChestHang.SupportOnly && !VRReloadController.OffHandHolds && !VRReloadController.OffHandAtChargingHandle
                && !VRReloadController.OffHandAtBolt && hands.scopeTransform == null;
        }

        // The rendered hand's points (palm, knuckles, fingertips) to the grenade's middle, over grabRadius: under 1 = in reach
        private static float ReachOf(Hand hand)
        {
            if (model == null)
                return float.MaxValue;
            Renderer renderer = model.GetComponentInChildren<Renderer>();
            Vector3 middle = renderer != null ? renderer.bounds.center : model.transform.position;
            // The physical right controller draws the right arm (the gun in the left hand frees it onto the right controller)
            VRGlobals.handsInteractionController.GetHandTouchPoints(!hand.left, hand.touchPoints);
            if (hand.touchPoints.Count == 0)
                hand.touchPoints.Add(ControllerPosition(hand));
            float nearest = float.MaxValue;
            foreach (Vector3 point in hand.touchPoints)
                nearest = Mathf.Min(nearest, Vector3.Distance(point, middle));
            return nearest / grabRadius;
        }

        private static bool ReadGrip(Hand hand)
        {
            bool down = (hand.left ? VRInput.LeftGrip : VRInput.RightGrip).state;
            bool pressed = down && !hand.gripDown;
            hand.gripDown = down;
            return pressed;
        }

        private static bool ReadTrigger(Hand hand)
        {
            float axis = (hand.left ? VRInput.LeftTrigger : VRInput.RightTrigger).axis;
            bool down = hand.triggerDown ? axis > 0.35f : axis > 0.5f;
            bool pressed = down && !hand.triggerDown;
            hand.triggerDown = down;
            return pressed;
        }

        // The controller's pose in the world: its tracking pose through the rig (GunSpots.HandPose)
        private static void ControllerPose(Hand hand, out Vector3 position, out Quaternion rotation)
        {
            VRPose pose = hand.left ? VRInput.LeftHandPose : VRInput.RightHandPose;
            Transform rig = VRGlobals.vrOffsetter.transform;
            position = rig.TransformPoint(pose.localPosition);
            rotation = rig.rotation * pose.localRotation;
        }

        private static Vector3 ControllerPosition(Hand hand)
        {
            ControllerPose(hand, out Vector3 position, out _);
            return position;
        }

        private static void Haptic(Hand hand, float amplitude, float duration)
        {
            TarkovVR.Source.Misc.HapticMixer.PlayPulse(hand.left ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand, amplitude, duration, 95f);
        }

        private static Item HandsItem()
        {
            return VRGlobals.player?.HandsController is EFT.Player.AbstractHandsController controller ? controller.Item : null;
        }
    }
}
