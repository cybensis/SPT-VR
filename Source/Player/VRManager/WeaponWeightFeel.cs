using UnityEngine;

namespace TarkovVR.Source.Player.VRManager
{
    // "Fake physics" weapon weight (2026-07): the shipped first-order Slerp smoothing FELT right where a
    // real PhysX simulation did not - two headset rounds of true rigid-body weight on the collision proxy
    // read as hand wobble / turn jitter / "the gun is moving away from my hand", because in this
    // architecture the gun is marker-anchored and the rendered hand rides it, so ANY simulated pose
    // divergence looks like the hand detaching. So weight stays a FILTER on rawRightHand's rotation
    // (the marker anchor keeps the gun in the hand by construction) - upgraded to behave like inertia:
    //  - swing/twist split about the barrel: rolling the gun (twist) is light and crisp, swinging the
    //    muzzle (swing) carries all the weight - the "physics lives toward the barrel" feel.
    //  - the swing is a second-order spring-damper: the velocity state IS momentum, so heavy guns glide
    //    into place and follow through on stops instead of just slowing down (a first-order Slerp can't).
    //  - one-handed, a constant droop torque sags the muzzle a few degrees (heavy guns are hard to hold
    //    level); two-handing stiffens the spring and the support hand carries the droop.
    //  - swing lag is HARD-CAPPED (maxLagDegrees): the gun resists but can never fall far behind the
    //    hand - the guarantee the real-physics version couldn't give.
    public class WeaponWeightFeel
    {
        public static float springBase = 350f;        // spring stiffness before ErgonomicWeight divides it; higher = lighter feel overall
        public static float dampingRatio = 0.95f;     // 1 = settles with no overshoot; lower = more follow-through on stops
        public static float supportStiffness = 2.5f;  // two-handed spring multiplier (the second hand's leverage)
        public static float twistRate = 25f;          // 1/s; barrel-roll catch-up. High because rolling a gun about its bore is easy
        public static float droopPerErgo = 0.9f;      // deg of one-handed muzzle sag per ErgonomicWeight unit ("hard to hold level")
        public static float maxDroop = 8f;            // deg; sag cap
        public static float maxLagDegrees = 20f;      // hard cap on total rotation lag behind the hand
        public static float maxSwingVel = 30f;        // rad/s safety rail on the momentum state

        // Jumping (his ask: "make jumping feel more real ... some inertia/impact feel"). The droop below is 1 g holding the muzzle down;
        // the body's own vertical acceleration reaches the gun the same way. A push-off or a landing accelerates the body up, the
        // muzzle lags = dips (an impulse: the change of the body's vertical speed over g, times the gun's weight torque - integrated
        // exactly, no impact time to guess); falling freely the gun weighs nothing, the droop lifts (two-handed the hands' hold, no
        // longer met by gravity, tips it up a little). Two-handed or braced the same torque meets a stiffer spring: it moves less.
        // Rotation only, through this spring and under its lag cap: the gun never leaves the hand. Follows the Weapon Weight setting.
        public static bool jumpInertia = true;
        public static float jumpInertiaScale = 1f;    // 1 = as much as the weight that droops the muzzle; 2 = twice the dip
        public static bool airborneFloat = true;      // in the air the droop lifts (one-handed: to level)
        public static float minAirTime = 0.12f;       // s off the ground before a fall without a jump counts (a stair edge: no)
        public static float takeoffWindow = 0.15f;    // s after leaving the ground the push-off's speed is still read (it builds over frames)
        public static float maxBodySpeedChange = 8f;  // m/s cap on one dip (a teleport, a respawn)
        public static bool jumpDebugLog = false;      // a line per push-off / landing: the speed change and the dip it makes
        private const float GRAVITY = 9.81f;

        // One instance per filtered target so the momentum states can't cross-pollute or double-filter:
        // Main = the rawRightHand path (one- and two-handed holds); SupportHold = ChestHang's
        // left-hand-only hold (only ever active when Main's output ISN'T driving the gun).
        public static readonly WeaponWeightFeel Main = new WeaponWeightFeel();
        public static readonly WeaponWeightFeel SupportHold = new WeaponWeightFeel();

