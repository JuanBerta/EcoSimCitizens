using System;
using System.Collections.Generic;
using System.Linq;
using Eco.Gameplay.Civics;
using Eco.Gameplay.Players;
using Eco.Gameplay.Property;
using Eco.Gameplay.Settlements;

namespace EcoSimCitizens
{
    /// <summary>Aggregated civic picture: settlements, deeds, elections, real population.</summary>
    public class CivicSnapshot
    {
        public int SettlementCount;
        public int ActiveElectionCount;
        public int RealPlayerCount;
        public int DeedCount;
        public double CivicScore;     // 0..100 heuristic

        public bool HasCivics => SettlementCount > 0;
    }

    /// <summary>
    /// Observes Eco's civic layer (deeds, settlements, elections) without mutating it.
    /// </summary>
    public class CivicObserver
    {
        public CivicSnapshot Observe()
        {
            var snap = new CivicSnapshot();
            var settlements = new HashSet<Settlement>();

            try
            {
                var deeds = PropertyManager.Obj.Deeds;
                snap.DeedCount = deeds == null ? 0 : deeds.Count;
                if (deeds != null)
                {
                    foreach (var deed in deeds)
                    {
                        if (deed == null) continue;
                        var s = deed.CachedOwningSettlement;
                        if (s != null) settlements.Add(s);
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CivicObserver deeds failed: " + ex.Message);
            }

            snap.SettlementCount = settlements.Count;

            try
            {
                foreach (var s in settlements)
                {
                    try
                    {
                        foreach (var _ in ElectionManager.Obj.CurrentElections(s))
                        {
                            snap.ActiveElectionCount++;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CivicObserver elections failed: " + ex.Message);
            }

            try
            {
                snap.RealPlayerCount = UserManager.Users.Count();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CivicObserver users failed: " + ex.Message);
            }

            // Heuristic civic health: populated settlements + active elections + claimed land.
            double score = 20;
            score += Math.Min(40, snap.SettlementCount * 8);
            score += Math.Min(25, snap.ActiveElectionCount * 12);
            score += Math.Min(15, snap.RealPlayerCount * 3);
            score += Math.Min(20, snap.DeedCount * 0.5);
            snap.CivicScore = SimMath.Clamp(score, 0, 100);
            return snap;
        }
    }
}
