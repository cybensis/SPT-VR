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
    // Guns on the body: every weapon you're not holding hangs where you reach for it (the first primary on the chest sling, the
    // second over the dominant shoulder, the pistol on the dominant hip, the knife on Tarkov's sheath; in the body's frame,
    // ChestHang.BodyYaw, so a glance doesn't swing them away from the hand), and the dominant hand takes one by gripping it
    // there, with no draw animation. Letting go of the gun in hand sends it to its own spot (ChestHang.SpotPose); it stays in
    // hands game-side until another is taken.
    //
    // Tarkov keeps the rules: one item in hands, and a switch is its own (the keyboard's Select commands), with its put-away and
    // draw fast-forwarded (Tarkov's own skip). The models on the body are Tarkov's: PlayerBody.SlotViews builds every slot weapon
    // for your player too and only turns its renderers off; a weapon in hands has no model. While a switch runs, the taken gun's
    // body model rides in the hand as the stand-in until Tarkov's own gun takes its place. Co-op partners see Tarkov's default
    // placement: only the drawing is local.
    public static class BodyWeapons
    {
        public static bool enabled = true;
        public static bool fastSwitch = true;           // false = Tarkov's put-away and draw animations play out
        public static float grabRadius = 0.13f;         // m RightHand rig to a body gun's grip, as ChestHang.regrabRadius
        public static float gunGrabRadius = 0.06f;      // m any point of the hand (palm, fingertips) off a gun's barrel line: anywhere on it takes it
        public static float switchTimeout = 3f;         // s before a switch that never lands is given up
        public static bool debugLog = true;             // every dominant grip press: the zones, each slot's gun and grip distance, why it did or didn't take one
        public static bool leftHandTakes = true;        // right-handed mode: the left hand takes a gun off the body too, and holds it left-handed
        // The gun a grab switches to: its draw skipped the moment Tarkov starts it (BodyWeaponPatches.SkipDraw), before the animator plays a
        // frame of it. UpdateSwitch's skip comes a frame later: the draw's swing-up and sound have started by then.
        public static bool skipDrawAtSpawn = true;
        // One log line a frame for switchTraceTime after a grab starts a switch: the hands' operation, the arms animator's layer 1, the
        // gun's animated root and its hand holder - what moves if a swing still shows
        public static bool switchTrace = true;
        public static float switchTraceTime = 1.5f;
        private static Item lastSwitchItem;

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
        private static bool standInInLeftHand;
        private static float switchStarted;
        // The gun the left hand took, held left-handed once it spawns (Handedness.GunInLeftHand); ChestHang keeps it there through the switch
        private static Item takenLeftItem;
        private static float takenLeftUntil;
        private static bool prevLeftGrip;

        public static bool Switching => switchingTo != null;
        // From a grab until the grip opens: the old holster zones and the radial menu leave that press alone
        public static bool GrabbedThisPress { get; private set; }
        // ...the left grip's, for a gun the left hand took (the quick-slot radial behind the shoulder leaves it alone)
        public static bool LeftGrabbedThisPress { get; private set; }
        // The dominant hand within reach of a gun on the body (the loot pointer yields there, as at the hanging gun's regrab zone)
        public static bool HandAtBodyGun { get; private set; }
        // The left hand within reach of one it may take (the off-hand loot pointer and body grab yield there)
        public static bool OffHandAtBodyGun { get; private set; }
        // A gun the left hand took is coming into, or just came into, the hands: ChestHang leaves it in the left hand
        public static bool KeepsLeftHand => takenLeftItem != null && Time.time < takenLeftUntil
            && (switchingTo == takenLeftItem || HandsItem() == takenLeftItem);
        // The holster zones' own swaps are this system's job now; the pistol's only where Tarkov draws it on the body (a holster)
        public static bool ReplacesBackZone => Active;
        public static bool ReplacesHipZone => Active && ViewOf(EquipmentSlot.Holster) != null;

        internal static bool IsActive => Active;

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
            bool leftGrip = VRInput.LeftGrip.state;
            bool leftGripPressed = leftGrip && !prevLeftGrip;
            prevLeftGrip = leftGrip;
            if (!leftGrip)
                LeftGrabbedThisPress = false;
            if (switchTrace && lastSwitchItem != null && Time.time - switchStarted <= switchTraceTime)
                TraceSwitchFrame();
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
            // Not a press the grenade on the chest took (GrenadeSlot, just before)
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
        // The left hand (right-handed mode): a gun off the body into it, held the way the left hand holds the slung gun it takes
        // (ChestHang). Not the knife (no left-handed hold for it).

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
            // At the slung gun's grip: that press takes the slung gun into the left hand (ChestHang)
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

        // The gun the dominant hand takes: the shoulder or hip zone's own while the hand is in that zone (the guns sit there,
        // ChestHang.SpotPose), else the one on the body nearest the hand within grabRadius. Never the one in hands (the hanging
        // one is ChestHang's regrab).
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
                // A spot placed by hand: that gun isn't in the zone any more (the reach below finds it where it is)
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

        // A capsule along the gun's barrel through the middle of its meshes, in its Weapon_root's frame (mesh-local corners, never
        // Renderer.bounds: the world box grows as the gun turns), plus the grip reach. Under 1 = in reach, smaller = nearer.
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
                // The gun's own drawn parts only: not its muzzle flash's combined mesh (MuzzleJetCombinedMesh, built out in front of the
                // muzzle - it stretched the line well past it), nor a mesh that isn't drawn
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

        // ReachScore's measure for any hand's points (GunSpots: either hand places a gun's spot). Under 1 = in reach.
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

        // A gun drawn at its spot on the body (GunSpots): shown, not the one in hands, not riding the hand in a switch, not the knife
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
            // The gun in hand hanging nearer than this one: that's its regrab
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
            // Co-op as a client: a hands change sent before the host answered the last move is refused, and the hands stay broken
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
            // The gun going away, then the one coming in: Tarkov's own skip of each
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

        // Player.SpawnController -> FirearmController.Spawn (BodyWeaponPatches.SkipDraw): the gun a grab asked for, its draw just started
        internal static void OnDrawStarted(FirearmController firearmController)
        {
            try
            {
                // Taken by the left hand: held by it from its first frame (before ChestHang sees the switch, which keeps it: KeepsLeftHand)
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
                if (switchTrace || debugLog)
                    Plugin.MyLog.LogInfo($"[BodyWeapons] draw of {firearmController.Item.LocalizedShortName()} skipped as it started (+{Time.time - switchStarted:0.000}s, frame {Time.frameCount}): op now {firearmController.CurrentOperation?.GetType().Name}");
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[BodyWeapons] skipping the draw failed: {exception}");
            }
        }

        // A draw that just started, finished now (also the gun a grenade put back returns to: GrenadeSlot). false = not drawing.
        internal static bool SkipDrawNow(FirearmController firearmController)
        {
            if (!(firearmController.CurrentOperation is FirearmController.SpawnOperation spawn))
                return false;
            // The round its draw would have racked in, shown now (a gun the hand racks had it taken out of the draw already:
            // VRReloadController.TakeRackOutOfDraw)
            if (spawn._ammoFromMagToChamber != null)
            {
                spawn.OnAddAmmoInChamber();
                spawn._ammoFromMagToChamber = null;
            }
            WeaponPatches.SkipDrawOrPutAway(firearmController);
            return true;
        }

        private static void TraceSwitchFrame()
        {
            EFT.Player player = VRGlobals.player;
            if (player == null)
                return;
            object hands = player.HandsController;
            FirearmController firearmController = hands as FirearmController;
            string handsText = hands is EFT.Player.AbstractHandsController controller ? $"{controller.GetType().Name} {controller.Item?.LocalizedShortName()}" : "none";
            string operation = firearmController != null ? firearmController.CurrentOperation?.GetType().Name ?? "none" : "-";
            string armsLayer = "-";
            if (player.ArmsAnimatorCommon != null)
            {
                var state = player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                bool idle = firearmController != null && firearmController.FirearmsAnimator != null && state.IsName(firearmController.FirearmsAnimator.FullIdleStateName);
                armsLayer = $"{state.fullPathHash} t{state.normalizedTime:0.00}{(idle ? " (idle)" : "")}";
            }
            Transform animatedRoot = player.ProceduralWeaponAnimation != null && player.ProceduralWeaponAnimation.HandsContainer != null
                ? player.ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim : null;
            string animatedPose = animatedRoot != null ? $"{(animatedRoot.localPosition * 100f).ToString("F1")} cm {animatedRoot.localEulerAngles.ToString("F0")}" : "-";
            string rootParent = firearmController != null && firearmController.WeaponRoot != null && firearmController.WeaponRoot.parent != null ? firearmController.WeaponRoot.parent.name : "-";
            Transform holder = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform : null;
            string holderPose = holder != null ? $"{(holder.localPosition * 100f).ToString("F1")} cm {holder.localEulerAngles.ToString("F0")}" : "-";
            var gunController = WeaponPatches.currentGunInteractController;
            Plugin.MyLog.LogInfo($"[BodyWeapons] switch +{Time.time - switchStarted:0.000}s f{Time.frameCount}: {handsText}, op {operation}, arms layer 1 {armsLayer}, "
                + $"Weapon_root_anim {animatedPose}, WeaponRoot under {rootParent}, holder {holderPose}, gun controller {(gunController != null ? gunController.enabled.ToString() : "none")}, "
                + $"switchingWeapon {VRGlobals.switchingWeapon}, switching {Switching}");
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
                // Hidden (Tarkov's own state for your player): an empty slot (walking out of the range empties them, the models
                // stay), and the gun in hands, whose body model Tarkov doesn't always drop (the range's hands item doesn't
                // reach the body)
                Item slotItem = SlotItem(slot);
                // (a gun the left hand took: until it's held left-handed, then Tarkov's own gun is in that hand)
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
                // The guns to our spots (Tarkov's thigh holster and back bones turn with the body); the knife stays on
                // Tarkov's sheath
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

        // The mod switches Tarkov's back-gun bones off (InitVRPatches SetupIK: weapon_holster, weapon_holster1, at rig setup and on
        // every equip) and both primaries' models hang under them: hidden and never in reach (the pistol is on its own bone).
        // The slot's own bone is back on while this runs; the Player layer they're on isn't collided.
        private static readonly List<GameObject> bonesTurnedOn = new List<GameObject>();

        // A slot holding a gun that isn't in your hands, with no model and none being built: Tarkov dropped it and didn't build it
        // again (happens at the range after a gun's second draw). Built again the way its slot view does it, at most once a second.
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

        // Back to Tarkov's: its pose on its bone, and hidden for your own player as Tarkov has it
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
                // Its own marker, or the stand-in for a gun whose marker is off the grip (WeaponPatches.RightGripMarker: the Sako)
                TransformLinks links = LinksOf(model);
                grip = WeaponPatches.RightGripMarker(model.GetComponent<WeaponPrefab>(), null, WeaponRootOf(model))
                    ?? links?.GetTransform(ECharacterWeaponBones.weapon_R_hand_marker);
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
