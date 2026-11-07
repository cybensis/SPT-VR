using System.Collections.Generic;
using EFT.InventoryLogic;
using TarkovVR.Source.Controls;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Co-op: what the manual reload shows, for other players' copies of you (the FIKA sync module's ReloadSyncApply draws it there).
    // Built every send while a firearm is in hands (VRArmSync.Tick, with the weapon pose), FIKA-agnostic (Unity / Tarkov types only:
    // this JITs without FIKA):
    //  - the two rendered wrists (the arm IK's hand bones, so a hand latched on a handle, bolt or magazine is where you see it), and
    //    which arms other players should drive to them instead of welding onto the gun's markers
    //  - the props in the hands (magazines, a round), by item id
    //  - every part of the gun this moves or hides (bolt, handle, carrier, side handle, release, chamber round, a partly-out magazine,
    //    the gun's magazine model hidden while it's in the hand), by its path under the gun's root, as its local pose and shown state
    //  - the bolt catch and the bolt-open layer this drives by hand
    //  - counters for one-off events: a spent case thrown out by the bolt, the chambered round shown as the bolt closes
    // Wrists and props are in the gun's frame (Weapon_root), so on the other side they ride the observed gun as it's placed.
    public static partial class VRReloadController
    {
        public static bool syncManualReload;                 // magazines by hand: other players skip Tarkov's magazine / rechamber animations
        public static bool syncManualBolt;                   // the bolt by hand: other players skip Tarkov's bolt cycle after a shot
        public static bool syncDriveLeft, syncDriveRight;    // that arm goes to its synced wrist (else it's welded on the gun's marker)
        public static Vector3 syncLeftPosition, syncRightPosition;
        public static Quaternion syncLeftRotation = Quaternion.identity, syncRightRotation = Quaternion.identity;
        public static readonly List<string> syncPropItems = new List<string>();
        public static readonly List<Vector3> syncPropPositions = new List<Vector3>();
        public static readonly List<Quaternion> syncPropRotations = new List<Quaternion>();
        public static readonly List<string> syncPartPaths = new List<string>();
        public static readonly List<Vector3> syncPartPositions = new List<Vector3>();
        public static readonly List<Quaternion> syncPartRotations = new List<Quaternion>();
        public static readonly List<bool> syncPartShown = new List<bool>();
        public static string syncHiddenGroundItem;           // the ground magazine's item while its model is in the hand
        public static bool syncBoltCatch;
        public static bool syncBoltOpenDriven;
        public static float syncBoltOpen;
        public static int syncShellsThrown;
        public static int syncRoundsShown;

        // SetBoltOpen's last value and frame: the bolt-open layer is ours while it's being written
        private static float boltOpenWritten;
        private static int boltOpenWrittenFrame = -10;
        // The magazine whose prop waits in the hand for Tarkov's move (CommitHold)
        private static Item pendingPropItem;
        private static readonly Dictionary<Transform, string> partPaths = new Dictionary<Transform, string>();
        private static Transform partPathsRoot;
        private static readonly System.Text.StringBuilder pathBuilder = new System.Text.StringBuilder(128);
        private static readonly List<Transform> pathChain = new List<Transform>(16);

        public static bool TryBuildSync(FirearmController firearmController)
        {
            syncPropItems.Clear();
            syncPropPositions.Clear();
            syncPropRotations.Clear();
            syncPartPaths.Clear();
            syncPartPositions.Clear();
            syncPartRotations.Clear();
            syncPartShown.Clear();
            syncHiddenGroundItem = null;
            Transform weaponRoot = firearmController != null ? firearmController.WeaponRoot : null;
            if (weaponRoot == null || VRGlobals.vrPlayer == null || VRGlobals.ikManager == null)
                return false;
            if (partPathsRoot != weaponRoot)
            {
                partPathsRoot = weaponRoot;
                partPaths.Clear();
            }

            syncManualReload = VRSettings.GetManualReloadOn();
            syncManualBolt = ManualBoltOn(firearmController);

            // The gun hand on the grip and the support hand on the foregrip are welded onto the gun's markers there; any other hand
            // (off the gun, on a part, holding the gun from the left) goes where it's drawn here
            bool gunInLeftHand = Handedness.GunInLeftHand;
            syncDriveRight = gunInLeftHand || ChestHang.Detached;
            syncDriveLeft = !(VRGlobals.vrPlayer.isSupporting && !gunInLeftHand);
            InGunFrame(weaponRoot, WristOf(VRGlobals.ikManager.leftArmIk), out syncLeftPosition, out syncLeftRotation);
            InGunFrame(weaponRoot, WristOf(VRGlobals.ikManager.rightArmIk), out syncRightPosition, out syncRightRotation);

            AddProp(weaponRoot, heldProp, heldMagazine);
            AddProp(weaponRoot, palmedProp, palmedMagazine);
            AddProp(weaponRoot, pendingProp, pendingPropItem);
            AddProp(weaponRoot, heldRoundProp, heldRoundSource);

            // The bolt-action's bolt while it's off its closed pose, and the round riding it
            if (drivingBolt && boltBonesFor == firearmController)
            {
                AddPart(weaponRoot, boltSlider);
                AddPart(weaponRoot, boltTurner);
            }
            if (boltChamberFor == firearmController)
            {
                AddPart(weaponRoot, boltChamberBone);
                AddPart(weaponRoot, boltChamberRound);
            }
            // The charging handle and what it moves: the handle model (not our rider on a carrier handle), the carrier, a side
            // handle's slider / base / knob, the bolts it pulls, and the round riding the bolt
            if (HoldingChargingHandle && handleForFirearm == firearmController)
            {
                if (handleView != handleRider)
                    AddPart(weaponRoot, handleView);
                AddPart(weaponRoot, handleCarrier);
                if (heldSideHandle != null)
                {
                    AddPart(weaponRoot, sideSlider);
                    AddPart(weaponRoot, sideBase);
                    AddPart(weaponRoot, sideKnob);
                }
                foreach (MovedBolt movedBolt in movedBolts)
                    AddPart(weaponRoot, movedBolt.bone);
                if (showingChamber)
                {
                    AddPart(weaponRoot, chamberBone);
                    AddPart(weaponRoot, chamberedRound);
                }
            }
            // The magazine release pressed in
            if (releaseParts != null && releasePartsFor == firearmController && releasePressShown > 0f)
            {
                foreach (Transform part in releaseParts)
                    AddPart(weaponRoot, part);
            }
            // A pistol's magazine partly out, and the gun's own magazine model while the magazine is in the hand or on its way out
            if (MagazinePartlyOut && partlyOutFor == firearmController)
                AddPart(weaponRoot, partlyOutView);
            AddPart(weaponRoot, hiddenGunMagazineView);
            AddPart(weaponRoot, pendingHiddenView);

            if (hiddenRenderersOf != null && hiddenRenderersOf.Item != null)
                syncHiddenGroundItem = hiddenRenderersOf.Item.Id.ToString();

            FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
            syncBoltCatch = firearmsAnimator != null && firearmsAnimator.GetBoltCatch();
            syncBoltOpenDriven = Time.frameCount - boltOpenWrittenFrame <= 2;
            syncBoltOpen = syncBoltOpenDriven ? boltOpenWritten : (syncBoltCatch ? 1f : 0f);
            return true;
        }

        private static Transform WristOf(RootMotion.FinalIK.LimbIK armIk)
        {
            return armIk != null && armIk.solver != null && armIk.solver.bone3 != null ? armIk.solver.bone3.transform : null;
        }

        private static void InGunFrame(Transform weaponRoot, Transform thing, out Vector3 position, out Quaternion rotation)
        {
            if (thing == null)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                return;
            }
            Quaternion toGun = Quaternion.Inverse(weaponRoot.rotation);
            position = toGun * (thing.position - weaponRoot.position);
            rotation = toGun * thing.rotation;
        }

        private static void AddProp(Transform weaponRoot, GameObject prop, Item item)
        {
            if (prop == null || item == null || !prop.activeInHierarchy)
                return;
            InGunFrame(weaponRoot, prop.transform, out Vector3 position, out Quaternion rotation);
            syncPropItems.Add(item.Id.ToString());
            syncPropPositions.Add(position);
            syncPropRotations.Add(rotation);
        }

        private static void AddPart(Transform weaponRoot, Transform part)
        {
            if (part == null)
                return;
            string path = PathUnder(weaponRoot, part);
            if (path == null)
                return;
            syncPartPaths.Add(path);
            syncPartPositions.Add(part.localPosition);
            syncPartRotations.Add(part.localRotation);
            syncPartShown.Add(part.gameObject.activeSelf);
        }

        // The names from under the gun's root down to the part, joined by '/' (the other side's copy of the gun is built from the same
        // prefab, so it finds the part by the same path). Null when it isn't under the root, or rides one of our own transforms (VR*).
        private static string PathUnder(Transform weaponRoot, Transform part)
        {
            if (partPaths.TryGetValue(part, out string cached))
                return cached;
            pathChain.Clear();
            Transform walk = part;
            while (walk != null && walk != weaponRoot)
            {
                pathChain.Add(walk);
                walk = walk.parent;
            }
            string path = null;
            if (walk == weaponRoot)
            {
                pathBuilder.Clear();
                bool ours = false;
                for (int index = pathChain.Count - 1; index >= 0; index--)
                {
                    string name = pathChain[index].name;
                    if (name.StartsWith("VR"))
                        ours = true;
                    if (pathBuilder.Length > 0)
                        pathBuilder.Append('/');
                    pathBuilder.Append(name);
                }
                if (!ours)
                    path = pathBuilder.ToString();
            }
            partPaths[part] = path;
            return path;
        }
    }
}
