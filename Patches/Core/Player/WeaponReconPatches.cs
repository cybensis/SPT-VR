using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using TarkovVR.Patches.Core.VR;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Patches.Core.Player
{
    // TEMPORARY RECON - profiles a VANILLA magazine reload (Manual Reload off, press B) to gather what the hand-driven
    // reload needs to look and sound like Tarkov's: the [FoodRecon] workflow, for guns.
    //
    // The reload animation lives on the gun model: its clip moves the magazine, the left-hand IK marker the arm reaches
    // for (weapon_L_IK_marker), and an animated copy of the left hand (the gun's HumanLPalm) whose fingers Tarkov's
    // HandPoser copies onto your hand. All three are in the gun's own space, so their relations are exact even though
    // the mod moves the gun and keeps your arm on the controller.
    //
    // Per reload, in the BepInEx log:
    //  * [EVENT]    each step of Tarkov's reload (mag out, mag to rig, new mag appears, mag in, chamber) + the clip time
    //  * [SOUND]    every sound event + the clip time; [SOUNDS] lists every sound name the gun can play
    //  * [STATE]    arms layer-1 state changes
    //  * [GRIP-AVG] the magazine in the left IK marker's frame while the hand holds it (old mag and new mag): where the
    //               magazine sits in YOUR hand, since your hand takes the marker's place
    //  * [AXIS]     the direction the magazine leaves / enters the well, in the gun's frame
    //  * [FINGERS]  the gun's left-hand finger pose while holding the new magazine (index-matched to your hand)
    internal static class WeaponRecon
    {
        public static bool enabled = true;              // temporary: on while we capture guns
        public static float stableMaxDistance = 0.35f;  // m: magazine farther than this from the marker = not in the hand
        public static float stablePositionTolerance = 0.01f;    // m frame-to-frame that still counts as held still
        public static float stableAngleTolerance = 6f;          // deg, same
        public static int stableMinimumFrames = 5;
        public static float axisDistance = 0.04f;      // m of travel that defines the in/out direction
        public static float trailingSeconds = 1.5f;    // keep logging sounds/states after the operation ends (the clip plays on)

        private enum ReloadPhase { MagazineInWell, OldMagazineInHand, BetweenMagazines, NewMagazineInHand, Finished }

        // Longest run where the magazine barely moves in a frame (= held still in the hand), averaged
        private sealed class StableRelation
        {
            public string label;
            public bool haveLast;
            public Vector3 lastPosition;
            public Quaternion lastRotation;
            public int runFrames;
            public Vector3 runPositionSum;
            public Vector4 runRotationSum;
            public Quaternion runFirstRotation;
            public int bestFrames;
            public Vector3 bestPosition;
            public Vector3 bestRotation;

            public StableRelation(string label)
            {
                this.label = label;
            }

            // Returns true when this frame extended the best run
            public bool Sample(Transform frame, Transform target)
            {
                Vector3 position = frame.InverseTransformPoint(target.position);
                Quaternion rotation = Quaternion.Inverse(frame.rotation) * target.rotation;
                if (position.magnitude > stableMaxDistance)
                {
                    runFrames = 0;
                    haveLast = false;
                    return false;
                }

                if (haveLast && (position - lastPosition).magnitude <= stablePositionTolerance
                    && Quaternion.Angle(rotation, lastRotation) <= stableAngleTolerance)
                {
                    runFrames++;
                    runPositionSum += position;
                    Vector4 rotationVector = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w);
                    Vector4 firstVector = new Vector4(runFirstRotation.x, runFirstRotation.y, runFirstRotation.z, runFirstRotation.w);
                    if (Vector4.Dot(rotationVector, firstVector) < 0f)
                        rotationVector = -rotationVector;
                    runRotationSum += rotationVector;
                }
                else
                {
                    runFrames = 1;
                    runPositionSum = position;
                    runFirstRotation = rotation;
                    runRotationSum = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w);
                }
                lastPosition = position;
                lastRotation = rotation;
                haveLast = true;

                if (runFrames <= bestFrames)
                    return false;
                bestFrames = runFrames;
                bestPosition = runPositionSum / runFrames;
                Vector4 average = runRotationSum;
                average.Normalize();
                bestRotation = new Quaternion(average.x, average.y, average.z, average.w).eulerAngles;
                return true;
            }

            public string Report()
            {
                if (bestFrames < stableMinimumFrames)
                    return $"[WeaponRecon][GRIP-AVG] {label}: never held still ({bestFrames} frames)";
                return $"[WeaponRecon][GRIP-AVG] {label} ({bestFrames}f): pos new Vector3({bestPosition.x:0.###}f, {bestPosition.y:0.###}f, {bestPosition.z:0.###}f)  "
                    + $"rot new Vector3({bestRotation.x:0.#}f, {bestRotation.y:0.#}f, {bestRotation.z:0.#}f)";
            }
        }

        private static object sessionOperation;
        private static FirearmController sessionFirearm;
        private static ReloadPhase phase;
        private static BaseSoundPlayer weaponSoundPlayer;
        private static Transform leftIkMarker;
        private static Transform gunLeftPalm;
        private static Transform[] gunLeftPalmChildren;
        private static Slot magazineSlot;
        private static int lastLayerOneState;
        private static float sessionEndTime = -1f;

        private static StableRelation oldMagazineInMarker, oldMagazineInGunPalm, newMagazineInMarker, newMagazineInGunPalm;
        private static Quaternion[] oldMagazineFingers, newMagazineFingers;

        private static bool haveRestPosition;
        private static Vector3 magazineRestPosition;
        private static Vector3 outAxis;
        private static bool haveOutAxis;
        private static readonly List<Vector3> newMagazinePath = new List<Vector3>();

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        public static void OnOperationCallback(object operation, string callbackName)
        {
            if (!enabled)
                return;
            try
            {
                if (callbackName == "Start")
                {
                    FirearmController firearmController = VRGlobals.firearmController;
                    if (firearmController == null || !ReferenceEquals(firearmController.CurrentOperation, operation))
                        return;
                    BeginSession(operation, firearmController);
                    return;
                }
                if (!ReferenceEquals(operation, sessionOperation))
                    return;

                Plugin.MyLog.LogInfo($"[WeaponRecon][EVENT] {callbackName}{StateText()}");
                if (callbackName == "OnMagPulledOutFromWeapon" && phase == ReloadPhase.MagazineInWell)
                    phase = ReloadPhase.OldMagazineInHand;
                else if (callbackName == "OnMagPuttedToRig" && phase <= ReloadPhase.OldMagazineInHand)
                    phase = ReloadPhase.BetweenMagazines;
                else if (callbackName == "OnMagAppeared" && phase <= ReloadPhase.BetweenMagazines)
                {
                    phase = ReloadPhase.NewMagazineInHand;
                    LogSoundPlayers("after the new magazine appeared");
                }
                else if (callbackName == "OnMagInsertedToWeapon" && phase <= ReloadPhase.NewMagazineInHand)
                    phase = ReloadPhase.Finished;
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[WeaponRecon] callback {callbackName}: {exception}");
            }
        }

        public static void OnSound(BaseSoundPlayer soundPlayer, string soundName, string handler)
        {
            if (!enabled || sessionOperation == null || soundPlayer == null)
                return;
            string source = soundPlayer == weaponSoundPlayer ? "gun" : soundPlayer.name;
            Plugin.MyLog.LogInfo($"[WeaponRecon][SOUND] {handler} '{soundName}' ({source}){StateText()}");
        }

        // The clip that actually plays, and which sound player plays it
        public static void OnPlayClip(BaseSoundPlayer soundPlayer, AudioClip clip)
        {
            if (!enabled || sessionOperation == null || soundPlayer == null)
                return;
            string source = soundPlayer == weaponSoundPlayer ? "gun" : $"{soundPlayer.name} ({soundPlayer.GetType().Name})";
            Plugin.MyLog.LogInfo($"[WeaponRecon][PLAY] clip '{(clip != null ? clip.name : "null")}' on {source}{StateText()}");
        }

        public static void Tick(EFT.Player player)
        {
            if (!enabled || sessionOperation == null || player == null || !player.IsYourPlayer)
                return;

            if (VRGlobals.firearmController != sessionFirearm)
            {
                EndSession();
                return;
            }
            // Back to idle: keep listening a little longer for the sounds at the end of the clip
            if (!ReferenceEquals(sessionFirearm.CurrentOperation, sessionOperation))
            {
                if (sessionEndTime < 0f)
                {
                    sessionEndTime = Time.time + trailingSeconds;
                    Plugin.MyLog.LogInfo($"[WeaponRecon][EVENT] operation finished, now {sessionFirearm.CurrentOperation?.GetType().Name}{StateText()}");
                }
                else if (Time.time > sessionEndTime)
                {
                    EndSession();
                    return;
                }
            }

            try
            {
                AnimatorStateInfoWrapper layerOne = player.ArmsAnimatorCommon.GetCurrentAnimatorStateInfo(1);
                if (layerOne.fullPathHash != lastLayerOneState)
                {
                    lastLayerOneState = layerOne.fullPathHash;
                    Plugin.MyLog.LogInfo($"[WeaponRecon][STATE] -> {layerOne.fullPathHash} (t={layerOne.normalizedTime:F3})");
                }
            }
            catch
            {
            }

            try
            {
                SampleMagazine();
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[WeaponRecon] sample: {exception}");
                EndSession();
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void BeginSession(object operation, FirearmController firearmController)
        {
            if (sessionOperation != null)
                EndSession();

            sessionOperation = operation;
            sessionFirearm = firearmController;
            phase = ReloadPhase.MagazineInWell;
            lastLayerOneState = 0;
            sessionEndTime = -1f;
            haveRestPosition = false;
            haveOutAxis = false;
            newMagazinePath.Clear();
            oldMagazineInMarker = new StableRelation("old mag in L IK marker");
            oldMagazineInGunPalm = new StableRelation("old mag in gun L palm");
            newMagazineInMarker = new StableRelation("new mag in L IK marker");
            newMagazineInGunPalm = new StableRelation("new mag in gun L palm");
            oldMagazineFingers = null;
            newMagazineFingers = null;

            Weapon weapon = firearmController.Item;
            magazineSlot = weapon.GetMagazineSlot();
            Transform[] links = firearmController.HandsHierarchy != null ? firearmController.HandsHierarchy.Transforms : null;
            leftIkMarker = links != null ? links[(int)ECharacterWeaponBones.weapon_L_IK_marker] : null;
            gunLeftPalm = links != null ? links[(int)ECharacterWeaponBones.HumanLPalm] : null;
            gunLeftPalmChildren = gunLeftPalm != null ? gunLeftPalm.GetComponentsInChildren<Transform>(true) : null;
            weaponSoundPlayer = firearmController._controllerObject != null
                ? firearmController._controllerObject.GetComponentInChildren<BaseSoundPlayer>(true)
                : null;

            Magazine magazine = weapon.GetCurrentMagazine();
            string objectName = firearmController._controllerObject != null ? firearmController._controllerObject.name : "?";
            Plugin.MyLog.LogInfo($"[WeaponRecon] ===== {weapon.LocalizedShortName()} ({weapon.TemplateId}) object '{objectName}', "
                + $"operation {operation.GetType().Name}, magazine {(magazine != null ? magazine.LocalizedShortName() : "none")} =====");
            Plugin.MyLog.LogInfo($"[WeaponRecon][EVENT] Start{StateText()}");
            if (leftIkMarker == null || gunLeftPalm == null)
                Plugin.MyLog.LogWarning("[WeaponRecon] the gun has no weapon_L_IK_marker / HumanLPalm - no grip capture");
            LogSoundPlayers("at the start");
        }

        private static void SampleMagazine()
        {
            Transform magazineView = MagazineView();
            if (magazineView == null || leftIkMarker == null)
                return;

            Transform weaponRoot = sessionFirearm.WeaponRoot;
            Vector3 positionInGun = weaponRoot.InverseTransformPoint(magazineView.position);
            if (!haveRestPosition && phase == ReloadPhase.MagazineInWell)
            {
                magazineRestPosition = positionInGun;
                haveRestPosition = true;
            }

            if (phase == ReloadPhase.OldMagazineInHand)
            {
                if (oldMagazineInMarker.Sample(leftIkMarker, magazineView))
                    oldMagazineFingers = SnapshotFingers();
                if (gunLeftPalm != null)
                    oldMagazineInGunPalm.Sample(gunLeftPalm, magazineView);
                if (haveRestPosition && !haveOutAxis && (positionInGun - magazineRestPosition).magnitude >= axisDistance)
                {
                    outAxis = (positionInGun - magazineRestPosition).normalized;
                    haveOutAxis = true;
                }
            }
            else if (phase == ReloadPhase.NewMagazineInHand)
            {
                if (newMagazineInMarker.Sample(leftIkMarker, magazineView))
                    newMagazineFingers = SnapshotFingers();
                if (gunLeftPalm != null)
                    newMagazineInGunPalm.Sample(gunLeftPalm, magazineView);
                newMagazinePath.Add(positionInGun);
            }
        }

        private static Transform MagazineView()
        {
            if (sessionFirearm.Firearms == null || magazineSlot == null)
                return null;
            ContainerCollectionView.SlotView magazineView = sessionFirearm.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            return magazineView != null ? magazineView.ItemView : null;
        }

        private static Quaternion[] SnapshotFingers()
        {
            if (gunLeftPalmChildren == null)
                return null;
            Quaternion[] snapshot = new Quaternion[gunLeftPalmChildren.Length];
            for (int i = 0; i < gunLeftPalmChildren.Length; i++)
                snapshot[i] = gunLeftPalmChildren[i] != null ? gunLeftPalmChildren[i].localRotation : Quaternion.identity;
            return snapshot;
        }

        private static void EndSession()
        {
            try
            {
                Report();
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[WeaponRecon] report: {exception}");
            }
            sessionOperation = null;
            sessionFirearm = null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void Report()
        {
            StringBuilder report = new StringBuilder();
            report.AppendLine($"[WeaponRecon] ===== report (ended in phase {phase}) =====");
            report.AppendLine(oldMagazineInMarker.Report());
            report.AppendLine(oldMagazineInGunPalm.Report());
            report.AppendLine(newMagazineInMarker.Report());
            report.AppendLine(newMagazineInGunPalm.Report());

            if (haveOutAxis)
                report.AppendLine($"[WeaponRecon][AXIS] out of the well, gun frame: ({outAxis.x:0.###}, {outAxis.y:0.###}, {outAxis.z:0.###})");
            Vector3 inAxis;
            if (TryGetInAxis(out inAxis))
                report.AppendLine($"[WeaponRecon][AXIS] into the well, gun frame: ({inAxis.x:0.###}, {inAxis.y:0.###}, {inAxis.z:0.###})");

            AppendFingers(report, "new mag", newMagazineFingers);
            AppendFingers(report, "old mag", oldMagazineFingers);
            Plugin.MyLog.LogInfo(report.ToString());
        }

        // The last stretch of the new magazine's path before it seats
        private static bool TryGetInAxis(out Vector3 inAxis)
        {
            inAxis = Vector3.zero;
            if (newMagazinePath.Count < 2)
                return false;
            Vector3 seated = newMagazinePath[newMagazinePath.Count - 1];
            for (int i = newMagazinePath.Count - 2; i >= 0; i--)
            {
                Vector3 offset = seated - newMagazinePath[i];
                if (offset.magnitude >= axisDistance)
                {
                    inAxis = offset.normalized;
                    return true;
                }
            }
            return false;
        }

        // Index-matched like Tarkov's HandPoser: gun palm child i drives your palm child i
        private static void AppendFingers(StringBuilder report, string label, Quaternion[] fingers)
        {
            if (fingers == null || gunLeftPalmChildren == null)
            {
                report.AppendLine($"[WeaponRecon][FINGERS] {label}: none captured");
                return;
            }
            Transform[] bodyPalmChildren = InitVRPatches.leftPalm != null ? InitVRPatches.leftPalm.GetComponentsInChildren<Transform>(true) : null;
            report.AppendLine($"[WeaponRecon][FINGERS] {label}: {fingers.Length} gun palm bones, {(bodyPalmChildren != null ? bodyPalmChildren.Length : 0)} body palm bones");
            for (int i = 0; i < fingers.Length; i++)
            {
                string gunBone = gunLeftPalmChildren[i] != null ? gunLeftPalmChildren[i].name : "?";
                string bodyBone = bodyPalmChildren != null && i < bodyPalmChildren.Length ? bodyPalmChildren[i].name : "-";
                Vector3 euler = fingers[i].eulerAngles;
                report.AppendLine($"[WeaponRecon][FINGERS]   {i,2} {gunBone,-28} -> {bodyBone,-28} new Vector3({euler.x:0.#}f, {euler.y:0.#}f, {euler.z:0.#}f)");
            }
        }

        // Every sound player on the gun (the magazine model has its own), with the names it can play and how many clips
        private static void LogSoundPlayers(string when)
        {
            if (sessionFirearm == null || sessionFirearm._controllerObject == null)
                return;
            BaseSoundPlayer[] soundPlayers = sessionFirearm._controllerObject.GetComponentsInChildren<BaseSoundPlayer>(true);
            Plugin.MyLog.LogInfo($"[WeaponRecon][SOUNDS] {soundPlayers.Length} sound players {when}");
            foreach (BaseSoundPlayer soundPlayer in soundPlayers)
            {
                Plugin.MyLog.LogInfo($"[WeaponRecon][SOUNDS] '{soundPlayer.name}' ({soundPlayer.GetType().Name}) "
                    + $"additional: {SoundNames(soundPlayer.AdditionalSounds)}  main: {SoundNames(soundPlayer.MainSounds)}");
            }
        }

        private static string SoundNames(IEnumerable<BaseSoundPlayer.SoundElement> sounds)
        {
            if (sounds == null)
                return "none";
            List<string> names = new List<string>();
            foreach (BaseSoundPlayer.SoundElement sound in sounds)
            {
                if (sound != null)
                    names.Add($"{sound.EventName}({(sound.SoundClips != null ? sound.SoundClips.Length : 0)})");
            }
            return string.Join(", ", names.ToArray());
        }

        private static string StateText()
        {
            try
            {
                IAnimator animator = VRGlobals.player?.ArmsAnimatorCommon;
                if (animator == null)
                    return "";
                AnimatorStateInfoWrapper layerOne = animator.GetCurrentAnimatorStateInfo(1);
                return $"  L1(h={layerOne.fullPathHash} t={layerOne.normalizedTime:F3})";
            }
            catch
            {
                return "";
            }
        }
    }

    [HarmonyPatch]
    internal class WeaponReconPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "SoundEventHandler")]
        private static void ReconSound(BaseSoundPlayer __instance, string soundName) => WeaponRecon.OnSound(__instance, soundName, "Sound");

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "SoundAtPointEventHandler")]
        private static void ReconSoundAtPoint(BaseSoundPlayer __instance, string soundName) => WeaponRecon.OnSound(__instance, soundName, "SoundAtPoint");

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "EventReceiver")]
        private static void ReconEventReceiver(BaseSoundPlayer __instance, string eventName) => WeaponRecon.OnSound(__instance, eventName, "EventReceiver");

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BaseSoundPlayer), "PlayClip")]
        private static void ReconPlayClip(BaseSoundPlayer __instance, AudioClip clip) => WeaponRecon.OnPlayClip(__instance, clip);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(EFT.Player), "LateUpdate")]
        private static void ReconTick(EFT.Player __instance) => WeaponRecon.Tick(__instance);
    }

    // Every step of the magazine reload operation (and the client subclasses that override them)
    [HarmonyPatch]
    internal class WeaponReconOperationPatches
    {
        private static readonly string[] RECON_CALLBACKS =
        {
            "Start", "RemoveAmmoFromChamber", "OnMagPulledOutFromWeapon", "OnMagPuttedToRig", "OnMagAppeared",
            "OnMagInsertedToWeapon", "OnAddAmmoInChamber", "OnShellEjectEvent", "OnOnOffBoltCatchEvent",
            "OnIdleStartEvent", "SwitchToIdlingState", "FastForward"
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            Type reloadOperation = typeof(FirearmController.ReloadExternalMagOperation);
            Type[] types;
            try
            {
                types = reloadOperation.Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types;
            }
            foreach (Type type in types)
            {
                if (type == null || !reloadOperation.IsAssignableFrom(type))
                    continue;
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!method.IsAbstract && Array.IndexOf(RECON_CALLBACKS, method.Name) >= 0)
                        yield return method;
                }
            }
        }

        private static void Prefix(object __instance, MethodBase __originalMethod)
        {
            WeaponRecon.OnOperationCallback(__instance, __originalMethod.Name);
        }
    }
}
