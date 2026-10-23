using System;
using System.Collections.Generic;
using Eco.Core.Systems;
using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Store;
using Eco.Gameplay.Items;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Players;
using Eco.Shared.Serialization;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// v2.0: real-store trading for offline citizen users, modelled on Rabotnik's
    /// RabotnikShopping (CC BY 4.0, github.com/LtChae/eco-rabotnik): shops are
    /// discovered with WorldObjectManager.ForEach + StoreComponent (no Credit
    /// dependency), the cart is built as BSON and settled through
    /// StoreComponent.DoPerformTrade(user, trade, user.BankAccount) so access,
    /// stock, price, tax and inventory moves are all vanilla. When the real-store
    /// path is disabled, fails or no store offers food, the caller falls back to
    /// the v1.2 SimStore simulated settlement (EnableRealStoreTrade=false is the
    /// documented rollback).
    /// </summary>
    public static class CitizenStoreTrade
    {
        private const long StoreCacheMs = 10000; // refresh the full-world store scan every 10 s
        private static readonly object Sync = new object();
        private static List<StoreComponent> _storeCache = new List<StoreComponent>();
        private static long _cacheTick = -1;

        /// <summary>
        /// Buys the cheapest affordable food for the citizen's offline user from a
        /// real store. Returns true and sets spent when a trade succeeded. When the
        /// real-store path is unavailable this returns false so the caller can
        /// fall back to SimStoreManager.TryBuyFood.
        /// </summary>
        public static bool TryBuyFood(Citizen c, CitizensDatabase db, ModConfig cfg, out float spent)
        {
            spent = 0;
            if (!cfg.EnableRealStoreTrade || !cfg.SimShopsEnabled || c == null) return false;
            try
            {
                var user = CitizenUserManager.EnsureOfflineUser(c, cfg);
                if (user == null || user.BankAccount == null) return false;

                double spendable = CitizenBudget.Spendable(c, user, cfg);
                if (spendable <= 0) return false;

                var stores = OpenStores();
                if (stores.Count == 0) return false;

                var best = BestOffer(user, stores, spendable);
                if (best == null) return false;

                int bought = Buy(user, best, out string error);
                if (bought <= 0)
                {
                    PluginLog.Info($"EcoSimCitizens: real-store buy failed for {c.Name} ({error}); falling back to sim store.");
                    return SimStoreManager.TryBuyFood(c, db, cfg, out spent);
                }

                float price = best.Price * bought;
                c.TradeBuyVolume++;
                c.LedgerBalance = Math.Max(0, c.LedgerBalance - price);
                PluginLog.Info($"EcoSimCitizens: real-store trade: {c.Name} bought {bought} {best.ItemName} from '{best.StoreName}' for {price:0.0} (native bank account).");
                spent = price;
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"EcoSimCitizens: CitizenStoreTrade.TryBuyFood failed for '{c?.Name}': {ex.Message}");
                return SimStoreManager.TryBuyFood(c, db, cfg, out spent);
            }
        }

        private static List<StoreComponent> OpenStores()
        {
            lock (Sync)
            {
                long now = Environment.TickCount;
                if (_cacheTick < 0 || now - _cacheTick > StoreCacheMs)
                {
                    _storeCache = ScanStores();
                    _cacheTick = now;
                }
                return new List<StoreComponent>(_storeCache);
            }
        }

        private static List<StoreComponent> ScanStores()
        {
            var stores = new List<StoreComponent>();
            WorldObjectManager.ForEach(o =>
            {
                try
                {
                    var s = o.GetComponent<StoreComponent>();
                    if (s == null || !o.Enabled || !s.OnOff.On || s.Currency == null || s.BankAccount == null) return;
                    if (s.StoreData == null || s.StoreData.SellOffers == null) return;
                    stores.Add(s);
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"EcoSimCitizens: store scan skipped an object: {ex.Message}");
                }
            });
            return stores;
        }

        private sealed class Offer
        {
            public StoreComponent Store;
            public TradeOffer Trade;
            public string ItemName;
            public string StoreName;
            public float Price;
            public int Quantity;
        }

        /// <summary>
        /// Cheapest affordable food offer across all open stores, bounded by the
        /// citizen's spendable budget (reserved food/rent kept out, see
        /// CitizenBudget.Spendable) and the store's own stock.
        /// </summary>
        private static Offer BestOffer(User user, List<StoreComponent> stores, double spendable)
        {
            Offer best = null;
            foreach (var store in stores)
            {
                float balance = user.BankAccount.GetCurrencyHoldingVal(store.Currency);
                foreach (var offer in store.StoreData.SellOffers)
                {
                    try
                    {
                        if (offer == null || offer.Buying || offer.IsTagOffer || offer.Price <= 0
                            || offer.Stack == null || offer.Stack.Quantity <= 0) continue;
                        var item = offer.Stack.Item;
                        if (item is not FoodItem) continue;
                        int affordable = (int)(Math.Min(balance, spendable) / offer.Price);
                        int quantity = Math.Min(offer.Stack.Quantity, affordable);
                        if (quantity <= 0) continue;
                        if (best == null || offer.Price < best.Price)
                        {
                            best = new Offer
                            {
                                Store = store,
                                Trade = offer,
                                ItemName = item.Name ?? item.GetType().Name,
                                StoreName = store.Parent?.Name ?? store.GetType().Name,
                                Price = offer.Price,
                                Quantity = quantity
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warn($"EcoSimCitizens: offer scan skipped: {ex.Message}");
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// BSON cart + DoPerformTrade (same settlement path as RabotnikShopping.cs
        /// L122-140). The ArgumentException guard mirrors Rabotnik: the client
        /// RPC notice is thrown after the trade is already applied and has no
        /// client to deliver to.
        /// </summary>
        private static int Buy(User user, Offer pick, out string error)
        {
            error = null;
            try
            {
                var line = BSONObject.New;
                line["offer"] = pick.Trade.GetOrCreateID();
                line["amount"] = pick.Quantity;
                var buying = BSONArray.New;
                buying.Add(line);
                var trade = BSONObject.New;
                trade["itemsToBuy"] = buying;
                trade["itemsToSell"] = BSONArray.New;
                var result = pick.Store.DoPerformTrade(user, trade, user.BankAccount);
                if (!result.Success) { error = result.Message; return 0; }
                return pick.Quantity;
            }
            catch (ArgumentException e) when (e.Message.Contains("RPC"))
            {
                // trade applied server-side; client notice has no client - count it as success
                return pick.Quantity;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return 0;
            }
        }
    }

    /// <summary>
    /// v2.0: spendable budget for an offline citizen user, modelled on Rabotnik's
    /// RabotnikBudget.Spendable (CC BY 4.0): the reserve for rent and food is kept
    /// out of the spendable amount. EcoSimCitizens v2.0 has no rent yet, so
    /// BudgetRentReserve defaults to 0 and is reserved for the next version.
    /// </summary>
    public static class CitizenBudget
    {
        public static double Spendable(Citizen c, User user, ModConfig cfg)
        {
            try
            {
                var cur = NativeCurrencyBridge.DefaultCurrency();
                double balance;
                if (user != null && user.BankAccount != null && cur != null)
                    balance = user.BankAccount.GetCurrencyHoldingVal(cur);
                else
                    balance = c.LedgerBalance;
                double reserved = cfg.BudgetFoodReserve + cfg.BudgetRentReserve;
                return Math.Max(0, balance - reserved);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenBudget.Spendable failed: " + ex.Message);
                return c.LedgerBalance;
            }
        }
    }
}
