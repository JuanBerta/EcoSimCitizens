using System;
using System.Collections.Generic;
using System.Numerics;
using Eco.Core;
using Eco.Gameplay.Components.Storage;
using Eco.Gameplay.Items;
using Eco.Gameplay.Objects;
using Eco.Gameplay.Players;
using Eco.Mods.TechTree;
using Eco.Shared.Math;
using Quaternion = Eco.Shared.Math.Quaternion;

namespace EcoSimCitizens
{
    /// <summary>
    /// Grants citizens the same starter kit a fresh player receives and lets them
    /// attempt their first settlement.
    ///
    /// Delivered kit (matches Eco PlayerDefaults):
    ///   - backpack:   Starter Camp x1, Claim Paper x4
    ///   - camp/tent:  Claim Paper x6, Claim Tool x1, Stone Axe x1,
    ///                 Wooden Shovel x1 (Eco 0.14 has no StoneShovelItem),
    ///                 Stone Hammer x1, Stone Pickaxe x1
    ///
    /// Placement flow: Starter Camp next to the NPC, then Campfire + Stockpile
    /// a few meters away. Claiming real property and creating stores are player
    /// interactions that require a User identity (Deed / PropertyAuthComponent /
    /// ForSaleComponent), so the kit only hands out the claim papers and tool and
    /// places the physical campfire/stockpile; deed/store creation stays out of
    /// scope. Workbench/ToolBench are NOT placed anymore (v0.9): craft stations
    /// pull in CreditComponent (CraftingComponent -&gt; CreditComponent) and objects
    /// created via WorldObjectManager.ForceAdd carry a dangling BankAccount owner
    /// reference that throws NullReferenceException in CreditComponent.Initialize
    /// on the next world load, crashing the server.
    ///
    /// Everything is idempotent: existing items are never duplicated, already
    /// placed objects are never placed twice, and failures are logged but never
    /// fatal to the simulation.
    /// </summary>
    public static class SurvivalKit
    {
        // Player starter backpack (Eco PlayerDefaults.GetDefaultInventory).
        private static readonly (Type type, int qty)[] BackpackKit =
        {
            (typeof(StarterCampItem), 1),
            (typeof(ClaimPaperItem), 4),
        };

        // Player starter camp storage (Eco PlayerDefaults.GetDefaultCampsiteInventory).
        // Eco 0.14 removed the Stone Shovel; Wooden Shovel is the tier-1 shovel.
        private static readonly (Type type, int qty)[] CampKit =
        {
            (typeof(ClaimPaperItem), 6),
            (typeof(ClaimToolItem), 1),
            (typeof(StoneAxeItem), 1),
            (typeof(WoodenShovelItem), 1),
            (typeof(StoneHammerItem), 1),
            (typeof(StonePickaxeItem), 1),
        };

        private const float CampRadius = 8f;    // camp considered present if within this distance
        private const float StationRadius = 15f; // workstation considered present if within this distance

