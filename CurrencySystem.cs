using System;
using System.Collections.Generic;
using System.Linq;

namespace EcoSimCitizens
{
    /// <summary>Settlement-issued currency with supply, reserves and GDP backing.</summary>
    public class SettlementCurrency
    {
        public string SettlementName { get; set; }
        public string Symbol { get; set; }
        public double Supply { get; set; }
        public double BackingReserve { get; set; }
        public double Gdp { get; set; }
        public double Population { get; set; }
    }

    /// <summary>
    /// PRD Module 5 (settlement currencies &amp; forex, Req.3). Each settlement can
    /// mint its own currency backed by reserves and GDP. Cross rates between
    /// settlement currencies move every tick: stronger GDP and larger reserves
    /// appreciate a currency. Citizens hold balances in multiple currencies and
    /// convert when rates move in their favour.
    /// </summary>
    public class CurrencySystem
    {
        public List<SettlementCurrency> Currencies { get; } = new List<SettlementCurrency>();
        public long Conversions { get; private set; }

        public SettlementCurrency Find(string settlement)
            => Currencies.FirstOrDefault(c => string.Equals(c.SettlementName, settlement, StringComparison.OrdinalIgnoreCase));

        public SettlementCurrency Mint(string settlement, string symbol, double initialReserve)
        {
            var cur = Find(settlement);
            if (cur != null) return cur;
            cur = new SettlementCurrency
            {
                SettlementName = settlement,
                Symbol = string.IsNullOrEmpty(symbol) ? (settlement.Length > 3 ? settlement[..3].ToUpperInvariant() : "S$") : symbol,
                Supply = initialReserve,
                BackingReserve = initialReserve,
                Gdp = initialReserve * 2,
                Population = 1
            };
            Currencies.Add(cur);
            return cur;
        }

        public void TickCurrencies()
        {
            foreach (var c in Currencies)
            {
                c.Population = Math.Max(1, c.Population);
                c.Supply += SimulationLogic.MintSupplyGrowth(c.Population, c.BackingReserve);
                c.Gdp = Math.Max(c.BackingReserve, c.Gdp * 1.01);
            }
        }

        /// <summary>Rate of first vs second currency (units of second per unit of first).</summary>
        public double ForexRate(SettlementCurrency from, SettlementCurrency to)
        {
            if (from == null || to == null) return 1.0;
            if (string.Equals(from.SettlementName, to.SettlementName, StringComparison.OrdinalIgnoreCase)) return 1.0;
            double gdpRatio = to.Gdp > 0 ? from.Gdp / to.Gdp : 1.0;
            double reserveRatio = to.BackingReserve > 0 ? from.BackingReserve / to.BackingReserve : 1.0;
            return SimulationLogic.ForexRate(gdpRatio, reserveRatio, 1.0);
        }

        /// <summary>Converts a citizen's holding between two settlement currencies.</summary>
        public bool Convert(Citizen c, string fromSettlement, string toSettlement, double amount)
        {
            var from = Find(fromSettlement);
            var to = Find(toSettlement);
            if (from == null || to == null || amount <= 0) return false;
            if (!c.CurrencyHoldings.TryGetValue(from.SettlementName, out double have) || have < amount) return false;
            double rate = ForexRate(from, to);
            c.CurrencyHoldings[from.SettlementName] = have - amount;
            c.CurrencyHoldings[to.SettlementName] = c.CurrencyHoldings.TryGetValue(to.SettlementName, out double cur) ? cur + amount * rate : amount * rate;
            Conversions++;
            return true;
        }
    }
}
