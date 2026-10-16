using System;
using System.Collections.Generic;
using Eco.Core;
using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Store;
using Eco.Gameplay.Economy;
using Eco.Gameplay.Objects;

namespace EcoSimCitizens
{
    /// <summary>
    /// Real-money layer for NPCs. Each citizen gets a real in-game BankAccount and
    /// spends real currency: buying food transfers actual money from the citizen's
    /// account into the store's real bank account. This makes NPC demand visible in
    /// the live economy instead of living only inside the mod's JSON ledger.
    ///
    /// NOTE: NPC accounts are funded with a simulated starting grant, because Eco
    /// has no native "NPC user" concept; every operation is guarded so the mod
    /// degrades to observational mode if the economy rejects the transaction.
    /// </summary>
    public static class CitizenEconomy
    {
        private static readonly Dictionary<string, BankAccount> Accounts = new(StringComparer.OrdinalIgnoreCase);

        public static BankAccount AccountFor(string citizenName)
        {
            if (!Accounts.TryGetValue(citizenName, out var acc))
            {
                acc = new BankAccount();
                Accounts[citizenName] = acc;
            }
            return acc;
        }

        /// <summary>Grants the citizen its starting balance in the first store's currency.</summary>
        public static void GrantStartFunds(string citizenName, float amount)
        {
            try
            {
                var cur = FindStoreCurrency();
                if (cur == null) return;
                AccountFor(citizenName).AddCurrency(cur, amount, false);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("GrantStartFunds failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Finds the cheapest priced food offer across all real stores and moves real
        /// money from the citizen's account to the store's bank account.
        /// </summary>
        public static bool TryBuyCheapestFood(string citizenName, out float spent)
        {
            spent = 0;
            try
            {
                var acc = AccountFor(citizenName);
                float best = float.MaxValue;
                StoreComponent bestStore = null;
                foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
                {
                    if (wo == null || wo.IsDestroyed) continue;

                    StoreComponent store = null;
                    try { store = wo.GetComponent("StoreComponent") as StoreComponent; }
                    catch { }

                    if (store == null || store.IsBarter || store.BankAccount == null) continue;
                    foreach (var offer in store.AllOffers)
                    {
                        if (offer == null || !offer.HasPrice || !offer.TagIsFoodTag) continue;
                        if (offer.Price > 0 && offer.Price < best)
                        {
                            best = offer.Price;
                            bestStore = store;
                        }
                    }
                }

                if (bestStore == null || best == float.MaxValue) return false;
                var cur = bestStore.Currency;
                if (cur == null) return false;

                // Real money movement: citizen pays, store account receives.
                acc.AddCurrency(cur, -best, false);
                bestStore.BankAccount.AddCurrency(cur, best, false);
                spent = best;
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("Buy food failed: " + ex.Message);
                return false;
            }
        }

        private static Currency FindStoreCurrency()
        {
            foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
            {
                if (wo == null || wo.IsDestroyed) continue;
                try
                {
                    var store = wo.GetComponent("StoreComponent") as StoreComponent;
                    if (store != null && store.Currency != null) return store.Currency;
                }
                catch { }
            }
            return null;
        }
    }
}
