using System;
using System.Collections.Generic;
using System.Linq;

namespace EcoSimCitizens
{
    /// <summary>Multi-owner corporation with equity shares and governance votes.</summary>
    public class Corporation
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Founder { get; set; }
        public Dictionary<string, double> Owners { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); // name -> shares
        public Dictionary<string, string> Employees { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // name -> role
        public Dictionary<string, double> Votes { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); // choice -> equity weight
        public double Assets { get; set; }
        public double Liabilities { get; set; }
        public double DividendPool { get; set; }

        public int TotalShares => (int)Math.Round(Owners.Values.Sum());
        public string LastVoteWinner { get; set; }
    }

    /// <summary>
    /// PRD Module 4 (advanced financial systems, Req.15). Corporations pool
    /// ownership, hold assets, vote on governance weighted by equity, and pay
    /// hourly or milestone wages to employees. Citizens join corporations that
    /// match their profession and share in dividends.
    /// </summary>
    public class CorporationSystem
    {
        public List<Corporation> Corporations { get; } = new List<Corporation>();
        public long DividendsPaid { get; private set; }

        public Corporation Create(string name, string founder, double foundingCapital)
        {
            var corp = new Corporation
            {
                Id = "corp_" + Guid.NewGuid().ToString("N")[..8],
                Name = name,
                Founder = founder,
                Assets = foundingCapital
            };
            corp.Owners[founder] = 100;
            Corporations.Add(corp);
            return corp;
        }

        public Corporation Find(string idOrName)
            => Corporations.FirstOrDefault(c =>
                string.Equals(c.Id, idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Name, idOrName, StringComparison.OrdinalIgnoreCase));

        public bool IssueShares(Corporation corp, string owner, double shares, double pricePerShare)
        {
            if (corp == null || shares <= 0) return false;
            corp.Owners[owner] = corp.Owners.TryGetValue(owner, out double cur) ? cur + shares : shares;
            corp.Assets += shares * pricePerShare;
            return true;
        }

        public void CastVote(Corporation corp, string owner, string choice)
        {
            if (corp == null || !corp.Owners.TryGetValue(owner, out double shares)) return;
            corp.Votes[choice] = corp.Votes.TryGetValue(choice, out double v) ? v + shares : shares;
        }

        public string ResolveVote(Corporation corp)
        {
            if (corp == null) return null;
            var winner = SimulationLogic.EquityVoteWinner(
                corp.Votes.Select(kv => (kv.Key, kv.Value, kv.Key)).ToList());
            corp.LastVoteWinner = winner;
            corp.Votes.Clear();
            return winner;
        }

        public bool Hire(Corporation corp, Citizen c, double hourlyWage)
        {
            if (corp == null || c == null) return false;
            double annualCost = hourlyWage * 8.0 * 30.0;
            double expectedGain = 3000.0; // rough value of one worker
            if (!SimulationLogic.ShouldHire(corp.Assets, annualCost, expectedGain)) return false;
            corp.Employees[c.Name] = SimulationLogic.PartyTaskFor(c.Profession);
            c.CorporationId = corp.Id;
            c.CorporationShares = 0;
            return true;
        }

        public void PayDividends(Corporation corp, double amount)
        {
            if (corp == null || amount <= 0 || corp.DividendPool < amount) return;
            corp.DividendPool -= amount;
            DividendsPaid++;
        }

        /// <summary>Adds profit from a citizen's work to their corporation and returns the dividend share.</summary>
        public double TickCorporationWork(Citizen c, double profit, double dividendRate)
        {
            var corp = Find(c.CorporationId);
            if (corp == null || profit <= 0) return 0;
            corp.Assets += profit * 0.6;
            corp.DividendPool += profit * dividendRate;
            double share = 0;
            if (c.CorporationShares > 0 && corp.TotalShares > 0)
            {
                share = corp.DividendPool * (c.CorporationShares / (double)corp.TotalShares) * 0.05;
                c.LedgerBalance += share;
            }
            return share;
        }
    }
}
