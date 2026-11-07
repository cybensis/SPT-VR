using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Clearing a malfunction by hand (Manual Reload), the way Tarkov's own clearing clips do it (each gun's misfire / jam_shell /
    // jam_feed / jam_hard / jam_soft, read for the M4, AK, MP5, Glock and SV-98):
    //  - misfire, soft slide, failure to feed: the handle or slide all the way back and let go (a bolt-action: the bolt cycled)
    //  - failure to eject: the case stuck in the ejection port, taken out with either hand's trigger there (a full rack clears it too)
    //  - hard slide: the handle (or bolt) doesn't move for the first hardSlideStuckTugs tugs, each a hard clunk through the gun; it
    //    breaks free on the next, then a full rack clears it
    // What clears it is Tarkov's one repair for all five (RepairMalfunction: the gun's state, the dud or stuck round thrown out, the
    // next round chambered), started through Check Chamber and skipped to its end, like the magazine and rechamber skips. Tarkov wants
    // the type examined first: the right fix by hand counts as that (its notification names the type as it clears).
    // Co-op: those are FIKA's own CheckChamber / ExamineWeapon packets; the sync module finishes them on the other players' copy the
    // moment they start (ReloadSyncApply.SkipMalfunctionOperation), else that copy refuses the repair and stays jammed.
    public static partial class VRReloadController
    {
        public static bool malfunctionsByHand = true;           // false = clearing one stays the interaction menu's
        public static int hardSlideStuckTugs = 2;               // tugs that don't move it; it breaks free on the next
        public static float hardSlideTugDistance = 0.025f;      // m the hand pulls on the stuck handle or bolt for a tug
        public static float hardSlideTugReset = 0.01f;          // m it has to come back toward rest before the next one counts
        public static float hardSlideGive = 0.004f;             // m a stuck handle still gives
        public static float jamPortRadius = 0.05f;              // m any point of the hand to the ejection port for its trigger to take a stuck case out
        public static bool malfunctionPoseOffWhileHeld = true;  // Tarkov's malfunction pose off while the hand works the handle or bolt,
                                                                // as its own repair does (it would hold the bolt where the jam left it)
        // TEST: your next shot fails with this type (UnityExplorer), then it goes back to None. Only where Tarkov itself could roll it
        // (ForcedMalfunction), so a gun never gets one it has no animations for.
        public static Weapon.EMalfunctionState testMalfunctionNextShot = Weapon.EMalfunctionState.None;

        private static bool malfunctionRacked;                  // this grab has been all the way back on a malfunctioned gun
        private static int hardSlideTugsDone;
        private static bool hardSlideTugging;
        private static bool hardSlideFree = true;
        private static FirearmController malfunctionPoseOffFor;
        private static GameObject jamPortZoneViz;

        // The stuck case of a failure to eject, the fingers locked on it
        public static float jamCaseBreakDistance = 0.05f;       // m the controller pulls away from where it took it before the case comes out
        public static float jamCaseGive = 0.006f;               // m the stuck case gives toward the pull, all of it at the break
        public static float jamCaseWiggleDegrees = 15f;         // deg it tips with the hand's turn while stuck (about its end in the gun)
        public static float jamCaseStrain = 1f;                 // the building buzz in that hand while it pulls (0 = none)
        // The thumb on the case ("Thumb needs to be closed a bit more but I imagine this will also depend on the bullet type"; a set bend
        // along the finger tracking's curl axis then: "thumb is still no good"): its tip bent onto the case's surface every frame, from the
        // round hold's pose (ThumbOntoCase), whatever the case's size and the hand's turn
        public static bool jamCaseThumbReach = true;            // false = the round hold's thumb as it is
        public static float jamCaseThumbMaxBend = 45f;          // deg each thumb joint may turn off the hold's pose to get there
        public static float jamCaseThumbPad = 0.006f;           // m the tip point stays off the case's surface (the pad of the thumb)
        public static bool HoldingJamCase { get; private set; }
        public static bool JamCaseInGunHand => HoldingJamCase && jamCaseInGunHand;
        private static bool jamCaseInGunHand;
        private static bool jamCaseOnRightArm;                  // the arm drawing that hand (OnRightArm), as it was at the grab
        private static bool jamCaseOut;
        private static bool jamCaseWontClear;                   // the pull broke it free but Tarkov's repair refused: no retry every frame
        private static FirearmController jamCaseFor;
        private static AmmoPoolObject jamCase;
        private static Transform jamCasePort;
        private static Vector3 jamCaseRestLocalPosition;        // stuck, in the port bone's frame
        private static Quaternion jamCaseRestLocalRotation;
        private static Vector3 jamCasePivotInCase;              // its end inside the gun, in its own frame
        private static Vector3 jamCaseAxisInProp, jamCaseCenterInProp;
        private static Vector3 controllerAtJamGrabInGun;
        private static Quaternion controllerRotationAtJamGrabInGun;
        private static Vector3 jamGripPointInPalm;              // where the round hold's fingers close on a round, in the palm bone's frame
        private static Vector3 jamCaseGrabInCase;               // where on the case they closed, in its own frame: the wrist's pivot while stuck
        private static float jamHandSnapStartTime;
        private static Vector3 jamCaseInHandPosition;           // out: the case as it came out, in the palm bone's frame
        private static Quaternion jamCaseInHandRotation;
        // The case as seen (its shown meshes: the model also carries the hidden full round's), for the thumb: centre and length axis in its
        // own frame, half length and radius in metres
        private static Vector3 jamCaseShownCenter, jamCaseShownAxis;
        private static float jamCaseShownHalfLength, jamCaseRadius;
        private static Transform jamThumbPalm;
        private static Transform[] jamThumbJoints;
        private static readonly Quaternion[] jamThumbPosed = new Quaternion[3];

        // The malfunction is the hands' to clear: Manual Reload, and a handle, slide or bolt they work
        internal static bool MalfunctionClearsByHand(FirearmController firearmController)
        {
            if (!enabled || !malfunctionsByHand || firearmController == null || firearmController.Item == null
                || firearmController.Item.MalfState.State == Weapon.EMalfunctionState.None || !VRSettings.GetManualReloadOn())
                return false;
            Weapon weapon = firearmController.Item;
            if (weapon.BoltAction)
                return ManualBoltOn(firearmController);
            return chargingHandle && (TryGetChargingHandle(firearmController, weapon) || TryGetSideKnobPoint(firearmController, out _));
        }

        // The handle, side handle and bolt can be taken: no malfunction, or one the hands clear
        private static bool HandsMayWorkAction(FirearmController firearmController, Weapon weapon)
        {
            return weapon.MalfState.State == Weapon.EMalfunctionState.None || MalfunctionClearsByHand(firearmController);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The handle / slide / bolt on a malfunctioned gun

        // At every grab of the handle or bolt
        private static void BeginMalfunctionGrab(FirearmController firearmController)
        {
            malfunctionRacked = false;
            hardSlideTugsDone = 0;
            hardSlideTugging = false;
            Weapon.EMalfunctionState malfunction = firearmController.Item.MalfState.State;
            hardSlideFree = malfunction != Weapon.EMalfunctionState.HardSlide;
            if (malfunction == Weapon.EMalfunctionState.None)
                return;
            if (debugLog)
                LogAction($"[VRReload] took the action of a gun with a {malfunction}");
            if (malfunctionPoseOffWhileHeld)
            {
                SetMalfunctionPose(firearmController, 0f);
                malfunctionPoseOffFor = firearmController;
            }
        }

        // Let go without clearing it: the gun shows its malfunction again
        private static void EndMalfunctionGrab(FirearmController firearmController)
        {
            if (malfunctionPoseOffFor != null && malfunctionPoseOffFor == firearmController && firearmController.Item != null
                && firearmController.Item.MalfState.State != Weapon.EMalfunctionState.None)
                SetMalfunctionPose(firearmController, 1f);
            malfunctionPoseOffFor = null;
            malfunctionRacked = false;
        }

        private static void SetMalfunctionPose(FirearmController firearmController, float weight)
        {
            FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
            if (firearmsAnimator != null && firearmsAnimator.MALFUNCTION_LAYER_INDEX >= 0)
                firearmsAnimator.SetLayerWeight(firearmsAnimator.MALFUNCTION_LAYER_INDEX, weight);
        }

        // All the way back on a malfunctioned gun: the repair waits for the let go (the bolt going home), Tarkov's rechamber / bolt
        // catch rules don't run. True = taken here.
        private static bool MalfunctionRackedBack(FirearmController firearmController)
        {
            if (firearmController.Item.MalfState.State == Weapon.EMalfunctionState.None || !MalfunctionClearsByHand(firearmController))
                return false;
            malfunctionRacked = true;
            if (debugLog)
                LogAction($"[VRReload] racked back on a {firearmController.Item.MalfState.State}: cleared as it goes forward");
            return true;
        }

        // A hard slide: the hand pulls, the handle (or bolt) barely gives. Each pull past hardSlideTugDistance is a tug, a hard clunk
        // through the gun with its rattle; the one after hardSlideStuckTugs breaks it free. wanted = m the hand would have it back.
        private static float StuckPull(FirearmController firearmController, float wanted, SoundHand hand)
        {
            if (hardSlideFree)
                return wanted;
            if (wanted >= hardSlideTugDistance && !hardSlideTugging)
            {
                hardSlideTugging = true;
                hardSlideTugsDone++;
                PlayFirstGunSound(firearmController, hand, "JamRattle");
                if (hardSlideTugsDone > hardSlideStuckTugs)
                {
                    hardSlideFree = true;
                    Pulse(hand.Seating, 0.6f, 0.05f);
                    if (debugLog)
                        LogAction($"[VRReload] hard slide: broke free on tug {hardSlideTugsDone}");
                    return wanted;
                }
                Pulse(hand.Seating, 0.8f, 0.06f);
                if (debugLog)
                    LogAction($"[VRReload] hard slide: tug {hardSlideTugsDone}, still stuck");
            }
            else if (wanted <= hardSlideTugReset)
            {
                hardSlideTugging = false;
            }
            return Mathf.Min(wanted, hardSlideGive);
        }

        // Let go from a full pull on a malfunctioned gun: the malfunction is cleared and the bolt goes home on the next round (Tarkov's repair
        // chambers it) - or, the magazine empty, the follower catches it back, as any rack on an empty magazine does (BoltComesBack's rule,
        // which the malfunction's full pull skipped): no round, no slam
        private static void RackClearsMalfunction(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            bool repaired = RepairByHand(firearmController);
            bool caughtBack = repaired && BoltStaysBack(weapon) && !firearmController.FirearmsAnimator.GetBoltCatch();
            if (caughtBack)
            {
                firearmController.FirearmsAnimator.SetBoltCatch(true);
                if (debugLog)
                    LogAction("[VRReload] malfunction cleared on an empty magazine: the bolt locks back");
            }
            else
                PlayFirstGunSound(firearmController, SoundHand.GunBody, "BoltIn", "SlideIn");
            PlayGunSound(firearmController, "HandleIn", SoundHand.GunBody);
            Pulse(SoundHand.GunBody, caughtBack ? 0.4f : 0.8f, 0.06f);
        }

        // The stuck case taken out by hand: that clears it, but nothing cycled the bolt. Tarkov's repair chambered the next round anyway; it
        // stays in the magazine as far as the hands go until a full rack (WaitingForRack: the trigger only clicks), like a magazine put in on
        // a closed, empty chamber ("you should still have to pull the charging handle to chamber another round")
        private static void NextRoundWaitsForRack(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            if (weapon == null || weapon.ChamberAmmoCount == 0)
                return;
            weaponsWaitingForRack.Add(weapon);
            LogAction($"[VRReload] {weapon.LocalizedShortName()}: stuck case out by hand, the next round waits for the handle");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The stuck case of a failure to eject, at the ejection port

        private static bool TryGetJamPort(FirearmController firearmController, out Vector3 port)
        {
            port = Vector3.zero;
            System.Collections.Generic.List<Transform> ports = firearmController.Firearms != null ? firearmController.Firearms._shellPortsTransforms : null;
            if (ports == null || ports.Count == 0 || ports[0] == null)
                return false;
            port = ports[0].position;
            return true;
        }

        // A hand at the port of a gun with a case stuck in it, the trigger taking it like the charging handle: the off hand while the gun
        // hand has the grip (not two-handing: it's on the foregrip), the gun hand once it has let go with the off hand holding the gun up.
        // Tarkov's own clips do both (the M4's and AK's gun hand, the Glock's support hand).
        private static bool InJamPortZone(FirearmController firearmController, Weapon weapon, bool gunHand)
        {
            bool handMayTakeIt = gunHand ? ChestHang.SupportOnly : !VRGlobals.vrPlayer.isSupporting && !ChestHang.Detached;
            if (!handMayTakeIt || weapon.MalfState.State != Weapon.EMalfunctionState.Jam || !MalfunctionClearsByHand(firearmController)
                || !TryGetJamPort(firearmController, out Vector3 port))
                return false;
            bool atPort = HandTouchesCapsule(gunHand, port, port, jamPortRadius);
            SetZoneViz(ref jamPortZoneViz, debugZones, port, jamPortRadius, atPort ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.9f, 0f, 0.3f));
            return atPort;
        }

        // No case model in the port (shells not shown): cleared on the spot
        private static void ClearJamAtPort(FirearmController firearmController, bool gunHand)
        {
            if (debugLog)
                LogAction($"[VRReload] the stuck case taken out at the ejection port ({(gunHand ? "gun" : "off")} hand), no case model to hold");
            SoundHand hand = gunHand ? SoundHand.GunHand : SoundHand.OffHand;
            PlayFirstGunSound(firearmController, hand, "ShellRemove", "ShellPull", "ShellGrab");
            Pulse(hand, 0.45f, 0.04f);
            if (RepairByHand(firearmController))
                NextRoundWaitsForRack(firearmController);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The stuck case in the fingers ("when I press trigger on the bullet, my fingers lock onto the bullet while it's stuck ... I can wiggle
        // it a bit ... you need to give it some pull before it actually comes out"): Tarkov's own case model, the one in the port
        // (Firearms._shellsInShellPort). The trigger locks the hand on it in the single-round hold (the Mosin clip's: HoldInPalm + RoundFingers)
        // - the hand latched to the case, like the charging handle. Stuck, it stays in the port: it tips with the hand's turn (up to
        // jamCaseWiggleDegrees, about its end inside the gun) and gives a few millimetres toward the pull, a strain building in that hand.
        // Pulled jamCaseBreakDistance it comes out with a knock: cleared, the case in the fingers, the hand back on the controller. The
        // trigger let go: thrown with the hand's motion (Tarkov's spent-shell physics and bounce sounds), or left in the port if still stuck.

        private static SoundHand JamCaseHand => jamCaseInGunHand ? SoundHand.GunHand : SoundHand.OffHand;

        private static Vector3 HandWorldVelocity(bool gunHand)
        {
            if (!gunHand)
                return OffHandWorldVelocity();
            Vector3 trackingVelocity = ControllerVelocity.GetSteamVRVelocity(Handedness.GunHandSource);
            Vector3 handVelocity = VRGlobals.vrOffsetter != null ? VRGlobals.vrOffsetter.transform.TransformDirection(trackingVelocity) : trackingVelocity;
            return handVelocity + VRGlobals.player.Velocity;
        }

        private static void TakeJamCase(FirearmController firearmController, bool gunHand)
        {
            Firearms firearms = firearmController.Firearms;
            AmmoPoolObject stuckCase = firearms != null && firearms._shellsInShellPort != null && firearms._shellsInShellPort.Length > 0
                ? firearms._shellsInShellPort[0] : null;
            Transform port = firearms != null && firearms._shellPortsTransforms.Count > 0 ? firearms._shellPortsTransforms[0] : null;
            if (stuckCase == null || port == null)
            {
                ClearJamAtPort(firearmController, gunHand);
                return;
            }
            // Out of the port's slot: Tarkov's repair throws whatever is there (SpawnShellAfterJam), and it's the hand's now. It stays on the
            // port bone while stuck (in the gun, where Tarkov's jam pose put it).
            firearms._shellsInShellPort[0] = null;
            if (stuckCase.transform.parent != port)
                stuckCase.transform.SetParent(port, worldPositionStays: true);
            jamCase = stuckCase;
            jamCasePort = port;
            jamCaseRestLocalPosition = stuckCase.transform.localPosition;
            jamCaseRestLocalRotation = stuckCase.transform.localRotation;
            jamCaseFor = firearmController;
            jamCaseInGunHand = gunHand;
            jamCaseOnRightArm = OnRightArm(gunHand);
            jamCaseOut = false;
            jamCaseWontClear = false;
            MeasureLongAxis(stuckCase.gameObject, out jamCaseAxisInProp, out jamCaseCenterInProp, out float caseLength);
            jamCasePivotInCase = CaseEndInGun(firearmController, stuckCase.transform, caseLength);
            MeasureShownCase(stuckCase.gameObject);
            HoldingJamCase = true;

            // The hand locked on it ("It should keep its current hand position but while holding the bullet, the fingers lock to the bullet
            // and that point becomes the pivot for the wrist"): turned as the controller is, placed so the point its fingers close on (the
            // round hold's round centre, HoldInPalm) sits on the case where the hand reached it. The latch = the arm's IK target = the
            // palm bone's pose, as the magazine's SnapHandToGrip takes it; it rides the case (PullStuckCase).
            LatchHand(stuckCase.transform, gunHand);
            Transform weaponRoot = firearmController.WeaponRoot;
            Transform controller = LatchedHandRig;
            jamGripPointInPalm = HoldInPalm(jamCaseOnRightArm, stuckCase.transform, jamCaseAxisInProp, jamCaseCenterInProp, out Vector3 holdPosition, out Quaternion holdRotation)
                ? holdPosition + holdRotation * Vector3.Scale(jamCaseCenterInProp, stuckCase.transform.lossyScale) : Vector3.zero;
            jamCaseGrabInCase = CasePointNearest(stuckCase.transform, caseLength, controller.position + controller.rotation * jamGripPointInPalm);
            jamHandSnapStartTime = Time.time;
            controllerAtJamGrabInGun = weaponRoot.InverseTransformPoint(controller.position);
            controllerRotationAtJamGrabInGun = Quaternion.Inverse(weaponRoot.rotation) * controller.rotation;
            if (debugLog)
                LogAction($"[VRReload] took the stuck case ({(gunHand ? "gun" : "off")} hand), model length {caseLength * 100f:0.0} cm, case shown {jamCaseShownHalfLength * 200f:0.0} cm long, {jamCaseRadius * 2000f:0.0} mm across");
            PlayFirstGunSound(firearmController, JamCaseHand, "ShellGrab");
            Pulse(JamCaseHand, 0.3f, 0.03f);
        }

        // The case's end inside the gun, in its own frame: of its two ends, the one nearer the chamber (it pivots on that one)
        private static Vector3 CaseEndInGun(FirearmController firearmController, Transform caseTransform, float caseLength)
        {
            Vector3 center = caseTransform.TransformPoint(jamCaseCenterInProp);
            Vector3 axis = caseTransform.TransformDirection(jamCaseAxisInProp).normalized * (caseLength * 0.5f);
            Transform[] chamberBones = firearmController.Firearms._patronInWeaponTransforms;
            Vector3 chamber = chamberBones != null && chamberBones.Length > 0 && chamberBones[0] != null ? chamberBones[0].position : firearmController.WeaponRoot.position;
            Vector3 end = (center + axis - chamber).sqrMagnitude < (center - axis - chamber).sqrMagnitude ? center + axis : center - axis;
            return caseTransform.InverseTransformPoint(end);
        }

        // The case as it's shown: the biggest of its active meshes (a spent case model also carries the full round's, hidden), its longest
        // side the length, the other two averaged across. No active mesh: the whole model's measure.
        private static void MeasureShownCase(GameObject prop)
        {
            MeshFilter biggest = null;
            float biggestVolume = 0f;
            foreach (MeshFilter meshFilter in prop.GetComponentsInChildren<MeshFilter>(false))
            {
                if (meshFilter.sharedMesh == null)
                    continue;
                Vector3 size = meshFilter.sharedMesh.bounds.size;
                float volume = size.x * size.y * size.z;
                if (biggest == null || volume > biggestVolume)
                {
                    biggest = meshFilter;
                    biggestVolume = volume;
                }
            }
            if (biggest == null)
            {
                MeasureLongAxis(prop, out jamCaseShownAxis, out jamCaseShownCenter, out float length);
                jamCaseShownHalfLength = length * 0.5f;
                jamCaseRadius = 0.005f;
                return;
            }
            Bounds bounds = biggest.sharedMesh.bounds;
            Vector3 extent = Vector3.Scale(bounds.size, biggest.transform.lossyScale);
            Vector3 axisLocal = extent.x >= extent.y && extent.x >= extent.z ? Vector3.right : extent.y >= extent.z ? Vector3.up : Vector3.forward;
            float longest = Vector3.Dot(extent, axisLocal);
            jamCaseShownAxis = prop.transform.InverseTransformDirection(biggest.transform.TransformDirection(axisLocal)).normalized;
            jamCaseShownCenter = prop.transform.InverseTransformPoint(biggest.transform.TransformPoint(bounds.center));
            jamCaseShownHalfLength = longest * 0.5f;
            jamCaseRadius = (extent.x + extent.y + extent.z - longest) * 0.25f;
        }

        // The thumb's tip onto the case: after the hand and its fingers are posed (the round hold), the thumb's three joints turned, tip first
        // (cyclic coordinate descent), until the tip is on the case's surface (jamCaseThumbPad off it) at the point nearest it, each joint
        // kept within jamCaseThumbMaxBend of the hold's pose. Each joint turns only about an axis across the line from it to the tip, so
        // never about the finger's own length. The tip: 2.5 cm out along the last joint (the rig has no tip bone; TryGetPinch's point).
        private static void ThumbOntoCase(bool rightArm)
        {
            if (!jamCaseThumbReach || !HoldingJamCase || jamCase == null)
                return;
            Transform palm = ArmPalm(rightArm);
            if (palm == null && !rightArm)
                palm = TarkovVR.Patches.Core.VR.InitVRPatches.leftPalm;
            if (palm == null)
                return;
            if (jamThumbPalm != palm || jamThumbJoints == null)
            {
                jamThumbPalm = palm;
                Transform[] bones = FingerBonesInPoseOrder(palm);
                // Pose order: the palm, then the thumb's three joints
                jamThumbJoints = bones != null && bones.Length >= 4 && bones[1] != null && bones[2] != null && bones[3] != null
                    ? new[] { bones[1], bones[2], bones[3] } : null;
            }
            if (jamThumbJoints == null)
                return;
            Transform caseTransform = jamCase.transform;
            Vector3 center = caseTransform.TransformPoint(jamCaseShownCenter);
            Vector3 axis = caseTransform.TransformDirection(jamCaseShownAxis).normalized;
            for (int joint = 0; joint < 3; joint++)
                jamThumbPosed[joint] = jamThumbJoints[joint].localRotation;
            Transform tipJoint = jamThumbJoints[2];
            for (int iteration = 0; iteration < 6; iteration++)
            {
                Vector3 tip = tipJoint.TransformPoint(-0.025f, 0f, 0f);
                Vector3 onAxis = center + axis * Mathf.Clamp(Vector3.Dot(tip - center, axis), -jamCaseShownHalfLength, jamCaseShownHalfLength);
                Vector3 outward = tip - onAxis;
                if (outward.sqrMagnitude < 1e-10f)
                    outward = palm.position - onAxis;
                Vector3 target = onAxis + outward.normalized * (jamCaseRadius + jamCaseThumbPad);
                if ((tip - target).sqrMagnitude < 0.0015f * 0.0015f)
                    break;
                for (int joint = 2; joint >= 0; joint--)
                {
                    Transform bone = jamThumbJoints[joint];
                    tip = tipJoint.TransformPoint(-0.025f, 0f, 0f);
                    bone.rotation = Quaternion.FromToRotation(tip - bone.position, target - bone.position) * bone.rotation;
                    bone.localRotation = Quaternion.RotateTowards(jamThumbPosed[joint], bone.localRotation, jamCaseThumbMaxBend);
                }
            }
        }

        // The point on the case's length nearest a world point (kept off its very ends), in its own frame
        private static Vector3 CasePointNearest(Transform caseTransform, float caseLength, Vector3 point)
        {
            Vector3 center = caseTransform.TransformPoint(jamCaseCenterInProp);
            Vector3 axis = caseTransform.TransformDirection(jamCaseAxisInProp).normalized;
            float along = Mathf.Clamp(Vector3.Dot(point - center, axis), -0.4f * caseLength, 0.4f * caseLength);
            return caseTransform.InverseTransformPoint(center + axis * along);
        }

        // Stuck: back at its rest in the port, then tipped about its inner end by the wiggle and given toward the pull (both in the gun's frame)
        private static void PoseStuckCase(Transform weaponRoot, Quaternion wiggleInGun, Vector3 giveInGun)
        {
            Transform caseTransform = jamCase.transform;
            caseTransform.localPosition = jamCaseRestLocalPosition;
            caseTransform.localRotation = jamCaseRestLocalRotation;
            Vector3 pivot = caseTransform.TransformPoint(jamCasePivotInCase);
            Quaternion wiggle = weaponRoot.rotation * wiggleInGun * Quaternion.Inverse(weaponRoot.rotation);
            caseTransform.SetPositionAndRotation(pivot + wiggle * (caseTransform.position - pivot) + weaponRoot.TransformVector(giveInGun),
                wiggle * caseTransform.rotation);
        }

        // From Tick, with that hand's trigger
        private static void UpdateJamCase(bool offTriggerReleased, bool gunTriggerReleased)
        {
            if (!HoldingJamCase)
                return;
            if (!JamCaseHoldIsStillValid())
            {
                // Cleared some other way meanwhile (a rack): it's out
                LetGoOfJamCase(throwIt: jamCaseOut || jamCaseFor == null || jamCaseFor.Item == null || jamCaseFor.Item.MalfState.State != Weapon.EMalfunctionState.Jam);
                return;
            }
            if (!jamCaseOut)
                PullStuckCase();
            if (jamCaseInGunHand ? gunTriggerReleased : offTriggerReleased)
                LetGoOfJamCase(throwIt: jamCaseOut);
        }

        // The controller's pull and turn since the grab, in the gun's frame: the stuck case tips and gives with them, a strain building
        // in that hand, and comes out at jamCaseBreakDistance
        private static void PullStuckCase()
        {
            Transform weaponRoot = jamCaseFor.WeaponRoot;
            Transform controller = LatchedHandRig;
            Vector3 pull = weaponRoot.InverseTransformPoint(controller.position) - controllerAtJamGrabInGun;
            float fraction = jamCaseBreakDistance > 0f ? Mathf.Clamp01(pull.magnitude / jamCaseBreakDistance) : 1f;
            Quaternion turn = Quaternion.Inverse(weaponRoot.rotation) * controller.rotation * Quaternion.Inverse(controllerRotationAtJamGrabInGun);
            Quaternion wiggle = Quaternion.RotateTowards(Quaternion.identity, turn, jamCaseWiggleDegrees);
            Vector3 give = pull.sqrMagnitude > 1e-8f ? pull.normalized * (jamCaseGive * fraction) : Vector3.zero;
            PoseStuckCase(weaponRoot, wiggle, give);

            // The hand: the controller's turn, its fingers' point on the case (the wrist pivots there), eased onto it over handSnapTime
            if (handLatched && handLatch != null)
            {
                Quaternion handRotation = controller.rotation;
                Vector3 handPosition = jamCase.transform.TransformPoint(jamCaseGrabInCase) - handRotation * jamGripPointInPalm;
                float snap = handSnapTime > 0f ? Mathf.Clamp01((Time.time - jamHandSnapStartTime) / handSnapTime) : 1f;
                snap = snap * snap * (3f - 2f * snap);
                handLatch.SetPositionAndRotation(Vector3.Lerp(controller.position, handPosition, snap), handRotation);
            }

            if (jamCaseStrain > 0f && pull.magnitude > 0.005f && haptics)
            {
                SteamVR_Input_Sources source = jamCaseInGunHand ? Handedness.GunHandSource : Handedness.OffHandSource;
                HapticMixer.Stream(source, jamCaseStrain * (0.06f + 0.45f * fraction * fraction), 60f + 70f * fraction, 0.05f);
            }

            if (fraction < 1f || jamCaseWontClear)
                return;
            // Out, with a knock through the gun
            PlayFirstGunSound(jamCaseFor, JamCaseHand, "ShellPull", "ShellRemove");
            Pulse(JamCaseHand.Seating, 0.7f, 0.05f);
            jamCaseOut = RepairByHand(jamCaseFor);
            if (!jamCaseOut)
            {
                jamCaseWontClear = true;
                return;
            }
            NextRoundWaitsForRack(jamCaseFor);
            if (debugLog)
                LogAction("[VRReload] the stuck case pulled out");
            // Into the fingers as it came out (relative to the hand, whatever way the hand was turned), off the gun (which rides the
            // controllers' rig, moving after the arms are posed); the hand eases back to the controller with it
            if (handLatched && handLatch != null)
            {
                Quaternion toHand = Quaternion.Inverse(handLatch.rotation);
                jamCaseInHandPosition = toHand * (jamCase.transform.position - handLatch.position);
                jamCaseInHandRotation = toHand * jamCase.transform.rotation;
            }
            else
            {
                jamCaseInHandPosition = jamGripPointInPalm;
                jamCaseInHandRotation = Quaternion.identity;
            }
            ReleaseHandLatch();
            jamCase.transform.SetParent(null, worldPositionStays: true);
        }

        private static bool JamCaseHoldIsStillValid()
        {
            if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || jamCase == null || jamCaseFor == null)
                return false;
            if (VRGlobals.firearmController != jamCaseFor || jamCaseFor.WeaponRoot == null)
                return false;
            // The gun hand only while the off hand holds the gun up; the off hand only while the gun hand has the grip
            if (jamCaseInGunHand ? !ChestHang.SupportOnly : ChestHang.Detached)
                return false;
            // The gun went from one hand to the other: that arm isn't this hand's any more
            if (jamCaseOnRightArm != OnRightArm(jamCaseInGunHand))
                return false;
            return jamCaseOut || jamCaseFor.Item.MalfState.State == Weapon.EMalfunctionState.Jam;
        }

        private static void LetGoOfJamCase(bool throwIt)
        {
            AmmoPoolObject heldCase = jamCase;
            FirearmController firearmController = jamCaseFor;
            bool gunHand = jamCaseInGunHand;
            Transform port = jamCasePort;
            HoldingJamCase = false;
            jamCase = null;
            jamCaseFor = null;
            jamCasePort = null;
            // The latch rides the case: off it first, whatever happens to the case
            ReleaseHandLatch();
            if (heldCase == null)
                return;
            Firearms firearms = firearmController != null ? firearmController.Firearms : null;
            if (!throwIt && port != null && firearms != null && firearms._shellsInShellPort != null && firearms._shellsInShellPort.Length > 0
                && firearms._shellsInShellPort[0] == null)
            {
                // Still stuck: left in the port as it was, where Tarkov's repair finds it
                firearms._shellsInShellPort[0] = heldCase;
                if (heldCase.transform.parent != port)
                    heldCase.transform.SetParent(port, worldPositionStays: false);
                heldCase.transform.localPosition = jamCaseRestLocalPosition;
                heldCase.transform.localRotation = jamCaseRestLocalRotation;
                if (debugLog)
                    LogAction("[VRReload] let go of the stuck case before it came out: still in the port");
                return;
            }
            ThrowJamCase(heldCase, firearms, HandWorldVelocity(gunHand));
        }

        // Tarkov's own spent shell from here on (SpawnShellAfterJam's steps): in the world's shells, its physics, its bounce sounds, gone after 3 s
        private static void ThrowJamCase(AmmoPoolObject heldCase, Firearms firearms, Vector3 velocity)
        {
            try
            {
                AmmoPoolObject shell = heldCase;
                GameWorld gameWorld = Comfort.Common.Singleton<GameWorld>.Instance;
                AmmoPoolObject thrown = gameWorld != null ? gameWorld.SpawnShellInTheWorld(ref shell) : null;
                if (thrown == null)
                {
                    EFT.AssetsManager.AssetPoolObject.ReturnToPool(heldCase.gameObject);
                    return;
                }
                thrown.transform.parent = null;
                Vector3 spin = Random.insideUnitSphere * 6f;
                if (firearms != null)
                    firearms.UseGameObjectForShellExtraction(Vector3.zero, spin, thrown, velocity);
                else
                    thrown.EnablePhysics(Vector3.zero, spin, velocity, Vector3.forward);
                thrown.SetUsed(isUsed: true);
                thrown.StartAutoDestroyCountDown(3f);
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogWarning($"[VRReload] throwing the stuck case failed, back to Tarkov's pool: {exception.Message}");
                EFT.AssetsManager.AssetPoolObject.ReturnToPool(heldCase.gameObject);
            }
        }

        // Out: after the holding hand's fingers are posed, like a held round (ApplyHeldFingerPose / ApplyGunHandFingerPose). Stuck, the
        // case is posed in the port (PullStuckCase) and the hand rides it.
        private static void PlaceJamCase()
        {
            if (!HoldingJamCase || !jamCaseOut || jamCase == null)
                return;
            Transform palm = ArmPalm(jamCaseOnRightArm);
            if (palm == null && !jamCaseOnRightArm)
                palm = TarkovVR.Patches.Core.VR.InitVRPatches.leftPalm;
            if (palm == null)
                return;
            jamCase.transform.SetPositionAndRotation(palm.position + palm.rotation * jamCaseInHandPosition, palm.rotation * jamCaseInHandRotation);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // From Tick

        // A bolt-action's slide jam starts with a clip of the gun hand going to the bolt and bumping it, the bolt not moving (mosin_to_jam_slide
        // 2.0 s, sv98_idle_to_jam_slide 1.8 s: its R-hand marker travels 10-30 cm) - here the hand does that itself ("the hardSlide jam for a
        // bolt action causes an animation to play where the hands bump on the bolt a bit"). Skipped the frame after it starts, BSG's own skip
        // (StartMalfunction.FastForward = its end: the malfunction pose on, idle). The other guns' and types' start clips play as they are.
        public static bool skipBoltActionSlideJamClip = true;

        private static void SkipBoltActionSlideJamClip()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (!skipBoltActionSlideJamClip || firearmController == null || !(firearmController.CurrentOperation is FirearmController.StartMalfunction))
                return;
            Weapon weapon = firearmController.Item;
            Weapon.EMalfunctionState malfunction = weapon.MalfState.State;
            if (!weapon.BoltAction || (malfunction != Weapon.EMalfunctionState.SoftSlide && malfunction != Weapon.EMalfunctionState.HardSlide)
                || !MalfunctionClearsByHand(firearmController))
                return;
            try
            {
                firearmController.FastForwardCurrentState();
                FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
                firearmsAnimator?.Animator?.Play(firearmsAnimator.FullIdleStateName, 1, 0.1f);
                if (debugLog)
                    LogAction($"[VRReload] {malfunction} on a bolt-action: its start clip skipped (the hand works the bolt)");
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogWarning($"[VRReload] skipping the {malfunction} start clip failed: {exception.Message}");
            }
        }

        // Tarkov's notice and examine put the player on the gun's "knows about it" lists twice: at once, and again through inventory
        // operations (ExamineMalfunctionOperation / ExamineMalfTypeOperation.ExecuteInternal: AddPlayerWho...) that land after our same-frame
        // repair has cleared them (MalfunctionState.Repair). Left there, the next malfunction counts as already known: no jingle
        // (PlayerOwnerInventoryController.ExamineMalfunction only plays MalfunctionExamined for an unknown one) and no glow ("the malfunction
        // audio cue only seems to play on the first malfunction"). Nobody knows about a malfunction that isn't there: cleared, as Repair does.
        private static void ForgetClearedMalfunction()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            Weapon weapon = firearmController != null ? firearmController.Item : null;
            if (weapon == null || weapon.MalfState.State != Weapon.EMalfunctionState.None)
                return;
            if (!AnyIn(weapon.MalfState.PlayersWhoKnowAboutMalfunction) && !AnyIn(weapon.MalfState.PlayersWhoKnowMalfType))
                return;
            weapon.MalfState.ClearPlayersWhoKnow();
            if (debugLog)
                LogAction($"[VRReload] {weapon.LocalizedShortName()}: who-knows lists of a cleared malfunction emptied");
        }

        private static bool AnyIn(System.Collections.Generic.IEnumerable<string> items)
        {
            if (items == null)
                return false;
            foreach (string _ in items)
                return true;
            return false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The test switch (MalfunctionTestPatches: FirearmController.GetMalfunctionState, Tarkov's roll for every shot, semi and auto)

        // The type testMalfunctionNextShot asks for, if Tarkov's own roll could give it on this shot (FirearmController
        // .GetSpecificMalfunctionVariants' conditions); used once, whatever the answer
        public static bool ForcedMalfunction(Weapon weapon, bool hasAmmoInMagazine, bool hasBoltCatch, bool magazineInserted, out Weapon.EMalfunctionState malfunction)
        {
            malfunction = testMalfunctionNextShot;
            if (malfunction == Weapon.EMalfunctionState.None || weapon == null)
                return false;
            testMalfunctionNextShot = Weapon.EMalfunctionState.None;
            string refused = null;
            if (!weapon.AllowMalfunction)
                refused = "this gun has no malfunctions";
            else if (malfunction == Weapon.EMalfunctionState.Misfire && !weapon.AllowMisfire)
                refused = "this gun doesn't misfire";
            else if (malfunction == Weapon.EMalfunctionState.Jam && !weapon.AllowJam)
                refused = "this gun doesn't fail to eject";
            else if (malfunction == Weapon.EMalfunctionState.Jam && !(hasAmmoInMagazine || !hasBoltCatch || !magazineInserted))
                refused = "a failure to eject needs a round left in the magazine (the bolt locks back on the last one)";
            else if (malfunction == Weapon.EMalfunctionState.Feed && !weapon.AllowFeed)
                refused = "this gun doesn't fail to feed";
            else if (malfunction == Weapon.EMalfunctionState.Feed && !(hasAmmoInMagazine && magazineInserted))
                refused = "a failure to feed needs a magazine in with a round left";
            else if ((malfunction == Weapon.EMalfunctionState.SoftSlide || malfunction == Weapon.EMalfunctionState.HardSlide) && !weapon.AllowSlide)
                refused = "this gun doesn't get slide malfunctions";
            else if ((malfunction == Weapon.EMalfunctionState.SoftSlide || malfunction == Weapon.EMalfunctionState.HardSlide) && !hasAmmoInMagazine)
                refused = "a slide malfunction needs a round left in the magazine";
            if (refused != null)
            {
                Plugin.MyLog.LogWarning($"[VRReload] test malfunction {malfunction} not forced on {weapon.LocalizedShortName()}: {refused}");
                return false;
            }
            Plugin.MyLog.LogInfo($"[VRReload] test malfunction: {malfunction} forced on this shot of {weapon.LocalizedShortName()}");
            return true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Tarkov's repair

        // As the menu's Fix Malfunction runs it, skipped to its end. The type has to be known first: noticed (Check Chamber on an
        // unknown misfire or slide is Tarkov's "something's wrong", no operation), then examined (its examine operation, skipped).
        private static bool RepairByHand(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            Weapon.EMalfunctionState malfunction = weapon.MalfState.State;
            if (malfunction == Weapon.EMalfunctionState.None || VRGlobals.player == null)
                return false;
            string profileId = VRGlobals.player.ProfileId;
            try
            {
                if (!(firearmController.CurrentOperation is FirearmController.Idling))
                {
                    Plugin.MyLog.LogWarning($"[VRReload] {malfunction} not cleared: the gun is busy ({firearmController.CurrentOperation?.GetType().Name ?? "none"})");
                    return false;
                }
                // Check Chamber and the repair refuse with the trigger held
                if (firearmController.IsTriggerPressed)
                    firearmController.SetTriggerPressed(false);
                if (!weapon.MalfState.IsKnownMalfType(profileId))
                {
                    bool noticedOnlyByChecking = malfunction == Weapon.EMalfunctionState.Misfire || malfunction == Weapon.EMalfunctionState.SoftSlide
                        || malfunction == Weapon.EMalfunctionState.HardSlide;
                    if (noticedOnlyByChecking && !weapon.MalfState.IsKnownMalfunction(profileId))
                        firearmController.CheckChamber();
                    if (firearmController.ExamineWeapon() && firearmController.CurrentOperation is FirearmController.ExamineMalfunctionTypeOperation)
                        firearmController.FastForwardCurrentState();
                }
                if (!weapon.MalfState.IsKnownMalfType(profileId) || !(firearmController.CurrentOperation is FirearmController.Idling))
                {
                    Plugin.MyLog.LogWarning($"[VRReload] {malfunction} not cleared: its type isn't known (operation {firearmController.CurrentOperation?.GetType().Name ?? "none"})");
                    return false;
                }
                if (!firearmController.CheckChamber() || !(firearmController.CurrentOperation is FirearmController.RepairMalfunction))
                {
                    Plugin.MyLog.LogWarning($"[VRReload] {malfunction} not cleared: Tarkov's repair didn't start (operation {firearmController.CurrentOperation?.GetType().Name ?? "none"})");
                    return false;
                }
                // BSG's own skip: the dud / stuck round out, the next one chambered, then idle. Its animation would play on otherwise.
                firearmController.FastForwardCurrentState();
                FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
                if (firearmsAnimator?.Animator != null)
                {
                    firearmsAnimator.Animator.Play(firearmsAnimator.FullIdleStateName, 1, 0.1f);
                    // The examine's "look" trigger (Idling.ExamineWeapon sets it) would play the inspect after the skip
                    AnimationControllerParametersTable.ResetTriggerLook(firearmsAnimator.Animator);
                }
                // ReloadFloat and the "reload in progress" block only clear on Idling's IdleStart event, which the skipped animation never sends
                if (firearmController.CurrentOperation is FirearmController.Idling idling)
                    idling.OnIdleStartEvent();
                malfunctionPoseOffFor = null;
                LogAction($"[VRReload] {malfunction} cleared by hand, chamber {weapon.ChamberAmmoCount}, malfunction now {weapon.MalfState.State}");
                return true;
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[VRReload] clearing the {malfunction} failed: {exception}");
                return false;
            }
        }
    }
}
