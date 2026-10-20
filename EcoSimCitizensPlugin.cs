using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Eco.Core.Plugins.Interfaces;
using Eco.Core.Utils;
using Eco.Gameplay.Components;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Players;
using Eco.Shared.Localization;
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
    ///
    /// Loading: Eco 0.14's mod-kit loads DLL mods as instance plugins through the
    /// IModKitPlugin/IServerPlugin/IInitializablePlugin interfaces (same pattern as
    /// GoodPrice). The instance is created by the mod loader, and
    /// Initialize(TimedTask) starts the periodic simulation loop on a background
    /// task. Commands resolve the plugin via EcoSimCitizens.Instance. NPCs are
    /// auto-topped-up to the AgentPoolManager.MaxActiveNpcs cap on server start
    /// and on every simulation tick, so the world always has the configured number
    /// of physical citizens.
    /// </summary>
    public class EcoSimCitizens : IModKitPlugin, IServerPlugin, IInitializablePlugin, IShutdownablePlugin
    {
        static EcoSimCitizens()
        {
            PluginLog.Info("EcoSimCitizens build marker: V3-20261016-2338");
            try
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    File.AppendAllText(Path.Combine(dir, "build_marker.log"),
                        $"[{DateTime.Now:HH:mm:ss}] static ctor executed; Location={System.Reflection.Assembly.GetExecutingAssembly().Location}\r\n");
                }
            }
            catch { }
        }

        private readonly CitizensDatabase _database = new CitizensDatabase();
        private SimulationCore _simulation;
        private DateTime _lastTickUtc;
        private readonly object _runLock = new object();
        private CancellationTokenSource _cts;
        private Task _loopTask;
        private Task _moveTask;
        private volatile bool _started;
        private volatile bool _despawnAllHeld;

        /// <summary>
        /// v0.6: true while /npccmd despawnall is in effect - automatic top-up is
        /// paused so the world stays empty until /npccmd respawnall is issued
        /// (or the server restarts, which resets this in-memory flag).
        /// </summary>
        public bool DespawnAllHeld => _despawnAllHeld;

        public static EcoSimCitizens Instance { get; private set; }

        public EcoSimCitizens() { Instance = this; }

        /// <summary>
        /// File-based debug log written next to the mod DLL. The server console
        /// (where PluginLog normally lands) is not captured by automation, so the
        /// sim also mirrors its key lifecycle/tick lines here for log-level tests.
        /// </summary>
        public static void LogDebug(string message)
        {
            try
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir)) return;
                string path = Path.Combine(dir, "debug_tick.log");
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
            }
            catch { }
        }

        public CitizensDatabase Database => _database;
        public SimulationCore Simulation => _simulation;

        // ------------------------------------------------------------ interfaces

        public void Initialize(TimedTask timer) => StartLoop();

        public Task ShutdownAsync()
        {
            try
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    try { _loopTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
                    try { _moveTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
                }
                SaveAll();
                PluginLog.Info("Shutdown complete.");
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Shutdown failed: " + ex.Message);
            }
            return Task.CompletedTask;
        }

        // ---------------------------------------------------- mod-kit status UI

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

        // ---------------------------------------------------------- simulation

        private void StartLoop()
        {
            if (_started) return;
            lock (_runLock)
            {
                if (_started) return;
                _started = true;
                try
                {
                    LogDebug("StartLoop: begin");
                    ModConfig.Instance.Load();
                    LogDebug("StartLoop: config loaded");
                    _database.Load();
                    LogDebug($"StartLoop: database loaded ({_database.Count} citizens, {_database.SimStoreCount} stores)");
                    _simulation = new SimulationCore();
                    LogDebug("StartLoop: simulation core ready");
                    Professions.LoadRealSkills();
                    LogDebug("StartLoop: professions ready");
                    _lastTickUtc = DateTime.UtcNow;
                    PluginLog.Info($"Initialized. Citizens loaded: {_database.Count}");
                    LogDebug($"Initialized. Citizens loaded: {_database.Count}");
                    // Persisted NPCs deserialize without running PostInitialize, so their
                    // MinimapComponent never registers with MinimapManager. Register an
                    // unconditional pass once the world is fully loaded (runs even when
                    // the NPC pool is already full and TopUpNpcs spawns 0).
                    try { InitializableExtension.RunIfOrWhenInitialized(WorldObjectManager.Init, EnsureMinimapForAllNpcs); }
                    catch (Exception ex) { PluginLog.Warn("Minimap ensure registration failed: " + ex.Message); }
                    // Persisted NPCs also need the survival kit (backpack + camp +
                    // workstations); same unconditional pass once the world is loaded.
                    try { InitializableExtension.RunIfOrWhenInitialized(WorldObjectManager.Init, EnsureSurvivalKitForAllNpcs); }
                    catch (Exception ex) { PluginLog.Warn("Survival kit ensure registration failed: " + ex.Message); }
                    // v0.5 persistence: after the world is loaded, three-way reconcile
                    // citizens.json vs world NPCs vs NpcId anchors (creates orphans,
                    // back-fills anchors, clears stale ones). Runs before any top-up so
                    // reconciled anchors stop duplicate spawns.
                    try { InitializableExtension.RunIfOrWhenInitialized(WorldObjectManager.Init, ReconcileAfterWorldLoad); }
                    catch (Exception ex) { PluginLog.Warn("Reconcile registration failed: " + ex.Message); }
                    _cts = new CancellationTokenSource();
                    _loopTask = Task.Run(() => LoopAsync(_cts.Token));
                    _moveTask = Task.Run(() => MoveLoopAsync(_cts.Token));
                    LogDebug("StartLoop: tasks launched");
                }
                catch (Exception ex)
                {
                    // Never let an Initialize failure hang the whole server: log the
                    // exact step and keep the plugin alive for the next attempt.
                    LogDebug("StartLoop FAILED: " + ex);
                    PluginLog.Warn("StartLoop failed: " + ex);
                }
            }
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            // server start: top up physical NPCs to the configured cap once the world is up
            try { TopUpNpcs(); }
            catch (Exception ex) { PluginLog.Warn("Startup NPC top-up failed: " + ex.Message); }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(ModConfig.Instance.TickIntervalMinutes), ct).ConfigureAwait(false);
                    TickOnce();
                    TopUpNpcs(); // keep the pool full after ticks (deaths release slots)
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { PluginLog.Warn("Run failed: " + ex); }
            }
        }

        /// <summary>
        /// High-frequency movement loop: every ~1 second advances every physical
        /// NPC towards its own goal in small smooth steps, and drains the NPC's
        /// throttled routine queue (real-resource gathering + camp-storage usage).
        /// Goal re-decisions stay on the slow simulation tick (ComputeGoal), so this
        /// loop only steps movement and never collapses NPCs onto a shared target.
        /// </summary>
        private async Task MoveLoopAsync(CancellationToken ct)
        {
            var rng = new Random();
            var creator = SafeOnlineCreator();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 0.7~1.1 s between steps: small per-step offsets (0.3~0.6 m)
                    // now read as smooth continuous movement instead of 5~8 s teleports.
                    await Task.Delay(TimeSpan.FromSeconds(0.7 + rng.NextDouble() * 0.4), ct).ConfigureAwait(false);
                    int moved = 0;
                    foreach (var npc in NpcManager.GetAll())
                    {
                        try
                        {
                            npc.BehaviorTick();
                            moved++;
                            Citizen c = null;
                            try { c = _database.Find(npc.CitizenName); } catch { }
                            npc.RoutineTick(c, creator);
                        }
                        catch (Exception ex) { PluginLog.Warn("NPC move step failed: " + ex.Message); }
                    }
                    if (moved > 0) LogDebug($"Move loop advanced {moved} NPC(s).");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { PluginLog.Warn("NPC move loop failed: " + ex.Message); }
            }
        }

        private User SafeOnlineCreator()
        {
            try { return UserManager.Users.FirstOrDefault(u => u != null && u.IsOnline); }
            catch { return null; }
        }

        private void TickOnce()
        {
            lock (_runLock)
            {
                var cfg = ModConfig.Instance;
                _lastTickUtc = DateTime.UtcNow;
                LogDebug($"Tick start. Citizens: {_database.Count}");
                try
                {
                    _simulation.Tick(_database, cfg);
                    _database.Save();
                    LogDebug($"Tick completed and saved. Citizens: {_database.Count}");
                }
                catch (Exception ex)
                {
                    LogDebug($"Tick FAILED: {ex}");
                    PluginLog.Warn("Tick failed: " + ex);
                    throw;
                }

                if (cfg.BroadcastDailyReport && _database.Count > 0)
                {
                    BroadcastReport();
                }
                PluginLog.Info($"Tick completed. Citizens: {_database.Count}");
            }
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

        /// <summary>
        /// Top up physical NPCs to the AgentPoolManager.MaxActiveNpcs cap. Spawns
        /// world NPCs for citizens that do not yet have one, using the first online
        /// user as creator (falls back to null creator when nobody is online).
        /// Returns the number of NPCs spawned this call.
        /// </summary>
        public int TopUpNpcs()
        {
            int spawned = 0;
            try
            {
                if (_simulation == null) return 0;
                if (_despawnAllHeld) return 0; // despawn-all in effect: do not re-populate
                var pool = _simulation.Pool;
                var creator = UserManager.Users.FirstOrDefault(u => u != null && u.IsOnline);
                var basePos = SafeSpawnPosition();
                var rng = new Random();
                // v0.6: world-wide distribution instead of spawn-point clustering.
                // Existing NPCs are treated as obstacles so new ones keep the minimum
                // spacing; the radius comes from the config (or is auto-detected).
                double radius = WorldBounds.EffectiveSpawnRadius(ModConfig.Instance);
                double minSpacing = Math.Max(1.0, ModConfig.Instance.NpcMinSpacing);
                var placed = NpcManager.GetAll().Select(n => n.Position).ToList();
                foreach (var c in _database.Snapshot())
                {
                    if (pool.ActiveNpcCount >= pool.MaxActiveNpcs) break;
                    if (NpcManager.Find(c.Name) != null) continue; // already physical
                    if (!pool.CanSpawn(c.Name)) continue;
                    var npc = NpcManager.SpawnAtDistributed(c, creator, basePos, radius, minSpacing, rng, placed);
                    if (npc == null)
                    {
                        pool.Release(c.Name); // spawn failed, free the slot for a retry
                        continue;
                    }
                    spawned++;
                }
                if (spawned > 0)
                    PluginLog.Info($"EcoSimCitizens: auto-spawned {spawned} NPC(s), pool {pool.ActiveNpcCount}/{pool.MaxActiveNpcs} (distributed radius {radius:0.0}m, min spacing {minSpacing:0.0}m).");
                LogDebug($"NPC top-up done. spawned={spawned} pool={pool.ActiveNpcCount}/{pool.MaxActiveNpcs}");
                EnsureMinimapForAllNpcs(); // always run: covers existing full pool (spawned=0) and newly spawned NPCs
                EnsureSurvivalKitForAllNpcs(); // covers persisted NPCs and re-grants missing kit pieces
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: NPC top-up failed. " + ex.Message);
            }
            return spawned;
        }

        /// <summary>
        /// v0.6 /npccmd despawnall: destroys every mod NPC WorldObject (plus mod-placed
        /// camp/workbench attachments near them), clears the NpcId anchors on the
        /// citizens so no stale physical link survives, persists citizens.json and
        /// holds automatic top-up until /npccmd respawnall (or a server restart).
        /// Citizens remain in the database with their simulated lives intact.
        /// </summary>
        public (int Npcs, int Attachments) DespawnAllNpcs()
        {
            _despawnAllHeld = true;
            var stats = NpcManager.DestroyAll();
            ClearAllNpcAnchors();
            try { _database.Save(); }
            catch (Exception ex) { PluginLog.Warn("DespawnAllNpcs: save failed: " + ex.Message); }
            LogDebug($"DespawnAll executed. npcs={stats.Npcs} attachments={stats.Attachments}");
            return stats;
        }

        /// <summary>
        /// v0.6 /npccmd respawnall: releases the despawn-all hold and immediately
        /// tops the world back up to the configured cap using distributed spawning.
        /// </summary>
        public int RespawnAllNpcs()
        {
            _despawnAllHeld = false;
            int spawned = TopUpNpcs();
            LogDebug($"RespawnAll executed. spawned={spawned}");
            return spawned;
        }

        private void ClearAllNpcAnchors()
        {
            try
            {
                foreach (var c in _database.Snapshot())
                {
                    if (!string.IsNullOrEmpty(c.NpcId))
                    {
                        c.NpcId = null;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("DespawnAllNpcs: anchor clearing failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Explicitly adds/sets the minimap marker on every live CitizenNpc. Needed
        /// because WorldObject.PostInitialize (which normally calls SetCategory) is
        /// only invoked on the create path (OnAdd -> DoInitializationSteps); persisted
        /// NPCs deserialize through OnDeserialized without it, so their
        /// MinimapComponent never reaches MinimapManager and the M-key map shows no
        /// NPC markers. GetOrCreateComponent auto-runs component Initialize when the
        /// world object is already initialized; SetCategory follows the official
        /// StoreObject convention and is idempotent.
        /// </summary>
        private void EnsureMinimapForAllNpcs()
        {
            int ensured = 0;
            try
            {
                foreach (var npc in NpcManager.GetAll())
                {
                    try
                    {
                        var comp = npc.GetOrCreateComponent<MinimapComponent>();
                        if (comp == null) continue;
                        comp.SetCategory(Localizer.DoStr("Citizens"));
                        ensured++;
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warn($"EcoSimCitizens: minimap ensure failed for {npc.CitizenName}: {ex.Message}");
                    }
                }
                PluginLog.Info($"EcoSimCitizens: minimap markers ensured on {ensured} NPC(s).");
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: EnsureMinimapForAllNpcs failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Ensures every live CitizenNpc (including persisted ones that skipped the
        /// spawn path) gets its starter kit and first settlement attempt: backpack
        /// inventory with the player-equivalent kit, Starter Camp, Workbench and
        /// Tool Bench next to the NPC. Idempotent - items and objects already
        /// present are never duplicated (see SurvivalKit).
        /// </summary>
        private void EnsureSurvivalKitForAllNpcs()
        {
            int ensured = 0;
            try
            {
                var creator = UserManager.Users.FirstOrDefault(u => u != null && u.IsOnline);
                foreach (var npc in NpcManager.GetAll())
                {
                    SurvivalKit.GrantToNpc(npc, creator);
                    ensured++;
                }
                PluginLog.Info($"EcoSimCitizens: survival kit ensured on {ensured} NPC(s).");
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: EnsureSurvivalKitForAllNpcs failed: " + ex.Message);
            }
        }

        private static Vector3 SafeSpawnPosition()
        {
            try { return UserManager.GetOrRelocateSpawnPosition(); }
            catch { return Vector3.Zero; }
        }

        /// <summary>
        /// Runs the three-way reconcile after the world is fully loaded. Uses the
        /// same RunIfOrWhenInitialized gate as the minimap/survival passes so world
        /// NPCs are enumerable. Returns the reconcile stats (or -1s on failure).
        /// </summary>
        private void ReconcileAfterWorldLoad()
        {
            try
            {
                var stats = _database.ReconcileWithWorld();
                PluginLog.Info($"EcoSimCitizens: startup reconcile stats - created {stats.Created}, backfilled {stats.Backfilled}, cleared {stats.Cleared}.");
                LogDebug($"Startup reconcile stats - created {stats.Created}, backfilled {stats.Backfilled}, cleared {stats.Cleared}.");
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: ReconcileAfterWorldLoad failed: " + ex.Message);
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
