using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eco.Gameplay.Players;
using Eco.Gameplay.Settlements;
using Eco.Shared.Services;
using Eco.Shared.Time;

namespace EcoSimCitizens
{
    /// <summary>
    /// The heart of the mod: advances every citizen one tick. The simulation is a
    /// closed loop driven by the live Eco economy/civics - prices, food availability,
    /// deeds and settlements all feed back into citizen wellbeing and behaviour.
    ///
    /// v0.3 adds the "real player" features: real production &amp; inventory, skill
    /// growth, workplace &amp; commuting, settlement membership, political/legal
    /// participation, day/night routine, life cycle and a social trail.
    ///
    /// v0.4 (PRD modules 1-7) adds the autonomous-agent layer: work parties and
    /// comparative-advantage commerce, autonomous construction and research,
    /// corporations / stock exchange / settlement currencies, data-driven politics,
    /// ecology &amp; meteor crisis management, contextual dialogue and admin telemetry.
    /// </summary>
    public class SimulationCore
    {
        private readonly MarketScanner _market = new MarketScanner();
        private readonly CivicObserver _civic = new CivicObserver();
        private readonly Random _rng = new Random();

        public MarketSnapshot LastMarket { get; private set; } = new MarketSnapshot();
        public CivicSnapshot LastCivic { get; private set; } = new CivicSnapshot();

        // --- v0.4 PRD systems (world-level state, restored across restarts) ---
        public AgentPoolManager Pool { get; } = new AgentPoolManager();
        public WorkPartySystem WorkParties { get; } = new WorkPartySystem();
        public ConstructionPlanner Construction { get; } = new ConstructionPlanner();
        public ResearchContribution Research { get; } = new ResearchContribution();
        public CorporationSystem Corporations { get; } = new CorporationSystem();
        public StockExchangeSystem Exchange { get; } = new StockExchangeSystem();
        public CurrencySystem Currencies { get; } = new CurrencySystem();
        public EnvironmentTracker Environment { get; } = new EnvironmentTracker();
        public PoliticsEngine Politics { get; } = new PoliticsEngine();
        public DialogueTree Dialogue { get; } = new DialogueTree();
        public TelemetryService Telemetry { get; } = new TelemetryService();
        public SimulationState WorldState { get; private set; } = new SimulationState();
        public string StatePath { get; private set; }

        public SimulationCore()
        {
            try
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    StatePath = Path.Combine(dir, "EcoSimCitizens", "sim_state.json");
                    WorldState = SimulationState.Load(StatePath);
                    Environment.Pollution = WorldState.Pollution;
                    Environment.CO2 = WorldState.CO2;
                    Environment.SoilDegradation = WorldState.SoilDegradation;
                    Environment.MeteorProgress = WorldState.MeteorProgress;
                    Environment.ImpactDay = WorldState.ImpactDay;
                    Environment.MeteorImpacted = WorldState.MeteorImpacted;
                    Pool.MaxActiveNpcs = Math.Max(1, WorldState.MaxActiveNpcs);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: world state load failed. " + ex.Message);
            }
        }

        /// <summary>Advances the simulation one tick. Returns citizens who died this tick.</summary>
        public List<Citizen> Tick(CitizensDatabase db, ModConfig cfg)
        {
            var market = _market.Scan(db);
            var civic = _civic.Observe();
            var citizens = db.Snapshot();
            var died = new List<Citizen>();

            CitizenSocial.MaxTrail = cfg.SocialTrailMax;
            Pool.MaxActiveNpcs = Math.Max(1, cfg.PoolLimit);

            double industryThisTick = 0;

            foreach (var c in citizens)
            {
                try
                {
                    industryThisTick += TickCitizen(c, db, cfg, market, civic);
                    if (!c.IsAlive) died.Add(c);
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"Tick failed for citizen '{c.Name}': {ex.Message}");
                }
            }

            // --- v0.5 autonomous shop economy: restock, reprice, upgrade, open/close ---
            if (cfg.SimShopsEnabled)
            {
                SimStoreManager.TickShops(db, cfg, market);
            }

            // --- v0.7 homestead/deed concept: group homeless citizens into
            // persisted homesteads (NPCs cannot hold real property; the groups are
            // the mod-internal deed/homestead view) ---
            if (cfg.HomesteadEnabled)
            {
                HomesteadSystem.TickHomesteads(db, cfg);
            }

