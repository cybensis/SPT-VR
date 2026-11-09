using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using RootMotion.FinalIK;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using GrenadeHandsController = EFT.Player.GrenadeHandsController;

namespace TarkovVR.Source.Weapons
{
    // A grenade in the hand (his grenade plan, round 3). The hold button (VR tab: grip, or the trigger) holds it; letting go throws it once
    // it's armed and puts it back (to the chest slot, your hands as they were) before that.
    //  - A grenade with a ring (GrenadeRings: the game's own pin and ring bones, eleven of its twelve models): the OTHER hand's index finger
    //    hooks the ring on its trigger (the clip's hand pose on the ring: the arm's IK target latched there, its fingers in the ring), and
    //    pulling it along the pin slides the pin out; out past pinFreeDistance it's armed, its ring stays on that index finger. Brought
    //    back to the grenade (after being away from it) the hand latches on the pin's hole and pushing it in re-pins it: Tarkov's
    //    PutGrenadeBack, its pin-back clip (his ask: "put the pin back in by bringing your off hand back to the grenade").
    //  - Without one (the RDG-2's caps): armed as soon as it's in the hand (his pick).
    // Armed = Tarkov's own pin pull (TryHighThrow -> HighThrowOperation, through its input path, which FIKA syncs), its clip played through
    // at animatorRushSpeed so the hand isn't pulled about by an animation of a pull that's done already; the throw = RepositionGrenadeThrow
    // (VRGrenadeController) as before. The pin's pose is ours at onBeforeRender, after the animator wrote it; put back as it was when the
    // grenade leaves the hands (its model goes back to Tarkov's pool).
    public static class GrenadeHold
    {
        public static bool enabled = true;                  // false = the old flow: the trigger pulls the pin, letting go of it throws
        public static float ringReachRadius = 0.05f;        // m the off hand's index middle joint off where it hooks the ring
        public static float pinFreeDistance = 0.03f;        // m the pin slides along its pull before it's out (a real pin's prongs)
        public static float ringLateralEscape = 0.12f;      // m the off controller strays off the pull line before the finger slips out
        public static float animatorRushSpeed = 30f;        // Tarkov's pin-pull clip (1.33 s on every grenade) played through at this speed
        public static float animatorRushTime = 0.07f;       // s
        public static float drawRushTime = 0.05f;           // s of the same for the draw a slot grab skips (its clip still plays out)
        public static float repinArmDistance = 0.08f;       // m the ring on the finger must first go from the grenade before bringing it back re-pins
        public static float repinBackOut = 0.02f;           // m pulling back out while pushing the pin in: the finger leaves the grenade, the ring still on it
        // The left hand (a grenade it took off the chest): the grenade itself mirrored, not only the hold - its pin and ring on the right,
        // toward the right hand that pulls them (his report: "the pin is on the left side when I pick it up with the left hand"). The hand
        // positioner it hangs from scaled -1 on its x (the controller's left-right), so hand, grenade and ring are the right hold's mirror.
        // false = the old way: the hand mirrored across the grenade's centre plane, the grenade as it is.
        public static bool mirrorInLeftHand = true;
        public static bool debugLog = true;                 // one line per step (first round)

        private static GrenadeHandsController controller;
        private static Transform pin, ring;
        private static GrenadeRings.RingData ringData;
        private static Vector3 pinRestLocalPosition;
        private static Quaternion pinRestLocalRotation = Quaternion.identity;
        private static Vector3 pinRestLocalScale = Vector3.one;     // the pin-pull clip scales the pin away: kept while it's ours
        private static Transform mirroredPositioner;                // the RightHandPositioner scaled -1 on x (mirrorInLeftHand)
        private static bool pinWasActive;
        private static bool heldSeen;               // the hold button down since it's in hand (a grenade drawn another way waits for it)
        private static bool holdDown;
        private static bool armRequested, armed, thrown, putBack;
        private static float armRequestedAt;
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
        // The pin out: its ring on the off hand's index finger (its middle joint) until the grenade's thrown or the pin's pushed back in
        private static bool pinInHand;
        private static Vector3 pinOnFingerPosition;
        private static Quaternion pinOnFingerRotation = Quaternion.identity;
        private static bool pushingIn;              // the ring's latch is pushing the pin back in (pull runs pinFreeDistance -> 0)
        private static bool repinArmed;             // the ring has been away from the grenade since the pull (or the last push)
        private static bool pinReseated;            // pushed back in: the pin held on its seat (the pin-back clip would carry it about)
        private static bool holderTurnedArmed;      // PlaceArmed turned the holder (four models): turned back when the pin goes back in
        private static bool offTriggerDown;
        private static bool ringInReach;
        private static bool hooked;

