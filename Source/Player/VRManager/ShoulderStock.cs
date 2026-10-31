using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using TarkovVR.Source.Controls;
using TarkovVR.Source.XR;

namespace TarkovVR.Source.Player.VRManager
{
    // Virtual gunstock (Contractors/Onward-style "shouldering"): while two-handing, bring the rifle's
    // butt into the shoulder pocket and the AIM PIVOT moves from the (noisy) rear hand onto a
    // head-anchored shoulder point - a longer lever with a stable base, so aim steadies and pitch is
    // steered by the support hand like a real shouldered rifle. Rotation-only by design: the gun stays
    // marker-anchored, so this can never pull the gun off the hand (the weapon-weight lesson).
    public static class ShoulderStock
    {
        public static bool enabled = true;           // dev kill-switch; the user-facing gate is the "Virtual Gunstock" menu toggle
        // Shouldering = two physical facts, each pose-invariant (unlike the old butt-in-a-calibrated-
        // pocket check, which drifted while moving): (1) the gun points away from the head - an ANGLE,
        // borrowed from ADS detection; (2) the BUTT is tucked near the shoulder - a DISTANCE, measured
        // to the HEAD (the shoulder sits a consistent ~20cm off the head, so no per-user calibration).
        // viewAngle ("am I looking down it") was REMOVED - that's an AIMING concept; a shouldered gun
        // stays shouldered while you glance around, and gating on gaze popped the stock out every time
        // you looked off-line (the "inconsistent while moving" symptom).
        public static float enterAlignAngle = 20f;   // deg; the gun's line must pass this close to the head
        public static float exitAlignAngle = 35f;
        public static float seatButtDist = 0.175f;    // m; the virtual buttplate must be this close to the SHOULDER ref to seat
        public static float releaseButtDist = 0.3f; // m; and leave by this before unseating (hysteresis)
        // The HMD pivots ~10cm around the NECK when you look around, so referencing the EYES made both
        // checks (and the seated aim base) swing with gaze - "shouldering only works looking in certain
        // directions". All head references below reconstruct the neck point first: HMD-local offset,
        // rotates with the head, nearly stationary under head turns.
        public static Vector3 neckLocalOffset = new Vector3(0f, -0.075f, -0.08f);
        public static float shoulderDropY = 0.28f;   // m the butt-distance reference sits below the NECK point (was 0.35 below the EYES - the neck is already ~7cm down, total drop preserved)
        public static bool autoStockLength = true;   // measure the REAL grip->buttplate length per weapon so the butt lands right (a fixed length misplaces it per gun); false = fixed stockLength
        public static float stockLength = 0.17f;     // m grip->buttplate fallback (measurement failed / autoStockLength off)
        public static float blendSpeed = 10f;        // 1/s seat/unseat blend
        public static float braceStiffness = 1.6f;   // WeaponWeightFeel spring x this while seated (braced = tighter)
        public static bool seatHaptic = true;        // little thunk on both controllers when the stock seats
        public static bool debugLog = false;         // 1/sec state line (UnityExplorer)

        private static float blend;                  // 0..1 seated amount
        private static bool seated;
        private static float lastUpdateTime = -10f;
        private static Vector3 seatBaseLocal;        // (rearHand - anchor) in the yaw frame, captured at the seat moment
        private static float nextLogTime;

        public static float Blend => blend;
        public static float SpringMult => 1f + (braceStiffness - 1f) * blend;

