using System;
using System.Collections.Generic;
using System.Linq;

namespace EcoSimCitizens
{
    /// <summary>
    /// Pure simulation rules shared by the Eco plugin and the standalone test
    /// project. This file intentionally has NO dependency on Eco assemblies so the
    /// tests can compile it in isolation and verify the math without a game server.
    /// </summary>
    public static class SimulationLogic
    {
        public static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        // ---------------------------------------------------------------- skills

        /// <summary>Maximum skill level a citizen can reach.</summary>
        public const int MaxLevel = 10;

        /// <summary>Experience needed to reach the next level (square-ish curve).</summary>
        public static double ExperienceToNextLevel(int level) => 100.0 + level * level * 25.0;

        /// <summary>
        /// Advances a skill level with the given work output. Returns the number of
        /// levels gained this tick (usually 0 or 1). Higher levels give diminishing
        /// returns, so the productivity multiplier converges towards 2.0.
        /// </summary>
        public static int AdvanceSkill(ref double experience, ref int level, double output, double learningRate = 1.0)
        {
            if (level >= MaxLevel) return 0;
            experience += output * learningRate;
            int gained = 0;
            while (level < MaxLevel && experience >= ExperienceToNextLevel(level))
            {
                experience -= ExperienceToNextLevel(level);
                level++;
                gained++;
            }
            return gained;
        }

        /// <summary>Productivity multiplier from skill level: 1.0 at level 0 .. ~2.0 at level 10.</summary>
        public static double SkillProductivity(int level) => 1.0 + level * 0.10;

        // ------------------------------------------------------------ lifecycle

        /// <summary>
        /// Maps age in years to a life stage. Birth is recorded when the citizen is
        /// created; they grow through Child -&gt; Adult -&gt; Elder and eventually die.
        /// </summary>
        public static string StageForAge(double ageYears)
        {
            if (ageYears < 14) return "Child";
            if (ageYears >= 65) return "Elder";
            return "Adult";
        }

        /// <summary>
        /// Decides whether the citizen dies this tick. Mortality is a small chance
        /// per tick, heavily influenced by age and by unmet needs (starving citizens
        /// have a much higher chance). Returns the death cause string, or null.
        /// </summary>
        public static string DeathCheck(double ageYears, double calories, double comfort, double social, double happiness, Random rng)
        {
            double baseChance = 0.0004;
            if (ageYears >= 65) baseChance += 0.004 + (ageYears - 65) * 0.0006;
            if (calories < 20) baseChance += 0.05;
            if (comfort < 15) baseChance += 0.01;
            if (social < 10) baseChance += 0.005;
            if (happiness < 10) baseChance += 0.01;
            if (rng.NextDouble() < baseChance)
            {
                if (calories < 20) return "starvation";
                if (comfort < 15) return "exposure";
                if (ageYears >= 65) return "old age";
                return "illness";
            }
            return null;
        }

        /// <summary>Year-equivalents elapsed per simulation tick (Eco day ~ 40 real minutes).</summary>
        public const double YearsPerTick = 1.0 / 365.0;

        // ------------------------------------------------------------- day phase

        /// <summary>
        /// Eco's TimeUtil.Days is a double counting full game days. The fractional
        /// part is the position inside the current day (0..1) which we map onto a
        /// 24-hour clock and four behavioural phases. An Eco day is ~40 real minutes.
        /// </summary>
        public enum DayPhase { Dawn, Day, Dusk, Night }

        public static DayPhase PhaseForTimeOfDay(double timeOfDay)
        {
            double h = timeOfDay * 24.0;
            if (h < 6) return DayPhase.Night;      // 00-06 sleep
            if (h < 8) return DayPhase.Dawn;       // 06-08 wake, commute to work
            if (h < 18) return DayPhase.Day;       // 08-18 work
            if (h < 21) return DayPhase.Dusk;      // 18-21 leisure, return home
            return DayPhase.Night;                 // 21-24 sleep
        }

        public static double TimeOfDayFromDays(double days)
        {
            double f = days - Math.Floor(days);
            return f < 0 ? f + 1.0 : f;
        }

        /// <summary>24h clock label for reports.</summary>
        public static string ClockLabel(double timeOfDay) => $"{(int)(timeOfDay * 24.0):00}:{(int)(timeOfDay * 24.0 * 60.0) % 60:00}";

        /// <summary>Hour-of-day as an integer.</summary>
        public static int HourOfDay(double timeOfDay) => (int)(timeOfDay * 24.0) % 24;

        // ------------------------------------------------------------ inventory