            // --- world-level systems (after citizen work feeds industry) ---
            if (cfg.EnvironmentEnabled)
            {
                bool flipped = Environment.TickEnvironment(cfg, industryThisTick);
                if (flipped && db.Count > 0)
                {
                    PluginLog.Info("EcoSimCitizens: environmental crisis is now active - citizens switch to cleaner recipes.");
                    if (cfg.DialogueEnabled)
                        Dialogue.Tell(citizens.FirstOrDefault(), "crisis_active", "settlement");
                }
                if (cfg.MeteorEnabled)
                {
                    if (Environment.ImpactDay <= 0) Environment.ImpactDay = TimeUtil.Days + cfg.MeteorImpactDay;
                    Environment.CheckImpact(TimeUtil.Days);
                }
            }

            if (cfg.ExchangeEnabled && Exchange.Futures.Count == 0)
            {
                double anchor = market.HasMarket && market.AvgPrice > 0 ? market.AvgPrice : cfg.RawMaterialCost;
                Exchange.AddFuture("Iron Bar", Math.Max(1.0, anchor));
                Exchange.AddFuture("Oil", 8.0);
            }

            if (cfg.CurrencyEnabled)
            {
                foreach (var c in citizens)
                {
                    if (string.IsNullOrEmpty(c.SettlementName)) continue;
                    if (Currencies.Find(c.SettlementName) == null)
                        Currencies.Mint(c.SettlementName, "", 50.0);
                }
            }

            Exchange.TickPrices();
            Currencies.TickCurrencies();

