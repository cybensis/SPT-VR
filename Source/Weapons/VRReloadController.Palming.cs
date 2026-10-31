using System.Collections.Generic;
using Diz.LanguageExtensions;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Two magazines in one hand: with a new magazine held, B at the gun's magazine takes that one too and pulls it out, and
    // the new one goes in while the old stays in the hand until it lets go. Tarkov has the animation (*_reload_admin, with its
    // mod_magazine_new bone) but never finished it: ReloadExternalMagOperation.SwitchToReloadWithNewMag only logs "Not
    // Implemented". The hand poses here are read from that clip; nothing plays it.
    //
    // The gun holds one magazine, so the old one moves to the rig the moment it comes free of the well, while its model stays
    // in the hand. Letting go at the chest leaves it there; anywhere else throws it from the rig to the ground.
    public static partial class VRReloadController
    {
        public static bool palming = true;                  // false = B with a new magazine in hand drops the gun's, as before
        public static float palmMaxThickness = 0.035f;      // m; a drum or a MAG5 doesn't fit in one hand with another magazine
        public static float palmMaxLength = 0.26f;          // m

        private sealed class PalmHold
        {
            public MagazineHold newMagazine;
            public MagazineHold oldMagazine;
            public Vector3[] fingers;
        }

        // By animation set (m4a1_reload_admin at 1.6 s). Only 8 of the 127 magazine guns have the clip.
        private static readonly Dictionary<string, PalmHold> palmHolds = new Dictionary<string, PalmHold>
        {
            {
                "m4a1", new PalmHold
                {
                    newMagazine = new MagazineHold { position = new Vector3(-0.158f, -0.206f, 0.069f), rotation = new Vector3(74.9f, 299.8f, 179.8f), magazineLength = 0.19f },
                    oldMagazine = new MagazineHold { position = new Vector3(-0.271f, -0.036f, 0.109f), rotation = new Vector3(352.3f, 300.2f, 180.2f), magazineLength = 0.19f },
                    fingers = new[]
                    {
                        Vector3.zero,
                        new Vector3(74.8f, 113.1f, 87.0f), new Vector3(357.7f, 359.5f, 5.7f), new Vector3(358.7f, 0.5f, 10.9f),      // thumb
                        new Vector3(347.0f, 335.5f, 29.3f), new Vector3(360.0f, 360.0f, 18.6f), new Vector3(360.0f, 359.9f, 52.5f),   // index
                        new Vector3(344.2f, 329.5f, 57.4f), new Vector3(354.0f, 357.9f, 33.9f), new Vector3(349.1f, 359.8f, 0.9f),    // middle
                        new Vector3(342.4f, 313.0f, 78.2f), new Vector3(354.2f, 357.4f, 24.0f), new Vector3(350.6f, 359.9f, 0.8f),    // ring
                        new Vector3(343.3f, 340.0f, 88.2f), new Vector3(348.6f, 3.9f, 15.4f), new Vector3(350.4f, 359.9f, 16.6f),     // pinky
                    },
                }
            },
        };

        private static GameObject palmedProp;
        private static Magazine palmedMagazine;
        private static FirearmController palmedFor;
        private static HeldMagazineSource palmedSource;
        private static PalmHold activePalmHold;
        private static bool palmedIsOld;                    // false = the new one, waiting while the hand pulls the gun's out
        private static bool palmedLetGo;                    // the grip opened before the old one's move to the rig had landed
        private static Vector3 palmedPosition;              // where it sits in the hand
        private static Quaternion palmedRotation;

        private static bool HoldingPalmedMagazine => palmedProp != null;

        private static bool MagazineFitsPalm(GameObject magazineObject, Vector3 wellPointInObject)
        {
            return TryGetMagazineShape(magazineObject, wellPointInObject, out _, out float length, out float thinnestSide)
                && length <= palmMaxLength && thinnestSide <= palmMaxThickness;
        }

        private static void PoseForThisMagazine(MagazineHold hold, GameObject prop, Vector3 wellPointInProp, out Vector3 position, out Quaternion rotation)
        {
            if (TryGetMagazineShape(prop, wellPointInProp, out Vector3 lengthAxis, out float length, out _))
                hold = MoveAlongMagazine(hold, lengthAxis, length);
            position = hold.position;
            rotation = Quaternion.Euler(hold.rotation);
        }

        //-----------------------------------------------------------------------------------------------------------------------------

        // B with a new magazine in the hand, at the gun's magazine. False = it doesn't apply, B drops the magazine as usual.
        private static bool TryStartPalming()
        {
            if (!palming || !VRSettings.GetManualReloadOn() || heldSource != HeldMagazineSource.FromPouch || onRail || HoldingPalmedMagazine)
                return false;
            if (!CanStartGrab(out FirearmController firearmController, out Weapon weapon, out Slot magazineSlot))
                return false;
            if (!palmHolds.TryGetValue(AnimationFamily(firearmController), out PalmHold palmHold))
                return false;
            if (weapon.MalfState.State == Weapon.EMalfunctionState.Feed || !InGunMagazineGrabZone(firearmController, weapon, magazineSlot, OffHandPalm()))
                return false;

            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(magazineSlot);
            if (magazineView == null || magazineView.ItemView == null || magazineView.Bone == null)
                return false;
            if (!MagazineFitsPalm(heldProp, propWellPointLocal)
                || !MagazineFitsPalm(magazineView.ItemView.gameObject, magazineView.ItemView.InverseTransformPoint(magazineView.Bone.position)))
                return false;

            PoseForThisMagazine(palmHold.newMagazine, heldProp, propWellPointLocal, out palmedPosition, out palmedRotation);
            palmedProp = heldProp;
            palmedMagazine = heldMagazine;
            palmedSource = heldSource;
            palmedFor = firearmController;
            palmedIsOld = false;
            palmedLetGo = false;
            activePalmHold = palmHold;
            heldProp = null;
            ClearHold();

            GrabFromGun(firearmController, weapon, magazineSlot, takenWithRelease: false);
            if (!HoldingGunMagazine)
            {
                ReturnPalmedNewToHold();
                return false;
            }

            // The hand on the old magazine the way the clip has it, the new one riding along in the palm
            PoseForThisMagazine(palmHold.oldMagazine, heldProp, propWellPointLocal, out Vector3 oldPosition, out Quaternion oldRotation);
            heldReloadGrip = new ReloadGrip { pull = new MagazineHold { position = oldPosition, rotation = oldRotation.eulerAngles } };
            magazineHandFingers = palmHold.fingers;
            if (snapHandToGrip)
                SnapHandToGrip(oldPosition, oldRotation.eulerAngles);
            Plugin.MyLog.LogInfo($"[VRReload] palming {palmedMagazine.LocalizedShortName()}, pulling {heldMagazine.LocalizedShortName()}");
            return true;
        }

        // The old magazine came free of the well: it moves to the rig and stays in the hand, the new one is the one going in
        private static void FinishPalmPull()
        {
            Magazine oldMagazine = heldMagazine;
            GameObject oldProp = heldProp;
            FirearmController firearmController = heldForFirearm;

            if (!TryBuildMoveToRig(oldMagazine, out OperationResult<MoveResult> moveResult))
            {
                // No room in the rig: it can't be kept, it falls from the hand (or stays in the gun where nothing can drop)
                onRail = false;
                magazineLeftWell = true;
                ReleaseHandLatch();
                if (CanDropToGround(oldMagazine))
                {
                    Vector3 handPosition = oldProp.transform.position;
                    Quaternion handRotation = oldProp.transform.rotation;
                    CommitHold();
                    RunDrop(oldMagazine, firearmController, handPosition, handRotation, OffHandWorldVelocity());
                    PlayGunSound(firearmController, "MagOut");
                }
                else
                {
                    CancelHold();
                }
                ReturnPalmedNewToHold();
                return;
            }

            oldProp.transform.SetParent(HandBone(), worldPositionStays: true);
            PoseForThisMagazine(activePalmHold.oldMagazine, oldProp, propWellPointLocal, out Vector3 oldPosition, out Quaternion oldRotation);

            // The gun's model of it stays hidden until Tarkov removes it (as CommitHold, but this prop stays in the hand)
            pendingHiddenView = hiddenGunMagazineView;
            hiddenGunMagazineView = null;
            heldProp = null;
            ReleaseHandLatch();
            ExpectOperation(ExpectedOperation.PullOut, firearmController);
            VRGlobals.player.InventoryController.TryRunNetworkTransaction(moveResult);
            TryForceExpectedOperation();

            GameObject newProp = palmedProp;
            Magazine newMagazine = palmedMagazine;
            Vector3 newPosition = palmedPosition;
            Quaternion newRotation = palmedRotation;
            palmedProp = oldProp;
            palmedMagazine = oldMagazine;
            palmedIsOld = true;
            palmedPosition = oldPosition;
            palmedRotation = oldRotation;

            // In the clip the new magazine doesn't move in the hand on its way in: the wrist turns it to the well
            ArmNewMagazine(firearmController, newMagazine, newProp, palmedSource);
            freeHoldPosition = newPosition;
            freeHoldRotation = newRotation;
            magazineHandFingers = activePalmHold.fingers;

            PlayGunSound(firearmController, "MagOut");
            Pulse(0.5f, 0.04f);
            Plugin.MyLog.LogInfo($"[VRReload] {oldMagazine.LocalizedShortName()} out and in the hand, {newMagazine.LocalizedShortName()} next");
        }

        // A new magazine as the held one, off the track, its way into this gun worked out (HoldNewMagazine's second half)
        private static void ArmNewMagazine(FirearmController firearmController, Magazine magazine, GameObject prop, HeldMagazineSource source)
        {
            ContainerCollectionView.SlotView magazineView = firearmController.Firearms.ContainerCollectionView.GetViewForSlot(firearmController.Item.GetMagazineSlot());
            ModPlacer modPlacer = prop.GetComponent<ModPlacer>();
            seatedLocalPosition = modPlacer != null ? modPlacer.ModPosition : Vector3.zero;
            seatedLocalRotation = modPlacer != null ? Quaternion.Euler(modPlacer.ModRotation) : Quaternion.Euler(90f, 0f, 0f);
            propWellPointLocal = SeatedWellPointLocal(prop);
            railAxisInWell = RailAxisInWell(prop, SeatedScale(prop));
            heldReloadGrip = FindReloadGrip(firearmController, magazine, prop, propWellPointLocal);

            heldSource = source;
            heldMagazine = magazine;
            heldForFirearm = firearmController;
            heldProp = prop;
            magazineWell = magazineView != null ? magazineView.Bone : null;
            magazineLeftWell = true;
            onRail = false;
            // It's right at the well already: it has to leave before the track can catch it
            railCatchArmed = false;
        }

        // The pull it was waiting on ended without the old magazine coming free: back to an ordinary new magazine in the hand
        private static void ReturnPalmedNewToHold()
        {
            GameObject prop = palmedProp;
            Magazine magazine = palmedMagazine;
            HeldMagazineSource source = palmedSource;
            FirearmController firearmController = palmedFor;
            palmedProp = null;
            palmedMagazine = null;
            palmedFor = null;
            if (prop == null || magazine == null || firearmController == null)
            {
                ReturnPropToPool(prop);
                return;
            }

            ArmNewMagazine(firearmController, magazine, prop, source);
            if (heldReloadGrip?.insert != null)
            {
                freeHoldPosition = heldReloadGrip.insert.position;
                freeHoldRotation = Quaternion.Euler(heldReloadGrip.insert.rotation);
                magazineHandFingers = heldReloadGrip.insert.fingers ?? MAGAZINE_INSERT_FINGERS;
            }
            else
            {
                GetPoseIn(HandBone(), prop.transform, out freeHoldPosition, out freeHoldRotation);
                magazineHandFingers = MAGAZINE_INSERT_FINGERS;
            }
        }

        // Every frame, after the held magazine's update
        private static void UpdatePalmedMagazine(bool gripReleased)
        {
            if (palmedMagazine == null)
                return;
            if (palmedProp == null)
            {
                ForgetPalmedMagazine();
                return;
            }

            if (!palmedIsOld && !HoldingGunMagazine)
            {
                ReturnPalmedNewToHold();
                if (gripReleased)
                    ReleaseHeldMagazine();
                return;
            }

            if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.firearmController == null
                || VRGlobals.firearmController != palmedFor)
            {
                DropPalmedModel();
                return;
            }

            Transform handBone = HandBone();
            Transform propTransform = palmedProp.transform;
            if (propTransform.parent != handBone)
                propTransform.SetParent(handBone, worldPositionStays: true);
            float ease = 1f - Mathf.Exp(-gripEaseRate * Time.deltaTime);
            GetPoseIn(handBone, propTransform, out Vector3 handLocalPosition, out Quaternion handLocalRotation);
            SetPoseIn(handBone, propTransform, Vector3.Lerp(handLocalPosition, palmedPosition, ease), Quaternion.Slerp(handLocalRotation, palmedRotation, ease));

            if (!palmedIsOld)
                return;

            bool stillInGun = palmedFor.Item != null && palmedFor.Item.GetCurrentMagazine() == palmedMagazine;
            // The move to the rig never landed: the magazine is still in the gun, showing again
            if (stillInGun && expectedOperation == ExpectedOperation.None && palmedFor.CurrentOperation is FirearmController.Idling)
            {
                Plugin.MyLog.LogWarning($"[VRReload] {palmedMagazine.LocalizedShortName()} never left the gun, letting go of its model");
                DropPalmedModel();
                return;
            }
            if (gripReleased)
                palmedLetGo = true;
            if (palmedLetGo && !stillInGun)
                LetGoOfPalmedMagazine();
        }

        // Already in the rig: at the chest it stays there, anywhere else it goes to the ground from where the hand has it
        private static void LetGoOfPalmedMagazine()
        {
            Magazine magazine = palmedMagazine;
            if (!InChestPouchZone(OffHandPalm()) && CanDropToGround(magazine))
            {
                pendingDropMagazine = magazine;
                pendingDropPosition = palmedProp.transform.position;
                pendingDropRotation = palmedProp.transform.rotation;
                pendingDropVelocity = OffHandWorldVelocity();
                pendingDropDeadline = Time.time + 3f;
                VRGlobals.player.InventoryController.ThrowItem(magazine, false, null);
            }
            else
            {
                PlayGunSound(palmedFor, "MagSbrosnik");
                Pulse(0.4f, 0.05f);
            }
            DropPalmedModel();
        }

        private static void DropPalmedModel()
        {
            ReturnPropToPool(palmedProp);
            ForgetPalmedMagazine();
        }

        private static void ForgetPalmedMagazine()
        {
            palmedProp = null;
            palmedMagazine = null;
            palmedFor = null;
            palmedIsOld = false;
            palmedLetGo = false;
            activePalmHold = null;
        }
    }
}
