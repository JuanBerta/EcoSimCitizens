# EcoSim Citizens

An autonomous citizen simulation mod for **Eco Global Survival**. It adds a layer of simulated residents to your world who work, earn, shop, live in deeds, and react to your player-driven economy and civics — a living backdrop that makes server economies feel populated even between player sessions.

> Built against Eco.ReferenceAssemblies `0.14.2-beta-release-1099` (net10.0). Referenced API surface is verified against the official assemblies; see [docs.play.eco](https://docs.play.eco/).

## Why

Eco's beauty is that everything is player-made. But when nobody is online, the world goes quiet. EcoSim Citizens introduces lightweight **simulated citizens** that:

- **Work** — productivity = profession × trait × health (calories).
- **Earn & spend** — each citizen has a private ledger and buys food from *real* player stores at *real* market prices.
- **Live somewhere** — citizens can be assigned a real in-game **deed**; comfort scales with plot count and settlement support.
- **Feel the civic pulse** — deeds, settlements and active elections feed a civic health score.
- **Persist** — state is saved as JSON next to the mod DLL and survives restarts.

The mod is **observational only**: it never claims land, spawns items, moves players, or mutates stores/contracts. Everything it reads is public read-only Eco state. The simulation is a closed loop *driven by* the live economy, so a server with a booming market and settled towns produces happier citizens than an empty frontier.

## Features (v0.1.0)

- 7 professions: Laborer, Farmer, Miner, Crafter, Merchant, Builder, Scholar.
- 6 traits: Diligent, Lazy, Curious, Moody, Gregarious, Balanced (auto-assigned, affects output & happiness).
- Three tracked needs: **Calories**, **Comfort**, **Social** (0–100, decay each tick).
- Live **market index**: store count, offer count, priced offers, food availability, avg/min price, price trend %.
- Live **civic snapshot**: settlement count, active elections, deed count, real online population, civic score.
- Daily report broadcast to online players.
- JSON persistence for citizens and config.
- In-game chat commands (see below).

## Installation

1. Download/build the mod: `dotnet build -c Release`.
2. Copy `EcoSimCitizens.dll` into your Eco server's `Mods` folder.
3. Restart the server. The plugin appears in the plugins list and creates `EcoSimCitizens/config.json` + `EcoSimCitizens/citizens.json` next to the DLL.
4. Spawn your first citizens (admin):

```
/ecosim spawn Ada Farmer
/ecosim spawn Bo Builder
```

5. Watch the daily report roll in, or check status any time:

```
/ecosim
```

## Commands

| Command | Access | Description |
| --- | --- | --- |
| `/ecosim` | User | Status popup (population, happiness, market, civic score) |
| `/ecosim report` | User | Full daily report in chat |
| `/ecosim list` | User | List all citizens with profession, trait, happiness, ledger, home |
| `/ecosim spawn <name> <profession>` | Admin | Spawn a citizen |
| `/ecosim remove <name>` | Admin | Remove a citizen |
| `/ecosim give <name> <amount>` | Admin | Credit a citizen's ledger |
| `/ecosim home <name> <deed>` | Admin | Assign a deed (by deed name) as the citizen's home |
| `/ecosim save` | Admin | Force-save state & config |
| `/ecosim config` | Admin | Show current config values |

## Configuration

Edit `EcoSimCitizens/config.json` (created on first run, or manually):

```json
{
  "TickIntervalMinutes": 30.0,
  "CalorieDecayPerTick": 4.0,
  "ComfortDecayPerTick": 2.5,
  "SocialDecayPerTick": 1.5,
  "CalorieGainFromFood": 12.0,
  "FoodSpendFactor": 0.6,
  "WagePerTick": 20.0,
  "StartingBalance": 100.0,
  "PriceTrendSensitivity": 1.0,
  "MaxCitizens": 50,
  "BroadcastDailyReport": true
}
```

## Project structure

```
EcoSimCitizens/
├── EcoSimCitizens.csproj       net10.0 + Eco.ReferenceAssemblies 0.14.2
├── ModConfig.cs                runtime config (JSON)
├── CitizenModel.cs             Citizen + profession/trait tables
├── CitizensDatabase.cs         thread-safe store + JSON persistence
├── MarketScanner.cs            reads real stores/TradeOffers (read-only)
├── CivicObserver.cs            deeds/settlements/elections snapshot
├── HousingEvaluator.cs         deed -> comfort mapping
├── SimulationCore.cs           per-tick simulation + report builder
├── EcoSimCommands.cs           /ecosim chat commands
├── EcoSimCitizensPlugin.cs     SimplePlugin<EcoSimCitizens> + IThreadedPlugin
└── PluginLog.cs                logging shim
```

## Roadmap

- [ ] Citizen names generated from culture pools
- [ ] Simulated contracts & work orders between citizens and player stores
- [ ] Population growth rules (immigration driven by civic score & housing)
- [ ] Citizen "tourism" events (visitors boost social, spend at stores)
- [ ] Law/Government integration (law activity affects civic score)
- [ ] Per-citizen skill progression and specialization over time
- [ ] Optional lightweight UI panel (ModKit) for admins

## License

MIT — see [LICENSE](LICENSE).

*Developed with AI assistance (Marvis).*
