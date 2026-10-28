using System.Collections.Generic;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using RootMotion.FinalIK;
using UnityEngine;
using Valve.VR;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Gun controller v2: magazines by hand. The off hand grabs the gun's magazine and pulls it out, takes a new one
    // from the chest, and seats it in the mag well. Each step runs Tarkov's own inventory move (the same moves the
    // inventory screen uses, so FIKA syncs them) and finishes Tarkov's operation the same frame, skipping its
    // animation, so the gun is never left mid-operation.
    //
    // Nothing in the inventory changes until the outcome is known:
    //  - the gun's magazine is only hidden while you hold it; letting go decides: back in the well = nothing changed,
    //    at the chest = into your rig/pockets, anywhere else = it drops to the ground where you let go
    //  - a new magazine stays in your rig until it is seated; letting go of it anywhere else puts it back
    // B double-tap (Y left-handed) drops the magazine straight out of the gun.
    public static class VRMagazineController
    {
        public static bool enabled = true;
        public static float magazineGrabRadius = 0.08f;     // m palm to the gun magazine's center to grab it
        // The mag well track: a magazine in the well only slides along the well's axis, with the hand locked on it
        public static float railLength = 0.05f;             // m a magazine slides in the well before it comes free (pulling out) / from the top to seated (putting in)
        public static float railCaptureRadius = 0.04f;      // m magazine top to the track that catches a magazine being put in
        public static float railCaptureAngle = 40f;         // deg off the seated orientation that still catches
        public static float railCaptureTime = 0.12f;        // s to ease a caught magazine onto the track
        public static float railExitMargin = 0.02f;         // m past the top of the track before a magazine being put in comes off it
        public static float railLateralEscape = 0.10f;      // m the controller moves sideways off the track before the hand lets go of it
        public static float handReleaseTime = 0.08f;        // s the rendered hand takes to go from the magazine back to the controller
        public static bool snapHandToGrip = true;           // grabbing the gun's magazine moves the hand onto it where the animation holds it
        public static float handSnapTime = 0.06f;           // s that move takes
        public static float gripEaseRate = 10f;             // 1/s a free magazine settles into the animation's grip
        public static Vector3 chestPouchOffset = new Vector3(0f, -0.42f, 0.05f);    // headset-local, yaw-only: the chest pouch zone center (headset round 1: 0.12 forward was too far out)
        public static float chestPouchRadius = 0.16f;       // m palm to the chest pouch center
        public static Vector3 newMagazineHoldPosition = new Vector3(-0.10f, 0.02f, 0.11f);    // off hand local, used until a magazine is pulled off a gun (average of his headset grabs)
        public static Vector3 newMagazineHoldRotation = new Vector3(320f, 250f, 265f);        // off hand local euler, same
        public static bool reuseLastGrabPose = true;        // guns without a measured reload grip: new magazines sit in the hand the way the last one pulled off a gun did
        public static bool magazineFingers = true;          // the animation's finger pose while holding a magazine
        public static float fingerBlendRate = 12f;          // 1/s ease of that finger pose in and out
        public static float dropAngularSpeed = 3f;          // rad/s tumble on a dropped magazine
        public static bool sounds = true;
        public static float soundVolume = 0.8f;
        public static bool haptics = true;
        public static bool debugZones = false;              // spheres: grab zone (blue), chest pouch (yellow), mag well seat radius (green)
        public static bool debugLog = false;

        private enum HeldMagazineSource { None, FromGun, FromPouch }
        private enum ExpectedOperation { None, PullOut, Insert }

        // Off hand state, read by VRPlayerManager / the loot pointer / the weapon interact
        public static bool OwnsOffHand { get; private set; }            // holding a magazine, or reaching for one
        public static bool HoldingMagazine => heldSource != HeldMagazineSource.None;
        public static bool HoldingGunMagazine => heldSource == HeldMagazineSource.FromGun;   // still in the gun as far as Tarkov knows: no firing

        private static HeldMagazineSource heldSource;
        private static Magazine heldMagazine;
        private static FirearmController heldForFirearm;
        private static GameObject heldProp;
        private static Transform hiddenGunMagazineView;
        private static Transform magazineWell;
        private static Vector3 propWellPointLocal;          // the point on the prop that sits at the mag well when seated
        private static bool magazineLeftWell;
        private static Vector3 freeHoldPosition;            // where a magazine off the track sits in the hand (LeftHand-local)
        private static Quaternion freeHoldRotation;

        // The track, in the mag well bone's frame: the seated pose, and the axis the magazine slides out along
        private static Vector3 seatedLocalPosition;
        private static Quaternion seatedLocalRotation;
        private static Vector3 railAxisInWell;
        private static bool onRail;
        private static bool railCatchArmed;                 // a magazine that just came off the track must leave the catch zone first
        private static bool railPullingOut;                 // started seated (the gun's own magazine): comes free at railLength
        private static float railPosition;                  // m out from seated
        private static float railPositionAtStart;
        private static Vector3 handAtRailStartInWell;       // the controller (LeftHand), not the rendered hand
        private static float railBlend;                     // easing a caught magazine onto the track
        private static Vector3 railEntryHandPosition;       // the magazine's hand-local pose when it was caught
        private static Quaternion railEntryHandRotation;

        // While on the track the rendered hand holds the magazine: the left arm IK reaches for this instead of LeftHand
        private static Transform handLatch;
        private static bool handLatched;
        private static bool handLatchReleasing;
        private static bool handLatchSnapping;
        private static Vector3 handSnapLocalPosition;       // the animation's grip, in the magazine's frame
        private static Quaternion handSnapLocalRotation;
        private static List<Collider> disabledPropColliders = new List<Collider>();
        private static Dictionary<Transform, int> propOriginalLayers = new Dictionary<Transform, int>();

        private static bool hasLastGrabPose;
        private static Vector3 lastGrabPosition;
        private static Quaternion lastGrabRotation;

        private static ReloadGrip heldReloadGrip;

        private static float fingerWeight;
        private static Vector3[] fingerPoseShown;
        private static Transform[] leftPalmBones;

        // Committed to an inventory move: the prop stays in the hand and the gun's magazine stays hidden until
        // Tarkov's operation is finished, so neither flashes for the frame in between
        private static GameObject pendingProp;
        private static Transform pendingHiddenView;

        private static bool previousGripState;
        private static bool previousReleaseState;
        private static int lastTickFrame = -10;

        private static ExpectedOperation expectedOperation;
        private static FirearmController expectedForFirearm;
        private static float expectedOperationDeadline;
        private static int expectedOperationStartFrame;

        // Placement for the magazine Tarkov throws into the world (VRMagazinePatches)
        private static Item pendingDropMagazine;
        private static Vector3 pendingDropPosition;
        private static Quaternion pendingDropRotation;
        private static Vector3 pendingDropVelocity;
        private static float pendingDropDeadline;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Tarkov's reload, measured from its own animation (WeaponRecon)

        // Where the magazine sits relative to the left-hand IK target while the animation holds it. In VR that target
        // is our LeftHand, so these are the prop's local pose in the hand. Old = pulling it out, new = putting it in.
        private sealed class ReloadGrip
        {
            public Vector3 oldMagazinePosition;
            public Vector3 oldMagazineRotation;
            public Vector3 newMagazinePosition;
            public Vector3 newMagazineRotation;
        }

        // By weapon template id
        private static readonly Dictionary<string, ReloadGrip> reloadGrips = new Dictionary<string, ReloadGrip>
        {
            // M4A1 (3 reloads, identical)
            {
                "5447a9cd4bdc2dbd208b4567", new ReloadGrip
                {
                    oldMagazinePosition = new Vector3(-0.186f, -0.033f, 0.115f), oldMagazineRotation = new Vector3(352.6f, 250f, 271.1f),
                    newMagazinePosition = new Vector3(-0.198f, -0.198f, 0.022f), newMagazineRotation = new Vector3(337.8f, 265.3f, 180.4f),
                }
            },
        };

        // The animation's left-hand finger poses holding a magazine, index-matched like Tarkov's HandPoser
        // (GetComponentsInChildren of the palm; 0 is the palm itself and is left alone). Measured on the M4A1, used for
        // every gun for now. Pull = the most closed moment while the hand is on the old magazine (the mag-out sound).
        private static readonly Vector3[] MAGAZINE_PULL_FINGERS =
        {
            Vector3.zero,
            new Vector3(50.3f, 117.5f, 96f), new Vector3(18.7f, 359.9f, 21.4f), new Vector3(15.6f, 0.1f, 37f),       // thumb
            new Vector3(0.7f, 355.6f, 9.1f), new Vector3(0.1f, 359.9f, 45.9f), new Vector3(0.1f, 359.9f, 57.2f),      // index
            new Vector3(0.6f, 357.4f, 22.4f), new Vector3(0f, 360f, 61.8f), new Vector3(0.1f, 360f, 49.9f),           // middle
            new Vector3(0.7f, 356.6f, 21.1f), new Vector3(0f, 360f, 44.9f), new Vector3(0f, 360f, 39.3f),             // ring
            new Vector3(1.1f, 355.7f, 17.1f), new Vector3(0f, 0f, 7.5f), new Vector3(360f, 0f, 67.3f),                // pinky
        };

        // Insert = while it puts the new magazine in
        private static readonly Vector3[] MAGAZINE_INSERT_FINGERS =
        {
            Vector3.zero,
            new Vector3(63.2f, 102.2f, 82.7f), new Vector3(353.7f, 359.7f, 15.9f), new Vector3(0.4f, 359.4f, 16f),    // thumb
            new Vector3(0.7f, 355.6f, 15.9f), new Vector3(0.1f, 360f, 49.8f), new Vector3(0.4f, 359.9f, 27.4f),       // index
            new Vector3(0.2f, 355f, 46.6f), new Vector3(0.1f, 360f, 73.5f), new Vector3(0.2f, 0.2f, 67.3f),           // middle
            new Vector3(0.1f, 354.4f, 62.1f), new Vector3(0f, 360f, 79f), new Vector3(0f, 360f, 58.3f),               // ring
            new Vector3(0.3f, 353.8f, 73.3f), new Vector3(360f, 360f, 85f), new Vector3(360f, 0f, 61f),               // pinky
        };

        private static ReloadGrip FindReloadGrip(Weapon weapon)
        {
            if (weapon == null)
                return null;
            reloadGrips.TryGetValue(weapon.TemplateId.ToString(), out ReloadGrip reloadGrip);
            return reloadGrip;
        }

        private static readonly List<Magazine> reachableMagazines = new List<Magazine>();
        private static Transform cachedRendererRoot;
        private static Renderer[] cachedRenderers;
        private static GameObject grabZoneViz, pouchZoneViz, wellZoneViz;

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Called every frame from the top of HandsInteractionController.Update
        public static void Tick()
        {
            // Resync the grip edge after skipped frames so a held grip doesn't read as a fresh press
            VRBoolean offHandGrip = VRSettings.GetLeftHandedMode() ? VRInput.RightGrip : VRInput.LeftGrip;
            bool gripState = offHandGrip.state;
            if (Time.frameCount - lastTickFrame > 1)
                previousGripState = gripState;
            lastTickFrame = Time.frameCount;
            bool gripPressed = gripState && !previousGripState;
            bool gripReleased = !gripState && previousGripState;
            previousGripState = gripState;

            // A click on the gun hand when the mag release goes down
            bool releaseState = MagazineReleaseHeld();
            if (releaseState && !previousReleaseState)
                PulseGunHand(0.25f, 0.03f);
            previousReleaseState = releaseState;

            if (expectedOperation != ExpectedOperation.None)
                TryForceExpectedOperation();

            if (HoldingMagazine)
            {
                if (!HoldIsStillValid())
                    CancelHold();
                else
                    UpdateHeldMagazine(gripReleased);
            }
            UpdateHandLatch();

            bool reaching = false;
            if (!HoldingMagazine && CanStartGrab(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
            {
                Vector3 palm = OffHandPalm();
                bool supporting = VRGlobals.vrPlayer.isSupporting;
                bool inGrabZone = InGunMagazineGrabZone(firearmController, weapon, magazineSlot, palm);
                bool inPouchZone = !supporting && InChestPouchZone(palm);
                // A hand on the support grip is only taken over by an actual grab, never by passing through a zone
                // (a pistol's support hand cups right over its magazine)
                reaching = !supporting && (inGrabZone || inPouchZone);
                if (gripPressed && inGrabZone)
                    GrabFromGun(firearmController, weapon, magazineSlot);
                else if (gripPressed && inPouchZone)
                    GrabFromPouch(firearmController, weapon, magazineSlot);
            }
            OwnsOffHand = HoldingMagazine || reaching;

            UpdateZoneViz();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Grabbing

        private static bool CanStartGrab(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot)
        {
            weapon = null;
            magazineSlot = null;
            if (!TryGetMagazineGun(out firearmController, out weapon, out magazineSlot))
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None)
                return false;
            if (ChestHang.Detached)
                return false;
            if (VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.heldItem != null)
                return false;
            return true;
        }

        // Manual reload on a gun with a detachable magazine: B is the mag release, not Tarkov's reload
        public static bool ManualReloadActive()
        {
            return VRSettings.GetManualReloadOn() && TryGetMagazineGun(out _, out _, out _);
        }

        // Holding B (Y left-handed) with manual reload on
        private static bool MagazineReleaseHeld()
        {
            if (!ManualReloadActive())
                return false;
            VRBoolean releaseButton = VRSettings.GetLeftHandedMode() ? VRInput.ButtonY : VRInput.ButtonB;
            return releaseButton.state;
        }

        // A held gun with a detachable magazine, in our player's hands
        private static bool TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot)
        {
            firearmController = null;
            weapon = null;
            magazineSlot = null;
            if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.vrPlayer.LeftHand == null
                || VRGlobals.player == null)
                return false;
            firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController.Firearms == null || firearmController.IsInLauncherMode())
                return false;
            weapon = firearmController.Item;
            if (weapon == null)
                return false;
            if (weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazine && weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport)
                return false;
            magazineSlot = weapon.GetMagazineSlot();
            return magazineSlot != null;
        }

        private static bool InGunMagazineGrabZone(FirearmController firearmController, Weapon weapon, Slot magazineSlot, Vector3 palm)
        {
            if (weapon.GetCurrentMagazine() == null)
                return false;
            // Manual reload: only while the mag release is held, on any gun. Otherwise grab-and-pull, but not on
            // pistols (the magazine sits inside the grip where the support hand cups it) or from the support grip.
            if (VRSettings.GetManualReloadOn())
            {
                if (!MagazineReleaseHeld())
                    return false;
            }
            else if (VRGlobals.vrPlayer.isWeapPistol || VRGlobals.vrPlayer.isSupporting)
            {
                return false;
            }
            if (!TryGetGunMagazineCenter(firearmController, magazineSlot, out Vector3 magazineCenter))
                return false;
            return (palm - magazineCenter).sqrMagnitude <= magazineGrabRadius * magazineGrabRadius;
        }

        private static bool InChestPouchZone(Vector3 palm)
        {
            if (!TryGetChestPouchCenter(out Vector3 pouchCenter))
                return false;
            return (palm - pouchCenter).sqrMagnitude <= chestPouchRadius * chestPouchRadius;
        }

        private static void GrabFromGun(FirearmController firearmController, Weapon weapon, Slot magazineSlot)
        {
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            Magazine magazine = weapon.GetCurrentMagazine();
            if (magazineView == null || magazineView.ItemView == null || magazine == null)
                return;
            // Tarkov won't unload a Feed malfunction either (it wants the jam examined first)
            if (weapon.MalfState.State == Weapon.EMalfunctionState.Feed)
            {
                Pulse(0.2f, 0.03f);
                Pulse(0.2f, 0.03f, 0.08f);
                return;
            }

            GameObject prop = CreateProp(magazine);
            if (prop == null)
                return;

            // The prop takes the exact pose of the magazine on the gun (a direct child of the well bone) and starts on
            // the track, seated, with the hand locked on it
            Transform propTransform = prop.transform;
            propTransform.SetParent(magazineView.Bone, worldPositionStays: false);
            propTransform.localPosition = magazineView.ItemView.localPosition;
            propTransform.localRotation = magazineView.ItemView.localRotation;
            propTransform.localScale = magazineView.ItemView.localScale;
            propWellPointLocal = propTransform.InverseTransformPoint(magazineView.Bone.position);
            seatedLocalPosition = propTransform.localPosition;
            seatedLocalRotation = propTransform.localRotation;
            heldReloadGrip = FindReloadGrip(weapon);
            railAxisInWell = RailAxisInWell(prop, propTransform.localScale);

            hiddenGunMagazineView = magazineView.ItemView;
            hiddenGunMagazineView.gameObject.SetActive(false);

            heldSource = HeldMagazineSource.FromGun;
            heldMagazine = magazine;
            heldForFirearm = firearmController;
            heldProp = prop;
            magazineWell = magazineView.Bone;
            magazineLeftWell = false;
            StartRail(pullingOut: true, startPosition: 0f);
            if (snapHandToGrip && heldReloadGrip != null)
                SnapHandToGrip(heldReloadGrip.oldMagazinePosition, heldReloadGrip.oldMagazineRotation);

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] grabbed gun magazine {magazine.LocalizedShortName()}, track axis {railAxisInWell.ToString("F2")}");
            PlayMagazineSound(magazine, EInventorySoundType.pickup, propTransform.position);
            Pulse(0.35f, 0.05f);
        }

        private static void GrabFromPouch(FirearmController firearmController, Weapon weapon, Slot magazineSlot)
        {
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            Magazine magazine = FindMagazineToReload(weapon, magazineSlot);
            if (magazineView == null || magazine == null)
            {
                // Nothing that fits: a short double buzz
                Pulse(0.2f, 0.03f);
                Pulse(0.2f, 0.03f, 0.08f);
                return;
            }

            GameObject prop = CreateProp(magazine);
            if (prop == null)
                return;

            Transform propTransform = prop.transform;
            Transform handBone = HandBone();
            propTransform.SetParent(handBone, worldPositionStays: false);
            ReloadGrip reloadGrip = FindReloadGrip(weapon);
            if (reloadGrip != null)
            {
                freeHoldPosition = reloadGrip.newMagazinePosition;
                freeHoldRotation = Quaternion.Euler(reloadGrip.newMagazineRotation);
            }
            else
            {
                bool useLastGrab = reuseLastGrabPose && hasLastGrabPose;
                freeHoldPosition = useLastGrab ? lastGrabPosition : newMagazineHoldPosition;
                freeHoldRotation = useLastGrab ? lastGrabRotation : Quaternion.Euler(newMagazineHoldRotation);
            }
            Vector3 seatedScale = SeatedScale(prop);
            SetWorldScale(propTransform, Vector3.Scale(magazineView.Bone.lossyScale, seatedScale));
            SetPoseIn(handBone, propTransform, freeHoldPosition, freeHoldRotation);

            // Where it will sit in the well (ContainerCollectionView.SlotView.InsertItem), for the track
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            seatedLocalPosition = modPlacer != null ? modPlacer.ModPosition : Vector3.zero;
            seatedLocalRotation = modPlacer != null ? Quaternion.Euler(modPlacer.ModRotation) : Quaternion.Euler(90f, 0f, 0f);
            propWellPointLocal = SeatedWellPointLocal(prop);
            railAxisInWell = RailAxisInWell(prop, seatedScale);

            heldSource = HeldMagazineSource.FromPouch;
            heldMagazine = magazine;
            heldForFirearm = firearmController;
            heldProp = prop;
            heldReloadGrip = reloadGrip;
            magazineWell = magazineView.Bone;
            magazineLeftWell = true;
            onRail = false;
            railCatchArmed = true;

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] took {magazine.LocalizedShortName()} ({magazine.Count} rounds) from the pouch");
            PlayMagazineSound(magazine, EInventorySoundType.pickup, propTransform.position);
            Pulse(0.35f, 0.05f);
        }

        // Tarkov's reload pick (FirearmHandsInputTranslator.ReloadExternalMagazine): the fullest reachable magazine that fits
        private static Magazine FindMagazineToReload(Weapon weapon, Slot magazineSlot)
        {
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            Magazine magazineInGun = weapon.GetCurrentMagazine();
            reachableMagazines.Clear();
            inventoryController.GetReachableItemsOfTypeNonAlloc(reachableMagazines, (Magazine magazine) => magazine.Count > 0);
            reachableMagazines.Sort((Magazine first, Magazine second) => second.Count.CompareTo(first.Count));
            foreach (Magazine magazine in reachableMagazines)
            {
                if (magazine == magazineInGun)
                    continue;
                if (ItemManipulator.CheckMoveIgnoringTargetItem(magazine, magazineSlot, inventoryController).Succeeded)
                    return magazine;
            }
            return null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Holding

        private static bool HoldIsStillValid()
        {
            if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null)
                return false;
            if (VRGlobals.firearmController == null || VRGlobals.firearmController != heldForFirearm)
                return false;
            return heldProp != null && magazineWell != null && heldMagazine != null;
        }

        private static void UpdateHeldMagazine(bool gripReleased)
        {
            if (onRail)
                UpdateRail(gripReleased);
            else
                UpdateFreeHold(gripReleased);
        }

        // Off the track: the magazine settles into the animation's grip, and gets caught by the track near the well
        private static void UpdateFreeHold(bool gripReleased)
        {
            Transform propTransform = heldProp.transform;
            Transform handBone = HandBone();
            float ease = 1f - Mathf.Exp(-gripEaseRate * Time.deltaTime);
            GetPoseIn(handBone, propTransform, out Vector3 handLocalPosition, out Quaternion handLocalRotation);
            SetPoseIn(handBone, propTransform, Vector3.Lerp(handLocalPosition, freeHoldPosition, ease),
                Quaternion.Slerp(handLocalRotation, freeHoldRotation, ease));

            if (gripReleased)
            {
                ReleaseHeldMagazine();
                return;
            }

            // Caught: the magazine's top near the track (up to just past its top), turned roughly the seated way
            Vector3 axis = RailAxisWorld();
            Vector3 seatedPosition = magazineWell.TransformPoint(seatedLocalPosition);
            Vector3 topOffset = propTransform.TransformPoint(propWellPointLocal) - magazineWell.position;
            float alongTrack = Vector3.Dot(topOffset, axis);
            float offTrack = (topOffset - axis * alongTrack).magnitude;
            float angle = Quaternion.Angle(propTransform.rotation, magazineWell.rotation * seatedLocalRotation);
            bool inCatchZone = alongTrack >= -0.005f && alongTrack <= railLength + railExitMargin && offTrack <= railCaptureRadius && angle <= railCaptureAngle;
            if (!railCatchArmed)
            {
                railCatchArmed = !inCatchZone;
                return;
            }
            if (inCatchZone)
            {
                // Relative to the controller: on the track the hand follows the magazine, so it can't be the source
                GetPoseIn(VRGlobals.vrPlayer.LeftHand.transform, propTransform, out railEntryHandPosition, out railEntryHandRotation);
                StartRail(pullingOut: false, startPosition: Mathf.Clamp(alongTrack, 0f, railLength));
            }
        }

        // On the track: the controller's travel along the well's axis moves the magazine; sideways it can't go
        private static void UpdateRail(bool gripReleased)
        {
            Vector3 axis = RailAxisWorld();
            Vector3 handDelta = VRGlobals.vrPlayer.LeftHand.transform.position - magazineWell.TransformPoint(handAtRailStartInWell);
            float handAlongTrack = Vector3.Dot(handDelta, axis);
            float handOffTrack = (handDelta - axis * handAlongTrack).magnitude;
            float trackPosition = railPositionAtStart + handAlongTrack;
            railBlend = Mathf.MoveTowards(railBlend, 1f, Time.deltaTime / Mathf.Max(railCaptureTime, 0.001f));

            if (railPullingOut)
            {
                if (gripReleased || handOffTrack > railLateralEscape)
                {
                    // Let go (or yanked sideways) before it came free: it stays in the well
                    CancelHold();
                    return;
                }
                railPosition = Mathf.Clamp(trackPosition, 0f, railLength);
                PlaceOnRail();
                if (trackPosition >= railLength)
                    ComeFreeOfWell();
                return;
            }

            if (handOffTrack > railLateralEscape || trackPosition > railLength + railExitMargin)
            {
                LeaveRail();
                return;
            }
            railPosition = Mathf.Clamp(trackPosition, 0f, railLength);
            PlaceOnRail();

            bool seated = railPosition <= 0f && railBlend >= 1f;
            if (seated)
            {
                if (heldSource == HeldMagazineSource.FromGun)
                {
                    // The gun's own magazine pushed back in: nothing changed in the inventory
                    PlayMagazineSound(heldMagazine, EInventorySoundType.drop, magazineWell.position);
                    Pulse(0.6f, 0.06f);
                    CancelHold();
                    return;
                }
                if (heldForFirearm.Item.GetCurrentMagazine() == null && heldForFirearm.CurrentOperation is FirearmController.Idling && TryInsert())
                    return;
            }

            if (gripReleased)
            {
                // Let go part way into the well: the gun's own magazine stays in it, a new one goes back to the rig
                if (heldSource == HeldMagazineSource.FromGun)
                    CancelHold();
                else
                    ReleaseHeldMagazine();
            }
        }

        // The magazine at railPosition on the track, eased in from where the hand held it when it was caught
        private static void PlaceOnRail()
        {
            Vector3 railPositionWorld = magazineWell.TransformPoint(seatedLocalPosition) + RailAxisWorld() * railPosition;
            Quaternion railRotationWorld = magazineWell.rotation * seatedLocalRotation;
            Transform propTransform = heldProp.transform;
            if (railBlend >= 1f || railPullingOut)
            {
                propTransform.SetPositionAndRotation(railPositionWorld, railRotationWorld);
                return;
            }
            Transform leftHand = VRGlobals.vrPlayer.LeftHand.transform;
            Vector3 handHeldPosition = leftHand.position + leftHand.rotation * railEntryHandPosition;
            Quaternion handHeldRotation = leftHand.rotation * railEntryHandRotation;
            float blend = Mathf.SmoothStep(0f, 1f, railBlend);
            propTransform.SetPositionAndRotation(Vector3.Lerp(handHeldPosition, railPositionWorld, blend),
                Quaternion.Slerp(handHeldRotation, railRotationWorld, blend));
        }

        // The magazine moves with the gun while it's in the well, and the rendered hand holds it there
        private static void StartRail(bool pullingOut, float startPosition)
        {
            onRail = true;
            railPullingOut = pullingOut;
            railPosition = startPosition;
            railPositionAtStart = startPosition;
            railBlend = pullingOut ? 1f : 0f;
            handAtRailStartInWell = magazineWell.InverseTransformPoint(VRGlobals.vrPlayer.LeftHand.transform.position);
            heldProp.transform.SetParent(magazineWell, worldPositionStays: true);
            LatchHand();
        }

        // Pulled down the track far enough: loose in the hand
        private static void ComeFreeOfWell()
        {
            onRail = false;
            railCatchArmed = false;
            magazineLeftWell = true;
            Transform propTransform = heldProp.transform;
            Transform handBone = HandBone();
            propTransform.SetParent(handBone, worldPositionStays: true);
            GetPoseIn(handBone, propTransform, out lastGrabPosition, out lastGrabRotation);
            hasLastGrabPose = true;
            if (heldReloadGrip != null)
            {
                freeHoldPosition = heldReloadGrip.oldMagazinePosition;
                freeHoldRotation = Quaternion.Euler(heldReloadGrip.oldMagazineRotation);
            }
            else
            {
                freeHoldPosition = lastGrabPosition;
                freeHoldRotation = lastGrabRotation;
            }
            ReleaseHandLatch();
            Pulse(0.5f, 0.04f);
            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] {heldMagazine.LocalizedShortName()} out of the well: hand-local position {lastGrabPosition.ToString("F3")} rotation {lastGrabRotation.eulerAngles.ToString("F1")}");
        }

        // A magazine being put in backed out of the track or was pulled off it sideways
        private static void LeaveRail()
        {
            onRail = false;
            railCatchArmed = false;
            heldProp.transform.SetParent(HandBone(), worldPositionStays: true);
            ReleaseHandLatch();
        }

        // The hand you see: the arm IK's wrist bone. A held magazine rides it rather than LeftHand, because LeftHand gets
        // the newest controller pose just before rendering, after the IK already placed the hand from the previous one:
        // attached to LeftHand the magazine ran a frame ahead of the hand (headset: "slightly lags in my hand").
        private static Transform HandBone()
        {
            LimbIK leftArmIk = VRGlobals.ikManager != null ? VRGlobals.ikManager.leftArmIk : null;
            if (leftArmIk != null && leftArmIk.solver != null && leftArmIk.solver.bone3 != null && leftArmIk.solver.bone3.transform != null)
                return leftArmIk.solver.bone3.transform;
            if (InitVRPatches.leftPalm != null)
                return InitVRPatches.leftPalm;
            return VRGlobals.vrPlayer.LeftHand.transform;
        }

        // A pose in a transform's frame without its scale (the grip data is in metres in the hand's frame)
        private static void GetPoseIn(Transform frame, Transform target, out Vector3 position, out Quaternion rotation)
        {
            Quaternion inverseFrame = Quaternion.Inverse(frame.rotation);
            position = inverseFrame * (target.position - frame.position);
            rotation = inverseFrame * target.rotation;
        }

        private static void SetPoseIn(Transform frame, Transform target, Vector3 position, Quaternion rotation)
        {
            target.SetPositionAndRotation(frame.position + frame.rotation * position, frame.rotation * rotation);
        }

        private static Vector3 RailAxisWorld()
        {
            return magazineWell.TransformDirection(railAxisInWell).normalized;
        }

        // The track out of the well, in the well bone's frame: the magazine's own length axis, seated. Needs propWellPointLocal
        // and seatedLocalRotation set. Tried and wrong on the M4A1 (headset): the well bone toward the magazine's middle
        // came out too upright (the bone isn't on the magazine's centre line), and the animation's insert path pulled to
        // the left (the clip still rotates the magazine into place over its last centimetres).
        private static Vector3 RailAxisInWell(GameObject prop, Vector3 seatedScale)
        {
            Vector3 towardMiddle = MagazineCenterInProp(prop) - propWellPointLocal;
            Vector3 axisInProp = TryGetMagazineLengthAxis(prop, out Vector3 lengthAxis) ? lengthAxis : towardMiddle;
            Vector3 axisInWell = seatedLocalRotation * Vector3.Scale(axisInProp, seatedScale);
            if (debugLog)
            {
                Vector3 middleInWell = seatedLocalRotation * Vector3.Scale(towardMiddle, seatedScale);
                Plugin.MyLog.LogInfo($"[VRMagazine] track {(axisInProp == lengthAxis ? "along the magazine's length" : "toward its middle")}, "
                    + $"{Vector3.Angle(axisInWell, middleInWell):0.#} deg off the well-to-middle direction");
            }
            return axisInWell.sqrMagnitude > 1e-8f ? axisInWell.normalized : Vector3.down;
        }

        // The longest side of the magazine body's mesh bounds (mags are modelled upright, so that's straight down the
        // magazine), in the prop's frame, pointing away from the well. The body is the biggest mesh; the rounds inside
        // are separate small ones. Bounds are mesh metadata, so this works on meshes that aren't readable.
        private static bool TryGetMagazineLengthAxis(GameObject prop, out Vector3 axisInProp)
        {
            axisInProp = Vector3.zero;
            MeshFilter body = null;
            float bodyVolume = 0f;
            foreach (MeshFilter meshFilter in prop.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter.sharedMesh == null)
                    continue;
                Vector3 size = Vector3.Scale(meshFilter.sharedMesh.bounds.size, meshFilter.transform.lossyScale);
                float volume = Mathf.Abs(size.x * size.y * size.z);
                if (volume > bodyVolume)
                {
                    bodyVolume = volume;
                    body = meshFilter;
                }
            }
            if (body == null)
                return false;

            Bounds bounds = body.sharedMesh.bounds;
            Vector3 bodySize = Vector3.Scale(bounds.size, body.transform.lossyScale);
            bodySize = new Vector3(Mathf.Abs(bodySize.x), Mathf.Abs(bodySize.y), Mathf.Abs(bodySize.z));
            Vector3 lengthInMesh = Vector3.forward;
            if (bodySize.x >= bodySize.y && bodySize.x >= bodySize.z)
                lengthInMesh = Vector3.right;
            else if (bodySize.y >= bodySize.z)
                lengthInMesh = Vector3.up;

            Transform propTransform = prop.transform;
            Vector3 axis = propTransform.InverseTransformDirection(body.transform.TransformDirection(lengthInMesh));
            Vector3 bodyCenterInProp = propTransform.InverseTransformPoint(body.transform.TransformPoint(bounds.center));
            if (Vector3.Dot(axis, bodyCenterInProp - propWellPointLocal) < 0f)
                axis = -axis;
            if (axis.sqrMagnitude < 1e-8f)
                return false;
            axisInProp = axis.normalized;
            return true;
        }

        private static Vector3 MagazineCenterInProp(GameObject prop)
        {
            Renderer[] renderers = prop.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
                return Vector3.zero;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return prop.transform.InverseTransformPoint(bounds.center);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The rendered hand on the magazine

        private static void LatchHand()
        {
            if (handLatch == null)
                handLatch = new GameObject("VRMagazineHandLatch").transform;
            Transform leftHand = VRGlobals.vrPlayer.LeftHand.transform;
            handLatch.SetParent(heldProp.transform, worldPositionStays: false);
            handLatch.SetPositionAndRotation(leftHand.position, leftHand.rotation);
            handLatched = true;
            handLatchReleasing = false;
            handLatchSnapping = false;
            AssertHandLatch();
        }

        // The animation holds the magazine at gripPosition / gripRotation in the hand's (IK target's) frame: move the
        // latched hand there, so the fingers wrap the magazine instead of sitting wherever the controller grabbed it
        private static void SnapHandToGrip(Vector3 gripPosition, Vector3 gripRotation)
        {
            if (!handLatched || handLatch == null)
                return;
            Transform propTransform = heldProp.transform;
            Quaternion handRotation = propTransform.rotation * Quaternion.Inverse(Quaternion.Euler(gripRotation));
            Vector3 handPosition = propTransform.position - handRotation * gripPosition;
            handSnapLocalPosition = propTransform.InverseTransformPoint(handPosition);
            handSnapLocalRotation = Quaternion.Inverse(propTransform.rotation) * handRotation;
            handLatchSnapping = true;
        }

        // The hand eases from the magazine back to the controller, then the arm goes back to LeftHand
        private static void ReleaseHandLatch()
        {
            if (!handLatched)
                return;
            handLatchReleasing = true;
            handLatchSnapping = false;
            if (handLatch != null)
                handLatch.SetParent(null, worldPositionStays: true);
        }

        private static void ReleaseHandLatchNow()
        {
            if (handLatch != null)
                handLatch.SetParent(null, worldPositionStays: true);
            if (handLatched && VRGlobals.ikManager != null && VRGlobals.ikManager.leftArmIk != null && VRGlobals.vrPlayer != null
                && VRGlobals.ikManager.leftArmIk.solver.target == handLatch)
                VRGlobals.ikManager.leftArmIk.solver.target = VRGlobals.vrPlayer.LeftHand.transform;
            handLatched = false;
            handLatchReleasing = false;
        }

        // Re-asserted every frame: the two-handing edges in VRPlayerManager set the target back to LeftHand
        private static void AssertHandLatch()
        {
            if (VRGlobals.ikManager == null || VRGlobals.ikManager.leftArmIk == null || handLatch == null)
                return;
            if (VRGlobals.ikManager.leftArmIk.solver.target != handLatch)
                VRGlobals.ikManager.leftArmIk.solver.target = handLatch;
        }

        private static void UpdateHandLatch()
        {
            if (!handLatched)
                return;
            if (VRGlobals.vrPlayer == null || handLatch == null)
            {
                ReleaseHandLatchNow();
                return;
            }
            if (handLatchReleasing)
            {
                Transform leftHand = VRGlobals.vrPlayer.LeftHand.transform;
                float ease = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(handReleaseTime / 3f, 0.001f));
                handLatch.position = Vector3.Lerp(handLatch.position, leftHand.position, ease);
                handLatch.rotation = Quaternion.Slerp(handLatch.rotation, leftHand.rotation, ease);
                if ((handLatch.position - leftHand.position).sqrMagnitude < 0.003f * 0.003f && Quaternion.Angle(handLatch.rotation, leftHand.rotation) < 2f)
                {
                    ReleaseHandLatchNow();
                    return;
                }
            }
            else if (handLatchSnapping)
            {
                float ease = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(handSnapTime / 3f, 0.001f));
                handLatch.localPosition = Vector3.Lerp(handLatch.localPosition, handSnapLocalPosition, ease);
                handLatch.localRotation = Quaternion.Slerp(handLatch.localRotation, handSnapLocalRotation, ease);
            }
            AssertHandLatch();
        }

        private static void ReleaseHeldMagazine()
        {
            if (heldSource == HeldMagazineSource.FromPouch)
            {
                // Never left the rig
                DestroyProp();
                ClearHold();
                return;
            }

            if (!magazineLeftWell || !(heldForFirearm.CurrentOperation is FirearmController.Idling))
            {
                CancelHold();
                return;
            }

            Magazine magazine = heldMagazine;
            FirearmController firearmController = heldForFirearm;
            Vector3 handPosition = heldProp.transform.position;
            Quaternion handRotation = heldProp.transform.rotation;
            bool atChest = InChestPouchZone(OffHandPalm());

            // At the chest: into the rig, or the ground if it's full. Anywhere else: the ground, or the rig where
            // nothing can be dropped (the hideout).
            if (atChest && TryBuildMoveToRig(magazine, out OperationResult<MoveResult> rigMove))
            {
                CommitHold();
                RunMoveToRig(magazine, firearmController, rigMove, handPosition);
            }
            else if (CanDropToGround(magazine))
            {
                CommitHold();
                RunDrop(magazine, firearmController, handPosition, handRotation, OffHandWorldVelocity());
            }
            else if (!atChest && TryBuildMoveToRig(magazine, out rigMove))
            {
                CommitHold();
                RunMoveToRig(magazine, firearmController, rigMove, handPosition);
            }
            else
            {
                CancelHold();
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Inventory moves (each starts Tarkov's operation; TryForceExpectedOperation finishes it)

        private static bool TryInsert()
        {
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            Slot magazineSlot = heldForFirearm.Item.GetMagazineSlot();
            OperationResult<MoveResult> moveResult = ItemManipulator.Move(heldMagazine, magazineSlot.CreateItemAddress(), inventoryController, simulate: true);
            if (moveResult.Failed || !moveResult.Value.CanExecute(inventoryController))
            {
                if (debugLog)
                    Plugin.MyLog.LogWarning($"[VRMagazine] can't seat {heldMagazine.LocalizedShortName()}: {(moveResult.Failed ? moveResult.Error.ToString() : "can't execute")}");
                return false;
            }

            Vector3 wellPosition = magazineWell.position;
            Magazine magazine = heldMagazine;
            FirearmController firearmController = heldForFirearm;
            CommitHold();

            ExpectOperation(ExpectedOperation.Insert, firearmController);
            inventoryController.TryRunNetworkTransaction(moveResult);
            TryForceExpectedOperation();

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] seated {magazine.LocalizedShortName()}");
            PlayMagazineSound(magazine, EInventorySoundType.drop, wellPosition);
            Pulse(0.8f, 0.07f);
            return true;
        }

        // Tarkov's unload (FirearmHandsInputTranslator.UnloadMag): the smallest rig/pocket grid that fits
        private static bool TryBuildMoveToRig(Magazine magazine, out OperationResult<MoveResult> moveResult)
        {
            moveResult = default;
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            GridItemAddress bestAddress = null;
            int bestGridSize = int.MaxValue;
            foreach (var grid in inventoryController.Inventory.Equipment.GetPrioritizedGridsForUnloadedObject())
            {
                GridItemAddress address = grid.FindLocationForItem(magazine);
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

            moveResult = ItemManipulator.Move(magazine, bestAddress, inventoryController, simulate: true);
            return !moveResult.Failed && moveResult.Value.CanExecute(inventoryController);
        }

        private static void RunMoveToRig(Magazine magazine, FirearmController firearmController, OperationResult<MoveResult> moveResult, Vector3 soundPosition)
        {
            ExpectOperation(ExpectedOperation.PullOut, firearmController);
            VRGlobals.player.InventoryController.TryRunNetworkTransaction(moveResult);
            TryForceExpectedOperation();

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] {magazine.LocalizedShortName()} put in the rig");
            PlayMagazineSound(magazine, EInventorySoundType.drop, soundPosition);
            Pulse(0.4f, 0.05f);
        }

        // Tarkov only drops to the ground in a raid (ItemUiContext.UnloadWeapon); in the hideout FIKA's item sync also
        // has no world to report to and throws every physics step
        private static bool CanDropToGround(Magazine magazine)
        {
            GameWorld gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null || gameWorld is HideoutGameWorld)
                return false;
            return VRGlobals.player.InventoryController.CanThrow(magazine);
        }

        private static void RunDrop(Magazine magazine, FirearmController firearmController, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            pendingDropMagazine = magazine;
            pendingDropPosition = position;
            pendingDropRotation = rotation;
            pendingDropVelocity = velocity;
            pendingDropDeadline = Time.time + 3f;

            ExpectOperation(ExpectedOperation.PullOut, firearmController);
            VRGlobals.player.InventoryController.ThrowItem(magazine, false, null);
            TryForceExpectedOperation();

            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] {magazine.LocalizedShortName()} dropped");
        }

        // B double-tap (Y left-handed): the magazine falls out of the well (into the rig in the hideout). Returns false
        // when it doesn't apply, so B keeps its old job on that gun.
        public static bool TryReleaseMagazine()
        {
            // The gun's magazine is already in the off hand. A new one in the off hand is fine: drop the old, then seat.
            if (HoldingGunMagazine)
                return true;
            if (!TryGetMagazineGun(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None
                || ChestHang.Detached || weapon.MalfState.State == Weapon.EMalfunctionState.Feed)
                return false;
            Magazine magazine = weapon.GetCurrentMagazine();
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazine == null || magazineView == null || magazineView.ItemView == null)
                return false;

            if (CanDropToGround(magazine))
            {
                // Falls out of the well with the gun's motion
                RunDrop(magazine, firearmController, magazineView.ItemView.position, magazineView.ItemView.rotation,
                    Vector3.down * 0.5f + VRGlobals.player.Velocity);
                return true;
            }
            if (TryBuildMoveToRig(magazine, out OperationResult<MoveResult> rigMove))
            {
                RunMoveToRig(magazine, firearmController, rigMove, magazineView.ItemView.position);
                return true;
            }
            return false;
        }

        // Called from the GameWorld.ThrowItem postfix: put our dropped magazine where the hand let go of it
        public static void PlaceDroppedMagazine(Item item, LootItem lootItem)
        {
            if (item == null || lootItem == null || item != pendingDropMagazine)
                return;
            pendingDropMagazine = null;
            if (Time.time > pendingDropDeadline)
                return;

            lootItem.transform.SetPositionAndRotation(pendingDropPosition, pendingDropRotation);
            Rigidbody rigidBody = lootItem.RigidBody;
            if (rigidBody != null)
            {
                rigidBody.velocity = pendingDropVelocity;
                rigidBody.angularVelocity = UnityEngine.Random.insideUnitSphere * dropAngularSpeed;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Finishing Tarkov's operation the same frame (skips its animation; see firearm-operations-reference)

        private static void ExpectOperation(ExpectedOperation operation, FirearmController firearmController)
        {
            expectedOperation = operation;
            expectedForFirearm = firearmController;
            expectedOperationDeadline = Time.time + 2f;
            expectedOperationStartFrame = Time.frameCount;
        }

        private static void TryForceExpectedOperation()
        {
            FirearmController firearmController = VRGlobals.firearmController;
            if (firearmController == null || firearmController != expectedForFirearm)
            {
                ClearPending();
                expectedOperation = ExpectedOperation.None;
                return;
            }

            try
            {
                FirearmController.FirearmOperation operation = firearmController.CurrentOperation;
                if (expectedOperation == ExpectedOperation.PullOut && operation is FirearmController.PullOutMagOperation)
                {
                    // Visible again right before RemoveMod returns it to the pool, so the pool gets it as it found it
                    RestorePendingView();
                    // BSG's own skip: runs the remaining mag-out callbacks in order, then plays idle
                    firearmController.FastForwardCurrentState();
                    ReturnPendingProp();
                    FinishForcedOperation(firearmController);
                }
                else if (expectedOperation == ExpectedOperation.Insert && operation is FirearmController.InsertMagOperation insertOperation)
                {
                    // Not BSG's FastForward: it can switch to idle twice when there's no round to chamber
                    insertOperation.OnMagAppeared();
                    ReturnPendingProp();
                    insertOperation.OnMagInsertedToWeapon();
                    if (insertOperation.State != EFT.Player.EOperationState.Finished)
                        insertOperation.OnAddAmmoInChamber();
                    firearmController.FirearmsAnimator.Animator.Play(firearmController.FirearmsAnimator.FullIdleStateName, 1, 0.1f);
                    FinishForcedOperation(firearmController);
                }
                else if (Time.time > expectedOperationDeadline)
                {
                    Plugin.MyLog.LogWarning($"[VRMagazine] expected {expectedOperation} never started (current: {operation?.GetType().Name ?? "none"})");
                    ClearPending();
                    expectedOperation = ExpectedOperation.None;
                }
            }
            catch (System.Exception exception)
            {
                Plugin.MyLog.LogError($"[VRMagazine] forcing {expectedOperation} failed: {exception}");
                ClearPending();
                expectedOperation = ExpectedOperation.None;
            }
        }

        private static void RestorePendingView()
        {
            if (pendingHiddenView != null)
                pendingHiddenView.gameObject.SetActive(true);
            pendingHiddenView = null;
        }

        private static void ReturnPendingProp()
        {
            ReturnPropToPool(pendingProp);
            pendingProp = null;
        }

        // The move never happened: the magazine is still in the gun, show it again
        private static void ClearPending()
        {
            RestorePendingView();
            ReturnPendingProp();
        }

        // ReloadFloat and the "reload in progress" block only clear on Idling's IdleStart event, which the skipped
        // animation never sends
        private static void FinishForcedOperation(FirearmController firearmController)
        {
            if (firearmController.CurrentOperation is FirearmController.Idling idling)
                idling.OnIdleStartEvent();
            if (debugLog)
                Plugin.MyLog.LogInfo($"[VRMagazine] finished {expectedOperation} after {Time.frameCount - expectedOperationStartFrame} frames");
            expectedOperation = ExpectedOperation.None;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The magazine in the hand

        // Tarkov's own magazine model, borrowed from its pool. Colliders off (the weapon collision proxy copies nearby
        // colliders) and drawn on the gun's layer; both are put back before it returns to the pool.
        private static GameObject CreateProp(Magazine magazine)
        {
            ObjectsFactory objectsFactory = Singleton<ObjectsFactory>.Instance;
            if (objectsFactory == null)
                return null;
            GameObject prop = objectsFactory.CreateItem(magazine, isAnimated: false);
            if (prop == null)
                return null;

            disabledPropColliders.Clear();
            foreach (Collider collider in prop.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled)
                    continue;
                collider.enabled = false;
                disabledPropColliders.Add(collider);
            }
            propOriginalLayers.Clear();
            int gunLayer = LayerMask.NameToLayer("Player");
            foreach (Transform child in prop.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                propOriginalLayers[child] = child.gameObject.layer;
                child.gameObject.layer = gunLayer;
            }
            prop.SetActive(true);
            return prop;
        }

        private static void DestroyProp()
        {
            ReturnPropToPool(heldProp);
            heldProp = null;
        }

        private static void ReturnPropToPool(GameObject prop)
        {
            if (prop == null)
                return;
            // The hand latch rides the magazine on the track; it must never go back to the pool with it
            if (handLatch != null && handLatch.IsChildOf(prop.transform))
                handLatch.SetParent(null, worldPositionStays: true);
            foreach (Collider collider in disabledPropColliders)
            {
                if (collider != null)
                    collider.enabled = true;
            }
            disabledPropColliders.Clear();
            foreach (KeyValuePair<Transform, int> originalLayer in propOriginalLayers)
            {
                if (originalLayer.Key != null)
                    originalLayer.Key.gameObject.layer = originalLayer.Value;
            }
            propOriginalLayers.Clear();
            prop.transform.SetParent(null, worldPositionStays: false);
            AssetPoolObject.ReturnToPool(prop);
        }

        // Where the mag well sits in the prop's frame when seated (ContainerCollectionView.SlotView.InsertItem)
        private static Vector3 SeatedWellPointLocal(GameObject prop)
        {
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            Vector3 seatedPosition = modPlacer != null ? modPlacer.ModPosition : Vector3.zero;
            Quaternion seatedRotation = modPlacer != null ? Quaternion.Euler(modPlacer.ModRotation) : Quaternion.Euler(90f, 0f, 0f);
            Vector3 seatedScale = SeatedScale(prop);
            Vector3 wellInPropFrame = Quaternion.Inverse(seatedRotation) * -seatedPosition;
            return new Vector3(wellInPropFrame.x / seatedScale.x, wellInPropFrame.y / seatedScale.y, wellInPropFrame.z / seatedScale.z);
        }

        private static Vector3 SeatedScale(GameObject prop)
        {
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            if (modPlacer == null || modPlacer.ModScale.x == 0f || modPlacer.ModScale.y == 0f || modPlacer.ModScale.z == 0f)
                return Vector3.one;
            return modPlacer.ModScale;
        }

        private static void SetWorldScale(Transform target, Vector3 worldScale)
        {
            Vector3 parentScale = target.parent != null ? target.parent.lossyScale : Vector3.one;
            target.localScale = new Vector3(worldScale.x / parentScale.x, worldScale.y / parentScale.y, worldScale.z / parentScale.z);
        }

        private static void RestoreGunMagazineView()
        {
            if (hiddenGunMagazineView != null)
                hiddenGunMagazineView.gameObject.SetActive(true);
            hiddenGunMagazineView = null;
        }

        // Committed to an inventory move: the prop and the hidden gun magazine go to the expected operation, which
        // cleans them up when Tarkov finishes (or puts the magazine back if it never does)
        private static void CommitHold()
        {
            pendingProp = heldProp;
            pendingHiddenView = hiddenGunMagazineView;
            heldProp = null;
            hiddenGunMagazineView = null;
            ClearHold();
        }

        // Puts everything back as it was: the gun's magazine visible again, nothing in the hand
        private static void CancelHold()
        {
            RestoreGunMagazineView();
            DestroyProp();
            ClearHold();
        }

        private static void ClearHold()
        {
            ReleaseHandLatch();
            onRail = false;
            heldSource = HeldMagazineSource.None;
            heldMagazine = null;
            heldForFirearm = null;
            heldReloadGrip = null;
            magazineWell = null;
            magazineLeftWell = false;
            OwnsOffHand = false;
        }

        // Called from HandsInteractionController.LateUpdate right after the off-hand finger update, so it's the last write
        // to those bones this frame. Eases in on a grab and back out to whatever the hand was doing on release.
        public static void ApplyHeldFingerPose()
        {
            Vector3[] pose = null;
            if (heldSource == HeldMagazineSource.FromGun)
                pose = MAGAZINE_PULL_FINGERS;
            else if (heldSource == HeldMagazineSource.FromPouch)
                pose = MAGAZINE_INSERT_FINGERS;
            if (pose != null)
                fingerPoseShown = pose;

            float target = pose != null && magazineFingers ? 1f : 0f;
            fingerWeight = Mathf.Lerp(fingerWeight, target, 1f - Mathf.Exp(-fingerBlendRate * Time.deltaTime));
            if (fingerWeight < 0.01f || fingerPoseShown == null)
                return;

            Transform leftPalm = InitVRPatches.leftPalm;
            if (leftPalm == null)
                return;
            if (leftPalmBones == null || leftPalmBones.Length == 0 || leftPalmBones[0] != leftPalm)
                leftPalmBones = leftPalm.GetComponentsInChildren<Transform>(true);

            int boneCount = Mathf.Min(leftPalmBones.Length, fingerPoseShown.Length);
            for (int i = 1; i < boneCount; i++)
                leftPalmBones[i].localRotation = Quaternion.Slerp(leftPalmBones[i].localRotation, Quaternion.Euler(fingerPoseShown[i]), fingerWeight);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Hand and zones

        // The rendered hand's knuckle line (the loot pointer's palm). The LeftHand rig transform sits at the wrist.
        private static Vector3 OffHandPalm()
        {
            if (VRGlobals.handsInteractionController != null)
                return VRGlobals.handsInteractionController.PalmOrigin(dominant: false);
            Transform leftHand = VRGlobals.vrPlayer.LeftHand.transform;
            return leftHand.position + leftHand.forward * GunInteractionController.interactProbeForward;
        }

        private static Vector3 OffHandWorldVelocity()
        {
            // Controller velocity is tracking-space only; add the body's so a magazine dropped while walking carries on
            Vector3 trackingVelocity = ControllerVelocity.GetSteamVRVelocity(OffHandSource());
            Vector3 handVelocity = VRGlobals.vrOffsetter != null ? VRGlobals.vrOffsetter.transform.TransformDirection(trackingVelocity) : trackingVelocity;
            return handVelocity + VRGlobals.player.Velocity;
        }

        private static bool TryGetGunMagazineCenter(FirearmController firearmController, Slot magazineSlot, out Vector3 magazineCenter)
        {
            magazineCenter = Vector3.zero;
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazineView == null || magazineView.ItemView == null)
                return false;
            if (cachedRendererRoot != magazineView.ItemView)
            {
                cachedRendererRoot = magazineView.ItemView;
                cachedRenderers = magazineView.ItemView.GetComponentsInChildren<Renderer>();
            }
            if (cachedRenderers == null || cachedRenderers.Length == 0)
            {
                magazineCenter = magazineView.ItemView.position;
                return true;
            }
            Bounds bounds = cachedRenderers[0].bounds;
            for (int i = 1; i < cachedRenderers.Length; i++)
            {
                if (cachedRenderers[i] != null)
                    bounds.Encapsulate(cachedRenderers[i].bounds);
            }
            magazineCenter = bounds.center;
            return true;
        }

        private static bool TryGetChestPouchCenter(out Vector3 pouchCenter)
        {
            pouchCenter = Vector3.zero;
            if (!ChestHang.HeadPose(out _, out _))
                return false;
            Quaternion headYaw = ChestHang.HeadYaw(out Vector3 headPosition);
            Vector3 offset = chestPouchOffset;
            if (VRSettings.GetLeftHandedMode())
                offset.x = -offset.x;
            pouchCenter = headPosition + headYaw * offset;
            return true;
        }

        private static SteamVR_Input_Sources OffHandSource()
        {
            return VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.RightHand : SteamVR_Input_Sources.LeftHand;
        }

        private static void Pulse(float amplitude, float duration, float delay = 0f)
        {
            if (haptics)
                VRInput.Haptic.Execute(delay, duration, 120f, amplitude, OffHandSource());
        }

        private static void PulseGunHand(float amplitude, float duration)
        {
            if (!haptics)
                return;
            SteamVR_Input_Sources gunHand = VRSettings.GetLeftHandedMode() ? SteamVR_Input_Sources.LeftHand : SteamVR_Input_Sources.RightHand;
            VRInput.Haptic.Execute(0f, duration, 120f, amplitude, gunHand);
        }

        private static void PlayMagazineSound(Item magazine, EInventorySoundType soundType, Vector3 position)
        {
            if (!sounds || magazine == null)
                return;
            GUISounds guiSounds = Singleton<GUISounds>.Instance;
            BetterAudio betterAudio = MonoBehaviourSingleton<BetterAudio>.Instance;
            if (guiSounds == null || betterAudio == null)
                return;
            AudioClip clip = guiSounds.GetItemClip(magazine.ItemSound, soundType);
            if (clip != null)
                betterAudio.TryPlayAtPoint(out _, position, clip, BetterAudio.AudioSourceGroupType.Character, 25, soundVolume);
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Debug spheres

        private static void UpdateZoneViz()
        {
            FirearmController firearmController = null;
            Weapon weapon = null;
            Slot magazineSlot = null;
            bool show = debugZones && TryGetMagazineGun(out firearmController, out weapon, out magazineSlot);
            if (!show)
            {
                SetZoneViz(ref grabZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref pouchZoneViz, false, Vector3.zero, 0f, Color.clear);
                SetZoneViz(ref wellZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }

            Vector3 palm = OffHandPalm();
            bool hasMagazineCenter = TryGetGunMagazineCenter(firearmController, magazineSlot, out Vector3 magazineCenter);
            bool inGrabZone = InGunMagazineGrabZone(firearmController, weapon, magazineSlot, palm);
            SetZoneViz(ref grabZoneViz, hasMagazineCenter && !HoldingMagazine, magazineCenter, magazineGrabRadius,
                inGrabZone ? new Color(0f, 1f, 0f, 0.3f) : new Color(0f, 0.4f, 1f, 0.3f));

            bool hasPouchCenter = TryGetChestPouchCenter(out Vector3 pouchCenter);
            SetZoneViz(ref pouchZoneViz, hasPouchCenter, pouchCenter, chestPouchRadius,
                InChestPouchZone(palm) ? new Color(0f, 1f, 0f, 0.25f) : new Color(1f, 0.9f, 0f, 0.25f));

            // The top of the track while holding a magazine: green on it, blue off it
            bool showTrack = HoldingMagazine && magazineWell != null;
            Vector3 trackTop = showTrack ? magazineWell.position + RailAxisWorld() * railLength : Vector3.zero;
            SetZoneViz(ref wellZoneViz, showTrack, trackTop, railCaptureRadius, onRail ? new Color(0f, 1f, 0.4f, 0.4f) : new Color(0f, 0.4f, 1f, 0.4f));
        }

        private static void SetZoneViz(ref GameObject sphere, bool show, Vector3 position, float radius, Color color)
        {
            if (!show)
            {
                if (sphere != null)
                    sphere.SetActive(false);
                return;
            }
            if (sphere == null)
            {
                sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "VRMagazineZone";
                UnityEngine.Object.Destroy(sphere.GetComponent<Collider>());
                Renderer sphereRenderer = sphere.GetComponent<Renderer>();
                sphereRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                sphereRenderer.receiveShadows = false;
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                    shader = Shader.Find("Standard");
                sphereRenderer.material = new Material(shader);
            }
            sphere.SetActive(true);
            sphere.transform.position = position;
            sphere.transform.localScale = Vector3.one * (radius * 2f);
            sphere.GetComponent<Renderer>().material.color = color;
        }
    }
}
