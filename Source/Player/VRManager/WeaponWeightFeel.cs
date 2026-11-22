using UnityEngine;
using TarkovVR.Source.Dev;

namespace TarkovVR.Source.Player.VRManager
{
    // Weapon weight, applied to rawRightHand's rotation only (the marker anchor keeps the gun in the hand; the position stays the hand's).
    // physicsWeight = true (default): the gun is a rigid body (its mass as a rod from the stock to the muzzle) held at the grip by a spring.
    // The spring is the gun hand's wristGripStiffness plus, two-handed, the support hand's supportGripStiffness at its distance, times the
    // stock's bracing. Against the gun's inertia that sets how fast it follows (pistol ~10 Hz, rifle ~2 Hz one-handed, ~5 Hz two-handed).
    // The hand's turn rate is fed forward, so the gun sits exactly on a still or steadily panning hand, and the weight only shows when the
    // hand speeds up or slows down (the gun trails the start of a move and carries past its end). The weight also sags the muzzle against
    // the spring. The arm's torque is capped (wristStrength, supportForce, brakeStrength, catchBoost). It runs in the rig's frame, so turning
    // or moving the rig isn't the hand moving. The lag is capped: a free simulation makes the gun look like it leaves the hand.
    // physicsWeight = false: a simpler spring.
    //  - The rotation is split into swing and twist about the barrel. Rolling the gun (twist) is light; swinging the muzzle carries the weight.
    //  - The swing is a second-order spring-damper. Its velocity is momentum, so heavy guns follow through on stops.
    //  - One-handed, a constant droop torque sags the muzzle. Two-handing stiffens the spring and removes the droop.
    //  - The swing lag is hard-capped (maxLagDegrees): the gun resists but never falls far behind the hand.
    public class WeaponWeightFeel
    {
        [Tunable("Sling, stock & weight", TunableKind.Switch, "Weapon Weight as a held rigid body on a spring: on your hand when it's still or panning steadily, trailing a "
            + "move's start and carrying past its end, sagging one-handed. Off: the older spring that always trails a little "
            + "(the rows from Max Lag down are that one's).")]
        public static bool physicsWeight = true;
        // The hold's stiffness. Against the gun's inertia about the grip it sets how quickly the gun follows the hand speeding up and
        // slowing down; against the gun's weight it sets the sag.
        [Tunable("Sling, stock & weight", TunableKind.Tune, "How stiffly the gun hand alone holds the gun. Lower = more one-handed sag and a looser, slower follow (an M4 "
            + "at 30: ~8° of sag, ~2 Hz).", Unit = "N m/rad")]
        public static float wristGripStiffness = 30f;     // N m/rad the gun hand alone (an M4 one-handed: ~8 deg of sag, ~2 Hz)
        [Tunable("Sling, stock & weight", TunableKind.Tune, "What the support hand adds two-handed, times how far apart your hands are squared. Lower = heavier, looser "
            + "two-handed.", Unit = "N/m")]
        public static float supportGripStiffness = 1500f; // N/m the support hand, at its distance from the gun hand (x that distance squared)
        [Tunable("Sling, stock & weight", TunableKind.Tune, "The most the muzzle sags under the gun's weight.", Unit = "°")]
        public static float maxSag = 15f;                 // deg
        [Tunable("Sling, stock & weight", TunableKind.Tune, "1 = the gun settles on your hand without swinging back; lower = a heavy gun rocks a little after a stop.")]
        public static float holdDamping = 1f;             // 1 = it settles without swinging back; lower = a heavy gun rocks on a stop
        [Tunable("Sling, stock & weight", TunableKind.Tune, "The loosest any hold gets (an LMG one-handed).", Unit = "Hz")]
        public static float minHoldFrequency = 1.5f;      // Hz: the softest the hold gets (an LMG one-handed)
        [Tunable("Sling, stock & weight", TunableKind.Tune, "The stiffest any hold gets (a pistol, rolling a gun about its bore).", Unit = "Hz")]
        public static float maxHoldFrequency = 25f;       // Hz: the stiffest (a pistol; rolling any gun about its bore)
        [Tunable("Sling, stock & weight", TunableKind.Tune, "How hard the gun hand alone can turn the gun: past it a violent swing trails further. Lower = heavier "
            + "one-handed.", Unit = "N m")]
        public static float wristStrength = 15f;          // N m the gun hand alone can turn the gun with
        [Tunable("Sling, stock & weight", TunableKind.Tune, "What the support hand adds to that, times how far apart your hands are. Lower = heavier two-handed.", Unit = "N")]
        public static float supportForce = 120f;          // N the support hand adds, at its distance from the gun hand
        [Tunable("Sling, stock & weight", TunableKind.Tune, "x the strength for stopping a swing. Lower = heavy guns carry through further past your hand.")]
        public static float brakeStrength = 3f;           // x the strength while stopping the gun
        [Tunable("Sling, stock & weight", TunableKind.Tune, "How much harder the grip works as the gun falls behind (so it never trails far).")]
        public static float catchBoost = 8f;              // the strength x (1 + this x (lag / maxLagDegrees)^2): the grip tightens as it falls behind
        [Tunable("Sling, stock & weight", TunableKind.Tune, "How far the gun reaches behind the grip, for its inertia (pistols: none).", Unit = "m")]
        public static float stockBehindGrip = 0.25f;      // m the stock reaches behind the grip (the gun is a rod from there to the muzzle; pistols: none)
        public static float boreRadius = 0.05f;           // m radius of gyration about the bore (rolling the gun is easy)
        public static float handSpinSmoothing = 0.015f;   // s the hand's own turn rate is smoothed over (tracking noise)
        [Tunable("Sling, stock & weight", TunableKind.Log, "A [WeaponWeight] line a second: the gun's mass, length and inertia, its weight, your strength, the sag and how "
            + "far behind your hand it is.", Suggested = true)]
        public static bool physicsDebugLog = false;       // a line a second: the gun's mass and inertia, the strength, how far behind it is
        public static float springBase = 350f;        // spring stiffness before ErgonomicWeight divides it; higher = lighter feel overall
        [Tunable("Sling, stock & weight", TunableKind.Tune, "1 settles without overshoot; lower gives more follow-through.")]
        public static float dampingRatio = 0.95f;     // 1 = settles with no overshoot; lower = more follow-through on stops
        public static float supportStiffness = 2.5f;  // two-handed spring multiplier (the second hand's leverage)
        [Tunable("Sling, stock & weight", TunableKind.Tune, "How fast the gun's roll catches up with your wrist.", Unit = "/s")]
        public static float twistRate = 25f;          // 1/s; barrel-roll catch-up. High because rolling a gun about its bore is easy
        public static float droopPerErgo = 0.9f;      // deg of one-handed muzzle sag per ErgonomicWeight unit
        public static float maxDroop = 8f;            // deg; sag cap
        [Tunable("Sling, stock & weight", TunableKind.Tune, "The most a gun's weight can lag behind your hand.", Unit = "°")]
        public static float maxLagDegrees = 20f;      // hard cap on total rotation lag behind the hand
        public static float maxSwingVel = 30f;        // rad/s safety rail on the momentum state

