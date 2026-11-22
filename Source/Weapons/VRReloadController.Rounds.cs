using System.Collections.Generic;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using TarkovVR.Patches.Core.VR;
using TarkovVR.Source.Player.Interactions;
using TarkovVR.Source.Settings;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace TarkovVR.Source.Weapons
{
    // Single rounds through an open bolt (Manual Reload), for the bolt-actions Tarkov loads through the action (Mosins; an M700, AXMC or
    // TRG's magazine topped up while it's in). With the bolt all the way back, a grip at the chest pouch takes one round of a loose stack
    // that fits (the type already in the gun first), and it goes into the open action. The free hand does it, the same one that works the
    // bolt (the gun hand while ChestHang.SupportOnly). On these guns with the bolt open, the off hand's pouch grip takes a round, not a magazine.
    //  - The inventory move is Tarkov's own ApplySingleItemToAddress (merge / transfer / split / move of one round), built simulated and
    //    run as a network transaction (FIKA syncs it). Not ReloadInternalMagWithOpenBoltOperation: its Prepare throws out a chambered
    //    round and it plays the reload animation.
    //  - The gun's hands controller would animate the chamber move and refuse the magazine ones, so BoltActionPatches.LetSingleRoundIn
    //    lets ours through (LetsSingleRoundIn). In co-op the host must accept the move before our client applies it: SptVrFikaSync.BoltRoundSync
    //    does that on every peer with the module (VR players only). A host without it refuses, and the round stays in the stack.
    //  - A round into an empty chamber waits for the bolt (WaitingForRack, like a round a magazine brought in): the chamber shows empty
    //    while open, and closing the bolt chambers it (BoltClosed shows the round). Otherwise the round goes on top of the magazine.
    //  - Letting go anywhere before it's in puts it back (it never left the stack); it is never dropped to the ground.
    // The same applies to a self-loader loaded through its open action into a fixed magazine (the SKS's internal 10-round; its detachable
    // boxes are swapped instead). With the carrier locked back on Tarkov's catch, every round goes on top of the magazine, and letting the
    // carrier go chambers the top one (the charging handle's own release).
    public static partial class VRReloadController
    {
        public static bool HoldingRound { get; private set; }

        private static bool roundInGunHand;
        private static bool roundOnRightArm;                // the arm drawing that hand (OnRightArm), as it was at the grab
        private static Ammo heldRoundSource;               // the stack the round comes from (it stays there until it's loaded)
        private static GameObject heldRoundProp;
        private static FirearmController heldRoundFor;
        private static Vector3 roundAxisInProp;             // its length, in the prop's frame
        private static Vector3 roundCenterInProp;
        private static float roundLength;
        private static bool roundLoading;                   // the move is running
        private static readonly List<Ammo> reachableRounds = new List<Ammo>();

        // The shared start of taking a round or a shell: this hand is free for one and at the chest pouch, a round that fits is found, and
        // its prop is made and noted as held. False = not a round's grip. True with prop null = it was, but there was nothing to take (no
        // round fits: a double buzz). gunHand: the gun hand's grip counts only while the off hand holds the gun up and the hand isn't at the
        // gun's grip (that's ChestHang's regrab); the off hand's only while the gun hand has the grip and the off hand isn't on the foregrip.
        private static bool TryTakeRoundFromPouch(FirearmController firearmController, Weapon weapon, Magazine magazine, bool gunHand, out Ammo source, out GameObject prop)
        {
            source = null;
            prop = null;
            if (gunHand ? !ChestHang.SupportOnly || ChestHang.HandInRegrabZone : VRGlobals.vrPlayer.isSupporting || ChestHang.Detached)
                return false;
            if (!InChestPouchZone(HandPalm(gunHand)))
                return false;
            source = Rounds.FindRoundToLoad(weapon, magazine);
            if (source == null)
            {
                PulseRoundHand(gunHand, 0.2f, 0.03f);
                PulseRoundHand(gunHand, 0.2f, 0.03f, 0.08f);
                return true;
            }
            prop = CreateProp(source);
            if (prop == null)
                return true;
            heldRoundSource = source;
            heldRoundProp = prop;
            heldRoundFor = firearmController;
            roundInGunHand = gunHand;
            roundOnRightArm = OnRightArm(gunHand);
            roundLoading = false;
            return true;
        }

        // The palm of the hand you see, chosen by the arm drawing it (OnRightArm), not by the controller
        private static Vector3 HandPalm(bool gunHand)
        {
            if (!gunHand)
                return OffHandPalm();
            return VRGlobals.handsInteractionController != null ? VRGlobals.handsInteractionController.PalmOrigin(OnRightArm(gunHand)) : VRGlobals.vrPlayer.RightHand.transform.position;
        }

        // Pulses only the hand with the round: taking one, putting it back, one that won't go in
        private static void PulseRoundHand(bool gunHand, float amplitude, float duration, float delay = 0f)
        {
            Pulse(gunHand ? SoundHand.GunHandAlone : SoundHand.OffHandAlone, amplitude, duration, delay);
        }

        // One round from the stack: into an empty chamber, else onto the top of the magazine (merged with a stack of the same type, or a
        // new one). Tarkov's ApplySingleItemToAddress, simulated.
        private static bool TryBuildLoadOne(Ammo source, Weapon weapon, Magazine magazine, out OperationResult<IItemOperationResult> operation, out bool intoChamber)
        {
            InventoryController inventoryController = VRGlobals.player.InventoryController;
            // A self-loader's rounds go on top of its magazine: the carrier chambers one as it goes home
            intoChamber = weapon.Chambers.Length > 0 && weapon.ChamberAmmoCount == 0 && !Rounds.LoadsFixedMagazineThroughAction(weapon);
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

        private static bool HeldRoundIsStillValid()
        {
            if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.player == null)
                return false;
            if (heldRoundProp == null || heldRoundSource == null || VRGlobals.firearmController == null || VRGlobals.firearmController != heldRoundFor)
                return false;
            // The gun went from one hand to the other: the arm holding it isn't this hand's any more
            if (roundOnRightArm != OnRightArm(roundInGunHand))
                return false;
            // The stack went somewhere else meanwhile (moved in the inventory, FIKA)
            return heldRoundSource.StackObjectsCount > 0 && heldRoundSource.Parent is GridItemAddress;
        }

        // Puts the round back where it was: it never left its stack
        private static void PutRoundBack()
        {
            DestroyRoundProp();
            EndRoundHold();
        }

        private static void EndRoundHold()
        {
            ClearBreakShell();
            ReleaseShellHand();
            heldShell = null;
            shellPath = null;
            shellCommitted = false;
            HoldingRound = false;
            roundLoading = false;
            heldRoundSource = null;
            heldRoundFor = null;
        }

        private static void DestroyRoundProp()
        {
            // A shell on its way in rides the gun's bones (and the hand rides it): take it off them before it goes back to the pool
            ReleaseBreakShellHand();
            if (heldRoundProp != null && heldRoundProp.transform.parent != null)
                heldRoundProp.transform.SetParent(null, worldPositionStays: true);
            ReturnPropToPool(heldRoundProp);
            heldRoundProp = null;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The round in the fingers

        // The round's length is the longest side of its biggest mesh's own bounds (never Renderer.bounds: the world box grows with rotation)
        private static void MeasureRound(GameObject prop)
        {
            MeasureLongAxis(prop, out roundAxisInProp, out roundCenterInProp, out roundLength);
        }

        // The biggest mesh under a model, by its box's volume
        private static MeshFilter BiggestMesh(GameObject prop, bool includeHidden)
        {
            MeshFilter biggest = null;
            float biggestVolume = 0f;
            foreach (MeshFilter meshFilter in prop.GetComponentsInChildren<MeshFilter>(includeHidden))
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
            return biggest;
        }

        // Also used for the stuck case of a failure to eject (VRReloadController.Malfunctions)
        private static void MeasureLongAxis(GameObject prop, out Vector3 axisInProp, out Vector3 centerInProp, out float length)
        {
            axisInProp = Vector3.forward;
            centerInProp = Vector3.zero;
            length = 0.06f;
            MeshFilter biggest = BiggestMesh(prop, includeHidden: true);
            if (biggest == null)
                return;
            Bounds bounds = biggest.sharedMesh.bounds;
            Vector3 extent = bounds.size;
            Vector3 axisLocal = extent.x >= extent.y && extent.x >= extent.z ? Vector3.right : extent.y >= extent.z ? Vector3.up : Vector3.forward;
            Vector3 axisWorld = biggest.transform.TransformVector(axisLocal * Vector3.Dot(extent, axisLocal));
            axisInProp = prop.transform.InverseTransformDirection(axisWorld).normalized;
            centerInProp = prop.transform.InverseTransformPoint(biggest.transform.TransformPoint(bounds.center));
            length = axisWorld.magnitude;
        }

        // Turns the round's length axis to run from the base to the bullet (the mesh's longest side has no set direction). Tarkov seats a
        // round in the chamber at the chamber bone's rotation turned 90 deg about x (Firearms.ParentShellToTransform), bullet down the bore
        // (Weapon_root -up); that gives the bullet's direction in the round's own frame.
        private static void PointRoundAlongBore(FirearmController firearmController)
        {
            Transform[] chamberBones = firearmController.Firearms != null ? firearmController.Firearms._patronInWeaponTransforms : null;
            Transform weaponRoot = firearmController.WeaponRoot;
            if (chamberBones != null && chamberBones.Length > 0 && chamberBones[0] != null && weaponRoot != null)
            {
                Vector3 bulletInRound = Quaternion.Inverse(chamberBones[0].rotation * Quaternion.Euler(90f, 0f, 0f)) * -weaponRoot.up;
                if (Vector3.Dot(roundAxisInProp, bulletInRound) < 0f)
                    roundAxisInProp = -roundAxisInProp;
            }
            if (Rounds.flipHeldRound)
                roundAxisInProp = -roundAxisInProp;
        }

        // Runs after the holding hand's fingers are posed (the end of ApplyHeldFingerPose / ApplyGunHandFingerPose). Uses the clip's hold
        // on that palm, else puts the round's base between the thumb and index tips, pointing along the index.
        private static void PlaceHeldRound()
        {
            if (!HoldingRound || heldRoundProp == null || VRGlobals.handsInteractionController == null)
                return;
            if (breakShellHeld)
            {
                PlaceHeldBreakShell();
                return;
            }
            if (HoldingShell)
            {
                PlaceHeldShell();
                return;
            }
            Transform palm = ArmPalm(roundOnRightArm);
            if (palm == null && !roundOnRightArm)
                palm = InitVRPatches.leftPalm;
            if (palm != null && Rounds.RoundHoldInPalm(roundOnRightArm, out Vector3 holdPosition, out Quaternion holdRotation))
            {
                SetPoseIn(palm, heldRoundProp.transform, holdPosition, holdRotation);
                return;
            }
            if (!VRGlobals.handsInteractionController.TryGetPinch(roundOnRightArm, out Vector3 thumbTip, out Vector3 indexTip, out Vector3 indexBase))
                return;
            Vector3 pinch = (thumbTip + indexTip) * 0.5f;
            Vector3 along = indexTip - indexBase;
            if (along.sqrMagnitude < 1e-8f)
                return;
            along.Normalize();
            Transform prop = heldRoundProp.transform;
            prop.rotation = Quaternion.FromToRotation(prop.rotation * roundAxisInProp, along) * prop.rotation;
            Vector3 center = pinch + along * (roundLength * Rounds.roundOutPastPinch);
            prop.position += center - prop.TransformPoint(roundCenterInProp);
        }

        // The same hold for anything shaped like a round, from its own measurements (MeasureLongAxis). The stuck case of a failure to eject is
        // the same model with the bullet hidden (AmmoPoolObject.SetUsed), seated the same way (ParentShellToTransform).
        private static bool HoldInPalm(bool rightArm, Transform prop, Vector3 axisInProp, Vector3 centerInProp, out Vector3 position, out Quaternion rotation)
        {
            position = Rounds.roundHoldPosition;
            rotation = Rounds.roundHoldBoneRotation.normalized * Quaternion.Euler(90f, 0f, 0f);
            if (rightArm)
                return true;
            Vector3 axis = axisInProp.sqrMagnitude > 1e-6f ? axisInProp.normalized : Vector3.forward;
            Vector3 normal = Vector3.Cross(axis, Mathf.Abs(axis.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 point = Vector3.Scale(centerInProp, prop.lossyScale);
            return MirrorHold(normal, point, leftToRight: false, ref position, ref rotation);
        }

        private static Vector3[] MirroredPose(Vector3[] pose, bool rightToLeft, Vector3[] into)
        {
            for (int index = 0; index < 16; index++)
            {
                Rounds.mirrorFromScratch[index] = index < pose.Length ? Quaternion.Euler(pose[index]) : Quaternion.identity;
                Rounds.mirrorToScratch[index] = Quaternion.identity;
            }
            if (!TarkovVR.Source.Player.Body.HandMirror.TryMirrorPose(Rounds.mirrorFromScratch, rightToLeft, Rounds.mirrorToScratch))
                return null;
            for (int index = 0; index < 16; index++)
                into[index] = Rounds.mirrorToScratch[index].eulerAngles;
            return into;
        }

        public static class Rounds
        {
            public static float roundLoadRadius = 0.06f;        // m from the round's middle to the chamber that loads it
            public static float roundOutPastPinch = 0.25f;      // share of the round's length its middle sits past the pinch (the fingers hold its base)
            public static bool flipHeldRound = false;           // holds the round reversed in the fingers, in case a round model breaks PointRoundAlongBore's rule (pinch only)

            // The hold from Tarkov's own single-round load (mosin_reload_loop at t 0.5: the right hand carrying the round to the action, fingers
            // closed on it): the round (its patron_in_weapon bone) in the right palm's frame, and the finger pose. The left hand holds its mirror
            // image. The fallback is the magazine pull's thumb-and-index pinch (its fingertips are a magazine's width apart, so the round floats
            // between them).
            public static Vector3 roundHoldPosition = new Vector3(-0.1188f, -0.0273f, -0.0473f);
            public static Quaternion roundHoldBoneRotation = new Quaternion(0.57807f, 0.67472f, 0.36482f, -0.27837f);

            private static readonly Vector3[] ROUND_HOLD_FINGERS =
            {
                Vector3.zero,
                new Vector3(295.71f, 307.6f, 25.05f), new Vector3(345.88f, 4.51f, 19.87f), new Vector3(0.36f, 357.99f, 22.93f),    // thumb
                new Vector3(355.1f, 1.35f, 15.81f), new Vector3(357.44f, 2.39f, 81.46f), new Vector3(357.77f, 1.18f, 45.82f),      // index
                new Vector3(1.03f, 359.78f, 31.33f), new Vector3(359.58f, 6.44f, 90.79f), new Vector3(2.61f, 358.7f, 70.52f),      // middle
                new Vector3(8.59f, 9.79f, 31.66f), new Vector3(357.29f, 0.67f, 92.33f), new Vector3(358.31f, 3.6f, 61.32f),        // ring
                new Vector3(14.53f, 16.8f, 36.97f), new Vector3(355.63f, 8.69f, 89.05f), new Vector3(1.12f, 0.02f, 73.76f),        // pinky
            };
            public static bool RoundInGunHand => HoldingRound && roundInGunHand;
            internal static bool previousGunHandGripState;

            //------------------------------------------------------------------------------------------------------------------------------------------------------------

            // True when rounds can go in now: a bolt-action Tarkov loads through the action, our bolt on it all the way back, and room in the gun
            internal static bool CanLoadRounds(out FirearmController firearmController, out Weapon weapon, out Magazine magazine)
            {
                firearmController = VRGlobals.firearmController;
                weapon = null;
                magazine = null;
                if (!enabled || !VRGlobals.inGame || VRGlobals.menuOpen || VRGlobals.vrPlayer == null || VRGlobals.player == null)
                    return false;
                bool boltAction = ManualBoltOn(firearmController);
                if ((!boltAction && !ManualTopLoader(firearmController)) || firearmController.IsInLauncherMode() || HoldingBolt)
                    return false;
                weapon = firearmController.Item;
                if (!weapon.SupportsInternalReload || !weapon.MustBoltBeOpennedForInternalReload || weapon.MalfState.State != Weapon.EMalfunctionState.None)
                    return false;
                if (!GunIdle(firearmController))
                    return false;
                if (HandsInteractionController.HoldingLoot)
                    return false;
                // Open: a bolt-action's bolt all the way back, a self-loader's carrier on the catch
                if (boltAction ? !boltStates.TryGetValue(weapon, out BoltState state) || state.slide < boltFullBack : !BoltLockedBack || HoldingChargingHandle)
                    return false;
                magazine = weapon.GetCurrentMagazine();
                if (magazine == null)
                    return false;
                return magazine.Count < magazine.MaxCount || (boltAction && weapon.Chambers.Length > 0 && weapon.ChamberAmmoCount == 0);
            }

            // A self-loader Tarkov loads through its open action, with its fixed magazine in (the SKS's internal 10-round, not a detachable box)
            internal static bool LoadsFixedMagazineThroughAction(Weapon weapon)
            {
                return weapon != null && !weapon.BoltAction && weapon.SupportsInternalReload && weapon.MustBoltBeOpennedForInternalReload
                    && weapon.GetCurrentMagazine() is Magazine magazine && magazine.Template is MagazineTemplate template
                    && template.ReloadMagType == Weapon.EReloadMode.InternalMagazine;
            }

            private static bool ManualTopLoader(FirearmController firearmController)
            {
                return enabled && VRSettings.GetManualReloadOn() && firearmController != null && firearmController._player != null && firearmController._player.IsYourPlayer
                    && LoadsFixedMagazineThroughAction(firearmController.Item);
            }

            // A grip at the chest: true when it was a round's grip (taken, or nothing fits), so nothing else takes that press
            internal static bool TryGrabRound(bool gunHand)
            {
                if (!CanLoadRounds(out FirearmController firearmController, out Weapon weapon, out Magazine magazine))
                    return false;
                if (!TryTakeRoundFromPouch(firearmController, weapon, magazine, gunHand, out Ammo source, out GameObject prop))
                    return false;
                if (prop == null)
                    return true;
                MeasureRound(prop);
                PointRoundAlongBore(firearmController);
                HoldingRound = true;
                PlaceHeldRound();
                // The round taken (ak74_round_catch, where the gun has it)
                PlayFirstGunSound(firearmController, gunHand ? SoundHand.GunHandAlone : SoundHand.OffHandAlone, "AmmoCatch");
                PulseRoundHand(gunHand, 0.25f, 0.03f);
                if (debugLog)
                    LogAction($"[VRReload] took a round of {source.LocalizedShortName()} ({source.StackObjectsCount} in that stack) in the {(gunHand ? "gun" : "off")} hand, "
                        + $"gun {magazine.Count}/{magazine.MaxCount} + chamber {weapon.ChamberAmmoCount}");
                return true;
            }

            // A loose stack (in a container's grid: not the rounds in another magazine) the gun takes, the type already in it first
            internal static Ammo FindRoundToLoad(Weapon weapon, Magazine magazine)
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

            // The gun's hands controller asks about an inventory move (BoltActionPatches.LetSingleRoundIn). True = our own round, from outside
            // an open bolt-action into its chamber or magazine, while the move is running: it goes through with no animation. Other peers'
            // copies of a VR player are handled by the FIKA sync module (SptVrFikaSync.BoltRoundSync, the same rule): the host has to accept
            // the move before our client applies it.
            internal static bool LetsSingleRoundIn(FirearmController controller, IInventoryOperation operation)
            {
                // A break-action's chamber by hand (VRReloadController.BreakAction): a shell in, a round out
                if (LetsBreakMoveThrough(controller, operation))
                    return true;
                if (controller == null || controller._player == null || !controller._player.IsYourPlayer || !roundLoading || heldRoundFor != controller)
                    return false;
                Weapon weapon = controller.Item;
                // A tube shotgun's shell by hand too (VRReloadController.Shotgun): its chamber or its tube
                if (weapon == null || !weapon.SupportsInternalReload || !(weapon.MustBoltBeOpennedForInternalReload || TryGetShotgun(controller, out _)))
                    return false;
                // A merge asks with a sub-operation of its own (AddSuboperation / RemoveSuboperation); the operation it belongs to says where it goes
                if (operation is ISubOperation subOperation && subOperation.BaseInventoryOperation != null && IsRoundIntoGun(weapon, subOperation.BaseInventoryOperation))
                    return true;
                return IsRoundIntoGun(weapon, operation);
            }

            // One round from outside the gun into its chamber or its magazine. The destination is To1 for a move / split, To2 for a transfer
            // or merge onto the stack on top of the magazine (a transfer's To1 is its own source). Not Tarkov's reload.
            private static bool IsRoundIntoGun(Weapon weapon, IInventoryOperation operation)
            {
                if (operation is LoadMagOperation || !(operation is IOneItemOperation move) || !(move.Item1 is Ammo))
                    return false;
                // From outside the gun, not a round moving inside it (Tarkov's own discharge / chambering)
                if (move.From1 != null && move.From1.IsChildOf(weapon))
                    return false;
                return IntoGun(weapon, move.To1) || (operation is ITwoItemOperation twoItems && IntoGun(weapon, twoItems.To2));
            }

            private static bool IntoGun(Weapon weapon, ItemAddress address)
            {
                if (address == null)
                    return false;
                object into = address.Container;
                Magazine magazine = weapon.GetCurrentMagazine();
                if (magazine != null && ReferenceEquals(into, magazine.Cartridges))
                    return true;
                foreach (Slot chamber in weapon.Chambers)
                {
                    if (ReferenceEquals(into, chamber))
                        return true;
                }
                return false;
            }

            //------------------------------------------------------------------------------------------------------------------------------------------------------------
            // From Tick

            // gripReleased / gunHandGripReleased: the two hands' grips let go this frame; the one holding the round counts
            internal static void UpdateHeldRound(bool gripReleased, bool gunHandGripReleased)
            {
                // A break-action's shell goes in along a chamber's bore (VRReloadController.BreakAction)
                if (breakShellHeld)
                {
                    UpdateHeldBreakShell(gripReleased);
                    return;
                }
                // A tube shotgun's shell has its own ports and paths (VRReloadController.Shotgun)
                if (HoldingShell)
                {
                    UpdateHeldShell(gripReleased, gunHandGripReleased);
                    return;
                }
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
                // Pushed in at the gun: the hand holding the gun feels it go in
                SoundHand loadingHand = roundInGunHand ? SoundHand.GunHand : SoundHand.OffHand;
                // Into the chamber by hand (the Mosin's AmmoInHand = ak74_round_in_chamber), or into the magazine through the top (its RoundIn =
                // mosin_round_load). Not AmmoIn / PatronIn / MagIn first: a Mosin has none but MagIn, and a round would sound like a magazine.
                if (intoChamber)
                    PlayFirstGunSound(firearmController, loadingHand, "RoundInChamber", "AmmoInChamber", "AmmoInHand", "RoundIn", "AmmoIn");
                else
                    PlayFirstGunSound(firearmController, loadingHand, "RoundIn", "AmmoIn", "ShellInGun", "AmmoInHand");
                Pulse(loadingHand, 0.4f, 0.04f);
                VRGlobals.player.InventoryController.TryRunNetworkTransaction(operation, result =>
                {
                    if (result.Succeed)
                    {
                        // In the chamber for Tarkov, but it waits for the bolt like one a magazine brought in; closing the bolt puts its
                        // model in (roundShown is cleared because a full stroke's rechamber marked the chamber shown with nothing in it)
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

            private static Vector3 RoundCenterWorld()
            {
                return heldRoundProp != null ? heldRoundProp.transform.TransformPoint(roundCenterInProp) : Vector3.zero;
            }

            // The round's pose in an arm's palm frame. The prop is the round's model, which Tarkov seats at the chamber bone's pose turned
            // 90 deg about x (Firearms.ParentShellToTransform), so the clip's bone pose is converted to the model's the same way. The left
            // palm's pose is the mirror image through a plane along the round's length: a round is its own mirror image across any such plane
            // (the choice only spins it about its length). False until HandMirror has the hands mesh.
            internal static bool RoundHoldInPalm(bool rightArm, out Vector3 position, out Quaternion rotation)
            {
                return HoldInPalm(rightArm, heldRoundProp.transform, roundAxisInProp, roundCenterInProp, out position, out rotation);
            }

            // The finger pose holding a round on an arm (pose order, palm first): the clip's on the right, mirrored on the left. The pinch is
            // the magazine pull's on the left, mirrored on the right (falling back to the bolt clip's closed hand there).
            internal static Vector3[] RoundFingers(bool rightArm)
            {
                if (breakShellHeld)
                    return BreakShellFingers(rightArm);
                if (HoldingShell)
                    return ShellFingers(rightArm);
                return rightArm ? ROUND_HOLD_FINGERS : MirroredPose(ROUND_HOLD_FINGERS, rightToLeft: true, mirroredRoundHold) ?? MAGAZINE_PULL_FINGERS;
            }

            // Converts one hand's finger pose (pose-order eulers) to the other hand's. Worked out on each call, because HandMirror's rest poses
            // come from the body's hands mesh, which a change of clothes can swap. Null until that mesh is found.
            private static readonly Vector3[] mirroredRoundHold = new Vector3[16];
            internal static readonly Quaternion[] mirrorFromScratch = new Quaternion[16];
            internal static readonly Quaternion[] mirrorToScratch = new Quaternion[16];

            // Where the round goes in: the chamber (Tarkov's patron_in_weapon bone), with the open action right behind it
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
            internal static void UpdateRoundViz()
            {
                bool show = debugZones && HoldingRound && !HoldingShell && heldRoundFor != null && TryGetLoadPoint(heldRoundFor, out Vector3 loadPoint);
                if (!show)
                {
                    return;
                }
                TryGetLoadPoint(heldRoundFor, out loadPoint);
                bool inZone = (RoundCenterWorld() - loadPoint).sqrMagnitude <= roundLoadRadius * roundLoadRadius;
                SetZoneViz(true, loadPoint, roundLoadRadius, inZone ? new Color(0f, 1f, 0f, 0.3f) : new Color(1f, 0.5f, 0.8f, 0.3f));
            }
        }
    }
}
