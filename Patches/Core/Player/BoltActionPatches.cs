using Comfort.Common;
using Diz.LanguageExtensions;
using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class BoltActionPatches
    {
        // A bolt-action's shot is out (the FiringBullet event): with the bolt by hand (VRReloadController.Bolt), Tarkov's own bolt cycle
        // after it doesn't play. Its fire operation is skipped to idle right here (FireOperation.FastForward: the shot is already
        // fired, so it only exits and plays the idle state), without the cycle's ShellEject / AddAmmoInChamber: the hand does those.
        // Not on a malfunction (OnFireEvent switched to it already).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.FireOperation), nameof(EFT.Player.FirearmController.FireOperation.OnFireEvent))]
        private static void BoltStaysShut(EFT.Player.FirearmController.FireOperation __instance)
        {
            if (!(__instance is EFT.Player.FirearmController.BoltActionFireOperation))
                return;
            EFT.Player.FirearmController controller = __instance.Controller;
            if (controller == null || controller.CurrentOperation != __instance)
                return;
            if (VRReloadController.BoltShotFired(controller))
                controller.FastForwardCurrentState();
        }

        // A single round into an open bolt-action by hand (VRReloadController.Rounds): its inventory move is put to the gun's hands
        // controller like any move touching the gun in hands, after it's applied (AbstractAsyncOperation.InProcess: a fail rolls it
        // back). Idling.Execute takes a round into the chamber as its own LoadCartridgeToChamber (headset: the first round "takes over
        // and does an animation ... and auto closes the bolt") and refuses one into the internal magazine, which Tarkov only loads
        // through its reload (LoadMagOperation): "Can't perform operation RemoveSuboperation ... ->StackSlot cartridges", every round
        // after the first. Succeeding keeps the move as it is, no animation: the hand did the loading.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.Idling), nameof(EFT.Player.FirearmController.Idling.Execute),
            new[] { typeof(EFT.InventoryLogic.Operations.IInventoryOperation), typeof(Callback) })]
        private static bool LetSingleRoundIn(EFT.Player.FirearmController.Idling __instance, EFT.InventoryLogic.Operations.IInventoryOperation operation, Comfort.Common.Callback callback)
        {
            if (!VRReloadController.LetsSingleRoundIn(__instance.Controller, operation))
                return true;
            callback?.Succeed();
            return false;
        }
    }
}
