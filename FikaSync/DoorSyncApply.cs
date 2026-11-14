using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Players;
using HarmonyLib;
using UnityEngine;

namespace SptVrFikaSync
{
    // A door another VR player's hand moves: its angle and handle as they send them, eased toward the newest packet every frame (as the
    // arms, ArmSyncApply.smoothRate). FIKA's own Open / Close for it still arrive (the state); their swing is skipped while the door is
    // driven from here (DoorSwingSkip), a swing already running stops, and the shut sound plays when the door drawn here reaches its
    // frame. A kick (Tarkov's breach swing) is never skipped or stopped.
    internal static class DoorSyncApply
    {
        public static float drivenHoldTime = 1f;        // s after the last packet that FIKA's own swing of that door is still skipped
        public static float smoothRate = 18f;           // the angle and handle eased toward the newest packet, per s (0 = snap to each)
        public static float shutReachedDegrees = 0.5f;  // the door drawn here this near its frame = shut: the shut sound plays
        public static float shutSoundMaxWait = 0.3f;    // s at most the shut sound waits for that
        public static float slamSpeed = 180f;           // deg/s the door shuts at full volume (as PhysicalDoors.slamSpeed)
        public static float quietSpeed = 30f;           // deg/s and slower: Tarkov's own shut volume

        private sealed class Remote
        {
            public WorldInteractiveObject door;
            public float drivenUntil;
            public float targetAngle;
            public float targetTurn = -1f;              // < 0 = no handle to drive
            public float shownTurn;
            public float lastPacketAngle = float.NaN;
            public float lastPacketTime;
            public float speed;                         // deg/s between the last two packets that moved it
            public bool shutPending;
            public float shutPendingSince;
        }

        // By door Id (a string doesn't keep the raid's doors alive past it)
        private static readonly Dictionary<string, Remote> remotes = new Dictionary<string, Remote>();
        private static readonly List<string> finished = new List<string>();

        public static void ResetState() => remotes.Clear();

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
            // A kick swings it: that plays out
            if (Kicking(door))
                return;
            if (!remotes.TryGetValue(packet.DoorId, out Remote remote) || remote.door != door)
            {
                remote = new Remote { door = door };
                remotes[packet.DoorId] = remote;
            }
            float now = Time.time;
            // (the same angle again = no news: through a headless host every packet comes twice)
            if (float.IsNaN(remote.lastPacketAngle) || Mathf.Abs(Mathf.DeltaAngle(remote.lastPacketAngle, packet.Angle)) > 0.01f)
            {
                float sinceLast = now - remote.lastPacketTime;
                if (!float.IsNaN(remote.lastPacketAngle) && sinceLast > 0.005f && sinceLast < 0.5f)
                    remote.speed = Mathf.Abs(Mathf.DeltaAngle(remote.lastPacketAngle, packet.Angle)) / sinceLast;
                else
                    remote.speed = 0f;
                remote.lastPacketAngle = packet.Angle;
                remote.lastPacketTime = now;
            }
            remote.drivenUntil = now + drivenHoldTime;
            remote.targetAngle = packet.Angle;
            DoorHandle handle = door._handle;
            remote.targetTurn = handle != null && IsRotation(handle.DefaultRotation) && IsRotation(handle.OpenRotation) ? Mathf.Clamp01(packet.HandleTurn) : -1f;

            StopSwing(door);
            if (smoothRate <= 0f)
                Show(remote, 1f);
        }

        // Per frame (FikaSyncPlugin.LateUpdate)
        public static void Tick()
        {
            if (remotes.Count == 0)
                return;
            float now = Time.time;
            float follow = smoothRate > 0f ? 1f - Mathf.Exp(-smoothRate * Time.deltaTime) : 1f;
            finished.Clear();
            foreach (KeyValuePair<string, Remote> entry in remotes)
            {
                Remote remote = entry.Value;
                WorldInteractiveObject door = remote.door;
                if (door == null || Kicking(door) || (FikaVrSync.isDoorDrivenLocally != null && FikaVrSync.isDoorDrivenLocally(door)))
                {
                    finished.Add(entry.Key);
                    continue;
                }
                bool there = Show(remote, follow);
                if (remote.shutPending && (Mathf.Abs(Mathf.DeltaAngle(door.CurrentAngle, door.CloseAngle)) < shutReachedDegrees
                    || now - remote.shutPendingSince > shutSoundMaxWait))
                {
                    remote.shutPending = false;
                    PlayShut(door, remote.speed);
                }
                if (there && !remote.shutPending && now >= remote.drivenUntil)
                    finished.Add(entry.Key);
            }
            for (int i = 0; i < finished.Count; i++)
                remotes.Remove(finished[i]);
        }

        // The door drawn a step toward the newest packet; true = it's there
        private static bool Show(Remote remote, float follow)
        {
            WorldInteractiveObject door = remote.door;
            bool there = true;
            if (door.DoorState != EDoorState.Locked)
            {
                float gap = Mathf.DeltaAngle(door.CurrentAngle, remote.targetAngle);
                if (Mathf.Abs(gap) > 0.02f)
                {
                    door.CurrentAngle += gap * follow;
                    there = false;
                }
                else if (gap != 0f)
                    door.CurrentAngle = remote.targetAngle;
            }
            DoorHandle handle = door._handle;
            if (remote.targetTurn >= 0f && handle != null)
            {
                float turnGap = remote.targetTurn - remote.shownTurn;
                if (Mathf.Abs(turnGap) > 0.002f)
                {
                    remote.shownTurn += turnGap * follow;
                    there = false;
                }
                else
                    remote.shownTurn = remote.targetTurn;
                handle.transform.localRotation = Quaternion.Slerp(handle.DefaultRotation, handle.OpenRotation, remote.shownTurn);
                if (handle.pos)
                    handle.transform.localPosition = Vector3.Lerp(handle.DefaultPosition, handle.OpenPosition, remote.shownTurn);
            }
            return there;
        }

