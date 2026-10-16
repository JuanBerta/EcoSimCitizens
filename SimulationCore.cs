using System;
using System.Collections.Generic;
using System.Linq;
using Eco.Gameplay.Players;
using Eco.Gameplay.Settlements;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// The heart of the mod: advances every citizen one tick. The simulation is a
    /// closed loop driven by the live Eco economy/civics - prices, food availability,
    /// deeds and settlements all feed back into citizen wellbeing and behaviour.
    /// </summary>
    public class SimulationCore
    {
        private readonly MarketScanner _market = new MarketScanner();
        private readonly CivicObserver _civic = new CivicObserver();

        public MarketSnapshot LastMarket { get; private set; } = new MarketSnapshot();
        public CivicSnapshot LastCivic { get; private set; } = new CivicSnapshot();

        public void Tick(CitizensDatabase db, ModConfig cfg)
        {
            var market = _market.Scan();
            var civic = _civic.Observe();
            var citizens = db.Snapshot();

            foreach (var c in citizens)
            {
                try
                {
                    TickCitizen(c, db, cfg, market, civic);
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"Tick failed for citizen '{c.Name}': {ex.Message}");
                }
            }

            LastMarket = market;
            LastCivic = civic;
        }

        private static void TickCitizen(Citizen c, CitizensDatabase db, ModConfig cfg, MarketSnapshot market, CivicSnapshot civic)
        {
            c.AgeInTicks++;

            // --- needs decay ---
            c.Calories = SimMath.Clamp(c.Calories - cfg.CalorieDecayPerTick, 0, 100);
            c.Comfort  = SimMath.Clamp(c.Comfort  - cfg.ComfortDecayPerTick, 0, 100);
            c.Social   = SimMath.Clamp(c.Social   - cfg.SocialDecayPerTick, 0, 100);

            // --- work: profession x trait x health ---
            var prof = Professions.Get(c.Profession);
            double traitWork = Traits.WorkMultiplier(c.Trait);
            double healthFactor = 0.4 + c.Calories / 100.0;
            double productivity = prof.Productivity * traitWork * healthFactor;

            c.WorkUnitsProduced += (long)(productivity * 100);
            c.TicksWorked++;
            c.LedgerBalance += cfg.WagePerTick * prof.EarnMultiplier * traitWork;

            // --- consume: real NPCs spend real in-game money; fallback to market index ---
            var npc = NpcManager.Find(c.Name);
            if (npc != null)
            {
                npc.BehaviorTick(c);
                if (!c.HasBeenFunded)
                {
                    CitizenEconomy.GrantStartFunds(c.Name, (float)cfg.StartingBalance);
                    c.HasBeenFunded = true;
                }
                if (c.Calories < 65 && CitizenEconomy.TryBuyCheapestFood(c.Name, out float spent))
                {
                    c.Calories = SimMath.Clamp(c.Calories + cfg.CalorieGainFromFood, 0, 100);
                    c.LedgerBalance -= spent;
                }
            }
            else if (market.HasMarket && market.FoodOfferCount > 0 && market.MinPrice > 0)
            {
                double cost = market.MinPrice * cfg.FoodSpendFactor;
                if (c.LedgerBalance >= cost)
                {
                    c.Calories = SimMath.Clamp(c.Calories + cfg.CalorieGainFromFood, 0, 100);
                    c.LedgerBalance -= cost;
                }
            }

            // --- housing: deed-derived comfort ---
            double targetComfort = HousingEvaluator.EvaluateComfort(c);
            c.Comfort = SimMath.Clamp(c.Comfort * 0.9 + targetComfort * 0.1, 0, 100);

            // --- social: communities of citizens (and gregarious traits) help ---
            double socialGain = Math.Min(5.0, db.Count * 0.25) + Traits.SocialBonus(c.Trait);
            c.Social = SimMath.Clamp(c.Social + socialGain, 0, 100);

            // --- happiness: weighted needs + trait bonus ---
            c.Happiness = SimMath.Clamp(
                c.Calories * 0.35 + c.Comfort * 0.30 + c.Social * 0.35 + Traits.HappinessBonus(c.Trait),
                0, 100);
        }

        // --- report helpers ---

        public double AverageHappiness(IEnumerable<Citizen> citizens)
        {
            var list = new List<double>();
            foreach (var c in citizens) list.Add(c.Happiness);
            return SimMath.Average(list);
        }

        public string BestCitizen(IEnumerable<Citizen> citizens)
        {
            string best = "-";
            double bestH = -1;
            foreach (var c in citizens)
            {
                if (c.Happiness > bestH) { bestH = c.Happiness; best = c.Name; }
            }
            return bestH < 0 ? "-" : $"{best} ({bestH:0.0})";
        }

        public string WorstCitizen(IEnumerable<Citizen> citizens)
        {
            string worst = "-";
            double worstH = 101;
            foreach (var c in citizens)
            {
                if (c.Happiness < worstH) { worstH = c.Happiness; worst = c.Name; }
            }
            return worstH > 100 ? "-" : $"{worst} ({worstH:0.0})";
        }
    }

    /// <summary>
    /// Formats the daily report that is broadcast to online players.
    /// </summary>
    public static class ReportBuilder
    {
        public static string Build(CitizensDatabase db, SimulationCore sim, ModConfig cfg)
        {
            var citizens = db.Snapshot();
            var m = sim.LastMarket;
            var v = sim.LastCivic;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== EcoSim Citizens Report ===");
            int npcCount = 0;
            try { npcCount = NpcManager.GetAll().Count(); } catch { }
            sb.AppendLine($"Population: {db.Count} citizens | Real residents: {v.RealPlayerCount} | World NPCs: {npcCount} | Day {Eco.Shared.Time.TimeUtil.Days:0.0}");

            if (m.HasMarket)
            {
                string trend = m.TrendPercent >= 0 ? $"+{m.TrendPercent:0.0}%" : $"{m.TrendPercent:0.0}%";
                sb.AppendLine($"Market: {m.StoreCount} stores, {m.OfferCount} offers ({m.PricedOfferCount} priced), avg price {m.AvgPrice:0.0} ({trend})");
                sb.AppendLine($"Food offers: {m.FoodOfferCount}, cheapest meal ~{m.MinPrice:0.00}");
            }
            else
            {
                sb.AppendLine("Market: no active stores yet - citizens struggle to eat.");
            }

            sb.AppendLine($"Civic: {v.SettlementCount} settlements, {v.ActiveElectionCount} active elections, {v.DeedCount} deeds, civic score {v.CivicScore:0.0}/100");

            if (db.Count > 0)
            {
                double avgH = sim.AverageHappiness(citizens);
                sb.AppendLine($"Happiness: avg {avgH:0.0}/100 | best {sim.BestCitizen(citizens)} | worst {sim.WorstCitizen(citizens)}");
                double totalLedger = 0;
                long totalWork = 0;
                foreach (var c in citizens)
                {
                    totalLedger += c.LedgerBalance;
                    totalWork += c.WorkUnitsProduced;
                }
                sb.AppendLine($"Ledger total: {totalLedger:0.00} | Work units produced: {totalWork:N0}");
            }
            else
            {
                sb.AppendLine("No citizens yet - admins can spawn them with /ecosim spawn <name> <profession>.");
            }
            return sb.ToString();
        }
    }
}
