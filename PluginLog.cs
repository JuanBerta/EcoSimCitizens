using System;
using System.Reflection;

namespace EcoSimCitizens
{
    /// <summary>
    /// Tiny logging shim. Tries to route through Eco's own log if present,
    /// otherwise falls back to the console. Never throws.
    /// </summary>
    public static class PluginLog
    {
        private static readonly MethodInfo EcoWriteLine;

        static PluginLog()
        {
            try
            {
                var logType = Type.GetType("Eco.Shared.Utils.Log, Eco.Shared", false);
                if (logType != null)
                {
                    EcoWriteLine = logType.GetMethod("WriteLine", new[] { typeof(string), typeof(ConsoleColor) });
                }
            }
            catch { EcoWriteLine = null; }
        }

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);

        private static void Write(string level, string message)
        {
            string line = $"[EcoSimCitizens] {level}: {message}";
            try { Console.WriteLine(line); } catch { }
            if (EcoWriteLine != null)
            {
                try { EcoWriteLine.Invoke(null, new object[] { line, ConsoleColor.DarkCyan }); } catch { }
            }
        }
    }
}