        // Called from the two-handed aim build each frame; returns the aim base (world space) = the
        // rear hand blended onto the shoulder while seated.
        public static Vector3 UpdateAimBase(Transform rearHandT, Transform supportHandT, float dt)
        {
            Vector3 rearWorld = rearHandT.position;
            if (Time.time - lastUpdateTime > 0.25f)   // wasn't two-handing (holster/eat/menu) - reset
            {
                blend = 0f;
                seated = false;
            }
            lastUpdateTime = Time.time;

            Camera cam = VRGlobals.VRCam != null ? VRGlobals.VRCam : Camera.main;
            if (!enabled || !VRSettings.GetVirtualStockOn() || cam == null ||
                TarkovVR.Source.Player.Interactions.ChestHang.Detached)
            {
                // Detached = the rear hand is off the gun (slung / support-held) - its position is
                // meaningless as a stock butt, so don't let it cause phantom seat thunks.
                blend = 0f;
                seated = false;
                return rearWorld;
            }

            // ALL math in TRACKING SPACE: the hands' rig-LOCAL positions (pure controller tracking)
            // vs the RAW OpenVR HMD pose. Neither world space NOR the camera's local pose works here:
            // in the hideout the rig is parked away from the player and the camera's LOCAL transform
            // gets re-written to compensate (headset log: camL=(0.8,1.2,-8.7) - a 9m "local" offset,
            // physically impossible as tracking), so the camera is poisoned as a head reference in
            // both spaces. The raw HMD pose is by definition in the same tracking frame as the hand
            // pose actions - and identical to the camera's local in raids (where nothing rewrites it).
            Transform rig = rearHandT.parent;
            Vector3 rearHand = rearHandT.localPosition;
            Vector3 supportHand = rig != null ? rig.InverseTransformPoint(supportHandT.position) : supportHandT.position;
            if (!RawHmdPose(out Vector3 headPos, out Quaternion headRot))
            {
                headPos = cam.transform.localPosition;   // no OpenVR = best remaining guess
                headRot = cam.transform.localRotation;
            }

            // Gaze-proof head reference: the neck point (see neckLocalOffset).
            Vector3 neck = headPos + headRot * neckLocalOffset;

            // (1) POSTURE angle: is the head behind the gun's line (the same rear->support vector
            // branch 1 aims with)? A sight point 1m out, like HandleIronSightAiming - but measured
            // from the NECK, not the eyes, so turning the head can't swing the angle.
            Vector3 aimDir = (supportHand - rearHand).normalized;
            Vector3 sightPoint = rearHand + aimDir;
            Vector3 toSight = (sightPoint - neck).normalized;
            float alignAngle = Vector3.Angle(aimDir, toSight);

            // (2) BUTT distance: is the virtual buttplate tucked near the SHOULDER? Measured to a ref
            // dropped shoulderDropY below the eyes (Y is up in tracking space) - the shoulder sits ~15cm
            // under the HMD, so measuring to the eye line inflated the distance. This is what
            // distinguishes "shouldered" from "held out at arm's length pointing forward", which the
            // angle alone can't - and it needs NO gaze, so looking around never unseats.
            float stockLen = autoStockLength ? WeaponStockLength() : stockLength;
            Vector3 butt = rearHand - aimDir * stockLen;
            Vector3 shoulderRef = neck - Vector3.up * shoulderDropY;
            float buttDist = Vector3.Distance(butt, shoulderRef);

            bool nowSeated = seated
                ? (alignAngle <= exitAlignAngle && buttDist <= releaseButtDist)
                : (alignAngle <= enterAlignAngle && buttDist <= seatButtDist);

            // Yaw-only head frame: while seated the aim base is held rigid in it (nodding/leaning must
            // not drag the base around); projected-up fallback keeps yaw sane looking straight up/down.
            Vector3 fwd = headRot * Vector3.forward;
            Vector3 flat = fwd;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f)
            {
                flat = (headRot * Vector3.up) * Mathf.Sign(-fwd.y);
                flat.y = 0f;
            }
            Quaternion yawRot = flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;

            if (nowSeated && !seated)
            {
                // Capture the CURRENT aim base relative to the NECK: seating changes the aim by exactly
                // ZERO (the "activating turns the gun" fix) - the yaw frame just holds that point rigid
                // afterward, which is the whole stabilization. Neck, not eyes: anchored to the HMD, a
                // seated glance around swung the stabilized aim base with the head pivot.
                seatBaseLocal = Quaternion.Inverse(yawRot) * (rearHand - neck);
                if (seatHaptic)
                    Pulse();
            }
            seated = nowSeated;

            blend = Mathf.MoveTowards(blend, seated ? 1f : 0f, blendSpeed * dt);
            Vector3 seatedBase = neck + yawRot * seatBaseLocal;

            if (debugLog && Time.time >= nextLogTime)
            {
                nextLogTime = Time.time + 1f;
                Plugin.MyLog.LogInfo($"[ShoulderStock] align={alignAngle:0.0} buttDist={buttDist:0.00} stock={stockLen:0.00} seated={seated} blend={blend:0.00}");
            }
            // back to the caller's world space through the hands' own rig (whatever/wherever it is)
            Vector3 baseTs = Vector3.Lerp(rearHand, seatedBase, blend);
            return rig != null ? rig.TransformPoint(baseTs) : baseTs;
        }

