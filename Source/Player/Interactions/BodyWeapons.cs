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
        public static float gunGrabRadius = 0.06f;      // m any point of the hand (palm, fingertips) off a gun's barrel line: anywhere on it takes it
        public static float switchTimeout = 3f;         // s before a switch that never lands is given up
        public static bool debugLog = true;             // every dominant grip press: the zones, each slot's gun and grip distance, why it did or didn't take one

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

        // From HandsInteractionController.Update, before ChestHang.UpdateInput (a grab here takes the press from its regrab). The
        // shoulder and hip zones (InitVRPatches' backHolster / sidearmHolster colliders, one frame stale) take their own gun.
        private static bool handInShoulderZone;
        private static bool handInHipZone;

        public static void UpdateInput(bool grip, bool gripPressed, bool inShoulderZone, bool inHipZone)
        {
            handInShoulderZone = inShoulderZone;
            handInHipZone = inHipZone;
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
            if (gripPressed && debugLog && Active)
                LogGripPress();
            if (gripPressed && HandAtBodyGun)
                TryGrab();
        }

        // The gun the dominant hand takes: the shoulder or hip zone's own while the hand is in that zone (the proven collision spots
        // the old swaps used; the guns sit there, ChestHang.SpotPose), else the one on the body nearest the hand within grabRadius.
        // Never the one in hands (the hanging one is ChestHang's regrab).
        private static bool NearestBodyGun(out Item takenItem, out GameObject takenModel, out EquipmentSlot takenSlot, out float nearest)
        {
            Vector3 hand = VRGlobals.vrPlayer.RightHand.transform.position;
            Item inHands = HandsItem();
            nearest = grabRadius;
            takenItem = null;
            takenModel = null;
            takenSlot = EquipmentSlot.FirstPrimaryWeapon;
            if (handInShoulderZone || handInHipZone)
            {
                EquipmentSlot zoneSlot = handInShoulderZone ? EquipmentSlot.SecondPrimaryWeapon : EquipmentSlot.Holster;
                GameObject zoneModel = ViewOf(zoneSlot)?.Model;
                Item zoneItem = SlotItem(zoneSlot);
                if (zoneModel != null && zoneModel.activeInHierarchy && zoneItem != null && zoneItem != inHands)
                {
                    takenItem = zoneItem;
                    takenModel = zoneModel;
                    takenSlot = zoneSlot;
                    nearest = 0f;
                    return true;
                }
            }
            nearest = 1f;
            foreach (EquipmentSlot slot in bodySlots)
            {
                GameObject model = ViewOf(slot)?.Model;
                Item item = SlotItem(slot);
                if (model == null || !model.activeInHierarchy || item == null || item == inHands)
                    continue;
                float score = ModelReachScore(model);
                if (score < nearest)
                {
                    nearest = score;
                    takenItem = item;
                    takenModel = model;
                    takenSlot = slot;
                }
            }
            return takenModel != null;
        }

        private static float ModelReachScore(GameObject model)
        {
            bool isGun = GripMarkerOf(model) != null;
            return ReachScore(WeaponRootOf(model), model.transform, GrabPoint(model), grabRadius, isGun);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Reach: anywhere on the gun, not just its grip (headset: "make it so you can grab the gun anywhere")

        // A capsule along the gun's barrel through the middle of its meshes, in its Weapon_root's frame (mesh-local corners, never
        // Renderer.bounds: the world box grows as the gun turns), plus the grip reach as before. Under 1 = in reach, smaller = nearer.
        private sealed class GunSpan
        {
            public Vector3 butt;      // Weapon_root local: +up is the butt
            public Vector3 muzzle;
            public float measuredAt;
        }
        private static readonly Dictionary<Transform, GunSpan> spans = new Dictionary<Transform, GunSpan>();
        private static readonly List<Vector3> touchPoints = new List<Vector3>();
        private static int touchPointsFrame = -1;

        internal static float ReachScore(Transform weaponRoot, Transform meshRoot, Vector3 gripPoint, float gripRadius, bool alongBarrel)
        {
            if (VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.RightHand == null || weaponRoot == null)
                return float.MaxValue;
            float score = Vector3.Distance(VRGlobals.vrPlayer.RightHand.transform.position, gripPoint) / gripRadius;
            GunSpan span = alongBarrel ? SpanOf(weaponRoot, meshRoot) : null;
            if (span == null)
                return score;
            Vector3 butt = weaponRoot.TransformPoint(span.butt);
            Vector3 muzzle = weaponRoot.TransformPoint(span.muzzle);
            foreach (Vector3 point in HandTouchPoints())
                score = Mathf.Min(score, DistanceToSegment(point, butt, muzzle) / gunGrabRadius);
            return score;
        }

        private static List<Vector3> HandTouchPoints()
        {
            if (touchPointsFrame == Time.frameCount)
                return touchPoints;
            touchPointsFrame = Time.frameCount;
            if (VRGlobals.handsInteractionController != null)
                VRGlobals.handsInteractionController.GetHandTouchPoints(true, touchPoints);
            else
                touchPoints.Clear();
            if (touchPoints.Count == 0)
                touchPoints.Add(VRGlobals.vrPlayer.RightHand.transform.position);
            return touchPoints;
        }

        private static GunSpan SpanOf(Transform weaponRoot, Transform meshRoot)
        {
            if (spans.TryGetValue(weaponRoot, out GunSpan span) && Time.time - span.measuredAt < 2f)
                return span;
            Vector3 low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 high = -low;
            foreach (MeshFilter meshFilter in meshRoot.GetComponentsInChildren<MeshFilter>())
            {
                if (meshFilter.sharedMesh == null)
                    continue;
                Bounds bounds = meshFilter.sharedMesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 sign = new Vector3((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f);
                    Vector3 inRoot = weaponRoot.InverseTransformPoint(meshFilter.transform.TransformPoint(bounds.center + Vector3.Scale(bounds.extents, sign)));
                    low = Vector3.Min(low, inRoot);
                    high = Vector3.Max(high, inRoot);
                }
            }
            if (low.x > high.x)
            {
                spans.Remove(weaponRoot);
                return null;
            }
            Vector3 middle = (low + high) * 0.5f;
            span = new GunSpan { butt = new Vector3(middle.x, high.y, middle.z), muzzle = new Vector3(middle.x, low.y, middle.z), measuredAt = Time.time };
            spans[weaponRoot] = span;
            return span;
        }

        private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 along = end - start;
            float t = along.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - start, along) / along.sqrMagnitude) : 0f;
            return Vector3.Distance(point, start + along * t);
        }

        private static void TryGrab()
        {
            if (!Active || !CanSwitchNow())
                return;
            Item inHands = HandsItem();
            if (!NearestBodyGun(out Item takenItem, out GameObject takenModel, out EquipmentSlot takenSlot, out float nearest))
                return;
            // The gun in hand hanging nearer than this one: that's its regrab
            if (ChestHang.Detached && ChestHang.RegrabScore() < nearest)
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
                Plugin.MyLog.LogInfo($"[BodyWeapons] took {takenItem.LocalizedShortName()} from {takenSlot} (reach {nearest:0.00}), in hands: {inHands?.LocalizedShortName() ?? "nothing"}");
        }

        // Only from hands Tarkov would let go of right now: empty, a knife, or the gun hanging at its spot and idle
        private static bool CanSwitchNow()
        {
            return WhyNotSwitch() == null;
        }

        private static string WhyNotSwitch()
        {
            if (VRGlobals.menuOpen) return "menu open";
            if (VRGlobals.usingItem) return "using an item";
            if (EatingInteractionController.ManualActive) return "eating";
            if (WeaponPatches.grenadeEquipped) return "grenade";
            if (ChestHang.SupportOnly) return "the off hand holds the gun";
            if (VRReloadController.HoldingMagazine) return "holding a magazine";
            if (VRReloadController.HoldingChargingHandle) return "holding the charging handle";
            object hands = VRGlobals.player.HandsController;
            if (hands is FirearmController firearmController)
            {
                if (!ChestHang.Hanging) return "the gun in hand isn't let go";
                if (!(firearmController.CurrentOperation is FirearmController.Idling)) return $"the gun in hand is busy ({firearmController.CurrentOperation?.GetType().Name})";
                return null;
            }
            return hands is EFT.Player.EmptyHandsController || hands is EFT.Player.KnifeController ? null : $"hands hold {hands?.GetType().Name ?? "nothing"}";
        }

        private static void LogGripPress()
        {
            Vector3 hand = VRGlobals.vrPlayer.RightHand.transform.position;
            Item inHands = HandsItem();
            var line = new System.Text.StringBuilder($"[BodyWeapons] grip press: shoulder zone {handInShoulderZone}, hip zone {handInHipZone}, can switch: {WhyNotSwitch() ?? "yes"}, in hands {inHands?.LocalizedShortName() ?? "nothing"}{(ChestHang.Detached ? $" (hanging, regrab reach {ChestHang.RegrabScore():0.00})" : "")}");
            foreach (EquipmentSlot slot in bodySlots)
            {
                PlayerBody.SlotView view = ViewOf(slot);
                GameObject model = view?.Model;
                Item item = SlotItem(slot);
                line.Append($" | {slot}: {(item != null ? item.LocalizedShortName() : "empty")}, view {(view != null ? "yes" : "NO")}, model {(model == null ? "NONE" : model.activeInHierarchy ? "active" : "INACTIVE")}");
                if (model != null)
                    line.Append($", grip {Vector3.Distance(hand, GrabPoint(model)) * 100f:0} cm, reach {ModelReachScore(model):0.00}");
            }
            Plugin.MyLog.LogInfo(line.ToString());
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
            Item inHandsItem = HandsItem();
            foreach (EquipmentSlot slot in bodySlots)
            {
                PlayerBody.SlotView view = ViewOf(slot);
                GameObject model = view?.Model;
                if (model == null || !model.activeInHierarchy)
                    continue;
                currentModels.Add(model);
                if (!tarkovPoses.ContainsKey(model))
                    tarkovPoses[model] = new Pose(model.transform.localPosition, model.transform.localRotation);
                // The gun in hands keeps its body model for your own player (PlayerBody isn't given what's in your hands, so its
                // slot view doesn't drop it): hidden, or it doubles the real one (headset: "two pistols on my hip")
                bool inHands = inHandsItem != null && SlotItem(slot) == inHandsItem && !(Switching && model == standIn);
                foreach (Renderer renderer in view.Renderers)
                {
                    if (renderer != null && renderer.enabled == inHands)
                        renderer.enabled = !inHands;
                }
                if (inHands)
                    continue;
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
