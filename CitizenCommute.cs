using System;
using System.Collections.Generic;
using System.Numerics;
using Eco.Core;
using Eco.Gameplay.Components;
using Eco.Gameplay.Components.Store;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Property;
using Eco.Gameplay.Settlements;

namespace EcoSimCitizens
{
    /// <summary>
    /// Workplaces and commuting. Each citizen needs a real workplace so it can
    /// "commute" every day. The mod picks the nearest real store or settlement as
    /// the workplace anchor (observational - it never creates buildings). The NPC
    /// then moves to that anchor during the day and back home at night.
    /// </summary>
    public static class CitizenCommute
    {
        private static readonly List<string> KnownWorkplaces = new List<string>();
        private static readonly object Sync = new object();

        /// <summary>
        /// Ensures the citizen has a workplace (a real store or settlement anchor).
        /// Returns true when the workplace is known this tick.
        /// </summary>
        public static bool EnsureWorkplace(Citizen c, CitizenNpc npc)
        {
            if (c.HasWorkplace) return true;
            if (npc == null) return false;

            string name = null;
            Vector3? pos = null;

            // Prefer a real store: a working citizen should be near the market.
            try
            {
                foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
                {
                    if (wo == null || wo.IsDestroyed) continue;
                    StoreComponent store = null;
                    try { store = wo.GetComponent("StoreComponent") as StoreComponent; }
                    catch { }
                    if (store == null) continue;
                    try
                    {
                        var p = wo.Position;
                        name = wo.Name;
                        pos = new Vector3(p.X, p.Y, p.Z);
                        break;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenCommute store scan failed: " + ex.Message);
            }

            // Fallback: first settlement reachable through the real property system.
            if (pos == null)
            {
                try
                {
                    var deeds = PropertyManager.Obj.Deeds;
                    if (deeds != null)
                    {
                        foreach (var deed in deeds)
                        {
                            if (deed == null) continue;
                            var s = deed.CachedOwningSettlement;
                            if (s == null) continue;
                            var pos3 = s.Position;
                            name = s.Name;
                            pos = new Vector3(pos3.X, pos3.Y, pos3.Z);
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warn("CitizenCommute settlement scan failed: " + ex.Message);
                }
            }

            if (pos == null) return false;

            c.WorkPlaceName = name ?? "Town";
            c.WorkX = pos.Value.X;
            c.WorkY = pos.Value.Y;
            c.WorkZ = pos.Value.Z;
            npc.SetWorkplace(pos.Value);
            CitizenSocial.AddTrail(c, $"found work at {(name ?? "Town")}");
            return true;
        }
    }
}
