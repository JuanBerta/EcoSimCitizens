using System;
using System.Linq;
using Eco.Gameplay.Civics.Demographics;
using Eco.Gameplay.Economy;
using Eco.Gameplay.Players;
using Eco.Gameplay.Utils;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// v2.0: controlled offline-User factory. Every physical NPC (CitizenNpc) gets
    /// an "economic persona": a connectionless User created through the same
    /// TestUtils.MakeTestUser path Rabotnik uses (verified in game to persist
    /// across restarts with balances and citizenship). The User participates in the
    /// native banking / store / civic systems; the NPC WorldObject keeps rendering
    /// the physical presence (dual carrier).
    ///
    /// Governance: name prefix, hard cap, retirement pool, abandoned whitelist.
    /// Accounts are created with BankAccountManager.TryCreateUserAccount so
    /// BankAccount.Owner is initialized - this avoids the dangling-Owner NRE family
    /// and no ForceAdd of CreditComponent objects happens anywhere. When disabled,
    /// everything falls back to the v1.2 registrar-account ledger simulation.
    /// </summary>
    public static class CitizenUserManager
    {
        private static readonly object Sync = new object();

        public static bool IsEnabled(ModConfig cfg) => cfg != null && cfg.EnableCitizenUsers;

        /// <summary>True when the user name belongs to the mod's controlled prefix set.</summary>
        public static bool IsCitizenUserName(string name, ModConfig cfg)
            => !string.IsNullOrEmpty(name)
               && !string.IsNullOrEmpty(cfg?.UserPrefix)
               && name.StartsWith(cfg.UserPrefix + " ", StringComparison.Ordinal);

        /// <summary>
        /// Ensures the citizen is bound to an offline User, creating / repurposing
        /// one through the controlled factory. Returns the User, or null when
        /// disabled, capped or failed (callers then stay on the v1.2 ledger path).
        /// </summary>
        public static User EnsureOfflineUser(Citizen c, ModConfig cfg)
        {
            if (!IsEnabled(cfg) || c == null) return null;
            lock (Sync)
            {
                try
                {
                    // 1) existing binding: restart reuses the same User by name
                    if (!string.IsNullOrEmpty(c.OfflineUserName))
                    {
                        var bound = UserManager.FindUserByName(c.OfflineUserName);
                        if (bound != null && IsCitizenUserName(bound.Name, cfg))
                        {
                            c.OfflineUserName = bound.Name; // normalize
                            return AttachAccount(bound, c.Name, cfg);
                        }
                        // name taken by a real player or vanished: rebind below
                        c.OfflineUserName = null;
                    }

                    // 2) retirement pool: offline, prefixed, unbound Users are reused
                    var retired = UserManager.Users.FirstOrDefault(u => u != null
                        && string.IsNullOrEmpty(u.SteamId) && !u.IsOnline
                        && IsCitizenUserName(u.Name, cfg)
                        && !IsBoundByAny(u, cfg));
                    if (retired != null)
                    {
                        c.OfflineUserName = retired.Name;
                        PluginLog.Info($"EcoSimCitizens: bound NPC {c.Name} to retired offline user '{retired.Name}'.");
                        return AttachAccount(retired, c.Name, cfg);
                    }

                    // 3) hard cap: refuse to create more offline users
                    if (CountCitizenUsers(cfg) >= cfg.MaxCitizenUsers)
                    {
                        PluginLog.Warn($"EcoSimCitizens: citizen-user cap reached ({CountCitizenUsers(cfg)}/{cfg.MaxCitizenUsers}); NPC {c.Name} stays ledger-only.");
                        return null;
                    }

                    // 4) create through the controlled factory
                    var name = BuildUserName(c, cfg);
                    if (UserManager.FindUserByName(name) != null)
                    {
                        PluginLog.Warn($"EcoSimCitizens: target user name '{name}' is taken by a non-citizen user; NPC {c.Name} stays ledger-only.");
                        return null;
                    }
                    var created = TestUtils.MakeTestUser(name);
                    if (created == null)
                    {
                        PluginLog.Warn($"EcoSimCitizens: MakeTestUser returned null for '{name}'; NPC {c.Name} stays ledger-only.");
                        return null;
                    }
                    c.OfflineUserName = created.Name;
                    PluginLog.Info($"EcoSimCitizens: created offline citizen user '{created.Name}' for NPC {c.Name}.");
                    return AttachAccount(created, c.Name, cfg);
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"EcoSimCitizens: EnsureOfflineUser failed for '{c?.Name}': {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Attaches the native bank account (TryCreateUserAccount initializes
        /// BankAccount.Owner, avoiding the dangling-Owner NRE family) and migrates
        /// any legacy registrar balance ("EcoSim-&lt;name&gt;") once into the User account
        /// so citizens.json balances survive the v1.2 -&gt; v2.0 transition.
        /// </summary>
        private static User AttachAccount(User user, string citizenName, ModConfig cfg)
        {
            try
            {
                BankAccountManager.Obj.TryCreateUserAccount(user);
                MigrateLegacyBalance(user, citizenName);
                if (cfg.MarkAbandonedCitizenUsers)
                {
                    try { DemographicManager.Abandoned?.SetWhitelist(user, false); }
                    catch (Exception ex) { PluginLog.Warn($"EcoSimCitizens: whitelist activation failed for {user.Name}: {ex.Message}"); }
                }
                user.MarkDirty();
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"EcoSimCitizens: AttachAccount failed for '{user.Name}': {ex.Message}");
            }
            return user;
        }

        /// <summary>
        /// Legacy v1.2 accounts were created through BankAccountManager.Registrar
        /// with no User owner ("EcoSim-&lt;name&gt;"). When the offline User account is
        /// first created, any remaining balance is moved over once (idempotent:
        /// second runs find the legacy account already drained).
        /// </summary>
        private static void MigrateLegacyBalance(User user, string citizenName)
        {
            try
            {
                var cur = NativeCurrencyBridge.DefaultCurrency();
                if (cur == null || user?.BankAccount == null) return;
                var legacy = NativeCurrencyBridge.NpcAccount(citizenName);
                if (legacy == null) return;
                float bal = legacy.GetCurrencyHoldingVal(cur);
                if (bal <= 0f) return;
                user.BankAccount.AddCurrency(cur, bal, false);
                legacy.AddCurrency(cur, -bal, false);
                PluginLog.Info($"EcoSimCitizens: migrated {bal:0.##} {cur.Name} from legacy registrar account 'EcoSim-{citizenName}' to offline user '{user.Name}'.");
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"EcoSimCitizens: legacy balance migration failed for '{citizenName}': {ex.Message}");
            }
        }

        /// <summary>True when any non-retired citizen currently claims this user name.</summary>
        public static bool IsBoundByAny(User user, ModConfig cfg)
        {
            if (user == null) return false;
            var db = EcoSimCitizens.Instance?.Database;
            if (db == null) return false;
            foreach (var c in db.Snapshot())
            {
                if (c.OfflineUserRetired) continue;
                if (string.Equals(c.OfflineUserName, user.Name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Number of offline citizen users present in UserManager (prefix + no SteamId).</summary>
        public static int CountCitizenUsers(ModConfig cfg)
        {
            try
            {
                return UserManager.Users.Count(u => u != null
                    && string.IsNullOrEmpty(u.SteamId)
                    && IsCitizenUserName(u.Name, cfg));
            }
            catch { return 0; }
        }

        public static string BuildUserName(Citizen c, ModConfig cfg)
            => (cfg?.UserPrefix ?? "Citizen") + " " + c.Name;

        /// <summary>Ensures every citizen in the database is bound once (startup pass).</summary>
        public static int EnsureAllCitizenUsers(CitizensDatabase db, ModConfig cfg)
        {
            if (!IsEnabled(cfg) || db == null) return 0;
            int bound = 0;
            foreach (var c in db.Snapshot())
            {
                if (c.OfflineUserRetired) continue;
                try
                {
                    if (EnsureOfflineUser(c, cfg) != null) bound++;
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"EcoSimCitizens: startup user bind failed for '{c.Name}': {ex.Message}");
                }
            }
            if (bound > 0) PluginLog.Info($"EcoSimCitizens: startup user bind completed - {bound} citizen(s) bound to offline users.");
            return bound;
        }

        /// <summary>
        /// Retires the citizen's offline user back to the pool: the User is never
        /// deleted and keeps its money; the binding is released so another NPC can
        /// reuse the name later. Returns the retired User name or null.
        /// </summary>
        public static string RetireOfflineUser(Citizen c, ModConfig cfg)
        {
            if (c == null) return null;
            lock (Sync)
            {
                var name = c.OfflineUserName;
                if (string.IsNullOrEmpty(name)) return null;
                try
                {
                    var user = UserManager.FindUserByName(name);
                    c.OfflineUserName = null;
                    c.OfflineUserRetired = true;
                    if (user != null && cfg.MarkAbandonedCitizenUsers)
                    {
                        try { DemographicManager.Abandoned?.SetWhitelist(user, true); }
                        catch (Exception ex) { PluginLog.Warn($"EcoSimCitizens: abandoned whitelist failed for {user.Name}: {ex.Message}"); }
                    }
                    PluginLog.Info($"EcoSimCitizens: retired offline user '{name}' (NPC {c.Name} released it).");
                    return name;
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"EcoSimCitizens: RetireOfflineUser failed for '{c.Name}': {ex.Message}");
                    return null;
                }
            }
        }
    }
}
