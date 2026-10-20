using System;
using System.Collections.Generic;
using System.Linq;
using Eco.Core;
using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Storage;
using Eco.Gameplay.Components.Store;
using Eco.Gameplay.Economy;
using Eco.Gameplay.Items;
using Eco.Gameplay.Objects;

namespace EcoSimCitizens
{
    /// <summary>
    /// Production, real inventory and simulated-store listing for citizens.
    ///
    ///  - Production: during the working day a citizen "crafts" real Eco item types
    ///    that fit its profession. Production is tracked in a personal inventory
    ///    ledger keyed by the real item type name.
    ///  - Listing: produced goods feed the citizen's own simulated store (mod
    ///    internal). Nothing is ever sold to a real StoreComponent - real stores
    ///    are player-only. When the citizen owns no open sim store the goods stay
    ///    in inventory and can later be consumed (food) or held (raw materials).
    /// </summary>
    public static class CitizenProduction
    {
        private static List<Item> _candidates;
        private static readonly object CacheSync = new object();

        /// <summary>Real Eco item types that a profession can produce (cached once).</summary>
        public static List<Item> CandidateItemsFor(string profession)
        {
            lock (CacheSync)
            {
                if (_candidates == null)
                {
                    _candidates = new List<Item>();
                    try
                    {
                        var all = Item.AllItemsExceptHidden;
                        if (all != null) _candidates.AddRange(all);
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warn("Item.AllItemsExceptHidden failed: " + ex.Message);
                    }
                }
            }

            var result = new List<Item>();
            foreach (var item in _candidates)
            {
                if (item == null || item.Type == null) continue;
                string n = item.Type.Name;
                if (n == null) continue;
                if (MatchesProfession(n, profession)) result.Add(item);
            }
            return result;
        }

        private static bool MatchesProfession(string itemName, string profession)
        {
            if (profession == null) return false;
            string p = profession.ToLowerInvariant();
            string n = itemName.ToLowerInvariant();

            if (p.Contains("farm") || p.Contains("cook") || p.Contains("chef") || p.Contains("bak") || p.Contains("gather"))
                return SimulationLogic.IsFoodItem(n) || n.Contains("seed") || n.Contains("plant") || n.Contains("crop");
            if (p.Contains("mining") || p.Contains("smelt"))
                return n.Contains("ore") || n.Contains("rock") || n.Contains("coal") || n.Contains("stone") || n.Contains("iron") || n.Contains("copper") || n.Contains("gold") || n.Contains("sand");
            if (p.Contains("carpent") || p.Contains("logg") || p.Contains("mason") || p.Contains("builder"))
                return n.Contains("wood") || n.Contains("lumber") || n.Contains("log") || n.Contains("brick") || n.Contains("mortar") || n.Contains("board") || n.Contains("frame");
            if (p.Contains("blacksmith") || p.Contains("smith") || p.Contains("mechanic") || p.Contains("electron"))
                return n.Contains("ingot") || n.Contains("bar") || n.Contains("gear") || n.Contains("rod") || n.Contains("wire") || n.Contains("component") || n.Contains("circuit") || n.Contains("steel") || n.Contains("iron");
            if (p.Contains("tailor"))
                return n.Contains("cloth") || n.Contains("fiber") || n.Contains("thread") || n.Contains("shirt") || n.Contains("pants") || n.Contains("shoe") || n.Contains("leather");
            if (p.Contains("scientist") || p.Contains("science") || p.Contains("research"))
                return n.Contains("paper") || n.Contains("book") || n.Contains("science") || n.Contains("lab") || n.Contains("glass");
            if (p.Contains("merchant"))
                return n.Contains("coin") || n.Contains("currency") || n.Contains("crate") || n.Contains("box");
            return n.Contains("lumber") || n.Contains("wood") || n.Contains("stone") || n.Contains("iron");
        }

        /// <summary>
        /// Produces goods for one working tick: the item is added BOTH to the
        /// citizen's ledger and - when the NPC world object is available - to its
        /// real PublicStorageComponent backpack, so players can open the NPC and see
        /// actual crafted goods appear. Returns the item type name produced, or null.
        /// </summary>
        public static string Produce(Citizen c, double productionUnits, double productivity, CitizenNpc npc = null)
        {
            try
            {
                var items = CandidateItemsFor(c.Profession);
                if (items.Count == 0) return null;
                var item = items[Math.Abs(c.AgeInTicks) % items.Count];
                if (item == null || item.Type == null) return null;

                string typeName = item.Type.Name;
                int qty = Math.Max(1, (int)(productionUnits * productivity));
                int total = SimulationLogic.InventoryAdd(c.Inventory, typeName, qty);
                c.ItemsProduced += qty;
                CitizenSocial.AddTrail(c, $"produced {qty} {typeName} (now {total})");

                // Mirror into the real backpack so the work is visible in the world.
                if (npc != null)
                {
                    try
                    {
                        var inv = npc.GetComponent<PublicStorageComponent>()?.Inventory;
                        if (inv != null) inv.AddItems(item, qty, null);
                    }
                    catch (Exception invEx)
                    {
                        PluginLog.Warn($"CitizenProduction: real backpack add failed for {c.Name}: {invEx.Message}");
                    }
                }
                return typeName;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenProduction.Produce failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Feeds the citizen's own simulated store (mod-internal) from its produced
        /// inventory. Returns the number of units restocked this tick. No real
        /// StoreComponent is touched and no real money flows here - sales revenue is
        /// settled later in SimStoreManager.TryBuyFood between NPC accounts. Real
        /// backpack items are intentionally NOT mirrored out when stocking the sim
        /// store (v1.0): the ledger tracks sim-store stock, while the physical
        /// backpack holds the actual goods players can see - removing them here made
        /// visible NPC inventory mysteriously empty.
        /// </summary>
        public static double SellProduced(Citizen c, CitizensDatabase db, CitizenNpc npc = null)
        {
            if (c == null || c.Inventory == null || c.Inventory.Count == 0) return 0;
            double stocked = 0;
            try
            {
                foreach (var kv in new List<KeyValuePair<string, int>>(c.Inventory))
                {
                    if (kv.Value <= 0) continue;
                    var store = db.FindOpenStoreOf(c.Name, kv.Key);
                    if (store == null) continue;
                    int qtyToStock = Math.Min(kv.Value, 8); // a handful per tick
                    int removed = SimulationLogic.InventoryRemove(c.Inventory, kv.Key, qtyToStock);
                    if (removed <= 0) continue;
                    store.Stock += removed;
                    stocked += removed;
                    c.TradeSellVolume += removed;
                    CitizenSocial.AddTrail(c, $"stocked {removed} {kv.Key} at '{store.Name}'");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenProduction.SellProduced failed: " + ex.Message);
            }
            return stocked;
        }
    }
}