        internal static bool Driven(WorldInteractiveObject door)
        {
            return door != null && !string.IsNullOrEmpty(door.Id) && remotes.TryGetValue(door.Id, out Remote remote) && remote.door == door
                && Time.time < remote.drivenUntil;
        }

        // The hand's angles let go of it (a kick swings it now)
        internal static void Release(WorldInteractiveObject door)
        {
            if (door != null && !string.IsNullOrEmpty(door.Id))
                remotes.Remove(door.Id);
        }

        // Tarkov's breach swing runs (Door.KickOpen)
        internal static bool Kicking(WorldInteractiveObject door)
        {
            WorldInteractiveObject.InteractionState interaction = door._interaction;
            return door is Door && interaction.ResultState == EDoorState.Breaching && interaction.IsInProgress && !interaction.Break;
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
            // The shut sound when the door drawn here gets to its frame (Tick): the angles come in behind FIKA's Close
            if (result == EDoorState.Shut && !string.IsNullOrEmpty(door.Id) && remotes.TryGetValue(door.Id, out Remote remote) && remote.door == door)
            {
                remote.shutPending = true;
                remote.shutPendingSince = Time.time;
            }
            if (result == EDoorState.Shut && door is KeycardDoor keycardDoor && keycardDoor._lockOnShut)
                keycardDoor.Lock();
        }

        // Louder the faster it came shut (PhysicalDoors.PlayShut on the VR player's side)
        private static void PlayShut(WorldInteractiveObject door, float speed)
        {
            AudioClip[] clips = door.ShutSound;
            if (clips == null || clips.Length == 0)
                return;
            AudioClip clip = clips[UnityEngine.Random.Range(0, clips.Length)];
            if (clip == null)
                return;
            Vector2 range = door.ShutSoundVolumeRange;
            float volume = Mathf.Lerp(UnityEngine.Random.Range(range.x, range.y), 0.8f, Mathf.InverseLerp(quietSpeed, slamSpeed, speed));
            door.PlaySoundAtPoint(door.GetSoundPos(), clip, BetterAudio.AudioSourceGroupType.InteractiveObjects, door._shutSoundRolloff, volume);
        }

        private static bool IsRotation(Quaternion rotation)
        {
            float length = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            return length > 0.5f && !float.IsNaN(length);
        }
    }

    // FIKA's Open / Close of a door a VR player's hand drives (DoorSyncApply): the state at once, without Tarkov's swing. Every override
    // of the swing is patched (each one is what a call on that door runs). A kick's swing always plays.
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
            // (Door.KickOpen sets the breach result before it starts the swing)
            if (__instance._interaction.ResultState == EDoorState.Breaching)
            {
                DoorSyncApply.Release(__instance);
                return true;
            }
            DoorSyncApply.Settle(__instance, state);
            __result = Settled();
            return false;
        }

        private static IEnumerator Settled()
        {
            yield break;
        }
    }

    // A VR player's breach on the other players' machines. A gun hit breaks the door with Tarkov's Execute alone (no Start, no kick clip),
    // which FIKA replays through that player's copy's movement state - and a copy left in a door or kick state drops it
    // (ObservedDoorInteractionState / ObservedBreachDoorState do nothing). When the replay didn't kick the door, it's kicked here. Not
    // while that copy's own kick clip is still to land (a VR player's kick from the gaze menu: Tarkov kicks at its KickTime).
    [HarmonyPatch(typeof(ObservedPlayer), nameof(ObservedPlayer.ExecuteInteraction))]
    internal static class BreachReplay
    {
        public static bool kickWhenDropped = true;
        public static bool logBreaches = true;          // one line per breach: kicked by FIKA's replay, or here (and the copy's state)

        private static bool loggedError;

        private static void Postfix(ObservedPlayer __instance, WorldInteractiveObject __0, InteractionResult __1)
        {
            try
            {
                if (!(__0 is Door door) || __1 == null || __1.InteractionType != EInteractionType.Breach || !HeadSyncApply.IsVrPlayer(__instance.NetId))
                    return;
                MovementState state = __instance.CurrentManagedState;
                bool kicked = DoorSyncApply.Kicking(door);
                bool ownKickToCome = state is BreachDoorState breachState && !breachState._hit;
                bool kickHere = !kicked && !ownKickToCome && kickWhenDropped;
                if (kickHere)
                {
                    DoorSyncApply.Release(door);
                    door.SetUser(__instance);
                    door.LockForInteraction();
                    door.KickOpen(__instance.MovementContext != null ? __instance.MovementContext.TransformPosition : __instance.Position, false);
                }
                if (logBreaches)
                {
                    string how = kicked ? "kicked by FIKA's replay" : ownKickToCome ? "their kick clip kicks it" : kickHere ? "FIKA's replay dropped it - kicked here" : "FIKA's replay dropped it";
                    FikaSyncPlugin.Log?.LogInfo($"[DoorSync] {__instance.Profile?.Nickname} breached '{door.name}' ({door.Id}): {how} (their state {state?.GetType().Name}, door {door.DoorState})");
                }
            }
            catch (Exception e)
            {
                if (!loggedError)
                {
                    loggedError = true;
                    FikaSyncPlugin.Log?.LogError($"[DoorSync] breach replay: {e}");
                }
            }
        }
    }
}
