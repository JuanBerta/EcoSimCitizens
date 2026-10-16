using System;
using System.Linq;
using System.Threading.Tasks;
using Eco.Core.Plugins.Interfaces;
using Eco.Gameplay.Players;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// EcoSim Citizens - an autonomous citizen simulation for Eco Global Survival.
    ///
    /// Citizens are lightweight simulated residents that:
    ///   - work (profession x trait x health productivity)
    ///   - earn and spend on a private ledger
    ///   - consume from the real player-driven market (store price index)
    ///   - live in real deeds and are affected by settlement/civic activity
    ///   - persist between server restarts (JSON next to the mod DLL)
    ///
    /// The simulation is purely observational towards Eco's own systems: it never
    /// claims land, spawns items, or touches other players' property.
    /// </summary>
    public class EcoSimCitizens : IThreadedPlugin
    {
        private readonly CitizensDatabase _database = new CitizensDatabase();
        private SimulationCore _simulation;
        private DateTime _lastTickUtc;
        private bool _initialized;
        private readonly object _runLock = new object();

        public CitizensDatabase Database => _database;
        public SimulationCore Simulation => _simulation;
        public static EcoSimCitizens Instance { get; private set; }

        public string GetCategory() => "EcoSim Citizens";

        public string GetStatus()
        {
            int count = _database.Count;
            string happiness = count > 0 && _simulation != null
                ? $"avg happiness {_simulation.AverageHappiness(_database.Snapshot()):0.0}"
                : "no citizens yet";
            int npcs = 0;
            try { npcs = NpcManager.GetAll().Count(); } catch { }
            return $"{count} citizens, {happiness}, {npcs} npcs in world, next tick in {Math.Max(0, ModConfig.Instance.TickIntervalMinutes - (DateTime.UtcNow - _lastTickUtc).TotalMinutes):0.0} min";
        }

        /// <summary>Called periodically by Eco's plugin thread.</summary>
        public void Run()
        {
            try
            {
                lock (_runLock)
                {
                    if (!_initialized)
                    {
                        _initialized = true;
                        Instance = this;
                        ModConfig.Instance.Load();
                        _database.Load();
                        _simulation = new SimulationCore();
                        Professions.LoadRealSkills();
                        _lastTickUtc = DateTime.UtcNow;
                        PluginLog.Info($"Initialized. Citizens loaded: {_database.Count}");
                    }

                    var cfg = ModConfig.Instance;
                    if ((DateTime.UtcNow - _lastTickUtc).TotalMinutes >= cfg.TickIntervalMinutes)
                    {
                        _lastTickUtc = DateTime.UtcNow;
                        _simulation.Tick(_database, cfg);
                        _database.Save();

                        if (cfg.BroadcastDailyReport && _database.Count > 0)
                        {
                            BroadcastReport();
                        }
                        PluginLog.Info($"Tick completed. Citizens: {_database.Count}");
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Run failed: " + ex);
            }
        }

        public Task ShutdownAsync()
        {
            try
            {
                _database.Save();
                ModConfig.Instance.Save();
                PluginLog.Info("Shutdown: state saved.");
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Shutdown save failed: " + ex.Message);
            }
            return Task.CompletedTask;
        }

        public void SaveAll()
        {
            try
            {
                _database.Save();
                ModConfig.Instance.Save();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("SaveAll failed: " + ex.Message);
            }
        }

        private void BroadcastReport()
        {
            try
            {
                string report = ReportBuilder.Build(_database, _simulation, ModConfig.Instance);
                var online = UserManager.Users.Where(u => u != null && u.IsOnline).ToList();
                if (online.Count == 0) return;
                PlayerExtensions.MsgLocStr(online, report, NotificationStyle.Info);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("BroadcastReport failed: " + ex.Message);
            }
        }
    }
}
