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

        // One instance per filtered target so the momentum states can't cross-pollute or double-filter:
        // Main = the rawRightHand path (one- and two-handed holds); SupportHold = ChestHang's
        // left-hand-only hold (only ever active when Main's output ISN'T driving the gun).
        public static readonly WeaponWeightFeel Main = new WeaponWeightFeel();
        public static readonly WeaponWeightFeel SupportHold = new WeaponWeightFeel();

        private Vector3 swingVel;                     // rad/s, world - the momentum state
        private float lastStepTime = -10f;

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
    }
}
