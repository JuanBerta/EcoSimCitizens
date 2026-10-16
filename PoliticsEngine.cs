using System;
using System.Collections.Generic;

namespace EcoSimCitizens
{
    /// <summary>
    /// PRD Module 5 (data-driven politics, Req.6/8). Government decisions are
    /// driven by live data: pollution and market trends shift citizen votes,
    /// shop prices are adjusted with the market trend, and settlements expand
    /// when GDP growth supports it. The engine only computes decisions; the
    /// simulation loop applies them to citizens and the report.
    /// </summary>
    public class PoliticsEngine
    {
        public long EcoLawVotes { get; private set; }
        public long ExpansionDecisions { get; private set; }

        /// <summary>
        /// Chooses this citizen's election vote: profession preference first, then
        /// environmental concern can override toward an ecological law.
        /// </summary>
        public string DecideVote(Citizen c, EnvironmentTracker env, ModConfig cfg)
        {
            string baseVote = SimulationLogic.VotePreferenceForProfession(c.Profession);
            if (env != null && cfg != null
                && SimulationLogic.ShouldVoteEcoLaw(env.Pollution, cfg.EnvironmentThreshold, c.EnvironmentConcern))
            {
                EcoLawVotes++;
                return "EcoLaw";
            }
            return baseVote;
        }

        /// <summary>
        /// Price adjustment for the settlement's stores: follows the market trend
        /// damped by the political mood (green votes push prices down slightly).
        /// </summary>
        public double DecidePrice(double basePrice, double trendPercent, bool ecoMajority)
        {
            double adjusted = SimulationLogic.PriceTrendAdjusted(basePrice, trendPercent, 0.35);
            if (ecoMajority) adjusted *= 0.97; // environmental majority: slight price dampening
            return Math.Max(0.01, adjusted);
        }

        /// <summary>Should the settlement expand into a new store/project?</summary>
        public bool ShouldExpand(double gdpGrowth, double populationGrowth, double treasury)
        {
            bool expand = gdpGrowth > 0.02 && populationGrowth > 0.01 && treasury > 500;
            if (expand) ExpansionDecisions++;
            return expand;
        }
    }
}
