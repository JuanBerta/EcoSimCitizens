using System;

namespace EcoSimCitizens
{
    /// <summary>
    /// PRD Module 6 (ecology &amp; crisis management, Req.5/17). Tracks pollution,
    /// CO2 and soil degradation from industrial activity, flips citizens to
    /// cleaner recipes and ecological votes when thresholds are crossed, and
    /// manages the meteor defence: citizens feed power/resources into the laser
    /// and, if the impact window passes undefended, the world enters extreme
    /// survival mode (debris clearance, air purification, supply rebuild).
    /// </summary>
    public class EnvironmentTracker
    {
        public double Pollution { get; set; }
        public double CO2 { get; set; }
        public double SoilDegradation { get; set; }
        public double IndustrialActivity { get; set; }
        public double MeteorProgress { get; set; }
        public double ImpactDay { get; set; }          // game day of the meteor
        public bool MeteorImpacted { get; set; }       // true after the impact window
        public bool CrisisActive { get; set; }
        public long CleanupActions { get; private set; }
        public long MeteorContributions { get; private set; }

        /// <summary>Runs the ecology tick; returns true when a new crisis flips on.</summary>
        public bool TickEnvironment(ModConfig cfg, double industrialActivity)
        {
            if (cfg == null || !cfg.EnvironmentEnabled) return false;
            IndustrialActivity = industrialActivity;
            Pollution = SimulationLogic.PollutionGrowth(Pollution, industrialActivity, cfg.EnvironmentalCleanup);
            CO2 = SimulationLogic.PollutionGrowth(CO2, industrialActivity * 0.7, cfg.EnvironmentalCleanup * 0.5);
            SoilDegradation = SimulationLogic.PollutionGrowth(SoilDegradation, industrialActivity * 0.4, cfg.EnvironmentalCleanup * 0.3);

            bool crisisNow = SimulationLogic.EnvironmentalCrisis(Pollution, CO2, SoilDegradation, cfg.EnvironmentThreshold);
            bool flipped = crisisNow && !CrisisActive;
            CrisisActive = crisisNow;
            return flipped;
        }

        /// <summary>Citizen defence contribution; returns true when the laser is fully charged.</summary>
        public bool ContributeToMeteorDefence(double power, double resources, double totalNeeded)
        {
            MeteorProgress = SimulationLogic.MeteorDefenseProgress(MeteorProgress, power, resources, totalNeeded);
            MeteorContributions++;
            return MeteorProgress >= 1.0;
        }

        /// <summary>Checks the impact window; flips extreme survival mode when undefended.</summary>
        public bool CheckImpact(double currentDay)
        {
            if (MeteorImpacted) return true;
            if (SimulationLogic.ExtremeSurvivalMode(MeteorProgress, ImpactDay, currentDay))
            {
                MeteorImpacted = true;
                return true;
            }
            return false;
        }

        /// <summary>Citizens switch to cleaner recipes when crisis or impact is active.</summary>
        public string ActiveRecipeFor(Citizen c)
        {
            if (CrisisActive || MeteorImpacted) return SimulationLogic.CleanerRecipeFor(c.Profession);
            return "StandardRecipe";
        }

        public string SurvivalPriority(Citizen c)
        {
            if (!MeteorImpacted) return null;
            return SimulationLogic.SurvivalPriorityFor(c.Profession);
        }

        public void DoCleanup(double amount)
        {
            Pollution = Math.Max(0, Pollution - amount);
            CO2 = Math.Max(0, CO2 - amount * 0.8);
            SoilDegradation = Math.Max(0, SoilDegradation - amount * 0.6);
            CleanupActions++;
        }
    }
}
