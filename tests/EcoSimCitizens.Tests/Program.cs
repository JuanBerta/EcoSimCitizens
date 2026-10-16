using System;
using System.Collections.Generic;
using System.Linq;
using EcoSimCitizens;

namespace EcoSimCitizens.Tests
{
    /// <summary>
    /// Standalone test runner for the pure simulation rules. No Eco assemblies are
    /// required, so these tests verify the math that drives NPC behaviour without a
    /// game server. Each test prints PASS/FAIL and the process exits non-zero on
    /// any failure.
    /// </summary>
    public static class Program
    {
        private static int _failures;

        public static int Main()
        {
            SkillTests();
            LifecycleTests();
            DayPhaseTests();
            InventoryTests();
            VotingTests();
            AgentPoolTests();
            CommerceTests();
            WorkPartyTests();
            ConstructionTests();
            ResearchTests();
            CorporationTests();
            ExchangeTests();
            CurrencyTests();
            EnvironmentTests();
            PoliticsTests();
            DialogueTests();
            SimulationStateTests();
            Console.WriteLine();
            if (_failures == 0)
            {
                Console.WriteLine("ALL TESTS PASSED");
                return 0;
            }
            Console.WriteLine($"{_failures} TEST(S) FAILED");
            return 1;
        }

        private static void Check(bool cond, string name)
        {
            if (cond) { Console.WriteLine($"PASS  {name}"); }
            else { _failures++; Console.WriteLine($"FAIL  {name}"); }
        }

        // ------------------------------------------------------------ skills

        private static void SkillTests()
        {
            Console.WriteLine("== Skill growth ==");
            double exp = 0;
            int level = 0;
            int gained = SimulationLogic.AdvanceSkill(ref exp, ref level, 1000, 1.0);
            Check(gained == 4, "large work output levels up multiple times (got " + gained + ")");
            Check(level == 4, "level advanced to 4 (got " + level + ")");
            Check(exp < SimulationLogic.ExperienceToNextLevel(4), "experience rolled over correctly (left " + exp + ", next " + SimulationLogic.ExperienceToNextLevel(4) + ")");

            double exp2 = 0; int level2 = 0;
            int g2 = 0;
            for (int i = 0; i < 500; i++) g2 += SimulationLogic.AdvanceSkill(ref exp2, ref level2, 10, 1.0);
            Check(level2 > 0, "small repeated work eventually levels up (level " + level2 + ")");
            Check(level2 <= 10, "level caps at 10 (level " + level2 + ")");
            Check(g2 == level2, "gained levels equals final level (fresh skill)");

            Check(SimulationLogic.SkillProductivity(0) == 1.0, "level 0 productivity is 1.0");
            Check(Math.Abs(SimulationLogic.SkillProductivity(10) - 2.0) < 1e-9, "level 10 productivity is 2.0");
            Check(SimulationLogic.ExperienceToNextLevel(0) == 100.0, "level 0 threshold is 100");
            Check(SimulationLogic.ExperienceToNextLevel(3) > SimulationLogic.ExperienceToNextLevel(2), "threshold grows with level");

            double expMax = 999999; int levelMax = SimulationLogic.MaxLevel;
            int gainedMax = SimulationLogic.AdvanceSkill(ref expMax, ref levelMax, 999999, 1.0);
            Check(gainedMax == 0 && levelMax == SimulationLogic.MaxLevel, "maxed skill gains no more levels");
        }

        // --------------------------------------------------------- lifecycle

