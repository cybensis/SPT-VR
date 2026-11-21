using HarmonyLib;
using TarkovVR.Source.Settings;

namespace TarkovVR.Patches.Core.Player
{
    // Firing while sprinting. Tarkov refuses a trigger pull from the idle gun while the player's state is in
    // EFTHardSettings.UnsuitableStates (FirearmController.Idling.SetTriggerPressed -> Player.StateIsSuitableForHandInput), and
    // starting a sprint releases the trigger (FirearmController.SprintStateChangedEvent). For your player only, Sprint counts as a
    // suitable state during the pull (every other unsuitable state still refuses it), and an automatic burst keeps going when a
    // sprint starts. The gun's sprint pose is already off in VR (IKPatches sets the hands animator's sprint layer weight to 0).
    // Only active with the run animation off (VR tab "Disable Run Animation", VRSettings.GetDisableRunAnim). With it on, the arms
    // play the run and firing stays blocked as in Tarkov.
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

        // Tarkov released the trigger as the sprint started; press it again if a burst was under way. Only for automatic fire:
        // re-pressing during a semi-auto shot would queue a second shot.
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
