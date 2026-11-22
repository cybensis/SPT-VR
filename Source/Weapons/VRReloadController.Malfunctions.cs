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
    // Clearing a malfunction by hand (Manual Reload), per type, the way Tarkov's own clearing clips do it:
    //  - misfire, soft slide, failure to feed: pull the handle or slide all the way back and let go (on a bolt-action, cycle the bolt)
    //  - failure to eject: take the case stuck in the ejection port out with either hand's trigger there (a full rack also clears it)
    //  - hard slide: the handle (or bolt) stays stuck for the first hardSlideStuckTugs tugs and breaks free on the next; then a full
    //    rack clears it
    // The clearing itself is Tarkov's single repair for all five types (RepairMalfunction), started through Check Chamber and skipped
    // to its end. Tarkov requires the type to be examined first; doing the right fix by hand counts as examining it.
    // Co-op: these go out as FIKA's own CheckChamber / ExamineWeapon packets. The sync module finishes them on the other players' copies
    // (ReloadSyncApply.SkipMalfunctionOperation); otherwise those copies refuse the repair and stay jammed.
    public static partial class VRReloadController
    {
        private static bool malfunctionRacked;                  // this grab has been all the way back on a malfunctioned gun
        private static bool malfunctionRackBlocked;             // ... but the MDR's deflector was shut over the port, so nothing clears
        private static int hardSlideTugsDone;
        private static bool hardSlideTugging;
        private static bool hardSlideFree = true;
        private static FirearmController malfunctionPoseOffFor;
        public static bool HoldingJamCase { get; private set; }

        // The hands clear this malfunction: Manual Reload is on and the gun has a handle, slide or bolt the hands work
        internal static bool MalfunctionClearsByHand(FirearmController firearmController)
        {
            if (!enabled || firearmController == null || firearmController.Item == null
                || firearmController.Item.MalfState.State == Weapon.EMalfunctionState.None || !VRSettings.GetManualReloadOn())
                return false;
            Weapon weapon = firearmController.Item;
            if (weapon.BoltAction)
                return ManualBoltOn(firearmController);
            // A pump gun clears with a stroke of the pump (VRReloadController.Shotgun); a semi-auto shotgun uses its handle, below
            if (ManualShotgunOn(firearmController, out ShotgunData shotgun) && PumpMode(weapon, shotgun))
                return true;
            return (TryGetChargingHandle(firearmController, weapon) || TryGetSideKnobPoint(firearmController, out _));
        }

        // The handle, side handle and bolt can be taken when there's no malfunction, or one the hands clear
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
            malfunctionRackBlocked = false;
            hardSlideTugsDone = 0;
            hardSlideTugging = false;
            Weapon.EMalfunctionState malfunction = firearmController.Item.MalfState.State;
            hardSlideFree = malfunction != Weapon.EMalfunctionState.HardSlide;
            if (malfunction == Weapon.EMalfunctionState.None)
                return;
            if (debugLog)
                LogAction($"[VRReload] took the action of a gun with a {malfunction}");
            SetMalfunctionPose(firearmController, 0f);
            malfunctionPoseOffFor = firearmController;
        }

        // Let go without clearing it: the gun shows its malfunction again
        private static void EndMalfunctionGrab(FirearmController firearmController)
        {
            if (malfunctionPoseOffFor != null && malfunctionPoseOffFor == firearmController && firearmController.Item != null
                && firearmController.Item.MalfState.State != Weapon.EMalfunctionState.None)
                SetMalfunctionPose(firearmController, 1f);
            malfunctionPoseOffFor = null;
            malfunctionRacked = false;
            malfunctionRackBlocked = false;
        }

        private static void SetMalfunctionPose(FirearmController firearmController, float weight)
        {
            FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
            if (firearmsAnimator != null && firearmsAnimator.MALFUNCTION_LAYER_INDEX >= 0)
                firearmsAnimator.SetLayerWeight(firearmsAnimator.MALFUNCTION_LAYER_INDEX, weight);
        }

        // Pulled all the way back on a malfunctioned gun: the repair waits for the let-go (the bolt going home), and Tarkov's rechamber /
        // bolt catch rules don't run. True = handled here.
        private static bool MalfunctionRackedBack(FirearmController firearmController)
        {
            if (firearmController.Item.MalfState.State == Weapon.EMalfunctionState.None || !MalfunctionClearsByHand(firearmController))
                return false;
            // The MDR's deflector shut over a failure to eject or feed: the rack does nothing for it (VRReloadController.Deflector)
            if (DeflectorBlocksClearing(firearmController))
            {
                malfunctionRackBlocked = true;
                // The bolt knocks the case against the shut cover (the MDR's cover_hit sounds)
                PlayVariedGunSound(firearmController, SoundHand.GunBody, "CoverJam");
                if (debugLog)
                    LogAction($"[VRReload] racked back on a {firearmController.Item.MalfState.State} with the deflector shut: not cleared");
                return true;
            }
            malfunctionRacked = true;
            if (debugLog)
                LogAction($"[VRReload] racked back on a {firearmController.Item.MalfState.State}: cleared as it goes forward");
            return true;
        }

        // A hard slide: the hand pulls and the handle (or bolt) barely gives. Each pull past hardSlideTugDistance is a tug (a hard clunk
        // through the gun with its rattle); the tug after hardSlideStuckTugs breaks it free. wanted = m back the hand would pull it.
        private static float StuckPull(FirearmController firearmController, float wanted, SoundHand hand)
        {
            if (hardSlideFree)
                return wanted;
            if (wanted >= Malfunctions.hardSlideTugDistance && !hardSlideTugging)
            {
                hardSlideTugging = true;
                hardSlideTugsDone++;
                bool freed = hardSlideTugsDone > Malfunctions.hardSlideStuckTugs;
                // The gun's own sounds: its first-try sound (an AK's StuckBoltStart), its tug sounds in turn, and the one that frees it
                if (freed)
                {
                    if (!PlayVariedGunSound(firearmController, hand, STUCK_FREE_SOUNDS))
                        PlayVariedGunSound(firearmController, hand, STUCK_TUG_SOUNDS);
                }
                else if (!(hardSlideTugsDone == 1 && PlayFirstGunSound(firearmController, hand, "StuckBoltStart")))
                    PlayVariedGunSound(firearmController, hand, STUCK_TUG_SOUNDS);
                if (freed)
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
            else if (wanted <= Malfunctions.hardSlideTugReset)
            {
                hardSlideTugging = false;
            }
            return Mathf.Min(wanted, Malfunctions.hardSlideGive);
        }

        // Let go after a full pull on a malfunctioned gun: it's cleared, and the bolt goes home on the next round (Tarkov's repair chambers it).
        // On an empty magazine the follower holds the bolt back instead (BoltComesBack's rule, which the malfunction's full pull skipped).
        private static void RackClearsMalfunction(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            bool repaired = RepairByHand(firearmController);
            bool caughtBack = repaired && BoltStaysBack(weapon) && !firearmController.FirearmsAnimator.GetBoltCatch();
            if (caughtBack)
            {
                PlayVariedGunSound(firearmController, SoundHand.GunBody, CATCH_ON_SOUNDS);
                firearmController.FirearmsAnimator.SetBoltCatch(true);
                if (debugLog)
                    LogAction("[VRReload] malfunction cleared on an empty magazine: the bolt locks back");
            }
            else
                PlayFirstGunSound(firearmController, SoundHand.GunBody, "BoltIn", "SlideIn");
            PlayGunSound(firearmController, "HandleIn", SoundHand.GunBody);
            Pulse(SoundHand.GunBody, caughtBack ? 0.4f : 0.8f, 0.06f);
            BoltSlamJolt(firearmController, caughtBack ? handleSlamFraction * heldOnCatchJolt : handleSlamFraction, "malfunction racked clear");
        }

        // A hand at the port of a gun with a stuck case; its trigger takes the case, like the charging handle. The off hand can while the gun
        // hand holds the grip (not while two-handing: then it's on the foregrip); the gun hand can once it has let go and the off hand holds
        // the gun up. Tarkov's own clips do both (the M4's and AK's gun hand, the Glock's support hand).
        private static bool InJamPortZone(FirearmController firearmController, Weapon weapon, bool gunHand)
        {
            bool handMayTakeIt = gunHand ? ChestHang.SupportOnly : !VRGlobals.vrPlayer.isSupporting && !ChestHang.Detached;
            if (!handMayTakeIt || weapon.MalfState.State != Weapon.EMalfunctionState.Jam || !MalfunctionClearsByHand(firearmController)
                || DeflectorBlocksClearing(firearmController) || !Malfunctions.TryGetJamPort(firearmController, out Vector3 port))
                return false;
            bool atPort = HandTouchesCapsule(gunHand, port, port, Malfunctions.jamPortRadius);
            SetZoneViz(ref Malfunctions.jamPortZoneViz, debugZones, port, Malfunctions.jamPortRadius, atPort ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.9f, 0f, 0.3f));
            return atPort;
        }

        private static void TakeJamCase(FirearmController firearmController, bool gunHand)
        {
            Firearms firearms = firearmController.Firearms;
            AmmoPoolObject stuckCase = firearms != null && firearms._shellsInShellPort != null && firearms._shellsInShellPort.Length > 0
                ? firearms._shellsInShellPort[0] : null;
            Transform port = firearms != null && firearms._shellPortsTransforms.Count > 0 ? firearms._shellPortsTransforms[0] : null;
            if (stuckCase == null || port == null)
            {
                Malfunctions.ClearJamAtPort(firearmController, gunHand);
                return;
            }
            // Take it out of the port's slot, because Tarkov's repair throws whatever is there (SpawnShellAfterJam); the hand owns it now.
            // It stays on the port bone while stuck, where Tarkov's jam pose put it.
            firearms._shellsInShellPort[0] = null;
            if (stuckCase.transform.parent != port)
                stuckCase.transform.SetParent(port, worldPositionStays: true);
            Malfunctions.jamCase = stuckCase;
            Malfunctions.jamCasePort = port;
            Malfunctions.jamCaseRestLocalPosition = stuckCase.transform.localPosition;
            Malfunctions.jamCaseRestLocalRotation = stuckCase.transform.localRotation;
            Malfunctions.jamCaseFor = firearmController;
            Malfunctions.jamCaseInGunHand = gunHand;
            Malfunctions.jamCaseOnRightArm = OnRightArm(gunHand);
            Malfunctions.jamCaseOut = false;
            Malfunctions.jamCaseWontClear = false;
            MeasureLongAxis(stuckCase.gameObject, out Malfunctions.jamCaseAxisInProp, out Malfunctions.jamCaseCenterInProp, out float caseLength);
            Malfunctions.jamCasePivotInCase = Malfunctions.CaseEndInGun(firearmController, stuckCase.transform, caseLength);
            Malfunctions.MeasureShownCase(stuckCase.gameObject);
            HoldingJamCase = true;

            // Lock the hand onto the case: rotated like the controller, and placed so the point its fingers close on (the round hold's round
            // centre, HoldInPalm) sits on the case where the hand reached it. That point is the wrist's pivot. The latch is both the arm's IK
            // target and the palm bone's pose; it rides the case (PullStuckCase).
            LatchHand(stuckCase.transform, gunHand);
            Transform weaponRoot = firearmController.WeaponRoot;
            Transform controller = LatchedHandRig;
            Malfunctions.jamGripPointInPalm = HoldInPalm(Malfunctions.jamCaseOnRightArm, stuckCase.transform, Malfunctions.jamCaseAxisInProp, Malfunctions.jamCaseCenterInProp, out Vector3 holdPosition, out Quaternion holdRotation)
                ? holdPosition + holdRotation * Vector3.Scale(Malfunctions.jamCaseCenterInProp, stuckCase.transform.lossyScale) : Vector3.zero;
            Malfunctions.jamCaseGrabInCase = Malfunctions.CasePointNearest(stuckCase.transform, caseLength, controller.position + controller.rotation * Malfunctions.jamGripPointInPalm);
            Malfunctions.jamHandSnapStartTime = Time.time;
            Malfunctions.controllerAtJamGrabInGun = weaponRoot.InverseTransformPoint(controller.position);
            Malfunctions.controllerRotationAtJamGrabInGun = Quaternion.Inverse(weaponRoot.rotation) * controller.rotation;
            if (debugLog)
                LogAction($"[VRReload] took the stuck case ({(gunHand ? "gun" : "off")} hand), model length {caseLength * 100f:0.0} cm, case shown {Malfunctions.jamCaseShownHalfLength * 200f:0.0} cm long, {Malfunctions.jamCaseRadius * 2000f:0.0} mm across");
            PlayFirstGunSound(firearmController, Malfunctions.JamCaseHand, CASE_GRAB_SOUNDS);
            Pulse(Malfunctions.JamCaseHand, 0.3f, 0.03f);
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

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Tarkov's repair

        // Runs the repair as the menu's Fix Malfunction does, skipped to its end. The type must be known first: noticed (Check Chamber on
        // an unknown misfire or slide gives Tarkov's "something's wrong", with no operation), then examined (its examine operation, skipped).
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

        public static class Malfunctions
        {
            public static int hardSlideStuckTugs = 2;               // tugs that don't move it; it breaks free on the next
            public static float hardSlideTugDistance = 0.025f;      // m the hand pulls on the stuck handle or bolt for a tug
            public static float hardSlideTugReset = 0.01f;          // m it has to come back toward rest before the next one counts
            public static float hardSlideGive = 0.004f;             // m a stuck handle still gives
            public static float jamPortRadius = 0.05f;              // m from any point of the hand to the ejection port for its trigger to take a stuck case out
                                                                    // as its own repair does (it would hold the bolt where the jam left it)
            // For testing: your next shot fails with this malfunction type, then this resets to None. Only applied where Tarkov itself could
            // roll that type (ForcedMalfunction), so a gun never gets one it has no animations for.
            public static Weapon.EMalfunctionState testMalfunctionNextShot = Weapon.EMalfunctionState.None;
            internal static GameObject jamPortZoneViz;

            // The stuck case of a failure to eject, with the fingers locked on it
            public static float jamCaseBreakDistance = 0.05f;       // m the controller must pull away from where it took the case before the case comes out
            public static float jamCaseGive = 0.006f;               // m the stuck case gives toward the pull, all of it at the break
            public static float jamCaseWiggleDegrees = 15f;         // deg it tips with the hand's turn while stuck (about its end in the gun)
            public static float jamCaseStrain = 1f;                 // strength of the rising buzz in that hand while it pulls (0 = none)
            // The thumb on the case: every frame its tip is bent onto the case's surface, starting from the round hold's pose (ThumbOntoCase),
            // whatever the case's size and the hand's rotation. A fixed bend along the curl axis doesn't fit every case.
            public static float jamCaseThumbMaxBend = 45f;          // deg each thumb joint may turn off the hold's pose to get there
            public static float jamCaseThumbPad = 0.006f;           // m the tip point stays off the case's surface (the pad of the thumb)
            public static bool JamCaseInGunHand => HoldingJamCase && jamCaseInGunHand;
            internal static bool jamCaseInGunHand;
            internal static bool jamCaseOnRightArm;                  // the arm drawing that hand (OnRightArm), as it was at the grab
            internal static bool jamCaseOut;
            internal static bool jamCaseWontClear;                   // the pull broke it free but Tarkov's repair refused: no retry every frame
            internal static FirearmController jamCaseFor;
            internal static AmmoPoolObject jamCase;
            internal static Transform jamCasePort;
            internal static Vector3 jamCaseRestLocalPosition;        // the stuck pose, in the port bone's frame
            internal static Quaternion jamCaseRestLocalRotation;
            internal static Vector3 jamCasePivotInCase;              // its end inside the gun, in its own frame
            internal static Vector3 jamCaseAxisInProp, jamCaseCenterInProp;
            internal static Vector3 controllerAtJamGrabInGun;
            internal static Quaternion controllerRotationAtJamGrabInGun;
            internal static Vector3 jamGripPointInPalm;              // where the round hold's fingers close on a round, in the palm bone's frame
            internal static Vector3 jamCaseGrabInCase;               // where on the case they closed, in its own frame: the wrist's pivot while stuck
            internal static float jamHandSnapStartTime;
            private static Vector3 jamCaseInHandPosition;           // once out: the case's pose as it came out, in the palm bone's frame
            private static Quaternion jamCaseInHandRotation;
            // The visible case, for the thumb (its shown meshes only: the model also carries the hidden full round's): centre and length axis
            // in its own frame, half length and radius in metres
            private static Vector3 jamCaseShownCenter, jamCaseShownAxis;
            internal static float jamCaseShownHalfLength, jamCaseRadius;
            private static Transform jamThumbPalm;
            private static Transform[] jamThumbJoints;
            private static readonly Quaternion[] jamThumbPosed = new Quaternion[3];

            // The stuck case was taken out by hand: that clears it, but nothing cycled the bolt. Tarkov's repair chambered the next round anyway,
            // so for the hands it stays in the magazine until a full rack (WaitingForRack: the trigger only clicks), like a magazine inserted on
            // a closed, empty chamber
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

            internal static bool TryGetJamPort(FirearmController firearmController, out Vector3 port)
            {
                port = Vector3.zero;
                System.Collections.Generic.List<Transform> ports = firearmController.Firearms != null ? firearmController.Firearms._shellPortsTransforms : null;
                if (ports == null || ports.Count == 0 || ports[0] == null)
                    return false;
                port = ports[0].position;
                return true;
            }

            // No case model in the port (shells not shown): cleared on the spot
            internal static void ClearJamAtPort(FirearmController firearmController, bool gunHand)
            {
                if (debugLog)
                    LogAction($"[VRReload] the stuck case taken out at the ejection port ({(gunHand ? "gun" : "off")} hand), no case model to hold");
                SoundHand hand = gunHand ? SoundHand.GunHand : SoundHand.OffHand;
                PlayFirstGunSound(firearmController, hand, CASE_OUT_SOUNDS);
                Pulse(hand, 0.45f, 0.04f);
                if (RepairByHand(firearmController))
                    NextRoundWaitsForRack(firearmController);
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            // The stuck case in the fingers: Tarkov's own case model in the port (Firearms._shellsInShellPort). The trigger latches the hand
            // onto it in the single-round hold (HoldInPalm + RoundFingers), like the charging handle. While stuck, it stays in the port, tipping
            // with the hand's rotation and giving a little toward the pull. Pulled jamCaseBreakDistance, it comes out: the malfunction is cleared
            // and the case is in the fingers. Releasing the trigger throws it with the hand's motion (Tarkov's spent-shell physics), or leaves
            // it in the port if it's still stuck.

            internal static SoundHand JamCaseHand => jamCaseInGunHand ? SoundHand.GunHand : SoundHand.OffHand;

            // The case's end inside the gun, in its own frame: of its two ends, the one nearer the chamber (it pivots on that one)
            internal static Vector3 CaseEndInGun(FirearmController firearmController, Transform caseTransform, float caseLength)
            {
                Vector3 center = caseTransform.TransformPoint(jamCaseCenterInProp);
                Vector3 axis = caseTransform.TransformDirection(jamCaseAxisInProp).normalized * (caseLength * 0.5f);
                Transform[] chamberBones = firearmController.Firearms._patronInWeaponTransforms;
                Vector3 chamber = chamberBones != null && chamberBones.Length > 0 && chamberBones[0] != null ? chamberBones[0].position : firearmController.WeaponRoot.position;
                Vector3 end = (center + axis - chamber).sqrMagnitude < (center - axis - chamber).sqrMagnitude ? center + axis : center - axis;
                return caseTransform.InverseTransformPoint(end);
            }

            // Measures the case as shown: the biggest of its active meshes (a spent case model also carries the full round's, hidden). Its longest
            // side is the length; the other two are averaged for the width. With no active mesh, the whole model is measured.
            internal static void MeasureShownCase(GameObject prop)
            {
                MeshFilter biggest = BiggestMesh(prop, includeHidden: false);
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

            // Puts the thumb's tip onto the case, after the hand and fingers are posed (the round hold). The thumb's three joints are rotated, tip
            // first (cyclic coordinate descent), until the tip is jamCaseThumbPad off the case's surface at its nearest point, each joint within
            // jamCaseThumbMaxBend of the hold's pose. The tip is 2.5 cm out along the last joint (the rig has no tip bone; same point as TryGetPinch).
            internal static void ThumbOntoCase(bool rightArm)
            {
                if (!HoldingJamCase || jamCase == null)
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
            internal static Vector3 CasePointNearest(Transform caseTransform, float caseLength, Vector3 point)
            {
                Vector3 center = caseTransform.TransformPoint(jamCaseCenterInProp);
                Vector3 axis = caseTransform.TransformDirection(jamCaseAxisInProp).normalized;
                float along = Mathf.Clamp(Vector3.Dot(point - center, axis), -0.4f * caseLength, 0.4f * caseLength);
                return caseTransform.InverseTransformPoint(center + axis * along);
            }

            // Poses the stuck case: back at its rest pose in the port, then tipped about its inner end by the wiggle and moved by the give
            // toward the pull (both in the gun's frame)
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

            // Called from Tick with each hand's trigger release
            internal static void UpdateJamCase(bool offTriggerReleased, bool gunTriggerReleased)
            {
                if (!HoldingJamCase)
                    return;
                if (!JamCaseHoldIsStillValid())
                {
                    // Cleared some other way meanwhile (e.g. a rack): the case is out
                    LetGoOfJamCase(throwIt: jamCaseOut || jamCaseFor == null || jamCaseFor.Item == null || jamCaseFor.Item.MalfState.State != Weapon.EMalfunctionState.Jam);
                    return;
                }
                if (!jamCaseOut)
                    PullStuckCase();
                if (jamCaseInGunHand ? gunTriggerReleased : offTriggerReleased)
                    LetGoOfJamCase(throwIt: jamCaseOut);
            }

            // Applies the controller's pull and rotation since the grab, in the gun's frame: the stuck case tips and gives with them, a strain
            // buzz builds in that hand, and the case comes out at jamCaseBreakDistance
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

                // The hand takes the controller's rotation, with its fingers' point on the case (the wrist pivots there), eased on over handSnapTime
                if (handLatched && handLatch != null)
                {
                    Quaternion handRotation = controller.rotation;
                    Vector3 handPosition = jamCase.transform.TransformPoint(jamCaseGrabInCase) - handRotation * jamGripPointInPalm;
                    float snap = handSnapTime > 0f ? Mathf.Clamp01((Time.time - jamHandSnapStartTime) / handSnapTime) : 1f;
                    snap = snap * snap * (3f - 2f * snap);
                    handLatch.SetPositionAndRotation(Vector3.Lerp(controller.position, handPosition, snap), handRotation);
                }

                if (jamCaseStrain > 0f && pull.magnitude > 0.005f)
                {
                    SteamVR_Input_Sources source = jamCaseInGunHand ? Handedness.GunHandSource : Handedness.OffHandSource;
                    HapticMixer.Stream(source, jamCaseStrain * (0.06f + 0.45f * fraction * fraction), 60f + 70f * fraction, 0.05f);
                }

                if (fraction < 1f || jamCaseWontClear)
                    return;
                // Out, with a knock through the gun
                PlayFirstGunSound(jamCaseFor, JamCaseHand, CASE_OUT_SOUNDS);
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
                // The case stays in the fingers as it came out (relative to the hand, however the hand was rotated) and comes off the gun
                // (which rides the controllers' rig and moves after the arms are posed). The hand eases back to the controller with it.
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

            // Once out, the case is placed after the holding hand's fingers are posed, like a held round (ApplyHeldFingerPose /
            // ApplyGunHandFingerPose). While stuck, it is posed in the port instead (PullStuckCase) and the hand rides it.
            internal static void PlaceJamCase()
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

            // A bolt-action's slide jam starts with a clip of the gun hand going to the bolt and bumping it (mosin_to_jam_slide 2.0 s,
            // sv98_idle_to_jam_slide 1.8 s); here the player's hand does that. The clip is skipped the frame after it starts, with BSG's own
            // skip (StartMalfunction.FastForward jumps to its end: malfunction pose on, idle). Other guns' and types' start clips play normally.

            internal static void SkipBoltActionSlideJamClip()
            {
                FirearmController firearmController = VRGlobals.firearmController;
                if (firearmController == null || !(firearmController.CurrentOperation is FirearmController.StartMalfunction))
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

            // Tarkov's notice and examine add the player to the gun's "knows about it" lists twice: at once, and again through inventory
            // operations (ExamineMalfunctionOperation / ExamineMalfTypeOperation.ExecuteInternal: AddPlayerWho...) that land after our same-frame
            // repair has already cleared them (MalfunctionState.Repair). Left there, the next malfunction counts as already known: no jingle
            // (PlayerOwnerInventoryController.ExamineMalfunction only plays MalfunctionExamined for an unknown one) and no glow. So the lists
            // of a gun with no malfunction are cleared here, as Repair does.
            internal static void ForgetClearedMalfunction()
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

            // Returns the type testMalfunctionNextShot asks for, if Tarkov's own roll could give it on this shot (the conditions of
            // FirearmController.GetSpecificMalfunctionVariants). The request is used up either way.
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
        }
    }
}
