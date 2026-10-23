using System;
using System.Linq;
using System.Numerics;
using System.Text;
using Eco.Core;
using Eco.Core.Plugins;
using Eco.Core.Utils;
using Eco.Gameplay.Economy;
using Eco.Gameplay.Players;
using Eco.Gameplay.Systems.Messaging.Chat;
using Eco.Gameplay.Systems.Messaging.Chat.Commands;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// In-game chat interface. Commands:
    ///   /ecosimulator                 status popup for the caller
    ///   /ecosimulator report          full daily report to the caller
    ///   /ecosimulator list            list all citizens
    ///   /ecosimulator professions     list the real in-game professions
    ///   /ecosimulator spawncitizen &lt;name&gt; &lt;profession&gt;   (admin, creates a real world NPC)
    ///   /ecosimulator removecitizen &lt;name&gt;   (admin, despawns the NPC too)
    ///   /ecosimulator givecash &lt;name&gt; &lt;amount&gt; [currency] (admin; citizen or real player)
    ///   /ecosimulator home &lt;name&gt; &lt;deed&gt;       (admin)
    ///   /ecosimulator save                   (admin)
    ///   /ecosimulator config                (admin)
    /// </summary>
    [ChatCommandHandler]
    public class EcoSimCommands
    {
        private static EcoSimCitizens Plugin =>
            EcoSimCitizens.Instance ?? throw new InvalidOperationException("EcoSimCitizens plugin not loaded.");

        private static void Reply(User user, string message, NotificationStyle style = NotificationStyle.Info)
        {
            if (user == null) { PluginLog.Warn("EcoSim reply skipped (null user): " + message); return; }
            var result = ChatManager.SendMessage(user, user, message);
            if (result.Failed) PluginLog.Warn($"EcoSim reply failed: {result.Message} | {message}");
        }

        private static void ReplyError(User user, string message) => Reply(user, message, NotificationStyle.Error);
        private static void ReplyOk(User user, string message) => Reply(user, message);



        [ChatCommand("EcoSim Citizens - status overview", "ecosimulator", ChatAuthorizationLevel.User)]
        public static void EcoSim(User user)
        {
            var sim = Plugin;
            var db = sim.Database;
            var sb = new StringBuilder();
            sb.AppendLine($"EcoSim Citizens - {db.Count} citizens");
            sb.AppendLine($"Happiness avg: {sim.Simulation.AverageHappiness(db.Snapshot()):0.0}/100");
            sb.AppendLine($"Market: {sim.Simulation.LastMarket.StoreCount} stores, avg price {sim.Simulation.LastMarket.AvgPrice:0.0}");
            sb.AppendLine($"Civic score: {sim.Simulation.LastCivic.CivicScore:0.0}/100");
            sb.AppendLine("Type /ecosimulator report for the full report.");
            ReplyOk(user, sb.ToString());
        }

        [ChatSubCommand("ecosimulator", "Full simulation report", "report", ChatAuthorizationLevel.User)]
        public static void Report(User user)
        {
            var sim = Plugin;
            ReplyOk(user, ReportBuilder.Build(sim.Database, sim.Simulation, ModConfig.Instance));
        }

        [ChatSubCommand("ecosimulator", "List all citizens", "list", ChatAuthorizationLevel.User)]
        public static void CitizenList(User user)
        {
            var db = Plugin.Database;
            var sb = new StringBuilder();
            sb.AppendLine($"Citizens ({db.Count}):");
            foreach (var c in db.Snapshot().OrderBy(c => c.Name))
            {
                string home = c.HasHome ? $" home:{c.HomeDeedName}" : "";
                sb.AppendLine($"  {c.Name} [{c.Profession}/{c.Trait}] H{c.Happiness:0.0} L{c.LedgerBalance:0.00}{home}");
            }
            ReplyOk(user, sb.ToString());
        }

        [ChatSubCommand("ecosimulator", "List the real in-game professions available to citizens", "professions", ChatAuthorizationLevel.User)]
        public static void ListProfessions(User user)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Real Eco professions (root skills from Skill.AllSkills):");
            foreach (var id in Professions.RealSkillIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var e = Professions.Get(id);
                sb.AppendLine($"  {id} - {e.Focus} (productivity {e.Productivity:0.0}, earnings x{e.EarnMultiplier:0.0})");
            }
            ReplyOk(user, sb.ToString());
        }

        [ChatSubCommand("ecosimulator", "Spawn a new citizen. Use /ecosimulator professions for the real profession list.", "spawncitizen", ChatAuthorizationLevel.Admin)]
        public static void CitizenSpawn(User user, string name, string profession)
        {
            var sim = Plugin;
            var db = sim.Database;

            if (string.IsNullOrWhiteSpace(name))
            {
                ReplyError(user, "Usage: /ecosimulator spawncitizen <name> <profession>");
                return;
            }

            if (db.Count >= ModConfig.Instance.MaxCitizens)
            {
                ReplyError(user, $"Citizen cap reached ({ModConfig.Instance.MaxCitizens}).");
                return;
            }

            var citizen = new Citizen
            {
                Name = name,
                Profession = Professions.Get(profession).Id,
                Trait = PickTrait(name),
                LedgerBalance = ModConfig.Instance.StartingBalance,
                Calories = 70,
                Comfort = 50,
                Social = 50,
                Happiness = 50,
            };

            if (!db.Add(citizen))
            {
                ReplyError(user, $"A citizen named '{name}' already exists.");
                return;
            }

            db.Save();

            CitizenNpc npc = null;
            try { npc = NpcManager.Spawn(citizen, user, GetPlayerPosition(user)); }
            catch (Exception ex) { PluginLog.Warn("NPC spawn failed: " + ex.Message); }

            if (npc != null)
            {
                ReplyOk(user, $"Citizen '{name}' ({citizen.Profession}, {citizen.Trait}) is now a real NPC in the world.");
            }
            else
            {
                ReplyOk(user, $"Citizen '{name}' ({citizen.Profession}, {citizen.Trait}) joined the simulation (world NPC unavailable, see logs).");
            }
            PluginLog.Info($"Spawned citizen {name} ({citizen.Profession}/{citizen.Trait}) npc={npc != null}");
        }

        [ChatSubCommand("ecosimulator", "Remove a citizen", "removecitizen", ChatAuthorizationLevel.Admin)]
        public static void CitizenRemove(User user, string name)
        {
            var db = Plugin.Database;
            var c = db.Find(name);
            if (c == null)
            {
                ReplyError(user, $"No citizen named '{name}'.");
                return;
            }
            NpcManager.DestroyFor(c);
            db.Remove(name);
            db.Save();
            ReplyOk(user, $"Citizen '{name}' removed (world NPC despawned).");
        }

        [ChatSubCommand("ecosimulator", "Credit a citizen's or player's native bank account (optional currency name)", "givecash", ChatAuthorizationLevel.Admin)]
        public static void CitizenGive(User user, string name, float amount, string currencyName = null)
        {
            // Resolve the currency: explicit name must exist in CurrencyManager,
            // otherwise fall back to the default sim settlement currency.
            Currency cur;
            if (!string.IsNullOrWhiteSpace(currencyName))
            {
                cur = CurrencyManager.GetClosestCurrency(currencyName);
                if (cur == null)
                {
                    ReplyError(user, $"No currency named '{currencyName}' exists.");
                    return;
                }
            }
            else
            {
                cur = NativeCurrencyBridge.DefaultCurrency();
                if (cur == null)
                {
                    ReplyError(user, "Default sim currency is unavailable (no user to register it); specify a currency name.");
                    return;
                }
            }

            var cfg = ModConfig.Instance;
            var db = Plugin.Database;

            // Target is a simulated citizen (NPC): settle through its native bank
            // account (offline citizen user when bound, legacy registrar account
            // otherwise). When no native account exists at all, fall back to the
            // internal ledger and warn.
            var c = db.Find(name);
            if (c != null)
            {
                var acc = CitizenEconomy.AccountFor(c, cfg);
                if (acc != null)
                {
                    acc.AddCurrency(cur, amount, false);
                    PluginLog.Info($"EcoSimCitizens: /givecash credited NPC '{name}' {amount:0.00} {cur.Name} (native account).");
                    ReplyOk(user, $"'{name}' received {amount:0.00} {cur.Name} (NPC native account).");
                    return;
                }
                c.LedgerBalance += amount;
                db.Save();
                PluginLog.Warn($"EcoSimCitizens: /givecash NPC '{name}' has no native account; credited ledger-only {amount:0.00} {cur.Name}.");
                Reply(user, $"'{name}' has no native account; credited to internal ledger ({amount:0.00} {cur.Name}).", NotificationStyle.Warning);
                return;
            }

            // Target is a real user: resolve by name and credit the native bank account.
            var target = UserManager.FindUserByName(name);
            if (target == null)
            {
                ReplyError(user, $"No citizen or player named '{name}'.");
                return;
            }
            if (target.BankAccount == null)
            {
                ReplyError(user, $"User '{name}' has no bank account.");
                return;
            }
            target.BankAccount.AddCurrency(cur, amount, false);
            PluginLog.Info($"EcoSimCitizens: /givecash credited player '{name}' {amount:0.00} {cur.Name} (native account).");
            ReplyOk(user, $"'{name}' received {amount:0.00} {cur.Name} (native player account).");
        }

        [ChatSubCommand("ecosimulator", "Assign a home deed to a citizen", "citizenhome", ChatAuthorizationLevel.Admin)]
        public static void CitizenHome(User user, string name, string deed)
        {
            var c = Plugin.Database.Find(name);
            if (c == null)
            {
                ReplyError(user, $"No citizen named '{name}'.");
                return;
            }
            if (HousingEvaluator.FindDeed(deed) == null)
            {
                Reply(user, $"No deed named '{deed}' found in the property system.", NotificationStyle.Warning);
            }
            c.HomeDeedName = deed;
            // v0.7: linking a real deed also names the citizen's homestead group.
            if (ModConfig.Instance.HomesteadEnabled && string.IsNullOrEmpty(c.HomesteadName) && HousingEvaluator.FindDeed(deed) != null)
            {
                c.HomesteadName = deed + " Homestead";
            }
            Plugin.Database.Save();
            ReplyOk(user, $"'{name}' now lives at deed '{deed}'.");
        }

        [ChatSubCommand("ecosimulator", "Force-save citizen state", "citizensave", ChatAuthorizationLevel.Admin)]
        public static void CitizenSave(User user)
        {
            var sim = Plugin;
            sim.Database.Save();
            ModConfig.Instance.Save();
            ReplyOk(user, "Citizen state and config saved.");
        }

        [ChatSubCommand("ecosimulator", "Show current config values", "citizenconfig", ChatAuthorizationLevel.Admin)]
        public static void CitizenConfig(User user)
        {
            var cfg = ModConfig.Instance;
            var sb = new StringBuilder();
            sb.AppendLine("EcoSim Citizens config:");
            sb.AppendLine($"  Tick interval: {cfg.TickIntervalMinutes} min");
            sb.AppendLine($"  Wage/tick: {cfg.WagePerTick:0.00} | Start balance: {cfg.StartingBalance:0.00}");
            sb.AppendLine($"  Decay: calories {cfg.CalorieDecayPerTick}, comfort {cfg.ComfortDecayPerTick}, social {cfg.SocialDecayPerTick}");
            sb.AppendLine($"  Citizen cap: {cfg.MaxCitizens} | Broadcast report: {cfg.BroadcastDailyReport}");
            sb.AppendLine("  Config file: EcoSimCitizens/config.json next to the mod DLL.");
            ReplyOk(user, sb.ToString());
        }

        [ChatSubCommand("ecosimulator", "Detailed citizen overview (skills, production, life stage, civics)", "citizens", ChatAuthorizationLevel.User)]
        public static void CitizensOverview(User user)
        {
            var db = Plugin.Database;
            var sb = new StringBuilder();
            sb.AppendLine($"Citizens ({db.Count}):");
            foreach (var c in db.Snapshot().OrderBy(c => c.Name))
            {
                string stage = c.LifeStage;
                string skills = $"{CitizenSkills.GetLevel(c)}/{SimulationLogic.MaxLevel}";
                sb.AppendLine($"  {c.Name} [{c.Profession}/{c.Trait}]");
                sb.AppendLine($"    happiness {c.Happiness:0.0} | skill lvl {skills} | produced {c.ItemsProduced} sold {c.ItemsSold} | earn {c.RealEarned:0.00}");
                sb.AppendLine($"    phase {c.CurrentPhase} | stage {stage} | age {c.AgeYears:0.0}y | home {(string.IsNullOrEmpty(c.SettlementName) ? (c.HasHome ? c.HomeDeedName : "-") : c.SettlementName)} | homestead {(!string.IsNullOrEmpty(c.HomesteadName) ? c.HomesteadName : "-")} | looks {(!string.IsNullOrEmpty(c.AppearanceSourcePlayer) ? "invoker:" + c.AppearanceSourcePlayer : "default")}");
            }
            ReplyOk(user, sb.ToString());
        }

        [ChatSubCommand("ecosimulator", "Full profile of one citizen", "citizeninfo", ChatAuthorizationLevel.User)]
        public static void CitizenInfo(User user, string name)
        {
            var c = Plugin.Database.Find(name);
            if (c == null)
            {
                ReplyError(user, $"No citizen named '{name}'.");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"== {c.Name} ==");
            sb.AppendLine($"Profession: {c.Profession} | Trait: {c.Trait} | Life stage: {c.LifeStage} | Age: {c.AgeYears:0.0}y");
            sb.AppendLine($"Happiness: {c.Happiness:0.0}/100 | Calories: {c.Calories:0.0} | Comfort: {c.Comfort:0.0} | Social: {c.Social:0.0}");
            sb.AppendLine($"Phase: {c.CurrentPhase} | Sleeping: {c.IsSleeping} | AgeInTicks: {c.AgeInTicks}");
            sb.AppendLine($"Skill: {c.Profession} level {CitizenSkills.GetLevel(c)} (xp {CitizenSkills.GetExperience(c):0})");
            sb.AppendLine($"Work: {(c.HasWorkplace ? $"{c.WorkPlaceName} ({c.WorkX:0},{c.WorkY:0},{c.WorkZ:0})" : "none")} | units {c.WorkUnitsProduced}");
            sb.AppendLine($"Production: {SimulationLogic.InventoryTotal(c.Inventory)} items held, {c.ItemsSold} sold, real earned {c.RealEarned:0.00}");
            sb.AppendLine($"Home: {(c.HasHome ? c.HomeDeedName : "none")} | Settlement: {(string.IsNullOrEmpty(c.SettlementName) ? "-" : c.SettlementName)}");
            sb.AppendLine($"Vote: {(c.LastVoteDay > 0 ? $"day {c.LastVoteDay:0.0} for '{c.LastVotedChoice}'" : "never")} | Legal: {c.LegalPreference}");
            sb.AppendLine($"Death: {(c.IsAlive ? "alive" : $"day {c.DeathDay:0.0}, {c.DeathCause}")}");
            sb.AppendLine("Recent social trail:");
            if (c.SocialTrail == null || c.SocialTrail.Count == 0)
            {
                sb.AppendLine("  (nothing yet)");
            }
            else
            {
                foreach (var t in c.SocialTrail) sb.AppendLine("  - " + t);
            }
            ReplyOk(user, sb.ToString());
        }

        [ChatSubCommand("ecosimulator", "Show one citizen's recent social trail", "trail", ChatAuthorizationLevel.User)]
        public static void Trail(User user, string name)
        {
            var c = Plugin.Database.Find(name);
            if (c == null)
            {
                ReplyError(user, $"No citizen named '{name}'.");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"Social trail for {c.Name}:");
            if (c.SocialTrail == null || c.SocialTrail.Count == 0)
            {
                sb.AppendLine("  (nothing yet)");
            }
            else
            {
                foreach (var t in c.SocialTrail) sb.AppendLine("  - " + t);
            }
            ReplyOk(user, sb.ToString());
        }

        private static string PickTrait(string seed)
        {
            var traits = Traits.AllIds.ToArray();
            int index = Math.Abs(seed.GetHashCode()) % traits.Length;
            return traits[index];
        }

        /// <summary>Spawn anchor for new NPCs (world spawn point, valid and always reachable).</summary>
        private static Vector3 GetPlayerPosition(User user)
        {
            try
            {
                if (user != null) return UserManager.GetOrRelocateSpawnPosition();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("GetPlayerPosition failed: " + ex.Message);
            }
            return Vector3.Zero;
        }
    }
}
