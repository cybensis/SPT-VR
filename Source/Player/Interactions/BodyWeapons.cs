using System.Collections.Generic;
using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;

using TarkovVR.Source.Weapons;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Player.Interactions
{
    // Guns on the body, VR-game style: every weapon you're not holding hangs where you reach for it (the first primary on the
    // chest sling, the second over the dominant shoulder, the pistol on the dominant hip, the knife on Tarkov's sheath; in the
    // body's frame, ChestHang.BodyYaw, so a glance doesn't swing them away from the hand), and the dominant
    // hand takes one by gripping it there, with no draw animation. Letting go of the gun in hand sends it to its own spot
    // (ChestHang.SpotPose); it stays in hands game-side until another is taken, so letting go costs no inventory action.
    //
    // Tarkov keeps the rules: one item in hands, and a switch is its own (the keyboard's Select commands), with its put-away and
    // draw fast-forwarded (Tarkov's own skip). The models on the body are Tarkov's: PlayerBody.SlotViews builds every slot weapon
    // on the body for your player too and only turns its renderers off, and a weapon in hands has no model (the slot view binds
    // "the slot's item unless it's the one in hands"). While a switch runs, the taken gun's body model rides in the hand as the
    // stand-in until Tarkov's own gun takes its place. Co-op partners see Tarkov's default placement: only the drawing is local.
    public static class BodyWeapons
    {
        public static bool enabled = true;
        public static bool fastSwitch = true;           // false = Tarkov's put-away and draw animations play out
        public static float grabRadius = 0.13f;         // m RightHand rig to a body gun's grip, as ChestHang.regrabRadius
        public static float switchTimeout = 3f;         // s before a switch that never lands is given up
        public static bool debugLog = false;

        private static readonly EquipmentSlot[] bodySlots =
            { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster, EquipmentSlot.Scabbard };

        // Models we've moved or shown, with Tarkov's own local pose, put back when we stop
        private static readonly Dictionary<GameObject, Pose> tarkovPoses = new Dictionary<GameObject, Pose>();
        private static readonly Dictionary<GameObject, Transform> weaponRoots = new Dictionary<GameObject, Transform>();
        private static readonly Dictionary<GameObject, Transform> gripMarkers = new Dictionary<GameObject, Transform>();
        private static readonly List<GameObject> stale = new List<GameObject>();
        private static readonly HashSet<GameObject> currentModels = new HashSet<GameObject>();
        private static bool hooked;

        // The switch under way
        private static Item switchingTo;
        private static GameObject standIn;
        private static Vector3 standInInHandPosition;
        private static Quaternion standInInHandRotation;
        private static float switchStarted;

        public static bool Switching => switchingTo != null;
        // From a grab until the grip opens: the old holster zones and the radial menu leave that press alone
        public static bool GrabbedThisPress { get; private set; }
        // The dominant hand within reach of a gun on the body (the loot pointer yields there, as at the hanging gun's regrab zone)
        public static bool HandAtBodyGun { get; private set; }
        // The holster zones' own swaps are this system's job now; the pistol's only where Tarkov draws it on the body (a holster)
        public static bool ReplacesBackZone => Active;
        public static bool ReplacesHipZone => Active && ViewOf(EquipmentSlot.Holster) != null;

        private static bool Active => enabled && VRGlobals.player != null && VRGlobals.player.IsYourPlayer && VRGlobals.vrPlayer != null
            && VRGlobals.vrPlayer.RightHand != null && VRGlobals.player.PlayerBody != null;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Grabbing

        // From HandsInteractionController.Update, before ChestHang.UpdateInput (a grab here takes the press from its regrab)
        public static void UpdateInput(bool grip, bool gripPressed)
        {
            if (!hooked)
            {
                hooked = true;
                Application.onBeforeRender += OnBeforeRender;
            }
            if (!grip)
                GrabbedThisPress = false;
            if (Switching)
            {
                HandAtBodyGun = false;
                UpdateSwitch();
                return;
            }
            HandAtBodyGun = Active && NearestBodyGun(out _, out _, out _, out _);
            if (gripPressed && HandAtBodyGun)
                TryGrab();
        }

        // The gun on the body nearest the dominant hand within grabRadius, not the one in hands
        private static bool NearestBodyGun(out Item takenItem, out GameObject takenModel, out EquipmentSlot takenSlot, out float nearest)
        {
            Vector3 hand = VRGlobals.vrPlayer.RightHand.transform.position;
            Item inHands = HandsItem();
            nearest = grabRadius;
            takenItem = null;
            takenModel = null;
            takenSlot = EquipmentSlot.FirstPrimaryWeapon;
            foreach (EquipmentSlot slot in bodySlots)
            {
                GameObject model = ViewOf(slot)?.Model;
                Item item = SlotItem(slot);
                if (model == null || !model.activeInHierarchy || item == null || item == inHands)
                    continue;
                float distance = Vector3.Distance(hand, GrabPoint(model));
                if (distance < nearest)
                {
                    nearest = distance;
                    takenItem = item;
                    takenModel = model;
                    takenSlot = slot;
                }
            }
            return takenModel != null;
        }

        private static void TryGrab()
        {
            if (!Active || !CanSwitchNow())
                return;
            Item inHands = HandsItem();
            if (!NearestBodyGun(out Item takenItem, out GameObject takenModel, out EquipmentSlot takenSlot, out float nearest))
                return;
            // The gun in hand hanging nearer than this one: that's its regrab
            if (ChestHang.Detached && ChestHang.RegrabDistance() < nearest)
                return;

            switchingTo = takenItem;
            standIn = takenModel;
            Transform handTransform = VRGlobals.vrPlayer.RightHand.transform;
            standInInHandRotation = Quaternion.Inverse(handTransform.rotation) * takenModel.transform.rotation;
            standInInHandPosition = Quaternion.Inverse(handTransform.rotation) * (takenModel.transform.position - handTransform.position);
            switchStarted = Time.time;
            GrabbedThisPress = true;
            ChestHang.IgnoreGripPress();
            ChestHang.TakenFromBody(takenItem);
            VRInputManager.ForceCommand(SelectCommand(takenSlot));
            ChestHang.Pulse(0.35f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[BodyWeapons] took {takenItem.LocalizedShortName()} from {takenSlot} ({nearest * 100f:0.0} cm), in hands: {inHands?.LocalizedShortName() ?? "nothing"}");
        }

        // Only from hands Tarkov would let go of right now: empty, a knife, or the gun hanging at its spot and idle
        private static bool CanSwitchNow()
        {
            if (VRGlobals.menuOpen || VRGlobals.usingItem || EatingInteractionController.ManualActive || WeaponPatches.grenadeEquipped
                || ChestHang.SupportOnly || VRReloadController.HoldingMagazine || VRReloadController.HoldingChargingHandle)
                return false;
            object hands = VRGlobals.player.HandsController;
            if (hands is FirearmController firearmController)
                return ChestHang.Hanging && firearmController.CurrentOperation is FirearmController.Idling;
            return hands is EFT.Player.EmptyHandsController || hands is EFT.Player.KnifeController;
        }

        private static void UpdateSwitch()
        {
            EFT.Player player = VRGlobals.player;
            if (player == null || Time.time - switchStarted > switchTimeout)
            {
                if (debugLog && player != null)
                    Plugin.MyLog.LogWarning($"[BodyWeapons] switch to {switchingTo?.LocalizedShortName()} never landed");
                EndSwitch();
                return;
            }
            // The gun going away, then the one coming in: Tarkov's own skip of each
            if (fastSwitch && player.HandsController is FirearmController firearmController && !(firearmController.CurrentOperation is FirearmController.Idling))
                firearmController.FastForwardCurrentState();
            object hands = player.HandsController;
            bool landed = hands is EFT.Player.AbstractHandsController controller && controller.Item == switchingTo
                && (!(hands is FirearmController landedFirearm) || landedFirearm.CurrentOperation is FirearmController.Idling);
            if (landed)
            {
                ChestHang.PlayGripSound();
                ChestHang.Pulse(0.45f);
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[BodyWeapons] {switchingTo.LocalizedShortName()} in hand after {Time.time - switchStarted:0.00} s");
                EndSwitch();
            }
        }

        private static void EndSwitch()
        {
            switchingTo = null;
            standIn = null;
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
        // Drawing them on the body

        // After every LateUpdate (Tarkov's body bones are final): the last word on where these models are
        private static void OnBeforeRender()
        {
            if (!Active)
            {
                PutAllBack();
                return;
            }
            currentModels.Clear();
            foreach (EquipmentSlot slot in bodySlots)
            {
                PlayerBody.SlotView view = ViewOf(slot);
                GameObject model = view?.Model;
                if (model == null || !model.activeInHierarchy)
                    continue;
                currentModels.Add(model);
                if (!tarkovPoses.ContainsKey(model))
                    tarkovPoses[model] = new Pose(model.transform.localPosition, model.transform.localRotation);
                foreach (Renderer renderer in view.Renderers)
                {
                    if (renderer != null && !renderer.enabled)
                        renderer.enabled = true;
                }
                if (Switching && model == standIn)
                {
                    Transform hand = VRGlobals.vrPlayer.RightHand.transform;
                    model.transform.SetPositionAndRotation(hand.position + hand.rotation * standInInHandPosition, hand.rotation * standInInHandRotation);
                }
                // The guns to our spots (Tarkov's thigh holster and back bones "aren't in good spots" and turn with the body);
                // the knife stays on Tarkov's sheath
                else if (slot != EquipmentSlot.Scabbard && ChestHang.SpotPose(slot, GripInWeaponRoot(model), out Vector3 position, out Quaternion rotation))
                {
                    PlaceWeaponRoot(model, position, rotation);
                }
            }
            // A model Tarkov took back (its gun went into hands, or into the bag) went to its pool: it sets its own pose when it's used again
            stale.Clear();
            foreach (GameObject model in tarkovPoses.Keys)
            {
                if (!currentModels.Contains(model))
                    stale.Add(model);
            }
            foreach (GameObject model in stale)
            {
                tarkovPoses.Remove(model);
                weaponRoots.Remove(model);
                gripMarkers.Remove(model);
            }
        }

        // Back to Tarkov's: its pose on its bone, and hidden for your own player as Tarkov has it
        private static void PutAllBack()
        {
            if (tarkovPoses.Count == 0)
                return;
            foreach (KeyValuePair<GameObject, Pose> entry in tarkovPoses)
            {
                if (entry.Key == null)
                    continue;
                entry.Key.transform.localPosition = entry.Value.position;
                entry.Key.transform.localRotation = entry.Value.rotation;
                foreach (Renderer renderer in entry.Key.GetComponentsInChildren<Renderer>(true))
                    renderer.enabled = false;
            }
            tarkovPoses.Clear();
            weaponRoots.Clear();
            gripMarkers.Clear();
            EndSwitch();
        }

        // The model moved rigidly so its Weapon_root lands on the pose (as ChestHang stamps the gun in hand)
        private static void PlaceWeaponRoot(GameObject model, Vector3 position, Quaternion rotation)
        {
            Transform root = WeaponRootOf(model);
            Quaternion turn = rotation * Quaternion.Inverse(root.rotation);
            model.transform.SetPositionAndRotation(position + turn * (model.transform.position - root.position), turn * model.transform.rotation);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Where the hand takes it: the grip, a little toward the muzzle (ChestHang's regrab zone, the same reach); a knife at its middle
        private static Vector3 GrabPoint(GameObject model)
        {
            Transform grip = GripMarkerOf(model);
            if (grip != null)
                return grip.position - WeaponRootOf(model).up * ChestHang.regrabForwardOffset;
            Renderer renderer = model.GetComponentInChildren<Renderer>();
            return renderer != null ? renderer.bounds.center : model.transform.position;
        }

        // The R-hand marker in the model's Weapon_root frame (rotation only): which side of the bore the grip is
        private static Vector3 GripInWeaponRoot(GameObject model)
        {
            Transform grip = GripMarkerOf(model);
            Transform root = WeaponRootOf(model);
            return grip != null ? Quaternion.Inverse(root.rotation) * (grip.position - root.position) : Vector3.forward;
        }

        private static Transform WeaponRootOf(GameObject model)
        {
            if (!weaponRoots.TryGetValue(model, out Transform root) || root == null)
            {
                root = LinksOf(model)?.GetTransform(ECharacterWeaponBones.Weapon_root) ?? model.transform;
                weaponRoots[model] = root;
            }
            return root;
        }

        private static Transform GripMarkerOf(GameObject model)
        {
            if (!gripMarkers.TryGetValue(model, out Transform grip))
            {
                grip = LinksOf(model)?.GetTransform(ECharacterWeaponBones.weapon_R_hand_marker);
                gripMarkers[model] = grip;
            }
            return grip;
        }

        private static TransformLinks LinksOf(GameObject model)
        {
            WeaponPrefab prefab = model.GetComponent<WeaponPrefab>();
            return prefab != null && prefab._objectInstance != null ? prefab._objectInstance.GetComponent<TransformLinks>() : null;
        }

        private static PlayerBody.SlotView ViewOf(EquipmentSlot slot)
        {
            PlayerBody body = VRGlobals.player != null ? VRGlobals.player.PlayerBody : null;
            return body != null && body.SlotViews.ContainsKey(slot) ? body.SlotViews.GetByKey(slot) : null;
        }

        private static Item SlotItem(EquipmentSlot slot)
        {
            return VRGlobals.player?.Equipment?.GetSlot(slot)?.ContainedItem;
        }

        private static Item HandsItem()
        {
            return VRGlobals.player?.HandsController is EFT.Player.AbstractHandsController controller ? controller.Item : null;
        }

        // The slot a weapon is equipped in, among the ones that live on the body
        internal static bool SlotOf(Item item, out EquipmentSlot slot)
        {
            foreach (EquipmentSlot candidate in bodySlots)
            {
                if (item != null && SlotItem(candidate) == item)
                {
                    slot = candidate;
                    return true;
                }
            }
            slot = EquipmentSlot.FirstPrimaryWeapon;
            return false;
        }

        // Tarkov's holster bone (the pistol's spot), whether or not a pistol is drawn there now
        internal static Transform HolsterBone()
        {
            Transform bone = ViewOf(EquipmentSlot.Holster)?._bone;
            return bone != null ? bone : VRGlobals.player?.PlayerBones?.HolsterPistol;
        }
    }
}
