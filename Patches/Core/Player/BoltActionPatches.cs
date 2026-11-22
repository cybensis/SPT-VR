using Comfort.Common;
using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class BoltActionPatches
    {
        // Runs after a bolt-action's shot (the FiringBullet event). While the bolt is worked by hand (VRReloadController.Bolt), Tarkov's
        // bolt cycle is skipped by fast-forwarding the fire operation to idle. The shot is already fired, so this only exits the operation
        // and plays the idle state, without the cycle's ShellEject / AddAmmoInChamber (the hand does those). Not on a malfunction:
        // OnFireEvent has already switched to it.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.FireOperation), nameof(EFT.Player.FirearmController.FireOperation.OnFireEvent))]
        private static void BoltStaysShut(EFT.Player.FirearmController.FireOperation __instance)
        {
            // A pump shotgun's shot with the pump worked by hand (VRReloadController.Shotgun): its pump cycle is skipped too. It uses the
            // plain fire operation. FIKA already has the shot (MakeShot ran in this event), and the other players' copies pump on their own.
            if (!(__instance is EFT.Player.FirearmController.BoltActionFireOperation))
            {
                EFT.Player.FirearmController shotgunController = __instance.Controller;
                if (shotgunController != null && shotgunController.CurrentOperation == __instance && VRReloadController.Shotgun.PumpShotFired(shotgunController))
                    shotgunController.FastForwardCurrentState();
                return;
            }
            EFT.Player.FirearmController controller = __instance.Controller;
            if (controller == null || controller.CurrentOperation != __instance)
                return;
            if (VRReloadController.Bolt.BoltShotFired(controller))
            {
                // Co-op: FIKA sends the bolt cycle (the next round into the chamber on the other players' copies) from this operation
                // only once the trigger is let go, and this skip ends the operation first, so their magazine would drift from yours.
                // It is sent here instead; the sync module's BoltCycleSync does their move without the animation.
                if (ModSupport.InstalledMods.FIKAInstalled)
                {
                    try { ModSupport.FIKA.VRArmSync.SendBoltCycle(controller); }
                    catch (System.Exception exception) { Plugin.MyLog.LogWarning($"[VRReload] bolt cycle not sent to the other players: {exception.Message}"); }
                }
                controller.FastForwardCurrentState();
            }
        }

        // A single round loaded by hand into an open bolt-action (VRReloadController.Rounds). Its inventory move reaches the gun's hands
        // controller after it is applied (AbstractAsyncOperation.InProcess: a failure rolls it back). Idling.Execute would turn a round
        // into the chamber into its own LoadCartridgeToChamber (animation, bolt closes), and it refuses a round into the internal magazine,
        // which Tarkov only loads through LoadMagOperation ("Can't perform operation RemoveSuboperation ... ->StackSlot cartridges").
        // Succeeding here keeps the move as it is, with no animation. Our own player only; other peers' copies are handled by the FIKA
        // module's BoltRoundSync.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.Idling), nameof(EFT.Player.FirearmController.Idling.Execute),
            new[] { typeof(EFT.InventoryLogic.Operations.IInventoryOperation), typeof(Callback) })]
        private static bool LetSingleRoundIn(EFT.Player.FirearmController.Idling __instance, EFT.InventoryLogic.Operations.IInventoryOperation operation, Comfort.Common.Callback callback)
        {
            if (!VRReloadController.Rounds.LetsSingleRoundIn(__instance.Controller, operation))
                return true;
            callback?.Succeed();
            return false;
        }
    }
}
