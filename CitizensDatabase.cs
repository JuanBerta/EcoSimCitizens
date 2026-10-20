using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Eco.Core;
using Eco.Gameplay.Objects;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// Thread-safe in-memory store plus JSON persistence for citizens and their
    /// simulated stores. State is saved to the mod folder so it survives server
    /// restarts.
    ///
    /// Persistence fixes:
    ///   - StatePath no longer nests an extra "EcoSimCitizens" directory: the
    ///     state file lives directly next to the mod DLL (citizens.json).
    ///   - Save() writes atomically: JSON goes to a .tmp sibling first, then the
    ///     file is replaced, so a crash never leaves a half-written state file.
    ///   - SaveThrottled() batches rapid changes (e.g. per-tick mutations) behind
    ///     a configurable throttle window; Shutdown and explicit saves always
    ///     flush immediately.
    ///   - Load() runs a three-way reconcile between persisted citizens, world
    ///     NPCs and NpcId anchors (see ReconcileWithWorld).
    /// </summary>
    public class CitizensDatabase
    {
        private sealed class StateFile
        {
            public List<Citizen> Citizens { get; set; } = new List<Citizen>();
            public List<SimStore> SimStores { get; set; } = new List<SimStore>();
        }

        private readonly object _lock = new object();
        private readonly List<Citizen> _citizens = new List<Citizen>();
        private readonly List<SimStore> _simStores = new List<SimStore>();
        private DateTime _lastSaveUtc = DateTime.MinValue;
        private bool _dirty;

        public int Count
        {
            get { lock (_lock) { return _citizens.Count; } }
        }

        public int SimStoreCount
        {
            get { lock (_lock) { return _simStores.Count; } }
        }

        public IEnumerable<Citizen> Snapshot()
        {
            lock (_lock) { return new List<Citizen>(_citizens); }
        }

        public IEnumerable<SimStore> SimStoresSnapshot()
        {
            lock (_lock) { return new List<SimStore>(_simStores); }
        }

        /// <summary>
        /// Read-only view of all simulated stores (snapshot taken under the lock).
        /// Used by reporting and the shop manager; mutations go through
        /// AddStore/RemoveStoresFor so they mark the DB dirty.
        /// </summary>
        public IReadOnlyList<SimStore> SimStores
        {
            get { lock (_lock) { return new List<SimStore>(_simStores); } }
        }

        public bool Add(Citizen c)
        {
            lock (_lock)
            {
                if (FindLocked(c.Name) != null) return false;
                _citizens.Add(c);
                MarkDirtyLocked();
                return true;
            }
        }

        public bool Remove(string name)
        {
            lock (_lock)
            {
                var c = FindLocked(name);
                if (c == null) return false;
                _citizens.Remove(c);
                _simStores.RemoveAll(s => string.Equals(s.OwnerName, name, StringComparison.OrdinalIgnoreCase));
                MarkDirtyLocked();
                return true;
            }
        }

        public Citizen Find(string name)
        {
            lock (_lock) { return FindLocked(name); }
        }

        private Citizen FindLocked(string name)
        {
            foreach (var c in _citizens)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        private Citizen FindByNpcIdLocked(string npcId)
        {
            if (string.IsNullOrEmpty(npcId)) return null;
            foreach (var c in _citizens)
            {
                if (string.Equals(c.NpcId, npcId, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        // ------------------------------------------------------------ sim stores

        public void AddStore(SimStore store)
        {
            if (store == null) return;
            lock (_lock)
            {
                _simStores.RemoveAll(s => string.Equals(s.Id, store.Id, StringComparison.OrdinalIgnoreCase));
                _simStores.Add(store);
                MarkDirtyLocked();
            }
        }

        public void RemoveStoresFor(string ownerName)
        {
            lock (_lock)
            {
                int before = _simStores.Count;
                _simStores.RemoveAll(s => string.Equals(s.OwnerName, ownerName, StringComparison.OrdinalIgnoreCase));
                if (_simStores.Count != before) MarkDirtyLocked();
            }
        }

        public int CountStoresOf(string ownerName)
        {
            lock (_lock)
            {
                return _simStores.Count(s => string.Equals(s.OwnerName, ownerName, StringComparison.OrdinalIgnoreCase));
            }
        }

        public SimStore FindOpenStoreOf(string ownerName, string itemTypeName)
        {
            lock (_lock)
            {
                return _simStores.FirstOrDefault(s => s.IsOpen
                    && string.Equals(s.OwnerName, ownerName, StringComparison.OrdinalIgnoreCase)
                    && s.Sells(itemTypeName));
            }
        }

        // ------------------------------------------------------------ persistence

        /// <summary>
        /// State file lives directly next to the mod DLL. The old bug nested an
        /// extra "EcoSimCitizens" folder into the DLL directory which could split
        /// state across two locations depending on where the DLL was deployed.
        /// </summary>
        public static string StatePath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "citizens.json");
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>Immediate atomic save. Called on shutdown and explicit saves.</summary>
        public void Save()
        {
            try
            {
                string path = StatePath;
                if (path == null) return;
                lock (_lock)
                {
                    WriteAtomicLocked(path, new StateFile
                    {
                        Citizens = new List<Citizen>(_citizens),
                        SimStores = new List<SimStore>(_simStores),
                    });
                    _lastSaveUtc = DateTime.UtcNow;
                    _dirty = false;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: failed to save citizens. " + ex.Message);
            }
        }

        /// <summary>
        /// Throttled save: rapid per-tick mutations only write at most once per
        /// SaveThrottleSeconds, deferring the rest to the next flush. Shutdown and
        /// explicit commands always call Save() which bypasses the throttle.
        /// </summary>
        public void SaveThrottled(double throttleSeconds)
        {
            try
            {
                string path = StatePath;
                if (path == null) return;
                lock (_lock)
                {
                    if (!_dirty) return;
                    if ((DateTime.UtcNow - _lastSaveUtc).TotalSeconds < Math.Max(1.0, throttleSeconds)) return;
                    WriteAtomicLocked(path, new StateFile
                    {
                        Citizens = new List<Citizen>(_citizens),
                        SimStores = new List<SimStore>(_simStores),
                    });
                    _lastSaveUtc = DateTime.UtcNow;
                    _dirty = false;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: failed to throttled-save citizens. " + ex.Message);
            }
        }

        private static void WriteAtomicLocked(string path, StateFile data)
        {
            string dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, JsonOptions));
            try
            {
                File.Move(tmp, path, true); // atomic replace on the same volume
            }
            catch
            {
                File.WriteAllText(path, File.ReadAllText(tmp));
                try { File.Delete(tmp); } catch { }
            }
        }

        public void Load()
        {
            try
            {
                string path = StatePath;
                if (path == null || !File.Exists(path)) return;
                string json = File.ReadAllText(path);

                StateFile state = null;
                try
                {
                    state = JsonSerializer.Deserialize<StateFile>(json, JsonOptions);
                }
                catch
                {
                    // legacy format: a bare citizens array from older versions
                    var legacy = JsonSerializer.Deserialize<List<Citizen>>(json, JsonOptions);
                    if (legacy != null)
                    {
                        state = new StateFile { Citizens = legacy };
                    }
                }
                if (state == null) return;

                lock (_lock)
                {
                    _citizens.Clear();
                    _citizens.AddRange(state.Citizens ?? new List<Citizen>());
                    _simStores.Clear();
                    if (state.SimStores != null) _simStores.AddRange(state.SimStores);
                    // stores whose owner vanished are closed, never dropped (data safety)
                    foreach (var s in _simStores)
                    {
                        if (FindLocked(s.OwnerName) == null) s.IsOpen = false;
                    }
                }
                PluginLog.Info($"EcoSimCitizens: loaded {_citizens.Count} citizens, {_simStores.Count} sim stores from {path}.");
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: failed to load citizens, starting empty. " + ex.Message);
            }
        }

        // ------------------------------------------------------------ reconcile

        /// <summary>
        /// Three-way reconcile after load: persisted citizens.json, live world
        /// NPCs and NpcId anchors are brought into agreement.
        ///   - world NPC without a citizen record  -> orphan citizen created (data kept)
        ///   - citizen + world NPC without anchor  -> NpcId back-filled from WorldObject.ID
        ///   - stale NpcId pointing at a dead NPC  -> anchor cleared (top-up will respawn)
        /// </summary>
        public ReconcileStats ReconcileWithWorld()
        {
            try
            {
                List<(string Name, string NpcId)> worldNpcs = new List<(string, string)>();
                foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
                {
                    if (wo is CitizenNpc npc && !npc.IsDestroyed && !string.IsNullOrEmpty(npc.CitizenName))
                    {
                        worldNpcs.Add((npc.CitizenName, npc.ID.ToString()));
                    }
                }

                var plan = SimStoreLogic.PlanReconcile(Snapshot().ToList(), worldNpcs);
                if (plan.IsEmpty)
                {
                    PluginLog.Info($"EcoSimCitizens: reconcile clean (world NPCs {worldNpcs.Count}).");
                    return new ReconcileStats(0, 0, 0);
                }

                int created = 0, backfilled = 0, cleared = 0;
                lock (_lock)
                {
                    foreach (var name in plan.CreateCitizens)
                    {
                        var wo = worldNpcs.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (string.IsNullOrEmpty(wo.NpcId)) continue;
                        var orphan = new Citizen
                        {
                            Name = name,
                            Profession = Professions.FallbackFor(name),
                            Trait = "Balanced",
                            LedgerBalance = 0,
                            Calories = 60,
                            Comfort = 50,
                            Social = 50,
                            Happiness = 50,
                            NpcId = wo.NpcId,
                        };
                        _citizens.Add(orphan);
                        created++;
                        PluginLog.Info($"EcoSimCitizens: reconcile created citizen '{name}' for orphan world NPC (npcId {wo.NpcId}).");
                    }
                    foreach (var bind in plan.BackfillNpcIds)
                    {
                        var c = FindLocked(bind.CitizenName);
                        if (c == null) continue;
                        c.NpcId = bind.NpcId;
                        backfilled++;
                        PluginLog.Info($"EcoSimCitizens: reconcile anchored '{bind.CitizenName}' to npcId {bind.NpcId}.");
                    }
                    foreach (var name in plan.ClearNpcIds)
                    {
                        var c = FindLocked(name);
                        if (c == null) continue;
                        c.NpcId = null;
                        cleared++;
                        PluginLog.Info($"EcoSimCitizens: reconcile cleared stale npcId anchor for '{name}'.");
                    }
                    if (plan.RenameAnchors.Count > 0)
                    {
                        foreach (var r in plan.RenameAnchors)
                        {
                            var c = FindByNpcIdLocked(r.NpcId);
                            if (c == null) continue;
                            // keep the citizen identity, NPC name wins for display consistency
                            PluginLog.Info($"EcoSimCitizens: reconcile name drift {r.OldName}->{r.NewName} for npcId {r.NpcId}.");
                        }
                    }
                    MarkDirtyLocked();
                }

                PluginLog.Info($"EcoSimCitizens: reconcile done - created {created}, backfilled {backfilled}, cleared {cleared}.");
                Save(); // persist the reconciled state immediately
                return new ReconcileStats(created, backfilled, cleared);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: reconcile failed. " + ex.Message);
                return new ReconcileStats(-1, -1, -1);
            }
        }

        private void MarkDirtyLocked() => _dirty = true;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };
    }

    /// <summary>Summary of a reconcile pass.</summary>
    public class ReconcileStats
    {
        public int Created;
        public int Backfilled;
        public int Cleared;
        public ReconcileStats(int created, int backfilled, int cleared)
        {
            Created = created;
            Backfilled = backfilled;
            Cleared = cleared;
        }
    }
}
