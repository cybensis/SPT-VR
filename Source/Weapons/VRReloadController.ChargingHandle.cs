using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The charging handle: the off-hand trigger takes it, the hand pulls it straight back along the barrel on a track
    // (locked on it, the handle model moving with it) and lets go of the trigger. The gun hand can do the same with its own
    // trigger once it has let go of the grip with the off hand holding the gun up. Like the real thing:
    //  - pulled all the way back with a round in the chamber -> Tarkov's own rechamber right then: that round is thrown
    //    out and the next one chambered as the bolt comes back
    //  - let go from a full pull -> the bolt slams forward; a bolt locked back (a magazine swapped by hand into an empty
    //    gun) closes. Tarkov already put the round in the chamber when the magazine went in; it has no operation that
    //    chambers on its own. Same for a magazine put into a gun with its bolt forward on an empty chamber
    //    (WaitingForRack): that round counts as in the magazine until a full pull.
    // The bolt lock is Tarkov's bolt-catch animator flag. Tarkov clears it in its own reload; while it's on the gun
    // doesn't fire (BlocksFiring).
    // A handle on the bolt (AK-type guns, VAL, SCAR, SVD: the mod_charge bone sits on the carrier, and the slot is usually empty
    // because the handle is part of the carrier's mesh) brings the whole carrier back with it. A pistol's slide is worked the same way.
    // The bolt rides back with the handle so the chamber shows: Tarkov's own locked-back pose (the "Catch" layer, or the
    // IdlePosition blend on guns that use that instead) blended in by how far back the handle is. Only the pose; the
    // bolt-catch flag isn't touched, and letting go puts the pose back to what that flag says.
    public static partial class VRReloadController
    {
        public static bool chargingHandle = true;
        public static bool rackByHandOnDraw = true;        // false = Tarkov racks a gun drawn with an empty chamber itself
        public static float handleGrabRadius = 0.06f;      // m palm to the back of the charging handle to grab it
        public static float handleZoneBack = 0.05f;        // m the grab zone reaches on past that toward the stock (a capsule)
        public static float handleTravel = 0.08f;          // m the handle comes back (headset: 0.07 a touch short)
        public static float handleFullPull = 0.9f;         // share of handleTravel that counts as all the way back
        public static float handleLockedBackTravel = 0.02f; // m a handle on the bolt has left to give, with the bolt already locked back
        public static float underGunPullGain = 1.6f;        // the off hand on a handle on the bolt: how much further it comes than the controller does.
                                                            // That hand reaches under the gun and runs into the other controller over a full pull;
                                                            // it's locked on the handle, so it doesn't show. 1 = one to one.
        public static bool boltFollowsHandle = true;       // false = only the handle model moves
        public static bool showChamberedRound = true;      // the round in the chamber shows while the bolt is open, riding the bolt face
        public static float boltLockOnDistance = 0.003f;   // m a part has to come back before it's taken as the bolt the round follows

        public static bool HoldingChargingHandle { get; private set; }
        public static bool OffHandAtChargingHandle { get; private set; }   // the trigger is the handle's here, not a weapon-part interaction
        public static bool HandleInGunHand => HoldingChargingHandle && handleInGunHand;

        public static bool BoltLockedBack
        {
            get
            {
                FirearmController firearmController = VRGlobals.firearmController;
                return firearmController != null && firearmController.FirearmsAnimator != null && firearmController.FirearmsAnimator.GetBoltCatch();
            }
        }

        private static bool handleInGunHand;                // which hand took it; only meaningful while holding it
        private static FirearmController handleForFirearm;
        private static Transform handleBone;                // the mod_charge slot bone
        private static Transform handleView;                // the handle model, a direct child of that bone
        private static Vector3 handleRestLocalPosition;
        private static Vector3 handleAxisInSlot;            // back toward the stock, in the slot bone's frame
        private static Vector3 handAtHandleGrabInSlot;      // the controller when the handle was grabbed
        private static float handlePull;                    // m back from rest
        private static bool handleFullyBack;

        // The chambered round while the bolt is open: Tarkov's round model sits on the patron_in_weapon bone, shown and
        // hidden by the clips' events
        private static bool showingChamber;
        private static bool chamberShownEmpty;              // the pull threw the round out, or it's still in the magazine: empty until the bolt goes forward
        private static Transform chamberBone;
        private static bool chamberBoneWasActive;
        private static Transform chamberedRound;
        private static Vector3 chamberedRoundRestLocalPosition;
        private static Transform[] boltParts;               // the gun's parts and where they sat with the bolt closed
        private static Vector3[] boltPartsRestInChamber;
        private static Transform boltPart;                  // the one found moving back with the bolt
        private static Vector3 boltPartRestInChamber;

        // The carrier the held handle is part of, and our own transforms on the gun's body standing in for handleBone and handleView
        private static Transform handleCarrier;
        private static Vector3 handleCarrierRestLocalPosition;
        private static Vector3 handleCarrierAxis;           // one metre back, in the carrier's parent's frame
        private static Transform handleFrame;
        private static Transform handleRider;

        // The gun's handle, looked up again now and then (mods only change in the inventory)
        private static FirearmController handleSlotFor;
        private static float handleSlotLookupTime;
        private static Transform foundHandleBone;           // the mod_charge bone
        private static Transform foundHandleModel;          // the handle model in its slot, null with none fitted
        private static Transform foundHandleCarrier;        // the part that bone sits on when that part is the bolt, else null
        private static FirearmController rigBonesFor;
        private static readonly Dictionary<string, Transform> rigBones = new Dictionary<string, Transform>();
        private static readonly Dictionary<string, string> slideBones = new Dictionary<string, string>();   // by animation set: the bone that is a pistol's slide
        private static readonly HashSet<string> handleMovesWithBolt = new HashSet<string>();                // by animation set: the held bolt pose moves the handle bone
        private static Transform handleMeshesFor;
        private static MeshFilter[] handleMeshes;
        private static GameObject handleZoneViz;

        // The hand on the handle, read from the gun's rack clip while the handle is back: in the mod_charge bone's frame, which
        // the clip moves back with the handle (ours stays put and the model moves, so it's the bone's pose with the handle at rest)
        private sealed class HandleHold
        {
            public Vector3 position;
            public Vector3 rotation;
            public Vector3[] fingers;
            public float travel;            // m; 0 = handleTravel
        }

        // By animation set; the other guns' come from ReloadHolds.json. m4a1_reload_charge_0 at 0.6 s: thumb and index on the left wing of the T.
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
        // Another way to hold the same handle (an AK's from over the top or from below): whichever the hand is turned closest to as it takes it
        private static readonly Dictionary<string, List<HandleHold>> handleAlternates = new Dictionary<string, List<HandleHold>>();
        private static readonly Dictionary<string, List<HandleHold>> gunHandHandleAlternates = new Dictionary<string, List<HandleHold>>();
        // A handle with no slot whose rig bone isn't named mod_charge (MP7: weapon_charghing_handle, sic), by animation set
        private static readonly Dictionary<string, string> handleBoneNames = new Dictionary<string, string>();
        // Of those, a knob riding the bolt or carrier, which is what comes back (9A-91, G36), and a bolt that's the handle itself with
        // no knob of its own, whose back end is inside the receiver: grabbed where its clip's hand is, in the bone's frame (PPSh, PP-91)
        private static readonly Dictionary<string, string> handleCarrierNames = new Dictionary<string, string>();
        private static readonly Dictionary<string, Vector3> handleGrabPoints = new Dictionary<string, Vector3>();
        private static Vector3? foundHandleGrabLocal;
        private static Vector3[] handleHandFingers;         // null = no clip read for this gun yet: the magazine pull's pinch
        private static float heldHandleTravel;              // m this gun's handle comes back

        // The gun a magazine went into with its bolt forward on an empty chamber: Tarkov's insert chambered the top round,
        // but it's still in the magazine until the handle is racked. On the weapon, so it holds across a weapon swap.
        private static readonly HashSet<Weapon> weaponsWaitingForRack = new HashSet<Weapon>();
        private static bool insertNeedsRack;                // the insert under way went in on a closed bolt and an empty chamber

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

        // Tarkov's draw (SpawnOperation.Start) has just chambered a round because the chamber was empty, and the animator takes its racking
        // draw while it still reads the chamber as empty (SPAWN -> OUT TO IDLE ARM on AmmoInChamber < 1). Placing the round now reads it as
        // full, so it takes the plain draw; the round then waits for the hand, like one a magazine brought in. A gun the hand can't rack keeps
        // Tarkov's. Not TryGetChargingHandle: the gun isn't on the rig yet, and that remembers what it finds.
        public static void TakeRackOutOfDraw(FirearmController.SpawnOperation spawnOperation)
        {
            try
            {
                FirearmController firearmController = spawnOperation.Controller;
                if (!rackByHandOnDraw || !enabled || !chargingHandle || spawnOperation._ammoFromMagToChamber == null || firearmController == null
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
                return boltAction && TryGetBolt(firearmController, out _);
            if (weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazine && weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport)
                return false;
            LoadRecordedGrips();
            string family = AnimationFamily(firearmController);
            if (slideBones.ContainsKey(family) || handleHolds.ContainsKey(family) || handleBoneNames.ContainsKey(family))
                return true;
            // A handle on the bolt carrier that only the gun hand's clips rack, its slot for an add-on handle empty (AK-50: headset, the draw
            // still chambered). The hand takes the carrier's bone; a bolt-action's is the bolt, which the hand doesn't work.
            if (gunHandHandleHolds.ContainsKey(family) && !weapon.BoltAction)
                return true;
            foreach (Slot slot in weapon.AllSlots)
            {
                if (slot.ID == EWeaponModType.mod_charge.ToString() && slot.ContainedItem != null)
                    return true;
            }
            return false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static bool TryGetChargingHandle(FirearmController firearmController, Weapon weapon)
        {
            if (handleSlotFor != firearmController || Time.time > handleSlotLookupTime)
            {
                handleSlotFor = firearmController;
                handleSlotLookupTime = Time.time + 2f;
                foundHandleBone = null;
                foundHandleModel = null;
                foundHandleCarrier = null;
                foundHandleGrabLocal = null;
                foreach (Slot slot in weapon.AllSlots)
                {
                    if (slot.ID != EWeaponModType.mod_charge.ToString())
                        continue;
                    ContainerCollectionView.SlotView slotView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(slot);
                    if (slotView == null)
                        continue;
                    foundHandleBone = slotView.Bone;
                    foundHandleModel = slot.ContainedItem != null ? slotView.ItemView : null;
                    if (foundHandleModel != null)
                        break;
                }
                // No slot for one (SVD), but the rig can still have the bone. A bolt-action's is the bolt, not a handle.
                if (foundHandleBone == null && !weapon.BoltAction)
                    foundHandleBone = FindRigBone(firearmController, EWeaponModType.mod_charge.ToString());
                LoadRecordedGrips();
                string family = AnimationFamily(firearmController);
                Transform namedHandle = foundHandleBone == null && handleBoneNames.TryGetValue(family, out string handleBoneName)
                    ? FindRigBone(firearmController, handleBoneName) : null;
                if (namedHandle != null)
                {
                    // That bone is the handle itself, moving: worked like a carrier (our frame on the gun body, the bone follows the
                    // hand), as a pistol's slide. Measured against the bone it would follow itself, the MP5's shake.
                    foundHandleBone = namedHandle;
                    foundHandleModel = namedHandle;
                    Transform carrier = handleCarrierNames.TryGetValue(family, out string carrierName) ? FindRigBone(firearmController, carrierName) : null;
                    foundHandleCarrier = carrier != null ? carrier : namedHandle;
                    if (handleGrabPoints.TryGetValue(family, out Vector3 grabLocal))
                        foundHandleGrabLocal = grabLocal;
                }
                else if (foundHandleBone == null)
                {
                    // A pistol: the slide is the handle and its own carrier, its meshes say where the back of it is
                    if (slideBones.TryGetValue(family, out string slideBone))
                        foundHandleBone = FindRigBone(firearmController, slideBone);
                    foundHandleModel = foundHandleBone;
                    foundHandleCarrier = foundHandleBone;
                }
                else
                {
                    Transform handleParent = foundHandleBone.parent;
                    if (handleParent != null && (handleParent.name.IndexOf("bolt", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || handleParent.name.IndexOf("charg", System.StringComparison.OrdinalIgnoreCase) >= 0))
                        foundHandleCarrier = handleParent;
                    // The bolt pose moves this bone, and the pull would be measured against that: the pose follows the pull, the pull
                    // follows the pose (headset, MP5: the hand shakes on the handle). Worked like a carrier, nothing measures from it.
                    else if (handleMovesWithBolt.Contains(family))
                        foundHandleCarrier = foundHandleBone;
                }
            }
            // An empty slot on a gun whose handle slides on its own: there's nothing there to take hold of
            return foundHandleBone != null && (foundHandleModel != null || foundHandleCarrier != null);
        }

        private static Transform FindRigBone(FirearmController firearmController, string boneName)
        {
            if (rigBonesFor != firearmController)
            {
                rigBonesFor = firearmController;
                rigBones.Clear();
            }
            if (rigBones.TryGetValue(boneName, out Transform found))
                return found;
            foreach (Transform part in firearmController.WeaponRoot.GetComponentsInChildren<Transform>(true))
            {
                if (part.name == boneName)
                {
                    found = part;
                    break;
                }
            }
            rigBones[boneName] = found;
            return found;
        }

        // Where the hand takes it: the back of the fitted handle, or the bone where a handle built into the carrier sits
        private static Vector3 ChargingHandleGrabPoint(Vector3 backWorld)
        {
            if (foundHandleGrabLocal.HasValue)
                return foundHandleBone.position + foundHandleBone.rotation * foundHandleGrabLocal.Value;
            return foundHandleModel != null ? HandleGrabPoint(foundHandleModel, backWorld) : foundHandleBone.position;
        }

        // Not on a jammed gun: clearing one is still the interaction menu's, which this zone would keep from coming up
        private static bool InChargingHandleZone(FirearmController firearmController, Weapon weapon, bool gunHand = false)
        {
            if (!chargingHandle || weapon.MalfState.State != Weapon.EMalfunctionState.None || !TryGetChargingHandle(firearmController, weapon))
                return false;
            Vector3 backWorld = firearmController.WeaponRoot.up;
            Vector3 grabPoint = ChargingHandleGrabPoint(backWorld);
            return HandTouchesCapsule(gunHand, grabPoint, grabPoint + backWorld * handleZoneBack, handleGrabRadius);
        }

        private static bool OffHandTouchesCapsule(Vector3 start, Vector3 end, float radius)
        {
            return HandTouchesCapsule(false, start, end, radius);
        }

        // Any part of the hand: one point on it read as out with the fingers plainly in the zone (headset)
        private static readonly List<Vector3> handTouchPoints = new List<Vector3>();
        private static bool HandTouchesCapsule(bool gunHand, Vector3 start, Vector3 end, float radius)
        {
            if (VRGlobals.handsInteractionController == null)
                return DistanceToSegment(gunHand ? VRGlobals.vrPlayer.RightHand.transform.position : OffHandPalm(), start, end) <= radius;
            // The hand you see: the arm drawing it (OnRightArm), not the controller
            VRGlobals.handsInteractionController.GetHandTouchPoints(OnRightArm(gunHand), handTouchPoints);
            foreach (Vector3 point in handTouchPoints)
            {
                if (DistanceToSegment(point, start, end) <= radius)
                    return true;
            }
            return false;
        }

        // The back end of the handle (the T on an AR): the handle model is a long bar reaching forward into the receiver,
        // so its middle would be nowhere near where you grab it. The furthest point of its meshes' boxes toward the stock.
        private static Vector3 HandleGrabPoint(Transform view, Vector3 backWorld)
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

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void GrabChargingHandle(FirearmController firearmController, Weapon weapon, bool gunHand = false, bool sideHandle = false)
        {
            if (!TryGetChargingHandle(firearmController, weapon))
                return;

            // WeaponRoot -up is toward the muzzle, so +up is back toward the stock
            Vector3 backWorld = firearmController.WeaponRoot.up;
            handleCarrier = sideHandle ? null : foundHandleCarrier;
            if (sideHandle)
            {
                if (!FrameHandleOnSidePart(firearmController, backWorld))
                    return;
            }
            else if (handleCarrier != null)
            {
                FrameHandleOnCarrier(backWorld);
            }
            else
            {
                handleBone = foundHandleBone;
                handleView = foundHandleModel;
            }
            handleForFirearm = firearmController;
            handleRestLocalPosition = handleView.localPosition;
            handleAxisInSlot = handleBone.InverseTransformDirection(backWorld).normalized;
            handleInGunHand = gunHand;
            handlePull = 0f;
            handleFullyBack = false;
            HoldingChargingHandle = true;
            LatchHand(handleView, gunHand);
            handAtHandleGrabInSlot = handleBone.InverseTransformPoint(LatchedHandRig.position);
            StartHandleNotch(firearmController, backWorld);
            handleHandFingers = null;
            heldHandleTravel = handleTravel;
            LoadRecordedGrips();
            string family = AnimationFamily(firearmController);
            handleHolds.TryGetValue(family, out HandleHold offHandHold);
            gunHandHandleHolds.TryGetValue(family, out HandleHold gunHandHold);
            // By the arm, not the controller: with the gun in the left hand the off hand is the right hand, and racks with the right
            // hand's clip (the side handle's hold is the left hand's: the right one takes it where it grabs)
            bool rightArm = OnRightArm(gunHand);
            HandleHold handleHold = sideHandle ? (rightArm ? null : heldSideHandle.hold) : rightArm ? gunHandHold : offHandHold;
            // The same handle goes back as far whichever hand has it, and a gun's clips may only ever rack with one
            HandleHold otherHandHold = sideHandle ? null : rightArm ? offHandHold : gunHandHold;
            if (handleHold != null && handleHold.travel > 0f)
                heldHandleTravel = handleHold.travel;
            else if (otherHandHold != null && otherHandHold.travel > 0f)
                heldHandleTravel = otherHandHold.travel;
            if (handleCarrier != null && BoltLockedBack)
                heldHandleTravel = Mathf.Min(heldHandleTravel, handleLockedBackTravel);
            if (handleHold != null)
            {
                HandleHold grip = sideHandle ? handleHold : HoldHandIsTurnedTo(handleHold, rightArm ? gunHandHandleAlternates : handleAlternates, family);
                handleHandFingers = grip.fingers;
                SnapHandTo(handleView, handleBone.position + handleBone.rotation * grip.position, handleBone.rotation * Quaternion.Euler(grip.rotation));
            }
            StartMovedBolts(firearmController, backWorld);
            BeginChamberView(firearmController, weapon);

            if (debugLog)
            {
                FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
                Transform[] patrons = firearmController.Firearms._patronInWeaponTransforms;
                Transform patron = patrons != null && patrons.Length > 0 ? patrons[0] : null;
                LogAction($"[VRReload] grabbed the charging handle '{handleView.name}', bolt locked back: {BoltLockedBack}, chamber: {weapon.ChamberAmmoCount}, " +
                    $"catch layer: {firearmsAnimator.BOLT_CATCH_LAYER_INDEX}, idle position: {firearmsAnimator.HasParameter(AnimationControllerParametersTable.FLOAT_IDLEPOSITION)}, " +
                    $"round model shown: {(patron != null ? $"{patron.gameObject.activeSelf} ({patron.childCount} child)" : "no patron_in_weapon")}, gun hand: {gunHand}, hold from a clip: {handleHold != null}, carrier: {(handleCarrier != null ? handleCarrier.name : "none")}");
            }
            PulseHandleHand(0.3f, 0.04f);
        }

        private static HandleHold HoldHandIsTurnedTo(HandleHold main, Dictionary<string, List<HandleHold>> alternates, string family)
        {
            HandleHold closest = main;
            if (!alternates.TryGetValue(family, out List<HandleHold> others))
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

        // The carrier is animated and we move it, so the pull can't be measured against it: two transforms of ours on the gun's
        // body, where the mod_charge bone sits now. The hand rides the second; the carrier follows in ApplyHandleCarrier.
        private static void FrameHandleOnCarrier(Vector3 backWorld)
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

        private static void EnsureHandleFrame()
        {
            if (handleFrame == null || handleRider == null)
            {
                handleFrame = new GameObject("VRChargingHandleFrame").transform;
                handleRider = new GameObject("VRChargingHandleRider").transform;
                handleRider.SetParent(handleFrame, worldPositionStays: false);
            }
        }

        // LateUpdate, after the animator has posed the carrier this frame
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

        private static bool ChargingHandleHoldIsStillValid()
        {
            if (!enabled || !chargingHandle || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null)
                return false;
            if (VRGlobals.firearmController == null || VRGlobals.firearmController != handleForFirearm)
                return false;
            // The off hand let go too, or the grip was taken back some other way
            if (handleInGunHand && !ChestHang.SupportOnly)
                return false;
            // The gun went from one hand to the other: that arm isn't this hand's any more
            if (latchedOnRightArm != OnRightArm(handleInGunHand))
                return false;
            // Our own rechamber runs for a frame while the handle is back
            if (!(handleForFirearm.CurrentOperation is FirearmController.Idling) && expectedOperation != ExpectedOperation.Rechamber)
                return false;
            return handleView != null && handleBone != null;
        }

        // The controller's travel along the barrel moves the handle back; sideways it can't go
        private static void UpdateChargingHandle(bool triggerReleased)
        {
            Vector3 axis = handleBone.TransformDirection(handleAxisInSlot).normalized;
            Vector3 handDelta = LatchedHandRig.position - handleBone.TransformPoint(handAtHandleGrabInSlot);
            float handAlongTrack = Vector3.Dot(handDelta, axis);

            // Only the left hand reaches under the gun to a handle on the bolt (the right one is on that side)
            float pullGain = handleCarrier != null && handleCarrier != foundHandleBone && !latchedOnRightArm ? underGunPullGain : 1f;
            handlePull = Mathf.Clamp(handAlongTrack * pullGain, 0f, heldHandleTravel);
            handleView.position = handleBone.TransformPoint(handleRestLocalPosition) + axis * handlePull;
            UpdateHandleNotch();

            if (!handleFullyBack && handlePull >= heldHandleTravel * handleFullPull)
            {
                handleFullyBack = true;
                BoltComesBack(handleForFirearm);
            }

            // After the rechamber above: its skip puts the bolt pose back to the catch flag, this frame would flash it closed
            if (boltFollowsHandle)
            {
                FirearmsAnimator firearmsAnimator = handleForFirearm.FirearmsAnimator;
                float boltOpen = Mathf.Clamp01(handlePull / (heldHandleTravel * handleFullPull));
                SetBoltOpen(firearmsAnimator, firearmsAnimator.GetBoltCatch() ? 1f : boltOpen);
            }

            // Only letting go of the trigger lets go (his call: no distance release). Once it has been all the way back, that slams
            // the bolt forward whatever the handle does after
            if (triggerReleased)
                LetGoOfChargingHandle(snapForward: handleFullyBack);
        }

        // All the way back: a round in the chamber comes out now, as the bolt comes back, and the next one goes in
        private static void BoltComesBack(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            PlayFirstGunSound(firearmController, HandleSoundHand, "HandleOut", "SlideOut", "BoltOut");
            PulseHandleHand(0.5f, 0.04f);
            // An empty magazine's follower pushes the catch up under the bolt. (With a round to throw out Tarkov's
            // rechamber below sets it.)
            if (!firearmController.FirearmsAnimator.GetBoltCatch() && BoltStaysBack(weapon))
            {
                if (debugLog)
                    LogAction("[VRReload] empty magazine: the bolt locks back");
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

        private static void LetGoOfChargingHandle(bool snapForward)
        {
            // The hand first, where it is, so it doesn't snap forward with the handle
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
                return;
            if (firearmController != VRGlobals.firearmController)
            {
                RestoreBoltPose(firearmController);
                return;
            }
            if (staysInNotch)
                firearmController.FirearmsAnimator.SetBoltCatch(true);
            else if (snapForward)
                BoltSlamsForward(firearmController);
            else
                PlayFirstGunSound(firearmController, HandleSoundHand, "HandleIn", "SlideIn");
            RestoreBoltPose(firearmController);
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

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Only a bolt this pull opens: one already locked back (a magazine swapped into an empty gun) has its next round
        // counted in the chamber by Tarkov's insert, but it's still in the magazine until the bolt goes forward. The same
        // round under a bolt that was forward (WaitingForRack) is hidden while it opens.
        private static void BeginChamberView(FirearmController firearmController, Weapon weapon)
        {
            chamberShownEmpty = WaitingForRack(weapon);
            Transform[] chamberBones = firearmController.Firearms._patronInWeaponTransforms;
            chamberBone = chamberBones != null && chamberBones.Length > 0 ? chamberBones[0] : null;
            showingChamber = showChamberedRound && boltFollowsHandle && chamberBone != null
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

        // LateUpdate, after the animator has posed the bolt this frame (read in Update it trails a frame behind).
        // The round rides the bolt face; the model itself isn't animated, so its offset stays until the next write.
        // Once the rechamber has thrown it out (its model goes back to Tarkov's pool) the next one is hidden, and so is a
        // round still in the magazine.
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

        // Every part of the gun, and where it sits in the chamber bone's frame with the bolt closed (the grab's first
        // frame). The chamber frame, not the gun's root: the receiver sways against the root with the idle breathing and
        // the chamber sways with it, so only the bolt's own travel is left (measured against the root, the round bobbed).
        // Left out: the handle and the round (we move those) and the gun's own arm and camera bones.
        private static void RecordBoltParts(Transform weaponRoot)
        {
            Transform[] parts = weaponRoot.GetComponentsInChildren<Transform>();
            int kept = 0;
            for (int index = 0; index < parts.Length; index++)
            {
                Transform part = parts[index];
                if (part == weaponRoot || part.IsChildOf(handleView) || part.IsChildOf(chamberBone) || part.name.Contains("Human") || part.name.Contains("Camera"))
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

        // The bolt = the part that has come furthest back along the barrel (the locked-back pose moves the whole bolt
        // group together); followed on its own once it's clearly moving. In the chamber bone's frame, = the round's.
        private static Vector3 BoltMovementInChamber(Vector3 backWorld)
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

        // The bolt goes where the gun's own catch flag says: forward, or held back by an empty magazine
        private static void RestoreBoltPose(FirearmController firearmController)
        {
            FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
            if (firearmsAnimator != null)
                SetBoltOpen(firearmsAnimator, firearmsAnimator.GetBoltCatch() ? 1f : 0f);
        }

        // Let go from a full pull: a bolt locked back closes (unless an empty magazine holds it), and a round still in the
        // magazine goes into the chamber. With no magazine in there's nothing to strip: it keeps waiting.
        private static void BoltSlamsForward(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            // Held by an empty magazine the bolt only comes onto the catch: no slam
            bool heldOnCatch = firearmController.FirearmsAnimator.GetBoltCatch() && BoltStaysBack(weapon);
            if (!heldOnCatch)
                PlayFirstGunSound(firearmController, HandleSoundHand, "BoltIn", "SlideIn");
            PlayGunSound(firearmController, "HandleIn", HandleSoundHand);
            PulseHandleHand(heldOnCatch ? 0.4f : 0.8f, 0.06f);
            BoltStripsRound(weapon);
            if (firearmController.FirearmsAnimator.GetBoltCatch())
            {
                if (debugLog)
                    LogAction($"[VRReload] bolt released, chamber: {weapon.ChamberAmmoCount}");
                firearmController.FirearmsAnimator.SetBoltCatch(BoltStaysBack(weapon));
            }
        }

        // The bolt going forward takes the round a magazine brought in on a closed bolt into the chamber, however it was let go:
        // the handle from a full pull, or slapped (an MP5's out of its notch, which it went into from that same pull)
        private static void BoltStripsRound(Weapon weapon)
        {
            if (!WaitingForRack(weapon) || weapon.GetCurrentMagazine() == null)
                return;
            if (debugLog)
                LogAction("[VRReload] racked: the round goes into the chamber");
            weaponsWaitingForRack.Remove(weapon);
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
            bool hasHandle = show && chargingHandle && TryGetChargingHandle(firearmController, weapon);
            if (!hasHandle || HoldingChargingHandle)
            {
                SetZoneViz(ref handleZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }
            Vector3 backWorld = firearmController.WeaponRoot.up;
            Vector3 grabPoint = ChargingHandleGrabPoint(backWorld);
            Vector3 zoneEnd = grabPoint + backWorld * handleZoneBack;
            bool inReach = OffHandTouchesCapsule(grabPoint, zoneEnd, handleGrabRadius)
                || (ChestHang.SupportOnly && HandTouchesCapsule(true, grabPoint, zoneEnd, handleGrabRadius));
            SetCapsuleZoneViz(ref handleZoneViz, grabPoint, zoneEnd, handleGrabRadius, inReach ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.4f, 0f, 0.3f));
        }
    }
}
