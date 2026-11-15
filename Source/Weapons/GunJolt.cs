using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // A part hitting its stop jolts the gun: a magazine seated, a bolt or slide slammed home, a bolt-action's bolt pushed home, a pump
    // closed. The part's momentum goes into the gun as an impulse where it hits (Hit), and the gun - a rigid body your hand holds at the
    // grip - takes it as a push and a turn about the grip, on a damped spring: the hand's hold. Played on top of the hand anchor
    // (GunInteractionController.UpdateWeaponPosition, before WeaponCollision so the gun's physics has it) as a rigid move of the holder:
    // the gun and the hands on it move, your controller doesn't.
    internal static class GunJolt
    {
        public static bool enabled = true;
        public static float joltGain = 1f;              // of the physical response: 1 = the masses and speeds as they are
        // The hand's hold: how stiffly it keeps the gun where the controller has it, moving and turning about the grip. A stiffness, so a
        // light gun comes back quicker and moves less for the same knock (a 3.5 kg rifle: 3.5 Hz, and 3 Hz turning)
        public static float holdStiffness = 1700f;      // N/m
        public static float holdDamping = 0.45f;        // damping ratio (1 = it comes straight back, lower = it rocks)
        public static float turnStiffness = 90f;        // N m/rad, about the grip across the bore
        public static float turnDamping = 0.45f;
        public static float maxHoldFrequency = 25f;     // Hz: the stiffest either gets (a pistol's turn)
        // The gun as a rod along its bore: how far its stock runs behind the grip, and how hard it is to roll about the bore
        public static float stockBehindGrip = 0.25f;    // m (pistols: none)
        public static float boreRadius = 0.05f;         // m, the radius of gyration about the bore
        public static float maxDistance = 0.04f;        // m the jolt may move the grip
        public static float maxTurn = 6f;               // deg it may turn the gun
        public static bool debugLog = false;            // one line per jolt: the impulse, where, and the gun's push and turn from it

        // The jolt's state, in the gun bone's frame: the grip's move and the gun's turn about it (a rotation vector)
        private static FirearmController stateFor;
        private static Vector3 offset, velocity;            // m, m/s
        private static Vector3 turn, turnVelocity;          // rad, rad/s
        private static bool moving;
        private static float holdOmega = 22f;               // rad/s, from the held gun's mass and inertia (Hit)
        private static float turnOmega = 19f;

        private static Transform gunBoneOf;
        private static Transform gunBone;

        // The holder as it was before this frame's jolt, and after: stripped before the next anchor (it only rewrites the position)
        private static bool applied;
        private static Vector3 preLocalPosition, postLocalPosition;
        private static Quaternion preLocalRotation, postLocalRotation;

        // An impulse (N s, world) into the gun at a world point
        internal static void Hit(FirearmController firearmController, Vector3 point, Vector3 impulse, string what)
        {
            if (!enabled || firearmController == null || firearmController != VRGlobals.firearmController || firearmController.Item == null
                || impulse.sqrMagnitude < 1e-10f || !Frame(firearmController, out Transform gun, out Vector3 grip, out Vector3 bore, out float muzzleReach))
                return;
            if (stateFor != firearmController)
                Clear(firearmController);

            float mass = GunMass(firearmController);
            // Inertia about the grip across the bore (a rod from the stock to the muzzle) and about the bore
            bool pistol = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isWeapPistol;
            float behind = pistol ? 0f : stockBehindGrip;
            float length = muzzleReach + behind;
            float centreAhead = muzzleReach - length * 0.5f;
            float acrossInertia = mass * (length * length / 12f + centreAhead * centreAhead);
            float boreInertia = mass * boreRadius * boreRadius;
            float stiffest = 2f * Mathf.PI * Mathf.Max(maxHoldFrequency, 1f);
            holdOmega = Mathf.Min(Mathf.Sqrt(Mathf.Max(holdStiffness, 1f) / mass), stiffest);
            turnOmega = Mathf.Min(Mathf.Sqrt(Mathf.Max(turnStiffness, 0.1f) / Mathf.Max(acrossInertia, 1e-4f)), stiffest);

            Vector3 angularImpulse = Vector3.Cross(point - grip, impulse);
            Vector3 aboutBore = Vector3.Project(angularImpulse, bore);
            Vector3 deltaVelocity = impulse / mass * joltGain;
            Vector3 deltaSpin = (aboutBore / Mathf.Max(boreInertia, 1e-5f) + (angularImpulse - aboutBore) / Mathf.Max(acrossInertia, 1e-4f)) * joltGain;
            velocity += gun.InverseTransformDirection(deltaVelocity);
            turnVelocity += gun.InverseTransformDirection(deltaSpin);
            moving = true;
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GunJolt] {what}: {impulse.magnitude:0.00} N s, {Vector3.Distance(point, grip) * 100f:0} cm from the grip; gun {mass:0.0} kg"
                    + $" -> {deltaVelocity.magnitude:0.00} m/s, {deltaSpin.magnitude * Mathf.Rad2Deg:0} deg/s; held at {holdOmega / (2f * Mathf.PI):0.0} Hz, turning {turnOmega / (2f * Mathf.PI):0.0} Hz"
                    + $" = about {deltaVelocity.magnitude / holdOmega * 100f:0.0} cm and {deltaSpin.magnitude / turnOmega * Mathf.Rad2Deg:0.0} deg");
        }

        // After the hand anchor, before WeaponCollision.Apply
        internal static void Apply(Transform holder)
        {
            if (!moving)
                return;
            FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController != stateFor || holder == null)
            {
                Clear(firearmController);
                return;
            }
            Step(Time.deltaTime);
            if (!enabled || !Frame(firearmController, out Transform gun, out Vector3 grip, out _, out _))
                return;

            // The gun turned about the grip and moved with it, in the gun bone's frame
            Quaternion gunRotation = gun.rotation;
            float degrees = turn.magnitude * Mathf.Rad2Deg;
            Quaternion localTurn = degrees > 1e-4f ? Quaternion.AngleAxis(degrees, turn.normalized) : Quaternion.identity;
            Quaternion worldTurn = gunRotation * localTurn * Quaternion.Inverse(gunRotation);
            Vector3 move = gunRotation * offset;
            preLocalPosition = holder.localPosition;
            preLocalRotation = holder.localRotation;
            holder.SetPositionAndRotation(grip + move + worldTurn * (holder.position - grip), worldTurn * holder.rotation);
            postLocalPosition = holder.localPosition;
            postLocalRotation = holder.localRotation;
            applied = true;
        }

        // Top of UpdateWeaponPosition, after WeaponCollision.PreAnchor (its offset went on after ours): the holder back to before the jolt.
        // Change detection lets a re-stamp of the holder win.
        internal static void PreAnchor()
        {
            if (!applied)
                return;
            applied = false;
            Transform holder = VRGlobals.weaponHolder != null ? VRGlobals.weaponHolder.transform : null;
            if (holder == null)
                return;
            if (Mathf.Abs(Quaternion.Dot(holder.localRotation, postLocalRotation)) > 0.99999f)
                holder.localRotation = preLocalRotation;
            if ((holder.localPosition - postLocalPosition).sqrMagnitude < 1e-8f)
                holder.localPosition = preLocalPosition;
        }

        // The gun let go of (the sling, the support hand alone): stripped before ChestHang saves the holder's pose, and at rest
        internal static void Reset()
        {
            PreAnchor();
            Clear(null);
        }

        // The hold's springs, in small steps (stiff springs at a render frame's step would gain energy)
        private static void Step(float deltaTime)
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(deltaTime / 0.002f), 1, 50);
            float stepTime = deltaTime / steps;
            for (int i = 0; i < steps; i++)
            {
                velocity += (-holdOmega * holdOmega * offset - 2f * holdDamping * holdOmega * velocity) * stepTime;
                offset += velocity * stepTime;
                turnVelocity += (-turnOmega * turnOmega * turn - 2f * turnDamping * turnOmega * turnVelocity) * stepTime;
                turn += turnVelocity * stepTime;
            }
            // Past the most it may go, it stops there (the hand doesn't give further)
            if (offset.magnitude > maxDistance)
            {
                offset = offset.normalized * maxDistance;
                velocity -= Vector3.Project(velocity, offset) * (Vector3.Dot(velocity, offset) > 0f ? 1f : 0f);
            }
            float maxTurnRadians = maxTurn * Mathf.Deg2Rad;
            if (turn.magnitude > maxTurnRadians)
            {
                turn = turn.normalized * maxTurnRadians;
                turnVelocity -= Vector3.Project(turnVelocity, turn) * (Vector3.Dot(turnVelocity, turn) > 0f ? 1f : 0f);
            }
            if (offset.sqrMagnitude < 1e-10f && velocity.sqrMagnitude < 1e-8f && turn.sqrMagnitude < 1e-10f && turnVelocity.sqrMagnitude < 1e-8f)
                Clear(stateFor);
        }

        private static void Clear(FirearmController firearmController)
        {
            stateFor = firearmController;
            offset = velocity = turn = turnVelocity = Vector3.zero;
            moving = false;
        }

        // The gun bone (Weapon_root_anim/weapon), the grip your hand holds (weapon_R_IK_marker), the way the bore points and how far ahead
        // of the grip the muzzle is
        private static bool Frame(FirearmController firearmController, out Transform gun, out Vector3 grip, out Vector3 bore, out float muzzleReach)
        {
            gun = GunBone(firearmController);
            grip = Vector3.zero;
            bore = Vector3.forward;
            muzzleReach = 0.3f;
            Transform weaponRoot = firearmController.WeaponRoot;
            if (gun == null || weaponRoot == null || VRGlobals.player == null || VRGlobals.player._markers == null || VRGlobals.player._markers.Length < 2
                || VRGlobals.player._markers[1] == null)
                return false;
            grip = VRGlobals.player._markers[1].transform.position;
            bore = -weaponRoot.up;
            Transform muzzle = firearmController.CurrentFireport?.Original;
            if (muzzle != null)
                muzzleReach = Mathf.Clamp(Vector3.Dot(muzzle.position - grip, bore), 0.05f, 1.5f);
            return true;
        }

        // On the bore line level with the grip, this far ahead: where a bolt or slide hits home
        internal static bool ChamberPoint(FirearmController firearmController, float aheadOfGrip, out Vector3 chamber, out Vector3 bore)
        {
            chamber = Vector3.zero;
            if (!Frame(firearmController, out _, out Vector3 grip, out bore, out _))
                return false;
            Transform muzzle = firearmController.CurrentFireport?.Original;
            Vector3 onBore = muzzle != null ? muzzle.position - bore * Vector3.Dot(muzzle.position - grip, bore) : grip;
            chamber = onBore + bore * aheadOfGrip;
            return true;
        }

        internal static float GunMass(FirearmController firearmController)
        {
            float mass = 3f;
            try { mass = firearmController.Item.TotalWeight; }
            catch { }
            return Mathf.Clamp(mass, 0.4f, 20f);
        }

        private static Transform GunBone(FirearmController firearmController)
        {
            Transform root = firearmController.WeaponRoot;
            if (root == null)
                return null;
            if (gunBoneOf != root || gunBone == null)
            {
                gunBoneOf = root;
                Transform rootAnim = VRGlobals.player != null && VRGlobals.player.ProceduralWeaponAnimation != null && VRGlobals.player.ProceduralWeaponAnimation.HandsContainer != null
                    ? VRGlobals.player.ProceduralWeaponAnimation.HandsContainer.WeaponRootAnim : null;
                gunBone = rootAnim != null ? rootAnim.Find("weapon") : null;
                if (gunBone == null)
                    gunBone = root.Find("Weapon_root_anim/weapon");
                if (gunBone == null)
                    gunBone = root;
            }
            return gunBone;
        }
    }
}
