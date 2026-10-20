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
    /// Homestead / deed concept for NPC citizens (v0.7).
    ///
    /// Design boundary (kept honest):
    ///   - Real Eco deeds and HomesteadFoundationComponent claims REQUIRE a User
    ///     with property permissions and a real plot; NPCs are WorldObjects, not
    ///     Users, so a real deed claim is not possible for them. The concept is
    ///     therefore implemented as a mod-internal homestead group: citizens are
    ///     grouped by settlement (or a wilderness fallback), each group is
    ///     persisted as a Homestead record in citizens.json, and the invoker
    ///     player's User is used where a native User parameter is required.
    ///   - The one real native call with the invoker User is currency
    ///     registration: when a player claims a homestead, the homestead's own
    ///     deed currency is registered through CurrencyManager.AddCurrency(invoker,
    ///     ...) - the invoker is the creator, exactly like real settlement
    ///     currencies. NPCs still never become Users.
    /// </summary>
    public class Homestead
    {
        public string Name { get; set; }                 // "Springfield Homestead" / "Wilderness Homestead #2"
        public string OwnerName { get; set; }            // first citizen member
        public string SettlementName { get; set; }       // source settlement, empty = wilderness
        public double FoundedDay { get; set; }           // UTC fractional day of founding
        public int Level { get; set; } = 1;              // upgrade level (reserved for future use)
        public List<string> MemberNames { get; set; } = new List<string>();
        public string ClaimedByPlayer { get; set; }      // invoker player that performed the native claim
        public double NativeDeedFund { get; set; }       // ledger of native deed funds (simulated)
        public float PlotX { get; set; }                 // anchor plot (from first member's home/work)
        public float PlotY { get; set; }
        public float PlotZ { get; set; }

        public bool IsClaimed => !string.IsNullOrEmpty(ClaimedByPlayer);
        public bool IsWilderness => string.IsNullOrEmpty(SettlementName);
        public int MemberCount => MemberNames == null ? 0 : MemberNames.Count;
    }

    /// <summary>
    /// Pure mod-side homestead/deed management. Groups homeless citizens into
    /// persisted homesteads, lets an invoker player claim a homestead (native
    /// currency registration uses the invoker's User), and exposes summaries for
    /// commands and reports. No real property claim is attempted.
    /// </summary>
    public static class HomesteadSystem
    {
        private const string WildernessTag = "Wilderness Homestead";

        /// <summary>
        /// Per-tick grouping: every alive citizen without a homestead is placed
        /// into the homestead of its settlement (capped at HomesteadMaxMembers),
        /// or into a wilderness homestead when it has no settlement. Idempotent.
        /// </summary>
        public static void TickHomesteads(CitizensDatabase db, ModConfig cfg)
        {
            if (db == null || cfg == null) return;
            try
            {
                foreach (var c in db.Snapshot())
                {
                    if (c == null || !c.IsAlive || !string.IsNullOrEmpty(c.HomesteadName)) continue;
                    var h = FindOrCreateFor(db, cfg, c);
                    if (h == null) continue;
                    c.HomesteadName = h.Name;
                    AddMember(h, c.Name);
                }
                db.SaveThrottled(cfg.SaveThrottleSeconds);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("EcoSimCitizens: HomesteadSystem.TickHomesteads failed: " + ex.Message);
            }
        }

        private static Homestead FindOrCreateFor(CitizensDatabase db, ModConfig cfg, Citizen c)
        {
            string settlement = string.IsNullOrEmpty(c.SettlementName) ? "" : c.SettlementName;

            // 1) an existing homestead for this settlement that still has room
            var candidate = db.HomesteadsSnapshot()
                .Where(h => string.Equals(h.SettlementName ?? "", settlement, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(h => h.MemberCount < Math.Max(1, cfg.HomesteadMaxMembers));
            if (candidate != null) return candidate;

            // 2) wilderness citizens without settlement: reuse any wilderness
            //    homestead with room, otherwise create a new numbered one
            if (string.IsNullOrEmpty(settlement))
            {
                var wild = db.HomesteadsSnapshot()
                    .Where(h => h.IsWilderness)
                    .FirstOrDefault(h => h.MemberCount < Math.Max(1, cfg.HomesteadMaxMembers));
                if (wild != null) return wild;
                int n = db.HomesteadsSnapshot().Count(h => h.IsWilderness) + 1;
                var created = new Homestead
                {
                    Name = $"{WildernessTag} #{n}",
                    OwnerName = c.Name,
                    SettlementName = "",
                    FoundedDay = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalDays,
                    PlotX = c.WorkX, PlotY = c.WorkY, PlotZ = c.WorkZ,
                };
                db.AddHomestead(created);
                return created;
            }

            // 3) new settlement homestead (suffix only when the base name is taken)
            string baseName = settlement + " Homestead";
            string name = baseName;
            int seq = 2;
            while (db.FindHomestead(name) != null) name = baseName + " #" + seq++;
            var h = new Homestead
            {
                Name = name,
                OwnerName = c.Name,
                SettlementName = settlement,
                FoundedDay = DateTime.UtcNow.Subtract(DateTime.UnixEpoch).TotalDays,
                PlotX = c.WorkX, PlotY = c.WorkY, PlotZ = c.WorkZ,
            };
            db.AddHomestead(h);
            return h;
        }

        private static void AddMember(Homestead h, string memberName)
        {
            if (h.MemberNames == null) h.MemberNames = new List<string>();
            if (!h.MemberNames.Any(m => string.Equals(m, memberName, StringComparison.OrdinalIgnoreCase)))
            {
                h.MemberNames.Add(memberName);
            }
        }

        /// <summary>
        /// Player claim: the invoker's User is the caller for the one native call
        /// that requires a User - registering the homestead's own deed currency via
        /// CurrencyManager.AddCurrency(invoker, ...). The claim itself is recorded
        /// on the mod-side Homestead (real property claim is impossible for NPCs).
        /// Returns the homestead or null on failure (no such homestead / no invoker).
        /// </summary>
        public static Homestead ClaimForInvoker(CitizensDatabase db, ModConfig cfg, string homesteadName, User invoker)
        {
            if (db == null || string.IsNullOrEmpty(homesteadName)) return null;
            var h = db.FindHomestead(homesteadName);
            if (h == null) return null;
            if (invoker == null)
            {
                PluginLog.Warn($"EcoSimCitizens: homestead '{homesteadName}' claim skipped - no invoker User available.");
                return null;
            }

            // Real native call with the invoker as User: register the homestead
            // deed currency so the claim is visible in the native currency list.
            try
            {
                string currencyName = h.Name + " Deed";
                var cur = NativeCurrencyBridge.EnsureCurrencyWithCreator(currencyName, invoker);
                if (cur == null)
                {
                    PluginLog.Warn($"EcoSimCitizens: homestead '{h.Name}' claim proceeded without native currency registration.");
                }
                else
                {
                    PluginLog.Info($"EcoSimCitizens: homestead '{h.Name}' deed currency registered by invoker '{invoker.Name}' (currency '{currencyName}').");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"EcoSimCitizens: homestead '{h.Name}' native currency registration failed: {ex.Message}");
            }

            h.ClaimedByPlayer = invoker.Name;
            h.NativeDeedFund += Math.Max(0.0, cfg.HomesteadClaimCost);
            db.AddHomestead(h);
            db.Save();
            return h;
        }

        /// <summary>One-line summary for reports and command help.</summary>
        public static string Summary(CitizensDatabase db)
        {
            var hs = db.HomesteadsSnapshot().ToList();
            if (hs.Count == 0) return "0 homesteads";
            int claimed = hs.Count(h => h.IsClaimed);
            int members = hs.Sum(h => h.MemberCount);
            return $"{hs.Count} homesteads ({members} members, {claimed} player-claimed)";
        }
    }
}
