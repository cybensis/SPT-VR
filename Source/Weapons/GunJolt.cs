using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The jolt Tarkov's reload clips give the gun when a part hits its stop: a magazine seated, a bolt or slide slammed home (the charging
    // handle let go, the bolt catch hit), a bolt-action's bolt pushed home, a pump closed. gen_jolts.py bakes each gun's from its own clips:
    // the gun bone's pose in its own frame from that moment, less the clip's slow move back to the aim. Our by-hand steps finish Tarkov's
    // operation without its clip, and the hand anchor would cancel the gun bone's motion anyway: this plays the jolt on top of the anchor
    // (GunInteractionController.UpdateWeaponPosition, before WeaponCollision so the gun's physics has it) as a rigid move of the holder -
    // the gun and the hands on it move, your controller doesn't. Scaled by how hard the step was done (Play's strength).
    internal static class GunJolt
    {
        public static bool enabled = true;
        public static float joltShare = 1f;             // of the clip's jolt the gun shows: 1 = vanilla's
        // A push (a magazine into the well, a bolt-action's bolt, a pump): its speed at the stop, m/s, from minStrength to the full jolt
        public static float minStrength = 0.15f;
        public static float softSpeed = 0.15f;
        public static float hardSpeed = 1.2f;
        // The most a jolt moves the gun: a pistol's clips swing it up to the aim right after the slide closes, which can't be told from
        // the jolt (the curve is scaled down whole to fit)
        public static float maxDistance = 0.05f;        // m
        public static float maxTurn = 8f;               // deg
        public static float pistolMaxDistance = 0.025f; // m
        public static float pistolMaxTurn = 8f;         // deg
        public static bool debugLog = false;            // one line per jolt: the gun's clip, how hard, how big

        internal enum Kind { Magazine, Slam, Catch, BoltClose, Pump }

        private static readonly string JOLTS_FILE = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\sptvr\\Assets\\ReloadJolts.json");

        private sealed class Curve
        {
            public string clip;
            public float dt;
            public float peak;              // m
            public float peakTurn;          // deg
            public float[][] position;      // m, the gun's frame at the stop
            public float[][] rotation;      // rotation vector, deg, same frame
        }

        private sealed class Playing
        {
            public Curve curve;
            public float startTime;
            public float scale;
            public FirearmController firearm;
        }

        private static Dictionary<string, Dictionary<string, Curve>> curves;
        private static readonly List<Playing> playing = new List<Playing>(4);
        private static Transform gunBoneOf;
        private static Transform gunBone;

        // The holder as it was before this frame's jolt, and after: stripped before the next anchor (it only rewrites the position)
        private static bool applied;
        private static Vector3 preLocalPosition, postLocalPosition;
        private static Quaternion preLocalRotation, postLocalRotation;

        // How hard a push was: its speed (m/s) at the stop -> 0..1
        internal static float PushStrength(float speed) => Mathf.Lerp(minStrength, 1f, Mathf.InverseLerp(softSpeed, hardSpeed, speed));

        // family = the gun's reload clips' name (VRReloadController.AnimationFamily); magazineType = the magazine's magAnimationIndex
        internal static void Play(FirearmController firearmController, string family, Kind kind, int magazineType, float strength)
        {
            if (!enabled || firearmController == null || string.IsNullOrEmpty(family) || joltShare <= 0f || strength <= 0f)
                return;
            Curve curve = Find(family, kind, magazineType);
            if (curve == null || curve.position == null || curve.rotation == null || curve.position.Length < 2 || curve.dt <= 0f)
            {
                if (debugLog)
                    Plugin.MyLog.LogInfo($"[GunJolt] {family} {kind}: no jolt in its clips");
                return;
            }
            bool pistol = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isWeapPistol;
            float distanceCap = pistol ? pistolMaxDistance : maxDistance;
            float turnCap = pistol ? pistolMaxTurn : maxTurn;
            float fit = Mathf.Min(1f, distanceCap / Mathf.Max(curve.peak, 1e-4f), turnCap / Mathf.Max(curve.peakTurn, 1e-3f));
            float scale = Mathf.Clamp01(strength) * joltShare * fit;
            if (scale < 0.01f)
                return;
            if (playing.Count >= 3)
                playing.RemoveAt(0);
            playing.Add(new Playing { curve = curve, startTime = Time.time, scale = scale, firearm = firearmController });
            if (debugLog)
                Plugin.MyLog.LogInfo($"[GunJolt] {family} {kind} ({curve.clip}): strength {strength:0.00}, {curve.peak * scale * 100f:0.0} cm / {curve.peakTurn * scale:0.0} deg"
                    + (fit < 1f ? $" (fitted to {distanceCap * 100f:0.#} cm / {turnCap:0.#} deg)" : ""));
        }

        // After the hand anchor, before WeaponCollision.Apply
        internal static void Apply(Transform holder)
        {
            if (playing.Count == 0)
                return;
            FirearmController firearmController = VRGlobals.firearmController;
            Vector3 move = Vector3.zero;
            Vector3 turn = Vector3.zero;
            float now = Time.time;
            for (int i = playing.Count - 1; i >= 0; i--)
            {
                Playing jolt = playing[i];
                if (jolt.firearm != firearmController || !Sample(jolt.curve, now - jolt.startTime, out Vector3 position, out Vector3 rotation))
                {
                    playing.RemoveAt(i);
                    continue;
                }
                move += position * jolt.scale;
                turn += rotation * jolt.scale;
            }
            if (!enabled || holder == null || firearmController == null || (move.sqrMagnitude < 1e-10f && turn.sqrMagnitude < 1e-6f))
                return;
            Transform gun = GunBone(firearmController);
            if (gun == null)
                return;

            // The gun moved by the jolt in its own frame, the holder with it (the gun and the hands on it are under it)
            Vector3 gunPosition = gun.position;
            Quaternion gunRotation = gun.rotation;
            float degrees = turn.magnitude;
            Quaternion localTurn = degrees > 1e-4f ? Quaternion.AngleAxis(degrees, turn / degrees) : Quaternion.identity;
            Vector3 targetPosition = gunPosition + gunRotation * move;
            Quaternion worldTurn = gunRotation * localTurn * Quaternion.Inverse(gunRotation);
            preLocalPosition = holder.localPosition;
            preLocalRotation = holder.localRotation;
            holder.SetPositionAndRotation(targetPosition + worldTurn * (holder.position - gunPosition), worldTurn * holder.rotation);
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

        // The gun let go of (the sling, the support hand alone): stripped before ChestHang saves the holder's pose, and none left to play
        internal static void Reset()
        {
            PreAnchor();
            playing.Clear();
        }

        // The clips' gun bone (Weapon_root_anim/weapon): the frame the jolt was measured in
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
                {
                    gunBone = root;
                    Plugin.MyLog.LogWarning($"[GunJolt] no Weapon_root_anim/weapon under {root.name}: jolts in its frame");
                }
            }
            return gunBone;
        }

        private static bool Sample(Curve curve, float time, out Vector3 position, out Vector3 rotation)
        {
            position = Vector3.zero;
            rotation = Vector3.zero;
            float frame = time / curve.dt;
            int frames = Mathf.Min(curve.position.Length, curve.rotation.Length);
            if (time < 0f || frame >= frames - 1)
                return false;
            int index = Mathf.FloorToInt(frame);
            float blend = frame - index;
            position = Vector3.Lerp(ToVector(curve.position[index]), ToVector(curve.position[index + 1]), blend);
            rotation = Vector3.Lerp(ToVector(curve.rotation[index]), ToVector(curve.rotation[index + 1]), blend);
            return true;
        }

        private static Vector3 ToVector(float[] values) => values != null && values.Length >= 3 ? new Vector3(values[0], values[1], values[2]) : Vector3.zero;

        private static Curve Find(string family, Kind kind, int magazineType)
        {
            Load();
            if (curves == null || !curves.TryGetValue(family, out Dictionary<string, Curve> events))
                return null;
            switch (kind)
            {
                case Kind.Magazine:
                    if (events.TryGetValue("magazine|" + magazineType, out Curve own) || events.TryGetValue("magazine|0", out own))
                        return own;
                    foreach (KeyValuePair<string, Curve> entry in events)
                    {
                        if (entry.Key.StartsWith("magazine|"))
                            return entry.Value;
                    }
                    return null;
                case Kind.Slam:
                    return Event(events, "slam") ?? Event(events, "catch");
                case Kind.Catch:
                    return Event(events, "catch") ?? Event(events, "slam");
                case Kind.BoltClose:
                    return Event(events, "boltClose") ?? Event(events, "slam");
                case Kind.Pump:
                    return Event(events, "pump");
            }
            return null;
        }

        private static Curve Event(Dictionary<string, Curve> events, string name) => events.TryGetValue(name, out Curve curve) ? curve : null;

        private static void Load()
        {
            if (curves != null)
                return;
            curves = new Dictionary<string, Dictionary<string, Curve>>();
            if (!File.Exists(JOLTS_FILE))
            {
                Plugin.MyLog.LogWarning($"[GunJolt] {JOLTS_FILE} is missing: no reload jolts");
                return;
            }
            try
            {
                curves = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, Curve>>>(File.ReadAllText(JOLTS_FILE)) ?? curves;
                Plugin.MyLog.LogInfo($"[GunJolt] reload jolts for {curves.Count} guns' clips");
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[GunJolt] couldn't read {JOLTS_FILE}: {exception.Message}");
            }
        }
    }
}
