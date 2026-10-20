using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Eco.Core;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Players;
using Eco.Mods.TechTree;
using Eco.Shared.Math;
using Quaternion = Eco.Shared.Math.Quaternion;

namespace EcoSimCitizens
{
    /// <summary>
    /// Bridges citizens and their live WorldObject NPCs: spawn, lookup and despawn.
    /// Spawning requires a real in-game User as creator (the admin issuing the
    /// command), so no fake player accounts are created.
    ///
    /// v0.6 adds globally distributed spawning (rejection sampling with a minimum
    /// inter-NPC distance so citizens spread across the whole world instead of
    /// clustering around the spawn point) and a bulk destroy path used by
    /// /npccmd despawnall (NPC WorldObjects plus their mod-placed camp/workbench
    /// attachments).
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

        // ------------------------------------------------------- v0.6 distributed spawn

        /// <summary>
        /// Uniform random point on a disc around the world centre. The sqrt radius
        /// makes the whole disc evenly covered instead of piling up near the edge;
        /// Y keeps the base height so the object lands on a valid floor.
        /// </summary>
        public static Vector3 RandomSpawnPointDistributed(Vector3 center, double radius, Random rng)
        {
            double angle = rng.NextDouble() * Math.PI * 2.0;
            double r = Math.Sqrt(rng.NextDouble()) * radius;
            return new Vector3(
                center.X + (float)(Math.Cos(angle) * r),
                center.Y,
                center.Z + (float)(Math.Sin(angle) * r));
        }

        /// <summary>
        /// Spawns one NPC at a globally distributed point. Rejection sampling keeps
        /// every new NPC at least minSpacing metres away from every position in
        /// <paramref name="placed"/> (which should contain the already-placed NPCs of
        /// the world plus the ones accepted earlier in the same batch). If the
        /// configured spacing cannot be satisfied after attempts, the constraint is
        /// relaxed (half, then a quarter) and a warning is logged; as a last resort
        /// the candidate is used anyway so a crowded world never blocks spawning.
        /// </summary>
        public static CitizenNpc SpawnAtDistributed(
            Citizen citizen, User creator, Vector3 center, double radius,
            double minSpacing, Random rng, IList<Vector3> placed, int attempts = 60)
        {
            if (placed == null) placed = new List<Vector3>();
            double spacing = Math.Max(1.0, minSpacing);

            for (int i = 0; i < attempts; i++)
            {
                var point = RandomSpawnPointDistributed(center, radius, rng);
                if (TooClose(point, placed, spacing)) continue;
                var npc = Spawn(citizen, creator, point);
                if (npc == null) continue;
                placed.Add(npc.Position);
                return npc;
            }

            // cannot fit at the configured spacing - relax and retry (logged)
            for (int relax = 0; relax < 2; relax++)
            {
                spacing *= 0.5;
                PluginLog.Warn($"NpcManager: '{citizen.Name}' could not fit at min spacing {minSpacing:0.0}m; relaxing to {spacing:0.0}m.");
                for (int i = 0; i < attempts; i++)
                {
                    var point = RandomSpawnPointDistributed(center, radius, rng);
                    if (TooClose(point, placed, spacing)) continue;
                    var npc = Spawn(citizen, creator, point);
                    if (npc == null) continue;
                    placed.Add(npc.Position);
                    return npc;
                }
            }

            // give up gracefully - very crowded world, place at a random point anyway
            PluginLog.Warn($"NpcManager: '{citizen.Name}' still could not fit after relaxed spacing; placing at a random point (crowded world).");
            var final = RandomSpawnPointDistributed(center, radius, rng);
            var finalNpc = Spawn(citizen, creator, final);
            if (finalNpc != null) placed.Add(finalNpc.Position);
            return finalNpc;
        }

        private static bool TooClose(Vector3 point, IList<Vector3> placed, double minSpacing)
        {
            foreach (var p in placed)
            {
                if (Vector3.Distance(point, p) < minSpacing) return true;
            }
            return false;
        }

        // ------------------------------------------------------- v0.6 bulk destroy

