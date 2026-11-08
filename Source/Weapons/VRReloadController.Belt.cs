using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Belt-fed machine guns by hand (Manual Reload), Tarkov's own reload done by the hands (his design, 2026-11-08): PKM / PKP / PKTM,
    // RPD / RPDN and M60E4 / E6 (gen_lmgs.py; the M60's clips key its cover as quaternion curves). The M60's LBT pouch has no belt data:
    // it goes in laid, as Tarkov has it.
    //  - A scope on the swinging mount (the PKP's, the RPDN's: Tarkov flips it aside first, pk_sight_open / rpdn_sight_open): its latch is
    //    taken with either hand's trigger and swung aside, its button pressed while held. The cover won't lift with it over the cover.
    //  - The feed cover: the trigger at its free end lifts it about its hinge (the clip's axis and travel), the hand riding it; let go past
    //    hingeOpenEnough it swings fully open, else shut. Open, the gun doesn't fire.
    //  - The box comes off (B, or the grip on it) only with the cover open; the belt goes with it. A box that isn't laid in the feed tray
    //    has its belt hanging out of it: the links past the ones in the box are a chain of points with gravity (BeltRope), drawn on Tarkov's
    //    own link bones (the rounds are on them).
    //  - The trigger at the belt's end takes it; it follows the hand (beltExtraOut more links come out of the box if the hand pulls further).
    //    Brought to where Tarkov's idle lays it, with the cover open, it goes in (eased into the animator's pose, which has it from there).
    //    With the cover open, a laid belt can be lifted back out the same way.
    //  - Shut on a laid belt, the gun fires again. A gun with a chamber (the PKs) then waits for its handle like any magazine gun
    //    (insertNeedsRack); one without (the RPDs) clicks until its handle has been all the way back once (waitsForCharge).
    // Who: the off hand while the gun hand has the grip, or the gun hand while the off hand holds the gun up (the deflector's split).
    // Not synced to co-op observers (they see Tarkov's poses); the inventory moves are.
    public static partial class VRReloadController
    {
        public static bool beltGunsByHand = true;
        public static float hingeGrabRadius = 0.05f;        // m any point of the hand to a cover's or latch's free end
        public static float hingeOpenEnough = 0.5f;         // share open past which a let go swings it fully open (else shut)
        public static float hingeSettleTime = 0.12f;        // s it takes to swing the rest of the way once let go
        public static float coverOpenToWork = 0.85f;        // share open the cover must be for the box and the belt
        public static float latchOpenToWork = 0.85f;        // share the latch must be swung aside for the cover to lift
        public static float beltGrabRadius = 0.05f;         // m any point of the hand to the belt (its hanging end, or its end in the tray)
        public static float beltLayRadius = 0.05f;          // m the held end must come to where Tarkov lays it
        public static float beltLayTime = 0.15f;            // s the belt eases into the animator's laid pose
        public static float beltTipEaseTime = 0.1f;         // s the end eases into the hand at the grab
        public static int beltExtraOut = 6;                 // links the hand can draw out of the box past the clip's own
        public static float beltDamping = 0.97f;            // share of each point's motion it keeps a step (lower = less swing)
        public static float beltMaxSpeed = 4f;              // m/s a belt point moves at most (a snap turn doesn't fling it)
        public static float beltMaxBendDegrees = 50f;       // deg two links meet at, at most
        public static int beltIterations = 10;
        public static float beltJumpDistance = 0.4f;        // m the box moves in a frame that's a jump (the rig recentred): the belt moves with it
        public static bool beltDebugLog = true;             // grabs, refusals, the belt's states (on for its first headset rounds)

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The data (ReloadHolds.json "lmgs")

        private sealed class HingeData
        {
            public string bone;
            public Vector3 axis;            // in the bone's closed frame
            public float angle;
            public string button;           // the latch's, pressed while it's held
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
        }

        private sealed class LmgData
        {
            public HingeData cover;
            public HingeData latch;
            public BeltData belt;
        }

        private static readonly Dictionary<string, LmgData> lmgGuns = new Dictionary<string, LmgData>();

        private static LmgData LmgFrom(SavedLmg saved)
        {
            if (saved == null)
                return null;
            LmgData data = new LmgData { cover = HingeFrom(saved.cover), latch = HingeFrom(saved.latch) };
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

        // A box of the model the belt data was read on (by its item template: what its pooled model is called isn't ours to rely on)
        private static bool BeltFits(Magazine box, BeltData belt)
        {
            return box != null && belt != null && (belt.boxIds == null || belt.boxIds.Contains(box.TemplateId.ToString()));
        }

        // Our belt gun in our hands, Manual Reload on
        private static bool TryGetLmg(FirearmController firearmController, out LmgData data)
        {
            data = null;
            if (!enabled || !beltGunsByHand || firearmController == null || firearmController.Item == null || !firearmController.Item.IsBeltMachineGun
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
            public float coverSettle = -1f; // let go: where it swings to (-1 = nowhere)
            public float latchSettle = -1f;
            public bool beltOut;            // the box on it has its belt out of the feed tray
            public bool waitsForCharge;     // a box went in by hand on a gun without a chamber: the handle first
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

        // No firing: the cover open (or being worked), the belt out of the tray, either in a hand
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

        // BoltComesBack: the handle all the way back
        private static void BeltGunCharged(FirearmController firearmController)
        {
            if (firearmController == null || firearmController.Item == null || !lmgStates.TryGetValue(firearmController.Item, out LmgState state) || !state.waitsForCharge)
                return;
            state.waitsForCharge = false;
            if (beltDebugLog)
                Plugin.MyLog.LogInfo($"[VRBelt] {firearmController.Item.LocalizedShortName()}: charged");
        }

        // The box comes off only with the cover open (GrabFromGun, TryReleaseMagazine): true = it stays, the hand told so
        private static bool BeltBoxStaysOn(FirearmController firearmController, SoundHand hand)
        {
            if (!TryGetLmg(firearmController, out _) || CoverOpenEnough(LmgStateOf(firearmController.Item)))
                return false;
            Refuse(hand);
            if (beltDebugLog)
                Plugin.MyLog.LogInfo("[VRBelt] the box stays on: the cover is shut");
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
            gunRope.simulating = false;
        }

        // A box seated by hand (TryInsert): its belt hangs out of it until the hand lays it; no chamber, the handle first
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
            state.waitsForCharge = (weapon.Chambers == null || weapon.Chambers.Length == 0) && chargingHandle && TryGetChargingHandle(firearmController, weapon);
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
            public Transform grab;          // ours, at that end
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
        private static Vector3 hingeAcrossAtGrab;           // the controller about the hinge at the grab, in the bone's parent frame
        private static Transform hingeRider;                // ours, on the part where the hand took it
        private static float latchButtonPressed;
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
            // The closed pose, by the bone: read the first time it's seen, before we've turned it (the gun's model comes back from Tarkov's
            // pool turned the way we left it)
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

        // Its meshes' far side from the hinge line, in its own closed frame (DeflectorFreeEnd's rule, with this hinge's axis)
        private static Vector3 HingeFreeEnd(Transform bone, Vector3 axis, Quaternion open)
        {
            Quaternion toClosed = Quaternion.Inverse(open);
            Vector3 centerSum = Vector3.zero;
            int corners = 0;
            float furthest = 0f;
            foreach (MeshFilter meshFilter in bone.GetComponentsInChildren<MeshFilter>(false))
            {
                if (meshFilter.sharedMesh == null)
                    continue;
                Bounds bounds = meshFilter.sharedMesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = bounds.center + Vector3.Scale(bounds.extents, new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
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

        // The mount is on this gun and drawn (the PKM's rig has none; a rig's mount can be hidden)
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

        // A scope on the mount: it has to be swung aside before the cover lifts (Tarkov's _sight clips play only then)
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

        // The controller's angle about the hinge since the grab (in the bone's parent frame, so turning the gun doesn't count)
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
            // The hand rides the part from where it took it
            if (hingeRider == null)
                hingeRider = new GameObject("VRHingeHand").transform;
            hingeRider.SetParent(rig.bone, worldPositionStays: false);
            hingeRider.SetPositionAndRotation(controller.position, controller.rotation);
            LatchHand(hingeRider, gunHand);
            SetLatchPivot(rig.grab.position);
            if (kind == HingeKind.Latch)
                PlayFirstGunSound(firearmController, hand, "SightButtonIn");
            Pulse(hand, 0.3f, 0.03f);
            if (beltDebugLog)
                Plugin.MyLog.LogInfo($"[VRBelt] took the {kind} ({(gunHand ? "gun" : "off")} hand), {hingeOpenAtGrab:0.00} open");
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
            // Here too (Update), so the latched hand reads it this frame; again after the animator (ApplyBeltGunBeforeIk)
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

        // Sounds and clicks at its ends: open as it leaves shut, shut as it gets there (a cover shut on a laid belt: the belt into the feed)
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
            public int pinned;                      // the last point held to the box (its baked pose); drawn out, it goes down
            public bool simulating;
            public Vector3 lastBoxPosition;
            public Vector3[] laidPositions;         // the animator's pose this frame (the gun's box: the belt laid)
            public Quaternion[] laidRotations;
            public Vector3 laidEndLocal;            // the laid belt's end, in the box's frame (read in Update)
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
        private static Vector3 beltEndInController;         // where the hand holds the end, the controller's frame
        private static Vector3 beltEndAtGrab;
        private static float beltEndEase;
        private static Vector3 beltEndTarget;               // this frame's, world (inside the belt's reach)

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
            float scale = Mathf.Abs(box.lossyScale.x);
            for (int link = 0; link < count - 1; link++)
                rope.lengths[link] = Mathf.Max((data.positions[link + 1] - data.positions[link]).magnitude, data.spacing * 0.5f) * scale;
            rope.lengths[count - 1] = data.spacing * scale;
            rope.simulating = false;
            return true;
        }

        // Its links back as they were (a box of Tarkov's pool, on the gun or in the hand)
        private static void ReleaseRope(BeltRope rope)
        {
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

        private static bool PointFixed(BeltRope rope, int point, bool endHeld) => point <= rope.pinned || (endHeld && point == rope.data.count - 1);

        // Gravity and the last step's motion on the free points, then the links' lengths and the belt's bend held (a few rounds)
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
            Vector3 fall = Physics.gravity * (deltaTime * deltaTime);
            float maxStep = beltMaxSpeed * deltaTime;
            for (int point = rope.pinned + 1; point <= count; point++)
            {
                Vector3 motion = Vector3.ClampMagnitude((points[point] - previous[point]) * beltDamping, maxStep);
                previous[point] = points[point];
                points[point] += motion + fall;
            }
            int end = count - 1;
            // Bent by b at the middle point, two links of length L reach 2 L cos(b / 2) apart
            float minFold = 2f * Mathf.Cos(0.5f * Mathf.Deg2Rad * Mathf.Clamp(beltMaxBendDegrees, 0f, 170f));
            for (int iteration = 0; iteration < beltIterations; iteration++)
            {
                if (endHeld)
                    points[end] = endTarget;
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
                // Two links apart no nearer than the sharpest bend allows (a belt folds, it doesn't kink)
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
        }

        // The links onto the points: the ones in the box as the clip has them, the rest turned onto their own stretch from the link before
        // (no twist of their own). layBlend toward the animator's laid pose.
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
                Vector3 position = points[link];
                if (layBlend > 0f)
                {
                    position = Vector3.Lerp(position, rope.laidPositions[link], layBlend);
                    rotation = Quaternion.Slerp(rotation, rope.laidRotations[link], layBlend);
                }
                rope.links[link].SetPositionAndRotation(position, rotation);
            }
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
                // Out of the tray: the chain starts from the animator's laid pose (ApplyBeltGunBeforeIk)
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
            if (beltHandHolder == null)
                beltHandHolder = new GameObject("VRBeltHand").transform;
            beltHandHolder.SetPositionAndRotation(controller.position, controller.rotation);
            LatchHand(beltHandHolder, gunHand);
            Pulse(hand, 0.3f, 0.03f);
            if (beltDebugLog)
                Plugin.MyLog.LogInfo($"[VRBelt] took the belt {(fromTray ? "out of the tray" : "by its end")} ({(gunHand ? "gun" : "off")} hand)");
        }

        // From Tick: the end follows the hand within the belt's reach (a link more out of the box when pulled), the hand stopping where the
        // belt does; at the tray with the cover open it goes in
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
            beltEndEase = Mathf.MoveTowards(beltEndEase, 1f, Time.deltaTime / Mathf.Max(beltTipEaseTime, 0.001f));
            Vector3 eased = Vector3.Lerp(beltEndAtGrab, wanted, Mathf.SmoothStep(0f, 1f, beltEndEase));
            Vector3 target = eased;
            if (gunRope.simulating)
            {
                Vector3 mouth = gunRope.points[gunRope.pinned];
                float reach = FreeLength(gunRope) * 0.98f;
                // Pulled further: a link more comes out of the box, up to beltExtraOut past the clip's
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
            // The hand stays on the controller, but no further than the belt reaches
            beltHandHolder.SetPositionAndRotation(controller.position + (target - eased), controller.rotation);
            if (CoverOpenEnough(state) && (target - gunRope.box.TransformPoint(gunRope.laidEndLocal)).magnitude <= beltLayRadius)
            {
                BeginLay(firearmController);
                return;
            }
            if (beltInGunHand ? gunTriggerReleased : offTriggerReleased)
                LetGoOfBelt();
        }

        private static void BeginLay(FirearmController firearmController)
        {
            SoundHand hand = beltInGunHand ? SoundHand.GunHand : SoundHand.OffHand;
            HoldingBelt = false;
            beltHeldFor = null;
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
            ReleaseHandLatch();
            if (beltDebugLog)
                Plugin.MyLog.LogInfo("[VRBelt] let go of the belt");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // From Tick

        // The hands at the latch, the cover or the belt (the trigger takes them): true = the trigger was ours this tick
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
            bool showZones = debugZones && !gunHand;
            if (EnsureHinge(firearmController, data, HingeKind.Cover, out HingeRig cover))
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
            if (LatchPresent(firearmController, data, out HingeRig latch))
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
            bool boxOn = weapon.GetCurrentMagazine() != null && gunRope.links != null && gunRope.box == GunBoxView(firearmController);
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
            else if (showZones)
                SetZoneViz(ref beltZoneViz, false, Vector3.zero, 0f, Color.clear);
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

        // Every tick: the let-go swing of the cover and latch, the latch's button, Tarkov's own reload taking it all over
        private static void TickBeltGun(bool triggerReleased, bool gunHandTriggerReleased)
        {
            UpdateHinge(triggerReleased, gunHandTriggerReleased);
            UpdateBeltHold(triggerReleased, gunHandTriggerReleased);
            latchButtonPressed = Mathf.MoveTowards(latchButtonPressed, HoldingHinge && heldHinge == HingeKind.Latch ? 1f : 0f, Time.deltaTime / 0.08f);
            FirearmController firearmController = VRGlobals.firearmController;
            if (!TryGetLmg(firearmController, out _))
            {
                beltLaying = false;
                if (!debugZones)
                    return;
                SetZoneViz(ref coverZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref latchZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref beltZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref trayZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }
            LmgState state = LmgStateOf(firearmController.Item);
            // Tarkov's own reload (its clips open, swap, lay, shut): it's all as it leaves it
            if (firearmController.IsInReloadOperation() && expectedOperation == ExpectedOperation.None)
            {
                LetGoOfHinge();
                LetGoOfBelt();
                state.cover = state.latch = 0f;
                state.coverSettle = state.latchSettle = -1f;
                state.beltOut = state.waitsForCharge = false;
                beltLaying = false;
                gunRope.simulating = false;
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
        // Player.LateUpdate prefix (ShotgunPatches): after the animator (which keys the cover, the latch and every belt link each frame), before
        // Tarkov's IK reads where the hands go - a hand latched to the cover rides it

        public static void ApplyBeltGunBeforeIk()
        {
            try
            {
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
                // The animator's pose this frame = the belt laid (Tarkov's idle keys it): the tray, and where a lay eases to
                BeltRope rope = gunRope;
                int count = data.belt.count;
                for (int link = 0; link < count; link++)
                {
                    rope.laidPositions[link] = rope.links[link].position;
                    rope.laidRotations[link] = rope.links[link].rotation;
                }
                rope.laidEndLocal = box.InverseTransformPoint(rope.laidPositions[count - 1]);
                if (!state.beltOut && !beltLaying)
                {
                    rope.simulating = false;
                    return;
                }
                if (!rope.simulating)
                {
                    StartRope(rope, beltLiftFromTray ? rope.laidPositions : FreshStash(rope));
                    // The stash / laid pose has no far end of its own (laidPositions): from the last link's turn
                    if (beltLiftFromTray)
                        rope.points[count] = rope.previous[count] = EndOfLink(rope, rope.points[count - 1], rope.laidRotations[count - 1]);
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
                if (beltLaying && beltLayProgress >= 1f)
                {
                    // In: the animator has it from here
                    beltLaying = false;
                    state.beltOut = false;
                    rope.simulating = false;
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

        // The hands' fingers on these parts: the magazine pull's pinch (the left's; mirrored for the right arm)
        private static readonly Vector3[] mirroredBeltPinch = new Vector3[16];
        private static Vector3[] BeltGunFingers(bool rightArm)
        {
            return rightArm ? MirroredPose(MAGAZINE_PULL_FINGERS, rightToLeft: false, mirroredBeltPinch) ?? GUN_HAND_HANDLE_FINGERS : MAGAZINE_PULL_FINGERS;
        }
    }
}
