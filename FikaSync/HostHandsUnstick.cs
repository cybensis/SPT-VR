using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using Fika.Core.Main.Components;
using Fika.Core.Main.ObservedClasses;
using Fika.Core.Main.Players;
using Fika.Core.Networking;
using Fika.Core.Networking.Packets.FirearmController;
using Fika.Core.Networking.Packets.Player.Common;
using HarmonyLib;

namespace SptVrFikaSync
{
    // Host only. FIKA's host checks a client's hands change against its copy of that player's inventory
    // (FikaServer.OnProceedRequestPacketReceived -> Item.CheckAction -> ObservedInventoryController.CheckItemAction). Any open event on the
    // item refuses it with "Cannot apply X because Default Inventory is currently being modified".
    // Tarkov opens a hands event for the item in the hands at the start (Player.TrySetInHands, FIKA's HandleInProcess) and closes it by the
    // item in the hands at the end (ItemController.ProcessActivity matches by item). If the hands changed in between, it stays open all raid.
    // So before the check, open hands events for an item the host's copy isn't holding are closed. A change still refused logs the open events.
    [HarmonyPatch]
    internal static class HostHandsUnstick
    {
        public static bool enabled = true;

        private static readonly List<ItemEventArgs> stale = new List<ItemEventArgs>();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FikaServer), "OnProceedRequestPacketReceived")]
        private static void BeforeHandsCheck(ProceedRequestPacket packet)
        {
            if (!enabled || packet.ProceedType == EProceedType.EmptyHands)
                return;
            try
            {
                if (!CoopHandler.TryGetCoopHandler(out CoopHandler coop) || coop.Players == null
                    || !coop.Players.TryGetValue(packet.NetId, out FikaPlayer player) || !(player is ObservedPlayer))
                    return;
                if (!(player.InventoryController is ObservedInventoryController inventory))
                    return;
                Item inHands = player.HandsController?.Item;
                stale.Clear();
                foreach (ItemEventArgs open in inventory.ActiveEvents)
                {
                    if (open is IItemInHandsEventArgs && open.Item != inHands)
                        stale.Add(open);
                }
                foreach (ItemEventArgs open in stale)
                {
                    inventory.RemoveActiveEvent(open);
                    FikaSyncPlugin.Log.LogWarning($"[HostHands] {player.Profile?.Nickname}: closed a stuck {open.GetType().Name} for {Describe(open.Item)} ({open.Status}),"
                        + $" the hands hold {Describe(inHands)}");
                }
                stale.Clear();
                LogIfStillRefused(player, inventory, packet, inHands);
            }
            catch (System.Exception exception)
            {
                FikaSyncPlugin.Log.LogError($"[HostHands] checking open hands events failed: {exception}");
            }
        }

        private static void LogIfStillRefused(FikaPlayer player, ObservedInventoryController inventory, ProceedRequestPacket packet, Item inHands)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
                return;
            var search = world.FindItemWithWorldData(packet.ItemId);
            if (!search.Succeeded || search.Value.item == null || search.Value.item.CurrentAddress == null)
                return;
            Item item = search.Value.item;
            var check = item.CheckAction(null);
            if (!check.Failed)
                return;
            var line = new StringBuilder($"[HostHands] {player.Profile?.Nickname}'s {packet.ProceedType} {Describe(item)} refused: {check.Error}; the hands hold {Describe(inHands)}; open events:");
            if (inventory.ActiveEvents.Count == 0)
                line.Append(" none");
            foreach (ItemEventArgs open in inventory.ActiveEvents)
                line.Append($" | {open.GetType().Name} {Describe(open.Item)} ({open.Status}){(open.Location != null ? $" at {open.Location.Container?.ParentItem?.ShortName}" : "")}");
            FikaSyncPlugin.Log.LogWarning(line.ToString());
        }

        private static string Describe(Item item) => item == null ? "nothing" : $"{item.ShortName} ({item.Id})";
    }
}
