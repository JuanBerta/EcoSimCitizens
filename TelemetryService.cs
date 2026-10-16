using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EcoSimCitizens
{
    /// <summary>
    /// PRD Module 7 (admin telemetry, Req.20). Aggregates simulation KPIs and
    /// renders them as JSON for /npc stats. Includes GDP proxy, happiness,
    /// inventory flow, pathfinding performance, and per-module counters so
    /// admins can verify the simulation is healthy at a glance.
    /// </summary>
    public class TelemetryService
    {
        // rolling counters
        public long TotalTicks { get; private set; }
        public double GdpProxy { get; private set; }
        public double LastAvgHappiness { get; private set; }
        public long ItemsProducedAll { get; private set; }
        public long ItemsSoldAll { get; private set; }
        public long ItemsBoughtAll { get; private set; }

        public void ObserveTick(CitizensDatabase db, SimulationCore sim, MarketSnapshot market, AgentPoolManager pool)
        {
            TotalTicks++;
            var citizens = db.Snapshot();
            if (db.Count > 0)
            {
                LastAvgHappiness = sim.AverageHappiness(citizens);
                double totalLedger = 0;
                foreach (var c in citizens)
                {
                    totalLedger += c.LedgerBalance;
                    ItemsProducedAll += c.ItemsProduced;
                    ItemsSoldAll += c.ItemsSold;
                    ItemsBoughtAll += c.TradeBuyVolume;
                }
                GdpProxy = GdpProxy * 0.9 + totalLedger * 0.1;
            }
        }

        /// <summary>
        /// Builds the JSON stats document. When settlementId is provided the
        /// document is scoped to that settlement's citizens and systems.
        /// </summary>
        public string BuildStatsJson(CitizensDatabase db, SimulationCore sim, string settlementId)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"simulation\": {{ \"ticks\": {TotalTicks}, \"gdp_proxy\": {GdpProxy:0.00}, \"avg_happiness\": {LastAvgHappiness:0.00} }},");
            sb.AppendLine($"  \"pathfinding\": {{ \"physical_ticks\": {sim.Pool.PhysicalMoveTicks}, \"logical_ticks\": {sim.Pool.LogicalMoveTicks}, \"physical_ratio\": {sim.Pool.PhysicalPathfindingRatio():0.000} }},");
            sb.AppendLine($"  \"flows\": {{ \"produced\": {ItemsProducedAll}, \"sold\": {ItemsSoldAll}, \"bought\": {ItemsBoughtAll} }},");

            var citizens = db.Snapshot()
                .Where(c => string.IsNullOrEmpty(settlementId) || string.Equals(c.SettlementName, settlementId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            sb.AppendLine($"  \"citizens\": [");
            for (int i = 0; i < citizens.Count; i++)
            {
                var c = citizens[i];
                sb.Append($"    {{ \"name\": \"{c.Name}\", \"profession\": \"{c.Profession}\", \"happiness\": {c.Happiness:0.0}, \"ledger\": {c.LedgerBalance:0.0}, \"skill\": {CitizenSkills.GetLevel(c)}, \"settlement\": \"{c.SettlementName}\", \"workparty\": \"{c.WorkPartyId}\", \"corporation\": \"{c.CorporationId}\", \"inventory_total\": {SimulationLogic.InventoryTotal(c.Inventory)} }}");
                sb.AppendLine(i < citizens.Count - 1 ? "," : "");
            }
            sb.AppendLine("  ],");

            sb.AppendLine($"  \"systems\": {{ \"parties\": {sim.WorkParties.Parties.Count}, \"corporations\": {sim.Corporations.Corporations.Count}, \"currencies\": {sim.Currencies.Currencies.Count}, \"listings\": {sim.Exchange.Listings.Count}, \"futures\": {sim.Exchange.Futures.Count}, \"milestones_completed\": {sim.WorkParties.MilestonesCompleted}, \"discoveries\": {sim.Research.DiscoveriesUnlocked}, \"cleanup_actions\": {sim.Environment.CleanupActions}, \"meteor_contributions\": {sim.Environment.MeteorContributions}, \"eco_law_votes\": {sim.Politics.EcoLawVotes}, \"expansions\": {sim.Politics.ExpansionDecisions} }},");

            sb.AppendLine($"  \"environment\": {{ \"pollution\": {sim.Environment.Pollution:0.0}, \"co2\": {sim.Environment.CO2:0.0}, \"soil\": {sim.Environment.SoilDegradation:0.0}, \"meteor_progress\": {sim.Environment.MeteorProgress:0.000}, \"crisis\": {sim.Environment.CrisisActive.ToString().ToLowerInvariant()}, \"impacted\": {sim.Environment.MeteorImpacted.ToString().ToLowerInvariant()} }}");
            sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
