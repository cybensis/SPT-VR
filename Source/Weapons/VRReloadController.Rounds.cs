using System.Collections.Generic;
using Diz.LanguageExtensions;
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
    // Single rounds through an open bolt (Manual Reload): the bolt-actions Tarkov loads through the action (Mosins; an M700, AXMC or
    // TRG's magazine topped up while it's in). With the bolt all the way back, a grip at the chest pouch takes ONE round of a loose
    // stack that fits (the type already in the gun first), held in a thumb-and-index pinch; into the open action it goes in.
    // The hand that's free does it, the same that works the bolt: the off hand while the gun hand has the grip, the gun hand while the
    // off hand holds the gun up (ChestHang.SupportOnly; his ask: "the same hand you pull the bolt on"). On those guns with the bolt
    // open, the off hand's pouch grip is a round, not a magazine.
    //  - The inventory move is Tarkov's own ApplySingleItemToAddress (merge / transfer / split / move of one round), built simulated and
    //    run as a network transaction (FIKA syncs it like any move). NOT Tarkov's ReloadInternalMagWithOpenBoltOperation: its Prepare
    //    throws out a chambered round and it plays the reload animation.
    //  - Into an empty chamber it goes in the chamber but waits for the bolt (WaitingForRack, like a round a magazine brought in): the
    //    chamber shows empty while open, closing the bolt chambers it (BoltClosed shows the round). Otherwise the top of the magazine.
    //  - Letting go anywhere before it's in puts it back (it never left the stack): no drop to the ground (not built).
    public static partial class VRReloadController
    {
        public static bool singleRoundLoading = true;
        public static float roundLoadRadius = 0.06f;        // m from the round's middle to the chamber that loads it
        public static float roundOutPastPinch = 0.25f;      // share of the round's length its middle sits past the pinch (the fingers hold its base)

        public static bool HoldingRound { get; private set; }
        public static bool RoundInGunHand => HoldingRound && roundInGunHand;

        private static bool roundInGunHand;
        private static bool previousGunHandGripState;
        private static Ammo heldRoundSource;               // the stack the round comes from (it stays there until it's loaded)
        private static GameObject heldRoundProp;
        private static FirearmController heldRoundFor;
        private static Vector3 roundAxisInProp;             // its length, in the prop's frame
        private static Vector3 roundCenterInProp;
        private static float roundLength;
        private static bool roundLoading;                   // the move is running
        private static GameObject roundZoneViz;
        private static readonly List<Ammo> reachableRounds = new List<Ammo>();

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Rounds can go in now: a bolt-action Tarkov loads through the action, our bolt on it all the way back, room in the gun
        private static bool CanLoadRounds(out FirearmController firearmController, out Weapon weapon, out Magazine magazine)
        {
            firearmController = VRGlobals.firearmController;
            weapon = null;
            magazine = null;
            if (!enabled || !singleRoundLoading || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.player == null)
                return false;
            if (!ManualBoltOn(firearmController) || firearmController.IsInLauncherMode() || HoldingBolt)
                return false;
            weapon = firearmController.Item;
            if (!weapon.SupportsInternalReload || !weapon.MustBoltBeOpennedForInternalReload || weapon.MalfState.State != Weapon.EMalfunctionState.None)
                return false;
            if (!(firearmController.CurrentOperation is FirearmController.Idling) || expectedOperation != ExpectedOperation.None)
                return false;
            if (VRGlobals.handsInteractionController != null && VRGlobals.handsInteractionController.heldItem != null)
                return false;
            if (!boltStates.TryGetValue(weapon, out BoltState state) || state.slide < boltFullBack)
                return false;
            magazine = weapon.GetCurrentMagazine();
            if (magazine == null)
                return false;
            return magazine.Count < magazine.MaxCount || (weapon.Chambers.Length > 0 && weapon.ChamberAmmoCount == 0);
        }

        // A grip at the chest: true when it was a round's grip (taken, or nothing that fits: a double buzz), so nothing else takes that
        // press. gunHand: the gun hand's grip, only while the off hand holds the gun up and the hand isn't at the gun's grip (that's
        // ChestHang's regrab); the off hand's only while the gun hand has the grip and the off hand isn't on the foregrip.
        private static bool TryGrabRound(bool gunHand)
        {
            if (!CanLoadRounds(out FirearmController firearmController, out Weapon weapon, out Magazine magazine))
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
            roundLoading = false;
            MeasureRound(prop);
            HoldingRound = true;
            PlaceHeldRound();
            PulseRoundHand(gunHand, 0.25f, 0.03f);
            if (debugLog)
                LogAction($"[VRReload] took a round of {source.LocalizedShortName()} ({source.StackObjectsCount} in that stack) in the {(gunHand ? "gun" : "off")} hand, "
                    + $"gun {magazine.Count}/{magazine.MaxCount} + chamber {weapon.ChamberAmmoCount}");
            return true;
        }

        private static Vector3 HandPalm(bool gunHand)
        {
            if (!gunHand)
                return OffHandPalm();
            return VRGlobals.handsInteractionController != null ? VRGlobals.handsInteractionController.PalmOrigin(true) : VRGlobals.vrPlayer.RightHand.transform.position;
        }

        private static void PulseRoundHand(bool gunHand, float amplitude, float duration, float delay = 0f)
        {
            if (!gunHand)
                Pulse(amplitude, duration, delay);
            else if (haptics)
                VRInput.Haptic.Execute(delay, duration, 120f, amplitude, Handedness.GunHandSource);
        }

        // A loose stack (in a container's grid: not the rounds in another magazine) the gun takes, the type already in it first
        private static Ammo FindRoundToLoad(Weapon weapon, Magazine magazine)
        {
            reachableRounds.Clear();
            VRGlobals.player.InventoryController.GetReachableItemsOfTypeNonAlloc(reachableRounds, (Ammo ammo) => ammo.StackObjectsCount > 0 && ammo.Parent is GridItemAddress);
            Item inGun = weapon.Chambers.Length > 0 && weapon.Chambers[0].ContainedItem != null ? weapon.Chambers[0].ContainedItem : magazine.Cartridges.Last;
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Ammo ammo in reachableRounds)
                {
                    if (pass == 0 && (inGun == null || ammo.TemplateId != inGun.TemplateId))
                        continue;
                    if (TryBuildLoadOne(ammo, weapon, magazine, out _, out _))
                        return ammo;
                }
            }
            return null;
        }

        // One round from the stack: into an empty chamber, else onto the top of the magazine (merged with a stack of the same type, or a
        // new one). Tarkov's ApplySingleItemToAddress, simulated.
        private static bool TryBuildLoadOne(Ammo source, Weapon weapon, Magazine magazine, out OperationResult<IItemOperationResult> operation, out bool intoChamber)
        {
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            intoChamber = weapon.Chambers.Length > 0 && weapon.ChamberAmmoCount == 0;
            ItemAddress to = intoChamber ? weapon.Chambers[0].CreateItemAddress() : magazine.Cartridges.CreateItemAddress();
            Item top = intoChamber ? null : magazine.Cartridges.Last;
            if (top != null && top.TemplateId == source.TemplateId)
            {
                operation = source.StackObjectsCount == 1
                    ? ItemManipulator.Merge(source, top, inventoryController, simulate: true).Cast<MergeResult, IItemOperationResult>()
                    : ItemManipulator.TransferExact(source, 1, top, inventoryController, simulate: true).Cast<TransferResult, IItemOperationResult>();
                if (operation.Succeeded)
                    return true;
            }
            operation = source.StackObjectsCount == 1
                ? ItemManipulator.Move(source, to, inventoryController, simulate: true).Cast<MoveResult, IItemOperationResult>()
                : ItemManipulator.SplitExact(source, 1, to, inventoryController, inventoryController, simulate: true).Cast<SplitResult, IItemOperationResult>();
            return operation.Succeeded;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // From Tick

        // gripReleased / gunHandGripReleased: the two hands' grips let go this frame; the one holding the round counts
        private static void UpdateHeldRound(bool gripReleased, bool gunHandGripReleased)
        {
            if (roundLoading)
                return;
            if (!HeldRoundIsStillValid())
            {
                PutRoundBack();
                return;
            }
            if (roundInGunHand ? gunHandGripReleased : gripReleased)
            {
                PutRoundBack();
                PulseRoundHand(roundInGunHand, 0.15f, 0.02f);
                return;
            }
            if (CanLoadRounds(out FirearmController firearmController, out Weapon weapon, out Magazine magazine) && firearmController == heldRoundFor
                && TryGetLoadPoint(firearmController, out Vector3 loadPoint) && (RoundCenterWorld() - loadPoint).sqrMagnitude <= roundLoadRadius * roundLoadRadius)
                LoadHeldRound(firearmController, weapon, magazine);
        }

        private static bool HeldRoundIsStillValid()
        {
            if (!enabled || !singleRoundLoading || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.player == null)
                return false;
            if (heldRoundProp == null || heldRoundSource == null || VRGlobals.firearmController == null || VRGlobals.firearmController != heldRoundFor)
                return false;
            // The stack went somewhere else meanwhile (moved in the inventory, FIKA)
            return heldRoundSource.StackObjectsCount > 0 && heldRoundSource.Parent is GridItemAddress;
        }

        private static void LoadHeldRound(FirearmController firearmController, Weapon weapon, Magazine magazine)
        {
            if (!TryBuildLoadOne(heldRoundSource, weapon, magazine, out OperationResult<IItemOperationResult> operation, out bool intoChamber))
            {
                if (debugLog)
                    LogAction($"[VRReload] the round won't go in: {operation.Error}");
                bool gunHand = roundInGunHand;
                PutRoundBack();
                PulseRoundHand(gunHand, 0.2f, 0.03f);
                PulseRoundHand(gunHand, 0.2f, 0.03f, 0.08f);
                return;
            }
            roundLoading = true;
            DestroyRoundProp();
            PlayFirstGunSound(firearmController, roundInGunHand ? SoundHand.GunHand : SoundHand.OffHand, "AmmoIn", "PatronIn", "Patron", "MagIn");
            PulseRoundHand(roundInGunHand, 0.4f, 0.04f);
            VRGlobals.player.InventoryController.TryRunNetworkTransaction(operation, result =>
            {
                if (result.Succeed)
                {
                    // In the chamber for Tarkov, but it waits for the bolt like one a magazine brought in; closing the bolt puts its
                    // model in (a full stroke's rechamber marked the chamber shown, with nothing in it)
                    if (intoChamber)
                    {
                        weaponsWaitingForRack.Add(weapon);
                        BoltStateOf(weapon).roundShown = false;
                    }
                    if (firearmController.FirearmsAnimator != null)
                    {
                        firearmController.FirearmsAnimator.SetAmmoOnMag(magazine.Count);
                        firearmController.FirearmsAnimator.SetAmmoInChamber(weapon.ChamberAmmoCount);
                    }
                }
                if (debugLog)
                    LogAction($"[VRReload] round {(result.Succeed ? "in" : "NOT in: " + result.Error)} ({(intoChamber ? "chamber, waits for the bolt" : "magazine")}), gun {magazine.Count}/{magazine.MaxCount} + chamber {weapon.ChamberAmmoCount}");
                EndRoundHold();
            });
        }

        // Back where it was: it never left its stack
        private static void PutRoundBack()
        {
            DestroyRoundProp();
            EndRoundHold();
        }

        private static void EndRoundHold()
        {
            HoldingRound = false;
            roundLoading = false;
            heldRoundSource = null;
            heldRoundFor = null;
        }

        private static void DestroyRoundProp()
        {
            ReturnPropToPool(heldRoundProp);
            heldRoundProp = null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The round in the fingers

        // Its length = the longest side of its biggest mesh's own bounds (never Renderer.bounds: the world box grows with the rotation)
        private static void MeasureRound(GameObject prop)
        {
            roundAxisInProp = Vector3.forward;
            roundCenterInProp = Vector3.zero;
            roundLength = 0.06f;
            MeshFilter biggest = null;
            float biggestVolume = 0f;
            foreach (MeshFilter meshFilter in prop.GetComponentsInChildren<MeshFilter>(true))
            {
                if (meshFilter.sharedMesh == null)
                    continue;
                Vector3 size = meshFilter.sharedMesh.bounds.size;
                float volume = size.x * size.y * size.z;
                if (biggest == null || volume > biggestVolume)
                {
                    biggest = meshFilter;
                    biggestVolume = volume;
                }
            }
            if (biggest == null)
                return;
            Bounds bounds = biggest.sharedMesh.bounds;
            Vector3 extent = bounds.size;
            Vector3 axisLocal = extent.x >= extent.y && extent.x >= extent.z ? Vector3.right : extent.y >= extent.z ? Vector3.up : Vector3.forward;
            Vector3 axisWorld = biggest.transform.TransformVector(axisLocal * Vector3.Dot(extent, axisLocal));
            roundAxisInProp = prop.transform.InverseTransformDirection(axisWorld).normalized;
            roundCenterInProp = prop.transform.InverseTransformPoint(biggest.transform.TransformPoint(bounds.center));
            roundLength = axisWorld.magnitude;
        }

        private static Vector3 RoundCenterWorld()
        {
            return heldRoundProp != null ? heldRoundProp.transform.TransformPoint(roundCenterInProp) : Vector3.zero;
        }

        // After the holding hand's fingers are posed (the end of ApplyHeldFingerPose / ApplyGunHandFingerPose): its base between the
        // thumb and index tips, pointing along the index
        private static void PlaceHeldRound()
        {
            if (!HoldingRound || heldRoundProp == null || VRGlobals.handsInteractionController == null)
                return;
            if (!VRGlobals.handsInteractionController.TryGetPinch(roundInGunHand, out Vector3 thumbTip, out Vector3 indexTip, out Vector3 indexBase))
                return;
            Vector3 pinch = (thumbTip + indexTip) * 0.5f;
            Vector3 along = indexTip - indexBase;
            if (along.sqrMagnitude < 1e-8f)
                return;
            along.Normalize();
            Transform prop = heldRoundProp.transform;
            prop.rotation = Quaternion.FromToRotation(prop.rotation * roundAxisInProp, along) * prop.rotation;
            Vector3 center = pinch + along * (roundLength * roundOutPastPinch);
            prop.position += center - prop.TransformPoint(roundCenterInProp);
        }

        // Where it goes in: the chamber (Tarkov's patron_in_weapon bone), the open action right behind it
        private static bool TryGetLoadPoint(FirearmController firearmController, out Vector3 loadPoint)
        {
            loadPoint = Vector3.zero;
            Transform[] chamberBones = firearmController.Firearms != null ? firearmController.Firearms._patronInWeaponTransforms : null;
            if (chamberBones == null || chamberBones.Length == 0 || chamberBones[0] == null)
                return false;
            loadPoint = chamberBones[0].position;
            return true;
        }

        // debugZones: the load zone while a round is in the hand (green once the round is in it)
        private static void UpdateRoundViz()
        {
            bool show = debugZones && HoldingRound && heldRoundFor != null && TryGetLoadPoint(heldRoundFor, out Vector3 loadPoint);
            if (!show)
            {
                SetZoneViz(ref roundZoneViz, false, Vector3.zero, 0f, Color.clear);
                return;
            }
            TryGetLoadPoint(heldRoundFor, out loadPoint);
            bool inZone = (RoundCenterWorld() - loadPoint).sqrMagnitude <= roundLoadRadius * roundLoadRadius;
            SetZoneViz(ref roundZoneViz, true, loadPoint, roundLoadRadius, inZone ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.5f, 0.8f, 0.3f));
        }
    }
}
