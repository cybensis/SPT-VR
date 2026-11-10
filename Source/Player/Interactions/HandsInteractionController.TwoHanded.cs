using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Weapons;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // A held loot item taken in both hands. The free hand gripping within reach of the item's surface takes hold where it touched (snapped
    // onto the surface the way the grip places an item against a palm, Grasp.TryGraspItemShift). The first hand stays the anchor: the item
    // keeps its grip there exactly, and turns about that grip so the line to the second grip follows the second controller (a support hand
    // on a rifle), half the second hand's roll about that line added. The second hand is drawn locked to its grip on the item (its arm's IK
    // target and EFT's hand marker both on secondHandLatch, a child of the item), its fingers closed on it by the grip like the first's.
    // Letting go with the second hand leaves the item with the first, where it is; letting go with the first passes it to the second
    // (HandOffToSecondHand); letting go with the last drops or throws it as before.
    internal partial class HandsInteractionController
    {
        public static bool twoHandedLoot = true;
        public static float secondHandReach = 0.06f;        // m: a free palm this close to the held item's surface takes it on a grip press
        public static float secondHandRollShare = 0.5f;     // how much of the second hand's roll about the grip-to-grip line the item takes

        private bool secondHandHolding;
        private Transform secondHandLatch;                  // the second arm's target: its grip on the item, a child of it
        private Vector3 secondHandInItemPosition;           // that grip in the item's frame (unscaled), and the anchor's
        private Quaternion secondHandInItemRotation;
        private Vector3 anchorInItemPosition;
        private Vector3 secondGripInRig;                    // the grip, snapped onto the surface, in that controller's rig frame

        // This hand holds the held item (the anchor or the second hand)
        private bool HoldsLoot(bool dominant)
        {
            return heldItem != null && (heldInDominantHand == dominant || (secondHandHolding && secondHandLatch != null));
        }

        // What this hand's arm reaches for while it holds the item: its rig hand (the anchor), or its grip on the item (the second hand)
        private Transform GraspRig(bool dominant)
        {
            if (heldItem == null)
                return null;
            if (dominant == heldInDominantHand)
                return cachedHand;
            return secondHandHolding ? secondHandLatch : null;
        }

        private static Transform HandRigTransform(bool dominant)
        {
            if (VRGlobals.vrPlayer == null)
                return null;
            GameObject hand = dominant ? VRGlobals.vrPlayer.RightHand : VRGlobals.vrPlayer.LeftHand;
            return hand != null ? hand.transform : null;
        }

        private static LimbIK HandArmIk(bool dominant)
        {
            if (VRGlobals.ikManager == null)
                return null;
            return dominant ? VRGlobals.ikManager.rightArmIk : VRGlobals.ikManager.leftArmIk;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Update, after the loot pointer and before the held item's drop check: the free hand taking hold, the second hand letting go
        private void UpdateSecondHand()
        {
            // (a use or an eat drives the arms itself, and puts the held item away)
            if (!twoHandedLoot || heldItem == null || !HeldItemAlive() || VRGlobals.usingItem || EatingInteractionController.ManualActive)
            {
                EndSecondHand();
                return;
            }
            bool secondDominant = !heldInDominantHand;
            VRBoolean grip = secondDominant ? primaryHandGrip : secondaryHandGrip;
            if (secondHandHolding)
            {
                if (!grip.state)
                    EndSecondHand();
                else
                    AssertSecondHandLatch();
                return;
            }
            if (isSummoningLoot || !grip.stateDown || !SecondHandFree(secondDominant))
                return;
            GraspHand anchorGrasp = graspHands[heldInDominantHand ? 1 : 0];
            if (anchorGrasp.item != heldItem || anchorGrasp.shape == null || anchorGrasp.pinch)
                return;
            Transform item = heldItem.transform;
            if (!TryGetGraspContact(secondDominant, false, out Vector3 contact, out _, out _)
                || !anchorGrasp.shape.NearestSurface(contact, item.position, item.rotation, out Vector3 surface, out _, out bool inside)
                || (!inside && (surface - contact).sqrMagnitude > secondHandReach * secondHandReach))
                return;
            BeginSecondHand(secondDominant, anchorGrasp.shape);
        }

        // The hand is on nothing else that takes its grip (the same gates the loot pointer uses for that hand)
        private bool SecondHandFree(bool dominant)
        {
            if (dominant)
                return DominantHandFree() && !rightCorpseTapArmed;
            return !NearForegrip() && !VRReloadController.OwnsOffHand && !Handedness.GunInLeftHand && !ChestHang.OffHandAtGunGrip
                && !isDraggingBody && !bodyInteractionArmed;
        }

        private void BeginSecondHand(bool secondDominant, GraspShape shape)
        {
            Transform rig = HandRigTransform(secondDominant);
            if (rig == null || cachedHand == null)
                return;
            Transform item = heldItem.transform;
            // Onto the surface where it touched: the hand moves by what the item would to sit in it
            Vector3 handPosition = rig.position;
            if (TryGraspItemShift(secondDominant, shape, false, item.position, item.rotation, out Vector3 itemShift))
                handPosition -= itemShift;
            if (secondHandLatch == null)
                secondHandLatch = new GameObject("SPTVR second hand on loot").transform;
            secondHandLatch.SetParent(item, worldPositionStays: false);
            secondHandLatch.SetPositionAndRotation(handPosition, rig.rotation);
            Quaternion itemInverse = Quaternion.Inverse(item.rotation);
            secondHandInItemPosition = itemInverse * (handPosition - item.position);
            secondHandInItemRotation = itemInverse * rig.rotation;
            anchorInItemPosition = itemInverse * (cachedHand.position - item.position);
            secondGripInRig = Quaternion.Inverse(rig.rotation) * (handPosition - rig.position);
            secondHandHolding = true;
            AssertSecondHandLatch();
            VRInput.Haptic.Execute(0, 0.06f, 1, 0.45f, secondDominant ? primaryInputSource : secondaryInputSource);
        }

        // Every frame the second hand holds: its arm reaches for its grip on the item. EFT solves the arms onto its hand markers too, so the
        // marker goes with it (ChestHang.KeepGunHandFree does that for a freed right arm already)
        private void AssertSecondHandLatch()
        {
            if (!secondHandHolding || secondHandLatch == null)
                return;
            bool secondDominant = !heldInDominantHand;
            LimbIK arm = HandArmIk(secondDominant);
            if (arm != null && arm.solver != null && arm.solver.target != secondHandLatch)
                arm.solver.target = secondHandLatch;
            int marker = secondDominant ? 1 : 0;
            if (VRGlobals.player != null && VRGlobals.player._markers != null && VRGlobals.player._markers.Length > marker
                && VRGlobals.player._markers[marker] != secondHandLatch)
                VRGlobals.player._markers[marker] = secondHandLatch;
        }

        // The second hand lets go (or the hold ended some other way): its arm back to its controller; the item stays with the anchor where it is
        private void EndSecondHand()
        {
            if (!secondHandHolding)
                return;
            secondHandHolding = false;
            GiveSecondArmBack(!heldInDominantHand);
            if (heldItem != null && cachedHand != null && HeldItemAlive())
            {
                cachedGrabOffset = cachedHand.InverseTransformPoint(heldItem.transform.position);
                cachedGrabRotation = Quaternion.Inverse(cachedHand.rotation) * heldItem.transform.rotation;
            }
            if (secondHandLatch != null)
                secondHandLatch.SetParent(null, worldPositionStays: false);
        }

        // (the grip can be gone with the item - taken into the inventory destroys it and its children - so a dead target counts as ours)
        private void GiveSecondArmBack(bool secondDominant)
        {
            Transform rig = HandRigTransform(secondDominant);
            LimbIK arm = HandArmIk(secondDominant);
            if (arm != null && arm.solver != null && (arm.solver.target == null || arm.solver.target == secondHandLatch))
                arm.solver.target = rig;
            int marker = secondDominant ? 1 : 0;
            if (VRGlobals.player != null && VRGlobals.player._markers != null && VRGlobals.player._markers.Length > marker
                && (VRGlobals.player._markers[marker] == null || VRGlobals.player._markers[marker] == secondHandLatch))
                VRGlobals.player._markers[marker] = rig;
        }

        // LateUpdate: Update bails during a sprint or a menu, and the hold can end meanwhile (the item stowed, taken, dropped)
        private void KeepSecondHand()
        {
            if (!secondHandHolding)
                return;
            if (heldItem == null || !HeldItemAlive() || secondHandLatch == null || VRGlobals.usingItem || EatingInteractionController.ManualActive)
                EndSecondHand();
            else
                AssertSecondHandLatch();
        }

        // The anchor lets go while the second hand holds: the item passes to that hand, brought to where its grip says (the hand and the item
        // move together, so the hand doesn't come off it), and a later release there drops it as usual
        private void HandOffToSecondHand()
        {
            bool newDominant = !heldInDominantHand;
            Transform newRig = HandRigTransform(newDominant);
            if (newRig == null || heldItem == null)
            {
                EndSecondHand();
                return;
            }
            Quaternion itemRotation = newRig.rotation * Quaternion.Inverse(secondHandInItemRotation);
            Vector3 itemPosition = newRig.position - itemRotation * secondHandInItemPosition;
            secondHandHolding = false;
            GiveSecondArmBack(newDominant);
            if (secondHandLatch != null)
                secondHandLatch.SetParent(null, worldPositionStays: false);

            Transform item = heldItem.transform;
            item.SetParent(newRig, worldPositionStays: true);
            item.SetPositionAndRotation(itemPosition, itemRotation);
            heldInDominantHand = newDominant;
            cachedHand = newRig;
            cachedGrabOffset = newRig.InverseTransformPoint(itemPosition);
            cachedGrabRotation = Quaternion.Inverse(newRig.rotation) * itemRotation;
            cachedItemPosition = itemPosition;
            // Its shape goes with the hand still holding it: the old anchor's grip opens without disposing it
            GraspHand oldAnchor = graspHands[newDominant ? 0 : 1];
            GraspHand newAnchor = graspHands[newDominant ? 1 : 0];
            if (newAnchor.item == heldItem && newAnchor.shape == oldAnchor.shape)
            {
                newAnchor.ownsShape = oldAnchor.ownsShape;
                oldAnchor.ownsShape = false;
            }
            NotifyWeightChanged();
            VRInput.Haptic.Execute(0, 0.06f, 1, 0.45f, newDominant ? primaryInputSource : secondaryInputSource);
        }

        // The item's pose with both hands on it (UpdateHeldItemPosition): the anchor's grip kept, turned about it so the grip-to-grip line
        // points at the second controller, plus secondHandRollShare of that hand's roll about the line
        private bool TryTwoHandedPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            bool secondDominant = !heldInDominantHand;
            Transform secondRig = HandRigTransform(secondDominant);
            if (!secondHandHolding || secondRig == null || cachedHand == null)
                return false;
            Quaternion oneHanded = cachedHand.rotation * cachedGrabRotation;
            Vector3 anchorWorld = cachedHand.TransformPoint(cachedGrabOffset) + oneHanded * anchorInItemPosition;
            Vector3 heldLine = oneHanded * (secondHandInItemPosition - anchorInItemPosition);
            // Where that controller puts its grip (it was snapped onto the surface, off the controller)
            Vector3 wantedLine = secondRig.position + secondRig.rotation * secondGripInRig - anchorWorld;
            rotation = oneHanded;
            if (heldLine.sqrMagnitude > 1e-6f && wantedLine.sqrMagnitude > 1e-6f)
            {
                rotation = Quaternion.FromToRotation(heldLine, wantedLine) * oneHanded;
                Quaternion secondHandSays = secondRig.rotation * Quaternion.Inverse(secondHandInItemRotation);
                Quaternion roll = TwistAround(secondHandSays * Quaternion.Inverse(rotation), wantedLine.normalized);
                rotation = Quaternion.Slerp(Quaternion.identity, roll, secondHandRollShare) * rotation;
            }
            position = anchorWorld - rotation * anchorInItemPosition;
            return true;
        }
    }
}