        // Jumping: the droop is 1 g pulling the muzzle down, and the body's vertical acceleration reaches the gun the same way. A push-off
        // or landing dips the muzzle (an impulse: the body's vertical speed change over g, times the gun's weight torque). In free fall
        // the gun weighs nothing and the droop lifts. Rotation only, through this spring and its lag cap. Follows the Weapon Weight setting.
        [Tunable("Sling, stock & weight", TunableKind.Switch, "Jumping reaches the gun (Weapon Weight on): the muzzle dips as you push off and as you land, then springs "
            + "back; in the air the droop lifts.")]
        public static bool jumpInertia = true;
        [Tunable("Sling, stock & weight", TunableKind.Tune, "How hard jumps and landings move the gun. 1 = as much as the weight that droops the muzzle; 2 = twice the dip.")]
        public static float jumpInertiaScale = 1f;    // 1 = as much as the weight that droops the muzzle; 2 = twice the dip
        [Tunable("Sling, stock & weight", TunableKind.Switch, "In the air the gun weighs nothing: the droop lifts (one-handed, to level). Off: only the push-off and landing "
            + "dips.")]
        public static bool airborneFloat = true;      // in the air the droop lifts (one-handed: to level)
        [Tunable("Sling, stock & weight", TunableKind.Tune, "How long a fall without a jump (off a ledge) must last before it counts, so stair edges don't move the gun.", Unit = "s")]
        public static float minAirTime = 0.12f;       // s off the ground before a fall without a jump counts (so a stair edge doesn't)
        public static float takeoffWindow = 0.15f;    // s after leaving the ground the push-off's speed is still read (it builds over frames)
        public static float maxBodySpeedChange = 8f;  // m/s cap on one dip (a teleport, a respawn)
        [Tunable("Sling, stock & weight", TunableKind.Log, "A line per push-off and landing: the change in your vertical speed that moved the gun.", Suggested = true)]
        public static bool jumpDebugLog = false;      // a line per push-off / landing: the speed change and the dip it makes
        private const float GRAVITY = 9.81f;