            // --- retire the dead: despawn their NPC and remove them from the database ---
            foreach (var dead in died)
            {
                try
                {
                    NpcManager.DestroyFor(dead);
                    db.Remove(dead.Name);
                    Pool.Release(dead.Name);
                    PluginLog.Info($"{dead.Name} died of {dead.DeathCause} on day {dead.DeathDay:0.0} and was retired from the simulation.");
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"Retire failed for {dead.Name}: {ex.Message}");
                }
            }
            if (died.Count > 0) db.Save();
            db.SaveThrottled(cfg.SaveThrottleSeconds);

            LastMarket = market;
            LastCivic = civic;

            Telemetry.ObserveTick(db, this, market, Pool);
            if (Telemetry.TotalTicks % 10 == 0) SaveWorldState();

            return died;
        }

        private void SaveWorldState()
        {
            try
            {
                if (string.IsNullOrEmpty(StatePath)) return;
                WorldState.Pollution = Environment.Pollution;
                WorldState.CO2 = Environment.CO2;
                WorldState.SoilDegradation = Environment.SoilDegradation;
                WorldState.MeteorProgress = Environment.MeteorProgress;
                WorldState.ImpactDay = Environment.ImpactDay;
                WorldState.MeteorImpacted = Environment.MeteorImpacted;
                WorldState.MaxActiveNpcs = Pool.MaxActiveNpcs;
                WorldState.PhysicalMoveTicks = Pool.PhysicalMoveTicks;
                WorldState.LogicalMoveTicks = Pool.LogicalMoveTicks;
                WorldState.Save(StatePath);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: world state save failed. " + ex.Message);
            }
        }

        /// <summary>Advances one citizen; returns the work output produced this tick (feeds industry).</summary>
        private double TickCitizen(Citizen c, CitizensDatabase db, ModConfig cfg, MarketSnapshot market, CivicSnapshot civic)
        {
            double outputThisTick = 0;

            c.AgeInTicks++;
            if (c.SocialTrail == null) c.SocialTrail = new List<string>();
            CitizenSocial.Trim(c);

            // --- day/night phase from the real Eco clock ---
            double timeOfDay = SimulationLogic.TimeOfDayFromDays(TimeUtil.Days);
            var phase = SimulationLogic.PhaseForTimeOfDay(timeOfDay);
            c.CurrentPhase = phase.ToString();
            c.IsSleeping = phase == SimulationLogic.DayPhase.Night;

            // --- needs decay ---
            c.Calories = SimMath.Clamp(c.Calories - cfg.CalorieDecayPerTick, 0, 100);
            c.Comfort  = SimMath.Clamp(c.Comfort  - cfg.ComfortDecayPerTick, 0, 100);
            c.Social   = SimMath.Clamp(c.Social   - cfg.SocialDecayPerTick, 0, 100);

            // --- workplace & commute (real NPC moves in the world) ---
            var npc = NpcManager.Find(c.Name);
            if (npc != null)
            {
                Pool.NoteMove(true);
                if (!c.HasWorkplace) CitizenCommute.EnsureWorkplace(c, npc);
                npc.ComputeGoal(c, phase, market.SimStorePositions);
                npc.BehaviorTick();
                if (!c.HasBeenFunded)
                {
                    CitizenEconomy.GrantStartFunds(c.Name, (float)cfg.StartingBalance);
                    c.HasBeenFunded = true;
                }
            }
            else
            {
                Pool.NoteMove(false);
            }

            // --- work: only during the working day; skills multiply output ---
            double workFraction = phase switch
            {
                SimulationLogic.DayPhase.Day => 1.0,
                SimulationLogic.DayPhase.Dawn or SimulationLogic.DayPhase.Dusk => 0.5,
                _ => 0.0
            };

            if (workFraction > 0)
            {
                var prof = Professions.Get(c.Profession);
                double healthFactor = 0.4 + c.Calories / 100.0;
                double productivity = CitizenSkills.EffectiveProductivity(c) * healthFactor;
                double output = prof.Productivity * productivity * workFraction;
                outputThisTick = output;

                c.WorkUnitsProduced += (long)(output * 100);
                c.TicksWorked++;
                c.LedgerBalance += cfg.WagePerTick * prof.EarnMultiplier * Traits.WorkMultiplier(c.Trait) * workFraction;

                CitizenSkills.TickSkill(c, output * 10.0, cfg.SkillLearnRate);

                if (cfg.ProductionEnabled)
                {
                    CitizenProduction.Produce(c, cfg.ProductionUnitsPerTick, productivity * workFraction, npc);
                }

                // --- v0.4: work party contribution (contracts & shared milestones) ---
                if (cfg.PartyEnabled)
                {
                    if (string.IsNullOrEmpty(c.WorkPartyId))
                    {
                        string task = SimulationLogic.PartyTaskFor(c.Profession);
                        var party = WorkParties.Parties.FirstOrDefault(p =>
                            string.Equals(p.Task, task, StringComparison.OrdinalIgnoreCase));
                        if (party == null)
                            party = WorkParties.CreateParty(c.Profession + " Crew", task, 20.0, cfg.PartyTargetProgress);
                        WorkParties.Join(party, c);
                    }
                    var payments = WorkParties.Contribute(c, output);
                    if (payments.TryGetValue(c.Name, out double pay) && pay > 0)
                    {
                        c.LedgerBalance += pay;
                        if (cfg.DialogueEnabled)
                            Dialogue.Tell(c, "milestone_paid", $"{pay:0.0}");
                    }
                }

                // --- v0.4: autonomous construction ---
                if (cfg.ConstructionEnabled)
                {
                    Construction.EnsurePlan(c, string.IsNullOrEmpty(c.SettlementName) ? "Forest" : c.SettlementName);
                    Construction.TickConstruction(c, output, market.HasMarket ? market.AvgPrice : 0, cfg);
                }

                // --- v0.4: research contribution ---
                if (cfg.ResearchMultiplier > 0)
                {
                    Research.Contribute(c, productivity, cfg.ResearchMultiplier);
                }

                // --- v0.4: corporation work & dividends ---
                if (cfg.CorporationEnabled)
                {
                    if (string.IsNullOrEmpty(c.CorporationId))
                    {
                        var corp = Corporations.Corporations.FirstOrDefault(x =>
                            string.Equals(x.Name, c.Profession + " Guild", StringComparison.OrdinalIgnoreCase));
                        if (corp == null)
                            corp = Corporations.Create(c.Profession + " Guild", c.Name, 100.0);
                        corp.Owners[c.Name] = corp.Owners.TryGetValue(c.Name, out double cur) ? cur + 1 : 1;
                        c.CorporationId = corp.Id;
                        c.CorporationShares = 1;
                    }
                    double dividend = Corporations.TickCorporationWork(c, output, cfg.DividendRate);
                    if (dividend > 0 && cfg.DialogueEnabled)
                        Dialogue.Tell(c, "company_dividend", c.CorporationId);
                }
            }

            // --- sell produced goods to the citizen's own simulated store ---
            if (cfg.SellEnabled)
            {
                CitizenProduction.SellProduced(c, db);
            }

            // --- v0.4: comparative-advantage commerce (materials & calorie-value food) ---
            if (cfg.CommerceEnabled)
            {
                AgentCommerce.TickCommerce(c, cfg, market);
            }

            // --- consume: eat from own inventory first, then from simulated shops ---
            if (c.Calories < 65)
            {
                int need = (int)Math.Ceiling((65 - c.Calories) / cfg.CalorieGainFromFood);
                int ate = SimulationLogic.ConsumeFood(c.Inventory, need);
                if (ate > 0)
                {
                    c.Calories = SimMath.Clamp(c.Calories + ate * cfg.CalorieGainFromFood, 0, 100);
                }
                else if (SimStoreManager.TryBuyFood(c, db, cfg, out float _))
                {
                    c.Calories = SimMath.Clamp(c.Calories + cfg.CalorieGainFromFood, 0, 100);
                }
                else if (market.HasMarket && market.FoodOfferCount > 0 && market.MinPrice > 0)
                {
                    // last-resort ledger fallback: mirrors the cheapest real offer as a
                    // mod-internal book entry - never touches a real StoreComponent.
                    double cost = market.MinPrice * cfg.FoodSpendFactor;
                    if (c.LedgerBalance >= cost)
                    {
                        c.Calories = SimMath.Clamp(c.Calories + cfg.CalorieGainFromFood, 0, 100);
                        c.LedgerBalance -= cost;
                    }
                }
            }

            // --- v0.5 autonomous shop ownership: open / upgrade when affordable ---
            if (cfg.SimShopsEnabled && c.IsAlive)
            {
                SimStoreManager.EnsureShopFor(c, db, cfg);
            }

            // --- housing: deed-derived comfort ---
            double targetComfort = HousingEvaluator.EvaluateComfort(c);
            c.Comfort = SimMath.Clamp(c.Comfort * 0.9 + targetComfort * 0.1, 0, 100);

            // --- settlement membership & civics ---
            if (civic.HasCivics)
            {
                CitizenCivics.EnsureSettlement(c, civic);
                if (cfg.VotingEnabled && c.IsAlive)
                {
                    CitizenCivics.TickVoting(c, civic, cfg.VotingIntervalDays);
                }
            }

            // --- v0.4: data-driven politics (ecology votes) ---
            if (cfg.PoliticsEnabled && cfg.EnvironmentEnabled && Environment.CrisisActive && c.AgeInTicks % 7 == 0)
            {
                c.EnvironmentConcern = SimMath.Clamp(
                    c.EnvironmentConcern + (Environment.Pollution > cfg.EnvironmentThreshold ? 0.1 : 0.02), 0, 1);
                c.LastVotedChoice = Politics.DecideVote(c, Environment, cfg);
                if (c.LastVotedChoice == "EcoLaw" && cfg.DialogueEnabled)
                    Dialogue.Tell(c, "pollution_complaint", $"{Environment.Pollution:0.0}");
            }

            // --- v0.4: ecology action & meteor defence ---
            if (cfg.EnvironmentEnabled)
            {
                if (Environment.CrisisActive && c.AgeInTicks % 3 == 0)
                {
                    double effort = cfg.EnvironmentalCleanup * (0.5 + c.EnvironmentConcern);
                    Environment.DoCleanup(effort);
                }
                if (cfg.MeteorEnabled && !Environment.MeteorImpacted && Environment.ImpactDay > 0 && c.AgeInTicks % 15 == 0)
                {
                    double power = Math.Max(0.1, outputThisTick * 0.5);
                    double resources = Math.Min(c.LedgerBalance, 5.0) * 0.01;
                    bool charged = Environment.ContributeToMeteorDefence(power, resources, cfg.MeteorDefenseNeeded);
                    c.MeteorContribution += power;
                    if (charged && cfg.DialogueEnabled)
                        Dialogue.Tell(c, "meteor_warning", "meteor laser");
                }
            }

            // --- v0.4: speculative futures trading ---
            if (cfg.ExchangeEnabled && c.AgeInTicks % 20 == 0 && c.LedgerBalance > 10)
            {
                Exchange.TryBuyFuture(c, "Iron Bar", cfg.FuturesRiskTolerance);
            }

            // --- v0.4: forex conversion ---
            if (cfg.CurrencyEnabled && c.AgeInTicks % 30 == 0 && Currencies.Currencies.Count >= 2)
            {
                var from = Currencies.Find(c.SettlementName ?? "");
                var to = Currencies.Currencies.FirstOrDefault(x => !string.Equals(x.SettlementName, c.SettlementName, StringComparison.OrdinalIgnoreCase));
                if (from != null && to != null && c.CurrencyHoldings.TryGetValue(from.SettlementName, out double have) && have > 10)
                {
                    if (Currencies.Convert(c, from.SettlementName, to.SettlementName, Math.Min(have * 0.1, 20)))
                    {
                        if (cfg.DialogueEnabled)
                            Dialogue.Tell(c, "forex_news", to.Symbol);
                    }
                }
            }

            // --- social: communities of citizens (and gregarious traits) help ---
            double socialGain = Math.Min(5.0, db.Count * 0.25) + Traits.SocialBonus(c.Trait);
            if (!string.IsNullOrEmpty(c.SettlementName)) socialGain += 0.5;
            c.Social = SimMath.Clamp(c.Social + socialGain, 0, 100);

            // --- happiness: weighted needs + trait bonus + settlement pride ---
            double settlementBonus = string.IsNullOrEmpty(c.SettlementName) ? 0 : 2;
            c.Happiness = SimMath.Clamp(
                c.Calories * 0.35 + c.Comfort * 0.30 + c.Social * 0.35 + Traits.HappinessBonus(c.Trait) + settlementBonus,
                0, 100);

            // --- life cycle ---
            if (cfg.LifecycleEnabled && c.IsAlive)
            {
                CitizenLifecycle.TickAge(c, new Random(Math.Abs(c.Name.GetHashCode()) ^ System.Environment.TickCount));
            }

            return outputThisTick;
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
            sb.AppendLine($"Population: {db.Count} citizens | Real residents: {v.RealPlayerCount} | World NPCs: {npcCount} | Day {TimeUtil.Days:0.0}");

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
                long totalItems = 0;
                long totalSold = 0;
                long totalBought = 0;
                int adults = 0, elders = 0, children = 0, asleep = 0, inSettlements = 0, voters = 0;
                double totalSkills = 0;
                double totalResearch = 0;
                foreach (var c in citizens)
                {
                    totalLedger += c.LedgerBalance;
                    totalWork += c.WorkUnitsProduced;
                    totalItems += SimulationLogic.InventoryTotal(c.Inventory);
                    totalSold += c.ItemsSold;
                    totalBought += c.TradeBuyVolume;
                    totalSkills += CitizenSkills.GetLevel(c);
                    totalResearch += c.ResearchPoints;
                    switch (c.LifeStage)
                    {
                        case "Child": children++; break;
                        case "Elder": elders++; break;
                        default: adults++; break;
                    }
                    if (c.IsSleeping) asleep++;
                    if (!string.IsNullOrEmpty(c.SettlementName)) inSettlements++;
                    if (c.LastVoteDay > 0) voters++;
                }
                sb.AppendLine($"Ledger total: {totalLedger:0.00} | Work units: {totalWork:N0} | Avg skill: {totalSkills / db.Count:0.0}");
                sb.AppendLine($"Production: {totalItems:N0} items held | {totalSold:N0} sold | {totalBought:N0} bought | research pts {totalResearch:0.0}");
                sb.AppendLine($"Life: {children} children, {adults} adults, {elders} elders | {asleep} sleeping now | {inSettlements} in settlements | {voters} have voted");
            }
            else
            {
                sb.AppendLine("No citizens yet - admins can spawn them with /ecosim spawn <name> <profession>.");
            }

            // --- v0.4 system summary ---
            sb.AppendLine($"Parties: {sim.WorkParties.Parties.Count} ({sim.WorkParties.MilestonesCompleted} milestones) | Corps: {sim.Corporations.Corporations.Count} | Currencies: {sim.Currencies.Currencies.Count} | Listings: {sim.Exchange.Listings.Count} | Futures: {sim.Exchange.Futures.Count}");

            // --- v0.5 simulated shop economy (NPC-only, mod-internal settlement) ---
            try
            {
                sb.AppendLine($"Sim stores: {db.SimStores.Count} open, {db.SimStores.Where(s => !s.IsOpen).Count()} closed | offers {db.SimStores.Where(s => s.IsOpen).Sum(s => s.Stock)} units | trades {db.SimStores.Sum(s => s.TradesSettled)} settled");
            }
            catch { }
            sb.AppendLine($"Research: {sim.Research.DomainPoints.Count} domains, {sim.Research.DiscoveriesUnlocked} discoveries | Pool: {sim.Pool.ActiveNpcCount}/{sim.Pool.MaxActiveNpcs} NPCs, physical pathfinding {sim.Pool.PhysicalPathfindingRatio():0.0%}");
            if (cfg.EnvironmentEnabled)
            {
                string envState = sim.Environment.CrisisActive ? "CRISIS" : "stable";
                if (sim.Environment.MeteorImpacted) envState = "METEOR IMPACTED";
                sb.AppendLine($"Environment: pollution {sim.Environment.Pollution:0.0}, CO2 {sim.Environment.CO2:0.0}, soil {sim.Environment.SoilDegradation:0.0} ({envState}) | meteor defence {sim.Environment.MeteorProgress:0.0%}");
            }
            return sb.ToString();
        }
    }
}
