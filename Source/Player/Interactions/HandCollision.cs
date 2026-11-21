using EFT.Interactive;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.Interactions
{
    // Physical hand collision: the free hands (and everything anchored to them, like the arm IK and held items)
    // can't reach through geometry. A swept sphere collides and slides from last frame's resolved position toward
    // the controller's, so fast swings can't tunnel through and letting go is instant. Exempt: the gun hand while a
    // firearm is up (WeaponCollision handles it), the support hand while gripping the gun, and both hands during
    // manual eating. Last frame's correction is removed before each new one, so nothing accumulates whatever order
    // Update runs in relative to the SteamVR pose callbacks (which rewrite the raw pose).
    public static class HandCollision
    {
        // ---- live-tunable knobs (UnityExplorer) ----
        public static float handRadius = 0.05f;           // m; collision sphere around the hand point
        public static float hapticStrength = 0.4f;
        public static float teleportReset = 1.5f;         // m; a controller jump further than this restarts the sweep from it

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
            if (!WeaponCollision.enabled ||
                VRGlobals.vrPlayer == null || VRGlobals.player == null ||
                !VRGlobals.inGame || VRGlobals.menuOpen)
            {
                ResetAll();
                return;
            }

            bool firearm = VRGlobals.firearmController != null && !WeaponPatches.grenadeEquipped;
            bool eating = EatingInteractionController.ManualActive;
            // On a mounted gun the hands hold its handles: its own colliders would keep them off
            if (MountedGun.Active)
            {
                ResetAll();
                return;
            }
            bool leftHanded = VRSettings.GetLeftHandedMode();

            // With a firearm up, the gun hand is constrained in the gun pipeline (WeaponCollision). Clamping it here
            // would race GunInteractionController's anchor read, since the Update order isn't fixed. The support hand
            // is exempt while gripping: its physical position drives two-handed aim, and clamping it would steer the
            // aim near walls. A hand holding a door (PhysicalDoors) isn't clamped either: the controller has to push
            // through where the door is. While the gun is slung or held by the support hand alone (ChestHang.Detached),
            // the gun hand is free, so it is clamped.
            Transform rightHand = VRGlobals.vrPlayer.RightHand ? VRGlobals.vrPlayer.RightHand.transform : null;
            Transform leftHand = VRGlobals.vrPlayer.LeftHand ? VRGlobals.vrPlayer.LeftHand.transform : null;
            bool gunInRightHand = firearm && !ChestHang.Detached;
            ClampHand(right, rightHand, !gunInRightHand && !eating && !PhysicalDoors.HoldsRig(rightHand), physRight: !leftHanded);
            ClampHand(left, leftHand, !eating && (!firearm || !VRGlobals.vrPlayer.isSupporting) && !PhysicalDoors.HoldsRig(leftHand),
                physRight: leftHanded);
        }

        // A rig hand's position as the controller has it, without this frame's clamp (whether or not the clamp has run yet)
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

            // Undo last frame's correction, unless the pose callback (or an equip path) has already rewritten
            // the transform, in which case the new value is the raw pose.
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
                    // A cast that starts in contact reports no hit. Only allow the move if it gets out of
                    // the overlap, otherwise pressing pushes through.
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
                // The cast uses a smaller sphere (0.8 r) and stops 0.2 r short, so it never starts a frame
                // overlapping a collider (casts silently skip colliders they start inside).
                float safe = Mathf.Max(0f, hit.distance - handRadius * 0.2f);
                resolved += direction * safe;
                move = Vector3.ProjectOnPlane(move - direction * safe, hit.normal);
                if (Vector3.Dot(move, intended) <= 0f)
                    break;                                 // the slide turned against the intended move: it's wrapping an edge
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

            // One light haptic tick on first contact (re-armed 0.2 s after a contact ends), nothing while pressing.
            bool contactNow = (hitAny && offset.sqrMagnitude > 1e-8f) || pushedDoor;
            if (!contactNow && state.inContact)
                state.contactEndStamp = Time.time;
            if (contactNow && !state.inContact && Time.time - state.contactEndStamp > 0.2f)
            {
                float amplitude = Mathf.Clamp01(approach / 3f) * hapticStrength;
                if (amplitude > 0.05f)
                    TarkovVR.Source.Misc.HapticMixer.PlayPulse(physRight ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand,
                        amplitude, 0.05f, 90f);
            }
            state.inContact = contactNow;
        }

        // Sweeps against the same world as the gun (CollisionWorld: tree trunks but not branches, triggers only for glass),
        // ignoring the player's own body. startBlocked = the cast began already in contact.
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
                // Skip branches, triggers (except glass) and window blockers, as the gun's world does (CollisionWorld)
                if (!CollisionWorld.Solid(hit.collider))
                    continue;
                if (ignoredDoor != null && hit.collider.transform.IsChildOf(ignoredDoor.transform))
                    continue;
                if (hit.collider.transform.IsChildOf(VRGlobals.player.Transform.Original))
                    continue;
                Transform weaponRoot = VRGlobals.firearmController?.WeaponRoot;
                if (weaponRoot != null && hit.collider.transform.IsChildOf(weaponRoot))
                    continue;                              // the held gun's own colliders move with the hand
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
            // A full buffer may have missed something solid
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
