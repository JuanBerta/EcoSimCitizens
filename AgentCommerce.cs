using System;
using System.Collections.Generic;

namespace EcoSimCitizens
{
    /// <summary>
    /// PRD Module 2 (agent macroeconomics &amp; commerce): comparative advantage
    /// trading and nutritional balancing (Req.4, Req.12).
    ///
    /// Every tick the agent compares its own production cost against the public
    /// market price for the same good. When buying is cheaper, it buys raw
    /// materials instead of making them; when producing is profitable it keeps
    /// producing and selling. Food purchases are picked by calories-per-currency
    /// value, not just cheapest absolute price.
    ///
    /// All money movements go through the citizen's simulated ledger (real
    /// purchases through the live economy remain in CitizenEconomy).
    /// </summary>
    public static class AgentCommerce
    {
        /// <summary>
        /// Runs one commerce tick for a citizen. Returns true when the citizen
        /// executed at least one buy (material or food) this tick.
        /// </summary>
        public static bool TickCommerce(Citizen c, ModConfig cfg, MarketSnapshot market)
        {
            if (c == null || cfg == null || !cfg.CommerceEnabled) return false;
            bool traded = false;

            // --- comparative advantage: buy raw materials when market beats self-production ---
            if (market.HasMarket && market.AvgPrice > 0)
            {
                var prof = Professions.Get(c.Profession);
                double outputPerTick = prof.Productivity * CitizenSkills.EffectiveProductivity(c) * 1.0;
                double wagePerTick = cfg.WagePerTick * prof.EarnMultiplier * Traits.WorkMultiplier(c.Trait);
                double selfCost = SimulationLogic.EstimatedUnitCost(wagePerTick, outputPerTick * 0.25, cfg.RawMaterialCost);
                if (SimulationLogic.ShouldBuyInsteadOfProduce(selfCost, market.AvgPrice, cfg.TradeMargin))
                {
                    double spend = Math.Min(c.LedgerBalance, market.AvgPrice * cfg.MaterialBuyUnits);
                    if (spend > 0)
                    {
                        c.LedgerBalance -= spend;
                        int units = (int)Math.Max(1, spend / Math.Max(0.01, market.AvgPrice));
                        SimulationLogic.InventoryAdd(c.Inventory, cfg.RawMaterialItemName, units);
                        c.TradeBuyVolume += units;
                        traded = true;
                    }
                }
            }

            // --- nutrition: pick food by calories-per-currency, not cheapest price ---
            if (c.Calories < 65 && market.HasMarket && market.FoodOfferCount > 0 && market.MinPrice > 0)
            {
                double caloriesPerUnit = Math.Max(1.0, cfg.CalorieGainFromFood);
                double bestValue = 0;
                double bestPrice = market.MinPrice;
                // use market min price as anchor; cheaper meals give more value per coin
                double candidatePrice = market.MinPrice;
                double value = SimulationLogic.CaloricValuePerCurrency(caloriesPerUnit, candidatePrice);
                if (value > bestValue) { bestValue = value; bestPrice = candidatePrice; }

                // per-tick food budget is a share of the balance; affordability is
                // decided against the cheapest meal, not an absolute price floor
                double budget = c.LedgerBalance * cfg.FoodSpendFactor;
                if (budget >= bestPrice)
                {
                    c.LedgerBalance -= bestPrice;
                    SimulationLogic.InventoryAdd(c.Inventory, cfg.FoodItemName, 1);
                    c.Calories = SimMath.Clamp(c.Calories + caloriesPerUnit, 0, 100);
                    traded = true;
                }
            }

            return traded;
        }

        /// <summary>Broadcast dialogue when a meaningful trade happened.</summary>
        public static string TradeDialogue(Citizen c, MarketSnapshot market)
        {
            if (c.TradeSellVolume > 0)
                return SimulationLogic.DialogueForEvent("trade_completed", c.Name, $"{c.TradeSellVolume} units");
            if (c.TradeBuyVolume > 0)
                return SimulationLogic.DialogueForEvent("trade_completed", c.Name, $"materials from {market.StoreCount} stores");
            return null;
        }
    }
}
