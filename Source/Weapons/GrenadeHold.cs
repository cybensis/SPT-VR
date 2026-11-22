using EFT.InputSystem;
using RootMotion.FinalIK;
using TarkovVR.ModSupport;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;
using GrenadeHandsController = EFT.Player.GrenadeHandsController;
using TarkovVR.Source.Player.Body;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Weapons
{
    // A grenade in the hand. It stays held while the hold button (the holding hand's grip) is down. Letting go throws it once it is
    // armed; before that, letting go puts it back (to the chest slot, with your hands as they were).
    //  - A grenade with a ring (GrenadeRings: the game's own pin and ring bones, on eleven of its twelve models): the other hand's index
    //    finger hooks the ring when its trigger is pressed (the arm's IK target is latched there, the fingers in the ring), and pulling
    //    along the pin slides it out. Past pinFreeDistance the grenade is armed and the ring stays on that finger. Brought back to the
    //    grenade (after being away from it), the hand latches on the pin's hole, and pushing in re-pins it with Tarkov's PutGrenadeBack
    //    (its pin-back clip).
    //  - Without a ring (the RDG-2's caps): armed as soon as it is in the hand.
    // Arming is Tarkov's own pin pull (TryHighThrow -> HighThrowOperation through its input path, which FIKA syncs), with its clip played
    // through at animatorRushSpeed. The throw is RepositionGrenadeThrow (VRGrenadeController). The pin's pose is set in onBeforeRender,
    // after the animator has written it, and restored when the grenade leaves the hands (its model goes back to Tarkov's pool).
    public static class GrenadeHold
    {
        [Tunable("Grenades", TunableKind.Tune, "How close the off hand's index finger must be to the ring to hook it.", Unit = "m")]
        public static float ringReachRadius = 0.05f;        // m from the off hand's index middle joint to the ring's hook point, to hook it
        [Tunable("Grenades", TunableKind.Tune, "How far the pin slides along its pull before it's out.", Unit = "m")]
        public static float pinFreeDistance = 0.03f;        // m the pin slides along its pull before it's out (a real pin's prongs)
        [Tunable("Grenades", TunableKind.Tune, "How far off the pull line the hand can go before the finger slips off the ring.", Unit = "m")]
        public static float ringLateralEscape = 0.12f;      // m the off controller may stray off the pull line before the finger slips out
        [Tunable("Grenades", TunableKind.Tune, "Speed Tarkov's pin-pull clip is played through at once the pin is out (it's an animation of a pull already "
            + "done).")]
        public static float animatorRushSpeed = 30f;        // Tarkov's pin-pull clip (1.33 s on every grenade) played through at this speed
        [Tunable("Grenades", TunableKind.Tune, "How long that lasts (the clip is 1.33 s on every grenade).", Unit = "s")]
        public static float animatorRushTime = 0.07f;       // s
        public static float drawRushTime = 0.05f;           // s of the same rush for the draw clip a slot grab skips (its clip still plays out)
        [Tunable("Grenades", TunableKind.Tune, "Re-pinning: how far the ring on the finger must first go from the grenade before bringing it back latches it "
            + "on the pin's hole.", Unit = "m")]
        public static float repinArmDistance = 0.08f;       // m the ring on the finger must first move away from the grenade before bringing it back re-pins
        [Tunable("Grenades", TunableKind.Tune, "Re-pinning: pulling back out this far while pushing the pin in leaves it out (the ring stays on the finger).", Unit = "m")]
        public static float repinBackOut = 0.02f;           // m of pulling back out while pushing the pin in that lets go (the ring stays on the finger)
        // In the left hand (a grenade it took off the chest) the grenade itself is mirrored, not only the hold, so its pin and ring face
        // the right hand that pulls them. The hand positioner it hangs from is scaled -1 on its x (the controller's left-right), so the
        // hand, grenade and ring are the mirror of the right-hand hold.
        [Tunable("Grenades", TunableKind.Tune, "How close any point of the other hand must be to the held grenade's middle for its grip to take the grenade "
            + "over.", Unit = "m")]
        public static float swapRadius = 0.08f;             // m from any point of the other hand to the grenade's middle for its grip to take the grenade over
        // While the pin is out (and while it goes back in), the hand keeps the turn it had when the pull began (KeepHandTurn).
        // The hand follows Tarkov's grip marker, and the armed clip turns that marker.

        private static GrenadeHandsController controller;
        private static Transform pin, ring;
        private static GrenadeRings.RingData ringData;
        private static Vector3 pinRestLocalPosition;
        private static Quaternion pinRestLocalRotation = Quaternion.identity;
        private static Vector3 pinRestLocalScale = Vector3.one;     // the pin-pull clip scales the pin away: re-applied while we drive the pin
        private static Transform mirroredPositioner;                // the RightHandPositioner scaled -1 on x for a grenade in the left hand
        private static bool pinWasActive;
        private static bool heldSeen;               // the hold button has been down since it came into the hand (a grenade drawn another way waits for that)
        private static bool holdDown;
        private static bool armRequested, armed, thrown, putBack;
        private static float armRequestedAt;
        private static bool armCommandSent;         // Tarkov's pin pull was requested (as a co-op client: only once the host has answered the take)
        private static float armCommandAt;
        private static float pinRushEnds = -1f;
        private static float rushUntil = -1f;
        private static GrenadeHandsController rushing;

        private static bool ringLatched;
        private static Transform latch;
        private static bool latchedOnRightArm;
        private static LimbIK latchedArm;
        private static Transform latchedArmTargetBefore;
        private static Vector3 controllerAtLatch;
        private static float pull;
        // Pin out: its ring hangs on the off hand's index finger (middle joint) until the grenade is thrown or the pin is pushed back in
        private static bool pinInHand;
        private static Transform pinFinger;         // the finger the ring is on (it stays there when that hand takes the grenade over)
        private static Vector3 pinOnFingerPosition;
        private static Quaternion pinOnFingerRotation = Quaternion.identity;
        private static bool pushingIn;              // the ring's latch is pushing the pin back in (pull runs pinFreeDistance -> 0)
        private static bool repinArmed;             // the ring has been away from the grenade since the pull (or the last push)
        private static bool pinReseated;            // pushed back in: the pin is held on its seat (the pin-back clip would move it around)
        // KeepHandTurn: the holder's rotation when the grenade came in, and the grip marker's rotation under it on the last resting frame
        private static Quaternion holderRest = Quaternion.identity;
        private static Quaternion markerAtRest = Quaternion.identity;
        private static bool markerAtRestSeen;
        private static float keptTurnMax;           // the most the armed hold turned the marker away from its rest (the throw's log line)
        private static bool offTriggerDown;
        private static bool ringInReach;
        private static bool hooked;

        // With the off hand on the ring or gripping the pin, we drive its fingers (VRReloadController.OffHandPose) and its arm (ArmLatch).
        // With the ring just hanging on the finger (trigger open) the hand is left alone.
        private static bool OffHandHoldsRing => controller != null && (ringLatched || (pinInHand && offTriggerDown && pinFinger == HookJoint()));
        private static bool offGripDown;
        private static readonly System.Collections.Generic.List<Vector3> offTouchPoints = new System.Collections.Generic.List<Vector3>();
        public static bool OffHandBusy => OffHandHoldsRing;
        internal static Vector3[] RingFingers => OffHandHoldsRing && ringData != null ? ringData.fingers : null;
        internal static Transform ArmLatch(bool rightArm) => controller != null && ringLatched && latch != null && latchedOnRightArm == rightArm ? latch : null;
        public static bool PinOut => controller != null && armed;

        // The grenade mirrored in the left hand: the mirror plane's world normal is the positioner's x
        public static bool MirroredInLeftHand => controller != null && mirroredPositioner != null;
        public static bool TryMirrorNormal(out Vector3 normal)
        {
            normal = MirroredInLeftHand ? mirroredPositioner.right : Vector3.right;
            return MirroredInLeftHand;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Every frame a grenade is in hand and not planting a tripwire (InputHandlers.GrenadeHandler)

        public static void UpdateCommand(GrenadeHandsController grenade, ref ECommand command)
        {
            if (!hooked)
            {
                hooked = true;
                Application.onBeforeRender += OnBeforeRender;
            }
            if (grenade != controller)
                Begin(grenade);
            if (controller == null)
                return;
            if (thrown || putBack)
                return;

            holdDown = ReadHold();
            if (!heldSeen)
            {
                if (!holdDown)
                    return;
                heldSeen = true;
            }
            if (UpdateSwap())
                return;
            // The holding hand's trigger picks a quick-access grenade to swap to (pin in, held, no hand on the ring)
            GrenadePicker.Update(controller, canPick: holdDown && !armed && !armRequested && !ringLatched && !pinInHand);
            if (armRequested && !armed)
                UpdateArming();
            UpdateRing();
            if (holdDown)
            {
                // No ring to pull (the RDG-2): ready as soon as it's held
                if (ring == null && !armRequested)
                    RequestArm("no ring to pull");
                return;
            }
            // Let go: thrown once armed (released while arming, which takes a frame or two: thrown the moment it is armed), else put back
            if (armed)
                Throw();
            else if (!armRequested)
                PutBack();
        }

        private static void Begin(GrenadeHandsController grenade)
        {
            End();
            if (grenade == null || grenade._player == null || !grenade._player.IsYourPlayer)
                return;
            controller = grenade;
            heldSeen = GrenadeSlot.TakenByHold(grenade.Item);
            if (GrenadeRings.TryFind(grenade.WeaponRoot, out pin, out ring, out ringData))
            {
                pinRestLocalPosition = pin.localPosition;
                pinRestLocalRotation = pin.localRotation;
                pinRestLocalScale = pin.localScale.sqrMagnitude > 1e-6f ? pin.localScale : Vector3.one;
                pinWasActive = pin.gameObject.activeSelf;
            }
            // The holder's rotation as its spawn set it (VRGrenadeController.HandleGrenade): the armed hold's turn is measured from it
            holderRest = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform.localRotation : Quaternion.identity;
            markerAtRestSeen = false;
            // In the left hand (taken from the slot with it): the whole hold is mirrored onto the left controller
            SetMirror(Handedness.GunInLeftHand);
            offGripDown = Handedness.OffHandGrip.state;
            if (Handedness.GunInLeftHand)
                PlaceHolder(VRGlobals.weaponHolder.transform.localPosition);
        }

        // Scales the positioner -1 on x for the left hand, and back to 1 for the right
        private static void SetMirror(bool leftHand)
        {
            Transform positioner = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform.parent : null;
            if (mirroredPositioner != null && mirroredPositioner != positioner)
                mirroredPositioner.localScale = Vector3.one;
            mirroredPositioner = null;
            if (positioner == null)
                return;
            if (leftHand)
            {
                mirroredPositioner = positioner;
                positioner.localScale = new Vector3(-1f, 1f, 1f);
            }
            else
            {
                positioner.localScale = Vector3.one;
            }
        }

        private static void End()
        {
            GrenadePicker.Reset();
            if (controller != null)
            {
                Unlatch();
                if (pin != null)
                {
                    pin.localPosition = pinRestLocalPosition;
                    pin.localRotation = pinRestLocalRotation;
                    pin.localScale = pinRestLocalScale;
                    pin.gameObject.SetActive(pinWasActive);
                }
                // The pooled hands object goes back unmirrored
                if (mirroredPositioner != null)
                    mirroredPositioner.localScale = Vector3.one;
                // Only this grenade's rush: the next one's draw may be rushing already (GrenadeSlot, at its spawn)
                if (rushing != null && rushing == controller)
                {
                    if (rushing._firearmsAnimator != null)
                        rushing._firearmsAnimator.Animator.speed = 1f;
                    rushing = null;
                }
            }
            controller = null;
            mirroredPositioner = null;
            pinRushEnds = -1f;
            pin = ring = null;
            ringData = null;
            heldSeen = holdDown = false;
            armRequested = armed = thrown = putBack = false;
            armCommandSent = false;
            pull = 0f;
            pinInHand = false;
            pinFinger = null;
            pushingIn = repinArmed = pinReseated = false;
            markerAtRestSeen = false;
            offTriggerDown = false;
            ringInReach = false;
        }

        // The grip of the hand holding it
        private static bool ReadHold()
        {
            return Handedness.GunHandGrip.state;
        }

        // The other hand's grip pressed at the grenade: that hand takes it over, and the ring stays on its finger. Not while a hand
        // is on the ring, or while the pin-pull clip is being played through.
        private static bool UpdateSwap()
        {
            bool offGrip = Handedness.OffHandGrip.state;
            bool pressed = offGrip && !offGripDown;
            offGripDown = offGrip;
            if (!pressed || ringLatched || (armRequested && !armed) || !OffHandFree() || VRGlobals.handsInteractionController == null)
                return false;
            if (!RealLeftHandedPatches.GrenadeCentre(controller.WeaponRoot, out Vector3 centre))
                return false;
            // The other hand's points (palm, knuckles, fingertips); with the grenade in the left hand, that is the physical right controller
            VRGlobals.handsInteractionController.GetHandTouchPoints(Handedness.GunHandIsLeft, offTouchPoints);
            float nearest = float.MaxValue;
            foreach (Vector3 point in offTouchPoints)
                nearest = Mathf.Min(nearest, Vector3.Distance(point, centre));
            if (nearest > swapRadius)
                return false;

            bool toLeft = !Handedness.GunInLeftHand;
            GrenadeSlot.HoldIn(controller.Item, toLeft);
            Handedness.SetGunInLeftHand(toLeft);
            SetMirror(toLeft);
            heldSeen = true;
            holdDown = true;
            // The hand that let go is the other hand now, its grip still closed: its next press is a new one
            offGripDown = true;
            ringInReach = false;
            ChestHang.PlayGripSound();
            Haptic(Handedness.GunHandSource, 0.45f, 0.05f);
            return true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Arming and letting go

        // When Tarkov's pin pull was sent for the grenade in hand (-1: not yet): the host's copy pulls it from then on (CoopHost.GrenadeThrown)
        internal static float PinSentAt => controller != null && armCommandSent ? armCommandAt : -1f;

        private static void RequestArm(string why)
        {
            armRequested = true;
            armRequestedAt = Time.time;
            armCommandSent = false;
            SendArmCommandWhenHostReady();
        }

        // Tarkov's pin pull. As a co-op client it is sent only once the host has answered the take (its copy of you still runs the draw
        // we skipped). Sent sooner, the pin pull and the throw's discard would reach a copy that isn't holding the grenade yet (FIKA's
        // own throw gate waits for the same answer: FikaClientGrenadeController.CanThrow). It is sent anyway after CoopHost.waitLimit.
        private static void SendArmCommandWhenHostReady()
        {
            if (armCommandSent)
                return;
            if (CoopHost.HoldsBack(armRequestedAt))
                return;
            armCommandSent = true;
            armCommandAt = Time.time;
            VRInputManager.ForceCommand(ECommand.TryHighThrow);
        }

        private static void UpdateArming()
        {
            if (!armCommandSent)
            {
                SendArmCommandWhenHostReady();
                return;
            }
            if (!controller.WaitingForHighThrow)
            {
                if (Time.time - armCommandAt > 1.5f)
                {
                    Plugin.MyLog.LogWarning($"[GrenadeHold] Tarkov didn't start the pin pull (operation {controller.CurrentOperation?.GetType().Name}), asking again");
                    armCommandAt = Time.time;
                    VRInputManager.ForceCommand(ECommand.TryHighThrow);
                }
                return;
            }
            // Play its pin-pull clip through quickly
            if (pinRushEnds < 0f)
            {
                pinRushEnds = Time.time + animatorRushTime;
                Rush(controller, animatorRushTime);
                return;
            }
            if (Time.time < pinRushEnds)
                return;
            armed = true;
            WeaponPatches.pinPulled = true;
            PlaceArmed();
        }

        // Runs the arms animator fast for a moment (through a clip of something the hands already did), then back at normal speed
        internal static void Rush(GrenadeHandsController grenade, float seconds)
        {
            if (grenade == null || grenade._firearmsAnimator == null || ThrowingNow(grenade))
                return;
            grenade._firearmsAnimator.Animator.speed = animatorRushSpeed;
            rushing = grenade;
            rushUntil = Time.time + seconds;
        }

        // Whether this grenade's throw is playing. Checked per grenade: another grenade's throw must not count
        private static bool ThrowingNow(GrenadeHandsController grenade) => TarkovVR.Patches.Core.VR.VRGrenadeController.Throwing(grenade);

        // The armed hold's holder pose (the same as InputHandlers' PositionGrenadeAfterPinPull)
        private static void PlaceArmed()
        {
            if (VRGlobals.weaponHolder == null)
                return;
            PlaceHolder(new Vector3(-0.1f, -0.43f, -0.25f));
            if (WeaponPatches.currentGunInteractController != null)
                WeaponPatches.currentGunInteractController.framesAfterEnabled = 0;
        }

        // Called from GunInteractionController.UpdateWeaponPosition, before the anchor puts the grip marker on the hand. While resting, the
        // hand follows Tarkov's hold and the grip marker's rotation is recorded. From the pull until the grenade rests again (pin back in,
        // its clip played), the holder turns so the marker keeps that rotation. Measured under the holder only (the mirrored positioner's
        // -1 scale is above it), like VRReloadController.ApplyPressCheckGunTurn.
        internal static void KeepHandTurn(Transform holder)
        {
            if (controller == null || thrown || holder == null || VRGlobals.player == null
                || VRGlobals.player._markers == null || VRGlobals.player._markers.Length < 2)
                return;
            if (!MarkerUnderHolder(VRGlobals.player._markers[1], holder, out Quaternion markerNow))
                return;
            bool resting = !armRequested && !armed && !pinInHand && controller.CurrentGrenadeOperation is GrenadeHandsController.Idling;
            if (resting)
            {
                markerAtRest = markerNow;
                markerAtRestSeen = true;
                keptTurnMax = 0f;
                holder.localRotation = holderRest;
                return;
            }
            if (!markerAtRestSeen)
                return;
            holder.localRotation = holderRest * markerAtRest * Quaternion.Inverse(markerNow);
            keptTurnMax = Mathf.Max(keptTurnMax, Quaternion.Angle(markerAtRest, markerNow));
        }

        // The marker's rotation in the holder's frame, from the local rotations between them (no scale in that chain)
        private static bool MarkerUnderHolder(Transform marker, Transform holder, out Quaternion rotation)
        {
            rotation = Quaternion.identity;
            if (marker == null)
                return false;
            Quaternion chain = marker.localRotation;
            for (Transform parent = marker.parent; parent != null; parent = parent.parent)
            {
                if (parent == holder)
                {
                    rotation = chain;
                    return true;
                }
                chain = parent.localRotation * chain;
            }
            return false;
        }

        // Places the holder where the right-handed hold has it, mirrored for the left hand when the grenade itself isn't mirrored
        // (VRPlayerManager.MirroredHolderPosition; the hold's rotation is mirrored by PlaceRightHandForGun, read once here)
        private static void PlaceHolder(Vector3 rightHanded)
        {
            Transform holder = VRGlobals.weaponHolder.transform;
            if (Handedness.GunInLeftHand && mirroredPositioner == null && VRGlobals.vrPlayer != null)
            {
                TarkovVR.Source.Player.VRManager.VRPlayerManager.CacheGunInHand();
                holder.localPosition = VRGlobals.vrPlayer.MirroredHolderPosition(rightHanded);
            }
            else
            {
                holder.localPosition = rightHanded;
            }
        }

        private static void Throw()
        {
            thrown = true;
            // Bookkeeping only, and it must never stop the throw: thrown is already set, so a throw that didn't happen would leave the grenade in the hand
            try
            {
                GrenadeSlot.NoteThrown(controller.Item);
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[GrenadeHold] noting the throw failed (thrown anyway): {exception}");
            }
            controller.ThrowGrenadeInner(null, 0f, 1f, Vector3.zero, 1f, false, true);
        }

        private static void PutBack()
        {
            putBack = true;
            GrenadeSlot.ReturnHands(controller);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The ring

        private static void UpdateRing()
        {
            float axis = Handedness.OffHandTrigger.axis;
            bool down = offTriggerDown ? axis > 0.35f : axis > 0.5f;
            bool pressed = down && !offTriggerDown;
            offTriggerDown = down;
            if (ring == null || pin == null)
                return;

            // The ring stays on the finger (trigger open or not) until the grenade is thrown or the pin is pushed back in
            if (pinInHand)
            {
                UpdateRepin();
                return;
            }
            if (armed || armRequested || !holdDown)
            {
                if (ringLatched)
                    Unlatch();
                return;
            }
            if (ringLatched)
            {
                Vector3 pullWorld = PullAxisWorld();
                Vector3 moved = OffController() - controllerAtLatch;
                float along = Vector3.Dot(moved, pullWorld);
                float lateral = (moved - pullWorld * along).magnitude;
                if (!down || lateral > ringLateralEscape)
                {
                    Unlatch();
                    return;
                }
                pull = Mathf.Clamp(along, 0f, pinFreeDistance);
                PlaceLatch();
                if (along >= pinFreeDistance)
                    PinComesOut();
                return;
            }
            bool inReach = OffHandFree() && HookDistance() < ringReachRadius;
            if (inReach && !ringInReach)
                Haptic(Handedness.OffHandSource, 0.15f, 0.03f);
            ringInReach = inReach;
            if (inReach && pressed)
                Latch();
        }

        private static bool OffHandFree()
        {
            var hands = VRGlobals.handsInteractionController;
            return VRGlobals.vrPlayer != null && hands != null && hands.heldItem == null && !VRReloadController.OffHandHolds;
        }

        // Pin out, ring on the finger: taking the ring away from the grenade and bringing it back latches the hand where the pin came out
        // (no button), and pushing in along the pull seats the pin. Then Tarkov's own PutGrenadeBack runs (its pin-back clip; the grenade
        // is idle and safe again). Pulling back out or off the line leaves the pin out, with the ring still on the finger.
        private static void UpdateRepin()
        {
            if (!armed || thrown || !holdDown)
            {
                if (ringLatched)
                    Unlatch();
                return;
            }
            Transform joint = HookJoint();
            if (joint == null || joint != pinFinger)
                return;
            if (!ringLatched)
            {
                float distance = Vector3.Distance(joint.position, RingHookPoint(pinFreeDistance));
                if (distance > repinArmDistance)
                    repinArmed = true;
                if (repinArmed && distance < ringReachRadius && OffHandFree())
                    Latch(pushing: true);
                return;
            }
            Vector3 pullWorld = PullAxisWorld();
            Vector3 moved = OffController() - controllerAtLatch;
            float along = Vector3.Dot(moved, pullWorld);
            float lateral = (moved - pullWorld * along).magnitude;
            if (lateral > ringLateralEscape || along > repinBackOut)
            {
                Unlatch();
                repinArmed = false;
                return;
            }
            pull = Mathf.Clamp(pinFreeDistance + along, 0f, pinFreeDistance);
            PlaceLatch();
            if (pull <= 0.001f)
                Repin();
        }

        private static void Repin()
        {
            pushingIn = false;
            pinInHand = false;
            Unlatch();
            pull = 0f;
            repinArmed = false;
            pinReseated = true;
            armed = false;
            armRequested = false;
            pinRushEnds = -1f;
            WeaponPatches.pinPulled = false;
            Haptic(Handedness.OffHandSource, 0.6f, 0.06f);
            Haptic(Handedness.GunHandSource, 0.25f, 0.04f);
            controller.PutGrenadeBack();
        }

        private static void Latch(bool pushing = false)
        {
            if (latch == null)
                latch = new GameObject("VRGrenadeRingLatch").transform;
            latchedOnRightArm = Handedness.GunInLeftHand;
            latchedArm = VRGlobals.ikManager != null ? (latchedOnRightArm ? VRGlobals.ikManager.rightArmIk : VRGlobals.ikManager.leftArmIk) : null;
            latchedArmTargetBefore = latchedArm != null ? latchedArm.solver.target : null;
            controllerAtLatch = OffController();
            pushingIn = pushing;
            pull = pushing ? pinFreeDistance : 0f;
            ringLatched = true;
            PlaceLatch();
            Haptic(Handedness.OffHandSource, 0.4f, 0.04f);
        }

        // Puts the arm's IK target on the ring where the clip's hand holds it, with the ring where the pull has it. Mirrored across the
        // grenade for the right hand (the clip's hand is a left hand). The right arm gets it through RealLeftHandedPatches
        // (VRReloadController.ArmLatch); the left arm directly, set again every frame like the reload's latch (VRPlayerManager's edges reset it).
        private static void PlaceLatch()
        {
            Matrix4x4 ringMatrix = RingMatrix(pull);
            Vector3 ringPosition = ringMatrix.MultiplyPoint3x4(Vector3.zero);
            Vector3 position = ringMatrix.MultiplyPoint3x4(ringData.latchPosition);
            Quaternion rotation = RingRotation() * ringData.latchRotation;
            if (latchedOnRightArm)
            {
                // Mirrored grenade: the clip's left hand on the ring is mirrored with it (the position already is). Unmirrored grenade:
                // mirror across the plane through the ring itself, since the ring is where it is.
                if (MirroredInLeftHand)
                {
                    // From the ring's own matrix (which includes the -1), like the left hand's grip (RealLeftHandedPatches)
                    if (TarkovVR.Source.Player.Body.HandMirror.TryOtherPalmFromMirroredFrame(ringMatrix * Matrix4x4.Rotate(ringData.latchRotation),
                        rightToLeft: false, out Quaternion mirroredPalm))
                        rotation = mirroredPalm;
                }
                else if (RealLeftHandedPatches.GunPlane(out _, out Vector3 planeNormal)
                    && TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(rotation, planeNormal, rightToLeft: false, out Quaternion mirrored))
                {
                    position = RealLeftHandedPatches.ReflectPoint(position, ringPosition, planeNormal);
                    rotation = mirrored;
                }
            }
            latch.SetPositionAndRotation(position, rotation);
            if (!latchedOnRightArm && latchedArm != null && latchedArm.solver.target != latch)
                HandTargets.Set(latchedArm, latch, HandOwner.Grenade);
        }

        private static void Unlatch()
        {
            if (ringLatched && !latchedOnRightArm && latchedArm != null && latchedArm.solver.target == latch)
                HandTargets.Set(latchedArm, latchedArmTargetBefore != null ? latchedArmTargetBefore : VRGlobals.vrPlayer?.LeftHand?.transform, HandOwner.Grenade);
            ringLatched = false;
            pushingIn = false;
            if (!pinInHand)
                pull = 0f;
        }

        private static void PinComesOut()
        {
            PinPose(pull, out Vector3 pinPosition, out Quaternion pinRotation);
            Transform joint = HookJoint();
            if (joint != null)
            {
                pinFinger = joint;
                pinOnFingerPosition = Quaternion.Inverse(joint.rotation) * (pinPosition - joint.position);
                pinOnFingerRotation = Quaternion.Inverse(joint.rotation) * pinRotation;
                pinInHand = true;
                repinArmed = false;
                pinReseated = false;
            }
            // The hand comes off the ring's latch; from here on the ring is on its index finger
            Unlatch();
            Haptic(Handedness.OffHandSource, 0.8f, 0.07f);
            Haptic(Handedness.GunHandSource, 0.3f, 0.04f);
            RequestArm("pin out");
        }

        // The pin on the grenade, slid out from its seat along its pull. Set through its local pose (Transform.rotation goes wrong under a
        // mirrored grenade's -1 scale), then read back as the world pose SetPositionAndRotation takes.
        private static void PinPose(float slid, out Vector3 position, out Quaternion rotation)
        {
            SeatPin(slid);
            position = pin.position;
            rotation = pin.rotation;
        }

        private static void SeatPin(float slid)
        {
            pin.localPosition = pinRestLocalPosition + pin.parent.InverseTransformVector(PullAxisWorld() * slid);
            pin.localRotation = pinRestLocalRotation;
            pin.localScale = pinRestLocalScale;
        }

        // Where the finger hooks the ring with the pin slid that far out (mirrored onto the right hand like the latch)
        private static Vector3 RingHookPoint(float slid)
        {
            Matrix4x4 ringMatrix = RingMatrix(slid);
            Vector3 hookPoint = ringMatrix.MultiplyPoint3x4(ringData.hookPoint);
            if (Handedness.GunInLeftHand && !MirroredInLeftHand && RealLeftHandedPatches.GunPlane(out _, out Vector3 planeNormal))
                hookPoint = RealLeftHandedPatches.ReflectPoint(hookPoint, ringMatrix.MultiplyPoint3x4(Vector3.zero), planeNormal);
            return hookPoint;
        }

        // The ring's full world matrix with the pin slid that far out, scale included (a mirrored grenade's -1 reaches it). RingRotation
        // gives its rotation the Unity way (parent rotations multiplied, the scale's sign left out), which SetPositionAndRotation and HandMirror take.
        private static Matrix4x4 RingMatrix(float slid)
        {
            Matrix4x4 pinMatrix = Matrix4x4.Translate(PullAxisWorld() * slid) * pin.parent.localToWorldMatrix
                * Matrix4x4.TRS(pinRestLocalPosition, pinRestLocalRotation, pinRestLocalScale);
            return pinMatrix * Matrix4x4.TRS(ring.localPosition, ring.localRotation, ring.localScale);
        }

        private static Quaternion RingRotation()
        {
            return pin.parent.rotation * pinRestLocalRotation * ring.localRotation;
        }

        private static Vector3 PullAxisWorld()
        {
            return pin.parent.TransformVector(ringData.pullAxis).normalized;
        }

        // The off controller's world position (the rig's off hand follows it, whichever hand holds the grenade)
        private static Vector3 OffController()
        {
            return VRGlobals.vrPlayer.LeftHand.transform.position;
        }

        // The rendered off hand's palm bone, on the arm that draws it (the right one with the grenade in the left hand)
        private static Transform OffPalm()
        {
            if (VRGlobals.ikManager == null)
                return null;
            LimbIK arm = Handedness.GunInLeftHand ? VRGlobals.ikManager.rightArmIk : VRGlobals.ikManager.leftArmIk;
            return arm != null && arm.solver != null && arm.solver.bone3 != null ? arm.solver.bone3.transform : null;
        }

        private static Transform hookJointPalm, hookJoint;

        // Distance from the off hand's index middle joint to where it hooks the ring
        private static float HookDistance()
        {
            Transform joint = HookJoint();
            return joint != null ? Vector3.Distance(joint.position, RingHookPoint(0f)) : float.MaxValue;
        }

        // The rendered off hand's index middle joint (the one through the ring)
        private static Transform HookJoint()
        {
            Transform palm = OffPalm();
            if (palm == null)
                return null;
            if (hookJointPalm != palm)
            {
                hookJointPalm = palm;
                hookJoint = null;
                string wanted = Handedness.GunInLeftHand ? "Base HumanRDigit22" : "Base HumanLDigit22";
                foreach (Transform node in palm.GetComponentsInChildren<Transform>(true))
                {
                    if (node.name == wanted)
                    {
                        hookJoint = node;
                        break;
                    }
                }
            }
            return hookJoint;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // After every LateUpdate: puts the pin where the pull or the finger has it (the animator wrote its own pose this frame), and ends the rush
        private static void OnBeforeRender()
        {
            if (rushing != null && Time.time >= rushUntil)
            {
                if (rushing._firearmsAnimator != null && !ThrowingNow(rushing))
                    rushing._firearmsAnimator.Animator.speed = 1f;
                rushing = null;
            }
            if (controller == null)
                return;
            if (VRGlobals.player == null || !ReferenceEquals(VRGlobals.player.HandsController, controller))
            {
                End();
                return;
            }
            GrenadePicker.Place();
            if (pin == null)
                return;
            Transform joint = pinInHand ? pinFinger : null;
            if (ringLatched || pull > 0f || joint != null || pinReseated)
                pin.localScale = pinRestLocalScale;
            if (ringLatched && pushingIn && joint != null)
            {
                // Being pushed back in: blended from the finger onto its seat as the push goes on
                float seated = 1f - Mathf.Clamp01(pull / pinFreeDistance);
                PinPose(pull, out Vector3 seatPosition, out Quaternion seatRotation);
                pin.SetPositionAndRotation(Vector3.Lerp(joint.position + joint.rotation * pinOnFingerPosition, seatPosition, seated),
                    Quaternion.Slerp(joint.rotation * pinOnFingerRotation, seatRotation, seated));
            }
            else if (ringLatched || (!pinInHand && pull > 0f))
            {
                SeatPin(pull);
            }
            else if (joint != null)
            {
                pin.SetPositionAndRotation(joint.position + joint.rotation * pinOnFingerPosition, joint.rotation * pinOnFingerRotation);
            }
            else if (pinReseated)
            {
                // Pushed back in: kept on its seat through Tarkov's pin-back clip (that clip moves the pin with its own hand)
                SeatPin(0f);
            }
        }

        private static void Haptic(Valve.VR.SteamVR_Input_Sources source, float amplitude, float duration)
        {
            TarkovVR.Source.Misc.HapticMixer.PlayPulse(source, amplitude, duration, 95f);
        }
    }
}
