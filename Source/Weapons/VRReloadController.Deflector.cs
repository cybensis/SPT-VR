using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The MDR's brass deflector by hand: the cover on the right of the receiver over its ejection port. Tarkov's own MDR clears of a failure
    // to eject or feed (mdr_jam_shell / mdr_jam_feed) flip it up on a hinge at one end - a pure turn about the bone's own -z, its origin on
    // the hinge, about 60 deg - then lift it off; here it swings on that hinge only (his "hinge only and yes close before firing"):
    //  - during one of those two malfunctions it has to be open: closed, the stuck case can't be reached and a rack doesn't clear it
    //  - open, the gun doesn't fire (BlocksFiring), whatever else is going on
    //  - either hand's trigger at its free end takes it, the hand that may take the stuck case (the off hand while the gun hand has the
    //    grip, the gun hand with the off hand holding the gun up); only during one of those malfunctions, or while it's open (to shut it).
    //    The hand rides its free end, it turns with the hand about the hinge, and stays where it's let go.
    // Both MDR rigs (5.56 and 7.62) have the same weapon_deflector bone.
    public static partial class VRReloadController
    {
        public static bool deflectorByHand = true;
        public static float deflectorOpenDegrees = 60f;         // the hinge's travel (the clips' flip, before they lift it off)
        public static float deflectorOpenEnough = 0.8f;         // share of that it must be open for the stuck case and the rack
        public static float deflectorGrabRadius = 0.05f;        // m any point of the hand to its free end
        public static Vector3 deflectorHingeAxis = new Vector3(0f, 0f, -1f);  // in the bone's closed frame (the clips'); flip it if it opens into the gun

        private const string DEFLECTOR_BONE = "weapon_deflector";
        private static readonly Dictionary<Weapon, float> deflectorAngles = new Dictionary<Weapon, float>();    // per gun, degrees open: it stays across a swap
        private static readonly Dictionary<Transform, Quaternion> deflectorClosedPoses = new Dictionary<Transform, Quaternion>();
        private static FirearmController deflectorFor;
        private static Transform deflectorBone;
        private static Quaternion deflectorClosedLocalRotation;
        private static Vector3 deflectorFreeEndLocal;           // its free end, in its own frame
        private static Transform deflectorGrab;                 // ours, on that end: the hand latches on it
        private static GameObject deflectorZoneViz;

        public static bool HoldingDeflector { get; private set; }
        public static bool DeflectorInGunHand => HoldingDeflector && deflectorInGunHand;
        private static bool deflectorInGunHand;
        private static bool deflectorOnRightArm;
        private static FirearmController deflectorHeldFor;
        private static float deflectorAngleAtGrab;
        private static float handAngleAtDeflectorGrab;

        // Open far enough to count: the current gun has a deflector and it's (more than barely) open
        private static bool DeflectorOpen(FirearmController firearmController)
        {
            return firearmController != null && firearmController.Item != null && deflectorAngles.TryGetValue(firearmController.Item, out float angle) && angle > 0.5f;
        }

        // The gun doesn't fire with it open
        public static bool DeflectorBlocksFiring => deflectorByHand && DeflectorOpen(VRGlobals.firearmController);

        // A failure to eject or feed on a gun with a deflector that isn't open far enough: the case can't be reached, a rack doesn't clear it
        private static bool DeflectorBlocksClearing(FirearmController firearmController)
        {
            if (!deflectorByHand || !EnsureDeflector(firearmController))
                return false;
            Weapon.EMalfunctionState malfunction = firearmController.Item.MalfState.State;
            if (malfunction != Weapon.EMalfunctionState.Jam && malfunction != Weapon.EMalfunctionState.Feed)
                return false;
            deflectorAngles.TryGetValue(firearmController.Item, out float angle);
            return angle < deflectorOpenDegrees * deflectorOpenEnough;
        }

        private static bool HasDeflector(FirearmController firearmController)
        {
            return firearmController != null && firearmController.Item != null && AnimationFamily(firearmController).StartsWith("mdr")
                && FindRigBone(firearmController, DEFLECTOR_BONE) != null;
        }

        // The bone, its closed pose and its free end, read while it's closed (nothing of ours on it)
        private static bool EnsureDeflector(FirearmController firearmController)
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
            // The closed pose, by the bone: read the first time it's seen, before we've turned it (a gun's model can come back from Tarkov's
            // pool still turned the way we left it)
            if (!deflectorClosedPoses.TryGetValue(deflectorBone, out deflectorClosedLocalRotation))
            {
                deflectorClosedLocalRotation = deflectorBone.localRotation;
                deflectorClosedPoses[deflectorBone] = deflectorClosedLocalRotation;
            }
            deflectorFreeEndLocal = DeflectorFreeEnd(deflectorBone, Quaternion.Inverse(deflectorClosedLocalRotation) * deflectorBone.localRotation);
            if (deflectorGrab == null)
                deflectorGrab = new GameObject("VRDeflectorGrab").transform;
            deflectorGrab.SetParent(deflectorBone, worldPositionStays: false);
            deflectorGrab.localPosition = deflectorFreeEndLocal;
            deflectorGrab.localRotation = Quaternion.identity;
            if (debugLog)
                LogAction($"[VRReload] deflector found on {firearmController.Item.LocalizedShortName()}, free end {(deflectorFreeEndLocal * 100f).ToString("F1")} cm, open {angle:0} deg");
            return true;
        }

        // Its meshes' far side from the hinge, in its own (closed) frame: the middle of its meshes, pushed out across the hinge to their furthest corner
        private static Vector3 DeflectorFreeEnd(Transform bone, Quaternion open)
        {
            Vector3 axis = deflectorHingeAxis.normalized;
            Quaternion toClosed = Quaternion.Inverse(open);
            Vector3 centerSum = Vector3.zero;
            int corners = 0;
            float furthest = 0f;
            foreach (MeshFilter meshFilter in bone.GetComponentsInChildren<MeshFilter>(true))
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

        private static void SetDeflectorAngle(FirearmController firearmController, float angle)
        {
            deflectorAngles[firearmController.Item] = angle;
            PoseDeflector(firearmController);
        }

        // Its bone turned open about the hinge (Update, so the latched hand reads it this frame, and again after the animator)
        private static void PoseDeflector(FirearmController firearmController)
        {
            if (deflectorBone == null || deflectorFor != firearmController || firearmController.Item == null)
                return;
            deflectorAngles.TryGetValue(firearmController.Item, out float angle);
            deflectorBone.localRotation = deflectorClosedLocalRotation * Quaternion.AngleAxis(angle, deflectorHingeAxis);
        }

        // HandsInteractionController.LateUpdate, after the animator (its jam clips write the bone)
        public static void ApplyDeflector()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (!deflectorByHand || firearmController == null || firearmController.Item == null || deflectorFor != firearmController || deflectorBone == null)
                return;
            if (HoldingDeflector || DeflectorOpen(firearmController))
                PoseDeflector(firearmController);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // A hand at its free end that may take it now
        private static bool InDeflectorZone(FirearmController firearmController, bool gunHand)
        {
            if (!deflectorByHand || HoldingDeflector || !EnsureDeflector(firearmController))
                return false;
            bool handMayTakeIt = gunHand ? ChestHang.SupportOnly : !VRGlobals.vrPlayer.isSupporting && !ChestHang.Detached;
            Weapon.EMalfunctionState malfunction = firearmController.Item.MalfState.State;
            bool worthTaking = DeflectorOpen(firearmController)
                || ((malfunction == Weapon.EMalfunctionState.Jam || malfunction == Weapon.EMalfunctionState.Feed) && MalfunctionClearsByHand(firearmController));
            if (!handMayTakeIt || !worthTaking)
                return false;
            Vector3 freeEnd = deflectorGrab.position;
            bool atIt = HandTouchesCapsule(gunHand, freeEnd, freeEnd, deflectorGrabRadius);
            SetZoneViz(ref deflectorZoneViz, debugZones, freeEnd, deflectorGrabRadius, atIt ? new Color(0f, 1f, 0f, 0.3f) : new Color(0f, 0.6f, 1f, 0.3f));
            return atIt;
        }

        // The hand's angle about the hinge, from the closed free end, in the bone's parent's frame (the gun's body)
        private static float HandAngleAboutHinge(Vector3 handWorld)
        {
            Transform frame = deflectorBone.parent;
            Vector3 pivot = deflectorBone.localPosition;
            Vector3 axis = deflectorClosedLocalRotation * deflectorHingeAxis.normalized;
            Vector3 closedEnd = Vector3.ProjectOnPlane(deflectorClosedLocalRotation * deflectorFreeEndLocal, axis);
            Vector3 hand = Vector3.ProjectOnPlane(frame.InverseTransformPoint(handWorld) - pivot, axis);
            return closedEnd.sqrMagnitude < 1e-10f || hand.sqrMagnitude < 1e-10f ? 0f : Vector3.SignedAngle(closedEnd, hand, axis);
        }

        private static void GrabDeflector(FirearmController firearmController, bool gunHand)
        {
            HoldingDeflector = true;
            deflectorInGunHand = gunHand;
            deflectorOnRightArm = OnRightArm(gunHand);
            deflectorHeldFor = firearmController;
            deflectorAngles.TryGetValue(firearmController.Item, out deflectorAngleAtGrab);
            LatchHand(deflectorGrab, gunHand);
            handAngleAtDeflectorGrab = HandAngleAboutHinge(LatchedHandRig.position);
            if (debugLog)
                LogAction($"[VRReload] took the deflector ({(gunHand ? "gun" : "off")} hand), open {deflectorAngleAtGrab:0} deg");
            Pulse(gunHand ? SoundHand.GunHand : SoundHand.OffHand, 0.3f, 0.03f);
        }

        // From Tick: it turns with the hand from where it was taken, between shut and fully open, a click at either end
        private static void UpdateDeflector(bool offTriggerReleased, bool gunTriggerReleased)
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
            float turned = Mathf.DeltaAngle(handAngleAtDeflectorGrab, HandAngleAboutHinge(LatchedHandRig.position));
            float angle = Mathf.Clamp(deflectorAngleAtGrab + turned, 0f, deflectorOpenDegrees);
            SetDeflectorAngle(firearmController, angle);
            SoundHand hand = deflectorInGunHand ? SoundHand.GunHand : SoundHand.OffHand;
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