        private static void LifecycleTests()
        {
            Console.WriteLine("== Lifecycle ==");
            Check(SimulationLogic.StageForAge(5) == "Child", "age 5 is Child");
            Check(SimulationLogic.StageForAge(30) == "Adult", "age 30 is Adult");
            Check(SimulationLogic.StageForAge(80) == "Elder", "age 80 is Elder");
            Check(SimulationLogic.StageForAge(13.9) == "Child", "age 13.9 is Child");
            Check(SimulationLogic.StageForAge(14) == "Adult", "age 14 is Adult");
            Check(SimulationLogic.StageForAge(65) == "Elder", "age 65 is Elder");

            // Starving citizens must die far more often than well-fed ones.
            var rng = new Random(12345);
            int starving = 0, healthy = 0;
            for (int i = 0; i < 20000; i++)
            {
                if (SimulationLogic.DeathCheck(30, 5, 60, 60, 60, rng) != null) starving++;
            }
            for (int i = 0; i < 20000; i++)
            {
                if (SimulationLogic.DeathCheck(30, 90, 90, 90, 90, rng) != null) healthy++;
            }
            Check(starving > healthy * 10, $"starvation mortality much higher ({starving} vs {healthy})");

            // Elderly die more often than young adults.
            int young = 0, old = 0;
            for (int i = 0; i < 20000; i++)
            {
                if (SimulationLogic.DeathCheck(30, 90, 90, 90, 90, rng) != null) young++;
            }
            for (int i = 0; i < 20000; i++)
            {
                if (SimulationLogic.DeathCheck(85, 90, 90, 90, 90, rng) != null) old++;
            }
            Check(old > young * 2, $"elder mortality higher ({old} vs {young})");

            Check(SimulationLogic.YearsPerTick > 0 && SimulationLogic.YearsPerTick < 0.01, "year per tick is plausible");
        }

        // ----------------------------------------------------------- day phase

        private static void DayPhaseTests()
        {
            Console.WriteLine("== Day phases ==");
            Check(SimulationLogic.PhaseForTimeOfDay(0.10) == SimulationLogic.DayPhase.Night, "02:24 is Night");
            Check(SimulationLogic.PhaseForTimeOfDay(0.30) == SimulationLogic.DayPhase.Dawn, "07:12 is Dawn");
            Check(SimulationLogic.PhaseForTimeOfDay(0.50) == SimulationLogic.DayPhase.Day, "12:00 is Day");
            Check(SimulationLogic.PhaseForTimeOfDay(0.80) == SimulationLogic.DayPhase.Dusk, "19:12 is Dusk");
            Check(SimulationLogic.PhaseForTimeOfDay(0.95) == SimulationLogic.DayPhase.Night, "22:48 is Night");
            Check(SimulationLogic.TimeOfDayFromDays(5.5) == 0.5, "fractional day is 0.5");
            Check(SimulationLogic.HourOfDay(0.5) == 12, "hour at noon is 12");
            Check(SimulationLogic.ClockLabel(0.5) == "12:00", "clock label at noon is 12:00");
            Check(SimulationLogic.HourOfDay(0.0) == 0, "hour at midnight is 0");
            Check(SimulationLogic.PhaseForTimeOfDay(0.25) == SimulationLogic.DayPhase.Dawn, "06:00 exactly is Dawn");
            Check(SimulationLogic.PhaseForTimeOfDay(1.0 / 3.0) == SimulationLogic.DayPhase.Day, "08:00 exactly is Day");
            Check(SimulationLogic.PhaseForTimeOfDay(0.75) == SimulationLogic.DayPhase.Dusk, "18:00 exactly is Dusk");
            Check(SimulationLogic.PhaseForTimeOfDay(0.875) == SimulationLogic.DayPhase.Night, "21:00 exactly is Night");
        }

        // ----------------------------------------------------------- inventory

