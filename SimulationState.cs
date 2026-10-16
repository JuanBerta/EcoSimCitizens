using System;
using System.Globalization;
using System.IO;

namespace EcoSimCitizens
{
    /// <summary>
    /// Minimal persistent state for the global simulation systems (environment,
    /// meteor defence, agent pool). Citizens themselves persist via
    /// CitizensDatabase; this file keeps the world-level state across restarts.
    /// Format is a simple line-based JSON so it stays safe even if a future Eco
    /// update changes the mod's assembly references.
    /// </summary>
    public class SimulationState
    {
        public double Pollution;
        public double CO2;
        public double SoilDegradation;
        public double MeteorProgress;
        public double ImpactDay;
        public bool MeteorImpacted;
        public int MaxActiveNpcs = 30;
        public long PhysicalMoveTicks;
        public long LogicalMoveTicks;

        public static SimulationState Load(string path)
        {
            var state = new SimulationState();
            try
            {
                if (!File.Exists(path)) return state;
                foreach (var line in File.ReadAllLines(path))
                {
                    var kv = SplitKv(line);
                    if (kv == null) continue;
                    switch (kv[0])
                    {
                        case "pollution": state.Pollution = ParseD(kv[1]); break;
                        case "co2": state.CO2 = ParseD(kv[1]); break;
                        case "soil": state.SoilDegradation = ParseD(kv[1]); break;
                        case "meteor_progress": state.MeteorProgress = ParseD(kv[1]); break;
                        case "impact_day": state.ImpactDay = ParseD(kv[1]); break;
                        case "impacted": state.MeteorImpacted = kv[1] == "true"; break;
                        case "max_npcs": state.MaxActiveNpcs = ParseI(kv[1]); break;
                        case "physical_ticks": state.PhysicalMoveTicks = ParseL(kv[1]); break;
                        case "logical_ticks": state.LogicalMoveTicks = ParseL(kv[1]); break;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SimulationState load failed: " + ex.Message);
            }
            return state;
        }

        public void Save(string path)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"pollution\": {Pollution.ToString("0.00", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"co2\": {CO2.ToString("0.00", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"soil\": {SoilDegradation.ToString("0.00", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"meteor_progress\": {MeteorProgress.ToString("0.000", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"impact_day\": {ImpactDay.ToString("0.0", CultureInfo.InvariantCulture)},");
                sb.AppendLine($"  \"impacted\": {MeteorImpacted.ToString().ToLowerInvariant()},");
                sb.AppendLine($"  \"max_npcs\": {MaxActiveNpcs},");
                sb.AppendLine($"  \"physical_ticks\": {PhysicalMoveTicks},");
                sb.AppendLine($"  \"logical_ticks\": {LogicalMoveTicks}");
                sb.AppendLine("}");
                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SimulationState save failed: " + ex.Message);
            }
        }

        private static string[] SplitKv(string line)
        {
            line = (line ?? "").Trim();
            if (line.Length == 0 || line == "{" || line == "}") return null;
            int colon = line.IndexOf(':');
            if (colon < 0) return null;
            string key = line.Substring(0, colon).Trim().Trim('"');
            string value = line.Substring(colon + 1).Trim().TrimEnd(',').Trim().Trim('"');
            return new[] { key, value };
        }

        private static double ParseD(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        private static int ParseI(string s) => int.TryParse(s, out int v) ? v : 0;
        private static long ParseL(string s) => long.TryParse(s, out long v) ? v : 0;
    }
}
