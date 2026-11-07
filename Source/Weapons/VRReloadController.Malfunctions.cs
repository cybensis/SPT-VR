using EFT;
using EFT.InventoryLogic;
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

        private static void ClearJamAtPort(FirearmController firearmController, bool gunHand)
        {
            if (debugLog)
                LogAction($"[VRReload] the stuck case taken out at the ejection port ({(gunHand ? "gun" : "off")} hand)");
            SoundHand hand = gunHand ? SoundHand.GunHand : SoundHand.OffHand;
            PlayFirstGunSound(firearmController, hand, "ShellRemove", "ShellPull", "ShellGrab");
            Pulse(hand, 0.45f, 0.04f);
            RepairByHand(firearmController);
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
