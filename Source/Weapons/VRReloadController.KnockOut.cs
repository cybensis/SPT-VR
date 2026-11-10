using System.Collections.Generic;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // An AK's magazine knocked out with the next one (his ask: "pop out another mag with a mag if you push it into the back of the mag
    // where the release latch is ... with actual physics, like I actually pushed it out"). The new magazine in the off hand, its body
    // pushed forward into the paddle catch behind the gun's magazine: the catch lets go and the old magazine leaves with the push - the
    // gun's own motion plus the push's share forward, rocking off its hinge the way it comes out by hand (the gun's rock-in axis, which
    // the push at the catch turns it about) - then it's loose loot like any dropped magazine. Only guns whose magazines rock in (the
    // paddle behind the magazine), not a belt box's hinge. The B drop's path (RunDrop): synced and placed the same way.
    public static partial class VRReloadController
    {
        public static bool knockMagazineOut = true;
        public static float knockContactRadius = 0.03f;     // m from the catch to the held magazine's body that counts as touching it
        public static float knockSpeed = 0.35f;             // m/s the held magazine must move forward (toward the muzzle) against the gun
        public static float knockTransfer = 0.9f;           // share of that push the old magazine leaves with
        public static float knockMaxSpeed = 4f;             // m/s cap on the push it's given
        public static float knockDrop = 0.3f;               // m/s down on top (it falls out of the well as the catch lets go)
        public static float knockSpin = 1f;                 // its rock off the hinge = the push over the catch's lever to the hinge, times this
        public static float knockMaxSpin = 7f;              // rad/s cap (Unity's own default cap on a body's spin)
        public static bool knockLog = true;                 // a line per knock, and per touch too slow to knock (round 1)

        private static FirearmController knockCatchFor;
        private static Transform knockCatchPart;
        private static Renderer knockCatchRenderer;
        private static Vector3 knockCatchLast;
        private static float knockCatchLastTime = -1f;
        private static GameObject knockBodyFor;
        private static MeshFilter knockBody;                // the held magazine's body (its biggest mesh)
        private static bool knockTouching;                  // touching the catch since last frame (the too-slow line once per touch)
        private static float knockTouchFastest;
        private static GameObject knockZoneViz;

        // Every Tick, after the grabs: the held new magazine against the gun's catch
        private static void TickKnockOut()
        {
            bool showZone = false;
            Vector3 zoneAt = Vector3.zero;
            try
            {
                if (!knockMagazineOut || !HoldingMagazine || HoldingGunMagazine || magazineInGunHand || onRail || HoldingPalmedMagazine
                    || HoldingSeatedMagazine || heldProp == null)
                {
                    ForgetKnockTouch();
                    knockCatchLastTime = -1f;
                    return;
                }
                if (!CanStartGrab(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot)
                    || weapon.MalfState.State == Weapon.EMalfunctionState.Feed || weapon.GetCurrentMagazine() == null)
                {
                    ForgetKnockTouch();
                    knockCatchLastTime = -1f;
                    return;
                }
                RockIn rockIn = RockInOf(firearmController);
                if (rockIn == null || rockIn.pivot != Vector3.zero || !TryGetKnockCatch(firearmController, weapon, magazineSlot, out Vector3 catchPoint))
                {
                    ForgetKnockTouch();
                    knockCatchLastTime = -1f;
                    return;
                }
                showZone = debugZones;
                zoneAt = catchPoint;

                // The gun's own motion at the catch (the body, the gun hand): the push is the held magazine's motion against it
                float now = Time.time;
                float sinceLast = now - knockCatchLastTime;
                bool measured = knockCatchLastTime > 0f && sinceLast > 1e-4f && sinceLast < 0.1f;
                Vector3 gunVelocity = measured ? (catchPoint - knockCatchLast) / sinceLast : Vector3.zero;
                knockCatchLast = catchPoint;
                knockCatchLastTime = now;
                if (!measured)
                    return;

                float distance = DistanceToHeldBody(catchPoint);
                if (distance > knockContactRadius)
                {
                    ForgetKnockTouch();
                    return;
                }
                Vector3 forward = -firearmController.WeaponRoot.up;
                float push = Vector3.Dot(HandWorldVelocity(gunHand: false) - gunVelocity, forward);
                if (push < knockSpeed)
                {
                    knockTouching = true;
                    knockTouchFastest = Mathf.Max(knockTouchFastest, push);
                    return;
                }
                knockTouching = false;
                KnockOut(firearmController, weapon, magazineSlot, rockIn, catchPoint, forward, push, gunVelocity, distance);
            }
            finally
            {
                SetZoneViz(ref knockZoneViz, showZone, zoneAt, knockContactRadius, new Color(1f, 0.5f, 0f, 0.35f));
            }
        }

        // A touch that ended without a knock: how fast it got, to tune knockSpeed by
        private static void ForgetKnockTouch()
        {
            if (knockTouching && knockLog)
                Plugin.MyLog.LogInfo($"[VRReload] the held magazine touched the catch but didn't knock: {knockTouchFastest:0.00} m/s forward at most (needs {knockSpeed:0.00})");
            knockTouching = false;
            knockTouchFastest = 0f;
        }

        private static void KnockOut(FirearmController firearmController, Weapon weapon, Slot magazineSlot, RockIn rockIn, Vector3 catchPoint,
            Vector3 forward, float push, Vector3 gunVelocity, float distance)
        {
            Magazine magazine = weapon.GetCurrentMagazine();
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            Transform well = MagazineWellBone(firearmController, magazineSlot);
            if (magazine == null || magazineView == null || magazineView.ItemView == null || well == null)
                return;

            // Tarkov's sounds of the catch and the magazine coming out; the knock in both hands
            PlayFirstGunSound(firearmController, SoundHand.OffHand, MAG_BUTTON_SOUNDS);
            PlayFirstGunSound(firearmController, SoundHand.GunBody, MAG_OUT_SOUNDS);
            Pulse(SoundHand.OffHand, 0.7f, 0.05f);
            Pulse(SoundHand.GunHand, 0.45f, 0.05f);

            if (CanDropToGround(magazine))
            {
                float given = Mathf.Min(push * knockTransfer, knockMaxSpeed);
                Vector3 velocity = gunVelocity + forward * given + Vector3.down * knockDrop;
                // The push at the catch turns it about the hinge (the well, its rock-in axis), out the way it rocks by hand
                float lever = Mathf.Max(0.03f, Vector3.Distance(catchPoint, well.position));
                float spin = Mathf.Min(given / lever * knockSpin, knockMaxSpin);
                Vector3 angularVelocity = well.TransformDirection(rockIn.axis).normalized * spin;
                RunDrop(magazine, firearmController, magazineView.ItemView.position, magazineView.ItemView.rotation, velocity, angularVelocity);
                if (knockLog)
                    Plugin.MyLog.LogInfo($"[VRReload] {magazine.LocalizedShortName()} knocked out by the next one: pushed {push:0.00} m/s forward at "
                        + $"{distance * 100f:0.0} cm from the catch -> leaves at {given:0.00} m/s forward + the gun's {gunVelocity.magnitude:0.00} m/s, rocking {spin:0.0} rad/s");
                return;
            }
            // The hideout: into the rig, as B does there
            if (TryBuildMoveToRig(magazine, out OperationResult<MoveResult> rigMove))
            {
                RunMoveToRig(magazine, firearmController, rigMove);
                if (knockLog)
                    Plugin.MyLog.LogInfo($"[VRReload] {magazine.LocalizedShortName()} knocked out by the next one (into the rig: no ground here)");
            }
        }

        // The catch: the gun's release part as its own measure found it (else the first part named like one), at its mesh if it has one
        private static bool TryGetKnockCatch(FirearmController firearmController, Weapon weapon, Slot magazineSlot, out Vector3 catchPoint)
        {
            catchPoint = Vector3.zero;
            if (knockCatchFor != firearmController)
            {
                knockCatchFor = firearmController;
                knockCatchPart = null;
                knockCatchRenderer = null;
                knockCatchLastTime = -1f;
                Transform well = MagazineWellBone(firearmController, magazineSlot);
                if (EnsureReleaseParts(firearmController, weapon, magazineSlot) && releaseParts != null && releaseParts.Length > 0)
                    knockCatchPart = releaseParts[0];
                else
                {
                    List<Transform> candidates = ReleaseCandidates(firearmController.WeaponRoot, well);
                    knockCatchPart = candidates.Count > 0 ? candidates[0] : null;
                }
                if (knockCatchPart != null)
                {
                    foreach (Renderer renderer in knockCatchPart.GetComponentsInChildren<Renderer>(true))
                    {
                        if (well == null || !renderer.transform.IsChildOf(well))
                        {
                            knockCatchRenderer = renderer;
                            break;
                        }
                    }
                }
                if (knockLog)
                    Plugin.MyLog.LogInfo($"[VRReload] {firearmController.Item.LocalizedShortName()}: knock-out catch = {(knockCatchPart != null ? knockCatchPart.name : "none found")}"
                        + (knockCatchRenderer != null ? $" (its mesh {knockCatchRenderer.name})" : ""));
            }
            if (knockCatchPart == null)
                return false;
            catchPoint = knockCatchRenderer != null && knockCatchRenderer.enabled ? knockCatchRenderer.bounds.center : knockCatchPart.position;
            return true;
        }

        // How far the point is from the held magazine's body: its biggest mesh's own bounds (not the world box, which grows as it turns)
        private static float DistanceToHeldBody(Vector3 point)
        {
            if (knockBodyFor != heldProp)
            {
                knockBodyFor = heldProp;
                knockBody = null;
                float biggest = 0f;
                foreach (MeshFilter meshFilter in heldProp.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (meshFilter.sharedMesh == null)
                        continue;
                    Vector3 size = Vector3.Scale(meshFilter.sharedMesh.bounds.size, meshFilter.transform.lossyScale);
                    float volume = Mathf.Abs(size.x * size.y * size.z);
                    if (volume > biggest)
                    {
                        biggest = volume;
                        knockBody = meshFilter;
                    }
                }
            }
            if (knockBody == null)
                return float.MaxValue;
            Transform body = knockBody.transform;
            Vector3 closest = knockBody.sharedMesh.bounds.ClosestPoint(body.InverseTransformPoint(point));
            return Vector3.Distance(body.TransformPoint(closest), point);
        }
    }
}
