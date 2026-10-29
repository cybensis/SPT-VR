using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // The small parts of the gun the hands work, moved the way Tarkov's own animations move them:
    //  - the mag release button goes in while a magazine rides up the last of the well and pops back out as it seats,
    //    and is held in while B (the mag release, Manual Reload) is down or the gun's magazine is pulled out by hand.
    //    Each gun's button moves its own way, so how far and which way is measured from the gun's own insert animation
    //    the first time it's needed.
    //  - a magazine just seated stays in the hand while the grip is held
    //  - slapping the bolt catch with the open off hand releases a locked-back bolt: it runs forward (Tarkov's locked-back
    //    pose eased out, the bolt catch paddle is part of it)
    public static partial class VRReloadController
    {
        public static bool magazineReleaseButton = true;
        public static float releaseButtonTime = 0.06f;      // s for the button to go all the way in or out (Tarkov's mag-out: ~0.1)
        public static bool holdSeatedMagazine = true;       // the hand stays on a magazine it seated while the grip is held
        public static float boltReleaseTime = 0.08f;        // s for the bolt to run forward (Tarkov: the paddle 0.07, then the bolt 0.13)
        public static bool slapBoltRelease = true;
        public static float boltSlapRadius = 0.07f;         // m palm to the bolt catch that counts as hitting it
        public static float boltSlapSpeed = 1.0f;           // m/s the off hand comes in at, against the gun hand, to count as a slap

        public static bool HoldingSeatedMagazine => seatedHoldFor != null;

        // The mag release's travel in the gun's insert animation: each part that moves, its pressed offset in its parent's
        // frame, and how far from seated the magazine is when the button starts going in and when it's all the way in
        private sealed class MagazineReleaseMotion
        {
            public string[] partNames;
            public Vector3[] pressedPositionOffsets;
            public Quaternion[] pressedRotationOffsets;
            public float pressStartDepth;
            public float pressFullDepth;
        }

        // By weapon template id; null = measured, nothing to move
        private static readonly Dictionary<string, MagazineReleaseMotion> releaseMotions = new Dictionary<string, MagazineReleaseMotion>();
        private const float releaseSampleRate = 60f;        // samples/s through the animation (its keys are 30/s)
        private const int maxInsertClipsMeasured = 3;

        private static FirearmController releasePartsFor;
        private static MagazineReleaseMotion releaseMotion;
        private static Transform[] releaseParts;
        private static Vector3[] releasePartRestPositions;
        private static Quaternion[] releasePartRestRotations;
        private static bool releasePartsPressed;
        private static float releasePressTarget;
        private static float releasePressShown;

        private static FirearmController seatedHoldFor;
        private static Transform seatedHoldWell;
        private static Vector3 handAtSeatInWell;            // the controller (LeftHand) when the magazine seated

        private static FirearmController boltReleaseFor;
        private static float boltReleaseStartTime;

        private static FirearmController boltCatchFor;
        private static Transform boltCatchPart;
        private static bool palmWasAtBoltCatch;
        private static GameObject boltCatchZoneViz;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The mag release button

        // Tick: how far in the button should be this frame (ApplyMagazineReleaseButton moves it, after the animator)
        private static void UpdateMagazineReleaseButton(bool releaseHeld)
        {
            releasePressTarget = 0f;
            if (releasePartsFor != VRGlobals.firearmController)
                ForgetReleaseParts();
            if (!magazineReleaseButton || !TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
                return;
            if (!(firearmController.CurrentOperation is FirearmController.Idling))
                return;

            // B, or the gun's magazine pulled out by hand without Manual Reload (that grab is the button press)
            float target = releaseHeld || (HoldingGunMagazine && onRail && railPullingOut && !VRSettings.GetManualReloadOn()) ? 1f : 0f;
            bool inserting = HoldingMagazine && onRail && !railPullingOut;
            if (target <= 0f && !inserting)
                return;
            if (!EnsureReleaseParts(firearmController, weapon, magazineSlot))
                return;
            // Seated = back out (the catch has snapped into the magazine)
            if (inserting && railPosition > 0f)
                target = Mathf.Max(target, Mathf.InverseLerp(releaseMotion.pressStartDepth, releaseMotion.pressFullDepth, railPosition));
            releasePressTarget = target;
        }

        // Called from HandsInteractionController.LateUpdate: the animator has posed the button this frame
        public static void ApplyMagazineReleaseButton()
        {
            if (releaseParts == null || releasePartsFor == null || releasePartsFor != VRGlobals.firearmController)
                return;
            releasePressShown = Mathf.MoveTowards(releasePressShown, releasePressTarget, Time.deltaTime / Mathf.Max(releaseButtonTime, 0.001f));
            if (releasePressShown <= 0f)
            {
                if (releasePartsPressed)
                    PutReleasePartsBack();
                return;
            }
            if (!releasePartsPressed)
            {
                for (int index = 0; index < releaseParts.Length; index++)
                {
                    if (releaseParts[index] == null)
                        return;
                    releasePartRestPositions[index] = releaseParts[index].localPosition;
                    releasePartRestRotations[index] = releaseParts[index].localRotation;
                }
                releasePartsPressed = true;
            }
            for (int index = 0; index < releaseParts.Length; index++)
            {
                Transform part = releaseParts[index];
                if (part == null)
                    continue;
                part.localPosition = releasePartRestPositions[index] + releaseMotion.pressedPositionOffsets[index] * releasePressShown;
                part.localRotation = releasePartRestRotations[index] * Quaternion.Slerp(Quaternion.identity, releaseMotion.pressedRotationOffsets[index], releasePressShown);
            }
        }

        private static void PutReleasePartsBack()
        {
            if (releaseParts != null)
            {
                for (int index = 0; index < releaseParts.Length; index++)
                {
                    if (releaseParts[index] == null)
                        continue;
                    releaseParts[index].localPosition = releasePartRestPositions[index];
                    releaseParts[index].localRotation = releasePartRestRotations[index];
                }
            }
            releasePartsPressed = false;
        }

        private static void ForgetReleaseParts()
        {
            if (releasePartsPressed)
                PutReleasePartsBack();
            releasePartsFor = null;
            releaseMotion = null;
            releaseParts = null;
            releasePressShown = 0f;
        }

        // This gun's button parts, measured once per gun type
        private static bool EnsureReleaseParts(FirearmController firearmController, Weapon weapon, Slot magazineSlot)
        {
            if (releasePartsFor == firearmController)
                return releaseParts != null;
            ForgetReleaseParts();
            releasePartsFor = firearmController;

            string templateId = weapon.TemplateId.ToString();
            if (!releaseMotions.TryGetValue(templateId, out MagazineReleaseMotion motion))
            {
                motion = MeasureReleaseMotion(firearmController, magazineSlot);
                releaseMotions[templateId] = motion;
            }
            if (motion == null)
                return false;

            Transform well = MagazineWellBone(firearmController, magazineSlot);
            Transform[] parts = new Transform[motion.partNames.Length];
            for (int index = 0; index < parts.Length; index++)
            {
                parts[index] = FindReleasePart(firearmController.WeaponRoot, well, motion.partNames[index]);
                if (parts[index] == null)
                    return false;
            }
            releaseMotion = motion;
            releaseParts = parts;
            releasePartRestPositions = new Vector3[parts.Length];
            releasePartRestRotations = new Quaternion[parts.Length];
            return true;
        }

        private static Transform MagazineWellBone(FirearmController firearmController, Slot magazineSlot)
        {
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            return magazineView != null ? magazineView.Bone : null;
        }

        // The gun's own parts named like a mag release (weapon_release, weapon_mag_release, ...), not the magazine's
        private static List<Transform> ReleaseCandidates(Transform weaponRoot, Transform well)
        {
            List<Transform> candidates = new List<Transform>();
            foreach (Transform part in weaponRoot.GetComponentsInChildren<Transform>(true))
            {
                if (IsReleasePartName(part.name) && (well == null || !part.IsChildOf(well)))
                    candidates.Add(part);
            }
            return candidates;
        }

        private static Transform FindReleasePart(Transform weaponRoot, Transform well, string partName)
        {
            foreach (Transform part in weaponRoot.GetComponentsInChildren<Transform>(true))
            {
                if (part.name == partName && (well == null || !part.IsChildOf(well)))
                    return part;
            }
            return null;
        }

        private static bool IsReleasePartName(string partName)
        {
            bool release = partName.IndexOf("release", StringComparison.OrdinalIgnoreCase) >= 0
                || partName.IndexOf("mag_catch", StringComparison.OrdinalIgnoreCase) >= 0;
            return release && partName.IndexOf("bolt", StringComparison.OrdinalIgnoreCase) < 0
                && partName.IndexOf("slide", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // Plays the gun's insert animations onto a bare copy of its bones (AnimationClip.SampleAnimation) and reads the
        // button. Unity only samples a Mecanim clip onto an object with an Animator ("Non-Legacy animations cannot be
        // sampled outside the Editor without an Animator"), and the gun's own Animator is no longer above it since the gun
        // was moved onto the rig. The copy has the gun's names and local poses from Weapon_root down, so the clips' paths
        // match, and the live gun isn't touched.
        private static MagazineReleaseMotion MeasureReleaseMotion(FirearmController firearmController, Slot magazineSlot)
        {
            string gunName = firearmController.Item.LocalizedShortName();
            Transform weaponRoot = firearmController.WeaponRoot;
            RuntimeAnimatorController animatorController = firearmController.FirearmsAnimator?.Animator?.runtimeAnimatorController;
            if (weaponRoot == null || weaponRoot.parent == null || animatorController == null)
            {
                Plugin.MyLog.LogWarning($"[VRReload] {gunName}: no animator to measure the mag release from");
                return null;
            }
            Transform well = MagazineWellBone(firearmController, magazineSlot);
            List<Transform> parts = ReleaseCandidates(weaponRoot, well);
            if (parts.Count == 0)
            {
                Plugin.MyLog.LogInfo($"[VRReload] {gunName}: no mag release part on this gun");
                return null;
            }

            GameObject sampler = new GameObject("VRReloadReleaseSampler");
            try
            {
                sampler.transform.localScale = weaponRoot.parent.lossyScale;
                sampler.AddComponent<Animator>();
                Dictionary<Transform, Transform> copies = new Dictionary<Transform, Transform>();
                List<Transform> sampledParts = new List<Transform>();
                foreach (Transform part in parts)
                    sampledParts.Add(CopyBoneChain(part, weaponRoot, sampler.transform, copies));
                Transform sampledWell = well != null ? CopyBoneChain(well, weaponRoot, sampler.transform, copies) : null;

                int measured = 0;
                foreach (AnimationClip clip in animatorController.animationClips)
                {
                    if (clip == null || clip.name.IndexOf("mag_in", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    MagazineReleaseMotion motion = MeasureReleaseInClip(clip, sampler, sampledParts, sampledWell, gunName);
                    if (motion != null)
                        return motion;
                    if (++measured >= maxInsertClipsMeasured)
                        break;
                }
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogError($"[VRReload] {gunName}: measuring the mag release failed: {exception}");
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(sampler);
            }
            Plugin.MyLog.LogInfo($"[VRReload] {gunName}: the mag release doesn't move in its insert animation");
            return null;
        }

        // An empty object for the bone and each parent up to Weapon_root, named and posed like the gun's
        private static Transform CopyBoneChain(Transform bone, Transform weaponRoot, Transform samplerRoot, Dictionary<Transform, Transform> copies)
        {
            if (copies.TryGetValue(bone, out Transform copy))
                return copy;
            Transform parentCopy = bone == weaponRoot ? samplerRoot : CopyBoneChain(bone.parent, weaponRoot, samplerRoot, copies);
            copy = new GameObject(bone.name).transform;
            copy.SetParent(parentCopy, worldPositionStays: false);
            copy.localPosition = bone.localPosition;
            copy.localRotation = bone.localRotation;
            copy.localScale = bone.localScale;
            copies[bone] = copy;
            return copy;
        }

        private static MagazineReleaseMotion MeasureReleaseInClip(AnimationClip clip, GameObject sampleRoot, List<Transform> parts, Transform well, string gunName)
        {
            int sampleCount = Mathf.Max(2, Mathf.CeilToInt(clip.length * releaseSampleRate) + 1);
            int partCount = parts.Count;
            Vector3[,] partPositions = new Vector3[sampleCount, partCount];
            Quaternion[,] partRotations = new Quaternion[sampleCount, partCount];
            float[] depths = new float[sampleCount];
            Vector3[] wellPositions = new Vector3[sampleCount];
            for (int sample = 0; sample < sampleCount; sample++)
            {
                clip.SampleAnimation(sampleRoot, clip.length * sample / (sampleCount - 1));
                for (int part = 0; part < partCount; part++)
                {
                    partPositions[sample, part] = parts[part].localPosition;
                    partRotations[sample, part] = parts[part].localRotation;
                }
                if (well != null)
                    wellPositions[sample] = well.localPosition;
            }

            // The end of the clip is seated with the button out; the magazine's distance from there, against the gun body
            // (the well bone's parent, which the animation also sways), is its depth
            int last = sampleCount - 1;
            float deepest = 0f;
            for (int sample = 0; sample < sampleCount; sample++)
            {
                Vector3 move = wellPositions[sample] - wellPositions[last];
                depths[sample] = well == null ? 0f : well.parent != null ? well.parent.TransformVector(move).magnitude : move.magnitude;
                deepest = Mathf.Max(deepest, depths[sample]);
            }

            // The part that moves the most (1 mm counts as 1 degree), and its most pressed moment
            int mainPart = -1;
            int peakSample = 0;
            float peakTravel = 0f;
            float[,] travels = new float[sampleCount, partCount];
            for (int part = 0; part < partCount; part++)
            {
                Transform parent = parts[part].parent;
                for (int sample = 0; sample < sampleCount; sample++)
                {
                    Vector3 move = partPositions[sample, part] - partPositions[last, part];
                    float travel = (parent != null ? parent.TransformVector(move).magnitude : move.magnitude) * 1000f
                        + Quaternion.Angle(partRotations[last, part], partRotations[sample, part]);
                    travels[sample, part] = travel;
                    if (travel > peakTravel)
                    {
                        peakTravel = travel;
                        peakSample = sample;
                        mainPart = part;
                    }
                }
            }
            if (mainPart < 0 || peakTravel < 0.5f)
            {
                if (well != null && deepest < 0.005f)
                    Plugin.MyLog.LogWarning($"[VRReload] {gunName}: sampling '{clip.name}' (from '{sampleRoot.transform.GetChild(0).name}') didn't move the magazine either, so the measurement doesn't work on this gun");
                return null;
            }

            // Seated = the first moment the magazine is home after being out
            int seatSample = last;
            bool wasOut = false;
            for (int sample = 0; sample < sampleCount; sample++)
            {
                if (depths[sample] > 0.01f)
                    wasOut = true;
                else if (wasOut && depths[sample] < 0.001f)
                {
                    seatSample = sample;
                    break;
                }
            }
            float pressStart = DepthWherePressReaches(0.1f, travels, mainPart, peakTravel, depths, seatSample);
            float pressFull = DepthWherePressReaches(0.9f, travels, mainPart, peakTravel, depths, seatSample);
            bool timedFromAnimation = pressStart > 0f && pressFull > 0f;
            if (!timedFromAnimation)
            {
                pressStart = railLength;
                pressFull = railLength * 0.7f;
            }
            if (pressStart <= pressFull + 0.001f)
                pressStart = pressFull + 0.005f;

            List<string> names = new List<string>();
            List<Vector3> positionOffsets = new List<Vector3>();
            List<Quaternion> rotationOffsets = new List<Quaternion>();
            for (int part = 0; part < partCount; part++)
            {
                if (travels[peakSample, part] < 0.2f)
                    continue;
                names.Add(parts[part].name);
                positionOffsets.Add(partPositions[peakSample, part] - partPositions[last, part]);
                rotationOffsets.Add(Quaternion.Inverse(partRotations[last, part]) * partRotations[peakSample, part]);
            }

            MagazineReleaseMotion motion = new MagazineReleaseMotion
            {
                partNames = names.ToArray(),
                pressedPositionOffsets = positionOffsets.ToArray(),
                pressedRotationOffsets = rotationOffsets.ToArray(),
                pressStartDepth = pressStart,
                pressFullDepth = pressFull,
            };
            string partList = "";
            for (int index = 0; index < motion.partNames.Length; index++)
                partList += $" {motion.partNames[index]} {motion.pressedPositionOffsets[index].ToString("F4")} {motion.pressedRotationOffsets[index].eulerAngles.ToString("F1")}";
            Plugin.MyLog.LogInfo($"[VRReload] {gunName} mag release from '{clip.name}':{partList}; goes in from {pressStart * 100f:0.0} cm to {pressFull * 100f:0.0} cm above seated"
                + (timedFromAnimation ? "" : " (default, the animation presses it after seating)"));
            return motion;
        }

        // Going up the well: the magazine's depth when the button first reaches this share of its travel (between samples)
        private static float DepthWherePressReaches(float share, float[,] travels, int part, float peakTravel, float[] depths, int seatSample)
        {
            for (int sample = 1; sample <= seatSample; sample++)
            {
                float previous = travels[sample - 1, part] / peakTravel;
                float current = travels[sample, part] / peakTravel;
                if (previous < share && current >= share)
                    return Mathf.Lerp(depths[sample - 1], depths[sample], (share - previous) / Mathf.Max(current - previous, 1e-6f));
            }
            return -1f;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The hand on a seated magazine, and the bolt release

        // Just seated with the grip still held: the rendered hand stays where it holds the magazine, riding the gun
        private static void BeginSeatedHold(FirearmController firearmController, Transform well)
        {
            if (!holdSeatedMagazine || firearmController == null || well == null || handLatch == null || !handLatched)
                return;
            seatedHoldFor = firearmController;
            seatedHoldWell = well;
            handAtSeatInWell = well.InverseTransformPoint(VRGlobals.vrPlayer.LeftHand.transform.position);
            // ClearHold just started easing it back to the controller: it stays on the magazine instead
            handLatch.SetParent(well, worldPositionStays: true);
            handLatchReleasing = false;
            handLatchSnapping = false;
            AssertHandLatch();
            OwnsOffHand = true;
        }

        private static void UpdateSeatedHold(bool gripReleased)
        {
            FirearmController firearmController = seatedHoldFor;
            Weapon weapon = firearmController.Item;
            // Still in the gun (the insert may be finishing this frame)
            bool magazineIn = weapon != null && (weapon.GetCurrentMagazine() != null || expectedOperation == ExpectedOperation.Insert);
            bool valid = enabled && VRGlobals.inGame && !VRGlobals.menuOpen && VRGlobals.vrPlayer != null && VRGlobals.vrPlayer.LeftHand != null
                && firearmController == VRGlobals.firearmController && seatedHoldWell != null && magazineIn && !ChestHang.Detached;
            if (!valid || gripReleased)
            {
                EndSeatedHold();
                return;
            }
            Vector3 handOffMagazine = VRGlobals.vrPlayer.LeftHand.transform.position - seatedHoldWell.TransformPoint(handAtSeatInWell);
            if (handOffMagazine.sqrMagnitude > railLateralEscape * railLateralEscape)
                EndSeatedHold();
        }

        private static void EndSeatedHold()
        {
            seatedHoldFor = null;
            seatedHoldWell = null;
            ReleaseHandLatch();
        }

        // The open off hand coming into the bolt catch fast (against the gun hand, which carries the gun) is a slap on it,
        // the way an AR's bolt is released. Only coming in counts: a hand already resting there has to leave first.
        private static void UpdateBoltSlap(bool handFree)
        {
            FirearmController firearmController = null;
            bool canSlap = slapBoltRelease && handFree && TryGetMagazineGun(out firearmController, out _, out _)
                && !VRGlobals.vrPlayer.isSupporting && !ChestHang.Detached
                && (VRGlobals.handsInteractionController == null || VRGlobals.handsInteractionController.heldItem == null);
            bool atCatch = false;
            Vector3 catchPoint = Vector3.zero;
            bool hasCatchPoint = canSlap && TryGetBoltCatchPoint(firearmController, out catchPoint);
            if (hasCatchPoint)
            {
                atCatch = (OffHandPalm() - catchPoint).sqrMagnitude <= boltSlapRadius * boltSlapRadius;
                if (atCatch && !palmWasAtBoltCatch)
                {
                    float speed = (ControllerVelocity.GetSteamVRVelocity(OffHandSource()) - ControllerVelocity.GetSteamVRVelocity(GunHandSource())).magnitude;
                    if (debugLog)
                        Plugin.MyLog.LogInfo($"[VRReload] hand at the bolt catch at {speed:0.00} m/s (slap from {boltSlapSpeed:0.00})");
                    if (speed >= boltSlapSpeed)
                        ReleaseBolt(firearmController);
                }
            }
            palmWasAtBoltCatch = atCatch;
            SetZoneViz(ref boltCatchZoneViz, debugZones && hasCatchPoint, catchPoint, boltSlapRadius,
                atCatch ? new Color(0f, 1f, 0f, 0.3f) : new Color(0.7f, 0f, 1f, 0.3f));
        }

        // The bolt catch paddle (a part named like bolt_catch), else the chamber: on the gun's side by the receiver
        private static bool TryGetBoltCatchPoint(FirearmController firearmController, out Vector3 point)
        {
            if (boltCatchFor != firearmController)
            {
                boltCatchFor = firearmController;
                boltCatchPart = null;
                if (firearmController.WeaponRoot != null)
                {
                    foreach (Transform part in firearmController.WeaponRoot.GetComponentsInChildren<Transform>(true))
                    {
                        if (part.name.IndexOf("bolt_catch", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            boltCatchPart = part;
                            break;
                        }
                    }
                }
                if (boltCatchPart == null)
                {
                    Transform[] chamberBones = firearmController.Firearms != null ? firearmController.Firearms._patronInWeaponTransforms : null;
                    boltCatchPart = chamberBones != null && chamberBones.Length > 0 ? chamberBones[0] : null;
                }
            }
            point = boltCatchPart != null ? boltCatchPart.position : Vector3.zero;
            return boltCatchPart != null;
        }

        private static SteamVR_Input_Sources GunHandSource()
        {
            return VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
        }

        // A locked-back bolt runs forward, unless an empty magazine on an empty chamber holds it (its follower keeps the
        // catch up, as with the charging handle). Tarkov's insert already counted the round in the chamber; this clears
        // the same bolt-catch flag the charging handle does.
        private static void ReleaseBolt(FirearmController firearmController)
        {
            FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
            Weapon weapon = firearmController.Item;
            if (firearmsAnimator == null || weapon == null || !firearmsAnimator.GetBoltCatch() || BoltStaysBack(weapon))
                return;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None)
                return;
            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRReload] bolt release, chamber: {weapon.ChamberAmmoCount}");
            firearmsAnimator.SetBoltCatch(false);
            // SetBoltCatch snaps the pose shut; it runs forward instead (UpdateBoltRelease)
            SetBoltOpen(firearmsAnimator, 1f);
            boltReleaseFor = firearmController;
            boltReleaseStartTime = Time.time;
            PlayGunSound(firearmController, "BoltInFast");
            Pulse(0.7f, 0.05f);
            PulseGunHand(0.6f, 0.05f);
        }

        private static void UpdateBoltRelease()
        {
            FirearmController firearmController = boltReleaseFor;
            if (firearmController != VRGlobals.firearmController || firearmController.FirearmsAnimator == null || HoldingChargingHandle)
            {
                boltReleaseFor = null;
                return;
            }
            float progress = (Time.time - boltReleaseStartTime) / Mathf.Max(boltReleaseTime, 0.001f);
            if (progress >= 1f)
            {
                boltReleaseFor = null;
                RestoreBoltPose(firearmController);
                return;
            }
            // The spring drives it: it picks up speed as it goes
            SetBoltOpen(firearmController.FirearmsAnimator, 1f - progress * progress);
        }
    }
}
