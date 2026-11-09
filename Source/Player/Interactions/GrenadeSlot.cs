using System.Collections.Generic;
using System.Text.RegularExpressions;
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
    // The grenade on the chest (his grenade plan, round 2): the grenade Tarkov would throw next shown on the top left of the chest (in
    // the body's frame like the guns' spots), taken by either hand's grip (his call: no trigger option). Which one:
    // Tarkov's own pick for its throw key (PlayerInputTranslator.vmethod_1) - the quick-access panel's grenade (TopPriorityGrenade), else a
    // random reachable one, not the type thrown last. That pick is random when the key is pressed, so it's made here once, shown, and the
    // grab takes that very one (SetItemInHands, as the key does); a new one after it's gone.
    // What's shown is our own copy of the grenade's meshes (Look), placed by the middle of the grenade. Tarkov's model of an item is its
    // hands prefab, whose root sits ~40 cm off the grenade, and comes out of the same pool as the grenade in your hand: one the VR hold
    // had rebuilt (the grenade moved under the RightHandPositioner) drew it wherever that hold left it (his test: "the slot just
    // disappears" after a grenade was put back).
    // The grab is Tarkov's own switch with the gun's put-away and the grenade's draw skipped (BodyWeapons' way), the model riding the hand
    // meanwhile. The left hand holds it the way it holds a gun (Handedness.GunInLeftHand: the right-handed hold mirrored,
    // RealLeftHandedPatches for the arms). In the hand, GrenadeHold; put back, its put-away is skipped too and the hands go back as they
    // were (a gun that hung on its spot hangs there again, its draw skipped).
    // Moving the slot: the trigger held 1 s at it, it rides the hand until the trigger opens, saved like a gun spot. In the hand, the
    // other hand's grip at it takes it over (GrenadeHold.UpdateSwap; HoldIn here).
    public static class GrenadeSlot
    {
        public static bool enabled = true;
        public static Vector3 slotFromNeck = new Vector3(-0.12f, -0.20f, 0.14f);    // m, body frame (x right, y up, z forward): the grenade's middle, top left of the chest (his placement)
        public static Vector3 slotEuler = new Vector3(278.5f, 132.7f, 166f);        // the grenade body's turn there, body frame (his placement)
        public static float grabRadius = 0.08f;     // m any point of the hand (palm, knuckles, fingertips) to the grenade's middle
        public static float holdSeconds = 1f;       // trigger held at the slot: moving the slot
        public static float switchTimeout = 3f;     // s before a grab whose switch never lands is given up
        public static float rehangSeconds = 3f;     // s a gun that hung before the grenade has, once it's back in the hands, to hang again
        public static float throwReturnTimeout = 8f;    // s after a throw Tarkov's draw of the gun that hung before still counts as its return
        public static bool debugLog = true;         // one line per grab / pick / placement (first round)

        private sealed class Hand
        {
            public bool left;
            public bool gripDown, triggerDown;
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

        // What's shown: one copy per grenade type, in the grenade body's frame (GrenadePrefab.ThrowingParts[0]), with the middle of
        // what's visible in that frame
        private sealed class Look
        {
            public GameObject root;
            public Vector3 middle;
            public float failedAt = -1f;
        }
        private static readonly Dictionary<MongoID, Look> looks = new Dictionary<MongoID, Look>();
        private static EFT.Player looksFor;
        private static Look shownLook;
        private static ThrowWeap shownFor;
        private static readonly HashSet<int> poppedBySlot = new HashSet<int>();
        private static readonly Regex lowerLod = new Regex(@"LOD[1-9]", RegexOptions.IgnoreCase);

        // A grab under way
        private static ThrowWeap switchingTo;
        private static bool switchFromHand;     // a grenade in hand swapped for another (GrenadePicker): no stand-in at the hand, the hung gun kept
        private static float switchStarted;
        private static Hand switchHand;
        private static Vector3 inHandPosition;
        private static Quaternion inHandRotation = Quaternion.identity;
        private static Item handsBefore;
        private static bool gunHungBefore;
        // What Tarkov draws once the grenade in hand is thrown: the item before it (PlayerOwner.PreviousItem as it came in), the one before
        // the first through picker swaps
        private static Item afterThrowItem;

        // The last grab: which grenade, and with which hand
        private static Item grabbedItem;
        private static Item takenFromSlot;      // the one the slot handed over (its put-back goes to handsBefore)
        private static bool grabbedLeft;
        private static bool grabbedByHold;

        // Put back: its put-away, and the gun the hands go back to
        private static EFT.Player.GrenadeHandsController puttingAway;
        private static float putAwayStarted;
        private static Item returningGun;
        private static bool returningHangs;
        private static float returnStarted;
        private static float returnTimeout;
        // The gun that hung on its spot when this grenade came into the hands (the grab saw it, or ChestHang still said so at its spawn)
        private static Item hungGun;

        // Moving the slot
        private static Hand placing;
        private static Look placingLook;
        private static Vector3 placingPosition;
        private static Quaternion placingRotation = Quaternion.identity;

        private static bool hooked;

        // This frame's press was a grenade grab (BodyWeapons / ChestHang leave it alone)
        public static bool TookPress { get; private set; }
        // The off hand at the grenade (the chest pouch's magazine grab yields to it; read a frame late by VRReloadController)
        public static bool OffHandAtSlot { get; private set; }
        public static bool Busy => placing != null || switchingTo != null;

        // A grenade the left hand took is held left: ChestHang leaves the left-hand hold on while it's in the hands
        public static bool KeepsLeftHand => grabbedLeft && (GrabbedInHands
            || (switchFromHand && switchingTo != null && VRGlobals.player?.HandsController is EFT.Player.BaseGrenadeHandsController));

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
            SkipPutAway();
            if (grabbedItem != null && !GrabbedInHands && !IsSwitching())
            {
                grabbedItem = null;
                takenFromSlot = null;
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
            bool lookShown = shownLook != null && shownLook.root != null && shownLook.root.activeSelf && picked != null && shownFor == picked;
            OffHandAtSlot = false;
            foreach (Hand hand in new[] { leftHand, rightHand })
            {
                bool gripPressed = ReadGrip(hand);
                ReadTrigger(hand);
                bool atSlot = lookShown && ReachOf(hand) < 1f;
                if (atSlot && !IsGunHand(hand))
                    OffHandAtSlot = true;
                if (!atSlot || !HandFree(hand))
                {
                    hand.triggerHeldAtSlot = 0f;
                    continue;
                }
                // The grip takes it; the trigger held at it moves the slot (as the guns' spots)
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

        private static bool Allowed()
        {
            return enabled && VRGlobals.inGame && !VRGlobals.menuOpen && VRGlobals.player != null && VRGlobals.player.IsYourPlayer && VRGlobals.vrPlayer != null
                && VRGlobals.vrOffsetter != null && VRGlobals.handsInteractionController != null && !VRGlobals.usingItem
                && !EatingInteractionController.ManualActive && VRGlobals.player.InventoryController != null;
        }

        private static void CancelInput()
        {
            leftHand.triggerHeldAtSlot = rightHand.triggerHeldAtSlot = 0f;
            placing = null;
            placingLook = null;
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
            // What Tarkov draws after the throw (the item before the grenade): its draw skipped, as after a put-back - straight into the
            // hand, or back on its spot if it hung
            Item back = afterThrowItem ?? hungGun;
            if (back != null && back.Parent != null)
                ExpectGunBack(back, hangs: back == hungGun, throwReturnTimeout);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] {item?.LocalizedShortName()} thrown: {(back != null && back.Parent != null ? $"{back.LocalizedShortName()} back next, its draw skipped{(back == hungGun ? ", hung on its spot" : ", in the hand")}" : "empty hands next")}");
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
            gunHungBefore = handsBefore != null && VRGlobals.player.HandsController is FirearmController && ChestHang.Hanging;
            switchingTo = picked;
            switchStarted = Time.time;
            switchHand = hand;
            grabbedItem = picked;
            takenFromSlot = picked;
            grabbedLeft = hand.left;
            grabbedByHold = true;
            Transform root = shownLook.root.transform;
            ControllerPose(hand, out Vector3 handPosition, out Quaternion handRotation);
            inHandRotation = Quaternion.Inverse(handRotation) * root.rotation;
            inHandPosition = Quaternion.Inverse(handRotation) * (root.position - handPosition);
            TookPress = true;
            if (IsGunHand(hand))
                ChestHang.IgnoreGripPress();
            Haptic(hand, 0.35f, 0.04f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] {(hand.left ? "left" : "right")} hand takes {picked.LocalizedShortName()}, in hands before: {handsBefore?.LocalizedShortName() ?? "nothing"}{(gunHungBefore ? " (hanging)" : "")}");
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
            switchFromHand = false;
        }

        // GrenadePicker: the grenade in hand swapped for another quick-access one, by the same hand and grip - Tarkov's switch, the held
        // one's put-away (SkipPutAway) and the next one's draw (OnGrenadeSpawned) skipped. A put-back later still returns the hands to
        // what they held before the first.
        internal static void SwapHeldTo(EFT.Player.GrenadeHandsController current, ThrowWeap next)
        {
            if (current == null || next == null || VRGlobals.player == null || switchingTo != null)
                return;
            bool left = Handedness.GunInLeftHand;
            switchingTo = next;
            switchFromHand = true;
            switchStarted = Time.time;
            switchHand = left ? leftHand : rightHand;
            if (takenFromSlot == current.Item)
                takenFromSlot = next;
            grabbedItem = next;
            grabbedLeft = left;
            grabbedByHold = true;
            puttingAway = current;
            putAwayStarted = Time.time;
            // Tarkov draws the item a grenade came in after once it's thrown (PlayerOwner.SetHandsController keeps PreviousItem for its
            // after-use callback): after this swap that's the grenade swapped from (his report, [GrenadeDiag]: thrown, and the other one
            // came into the hand by itself). The item before the first one instead.
            EFT.PlayerOwner owner = VRGlobals.player.GetComponent<EFT.PlayerOwner>();
            if (owner != null)
                owner.PreviousItem = afterThrowItem;
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] {current.Item.LocalizedShortName()} swapped for {next.LocalizedShortName()} ({(left ? "left" : "right")} hand), after its throw: {afterThrowItem?.LocalizedShortName() ?? "empty hands"}{(owner == null ? " (NO PlayerOwner - Tarkov's own pick)" : "")}");
            VRGlobals.player.SetItemInHands(next, delegate { });
        }

        // GrenadeHandsController.Spawn (VRGrenadeController): the grenade a grab asked for, its draw just started. Skipped (its own
        // FastForward = the draw's end), its clip played through; in the left hand if the left took it.
        public static void OnGrenadeSpawned(EFT.Player.GrenadeHandsController grenade)
        {
            try
            {
                if (grenade?._player == null || !grenade._player.IsYourPlayer)
                    return;
                // Any grenade: was the gun hanging (ChestHang hasn't seen the switch yet - Tarkov sets the hands, then Spawn)
                bool fromSlot = switchingTo != null && grenade.Item == switchingTo;
                // (swapped in the hand: the gun that hung when the first came out still did)
                if (!(fromSlot && switchFromHand))
                {
                    hungGun = fromSlot ? (gunHungBefore ? handsBefore : null)
                        : ChestHang.Hanging && VRGlobals.firearmController != null ? VRGlobals.firearmController.Item : null;
                    // Still the item before this one: Tarkov moves it on when the draw ends (the FastForward below)
                    afterThrowItem = grenade._player.GetComponent<EFT.PlayerOwner>()?.PreviousItem;
                }
                if (!fromSlot)
                {
                    // Not asked for by a grab or the picker (his report: one came by itself after a throw): who drew it
                    if (debugLog)
                        Plugin.MyLog.LogInfo($"[GrenadeDiag] {grenade.Item?.LocalizedShortName()} drawn without a grab (process {grenade._player.ProcessStatus}, next {grenade._player.ScheduledProcess?.GetType().Name ?? "none"}): {TarkovVR.Patches.Core.VR.VRGrenadeController.ShortStack()}");
                    return;
                }
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

        // GrenadeHold's hand swap: this grenade held by that hand's grip now (ChestHang keeps a left-hand hold on through KeepsLeftHand; letting
        // go before the pin's out still puts it back), also a grenade drawn another way
        public static void HoldIn(Item grenade, bool left)
        {
            grabbedItem = grenade;
            grabbedLeft = left;
            grabbedByHold = true;
        }

        // GrenadeHold: is this grenade held by the button that took it (so letting that go puts it back)
        public static bool TakenByHold(Item item)
        {
            return grabbedByHold && item != null && item == grabbedItem;
        }

        // GrenadeHold, let go before the pin was out: the hands back as they were (the grenade's on the slot again: it's in the bag). Its
        // put-away is skipped (SkipPutAway), the gun's draw too (OnGunDrawStarted); a gun that hung on its spot goes back there.
        public static void ReturnHands(EFT.Player.GrenadeHandsController grenade)
        {
            // Taken here: what the hands held before. Drawn another way (the radial menu): Tarkov's own last weapon.
            bool fromHere = grenade != null && grenade.Item == takenFromSlot;
            Item back = fromHere ? handsBefore : VRGlobals.player.LastEquippedWeaponOrKnifeItem;
            puttingAway = grenade;
            putAwayStarted = Time.time;
            if (back != null && back.Parent != null && BodyWeapons.SlotOf(back, out EquipmentSlot slot))
            {
                ExpectGunBack(back, back == hungGun, switchTimeout);
                VRInputManager.ForceCommand(SelectCommand(slot));
            }
            else
            {
                VRGlobals.player.SetEmptyHands(delegate { });
            }
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] {grenade?.Item?.LocalizedShortName()} back on the slot, hands back to {(back != null && back.Parent != null ? back.LocalizedShortName() : "empty")}{(back != null && back == hungGun ? " (hanging)" : "")}");
            // grabbedItem stays until it's out of the hands (UpdateInput): a left-hand grenade stays left for its put-away
            grabbedByHold = false;
        }

        // The grenade put back: Tarkov's put-away (Remove: the hide clip, "an animation which disarms it") ended as it starts, by the
        // clip's own end event (WeapOut: our VRGrenadeController cleanup, then Tarkov's next hands)
        private static void SkipPutAway()
        {
            if (puttingAway == null)
                return;
            if (VRGlobals.player == null || !ReferenceEquals(VRGlobals.player.HandsController, puttingAway) || Time.time - putAwayStarted > switchTimeout)
            {
                puttingAway = null;
                return;
            }
            if (!(puttingAway.CurrentOperation is EFT.Player.GrenadeHandsController.Remove))
                return;
            EFT.Player.GrenadeHandsController grenade = puttingAway;
            puttingAway = null;
            try
            {
                grenade.IEventsConsumerOnWeapOut();
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[GrenadeSlot] {grenade.Item?.LocalizedShortName()}'s put-away skipped (+{Time.time - putAwayStarted:0.000} s)");
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[GrenadeSlot] skipping the put-away failed: {exception}");
            }
        }

        // The gun coming back into the hands: after a put-back (the hands as they were), or Tarkov's own draw after a throw of the gun that
        // hung before (his report: "after you throw a grenade it automatically unslings your current weapon")
        private static void ExpectGunBack(Item gun, bool hangs, float timeout)
        {
            returningGun = gun;
            returningHangs = hangs;
            returnStarted = Time.time;
            returnTimeout = timeout;
        }

        // FirearmController.Spawn (BodyWeaponPatches.SkipDraw): that gun, its draw skipped as it starts; one that hung goes back on its spot
        // (ChestHang.HangOnArrival: hung once it's idle)
        internal static void OnGunDrawStarted(FirearmController firearmController)
        {
            try
            {
                if (returningGun == null || firearmController?._player == null || !firearmController._player.IsYourPlayer)
                    return;
                if (Time.time - returnStarted > returnTimeout)
                {
                    returningGun = null;
                    return;
                }
                if (firearmController.Item != returningGun)
                    return;
                returningGun = null;
                bool skipped = BodyWeapons.SkipDrawNow(firearmController);
                if (returningHangs)
                    ChestHang.HangOnArrival(rehangSeconds);
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[GrenadeSlot] {firearmController.Item.LocalizedShortName()} back (+{Time.time - returnStarted:0.000} s): draw {(skipped ? "skipped" : "not playing")}{(returningHangs ? ", hung on its spot again" : "")}");
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[GrenadeSlot] skipping the gun's draw failed: {exception}");
            }
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
            if (shownLook == null || shownLook.root == null)
                return;
            Transform root = shownLook.root.transform;
            ControllerPose(hand, out Vector3 handPosition, out Quaternion handRotation);
            placingRotation = Quaternion.Inverse(handRotation) * root.rotation;
            placingPosition = Quaternion.Inverse(handRotation) * (root.position - handPosition);
            placing = hand;
            placingLook = shownLook;
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
            Look look = placingLook;
            placing = null;
            placingLook = null;
            ControllerPose(hand, out Vector3 handPosition, out Quaternion handRotation);
            Quaternion rotation = handRotation * placingRotation;
            Vector3 middle = handPosition + handRotation * placingPosition + rotation * look.middle;
            Quaternion body = ChestHang.BodyYaw(out Vector3 neck);
            Quaternion toBody = Quaternion.Inverse(body);
            VRSettings.SetGrenadeSlot(toBody * (middle - neck), toBody * rotation);
            Haptic(hand, 0.45f, 0.06f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] slot placed: middle {(toBody * (middle - neck)).ToString("F3")} from the neck, turn {(toBody * rotation).eulerAngles.ToString("F1")} (body frame)");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Drawing it

        // After every LateUpdate: where the grenade is (BodyYaw read live, as the guns on the body)
        private static void OnBeforeRender()
        {
            try
            {
                bool standIn = switchingTo != null && !switchFromHand;
                ThrowWeap shown = Allowed() ? (standIn ? switchingTo : picked) : null;
                // In the hands already (not a switch's stand-in): Tarkov's own model there
                if (shown != null && !standIn && HandsItem() == shown)
                    shown = null;
                Look look = shown != null ? LookOf(shown) : null;
                if (shownLook != null && shownLook != look && shownLook.root != null && shownLook.root.activeSelf)
                    shownLook.root.SetActive(false);
                shownLook = look;
                shownFor = look != null ? shown : null;
                if (look == null)
                    return;
                if (!look.root.activeSelf)
                    look.root.SetActive(true);
                Transform root = look.root.transform;
                if (standIn && switchHand != null)
                {
                    ControllerPose(switchHand, out Vector3 handPosition, out Quaternion handRotation);
                    root.SetPositionAndRotation(handPosition + handRotation * inHandPosition, handRotation * inHandRotation);
                }
                else if (placing != null)
                {
                    ControllerPose(placing, out Vector3 handPosition, out Quaternion handRotation);
                    root.SetPositionAndRotation(handPosition + handRotation * placingPosition, handRotation * placingRotation);
                }
                else
                {
                    SlotPose(look, out Vector3 position, out Quaternion rotation);
                    root.SetPositionAndRotation(position, rotation);
                }
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[GrenadeSlot] drawing the slot: {exception}");
                enabled = false;
            }
        }

        // The look's root at the slot: its middle where it was placed (or the default), its body turned as placed
        private static void SlotPose(Look look, out Vector3 position, out Quaternion rotation)
        {
            Quaternion body = ChestHang.BodyYaw(out Vector3 neck);
            if (!VRSettings.GetGrenadeSlot(out Vector3 middleFromNeck, out Quaternion turn))
            {
                middleFromNeck = slotFromNeck;
                turn = Quaternion.Euler(slotEuler);
            }
            rotation = body * turn;
            position = neck + body * middleFromNeck - rotation * look.middle;
        }

        private static Look LookOf(ThrowWeap grenade)
        {
            if (!ReferenceEquals(VRGlobals.player, looksFor))
            {
                ClearLooks();
                looksFor = VRGlobals.player;
            }
            if (looks.TryGetValue(grenade.TemplateId, out Look look))
            {
                if (look.root != null)
                    return look;
                if (look.failedAt >= 0f && Time.time - look.failedAt < 5f)
                    return null;
            }
            look = MakeLook(grenade) ?? new Look { failedAt = Time.time };
            looks[grenade.TemplateId] = look;
            return look.root != null ? look : null;
        }

        private static void ClearLooks()
        {
            foreach (Look look in looks.Values)
            {
                if (look.root != null)
                    UnityEngine.Object.Destroy(look.root);
            }
            looks.Clear();
            poppedBySlot.Clear();
            shownLook = null;
            shownFor = null;
            placingLook = null;
        }

        // The grenade's model out of Tarkov's pool for a moment: its visible meshes copied (in the frame of its body, GrenadePrefab
        // .ThrowingParts[0] - a model the VR hold rebuilt keeps the grenade's own parts in place relative to it), then it goes straight
        // back. Hidden throwing parts count (a thrown grenade's model comes back with them off); other hidden parts and lower LODs don't.
        private static Look MakeLook(ThrowWeap grenade)
        {
            ObjectsFactory factory = Singleton<ObjectsFactory>.Instance;
            GameObject pooled = factory != null ? factory.CreateItem(grenade, isAnimated: false) : null;
            if (pooled == null)
                return null;
            Transform top = pooled.transform;
            try
            {
                poppedBySlot.Add(pooled.GetInstanceID());
                bool rebuiltByVrHold = pooled.GetComponentInChildren<HandsPositioner>(true) != null;
                // Its animator's pose now (the model played it on the slot the first build drew it on: TurnOldPlacement measures that)
                foreach (Animator animator in pooled.GetComponentsInChildren<Animator>())
                {
                    if (animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
                        animator.Update(0f);
                }
                HashSet<Transform> throwingParts = new HashSet<Transform>();
                Transform body = null;
                GrenadePrefab prefab = pooled.GetComponent<GrenadePrefab>();
                if (prefab != null && prefab.ThrowingParts != null)
                {
                    foreach (string name in prefab.ThrowingParts)
                    {
                        Transform part = TransformTools.FindTransformRecursive(top, name);
                        if (part == null)
                            continue;
                        throwingParts.Add(part);
                        if (body == null)
                            body = part;
                    }
                }
                if (body == null)
                    body = top;

                GameObject root = new GameObject($"VRGrenadeSlot {grenade.LocalizedShortName()}");
                int layer = LayerMask.NameToLayer("Player");
                root.layer = layer;
                Quaternion toBody = Quaternion.Inverse(body.rotation);
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                int parts = 0;
                foreach (MeshRenderer renderer in pooled.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!renderer.enabled || lowerLod.IsMatch(renderer.name) || !Shown(renderer.transform, top, throwingParts))
                        continue;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = filter != null ? filter.sharedMesh : null;
                    if (mesh == null)
                        continue;
                    Transform source = renderer.transform;
                    GameObject part = new GameObject(source.name);
                    part.layer = layer;
                    Transform partTransform = part.transform;
                    partTransform.SetParent(root.transform, false);
                    partTransform.localPosition = toBody * (source.position - body.position);
                    partTransform.localRotation = toBody * source.rotation;
                    partTransform.localScale = source.lossyScale;
                    part.AddComponent<MeshFilter>().sharedMesh = mesh;
                    MeshRenderer copy = part.AddComponent<MeshRenderer>();
                    copy.sharedMaterials = renderer.sharedMaterials;
                    copy.shadowCastingMode = renderer.shadowCastingMode;
                    copy.receiveShadows = renderer.receiveShadows;
                    Bounds bounds = mesh.bounds;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 offset = new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f);
                        Vector3 point = bounds.center + Vector3.Scale(bounds.extents, offset);
                        point = partTransform.localPosition + partTransform.localRotation * Vector3.Scale(partTransform.localScale, point);
                        min = Vector3.Min(min, point);
                        max = Vector3.Max(max, point);
                    }
                    parts++;
                }
                if (parts == 0)
                {
                    UnityEngine.Object.Destroy(root);
                    Plugin.MyLog.LogWarning($"[GrenadeSlot] {grenade.LocalizedShortName()}: no meshes to show on the slot (model {pooled.name})");
                    return null;
                }
                Look look = new Look { root = root, middle = (min + max) * 0.5f };
                root.SetActive(false);
                if (!rebuiltByVrHold)
                    TurnOldPlacement(top, body, look.middle);
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[GrenadeSlot] {grenade.LocalizedShortName()}'s look: {parts} meshes, {(max - min).ToString("F3")} m, body {body.name}{(rebuiltByVrHold ? " (its model was the VR hold's)" : "")}");
                return look;
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[GrenadeSlot] copying {grenade.LocalizedShortName()}'s model failed: {exception}");
                return null;
            }
            finally
            {
                top.SetParent(null, worldPositionStays: false);
                AssetPoolObject.ReturnToPool(pooled);
            }
        }

        // Every switched-off node from it up to the model's root must be a throwing part
        private static bool Shown(Transform node, Transform top, HashSet<Transform> throwingParts)
        {
            for (Transform current = node; current != null; current = current.parent)
            {
                if (!current.gameObject.activeSelf && !throwingParts.Contains(current))
                    return false;
                if (current == top)
                    break;
            }
            return true;
        }

        // The first build saved the model ROOT's pose (grenadeSlot); measured on a model the VR hold never had, that's the grenade's
        // middle and body turn now, kept exactly where it was placed
        private static void TurnOldPlacement(Transform top, Transform body, Vector3 middle)
        {
            if (VRSettings.GetGrenadeSlot(out _, out _) || !VRSettings.GetOldGrenadeSlot(out Vector3 rootPosition, out Quaternion rootRotation))
                return;
            Quaternion fromRoot = Quaternion.Inverse(top.rotation);
            Quaternion bodyInRoot = fromRoot * body.rotation;
            Vector3 middleInRoot = fromRoot * (body.position + body.rotation * middle - top.position);
            Quaternion turn = rootRotation * bodyInRoot;
            Vector3 middleFromNeck = rootPosition + rootRotation * middleInRoot;
            VRSettings.SetGrenadeSlot(middleFromNeck, turn);
            Plugin.MyLog.LogInfo($"[GrenadeSlot] the placed slot kept: middle {middleFromNeck.ToString("F3")} from the neck, turn {turn.eulerAngles.ToString("F1")} (body frame)");
        }

        // GrenadeHold's spawn line: did a look come from this very object
        internal static bool CameFromSlot(GameObject handsObject)
        {
            return handsObject != null && poppedBySlot.Contains(handsObject.GetInstanceID());
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
            if (shownLook == null || shownLook.root == null)
                return float.MaxValue;
            Transform root = shownLook.root.transform;
            Vector3 middle = root.position + root.rotation * shownLook.middle;
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