        // One instance per filtered target so the momentum states can't mix or double-filter: Main = the rawRightHand path;
        // SupportHold = ChestHang's left-hand-only hold (active only when Main isn't driving the gun).
        public static readonly WeaponWeightFeel Main = new WeaponWeightFeel();
        public static readonly WeaponWeightFeel SupportHold = new WeaponWeightFeel();

        private Vector3 swingVel;                     // rad/s, world - the momentum state
        private float lastStepTime = -10f;
        private int lastJumpDipFrame = -1;            // this filter already applied this frame's dip

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
        // frame = the transform the filtered rotation moves with (the rig); its own turns don't count as the hand's. Null = world.
        public Quaternion Step(Quaternion current, Quaternion target, Vector3 barrelDir,
                               float ergoWeight, bool twoHanded, float dt, Transform frame = null)
        {
            if (Time.time - lastStepTime > 0.25f)
            {
                swingVel = Vector3.zero;              // weapon was away (holster/eat/menu) - drop stale momentum
                havePhysicsState = false;
            }
            lastStepTime = Time.time;
            dt = Mathf.Clamp(dt, 1e-4f, 0.033f);
            if (physicsWeight && VRGlobals.firearmController != null)
                return PhysicsStep(current, target, barrelDir, twoHanded, dt, frame);

            Quaternion error = target * Quaternion.Inverse(current);
            if (error.w < 0f)                           // shortest arc
                error = new Quaternion(-error.x, -error.y, -error.z, -error.w);

            // decompose err = swing * twist, twist about the barrel axis
            Vector3 projected = Vector3.Project(new Vector3(error.x, error.y, error.z), barrelDir);
            Quaternion twist = new Quaternion(projected.x, projected.y, projected.z, error.w);
            float twistMagnitude = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            twist = twistMagnitude > 1e-6f ? new Quaternion(twist.x / twistMagnitude, twist.y / twistMagnitude, twist.z / twistMagnitude, twist.w / twistMagnitude)
                               : Quaternion.identity;
            Quaternion swing = error * Quaternion.Inverse(twist);

            // twist: light first-order catch-up
            current = Quaternion.Slerp(Quaternion.identity, twist, Mathf.Min(1f, twistRate * dt)) * current;

            // swing: spring-damper, stiffness from the gun's ergo weight
            swing.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f)
                angle -= 360f;
            Vector3 swingError = Mathf.Abs(angle) > 1e-4f && !float.IsInfinity(axis.x)
                ? axis.normalized * (angle * Mathf.Deg2Rad) : Vector3.zero;
            float k = springBase / Mathf.Max(ergoWeight, 1f) * (twoHanded ? supportStiffness : 1f);
            k *= ShoulderStock.SpringMult;   // a seated stock braces the gun (tighter spring)
            float damping = 2f * dampingRatio * Mathf.Sqrt(k);
            Vector3 acceleration = k * swingError - damping * swingVel;

            // Jumping: the body's vertical acceleration acts on the gun in proportion to its weight torque (1 g = what holds the one-handed droop)
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
                        acceleration -= dipAxis * weightTorque;
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
                    acceleration += droopAxis.normalized * (k * sag);
            }

