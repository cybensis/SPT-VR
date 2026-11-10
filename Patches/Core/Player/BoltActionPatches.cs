using Comfort.Common;
using HarmonyLib;
using TarkovVR.Source.Weapons;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class BoltActionPatches
    {
        // A bolt-action's shot is out (the FiringBullet event): with the bolt by hand (VRReloadController.Bolt), Tarkov's bolt cycle is
        // skipped by fast-forwarding the fire operation to idle (the shot is already fired, so it only exits and plays the idle state),
        // without the cycle's ShellEject / AddAmmoInChamber: the hand does those. Not on a malfunction (OnFireEvent switched to it already).
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player.FirearmController.FireOperation), nameof(EFT.Player.FirearmController.FireOperation.OnFireEvent))]
        private static void BoltStaysShut(EFT.Player.FirearmController.FireOperation __instance)
        {
            // A pump shotgun's shot, the pump by hand (VRReloadController.Shotgun): its pump is skipped too. Its fire operation is the plain
            // one; FIKA already has the shot (MakeShot ran in this event), the other players' copies pump on their own.
            if (!(__instance is EFT.Player.FirearmController.BoltActionFireOperation))
            {
                EFT.Player.FirearmController shotgunController = __instance.Controller;
                if (shotgunController != null && shotgunController.CurrentOperation == __instance && VRReloadController.PumpShotFired(shotgunController))
                    shotgunController.FastForwardCurrentState();
                return;
            }
            EFT.Player.FirearmController controller = __instance.Controller;
            if (controller == null || controller.CurrentOperation != __instance)
                return;
            if (VRReloadController.BoltShotFired(controller))
            {
                // Co-op: FIKA only sends the bolt cycle (the next round into the chamber on the other players' copies) from this
                // operation once the trigger is let go, and this skip ends it first, so their magazine would drift from yours. Sent
                // here instead; the sync module's BoltCycleSync does their move without the animation.
                if (ModSupport.InstalledMods.FIKAInstalled)
                {
                    try { ModSupport.FIKA.VRArmSync.SendBoltCycle(controller); }
                    catch (System.Exception exception) { Plugin.MyLog.LogWarning($"[VRReload] bolt cycle not sent to the other players: {exception.Message}"); }
                }
                controller.FastForwardCurrentState();
            }
        }

        // A single round into an open bolt-action by hand (VRReloadController.Rounds): its inventory move goes to the gun's hands
        // controller after it's applied (AbstractAsyncOperation.InProcess: a fail rolls it back). Idling.Execute would turn a round into
        // the chamber into its own LoadCartridgeToChamber (animation + bolt closes) and refuses one into the internal magazine, which
        // Tarkov only loads through LoadMagOperation ("Can't perform operation RemoveSuboperation ... ->StackSlot cartridges").
        // Succeeding keeps the move as it is, no animation. Our own player only; other peers' copies are the FIKA module's BoltRoundSync.
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
