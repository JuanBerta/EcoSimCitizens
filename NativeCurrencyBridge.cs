using System;
using System.Collections.Generic;
using System.Linq;
using Eco.Gameplay.Economy;
using Eco.Gameplay.Players;
using Eco.Shared.Items;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// Bridge between the mod's simulated economy and Eco's native economy:
    ///  1. registers settlement currencies as real Eco currencies via
    ///     CurrencyManager.AddCurrency(User, string, CurrencyType) so they appear
    ///     in the game's currency list;
    ///  2. registers every NPC's bank account through BankAccountManager.Registrar
    ///     so NPC accounts are visible/enumerable in the native banking system.
    ///
    /// NPC-vs-NPC trades never call StoreComponent.PerformTrade: the money only
    /// moves between the two NPC BankAccounts (AddCurrency on both sides), which is
    /// the mod-internal simulated settlement requested by the design decision.
    ///
    /// Eco has no native "NPC user" concept, so NPC accounts are created directly
    /// through the registrar (TryCreateUserAccount requires a real User and is only
    /// used for real players). All failures degrade to ledger-only simulation.
    /// </summary>
    public static class NativeCurrencyBridge
    {
        private const string DefaultCurrencyName = "EcoSim Currency";
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Currency> Registered = new Dictionary<string, Currency>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the default sim currency, registering it as a native Backed
        /// currency on first use. Returns null if no user is available to act as
        /// creator (trades then fall back to ledger-only simulation).
        /// </summary>
        public static Currency DefaultCurrency()
        {
            lock (Sync)
            {
                if (Registered.TryGetValue(DefaultCurrencyName, out var cur) && cur != null) return cur;
                var c = EnsureNativeCurrency(DefaultCurrencyName, CurrencyType.Backed);
                if (c != null) Registered[DefaultCurrencyName] = c;
                return c;
            }
        }

        /// <summary>
        /// Ensures a settlement currency exists as a native Eco currency, creating
        /// it with CurrencyManager.AddCurrency when missing. Idempotent - existing
        /// currencies are reused (matched by closest name).
        /// </summary>
        public static Currency EnsureSettlementCurrency(string settlementName)
        {
            if (string.IsNullOrEmpty(settlementName)) return null;
            lock (Sync)
            {
                if (Registered.TryGetValue(settlementName, out var cur) && cur != null) return cur;
                var c = EnsureNativeCurrency(settlementName, CurrencyType.Backed);
                if (c != null) Registered[settlementName] = c;
                return c;
            }
        }

        private static Currency EnsureNativeCurrency(string name, CurrencyType type)
        {
            try
            {
                var existing = CurrencyManager.GetClosestCurrency(name);
                if (existing != null) return existing;
                var creator = FirstUser();
                if (creator == null)
                {
                    PluginLog.Warn($"EcoSimCitizens: no user available to register native currency '{name}'; simulation will run ledger-only.");
                    return null;
                }
                var created = CurrencyManager.AddCurrency(creator, name, type);
                if (created != null)
                {
                    PluginLog.Info($"EcoSimCitizens: native currency '{created.Name}' registered via CurrencyManager.AddCurrency (type={type}).");
                }
                return created;
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"EcoSimCitizens: native currency registration for '{name}' failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets or creates the native BankAccount for an NPC through
        /// BankAccountManager.Registrar. The account is registered in the game's
        /// banking system so it is visible/enumerable, but it has no User owner
        /// (Eco has no NPC user); it is identified by the "EcoSim-&lt;name&gt;" account name.
        /// </summary>
        public static BankAccount NpcAccount(string citizenName)
        {
            if (string.IsNullOrEmpty(citizenName)) return null;
            lock (Sync)
            {
                try
                {
                    var reg = BankAccountManager.Obj.Registrar;
                    string accName = "EcoSim-" + citizenName;
                    var existing = reg.GetByName(accName);
                    if (existing != null) return existing;
                    BankAccount created = null;
                    reg.Add(typeof(BankAccount), null, accName, a => { created = a as BankAccount; });
                    if (created != null)
                    {
                        PluginLog.Info($"EcoSimCitizens: NPC bank account '{accName}' registered through BankAccountManager.Registrar.");
                    }
                    return created;
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"EcoSimCitizens: NPC account creation for '{citizenName}' failed: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>Balance of an NPC account in the given currency (0 when no account yet).</summary>
        public static float NpcBalance(string citizenName, Currency cur)
        {
            try
            {
                var acc = NpcAccount(citizenName);
                return acc == null || cur == null ? 0f : acc.GetCurrencyHoldingVal(cur);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("NpcBalance failed: " + ex.Message);
                return 0f;
            }
        }

        /// <summary>
        /// Ensures a native currency using an explicit creator User (the invoker
        /// player). Used by homestead claims so the claim is visible in the native
        /// currency list with the player as creator - the one native call that
        /// takes the invoker's User directly.
        /// </summary>
        public static Currency EnsureCurrencyWithCreator(string name, User creator)
        {
            if (string.IsNullOrEmpty(name) || creator == null) return null;
            lock (Sync)
            {
                if (Registered.TryGetValue(name, out var cur) && cur != null) return cur;
                var c = TryCreateWithCreator(name, creator);
                if (c != null) Registered[name] = c;
                return c;
            }
        }

        private static Currency TryCreateWithCreator(string name, User creator)
        {
            try
            {
                var existing = CurrencyManager.GetClosestCurrency(name);
                if (existing != null) return existing;
                var created = CurrencyManager.AddCurrency(creator, name, CurrencyType.Backed);
                if (created != null)
                {
                    PluginLog.Info($"EcoSimCitizens: native currency '{created.Name}' registered with invoker '{creator.Name}' as creator.");
                }
                return created;
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"EcoSimCitizens: currency creation for '{name}' with invoker failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>First available user that can act as creator for currency registration.</summary>
        public static User FirstUser()
        {
            try
            {
                var online = UserManager.Users.FirstOrDefault(u => u != null && u.IsOnline);
                if (online != null) return online;
                return UserManager.Users.FirstOrDefault(u => u != null);
            }
            catch
            {
                return null;
            }
        }
    }
}