        /// <summary>
        /// Destroys every live CitizenNpc and the mod-placed attachments (starter
        /// camp, workbench, tool bench) that sit close to a despawned NPC. Returns
        /// the destroyed NPC and attachment counts for a readable command result.
        /// </summary>
        public static (int Npcs, int Attachments) DestroyAll()
        {
            int npcs = 0, attachments = 0;
            try
            {
                var wom = PluginManager.GetPlugin<WorldObjectManager>();
                var all = wom.All.ToList();
                var npcPositions = new List<Vector3>();

                foreach (var wo in all)
                {
                    if (wo is CitizenNpc npc && !npc.IsDestroyed)
                    {
                        npcPositions.Add(npc.Position);
                        npcs++;
                        try { npc.Destroy(); }
                        catch (Exception ex) { PluginLog.Warn("NpcManager.DestroyAll: NPC destroy failed: " + ex.Message); }
                    }
                }

                // attachments are independent WorldObjects placed by SurvivalKit next
                // to the NPC (camp 8 m, stations 15 m); clean the ones near a removed NPC
                foreach (var wo in wom.All.ToList())
                {
                    if (wo == null || wo.IsDestroyed) continue;
                    bool isAttachment = wo is StarterCampObject || wo is WorkbenchObject || wo is ToolBenchObject;
                    if (!isAttachment) continue;
                    if (!npcPositions.Any(p => Vector3.Distance(p, wo.Position) <= 18f)) continue;
                    attachments++;
                    try { wo.Destroy(); }
                    catch (Exception ex) { PluginLog.Warn("NpcManager.DestroyAll: attachment destroy failed: " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("NpcManager.DestroyAll failed: " + ex.Message);
            }
            return (npcs, attachments);
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

    /// <summary>
    /// v0.6: world size probing for the global spawn radius. The Eco world class is
    /// reached through reflection so the mod compiles against any 0.14 reference
    /// assembly without depending on a fragile private API; when probing fails the
    /// configurable NpcSpawnRadius (or a 350 m fallback) keeps distribution working.
    /// </summary>
    public static class WorldBounds
    {
        /// <summary>
        /// Radius used for global NPC distribution. The config value wins when it is
        /// set to a positive number; otherwise the live world size is probed and a
        /// 350 m fallback keeps the distribution meaningful on unknown/tiny worlds.
        /// </summary>
        public static double EffectiveSpawnRadius(ModConfig cfg)
        {
            if (cfg != null && cfg.NpcSpawnRadius > 0) return cfg.NpcSpawnRadius;
            double detected = DetectWorldRadius();
            return detected > 0 ? detected : 350.0;
        }

        public static double DetectWorldRadius()
        {
            try
            {
                var candidates = new[] { "Eco.Core.World.World, Eco.Core", "Eco.Core.World, Eco.Core" };
                Type t = null;
                foreach (var c in candidates)
                {
                    t = Type.GetType(c);
                    if (t != null) break;
                }
                if (t == null) return 0;

                double width = 0, height = 0, radius = 0;
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    try
                    {
                        object v = p.GetValue(null);
                        if (v == null) continue;
                        string n = p.Name.ToLowerInvariant();
                        if (n.Contains("radius"))
                        {
                            radius = ToDouble(v);
                            if (radius > 0) return radius;
                        }
                        if (!n.Contains("size") && !n.Contains("width") && !n.Contains("height") && !n.Contains("world")) continue;
                        double d = ToDouble(v);
                        if (d <= 0) continue;
                        if (n.Contains("height")) height = d;
                        else width = d;
                    }
                    catch { }
                }

                if (width > 0 && height > 0) return Math.Min(width, height) / 2.0;
                if (width > 0) return width / 2.0;
                return radius > 0 ? radius : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double ToDouble(object v)
        {
            if (v == null) return 0;
            if (v is float f) return f;
            if (v is int i) return i;
            if (v is double d) return d;
            if (v is long l) return l;
            var t = v.GetType();
            var x = t.GetProperty("X") ?? t.GetProperty("Width");
            var y = t.GetProperty("Z") ?? t.GetProperty("Height");
            object xv = x?.GetValue(v), yv = y?.GetValue(v);
            if (xv != null && yv != null)
            {
                double dx = Convert.ToDouble(xv);
                double dy = Convert.ToDouble(yv);
                return Math.Max(dx, dy);
            }
            return 0;
        }
    }
}