        // His report: the first grenade in hand isn't held right, the next one is (before the slot too). One line at its spawn (was
        // its object ever in the VR hold / on the slot) and one 0.3 s into the hold (where it sits in the palm), to compare the two.
        private static GrenadeHandsController notedSpawnFor;
        private static string spawnSetup;
        private static float begunAt;
        private static bool poseLogged;

        // The off hand on the ring or gripping the pin: its fingers (VRReloadController.OffHandPose) and its arm (ArmLatch) are ours. The
        // ring just hanging on the finger (trigger open) leaves the hand its own.
        private static bool OffHandHoldsRing => controller != null && (ringLatched || (pinInHand && offTriggerDown));
        public static bool OffHandBusy => OffHandHoldsRing;
        internal static Vector3[] RingFingers => OffHandHoldsRing && ringData != null ? ringData.fingers : null;
        internal static Transform ArmLatch(bool rightArm) => controller != null && ringLatched && latch != null && latchedOnRightArm == rightArm ? latch : null;
        public static bool PinOut => controller != null && armed;

        // The grenade in the left hand, mirrored (mirrorInLeftHand): the mirror's world plane normal, the positioner's x
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
            if (debugLog && !poseLogged && Time.time - begunAt > 0.3f)
            {
                poseLogged = true;
                LogHoldPose();
            }
            if (thrown || putBack)
                return;

