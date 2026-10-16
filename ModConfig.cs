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

        // --- v0.3: production & real inventory ---
        public bool ProductionEnabled { get; set; } = true;
        public double ProductionUnitsPerTick { get; set; } = 2.0;
        public bool SellEnabled { get; set; } = true;

        // --- v0.3: skill growth ---
        public double SkillLearnRate { get; set; } = 1.0;

        // --- v0.3: civics (settlement membership & voting) ---
        public bool VotingEnabled { get; set; } = true;
        public double VotingIntervalDays { get; set; } = 7.0;

        // --- v0.3: life cycle & social trail ---
        public bool LifecycleEnabled { get; set; } = true;
        public double LifecycleStartAgeYears { get; set; } = 18.0;
        public int SocialTrailMax { get; set; } = 8;
        public bool BroadcastCitizenEvents { get; set; } = true;

        // --- v0.4: macroeconomics & commerce (Module 2) ---
        public bool CommerceEnabled { get; set; } = true;
        public double TradeMargin { get; set; } = 0.15;
        public double RawMaterialCost { get; set; } = 5.0;
        public string RawMaterialItemName { get; set; } = "Iron Bar";
        public int MaterialBuyUnits { get; set; } = 2;
        public string FoodItemName { get; set; } = "Cooked Meal";

        // --- v0.4: work parties & contracts (Module 2) ---
        public bool PartyEnabled { get; set; } = true;
        public double PartyTargetProgress { get; set; } = 50.0;

        // --- v0.4: autonomous construction (Module 3) ---
        public bool ConstructionEnabled { get; set; } = true;
        public double MaterialUnitPrice { get; set; } = 4.0;
        public double ConstructionWorkFactor { get; set; } = 1.0;

        // --- v0.4: research & technology (Module 3) ---
        public double ResearchMultiplier { get; set; } = 1.0;

        // --- v0.4: corporations (Module 4) ---
        public bool CorporationEnabled { get; set; } = true;
        public double DividendRate { get; set; } = 0.05;

        // --- v0.4: stock exchange & futures (Module 4) ---
        public bool ExchangeEnabled { get; set; } = true;
        public double FuturesRiskTolerance { get; set; } = 0.1;

        // --- v0.4: settlement currencies & forex (Module 5) ---
        public bool CurrencyEnabled { get; set; } = true;

        // --- v0.4: ecology & crisis management (Module 6) ---
        public bool EnvironmentEnabled { get; set; } = true;
        public double EnvironmentThreshold { get; set; } = 60.0;
        public double EnvironmentalCleanup { get; set; } = 0.5;
        public bool MeteorEnabled { get; set; } = true;
        public double MeteorImpactDay { get; set; } = 30.0;
        public double MeteorDefenseNeeded { get; set; } = 100.0;

        // --- v0.4: data-driven politics & dialogue (Modules 5/7) ---
        public bool PoliticsEnabled { get; set; } = true;
        public bool DialogueEnabled { get; set; } = true;

        // --- v0.4: agent pool ---
        public int PoolLimit { get; set; } = 30;

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
            ProductionEnabled = other.ProductionEnabled;
            ProductionUnitsPerTick = other.ProductionUnitsPerTick;
            SellEnabled = other.SellEnabled;
            SkillLearnRate = other.SkillLearnRate;
            VotingEnabled = other.VotingEnabled;
            VotingIntervalDays = other.VotingIntervalDays;
            LifecycleEnabled = other.LifecycleEnabled;
            LifecycleStartAgeYears = other.LifecycleStartAgeYears;
            SocialTrailMax = other.SocialTrailMax;
            BroadcastCitizenEvents = other.BroadcastCitizenEvents;
            CommerceEnabled = other.CommerceEnabled;
            TradeMargin = other.TradeMargin;
            RawMaterialCost = other.RawMaterialCost;
            RawMaterialItemName = other.RawMaterialItemName ?? "Iron Bar";
            MaterialBuyUnits = other.MaterialBuyUnits;
            FoodItemName = other.FoodItemName ?? "Cooked Meal";
            PartyEnabled = other.PartyEnabled;
            PartyTargetProgress = other.PartyTargetProgress;
            ConstructionEnabled = other.ConstructionEnabled;
            MaterialUnitPrice = other.MaterialUnitPrice;
            ConstructionWorkFactor = other.ConstructionWorkFactor;
            ResearchMultiplier = other.ResearchMultiplier;
            CorporationEnabled = other.CorporationEnabled;
            DividendRate = other.DividendRate;
            ExchangeEnabled = other.ExchangeEnabled;
            FuturesRiskTolerance = other.FuturesRiskTolerance;
            CurrencyEnabled = other.CurrencyEnabled;
            EnvironmentEnabled = other.EnvironmentEnabled;
            EnvironmentThreshold = other.EnvironmentThreshold;
            EnvironmentalCleanup = other.EnvironmentalCleanup;
            MeteorEnabled = other.MeteorEnabled;
            MeteorImpactDay = other.MeteorImpactDay;
            MeteorDefenseNeeded = other.MeteorDefenseNeeded;
            PoliticsEnabled = other.PoliticsEnabled;
            DialogueEnabled = other.DialogueEnabled;
            PoolLimit = other.PoolLimit;
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
