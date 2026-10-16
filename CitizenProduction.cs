using System;
using System.Collections.Generic;
using System.Linq;
using Eco.Core;
using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Store;
using Eco.Gameplay.Economy;
using Eco.Gameplay.Items;
using Eco.Gameplay.Objects;

namespace EcoSimCitizens
{
    /// <summary>
    /// Production, real inventory and market listing for citizens.
    ///
    ///  - Production: during the working day a citizen "crafts" real Eco item types
    ///    that fit its profession. Production is tracked in a personal inventory
    ///    ledger keyed by the real item type name.
    ///  - Listing: the produced goods are offered to the real market. When a real
    ///    store runs a buying offer that matches the item, real currency flows from
    ///    the store's bank account into the citizen's account (the mirror of the
    ///    v0.2 food purchase path). If no store buys, the goods stay in inventory
    ///    and can later be consumed (food) or held (raw materials).
    ///
    /// The mod never injects items into world inventories or store stock; all item
    /// flow happens inside the citizen ledger, but the money movement is real.
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
        /// Produces goods for one working tick and stores them in the citizen's real
        /// inventory ledger. Returns the item type name produced, or null.
        /// </summary>
        public static string Produce(Citizen c, double productionUnits, double productivity)
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
                return typeName;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenProduction.Produce failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Tries to sell the citizen's real inventory to real store buying offers.
        /// Returns the amount of real currency earned this tick.
        /// </summary>
        public static double SellProduced(Citizen c)
        {
            if (c.Inventory == null || c.Inventory.Count == 0) return 0;
            double earned = 0;
            try
            {
                foreach (var kv in new List<KeyValuePair<string, int>>(c.Inventory))
                {
                    if (kv.Value <= 0) continue;
                    double sold = SellOneType(c, kv.Key, kv.Value);
                    if (sold > 0)
                    {
                        earned += sold;
                        CitizenSocial.AddTrail(c, $"sold {kv.Key} to a store");
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenProduction.SellProduced failed: " + ex.Message);
            }
            return earned;
        }

        private static double SellOneType(Citizen c, string typeName, int qty)
        {
            try
            {
                Item item = Item.Get(typeName);
                if (item == null) return 0;

                double bestPrice = 0;
                StoreComponent bestStore = null;
                foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
                {
                    if (wo == null || wo.IsDestroyed) continue;
                    StoreComponent store = null;
                    try { store = wo.GetComponent("StoreComponent") as StoreComponent; }
                    catch { }
                    if (store == null || store.IsBarter || store.BankAccount == null) continue;

                    foreach (var offer in store.AllOffers)
                    {
                        if (offer == null || !offer.Buying || !offer.HasPrice || offer.Price <= 0) continue;
                        if (offer.Price <= bestPrice) continue;
                        try
                        {
                            if (offer.MatchesItem(item))
                            {
                                bestPrice = offer.Price;
                                bestStore = store;
                            }
                        }
                        catch { }
                    }
                }

                if (bestStore == null || bestPrice <= 0) return 0;

                int qtyToSell = Math.Min(qty, 8); // a citizen only sells a handful per tick
                int removed = SimulationLogic.InventoryRemove(c.Inventory, typeName, qtyToSell);
                if (removed <= 0) return 0;

                double total = bestPrice * removed;
                var acc = CitizenEconomy.AccountFor(c.Name);
                var cur = bestStore.Currency;
                if (cur == null)
                {
                    SimulationLogic.InventoryAdd(c.Inventory, typeName, removed);
                    return 0;
                }
                bestStore.BankAccount.AddCurrency(cur, (float)-total, false);
                acc.AddCurrency(cur, (float)total, false);
                c.ItemsSold += removed;
                c.RealEarned += total;
                c.LedgerBalance += total;
                return total;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SellOneType failed: " + ex.Message);
                return 0;
            }
        }
    }
}
