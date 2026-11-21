using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Interactive;
using EFT.InventoryLogic;
using Fika.Core.Main.ObservedClasses.HandsControllers;
using Fika.Core.Main.Players;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;
using FirearmController = EFT.Player.FirearmController;

namespace SptVrFikaSync
{
    // Draws a VR player's manual reload on their copy on this machine, from VRReloadPacket (sent by SPT-VR's VRReloadController.Sync).
    // Runs in the ObservedPlayer.LateUpdate postfix after ArmSyncApply has placed the gun (lower priority = later), so everything moves with the gun:
    //  - Tarkov's magazine-out, magazine-in and rechamber operations, started on this copy by the VR player's inventory moves, are finished
    //    the frame they start. Their animations would fight the synced arms, and a busy copy refuses the next move of a quick reload.
    //  - Gun parts the reload moves or hides, found by path under the gun's root. Their rest pose is recorded and restored when they stop coming.
    //  - The bolt catch and the bolt-open layer.
    //  - Magazines and rounds in the hands: the item's own model (found by id on this copy, or on the ground) at the synced pose.
    //    A ground magazine in the hand also has its world item hidden here.
    //  - Arms not attached to the gun's markers (a hand on a part, off the gun, or the gun in the left hand), moved to the synced wrists.
    //  - The reload sounds the VR player played, on this copy's gun (the skipped animations' own sounds don't play).
    //  - One-off events: the spent case thrown out by the bolt, and the chambered round shown as the bolt closes.
    // Only VR players send this packet; other players' copies are left alone. Gate: FikaVrSync.enableReloadSync.
    [HarmonyPatch]
    internal static class ReloadSyncApply
    {
        public static float staleTimeout = 0.5f;
        public static bool skipTarkovReloadAnimations = true;
        public static bool debug = false;

        private sealed class PropView
        {
            public GameObject model;
            public readonly List<Collider> disabledColliders = new List<Collider>();
            public readonly Dictionary<Transform, int> originalLayers = new Dictionary<Transform, int>();
            public Vector3 position;
            public Quaternion rotation;
        }

        private struct PartRest
        {
            public Transform parent;
            public Vector3 localPosition;
            public Quaternion localRotation;
            public bool active;
        }

        private sealed class Remote
        {
            public VRReloadPacket packet;
            public bool hasPacket;
            public float receivedAt;
            public bool drawing;
            public bool armsSmoothed;
            public Vector3 leftPosition, rightPosition;
            public Quaternion leftRotation, rightRotation;
            public readonly Dictionary<int, PropView> props = new Dictionary<int, PropView>();
            public readonly Dictionary<int, float> itemNotFoundAt = new Dictionary<int, float>();
            public readonly Dictionary<Transform, PartRest> parts = new Dictionary<Transform, PartRest>();
            public readonly HashSet<Transform> partsThisFrame = new HashSet<Transform>();
            public Transform pathsRoot;
            public readonly Dictionary<int, Transform> paths = new Dictionary<int, Transform>();
            public float pathsBuiltAt = -10f;
            public bool countersSeen;
            public int shellsThrown, roundsShown;
            public bool boltOpenWasDriven;
            public int hiddenGroundHash;
            public LootItem hiddenGround;
            public float groundNotFoundAt = -10f;
            public readonly List<Renderer> hiddenRenderers = new List<Renderer>();
            public readonly Queue<int> sounds = new Queue<int>();
            public FirearmController soundsFor;
            public BaseSoundPlayer soundPlayer;
            public readonly Dictionary<int, string> soundNames = new Dictionary<int, string>();
        }
        private static readonly Dictionary<int, Remote> remotes = new Dictionary<int, Remote>();

        private static Remote RemoteOf(int netId)
        {
            if (!remotes.TryGetValue(netId, out Remote remote))
            {
                remote = new Remote();
                remotes[netId] = remote;
            }
            return remote;
        }

        public static void Store(VRReloadPacket packet)
        {
            Remote remote = RemoteOf(packet.NetId);
            remote.packet = packet;
            remote.hasPacket = true;
            remote.receivedAt = Time.time;
        }

