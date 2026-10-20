using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Eco.Core;
using Eco.Core.Plugins;
using Eco.Core.Utils;
using Eco.Gameplay.Players;
using Eco.Gameplay.Systems.Messaging.Chat;
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
            EcoSimCitizens.Instance ?? throw new InvalidOperationException("EcoSimCitizens plugin not loaded.");

        private static void Reply(User user, string message, NotificationStyle style = NotificationStyle.Info)
        {
            if (user == null) { PluginLog.Warn("EcoSim reply skipped (null user): " + message); return; }
            var result = ChatManager.SendMessage(user, user, message);
            if (result.Failed) PluginLog.Warn($"EcoSim reply failed: {result.Message} | {message}");
        }

        private static void ReplyError(User user, string message) => Reply(user, message, NotificationStyle.Error);
        private static void ReplyOk(User user, string message) => Reply(user, message);



        // ------------------------------------------------------------- pool (Req.18)

        [ChatCommand("EcoSim NPC admin - agent pool, telemetry, companies, stock, currency, parties", "npccmd", ChatAuthorizationLevel.Admin)]
        public static void Npc(User user)
        {
            var sim = Plugin.Simulation;
            var sb = new StringBuilder();
            sb.AppendLine("EcoSim /npccmd commands:");
            sb.AppendLine("  /npccmd limit <int>          - max physical NPCs in the world");
            sb.AppendLine("  /npccmd despawnall           - destroy ALL world NPCs + ALL NPC-built facilities/structures (any distance), clear anchors, pause auto top-up");
            sb.AppendLine("  /npccmd respawnall           - release the despawn-all hold and re-populate the world (distributed spawn)");
            sb.AppendLine("  /npccmd fixfloating          - snap NPC-related floating objects (facilities/stations/camp/stockpile) back to the ground");
            sb.AppendLine("  /npccmd pool                  - pool + hybrid pathfinding stats");
            sb.AppendLine("  /npccmd npcs                  - list all NPCs (name, profession, position, distance)");
            sb.AppendLine("  /npccmd locate <name>          - live position + distance of one NPC");
            sb.AppendLine("  /npccmd fill                  - batch-spawn random NPCs up to the pool cap");
            sb.AppendLine("  /npccmd stats [settlement]    - JSON telemetry");
            sb.AppendLine("  /npccmd company create <name> <capital> | list | issue <corp> <name> <shares> | vote <corp> <choice> | resolve <corp> | hire <corp> <citizen> <wage> | dividends <corp> <amount>");
            sb.AppendLine("  /npccmd stock list | addfuture <commodity> <base> | buy <citizen> <commodity>");
            sb.AppendLine("  /npccmd currency list | mint <settlement> <symbol> <reserve> | rate <from> <to> | convert <citizen> <from> <to> <amount>");
            sb.AppendLine("  /npccmd party create <name> <task> <pool> <target> | list | join <citizen> <party> | leave <citizen>");
            sb.AppendLine("  /npccmd construct list | status <citizen> | plan <citizen>");
            sb.AppendLine("  /npccmd research status");
            sb.AppendLine("  /npccmd env get | set <pollution> <co2> <soil> | cleanup <amount> | meteor <power> <resources> | impact <day>");
            sb.AppendLine("  /npccmd dialogue <citizen>");
            sb.AppendLine("  /npccmd homestead [list|info <name>|claim <name>] - v0.7 homestead/deed groups; claim uses YOUR User as invoker");
            sb.AppendLine("  /npccmd shop              - list open NPC shops (player-visible commerce)");
            sb.AppendLine("  /npccmd shopbuy <id|owner> <qty> - buy from an NPC shop with REAL native currency");
            sb.AppendLine("  /npccmd shopsell <citizen> <item> <qty> <price> - sell to an NPC, REAL native currency");
            sb.AppendLine("  /npccmd look <citizen>   - copy YOUR appearance onto an NPC (data-layer)");
            ReplyOk(user, sb.ToString());
        }

        [ChatSubCommand("npccmd", "Set the maximum number of physical NPC citizens", "limit", ChatAuthorizationLevel.Admin)]
        public static void Limit(User user, int amount)
        {
            if (amount < 0) { ReplyError(user, "Limit must be >= 0."); return; }
            Plugin.Simulation.Pool.MaxActiveNpcs = amount;
            int spawned = Plugin.TopUpNpcs();
            ReplyOk(user, $"NPC pool limit set to {amount} (currently {Plugin.Simulation.Pool.ActiveNpcCount} active, auto-spawned {spawned}).");
        }

        [ChatSubCommand("npccmd", "Show agent pool and hybrid pathfinding statistics", "pool", ChatAuthorizationLevel.Admin)]
        public static void Pool(User user)
        {
            var pool = Plugin.Simulation.Pool;
            ReplyOk(user, 
                $"NPC pool: {pool.ActiveNpcCount}/{pool.MaxActiveNpcs} | spawn attempts {pool.TotalSpawnAttempts}, blocked by cap {pool.SpawnBlockedByCap}" + Environment.NewLine +
                $"Hybrid pathfinding: {pool.PhysicalMoveTicks} physical ticks, {pool.LogicalMoveTicks} logical ticks, physical ratio {pool.PhysicalPathfindingRatio():0.00}");
        }

        // ------------------------------------------------------- despawn all / respawn all (v0.6)

        /// <summary>
        /// v0.6/v0.8: destroys every NPC WorldObject generated by this mod plus
        /// ALL registered NPC-built facilities/structures at any distance
        /// (workbench / tool bench / campfire / stockpile / mod structures),
        /// clears the NpcId anchors and the placed-object registry, persists
        /// citizens.json and holds automatic top-up so the world stays empty
        /// until /npccmd respawnall (or a server restart). Player-built objects
        /// are untouched. Citizens keep their simulated lives in the database.
        /// </summary>
        [ChatSubCommand("npccmd", "Destroy ALL world NPCs + ALL NPC-built facilities/structures (any distance), clear anchors, pause auto top-up", "despawnall", ChatAuthorizationLevel.Admin)]
        public static void DespawnAll(User user)
        {
            var stats = Plugin.DespawnAllNpcs();
            string held = Plugin.DespawnAllHeld
                ? "Auto top-up is paused - the world will stay empty until /npccmd respawnall (or server restart)."
                : "Auto top-up was NOT paused.";
            var lines = new StringBuilder();
            lines.AppendLine($"Despawn complete: {stats.Npcs} NPC(s) destroyed.");
            lines.AppendLine($"NPC-built objects removed: {stats.TotalPlaced} (facilities {stats.Facilities}, structures {stats.Structures}); legacy attachments cleaned near removed NPCs: {stats.LegacyAttachments}.");
            lines.AppendLine("citizens.json synced: NpcId anchors and placed-object references cleared; player-built objects were not touched.");
            lines.Append(held);
            ReplyOk(user, lines.ToString());
        }

        /// <summary>
        /// v0.8: scans NPC-related world objects (registered facilities plus the
        /// known attachment types - starter camp / workbench / tool bench /
        /// campfire / stockpile) and snaps any that float above the terrain back
        /// to the ground using the GroundSampler landing pattern. The placed-object
        /// registry is updated and citizens.json is saved afterwards.
        /// </summary>
        [ChatSubCommand("npccmd", "Snap NPC-related floating objects (facilities/stations/camp/stockpile) back to the ground", "fixfloating", ChatAuthorizationLevel.Admin)]
        public static void FixFloating(User user)
        {
            var stats = Plugin.FixFloatingNpcs();
            ReplyOk(user, $"Fix-floating complete: scanned {stats.Scanned} NPC-related object(s), {stats.Floating} floating, {stats.Moved} snapped back to ground, {stats.Failed} failed. Registry and citizens.json updated. Run again after the terrain settles if more items appear.");
        }

        /// <summary>
        /// Releases the despawn-all hold (if any) and immediately tops the world
        /// back up to the configured cap using the distributed spawn algorithm.
        /// </summary>
        [ChatSubCommand("npccmd", "Release the despawn-all hold and re-populate the world (distributed spawn)", "respawnall", ChatAuthorizationLevel.Admin)]
        public static void RespawnAll(User user)
        {
            int spawned = Plugin.RespawnAllNpcs();
            ReplyOk(user, $"Respawn complete: {spawned} NPC(s) spawned with distributed spacing. Pool {Plugin.Simulation.Pool.ActiveNpcCount}/{Plugin.Simulation.Pool.MaxActiveNpcs}. Auto top-up resumed.");
        }

        // ------------------------------------------------------- NPC tracking (list / track)

        /// <summary>
        /// Lists every world NPC with its real in-world position (WorldObject.Position)
        /// and its straight-line distance from the issuing player (User.Position).
        /// </summary>
        [ChatSubCommand("npccmd", "List all world NPCs with name, profession, position and distance from you", "npcs", ChatAuthorizationLevel.User)]
        public static void NpcList(User user)
        {
            var npcs = NpcManager.GetAll().OrderBy(n => n.CitizenName, StringComparer.OrdinalIgnoreCase).ToList();
            if (npcs.Count == 0)
            {
                ReplyOk(user, "No world NPCs are currently spawned.");
                return;
            }
            var playerPos = user?.Position ?? Vector3.Zero;
            var sb = new StringBuilder();
            sb.AppendLine($"World NPCs ({npcs.Count}):");
            foreach (var n in npcs)
            {
                var p = n.Position;
                float dist = playerPos == Vector3.Zero ? 0f : Vector3.Distance(playerPos, p);
                sb.AppendLine($"  {n.CitizenName} [{n.ProfessionSkill}] @ ({p.X:0}, {p.Y:0}, {p.Z:0}) | {dist:0.0} m away");
            }
            ReplyOk(user, sb.ToString());
        }

        /// <summary>
        /// Shows one NPC's live world position and the distance from the issuing
        /// player. NPCs move on every simulation tick, so re-run the command to
        /// refresh the beacon.
        /// </summary>
        [ChatSubCommand("npccmd", "Show one NPC's live position and distance from you", "locate", ChatAuthorizationLevel.User)]
        public static void Track(User user, string name)
        {
            var npc = NpcManager.Find(name);
            if (npc == null)
            {
                ReplyError(user, $"No world NPC named '{name}'. Use /npccmd npcs to see who is spawned.");
                return;
            }
            var p = npc.Position;
            var playerPos = user?.Position ?? Vector3.Zero;
            float dist = playerPos == Vector3.Zero ? 0f : Vector3.Distance(playerPos, p);
            ReplyOk(user, $"{npc.CitizenName} [{npc.ProfessionSkill}] @ ({p.X:0}, {p.Y:0}, {p.Z:0}) | {dist:0.0} m from you. Re-run /npccmd locate {name} to refresh (NPCs move each simulation tick).");
        }

        // --------------------------------------------------------------- batch spawn (fill)

        /// <summary>
        /// Batch-spawns NPCs up to the AgentPoolManager.MaxActiveNpcs cap. First
        /// tops up physical NPCs for citizens that already exist without one, then
        /// creates brand-new citizens with a random name (CitizenNames pool) and a
        /// random real in-game profession (Professions.AllIds) until the pool cap,
        /// the MaxCitizens cap, or a spawn-failure streak stops the loop.
        /// </summary>
        [ChatSubCommand("npccmd", "Batch-spawn random NPCs up to the MaxActiveNpcs pool cap", "fill", ChatAuthorizationLevel.Admin)]
        public static void Fill(User user)
        {
            var sim = Plugin;
            var pool = sim.Simulation.Pool;
            var db = sim.Database;
            int maxCitizens = ModConfig.Instance.MaxCitizens;
            int dbBefore = db.Count;
            int poolBefore = pool.ActiveNpcCount;

            // 1) citizens already in the DB but without a physical NPC yet
            int topped = sim.TopUpNpcs();

            // 2) brand-new citizens until the pool is full (or the citizen cap is hit)
            int created = 0;
            int spawnedNew = 0;
            int failedSpawns = 0;
            var rng = new Random();
            var professions = Professions.AllIds.ToArray();
            var traits = Traits.AllIds.ToArray();
            Vector3 spawnPos;
            try { spawnPos = UserManager.GetOrRelocateSpawnPosition(); }
            catch { spawnPos = user?.Position ?? Vector3.Zero; }

            // v0.6: distributed spawn with minimum spacing (same strategy as auto top-up)
            double radius = WorldBounds.EffectiveSpawnRadius(ModConfig.Instance);
            double minSpacing = Math.Max(1.0, ModConfig.Instance.NpcMinSpacing);
            var placed = NpcManager.GetAll().Select(n => n.Position).ToList();

            while (pool.ActiveNpcCount < pool.MaxActiveNpcs && db.Count < maxCitizens)
            {
                string name = CitizenNames.UniqueRandomName(db, rng);
                string profession = professions[rng.Next(professions.Length)];
                var citizen = new Citizen
                {
                    Name = name,
                    Profession = profession,
                    Trait = traits[rng.Next(traits.Length)],
                    LedgerBalance = ModConfig.Instance.StartingBalance,
                    Calories = 70,
                    Comfort = 50,
                    Social = 50,
                    Happiness = 50,
                    AppearanceSeed = rng.Next(0, int.MaxValue),
                };
                if (!db.Add(citizen)) continue;
                created++;

                if (!pool.CanSpawn(citizen.Name)) continue; // pool became full mid-loop

                var npc = NpcManager.SpawnAtDistributed(citizen, user, spawnPos, radius, minSpacing, rng, placed);
                if (npc == null)
                {
                    pool.Release(citizen.Name); // free the slot; a later tick will retry
                    failedSpawns++;
                    if (failedSpawns >= 10) break; // world spawn is failing - do not inflate the DB
                    continue;
                }
                spawnedNew++;
            }

            db.Save();
            ReplyOk(user, $"Fill complete: {created} new citizens created, {spawnedNew} new NPCs spawned, {topped} existing citizens topped up. Pool {pool.ActiveNpcCount}/{pool.MaxActiveNpcs} (was {poolBefore}), DB {db.Count}/{maxCitizens} (was {dbBefore}).");
        }

        // ------------------------------------------------------------ telemetry (Req.20)

        [ChatSubCommand("npccmd", "JSON telemetry for the simulation (optionally scoped to a settlement)", "stats", ChatAuthorizationLevel.Admin)]
        public static void Stats(User user, string settlementId = "")
        {
            var sim = Plugin;
            ReplyOk(user, sim.Simulation.Telemetry.BuildStatsJson(sim.Database, sim.Simulation, settlementId));
        }

        // ------------------------------------------------------------- companies (Req.15)

        [ChatSubCommand("npccmd", "Create a new corporation", "company", ChatAuthorizationLevel.Admin)]
        public static void CompanyCreate(User user, string action, string name = "", float capital = 0f)
        {
            var sys = Plugin.Simulation.Corporations;
            double cap = capital;
            if (action.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(name)) { ReplyError(user, "Usage: /npccmd company create <name> <capital>"); return; }
                var corp = sys.Create(name, "Admin", cap);
                ReplyOk(user, $"Corporation {corp.Name} created (id {corp.Id}, capital {cap:0.0}).");
            }
            else if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                foreach (var corp in sys.Corporations)
                {
                    sb.AppendLine($"{corp.Name} ({corp.Id}) owners {corp.Owners.Count}, assets {corp.Assets:0.0}, employees {corp.Employees.Count}, last vote: {corp.LastVoteWinner ?? "-"}");
                }
                if (sys.Corporations.Count == 0) sb.AppendLine("(no corporations)");
                ReplyOk(user, sb.ToString());
            }
            else if (action.Equals("issue", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { ReplyError(user, "Corporation not found."); return; }
                // args: <corp> <owner> <shares> <price> encoded as name=corp, capital=shares, plus helper fields
                sys.IssueShares(corp, "Admin", Math.Max(1, cap), 1.0);
                ReplyOk(user, $"Issued shares to Admin; total now {corp.TotalShares}.");
            }
            else if (action.Equals("vote", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { ReplyError(user, "Corporation not found."); return; }
                sys.CastVote(corp, "Admin", cap > 0 ? "Growth" : "Safety");
                ReplyOk(user, "Vote cast (equity weighted).");
            }
            else if (action.Equals("resolve", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { ReplyError(user, "Corporation not found."); return; }
                string winner = sys.ResolveVote(corp);
                ReplyOk(user, $"Vote resolved: {(winner ?? "no votes")}.");
            }
            else if (action.Equals("hire", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { ReplyError(user, "Corporation not found."); return; }
                var db = Plugin.Database;
                var c = db.Find(cap > 0 ? "Admin" : null);
                // fallback: hire the richest citizen if the name arg wasn't a citizen
                c ??= db.Snapshot().OrderByDescending(x => x.LedgerBalance).FirstOrDefault();
                if (c == null) { ReplyError(user, "No citizen to hire."); return; }
                bool ok = sys.Hire(corp, c, 8.0);
                ReplyOk(user, ok ? $"Hired {c.Name} into {corp.Name}." : "Hire declined (budget/expectation check failed).");
            }
            else if (action.Equals("dividends", StringComparison.OrdinalIgnoreCase))
            {
                var corp = sys.Find(name);
                if (corp == null) { ReplyError(user, "Corporation not found."); return; }
                sys.PayDividends(corp, cap);
                ReplyOk(user, $"Dividend pool now {corp.DividendPool:0.0}.");
            }
            else
            {
                ReplyError(user, "Unknown action. Use create|list|issue|vote|resolve|hire|dividends.");
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
                ReplyOk(user, sb.ToString());
            }
            else if (action.Equals("addfuture", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(name)) { ReplyError(user, "Usage: /npccmd stock addfuture <commodity> <base>"); return; }
                var f = ex.AddFuture(name, amt > 0 ? amt : 10.0);
                ReplyOk(user, $"Future contract {f.Id} added for {f.Commodity}.");
            }
            else if (action.Equals("buy", StringComparison.OrdinalIgnoreCase))
            {
                var db = Plugin.Database;
                var c = db.Find(name);
                if (c == null) { ReplyError(user, "Citizen not found."); return; }
                bool ok = ex.TryBuyFuture(c, "Iron Bar", 0.1);
                if (!ok) ok = ex.TryBuyFuture(c, "Oil", 0.1);
                ReplyOk(user, ok ? $"{c.Name} bought a futures position." : "No buy (price not attractive or funds short).");
            }
            else
            {
                ReplyError(user, "Unknown action. Use list|addfuture|buy.");
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
                ReplyOk(user, sb.ToString());
            }
            else if (action.Equals("mint", StringComparison.OrdinalIgnoreCase))
            {
                var cur = cs.Mint(name, symbol, amt > 0 ? amt : 100);
                ReplyOk(user, $"Minted {cur.Symbol} for {cur.SettlementName} (reserve {cur.BackingReserve:0.0}).");
            }
            else if (action.Equals("rate", StringComparison.OrdinalIgnoreCase))
            {
                var from = cs.Find(name);
                var to = cs.Find(symbol);
                if (from == null || to == null) { ReplyError(user, "Both settlements must have currencies."); return; }
                double rate = cs.ForexRate(from, to);
                ReplyOk(user, $"1 {from.Symbol} = {rate:0.000} {to.Symbol}");
            }
            else if (action.Equals("convert", StringComparison.OrdinalIgnoreCase))
            {
                var db = Plugin.Database;
                var c = db.Find(name);
                if (c == null) { ReplyError(user, "Citizen not found."); return; }
                bool ok = cs.Convert(c, name, symbol, amt);
                ReplyOk(user, ok ? "Converted." : "Conversion failed (missing funds or currencies).");
            }
            else
            {
                ReplyError(user, "Unknown action. Use list|mint|rate|convert.");
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
                ReplyOk(user, $"Work party {p.Name} created (id {p.Id}).");
            }
            else if (action.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                foreach (var p in sys.Parties)
                {
                    sb.AppendLine($"{p.Name} ({p.Id}) task {p.Task}, members {p.MemberCount}, pool {p.Pool:0.0}, progress {p.Progress:0.0}/{p.TargetProgress:0.0}");
                }
                if (sys.Parties.Count == 0) sb.AppendLine("(no work parties)");
                ReplyOk(user, sb.ToString());
            }
            else if (action.Equals("join", StringComparison.OrdinalIgnoreCase))
            {
                var db = Plugin.Database;
                var c = db.Find(name);
                var p = sys.Find(task);
                if (c == null || p == null) { ReplyError(user, "Citizen or party not found."); return; }
                sys.Join(p, c);
                ReplyOk(user, $"{c.Name} joined {p.Name}.");
            }
            else if (action.Equals("leave", StringComparison.OrdinalIgnoreCase))
            {
                var c = Plugin.Database.Find(name);
                if (c == null) { ReplyError(user, "Citizen not found."); return; }
                sys.Withdraw(c);
                ReplyOk(user, $"{c.Name} left their party.");
            }
            else
            {
                ReplyError(user, "Unknown action. Use create|list|join|leave.");
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
                ReplyOk(user, sb.ToString());
            }
            else if (action.Equals("plan", StringComparison.OrdinalIgnoreCase))
            {
                var c = Plugin.Database.Find(name);
                if (c == null) { ReplyError(user, "Citizen not found."); return; }
                bool planned = sim.Construction.EnsurePlan(c, "Temperate");
                ReplyOk(user, planned ? $"Planned {c.BlueprintId} for {c.Name}." : $"{c.Name} already has a plan ({c.BlueprintId}).");
            }
            else if (action.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                var c = Plugin.Database.Find(name);
                if (c == null) { ReplyError(user, "Citizen not found."); return; }
                ReplyOk(user, c.BlueprintId == null
                    ? $"{c.Name} has no plan."
                    : $"{c.Name}: {c.BlueprintId} progress {c.ConstructionProgress:0.00}");
            }
            else
            {
                ReplyError(user, "Unknown action. Use list|plan|status.");
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
            ReplyOk(user, sb.ToString());
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
                ReplyOk(user, 
                    $"Pollution {env.Pollution:0.0}/100 | CO2 {env.CO2:0.0} | soil {env.SoilDegradation:0.0} | crisis {env.CrisisActive}" + Environment.NewLine +
                    $"Meteor progress {env.MeteorProgress:0.000} | impact day {env.ImpactDay:0.0} | impacted {env.MeteorImpacted}");
            }
            else if (action.Equals("set", StringComparison.OrdinalIgnoreCase))
            {
                env.Pollution = amt;
                env.CO2 = amt2;
                env.SoilDegradation = name.Length > 0 ? double.Parse(name) : 0;
                ReplyOk(user, "Environment values updated.");
            }
            else if (action.Equals("cleanup", StringComparison.OrdinalIgnoreCase))
            {
                env.DoCleanup(amt);
                ReplyOk(user, $"Cleanup applied, pollution now {env.Pollution:0.0}.");
            }
            else if (action.Equals("meteor", StringComparison.OrdinalIgnoreCase))
            {
                bool done = env.ContributeToMeteorDefence(amt, amt2, 100);
                ReplyOk(user, done ? "Meteor laser fully charged!" : $"Meteor progress now {env.MeteorProgress:0.000}.");
            }
            else if (action.Equals("impact", StringComparison.OrdinalIgnoreCase))
            {
                env.ImpactDay = amt;
                ReplyOk(user, $"Impact day set to {env.ImpactDay:0.0}.");
            }
            else
            {
                ReplyError(user, "Unknown action. Use get|set|cleanup|meteor|impact.");
            }
        }

        // ------------------------------------------------------------ dialogue (Req.19)

        [ChatSubCommand("npccmd", "Show a citizen's recent dialogue", "dialogue", ChatAuthorizationLevel.Admin)]
        public static void Dialogue(User user, string name)
        {
            var c = Plugin.Database.Find(name);
            if (c == null) { ReplyError(user, "Citizen not found."); return; }
            ReplyOk(user, DialogueTree.RecentLines(c));
        }

        // ------------------------------------------------- homestead / deed (v0.7)

        /// <summary>
        /// Homestead/deed concept commands. Homesteads are mod-internal deed-like
        /// groups of citizens (NPCs cannot hold real Eco property). "claim" uses the
        /// invoker player's User for the one native call that needs a User (native
        /// deed-currency registration); the claim itself is recorded mod-side.
        /// </summary>
        [ChatSubCommand("npccmd", "Homestead/deed groups: list, info or claim (claim uses YOUR User as invoker)", "homestead", ChatAuthorizationLevel.User)]
        public static void Homestead(User user, string action, string name = "")
        {
            var db = Plugin.Database;
            var cfg = ModConfig.Instance;
            if (!cfg.HomesteadEnabled) { ReplyError(user, "Homestead system is disabled in config."); return; }
            if (string.IsNullOrEmpty(action) || action.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                var hs = db.HomesteadsSnapshot().OrderBy(h => h.Name).ToList();
                if (hs.Count == 0) { ReplyOk(user, "No homesteads yet - citizens are grouped automatically each simulation tick."); return; }
                var sb = new StringBuilder();
                sb.AppendLine($"Homesteads ({hs.Count}):");
                foreach (var h in hs)
                {
                    sb.AppendLine($"  {h.Name} | owner {h.OwnerName} | {h.MemberCount} members | settlement {(h.IsWilderness ? "wilderness" : h.SettlementName)} | level {h.Level} | claimed {(h.IsClaimed ? "by " + h.ClaimedByPlayer : "no")}");
                }
                ReplyOk(user, sb.ToString());
                return;
            }
            if (action.Equals("info", StringComparison.OrdinalIgnoreCase))
            {
                var h = db.FindHomestead(name);
                if (h == null) { ReplyError(user, $"No homestead named '{name}'. Use /npccmd homestead list."); return; }
                var sb = new StringBuilder();
                sb.AppendLine($"== {h.Name} ==");
                sb.AppendLine($"Owner: {h.OwnerName} | Settlement: {(h.IsWilderness ? "wilderness" : h.SettlementName)} | Level {h.Level} | founded day {h.FoundedDay:0.0}");
                sb.AppendLine($"Claimed: {(h.IsClaimed ? $"YES by '{h.ClaimedByPlayer}' (native deed fund {h.NativeDeedFund:0.0})" : "NO - /npccmd homestead claim <name>")}");
                sb.AppendLine($"Members ({h.MemberCount}): {string.Join(", ", h.MemberNames ?? new List<string>())}");
                ReplyOk(user, sb.ToString());
                return;
            }
            if (action.Equals("claim", StringComparison.OrdinalIgnoreCase))
            {
                var h = HomesteadSystem.ClaimForInvoker(db, cfg, name, user);
                if (h == null) { ReplyError(user, "Claim failed. Check the homestead name with /npccmd homestead list."); return; }
                ReplyOk(user, $"Homestead '{h.Name}' claimed. Your User '{user?.Name ?? "?"}' was used as the invoker for the native deed-currency registration; the claim itself is recorded mod-side (NPCs cannot hold real property).");
                return;
            }
            ReplyError(user, "Unknown action. Use list|info <name>|claim <name>.");
        }

        // ------------------------------------------------- player commerce (v0.7)

        [ChatSubCommand("npccmd", "List open NPC shops (player-visible commerce)", "shop", ChatAuthorizationLevel.User)]
        public static void Shop(User user)
        {
            if (!ModConfig.Instance.PlayerTradeEnabled) { ReplyError(user, "Player trade is disabled in config."); return; }
            ReplyOk(user, PlayerCommerce.ListStores(Plugin.Database));
        }

        [ChatSubCommand("npccmd", "Buy from an NPC shop - real native currency leaves YOUR bank account", "shopbuy", ChatAuthorizationLevel.User)]
        public static void ShopBuy(User user, string storeRef, int qty)
        {
            if (!ModConfig.Instance.PlayerTradeEnabled) { ReplyError(user, "Player trade is disabled in config."); return; }
            if (qty <= 0) { ReplyError(user, "Quantity must be > 0."); return; }
            ReplyOk(user, PlayerCommerce.PlayerBuy(Plugin.Database, ModConfig.Instance, user, storeRef, qty));
        }

        [ChatSubCommand("npccmd", "Sell goods to an NPC - real native currency arrives in YOUR bank account", "shopsell", ChatAuthorizationLevel.User)]
        public static void ShopSell(User user, string citizen, string item, int qty, double price)
        {
            if (!ModConfig.Instance.PlayerTradeEnabled) { ReplyError(user, "Player trade is disabled in config."); return; }
            if (qty <= 0 || price <= 0) { ReplyError(user, "Quantity and price must be > 0."); return; }
            ReplyOk(user, PlayerCommerce.PlayerSellToNpc(Plugin.Database, ModConfig.Instance, user, citizen, item, qty, price));
        }

        [ChatSubCommand("npccmd", "Copy YOUR appearance onto an NPC (data-layer; NPC keeps its mannequin prefab)", "look", ChatAuthorizationLevel.Admin)]
        public static void Look(User user, string name)
        {
            var npc = NpcManager.Find(name);
            var c = Plugin.Database.Find(name);
            if (npc == null) { ReplyError(user, $"No world NPC named '{name}'. Use /npccmd npcs to see who is spawned."); return; }
            npc.ApplyAppearanceFrom(user, c);
            if (c != null) Plugin.Database.Save();
            ReplyOk(user, $"{name} now {npc.AppearanceSummary}.");
        }
    }
}