        private static void InventoryTests()
        {
            Console.WriteLine("== Inventory ==");
            var inv = new Dictionary<string, int>();
            SimulationLogic.InventoryAdd(inv, "CookedBeef", 3);
            SimulationLogic.InventoryAdd(inv, "CookedBeef", 2);
            Check(SimulationLogic.InventoryCount(inv, "CookedBeef") == 5, "add stacks same item (got " + SimulationLogic.InventoryCount(inv, "CookedBeef") + ")");
            Check(SimulationLogic.InventoryTotal(inv) == 5, "total count is 5");

            int removed = SimulationLogic.InventoryRemove(inv, "CookedBeef", 2);
            Check(removed == 2, "remove 2 returns 2");
            Check(SimulationLogic.InventoryCount(inv, "CookedBeef") == 3, "remaining count is 3");

            int over = SimulationLogic.InventoryRemove(inv, "CookedBeef", 99);
            Check(over == 3, "cannot remove more than owned (removed " + over + ")");
            Check(!inv.ContainsKey("CookedBeef"), "empty slot removed from ledger");
            Check(SimulationLogic.InventoryRemove(inv, "CookedBeef", 5) == 0, "removing from empty slot returns 0");
            Check(SimulationLogic.InventoryRemove(inv, "CookedBeef", -1) == 0, "negative remove request returns 0");
            Check(SimulationLogic.InventoryAdd(inv, "IronBar", 0) == 0, "zero add keeps count");

            var food = new Dictionary<string, int> { ["CookedBeef"] = 4, ["Stone"] = 10, ["BakedBread"] = 2 };
            int consumed = SimulationLogic.ConsumeFood(food, 3);
            Check(consumed == 3, "consume food satisfies need (ate " + consumed + ")");
            Check(SimulationLogic.InventoryCount(food, "CookedBeef") == 1, "food consumed from inventory");
            Check(SimulationLogic.InventoryCount(food, "Stone") == 10, "non-food untouched");

            var allFood = new Dictionary<string, int> { ["CookedBeef"] = 4, ["BakedBread"] = 2 };
            int maxEat = SimulationLogic.ConsumeFood(allFood, 999);
            Check(maxEat == 6, "can eat all food if starving (ate " + maxEat + ")");
            Check(SimulationLogic.ConsumeFood(null, 5) == 0, "consume with null inventory returns 0");
            Check(SimulationLogic.ConsumeFood(new Dictionary<string, int> { ["CookedBeef"] = 4 }, 0) == 0, "consume with zero need returns 0");

            Check(SimulationLogic.IsFoodItem("CookedBeef"), "CookedBeef is food");
            Check(!SimulationLogic.IsFoodItem("IronBar"), "IronBar is not food");
            Check(!SimulationLogic.IsFoodItem(null), "null is not food");
        }

        // -------------------------------------------------------------- voting

        private static void VotingTests()
        {
            Console.WriteLine("== Voting ==");
            var votes = new List<KeyValuePair<string, int>>
            {
                new KeyValuePair<string, int>("Growth", 3),
                new KeyValuePair<string, int>("Welfare", 5),
                new KeyValuePair<string, int>("Trade", 2),
            };
            Check(SimulationLogic.WinningChoice(votes) == "Welfare", "majority choice wins");
            Check(SimulationLogic.WinningChoice(null) == null, "no votes -> no winner");
            Check(SimulationLogic.WinningChoice(new List<KeyValuePair<string, int>>()) == null, "empty votes -> no winner");

            Check(SimulationLogic.VotePreferenceForProfession("Laborer") == "Welfare", "laborer votes welfare");
            Check(SimulationLogic.VotePreferenceForProfession("Blacksmith") == "Industry", "blacksmith votes industry");
            Check(SimulationLogic.VotePreferenceForProfession("Farmer") == "Agriculture", "farmer votes agriculture");
            Check(SimulationLogic.VotePreferenceForProfession("Scientist") == "Research", "scientist votes research");
            Check(SimulationLogic.VotePreferenceForProfession("Merchant") == "Trade", "merchant votes trade");
            Check(SimulationLogic.VotePreferenceForProfession("Carpenter") == "Housing", "carpenter votes housing");
            Check(SimulationLogic.VotePreferenceForProfession("Miner") == "Growth", "miner votes growth");
            Check(SimulationLogic.VotePreferenceForProfession(null) == "Growth", "null profession votes growth");
            Check(SimulationLogic.VotePreferenceForProfession("UnknownJob") == "Growth", "unknown job votes growth");
        }

        // ------------------------------------------------------- agent pool

        private static void AgentPoolTests()
        {
            Console.WriteLine("== Agent pool cap ==");
            var pool = new AgentPoolManager { MaxActiveNpcs = 3 };
            Check(pool.CanSpawn("A") && pool.CanSpawn("B") && pool.CanSpawn("C"), "first three citizens fit under cap");
            Check(!pool.CanSpawn("D"), "fourth citizen blocked by cap");
            Check(pool.ActiveNpcCount == 3, "three NPC slots active (got " + pool.ActiveNpcCount + ")");
            Check(pool.SpawnBlockedByCap == 1, "one spawn blocked by cap");
            pool.Release("B");
            Check(pool.CanSpawn("D"), "released slot can be reused");
            Check(pool.ActiveNpcCount == 3, "reuse keeps pool at cap");

            pool.NoteMove(true);
            pool.NoteMove(true);
            pool.NoteMove(false);
            Check(Math.Abs(pool.PhysicalPathfindingRatio() - 2.0 / 3.0) < 1e-9, "physical pathfinding ratio is 2/3");
            pool.ResetStats();
            Check(pool.PhysicalPathfindingRatio() == 0.0, "reset stats clears counters");
        }

