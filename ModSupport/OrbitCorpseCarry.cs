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
    // Compiled into BOTH SPT-VR.dll (OrbitSupport: single player / the VR player hosting) and SPT-VR-FikaSync.dll (OrbitSync: the
    // host / headless), linked from FikaSync.csproj. ORBIT-typed through and through: only ever touched from their NoInlining paths
    // behind the ORBIT check, and held as `object` in their static dictionaries (a typed static field would load ORBIT's types).
    //
    // A dragged corpse's ORBIT waypoint is pulled on the grab and a new one made where the body comes to rest (Waypoint.Position is
    // readonly). The new one gets a NEW id on purpose: ORBIT refuses a claim on a removed id (WaypointSystem.TryClaim), so anything
    // still holding the old object - a bot already sent to the death spot - fails safe; the old id reused would make that stale
    // object, death spot and all, claimable again. ORBIT 3.x keeps more about a corpse than the waypoint and its killer tag, by id or
    // by that object, so this carries it from the old waypoint to the new one:
    //   - the killer squad tag (loot right without line of sight)
    //   - its killers' own-kill loot queue (Agent.OwnKillCorpseIds: ORBIT drops an id it no longer tracks - the killer gave up on it)
    //   - the squads that already looted it and the bots that judged it not worth it (CompletedPoiIds / ValueSkippedPoiIds)
    //   - a boss body Boss Rush squads look for (_rushCorpses, keyed by the waypoint object) and the squads' boss-loot memory of it
    internal sealed class OrbitCorpseCarry
    {
        private Waypoint pulled;
        private int killerSquad = -1;
        private string rushRole;
        private readonly List<Agent> ownKillAgents = new List<Agent>();
        private readonly List<Squad> completedSquads = new List<Squad>();
        private readonly List<Agent> valueSkippedAgents = new List<Agent>();

        public int PulledId => pulled != null ? pulled.Id : -1;
        public int KillerSquad => killerSquad;

        // Everything ORBIT knows about the corpse's waypoint, just before it's removed. Each part apart: one that fails (an ORBIT
        // update renamed what it reads) loses only itself.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static OrbitCorpseCarry Capture(WaypointSystem waypoints, Waypoint waypoint, Action<string> warn)
        {
            var carry = new OrbitCorpseCarry { pulled = waypoint };
            Guard(() =>
            {
                if (waypoints._corpseKillerSquadId != null && waypoints._corpseKillerSquadId.TryGetValue(waypoint.Id, out int squadId))
                    carry.killerSquad = squadId;
            }, "killer squad", warn);
            Guard(() => carry.CaptureRush(waypoints), "boss rush body", warn);
            Guard(() => carry.CaptureIds(), "loot queues", warn);
            return carry;
        }

        // The new waypoint takes over what the old one had
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Restore(WaypointSystem waypoints, Waypoint moved, Action<string> warn)
        {
            if (killerSquad >= 0)
                Guard(() => waypoints.TagCorpseKillerSquad(moved.Id, killerSquad), "killer squad", warn);
            if (rushRole != null)
                Guard(() => waypoints.RegisterRushCorpse(moved, rushRole), "boss rush body", warn);
            Guard(() => RestoreIds(moved.Id), "loot queues", warn);
            Guard(() => RestoreBossLoot(moved), "boss loot", warn);
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

        private void CaptureRush(WaypointSystem waypoints)
        {
            if (waypoints._rushCorpses != null && waypoints._rushCorpses.TryGetValue(pulled, out string role))
            {
                rushRole = role;
                // Not found by Boss Rush while it's being carried, like the waypoint itself
                waypoints._rushCorpses.Remove(pulled);
            }
        }

        private void CaptureIds()
        {
            OrbitManager manager = Comfort.Common.Singleton<OrbitManager>.Instance;
            if (manager == null)
                return;
            int id = pulled.Id;
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

        private void RestoreIds(int id)
        {
            for (int i = 0; i < ownKillAgents.Count; i++)
            {
                Agent agent = ownKillAgents[i];
                if (agent?.Bot == null || agent.Bot.IsDead)
                    continue;
                agent.OwnKillCorpseIds.Remove(pulled.Id);
                if (!agent.OwnKillCorpseIds.Contains(id))
                    agent.OwnKillCorpseIds.Add(id);
            }
            for (int i = 0; i < valueSkippedAgents.Count; i++)
                valueSkippedAgents[i]?.ValueSkippedPoiIds.Add(id);
            for (int i = 0; i < completedSquads.Count; i++)
                completedSquads[i]?.CompletedPoiIds.Add(id);
        }

        // Squads that killed a boss remember its body (BossRushLoot): what pointed at the old waypoint points at the new one
        private void RestoreBossLoot(Waypoint moved)
        {
            OrbitManager manager = Comfort.Common.Singleton<OrbitManager>.Instance;
            if (manager == null)
                return;
            List<Squad> squads = manager.SquadData.Entities.Values;
            for (int s = 0; s < squads.Count; s++)
            {
                BossRushLoot loot = squads[s]?.BossLoot;
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

        // A lambda is its own method, compiled when it runs: a member an ORBIT update renamed throws here, inside the try
        private static void Guard(Action part, string what, Action<string> warn)
        {
            try { part(); }
            catch (Exception e) { warn?.Invoke($"{what}: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