        private Vector3 swingVel;                     // rad/s, world - the momentum state
        private float lastStepTime = -10f;
        private int lastJumpDipFrame = -1;            // this filter took this frame's dip already

        // The body's vertical motion, read once a frame (whichever filter steps first)
        private static int bodyFrame = -1;
        private static bool hasLastBody;
        private static float lastBodyY;
        private static float lastBodyTime;
        private static bool wasAirborne;
        private static bool airborneIsJump;           // left the ground in Tarkov's jump state (a fall off a ledge: no push-off)
        private static bool countedInAir;
        private static float airborneSince;
        private static float airVerticalSpeed;        // m/s on the last frame off the ground
        private static float takeoffSpeedTaken;       // m/s of the push-off already given to the gun
        private static float frameSpeedChange;        // m/s: this frame's upward change of the body's speed (push-off, landing)
        private static bool frameWeightless;

        // Filters `current` (last frame's filtered rotation) toward `target` for this frame. barrelDir =
        // the gun's current muzzle direction (world). All rotation deltas are world-frame pre-multiplies.
        public Quaternion Step(Quaternion current, Quaternion target, Vector3 barrelDir,
                               float ergoWeight, bool twoHanded, float dt)
        {
            if (Time.time - lastStepTime > 0.25f)
                swingVel = Vector3.zero;              // weapon was away (holster/eat/menu) - drop stale momentum
            lastStepTime = Time.time;
            dt = Mathf.Clamp(dt, 1e-4f, 0.033f);

            Quaternion err = target * Quaternion.Inverse(current);
            if (err.w < 0f)                           // shortest arc
                err = new Quaternion(-err.x, -err.y, -err.z, -err.w);

            // decompose err = swing * twist, twist about the barrel axis
            Vector3 p = Vector3.Project(new Vector3(err.x, err.y, err.z), barrelDir);
            Quaternion twist = new Quaternion(p.x, p.y, p.z, err.w);
            float tm = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            twist = tm > 1e-6f ? new Quaternion(twist.x / tm, twist.y / tm, twist.z / tm, twist.w / tm)
                               : Quaternion.identity;
            Quaternion swing = err * Quaternion.Inverse(twist);

            // twist: light first-order catch-up
            current = Quaternion.Slerp(Quaternion.identity, twist, Mathf.Min(1f, twistRate * dt)) * current;

            // swing: spring-damper, stiffness from the gun's ergo weight
            swing.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f)
                ang -= 360f;
            Vector3 e = Mathf.Abs(ang) > 1e-4f && !float.IsInfinity(axis.x)
                ? axis.normalized * (ang * Mathf.Deg2Rad) : Vector3.zero;
            float k = springBase / Mathf.Max(ergoWeight, 1f) * (twoHanded ? supportStiffness : 1f);
            k *= ShoulderStock.SpringMult;   // a seated stock braces the gun (tighter spring)
            float c = 2f * dampingRatio * Mathf.Sqrt(k);
            Vector3 accel = k * e - c * swingVel;

            // Jumping: the body's vertical acceleration on the gun, against its weight torque (1 g's worth: what holds the one-handed droop)
            UpdateBody();
            if (jumpInertia && (frameSpeedChange > 0f || frameWeightless))
            {
                Vector3 dipAxis = Vector3.Cross(barrelDir, Vector3.down);
                if (dipAxis.sqrMagnitude > 1e-6f)
                {
                    dipAxis.Normalize();
                    float weightTorque = springBase / Mathf.Max(ergoWeight, 1f) * Mathf.Min(droopPerErgo * ergoWeight, maxDroop) * Mathf.Deg2Rad
                        * jumpInertiaScale;
                    if (frameWeightless)
                        accel -= dipAxis * weightTorque;
                    if (frameSpeedChange > 0f && lastJumpDipFrame != Time.frameCount)
                    {
                        lastJumpDipFrame = Time.frameCount;
                        swingVel += dipAxis * (weightTorque * frameSpeedChange / GRAVITY);
                    }
                }
            }

