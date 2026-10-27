using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;

namespace TarkovVR.Source.Player.Interactions
{
    // Physical hand collision: the free hands (and everything anchored to them - arms IK, held items)
    // can't reach through geometry. Same model as the loose-loot clamp: swept-sphere collide-and-slide
    // from last frame's RESOLVED position toward the controller-desired one, so fast swings can't tunnel
    // and release is instant (a free path back = no constraint, no spring needed). Per-hand exemptions:
    // the gun-driven hand while a firearm is up (WeaponCollision owns that side), the support hand while
    // gripping the gun, and both hands during manual eating (it pins its own hands).
    // Enactment mirrors WeaponCollision: strip-before-rewrite bookkeeping so nothing accumulates no
    // matter how Update order falls against the SteamVR pose callbacks (which rewrite the raw pose).
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
        private static readonly RaycastHit[] castHits = new RaycastHit[16];

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

            // With a firearm up, the gun hand is constrained INSIDE the gun pipeline (WeaponCollision's
            // swept grip sphere) - clamping the marker from here raced GIC's anchor read (Update order is
            // nondeterministic) and lost, so the gun side is exempt again. The support hand stays exempt
            // while gripping: its PHYSICAL position drives two-handed aim, and clamping it would steer
            // your aim near walls (the rendered hand rides the gun anyway).
            ClampHand(right, VRGlobals.vrPlayer.RightHand ? VRGlobals.vrPlayer.RightHand.transform : null,
                !firearm && !eating, physRight: !leftHanded);
            ClampHand(left, VRGlobals.vrPlayer.LeftHand ? VRGlobals.vrPlayer.LeftHand.transform : null,
                !eating && (!firearm || !VRGlobals.vrPlayer.isSupporting), physRight: leftHanded);
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

        private static void ClampHand(HandState s, Transform hand, bool clamp, bool physRight)
        {
            if (hand == null)
            {
                s.hasLast = false;
                s.enacted = false;
                return;
            }

            // Strip last frame's enactment - unless the pose callback (or an equip path) already rewrote
            // the transform, in which case the fresh value IS the raw pose.
            if (s.enacted)
            {
                s.enacted = false;
                if ((hand.localPosition - s.lastEnactLocal).sqrMagnitude < 1e-10f)
                    hand.localPosition = s.preEnactLocal;
            }
            if (!clamp)
            {
                s.hasLast = false;
                s.inContact = false;
                return;
            }

            Vector3 desired = hand.position;
            if (!s.hasLast || (desired - s.lastResolved).sqrMagnitude > teleportReset * teleportReset)
            {
                s.lastResolved = desired;
                s.hasLast = true;
            }

            // Collide-and-slide from the last resolved position toward where the controller wants to be.
            Vector3 resolved = s.lastResolved;
            Vector3 move = desired - resolved;
            Vector3 intended = move;
            bool hitAny = false;
            float approach = 0f;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            for (int iter = 0; iter < 3; iter++)
            {
                float dist = move.magnitude;
                if (dist < 1e-5f)
                    break;
                Vector3 dir = move / dist;
                if (!SweepSphere(resolved, dir, dist, out RaycastHit hit, out bool startBlocked))
                {
                    // A cast that STARTS in contact reports "free" - only allow the move if it escapes
                    // the overlap (silently skipping zero-distance hits = pressure push-through).
                    if (startBlocked && OverlappingWorld(resolved + move, handRadius * 0.6f))
                        break;
                    resolved += move;
                    break;
                }
                hitAny = true;
                approach = Mathf.Max(approach, Vector3.Dot(dir, -hit.normal) * dist / dt);
                // Shrunk cast (0.8 r) stopping 0.2 r short: the cast sphere never starts a frame
                // overlapping a collider (start-overlapped colliders are silently omitted by casts).
                float safe = Mathf.Max(0f, hit.distance - handRadius * 0.2f);
                resolved += dir * safe;
                move = Vector3.ProjectOnPlane(move - dir * safe, hit.normal);
                if (Vector3.Dot(move, intended) <= 0f)
                    break;                                 // slide reversing against the intent = wrapping an edge
            }
            s.lastResolved = resolved;

            Vector3 offset = resolved - desired;
            if (offset.sqrMagnitude > 1e-10f)
            {
                s.preEnactLocal = hand.localPosition;
                hand.position = resolved;
                s.lastEnactLocal = hand.localPosition;
                s.enacted = true;
            }

            // One light tick on first contact (edge + re-arm), nothing while pressing.
            bool contactNow = hitAny && offset.sqrMagnitude > 1e-8f;
            if (!contactNow && s.inContact)
                s.contactEndStamp = Time.time;
            if (contactNow && !s.inContact && contactHaptics && Time.time - s.contactEndStamp > 0.2f)
            {
                float amp = Mathf.Clamp01(approach / 3f) * hapticStrength;
                if (amp > 0.05f)
                    SteamVR_Actions._default.Haptic.Execute(0f, 0.05f, 90f, amp,
                        physRight ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand);
            }
            s.inContact = contactNow;
        }

        // Same world/filter rules as the gun's barrel cast: solid-world mask (+ glass), trigger hits only
        // on the glass layer, own body ignored. startBlocked = the cast began already in contact.
        private static bool SweepSphere(Vector3 origin, Vector3 dir, float dist, out RaycastHit best,
            out bool startBlocked)
        {
            best = default;
            startBlocked = false;
            float bestDist = float.MaxValue;
            int n = Physics.SphereCastNonAlloc(origin, handRadius * 0.8f, dir, castHits, dist,
                WeaponCollision.GetOcclusionMask(), QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                RaycastHit hit = castHits[i];
                if (hit.collider.isTrigger && hit.collider.gameObject.layer != WeaponCollision.GlassLayer)
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

        private static readonly Collider[] overlapBuf = new Collider[8];

        private static bool OverlappingWorld(Vector3 pos, float radius)
        {
            int n = Physics.OverlapSphereNonAlloc(pos, radius, overlapBuf,
                WeaponCollision.GetOcclusionMask(), QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (overlapBuf[i].transform.IsChildOf(VRGlobals.player.Transform.Original))
                    continue;
                return true;
            }
            return false;
        }
    }
}