        // --------------------------------------------------------- commerce

        private static void CommerceTests()
        {
            Console.WriteLine("== Commerce (comparative advantage & nutrition) ==");
            var cfg = new ModConfig { CommerceEnabled = true, TradeMargin = 0.15, RawMaterialCost = 5.0, FoodSpendFactor = 0.6 };
            var market = new MarketSnapshot { StoreCount = 3, PricedOfferCount = 3, AvgPrice = 1.0, MinPrice = 1.0, FoodOfferCount = 2 };

            var trader = new Citizen { Name = "Trader", Profession = "Farmer", Trait = "Balanced", LedgerBalance = 500, Calories = 75 };
            bool traded = AgentCommerce.TickCommerce(trader, cfg, market);
            Check(traded, "farmer buys cheap materials instead of producing");
            Check(SimulationLogic.InventoryCount(trader.Inventory, "Iron Bar") == 2, "bought 2 material units (got " + SimulationLogic.InventoryCount(trader.Inventory, "Iron Bar") + ")");
            Check(trader.LedgerBalance == 498, "material purchase deducted from ledger (got " + trader.LedgerBalance + ")");

            var hungry = new Citizen { Name = "Hungry", Profession = "Laborer", Trait = "Balanced", LedgerBalance = 100, Calories = 30 };
            var foodMarket = new MarketSnapshot { StoreCount = 1, PricedOfferCount = 1, AvgPrice = 20.0, MinPrice = 4.0, FoodOfferCount = 3 };
            bool traded2 = AgentCommerce.TickCommerce(hungry, cfg, foodMarket);
            Check(traded2, "hungry citizen executes at least one buy");
            Check(SimulationLogic.InventoryCount(hungry.Inventory, "Cooked Meal") == 1, "hungry citizen bought one meal (got " + SimulationLogic.InventoryCount(hungry.Inventory, "Cooked Meal") + ")");
            Check(Math.Abs(hungry.Calories - 42.0) < 1e-9, "calories raised by meal value (got " + hungry.Calories + ")");
            Check(hungry.LedgerBalance < 100, "meal purchase spent currency");

            var poor = new Citizen { Name = "Poor", Profession = "Laborer", Trait = "Balanced", LedgerBalance = 5, Calories = 30 };
            bool traded3 = AgentCommerce.TickCommerce(poor, cfg, foodMarket);
            Check(SimulationLogic.InventoryCount(poor.Inventory, "Cooked Meal") == 0, "poor citizen cannot afford meal (got " + SimulationLogic.InventoryCount(poor.Inventory, "Cooked Meal") + ")");

            var off = new ModConfig { CommerceEnabled = false };
            var before = new Citizen { Name = "Off", Profession = "Farmer", LedgerBalance = 500, Calories = 30 };
            bool tradedOff = AgentCommerce.TickCommerce(before, off, market);
            Check(!tradedOff && before.LedgerBalance == 500, "commerce disabled does nothing");
            Check(SimulationLogic.ShouldBuyInsteadOfProduce(10, 1, 0.15), "high self cost triggers buying");
            Check(!SimulationLogic.ShouldBuyInsteadOfProduce(1, 10, 0.15), "low self cost keeps producing");
            Check(Math.Abs(SimulationLogic.CaloricValuePerCurrency(12, 4) - 3.0) < 1e-9, "calories per currency computed");
        }

        // --------------------------------------------------------- work party

