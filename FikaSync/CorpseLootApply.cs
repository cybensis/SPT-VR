using System;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receiver for CorpseLootPacket. Runs on every peer but acts only on the host/headless (FikaBackendUtils.IsServer), which runs
    // the bots and the looting AI. A VR client dragging a corpse can't update the AI itself (its OrbitManager and bot game are null).
    //
    // What the server does with a dragged body:
    //   * ORBIT (if installed) loots by waypoint, not by the body's collider. OrbitSync removes the loot waypoint on grab
    //     and re-adds it at the rest spot on settle.
    //   * LootingBots and base SPT scan the body's colliders, so the server's ragdoll copy is moved to the rest spot on settle.
    //     A headless never applies the body-drag sync (it is keyed on ObservedPlayersCorpses, which is empty there),
    //     so without this the server's copy stays where the body died.
    internal static class CorpseLootApply
    {
        public static void Apply(CorpseLootPacket p)
        {
            // Only the raid/bot host acts. Clients have no bots; the dragger already moved its own local copy.
            if (!FikaBackendUtils.IsServer)
                return;

            Corpse corpse = ResolveCorpse(p.ProfileId);
            if (corpse == null)
            {
                FikaSyncPlugin.Log.LogWarning($"[CorpseLoot] could not resolve corpse for profile {p.ProfileId} on host");
                return;
            }

            if (!p.Released)
            {
                OrbitSync.OnGrab(corpse);
            }
            else
            {
                RelocateRagdoll(corpse, p.RestPos);
                OrbitSync.OnSettle(corpse, p.RestPos);
            }
        }

        // The dead player's Corpse on this machine, by profile id. GetEverExistedPlayerByID also returns dead bots, so their
        // .Corpse is reachable. This is the same identity ORBIT's waypoint Target carries.
        private static Corpse ResolveCorpse(string profileId)
        {
            if (string.IsNullOrEmpty(profileId))
                return null;
            GameWorld gw = Singleton<GameWorld>.Instance;
            if (gw == null)
                return null;
            try
            {
                Player player = gw.GetEverExistedPlayerByID(profileId);
                return player != null ? player.Corpse : null;
            }
            catch (Exception e)
            {
                FikaSyncPlugin.Log.LogWarning($"[CorpseLoot] resolve failed: {e.Message}");
                return null;
            }
        }

        // Teleports the server's settled ragdoll to the rest spot by moving every bone (and the root) by the pelvis offset.
        // This keeps the pose and moves the colliders. The bones are kinematic once the corpse has settled, so a transform
        // write holds. If EFT has already removed the rigidbodies, the bone GameObjects and their colliders still exist.
        private static void RelocateRagdoll(Corpse corpse, Vector3 restPos)
        {
            try
            {
                CorpseRagdoll rag = corpse.Ragdoll;
                Transform pelvis = corpse.TrackableTransform;
                if (rag == null || rag._rigidbodySpawners == null || pelvis == null)
                    return;
                Vector3 delta = restPos - pelvis.position;
                if (delta.sqrMagnitude < 0.0001f)
                    return;
                RigidbodySpawner[] bones = rag._rigidbodySpawners;
                for (int i = 0; i < bones.Length; i++)
                {
                    Transform t = bones[i] != null ? bones[i].transform : null;
                    if (t != null)
                        t.position += delta;
                }
                corpse.transform.position += delta; // keep the corpse root with the bones
            }
            catch (Exception e) { FikaSyncPlugin.Log.LogWarning($"[CorpseLoot] relocate failed: {e.Message}"); }
        }
    }
}
