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
    /// PRD admin interface. New /npccmd command group covering agent pool limits
    /// (Req.18), JSON telemetry (Req.20), work parties (Req.7), construction
    /// (Req.2/3), research (Req.9), corporations (Req.15), the stock exchange
    /// (Req.16), settlement currencies &amp; forex (Req.3), environment &amp; meteor
    /// crisis (Req.5/17) and contextual dialogue (Req.19).
    /// </summary>
    [ChatCommandHandler]
    public class NpcCommands
    {
        private static EcoSimCitizens Plugin =>
            PluginManager.GetPlugin<EcoSimCitizens>() ?? throw new InvalidOperationException("EcoSimCitizens plugin not loaded.");

        // ------------------------------------------------------------- pool (Req.18)

        [ChatCommand("EcoSim NPC admin - agent pool, telemetry, companies, stock, currency, parties", "npccmd", ChatAuthorizationLevel.Admin)]
        public static void Npc(User user)
        {
            var sim = Plugin.Simulation;
            var sb = new StringBuilder();
            sb.AppendLine("EcoSim /npccmd commands:");
            sb.AppendLine("  /npccmd limit <int>          - max physical NPCs in the world");
            sb.AppendLine("  /npccmd pool                  - pool + hybrid pathfinding stats");
            sb.AppendLine("  /npccmd stats [settlement]    - JSON telemetry");
            sb.AppendLine("  /npccmd company create <name> <capital> | list | issue <corp> <name> <shares> | vote <corp> <choice> | resolve <corp> | hire <corp> <citizen> <wage> | dividends <corp> <amount>");
            sb.AppendLine("  /npccmd stock list | addfuture <commodity> <base> | buy <citizen> <commodity>");
            sb.AppendLine("  /npccmd currency list | mint <settlement> <symbol> <reserve> | rate <from> <to> | convert <citizen> <from> <to> <amount>");
            sb.AppendLine("  /npccmd party create <name> <task> <pool> <target> | list | join <citizen> <party> | leave <citizen>");
            sb.AppendLine("  /npccmd construct list | status <citizen> | plan <citizen>");
            sb.AppendLine("  /npccmd research status");
            sb.AppendLine("  /npccmd env get | set <pollution> <co2> <soil> | cleanup <amount> | meteor <power> <resources> | impact <day>");
            sb.AppendLine("  /npccmd dialogue <citizen>");
            user.OkBoxLocStr(sb.ToString());
        }

        [ChatSubCommand("npccmd", "Set the maximum number of physical NPC citizens", "limit", ChatAuthorizationLevel.Admin)]
        public static void Limit(User user, int amount)
        {
            if (amount < 0) { user.ErrorLocStr("Limit must be >= 0."); return; }
            Plugin.Simulation.Pool.MaxActiveNpcs = amount;
            user.MsgLocStr($"NPC pool limit set to {amount} (currently {Plugin.Simulation.Pool.ActiveNpcCount} active).", NotificationStyle.Info);
        }

        [ChatSubCommand("npccmd", "Show agent pool and hybrid pathfinding statistics", "pool", ChatAuthorizationLevel.Admin)]
        public static void Pool(User user)
        {
            var pool = Plugin.Simulation.Pool;
            user.MsgLocStr(
                $"NPC pool: {pool.ActiveNpcCount}/{pool.MaxActiveNpcs} | spawn attempts {pool.TotalSpawnAttempts}, blocked by cap {pool.SpawnBlockedByCap}" + Environment.NewLine +
                $"Hybrid pathfinding: {pool.PhysicalMoveTicks} physical ticks, {pool.LogicalMoveTicks} logical ticks, physical ratio {pool.PhysicalPathfindingRatio():0.00}",
                NotificationStyle.Info);
        }

        // ------------------------------------------------------------ telemetry (Req.20)

        [ChatSubCommand("npccmd", "JSON telemetry for the simulation (optionally scoped to a settlement)", "stats", ChatAuthorizationLevel.Admin)]
        public static void Stats(User user, string settlementId = "")
        {
            var sim = Plugin;
            user.MsgLocStr(sim.Simulation.Telemetry.BuildStatsJson(sim.Database, sim.Simulation, settlementId), NotificationStyle.Info);
        }

        // ------------------------------------------------------------- companies (Req.15)

        [ChatSubCommand("npccmd", "Create a new corporation", "company", ChatAuthorizationLevel.Admin)]
        public static void CompanyCreate(User user, string action, string name = "", float capital = 0f)
        {
            var sys = Plugin.Simulation.Corporations;
            double cap = capital;
            if (action.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(name)) { user.ErrorLocStr("Usage: /npccmd company create <name> <capital>"); return; }
                var corp = sys.Create(name, "Admin", cap);
                user.MsgLocStr($"Corporation {corp.Name} created (id {corp.Id}, capital {cap:0.0}).", NotificationStyle.Info);
            }
            else if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                foreach (var corp in sys.Corporations)
                {
                    sb.AppendLine($"{corp.Name} ({corp.Id}) owners {corp.Owners.Count}, assets {corp.Assets:0.0}, employees {corp.Employees.Count}, last vote: {corp.LastVoteWinner ?? "-"}");
                }
                if (sys.Corporations.Count == 0) sb.AppendLine("(no corporations)");
                user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
            }
            else if (action.Equals("issue", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { user.ErrorLocStr("Corporation not found."); return; }
                // args: <corp> <owner> <shares> <price> encoded as name=corp, capital=shares, plus helper fields
                sys.IssueShares(corp, "Admin", Math.Max(1, cap), 1.0);
                user.MsgLocStr($"Issued shares to Admin; total now {corp.TotalShares}.", NotificationStyle.Info);
            }
            else if (action.Equals("vote", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { user.ErrorLocStr("Corporation not found."); return; }
                sys.CastVote(corp, "Admin", cap > 0 ? "Growth" : "Safety");
                user.MsgLocStr("Vote cast (equity weighted).", NotificationStyle.Info);
            }
            else if (action.Equals("resolve", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { user.ErrorLocStr("Corporation not found."); return; }
                string winner = sys.ResolveVote(corp);
                user.MsgLocStr($"Vote resolved: {(winner ?? "no votes")}.", NotificationStyle.Info);
            }
            else if (action.Equals("hire", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { user.ErrorLocStr("Corporation not found."); return; }
                var db = Plugin.Database;
                var c = db.Find(cap > 0 ? "Admin" : null);
                // fallback: hire the richest citizen if the name arg wasn't a citizen
                c ??= db.Snapshot().OrderByDescending(x => x.LedgerBalance).FirstOrDefault();
                if (c == null) { user.ErrorLocStr("No citizen to hire."); return; }
                bool ok = sys.Hire(corp, c, 8.0);
                user.MsgLocStr(ok ? $"Hired {c.Name} into {corp.Name}." : "Hire declined (budget/expectation check failed).", NotificationStyle.Info);
            }
            else if (action.Equals("dividends", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { user.ErrorLocStr("Corporation not found."); return; }
                sys.PayDividends(corp, cap);
                user.MsgLocStr($"Dividend pool now {corp.DividendPool:0.0}.", NotificationStyle.Info);
            }
            else
            {
                user.ErrorLocStr("Unknown action. Use create|list|issue|vote|resolve|hire|dividends.");
            }
        }

        // ---------------------------------------------------------- stock exchange (Req.16)

        [ChatSubCommand("npccmd", "Stock exchange: list, add futures, buy", "stock", ChatAuthorizationLevel.Admin)]
        public static void Stock(User user, string action, string name = "", float amount = 0f)
        {
            var ex = Plugin.Simulation.Exchange;
            double amt = amount;
            if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                foreach (var l in ex.Listings) sb.AppendLine($"{l.Name}: {l.LastPrice:0.00}/share ({l.Shares} shares)");
                foreach (var f in ex.Futures) sb.AppendLine($"Future {f.Commodity}: {f.LastPrice:0.00} (base {f.BasePrice:0.00}, supply {f.Supply:0.0}, demand {f.Demand:0.0})");
                if (ex.Listings.Count == 0 && ex.Futures.Count == 0) sb.AppendLine("(exchange empty - /npccmd stock addfuture <commodity> <base>)");
                user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
            }
            else if (action.Equals("addfuture", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(name)) { user.ErrorLocStr("Usage: /npccmd stock addfuture <commodity> <base>"); return; }
                var f = ex.AddFuture(name, amt > 0 ? amt : 10.0);
                user.MsgLocStr($"Future contract {f.Id} added for {f.Commodity}.", NotificationStyle.Info);
            }
            else if (action.Equals("buy", StringComparison.OrdinalIgnoreCase))
            {
                var db = Plugin.Database;
                var c = db.Find(name);
                if (c == null) { user.ErrorLocStr("Citizen not found."); return; }
                bool ok = ex.TryBuyFuture(c, "Iron Bar", 0.1);
                if (!ok) ok = ex.TryBuyFuture(c, "Oil", 0.1);
                user.MsgLocStr(ok ? $"{c.Name} bought a futures position." : "No buy (price not attractive or funds short).", NotificationStyle.Info);
            }
            else
            {
                user.ErrorLocStr("Unknown action. Use list|addfuture|buy.");
            }
        }

        // --------------------------------------------------------------- currency (Req.3)

        [ChatSubCommand("npccmd", "Settlement currencies and forex", "currency", ChatAuthorizationLevel.Admin)]
        public static void Currency(User user, string action, string name = "", string symbol = "", float amount = 0f)
        {
            var cs = Plugin.Simulation.Currencies;
            double amt = amount;
            if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                foreach (var cur in cs.Currencies)
                {
                    sb.AppendLine($"{cur.SettlementName} ({cur.Symbol}): supply {cur.Supply:0.0}, reserve {cur.BackingReserve:0.0}, GDP {cur.Gdp:0.0}, pop {cur.Population:0}");
                }
                if (cs.Currencies.Count == 0) sb.AppendLine("(no currencies minted)");
                user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
            }
            else if (action.Equals("mint", StringComparison.OrdinalIgnoreCase))
            {
                var cur = cs.Mint(name, symbol, amt > 0 ? amt : 100);
                user.MsgLocStr($"Minted {cur.Symbol} for {cur.SettlementName} (reserve {cur.BackingReserve:0.0}).", NotificationStyle.Info);
            }
            else if (action.Equals("rate", StringComparison.OrdinalIgnoreCase))
            {
                var from = cs.Find(name);
                var to = cs.Find(symbol);
                if (from == null || to == null) { user.ErrorLocStr("Both settlements must have currencies."); return; }
                double rate = cs.ForexRate(from, to);
                user.MsgLocStr($"1 {from.Symbol} = {rate:0.000} {to.Symbol}", NotificationStyle.Info);
            }
            else if (action.Equals("convert", StringComparison.OrdinalIgnoreCase))
            {
                var db = Plugin.Database;
                var c = db.Find(name);
                if (c == null) { user.ErrorLocStr("Citizen not found."); return; }
                bool ok = cs.Convert(c, name, symbol, amt);
                user.MsgLocStr(ok ? "Converted." : "Conversion failed (missing funds or currencies).", NotificationStyle.Info);
            }
            else
            {
                user.ErrorLocStr("Unknown action. Use list|mint|rate|convert.");
            }
        }

        // ------------------------------------------------------------- work parties (Req.7)

        [ChatSubCommand("npccmd", "Work parties and contracts", "party", ChatAuthorizationLevel.Admin)]
        public static void Party(User user, string action, string name = "", string task = "", float amount = 0f)
        {
            var sys = Plugin.Simulation.WorkParties;
            double amt = amount;
            if (action.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                var p = sys.CreateParty(name, string.IsNullOrEmpty(task) ? "General" : task, amt > 0 ? amt : 100, 50);
                user.MsgLocStr($"Work party {p.Name} created (id {p.Id}).", NotificationStyle.Info);
            }
            else if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                foreach (var p in sys.Parties)
                {
                    sb.AppendLine($"{p.Name} ({p.Id}) task {p.Task}, members {p.MemberCount}, pool {p.Pool:0.0}, progress {p.Progress:0.0}/{p.TargetProgress:0.0}");
                }
                if (sys.Parties.Count == 0) sb.AppendLine("(no work parties)");
                user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
            }
            else if (action.Equals("join", StringComparison.OrdinalIgnoreCase))
            {
                var db = Plugin.Database;
                var c = db.Find(name);
                var p = sys.Find(task);
                if (c == null || p == null) { user.ErrorLocStr("Citizen or party not found."); return; }
                sys.Join(p, c);
                user.MsgLocStr($"{c.Name} joined {p.Name}.", NotificationStyle.Info);
            }
            else if (action.Equals("leave", StringComparison.OrdinalIgnoreCase))
            {
                var c = Plugin.Database.Find(name);
                if (c == null) { user.ErrorLocStr("Citizen not found."); return; }
                sys.Withdraw(c);
                user.MsgLocStr($"{c.Name} left their party.", NotificationStyle.Info);
            }
            else
            {
                user.ErrorLocStr("Unknown action. Use create|list|join|leave.");
            }
        }

        // ------------------------------------------------------------ construction (Req.2/3)

        [ChatSubCommand("npccmd", "Construction plans", "construct", ChatAuthorizationLevel.Admin)]
        public static void ConstructionManage(User user, string action, string name = "")
        {
            var sim = Plugin.Simulation;
            if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                foreach (var c in Plugin.Database.Snapshot().Where(x => x.BlueprintId != null))
                {
                    sb.AppendLine($"{c.Name}: {c.BlueprintId} {c.ConstructionProgress:0.00} ({(c.ConstructionMaterialsSecured ? "materials ready" : "saving materials")})");
                }
                if (Plugin.Database.Snapshot().All(x => x.BlueprintId == null)) sb.AppendLine("(no active construction plans)");
                sb.AppendLine($"Completed projects: {sim.Construction.ProjectsCompleted} | materials purchased: {sim.Construction.MaterialsPurchased:0.0}");
                user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
            }
            else if (action.Equals("plan", StringComparison.OrdinalIgnoreCase))
            {
                var c = Plugin.Database.Find(name);
                if (c == null) { user.ErrorLocStr("Citizen not found."); return; }
                bool planned = sim.Construction.EnsurePlan(c, "Temperate");
                user.MsgLocStr(planned ? $"Planned {c.BlueprintId} for {c.Name}." : $"{c.Name} already has a plan ({c.BlueprintId}).", NotificationStyle.Info);
            }
            else if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                var c = Plugin.Database.Find(name);
                if (c == null) { user.ErrorLocStr("Citizen not found."); return; }
                user.MsgLocStr(c.BlueprintId == null
                    ? $"{c.Name} has no plan."
                    : $"{c.Name}: {c.BlueprintId} progress {c.ConstructionProgress:0.00}", NotificationStyle.Info);
            }
            else
            {
                user.ErrorLocStr("Unknown action. Use list|plan|status.");
            }
        }

        // ------------------------------------------------------------- research (Req.9)

        [ChatSubCommand("npccmd", "Research hubs", "research", ChatAuthorizationLevel.Admin)]
        public static void Research(User user)
        {
            var r = Plugin.Simulation.Research;
            var sb = new StringBuilder();
            sb.AppendLine($"Discoveries unlocked: {r.DiscoveriesUnlocked} | total points: {r.TotalPoints():0.0}");
            foreach (var kv in r.DomainPoints.OrderByDescending(k => k.Value))
            {
                int tier = r.TierFor(kv.Key);
                sb.AppendLine($"  {kv.Key}: {kv.Value:0.0} points (tier {tier})");
            }
            user.MsgLocStr(sb.ToString(), NotificationStyle.Info);
        }

        // -------------------------------------------------------- environment & meteor (Req.5/17)

        [ChatSubCommand("npccmd", "Environment and meteor crisis management", "env", ChatAuthorizationLevel.Admin)]
        public static void Env(User user, string action, string name = "", float amount = 0f, float amount2 = 0f)
        {
            var env = Plugin.Simulation.Environment;
            double amt = amount;
            double amt2 = amount2;
            if (action.Equals("get", StringComparison.OrdinalIgnoreCase))
            {
                user.MsgLocStr(
                    $"Pollution {env.Pollution:0.0}/100 | CO2 {env.CO2:0.0} | soil {env.SoilDegradation:0.0} | crisis {env.CrisisActive}" + Environment.NewLine +
                    $"Meteor progress {env.MeteorProgress:0.000} | impact day {env.ImpactDay:0.0} | impacted {env.MeteorImpacted}",
                    NotificationStyle.Info);
            }
            else if (action.Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                env.Pollution = amt;
                env.CO2 = amt2;
                env.SoilDegradation = name.Length > 0 ? double.Parse(name) : 0;
                user.MsgLocStr("Environment values updated.", NotificationStyle.Info);
            }
            else if (action.Equals("cleanup", StringComparison.OrdinalIgnoreCase))
            {
                env.DoCleanup(amt);
                user.MsgLocStr($"Cleanup applied, pollution now {env.Pollution:0.0}.", NotificationStyle.Info);
            }
            else if (action.Equals("meteor", StringComparison.OrdinalIgnoreCase))
            {
                bool done = env.ContributeToMeteorDefence(amt, amt2, 100);
                user.MsgLocStr(done ? "Meteor laser fully charged!" : $"Meteor progress now {env.MeteorProgress:0.000}.", NotificationStyle.Info);
            }
            else if (action.Equals("impact", StringComparison.OrdinalIgnoreCase))
            {
                env.ImpactDay = amt;
                user.MsgLocStr($"Impact day set to {env.ImpactDay:0.0}.", NotificationStyle.Info);
            }
            else
            {
                user.ErrorLocStr("Unknown action. Use get|set|cleanup|meteor|impact.");
            }
        }

        // ------------------------------------------------------------ dialogue (Req.19)

        [ChatSubCommand("npccmd", "Show a citizen's recent dialogue", "dialogue", ChatAuthorizationLevel.Admin)]
        public static void Dialogue(User user, string name)
        {
            var c = Plugin.Database.Find(name);
            if (c == null) { user.ErrorLocStr("Citizen not found."); return; }
            user.MsgLocStr(DialogueTree.RecentLines(c), NotificationStyle.Info);
        }
    }
}
