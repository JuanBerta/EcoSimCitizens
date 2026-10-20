using System;
using System.Collections.Generic;
using System.Numerics;
using Eco.Core;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Players;
using Eco.Shared.Math;
using Quaternion = Eco.Shared.Math.Quaternion;

namespace EcoSimCitizens
{
    /// <summary>
    /// Bridges citizens and their live WorldObject NPCs: spawn, lookup and despawn.
    /// Spawning requires a real in-game User as creator (the admin issuing the
    /// command), so no fake player accounts are created.
    /// </summary>
    public static class NpcManager
    {
        public static CitizenNpc Spawn(Citizen citizen, User creator, Vector3 pos)
        {
            try
            {
                CitizenNpc created = null;
                WorldObjectManager.ForceAdd(
                    typeof(CitizenNpc),
                    creator,
                    pos,
                    Quaternion.Identity,
                    false,
                    null,
                    wo =>
                    {
                        created = wo as CitizenNpc;
                        created?.Bind(citizen, pos);
                        // Starter kit + first settlement attempt (camp, workbench,
                        // tool bench). Failures are logged, never fatal to spawn.
                        SurvivalKit.GrantToNpc(created, creator);
                    },
                    null);
                return created;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("NpcManager.Spawn failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Random spawn candidate around a base point: X/Z offset 5..25 m at a
        /// random angle, Y stays at the base height. Each NPC gets its own point so
        /// batch spawns no longer stack on a single coordinate.
        /// </summary>
        public static Vector3 RandomSpawnPoint(Vector3 basePos, Random rng)
        {
            double angle = rng.NextDouble() * Math.PI * 2.0;
            double radius = 5.0 + rng.NextDouble() * 20.0;
            float dx = (float)(Math.Cos(angle) * radius);
            float dz = (float)(Math.Sin(angle) * radius);
            return new Vector3(basePos.X + dx, basePos.Y, basePos.Z + dz);
        }

        /// <summary>
        /// Spawns at independent random candidate points around basePos, retrying a
        /// fresh point when WorldObjectManager.ForceAdd fails, up to attempts times.
        /// </summary>
        public static CitizenNpc SpawnAtRandom(Citizen citizen, User creator, Vector3 basePos, Random rng, int attempts = 5)
        {
            for (int i = 0; i < attempts; i++)
            {
                var npc = Spawn(citizen, creator, RandomSpawnPoint(basePos, rng));
                if (npc != null) return npc;
            }
            return null;
        }

        public static CitizenNpc Find(string citizenName)
        {
            foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
            {
                if (wo is CitizenNpc npc && string.Equals(npc.CitizenName, citizenName, StringComparison.OrdinalIgnoreCase))
                    return npc;
            }
            return null;
        }

        public static IEnumerable<CitizenNpc> GetAll()
        {
            foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
            {
                if (wo is CitizenNpc npc && !npc.IsDestroyed) yield return npc;
            }
        }

        public static void DestroyFor(Citizen citizen)
        {
            if (citizen == null) return;
            var npc = Find(citizen.Name);
            if (npc == null) return;
            try
            {
                npc.Destroy();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("NpcManager.DestroyFor failed: " + ex.Message);
            }
        }
    }
}