            swingVel += acceleration * dt;                   // semi-implicit Euler - stable at these stiffnesses
            float swingSpeed = swingVel.magnitude;
            if (swingSpeed > maxSwingVel)
                swingVel *= maxSwingVel / swingSpeed;
            float stepDeg = swingVel.magnitude * dt * Mathf.Rad2Deg;
            if (stepDeg > 1e-4f)
                current = Quaternion.AngleAxis(stepDeg, swingVel.normalized) * current;

            // hard lag cap: resist, never detach
            float resAng = Quaternion.Angle(current, target);
            if (resAng > maxLagDegrees)
                current = Quaternion.Slerp(target, current, maxLagDegrees / resAng);

            return current;
        }

        //------------------------------------------------------------------------------------------------------------------------------------
        // The held gun as a rigid body (physicsWeight)

        private bool havePhysicsState;
        private Quaternion localOrientation = Quaternion.identity;  // the gun's orientation in the frame
        private Vector3 localSpin;                                  // rad/s, in the frame's axes
        private Quaternion lastLocalTarget = Quaternion.identity;
        private Vector3 handSpin;                                   // rad/s, the hand's own turn rate (smoothed)
        private Quaternion lastWorldOutput = Quaternion.identity;
        private float nextPhysicsLog;

        private Quaternion PhysicsStep(Quaternion current, Quaternion target, Vector3 barrelDir, bool twoHanded, float dt, Transform frame)
        {
            Quaternion frameRotation = frame != null ? frame.rotation : Quaternion.identity;
            Quaternion toLocal = Quaternion.Inverse(frameRotation);
            Quaternion localTarget = toLocal * target;
            // Where the gun is: our own state if the caller passed back what we returned (unchanged, or carried along by the frame);
            // otherwise whatever the caller set (a snap onto the hand)
            if (havePhysicsState && Quaternion.Angle(current, frameRotation * localOrientation) > 0.05f && Quaternion.Angle(current, lastWorldOutput) > 0.05f)
                havePhysicsState = false;
            if (!havePhysicsState)
            {
                localOrientation = toLocal * current;
                localSpin = Vector3.zero;
                lastLocalTarget = localTarget;
                handSpin = Vector3.zero;
                havePhysicsState = true;
            }
            Vector3 handSpinNow = RotationVector(localTarget * Quaternion.Inverse(lastLocalTarget)) / dt;
            handSpin = Vector3.Lerp(handSpin, handSpinNow, 1f - Mathf.Exp(-dt / Mathf.Max(handSpinSmoothing, 1e-3f)));

            // The gun: its mass, as a rod along the bore from the stock (pistols: the grip) to the muzzle, turned about the grip
            GunBody(barrelDir, out float mass, out float muzzleReach);
            bool pistol = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isWeapPistol;
            float length = muzzleReach + (pistol ? 0f : stockBehindGrip);
            float centreAhead = muzzleReach - length * 0.5f;
            float acrossInertia = Mathf.Max(mass * (length * length / 12f + centreAhead * centreAhead), 1e-4f);
            float boreInertia = Mathf.Max(mass * boreRadius * boreRadius, 1e-5f);
            Vector3 bore = (toLocal * barrelDir).normalized;
            Vector3 down = toLocal * Vector3.down;

            // Its weight torque about the grip (none in free fall), and a push-off or landing as a jolt of that weight
            UpdateBody();
            Vector3 weightTorque = frameWeightless ? Vector3.zero : Vector3.Cross(bore * centreAhead, down * (mass * GRAVITY));
            if (jumpInertia && frameSpeedChange > 0f && lastJumpDipFrame != Time.frameCount)
            {
                lastJumpDipFrame = Time.frameCount;
                Vector3 jolt = Vector3.Cross(bore * centreAhead, down * (mass * frameSpeedChange)) * jumpInertiaScale;
                localSpin += InverseInertia(jolt, bore, acrossInertia, boreInertia);
            }

            float lever = SupportLever();
            float strength = (wristStrength + (twoHanded ? supportForce * lever : 0f)) * ShoulderStock.SpringMult;
            float stiffness = (wristGripStiffness + (twoHanded ? supportGripStiffness * lever * lever : 0f)) * ShoulderStock.SpringMult;
            // The sag: the weight against the hold
            float sagDegrees = Mathf.Min(weightTorque.magnitude / Mathf.Max(stiffness, 1f) * Mathf.Rad2Deg, maxSag);
            Vector3 sagAxis = Vector3.Cross(bore, down);
            Quaternion sag = sagDegrees > 1e-3f && sagAxis.sqrMagnitude > 1e-6f ? Quaternion.AngleAxis(sagDegrees, sagAxis.normalized) : Quaternion.identity;

            // How quickly it follows: the hold against its inertia, across the bore and about it
            float softest = 2f * Mathf.PI * Mathf.Max(minHoldFrequency, 0.1f);
            float stiffest = 2f * Mathf.PI * Mathf.Max(maxHoldFrequency, minHoldFrequency);
            float acrossOmega = Mathf.Clamp(Mathf.Sqrt(stiffness / acrossInertia), softest, stiffest);
            float boreOmega = Mathf.Clamp(Mathf.Sqrt(stiffness / boreInertia), softest, stiffest);
            float damping = Mathf.Max(holdDamping, 0.1f);
            float capRadians = Mathf.Max(maxLagDegrees, 1f) * Mathf.Deg2Rad;
            float weightSize = weightTorque.magnitude;
            // The hold is stiff: integrate in small steps, with the hand's move spread over them
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt / 0.001f), 1, 40);
            float stepTime = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                Quaternion goal = sag * Quaternion.Slerp(lastLocalTarget, localTarget, (i + 1f) / steps);
                Vector3 error = RotationVector(goal * Quaternion.Inverse(localOrientation));
                float errorSize = error.magnitude;
                float grip = 1f + catchBoost * (errorSize / capRadians) * (errorSize / capRadians);
                // Toward the hand no faster than it can still stop there, across the bore and about it
                float spare = Mathf.Max(strength * brakeStrength * grip - weightSize, 1e-3f);
                Vector3 errorAlong = bore * Vector3.Dot(bore, error);
                Vector3 handAlong = bore * Vector3.Dot(bore, handSpin);
                Vector3 spinAlong = bore * Vector3.Dot(bore, localSpin);
                Vector3 wantedAcceleration =
                    Follow(error - errorAlong, handSpin - handAlong, localSpin - spinAlong, acrossOmega, damping, spare / acrossInertia)
                    + Follow(errorAlong, handAlong, spinAlong, boreOmega, damping, spare / boreInertia);
                Vector3 accelerationTorque = Inertia(wantedAcceleration, bore, acrossInertia, boreInertia);
                Vector3 armTorque = accelerationTorque - weightTorque;
                float limit = strength * grip * (Vector3.Dot(accelerationTorque, localSpin) < 0f ? brakeStrength : 1f);
                float armSize = armTorque.magnitude;
                if (armSize > limit)
                    armTorque *= limit / armSize;
                localSpin += InverseInertia(armTorque + weightTorque, bore, acrossInertia, boreInertia) * stepTime;
                float spinSize = localSpin.magnitude;
                if (spinSize > maxSwingVel)
                    localSpin *= maxSwingVel / spinSize;
                float stepDegrees = localSpin.magnitude * stepTime * Mathf.Rad2Deg;
                if (stepDegrees > 1e-6f)
                    localOrientation = Quaternion.AngleAxis(stepDegrees, localSpin.normalized) * localOrientation;
            }
            lastLocalTarget = localTarget;

            // Never more than maxLagDegrees off the hand: at the cap it is carried along with the hand
            float lag = Quaternion.Angle(localOrientation, localTarget);
            if (lag > maxLagDegrees)
            {
                localOrientation = Quaternion.Slerp(localTarget, localOrientation, maxLagDegrees / lag);
                localSpin = handSpin;
            }
            if (physicsDebugLog && Time.time >= nextPhysicsLog)
            {
                nextPhysicsLog = Time.time + 1f;
                Plugin.MyLog.LogInfo($"[WeaponWeight] {mass:0.0} kg, {length * 100f:0} cm ({muzzleReach * 100f:0} ahead of the grip), inertia {acrossInertia:0.000} kg m2, weight {weightSize:0.0} N m,"
                    + $" hold {stiffness:0} N m/rad = {acrossOmega / (2f * Mathf.PI):0.0} Hz, strength {strength:0} N m"
                    + $" ({(twoHanded ? $"two-handed, hands {lever * 100f:0} cm apart" : "one hand")}), sag {sagDegrees:0.0} deg, {lag:0.0} deg behind the hand");
            }
            lastWorldOutput = frameRotation * localOrientation;
            return lastWorldOutput;
        }

        // A damped spring toward the hand that also carries the hand's own turn rate (omega = the hold against the inertia, damping = its
        // ratio). It closes no faster than it can stop at the hand with stoppingAcceleration.
        private static Vector3 Follow(Vector3 error, Vector3 handSpin, Vector3 spin, float omega, float damping, float stoppingAcceleration)
        {
            float errorSize = error.magnitude;
            Vector3 errorDirection = errorSize > 1e-7f ? error / errorSize : Vector3.zero;
            float closing = Mathf.Min(omega / (2f * damping) * errorSize, Mathf.Sqrt(2f * stoppingAcceleration * errorSize));
            return 2f * damping * omega * (handSpin + errorDirection * closing - spin);
        }

        private static Vector3 Inertia(Vector3 vector, Vector3 bore, float across, float aboutBore)
        {
            Vector3 along = bore * Vector3.Dot(bore, vector);
            return (vector - along) * across + along * aboutBore;
        }

        private static Vector3 InverseInertia(Vector3 torque, Vector3 bore, float across, float aboutBore)
        {
            Vector3 along = bore * Vector3.Dot(bore, torque);
            return (torque - along) / across + along / aboutBore;
        }

        // A rotation as its axis times its angle, rad (the shortest way round)
        private static Vector3 RotationVector(Quaternion rotation)
        {
            if (rotation.w < 0f)
                rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
            rotation.ToAngleAxis(out float degrees, out Vector3 axis);
            if (degrees > 180f)
                degrees -= 360f;
            if (Mathf.Abs(degrees) < 1e-6f || float.IsInfinity(axis.x) || float.IsNaN(axis.x))
                return Vector3.zero;
            return axis.normalized * (degrees * Mathf.Deg2Rad);
        }

        // The support hand's lever on the gun: how far apart the hands are
        private static float SupportLever()
        {
            if (VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.LeftHand == null || VRGlobals.vrPlayer.RightHand == null)
                return 0.3f;
            return Mathf.Clamp(Vector3.Distance(VRGlobals.vrPlayer.LeftHand.transform.position, VRGlobals.vrPlayer.RightHand.transform.position), 0.1f, 0.9f);
        }

        // The held gun's mass (mods, magazine and rounds in it) and how far its muzzle is ahead of the grip along the bore, read once a second
        private static EFT.Player.FirearmController gunBodyOf;
        private static float gunBodyReadTime = -10f;
        private static float gunMass = 3f;
        private static float gunMuzzleReach = 0.5f;

        private static void GunBody(Vector3 barrelDir, out float mass, out float muzzleReach)
        {
            EFT.Player.FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController != gunBodyOf || Time.time - gunBodyReadTime > 1f)
            {
                gunBodyOf = firearmController;
                gunBodyReadTime = Time.time;
                try { gunMass = Mathf.Clamp(firearmController.Item.TotalWeight, 0.3f, 20f); }
                catch { gunMass = 3f; }
                Transform muzzle = firearmController.CurrentFireport?.Original;
                Transform grip = VRGlobals.player != null && VRGlobals.player._markers != null && VRGlobals.player._markers.Length > 1 && VRGlobals.player._markers[1] != null
                    ? VRGlobals.player._markers[1].transform : null;
                if (muzzle != null && grip != null)
                    gunMuzzleReach = Mathf.Clamp(Vector3.Dot(muzzle.position - grip.position, barrelDir.normalized), 0.05f, 1.5f);
            }
            mass = gunMass;
            muzzleReach = gunMuzzleReach;
        }

        // Once a frame: the body's vertical speed from its position, and from that this frame's push-off or landing and whether it is in
        // free fall. Being off the ground counts once it is a jump (Tarkov's jump state at lift-off) or has lasted minAirTime.
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
                // The push-off: the upward speed built over its first frames, passed to the gun as it builds
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
