using System;
using System.Collections.Generic;
using EFT;
using Fika.Core.Main.Players;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receive + render the VR hands/weapon for remote players, in the ObservedPlayer.LateUpdate postfix
    // (after FIKA's visual pass). Dispatch by held controller:
    //   - SPRINTING            -> skip (let FIKA's vanilla run animation play; VR poses look bad running)
    //   - Firearm              -> override the WEAPON pose; right hand IKs to the gun, left hand to the
    //                             foregrip marker if gripping else to its synced free pose
    //   - Empty / Knife / Grenade -> drive both arm IKs to the controller hand poses (the held item
    //                             follows the hand)
    //   - Meds (eating)        -> handled by EatingSyncApply (arms + food props)
    //   - anything else        -> vanilla
    // Runs on ANY peer with this module (incl. flatscreen), so they all see the VR arms/weapon.
    [HarmonyPatch]
    internal static class ArmSyncApply
    {
        // ---- live tunables ----
        public static float armIkWeight = 1f;
        public static bool driveHandRotation = true;      // false = position-only (palm keeps anim rot)
        public static Vector3 leftHandRotOffsetEuler = Vector3.zero;
        public static Vector3 rightHandRotOffsetEuler = Vector3.zero;
        // Exponential smoothing of the (chest-local) poses to hide the ~20 Hz packet stepping. 0 = snap
        // (off). Body movement stays 1:1 (we only smooth the hand/weapon OFFSET from the chest), so this
        // costs a touch of latency (~1/rate s) but no body lag. ~18 = smooth but responsive.
        public static float smoothRate = 18f;
        public static float staleTimeout = 0.4f;          // stop driving arms if no packet for this long
        public static bool debugWeaponSync = false;       // log the weapon-root / marker hierarchy once
        // Cosmetic forward nudge (metres) applied to the observed weapon pose, along the observed body's
        // FACING flattened to horizontal (away from the torso, independent of aim pitch). The sender's gun
        // is body-anchored close to the chest, which can look shoved into the torso on an observer; this
        // pushes the whole gun (the grip markers ride it, so the welded hands + arms follow) out so it sits
        // more naturally in front. Cosmetic only (observer-side; doesn't touch hitreg/aim). 0 = off.
        public static float weaponForwardOffset = 0.1f;

        private struct RemoteArms
        {
            public Vector3 leftPos;   public Quaternion leftRot;    // both chest-local
            public Vector3 rightPos;  public Quaternion rightRot;
            public float recvTime;                                  // Time.time when last received
        }
        private static readonly Dictionary<int, RemoteArms> remote = new Dictionary<int, RemoteArms>();
        private static readonly Dictionary<int, RemoteArms> displayedArms = new Dictionary<int, RemoteArms>();

        private struct RemoteWeapon
        {
            public Vector3 pos;       public Quaternion rot;        // WeaponRoot, chest-local
            public bool leftOnGrip;                                  // is the off hand on the foregrip?
            public Vector3 leftPos;   public Quaternion leftRot;    // off hand chest-local (used when free)
        }
        private static readonly Dictionary<int, RemoteWeapon> weaponRemote = new Dictionary<int, RemoteWeapon>();
        private static readonly Dictionary<int, RemoteWeapon> displayedWeapon = new Dictionary<int, RemoteWeapon>();

        public static void ResetState()
        {
            remote.Clear(); displayedArms.Clear();
            weaponRemote.Clear(); displayedWeapon.Clear();
        }

        public static void Store(VRArmsPacket p)
        {
            remote[p.NetId] = new RemoteArms
            {
                leftPos = p.LeftPos,   leftRot = p.LeftRot,
                rightPos = p.RightPos, rightRot = p.RightRot,
                recvTime = Time.time,
            };
        }

        public static void StoreWeapon(VRWeaponPacket p)
        {
            weaponRemote[p.NetId] = new RemoteWeapon
            {
                pos = p.Pos, rot = p.Rot,
                leftOnGrip = p.LeftOnGrip, leftPos = p.LeftPos, leftRot = p.LeftRot,
            };
        }

        // True once we've received a VR weapon pose for this NetId, i.e. this observed player is a VR
        // sender whose gun/knife/grenade we drive. Used to scope the observed-ADS suppression to VR
        // players only (flatscreen/other observed players keep their normal aim-down-sights animation).
        public static bool IsWeaponSynced(int netId) => weaponRemote.ContainsKey(netId);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(ObservedPlayer), nameof(ObservedPlayer.LateUpdate))]
        private static void DriveRemoteArms(ObservedPlayer __instance)
        {
            try
            {
                if (__instance.IsAI || __instance.HealthController == null || !__instance.HealthController.IsAlive)
                    return;

                var hc = __instance.HandsController;

                // Held items (gun / knife / grenade) -> override the item's WeaponRoot so it follows the
                // controller, and re-IK the hands onto it. The GUN is sprint-gated (a welded gun looks
                // bad running); melee/grenade still sync while running. WeaponRoot is on the base
                // AbstractHandsController, so this is the same path for all three.
                if (hc is Player.FirearmController || hc is Player.KnifeController
                    || hc is Player.GrenadeHandsController || hc is Player.QuickGrenadeThrowHandsController)
                {
                    if (!FikaVrSync.enableWeaponSync)
                        return;
                    if (hc is Player.FirearmController && __instance.IsSprintEnabled)
                        return;
                    if (hc.WeaponRoot == null)
                        return;
                    if (weaponRemote.TryGetValue(__instance.NetId, out RemoteWeapon w))
                        // A gun's held hand drops IK weight during reload (preserve it); a knife/grenade
                        // is ALWAYS held in that hand, so force the weight so it can't go stuck (the
                        // grenade pin-pull throw-ready state drops it otherwise).
                        DriveRemoteWeapon(__instance, hc.WeaponRoot, w, forceHeldHandWeight: !(hc is Player.FirearmController));
                    return;
                }

                // Empty hands -> drive the arms to the controller poses. (Eating/meds is handled by
                // EatingSyncApply's own ObservedPlayer.LateUpdate postfix: it drives the arms the same
                // way AND overrides the food props so they ride the synced hands.)
                // The freshness gate stops us driving stale poses if the sender goes quiet (lag).
                if (hc is Player.EmptyHandsController)
                {
                    if (!FikaVrSync.enableArmSync)
                        return;
                    if (!remote.TryGetValue(__instance.NetId, out RemoteArms arms))
                        return;
                    if (Time.time - arms.recvTime > staleTimeout)
                        return;
                    DriveRemoteHands(__instance, arms);
                }
            }
            catch (Exception e)
            {
                LogOnce(e);
            }
        }

        // Empty / knife / grenade: both arms to the synced controller poses.
        private static void DriveRemoteHands(ObservedPlayer p, RemoteArms raw)
        {
            LimbIK[] limbs = p._observedLimbs;
            if (limbs == null || limbs.Length < 2)
                return;
            Transform chest = p.PlayerBones?.Ribcage?.Original;
            if (chest == null)
                return;

            RemoteArms a = smoothRate > 0f ? SmoothArms(p.NetId, raw) : raw;
            Quaternion leftRot = chest.rotation * a.leftRot * Quaternion.Euler(leftHandRotOffsetEuler);
            Quaternion rightRot = chest.rotation * a.rightRot * Quaternion.Euler(rightHandRotOffsetEuler);
            DriveArm(limbs[0], chest.TransformPoint(a.leftPos),  leftRot);
            DriveArm(limbs[1], chest.TransformPoint(a.rightPos), rightRot);
        }

        // Armed: place the observed gun where the VR player aims it, then weld the hands. Right hand
        // rides the gun's right grip marker; left hand rides the foregrip marker WHILE gripping, else
        // follows its synced free pose (so letting go of the foregrip is visible). Marker re-IK keeps
        // EFT's weights (it drops them during reload so the hands can leave the gun).
        private static void DriveRemoteWeapon(ObservedPlayer p, Transform wr, RemoteWeapon raw, bool forceHeldHandWeight)
        {
            Transform chest = p.PlayerBones?.Ribcage?.Original;
            if (wr == null || chest == null)
                return;

            RemoteWeapon w = smoothRate > 0f ? SmoothWeapon(p.NetId, raw) : raw;
            // Nudge the gun forward so it doesn't look shoved into the torso. Push along the observed
            // body's FACING (flattened to horizontal so it never shoves up/down), not a skeleton bone
            // axis (bone forwards are unreliable). The grip markers ride the weapon, so the welded hands
            // (and arms) follow it out. The SAME nudge is applied to the free off hand below so it doesn't
            // snap back to the torso when it leaves the foregrip. Cosmetic, observer-side only.
            Vector3 fwdOffset = Vector3.zero;
            if (weaponForwardOffset != 0f)
            {
                Transform body = p.Transform?.Original;
                Vector3 fwd = body != null ? body.forward : chest.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude > 1e-4f)
                    fwdOffset = fwd.normalized * weaponForwardOffset;
            }
            wr.SetPositionAndRotation(chest.TransformPoint(w.pos) + fwdOffset, chest.rotation * w.rot);

            LimbIK[] limbs = p._observedLimbs;
            Transform[] markers = p._observedMarkers;
            if (limbs == null || limbs.Length < 2)
                return;

            // Right hand: always on the gun/item. For a knife/grenade FORCE the IK weight (the hand
            // always holds it) so it can't go stuck when EFT drops the weight (e.g. grenade pin-pull).
            if (forceHeldHandWeight && limbs[1] != null && limbs[1].solver != null)
            {
                limbs[1].solver.IKPositionWeight = 1f;
                limbs[1].solver.IKRotationWeight = 1f;
            }
            ReIkToMarker(limbs[1], markers, 1);

            // Left hand: foregrip marker if gripping, else driven to its synced free pose.
            if (limbs[0] != null && limbs[0].solver != null)
            {
                if (raw.leftOnGrip)
                {
                    ReIkToMarker(limbs[0], markers, 0);
                }
                else
                {
                    // Free off hand: same forward nudge as the gun, so it stays out front with the weapon
                    // instead of snapping back to the torso the instant it leaves the foregrip.
                    Quaternion lr = chest.rotation * w.leftRot * Quaternion.Euler(leftHandRotOffsetEuler);
                    DriveArm(limbs[0], chest.TransformPoint(w.leftPos), lr);
                }
            }

            if (debugWeaponSync && !_loggedWeapon)
            {
                _loggedWeapon = true;
                bool m1Child = markers != null && markers.Length > 1 && markers[1] != null && markers[1].IsChildOf(wr);
                FikaSyncPlugin.Log.LogInfo($"[FikaSync] weapon override NetId {p.NetId}: WeaponRoot={wr.name}, marker[1] childOfWeaponRoot={m1Child} (if FALSE the hands won't follow the moved gun -- markers don't ride WeaponRoot).");
            }
        }

        // Re-aim a limb's solver at a (now-moved) grip marker, preserving EFT's weight for this frame.
        private static void ReIkToMarker(LimbIK limb, Transform[] markers, int i)
        {
            if (limb == null || limb.solver == null || markers == null || markers.Length <= i || markers[i] == null)
                return;
            limb.solver.IKPosition = markers[i].position;
            limb.solver.IKRotation = markers[i].rotation;
            limb.solver.Update();
        }

        // internal so EatingSyncApply can drive the observed arms the same way during a meds eat.
        internal static void DriveArm(LimbIK limb, Vector3 worldPos, Quaternion worldRot)
        {
            if (limb == null || limb.solver == null)
                return;
            limb.solver.IKPositionWeight = armIkWeight;
            limb.solver.IKRotationWeight = driveHandRotation ? armIkWeight : 0f;
            limb.solver.IKPosition = worldPos;
            limb.solver.IKRotation = worldRot;
            limb.solver.Update();
        }

        // ---- smoothing (lerp the displayed chest-local pose toward the latest packet) ----
        private static RemoteArms SmoothArms(int netId, RemoteArms target)
        {
            float t = 1f - Mathf.Exp(-smoothRate * Time.deltaTime);
            if (!displayedArms.TryGetValue(netId, out RemoteArms d))
                d = target;
            d.leftPos = Vector3.Lerp(d.leftPos, target.leftPos, t);
            d.leftRot = Quaternion.Slerp(d.leftRot, target.leftRot, t);
            d.rightPos = Vector3.Lerp(d.rightPos, target.rightPos, t);
            d.rightRot = Quaternion.Slerp(d.rightRot, target.rightRot, t);
            displayedArms[netId] = d;
            return d;
        }

        private static RemoteWeapon SmoothWeapon(int netId, RemoteWeapon target)
        {
            float t = 1f - Mathf.Exp(-smoothRate * Time.deltaTime);
            if (!displayedWeapon.TryGetValue(netId, out RemoteWeapon d))
                d = target;
            d.pos = Vector3.Lerp(d.pos, target.pos, t);
            d.rot = Quaternion.Slerp(d.rot, target.rot, t);
            d.leftPos = Vector3.Lerp(d.leftPos, target.leftPos, t);
            d.leftRot = Quaternion.Slerp(d.leftRot, target.leftRot, t);
            d.leftOnGrip = target.leftOnGrip; // state, not interpolated
            displayedWeapon[netId] = d;
            return d;
        }

        private static bool _loggedWeapon;
        private static bool _err;
        private static void LogOnce(Exception e)
        {
            if (_err)
                return;
            _err = true;
            FikaSyncPlugin.Log.LogError($"[FikaSync] arm/weapon drive error: {e}");
        }
    }
}