        private static void WorkPartyTests()
        {
            Console.WriteLine("== Work parties ==");
            var wp = new WorkPartySystem();
            var party = wp.CreateParty("Bridge Crew", "Construction", 100, 50);
            Check(wp.Find("Bridge Crew") != null, "party findable by name");

            var alice = new Citizen { Name = "Alice", Profession = "Carpenter", LedgerBalance = 200 };
            var bob = new Citizen { Name = "Bob", Profession = "Laborer", LedgerBalance = 200 };
            Check(wp.Join(party, alice) && wp.Join(party, bob), "two citizens join the party");
            Check(!wp.Join(party, alice), "cannot join twice");

            var pay1 = wp.Contribute(alice, 20);
            var pay2 = wp.Contribute(bob, 30); // 20+30 = 50 = target -> milestone completes here
            var pay3 = wp.Contribute(alice, 20); // new milestone still in progress
            Check(pay1.Count == 0, "milestone not paid before target reached");
            Check(pay2.Count == 2, "milestone pays all contributors (got " + pay2.Count + ")");
            Check(Math.Abs(pay2["Alice"] - 40.0) < 1e-9, "Alice receives her 40/50 share (got " + pay2["Alice"] + ")");
            Check(Math.Abs(pay2["Bob"] - 60.0) < 1e-9, "Bob receives his 30/50 share (got " + pay2["Bob"] + ")");
            Check(pay3.Count == 0, "second milestone not paid before target");
            Check(wp.MilestonesCompleted == 1, "milestone counter incremented");
            Check(Math.Abs(wp.TotalPoolPaid - 100.0) < 1e-9, "total pool paid tracks payments");

            var lonely = new Citizen { Name = "Lonely", Profession = "Miner", LedgerBalance = 100 };
            wp.Join(party, lonely);
            wp.Withdraw(lonely);
            Check(lonely.WorkPartyId == null && !party.Members.ContainsKey("Lonely"), "withdraw removes member and clears id");
            Check(SimulationLogic.MilestoneShare(100, 10, 0) == 0, "zero total contribution pays nothing");
            Check(Math.Abs(SimulationLogic.HourlyWageFor(200, 8) - 25.0) < 1e-9, "hourly wage computed");
            Check(SimulationLogic.PartyTaskFor("Blacksmith") == "Crafting", "blacksmith party task is crafting");
        }

        // ------------------------------------------------------ construction

        private static void ConstructionTests()
        {
            Console.WriteLine("== Construction ==");
            var builder = new Citizen { Name = "Builder", Profession = "Carpenter", Trait = "Balanced", LedgerBalance = 1000 };
            var planner = new ConstructionPlanner();
            var cfg = new ModConfig { ConstructionEnabled = true, MaterialUnitPrice = 4.0, ConstructionWorkFactor = 1.0 };

            Check(planner.EnsurePlan(builder, "Temperate"), "carpenter gets a plan");
            Check(builder.BlueprintId == "House", "carpenter blueprint is House (got " + builder.BlueprintId + ")");
            Check(!planner.EnsurePlan(builder, "Temperate"), "plan not replaced while active");

            planner.TickConstruction(builder, 10, 4.0, cfg);
            Check(builder.ConstructionMaterialsSecured, "materials secured when affordable");
            Check(builder.LedgerBalance < 1000, "materials paid from ledger");
            Check(builder.ConstructionProgress > 0, "construction progresses after materials secured");

            var saver = new Citizen { Name = "Saver", Profession = "Mason", LedgerBalance = 10 };
            planner.EnsurePlan(saver, "Temperate");
            planner.TickConstruction(saver, 50, 4.0, cfg);
            Check(!saver.ConstructionMaterialsSecured && saver.ConstructionProgress == 0, "saving up delays construction");

            for (int i = 0; i < 100 && builder.ConstructionProgress < 1.0; i++)
            {
                planner.TickConstruction(builder, 10, 4.0, cfg);
            }
            Check(builder.ConstructionProgress == 0, "construction progress resets after completion (got " + builder.ConstructionProgress + ")");
            Check(builder.HomeDeedName == "SimBuild Builder", "completed house sets home (got " + builder.HomeDeedName + ")");
            Check(builder.BlueprintId == null, "blueprint cleared after completion");
            Check(planner.ProjectsCompleted == 1, "project counter incremented");
            Check(SimulationLogic.BlueprintFor("Scientist", "Snow") == "Laboratory (Cold Frame)", "biome adapts blueprint");
            Check(SimulationLogic.BlueprintComplexity("Laboratory") == 28.0, "laboratory complexity is 28");
        }

        // ---------------------------------------------------------- research

