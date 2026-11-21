using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Belt-fed machine guns by hand (Manual Reload): PKM / PKP / PKTM, RPD / RPDN, M60E4 / E6 (data from gen_lmgs.py).
    // Steps: swing a scope mount's latch aside (PKP, RPDN; pk_sight_open / rpdn_sight_open), lift the feed cover by its free end (the gun
    // doesn't fire while it is open), take the box off (B or the grip; only with the cover open), lay the belt's end where Tarkov's idle
    // pose has it, and shut the cover. A box out of the tray has its belt hanging: the free links are a chain with gravity (BeltRope)
    // drawn on Tarkov's link bones.
    // When the cover shuts on a laid belt, a gun with a chamber (the PKs) waits for its handle (insertNeedsRack); one without (the RPDs)
    // clicks until its handle has been all the way back once (waitsForCharge). The M60's LBT pouch has no belt data, so it goes in laid.
    // Either hand works: the off hand while the gun hand holds the grip, or the gun hand while the off hand holds the gun up.
    // Not synced to co-op observers (they see Tarkov's poses); the inventory moves are synced.
    public static partial class VRReloadController
    {
        public static float hingeGrabRadius = 0.05f;        // m from any point of the hand to a cover's or latch's free end
        public static float hingeOpenEnough = 0.5f;         // share open past which letting go swings it fully open (else it shuts)
        public static float hingeSettleTime = 0.12f;        // s it takes to swing the rest of the way once let go
        public static float coverOpenToWork = 0.85f;        // share open the cover must be for the box and the belt
        public static float latchOpenToWork = 0.85f;        // share the latch must be swung aside for the cover to lift
        public static float beltGrabRadius = 0.05f;         // m from any point of the hand to the belt (its hanging end, or its end in the tray)
        public static float beltLayRadius = 0.05f;          // m the held end must come to where Tarkov lays it
        public static float beltLayTime = 0.15f;            // s the belt eases into the animator's laid pose
        public static float beltTipEaseTime = 0.1f;         // s the end eases into the hand at the grab
        public static int beltExtraOut = 6;                 // links the hand can draw out of the box past the clip's own
        public static float beltDamping = 0.9f;             // share of each point's motion kept per step (lower = less swing)
        public static float beltMaxSpeed = 4f;              // m/s a belt point moves at most (a snap turn doesn't fling it)
        public static float beltMaxBendDegrees = 50f;       // deg, the largest angle between two links
        // Share of the way each free link is pulled back per step toward the clip's hanging shape (in the box's frame). While held, the
        // pull fades toward the end in the hand, so the belt bends to follow the hand from the box out.
        public static float beltShapeFree = 0.25f;
        public static float beltShapeHeld = 0.3f;
        public static float beltGravity = 0.3f;             // share of gravity on the free links (the shape spring carries the rest)
        public static float beltBoxMargin = 0.004f;         // m the free links are kept off the box's body (so they don't pass through it)
        public static int beltIterations = 10;
        public static float beltJumpDistance = 0.4f;        // m; a box move this large in one frame is a jump (the rig recentred), the belt moves with it
        public static bool beltDebugLog = true;             // grabs, refusals, the belt's states
        // Each part is worked only by the hand its clips use (gen_lmgs "hands": the PKM's right hand does everything, the RPD's and the
        // M60's left hand everything but the handle).
        // A hand taking a part (the cover, the latch; the box through the magazine holds) moves onto the clip's hold, and the belt's end
        // goes into the fingers where the clip pinches it.
        public static float partMirrorPlaneTolerance = 0.03f; // m a part's bone may be off the gun's centre plane and still mirror its hold (gun in the other hand)

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The data (ReloadHolds.json "lmgs")

        private sealed class HingeData
        {
            public string bone;
            public Vector3 axis;            // in the bone's closed frame
            public float angle;
            public string button;           // the latch's button bone, pressed while the latch is held
            public Vector3 buttonTravel;    // its parent's frame
        }

        private sealed class BeltData
        {
            public string box;              // the box model it was read on
            public HashSet<string> boxIds;  // the item templates that are that model; another box (the M60's LBT pouch) skips the belt step
            public string prefix;           // "belt_link_": belt_link_001..count, each nested under the one before
            public int count;
            public int anchor;              // the last link still in the box (1-based)
            public float spacing;
            public Vector3[] positions;     // hanging, box root frame
            public Quaternion[] rotations;
            public Vector3[] laidPositions; // Tarkov's belt in the tray (its idle clip), box root frame; null = not baked
            public Quaternion[] laidRotations;
            // The box's other bones the idle clip animates (the spent links hanging off the last one, the lid, the rounds) and their local
            // poses. They are held there because the animator doesn't pose a box put on by hand.
            public string[] idleNames;
            public Vector3[] idlePositions, idleScales;
            public Quaternion[] idleRotations;
        }

        // A place for the left hand on the gun (SupportSpot*): where the clips put its IK marker, in the weapon bone's frame
        private sealed class SupportSpot
        {
            public string name;
            public bool onBox;
            public Vector3 ikPosition;
            public Quaternion ikRotation;
            public Vector3[] fingers;
        }

        // A clip's palm on a part, in the part's frame
        private sealed class PartHold
        {
            public bool rightHand;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3[] fingers;
        }

        private sealed class LmgData
        {
            public HingeData cover;
            public HingeData latch;
            public BeltData belt;
            public PartHold coverHold, latchHold, beltHold;
            public MagazineHold boxPull, boxInsert;     // the box in the palm's frame (FindReloadGrip)
            public bool hasHands;                       // the data says which hand works each part
            public bool coverByGunHand, latchByGunHand, beltByGunHand, boxByGunHand, chargeByGunHand;
            public SupportSpot[] supportSpots;          // the first is where its clips have the left hand at rest
            public RockIn boxRock;                      // the box rocking on and off across the gun (the PKM's); null = the straight track
        }

        private static readonly Dictionary<string, LmgData> lmgGuns = new Dictionary<string, LmgData>();

        private static LmgData LmgFrom(SavedLmg saved)
        {
            if (saved == null)
                return null;
            LmgData data = new LmgData { cover = HingeFrom(saved.cover), latch = HingeFrom(saved.latch) };
            // The clip's right hand is the gun hand (with the gun in the left hand, the left hand, mirrored)
            if (saved.hands != null && saved.hands.Count > 0)
            {
                data.hasHands = true;
                bool ByGunHand(string part) => saved.hands.TryGetValue(part, out string hand) && hand == "R";
                data.coverByGunHand = ByGunHand("cover");
                data.latchByGunHand = ByGunHand("latch");
                data.beltByGunHand = ByGunHand("belt");
                data.boxByGunHand = ByGunHand("box");
                data.chargeByGunHand = ByGunHand("charge");
            }
            if (saved.supportSpots != null && saved.supportSpots.Count >= 2)
            {
                List<SupportSpot> spots = new List<SupportSpot>();
                foreach (SavedSupportSpot spot in saved.supportSpots)
                {
                    if (spot?.ik == null || spot.ik.Length < 7)
                        continue;
                    spots.Add(new SupportSpot
                    {
                        name = spot.name,
                        onBox = spot.onBox,
                        ikPosition = Vector3From(spot.ik, 0),
                        ikRotation = QuaternionFrom(spot.ik, 3),
                        fingers = spot.fingers != null && spot.fingers.Length >= 48 ? FingersFrom(spot.fingers) : null,
                    });
                }
                data.supportSpots = spots.Count >= 2 ? spots.ToArray() : null;
            }
            SavedBoxRock rock = saved.boxRock;
            if (rock != null && rock.axis != null && rock.axis.Length >= 3 && rock.pivot != null && rock.pivot.Length >= 3 && rock.angle > 1f)
                data.boxRock = new RockIn { axis = Vector3From(rock.axis, 0).normalized, angle = rock.angle, pivot = Vector3From(rock.pivot, 0) };
            if (saved.holds != null)
            {
                saved.holds.TryGetValue("cover", out SavedPartHold cover);
                saved.holds.TryGetValue("latch", out SavedPartHold latch);
                saved.holds.TryGetValue("belt", out SavedPartHold beltEnd);
                saved.holds.TryGetValue("boxPull", out SavedPartHold boxPull);
                saved.holds.TryGetValue("boxInsert", out SavedPartHold boxInsert);
                data.coverHold = PartHoldFrom(cover);
                data.latchHold = PartHoldFrom(latch);
                data.beltHold = PartHoldFrom(beltEnd);
                data.boxPull = BoxHoldFrom(boxPull);
                data.boxInsert = BoxHoldFrom(boxInsert);
            }
            SavedBelt belt = saved.belt;
            if (belt != null && belt.hanging != null && belt.hanging.Count == belt.count && belt.count >= 3 && belt.anchor >= 1 && belt.anchor < belt.count)
            {
                data.belt = new BeltData
                {
                    box = string.IsNullOrEmpty(belt.box) ? null : belt.box,
                    boxIds = belt.boxIds != null && belt.boxIds.Count > 0 ? new HashSet<string>(belt.boxIds) : null,
                    prefix = belt.prefix,
                    count = belt.count,
                    anchor = belt.anchor,
                    spacing = belt.spacing,
                    positions = new Vector3[belt.count],
                    rotations = new Quaternion[belt.count],
                };
                for (int link = 0; link < belt.count; link++)
                {
                    float[] pose = belt.hanging[link];
                    data.belt.positions[link] = Vector3From(pose, 0);
                    data.belt.rotations[link] = QuaternionFrom(pose, 3);
                }
                SavedIdleLocals idle = belt.idleLocals;
                if (idle?.names != null && idle.poses != null && idle.names.Count == idle.poses.Count)
                {
                    int bones = idle.names.Count;
                    data.belt.idleNames = idle.names.ToArray();
                    data.belt.idlePositions = new Vector3[bones];
                    data.belt.idleRotations = new Quaternion[bones];
                    data.belt.idleScales = new Vector3[bones];
                    for (int bone = 0; bone < bones; bone++)
                    {
                        float[] pose = idle.poses[bone];
                        data.belt.idlePositions[bone] = Vector3From(pose, 0);
                        data.belt.idleRotations[bone] = QuaternionFrom(pose, 3);
                        data.belt.idleScales[bone] = pose.Length >= 10 ? Vector3From(pose, 7) : Vector3.one;
                    }
                }
                if (belt.laid != null && belt.laid.Count == belt.count)
                {
                    data.belt.laidPositions = new Vector3[belt.count];
                    data.belt.laidRotations = new Quaternion[belt.count];
                    for (int link = 0; link < belt.count; link++)
                    {
                        data.belt.laidPositions[link] = Vector3From(belt.laid[link], 0);
                        data.belt.laidRotations[link] = QuaternionFrom(belt.laid[link], 3);
                    }
                }
            }
            return data.cover != null ? data : null;
        }

        private static HingeData HingeFrom(SavedHinge saved)
        {
            if (saved == null || string.IsNullOrEmpty(saved.bone) || saved.axis == null || saved.axis.Length < 3 || saved.angle <= 1f)
                return null;
            return new HingeData
            {
                bone = saved.bone,
                axis = Vector3From(saved.axis, 0).normalized,
                angle = saved.angle,
                button = string.IsNullOrEmpty(saved.button) ? null : saved.button,
                buttonTravel = saved.buttonTravel != null && saved.buttonTravel.Length >= 3 ? Vector3From(saved.buttonTravel, 0) : Vector3.zero,
            };
        }

        private static PartHold PartHoldFrom(SavedPartHold saved)
        {
            if (saved == null || saved.position == null || saved.position.Length < 3 || saved.rotation == null || saved.rotation.Length < 3)
                return null;
            return new PartHold
            {
                rightHand = saved.hand == "R",
                position = Vector3From(saved.position, 0),
                rotation = Quaternion.Euler(Vector3From(saved.rotation, 0)),
                fingers = saved.fingers != null && saved.fingers.Length >= 48 ? FingersFrom(saved.fingers) : null,
            };
        }

        private static MagazineHold BoxHoldFrom(SavedPartHold saved)
        {
            if (saved == null || saved.position == null || saved.position.Length < 3 || saved.rotation == null || saved.rotation.Length < 3)
                return null;
            return new MagazineHold
            {
                position = Vector3From(saved.position, 0),
                rotation = Vector3From(saved.rotation, 0),
                fingers = saved.fingers != null && saved.fingers.Length >= 48 ? FingersFrom(saved.fingers) : null,
                inBoneFrame = saved.inBoneFrame,
                rightHand = saved.hand == "R",
            };
        }

        // Which hand may work a part: the one the clips work it with, or either hand when the data has no hands
        private static bool HandWorksPart(LmgData data, bool partIsTheGunHands, bool gunHand)
        {
            return data == null || !data.hasHands || partIsTheGunHands == gunHand;
        }

        // Not a belt gun: any hand
        private static bool BeltBoxHandMay(FirearmController firearmController, bool gunHand)
        {
            return !TryGetLmg(firearmController, out LmgData data) || HandWorksPart(data, data.boxByGunHand, gunHand);
        }

        // A box the clips take off with the off hand (the RPD's) is released with that hand's button while the hand is on the box
        // (ThumbWorksCatch, like an AK's paddle). The PKM's box is the gun hand's, on B.
        private static bool BeltBoxReleasedByOffHand(FirearmController firearmController)
        {
            return TryGetLmg(firearmController, out LmgData data) && data.hasHands && !data.boxByGunHand;
        }

        private static bool BeltChargeHandMay(FirearmController firearmController, bool gunHand)
        {
            return !TryGetLmg(firearmController, out LmgData data) || HandWorksPart(data, data.chargeByGunHand, gunHand);
        }

        // A part's hold for this arm, in world space: as the clip has it, or for the other arm (gun in the left hand) mirrored through the
        // gun's centre plane. Mirroring needs the part's bone on that plane (the cover, not a mount off to one side). False = no hold.
        private static bool PartHoldWorld(PartHold hold, Transform frame, bool rightArm, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (hold == null || frame == null)
                return false;
            Vector3 local = hold.position;
            Quaternion turn = hold.rotation;
            if (hold.rightHand != rightArm)
            {
                if (!TarkovVR.Patches.Core.Player.RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                    return false;
                Quaternion toFrame = Quaternion.Inverse(frame.rotation);
                Vector3 normal = (toFrame * planeNormal).normalized;
                Vector3 point = toFrame * (planePoint - frame.position);
                if (Mathf.Abs(Vector3.Dot(point, normal)) > partMirrorPlaneTolerance
                    || !TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(turn, normal, rightToLeft: hold.rightHand, out turn))
                    return false;
                local -= 2f * Vector3.Dot(local - point, normal) * normal;
            }
            position = frame.position + frame.rotation * local;
            rotation = frame.rotation * turn;
            return true;
        }

        // The hold's finger pose for this arm (mirrored for the other one)
        private static readonly Vector3[] mirroredPartFingers = new Vector3[16];
        private static Vector3[] PartFingers(PartHold hold, bool rightArm)
        {
            if (hold?.fingers == null)
                return null;
            return hold.rightHand == rightArm ? hold.fingers : MirroredPose(hold.fingers, rightToLeft: hold.rightHand, mirroredPartFingers);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The PKM's box rocks on and off across the gun, left to right, the way an AK magazine rocks front to back: the AK rock
        // (VRReloadController.RockIn) hinged on the box's hooked top edge (gen_lmgs boxRock). Following the clip's own path exactly is
        // too hard to hit by hand, so it isn't used.


        // For GrabFromGun / ArmNewMagazine: this gun's box rock, if this is the belt box the data was read on
        private static RockIn BeltBoxRockIn(FirearmController firearmController, Magazine magazine)
        {
            return TryGetLmg(firearmController, out LmgData data) && data.belt != null && BeltFits(magazine, data.belt) ? data.boxRock : null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The belt box's grab zone is its body: any point of the hand within beltBoxGrabMargin of it. (A magazine's grab capsule,
        // a magazine length down, would sit inside the middle of the box.)

        public static float beltBoxGrabMargin = 0.04f;      // m from any point of the hand to the box's body (its mesh bounds)
        private static Transform beltBoxBoundsFor;
        private static Bounds beltBoxBounds;                // in the box view's own frame

        private static bool HandAtBeltBox(FirearmController firearmController, Slot magazineSlot, bool gunHand)
        {
            ContainerCollectionView.SlotView view = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            Transform box = view != null ? view.ItemView : null;
            if (box == null)
                return false;
            if (beltBoxBoundsFor != box)
            {
                beltBoxBoundsFor = box;
                beltBoxBounds = new Bounds();
                bool any = false;
                foreach (MeshFilter meshFilter in box.GetComponentsInChildren<MeshFilter>(false))
                {
                    // The box's own body, not the belt and rounds hanging off it
                    if (meshFilter.sharedMesh == null || meshFilter.name.IndexOf("belt", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || meshFilter.name.IndexOf("patron", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;
                    Bounds bounds = meshFilter.sharedMesh.bounds;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 local = BoundsCorners.Corner(bounds, corner);
                        Vector3 inBox = box.InverseTransformPoint(meshFilter.transform.TransformPoint(local));
                        if (!any)
                            beltBoxBounds = new Bounds(inBox, Vector3.zero);
                        else
                            beltBoxBounds.Encapsulate(inBox);
                        any = true;
                    }
                }
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBelt] box '{box.name}' grab zone: body {(beltBoxBounds.size * 100f).ToString("F1")} cm");
            }
            if (beltBoxBounds.size.sqrMagnitude < 1e-6f)
                return false;
            if (VRGlobals.handsInteractionController == null)
                return DistanceToBox(box, HandPalm(gunHand)) <= beltBoxGrabMargin;
            VRGlobals.handsInteractionController.GetHandTouchPoints(OnRightArm(gunHand), handTouchPoints);
            foreach (Vector3 point in handTouchPoints)
            {
                if (DistanceToBox(box, point) <= beltBoxGrabMargin)
                    return true;
            }
            return false;
        }

        // The middle of a belt box's body, in its own frame: its biggest mesh (not the belt's links, the rounds or the spent links)
        private static bool TryBeltBoxBodyCenter(GameObject box, out Vector3 center)
        {
            center = Vector3.zero;
            float biggest = 0f;
            foreach (MeshFilter meshFilter in box.GetComponentsInChildren<MeshFilter>(true))
            {
                string name = meshFilter.name;
                if (meshFilter.sharedMesh == null || name.IndexOf("belt", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("link", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("patron", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                Bounds bounds = meshFilter.sharedMesh.bounds;
                Vector3 size = Vector3.Scale(bounds.size, meshFilter.transform.lossyScale);
                float volume = Mathf.Abs(size.x * size.y * size.z);
                if (volume <= biggest)
                    continue;
                biggest = volume;
                center = box.transform.InverseTransformPoint(meshFilter.transform.TransformPoint(bounds.center));
            }
            return biggest > 0f;
        }

        // A world point's distance (metres) to the box's body, 0 inside
        private static float DistanceToBox(Transform box, Vector3 world)
        {
            Vector3 local = box.InverseTransformPoint(world);
            Vector3 closest = beltBoxBounds.ClosestPoint(local);
            return (box.TransformPoint(closest) - world).magnitude;
        }

        // The left hand holds the gun by the box (the PKM's support spot there), so the box can't come off from under it
        private static bool SupportHandOnBox => supportSpotDriving && supportSpot > 0 && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting
            && supportSpotFor != null && TryGetSupportSpots(supportSpotFor, out SupportSpot[] spots, out _) && supportSpot < spots.Length && spots[supportSpot].onBox;

        // A box of the model the belt data was read on (by item template; the name of its pooled model can't be relied on)
        private static bool BeltFits(Magazine box, BeltData belt)
        {
            return box != null && belt != null && (belt.boxIds == null || belt.boxIds.Contains(box.TemplateId.ToString()));
        }

        // Our belt gun in our hands, Manual Reload on
        private static bool TryGetLmg(FirearmController firearmController, out LmgData data)
        {
            data = null;
            if (!enabled || firearmController == null || firearmController.Item == null || !firearmController.Item.IsBeltMachineGun
                || firearmController._player == null || !firearmController._player.IsYourPlayer || !VRSettings.GetManualReloadOn())
                return false;
            LoadRecordedGrips();
            return lmgGuns.TryGetValue(AnimationFamily(firearmController), out data);
        }

        // Per gun, so an open cover stays open across a weapon swap
        private sealed class LmgState
        {
            public float cover;             // 0 shut .. 1 open
            public float latch;             // 0 over the cover .. 1 aside
            public float coverSettle = -1f; // after a let go: where it swings to (-1 = not settling)
            public float latchSettle = -1f;
            public bool beltOut;            // the box on it has its belt out of the feed tray
            public bool waitsForCharge;     // a box went in by hand on a gun without a chamber: the handle must be pulled first
        }
        private static readonly Dictionary<Weapon, LmgState> lmgStates = new Dictionary<Weapon, LmgState>();

        private static LmgState LmgStateOf(Weapon weapon)
        {
            if (!lmgStates.TryGetValue(weapon, out LmgState state))
            {
                state = new LmgState();
                lmgStates[weapon] = state;
            }
            return state;
        }

        private static bool CoverOpenEnough(LmgState state) => state.cover >= coverOpenToWork;

        // No firing while the cover is open (or being worked), the belt is out of the tray, or either is in a hand
        public static bool BeltGunBlocksFiring
        {
            get
            {
                FirearmController firearmController = VRGlobals.firearmController;
                if (firearmController == null || firearmController.Item == null || !TryGetLmg(firearmController, out _))
                    return false;
                if (HoldingHinge || HoldingBelt || beltLaying)
                    return true;
                return lmgStates.TryGetValue(firearmController.Item, out LmgState state)
                    && (state.cover > 0.02f || (state.beltOut && firearmController.Item.GetCurrentMagazine() != null));
            }
        }

        // A box put in by hand on a gun without a chamber: the trigger only clicks until the handle has been all the way back
        private static bool BeltWaitsForCharge
        {
            get
            {
                FirearmController firearmController = VRGlobals.firearmController;
                return firearmController != null && firearmController.Item != null && lmgStates.TryGetValue(firearmController.Item, out LmgState state)
                    && state.waitsForCharge && firearmController.Item.GetCurrentMagazineCount() > 0 && TryGetLmg(firearmController, out _);
            }
        }

        // Called from BoltComesBack when the handle is all the way back
        private static void BeltGunCharged(FirearmController firearmController)
        {
            if (firearmController == null || firearmController.Item == null || !lmgStates.TryGetValue(firearmController.Item, out LmgState state) || !state.waitsForCharge)
                return;
            state.waitsForCharge = false;
            if (beltDebugLog)
                Plugin.MyLog.LogInfo($"[VRBelt] {firearmController.Item.LocalizedShortName()}: charged");
        }

        // The box comes off only with the cover open (GrabFromGun, TryReleaseMagazine). True = it stays on, and the hand gets a refusal buzz.
        private static bool BeltBoxStaysOn(FirearmController firearmController, SoundHand hand)
        {
            if (!TryGetLmg(firearmController, out _))
                return false;
            bool coverShut = !CoverOpenEnough(LmgStateOf(firearmController.Item));
            bool heldUnder = SupportHandOnBox;
            if (!coverShut && !heldUnder)
                return false;
            Refuse(hand);
            if (beltDebugLog)
                Plugin.MyLog.LogInfo(coverShut ? "[VRBelt] the box stays on: the cover is shut" : "[VRBelt] the box stays on: the left hand holds it (take the handguard)");
            return true;
        }

        // The box came off by hand (GrabFromGun): the belt goes with it, out of the tray if it was laid
        private static void BeltBoxTakenOff(FirearmController firearmController)
        {
            if (!TryGetLmg(firearmController, out LmgData data) || data.belt == null)
                return;
            if (HoldingBelt)
                LetGoOfBelt();
            // The belt as it is now, for the box in the hand to start from
            if (gunRope.simulating)
                StashRope(gunRope);
            else if (gunRope.links != null)
                StashLinks(gunRope);
            LmgState state = LmgStateOf(firearmController.Item);
            state.beltOut = false;
            beltLaying = false;
            StopRope(gunRope);
        }

        // A box seated by hand (TryInsert): its belt hangs out until the hand lays it. On a gun with no chamber, the handle must be pulled first.
        private static void BeltBoxPutOn(FirearmController firearmController)
        {
            if (!TryGetLmg(firearmController, out LmgData data) || data.belt == null)
                return;
            if (propRope.simulating)
                StashRope(propRope);
            LmgState state = LmgStateOf(firearmController.Item);
            // A box we have no belt shape for goes in laid, as Tarkov has it
            state.beltOut = BeltFits(heldMagazine, data.belt);
            Weapon weapon = firearmController.Item;
            state.waitsForCharge = (weapon.Chambers == null || weapon.Chambers.Length == 0) && TryGetChargingHandle(firearmController, weapon);
            if (beltDebugLog)
                Plugin.MyLog.LogInfo($"[VRBelt] box on {weapon.LocalizedShortName()}: belt out of the tray{(state.waitsForCharge ? ", waits for the handle" : "")}");
        }

        private static void Refuse(SoundHand hand)
        {
            Pulse(hand, 0.2f, 0.03f);
            Pulse(hand, 0.2f, 0.03f, 0.08f);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The cover and the latch: hinges

        private enum HingeKind { Cover, Latch }

        private sealed class HingeRig
        {
            public FirearmController firearm;
            public Transform bone;
            public Quaternion closed;
            public Vector3 freeEndLocal;    // in its own closed frame
            public Transform grab;          // our grab transform at that end
            public Transform button;
            public Vector3 buttonRest;
            public bool visible;
            public float visibleCheckTime;
        }
        private static readonly HingeRig coverRig = new HingeRig(), latchRig = new HingeRig();
        private static readonly Dictionary<Transform, Quaternion> hingeClosedPoses = new Dictionary<Transform, Quaternion>();
        private static readonly Dictionary<Transform, Vector3> buttonRestPoses = new Dictionary<Transform, Vector3>();
        private static GameObject coverZoneViz, latchZoneViz, beltZoneViz, trayZoneViz;

        public static bool HoldingHinge { get; private set; }
        private static HingeKind heldHinge;
        private static bool hingeInGunHand, hingeOnRightArm;
        private static FirearmController hingeHeldFor;
        private static float hingeOpenAtGrab;
        private static Vector3 hingeAcrossAtGrab;           // the controller's offset across the hinge at the grab, in the bone's parent frame
        private static Transform hingeRider;                // our transform on the part, where the hand took hold
        private static float latchButtonPressed;
        private static PartHold heldPartHold;               // the clip's hold on the part held now (a hinge or the belt's end), for its fingers
        private static bool HingeInGunHand => HoldingHinge && hingeInGunHand;

        private static HingeRig RigOf(HingeKind kind) => kind == HingeKind.Cover ? coverRig : latchRig;
        private static HingeData HingeOf(LmgData data, HingeKind kind) => kind == HingeKind.Cover ? data.cover : data.latch;

        private static bool EnsureHinge(FirearmController firearmController, LmgData data, HingeKind kind, out HingeRig rig)
        {
            rig = RigOf(kind);
            HingeData hinge = HingeOf(data, kind);
            if (hinge == null)
                return false;
            if (rig.firearm == firearmController && rig.bone != null)
                return true;
            rig.firearm = firearmController;
            rig.bone = FindRigBone(firearmController, hinge.bone);
            rig.visibleCheckTime = 0f;
            if (rig.bone == null)
                return false;
            // The closed pose is stored per bone the first time it is seen, before we turn it. The gun's model comes back from
            // Tarkov's pool turned the way we left it.
            if (!hingeClosedPoses.TryGetValue(rig.bone, out rig.closed))
            {
                rig.closed = rig.bone.localRotation;
                hingeClosedPoses[rig.bone] = rig.closed;
            }
            rig.freeEndLocal = HingeFreeEnd(rig.bone, hinge.axis, Quaternion.Inverse(rig.closed) * rig.bone.localRotation);
            if (rig.grab == null)
                rig.grab = new GameObject(kind == HingeKind.Cover ? "VRCoverGrab" : "VRLatchGrab").transform;
            rig.grab.SetParent(rig.bone, worldPositionStays: false);
            rig.grab.localPosition = rig.freeEndLocal;
            rig.grab.localRotation = Quaternion.identity;
            rig.button = hinge.button != null ? FindRigBone(firearmController, hinge.button) : null;
            if (rig.button != null && !buttonRestPoses.TryGetValue(rig.button, out rig.buttonRest))
            {
                rig.buttonRest = rig.button.localPosition;
                buttonRestPoses[rig.button] = rig.buttonRest;
            }
            if (beltDebugLog)
                Plugin.MyLog.LogInfo($"[VRBelt] {kind} '{rig.bone.name}' on {firearmController.Item.LocalizedShortName()}: free end {(rig.freeEndLocal * 100f).ToString("F1")} cm, "
                    + $"{hinge.angle:0.#} deg about {hinge.axis.ToString("F2")}{(rig.button != null ? ", button " + rig.button.name : "")}");
            return true;
        }

        // The free end of a hinged part, in its own closed frame: the centre of its meshes, pushed out across the hinge line to
        // their farthest corner
        private static Vector3 HingeFreeEnd(Transform bone, Vector3 axis, Quaternion open, bool includeHiddenMeshes = false)
        {
            Quaternion toClosed = Quaternion.Inverse(open);
            Vector3 centerSum = Vector3.zero;
            int corners = 0;
            float furthest = 0f;
            foreach (MeshFilter meshFilter in bone.GetComponentsInChildren<MeshFilter>(includeHiddenMeshes))
            {
                if (meshFilter.sharedMesh == null)
                    continue;
                Bounds bounds = meshFilter.sharedMesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = BoundsCorners.Corner(bounds, corner);
                    Vector3 inBone = toClosed * bone.InverseTransformPoint(meshFilter.transform.TransformPoint(local));
                    centerSum += inBone;
                    corners++;
                    furthest = Mathf.Max(furthest, Vector3.ProjectOnPlane(inBone, axis).magnitude);
                }
            }
            if (corners == 0)
                return Vector3.zero;
            Vector3 center = centerSum / corners;
            Vector3 across = Vector3.ProjectOnPlane(center, axis);
            return across.sqrMagnitude > 1e-8f ? center - across + across.normalized * furthest : center;
        }

        // True when the mount is on this gun and visible (the PKM's rig has none; a rig's mount can be hidden)
        private static bool LatchPresent(FirearmController firearmController, LmgData data, out HingeRig rig)
        {
            if (!EnsureHinge(firearmController, data, HingeKind.Latch, out rig))
                return false;
            if (Time.time > rig.visibleCheckTime)
            {
                rig.visibleCheckTime = Time.time + 2f;
                rig.visible = false;
                foreach (Renderer renderer in rig.bone.GetComponentsInChildren<Renderer>(false))
                {
                    if (renderer.enabled)
                    {
                        rig.visible = true;
                        break;
                    }
                }
            }
            return rig.visible;
        }

        // With a scope on the mount, the mount has to be swung aside before the cover lifts (Tarkov's _sight clips play only then)
        private static bool LatchNeeded(FirearmController firearmController, LmgData data)
        {
            if (!LatchPresent(firearmController, data, out _))
                return false;
            foreach (Slot slot in firearmController.Item.AllSlots)
            {
                if (slot.ID == "mod_scope" && slot.ContainedItem != null)
                    return true;
            }
            return false;
        }

        private static float HingeValue(LmgState state, HingeKind kind) => kind == HingeKind.Cover ? state.cover : state.latch;

        private static void PoseHinge(HingeRig rig, HingeData hinge, float open)
        {
            rig.bone.localRotation = rig.closed * Quaternion.AngleAxis(hinge.angle * open, hinge.axis);
        }

        // The controller's offset from the hinge across its axis, in the bone's parent frame (so turning the gun doesn't count).
        // The angle from the grab's offset to this one is the turn since the grab.
        private static Vector3 AcrossHinge(HingeRig rig, HingeData hinge, Vector3 world)
        {
            Transform parent = rig.bone.parent;
            Vector3 axis = rig.closed * hinge.axis;
            return Vector3.ProjectOnPlane(parent.InverseTransformPoint(world) - rig.bone.localPosition, axis);
        }

        private static void GrabHinge(FirearmController firearmController, LmgData data, HingeKind kind, bool gunHand)
        {
            if (!EnsureHinge(firearmController, data, kind, out HingeRig rig))
                return;
            LmgState state = LmgStateOf(firearmController.Item);
            SoundHand hand = gunHand ? SoundHand.GunHand : SoundHand.OffHand;
            if (kind == HingeKind.Cover && LatchNeeded(firearmController, data) && state.latch < latchOpenToWork)
            {
                Refuse(hand);
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo("[VRBelt] the cover stays shut: the scope's mount is over it (swing its latch aside)");
                return;
            }
            if (kind == HingeKind.Latch && state.cover > 0.02f)
            {
                Refuse(hand);
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo("[VRBelt] the mount stays: the cover is open");
                return;
            }
            HingeData hinge = HingeOf(data, kind);
            HoldingHinge = true;
            heldHinge = kind;
            hingeInGunHand = gunHand;
            hingeOnRightArm = OnRightArm(gunHand);
            hingeHeldFor = firearmController;
            hingeOpenAtGrab = HingeValue(state, kind);
            if (kind == HingeKind.Cover)
                state.coverSettle = -1f;
            else
                state.latchSettle = -1f;
            Transform controller = HandRig(gunHand);
            PoseHinge(rig, hinge, hingeOpenAtGrab);
            hingeAcrossAtGrab = AcrossHinge(rig, hinge, controller.position);
            // The hand rides the part from where it took hold
            if (hingeRider == null)
                hingeRider = new GameObject("VRHingeHand").transform;
            hingeRider.SetParent(rig.bone, worldPositionStays: false);
            hingeRider.SetPositionAndRotation(controller.position, controller.rotation);
            LatchHand(hingeRider, gunHand);
            SetLatchPivot(rig.grab.position);
            // The hand moves onto the clip's hold on the part and rides it from there (hingeRider is on the bone), with the clip's fingers
            heldPartHold = kind == HingeKind.Cover ? data.coverHold : data.latchHold;
            Vector3 palmPosition = Vector3.zero;
            Quaternion palmRotation = Quaternion.identity;
            bool snapped = PartHoldWorld(heldPartHold, rig.bone, hingeOnRightArm, out palmPosition, out palmRotation);
            if (snapped)
                SnapHandTo(hingeRider, palmPosition, palmRotation);
            if (kind == HingeKind.Latch)
                PlayFirstGunSound(firearmController, hand, "SightButtonIn");
            Pulse(hand, 0.3f, 0.03f);
            if (beltDebugLog)
                Plugin.MyLog.LogInfo($"[VRBelt] took the {kind} ({(gunHand ? "gun" : "off")} hand), {hingeOpenAtGrab:0.00} open, "
                    + (snapped ? $"onto the clip's hold ({(Vector3.Distance(palmPosition, controller.position) * 100f):0.#} cm from the hand)" : heldPartHold == null ? "no hold read" : "no hold for this arm"));
        }

        // From Tick: the part follows the hand about its hinge
        private static void UpdateHinge(bool offTriggerReleased, bool gunTriggerReleased)
        {
            if (!HoldingHinge)
                return;
            FirearmController firearmController = hingeHeldFor;
            LmgData data = null;
            HingeRig rig = null;
            bool valid = VRGlobals.inGame && !VRGlobals.menuOpen && VRGlobals.vrPlayer != null && firearmController != null && firearmController == VRGlobals.firearmController
                && TryGetLmg(firearmController, out data) && EnsureHinge(firearmController, data, heldHinge, out rig)
                && (hingeInGunHand ? ChestHang.SupportOnly : !ChestHang.Detached) && hingeOnRightArm == OnRightArm(hingeInGunHand);
            if (!valid)
            {
                LetGoOfHinge();
                return;
            }
            LmgState state = LmgStateOf(firearmController.Item);
            HingeData hinge = HingeOf(data, heldHinge);
            Vector3 across = AcrossHinge(rig, hinge, LatchedHandRig.position);
            float open = HingeValue(state, heldHinge);
            if (across.sqrMagnitude > 1e-6f && hingeAcrossAtGrab.sqrMagnitude > 1e-6f)
                open = Mathf.Clamp01(hingeOpenAtGrab + Vector3.SignedAngle(hingeAcrossAtGrab, across, rig.closed * hinge.axis) / Mathf.Max(hinge.angle, 1f));
            SetHingeValue(firearmController, state, heldHinge, open, hingeInGunHand ? SoundHand.GunHand : SoundHand.OffHand);
            // Posed here in Update so the latched hand reads it this frame, and again after the animator (ApplyBeltGunBeforeIk)
            PoseHinge(rig, hinge, open);
            if (hingeInGunHand ? gunTriggerReleased : offTriggerReleased)
                LetGoOfHinge();
        }

        private static void LetGoOfHinge()
        {
            if (!HoldingHinge)
                return;
            HoldingHinge = false;
            FirearmController firearmController = hingeHeldFor;
            hingeHeldFor = null;
            heldPartHold = null;
            ReleaseHandLatch();
            if (firearmController == null || firearmController.Item == null)
                return;
            LmgState state = LmgStateOf(firearmController.Item);
            float value = HingeValue(state, heldHinge);
            float settle = value >= hingeOpenEnough ? 1f : 0f;
            if (heldHinge == HingeKind.Cover)
                state.coverSettle = settle;
            else
            {
                state.latchSettle = settle;
                PlayFirstGunSound(firearmController, hingeInGunHand ? SoundHand.GunHand : SoundHand.OffHand, "SightButtonOut");
            }
        }

        // Sounds and haptic clicks at the ends of the swing: open as it leaves shut, shut as it gets there (a cover shutting on a laid
        // belt also plays the belt going into the feed)
        private static void SetHingeValue(FirearmController firearmController, LmgState state, HingeKind kind, float value, SoundHand hand)
        {
            float before = HingeValue(state, kind);
            if (kind == HingeKind.Cover)
                state.cover = value;
            else
                state.latch = value;
            if (before <= 0.001f && value > 0.001f)
            {
                if (kind == HingeKind.Cover)
                    PlayFirstGunSound(firearmController, hand, "OpenCover", "OpenDust");
                else
                    PlayFirstGunSound(firearmController, hand, "SightOpen");
            }
            else if (before > 0.001f && value <= 0.001f)
            {
                if (kind == HingeKind.Cover)
                {
                    if (!PlayFirstGunSound(firearmController, hand, "CloseCover", "CloseDust"))
                        PlayVariedGunSound(firearmController, hand, "DustClose");
                    if (!state.beltOut && firearmController.Item.GetCurrentMagazine() != null)
                        PlayFirstGunSound(firearmController, SoundHand.GunBody, "BeltChamberIn", "BeltToChamber");
                }
                else
                    PlayFirstGunSound(firearmController, hand, "SightClose");
                Pulse(hand.Seating, 0.5f, 0.04f);
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBelt] {kind} shut{(kind == HingeKind.Cover ? (state.beltOut ? ", belt out" : ", belt laid") : "")}");
            }
            else if (before < 0.999f && value >= 0.999f)
            {
                Pulse(hand, 0.3f, 0.03f);
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBelt] {kind} all the way open");
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The belt: a chain of points from the last link held in the box to the far end of the last one

        private sealed class BeltRope
        {
            public Transform box;
            public BeltData data;
            public Transform[] links;
            public Vector3[] restLocalPositions;
            public Quaternion[] restLocalRotations;
            public Vector3[] points, previous;      // count + 1: the links, then the far end of the last one
            public float[] lengths;                 // point i to i + 1
            public int pinned;                      // the last point held to the box (at its baked pose); it drops as the belt is drawn out
            public bool simulating;
            public Vector3 lastBoxPosition;
            public Vector3[] laidPositions;         // the animator's pose this frame (for the gun's box, the laid belt)
            public Quaternion[] laidRotations;
            public Vector3 laidEndLocal;            // the laid belt's end, in the box's frame (read in Update)
            public Transform body;                  // the box's own body mesh (its biggest that isn't the belt's); the free links are kept out of it
            public Bounds bodyBounds;               // in that mesh's frame
            public Transform[] spentCells;          // empty_link_*: the fired side of the belt
            public readonly List<GameObject> hiddenCells = new List<GameObject>();
            public bool[] collides;                 // per point: the clip's hanging pose has it clear of the body, so it's kept clear
            // Whether the animator poses this box's belt (its binding to the box worked). Checked on a frame after we wrote the links: if it
            // does, it re-poses every link each frame. If it doesn't, we hold the laid belt in Tarkov's laid pose ourselves.
            public bool animatorDrives = true;
            public bool drivesKnown;
            public Transform[] idleBones;           // BeltData.idleNames, found in this box (null where it has none of that name)
            public Quaternion[] writtenLocal;
            public int writtenFrame = -10;
        }
        private static readonly BeltRope gunRope = new BeltRope(), propRope = new BeltRope();
        private static Vector3[] beltStash = new Vector3[0];
        private static float beltStashTime = -10f;
        private static bool beltLaying, beltLiftFromTray;
        private static float beltLayProgress;
        private static float beltRollSoundTime;

        public static bool HoldingBelt { get; private set; }
        private static bool beltInGunHand, beltOnRightArm;
        private static bool BeltInGunHand => HoldingBelt && beltInGunHand;
        private static FirearmController beltHeldFor;
        private static Transform beltHandHolder;
        private static Vector3 beltEndInController;         // where the hand holds the end, in the controller's frame
        private static Vector3 beltEndAtGrab;
        private static float beltEndEase;
        private static Vector3 beltEndTarget;               // this frame's target for the end, world (within the belt's reach)
        private static bool beltEndAtTray;                  // the held end is where letting go lays it (one blip on the way in)
        // The end is held in the clip's pinch (heldPartHold): the last link is turned and placed so the hand holds it as the clip does, both
        // its ends pinned (the far one at beltFarTarget), easing in from how it lay at the grab
        private static bool beltEndTurned;
        private static Quaternion beltEndTurnAtGrab, beltEndTurn;
        private static Vector3 beltFarTarget;

        private static bool BindRope(BeltRope rope, Transform box, BeltData data)
        {
            if (rope.box == box && rope.data == data && rope.links != null)
                return true;
            ReleaseRope(rope);
            Transform[] links = new Transform[data.count];
            foreach (Transform part in box.GetComponentsInChildren<Transform>(true))
            {
                string name = part.name;
                // belt_link_fake_* is another chain: its number doesn't parse
                if (name.StartsWith(data.prefix) && int.TryParse(name.Substring(data.prefix.Length), out int number) && number >= 1 && number <= data.count
                    && links[number - 1] == null)
                    links[number - 1] = part;
            }
            foreach (Transform link in links)
            {
                if (link == null)
                {
                    if (beltDebugLog)
                        Plugin.MyLog.LogWarning($"[VRBelt] box '{box.name}': not all {data.count} belt links found");
                    return false;
                }
            }
            int count = data.count;
            rope.box = box;
            rope.data = data;
            rope.links = links;
            rope.restLocalPositions = new Vector3[count];
            rope.restLocalRotations = new Quaternion[count];
            for (int link = 0; link < count; link++)
            {
                rope.restLocalPositions[link] = links[link].localPosition;
                rope.restLocalRotations[link] = links[link].localRotation;
            }
            rope.points = new Vector3[count + 1];
            rope.previous = new Vector3[count + 1];
            rope.lengths = new float[count];
            rope.laidPositions = new Vector3[count];
            rope.laidRotations = new Quaternion[count];
            rope.writtenLocal = new Quaternion[count];
            rope.writtenFrame = -10;
            rope.animatorDrives = true;
            rope.drivesKnown = false;
            rope.idleBones = null;
            if (data.idleNames != null)
            {
                // By name in this box (unique in each box model; ammo_belt_fake's own empty links are another root, not under it)
                Dictionary<string, Transform> byName = new Dictionary<string, Transform>();
                foreach (Transform part in box.GetComponentsInChildren<Transform>(true))
                {
                    if (!byName.ContainsKey(part.name))
                        byName[part.name] = part;
                }
                rope.idleBones = new Transform[data.idleNames.Length];
                for (int bone = 0; bone < data.idleNames.Length; bone++)
                    byName.TryGetValue(data.idleNames[bone], out rope.idleBones[bone]);
            }
            float scale = Mathf.Abs(box.lossyScale.x);
            for (int link = 0; link < count - 1; link++)
                rope.lengths[link] = Mathf.Max((data.positions[link + 1] - data.positions[link]).magnitude, data.spacing * 0.5f) * scale;
            rope.lengths[count - 1] = data.spacing * scale;
            rope.simulating = false;
            // The box's body: its biggest mesh outside the belt
            Transform beltRoot = links[0].parent;
            rope.body = null;
            float biggest = 0f;
            foreach (MeshFilter meshFilter in box.GetComponentsInChildren<MeshFilter>(false))
            {
                if (meshFilter.sharedMesh == null || (beltRoot != null && meshFilter.transform.IsChildOf(beltRoot)))
                    continue;
                Vector3 size = Vector3.Scale(meshFilter.sharedMesh.bounds.size, meshFilter.transform.lossyScale);
                float volume = Mathf.Abs(size.x * size.y * size.z);
                if (volume > biggest)
                {
                    biggest = volume;
                    rope.body = meshFilter.transform;
                    rope.bodyBounds = meshFilter.sharedMesh.bounds;
                }
            }
            List<Transform> cells = new List<Transform>();
            foreach (Transform part in box.GetComponentsInChildren<Transform>(true))
            {
                if (part.name.StartsWith("empty_link_", System.StringComparison.Ordinal))
                    cells.Add(part);
            }
            rope.spentCells = cells.ToArray();
            rope.hiddenCells.Clear();
            return true;
        }

        // Out of the tray, the fired side's empty links are hidden (Tarkov's belt visual shows them per shot; no shot happens while it's out)
        private static void HideSpentCells(BeltRope rope)
        {
            if (rope.spentCells == null)
                return;
            foreach (Transform cell in rope.spentCells)
            {
                if (cell != null && cell.gameObject.activeSelf)
                {
                    cell.gameObject.SetActive(false);
                    rope.hiddenCells.Add(cell.gameObject);
                }
            }
        }

        private static void ShowSpentCells(BeltRope rope)
        {
            foreach (GameObject cell in rope.hiddenCells)
            {
                if (cell != null)
                    cell.SetActive(true);
            }
            rope.hiddenCells.Clear();
        }

        // Stops the chain (the belt is laid and the animator poses it, or the box is gone) and shows what it hid
        private static void StopRope(BeltRope rope)
        {
            rope.simulating = false;
            ShowSpentCells(rope);
        }

        // Puts its links back as they were (the box is from Tarkov's pool, on the gun or in the hand)
        private static void ReleaseRope(BeltRope rope)
        {
            ShowSpentCells(rope);
            if (rope.links != null)
            {
                for (int link = 0; link < rope.links.Length; link++)
                {
                    if (rope.links[link] != null)
                    {
                        rope.links[link].localPosition = rope.restLocalPositions[link];
                        rope.links[link].localRotation = rope.restLocalRotations[link];
                    }
                }
            }
            rope.box = null;
            rope.data = null;
            rope.links = null;
            rope.simulating = false;
        }

        private static Vector3 EndOfLink(BeltRope rope, Vector3 lastPosition, Quaternion lastRotation)
        {
            // Each link's next one sits along its own -x
            return lastPosition + lastRotation * (Vector3.left * rope.lengths[rope.data.count - 1]);
        }

        private static void StartRope(BeltRope rope, Vector3[] from)
        {
            int count = rope.data.count;
            for (int point = 0; point < count; point++)
                rope.points[point] = from != null ? from[point] : rope.box.TransformPoint(rope.data.positions[point]);
            rope.points[count] = from != null && from.Length > count ? from[count]
                : EndOfLink(rope, rope.points[count - 1], rope.box.rotation * rope.data.rotations[count - 1]);
            System.Array.Copy(rope.points, rope.previous, count + 1);
            rope.pinned = rope.data.anchor - 1;
            rope.lastBoxPosition = rope.box.position;
            rope.simulating = true;
            HideSpentCells(rope);
            // Only the free links the clip itself has clear of the body are kept out of it (not those at the box's opening)
            if (rope.collides == null || rope.collides.Length != count + 1)
                rope.collides = new bool[count + 1];
            for (int point = 0; point <= count; point++)
            {
                Vector3 hanging = HangingPoint(rope, point);
                rope.collides[point] = point > rope.data.anchor && (OutOfBody(rope, hanging) - hanging).sqrMagnitude < 1e-10f;
            }
        }

        // Where the clip has a link hanging, in world space now (the far end for the point past the last link)
        private static Vector3 HangingPoint(BeltRope rope, int point)
        {
            int count = rope.data.count;
            if (point < count)
                return rope.box.TransformPoint(rope.data.positions[point]);
            return EndOfLink(rope, rope.box.TransformPoint(rope.data.positions[count - 1]), rope.box.rotation * rope.data.rotations[count - 1]);
        }

        // Pushes a point out of the box's body: a point inside its bounds (widened by beltBoxMargin, in the mesh's frame) moves out through
        // the nearest face
        private static Vector3 OutOfBody(BeltRope rope, Vector3 point)
        {
            if (rope.body == null)
                return point;
            Vector3 local = rope.body.InverseTransformPoint(point);
            Vector3 scale = rope.body.lossyScale;
            Vector3 margin = new Vector3(beltBoxMargin / Mathf.Max(Mathf.Abs(scale.x), 1e-4f), beltBoxMargin / Mathf.Max(Mathf.Abs(scale.y), 1e-4f),
                beltBoxMargin / Mathf.Max(Mathf.Abs(scale.z), 1e-4f));
            Vector3 min = rope.bodyBounds.min - margin, max = rope.bodyBounds.max + margin;
            if (local.x <= min.x || local.x >= max.x || local.y <= min.y || local.y >= max.y || local.z <= min.z || local.z >= max.z)
                return point;
            // The shortest way out, in metres
            float best = float.MaxValue;
            Vector3 moved = local;
            for (int axis = 0; axis < 3; axis++)
            {
                float axisScale = Mathf.Abs(scale[axis]);
                float toMin = (local[axis] - min[axis]) * axisScale, toMax = (max[axis] - local[axis]) * axisScale;
                if (toMin < best)
                {
                    best = toMin;
                    moved = local;
                    moved[axis] = min[axis];
                }
                if (toMax < best)
                {
                    best = toMax;
                    moved = local;
                    moved[axis] = max[axis];
                }
            }
            return rope.body.TransformPoint(moved);
        }

        private static void StashRope(BeltRope rope)
        {
            if (rope.points == null)
                return;
            beltStash = (Vector3[])rope.points.Clone();
            beltStashTime = Time.time;
        }

        private static void StashLinks(BeltRope rope)
        {
            int count = rope.data.count;
            Vector3[] points = new Vector3[count + 1];
            for (int link = 0; link < count; link++)
                points[link] = rope.links[link].position;
            points[count] = EndOfLink(rope, points[count - 1], rope.links[count - 1].rotation);
            beltStash = points;
            beltStashTime = Time.time;
        }

        private static Vector3[] FreshStash(BeltRope rope)
        {
            return Time.time - beltStashTime < 0.5f && beltStash.Length == rope.data.count + 1 ? beltStash : null;
        }

        private static float FreeLength(BeltRope rope)
        {
            float length = 0f;
            for (int point = rope.pinned; point < rope.data.count - 1; point++)
                length += rope.lengths[point];
            return length;
        }

        private static bool PointFixed(BeltRope rope, int point, bool endHeld) => point <= rope.pinned || (endHeld && point == rope.data.count - 1)
            || (endHeld && point == rope.data.count && EndTurnHeld(rope));

        // The gun's belt is held in the clip's pinch (UpdateBeltHold), so the hand also fixes the far end of its last link
        private static bool EndTurnHeld(BeltRope rope) => rope == gunRope && HoldingBelt && beltEndTurned;

        // Applies gravity and the last step's motion to the free points, then enforces the link lengths and the bend limit over a few iterations
        private static void StepRope(BeltRope rope, bool endHeld, Vector3 endTarget, float deltaTime)
        {
            int count = rope.data.count;
            Vector3[] points = rope.points, previous = rope.previous;
            Vector3 boxMove = rope.box.position - rope.lastBoxPosition;
            if (boxMove.sqrMagnitude > beltJumpDistance * beltJumpDistance)
            {
                for (int point = 0; point <= count; point++)
                {
                    points[point] += boxMove;
                    previous[point] += boxMove;
                }
            }
            rope.lastBoxPosition = rope.box.position;
            for (int point = 0; point <= rope.pinned; point++)
                points[point] = previous[point] = rope.box.TransformPoint(rope.data.positions[point]);
            Vector3 fall = Physics.gravity * (beltGravity * deltaTime * deltaTime);
            float maxStep = beltMaxSpeed * deltaTime;
            // The clip's hanging shape pulls the free links back toward it (links drawn out beyond the clip's have no shape: they were in
            // the box). While held, the pull fades toward the end in the hand.
            int firstFree = rope.data.anchor;
            float span = Mathf.Max(1f, count - firstFree);
            for (int point = rope.pinned + 1; point <= count; point++)
            {
                Vector3 motion = Vector3.ClampMagnitude((points[point] - previous[point]) * beltDamping, maxStep);
                previous[point] = points[point];
                points[point] += motion + fall;
                if (point >= firstFree)
                {
                    float shape = beltShapeFree;
                    if (endHeld)
                    {
                        float alongToEnd = Mathf.Clamp01((point - firstFree) / span);
                        shape = beltShapeHeld * (1f - alongToEnd) * (1f - alongToEnd);
                    }
                    points[point] = Vector3.Lerp(points[point], HangingPoint(rope, point), shape);
                }
            }
            int end = count - 1;
            // Bent by b at the middle point, two links of length L reach 2 L cos(b / 2) apart
            float minFold = 2f * Mathf.Cos(0.5f * Mathf.Deg2Rad * Mathf.Clamp(beltMaxBendDegrees, 0f, 170f));
            bool farHeld = endHeld && EndTurnHeld(rope);
            for (int iteration = 0; iteration < beltIterations; iteration++)
            {
                if (endHeld)
                    points[end] = endTarget;
                if (farHeld)
                    points[count] = beltFarTarget;
                for (int point = rope.pinned; point < count; point++)
                {
                    Vector3 delta = points[point + 1] - points[point];
                    float distance = delta.magnitude;
                    if (distance < 1e-6f)
                        continue;
                    Vector3 correction = delta * ((distance - rope.lengths[point]) / distance);
                    bool firstFixed = PointFixed(rope, point, endHeld), secondFixed = PointFixed(rope, point + 1, endHeld);
                    if (firstFixed && secondFixed)
                        continue;
                    if (firstFixed)
                        points[point + 1] -= correction;
                    else if (secondFixed)
                        points[point] += correction;
                    else
                    {
                        points[point] += correction * 0.5f;
                        points[point + 1] -= correction * 0.5f;
                    }
                }
                // Keep the free links the clip has clear of the box's body (the drum) out of it
                if (rope.collides != null)
                {
                    for (int point = rope.pinned + 1; point <= count; point++)
                    {
                        if (rope.collides[point] && !PointFixed(rope, point, endHeld))
                            points[point] = OutOfBody(rope, points[point]);
                    }
                }
                // Points two links apart come no closer than the sharpest bend allows (a belt folds, it doesn't kink)
                for (int point = rope.pinned; point < count - 1; point++)
                {
                    Vector3 delta = points[point + 2] - points[point];
                    float distance = delta.magnitude;
                    float least = minFold * 0.5f * (rope.lengths[point] + rope.lengths[point + 1]);
                    if (distance >= least || distance < 1e-6f)
                        continue;
                    Vector3 push = delta * ((least - distance) / distance);
                    bool firstFixed = PointFixed(rope, point, endHeld), secondFixed = PointFixed(rope, point + 2, endHeld);
                    if (firstFixed && secondFixed)
                        continue;
                    if (firstFixed)
                        points[point + 2] += push;
                    else if (secondFixed)
                        points[point] -= push;
                    else
                    {
                        points[point] -= push * 0.5f;
                        points[point + 2] += push * 0.5f;
                    }
                }
            }
            if (endHeld)
                points[end] = endTarget;
            if (farHeld)
                points[count] = beltFarTarget;
        }

        // Writes the links onto the points: the ones in the box as the clip has them, the rest turned from the previous link to lie along
        // their own segment (no twist of their own). layBlend blends toward the animator's laid pose.
        private static void WriteRope(BeltRope rope, float layBlend)
        {
            int count = rope.data.count;
            Vector3[] points = rope.points;
            Quaternion previousRotation = Quaternion.identity;
            for (int link = 0; link < count; link++)
            {
                Quaternion rotation;
                if (link <= rope.pinned)
                    rotation = rope.box.rotation * rope.data.rotations[link];
                else
                {
                    Vector3 along = points[link + 1] - points[link];
                    rotation = along.sqrMagnitude > 1e-10f ? Quaternion.FromToRotation(previousRotation * Vector3.left, along) * previousRotation : previousRotation;
                }
                previousRotation = rotation;
                // In the clip's pinch the end link is exactly as the hand holds it (the segment alone would leave it the previous link's twist)
                if (link == count - 1 && EndTurnHeld(rope))
                    rotation = beltEndTurn;
                Vector3 position = points[link];
                if (layBlend > 0f)
                {
                    position = Vector3.Lerp(position, rope.laidPositions[link], layBlend);
                    rotation = Quaternion.Slerp(rotation, rope.laidRotations[link], layBlend);
                }
                rope.links[link].SetPositionAndRotation(position, rotation);
            }
            RememberWritten(rope);
        }

        private static void RememberWritten(BeltRope rope)
        {
            for (int link = 0; link < rope.links.Length; link++)
                rope.writtenLocal[link] = rope.links[link].localRotation;
            rope.writtenFrame = Time.frameCount;
        }

        // Poses the box's other bones the idle clip animates (on a box the animator doesn't pose, they'd stay in the model's rest shape)
        private static void WriteIdleBones(BeltRope rope)
        {
            BeltData data = rope.data;
            if (rope.idleBones == null || data.idleNames == null)
                return;
            for (int bone = 0; bone < rope.idleBones.Length; bone++)
            {
                Transform part = rope.idleBones[bone];
                if (part == null)
                    continue;
                part.localPosition = data.idlePositions[bone];
                part.localRotation = data.idleRotations[bone];
                part.localScale = data.idleScales[bone];
            }
        }

        // Tarkov's laid belt, held by us (a box the animator doesn't pose)
        private static void WriteLaid(BeltRope rope)
        {
            for (int link = 0; link < rope.links.Length; link++)
                rope.links[link].SetPositionAndRotation(rope.laidPositions[link], rope.laidRotations[link]);
            RememberWritten(rope);
        }

        private static Transform GunBoxView(FirearmController firearmController)
        {
            Slot slot = firearmController.Item.GetMagazineSlot();
            if (slot == null || slot.ContainedItem == null || firearmController.Firearms == null)
                return null;
            ContainerCollectionView.SlotView view = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(slot);
            return view != null ? view.ItemView : null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The belt in the hand

        private static void GrabBelt(FirearmController firearmController, bool gunHand, bool fromTray)
        {
            LmgState state = LmgStateOf(firearmController.Item);
            Transform controller = HandRig(gunHand);
            SoundHand hand = gunHand ? SoundHand.GunHand : SoundHand.OffHand;
            int count = gunRope.data.count;
            if (fromTray)
            {
                // Lifted out of the tray: the chain starts from the animator's laid pose (ApplyBeltGunBeforeIk)
                state.beltOut = true;
                beltLiftFromTray = true;
                beltEndAtGrab = gunRope.links[count - 1].position;
                PlayFirstGunSound(firearmController, hand, "BeltOut");
            }
            else
            {
                beltEndAtGrab = gunRope.points[count - 1];
                PlayFirstGunSound(firearmController, hand, "BeltRoll", "BeltOut");
            }
            HoldingBelt = true;
            beltInGunHand = gunHand;
            beltOnRightArm = OnRightArm(gunHand);
            beltHeldFor = firearmController;
            beltEndInController = controller.InverseTransformPoint(HandPalm(gunHand));
            beltEndEase = 0f;
            beltEndTarget = beltEndAtGrab;
            beltEndAtTray = fromTray;
            // The clip's pinch on the belt's end (gen_lmgs), only on the arm it was read on: a loose belt isn't mirrored (its end is off the gun's plane)
            LmgData data = TryGetLmg(firearmController, out LmgData lmg) ? lmg : null;
            heldPartHold = data?.beltHold;
            beltEndTurned = heldPartHold != null && heldPartHold.rightHand == beltOnRightArm;
            beltEndTurnAtGrab = beltEndTurn = gunRope.links[count - 1].rotation;
            if (beltHandHolder == null)
                beltHandHolder = new GameObject("VRBeltHand").transform;
            beltHandHolder.SetPositionAndRotation(controller.position, controller.rotation);
            LatchHand(beltHandHolder, gunHand);
            Pulse(hand, 0.3f, 0.03f);
            if (beltDebugLog)
                Plugin.MyLog.LogInfo($"[VRBelt] took the belt {(fromTray ? "out of the tray" : "by its end")} ({(gunHand ? "gun" : "off")} hand), "
                    + (beltEndTurned ? "into the clip's pinch" : heldPartHold == null ? "no hold read" : "no hold for this arm"));
        }

        // From Tick: the end follows the hand within the belt's reach (pulling draws another link out of the box), and the hand stops where
        // the belt does. Let go at the tray with the cover open, the belt is laid.
        private static void UpdateBeltHold(bool offTriggerReleased, bool gunTriggerReleased)
        {
            if (!HoldingBelt)
                return;
            FirearmController firearmController = beltHeldFor;
            LmgData data = null;
            bool valid = VRGlobals.inGame && !VRGlobals.menuOpen && VRGlobals.vrPlayer != null && firearmController != null && firearmController == VRGlobals.firearmController
                && TryGetLmg(firearmController, out data) && data.belt != null && gunRope.links != null && firearmController.Item.GetCurrentMagazine() != null
                && (beltInGunHand ? ChestHang.SupportOnly : !ChestHang.Detached) && beltOnRightArm == OnRightArm(beltInGunHand);
            if (!valid)
            {
                LetGoOfBelt();
                return;
            }
            LmgState state = LmgStateOf(firearmController.Item);
            Transform controller = HandRig(beltInGunHand);
            Vector3 wanted = controller.TransformPoint(beltEndInController);
            // In the clip's pinch the hand (latched on the controller) is the palm, and the link goes where that puts it (palm = link * hold)
            Quaternion wantedTurn = Quaternion.identity;
            if (beltEndTurned)
            {
                wantedTurn = controller.rotation * Quaternion.Inverse(heldPartHold.rotation);
                wanted = controller.position - wantedTurn * heldPartHold.position;
            }
            beltEndEase = Mathf.MoveTowards(beltEndEase, 1f, Time.deltaTime / Mathf.Max(beltTipEaseTime, 0.001f));
            float easeShown = Mathf.SmoothStep(0f, 1f, beltEndEase);
            Vector3 eased = Vector3.Lerp(beltEndAtGrab, wanted, easeShown);
            if (beltEndTurned)
                beltEndTurn = Quaternion.Slerp(beltEndTurnAtGrab, wantedTurn, easeShown);
            Vector3 target = eased;
            if (gunRope.simulating)
            {
                Vector3 mouth = gunRope.points[gunRope.pinned];
                float reach = FreeLength(gunRope) * 0.98f;
                // Pulled further, one more link comes out of the box, up to beltExtraOut past the clip's
                if ((target - mouth).magnitude > reach && gunRope.pinned > Mathf.Max(0, data.belt.anchor - 1 - beltExtraOut))
                {
                    gunRope.pinned--;
                    mouth = gunRope.points[gunRope.pinned];
                    reach = FreeLength(gunRope) * 0.98f;
                    if (Time.time > beltRollSoundTime)
                    {
                        beltRollSoundTime = Time.time + 0.3f;
                        PlayFirstGunSound(firearmController, beltInGunHand ? SoundHand.GunHand : SoundHand.OffHand, "BeltRoll");
                    }
                }
                if ((target - mouth).magnitude > reach)
                    target = mouth + (target - mouth).normalized * reach;
            }
            beltEndTarget = target;
            if (beltEndTurned)
                beltFarTarget = EndOfLink(gunRope, target, beltEndTurn);
            // The hand stays on the controller, but no further than the belt reaches
            beltHandHolder.SetPositionAndRotation(controller.position + (target - eased), controller.rotation);
            // At the tray with the cover open: a blip on entering, and letting go there lays it; let go anywhere else and it drops and hangs.
            // (Laying on touch alone would put a belt lifted out of the tray straight back in.)
            bool atTray = CoverOpenEnough(state) && (target - gunRope.box.TransformPoint(gunRope.laidEndLocal)).magnitude <= beltLayRadius;
            if (atTray && !beltEndAtTray)
                Pulse(beltInGunHand ? SoundHand.GunHand : SoundHand.OffHand, 0.25f, 0.03f);
            beltEndAtTray = atTray;
            if (beltInGunHand ? gunTriggerReleased : offTriggerReleased)
            {
                if (atTray)
                    BeginLay(firearmController);
                else
                    LetGoOfBelt();
            }
        }

        private static void BeginLay(FirearmController firearmController)
        {
            SoundHand hand = beltInGunHand ? SoundHand.GunHand : SoundHand.OffHand;
            HoldingBelt = false;
            beltHeldFor = null;
            beltEndTurned = false;
            heldPartHold = null;
            ReleaseHandLatch();
            beltLaying = true;
            beltLayProgress = 0f;
            PlayFirstGunSound(firearmController, hand, "BeltIn");
            Pulse(hand.Seating, 0.45f, 0.04f);
            if (beltDebugLog)
                Plugin.MyLog.LogInfo("[VRBelt] belt laid in the tray");
        }

        private static void LetGoOfBelt()
        {
            if (!HoldingBelt)
                return;
            HoldingBelt = false;
            beltHeldFor = null;
            beltEndTurned = false;
            heldPartHold = null;
            ReleaseHandLatch();
            if (beltDebugLog)
                Plugin.MyLog.LogInfo("[VRBelt] let go of the belt");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // From Tick

        // A hand at the latch, the cover or the belt takes it with the trigger. True = the trigger press was used here this tick.
        private static bool TickBeltGunGrabs(FirearmController firearmController, Weapon weapon, bool triggerPressed, bool gunHand)
        {
            if (!TryGetLmg(firearmController, out LmgData data) || HoldingHinge || HoldingBelt || beltLaying)
                return false;
            bool handMay = gunHand ? ChestHang.SupportOnly : !VRGlobals.vrPlayer.isSupporting && !ChestHang.Detached;
            if (!handMay)
                return false;
            LmgState state = LmgStateOf(weapon);
            float nearest = float.MaxValue;
            int part = -1;                  // 0 cover, 1 latch, 2 the belt's hanging end, 3 the belt in the tray
            // Only the parts this hand works in the clips (HandWorksPart); only their zones are shown for this hand
            bool coverHand = HandWorksPart(data, data.coverByGunHand, gunHand), latchHand = HandWorksPart(data, data.latchByGunHand, gunHand);
            bool beltHand = HandWorksPart(data, data.beltByGunHand, gunHand);
            bool showZones = debugZones;
            if (coverHand && EnsureHinge(firearmController, data, HingeKind.Cover, out HingeRig cover))
            {
                float outside = HandDistanceToCapsule(gunHand, cover.grab.position, cover.grab.position) - hingeGrabRadius;
                if (outside <= 0f && outside < nearest)
                {
                    nearest = outside;
                    part = 0;
                }
                if (showZones)
                    SetZoneViz(ref coverZoneViz, true, cover.grab.position, hingeGrabRadius, outside <= 0f ? new Color(0f, 1f, 0f, 0.3f) : new Color(0f, 0.6f, 1f, 0.3f));
            }
            if (latchHand && LatchPresent(firearmController, data, out HingeRig latch))
            {
                Vector3 from = latch.button != null ? latch.button.position : latch.grab.position;
                float outside = HandDistanceToCapsule(gunHand, from, latch.grab.position) - hingeGrabRadius;
                if (outside <= 0f && outside < nearest)
                {
                    nearest = outside;
                    part = 1;
                }
                if (showZones)
                    SetCapsuleZoneViz(ref latchZoneViz, from, latch.grab.position, hingeGrabRadius, outside <= 0f ? new Color(0f, 1f, 0f, 0.3f) : new Color(0.7f, 0.3f, 1f, 0.3f));
            }
            bool boxOn = beltHand && weapon.GetCurrentMagazine() != null && gunRope.links != null && gunRope.box == GunBoxView(firearmController);
            if (boxOn && state.beltOut && gunRope.simulating)
            {
                int count = gunRope.data.count;
                Vector3 mouth = gunRope.points[gunRope.pinned], end = gunRope.points[count - 1];
                float outside = HandDistanceToCapsule(gunHand, mouth, end) - beltGrabRadius;
                if (outside <= 0f && outside < nearest)
                {
                    nearest = outside;
                    part = 2;
                }
                if (showZones)
                    SetCapsuleZoneViz(ref beltZoneViz, mouth, end, beltGrabRadius, outside <= 0f ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.9f, 0f, 0.3f));
            }
            if (boxOn && !state.beltOut && CoverOpenEnough(state))
            {
                Vector3 end = gunRope.links[gunRope.data.count - 1].position;
                float outside = HandDistanceToCapsule(gunHand, end, end) - beltGrabRadius;
                if (outside <= 0f && outside < nearest)
                {
                    nearest = outside;
                    part = 3;
                }
            }
            // The tray: where the held end goes in
            if (showZones)
            {
                bool showTray = boxOn && state.beltOut && CoverOpenEnough(state);
                SetZoneViz(ref trayZoneViz, showTray, showTray ? gunRope.box.TransformPoint(gunRope.laidEndLocal) : Vector3.zero, beltLayRadius, new Color(0f, 1f, 0.4f, 0.3f));
            }
            if (part < 0)
                return false;
            if (!gunHand)
                OffHandAtChargingHandle = true;
            if (!triggerPressed)
                return false;
            if (part == 0)
                GrabHinge(firearmController, data, HingeKind.Cover, gunHand);
            else if (part == 1)
                GrabHinge(firearmController, data, HingeKind.Latch, gunHand);
            else
                GrabBelt(firearmController, gunHand, fromTray: part == 3);
            return true;
        }

        // Every tick: the cover and latch swinging after a let go, the latch's button, and Tarkov's own reload taking over
        private static void TickBeltGun(bool triggerReleased, bool gunHandTriggerReleased)
        {
            UpdateHinge(triggerReleased, gunHandTriggerReleased);
            UpdateBeltHold(triggerReleased, gunHandTriggerReleased);
            latchButtonPressed = Mathf.MoveTowards(latchButtonPressed, HoldingHinge && heldHinge == HingeKind.Latch ? 1f : 0f, Time.deltaTime / 0.08f);
            // Hide the zones; TickBeltGunGrabs (which runs after this) shows the ones a hand may take now
            if (debugZones)
            {
                SetZoneViz(ref coverZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref latchZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref beltZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref trayZoneViz, false, Vector3.zero, 0f, Color.clear);
            }
            FirearmController firearmController = VRGlobals.firearmController;
            if (!TryGetLmg(firearmController, out _))
            {
                beltLaying = false;
                return;
            }
            LmgState state = LmgStateOf(firearmController.Item);
            // Tarkov's own reload (its clips open, swap, lay and shut): everything is left as that reload leaves it
            if (firearmController.IsInReloadOperation() && expectedOperation == ExpectedOperation.None)
            {
                LetGoOfHinge();
                LetGoOfBelt();
                state.cover = state.latch = 0f;
                state.coverSettle = state.latchSettle = -1f;
                state.beltOut = state.waitsForCharge = false;
                beltLaying = false;
                StopRope(gunRope);
                return;
            }
            if (!HoldingHinge || heldHinge != HingeKind.Cover)
                SettleHinge(firearmController, state, HingeKind.Cover);
            if (!HoldingHinge || heldHinge != HingeKind.Latch)
                SettleHinge(firearmController, state, HingeKind.Latch);
        }

        private static void SettleHinge(FirearmController firearmController, LmgState state, HingeKind kind)
        {
            float target = kind == HingeKind.Cover ? state.coverSettle : state.latchSettle;
            if (target < 0f)
                return;
            float value = Mathf.MoveTowards(HingeValue(state, kind), target, Time.deltaTime / Mathf.Max(hingeSettleTime, 0.001f));
            SetHingeValue(firearmController, state, kind, value, SoundHand.GunBody);
            if (Mathf.Approximately(value, target))
            {
                if (kind == HingeKind.Cover)
                    state.coverSettle = -1f;
                else
                    state.latchSettle = -1f;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Where the support hand goes on a gun with two support spots (the PKM: the handguard at rest, the box when aiming). Two-handing
        // gives the left arm to Tarkov's IK on the gun's weapon_L_IK_marker, which the PKM's aiming clip moves onto the box. The hand takes
        // the nearer spot as it grabs (VRPlayerManager.SupportGripPoint) and keeps it: the marker is put back there every frame after the
        // animator. Works with or without Manual Reload; not with the gun in the left hand (that arm swap mirrors the grip).

        private static int supportSpot = -1;                // the spot the hand takes / has (index into LmgData.supportSpots)
        private static FirearmController supportSpotFor;

        private static bool TryGetSupportSpots(FirearmController firearmController, out SupportSpot[] spots, out Transform weaponBone)
        {
            spots = null;
            weaponBone = null;
            if (!enabled || Handedness.GunInLeftHand || firearmController == null || firearmController.Item == null
                || !firearmController.Item.IsBeltMachineGun || firearmController._player == null || !firearmController._player.IsYourPlayer
                || firearmController.HandsHierarchy == null)
                return false;
            LoadRecordedGrips();
            if (!lmgGuns.TryGetValue(AnimationFamily(firearmController), out LmgData data) || data.supportSpots == null)
                return false;
            Transform[] bones = firearmController.HandsHierarchy.Transforms;
            if (bones == null || bones.Length <= (int)ECharacterWeaponBones.weapon_L_IK_marker)
                return false;
            weaponBone = bones[(int)ECharacterWeaponBones.weapon];
            spots = data.supportSpots;
            return weaponBone != null && bones[(int)ECharacterWeaponBones.weapon_L_IK_marker] != null;
        }

        private static Vector3 SpotWorld(Transform weaponBone, SupportSpot spot) => weaponBone.position + weaponBone.rotation * spot.ikPosition;

        // For VRPlayerManager.SupportGripPoint, called in each UpdateLeftHand: where the support grab is. When not two-handing, the spot
        // nearest the hand (the one a grab would take now); when two-handing, the one it took. False = use the gun's own marker.
        public static bool SupportSpotPoint(out Vector3 point)
        {
            point = Vector3.zero;
            FirearmController firearmController = VRGlobals.firearmController;
            if (VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.LeftHand == null || !TryGetSupportSpots(firearmController, out SupportSpot[] spots, out Transform weaponBone))
            {
                supportSpot = -1;
                return false;
            }
            bool boxOn = firearmController.Item.GetCurrentMagazine() != null;
            if (!VRGlobals.vrPlayer.isSupporting || supportSpotFor != firearmController || supportSpot < 0 || supportSpot >= spots.Length
                || (spots[supportSpot].onBox && !boxOn))
            {
                Vector3 hand = VRGlobals.vrPlayer.LeftHand.transform.position;
                int nearest = 0;
                for (int spot = 1; spot < spots.Length; spot++)
                {
                    // The box spot only counts with a box on the gun
                    if (spots[spot].onBox && !boxOn)
                        continue;
                    if ((SpotWorld(weaponBone, spots[spot]) - hand).sqrMagnitude < (SpotWorld(weaponBone, spots[nearest]) - hand).sqrMagnitude)
                        nearest = spot;
                }
                supportSpot = nearest;
                supportSpotFor = firearmController;
            }
            point = SpotWorld(weaponBone, spots[supportSpot]);
            return true;
        }

        // After the animator: keeps the hand on the spot it took. Moving the left IK marker alone doesn't hold the hand there, so the arm
        // targets our own point on the spot (as the pump does in VRReloadController.Shotgun): the solver's target wins over the IK position
        // Tarkov's IkProcess writes from the marker. The rest spot uses Tarkov's own grip (its GripPose, _ikTargets[0]); the box spot uses
        // the aiming clip's marker. The marker is moved there too (the grip poser's weight follows it); AfterSupportIkProcess holds the
        // rest spot's grip fingers through an aim.
        private static Transform supportSpotTarget;
        private static bool supportSpotDriving;
        private static int supportSpotLogged = -1;
        private static float supportSpotCheckTime = -1f;

        private static void ApplySupportSpot()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            LimbIK leftArmIk = VRGlobals.ikManager != null ? VRGlobals.ikManager.leftArmIk : null;
            SupportSpot[] spots = null;
            Transform weaponBone = null;
            bool holding = supportSpot >= 0 && supportSpotFor == firearmController && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting
                && leftArmIk != null && leftArmIk.solver != null && VRGlobals.player != null
                && TryGetSupportSpots(firearmController, out spots, out weaponBone) && supportSpot < spots.Length
                // Don't take over from a reload latch or the pump's point while they hold the arm
                && (leftArmIk.solver.target == null || leftArmIk.solver.target == supportSpotTarget);
            if (!holding)
            {
                ReleaseSupportSpot(leftArmIk);
                return;
            }
            SupportSpot spot = spots[supportSpot];
            Vector3 markerPosition = SpotWorld(weaponBone, spot);
            Quaternion markerRotation = weaponBone.rotation * spot.ikRotation;
            Transform marker = firearmController.HandsHierarchy.Transforms[(int)ECharacterWeaponBones.weapon_L_IK_marker];
            marker.SetPositionAndRotation(markerPosition, markerRotation);
            Transform grip = supportSpot == 0 && VRGlobals.player._ikTargets != null && VRGlobals.player._ikTargets.Length > 0 ? VRGlobals.player._ikTargets[0] : null;
            if (supportSpotTarget == null)
                supportSpotTarget = new GameObject("VRSupportSpot").transform;
            // Parented to the gun so it follows whatever moves the gun later this frame
            if (supportSpotTarget.parent != weaponBone)
                supportSpotTarget.SetParent(weaponBone, worldPositionStays: false);
            supportSpotTarget.SetPositionAndRotation(grip != null ? grip.position : markerPosition, grip != null ? grip.rotation : markerRotation);
            leftArmIk.solver.target = supportSpotTarget;
            supportSpotDriving = true;
            if (supportSpotLogged != supportSpot)
            {
                supportSpotLogged = supportSpot;
                supportSpotCheckTime = Time.time + 0.5f;
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBelt] support hand on the {spot.name}: the controller {SpotDistances(spots, weaponBone, VRGlobals.vrPlayer.LeftHand.transform.position)}"
                        + (supportSpot == 0 ? $", Tarkov's grip {(grip != null ? "found" : "not found: on the marker")}" : ""));
            }
            // Shortly after, log where the rendered hand is (shows whether the hold worked)
            if (supportSpotCheckTime > 0f && Time.time >= supportSpotCheckTime)
            {
                supportSpotCheckTime = -1f;
                Transform palm = ArmPalm(rightArm: false);
                if (beltDebugLog && palm != null)
                    Plugin.MyLog.LogInfo($"[VRBelt] support hand drawn: {SpotDistances(spots, weaponBone, palm.position)}, {((palm.position - supportSpotTarget.position).magnitude * 100f):0.#} cm off its target");
            }
        }

        private static string SpotDistances(SupportSpot[] spots, Transform weaponBone, Vector3 point)
        {
            string text = "";
            for (int spot = 0; spot < spots.Length; spot++)
                text += $"{(spot > 0 ? ", " : "")}{((SpotWorld(weaponBone, spots[spot]) - point).magnitude * 100f):0.#} cm from the {spots[spot].name}";
            return text;
        }

        // Hands the left arm back: to Tarkov's IK (no target) while still two-handing, or to the controller if two-handing ended without
        // UpdateRightHand's release (VRPlayerManager.ForceUnlockHand only hands the arm back when it has no target)
        private static void ReleaseSupportSpot(LimbIK leftArmIk)
        {
            supportSpotLogged = -1;
            supportSpotCheckTime = -1f;
            if (!supportSpotDriving)
                return;
            supportSpotDriving = false;
            if (leftArmIk == null || leftArmIk.solver == null || leftArmIk.solver.target != supportSpotTarget)
                return;
            bool supporting = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting;
            if (supporting || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.LeftHand == null)
            {
                leftArmIk.solver.target = null;
                return;
            }
            if (VRGlobals.player != null)
                VRGlobals.player._markers[0] = VRGlobals.vrPlayer.LeftHand.transform;
            leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
            leftArmIk.enabled = true;
        }

        // Player.IkProcess postfix (BeltGunPatches). IkProcess has just set the left grip poser's weight from the marker's distance to the
        // grip; on the rest spot the hand keeps Tarkov's grip, aiming or not.
        public static void AfterSupportIkProcess(EFT.Player player)
        {
            if (supportSpotDriving && supportSpot == 0 && player != null && player.HandPosers != null && player.HandPosers.Length > 0 && player.HandPosers[0] != null)
                player.HandPosers[0].GripWeight = 1f;
        }

        // The finger pose for a spot other than the rest one (the box), from its clip. On the rest spot Tarkov's own grip poser shapes the
        // hand (the marker is on its grip reference); elsewhere the fingers are the animator's, which outside the aim are the handguard's.
        private static bool SupportSpotFingers(out Vector3[] fingers)
        {
            fingers = null;
            if (supportSpot <= 0 || VRGlobals.vrPlayer == null || !VRGlobals.vrPlayer.isSupporting || supportSpotFor != VRGlobals.firearmController
                || !TryGetSupportSpots(supportSpotFor, out SupportSpot[] spots, out _) || supportSpot >= spots.Length)
                return false;
            fingers = spots[supportSpot].fingers;
            return fingers != null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Player.LateUpdate prefix (ShotgunPatches): runs after the animator (which poses the cover, the latch and every belt link each frame)
        // and before Tarkov's IK reads where the hands go, so a hand latched to the cover rides it

        public static void ApplyBeltGunBeforeIk()
        {
            try
            {
                ApplySupportSpot();
                ApplyPropRope();
                FirearmController firearmController = VRGlobals.firearmController;
                if (!TryGetLmg(firearmController, out LmgData data))
                {
                    ReleaseRope(gunRope);
                    return;
                }
                LmgState state = LmgStateOf(firearmController.Item);
                if (EnsureHinge(firearmController, data, HingeKind.Cover, out HingeRig cover) && (state.cover > 0f || (HoldingHinge && heldHinge == HingeKind.Cover)))
                    PoseHinge(cover, data.cover, state.cover);
                if (EnsureHinge(firearmController, data, HingeKind.Latch, out HingeRig latch))
                {
                    if (state.latch > 0f || (HoldingHinge && heldHinge == HingeKind.Latch))
                        PoseHinge(latch, data.latch, state.latch);
                    if (latch.button != null && latchButtonPressed > 0f)
                        latch.button.localPosition = latch.buttonRest + data.latch.buttonTravel * latchButtonPressed;
                }
                Transform box = GunBoxView(firearmController);
                bool fits = BeltFits(firearmController.Item.GetCurrentMagazine(), data.belt);
                if (box == null || data.belt == null || !fits)
                {
                    ReleaseRope(gunRope);
                    if (box != null && !fits)
                        state.beltOut = false;
                    return;
                }
                if (!BindRope(gunRope, box, data.belt))
                    return;
                BeltRope rope = gunRope;
                int count = data.belt.count;
                // Does the animator pose this box's belt? Checked the frame after we wrote it: if it does, it re-poses every link each frame
                if (rope.writtenFrame == Time.frameCount - 1)
                {
                    bool reposed = false;
                    for (int link = 0; link < count && !reposed; link++)
                        reposed = Quaternion.Angle(rope.links[link].localRotation, rope.writtenLocal[link]) > 0.05f;
                    if (!rope.drivesKnown || rope.animatorDrives != reposed)
                    {
                        rope.drivesKnown = true;
                        rope.animatorDrives = reposed;
                        if (beltDebugLog)
                            Plugin.MyLog.LogInfo($"[VRBelt] {firearmController.Item.LocalizedShortName()}: the animator {(reposed ? "poses" : "doesn't pose")} this box's belt");
                    }
                }
                // The laid belt, which a lay eases to and which marks the tray: Tarkov's own (baked from its idle clip), else the animator's pose now
                bool baked = data.belt.laidPositions != null;
                for (int link = 0; link < count; link++)
                {
                    rope.laidPositions[link] = baked ? box.TransformPoint(data.belt.laidPositions[link]) : rope.links[link].position;
                    rope.laidRotations[link] = baked ? box.rotation * data.belt.laidRotations[link] : rope.links[link].rotation;
                }
                rope.laidEndLocal = box.InverseTransformPoint(rope.laidPositions[count - 1]);
                if (!state.beltOut && !beltLaying)
                {
                    StopRope(rope);
                    // Laid: the animator poses it, or we hold it in Tarkov's laid pose where the animator doesn't pose this box
                    if (!rope.animatorDrives && baked)
                        WriteLaid(rope);
                    if (!rope.animatorDrives)
                        WriteIdleBones(rope);
                    return;
                }
                if (!rope.simulating)
                {
                    // Lifted out of the tray: start from where the laid belt is now
                    if (beltLiftFromTray)
                    {
                        for (int link = 0; link < count; link++)
                            rope.points[link] = rope.links[link].position;
                        StartRope(rope, null);
                        for (int link = 0; link < count; link++)
                            rope.points[link] = rope.previous[link] = rope.links[link].position;
                        rope.points[count] = rope.previous[count] = EndOfLink(rope, rope.points[count - 1], rope.links[count - 1].rotation);
                    }
                    else
                        StartRope(rope, FreshStash(rope));
                    beltLiftFromTray = false;
                }
                float deltaTime = Mathf.Clamp(Time.deltaTime, 0.001f, 1f / 30f);
                bool endHeld = HoldingBelt || beltLaying;
                Vector3 endTarget = beltLaying ? rope.laidPositions[count - 1] : beltEndTarget;
                StepRope(rope, endHeld, endTarget, deltaTime);
                float blend = 0f;
                if (beltLaying)
                {
                    beltLayProgress += deltaTime / Mathf.Max(beltLayTime, 0.001f);
                    blend = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(beltLayProgress));
                }
                WriteRope(rope, blend);
                if (rope.drivesKnown && !rope.animatorDrives)
                    WriteIdleBones(rope);
                if (beltLaying && beltLayProgress >= 1f)
                {
                    // Laid: the animator takes over from here
                    beltLaying = false;
                    state.beltOut = false;
                    StopRope(rope);
                }
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[VRBelt] posing the belt gun failed: {exception}");
                beltLaying = false;
            }
        }

        // A belt box in the hand: its belt hangs out of it
        private static void ApplyPropRope()
        {
            LmgData data = null;
            bool wanted = HoldingMagazine && heldProp != null && heldMagazine != null && heldMagazine.IsMagazineWithBelt && heldForFirearm != null
                && TryGetLmg(heldForFirearm, out data) && data.belt != null && BeltFits(heldMagazine, data.belt);
            if (!wanted)
            {
                ReleaseRope(propRope);
                return;
            }
            if (!BindRope(propRope, heldProp.transform, data.belt))
                return;
            if (!propRope.simulating)
                StartRope(propRope, FreshStash(propRope));
            StepRope(propRope, false, Vector3.zero, Mathf.Clamp(Time.deltaTime, 0.001f, 1f / 30f));
            WriteRope(propRope, 0f);
        }

        // The hand's finger pose on these parts for this arm: the clip's (heldPartHold), else the magazine pull's pinch (the left hand's,
        // mirrored for the right arm)
        private static readonly Vector3[] mirroredBeltPinch = new Vector3[16];
        private static Vector3[] BeltGunFingers(bool rightArm)
        {
            Vector3[] clip = PartFingers(heldPartHold, rightArm);
            if (clip != null)
                return clip;
            return rightArm ? MirroredPose(MAGAZINE_PULL_FINGERS, rightToLeft: false, mirroredBeltPinch) ?? GUN_HAND_HANDLE_FINGERS : MAGAZINE_PULL_FINGERS;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // When a belt box goes on or off, Tarkov's WeaponPrefab.UpdateAnimatorHierarchy (BeltGunPatches) turns the hands-and-gun animator's
        // object off and on so the animator binds the new box's bones. Two things break in VR:
        //  - The binding is by path from the animator's object, and VR moved Weapon_root onto the rig (WeaponPatches), so every bone of the
        //    gun stops being animated. Weapon_root goes back to its place for the toggle, is bound there (bones and animation events), then
        //    goes back onto the rig.
        //  - The prefab's animator has keepAnimatorStateOnDisable off, so the toggle restarts every layer from its entry state with default
        //    parameters. After one of our own box moves (Manual Reload skips Tarkov's operation to its end) the animator's state is restored.

        internal sealed class AnimatorRebind
        {
            public WeaponPrefab prefab;
            public Animator animator;
            public Transform weaponRoot, rigParent;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public Vector3 localScale;
            public bool restoreState;
            public int[] stateHashes;
            public float[] stateTimes;
            public float[] layerWeights;
            public AnimatorControllerParameter[] parameters;
            public float[] parameterValues;
        }

        internal static AnimatorRebind BeforeAnimatorRebind(WeaponPrefab prefab)
        {
            try
            {
                FirearmController firearmController = VRGlobals.firearmController;
                if (prefab == null || firearmController == null || firearmController._weaponPrefab != prefab
                    || firearmController._player == null || !firearmController._player.IsYourPlayer)
                    return null;
                Animator animator = prefab._formerAnimator;
                if (animator == null)
                    return null;
                AnimatorRebind rebind = new AnimatorRebind { prefab = prefab, animator = animator };
                // Put Weapon_root back where the animator's paths expect it; RightHandPositioner/weaponHolder hang in its old place (WeaponPatches)
                Transform weaponRoot = firearmController.WeaponRoot;
                Transform holder = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform : null;
                if (weaponRoot != null && holder != null && weaponRoot.parent == holder && holder.parent != null && holder.parent.parent != null)
                {
                    rebind.weaponRoot = weaponRoot;
                    rebind.rigParent = holder;
                    rebind.localPosition = weaponRoot.localPosition;
                    rebind.localRotation = weaponRoot.localRotation;
                    rebind.localScale = weaponRoot.localScale;
                    weaponRoot.SetParent(holder.parent.parent, worldPositionStays: false);
                }
                // Our own box move (Manual Reload's belt guns): save the animator's current state to restore afterwards
                if (TryGetLmg(firearmController, out _) && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
                {
                    int layers = animator.layerCount;
                    rebind.restoreState = true;
                    rebind.stateHashes = new int[layers];
                    rebind.stateTimes = new float[layers];
                    rebind.layerWeights = new float[layers];
                    for (int layer = 0; layer < layers; layer++)
                    {
                        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
                        rebind.stateHashes[layer] = state.fullPathHash;
                        rebind.stateTimes[layer] = state.loop ? Mathf.Repeat(state.normalizedTime, 1f) : Mathf.Min(state.normalizedTime, 1f);
                        rebind.layerWeights[layer] = animator.GetLayerWeight(layer);
                    }
                    rebind.parameters = animator.parameters;
                    rebind.parameterValues = new float[rebind.parameters.Length];
                    for (int index = 0; index < rebind.parameters.Length; index++)
                    {
                        AnimatorControllerParameter parameter = rebind.parameters[index];
                        switch (parameter.type)
                        {
                            case AnimatorControllerParameterType.Float: rebind.parameterValues[index] = animator.GetFloat(parameter.nameHash); break;
                            case AnimatorControllerParameterType.Int: rebind.parameterValues[index] = animator.GetInteger(parameter.nameHash); break;
                            case AnimatorControllerParameterType.Bool: rebind.parameterValues[index] = animator.GetBool(parameter.nameHash) ? 1f : 0f; break;
                        }
                    }
                }
                return rebind;
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[VRBelt] before the animator rebind: {exception}");
                return null;
            }
        }

        internal static void AfterAnimatorRebind(AnimatorRebind rebind)
        {
            if (rebind == null)
                return;
            try
            {
                Animator animator = rebind.animator;
                if (rebind.weaponRoot != null)
                {
                    // Bind now, with Weapon_root where its paths expect it: the bones, and its animation events subscribed again. A bare
                    // Animator.Rebind recreates the state behaviours with nothing listening: no FireEnd / IdleStart, so the fire operation never
                    // ends. (These are WeaponPrefab.RebindAnimator's own steps; calling it would need the voice-chat assembly for its IPlayer parameter.)
                    if (animator != null && animator.isActiveAndEnabled && rebind.prefab != null && rebind.prefab._animator != null && VRGlobals.player != null)
                    {
                        AnimationEventSystem.AnimationEventsEmitter emitter = rebind.prefab._animationEventsEmitter;
                        if (emitter != null)
                            emitter.RemoveBindedAnimator();
                        rebind.prefab._animator.RebindBones();
                        if (emitter != null)
                            emitter.SetAnimator(rebind.prefab._animator, AnimationEventSystem.AnimationEventsEmitter.EEmitType.EmitOnDemand, VRGlobals.player.ProfileId);
                    }
                    rebind.weaponRoot.SetParent(rebind.rigParent, worldPositionStays: false);
                    rebind.weaponRoot.localPosition = rebind.localPosition;
                    rebind.weaponRoot.localRotation = rebind.localRotation;
                    rebind.weaponRoot.localScale = rebind.localScale;
                }
                if (rebind.restoreState && animator != null && animator.isActiveAndEnabled && animator.layerCount == rebind.stateHashes.Length)
                {
                    for (int index = 0; index < rebind.parameters.Length; index++)
                    {
                        AnimatorControllerParameter parameter = rebind.parameters[index];
                        switch (parameter.type)
                        {
                            case AnimatorControllerParameterType.Float: animator.SetFloat(parameter.nameHash, rebind.parameterValues[index]); break;
                            case AnimatorControllerParameterType.Int: animator.SetInteger(parameter.nameHash, (int)rebind.parameterValues[index]); break;
                            case AnimatorControllerParameterType.Bool: animator.SetBool(parameter.nameHash, rebind.parameterValues[index] > 0.5f); break;
                        }
                    }
                    for (int layer = 0; layer < rebind.stateHashes.Length; layer++)
                    {
                        animator.SetLayerWeight(layer, rebind.layerWeights[layer]);
                        animator.Play(rebind.stateHashes[layer], layer, rebind.stateTimes[layer]);
                    }
                }
                if (beltDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBelt] animator rebound for the box{(rebind.weaponRoot != null ? ", Weapon_root bound in its place" : "")}{(rebind.restoreState ? ", its state put back" : "")}");
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[VRBelt] after the animator rebind: {exception}");
            }
        }
    }
}
