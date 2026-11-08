using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using RootMotion.FinalIK;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Tube shotguns by hand (Manual Reload): MR-133, M870, M590, KS-23, M3 (pump mode) and the MR-153 / MP-155 (semi-autos).
    //  - Shells come out of the chest pouch one at a time, the free hand's grip (the same split as the bolt-action rounds: the off hand
    //    while the gun hand has the grip, the gun hand while the off hand holds the gun up), held the way Tarkov's reload clip holds one.
    //  - Brought up to the loading port underneath, the shell goes onto the clip's own path into the tube (gen_shotguns.py): the shell and
    //    the hand follow the clip as the hand pushes; past shellCommit it's in (the inventory move then) and finishes on its own. Pulled
    //    away before that it comes back off the path into the hand; let go of the grip and it's back in the pouch.
    //  - With the action open (the pump all the way back, a semi-auto's bolt locked back) and the chamber empty, the ejection port takes it
    //    the same way, along the clip's first-shell drop. A pump gun's close then carries it into the chamber the way that clip does; a
    //    semi-auto's bolt runs forward on its own (ReleaseBolt).
    //  - The pump: two-handing, the support hand pulling back along the gun (the two controllers' distance) moves it, every bone the
    //    clip's stroke moves with it - the left-hand IK marker too, so the hand on the gun rides the pump. Written before Tarkov's IK
    //    reads the markers (Player.LateUpdate prefix, ShotgunPatches). All the way back throws out a chambered shell (Tarkov's rechamber,
    //    which brings the next one up) or feeds the one waiting; closed, that one is chambered. Tarkov still pumps after every shot.
    // Inventory: a shell into an empty chamber goes in the chamber but waits for the action (WaitingForRack, like a round a magazine
    // brought in): through the loading port it's in the tube as far as the player can see until the pump strokes, so it counts against
    // the tube's room. The move itself is the bolt-action rounds' (TryBuildLoadOne, let through by LetsSingleRoundIn).
    public static partial class VRReloadController
    {
        public static bool shotguns = true;                 // false = Tarkov's own shotgun reloads only
        public static bool pumpByHand = true;               // false = the pump is Tarkov's alone (after every shot)
        public static float shellCaptureRadius = 0.06f;     // m from the held shell to where a path starts that puts it on that path
        public static float shellCommit = 0.45f;            // share of the path past which the shell is in and goes the rest on its own
        public static float shellLetGoDistance = 0.1f;      // m the hand can be off the path before a shell not yet in comes back out
        public static float shellFinishSpeed = 0.35f;       // m/s the rest of the way once it's in
        public static float shellFollowSpeed = 1.2f;        // m/s the shell follows the hand along the path at most (no jumps)
        public static float shellSnapTime = 0.08f;          // s the shell eases from the hand onto the path
        // m the support hand comes back before the pump moves (headset round 1: 1.5 cm was "a bit too easy to accidentally pull")
        public static float pumpDeadzone = 0.035f;
        public static float pumpRestFollowTime = 0.4f;      // s the closed pump's rest follows a slow drift of the hands (no creeping open)
        // After a shot the pump is the hand's (his round 1: "shooting automatically does a pump after each shot"): Tarkov's pump after the
        // shot is skipped (BoltActionPatches, like a bolt-action's bolt), the fired shell stays in and the trigger is dead until the pump
        // has been all the way back. false = Tarkov pumps after every shot.
        public static bool pumpAfterShotByHand = true;
        // The support hand rides the pump (round 1: "the hand does not seem to move with the pump"): Tarkov's IK holds it on the foregrip's
        // grip pose, which isn't one of the stroke's bones; while the pump is open the left arm aims at our point on the pump instead
        public static bool pumpHandFollows = true;
        public static float pumpFullBack = 0.92f;           // share of the stroke that counts as all the way back
        // A semi-auto's shell into the tube with the bolt closed on an empty chamber stays in the tube until the bolt handle is racked
        // (WaitingForRack, like a magazine put in on a closed bolt). false = it goes straight into the chamber (before there was a handle).
        public static bool semiShellWaitsForHandle = true;
        public static bool shotgunDebugLog = true;          // grabs, paths, strokes (on for its first headset rounds)

        public static bool HoldingShell => HoldingRound && heldShell != null;
        public static bool ShellInOffHand => HoldingShell && !roundInGunHand;
        public static bool InsertingShell => HoldingShell && shellPath != null;
        public static bool PumpHeld { get; private set; }

        // No firing: a shell on its way in, or the pump not closed
        public static bool ShotgunBlocksFiring
        {
            get
            {
                if (InsertingShell)
                    return true;
                FirearmController firearmController = VRGlobals.firearmController;
                if (firearmController == null || firearmController.Item == null || !pumpStates.TryGetValue(firearmController.Item, out PumpState state) || (state.open <= 0f && !state.spentCase))
                    return false;
                return ManualShotgunOn(firearmController, out ShotgunData data) && PumpMode(firearmController.Item, data);
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The data (ReloadHolds.json "shotguns")

        private sealed class ShellKey
        {
            public Vector3 shellPosition;   // the shell's bone (patron_in_weapon), in the weapon bone's frame
            public Quaternion shellRotation;
            public Vector3 palmPosition;    // the clip's left palm
            public Quaternion palmRotation;
            public Quaternion[] fingers;    // the left hand's, pose order (palm first)
        }

        private sealed class ShellPath
        {
            public ShellKey[] keys;         // evenly spaced along the shell's path
            public float length;
            // The clip's hand lets go of the shell on the way (the port drop: it puts it in, moves off and comes back): the hand keeps
            // holding it as at the start instead
            public bool handRidesShell;
            public Vector3 palmInShellPosition;
            public Quaternion palmInShellRotation;
            // The action closing on it: the shell by how far the action is still open (1 at the drop, 0 shut). Null: it doesn't move.
            public float[] closeOpen;
            public Vector3[] closePositions;
            public Quaternion[] closeRotations;
        }

        private sealed class ShotgunData
        {
            public float pumpTravel;
            public string[] pumpBonePaths;
            public Vector3[][] pumpPositions;   // per bone, its local pose at evenly spaced points of the stroke
            public Quaternion[][] pumpRotations;
            public ShellPath tube;
            public ShellPath port;
            public Vector3 holdPosition;        // the shell's bone in the left palm's frame
            public Quaternion holdRotation;
            public Vector3[] holdFingers;
            public bool HasPump => pumpBonePaths != null && pumpBonePaths.Length > 0 && pumpTravel > 0.01f;
        }

        private static readonly Dictionary<string, ShotgunData> shotgunGuns = new Dictionary<string, ShotgunData>();
        private static readonly Dictionary<string, string> shotgunFamilies = new Dictionary<string, string>();   // by template id

        private static Vector3 Vector3From(float[] numbers, int start) => new Vector3(numbers[start], numbers[start + 1], numbers[start + 2]);

        private static Quaternion QuaternionFrom(float[] numbers, int start)
        {
            Quaternion rotation = new Quaternion(numbers[start], numbers[start + 1], numbers[start + 2], numbers[start + 3]);
            return rotation.normalized;
        }

        private static ShotgunData ShotgunFrom(SavedShotgun saved)
        {
            if (saved == null || saved.hold == null || saved.hold.position == null || saved.hold.rotation == null)
                return null;
            ShotgunData data = new ShotgunData
            {
                tube = ShellPathFrom(saved.tube),
                port = ShellPathFrom(saved.port),
                holdPosition = Vector3From(saved.hold.position, 0),
                holdRotation = QuaternionFrom(saved.hold.rotation, 0),
                holdFingers = FingersFrom(saved.hold.fingers),
            };
            if (saved.pump != null && saved.pump.bones != null && saved.pump.bones.Count > 0)
            {
                int count = saved.pump.bones.Count;
                data.pumpTravel = saved.pump.travel;
                data.pumpBonePaths = new string[count];
                data.pumpPositions = new Vector3[count][];
                data.pumpRotations = new Quaternion[count][];
                for (int bone = 0; bone < count; bone++)
                {
                    SavedPumpBone savedBone = saved.pump.bones[bone];
                    data.pumpBonePaths[bone] = savedBone.path;
                    int keyCount = savedBone.keys?.Count ?? 0;
                    data.pumpPositions[bone] = new Vector3[keyCount];
                    data.pumpRotations[bone] = new Quaternion[keyCount];
                    for (int key = 0; key < keyCount; key++)
                    {
                        data.pumpPositions[bone][key] = Vector3From(savedBone.keys[key], 0);
                        data.pumpRotations[bone][key] = QuaternionFrom(savedBone.keys[key], 3);
                    }
                }
            }
            return data.tube != null || data.port != null ? data : null;
        }

        private static ShellPath ShellPathFrom(SavedShellPath saved)
        {
            if (saved == null || saved.keys == null || saved.keys.Count < 2)
                return null;
            ShellPath path = new ShellPath { keys = new ShellKey[saved.keys.Count], length = Mathf.Max(saved.length, 0.01f) };
            for (int index = 0; index < saved.keys.Count; index++)
            {
                SavedShellKey savedKey = saved.keys[index];
                Vector3[] fingers = FingersFrom(savedKey.fingers);
                ShellKey key = new ShellKey
                {
                    shellPosition = Vector3From(savedKey.shell, 0),
                    shellRotation = QuaternionFrom(savedKey.shell, 3),
                    palmPosition = Vector3From(savedKey.palm, 0),
                    palmRotation = QuaternionFrom(savedKey.palm, 3),
                    fingers = new Quaternion[16],
                };
                for (int joint = 0; joint < 16; joint++)
                    key.fingers[joint] = fingers != null && joint < fingers.Length ? Quaternion.Euler(fingers[joint]) : Quaternion.identity;
                path.keys[index] = key;
            }
            // The palm as it holds the shell at the start, and how far the clip's palm wanders off that over the path
            ShellKey first = path.keys[0];
            path.palmInShellRotation = Quaternion.Inverse(first.shellRotation) * first.palmRotation;
            path.palmInShellPosition = Quaternion.Inverse(first.shellRotation) * (first.palmPosition - first.shellPosition);
            float drift = 0f;
            foreach (ShellKey key in path.keys)
            {
                Vector3 held = key.shellPosition + key.shellRotation * path.palmInShellPosition;
                drift = Mathf.Max(drift, (held - key.palmPosition).magnitude);
            }
            path.handRidesShell = drift > 0.04f;
            if (saved.close != null && saved.close.Count >= 2)
            {
                // Stored from open (1) to shut (0); kept that way
                int count = saved.close.Count;
                path.closeOpen = new float[count];
                path.closePositions = new Vector3[count];
                path.closeRotations = new Quaternion[count];
                for (int index = 0; index < count; index++)
                {
                    path.closeOpen[index] = saved.close[index][0];
                    path.closePositions[index] = Vector3From(saved.close[index], 1);
                    path.closeRotations[index] = QuaternionFrom(saved.close[index], 4);
                }
            }
            return path;
        }

        // The family: the first of the animator's clips named after one we have data for (the MP-155 plays the MR-153's)
        private static bool TryGetShotgun(FirearmController firearmController, out ShotgunData data)
        {
            data = null;
            if (firearmController == null || firearmController.Item == null)
                return false;
            Weapon weapon = firearmController.Item;
            if (weapon.ReloadMode != Weapon.EReloadMode.InternalMagazine || weapon.BoltAction)
                return false;
            LoadRecordedGrips();
            string templateId = weapon.TemplateId.ToString();
            if (!shotgunFamilies.TryGetValue(templateId, out string family))
            {
                RuntimeAnimatorController animatorController = firearmController.FirearmsAnimator?.Animator?.runtimeAnimatorController;
                if (animatorController == null)
                    return false;
                family = null;
                foreach (AnimationClip clip in animatorController.animationClips)
                {
                    if (clip == null)
                        continue;
                    foreach (string known in shotgunGuns.Keys)
                    {
                        if (clip.name.StartsWith(known + "_", System.StringComparison.OrdinalIgnoreCase))
                        {
                            family = known;
                            break;
                        }
                    }
                    if (family != null)
                        break;
                }
                shotgunFamilies[templateId] = family;
                if (shotgunDebugLog)
                    Plugin.MyLog.LogInfo($"[VRShotgun] {weapon.LocalizedShortName()}: clips '{family ?? "none"}'");
                if (shotgunDebugLog && family != null)
                {
                    BaseSoundPlayer soundPlayer = GunSoundPlayerOf(firearmController);
                    if (soundPlayer != null)
                        Plugin.MyLog.LogInfo($"[VRShotgun] {weapon.LocalizedShortName()} sounds: {string.Join(", ", soundPlayer.AdditionalSounds.ConvertAll(sound => sound.EventName))}");
                }
            }
            return family != null && shotgunGuns.TryGetValue(family, out data);
        }

        private static bool ManualShotgunOn(FirearmController firearmController, out ShotgunData data)
        {
            data = null;
            return enabled && shotguns && VRSettings.GetManualReloadOn() && firearmController != null && firearmController._player != null
                && firearmController._player.IsYourPlayer && TryGetShotgun(firearmController, out data);
        }

        // The hands load this shotgun (Manual Reload + its data): the off hand's weapon-part menu isn't needed (GunInteractionController's
        // ManualReloadReplacesMenu; it only knew magazine guns, so a tube shotgun kept the menu - his report "the shotgun still has the
        // weapon interact menu when bringing offhand up to it")
        public static bool HandsLoadShotgun(FirearmController firearmController)
        {
            return ManualShotgunOn(firearmController, out _);
        }

        // Worked as a pump gun now: the M3 switched to semi-auto is a semi-auto
        private static bool PumpMode(Weapon weapon, ShotgunData data)
        {
            return pumpByHand && data != null && data.HasPump && weapon.SelectedFireMode != Weapon.EFireMode.semiauto;
        }

        // The gun's action is its pump right now, whoever works it (pumpByHand or not): the M3's bolt handle is the semi-auto mode's only
        private static bool ShotgunInPumpMode(FirearmController firearmController)
        {
            return TryGetShotgun(firearmController, out ShotgunData data) && data.HasPump && firearmController.Item.SelectedFireMode != Weapon.EFireMode.semiauto;
        }

        // The clip's frame: the weapon bone under Weapon_root_anim
        private static FirearmController shotgunFrameFor;
        private static Transform shotgunFrame;

        private static Transform ShotgunFrame(FirearmController firearmController)
        {
            if (shotgunFrameFor == firearmController && shotgunFrame != null)
                return shotgunFrame;
            shotgunFrameFor = firearmController;
            shotgunFrame = null;
            if (firearmController != null && firearmController.WeaponRoot != null)
            {
                foreach (Transform part in firearmController.WeaponRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (part.name == "weapon" && part.parent != null && part.parent.name == "Weapon_root_anim")
                    {
                        shotgunFrame = part;
                        break;
                    }
                }
            }
            if (shotgunFrame == null && shotgunDebugLog)
                Plugin.MyLog.LogWarning("[VRShotgun] no Weapon_root_anim/weapon bone on this gun");
            return shotgunFrame;
        }

        private static Vector3 FromFrame(Transform frame, Vector3 position) => frame.position + frame.rotation * position;
        private static Vector3 IntoFrame(Transform frame, Vector3 world) => Quaternion.Inverse(frame.rotation) * (world - frame.position);

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The shell in the hand

        private static ShotgunData heldShell;               // the gun's data while the held round is a shell
        private static ShellPath shellPath;                 // the path it's on, null while it's in the hand
        private static bool shellIntoPort;
        private static float shellProgress;
        private static float shellSnap;
        private static bool shellCommitted;                 // in: the inventory move ran
        private static int shellLoaded;                     // that move: 0 not back yet, 1 done, -1 refused
        private static bool shellIntoChamber;
        private static Vector3 shellInRigPosition;          // the shell in the controller hand's frame as it went on the path
        private static Quaternion shellInRigRotation;
        private static Vector3 shellStartPosition;          // and where it was then, in the weapon frame
        private static Quaternion shellStartRotation;
        private static Transform shellHandHolder;           // the latched hand's target, on the path (under the weapon frame)
        private static readonly Quaternion[] shellFingersLeft = new Quaternion[16];
        private static readonly Vector3[] shellFingersShown = new Vector3[16];
        private static readonly Vector3[] shellFingersMirrored = new Vector3[16];
        private static GameObject tubeZoneViz, portZoneViz;

        // Shells can go in now: our shotgun, idle, room in the tube or an open action on an empty chamber
        private static bool CanLoadShells(out FirearmController firearmController, out Weapon weapon, out Magazine magazine, out ShotgunData data)
        {
            firearmController = VRGlobals.firearmController;
            weapon = null;
            magazine = null;
            data = null;
            if (!enabled || !singleRoundLoading || !shotguns || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.player == null)
                return false;
            if (!ManualShotgunOn(firearmController, out data) || firearmController.IsInLauncherMode())
                return false;
            weapon = firearmController.Item;
            if (!weapon.SupportsInternalReload || weapon.MalfState.State != Weapon.EMalfunctionState.None)
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None)
                return false;
            if (VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.heldItem != null)
                return false;
            magazine = weapon.GetCurrentMagazine();
            if (magazine == null || magazine is CylinderMagazine)
                return false;
            return TubeHasRoom(weapon, magazine) || PortTakesShell(firearmController, weapon, data);
        }

        // A shell waiting for the action counts as one in the tube (it went in through the loading port)
        private static bool TubeHasRoom(Weapon weapon, Magazine magazine)
        {
            return magazine.Count + (WaitingForRack(weapon) ? 1 : 0) < magazine.MaxCount;
        }

        private static bool PortTakesShell(FirearmController firearmController, Weapon weapon, ShotgunData data)
        {
            if (data.port == null || weapon.Chambers.Length == 0 || weapon.ChamberAmmoCount > 0)
                return false;
            if (PumpMode(weapon, data))
                return PumpStateOf(weapon).open >= pumpFullBack;
            return firearmController.FirearmsAnimator != null && firearmController.FirearmsAnimator.GetBoltCatch();
        }

        // A grip at the chest, like the rounds (TryGrabRound): true when it was a shell's grip (taken, or none that fits: a double buzz)
        private static bool TryGrabShell(bool gunHand)
        {
            if (!CanLoadShells(out FirearmController firearmController, out Weapon weapon, out Magazine magazine, out ShotgunData data))
                return false;
            if (gunHand ? !ChestHang.SupportOnly || ChestHang.HandInRegrabZone : VRGlobals.vrPlayer.isSupporting || ChestHang.Detached)
                return false;
            if (!InChestPouchZone(HandPalm(gunHand)))
                return false;
            Ammo source = FindRoundToLoad(weapon, magazine);
            if (source == null)
            {
                PulseRoundHand(gunHand, 0.2f, 0.03f);
                PulseRoundHand(gunHand, 0.2f, 0.03f, 0.08f);
                return true;
            }
            GameObject prop = CreateProp(source);
            if (prop == null)
                return true;
            heldRoundSource = source;
            heldRoundProp = prop;
            heldRoundFor = firearmController;
            roundInGunHand = gunHand;
            roundOnRightArm = OnRightArm(gunHand);
            roundLoading = false;
            heldShell = data;
            shellPath = null;
            shellCommitted = false;
            MeasureRound(prop);
            HoldingRound = true;
            ShowShellHoldFingers();
            PlaceHeldRound();
            PlayFirstGunSound(firearmController, gunHand ? SoundHand.GunHandAlone : SoundHand.OffHandAlone, "ShellOutRig", "AmmoCatch");
            PulseRoundHand(gunHand, 0.25f, 0.03f);
            if (shotgunDebugLog)
                Plugin.MyLog.LogInfo($"[VRShotgun] took a shell of {source.LocalizedShortName()} ({source.StackObjectsCount} in that stack) in the {(gunHand ? "gun" : "off")} hand, "
                    + $"tube {magazine.Count}/{magazine.MaxCount} + chamber {weapon.ChamberAmmoCount}{(WaitingForRack(weapon) ? " (waiting for the action)" : "")}, port open {PortTakesShell(firearmController, weapon, data)}");
            return true;
        }

        // From UpdateHeldRound while the round held is a shell
        private static void UpdateHeldShell(bool gripReleased, bool gunHandGripReleased)
        {
            bool released = roundInGunHand ? gunHandGripReleased : gripReleased;
            FirearmController firearmController = heldRoundFor;
            // Once it's in, the stack it came from may be gone (its last shell moved): only the gun and the arm matter then
            bool valid = shellCommitted ? ShellGunStillValid(firearmController) : HeldRoundIsStillValid() && ShellGunStillValid(firearmController);
            if (!valid)
            {
                if (shotgunDebugLog)
                    Plugin.MyLog.LogInfo($"[VRShotgun] the shell's gun or hand changed: {(shellCommitted ? "it's in" : "back in the pouch")}");
                EndShell();
                return;
            }
            if (shellPath == null)
            {
                if (released)
                {
                    PutRoundBack();
                    PulseRoundHand(roundInGunHand, 0.15f, 0.02f);
                    return;
                }
                ShowShellHoldFingers();
                UpdateShellZones(firearmController, seat: true);
                return;
            }
            SetZoneViz(ref tubeZoneViz, false, Vector3.zero, 0f, Color.clear);
            SetZoneViz(ref portZoneViz, false, Vector3.zero, 0f, Color.clear);
            UpdateShellOnPath(firearmController, released);
        }

        private static bool ShellGunStillValid(FirearmController firearmController)
        {
            if (!enabled || !shotguns || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.player == null)
                return false;
            if (heldRoundProp == null || firearmController == null || VRGlobals.firearmController != firearmController || firearmController.Item == null)
                return false;
            // Firing or anything else Tarkov does with the gun meanwhile doesn't take the shell out of the hand (seating it onto a path
            // waits for the gun to be idle)
            return roundOnRightArm == OnRightArm(roundInGunHand) && ShotgunFrame(firearmController) != null;
        }

        private static bool ShotgunIdle(FirearmController firearmController)
        {
            return firearmController.CurrentOperation is FirearmController.Idling && expectedOperation == ExpectedOperation.None
                && firearmController.Item.MalfState.State == Weapon.EMalfunctionState.None;
        }

        // The zones the held shell goes onto a path from (debugZones shows them), and onto it when it's in one
        private static void UpdateShellZones(FirearmController firearmController, bool seat)
        {
            Transform frame = ShotgunFrame(firearmController);
            Weapon weapon = firearmController.Item;
            Magazine magazine = ShotgunIdle(firearmController) ? weapon.GetCurrentMagazine() : null;
            ShotgunData data = heldShell;
            Vector3 shell = heldRoundProp.transform.position;
            bool portOpen = magazine != null && PortTakesShell(firearmController, weapon, data);
            bool tubeRoom = magazine != null && data.tube != null && TubeHasRoom(weapon, magazine);
            Vector3 portStart = portOpen ? FromFrame(frame, data.port.keys[0].shellPosition) : Vector3.zero;
            Vector3 tubeStart = tubeRoom ? FromFrame(frame, data.tube.keys[0].shellPosition) : Vector3.zero;
            bool atPort = portOpen && (shell - portStart).sqrMagnitude <= shellCaptureRadius * shellCaptureRadius;
            bool atTube = tubeRoom && (shell - tubeStart).sqrMagnitude <= shellCaptureRadius * shellCaptureRadius;
            SetZoneViz(ref portZoneViz, debugZones && portOpen, portStart, shellCaptureRadius, atPort ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.6f, 0.1f, 0.3f));
            SetZoneViz(ref tubeZoneViz, debugZones && tubeRoom, tubeStart, shellCaptureRadius, atTube ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.5f, 0.8f, 0.3f));
            if (!seat)
                return;
            if (atPort && (!atTube || (shell - portStart).sqrMagnitude <= (shell - tubeStart).sqrMagnitude))
                BeginShellOnPath(firearmController, frame, data.port, intoPort: true);
            else if (atTube)
                BeginShellOnPath(firearmController, frame, data.tube, intoPort: false);
        }

        private static void BeginShellOnPath(FirearmController firearmController, Transform frame, ShellPath path, bool intoPort)
        {
            Transform rig = HandRig(roundInGunHand);
            Transform prop = heldRoundProp.transform;
            shellInRigPosition = Quaternion.Inverse(rig.rotation) * (prop.position - rig.position);
            shellInRigRotation = Quaternion.Inverse(rig.rotation) * prop.rotation;
            shellStartPosition = IntoFrame(frame, prop.position);
            shellStartRotation = Quaternion.Inverse(frame.rotation) * prop.rotation;
            prop.SetParent(frame, worldPositionStays: true);
            shellPath = path;
            shellIntoPort = intoPort;
            shellCommitted = false;
            shellLoaded = 0;
            shellSnap = 0f;
            shellProgress = ClosestOnShellPath(path, ImpliedShellInFrame(frame, rig), out _);
            if (shellHandHolder == null)
                shellHandHolder = new GameObject("VRShellHand").transform;
            shellHandHolder.SetParent(frame, worldPositionStays: false);
            PlaceShellOnPath(frame);
            LatchHand(shellHandHolder, roundInGunHand);
            SnapHandTo(shellHandHolder, shellHandHolder.position, shellHandHolder.rotation);
            PulseRoundHand(roundInGunHand, 0.25f, 0.03f);
            if (shotgunDebugLog)
                Plugin.MyLog.LogInfo($"[VRShotgun] shell onto the {(intoPort ? "ejection port" : "loading port")} path at {shellProgress:0.00}, {(roundOnRightArm ? "right" : "left")} arm, "
                    + $"{(path.handRidesShell ? "the hand keeps its hold" : "the clip's hand")}");
        }

        // Where the controller would have the shell, in the weapon frame
        private static Vector3 ImpliedShellInFrame(Transform frame, Transform rig)
        {
            return IntoFrame(frame, rig.position + rig.rotation * shellInRigPosition);
        }

        // The share of the path nearest a point (the keys are evenly spaced along it), and how far off it the point is
        private static float ClosestOnShellPath(ShellPath path, Vector3 point, out float distance)
        {
            ShellKey[] keys = path.keys;
            float best = 0f;
            distance = float.MaxValue;
            for (int index = 0; index < keys.Length - 1; index++)
            {
                Vector3 from = keys[index].shellPosition;
                Vector3 segment = keys[index + 1].shellPosition - from;
                float along = segment.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector3.Dot(point - from, segment) / segment.sqrMagnitude) : 0f;
                float off = (from + segment * along - point).magnitude;
                if (off < distance)
                {
                    distance = off;
                    best = (index + along) / (keys.Length - 1);
                }
            }
            return best;
        }

        private static void UpdateShellOnPath(FirearmController firearmController, bool released)
        {
            Transform frame = ShotgunFrame(firearmController);
            Transform rig = HandRig(roundInGunHand);
            float target = ClosestOnShellPath(shellPath, ImpliedShellInFrame(frame, rig), out float offPath);
            float step = Time.deltaTime / shellPath.length;
            if (!shellCommitted)
            {
                if (released)
                {
                    PutRoundBack();
                    PulseRoundHand(roundInGunHand, 0.15f, 0.02f);
                    return;
                }
                if ((offPath > shellLetGoDistance && shellProgress < shellCommit) || !ShotgunIdle(firearmController))
                {
                    TakeShellOffPath();
                    return;
                }
                shellProgress = Mathf.MoveTowards(shellProgress, target, step * shellFollowSpeed);
                if (shellProgress >= shellCommit && !CommitShell(firearmController))
                    return;
            }
            else
                shellProgress = Mathf.Min(1f, Mathf.Max(shellProgress + step * shellFinishSpeed, Mathf.Min(target, shellProgress + step * shellFollowSpeed)));
            shellSnap = Mathf.MoveTowards(shellSnap, 1f, Time.deltaTime / Mathf.Max(shellSnapTime, 0.001f));
            PlaceShellOnPath(frame);
            if (shellProgress >= 1f && shellLoaded != 0)
                FinishShell(firearmController);
        }

        // Back into the hand: it hadn't gone in
        private static void TakeShellOffPath()
        {
            if (heldRoundProp != null)
                heldRoundProp.transform.SetParent(null, worldPositionStays: true);
            shellPath = null;
            ReleaseShellHand();
            ShowShellHoldFingers();
            PulseRoundHand(roundInGunHand, 0.12f, 0.02f);
            if (shotgunDebugLog)
                Plugin.MyLog.LogInfo("[VRShotgun] shell pulled back off the path");
        }

        // Past the commit point: the inventory move (the bolt-action rounds' one: into an empty chamber, else onto the tube's top)
        private static bool CommitShell(FirearmController firearmController)
        {
            Weapon weapon = firearmController.Item;
            Magazine magazine = weapon.GetCurrentMagazine();
            if (magazine == null || !TryBuildLoadOne(heldRoundSource, weapon, magazine, out var operation, out bool intoChamber))
            {
                if (shotgunDebugLog)
                    Plugin.MyLog.LogInfo("[VRShotgun] the shell won't go in");
                bool gunHand = roundInGunHand;
                TakeShellOffPath();
                PulseRoundHand(gunHand, 0.2f, 0.03f);
                PulseRoundHand(gunHand, 0.2f, 0.03f, 0.08f);
                return false;
            }
            shellCommitted = true;
            shellIntoChamber = intoChamber;
            roundLoading = true;
            Pulse(roundInGunHand ? SoundHand.GunHand : SoundHand.OffHand, 0.3f, 0.03f);
            VRGlobals.player.InventoryController.TryRunNetworkTransaction(operation, result =>
            {
                shellLoaded = result.Succeed ? 1 : -1;
                if (result.Succeed)
                {
                    // In the chamber for Tarkov, but it waits for the action (a pump stroke; a semi-auto's bolt at the finish)
                    if (intoChamber)
                        weaponsWaitingForRack.Add(weapon);
                    if (firearmController.FirearmsAnimator != null)
                    {
                        firearmController.FirearmsAnimator.SetAmmoOnMag(magazine.Count);
                        firearmController.FirearmsAnimator.SetAmmoInChamber(weapon.ChamberAmmoCount);
                    }
                }
                if (shotgunDebugLog)
                    Plugin.MyLog.LogInfo($"[VRShotgun] shell {(result.Succeed ? "in" : "NOT in: " + result.Error)} ({(intoChamber ? "chamber, waits for the action" : "tube")}), tube {magazine.Count}/{magazine.MaxCount} + chamber {weapon.ChamberAmmoCount}");
            });
            return true;
        }

        // At the end of its path: in the tube it's gone from sight; dropped in the port it lies there until the action closes on it
        private static void FinishShell(FirearmController firearmController)
        {
            if (shellLoaded < 0)
            {
                bool gunHand = roundInGunHand;
                EndShell();
                PulseRoundHand(gunHand, 0.2f, 0.03f);
                PulseRoundHand(gunHand, 0.2f, 0.03f, 0.08f);
                return;
            }
            Weapon weapon = firearmController.Item;
            SoundHand hand = roundInGunHand ? SoundHand.GunHand : SoundHand.OffHand;
            if (shellIntoPort)
                PlayFirstGunSound(firearmController, hand, "ShellInPort", "ShellInsert", "LoadOne", "ShellIn", "AmmoIn");
            else
                PlayFirstGunSound(firearmController, hand, "ShellIn", "LoadOne", "AmmoIn");
            Pulse(hand.Seating, 0.45f, 0.04f);
            bool pumpGun = PumpMode(weapon, heldShell);
            GameObject prop = heldRoundProp;
            if (shellIntoPort && pumpGun && shellIntoChamber && prop != null)
            {
                // Ours to show until the pump closes on it
                ReturnPortShell();
                portShellProp = prop;
                portShellFor = firearmController;
                portShellPath = shellPath;
                heldRoundProp = null;
            }
            if (!pumpGun && shellIntoChamber)
            {
                // A semi-auto: its bolt locked back runs forward on it (ReleaseBolt strips it). Closed, it's in the tube until the bolt handle
                // is racked (BoltSlamsForward shows it then); a gun with no handle data, simply in.
                bool boltBack = firearmController.FirearmsAnimator != null && firearmController.FirearmsAnimator.GetBoltCatch();
                if (boltBack)
                    ReleaseBolt(firearmController);
                if (boltBack || !semiShellWaitsForHandle || !chargingHandle || !TryGetChargingHandle(firearmController, weapon))
                {
                    BoltStripsRound(weapon);
                    ShowChamberedRound(firearmController);
                }
            }
            if (shotgunDebugLog)
                Plugin.MyLog.LogInfo($"[VRShotgun] shell home ({(shellIntoPort ? "ejection port" : "tube")}), chamber {weapon.ChamberAmmoCount}{(WaitingForRack(weapon) ? " waiting for the action" : "")}");
            EndShell();
        }

        // The hand lets go of the path: eases back onto the controller
        private static void ReleaseShellHand()
        {
            if (handLatched && handLatch != null && latchHolder == shellHandHolder && shellHandHolder != null)
                ReleaseHandLatch();
        }

        // Done with the held shell however it ended (its prop back to Tarkov's pool unless the port kept it)
        private static void EndShell()
        {
            ReleaseShellHand();
            DestroyRoundProp();
            EndRoundHold();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Placing the shell and the hand

        // The held shell before it's on a path: the clip's hold on the left palm, its mirror image on the right
        private static void PlaceHeldShell()
        {
            if (shellPath != null || heldRoundProp == null)
                return;
            Transform palm = ArmPalm(roundOnRightArm);
            if (palm == null && !roundOnRightArm)
                palm = TarkovVR.Patches.Core.VR.InitVRPatches.leftPalm;
            if (palm == null)
                return;
            ShellHoldInPalm(roundOnRightArm, out Vector3 position, out Quaternion rotation);
            SetPoseIn(palm, heldRoundProp.transform, position, rotation);
        }

        // The shell model's pose in an arm's palm frame: the left's from the clip (the model sits on the bone turned 90 deg about x, like
        // a chambered round: Firearms.ParentShellToTransform), the right's its mirror image through a plane along the shell
        private static void ShellHoldInPalm(bool rightArm, out Vector3 position, out Quaternion rotation)
        {
            position = heldShell.holdPosition;
            rotation = heldShell.holdRotation * Quaternion.Euler(90f, 0f, 0f);
            if (!rightArm || heldRoundProp == null)
                return;
            Vector3 axis = roundAxisInProp.sqrMagnitude > 1e-6f ? roundAxisInProp.normalized : Vector3.forward;
            Vector3 normal = Vector3.Cross(axis, Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 point = Vector3.Scale(roundCenterInProp, heldRoundProp.transform.lossyScale);
            MirrorHold(normal, point, leftToRight: true, ref position, ref rotation);
        }

        private static void ShowShellHoldFingers()
        {
            Vector3[] fingers = heldShell != null ? heldShell.holdFingers : null;
            for (int joint = 0; joint < 16; joint++)
                shellFingersLeft[joint] = fingers != null && joint < fingers.Length ? Quaternion.Euler(fingers[joint]) : Quaternion.identity;
        }

        // The finger pose for the hand holding a shell (RoundFingers): left-hand data, mirrored for the right arm
        private static Vector3[] ShellFingers(bool rightArm)
        {
            for (int joint = 0; joint < 16; joint++)
                shellFingersShown[joint] = shellFingersLeft[joint].eulerAngles;
            if (!rightArm)
                return shellFingersShown;
            return MirroredPose(shellFingersShown, rightToLeft: false, shellFingersMirrored) ?? shellFingersShown;
        }

        private static void KeyAt(ShellKey[] keys, float progress, out int index, out float blend)
        {
            float scaled = Mathf.Clamp01(progress) * (keys.Length - 1);
            index = Mathf.Min(Mathf.FloorToInt(scaled), keys.Length - 2);
            blend = scaled - index;
        }

        // The shell at the path's progress (eased on from where the hand had it), the latched hand's target with it
        private static void PlaceShellOnPath(Transform frame)
        {
            if (heldRoundProp == null || shellPath == null)
                return;
            ShellKey[] keys = shellPath.keys;
            KeyAt(keys, shellProgress, out int index, out float blend);
            ShellKey from = keys[index], to = keys[index + 1];
            Vector3 bonePosition = Vector3.Lerp(from.shellPosition, to.shellPosition, blend);
            Quaternion boneRotation = Quaternion.Slerp(from.shellRotation, to.shellRotation, blend);
            float snap = Mathf.SmoothStep(0f, 1f, shellSnap);
            Vector3 propPosition = Vector3.Lerp(shellStartPosition, bonePosition, snap);
            Quaternion propRotation = Quaternion.Slerp(shellStartRotation, boneRotation * Quaternion.Euler(90f, 0f, 0f), snap);
            heldRoundProp.transform.SetPositionAndRotation(FromFrame(frame, propPosition), frame.rotation * propRotation);

            // The hand: the clip's on the left arm (its mirror image through the gun's centre plane on the right), or, where the clip's hand
            // lets go of the shell on the way, held as it took it (the right arm: the shell in its own hold's mirror image)
            Vector3 palmPosition;
            Quaternion palmRotation;
            if (shellPath.handRidesShell)
            {
                if (!roundOnRightArm)
                {
                    palmRotation = boneRotation * shellPath.palmInShellRotation;
                    palmPosition = bonePosition + boneRotation * shellPath.palmInShellPosition;
                    for (int joint = 0; joint < 16; joint++)
                        shellFingersLeft[joint] = keys[0].fingers[joint];
                }
                else
                {
                    ShellHoldInPalm(rightArm: true, out Vector3 holdPosition, out Quaternion holdRotation);
                    Quaternion shellRotation = boneRotation * Quaternion.Euler(90f, 0f, 0f);
                    palmRotation = shellRotation * Quaternion.Inverse(holdRotation);
                    palmPosition = bonePosition - palmRotation * holdPosition;
                    ShowShellHoldFingers();
                }
            }
            else
            {
                palmPosition = Vector3.Lerp(from.palmPosition, to.palmPosition, blend);
                palmRotation = Quaternion.Slerp(from.palmRotation, to.palmRotation, blend);
                for (int joint = 0; joint < 16; joint++)
                    shellFingersLeft[joint] = Quaternion.Slerp(from.fingers[joint], to.fingers[joint], blend);
                if (roundOnRightArm)
                {
                    palmPosition.x = -palmPosition.x;
                    if (TarkovVR.Source.Player.Body.HandMirror.TryMirrorPalm(palmRotation, Vector3.right, rightToLeft: false, out Quaternion mirrored))
                        palmRotation = mirrored;
                }
            }
            if (shellHandHolder != null)
                shellHandHolder.SetPositionAndRotation(FromFrame(frame, palmPosition), frame.rotation * palmRotation);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The shell dropped in the open port: ours to show until the action closes on it

        private static GameObject portShellProp;
        private static FirearmController portShellFor;
        private static ShellPath portShellPath;

        private static void ReturnPortShell()
        {
            if (portShellProp != null)
            {
                portShellProp.transform.SetParent(null, worldPositionStays: true);
                ReturnPropToPool(portShellProp);
            }
            portShellProp = null;
            portShellFor = null;
            portShellPath = null;
        }

        // Where the drop left it, carried by the action as it closes (the clip's close, by how far the pump is still open)
        private static void PlacePortShell(Transform frame, float open)
        {
            if (portShellProp == null || portShellPath == null || frame == null)
                return;
            ShellKey last = portShellPath.keys[portShellPath.keys.Length - 1];
            Vector3 position = last.shellPosition;
            Quaternion rotation = last.shellRotation;
            float[] opens = portShellPath.closeOpen;
            if (opens != null && open < 1f)
            {
                for (int index = 0; index < opens.Length - 1; index++)
                {
                    if (open <= opens[index] && open >= opens[index + 1])
                    {
                        float span = opens[index] - opens[index + 1];
                        float blend = span > 1e-5f ? (opens[index] - open) / span : 0f;
                        position = Vector3.Lerp(portShellPath.closePositions[index], portShellPath.closePositions[index + 1], blend);
                        rotation = Quaternion.Slerp(portShellPath.closeRotations[index], portShellPath.closeRotations[index + 1], blend);
                        break;
                    }
                }
            }
            portShellProp.transform.SetPositionAndRotation(FromFrame(frame, position), frame.rotation * rotation * Quaternion.Euler(90f, 0f, 0f));
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The pump

        // Per gun, so a pump left open stays open across a weapon swap
        private sealed class PumpState
        {
            public float open;              // 0 = closed .. 1 = all the way back
            public bool backThisStroke;     // has been all the way back since it was last closed
            public bool roundShown;         // Tarkov's rechamber already showed the next shell
            public bool spentCase;          // fired, the pump not worked since (pumpAfterShotByHand)
            public bool malfunctionRacked;  // all the way back on a malfunction this stroke: closing it clears it
        }
        private static readonly Dictionary<Weapon, PumpState> pumpStates = new Dictionary<Weapon, PumpState>();
        private static float pumpRestDistance;              // the two controllers' distance with the pump closed
        private static FirearmController pumpBonesFor;
        private static Transform[] pumpBones;

        private static PumpState PumpStateOf(Weapon weapon)
        {
            if (!pumpStates.TryGetValue(weapon, out PumpState state))
            {
                state = new PumpState();
                pumpStates[weapon] = state;
            }
            return state;
        }

        // From Tick: the support hand on the pump moves it, and rides it
        private static void TickShotgun()
        {
            pumpHandWanted = false;
            TickPump();
            // Nothing measuring the pull (the gun hand let go of the grip: the off hand holds the gun alone; Tarkov's rechamber under way)
            // but the pump left open: the hand stays on it (his round 2: "if I pull the pump back, then let go of main hand, my offhand moved
            // back to default foregrip position")
            if (!pumpHandWanted)
                HoldPumpHand();
            if (!pumpHandWanted)
                ReleasePumpHand();
        }

        // From Tick, no hand busy: what the magazine guns' grab block does (CanStartGrab, which a tube shotgun isn't for) - a semi-auto's bolt
        // handle by the trigger (the off hand off the gun, or the gun hand while the off hand holds it up), and a shell stuck in the port of
        // either kind (a pump gun's support hand off the pump for it; a full stroke clears it too)
        private static void TickShotgunGrabs(bool triggerPressed, bool gunHandTriggerPressed)
        {
            if (!CanWorkShotgun(out FirearmController firearmController, out Weapon weapon))
                return;
            if (!ChestHang.Detached)
            {
                bool atJamPort = InJamPortZone(firearmController, weapon, gunHand: false);
                bool inHandleZone = !atJamPort && !VRGlobals.vrPlayer.isSupporting && InChargingHandleZone(firearmController, weapon);
                if (atJamPort || inHandleZone)
                    OffHandAtChargingHandle = true;
                if (triggerPressed && atJamPort)
                    TakeJamCase(firearmController, gunHand: false);
                else if (triggerPressed && inHandleZone)
                    GrabChargingHandle(firearmController, weapon);
            }
            else if (ChestHang.SupportOnly)
            {
                bool gunHandAtJamPort = InJamPortZone(firearmController, weapon, gunHand: true);
                if (gunHandTriggerPressed && gunHandAtJamPort)
                    TakeJamCase(firearmController, gunHand: true);
                else if (gunHandTriggerPressed && InChargingHandleZone(firearmController, weapon, gunHand: true))
                    GrabChargingHandle(firearmController, weapon, gunHand: true);
            }
        }

        // Our tube shotgun, idle, nothing in the hands but the gun
        private static bool CanWorkShotgun(out FirearmController firearmController, out Weapon weapon)
        {
            firearmController = VRGlobals.firearmController;
            weapon = null;
            if (!enabled || !shotguns || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.player == null)
                return false;
            if (!ManualShotgunOn(firearmController, out _) || firearmController.IsInLauncherMode() || firearmController.WeaponRoot == null)
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None)
                return false;
            if (VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.heldItem != null)
                return false;
            weapon = firearmController.Item;
            return true;
        }

        // UpdateZoneViz (debugZones) on a gun with no detachable magazine: a semi-auto shotgun's handle zone; false = none to show
        private static bool ShowShotgunHandleZone()
        {
            if (!debugZones || !CanWorkShotgun(out FirearmController firearmController, out Weapon weapon) || !chargingHandle
                || !TryGetChargingHandle(firearmController, weapon))
                return false;
            UpdateChargingHandleViz(firearmController, weapon, show: true);
            return true;
        }

        private static void HoldPumpHand()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            // A shell in the gun hand doesn't count (his round 3: taking one out with the pump open moved the hand off it); the off hand
            // holding one isn't on the gun (isSupporting)
            if (VRGlobals.vrPlayer == null || !VRGlobals.vrPlayer.isSupporting || VRGlobals.menuOpen || ShellInOffHand)
                return;
            if (!ManualShotgunOn(firearmController, out ShotgunData data) || !PumpMode(firearmController.Item, data)
                || !pumpStates.TryGetValue(firearmController.Item, out PumpState state))
                return;
            DrivePumpHand(firearmController, data, state);
        }

        private static void TickPump()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (!ManualShotgunOn(firearmController, out ShotgunData data) || !VRGlobals.inGame || VRGlobals.vrPlayer == null
                || VRGlobals.vrPlayer.LeftHand == null || VRGlobals.vrPlayer.RightHand == null)
            {
                PumpHeld = false;
                return;
            }
            Weapon weapon = firearmController.Item;
            PumpState state = PumpStateOf(weapon);
            // Tarkov's own reload works the action and leaves it closed
            if (firearmController.IsInReloadOperation() && expectedOperation == ExpectedOperation.None)
            {
                // Its own reload cycles the action too: a shell waiting for ours is chambered
                weaponsWaitingForRack.Remove(weapon);
                ResetPump(state);
                return;
            }
            if (!PumpMode(weapon, data))
            {
                PumpHeld = false;
                if (state.open > 0f)
                    ResetPump(state);
                return;
            }
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None)
            {
                PumpHeld = false;
                return;
            }
            // Tarkov's catch holds it back (the MR-133 and M3 after their last shell): so does ours
            if (!PumpHeld && state.open < 1f && firearmController.FirearmsAnimator != null && firearmController.FirearmsAnimator.GetBoltCatch())
            {
                state.open = 1f;
                state.backThisStroke = true;
                state.roundShown = false;
                pumpBackSounded = true;
                pumpForwardSounded = false;
                pumpPeak = 1f;
            }
            // A malfunction is the pump's to clear (VRReloadController.Malfunctions): a full stroke, back and closed
            bool malfunction = weapon.MalfState.State != Weapon.EMalfunctionState.None;
            bool working = VRGlobals.vrPlayer.isSupporting && !ChestHang.Detached && !VRGlobals.menuOpen && !HoldingRound
                && (!malfunction || MalfunctionClearsByHand(firearmController));
            if (!working)
            {
                PumpHeld = false;
                return;
            }
            float distance = Vector3.Distance(VRGlobals.vrPlayer.LeftHand.transform.position, VRGlobals.vrPlayer.RightHand.transform.position);
            float span = Mathf.Max(data.pumpTravel - pumpDeadzone, 0.01f);
            if (!PumpHeld)
            {
                PumpHeld = true;
                pumpRestDistance = distance + (state.open > 0f ? pumpDeadzone + state.open * span : 0f);
                // A hard slide holds the pump until it breaks free (StuckPull, its tugs counted from this hold on, as a handle's from its
                // grab); one already open is moving
                hardSlideTugsDone = 0;
                hardSlideTugging = false;
                hardSlideFree = weapon.MalfState.State != Weapon.EMalfunctionState.HardSlide || state.open > 0f;
            }
            float pull = pumpRestDistance - distance;
            if (pull < 0f)
            {
                pumpRestDistance = distance;
                pull = 0f;
            }
            else if (state.open <= 0f && pull < pumpDeadzone)
                pumpRestDistance = Mathf.Lerp(pumpRestDistance, distance, 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(pumpRestFollowTime, 0.01f)));
            float back = Mathf.Max(pull - pumpDeadzone, 0f);
            if (malfunction)
                back = StuckPull(firearmController, back, SoundHand.OffHand);
            MovePump(firearmController, state, Mathf.Clamp01(back / span));
            PumpMalfunctionPose(firearmController, state);
            DrivePumpHand(firearmController, data, state);
        }

        // Tarkov's malfunction pose off while the pump is open (it would hold the action where the malfunction left it; its own repair has
        // it off too), back on once the pump is closed with the malfunction still there
        private static void PumpMalfunctionPose(FirearmController firearmController, PumpState state)
        {
            if (firearmController.Item.MalfState.State == Weapon.EMalfunctionState.None)
                return;
            bool poseOff = malfunctionPoseOffWhileHeld && state.open > 0f;
            if (poseOff && malfunctionPoseOffFor != firearmController)
            {
                SetMalfunctionPose(firearmController, 0f);
                malfunctionPoseOffFor = firearmController;
            }
            else if (!poseOff && malfunctionPoseOffFor == firearmController)
            {
                SetMalfunctionPose(firearmController, 1f);
                malfunctionPoseOffFor = null;
            }
        }

        // The stroke sounds are the whole stroke (the M870's pump back 0.34 s, the M3's 0.77): each plays as its stroke starts, once
        private static bool pumpBackSounded, pumpForwardSounded;
        private static float pumpPeak;

        private static void MovePump(FirearmController firearmController, PumpState state, float open)
        {
            float previous = state.open;
            state.open = open;
            if (!pumpBackSounded && previous < 0.1f && open >= 0.1f)
            {
                pumpBackSounded = true;
                pumpForwardSounded = false;
                PlayFirstGunSound(firearmController, SoundHand.OffHand, "PumpBack", "PumpOut", "BoltOut", "SlideOut");
            }
            pumpPeak = Mathf.Max(pumpPeak, open);
            if (!pumpForwardSounded && pumpPeak >= 0.3f && open <= pumpPeak - 0.1f)
            {
                pumpForwardSounded = true;
                PlayFirstGunSound(firearmController, SoundHand.OffHand, "PumpForward", "PumpIn", "BoltIn", "SlideIn");
            }
            if (open <= 0f)
            {
                pumpBackSounded = false;
                pumpForwardSounded = false;
                pumpPeak = 0f;
            }
            if (previous <= 0f && open > 0f)
            {
                Pulse(SoundHand.OffHand, 0.15f, 0.02f);
                if (shotgunDebugLog)
                    Plugin.MyLog.LogInfo($"[VRShotgun] pump opening, chamber {firearmController.Item.ChamberAmmoCount}{(WaitingForRack(firearmController.Item) ? " (waiting)" : "")}");
            }
            if (!state.backThisStroke && open >= pumpFullBack)
                PumpAllTheWayBack(firearmController, state);
            if (previous > 0f && open <= 0f)
                PumpClosed(firearmController, state);
        }

        // All the way back: a live shell in the chamber comes out (Tarkov's rechamber, which brings the next one up); a shell waiting for
        // the action (in through the loading port or dropped in the port) is fed
        private static void PumpAllTheWayBack(FirearmController firearmController, PumpState state)
        {
            Weapon weapon = firearmController.Item;
            state.backThisStroke = true;
            Pulse(SoundHand.OffHand.Seating, 0.5f, 0.04f);
            // A malfunction (misfire, failure to feed or eject, a slide that broke free): cleared as the pump closes (PumpClosed: Tarkov's
            // repair throws the dud or stuck shell out and chambers the next), no rechamber of ours
            if (MalfunctionRackedBack(firearmController))
            {
                state.malfunctionRacked = true;
                state.roundShown = false;
                if (shotgunDebugLog)
                    Plugin.MyLog.LogInfo($"[VRShotgun] pump back on a {weapon.MalfState.State}: cleared as it closes");
                return;
            }
            // The shot's fired shell: out of the port (Tarkov's own flying shell, from where the shot put it). Tarkov counted the next one
            // into the chamber at the shot; it comes up now and is chambered as the pump closes.
            if (state.spentCase)
            {
                state.spentCase = false;
                state.roundShown = false;
                firearmController.Firearms.StartSpawnShell(firearmController._player.Velocity * 0.66f);
                syncShellsThrown++;   // co-op: other players' copies throw theirs now (VRReloadController.Sync)
                if (shotgunDebugLog)
                    Plugin.MyLog.LogInfo($"[VRShotgun] pump back: the fired shell comes out, chamber {weapon.ChamberAmmoCount}");
                return;
            }
            if (weapon.ChamberAmmoCount == 0 || WaitingForRack(weapon))
            {
                state.roundShown = false;
                if (shotgunDebugLog)
                    Plugin.MyLog.LogInfo($"[VRShotgun] pump back: {(weapon.ChamberAmmoCount == 0 ? "nothing in the chamber" : "the waiting shell comes up")}");
                return;
            }
            if (shotgunDebugLog)
                Plugin.MyLog.LogInfo("[VRShotgun] pump back: the chambered shell comes out (rechamber)");
            state.roundShown = true;
            ExpectOperation(ExpectedOperation.Rechamber, firearmController);
            VRGlobals.player.InventoryController.RechamberWeapon(weapon);
            TryForceExpectedOperation();
        }

        // Closed: the shell it fed is chambered, a catch that held it back lets go
        private static void PumpClosed(FirearmController firearmController, PumpState state)
        {
            Weapon weapon = firearmController.Item;
            Pulse(SoundHand.OffHand.Seating, 0.6f, 0.05f);
            if (firearmController.FirearmsAnimator != null && firearmController.FirearmsAnimator.GetBoltCatch())
                firearmController.FirearmsAnimator.SetBoltCatch(false);
            bool clearsMalfunction = state.backThisStroke && state.malfunctionRacked && MalfunctionClearsByHand(firearmController);
            state.malfunctionRacked = false;
            if (clearsMalfunction)
            {
                // The next shell chambered by the repair: shown if it didn't show it
                if (RepairByHand(firearmController) && weapon.ChamberAmmoCount > 0 && firearmController.Firearms != null && !firearmController.Firearms.HasPatronInWeapon())
                    ShowChamberedRound(firearmController);
            }
            else if (state.backThisStroke)
            {
                BoltStripsRound(weapon);
                if (!state.roundShown)
                    ShowChamberedRound(firearmController);
            }
            ReturnPortShell();
            state.backThisStroke = false;
            state.roundShown = false;
            if (shotgunDebugLog)
                Plugin.MyLog.LogInfo($"[VRShotgun] pump closed, chamber {weapon.ChamberAmmoCount}, tube {weapon.GetCurrentMagazine()?.Count}");
        }

        // Tarkov's reload took the action over (and closes it), or the gun left pump mode
        private static void ResetPump(PumpState state)
        {
            PumpHeld = false;
            if (state.open <= 0f && !state.backThisStroke)
                return;
            state.open = 0f;
            state.backThisStroke = false;
            state.roundShown = false;
            state.spentCase = false;
            state.malfunctionRacked = false;
            ReturnPortShell();
        }

        // The shot is out (FireOperation.OnFireEvent, BoltActionPatches): true = the pump after it is the hand's, the caller skips Tarkov's
        // (its fire operation fast-forwarded to idle: no pump animation, no shell thrown, no round shown). Not on a malfunction.
        public static bool PumpShotFired(FirearmController firearmController)
        {
            if (!pumpAfterShotByHand || !ManualShotgunOn(firearmController, out ShotgunData data) || !PumpMode(firearmController.Item, data)
                || firearmController.Item.MalfState.State != Weapon.EMalfunctionState.None)
                return false;
            PumpState state = PumpStateOf(firearmController.Item);
            state.spentCase = true;
            state.backThisStroke = false;
            state.roundShown = false;
            // Its last shell: Tarkov's catch would hold the pump back, which the skipped pump never did - closed, the fired shell in
            if (firearmController.FirearmsAnimator != null && firearmController.FirearmsAnimator.GetBoltCatch())
                firearmController.FirearmsAnimator.SetBoltCatch(false);
            if (shotgunDebugLog)
                Plugin.MyLog.LogInfo($"[VRShotgun] shot: the fired shell stays in until the pump, chamber {firearmController.Item.ChamberAmmoCount}");
            return true;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The support hand on the pump

        private static Transform pumpHandTarget;            // ours, under the weapon frame: where the left arm aims while the pump is open
        private static bool pumpHandDriving;
        private static bool pumpHandWanted;                 // this tick
        private static FirearmController pumpHandOffsetFor;
        private static Vector3 palmInMarkerPosition;        // the rendered palm on the gun's left marker with the pump closed
        private static Quaternion palmInMarkerRotation = Quaternion.identity;

        // From TickShotgun, two-handing a pump gun: closed, where the hand sits on the gun's left marker (Tarkov's IK puts it there, onto the
        // foregrip's grip pose); open, the arm aims there on the marker as the pump has it
        private static void DrivePumpHand(FirearmController firearmController, ShotgunData data, PumpState state)
        {
            if (!pumpHandFollows || Handedness.GunInLeftHand || VRGlobals.ikManager == null || VRGlobals.ikManager.leftArmIk == null)
                return;
            // The marker bone the stroke's keys move (the same one Tarkov's IK reads while two-handing), its keys local to its parent
            Transform frame = ShotgunFrame(firearmController);
            Transform palm = ArmPalm(rightArm: false);
            int markerBone = data.pumpBonePaths != null ? System.Array.IndexOf(data.pumpBonePaths, "weapon_L_IK_marker") : -1;
            if (frame == null || palm == null || markerBone < 0 || !EnsurePumpBones(firearmController, data, frame))
                return;
            Transform marker = pumpBones[markerBone];
            if (marker == null)
                return;
            if (state.open <= 0f)
            {
                // Read off the last frame's solved hand (Update: what it rendered), on the marker as the animator had it
                Quaternion toMarker = Quaternion.Inverse(marker.rotation);
                palmInMarkerPosition = toMarker * (palm.position - marker.position);
                palmInMarkerRotation = toMarker * palm.rotation;
                pumpHandOffsetFor = firearmController;
                return;
            }
            if (pumpHandOffsetFor != firearmController)
            {
                // Taken already open (a catch held it back): on the marker itself until it has been closed once
                palmInMarkerPosition = Vector3.zero;
                palmInMarkerRotation = Quaternion.identity;
                pumpHandOffsetFor = firearmController;
            }
            Vector3[] positions = data.pumpPositions[markerBone];
            Quaternion[] rotations = data.pumpRotations[markerBone];
            if (positions.Length < 2)
                return;
            float scaled = Mathf.Clamp01(state.open) * (positions.Length - 1);
            int index = Mathf.Min(Mathf.FloorToInt(scaled), positions.Length - 2);
            float blend = scaled - index;
            Transform markerParent = marker.parent != null ? marker.parent : frame;
            Vector3 markerPosition = markerParent.TransformPoint(Vector3.Lerp(positions[index], positions[index + 1], blend));
            Quaternion markerRotation = markerParent.rotation * Quaternion.Slerp(rotations[index], rotations[index + 1], blend);
            if (pumpHandTarget == null)
                pumpHandTarget = new GameObject("VRPumpHand").transform;
            if (pumpHandTarget.parent != frame)
                pumpHandTarget.SetParent(frame, worldPositionStays: false);
            pumpHandTarget.SetPositionAndRotation(markerPosition + markerRotation * palmInMarkerPosition, markerRotation * palmInMarkerRotation);
            VRGlobals.ikManager.leftArmIk.solver.target = pumpHandTarget;
            pumpHandDriving = true;
            pumpHandWanted = true;
        }

        // Back to Tarkov's IK (two-handing: no target), or the controller if the hand left the gun meanwhile
        private static void ReleasePumpHand()
        {
            if (!pumpHandDriving)
                return;
            pumpHandDriving = false;
            LimbIK leftArmIk = VRGlobals.ikManager != null ? VRGlobals.ikManager.leftArmIk : null;
            if (leftArmIk == null || leftArmIk.solver.target != pumpHandTarget)
                return;
            bool supporting = VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.isSupporting;
            leftArmIk.solver.target = supporting || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.LeftHand == null ? null : VRGlobals.vrPlayer.LeftHand.transform;
        }

        private static bool EnsurePumpBones(FirearmController firearmController, ShotgunData data, Transform frame)
        {
            if (pumpBonesFor == firearmController && pumpBones != null && pumpBones.Length == data.pumpBonePaths.Length)
                return true;
            pumpBonesFor = firearmController;
            pumpBones = new Transform[data.pumpBonePaths.Length];
            int found = 0;
            for (int bone = 0; bone < pumpBones.Length; bone++)
            {
                pumpBones[bone] = frame.Find(data.pumpBonePaths[bone]);
                if (pumpBones[bone] != null)
                    found++;
            }
            if (shotgunDebugLog)
                Plugin.MyLog.LogInfo($"[VRShotgun] {firearmController.Item.LocalizedShortName()}: pump bones {found}/{pumpBones.Length} ({string.Join(", ", data.pumpBonePaths)})");
            return true;
        }

        // Player.LateUpdate prefix (ShotgunPatches): after the animator, before Tarkov's IK reads the left-hand marker the pump carries
        public static void ApplyShotgunBeforeIk()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (portShellProp != null && (firearmController == null || firearmController != portShellFor || !WaitingForRack(firearmController.Item)))
                ReturnPortShell();
            if (firearmController == null || firearmController.Item == null || !pumpStates.TryGetValue(firearmController.Item, out PumpState state))
                return;
            if (!ManualShotgunOn(firearmController, out ShotgunData data) || !PumpMode(firearmController.Item, data))
                return;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) && expectedOperation != ExpectedOperation.Rechamber)
                return;
            Transform frame = ShotgunFrame(firearmController);
            if (frame == null)
                return;
            PlacePortShell(frame, state.open);
            // Closed it's the animator's; held back by Tarkov's catch with no hand on it, the animator's catch pose shows it open
            bool animatorHoldsIt = state.open >= 1f && !PumpHeld && firearmController.FirearmsAnimator != null && firearmController.FirearmsAnimator.GetBoltCatch();
            if (state.open <= 0f || animatorHoldsIt || !EnsurePumpBones(firearmController, data, frame))
                return;
            int keyCount = data.pumpPositions[0].Length;
            if (keyCount < 2)
                return;
            float scaled = Mathf.Clamp01(state.open) * (keyCount - 1);
            int index = Mathf.Min(Mathf.FloorToInt(scaled), keyCount - 2);
            float blend = scaled - index;
            for (int bone = 0; bone < pumpBones.Length; bone++)
            {
                Transform transform = pumpBones[bone];
                if (transform == null || data.pumpPositions[bone].Length != keyCount)
                    continue;
                transform.localPosition = Vector3.Lerp(data.pumpPositions[bone][index], data.pumpPositions[bone][index + 1], blend);
                transform.localRotation = Quaternion.Slerp(data.pumpRotations[bone][index], data.pumpRotations[bone][index + 1], blend);
            }
        }
    }
}
