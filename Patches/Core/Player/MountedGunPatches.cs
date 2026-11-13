using EFT;
using EFT.Interactive;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal class MountedGunPatches
    {
        // Letting go of a mounted gun you took with empty hands: back to empty hands, not Tarkov's draw of your active weapon. FIKA's
        // override calls this base and still sends the Leave (its observers do nothing on it; the hands change syncs itself).
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EFT.Player), nameof(EFT.Player.OperateStationaryWeapon))]
        private static bool LeaveToEmptyHands(EFT.Player __instance, StationaryWeapon stationaryWeapon, StationaryWeaponPacket.EStationaryCommand command)
        {
            if (command != StationaryWeaponPacket.EStationaryCommand.Leave || !MountedGun.LeavesToEmptyHands(__instance))
                return true;
            __instance.SetEmptyHands(result => { });
            return false;
        }

        // On the gun: the body faces the aim and stands under your head, no step clips (MountedGun.MoveBody)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(StationaryPlayerState), nameof(StationaryPlayerState.ManualAnimatorMoveUpdate))]
        private static bool BodyUnderTheHead(StationaryPlayerState __instance, float deltaTime)
        {
            return !MountedGun.MoveBody(__instance, deltaTime);
        }
    }
}
