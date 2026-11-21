using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Comfort.Common;
using EFT.Interactive;
using Orbit.Core;
using Orbit.Navigation;
using Orbit.Systems;
using TarkovVR.ModSupport.ORBIT;
using UnityEngine;

namespace SptVrFikaSync
{
    // ORBIT bridge in the FikaSync module. The headless host runs ORBIT and the bots but not SPT-VR.dll (that needs SteamVR),
    // so moving a dragged corpse's loot waypoint is done here, not in SPT-VR.dll's OrbitSupport:
    //   * Grab: remove the corpse's ORBIT loot waypoint. ORBIT loots logically: a bot empties corpse.Item once it reaches
    //     the waypoint, wherever the body physically is.
    //   * Settle: add an identical corpse waypoint where the body comes to rest, which keeps the kill credit.
    // The corpse is matched by its PlayerProfileID (the only stable identity for a headless-local Corpse).
    // ORBIT is a soft dependency. Available is set by FikaSyncPlugin's BepInEx plugin check, and every method that uses ORBIT types
    // is NoInlining and only called behind that check, so a FIKA peer without ORBIT never JIT-compiles one.
    internal static class OrbitSync
    {
        // Set once by FikaSyncPlugin.Awake when Chainloader.PluginInfos contains com.chazut.orbit.
        public static bool Available;

        // Corpses whose ORBIT waypoint was removed for a drag; each is re-added on settle. The value is the ORBIT state saved for it
        // (OrbitCorpseCarry, linked from ModSupport/), stored as object so the field type names no ORBIT type when ORBIT isn't loaded.
        private static readonly Dictionary<Corpse, object> _held = new Dictionary<Corpse, object>();

        // These only use EFT's Corpse type, so they JIT safely without ORBIT. The ORBIT work is in the NoInlining methods below.
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

                OrbitCorpseCarry carry = OrbitCorpseCarry.Capture(ws, wp, Warn);
                ws.RemoveWaypoint(wp.Id);
                _held[corpse] = carry;
                FikaSyncPlugin.Log.LogInfo($"[OrbitSync] pulled corpse waypoint {wp.Id} on grab ({corpse.PlayerProfileID}) — unlootable while dragged; {carry.Summary()}");
            }
            catch (Exception e) { FikaSyncPlugin.Log.LogWarning($"[OrbitSync] pull failed: {e.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Readd(Corpse corpse, Vector3 restPos)
        {
            try
            {
                if (!_held.TryGetValue(corpse, out object held) || !(held is OrbitCorpseCarry carry)) return; // not removed on grab: don't create one
                _held.Remove(corpse);

                WaypointSystem ws = Singleton<OrbitManager>.Instance?.WaypointSystem;
                if (ws == null) return;

                if (OrbitCorpseCarry.InDangerZone(restPos))
                {
                    FikaSyncPlugin.Log.LogInfo($"[OrbitSync] corpse dragged into a minefield / border zone: no loot waypoint ({corpse.PlayerProfileID})");
                    return;
                }

                // Same as ORBIT's own corpse registration (Corpse category, corpse.name, radius 3*3, empty door/cover);
                // only the position and a fresh id differ.
                Waypoint moved = new Waypoint(
                    ws.NewRuntimeWaypointId(), WaypointCategory.Corpse, corpse.name, restPos,
                    3f * 3f, new List<Door>(), new List<CoverPoint>(), corpse);

                if (ws.AddRuntimeWaypoint(moved))
                {
                    carry.Restore(ws, moved, Warn);
                    FikaSyncPlugin.Log.LogInfo($"[OrbitSync] re-added corpse waypoint {carry.PulledId} -> {moved.Id} at settle {restPos} ({corpse.PlayerProfileID}; {carry.Summary()})");
                }
            }
            catch (Exception e) { FikaSyncPlugin.Log.LogWarning($"[OrbitSync] re-add failed: {e.Message}"); }
        }

        private static void Warn(string message)
        {
            FikaSyncPlugin.Log.LogWarning($"[OrbitSync] carrying a corpse's ORBIT state: {message}");
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
