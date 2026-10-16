using System;
using System.Collections.Generic;

namespace EcoSimCitizens
{
    /// <summary>
    /// PRD Module 7 (contextual dialogue, Req.19). Turns simulation events into
    /// in-character citizen dialogue. Lines are logged to the citizen's dialogue
    /// log and surfaced in the report/telemetry so admins can see the social
    /// layer without spamming chat every tick.
    /// </summary>
    public class DialogueTree
    {
        private const int MaxLog = 20;

        /// <summary>Emits a dialogue line for an event; returns the line (or null).</summary>
        public string Tell(Citizen c, string eventType, string subject)
        {
            if (c == null || string.IsNullOrEmpty(eventType)) return null;
            string line = SimulationLogic.DialogueForEvent(eventType, c.Name, subject ?? "");
            c.DialogueLog ??= new List<string>();
            c.DialogueLog.Add(line);
            if (c.DialogueLog.Count > MaxLog) c.DialogueLog.RemoveAt(0);
            return line;
        }

        /// <summary>Returns recent dialogue lines of a citizen as one string.</summary>
        public static string RecentLines(Citizen c)
        {
            if (c == null || c.DialogueLog == null || c.DialogueLog.Count == 0) return "(no dialogue yet)";
            return string.Join(Environment.NewLine, c.DialogueLog);
        }
    }
}