        // Per-weapon grip->buttplate length: the rear-most renderer extent BEHIND the grip marker,
        // projected on the barrel-back axis (WeaponRoot +up; muzzle = -up). Locates the virtual butt
        // per gun (an SMG vs a full rifle differ a lot). A world-space MEASUREMENT is safe even though
        // the seat math is tracking-space: the result is a SCALAR, rigid within the weapon, so it's
        // frame-agnostic. Cached per HandsController, 1s refresh (catches stock fold/unfold + mods).
        private static float measuredStock = -1f;
        private static object stockOwner;
        private static float stockRefresh;
        private static float WeaponStockLength()
        {
            var fc = VRGlobals.firearmController;
            if (fc == null || fc.WeaponRoot == null)
                return stockLength;
            if (!ReferenceEquals(stockOwner, fc) || Time.time > stockRefresh)
            {
                stockOwner = fc;
                stockRefresh = Time.time + 1f;
                measuredStock = MeasureStock(fc);
            }
            return measuredStock > 0.01f ? measuredStock : stockLength;
        }

        private static readonly Vector3[] cornerSigns =
        {
            new Vector3(-1,-1,-1), new Vector3(-1,-1,1), new Vector3(-1,1,-1), new Vector3(-1,1,1),
            new Vector3(1,-1,-1),  new Vector3(1,-1,1),  new Vector3(1,1,-1),  new Vector3(1,1,1)
        };

        private static float MeasureStock(EFT.Player.FirearmController fc)
        {
            Transform wr = fc.WeaponRoot;
            var links = fc.HandsHierarchy;
            Transform grip = links != null && links.Transforms != null && links.Transforms.Length > 8 && links.Transforms[8] != null
                ? links.Transforms[8] : wr;
            Vector3 back = wr.up;   // toward the shooter
            Vector3 gripPos = grip.position;
            float best = 0f;
            Renderer[] renderers = wr.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                // MESH-LOCAL bounds corners, transformed out and projected - NOT Renderer.bounds:
                // a world AABB inflates with the gun's WORLD orientation (up to ~40% on a diagonal),
                // which made the measured stock - and therefore the seat's butt distance - depend on
                // which way you'd stick-turned ("shouldering only works facing certain directions").
                Mesh mesh = null;
                if (renderers[i] is SkinnedMeshRenderer smr)
                    mesh = smr.sharedMesh;
                else
                {
                    MeshFilter mf = renderers[i].GetComponent<MeshFilter>();
                    if (mf != null)
                        mesh = mf.sharedMesh;
                }
                if (mesh != null)
                {
                    Bounds lb = mesh.bounds;   // mesh-local: orientation-independent
                    Transform rt = renderers[i].transform;
                    for (int c = 0; c < 8; c++)
                    {
                        Vector3 corner = lb.center + Vector3.Scale(lb.extents, cornerSigns[c]);
                        float proj = Vector3.Dot(rt.TransformPoint(corner) - gripPos, back);
                        if (proj > best)
                            best = proj;
                    }
                }
                else
                {
                    // no mesh (particle/etc.) - center-only projection, no inflating extents term
                    float proj = Vector3.Dot(renderers[i].bounds.center - gripPos, back);
                    if (proj > best)
                        best = proj;
                }
            }
            return Mathf.Clamp(best, 0.05f, 0.6f);
        }

        // The HMD's pose straight from OpenVR, in the app's current tracking universe (the same frame
        // the SteamVR hand-pose actions report in).
        private static readonly TrackedDevicePose_t[] hmdPoses = new TrackedDevicePose_t[1];
        internal static bool RawHmdPose(out Vector3 pos, out Quaternion rot)
        {
            // OpenXR's headset pose is already in the same tracking space as the hand poses
            if (OpenXRBootstrap.Active)
                return OpenXRInput.TryGetHeadPose(out pos, out rot);
            var sys = OpenVR.System;
            if (sys == null)
            {
                pos = Vector3.zero;
                rot = Quaternion.identity;
                return false;
            }
            var origin = OpenVR.Compositor != null
                ? OpenVR.Compositor.GetTrackingSpace() : ETrackingUniverseOrigin.TrackingUniverseStanding;
            sys.GetDeviceToAbsoluteTrackingPose(origin, 0f, hmdPoses);
            if (!hmdPoses[0].bPoseIsValid)
            {
                pos = Vector3.zero;
                rot = Quaternion.identity;
                return false;
            }
            var rt = new SteamVR_Utils.RigidTransform(hmdPoses[0].mDeviceToAbsoluteTracking);
            pos = rt.pos;
            rot = rt.rot;
            return true;
        }

        private static void Pulse()
        {
            SteamVR_Input_Sources gunHand = VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
            SteamVR_Input_Sources offHand = VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand;
            VRInput.Haptic.Execute(0f, 0.06f, 110f, 0.5f, gunHand);
            VRInput.Haptic.Execute(0f, 0.06f, 110f, 0.35f, offHand);
        }
    }
}
