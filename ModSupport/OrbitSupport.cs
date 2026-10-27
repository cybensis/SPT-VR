using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using Orbit.Core;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;
using UnityEngine.AI;

namespace TarkovVR.ModSupport.ORBIT
{
    internal static class OrbitSupport
    {
        private static readonly Dictionary<Corpse, int> _heldCorpses = new Dictionary<Corpse, int>();

        public static void OnCorpseGrabbed(Corpse corpse)
        {
            if (corpse == null || !InstalledMods.ORBITInstalled)
                return;
            PullCorpseWaypoint(corpse);
        }

        public static void OnDraggedCorpseSettled(Corpse corpse)
        {
            if (corpse == null)
                return;

            Vector3 pelvis;
            try
            {
                Transform t = corpse.TrackableTransform != null ? corpse.TrackableTransform : corpse.transform;
                pelvis = t.position;
            }
            catch { return; }

            // Snap to the navmesh so the bot has a reachable point (the body may rest against a wall / on
            // cover slightly off-mesh; BSG nav-snaps the final approach within a couple metres anyway).
            Vector3 navPos = pelvis;
            if (NavMesh.SamplePosition(pelvis, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                navPos = hit.position;

            // vanilla EFT body record (for when you're not using Orbit)
            RefreshVanillaDeadBody(corpse, navPos, pelvis);

            // ORBIT: re-add the pulled loot waypoint at the resting spot. Gated so the ORBIT-typed helper is
            // never JITted on a no-ORBIT load.
            if (InstalledMods.ORBITInstalled)
                ReaddCorpseWaypoint(corpse, navPos);
        }

        // ─── ORBIT (NoInlining: only JITted when ORBIT is present) ─────────────────────────────────────
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PullCorpseWaypoint(Corpse corpse)
        {
            try
            {
                WaypointSystem ws = Singleton<OrbitManager>.Instance?.WaypointSystem;
                if (ws == null) return; // no raid / not host (clients have no OrbitManager)
                if (_heldCorpses.ContainsKey(corpse)) return; // already pulled (re-grab / double event)

                Waypoint wp = FindCorpseWaypoint(ws, corpse);
                if (wp == null)
                {
                    // ORBIT wasn't tracking this body (already looted / not registered) — nothing to protect.
                    if (Plugin.debugBodyDragLoot)
                        Plugin.MyLog.LogInfo("[OrbitSupport] grab: no ORBIT waypoint to pull for this corpse");
                    return;
                }

                // Preserve the kill-credit squad tag before RemoveWaypoint drops it
                int killerSquad = -1;
                if (ws._corpseKillerSquadId != null && ws._corpseKillerSquadId.TryGetValue(wp.Id, out int sq))
                    killerSquad = sq;

                ws.RemoveWaypoint(wp.Id);
                _heldCorpses[corpse] = killerSquad;
                if (Plugin.debugBodyDragLoot)
                    Plugin.MyLog.LogInfo($"[OrbitSupport] pulled corpse waypoint {wp.Id} on grab (unlootable while dragged, killerSquad={killerSquad})");
            }
            catch (Exception e)
            {
                Plugin.MyLog.LogWarning($"[OrbitSupport] pull failed: {e.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReaddCorpseWaypoint(Corpse corpse, Vector3 newPos)
        {
            try
            {
                // Only re-add if we pulled it on grab — i.e. ORBIT WAS tracking it. Avoids fabricating a loot
                // waypoint for a corpse ORBIT never knew about.
                if (!_heldCorpses.TryGetValue(corpse, out int killerSquad))
                    return;
                _heldCorpses.Remove(corpse);

                WaypointSystem ws = Singleton<OrbitManager>.Instance?.WaypointSystem;
                if (ws == null) return;

                // Rebuild a corpse waypoint identical to ORBIT's own registration (CorpseRegistrationPatch:
                // Corpse category, corpse.name, radiusSqr 3*3, empty door/cover lists) — only the position
                // and the fresh id differ.
                Waypoint moved = new Waypoint(
                    ws.NewRuntimeWaypointId(), WaypointCategory.Corpse, corpse.name, newPos,
                    3f * 3f, new List<Door>(), new List<CoverPoint>(), corpse);

                if (ws.AddRuntimeWaypoint(moved))
                {
                    if (killerSquad >= 0)
                        ws.TagCorpseKillerSquad(moved.Id, killerSquad);
                    if (Plugin.debugBodyDragLoot)
                        Plugin.MyLog.LogInfo($"[OrbitSupport] re-added corpse waypoint {moved.Id} at settle {newPos} (killerSquad={killerSquad})");
                }
                else if (Plugin.debugBodyDragLoot)
                {
                    Plugin.MyLog.LogWarning($"[OrbitSupport] AddRuntimeWaypoint rejected settle pos {newPos} (outside grid) — body left untracked");
                }
            }
            catch (Exception e)
            {
                Plugin.MyLog.LogWarning($"[OrbitSupport] re-add failed: {e.Message}");
            }
        }

        // Find the corpse's current loot waypoint (its Target is this corpse). One grid scan per settle —
        // dragging is a rare user action, so it isn't worth a cross-raid cache.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Waypoint FindCorpseWaypoint(WaypointSystem ws, Corpse corpse)
        {
            Cell[,] cells = ws.Cells;
            if (cells == null) return null;
            int w = cells.GetLength(0);
            int h = cells.GetLength(1);
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    List<Waypoint> list = cells[x, y].Waypoints;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Waypoint wp = list[i];
                        if (wp != null && ReferenceEquals(wp.Target, corpse))
                            return wp;
                    }
                }
            }
            return null;
        }

        private static void RefreshVanillaDeadBody(Corpse corpse, Vector3 navPos, Vector3 pelvis)
        {
            try
            {
                string profileId = corpse.PlayerProfileID;
                if (string.IsNullOrEmpty(profileId))
                    return;
                IBotGame botGame = Singleton<IBotGame>.Instance;
                DeadBodiesController dbc = botGame?.BotsController?.BotSpawner?._deadBodiesController;
                List<DeadBody> bodies = dbc?.Bodies;
                if (bodies == null)
                    return;
                for (int i = 0; i < bodies.Count; i++)
                {
                    DeadBody body = bodies[i];
                    if (body?.Player == null || body.Player.ProfileId != profileId)
                        continue;
                    body.Position = navPos;       // the nav target bots path to
                    body._bodyPosition = pelvis;      // BodyPosition (used by some search heuristics)
                    body.IsOnNavMesh = true;      // we navmesh-snapped navPos
                    if (Plugin.debugBodyDragLoot)
                        Plugin.MyLog.LogInfo($"[OrbitSupport] refreshed vanilla DeadBody for {profileId} -> {navPos}");
                    break;
                }
            }
            catch (Exception e)
            {
                if (Plugin.debugBodyDragLoot)
                    Plugin.MyLog.LogWarning($"[OrbitSupport] vanilla DeadBody refresh failed: {e.Message}");
            }
        }
    }
}
