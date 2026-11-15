using EFT.InventoryLogic;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // A part the hands work hitting its stop jolts the gun (GunJolt): its momentum, the part's mass by how fast it got there
    public static partial class VRReloadController
    {
        // The parts' masses. Not from the gun's template weight: on a modular gun that's its lower alone (a Spear's 0.4 kg, a Glock's
        // 0.23). A self-loading action is built for its cartridge, so its bolt carrier / slide goes by the default round's momentum
        // (an M4's carrier 0.3 kg, an AKM's 0.45, a 9 mm slide 0.17); Tarkov's shot weighs nothing real, so shotguns get a fixed one.
        public static float boltMassPerMomentum = 0.08f;    // kg per N s of the default round's momentum (bullet mass x muzzle speed)
        public static float minBoltMass = 0.12f;
        public static float maxBoltMass = 0.7f;
        public static float shotgunBoltMass = 0.4f;         // kg a self-loading shotgun's carrier
        public static float boltActionBoltMass = 0.3f;      // kg a bolt-action's bolt
        public static float pumpMass = 0.25f;               // kg a pump gun's forend and action bars
        public static float boltSlamSpeed = 4f;             // m/s the spring takes a bolt or slide home at from all the way back
        public static float chamberAheadOfGrip = 0.06f;     // m along the bore from the grip: where a bolt hits home
        public static float pushSpeedSmoothing = 0.03f;     // s the push speed of a magazine / bolt / pump is smoothed over
        // s: a jolt takes the hardest push in this window before the stop. A magazine seats only once it has eased onto the track, by
        // when a fast push has already stopped.
        public static float pushPeakWindow = 0.25f;
        // s: the charging handle's let-go takes how far back it was in this window before (the trigger's let-go registers late, the
        // handle following the hand forward meanwhile)
        public static float handleLetGoWindow = 0.15f;
        public static float heldOnCatchJolt = 0.35f;        // of the slam when an empty magazine's follower stops the bolt on the catch
        public static float partPullSoundMin = 0.15f;       // of a full pull: a handle let go from this far back or more springs home (sound, jolt)

        // The charging handle let go: how far back it still was, 1 = all the way (the carrier follows the handle: ridden forward = soft)
        private static float handleSlamFraction = 1f;
        private static float handlePullPeak;
        private static float handlePullPeakTime = -10f;

        // Every frame the handle is held (UpdateChargingHandle): the furthest back it's been in handleLetGoWindow
        private static void TrackHandlePull(float pull)
        {
            if (pull >= handlePullPeak || Time.time - handlePullPeakTime > handleLetGoWindow)
            {
                handlePullPeak = pull;
                handlePullPeakTime = Time.time;
            }
        }

        private static float HandlePullAtLetGo(float pull) => Time.time - handlePullPeakTime <= handleLetGoWindow ? Mathf.Max(pull, handlePullPeak) : pull;

        // A self-loading gun's bolt carrier or slide
        private static float BoltMass(FirearmController firearmController)
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

        // A bolt or slide the spring slams home (the charging handle let go, the catch hit): fraction = how far back it ran from
        private static void BoltSlamJolt(FirearmController firearmController, float fraction, string what)
        {
            if (fraction <= 0f || !GunJolt.ChamberPoint(firearmController, chamberAheadOfGrip, out Vector3 chamber, out Vector3 bore))
                return;
            float mass = BoltMass(firearmController);
            GunJolt.Hit(firearmController, chamber, bore * (mass * boltSlamSpeed * Mathf.Clamp01(fraction)), $"{what} ({mass:0.00} kg bolt, from {Mathf.Clamp01(fraction) * 100f:0}% back)");
        }

        // A bolt-action's bolt pushed home by the hand
        private static void BoltPushJolt(FirearmController firearmController, float speed)
        {
            if (speed <= 0f || !GunJolt.ChamberPoint(firearmController, chamberAheadOfGrip, out Vector3 chamber, out Vector3 bore))
                return;
            GunJolt.Hit(firearmController, chamber, bore * (boltActionBoltMass * speed), "bolt pushed home");
        }

        // A magazine seated: its momentum up the well, at the well
        private static void MagazineSeatJolt(FirearmController firearmController, Magazine magazine, Transform well, Vector3 insertDirection, float speed)
        {
            if (magazine == null || well == null || speed <= 0f || insertDirection.sqrMagnitude < 1e-6f)
                return;
            float mass = 0.4f;
            try { mass = magazine.TotalWeight; }
            catch { }
            GunJolt.Hit(firearmController, well.position, insertDirection.normalized * (Mathf.Clamp(mass, 0.05f, 5f) * speed), "magazine seated");
        }

        // A pump closed by the hand on it: the forend's momentum forward, at the hand
        private static void PumpJolt(FirearmController firearmController, float speed)
        {
            if (speed <= 0f || !GunJolt.ChamberPoint(firearmController, chamberAheadOfGrip, out Vector3 chamber, out Vector3 bore))
                return;
            Transform hand = HandRig(gunHand: false);
            GunJolt.Hit(firearmController, hand != null ? hand.position : chamber, bore * (pumpMass * speed), "pump closed");
        }

        // How fast a part is pushed home: its distance from home (m) each frame -> m/s toward it
        private sealed class PushSpeed
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

        private static readonly PushSpeed magazinePush = new PushSpeed();
        private static readonly PushSpeed boltPush = new PushSpeed();
        private static readonly PushSpeed pumpPush = new PushSpeed();
    }
}
