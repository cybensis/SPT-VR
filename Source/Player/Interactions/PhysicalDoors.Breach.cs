using Diz.LanguageExtensions;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // A hard hit with the gun breaks a shut door open: Tarkov's own breach (its kick-open swing, the broken lock, bots hearing it, FIKA's
    // packet) without the kick. A door its latch holds shut, hit the way it opens; a locked door only where Tarkov's own kick breaks it (a
    // breach door, from the side it swings away from) - one with a key only thuds, as the kick does.
    internal static partial class PhysicalDoors
    {
        public static bool gunBreach = true;
        public static float breachMinSpeed = 3.5f;      // m/s the hand drives the gun into the door at
        public static float breachMinFacing = 0.3f;     // how squarely the hit drives it the way it opens (cos of the angle off its swing)
        public static float lockedHitMinSpeed = 2.5f;   // m/s a locked door thuds at
        public static float lockedHitRetrigger = 0.4f;  // s

        public static bool logGunHits = true;           // a line per hard gun hit on a door that didn't breach: why
        private static float lastLockedHitTime = -10f;
        private static float lastHitLogTime = -10f;

        // WeaponCollision.OnProxyContact: the gun's physics body hit a door, the hand bringing that point of the gun into it at approach m/s
        // (normal: out of the door)
        internal static void GunHit(Door door, Vector3 point, Vector3 normal, float approach)
        {
            if (!Active || !gunBreach || door == null || door is KeycardDoor || !Supports(door))
                return;
            EFT.Player player = VRGlobals.player;
            if (player == null || player.MovementContext == null)
                return;
            if (door.DoorState == EDoorState.Locked)
            {
                if (approach < lockedHitMinSpeed || Time.time - lastLockedHitTime <= lockedHitRetrigger)
                    return;
                lastLockedHitTime = Time.time;
                string kickRefused = WhyLockedHolds(door, approach, player);
                if (kickRefused == null && Breach(door, approach))
                    return;
                LockedHit(door, point, approach);
                LogGunHit(door, approach, kickRefused ?? "Tarkov didn't take the breach");
                return;
            }
            string refused = WhyNoBreach(door, point, normal, approach, player);
            if (refused != null)
            {
                if (approach >= lockedHitMinSpeed)
                    LogGunHit(door, approach, refused);
                return;
            }
            Breach(door, approach);
        }

        // A locked door breaks as Tarkov's kick breaks it (MovementContext.StartDoorBreak): Door.BreachSuccessRoll from the kick spot on your
        // side = a breach door (CanBeBreached), kicked from the side it swings away from. A door with a key isn't one: the kick only thuds.
        private static string WhyLockedHolds(Door door, float approach, EFT.Player player)
        {
            if (approach < breachMinSpeed)
                return $"locked, too soft for a kick (needs {breachMinSpeed:0.0} m/s)";
            if (!door.CanInteractWithBreach)
                return "locked, Tarkov offers no kick on it";
            if (!door.BreachSuccessRoll(door.GetBreakInPosition(player.MovementContext.TransformPosition)))
                return door.CanBeBreached && door.Operatable ? "locked, kicked from the side it doesn't break open from"
                    : "locked, Tarkov's kick doesn't open it (it takes its key)";
            if (runs.TryGetValue(door, out DoorRun run) && (run.driver != null || run.turner != null))
                return "a hand holds it";
            if (player.MovementContext.IsInPronePose)
                return "prone";
            return null;
        }

        private static string WhyNoBreach(Door door, Vector3 point, Vector3 normal, float approach, EFT.Player player)
        {
            if (door.DoorState != EDoorState.Shut)
                return $"not shut ({door.DoorState})";
            if (!door.CanBeBreached || !door.CanInteractWithBreach)
                return "Tarkov doesn't let this door be breached";
            // A door free to swing the gun just pushes; a hand on it holds it
            if (MayBePushed(door))
                return "free to swing: the gun pushes it";
            if (runs.TryGetValue(door, out DoorRun run) && (run.driver != null || run.turner != null))
                return "a hand holds it";
            // (its door interaction stands the player up from prone)
            if (player.MovementContext.IsInPronePose)
                return "prone";
            if (!OpeningFacing(door, point, normal, out float facing))
                return "hit at the hinge";
            if (facing < breachMinFacing)
                return facing < 0f ? $"hit from the side it opens toward (it breaks open away from you: hit it from the other side)"
                    : $"hit glancing (facing {facing:0.00}, needs {breachMinFacing:0.00})";
            if (approach < breachMinSpeed)
                return $"too soft (needs {breachMinSpeed:0.0} m/s)";
            return null;
        }

        // How squarely the hit (into the surface: -normal) drives the door's point there the way it opens (1 = straight); false = at the hinge
        private static bool OpeningFacing(Door door, Vector3 point, Vector3 normal, out float facing)
        {
            facing = 0f;
            Vector3 axis = HingeAxis(door);
            Vector3 lever = Vector3.ProjectOnPlane(point - door.transform.position, axis);
            if (lever.magnitude < minHingeRadius)
                return false;
            Vector3 opening = Vector3.Cross(axis, lever).normalized * Mathf.Sign(Span(door));
            facing = Vector3.Dot(-normal, opening);
            return true;
        }

        private static void LogGunHit(Door door, float approach, string why)
        {
            if (!logGunHits || Time.time - lastHitLogTime < 0.5f)
                return;
            lastHitLogTime = Time.time;
            Plugin.MyLog.LogInfo($"[Doors] gun hit '{door.name}' ({door.Id}, {door.DoorState}) at {approach:0.0} m/s, no breach: {why}");
        }

        // The Execute stage (FIKA sends it) of Tarkov's breach, without the hands' clip. Not interactionInProgress: its kick-open swing plays.
        // false = Tarkov didn't take it.
        private static bool Breach(Door door, float approach)
        {
            EFT.Player player = VRGlobals.player;
            EDoorState startState = door.DoorState;
            player.MovementContext.ResetCanUsePropState();
            Option<InteractionResult> option = Door.Interact(player, EInteractionType.Breach);
            if (!option.Succeeded)
            {
                if (refusalsLogged < 10)
                {
                    refusalsLogged++;
                    Plugin.MyLog.LogWarning($"[Doors] can't breach '{door.name}' now: {option.Error}");
                }
                return false;
            }
            bool withoutAnimation = door.interactWithoutAnimation;
            door.interactWithoutAnimation = true;
            try
            {
                player.ExecuteInteraction(door, option.Value);
            }
            finally
            {
                door.interactWithoutAnimation = withoutAnimation;
            }
            if (door.DoorState == startState)
            {
                if (refusalsLogged < 10)
                {
                    refusalsLogged++;
                    Plugin.MyLog.LogWarning($"[Doors] Tarkov didn't take the breach of '{door.name}': door {door.DoorState}");
                }
                return false;
            }
            // Bots hear it, and it costs a kick's stamina; a locked door broken = Tarkov's door experience (BreachDoorState.Exit)
            player.MovementContext.OnBreach();
            if (startState == EDoorState.Locked)
                player.MovementContext.LockedDoorOpened(breached: true);
            if (logGrabs)
                Plugin.MyLog.LogInfo($"[Doors] the gun breached '{door.name}' ({door.Id}, was {startState}) at {approach:0.0} m/s");
            return true;
        }

        // A locked door holds: Tarkov's failed-kick thud and dust (not FailBreach: it puts back a state from an interaction that never ran)
        private static void LockedHit(Door door, Vector3 point, float approach)
        {
            if (door.HitEffect != null)
                door.HitEffect.Play();
            if (door.HitClip != null)
            {
                float volume = Mathf.Lerp(0.35f, 1f, Mathf.InverseLerp(lockedHitMinSpeed, breachMinSpeed * 1.5f, approach));
                door.PlaySoundAtPoint(point, door.HitClip, BetterAudio.AudioSourceGroupType.InteractiveObjects, door._hitSoundRolloff, volume);
            }
        }
    }
}
