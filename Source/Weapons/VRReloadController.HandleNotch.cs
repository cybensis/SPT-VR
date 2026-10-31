using System.Collections.Generic;
using EFT.InventoryLogic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The MP5's and the AUG A1's bolt never locks back on its own (Tarkov's ManualBoltCatch): the handle does it, pulled all the way back and
    // turned up into a notch (<gun>_bolt_open), where it stays when let go. Slapped out of it, it runs forward (<gun>_reload_catch_0). How
    // far it turns comes from the gun's clips. The lift only counts from full pull on, so a plain rack can't end up in the notch.
    public static partial class VRReloadController
    {
        public static float handleNotchLift = 0.03f;        // m the hand lifts from full pull to turn the handle all the way into the notch
        public static float handleNotchHold = 0.6f;         // share of that it has to be turned when let go to stay there

        private sealed class HandleNotch
        {
            public Vector3 axis;        // in the handle bone's frame
            public float angle;         // degrees
        }

        private static readonly Dictionary<string, HandleNotch> handleNotches = new Dictionary<string, HandleNotch>();
        private static HandleNotch heldNotch;               // null = this pull can't end in a notch
        private static float notchTurn;                     // 0 to 1
        private static bool notchClicked;
        private static float notchStartHeight;
        private static Vector3 notchPointOnHand;            // the handle where the hand took it, in the controller's frame, so a wrist roll lifts it too
        private static Quaternion handleCarrierRestLocalRotation;

        private static HandleNotch HandleNotchOf(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            if (weapon == null || !weapon.ManualBoltCatch)
                return null;
            LoadRecordedGrips();
            handleNotches.TryGetValue(AnimationFamily(firearmController), out HandleNotch notch);
            return notch;
        }

        // A bolt already held back has the handle in the notch: this pull only takes it out
        private static void StartHandleNotch(FirearmController firearmController, Vector3 backWorld)
        {
            notchTurn = 0f;
            notchClicked = false;
            heldNotch = handleCarrier != null && !BoltLockedBack ? HandleNotchOf(firearmController) : null;
            if (heldNotch == null)
                return;
            notchPointOnHand = LatchedHandRig.InverseTransformPoint(ChargingHandleGrabPoint(backWorld));
            handleCarrierRestLocalRotation = handleCarrier.localRotation;
        }

        private static void UpdateHandleNotch()
        {
            if (heldNotch == null)
                return;
            // +z of the gun's body is up
            float height = handleCarrier.parent.InverseTransformPoint(LatchedHandRig.TransformPoint(notchPointOnHand)).z;
            if (handlePull < heldHandleTravel * handleFullPull)
            {
                notchStartHeight = height;
                notchTurn = 0f;
                notchClicked = false;
            }
            else
            {
                notchTurn = Mathf.Clamp01((height - notchStartHeight) / handleNotchLift);
                if (notchTurn >= handleNotchHold && !notchClicked)
                {
                    notchClicked = true;
                    PlayFirstGunSound(handleForFirearm, "BoltUp", "BoltCatch");
                    PulseHandleHand(0.5f, 0.04f);
                }
            }
            handleRider.localRotation = Quaternion.AngleAxis(heldNotch.angle * notchTurn, heldNotch.axis);
        }

        // LateUpdate, with the carrier: the animator's held pose turns the handle with the pull, this turns it with the hand
        private static void ApplyHandleNotch()
        {
            if (heldNotch != null)
                handleCarrier.localRotation = handleCarrierRestLocalRotation * Quaternion.AngleAxis(heldNotch.angle * notchTurn, heldNotch.axis);
        }

        private static bool EndHandleNotch()
        {
            bool staysBack = heldNotch != null && handleFullyBack && notchTurn >= handleNotchHold;
            if (heldNotch != null)
            {
                if (handleCarrier != null)
                    handleCarrier.localRotation = handleCarrierRestLocalRotation;
                if (handleRider != null)
                    handleRider.localRotation = Quaternion.identity;
            }
            heldNotch = null;
            return staysBack;
        }
    }
}
