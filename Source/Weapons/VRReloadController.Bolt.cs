using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // A bolt-action rifle's bolt, by hand (Manual Reload). Either hand's trigger takes the knob: the off hand while the gun hand
    // has the grip, the gun hand once it has let go of the grip with the off hand holding the gun up (ChestHang's support hold).
    // The hand turns the handle up about the bolt, then slides it back, forward and down, as on the real gun; let go anywhere and
    // the bolt stays there.
    //  - After a shot Tarkov's own bolt cycle doesn't play (BoltActionPatches skips it at the shot): the spent case stays in and the
    //    trigger is dead until the bolt has been all the way back. Tarkov already counted the next round in the chamber at the shot.
    //  - All the way back: the spent case flies out (Tarkov's own shell); a live round is thrown out by Tarkov's rechamber, which
    //    takes the next one in; a round a draw or a magazine brought in on an empty chamber is still in the magazine (WaitingForRack).
    //  - Closed and turned down: the round in the chamber shows, the trigger works.
    // The bolt is two bones from the gun's own after-shot clip (gen_bolts.py, ReloadHolds.json "bolts"): one slides back, one (or the
    // same, the DVL-10) turns the handle up. We move them from their closed pose while the bolt isn't closed: in Update as the hand
    // moves them (the latched hand reads them this frame) and again after the animator (LateUpdate), which may write them too.
    public static partial class VRReloadController
    {
        public static bool boltAction = true;               // false = Tarkov cycles a bolt-action's bolt itself after every shot
        public static float boltGrabRadius = 0.07f;         // m any point of the hand to the knob (the knob's middle, from its mesh: gen_bolts.py)
        public static float boltFullBack = 0.92f;           // share of the slide that counts as all the way back
        public static float boltTurnedUp = 0.97f;           // share of the turn before it slides
        public static float boltClosedTurn = 0.03f;         // share of the turn left that counts as turned down
        public static bool boltShowsChamberedRound = true;  // a live round in the chamber comes back on the bolt face as it opens (you can see one's in)
        public static float boltTurnSign = 1f;          // -1 if the handle turns the wrong way (the clip's turn read the other way round)
        public static bool boltDebugLog = false;            // the bolt's grabs, strokes and what came out of the chamber (on for its first headset rounds)

        public static bool HoldingBolt { get; private set; }
        public static bool BoltInGunHand => HoldingBolt && boltInGunHand;
        public static bool OffHandAtBolt { get; private set; }

        // No firing: a spent case in the chamber, or the bolt not closed and turned down
        public static bool BoltBlocksFiring
        {
            get
            {
                if (HoldingBolt)
                    return true;
                // Only while the bolt is ours: Manual Reload switched off leaves the gun to Tarkov, never stuck
                FirearmController firearmController = VRGlobals.firearmController;
                return firearmController != null && firearmController.Item != null && boltStates.TryGetValue(firearmController.Item, out BoltState state)
                    && (state.spentCase || !state.Closed) && ManualBoltOn(firearmController);
            }
        }

        private sealed class BoltData
        {
            public string slider;           // the bone that slides back
            public string turner;           // the bone that turns the handle up (the slider itself on the DVL-10)
            public Vector3 travel;          // all the way back, in the slider's parent's frame
            public Vector3 turnAxis;        // in the turner's closed frame
            public float turnDegrees;
            public Vector3 knob;            // where the hand takes it, in the turner's frame (m)
            public HandleHold hold;         // the gun hand on the knob, from the clip (null: it stays where it grabbed)
        }
        private static readonly Dictionary<string, BoltData> boltGuns = new Dictionary<string, BoltData>();

        // Per gun, so a bolt left open, or a case left in, stays that way across a weapon swap
        private sealed class BoltState
        {
            public float turn;              // 0 = down .. 1 = up
            public float slide;             // 0 = forward .. 1 = all the way back
            public bool spentCase;          // fired, the bolt not worked since
            public bool backThisStroke;     // has been all the way back since it was last closed
            public bool roundShown;         // the chambered round's model is already there (a rechamber or a draw put it)
            public bool Closed => slide <= 0f && turn <= boltClosedTurn;
        }
        private static readonly Dictionary<Weapon, BoltState> boltStates = new Dictionary<Weapon, BoltState>();

        private static readonly Dictionary<string, string> boltFamilies = new Dictionary<string, string>();   // by template id: the bolt clip's family

        // The current gun's bolt bones and their closed pose (read while closed: then nothing of ours is on them)
        private static FirearmController boltBonesFor;
        private static BoltData boltBonesData;
        private static Transform boltSlider;
        private static Transform boltTurner;
        private static Transform boltFrame;                 // the slider's parent: the gun's body
        private static Vector3 sliderClosedLocalPosition;
        private static Quaternion turnerClosedLocalRotation;
        private static Vector3 turnerClosedInFrame;         // the turner's closed pose in the gun body's frame (its local units)
        private static Quaternion turnerClosedRotationInFrame;
        private static bool boltClosedPoseRead;
        private static bool drivingBolt;                    // we've moved the bones off their closed pose

        // Our knob, on the turner's pose in the gun body's frame: the hand latches on it, so it rides the gun as the gun moves
        private static Transform boltKnob;
        private static bool boltInGunHand;
        private static FirearmController boltForFirearm;
        private static Vector3 controllerToKnob;            // at the grab, in the gun body's rotation (m)
        private static Vector3[] boltHandFingers;
        private static bool loggedBoltSounds;
        private static GameObject boltZoneViz;

        // The chamber while the bolt is open (from leaving closed until closed again), like the charging handle's: Tarkov's round
        // model sits on the patron_in_weapon bone; we show it and move it back with the slider
        private static FirearmController boltChamberFor;
        private static Transform boltChamberBone;
        private static bool boltChamberBoneWasActive;
        private static Transform boltChamberRound;
        private static Vector3 boltChamberRoundRest;
        private static bool boltChamberEmpty;               // shown empty: the round went out at full travel (the next one stays in the
                                                            // magazine until the bolt closes), or it never left it (WaitingForRack)

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // The bolt clip's family: <family>_fire_chambering_0 (the Sako's <family>_rechamber_0). AnimationFamily reads the magazine clips,
        // which an internal-magazine rifle (the Mosin) has none of.
        private static string BoltFamily(FirearmController firearmController)
        {
            string templateId = firearmController.Item.TemplateId.ToString();
            if (boltFamilies.TryGetValue(templateId, out string family))
                return family;
            family = null;
            RuntimeAnimatorController animatorController = firearmController.FirearmsAnimator?.Animator?.runtimeAnimatorController;
            if (animatorController != null)
            {
                foreach (AnimationClip clip in animatorController.animationClips)
                {
                    if (clip == null)
                        continue;
                    int end = clip.name.IndexOf("_fire_chambering_0", System.StringComparison.OrdinalIgnoreCase);
                    if (end < 0)
                        end = clip.name.IndexOf("_rechamber_0", System.StringComparison.OrdinalIgnoreCase);
                    if (end > 0)
                    {
                        family = clip.name.Substring(0, end).ToLowerInvariant();
                        break;
                    }
                }
            }
            boltFamilies[templateId] = family;
            if (boltDebugLog)
                Plugin.MyLog.LogInfo($"[VRReload] {firearmController.Item.LocalizedShortName()}: bolt clips '{family ?? "none"}', bolt data {(family != null && boltGuns.ContainsKey(family))}");
            return family;
        }

        private static bool TryGetBolt(FirearmController firearmController, out BoltData bolt)
        {
            bolt = null;
            if (firearmController == null || firearmController.Item == null || !firearmController.Item.BoltAction)
                return false;
            LoadRecordedGrips();
            string family = BoltFamily(firearmController);
            return family != null && boltGuns.TryGetValue(family, out bolt);
        }

        private static bool ManualBoltOn(FirearmController firearmController)
        {
            return enabled && boltAction && VRSettings.GetManualReloadOn() && firearmController != null && firearmController._player != null
                && firearmController._player.IsYourPlayer && TryGetBolt(firearmController, out _);
        }

        private static BoltState BoltStateOf(Weapon weapon)
        {
            if (!boltStates.TryGetValue(weapon, out BoltState state))
            {
                state = new BoltState();
                boltStates[weapon] = state;
            }
            return state;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The shot (BoltActionPatches: FireOperation.OnFireEvent, on a bolt-action's fire)

        // True = we take the bolt cycle: the caller skips Tarkov's (its fire operation fast-forwarded to idle, no cycle animation).
        // The shot moved the fired round's model to the shell port; the next round, already in the chamber for Tarkov, isn't shown.
        public static bool BoltShotFired(FirearmController firearmController)
        {
            if (!ManualBoltOn(firearmController) || firearmController.Item.MalfState.State != Weapon.EMalfunctionState.None)
                return false;
            BoltState state = BoltStateOf(firearmController.Item);
            state.spentCase = true;
            state.backThisStroke = false;
            state.roundShown = false;
            if (boltDebugLog)
                Plugin.MyLog.LogInfo($"[VRReload] bolt-action shot: the case stays in, chamber {firearmController.Item.ChamberAmmoCount}");
            return true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // From Tick

        private static void TickBolt(bool offTriggerPressed, bool offTriggerReleased, bool gunTriggerPressed, bool gunTriggerReleased, bool handBusy)
        {
            OffHandAtBolt = false;
            SetZoneViz(ref boltZoneViz, false, Vector3.zero, 0f, Color.clear);
            if (HoldingBolt)
            {
                if (!BoltHoldIsStillValid())
                    LetGoOfBolt();
                else
                    UpdateHeldBolt(boltInGunHand ? gunTriggerReleased : offTriggerReleased);
                return;
            }
            FirearmController firearmController = VRGlobals.firearmController;
            if (!CanWorkBolt(firearmController, out BoltData bolt))
                return;
            Weapon weapon = firearmController.Item;
            // Tarkov's own reload works the bolt in its animation and leaves it closed (not our own forced magazine operations)
            if (firearmController.IsInReloadOperation() && expectedOperation == ExpectedOperation.None)
            {
                ResetBolt(weapon);
                return;
            }
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None)
                return;
            if (!EnsureBoltBones(firearmController, bolt))
                return;
            BoltState state = BoltStateOf(weapon);
            if (!boltClosedPoseRead)
                ReadClosedBoltPose();
            // A bolt left open (before a weapon swap): back where it was
            if (!state.Closed)
                drivingBolt = true;
            if (handBusy)
                return;
            Vector3 knob = KnobWorld(state, bolt);
            // The off hand while the gun hand has the grip (not holding the foregrip: that's two-handing)
            bool offHandAt = !ChestHang.Detached && !VRGlobals.vrPlayer.isSupporting && HandTouchesCapsule(false, knob, knob, boltGrabRadius);
            bool gunHandAt = ChestHang.SupportOnly && HandTouchesCapsule(true, knob, knob, boltGrabRadius);
            OffHandAtBolt = offHandAt;
            // debugZones: the grab zone, green while a hand that may take it is in it
            SetZoneViz(ref boltZoneViz, debugZones, knob, boltGrabRadius, offHandAt || gunHandAt ? new Color(0f, 1f, 0f, 0.3f) : new Color(0.7f, 0f, 1f, 0.3f));
            if (offTriggerPressed && offHandAt)
                GrabBolt(firearmController, bolt, state, gunHand: false);
            else if (gunTriggerPressed && gunHandAt)
                GrabBolt(firearmController, bolt, state, gunHand: true);
        }

        private static bool CanWorkBolt(FirearmController firearmController, out BoltData bolt)
        {
            bolt = null;
            if (!ManualBoltOn(firearmController) || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.LeftHand == null)
                return false;
            if (firearmController.IsInLauncherMode() || firearmController.Item.MalfState.State != Weapon.EMalfunctionState.None)
                return false;
            if (VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.heldItem != null)
                return false;
            return TryGetBolt(firearmController, out bolt);
        }

        private static bool BoltHoldIsStillValid()
        {
            if (!enabled || !boltAction || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null)
                return false;
            if (VRGlobals.firearmController == null || VRGlobals.firearmController != boltForFirearm || boltKnob == null || boltSlider == null)
                return false;
            // The gun hand: only while the off hand holds the gun up; the off hand: only while the gun hand has the grip
            if (boltInGunHand ? !ChestHang.SupportOnly : ChestHang.Detached)
                return false;
            // The gun went from one hand to the other: that arm isn't this hand's any more
            if (latchedOnRightArm != OnRightArm(boltInGunHand))
                return false;
            return boltForFirearm.CurrentOperation is FirearmController.Idling || expectedOperation == ExpectedOperation.Rechamber;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static bool EnsureBoltBones(FirearmController firearmController, BoltData bolt)
        {
            if (boltBonesFor == firearmController && boltBonesData == bolt && boltSlider != null && boltTurner != null && boltFrame != null)
                return true;
            EndBoltChamberView();
            boltBonesFor = firearmController;
            boltBonesData = bolt;
            boltSlider = FindRigBone(firearmController, bolt.slider);
            boltTurner = FindRigBone(firearmController, bolt.turner);
            boltFrame = boltSlider != null ? boltSlider.parent : null;
            boltClosedPoseRead = false;
            drivingBolt = false;
            if (boltSlider == null || boltTurner == null || boltFrame == null)
            {
                if (boltDebugLog)
                    Plugin.MyLog.LogWarning($"[VRReload] bolt bones not found on {firearmController.Item.LocalizedShortName()}: slide '{bolt.slider}' {boltSlider != null}, turn '{bolt.turner}' {boltTurner != null}");
                boltSlider = null;
                boltTurner = null;
                return false;
            }
            if (!loggedBoltSounds && boltDebugLog)
            {
                loggedBoltSounds = true;
                BaseSoundPlayer soundPlayer = GunSoundPlayerOf(firearmController);
                if (soundPlayer != null)
                    LogAction($"[VRReload] {firearmController.Item.LocalizedShortName()} sounds: {string.Join(", ", soundPlayer.AdditionalSounds.ConvertAll(sound => sound.EventName))}");
            }
            return true;
        }

        // The closed pose: only read while nothing of ours is on the bones
        private static void ReadClosedBoltPose()
        {
            if (drivingBolt || boltSlider == null)
                return;
            sliderClosedLocalPosition = boltSlider.localPosition;
            turnerClosedLocalRotation = boltTurner.localRotation;
            turnerClosedInFrame = boltFrame.InverseTransformPoint(boltTurner.position);
            turnerClosedRotationInFrame = Quaternion.Inverse(boltFrame.rotation) * boltTurner.rotation;
            boltClosedPoseRead = true;
        }

        private static Quaternion TurnOf(BoltState state, BoltData bolt)
        {
            return Quaternion.AngleAxis(boltTurnSign * bolt.turnDegrees * state.turn, bolt.turnAxis);
        }

        // The turner where this bolt state puts it, in the gun body's frame: slid back along the slider's travel, turned about its own axis
        private static void TurnerInFrame(float turn, float slide, BoltData bolt, out Vector3 position, out Quaternion rotation)
        {
            position = turnerClosedInFrame + bolt.travel * slide;
            rotation = turnerClosedRotationInFrame * Quaternion.AngleAxis(boltTurnSign * bolt.turnDegrees * turn, bolt.turnAxis);
        }

        // The knob point in the gun body's frame for a turner pose there (the knob is in metres in the turner's frame)
        private static Vector3 KnobInFrame(Vector3 turnerPosition, Quaternion turnerRotation, BoltData bolt)
        {
            return turnerPosition + boltFrame.InverseTransformVector(boltFrame.rotation * (turnerRotation * bolt.knob));
        }

        private static Vector3 KnobWorld(BoltState state, BoltData bolt)
        {
            TurnerInFrame(state.turn, state.slide, bolt, out Vector3 position, out Quaternion rotation);
            return boltFrame.TransformPoint(KnobInFrame(position, rotation, bolt));
        }

        private static void GrabBolt(FirearmController firearmController, BoltData bolt, BoltState state, bool gunHand)
        {
            if (boltKnob == null)
                boltKnob = new GameObject("VRBoltKnob").transform;
            boltKnob.SetParent(boltFrame, worldPositionStays: false);
            PlaceBoltKnob(state, bolt);
            boltForFirearm = firearmController;
            boltInGunHand = gunHand;
            HoldingBolt = true;
            Transform handRig = HandRig(gunHand);
            controllerToKnob = Quaternion.Inverse(boltFrame.rotation) * (KnobWorld(state, bolt) - handRig.position);
            LatchHand(boltKnob, gunHand);
            boltHandFingers = null;
            // The clip's hand on the knob is the right hand's: the gun hand, or the off hand with the gun in the left hand
            if (OnRightArm(gunHand) && bolt.hold != null)
            {
                // The clip's palm on the knob, in the turner's frame: our knob transform is on the turner's pose
                boltHandFingers = bolt.hold.fingers;
                SnapHandTo(boltKnob, boltKnob.position + boltKnob.rotation * bolt.hold.position, boltKnob.rotation * Quaternion.Euler(bolt.hold.rotation));
            }
            if (boltDebugLog)
                Plugin.MyLog.LogInfo($"[VRReload] took the bolt ({(gunHand ? "gun" : "off")} hand), turn {state.turn:0.00} slide {state.slide:0.00}, spent case {state.spentCase}, chamber {firearmController.Item.ChamberAmmoCount}, waiting for rack {WaitingForRack(firearmController.Item)}");
            PulseBoltHand(0.3f, 0.04f);
        }

        private static void PlaceBoltKnob(BoltState state, BoltData bolt)
        {
            TurnerInFrame(state.turn, state.slide, bolt, out Vector3 position, out Quaternion rotation);
            boltKnob.localPosition = position;
            boltKnob.localRotation = rotation;
        }

        private static void UpdateHeldBolt(bool triggerReleased)
        {
            BoltData bolt = boltBonesData;
            Weapon weapon = boltForFirearm.Item;
            BoltState state = BoltStateOf(weapon);
            Transform handRig = LatchedHandRig;
            // Where the controller puts the knob, in the gun body's frame
            Vector3 wantedWorld = handRig.position + boltFrame.rotation * controllerToKnob;
            Vector3 wanted = boltFrame.InverseTransformPoint(wantedWorld);

            float previousTurn = state.turn;
            float previousSlide = state.slide;
            bool wasClosed = state.Closed;
            // Turned up, it slides along its travel; still turning, it goes round its axis
            TurnerInFrame(1f, 0f, bolt, out Vector3 upPosition, out Quaternion upRotation);
            Vector3 knobUp = KnobInFrame(upPosition, upRotation, bolt);
            float distance = bolt.travel.magnitude;
            float along = distance > 0f ? Vector3.Dot(wanted - knobUp, bolt.travel / distance) / distance : 0f;
            if ((state.slide > 0f || state.turn >= boltTurnedUp) && along > 0f)
            {
                state.turn = 1f;
                state.slide = Mathf.Clamp01(along);
            }
            else
            {
                state.slide = 0f;
                Vector3 axis = turnerClosedRotationInFrame * bolt.turnAxis * boltTurnSign;
                Vector3 knobDown = Vector3.ProjectOnPlane(KnobInFrame(turnerClosedInFrame, turnerClosedRotationInFrame, bolt) - turnerClosedInFrame, axis);
                Vector3 toWanted = Vector3.ProjectOnPlane(wanted - turnerClosedInFrame, axis);
                float angle = toWanted.sqrMagnitude > 1e-8f && knobDown.sqrMagnitude > 1e-8f ? Vector3.SignedAngle(knobDown, toWanted, axis) : 0f;
                state.turn = bolt.turnDegrees > 0f ? Mathf.Clamp01(angle / bolt.turnDegrees) : 1f;
            }
            if (!state.Closed)
                drivingBolt = true;
            if (wasClosed && !state.Closed)
                BeginBoltChamberView(boltForFirearm, state);
            PlaceBoltKnob(state, bolt);
            ApplyBoltBones(state, bolt);

            if (previousTurn < boltTurnedUp && state.turn >= boltTurnedUp)
            {
                PlayFirstGunSound(boltForFirearm, BoltSoundHand, "BoltUp", "BoltOpen", "HandleUp");
                PulseBoltHand(0.25f, 0.03f);
            }
            if (!state.backThisStroke && state.slide >= boltFullBack)
            {
                state.backThisStroke = true;
                BoltAllTheWayBack(boltForFirearm, state);
            }
            if (previousSlide > 0f && state.slide <= 0f)
                PulseBoltHand(0.3f, 0.03f);
            if (!wasClosed && state.Closed)
                BoltClosed(boltForFirearm, state);

            // Only letting go of the trigger lets go (his call: no distance release); the bolt stays where it is
            if (triggerReleased)
                LetGoOfBolt();
        }

        // All the way back: what's in the chamber comes out
        private static void BoltAllTheWayBack(FirearmController firearmController, BoltState state)
        {
            Weapon weapon = firearmController.Item;
            PlayFirstGunSound(firearmController, BoltSoundHand, "BoltOut", "BoltBack", "HandleOut", "SlideOut");
            PulseBoltHand(0.5f, 0.04f);
            if (state.spentCase)
            {
                // The case the shot left: Tarkov's own flying shell, from where the shot put it (the shell port)
                state.spentCase = false;
                state.roundShown = false;
                firearmController.Firearms.StartSpawnShell(firearmController._player.Velocity * 0.66f);
                syncShellsThrown++;   // co-op: other players' copies throw theirs now (VRReloadController.Sync)
                if (boltDebugLog)
                    Plugin.MyLog.LogInfo("[VRReload] bolt back: the spent case comes out");
                return;
            }
            // A round a draw or a magazine brought in on an empty chamber is still in the magazine: it goes in as the bolt closes (its
            // model is already in the chamber)
            if (WaitingForRack(weapon))
            {
                state.roundShown = true;
                return;
            }
            if (weapon.ChamberAmmoCount == 0)
                return;
            if (boltDebugLog)
                Plugin.MyLog.LogInfo("[VRReload] bolt back: the chambered round comes out (rechamber)");
            // Tarkov's rechamber throws it out and shows the next one, which is still in the magazine until the bolt closes
            state.roundShown = true;
            boltChamberEmpty = true;
            ExpectOperation(ExpectedOperation.Rechamber, firearmController);
            VRGlobals.player.InventoryController.RechamberWeapon(weapon);
            TryForceExpectedOperation();
        }

        // Forward and turned down: the round in the chamber shows, the gun can fire
        private static void BoltClosed(FirearmController firearmController, BoltState state)
        {
            Weapon weapon = firearmController.Item;
            EndBoltChamberView();
            PlayFirstGunSound(firearmController, BoltSoundHand, "BoltIn", "BoltDown", "HandleIn", "SlideIn");
            PulseBoltHand(0.6f, 0.05f);
            if (state.backThisStroke)
            {
                BoltStripsRound(weapon);
                if (!state.roundShown)
                    ShowChamberedRound(firearmController);
            }
            state.backThisStroke = false;
            state.roundShown = false;
            if (boltDebugLog)
                Plugin.MyLog.LogInfo($"[VRReload] bolt closed, chamber {weapon.ChamberAmmoCount}, spent case {state.spentCase}");
        }

        private static void ShowChamberedRound(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            if (weapon.Chambers == null || weapon.Chambers.Length == 0 || !(weapon.Chambers[0].ContainedItem is Ammo ammo))
                return;
            try
            {
                firearmController.Firearms.SetRoundIntoWeapon(ammo);
                syncRoundsShown++;   // co-op: other players' copies show theirs now (VRReloadController.Sync)
            }
            catch (System.Exception exception)
            {
                if (boltDebugLog)
                    Plugin.MyLog.LogWarning($"[VRReload] showing the chambered round failed: {exception.Message}");
            }
        }

        private static void LetGoOfBolt()
        {
            ReleaseHandLatch();
            HoldingBolt = false;
            FirearmController firearmController = boltForFirearm;
            boltForFirearm = null;
            boltHandFingers = null;
            // Let go turned all but down: it drops the rest of the way
            if (firearmController != null && firearmController.Item != null && boltStates.TryGetValue(firearmController.Item, out BoltState state) && state.Closed)
            {
                state.turn = 0f;
                PutBoltBonesBack();
            }
        }

        // Tarkov's own reload took over the bolt (and closes it)
        private static void ResetBolt(Weapon weapon)
        {
            if (boltStates.TryGetValue(weapon, out BoltState state) && (!state.Closed || state.spentCase))
            {
                EndBoltChamberView();
                state.turn = 0f;
                state.slide = 0f;
                state.spentCase = false;
                state.backThisStroke = false;
                state.roundShown = false;
                PutBoltBonesBack();
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The bones, after the animator (HandsInteractionController.LateUpdate)

        public static void ApplyBolt()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController != boltBonesFor || boltBonesData == null || firearmController.Item == null)
                return;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) && expectedOperation != ExpectedOperation.Rechamber)
                return;
            if (!drivingBolt)
            {
                KeepBoltShut(firearmController);
                return;
            }
            BoltState state = BoltStateOf(firearmController.Item);
            ApplyBoltBones(state, boltBonesData);
            ApplyBoltChamberView(firearmController, state, boltBonesData);
        }

        // Closed and ours, the gun idle: held shut against the animator, so the bolt shows where it is. Handed back to the animator once
        // closed, a bolt-action stayed drawn open by it (headset, once, a hideout session that threw a live round out: "the bolt was stuck
        // open and when I tried grabbing it, it would grab at the closed position ... I wouldn't be able to grab bullets" - our state was
        // closed). Suspected: the rechamber skip sends only the arms layer back to idle, and a layer left in Tarkov's rechamber clip held
        // the bolt open. Tarkov's own operations (its reload, check chamber, a malfunction) aren't idle: their bolt animations play.
        public static bool keepBoltShut = true;

        private static void KeepBoltShut(FirearmController firearmController)
        {
            if (!keepBoltShut || HoldingBolt || !boltClosedPoseRead || boltSlider == null || boltTurner == null || !ManualBoltOn(firearmController))
                return;
            boltSlider.localPosition = sliderClosedLocalPosition;
            boltTurner.localRotation = turnerClosedLocalRotation;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The chambered round on the bolt face

        // The bolt leaves closed: a live round in the chamber shows and rides back with it. After a shot the fired case's model is at
        // the shell port already (nothing in the chamber to show); a round Tarkov counts in but that's still in the magazine stays hidden.
        private static void BeginBoltChamberView(FirearmController firearmController, BoltState state)
        {
            EndBoltChamberView();
            Weapon weapon = firearmController.Item;
            if (!boltShowsChamberedRound || state.spentCase || weapon.ChamberAmmoCount == 0)
                return;
            Transform[] chamberBones = firearmController.Firearms._patronInWeaponTransforms;
            Transform chamberBone = chamberBones != null && chamberBones.Length > 0 ? chamberBones[0] : null;
            if (chamberBone == null)
                return;
            boltChamberFor = firearmController;
            boltChamberBone = chamberBone;
            boltChamberBoneWasActive = chamberBone.gameObject.activeSelf;
            boltChamberEmpty = WaitingForRack(weapon);
            boltChamberRound = chamberBone.childCount > 0 ? chamberBone.GetChild(0) : null;
            if (boltChamberRound != null)
                boltChamberRoundRest = boltChamberRound.localPosition;
            if (boltDebugLog)
                Plugin.MyLog.LogInfo($"[VRReload] bolt opening: chamber '{chamberBone.name}' (was {(boltChamberBoneWasActive ? "shown" : "hidden")}), round model {(boltChamberRound != null ? boltChamberRound.name : "none")}, "
                    + $"{(boltChamberEmpty ? "still in the magazine: shown empty" : "rides the bolt")}, chamber on the slider {chamberBone.IsChildOf(boltSlider)}");
        }

        // After the animator: the round moves back as far as the slider has. A chamber bone that is itself on the slider rides it already.
        // Once the full stroke threw the round out (its model goes back to Tarkov's pool) the chamber stays empty until the bolt closes.
        private static void ApplyBoltChamberView(FirearmController firearmController, BoltState state, BoltData bolt)
        {
            if (boltChamberBone == null || boltChamberFor != firearmController)
                return;
            if (boltChamberEmpty)
            {
                boltChamberBone.gameObject.SetActive(false);
                return;
            }
            boltChamberBone.gameObject.SetActive(true);
            if (boltChamberRound == null || boltChamberRound.parent != boltChamberBone)
                return;
            Vector3 back = boltChamberBone.IsChildOf(boltSlider) ? Vector3.zero
                : boltChamberBone.InverseTransformVector(boltFrame.TransformVector(bolt.travel * state.slide));
            boltChamberRound.localPosition = boltChamberRoundRest + back;
        }

        private static void EndBoltChamberView()
        {
            if (boltChamberBone != null)
            {
                boltChamberBone.gameObject.SetActive(boltChamberBoneWasActive);
                if (boltChamberRound != null && boltChamberRound.parent == boltChamberBone)
                    boltChamberRound.localPosition = boltChamberRoundRest;
            }
            boltChamberFor = null;
            boltChamberBone = null;
            boltChamberRound = null;
            boltChamberEmpty = false;
        }

        private static void ApplyBoltBones(BoltState state, BoltData bolt)
        {
            if (boltSlider == null || boltTurner == null || !boltClosedPoseRead)
                return;
            if (!HoldingBolt && state.slide <= 0f && state.turn <= 0f)
            {
                PutBoltBonesBack();
                return;
            }
            boltSlider.localPosition = sliderClosedLocalPosition + bolt.travel * state.slide;
            boltTurner.localRotation = turnerClosedLocalRotation * TurnOf(state, bolt);
        }

        private static void PutBoltBonesBack()
        {
            if (drivingBolt && boltSlider != null && boltTurner != null && boltClosedPoseRead)
            {
                boltSlider.localPosition = sliderClosedLocalPosition;
                boltTurner.localRotation = turnerClosedLocalRotation;
            }
            drivingBolt = false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static SoundHand BoltSoundHand => boltInGunHand ? SoundHand.GunHand : SoundHand.OffHand;

        private static void PulseBoltHand(float amplitude, float duration)
        {
            if (boltInGunHand)
                PulseGunHand(amplitude, duration);
            else
                Pulse(amplitude, duration);
        }

        // The finger pose on the knob: the clip's for the right hand (the gun hand's, or the off hand's with the gun in the left
        // hand), the magazine pull's pinch for the left
        private static Vector3[] BoltFingerPose(bool gunHand)
        {
            if (!HoldingBolt || boltInGunHand != gunHand)
                return null;
            return OnRightArm(gunHand) ? boltHandFingers ?? GUN_HAND_HANDLE_FINGERS : MAGAZINE_PULL_FINGERS;
        }
    }
}
