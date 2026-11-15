using EFT;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;
using UnityEngine;

namespace TarkovVR.Patches.Core.Player
{
    [HarmonyPatch]
    internal static class DoorBodyPushPatches
    {
        // This frame's step, before the character controller's collision stops the body at a door (after MovementPatches' analog
        // redirect, so it's the heading actually walked)
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Low)]
        [HarmonyPatch(typeof(MovementState), "ApplyMotion")]
        private static void BodyPushesDoors(MovementState __instance, Vector3 motion)
        {
            if (__instance.MovementContext == null || __instance.MovementContext._player == null || !__instance.MovementContext._player.IsYourPlayer || !VRGlobals.inGame)
                return;
            PhysicalDoors.BodyPush(motion, Time.deltaTime);
        }
    }
}
