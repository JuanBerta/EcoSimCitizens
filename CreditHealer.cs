using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Eco.Core;
using Eco.Gameplay.Components;
using Eco.Gameplay.Objects;
using Eco.Mods.TechTree;

namespace EcoSimCitizens
{
    /// <summary>
    /// v1.1 startup credit-heal guard.
    ///
    /// Root cause: pre-v0.9 mod versions placed WorkbenchObject/ToolBenchObject via
    /// WorldObjectManager.ForceAdd. CraftingComponent implicitly pulls in
    /// CreditComponent, and the ForceAdd path leaves the BankAccount reference
    /// dangling. Those objects were serialized into the world save; on every server
    /// start WorldObjectManager's parallel Initialize calls CreditComponent.Initialize
    /// on them and throws NullReferenceException, killing the server before any
    /// plugin business logic runs.
    ///
    /// v0.9 SelfHealPlacedObjects only destroys objects listed in the PlacedObject
    /// registry, and the old bad objects predate the registry (the current
    /// citizens.json has 0 registry entries), so it never touches them. It also runs
    /// once from plugin Initialize, before the WMO has deserialized the objects.
    ///
    /// This guard keeps scanning the live object set during the whole WMO load
    /// window and uses three independent signals to prove an object is credit-bad:
    ///   1. PlacedObject registry hit for a Workbench/ToolBench (mod-placed);
    ///   2. CreditComponent.BankAccount is null (reflection). A player-placed
    ///      workbench always has a live BankAccount, so this can never match a
    ///      player-built object - the deterministic signal;
    ///   3. known-bad coordinate whitelist collected from server logs.
    /// Objects matching none of the signals are left alone: we never delete a
    /// player workbench we cannot prove is ours.
    /// </summary>
    public static class CreditHealer
    {
        // Known-bad ForceAdd credit objects observed on Beinded Test Server logs
        // (X, Y, Z in metres, matching tolerance below). Player-built workbenches
        // such as (537, 70, 334) Owner=Beinded are intentionally NOT listed.
        private static readonly Vector3[] KnownBadPositions =
        {
            new Vector3(889f, 73f, 151f), // Workbench - present in every load incl. the latest crash log
            new Vector3(844f, 66f, 159f), // Workbench - present in the 12:19 load, gone after 14:06
        };

        private const int ScanIntervalMs = 300;      // scan cadence during the WMO load window
        private const int RunWindowMs = 150_000;     // keep scanning for 150 s, covers full world load
        private const float PositionToleranceM = 3f; // whitelist match tolerance

        /// <summary>
        /// Starts the guard loop as a background task. It runs for the first 150 s
        /// of the server process (plugin Initialize happens before WMO deserializes
        /// objects, so the loop overlaps the entire load window), then stops.
        /// </summary>
        public static Task StartCreditHealLoop(CancellationToken ct)
        {
            return Task.Run(() => CreditHealLoopAsync(ct));
        }

