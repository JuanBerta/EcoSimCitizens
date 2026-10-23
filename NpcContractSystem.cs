using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Eco.Core.Utils;
using Eco.Gameplay.Civics.Misc;
using Eco.Gameplay.Components.Storage;
using Eco.Gameplay.Economy;
using Eco.Gameplay.Economy.Contracts;
using Eco.Gameplay.Items;
using Eco.Gameplay.Objects;
using Eco.Shared.Items;
using Eco.Shared.Math;
using Eco.Shared.Services;
using Eco.Shared.Time;

namespace EcoSimCitizens
{
    /// <summary>
    /// v2.1 contract system for NPC citizens.
    ///
    /// Two layers, both read-only against the real Eco contract board:
    ///  1. BOARD SCAN: every ContractBoardScanTicks a citizen reads
    ///     EconomyManager.Contracts and looks for Proposed hauling contracts
    ///     (ContractClauseTransport without harvest clauses). Reward is read
    ///     from ContractClausePayment clauses; food cost is estimated from the
    ///     current market price. A contract is only ever "taken" when
    ///     reward >= foodCost * ContractFoodCoverageFactor.
    ///     Real acceptance (SetProposedState + reflection on the private
    ///     EndTime/Contractor properties, as Rabotnik does) is gated behind
    ///     ModConfig.ContractAcceptAttempts and a version probe
    ///     (GetProperty != null). NPCs have no physical hauling executor, so by
    ///     default acceptance stays OFF and the scan is purely informative.
    ///  2. INTERNAL HAULING LOOP: the guaranteed, always-runnable fallback.
    ///     Each citizen self-generates a small hauling job; on completion it
    ///     is paid through the native bank account (and the mod ledger) and a
    ///     raw item is delivered into the citizen's inventory, which feeds the
    ///     production &amp; sales system (NpcProductionSystem / SellProduced) so
    ///     the goods can end up on the citizen's own SimStore for players.
    ///
    /// No ForceAdd, no CreditComponent dependency, no fake Users.
    /// </summary>
    public static class NpcContractSystem
    {
        /// <summary>Read-only board scan results (per tick, last scan wins).</summary>
        public static class ContractScanStats
        {
            public static int LastHaulingSeen;
            public static int LastAffordable;
            public static string ProbeResult = "not-run";
            public static bool ProbeCached;
            public static bool LastBoardNull;
        }

        private static readonly object Sync = new object();

