using UnityEngine;

namespace TarkovVR.ModSupport
{
    // Co-op as a FIKA client. The host plays each of your moves on its copy of you at full length, including the draws and
    // put-aways we skip locally, and answers when it is done. A hands or inventory change sent before that answer is refused
    // ("Default Inventory is currently being modified"), and a refused hands change leaves the hands broken, so our quick moves
    // check here first. In single player or as the host this never waits.
    // No FIKA types in this class: FikaHostWait keeps them behind NoInlining calls.
    internal static class CoopHost
    {
        public static float waitLimit = 5f;     // s a quick move waits on the host's answer before it goes anyway
        // s the host's copy of you is still throwing after a grenade leaves your hand. The host doesn't answer a throw, and it
        // plays the draw, pin pull and throw that we skip at full length.
        public static float throwSettle = 2.5f;

        private static float busyUntil = -1f;

        internal static bool IsClient => InstalledMods.FIKAInstalled && FIKA.FikaHostWait.IsClient();

        // True while the host hasn't answered your last move yet, or is still playing a move it doesn't answer
        internal static bool StillAnswering => IsClient && (Time.time < busyUntil || FIKA.FikaHostWait.WaitingForHost(VRGlobals.player));

        // How long the host's copy of you takes to throw a grenade: the pin pull starts when we sent it (after the host answered
        // taking the grenade), then the throw follows, each at full clip length (*_idle_to_out 1.5 s, *_fire1 0.9 s). Network delay can be
        // ignored, since our next move crosses it too. A quick throw keeps the host busy well past throwSettle.
        public static float hostPinPull = 1.5f;
        public static float hostThrow = 0.9f;
        public static float hostThrowMargin = 1f;   // s extra for the host's frames and the end of its throw; an estimate, not measured

        internal static void GrenadeThrown(float pinSentAt)
        {
            float settle = throwSettle;
            if (pinSentAt >= 0f)
                settle = Mathf.Max(settle, Mathf.Max(pinSentAt + hostPinPull, Time.time) + hostThrow + hostThrowMargin - Time.time);
            HostBusyFor(settle);
        }

        // Marks the host busy for a move it plays for this long without sending an answer
        internal static void HostBusyFor(float seconds)
        {
            if (IsClient)
                busyUntil = Mathf.Max(busyUntil, Time.time + seconds);
        }

        // True if a move requested at 'since' (Time.time) should still wait
        internal static bool HoldsBack(float since)
        {
            return StillAnswering && Time.time - since < waitLimit;
        }

        // Set when the host refuses one of your hands changes (FikaRefusals hooks FikaPlayer.HandleCallbackResponse with an error)
        private static float lastRefusalAt = -1f;
        internal static string LastRefusal { get; private set; }

        internal static void NoteRefused(string error)
        {
            lastRefusalAt = Time.time;
            LastRefusal = error;
        }

        // True if a refusal came in at or after 'since' (Time.time)
        internal static bool RefusedSince(float since)
        {
            return IsClient && lastRefusalAt >= 0f && lastRefusalAt >= since;
        }
    }
}
