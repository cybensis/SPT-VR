using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Orbit.Core;
using Orbit.Entities;
using Orbit.Navigation;
using Orbit.Systems;
using UnityEngine;

namespace TarkovVR.ModSupport.ORBIT
{
    // Compiled into both SPT-VR.dll (OrbitSupport: single player / the VR player hosting) and SPT-VR-FikaSync.dll (OrbitSync: the
    // host / headless), linked from FikaSync.csproj. Only ever touched from their NoInlining paths behind the ORBIT check, and held
    // as `object` in their static dictionaries (a typed static field would load ORBIT's types).
    //
    // A dragged corpse's ORBIT waypoint is pulled on the grab and a new one made where the body comes to rest (Waypoint.Position is
    // readonly). The new one gets a new id on purpose: ORBIT refuses a claim on a removed id (WaypointSystem.TryClaim), so anything
    // still holding the old object (a bot already sent to the death spot) fails safe; reusing the old id would make that stale
    // object claimable again. ORBIT 3.x keeps more about a corpse than the waypoint and its killer tag, by id or by that object,
    // so this carries it from the old waypoint to the new one:
    //   - the killer squad tag (loot right without line of sight)
    //   - its killers' own-kill loot queue (Agent.OwnKillCorpseIds: ORBIT drops an id it no longer tracks - the killer gave up on it)
    //   - the squads that already looted it and the bots that judged it not worth it (CompletedPoiIds / ValueSkippedPoiIds)
    //   - a boss body Boss Rush squads look for (_rushCorpses, keyed by the waypoint object) and the squads' boss-loot memory of it
    //
    // No field of an ORBIT type, here or in a closure (a lambda capturing an ORBIT-typed local makes one): Assembly.GetTypes() on
    // SPT-VR.dll loads every type's fields, so without ORBIT installed it throws ReflectionTypeLoadException for any mod that scans
    // assemblies. Held as object, cast where used; ORBIT types only in method signatures and bodies, which load when they run.
    internal sealed class OrbitCorpseCarry
    {
        private object pulled;                                          // Waypoint
        private int killerSquad = -1;
        private string rushRole;
        private readonly List<object> ownKillAgents = new List<object>();       // Agent
        private readonly List<object> completedSquads = new List<object>();     // Squad
        private readonly List<object> valueSkippedAgents = new List<object>();  // Agent

        private Waypoint Pulled => (Waypoint)pulled;

        public int PulledId => pulled != null ? Pulled.Id : -1;
        public int KillerSquad => killerSquad;

        // Everything ORBIT knows about the corpse's waypoint, just before it's removed. Each part apart: one that fails (an ORBIT
        // update renamed what it reads) loses only itself - its own NoInlining method, compiled when it's called, inside the try.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static OrbitCorpseCarry Capture(WaypointSystem waypoints, Waypoint waypoint, Action<string> warn)
        {
            var carry = new OrbitCorpseCarry { pulled = waypoint };
            try { carry.CaptureKillerSquad(waypoints); } catch (Exception exception) { Warn(warn, "killer squad", exception); }
            try { carry.CaptureRush(waypoints); } catch (Exception exception) { Warn(warn, "boss rush body", exception); }
            try { carry.CaptureIds(); } catch (Exception exception) { Warn(warn, "loot queues", exception); }
            return carry;
        }