        /// <summary>
        /// Idempotent entry point: ensures the NPC has a backpack with the starter
        /// kit, then attempts the first settlement (camp + workstations).
        /// </summary>
        public static void GrantToNpc(CitizenNpc npc, User creator)
        {
            if (npc == null) return;
            try
            {
                EnsureBackpackKit(npc, creator);

                // First settlement: camp, then workstations near the camp.
                var camp = EnsureCamp(npc, creator);
                var campPos = camp != null ? camp.Position : npc.Position;

                EnsureCampKit(npc, camp, creator);
                // v0.9: Workbench/ToolBench placement removed - craft stations pull
                // in CreditComponent and ForceAdd-created copies crash with a
                // NullReferenceException in CreditComponent.Initialize on reload
                // (log_261020121654). Campfire/Stockpile carry no credit dependency.
                // Campfire cooking: an open campfire 4m from the camp plus a
                // stockpile with fuel and ready campfire food (Charred Meat), so the
                // citizen can cook/eat campfire food at its own camp.
                EnsureStation(npc, typeof(CampfireObject), creator, campPos, 4.0f, "Campfire");
                EnsureStation(npc, typeof(StockpileObject), creator, campPos, 9.0f, "Stockpile");
                EnsureCampfireSupplies(npc, campPos, creator);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"{npc.CitizenName}: SurvivalKit.GrantToNpc failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------- backpack

        private static void EnsureBackpackKit(CitizenNpc npc, User creator)
        {
            var storage = npc.GetOrCreateComponent<PublicStorageComponent>(null, c => c.Initialize(15, 1000));
            var inv = storage?.Inventory;
            if (inv == null && storage != null)
            {
                // Component existed already (RequireComponent) but its inventory was
                // not created: initialize it explicitly with the backpack size.
                try
                {
                    storage.Initialize(15, 1000);
                    inv = storage.Inventory;
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"{npc.CitizenName}: backpack storage init failed: {ex.Message}");
                }
            }
            if (inv == null)
            {
                PluginLog.Warn($"{npc.CitizenName}: backpack storage unavailable, starter kit skipped");
                return;
            }

            int added = 0;
            foreach (var entry in BackpackKit)
                added += EnsureItems(inv, entry.type, entry.qty, creator);
            PluginLog.Info($"{npc.CitizenName}: backpack starter kit ensured ({added} item added)");
        }

        // ------------------------------------------------------------- camp

        private static StarterCampObject EnsureCamp(CitizenNpc npc, User creator)
        {
            // Already has a camp nearby? Keep it (idempotent across restarts).
            var existing = FindNearby<StarterCampObject>(npc.Position, CampRadius);
            if (existing != null)
            {
                PluginLog.Info($"{npc.CitizenName}: camp already placed nearby");
                return existing;
            }

            return TryPlace(npc, typeof(StarterCampObject), creator, npc.Position, 0f, "StarterCamp", true) as StarterCampObject;
        }

        private static void EnsureCampKit(CitizenNpc npc, StarterCampObject camp, User creator)
        {
            // Camp storage first (the tent's own inventory when present), else keep
            // the camp kit in the NPC backpack.
            var targetInv = camp?.GetComponent<StorageComponent>()?.Inventory;
            if (targetInv != null)
            {
                int added = 0;
                foreach (var entry in CampKit)
                    added += EnsureItems(targetInv, entry.type, entry.qty, creator);
                PluginLog.Info($"{npc.CitizenName}: camp storage kit ensured ({added} item added)");
            }
            else
            {
                var inv = npc.GetComponent<PublicStorageComponent>()?.Inventory;
                if (inv != null)
                {
                    foreach (var entry in CampKit)
                        EnsureItems(inv, entry.type, entry.qty, creator);
                    PluginLog.Info($"{npc.CitizenName}: camp has no own storage, camp kit kept in backpack");
                }
            }
        }

        // ------------------------------------------------------------- stations

        private static void EnsureStation(CitizenNpc npc, Type objectType, User creator, Vector3 campPos, float offset, string label)
        {
            if (FindNearby(objectType, campPos, StationRadius) != null)
            {
                PluginLog.Info($"{npc.CitizenName}: {label} already placed nearby");
                return;
            }
            TryPlace(npc, objectType, creator, campPos, offset, label, false);
        }

        /// <summary>
        /// Stockpile supplies for campfire cooking: fuel (Birch Log) so the campfire
        /// can be lit, plus ready campfire food (Charred Meat) so the citizen always
        /// has campfire food at hand. Idempotent - only fills missing quantities.
        /// </summary>
        private static void EnsureCampfireSupplies(CitizenNpc npc, Vector3 campPos, User creator)
        {
            try
            {
                var stockpile = FindNearby(typeof(StockpileObject), campPos, StationRadius) as StockpileObject;
                if (stockpile == null) return;
                var inv = stockpile.GetComponent<StorageComponent>()?.Inventory;
                if (inv == null) return;

                int added = 0;
                added += EnsureItems(inv, typeof(BirchLogItem), 2, creator);
                added += EnsureItems(inv, typeof(CharredMeatItem), 2, creator);
                if (added > 0)
                    PluginLog.Info($"{npc.CitizenName}: campfire stockpile supplied ({added} item added)");
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"{npc.CitizenName}: EnsureCampfireSupplies failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------- helpers

        /// <summary>
        /// Adds items only when the inventory does not already hold the requested
        /// quantity, so repeated calls never duplicate the kit.
        /// </summary>
        private static int EnsureItems(Inventory inv, Type itemType, int qty, User creator)
        {
            try
            {
                if (inv == null) return 0;
                var current = inv.TypeToCount.TryGetValue(itemType, out var c) ? c : 0;
                if (current >= qty) return 0;
                var item = Item.Get(itemType);
                if (item == null)
                {
                    PluginLog.Warn($"SurvivalKit: item type {itemType.Name} not registered, skipped");
                    return 0;
                }
                return inv.AddItems(item, qty - current, creator) ? 1 : 0;
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"SurvivalKit: EnsureItems({itemType.Name}) failed: {ex.Message}");
                return 0;
            }
        }

        private static T FindNearby<T>(Vector3 pos, float radius) where T : WorldObject
            => FindNearby(typeof(T), pos, radius) as T;

        private static WorldObject FindNearby(Type objectType, Vector3 pos, float radius)
        {
            foreach (var wo in PluginManager.GetPlugin<WorldObjectManager>().All)
            {
                if (wo == null || wo.IsDestroyed) continue;
                if (wo.GetType() != objectType) continue;
                if (Vector3.Distance(wo.Position, pos) <= radius) return wo;
            }
            return null;
        }

        /// <summary>
        /// Places a world object near the NPC (camp on the NPC, stations offset from
        /// the camp). Every candidate is first snapped to the terrain surface so
        /// placed objects never float in the air (the NPC/base Y can be far above
        /// the ground). validatePlacement=false mirrors the NPC spawn path; up to 3
        /// candidate offsets are tried before giving up.
        /// </summary>
        private static WorldObject TryPlace(CitizenNpc npc, Type objectType, User creator, Vector3 center, float offset, string label, bool allowCenter = false)
        {
            var candidates = allowCenter ? CandidateOffsets(offset) : StationOffsets(offset);
            foreach (var candidate in candidates)
            {
                try
                {
                    Vector3 placePos = GroundSampler.SnapToGround(center + candidate);
                    WorldObject created = null;
                    WorldObjectManager.ForceAdd(
                        objectType,
                        creator,
                        placePos,
                        Quaternion.Identity,
                        false,
                        null,
                        wo => created = wo,
                        null);
                    if (created != null)
                    {
                        PluginLog.Info($"{npc.CitizenName}: placed {label} at {created.Position}");
                        // v0.8: register the placed object so despawnall can clear
                        // ALL NPC-built facilities at any distance and fixfloating
                        // can find/correct floating ones - without touching
                        // player-built objects.
                        try
                        {
                            var db = EcoSimCitizens.Instance?.Database;
                            if (db != null)
                            {
                                db.RegisterPlacedObject(new PlacedObject
                                {
                                    WoId = created.ID.ToString(),
                                    TypeName = objectType.Name,
                                    OwnerNpcName = npc.CitizenName,
                                    IsStructure = false,
                                    X = created.Position.X,
                                    Y = created.Position.Y,
                                    Z = created.Position.Z,
                                    PlacedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            PluginLog.Warn($"SurvivalKit: register placed object failed: {ex.Message}");
                        }
                        return created;
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"{npc.CitizenName}: {label} placement at {center + candidate} failed: {ex.Message}");
                }
            }
            PluginLog.Warn($"{npc.CitizenName}: {label} placement failed after {candidates.Count} attempts");
            return null;
        }

        /// <summary>
        /// Candidate offsets for the camp itself: the exact centre (the tent goes
        /// under the NPC's feet) plus two perpendicular 45° spots as fallbacks.
        /// </summary>
        private static List<Vector3> CandidateOffsets(float offset)
        {
            var list = new List<Vector3> { new Vector3(0, 0, 0) };
            if (offset > 0.01f)
            {
                list.Add(new Vector3(offset, 0, offset));
                list.Add(new Vector3(-offset, 0, offset));
            }
            return list;
        }

        /// <summary>
        /// Candidate offsets for workstations: NEVER the centre - the camp interior
        /// must stay free. Eight directions around the camp at exactly `offset`
        /// metres so the workbench/tool bench sits OUTSIDE the tent, adjacent to it.
        /// </summary>
        private static List<Vector3> StationOffsets(float offset)
        {
            var list = new List<Vector3>();
            if (offset <= 0.01f) return list;
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4.0;
                list.Add(new Vector3(
                    (float)(Math.Cos(angle) * offset),
                    0,
                    (float)(Math.Sin(angle) * offset)));
            }
            return list;
        }

        // ------------------------------------------------------------- camp storage sync

        private static readonly Type[] KitToolTypes =
        {
            typeof(ClaimPaperItem), typeof(ClaimToolItem), typeof(StoneAxeItem),
            typeof(WoodenShovelItem), typeof(StoneHammerItem), typeof(StonePickaxeItem),
        };

        /// <summary>
        /// Makes the NPC genuinely "use" the small camp storage box: whenever the
        /// citizen is near its own tent it deposits produced goods / raw materials
        /// into the camp chest (respecting capacity) and re-arms any missing kit
        /// tool from the chest back into the backpack. Pure server-side inventory
        /// operations - no player auth involved, so NPCs can always use the box.
        /// Returns (deposited, takenBack) item counts.
        /// </summary>
        public static (int deposited, int takenBack) SyncWithCampStorage(CitizenNpc npc, User creator)
        {
            try
            {
                if (npc == null) return (0, 0);
                var camp = FindNearby<StarterCampObject>(npc.Position, CampRadius);
                if (camp == null) return (0, 0);
                var chest = camp.GetComponent<StorageComponent>()?.Inventory;
                var backpack = npc.GetComponent<PublicStorageComponent>()?.Inventory;
                if (chest == null || backpack == null) return (0, 0);

                int deposited = 0;
                int taken = 0;

                // --- deposit produced goods / raw materials (never kit tools, claim
                // paper, camp item or food - food is reserved for personal use) ---
                foreach (var stack in new List<ItemStack>(backpack.Stacks))
                {
                    if (deposited >= 4) break; // a handful per sync
                    if (stack == null || stack.Item == null) continue;
                    int qty = stack.Quantity;
                    if (qty <= 0) continue;
                    Type t = stack.Item.Type;
                    if (t == null) continue;
                    if (IsKitOrFood(t)) continue;
                    int move = Math.Min(qty, 2);
                    int have = backpack.TypeToCount.TryGetValue(t, out var haveV) ? haveV : 0;
                    if (have <= 0) continue;
                    backpack.RemoveItems(t, Math.Min(move, have), creator);
                    if (!chest.AddItems(Item.Get(t), move, creator))
                    {
                        // chest full / refused - put it straight back
                        try { backpack.AddItems(Item.Get(t), move, creator); } catch { }
                        continue;
                    }
                    deposited += move;
                }

                // --- take back a missing kit tool from the chest (re-arm) ---
                foreach (var tool in KitToolTypes)
                {
                    if (taken >= 2) break;
                    if (tool == typeof(ClaimPaperItem)) continue; // paper stays in the chest
                    if (backpack.TypeToCount.TryGetValue(tool, out var have) && have > 0) continue;
                    if (!chest.TypeToCount.TryGetValue(tool, out var chestHave) || chestHave <= 0) continue;
                    chest.RemoveItems(tool, 1, creator);
                    if (backpack.AddItems(Item.Get(tool), 1, creator))
                        taken++;
                    else
                        chest.AddItems(Item.Get(tool), 1, creator); // rollback
                }

                if (deposited > 0 || taken > 0)
                    PluginLog.Info($"{npc.CitizenName}: camp storage sync deposited {deposited}, re-armed {taken}");
                return (deposited, taken);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"SurvivalKit.SyncWithCampStorage failed for {npc?.CitizenName}: {ex.Message}");
                return (0, 0);
            }
        }

        private static bool IsKitOrFood(Type t)
        {
            foreach (var kit in KitToolTypes)
                if (kit == t) return true;
            if (t == typeof(StarterCampItem)) return true;
            string n = t.Name.ToLowerInvariant();
            return SimulationLogic.IsFoodItem(n);
        }
    }
}
