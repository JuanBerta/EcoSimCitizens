using System;
using System.Linq;
using System.Numerics;
using System.Text;
using Eco.Core;
using Eco.Core.Plugins;
using Eco.Gameplay.Players;
using Eco.Gameplay.Systems.Messaging.Chat.Commands;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// In-game chat interface. Commands:
    ///   /ecosim                 status popup for the caller
    ///   /ecosim report          full daily report to the caller
    ///   /ecosim list            list all citizens
    ///   /ecosim professions     list the real in-game professions
    ///   /ecosim spawn &lt;name&gt; &lt;profession&gt;   (admin, creates a real world NPC)
    ///   /ecosim remove &lt;name&gt;            (admin, despawns the NPC too)
    ///   /ecosim give &lt;name&gt; &lt;amount&gt;     (admin)
    ///   /ecosim home &lt;name&gt; &lt;deed&gt;       (admin)
    ///   /ecosim save                   (admin)
    ///   /ecosim config                (admin)
    /// </summary>
    [ChatCommandHandler]
    public class EcoSimCommands
    {
        private static EcoSimCitizens Plugin =>
            PluginManager.GetPlugin<EcoSimCitizens>() ?? throw new InvalidOperationException("EcoSimCitizens plugin not loaded.");

        [ChatCommand("EcoSim Citizens - status overview", "ecosim", ChatAuthorizationLevel.User)]
        public static void EcoSim(User user)
        {
            var sim = Plugin;
            var db = sim.Database;
            var sb = new StringBuilder();
            sb.AppendLine($"EcoSim Citizens - {db.Count} citizens");
            sb.AppendLine($"Happiness avg: {sim.Simulation.AverageHappiness(db.Snapshot()):0.0}/100");
            sb.AppendLine($"Market: {sim.Simulation.LastMarket.StoreCount} stores, avg price {sim.Simulation.LastMarket.AvgPrice:0.0}");
            sb.AppendLine($"Civic score: {sim.Simulation.LastCivic.CivicScore:0.0}/100");
            sb.AppendLine("Type /ecosim report for the full report.");
            user.OkBoxLocStr(sb.ToString());
        }

        [ChatSubCommand("ecosim", "Full simulation report", "report", ChatAuthorizationLevel.User)]
        public static void Report(User user)
        {
            var sim = Plugin;
            user.MsgLocStr(ReportBuilder.Build(sim.Database, sim.Simulation, ModConfig.Instance), NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "List all citizens", "list", ChatAuthorizationLevel.User)]
        public static void List(User user)
        {
            var db = Plugin.Database;
            var sb = new StringBuilder();
            sb.AppendLine($"Citizens ({db.Count}):");
            foreach (var c in db.Snapshot().OrderBy(c => c.Name))
            {
                string home = c.HasHome ? $" home:{c.HomeDeedName}" : "";
                sb.AppendLine($"  {c.Name} [{c.Profession}/{c.Trait}] H{c.Happiness:0.0} L{c.LedgerBalance:0.00}{home}");
            }
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "List the real in-game professions available to citizens", "professions", ChatAuthorizationLevel.User)]
        public static void ListProfessions(User user)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Real Eco professions (root skills from Skill.AllSkills):");
            foreach (var id in Professions.RealSkillIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var e = Professions.Get(id);
                sb.AppendLine($"  {id} - {e.Focus} (productivity {e.Productivity:0.0}, earnings x{e.EarnMultiplier:0.0})");
            }
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "Spawn a new citizen. Use /ecosim professions for the real profession list.", "spawn", ChatAuthorizationLevel.Admin)]
        public static void Spawn(User user, string name, string profession)
        {
            var sim = Plugin;
            var db = sim.Database;

            if (string.IsNullOrWhiteSpace(name))
            {
                user.MsgLocStr("Usage: /ecosim spawn <name> <profession>", NotificationStyle.Error);
                return;
            }

            if (db.Count >= ModConfig.Instance.MaxCitizens)
            {
                user.MsgLocStr($"Citizen cap reached ({ModConfig.Instance.MaxCitizens}).", NotificationStyle.Error);
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
                user.MsgLocStr($"A citizen named '{name}' already exists.", NotificationStyle.Error);
                return;
            }

            db.Save();

            CitizenNpc npc = null;
            try { npc = NpcManager.Spawn(citizen, user, GetPlayerPosition(user)); }
            catch (Exception ex) { PluginLog.Warn("NPC spawn failed: " + ex.Message); }

            if (npc != null)
            {
                user.MsgLocStr($"Citizen '{name}' ({citizen.Profession}, {citizen.Trait}) is now a real NPC in the world.", NotificationStyle.Info);
            }
            else
            {
                user.MsgLocStr($"Citizen '{name}' ({citizen.Profession}, {citizen.Trait}) joined the simulation (world NPC unavailable, see logs).", NotificationStyle.Info);
            }
            PluginLog.Info($"Spawned citizen {name} ({citizen.Profession}/{citizen.Trait}) npc={npc != null}");
        }

        [ChatSubCommand("ecosim", "Remove a citizen", "remove", ChatAuthorizationLevel.Admin)]
        public static void Remove(User user, string name)
        {
            var db = Plugin.Database;
            var c = db.Find(name);
            if (c == null)
            {
                user.MsgLocStr($"No citizen named '{name}'.", NotificationStyle.Error);
                return;
            }
            NpcManager.DestroyFor(c);
            db.Remove(name);
            db.Save();
            user.MsgLocStr($"Citizen '{name}' removed (world NPC despawned).", NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "Credit a citizen's ledger", "give", ChatAuthorizationLevel.Admin)]
        public static void Give(User user, string name, double amount)
        {
            var c = Plugin.Database.Find(name);
            if (c == null)
            {
                user.MsgLocStr($"No citizen named '{name}'.", NotificationStyle.Error);
                return;
            }
            c.LedgerBalance += amount;
            Plugin.Database.Save();
            user.MsgLocStr($"'{name}' ledger is now {c.LedgerBalance:0.00}.", NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "Assign a home deed to a citizen", "home", ChatAuthorizationLevel.Admin)]
        public static void Home(User user, string name, string deed)
        {
            var c = Plugin.Database.Find(name);
            if (c == null)
            {
                user.MsgLocStr($"No citizen named '{name}'.", NotificationStyle.Error);
                return;
            }
            if (HousingEvaluator.FindDeed(deed) == null)
            {
                user.MsgLocStr($"No deed named '{deed}' found in the property system.", NotificationStyle.Warning);
            }
            c.HomeDeedName = deed;
            Plugin.Database.Save();
            user.MsgLocStr($"'{name}' now lives at deed '{deed}'.", NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "Force-save citizen state", "save", ChatAuthorizationLevel.Admin)]
        public static void Save(User user)
        {
            var sim = Plugin;
            sim.Database.Save();
            ModConfig.Instance.Save();
            user.MsgLocStr("Citizen state and config saved.", NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "Show current config values", "config", ChatAuthorizationLevel.Admin)]
        public static void Config(User user)
        {
            var cfg = ModConfig.Instance;
            var sb = new StringBuilder();
            sb.AppendLine("EcoSim Citizens config:");
            sb.AppendLine($"  Tick interval: {cfg.TickIntervalMinutes} min");
            sb.AppendLine($"  Wage/tick: {cfg.WagePerTick:0.00} | Start balance: {cfg.StartingBalance:0.00}");
            sb.AppendLine($"  Decay: calories {cfg.CalorieDecayPerTick}, comfort {cfg.ComfortDecayPerTick}, social {cfg.SocialDecayPerTick}");
            sb.AppendLine($"  Citizen cap: {cfg.MaxCitizens} | Broadcast report: {cfg.BroadcastDailyReport}");
            sb.AppendLine("  Config file: EcoSimCitizens/config.json next to the mod DLL.");
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "Detailed citizen overview (skills, production, life stage, civics)", "citizens", ChatAuthorizationLevel.User)]
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
                sb.AppendLine($"    phase {c.CurrentPhase} | stage {stage} | age {c.AgeYears:0.0}y | home {(string.IsNullOrEmpty(c.SettlementName) ? (c.HasHome ? c.HomeDeedName : "-") : c.SettlementName)}");
            }
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "Full profile of one citizen", "info", ChatAuthorizationLevel.User)]
        public static void Info(User user, string name)
        {
            var c = Plugin.Database.Find(name);
            if (c == null)
            {
                user.MsgLocStr($"No citizen named '{name}'.", NotificationStyle.Error);
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
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
        }

        [ChatSubCommand("ecosim", "Show one citizen's recent social trail", "trail", ChatAuthorizationLevel.User)]
        public static void Trail(User user, string name)
        {
            var c = Plugin.Database.Find(name);
            if (c == null)
            {
                user.MsgLocStr($"No citizen named '{name}'.", NotificationStyle.Error);
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
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
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
