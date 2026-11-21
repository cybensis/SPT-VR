using HarmonyLib;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.Player
{
    // Firing while sprinting. Tarkov refuses a trigger pull from the idle gun while the player's state is in
    // EFTHardSettings.UnsuitableStates (FirearmController.Idling.SetTriggerPressed -> Player.StateIsSuitableForHandInput), and a
    // sprint starting releases the trigger (FirearmController.SprintStateChangedEvent). Your player only: the pull goes through with
    // Sprint read as a fine state (every other unsuitable state still refuses it), and an automatic burst carries on when a sprint
    // starts. The gun's sprint pose is already off in VR (IKPatches: the hands animator's sprint layer at weight 0).
    // Only with the sprint animation off (VR tab "Disable Run Animation", VRSettings.GetDisableRunAnim): with it on the arms play the
    // run, and firing stays blocked as in Tarkov.
    [HarmonyPatch]
    public static class SprintFirePatches
    {
        private static bool pullingNow;

        private static bool Allowed => VRSettings.GetDisableRunAnim();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.Idling), "SetTriggerPressed")]
        private static void PullThroughSprint(EFT.Player.FirearmController.Idling __instance, bool pressed)
        {
            EFT.Player player = __instance?.Controller?._player;
            pullingNow = Allowed && pressed && player != null && player.IsYourPlayer && player.IsSprintEnabled;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.Idling), "SetTriggerPressed")]
        private static void PullDone()
        {
            pullingNow = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.StateIsSuitableForHandInput), MethodType.Getter)]
        private static void SprintIsSuitable(EFT.Player __instance, ref bool __result)
        {
            if (pullingNow && !__result && __instance.IsYourPlayer && __instance.CurrentStateName == EPlayerState.Sprint)
                __result = true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "SprintStateChangedEvent")]
        private static void TriggerBeforeSprint(EFT.Player.FirearmController __instance, out bool __state)
        {
            __state = __instance != null && __instance.IsTriggerPressed;
        }

        // Tarkov let go of the trigger as the sprint started: held again for a burst under way (only automatic fire - a semi-auto shot
        // in progress would queue a second one off the press)
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController), "SprintStateChangedEvent")]
        private static void KeepBurstIntoSprint(EFT.Player.FirearmController __instance, bool value, bool __state)
        {
            if (!Allowed || !value || !__state || __instance?._player == null || !__instance._player.IsYourPlayer)
                return;
            if (!(__instance.CurrentOperation is EFT.Player.FirearmController.AutomaticFireOperation) || __instance.IsTriggerPressed)
                return;
            __instance.SetTriggerPressed(true);
        }
    }
}
