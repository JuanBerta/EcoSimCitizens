using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Eco.Core;
using Eco.Gameplay.Components.Storage;
using Eco.Gameplay.Items;
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
                // Always land on the terrain surface first: NPCs must never inherit
                // an in-air Y from the base position (world centre / tree-top).
                pos = GroundSampler.SnapToGround(pos);

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
                        // v0.7: NPC appearance follows the invoker player (the User
                        // that caused the spawn). Data-layer copy only - the NPC
                        // keeps its vanilla mannequin prefab and never gets a User.
                        if (created != null && ModConfig.Instance.AppearanceSyncEnabled)
                        {
                            try { created.ApplyAppearanceFrom(creator, citizen); } catch { }
                        }
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
        /// random angle, then snapped to the terrain surface (Y no longer inherits
        /// the base height, which could be in the air). Each NPC gets its own point
        /// so batch spawns no longer stack on a single coordinate.
        /// </summary>
        public static Vector3 RandomSpawnPoint(Vector3 basePos, Random rng)
        {
            double angle = rng.NextDouble() * Math.PI * 2.0;
            double radius = 5.0 + rng.NextDouble() * 20.0;
            float dx = (float)(Math.Cos(angle) * radius);
            float dz = (float)(Math.Sin(angle) * radius);
            return GroundSampler.SnapToGround(new Vector3(basePos.X + dx, basePos.Y, basePos.Z + dz));
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
        /// the point is then snapped to the terrain surface so distributed spawns
        /// never land in the air (the world centre Y was only a placeholder).
        /// </summary>
        public static Vector3 RandomSpawnPointDistributed(Vector3 center, double radius, Random rng)
        {
            double angle = rng.NextDouble() * Math.PI * 2.0;
            double r = Math.Sqrt(rng.NextDouble()) * radius;
            return GroundSampler.SnapToGround(new Vector3(
                center.X + (float)(Math.Cos(angle) * r),
                center.Y,
                center.Z + (float)(Math.Sin(angle) * r)));
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

        // ------------------------------------------------------- v0.6 bulk destroy (v0.8 full coverage)

        /// <summary>
        /// v0.8: destroys every live CitizenNpc plus ALL mod-placed NPC facilities
        /// and structures, regardless of distance:
        ///   1. every registered PlacedObject (persisted in citizens.json by
        ///      SurvivalKit.TryPlace) is destroyed by WorldObject ID - this covers
        ///      starter camp / workbench / tool bench / campfire / stockpile and any
        ///      future mod-built structure, at any distance from the NPC;
        ///   2. legacy fallback: known attachment types that were placed before the
        ///      registry existed are still cleaned when they sit near a destroyed
        ///      NPC (old 18 m rule) - player-built objects further away are kept;
        ///   3. v1.2 world-wide sweep: when the registry is empty (worlds that ran
        ///      the broken v1.1 despawn/respawn chain) NPC-built Stockpiles are
        ///      still recognised anywhere via two independent signals - proximity
        ///      to a destroyed NPC's last known position (20 m camp radius) and the
        ///      NPC starter-kit items stored inside the stockpile. Objects that
        ///      cannot be attributed to an NPC (ambiguous) are NEVER deleted and
        ///      are logged instead, so player-built objects are never touched.
        /// The registry is cleared so citizens.json no longer references destroyed
        /// facilities.
        /// </summary>
        public static DespawnStats DestroyAll()
        {
            var stats = new DespawnStats();
            try
            {
                var wom = PluginManager.GetPlugin<WorldObjectManager>();
                var db = EcoSimCitizens.Instance?.Database;
                var npcPositions = new List<Vector3>();

                foreach (var wo in wom.All.ToList())
                {
                    if (wo is CitizenNpc npc && !npc.IsDestroyed)
                    {
                        npcPositions.Add(npc.Position);
                        stats.Npcs++;
                        try { npc.Destroy(); }
                        catch (Exception ex) { PluginLog.Warn("NpcManager.DestroyAll: NPC destroy failed: " + ex.Message); }
                    }
                }

                // 1) registered mod-placed objects (any distance)
                var registry = (db?.PlacedObjectsSnapshot() ?? new List<PlacedObject>()).ToList();
                foreach (var rec in registry)
                {
                    var wo = FindByWoId(rec.WoId);
                    if (wo != null && !wo.IsDestroyed)
                    {
                        try { wo.Destroy(); }
                        catch (Exception ex) { PluginLog.Warn($"NpcManager.DestroyAll: placed object destroy failed ({rec.TypeName} {rec.WoId}): {ex.Message}"); }
                    }
                    if (rec.IsStructure) stats.Structures++;
                    else stats.Facilities++;
                }
                if (registry.Count > 0) db?.ClearPlacedObjects();

                // 2) legacy fallback: known attachment types near a removed NPC
                foreach (var wo in wom.All.ToList())
                {
                    if (wo == null || wo.IsDestroyed) continue;
                    if (!IsKnownAttachment(wo)) continue;
                    if (!npcPositions.Any(p => Vector3.Distance(p, wo.Position) <= 18f)) continue;
                    stats.LegacyAttachments++;
                    try { wo.Destroy(); }
                    catch (Exception ex) { PluginLog.Warn("NpcManager.DestroyAll: legacy attachment destroy failed: " + ex.Message); }
                }

                // 3) v1.2 world-wide sweep for NPC-built facilities when the
                //    registry does not cover them (empty registry after the v1.1
                //    broken despawn/respawn chain). Never delete when the
                //    attribution is ambiguous - log and keep.
                var registryIds = new HashSet<string>(registry.Select(r => r.WoId), StringComparer.OrdinalIgnoreCase);
                foreach (var wo in wom.All.ToList())
                {
                    if (wo == null || wo.IsDestroyed) continue;
                    if (wo is CitizenNpc) continue;
                    if (!IsKnownAttachment(wo)) continue;
                    if (registryIds.Contains(wo.ID.ToString())) continue; // already handled above
                    bool nearNpc = npcPositions.Any(p => Vector3.Distance(p, wo.Position) <= 20f);
                    bool npcKitStockpile = HasNpcKitItems(wo);
                    if (!nearNpc && !npcKitStockpile)
                    {
                        stats.SkippedAmbiguous++;
                        PluginLog.Info($"NpcManager.DestroyAll: kept {wo.GetType().Name} {wo.ID} at {wo.Position} (no NPC signal - probably player-built)");
                        continue;
                    }
                    stats.Facilities++;
                    try { wo.Destroy(); }
                    catch (Exception ex) { PluginLog.Warn($"NpcManager.DestroyAll: world-wide facility destroy failed ({wo.GetType().Name} {wo.ID}): {ex.Message}"); }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("NpcManager.DestroyAll failed: " + ex.Message);
            }
            return stats;
        }

        /// <summary>
        /// v1.2: true when a Stockpile holds any of the NPC starter-kit items
        /// (Claim Paper / Claim Tool / Stone Axe / Wooden Shovel / Stone Hammer /
        /// Stone Pickaxe) that SurvivalKit.CampKit places into the citizen's own
        /// stockpile. This is a strong NPC-ownership signal for the world-wide
        /// despawn sweep - player stockpiles practically never contain the whole
        /// mod kit set.
        /// </summary>
        private static bool HasNpcKitItems(WorldObject wo)
        {
            try
            {
                if (!(wo is StockpileObject)) return false;
                var inv = wo.GetComponent<StorageComponent>()?.Inventory;
                if (inv == null || inv.TypeToCount == null) return false;
                foreach (var t in new[]
                         {
                             typeof(ClaimPaperItem), typeof(ClaimToolItem),
                             typeof(StoneAxeItem), typeof(WoodenShovelItem),
                             typeof(StoneHammerItem), typeof(StonePickaxeItem),
                         })
                {
                    if (inv.TypeToCount.TryGetValue(t, out var c) && c > 0) return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"NpcManager.HasNpcKitItems failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// v0.9 startup self-heal: the world save from before the v0.8 fix contains
        /// Workbench/ToolBench objects that were ForceAdd-placed while their
        /// CreditComponent.BankAccount was still null. Once serialized they throw
        /// NullReferenceException in CreditComponent.Initialize during
        /// WorldObjectManager load, which kills the server at startup.
        /// This runs from plugin Initialize, which executes while the WMO parallel
        /// init loop is still in flight, and destroys every registered
        /// Workbench/ToolBench before (or as soon as) the loop reaches it.
        /// Safety: only objects in the PlacedObject registry (mod-placed NPC
        /// facilities) are candidates - player-built objects are never touched,
        /// and registered Camp/Campfire/Stockpile are kept.
        /// </summary>
        public static DespawnStats SelfHealPlacedObjects()
        {
            var stats = new DespawnStats();
            var db = EcoSimCitizens.Instance?.Database;
            if (db == null)
            {
                PluginLog.Warn("SelfHealPlacedObjects: database not ready, skipped");
                return stats;
            }
            try
            {
                bool IsBadType(PlacedObject r) =>
                    r.TypeName == "WorkbenchObject" || r.TypeName == "ToolBenchObject";

                var bad = db.PlacedObjectsSnapshot().Where(IsBadType).ToList();
                if (bad.Count == 0)
                {
                    EcoSimCitizens.LogDebug("SelfHealPlacedObjects: no bad registered Workbench/ToolBench found");
                    return stats;
                }
                foreach (var rec in bad)
                {
                    var wo = FindByWoId(rec.WoId);
                    if (wo != null && !wo.IsDestroyed)
                    {
                        try { wo.Destroy(); stats.Facilities++; }
                        catch (Exception ex) { PluginLog.Warn($"SelfHealPlacedObjects: destroy failed ({rec.TypeName} {rec.WoId}): {ex.Message}"); }
                    }
                    else
                    {
                        PluginLog.Info($"SelfHealPlacedObjects: registered {rec.TypeName} {rec.WoId} not found in world (already gone)");
                    }
                }
                int removed = db.RemovePlacedObjectsWhere(IsBadType);
                stats.Structures = removed;
                try { db.Save(); } catch (Exception ex) { PluginLog.Warn("SelfHealPlacedObjects: save failed: " + ex.Message); }
                PluginLog.Info($"SelfHealPlacedObjects: destroyed {stats.Facilities}, removed {removed} registry entries (Workbench/ToolBench credit-bad objects)");
                EcoSimCitizens.LogDebug($"SelfHealPlacedObjects: destroyed {stats.Facilities}, removed {removed} registry entries (Workbench/ToolBench credit-bad objects)");
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SelfHealPlacedObjects failed: " + ex.Message);
            }
            return stats;
        }

        /// <summary>
        /// v0.8 /npccmd fixfloating (v1.2 direction-aware): scans NPC-relevant
        /// world objects (registered mod-placed facilities plus the known
        /// attachment types) and snaps floating ones back to the terrain surface.
        /// v1.2 root-cause fix: the old SnapToGround followed the column's highest
        /// block, so an object near a player roof/floor was dragged UP instead of
        /// down - repeated /fixfloating runs therefore lifted objects forever.
        /// SnapToGroundDown only looks below the object (first solid support
        /// strictly under its feet), so floating objects move DOWN; buried objects
        /// surface; a correct placement (Y == support+1.5) is left untouched and
        /// repeated runs converge instead of climbing. The registry position is
        /// updated after a successful move so citizens.json stays accurate.
        /// </summary>
        public static FixFloatStats FixFloating(float toleranceM = 1.0f)
        {
            var stats = new FixFloatStats();
            try
            {
                var wom = PluginManager.GetPlugin<WorldObjectManager>();
                var db = EcoSimCitizens.Instance?.Database;

                // candidates: registered objects (may be custom structures) plus
                // every live known attachment type (legacy coverage)
                var candidates = new List<WorldObject>();
                foreach (var wo in wom.All)
                {
                    if (wo == null || wo.IsDestroyed) continue;
                    if (wo is CitizenNpc) continue; // NPCs are handled by their own logic
                    if (IsKnownAttachment(wo) && !candidates.Contains(wo)) candidates.Add(wo);
                }
                foreach (var rec in (db?.PlacedObjectsSnapshot() ?? new List<PlacedObject>()).ToList())
                {
                    var wo = FindByWoId(rec.WoId);
                    if (wo != null && !wo.IsDestroyed && !candidates.Contains(wo)) candidates.Add(wo);
                }

                foreach (var wo in candidates)
                {
                    stats.Scanned++;
                    Vector3 ground;
                    try { ground = GroundSampler.SnapToGroundDown(wo.Position); }
                    catch (Exception ex) { PluginLog.Warn($"NpcManager.FixFloating: ground sample failed for {wo}: {ex.Message}"); continue; }
                    if (Math.Abs(wo.Position.Y - ground.Y) <= toleranceM) continue;
                    stats.Floating++;
                    if (ground.Y < wo.Position.Y - toleranceM) stats.Lowered++;
                    else stats.RaisedFromBuried++;
                    try
                    {
                        wo.MoveWorldObjectTo(ground, Quaternion.Identity);
                        stats.Moved++;
                        db?.UpdatePlacedObjectPosition(wo.ID.ToString(), ground.X, ground.Y, ground.Z);
                        PluginLog.Info($"NpcManager.FixFloating: moved {wo.GetType().Name} {wo.ID} from Y={wo.Position.Y:0.00} to Y={ground.Y:0.00} ({((ground.Y < wo.Position.Y - toleranceM) ? "lowered" : "surfaced")})");
                    }
                    catch (Exception ex)
                    {
                        stats.Failed++;
                        PluginLog.Warn($"NpcManager.FixFloating: move failed for {wo.GetType().Name} {wo.ID}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("NpcManager.FixFloating failed: " + ex.Message);
            }
            return stats;
        }

        private static bool IsKnownAttachment(WorldObject wo)
        {
            return wo is StarterCampObject || wo is WorkbenchObject || wo is ToolBenchObject
                || wo is CampfireObject || wo is StockpileObject;
        }

        private static WorldObject FindByWoId(string woId)
        {
            if (string.IsNullOrEmpty(woId)) return null;
            foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
            {
                if (wo == null || wo.IsDestroyed) continue;
                if (string.Equals(wo.ID.ToString(), woId, StringComparison.OrdinalIgnoreCase)) return wo;
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

    /// <summary>Result of a full despawn-all sweep (v0.8, v1.2 world-wide).</summary>
    public class DespawnStats
    {
        public int Npcs;              // CitizenNpc world objects destroyed
        public int Facilities;        // registered mod-placed facilities destroyed
        public int Structures;        // registered mod-placed structures (e.g. adobe house) destroyed
        public int LegacyAttachments; // unregistered known attachments cleaned near removed NPCs
        public int SkippedAmbiguous;  // kept objects with no reliable NPC signal (player-built safety)

        public int TotalPlaced => Facilities + Structures;
        public override string ToString()
            => $"NPCs={Npcs} facilities={Facilities} structures={Structures} legacyAttachments={LegacyAttachments} skippedAmbiguous={SkippedAmbiguous}";
    }

    /// <summary>Result of a floating-object correction sweep (v0.8, v1.2 direction-aware).</summary>
    public class FixFloatStats
    {
        public int Scanned;   // candidate objects examined
        public int Floating;  // objects judged to be floating above the terrain
        public int Lowered;   // objects moved DOWN to the first support below (the v1.2 fix)
        public int RaisedFromBuried; // objects surfaced from being embedded in a solid block
        public int Moved;     // objects successfully snapped back to the ground
        public int Failed;    // move failures

        public override string ToString()
            => $"scanned={Scanned} floating={Floating} lowered={Lowered} surfaced={RaisedFromBuried} moved={Moved} failed={Failed}";
    }
}