        private static void ResearchTests()
        {
            Console.WriteLine("== Research & technology ==");
            var research = new ResearchContribution();
            var scholar = new Citizen { Name = "Doc", Profession = "Scientist", ResearchPoints = 0 };
            double pts = research.Contribute(scholar, 100, 1.0);
            Check(pts >= 100, "scholar contributes research (got " + pts + ")");
            Check(research.DiscoveriesUnlocked == 1, "first discovery unlocked (tier 0 cost 50)");
            Check(research.TierFor("Science") == 1, "science domain tier raised (got " + research.TierFor("Science") + ")");
            Check(Math.Abs(research.PointsFor("Science")) >= 50, "overflow points kept in domain");

            var worker = new Citizen { Name = "Worker", Profession = "Miner", ResearchPoints = 0 };
            double wpts = research.Contribute(worker, 10, 1.0);
            Check(Math.Abs(wpts - 10.0) < 1e-9, "non-scholar contributes base rate (got " + wpts + ")");
            Check(research.PointsFor("Mining") > 0, "mining domain receives points");
            Check(SimulationLogic.ResearchDomainFor("Farmer") == "Agriculture", "farmer researches agriculture");
            Check(SimulationLogic.SkillUnlockResearchCost(2) == 210.0, "tier 2 unlock cost is 210");
        }

        // ------------------------------------------------------ corporations

        private static void CorporationTests()
        {
            Console.WriteLine("== Corporations ==");
            var corps = new CorporationSystem();
            var corp = corps.Create("SteelWorks", "Alice", 1000);
            Check(corp.Owners["Alice"] == 100, "founder owns 100 shares");
            Check(corps.Find("SteelWorks") == corp, "corporation findable by name");

            Check(corps.IssueShares(corp, "Bob", 50, 10), "shares issued to investor");
            Check(Math.Abs(corp.Assets - 1500.0) < 1e-9, "issue proceeds add to assets (got " + corp.Assets + ")");

            corps.CastVote(corp, "Alice", "Growth");
            corps.CastVote(corp, "Bob", "Welfare");
            string winner = corps.ResolveVote(corp);
            Check(winner == "Growth", "equity-weighted vote: Alice wins (got " + winner + ")");
            Check(corp.Votes.Count == 0, "votes cleared after resolution");

            var emp = new Citizen { Name = "Emp", Profession = "Blacksmith", LedgerBalance = 0 };
            Check(corps.Hire(corp, emp, 1.0), "affordable wage hires worker");
            Check(emp.CorporationId == corp.Id, "employee linked to corporation");
            Check(!corps.Hire(corp, new Citizen { Name = "Expensive", Profession = "Laborer" }, 100.0), "unaffordable wage not hired");

            emp.CorporationShares = 50;
            double share = corps.TickCorporationWork(emp, 1000, 0.1);
            Check(share > 0, "worker receives dividend share (got " + share + ")");
            Check(emp.LedgerBalance > 0, "dividend credited to ledger");
            Check(corp.DividendPool > 0, "dividend pool accumulated");
            Check(SimulationLogic.ShareValue(1500, 0, 150) == 10.0, "share value from net assets");
        }

        // ----------------------------------------------------------- exchange

        private static void ExchangeTests()
        {
            Console.WriteLine("== Stock & futures exchange ==");
            var ex = new StockExchangeSystem();
            var corp = new Corporation { Id = "corp1", Name = "SteelWorks", Assets = 1500, Liabilities = 0 };
            corp.Owners["Alice"] = 100;
            corp.Owners["Bob"] = 50;
            var listed = ex.ListCompany(corp);
            Check(listed.LastPrice == 10.0, "listed company priced by assets (got " + listed.LastPrice + ")");

            var fut = ex.AddFuture("Iron Bars", 10);
            Check(fut.BasePrice == 10.0, "futures contract registered");
            ex.ObserveSupplyDemand("Iron Bars", 50, 200);
            ex.TickPrices();
            Check(fut.Supply == 50 && fut.Demand == 200, "scarcity feeds from observed supply/demand");
            Check(fut.LastPrice > 10.0, "scarcity pushes futures price up (got " + fut.LastPrice + ")");

            var spec = new Citizen { Name = "Spec", Profession = "Merchant", LedgerBalance = 500 };
            fut.LastPrice = 8.0; // cheap contract, expected base*1.2 = 12
            Check(ex.TryBuyFuture(spec, "Iron Bars", 0.1), "speculator buys underpriced futures");
            Check(spec.FuturesHeld["Iron Bars"] == 10, "futures position held (got " + spec.FuturesHeld["Iron Bars"] + ")");
            Check(ex.ContractsBought == 1, "contract counter incremented");
            Check(SimulationLogic.ScarcityFromSupplyDemand(0, 5) == 1.0, "no supply -> max scarcity");
            Check(SimulationLogic.FutureContractPrice(10, 0, 0) == 10.0, "neutral contract price is base");
        }

