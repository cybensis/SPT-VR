using Diz.LanguageExtensions;
using EFT;
using EFT.Interactive;
using UnityEngine;

namespace TarkovVR.Source.Player.Interactions
{
    // A hard hit with the gun breaks a shut door open. This runs Tarkov's own breach (the kick-open swing, the broken lock, bots hearing
    // it, FIKA's packet) without the kick. A door held shut by its latch breaks when hit in the direction it opens. A locked door only
    // breaks where Tarkov's own kick would break it (a breach door, hit from the side it swings away from); a door that takes a key only
    // thuds, as it does for the kick.
    internal static partial class PhysicalDoors
    {
        public static bool gunBreach = true;
        public static float breachMinSpeed = 3.5f;      // m/s, how fast the hand must drive the gun into the door
        public static float breachMinFacing = 0.3f;     // cos of the angle between the hit and the door's opening direction
        public static float lockedHitMinSpeed = 2.5f;   // m/s, the slowest hit at which a locked door thuds
        public static float lockedHitRetrigger = 0.4f;  // s

        public static bool logGunHits = true;           // one line per hard gun hit on a door that didn't breach, with the reason
        private static float lastLockedHitTime = -10f;
        private static float lastHitLogTime = -10f;

        // Called from WeaponCollision.OnProxyContact when the gun's physics body hits a door. approach is how fast (m/s) the hand drives
        // that point of the gun into the door; normal points out of the door.
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

        // A locked door breaks the way Tarkov's kick breaks it (MovementContext.StartDoorBreak): Door.BreachSuccessRoll from the kick spot
        // on the player's side. That passes for a breach door (CanBeBreached) kicked from the side it swings away from. A door with a key
        // isn't a breach door, so the kick only thuds. Returns why the door holds, or null if it breaks.
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
            // The gun just pushes a door that is free to swing, and a hand on a door holds it
            if (MayBePushed(door))
                return "free to swing: the gun pushes it";
            if (runs.TryGetValue(door, out DoorRun run) && (run.driver != null || run.turner != null))
                return "a hand holds it";
            // The door interaction would stand the player up from prone
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

        // How squarely the hit (into the surface, along -normal) pushes that point of the door the way it opens (1 = straight).
        // Returns false for a hit at the hinge.
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

        // Runs the Execute stage of Tarkov's breach (FIKA sends it), without the hands' clip. interactionInProgress isn't set, so the
        // kick-open swing plays. Returns false if Tarkov didn't take it.
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
            // Bots hear it and it costs a kick's stamina. Breaking a locked door gives Tarkov's door experience, as BreachDoorState.Exit does.
            player.MovementContext.OnBreach();
            if (startState == EDoorState.Locked)
                player.MovementContext.LockedDoorOpened(breached: true);
            if (logGrabs)
                Plugin.MyLog.LogInfo($"[Doors] the gun breached '{door.name}' ({door.Id}, was {startState}) at {approach:0.0} m/s");
            return true;
        }

        // A locked door holds: plays Tarkov's failed-kick thud and dust. Not Door.FailBreach: it restores a state from an interaction
        // that never ran.
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
