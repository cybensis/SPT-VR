using System.Runtime.CompilerServices;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace TarkovVR.ModSupport.FIKA
{
    // As a FIKA client, an inventory move doesn't run when it's made: ClientInventoryController sends it to the host, the host replies
    // "started" and runs it on its copy of you, and only then does it run in this game (ClientInventoryOperationHandler). The host's
    // "done" comes when its copy's hands operation ends; until then FikaPlayer.WaitingForCallback is set (it also blocks the trigger and
    // Tarkov's reload). Hosting, a move runs at once. Callers are InstalledMods.FIKAInstalled-gated; NoInlining keeps FIKA's types out of them.
    internal static class FikaHostWait
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsClient()
        {
            return FikaBackendUtils.IsClient;
        }

        // A move of this player's still has no answer from the host
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool WaitingForHost(EFT.Player player)
        {
            return player is FikaPlayer fikaPlayer && fikaPlayer.WaitingForCallback;
        }
    }
}
