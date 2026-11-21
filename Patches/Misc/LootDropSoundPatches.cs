using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using JsonType;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace TarkovVR.Patches.Misc
{
    // EFT only voices a loose item landing if its template has a DropSoundType other than None, and that enum only holds
    // weapon classes (Pistol/SubMachineGun/Rifle), so every non-weapon lands silently. This gives those items a drop sound.
    // Weapons are never touched here: they keep vanilla's own surface-aware gun banks.
    //
    // dropSoundMode -- which layers play:
    //   InventorySounds -- the item's inventory "_drop" foley (GUISounds/ItemSounds): sounds like the material, ignores the surface.
    //   SurfaceOnly     -- only a per-surface thud, no material sound.
    //   Layered (default) -- both.
    // surfaceSource -- where the surface thud comes from:
    //   FootstepBanks (default) -- the player's footstep SurfaceSet (Sounds.GetSurfaceSet): a per-surface body thud
    //                      (ProneDrop/Landing) with no gun rattle.
    //   GunBanks        -- the gun drop banks (ItemDropSounds), class picked by weight; every clip has gun clatter in it.
    //
    // Co-op (FIKA): a dropped item is an ObservedLootItem on every machine. The dropper simulates it locally
    // (_isNetworkGame == false), so vanilla OnCollisionEnter fires. Everyone else observes it (_isNetworkGame == true): LootSyncStruct ->
    // ApplyNetPacket -> PlayDropSound() when it settles, the same path weapon drops sync on. Both are hooked and split by
    // _isNetworkGame, so no machine plays it twice. Only EFT types are referenced (no FIKA types), so this is harmless in solo.
    [HarmonyPatch]
    internal class LootDropSoundPatches
    {
        public enum LootDropSoundMode { InventorySounds, SurfaceOnly, Layered }
        public enum SurfaceBankSource { FootstepBanks, GunBanks }
        public enum FootstepSurfaceKind { ProneDrop, Landing }

        public static LootDropSoundMode dropSoundMode = LootDropSoundMode.Layered;
        public static SurfaceBankSource surfaceSource = SurfaceBankSource.FootstepBanks;
        public static FootstepSurfaceKind footstepKind = FootstepSurfaceKind.ProneDrop; // ProneDrop = heavier thud; Landing = lighter

        // GunBanks weight split (kg): < lightWeightMax -> Pistol, >= heavyWeightMin -> Rifle, else SubMachineGun.
        public static float lightWeightMax = 1.0f;
        public static float heavyWeightMin = 3.0f;

        // Only voice a real impact (not a gentle placement), and don't play again on every bounce.
        public static float minImpactSpeed = 1.2f;
        public static float retriggerDelay = 0.4f;

        // InventorySounds playback (mirrors vanilla LootItem.RemoveLootItem's pickup clip).
        public static int invRolloff = 10;
        public static float invVolume = 1f;

        // Surface thud volume scales with the item's current weight: silent below surfaceMinWeight, then
        // rising from surfaceVolMin just above the cutoff to surfaceVolMax at surfaceVolMaxWeight.
        public static float surfaceVolMin = 0.02f;
        public static float surfaceVolMax = 0.5f;
        public static float surfaceVolMaxWeight = 10.0f; // kg at which the thud reaches surfaceVolMax

        // Below this weight (kg) an item is too light to thud, so the surface bank is skipped
        // (Layered keeps only the material foley; SurfaceOnly is silent). Ammo ~0.01, bandage ~0.15, can ~0.5.
        public static float surfaceMinWeight = 0.4f;

        // A harder throw or drop is louder. Impact speed (m/s) maps to a volume multiplier between velVolumeSoft
        // and velVolumeLoud. Each layer has an influence (0 = ignore velocity, 1 = full multiplier), so the
        // inventory foley follows it more than the surface thud.
        public static float velSpeedSoft = 1.5f;         // m/s -- gentle drop -> velVolumeSoft
        public static float velSpeedHard = 8f;           // m/s -- hard throw -> velVolumeLoud
        public static float velVolumeSoft = 0.4f;
        public static float velVolumeLoud = 1.5f;
        public static float invVelInfluence = 1.0f;      // inventory foley follows velocity fully
        public static float surfaceVelInfluence = 0.35f; // surface thud follows velocity only a little

        // Per-item retrigger time. The weak table drops an entry when its LootItem is destroyed, so it doesn't leak.
        private static readonly ConditionalWeakTable<LootItem, StrongBox<float>> lastPlayed =
            new ConditionalWeakTable<LootItem, StrongBox<float>>();

        // Peak networked speed per observed item. By the time the settle fires, the item has almost stopped, so the
        // fastest in-flight packet velocity is kept to scale a co-op observer's volume by how hard it was thrown.
        private static readonly ConditionalWeakTable<LootItem, StrongBox<float>> peakNetSpeed =
            new ConditionalWeakTable<LootItem, StrongBox<float>>();

        // On the dropper's machine and in single player, vanilla fires OnCollisionEnter from real collisions.
        // Networked observers (_isNetworkGame == true) are driven by packets instead and handled in PlayDropSound.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LootItem), "OnCollisionEnter")]
        private static void VoiceOnImpact(LootItem __instance, Collision collision)
        {
            if (__instance is ObservedLootItem observedItem && observedItem._isNetworkGame)
                return;

            float impact = collision.relativeVelocity.magnitude;
            if (impact < minImpactSpeed)
                return;

            Vector3 position = (collision.contacts.Length > 0) ? collision.contacts[0].point : __instance.transform.position;
            BaseBallistic ballistic = collision.collider ? collision.collider.GetComponent<BaseBallistic>() : null;
            BaseBallistic.ESurfaceSound surface = (ballistic != null)
                ? ballistic.GetSurfaceSound(position)
                : BaseBallistic.ESurfaceSound.Concrete;

            Voice(__instance, position, surface, impact);
        }

        // Observer side in co-op: ObservedLootItem.ApplyNetPacket calls this when the networked item settles (the
        // path that syncs weapon drops). Only a networked observer gets here.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(LootItem), "PlayDropSound")]
        private static void VoiceOnNetworkedSettle(LootItem __instance)
        {
            if (!(__instance is ObservedLootItem observedItem) || !observedItem._isNetworkGame)
                return;

            Vector3 position = __instance.transform.position;
            __instance.CheckSurfaceImmediately(position);            // vanilla downward surface raycast -> sets _currentSurface
            float speed = peakNetSpeed.TryGetValue(__instance, out StrongBox<float> peak) ? peak.Value : velSpeedSoft;
            Voice(__instance, position, __instance._currentSurface, speed);
        }

        // Observer side: keeps the fastest velocity the synced item reaches in flight, so the settle can
        // scale volume by how hard it was thrown. ApplyNetPacket sets _rigidBody.velocity from each packet.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ObservedLootItem), "ApplyNetPacket")]
        private static void TrackNetSpeed(ObservedLootItem __instance)
        {
            Rigidbody body = __instance._rigidBody;
            if (body == null)
                return;
            StrongBox<float> peak = peakNetSpeed.GetOrCreateValue(__instance);
            float speed = body.velocity.magnitude;
            if (speed > peak.Value)
                peak.Value = speed;
        }

        private static void Voice(LootItem instance, Vector3 pos, BaseBallistic.ESurfaceSound surface, float impact)
        {

            // ItemOwner.RootItem is the reliable accessor for the item (vanilla reads the same one for the
            // pickup sound). Weapons have a real DropSoundType and vanilla already voiced them, so skip them.
            Item item = instance.ItemOwner?.RootItem;
            if (item == null || item.DropSoundType != EItemDropSoundType.None)
                return;

            StrongBox<float> last = lastPlayed.GetOrCreateValue(instance);
            if (Time.time - last.Value < retriggerDelay)
                return;
            last.Value = Time.time;

            switch (dropSoundMode)
            {
                case LootDropSoundMode.InventorySounds:
                    PlayInventoryDrop(item, pos, impact);
                    break;
                case LootDropSoundMode.SurfaceOnly:
                    PlaySurfaceBank(item, surface, pos, impact);
                    break;
                case LootDropSoundMode.Layered:
                    PlayInventoryDrop(item, pos, impact);
                    PlaySurfaceBank(item, surface, pos, impact);
                    break;
            }
        }

        // Maps impact speed to a per-layer volume multiplier (velVolumeSoft..velVolumeLoud), blended by how
        // much that layer should respond to velocity (influence 0 = none, 1 = full).
        private static float VelocityMult(float impact, float influence)
        {
            float multiplier = Mathf.Lerp(velVolumeSoft, velVolumeLoud, Mathf.InverseLerp(velSpeedSoft, velSpeedHard, impact));
            return Mathf.Lerp(1f, multiplier, influence);
        }

        // Plays the item's "<category>_drop" inventory clip in 3D at the landing point through BetterAudio, like the
        // vanilla pickup sound. A category with no clip stays silent. Volume mostly follows the throw or drop speed.
        private static void PlayInventoryDrop(Item item, Vector3 pos, float impact)
        {
            AudioClip clip = Singleton<GUISounds>.Instance.GetItemClip(item.ItemSound, EInventorySoundType.drop);
            if (clip != null)
                MonoBehaviourSingleton<BetterAudio>.Instance.TryPlayAtPoint(out _, pos, clip,
                    BetterAudio.AudioSourceGroupType.Character, invRolloff, invVolume * VelocityMult(impact, invVelInfluence));
        }

        // Plays a per-surface thud: either the footstep SurfaceSet bank (no gun sound) or a gun bank picked by weight.
        // Volume scales with the item's current weight, including contents, and a little with impact speed.
        private static void PlaySurfaceBank(Item item, BaseBallistic.ESurfaceSound surface, Vector3 pos, float impact)
        {
            BetterAudio betterAudio = Singleton<BetterAudio>.Instance;
            if (betterAudio == null)
                return;

            // TotalWeight is the current weight including contents (loaded mag, filled rig or backpack, ammo stack),
            // the same value the inspect window shows, not the empty Template.Weight.
            float weight = item.TotalWeight;
            if (weight < surfaceMinWeight)
                return; // too light to thud -- only the material foley (if any) plays

            var bank = ResolveSurfaceBank(betterAudio, surface, weight);
            if (bank == null)
                return;

            // surfaceVolMin at the cutoff, rising to surfaceVolMax at surfaceVolMaxWeight and clamped above it,
            // then scaled by impact speed (less than the inventory layer).
            float volume = Mathf.Lerp(surfaceVolMin, surfaceVolMax, Mathf.InverseLerp(surfaceMinWeight, surfaceVolMaxWeight, weight))
                * VelocityMult(impact, surfaceVelInfluence);

            float distance = CameraManager.Instance.Distance(pos);
            betterAudio.PlayAtPoint(pos, bank, distance, volume, -1f, EnvironmentType.Outdoor, EOcclusionTest.OneShotPropagation);
        }

        // FootstepBanks: the player's per-surface SurfaceSet thud (surface only, no gun). GunBanks: the gun
        // drop bank for the surface, with the gun class picked by weight.
        private static SoundBank ResolveSurfaceBank(BetterAudio ba, BaseBallistic.ESurfaceSound surface, float weight)
        {
            if (surfaceSource == SurfaceBankSource.FootstepBanks)
            {
                // _playerSounds is the local player's footstep Sounds asset. The surface table is shared, so it
                // works for any drop, including remote ones we observe.
                var player = VRGlobals.player;
                if (player == null || player._playerSounds == null)
                    return null;
                var set = player._playerSounds.GetSurfaceSet(surface);
                if (set == null)
                    return null;
                return footstepKind == FootstepSurfaceKind.ProneDrop ? set.ProneDropSoundBank : set.LandingSoundBank;
            }

            if (ba.ItemDropSounds == null)
                return null;
            EItemDropSoundType type =
                weight < lightWeightMax ? EItemDropSoundType.Pistol :
                weight < heavyWeightMin ? EItemDropSoundType.SubMachineGun :
                                          EItemDropSoundType.Rifle;
            return ba.ItemDropSounds.GetSoundBank(surface, type);
        }
    }
}
