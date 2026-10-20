using System;
using System.Collections.Generic;
using System.Numerics;
using Eco.Core;
using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Store;
using Eco.Gameplay.Objects;

namespace EcoSimCitizens
{
    /// <summary>Aggregated picture of the live player-driven market.</summary>
    public class MarketSnapshot
    {
        /// <summary>World positions of every real player store seen on the last scan.</summary>
        public List<Vector3> StorePositions = new List<Vector3>();

        /// <summary>World positions of simulated NPC stores (mod-internal shops).</summary>
        public List<Vector3> SimStorePositions = new List<Vector3>();

        public int StoreCount;
        public int SimStoreCount;
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
    /// contributes its offers. Purely observational - the mod never mutates stores
    /// and NPCs never trade with them. Simulated NPC stores are also scanned into
    /// the snapshot so NPC shopping goals target mod-internal shops only.
    /// </summary>
    public class MarketScanner
    {
        private double _lastAvgPrice = -1;

        /// <summary>
        /// Unified tradability predicate for real stores. A store is tradable for
        /// real players when the full StoreComponent is present and active:
        /// StoreData configured, switched on, not barter, bank account attached and
        /// a settlement currency configured. NPCs never buy from these - they only
        /// feed market data.
        /// </summary>
        public static bool IsTradableStore(StoreComponent store)
        {
            if (store == null) return false;
            try
            {
                if (store.StoreData == null) return false;
                if (store.IsBarter) return false;
                if (store.OnOff == null || !store.OnOff.On) return false;
                if (store.BankAccount == null) return false;
                if (store.Currency == null) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public MarketSnapshot Scan(CitizensDatabase db = null)
        {
            var snap = new MarketSnapshot();
            double sum = 0;
            double min = double.MaxValue;
            double max = 0;

            try
            {
                foreach (var store in WorldObjectUtil.AllObjsWithComponent<StoreComponent>())
                {
                    if (store == null || store.Parent == null || store.Parent.IsDestroyed) continue;
                    if (!IsTradableStore(store)) continue;
                    var wo = store.Parent;
                    snap.StoreCount++;
                    snap.StorePositions.Add(wo.Position);

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

                // simulated NPC stores: world positions are the NPC's home/stand
                if (db != null)
                {
                    foreach (var s in db.SimStoresSnapshot())
                    {
                        if (s == null || !s.IsOpen) continue;
                        snap.SimStoreCount++;
                        snap.SimStorePositions.Add(new Vector3(s.X, s.Y, s.Z));
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
