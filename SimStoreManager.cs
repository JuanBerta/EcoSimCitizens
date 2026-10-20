using System;
using System.Collections.Generic;
using System.Linq;
using Eco.Gameplay.Players;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// The NPC autonomous shop economy. Citizens open simulated stores, stock them
    /// with their own production, price adaptively, and other citizens buy from
    /// them - all inside the mod's simulated economy (BankAccount-to-BankAccount
    /// transfers, never StoreComponent.PerformTrade).
    ///
    /// Decision loop per citizen (run from SimulationCore):
    ///   - hungry   -> SimStoreManager.TryBuyFood  (buy cheapest food from any open sim store)
    ///   - has cash -> EnsureShopFor               (open/own a sim store selling own production)
    ///   - every tick -> TickShops                 (restock, reprice, upgrade, open/close)
    /// </summary>
    public static class SimStoreManager
    {
        /// <summary>
        /// Opens a simulated store for a citizen when it can afford it and has
        /// something to sell. Merchants open first, everyone else opens when they
        /// produce goods. Returns true when a shop was created.
        /// </summary>
        public static bool EnsureShopFor(Citizen c, CitizensDatabase db, ModConfig cfg)
        {
            try
            {
                if (!cfg.SimShopsEnabled || c == null || !c.IsAlive) return false;
                if (db.CountStoresOf(c.Name) >= cfg.SimShopMaxPerOwner) return false;
                if (!SimStoreLogic.CanAfford(c.LedgerBalance, cfg.ShopOpenCost)) return false;

                string item = PickSellableItem(c);
                if (item == null) return false;

                var npc = NpcManager.Find(c.Name);
                float x = npc != null ? npc.Position.X : (float)c.WorkX;
                float y = npc != null ? npc.Position.Y : (float)c.WorkY;
                float z = npc != null ? npc.Position.Z : (float)c.WorkZ;

                double baseCost = BaseCostFor(c, cfg);
                double price = SimStoreLogic.InitialPrice(baseCost, cfg.SimShopBaseMargin);
                int stock = Math.Min(Math.Max(1, InventoryCount(c, item)), 5);

                string shopName = c.Name + "'s " + FriendlyName(item);
                var store = new SimStore
                {
                    Id = "sim-" + c.Name + "-" + db.SimStores.Count + "-" + c.AgeInTicks,
                    OwnerName = c.Name,
                    Name = shopName,
                    ItemTypeName = item,
                    Price = price,
                    Stock = stock,
                    IsOpen = true,
                    FoundedDay = 0, // filled by the caller with TimeUtil.Days when available
                    Level = 1,
                    X = x, Y = y, Z = z,
                };
                db.AddStore(store);
                SimulationLogic.InventoryRemove(c.Inventory, item, stock);
                c.LedgerBalance -= cfg.ShopOpenCost;

                CitizenSocial.AddTrail(c, $"opened sim store '{shopName}' selling {item} at {price:0.0}");
                PluginLog.Info($"EcoSimCitizens: {c.Name} opened sim store '{shopName}' selling {item} at {price:0.0} (stock {stock}).");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SimStoreManager.EnsureShopFor failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Per-tick shop operations: restock from the owner's inventory, reprice
        /// with market trend + scarcity, upgrade when the shop is profitable and
        /// the owner can pay, close when the owner dies or the shop runs dry.
        /// </summary>
        public static void TickShops(CitizensDatabase db, ModConfig cfg, MarketSnapshot market)
        {
            try
            {
                if (!cfg.SimShopsEnabled) return;
                foreach (var store in db.SimStoresSnapshot())
                {
                    var owner = db.Find(store.OwnerName);
                    if (owner == null || !owner.IsAlive)
                    {
                        store.IsOpen = false;
                        continue;
                    }

                    // restock from the owner's own production inventory
                    if (store.Stock < 20 && owner.Inventory != null
                        && owner.Inventory.TryGetValue(store.ItemTypeName, out int have) && have > 0)
                    {
                        int add = Math.Min(have, 5);
                        SimulationLogic.InventoryRemove(owner.Inventory, store.ItemTypeName, add);
                        store.Stock += add;
                        CitizenSocial.AddTrail(owner, $"restocked {add} {store.ItemTypeName} at '{store.Name}'");
                    }

                    // adaptive pricing: market trend (damped) + stock scarcity
                    double trend = market != null && market.HasMarket ? market.TrendPercent : 0;
                    store.Price = SimStoreLogic.AdjustedPrice(store.Price, trend, cfg.PriceTrendSensitivity, store.Stock);

                    // close when the shelves are empty and the owner cannot restock
                    if (store.Stock <= 0 && (owner.Inventory == null
                        || !owner.Inventory.TryGetValue(store.ItemTypeName, out int have2) || have2 <= 0))
                    {
                        store.IsOpen = false;
                        continue;
                    }
                    if (!store.IsOpen && store.Stock > 0 && owner.LedgerBalance > cfg.ShopOpenCost * 0.2)
                    {
                        store.IsOpen = true;
                        CitizenSocial.AddTrail(owner, $"reopened sim store '{store.Name}'");
                    }

                    // upgrade: profitable shop + owner can pay the upgrade fee
                    if (store.Level < 3 && store.TotalRevenue >= cfg.ShopUpgradeCost * (store.Level + 1)
                        && SimStoreLogic.CanAfford(owner.LedgerBalance, cfg.ShopUpgradeCost))
                    {
                        owner.LedgerBalance -= cfg.ShopUpgradeCost;
                        store.Level++;
                        PluginLog.Info($"EcoSimCitizens: {owner.Name} upgraded sim store '{store.Name}' to level {store.Level}.");
                        CitizenSocial.AddTrail(owner, $"upgraded sim store '{store.Name}' to level {store.Level}");
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SimStoreManager.TickShops failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Buys the cheapest food available in any open sim store owned by another
        /// citizen. Settlement happens mod-internally: money moves from the buyer's
        /// NPC bank account into the seller's NPC bank account (plus ledger mirror).
        /// Returns true and sets spent on success.
        /// </summary>
        public static bool TryBuyFood(Citizen c, CitizensDatabase db, ModConfig cfg, out float spent)
        {
            spent = 0;
            try
            {
                if (!cfg.SimShopsEnabled || c == null) return false;
                var candidates = db.SimStoresSnapshot()
                    .Where(s => s.IsOpen && s.Stock > 0 && s.Price > 0
                        && !string.Equals(s.OwnerName, c.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => s.Price)
                    .ToList();
                if (candidates.Count == 0) return false;

                foreach (var store in candidates)
                {
                    var owner = db.Find(store.OwnerName);
                    if (owner == null || !owner.IsAlive) continue;

                    double price = store.Price;
                    if (!CitizenEconomy.TransferNpcFunds(c, owner, price)) continue;

                    store.Stock--;
                    store.TotalSales++;
                    store.TotalRevenue += price;
                    store.TradesSettled++;
                    c.TradeBuyVolume++;
                    owner.ItemsSold++;
                    owner.RealEarned += price;
                    CitizenSocial.AddTrail(c, $"bought {store.ItemTypeName} at {store.Name} for {price:0.0}");
                    PluginLog.Info($"EcoSimCitizens: NPC trade: {c.Name} bought {store.ItemTypeName} from {owner.Name}'s '{store.Name}' for {price:0.0} (simulated economy settlement).");
                    spent = (float)price;
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SimStoreManager.TryBuyFood failed: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------- helpers

        private static string PickSellableItem(Citizen c)
        {
            if (c.Inventory == null || c.Inventory.Count == 0) return null;
            // merchants / everyone: prefer food when the citizen has it, else any stock
            var items = c.Inventory
                .Where(kv => kv.Value > 0)
                .OrderByDescending(kv => SimulationLogic.IsFoodItem(kv.Key))
                .ThenByDescending(kv => kv.Value)
                .ToList();
            return items.Count == 0 ? null : items[0].Key;
        }

        private static int InventoryCount(Citizen c, string item)
        {
            if (c.Inventory == null || !c.Inventory.TryGetValue(item, out int v)) return 0;
            return v;
        }

        private static double BaseCostFor(Citizen c, ModConfig cfg)
            => string.IsNullOrEmpty(c.Profession)
                ? cfg.RawMaterialCost
                : cfg.RawMaterialCost * (c.Profession.Contains("Merchant", StringComparison.OrdinalIgnoreCase) ? 1.0 : 1.2);

        private static string FriendlyName(string itemTypeName)
        {
            if (string.IsNullOrEmpty(itemTypeName)) return "Goods";
            string n = itemTypeName.Replace("Item", "").Trim();
            if (n.Length > 0 && char.IsLower(n[0])) n = char.ToUpper(n[0]) + n.Substring(1);
            return n + " Store";
        }
    }
}