        // ---------------------------------------------------------- currency

        private static void CurrencyTests()
        {
            Console.WriteLine("== Settlement currencies & forex ==");
            var fx = new CurrencySystem();
            var alpha = fx.Mint("Alpha Settlement", "ALD", 100);
            var beta = fx.Mint("Beta Settlement", "BLD", 100);
            Check(alpha.Symbol == "ALD", "currency minted with symbol");
            Check(fx.Find("Alpha Settlement") == alpha, "currency findable by settlement");

            double rate = fx.ForexRate(alpha, beta);
            Check(Math.Abs(rate - 1.2) < 1e-9, "equal economies give rate 1.2 (got " + rate + ")");
            Check(fx.ForexRate(alpha, alpha) == 1.0, "same settlement rate is 1.0");

            var trader = new Citizen { Name = "FxTrader", Profession = "Merchant" };
            trader.CurrencyHoldings["Alpha Settlement"] = 50;
            Check(fx.Convert(trader, "Alpha Settlement", "Beta Settlement", 10), "conversion succeeds");
            Check(Math.Abs(trader.CurrencyHoldings["Alpha Settlement"] - 40.0) < 1e-9, "source balance reduced (got " + trader.CurrencyHoldings["Alpha Settlement"] + ")");
            Check(Math.Abs(trader.CurrencyHoldings["Beta Settlement"] - 12.0) < 1e-9, "target balance converted at rate (got " + trader.CurrencyHoldings["Beta Settlement"] + ")");
            Check(fx.Conversions == 1, "conversion counter incremented");

            double supplyBefore = alpha.Supply;
            double gdpBefore = alpha.Gdp;
            fx.TickCurrencies();
            Check(alpha.Supply > supplyBefore, "money supply grows with population/reserves");
            Check(alpha.Gdp > gdpBefore, "GDP grows after tick (got " + alpha.Gdp + ")");
            Check(!fx.Convert(trader, "Alpha Settlement", "Beta Settlement", 999), "cannot convert more than held");
            Check(SimulationLogic.MintSupplyGrowth(10, 100) > 0, "mint supply growth positive");
        }

        // ------------------------------------------------------- environment

        private static void EnvironmentTests()
        {
            Console.WriteLine("== Ecology & crisis ==");
            var env = new EnvironmentTracker { Pollution = 59, CO2 = 10, SoilDegradation = 10 };
            var cfg = new ModConfig { EnvironmentEnabled = true, EnvironmentThreshold = 60, EnvironmentalCleanup = 0.5 };
            bool flipped = env.TickEnvironment(cfg, 100);
            Check(flipped && env.CrisisActive, "crisis flips on past threshold");
            Check(!env.TickEnvironment(cfg, 100) && env.CrisisActive, "crisis stays on after flipping");
            Check(env.Pollution >= 60.0, "pollution accumulates with industry (got " + env.Pollution + ")");

            bool charged = env.ContributeToMeteorDefence(50, 50, 100);
            Check(charged && env.MeteorProgress >= 1.0, "meteor defence completes with enough power/resources");

            env.MeteorProgress = 0.5;
            env.ImpactDay = 10;
            Check(env.CheckImpact(12) && env.MeteorImpacted, "undefended meteor flips extreme survival mode");
            Check(env.SurvivalPriority(new Citizen { Name = "Miner", Profession = "Miner" }) == "DebrisClearance", "survival priority by profession");
            Check(env.ActiveRecipeFor(new Citizen { Name = "Blacksmith", Profession = "Blacksmith" }) == "ElectricSmelterRecipe", "crisis switches to cleaner recipe");

            env.DoCleanup(10);
            Check(env.CleanupActions == 1, "cleanup action recorded");
            Check(env.Pollution < 60.0, "cleanup reduces pollution (got " + env.Pollution + ")");
            Check(SimulationLogic.EnvironmentalCrisis(70, 10, 10, 60), "pollution alone triggers crisis");
            Check(SimulationLogic.ExtremeSurvivalMode(0.5, 10, 12), "survival mode when impact passed undefended");
        }

