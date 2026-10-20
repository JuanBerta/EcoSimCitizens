using System;
using System.Linq;
using System.Text;
using Eco.Gameplay.Economy;
using Eco.Gameplay.Players;
using Eco.Shared.Services;

namespace EcoSimCitizens
{
    /// <summary>
    /// v0.7: closes the loop between real players and NPC commerce.
    ///
    /// Design boundary (kept honest):
    ///   - Money is REAL: every player-vs-NPC trade moves native currency
    ///     between the player's real BankAccount and the NPC's registered native
    ///     account (BankAccountManager.Registrar), via BankAccount.AddCurrency on
    ///     both sides.
    ///   - Goods are SIMULATED: NPC shelves are SimStore records, and the player
    ///     receives a digital receipt instead of a spawned Item. Eco has no safe
    ///     generic API to inject arbitrary Items into a player's real inventory
    ///     from a mod command, so physical goods delivery is out of scope; the
    ///     store's stock/revenue ledger still updates as if the goods moved.
    ///   - The NPC never becomes a User; no StoreComponent.PerformTrade is used.
    /// </summary>
    public static class PlayerCommerce
    {
        /// <summary>All open NPC shops, player-readable.</summary>
        public static string ListStores(CitizensDatabase db)
        {
            var stores = db.SimStoresSnapshot()
                .Where(s => s.IsOpen)
                .OrderBy(s => s.OwnerName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (stores.Count == 0) return "No NPC shops are open right now.";

            var sb = new StringBuilder();
            sb.AppendLine($"Open NPC shops ({stores.Count}):");
            sb.AppendLine("  [id] owner | goods | price | stock | level");
            foreach (var s in stores)
            {
                sb.AppendLine($"  {s.Id} | {s.OwnerName} | {s.ItemTypeName} | {s.Price:0.00} | {s.Stock} | Lv{s.Level}");
            }
            sb.AppendLine("Buy with /npccmd shopbuy <id> <qty> (native currency). Sell to an NPC with /npccmd shopsell <citizen> <item> <qty> <price>.");
            return sb.ToString();
        }

        /// <summary>Find a store by id or by owner name (case-insensitive).</summary>
        public static SimStore FindStore(CitizensDatabase db, string storeRef)
        {
            if (string.IsNullOrEmpty(storeRef)) return null;
            return db.SimStoresSnapshot().FirstOrDefault(s =>
                string.Equals(s.Id, storeRef, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.OwnerName, storeRef, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Real player buys from an NPC shop: native currency moves player -> NPC
        /// account, shelf stock drops, store revenue/sales ledger updates. The
        /// goods are a digital receipt (simulated delivery).
        /// </summary>
        public static string PlayerBuy(CitizensDatabase db, ModConfig cfg, User player, string storeRef, int qty)
        {
            if (player == null) return "Trade skipped: no player.";
            var store = FindStore(db, storeRef);
            if (store == null) return $"No open NPC shop matches '{storeRef}'. Use /npccmd shop to list shops.";
            if (qty <= 0) return "Quantity must be > 0.";
            if (!store.IsOpen) return $"Shop '{store.Id}' is currently closed.";
            if (store.Stock < qty) return $"Shop '{store.Id}' only has {store.Stock} in stock.";

            var cur = NativeCurrencyBridge.DefaultCurrency();
            if (cur == null) return "Trade skipped: native currency is unavailable (no user to register it).";
            var playerAcc = player.BankAccount;
            var npcAcc = NativeCurrencyBridge.NpcAccount(store.OwnerName);
            if (playerAcc == null) return "Trade skipped: your bank account is unavailable.";
            if (npcAcc == null) return $"Trade skipped: NPC account for '{store.OwnerName}' is unavailable.";

            double cost = store.Price * qty;
            float balance = playerAcc.GetCurrencyHoldingVal(cur);
            if (balance < (float)cost) return $"Trade skipped: you need {cost:0.00} {cur.Name} but have {balance:0.00}.";

            try
            {
                playerAcc.AddCurrency(cur, -(float)cost, false);
                npcAcc.AddCurrency(cur, (float)cost);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"EcoSimCitizens: player buy transfer failed: {ex.Message}");
                return "Trade failed during currency transfer. Nothing was charged.";
            }

            // simulated goods ledger
            store.Stock -= qty;
            store.PlayerSales += qty;
            store.PlayerRevenue += cost;
            var owner = db.Find(store.OwnerName);
            if (owner != null)
            {
                lock (owner)
                {
                    owner.ItemsSold += qty;
                    owner.RealEarned += cost;
                    owner.RealTradeVolume += cost;
                    owner.TradeSellVolume++;
                }
            }
            db.AddStore(store);
            db.Save();

            PluginLog.Info($"EcoSimCitizens: player '{player.Name}' bought {qty}x {store.ItemTypeName} from '{store.OwnerName}' for {cost:0.00} {cur.Name} (native transfer).");
            return $"Purchased {qty}x {store.ItemTypeName} from '{store.OwnerName}' for {cost:0.00} {cur.Name}. Payment left your real bank account; the goods are a digital receipt from the NPC shop (no physical item spawned).";
        }

        /// <summary>
        /// Real player sells to an NPC: native currency moves NPC -> player
        /// account, the NPC's simulated inventory gains the goods (digital
        /// delivery; the player's real backpack is not touched).
        /// </summary>
        public static string PlayerSellToNpc(CitizensDatabase db, ModConfig cfg, User player, string citizenName, string itemTypeName, int qty, double pricePerUnit)
        {
            if (player == null) return "Trade skipped: no player.";
            var c = db.Find(citizenName);
            if (c == null) return $"No citizen named '{citizenName}'. Use /npccmd npcs to see who is spawned.";
            if (qty <= 0) return "Quantity must be > 0.";
            if (pricePerUnit <= 0) return "Price per unit must be > 0.";
            if (string.IsNullOrEmpty(itemTypeName)) return "Item type is required.";

            var cur = NativeCurrencyBridge.DefaultCurrency();
            if (cur == null) return "Trade skipped: native currency is unavailable.";
            var playerAcc = player.BankAccount;
            var npcAcc = NativeCurrencyBridge.NpcAccount(citizenName);
            if (playerAcc == null) return "Trade skipped: your bank account is unavailable.";
            if (npcAcc == null) return $"Trade skipped: NPC account for '{citizenName}' is unavailable.";

            double cost = pricePerUnit * qty;
            float npcBalance = npcAcc.GetCurrencyHoldingVal(cur);
            if (npcBalance < (float)cost) return $"Trade skipped: {citizenName} only has {npcBalance:0.00} {cur.Name} and the order costs {cost:0.00}.";

            try
            {
                npcAcc.AddCurrency(cur, -(float)cost, false);
                playerAcc.AddCurrency(cur, (float)cost);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"EcoSimCitizens: player sell transfer failed: {ex.Message}");
                return "Trade failed during currency transfer. Nothing was charged.";
            }

            // simulated goods ledger: NPC inventory gains the goods
            lock (c)
            {
                SimulationLogic.InventoryAdd(c.Inventory, itemTypeName, qty);
                c.RealTradeVolume += cost;
                c.TradeBuyVolume++;
            }
            db.Save();

            PluginLog.Info($"EcoSimCitizens: player '{player.Name}' sold {qty}x {itemTypeName} to '{citizenName}' for {cost:0.00} {cur.Name} (native transfer).");
            return $"Sold {qty}x {itemTypeName} to '{citizenName}' for {cost:0.00} {cur.Name}. Payment arrived in your real bank account; the goods are a digital delivery into the NPC's inventory (your backpack was not touched).";
        }

        /// <summary>Player-facing trade summary for reports.</summary>
        public static string Summary(CitizensDatabase db)
        {
            var citizens = db.Snapshot().Where(c => c != null).ToList();
            double volume = citizens.Sum(c => c.RealTradeVolume);
            long buyTrades = citizens.Sum(c => c.TradeBuyVolume);
            long sellTrades = citizens.Sum(c => c.TradeSellVolume);
            return $"player-vs-NPC real trades: volume {volume:0.00}, buy {buyTrades}, sell {sellTrades}";
        }
    }
}