        /// <summary>
        /// Adds quantity to the citizen's personal inventory (a real Eco item type
        /// name -&gt; count ledger). Returns the new count.
        /// </summary>
        public static int InventoryAdd(Dictionary<string, int> inv, string itemTypeName, int qty)
        {
            if (qty <= 0) return InventoryCount(inv, itemTypeName);
            inv[itemTypeName] = InventoryCount(inv, itemTypeName) + qty;
            return inv[itemTypeName];
        }

        /// <summary>
        /// Removes up to qty from the citizen's inventory. Returns the amount that
        /// was actually removed (0 if the citizen does not own enough).
        /// </summary>
        public static int InventoryRemove(Dictionary<string, int> inv, string itemTypeName, int qty)
        {
            if (qty <= 0) return 0;
            int have = InventoryCount(inv, itemTypeName);
            int remove = Math.Min(have, qty);
            if (remove <= 0) return 0;
            inv[itemTypeName] = have - remove;
            if (inv[itemTypeName] <= 0) inv.Remove(itemTypeName);
            return remove;
        }

        public static int InventoryCount(Dictionary<string, int> inv, string itemTypeName)
            => inv != null && inv.TryGetValue(itemTypeName, out int v) ? v : 0;

        public static int InventoryTotal(Dictionary<string, int> inv)
        {
            if (inv == null) return 0;
            int total = 0;
            foreach (var kv in inv) total += kv.Value;
            return total;
        }

        /// <summary>
        /// Converts food inventory into calories for a citizen. Food item names that
        /// contain these tokens count as edible; each unit restores a fixed amount.
        /// </summary>
        public static int ConsumeFood(Dictionary<string, int> inv, int need)
        {
            if (inv == null || need <= 0) return 0;
            var edible = inv.Where(kv => kv.Value > 0 && IsFoodItem(kv.Key)).ToList();
            int consumedUnits = 0;
            foreach (var kv in edible)
            {
                if (need <= 0) break;
                int take = Math.Min(kv.Value, need);
                int removed = InventoryRemove(inv, kv.Key, take);
                consumedUnits += removed;
                need -= removed;
            }
            return consumedUnits;
        }

        public static bool IsFoodItem(string itemTypeName)
        {
            if (string.IsNullOrEmpty(itemTypeName)) return false;
            return itemTypeName.Contains("Food", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Meal", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Bread", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Soup", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Salad", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Stew", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Baked", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Cooked", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Fish", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Meat", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Fruit", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Vegetable", StringComparison.OrdinalIgnoreCase)
                || itemTypeName.Contains("Mushroom", StringComparison.OrdinalIgnoreCase);
        }

        // --------------------------------------------------------------- voting

        /// <summary>Tallies votes for a ranked list of (choice, count) pairs.</summary>
        public static string WinningChoice(List<KeyValuePair<string, int>> votes)
        {
            if (votes == null || votes.Count == 0) return null;
            string best = null;
            int bestCount = -1;
            foreach (var kv in votes)
            {
                if (kv.Value > bestCount) { bestCount = kv.Value; best = kv.Key; }
            }
            return best;
        }

        /// <summary>
        /// Maps a citizen's profession to the election choice they prefer. Citizens
        /// vote with their wallet: producers favour growth policies, labourers
        /// favour welfare, scholars favour law &amp; order.
        /// </summary>
        public static string VotePreferenceForProfession(string profession)
        {
            if (profession == null) return "Growth";
            if (profession.Contains("Merchant", StringComparison.OrdinalIgnoreCase)) return "Trade";
            if (profession.Contains("Farmer", StringComparison.OrdinalIgnoreCase)) return "Agriculture";
            if (profession.Contains("Miner", StringComparison.OrdinalIgnoreCase)) return "Growth";
            if (profession.Contains("Carpent", StringComparison.OrdinalIgnoreCase)) return "Housing";
            if (profession.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase)) return "Industry";
            if (profession.Contains("Scientist", StringComparison.OrdinalIgnoreCase)) return "Research";
            if (profession.Contains("Science", StringComparison.OrdinalIgnoreCase)) return "Research";
            if (profession.Contains("Laborer", StringComparison.OrdinalIgnoreCase)) return "Welfare";
            return "Growth";
        }

        // ==================================================== PRD Module 2: comparative advantage trading

        /// <summary>
        /// Comparative advantage: if producing one unit ourselves costs more than
        /// buying it on the public market (plus a margin), agents prefer to buy.
        /// </summary>
        public static bool ShouldBuyInsteadOfProduce(double selfCost, double marketPrice, double margin = 0.15)
            => marketPrice > 0 && selfCost > marketPrice * (1.0 + margin);

