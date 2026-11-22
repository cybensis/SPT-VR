using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Weapons
{
    // The MDR's brass deflector by hand: the cover over the ejection port on the right of the receiver. Tarkov's own MDR clips for a
    // failure to eject or feed (mdr_jam_shell / mdr_jam_feed) flip it up on a hinge at one end (a pure turn of about 60 deg about the
    // bone's own -z, with its origin on the hinge), then lift it off. Here it only swings on that hinge:
    //  - During one of those two malfunctions it has to be open. Closed, the stuck case can't be reached and a rack doesn't clear it.
    //  - While open, the gun doesn't fire (DeflectorBlocksFiring).
    //  - Either hand's trigger takes it where Tarkov's hand does (the clip's index tip at the grab) or at its free end. Only the hand that
    //    may take the stuck case can: the off hand while the gun hand holds the grip, or the gun hand while the off hand holds the gun up.
    //    It can be taken only during one of those malfunctions, or while open (to shut it). It stays where it's let go.
    //  - It opens along Tarkov's clip: mdr_jam_shell t 2.93 -> 3.20, where the hand pushes it out and forward ~7 cm with ~35 deg of roll
    //    while it flips 0 -> 62 deg (the lift-off starts at 3.23 and is never reached). DEFLECTOR_OPEN_PATH holds the baked hinge angle and
    //    right palm pose in the gun's frame per frame. Progress along it comes from the controller's push along the path plus its roll about
    //    the path's roll axis since the grab. The hinge and the right hand (with the clip's fingers) follow the clip; the left hand stays
    //    where it took hold, with the clip's fingers mirrored.
    // Both MDR rigs (5.56 and 7.62) have the same weapon_deflector bone.
    public static partial class VRReloadController
    {
        private static FirearmController deflectorFor;
        private static Transform deflectorBone;

        public static bool HoldingDeflector { get; private set; }

        // True when the gun has a deflector and it's more than barely open
        private static bool DeflectorOpen(FirearmController firearmController)
        {
            return firearmController != null && firearmController.Item != null && Deflector.deflectorAngles.TryGetValue(firearmController.Item, out float angle) && angle > 0.5f;
        }

        // A failure to eject or feed with the deflector not open far enough: the case can't be reached and a rack doesn't clear it
        private static bool DeflectorBlocksClearing(FirearmController firearmController)
        {
            if (!Deflector.deflectorByHand || !Deflector.EnsureDeflector(firearmController))
                return false;
            Weapon.EMalfunctionState malfunction = firearmController.Item.MalfState.State;
            if (malfunction != Weapon.EMalfunctionState.Jam && malfunction != Weapon.EMalfunctionState.Feed)
                return false;
            Deflector.deflectorAngles.TryGetValue(firearmController.Item, out float angle);
            return angle < Deflector.deflectorOpenDegrees * Deflector.deflectorOpenEnough;
        }

        public static class Deflector
        {
            public static bool deflectorByHand = true;
            public static float deflectorOpenDegrees = 61.7f;       // deg, the hinge's travel (the clip's flip, up to before the lift-off)
            public static float deflectorOpenEnough = 0.8f;         // fraction of that it must be open to reach the stuck case and rack
            public static float deflectorGrabRadius = 0.05f;        // m from any point of the hand to the grab point
            public static Vector3 deflectorHingeAxis = new Vector3(0f, 0f, -1f);  // in the bone's closed frame (the clip's); flip it if it opens into the gun
            public static float deflectorPushTravel = 0.069f;       // m of push along the clip's path to open it fully (the clip's own hand moves 6.9 cm)
            public static float deflectorPushShare = 0.7f;          // fraction of the opening that comes from the push; the roll gives the rest

            // mdr_jam_shell t 2.933 -> 3.200 (30 fps): hinge degrees, then the right palm's position (m) and rotation in the gun's (weapon node's) frame
            private static readonly float[] DEFLECTOR_OPEN_PATH =
            {
                0.0f, 0.0461f, -0.0764f, -0.1530f, -0.62261f, -0.63796f, 0.39778f, -0.21712f,
                2.6f, 0.0452f, -0.0761f, -0.1525f, -0.61847f, -0.63876f, 0.40892f, -0.20559f,
                9.6f, 0.0491f, -0.0787f, -0.1519f, -0.62218f, -0.62811f, 0.43285f, -0.17613f,
                19.5f, 0.0584f, -0.0862f, -0.1512f, -0.62753f, -0.60839f, 0.46600f, -0.13754f,
                30.8f, 0.0694f, -0.0961f, -0.1505f, -0.63158f, -0.58245f, 0.50336f, -0.09214f,
                42.2f, 0.0787f, -0.1062f, -0.1498f, -0.63296f, -0.55203f, 0.54095f, -0.04472f,
                52.0f, 0.0851f, -0.1220f, -0.1492f, -0.63130f, -0.52024f, 0.57516f, 0.00038f,
                59.0f, 0.0877f, -0.1329f, -0.1487f, -0.62762f, -0.49066f, 0.60321f, 0.03852f,
                61.7f, 0.0870f, -0.1327f, -0.1506f, -0.62584f, -0.48208f, 0.61107f, 0.05016f,
            };
            private const int DEFLECTOR_PATH_STRIDE = 8;
            // The right hand's finger rotations mid-flip (t 3.07), in pose order
            private static readonly Vector3[] DEFLECTOR_FINGERS =
            {
                new Vector3(75.4f, 204.6f, 194.1f),   // palm
                new Vector3(290.3f, 216.0f, 118.8f), new Vector3(4.5f, 356.3f, 11.2f), new Vector3(5.8f, 358.4f, 355.4f),     // thumb
                new Vector3(358.3f, 0.1f, 12.4f), new Vector3(0.0f, 0.0f, 59.3f), new Vector3(0.0f, 0.0f, 53.7f),           // index
                new Vector3(4.4f, 358.6f, 20.2f), new Vector3(0.0f, 360.0f, 48.9f), new Vector3(0.0f, 0.0f, 49.7f),         // middle
                new Vector3(9.4f, 0.8f, 19.2f), new Vector3(0.1f, 0.4f, 27.2f), new Vector3(1.1f, 358.7f, 24.1f),           // ring
                new Vector3(24.3f, 8.3f, 20.5f), new Vector3(0.2f, 1.5f, 20.0f), new Vector3(1.0f, 0.1f, 27.1f),            // pinky
            };
            private static readonly Vector3[] mirroredDeflectorFingers = new Vector3[16];
            // Where the clip's index fingertip touches it at the grab, in the gun's frame
            private static readonly Vector3 DEFLECTOR_TOUCH_IN_GUN = new Vector3(0.0159f, -0.1442f, 0.0048f);

            private const string DEFLECTOR_BONE = "weapon_deflector";
            internal static readonly Dictionary<Weapon, float> deflectorAngles = new Dictionary<Weapon, float>();    // per gun, degrees open; kept across a weapon swap
            private static readonly Dictionary<Transform, Quaternion> deflectorClosedPoses = new Dictionary<Transform, Quaternion>();
            private static Quaternion deflectorClosedLocalRotation;
            private static Vector3 deflectorFreeEndLocal;           // its free end, in its own frame
            private static Transform deflectorGrab;                 // our transform on that end, which the hand latches onto
            public static bool DeflectorInGunHand => HoldingDeflector && deflectorInGunHand;
            internal static bool deflectorInGunHand;
            internal static bool deflectorOnRightArm;
            private static FirearmController deflectorHeldFor;
            private static float deflectorProgressAtGrab;           // 0 shut .. 1 open along the clip's path, when it was taken
            private static Vector3 controllerAtDeflectorGrabInGun;
            private static Quaternion controllerRotationAtDeflectorGrabInGun;
            // The MDR's own sounds. CoverOpen = mdr_jam_cover_take (the latch letting go), played as it leaves shut. CoverClose =
            // mdr_jam_cover_putinplace (~1 s of swing and click), played as it starts back.
            [Tunable("Malfunctions", TunableKind.Tune, "MDR deflector: how far it swings back from the most it was open before its closing sound plays.", Unit = "°")]
            public static float deflectorCloseSoundTurn = 4f;      // deg back from its widest opening that starts the close sound
            private static bool deflectorOpenSounded, deflectorCloseSounded;
            private static float deflectorPeakAngle, deflectorCloseSoundAngle;

            // The gun doesn't fire with it open
            public static bool DeflectorBlocksFiring => deflectorByHand && DeflectorOpen(VRGlobals.firearmController);

            private static bool HasDeflector(FirearmController firearmController)
            {
                return firearmController != null && firearmController.Item != null && AnimationFamily(firearmController).StartsWith("mdr")
                    && FindRigBone(firearmController, DEFLECTOR_BONE) != null;
            }

            // Finds the bone, its closed pose and its free end, read while it's closed (with nothing of ours on it)
            internal static bool EnsureDeflector(FirearmController firearmController)
            {
                if (firearmController == null || firearmController.Item == null)
                    return false;
                if (deflectorFor == firearmController && deflectorBone != null)
                    return true;
                deflectorFor = firearmController;
                deflectorBone = HasDeflector(firearmController) ? FindRigBone(firearmController, DEFLECTOR_BONE) : null;
                if (deflectorBone == null)
                    return false;
                deflectorAngles.TryGetValue(firearmController.Item, out float angle);
                // The closed pose is stored per bone the first time it's seen, before we turn it: a gun model can come back from Tarkov's
                // pool still turned the way we left it
                if (!deflectorClosedPoses.TryGetValue(deflectorBone, out deflectorClosedLocalRotation))
                {
                    deflectorClosedLocalRotation = deflectorBone.localRotation;
                    deflectorClosedPoses[deflectorBone] = deflectorClosedLocalRotation;
                }
                deflectorFreeEndLocal = HingeFreeEnd(deflectorBone, deflectorHingeAxis.normalized, Quaternion.Inverse(deflectorClosedLocalRotation) * deflectorBone.localRotation, includeHiddenMeshes: true);
                if (deflectorGrab == null)
                    deflectorGrab = new GameObject("VRDeflectorGrab").transform;
                deflectorGrab.SetParent(deflectorBone, worldPositionStays: false);
                deflectorGrab.localPosition = deflectorFreeEndLocal;
                deflectorGrab.localRotation = Quaternion.identity;
                if (debugLog)
                    LogAction($"[VRReload] deflector found on {firearmController.Item.LocalizedShortName()}, free end {(deflectorFreeEndLocal * 100f).ToString("F1")} cm, open {angle:0} deg");
                return true;
            }

            private static void SetDeflectorAngle(FirearmController firearmController, float angle)
            {
                deflectorAngles[firearmController.Item] = angle;
                PoseDeflector(firearmController);
            }

            // Turns the bone open about the hinge. Called in Update, so the latched hand reads it this frame, and again after the animator.
            private static void PoseDeflector(FirearmController firearmController)
            {
                if (deflectorBone == null || deflectorFor != firearmController || firearmController.Item == null)
                    return;
                deflectorAngles.TryGetValue(firearmController.Item, out float angle);
                deflectorBone.localRotation = deflectorClosedLocalRotation * Quaternion.AngleAxis(angle, deflectorHingeAxis);
            }

            // Called from HandsInteractionController.LateUpdate, after the animator (its jam clips write the bone)
            public static void ApplyDeflector()
            {
                FirearmController firearmController = VRGlobals.firearmController;
                if (!deflectorByHand || firearmController == null || firearmController.Item == null || deflectorFor != firearmController || deflectorBone == null)
                    return;
                if (HoldingDeflector || DeflectorOpen(firearmController))
                    PoseDeflector(firearmController);
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------

            // True when this hand is at the deflector and may take it now
            internal static bool InDeflectorZone(FirearmController firearmController, bool gunHand)
            {
                if (!deflectorByHand || HoldingDeflector || !EnsureDeflector(firearmController))
                    return false;
                bool handMayTakeIt = gunHand ? ChestHang.SupportOnly : !VRGlobals.vrPlayer.isSupporting && !ChestHang.Detached;
                Weapon.EMalfunctionState malfunction = firearmController.Item.MalfState.State;
                bool worthTaking = DeflectorOpen(firearmController)
                    || ((malfunction == Weapon.EMalfunctionState.Jam || malfunction == Weapon.EMalfunctionState.Feed) && MalfunctionClearsByHand(firearmController));
                if (!handMayTakeIt || !worthTaking)
                    return false;
                // Where Tarkov's hand takes it when shut, or its free end wherever it is now
                Vector3 touch = DeflectorGunFrame(out Vector3 gunPosition, out Quaternion gunRotation) ? gunPosition + gunRotation * DEFLECTOR_TOUCH_IN_GUN : deflectorGrab.position;
                Vector3 freeEnd = deflectorGrab.position;
                bool atIt = HandTouchesCapsule(gunHand, touch, touch, deflectorGrabRadius) || HandTouchesCapsule(gunHand, freeEnd, freeEnd, deflectorGrabRadius);
                SetZoneViz(debugZones, touch, deflectorGrabRadius, atIt ? new Color(0f, 1f, 0f, 0.3f) : new Color(0f, 0.6f, 1f, 0.3f));
                return atIt;
            }

            // The gun frame the clip's path is stored in: the weapon node (the deflector's parent), unscaled
            private static bool DeflectorGunFrame(out Vector3 position, out Quaternion rotation)
            {
                Transform gun = deflectorBone != null ? deflectorBone.parent : null;
                position = gun != null ? gun.position : Vector3.zero;
                rotation = gun != null ? gun.rotation : Quaternion.identity;
                return gun != null;
            }

            private static int DeflectorPathFrames => DEFLECTOR_OPEN_PATH.Length / DEFLECTOR_PATH_STRIDE;

            // At a point along the clip's path (0 shut .. 1 open): the hinge angle and the right palm's pose in the gun's frame,
            // interpolated between the path's frames
            private static void DeflectorPathAt(float progress, out float angle, out Vector3 palmPosition, out Quaternion palmRotation)
            {
                float frame = Mathf.Clamp01(progress) * (DeflectorPathFrames - 1);
                int first = Mathf.Min((int)frame, DeflectorPathFrames - 2);
                float blend = frame - first;
                int a = first * DEFLECTOR_PATH_STRIDE, b = a + DEFLECTOR_PATH_STRIDE;
                float[] path = DEFLECTOR_OPEN_PATH;
                angle = Mathf.Lerp(path[a], path[b], blend);
                palmPosition = Vector3.Lerp(new Vector3(path[a + 1], path[a + 2], path[a + 3]), new Vector3(path[b + 1], path[b + 2], path[b + 3]), blend);
                palmRotation = Quaternion.Slerp(new Quaternion(path[a + 4], path[a + 5], path[a + 6], path[a + 7]),
                    new Quaternion(path[b + 4], path[b + 5], path[b + 6], path[b + 7]), blend);
            }

            // The inverse: how far along the path an angle is (the angle only rises along the path)
            private static float DeflectorProgressOf(float angle)
            {
                float[] path = DEFLECTOR_OPEN_PATH;
                for (int frame = 1; frame < DeflectorPathFrames; frame++)
                {
                    float from = path[(frame - 1) * DEFLECTOR_PATH_STRIDE], to = path[frame * DEFLECTOR_PATH_STRIDE];
                    if (angle <= to)
                        return (frame - 1 + (to > from ? Mathf.Clamp01((angle - from) / (to - from)) : 0f)) / (DeflectorPathFrames - 1);
                }
                return 1f;
            }

            internal static void GrabDeflector(FirearmController firearmController, bool gunHand)
            {
                HoldingDeflector = true;
                deflectorInGunHand = gunHand;
                deflectorOnRightArm = OnRightArm(gunHand);
                deflectorHeldFor = firearmController;
                deflectorAngles.TryGetValue(firearmController.Item, out float angle);
                deflectorProgressAtGrab = DeflectorProgressOf(angle);
                deflectorOpenSounded = angle > 2f;
                deflectorCloseSounded = false;
                deflectorPeakAngle = angle;
                Transform controller = HandRig(gunHand);
                DeflectorGunFrame(out Vector3 gunPosition, out Quaternion gunRotation);
                Quaternion toGun = Quaternion.Inverse(gunRotation);
                controllerAtDeflectorGrabInGun = toGun * (controller.position - gunPosition);
                controllerRotationAtDeflectorGrabInGun = toGun * controller.rotation;
                // The right hand goes to the clip's pose (eased there, then following the clip), latched to the gun's body.
                // The left hand stays where it took hold.
                if (deflectorOnRightArm)
                {
                    LatchHand(deflectorBone.parent, gunHand);
                    PlaceDeflectorHand(deflectorProgressAtGrab);
                }
                else
                {
                    LatchHand(deflectorGrab, gunHand);
                    SetLatchPivot(deflectorGrab.position);
                }
                if (debugLog)
                    LogAction($"[VRReload] took the deflector ({(gunHand ? "gun" : "off")} hand), open {angle:0} deg ({deflectorProgressAtGrab:0.00} along the clip)");
                Pulse(gunHand ? SoundHand.GunHand : SoundHand.OffHand, 0.3f, 0.03f);
            }

            // The clip's finger pose on it: as is for the right hand, mirrored for the left
            internal static Vector3[] DeflectorFingers(bool rightArm)
            {
                return rightArm ? DEFLECTOR_FINGERS : MirroredPose(DEFLECTOR_FINGERS, rightToLeft: true, mirroredDeflectorFingers) ?? MAGAZINE_PULL_FINGERS;
            }

            // Puts the right hand where the clip has it at that point of the opening (the latch eases there: SnapHandTo)
            private static void PlaceDeflectorHand(float progress)
            {
                DeflectorPathAt(progress, out _, out Vector3 palmPosition, out Quaternion palmRotation);
                DeflectorGunFrame(out Vector3 gunPosition, out Quaternion gunRotation);
                SnapHandTo(deflectorBone.parent, gunPosition + gunRotation * palmPosition, gunRotation * palmRotation);
                SetLatchPivot(deflectorGrab.position);
            }

            // How far along the clip's path the controller has gone since the grab. It mixes the push along the path's travel and the roll about
            // the path's roll axis (both in the gun's frame, so turning the gun doesn't count), weighted deflectorPushShare : the rest.
            private static float DeflectorProgressFromHand()
            {
                DeflectorPathAt(0f, out _, out Vector3 startPosition, out Quaternion startRotation);
                DeflectorPathAt(1f, out _, out Vector3 endPosition, out Quaternion endRotation);
                Transform controller = LatchedHandRig;
                DeflectorGunFrame(out Vector3 gunPosition, out Quaternion gunRotation);
                Quaternion toGun = Quaternion.Inverse(gunRotation);
                Vector3 moved = toGun * (controller.position - gunPosition) - controllerAtDeflectorGrabInGun;
                Vector3 pathTravel = endPosition - startPosition;
                float push = pathTravel.sqrMagnitude > 1e-8f && deflectorPushTravel > 0f ? Vector3.Dot(moved, pathTravel.normalized) / deflectorPushTravel : 0f;

                // The roll: the path's turn from start to end as an angle about an axis, then the controller's turn since the grab about that axis
                (endRotation * Quaternion.Inverse(startRotation)).ToAngleAxis(out float pathTurn, out Vector3 rollAxis);
                if (pathTurn > 180f)
                {
                    pathTurn = 360f - pathTurn;
                    rollAxis = -rollAxis;
                }
                Quaternion turned = toGun * controller.rotation * Quaternion.Inverse(controllerRotationAtDeflectorGrabInGun);
                Vector3 turnedVector = new Vector3(turned.x, turned.y, turned.z);
                float roll = 2f * Mathf.Atan2(Vector3.Dot(turnedVector, rollAxis), turned.w) * Mathf.Rad2Deg;
                if (roll > 180f) roll -= 360f;
                else if (roll < -180f) roll += 360f;
                float twist = pathTurn > 1f ? roll / pathTurn : 0f;
                return Mathf.Clamp01(deflectorProgressAtGrab + deflectorPushShare * push + (1f - deflectorPushShare) * twist);
            }

            // Called from Tick: the opening follows the hand along the clip between shut and fully open, with a click at either end
            internal static void UpdateDeflector(bool offTriggerReleased, bool gunTriggerReleased)
            {
                if (!HoldingDeflector)
                    return;
                FirearmController firearmController = deflectorHeldFor;
                bool valid = enabled && deflectorByHand && VRGlobals.inGame && !VRGlobals.menuOpen && VRGlobals.vrPlayer != null
                    && firearmController != null && firearmController == VRGlobals.firearmController && deflectorFor == firearmController && deflectorBone != null
                    && (deflectorInGunHand ? ChestHang.SupportOnly : !ChestHang.Detached) && deflectorOnRightArm == OnRightArm(deflectorInGunHand);
                if (!valid)
                {
                    LetGoOfDeflector();
                    return;
                }
                deflectorAngles.TryGetValue(firearmController.Item, out float before);
                float progress = DeflectorProgressFromHand();
                DeflectorPathAt(progress, out float angle, out _, out _);
                angle = Mathf.Min(angle, deflectorOpenDegrees);
                SetDeflectorAngle(firearmController, angle);
                if (deflectorOnRightArm)
                    PlaceDeflectorHand(progress);
                SoundHand hand = deflectorInGunHand ? SoundHand.GunHand : SoundHand.OffHand;
                if (!deflectorOpenSounded && angle > 2f)
                {
                    deflectorOpenSounded = true;
                    deflectorCloseSounded = false;
                    deflectorPeakAngle = angle;
                    PlayFirstGunSound(firearmController, hand, "CoverOpen");
                }
                deflectorPeakAngle = Mathf.Max(deflectorPeakAngle, angle);
                if (deflectorOpenSounded && !deflectorCloseSounded && deflectorPeakAngle >= 15f && angle <= deflectorPeakAngle - deflectorCloseSoundTurn)
                {
                    deflectorCloseSounded = true;
                    deflectorCloseSoundAngle = angle;
                    PlayFirstGunSound(firearmController, hand, "CoverClose");
                }
                // Opened again well past where it started closing: the next close plays its sound again
                if (deflectorCloseSounded && angle > deflectorCloseSoundAngle + 6f)
                {
                    deflectorCloseSounded = false;
                    deflectorPeakAngle = angle;
                }
                if (angle <= 0.5f)
                    deflectorOpenSounded = false;
                if (before < deflectorOpenDegrees - 0.5f && angle >= deflectorOpenDegrees - 0.5f)
                    Pulse(hand, 0.35f, 0.03f);
                else if (before > 0.5f && angle <= 0.5f)
                {
                    // Shut: a firmer click through the gun
                    Pulse(hand.Seating, 0.45f, 0.04f);
                    if (debugLog)
                        LogAction("[VRReload] deflector shut");
                }
                if (deflectorInGunHand ? gunTriggerReleased : offTriggerReleased)
                    LetGoOfDeflector();
            }

            private static void LetGoOfDeflector()
            {
                HoldingDeflector = false;
                FirearmController firearmController = deflectorHeldFor;
                deflectorHeldFor = null;
                ReleaseHandLatch();
                // Nearly shut snaps shut
                if (firearmController != null && firearmController.Item != null && deflectorAngles.TryGetValue(firearmController.Item, out float angle) && angle > 0f && angle <= 0.5f)
                    SetDeflectorAngle(firearmController, 0f);
            }
        }
    }
}
