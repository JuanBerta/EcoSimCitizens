using System;
using Eco.Gameplay.Property;
using Eco.Gameplay.Settlements;

namespace EcoSimCitizens
{
    /// <summary>
    /// Maps a citizen's HomeDeedName onto the real property system to derive comfort.
    /// A citizen with no deed lives rough (low comfort); with a deed, comfort scales
    /// with plot count and settlement support.
    /// </summary>
    public static class HousingEvaluator
    {
        public static double EvaluateComfort(Citizen c)
        {
            if (!c.HasHome) return 20.0;                 // homeless baseline
            var deed = FindDeed(c.HomeDeedName);
            if (deed == null) return 15.0;               // home lost to unclaiming

            double comfort = 55.0 + deed.PlotCount * 5.0;
            if (deed.CachedOwningSettlement != null) comfort += 15.0;
            if (deed.CachedInfluencingSettlement != null) comfort += 5.0;
            return SimMath.Clamp(comfort, 0, 100);
        }

        public static Deed FindDeed(string name)
        {
            try
            {
                var deeds = PropertyManager.Obj.Deeds;
                if (deeds == null) return null;
                foreach (var deed in deeds)
                {
                    if (deed == null) continue;
                    if (string.Equals(deed.Name, name, StringComparison.OrdinalIgnoreCase)) return deed;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("HousingEvaluator failed: " + ex.Message);
            }
            return null;
        }
    }
}