        /// <summary>Estimated per-unit production cost: wages plus raw material cost.</summary>
        public static double EstimatedUnitCost(double wagePerTick, double outputPerTick, double rawCostPerUnit)
            => outputPerTick <= 0 ? double.MaxValue : (wagePerTick / outputPerTick) + rawCostPerUnit;

        /// <summary>Positive margin means selling is profitable.</summary>
        public static double TradeMargin(double sellPrice, double unitCost) => sellPrice - unitCost;

        /// <summary>Price adjustment decision: agents follow the market trend but damp it.</summary>
        public static double PriceTrendAdjusted(double basePrice, double trendPercent, double sensitivity)
            => basePrice * (1.0 + SimMathClamp(trendPercent / 100.0 * sensitivity, -0.5, 0.5));

        /// <summary>Calories gained per currency unit spent (higher is better value).</summary>
        public static double CaloricValuePerCurrency(double calories, double price)
            => price <= 0 ? 0 : calories / price;

        // ==================================================== PRD Module 2: work parties & contracts

        /// <summary>Milestone payment: each contributor receives a share proportional to their contribution.</summary>
        public static double MilestoneShare(double pool, double contribution, double totalContribution)
            => totalContribution <= 0 ? 0 : pool * contribution / totalContribution;

        /// <summary>Hourly wage a party can offer with a given budget and hours.</summary>
        public static double HourlyWageFor(double budget, double hours)
            => hours <= 0 ? 0 : budget / hours;

