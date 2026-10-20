using System;
using System.Collections.Generic;
using System.Numerics;
using Eco.Core;
using Eco.Gameplay.Components.Storage;
using Eco.Gameplay.Items;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Players;
using Eco.Shared.Math;
using Eco.Shared.Time;

namespace EcoSimCitizens
{
    /// <summary>
    /// High-frequency "living" actions for citizens, called from the movement loop
    /// (throttled to ~every 30s per NPC):
    ///
    ///  - Real gathering: during the day the NPC walks up to a real world resource
    ///    (trees, bushes, crops...) and harvests a handful of the matching real item
    ///    (LogItem / PlantFibersItem / StoneItem / DirtItem...) straight into its
    ///    PublicStorageComponent backpack. This makes NPC work visible and real:
    ///    items appear in the backpack / camp chest instead of a hidden ledger.
    ///  - Camp storage usage: near its own tent the NPC deposits produced goods into
    ///    the camp chest and re-arms missing kit tools (see SurvivalKit.SyncWithCampStorage).
    ///
    /// Resource types are matched by runtime type name (Eco.Mods.Organisms) so this
    /// file never needs a compile-time reference to organism classes.
    /// </summary>
    public static class CitizenRoutine
    {
        // Trees -> LogItem (lumberjack / carpenter output).
        private static readonly HashSet<string> TreeTypeNames = new HashSet<string>
        {
            "Birch", "Cedar", "Ceiba", "Fir", "Oak", "OldGrowthRedwood", "Palm",
            "Redwood", "Spruce", "ArcticWillow", "DwarfWillow", "Joshua",
            "SaguaroCactus", "Agave", "BarrelCactus", "Willow"
        };

        // Bushes / grass / flowers / crops -> PlantFibersItem (farmer / gatherer output).
        private static readonly HashSet<string> PlantTypeNames = new HashSet<string>
        {
            "CreosoteBush", "RoseBush", "PitcherPlant", "Fern", "Fireweed", "Flax",
            "Huckleberry", "Salal", "Sunflower", "Switchgrass", "Bunchgrass",
            "CommonGrass", "BigBluestem", "Bullrush", "Buttonbush", "Camas",
            "Lupine", "Daisy", "Orchid", "Heliconia", "Corn", "Tomato", "Wheat",
            "Rice", "Bean", "Pea", "Cabbage", "Carrot", "Potato", "Pumpkin", "Zucchini"
        };

        private const float GatherSearchRadius = 20f;
        private const float HarvestReach = 3f;

        /// <summary>
        /// Throttled routine: gather while awake, use camp storage when near home.
        /// </summary>
        public static void Perform(CitizenNpc npc, Citizen c, User creator)
        {
            if (npc == null) return;

            // Only work during the day / twilight; at night NPCs sleep at home.
            double timeOfDay = SimulationLogic.TimeOfDayFromDays(TimeUtil.Days);
            var phase = SimulationLogic.PhaseForTimeOfDay(timeOfDay);
            if (phase != SimulationLogic.DayPhase.Night)
            {
                TryHarvest(npc, c, creator);
            }

            SurvivalKit.SyncWithCampStorage(npc, creator);
        }

        /// <summary>
        /// Walks to and harvests the nearest matching real resource. Returns true when
        /// a resource is still relevant (either harvested now, or a gather goal was
        /// set / kept so the NPC keeps working the spot); false when no resource is
        /// nearby so the caller falls back to idle wandering.
        /// </summary>
        public static bool TryHarvest(CitizenNpc npc, Citizen c, User creator)
        {
            try
            {
                if (npc == null) return false;
                var resource = FindNearestHarvestable(npc.Position, GatherSearchRadius);
                if (resource == null) return false;

                float dist = Vector3.Distance(npc.Position, resource.Position);
                if (dist > HarvestReach)
                {
                    // Not close enough yet - walk to it (only if not already heading
                    // somewhere more important; keep the goal steady otherwise).
                    if (npc.GoalType != "Gather")
                        npc.SetGatherGoal(resource.Position);
                    return true;
                }

                var itemType = HarvestItemTypeFor(resource.GetType().Name);
                if (itemType == null) return false;

                var item = Item.Get(itemType);
                if (item == null) return false;

                var inv = npc.GetComponent<PublicStorageComponent>()?.Inventory;
                if (inv == null) return false;

                int qty = 1 + (System.Math.Abs(npc.GetHashCode()) % 2); // 1..2 per harvest
                if (!inv.AddItems(item, qty, creator)) return false;

                if (c != null)
                {
                    SimulationLogic.InventoryAdd(c.Inventory, itemType.Name, qty);
                    c.ItemsProduced += qty;
                }

                EcoSimCitizens.LogDebug($"{npc.CitizenName} harvested {qty} {itemType.Name} near {resource.GetType().Name}");
                // Stay on the spot: the next throttled routine call re-targets the
                // next resource or the arrival path keeps gathering this one.
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"CitizenRoutine.TryHarvest failed for {npc?.CitizenName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Nearest alive resource object within radius, else null.</summary>
        private static WorldObject FindNearestHarvestable(Vector3 pos, float radius)
        {
            WorldObject best = null;
            float bestDist = float.MaxValue;
            try
            {
                var manager = PluginManager.GetPlugin<WorldObjectManager>();
                if (manager == null) return null;
                foreach (var wo in manager.All)
                {
                    if (wo == null || wo.IsDestroyed) continue;
                    string n = wo.GetType().Name;
                    if (n == null) continue;
                    if (!TreeTypeNames.Contains(n) && !PlantTypeNames.Contains(n)) continue;
                    float d = Vector3.Distance(pos, wo.Position);
                    if (d <= radius && d < bestDist)
                    {
                        bestDist = d;
                        best = wo;
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenRoutine.FindNearestHarvestable failed: " + ex.Message);
            }
            return best;
        }

        private static Type HarvestItemTypeFor(string resourceTypeName)
        {
            if (resourceTypeName == null) return null;
            bool tree = TreeTypeNames.Contains(resourceTypeName);
            bool plant = PlantTypeNames.Contains(resourceTypeName);
            if (!tree && !plant) return null;
            try
            {
                // Look the real item up through the registry by simple type name.
                foreach (var item in Item.AllItemsExceptHidden)
                {
                    if (item == null || item.Type == null) continue;
                    string n = item.Type.Name;
                    if (n == null) continue;
                    if (tree && n == "LogItem") return item.Type;
                    if (plant && n == "PlantFibersItem") return item.Type;
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warn("CitizenRoutine item lookup failed: " + ex.Message);
            }
            return null;
        }
    }
}