        public static void QueueSound(int netId, int nameHash)
        {
            Remote remote = RemoteOf(netId);
            if (remote.sounds.Count < 16)
                remote.sounds.Enqueue(nameHash);
        }

        // True when the VR player works the bolt by hand (BoltCycleSync then skips Tarkov's bolt cycle for them)
        public static bool WorksBoltByHand(int netId)
        {
            return remotes.TryGetValue(netId, out Remote remote) && remote.hasPacket && (remote.packet.Flags & VRReloadPacket.ManualBolt) != 0;
        }

        // Called on a new raid. The last raid's objects are gone, so there is nothing to restore.
        public static void ResetState()
        {
            remotes.Clear();
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Low)]
        [HarmonyPatch(typeof(ObservedPlayer), nameof(ObservedPlayer.LateUpdate))]
        private static void DrawReload(ObservedPlayer __instance)
        {
            try
            {
                if (!remotes.TryGetValue(__instance.NetId, out Remote remote))
                    return;
                FirearmController firearmController = __instance.HandsController as FirearmController;
                bool live = FikaVrSync.enableReloadSync && remote.hasPacket && remote.packet.Active && Time.time - remote.receivedAt <= staleTimeout
                    && !__instance.IsAI && __instance.HealthController != null && __instance.HealthController.IsAlive
                    && firearmController != null && firearmController.WeaponRoot != null && !__instance.IsSprintEnabled;
                if (!live)
                {
                    Release(remote, firearmController);
                    remote.sounds.Clear();
                    return;
                }
                Draw(__instance, firearmController, remote);
            }
            catch (Exception exception)
            {
                LogOnce(exception);
            }
        }

        private static void Draw(ObservedPlayer observed, FirearmController firearmController, Remote remote)
        {
            VRReloadPacket packet = remote.packet;
            Transform weaponRoot = firearmController.WeaponRoot;
            if (remote.pathsRoot != weaponRoot)
            {
                // The gun changed: restore the parts moved on the previous gun first
                RestoreParts(remote);
                remote.pathsRoot = weaponRoot;
                remote.paths.Clear();
                remote.pathsBuiltAt = -10f;
            }
            remote.drawing = true;

            if (skipTarkovReloadAnimations)
                SkipTarkovReload(firearmController, remote, (packet.Flags & VRReloadPacket.ManualReload) != 0);
            DrawAnimator(firearmController, remote, packet);
            DrawEvents(observed, firearmController, remote, packet);
            DrawParts(weaponRoot, remote, packet);
            DrawProps(observed, weaponRoot, remote, packet);
            HideGroundMagazine(remote, packet.HiddenGroundItem);
            DrawArms(observed, weaponRoot, remote, packet);
            PlaySounds(firearmController, remote);
        }

