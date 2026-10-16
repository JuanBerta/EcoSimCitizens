using System;
using System.Collections.Generic;
using System.Linq;

namespace EcoSimCitizens
{
    /// <summary>Listed corporation traded on the simulated exchange.</summary>
    public class ListedCompany
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public double Shares { get; set; }
        public double Assets { get; set; }
        public double Liabilities { get; set; }
        public double LastPrice { get; set; }
    }

    /// <summary>Futures contract on a commodity; price driven by real market scarcity.</summary>
    public class FuturesContract
    {
        public string Id { get; set; }
        public string Commodity { get; set; }
        public double BasePrice { get; set; }
        public double Supply { get; set; }
        public double Demand { get; set; }
        public double LastPrice { get; set; }
        public double Volatility { get; set; }
    }

    /// <summary>
    /// PRD Module 4 (securities &amp; futures exchange, Req.16). Simulated stock
    /// exchange: corporations list shares valued from assets, futures contracts on
    /// staple commodities (Iron Bars, Oil) are priced from scarcity (supply vs
    /// demand). Citizens trade when contracts are underpriced versus expected
    /// future value, adding a real speculative layer to their behaviour.
    /// </summary>
    public class StockExchangeSystem
    {
        public List<ListedCompany> Listings { get; } = new List<ListedCompany>();
        public List<FuturesContract> Futures { get; } = new List<FuturesContract>();
        public long ContractsBought { get; private set; }

        private readonly Random _rng = new Random();

        public ListedCompany ListCompany(Corporation corp)
        {
            var listed = new ListedCompany
            {
                Id = corp.Id,
                Name = corp.Name,
                Shares = Math.Max(1, corp.TotalShares),
                Assets = corp.Assets,
                Liabilities = corp.Liabilities
            };
            listed.LastPrice = SimulationLogic.ShareValue(listed.Assets, listed.Liabilities, (int)listed.Shares);
            Listings.RemoveAll(l => string.Equals(l.Id, corp.Id, StringComparison.OrdinalIgnoreCase));
            Listings.Add(listed);
            return listed;
        }

        public FuturesContract AddFuture(string commodity, double basePrice)
        {
            var f = new FuturesContract
            {
                Id = "fut_" + commodity.Replace(" ", "").ToLowerInvariant(),
                Commodity = commodity,
                BasePrice = Math.Max(0.01, basePrice),
                Supply = 100,
                Demand = 100,
                Volatility = _rng.NextDouble() * 0.2 - 0.1
            };
            Futures.RemoveAll(x => string.Equals(x.Id, f.Id, StringComparison.OrdinalIgnoreCase));
            Futures.Add(f);
            return f;
        }

        /// <summary>Updates prices every tick from scarcity and volatility.</summary>
        public void TickPrices()
        {
            foreach (var l in Listings)
            {
                l.LastPrice = SimulationLogic.ShareValue(l.Assets, l.Liabilities, (int)Math.Max(1, l.Shares));
            }
            foreach (var f in Futures)
            {
                f.Volatility = _rng.NextDouble() * 0.3 - 0.15;
                f.LastPrice = SimulationLogic.FutureContractPrice(f.BasePrice, SimulationLogic.ScarcityFromSupplyDemand(f.Supply, f.Demand), f.Volatility);
            }
        }

        /// <summary>Feeds real observed market activity into the futures scarcity model.</summary>
        public void ObserveSupplyDemand(string commodity, double supply, double demand)
        {
            var f = Futures.FirstOrDefault(x => string.Equals(x.Commodity, commodity, StringComparison.OrdinalIgnoreCase));
            if (f == null) return;
            f.Supply = Math.Max(0, supply);
            f.Demand = Math.Max(0, demand);
        }

        /// <summary>Citizen speculates: buys futures when the contract is underpriced.</summary>
        public bool TryBuyFuture(Citizen c, string commodity, double riskTolerance)
        {
            var f = Futures.FirstOrDefault(x => string.Equals(x.Commodity, commodity, StringComparison.OrdinalIgnoreCase));
            if (f == null || f.LastPrice <= 0) return false;
            double expected = f.BasePrice * 1.2;
            if (!SimulationLogic.ShouldBuyFuture(f.LastPrice, expected, riskTolerance)) return false;
            double cost = f.LastPrice * 10;
            if (c.LedgerBalance < cost) return false;
            c.LedgerBalance -= cost;
            c.FuturesHeld[commodity] = c.FuturesHeld.TryGetValue(commodity, out double cur) ? cur + 10 : 10;
            ContractsBought++;
            return true;
        }
    }
}
