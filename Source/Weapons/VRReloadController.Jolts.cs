using EFT.InventoryLogic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Weapons
{
    // A part the hands work jolts the gun (GunJolt) when it hits its stop, with its momentum: the part's mass times its speed
    public static partial class VRReloadController
    {
        [Tunable("Reload & handles", TunableKind.Tune, "Of the handle's slam jolt when an empty magazine stops the bolt on the catch instead.", Suggested = true)]
        public static float heldOnCatchJolt = 0.35f;        // of the slam when an empty magazine's follower stops the bolt on the catch
        [Tunable("Reload & handles", TunableKind.Tune, "Of a full pull: a handle let go from this far back or more springs home with a sound and a small jolt.", Suggested = true)]
        public static float partPullSoundMin = 0.15f;       // of a full pull: a handle let go from this far back or more springs home (sound, jolt)

        // How far back the charging handle still was when let go, 1 = all the way. The carrier follows the handle, so one ridden
        // forward hits softly.
        private static float handleSlamFraction = 1f;

        // Called every frame the handle is held (UpdateChargingHandle): tracks the furthest back it has been within handleLetGoWindow
        private static void TrackHandlePull(float pull)
        {
            if (pull >= Jolts.handlePullPeak || Time.time - Jolts.handlePullPeakTime > Jolts.handleLetGoWindow)
            {
                Jolts.handlePullPeak = pull;
                Jolts.handlePullPeakTime = Time.time;
            }
        }

        private static float HandlePullAtLetGo(float pull) => Time.time - Jolts.handlePullPeakTime <= Jolts.handleLetGoWindow ? Mathf.Max(pull, Jolts.handlePullPeak) : pull;

        // A bolt or slide slammed home by the spring (the charging handle let go, the bolt catch hit). fraction = how far back it started.
        private static void BoltSlamJolt(FirearmController firearmController, float fraction, string what)
        {
            if (fraction <= 0f || !GunJolt.ChamberPoint(firearmController, Jolts.chamberAheadOfGrip, out Vector3 chamber, out Vector3 bore))
                return;
            float mass = Jolts.BoltMass(firearmController);
            GunJolt.Hit(firearmController, chamber, bore * (mass * Jolts.boltSlamSpeed * Mathf.Clamp01(fraction)), $"{what} ({mass:0.00} kg bolt, from {Mathf.Clamp01(fraction) * 100f:0}% back)");
        }

        // A bolt-action's bolt pushed home by the hand
        private static void BoltPushJolt(FirearmController firearmController, float speed)
        {
            if (speed <= 0f || !GunJolt.ChamberPoint(firearmController, Jolts.chamberAheadOfGrip, out Vector3 chamber, out Vector3 bore))
                return;
            GunJolt.Hit(firearmController, chamber, bore * (Jolts.boltActionBoltMass * speed), "bolt pushed home");
        }

        // A pump closed by the hand on it: the forend's momentum forward along the bore, applied at the hand
        private static void PumpJolt(FirearmController firearmController, float speed)
        {
            if (speed <= 0f || !GunJolt.ChamberPoint(firearmController, Jolts.chamberAheadOfGrip, out Vector3 chamber, out Vector3 bore))
                return;
            Transform hand = HandRig(gunHand: false);
            GunJolt.Hit(firearmController, hand != null ? hand.position : chamber, bore * (Jolts.pumpMass * speed), "pump closed");
        }
        private static readonly Jolts.PushSpeed boltPush = new Jolts.PushSpeed();
        private static readonly Jolts.PushSpeed pumpPush = new Jolts.PushSpeed();

        public static class Jolts
        {
            // Part masses. Don't use the gun's template weight: on a modular gun that is the lower receiver alone (a Spear's 0.4 kg, a
            // Glock's 0.23). A self-loading action is built for its cartridge, so the bolt carrier or slide mass scales with the default
            // round's momentum (an M4's carrier 0.3 kg, an AKM's 0.45, a 9 mm slide 0.17). Tarkov's shotgun pellets weigh almost nothing,
            // so shotguns get a fixed mass.
            [Tunable("Reload & handles", TunableKind.Tune, "A self-loading gun's bolt carrier or slide, by its default round's momentum (bullet mass x muzzle speed): an "
                + "M4 0.3 kg, an AKM 0.45, a Spear 0.65, a 9 mm slide 0.17.", Unit = "kg per N s", Suggested = true)]
            public static float boltMassPerMomentum = 0.08f;    // kg per N s of the default round's momentum (bullet mass x muzzle speed)
            [Tunable("Reload & handles", TunableKind.Tune, "The lightest bolt or slide.", Unit = "kg", Suggested = true)]
            public static float minBoltMass = 0.12f;
            [Tunable("Reload & handles", TunableKind.Tune, "The heaviest.", Unit = "kg", Suggested = true)]
            public static float maxBoltMass = 0.7f;
            [Tunable("Reload & handles", TunableKind.Tune, "A self-loading shotgun's carrier (Tarkov's shot has no real weight).", Unit = "kg", Suggested = true)]
            public static float shotgunBoltMass = 0.4f;         // kg, a self-loading shotgun's carrier
            [Tunable("Reload & handles", TunableKind.Tune, "A bolt-action's bolt, pushed home by the hand.", Unit = "kg", Suggested = true)]
            public static float boltActionBoltMass = 0.3f;      // kg, a bolt-action's bolt
            [Tunable("Reload & handles", TunableKind.Tune, "A pump gun's forend and action bars.", Unit = "kg", Suggested = true)]
            public static float pumpMass = 0.25f;               // kg, a pump gun's forend and action bars
            [Tunable("Reload & handles", TunableKind.Tune, "How fast the spring takes a bolt or slide home from all the way back.", Unit = "m/s", Suggested = true)]
            public static float boltSlamSpeed = 4f;             // m/s, how fast the spring drives a bolt or slide home from all the way back
            public static float chamberAheadOfGrip = 0.06f;     // m along the bore ahead of the grip, where a bolt hits home
            public static float pushSpeedSmoothing = 0.03f;     // s, smoothing time of a magazine / bolt / pump push speed
            // s: a jolt uses the hardest push within this window before the stop. A magazine only seats after it has eased onto its track,
            // and by then a fast push has already stopped.
            [Tunable("Reload & handles", TunableKind.Tune, "A magazine, bolt-action bolt or pump jolts as hard as your hardest push in this long before it hit home.", Unit = "s", Suggested = true)]
            public static float pushPeakWindow = 0.25f;
            // s: a let-go charging handle uses how far back it was within this window before. The trigger release registers late, and
            // the handle follows the hand forward in the meantime.
            [Tunable("Reload & handles", TunableKind.Tune, "The charging handle's jolt counts how far back it was this long before you let go (the trigger's let-go "
                + "registers a little late).", Unit = "s", Suggested = true)]
            public static float handleLetGoWindow = 0.15f;
            internal static float handlePullPeak;
            internal static float handlePullPeakTime = -10f;

            // A self-loading gun's bolt carrier or slide
            internal static float BoltMass(FirearmController firearmController)
            {
                try
                {
                    Weapon weapon = firearmController.Item;
                    string caliber = weapon.AmmoCaliber ?? "";
                    if (caliber.Contains("12g") || caliber.Contains("20g") || caliber.Contains("23x75"))
                        return shotgunBoltMass;
                    AmmoTemplate round = weapon.Template.DefAmmoTemplate;
                    if (round != null && round.BulletMassGram > 0f && round.InitialSpeed > 0f)
                        return Mathf.Clamp(boltMassPerMomentum * round.BulletMassGram * 0.001f * round.InitialSpeed, minBoltMass, Mathf.Max(maxBoltMass, minBoltMass));
                }
                catch { }
                return 0.3f;
            }

            // A magazine seated: its momentum up the well, applied at the well
            internal static void MagazineSeatJolt(FirearmController firearmController, Magazine magazine, Transform well, Vector3 insertDirection, float speed)
            {
                if (magazine == null || well == null || speed <= 0f || insertDirection.sqrMagnitude < 1e-6f)
                    return;
                float mass = 0.4f;
                try { mass = magazine.TotalWeight; }
                catch { }
                GunJolt.Hit(firearmController, well.position, insertDirection.normalized * (Mathf.Clamp(mass, 0.05f, 5f) * speed), "magazine seated");
            }

            // How fast a part is pushed home: its distance from home (m), tracked each frame, gives the speed toward home (m/s)
            internal sealed class PushSpeed
            {
                private float speed;
                private float lastDistance;
                private int lastFrame = -10;
                private float peak;
                private float peakTime = -10f;

                // The hardest push in pushPeakWindow (0 when it wasn't tracked this frame or the last)
                public float Peak
                {
                    get
                    {
                        if (Time.frameCount - lastFrame > 1)
                            return 0f;
                        return Time.time - peakTime <= pushPeakWindow ? Mathf.Max(peak, speed) : speed;
                    }
                }

                public void Track(float distanceFromHome)
                {
                    if (lastFrame == Time.frameCount)
                        return;
                    float deltaTime = Time.deltaTime;
                    bool going = lastFrame == Time.frameCount - 1 && deltaTime > 1e-4f;
                    float now = going ? Mathf.Max(0f, (lastDistance - distanceFromHome) / deltaTime) : 0f;
                    speed = going ? Mathf.Lerp(speed, now, 1f - Mathf.Exp(-deltaTime / Mathf.Max(pushSpeedSmoothing, 1e-3f))) : 0f;
                    if (speed >= peak || Time.time - peakTime > pushPeakWindow)
                    {
                        peak = speed;
                        peakTime = Time.time;
                    }
                    lastDistance = distanceFromHome;
                    lastFrame = Time.frameCount;
                }
            }

            internal static readonly PushSpeed magazinePush = new PushSpeed();
        }
    }
}
