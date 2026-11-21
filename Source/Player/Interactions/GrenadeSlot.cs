using System.Collections.Generic;
using System.Text.RegularExpressions;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using TarkovVR.ModSupport;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Settings;
using TarkovVR.Source.Weapons;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Player.Interactions
{
    // A grenade slot on the chest. It shows the grenade Tarkov would throw next, placed in the body's frame, and either hand's grip
    // takes it. The choice follows Tarkov's own throw key (PlayerInputTranslator.vmethod_1: TopPriorityGrenade, else a random
    // reachable one, avoiding the type thrown last). It is made here once, so the grab takes the grenade shown (SetItemInHands, as
    // the key does).
    // The grenade shown is our own copy of its meshes (Look), placed by the grenade's middle. Tarkov's model is the hands prefab: its
    // root sits ~40 cm off the grenade, and it shares a pool with the grenade in the hand (which the VR hold may have rebuilt).
    // The grab is Tarkov's own hands switch with the gun's put-away and the grenade's draw skipped. The left hand holds it the way it
    // holds a gun (Handedness.GunInLeftHand). Putting it back also skips its put-away and returns the hands to what they held.
    // Holding the trigger at the slot for holdSeconds moves the slot (saved like a gun spot).
    public static class GrenadeSlot
    {
        public static bool enabled = true;
        public static Vector3 slotFromNeck = new Vector3(-0.12f, -0.20f, 0.14f);    // m, body frame (x right, y up, z forward): the grenade's middle, top left of the chest
        public static Vector3 slotEuler = new Vector3(278.5f, 132.7f, 166f);        // the grenade body's rotation there, body frame
        public static float grabRadius = 0.08f;     // m from any point of the hand (palm, knuckles, fingertips) to the grenade's middle
        public static float holdSeconds = 1f;       // s the trigger is held at the slot to start moving it
        public static float switchTimeout = 3f;     // s before a grab whose hands switch never completes is given up
        public static float rehangSeconds = 3f;     // s, once a gun that hung before the grenade is back in the hands, for it to hang again
        public static float throwReturnTimeout = 8f;    // s after a throw that Tarkov's draw of the previously hung gun still counts as its return
        public static float returnRetryDelay = 1f;      // co-op client: s between requests for a gun's return the host refused (once it's free)
        public static int returnRetries = 4;
        public static float refusedTakeHold = 2f;       // co-op client: s the slot waits after the host refused a take
        public static bool debugLog = true;         // one line per grab / pick / placement

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

        // The grenade shown: one copy per grenade type, in the grenade body's frame (GrenadePrefab.ThrowingParts[0]), with the middle
        // of its visible meshes in that frame
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
        private static bool switchFromHand;     // a grenade in hand swapped for another (GrenadePicker): no stand-in at the hand, hungGun kept
        private static float switchStarted;
        private static Hand switchHand;
        private static Vector3 inHandPosition;
        private static Quaternion inHandRotation = Quaternion.identity;
        private static Item handsBefore;
        private static bool gunHungBefore;
        // Co-op as a client: when the last take was requested (kept until the host has had time to answer), and the wait after a refusal
        private static float takeAskedAt = -1f;
        private static float takeRefusedUntil = -1f;
        // What Tarkov draws once the grenade in hand is thrown: the item before it (PlayerOwner.PreviousItem when it came in). After
        // picker swaps, it stays the item before the first grenade.
        private static Item afterThrowItem;

        // The last grab: which grenade, and with which hand
        private static Item grabbedItem;
        private static Item takenFromSlot;      // the one the slot handed over (its put-back goes to handsBefore)
        private static bool grabbedLeft;
        private static bool grabbedByHold;

        // Putting it back: the grenade's put-away, and the gun the hands go back to
        private static EFT.Player.GrenadeHandsController puttingAway;
        private static float putAwayStarted;
        private static Item returningGun;
        private static bool returningHangs;
        private static float returnStarted;
        private static float returnTimeout;
        private static int returnTries;
        private static float lastReturnTry;
        // The gun that hung on its spot when this grenade came into the hands (seen by the grab, or ChestHang still reported it at spawn)
        private static Item hungGun;

        // Moving the slot
        private static Hand placing;
        private static Look placingLook;
        private static Vector3 placingPosition;
        private static Quaternion placingRotation = Quaternion.identity;

        private static bool hooked;

        // This frame's grip press was a grenade grab, so BodyWeapons and ChestHang ignore it
        public static bool TookPress { get; private set; }
        // The off hand is at the grenade (the chest pouch's magazine grab gives way to it; VRReloadController reads it a frame late)
        public static bool OffHandAtSlot { get; private set; }
        public static bool Busy => placing != null || switchingTo != null;

        // A grenade the left hand took is held in the left hand: ChestHang keeps the left-hand hold on while it's in the hands
        public static bool KeepsLeftHand => grabbedLeft && (GrabbedInHands
            || (switchFromHand && switchingTo != null && VRGlobals.player?.HandsController is EFT.Player.BaseGrenadeHandsController));

        private static bool GrabbedInHands => grabbedItem != null && VRGlobals.player != null
            && VRGlobals.player.HandsController is EFT.Player.BaseGrenadeHandsController grenade && grenade.Item == grabbedItem;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Called from HandsInteractionController.Update before BodyWeapons and ChestHang, so a grab here takes the press from them

        public static void UpdateInput()
        {
            TookPress = false;
            if (!hooked)
            {
                hooked = true;
                Application.onBeforeRender += OnBeforeRender;
            }
            SkipPutAway();
            UpdatePendingReturn();
            UpdateTakeAnswer();
            RetryRefusedReturn();
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
                // The grip takes it; holding the trigger at it moves the slot (like the guns' body spots)
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
            if (Time.time < nextPickTime && (picked == null || InInventory(picked)))
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

        // Tarkov's rule: a random one, with up to three tries to get a type other than the one thrown last
        private static ThrowWeap RandomPick()
        {
            ThrowWeap choice = reachable[UnityEngine.Random.Range(0, reachable.Count)];
            for (int tries = 0; tries < 2 && reachable.Count > 1 && lastThrownTemplate.HasValue && choice.TemplateId == lastThrownTemplate.Value; tries++)
                choice = reachable[UnityEngine.Random.Range(0, reachable.Count)];
            return choice;
        }

        // Called by GrenadeHold when a grenade is thrown: the next pick avoids its type (Tarkov's _prevGrenadeId)
        public static void NoteThrown(Item item)
        {
            // Tarkov draws the item from before the grenade after the throw. Its draw is skipped, as after a put-back: it goes straight
            // into the hand, or back on its spot if it was hanging.
            Item back = afterThrowItem ?? hungGun;
            takeAskedAt = -1f;
            if (InInventory(back))
                ExpectGunBack(back, hangs: back == hungGun, throwReturnTimeout);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] {item?.LocalizedShortName()} thrown: {(InInventory(back) ? $"{back.LocalizedShortName()} back next, its draw skipped{(back == hungGun ? ", hung on its spot" : ", in the hand")}" : "empty hands next")}");
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
            takeAskedAt = Time.time;
            VRGlobals.player.SetItemInHands(picked, delegate { });
        }

        // Co-op as a client: the host's answer to the last take. FIKA asks the host once the grenade is in the hand here, and a refusal
        // puts it away into empty hands. Then the hands go back to what they held (a gun that was hanging hangs again), and the slot
        // waits a moment.
        private static void UpdateTakeAnswer()
        {
            if (takeAskedAt < 0f)
                return;
            if (CoopHost.RefusedSince(takeAskedAt))
            {
                OnTakeRefused();
                return;
            }
            if (Time.time - takeAskedAt > CoopHost.waitLimit && !CoopHost.StillAnswering)
                takeAskedAt = -1f;
        }

        private static void OnTakeRefused()
        {
            Item grenade = takenFromSlot ?? switchingTo;
            takeAskedAt = -1f;
            takeRefusedUntil = Time.time + refusedTakeHold;
            EndSwitch();
            grabbedItem = null;
            takenFromSlot = null;
            grabbedLeft = false;
            grabbedByHold = false;
            bool goesBack = false;
            EquipmentSlot slot = EquipmentSlot.FirstPrimaryWeapon;
            if (InInventory(handsBefore) && BodyWeapons.SlotOf(handsBefore, out slot))
                goesBack = true;
            Plugin.MyLog.LogWarning($"[GrenadeSlot] the host refused {grenade?.LocalizedShortName()} ({CoopHost.LastRefusal}): "
                + (goesBack ? $"hands back to {handsBefore.LocalizedShortName()}{(gunHungBefore ? " (hanging)" : "")}" : "hands left empty") + $", the slot waits {refusedTakeHold:0.#} s");
            if (!goesBack)
                return;
            ExpectGunBack(handsBefore, gunHungBefore, switchTimeout);
            // Request it now if FIKA has already gone to empty hands; otherwise RetryRefusedReturn requests it once it has
            if (ReturnRefused && !CoopHost.StillAnswering)
                VRInputManager.ForceCommand(BodyWeapons.SelectCommand(slot));
        }

        private static string WhyNotSwitch(Hand hand)
        {
            if (WeaponPatches.grenadeEquipped) return "a grenade in hand already";
            if (CoopHost.StillAnswering) return "co-op: the host hasn't answered your last move yet";
            if (ReturnRefused) return "co-op: the host refused the gun's return, asking again";
            if (Time.time < takeRefusedUntil) return "co-op: the host refused the last take";
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
            // The gun being put away: skip its put-away with Tarkov's own skip (BodyWeapons' fast switch)
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

        // GrenadePicker: swaps the grenade in hand for another quick-access one, held by the same hand and grip. Uses Tarkov's switch
        // with the held grenade's put-away (SkipPutAway) and the next one's draw (OnGrenadeSpawned) skipped. A later put-back still
        // returns the hands to what they held before the first grenade.
        internal static void SwapHeldTo(EFT.Player.GrenadeHandsController current, ThrowWeap next)
        {
            if (current == null || next == null || VRGlobals.player == null || switchingTo != null)
                return;
            // Co-op as a client: wait until the host has answered the last move (a hands change sent sooner is refused)
            if (CoopHost.StillAnswering)
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[GrenadeSlot] {current.Item.LocalizedShortName()} not swapped: the host hasn't answered your last move yet");
                return;
            }
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
            // After a throw, Tarkov draws the item the grenade replaced (PlayerOwner.SetHandsController keeps PreviousItem for its
            // after-use callback). After this swap that would be the grenade swapped out, so set it to the item before the first one.
            EFT.PlayerOwner owner = VRGlobals.player.GetComponent<EFT.PlayerOwner>();
            if (owner != null)
                owner.PreviousItem = afterThrowItem;
            takeAskedAt = -1f;
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] {current.Item.LocalizedShortName()} swapped for {next.LocalizedShortName()} ({(left ? "left" : "right")} hand), after its throw: {afterThrowItem?.LocalizedShortName() ?? "empty hands"}{(owner == null ? " (NO PlayerOwner - Tarkov's own pick)" : "")}");
            VRGlobals.player.SetItemInHands(next, delegate { });
        }

        // From GrenadeHandsController.Spawn (VRGrenadeController), as the draw of the grenade a grab requested starts. The draw is
        // skipped (its own FastForward = the draw's end) and its clip played through; it goes in the left hand if the left took it.
        public static void OnGrenadeSpawned(EFT.Player.GrenadeHandsController grenade)
        {
            try
            {
                if (grenade?._player == null || !grenade._player.IsYourPlayer)
                    return;
                // For any grenade: was the gun hanging? ChestHang hasn't seen the switch yet (Tarkov sets the hands, then calls Spawn).
                bool fromSlot = switchingTo != null && grenade.Item == switchingTo;
                // When swapped in the hand, hungGun stays the gun that hung when the first grenade came out.
                if (!(fromSlot && switchFromHand))
                {
                    hungGun = fromSlot ? (gunHungBefore ? handsBefore : null)
                        : ChestHang.Hanging && VRGlobals.firearmController != null ? VRGlobals.firearmController.Item : null;
                    // Still the item before this one here: Tarkov updates it when the draw ends (the FastForward below)
                    afterThrowItem = grenade._player.GetComponent<EFT.PlayerOwner>()?.PreviousItem;
                }
                if (!fromSlot)
                {
                    // Not requested by a grab or the picker: log what drew it
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

        // GrenadeHold's hand swap, also used for a grenade drawn another way: this grenade is now held by that hand's grip. ChestHang
        // keeps a left-hand hold on through KeepsLeftHand, and letting go before the pin is out still puts it back.
        public static void HoldIn(Item grenade, bool left)
        {
            grabbedItem = grenade;
            grabbedLeft = left;
            grabbedByHold = true;
        }

        // GrenadeHold: whether this grenade is held by the button that took it (so letting go of that button puts it back)
        public static bool TakenByHold(Item item)
        {
            return grabbedByHold && item != null && item == grabbedItem;
        }

        // Co-op as a client: a put-back held until the host has answered the move before it (CoopHost)
        private static EFT.Player.GrenadeHandsController pendingReturn;
        private static float pendingReturnSince;

        // GrenadeHold, released before the pin was out: returns the hands to what they held, once the host has answered the grenade's
        // own move (co-op as a client: if sent sooner, the host refuses it and the hands stay broken)
        public static void ReturnHands(EFT.Player.GrenadeHandsController grenade)
        {
            if (CoopHost.StillAnswering)
            {
                if (pendingReturn != grenade)
                {
                    pendingReturn = grenade;
                    pendingReturnSince = Time.time;
                    if (debugLog)
                        Plugin.MyLog.LogInfo($"[GrenadeSlot] {grenade?.Item?.LocalizedShortName()} put back: waiting for the host to answer the last move first");
                }
                return;
            }
            ReturnHandsNow(grenade);
        }

        // Every frame: a held put-back is sent once the host has answered (or after CoopHost.waitLimit), or dropped if the grenade left another way
        private static void UpdatePendingReturn()
        {
            if (pendingReturn == null)
                return;
            if (VRGlobals.player == null || !ReferenceEquals(VRGlobals.player.HandsController, pendingReturn))
            {
                pendingReturn = null;
                return;
            }
            bool timedOut = Time.time - pendingReturnSince > CoopHost.waitLimit;
            if (CoopHost.StillAnswering && !timedOut)
                return;
            EFT.Player.GrenadeHandsController grenade = pendingReturn;
            pendingReturn = null;
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] put-back {(timedOut ? $"going anyway: no answer from the host in {CoopHost.waitLimit:0} s" : $"going: the host answered after {Time.time - pendingReturnSince:0.00} s")}");
            ReturnHandsNow(grenade);
        }

        // Returns the hands to what they held (the grenade goes back in the inventory and shows on the slot again). Its put-away is
        // skipped (SkipPutAway), and so is the gun's draw (OnGunDrawStarted); a gun that was hanging on its spot goes back there.
        private static void ReturnHandsNow(EFT.Player.GrenadeHandsController grenade)
        {
            // Taken from the slot: what the hands held before. Drawn another way (the radial menu): Tarkov's own last weapon.
            bool fromHere = grenade != null && grenade.Item == takenFromSlot;
            Item back = fromHere ? handsBefore : VRGlobals.player.LastEquippedWeaponOrKnifeItem;
            takeAskedAt = -1f;
            puttingAway = grenade;
            putAwayStarted = Time.time;
            if (InInventory(back) && BodyWeapons.SlotOf(back, out EquipmentSlot slot))
            {
                ExpectGunBack(back, back == hungGun, switchTimeout);
                VRInputManager.ForceCommand(BodyWeapons.SelectCommand(slot));
            }
            else
            {
                VRGlobals.player.SetEmptyHands(delegate { });
            }
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeSlot] {grenade?.Item?.LocalizedShortName()} back on the slot, hands back to {(InInventory(back) ? back.LocalizedShortName() : "empty")}{(back != null && back == hungGun ? " (hanging)" : "")}");
            // grabbedItem stays set until it's out of the hands (UpdateInput), so a left-hand grenade stays left for its put-away
            grabbedByHold = false;
        }

        // For a grenade put back: ends Tarkov's put-away (the Remove operation, the hide clip) as soon as it starts, by firing the clip's
        // own end event (WeapOut: our VRGrenadeController cleanup runs, then Tarkov's next hands controller)
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

        // Expects a gun to come back into the hands: after a put-back, or Tarkov's own draw after a throw of the gun that was hanging
        // before
        private static void ExpectGunBack(Item gun, bool hangs, float timeout)
        {
            returningGun = gun;
            returningHangs = hangs;
            returnStarted = Time.time;
            returnTimeout = timeout;
            returnTries = 0;
            lastReturnTry = Time.time;
        }

        // Whether the item is still in the inventory. Item.Parent throws for an item that's in none: a thrown grenade, or the empty hands'
        // own item (what FIKA leaves in the hands after a refused hands change)
        private static bool InInventory(Item item) => item != null && item.CurrentAddress != null;

        // Co-op as a client: the host refused the gun's return (its copy of you was still playing a move at full length: "Default
        // Inventory is currently being modified"), and FIKA left the hands empty without Tarkov's empty-hands spawn
        private static bool ReturnRefused => returningGun != null && CoopHost.IsClient && VRGlobals.player != null
            && VRGlobals.player.HandsController is EFT.Player.EmptyHandsController;

        // Requests the refused return again once the host has answered everything, a few times. No grab is allowed meanwhile
        // (it would act on those broken hands).
        private static void RetryRefusedReturn()
        {
            if (!ReturnRefused || CoopHost.StillAnswering || Time.time - lastReturnTry < returnRetryDelay)
                return;
            if (returnTries >= returnRetries || !InInventory(returningGun) || !BodyWeapons.SlotOf(returningGun, out EquipmentSlot slot))
            {
                Plugin.MyLog.LogWarning($"[GrenadeSlot] {returningGun.LocalizedShortName()} not coming back after {returnTries} tries: left in empty hands");
                returningGun = null;
                return;
            }
            returnTries++;
            lastReturnTry = Time.time;
            returnStarted = Time.time;
            Plugin.MyLog.LogWarning($"[GrenadeSlot] the host refused {returningGun.LocalizedShortName()}'s return: asking again ({returnTries} of {returnRetries})");
            VRInputManager.ForceCommand(BodyWeapons.SelectCommand(slot));
        }

        // From FirearmController.Spawn (BodyWeaponPatches.SkipDraw): skips the returning gun's draw as it starts. A gun that was hanging
        // goes back on its spot (ChestHang.HangOnArrival hangs it once it's idle).
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

        // Runs after every LateUpdate and places the grenade (BodyYaw read live, like the guns on the body)
        private static void OnBeforeRender()
        {
            try
            {
                bool standIn = switchingTo != null && !switchFromHand;
                ThrowWeap shown = Allowed() ? (standIn ? switchingTo : picked) : null;
                // Already in the hands (and not a switch's stand-in): Tarkov's own model shows it there
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

        // The look's root pose at the slot: its middle where it was placed (or the default), its body rotated as placed
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

        // Takes the grenade's model out of Tarkov's pool for a moment, copies its visible meshes, and returns it straight away. The copy
        // is in the frame of the grenade's body (GrenadePrefab.ThrowingParts[0]); a model the VR hold rebuilt keeps the grenade's own
        // parts in place relative to it. Hidden throwing parts are included (a thrown grenade's model comes back with them off); other
        // hidden parts and lower LODs are not.
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
                // Evaluate its animator's pose now (TurnOldPlacement measures the model in that pose)
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
                        Vector3 point = BoundsCorners.Corner(bounds, corner);
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

        // Visible if every inactive node from it up to the model's root is a throwing part
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

        // The legacy setting (GetOldGrenadeSlot) stores the model root's pose. On a model the VR hold never rebuilt, it is converted to the
        // grenade's middle and body rotation, so the slot stays where it was placed.
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

        // For GrenadeHold's spawn log: whether a look was copied from this object
        internal static bool CameFromSlot(GameObject handsObject)
        {
            return handsObject != null && poppedBySlot.Contains(handsObject.GetInstanceID());
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The hands

        private static bool IsGunHand(Hand hand) => hand.left == Handedness.GunHandIsLeft;

        // Free to take it: the gun hand holds nothing it would have to let go of (empty, a knife, or the gun hanging); the off hand is off
        // the gun and holds nothing
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

        // Distance from the nearest rendered hand point (palm, knuckles, fingertips) to the grenade's middle, divided by grabRadius:
        // under 1 = in reach
        private static float ReachOf(Hand hand)
        {
            if (shownLook == null || shownLook.root == null)
                return float.MaxValue;
            Transform root = shownLook.root.transform;
            Vector3 middle = root.position + root.rotation * shownLook.middle;
            // The physical right controller drives the right arm (with the gun in the left hand, the right arm follows the right controller)
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

        // The controller's world pose: its tracking pose transformed by the rig (like GunSpots.HandPose)
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
