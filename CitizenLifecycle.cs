using System;
using Eco.Shared.Time;

namespace EcoSimCitizens
{
    /// <summary>
    /// Life cycle and death for citizens. Citizens age slowly (one Eco day is a
    /// fraction of a year), move through Child -&gt; Adult -&gt; Elder stages and
    /// eventually die. Death is influenced by unmet needs: starvation, exposure,
    /// isolation and unhappiness all raise mortality, exactly like a real player.
    /// Death records are kept in the social trail before the citizen is retired
    /// from the simulation by SimulationCore.
    /// </summary>
    public static class CitizenLifecycle
    {
        public static void TickAge(Citizen c, Random rng)
        {
            if (c == null || !c.IsAlive) return;
            try
            {
                c.AgeYears += SimulationLogic.YearsPerTick;
                string stage = SimulationLogic.StageForAge(c.AgeYears);
                if (stage != c.LifeStage)
                {
                    c.LifeStage = stage;
                    CitizenSocial.AddTrail(c, $"became a {stage}");
                }

                string cause = SimulationLogic.DeathCheck(c.AgeYears, c.Calories, c.Comfort, c.Social, c.Happiness, rng);
                if (cause != null)
                {
                    c.DeathDay = TimeUtil.Days;
                    c.DeathCause = cause;
                    CitizenSocial.AddTrail(c, $"died of {cause} on day {c.DeathDay:0.0}");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenLifecycle.TickAge failed: " + ex.Message);
            }
        }
    }
}
