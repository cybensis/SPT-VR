using UnityEngine;

namespace TarkovVR.ModSupport
{
    // Co-op as a FIKA client: the host runs each of your moves on its copy of you at full length (the draws and put-aways we skip
    // here) and answers when it's done. A hands or inventory change sent before that answer is refused ("Default Inventory is
    // currently being modified"), and a refused hands change leaves the hands broken. Our own quick moves ask here first.
    // Single player and hosting: never waits. No FIKA types here (FikaHostWait keeps them behind NoInlining calls).
    internal static class CoopHost
    {
        public static float waitLimit = 5f;     // s a quick move waits on the host's answer before it goes anyway
        // s after a grenade leaves the hand the host's copy of you is still throwing it: a throw isn't an operation it answers, and it
        // plays the draw, the pin and the throw we skip at full length, behind us
        public static float throwSettle = 2.5f;

        private static float busyUntil = -1f;

        internal static bool IsClient => InstalledMods.FIKAInstalled && FIKA.FikaHostWait.IsClient();

        // The host hasn't answered your last move yet, or is still playing one it doesn't answer
        internal static bool StillAnswering => IsClient && (Time.time < busyUntil || FIKA.FikaHostWait.WaitingForHost(VRGlobals.player));

        // A move the host plays out for this long without an answer to wait on
        internal static void HostBusyFor(float seconds)
        {
            if (IsClient)
                busyUntil = Mathf.Max(busyUntil, Time.time + seconds);
        }

        // A move asked for at 'since' (Time.time) should still hold off
        internal static bool HoldsBack(float since)
        {
            return StillAnswering && Time.time - since < waitLimit;
        }
    }
}