        // ------------------------------------------------------------ politics

        private static void PoliticsTests()
        {
            Console.WriteLine("== Politics ==");
            var pol = new PoliticsEngine();
            var cfg = new ModConfig { EnvironmentThreshold = 60 };
            var env = new EnvironmentTracker { Pollution = 40 };
            var citizen = new Citizen { Name = "Green", Profession = "Farmer", EnvironmentConcern = 0.5 };
            Check(pol.DecideVote(citizen, env, cfg) == "EcoLaw", "concerned citizen votes eco law");
            Check(pol.EcoLawVotes == 1, "eco law vote counter incremented");

            var other = new Citizen { Name = "Plain", Profession = "Farmer", EnvironmentConcern = 0.0 };
            Check(pol.DecideVote(other, env, cfg) == "Agriculture", "low concern keeps profession vote");

            double price = pol.DecidePrice(100, 10, true);
            Check(Math.Abs(price - 100.395) < 1e-6, "eco majority dampens prices (got " + price + ")");
            Check(pol.ShouldExpand(0.05, 0.02, 600), "healthy settlement expands");
            Check(!pol.ShouldExpand(0.0, 0.02, 600), "stagnant economy does not expand");
            Check(pol.ExpansionDecisions == 1, "expansion decision counter incremented");
        }

        // ----------------------------------------------------------- dialogue

        private static void DialogueTests()
        {
            Console.WriteLine("== Dialogue ==");
            var tree = new DialogueTree();
            var c = new Citizen { Name = "Teller", Profession = "Farmer" };
            string line = tree.Tell(c, "trade_completed", "5 units");
            Check(line != null && line.Contains("Sold 5 units"), "trade dialogue line emitted (got " + line + ")");
            Check(tree.Tell(null, "trade_completed", "x") == null, "null citizen emits no line");
            Check(tree.Tell(c, null, "x") == null, "null event emits no line");
            string recent = DialogueTree.RecentLines(c);
            Check(recent.Contains("Teller"), "recent lines contain citizen lines");
            Check(DialogueTree.RecentLines(new Citizen { Name = "Empty" }) == "(no dialogue yet)", "empty log placeholder");
            Check(SimulationLogic.DialogueForEvent("meteor_warning", "A", "meteor") != null, "meteor dialogue generated");
        }

        // ----------------------------------------------------- simulation state

        private static void SimulationStateTests()
        {
            Console.WriteLine("== Simulation state persistence ==");
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecosim_test_state.json");
            try
            {
                var state = new SimulationState
                {
                    Pollution = 12.34,
                    CO2 = 5.67,
                    SoilDegradation = 8.9,
                    MeteorProgress = 0.75,
                    ImpactDay = 42,
                    MeteorImpacted = true,
                    MaxActiveNpcs = 17,
                    PhysicalMoveTicks = 100,
                    LogicalMoveTicks = 50
                };
                state.Save(path);
                var loaded = SimulationState.Load(path);
                Check(Math.Abs(loaded.Pollution - 12.34) < 0.01, "pollution persisted (got " + loaded.Pollution + ")");
                Check(Math.Abs(loaded.CO2 - 5.67) < 0.01, "CO2 persisted (got " + loaded.CO2 + ")");
                Check(Math.Abs(loaded.SoilDegradation - 8.9) < 0.01, "soil persisted (got " + loaded.SoilDegradation + ")");
                Check(Math.Abs(loaded.MeteorProgress - 0.75) < 0.001, "meteor progress persisted (got " + loaded.MeteorProgress + ")");
                Check(Math.Abs(loaded.ImpactDay - 42.0) < 0.01, "impact day persisted (got " + loaded.ImpactDay + ")");
                Check(loaded.MeteorImpacted, "impacted flag persisted");
                Check(loaded.MaxActiveNpcs == 17, "max npcs persisted (got " + loaded.MaxActiveNpcs + ")");
                Check(loaded.PhysicalMoveTicks == 100 && loaded.LogicalMoveTicks == 50, "move counters persisted");
            }
            finally
            {
                try { System.IO.File.Delete(path); } catch { }
            }
        }
    }
}
