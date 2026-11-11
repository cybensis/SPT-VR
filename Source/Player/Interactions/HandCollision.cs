using EFT.Interactive;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.Interactions
{
    // Physical hand collision: the free hands (and everything anchored to them - arms IK, held items)
    // can't reach through geometry. Swept-sphere collide-and-slide from last frame's resolved position
    // toward the controller-desired one, so fast swings can't tunnel and release is instant. Exempt: the
    // gun hand while a firearm is up (WeaponCollision owns that side), the support hand while gripping the
    // gun, both hands during manual eating. The enactment is stripped before each rewrite, so nothing
    // accumulates however Update order falls against the SteamVR pose callbacks (which rewrite the raw pose).
    public static class HandCollision
    {
        // ---- live-tunable knobs (UnityExplorer) ----
        public static bool enabled = true;
        public static float handRadius = 0.05f;           // m; collision sphere around the hand point
        public static bool contactHaptics = true;
        public static float hapticStrength = 0.4f;
        public static float teleportReset = 1.5f;         // m; a desired-position jump past this resets continuity

        private class HandState
        {
            public Vector3 lastResolved;
            public bool hasLast;
            public bool enacted;
            public Vector3 preEnactLocal;
            public Vector3 lastEnactLocal;
            public bool inContact;
            public float contactEndStamp = -10f;
        }

        private static readonly HandState right = new HandState();
        private static readonly HandState left = new HandState();
        private static readonly RaycastHit[] castHits = new RaycastHit[64];
        private static Door ignoredDoor;                    // the door this hand pushed this frame: the rest of its move goes past it

        public static void Apply()
        {
            if (!enabled || !WeaponCollision.enabled ||
                VRGlobals.vrPlayer == null || VRGlobals.player == null ||
                !VRGlobals.inGame || VRGlobals.menuOpen)
            {
                ResetAll();
                return;
            }

            bool firearm = VRGlobals.firearmController != null && !WeaponPatches.grenadeEquipped;
            bool eating = EatingInteractionController.ManualActive;
            bool leftHanded = VRSettings.GetLeftHandedMode();

            // With a firearm up, the gun hand is constrained inside the gun pipeline (WeaponCollision):
            // clamping it from here races GunInteractionController's anchor read (Update order is
            // nondeterministic). The support hand is exempt while gripping: its physical position drives
            // two-handed aim, and clamping it would steer the aim near walls.
            // A hand on a door (PhysicalDoors) isn't clamped either: the controller pushes it through where the door is.
            // The gun hand is free (clamped) while the gun is slung or held by the support hand alone (ChestHang.Detached).
            Transform rightHand = VRGlobals.vrPlayer.RightHand ? VRGlobals.vrPlayer.RightHand.transform : null;
            Transform leftHand = VRGlobals.vrPlayer.LeftHand ? VRGlobals.vrPlayer.LeftHand.transform : null;
            bool gunInRightHand = firearm && !ChestHang.Detached;
            ClampHand(right, rightHand, !gunInRightHand && !eating && !PhysicalDoors.HoldsRig(rightHand), physRight: !leftHanded);
            ClampHand(left, leftHand, !eating && (!firearm || !VRGlobals.vrPlayer.isSupporting) && !PhysicalDoors.HoldsRig(leftHand),
                physRight: leftHanded);
        }

        // A rig hand's position as the controller has it, this frame's clamp left out (whichever ran first this frame)
        internal static Vector3 RawPosition(Transform hand)
        {
            if (hand == null)
                return Vector3.zero;
            HandState state = null;
            if (VRGlobals.vrPlayer != null)
            {
                if (VRGlobals.vrPlayer.RightHand && hand == VRGlobals.vrPlayer.RightHand.transform)
                    state = right;
                else if (VRGlobals.vrPlayer.LeftHand && hand == VRGlobals.vrPlayer.LeftHand.transform)
                    state = left;
            }
            if (state != null && state.enacted && hand.parent != null && (hand.localPosition - state.lastEnactLocal).sqrMagnitude < 1e-10f)
                return hand.parent.TransformPoint(state.preEnactLocal);
            return hand.position;
        }

        public static void ResetAll()
        {
            right.hasLast = false;
            right.enacted = false;
            right.inContact = false;
            left.hasLast = false;
            left.enacted = false;
            left.inContact = false;
        }

        private static void ClampHand(HandState state, Transform hand, bool clamp, bool physRight)
        {
            if (hand == null)
            {
                state.hasLast = false;
                state.enacted = false;
                return;
            }

            // Strip last frame's enactment - unless the pose callback (or an equip path) already rewrote
            // the transform, in which case the fresh value is the raw pose.
            if (state.enacted)
            {
                state.enacted = false;
                if ((hand.localPosition - state.lastEnactLocal).sqrMagnitude < 1e-10f)
                    hand.localPosition = state.preEnactLocal;
            }
            if (!clamp)
            {
                state.hasLast = false;
                state.inContact = false;
                return;
            }

            Vector3 desired = hand.position;
            if (!state.hasLast || (desired - state.lastResolved).sqrMagnitude > teleportReset * teleportReset)
            {
                state.lastResolved = desired;
                state.hasLast = true;
            }

            // Collide-and-slide from the last resolved position toward where the controller wants to be.
            Vector3 resolved = state.lastResolved;
            Vector3 move = desired - resolved;
            Vector3 intended = move;
            bool hitAny = false;
            bool pushedDoor = false;
            float approach = 0f;
            float deltaTime = Mathf.Max(Time.deltaTime, 1e-4f);
            ignoredDoor = null;
            for (int iter = 0; iter < 3; iter++)
            {
                float distance = move.magnitude;
                if (distance < 1e-5f)
                    break;
                Vector3 direction = move / distance;
                if (!SweepSphere(resolved, direction, distance, out RaycastHit hit, out bool startBlocked))
                {
                    // A cast that starts in contact reports "free": only allow the move if it escapes
                    // the overlap (else pressing pushes through).
                    if (startBlocked && OverlappingWorld(resolved + move, handRadius * 0.6f))
                        break;
                    resolved += move;
                    break;
                }
                approach = Mathf.Max(approach, Vector3.Dot(direction, -hit.normal) * distance / deltaTime);
                // A door free to move swings out of the way (PhysicalDoors), and the same move goes on past it
                Door door = ignoredDoor == null ? CollisionWorld.DoorOf(hit.collider) : null;
                if (door != null && PhysicalDoors.PushedBy(door, hit.point, direction * (distance - hit.distance), deltaTime))
                {
                    ignoredDoor = door;
                    pushedDoor = true;
                    continue;
                }
                hitAny = true;
                // Shrunk cast (0.8 r) stopping 0.2 r short: the cast sphere never starts a frame
                // overlapping a collider (start-overlapped colliders are silently omitted by casts).
                float safe = Mathf.Max(0f, hit.distance - handRadius * 0.2f);
                resolved += direction * safe;
                move = Vector3.ProjectOnPlane(move - direction * safe, hit.normal);
                if (Vector3.Dot(move, intended) <= 0f)
                    break;                                 // slide reversing against the intent = wrapping an edge
            }
            state.lastResolved = resolved;

            Vector3 offset = resolved - desired;
            if (offset.sqrMagnitude > 1e-10f)
            {
                state.preEnactLocal = hand.localPosition;
                hand.position = resolved;
                state.lastEnactLocal = hand.localPosition;
                state.enacted = true;
            }

            // One light tick on first contact (edge + re-arm), nothing while pressing.
            bool contactNow = (hitAny && offset.sqrMagnitude > 1e-8f) || pushedDoor;
            if (!contactNow && state.inContact)
                state.contactEndStamp = Time.time;
            if (contactNow && !state.inContact && contactHaptics && Time.time - state.contactEndStamp > 0.2f)
            {
                float amplitude = Mathf.Clamp01(approach / 3f) * hapticStrength;
                if (amplitude > 0.05f)
                    TarkovVR.Source.Misc.HapticMixer.PlayPulse(physRight ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand,
                        amplitude, 0.05f, 90f);
            }
            state.inContact = contactNow;
        }

        // The gun's world (CollisionWorld: trunks not branches, triggers only for glass), own body ignored.
        // startBlocked = the cast began already in contact.
        private static bool SweepSphere(Vector3 origin, Vector3 direction, float distance, out RaycastHit best,
            out bool startBlocked)
        {
            best = default;
            startBlocked = false;
            float bestDist = float.MaxValue;
            int hitCount = Physics.SphereCastNonAlloc(origin, handRadius * 0.8f, direction, castHits, distance,
                CollisionWorld.Mask(WeaponCollision.GetOcclusionMask()), QueryTriggerInteraction.Collide);
            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = castHits[i];
                // Branches, triggers (glass excepted), window blockers: the gun's world (CollisionWorld)
                if (!CollisionWorld.Solid(hit.collider))
                    continue;
                if (ignoredDoor != null && hit.collider.transform.IsChildOf(ignoredDoor.transform))
                    continue;
                if (hit.collider.transform.IsChildOf(VRGlobals.player.Transform.Original))
                    continue;
                Transform weaponRoot = VRGlobals.firearmController?.WeaponRoot;
                if (weaponRoot != null && hit.collider.transform.IsChildOf(weaponRoot))
                    continue;                              // the held gun's own colliders ride the hand
                if (hit.distance <= 0f)
                {
                    startBlocked = true;
                    continue;
                }
                if (hit.distance >= bestDist)
                    continue;
                bestDist = hit.distance;
                best = hit;
            }
            return bestDist < float.MaxValue;
        }

        private static readonly Collider[] overlapBuf = new Collider[32];

        private static bool OverlappingWorld(Vector3 position, float radius)
        {
            int overlapCount = Physics.OverlapSphereNonAlloc(position, radius, overlapBuf,
                CollisionWorld.Mask(WeaponCollision.GetOcclusionMask()), QueryTriggerInteraction.Ignore);
            // (a full buffer may have missed something solid)
            if (overlapCount >= overlapBuf.Length)
                return true;
            for (int i = 0; i < overlapCount; i++)
            {
                if (overlapBuf[i].transform.IsChildOf(VRGlobals.player.Transform.Original) || !CollisionWorld.Solid(overlapBuf[i])
                    || (ignoredDoor != null && overlapBuf[i].transform.IsChildOf(ignoredDoor.transform)))
                    continue;
                return true;
            }
            return false;
        }
    }
}
