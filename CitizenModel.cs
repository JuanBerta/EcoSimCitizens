using System;
using System.Collections.Generic;

namespace EcoSimCitizens
{
    /// <summary>
    /// One simulated citizen. This is pure mod state - nothing here touches Eco
    /// entities directly, which keeps the simulation safe and reversible.
    /// </summary>
    public class Citizen
    {
        public string Name { get; set; }
        public string Profession { get; set; } = "Laborer";
        public string Trait { get; set; } = "Balanced";
        public string HomeDeedName { get; set; }          // optional: name of an in-game deed
        public double LedgerBalance { get; set; }
        public double Calories { get; set; }              // 0..100
        public double Comfort { get; set; }               // 0..100
        public double Social { get; set; }                // 0..100
        public double Happiness { get; set; }             // computed 0..100
        public long WorkUnitsProduced { get; set; }
        public long TicksWorked { get; set; }
        public int AgeInTicks { get; set; }

        public bool HasHome => !string.IsNullOrEmpty(HomeDeedName);
    }

    /// <summary>Static profession table. Each profession shapes work output and earning.</summary>
    public static class Professions
    {
        public sealed class Entry
        {
            public string Id;
            public double Productivity;
            public double EarnMultiplier;
            public string Focus;
        }

        private static readonly Dictionary<string, Entry> Table = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Laborer"]  = new Entry { Id = "Laborer",  Productivity = 1.0,  EarnMultiplier = 1.0,  Focus = "General labour" },
            ["Farmer"]   = new Entry { Id = "Farmer",   Productivity = 1.3,  EarnMultiplier = 1.1,  Focus = "Food security" },
            ["Miner"]    = new Entry { Id = "Miner",    Productivity = 1.4,  EarnMultiplier = 1.2,  Focus = "Raw materials" },
            ["Crafter"]  = new Entry { Id = "Crafter",  Productivity = 1.5,  EarnMultiplier = 1.3,  Focus = "Manufactured goods" },
            ["Merchant"] = new Entry { Id = "Merchant", Productivity = 0.9,  EarnMultiplier = 1.6,  Focus = "Trade & pricing" },
            ["Builder"]  = new Entry { Id = "Builder",  Productivity = 1.6,  EarnMultiplier = 1.15, Focus = "Construction & housing" },
            ["Scholar"]  = new Entry { Id = "Scholar",  Productivity = 0.7,  EarnMultiplier = 1.0,  Focus = "Research & civics" },
        };

        public static Entry Get(string id)
        {
            return Table.TryGetValue(id ?? "", out var e) ? e : Table["Laborer"];
        }

        public static IEnumerable<string> AllIds => Table.Keys;
    }

    /// <summary>Static trait table. Traits modify productivity and need satisfaction.</summary>
    public static class Traits
    {
        private sealed class Entry
        {
            public double WorkMultiplier;
            public double HappinessBonus;
            public double SocialBonus;
        }

        private static readonly Dictionary<string, Entry> Table = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Diligent"]    = new Entry { WorkMultiplier = 1.25, HappinessBonus = 2,  SocialBonus = 0 },
            ["Lazy"]        = new Entry { WorkMultiplier = 0.80, HappinessBonus = -1, SocialBonus = 0 },
            ["Curious"]     = new Entry { WorkMultiplier = 1.00, HappinessBonus = 5,  SocialBonus = 1 },
            ["Moody"]       = new Entry { WorkMultiplier = 0.95, HappinessBonus = 0,  SocialBonus = 0 },
            ["Gregarious"]  = new Entry { WorkMultiplier = 1.00, HappinessBonus = 2,  SocialBonus = 4 },
            ["Balanced"]    = new Entry { WorkMultiplier = 1.00, HappinessBonus = 0,  SocialBonus = 0 },
        };

        public static double WorkMultiplier(string id) => Table.TryGetValue(id ?? "", out var e) ? e.WorkMultiplier : 1.0;
        public static double HappinessBonus(string id) => Table.TryGetValue(id ?? "", out var e) ? e.HappinessBonus : 0.0;
        public static double SocialBonus(string id) => Table.TryGetValue(id ?? "", out var e) ? e.SocialBonus : 0.0;

        public static IEnumerable<string> AllIds => Table.Keys;
    }

    /// <summary>Utility helpers used across the simulation.</summary>
    public static class SimMath
    {
        public static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        public static double Average(IEnumerable<double> values)
        {
            double sum = 0;
            int count = 0;
            foreach (var v in values)
            {
                sum += v;
                count++;
            }
            return count == 0 ? 0 : sum / count;
        }
    }
}
