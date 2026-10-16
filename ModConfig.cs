using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EcoSimCitizens
{
    /// <summary>
    /// Runtime configuration for EcoSim Citizens. Stored as JSON next to the mod DLL.
    /// All values are intentionally plain numbers so server admins can tune the sim
    /// without recompiling.
    /// </summary>
    public class ModConfig
    {
        public static ModConfig Instance { get; } = new ModConfig();

        /// <summary>Real-world minutes between simulation ticks. Eco's default day is ~40 min.</summary>
        public double TickIntervalMinutes { get; set; } = 30.0;

        // --- needs ---
        public double CalorieDecayPerTick { get; set; } = 4.0;
        public double ComfortDecayPerTick { get; set; } = 2.5;
        public double SocialDecayPerTick { get; set; } = 1.5;
        public double CalorieGainFromFood { get; set; } = 12.0;
        public double FoodSpendFactor { get; set; } = 0.6;

        // --- economy ---
        public double WagePerTick { get; set; } = 20.0;
        public double StartingBalance { get; set; } = 100.0;
        public double PriceTrendSensitivity { get; set; } = 1.0;

        // --- simulation limits ---
        public int MaxCitizens { get; set; } = 50;
        public bool BroadcastDailyReport { get; set; } = true;

        public static string ConfigPath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    return Path.Combine(dir, "EcoSimCitizens", "config.json");
                }
                catch
                {
                    return null;
                }
            }
        }

        public void Load()
        {
            try
            {
                string path = ConfigPath;
                if (path != null && File.Exists(path))
                {
                    var json = JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path), JsonOptions);
                    if (json != null)
                    {
                        CopyFrom(json);
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: failed to load config, using defaults. " + ex.Message);
            }
        }

        public void Save()
        {
            try
            {
                string path = ConfigPath;
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: failed to save config. " + ex.Message);
            }
        }

        private void CopyFrom(ModConfig other)
        {
            TickIntervalMinutes = other.TickIntervalMinutes;
            CalorieDecayPerTick = other.CalorieDecayPerTick;
            ComfortDecayPerTick = other.ComfortDecayPerTick;
            SocialDecayPerTick = other.SocialDecayPerTick;
            CalorieGainFromFood = other.CalorieGainFromFood;
            FoodSpendFactor = other.FoodSpendFactor;
            WagePerTick = other.WagePerTick;
            StartingBalance = other.StartingBalance;
            PriceTrendSensitivity = other.PriceTrendSensitivity;
            MaxCitizens = other.MaxCitizens;
            BroadcastDailyReport = other.BroadcastDailyReport;
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
    }
}
