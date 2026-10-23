using System;
using Eco.Gameplay.Economy;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// NPC money layer. Every citizen gets a native BankAccount and trades settle
    /// mod-internally: money moves from buyer's account to seller's account with
    /// AddCurrency on both sides - never StoreComponent.PerformTrade, never
    /// real stores. Grants use the registered settlement currency so the currency
    /// appears in the game's native currency list.
    ///
    /// v2.0 dual-carrier: when controlled offline citizen users are enabled, the
    /// "primary" account is the offline User's own native BankAccount (created via
    /// BankAccountManager.TryCreateUserAccount so BankAccount.Owner is set); the
    /// v1.2 registrar account ("EcoSim-&lt;name&gt;", no owner) becomes the fallback and
    /// its balance is migrated once. When disabled, everything degrades to the
    /// v1.2 ledger-only simulation (server with no users etc.).
    /// </summary>
    public static class CitizenEconomy
    {
        /// <summary>
        /// Gets the primary native bank account for a citizen: the offline User's
        /// account when citizen users are enabled, otherwise the legacy registrar
        /// account. Never returns an account without its owner initialized.
        /// </summary>
        public static BankAccount AccountFor(Citizen c, ModConfig cfg)
        {
            if (CitizenUserManager.IsEnabled(cfg) && c != null)
            {
                var user = CitizenUserManager.EnsureOfflineUser(c, cfg);
                if (user != null && user.BankAccount != null) return user.BankAccount;
            }
            return NativeCurrencyBridge.NpcAccount(c?.Name);
        }

        /// <summary>Grants the citizen its starting balance in the registered settlement currency.</summary>
        public static void GrantStartFunds(Citizen c, ModConfig cfg, float amount)
        {
            try
            {
                var cur = NativeCurrencyBridge.DefaultCurrency();
                var acc = AccountFor(c, cfg);
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
        /// seller's account (both primary native accounts when available) and
        /// mirrors the balances in the citizen ledgers. Returns false when the buyer
        /// cannot pay. Never touches a real store or StoreComponent.
        /// </summary>
        public static bool TransferNpcFunds(Citizen from, Citizen to, ModConfig cfg, double amount)
        {
            if (from == null || to == null || amount <= 0) return false;
            try
            {
                var cur = NativeCurrencyBridge.DefaultCurrency();
                if (cur != null)
                {
                    var buyer = AccountFor(from, cfg);
                    var seller = AccountFor(to, cfg);
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

        /// <summary>Balance of the citizen's primary account in the registered currency (0 when unavailable).</summary>
        public static float NativeBalance(Citizen c, ModConfig cfg)
        {
            var cur = NativeCurrencyBridge.DefaultCurrency();
            var acc = AccountFor(c, cfg);
            return acc == null || cur == null ? 0f : acc.GetCurrencyHoldingVal(cur);
        }
    }
}
