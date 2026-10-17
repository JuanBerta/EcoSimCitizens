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
    ///   /ecosimulator                 status popup for the caller
    ///   /ecosimulator report          full daily report to the caller
    ///   /ecosimulator list            list all citizens
    ///   /ecosimulator professions     list the real in-game professions
    ///   /ecosimulator spawncitizen &lt;name&gt; &lt;profession&gt;   (admin, creates a real world NPC)
    ///   /ecosimulator removecitizen &lt;name&gt;   (admin, despawns the NPC too)
    ///   /ecosimulator givecash &lt;name&gt; &lt;amount&gt; (admin)
    ///   /ecosimulator home &lt;name&gt; &lt;deed&gt;       (admin)
    ///   /ecosimulator save                   (admin)
    ///   /ecosimulator config                (admin)
    /// </summary>
    [ChatCommandHandler]
    public class EcoSimCommands
    {
        private static EcoSimCitizens Plugin =>
            PluginManager.GetPlugin<EcoSimCitizens>() ?? throw new InvalidOperationException("EcoSimCitizens plugin not loaded.");

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
            user.OkBoxLocStr(sb.ToString());
        }

        [ChatSubCommand("ecosimulator", "Full simulation report", "report", ChatAuthorizationLevel.User)]
        public static void Report(User user)
        {
            var sim = Plugin;
            user.MsgLocStr(ReportBuilder.Build(sim.Database, sim.Simulation, ModConfig.Instance), NotificationStyle.Info);
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
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
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
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
        }

        [ChatSubCommand("ecosimulator", "Spawn a new citizen. Use /ecosimulator professions for the real profession list.", "spawncitizen", ChatAuthorizationLevel.Admin)]
        public static void CitizenSpawn(User user, string name, string profession)
        {
            var sim = Plugin;
            var db = sim.Database;

            if (string.IsNullOrWhiteSpace(name))
            {
                user.MsgLocStr("Usage: /ecosimulator spawncitizen <name> <profession>", NotificationStyle.Error);
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

        [ChatSubCommand("ecosimulator", "Remove a citizen", "removecitizen", ChatAuthorizationLevel.Admin)]
        public static void CitizenRemove(User user, string name)
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

        [ChatSubCommand("ecosimulator", "Credit a citizen's ledger", "givecash", ChatAuthorizationLevel.Admin)]
        public static void CitizenGive(User user, string name, float amount)
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

        [ChatSubCommand("ecosimulator", "Assign a home deed to a citizen", "citizenhome", ChatAuthorizationLevel.Admin)]
        public static void CitizenHome(User user, string name, string deed)
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

        [ChatSubCommand("ecosimulator", "Force-save citizen state", "citizensave", ChatAuthorizationLevel.Admin)]
        public static void CitizenSave(User user)
        {
            var sim = Plugin;
            sim.Database.Save();
            ModConfig.Instance.Save();
            user.MsgLocStr("Citizen state and config saved.", NotificationStyle.Info);
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
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
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
                sb.AppendLine($"    phase {c.CurrentPhase} | stage {stage} | age {c.AgeYears:0.0}y | home {(string.IsNullOrEmpty(c.SettlementName) ? (c.HasHome ? c.HomeDeedName : "-") : c.SettlementName)}");
            }
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
        }

        [ChatSubCommand("ecosimulator", "Full profile of one citizen", "citizeninfo", ChatAuthorizationLevel.User)]
        public static void CitizenInfo(User user, string name)
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

        [ChatSubCommand("ecosimulator", "Show one citizen's recent social trail", "trail", ChatAuthorizationLevel.User)]
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
