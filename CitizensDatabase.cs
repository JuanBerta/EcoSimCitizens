using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace EcoSimCitizens
{
    /// <summary>
    /// Thread-safe in-memory store plus JSON persistence for citizens.
    /// State is saved to the mod folder so it survives server restarts.
    /// </summary>
    public class CitizensDatabase
    {
        private readonly object _lock = new object();
        private readonly List<Citizen> _citizens = new List<Citizen>();

        public int Count
        {
            get { lock (_lock) { return _citizens.Count; } }
        }

        public IEnumerable<Citizen> Snapshot()
        {
            lock (_lock) { return new List<Citizen>(_citizens); }
        }

        public bool Add(Citizen c)
        {
            lock (_lock)
            {
                if (FindLocked(c.Name) != null) return false;
                _citizens.Add(c);
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

        public static string StatePath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    return Path.Combine(dir, "EcoSimCitizens", "citizens.json");
                }
                catch
                {
                    return null;
                }
            }
        }

        public void Save()
        {
            try
            {
                string path = StatePath;
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<Citizen> copy;
                lock (_lock) { copy = new List<Citizen>(_citizens); }
                File.WriteAllText(path, JsonSerializer.Serialize(copy, JsonOptions));
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: failed to save citizens. " + ex.Message);
            }
        }

        public void Load()
        {
            try
            {
                string path = StatePath;
                if (path == null || !File.Exists(path)) return;
                var list = JsonSerializer.Deserialize<List<Citizen>>(File.ReadAllText(path), JsonOptions);
                if (list == null) return;
                lock (_lock)
                {
                    _citizens.Clear();
                    _citizens.AddRange(list);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: failed to load citizens, starting empty. " + ex.Message);
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };
    }
}