        // Restores everything to Tarkov's own state. Cheap when nothing was drawn.
        private static void Release(Remote remote, FirearmController firearmController)
        {
            if (!remote.drawing)
                return;
            remote.drawing = false;
            RestoreParts(remote);
            ReturnAllProps(remote);
            ShowGroundMagazine(remote);
            if (remote.boltOpenWasDriven && firearmController != null && firearmController.FirearmsAnimator != null)
                SetBoltOpen(firearmController.FirearmsAnimator, firearmController.FirearmsAnimator.GetBoltCatch() ? 1f : 0f);
            remote.boltOpenWasDriven = false;
            remote.armsSmoothed = false;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Finishes at once the Tarkov operations that the VR player's moves start on this copy (the same skips as
        // VRReloadController.TryForceExpectedOperation makes on their game).
        // A VR player always handles magazines by hand, with or without Manual Reload (Tarkov's B-button reload is a different operation and
        // is not touched here), so magazine operations are always skipped. Otherwise this copy plays the whole clip, and the host's "done"
        // reply to the VR player waits for it to end; FIKA blocks their trigger until then.
        // The charging-handle rechamber is only done by hand with Manual Reload on.
        private static void SkipTarkovReload(FirearmController firearmController, Remote remote, bool manualReload)
        {
            FirearmController.FirearmOperation operation = firearmController.CurrentOperation;
            if (operation is FirearmController.PullOutMagOperation)
            {
                // Restore moved parts first, so the magazine model goes back to Tarkov's pool as it came out
                RestoreParts(remote);
                firearmController.FastForwardCurrentState();
                FinishSkipped(firearmController, remote, "magazine out");
            }
            else if (operation is FirearmController.InsertMagOperation insertOperation)
            {
                RestoreParts(remote);
                // Not BSG's FastForward: it can switch to idle twice when there is no round to chamber
                insertOperation.OnMagAppeared();
                insertOperation.OnMagInsertedToWeapon();
                if (insertOperation.State != EFT.Player.EOperationState.Finished)
                    insertOperation.OnAddAmmoInChamber();
                PlayIdle(firearmController);
                FinishSkipped(firearmController, remote, "magazine in");
            }
            else if (manualReload && operation is FirearmController.RechamberOperation)
            {
                firearmController.FastForwardCurrentState();
                PlayIdle(firearmController);
                // While _isThrowingPatron is set, FIKA routes the next shell-eject event to this operation. The skip already called it, so clear it.
                if (firearmController is ObservedFirearmController observedController)
                    observedController._isThrowingPatron = false;
                FinishSkipped(firearmController, remote, "rechamber");
            }
        }

        // When a VR player clears a malfunction by hand, their game runs Tarkov's notice, examine and repair back to back, each skipped to
        // its end (SPT-VR's VRReloadController.RepairByHand). FIKA sends them as ExamineWeapon / CheckChamber packets that arrive together.
        // If they were only skipped at the next LateUpdate, the repair's CheckChamber would arrive while the examine still runs and be
        // refused, and this copy would keep the malfunction. So both are finished the moment the packet starts them.
        // This copy may also still be playing the jam animation (StartMalfunction, which ends on its animation's event) when the packets
        // arrive, so that is ended first.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(FirearmController), nameof(FirearmController.ExamineWeapon))]
        private static void EndJamBeatBeforeExamine(FirearmController __instance)
        {
            EndJamBeat(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FirearmController), nameof(FirearmController.CheckChamber))]
        private static void EndJamBeatBeforeCheck(FirearmController __instance)
        {
            EndJamBeat(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), nameof(FirearmController.ExamineWeapon))]
        private static void SkipMalfunctionExamine(FirearmController __instance)
        {
            SkipMalfunctionOperation(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FirearmController), nameof(FirearmController.CheckChamber))]
        private static void SkipMalfunctionRepair(FirearmController __instance)
        {
            SkipMalfunctionOperation(__instance);
        }

        private static void EndJamBeat(FirearmController firearmController)
        {
            try
            {
                if (!ClearsMalfunctionsByHand(firearmController, out _) || !(firearmController.CurrentOperation is FirearmController.StartMalfunction))
                    return;
                // BSG's own skip: applies the malfunction pose, then goes to idle
                firearmController.FastForwardCurrentState();
                if (debug)
                    FikaSyncPlugin.Log.LogInfo("[FikaSync] reload: the jam beat skipped to its end on a VR player's copy");
            }
            catch (Exception exception)
            {
                LogOnce(exception);
            }
        }

        // True for a VR player's copy with Manual Reload on
        private static bool ClearsMalfunctionsByHand(FirearmController firearmController, out Remote remote)
        {
            remote = null;
            return FikaVrSync.enableReloadSync && skipTarkovReloadAnimations && firearmController is ObservedFirearmController
                && firearmController._player is ObservedPlayer observed && remotes.TryGetValue(observed.NetId, out remote)
                && remote.hasPacket && (remote.packet.Flags & VRReloadPacket.ManualReload) != 0;
        }

        private static void SkipMalfunctionOperation(FirearmController firearmController)
        {
            try
            {
                if (!ClearsMalfunctionsByHand(firearmController, out Remote remote))
                    return;
                FirearmController.FirearmOperation operation = firearmController.CurrentOperation;
                bool repair = operation is FirearmController.RepairMalfunction;
                if (!repair && !(operation is FirearmController.ExamineMalfunctionTypeOperation))
                    return;
                firearmController.FastForwardCurrentState();
                PlayIdle(firearmController);
                IAnimator animator = firearmController.FirearmsAnimator?.Animator;
                if (animator != null)
                    AnimationControllerParametersTable.ResetTriggerLook(animator);
                FinishSkipped(firearmController, remote, repair ? "malfunction repair" : "malfunction examine");
            }
            catch (Exception exception)
            {
                LogOnce(exception);
            }
        }

        private static void PlayIdle(FirearmController firearmController)
        {
            FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
            firearmsAnimator?.Animator?.Play(firearmsAnimator.FullIdleStateName, 1, 0.1f);
        }

        // The reload block only clears on Idling's IdleStart event, which the skipped animation never sends, so it is sent here.
        // The gun's hierarchy may have changed (a magazine model back in the pool, a new one added), so the part paths are rebuilt.
        private static void FinishSkipped(FirearmController firearmController, Remote remote, string what)
        {
            if (firearmController.CurrentOperation is FirearmController.Idling idling)
                idling.OnIdleStartEvent();
            remote.paths.Clear();
            remote.pathsBuiltAt = -10f;
            if (debug)
                FikaSyncPlugin.Log.LogInfo($"[FikaSync] reload: {what} skipped to the end on a VR player's copy");
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        private static void DrawAnimator(FirearmController firearmController, Remote remote, VRReloadPacket packet)
        {
            FirearmsAnimator firearmsAnimator = firearmController.FirearmsAnimator;
            if (firearmsAnimator == null)
                return;
            bool boltCatch = (packet.Flags & VRReloadPacket.BoltCatch) != 0;
            if (firearmsAnimator.GetBoltCatch() != boltCatch)
                firearmsAnimator.SetBoltCatch(boltCatch);
            bool driven = (packet.Flags & VRReloadPacket.BoltOpenDriven) != 0;
            if (driven)
                SetBoltOpen(firearmsAnimator, packet.BoltOpen);
            else if (remote.boltOpenWasDriven)
                SetBoltOpen(firearmsAnimator, boltCatch ? 1f : 0f);
            remote.boltOpenWasDriven = driven;
        }

        // Same as VRReloadController.SetBoltOpen: 0 = closed, 1 = Tarkov's locked-back pose
        private static void SetBoltOpen(FirearmsAnimator firearmsAnimator, float open)
        {
            IAnimator animator = firearmsAnimator.Animator;
            if (animator == null)
                return;
            if (firearmsAnimator.BOLT_CATCH_LAYER_INDEX >= 0)
                animator.SetLayerWeight(firearmsAnimator.BOLT_CATCH_LAYER_INDEX, open);
            if (firearmsAnimator.HasParameter(AnimationControllerParametersTable.FLOAT_IDLEPOSITION))
                AnimationControllerParametersTable.SetIdlePosition(animator, open);
        }

        private static void DrawEvents(ObservedPlayer observed, FirearmController firearmController, Remote remote, VRReloadPacket packet)
        {
            if (!remote.countersSeen)
            {
                remote.countersSeen = true;
                remote.shellsThrown = packet.ShellsThrown;
                remote.roundsShown = packet.RoundsShown;
                return;
            }
            if (packet.ShellsThrown != remote.shellsThrown)
            {
                remote.shellsThrown = packet.ShellsThrown;
                try { firearmController.Firearms.StartSpawnShell(observed.Velocity * 0.66f); }
                catch (Exception exception) { if (debug) FikaSyncPlugin.Log.LogWarning($"[FikaSync] reload: no shell thrown: {exception.Message}"); }
            }
            if (packet.RoundsShown != remote.roundsShown)
            {
                remote.roundsShown = packet.RoundsShown;
                Weapon weapon = firearmController.Item;
                if (weapon != null && weapon.Chambers.Length > 0 && weapon.Chambers[0].ContainedItem is Ammo ammo)
                {
                    try { firearmController.Firearms.SetRoundIntoWeapon(ammo); }
                    catch (Exception exception) { if (debug) FikaSyncPlugin.Log.LogWarning($"[FikaSync] reload: chambered round not shown: {exception.Message}"); }
                }
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // The gun's parts, by path under its root

        private static void DrawParts(Transform weaponRoot, Remote remote, VRReloadPacket packet)
        {
            if (remote.paths.Count == 0 && Time.time - remote.pathsBuiltAt > 1f)
                BuildPaths(weaponRoot, remote);
            remote.partsThisFrame.Clear();
            bool missed = false;
            for (int index = 0; index < packet.PartCount; index++)
            {
                // Skip parts no longer on this gun: a model Tarkov took back to its pool isn't ours to move
                if (!remote.paths.TryGetValue(packet.PartPaths[index], out Transform part) || part == null || !part.IsChildOf(weaponRoot))
                {
                    missed = true;
                    continue;
                }
                if (!remote.parts.ContainsKey(part))
                {
                    remote.parts[part] = new PartRest
                    {
                        parent = part.parent,
                        localPosition = part.localPosition,
                        localRotation = part.localRotation,
                        active = part.gameObject.activeSelf,
                    };
                }
                part.localPosition = packet.PartPositions[index];
                part.localRotation = packet.PartRotations[index];
                if (part.gameObject.activeSelf != packet.PartShown[index])
                    part.gameObject.SetActive(packet.PartShown[index]);
                remote.partsThisFrame.Add(part);
            }
            // Parts no longer in the packet go back to their rest pose
            if (remote.parts.Count > remote.partsThisFrame.Count)
            {
                partScratch.Clear();
                foreach (KeyValuePair<Transform, PartRest> entry in remote.parts)
                {
                    if (!remote.partsThisFrame.Contains(entry.Key))
                        partScratch.Add(entry.Key);
                }
                foreach (Transform part in partScratch)
                {
                    RestorePart(part, remote.parts[part]);
                    remote.parts.Remove(part);
                }
            }
            // A missing part means the gun's hierarchy changed (a magazine swapped, a round model added): rebuild the paths, at most once a second
            if (missed && Time.time - remote.pathsBuiltAt > 1f)
                BuildPaths(weaponRoot, remote);
        }
        private static readonly List<Transform> partScratch = new List<Transform>();

        private static void RestoreParts(Remote remote)
        {
            foreach (KeyValuePair<Transform, PartRest> entry in remote.parts)
                RestorePart(entry.Key, entry.Value);
            remote.parts.Clear();
        }

        // Skips a part Tarkov has reparented since (a magazine model back in its pool): Tarkov owns it again
        private static void RestorePart(Transform part, PartRest rest)
        {
            if (part == null || part.parent != rest.parent)
                return;
            part.localPosition = rest.localPosition;
            part.localRotation = rest.localRotation;
            if (part.gameObject.activeSelf != rest.active)
                part.gameObject.SetActive(rest.active);
        }

        // Maps every transform under the root by the path SPT-VR sends: the names below the root joined by '/'. Of equal paths, the first wins.
        private static void BuildPaths(Transform weaponRoot, Remote remote)
        {
            remote.paths.Clear();
            remote.pathsBuiltAt = Time.time;
            pathBuilder.Clear();
            AddPaths(weaponRoot, remote.paths);
        }
        private static readonly System.Text.StringBuilder pathBuilder = new System.Text.StringBuilder(256);

        private static void AddPaths(Transform parent, Dictionary<int, Transform> paths)
        {
            int length = pathBuilder.Length;
            for (int index = 0; index < parent.childCount; index++)
            {
                Transform child = parent.GetChild(index);
                if (length > 0)
                    pathBuilder.Append('/');
                pathBuilder.Append(child.name);
                int hash = FikaVrSync.StableHash(pathBuilder.ToString());
                if (!paths.ContainsKey(hash))
                    paths[hash] = child;
                AddPaths(child, paths);
                pathBuilder.Length = length;
            }
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------
        // Magazines and rounds in the hands

        private static void DrawProps(ObservedPlayer observed, Transform weaponRoot, Remote remote, VRReloadPacket packet)
        {
            float blend = ArmSyncApply.smoothRate > 0f ? 1f - Mathf.Exp(-ArmSyncApply.smoothRate * Time.deltaTime) : 1f;
            propsThisFrame.Clear();
            for (int index = 0; index < packet.PropCount; index++)
            {
                int itemHash = packet.PropItems[index];
                propsThisFrame.Add(itemHash);
                if (!remote.props.TryGetValue(itemHash, out PropView view))
                {
                    if (remote.itemNotFoundAt.TryGetValue(itemHash, out float lastTry) && Time.time - lastTry < 1f)
                        continue;
                    Item item = FindItem(observed, itemHash);
                    if (item == null)
                    {
                        remote.itemNotFoundAt[itemHash] = Time.time;
                        continue;
                    }
                    remote.itemNotFoundAt.Remove(itemHash);
                    view = CreateProp(item, weaponRoot.gameObject.layer);
                    if (view == null)
                        continue;
                    view.position = packet.PropPositions[index];
                    view.rotation = packet.PropRotations[index];
                    remote.props[itemHash] = view;
                }
                if (view.model == null)
                    continue;
                view.position = Vector3.Lerp(view.position, packet.PropPositions[index], blend);
                view.rotation = Quaternion.Slerp(view.rotation, packet.PropRotations[index], blend);
                view.model.transform.SetPositionAndRotation(weaponRoot.position + weaponRoot.rotation * view.position, weaponRoot.rotation * view.rotation);
            }
            if (remote.props.Count > propsThisFrame.Count)
            {
                propScratch.Clear();
                foreach (int itemHash in remote.props.Keys)
                {
                    if (!propsThisFrame.Contains(itemHash))
                        propScratch.Add(itemHash);
                }
                foreach (int itemHash in propScratch)
                {
                    ReturnProp(remote.props[itemHash]);
                    remote.props.Remove(itemHash);
                }
            }
        }
        private static readonly HashSet<int> propsThisFrame = new HashSet<int>();
        private static readonly List<int> propScratch = new List<int>();

        private static void ReturnAllProps(Remote remote)
        {
            foreach (PropView view in remote.props.Values)
                ReturnProp(view);
            remote.props.Clear();
        }

        // Finds the item on this copy of the player (a magazine in the gun or the rig, the round's stack), else on the ground
        private static Item FindItem(ObservedPlayer observed, int itemHash)
        {
            IEnumerable<Item> items = observed.Inventory?.Equipment?.GetAllItems();
            if (items != null)
            {
                foreach (Item item in items)
                {
                    if (item != null && FikaVrSync.StableHash(item.Id.ToString()) == itemHash)
                        return item;
                }
            }
            LootItem lootItem = FindLootItem(itemHash);
            return lootItem != null ? lootItem.Item : null;
        }

        private static LootItem FindLootItem(int itemHash)
        {
            foreach (LootItem lootItem in UnityEngine.Object.FindObjectsOfType<LootItem>())
            {
                if (lootItem != null && lootItem.Item != null && FikaVrSync.StableHash(lootItem.Item.Id.ToString()) == itemHash)
                    return lootItem;
            }
            return null;
        }

        // Takes Tarkov's own model of the item from its pool, like SPT-VR's CreateProp. Its colliders are turned off and it is put on the
        // gun's layer; ReturnProp undoes both before the model goes back to the pool.
        private static PropView CreateProp(Item item, int layer)
        {
            ObjectsFactory objectsFactory = Singleton<ObjectsFactory>.Instance;
            if (objectsFactory == null)
                return null;
            GameObject model = objectsFactory.CreateItem(item, isAnimated: false);
            if (model == null)
                return null;
            PropView view = new PropView { model = model };
            foreach (Collider collider in model.GetComponentsInChildren<Collider>())
            {
                if (!collider.enabled)
                    continue;
                collider.enabled = false;
                view.disabledColliders.Add(collider);
            }
            foreach (Transform child in model.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                view.originalLayers[child] = child.gameObject.layer;
                child.gameObject.layer = layer;
            }
            model.SetActive(true);
            return view;
        }

        private static void ReturnProp(PropView view)
        {
            if (view == null || view.model == null)
                return;
            foreach (Collider collider in view.disabledColliders)
            {
                if (collider != null)
                    collider.enabled = true;
            }
            foreach (KeyValuePair<Transform, int> originalLayer in view.originalLayers)
            {
                if (originalLayer.Key != null)
                    originalLayer.Key.gameObject.layer = originalLayer.Value;
            }
            view.model.transform.SetParent(null, worldPositionStays: false);
            AssetPoolObject.ReturnToPool(view.model);
            view.model = null;
        }

        // Hides the world item of a ground magazine whose model is in the VR player's hand. It is hidden again every frame, as on their
        // game, because the loot drop schedules its own MakeVisible.
        private static void HideGroundMagazine(Remote remote, int itemHash)
        {
            if (itemHash != remote.hiddenGroundHash)
            {
                ShowGroundMagazine(remote);
                remote.hiddenGroundHash = itemHash;
                remote.groundNotFoundAt = -10f;
            }
            if (itemHash == 0)
                return;
            if (remote.hiddenGround == null)
            {
                if (Time.time - remote.groundNotFoundAt < 1f)
                    return;
                remote.hiddenGround = FindLootItem(itemHash);
                if (remote.hiddenGround == null)
                {
                    remote.groundNotFoundAt = Time.time;
                    return;
                }
            }
            remote.hiddenGround.MakeVisible(false);
            remote.hiddenGround.GetComponentsInChildren(true, rendererScratch);
            foreach (Renderer renderer in rendererScratch)
            {
                if (!renderer.enabled)
                    continue;
                renderer.enabled = false;
                remote.hiddenRenderers.Add(renderer);
            }
        }
        private static readonly List<Renderer> rendererScratch = new List<Renderer>();

        private static void ShowGroundMagazine(Remote remote)
        {
            if (remote.hiddenGround != null)
            {
                remote.hiddenGround.MakeVisible(true);
                foreach (Renderer renderer in remote.hiddenRenderers)
                {
                    if (renderer != null)
                        renderer.enabled = true;
                }
            }
            remote.hiddenRenderers.Clear();
            remote.hiddenGround = null;
            remote.hiddenGroundHash = 0;
        }

        //------------------------------------------------------------------------------------------------------------------------------------------------------------

        // Moves the arms that SPT-VR doesn't attach to the gun's markers onto the wrists the VR player's game rendered.
        // The poses are in the gun's frame, so they move with the gun.
        private static void DrawArms(ObservedPlayer observed, Transform weaponRoot, Remote remote, VRReloadPacket packet)
        {
            bool driveLeft = (packet.Flags & VRReloadPacket.DriveLeft) != 0;
            bool driveRight = (packet.Flags & VRReloadPacket.DriveRight) != 0;
            LimbIK[] limbs = observed._limbs;
            if ((!driveLeft && !driveRight) || limbs == null || limbs.Length < 2)
            {
                remote.armsSmoothed = false;
                return;
            }
            if (!remote.armsSmoothed || ArmSyncApply.smoothRate <= 0f)
            {
                remote.armsSmoothed = true;
                remote.leftPosition = packet.LeftPos; remote.leftRotation = packet.LeftRot;
                remote.rightPosition = packet.RightPos; remote.rightRotation = packet.RightRot;
            }
            else
            {
                float blend = 1f - Mathf.Exp(-ArmSyncApply.smoothRate * Time.deltaTime);
                remote.leftPosition = Vector3.Lerp(remote.leftPosition, packet.LeftPos, blend);
                remote.leftRotation = Quaternion.Slerp(remote.leftRotation, packet.LeftRot, blend);
                remote.rightPosition = Vector3.Lerp(remote.rightPosition, packet.RightPos, blend);
                remote.rightRotation = Quaternion.Slerp(remote.rightRotation, packet.RightRot, blend);
            }
            ArmSyncApply.SetElbowPoles(observed, limbs);
            if (driveLeft)
                ArmSyncApply.DriveArm(limbs[0], weaponRoot.position + weaponRoot.rotation * remote.leftPosition, weaponRoot.rotation * remote.leftRotation);
            if (driveRight)
                ArmSyncApply.DriveArm(limbs[1], weaponRoot.position + weaponRoot.rotation * remote.rightPosition, weaponRoot.rotation * remote.rightRotation);
        }

        // Plays each forwarded sound by its event name on this copy's gun. As with eating sounds, the hash is resolved against the gun's sound bank.
        private static void PlaySounds(FirearmController firearmController, Remote remote)
        {
            if (remote.sounds.Count == 0)
                return;
            if (remote.soundsFor != firearmController)
            {
                remote.soundsFor = firearmController;
                remote.soundPlayer = firearmController._controllerObject != null ? firearmController._controllerObject.GetComponentInChildren<BaseSoundPlayer>(true) : null;
                remote.soundNames.Clear();
            }
            while (remote.sounds.Count > 0)
            {
                int nameHash = remote.sounds.Dequeue();
                if (remote.soundPlayer == null)
                    continue;
                if (!remote.soundNames.TryGetValue(nameHash, out string name))
                {
                    name = ResolveSoundName(remote.soundPlayer, nameHash);
                    remote.soundNames[nameHash] = name;
                }
                if (name != null)
                    remote.soundPlayer.SoundEventHandler(name);
            }
        }

        // SPT-VR sends the names its reload uses (MagOut, BoltOut ...). The gun's sound bank names them with or without a "Snd" prefix.
        private static string ResolveSoundName(BaseSoundPlayer soundPlayer, int nameHash)
        {
            foreach (var sound in soundPlayer.AdditionalSounds)
            {
                string eventName = sound.EventName;
                if (string.IsNullOrEmpty(eventName))
                    continue;
                if (FikaVrSync.StableHash(eventName) == nameHash)
                    return eventName;
                if (eventName.StartsWith("Snd") && FikaVrSync.StableHash(eventName.Substring(3)) == nameHash)
                    return eventName.Substring(3);
            }
            return null;
        }

        private static bool loggedError;
        private static void LogOnce(Exception exception)
        {
            if (loggedError)
                return;
            loggedError = true;
            FikaSyncPlugin.Log.LogError($"[FikaSync] reload draw error: {exception}");
        }
    }

    // A VR player works a bolt-action's bolt by hand. On their copy, FIKA's bolt cycle (HandleObservedBoltAction: the cycle animation, then
    // the next round into the chamber at its shell-eject event) is replaced by moving the round at once. That matches the VR player's own
    // game, where Tarkov chambers a bolt-action's next round as it fires. The spent case is thrown when their bolt comes back
    // (ReloadSyncApply's shell counter). SPT-VR sends the cycle packet itself (VRArmSync.SendBoltCycle), because FIKA only sends one from a
    // fire operation, which the manual bolt ends first.
    [HarmonyPatch]
    internal static class BoltCycleSync
    {
        public static bool enabled = true;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(ObservedFirearmController), nameof(ObservedFirearmController.HandleObservedBoltAction))]
        private static bool CycleWithoutAnimation(ObservedFirearmController __instance)
        {
            try
            {
                ObservedPlayer observed = __instance._observedPlayer;
                if (!enabled || observed == null || !ArmSyncApply.IsVrPlayer(observed.NetId) || !ReloadSyncApply.WorksBoltByHand(observed.NetId))
                    return true;
                Weapon weapon = __instance.Item;
                Magazine magazine = weapon != null ? weapon.GetCurrentMagazine() : null;
                if (magazine != null && !(magazine is CylinderMagazine) && weapon.HasChambers && weapon.Chambers[0].ContainedItem == null)
                    magazine.Cartridges.PopTo(observed.InventoryController, weapon.Chambers[0].CreateItemAddress());
                return false;
            }
            catch (Exception exception)
            {
                FikaSyncPlugin.Log.LogError($"[FikaSync] bolt cycle on a VR player's copy failed, FIKA's own runs: {exception}");
                return true;
            }
        }
    }
}
