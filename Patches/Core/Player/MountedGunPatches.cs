using EFT;
using EFT.Interactive;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class MountedGunPatches
    {
        // Letting go of a mounted gun that was taken with empty hands goes back to empty hands, instead of Tarkov drawing the
        // active weapon. FIKA's override calls this base method and still sends the Leave. Its observers do nothing with it;
        // the hands change syncs on its own.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.OperateStationaryWeapon))]
        private static bool LeaveToEmptyHands(EFT.Player __instance, StationaryWeapon stationaryWeapon, StationaryWeaponPacket.EStationaryCommand command)
        {
            if (command != StationaryWeaponPacket.EStationaryCommand.Leave || !MountedGun.LeavesToEmptyHands(__instance))
                return true;
            __instance.SetEmptyHands(result => { });
            return false;
        }

        // While on the gun, MountedGun.MoveBody turns the body to the aim and keeps it under the head, without step animations
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationaryPlayerState), nameof(StationaryPlayerState.ManualAnimatorMoveUpdate))]
        private static bool BodyUnderTheHead(StationaryPlayerState __instance, float deltaTime)
        {
            return !MountedGun.MoveBody(__instance, deltaTime);
        }
    }
}
