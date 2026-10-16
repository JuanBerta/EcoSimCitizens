using System;
using System.Collections.Generic;
using Eco.Gameplay.Skills;

namespace EcoSimCitizens
{
    /// <summary>
    /// One simulated citizen. In v0.2 the citizen is also backed by a real
    /// CitizenNpc WorldObject, so it exists physically in the Eco world.
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
        public bool HasBeenFunded { get; set; }           // real bank account funded once
        public string NpcId { get; set; }                 // optional reference to the live NPC

        public bool HasHome => !string.IsNullOrEmpty(HomeDeedName);
    }

    /// <summary>
    /// Profession table. In v0.2 it is built from Eco's real skill system:
    /// every root skill in Skill.AllSkills becomes a citizen profession with
    /// productivity/earnings tuned per craft. A small static table remains as
    /// fallback until the game's skills are available.
    /// </summary>
    public static class Professions
    {
        public sealed class Entry
        {
            public string Id;
            public double Productivity;
            public double EarnMultiplier;
            public string Focus;
        }

        private static readonly Dictionary<string, Entry> FallbackTable = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Laborer"]  = new Entry { Id = "Laborer",  Productivity = 1.0,  EarnMultiplier = 1.0,  Focus = "General labour" },
            ["Farmer"]   = new Entry { Id = "Farmer",   Productivity = 1.3,  EarnMultiplier = 1.1,  Focus = "Food security" },
            ["Miner"]    = new Entry { Id = "Miner",    Productivity = 1.4,  EarnMultiplier = 1.2,  Focus = "Raw materials" },
            ["Crafter"]  = new Entry { Id = "Crafter",  Productivity = 1.5,  EarnMultiplier = 1.3,  Focus = "Manufactured goods" },
            ["Merchant"] = new Entry { Id = "Merchant", Productivity = 0.9,  EarnMultiplier = 1.6,  Focus = "Trade & pricing" },
            ["Builder"]  = new Entry { Id = "Builder",  Productivity = 1.6,  EarnMultiplier = 1.15, Focus = "Construction & housing" },
            ["Scholar"]  = new Entry { Id = "Scholar",  Productivity = 0.7,  EarnMultiplier = 1.0,  Focus = "Research & civics" },
        };

        private static readonly Dictionary<string, Entry> RealSkills = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Sync = new object();
        private static bool _skillsLoaded;

        /// <summary>Builds the profession table from Eco's real Skill.AllSkills (root skills only).</summary>
        public static void LoadRealSkills()
        {
            lock (Sync)
            {
                if (_skillsLoaded) return;
                _skillsLoaded = true;
                try
                {
                    var all = Skill.AllSkills;
                    if (all == null) return;
                    foreach (var s in all)
                    {
                        if (s == null || !s.IsRoot) continue;
                        RealSkills[s.Name] = ResolveFromSkillName(s.Name);
                    }
                    PluginLog.Info($"Loaded {RealSkills.Count} real Eco professions from Skill.AllSkills.");
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("Could not load real skills: " + ex.Message);
                }
            }
        }

        public static Entry Get(string id)
        {
            LoadRealSkills();
            if (!string.IsNullOrEmpty(id) && RealSkills.TryGetValue(id, out var real)) return real;
            if (!string.IsNullOrEmpty(id) && FallbackTable.TryGetValue(id, out var fb)) return fb;
            return FallbackTable["Laborer"];
        }

        public static IEnumerable<string> AllIds
        {
            get
            {
                LoadRealSkills();
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in RealSkills.Keys) set.Add(k);
                foreach (var k in FallbackTable.Keys) set.Add(k);
                return set;
            }
        }

        public static IEnumerable<string> RealSkillIds
        {
            get
            {
                LoadRealSkills();
                return new List<string>(RealSkills.Keys);
            }
        }

        private static Entry ResolveFromSkillName(string name)
        {
            if (name == null) return Mk("Laborer", 1.0, 1.0, "General labour");
            if (name.Contains("Carpent", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.6, 1.15, "Construction & housing");
            if (name.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.5, 1.3, "Metalworking");
            if (name.Contains("Smith", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.5, 1.3, "Metalworking");
            if (name.Contains("Smelt", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.5, 1.3, "Metalworking");
            if (name.Contains("Mason", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.4, 1.15, "Construction & housing");
            if (name.Contains("Mining", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.4, 1.2, "Raw materials");
            if (name.Contains("Logg", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.2, 1.05, "Timber & raw materials");
            if (name.Contains("Farm", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.3, 1.1, "Food security");
            if (name.Contains("Cook", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.3, 1.1, "Food & cooking");
            if (name.Contains("Chef", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.3, 1.1, "Food & cooking");
            if (name.Contains("Bak", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.2, 1.1, "Food & cooking");
            if (name.Contains("Hunt", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.1, 1.0, "Hunting & food");
            if (name.Contains("Gather", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.0, 1.0, "Foraging & raw materials");
            if (name.Contains("Tailor", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.2, 1.1, "Clothing");
            if (name.Contains("Electron", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.4, 1.2, "Advanced crafting");
            if (name.Contains("Mechanic", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.4, 1.2, "Machinery");
            if (name.Contains("Scientist", StringComparison.OrdinalIgnoreCase)) return Mk(name, 0.8, 1.0, "Research & civics");
            if (name.Contains("Science", StringComparison.OrdinalIgnoreCase)) return Mk(name, 0.8, 1.0, "Research & civics");
            if (name.Contains("Self", StringComparison.OrdinalIgnoreCase)) return Mk(name, 0.8, 1.0, "Personal development");
            if (name.Contains("Oil", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.5, 1.3, "Specialist craft");
            if (name.Contains("Recycl", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.3, 1.1, "Specialist craft");
            if (name.Contains("Composites", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.4, 1.2, "Specialist craft");
            if (name.Contains("Ship", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.3, 1.1, "Specialist craft");
            if (name.Contains("Glass", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.3, 1.1, "Specialist craft");
            if (name.Contains("Pottery", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.2, 1.1, "Specialist craft");
            if (name.Contains("Paper", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.1, 1.0, "Specialist craft");
            if (name.Contains("Mill", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.1, 1.0, "Specialist craft");
            if (name.Contains("Fertilizers", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.2, 1.1, "Specialist craft");
            if (name.Contains("Cutting", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.5, 1.3, "Specialist craft");
            if (name.Contains("Advanced", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.5, 1.3, "Specialist craft");
            if (name.Contains("Campfire", StringComparison.OrdinalIgnoreCase)) return Mk(name, 1.1, 1.0, "Cooking basics");
            return Mk(name, 1.0, 1.0, "General labour");
        }

        private static Entry Mk(string id, double prod, double earn, string focus)
            => new Entry { Id = id, Productivity = prod, EarnMultiplier = earn, Focus = focus };
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
