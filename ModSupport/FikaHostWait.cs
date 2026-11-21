using System.Runtime.CompilerServices;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;

namespace TarkovVR.ModSupport.FIKA
{
    // As a FIKA client, an inventory move doesn't run when it's made. ClientInventoryController sends it to the host, the host replies
    // "started" and runs it on its copy of you, and only then does it run in this game (ClientInventoryOperationHandler). The host sends
    // "done" when its copy's hands operation ends; until then FikaPlayer.WaitingForCallback is set, which also blocks the trigger and
    // Tarkov's reload. On the host a move runs at once.
    // Callers check InstalledMods.FIKAInstalled first; NoInlining keeps FIKA's types out of their method bodies.
    internal static class FikaHostWait
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsClient()
        {
            return FikaBackendUtils.IsClient;
        }

        // True while a move by this player still has no answer from the host.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool WaitingForHost(EFT.Player player)
        {
            return player is FikaPlayer fikaPlayer && fikaPlayer.WaitingForCallback;
        }
    }
}
