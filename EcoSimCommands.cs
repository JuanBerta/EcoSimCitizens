using System;
using System.Linq;
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
    ///   /ecosim spawn &lt;name&gt; &lt;profession&gt;   (admin)
    ///   /ecosim remove &lt;name&gt;            (admin)
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

        [ChatSubCommand("ecosim", "Spawn a new citizen. Professions: Laborer, Farmer, Miner, Crafter, Merchant, Builder, Scholar.", "spawn", ChatAuthorizationLevel.Admin)]
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
            user.MsgLocStr($"Citizen '{name}' ({citizen.Profession}, {citizen.Trait}) joined the simulation.", NotificationStyle.Info);
            PluginLog.Info($"Spawned citizen {name} ({citizen.Profession}/{citizen.Trait})");
        }

        [ChatSubCommand("ecosim", "Remove a citizen", "remove", ChatAuthorizationLevel.Admin)]
        public static void Remove(User user, string name)
        {
            var db = Plugin.Database;
            if (db.Remove(name))
            {
                db.Save();
                user.MsgLocStr($"Citizen '{name}' removed.", NotificationStyle.Info);
            }
            else
            {
                user.MsgLocStr($"No citizen named '{name}'.", NotificationStyle.Error);
            }
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

        private static string PickTrait(string seed)
        {
            var traits = Traits.AllIds.ToArray();
            int index = Math.Abs(seed.GetHashCode()) % traits.Length;
            return traits[index];
        }
    }
}
