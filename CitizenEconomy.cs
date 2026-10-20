using System;
using System.Collections.Generic;
using Eco.Gameplay.Economy;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// NPC money layer. Every citizen gets a native BankAccount registered through
    /// BankAccountManager.Registrar (NativeCurrencyBridge.NpcAccount) and trades
    /// settle mod-internally: money moves from buyer's account to seller's account
    /// with AddCurrency on both sides - never StoreComponent.PerformTrade, never
    /// real stores. Grants use the registered settlement currency so the currency
    /// appears in the game's native currency list.
    ///
    /// When no native currency/account can be registered (e.g. server with no
    /// users), operations degrade to ledger-only simulation so NPCs still function.
    /// </summary>
    public static class CitizenEconomy
    {
        /// <summary>
        /// Gets or creates the native bank account for an NPC via
        /// BankAccountManager.Registrar. The account is registered in the banking
        /// system (visible/enumerable); identified by "EcoSim-&lt;name&gt;".
        /// </summary>
        public static BankAccount AccountFor(string citizenName)
            => NativeCurrencyBridge.NpcAccount(citizenName);

        /// <summary>Grants the citizen its starting balance in the registered settlement currency.</summary>
        public static void GrantStartFunds(string citizenName, float amount)
        {
            try
            {
                var cur = NativeCurrencyBridge.DefaultCurrency();
                var acc = AccountFor(citizenName);
                if (cur == null || acc == null) return;
                acc.AddCurrency(cur, amount, false);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("GrantStartFunds failed: " + ex.Message);
            }
        }

        /// <summary>
        /// NPC-to-NPC simulated settlement: moves money from buyer's account into
        /// seller's account (both registered native accounts when available) and
        /// mirrors the balances in the citizen ledgers. Returns false when the buyer
        /// cannot pay. Never touches a real store or StoreComponent.
        /// </summary>
        public static bool TransferNpcFunds(Citizen from, Citizen to, double amount)
        {
            if (from == null || to == null || amount <= 0) return false;
            try
            {
                var cur = NativeCurrencyBridge.DefaultCurrency();
                if (cur != null)
                {
                    var buyer = AccountFor(from.Name);
                    var seller = AccountFor(to.Name);
                    if (buyer != null && seller != null)
                    {
                        float amt = (float)amount;
                        if (buyer.GetCurrencyHoldingVal(cur) < amt) return false;
                        buyer.AddCurrency(cur, -amt, false);
                        seller.AddCurrency(cur, amt, false);
                    }
                    else
                    {
                        // ledger-only fallback when accounts unavailable
                        if (from.LedgerBalance < amount) return false;
                    }
                }
                else
                {
                    if (from.LedgerBalance < amount) return false;
                }

                from.LedgerBalance -= amount;
                to.LedgerBalance += amount;
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warn("TransferNpcFunds failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Balance of an NPC account in the registered currency (0 when unavailable).</summary>
        public static float NativeBalance(string citizenName)
        {
            var cur = NativeCurrencyBridge.DefaultCurrency();
            return NativeCurrencyBridge.NpcBalance(citizenName, cur);
        }
    }
}
