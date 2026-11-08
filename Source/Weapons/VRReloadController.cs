using System.Collections.Generic;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Misc;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using RootMotion.FinalIK;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Gun controller v2: magazines by hand. The off hand grabs the gun's magazine and pulls it out, takes a new one
    // from the chest, and seats it in the mag well. Each step runs Tarkov's own inventory move (the same moves the
    // inventory screen uses, so FIKA syncs them) and finishes Tarkov's operation the same frame, skipping its
    // animation, so the gun is never left mid-operation.
    //
    // Nothing in the inventory changes until the outcome is known:
    //  - the gun's magazine is only hidden while you hold it; letting go decides: back in the well = nothing changed,
    //    at the chest = into your rig/pockets, anywhere else = it drops to the ground where you let go
    //  - a new magazine stays in your rig until it is seated; letting go of it anywhere else puts it back
    // B (Y left-handed) drops the magazine straight out of the gun: one press with Manual Reload, a double tap without.
    public static partial class VRReloadController
    {
        public static bool enabled = true;
        public static float magazineGrabRadius = 0.08f;     // m palm to the gun magazine to grab it: a capsule from the magazine's center down to its bottom
        // The mag well track: a magazine in the well only slides along the well's axis, with the hand locked on it
        public static float railLength = 0.05f;             // m a magazine slides in the well before it comes free (pulling out) / from the top to seated (putting in)
        public static float magazineCatchDistance = 0.01f;  // m Manual Reload: past this the mag catch no longer holds it, B or not
        public static float railCaptureRadius = 0.04f;      // m magazine top to the track that catches a magazine being put in
        public static float railCaptureAngle = 40f;         // deg off the seated orientation that still catches
        public static float railCaptureTime = 0.12f;        // s to ease a caught magazine onto the track
        public static float railClearDistance = 0.05f;      // m a magazine just pulled out has to get from the well before the track can catch it again
        public static float pistolPushGain = 2.5f;          // how much further a pistol's magazine goes in than the hand pushes it. 1 = one to one.
        public static float pistolInsertStart = 0.08f;      // m below seated a pistol's magazine starts on the track going in
        public static float pistolCatchReach = 0.11f;       // m below seated the track catches it: about its tip at the bottom of the grip (headset: at 0.07 it had to be nearly in)
        public static float pistolCatchRadius = 0.05f;      // m off the track that still catches it
        public static float railExitMargin = 0.02f;         // m past the top of the track before a magazine being put in comes off it
        public static float railLateralEscape = 0.10f;      // m the controller moves sideways off the track before the hand lets go of it
        public static float handReleaseTime = 0.08f;        // s the rendered hand takes to go from the magazine back to the controller
        public static bool snapHandToGrip = true;           // grabbing the gun's magazine moves the hand onto it where the animation holds it
        public static float handSnapTime = 0.06f;           // s that move takes
        // A hand locked on a part of the gun (the handle, a slide, the bolt, a magazine on the track, the MDR's deflector) turns with the
        // controller a little, about the point it holds ("I really liked the hand pivot on the bullet ... while holding charging handle, you
        // have a bit of wiggle on the hand"). Capped: those holds have the fingers shaped around the part. 0 = rigid, as before.
        public static float handWiggleDegrees = 15f;
        public static float gripEaseRate = 10f;             // 1/s a free magazine settles into the animation's grip
        public static Vector3 chestPouchOffset = new Vector3(0f, -0.40f, 0.03f);    // from the eyes looking level, body frame (neck anchored): the chest pouch zone
                                                                                     // center (headset: 0.12 forward too far out; 2026-11-06 "too far forward and low" = up/back 2 cm)
        public static float chestPouchRadius = 0.16f;       // m palm to the chest pouch center
        public static Vector3 newMagazineHoldPosition = new Vector3(-0.10f, 0.02f, 0.11f);    // off hand local, used until a magazine is pulled off a gun (average of his headset grabs)
        public static Vector3 newMagazineHoldRotation = new Vector3(320f, 250f, 265f);        // off hand local euler, same
        public static bool reuseLastGrabPose = true;        // guns without a measured reload grip: new magazines sit in the hand the way the last one pulled off a gun did
        public static bool magazineFingers = true;          // the animation's finger pose while holding a magazine
        public static float fingerBlendRate = 12f;          // 1/s ease of that finger pose in and out
        public static float dropAngularSpeed = 3f;          // rad/s tumble on a dropped magazine
        public static bool sounds = true;                   // the gun's own reload sounds
        public static bool haptics = true;
        public static bool soundHaptics = true;             // the reload sounds also play on the controller doing the work, through the eating sounds' audio tap
        public static float soundHapticsStrength = 1f;
        public static float soundHapticsIdleStop = 0.5f;    // s after a sound ends that the tap comes off: the gun hands its source back to the pool 1 s after
        public static bool debugZones = false;             // spheres: grab zone (blue), chest pouch (yellow), mag well seat radius (green)
        public static bool debugLog = false;
        private static void LogAction(string message) { if (debugLog) Plugin.MyLog.LogInfo(message); }
        public static bool pickUpGroundMagazines = true;    // a loose magazine that fits, picked up in the off hand, is held for the reload

        private enum HeldMagazineSource { None, FromGun, FromPouch, FromGround }
        private enum ExpectedOperation { None, PullOut, Insert, Rechamber }

        // Off hand state, read by VRPlayerManager / the loot pointer / the weapon interact
        public static bool OwnsOffHand { get; private set; }            // holding a magazine, or reaching for one
        // Holding a part of the gun, without the zones it's only reaching into (GunSpots: the chest pouch zone overlaps the chest sling)
        public static bool OffHandHolds => HoldingMagazine || (HoldingChargingHandle && !HandleInGunHand) || (HoldingBolt && !BoltInGunHand)
            || HoldingSeatedMagazine || HoldingPalmedMagazine || (HoldingRound && !RoundInGunHand) || (HoldingJamCase && !jamCaseInGunHand) || (HoldingDeflector && !deflectorInGunHand);
        public static bool GunHandHolds => HandleInGunHand || BoltInGunHand || RoundInGunHand || JamCaseInGunHand || DeflectorInGunHand;
        // A hand on one of the gun's own parts, for GunHaptics (what happens to the gun reaches it): the handle, the bolt, a
        // magazine on the track (the gun's own, still in the well, included) or held seated. A held round or a magazine off the
        // track isn't on the gun.
        internal static bool OffHandOnGunPart => (HoldingChargingHandle && !HandleInGunHand) || (HoldingBolt && !BoltInGunHand)
            || (HoldingMagazine && onRail) || HoldingSeatedMagazine || (HoldingJamCase && !jamCaseInGunHand && !jamCaseOut) || (HoldingDeflector && !deflectorInGunHand);
        internal static bool GunHandOnGunPart => HandleInGunHand || BoltInGunHand || (JamCaseInGunHand && !jamCaseOut) || DeflectorInGunHand;
        public static bool HoldingMagazine => heldSource != HeldMagazineSource.None;
        public static bool HoldingGunMagazine => heldSource == HeldMagazineSource.FromGun;   // still in the gun as far as Tarkov knows: no firing

        // No firing: the gun's magazine is out in the hand, the charging handle is being worked, or the round in the chamber
        // is still in the magazine (Tarkov's insert chambers one at once; it has to be racked first). On a truly empty
        // chamber the trigger goes through: Tarkov dry-fires (the click, the finger) without looking at the bolt catch.
        public static bool BlocksFiring => HoldingGunMagazine || HoldingChargingHandle || RoundStillInMagazine || MagazinePartlyOut || BoltBlocksFiring || DeflectorBlocksFiring;

        private static bool ChamberLoaded => VRGlobals.firearmController != null && VRGlobals.firearmController.Item != null
            && VRGlobals.firearmController.Item.ChamberAmmoCount > 0;

        // The round Tarkov's insert chambered: the bolt is locked back on it, or it went in with the bolt forward and the
        // handle hasn't been racked
        private static bool RoundStillInMagazine => ChamberLoaded && (BoltLockedBack || WaitingForRack(VRGlobals.firearmController.Item));

        // On that round the trigger clicks like an empty gun, it's never handed to Tarkov (it would fire)
        public static bool TriggerOnlyClicks => !HoldingGunMagazine && !HoldingChargingHandle && RoundStillInMagazine && !BoltBlocksFiring;

        // Tarkov's own empty-gun trigger (Idling.SetTriggerPressed: SetAmmoInChamber, then SetFire) with the chamber shown
        // empty, as it looks with the bolt open: the animator plays its dry fire, the click and the finger. Local only:
        // FIKA's DryShot packet replays as SetFire on the other players, whose animator still has a round chambered.
        public static void DryFireTrigger(bool pressed)
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController.FirearmsAnimator == null || firearmController.Item == null)
                return;
            if (pressed)
                firearmController.FirearmsAnimator.SetAmmoInChamber(0f);
            firearmController.FirearmsAnimator.SetFire(pressed);
            if (!pressed)
                firearmController.FirearmsAnimator.SetAmmoInChamber(firearmController.Item.ChamberAmmoCount);
        }

        private static HeldMagazineSource heldSource;
        private static Magazine heldMagazine;
        private static FirearmController heldForFirearm;
        private static GameObject heldProp;
        private static Transform hiddenGunMagazineView;
        private static Transform magazineWell;
        private static Vector3 propWellPointLocal;          // the point on the prop that sits at the mag well when seated
        private static bool magazineLeftWell;
        private static Vector3 freeHoldPosition;            // where a magazine off the track sits in the hand (HandBone-local)
        private static Quaternion freeHoldRotation;
        private static bool heldOnRightArm;                 // the arm holding it (OnRightArm): the hold ends if that changes

        // The track, in the mag well bone's frame: the seated pose, and the axis the magazine slides out along
        private static Vector3 seatedLocalPosition;
        private static Quaternion seatedLocalRotation;
        private static Vector3 railAxisInWell;
        private static bool onRail;
        private static bool magazineCatchPassed;            // Manual Reload: the gun's magazine has moved past the catch
        private static bool grabbedWithRelease;             // taken from the support grip by pressing B: letting go of B gives it back
        private static float railCatchClearance;            // how far from the well it has to get first, on top of leaving the catch zone
        private static bool railCatchArmed;                 // a magazine that just came off the track must leave the catch zone first
        private static bool railPullingOut;                 // started seated (the gun's own magazine): comes free at railLength
        private static float railPosition;                  // m out from seated
        private static float railPositionAtStart;
        private static Vector3 handAtRailStartInWell;       // the controller (LeftHand), not the rendered hand
        private static float railBlend;                     // easing a caught magazine onto the track
        private static Vector3 railEntryHandPosition;       // the magazine's hand-local pose when it was caught
        private static Quaternion railEntryHandRotation;

        // While on the track the rendered hand holds the magazine: the left arm IK reaches for this instead of LeftHand
        private static Transform handLatch;
        private static bool handLatched;
        private static bool handLatchReleasing;
        private static bool handLatchSnapping;
        private static bool latchedHandIsGunHand;           // the gun hand on the charging handle; everything else latches the off hand
        private static bool latchedOnRightArm;              // the arm drawing the latched hand (OnRightArm), as it was at the grab
        private static Transform latchedHandRig;            // that hand's controller transform in its arm's frame (HandRig), as it was at the grab
        private static Vector3 handSnapLocalPosition;       // the animation's grip, in the magazine's frame
        private static Quaternion handSnapLocalRotation;
        // The latch's own pose on what it holds, before the wiggle (the wiggle is put on top every frame, never into this)
        private static Vector3 latchBaseLocalPosition;
        private static Quaternion latchBaseLocalRotation = Quaternion.identity;
        private static Quaternion latchControllerAtGrabInHolder = Quaternion.identity;  // the controller's turn at the grab, in the holder's frame
        private static Vector3 latchPivotInHolder;          // the point the hand holds, in the holder's frame: the wiggle turns the hand about it
        private static Transform latchHolder;               // what those are relative to (LatchHand's holder, or where the latch was moved on to)
        private static Vector3 latchPivotInLatch;           // the same point in the hand's own frame, kept each frame: what a rebase starts from
        public static float latchWigglePivotMaxDistance = 0.25f;   // m a pivot further than this from the hand isn't one (no wiggle)

        // What each borrowed magazine model had changed, to put back before it returns to the pool (palming holds two)
        private sealed class PropChanges
        {
            public readonly List<Collider> disabledColliders = new List<Collider>();
            public readonly Dictionary<Transform, int> originalLayers = new Dictionary<Transform, int>();
        }
        private static readonly Dictionary<GameObject, PropChanges> propChanges = new Dictionary<GameObject, PropChanges>();

        private static bool hasLastGrabPose;
        private static Vector3 lastGrabPosition;
        private static Quaternion lastGrabRotation;

        private static ReloadGrip heldReloadGrip;

        private static float fingerWeight;
        private static Vector3[] fingerPoseShown;
        private static bool fingerPoseShownIsRight;         // recorded on the right hand (the bolt's / handle's gun-hand data), else on the left
        private static Transform offRightPalm;              // the off hand on the right arm (OffHandOnRightArm): its bones in pose order
        private static Transform[] offRightFingerBones;
        private static readonly Quaternion[] leftPoseScratch = new Quaternion[16];
        private static readonly Quaternion[] rightPoseScratch = new Quaternion[16];
        private static float gunHandFingerWeight;
        private static Vector3[] gunHandFingerPoseShown;
        private static Transform gunHandPalm;
        private static Transform[] gunHandFingerBones;
        private static Vector3[] magazineHandFingers;       // the pose the hand took on the magazine, kept until it lets go (seating, pulling it back out)
        private static Transform[] leftPalmBones;

        // Committed to an inventory move: the prop stays in the hand and the gun's magazine stays hidden until
        // Tarkov's operation is finished, so neither flashes for the frame in between
        private static GameObject pendingProp;
        private static Transform pendingHiddenView;

        private static bool previousGripState;
        private static bool previousReleaseState;
        private static bool previousTriggerState;
        private static bool previousGunHandTriggerState;
        private static int lastTickFrame = -10;
        private static bool lastTickGunInLeftHand;
        private static int lastTriggerTickFrame = -10;

        private static ExpectedOperation expectedOperation;
        private static FirearmController expectedForFirearm;
        private static float expectedOperationDeadline;
        private static int expectedOperationStartFrame;

        // A magazine picked up off the ground: the ground item stays in the world, hidden, while our model of it is held
        private static LootItem groundMagazine;
        private static float groundMagazineLetGoTime;       // when the loot hold let go of it (FIKA needs a beat before a move)
        private static LootItem lastRefusedGroundItem;      // didn't fit this gun: not checked (or logged) again while it's held
        private static FirearmController lastRefusedForFirearm;
        // After the move that takes it off the ground: Tarkov removes the ground item when it lands; still there = it
        // didn't, show it again
        private static LootItem unconfirmedGroundMagazine;
        private static Item unconfirmedGroundItem;
        private static float unconfirmedGroundDeadline;
        private static LootItem hiddenRenderersOf;
        private static readonly List<Renderer> hiddenGroundRenderers = new List<Renderer>();
        private static readonly List<Renderer> rendererScratch = new List<Renderer>();

        // Placement for the magazine Tarkov throws into the world (VRMagazinePatches)
        private static Item pendingDropMagazine;
        private static Vector3 pendingDropPosition;
        private static Quaternion pendingDropRotation;
        private static Vector3 pendingDropVelocity;
        private static float pendingDropDeadline;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Tarkov's reload, measured from its own animation (WeaponRecon). The holds themselves: VRReloadController.Grips.cs

        // The animation's left-hand finger poses holding a magazine, index-matched like Tarkov's HandPoser
        // (GetComponentsInChildren of the palm; 0 is the palm itself and is left alone). Measured on the M4A1, used for
        // any magazine with no recording of its own. Pull = the most closed moment while the hand is on the old magazine (the mag-out sound).
        private static readonly Vector3[] MAGAZINE_PULL_FINGERS =
        {
            Vector3.zero,
            new Vector3(50.3f, 117.5f, 96f), new Vector3(18.7f, 359.9f, 21.4f), new Vector3(15.6f, 0.1f, 37f),       // thumb
            new Vector3(0.7f, 355.6f, 9.1f), new Vector3(0.1f, 359.9f, 45.9f), new Vector3(0.1f, 359.9f, 57.2f),      // index
            new Vector3(0.6f, 357.4f, 22.4f), new Vector3(0f, 360f, 61.8f), new Vector3(0.1f, 360f, 49.9f),           // middle
            new Vector3(0.7f, 356.6f, 21.1f), new Vector3(0f, 360f, 44.9f), new Vector3(0f, 360f, 39.3f),             // ring
            new Vector3(1.1f, 355.7f, 17.1f), new Vector3(0f, 0f, 7.5f), new Vector3(360f, 0f, 67.3f),                // pinky
        };

        // Insert = while it puts the new magazine in
        private static readonly Vector3[] MAGAZINE_INSERT_FINGERS =
        {
            Vector3.zero,
            new Vector3(63.2f, 102.2f, 82.7f), new Vector3(353.7f, 359.7f, 15.9f), new Vector3(0.4f, 359.4f, 16f),    // thumb
            new Vector3(0.7f, 355.6f, 15.9f), new Vector3(0.1f, 360f, 49.8f), new Vector3(0.4f, 359.9f, 27.4f),       // index
            new Vector3(0.2f, 355f, 46.6f), new Vector3(0.1f, 360f, 73.5f), new Vector3(0.2f, 0.2f, 67.3f),           // middle
            new Vector3(0.1f, 354.4f, 62.1f), new Vector3(0f, 360f, 79f), new Vector3(0f, 360f, 58.3f),               // ring
            new Vector3(0.3f, 353.8f, 73.3f), new Vector3(360f, 360f, 85f), new Vector3(360f, 0f, 61f),               // pinky
        };

        // The right hand's fingers hooked over an AR's T (m4a1_discharge), for a gun whose clips never rack with that hand
        private static readonly Vector3[] GUN_HAND_HANDLE_FINGERS =
        {
            Vector3.zero,
            new Vector3(323.6f, 322.3f, 21.1f), new Vector3(359.9f, 0.1f, 0f), new Vector3(359.4f, 0.1f, 21.5f),      // thumb
            new Vector3(360f, 354.7f, 15.8f), new Vector3(0f, 360f, 18.2f), new Vector3(0f, 0f, 25.9f),               // index
            new Vector3(0f, 5.7f, 25.7f), new Vector3(0f, 0f, 15.1f), new Vector3(0f, 0.1f, 25.8f),                   // middle
            new Vector3(0.1f, 7.4f, 45.8f), new Vector3(0f, 0.1f, 45.9f), new Vector3(0f, 0.1f, 45.9f),               // ring
            new Vector3(0.1f, 7.2f, 45.8f), new Vector3(0f, 0.1f, 45.9f), new Vector3(0f, 0.2f, 45.9f),               // pinky
        };

        private static FirearmController gunSoundPlayerFor;
        private static BaseSoundPlayer gunSoundPlayer;

        // Which controller(s) feel a reload sound or pulse: the hand doing it, and how it reaches the others on the gun (GunHaptics)
        private readonly struct SoundHand
        {
            public readonly bool? gunHandActs;      // null = no hand: the gun's own parts moving
            public readonly GunHaptics.Feel feel;

            private SoundHand(bool? gunHandActs, GunHaptics.Feel feel)
            {
                this.gunHandActs = gunHandActs;
                this.feel = feel;
            }

            public static SoundHand OffHand => new SoundHand(false, GunHaptics.Feel.Carried);
            public static SoundHand GunHand => new SoundHand(true, GunHaptics.Feel.Carried);
            public static SoundHand GunBody => new SoundHand(null, GunHaptics.Feel.Mechanism);  // a slam, the magazine falling free
            public static SoundHand OffHandAlone => new SoundHand(false, GunHaptics.Feel.Alone); // the pouch, a blip
            public static SoundHand GunHandAlone => new SoundHand(true, GunHaptics.Feel.Alone);
            public SoundHand Seating => new SoundHand(gunHandActs, GunHaptics.Feel.Mechanism); // this hand, and the gun's whole body: a magazine seating or coming free
        }
        private static SoundHand playingSoundHand;
        private static bool playingOwnSound;                // true inside our SoundEventHandler call: the clip it plays is ours

        private static readonly List<Magazine> reachableMagazines = new List<Magazine>();
        private static readonly List<Renderer> magazineRenderers = new List<Renderer>();
        private static GameObject grabZoneViz, pouchZoneViz, wellZoneViz;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Called every frame from the top of HandsInteractionController.Update
        public static void Tick()
        {
            // Resync the grip edge after skipped frames so a held grip doesn't read as a fresh press
            VRBoolean offHandGrip = Handedness.OffHandGrip;
            bool gripState = offHandGrip.state;
            // The gun hand's grip: a single round in that hand (VRReloadController.Rounds)
            VRBoolean gunHandGrip = Handedness.GunHandGrip;
            bool gunHandGripState = gunHandGrip.state;
            // The gun changed hands since the last tick (ChestHang's hand swap): the off / gun hand reads here are the other
            // controllers now, so their last states are the wrong hand's - the hand left on the grip read as a fresh press (and
            // grabbed the magazine right there). Resynced like after skipped frames.
            bool handsSwapped = Handedness.GunInLeftHand != lastTickGunInLeftHand;
            lastTickGunInLeftHand = Handedness.GunInLeftHand;
            if (Time.frameCount - lastTickFrame > 1 || handsSwapped)
            {
                previousGripState = gripState;
                previousGunHandGripState = gunHandGripState;
            }
            lastTickFrame = Time.frameCount;
            bool gripPressed = gripState && !previousGripState;
            bool gripReleased = !gripState && previousGripState;
            previousGripState = gripState;
            bool gunHandGripPressed = gunHandGripState && !previousGunHandGripState;
            bool gunHandGripReleased = !gunHandGripState && previousGunHandGripState;
            previousGunHandGripState = gunHandGripState;

            // The off-hand trigger works the charging handle (the same 0.5 / 0.35 hysteresis as the other trigger reads)
            float offHandTrigger = Handedness.OffHandTrigger.axis;
            bool triggerResumed = Time.frameCount - lastTriggerTickFrame > 1 || handsSwapped;
            if (triggerResumed)
                previousTriggerState = offHandTrigger > 0.5f;
            lastTriggerTickFrame = Time.frameCount;
            bool triggerState = previousTriggerState ? offHandTrigger > 0.35f : offHandTrigger > 0.5f;
            bool triggerPressed = triggerState && !previousTriggerState;
            bool triggerReleased = !triggerState && previousTriggerState;
            previousTriggerState = triggerState;

            // The gun hand's trigger works the handle too, once that hand is off the grip (it can't fire then)
            float gunHandTrigger = Handedness.GunHandTrigger.axis;
            if (triggerResumed)
                previousGunHandTriggerState = gunHandTrigger > 0.5f;
            bool gunHandTriggerState = previousGunHandTriggerState ? gunHandTrigger > 0.35f : gunHandTrigger > 0.5f;
            bool gunHandTriggerPressed = gunHandTriggerState && !previousGunHandTriggerState;
            bool gunHandTriggerReleased = !gunHandTriggerState && previousGunHandTriggerState;
            previousGunHandTriggerState = gunHandTriggerState;

            // A click on the gun hand when the mag release goes down
            bool releaseState = MagazineReleaseHeld();
            if (handsSwapped)
                previousReleaseState = releaseState;
            bool releasePressed = releaseState && !previousReleaseState;
            bool releaseLetGo = !releaseState && previousReleaseState;
            if (releasePressed)
            {
                PlayGunSound(VRGlobals.firearmController, "Button", ReleasePressingHand(VRGlobals.firearmController));
                if (OffHandPressesCatch(VRGlobals.firearmController))
                    Pulse(0.25f, 0.03f);
                else
                    PulseGunHand(0.25f, 0.03f);
            }
            previousReleaseState = releaseState;

            if (expectedOperation != ExpectedOperation.None)
                TryForceExpectedOperation();
            // Malfunctions (VRReloadController.Malfunctions): a bolt-action slide jam's start clip skipped, a cleared one's stale who-knows lists emptied
            SkipBoltActionSlideJamClip();
            ForgetClearedMalfunction();
            CheckGroundMagazineMove();

            if (HoldingSeatedMagazine)
                UpdateSeatedHold(gripReleased, releasePressed);
            if (HoldingMagazine)
            {
                if (!HoldIsStillValid())
                    CancelHold();
                else
                    UpdateHeldMagazine(gripReleased, releaseLetGo);
            }
            UpdatePalmedMagazine(gripReleased);
            UpdateMagazinePartlyOut(releaseState);
            if (HoldingChargingHandle)
            {
                if (!ChargingHandleHoldIsStillValid())
                    LetGoOfChargingHandle(snapForward: false);
                else
                    UpdateChargingHandle(HandleInGunHand ? gunHandTriggerReleased : triggerReleased);
            }
            // The stuck case of a failure to eject in the fingers (VRReloadController.Malfunctions)
            UpdateJamCase(triggerReleased, gunHandTriggerReleased);
            // The MDR's deflector in the hand (VRReloadController.Deflector)
            UpdateDeflector(triggerReleased, gunHandTriggerReleased);
            if (boltReleaseFor != null)
                UpdateBoltRelease();
            // A bolt-action's bolt, by either hand's trigger (VRReloadController.Bolt)
            TickBolt(triggerPressed, triggerReleased, gunHandTriggerPressed, gunHandTriggerReleased,
                handBusy: HoldingMagazine || HoldingChargingHandle || HoldingSeatedMagazine || HoldingPalmedMagazine || HoldingRound || HoldingJamCase || HoldingDeflector);
            // A single round in the fingers, for an open bolt-action (VRReloadController.Rounds)
            if (HoldingRound)
                UpdateHeldRound(gripReleased, gunHandGripReleased);
            UpdateHandLatch();
            UpdateMagazineReleaseButton(releaseState);

            bool handBusy = HoldingMagazine || HoldingChargingHandle || HoldingSeatedMagazine || HoldingPalmedMagazine || HoldingBolt || HoldingRound || HoldingJamCase || HoldingDeflector;
            // On a gun that loads through its open bolt, the pouch grip is a round, not a magazine: the off hand's, or the gun hand's
            // while the off hand holds the gun up
            if (!handBusy && gripPressed && TryGrabRound(gunHand: false))
            {
                gripPressed = false;
                handBusy = true;
            }
            else if (!handBusy && gunHandGripPressed && TryGrabRound(gunHand: true))
                handBusy = true;
            UpdateBoltSlap(handFree: !handBusy);
            if (!handBusy)
                TryTakeOverGroundMagazine();
            KeepGroundMagazineHidden();

            bool reaching = false;
            OffHandAtChargingHandle = false;
            SetZoneViz(ref jamPortZoneViz, false, Vector3.zero, 0f, Color.clear);   // InJamPortZone shows it again where it applies
            SetZoneViz(ref deflectorZoneViz, false, Vector3.zero, 0f, Color.clear); // InDeflectorZone too
            if (!handBusy && CanStartGrab(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
            {
                Vector3 palm = OffHandPalm();
                bool supporting = VRGlobals.vrPlayer.isSupporting;
                bool inGrabZone = InGunMagazineGrabZone(firearmController, weapon, magazineSlot, palm);
                bool inHandleZone = !supporting && InChargingHandleZone(firearmController, weapon);
                bool inSideHandleZone = !supporting && !inHandleZone && InSideHandleZone(firearmController, weapon);
                bool inPouchZone = !supporting && InChestPouchZone(palm);
                // A failure to eject: the case stuck in the ejection port, taken with the trigger like the handle (VRReloadController.Malfunctions)
                bool atJamPort = InJamPortZone(firearmController, weapon, gunHand: false);
                bool atDeflector = !atJamPort && InDeflectorZone(firearmController, gunHand: false);
                OffHandAtChargingHandle = inHandleZone || inSideHandleZone || atJamPort || atDeflector;
                // A hand on the support grip is only taken over by an actual grab, never by passing through a zone
                // (a pistol's support hand cups right over its magazine). Not the handle's zone: that's the trigger's, the
                // foregrip's the grip's, and on an AK the two are 12 cm apart, so this would keep the foregrip from being taken.
                reaching = !supporting && (inGrabZone || inPouchZone);
                // A press ChestHang's hand swap left to the magazine (the hand nearer it than the grip): taken with the swap's reach,
                // the palm maybe just outside this one (a short magazine in the grip, the MP7's 20-round)
                bool pressLeftToMagazine = ChestHang.GripPressLeftToMagazineFrame == Time.frameCount && GunMagazineGrabbable(weapon) && !MagazinePartlyOut;
                // Before the handle: on an AK the handle rides the carrier right at the port (a rack would clear it too)
                if (triggerPressed && atJamPort)
                    TakeJamCase(firearmController, gunHand: false);
                else if (triggerPressed && atDeflector)
                    GrabDeflector(firearmController, gunHand: false);
                else if (gripPressed && (inGrabZone || pressLeftToMagazine))
                    GrabFromGun(firearmController, weapon, magazineSlot, takenWithRelease: false);
                // Manual Reload, two-handing with the support hand at the magazine (a pistol): the mag release takes it
                else if (releasePressed && supporting && inGrabZone && !MagazineSlidesOntoHand)
                    GrabFromGun(firearmController, weapon, magazineSlot, takenWithRelease: true);
                else if (triggerPressed && (inHandleZone || inSideHandleZone))
                    GrabChargingHandle(firearmController, weapon, sideHandle: inSideHandleZone);
                else if (gripPressed && inPouchZone)
                    GrabFromPouch(firearmController, weapon, magazineSlot);
            }
            // The gun hand off the grip, the off hand holding the gun up (ChestHang's support hold): that hand can take the handle, or a
            // case stuck in the port (its trigger: its grip there would take the gun's grip back)
            if (!handBusy && ChestHang.SupportOnly && CanWorkGun(out FirearmController heldFirearm, out Weapon heldWeapon, out _))
            {
                bool gunHandAtJamPort = InJamPortZone(heldFirearm, heldWeapon, gunHand: true);
                bool gunHandAtDeflector = !gunHandAtJamPort && InDeflectorZone(heldFirearm, gunHand: true);
                if (gunHandTriggerPressed && gunHandAtJamPort)
                    TakeJamCase(heldFirearm, gunHand: true);
                else if (gunHandTriggerPressed && gunHandAtDeflector)
                    GrabDeflector(heldFirearm, gunHand: true);
                else if (gunHandTriggerPressed && InChargingHandleZone(heldFirearm, heldWeapon, gunHand: true))
                    GrabChargingHandle(heldFirearm, heldWeapon, gunHand: true);
            }
            // Manual Reload, like the real button: B lets the magazine fall out of the well, unless the off hand has it
            // (on the track, still on it from seating it, or cupping it from the support grip just above)
            // A new magazine in the hand, at the gun's: the trigger takes that one too (palming)
            UpdatePalmZoneBlip();
            if (triggerPressed)
                TryStartPalming();
            if (releasePressed && !HoldingGunMagazine && !HoldingSeatedMagazine && !TrySlideMagazinePartlyOut())
                TryReleaseMagazine();
            // The gun hand on the handle leaves the off hand where it is: on the foregrip, holding the gun up
            if (OffHandAtBolt)
                OffHandAtChargingHandle = true;
            OwnsOffHand = (HoldingJamCase && !jamCaseInGunHand) || (HoldingDeflector && !deflectorInGunHand) || HoldingMagazine || (HoldingChargingHandle && !HandleInGunHand) || (HoldingBolt && !BoltInGunHand) || HoldingSeatedMagazine || HoldingPalmedMagazine
                || (HoldingRound && !RoundInGunHand) || reaching;

            UpdateZoneViz();
            UpdateRoundViz();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Grabbing

        private static bool CanStartGrab(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot)
        {
            return CanWorkGun(out firearmController, out weapon, out magazineSlot) && !ChestHang.Detached;
        }

        private static bool CanWorkGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot)
        {
            weapon = null;
            magazineSlot = null;
            if (!TryGetMagazineGun(out firearmController, out weapon, out magazineSlot))
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None)
                return false;
            if (VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.heldItem != null)
                return false;
            return true;
        }

        // Manual reload on a gun with a detachable magazine: B is the mag release, not Tarkov's reload
        public static bool ManualReloadActive()
        {
            return VRSettings.GetManualReloadOn() && TryGetMagazineGun(out _, out _, out _);
        }

        // Holding B (Y left-handed) with manual reload on
        private static bool MagazineReleaseHeld()
        {
            if (!ManualReloadActive())
                return false;
            if (EitherHandWorksCatch(VRGlobals.firearmController))
                return GunHandReleaseButton.state || OffHandReleaseHeld();
            if (ThumbWorksCatch(VRGlobals.firearmController))
                return OffHandReleaseHeld();
            return GunHandReleaseButton.state;
        }

        private static VRBoolean GunHandReleaseButton => Handedness.GunHandUpperButton;

        // A paddle catch is the off thumb's, on the off controller's button: only with that hand on the magazine (off it, the
        // same button is the flashlight or Tarkov's Escape)
        private static bool OffHandReleaseHeld()
        {
            VRBoolean thumbButton = Handedness.OffHandUpperButton;
            return thumbButton.state && ((HoldingGunMagazine && onRail) || HoldingSeatedMagazine);
        }

        // The off thumb on an AK / MP5's latch, and on a gun either hand works when it's that button down; the gun hand's finger otherwise
        private static bool OffHandPressesCatch(FirearmController firearmController)
        {
            if (EitherHandWorksCatch(firearmController))
                return !GunHandReleaseButton.state && OffHandReleaseHeld();
            return ThumbWorksCatch(firearmController);
        }

        // A held gun with a detachable magazine, in our player's hands
        private static bool TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot)
        {
            firearmController = null;
            weapon = null;
            magazineSlot = null;
            if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.LeftHand == null
                || VRGlobals.player == null)
                return false;
            firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController.Firearms == null || firearmController.IsInLauncherMode())
                return false;
            weapon = firearmController.Item;
            if (weapon == null)
                return false;
            magazineSlot = weapon.GetMagazineSlot();
            if (magazineSlot == null || !TakesDetachableMagazine(weapon, magazineSlot))
            {
                magazineSlot = null;
                return false;
            }
            return true;
        }

        // Tarkov's ReloadMode only says "detachable" while a magazine is in: with the slot empty it's the template's, and the AVT, SVT,
        // M700, AXMC and TRG templates (and the SKS's / Mosin's, for their detachable ones) say InternalMagazine. Pulled empty, those
        // were no magazine gun any more, so no magazine could go back in by hand. Then it's whether the slot takes a detachable one.
        private static readonly Dictionary<MongoID, bool> slotTakesDetachable = new Dictionary<MongoID, bool>();
        private static bool TakesDetachableMagazine(Weapon weapon, Slot magazineSlot)
        {
            Weapon.EReloadMode reloadMode = weapon.ReloadMode;
            if (reloadMode == Weapon.EReloadMode.ExternalMagazine || reloadMode == Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport)
                return true;
            if (reloadMode != Weapon.EReloadMode.InternalMagazine || weapon.GetCurrentMagazine() != null)
                return false;
            if (!slotTakesDetachable.TryGetValue(weapon.TemplateId, out bool takes))
            {
                ItemFactory itemFactory = Singleton<ItemFactory>.Instance;
                if (itemFactory == null)
                    return false;
                foreach (ItemFilter filter in magazineSlot.Filters)
                {
                    foreach (MongoID templateId in filter.Filter)
                    {
                        if (itemFactory.ItemTemplates.TryGetValue(templateId, out ItemTemplate template) && template is MagazineTemplate magazineTemplate
                            && magazineTemplate.ReloadMagType == Weapon.EReloadMode.ExternalMagazine)
                            takes = true;
                    }
                }
                slotTakesDetachable[weapon.TemplateId] = takes;
            }
            return takes;
        }

        private static bool InGunMagazineGrabZone(FirearmController firearmController, Weapon weapon, Slot magazineSlot, Vector3 palm)
        {
            if (!GunMagazineGrabbable(weapon))
                return false;
            // Partly out: the magazine and below it, the hand can take it there (headset: with the palm having to be right at the
            // magazine it couldn't be grabbed)
            if (MagazinePartlyOut)
                return OffHandUnderMagazine(firearmController, magazineSlot, onItToo: true);
            return TryGetGunMagazineCapsule(firearmController, magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom)
                && DistanceToSegment(palm, magazineCenter, magazineBottom) <= magazineGrabRadius;
        }

        // Everything but where the hand is
        private static bool GunMagazineGrabbable(Weapon weapon)
        {
            if (weapon.GetCurrentMagazine() == null)
                return false;
            // Manual Reload: any gun, the mag catch holds it in the well until B is down (UpdateRail). Otherwise
            // grab-and-pull, but not on pistols (the magazine sits inside the grip where the support hand cups it) or
            // from the support grip.
            if (!VRSettings.GetManualReloadOn() && (VRGlobals.vrPlayer.isWeapPistol || VRGlobals.vrPlayer.isSupporting))
                return false;
            // A pistol's has to come partly out first (the release held)
            return !MagazineSlidesOntoHand || MagazinePartlyOut;
        }

        // For ChestHang's hand swap (nearest wins, see TryGrabFromGunHand): the off hand's touch points (the swap's own: palm,
        // knuckles, fingertips) to the seated magazine's grab capsule, over magazineGrabRadius. float.MaxValue where a grip press
        // couldn't take it now (Tick's own conditions). A pistol's partly-out magazine isn't in it: the swap leaves pistols alone.
        internal static float OffHandMagazineScore(List<Vector3> touchPoints)
        {
            if (HoldingMagazine || HoldingChargingHandle || HoldingSeatedMagazine || HoldingPalmedMagazine || HoldingBolt || HoldingRound)
                return float.MaxValue;
            if (touchPoints == null || touchPoints.Count == 0 || !CanStartGrab(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot)
                || !GunMagazineGrabbable(weapon) || MagazinePartlyOut
                || !TryGetGunMagazineCapsule(firearmController, magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom))
                return float.MaxValue;
            float nearest = float.MaxValue;
            foreach (Vector3 point in touchPoints)
                nearest = Mathf.Min(nearest, DistanceToSegment(point, magazineCenter, magazineBottom) / magazineGrabRadius);
            return nearest;
        }

        private static float DistanceToSegment(Vector3 point, Vector3 segmentStart, Vector3 segmentEnd)
        {
            Vector3 segment = segmentEnd - segmentStart;
            float along = segment.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(point - segmentStart, segment) / segment.sqrMagnitude) : 0f;
            return (point - (segmentStart + segment * along)).magnitude;
        }

        // The magazine's length is only read when the magazine changes (this runs every frame the hand is free); its center
        // is read fresh, see TryGetGunMagazineCenter
        private static Transform magazineShapeView;
        private static Magazine magazineShapeFor;
        private static Vector3 magazineShapeAxis;
        private static float magazineShapeLength;

        private static bool TryGetGunMagazineCapsule(FirearmController firearmController, Slot magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom)
        {
            magazineBottom = Vector3.zero;
            if (!TryGetGunMagazineCenter(firearmController, magazineSlot, out magazineCenter))
                return false;
            magazineBottom = magazineCenter;
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazineView == null || magazineView.ItemView == null || magazineView.Bone == null)
                return true;
            Magazine magazine = firearmController.Item.GetCurrentMagazine();
            if (magazineShapeView != magazineView.ItemView || magazineShapeFor != magazine)
            {
                magazineShapeView = magazineView.ItemView;
                magazineShapeFor = magazine;
                if (!TryGetMagazineShape(magazineView.ItemView.gameObject, magazineView.ItemView.InverseTransformPoint(magazineView.Bone.position),
                    out magazineShapeAxis, out magazineShapeLength, out _))
                    magazineShapeLength = 0f;
            }
            magazineBottom = magazineCenter + magazineView.ItemView.TransformDirection(magazineShapeAxis).normalized * (magazineShapeLength * 0.5f);
            return true;
        }

        private static bool InChestPouchZone(Vector3 palm)
        {
            if (!TryGetChestPouchCenter(out Vector3 pouchCenter))
                return false;
            return (palm - pouchCenter).sqrMagnitude <= chestPouchRadius * chestPouchRadius;
        }

        // keepHandPose: the rendered hand is already on the magazine (the seated hold) and stays exactly there, fingers too,
        // instead of starting at the controller and snapping into the animation's grip
        private static void GrabFromGun(FirearmController firearmController, Weapon weapon, Slot magazineSlot, bool takenWithRelease, bool keepHandPose = false)
        {
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            Magazine magazine = weapon.GetCurrentMagazine();
            if (magazineView == null || magazineView.ItemView == null || magazine == null)
                return;
            // Tarkov won't unload a Feed malfunction either (it wants the jam examined first)
            if (weapon.MalfState.State == Weapon.EMalfunctionState.Feed)
            {
                Pulse(SoundHand.OffHandAlone, 0.2f, 0.03f);
                Pulse(SoundHand.OffHandAlone, 0.2f, 0.03f, 0.08f);
                return;
            }

            float startPosition = TakePartlyOutMagazine();
            GameObject prop = CreateProp(magazine);
            if (prop == null)
                return;

            // The prop takes the exact pose of the magazine on the gun (a direct child of the well bone) and starts on
            // the track, seated, with the hand locked on it
            Transform propTransform = prop.transform;
            propTransform.SetParent(magazineView.Bone, worldPositionStays: false);
            propTransform.localPosition = magazineView.ItemView.localPosition;
            propTransform.localRotation = magazineView.ItemView.localRotation;
            propTransform.localScale = magazineView.ItemView.localScale;
            propWellPointLocal = propTransform.InverseTransformPoint(magazineView.Bone.position);
            seatedLocalPosition = propTransform.localPosition;
            seatedLocalRotation = propTransform.localRotation;
            heldReloadGrip = FindReloadGrip(firearmController, magazine, prop, propWellPointLocal);
            railAxisInWell = RailAxisInWell(firearmController, prop, propTransform.localScale);
            heldRockIn = RockInOf(firearmController);

            hiddenGunMagazineView = magazineView.ItemView;
            hiddenGunMagazineView.gameObject.SetActive(false);

            heldSource = HeldMagazineSource.FromGun;
            heldMagazine = magazine;
            heldForFirearm = firearmController;
            heldProp = prop;
            heldOnRightArm = OffHandOnRightArm;
            magazineWell = magazineView.Bone;
            magazineLeftWell = false;
            magazineCatchPassed = false;
            grabbedWithRelease = takenWithRelease;
            Vector3 handPosition = handLatch != null ? handLatch.position : Vector3.zero;
            Quaternion handRotation = handLatch != null ? handLatch.rotation : Quaternion.identity;
            StartRail(pullingOut: true, startPosition: startPosition);
            if (startPosition > 0f)
            {
                railStartedPartlyOut = true;
                magazineCatchPassed = true;
                PlaceOnRail();
            }
            if (keepHandPose)
            {
                handLatch.SetPositionAndRotation(handPosition, handRotation);
                latchBaseLocalPosition = handLatch.localPosition;
                latchBaseLocalRotation = handLatch.localRotation;
            }
            else
            {
                magazineHandFingers = heldReloadGrip?.pull?.fingers ?? MAGAZINE_PULL_FINGERS;
                if (snapHandToGrip && heldReloadGrip?.pull != null)
                    SnapHandToGrip(heldReloadGrip.pull.position, heldReloadGrip.pull.rotation);
            }

            if (debugLog)
                LogAction($"[VRReload] grabbed gun magazine {magazine.LocalizedShortName()}, track axis {railAxisInWell.ToString("F2")}");
            if (!VRSettings.GetManualReloadOn())
                PlayGunSound(firearmController, "Button");
            Pulse(0.35f, 0.05f);
        }

        private static void GrabFromPouch(FirearmController firearmController, Weapon weapon, Slot magazineSlot)
        {
            Magazine magazine = FindMagazineToReload(weapon, magazineSlot);
            if (magazine == null)
            {
                // Nothing that fits: a short double buzz
                Pulse(SoundHand.OffHandAlone, 0.2f, 0.03f);
                Pulse(SoundHand.OffHandAlone, 0.2f, 0.03f, 0.08f);
                return;
            }
            if (!HoldNewMagazine(firearmController, weapon, magazineSlot, magazine, HeldMagazineSource.FromPouch, startFrom: null))
                return;

            if (debugLog)
                LogAction($"[VRReload] took {magazine.LocalizedShortName()} ({magazine.Count} rounds) from the pouch");
            PlayGunSound(firearmController, "MagPullOut", SoundHand.OffHandAlone);
            Pulse(SoundHand.OffHandAlone, 0.35f, 0.05f);
        }

        // The loot hold has a magazine that fits the gun in the off hand: it becomes a magazine held for the reload, like
        // one from the rig, eased from where it sits in the palm into the reload grip. The loot hold lets go of the
        // ground item first, so it's back on FIKA's own loot sync before any move (the stow's order: a move on a VR-held
        // item doesn't reach the other players); it's hidden where it lands while our model of it is in the hand.
        private static void TryTakeOverGroundMagazine()
        {
            HandsInteractionController hands = VRGlobals.handsInteractionController;
            if (!pickUpGroundMagazines || hands == null || !hands.HoldsLootInOffHand(out LootItem lootItem))
            {
                lastRefusedGroundItem = null;
                return;
            }
            if (!(lootItem.Item is Magazine magazine))
                return;
            if (!TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
                return;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None || ChestHang.Detached)
                return;
            // No loose loot moves in the hideout (FIKA's item sync has no world there)
            if (Singleton<GameWorld>.Instance == null || Singleton<GameWorld>.Instance is HideoutGameWorld)
                return;
            if (lootItem == lastRefusedGroundItem && firearmController == lastRefusedForFirearm)
                return;
            Option<bool> fits = ItemManipulator.CheckMoveIgnoringTargetItem(magazine, magazineSlot, VRGlobals.player.InventoryController);
            if (fits.Failed)
            {
                lastRefusedGroundItem = lootItem;
                lastRefusedForFirearm = firearmController;
                if (debugLog)
                    LogAction($"[VRReload] {magazine.LocalizedShortName()} picked up, not for this gun: {fits.Error}");
                return;
            }
            if (!HoldNewMagazine(firearmController, weapon, magazineSlot, magazine, HeldMagazineSource.FromGround, startFrom: lootItem.transform))
                return;

            hands.ForceDropHeldItem();
            groundMagazine = lootItem;
            groundMagazineLetGoTime = Time.time;
            HideGroundMagazine(lootItem);
            if (debugLog)
                LogAction($"[VRReload] picked {magazine.LocalizedShortName()} ({magazine.Count} rounds) up off the ground");
            Pulse(SoundHand.OffHandAlone, 0.25f, 0.04f);
        }

        // Re-asserted every Update and LateUpdate: the loot drop schedules its own make-visible a moment later
        public static void KeepGroundMagazineHidden()
        {
            if (heldSource == HeldMagazineSource.FromGround && groundMagazine != null)
                HideGroundMagazine(groundMagazine);
        }

        // Tarkov's MakeVisible only covers the renderers the item cached when it spawned (the magazine body); the
        // cartridges are added to the magazine after that, so they're switched off here too and remembered (headset:
        // "the bullets fall to the floor as if they're in a mag")
        private static void HideGroundMagazine(LootItem lootItem)
        {
            if (hiddenRenderersOf != lootItem)
            {
                hiddenGroundRenderers.Clear();
                hiddenRenderersOf = lootItem;
            }
            lootItem.MakeVisible(false);
            lootItem.GetComponentsInChildren(true, rendererScratch);
            foreach (Renderer renderer in rendererScratch)
            {
                if (!renderer.enabled)
                    continue;
                renderer.enabled = false;
                hiddenGroundRenderers.Add(renderer);
            }
        }

        private static void ShowGroundMagazine(LootItem lootItem)
        {
            lootItem.MakeVisible(true);
            if (hiddenRenderersOf == lootItem)
            {
                foreach (Renderer renderer in hiddenGroundRenderers)
                {
                    if (renderer != null)
                        renderer.enabled = true;
                }
            }
            hiddenGroundRenderers.Clear();
            hiddenRenderersOf = null;
        }

        // FIKA: the release has to reach the other players before the move, as in the stow
        private static bool GroundMagazineMoveReady()
        {
            return heldSource != HeldMagazineSource.FromGround || Time.time >= groundMagazineLetGoTime + HandsInteractionController.stowReleaseDelay;
        }

        // The move that takes it off the ground has been sent; Tarkov removes the ground item when it lands
        private static void ExpectGroundMagazineGone()
        {
            unconfirmedGroundMagazine = groundMagazine;
            unconfirmedGroundItem = heldMagazine;
            unconfirmedGroundDeadline = Time.time + 1.5f;
        }

        private static void CheckGroundMagazineMove()
        {
            if (ReferenceEquals(unconfirmedGroundMagazine, null))
                return;
            if (Time.time < unconfirmedGroundDeadline)
                return;
            // Still on the ground holding the same magazine: the move never landed
            if (unconfirmedGroundMagazine != null && unconfirmedGroundMagazine.gameObject.activeInHierarchy && unconfirmedGroundMagazine.Item == unconfirmedGroundItem)
            {
                Plugin.MyLog.LogWarning("[VRReload] the magazine off the ground never moved, showing it again");
                ShowGroundMagazine(unconfirmedGroundMagazine);
            }
            unconfirmedGroundMagazine = null;
            unconfirmedGroundItem = null;
        }

        // A magazine that isn't the gun's own into the off hand, in the reload animation's grip: straight into it (from
        // the rig), or from where startFrom is (picked up off the ground) and eased in
        private static bool HoldNewMagazine(FirearmController firearmController, Weapon weapon, Slot magazineSlot, Magazine magazine,
            HeldMagazineSource source, Transform startFrom)
        {
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazineView == null)
                return false;
            GameObject prop = CreateProp(magazine);
            if (prop == null)
                return false;

            Transform propTransform = prop.transform;
            Transform handBone = HandBone();
            propTransform.SetParent(handBone, worldPositionStays: false);
            Vector3 seatedScale = SeatedScale(prop);
            SetWorldScale(propTransform, Vector3.Scale(magazineView.Bone.lossyScale, seatedScale));
            propWellPointLocal = SeatedWellPointLocal(prop);
            ReloadGrip reloadGrip = FindReloadGrip(firearmController, magazine, prop, propWellPointLocal);
            if (reloadGrip?.insert != null)
            {
                freeHoldPosition = reloadGrip.insert.position;
                freeHoldRotation = Quaternion.Euler(reloadGrip.insert.rotation);
            }
            else
            {
                bool useLastGrab = reuseLastGrabPose && hasLastGrabPose;
                freeHoldPosition = useLastGrab ? lastGrabPosition : newMagazineHoldPosition;
                freeHoldRotation = useLastGrab ? lastGrabRotation : Quaternion.Euler(newMagazineHoldRotation);
            }
            ToOffHandPalm(prop, magazineView.Bone, ref freeHoldPosition, ref freeHoldRotation);
            heldOnRightArm = OffHandOnRightArm;
            // UpdateFreeHold eases it the rest of the way into the grip
            if (startFrom != null)
                propTransform.SetPositionAndRotation(startFrom.position, startFrom.rotation);
            else
                SetPoseIn(handBone, propTransform, freeHoldPosition, freeHoldRotation);

            // Where it will sit in the well (ContainerCollectionView.SlotView.InsertItem), for the track
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            seatedLocalPosition = modPlacer != null ? modPlacer.ModPosition : Vector3.zero;
            seatedLocalRotation = modPlacer != null ? Quaternion.Euler(modPlacer.ModRotation) : Quaternion.Euler(90f, 0f, 0f);
            railAxisInWell = RailAxisInWell(firearmController, prop, seatedScale);
            heldRockIn = RockInOf(firearmController);

            heldSource = source;
            heldMagazine = magazine;
            heldForFirearm = firearmController;
            heldProp = prop;
            heldReloadGrip = reloadGrip;
            magazineWell = magazineView.Bone;
            magazineLeftWell = true;
            onRail = false;
            railCatchArmed = true;
            magazineHandFingers = reloadGrip?.insert?.fingers ?? MAGAZINE_INSERT_FINGERS;
            return true;
        }

        // Tarkov's reload pick (FirearmHandsInputTranslator.ReloadExternalMagazine): the fullest reachable magazine that fits
        private static Magazine FindMagazineToReload(Weapon weapon, Slot magazineSlot)
        {
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            Magazine magazineInGun = weapon.GetCurrentMagazine();
            reachableMagazines.Clear();
            inventoryController.GetReachableItemsOfTypeNonAlloc(reachableMagazines, (Magazine magazine) => magazine.Count > 0);
            reachableMagazines.Sort((Magazine first, Magazine second) => second.Count.CompareTo(first.Count));
            foreach (Magazine magazine in reachableMagazines)
            {
                if (magazine == magazineInGun)
                    continue;
                if (ItemManipulator.CheckMoveIgnoringTargetItem(magazine, magazineSlot, inventoryController).Succeeded)
                    return magazine;
            }
            return null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Holding

        private static bool HoldIsStillValid()
        {
            if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null)
                return false;
            if (VRGlobals.firearmController == null || VRGlobals.firearmController != heldForFirearm)
                return false;
            // Off the ground: still lying there as the same magazine (FIKA: nobody else took it)
            if (heldSource == HeldMagazineSource.FromGround && (groundMagazine == null || groundMagazine.Item != heldMagazine))
                return false;
            // The gun went from one hand to the other: the arm holding it isn't the off hand's any more (it goes back where it came from)
            if (heldOnRightArm != OffHandOnRightArm)
                return false;
            return heldProp != null && magazineWell != null && heldMagazine != null;
        }

        private static void UpdateHeldMagazine(bool gripReleased, bool releaseLetGo)
        {
            if (onRail)
                UpdateRail(gripReleased, releaseLetGo);
            else
                UpdateFreeHold(gripReleased);
        }

        // Off the track: the magazine settles into the animation's grip, and gets caught by the track near the well
        private static void UpdateFreeHold(bool gripReleased)
        {
            Transform propTransform = heldProp.transform;
            Transform handBone = HandBone();
            float ease = 1f - Mathf.Exp(-gripEaseRate * Time.deltaTime);
            GetPoseIn(handBone, propTransform, out Vector3 handLocalPosition, out Quaternion handLocalRotation);
            SetPoseIn(handBone, propTransform, Vector3.Lerp(handLocalPosition, freeHoldPosition, ease),
                Quaternion.Slerp(handLocalRotation, freeHoldRotation, ease));

            if (gripReleased)
            {
                ReleaseHeldMagazine();
                return;
            }

            // Caught: the magazine's top near the track (up to just past its top), turned roughly the seated way. One that rocks in
            // hooks at its full tilt.
            Vector3 axis = heldRockIn != null ? HookAxisWorld() : RailAxisWorld();
            Vector3 seatedPosition = magazineWell.TransformPoint(seatedLocalPosition);
            Vector3 topOffset = propTransform.TransformPoint(propWellPointLocal) - magazineWell.position;
            float alongTrack = Vector3.Dot(topOffset, axis);
            float offTrack = (topOffset - axis * alongTrack).magnitude;
            Quaternion caughtRotation = heldRockIn != null ? RockRotation(railLength) * seatedLocalRotation : seatedLocalRotation;
            float angle = Quaternion.Angle(propTransform.rotation, magazineWell.rotation * caughtRotation);
            bool pistolTrack = heldRockIn == null && VRGlobals.vrPlayer.isWeapPistol;
            float catchReach = pistolTrack ? pistolCatchReach : railLength + railExitMargin;
            float catchRadius = pistolTrack ? pistolCatchRadius : railCaptureRadius;
            bool inCatchZone = alongTrack >= -0.005f && alongTrack <= catchReach && offTrack <= catchRadius && angle <= railCaptureAngle;
            if (!railCatchArmed)
            {
                railCatchArmed = !inCatchZone && topOffset.magnitude >= railCatchClearance;
                return;
            }
            // A new magazine only with the well empty (the gun's own, out in the hand, still counts as in it for Tarkov)
            if (inCatchZone && (heldSource == HeldMagazineSource.FromGun || heldForFirearm.Item.GetCurrentMagazine() == null))
            {
                // Relative to the controller: on the track the hand follows the magazine, so it can't be the source
                GetPoseIn(VRGlobals.vrPlayer.LeftHand.transform, propTransform, out railEntryHandPosition, out railEntryHandRotation);
                // Always from the start of the track, wherever it was caught: from the side, high up, it would start nearly seated
                // and go in through the wall of the well
                StartRail(pullingOut: false, startPosition: InsertTrackLength);
            }
        }

        // How far a magazine going in is from seated when it starts on the track
        private static float InsertTrackLength => heldRockIn == null && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isWeapPistol ? pistolInsertStart : railLength;

        // On the track: the controller's travel along the well's axis moves the magazine; sideways it can't go
        private static void UpdateRail(bool gripReleased, bool releaseLetGo)
        {
            Vector3 axis = RailAxisWorld();
            Vector3 handDelta = VRGlobals.vrPlayer.LeftHand.transform.position - magazineWell.TransformPoint(handAtRailStartInWell);
            float handAlongTrack = Vector3.Dot(handDelta, axis);
            float handOffTrack = (handDelta - axis * handAlongTrack).magnitude;
            if (heldRockIn != null)
                MeasureHandOnRock(out handAlongTrack, out handOffTrack);
            // A pistol's magazine goes in right under the gun hand: the controllers meet (headset), so it goes further than the hand does
            else if (handAlongTrack < 0f && VRGlobals.vrPlayer.isWeapPistol)
                handAlongTrack *= pistolPushGain;
            float trackPosition = railPositionAtStart + handAlongTrack;
            railBlend = Mathf.MoveTowards(railBlend, 1f, Time.deltaTime / Mathf.Max(railCaptureTime, 0.001f));

            if (railPullingOut)
            {
                // Manual Reload: the mag catch holds it seated until B is down; once past the catch it slides freely
                bool heldByCatch = VRSettings.GetManualReloadOn() && !magazineCatchPassed && !MagazineReleaseHeld();
                float pullLength = railStartedPartlyOut ? partlyOutDistance + partlyOutPullLeft : railLength;
                railPosition = Mathf.Clamp(trackPosition, 0f, heldByCatch ? 0f : pullLength);
                if (railPosition >= magazineCatchDistance)
                    magazineCatchPassed = true;

                // The controller away from where the hand holds the magazine: along the track too while the catch holds it
                float handOffMagazine = heldRockIn != null ? HandOffRockingMagazine() : (handDelta - axis * (railPosition - railPositionAtStart)).magnitude;
                if (RockedMagazineLeaves(gripReleased, handOffMagazine))
                    return;
                bool gaveBackToSupport = grabbedWithRelease && releaseLetGo && !magazineCatchPassed;
                if (gripReleased || gaveBackToSupport || handOffMagazine > railLateralEscape)
                {
                    // Let go (or pulled away) before it came free: it stays in the well, and a hand that came off the
                    // support grip goes back to it
                    if (railStartedPartlyOut && railPosition > 0f)
                        ReturnToPartlyOut();
                    else
                        CancelHold();
                    return;
                }
                PlaceOnRail();
                // Rocked back home past the catch, or back up from partly out: the catch holds it again
                if ((railStartedPartlyOut || (heldRockIn != null && magazineCatchPassed)) && railPosition <= 0f)
                {
                    PushedBackIn(gripReleased);
                    return;
                }
                if (!heldByCatch && trackPosition >= pullLength)
                    ComeFreeOfWell();
                return;
            }

            // Rocking in, only coming away from the magazine lets go of it: tilted too far it stays hooked
            bool cameOffTrack = heldRockIn != null ? handOffTrack > rockInInsertEscape
                : handOffTrack > railLateralEscape || trackPosition > InsertTrackLength + railExitMargin;
            if (cameOffTrack)
            {
                LeaveRail();
                return;
            }
            railPosition = Mathf.Clamp(trackPosition, 0f, InsertTrackLength);
            PlaceOnRail();

            bool seated = railPosition <= 0f && railBlend >= 1f;
            if (seated)
            {
                // Seated with the grip still held, the hand stays on it (the bolt release is under it)
                Transform well = magazineWell;
                FirearmController firearmController = heldForFirearm;
                if (heldSource == HeldMagazineSource.FromGun)
                {
                    // The gun's own magazine pushed back in: nothing changed in the inventory
                    PlayGunSound(heldForFirearm, "MagIn", SoundHand.OffHand.Seating);
                    Pulse(SoundHand.OffHand.Seating, 0.6f, 0.06f);
                    CancelHold();
                    if (!gripReleased)
                        BeginSeatedHold(firearmController, well);
                    return;
                }
                if (heldForFirearm.Item.GetCurrentMagazine() == null && heldForFirearm.CurrentOperation is FirearmController.Idling
                    && GroundMagazineMoveReady() && TryInsert())
                {
                    if (!gripReleased)
                        BeginSeatedHold(firearmController, well);
                    return;
                }
            }

            if (gripReleased)
            {
                // Let go part way into the well: the gun's own magazine stays in it, a new one goes back to the rig
                if (heldSource == HeldMagazineSource.FromGun)
                    CancelHold();
                else
                    ReleaseHeldMagazine();
            }
        }

        // The magazine at railPosition on the track, eased in from where the hand held it when it was caught
        private static void PlaceOnRail()
        {
            Vector3 railPositionWorld;
            Quaternion railRotationWorld;
            if (heldRockIn != null)
            {
                Quaternion rock = RockRotation(railPosition);
                railPositionWorld = magazineWell.TransformPoint(rock * seatedLocalPosition);
                railRotationWorld = magazineWell.rotation * (rock * seatedLocalRotation);
            }
            else
            {
                railPositionWorld = magazineWell.TransformPoint(seatedLocalPosition) + RailAxisWorld() * railPosition;
                railRotationWorld = magazineWell.rotation * seatedLocalRotation;
            }
            Transform propTransform = heldProp.transform;
            if (railBlend >= 1f || railPullingOut)
            {
                propTransform.SetPositionAndRotation(railPositionWorld, railRotationWorld);
                return;
            }
            Transform leftHand = VRGlobals.vrPlayer.LeftHand.transform;
            Vector3 handHeldPosition = leftHand.position + leftHand.rotation * railEntryHandPosition;
            Quaternion handHeldRotation = leftHand.rotation * railEntryHandRotation;
            float blend = Mathf.SmoothStep(0f, 1f, railBlend);
            propTransform.SetPositionAndRotation(Vector3.Lerp(handHeldPosition, railPositionWorld, blend),
                Quaternion.Slerp(handHeldRotation, railRotationWorld, blend));
        }

        // The magazine moves with the gun while it's in the well, and the rendered hand holds it there
        private static void StartRail(bool pullingOut, float startPosition)
        {
            onRail = true;
            railStartedPartlyOut = false;
            railPullingOut = pullingOut;
            railPosition = startPosition;
            railPositionAtStart = startPosition;
            railBlend = pullingOut ? 1f : 0f;
            handAtRailStartInWell = magazineWell.InverseTransformPoint(VRGlobals.vrPlayer.LeftHand.transform.position);
            heldProp.transform.SetParent(magazineWell, worldPositionStays: true);
            LatchHand(heldProp.transform);
            SetLatchPivot(heldProp.transform.TransformPoint(MagazineCenterInProp(heldProp)));
            if (heldRockIn != null)
                StartRockLever();
        }

        // Pulled down the track far enough: loose in the hand
        private static void ComeFreeOfWell()
        {
            if (HoldingPalmedMagazine && !palmedIsOld)
            {
                FinishPalmPull();
                return;
            }
            onRail = false;
            railCatchArmed = false;
            // On its way away from the gun, not back in: passing the well again right after isn't an insert (headset)
            railCatchClearance = railClearDistance;
            magazineLeftWell = true;
            Transform propTransform = heldProp.transform;
            Transform handBone = HandBone();
            propTransform.SetParent(handBone, worldPositionStays: true);
            GetPoseIn(handBone, propTransform, out Vector3 grabPosition, out Quaternion grabRotation);
            // Kept as the left palm's, like every other hold: the next magazine may be in the other hand
            lastGrabPosition = grabPosition;
            lastGrabRotation = grabRotation;
            ToLeftPalm(heldProp, magazineWell, ref lastGrabPosition, ref lastGrabRotation);
            hasLastGrabPose = true;
            if (heldReloadGrip?.pull != null)
            {
                freeHoldPosition = heldReloadGrip.pull.position;
                freeHoldRotation = Quaternion.Euler(heldReloadGrip.pull.rotation);
                ToOffHandPalm(heldProp, magazineWell, ref freeHoldPosition, ref freeHoldRotation);
            }
            else
            {
                freeHoldPosition = grabPosition;
                freeHoldRotation = grabRotation;
            }
            ReleaseHandLatch();
            PlayGunSound(heldForFirearm, "MagOut", SoundHand.OffHand.Seating);
            Pulse(SoundHand.OffHand.Seating, 0.5f, 0.04f);
            if (debugLog)
                LogAction($"[VRReload] {heldMagazine.LocalizedShortName()} out of the well: hand-local position {lastGrabPosition.ToString("F3")} rotation {lastGrabRotation.eulerAngles.ToString("F1")}");
        }

        // A magazine being put in backed out of the track or was pulled off it sideways
        private static void LeaveRail()
        {
            onRail = false;
            railCatchArmed = false;
            railCatchClearance = 0f;
            heldProp.transform.SetParent(HandBone(), worldPositionStays: true);
            ReleaseHandLatch();
        }

        // The hand you see: the arm IK's wrist bone. A held magazine rides it rather than LeftHand, because LeftHand gets
        // the newest controller pose just before rendering, after the IK already placed the hand from the previous one:
        // attached to LeftHand the magazine ran a frame ahead of the hand (headset: "slightly lags in my hand").
        private static Transform HandBone()
        {
            Transform palm = ArmPalm(OffHandOnRightArm);
            if (palm != null)
                return palm;
            if (!OffHandOnRightArm && InitVRPatches.leftPalm != null)
                return InitVRPatches.leftPalm;
            return HandRig(gunHand: false);
        }

        // A magazine's pose in the LEFT palm's frame (every hold the clips have, the defaults, the last grab: Tarkov's reloads only
        // ever hold it in the left hand) -> in the off hand's palm frame, HandBone. The same, or with the gun in the left hand, its
        // mirror image on the right palm.
        private static void ToOffHandPalm(GameObject prop, Transform well, ref Vector3 position, ref Quaternion rotation)
        {
            if (OffHandOnRightArm && PropCentrePlane(prop, well, out Vector3 normal, out Vector3 point))
                MirrorHold(normal, point, leftToRight: true, ref position, ref rotation);
        }

        // The other way: a pose measured on the off hand, kept as the left palm's
        private static void ToLeftPalm(GameObject prop, Transform well, ref Vector3 position, ref Quaternion rotation)
        {
            if (OffHandOnRightArm && PropCentrePlane(prop, well, out Vector3 normal, out Vector3 point))
                MirrorHold(normal, point, leftToRight: false, ref position, ref rotation);
        }

        // The gun's centre plane in a magazine prop's own frame (its normal, and a point on it in metres along the prop's axes). Seated,
        // a magazine is its own mirror image across it, and its well point (where the well bone sits) is on it.
        private static bool PropCentrePlane(GameObject prop, Transform well, out Vector3 normal, out Vector3 point)
        {
            normal = Vector3.right;
            point = Vector3.zero;
            if (prop == null || well == null || !TarkovVR.Patches.Core.Player.RealLeftHandedPatches.GunPlane(out _, out Vector3 gunRight))
                return false;
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            Quaternion seatedRotation = modPlacer != null ? Quaternion.Euler(modPlacer.ModRotation) : Quaternion.Euler(90f, 0f, 0f);
            normal = (Quaternion.Inverse(seatedRotation) * well.InverseTransformDirection(gunRight)).normalized;
            point = Vector3.Scale(SeatedWellPointLocal(prop), prop.transform.lossyScale);
            return true;
        }

        // A thing's pose in one palm's frame -> in the other palm's, for the mirror image of that hold: the palm reflected through the
        // thing's own centre plane (which maps the thing onto itself), turned into the other palm's bone frame (HandMirror.TryMirrorPalm).
        // Worked with the palm at the origin, unturned. False (the pose unchanged) until HandMirror has the hands mesh.
        private static bool MirrorHold(Vector3 normalInThing, Vector3 pointInThing, bool leftToRight, ref Vector3 position, ref Quaternion rotation)
        {
            Vector3 normal = (rotation * normalInThing).normalized;
            Vector3 planePoint = position + rotation * pointInThing;
            if (!TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(Quaternion.identity, normal, rightToLeft: !leftToRight, out Quaternion palmRotation))
                return false;
            Vector3 palmPosition = 2f * Vector3.Dot(planePoint, normal) * normal;
            Quaternion toPalm = Quaternion.Inverse(palmRotation);
            position = toPalm * (position - palmPosition);
            rotation = toPalm * rotation;
            return true;
        }

        // A pose in a transform's frame without its scale (the grip data is in metres in the hand's frame)
        private static void GetPoseIn(Transform frame, Transform target, out Vector3 position, out Quaternion rotation)
        {
            Quaternion inverseFrame = Quaternion.Inverse(frame.rotation);
            position = inverseFrame * (target.position - frame.position);
            rotation = inverseFrame * target.rotation;
        }

        private static void SetPoseIn(Transform frame, Transform target, Vector3 position, Quaternion rotation)
        {
            target.SetPositionAndRotation(frame.position + frame.rotation * position, frame.rotation * rotation);
        }

        private static Vector3 RailAxisWorld()
        {
            return magazineWell.TransformDirection(railAxisInWell).normalized;
        }

        // A pistol's comes from its clip: its magazine goes down the raked grip, 14 to 20 degrees back from the mesh's upright box
        // (headset, Glock: along that box it came out through the front of the grip)
        private static Vector3 RailAxisInWell(FirearmController firearmController, GameObject prop, Vector3 seatedScale)
        {
            LoadRecordedGrips();
            return wellAxes.TryGetValue(AnimationFamily(firearmController), out Vector3 axis) ? axis : RailAxisInWell(prop, seatedScale);
        }

        // The track out of the well, in the well bone's frame: the magazine's own length axis, seated. Needs propWellPointLocal
        // and seatedLocalRotation set. Tried and wrong on the M4A1 (headset): the well bone toward the magazine's middle
        // came out too upright (the bone isn't on the magazine's centre line), and the animation's insert path pulled to
        // the left (the clip still rotates the magazine into place over its last centimetres).
        private static Vector3 RailAxisInWell(GameObject prop, Vector3 seatedScale)
        {
            Vector3 towardMiddle = MagazineCenterInProp(prop) - propWellPointLocal;
            Vector3 axisInProp = TryGetMagazineShape(prop, propWellPointLocal, out Vector3 lengthAxis, out _, out _) ? lengthAxis : towardMiddle;
            Vector3 axisInWell = seatedLocalRotation * Vector3.Scale(axisInProp, seatedScale);
            if (debugLog)
            {
                Vector3 middleInWell = seatedLocalRotation * Vector3.Scale(towardMiddle, seatedScale);
                LogAction($"[VRReload] track {(axisInProp == lengthAxis ? "along the magazine's length" : "toward its middle")}, "
                    + $"{Vector3.Angle(axisInWell, middleInWell):0.#} deg off the well-to-middle direction");
            }
            return axisInWell.sqrMagnitude > 1e-8f ? axisInWell.normalized : Vector3.down;
        }

        // The longest side of the magazine body's mesh bounds (mags are modelled upright, so that's straight down the
        // magazine), in the prop's frame, pointing away from the well. The body is the biggest mesh; the rounds inside
        // are separate small ones. Bounds are mesh metadata, so this works on meshes that aren't readable.
        // Not when the longest side runs across the gun (the Beta-C dual drum is wider than it is tall): if it's nowhere near
        // the way the magazine hangs from the well, the side closest to that direction is its length.
        public static float minLengthAxisAlignment = 0.5f;   // cos of how far the longest side may be off the well to body direction
        public static float minBodyOffsetAlongLength = 0.005f; // m the body's middle must sit off the well point along its length to say which end is out
        private static bool TryGetMagazineShape(GameObject prop, Vector3 wellPointInProp, out Vector3 axisInProp, out float length, out float thinnestSide)
        {
            axisInProp = Vector3.zero;
            length = 0f;
            thinnestSide = 0f;
            MeshFilter body = null;
            float bodyVolume = 0f;
            foreach (MeshFilter meshFilter in prop.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter.sharedMesh == null)
                    continue;
                Vector3 size = Vector3.Scale(meshFilter.sharedMesh.bounds.size, meshFilter.transform.lossyScale);
                float volume = Mathf.Abs(size.x * size.y * size.z);
                if (volume > bodyVolume)
                {
                    bodyVolume = volume;
                    body = meshFilter;
                }
            }
            if (body == null)
                return false;

            Bounds bounds = body.sharedMesh.bounds;
            Vector3 bodySize = Vector3.Scale(bounds.size, body.transform.lossyScale);
            bodySize = new Vector3(Mathf.Abs(bodySize.x), Mathf.Abs(bodySize.y), Mathf.Abs(bodySize.z));
            int lengthSide = 2;
            if (bodySize.x >= bodySize.y && bodySize.x >= bodySize.z)
                lengthSide = 0;
            else if (bodySize.y >= bodySize.z)
                lengthSide = 1;

            Transform propTransform = prop.transform;
            Vector3 towardBody = propTransform.InverseTransformPoint(body.transform.TransformPoint(bounds.center)) - wellPointInProp;
            Vector3[] sides =
            {
                propTransform.InverseTransformDirection(body.transform.right),
                propTransform.InverseTransformDirection(body.transform.up),
                propTransform.InverseTransformDirection(body.transform.forward),
            };
            if (towardBody.sqrMagnitude > 1e-6f && Mathf.Abs(Vector3.Dot(sides[lengthSide].normalized, towardBody.normalized)) < minLengthAxisAlignment)
            {
                float closest = -1f;
                for (int side = 0; side < sides.Length; side++)
                {
                    float alignment = Mathf.Abs(Vector3.Dot(sides[side].normalized, towardBody.normalized));
                    if (alignment > closest)
                    {
                        closest = alignment;
                        lengthSide = side;
                    }
                }
            }

            Vector3 axis = sides[lengthSide];
            if (axis.sqrMagnitude < 1e-8f)
                return false;
            // Which end hangs out of the well: the body's middle is well below the well point on almost every magazine. Not on one
            // modelled centred on its own origin with no ModPlacer (the MP7's 20-round: its body's middle IS the well point), where
            // the test fell to the mesh's own axis, toward its top: the track pointed up into the gun and never caught it (headset:
            // "It wasnt locking onto a track at all", the 40-round fine). Then out is the seated well bone's -z, as in every
            // straight-out rifle's own clip (Tools/gen_reload_holds wellAxis).
            float bodyAlong = Vector3.Dot(axis.normalized, towardBody);
            if (Mathf.Abs(bodyAlong) >= minBodyOffsetAlongLength)
            {
                if (bodyAlong < 0f)
                    axis = -axis;
            }
            else if ((SeatedRotation(prop) * Vector3.Scale(axis, SeatedScale(prop))).z > 0f)
                axis = -axis;
            axisInProp = axis.normalized;
            length = lengthSide == 0 ? bodySize.x : lengthSide == 1 ? bodySize.y : bodySize.z;
            thinnestSide = Mathf.Min(bodySize.x, Mathf.Min(bodySize.y, bodySize.z));
            return true;
        }

        private static Vector3 MagazineCenterInProp(GameObject prop)
        {
            Renderer[] renderers = prop.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return Vector3.zero;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return prop.transform.InverseTransformPoint(bounds.center);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The rendered hand on what it holds (a magazine on the track, the charging handle): it rides that, from where
        // the controller grabbed it

        private static Transform LatchedHandRig => latchedHandRig != null ? latchedHandRig : HandRig(latchedHandIsGunHand);

        private static LimbIK LatchedArmIk => ArmIk(latchedOnRightArm);

        // The latch on that arm, for RealLeftHandedPatches (the gun in the left hand): it leaves that arm's target alone
        internal static Transform ArmLatch(bool rightArm)
        {
            return handLatched && handLatch != null && latchedOnRightArm == rightArm ? handLatch : null;
        }

        private static void LatchHand(Transform holder, bool gunHand = false)
        {
            if (handLatch == null)
                handLatch = new GameObject("VRReloadHandLatch").transform;
            latchedHandIsGunHand = gunHand;
            latchedOnRightArm = OnRightArm(gunHand);
            latchedHandRig = HandRig(gunHand);
            Transform handRig = LatchedHandRig;
            handLatch.SetParent(holder, worldPositionStays: false);
            handLatch.SetPositionAndRotation(handRig.position, handRig.rotation);
            latchBaseLocalPosition = handLatch.localPosition;
            latchBaseLocalRotation = handLatch.localRotation;
            latchControllerAtGrabInHolder = Quaternion.Inverse(holder.rotation) * handRig.rotation;
            latchPivotInHolder = Vector3.zero;      // the holder's own point; the grabs that know better set it after
            latchPivotInLatch = handLatch.InverseTransformPoint(holder.position);
            latchHolder = holder;
            handLatched = true;
            handLatchReleasing = false;
            handLatchSnapping = false;
            AssertHandLatch();
        }

        // The point the latched hand holds, the wiggle's pivot (LatchHand's default: the holder's own origin)
        private static void SetLatchPivot(Vector3 worldPoint)
        {
            if (handLatched && handLatch != null && handLatch.parent != null)
            {
                latchPivotInHolder = handLatch.parent.InverseTransformPoint(worldPoint);
                latchPivotInLatch = handLatch.InverseTransformPoint(worldPoint);
            }
        }

        // The latch moved on to another part, keeping its world pose (BeginSeatedHold puts it on the magazine well as the magazine seats):
        // its base, the controller's reference turn and the pivot are re-expressed there from where it is now. Written against the old
        // part's frame, the base turned the hand ("as soon as the mag goes in, wrist twists 90 degrees", from the wiggle's round on). The
        // pivot comes from the hand, never the old part: that's the magazine prop, already back in Tarkov's pool somewhere else (taken from
        // it, the pivot landed far off and the wiggle flung the hand: "My hand goes flying off now when the mag is seated").
        private static void RebaseLatch()
        {
            Transform holder = handLatch.parent;
            Vector3 pivotWorld = handLatch.TransformPoint(latchPivotInLatch);
            latchHolder = holder;
            handLatchSnapping = false;
            if (holder == null)
                return;
            latchBaseLocalPosition = handLatch.localPosition;
            latchBaseLocalRotation = handLatch.localRotation;
            Transform controller = LatchedHandRig;
            if (controller != null)
                latchControllerAtGrabInHolder = Quaternion.Inverse(holder.rotation) * controller.rotation;
            latchPivotInHolder = holder.InverseTransformPoint(pivotWorld);
        }

        // The controller's turn since the grab, in the holder's frame (so the gun turning doesn't count), capped at handWiggleDegrees, turns
        // the latched hand about the point it holds
        private static void ApplyLatchWiggle()
        {
            Transform holder = handLatch.parent;
            // The right hand on the MDR's deflector follows Tarkov's clip, whose roll is part of the opening itself
            if (handWiggleDegrees <= 0f || holder == null || (HoldingDeflector && deflectorOnRightArm))
                return;
            Transform controller = LatchedHandRig;
            if (controller == null)
                return;
            Quaternion turnInHolder = Quaternion.Inverse(holder.rotation) * controller.rotation * Quaternion.Inverse(latchControllerAtGrabInHolder);
            turnInHolder = Quaternion.RotateTowards(Quaternion.identity, turnInHolder, handWiggleDegrees);
            Quaternion wiggle = holder.rotation * turnInHolder * Quaternion.Inverse(holder.rotation);
            Vector3 pivot = holder.TransformPoint(latchPivotInHolder);
            // The pivot in the hand's own frame, from the unwiggled pose: what a rebase starts from if the latch is moved on
            latchPivotInLatch = handLatch.InverseTransformPoint(pivot);
            // A pivot far off the hand is a stale one: no wiggle rather than swinging the hand about it
            if ((pivot - handLatch.position).sqrMagnitude > latchWigglePivotMaxDistance * latchWigglePivotMaxDistance)
                return;
            handLatch.SetPositionAndRotation(pivot + wiggle * (handLatch.position - pivot), wiggle * handLatch.rotation);
        }

        // The animation holds the magazine at gripPosition / gripRotation in the hand's (IK target's) frame: move the
        // latched hand there, so the fingers wrap the magazine instead of sitting wherever the controller grabbed it
        private static void SnapHandToGrip(Vector3 gripPosition, Vector3 gripRotation)
        {
            Transform propTransform = heldProp.transform;
            Quaternion gripTurn = Quaternion.Euler(gripRotation);
            ToOffHandPalm(heldProp, magazineWell, ref gripPosition, ref gripTurn);
            Quaternion handRotation = propTransform.rotation * Quaternion.Inverse(gripTurn);
            SnapHandTo(propTransform, propTransform.position - handRotation * gripPosition, handRotation);
        }

        // The latched hand eases to this world pose and rides what it's latched to from there
        private static void SnapHandTo(Transform holder, Vector3 handPosition, Quaternion handRotation)
        {
            if (!handLatched || handLatch == null)
                return;
            handSnapLocalPosition = holder.InverseTransformPoint(handPosition);
            handSnapLocalRotation = Quaternion.Inverse(holder.rotation) * handRotation;
            handLatchSnapping = true;
        }

        // The hand eases from the magazine back to the controller, then the arm goes back to LeftHand
        private static void ReleaseHandLatch()
        {
            if (!handLatched)
                return;
            handLatchReleasing = true;
            handLatchSnapping = false;
            if (handLatch != null)
                handLatch.SetParent(null, worldPositionStays: true);
        }

        private static void ReleaseHandLatchNow()
        {
            if (handLatch != null)
                handLatch.SetParent(null, worldPositionStays: true);
            LimbIK armIk = LatchedArmIk;
            if (handLatched && armIk != null && VRGlobals.vrPlayer != null && armIk.solver.target == handLatch)
                armIk.solver.target = LatchedHandRig;
            handLatched = false;
            handLatchReleasing = false;
        }

        // Re-asserted every frame: the two-handing edges in VRPlayerManager set the target back to LeftHand
        private static void AssertHandLatch()
        {
            LimbIK armIk = LatchedArmIk;
            if (armIk == null || handLatch == null)
                return;
            if (armIk.solver.target != handLatch)
                armIk.solver.target = handLatch;
        }

        private static void UpdateHandLatch()
        {
            if (!handLatched)
                return;
            if (VRGlobals.vrPlayer == null || handLatch == null)
            {
                ReleaseHandLatchNow();
                return;
            }
            // The gun went from one hand to the other under it: that arm is someone else's now (the sling frees the right one, the
            // equip path re-stamps it), so it comes off the latch at once with no target of ours left on it
            if (latchedOnRightArm != OnRightArm(latchedHandIsGunHand))
            {
                handLatch.SetParent(null, worldPositionStays: true);
                LimbIK armIk = LatchedArmIk;
                if (armIk != null && armIk.solver.target == handLatch)
                    armIk.solver.target = null;
                handLatched = false;
                handLatchReleasing = false;
                handLatchSnapping = false;
                return;
            }
            if (handLatchReleasing)
            {
                // The foregrip grab took the arm while the hand was still easing back (VRPlayerManager's two-handing edge gives
                // it to Tarkov's own left-hand IK: target null). It goes there now: asserting ours on top, then handing the
                // arm to LeftHand when the ease ended, left the hand following the controller up the gun instead of
                // locking onto the foregrip. (The ease never ends while the hand moves: it trails the controller.)
                // The gun hand: taking the grip back gives its arm to the gun the same way (ChestHang.Reattach).
                if (latchedHandIsGunHand ? !ChestHang.Detached : VRGlobals.vrPlayer.isSupporting)
                {
                    handLatch.SetParent(null, worldPositionStays: true);
                    LimbIK armIk = LatchedArmIk;
                    if (armIk != null && armIk.solver.target == handLatch)
                        armIk.solver.target = null;
                    handLatched = false;
                    handLatchReleasing = false;
                    return;
                }
                Transform handRig = LatchedHandRig;
                float ease = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(handReleaseTime / 3f, 0.001f));
                handLatch.position = Vector3.Lerp(handLatch.position, handRig.position, ease);
                handLatch.rotation = Quaternion.Slerp(handLatch.rotation, handRig.rotation, ease);
                if ((handLatch.position - handRig.position).sqrMagnitude < 0.003f * 0.003f && Quaternion.Angle(handLatch.rotation, handRig.rotation) < 2f)
                {
                    ReleaseHandLatchNow();
                    return;
                }
            }
            // The stuck case of a failure to eject places the latch itself, fully free (PullStuckCase)
            else if (!(HoldingJamCase && !jamCaseOut))
            {
                // Moved on to another part (or off any) since: carry on from where it is, never the old part's numbers
                if (handLatch.parent != latchHolder)
                    RebaseLatch();
                if (handLatch.parent == null)
                {
                    AssertHandLatch();
                    return;
                }
                if (handLatchSnapping)
                {
                    float ease = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(handSnapTime / 3f, 0.001f));
                    latchBaseLocalPosition = Vector3.Lerp(latchBaseLocalPosition, handSnapLocalPosition, ease);
                    latchBaseLocalRotation = Quaternion.Slerp(latchBaseLocalRotation, handSnapLocalRotation, ease);
                }
                handLatch.localPosition = latchBaseLocalPosition;
                handLatch.localRotation = latchBaseLocalRotation;
                ApplyLatchWiggle();
            }
            AssertHandLatch();
        }

        private static void ReleaseHeldMagazine()
        {
            if (heldSource == HeldMagazineSource.FromPouch)
            {
                // Never left the rig
                PlayGunSound(heldForFirearm, "MagSbrosnik", SoundHand.OffHandAlone);
                DestroyProp();
                ClearHold();
                return;
            }
            if (heldSource == HeldMagazineSource.FromGround)
            {
                // At the chest: into the rig. Anywhere else (or no room): back to the ground from the hand.
                if (InChestPouchZone(OffHandPalm()) && GroundMagazineMoveReady() && TryBuildMoveToRig(heldMagazine, out OperationResult<MoveResult> groundToRig))
                {
                    ExpectGroundMagazineGone();
                    VRGlobals.player.InventoryController.TryRunNetworkTransaction(groundToRig);
                    if (debugLog)
                        LogAction($"[VRReload] {heldMagazine.LocalizedShortName()} off the ground put in the rig");
                    PlayGunSound(heldForFirearm, "MagSbrosnik", SoundHand.OffHandAlone);
                    Pulse(SoundHand.OffHandAlone, 0.4f, 0.05f);
                    DestroyProp();
                    ClearHold();
                }
                else
                {
                    CancelHold();
                }
                return;
            }

            if (!magazineLeftWell || !(heldForFirearm.CurrentOperation is FirearmController.Idling))
            {
                CancelHold();
                return;
            }

            Magazine magazine = heldMagazine;
            FirearmController firearmController = heldForFirearm;
            Vector3 handPosition = heldProp.transform.position;
            Quaternion handRotation = heldProp.transform.rotation;
            bool atChest = InChestPouchZone(OffHandPalm());

            // At the chest: into the rig, or the ground if it's full. Anywhere else: the ground, or the rig where
            // nothing can be dropped (the hideout).
            if (atChest && TryBuildMoveToRig(magazine, out OperationResult<MoveResult> rigMove))
            {
                CommitHold();
                RunMoveToRig(magazine, firearmController, rigMove);
            }
            else if (CanDropToGround(magazine))
            {
                CommitHold();
                RunDrop(magazine, firearmController, handPosition, handRotation, OffHandWorldVelocity());
            }
            else if (!atChest && TryBuildMoveToRig(magazine, out rigMove))
            {
                CommitHold();
                RunMoveToRig(magazine, firearmController, rigMove);
            }
            else
            {
                CancelHold();
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Inventory moves (each starts Tarkov's operation; TryForceExpectedOperation finishes it)

        private static bool TryInsert()
        {
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            Slot magazineSlot = heldForFirearm.Item.GetMagazineSlot();
            OperationResult<MoveResult> moveResult = ItemManipulator.Move(heldMagazine, magazineSlot.CreateItemAddress(), inventoryController, simulate: true);
            if (moveResult.Failed || !moveResult.Value.CanExecute(inventoryController))
            {
                if (debugLog)
                    Plugin.MyLog.LogWarning($"[VRReload] can't seat {heldMagazine.LocalizedShortName()}: {(moveResult.Failed ? moveResult.Error.ToString() : "can't execute")}");
                return false;
            }

            Magazine magazine = heldMagazine;
            FirearmController firearmController = heldForFirearm;
            Weapon weapon = firearmController.Item;
            // Bolt forward on an empty chamber: the round the insert chambers waits for the handle (a locked-back bolt
            // already holds it off). Guns without a handle we can work keep Tarkov's chambering.
            insertNeedsRack = weapon.ChamberAmmoCount == 0 && !firearmController.FirearmsAnimator.GetBoltCatch()
                && ((chargingHandle && TryGetChargingHandle(firearmController, weapon)) || (weapon.BoltAction && ManualBoltOn(firearmController)));
            if (heldSource == HeldMagazineSource.FromGround)
                ExpectGroundMagazineGone();
            CommitHold();

            ExpectOperation(ExpectedOperation.Insert, firearmController);
            inventoryController.TryRunNetworkTransaction(moveResult);
            TryForceExpectedOperation();

            if (debugLog)
                LogAction($"[VRReload] seated {magazine.LocalizedShortName()}");
            PlayGunSound(firearmController, "MagIn", SoundHand.OffHand.Seating);
            Pulse(SoundHand.OffHand.Seating, 0.8f, 0.07f);
            return true;
        }

        // Tarkov's unload (FirearmHandsInputTranslator.UnloadMag): the smallest rig/pocket grid that fits
        private static bool TryBuildMoveToRig(Magazine magazine, out OperationResult<MoveResult> moveResult)
        {
            moveResult = default;
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            GridItemAddress bestAddress = null;
            int bestGridSize = int.MaxValue;
            foreach (var grid in inventoryController.Inventory.Equipment.GetPrioritizedGridsForUnloadedObject())
            {
                GridItemAddress address = grid.FindLocationForItem(magazine);
                if (address == null)
                    continue;
                int gridSize = address.Grid.GridWidth * address.Grid.GridHeight;
                if (gridSize < bestGridSize)
                {
                    bestGridSize = gridSize;
                    bestAddress = address;
                }
            }
            if (bestAddress == null)
                return false;

            moveResult = ItemManipulator.Move(magazine, bestAddress, inventoryController, simulate: true);
            return !moveResult.Failed && moveResult.Value.CanExecute(inventoryController);
        }

        private static void RunMoveToRig(Magazine magazine, FirearmController firearmController, OperationResult<MoveResult> moveResult)
        {
            ExpectOperation(ExpectedOperation.PullOut, firearmController);
            VRGlobals.player.InventoryController.TryRunNetworkTransaction(moveResult);
            TryForceExpectedOperation();

            if (debugLog)
                LogAction($"[VRReload] {magazine.LocalizedShortName()} put in the rig");
            PlayGunSound(firearmController, "MagSbrosnik", SoundHand.OffHandAlone);
            Pulse(SoundHand.OffHandAlone, 0.4f, 0.05f);
        }

        // Tarkov only drops to the ground in a raid (ItemUiContext.UnloadWeapon); in the hideout FIKA's item sync also
        // has no world to report to and throws every physics step
        private static bool CanDropToGround(Magazine magazine)
        {
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || gameWorld is HideoutGameWorld)
                return false;
            return VRGlobals.player.InventoryController.CanThrow(magazine);
        }

        private static void RunDrop(Magazine magazine, FirearmController firearmController, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            pendingDropMagazine = magazine;
            pendingDropPosition = position;
            pendingDropRotation = rotation;
            pendingDropVelocity = velocity;
            pendingDropDeadline = Time.time + 3f;

            ExpectOperation(ExpectedOperation.PullOut, firearmController);
            VRGlobals.player.InventoryController.ThrowItem(magazine, false, null);
            TryForceExpectedOperation();

            if (debugLog)
                LogAction($"[VRReload] {magazine.LocalizedShortName()} dropped");
        }

        // B (Y left-handed): the magazine falls out of the well (into the rig in the hideout; a pistol's slides partly out while
        // B is held and falls when it's let go, see VRReloadController.PartlyOut.cs). Returns false
        // when it doesn't apply, so B keeps its old job on that gun.
        public static bool TryReleaseMagazine()
        {
            // The gun's magazine is already in the off hand. A new one in the off hand is fine: drop the old, then seat.
            if (HoldingGunMagazine)
                return true;
            if (!TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None
                || ChestHang.Detached || weapon.MalfState.State == Weapon.EMalfunctionState.Feed)
                return false;
            Magazine magazine = weapon.GetCurrentMagazine();
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazine == null || magazineView == null || magazineView.ItemView == null)
                return false;

            if (CanDropToGround(magazine))
            {
                // Falls out of the well with the gun's motion
                PlayReleaseSounds(firearmController);
                RunDrop(magazine, firearmController, magazineView.ItemView.position, magazineView.ItemView.rotation,
                    Vector3.down * 0.5f + VRGlobals.player.Velocity);
                return true;
            }
            if (TryBuildMoveToRig(magazine, out OperationResult<MoveResult> rigMove))
            {
                PlayReleaseSounds(firearmController);
                RunMoveToRig(magazine, firearmController, rigMove);
                return true;
            }
            return false;
        }

        // Manual Reload already played the button when B went down
        private static void PlayReleaseSounds(FirearmController firearmController)
        {
            if (!VRSettings.GetManualReloadOn())
                PlayGunSound(firearmController, "Button", ReleasePressingHand(firearmController));
            // Falls out of the gun the gun hand holds
            PlayGunSound(firearmController, "MagOut", SoundHand.GunBody);
        }

        private static SoundHand ReleasePressingHand(FirearmController firearmController)
        {
            return OffHandPressesCatch(firearmController) ? SoundHand.OffHand : SoundHand.GunHand;
        }

        // Called from the GameWorld.ThrowItem postfix: put our dropped magazine where the hand let go of it
        public static void PlaceDroppedMagazine(Item item, LootItem lootItem)
        {
            if (item == null || lootItem == null || item != pendingDropMagazine)
                return;
            pendingDropMagazine = null;
            if (Time.time > pendingDropDeadline)
                return;

            lootItem.transform.SetPositionAndRotation(pendingDropPosition, pendingDropRotation);
            Rigidbody rigidBody = lootItem.RigidBody;
            if (rigidBody != null)
            {
                rigidBody.velocity = pendingDropVelocity;
                rigidBody.angularVelocity = UnityEngine.Random.insideUnitSphere * dropAngularSpeed;
            }
            // Tarkov hides a thrown item for a moment so it doesn't pop in at the body; this one is where the hand let go.
            // (Its hide only covers the magazine body, so the cartridges showed on their own: "just the bullets".)
            lootItem.MakeVisible(true);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Finishing Tarkov's operation the same frame (skips its animation; see firearm-operations-reference)

        private static void ExpectOperation(ExpectedOperation operation, FirearmController firearmController)
        {
            expectedOperation = operation;
            expectedForFirearm = firearmController;
            expectedOperationDeadline = Time.time + 2f;
            expectedOperationStartFrame = Time.frameCount;
        }

        private static void TryForceExpectedOperation()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController != expectedForFirearm)
            {
                ClearPending();
                expectedOperation = ExpectedOperation.None;
                return;
            }

            try
            {
                FirearmController.FirearmOperation operation = firearmController.CurrentOperation;
                if (expectedOperation == ExpectedOperation.PullOut && operation is FirearmController.PullOutMagOperation)
                {
                    // Visible again right before RemoveMod returns it to the pool, so the pool gets it as it found it
                    RestorePendingView();
                    // BSG's own skip: runs the remaining mag-out callbacks in order, then plays idle
                    firearmController.FastForwardCurrentState();
                    ReturnPendingProp();
                    FinishForcedOperation(firearmController);
                }
                else if (expectedOperation == ExpectedOperation.Insert && operation is FirearmController.InsertMagOperation insertOperation)
                {
                    // Not BSG's FastForward: it can switch to idle twice when there's no round to chamber
                    insertOperation.OnMagAppeared();
                    ReturnPendingProp();
                    insertOperation.OnMagInsertedToWeapon();
                    if (insertOperation.State != EFT.Player.EOperationState.Finished)
                        insertOperation.OnAddAmmoInChamber();
                    firearmController.FirearmsAnimator.Animator.Play(firearmController.FirearmsAnimator.FullIdleStateName, 1, 0.1f);
                    FinishForcedOperation(firearmController);
                    // Here, not in TryInsert: the move lands a frame later, and only then has Tarkov chambered the round
                    // (InsertMagResult.Run, as this operation starts)
                    if (insertNeedsRack && firearmController.Item.ChamberAmmoCount > 0)
                    {
                        weaponsWaitingForRack.Add(firearmController.Item);
                        LogAction($"[VRReload] {firearmController.Item.LocalizedShortName()}: magazine in on an empty chamber, the round waits for the handle");
                    }
                    insertNeedsRack = false;
                }
                else if (expectedOperation == ExpectedOperation.Rechamber && operation is FirearmController.RechamberOperation)
                {
                    // BSG's own skip, guarded by its state: the chambered round is thrown out, the next one chambered, the
                    // bolt catch set. Unlike the magazine skips it never sends the arms back to idle, so the rechamber
                    // clip it started would play on (headset: "it still plays the base game charge handle pull").
                    firearmController.FastForwardCurrentState();
                    firearmController.FirearmsAnimator.Animator.Play(firearmController.FirearmsAnimator.FullIdleStateName, 1, 0.1f);
                    FinishForcedOperation(firearmController);
                }
                else if (Time.time > expectedOperationDeadline)
                {
                    Plugin.MyLog.LogWarning($"[VRReload] expected {expectedOperation} never started (current: {operation?.GetType().Name ?? "none"})");
                    ClearPending();
                    expectedOperation = ExpectedOperation.None;
                }
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[VRReload] forcing {expectedOperation} failed: {exception}");
                ClearPending();
                expectedOperation = ExpectedOperation.None;
            }
        }

        private static void RestorePendingView()
        {
            if (pendingHiddenView != null)
                pendingHiddenView.gameObject.SetActive(true);
            pendingHiddenView = null;
        }

        private static void ReturnPendingProp()
        {
            ReturnPropToPool(pendingProp);
            pendingProp = null;
            pendingPropItem = null;
        }

        // The move never happened: the magazine is still in the gun, show it again
        private static void ClearPending()
        {
            RestorePendingView();
            ReturnPendingProp();
        }

        // ReloadFloat and the "reload in progress" block only clear on Idling's IdleStart event, which the skipped
        // animation never sends
        private static void FinishForcedOperation(FirearmController firearmController)
        {
            if (firearmController.CurrentOperation is FirearmController.Idling idling)
                idling.OnIdleStartEvent();
            if (debugLog)
                LogAction($"[VRReload] finished {expectedOperation} after {Time.frameCount - expectedOperationStartFrame} frames");
            expectedOperation = ExpectedOperation.None;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The magazine in the hand

        // Tarkov's own magazine model, borrowed from its pool. Colliders off (the weapon collision proxy copies nearby
        // colliders) and drawn on the gun's layer; both are put back before it returns to the pool.
        private static GameObject CreateProp(Item item)
        {
            ObjectsFactory objectsFactory = Singleton<ObjectsFactory>.Instance;
            if (objectsFactory == null)
                return null;
            GameObject prop = objectsFactory.CreateItem(item, isAnimated: false);
            if (prop == null)
                return null;

            PropChanges changes = new PropChanges();
            foreach (Collider collider in prop.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled)
                    continue;
                collider.enabled = false;
                changes.disabledColliders.Add(collider);
            }
            int gunLayer = LayerMask.NameToLayer("Player");
            foreach (Transform child in prop.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                changes.originalLayers[child] = child.gameObject.layer;
                child.gameObject.layer = gunLayer;
            }
            propChanges[prop] = changes;
            prop.SetActive(true);
            return prop;
        }

        private static void DestroyProp()
        {
            ReturnPropToPool(heldProp);
            heldProp = null;
        }

        private static void ReturnPropToPool(GameObject prop)
        {
            if (prop == null)
                return;
            // The hand latch rides the magazine on the track; it must never go back to the pool with it
            if (handLatch != null && handLatch.IsChildOf(prop.transform))
                handLatch.SetParent(null, worldPositionStays: true);
            if (propChanges.TryGetValue(prop, out PropChanges changes))
            {
                foreach (Collider collider in changes.disabledColliders)
                {
                    if (collider != null)
                        collider.enabled = true;
                }
                foreach (KeyValuePair<Transform, int> originalLayer in changes.originalLayers)
                {
                    if (originalLayer.Key != null)
                        originalLayer.Key.gameObject.layer = originalLayer.Value;
                }
                propChanges.Remove(prop);
            }
            prop.transform.SetParent(null, worldPositionStays: false);
            AssetPoolObject.ReturnToPool(prop);
        }

        // Where the mag well sits in the prop's frame when seated (ContainerCollectionView.SlotView.InsertItem)
        private static Vector3 SeatedWellPointLocal(GameObject prop)
        {
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            Vector3 seatedPosition = modPlacer != null ? modPlacer.ModPosition : Vector3.zero;
            Quaternion seatedRotation = modPlacer != null ? Quaternion.Euler(modPlacer.ModRotation) : Quaternion.Euler(90f, 0f, 0f);
            Vector3 seatedScale = SeatedScale(prop);
            Vector3 wellInPropFrame = Quaternion.Inverse(seatedRotation) * -seatedPosition;
            return new Vector3(wellInPropFrame.x / seatedScale.x, wellInPropFrame.y / seatedScale.y, wellInPropFrame.z / seatedScale.z);
        }

        // The model's turn on its slot bone seated (SlotView.InsertItem): its ModPlacer's, else Tarkov's default
        private static Quaternion SeatedRotation(GameObject prop)
        {
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            return modPlacer != null ? Quaternion.Euler(modPlacer.ModRotation) : Quaternion.Euler(90f, 0f, 0f);
        }

        private static Vector3 SeatedScale(GameObject prop)
        {
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            if (modPlacer == null || modPlacer.ModScale.x == 0f || modPlacer.ModScale.y == 0f || modPlacer.ModScale.z == 0f)
                return Vector3.one;
            return modPlacer.ModScale;
        }

        private static void SetWorldScale(Transform target, Vector3 worldScale)
        {
            Vector3 parentScale = target.parent != null ? target.parent.lossyScale : Vector3.one;
            target.localScale = new Vector3(worldScale.x / parentScale.x, worldScale.y / parentScale.y, worldScale.z / parentScale.z);
        }

        private static void RestoreGunMagazineView()
        {
            if (hiddenGunMagazineView != null)
                hiddenGunMagazineView.gameObject.SetActive(true);
            hiddenGunMagazineView = null;
        }

        // Committed to an inventory move: the prop and the hidden gun magazine go to the expected operation, which
        // cleans them up when Tarkov finishes (or puts the magazine back if it never does)
        private static void CommitHold()
        {
            pendingProp = heldProp;
            pendingPropItem = heldMagazine;
            pendingHiddenView = hiddenGunMagazineView;
            heldProp = null;
            hiddenGunMagazineView = null;
            ClearHold();
        }

        // Puts everything back as it was: the gun's magazine visible again, nothing in the hand; one picked up off the
        // ground drops from where the hand has it
        private static void CancelHold()
        {
            RestoreGunMagazineView();
            if (heldSource == HeldMagazineSource.FromGround)
                ReturnGroundMagazine();
            DestroyProp();
            ClearHold();
        }

        // The ground item, hidden while our model was held, is put where the model is and let go like a held item
        private static void ReturnGroundMagazine()
        {
            if (groundMagazine == null || groundMagazine.Item != heldMagazine)
                return;
            bool thrown = heldProp != null && VRGlobals.handsInteractionController != null
                && VRGlobals.handsInteractionController.ThrowFromOffHand(groundMagazine, heldProp.transform.position, heldProp.transform.rotation);
            // After the drop: it hides the item for a moment too, and it's already where the hand let go
            ShowGroundMagazine(groundMagazine);
            if (debugLog)
                LogAction($"[VRReload] {heldMagazine.LocalizedShortName()} back to the ground{(thrown ? "" : " (where it lay)")}");
        }

        private static void ClearHold()
        {
            groundMagazine = null;
            ReleaseHandLatch();
            onRail = false;
            heldSource = HeldMagazineSource.None;
            heldMagazine = null;
            heldForFirearm = null;
            heldReloadGrip = null;
            heldRockIn = null;
            magazineWell = null;
            magazineLeftWell = false;
            OwnsOffHand = false;
        }

        // Called from HandsInteractionController.LateUpdate right after the LEFT hand's finger update, so it's the last write
        // to those bones this frame. Eases in on a grab and back out to whatever the hand was doing on release. The off hand on the
        // right arm (the gun in the left hand) is posed in ApplyGunHandFingerPose instead, after the right hand's own finger update;
        // the left arm then carries the gun hand, on the handle / bolt / a round while the right hand holds the gun alone.
        public static void ApplyHeldFingerPose()
        {
            try
            {
                if (OffHandOnRightArm)
                    ApplyGunHandFingers(rightArm: false);
                else
                    ApplyOffHandFingerPose();
            }
            finally
            {
                // A round (or a stuck case) in this hand goes where the fingers now are
                if (HoldingRound && !roundOnRightArm)
                    PlaceHeldRound();
                if (HoldingJamCase && !jamCaseOnRightArm)
                {
                    PlaceJamCase();
                    ThumbOntoCase(rightArm: false);
                }
            }
        }

        // What the off hand's fingers hold, and whether the pose was recorded on the right hand (a gun-hand clip's, for the right arm)
        private static Vector3[] OffHandPose(out bool rightHandPose)
        {
            bool rightArm = OffHandOnRightArm;
            rightHandPose = rightArm;
            // A gun whose charging handle clip hasn't been read yet: the magazine pull's thumb-and-index pinch is the closest grip
            // (the right hand: its fingers hooked over a T)
            if (HoldingChargingHandle && !HandleInGunHand)
                return handleHandFingers ?? (rightArm ? GUN_HAND_HANDLE_FINGERS : MAGAZINE_PULL_FINGERS);
            if (HoldingBolt && !BoltInGunHand)
                return BoltFingerPose(gunHand: false);
            // A round, or the stuck case of a failure to eject (its thumb then put on it, ThumbOntoCase): already the arm's own pose (RoundFingers)
            if ((HoldingRound && !RoundInGunHand) || (HoldingJamCase && !jamCaseInGunHand))
                return RoundFingers(rightArm);
            // The MDR's deflector: the clip's fingers on it
            if (HoldingDeflector && !deflectorInGunHand)
                return DeflectorFingers(rightArm);
            // The magazine: only ever held in the left hand in the clips
            rightHandPose = false;
            if (HoldingMagazine || HoldingSeatedMagazine || HoldingPalmedMagazine)
                return magazineHandFingers ?? MAGAZINE_INSERT_FINGERS;
            return null;
        }

        private static void ApplyOffHandFingerPose()
        {
            latchThumbShown = Mathf.MoveTowards(latchThumbShown, latchThumbTarget, Time.deltaTime / Mathf.Max(releaseButtonTime, 0.001f));
            Vector3[] pose = OffHandPose(out bool rightHandPose);
            if (pose != null)
            {
                fingerPoseShown = pose;
                fingerPoseShownIsRight = rightHandPose;
            }

            float target = pose != null && magazineFingers ? 1f : 0f;
            fingerWeight = Mathf.Lerp(fingerWeight, target, 1f - Mathf.Exp(-fingerBlendRate * Time.deltaTime));
            if (fingerWeight < 0.01f || fingerPoseShown == null)
                return;
            if (OffHandOnRightArm)
            {
                ApplyOffHandPoseOnRight();
                return;
            }
            if (fingerPoseShownIsRight)
                return;

            Transform leftPalm = InitVRPatches.leftPalm;
            if (leftPalm == null)
                return;
            if (leftPalmBones == null || leftPalmBones.Length == 0 || leftPalmBones[0] != leftPalm)
                leftPalmBones = leftPalm.GetComponentsInChildren<Transform>(true);

            int boneCount = Mathf.Min(leftPalmBones.Length, fingerPoseShown.Length);
            for (int i = 1; i < boneCount; i++)
                leftPalmBones[i].localRotation = Quaternion.Slerp(leftPalmBones[i].localRotation, Quaternion.Euler(fingerPoseShown[i]), fingerWeight);
            ApplyLatchThumb(leftPalmBones);
        }

        // A left-hand pose on the left hand's bones the way the off hand's is put on above (its palm's children in order)
        private static void ApplyLeftHandPose(Vector3[] pose, float weight)
        {
            Transform leftPalm = InitVRPatches.leftPalm;
            if (leftPalm == null)
                return;
            if (leftPalmBones == null || leftPalmBones.Length == 0 || leftPalmBones[0] != leftPalm)
                leftPalmBones = leftPalm.GetComponentsInChildren<Transform>(true);
            int boneCount = Mathf.Min(leftPalmBones.Length, pose.Length);
            for (int i = 1; i < boneCount; i++)
                leftPalmBones[i].localRotation = Quaternion.Slerp(leftPalmBones[i].localRotation, Quaternion.Euler(pose[i]), weight);
        }

        // The same for the RIGHT hand, after that hand's own finger update: the gun hand on the charging handle / bolt, or the off hand
        // when it's on the right arm (the gun in the left hand)
        public static void ApplyGunHandFingerPose()
        {
            try
            {
                if (OffHandOnRightArm)
                    ApplyOffHandFingerPose();
                else
                    ApplyGunHandFingers(rightArm: true);
            }
            finally
            {
                // A round (or a stuck case) in this hand goes where the fingers now are
                if (HoldingRound && roundOnRightArm)
                    PlaceHeldRound();
                if (HoldingJamCase && jamCaseOnRightArm)
                {
                    PlaceJamCase();
                    ThumbOntoCase(rightArm: true);
                }
            }
        }

        // The off hand's pose on the right hand's bones: a right-hand pose as it is, a left-hand one (the magazine's, the round's)
        // mirrored, with the off thumb on the magazine catch in it
        private static void ApplyOffHandPoseOnRight()
        {
            Transform palm = ArmPalm(rightArm: true);
            if (palm == null)
                return;
            if (offRightFingerBones == null || offRightPalm != palm)
            {
                offRightPalm = palm;
                offRightFingerBones = FingerBonesInPoseOrder(palm);
            }
            int boneCount = Mathf.Min(offRightFingerBones.Length, fingerPoseShown.Length);
            if (fingerPoseShownIsRight)
            {
                for (int i = 1; i < boneCount; i++)
                {
                    if (offRightFingerBones[i] != null)
                        offRightFingerBones[i].localRotation = Quaternion.Slerp(offRightFingerBones[i].localRotation, Quaternion.Euler(fingerPoseShown[i]), fingerWeight);
                }
                return;
            }
            for (int i = 0; i < leftPoseScratch.Length; i++)
                leftPoseScratch[i] = i < fingerPoseShown.Length ? Quaternion.Euler(fingerPoseShown[i]) : Quaternion.identity;
            if (latchThumbShown > 0f && latchThumbPose != null)
            {
                int firstJoint = 1 + latchFinger * 3;
                for (int joint = 0; joint < latchThumbPose.Length && firstJoint + joint < leftPoseScratch.Length; joint++)
                    leftPoseScratch[firstJoint + joint] = Quaternion.Slerp(leftPoseScratch[firstJoint + joint], Quaternion.Euler(latchThumbPose[joint]), latchThumbShown);
            }
            if (!TarkovVR.Source.Player.Body.HandMirror.TryMirrorPose(leftPoseScratch, rightToLeft: false, rightPoseScratch))
                return;
            boneCount = Mathf.Min(offRightFingerBones.Length, rightPoseScratch.Length);
            for (int i = 1; i < boneCount; i++)
            {
                if (offRightFingerBones[i] != null)
                    offRightFingerBones[i].localRotation = Quaternion.Slerp(offRightFingerBones[i].localRotation, rightPoseScratch[i], fingerWeight);
            }
        }

        // The gun hand on the charging handle / bolt / a round, on its arm: the right normally; the left with the gun in the left hand
        // (the hand's own data then, as the off hand's on that arm: GrabChargingHandle read the left-hand clip grip, BoltFingerPose
        // gives the pinch, a round the pinch as the off hand holds one)
        private static void ApplyGunHandFingers(bool rightArm)
        {
            Vector3[] pose = HandleInGunHand ? handleHandFingers ?? (rightArm ? GUN_HAND_HANDLE_FINGERS : MAGAZINE_PULL_FINGERS) : BoltFingerPose(gunHand: true);
            // A single round: the clip's hold (RoundFingers). The bolt clip's hand on the knob it used first was a closed grip: headset,
            // "the hold position of the bullet on the right hand still is pretty awkward for putting the bullet in".
            if (pose == null && (RoundInGunHand || JamCaseInGunHand))
                pose = RoundFingers(rightArm);
            else if (pose == null && DeflectorInGunHand)
                pose = DeflectorFingers(rightArm);
            if (pose != null)
                gunHandFingerPoseShown = pose;

            float target = pose != null && magazineFingers ? 1f : 0f;
            gunHandFingerWeight = Mathf.Lerp(gunHandFingerWeight, target, 1f - Mathf.Exp(-fingerBlendRate * Time.deltaTime));
            if (gunHandFingerWeight < 0.01f || gunHandFingerPoseShown == null)
                return;
            if (!rightArm)
            {
                ApplyLeftHandPose(gunHandFingerPoseShown, gunHandFingerWeight);
                return;
            }

            LimbIK rightArmIk = VRGlobals.ikManager != null ? VRGlobals.ikManager.rightArmIk : null;
            Transform palm = rightArmIk != null && rightArmIk.solver != null && rightArmIk.solver.bone3 != null ? rightArmIk.solver.bone3.transform : null;
            if (palm == null)
                return;
            if (gunHandFingerBones == null || gunHandPalm != palm)
            {
                gunHandPalm = palm;
                gunHandFingerBones = FingerBonesInPoseOrder(palm);
            }

            int boneCount = Mathf.Min(gunHandFingerBones.Length, gunHandFingerPoseShown.Length);
            for (int i = 1; i < boneCount; i++)
            {
                if (gunHandFingerBones[i] != null)
                    gunHandFingerBones[i].localRotation = Quaternion.Slerp(gunHandFingerBones[i].localRotation, Quaternion.Euler(gunHandFingerPoseShown[i]), gunHandFingerWeight);
            }
        }

        // The palm, then three joints a finger, thumb first. Found by name: the palm also carries attach points.
        private static Transform[] FingerBonesInPoseOrder(Transform palm)
        {
            Transform[] palmBones = palm.GetComponentsInChildren<Transform>(true);
            Transform[] ordered = new Transform[16];
            ordered[0] = palm;
            for (int finger = 1; finger <= 5; finger++)
            {
                for (int joint = 1; joint <= 3; joint++)
                {
                    string boneEnding = "Digit" + finger + joint;
                    ordered[(finger - 1) * 3 + joint] = System.Array.Find(palmBones, palmBone => palmBone.name.EndsWith(boneEnding));
                }
            }
            return ordered;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Hand and zones

        // Which arm draws a hand. With the gun in the left hand (Handedness.GunInLeftHand) the off hand is the right controller, drawn
        // by the right arm: it works the gun with the right hand's clip data (the bolt, the charging handle) or the left hand's mirrored
        // (the magazine and the round: the clips only ever hold those in the left). The input stays the off hand's (Handedness).
        private static bool OnRightArm(bool gunHand) => gunHand != Handedness.GunInLeftHand;
        private static bool OffHandOnRightArm => Handedness.GunInLeftHand;

        private static LimbIK ArmIk(bool rightArm)
        {
            if (VRGlobals.ikManager == null)
                return null;
            return rightArm ? VRGlobals.ikManager.rightArmIk : VRGlobals.ikManager.leftArmIk;
        }

        // An arm's wrist bone: the hand you see
        private static Transform ArmPalm(bool rightArm)
        {
            LimbIK armIk = ArmIk(rightArm);
            return armIk != null && armIk.solver != null && armIk.solver.bone3 != null ? armIk.solver.bone3.transform : null;
        }

        // The rig transform following a hand's controller, in its arm's hand frame: where a latch starts and eases back to. The off hand
        // on the right arm: VRPlayerManager.RightControllerHand (the rig's LeftHand follows the right controller then, in the left
        // hand's frame, and the right arm aims at that one when it's free). The gun hand on the left arm (only off the gun while the
        // right hand holds it alone): LeftControllerHand, for the same reason (the rig's RightHand holds the mirrored gun hold then)
        private static Transform HandRig(bool gunHand)
        {
            if (!gunHand && OffHandOnRightArm)
            {
                Transform rightControllerHand = VRGlobals.vrPlayer.RightControllerHand;
                if (rightControllerHand != null)
                    return rightControllerHand;
            }
            if (gunHand && Handedness.GunInLeftHand)
            {
                Transform leftControllerHand = VRGlobals.vrPlayer.LeftControllerHand;
                if (leftControllerHand != null)
                    return leftControllerHand;
            }
            return (gunHand ? VRGlobals.vrPlayer.RightHand : VRGlobals.vrPlayer.LeftHand).transform;
        }

        // The rendered hand's knuckle line (the loot pointer's palm). The LeftHand rig transform sits at the wrist.
        private static Vector3 OffHandPalm()
        {
            if (VRGlobals.handsInteractionController != null)
                return VRGlobals.handsInteractionController.PalmOrigin(dominant: OffHandOnRightArm);
            Transform leftHand = VRGlobals.vrPlayer.LeftHand.transform;
            return leftHand.position + leftHand.forward * GunInteractionController.interactProbeForward;
        }

        private static Vector3 OffHandWorldVelocity()
        {
            // Controller velocity is tracking-space only; add the body's so a magazine dropped while walking carries on
            Vector3 trackingVelocity = ControllerVelocity.GetSteamVRVelocity(OffHandSource());
            Vector3 handVelocity = VRGlobals.vrOffsetter != null ? VRGlobals.vrOffsetter.transform.TransformDirection(trackingVelocity) : trackingVelocity;
            return handVelocity + VRGlobals.player.Velocity;
        }

        private static bool TryGetGunMagazineCenter(FirearmController firearmController, Slot magazineSlot, out Vector3 magazineCenter)
        {
            magazineCenter = Vector3.zero;
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazineView == null || magazineView.ItemView == null)
                return false;
            // Read fresh each time, not cached: the model and its cartridges come from Tarkov's pools, and a cached list
            // kept cartridges that had gone back to the pool and on into a dropped magazine on the ground, pulling the
            // center (and the grab zone) off the gun
            magazineView.ItemView.GetComponentsInChildren(false, magazineRenderers);
            if (magazineRenderers.Count == 0)
            {
                magazineCenter = magazineView.ItemView.position;
                return true;
            }
            Bounds bounds = magazineRenderers[0].bounds;
            for (int i = 1; i < magazineRenderers.Count; i++)
                bounds.Encapsulate(magazineRenderers[i].bounds);
            magazineCenter = bounds.center;
            return true;
        }

        private static bool TryGetChestPouchCenter(out Vector3 pouchCenter)
        {
            pouchCenter = Vector3.zero;
            if (!ChestHang.HeadPose(out _, out _))
                return false;
            Vector3 offset = chestPouchOffset;
            if (VRSettings.GetLeftHandedMode())
                offset.x = -offset.x;
            // In the body's frame from the neck, like the guns on the body: on the headset, looking down at the pouch swung it forward
            // and down with the head (the HMD pivots ~10 cm about the neck). The offset is from the eyes looking level.
            Quaternion bodyRot = ChestHang.BodyYaw(out Vector3 neck);
            pouchCenter = neck + bodyRot * (offset - Player.VRManager.ShoulderStock.neckLocalOffset);
            return true;
        }

        private static SteamVR_Input_Sources OffHandSource()
        {
            return Handedness.OffHandSource;
        }

        // The off hand working on the gun (the gun hand feels it through the gun)
        private static void Pulse(float amplitude, float duration, float delay = 0f)
        {
            Pulse(SoundHand.OffHand, amplitude, duration, delay);
        }

        private static void Pulse(SoundHand hand, float amplitude, float duration, float delay = 0f)
        {
            if (haptics)
                GunHaptics.Pulse(hand.gunHandActs, hand.feel, amplitude, duration, 120f, delay);
        }

        private static SoundHand HandleSoundHand => handleInGunHand ? SoundHand.GunHand : SoundHand.OffHand;

        // Whichever hand has the charging handle
        private static void PulseHandleHand(float amplitude, float duration)
        {
            Pulse(HandleSoundHand, amplitude, duration);
        }

        private static void PulseGunHand(float amplitude, float duration)
        {
            Pulse(SoundHand.GunHand, amplitude, duration);
        }

        // The gun's own reload sounds, by the names its animation fires (WeaponRecon): Button, MagOut, MagSbrosnik (the dump
        // pouch), MagPullOut (a new magazine from the pouch), MagIn. The gun's sound player resolves "Snd" + name; a gun
        // without that sound stays silent.
        private static void PlayGunSound(FirearmController firearmController, string soundName)
        {
            PlayGunSound(firearmController, soundName, SoundHand.OffHand);
        }

        private static void PlayGunSound(FirearmController firearmController, string soundName, SoundHand hand)
        {
            BaseSoundPlayer soundPlayer = GunSoundPlayerOf(firearmController);
            if (soundPlayer != null)
                PlayFelt(soundPlayer, soundName, hand);
        }

        // The first of these the gun has: guns name the same sound differently (an AR's HandleOut, a pistol's SlideOut, an AK's BoltOut)
        private static void PlayFirstGunSound(FirearmController firearmController, SoundHand hand, params string[] soundNames)
        {
            BaseSoundPlayer soundPlayer = GunSoundPlayerOf(firearmController);
            if (soundPlayer == null)
                return;
            foreach (string soundName in soundNames)
            {
                string eventName = "Snd" + soundName;
                if (soundPlayer.AdditionalSounds.Exists(sound => sound.EventName == soundName || sound.EventName == eventName))
                {
                    PlayFelt(soundPlayer, soundName, hand);
                    return;
                }
            }
        }

        private static BaseSoundPlayer GunSoundPlayerOf(FirearmController firearmController)
        {
            if (!sounds || firearmController == null || firearmController._controllerObject == null)
                return null;
            if (gunSoundPlayerFor != firearmController)
            {
                gunSoundPlayerFor = firearmController;
                gunSoundPlayer = firearmController._controllerObject.GetComponentInChildren<BaseSoundPlayer>(true);
            }
            return gunSoundPlayer;
        }

        // SoundEventHandler plays its clip before returning (BaseSoundPlayer.PlayClip), where OnGunClipPlayed picks it up
        private static void PlayFelt(BaseSoundPlayer soundPlayer, string soundName, SoundHand hand)
        {
            playingSoundHand = hand;
            playingOwnSound = true;
            try
            {
                soundPlayer.SoundEventHandler(soundName);
            }
            finally
            {
                playingOwnSound = false;
            }
            // Co-op: other players hear it from their copy of the gun (their copy skips Tarkov's reload animations and their sounds).
            // Optional: it must never break the sound here.
            if (ModSupport.InstalledMods.FIKAInstalled)
            {
                try { ModSupport.FIKA.VRArmSync.SendReloadSound(soundName); }
                catch (System.Exception exception) { LogAction($"[VRReload] reload sound not sent: {exception.Message}"); }
            }
        }

        // BaseSoundPlayer.PlayClip postfix (VRMagazinePatches): one of our reload sounds also plays on the controller(s) doing
        // the work and the hands on the gun (GunHaptics), live off the audio like the eating sounds (AudioHaptics). The source
        // carries every clip still ringing on it.
        public static void OnGunClipPlayed(BaseSoundPlayer soundPlayer, AudioClip clip)
        {
            if (!playingOwnSound || !haptics || !soundHaptics || soundPlayer == null || soundPlayer != gunSoundPlayer || clip == null)
                return;
            GunHaptics.Gains(playingSoundHand.gunHandActs, playingSoundHand.feel, out float gunGain, out float gunPitch, out float offGain, out float offPitch);
            bool offIsLeft = OffHandSource() == SteamVR_Input_Sources.LeftHand;
            AudioHaptics.OnClipPlayed(soundPlayer._clipsSource, clip,
                offIsLeft ? offGain : gunGain, offIsLeft ? gunGain : offGain,
                offIsLeft ? offPitch : gunPitch, offIsLeft ? gunPitch : offPitch,
                soundHapticsStrength, soundHapticsIdleStop);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Debug spheres

        private static void UpdateZoneViz()
        {
            FirearmController firearmController = null;
            Weapon weapon = null;
            Slot magazineSlot = null;
            bool show = debugZones && TryGetMagazineGun(out firearmController, out weapon, out magazineSlot);
            if (!show)
            {
                SetZoneViz(ref grabZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref pouchZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref wellZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref handleZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref otherKnobZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref sideHandleZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref belowWellZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }

            Vector3 palm = OffHandPalm();
            UpdateChargingHandleViz(firearmController, weapon, show);
            UpdateBelowWellViz(firearmController, magazineSlot);
            bool inGrabZone = InGunMagazineGrabZone(firearmController, weapon, magazineSlot, palm);
            if (!HoldingMagazine && TryGetGunMagazineCapsule(firearmController, magazineSlot, out Vector3 magazineCenter, out Vector3 magazineBottom))
                SetCapsuleZoneViz(ref grabZoneViz, magazineCenter, magazineBottom, magazineGrabRadius, inGrabZone ? new Color(0f, 1f, 0f, 0.3f) : new Color(0f, 0.4f, 1f, 0.3f));
            else
                SetZoneViz(ref grabZoneViz, false, Vector3.zero, 0f, Color.clear);

            bool hasPouchCenter = TryGetChestPouchCenter(out Vector3 pouchCenter);
            SetZoneViz(ref pouchZoneViz, hasPouchCenter, pouchCenter, chestPouchRadius,
                InChestPouchZone(palm) ? new Color(0f, 1f, 0f, 0.25f) : new Color(1f, 0.9f, 0f, 0.25f));

            // The top of the track while holding a magazine: green on it, blue off it
            bool showTrack = HoldingMagazine && magazineWell != null;
            Vector3 trackTop = showTrack ? magazineWell.position + (heldRockIn != null ? HookAxisWorld() : RailAxisWorld()) * InsertTrackLength : Vector3.zero;
            SetZoneViz(ref wellZoneViz, showTrack, trackTop, railCaptureRadius, onRail ? new Color(0f, 1f, 0.4f, 0.4f) : new Color(0f, 0.4f, 1f, 0.4f));
        }

        private static void SetCapsuleZoneViz(ref GameObject capsule, Vector3 start, Vector3 end, float radius, Color color)
        {
            if (capsule == null)
            {
                capsule = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                capsule.name = "VRMagazineZone";
                UnityEngine.Object.Destroy(capsule.GetComponent<Collider>());
                Renderer capsuleRenderer = capsule.GetComponent<Renderer>();
                capsuleRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                capsuleRenderer.receiveShadows = false;
                capsuleRenderer.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Standard"));
            }
            capsule.SetActive(true);
            Vector3 segment = end - start;
            capsule.transform.position = (start + end) * 0.5f;
            if (segment.sqrMagnitude > 1e-8f)
                capsule.transform.rotation = Quaternion.FromToRotation(Vector3.up, segment);
            // Unity's capsule is 1 wide and 2 tall
            capsule.transform.localScale = new Vector3(radius * 2f, (segment.magnitude + radius * 2f) * 0.5f, radius * 2f);
            capsule.GetComponent<Renderer>().material.color = color;
        }

        private static void SetZoneViz(ref GameObject sphere, bool show, Vector3 position, float radius, Color color)
        {
            if (!show)
            {
                if (sphere != null)
                    sphere.SetActive(false);
                return;
            }
            if (sphere == null)
            {
                sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "VRMagazineZone";
                UnityEngine.Object.Destroy(sphere.GetComponent<Collider>());
                Renderer sphereRenderer = sphere.GetComponent<Renderer>();
                sphereRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                sphereRenderer.receiveShadows = false;
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                    shader = Shader.Find("Standard");
                sphereRenderer.material = new Material(shader);
            }
            sphere.SetActive(true);
            sphere.transform.position = position;
            sphere.transform.localScale = Vector3.one * (radius * 2f);
            sphere.GetComponent<Renderer>().material.color = color;
        }
    }
}