        private static async Task CreditHealLoopAsync(CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int destroyed = 0;
            bool firstPass = true;
            while (!ct.IsCancellationRequested && sw.ElapsedMilliseconds < RunWindowMs)
            {
                try
                {
                    destroyed += ScanOnce(firstPass);
                    firstPass = false;
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("CreditHeal scan failed: " + ex.Message);
                }
                try { await Task.Delay(ScanIntervalMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            if (destroyed > 0)
                PluginLog.Info($"CreditHeal: guard finished, destroyed {destroyed} credit-bad object(s) this startup.");
            else
                PluginLog.Info("CreditHeal: guard finished, no credit-bad objects found.");
        }

        /// <summary>
        /// One scan pass over WorldObjectManager.All. Destroys every
        /// WorkbenchObject/ToolBenchObject that provably carries a dangling credit
        /// (registry hit, null BankAccount, or known-bad position). Returns the
        /// number destroyed in this pass.
        /// </summary>
        public static int ScanOnce(bool verbose)
        {
            int destroyed = 0;
            try
            {
                var wom = PluginManager.GetPlugin<WorldObjectManager>();
                if (wom == null) return 0;
                var db = EcoSimCitizens.Instance?.Database;
                foreach (var wo in wom.All)
                {
                    if (wo == null || wo.IsDestroyed) continue;
                    if (!(wo is WorkbenchObject) && !(wo is ToolBenchObject)) continue;

                    string reason;
                    if (IsRegistryBad(wo, db))
                    {
                        reason = "registry";
                    }
                    else if (HasDanglingCreditBankAccount(wo))
                    {
                        reason = "dangling BankAccount";
                    }
                    else if (IsKnownBadPosition(wo))
                    {
                        reason = "known bad position";
                    }
                    else
                    {
                        if (verbose)
                        {
                            try
                            {
                                PluginLog.Info($"CreditHeal: kept {wo.GetType().Name} {wo.ID} at {wo.Position} (no bad signal; likely player-built, untouched)");
                            }
                            catch { }
                        }
                        continue;
                    }

                    try
                    {
                        wo.Destroy();
                        destroyed++;
                        PluginLog.Info($"CreditHeal: destroyed credit-bad {wo.GetType().Name} {wo.ID} at {wo.Position} ({reason})");
                    }
                    catch (Exception ex)
                    {
                        PluginLog.Warn($"CreditHeal: destroy failed for {wo.GetType().Name} {wo.ID} at {wo.Position} ({reason}): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CreditHeal.ScanOnce failed: " + ex.Message);
            }
            return destroyed;
        }

        /// <summary>
        /// Signal 1: the object is in the PlacedObject registry as a workbench /
        /// tool bench, i.e. the mod placed it for an NPC.
        /// </summary>
        private static bool IsRegistryBad(WorldObject wo, CitizensDatabase db)
        {
            if (db == null) return false;
            try
            {
                string id = wo.ID.ToString();
                string type = wo.GetType().Name;
                return db.PlacedObjectsSnapshot().Any(r =>
                    string.Equals(r.WoId, id, StringComparison.OrdinalIgnoreCase) &&
                    (r.TypeName == "WorkbenchObject" || r.TypeName == "ToolBenchObject") &&
                    string.Equals(r.TypeName, type, StringComparison.Ordinal));
            }
            catch { return false; }
        }

        /// <summary>
        /// Signal 2 (deterministic): read CreditComponent.BankAccount through
        /// reflection; null means the ForceAdd placement skipped BankAccount setup
        /// and the object will NRE during Initialize. A player-placed craft station
        /// always has a live BankAccount, so this never matches player property.
        /// If the member cannot be resolved (API drift) we refuse to judge.
        /// </summary>
        private static bool HasDanglingCreditBankAccount(WorldObject wo)
        {
            try
            {
                var comp = wo.GetComponent<CreditComponent>();
                if (comp == null) return false;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var prop in comp.GetType().GetProperties(flags))
                {
                    if (string.Equals(prop.Name, "BankAccount", StringComparison.OrdinalIgnoreCase))
                    {
                        object val = prop.GetValue(comp);
                        return val == null;
                    }
                }
                foreach (var field in comp.GetType().GetFields(flags))
                {
                    if (string.Equals(field.Name, "BankAccount", StringComparison.OrdinalIgnoreCase))
                    {
                        object val = field.GetValue(comp);
                        return val == null;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Signal 3: the object sits at a coordinate known from server logs to be a
        /// ForceAdd credit-bad workbench.
        /// </summary>
        private static bool IsKnownBadPosition(WorldObject wo)
        {
            try
            {
                var p = wo.Position;
                foreach (var bad in KnownBadPositions)
                {
                    float dx = p.X - bad.X, dy = p.Y - bad.Y, dz = p.Z - bad.Z;
                    if (dx * dx + dy * dy + dz * dz <= PositionToleranceM * PositionToleranceM)
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
