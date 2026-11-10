using System.Collections.Generic;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using TarkovVR.Source.Controls;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // Which grenade you hold (his ask, with grenades out of the quick-slot radial): the trigger of the hand holding a grenade, held, opens
    // a radial of the other grenades in your quick access; that controller's stick points at one, letting go of the trigger swaps to it
    // (GrenadeSlot.SwapHeldTo: Tarkov's switch, its put-away and draw skipped, the same hand holding it). Only with the pin in.
    public static class GrenadePicker
    {
        public static bool enabled = true;
        public static float aboveHand = 0.12f;      // m the menu floats over the holding controller, turned to face you

        private static GameObject menu;
        private static CircularSegmentUI ui;
        private static readonly List<ThrowWeap> options = new List<ThrowWeap>();
        private static readonly List<ItemIcon> icons = new List<ItemIcon>();    // kept while it's open (the sprites are theirs)
        private static EFT.Player.GrenadeHandsController openFor;
        private static bool triggerDown;

        public static bool Open => menu != null && menu.activeSelf;

        // Every frame a grenade is held (GrenadeHold.UpdateCommand); canPick = held by its grip, the pin in, no hand on the ring
        public static void Update(EFT.Player.GrenadeHandsController grenade, bool canPick)
        {
            float axis = Handedness.GunHandTrigger.axis;
            bool down = triggerDown ? axis > 0.35f : axis > 0.5f;
            bool pressed = down && !triggerDown;
            triggerDown = down;

            if (Open)
            {
                if (!canPick || grenade != openFor)
                {
                    Close();
                    return;
                }
                ui.pickerStick = Handedness.GunHandIsLeft ? VRInput.RawLeftJoystick.axis : VRInput.RawRightJoystick.axis;
                if (down)
                    return;
                int chosen = ui.SelectedSegment;
                ThrowWeap next = chosen >= 0 && chosen < options.Count ? options[chosen] : null;
                Close();
                if (next != null && next != grenade.Item)
                    GrenadeSlot.SwapHeldTo(grenade, next);
                return;
            }
            if (enabled && pressed && canPick)
                TryOpen(grenade);
        }

        private static void TryOpen(EFT.Player.GrenadeHandsController grenade)
        {
            EFT.Player player = VRGlobals.player;
            if (player == null || player.Inventory?.FastAccess == null || player.InventoryController == null)
                return;
            options.Clear();
            icons.Clear();
            List<Sprite> sprites = new List<Sprite>();
            foreach (KeyValuePair<EBoundItem, Item> bound in player.Inventory.FastAccess.BoundItems)
            {
                if (!(bound.Value is ThrowWeap other) || other == grenade.Item || options.Contains(other) || !player.InventoryController.Examined(other))
                    continue;
                ItemIcon icon = ItemViewFactory.LoadItemIcon(other);
                if (icon == null || icon.Sprite == null)
                    continue;
                options.Add(other);
                icons.Add(icon);
                sprites.Add(icon.Sprite);
            }
            if (options.Count == 0)
                return;
            if (menu == null)
            {
                menu = new GameObject("VRGrenadePicker");
                menu.layer = 5;
                ui = menu.AddComponent<CircularSegmentUI>();
                ui.Init();
                menu.SetActive(false);
            }
            ui.CreatePickerUi(sprites.ToArray());
            ui.pickerOnLeftController = Handedness.GunHandIsLeft;
            ui.pickerStick = Vector2.zero;
            openFor = grenade;
            Place();
            menu.SetActive(true);
        }

        private static void Close()
        {
            if (menu != null && menu.activeSelf)
                menu.SetActive(false);
            openFor = null;
            options.Clear();
            icons.Clear();
        }

        // Over the holding controller, facing the head (GrenadeHold's onBeforeRender, after the rig moved)
        public static void Place()
        {
            if (!Open && openFor == null)
                return;
            if (menu == null || VRGlobals.vrOffsetter == null || !ChestHang.HeadPose(out Vector3 head, out _))
                return;
            VRPose pose = Handedness.GunHandIsLeft ? VRInput.LeftHandPose : VRInput.RightHandPose;
            Vector3 position = VRGlobals.vrOffsetter.transform.TransformPoint(pose.localPosition) + Vector3.up * aboveHand;
            Vector3 away = position - head;
            if (away.sqrMagnitude < 1e-6f)
                return;
            menu.transform.SetPositionAndRotation(position, Quaternion.LookRotation(away, Vector3.up));
        }

        // The grenade left the hands, a menu opened or the raid ended: closed
        public static void Reset()
        {
            Close();
            triggerDown = false;
        }
    }
}
