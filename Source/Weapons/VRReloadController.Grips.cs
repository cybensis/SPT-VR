using System;
using System.Collections.Generic;
using System.IO;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using TarkovVR.Source.Controls;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Where the magazine sits in the off hand, recorded from Tarkov's own reload (WeaponRecon). One recording per animation
    // set and magazine type: Tarkov picks the reload clips by magazine.magAnimationIndex, and a gun that reuses another
    // gun's clips also shares its holds, because the clips carry that gun's name. A type with no recording borrows one
    // from the same animation set, moved along the magazine so the hand stays the same distance from its bottom.
    public static partial class VRReloadController
    {
        public static bool saveReloadGrips = false;      // temporary: reloads with Manual Reload off record any hold that's missing
        public static bool rerecordReloadGrips = false;  // true = replace recordings, except the built in one below

        private const string BUILT_IN_GRIP = "m4a1|0";   // tested in the headset, a recording never replaces it
        private const int HIGHEST_MAGAZINE_TYPE = 15;
        private const int FIRST_THUMB_BONE = 1;          // palm bones in GetComponentsInChildren order: 0 is the palm
        private const int LAST_THUMB_BONE = 3;
        public static float sameHoldDistance = 0.04f;    // m pull and insert holds may differ and still be the same way of holding it
        public static float sameHoldAngle = 15f;         // deg, same
        private static readonly string RELOAD_GRIPS_FILE = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\sptvr\\ReloadGrips.json");
        // Generated from every gun's own clips (Tools/gen_reload_holds), for all that haven't been recorded. Recordings win.
        private static readonly string GENERATED_HOLDS_FILE = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\sptvr\\Assets\\ReloadHolds.json");

        // The magazine's pose in the hand's frame: the left IK marker's while recording, since your hand takes its place
        private sealed class MagazineHold
        {
            public Vector3 position;
            public Vector3 rotation;
            public float magazineLength;
            public Vector3[] fingers;        // null = the M4A1's
            public bool inBoneFrame;         // generated: the pose of the magazine's slot bone (the clips move the bone, not the model)
        }

        private sealed class ReloadGrip
        {
            public MagazineHold pull;        // the magazine in the gun, coming out
            public MagazineHold insert;      // a new magazine, going in
        }

        // Plain numbers in the file: Json.NET can't write a Vector3 (its normalized property loops forever)
        public sealed class SavedHold
        {
            public string recordedFrom;
            public float[] position;
            public float[] rotation;
            public float magazineLength;
            public float[] fingers;
        }

        public sealed class SavedGrip
        {
            public SavedHold pull;
            public SavedHold insert;
        }

        public class SavedHandleHold
        {
            public float[] position;
            public float[] rotation;
            public float[] fingers;
            public float travel;
        }

        public sealed class SavedPalmHold
        {
            public SavedHold newMagazine;
            public SavedHold oldMagazine;
            public float[] fingers;
            public float[] thumb;
        }

        public sealed class SavedSlide : SavedHandleHold
        {
            public string bone;
        }

        public sealed class SavedBoltBone
        {
            public string bone;
            public float ratio;
        }

        public sealed class SavedSideHandle : SavedHandleHold
        {
            public string sliderBone;
            public string baseBone;
            public string knobBone;
            public float sliderLag;
            public float[] foldAxis;
            public float foldAngle;
        }

        public sealed class SavedHandleNotch
        {
            public float[] axis;
            public float angle;
        }

        public sealed class SavedRockIn
        {
            public float[] axis;
            public float angle;
            public float[] thumb;
        }

        public sealed class GeneratedHolds
        {
            public Dictionary<string, SavedGrip> grips;
            public Dictionary<string, SavedHandleHold> handles;
            public Dictionary<string, SavedHandleHold> gunHandHandles;
            public Dictionary<string, List<SavedHandleHold>> handleAlternates;
            public Dictionary<string, List<SavedHandleHold>> gunHandHandleAlternates;
            public Dictionary<string, SavedRockIn> rockIn;
            public Dictionary<string, SavedSlide> slides;
            public List<string> handleMovesWithBolt;
            public Dictionary<string, SavedHandleNotch> handleNotches;
            public Dictionary<string, float[]> thumbCatches;
            public Dictionary<string, float[]> wellAxes;
            public Dictionary<string, SavedPalmHold> palms;
            public Dictionary<string, List<SavedBoltBone>> boltBones;
            public Dictionary<string, SavedSideHandle> sideHandles;
            public Dictionary<string, string> handleBones;
            public Dictionary<string, string> handleCarriers;
            public Dictionary<string, float[]> handleGrabPoints;
            public Dictionary<string, SavedReleasePress> releasePress;
            public Dictionary<string, SavedBolt> bolts;
        }

        // A bolt-action's bolt (gen_bolts.py): the slide and turn bones, how far, the knob, the gun hand's hold on it
        public sealed class SavedBolt
        {
            public string slider;
            public string turner;
            public float[] travel;
            public float distance;
            public float[] turnAxis;
            public float turnDegrees;
            public float[] knob;
            public SavedHandleHold hold;
        }

        public sealed class SavedReleasePress
        {
            public string hand;         // "gun" or "off"
            public int finger;          // 0 thumb .. 4 pinky
            public float[] joints;      // that finger's three joints pressing it, local eulers
        }

        // By animation set: the way the magazine leaves the well, in the well bone's frame, where the mesh can't say
        private static readonly Dictionary<string, Vector3> wellAxes = new Dictionary<string, Vector3>();

        private static readonly Dictionary<string, ReloadGrip> reloadGrips = new Dictionary<string, ReloadGrip>
        {
            // M4A1 with a standard 30 round magazine (3 reloads, identical)
            {
                BUILT_IN_GRIP, new ReloadGrip
                {
                    pull = new MagazineHold { position = new Vector3(-0.186f, -0.033f, 0.115f), rotation = new Vector3(352.6f, 250f, 271.1f), magazineLength = 0.19f },
                    insert = new MagazineHold { position = new Vector3(-0.198f, -0.198f, 0.022f), rotation = new Vector3(337.8f, 265.3f, 180.4f), magazineLength = 0.19f },
                }
            },
        };

        private static Dictionary<string, SavedGrip> savedGrips;
        private static readonly Dictionary<string, string> animationFamilies = new Dictionary<string, string>();

        private static string GripKey(string family, int magazineType)
        {
            return $"{family}|{magazineType}";
        }

        // Where a clip's name says a magazine going in or out, -1 if it doesn't: "_mag_in" on every gun but the Five-seveN, whose
        // clips spell it "_magin" (fiveseven_magin_0; missed, it had no animation set and so no data at all)
        private static int MagazineClipNameEnd(string clipName, bool goingIn)
        {
            int nameEnd = clipName.IndexOf(goingIn ? "_mag_in" : "_mag_out", StringComparison.OrdinalIgnoreCase);
            return nameEnd >= 0 ? nameEnd : clipName.IndexOf(goingIn ? "_magin" : "_magout", StringComparison.OrdinalIgnoreCase);
        }

        // The name the gun's reload clips start with (m4a1_mag_in_0 = m4a1). No magazine clips: its own template id.
        private static string AnimationFamily(FirearmController firearmController)
        {
            string templateId = firearmController.Item.TemplateId.ToString();
            if (animationFamilies.TryGetValue(templateId, out string family))
                return family;

            family = templateId;
            RuntimeAnimatorController animatorController = firearmController.FirearmsAnimator?.Animator?.runtimeAnimatorController;
            if (animatorController != null)
            {
                // Then "_reload_in", the SR-2M's magazine clips (sr2m_reload_in_0), only with no magazine clip at all: on other guns
                // reload_in is a grenade launcher's or sits beside their own mag_in
                for (int pass = 0; pass < 2 && family == templateId; pass++)
                {
                    foreach (AnimationClip clip in animatorController.animationClips)
                    {
                        int nameEnd = clip == null ? -1
                            : pass == 0 ? MagazineClipNameEnd(clip.name, goingIn: true) : clip.name.IndexOf("_reload_in", StringComparison.OrdinalIgnoreCase);
                        if (nameEnd > 0)
                        {
                            family = clip.name.Substring(0, nameEnd).ToLowerInvariant();
                            break;
                        }
                    }
                }
            }
            animationFamilies[templateId] = family;
            return family;
        }

        //-----------------------------------------------------------------------------------------------------------------------------
        // Using them

        private static ReloadGrip FindReloadGrip(FirearmController firearmController, Magazine magazine, GameObject prop, Vector3 wellPointInProp)
        {
            LoadRecordedGrips();
            string family = AnimationFamily(firearmController);
            int magazineType = magazine.magAnimationIndex;
            if (!TryGetMagazineShape(prop, wellPointInProp, out Vector3 lengthAxis, out float length, out _))
                length = 0f;

            ReloadGrip grip = new ReloadGrip
            {
                pull = ResolveHold(family, magazineType, true, prop, lengthAxis, length, out string pullSource),
                insert = ResolveHold(family, magazineType, false, prop, lengthAxis, length, out string insertSource),
            };
            grip.pull = WithInsertThumb(grip.pull, grip.insert);
            LogAction($"[VRReload] {firearmController.Item.LocalizedShortName()} with {magazine.LocalizedShortName()}: animations '{family}', "
                + $"magazine type {magazineType}, length {length * 100f:0.#} cm, pull hold {pullSource}, insert hold {insertSource}");
            return grip.pull != null || grip.insert != null ? grip : null;
        }

        // Animation sets whose clips gave no hold at all, on guns that take another set's magazines: that set's holds, which are where
        // the magazine sits in the hand, not on the gun (MCX .300 BLK: its clips leave the hand marker off the palm; M4 magazines)
        private static readonly Dictionary<string, string> magazineHoldsFrom = new Dictionary<string, string> { { "mcx_4", "m4a1" } };

        private static MagazineHold ResolveHold(string family, int magazineType, bool pull, GameObject prop, Vector3 lengthAxis, float length, out string source)
        {
            MagazineHold hold = ResolveHoldIn(family, magazineType, pull, prop, lengthAxis, length, out source);
            if (hold == null && magazineHoldsFrom.TryGetValue(family, out string otherFamily))
            {
                hold = ResolveHoldIn(otherFamily, magazineType, pull, prop, lengthAxis, length, out source);
                source += $", {otherFamily}'s";
            }
            return hold;
        }

        private static MagazineHold ResolveHoldIn(string family, int magazineType, bool pull, GameObject prop, Vector3 lengthAxis, float length, out string source)
        {
            MagazineHold exact = HoldFor(family, magazineType, pull);
            if (exact != null)
            {
                source = exact.inBoneFrame ? "generated" : "recorded";
                return InModelFrame(exact, prop);
            }
            for (int otherType = 0; otherType <= HIGHEST_MAGAZINE_TYPE; otherType++)
            {
                MagazineHold borrowed = otherType != magazineType ? HoldFor(family, otherType, pull) : null;
                if (borrowed == null)
                    continue;
                source = $"borrowed from type {otherType}";
                return MoveAlongMagazine(InModelFrame(borrowed, prop), lengthAxis, length);
            }
            source = "none";
            return null;
        }

        // The model sits off its slot bone by its ModPlacer (SlotView.InsertItem), per magazine
        private static MagazineHold InModelFrame(MagazineHold hold, GameObject prop)
        {
            if (!hold.inBoneFrame)
                return hold;
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            Vector3 seatedPosition = modPlacer != null ? modPlacer.ModPosition : Vector3.zero;
            Quaternion seatedRotation = modPlacer != null ? Quaternion.Euler(modPlacer.ModRotation) : Quaternion.Euler(90f, 0f, 0f);
            Quaternion boneRotation = Quaternion.Euler(hold.rotation);
            return new MagazineHold
            {
                position = hold.position + boneRotation * seatedPosition,
                rotation = (boneRotation * seatedRotation).eulerAngles,
                magazineLength = hold.magazineLength,
                fingers = hold.fingers,
            };
        }

        // The pull's fingers come from its most closed moment, which only looks at the four fingers: the thumb is wherever it
        // happens to be by then (on a drum, far past the magazine). When the hand holds the magazine about the same way going
        // in, the insert's thumb fits, and that one's taken from a long still run.
        private static MagazineHold WithInsertThumb(MagazineHold pull, MagazineHold insert)
        {
            if (pull?.fingers == null || insert?.fingers == null || pull.fingers.Length <= LAST_THUMB_BONE || insert.fingers.Length <= LAST_THUMB_BONE
                || (pull.position - insert.position).magnitude > sameHoldDistance
                || Quaternion.Angle(Quaternion.Euler(pull.rotation), Quaternion.Euler(insert.rotation)) > sameHoldAngle)
                return pull;

            Vector3[] fingers = (Vector3[])pull.fingers.Clone();
            for (int bone = FIRST_THUMB_BONE; bone <= LAST_THUMB_BONE; bone++)
                fingers[bone] = insert.fingers[bone];
            return new MagazineHold { position = pull.position, rotation = pull.rotation, magazineLength = pull.magazineLength, fingers = fingers };
        }

        private static MagazineHold HoldFor(string family, int magazineType, bool pull)
        {
            if (!reloadGrips.TryGetValue(GripKey(family, magazineType), out ReloadGrip grip))
                return null;
            return pull ? grip.pull : grip.insert;
        }

        // The hold's magazine starts at the well and hangs down the length axis, so a longer one moves up through the hand
        // by the difference, which leaves the hand the same distance from the bottom
        private static MagazineHold MoveAlongMagazine(MagazineHold hold, Vector3 lengthAxis, float length)
        {
            if (length <= 0f || hold.magazineLength <= 0f)
                return hold;
            return new MagazineHold
            {
                position = hold.position - Quaternion.Euler(hold.rotation) * lengthAxis * (length - hold.magazineLength),
                rotation = hold.rotation,
                magazineLength = length,
                fingers = hold.fingers,
            };
        }

        //-----------------------------------------------------------------------------------------------------------------------------
        // Recording them (WeaponRecon, at the end of a vanilla reload)

        internal static float MeasureMagazineLength(Transform magazineView, Vector3 wellPosition)
        {
            return TryGetMagazineShape(magazineView.gameObject, magazineView.InverseTransformPoint(wellPosition), out _, out float length, out _) ? length : 0f;
        }

        internal static bool TrySaveHold(FirearmController firearmController, int magazineType, bool pull, Vector3 position, Vector3 rotation,
            float magazineLength, Quaternion[] fingers, string recordedFrom)
        {
            LoadRecordedGrips();
            string family = AnimationFamily(firearmController);
            string key = GripKey(family, magazineType);
            string holdName = pull ? "pull" : "insert";
            MagazineHold existing = HoldFor(family, magazineType, pull);
            if (key == BUILT_IN_GRIP || (existing != null && !existing.inBoneFrame && !rerecordReloadGrips))
            {
                Plugin.MyLog.LogInfo($"[ReloadGrips] {key} {holdName}: already have it");
                return false;
            }

            float[] fingerNumbers = new float[fingers.Length * 3];
            Vector3[] fingerEulers = new Vector3[fingers.Length];
            for (int i = 0; i < fingers.Length; i++)
            {
                fingerEulers[i] = fingers[i].eulerAngles;
                fingerNumbers[i * 3] = fingerEulers[i].x;
                fingerNumbers[i * 3 + 1] = fingerEulers[i].y;
                fingerNumbers[i * 3 + 2] = fingerEulers[i].z;
            }
            SavedHold saved = new SavedHold
            {
                recordedFrom = recordedFrom,
                position = new[] { position.x, position.y, position.z },
                rotation = new[] { rotation.x, rotation.y, rotation.z },
                magazineLength = magazineLength,
                fingers = fingerNumbers,
            };
            if (!savedGrips.TryGetValue(key, out SavedGrip savedGrip))
                savedGrips[key] = savedGrip = new SavedGrip();
            if (pull)
                savedGrip.pull = saved;
            else
                savedGrip.insert = saved;

            if (!reloadGrips.TryGetValue(key, out ReloadGrip grip))
                reloadGrips[key] = grip = new ReloadGrip();
            MagazineHold hold = new MagazineHold { position = position, rotation = rotation, magazineLength = magazineLength, fingers = fingerEulers };
            if (pull)
                grip.pull = hold;
            else
                grip.insert = hold;

            Plugin.MyLog.LogInfo($"[ReloadGrips] {key} {holdName} recorded from {recordedFrom}, magazine length {magazineLength * 100f:0.#} cm");
            return true;
        }

        // Writes the file and buzzes both controllers, so you know you can move on to the next one
        internal static void FinishSavingHolds()
        {
            try
            {
                File.WriteAllText(RELOAD_GRIPS_FILE, JsonConvert.SerializeObject(savedGrips, Formatting.Indented));
                Plugin.MyLog.LogInfo($"[ReloadGrips] saved, {savedGrips.Count} recordings in the file");
                VRInput.Haptic.Execute(0f, 0.25f, 120f, 0.7f, SteamVR_Input_Sources.LeftHand);
                VRInput.Haptic.Execute(0f, 0.25f, 120f, 0.7f, SteamVR_Input_Sources.RightHand);
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[ReloadGrips] couldn't save: {exception.Message}");
            }
        }

        private static void LoadRecordedGrips()
        {
            if (savedGrips != null)
                return;
            savedGrips = new Dictionary<string, SavedGrip>();
            LoadGeneratedHolds();
            if (!File.Exists(RELOAD_GRIPS_FILE))
                return;
            try
            {
                savedGrips = JsonConvert.DeserializeObject<Dictionary<string, SavedGrip>>(File.ReadAllText(RELOAD_GRIPS_FILE)) ?? savedGrips;
                foreach (KeyValuePair<string, SavedGrip> entry in savedGrips)
                {
                    if (entry.Key == BUILT_IN_GRIP || entry.Value == null)
                        continue;
                    if (!reloadGrips.TryGetValue(entry.Key, out ReloadGrip grip))
                        reloadGrips[entry.Key] = grip = new ReloadGrip();
                    grip.pull = HoldFrom(entry.Value.pull) ?? grip.pull;
                    grip.insert = HoldFrom(entry.Value.insert) ?? grip.insert;
                }
                Plugin.MyLog.LogInfo($"[ReloadGrips] loaded {savedGrips.Count} recordings");
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[ReloadGrips] couldn't read {RELOAD_GRIPS_FILE}: {exception.Message}");
            }
        }

        // Fills in what isn't tested in the headset or recorded
        private static void LoadGeneratedHolds()
        {
            if (!File.Exists(GENERATED_HOLDS_FILE))
            {
                Plugin.MyLog.LogWarning($"[ReloadGrips] {GENERATED_HOLDS_FILE} is missing: only recorded guns have reload holds");
                return;
            }
            try
            {
                GeneratedHolds generated = JsonConvert.DeserializeObject<GeneratedHolds>(File.ReadAllText(GENERATED_HOLDS_FILE));
                foreach (KeyValuePair<string, SavedGrip> entry in generated.grips)
                {
                    if (!reloadGrips.TryGetValue(entry.Key, out ReloadGrip grip))
                        reloadGrips[entry.Key] = grip = new ReloadGrip();
                    grip.pull = grip.pull ?? InBoneFrame(HoldFrom(entry.Value.pull));
                    grip.insert = grip.insert ?? InBoneFrame(HoldFrom(entry.Value.insert));
                }
                foreach (KeyValuePair<string, SavedHandleHold> entry in generated.handles)
                {
                    if (handleHolds.ContainsKey(entry.Key))
                        continue;
                    handleHolds[entry.Key] = HandleHoldFrom(entry.Value);
                }
                if (generated.gunHandHandles != null)
                {
                    foreach (KeyValuePair<string, SavedHandleHold> entry in generated.gunHandHandles)
                        gunHandHandleHolds[entry.Key] = HandleHoldFrom(entry.Value);
                }
                if (generated.slides != null)
                {
                    foreach (KeyValuePair<string, SavedSlide> entry in generated.slides)
                    {
                        slideBones[entry.Key] = entry.Value.bone;
                        handleHolds[entry.Key] = HandleHoldFrom(entry.Value);
                    }
                }
                if (generated.handleMovesWithBolt != null)
                    handleMovesWithBolt.UnionWith(generated.handleMovesWithBolt);
                if (generated.wellAxes != null)
                {
                    foreach (KeyValuePair<string, float[]> entry in generated.wellAxes)
                        wellAxes[entry.Key] = new Vector3(entry.Value[0], entry.Value[1], entry.Value[2]);
                }
                if (generated.thumbCatches != null)
                {
                    foreach (KeyValuePair<string, float[]> entry in generated.thumbCatches)
                        thumbCatches[entry.Key] = FingersFrom(entry.Value);
                }
                if (generated.handleNotches != null)
                {
                    foreach (KeyValuePair<string, SavedHandleNotch> entry in generated.handleNotches)
                        handleNotches[entry.Key] = new HandleNotch { axis = new Vector3(entry.Value.axis[0], entry.Value.axis[1], entry.Value.axis[2]), angle = entry.Value.angle };
                }
                LoadHandleAlternates(generated.handleAlternates, handleAlternates);
                LoadHandleAlternates(generated.gunHandHandleAlternates, gunHandHandleAlternates);
                if (generated.boltBones != null)
                {
                    foreach (KeyValuePair<string, List<SavedBoltBone>> entry in generated.boltBones)
                        boltBonesByFamily[entry.Key] = entry.Value;
                }
                if (generated.releasePress != null)
                {
                    foreach (KeyValuePair<string, SavedReleasePress> entry in generated.releasePress)
                    {
                        SavedReleasePress saved = entry.Value;
                        if (saved.joints == null || saved.joints.Length < 9)
                            continue;
                        Vector3[] joints = new Vector3[3];
                        for (int joint = 0; joint < 3; joint++)
                            joints[joint] = new Vector3(saved.joints[joint * 3], saved.joints[joint * 3 + 1], saved.joints[joint * 3 + 2]);
                        releasePresses[entry.Key] = new ReleasePress { offHand = saved.hand == "off", finger = saved.finger, joints = joints };
                    }
                }
                if (generated.handleBones != null)
                {
                    foreach (KeyValuePair<string, string> entry in generated.handleBones)
                        handleBoneNames[entry.Key] = entry.Value;
                }
                if (generated.handleCarriers != null)
                {
                    foreach (KeyValuePair<string, string> entry in generated.handleCarriers)
                        handleCarrierNames[entry.Key] = entry.Value;
                }
                if (generated.handleGrabPoints != null)
                {
                    foreach (KeyValuePair<string, float[]> entry in generated.handleGrabPoints)
                    {
                        if (entry.Value != null && entry.Value.Length >= 3)
                            handleGrabPoints[entry.Key] = new Vector3(entry.Value[0], entry.Value[1], entry.Value[2]);
                    }
                }
                if (generated.bolts != null)
                {
                    foreach (KeyValuePair<string, SavedBolt> entry in generated.bolts)
                    {
                        SavedBolt saved = entry.Value;
                        if (saved.travel == null || saved.turnAxis == null || saved.knob == null || string.IsNullOrEmpty(saved.slider) || string.IsNullOrEmpty(saved.turner))
                            continue;
                        boltGuns[entry.Key] = new BoltData
                        {
                            slider = saved.slider,
                            turner = saved.turner,
                            travel = new Vector3(saved.travel[0], saved.travel[1], saved.travel[2]),
                            turnAxis = new Vector3(saved.turnAxis[0], saved.turnAxis[1], saved.turnAxis[2]).normalized,
                            turnDegrees = saved.turnDegrees,
                            knob = new Vector3(saved.knob[0], saved.knob[1], saved.knob[2]),
                            hold = saved.hold != null && saved.hold.position != null && saved.hold.rotation != null ? HandleHoldFrom(saved.hold) : null,
                        };
                    }
                }
                if (generated.sideHandles != null)
                {
                    foreach (KeyValuePair<string, SavedSideHandle> entry in generated.sideHandles)
                    {
                        SavedSideHandle saved = entry.Value;
                        sideHandles[entry.Key] = new SideHandle
                        {
                            hold = HandleHoldFrom(saved),
                            sliderBone = saved.sliderBone,
                            baseBone = saved.baseBone,
                            knobBone = saved.knobBone,
                            sliderLag = saved.sliderLag,
                            foldAxis = new Vector3(saved.foldAxis[0], saved.foldAxis[1], saved.foldAxis[2]),
                            foldAngle = saved.foldAngle,
                        };
                    }
                }
                if (generated.rockIn != null)
                {
                    foreach (KeyValuePair<string, SavedRockIn> entry in generated.rockIn)
                    {
                        rockInGuns[entry.Key] = new RockIn
                        {
                            axis = new Vector3(entry.Value.axis[0], entry.Value.axis[1], entry.Value.axis[2]),
                            angle = entry.Value.angle,
                            thumb = entry.Value.thumb != null ? FingersFrom(entry.Value.thumb) : null,
                        };
                    }
                }
                foreach (KeyValuePair<string, SavedPalmHold> entry in generated.palms)
                {
                    if (palmHolds.ContainsKey(entry.Key))
                        continue;
                    palmHolds[entry.Key] = new PalmHold
                    {
                        newMagazine = InBoneFrame(HoldFrom(entry.Value.newMagazine)),
                        oldMagazine = InBoneFrame(HoldFrom(entry.Value.oldMagazine)),
                        fingers = FingersFrom(entry.Value.fingers),
                        thumb = FingersFrom(entry.Value.thumb),
                    };
                }
                Plugin.MyLog.LogInfo($"[ReloadGrips] generated holds: {generated.grips.Count} gun and magazine types, {generated.handles.Count} charging handles, {generated.palms.Count} palming poses");
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[ReloadGrips] couldn't read {GENERATED_HOLDS_FILE}: {exception.Message}");
            }
        }

        private static MagazineHold InBoneFrame(MagazineHold hold)
        {
            if (hold != null)
                hold.inBoneFrame = true;
            return hold;
        }

        private static void LoadHandleAlternates(Dictionary<string, List<SavedHandleHold>> saved, Dictionary<string, List<HandleHold>> holds)
        {
            if (saved == null)
                return;
            foreach (KeyValuePair<string, List<SavedHandleHold>> entry in saved)
            {
                List<HandleHold> others = new List<HandleHold>();
                foreach (SavedHandleHold hold in entry.Value)
                    others.Add(HandleHoldFrom(hold));
                holds[entry.Key] = others;
            }
        }

        private static HandleHold HandleHoldFrom(SavedHandleHold saved)
        {
            return new HandleHold
            {
                position = new Vector3(saved.position[0], saved.position[1], saved.position[2]),
                rotation = new Vector3(saved.rotation[0], saved.rotation[1], saved.rotation[2]),
                fingers = FingersFrom(saved.fingers),
                travel = saved.travel,
            };
        }

        private static Vector3[] FingersFrom(float[] numbers)
        {
            if (numbers == null || numbers.Length < 3)
                return null;
            Vector3[] fingers = new Vector3[numbers.Length / 3];
            for (int i = 0; i < fingers.Length; i++)
                fingers[i] = new Vector3(numbers[i * 3], numbers[i * 3 + 1], numbers[i * 3 + 2]);
            return fingers;
        }

        private static MagazineHold HoldFrom(SavedHold saved)
        {
            if (saved == null || saved.position == null || saved.position.Length != 3 || saved.rotation == null || saved.rotation.Length != 3)
                return null;
            return new MagazineHold
            {
                position = new Vector3(saved.position[0], saved.position[1], saved.position[2]),
                rotation = new Vector3(saved.rotation[0], saved.rotation[1], saved.rotation[2]),
                magazineLength = saved.magazineLength,
                fingers = FingersFrom(saved.fingers),
            };
        }
    }
}