            holdDown = ReadHold();
            if (!heldSeen)
            {
                if (!holdDown)
                    return;
                heldSeen = true;
            }
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
            // Let go: thrown once armed (let go while arming, a frame or two: the moment it is), else back to the slot
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
            begunAt = Time.time;
            poseLogged = false;
            heldSeen = GrenadeSlot.TakenByHold(grenade.Item);
            if (GrenadeRings.TryFind(grenade.WeaponRoot, out pin, out ring, out ringData))
            {
                pinRestLocalPosition = pin.localPosition;
                pinRestLocalRotation = pin.localRotation;
                pinRestLocalScale = pin.localScale.sqrMagnitude > 1e-6f ? pin.localScale : Vector3.one;
                pinWasActive = pin.gameObject.activeSelf;
            }
            else if (debugLog)
            {
                Plugin.MyLog.LogInfo($"[GrenadeHold] {grenade.Item.LocalizedShortName()}: no ring to pull (model {grenade.HandsHierarchy?.name}, pin-like bones: {GrenadeRings.PinLikeNames(grenade.WeaponRoot)}) - ready when held");
            }
            // In the left hand (taken from the slot with it): the whole hold mirrored on the left controller, or the hold alone
            Transform positioner = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform.parent : null;
            if (Handedness.GunInLeftHand && mirrorInLeftHand && positioner != null)
            {
                mirroredPositioner = positioner;
                positioner.localScale = new Vector3(-1f, 1f, 1f);
            }
            else if (positioner != null)
            {
                positioner.localScale = Vector3.one;
            }
            if (Handedness.GunInLeftHand)
                PlaceHolder(VRGlobals.weaponHolder.transform.localPosition);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeHold] {grenade.Item.LocalizedShortName()} in the {(Handedness.GunInLeftHand ? "LEFT" : "right")} hand, ring {(ring != null ? ring.name : "none")}, held {heldSeen}");
        }

        private static void End()
        {
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
            pull = 0f;
            pinInHand = false;
            pushingIn = repinArmed = pinReseated = holderTurnedArmed = false;
            offTriggerDown = false;
            ringInReach = false;
        }

        // The hold button of the hand holding it: grip, or the trigger (VR tab), the trigger with the mod's 0.5 / 0.35 hysteresis
        private static bool ReadHold()
        {
            if (VRSettings.GetGrenadeHoldWithTrigger())
            {
                float axis = Handedness.GunHandTrigger.axis;
                return holdDown ? axis > 0.35f : axis > 0.5f;
            }
            return Handedness.GunHandGrip.state;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Arming and letting go

        private static void RequestArm(string why)
        {
            armRequested = true;
            armRequestedAt = Time.time;
            VRInputManager.ForceCommand(ECommand.TryHighThrow);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeHold] arming ({why})");
        }

        private static void UpdateArming()
        {
            if (!controller.WaitingForHighThrow)
            {
                if (Time.time - armRequestedAt > 1.5f)
                {
                    Plugin.MyLog.LogWarning($"[GrenadeHold] Tarkov didn't start the pin pull (operation {controller.CurrentOperation?.GetType().Name}), asking again");
                    armRequestedAt = Time.time;
                    VRInputManager.ForceCommand(ECommand.TryHighThrow);
                }
                return;
            }
            // Its pin-pull clip: played through
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
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeHold] armed after {Time.time - armRequestedAt:0.00} s");
        }

        // The arms animator run fast for a moment (a clip of something the hands already did), then back at normal speed
        internal static void Rush(GrenadeHandsController grenade, float seconds)
        {
            if (grenade == null || grenade._firearmsAnimator == null || VRGrenadeControllerThrowing())
                return;
            grenade._firearmsAnimator.Animator.speed = animatorRushSpeed;
            rushing = grenade;
            rushUntil = Time.time + seconds;
        }

        private static bool VRGrenadeControllerThrowing() => TarkovVR.Patches.Core.VR.VRGrenadeController.throwInProgress;

        // The armed hold, as the old flow placed it once its pin-pull clip was over (InputHandlers' PositionGrenadeAfterPinPull)
        private static void PlaceArmed()
        {
            if (VRGlobals.weaponHolder == null)
                return;
            Transform holder = VRGlobals.weaponHolder.transform;
            Vector3 position = new Vector3(-0.1f, -0.43f, -0.25f);
            string model = controller.HandsHierarchy != null ? controller.HandsHierarchy.name : "";
            if (model == "weapon_grenade_m7920.generated(Clone)" || model == "weapon_grenade_rgo.generated(Clone)"
                || model == "weapon_grenade_rgn.generated(Clone)" || model == "weapon_grenade_m18.generated(Clone)")
            {
                holder.localRotation = Quaternion.Euler(30, 273, 116);
                holderTurnedArmed = true;
                position = new Vector3(0.05f, -0.43f, -0.15f);
            }
            PlaceHolder(position);
            if (WeaponPatches.currentGunInteractController != null)
                WeaponPatches.currentGunInteractController.framesAfterEnabled = 0;
        }

        // The holder where the right-handed hold has it, mirrored for the left hand (VRPlayerManager.MirroredHolderPosition; the hold's
        // rotation is mirrored by PlaceRightHandForGun, read once here)
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
            GrenadeSlot.NoteThrown(controller.Item);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeHold] thrown from the {(Handedness.GunInLeftHand ? "left" : "right")} hand");
            controller.ThrowGrenadeInner(null, 0f, 1f, Vector3.zero, 1f, false, true);
        }

        private static void PutBack()
        {
            putBack = true;
            if (debugLog)
                Plugin.MyLog.LogInfo("[GrenadeHold] let go before the pin was out: back to the slot");
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

            // The ring on the finger: it stays there (trigger open or not) until the grenade's thrown or the pin's pushed back in
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
                    if (debugLog)
                        Plugin.MyLog.LogInfo($"[GrenadeHold] finger off the ring ({(down ? $"pulled {lateral * 100f:0} cm off the line" : "trigger let go")}) at {pull * 100f:0.0} cm");
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

        // The pin out and its ring on the finger, the grenade armed in the other hand: the ring taken away from the grenade and brought
        // back latches the hand on it where the pin came out (no button: the ring's on the finger already), and pushing it in along the
        // pull seats the pin - Tarkov's own put-back then (PutGrenadeBack: its pin-back clip, the grenade idle and safe again).
        // Pulling back out or off the line leaves the pin out, the ring still on the finger.
        private static void UpdateRepin()
        {
            if (!armed || thrown || !holdDown)
            {
                if (ringLatched)
                    Unlatch();
                return;
            }
            Transform joint = HookJoint();
            if (joint == null)
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
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[GrenadeHold] pin not pushed in ({(lateral > ringLateralEscape ? $"{lateral * 100f:0} cm off the line" : "pulled back out")}) at {pull * 100f:0.0} cm");
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
            if (holderTurnedArmed && VRGlobals.weaponHolder != null)
                VRGlobals.weaponHolder.transform.localRotation = Quaternion.Euler(15, 275, 90);
            holderTurnedArmed = false;
            Haptic(Handedness.OffHandSource, 0.6f, 0.06f);
            Haptic(Handedness.GunHandSource, 0.25f, 0.04f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeHold] pin pushed back into {controller.Item.LocalizedShortName()}: safe again");
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
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeHold] {(pushing ? "pin back at the hole of" : "finger in the ring of")} {controller.Item.LocalizedShortName()} ({(latchedOnRightArm ? "right" : "left")} hand)");
        }

        // The arm's IK target on the ring where the clip's hand holds it, the ring where the pull has it; mirrored across the grenade for
        // the right hand (the clip's is a left hand). The right arm takes it through RealLeftHandedPatches (VRReloadController.ArmLatch);
        // the left directly, re-asserted every frame like the reload's latch (VRPlayerManager's edges set it back).
        private static void PlaceLatch()
        {
            Matrix4x4 ringMatrix = RingMatrix(pull);
            Vector3 ringPosition = ringMatrix.MultiplyPoint3x4(Vector3.zero);
            Vector3 position = ringMatrix.MultiplyPoint3x4(ringData.latchPosition);
            Quaternion rotation = RingRotation() * ringData.latchRotation;
            if (latchedOnRightArm)
            {
                // The mirrored grenade: the clip's left hand on the ring, mirrored with it (the position already is). Unmirrored: across
                // the plane through the ring itself - the grenade isn't mirrored, its ring is where it is.
                if (TryMirrorNormal(out Vector3 mirrorNormal))
                {
                    if (TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(rotation, mirrorNormal, rightToLeft: false, out Quaternion mirroredPalm))
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
                latchedArm.solver.target = latch;
        }

        private static void Unlatch()
        {
            if (ringLatched && !latchedOnRightArm && latchedArm != null && latchedArm.solver.target == latch)
                latchedArm.solver.target = latchedArmTargetBefore != null ? latchedArmTargetBefore : VRGlobals.vrPlayer?.LeftHand?.transform;
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
                pinOnFingerPosition = Quaternion.Inverse(joint.rotation) * (pinPosition - joint.position);
                pinOnFingerRotation = Quaternion.Inverse(joint.rotation) * pinRotation;
                pinInHand = true;
                repinArmed = false;
                pinReseated = false;
            }
            // The hand off the ring's latch, the ring on its index finger from here
            Unlatch();
            Haptic(Handedness.OffHandSource, 0.8f, 0.07f);
            Haptic(Handedness.GunHandSource, 0.3f, 0.04f);
            RequestArm("pin out");
        }

        // The pin on the grenade: at rest on its seat, slid out along its pull
        private static void PinPose(float slid, out Vector3 position, out Quaternion rotation)
        {
            Transform parent = pin.parent;
            rotation = parent.rotation * pinRestLocalRotation;
            position = parent.TransformPoint(pinRestLocalPosition) + PullAxisWorld() * slid;
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

        // The ring's full world matrix with the pin slid that far out, scale included (a mirrored grenade's -1 reaches it); its rotation
        // Unity's way (parent rotations multiplied, the scale's sign left out - what SetPositionAndRotation and HandMirror take)
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

        // The off controller's place in the world (the rig's off hand follows it, whichever hand holds the grenade)
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

        // The off hand's index middle joint to where it hooks the ring
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

        // After every LateUpdate: the pin where the pull or the fingers have it (the animator wrote its own pose this frame), the rush ended
        private static void OnBeforeRender()
        {
            if (rushing != null && Time.time >= rushUntil)
            {
                if (rushing._firearmsAnimator != null && !VRGrenadeControllerThrowing())
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
            if (pin == null)
                return;
            Transform joint = pinInHand ? HookJoint() : null;
            if (ringLatched || pull > 0f || joint != null || pinReseated)
                pin.localScale = pinRestLocalScale;
            if (ringLatched && pushingIn && joint != null)
            {
                // Pushed back in: from the finger onto its seat as the push goes (his report: the pin left the hand as it got close)
                float seated = 1f - Mathf.Clamp01(pull / pinFreeDistance);
                PinPose(pull, out Vector3 seatPosition, out Quaternion seatRotation);
                pin.SetPositionAndRotation(Vector3.Lerp(joint.position + joint.rotation * pinOnFingerPosition, seatPosition, seated),
                    Quaternion.Slerp(joint.rotation * pinOnFingerRotation, seatRotation, seated));
            }
            else if (ringLatched || (!pinInHand && pull > 0f))
            {
                PinPose(pull, out Vector3 position, out Quaternion rotation);
                pin.SetPositionAndRotation(position, rotation);
            }
            else if (joint != null)
            {
                pin.SetPositionAndRotation(joint.position + joint.rotation * pinOnFingerPosition, joint.rotation * pinOnFingerRotation);
            }
            else if (pinReseated)
            {
                // Pushed back in: on its seat through Tarkov's pin-back clip (its pin comes from the clip's own hand)
                PinPose(0f, out Vector3 position, out Quaternion rotation);
                pin.SetPositionAndRotation(position, rotation);
            }
        }

        // VRGrenadeController's Spawn prefix, before it sets the hold up: what the in-hand object had been before (once per controller,
        // that prefix calls itself)
        internal static void NoteSpawn(GrenadeHandsController grenade)
        {
            if (!debugLog || grenade == null || grenade == notedSpawnFor)
                return;
            notedSpawnFor = grenade;
            Transform weaponRoot = grenade.WeaponRoot;
            Transform parent = weaponRoot != null ? weaponRoot.parent : null;
            string setup = parent == null ? "no Weapon_root parent"
                : parent.name == "weaponHolder" ? "its object still in a VR hold"
                : parent.Find("RightHandPositioner") != null ? "its object in the VR hands before"
                : "its object new to the VR hands";
            if (GrenadeSlot.CameFromSlot(grenade.ControllerGameObject))
                setup += ", copied for the slot before";
            spawnSetup = $"{setup} (object {grenade.ControllerGameObject.GetInstanceID()})";
            Plugin.MyLog.LogInfo($"[GrenadeHold] {grenade.Item.LocalizedShortName()} spawning: {spawnSetup}");
        }

        // Where it sits in the holding palm, and the hold's own transforms
        private static void LogHoldPose()
        {
            Transform holder = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform : null;
            Transform weaponRoot = controller.WeaponRoot;
            Transform body = controller._grenadeTransform;
            LimbIK arm = VRGlobals.ikManager != null ? (Handedness.GunInLeftHand ? VRGlobals.ikManager.leftArmIk : VRGlobals.ikManager.rightArmIk) : null;
            Transform palm = arm != null && arm.solver != null && arm.solver.bone3 != null ? arm.solver.bone3.transform : null;
            string inPalm = palm != null && body != null
                ? $"body at {(palm.InverseTransformPoint(body.position) * 100f).ToString("F1")} cm in the palm, turned {(Quaternion.Inverse(palm.rotation) * body.rotation).eulerAngles.ToString("F0")}"
                : "no palm/body";
            string marker = holder != null && holder.parent != null && VRGlobals.player != null && VRGlobals.player._markers.Length > 1
                ? $"{Vector3.Distance(VRGlobals.player._markers[1].position, holder.parent.position) * 100f:0.0} cm"
                : "?";
            // The mirrored left hold: the left hand's target against the grip marker, the rendered palm against that target and the
            // controller's rig hand (his report: the left hand jumps off the grenade it takes)
            string leftHold = "";
            if (MirroredInLeftHand && palm != null && VRGlobals.player != null && VRGlobals.player._markers.Length > 1 && VRGlobals.vrPlayer != null)
            {
                Transform gripMarker = VRGlobals.player._markers[1];
                Vector3 target = RealLeftHandedPatches.GrenadeLeftTarget;
                leftHold = $"; LEFT mirrored: target {Vector3.Distance(target, gripMarker.position) * 100f:0.0} cm off the grip marker (EFT's right target "
                    + $"{RealLeftHandedPatches.GrenadeIkGap * 100f:0.0} cm off it, right weight {RealLeftHandedPatches.GrenadeRightWeight:0.00}), palm "
                    + $"{Vector3.Distance(palm.position, target) * 100f:0.0} cm and {Quaternion.Angle(palm.rotation, RealLeftHandedPatches.GrenadeLeftTargetRotation):0} deg off "
                    + $"that target, {Vector3.Distance(palm.position, VRGlobals.vrPlayer.RightHand.transform.position) * 100f:0.0} cm off the controller's rig hand, "
                    + $"grip marker {Vector3.Distance(gripMarker.position, mirroredPositioner.position) * 100f:0.0} cm off the positioner";
            }
            Plugin.MyLog.LogInfo($"[GrenadeHold] {controller.Item.LocalizedShortName()} 0.3 s in hand ({(notedSpawnFor == controller ? spawnSetup : "spawn not seen")}): {inPalm}{leftHold}; "
                + $"holder {(holder != null ? holder.localPosition.ToString("F3") + " " + holder.localEulerAngles.ToString("F0") : "none")}, "
                + $"Weapon_root {(weaponRoot != null ? weaponRoot.localPosition.ToString("F3") + " " + weaponRoot.localEulerAngles.ToString("F0") + " under " + weaponRoot.parent?.name : "none")}, "
                + $"hands root {(VRGlobals.oldWeaponHolder != null ? VRGlobals.oldWeaponHolder.transform.localEulerAngles.ToString("F0") : "none")}, "
                + $"grip marker {marker} off the hand, operation {controller.CurrentOperation?.GetType().Name}, arm IK {(arm != null ? arm.enabled.ToString() : "none")}");
        }

        private static void Haptic(Valve.VR.SteamVR_Input_Sources source, float amplitude, float duration)
        {
            TarkovVR.Source.Misc.HapticMixer.PlayPulse(source, amplitude, duration, 95f);
        }
    }
}
