using System;
using System.Collections.Generic;
using System.Linq;

namespace EcoSimCitizens
{
    /// <summary>
    /// PRD Module 3 (technological progression, Req.9). Citizens contribute
    /// research points to shared hubs per domain. When a domain crosses its
    /// unlock threshold the hub "publishes" the discovery, which permanently
    /// boosts skill gain for everyone in that domain.
    /// </summary>
    public class ResearchContribution
    {
        public Dictionary<string, double> DomainPoints { get; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> DomainTier { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public long DiscoveriesUnlocked { get; private set; }

        public double PointsFor(string domain) => DomainPoints.TryGetValue(domain ?? "", out double v) ? v : 0;

        public int TierFor(string domain) => DomainTier.TryGetValue(domain ?? "", out int t) ? t : 0;

        /// <summary>Contributes this tick's research and returns the citizen's new research total.</summary>
        public double Contribute(Citizen c, double productivity, double multiplier)
        {
            if (c == null) return 0;
            string domain = SimulationLogic.ResearchDomainFor(c.Profession);
            bool scholar = c.Profession != null
                && (c.Profession.Contains("Scientist", StringComparison.OrdinalIgnoreCase)
                    || c.Profession.Contains("Science", StringComparison.OrdinalIgnoreCase));
            double points = SimulationLogic.ResearchPointsForTick(productivity, scholar, multiplier);
            c.ResearchPoints += points;
            DomainPoints[domain] = PointsFor(domain) + points;

            int tier = TierFor(domain);
            double cost = SimulationLogic.SkillUnlockResearchCost(tier);
            if (PointsFor(domain) >= cost)
            {
                DomainPoints[domain] = PointsFor(domain) - cost;
                DomainTier[domain] = tier + 1;
                DiscoveriesUnlocked++;
            }
            return c.ResearchPoints;
        }

        /// <summary>Total research points across all domains.</summary>
        public double TotalPoints() => DomainPoints.Values.Sum();
    }
}
