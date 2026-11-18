using System.Collections.Generic;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Break-actions by hand (Manual Reload): MP-43-1C, its sawed-off, MP-18.
    //  - B (Y left-handed) is the top lever / locking lever (a support grip lets go): the barrels unlatch and swing on Tarkov's hinge (gen_breakactions.py: every bone
    //    the open clip moves, keyed by the barrel's turn, the lever's own swing). Free, they swing as a rod on the hinge under gravity and the
    //    gun's own motion (a flick shuts them); fully home with the lever let go they latch.
    //  - The off hand's grip on the fore-end holds them: their turn follows the hand about the hinge, pushed home they latch and the hand
    //    stays on as the two-handed grip. Open, the off hand is never the support grip (the gun hand aims alone).
    //  - Open, the off hand's grip at a chamber draws its shell out along the bore. A fired case let go flies off with the hand (Tarkov's own
    //    shell physics); a live round goes to the rig at the chest pouch, into an empty chamber at its mouth, else to the ground.
    //  - The chest pouch's grip with a chamber empty takes a shell (the bolt-action rounds' hold machinery); lined up on an empty chamber it
    //    slides in along the bore, past breakShellCommit the inventory move into that chamber runs and Tarkov's round model is put in.
    //  - Moves into / out of a chamber skip Tarkov's own chamber operations (LetsSingleRoundIn), like the bolt-action rounds.
    public static partial class VRReloadController
    {
        public static bool breakActions = true;             // false = Tarkov's own break-action reloads only
        public static bool breakDebugLog = true;            // unlatch, holds, cases, shells
        public static float barrelOpenEnough = 0.6f;        // share of the hinge's travel open before the chambers can be reached
        public static float foreEndGrabRadius = 0.1f;       // m from the palm to where the fore-end is taken
        public static float caseGrabRadius = 0.045f;        // m from the palm to a chamber's mouth that takes its shell
        public static float caseFreeShare = 0.85f;          // share of its length a shell comes out before it's free in the hand
        public static float breakShellCaptureRadius = 0.035f;   // m off a chamber's bore line a held shell goes onto it
        public static float breakShellCaptureAngle = 40f;   // deg off the bore
        public static float breakShellReach = 0.03f;        // m past its own length behind the chamber the bore line catches it
        public static float breakShellCommit = 0.5f;        // share of its length in past which it's loaded and slides home itself
        public static float breakShellFinishSpeed = 0.5f;   // m/s the rest of the way
        public static float caseIntoHandTime = 0.12f;       // s a drawn shell eases into the hand's hold
        public static float leverTime = 0.06f;              // s the lever takes to swing
        // The barrels on the hinge: a rod about its end (inertia 4/3 m r^2 at its centre of mass r), gravity and the hinge's own acceleration
        // (the gun's motion) as pseudo-forces, the gun's turn about the hinge axis left behind; hinge friction, a bounce off the stop
        public static float barrelGravity = 1f;
        public static float barrelInertia = 1f;             // share of the gun's own motion felt (0 = gravity only)
        public static float barrelMotionSmoothing = 0.06f;  // s the gun motion's derivatives are smoothed over (tracking noise)
        public static float barrelFriction = 3f;            // 1/s
        public static float barrelBounce = 0.15f;
        public static float barrelStopSound = 60f;          // deg/s into the open stop that sounds it

        public static bool HoldingBarrels { get; private set; }
        public static bool HoldingBreakCase { get; private set; }

        // The off hand is ours on an unlatched break-action (never two-handing open barrels) and while it works one
        private static bool BreakActionOwnsOffHand => HoldingBarrels || HoldingBreakCase || breakShellHeld || breakGunOpen;
        private static bool breakGunOpen;                   // this frame: our break-action, unlatched or open

        // No firing: unlatched, open, or a shell on its way
        public static bool BreakActionBlocksFiring => breakGunOpen || HoldingBarrels || HoldingBreakCase || (breakShellHeld && breakShellOnBore);

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The data (ReloadHolds.json "breakActions")

        public sealed class SavedBreakAction
        {
            public string barrel;               // the hinge's bone, under the weapon bone
            public float[] hingeAxis;           // in its closed local frame, + opening
            public float openDegrees;
            public List<string> chambers;
            public SavedBreakBones open;        // every bone the open moves, keyed by the barrel's turn
            public SavedBreakBones lever;       // the lever's swing
            public SavedBreakHold foreEnd;      // the left palm on the fore-end, in the barrel bone's frame; grabPoint where the fingers close
            public SavedBreakHold loadHold;     // a new shell's bone in the left palm's frame
            public SavedBreakHold pluckHold;    // a drawn one's
        }

        public sealed class SavedBreakBones
        {
            public float degrees;
            public List<SavedPumpBone> bones;
        }

        public sealed class SavedBreakHold
        {
            public float[] position;
            public float[] rotation;
            public float[] fingers;
            public float[] grabPoint;
        }

        private sealed class BreakHold
        {
            public Vector3 position;
            public Quaternion rotation;
            public Vector3[] fingers;
            public Vector3 grabPoint;
        }

        private sealed class KeyedBones
        {
            public string[] paths;
            public Vector3[][] positions;
            public Quaternion[][] rotations;
        }

        private sealed class BreakData
        {
            public string barrelPath;
            public Vector3 hingeAxis;
            public float openDegrees;
            public int chamberCount;
            public KeyedBones open;
            public KeyedBones lever;
            public BreakHold foreEnd;
            public BreakHold loadHold;
            public BreakHold pluckHold;
        }

        private static readonly Dictionary<string, BreakData> breakGuns = new Dictionary<string, BreakData>();
        private static readonly Dictionary<string, string> breakFamilies = new Dictionary<string, string>();    // by template id

        private static BreakData BreakFrom(SavedBreakAction saved)
        {
            if (saved == null || string.IsNullOrEmpty(saved.barrel) || saved.hingeAxis == null || saved.open == null || saved.foreEnd == null)
                return null;
            BreakData data = new BreakData
            {
                barrelPath = saved.barrel,
                hingeAxis = new Vector3(saved.hingeAxis[0], saved.hingeAxis[1], saved.hingeAxis[2]).normalized,
                openDegrees = Mathf.Max(saved.openDegrees, 1f),
                chamberCount = saved.chambers?.Count ?? 1,
                open = KeyedFrom(saved.open),
                lever = KeyedFrom(saved.lever),
                foreEnd = BreakHoldFrom(saved.foreEnd),
                loadHold = BreakHoldFrom(saved.loadHold),
                pluckHold = BreakHoldFrom(saved.pluckHold),
            };
            return data.open != null && data.foreEnd != null && System.Array.IndexOf(data.open.paths, data.barrelPath) >= 0 ? data : null;
        }

        private static KeyedBones KeyedFrom(SavedBreakBones saved)
        {
            if (saved == null || saved.bones == null || saved.bones.Count == 0)
                return null;
            int count = saved.bones.Count;
            KeyedBones keyed = new KeyedBones { paths = new string[count], positions = new Vector3[count][], rotations = new Quaternion[count][] };
            for (int bone = 0; bone < count; bone++)
            {
                SavedPumpBone savedBone = saved.bones[bone];
                int keyCount = savedBone.keys?.Count ?? 0;
                if (keyCount < 2)
                    return null;
                keyed.paths[bone] = savedBone.path;
                keyed.positions[bone] = new Vector3[keyCount];
                keyed.rotations[bone] = new Quaternion[keyCount];
                for (int key = 0; key < keyCount; key++)
                {
                    keyed.positions[bone][key] = Vector3From(savedBone.keys[key], 0);
                    keyed.rotations[bone][key] = QuaternionFrom(savedBone.keys[key], 3);
                }
            }
            return keyed;
        }

        private static BreakHold BreakHoldFrom(SavedBreakHold saved)
        {
            if (saved == null || saved.position == null || saved.rotation == null)
                return null;
            return new BreakHold
            {
                position = Vector3From(saved.position, 0),
                rotation = QuaternionFrom(saved.rotation, 0),
                fingers = FingersFrom(saved.fingers),
                grabPoint = saved.grabPoint != null && saved.grabPoint.Length >= 3 ? Vector3From(saved.grabPoint, 0) : Vector3From(saved.position, 0),
            };
        }

        // The family: the longest name before "_reload_start" among the animator's clips we have data for (the sawed-off's clips start
        // with the MP-43's name too)
        private static bool TryGetBreakAction(FirearmController firearmController, out BreakData data)
        {
            data = null;
            if (firearmController == null || firearmController.Item == null || firearmController.Item.ReloadMode != Weapon.EReloadMode.OnlyBarrel)
                return false;
            LoadRecordedGrips();
            string templateId = firearmController.Item.TemplateId.ToString();
            if (!breakFamilies.TryGetValue(templateId, out string family))
            {
                RuntimeAnimatorController animatorController = firearmController.FirearmsAnimator?.Animator?.runtimeAnimatorController;
                if (animatorController == null)
                    return false;
                family = null;
                foreach (AnimationClip clip in animatorController.animationClips)
                {
                    if (clip == null)
                        continue;
                    string clipName = ClipName(clip);
                    int end = clipName.IndexOf("_reload_start", System.StringComparison.OrdinalIgnoreCase);
                    if (end <= 0)
                        continue;
                    string name = clipName.Substring(0, end).ToLowerInvariant();
                    if (breakGuns.ContainsKey(name) && (family == null || name.Length > family.Length))
                        family = name;
                }
                breakFamilies[templateId] = family;
                if (breakDebugLog)
                {
                    BaseSoundPlayer soundPlayer = GunSoundPlayerOf(firearmController);
                    Plugin.MyLog.LogInfo($"[VRBreak] {firearmController.Item.LocalizedShortName()}: clips '{family ?? "none"}', sounds: "
                        + (soundPlayer != null ? string.Join(", ", soundPlayer.AdditionalSounds.ConvertAll(sound => sound.EventName)) : "no sound player"));
                }
            }
            return family != null && breakGuns.TryGetValue(family, out data);
        }

        private static bool ManualBreakOn(FirearmController firearmController, out BreakData data)
        {
            data = null;
            return enabled && breakActions && VRSettings.GetManualReloadOn() && firearmController != null && firearmController._player != null
                && firearmController._player.IsYourPlayer && TryGetBreakAction(firearmController, out data);
        }

        // The hands load this gun (GunInteractionController's ManualReloadReplacesMenu)
        public static bool HandsLoadBreakAction(FirearmController firearmController)
        {
            return ManualBreakOn(firearmController, out _);
        }

        // B (Y left-handed) is this gun's lever: never Tarkov's reload (InputHandlers). Not on a malfunction: Tarkov's own fix.
        public static bool GunHandButtonIsBarrelLever(FirearmController firearmController)
        {
            return VRGlobals.inGame && ManualBreakOn(firearmController, out _) && firearmController.Item.MalfState.State == Weapon.EMalfunctionState.None;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The barrels' state, per gun (it stays across a swap)

        private sealed class BreakState
        {
            public float open;              // deg off shut
            public float speed;             // deg/s
            public bool latched = true;
            public float lever;             // 0 at rest .. 1 swung over
            public int restFrames;          // frames the rest pose is still written after it got there (the animator may not write it)
        }
        private static readonly Dictionary<Weapon, BreakState> breakStates = new Dictionary<Weapon, BreakState>();

        private static BreakState BreakStateOf(Weapon weapon)
        {
            if (!breakStates.TryGetValue(weapon, out BreakState state))
            {
                state = new BreakState();
                breakStates[weapon] = state;
            }
            return state;
        }

        private static FirearmController breakBonesFor;
        private static Transform breakFrame, barrelBone;
        private static Transform[] breakOpenBones, breakLeverBones;
        private static Vector3 barrelCenterOfMass;          // in the barrel bone's frame
        private static Quaternion barrelClosedLocal;        // its closed local rotation (the keys' first)

        private static bool EnsureBreakBones(FirearmController firearmController, BreakData data)
        {
            if (breakBonesFor == firearmController && barrelBone != null && breakFrame != null)
                return true;
            breakBonesFor = firearmController;
            breakFrame = ShotgunFrame(firearmController);
            barrelBone = breakFrame != null ? breakFrame.Find(data.barrelPath) : null;
            if (barrelBone == null)
                return false;
            breakOpenBones = FindKeyedBones(breakFrame, data.open);
            breakLeverBones = FindKeyedBones(breakFrame, data.lever);
            barrelClosedLocal = data.open.rotations[System.Array.IndexOf(data.open.paths, data.barrelPath)][0];
            barrelCenterOfMass = MeshCenterIn(barrelBone);
            if (breakDebugLog)
                Plugin.MyLog.LogInfo($"[VRBreak] {firearmController.Item.LocalizedShortName()}: barrel '{barrelBone.name}', {data.openDegrees:0.#} deg about {data.hingeAxis.ToString("F2")}, "
                    + $"centre of mass {(barrelCenterOfMass * 100f).ToString("F1")} cm off the hinge, open bones {CountFound(breakOpenBones)}/{data.open.paths.Length}, "
                    + $"lever {(data.lever != null ? CountFound(breakLeverBones) + "/" + data.lever.paths.Length : "none")}, chambers {data.chamberCount}");
            return true;
        }

        private static Transform[] FindKeyedBones(Transform frame, KeyedBones keyed)
        {
            if (keyed == null)
                return null;
            Transform[] bones = new Transform[keyed.paths.Length];
            for (int bone = 0; bone < bones.Length; bone++)
                bones[bone] = frame.Find(keyed.paths[bone]);
            return bones;
        }

        private static int CountFound(Transform[] bones)
        {
            int found = 0;
            if (bones != null)
            {
                foreach (Transform bone in bones)
                {
                    if (bone != null)
                        found++;
                }
            }
            return found;
        }

        // The barrels' middle in their bone's frame: their meshes' boxes (mesh-local corners), weighted by volume
        private static Vector3 MeshCenterIn(Transform bone)
        {
            Vector3 sum = Vector3.zero;
            float weight = 0f;
            foreach (MeshFilter meshFilter in bone.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter.sharedMesh == null)
                    continue;
                Bounds bounds = meshFilter.sharedMesh.bounds;
                Vector3 size = Vector3.Scale(bounds.size, meshFilter.transform.lossyScale);
                float volume = Mathf.Max(size.x * size.y * size.z, 1e-9f);
                sum += bone.InverseTransformPoint(meshFilter.transform.TransformPoint(bounds.center)) * volume;
                weight += volume;
            }
            // No meshes read: a barrel's length ahead of the hinge
            return weight > 0f ? sum / weight : new Vector3(0f, -0.25f, 0f);
        }

        private static bool KeyedPose(KeyedBones keyed, int bone, float share, out Vector3 position, out Quaternion rotation)
        {
            Vector3[] positions = keyed.positions[bone];
            Quaternion[] rotations = keyed.rotations[bone];
            float scaled = Mathf.Clamp01(share) * (positions.Length - 1);
            int index = Mathf.Min(Mathf.FloorToInt(scaled), positions.Length - 2);
            float blend = scaled - index;
            position = Vector3.Lerp(positions[index], positions[index + 1], blend);
            rotation = Quaternion.Slerp(rotations[index], rotations[index + 1], blend);
            return true;
        }

        // The barrels and the lever as our state has them: off their rest pose only (at rest the animator's pose is the same)
        private static void PoseBreakAction(BreakData data, BreakState state)
        {
            if (state.open > 0f || state.lever > 0f || HoldingBarrels)
                state.restFrames = 3;
            else if (state.restFrames > 0)
                state.restFrames--;
            bool shown = state.restFrames > 0;
            if (shown)
            {
                for (int bone = 0; bone < breakOpenBones.Length; bone++)
                {
                    Transform transform = breakOpenBones[bone];
                    if (transform != null && KeyedPose(data.open, bone, state.open / data.openDegrees, out Vector3 position, out Quaternion rotation))
                        transform.SetLocalPositionAndRotation(position, rotation);
                }
            }
            if (breakLeverBones != null && shown)
            {
                for (int bone = 0; bone < breakLeverBones.Length; bone++)
                {
                    Transform transform = breakLeverBones[bone];
                    if (transform != null && KeyedPose(data.lever, bone, state.lever, out Vector3 position, out Quaternion rotation))
                        transform.SetLocalPositionAndRotation(position, rotation);
                }
            }
        }

        // The hinge axis in world space (+ opening), through the barrel bone's origin
        private static Vector3 HingeAxisWorld(BreakData data)
        {
            Transform parent = barrelBone.parent;
            return (parent != null ? parent.rotation : Quaternion.identity) * (barrelClosedLocal * data.hingeAxis);
        }

        // Player.LateUpdate prefix (ShotgunPatches): after the animator, before Tarkov's IK
        public static void ApplyBreakActionBeforeIk()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (!ManualBreakOn(firearmController, out BreakData data) || breakBonesFor != firearmController || barrelBone == null
                || !breakStates.TryGetValue(firearmController.Item, out BreakState state))
                return;
            if (!(firearmController.CurrentOperation is FirearmController.Idling))
                return;
            PoseBreakAction(data, state);
            PlaceBreakProps(data);
        }

        // Co-op (TryBuildSync): the barrels and the lever off their rest pose, as PoseBreakAction writes them this frame
        private static void AddBreakActionParts(FirearmController firearmController, Transform weaponRoot)
        {
            if (breakBonesFor != firearmController || barrelBone == null || !ManualBreakOn(firearmController, out BreakData data)
                || !breakStates.TryGetValue(firearmController.Item, out BreakState state))
                return;
            if (state.open > 0f || HoldingBarrels)
            {
                for (int bone = 0; bone < breakOpenBones.Length; bone++)
                {
                    Transform transform = breakOpenBones[bone];
                    if (transform != null && KeyedPose(data.open, bone, state.open / data.openDegrees, out Vector3 position, out Quaternion rotation))
                        AddPartPose(weaponRoot, transform, position, rotation, transform.gameObject.activeSelf);
                }
            }
            if (breakLeverBones != null && state.lever > 0f)
            {
                for (int bone = 0; bone < breakLeverBones.Length; bone++)
                {
                    Transform transform = breakLeverBones[bone];
                    if (transform != null && KeyedPose(data.lever, bone, state.lever, out Vector3 position, out Quaternion rotation))
                        AddPartPose(weaponRoot, transform, position, rotation, transform.gameObject.activeSelf);
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // From Tick

        private static bool previousLeverButton;
        private static FirearmController breakMotionFor;
        private static int breakMotionFrame = -10;
        private static Vector3 hingePreviousPosition, hingePreviousVelocity, hingeAcceleration;
        private static Quaternion gunPreviousRotation;
        private static Vector3 gunPreviousTurnRate, gunTurnAcceleration;

        // The off hand's grip pressed this frame: true when it was the break-action's (the barrels, a shell in a chamber)
        private static bool TickBreakAction(bool gripPressed, bool gripReleased, bool handBusy)
        {
            breakGunOpen = false;
            FirearmController firearmController = VRGlobals.firearmController;
            if (!VRGlobals.inGame || VRGlobals.vrPlayer == null || VRGlobals.player == null || !ManualBreakOn(firearmController, out BreakData data)
                || !EnsureBreakBones(firearmController, data))
            {
                EndBreakHolds();
                breakMotionFor = null;
                return false;
            }
            Weapon weapon = firearmController.Item;
            BreakState state = BreakStateOf(weapon);
            // Tarkov's own reload works the barrels with its clip and leaves them shut
            if (firearmController.IsInReloadOperation() && expectedOperation == ExpectedOperation.None)
            {
                EndBreakHolds();
                state.open = 0f;
                state.speed = 0f;
                state.latched = true;
                state.lever = 0f;
                return false;
            }
            if (!(firearmController.CurrentOperation is FirearmController.Idling))
            {
                breakGunOpen = !state.latched || state.open > 0f;
                return false;
            }
            float deltaTime = Mathf.Max(Time.deltaTime, 1e-4f);
            TrackGunMotion(firearmController, deltaTime);

            // The lever: the gun hand's B (not just out of a menu: B backed out of it), not on a malfunction
            bool leverButton = Handedness.GunHandUpperButton.GetState(SteamVR_Input_Sources.Any) && !MenuExitButtonWait.Blocks
                && !VRGlobals.menuOpen && !ChestHang.Detached && weapon.MalfState.State == Weapon.EMalfunctionState.None;
            bool leverPressed = leverButton && !previousLeverButton;
            previousLeverButton = leverButton;
            if (leverPressed)
            {
                PlayFirstGunSound(firearmController, SoundHand.GunHand, "BarrelLock", "Hammer");
                PulseGunHand(0.25f, 0.03f);
                if (state.latched)
                {
                    state.latched = false;
                    state.speed = 0f;
                    if (breakDebugLog)
                        Plugin.MyLog.LogInfo($"[VRBreak] unlatched, chambers {ChambersText(firearmController, weapon)}, models {ChamberModelsText(firearmController.Firearms, weapon)}");
                }
            }
            state.lever = Mathf.MoveTowards(state.lever, leverButton || !state.latched ? 1f : 0f, deltaTime / Mathf.Max(leverTime, 0.001f));

            if (HoldingBarrels)
                UpdateBarrelHold(firearmController, data, state, gripPressed, gripReleased, leverButton, deltaTime);
            else if (!state.latched)
                SwingBarrels(firearmController, data, state, leverButton, deltaTime);

            // The off hand's grabs: a shell in a chamber, else the fore-end
            bool taken = false;
            if (gripPressed && !handBusy && !HoldingBarrels && !HoldingBreakCase && !ChestHang.Detached)
            {
                if (TryPluckCase(firearmController, data, state))
                    taken = true;
                else if ((!state.latched || state.open > 0f) && OffHandAtForeEnd(data, state))
                {
                    BeginBarrelHold(firearmController, data, state);
                    taken = true;
                }
            }
            if (HoldingBreakCase)
                UpdateCasePluck(firearmController, data, state, gripReleased);

            breakGunOpen = !state.latched || state.open > 0f;
            PoseBreakAction(data, state);
            PlaceBreakProps(data);
            UpdateBreakZones(firearmController, data, state);
            return taken;
        }

        // The hinge's acceleration and the gun's turn acceleration, smoothed (the barrels feel the gun's motion). In the rig's frame: a snap
        // turn or a height reset moves the rig in one frame, which isn't motion of the hand.
        private static Transform BreakMotionFrame => VRGlobals.vrOffsetter != null ? VRGlobals.vrOffsetter.transform : null;

        private static void TrackGunMotion(FirearmController firearmController, float deltaTime)
        {
            Transform rig = BreakMotionFrame;
            Quaternion toRig = rig != null ? Quaternion.Inverse(rig.rotation) : Quaternion.identity;
            Vector3 hinge = toRig * (barrelBone.position - (rig != null ? rig.position : Vector3.zero));
            Quaternion rotation = toRig * (barrelBone.parent != null ? barrelBone.parent.rotation : barrelBone.rotation);
            bool continuous = breakMotionFor == firearmController && Time.frameCount - breakMotionFrame == 1;
            breakMotionFor = firearmController;
            breakMotionFrame = Time.frameCount;
            if (!continuous)
            {
                hingePreviousPosition = hinge;
                hingePreviousVelocity = Vector3.zero;
                hingeAcceleration = Vector3.zero;
                gunPreviousRotation = rotation;
                gunPreviousTurnRate = Vector3.zero;
                gunTurnAcceleration = Vector3.zero;
                return;
            }
            float smoothing = 1f - Mathf.Exp(-deltaTime / Mathf.Max(barrelMotionSmoothing, 0.001f));
            Vector3 velocity = Vector3.Lerp(hingePreviousVelocity, (hinge - hingePreviousPosition) / deltaTime, smoothing);
            hingeAcceleration = Vector3.Lerp(hingeAcceleration, (velocity - hingePreviousVelocity) / deltaTime, smoothing);
            hingePreviousVelocity = velocity;
            hingePreviousPosition = hinge;
            (rotation * Quaternion.Inverse(gunPreviousRotation)).ToAngleAxis(out float turn, out Vector3 axis);
            if (turn > 180f)
                turn -= 360f;
            Vector3 turnRate = float.IsNaN(axis.x) || float.IsInfinity(axis.x) ? Vector3.zero : axis * (turn * Mathf.Deg2Rad / deltaTime);
            turnRate = Vector3.Lerp(gunPreviousTurnRate, turnRate, smoothing);
            gunTurnAcceleration = Vector3.Lerp(gunTurnAcceleration, (turnRate - gunPreviousTurnRate) / deltaTime, smoothing);
            gunPreviousTurnRate = turnRate;
            gunPreviousRotation = rotation;
        }

        // Free on the hinge: a rod about its end, gravity and the gun's motion on it
        private static void SwingBarrels(FirearmController firearmController, BreakData data, BreakState state, bool leverHeld, float deltaTime)
        {
            Vector3 axis = HingeAxisWorld(data);
            Vector3 lever = barrelBone.rotation * barrelCenterOfMass;
            Vector3 across = Vector3.ProjectOnPlane(lever, axis);
            float inertia = Mathf.Max(4f / 3f * across.sqrMagnitude, 1e-4f);
            Transform rig = BreakMotionFrame;
            Quaternion fromRig = rig != null ? rig.rotation : Quaternion.identity;
            Vector3 pull = Physics.gravity * barrelGravity - fromRig * hingeAcceleration * barrelInertia;
            float acceleration = Vector3.Dot(Vector3.Cross(lever, pull), axis) / inertia - Vector3.Dot(fromRig * gunTurnAcceleration, axis) * barrelInertia;
            float speed = state.speed * Mathf.Deg2Rad;
            speed += acceleration * deltaTime;
            speed *= Mathf.Exp(-barrelFriction * deltaTime);
            state.speed = speed * Mathf.Rad2Deg;
            MoveBarrels(firearmController, data, state, state.open + state.speed * deltaTime, leverHeld, SoundHand.GunBody);
        }

        // To that much open, at the stops: open = a bounce (and its knock), shut with the lever let go = latched
        private static void MoveBarrels(FirearmController firearmController, BreakData data, BreakState state, float open, bool leverHeld, SoundHand hand)
        {
            float before = state.open;
            if (open >= data.openDegrees)
            {
                open = data.openDegrees;
                if (state.speed > 0f)
                {
                    if (state.speed >= barrelStopSound && before < data.openDegrees)
                    {
                        PlayFirstGunSound(firearmController, hand, "BarrelOpen");
                        Pulse(hand.Seating, Mathf.Clamp01(state.speed / 600f) * 0.6f + 0.2f, 0.04f);
                    }
                    state.speed = -state.speed * barrelBounce;
                }
            }
            if (open <= 0f)
            {
                open = 0f;
                if (state.speed < 0f)
                    state.speed = 0f;
                if (!leverHeld && !state.latched)
                {
                    state.open = 0f;
                    Latch(firearmController, state, hand);
                    return;
                }
            }
            state.open = open;
        }

        private static void Latch(FirearmController firearmController, BreakState state, SoundHand hand)
        {
            state.latched = true;
            state.speed = 0f;
            PlayFirstGunSound(firearmController, hand, "BarrelClose");
            Pulse(hand.Seating, 0.6f, 0.05f);
            if (breakDebugLog)
                Plugin.MyLog.LogInfo($"[VRBreak] latched shut, chambers {ChambersText(firearmController, firearmController.Item)}");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The off hand on the fore-end

        private static FirearmController barrelsHeldFor;
        private static Transform barrelRider;               // ours, on the barrel bone: the hand latches on it
        private static Vector3 barrelAcrossAtGrab;          // the controller about the hinge at the grab, in the barrel's parent frame
        private static float barrelOpenAtGrab;
        private static bool barrelHandOnRightArm;

        // The palm at the fore-end: where the close clip's fingers close on it, with the barrels as they are
        private static bool OffHandAtForeEnd(BreakData data, BreakState state)
        {
            Vector3 grab = barrelBone.TransformPoint(MirroredForArm(data.foreEnd.grabPoint, OffHandOnRightArm));
            return (OffHandPalm() - grab).sqrMagnitude <= foreEndGrabRadius * foreEndGrabRadius;
        }

        private static Vector3 MirroredForArm(Vector3 pointInBarrel, bool rightArm) => rightArm ? new Vector3(-pointInBarrel.x, pointInBarrel.y, pointInBarrel.z) : pointInBarrel;

        private static void BeginBarrelHold(FirearmController firearmController, BreakData data, BreakState state)
        {
            HoldingBarrels = true;
            barrelsHeldFor = firearmController;
            barrelHandOnRightArm = OffHandOnRightArm;
            barrelOpenAtGrab = state.open;
            state.speed = 0f;
            Transform controller = HandRig(gunHand: false);
            barrelAcrossAtGrab = AcrossBarrelHinge(data, controller.position);
            if (barrelRider == null)
                barrelRider = new GameObject("VRBarrelHand").transform;
            barrelRider.SetParent(barrelBone, worldPositionStays: false);
            barrelRider.SetPositionAndRotation(controller.position, controller.rotation);
            // The hand onto the clip's hold on the fore-end, riding the barrels from there
            LatchHand(barrelRider, gunHand: false);
            ForeEndHoldWorld(data, barrelHandOnRightArm, out Vector3 palmPosition, out Quaternion palmRotation);
            SnapHandTo(barrelRider, palmPosition, palmRotation);
            Pulse(SoundHand.OffHand, 0.3f, 0.03f);
            if (breakDebugLog)
                Plugin.MyLog.LogInfo($"[VRBreak] fore-end taken, {state.open:0.#} deg open");
        }

        // The clip's palm on the fore-end, world space; the right arm's its mirror image through the gun's centre plane
        private static void ForeEndHoldWorld(BreakData data, bool rightArm, out Vector3 position, out Quaternion rotation)
        {
            Vector3 local = data.foreEnd.position;
            Quaternion localRotation = data.foreEnd.rotation;
            if (rightArm)
            {
                local.x = -local.x;
                if (TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(localRotation, Vector3.right, rightToLeft: false, out Quaternion mirrored))
                    localRotation = mirrored;
            }
            position = barrelBone.position + barrelBone.rotation * local;
            rotation = barrelBone.rotation * localRotation;
        }

        // A point about the hinge, in the barrel's parent frame (turning the gun doesn't count), off the axis
        private static Vector3 AcrossBarrelHinge(BreakData data, Vector3 world)
        {
            Transform parent = barrelBone.parent;
            Vector3 axis = barrelClosedLocal * data.hingeAxis;
            Vector3 local = parent != null ? parent.InverseTransformPoint(world) : world;
            return Vector3.ProjectOnPlane(local - barrelBone.localPosition, axis);
        }

        private static void UpdateBarrelHold(FirearmController firearmController, BreakData data, BreakState state, bool gripPressed, bool gripReleased, bool leverHeld,
            float deltaTime)
        {
            // "Toggle Gun Grip": a press lets go, as it does the foregrip
            bool toggle = VRSettings.GetGunGripToggle();
            bool letGo = toggle ? gripPressed : gripReleased;
            if (firearmController != barrelsHeldFor || barrelHandOnRightArm != OffHandOnRightArm || ChestHang.Detached || VRGlobals.menuOpen)
            {
                LetGoOfBarrels(keepSupport: false);
                return;
            }
            Vector3 across = AcrossBarrelHinge(data, HandRig(gunHand: false).position);
            float open = state.open;
            if (across.sqrMagnitude > 1e-6f && barrelAcrossAtGrab.sqrMagnitude > 1e-6f)
                open = barrelOpenAtGrab + Vector3.SignedAngle(barrelAcrossAtGrab, across, barrelClosedLocal * data.hingeAxis);
            float before = state.open;
            float target = Mathf.Clamp(open, 0f, data.openDegrees);
            // Carried on into the let-go (a push up and away shuts them)
            state.speed = Mathf.Lerp(state.speed, (target - before) / deltaTime, 1f - Mathf.Exp(-deltaTime / 0.03f));
            if (open <= 0f && !leverHeld && !letGo)
            {
                // Home with the lever let go: latched, the hand stays on as the support grip
                state.open = 0f;
                Latch(firearmController, state, SoundHand.OffHand);
                LetGoOfBarrels(keepSupport: toggle || Handedness.OffHandGrip.state);
                return;
            }
            state.open = target;
            if (state.open >= data.openDegrees && before < data.openDegrees && state.speed > barrelStopSound)
                PlayFirstGunSound(firearmController, SoundHand.OffHand, "BarrelOpen");
            if (letGo)
                LetGoOfBarrels(keepSupport: false);
        }

        private static void LetGoOfBarrels(bool keepSupport)
        {
            if (!HoldingBarrels)
                return;
            HoldingBarrels = false;
            barrelsHeldFor = null;
            ReleaseHandLatch();
            // Two-handing from here (VRPlayerManager's own entry, handLock: opening the grip lets go); the releasing latch hands the
            // arm to Tarkov's IK once it sees it (UpdateHandLatch)
            if (keepSupport && VRGlobals.vrPlayer != null && !Handedness.GunInLeftHand)
                VRGlobals.vrPlayer.TakeSupportGrip();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // A shell drawn out of a chamber

        private static FirearmController caseFor;
        private static int caseChamber;
        private static bool caseSpent;                      // a fired case (Tarkov's shell model), else the live round's model
        private static AmmoTemplate caseTemplate;           // the fired case's, for Weapon.ShellsInChambers if it goes back
        private static Ammo caseRound;                      // the live round: still in its chamber for the inventory
        private static GameObject caseProp;
        private static Transform caseRestParent;
        private static Vector3 caseRestLocalPosition;
        private static Quaternion caseRestLocalRotation;
        private static Vector3 caseRestInBarrelPosition;    // where it sits in its chamber, in the barrel bone's frame
        private static Quaternion caseRestInBarrelRotation;
        private static Vector3 caseBoreInBarrel;            // out of the chamber
        private static float caseLength;
        private static float caseOut;
        private static bool caseFree;
        private static float caseFreeTime;
        private static Vector3 caseHandAtGrab;              // the controller at the grab, in the barrel bone's frame
        private static Vector3 caseInPalmPosition;          // as it came free, in the palm's frame
        private static Quaternion caseInPalmRotation;
        private static Transform caseRider;
        private static bool caseOnRightArm;

        private static bool TryPluckCase(FirearmController firearmController, BreakData data, BreakState state)
        {
            if (state.open < data.openDegrees * barrelOpenEnough)
                return false;
            Weapon weapon = firearmController.Item;
            Firearms firearms = firearmController.Firearms;
            if (firearms == null)
                return false;
            Vector3 palm = OffHandPalm();
            int best = -1;
            float bestDistance = caseGrabRadius * caseGrabRadius;
            bool bestSpent = false;
            GameObject bestModel = null;
            for (int chamber = 0; chamber < weapon.Chambers.Length; chamber++)
            {
                GameObject model = ChamberModel(firearms, weapon, chamber, out bool spent);
                if (model == null)
                    continue;
                float distance = (model.transform.position - palm).sqrMagnitude;
                if (distance <= bestDistance)
                {
                    best = chamber;
                    bestDistance = distance;
                    bestSpent = spent;
                    bestModel = model;
                }
            }
            if (best < 0)
                return false;
            // Off Tarkov's books for the model: ours until it goes back or out
            if (bestSpent)
            {
                caseTemplate = weapon.ShellsInChambers != null && best < weapon.ShellsInChambers.Length ? weapon.ShellsInChambers[best] : null;
                firearms._shellsInShellPort[best] = null;
                caseRound = null;
            }
            else
            {
                firearms._ammoObjectInWeapon[best] = null;
                caseRound = weapon.Chambers[best].ContainedItem as Ammo;
                caseTemplate = null;
            }
            caseFor = firearmController;
            caseChamber = best;
            caseSpent = bestSpent;
            caseProp = bestModel;
            caseRestParent = bestModel.transform.parent;
            caseRestLocalPosition = bestModel.transform.localPosition;
            caseRestLocalRotation = bestModel.transform.localRotation;
            caseRestInBarrelPosition = barrelBone.InverseTransformPoint(bestModel.transform.position);
            caseRestInBarrelRotation = Quaternion.Inverse(barrelBone.rotation) * bestModel.transform.rotation;
            Transform chamberBone = ChamberBone(firearms, best);
            caseBoreInBarrel = Quaternion.Inverse(barrelBone.rotation) * (chamberBone != null ? chamberBone.up : barrelBone.up);
            MeasureLongAxis(bestModel, out _, out _, out caseLength);
            caseLength = Mathf.Clamp(caseLength, 0.03f, 0.12f);
            caseOut = 0f;
            caseFree = false;
            caseOnRightArm = OffHandOnRightArm;
            Transform controller = HandRig(gunHand: false);
            caseHandAtGrab = barrelBone.InverseTransformPoint(controller.position);
            bestModel.transform.SetParent(barrelBone, worldPositionStays: true);
            // The hand rides the shell along the bore from where it took it
            if (caseRider == null)
                caseRider = new GameObject("VRCaseHand").transform;
            caseRider.SetParent(bestModel.transform, worldPositionStays: false);
            caseRider.SetPositionAndRotation(controller.position, controller.rotation);
            LatchHand(caseRider, gunHand: false);
            SetLatchPivot(bestModel.transform.position);
            HoldingBreakCase = true;
            PlayFirstGunSound(firearmController, SoundHand.OffHand, "UnloadOne", "ShellOut");
            Pulse(SoundHand.OffHand, 0.3f, 0.03f);
            if (breakDebugLog)
                Plugin.MyLog.LogInfo($"[VRBreak] took the {(bestSpent ? "fired case" : "live round")} in chamber {best} ({caseLength * 100f:0.#} cm long)");
            return true;
        }

        // The chamber's shell model: the fired case Tarkov keeps on its shell port, or the live round on the chamber bone
        private static GameObject ChamberModel(Firearms firearms, Weapon weapon, int chamber, out bool spent)
        {
            spent = false;
            if (weapon.Chambers[chamber].ContainedItem is Ammo round && !round.IsUsed)
            {
                AmmoPoolObject live = firearms._ammoObjectInWeapon != null && chamber < firearms._ammoObjectInWeapon.Length ? firearms._ammoObjectInWeapon[chamber] : null;
                return live != null ? live.gameObject : null;
            }
            AmmoPoolObject fired = firearms._shellsInShellPort != null && chamber < firearms._shellsInShellPort.Length ? firearms._shellsInShellPort[chamber] : null;
            spent = fired != null;
            return fired != null ? fired.gameObject : null;
        }

        private static Transform ChamberBone(Firearms firearms, int chamber)
        {
            Transform[] bones = firearms._patronInWeaponTransforms;
            return bones != null && chamber < bones.Length ? bones[chamber] : null;
        }

        // Empty for a new shell: no round in it for the inventory, no fired case to draw out (one Tarkov counts with no model left for it,
        // a re-equip's, can't be drawn: it doesn't block the chamber, and loading it clears it)
        private static bool ChamberEmpty(Firearms firearms, Weapon weapon, int chamber)
        {
            if (weapon.Chambers[chamber].ContainedItem != null)
                return false;
            return firearms._shellsInShellPort == null || chamber >= firearms._shellsInShellPort.Length || firearms._shellsInShellPort[chamber] == null;
        }

        private static void ForgetFiredCase(Weapon weapon, int chamber)
        {
            if (weapon.ShellsInChambers != null && chamber < weapon.ShellsInChambers.Length)
                weapon.ShellsInChambers[chamber] = null;
        }

        private static void UpdateCasePluck(FirearmController firearmController, BreakData data, BreakState state, bool gripReleased)
        {
            if (caseProp == null || caseFor != firearmController || caseOnRightArm != OffHandOnRightArm || VRGlobals.menuOpen)
            {
                EndCasePluck(putBack: true);
                return;
            }
            if (!caseFree)
            {
                Vector3 moved = barrelBone.InverseTransformPoint(HandRig(gunHand: false).position) - caseHandAtGrab;
                caseOut = Mathf.Clamp(Vector3.Dot(moved, caseBoreInBarrel), 0f, caseLength);
                if (gripReleased)
                {
                    EndCasePluck(putBack: true);
                    return;
                }
                if (caseOut >= caseLength * caseFreeShare)
                {
                    // Out of the chamber: in the hand now, easing into the clip's hold
                    caseFree = true;
                    caseFreeTime = Time.time;
                    ReleaseHandLatch();
                    caseProp.transform.SetParent(null, worldPositionStays: true);
                    Transform palm = ArmPalm(caseOnRightArm);
                    if (palm != null)
                        GetPoseIn(palm, caseProp.transform, out caseInPalmPosition, out caseInPalmRotation);
                    Pulse(SoundHand.OffHand, 0.25f, 0.03f);
                    if (breakDebugLog)
                        Plugin.MyLog.LogInfo($"[VRBreak] {(caseSpent ? "fired case" : "live round")} out of chamber {caseChamber}");
                }
                return;
            }
            if (!gripReleased)
                return;
            if (caseSpent)
            {
                GameObject prop = caseProp;
                ClearCase();
                PlayFirstGunSound(firearmController, SoundHand.OffHand, "ShellThrow");
                ThrowJamCase(prop.GetComponent<AmmoPoolObject>(), firearmController.Firearms, HandWorldVelocity(gunHand: false));
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo("[VRBreak] fired case thrown");
                return;
            }
            LetGoOfLiveRound(firearmController, state);
        }

        // A live round let go out of its chamber: into an empty chamber at its mouth, the rig at the chest pouch, else the ground
        private static void LetGoOfLiveRound(FirearmController firearmController, BreakState state)
        {
            Weapon weapon = firearmController.Item;
            Firearms firearms = firearmController.Firearms;
            Ammo round = caseRound;
            GameObject prop = caseProp;
            int from = caseChamber;
            ClearCase();
            if (round == null || prop == null || round.Parent == null || !ReferenceEquals(round.Parent.Container, weapon.Chambers[from]))
            {
                if (prop != null)
                    AssetPoolObject.ReturnToPool(prop);
                return;
            }
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            int into = -1;
            if (state.open >= BreakOpenEnoughDegrees(firearmController))
            {
                float nearest = caseGrabRadius * caseGrabRadius;
                for (int chamber = 0; chamber < weapon.Chambers.Length; chamber++)
                {
                    Transform bone = ChamberBone(firearms, chamber);
                    float distance = bone != null ? (prop.transform.position - bone.position).sqrMagnitude : float.MaxValue;
                    if ((chamber == from || ChamberEmpty(firearms, weapon, chamber)) && distance <= nearest)
                    {
                        into = chamber;
                        nearest = distance;
                    }
                }
            }
            if (into == from)
            {
                // Back where it was: nothing moved for the inventory
                ReturnLiveModel(firearms, from, prop);
                return;
            }
            Vector3 position = prop.transform.position;
            Quaternion rotation = prop.transform.rotation;
            AssetPoolObject.ReturnToPool(prop);
            SoundHand hand = SoundHand.OffHand;
            if (into >= 0)
            {
                var move = ItemManipulator.Move(round, weapon.Chambers[into].CreateItemAddress(), inventoryController, simulate: true);
                if (!move.Failed)
                {
                    RunBreakMove(firearmController, move.Cast<MoveResult, IItemOperationResult>(), "live round into chamber " + into, succeeded =>
                    {
                        if (firearms != null && weapon.Chambers[into].ContainedItem is Ammo moved)
                        {
                            ForgetFiredCase(weapon, into);
                            firearms.SetRoundIntoWeapon(moved, into);
                        }
                        else if (!succeeded && firearms != null)
                            firearms.SetRoundIntoWeapon(round, from);
                        SetBreakAnimatorCounts(firearmController);
                    });
                    PlayFirstGunSound(firearmController, hand, "LoadOne");
                    return;
                }
            }
            if (InChestPouchZone(OffHandPalm()) && TryBuildRoundToRig(round, out var rigMove))
            {
                RunBreakMove(firearmController, rigMove.Cast<MoveResult, IItemOperationResult>(), "live round into the rig", succeeded =>
                {
                    if (!succeeded && firearms != null)
                        firearms.SetRoundIntoWeapon(round, from);
                    SetBreakAnimatorCounts(firearmController);
                });
                PlayFirstGunSound(firearmController, SoundHand.OffHandAlone, "ShellOutRig");
                return;
            }
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld != null && !(gameWorld is HideoutGameWorld) && inventoryController.CanThrow(round))
            {
                pendingDropMagazine = round;
                pendingDropPosition = position;
                pendingDropRotation = rotation;
                pendingDropVelocity = HandWorldVelocity(gunHand: false);
                pendingDropAngularVelocity = null;
                pendingDropDeadline = Time.time + 3f;
                breakMoveInFlight = true;
                inventoryController.ThrowItem(round, false, result =>
                {
                    breakMoveInFlight = false;
                    if (!result.Succeed && firearms != null && weapon.Chambers[from].ContainedItem == round)
                        firearms.SetRoundIntoWeapon(round, from);
                    SetBreakAnimatorCounts(firearmController);
                    if (breakDebugLog)
                        Plugin.MyLog.LogInfo($"[VRBreak] live round {(result.Succeed ? "dropped" : "NOT dropped: " + result.Error)}");
                });
                return;
            }
            // Nowhere for it: back in its chamber
            if (firearms != null)
                firearms.SetRoundIntoWeapon(round, from);
        }

        private static float BreakOpenEnoughDegrees(FirearmController firearmController)
        {
            return TryGetBreakAction(firearmController, out BreakData data) ? data.openDegrees * barrelOpenEnough : 0f;
        }

        private static void ReturnLiveModel(Firearms firearms, int chamber, GameObject prop)
        {
            if (firearms == null || firearms._ammoObjectInWeapon == null || chamber >= firearms._ammoObjectInWeapon.Length || firearms._ammoObjectInWeapon[chamber] != null)
            {
                AssetPoolObject.ReturnToPool(prop);
                return;
            }
            Transform bone = ChamberBone(firearms, chamber);
            if (bone == null)
            {
                AssetPoolObject.ReturnToPool(prop);
                return;
            }
            Firearms.ParentShellToTransform(prop, bone);
            firearms._ammoObjectInWeapon[chamber] = prop.GetComponent<AmmoPoolObject>();
        }

        // Let go before it was out, or the gun / hand changed: back in its chamber as Tarkov had it
        private static void EndCasePluck(bool putBack)
        {
            if (!HoldingBreakCase)
                return;
            GameObject prop = caseProp;
            FirearmController firearmController = caseFor;
            int chamber = caseChamber;
            bool spent = caseSpent;
            AmmoTemplate template = caseTemplate;
            Transform restParent = caseRestParent;
            Vector3 restPosition = caseRestLocalPosition;
            Quaternion restRotation = caseRestLocalRotation;
            ClearCase();
            if (prop == null)
                return;
            Firearms firearms = firearmController != null ? firearmController.Firearms : null;
            if (!putBack || firearms == null || restParent == null)
            {
                AssetPoolObject.ReturnToPool(prop);
                return;
            }
            prop.transform.SetParent(restParent, worldPositionStays: false);
            prop.transform.SetLocalPositionAndRotation(restPosition, restRotation);
            if (spent && firearms._shellsInShellPort != null && chamber < firearms._shellsInShellPort.Length && firearms._shellsInShellPort[chamber] == null)
            {
                firearms._shellsInShellPort[chamber] = prop.GetComponent<AmmoPoolObject>();
                Weapon weapon = firearmController.Item;
                if (weapon != null && weapon.ShellsInChambers != null && chamber < weapon.ShellsInChambers.Length)
                    weapon.ShellsInChambers[chamber] = template;
            }
            else if (!spent && firearms._ammoObjectInWeapon != null && chamber < firearms._ammoObjectInWeapon.Length && firearms._ammoObjectInWeapon[chamber] == null)
                firearms._ammoObjectInWeapon[chamber] = prop.GetComponent<AmmoPoolObject>();
            else
                AssetPoolObject.ReturnToPool(prop);
            SetBreakAnimatorCounts(firearmController);
            if (breakDebugLog)
                Plugin.MyLog.LogInfo($"[VRBreak] {(spent ? "fired case" : "live round")} left in chamber {chamber}");
        }

        // Done holding it: off the hand, and a fired case off the chamber's books for good
        private static void ClearCase()
        {
            if (HoldingBreakCase && !caseFree)
                ReleaseHandLatch();
            if (caseRider != null && caseProp != null && caseRider.IsChildOf(caseProp.transform))
                caseRider.SetParent(null, worldPositionStays: true);
            if (handLatch != null && caseProp != null && handLatch.IsChildOf(caseProp.transform))
                handLatch.SetParent(null, worldPositionStays: true);
            if (caseSpent && caseFor != null && caseFor.Item != null)
            {
                Weapon weapon = caseFor.Item;
                if (weapon.ShellsInChambers != null && caseChamber < weapon.ShellsInChambers.Length)
                    weapon.ShellsInChambers[caseChamber] = null;
                SetBreakAnimatorCounts(caseFor);
            }
            HoldingBreakCase = false;
            caseProp = null;
            caseFor = null;
            caseRound = null;
            caseTemplate = null;
            caseFree = false;
        }

        // The drawn shell: on the bore while it comes out, then in the hand (eased onto the clip's hold, its mirror image on the right arm)
        private static void PlaceCase(BreakData data)
        {
            if (!HoldingBreakCase || caseProp == null || barrelBone == null)
                return;
            if (!caseFree)
            {
                caseProp.transform.SetPositionAndRotation(barrelBone.TransformPoint(caseRestInBarrelPosition + caseBoreInBarrel * caseOut),
                    barrelBone.rotation * caseRestInBarrelRotation);
                return;
            }
            Transform palm = ArmPalm(caseOnRightArm);
            if (palm == null)
                return;
            Vector3 position = caseInPalmPosition;
            Quaternion rotation = caseInPalmRotation;
            if (data.pluckHold != null && BreakShellHoldInPalm(data.pluckHold, caseProp.transform, caseOnRightArm, out Vector3 holdPosition, out Quaternion holdRotation))
            {
                float ease = Mathf.SmoothStep(0f, 1f, (Time.time - caseFreeTime) / Mathf.Max(caseIntoHandTime, 0.001f));
                position = Vector3.Lerp(caseInPalmPosition, holdPosition, ease);
                rotation = Quaternion.Slerp(caseInPalmRotation, holdRotation, ease);
            }
            SetPoseIn(palm, caseProp.transform, position, rotation);
        }

        // A shell held as a clip holds it: its model sits on the shell's bone turned 90 deg about x (Firearms.ParentShellToTransform); the
        // right palm's is the mirror image through a plane along the shell
        private static bool BreakShellHoldInPalm(BreakHold hold, Transform prop, bool rightArm, out Vector3 position, out Quaternion rotation)
        {
            position = hold.position;
            rotation = hold.rotation * Quaternion.Euler(90f, 0f, 0f);
            if (!rightArm)
                return true;
            MeasureLongAxis(prop.gameObject, out Vector3 axisInProp, out Vector3 centerInProp, out _);
            Vector3 axis = axisInProp.sqrMagnitude > 1e-6f ? axisInProp.normalized : Vector3.forward;
            Vector3 normal = Vector3.Cross(axis, Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
            return MirrorHold(normal, Vector3.Scale(centerInProp, prop.lossyScale), leftToRight: true, ref position, ref rotation);
        }

        // Each pass after the bones are posed: the drawn case and a new shell on the bore
        private static void PlaceBreakProps(BreakData data)
        {
            if (HoldingBreakCase && !caseFree)
                PlaceCase(data);
            if (breakShellHeld && breakShellOnBore)
                PlaceBreakShellOnBore();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // A new shell from the chest pouch

        private static bool breakShellHeld;
        private static BreakData breakShellData;
        private static bool breakShellOnBore;
        private static int breakShellChamber;
        private static float breakShellOut;                 // m behind the chamber along its bore
        private static float breakShellOutAtCapture;
        private static Vector3 breakShellHandAtCapture;     // the controller then, in the barrel bone's frame
        private static Vector3 breakShellSeatInBarrel;      // the model seated, in the barrel bone's frame
        private static Quaternion breakShellSeatRotationInBarrel;
        private static Vector3 breakShellBoreInBarrel;
        private static bool breakShellCommitted;
        private static int breakShellLoaded;                // the move: 0 not back yet, 1 done, -1 refused
        private static Transform breakShellRider;
        private static Vector3 breakShellSnapFrom;          // the shell as the hand had it at the capture, in the barrel bone's frame
        private static Quaternion breakShellSnapFromRotation;
        private static float breakShellSnapStart;
        public static float breakShellSnapTime = 0.08f;     // s it eases from the hand onto the bore
        private static bool breakMoveInFlight;              // our move into / out of a chamber is running (LetsSingleRoundIn)

        // A grip at the chest (like TryGrabRound): true when it was this gun's (a shell taken, or none that fits: a double buzz)
        private static bool TryGrabBreakShell(bool gunHand)
        {
            if (gunHand || HoldingBarrels || HoldingBreakCase)
                return false;
            FirearmController firearmController = VRGlobals.firearmController;
            if (!enabled || !singleRoundLoading || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.player == null)
                return false;
            if (!ManualBreakOn(firearmController, out BreakData data) || !EnsureBreakBones(firearmController, data) || !GunIdle(firearmController)
                || HandsInteractionController.HoldingLoot || ChestHang.Detached)
                return false;
            Weapon weapon = firearmController.Item;
            Firearms firearms = firearmController.Firearms;
            if (firearms == null || !breakStates.TryGetValue(weapon, out BreakState state) || state.open < data.openDegrees * barrelOpenEnough)
                return false;
            int empty = -1;
            for (int chamber = 0; chamber < weapon.Chambers.Length && empty < 0; chamber++)
            {
                if (ChamberEmpty(firearms, weapon, chamber))
                    empty = chamber;
            }
            if (empty < 0 || !InChestPouchZone(OffHandPalm()))
                return false;
            Ammo source = FindBreakShell(weapon, empty);
            if (source == null)
            {
                PulseRoundHand(false, 0.2f, 0.03f);
                PulseRoundHand(false, 0.2f, 0.03f, 0.08f);
                return true;
            }
            GameObject prop = CreateProp(source);
            if (prop == null)
                return true;
            heldRoundSource = source;
            heldRoundProp = prop;
            heldRoundFor = firearmController;
            roundInGunHand = false;
            roundOnRightArm = OnRightArm(false);
            roundLoading = false;
            breakShellHeld = true;
            breakShellData = data;
            breakShellOnBore = false;
            breakShellCommitted = false;
            MeasureRound(prop);
            PointRoundAlongBore(firearmController);
            HoldingRound = true;
            PlaceHeldRound();
            PlayFirstGunSound(firearmController, SoundHand.OffHandAlone, "ShellOutRig", "AmmoCatch");
            PulseRoundHand(false, 0.25f, 0.03f);
            if (breakDebugLog)
                Plugin.MyLog.LogInfo($"[VRBreak] took a shell of {source.LocalizedShortName()} ({source.StackObjectsCount} in that stack), chambers {ChambersText(firearmController, weapon)}");
            return true;
        }

        // A loose stack (in a grid) that goes into that chamber, the type already in the gun first
        private static Ammo FindBreakShell(Weapon weapon, int chamber)
        {
            reachableRounds.Clear();
            VRGlobals.player.InventoryController.GetReachableItemsOfTypeNonAlloc(reachableRounds, (Ammo ammo) => ammo.StackObjectsCount > 0 && ammo.Parent is GridItemAddress);
            Item inGun = null;
            foreach (Slot slot in weapon.Chambers)
            {
                if (slot.ContainedItem != null)
                    inGun = slot.ContainedItem;
            }
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Ammo ammo in reachableRounds)
                {
                    if (pass == 0 && (inGun == null || ammo.TemplateId != inGun.TemplateId))
                        continue;
                    if (TryBuildLoadIntoChamber(ammo, weapon, chamber, out _))
                        return ammo;
                }
            }
            return null;
        }

        private static bool TryBuildLoadIntoChamber(Ammo source, Weapon weapon, int chamber, out OperationResult<IItemOperationResult> operation)
        {
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            ItemAddress to = weapon.Chambers[chamber].CreateItemAddress();
            operation = source.StackObjectsCount == 1
                ? ItemManipulator.Move(source, to, inventoryController, simulate: true).Cast<MoveResult, IItemOperationResult>()
                : ItemManipulator.SplitExact(source, 1, to, inventoryController, inventoryController, simulate: true).Cast<SplitResult, IItemOperationResult>();
            return operation.Succeeded;
        }

        private static bool TryBuildRoundToRig(Ammo round, out OperationResult<MoveResult> moveResult)
        {
            moveResult = default;
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            GridItemAddress bestAddress = null;
            int bestGridSize = int.MaxValue;
            foreach (var grid in inventoryController.Inventory.Equipment.GetPrioritizedGridsForUnloadedObject())
            {
                GridItemAddress address = grid.FindLocationForItem(round);
                if (address == null)
                    continue;
                int gridSize = address.Grid.GridWidth * address.Grid.GridHeight;
                if (gridSize < bestGridSize)
                {
                    bestGridSize = gridSize;
                    bestAddress = address;
                }
            }
            if (bestAddress == null)
                return false;
            moveResult = ItemManipulator.Move(round, bestAddress, inventoryController, simulate: true);
            return !moveResult.Failed && moveResult.Value.CanExecute(inventoryController);
        }

        private static void RunBreakMove(FirearmController firearmController, OperationResult<IItemOperationResult> operation, string what, System.Action<bool> done)
        {
            breakMoveInFlight = true;
            VRGlobals.player.InventoryController.TryRunNetworkTransaction(operation, result =>
            {
                breakMoveInFlight = false;
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] {what}: {(result.Succeed ? "done" : "NOT done: " + result.Error)}, chambers {ChambersText(firearmController, firearmController.Item)}");
                done?.Invoke(result.Succeed);
            });
        }

        // From UpdateHeldRound while the held round is a break-action's shell
        private static void UpdateHeldBreakShell(bool gripReleased)
        {
            FirearmController firearmController = heldRoundFor;
            BreakData data = breakShellData;
            bool valid = firearmController != null && firearmController == VRGlobals.firearmController && heldRoundProp != null && data != null
                && ManualBreakOn(firearmController, out _) && barrelBone != null && breakBonesFor == firearmController && roundOnRightArm == OnRightArm(false)
                && !VRGlobals.menuOpen && (breakShellCommitted || HeldRoundIsStillValid());
            if (!valid)
            {
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] the shell's gun or hand changed: {(breakShellCommitted ? "it's in" : "back in the pouch")}");
                EndBreakShell();
                return;
            }
            Weapon weapon = firearmController.Item;
            Firearms firearms = firearmController.Firearms;
            BreakState state = BreakStateOf(weapon);
            if (!breakShellOnBore)
            {
                if (gripReleased)
                {
                    PutRoundBack();
                    PulseRoundHand(false, 0.15f, 0.02f);
                    return;
                }
                if (state.open >= data.openDegrees * barrelOpenEnough && GunIdle(firearmController) && firearms != null)
                    TryPutShellOnBore(firearms, weapon);
                return;
            }
            Vector3 moved = barrelBone.InverseTransformPoint(HandRig(gunHand: false).position) - breakShellHandAtCapture;
            float wanted = breakShellOutAtCapture + Vector3.Dot(moved, breakShellBoreInBarrel);
            if (!breakShellCommitted)
            {
                if (gripReleased)
                {
                    PutRoundBack();
                    PulseRoundHand(false, 0.15f, 0.02f);
                    return;
                }
                // Drawn back off the bore, the barrels swung up past reach, or Tarkov took the gun: back in the hand
                if (wanted > roundLength + breakShellReach + 0.02f || state.open < data.openDegrees * barrelOpenEnough * 0.5f || !GunIdle(firearmController))
                {
                    TakeBreakShellOffBore();
                    return;
                }
                breakShellOut = Mathf.Clamp(wanted, 0f, roundLength + breakShellReach);
                if (breakShellOut <= roundLength * (1f - breakShellCommit))
                    CommitBreakShell(firearmController, weapon);
                return;
            }
            breakShellOut = Mathf.Max(0f, Mathf.Min(breakShellOut - breakShellFinishSpeed * Time.deltaTime, Mathf.Max(wanted, 0f)));
            if (breakShellOut <= 0f && breakShellLoaded != 0)
                FinishBreakShell(firearmController, weapon);
        }

        // Lined up on an empty chamber's bore behind it: onto it
        private static void TryPutShellOnBore(Firearms firearms, Weapon weapon)
        {
            Transform prop = heldRoundProp.transform;
            Vector3 propAxis = prop.rotation * roundAxisInProp;
            for (int chamber = 0; chamber < weapon.Chambers.Length; chamber++)
            {
                Transform bone = ChamberBone(firearms, chamber);
                if (bone == null || !ChamberEmpty(firearms, weapon, chamber))
                    continue;
                // Seated: the model on the chamber bone as Tarkov puts it (Firearms.ParentShellToTransform)
                Quaternion seatedRotation = bone.rotation * Quaternion.Euler(90f, 0f, 0f);
                Vector3 seatedCenter = bone.position + seatedRotation * roundCenterInProp;
                Vector3 bore = bone.up;
                Vector3 off = prop.TransformPoint(roundCenterInProp) - seatedCenter;
                float along = Vector3.Dot(off, bore);
                float lateral = (off - bore * along).magnitude;
                if (along < 0f || along > roundLength + breakShellReach || lateral > breakShellCaptureRadius
                    || Vector3.Angle(propAxis, seatedRotation * roundAxisInProp) > breakShellCaptureAngle)
                    continue;
                breakShellOnBore = true;
                breakShellChamber = chamber;
                breakShellOut = along;
                breakShellOutAtCapture = along;
                breakShellHandAtCapture = barrelBone.InverseTransformPoint(HandRig(gunHand: false).position);
                breakShellSeatInBarrel = barrelBone.InverseTransformPoint(bone.position);
                breakShellSeatRotationInBarrel = Quaternion.Inverse(barrelBone.rotation) * seatedRotation;
                breakShellBoreInBarrel = Quaternion.Inverse(barrelBone.rotation) * bore;
                breakShellSnapFrom = barrelBone.InverseTransformPoint(prop.position);
                breakShellSnapFromRotation = Quaternion.Inverse(barrelBone.rotation) * prop.rotation;
                breakShellSnapStart = Time.time;
                prop.SetParent(barrelBone, worldPositionStays: true);
                PlaceBreakShellOnBore();
                // The hand rides it in, from where it holds it
                Transform controller = HandRig(gunHand: false);
                if (breakShellRider == null)
                    breakShellRider = new GameObject("VRBreakShellHand").transform;
                breakShellRider.SetParent(prop, worldPositionStays: false);
                breakShellRider.SetPositionAndRotation(controller.position, controller.rotation);
                LatchHand(breakShellRider, gunHand: false);
                PulseRoundHand(false, 0.25f, 0.03f);
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] shell onto chamber {chamber}'s bore, {along * 100f:0.#} cm out");
                return;
            }
        }

        private static void PlaceBreakShellOnBore()
        {
            if (heldRoundProp == null || barrelBone == null)
                return;
            float snap = Mathf.SmoothStep(0f, 1f, (Time.time - breakShellSnapStart) / Mathf.Max(breakShellSnapTime, 0.001f));
            Vector3 position = Vector3.Lerp(breakShellSnapFrom, breakShellSeatInBarrel + breakShellBoreInBarrel * breakShellOut, snap);
            Quaternion rotation = Quaternion.Slerp(breakShellSnapFromRotation, breakShellSeatRotationInBarrel, snap);
            heldRoundProp.transform.SetPositionAndRotation(barrelBone.TransformPoint(position), barrelBone.rotation * rotation);
        }

        private static void TakeBreakShellOffBore()
        {
            breakShellOnBore = false;
            ReleaseBreakShellHand();
            if (heldRoundProp != null)
                heldRoundProp.transform.SetParent(null, worldPositionStays: true);
            PulseRoundHand(false, 0.12f, 0.02f);
            if (breakDebugLog)
                Plugin.MyLog.LogInfo("[VRBreak] shell drawn back off the bore");
        }

        private static void CommitBreakShell(FirearmController firearmController, Weapon weapon)
        {
            if (!TryBuildLoadIntoChamber(heldRoundSource, weapon, breakShellChamber, out var operation))
            {
                if (breakDebugLog)
                    Plugin.MyLog.LogInfo($"[VRBreak] the shell won't go in: {operation.Error}");
                TakeBreakShellOffBore();
                PulseRoundHand(false, 0.2f, 0.03f);
                PulseRoundHand(false, 0.2f, 0.03f, 0.08f);
                return;
            }
            breakShellCommitted = true;
            breakShellLoaded = 0;
            roundLoading = true;
            Pulse(SoundHand.OffHand, 0.3f, 0.03f);
            int chamber = breakShellChamber;
            RunBreakMove(firearmController, operation, "shell into chamber " + chamber, succeeded => breakShellLoaded = succeeded ? 1 : -1);
        }

        // Home: Tarkov's round model in the chamber for ours
        private static void FinishBreakShell(FirearmController firearmController, Weapon weapon)
        {
            int chamber = breakShellChamber;
            bool loaded = breakShellLoaded > 0;
            EndBreakShell();
            Firearms firearms = firearmController.Firearms;
            if (!loaded)
            {
                PulseRoundHand(false, 0.2f, 0.03f);
                PulseRoundHand(false, 0.2f, 0.03f, 0.08f);
                return;
            }
            ForgetFiredCase(weapon, chamber);
            if (firearms != null && weapon.Chambers[chamber].ContainedItem is Ammo round)
                firearms.SetRoundIntoWeapon(round, chamber);
            SetBreakAnimatorCounts(firearmController);
            PlayFirstGunSound(firearmController, SoundHand.OffHand, "LoadOne", "ShellIn");
            Pulse(SoundHand.OffHand.Seating, 0.45f, 0.04f);
        }

        private static void ReleaseBreakShellHand()
        {
            if (handLatched && handLatch != null && latchHolder == breakShellRider && breakShellRider != null)
                ReleaseHandLatch();
            if (breakShellRider != null && heldRoundProp != null && breakShellRider.IsChildOf(heldRoundProp.transform))
                breakShellRider.SetParent(null, worldPositionStays: true);
        }

        private static void EndBreakShell()
        {
            ReleaseBreakShellHand();
            DestroyRoundProp();
            EndRoundHold();
        }

        // EndRoundHold's part
        private static void ClearBreakShell()
        {
            if (breakShellHeld)
                ReleaseBreakShellHand();
            breakShellHeld = false;
            breakShellData = null;
            breakShellOnBore = false;
            breakShellCommitted = false;
        }

        // The held shell in the hand (PlaceHeldRound): the load clip's hold, mirrored on the right arm
        private static void PlaceHeldBreakShell()
        {
            if (breakShellOnBore || heldRoundProp == null || breakShellData == null || breakShellData.loadHold == null)
                return;
            Transform palm = ArmPalm(roundOnRightArm);
            if (palm == null)
                return;
            if (BreakShellHoldInPalm(breakShellData.loadHold, heldRoundProp.transform, roundOnRightArm, out Vector3 position, out Quaternion rotation))
                SetPoseIn(palm, heldRoundProp.transform, position, rotation);
        }

        // The fingers holding a break-action's shell / drawn case / the fore-end (pose order, palm first), mirrored on the right arm
        private static readonly Vector3[] breakFingersMirrored = new Vector3[16];

        private static Vector3[] BreakFingers(BreakHold hold, bool rightArm)
        {
            if (hold == null || hold.fingers == null)
                return null;
            return rightArm ? MirroredPose(hold.fingers, rightToLeft: false, breakFingersMirrored) ?? hold.fingers : hold.fingers;
        }

        private static Vector3[] BreakShellFingers(bool rightArm) => BreakFingers(breakShellData?.loadHold, rightArm) ?? MAGAZINE_PULL_FINGERS;

        // OffHandPose: the barrels or a drawn case in the off hand
        private static Vector3[] BreakHandFingers(bool rightArm)
        {
            if (!TryGetBreakAction(VRGlobals.firearmController, out BreakData data))
                return null;
            if (HoldingBreakCase)
                return BreakFingers(data.pluckHold, rightArm) ?? MAGAZINE_PULL_FINGERS;
            return BreakFingers(data.foreEnd, rightArm);
        }

        // ApplyHeldFingerPose / ApplyGunHandFingerPose: the drawn case into the fingers once out
        private static void PlaceBreakCaseInHand(bool rightArm)
        {
            if (HoldingBreakCase && caseFree && caseOnRightArm == rightArm && TryGetBreakAction(caseFor, out BreakData data))
                PlaceCase(data);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Our move into or out of one of the break-action's chambers (LetsSingleRoundIn): no Tarkov chamber operation
        private static bool LetsBreakMoveThrough(FirearmController controller, EFT.InventoryLogic.Operations.IInventoryOperation operation)
        {
            if (!breakMoveInFlight || controller == null || controller != VRGlobals.firearmController || controller.Item == null
                || controller.Item.ReloadMode != Weapon.EReloadMode.OnlyBarrel)
                return false;
            if (operation is EFT.InventoryLogic.Operations.ISubOperation subOperation && subOperation.BaseInventoryOperation != null && TouchesChamber(controller.Item, subOperation.BaseInventoryOperation))
                return true;
            return TouchesChamber(controller.Item, operation);
        }

        private static bool TouchesChamber(Weapon weapon, EFT.InventoryLogic.Operations.IInventoryOperation operation)
        {
            if (!(operation is EFT.InventoryLogic.Operations.IOneItemOperation move) || !(move.Item1 is Ammo))
                return false;
            if (IsChamber(weapon, move.From1) || IsChamber(weapon, move.To1))
                return true;
            return operation is EFT.InventoryLogic.Operations.ITwoItemOperation twoItems && IsChamber(weapon, twoItems.To2);
        }

        private static bool IsChamber(Weapon weapon, ItemAddress address)
        {
            if (address == null)
                return false;
            foreach (Slot chamber in weapon.Chambers)
            {
                if (ReferenceEquals(address.Container, chamber))
                    return true;
            }
            return false;
        }

        private static void SetBreakAnimatorCounts(FirearmController firearmController)
        {
            if (firearmController == null || firearmController.FirearmsAnimator == null || firearmController.Item == null)
                return;
            firearmController.FirearmsAnimator.SetAmmoInChamber(firearmController.Item.ChamberAmmoCount);
            firearmController.FirearmsAnimator.SetShellsInWeapon(firearmController.Item.ShellsInWeaponCount);
        }

        private static string ChambersText(FirearmController firearmController, Weapon weapon)
        {
            Firearms firearms = firearmController != null ? firearmController.Firearms : null;
            var text = new System.Text.StringBuilder();
            for (int chamber = 0; chamber < weapon.Chambers.Length; chamber++)
            {
                if (chamber > 0)
                    text.Append('/');
                Item item = weapon.Chambers[chamber].ContainedItem;
                bool fired = weapon.ShellsInChambers != null && chamber < weapon.ShellsInChambers.Length && weapon.ShellsInChambers[chamber] != null;
                text.Append(item != null ? "live" : fired ? "fired" : "empty");
            }
            return text.ToString();
        }

        // Which bone each chamber's model is on (a fired case on its shell port, a round on its chamber bone)
        private static string ChamberModelsText(Firearms firearms, Weapon weapon)
        {
            if (firearms == null)
                return "none";
            var text = new System.Text.StringBuilder();
            for (int chamber = 0; chamber < weapon.Chambers.Length; chamber++)
            {
                GameObject model = ChamberModel(firearms, weapon, chamber, out bool spent);
                text.Append(chamber > 0 ? ", " : "").Append(model == null ? "-" : (spent ? "case on " : "round on ") + (model.transform.parent != null ? model.transform.parent.name : "nothing"));
            }
            return text.ToString();
        }

        // The gun went away, the hands changed, Tarkov took it: nothing held stays held
        private static void EndBreakHolds()
        {
            if (HoldingBarrels)
                LetGoOfBarrels(keepSupport: false);
            if (HoldingBreakCase)
                EndCasePluck(putBack: true);
        }

        // debugZones: the fore-end (orange, green in reach) and each reachable chamber (blue / green)
        private static GameObject foreEndZoneViz;
        private static readonly GameObject[] chamberZoneViz = new GameObject[2];

        private static void UpdateBreakZones(FirearmController firearmController, BreakData data, BreakState state)
        {
            bool open = !state.latched || state.open > 0f;
            Vector3 grab = barrelBone.TransformPoint(MirroredForArm(data.foreEnd.grabPoint, OffHandOnRightArm));
            bool atForeEnd = (OffHandPalm() - grab).sqrMagnitude <= foreEndGrabRadius * foreEndGrabRadius;
            SetZoneViz(ref foreEndZoneViz, debugZones && open && !HoldingBarrels, grab, foreEndGrabRadius, atForeEnd ? new Color(0f, 1f, 0f, 0.25f) : new Color(1f, 0.6f, 0.1f, 0.25f));
            Firearms firearms = firearmController.Firearms;
            Weapon weapon = firearmController.Item;
            for (int chamber = 0; chamber < chamberZoneViz.Length; chamber++)
            {
                bool show = debugZones && firearms != null && chamber < weapon.Chambers.Length && state.open >= data.openDegrees * barrelOpenEnough && !HoldingBreakCase;
                GameObject model = show ? ChamberModel(firearms, weapon, chamber, out _) : null;
                Transform bone = show ? ChamberBone(firearms, chamber) : null;
                Vector3 at = model != null ? model.transform.position : bone != null ? bone.position : Vector3.zero;
                bool reach = model != null && (OffHandPalm() - at).sqrMagnitude <= caseGrabRadius * caseGrabRadius;
                SetZoneViz(ref chamberZoneViz[chamber], show && bone != null, at, caseGrabRadius, reach ? new Color(0f, 1f, 0f, 0.3f) : new Color(0.2f, 0.5f, 1f, 0.3f));
            }
        }
    }
}
