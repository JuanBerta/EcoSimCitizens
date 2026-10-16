using System;
using System.Collections.Generic;
using System.Linq;

namespace EcoSimCitizens
{
    /// <summary>One autonomous construction project planned by a citizen.</summary>
    public class ConstructionPlan
    {
        public string Blueprint { get; set; }
        public double Complexity { get; set; }
        public double MaterialCost { get; set; }
        public double Progress { get; set; }             // 0..1
        public bool MaterialsSecured { get; set; }
        public string Biome { get; set; }
    }

    /// <summary>
    /// PRD Module 3 (autonomous urbanism, Req.2/3). Citizens plan constructions
    /// adapted to their profession and biome, gather materials from their ledger
    /// (and, when real stores exist, from the market), and advance the build over
    /// ticks. A completed house/store/workshop updates their home and workplace
    /// ties so the whole economy responds to the new property.
    /// </summary>
    public class ConstructionPlanner
    {
        public long ProjectsCompleted { get; private set; }
        public double MaterialsPurchased { get; private set; }

        /// <summary>Assigns a blueprint to a citizen if they do not have one yet.</summary>
        public bool EnsurePlan(Citizen c, string biome)
        {
            if (c == null || c.BlueprintId != null) return false;
            string blueprint = SimulationLogic.BlueprintFor(c.Profession, biome);
            c.BlueprintId = blueprint;
            c.ConstructionProgress = 0;
            return true;
        }

        /// <summary>
        /// Advances the citizen's construction. First secures materials (buying
        /// from the ledger at the market price), then applies work each tick.
        /// On completion the citizen claims the structure as home or workplace.
        /// </summary>
        public void TickConstruction(Citizen c, double workApplied, double marketMaterialPrice, ModConfig cfg)
        {
            if (c == null || c.BlueprintId == null || cfg == null || !cfg.ConstructionEnabled) return;
            if (c.ConstructionProgress >= 1.0) return; // already done

            string blueprint = c.BlueprintId;
            double complexity = SimulationLogic.BlueprintComplexity(blueprint);
            double materialCost = SimulationLogic.BlueprintMaterialCost(blueprint, marketMaterialPrice > 0 ? marketMaterialPrice : cfg.MaterialUnitPrice);

            if (!c.ConstructionMaterialsSecured)
            {
                if (c.LedgerBalance >= materialCost)
                {
                    c.LedgerBalance -= materialCost;
                    MaterialsPurchased += materialCost;
                    c.ConstructionMaterialsSecured = true;
                }
                else
                {
                    return; // still saving up
                }
            }

            c.ConstructionProgress = SimulationLogic.ConstructionProgress(c.ConstructionProgress, workApplied * cfg.ConstructionWorkFactor, complexity);
            if (SimulationLogic.ConstructionComplete(c.ConstructionProgress))
            {
                ProjectsCompleted++;
                ApplyCompletedStructure(c, blueprint);
                c.BlueprintId = null;
                c.ConstructionProgress = 0;
                c.ConstructionMaterialsSecured = false;
            }
        }

        private static void ApplyCompletedStructure(Citizen c, string blueprint)
        {
            // A house makes the citizen's home; a store or workshop becomes work.
            if (blueprint != null && (blueprint.Contains("House", StringComparison.OrdinalIgnoreCase)
                || blueprint.Contains("Farmhouse", StringComparison.OrdinalIgnoreCase)
                || blueprint.Contains("Minecamp", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrEmpty(c.HomeDeedName)) c.HomeDeedName = "SimBuild " + c.Name;
                return;
            }
            if (blueprint != null && (blueprint.Contains("Store", StringComparison.OrdinalIgnoreCase)
                || blueprint.Contains("Workshop", StringComparison.OrdinalIgnoreCase)
                || blueprint.Contains("Laboratory", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrEmpty(c.WorkPlaceName)) c.WorkPlaceName = "SimBuild " + c.Name;
            }
        }
    }
}
