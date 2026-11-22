using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;
using TarkovVR.Source.Player.Body;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Weapons
{
    // The charging handle. The off-hand trigger grabs it, the hand pulls it straight back along the barrel, and releasing the trigger
    // lets go. The gun hand can do the same once it's off the grip and the off hand holds the gun up.
    //  - A full pull with a round in the chamber runs Tarkov's own rechamber: that round is thrown out and the next one chambered.
    //  - Letting go after a full pull slams the bolt forward; a bolt locked back closes. Tarkov has no operation that only chambers:
    //    its magazine insert already chambered the round, which counts as still in the magazine until a full pull (WaitingForRack).
    // The bolt lock is Tarkov's bolt-catch animator flag. While it's set the gun doesn't fire (BlocksFiring).
    // A handle on the bolt (AK-type guns, VAL, SCAR, SVD: the mod_charge bone sits on the carrier, its slot usually empty) brings
    // the whole carrier back with it. A pistol's slide is worked the same way.
    // The bolt follows the handle: Tarkov's locked-back pose (the "Catch" layer, or the IdlePosition blend) is blended in by how far
    // back the handle is. Only the pose changes; the bolt-catch flag isn't touched.
    public static partial class VRReloadController
    {
        [Tunable("Reload & handles", TunableKind.Tune, "How close the palm must be to a charging handle to grab it.", Unit = "m")]
        public static float handleGrabRadius = 0.06f;      // m, palm distance to the back of the charging handle to grab it
        public static float handleFullPull = 0.9f;         // share of handleTravel that counts as all the way back

        public static bool HoldingChargingHandle { get; private set; }
        public static bool OffHandAtChargingHandle { get; private set; }   // the trigger here works the handle, not a weapon-part interaction

        public static bool BoltLockedBack
        {
            get
            {
                FirearmController firearmController = VRGlobals.firearmController;
                return firearmController != null && firearmController.FirearmsAnimator != null && firearmController.FirearmsAnimator.GetBoltCatch();
            }
        }
        private static FirearmController handleForFirearm;
        private static Transform handleBone;                // the mod_charge slot bone
        private static Transform handleView;                // the handle model, a direct child of that bone
        private static float handlePull;                    // m back from rest
        private static bool handleFullyBack;

        // The chambered round while the bolt is open. Tarkov's round model sits on the patron_in_weapon bone and is shown and
        // hidden by the clips' events.
        private static bool showingChamber;
        private static Transform chamberBone;
        private static Transform chamberedRound;

        // The carrier the held handle is part of, and our own transforms on the gun's body standing in for handleBone and handleView
        private static Transform handleCarrier;
        private static Transform handleFrame;
        private static Transform handleRider;
        private static readonly Dictionary<string, string> slideBones = new Dictionary<string, string>();   // by animation set: the bone that is a pistol's slide
        private static readonly HashSet<string> handleMovesWithBolt = new HashSet<string>();                // by animation set: the held bolt pose moves the handle bone

        // The hand pose on the handle, read from the gun's rack clip while the handle is back. It's in the mod_charge bone's frame,
        // which the clip moves back with the handle. Ours stays put and the model moves, so this is the bone's pose with the handle at rest.
        internal sealed class HandleHold
        {
            public Vector3 position;
            public Vector3 rotation;
            public Vector3[] fingers;
            public float travel;            // m; 0 = handleTravel
        }

        // By animation set; the other guns' holds come from ReloadHolds.json. m4a1 = m4a1_reload_charge_0 at 0.6 s: thumb and index on the
        // left wing of the T.
        private static readonly Dictionary<string, HandleHold> handleHolds = new Dictionary<string, HandleHold>
        {
            {
                "m4a1", new HandleHold
                {
                    position = new Vector3(-0.1438f, 0.0636f, -0.0651f),
                    rotation = new Vector3(324.8f, 110.6f, 32.1f),
                    fingers = new[]
                    {
                        Vector3.zero,
                        new Vector3(69.0f, 53.0f, 39.4f), new Vector3(359.8f, 358.2f, 30.9f), new Vector3(359.0f, 358.8f, 43.4f),     // thumb
                        new Vector3(4.1f, 345.6f, 20.0f), new Vector3(360.0f, 359.2f, 59.9f), new Vector3(0.2f, 359.6f, 58.9f),       // index
                        new Vector3(1.5f, 345.0f, 55.4f), new Vector3(0.0f, 359.7f, 79.9f), new Vector3(0.1f, 359.6f, 76.6f),         // middle
                        new Vector3(1.9f, 344.2f, 75.0f), new Vector3(359.6f, 359.7f, 84.1f), new Vector3(360.0f, 359.6f, 83.6f),     // ring
                        new Vector3(2.6f, 343.9f, 91.2f), new Vector3(359.8f, 359.7f, 74.2f), new Vector3(359.6f, 359.7f, 42.8f),     // pinky
                    },
                }
            },
        };
        // Same for the gun hand, from a clip where the right hand racks; all from ReloadHolds.json
        private static readonly Dictionary<string, HandleHold> gunHandHandleHolds = new Dictionary<string, HandleHold>();
        // Other ways to hold the same handle (an AK's from over the top or from below). The one the hand is turned closest to at the grab is used.
        private static readonly Dictionary<string, List<HandleHold>> handleAlternates = new Dictionary<string, List<HandleHold>>();
        private static readonly Dictionary<string, List<HandleHold>> gunHandHandleAlternates = new Dictionary<string, List<HandleHold>>();
        // A handle with no slot whose rig bone isn't named mod_charge (MP7: weapon_charghing_handle, sic), by animation set
        private static readonly Dictionary<string, string> handleBoneNames = new Dictionary<string, string>();
        // Of those: the bolt or carrier a knob rides on, which is what comes back (9A-91, G36). And for a bolt that is the handle itself,
        // with no knob of its own and its back end inside the receiver: the grab point where its clip's fingers close on it, in the bone's
        // frame (PPSh, PP-91).
        private static readonly Dictionary<string, string> handleCarrierNames = new Dictionary<string, string>();
        private static readonly Dictionary<string, Vector3> handleGrabPoints = new Dictionary<string, Vector3>();
        // Where the carrier of those handles sits at the rack's full back. The pull stops there, from wherever the carrier is at the grab:
        // an open bolt rests cocked (the PPSh's 2 cm short of full back), and a full handle travel from there would leave the back of the gun.
        private static readonly Dictionary<string, SavedHandleFullBack> handleFullBacks = new Dictionary<string, SavedHandleFullBack>();
        // How far the handle comes back, for guns with no clip hand pose on it to tell (the RPD's, from its charge clip)
        private static readonly Dictionary<string, float> handleTravels = new Dictionary<string, float>();
        // Ambidextrous handles with a knob each side (the MDR's paddles): left then right, in the handle bone's frame. Each has a grab zone.
        // The left hand's holds are on the left knob, the right hand's (or their mirror) on the right.
        private static readonly Dictionary<string, Vector3[]> handleKnobs = new Dictionary<string, Vector3[]>();
        // A left-hand hold over the top of the handle, used when the palm is above it (the MDR's is the M4's: every MDR clip racks underhand)
        private static readonly Dictionary<string, HandleHold> overTopHandleHolds = new Dictionary<string, HandleHold>();
        // The knob the hand takes folds out about its pin, and back once let go. The MDR's paddles fold 45 deg in its clips (the right one
        // is the left's mirrored); a handle with one knob, the Vector's, folds 83.5 deg (grabbedKnob 0).
        private sealed class KnobFold
        {
            public string bone;
            public Vector3 axis;            // in the knob bone's own frame
            public float angle;
        }
        private static readonly Dictionary<string, KnobFold[]> handleKnobFolds = new Dictionary<string, KnobFold[]>();
        private static float heldHandleTravel;              // m this gun's handle comes back

        // Guns a magazine went into with the bolt forward on an empty chamber. Tarkov's insert chambered the top round, but it counts
        // as still in the magazine until the handle is racked. Kept per weapon, so it survives a weapon swap.
        private static readonly HashSet<Weapon> weaponsWaitingForRack = new HashSet<Weapon>();

        private static bool WaitingForRack(Weapon weapon)
        {
            if (weapon == null || !weaponsWaitingForRack.Contains(weapon))
                return false;
            // The round left some other way (unloaded in the inventory)
            if (weapon.ChamberAmmoCount == 0)
            {
                weaponsWaitingForRack.Remove(weapon);
                return false;
            }
            return true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static bool TryGetChargingHandle(FirearmController firearmController, Weapon weapon)
        {
            // At a raid's end the gun's model is destroyed before its controller, leaving no views to look in
            if (weapon == null || firearmController == null || firearmController.Firearms == null || firearmController.Firearms.ContainerCollectionView == null)
                return false;
            if (ChargingHandle.handleSlotFor != firearmController || Time.time > ChargingHandle.handleSlotLookupTime)
            {
                ChargingHandle.handleSlotFor = firearmController;
                ChargingHandle.handleSlotLookupTime = Time.time + 2f;
                ChargingHandle.foundHandleBone = null;
                ChargingHandle.foundHandleModel = null;
                ChargingHandle.foundHandleCarrier = null;
                ChargingHandle.foundHandleGrabLocal = null;
                ChargingHandle.foundHandleKnobs = null;
                foreach (Slot slot in weapon.AllSlots)
                {
                    if (slot.ID != EWeaponModType.mod_charge.ToString())
                        continue;
                    ContainerCollectionView.SlotView slotView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(slot);
                    if (slotView == null)
                        continue;
                    ChargingHandle.foundHandleBone = slotView.Bone;
                    ChargingHandle.foundHandleModel = slot.ContainedItem != null ? slotView.ItemView : null;
                    if (ChargingHandle.foundHandleModel != null)
                        break;
                }
                // No slot for one (SVD), but the rig can still have the bone. A bolt-action's is the bolt, not a handle.
                if (ChargingHandle.foundHandleBone == null && !weapon.BoltAction)
                    ChargingHandle.foundHandleBone = FindRigBone(firearmController, EWeaponModType.mod_charge.ToString());
                LoadRecordedGrips();
                string family = AnimationFamily(firearmController);
                Transform namedHandle = ChargingHandle.foundHandleBone == null && handleBoneNames.TryGetValue(family, out string handleBoneName)
                    ? FindRigBone(firearmController, handleBoneName) : null;
                if (namedHandle != null)
                {
                    // That bone is the moving handle itself. It is worked like a carrier (our frame on the gun body, the bone follows the
                    // hand), as a pistol's slide is. Measured against the bone, the pull would feed back on its own movement and shake the hand.
                    ChargingHandle.foundHandleBone = namedHandle;
                    ChargingHandle.foundHandleModel = namedHandle;
                    Transform carrier = handleCarrierNames.TryGetValue(family, out string carrierName) ? FindRigBone(firearmController, carrierName) : null;
                    ChargingHandle.foundHandleCarrier = carrier != null ? carrier : namedHandle;
                    if (handleGrabPoints.TryGetValue(family, out Vector3 grabLocal))
                        ChargingHandle.foundHandleGrabLocal = grabLocal;
                    if (handleKnobs.TryGetValue(family, out Vector3[] knobs) && knobs.Length > 0)
                        ChargingHandle.foundHandleKnobs = knobs;
                }
                else if (ChargingHandle.foundHandleBone == null)
                {
                    // A pistol: the slide is the handle and its own carrier, and its meshes give where its back end is
                    if (slideBones.TryGetValue(family, out string slideBone))
                        ChargingHandle.foundHandleBone = FindRigBone(firearmController, slideBone);
                    ChargingHandle.foundHandleModel = ChargingHandle.foundHandleBone;
                    ChargingHandle.foundHandleCarrier = ChargingHandle.foundHandleBone;
                }
                else
                {
                    Transform handleParent = ChargingHandle.foundHandleBone.parent;
                    if (handleParent != null && (handleParent.name.IndexOf("bolt", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || handleParent.name.IndexOf("charg", System.StringComparison.OrdinalIgnoreCase) >= 0))
                        ChargingHandle.foundHandleCarrier = handleParent;
                    // The bolt pose moves this bone. Measuring the pull against it would feed back (the pose follows the pull, the pull
                    // follows the pose; on the MP5 the hand shakes on the handle), so it's worked like a carrier and nothing measures from it.
                    else if (handleMovesWithBolt.Contains(family))
                        ChargingHandle.foundHandleCarrier = ChargingHandle.foundHandleBone;
                }
            }
            // An empty slot on a gun whose handle slides on its own (not on the carrier): there's nothing to take hold of. A tube shotgun
            // worked by its pump (the M3 in pump mode) doesn't use its handle to work the action.
            return ChargingHandle.foundHandleBone != null && (ChargingHandle.foundHandleModel != null || ChargingHandle.foundHandleCarrier != null) && !ShotgunInPumpMode(firearmController);
        }

        private static Transform FindRigBone(FirearmController firearmController, string boneName)
        {
            if (ChargingHandle.rigBonesFor != firearmController)
            {
                ChargingHandle.rigBonesFor = firearmController;
                ChargingHandle.rigBones.Clear();
            }
            if (ChargingHandle.rigBones.TryGetValue(boneName, out Transform found))
                return found;
            foreach (Transform part in firearmController.WeaponRoot.GetComponentsInChildren<Transform>(true))
            {
                if (part.name == boneName)
                {
                    found = part;
                    break;
                }
            }
            ChargingHandle.rigBones[boneName] = found;
            return found;
        }

        // Where the hand grabs it: the back of the fitted handle, or the bone where a handle built into the carrier sits. With a pair of
        // knobs: the one taken while holding, else the left one.
        private static Vector3 ChargingHandleGrabPoint(Vector3 backWorld)
        {
            if (ChargingHandle.foundHandleKnobs != null)
                return ChargingHandle.HandleKnobPoint(HoldingChargingHandle ? ChargingHandle.grabbedKnob : 0);
            if (ChargingHandle.foundHandleGrabLocal.HasValue)
                return ChargingHandle.foundHandleBone.position + ChargingHandle.foundHandleBone.rotation * ChargingHandle.foundHandleGrabLocal.Value;
            return ChargingHandle.foundHandleModel != null ? ChargingHandle.HandleGrabPoint(ChargingHandle.foundHandleModel, backWorld) : ChargingHandle.foundHandleBone.position;
        }

        // On a jammed gun, only when the jam is one the hands clear (VRReloadController.Malfunctions). Otherwise clearing it is the
        // interaction menu's job, and this zone would keep the menu from coming up.
        private static bool InChargingHandleZone(FirearmController firearmController, Weapon weapon, bool gunHand = false)
        {
            if (!HandsMayWorkAction(firearmController, weapon) || !TryGetChargingHandle(firearmController, weapon))
                return false;
            Vector3 backWorld = firearmController.WeaponRoot.up;
            for (int zone = 0; zone < ChargingHandle.HandleZoneCount; zone++)
            {
                Vector3 grabPoint = ChargingHandle.HandleZonePoint(zone, backWorld);
                if (HandTouchesCapsule(gunHand, grabPoint, grabPoint + backWorld * ChargingHandle.handleZoneBack, handleGrabRadius))
                    return true;
            }
            return false;
        }

        private static bool OffHandTouchesCapsule(Vector3 start, Vector3 end, float radius)
        {
            return HandTouchesCapsule(false, start, end, radius);
        }

        // Any part of the hand counts: a single point can read as outside while the fingers are clearly in the zone
        private static readonly List<Vector3> handTouchPoints = new List<Vector3>();
        private static bool HandTouchesCapsule(bool gunHand, Vector3 start, Vector3 end, float radius)
        {
            return HandDistanceToCapsule(gunHand, start, end) <= radius;
        }

        private static float HandDistanceToCapsule(bool gunHand, Vector3 start, Vector3 end)
        {
            if (VRGlobals.handsInteractionController == null)
                return DistanceToSegment(gunHand ? VRGlobals.vrPlayer.RightHand.transform.position : OffHandPalm(), start, end);
            // The hand you see: the arm drawing it (OnRightArm), not the controller
            VRGlobals.handsInteractionController.GetHandTouchPoints(OnRightArm(gunHand), handTouchPoints);
            float nearest = float.MaxValue;
            foreach (Vector3 point in handTouchPoints)
                nearest = Mathf.Min(nearest, DistanceToSegment(point, start, end));
            return nearest;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void GrabChargingHandle(FirearmController firearmController, Weapon weapon, bool gunHand = false, bool sideHandle = false)
        {
            if (!TryGetChargingHandle(firearmController, weapon))
                return;
            // During a press check (the off hand coming to rack it): put the slide back to rest before it's measured as the handle's rest
            if (PressCheckActive)
                EndPressCheck();

            // WeaponRoot -up is toward the muzzle, so +up is back toward the stock
            Vector3 backWorld = firearmController.WeaponRoot.up;
            ChargingHandle.grabbedKnob = ChargingHandle.foundHandleKnobs != null && !sideHandle ? ChargingHandle.NearestHandleKnob(gunHand, backWorld) : 0;
            handleCarrier = sideHandle ? null : ChargingHandle.foundHandleCarrier;
            if (sideHandle)
            {
                if (!FrameHandleOnSidePart(firearmController, backWorld))
                    return;
            }
            else if (handleCarrier != null)
            {
                ChargingHandle.FrameHandleOnCarrier(backWorld);
            }
            else
            {
                handleBone = ChargingHandle.foundHandleBone;
                handleView = ChargingHandle.foundHandleModel;
            }
            handleForFirearm = firearmController;
            ChargingHandle.handleRestLocalPosition = handleView.localPosition;
            ChargingHandle.handleAxisInSlot = handleBone.InverseTransformDirection(backWorld).normalized;
            ChargingHandle.handleInGunHand = gunHand;
            handlePull = 0f;
            handleFullyBack = false;
            HoldingChargingHandle = true;
            LatchHand(handleView, gunHand);
            // The wiggle turns the hand about where it holds the handle
            if (sideHandle && TryGetSideKnobPoint(firearmController, out Vector3 sideKnobPoint))
                SetLatchPivot(sideKnobPoint);
            else if (!sideHandle)
                SetLatchPivot(ChargingHandleGrabPoint(backWorld));
            ChargingHandle.handAtHandleGrabInSlot = handleBone.InverseTransformPoint(LatchedHandRig.position);
            StartHandleNotch(firearmController, backWorld);
            ChargingHandle.handleHandFingers = null;
            heldHandleTravel = ChargingHandle.handleTravel;
            LoadRecordedGrips();
            string family = AnimationFamily(firearmController);
            if (handleTravels.TryGetValue(family, out float familyTravel) && familyTravel > 0f)
                heldHandleTravel = familyTravel;
            if (!sideHandle)
                ChargingHandle.StartKnobFold(firearmController, family);
            handleHolds.TryGetValue(family, out HandleHold offHandHold);
            gunHandHandleHolds.TryGetValue(family, out HandleHold gunHandHold);
            // Chosen by arm, not by controller: with the gun in the left hand the off hand is the right hand, and racks with the right
            // hand's clip. The side handle's hold is the left hand's; the right hand takes the side handle where it grabs.
            bool rightArm = OnRightArm(gunHand);
            HandleHold handleHold = sideHandle ? (rightArm ? null : heldSideHandle.hold) : rightArm ? gunHandHold : offHandHold;
            // The handle travels as far whichever hand has it, and a gun's clips may only rack with one hand
            HandleHold otherHandHold = sideHandle ? null : rightArm ? offHandHold : gunHandHold;
            if (handleHold != null && handleHold.travel > 0f)
                heldHandleTravel = handleHold.travel;
            else if (otherHandHold != null && otherHandHold.travel > 0f)
                heldHandleTravel = otherHandHold.travel;
            if (handleCarrier != null && BoltLockedBack)
                heldHandleTravel = Mathf.Min(heldHandleTravel, ChargingHandle.handleLockedBackTravel);
            // Only the travel left to its full back from where it is now (a cocked open bolt: the PPSh's 2 cm)
            if (handleCarrier != null && ChargingHandle.handleCarrierAxis.sqrMagnitude > 1e-8f && handleFullBacks.TryGetValue(family, out SavedHandleFullBack carrierFullBack)
                && carrierFullBack.bone == handleCarrier.name)
            {
                Vector3 fullBackLocal = new Vector3(carrierFullBack.fullBack[0], carrierFullBack.fullBack[1], carrierFullBack.fullBack[2]);
                float room = Vector3.Dot(fullBackLocal - ChargingHandle.handleCarrierRestLocalPosition, ChargingHandle.handleCarrierAxis) / ChargingHandle.handleCarrierAxis.sqrMagnitude;
                heldHandleTravel = Mathf.Min(heldHandleTravel, Mathf.Max(room, ChargingHandle.minHandleTravel));
            }
            HandleHold grip = sideHandle ? handleHold : ChargingHandle.HandleGripFor(firearmController, rightArm, family, offHandHold, gunHandHold, backWorld);
            if (grip != null)
            {
                ChargingHandle.handleHandFingers = grip.fingers;
                SnapHandTo(handleView, handleBone.position + handleBone.rotation * grip.position, handleBone.rotation * Quaternion.Euler(grip.rotation));
            }
            BeginMalfunctionGrab(firearmController);
            StartMovedBolts(firearmController, backWorld);
            ChargingHandle.BeginChamberView(firearmController, weapon);

            if (debugLog)
            {
                FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
                Transform[] patrons = firearmController.Firearms._patronInWeaponTransforms;
                Transform patron = patrons != null && patrons.Length > 0 ? patrons[0] : null;
                LogAction($"[VRReload] grabbed the charging handle '{handleView.name}', bolt locked back: {BoltLockedBack}, chamber: {weapon.ChamberAmmoCount}, " +
                    $"catch layer: {firearmsAnimator.BOLT_CATCH_LAYER_INDEX}, idle position: {firearmsAnimator.HasParameter(AnimationControllerParametersTable.FLOAT_IDLEPOSITION)}, " +
                    $"round model shown: {(patron != null ? $"{patron.gameObject.activeSelf} ({patron.childCount} child)" : "no patron_in_weapon")}, gun hand: {gunHand}, hold from a clip: {grip != null}, " +
                    $"knob: {(ChargingHandle.foundHandleKnobs != null ? (ChargingHandle.grabbedKnob == 0 ? "left" : "right") : "one")}, carrier: {(handleCarrier != null ? handleCarrier.name : "none")}");
            }
            // A knob that folds out as it's taken (the MDR's paddles, the Spear's side handle) has its own sound (HandleOpen is a handle
            // grab clip on both). Otherwise the gun's grab sound plays (the stuck grab sound on a hard-slide malfunction).
            bool knobFolds = (ChargingHandle.foldingKnob != null && ChargingHandle.foldingKnobFor == firearmController) || heldSideHandle != null;
            if (!(knobFolds && PlayFirstGunSound(firearmController, HandleSoundHand, "HandleOpen")))
                PlayGrabSound(firearmController, HandleSoundHand);
            PulseHandleHand(0.3f, 0.04f);
        }

        private static void EnsureHandleFrame()
        {
            if (handleFrame == null || handleRider == null)
            {
                handleFrame = new GameObject("VRChargingHandleFrame").transform;
                handleRider = new GameObject("VRChargingHandleRider").transform;
                handleRider.SetParent(handleFrame, worldPositionStays: false);
            }
        }

        // 0 = closed, 1 = Tarkov's locked-back pose
        private static void SetBoltOpen(FirearmsAnimator firearmsAnimator, float open)
        {
            // Co-op: other players' copies of the gun mirror the layer while it's written (VRReloadController.Sync)
            boltOpenWritten = open;
            boltOpenWrittenFrame = Time.frameCount;
            IAnimator animator = firearmsAnimator?.Animator;
            if (animator == null)
                return;
            if (firearmsAnimator.BOLT_CATCH_LAYER_INDEX >= 0)
                animator.SetLayerWeight(firearmsAnimator.BOLT_CATCH_LAYER_INDEX, open);
            if (firearmsAnimator.HasParameter(AnimationControllerParametersTable.FLOAT_IDLEPOSITION))
                AnimationControllerParametersTable.SetIdlePosition(animator, open);
        }

        // The bolt goes where the gun's own catch flag says: forward, or held back by an empty magazine
        private static void RestoreBoltPose(FirearmController firearmController)
        {
            FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
            if (firearmsAnimator != null)
                SetBoltOpen(firearmsAnimator, firearmsAnimator.GetBoltCatch() ? 1f : 0f);
        }

        // The bolt going forward takes the round a magazine brought in on a closed bolt into the chamber, however it was released:
        // the handle let go after a full pull, or slapped (an MP5's out of its notch, which it entered on that same pull).
        private static bool BoltStripsRound(Weapon weapon)
        {
            if (!WaitingForRack(weapon) || weapon.GetCurrentMagazine() == null)
                return false;
            if (debugLog)
                LogAction("[VRReload] racked: the round goes into the chamber");
            weaponsWaitingForRack.Remove(weapon);
            return true;
        }

        // An empty magazine catches the bolt (the follower pushes the catch up); no magazine, or rounds left, it closes.
        // Same rule as Tarkov's RechamberOperation.FastForward. Not on a gun whose catch is its handle: nothing but the hand holds that back.
        private static bool BoltStaysBack(Weapon weapon)
        {
            Magazine magazine = weapon.GetCurrentMagazine();
            return weapon.IsBoltCatch && !weapon.ManualBoltCatch && weapon.ChamberAmmoCount == 0 && magazine != null && magazine.Count == 0;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // The grab point (green when the palm is in reach)
        private static void UpdateChargingHandleViz(FirearmController firearmController, Weapon weapon, bool show)
        {
            UpdateSideHandleViz(firearmController, weapon, show);
            bool hasHandle = show && TryGetChargingHandle(firearmController, weapon);
            if (!hasHandle || HoldingChargingHandle)
            {
                return;
            }
            Vector3 backWorld = firearmController.WeaponRoot.up;
            for (int zone = 0; zone < 2; zone++)
            {
                if (zone >= ChargingHandle.HandleZoneCount)
                {
                    continue;
                }
                Vector3 grabPoint = ChargingHandle.HandleZonePoint(zone, backWorld);
                Vector3 zoneEnd = grabPoint + backWorld * ChargingHandle.handleZoneBack;
                bool inReach = OffHandTouchesCapsule(grabPoint, zoneEnd, handleGrabRadius)
                    || (ChestHang.SupportOnly && HandTouchesCapsule(true, grabPoint, zoneEnd, handleGrabRadius));
                Color color = inReach ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.4f, 0f, 0.3f);
                if (zone == 0)
                    SetCapsuleZoneViz(grabPoint, zoneEnd, handleGrabRadius, color);
                else
                    SetCapsuleZoneViz(grabPoint, zoneEnd, handleGrabRadius, color);
            }
        }

        public static class ChargingHandle
        {
            public static float handleZoneBack = 0.05f;        // m the grab zone (a capsule) extends past that toward the stock
            [Tunable("Reload & handles", TunableKind.Tune, "Handle travel for guns without their own measured value.", Unit = "m")]
            public static float handleTravel = 0.08f;          // m the handle comes back
            public static float minHandleTravel = 0.01f;       // m a handle already at (or past) its full back can still move, so the pull registers
            [Tunable("Reload & handles", TunableKind.Tune, "How far a handle can still come back with the bolt already locked back.", Unit = "m")]
            public static float handleLockedBackTravel = 0.02f; // m of travel left for a handle on the bolt when the bolt is already locked back
            public static float underGunPullGain = 1.6f;        // off hand on a handle on the bolt: handle travel per controller travel (1 = one to one).
                                                                // That hand reaches under the gun and would hit the other controller over a full pull.
            public static float boltLockOnDistance = 0.003f;   // m a part has to come back before it's taken as the bolt the round follows
            public static bool HandleInGunHand => HoldingChargingHandle && handleInGunHand;

            internal static bool handleInGunHand;                // which hand took it; only meaningful while holding it
            // The gun hand can hold the handle with its own pin instead of the shared hand latch (PinGunHandOnHandle)
            private static Transform handleHandPin;
            private static RootMotion.FinalIK.LimbIK handleHandArm;
            private static bool handleHandPinned;
            internal static Vector3 handleRestLocalPosition;
            internal static Vector3 handleAxisInSlot;            // back toward the stock, in the slot bone's frame
            internal static Vector3 handAtHandleGrabInSlot;      // the controller position when the handle was grabbed
            private static bool chamberShownEmpty;              // the pull threw the round out, or it's still in the magazine: empty until the bolt goes forward
            private static bool chamberBoneWasActive;
            private static Vector3 chamberedRoundRestLocalPosition;
            private static Transform[] boltParts;               // the gun's parts and where they sat with the bolt closed
            private static Vector3[] boltPartsRestInChamber;
            private static Transform boltPart;                  // the one found moving back with the bolt
            private static Vector3 boltPartRestInChamber;
            internal static Vector3 handleCarrierRestLocalPosition;
            internal static Vector3 handleCarrierAxis;           // one metre back, in the carrier's parent's frame

            // The gun's handle, looked up again every couple of seconds (mods only change in the inventory)
            internal static FirearmController handleSlotFor;
            internal static float handleSlotLookupTime;
            internal static Transform foundHandleBone;           // the mod_charge bone
            internal static Transform foundHandleModel;          // the handle model in its slot, null with none fitted
            internal static Transform foundHandleCarrier;        // the part that bone sits on when that part is the bolt, else null
            internal static FirearmController rigBonesFor;
            internal static readonly Dictionary<string, Transform> rigBones = new Dictionary<string, Transform>();
            private static Transform handleMeshesFor;
            private static MeshFilter[] handleMeshes;
            internal static Vector3? foundHandleGrabLocal;
            internal static Vector3[] foundHandleKnobs;
            internal static int grabbedKnob;                     // which of those the hand took, while holding
            [Tunable("Reload & handles", TunableKind.Tune, "How far off the gun's centre a single handle can sit and still give the right hand the left hand's grip, "
                + "mirrored (an AR's T). 0: only knob pairs like the MDR's.", Unit = "m")]
            public static float handleMirrorPlaneTolerance = 0.015f;   // m a grab point can sit off the gun's centre plane and still take the left hand's hold mirrored (a T)
            [Tunable("Reload & handles", TunableKind.Tune, "How long a folding knob (the MDR's paddles) takes to swing out as you grab it, and back.", Unit = "s")]
            public static float knobFoldTime = 0.1f;            // s a knob takes to fold out as it's taken, and back
            internal static Transform foldingKnob;
            private static Quaternion foldingKnobRest;
            private static KnobFold foldingKnobFold;
            internal static FirearmController foldingKnobFor;
            private static float knobFoldShown;
            private static bool knobFoldWasOut;                 // held out last frame (its fold back plays a sound)
            internal static Vector3[] handleHandFingers;         // null = no clip pose for this gun; the fingers use the magazine pull's pinch
            internal static bool insertNeedsRack;                // the insert under way went in on a closed bolt and an empty chamber

            // Tarkov's draw (SpawnOperation.Start) chambers a round into an empty chamber, and the animator picks its racking draw while it
            // still reads the chamber as empty (SPAWN -> OUT TO IDLE ARM on AmmoInChamber < 1). Placing the round now makes it pick the plain
            // draw, and the round then waits for the hand, like one a magazine brought in. A gun the hand can't rack keeps Tarkov's draw.
            // This doesn't use TryGetChargingHandle: the gun isn't on the rig yet, and TryGetChargingHandle caches what it finds.
            public static void TakeRackOutOfDraw(FirearmController.SpawnOperation spawnOperation)
            {
                try
                {
                    FirearmController firearmController = spawnOperation.Controller;
                    if (!enabled || spawnOperation._ammoFromMagToChamber == null || firearmController == null
                        || firearmController._player == null || !firearmController._player.IsYourPlayer || !VRSettings.GetManualReloadOn())
                        return;
                    Weapon weapon = firearmController.Item;
                    if (weapon == null || !CanBeRackedByHand(firearmController, weapon))
                        return;
                    spawnOperation.OnAddAmmoInChamber();
                    // The clip's own event, if one still comes, has nothing left to do
                    spawnOperation._ammoFromMagToChamber = null;
                    weaponsWaitingForRack.Add(weapon);
                    LogAction($"[VRReload] {weapon.LocalizedShortName()}: drawn on an empty chamber, the round waits for the handle");
                }
                catch (System.Exception exception)
                {
                    Plugin.MyLog.LogError($"[VRReload] taking the rack out of the draw failed, Tarkov's stays: {exception}");
                }
            }

            private static bool CanBeRackedByHand(FirearmController firearmController, Weapon weapon)
            {
                // A bolt-action's bolt (VRReloadController.Bolt): its draw on an empty chamber leaves the round for the hand too
                if (weapon.BoltAction)
                    return TryGetBolt(firearmController, out _);
                if (weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazine && weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport)
                    return false;
                LoadRecordedGrips();
                string family = AnimationFamily(firearmController);
                if (slideBones.ContainsKey(family) || handleHolds.ContainsKey(family) || handleBoneNames.ContainsKey(family))
                    return true;
                // A handle on the bolt carrier that only the gun hand's clips rack, with its add-on handle slot empty (AK-50).
                // The hand takes the carrier's bone. On a bolt-action that bone is the bolt, which isn't worked here.
                if (gunHandHandleHolds.ContainsKey(family) && !weapon.BoltAction)
                    return true;
                foreach (Slot slot in weapon.AllSlots)
                {
                    if (slot.ID == EWeaponModType.mod_charge.ToString() && slot.ContainedItem != null)
                        return true;
                }
                return false;
            }

            internal static Vector3 HandleKnobPoint(int knob)
            {
                return foundHandleBone.position + foundHandleBone.rotation * foundHandleKnobs[Mathf.Clamp(knob, 0, foundHandleKnobs.Length - 1)];
            }

            // The grab zones: at the grab point, or on each of a pair of knobs
            internal static int HandleZoneCount => foundHandleKnobs != null ? foundHandleKnobs.Length : 1;

            internal static Vector3 HandleZonePoint(int zone, Vector3 backWorld)
            {
                return foundHandleKnobs != null ? HandleKnobPoint(zone) : ChargingHandleGrabPoint(backWorld);
            }

            // Which knob of a pair the hand is nearest
            internal static int NearestHandleKnob(bool gunHand, Vector3 backWorld)
            {
                int nearest = 0;
                float nearestDistance = float.MaxValue;
                for (int zone = 0; zone < HandleZoneCount; zone++)
                {
                    Vector3 grabPoint = HandleZonePoint(zone, backWorld);
                    float distance = HandDistanceToCapsule(gunHand, grabPoint, grabPoint + backWorld * handleZoneBack);
                    if (distance < nearestDistance)
                    {
                        nearestDistance = distance;
                        nearest = zone;
                    }
                }
                return nearest;
            }

            // The back end of the handle (the T on an AR): the furthest point of its meshes' boxes toward the stock. The handle model is a
            // long bar reaching forward into the receiver, so its middle is nowhere near where you grab it.
            internal static Vector3 HandleGrabPoint(Transform view, Vector3 backWorld)
            {
                if (handleMeshesFor != view)
                {
                    handleMeshesFor = view;
                    handleMeshes = view.GetComponentsInChildren<MeshFilter>();
                }
                Vector3 grabPoint = view.position;
                float furthestBack = float.MinValue;
                foreach (MeshFilter meshFilter in handleMeshes)
                {
                    if (meshFilter == null)
                        continue;
                    if (meshFilter.sharedMesh == null)
                        continue;
                    Transform meshTransform = meshFilter.transform;
                    Bounds bounds = meshFilter.sharedMesh.bounds;
                    Vector3 center = meshTransform.TransformPoint(bounds.center);
                    Vector3 halfX = meshTransform.TransformVector(new Vector3(bounds.extents.x, 0f, 0f));
                    Vector3 halfY = meshTransform.TransformVector(new Vector3(0f, bounds.extents.y, 0f));
                    Vector3 halfZ = meshTransform.TransformVector(new Vector3(0f, 0f, bounds.extents.z));
                    // How far the box reaches back from its center
                    float reachBack = Mathf.Abs(Vector3.Dot(halfX, backWorld)) + Mathf.Abs(Vector3.Dot(halfY, backWorld)) + Mathf.Abs(Vector3.Dot(halfZ, backWorld));
                    float back = Vector3.Dot(center, backWorld) + reachBack;
                    if (back > furthestBack)
                    {
                        furthestBack = back;
                        grabPoint = center + backWorld * reachBack;
                    }
                }
                return grabPoint;
            }

            // The hold this arm takes the handle with; null = the hand stays where it grabbed. It is the arm's own clip hold the hand is turned
            // closest to, or the over-the-top hold when the palm is above the knob and the gun has one. With no right-hand clip, the left hand's
            // hold is mirrored across the handle where that maps the handle onto itself (a T on the gun's centre plane, or a pair of knobs).
            // With a pair of knobs the left hand's holds are on the left one and the right hand's on the right; the other knob is taken where
            // the hand is.
            internal static HandleHold HandleGripFor(FirearmController firearmController, bool rightArm, string family, HandleHold offHandHold, HandleHold gunHandHold, Vector3 backWorld)
            {
                if (foundHandleKnobs != null && foundHandleKnobs.Length > 1 && grabbedKnob != (rightArm ? 1 : 0))
                    return null;
                HandleHold main = rightArm ? gunHandHold : offHandHold;
                HandleHold overTop = null;
                List<HandleHold> others = null;
                if (main != null)
                {
                    (rightArm ? gunHandHandleAlternates : handleAlternates).TryGetValue(family, out others);
                    if (!rightArm)
                        overTopHandleHolds.TryGetValue(family, out overTop);
                }
                else if (rightArm && offHandHold != null && TryHandleMirrorPlane(backWorld, out Vector3 normal, out Vector3 point))
                {
                    main = MirroredHandleHold(offHandHold, normal, point);
                    if (overTopHandleHolds.TryGetValue(family, out HandleHold leftOverTop))
                        overTop = MirroredHandleHold(leftOverTop, normal, point);
                    if (handleAlternates.TryGetValue(family, out List<HandleHold> leftOthers))
                    {
                        others = new List<HandleHold>();
                        foreach (HandleHold other in leftOthers)
                        {
                            HandleHold mirrored = MirroredHandleHold(other, normal, point);
                            if (mirrored != null)
                                others.Add(mirrored);
                        }
                    }
                    if (debugLog)
                        LogAction($"[VRReload] charging handle: the left hand's hold mirrored for the right ({(main != null ? "ok" : "no hands mesh yet")})");
                }
                if (main == null)
                    return null;
                if (overTop != null && PalmAboveHandle(firearmController, rightArm, backWorld))
                {
                    if (debugLog)
                        LogAction("[VRReload] charging handle: over-the-top grip (palm above the knob)");
                    return overTop;
                }
                return HoldHandIsTurnedTo(main, others);
            }

            // True if the palm is above the grab point (the gun's up is WeaponRoot +z, the sights' side)
            private static bool PalmAboveHandle(FirearmController firearmController, bool rightArm, Vector3 backWorld)
            {
                Vector3 palm = VRGlobals.handsInteractionController != null ? VRGlobals.handsInteractionController.PalmOrigin(dominant: rightArm) : LatchedHandRig.position;
                return Vector3.Dot(palm - ChargingHandleGrabPoint(backWorld), firearmController.WeaponRoot.forward) > 0f;
            }

            // The plane that mirrors the handle onto itself, in the hold frame (handleBone): halfway between a pair of knobs, or the gun's
            // centre plane if the grab point lies on it
            private static bool TryHandleMirrorPlane(Vector3 backWorld, out Vector3 normal, out Vector3 point)
            {
                if (foundHandleKnobs != null && foundHandleKnobs.Length > 1)
                {
                    normal = (foundHandleKnobs[1] - foundHandleKnobs[0]).normalized;
                    point = (foundHandleKnobs[0] + foundHandleKnobs[1]) * 0.5f;
                    return true;
                }
                normal = Vector3.right;
                point = Vector3.zero;
                if (!TarkovVR.Patches.Core.Player.RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                    return false;
                Quaternion toFrame = Quaternion.Inverse(handleBone.rotation);
                normal = (toFrame * planeNormal).normalized;
                point = toFrame * (planePoint - handleBone.position);
                Vector3 grabPoint = toFrame * (ChargingHandleGrabPoint(backWorld) - handleBone.position);
                return Mathf.Abs(Vector3.Dot(grabPoint - point, normal)) <= handleMirrorPlaneTolerance;
            }

            // Turns a left-hand hold into its mirror image for the right hand through that plane: the palm is reflected and turned into the
            // right palm's frame (HandMirror.TryMirrorPalm), and the fingers are mirrored. Returns null until HandMirror has the hands mesh.
            private static HandleHold MirroredHandleHold(HandleHold hold, Vector3 normal, Vector3 point)
            {
                if (hold == null || !TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(Quaternion.Euler(hold.rotation), normal, rightToLeft: false, out Quaternion rotation))
                    return null;
                return new HandleHold
                {
                    position = hold.position - 2f * Vector3.Dot(hold.position - point, normal) * normal,
                    rotation = rotation.eulerAngles,
                    fingers = hold.fingers != null ? MirroredPose(hold.fingers, rightToLeft: false, new Vector3[16]) : null,
                    travel = hold.travel,
                };
            }

            private static HandleHold HoldHandIsTurnedTo(HandleHold main, List<HandleHold> others)
            {
                HandleHold closest = main;
                if (others == null || others.Count == 0)
                    return closest;
                Quaternion handRotation = LatchedHandRig.rotation;
                float smallestTurn = Quaternion.Angle(handRotation, handleBone.rotation * Quaternion.Euler(main.rotation));
                foreach (HandleHold other in others)
                {
                    float turn = Quaternion.Angle(handRotation, handleBone.rotation * Quaternion.Euler(other.rotation));
                    if (turn < smallestTurn)
                    {
                        smallestTurn = turn;
                        closest = other;
                    }
                }
                if (debugLog)
                    LogAction($"[VRReload] charging handle: {(closest == main ? "main" : "other")} grip, {smallestTurn:0} deg from the hand");
                return closest;
            }

            internal static void StartKnobFold(FirearmController firearmController, string family)
            {
                if (!handleKnobFolds.TryGetValue(family, out KnobFold[] folds) || grabbedKnob >= folds.Length)
                    return;
                Transform knob = FindRigBone(firearmController, folds[grabbedKnob].bone);
                if (knob == null)
                    return;
                if (knob != foldingKnob)
                {
                    PutFoldingKnobBack();
                    foldingKnob = knob;
                    foldingKnobRest = knob.localRotation;
                    knobFoldShown = 0f;
                }
                foldingKnobFold = folds[grabbedKnob];
                foldingKnobFor = firearmController;
                knobFoldWasOut = false;
            }

            private static void PutFoldingKnobBack()
            {
                if (foldingKnob != null)
                    foldingKnob.localRotation = foldingKnobRest;
                foldingKnob = null;
                foldingKnobFold = null;
                foldingKnobFor = null;
                knobFoldShown = 0f;
                knobFoldWasOut = false;
            }

            // Runs in LateUpdate, after the animator: the knob is folded out while the handle is held, and back after
            public static void ApplyKnobFold()
            {
                if (foldingKnob == null || foldingKnobFold == null)
                    return;
                // Another gun in the hands: put the old one's knob straight back
                if (foldingKnobFor != VRGlobals.firearmController)
                {
                    PutFoldingKnobBack();
                    return;
                }
                bool folded = HoldingChargingHandle && handleForFirearm == foldingKnobFor;
                // Let go: it folds back with its own sound (the MDR's HandleClose)
                if (knobFoldWasOut && !folded)
                    PlayFirstGunSound(foldingKnobFor, SoundHand.GunBody, "HandleClose");
                knobFoldWasOut = folded;
                knobFoldShown = Mathf.MoveTowards(knobFoldShown, folded ? 1f : 0f, Time.deltaTime / Mathf.Max(knobFoldTime, 0.001f));
                foldingKnob.localRotation = foldingKnobRest * Quaternion.AngleAxis(foldingKnobFold.angle * Mathf.SmoothStep(0f, 1f, knobFoldShown), foldingKnobFold.axis);
                if (!folded && knobFoldShown <= 0f)
                    PutFoldingKnobBack();
            }

            // The carrier is animated and we move it, so the pull can't be measured against it. Instead two transforms of ours sit on the gun's
            // body where the mod_charge bone is now. The hand follows the second one, and the carrier follows it in ApplyHandleCarrier.
            internal static void FrameHandleOnCarrier(Vector3 backWorld)
            {
                EnsureHandleFrame();
                Transform gunBody = handleCarrier.parent;
                handleFrame.SetParent(gunBody, worldPositionStays: false);
                handleFrame.SetPositionAndRotation(foundHandleBone.position, foundHandleBone.rotation);
                handleRider.localPosition = Vector3.zero;
                handleRider.localRotation = Quaternion.identity;
                handleBone = handleFrame;
                handleView = handleRider;
                handleCarrierRestLocalPosition = handleCarrier.localPosition;
                handleCarrierAxis = gunBody.InverseTransformVector(backWorld);
            }

            // Runs in LateUpdate, after the animator has posed the carrier this frame
            public static void ApplyHandleCarrier()
            {
                if (!HoldingChargingHandle)
                    return;
                ApplyHandleMovedParts();
                if (handleCarrier == null)
                    return;
                handleCarrier.localPosition = handleCarrierRestLocalPosition + handleCarrierAxis * handlePull;
                ApplyHandleNotch();
            }

            internal static bool ChargingHandleHoldIsStillValid()
            {
                if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null)
                    return false;
                if (VRGlobals.firearmController == null || VRGlobals.firearmController != handleForFirearm)
                    return false;
                // The gun hand keeps the handle only while the off hand holds the gun (SupportOnly), or while the gun stays on this hand after
                // the off hand let go too (ChestHang.HandleHeld). Otherwise the grip was taken back some other way.
                if (handleInGunHand && !ChestHang.SupportOnly && !ChestHang.HandleHeld)
                    return false;
                // The gun went from one hand to the other, so that arm isn't this hand's any more. Not checked when pinned: the latch then
                // belongs to the other hand.
                if (!handleHandPinned && latchedOnRightArm != OnRightArm(handleInGunHand))
                    return false;
                // Our own operations may run for a frame while the handle is back: the rechamber, or a magazine out or in (an "Iraqi" reload)
                if (!(handleForFirearm.CurrentOperation is FirearmController.Idling) && expectedOperation == ExpectedOperation.None)
                    return false;
                // The M3 switched to pump mode meanwhile
                if (ShotgunInPumpMode(handleForFirearm))
                    return false;
                return handleView != null && handleBone != null;
            }

            // The controller's travel along the barrel moves the handle back; sideways it can't go
            internal static void UpdateChargingHandle(bool triggerReleased)
            {
                Vector3 axis = handleBone.TransformDirection(handleAxisInSlot).normalized;
                // The hand holding it. When pinned, the latch may belong to the other hand by now.
                Transform handRig = handleHandPinned ? HandRig(handleInGunHand) : LatchedHandRig;
                bool handOnRightArm = handleHandPinned ? OnRightArm(handleInGunHand) : latchedOnRightArm;
                Vector3 handDelta = handRig.position - handleBone.TransformPoint(handAtHandleGrabInSlot);
                float handAlongTrack = Vector3.Dot(handDelta, axis);

                // Only the left hand reaches under the gun to a handle on the bolt (the right one is on that side)
                float pullGain = handleCarrier != null && handleCarrier != foundHandleBone && !handOnRightArm ? underGunPullGain : 1f;
                // A hard slide holds it until it breaks free (VRReloadController.Malfunctions)
                handlePull = StuckPull(handleForFirearm, Mathf.Clamp(handAlongTrack * pullGain, 0f, heldHandleTravel), HandleSoundHand);
                TrackHandlePull(handlePull);
                handleView.position = handleBone.TransformPoint(handleRestLocalPosition) + axis * handlePull;
                UpdateHandleNotch();

                if (!handleFullyBack && handlePull >= heldHandleTravel * handleFullPull)
                {
                    handleFullyBack = true;
                    BoltComesBack(handleForFirearm);
                }

                // Done after the rechamber above: skipping it puts the bolt pose back to the catch flag, which would flash the bolt closed this frame
                FirearmsAnimator firearmsAnimator = handleForFirearm.FirearmsAnimator;
                float boltOpen = Mathf.Clamp01(handlePull / (heldHandleTravel * handleFullPull));
                SetBoltOpen(firearmsAnimator, firearmsAnimator.GetBoltCatch() ? 1f : boltOpen);

                // Only releasing the trigger lets go (there is no distance release). Once the handle has been all the way back, letting go
                // slams the bolt forward, whatever the handle does after.
                if (triggerReleased)
                    LetGoOfChargingHandle(snapForward: handleFullyBack);
            }

            // All the way back: a round in the chamber comes out now, as the bolt comes back, and the next one goes in
            private static void BoltComesBack(FirearmController firearmController)
            {
                Weapon weapon = firearmController.Item;
                PlayFirstGunSound(firearmController, HandleSoundHand, "HandleOut", "SlideOut", "BoltOut");
                PulseHandleHand(0.5f, 0.04f);
                // A belt gun without a chamber (the RPD) is cocked now (VRReloadController.Belt)
                BeltGunCharged(firearmController);
                // A malfunction: cleared as the bolt goes home, by Tarkov's repair, which throws the round out itself
                if (MalfunctionRackedBack(firearmController))
                {
                    chamberShownEmpty = true;
                    return;
                }
                // An empty magazine's follower pushes the catch up under the bolt. With a round to throw out, Tarkov's
                // rechamber below sets it instead.
                if (!firearmController.FirearmsAnimator.GetBoltCatch() && BoltStaysBack(weapon))
                {
                    if (debugLog)
                        LogAction("[VRReload] empty magazine: the bolt locks back");
                    PlayVariedGunSound(firearmController, SoundHand.GunBody, CATCH_ON_SOUNDS);
                    firearmController.FirearmsAnimator.SetBoltCatch(true);
                    return;
                }
                // Nothing in the chamber to throw out (a round still in the magazine only goes in as the bolt closes)
                if (firearmController.FirearmsAnimator.GetBoltCatch() || weapon.ChamberAmmoCount == 0 || WaitingForRack(weapon))
                    return;
                if (!(firearmController.CurrentOperation is FirearmController.Idling))
                    return;
                if (debugLog)
                    LogAction("[VRReload] rechamber: the chambered round comes out");
                ExpectOperation(ExpectedOperation.Rechamber, firearmController);
                VRGlobals.player.InventoryController.RechamberWeapon(weapon);
                TryForceExpectedOperation();
                chamberShownEmpty = true;
            }

            // Moves the gun hand off the shared hand latch onto its own pin on the handle, at the latch's pose (ChestHang.HandleHeld, an
            // "Iraqi" reload). The off hand's magazine catching the track takes that latch, and sharing one latch would move the gun hand's
            // arm onto the magazine and break the handle hold. The pin follows the handle model like the latch did.
            internal static void PinGunHandOnHandle()
            {
                if (handleHandPinned || !HandleInGunHand || !handLatched || handLatchReleasing || !latchedHandIsGunHand || handLatch == null || handleView == null)
                    return;
                RootMotion.FinalIK.LimbIK armIk = ArmIk(latchedOnRightArm);
                if (armIk == null)
                    return;
                if (handleHandPin == null)
                    handleHandPin = new GameObject("VRReloadHandleHandPin").transform;
                handleHandPin.SetParent(handleView, worldPositionStays: false);
                handleHandPin.SetPositionAndRotation(handLatch.position, handLatch.rotation);
                HandTargets.Set(armIk, handleHandPin, HandOwner.ChargingHandle);
                handleHandArm = armIk;
                handleHandPinned = true;
                // Free the shared latch for the other hand where it is (the arm already aims at the pin)
                handLatch.SetParent(null, worldPositionStays: true);
                handLatched = false;
                handLatchReleasing = false;
                handLatchSnapping = false;
                if (debugLog)
                    Plugin.MyLog.LogInfo("[VRReload] gun hand pinned on the handle: the hand latch free for the off hand");
            }

            private static void UnpinGunHand()
            {
                if (!handleHandPinned)
                    return;
                handleHandPinned = false;
                if (handleHandArm != null && handleHandArm.solver.target == handleHandPin)
                    HandTargets.Set(handleHandArm, HandRig(gunHand: true), HandOwner.ChargingHandle);
                handleHandArm = null;
                if (handleHandPin != null)
                    handleHandPin.SetParent(null, worldPositionStays: false);
            }

            internal static void LetGoOfChargingHandle(bool snapForward)
            {
                // How hard the spring drives it home (the jolt of the slam below) depends on where it's let go
                float fullPull = heldHandleTravel * handleFullPull;
                float letGoPull = fullPull > 1e-4f ? Mathf.Clamp01(HandlePullAtLetGo(handlePull) / fullPull) : 1f;
                handleSlamFraction = letGoPull;
                // Release the hand first, where it is, so it doesn't snap forward with the handle. A pinned hand releases its own pin (the
                // latch is the other hand's).
                if (handleHandPinned)
                    UnpinGunHand();
                else
                    ReleaseHandLatch();
                EndChamberView();
                bool staysInNotch = EndHandleNotch();
                EndHandleMovedParts();
                if (handleView != null)
                    handleView.localPosition = handleRestLocalPosition;
                if (handleCarrier != null)
                    handleCarrier.localPosition = handleCarrierRestLocalPosition;
                handleCarrier = null;
                if (handleFrame != null)
                    handleFrame.SetParent(null, worldPositionStays: false);
                HoldingChargingHandle = false;
                FirearmController firearmController = handleForFirearm;
                handleForFirearm = null;
                handleBone = null;
                handleView = null;

                if (firearmController == null)
                {
                    EndMalfunctionGrab(null);
                    return;
                }
                if (firearmController != VRGlobals.firearmController)
                {
                    RestoreBoltPose(firearmController);
                    EndMalfunctionGrab(firearmController);
                    return;
                }
                // Racked back on a malfunction: the bolt goes home and that clears it (a notch doesn't hold it)
                if (snapForward && malfunctionRacked && MalfunctionClearsByHand(firearmController))
                    RackClearsMalfunction(firearmController);
                // The MDR's deflector shut over a failure to eject or feed: the bolt goes back onto it, nothing cleared
                else if (snapForward && malfunctionRackBlocked)
                    PlayFirstGunSound(firearmController, SoundHand.GunBody, "HandleIn", "SlideIn");
                else if (staysInNotch)
                    firearmController.FirearmsAnimator.SetBoltCatch(true);
                else if (snapForward)
                    BoltSlamsForward(firearmController);
                else
                {
                    // Let go part way: it springs home from there. A gun with no handle sound plays its bolt's (the AK's handle is its carrier),
                    // but only if it came back some way: releasing a grab isn't a bolt hitting home.
                    bool cameBack = letGoPull >= partPullSoundMin;
                    if (!PlayFirstGunSound(firearmController, HandleSoundHand, "HandleIn", "SlideIn") && cameBack)
                        PlayFirstGunSound(firearmController, HandleSoundHand, "BoltIn");
                    if (cameBack)
                        BoltSlamJolt(firearmController, handleSlamFraction, "handle let go part way");
                }
                RestoreBoltPose(firearmController);
                EndMalfunctionGrab(firearmController);
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------

            // Shows the chambered round only for a bolt this pull opens. A bolt already locked back (a magazine swapped into an empty gun)
            // has its next round counted in the chamber by Tarkov's insert, but it is still in the magazine until the bolt goes forward.
            // The same round under a bolt that was forward (WaitingForRack) is hidden while the bolt opens.
            internal static void BeginChamberView(FirearmController firearmController, Weapon weapon)
            {
                chamberShownEmpty = WaitingForRack(weapon);
                Transform[] chamberBones = firearmController.Firearms._patronInWeaponTransforms;
                chamberBone = chamberBones != null && chamberBones.Length > 0 ? chamberBones[0] : null;
                showingChamber = chamberBone != null
                    && !firearmController.FirearmsAnimator.GetBoltCatch() && weapon.ChamberAmmoCount > 0;
                if (!showingChamber)
                {
                    chamberBone = null;
                    chamberedRound = null;
                    return;
                }
                chamberBoneWasActive = chamberBone.gameObject.activeSelf;
                chamberedRound = chamberBone.childCount > 0 ? chamberBone.GetChild(0) : null;
                if (chamberedRound != null)
                    chamberedRoundRestLocalPosition = chamberedRound.localPosition;
                boltParts = null;
                boltPart = null;
            }

            // Runs in LateUpdate, after the animator has posed the bolt this frame (read in Update it lags a frame behind).
            // The round moves with the bolt face. The model itself isn't animated, so its offset stays until the next write.
            // Hidden once the rechamber has thrown it out (its model goes back to Tarkov's pool), and while it's still in the magazine.
            public static void ApplyChamberView()
            {
                if (!showingChamber || chamberBone == null || handleForFirearm == null)
                    return;
                if (chamberShownEmpty)
                {
                    chamberBone.gameObject.SetActive(false);
                    return;
                }
                chamberBone.gameObject.SetActive(true);
                if (chamberedRound == null || chamberedRound.parent != chamberBone)
                    return;
                Transform weaponRoot = handleForFirearm.WeaponRoot;
                if (boltParts == null)
                    RecordBoltParts(weaponRoot);
                chamberedRound.localPosition = chamberedRoundRestLocalPosition + BoltMovementInChamber(weaponRoot.up);
            }

            // Records every part of the gun and where it sits in the chamber bone's frame with the bolt closed (the grab's first frame).
            // The chamber frame is used, not the gun's root: the receiver sways against the root with the idle breathing and the chamber
            // sways with it, so only the bolt's own travel is left (measured against the root, the round bobs).
            // Left out: the handle and the round (we move those), the gun's own arm and camera bones, and its hand markers and elbow
            // goals (every clip and the walk move those, so the round would follow a hand).
            private static void RecordBoltParts(Transform weaponRoot)
            {
                Transform[] parts = weaponRoot.GetComponentsInChildren<Transform>();
                int kept = 0;
                for (int index = 0; index < parts.Length; index++)
                {
                    Transform part = parts[index];
                    if (part == weaponRoot || part.IsChildOf(handleView) || part.IsChildOf(chamberBone) || !MayBeTheBolt(part.name))
                        continue;
                    parts[kept++] = part;
                }
                boltParts = new Transform[kept];
                boltPartsRestInChamber = new Vector3[kept];
                for (int index = 0; index < kept; index++)
                {
                    boltParts[index] = parts[index];
                    boltPartsRestInChamber[index] = chamberBone.InverseTransformPoint(parts[index].position);
                }
            }

            private static bool MayBeTheBolt(string name)
            {
                return name.IndexOf("human", System.StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("camera", System.StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("marker", System.StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("bend_goal", System.StringComparison.OrdinalIgnoreCase) < 0;
            }

            // How far the round comes back: only the bolt's travel along the bore (a round in the chamber can't move any other way),
            // and nothing before the handle moves. In the chamber bone's frame, which is the round's.
            private static Vector3 BoltMovementInChamber(Vector3 backWorld)
            {
                if (handlePull <= 0f)
                    return Vector3.zero;
                Vector3 boreBack = chamberBone.InverseTransformVector(backWorld).normalized;
                return boreBack * Mathf.Max(0f, Vector3.Dot(BoltPartMovement(backWorld), boreBack));
            }

            // The bolt is the part that has come furthest back along the barrel (the locked-back pose moves the whole bolt
            // group together). Once it is clearly moving, that part alone is followed.
            private static Vector3 BoltPartMovement(Vector3 backWorld)
            {
                if (boltPart == null)
                {
                    float furthestBack = 0f;
                    int furthestIndex = -1;
                    for (int index = 0; index < boltParts.Length; index++)
                    {
                        if (boltParts[index] == null)
                            continue;
                        Vector3 move = chamberBone.InverseTransformPoint(boltParts[index].position) - boltPartsRestInChamber[index];
                        float back = Vector3.Dot(chamberBone.TransformVector(move), backWorld);
                        if (back > furthestBack)
                        {
                            furthestBack = back;
                            furthestIndex = index;
                        }
                    }
                    if (furthestIndex < 0)
                        return Vector3.zero;
                    Vector3 furthestMove = chamberBone.InverseTransformPoint(boltParts[furthestIndex].position) - boltPartsRestInChamber[furthestIndex];
                    if (furthestBack < boltLockOnDistance || handlePull <= 0f)
                        return furthestMove;
                    boltPart = boltParts[furthestIndex];
                    boltPartRestInChamber = boltPartsRestInChamber[furthestIndex];
                    if (debugLog)
                        LogAction($"[VRReload] the round rides '{boltPart.name}' (parent '{(boltPart.parent != null ? boltPart.parent.name : "-")}')");
                }
                return chamberBone.InverseTransformPoint(boltPart.position) - boltPartRestInChamber;
            }

            private static void EndChamberView()
            {
                if (showingChamber && chamberBone != null)
                {
                    chamberBone.gameObject.SetActive(chamberBoneWasActive);
                    if (chamberedRound != null && chamberedRound.parent == chamberBone)
                        chamberedRound.localPosition = chamberedRoundRestLocalPosition;
                }
                showingChamber = false;
                chamberBone = null;
                chamberedRound = null;
                boltParts = null;
                boltPart = null;
            }

            // Let go from a full pull: a bolt locked back closes (unless an empty magazine holds it), and a round still in the
            // magazine goes into the chamber. With no magazine in there's nothing to strip: it keeps waiting.
            private static void BoltSlamsForward(FirearmController firearmController)
            {
                Weapon weapon = firearmController.Item;
                // Held by an empty magazine the bolt only comes onto the catch: no slam
                bool heldOnCatch = firearmController.FirearmsAnimator.GetBoltCatch() && BoltStaysBack(weapon);
                // The gun's own slam, felt by the hands on its body: the hand that let go of the handle isn't on it any more
                if (!heldOnCatch)
                    PlayFirstGunSound(firearmController, SoundHand.GunBody, "BoltIn", "SlideIn");
                PlayGunSound(firearmController, "HandleIn", SoundHand.GunBody);
                Pulse(SoundHand.GunBody, heldOnCatch ? 0.4f : 0.8f, 0.06f);
                BoltSlamJolt(firearmController, heldOnCatch ? handleSlamFraction * heldOnCatchJolt : handleSlamFraction, heldOnCatch ? "bolt onto the catch" : "bolt slammed home");
                // A tube shotgun's shell has no model in the chamber until it's shown (the load put it there in the inventory only)
                if (BoltStripsRound(weapon) && HandsLoadShotgun(firearmController) && firearmController.Firearms != null && !firearmController.Firearms.HasPatronInWeapon())
                    ShowChamberedRound(firearmController);
                if (firearmController.FirearmsAnimator.GetBoltCatch())
                {
                    if (debugLog)
                        LogAction($"[VRReload] bolt released, chamber: {weapon.ChamberAmmoCount}");
                    firearmController.FirearmsAnimator.SetBoltCatch(BoltStaysBack(weapon));
                }
            }
        }
    }
}
