using System;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Fika.Core.Main.Utils;
using UnityEngine;

namespace SptVrFikaSync
{
    // Receiver for CorpseLootPacket. Runs on EVERY peer but ACTS only on the HOST/HEADLESS (FikaBackendUtils
    // .IsServer) — the machine that actually runs the bots + looting AI. A VR client that drags a corpse
    // can't update the AI itself (its OrbitManager / bot game are null), so it sends this and the server does
    // the work here.
    //
    // What the server does with a dragged body:
    //   * ORBIT (if installed): pull the loot waypoint on grab, re-add it at the rest spot on settle
    //     (OrbitSync). ORBIT loots logically, so the waypoint is what matters — not the body's collider.
    //   * LootingBots / base SPT (live collider scanners): relocate the server's ragdoll copy to the rest
    //     spot on settle, so they detect the body where it was hidden, not where it died. The headless never
    //     applies the body-drag sync (that's keyed on ObservedPlayersCorpses, which is empty on a headless),
    //     so the server's copy otherwise stays at the death spot forever.
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

        // The dead entity's Corpse on this machine, by profile id. GetEverExistedPlayerByID keeps the player
        // (incl. dead bots) so its .Corpse is reachable — the same identity ORBIT's waypoint Target carries.
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

        // Teleport the server's settled ragdoll to the rest spot by offsetting EVERY bone (and the root) by
        // the pelvis delta — preserves the pose, moves the colliders. This is what makes a live collider
        // scanner (LootingBots) find the body where the player dropped it. The bones are kinematic once the
        // corpse has settled, so a transform write holds; even if EFT has torn the rigidbodies down, the
        // bone GameObjects (and their colliders) still exist, so the offset still lands the colliders.
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
                corpse.transform.position += delta; // keep the corpse root consistent with the bones
            }
            catch (Exception e) { FikaSyncPlugin.Log.LogWarning($"[CorpseLoot] relocate failed: {e.Message}"); }
        }
    }
}