        // The new waypoint takes over what the old one had
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Restore(WaypointSystem waypoints, Waypoint moved, Action<string> warn)
        {
            if (killerSquad >= 0)
                try { RestoreKillerSquad(waypoints, moved); } catch (Exception exception) { Warn(warn, "killer squad", exception); }
            if (rushRole != null)
                try { RestoreRush(waypoints, moved); } catch (Exception exception) { Warn(warn, "boss rush body", exception); }
            try { RestoreIds(moved.Id); } catch (Exception exception) { Warn(warn, "loot queues", exception); }
            try { RestoreBossLoot(moved); } catch (Exception exception) { Warn(warn, "boss loot", exception); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CaptureKillerSquad(WaypointSystem waypoints)
        {
            if (waypoints._corpseKillerSquadId != null && waypoints._corpseKillerSquadId.TryGetValue(Pulled.Id, out int squadId))
                killerSquad = squadId;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RestoreKillerSquad(WaypointSystem waypoints, Waypoint moved)
        {
            waypoints.TagCorpseKillerSquad(moved.Id, killerSquad);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RestoreRush(WaypointSystem waypoints, Waypoint moved)
        {
            waypoints.RegisterRushCorpse(moved, rushRole);
        }

        // Where the body can't be a loot waypoint: ORBIT doesn't register a corpse inside a minefield / border zone (it would lure
        // looters into the mines), so neither do we when it's dragged into one
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool InDangerZone(Vector3 position)
        {
            try { return DangerZones.IsInside(position); }
            catch { return false; }
        }

        public string Summary()
        {
            return $"killerSquad={killerSquad}, own-kill queues={ownKillAgents.Count}, looted by={completedSquads.Count} squads, " +
                $"skipped by={valueSkippedAgents.Count}, boss={rushRole ?? "-"}";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CaptureRush(WaypointSystem waypoints)
        {
            Waypoint waypoint = Pulled;
            if (waypoints._rushCorpses != null && waypoints._rushCorpses.TryGetValue(waypoint, out string role))
            {
                rushRole = role;
                // Not found by Boss Rush while it's being carried, like the waypoint itself
                waypoints._rushCorpses.Remove(waypoint);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CaptureIds()
        {
            OrbitManager manager = Comfort.Common.Singleton<OrbitManager>.Instance;
            if (manager == null)
                return;
            int id = Pulled.Id;
            List<Agent> agents = manager.AgentData.Entities.Values;
            for (int i = 0; i < agents.Count; i++)
            {
                Agent agent = agents[i];
                if (agent == null)
                    continue;
                if (agent.OwnKillCorpseIds.Contains(id))
                    ownKillAgents.Add(agent);
                if (agent.ValueSkippedPoiIds.Contains(id))
                    valueSkippedAgents.Add(agent);
            }
            List<Squad> squads = manager.SquadData.Entities.Values;
            for (int i = 0; i < squads.Count; i++)
                if (squads[i] != null && squads[i].CompletedPoiIds.Contains(id))
                    completedSquads.Add(squads[i]);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RestoreIds(int id)
        {
            int pulledId = Pulled.Id;
            for (int i = 0; i < ownKillAgents.Count; i++)
            {
                Agent agent = (Agent)ownKillAgents[i];
                if (agent?.Bot == null || agent.Bot.IsDead)
                    continue;
                agent.OwnKillCorpseIds.Remove(pulledId);
                if (!agent.OwnKillCorpseIds.Contains(id))
                    agent.OwnKillCorpseIds.Add(id);
            }
            for (int i = 0; i < valueSkippedAgents.Count; i++)
                ((Agent)valueSkippedAgents[i])?.ValueSkippedPoiIds.Add(id);
            for (int i = 0; i < completedSquads.Count; i++)
                ((Squad)completedSquads[i])?.CompletedPoiIds.Add(id);
        }

        // Squads that killed a boss remember its body (BossRushLoot): what pointed at the old waypoint points at the new one
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RestoreBossLoot(Waypoint moved)
        {
            OrbitManager manager = Comfort.Common.Singleton<OrbitManager>.Instance;
            if (manager == null)
                return;
            Waypoint pulled = Pulled;
            List<Squad> squads = manager.SquadData.Entities.Values;
            for (int squadIndex = 0; squadIndex < squads.Count; squadIndex++)
            {
                BossRushLoot loot = squads[squadIndex]?.BossLoot;
                if (loot == null)
                    continue;
                foreach (string role in new List<string>(loot._kills.Keys))
                    if (loot._kills[role] == pulled)
                        loot._kills[role] = moved;
                if (loot._pending.Contains(pulled))
                {
                    Waypoint[] queued = loot._pending.ToArray();
                    loot._pending.Clear();
                    foreach (Waypoint waypoint in queued)
                        loot._pending.Enqueue(waypoint == pulled ? moved : waypoint);
                }
                if (loot._queued.Remove(pulled.Id))
                    loot._queued.Add(moved.Id);
                if (loot._resume == pulled)
                    loot._resume = moved;
                foreach (Agent member in new List<Agent>(loot._orders.Keys))
                    if (loot._orders[member] == pulled)
                        loot._orders[member] = moved;
            }
        }

        private static void Warn(Action<string> warn, string what, Exception exception)
        {
            warn?.Invoke($"{what}: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
