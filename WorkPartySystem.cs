using System;
using System.Collections.Generic;
using System.Linq;

namespace EcoSimCitizens
{
    /// <summary>One work party: a contract group of citizens with a shared task and pool.</summary>
    public class WorkParty
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Task { get; set; }
        public double Pool { get; set; }
        public double TargetProgress { get; set; }       // progress needed to unlock the milestone
        public double Progress { get; set; }
        public Dictionary<string, double> Contributions { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Members { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> profession

        public int MemberCount => Members.Count;
    }

    /// <summary>
    /// PRD Module 2 (contracts &amp; work parties, Req.7). Parties pool wages,
    /// advance a shared milestone, and pay contributors proportionally to their
    /// contribution when the milestone completes.
    /// </summary>
    public class WorkPartySystem
    {
        public List<WorkParty> Parties { get; } = new List<WorkParty>();
        public long MilestonesCompleted { get; private set; }
        public double TotalPoolPaid { get; private set; }

        public WorkParty CreateParty(string name, string task, double initialPool, double targetProgress)
        {
            var p = new WorkParty
            {
                Id = "wp_" + Guid.NewGuid().ToString("N")[..8],
                Name = name,
                Task = string.IsNullOrEmpty(task) ? "General" : task,
                Pool = initialPool,
                TargetProgress = Math.Max(1.0, targetProgress),
                Progress = 0
            };
            Parties.Add(p);
            return p;
        }

        public WorkParty Find(string idOrName)
        {
            return Parties.FirstOrDefault(p =>
                string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        }

        public bool Join(WorkParty party, Citizen c)
        {
            if (party == null || c == null || party.Members.ContainsKey(c.Name)) return false;
            party.Members[c.Name] = c.Profession;
            c.WorkPartyId = party.Id;
            return true;
        }

        /// <summary>
        /// Advances the party's shared milestone by the citizen's work output.
        /// When complete, the pool is split by contribution and cleared.
        /// Returns a map of member name -&gt; payment (empty when the milestone is
        /// still in progress). The simulation loop applies the payments.
        /// </summary>
        public Dictionary<string, double> Contribute(Citizen c, double output)
        {
            var party = Find(c.WorkPartyId);
            if (party == null || output <= 0) return new Dictionary<string, double>();

            double contribution = output;
            party.Progress = Math.Min(party.TargetProgress, party.Progress + contribution);
            party.Contributions[c.Name] = party.Contributions.TryGetValue(c.Name, out double prev) ? prev + contribution : contribution;
            c.LedgerBalance -= contribution * 0.5; // labour cost: half the output is deducted as upkeep

            if (party.Progress >= party.TargetProgress)
            {
                double total = party.Contributions.Values.Sum();
                var payments = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in party.Contributions)
                {
                    payments[kv.Key] = SimulationLogic.MilestoneShare(party.Pool, kv.Value, total);
                }
                MilestonesCompleted++;
                TotalPoolPaid += party.Pool;
                party.Pool = 0;
                party.Progress = 0;
                party.Contributions.Clear();
                return payments;
            }
            return new Dictionary<string, double>();
        }

        public void Withdraw(Citizen c)
        {
            if (string.IsNullOrEmpty(c.WorkPartyId)) return;
            var party = Find(c.WorkPartyId);
            if (party != null)
            {
                party.Members.Remove(c.Name);
                party.Contributions.Remove(c.Name);
            }
            c.WorkPartyId = null;
        }
    }
}
