using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
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

        // The stuck case of a failure to eject in the fingers
        public static float jamCasePullOut = 0.03f;             // m the hand pulls it away from where it took it: out, cleared
        public static float jamCaseSnapTime = 0.08f;            // s it takes from the port into the fingers
        public static bool HoldingJamCase { get; private set; }
        public static bool JamCaseInGunHand => HoldingJamCase && jamCaseInGunHand;
        private static bool jamCaseInGunHand;
        private static bool jamCaseOnRightArm;                  // the arm drawing that hand (OnRightArm), as it was at the grab
        private static bool jamCaseOut;
        private static FirearmController jamCaseFor;
        private static AmmoPoolObject jamCase;
        private static Vector3 jamCaseAxisInProp, jamCaseCenterInProp;
        private static Vector3 handAtJamGrabInGun;
        private static float jamCaseGrabTime;
        private static Vector3 jamCaseStartPosition;
        private static Quaternion jamCaseStartRotation;

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

        // Let go from a full pull on a malfunctioned gun: the bolt slams home and the malfunction is cleared
        private static void RackClearsMalfunction(FirearmController firearmController)
        {
            PlayFirstGunSound(firearmController, SoundHand.GunBody, "BoltIn", "SlideIn");
            PlayGunSound(firearmController, "HandleIn", SoundHand.GunBody);
            Pulse(SoundHand.GunBody, 0.8f, 0.06f);
            RepairByHand(firearmController);
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
            RepairByHand(firearmController);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The case in the fingers ("pulling the bullet out doesn't seem to have your fingers actually grab the bullet"): Tarkov's own case
        // model, the one in the port (Firearms._shellsInShellPort), held like a single round (the Mosin clip's hold, HoldInPalm + RoundFingers).
        // Pulled jamCasePullOut away from where the hand took it, it's out: cleared. The trigger let go: thrown with the hand's motion
        // (Tarkov's spent-shell physics and bounce sounds), or back in the port if it wasn't out.

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
            if (stuckCase == null)
            {
                ClearJamAtPort(firearmController, gunHand);
                return;
            }
            // Out of the port's slot: Tarkov's repair throws whatever is there (SpawnShellAfterJam), and it's in the hand now
            firearms._shellsInShellPort[0] = null;
            // Off the gun too: the gun rides the controllers' rig, which moves after the arms are posed
            stuckCase.transform.SetParent(null, worldPositionStays: true);
            jamCase = stuckCase;
            jamCaseFor = firearmController;
            jamCaseInGunHand = gunHand;
            jamCaseOnRightArm = OnRightArm(gunHand);
            jamCaseOut = false;
            MeasureLongAxis(stuckCase.gameObject, out jamCaseAxisInProp, out jamCaseCenterInProp, out _);
            jamCaseStartPosition = stuckCase.transform.position;
            jamCaseStartRotation = stuckCase.transform.rotation;
            jamCaseGrabTime = Time.time;
            handAtJamGrabInGun = firearmController.WeaponRoot.InverseTransformPoint(HandPalm(gunHand));
            HoldingJamCase = true;
            if (debugLog)
                LogAction($"[VRReload] took the stuck case ({(gunHand ? "gun" : "off")} hand)");
            PlayFirstGunSound(firearmController, JamCaseHand, "ShellGrab");
            Pulse(JamCaseHand, 0.3f, 0.03f);
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
            {
                Vector3 handInGun = jamCaseFor.WeaponRoot.InverseTransformPoint(HandPalm(jamCaseInGunHand));
                if ((handInGun - handAtJamGrabInGun).magnitude >= jamCasePullOut)
                {
                    PlayFirstGunSound(jamCaseFor, JamCaseHand, "ShellPull", "ShellRemove");
                    Pulse(JamCaseHand, 0.45f, 0.04f);
                    jamCaseOut = RepairByHand(jamCaseFor);
                }
            }
            if (jamCaseInGunHand ? gunTriggerReleased : offTriggerReleased)
                LetGoOfJamCase(throwIt: jamCaseOut);
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
            HoldingJamCase = false;
            jamCase = null;
            jamCaseFor = null;
            if (heldCase == null)
                return;
            Firearms firearms = firearmController != null ? firearmController.Firearms : null;
            if (!throwIt && firearms != null && firearms._shellsInShellPort != null && firearms._shellsInShellPort.Length > 0
                && firearms._shellsInShellPort[0] == null && firearms._shellPortsTransforms.Count > 0 && firearms._shellPortsTransforms[0] != null)
            {
                // Not out: back in the port the way Tarkov seats it, where its repair finds it
                firearms._shellsInShellPort[0] = heldCase;
                Firearms.ParentShellToTransform(heldCase.gameObject, firearms._shellPortsTransforms[0]);
                if (debugLog)
                    LogAction("[VRReload] let go of the stuck case before it was out: back in the port");
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

        // After the holding hand's fingers are posed, like a held round (ApplyHeldFingerPose / ApplyGunHandFingerPose). Eased from the port
        // into the fingers over jamCaseSnapTime: the hand takes it from up to jamPortRadius away.
        private static void PlaceJamCase()
        {
            if (!HoldingJamCase || jamCase == null)
                return;
            Transform palm = ArmPalm(jamCaseOnRightArm);
            if (palm == null && !jamCaseOnRightArm)
                palm = TarkovVR.Patches.Core.VR.InitVRPatches.leftPalm;
            if (palm == null || !HoldInPalm(jamCaseOnRightArm, jamCase.transform, jamCaseAxisInProp, jamCaseCenterInProp, out Vector3 holdPosition, out Quaternion holdRotation))
                return;
            Vector3 position = palm.position + palm.rotation * holdPosition;
            Quaternion rotation = palm.rotation * holdRotation;
            float blend = jamCaseSnapTime > 0f ? Mathf.Clamp01((Time.time - jamCaseGrabTime) / jamCaseSnapTime) : 1f;
            if (blend < 1f)
            {
                blend = blend * blend * (3f - 2f * blend);
                position = Vector3.Lerp(jamCaseStartPosition, position, blend);
                rotation = Quaternion.Slerp(jamCaseStartRotation, rotation, blend);
            }
            jamCase.transform.SetPositionAndRotation(position, rotation);
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
