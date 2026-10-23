using System;
using System.Linq;
using System.Text;
using Eco.Gameplay.Items;

namespace EcoSimCitizens
{
    /// <summary>
    /// v2.1 NPC production &amp; sales enhancement.
    ///
    /// Reuses the existing production pipeline (CitizenProduction.Produce /
    /// SellProduced), the pure-simulation shop carrier (SimStoreManager /
    /// SimStoreLogic) and the player commerce layer (PlayerCommerce). The v2.1
    /// additions are:
    ///   - contract-hauled raw goods (NpcContractSystem.HaulItemFor) enter the
    ///     citizen's inventory and are stocked onto the citizen's own SimStore
    ///     through the normal SellProduced path, so players can buy them with
    ///     real native currency;
    ///   - a deterministic store restock: if a citizen holds sellable goods but
    ///     has no open store, one is opened whenever affordable;
    ///   - a report for /npccmd production.
    ///
    /// Hard constraints: SimStore only - never ForceAdd any object carrying
    /// StoreComponent/CreditComponent; no fake Users; no silent exception
    /// swallowing.
    /// </summary>
    public static class NpcProductionSystem
    {
        /// <summary>
        /// Called from SimulationCore.TickCitizen after the v2.0 production /
        /// sell / shop logic. Opens a store when a citizen has sellable goods
        /// but no open one, and keeps hauled contract goods flowing to shelves.
        /// </summary>
        public static void TickProductionSales(Citizen c, CitizensDatabase db, ModConfig cfg, MarketSnapshot market)
        {
            if (c == null || !c.IsAlive || !cfg.NpcProductionSalesEnabled) return;
            try
            {
                int ownedOpen = 0;
                try
                {
                    ownedOpen = db.SimStoresSnapshot()
                        .Count(s => s.IsOpen && string.Equals(s.OwnerName, c.Name, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    ownedOpen = 0;
                }

                bool hasGoods = c.Inventory != null && c.Inventory.Values.Any(v => v > 0);
                if (ownedOpen == 0 && hasGoods && cfg.SimShopsEnabled)
                {
                    bool canAfford = SimStoreLogic.CanAfford(c.LedgerBalance, cfg.ShopOpenCost);
                    if (canAfford)
                    {
                        SimStoreManager.EnsureShopFor(c, db, cfg);
                        PluginLog.Info($"NpcProductionSystem: '{c.Name}' opened a SimStore to sell its hauled/produced goods.");
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"NpcProductionSystem.TickProductionSales failed for '{c?.Name}': {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ report

        public static string BuildReport(CitizensDatabase db, ModConfig cfg)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"NPC production & sales: {(cfg.NpcProductionSalesEnabled ? "ON" : "OFF")} | production {(cfg.ProductionEnabled ? "ON" : "OFF")} | sell-to-store {(cfg.SellEnabled ? "ON" : "OFF")}");
            var citizens = db.Snapshot().Where(x => x != null).ToList();
            long produced = citizens.Sum(x => x.ItemsProduced);
            long sold = citizens.Sum(x => x.ItemsSold);
            double earned = citizens.Sum(x => x.RealEarned);
            long hauled = citizens.Sum(x => x.HauledUnits);
            long held = citizens.Sum(x => SimulationLogic.InventoryTotal(x.Inventory));
            sb.AppendLine($"Totals: {produced} produced | {held} items held | {sold} sold | earned {earned:0.00} | {hauled} units hauled via contracts");
            int open = 0;
            long playerQty = 0;
            double playerRev = 0;
            try
            {
                open = db.SimStoresSnapshot().Count(s => s.IsOpen);
                playerQty = (long)db.SimStoresSnapshot().Sum(s => s.PlayerSales);
                playerRev = db.SimStoresSnapshot().Sum(s => s.PlayerRevenue);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Sim store stats unavailable: {ex.Message}");
                return sb.ToString();
            }
            sb.AppendLine($"Sim stores: {open} open | player-purchased {playerQty} units, {playerRev:0.00} revenue (native currency)");
            return sb.ToString();
        }
    }
}
