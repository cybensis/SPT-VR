using System.Collections.Generic;
using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using TarkovVR.ModSupport;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;

using TarkovVR.Source.Weapons;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Player.Interactions
{
    // Guns on the body. Each weapon you're not holding hangs where you reach for it (the first primary on the chest sling, the second over
    // the dominant shoulder, the pistol on the dominant hip, the knife on Tarkov's sheath), in the body's frame (ChestHang.BodyYaw) so
    // turning your head doesn't move them. The dominant hand takes one by gripping it, with no draw animation. A gun you let go of goes to
    // its spot (ChestHang.SpotPose) and stays in hands game-side until another is taken. Switches are Tarkov's own Select commands with
    // the put-away and draw skipped. The body models are Tarkov's (PlayerBody.SlotViews builds them for your player too and hides the
    // renderers; a weapon in hands has none). During a switch the taken gun's body model rides in the hand until Tarkov's gun replaces it.
    // Only the drawing is local: co-op partners see Tarkov's default placement.
    public static class BodyWeapons
    {
        public static bool enabled = true;
        public static bool fastSwitch = true;           // false = Tarkov's put-away and draw animations play out
        public static float grabRadius = 0.13f;         // m, from the RightHand rig transform to a body gun's grip (like ChestHang.regrabRadius)
        public static float gunGrabRadius = 0.06f;      // m, from any point of the hand (palm, fingertips) to a gun's barrel line: grabbing anywhere on it takes it
        public static float switchTimeout = 3f;         // s before a switch that never lands is given up
        public static bool debugLog = false;            // logs every grip press: the zones, each slot's gun and grip distance, why a gun was or wasn't taken
        public static bool leftHandTakes = true;        // right-handed mode: the left hand takes a gun off the body too, and holds it left-handed
        // true = the grabbed gun's draw is skipped the moment Tarkov starts it (BodyWeaponPatches.SkipDraw), before the animator plays a frame.
        // UpdateSwitch's skip comes a frame later, when the draw's swing-up and sound have already started.
        public static bool skipDrawAtSpawn = true;
        private static Item lastSwitchItem;

        private static readonly EquipmentSlot[] bodySlots =
            { EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon, EquipmentSlot.Holster, EquipmentSlot.Scabbard };

        // Models we've moved or shown, with Tarkov's own local pose to put back when we stop
        private static readonly Dictionary<GameObject, Pose> tarkovPoses = new Dictionary<GameObject, Pose>();
        private static readonly Dictionary<GameObject, Transform> weaponRoots = new Dictionary<GameObject, Transform>();
        private static readonly Dictionary<GameObject, Transform> gripMarkers = new Dictionary<GameObject, Transform>();
        private static readonly List<GameObject> stale = new List<GameObject>();
        private static readonly HashSet<GameObject> currentModels = new HashSet<GameObject>();
        private static bool hooked;

        // The switch in progress
        private static Item switchingTo;
        private static GameObject standIn;
        private static Vector3 standInInHandPosition;
        private static Quaternion standInInHandRotation;
        private static bool standInInLeftHand;
        private static float switchStarted;
        // The gun the left hand took. It is held left-handed once it spawns (Handedness.GunInLeftHand); ChestHang keeps it there through the switch.
        private static Item takenLeftItem;
        private static float takenLeftUntil;
        private static bool prevLeftGrip;

        public static bool Switching => switchingTo != null;
        // True from a grab until the grip opens, so the holster zones and the radial menu ignore that press
        public static bool GrabbedThisPress { get; private set; }
        // The same for the left grip, for a gun the left hand took (the quick-slot radial behind the shoulder ignores that press)
        public static bool LeftGrabbedThisPress { get; private set; }
        // The dominant hand is within reach of a gun on the body. The loot pointer yields there, as at the hanging gun's regrab zone.
        public static bool HandAtBodyGun { get; private set; }
        // The left hand is within reach of a body gun it may take. The off-hand loot pointer and body grab yield there.
        public static bool OffHandAtBodyGun { get; private set; }
        // A gun the left hand took is coming into the hands, or just arrived. ChestHang leaves it in the left hand.
        public static bool KeepsLeftHand => takenLeftItem != null && Time.time < takenLeftUntil
            && (switchingTo == takenLeftItem || HandsItem() == takenLeftItem);
        // This system replaces the holster zones' own swaps. For the pistol, only when Tarkov draws it on the body (in a holster).
        public static bool ReplacesBackZone => Active;
        public static bool ReplacesHipZone => Active && ViewOf(EquipmentSlot.Holster) != null;

        internal static bool IsActive => Active;

        private static bool Active => enabled && VRGlobals.player != null && VRGlobals.player.IsYourPlayer && VRGlobals.vrPlayer != null
            && VRGlobals.vrPlayer.RightHand != null && VRGlobals.player.PlayerBody != null;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Grabbing

        // Called from HandsInteractionController.Update, before ChestHang.UpdateInput, so a grab here takes the press before ChestHang's
        // regrab sees it. The shoulder and hip zones (InitVRPatches' backHolster / sidearmHolster colliders, one frame stale) take their own gun.
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
            bool leftGrip = VRInput.LeftGrip.state;
            bool leftGripPressed = leftGrip && !prevLeftGrip;
            prevLeftGrip = leftGrip;
            if (!leftGrip)
                LeftGrabbedThisPress = false;
            if (Switching)
            {
                HandAtBodyGun = false;
                OffHandAtBodyGun = false;
                UpdateSwitch();
                return;
            }
            HandAtBodyGun = Active && NearestBodyGun(out _, out _, out _, out _);
            if (gripPressed && debugLog && Active)
                LogGripPress();
            // Skip a press the chest grenade already took (GrenadeSlot runs just before)
            if (gripPressed && HandAtBodyGun && !GrenadeSlot.TookPress)
            {
                TryGrab();
                if (Switching)
                {
                    OffHandAtBodyGun = false;
                    return;
                }
            }
            OffHandAtBodyGun = Active && LeftHandMayTake() && NearestBodyGunToLeftHand(out _, out _, out _, out _);
            if (leftGripPressed && debugLog && Active)
                LogLeftGripPress();
            if (leftGripPressed && OffHandAtBodyGun && !GrenadeSlot.TookPress)
                TryGrabLeft();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The left hand (right-handed mode) takes a gun off the body and holds it the way it holds a slung gun it takes (ChestHang).
        // Not the knife: it has no left-handed hold.

        private static readonly List<Vector3> leftTouchPoints = new List<Vector3>();

        private static string WhyLeftHandMayNot()
        {
            if (!leftHandTakes) return "off (leftHandTakes)";
            if (Handedness.LeftHanded) return "left-handed mode";
            if (Handedness.GunInLeftHand) return "a gun in the left hand already";
            if (VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.LeftHand == null || VRGlobals.handsInteractionController == null) return "no left hand";
            if (VRGlobals.vrPlayer.isSupporting) return "on the foregrip";
            if (VRReloadController.OwnsOffHand) return "on a reload part or in its zone";
            if (HandsInteractionController.HoldingLoot) return "holding loot";
            if (GrenadeSlot.OffHandAtSlot) return "at the grenade";
            if (PhysicalDoors.HoldsDoor(false)) return "on a door";
            // At the slung gun's grip, the press takes the slung gun into the left hand instead (ChestHang)
            if (ChestHang.LeftHandAtHangingGrip()) return "at the slung gun's grip";
            return null;
        }

        private static bool LeftHandMayTake() => WhyLeftHandMayNot() == null;

        private static bool NearestBodyGunToLeftHand(out Item takenItem, out GameObject takenModel, out EquipmentSlot takenSlot, out float nearest)
        {
            takenItem = null;
            takenModel = null;
            takenSlot = EquipmentSlot.FirstPrimaryWeapon;
            nearest = 1f;
            Item inHands = HandsItem();
            VRGlobals.handsInteractionController.GetHandTouchPoints(false, leftTouchPoints);
            foreach (EquipmentSlot slot in bodySlots)
            {
                if (slot == EquipmentSlot.Scabbard)
                    continue;
                GameObject model = ViewOf(slot)?.Model;
                Item item = SlotItem(slot);
                if (model == null || !model.activeInHierarchy || item == null || item == inHands || GripMarkerOf(model) == null)
                    continue;
                float score = HandReach(WeaponRootOf(model), model.transform, GrabPoint(model), leftTouchPoints);
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

        private static void TryGrabLeft()
        {
            string whyNot = WhyNotSwitch();
            if (!Active || whyNot != null)
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[BodyWeapons] left hand: no switch now ({whyNot})");
                return;
            }
            Item inHands = HandsItem();
            if (!NearestBodyGunToLeftHand(out Item takenItem, out GameObject takenModel, out EquipmentSlot takenSlot, out float nearest))
                return;
            switchingTo = takenItem;
            lastSwitchItem = takenItem;
            standIn = takenModel;
            standInInLeftHand = true;
            Transform handTransform = VRGlobals.vrPlayer.LeftHand.transform;
            standInInHandRotation = Quaternion.Inverse(handTransform.rotation) * takenModel.transform.rotation;
            standInInHandPosition = Quaternion.Inverse(handTransform.rotation) * (takenModel.transform.position - handTransform.position);
            switchStarted = Time.time;
            takenLeftItem = takenItem;
            takenLeftUntil = Time.time + switchTimeout + 1f;
            LeftGrabbedThisPress = true;
            ChestHang.TakenFromBody(takenItem);
            VRInputManager.ForceCommand(SelectCommand(takenSlot));
            HapticMixer.PlayPulse(Valve.VR.SteamVR_Input_Sources.LeftHand, 0.35f, 0.07f, 95f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[BodyWeapons] left hand took {takenItem.LocalizedShortName()} from {takenSlot} (reach {nearest:0.00}), in hands: {inHands?.LocalizedShortName() ?? "nothing"}");
        }

        private static void LogLeftGripPress()
        {
            var line = new System.Text.StringBuilder($"[BodyWeapons] left grip press: {WhyLeftHandMayNot() ?? "free"}, can switch: {WhyNotSwitch() ?? "yes"}");
            if (VRGlobals.handsInteractionController != null)
            {
                VRGlobals.handsInteractionController.GetHandTouchPoints(false, leftTouchPoints);
                foreach (EquipmentSlot slot in bodySlots)
                {
                    GameObject model = ViewOf(slot)?.Model;
                    if (slot == EquipmentSlot.Scabbard || model == null || !model.activeInHierarchy)
                        continue;
                    line.Append($" | {slot}: {SlotItem(slot)?.LocalizedShortName() ?? "empty"}, reach {HandReach(WeaponRootOf(model), model.transform, GrabPoint(model), leftTouchPoints):0.00}");
                }
            }
            Plugin.MyLog.LogInfo(line.ToString());
        }

        // The gun the dominant hand takes: while the hand is in the shoulder or hip zone, that zone's gun (the guns sit there,
        // ChestHang.SpotPose); otherwise the body gun nearest the hand within grabRadius. Never the gun in hands (taking the hanging
        // one back is ChestHang's regrab).
        private static bool NearestBodyGun(out Item takenItem, out GameObject takenModel, out EquipmentSlot takenSlot, out float nearest)
        {
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
                // If the gun's spot was placed by hand, the gun isn't in the zone any more; the reach check below finds it where it is.
                if (!GunSpots.MovedOffZone(zoneSlot) && zoneModel != null && zoneModel.activeInHierarchy && zoneItem != null && zoneItem != inHands)
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
        // Reach: anywhere on the gun, not just its grip

        // A capsule along the gun's barrel through the middle of its meshes, in its Weapon_root's frame, plus the grip reach. Measured
        // from mesh-local corners, never Renderer.bounds (the world box grows as the gun turns). A score under 1 is in reach; smaller is nearer.
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
                // Only the gun's own drawn parts. Skip the muzzle flash's combined mesh (MuzzleJetCombinedMesh), which sits out in front of
                // the muzzle and would stretch the line well past it, and any mesh that isn't drawn.
                if (meshFilter.name.IndexOf("MuzzleJet", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                MeshRenderer meshRenderer = meshFilter.GetComponent<MeshRenderer>();
                if (meshRenderer == null || !meshRenderer.enabled)
                    continue;
                Bounds bounds = meshFilter.sharedMesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 inRoot = weaponRoot.InverseTransformPoint(meshFilter.transform.TransformPoint(BoundsCorners.Corner(bounds, corner)));
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

        // ReachScore's measure for any hand's touch points (GunSpots lets either hand place a gun's spot). Under 1 is in reach.
        internal static float HandReach(Transform weaponRoot, Transform meshRoot, Vector3 gripPoint, List<Vector3> handPoints)
        {
            if (weaponRoot == null)
                return float.MaxValue;
            GunSpan span = SpanOf(weaponRoot, meshRoot);
            Vector3 butt = span != null ? weaponRoot.TransformPoint(span.butt) : Vector3.zero;
            Vector3 muzzle = span != null ? weaponRoot.TransformPoint(span.muzzle) : Vector3.zero;
            float score = float.MaxValue;
            foreach (Vector3 point in handPoints)
            {
                score = Mathf.Min(score, Vector3.Distance(point, gripPoint) / grabRadius);
                if (span != null)
                    score = Mathf.Min(score, DistanceToSegment(point, butt, muzzle) / gunGrabRadius);
            }
            return score;
        }

        // A gun drawn at its spot on the body (for GunSpots): shown, not the one in hands, not riding the hand in a switch, and not the knife
        internal static bool DrawnBodyGun(EquipmentSlot slot, out GameObject model, out Transform weaponRoot, out Vector3 gripLocal, out Vector3 grabPoint)
        {
            model = null;
            weaponRoot = null;
            gripLocal = Vector3.forward;
            grabPoint = Vector3.zero;
            if (!Active || slot == EquipmentSlot.Scabbard)
                return false;
            GameObject found = ViewOf(slot)?.Model;
            Item item = SlotItem(slot);
            if (found == null || !found.activeInHierarchy || item == null || item == HandsItem() || (Switching && found == standIn) || GripMarkerOf(found) == null)
                return false;
            model = found;
            weaponRoot = WeaponRootOf(found);
            gripLocal = GripInWeaponRoot(found);
            grabPoint = GrabPoint(found);
            return true;
        }

        private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 along = end - start;
            float fraction = along.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - start, along) / along.sqrMagnitude) : 0f;
            return Vector3.Distance(point, start + along * fraction);
        }

        private static void TryGrab()
        {
            if (!Active || !CanSwitchNow())
                return;
            Item inHands = HandsItem();
            if (!NearestBodyGun(out Item takenItem, out GameObject takenModel, out EquipmentSlot takenSlot, out float nearest))
                return;
            // The hanging gun in hand is nearer than this one: leave the press to its regrab
            if (ChestHang.Detached && ChestHang.RegrabScore() < nearest)
                return;

            switchingTo = takenItem;
            lastSwitchItem = takenItem;
            standIn = takenModel;
            standInInLeftHand = false;
            takenLeftItem = null;
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
            // As a co-op client, a hands change sent before the host has answered the last move is refused, and the hands stay broken
            if (CoopHost.StillAnswering) return "co-op: the host hasn't answered your last move yet";
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
                {
                    line.Append($", grip {Vector3.Distance(hand, GrabPoint(model)) * 100f:0} cm, reach {ModelReachScore(model):0.00}");
                    GunSpan span = GripMarkerOf(model) != null ? SpanOf(WeaponRootOf(model), model.transform) : null;
                    if (span != null)
                        line.Append($", gun line {(span.butt - span.muzzle).magnitude * 100f:0} cm");
                }
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
            // Skip the put-away of the gun going away, then the draw of the one coming in, with Tarkov's own skip
            if (fastSwitch && player.HandsController is FirearmController firearmController && !(firearmController.CurrentOperation is FirearmController.Idling))
                WeaponPatches.SkipDrawOrPutAway(firearmController);
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

        // Called from FirearmController.Spawn (via Player.SpawnController, patched in BodyWeaponPatches.SkipDraw) when a gun's draw has just started
        internal static void OnDrawStarted(FirearmController firearmController)
        {
            try
            {
                // A gun the left hand took is held by it from its first frame, before ChestHang sees the switch (ChestHang then keeps it: KeepsLeftHand)
                if (firearmController != null && firearmController._player != null && firearmController._player.IsYourPlayer
                    && takenLeftItem != null && firearmController.Item == takenLeftItem && Time.time < takenLeftUntil)
                {
                    Handedness.SetGunInLeftHand(true);
                    if (debugLog)
                        Plugin.MyLog.LogInfo($"[BodyWeapons] {takenLeftItem.LocalizedShortName()} spawning in the left hand");
                }
                if (!skipDrawAtSpawn || !fastSwitch || !Active || firearmController == null || firearmController._player == null || !firearmController._player.IsYourPlayer)
                    return;
                if (lastSwitchItem == null || firearmController.Item != lastSwitchItem || Time.time - switchStarted > switchTimeout)
                    return;
                if (!SkipDrawNow(firearmController))
                    return;
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[BodyWeapons] draw of {firearmController.Item.LocalizedShortName()} skipped as it started (+{Time.time - switchStarted:0.000}s, frame {Time.frameCount}): op now {firearmController.CurrentOperation?.GetType().Name}");
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[BodyWeapons] skipping the draw failed: {exception}");
            }
        }

        // Finishes a draw that has just started (GrenadeSlot also uses it for the gun a grenade returns to). Returns false if the gun isn't drawing.
        internal static bool SkipDrawNow(FirearmController firearmController)
        {
            if (!(firearmController.CurrentOperation is FirearmController.SpawnOperation spawn))
                return false;
            // Show now the round the draw would have chambered. For a gun the hand racks, VRReloadController.TakeRackOutOfDraw has
            // already taken it out of the draw.
            if (spawn._ammoFromMagToChamber != null)
            {
                spawn.OnAddAmmoInChamber();
                spawn._ammoFromMagToChamber = null;
            }
            WeaponPatches.SkipDrawOrPutAway(firearmController);
            return true;
        }

        private static void EndSwitch()
        {
            switchingTo = null;
            standIn = null;
        }

        internal static ECommand SelectCommand(EquipmentSlot slot)
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

        // Runs after every LateUpdate, when Tarkov's body bones are final, so it has the last word on where these models are
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
                if (model != null)
                {
                    lostSince.Remove(slot);
                    KeepSlotBoneOn(view, model);
                }
                else if (view != null)
                {
                    RebuildIfLost(slot, view, inHandsItem);
                }
                if (model == null || !model.activeInHierarchy)
                    continue;
                currentModels.Add(model);
                if (!tarkovPoses.ContainsKey(model))
                    tarkovPoses[model] = new Pose(model.transform.localPosition, model.transform.localRotation);
                // Hidden, as Tarkov keeps them for your player: an empty slot (walking out of the range empties the slots, but the
                // models stay), and the gun in hands, whose body model Tarkov doesn't always drop (the range's hands item doesn't
                // reach the body)
                Item slotItem = SlotItem(slot);
                // A gun the left hand took rides only until it's held left-handed; then Tarkov's own gun is in that hand
                bool riding = Switching && model == standIn && !(standInInLeftHand && Handedness.GunInLeftHand);
                bool hidden = slotItem == null || (inHandsItem != null && slotItem == inHandsItem && !riding);
                foreach (Renderer renderer in view.Renderers)
                {
                    if (renderer != null && renderer.enabled == hidden)
                        renderer.enabled = !hidden;
                }
                if (hidden)
                    continue;
                if (riding)
                {
                    Transform hand = (standInInLeftHand ? VRGlobals.vrPlayer.LeftHand : VRGlobals.vrPlayer.RightHand).transform;
                    model.transform.SetPositionAndRotation(hand.position + hand.rotation * standInInHandPosition, hand.rotation * standInInHandRotation);
                }
                // Move the guns to our spots (Tarkov's thigh-holster and back bones turn with the body). The knife stays on
                // Tarkov's sheath.
                else if (slot != EquipmentSlot.Scabbard && ChestHang.SpotPose(slot, GripInWeaponRoot(model), out Vector3 position, out Quaternion rotation))
                {
                    PlaceWeaponRoot(model, position, rotation);
                }
            }
            // A model Tarkov took back (its gun went into hands or into the bag) went back to its pool; Tarkov sets its pose when it's used again
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

        // The mod switches Tarkov's back-gun bones off (weapon_holster, weapon_holster1 in InitVRPatches SetupIK, at rig setup and on
        // every equip). Both primaries' models hang under them, so they would be hidden and out of reach (the pistol has its own bone).
        // While this runs, each slot's own bone is turned back on; the Player layer they're on has no collision.
        private static readonly List<GameObject> bonesTurnedOn = new List<GameObject>();

        // A slot holding a gun that isn't in your hands, with no model and none being built: Tarkov dropped the model and didn't rebuild
        // it (happens at the range after a gun's second draw). It is rebuilt the way its slot view does it, at most once a second.
        private static readonly Dictionary<EquipmentSlot, float> lostSince = new Dictionary<EquipmentSlot, float>();

        private static void RebuildIfLost(EquipmentSlot slot, PlayerBody.SlotView view, Item inHandsItem)
        {
            Item item = SlotItem(slot);
            if (slot == EquipmentSlot.Scabbard || item == null || item == inHandsItem || (view.LoadingJob != null && !view.LoadingJob.IsCompleted))
            {
                lostSince.Remove(slot);
                return;
            }
            if (!lostSince.TryGetValue(slot, out float since))
            {
                lostSince[slot] = Time.time;
                return;
            }
            if (Time.time - since < 1f)
                return;
            lostSince[slot] = Time.time;
            if (debugLog)
                Plugin.MyLog.LogWarning($"[BodyWeapons] {slot}: {item.LocalizedShortName()} has no body model, building it again");
            view.CancelCreating();
            view._item = item;
            view.LoadItem();
        }

        private static void KeepSlotBoneOn(PlayerBody.SlotView view, GameObject model)
        {
            if (!model.activeSelf || model.activeInHierarchy)
                return;
            Transform bone = model.transform.parent;
            if (bone == null || bone.gameObject.activeSelf || (bone != view._bone && bone != view._alternativeHolsterBone))
                return;
            bone.gameObject.SetActive(true);
            if (!bonesTurnedOn.Contains(bone.gameObject))
                bonesTurnedOn.Add(bone.gameObject);
        }

        // Restores Tarkov's state: each model back in its pose on its bone, and hidden for your own player as Tarkov has it
        private static void PutAllBack()
        {
            foreach (GameObject bone in bonesTurnedOn)
            {
                if (bone != null)
                    bone.SetActive(false);
            }
            bonesTurnedOn.Clear();
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

        // Moves the model rigidly so its Weapon_root lands on the pose (the way ChestHang places the gun in hand)
        private static void PlaceWeaponRoot(GameObject model, Vector3 position, Quaternion rotation)
        {
            Transform root = WeaponRootOf(model);
            Quaternion turn = rotation * Quaternion.Inverse(root.rotation);
            model.transform.SetPositionAndRotation(position + turn * (model.transform.position - root.position), turn * model.transform.rotation);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Where the hand takes it: the grip, a little toward the muzzle (the same point as ChestHang's regrab zone). A knife at its middle.
        private static Vector3 GrabPoint(GameObject model)
        {
            Transform grip = GripMarkerOf(model);
            if (grip != null)
                return grip.position - WeaponRootOf(model).up * ChestHang.regrabForwardOffset;
            Renderer renderer = model.GetComponentInChildren<Renderer>();
            return renderer != null ? renderer.bounds.center : model.transform.position;
        }

        // The R-hand marker in the model's Weapon_root frame (rotation only): tells which side of the bore the grip is on
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
                // Its own marker, or a stand-in for a gun whose marker is off the grip (WeaponPatches.RightGripMarker)
                grip = WeaponPatches.RightGripMarker(model.GetComponent<WeaponPrefab>(), LinksOf(model), WeaponRootOf(model));
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
