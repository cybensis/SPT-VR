using System;
using System.Collections.Generic;
using System.IO;
using EFT;
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
        public static bool saveReloadGrips = true;       // temporary: reloads with Manual Reload off record any hold that's missing
        public static bool rerecordReloadGrips = false;  // true = replace recordings, except the built in one below

        private const string BUILT_IN_GRIP = "m4a1|0";   // tested in the headset, a recording never replaces it
        private const int HIGHEST_MAGAZINE_TYPE = 15;
        private const int FIRST_THUMB_BONE = 1;          // palm bones in GetComponentsInChildren order: 0 is the palm
        private const int LAST_THUMB_BONE = 3;
        public static float sameHoldDistance = 0.04f;    // m pull and insert holds may differ and still be the same way of holding it
        public static float sameHoldAngle = 15f;         // deg, same
        private static readonly string RELOAD_GRIPS_FILE = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx\\plugins\\sptvr\\ReloadGrips.json");

        // The magazine's pose in the hand's frame: the left IK marker's while recording, since your hand takes its place
        private sealed class MagazineHold
        {
            public Vector3 position;
            public Vector3 rotation;
            public float magazineLength;
            public Vector3[] fingers;        // null = the M4A1's
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
                foreach (AnimationClip clip in animatorController.animationClips)
                {
                    int nameEnd = clip != null ? clip.name.IndexOf("_mag_in", StringComparison.OrdinalIgnoreCase) : -1;
                    if (nameEnd > 0)
                    {
                        family = clip.name.Substring(0, nameEnd).ToLowerInvariant();
                        break;
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
                pull = ResolveHold(family, magazineType, true, lengthAxis, length, out string pullSource),
                insert = ResolveHold(family, magazineType, false, lengthAxis, length, out string insertSource),
            };
            grip.pull = WithInsertThumb(grip.pull, grip.insert);
            Plugin.MyLog.LogInfo($"[VRReload] {firearmController.Item.LocalizedShortName()} with {magazine.LocalizedShortName()}: animations '{family}', "
                + $"magazine type {magazineType}, length {length * 100f:0.#} cm, pull hold {pullSource}, insert hold {insertSource}");
            return grip.pull != null || grip.insert != null ? grip : null;
        }

        private static MagazineHold ResolveHold(string family, int magazineType, bool pull, Vector3 lengthAxis, float length, out string source)
        {
            MagazineHold exact = HoldFor(family, magazineType, pull);
            if (exact != null)
            {
                source = "recorded";
                return exact;
            }
            for (int otherType = 0; otherType <= HIGHEST_MAGAZINE_TYPE; otherType++)
            {
                MagazineHold borrowed = otherType != magazineType ? HoldFor(family, otherType, pull) : null;
                if (borrowed == null)
                    continue;
                source = $"borrowed from type {otherType}";
                return MoveAlongMagazine(borrowed, lengthAxis, length);
            }
            source = "none";
            return null;
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
            if (key == BUILT_IN_GRIP || (HoldFor(family, magazineType, pull) != null && !rerecordReloadGrips))
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
            if (!File.Exists(RELOAD_GRIPS_FILE))
                return;
            try
            {
                savedGrips = JsonConvert.DeserializeObject<Dictionary<string, SavedGrip>>(File.ReadAllText(RELOAD_GRIPS_FILE)) ?? savedGrips;
                foreach (KeyValuePair<string, SavedGrip> entry in savedGrips)
                {
                    if (entry.Key == BUILT_IN_GRIP || entry.Value == null)
                        continue;
                    reloadGrips[entry.Key] = new ReloadGrip { pull = HoldFrom(entry.Value.pull), insert = HoldFrom(entry.Value.insert) };
                }
                Plugin.MyLog.LogInfo($"[ReloadGrips] loaded {savedGrips.Count} recordings");
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[ReloadGrips] couldn't read {RELOAD_GRIPS_FILE}: {exception.Message}");
            }
        }

        private static MagazineHold HoldFrom(SavedHold saved)
        {
            if (saved == null || saved.position == null || saved.position.Length != 3 || saved.rotation == null || saved.rotation.Length != 3)
                return null;
            Vector3[] fingers = null;
            if (saved.fingers != null && saved.fingers.Length >= 3)
            {
                fingers = new Vector3[saved.fingers.Length / 3];
                for (int i = 0; i < fingers.Length; i++)
                    fingers[i] = new Vector3(saved.fingers[i * 3], saved.fingers[i * 3 + 1], saved.fingers[i * 3 + 2]);
            }
            return new MagazineHold
            {
                position = new Vector3(saved.position[0], saved.position[1], saved.position[2]),
                rotation = new Vector3(saved.rotation[0], saved.rotation[1], saved.rotation[2]),
                magazineLength = saved.magazineLength,
                fingers = fingers,
            };
        }
    }
}
