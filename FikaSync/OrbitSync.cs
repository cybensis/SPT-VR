using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT.Interactive;
using Orbit.Core;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;

namespace SptVrFikaSync
{
    // Companion-side ORBIT bridge. The headless/host runs ORBIT + the bots, but NOT the VR mod
    // (SPT-VR.dll needs SteamVR), so the dragged-corpse loot-relocation must live HERE, not in
    // SPT-VR.dll's OrbitSupport. Same pull-on-grab / re-add-on-settle model:
    //   * Grab   -> remove the corpse's ORBIT loot waypoint so bots can't loot a body being carried away
    //               (ORBIT loots LOGICALLY: a bot drains corpse.Item once it reaches the waypoint, wherever
    //               the body physically is — a stale waypoint = a bot empties a body from the death spot).
    //   * Settle -> re-add an identical corpse waypoint at the resting place, restoring the kill credit.
    //
    // The corpse is matched by its PlayerProfileID (the only stable identity for a headless-local Corpse).
    // ORBIT is a SOFT dependency: Available is set by FikaSyncPlugin's BepInEx-registry check, and every
    // ORBIT-typed method is [MethodImpl(NoInlining)] called ONLY behind that gate, so a FIKA peer WITHOUT
    // ORBIT never JITs an ORBIT-binding method (the fika-softdep-no-typed-fields rule).
    internal static class OrbitSync
    {
        // Set once by FikaSyncPlugin.Awake (Chainloader.PluginInfos contains com.chazut.orbit).
        public static bool Available;

        // Corpses whose ORBIT waypoint we've pulled for the duration of a drag. Key present = "re-add on
        // settle"; value = the kill-credit squad to restore (or -1). Dictionary<Corpse,int> names no ORBIT
        // type, so it's safe as a static field even when ORBIT isn't loaded.
        private static readonly Dictionary<Corpse, int> _held = new Dictionary<Corpse, int>();

        // Corpse-typed signatures only (EFT) → these JIT safely without ORBIT; the ORBIT work is isolated.
        public static void OnGrab(Corpse corpse)
        {
            if (!Available || corpse == null) return;
            Pull(corpse);
        }

        public static void OnSettle(Corpse corpse, Vector3 restPos)
        {
            if (!Available || corpse == null) return;
            Readd(corpse, restPos);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Pull(Corpse corpse)
        {
            try
            {
                WaypointSystem ws = Singleton<OrbitManager>.Instance?.WaypointSystem;
                if (ws == null) return;
                if (_held.ContainsKey(corpse)) return;

                Waypoint wp = FindCorpseWaypoint(ws, corpse);
                if (wp == null) return; // ORBIT wasn't tracking it (already looted / not registered)

                int killer = -1;
                if (ws._corpseKillerSquadId != null && ws._corpseKillerSquadId.TryGetValue(wp.Id, out int sq))
                    killer = sq;

                ws.RemoveWaypoint(wp.Id);
                _held[corpse] = killer;
                FikaSyncPlugin.Log.LogInfo($"[OrbitSync] pulled corpse waypoint {wp.Id} on grab ({corpse.PlayerProfileID}) — unlootable while dragged");
            }
            catch (Exception e) { FikaSyncPlugin.Log.LogWarning($"[OrbitSync] pull failed: {e.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Readd(Corpse corpse, Vector3 restPos)
        {
            try
            {
                if (!_held.TryGetValue(corpse, out int killer)) return; // wasn't pulled → don't fabricate one
                _held.Remove(corpse);

                WaypointSystem ws = Singleton<OrbitManager>.Instance?.WaypointSystem;
                if (ws == null) return;

                // Identical to ORBIT's own corpse registration (Corpse category, corpse.name, radius 3*3,
                // empty door/cover) — only the position + fresh id differ.
                Waypoint moved = new Waypoint(
                    ws.NewRuntimeWaypointId(), WaypointCategory.Corpse, corpse.name, restPos,
                    3f * 3f, new List<Door>(), new List<CoverPoint>(), corpse);

                if (ws.AddRuntimeWaypoint(moved))
                {
                    if (killer >= 0)
                        ws.TagCorpseKillerSquad(moved.Id, killer);
                    FikaSyncPlugin.Log.LogInfo($"[OrbitSync] re-added corpse waypoint {moved.Id} at settle {restPos} ({corpse.PlayerProfileID})");
                }
            }
            catch (Exception e) { FikaSyncPlugin.Log.LogWarning($"[OrbitSync] re-add failed: {e.Message}"); }
        }

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
    }
}
