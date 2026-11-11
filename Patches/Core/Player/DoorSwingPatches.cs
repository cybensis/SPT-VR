using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EFT.Interactive;
using HarmonyLib;
using TarkovVR.Source.Player.Interactions;

namespace TarkovVR.Patches.Core.Player
{
    // Doors by hand (PhysicalDoors): the Open / Close it runs settle the door's state at once, without Tarkov's swing (the hand moves
    // the door). Every override of the swing (Door's, KeycardDoor's) is patched: each one is what a call on that door runs.
    [HarmonyPatch]
    internal static class DoorSwingPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in AccessTools.GetTypesFromAssembly(typeof(WorldInteractiveObject).Assembly))
            {
                if (!typeof(WorldInteractiveObject).IsAssignableFrom(type))
                    continue;
                MethodInfo method = AccessTools.DeclaredMethod(type, nameof(WorldInteractiveObject.SmoothDoorOpenCoroutine));
                if (method != null && !method.IsAbstract)
                    yield return method;
            }
        }

        private static bool Prefix(WorldInteractiveObject __instance, EDoorState state, ref IEnumerator __result)
        {
            if (!PhysicalDoors.SkipsSwing(__instance))
                return true;
            PhysicalDoors.SettleState(__instance, state);
            __result = Settled();
            return false;
        }

        private static IEnumerator Settled()
        {
            yield break;
        }
    }
}
