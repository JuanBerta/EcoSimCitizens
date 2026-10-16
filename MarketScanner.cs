using System;
using System.Collections.Generic;
using Eco.Core;
using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Store;
using Eco.Gameplay.Objects;

namespace EcoSimCitizens
{
    /// <summary>Aggregated picture of the live player-driven market.</summary>
    public class MarketSnapshot
    {
        public int StoreCount;
        public int OfferCount;
        public int PricedOfferCount;
        public int FoodOfferCount;
        public double AvgPrice;
        public double MinPrice;
        public double MaxPrice;
        public double TrendPercent;   // change of AvgPrice vs previous tick, percent

        public bool HasMarket => StoreCount > 0;
    }

    /// <summary>
    /// Reads the real in-game economy: every world object with a StoreComponent
    /// contributes its offers. Purely observational - the mod never mutates stores.
    /// </summary>
    public class MarketScanner
    {
        private double _lastAvgPrice = -1;

        public MarketSnapshot Scan()
        {
            var snap = new MarketSnapshot();
            double sum = 0;
            double min = double.MaxValue;
            double max = 0;

            try
            {
                foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
                {
                    if (wo == null || wo.IsDestroyed) continue;

                    StoreComponent store = null;
                    try { store = wo.GetComponent("StoreComponent") as StoreComponent; }
                    catch { }

                    if (store == null) continue;
                    snap.StoreCount++;

                    foreach (var offer in store.AllOffers)
                    {
                        if (offer == null) continue;
                        snap.OfferCount++;

                        bool priced = offer.HasPrice;
                        if (!priced) continue;
                        snap.PricedOfferCount++;

                        double price = offer.Price;
                        sum += price;
                        if (price < min) min = price;
                        if (price > max) max = price;

                        if (offer.TagIsFoodTag) snap.FoodOfferCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("MarketScanner failed: " + ex.Message);
            }

            if (snap.PricedOfferCount > 0)
            {
                snap.AvgPrice = sum / snap.PricedOfferCount;
                snap.MinPrice = min;
                snap.MaxPrice = max;
            }
            else
            {
                snap.MinPrice = 0;
            }

            if (_lastAvgPrice > 0 && snap.AvgPrice > 0)
            {
                snap.TrendPercent = (snap.AvgPrice - _lastAvgPrice) / _lastAvgPrice * 100.0;
            }
            _lastAvgPrice = snap.AvgPrice > 0 ? snap.AvgPrice : _lastAvgPrice;
            return snap;
        }
    }
}
