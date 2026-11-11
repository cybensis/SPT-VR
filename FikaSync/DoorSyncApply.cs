using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace SptVrFikaSync
{
    // A door another VR player's hand moves: its angle and handle as they send them. FIKA's own Open / Close for it still arrive
    // (the state); their swing is skipped while the door is driven from here (DoorSwingSkip), and a swing already running stops,
    // so the door sits where the hand has it, not fully open.
    internal static class DoorSyncApply
    {
        public static float drivenHoldTime = 1f;    // s after the last packet that FIKA's own swing of that door is still skipped

        // By door Id (a string doesn't keep the raid's doors alive past it)
        private static readonly Dictionary<string, float> drivenUntil = new Dictionary<string, float>();

        public static void ResetState() => drivenUntil.Clear();

        public static void Apply(VRDoorPacket packet)
        {
            if (!FikaVrSync.enableDoorSync || string.IsNullOrEmpty(packet.DoorId))
                return;
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
                return;
            WorldInteractiveObject door = gameWorld.FindDoor(packet.DoorId);
            if (!(door is Door) || !door.isActiveAndEnabled)
                return;
            if (FikaVrSync.isDoorDrivenLocally != null && FikaVrSync.isDoorDrivenLocally(door))
                return;
            drivenUntil[packet.DoorId] = Time.time + drivenHoldTime;

            StopSwing(door);
            if (door.DoorState != EDoorState.Locked)
                door.CurrentAngle = packet.Angle;
            DoorHandle handle = door._handle;
            if (handle != null && IsRotation(handle.DefaultRotation) && IsRotation(handle.OpenRotation))
            {
                float turn = Mathf.Clamp01(packet.HandleTurn);
                handle.transform.localRotation = Quaternion.Slerp(handle.DefaultRotation, handle.OpenRotation, turn);
                if (handle.pos)
                    handle.transform.localPosition = Vector3.Lerp(handle.DefaultPosition, handle.OpenPosition, turn);
            }
        }

        internal static bool Driven(WorldInteractiveObject door)
        {
            return door != null && !string.IsNullOrEmpty(door.Id) && drivenUntil.TryGetValue(door.Id, out float until) && Time.time < until;
        }

        // FIKA's Open / Close arrived first and its swing runs: it ends here, in the state it was heading for
        private static void StopSwing(WorldInteractiveObject door)
        {
            WorldInteractiveObject.InteractionState interaction = door._interaction;
            if (door.DoorState != EDoorState.Interacting || !interaction.IsInProgress)
                return;
            interaction.Break = true;
            EDoorState result = interaction.ResultState == EDoorState.Breaching ? EDoorState.Open : interaction.ResultState;
            door.DoorState = result == EDoorState.Open || result == EDoorState.Shut ? result : door.FallbackState;
            door._previousInteraction.SetEndData(interaction);
        }

        // The state the swing would leave the door in, at once
        internal static void Settle(WorldInteractiveObject door, EDoorState state)
        {
            EDoorState result = state == EDoorState.Breaching ? EDoorState.Open : state;
            WorldInteractiveObject.InteractionState interaction = door._interaction;
            interaction.ProgressTime = interaction.Duration;
            door.DoorState = result;
            door._previousInteraction.SetEndData(interaction);
            if (result == EDoorState.Open)
            {
                Vector2 volume = door.OpenSoundVolumeRange;
                door.PlaySound(EDoorState.Open, UnityEngine.Random.Range(volume.x, volume.y));
            }
            if (result == EDoorState.Shut && door is KeycardDoor keycardDoor && keycardDoor._lockOnShut)
                keycardDoor.Lock();
        }

        private static bool IsRotation(Quaternion rotation)
        {
            float length = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            return length > 0.5f && !float.IsNaN(length);
        }
    }

    // FIKA's Open / Close of a door a VR player's hand drives (DoorSyncApply): the state at once, without Tarkov's swing. Every override
    // of the swing is patched (each one is what a call on that door runs).
    [HarmonyPatch]
    internal static class DoorSwingSkip
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
            if (!DoorSyncApply.Driven(__instance))
                return true;
            DoorSyncApply.Settle(__instance, state);
            __result = Settled();
            return false;
        }

        private static IEnumerator Settled()
        {
            yield break;
        }
    }
}
