using System;
using System.Collections.Generic;
using System.Linq;

namespace EcoSimCitizens
{
    /// <summary>
    /// A simulated store owned by an NPC. Lives entirely inside the mod's
    /// simulated economy: no StoreComponent, no real store world object and no
    /// StoreComponent.PerformTrade. Inventory, pricing, opening state and sales
    /// history are persisted in citizens.json so shops survive server restarts.
    ///
    /// Real stores (StoreComponent) are only ever scanned by MarketScanner as
    /// player-facing market information; NPCs never trade with them.
    /// </summary>
    public class SimStore
    {
        public string Id { get; set; }                 // stable id: "sim-" + owner + "-" + foundedDay
        public string OwnerName { get; set; }
        public string Name { get; set; }
        public string ItemTypeName { get; set; }       // the main good this shop sells
        public double Price { get; set; }              // current asking price (sim currency)
        public int Stock { get; set; }                 // simulated shelf stock
        public bool IsOpen { get; set; }               // operating state
        public double TotalSales { get; set; }         // lifetime unit sales
        public double TotalRevenue { get; set; }       // lifetime revenue
        public double TradesSettled { get; set; }      // lifetime settled NPC trades
        public double PlayerSales { get; set; }        // v0.7: player-purchased units (real native currency)
        public double PlayerRevenue { get; set; }      // v0.7: player-paid revenue (real native currency)
        public double FoundedDay { get; set; }         // game day it opened
        public int Level { get; set; }                 // shop upgrade level (1..3)
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        /// <summary>Whether this shop currently sells the given item type.</summary>
        public bool Sells(string itemTypeName)
            => string.Equals(ItemTypeName, itemTypeName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pure simulation rules for the simulated store economy. No Eco assembly
    /// dependency, so the tests project can verify the math headlessly.
    /// </summary>
    public static class SimStoreLogic
    {
        /// <summary>Opening price: base material cost plus the shop margin.</summary>
        public static double InitialPrice(double baseCost, double margin)
            => Math.Max(1.0, baseCost * (1.0 + Math.Max(0.0, margin)));

        /// <summary>
        /// Adaptive pricing: follow the market trend but damp it, and raise the
        /// price when stock runs thin (scarcity). Clamped to a sane band.
        /// </summary>
        public static double AdjustedPrice(double current, double trendPercent, double sensitivity, int stock, int maxStock = 40)
        {
            double p = current;
            if (trendPercent != 0)
            {
                p = p * (1.0 + SimMath.Clamp(trendPercent / 100.0 * sensitivity, -0.5, 0.5));
            }
            double scarcity = maxStock <= 0 ? 0.5 : (1.0 - (double)stock / maxStock);
            p = p * (1.0 + SimMath.Clamp(scarcity * 0.25, -0.1, 0.5));
            return SimMath.Clamp(p, 0.5, 1000.0);
        }

        /// <summary>A citizen can afford to open/upgrade a shop when balance covers the cost.</summary>
        public static bool CanAfford(double balance, double cost)
            => balance >= cost;

        /// <summary>
        /// Pure reconcile plan for the three-way sync (persisted citizens, world
        /// NPCs, NpcId anchors). Returns which citizens to create for orphan NPCs,
        /// which NpcId anchors to back-fill, and which stale NpcId references to
        /// clear. Unit-testable without a live world.
        /// </summary>
        public static ReconcilePlan PlanReconcile(List<Citizen> citizens, IReadOnlyList<(string Name, string NpcId)> worldNpcs)
        {
            var plan = new ReconcilePlan();

            // map of existing citizens by name and by npc id
            var byName = new Dictionary<string, Citizen>(StringComparer.OrdinalIgnoreCase);
            var byNpcId = new Dictionary<string, Citizen>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in citizens)
            {
                byName[c.Name] = c;
                if (!string.IsNullOrEmpty(c.NpcId)) byNpcId[c.NpcId] = c;
            }

            var seenNpcIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, npcId) in worldNpcs)
            {
                if (string.IsNullOrEmpty(npcId)) continue;
                seenNpcIds.Add(npcId);
                if (byNpcId.TryGetValue(npcId, out var anchored))
                {
                    // already anchored to this NPC; make sure the name matches
                    if (!string.Equals(anchored.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        plan.RenameAnchors.Add(new ReconcilePlan.Rename(npcId, anchored.Name, name));
                    }
                    continue;
                }
                if (byName.TryGetValue(name, out var byNpc))
                {
                    // world NPC exists and citizen exists: anchor by NpcId
                    if (!string.Equals(byNpc.NpcId, npcId, StringComparison.OrdinalIgnoreCase))
                    {
                        plan.BackfillNpcIds.Add(new ReconcilePlan.Bind(name, npcId));
                    }
                    continue;
                }
                // orphan world NPC with no persisted citizen: bring it into the DB
                plan.CreateCitizens.Add(name);
            }

            // stale anchors: NpcId points at an NPC that no longer exists in the world
            foreach (var c in citizens)
            {
                if (!string.IsNullOrEmpty(c.NpcId) && !seenNpcIds.Contains(c.NpcId))
                {
                    plan.ClearNpcIds.Add(c.Name);
                }
            }

            return plan;
        }
    }

    /// <summary>Result of a three-way reconcile plan (see SimStoreLogic.PlanReconcile).</summary>
    public class ReconcilePlan
    {
        public List<string> CreateCitizens { get; } = new List<string>();
        public List<Bind> BackfillNpcIds { get; } = new List<Bind>();
        public List<string> ClearNpcIds { get; } = new List<string>();
        public List<Rename> RenameAnchors { get; } = new List<Rename>();

        public bool IsEmpty => CreateCitizens.Count == 0 && BackfillNpcIds.Count == 0
            && ClearNpcIds.Count == 0 && RenameAnchors.Count == 0;

        public sealed class Bind
        {
            public string CitizenName;
            public string NpcId;
            public Bind(string name, string npcId) { CitizenName = name; NpcId = npcId; }
        }

        public sealed class Rename
        {
            public string NpcId;
            public string OldName;
            public string NewName;
            public Rename(string npcId, string oldName, string newName)
            {
                NpcId = npcId; OldName = oldName; NewName = newName;
            }
        }

        public override string ToString()
            => $"create={CreateCitizens.Count} backfill={BackfillNpcIds.Count} clear={ClearNpcIds.Count} rename={RenameAnchors.Count}";
    }
}
