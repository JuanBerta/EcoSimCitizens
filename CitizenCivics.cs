using System;
using System.Collections.Generic;
using System.Linq;
using Eco.Gameplay.Civics;
using Eco.Gameplay.Property;
using Eco.Gameplay.Settlements;
using Eco.Shared.Time;

namespace EcoSimCitizens
{
    /// <summary>
    /// Political and legal participation plus settlement membership.
    ///
    ///  - Settlement membership: a citizen is tied to one of the real settlements
    ///    on the map (observational - NPCs cannot hold real citizenship without a
    ///    User, so the tie is a simulated membership that still reacts to real
    ///    settlement state through the civic snapshot).
    ///  - Voting: every few game days the citizen "votes" in an active election,
    ///    choosing the option that fits its profession (producers vote growth,
    ///    labourers welfare, scholars research...). Votes are recorded in the
    ///    citizen's own civic record and influence its legal preference; the mod
    ///    never mutates real elections.
    /// </summary>
    public static class CitizenCivics
    {
        /// <summary>Assigns a real settlement as the citizen's home settlement if missing.</summary>
        public static void EnsureSettlement(Citizen c, CivicSnapshot civic)
        {
            if (!string.IsNullOrEmpty(c.SettlementName)) return;
            if (!civic.HasCivics) return;

            try
            {
                foreach (var deed in PropertyManager.Obj.Deeds)
                {
                    if (deed == null) continue;
                    var s = deed.CachedOwningSettlement;
                    if (s == null) continue;
                    c.SettlementName = s.Name;
                    CitizenSocial.AddTrail(c, $"joined settlement {s.Name}");
                    return;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EnsureSettlement failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Registers a simulated vote when an active election is running and the
        /// voting cooldown has passed. Returns the chosen option, or null.
        /// </summary>
        public static string TickVoting(Citizen c, CivicSnapshot civic, double votingIntervalDays)
        {
            if (!civic.HasCivics || civic.ActiveElectionCount <= 0) return null;
            try
            {
                double today = TimeUtil.Days;
                if (today - c.LastVoteDay < votingIntervalDays) return null;

                string choice = SimulationLogic.VotePreferenceForProfession(c.Profession);
                c.LastVoteDay = today;
                c.LastVotedChoice = choice;
                c.LegalPreference = LegalForChoice(choice);
                CitizenSocial.AddTrail(c, $"voted for {choice} in a settlement election");
                return choice;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("TickVoting failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Approximates the citizen's legal stance from its vote choice.</summary>
        public static string LegalForChoice(string choice)
        {
            if (choice == null) return "Neutral";
            if (choice == "Trade" || choice == "Industry") return "Pro-market";
            if (choice == "Agriculture" || choice == "Housing") return "Pro-development";
            if (choice == "Research") return "Pro-science";
            if (choice == "Welfare") return "Pro-welfare";
            if (choice == "Growth") return "Pro-growth";
            return "Neutral";
        }
    }
}