        /// <summary>Task focus of a work party for a profession.</summary>
        public static string PartyTaskFor(string profession)
        {
            if (profession == null) return "General";
            if (profession.Contains("Miner", StringComparison.OrdinalIgnoreCase)) return "Mining";
            if (profession.Contains("Carpent", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Mason", StringComparison.OrdinalIgnoreCase)) return "Construction";
            if (profession.Contains("Farm", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Cook", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Chef", StringComparison.OrdinalIgnoreCase)) return "Food";
            if (profession.Contains("Logg", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Gather", StringComparison.OrdinalIgnoreCase)) return "Harvesting";
            if (profession.Contains("Smelt", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Electron", StringComparison.OrdinalIgnoreCase)) return "Crafting";
            if (profession.Contains("Merchant", StringComparison.OrdinalIgnoreCase)) return "Transport";
            return "General";
        }

        // ==================================================== PRD Module 3: dynamic construction

        /// <summary>Work applied to a construction plan; complexity slows it down.</summary>
        public static double ConstructionProgress(double current, double workApplied, double complexity)
            => SimMathClamp(current + (complexity <= 0 ? workApplied : workApplied / complexity), 0.0, 1.0);

        public static bool ConstructionComplete(double progress) => progress >= 1.0;

        /// <summary>Blueprint choice: builders raise housing, merchants build stores, others workshops.</summary>
        public static string BlueprintFor(string profession, string biome)
        {
            string b = "Workshop";
            if (profession != null)
            {
                if (profession.Contains("Carpent", StringComparison.OrdinalIgnoreCase)
                    || profession.Contains("Mason", StringComparison.OrdinalIgnoreCase)
                    || profession.Contains("Builder", StringComparison.OrdinalIgnoreCase)) b = "House";
                if (profession.Contains("Merchant", StringComparison.OrdinalIgnoreCase)) b = "Store";
                if (profession.Contains("Farmer", StringComparison.OrdinalIgnoreCase)) b = "Farmhouse";
                if (profession.Contains("Miner", StringComparison.OrdinalIgnoreCase)) b = "Minecamp";
                if (profession.Contains("Scientist", StringComparison.OrdinalIgnoreCase)
                    || profession.Contains("Science", StringComparison.OrdinalIgnoreCase)) b = "Laboratory";
            }
            // biome-adaptive materials: colder biomes swap timber for stone framing.
            if (biome != null && (biome.Contains("Snow", StringComparison.OrdinalIgnoreCase)
                || biome.Contains("Tundra", StringComparison.OrdinalIgnoreCase)
                || biome.Contains("Ice", StringComparison.OrdinalIgnoreCase)))
            {
                b = b + " (Cold Frame)";
            }
            else if (biome != null && (biome.Contains("Desert", StringComparison.OrdinalIgnoreCase)
                || biome.Contains("Arid", StringComparison.OrdinalIgnoreCase)))
            {
                b = b + " (Mud Frame)";
            }
            return b;
        }

        /// <summary>Base complexity of a blueprint by name keywords.</summary>
        public static double BlueprintComplexity(string blueprint)
        {
            if (blueprint == null) return 10.0;
            if (blueprint.Contains("House", StringComparison.OrdinalIgnoreCase)) return 24.0;
            if (blueprint.Contains("Store", StringComparison.OrdinalIgnoreCase)) return 18.0;
            if (blueprint.Contains("Farmhouse", StringComparison.OrdinalIgnoreCase)) return 20.0;
            if (blueprint.Contains("Minecamp", StringComparison.OrdinalIgnoreCase)) return 16.0;
            if (blueprint.Contains("Laboratory", StringComparison.OrdinalIgnoreCase)) return 28.0;
            return 12.0; // workshop
        }

        /// <summary>Material cost of a blueprint, roughly complexity * material unit price.</summary>
        public static double BlueprintMaterialCost(string blueprint, double materialUnitPrice)
            => BlueprintComplexity(blueprint) * Math.Max(1.0, materialUnitPrice);

        // ==================================================== PRD Module 3: technological progression

        /// <summary>Research points contributed this tick; scholars contribute more.</summary>
        public static double ResearchPointsForTick(double productivity, bool isScholar, double multiplier)
            => Math.Max(0.1, productivity * (isScholar ? 2.5 : 1.0) * multiplier);

        /// <summary>Research needed to unlock the next skill tier; grows with tier.</summary>
        public static double SkillUnlockResearchCost(int currentTier) => 50.0 + currentTier * currentTier * 40.0;

        public static string ResearchDomainFor(string profession)
        {
            if (profession == null) return "General";
            if (profession.Contains("Farm", StringComparison.OrdinalIgnoreCase)) return "Agriculture";
            if (profession.Contains("Miner", StringComparison.OrdinalIgnoreCase)) return "Mining";
            if (profession.Contains("Smelt", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Electron", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Mechanic", StringComparison.OrdinalIgnoreCase)) return "Industry";
            if (profession.Contains("Carpent", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Mason", StringComparison.OrdinalIgnoreCase)) return "Construction";
            if (profession.Contains("Scientist", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Science", StringComparison.OrdinalIgnoreCase)) return "Science";
            if (profession.Contains("Cook", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Chef", StringComparison.OrdinalIgnoreCase)) return "Cooking";
            return "General";
        }

        // ==================================================== PRD Module 4: corporations

        /// <summary>Equity-weighted governance vote: each owner votes with their shares.</summary>
        public static string EquityVoteWinner(List<(string owner, double shares, string choice)> votes)
        {
            if (votes == null || votes.Count == 0) return null;
            double best = -1;
            string winner = null;
            foreach (var (owner, shares, choice) in votes)
            {
                if (shares > best) { best = shares; winner = choice; }
            }
            return winner;
        }

        /// <summary>Share value: net assets divided by total issued shares.</summary>
        public static double ShareValue(double assets, double liabilities, int totalShares)
            => totalShares <= 0 ? 1.0 : Math.Max(0.01, (assets - liabilities) / totalShares);

        /// <summary>Should the corporation hire when the expected gain exceeds the wage cost?</summary>
        public static bool ShouldHire(double budget, double wageCost, double expectedGain)
            => budget > wageCost && expectedGain > wageCost * 1.1;

        // ==================================================== PRD Module 4: securities & futures exchange

        /// <summary>Future contract price: base price scaled by scarcity and a volatility wiggle.</summary>
        public static double FutureContractPrice(double basePrice, double scarcity, double volatility)
            => basePrice * SimMathClamp(1.0 + scarcity, 0.5, 3.0) * SimMathClamp(1.0 + volatility, 0.8, 1.2);

        /// <summary>Scarcity from supply/demand: demand above supply pushes scarcity above 0.</summary>
        public static double ScarcityFromSupplyDemand(double supply, double demand)
            => supply <= 0 ? 1.0 : SimMathClamp((demand - supply) / supply, -0.5, 2.0);

        /// <summary>Buy futures when the contract is cheap relative to the expected future price.</summary>
        public static bool ShouldBuyFuture(double contractPrice, double expectedFuturePrice, double riskTolerance)
            => contractPrice > 0 && expectedFuturePrice > contractPrice * (1.0 + riskTolerance);

        // ==================================================== PRD Module 5: settlement currencies & forex

        /// <summary>
        /// Dynamic forex rate between two settlement currencies: stronger GDP and
        /// larger backing reserves make the first currency more valuable.
        /// </summary>
        public static double ForexRate(double gdpRatio, double reserveRatio, double baseRate)
            => baseRate * SimMathClamp(0.5 + gdpRatio * 0.35 + reserveRatio * 0.35, 0.25, 4.0);

        /// <summary>Money supply growth: minted currency follows population and reserves.</summary>
        public static double MintSupplyGrowth(double population, double backingReserve)
            => Math.Max(0.0, population * 0.05 + backingReserve * 0.01);

        public static double ConvertCurrency(double amount, double rate)
            => amount * rate;

        // ==================================================== PRD Module 6: ecology & crisis management

        /// <summary>Pollution growth per tick: industrial activity adds, cleanup removes.</summary>
        public static double PollutionGrowth(double basePollution, double industrialActivity, double cleanup)
            => SimMathClamp(basePollution + industrialActivity * 0.02 - cleanup * 0.05, 0.0, 100.0);

        public static bool EnvironmentalCrisis(double pollution, double co2, double soil, double threshold)
            => pollution >= threshold || co2 >= threshold || soil >= threshold;

        /// <summary>Citizens with high concern vote for strict ecological laws.</summary>
        public static bool ShouldVoteEcoLaw(double pollution, double threshold, double civicEngagement)
            => pollution >= threshold * 0.6 && civicEngagement >= 0.2;

        /// <summary>Meteor defense progress towards the laser power goal.</summary>
        public static double MeteorDefenseProgress(double current, double powerContribution, double resourceContribution, double totalNeeded)
            => SimMathClamp(current + (powerContribution + resourceContribution) / Math.Max(1.0, totalNeeded), 0.0, 1.0);

        /// <summary>Once the meteor window passes without a defended laser, agents switch to extreme survival mode.</summary>
        public static bool ExtremeSurvivalMode(double meteorProgress, double impactDay, double currentDay)
            => meteorProgress < 1.0 && currentDay >= impactDay;

        public static string CleanerRecipeFor(string profession)
        {
            if (profession == null) return "StandardRecipe";
            if (profession.Contains("Smelt", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase)) return "ElectricSmelterRecipe";
            if (profession.Contains("Electron", StringComparison.OrdinalIgnoreCase)) return "SolarAssemblyRecipe";
            if (profession.Contains("Miner", StringComparison.OrdinalIgnoreCase)) return "EfficientMiningRecipe";
            if (profession.Contains("Farm", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Cook", StringComparison.OrdinalIgnoreCase)) return "OrganicFarmingRecipe";
            return "StandardRecipe";
        }

        /// <summary>Post-impact priorities for extreme survival mode.</summary>
        public static string SurvivalPriorityFor(string profession)
        {
            if (profession == null) return "DebrisClearance";
            if (profession.Contains("Miner", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Logg", StringComparison.OrdinalIgnoreCase)) return "DebrisClearance";
            if (profession.Contains("Farm", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Cook", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Chef", StringComparison.OrdinalIgnoreCase)) return "AirPurification";
            if (profession.Contains("Scientist", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Science", StringComparison.OrdinalIgnoreCase)) return "SupplyChainRebuild";
            if (profession.Contains("Carpent", StringComparison.OrdinalIgnoreCase)
                || profession.Contains("Mason", StringComparison.OrdinalIgnoreCase)) return "ShelterRebuild";
            return "SupplyChainRebuild";
        }

        // ==================================================== PRD Module 7: contextual dialogue

        /// <summary>
        /// Event-driven dialogue lines. Events: trade_completed, pollution_complaint,
        /// job_offer, meteor_warning, vote_result, milestone_paid, crisis_active.
        /// </summary>
        public static string DialogueForEvent(string eventType, string citizenName, string subject)
        {
            string who = string.IsNullOrEmpty(citizenName) ? "A citizen" : citizenName;
            switch (eventType)
            {
                case "trade_completed": return $"{who}: Sold {subject} at the market - good price.";
                case "pollution_complaint": return $"{who}: The {subject} levels worry me. We should vote for cleaner laws.";
                case "job_offer": return $"{who}: I'm taking a {subject} contract - fair wage for fair work.";
                case "meteor_warning": return $"{who}: The {subject} is close. I'm contributing to the meteor laser.";
                case "vote_result": return $"{who}: Glad {subject} won the vote. Democracy works.";
                case "milestone_paid": return $"{who}: Work party milestone reached, {subject} paid out!";
                case "crisis_active": return $"{who}: Extreme survival mode. Clearing {subject} debris around the settlement.";
                case "research_done": return $"{who}: New {subject} research complete - my skills just improved.";
                case "company_dividend": return $"{who}: {subject} paid a dividend. The corporation is healthy.";
                case "forex_news": return $"{who}: Exchange rate moved on {subject}. Good time to trade currency.";
                default: return $"{who}: {subject}";
            }
        }

        private static double SimMathClamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
    }
}
