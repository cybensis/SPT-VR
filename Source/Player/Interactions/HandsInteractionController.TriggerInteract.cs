using EFT.Interactive;
using TarkovVR.Patches.Core.Player;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Source.Player.Interactions
{
    // The interact press: an empty hand's trigger on a body opens its loot grid, in a container opens it, and on a door the hands
    // don't drive opens or closes it. The grip on a body only drags.
    internal partial class HandsInteractionController
    {
        public static bool triggerInteract = true;              // false = use the grip: a quick tap on a body, a press on a container / door
        public static float interactTriggerPress = 0.75f;       // trigger axis: pressed from here, released under interactTriggerRelease
        public static float interactTriggerRelease = 0.35f;

        private readonly bool[] interactTriggerHeld = new bool[2];      // [0] off hand, [1] dominant
        private readonly bool[] interactTriggerPressed = new bool[2];   // pressed this frame and not taken yet

        // Called from the top of Update, before its early return, so a trigger held through a menu or a sprint isn't read as a press
        // when they end. A press by a hand on a door or key is skipped: the doors take that trigger this frame (UpdatePhysicalDoors),
        // before the hand reads as free.
        private void UpdateInteractTriggers()
        {
            for (int hand = 0; hand < 2; hand++)
            {
                bool dominant = hand == 1;
                VRSingle trigger = dominant ? Handedness.GunHandTrigger : Handedness.OffHandTrigger;
                float axis = trigger != null ? trigger.axis : 0f;
                bool held = interactTriggerHeld[hand] ? axis > interactTriggerRelease : axis >= interactTriggerPress;
                interactTriggerPressed[hand] = held && !interactTriggerHeld[hand] && !PhysicalDoors.HoldsDoor(dominant);
                interactTriggerHeld[hand] = held;
            }
        }

        // True when that hand's trigger was pressed this frame and the hand is free. The press is consumed, so later checks this
        // frame don't see it.
        private bool TakeInteractPress(bool dominant, string what)
        {
            int hand = dominant ? 1 : 0;
            if (!interactTriggerPressed[hand])
                return false;
            // The gun hand on the gun: its trigger fires the gun, so there is nothing to log
            if (dominant && !ChestHang.Detached && !(VRGlobals.player != null && VRGlobals.player.HandsIsEmpty))
                return false;
            string busy = InteractHandBusy(dominant);
            if (busy != null)
            {
                if (logBodyGrips)
                    Plugin.MyLog.LogInfo($"[Interact] {(dominant ? "dominant" : "off")} hand's trigger on {what} not taken: {busy}");
                return false;
            }
            interactTriggerPressed[hand] = false;
            return true;
        }

        // Returns null when that hand is free (the other hand may hold something), else what keeps it busy. The off hand's trigger
        // also holds the breath while aiming, runs a gun part's prompt, and drops the backpack / turns the headlight on in those zones.
        private string InteractHandBusy(bool dominant)
        {
            if (HoldsLoot(dominant))
                return "holding loot";
            if (isSummoningLoot && heldInDominantHand == dominant)
                return "loot coming to the hand";
            if (isDraggingBody && bodyGrabDominant == dominant)
                return "dragging a body";
            if (VRGlobals.usingItem || EatingInteractionController.ManualActive)
                return "using an item";
            if (PhysicalDoors.HoldsDoor(dominant))
                return "on a door";
            if (WeaponPatches.grenadeEquipped)
                return "a grenade in hand";
            if (dominant)
                return DominantHandBusy();
            if (NearForegrip())
                return "at the gun's foregrip";
            if (VRReloadController.OwnsOffHand)
                return "a magazine";
            if (ChestHang.OffHandAtGunGrip)
                return "at the gun's grip";
            if (BodyWeapons.OffHandAtBodyGun)
                return "at a gun on the body";
            if (leftHandState.inBackpack || leftHandState.inHeadGear || leftHandState.inScope)
                return "in a gear zone";
            if (WeaponPatches.currentGunInteractController != null && WeaponPatches.currentGunInteractController.lastHitCompIndex != -1)
                return "on a gun part";
            if (VRGlobals.firearmController != null && VRGlobals.firearmController.IsAiming)
                return "aiming";
            return null;
        }

        // The trigger with the palm on a body opens its loot grid. Not with the gun in the left hand: the palms used here are then drawn
        // on the other arms (same as for the body grab).
        private bool TryTriggerBodyLoot(bool dominant)
        {
            if (!triggerInteract || !interactTriggerPressed[dominant ? 1 : 0] || Handedness.GunInLeftHand || BodyHand(dominant) == null)
                return false;
            if (!TryFindBodyBone(PalmOrigin(dominant), out _, out Corpse corpse, out _, out _) || corpse == null)
                return false;
            if (!TakeInteractPress(dominant, "a body"))
                return false;
            OpenCorpseLoot(corpse);
            return true;
        }
    }
}