        /// <summary>
        /// Called from SimulationCore.TickCitizen for every citizen. The cadence
        /// splits board scans so we do not hammer the contract board every tick.
        /// </summary>
        public static void TickContracts(Citizen c, CitizensDatabase db, ModConfig cfg, MarketSnapshot market)
        {
            if (c == null || !c.IsAlive || !cfg.ContractSystemEnabled) return;
            try
            {
                lock (Sync)
                {
                    // 1) periodically look at the real contract board (read-only)
                    if (c.AgeInTicks % Math.Max(1, cfg.ContractBoardScanTicks) == 0)
                    {
                        ScanBoardAndAssess(c, cfg, market);
                    }

                    // 2) internal hauling loop (self-generated contract)
                    if (c.ContractState == "Active")
                    {
                        TickActiveContract(c, db, cfg, market);
                    }
                    else if (c.ContractState != "Active" && CountActiveContracts(c, db) < Math.Max(1, cfg.ContractMaxActivePerCitizen))
                    {
                        TryStartInternalContract(c, cfg, market);
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"NpcContractSystem.TickContracts failed for '{c.Name}': {ex.Message}");
            }
        }

        private static int CountActiveContracts(Citizen c, CitizensDatabase db)
        {
            try
            {
                return db.Snapshot().Count(x => x != null && x.ContractState == "Active");
            }
            catch
            {
                return c.ContractState == "Active" ? 1 : 0;
            }
        }

        // ----------------------------------------------------------------- board scan

        /// <summary>
        /// Reads the real contract board (EconomyManager.Contracts) and counts
        /// Proposed hauling contracts and how many of them pass the food-cost
        /// coverage check. Never mutates the board.
        /// </summary>
        private static void ScanBoardAndAssess(Citizen c, ModConfig cfg, MarketSnapshot market)
        {
            if (!ContractScanStats.ProbeCached)
            {
                ContractScanStats.ProbeCached = true;
                ContractScanStats.ProbeResult = ProbeReflectionApi();
            }

            var board = EconomyManager.Contracts;
            var list = board?.Contracts;
            if (list == null || list.Count() == 0)
            {
                ContractScanStats.LastBoardNull = list == null;
                ContractScanStats.LastHaulingSeen = 0;
                ContractScanStats.LastAffordable = 0;
                return;
            }

            int hauling = 0;
            int affordable = 0;
            foreach (var contract in list)
            {
                if (contract == null) continue;
                if (contract.State != ProposableState.Proposed) continue;
                if (!contract.Clauses.Any(cl => cl is ContractClauseTransport)) continue;

                hauling++;
                double reward = EstimateReward(contract);
                int units = EstimateHaulUnits(contract.Clauses.OfType<ContractClauseTransport>().FirstOrDefault());
                double foodCost = FoodCostForHaul(Math.Max(1, units), market, cfg);
                if (reward >= foodCost * cfg.ContractFoodCoverageFactor) affordable++;
            }

            ContractScanStats.LastBoardNull = false;
            ContractScanStats.LastHaulingSeen = hauling;
            ContractScanStats.LastAffordable = affordable;
            PluginLog.Info(
                $"NpcContractSystem: board scan for '{c.Name}' - {hauling} hauling contract(s), " +
                $"{affordable} affordable (reward covers food cost x {cfg.ContractFoodCoverageFactor:0.0}); " +
                $"reflection probe '{ContractScanStats.ProbeResult}'. Real acceptance is OFF by default (NPCs use the internal hauling loop).");
        }

        /// <summary>
        /// Version probe for the private contract properties Rabotnik writes
        /// (Contract.EndTime / Contract.Contractor). We only check existence and
        /// never write them. Failures degrade to the internal loop.
        /// </summary>
        private static string ProbeReflectionApi()
        {
            try
            {
                var endTime = typeof(Contract).GetProperty("EndTime", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var contractor = typeof(Contract).GetProperty("Contractor", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return (endTime != null && contractor != null) ? "ok" : "missing";
            }
            catch (Exception ex)
            {
                return "error:" + ex.Message;
            }
        }

        private static double EstimateReward(Contract contract)
        {
            double reward = 0;
            foreach (var clause in contract.Clauses)
            {
                if (clause is ContractClausePayment p && p.Payment > 0) reward += p.Payment;
            }
            return reward;
        }

        /// <summary>
        /// Approximates the number of item stacks inside the hauling source
        /// container (read-only; a negative/missing source yields 1 unit).
        /// </summary>
        private static int EstimateHaulUnits(ContractClauseTransport transport)
        {
            try
            {
                var src = transport?.MoveSource?.PickedObj;
                if (src == null) return 1;
                var storage = src.GetComponent<StorageComponent>();
                if (storage?.Inventory == null) return 1;
                int total = 0;
                foreach (var st in storage.Inventory.NonEmptyStacks) total += st.Quantity;
                return Math.Max(1, total);
            }
            catch
            {
                return 1;
            }
        }

        // ------------------------------------------------------- internal hauling loop

        /// <summary>
        /// Starts a self-generated hauling contract. The reward is set so that it
        /// always covers the estimated food cost (base + foodCost * factor), and
        /// the contract is skipped entirely if the computed reward would not cover
        /// it (only possible when the market food price is absurdly high).
        /// </summary>
        private static void TryStartInternalContract(Citizen c, ModConfig cfg, MarketSnapshot market)
        {
            double foodCost = FoodCostForHaul(1, market, cfg);
            double reward = cfg.ContractBaseReward + foodCost * cfg.ContractFoodCoverageFactor;
            if (reward < foodCost * cfg.ContractFoodCoverageFactor)
            {
                c.ContractState = "BoardSkipped";
                PluginLog.Info($"NpcContractSystem: '{c.Name}' skipped hauling - reward {reward:0.0} does not cover food cost {foodCost:0.0}.");
                return;
            }

            c.ContractId = "haul-" + c.Name + "-" + c.AgeInTicks;
            c.ContractState = "Active";
            c.ContractWorkRemaining = Math.Max(1, cfg.ContractWorkTicks);
            PluginLog.Info($"NpcContractSystem: '{c.Name}' started internal hauling '{c.ContractId}' (reward {reward:0.0}, food cost {foodCost:0.0}).");
        }

        private static void TickActiveContract(Citizen c, CitizensDatabase db, ModConfig cfg, MarketSnapshot market)
        {
            if (c.ContractWorkRemaining > 0)
            {
                c.ContractWorkRemaining--;
                return;
            }

            // complete the haul: pay reward (native + ledger) and deliver raw goods
            double foodCost = FoodCostForHaul(1, market, cfg);
            double reward = cfg.ContractBaseReward + foodCost * cfg.ContractFoodCoverageFactor;
            PayReward(c, cfg, reward);

            string itemName = HaulItemFor(c, db, cfg);
            int units = Math.Max(1, 1 + (c.AgeInTicks % 3));
            SimulationLogic.InventoryAdd(c.Inventory, itemName, units);
            c.HauledUnits += units;
            c.ContractsCompleted++;
            c.ContractEarnings += reward;
            PluginLog.Info($"NpcContractSystem: '{c.Name}' completed '{c.ContractId}' - paid {reward:0.0}, hauled {units} x {itemName}.");

            c.ContractId = null;
            c.ContractState = "Idle";
        }

        private static void PayReward(Citizen c, ModConfig cfg, double reward)
        {
            try
            {
                var cur = NativeCurrencyBridge.DefaultCurrency();
                var account = CitizenEconomy.AccountFor(c, cfg);
                if (cur != null && account != null)
                {
                    account.AddCurrency(cur, (float)reward, false);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"NpcContractSystem: native payment failed for '{c.Name}': {ex.Message}");
            }
            c.LedgerBalance += reward;
        }

        /// <summary>
        /// Chooses the raw item a completed hauling contract delivers. Prefers
        /// the item the citizen's own open SimStore already sells so the hauled
        /// goods flow straight onto the shelf; otherwise falls back to a
        /// non-food profession item, then any profession item, then the config
        /// raw material.
        /// </summary>
        private static string HaulItemFor(Citizen c, CitizensDatabase db, ModConfig cfg)
        {
            try
            {
                var open = db.SimStoresSnapshot()
                    .Where(s => s.IsOpen && string.Equals(s.OwnerName, c.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.ItemTypeName)
                    .FirstOrDefault(n => !string.IsNullOrEmpty(n));
                if (!string.IsNullOrEmpty(open)) return open;
            }
            catch
            {
                // fall through to profession-based selection
            }

            try
            {
                var items = CitizenProduction.CandidateItemsFor(c.Profession);
                foreach (var it in items)
                {
                    if (it?.Type == null || string.IsNullOrEmpty(it.Type.Name)) continue;
                    if (!SimulationLogic.IsFoodItem(it.Type.Name)) return it.Type.Name;
                }
                foreach (var it in items)
                {
                    if (it?.Type == null || string.IsNullOrEmpty(it.Type.Name)) continue;
                    return it.Type.Name;
                }
            }
            catch
            {
                // fall through to config raw material
            }

            return string.IsNullOrEmpty(cfg.RawMaterialItemName) ? "Iron Bar" : cfg.RawMaterialItemName;
        }

        private static double FoodCostForHaul(int units, MarketSnapshot market, ModConfig cfg)
        {
            double unitPrice = (market != null && market.HasMarket && market.MinPrice > 0)
                ? market.MinPrice
                : cfg.RawMaterialCost;
            return units * cfg.ContractCaloriesPerUnit * unitPrice;
        }

        // ------------------------------------------------------------------ report

        public static string BuildReport(CitizensDatabase db, ModConfig cfg)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Contract system: {(cfg.ContractSystemEnabled ? "ON" : "OFF")} | real-board acceptance {(cfg.ContractAcceptAttempts ? "ON" : "OFF (internal hauling loop)")}");
            sb.AppendLine($"Board scan: {ContractScanStats.LastHaulingSeen} hauling contract(s) seen, {ContractScanStats.LastAffordable} affordable (reward >= food cost x {cfg.ContractFoodCoverageFactor:0.0}) | reflection probe: {ContractScanStats.ProbeResult} | board null: {ContractScanStats.LastBoardNull}");
            var citizens = db.Snapshot().Where(x => x != null).ToList();
            sb.AppendLine($"Contracts completed: {citizens.Sum(x => x.ContractsCompleted)} | earnings {citizens.Sum(x => x.ContractEarnings):0.00} | hauled {citizens.Sum(x => x.HauledUnits)} units");
            int active = citizens.Count(x => x.ContractState == "Active");
            sb.AppendLine($"Active internal hauling contracts: {active}");
            foreach (var c in citizens.Where(x => x.ContractState != "Idle").Take(12))
            {
                sb.AppendLine($"  {c.Name}: {c.ContractState} (id {c.ContractId ?? "-"}, {c.ContractWorkRemaining} ticks left, done {c.ContractsCompleted}, earned {c.ContractEarnings:0.0})");
            }
            return sb.ToString();
        }
    }
}
