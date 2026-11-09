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
    //    pulling it along the pin slides the pin out; out past pinFreeDistance it's armed, the pin stays in those fingers until that trigger
    //    opens (then it's gone).
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
        public static bool debugLog = true;                 // one line per step (first round)

        private static GrenadeHandsController controller;
        private static Transform pin, ring;
        private static GrenadeRings.RingData ringData;
        private static Vector3 pinRestLocalPosition;
        private static Quaternion pinRestLocalRotation = Quaternion.identity;
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
        private static bool pinInHand, pinHidden;
        private static Vector3 pinInPalmPosition;
        private static Quaternion pinInPalmRotation = Quaternion.identity;
        private static bool offTriggerDown;
        private static bool ringInReach;
        private static bool hooked;

        // The off hand on the ring or holding the pin: its fingers (VRReloadController.OffHandPose) and its arm (ArmLatch) are ours
        public static bool OffHandBusy => controller != null && (ringLatched || pinInHand);
        internal static Vector3[] RingFingers => controller != null && (ringLatched || pinInHand) && ringData != null ? ringData.fingers : null;
        internal static Transform ArmLatch(bool rightArm) => controller != null && ringLatched && latch != null && latchedOnRightArm == rightArm ? latch : null;
        public static bool PinOut => controller != null && armed;

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
            if (controller == null || thrown || putBack)
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
            heldSeen = GrenadeSlot.TakenByHold(grenade.Item);
            if (GrenadeRings.TryFind(grenade.WeaponRoot, out pin, out ring, out ringData))
            {
                pinRestLocalPosition = pin.localPosition;
                pinRestLocalRotation = pin.localRotation;
                pinWasActive = pin.gameObject.activeSelf;
            }
            else if (debugLog)
            {
                Plugin.MyLog.LogInfo($"[GrenadeHold] {grenade.Item.LocalizedShortName()}: no ring to pull (model {grenade.HandsHierarchy?.name}, pin-like bones: {GrenadeRings.PinLikeNames(grenade.WeaponRoot)}) - ready when held");
            }
            // In the left hand (taken from the slot with it): the hold mirrored on the left controller
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
                    pin.gameObject.SetActive(pinWasActive);
                }
                // Only this grenade's rush: the next one's draw may be rushing already (GrenadeSlot, at its spawn)
                if (rushing != null && rushing == controller)
                {
                    if (rushing._firearmsAnimator != null)
                        rushing._firearmsAnimator.Animator.speed = 1f;
                    rushing = null;
                }
            }
            controller = null;
            pinRushEnds = -1f;
            pin = ring = null;
            ringData = null;
            heldSeen = holdDown = false;
            armRequested = armed = thrown = putBack = false;
            pull = 0f;
            pinInHand = pinHidden = false;
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
            if (Handedness.GunInLeftHand && VRGlobals.vrPlayer != null)
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

            if (pinInHand)
            {
                // The trigger opens: the pin's gone
                if (!down)
                {
                    pinInHand = false;
                    pinHidden = true;
                    pin.gameObject.SetActive(false);
                }
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

        private static void Latch()
        {
            if (latch == null)
                latch = new GameObject("VRGrenadeRingLatch").transform;
            latchedOnRightArm = Handedness.GunInLeftHand;
            latchedArm = VRGlobals.ikManager != null ? (latchedOnRightArm ? VRGlobals.ikManager.rightArmIk : VRGlobals.ikManager.leftArmIk) : null;
            latchedArmTargetBefore = latchedArm != null ? latchedArm.solver.target : null;
            controllerAtLatch = OffController();
            pull = 0f;
            ringLatched = true;
            PlaceLatch();
            Haptic(Handedness.OffHandSource, 0.4f, 0.04f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GrenadeHold] finger in the ring of {controller.Item.LocalizedShortName()} ({(latchedOnRightArm ? "right" : "left")} hand)");
        }

        // The arm's IK target on the ring where the clip's hand holds it, the ring where the pull has it; mirrored across the grenade for
        // the right hand (the clip's is a left hand). The right arm takes it through RealLeftHandedPatches (VRReloadController.ArmLatch);
        // the left directly, re-asserted every frame like the reload's latch (VRPlayerManager's edges set it back).
        private static void PlaceLatch()
        {
            PinPose(out Vector3 pinPosition, out Quaternion pinRotation);
            Vector3 ringPosition = pinPosition + pinRotation * ring.localPosition;
            Quaternion ringRotation = pinRotation * ring.localRotation;
            Vector3 position = ringPosition + ringRotation * ringData.latchPosition;
            Quaternion rotation = ringRotation * ringData.latchRotation;
            if (latchedOnRightArm && RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal)
                && TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(rotation, planeNormal, rightToLeft: false, out Quaternion mirrored))
            {
                position = RealLeftHandedPatches.ReflectPoint(position, planePoint, planeNormal);
                rotation = mirrored;
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
            if (!pinInHand)
                pull = 0f;
        }

        private static void PinComesOut()
        {
            PinPose(out Vector3 pinPosition, out Quaternion pinRotation);
            Transform palm = OffPalm();
            if (palm != null)
            {
                pinInPalmPosition = palm.InverseTransformPoint(pinPosition);
                pinInPalmRotation = Quaternion.Inverse(palm.rotation) * pinRotation;
                pinInHand = true;
            }
            // The hand off the ring's latch, the pin in its fingers from here
            Unlatch();
            Haptic(Handedness.OffHandSource, 0.8f, 0.07f);
            Haptic(Handedness.GunHandSource, 0.3f, 0.04f);
            RequestArm("pin out");
        }

        // Where the pin is now: at rest on the grenade, slid out along its pull
        private static void PinPose(out Vector3 position, out Quaternion rotation)
        {
            Transform parent = pin.parent;
            rotation = parent.rotation * pinRestLocalRotation;
            position = parent.TransformPoint(pinRestLocalPosition) + PullAxisWorld() * pull;
        }

        private static Vector3 PullAxisWorld()
        {
            return pin.parent.TransformDirection(ringData.pullAxis).normalized;
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

        // The off hand's index middle joint to where it hooks the ring (mirrored onto the right hand like the latch)
        private static float HookDistance()
        {
            Transform palm = OffPalm();
            if (palm == null)
                return float.MaxValue;
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
            if (hookJoint == null)
                return float.MaxValue;
            Vector3 hookPoint = ring.TransformPoint(ringData.hookPoint);
            if (Handedness.GunInLeftHand && RealLeftHandedPatches.GunPlane(out Vector3 planePoint, out Vector3 planeNormal))
                hookPoint = RealLeftHandedPatches.ReflectPoint(hookPoint, planePoint, planeNormal);
            return Vector3.Distance(hookJoint.position, hookPoint);
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
            if (pin == null || pinHidden)
                return;
            if (pinInHand)
            {
                Transform palm = OffPalm();
                if (palm != null)
                    pin.SetPositionAndRotation(palm.TransformPoint(pinInPalmPosition), palm.rotation * pinInPalmRotation);
            }
            else if (ringLatched || pull > 0f)
            {
                PinPose(out Vector3 position, out Quaternion rotation);
                pin.SetPositionAndRotation(position, rotation);
            }
        }

        private static void Haptic(Valve.VR.SteamVR_Input_Sources source, float amplitude, float duration)
        {
            TarkovVR.Source.Misc.HapticMixer.PlayPulse(source, amplitude, duration, 95f);
        }
    }
}