            // droop torque sized so the one-handed equilibrium sag is exactly droopPerErgo*ergo degrees
            // (spring balance: tau = k * sag). Axis = the rotation that tips the muzzle toward down.
            if (!twoHanded)
            {
                float sag = Mathf.Min(droopPerErgo * ergoWeight, maxDroop) * Mathf.Deg2Rad;
                Vector3 droopAxis = Vector3.Cross(barrelDir, Vector3.down);
                if (droopAxis.sqrMagnitude > 1e-6f)
                    accel += droopAxis.normalized * (k * sag);
            }

            swingVel += accel * dt;                   // semi-implicit Euler - stable at these stiffnesses
            float sv = swingVel.magnitude;
            if (sv > maxSwingVel)
                swingVel *= maxSwingVel / sv;
            float stepDeg = swingVel.magnitude * dt * Mathf.Rad2Deg;
            if (stepDeg > 1e-4f)
                current = Quaternion.AngleAxis(stepDeg, swingVel.normalized) * current;

            // hard lag cap: resist, never detach
            float resAng = Quaternion.Angle(current, target);
            if (resAng > maxLagDegrees)
                current = Quaternion.Slerp(target, current, maxLagDegrees / resAng);

            return current;
        }

        // Once a frame: the body's vertical speed from its position, and from it this frame's push-off / landing and whether it's falling
        // freely. Off the ground counts once it's a jump (Tarkov's jump state as it lifts) or has lasted minAirTime.
        private static void UpdateBody()
        {
            if (bodyFrame == Time.frameCount)
                return;
            bodyFrame = Time.frameCount;
            frameSpeedChange = 0f;
            frameWeightless = false;

            EFT.Player player = VRGlobals.player;
            if (!jumpInertia || player == null || player.MovementContext == null)
            {
                hasLastBody = false;
                return;
            }
            float now = Time.time;
            float y = player.Position.y;
            bool airborne = !player.MovementContext.IsGrounded;
            float elapsed = now - lastBodyTime;
            bool fresh = hasLastBody && elapsed > 1e-4f && elapsed < 0.1f;
            float verticalSpeed = fresh ? (y - lastBodyY) / elapsed : 0f;
            lastBodyY = y;
            lastBodyTime = now;
            hasLastBody = true;
            if (!fresh)
            {
                // A gap (no gun for a while, a load): start over from here
                wasAirborne = airborne;
                countedInAir = false;
                airborneSince = now;
                return;
            }

            if (airborne && !wasAirborne)
            {
                airborneSince = now;
                takeoffSpeedTaken = 0f;
                airborneIsJump = player.MovementContext.CurrentState is EFT.JumpPlayerState;
                countedInAir = false;
            }
            if (airborne)
            {
                // The push-off: the upward speed it builds over its first frames, given to the gun as it comes
                if (airborneIsJump && now - airborneSince <= takeoffWindow && verticalSpeed > takeoffSpeedTaken)
                {
                    frameSpeedChange += verticalSpeed - takeoffSpeedTaken;
                    takeoffSpeedTaken = verticalSpeed;
                }
                countedInAir = airborneIsJump || now - airborneSince >= minAirTime;
                frameWeightless = countedInAir && airborneFloat;
                airVerticalSpeed = verticalSpeed;
            }
            else if (wasAirborne && countedInAir)
            {
                // The landing: the downward speed the ground stops
                frameSpeedChange += Mathf.Max(0f, -airVerticalSpeed);
                countedInAir = false;
                if (jumpDebugLog)
                    Plugin.MyLog.LogInfo($"[WeaponWeight] landing: {-airVerticalSpeed:F2} m/s stopped after {now - airborneSince:F2} s in the air ({(airborneIsJump ? "jump" : "fall")})");
            }
            wasAirborne = airborne;
            frameSpeedChange = Mathf.Min(frameSpeedChange, maxBodySpeedChange);
            if (jumpDebugLog && frameSpeedChange > 0f && airborne)
                Plugin.MyLog.LogInfo($"[WeaponWeight] push-off: +{frameSpeedChange:F2} m/s (up {verticalSpeed:F2} m/s)");
        }
    }
}
