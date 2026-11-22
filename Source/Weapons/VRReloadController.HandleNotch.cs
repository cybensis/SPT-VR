using System.Collections.Generic;
using EFT.InventoryLogic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The MP5's and AUG A1's bolt never locks back on its own (Tarkov's ManualBoltCatch). Instead the handle is pulled all the way back and
    // turned up into a notch (<gun>_bolt_open), where it stays when let go. Slapped out of the notch, it runs forward (<gun>_reload_catch_0).
    // How far it turns comes from the gun's clips. The lift only counts from full pull onward, so a plain rack can't end up in the notch.
    public static partial class VRReloadController
    {
        internal sealed class HandleNotch
        {
            public Vector3 axis;        // in the handle bone's frame
            public float angle;         // degrees
        }

        private static readonly Dictionary<string, HandleNotch> handleNotches = new Dictionary<string, HandleNotch>();

        private static HandleNotch HandleNotchOf(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            if (weapon == null || !weapon.ManualBoltCatch)
                return null;
            LoadRecordedGrips();
            handleNotches.TryGetValue(AnimationFamily(firearmController), out HandleNotch notch);
            return notch;
        }

        // A bolt already held back has its handle in the notch, so this pull can only take it out
        private static void StartHandleNotch(FirearmController firearmController, Vector3 backWorld)
        {
            HandleNotches.notchTurn = 0f;
            HandleNotches.notchClicked = false;
            HandleNotches.heldNotch = handleCarrier != null && !BoltLockedBack ? HandleNotchOf(firearmController) : null;
            if (HandleNotches.heldNotch == null)
                return;
            HandleNotches.notchPointOnHand = LatchedHandRig.InverseTransformPoint(ChargingHandleGrabPoint(backWorld));
            HandleNotches.handleCarrierRestLocalRotation = handleCarrier.localRotation;
        }

        private static void UpdateHandleNotch()
        {
            if (HandleNotches.heldNotch == null)
                return;
            // +z of the gun's body is up
            float height = handleCarrier.parent.InverseTransformPoint(LatchedHandRig.TransformPoint(HandleNotches.notchPointOnHand)).z;
            if (handlePull < heldHandleTravel * handleFullPull)
            {
                HandleNotches.notchStartHeight = height;
                HandleNotches.notchTurn = 0f;
                HandleNotches.notchClicked = false;
            }
            else
            {
                HandleNotches.notchTurn = Mathf.Clamp01((height - HandleNotches.notchStartHeight) / HandleNotches.handleNotchLift);
                if (HandleNotches.notchTurn >= HandleNotches.handleNotchHold && !HandleNotches.notchClicked)
                {
                    HandleNotches.notchClicked = true;
                    PlayFirstGunSound(handleForFirearm, HandleSoundHand, "OnCatch", "BoltCatch", "BoltUp");
                    PulseHandleHand(0.5f, 0.04f);
                }
            }
            handleRider.localRotation = Quaternion.AngleAxis(HandleNotches.heldNotch.angle * HandleNotches.notchTurn, HandleNotches.heldNotch.axis);
        }

        // LateUpdate, together with the carrier: the animator's held pose turns the handle with the pull, and this adds the hand's turn
        private static void ApplyHandleNotch()
        {
            if (HandleNotches.heldNotch != null)
                handleCarrier.localRotation = HandleNotches.handleCarrierRestLocalRotation * Quaternion.AngleAxis(HandleNotches.heldNotch.angle * HandleNotches.notchTurn, HandleNotches.heldNotch.axis);
        }

        private static bool EndHandleNotch()
        {
            bool staysBack = HandleNotches.heldNotch != null && handleFullyBack && HandleNotches.notchTurn >= HandleNotches.handleNotchHold;
            if (HandleNotches.heldNotch != null)
            {
                if (handleCarrier != null)
                    handleCarrier.localRotation = HandleNotches.handleCarrierRestLocalRotation;
                if (handleRider != null)
                    handleRider.localRotation = Quaternion.identity;
            }
            HandleNotches.heldNotch = null;
            return staysBack;
        }

        public static class HandleNotches
        {
            public static float handleNotchLift = 0.03f;        // m the hand lifts from full pull to turn the handle all the way into the notch
            public static float handleNotchHold = 0.6f;         // fraction of that turn needed at the let-go for the handle to stay in the notch
            internal static HandleNotch heldNotch;               // null = this pull can't end in a notch
            internal static float notchTurn;                     // 0 to 1
            internal static bool notchClicked;
            internal static float notchStartHeight;
            internal static Vector3 notchPointOnHand;            // the grab point on the handle in the controller's frame, so a wrist roll lifts it too
            internal static Quaternion handleCarrierRestLocalRotation;
        }
    }
}
