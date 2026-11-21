using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
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
        // Corpses whose ORBIT waypoint is removed while dragged, mapped to the ORBIT state saved for them (an OrbitCorpseCarry, stored
        // as object: a typed static field would load ORBIT's types when ORBIT isn't installed).
        private static readonly Dictionary<Corpse, object> _heldCorpses = new Dictionary<Corpse, object>();

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
                Transform bodyTransform = corpse.TrackableTransform != null ? corpse.TrackableTransform : corpse.transform;
                pelvis = bodyTransform.position;
            }
            catch { return; }

            // Snap to the navmesh so bots get a reachable point. The body may rest against a wall or on cover slightly
            // off the mesh; BSG nav-snaps the final approach within a couple of metres anyway.
            Vector3 navPos = pelvis;
            if (NavMesh.SamplePosition(pelvis, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                navPos = hit.position;

            // The vanilla EFT dead-body record, which bots use when ORBIT isn't installed.
            RefreshVanillaDeadBody(corpse, navPos, pelvis);

            // ORBIT: re-add the removed loot waypoint at the resting spot. Gated so the ORBIT-typed helper is
            // never JIT-compiled without ORBIT.
            if (InstalledMods.ORBITInstalled)
                ReaddCorpseWaypoint(corpse, navPos);
        }

        // ─── ORBIT (NoInlining: only JITted when ORBIT is present) ─────────────────────────────────────
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PullCorpseWaypoint(Corpse corpse)
        {
            try
            {
                WaypointSystem waypointSystem = Singleton<OrbitManager>.Instance?.WaypointSystem;
                if (waypointSystem == null) return; // no raid / not host (clients have no OrbitManager)
                if (_heldCorpses.ContainsKey(corpse)) return; // already pulled (re-grab / double event)

                Waypoint waypoint = FindCorpseWaypoint(waypointSystem, corpse);
                if (waypoint == null)
                {
                    // ORBIT wasn't tracking this body (already looted / not registered) — nothing to protect.
                    if (Plugin.debugBodyDragLoot)
                        Plugin.MyLog.LogInfo("[OrbitSupport] grab: no ORBIT waypoint to pull for this corpse");
                    return;
                }

                // Save what ORBIT keeps about the corpse besides the waypoint (kill credit, own-kill queues, boss body...)
                // before RemoveWaypoint drops the parts keyed by the waypoint id.
                OrbitCorpseCarry carry = OrbitCorpseCarry.Capture(waypointSystem, waypoint, Warn);
                waypointSystem.RemoveWaypoint(waypoint.Id);
                _heldCorpses[corpse] = carry;
                if (Plugin.debugBodyDragLoot)
                    Plugin.MyLog.LogInfo($"[OrbitSupport] pulled corpse waypoint {waypoint.Id} on grab (unlootable while dragged; {carry.Summary()})");
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogWarning($"[OrbitSupport] pull failed: {exception.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReaddCorpseWaypoint(Corpse corpse, Vector3 newPos)
        {
            try
            {
                // Only re-add if we removed it on grab (ORBIT was tracking it): no loot waypoint for a corpse ORBIT never knew.
                if (!_heldCorpses.TryGetValue(corpse, out object held) || !(held is OrbitCorpseCarry carry))
                    return;
                _heldCorpses.Remove(corpse);

                WaypointSystem waypointSystem = Singleton<OrbitManager>.Instance?.WaypointSystem;
                if (waypointSystem == null) return;

                if (OrbitCorpseCarry.InDangerZone(newPos))
                {
                    if (Plugin.debugBodyDragLoot)
                        Plugin.MyLog.LogInfo($"[OrbitSupport] corpse dragged into a minefield / border zone: no loot waypoint (ORBIT's own rule)");
                    return;
                }

                // Rebuild a corpse waypoint identical to ORBIT's own registration (CorpseRegistrationPatch:
                // Corpse category, corpse.name, radiusSqr 3*3, empty door/cover lists) — only the position
                // and the fresh id differ.
                Waypoint moved = new Waypoint(
                    waypointSystem.NewRuntimeWaypointId(), WaypointCategory.Corpse, corpse.name, newPos,
                    3f * 3f, new List<Door>(), new List<CoverPoint>(), corpse);

                if (waypointSystem.AddRuntimeWaypoint(moved))
                {
                    carry.Restore(waypointSystem, moved, Warn);
                    if (Plugin.debugBodyDragLoot)
                        Plugin.MyLog.LogInfo($"[OrbitSupport] re-added corpse waypoint {carry.PulledId} -> {moved.Id} at settle {newPos} ({carry.Summary()})");
                }
                else if (Plugin.debugBodyDragLoot)
                {
                    Plugin.MyLog.LogWarning($"[OrbitSupport] AddRuntimeWaypoint rejected settle pos {newPos} (outside grid) — body left untracked");
                }
            }
            catch (Exception exception)
            {
                Plugin.MyLog.LogWarning($"[OrbitSupport] re-add failed: {exception.Message}");
            }
        }

        private static void Warn(string message)
        {
            Plugin.MyLog.LogWarning($"[OrbitSupport] carrying a corpse's ORBIT state: {message}");
        }

        // The corpse's current loot waypoint (its Target is this corpse). A full grid scan: dragging is rare, no cache.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Waypoint FindCorpseWaypoint(WaypointSystem waypointSystem, Corpse corpse)
        {
            Cell[,] cells = waypointSystem.Cells;
            if (cells == null) return null;
            int width = cells.GetLength(0);
            int height = cells.GetLength(1);
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    List<Waypoint> list = cells[x, y].Waypoints;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Waypoint waypoint = list[i];
                        if (waypoint != null && ReferenceEquals(waypoint.Target, corpse))
                            return waypoint;
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
                DeadBodiesController deadBodiesController = botGame?.BotsController?.BotSpawner?._deadBodiesController;
                List<DeadBody> bodies = deadBodiesController?.Bodies;
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
            catch (Exception exception)
            {
                if (Plugin.debugBodyDragLoot)
                    Plugin.MyLog.LogWarning($"[OrbitSupport] vanilla DeadBody refresh failed: {exception.Message}");
            }
        }
    }
}
